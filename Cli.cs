using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
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
  huereka pair <bridge-ip>     Press the bridge link button before running
  huereka help

Uses the same saved pairing as the desktop app, for this Windows user.
Exit codes: 0 = success, 1 = connection/bridge error, 2 = invalid command.
";

        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            if (args.Length == 1 && args[0] == "--self-test")
            {
                try { SelfTest().GetAwaiter().GetResult(); Console.WriteLine("CLI tests passed."); return 0; }
                catch (Exception error) { Console.Error.WriteLine(error); return 1; }
            }
            return Execute(args, Settings.Load, settings => new Bridge(settings), settings => settings.Save(), Console.Out, Console.Error).GetAwaiter().GetResult();
        }

        static void Validate(string[] args)
        {
            if (args.Length == 0) return;
            string command = args[0].ToLowerInvariant();
            int count;
            switch (command)
            {
                case "help": case "--help": case "-h": case "list": count = 1; break;
                case "pair": case "on": case "off": case "warm": case "cool": count = 2; break;
                case "brightness": case "color": count = 3; break;
                default: throw new ArgumentException("Unknown command. Run huereka help.");
            }
            if (args.Length != count) throw new ArgumentException("Wrong number of arguments. Run huereka help.");
            if (count > 1 && command != "pair")
            {
                int id;
                if (!Int32.TryParse(args[1], out id) || id <= 0 || args[1] != id.ToString())
                    throw new ArgumentException("Use a numeric light ID from huereka list.");
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
            // Convert RGB to HSV. Hue uses 0-65535; saturation and brightness use 0-254.
            // HSV saturation is needed here; System.Drawing.Color.GetSaturation returns HSL.
            int rgb = Convert.ToInt32(hex, 16);
            double r = ((rgb >> 16) & 255) / 255.0, g = ((rgb >> 8) & 255) / 255.0, b = (rgb & 255) / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
            if (max == 0) return new Dictionary<string, object> { { "on", false } };
            double delta = max - min, hue = 0;
            if (delta > 0)
            {
                if (max == r) hue = 60 * ((g - b) / delta);
                else if (max == g) hue = 60 * ((b - r) / delta + 2);
                else hue = 60 * ((r - g) / delta + 4);
                if (hue < 0) hue += 360;
            }
            return new Dictionary<string, object> {
                { "on", true }, { "hue", (int)Math.Round(hue / 360 * 65535) },
                { "sat", (int)Math.Round(delta / max * 254) },
                { "bri", Math.Max(1, (int)Math.Round(max * 254)) }
            };
        }

        static async Task<int> Execute(string[] args, Func<Settings> load, Func<Settings, Bridge> create, Action<Settings> save, TextWriter output, TextWriter errorOutput)
        {
            try
            {
                Validate(args);
                string command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
                if (command == "help" || command == "--help" || command == "-h") { output.Write(Help); return 0; }
                if (command == "pair")
                {
                    var settings = new Settings { Address = Bridge.ValidateAddress(args[1]) };
                    var pairingBridge = create(settings);
                    await pairingBridge.Pair();
                    await pairingBridge.Lights();
                    save(settings);
                    output.WriteLine("Paired with " + settings.Address + ". Run huereka list.");
                    return 0;
                }
                var connection = load();
                if (String.IsNullOrEmpty(connection.Key)) throw new InvalidOperationException("No saved pairing. Pair in the desktop app or run huereka pair <bridge-ip> after pressing the bridge button.");
                var bridge = create(connection);
                var lights = await bridge.Lights();
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
                    state = new { on = true, bri = MainWindow.Brightness(Int32.Parse(args[2])) };
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
            Func<Settings, Bridge> create = settings => new Bridge(settings, (method, path, body) => {
                calls.Add(method + " " + path + " " + body);
                if (method == "POST") return "[{\"success\":{\"username\":\"test-key\"}}]";
                if (method == "GET") return "{\"1\":{\"name\":\"Desk\",\"state\":{\"on\":true,\"bri\":127,\"ct\":300,\"hue\":0,\"sat\":254,\"reachable\":true}},\"2\":{\"name\":\"Offline\",\"state\":{\"on\":false,\"reachable\":false}},\"3\":{\"name\":\"Plug\",\"state\":{\"on\":false,\"reachable\":true}}}";
                return rejectWrite ? "[{\"error\":{\"type\":7,\"description\":\"invalid value\"}}]" : "[{\"success\":{}}]";
            });
            var output = new StringWriter(); var error = new StringWriter();
            Func<string[], Task<int>> run = args => Execute(args, () => new Settings { Address = "192.168.1.2", Key = "test-key" }, create, settings => paired = true, output, error);
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
            rejectWrite = true;
            Check(await run(new[] { "on", "1" }) == 1, "Bridge errors return nonzero");
            Check(await run(new[] { "pair", "192.168.1.2" }) == 0 && paired, "Pair saves credentials");
            Check(await Execute(new[] { "list" }, () => new Settings(), create, s => {}, output, error) == 1, "Missing credentials");
        }
        static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    }
}
