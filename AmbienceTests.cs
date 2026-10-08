using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace HueReka
{
    static class AmbienceTests
    {
        internal const string ColorBulb = "{\"on\":true,\"bri\":100,\"hue\":0,\"sat\":0,\"ct\":300,\"reachable\":true}";
        internal const string ColorOnly = "{\"on\":true,\"bri\":100,\"hue\":0,\"sat\":0,\"reachable\":true}";
        internal const string WhiteBulb = "{\"on\":true,\"bri\":100,\"ct\":300,\"reachable\":true}";
        internal const string Dimmable = "{\"on\":true,\"bri\":100,\"reachable\":true}";
        internal const string Plug = "{\"on\":true,\"reachable\":true}";

        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        internal static Light MakeLight(string id, string stateJson) { return new Light(id, Json.Map(Json.Parse("{\"name\":\"Light " + id + "\",\"state\":" + stateJson + "}"))); }

        public static void Run()
        {
            PaletteTests();
            PlannerTests();
            RunnerTests();
            SettingsTests();
        }

        static void SettingsTests()
        {
            var original = new Settings { Address = "192.168.1.2", Key = "k", FavoriteLight = "4",
                CustomPalettes = new List<Palette> { new Palette("Cozy", PaletteEntry.Rgb(255, 0, 0), PaletteEntry.White(2700)) },
                AmbiencePalette = "Cozy", AmbienceTogether = true, AmbienceSpeedSeconds = 45, AmbienceBrightness = 30 };
            var copy = Json.Serializer.Deserialize<Settings>(Json.Serializer.Serialize(original));
            Assert(copy.CustomPalettes.Single().Name == "Cozy" && copy.CustomPalettes[0].Entries[0].R == 255 && copy.CustomPalettes[0].Entries[1].Kelvin == 2700, "Custom palettes survive saving");
            Assert(copy.AmbiencePalette == "Cozy" && copy.AmbienceTogether && copy.AmbienceSpeedSeconds == 45 && copy.AmbienceBrightness == 30, "Ambience choices survive saving");
            Assert(!Json.Serializer.Serialize(original).Contains("IsWhite"), "Only palette data is saved");
            var old = Json.Serializer.Deserialize<Settings>("{\"Address\":\"192.168.1.2\",\"Key\":\"k\",\"Fingerprint\":\"\",\"FavoriteLight\":\"1\"}");
            Assert(old.CustomPalettes.Count == 0 && old.AmbiencePalette == "Sunset" && !old.AmbienceTogether && old.AmbienceSpeedSeconds == 120 && old.AmbienceBrightness == 70, "Settings from older versions get ambience defaults");
            var next = new Settings { Address = "192.168.1.9" };
            next.KeepPreferencesFrom(original);
            Assert(next.Address == "192.168.1.9" && next.Key == "" && next.FavoriteLight == "" && next.CustomPalettes.Single().Name == "Cozy" && next.AmbienceSpeedSeconds == 45 && next.AmbienceTogether, "Pairing again keeps palettes and ambience choices, not the bridge's details");
            next.CustomPalettes[0].Name = "Changed";
            Assert(original.CustomPalettes[0].Name == "Cozy", "Kept palettes are copies");
            var fromBroken = new Settings(); fromBroken.KeepPreferencesFrom(new Settings { CustomPalettes = null });
            Assert(fromBroken.CustomPalettes != null && fromBroken.CustomPalettes.Count == 0, "A missing palette list is kept as empty");
        }

        static void PlannerTests()
        {
            Assert(AmbienceSpeed.FromSlider(1) == 10 && AmbienceSpeed.FromSlider(100) == 600 && AmbienceSpeed.FromSlider(-5) == 10 && AmbienceSpeed.FromSlider(500) == 600, "Speed slider spans 10 s to 10 min");
            Assert(AmbienceSpeed.ToSlider(10) == 1 && AmbienceSpeed.ToSlider(600) == 100 && AmbienceSpeed.ToSlider(5) == 1 && AmbienceSpeed.ToSlider(9999) == 100, "Speed to slider clamps");
            for (int v = 2; v <= 100; v++) Assert(AmbienceSpeed.FromSlider(v) >= AmbienceSpeed.FromSlider(v - 1), "Speed slider never goes backwards");
            Assert(AmbienceSpeed.FromSlider(AmbienceSpeed.ToSlider(120)) == 120, "Default speed survives the slider");
            Assert(AmbienceSpeed.Describe(45) == "every 45 s" && AmbienceSpeed.Describe(120) == "every 2 min" && AmbienceSpeed.Describe(90) == "every 1 min 30 s", "Speed descriptions");

            for (int count = 2; count <= 5; count++)
                for (int lights = 1; lights <= 8; lights++)
                    for (int step = 0; step < 6; step++)
                    {
                        var drift = Enumerable.Range(0, lights).Select(i => AmbiencePlanner.EntryIndex(AmbienceMode.Drift, step, i, lights, count)).ToList();
                        Assert(drift.All(index => index >= 0 && index < count), "Drift index stays in the palette");
                        for (int i = 1; i < lights; i++) Assert(drift[i] != drift[i - 1], "Neighbouring lights show different entries in Drift");
                        if (lights <= count) Assert(drift.Distinct().Count() == lights, "Lights spread over the palette when there is room");
                        Assert(Enumerable.Range(0, lights).All(i => AmbiencePlanner.EntryIndex(AmbienceMode.Together, step, i, lights, count) == step % count), "Together moves every light to the same entry");
                    }

            var ids = Enumerable.Range(1, 50).Select(i => i.ToString()).ToList();
            var factors = ids.Select(id => AmbiencePlanner.PaceFactor(7, id)).ToList();
            Assert(factors.All(f => f >= 0.75 && f < 1.25) && factors.Distinct().Count() > 10, "Each light gets its own pace in 0.75-1.25");
            Assert(ids.All(id => AmbiencePlanner.PaceFactor(7, id) == AmbiencePlanner.PaceFactor(7, id)), "The same seed gives the same pace");
            Assert(ids.Any(id => AmbiencePlanner.PaceFactor(7, id) != AmbiencePlanner.PaceFactor(8, id)), "Another seed gives other paces");
            // Lights are usually numbered 1, 2, 3...; their paces must not climb or fall in a visible pattern.
            // Unmixed hashes made every other light's pace fall by the same small amount (1.249, 0.988, 1.227, 0.966...).
            Func<List<double>, bool> monotonic = values => values.Zip(values.Skip(1), (a, b) => Math.Sign(b - a)).Distinct().Count() == 1;
            var twelve = ids.Take(12).Select(id => AmbiencePlanner.PaceFactor(7, id)).ToList();
            Assert(!monotonic(twelve.Where((f, i) => i % 2 == 0).ToList()) && !monotonic(twelve.Where((f, i) => i % 2 == 1).ToList()), "Consecutive light ids get unrelated paces");
            var drifting = new AmbienceOptions { Palette = Palettes.Presets[0], Mode = AmbienceMode.Drift, SpeedSeconds = 100, Seed = 7 };
            Assert(ids.All(id => AmbiencePlanner.StepSeconds(drifting, id) >= 75 && AmbiencePlanner.StepSeconds(drifting, id) <= 125), "Drift steps stay within a quarter of the speed");
            drifting.Mode = AmbienceMode.Together;
            Assert(ids.All(id => AmbiencePlanner.StepSeconds(drifting, id) == 100), "Together steps use the exact speed");

            var color = MakeLight("1", ColorBulb); var colorOnly = MakeLight("2", ColorOnly); var white = MakeLight("3", WhiteBulb);
            var dimmable = MakeLight("4", Dimmable); var plug = MakeLight("5", Plug);
            var narrow = new Light("6", Json.Map(Json.Parse("{\"name\":\"Narrow\",\"state\":" + WhiteBulb + ",\"capabilities\":{\"control\":{\"ct\":{\"min\":200,\"max\":454}}}}")));
            Assert(new[] { color, colorOnly, white, dimmable }.All(AmbiencePlanner.CanJoin) && !AmbiencePlanner.CanJoin(plug), "Plugs can't join, every dimmable or colored bulb can");

            var red = AmbiencePlanner.State(PaletteEntry.Rgb(255, 0, 0), color, 70, 1200);
            Assert(Equals(red["on"], true) && (int)red["hue"] == 0 && (int)red["sat"] == 254 && (int)red["bri"] == 178 && (int)red["transitiontime"] == 1200 && !red.ContainsKey("ct"), "Color entry on a color bulb");
            var candle = AmbiencePlanner.State(PaletteEntry.White(2000), white, 70, 50);
            Assert((int)candle["ct"] == 500 && !candle.ContainsKey("hue"), "White entry on a white bulb");
            Assert((int)AmbiencePlanner.State(PaletteEntry.White(6500), narrow, 70, 50)["ct"] == 200, "White clamps to the bulb's coolest white");
            var approximated = AmbiencePlanner.State(PaletteEntry.White(2700), colorOnly, 70, 50);
            Assert(approximated.ContainsKey("hue") && (int)approximated["sat"] > 0 && !approximated.ContainsKey("ct"), "White on a color-only bulb becomes a warm color");
            Assert((int)AmbiencePlanner.State(PaletteEntry.Rgb(255, 120, 0), white, 70, 50)["ct"] == 500, "Warm colors become the warmest white on white bulbs");
            Assert((int)AmbiencePlanner.State(PaletteEntry.Rgb(0, 80, 255), white, 70, 50)["ct"] == 153, "Blues become the coolest white on white bulbs");
            Assert((int)AmbiencePlanner.State(PaletteEntry.Rgb(40, 220, 120), white, 70, 50)["ct"] == 326, "Other colors become a neutral white");
            var dim = AmbiencePlanner.State(PaletteEntry.Rgb(255, 0, 0), dimmable, 70, 50);
            Assert(dim.Keys.OrderBy(k => k).SequenceEqual(new[] { "bri", "on", "transitiontime" }), "Dimmable bulbs only fade brightness");
            Assert((int)AmbiencePlanner.State(PaletteEntry.White(3000), white, 70, 70000)["transitiontime"] == 65535 && (int)AmbiencePlanner.State(PaletteEntry.White(3000), white, 70, -1)["transitiontime"] == 0, "Transition time stays within the bridge's limits");
        }

        static void PaletteTests()
        {
            var none = new string[0];
            Assert(Palettes.Presets.Select(p => p.Name).SequenceEqual(new[] { "Sunset", "Ocean", "Forest", "Candlelight", "Aurora", "Daylight" }), "Built-in presets in order");
            Assert(Palettes.Presets.All(p => p.Problem(Palettes.Presets.Where(other => other != p).Select(other => other.Name)) == null), "Every preset is valid");
            var two = new[] { PaletteEntry.Rgb(255, 0, 0), PaletteEntry.White(2700) };
            Assert(new Palette("Cozy", two).Problem(none) == null, "Valid custom palette");
            Assert(new Palette("  ", two).Problem(none) != null, "Blank name rejected");
            Assert(new Palette(new string('x', 41), two).Problem(none) != null, "41-character name rejected");
            Assert(new Palette(new string('x', 40), two).Problem(none) == null, "40-character name accepted");
            Assert(new Palette(" sunset ", two).Problem(new[] { "Sunset" }) != null, "Duplicate names compare trimmed and ignoring case");
            Assert(new Palette("Solo", PaletteEntry.Rgb(1, 2, 3)).Problem(none) != null, "One entry is too few");
            Assert(new Palette("Big", Enumerable.Repeat(PaletteEntry.White(3000), 13).ToArray()).Problem(none) != null, "Thirteen entries are too many");
            Assert(new Palette("Big", Enumerable.Repeat(PaletteEntry.White(3000), 12).ToArray()).Problem(none) == null, "Twelve entries are allowed");
            Assert(new Palette("Hot", PaletteEntry.White(1999), PaletteEntry.White(3000)).Problem(none) != null, "Too-warm white rejected");
            Assert(new Palette("Cold", PaletteEntry.White(6501), PaletteEntry.White(3000)).Problem(none) != null, "Too-cool white rejected");
            Assert(new Palette("Bad", PaletteEntry.Rgb(256, 0, 0), PaletteEntry.White(3000)).Problem(none) != null, "RGB above 255 rejected");
            Assert(new Palette("Neg", new PaletteEntry { R = 1, Kelvin = -5 }, PaletteEntry.White(3000)).Problem(none) != null, "Negative Kelvin rejected");
            Assert(new Palette("Hole", PaletteEntry.White(3000), null).Problem(none) != null, "Empty entry rejected");
            Assert(new Palette { Name = "Nothing", Entries = null }.Problem(none) != null, "Missing entry list rejected");
            var original = new Palette("Cozy", two); var copy = original.Copy(); copy.Entries[0].R = 9; copy.Name = "Other";
            Assert(original.Entries[0].R == 255 && original.Name == "Cozy", "Copy never changes the original");
            Assert(PaletteEntry.White(2200).Label() == "2200 K white" && PaletteEntry.Rgb(255, 136, 0).Label() == "#FF8800", "Entry labels");

            Assert(HueColor.Mired(2000, 153, 500) == 500 && HueColor.Mired(6500, 153, 500) == 154 && HueColor.Mired(4000, 153, 500) == 250, "Kelvin to mired");
            Assert(HueColor.Brightness(1) == 3 && HueColor.Brightness(70) == 178 && HueColor.Brightness(100) == 254 && HueColor.Brightness(0) == 1 && MainWindow.Brightness(50) == HueColor.Brightness(50), "Percent to bridge brightness");
            Assert(HueColor.Mired(6500, 200, 454) == 200 && HueColor.Mired(2000, 153, 454) == 454, "Mired clamps to the bulb's range");
            var red = HueColor.FromRgb(255, 0, 0);
            Assert(red.Hue == 0 && red.Sat == 254 && red.Bri == 254, "Red to Hue HSV");
            Assert(HueColor.FromRgb(0, 0, 255).Hue == 43690 && HueColor.FromRgb(0, 0, 0).Sat == 0, "Blue hue and black");
            var warm = HueColor.FromKelvin(2000); var cool = HueColor.FromKelvin(6500);
            Assert(warm.R == 255 && warm.B < 40 && cool.B > 240, "Kelvin preview runs from amber to near white");
            Assert(PaletteEntry.Rgb(300, -4, 10).PreviewColor().R == 255, "Preview clamps bad RGB instead of throwing");

            var settings = new Settings { CustomPalettes = new List<Palette> {
                new Palette("Cozy", two), new Palette("Broken", PaletteEntry.White(9000), PaletteEntry.White(3000)), null,
                new Palette("COZY", two), new Palette("Ocean", two) } };
            Assert(Palettes.Custom(settings).Select(p => p.Name).SequenceEqual(new[] { "Cozy" }), "Invalid, duplicate and preset-named saved palettes are skipped");
            Assert(Palettes.All(settings).Count == 7 && Palettes.Find(settings, " cozy ").Name == "Cozy" && Palettes.Find(settings, "Nope") == null && Palettes.Find(settings, null) == null, "Find palettes by name");
            Assert(Palettes.IsPreset("aurora") && !Palettes.IsPreset("Cozy"), "Preset detection ignores case");
            Assert(Palettes.Custom(new Settings { CustomPalettes = null }).Count == 0, "A missing palette list loads as empty");
        }

        static void RunnerTests()
        {
            var oldDelay = AmbienceRunner.Delay; var oldClock = AmbienceRunner.Clock;
            var start = new DateTime(2026, 1, 1); var now = start;
            AmbienceRunner.Clock = () => now;
            try
            {
                var calls = new List<string>(); var times = new List<DateTime>(); bool down = false;
                var bridge = new Bridge(new Settings { Address = "192.168.1.2", Key = "k" }, (method, path, body) => {
                    if (down) throw new WebException("Bridge unplugged");
                    calls.Add(path + " " + body); times.Add(now);
                    return path.Contains("/lights/3/") ? "[{\"error\":{\"type\":201,\"description\":\"device is not reachable\"}}]" : "[{\"success\":{}}]";
                });
                var lights = new List<Light> { MakeLight("1", ColorBulb), MakeLight("2", WhiteBulb), MakeLight("3", ColorBulb), MakeLight("4", Plug) };
                var sunset = Palettes.Presets[0];
                Action<AmbienceOptions, IEnumerable<Light>, string> rejects = (bad, chosen, message) => {
                    try { new AmbienceRunner(bridge, bad, chosen); throw new Exception(message); } catch (ArgumentException) { } };
                rejects(new AmbienceOptions { Palette = new Palette("Bad", PaletteEntry.White(3000)) }, lights, "Invalid palette rejected");
                rejects(new AmbienceOptions { Palette = sunset, SpeedSeconds = 9 }, lights, "Speed below 10 s rejected");
                rejects(new AmbienceOptions { Palette = sunset, BrightnessPercent = 0 }, lights, "Brightness 0 rejected");
                rejects(new AmbienceOptions { Palette = sunset }, lights.Skip(3), "Plugs alone can't run an ambience");

                // Together: three rounds over lights 1-3, then cancel.
                var messages = new List<string>(); var waits = new List<TimeSpan>();
                using (var stop = new CancellationTokenSource())
                {
                    AmbienceRunner.Delay = (span, cancel) => { waits.Add(span); now += span; if (calls.Count >= 9) stop.Cancel(); cancel.ThrowIfCancellationRequested(); return Task.FromResult(0); };
                    var together = new AmbienceRunner(bridge, new AmbienceOptions { Palette = sunset, Mode = AmbienceMode.Together, SpeedSeconds = 60, BrightnessPercent = 50, Seed = 1 }, lights);
                    Assert(together.LightCount == 3 && together.LightIds.SequenceEqual(new[] { "1", "2", "3" }), "Plugs are left out of the ambience");
                    together.Run(new Reporter(messages.Add), stop.Token).GetAwaiter().GetResult();
                }
                Assert(calls.Count == 9, "Cancelling stops the ambience cleanly");
                Assert(calls.Take(3).All(call => call.Contains("\"transitiontime\":20")) && calls.Skip(3).All(call => call.Contains("\"transitiontime\":600")), "First fade is quick, later fades last the whole step");
                Assert(calls[0].StartsWith("/api/k/lights/1/state") && calls[1].StartsWith("/api/k/lights/2/state") && calls[2].StartsWith("/api/k/lights/3/state"), "Each light gets one command per step");
                Assert(calls[0].Substring(calls[0].IndexOf(' ')) == calls[2].Substring(calls[2].IndexOf(' ')) && calls[3].Substring(calls[3].IndexOf(' ')) != calls[0].Substring(calls[0].IndexOf(' ')), "Together lights match each step and move on the next");
                Assert(calls[1].Contains("\"ct\":500") && calls.All(call => call.Contains("\"bri\":127")), "White bulbs follow with whites, at the chosen brightness");
                Assert(times[3] - times[0] == TimeSpan.FromSeconds(2) && times[6] - times[3] == TimeSpan.FromSeconds(60), "Steps wait for the fade to finish");
                Assert(waits.Count(w => w == AmbienceRunner.CommandSpacing) >= 8, "Commands are spaced to respect the bridge's rate limit");
                Assert(messages.Count == 3 && messages.All(m => m.StartsWith("Light 3:") && m.Contains("not reachable")), "An unreachable bulb is reported once per step and the others continue");

                // Drift: the bridge drops and returns, then lights are excluded until none are left.
                calls.Clear(); messages.Clear(); now = start;
                int delays = 0, downDelays = 0;
                var options = new AmbienceOptions { Palette = sunset, Mode = AmbienceMode.Drift, SpeedSeconds = 100, BrightnessPercent = 50, Seed = 7 };
                var drift = new AmbienceRunner(bridge, options, lights.Take(2));
                AmbienceRunner.Delay = (span, cancel) => {
                    if (++delays > 1000) throw new Exception("Runner never stopped");
                    now += span;
                    if (calls.Count == 2 && downDelays == 0) down = true;
                    if (down && ++downDelays > 4) down = false;
                    if (calls.Count == 4) drift.Exclude(new[] { "1" });
                    if (calls.Count == 6) drift.Exclude(new[] { "2", "2" });
                    return Task.FromResult(0);
                };
                drift.Run(new Reporter(messages.Add), CancellationToken.None).GetAwaiter().GetResult();
                Assert(messages.Count(m => m.StartsWith("Lost contact")) == 1 && messages.Count(m => m.StartsWith("Back in touch")) == 1, "A dropped bridge is reported once, and its return once");
                Assert(messages.Last().StartsWith("Ambience stopped") && drift.LightCount == 0, "The ambience stops when every light was taken out");
                Assert(calls.Skip(4).All(call => call.StartsWith("/api/k/lights/2/state")), "An excluded light gets no more commands");
                Assert(calls.Any(call => call.StartsWith("/api/k/lights/1/state") && call.Contains("\"transitiontime\":" + AmbiencePlanner.StepSeconds(options, "1") * 10)), "Drift fades each light at its own pace");
                // A light that lost the bridge retries the same step soon, instead of skipping ahead.
                var attempts = new List<string>(); var attemptTimes = new List<DateTime>(); now = start;
                var flaky = new Bridge(new Settings { Address = "192.168.1.2", Key = "k" }, (method, path, body) => {
                    attempts.Add(body); attemptTimes.Add(now);
                    if (attempts.Count == 2) throw new WebException("Bridge unplugged");
                    return "[{\"success\":{}}]"; });
                using (var stop = new CancellationTokenSource())
                {
                    AmbienceRunner.Delay = (span, cancel) => { now += span; if (attempts.Count >= 4) stop.Cancel(); cancel.ThrowIfCancellationRequested(); return Task.FromResult(0); };
                    new AmbienceRunner(flaky, new AmbienceOptions { Palette = sunset, Mode = AmbienceMode.Together, SpeedSeconds = 60, BrightnessPercent = 50, Seed = 1 }, new[] { lights[0] }).Run(null, stop.Token).GetAwaiter().GetResult();
                }
                Assert(attempts.Count == 4 && attempts[2] == attempts[1] && attempts[1] != attempts[0] && attempts[3] != attempts[2], "A dropped bridge makes the light retry the same step, not skip it");
                Assert(attemptTimes[2] - attemptTimes[1] == AmbienceRunner.RetryDelay, "The retry comes after the short retry delay, not the whole fade");

                // Together: a light that missed a command rejoins the current step, and the shared schedule never slips.
                var sent = new List<Tuple<string, string, DateTime>>(); int lightTwoCalls = 0; now = start;
                var shaky = new Bridge(new Settings { Address = "192.168.1.2", Key = "k" }, (method, path, body) => {
                    string id = path.Split('/')[4]; DateTime asked = now; now += TimeSpan.FromMilliseconds(30); // every request takes a while
                    if (id == "2" && ++lightTwoCalls == 2) throw new WebException("Bridge unplugged");
                    sent.Add(Tuple.Create(id, body, asked)); return "[{\"success\":{}}]"; });
                var three = new[] { MakeLight("1", ColorBulb), MakeLight("2", ColorBulb), MakeLight("3", ColorBulb) };
                using (var stop = new CancellationTokenSource())
                {
                    AmbienceRunner.Delay = (span, cancel) => { now += span; if (sent.Count >= 15) stop.Cancel(); cancel.ThrowIfCancellationRequested(); return Task.FromResult(0); };
                    new AmbienceRunner(shaky, new AmbienceOptions { Palette = sunset, Mode = AmbienceMode.Together, SpeedSeconds = 60, BrightnessPercent = 50, Seed = 1 }, three).Run(null, stop.Token).GetAwaiter().GetResult();
                }
                var firsts = sent.Where(call => call.Item1 == "1").ToList();
                var rounds = firsts.Select(first => sent.Where(call => call.Item3 >= first.Item3 && call.Item3 < first.Item3 + TimeSpan.FromSeconds(1)).ToList()).ToList();
                var retried = sent.Where(call => call.Item1 == "2").ToList()[1];
                Assert(firsts.Count == 5 && retried.Item2 == firsts[1].Item2 && retried.Item3 > firsts[1].Item3 + AmbienceRunner.RetryDelay - TimeSpan.FromSeconds(1) && retried.Item3 < firsts[2].Item3, "A light that missed a step gets that same step on its retry");
                Assert(rounds.Skip(2).All(round => round.Count == 3 && round.Select(call => call.Item2).Distinct().Count() == 1 && round.Select(call => call.Item1).SequenceEqual(new[] { "1", "2", "3" })), "After a missed command, every light gets the same entry in every round");
                Assert(firsts.Skip(1).Zip(firsts.Skip(2), (a, b) => b.Item3 - a.Item3).All(gap => gap == TimeSpan.FromSeconds(60)), "Together rounds stay exactly one fade apart, whatever the commands' delays");

                // The runner keeps its own copy of the options.
                var mine = new AmbienceOptions { Palette = sunset, SpeedSeconds = 60 };
                var copied = new AmbienceRunner(bridge, mine, lights);
                mine.Palette = null; mine.SpeedSeconds = 5;
                Assert(copied.Options.Palette != null && copied.Options.Palette.Name == "Sunset" && copied.Options.SpeedSeconds == 60, "Changing the caller's options later doesn't affect a runner");
            }
            finally { AmbienceRunner.Delay = oldDelay; AmbienceRunner.Clock = oldClock; }
        }
    }
}
