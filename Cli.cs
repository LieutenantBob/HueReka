using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace HueReka
{
    static class Cli
    {
        const string Help = @"HueReka - individual light control

  huereka list                 List light IDs, names and current state
  huereka on <id>              Turn one light on
  huereka off <id>             Turn one light off
  huereka brightness <id> <1-100>
                              Set brightness and turn the light on
  huereka color <id> <RRGGBB>  Six uppercase hex digits, without #
                              Set color and brightness; 000000 turns off
  huereka warm <id>            Warm white and turn on (compatible bulbs)
  huereka cool <id>            Cool white and turn on (compatible bulbs)
  huereka palettes             List ambience palettes, built-in and your own
  huereka ambience <palette> [id ...] [--together] [--speed <10-600>] [--brightness <1-100>]
                              Slowly fade lights through a palette until Ctrl+C.
                              No IDs = all lights. Defaults: drift, 120 s, 70%
  huereka pair <bridge-ip>     Pair, then press the bridge link button when asked
  huereka help

Uses the same saved pairing as the desktop app, for this Windows user.
Exit codes: 0 = success, 1 = connection/bridge error, 2 = invalid command.
";

        static volatile bool ambienceRunning;

        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (args.Length == 1 && args[0] == "--self-test")
            {
                try { SelfTest().GetAwaiter().GetResult(); Console.WriteLine("CLI tests passed."); return 0; }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            }
            using (var stop = new CancellationTokenSource())
            {
                // Ctrl+C ends an ambience gracefully; for every other command it keeps its normal meaning.
                Console.CancelKeyPress += (sender, e) => { if (!ambienceRunning) return; e.Cancel = true; stop.Cancel(); };
                return Execute(args, Settings.Load, settings => new Bridge(settings), settings => settings.Save(), Console.Out, Console.Error, stop.Token).GetAwaiter().GetResult();
            }
        }

        static void Validate(string[] args)
        {
            if (args.Length == 0) return;
            string command = args[0].ToLowerInvariant();
            if (command == "ambience") { ParseAmbience(args); return; }
            int count;
            switch (command)
            {
                case "help": case "--help": case "-h": case "list": case "palettes": count = 1; break;
                case "pair": case "on": case "off": case "warm": case "cool": count = 2; break;
                case "brightness": case "color": count = 3; break;
                default: throw new ArgumentException("Unknown command. Run huereka help.");
            }
            if (args.Length != count) throw new ArgumentException("Wrong number of arguments. Run huereka help.");
            if (count > 1 && command != "pair")
            {
                CheckId(args[1]);
            }
            if (command == "brightness")
            {
                int percent;
                if (!Int32.TryParse(args[2], out percent) || percent < 1 || percent > 100)
                    throw new ArgumentException("Brightness must be a whole number from 1 to 100.");
            }
            if (command == "color" && !Regex.IsMatch(args[2], @"\A[0-9A-F]{6}\z"))
                throw new ArgumentException("Color must be exactly six uppercase hexadecimal digits (RRGGBB), for example FF8800, without #.");
            if (command == "pair")
                try { Bridge.ValidateAddress(args[1]); } catch (InvalidOperationException error) { throw new ArgumentException(error.Message); }
        }

        static Dictionary<string, object> ColorState(string hex)
        {
            int rgb = Convert.ToInt32(hex, 16);
            if (rgb == 0) return new Dictionary<string, object> { { "on", false } };
            var hsv = HueColor.FromRgb((rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            return new Dictionary<string, object> { { "on", true }, { "hue", hsv.Hue }, { "sat", hsv.Sat }, { "bri", Math.Max(1, hsv.Bri) } };
        }

        sealed class AmbienceRequest
        {
            public string Palette;
            public List<string> Ids = new List<string>();
            public bool Together;
            public int Speed = AmbienceSpeed.DefaultSeconds, Brightness = AmbienceSpeed.DefaultBrightness;
        }

        static AmbienceRequest ParseAmbience(string[] args)
        {
            if (args.Length < 2 || args[1].StartsWith("-")) throw new ArgumentException("Name a palette, for example huereka ambience Sunset. Run huereka palettes to see them all.");
            var request = new AmbienceRequest { Palette = args[1] };
            for (int i = 2; i < args.Length; i++)
            {
                string arg = args[i];
                if (arg == "--together") request.Together = true;
                else if (arg == "--speed") request.Speed = Number(args, ++i, AmbienceSpeed.MinSeconds, AmbienceSpeed.MaxSeconds, "Speed must be a whole number of seconds from 10 to 600.");
                else if (arg == "--brightness") request.Brightness = Number(args, ++i, 1, 100, "Brightness must be a whole number from 1 to 100.");
                else if (arg.StartsWith("-")) throw new ArgumentException("Unknown option " + arg + ". Run huereka help.");
                else { CheckId(arg); if (!request.Ids.Contains(arg)) request.Ids.Add(arg); }
            }
            return request;
        }

        static int Number(string[] args, int index, int min, int max, string message)
        {
            int value;
            if (index >= args.Length || !Int32.TryParse(args[index], out value) || value < min || value > max || args[index] != value.ToString()) throw new ArgumentException(message);
            return value;
        }

        static void CheckId(string text)
        {
            int id;
            if (!Int32.TryParse(text, out id) || id <= 0 || text != id.ToString()) throw new ArgumentException("Use a numeric light ID from huereka list.");
        }

        static string LightCount(int count) { return count == 1 ? "1 light" : count + " lights"; }

        static async Task<int> RunAmbience(Bridge bridge, List<Light> lights, Palette palette, AmbienceRequest request, TextWriter output, TextWriter errorOutput, CancellationToken stop)
        {
            var chosen = new List<Light>();
            if (request.Ids.Count == 0) chosen.AddRange(lights.Where(AmbiencePlanner.CanJoin));
            foreach (string id in request.Ids)
            {
                var light = lights.SingleOrDefault(candidate => candidate.Id == id);
                if (light == null) throw new ArgumentException("Light ID " + id + " does not exist. Run huereka list.");
                if (!AmbiencePlanner.CanJoin(light)) throw new ArgumentException("Light " + id + " (" + light.Name + ") can't change brightness or color, so it can't join an ambience.");
                chosen.Add(light);
            }
            if (chosen.Count == 0) throw new InvalidOperationException("None of your lights can change brightness or color.");
            foreach (var light in chosen.Where(light => !light.Reachable)) errorOutput.WriteLine("Note: " + light.Name + " (" + light.Id + ") is unreachable right now. It joins in once it answers.");
            var options = new AmbienceOptions { Palette = palette, Mode = request.Together ? AmbienceMode.Together : AmbienceMode.Drift, SpeedSeconds = request.Speed, BrightnessPercent = request.Brightness };
            var runner = new AmbienceRunner(bridge, options, chosen);
            output.WriteLine("Ambience " + palette.Name + " on " + LightCount(chosen.Count) + " (" + (request.Together ? "together" : "drift") + "), " + AmbienceSpeed.Describe(request.Speed) + ". Press Ctrl+C to stop.");
            ambienceRunning = true;
            try { await runner.Run(new Reporter(message => errorOutput.WriteLine(message)), stop); }
            finally { ambienceRunning = false; }
            output.WriteLine("Ambience stopped.");
            return 0;
        }

        static async Task<int> Execute(string[] args, Func<Settings> load, Func<Settings, Bridge> create, Action<Settings> save, TextWriter output, TextWriter errorOutput, CancellationToken stop)
        {
            try
            {
                Validate(args);
                string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
                if (command == "help" || command == "--help" || command == "-h") { output.Write(Help); return 0; }
                if (command == "pair")
                {
                    var settings = new Settings { Address = Bridge.ValidateAddress(args[1]) };
                    try { settings.KeepPreferencesFrom(load()); }
                    catch (Exception) { } // a broken old file must never block pairing
                    var pairingBridge = create(settings);
                    output.WriteLine("Press the round link button on your Hue Bridge. Waiting up to 90 seconds...");
                    await pairingBridge.PairWhenReady(TimeSpan.FromSeconds(90), null, CancellationToken.None);
                    await pairingBridge.Lights();
                    save(settings);
                    output.WriteLine("Paired with " + settings.Address + ". Run huereka list.");
                    return 0;
                }
                if (command == "palettes")
                {
                    output.WriteLine("NAME\tTYPE\tCOLORS");
                    foreach (var palette in Palettes.All(load()))
                        output.WriteLine(palette.Name + "\t" + (Palettes.IsPreset(palette.Name) ? "built-in" : "custom") + "\t" + String.Join(", ", palette.Entries.Select(entry => entry.Label())));
                    return 0;
                }
                var connection = load();
                AmbienceRequest ambience = command == "ambience" ? ParseAmbience(args) : null;
                Palette chosenPalette = ambience == null ? null : Palettes.Find(connection, ambience.Palette);
                if (ambience != null && chosenPalette == null) throw new ArgumentException("There's no palette called " + ambience.Palette + ". Run huereka palettes.");
                if (String.IsNullOrEmpty(connection.Key)) throw new InvalidOperationException("No saved pairing. Pair in the desktop app or run huereka pair <bridge-ip> after pressing the bridge button.");
                var bridge = create(connection);
                var lights = await bridge.Lights();
                if (ambience != null) return await RunAmbience(bridge, lights, chosenPalette, ambience, output, errorOutput, stop);
                if (command == "list")
                {
                    output.WriteLine("ID\tPOWER\tBRIGHTNESS\tREACHABLE\tNAME");
                    foreach (var light in lights)
                    {
                        string brightness = light.State.ContainsKey("bri") ? Math.Round(Convert.ToInt32(light.State["bri"]) * 100.0 / 254) + "%" : "-";
                        output.WriteLine(light.Id + "\t" + (Convert.ToBoolean(light.State["on"]) ? "on" : "off") + "\t" + brightness + "\t" + (light.Reachable ? "yes" : "no") + "\t" + light.Name);
                    }
                    return 0;
                }
                var selected = lights.SingleOrDefault(light => light.Id == args[1]);
                if (selected == null) throw new ArgumentException("Light ID " + args[1] + " does not exist. Run huereka list.");
                if (!selected.Reachable) throw new InvalidOperationException("Light " + selected.Id + " is unreachable. Check its power and bridge connection.");
                object state;
                if (command == "brightness")
                {
                    if (!selected.State.ContainsKey("bri")) throw new ArgumentException("This light does not support brightness.");
                    state = new { on = true, bri = HueColor.Brightness(Int32.Parse(args[2])) };
                }
                else if (command == "color")
                {
                    if (!selected.State.ContainsKey("hue") || !selected.State.ContainsKey("sat") || !selected.State.ContainsKey("bri"))
                        throw new ArgumentException("This light does not support RGB color.");
                    state = ColorState(args[2]);
                }
                else if (command == "warm" || command == "cool")
                {
                    if (!selected.State.ContainsKey("ct")) throw new ArgumentException("This light does not support white temperature.");
                    state = new { on = true, ct = command == "warm" ? selected.MaxCt : selected.MinCt };
                }
                else state = new { on = command == "on" };
                await bridge.Set(selected.Id, state);
                output.WriteLine("OK: " + selected.Id + " (" + selected.Name + ") " + command + (command == "brightness" ? " " + args[2] + "%" : command == "color" ? " " + args[2] : ""));
                return 0;
            }
            catch (ArgumentException error) { errorOutput.WriteLine("Error: " + error.Message); return 2; }
            catch (WebException) { errorOutput.WriteLine("Error: Cannot reach or trust the bridge. Check the network and IP address. To accept a changed certificate, press the bridge button and run huereka pair <bridge-ip>."); return 1; }
            catch (Exception error) { errorOutput.WriteLine("Error: " + error.Message.Replace("click Pair again", "run huereka pair <bridge-ip> again")); return 1; }
        }

        static async Task SelfTest()
        {
            var calls = new List<string>();
            bool rejectWrite = false, paired = false;
            int unpressedPairAttempts = 2;
            Bridge.PairPollInterval = TimeSpan.FromMilliseconds(5);
            Func<Settings, Bridge> create = settings => new Bridge(settings, (method, path, body) => {
                calls.Add(method + " " + path + " " + body);
                if (method == "POST" && unpressedPairAttempts-- > 0) return "[{\"error\":{\"type\":101,\"description\":\"link button not pressed\"}}]";
                if (method == "POST") return "[{\"success\":{\"username\":\"test-key\"}}]";
                if (method == "GET") return "{\"1\":{\"name\":\"Desk\",\"state\":{\"on\":true,\"bri\":127,\"ct\":300,\"hue\":0,\"sat\":254,\"reachable\":true}},\"2\":{\"name\":\"Offline\",\"state\":{\"on\":false,\"reachable\":false}},\"3\":{\"name\":\"Plug\",\"state\":{\"on\":false,\"reachable\":true}}}";
                return rejectWrite ? "[{\"error\":{\"type\":7,\"description\":\"invalid value\"}}]" : "[{\"success\":{}}]";
            });
            var output = new StringWriter(); var error = new StringWriter();
            Func<string[], Task<int>> run = args => Execute(args, () => new Settings { Address = "192.168.1.2", Key = "test-key" }, create, settings => paired = true, output, error, CancellationToken.None);
            Check(await run(new[] { "help" }) == 0 && calls.Count == 0, "Help must work offline");
            Check(await run(new[] { "brightness", "1", "101" }) == 2 && calls.Count == 0, "Reject invalid values before connecting");
            Check(await run(new[] { "off", "1", "extra" }) == 2 && calls.Count == 0, "Reject extra arguments");
            foreach (string invalid in new[] { "ff8800", "#FF8800", "FFF", "1234567", "GG0000", "FFFFFF\n", "" })
                Check(await run(new[] { "color", "1", invalid }) == 2 && calls.Count == 0, "Reject invalid hex before connecting");
            Check(await run(new[] { "color", "1" }) == 2 && calls.Count == 0, "Require color argument");
            Check(await run(new[] { "list" }) == 0 && output.ToString().Contains("Desk"), "List lights");
            Check(await run(new[] { "off", "1" }) == 0 && calls.Last().Contains("/lights/1/state {\"on\":false}"), "Individual off");
            Check(await run(new[] { "on", "1" }) == 0 && calls.Last().Contains("{\"on\":true}"), "Individual on");
            Check(await run(new[] { "brightness", "1", "50" }) == 0 && calls.Last().Contains("\"bri\":127"), "Brightness command");
            Check(await run(new[] { "warm", "1" }) == 0 && calls.Last().Contains("\"ct\":500"), "Warm white");
            Check(await run(new[] { "cool", "1" }) == 0 && calls.Last().Contains("\"ct\":153"), "Cool white");
            Check(await run(new[] { "color", "1", "FF0000" }) == 0 && calls.Last().Contains("/lights/1/state") && calls.Last().Contains("\"hue\":0") && calls.Last().Contains("\"sat\":254") && calls.Last().Contains("\"bri\":254"), "Red command payload");
            Check((int)ColorState("00FF00")["hue"] == 21845 && (int)ColorState("0000FF")["hue"] == 43690, "Green and blue conversion");
            Check((int)ColorState("808080")["sat"] == 0 && (int)ColorState("808080")["bri"] == 127, "Gray has no saturation");
            Check((int)ColorState("FFFFFF")["sat"] == 0 && (int)ColorState("FFFFFF")["bri"] == 254, "White conversion");
            Check((int)ColorState("804040")["sat"] == 127, "Use HSV saturation rather than HSL");
            Check((int)ColorState("000001")["bri"] == 1, "Minimum nonblack brightness");
            Check(await run(new[] { "color", "1", "000000" }) == 0 && calls.Last().EndsWith("{\"on\":false}"), "Black turns light off");
            int writes = calls.Count(call => call.StartsWith("PUT"));
            Check(await run(new[] { "on", "2" }) == 1, "Unreachable light");
            Check(await run(new[] { "on", "99" }) == 2, "Unknown light");
            Check(await run(new[] { "brightness", "3", "50" }) == 2, "Unsupported brightness");
            Check(await run(new[] { "color", "3", "FF8800" }) == 2, "Unsupported color");
            Check(writes == calls.Count(call => call.StartsWith("PUT")), "Invalid targets must not receive writes");
            int before = calls.Count;
            Check(await run(new[] { "palettes" }) == 0 && output.ToString().Contains("Sunset	built-in	#FF9329, #FF6F51, #E85C8A, 2200 K white") && calls.Count == before, "List palettes without contacting the bridge");
            foreach (var invalid in new[] {
                new[] { "ambience" }, new[] { "ambience", "--together" }, new[] { "ambience", "Sunset", "--speed", "5" }, new[] { "ambience", "Sunset", "--speed" },
                new[] { "ambience", "Sunset", "--speed", "601" }, new[] { "ambience", "Sunset", "--brightness", "0" }, new[] { "ambience", "Sunset", "--bogus" },
                new[] { "ambience", "Sunset", "abc" }, new[] { "ambience", "Sunset", "01" }, new[] { "ambience", "Nope" }, new[] { "palettes", "extra" } })
                Check(await run(invalid) == 2 && calls.Count == before, "Reject invalid ambience input before connecting: " + String.Join(" ", invalid));
            Check(await run(new[] { "ambience", "Sunset", "3" }) == 2, "A plug can't join an ambience");
            Check(await run(new[] { "ambience", "Sunset", "99" }) == 2, "Unknown light in an ambience");
            Check(calls.Skip(before).All(call => call.StartsWith("GET")), "Rejected ambiences send no light commands");
            var oldDelay = AmbienceRunner.Delay; var oldClock = AmbienceRunner.Clock; var fakeNow = DateTime.UtcNow;
            using (var stop = new CancellationTokenSource())
            {
                AmbienceRunner.Clock = () => fakeNow;
                int ambienceStart = calls.Count;
                AmbienceRunner.Delay = (span, cancel) => { fakeNow += span; if (calls.Skip(ambienceStart).Count(call => call.StartsWith("PUT")) >= 2) stop.Cancel(); cancel.ThrowIfCancellationRequested(); return Task.FromResult(0); };
                try
                {
                    Check(await Execute(new[] { "ambience", "sunset", "1", "--together", "--speed", "30", "--brightness", "50" }, () => new Settings { Address = "192.168.1.2", Key = "test-key" }, create, s => { }, output, error, stop.Token) == 0, "Ambience runs until stopped");
                    var fades = calls.Skip(ambienceStart).Where(call => call.StartsWith("PUT")).ToList();
                    Check(fades.Count == 2 && fades.All(call => call.Contains("/lights/1/state") && call.Contains("\"bri\":127")) && fades[0].Contains("\"transitiontime\":20") && fades[1].Contains("\"transitiontime\":300"), "Ambience fades light 1 at the chosen speed and brightness");
                    Check(output.ToString().Contains("Ambience Sunset on 1 light (together), every 30 s. Press Ctrl+C to stop.") && output.ToString().Contains("Ambience stopped."), "Ambience start and stop messages");
                }
                finally { AmbienceRunner.Delay = oldDelay; AmbienceRunner.Clock = oldClock; }
            }
            rejectWrite = true;
            Check(await run(new[] { "on", "1" }) == 1, "Bridge errors return nonzero");
            int pairCalls = calls.Count;
            Check(await run(new[] { "pair", "192.168.1.2" }) == 0 && paired, "Pair saves credentials");
            Check(calls.Skip(pairCalls).Count(call => call.StartsWith("POST")) == 3, "Pair waits for the link button instead of failing");
            Settings kept = null;
            Check(await Execute(new[] { "pair", "192.168.1.3" }, () => new Settings { Address = "192.168.1.2", Key = "old", CustomPalettes = new List<Palette> { new Palette("Kept", PaletteEntry.White(2200), PaletteEntry.White(5000)) }, AmbienceSpeedSeconds = 45 }, create, s => kept = s, output, error, CancellationToken.None) == 0
                && kept != null && kept.Address == "192.168.1.3" && kept.CustomPalettes.Single().Name == "Kept" && kept.AmbienceSpeedSeconds == 45, "Pairing again keeps palettes and ambience choices");
            kept = null;
            Check(await Execute(new[] { "pair", "192.168.1.3" }, () => { throw new InvalidOperationException("unreadable"); }, create, s => kept = s, output, error, CancellationToken.None) == 0
                && kept != null && kept.CustomPalettes.Count == 0, "A broken saved file does not block pairing");
            Check(await Execute(new[] { "list" }, () => new Settings(), create, s => {}, output, error, CancellationToken.None) == 1, "Missing credentials");
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
