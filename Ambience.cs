using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace HueReka
{
    enum AmbienceMode { Drift, Together }

    sealed class AmbienceOptions
    {
        public Palette Palette;
        public AmbienceMode Mode = AmbienceMode.Drift;
        public int SpeedSeconds = AmbienceSpeed.DefaultSeconds;
        public int BrightnessPercent = AmbienceSpeed.DefaultBrightness;
        public int Seed = Environment.TickCount;
    }

    static class AmbienceSpeed
    {
        public const int MinSeconds = 10, MaxSeconds = 600, DefaultSeconds = 120, DefaultBrightness = 70;
        // Logarithmic, so the short end, where a few seconds are noticeable, gets fine control.
        public static int FromSlider(int value)
        {
            double fraction = (Math.Max(1, Math.Min(100, value)) - 1) / 99.0;
            double seconds = MinSeconds * Math.Pow((double)MaxSeconds / MinSeconds, fraction);
            int rounded = seconds < 60 ? (int)Math.Round(seconds) : (int)Math.Round(seconds / 10) * 10;
            return Math.Max(MinSeconds, Math.Min(MaxSeconds, rounded));
        }
        public static int ToSlider(int seconds)
        {
            int clamped = Math.Max(MinSeconds, Math.Min(MaxSeconds, seconds));
            return 1 + (int)Math.Round(99 * Math.Log((double)clamped / MinSeconds) / Math.Log((double)MaxSeconds / MinSeconds));
        }
        public static string Describe(int seconds)
        {
            if (seconds < 60) return "every " + seconds + " s";
            int minutes = seconds / 60, rest = seconds % 60;
            return "every " + minutes + " min" + (rest > 0 ? " " + rest + " s" : "");
        }
    }

    static class AmbiencePlanner
    {
        public const int MaxTransition = 65535; // deciseconds; the bridge's limit
        public static bool CanJoin(Light light)
        {
            var state = light.State;
            return state.ContainsKey("bri") || state.ContainsKey("ct") || (state.ContainsKey("hue") && state.ContainsKey("sat"));
        }
        // Drift spreads the lights over the palette, so neighbouring lights never show the same entry.
        public static int EntryIndex(AmbienceMode mode, int step, int lightIndex, int lightCount, int entryCount)
        {
            int offset = mode == AmbienceMode.Together ? 0 : lightCount <= entryCount ? lightIndex * entryCount / lightCount : lightIndex % entryCount;
            return (step + offset) % entryCount;
        }
        // Each light's own pace in Drift mode: 0.75-1.25 times the speed, fixed for a seed and light,
        // so the lights slowly fall out of step instead of changing in lockstep.
        public static double PaceFactor(int seed, string lightId)
        {
            int hash = seed;
            unchecked { foreach (char c in lightId ?? "") hash = hash * 31 + c; }
            return 0.75 + new Random(hash).NextDouble() * 0.5;
        }
        public static int StepSeconds(AmbienceOptions options, string lightId)
        {
            if (options.Mode == AmbienceMode.Together) return options.SpeedSeconds;
            return Math.Max(1, (int)Math.Round(options.SpeedSeconds * PaceFactor(options.Seed, lightId)));
        }
        public static Dictionary<string, object> State(PaletteEntry entry, Light light, int brightnessPercent, int transitionDeciseconds)
        {
            var current = light.State;
            bool color = current.ContainsKey("hue") && current.ContainsKey("sat"), white = current.ContainsKey("ct");
            var state = new Dictionary<string, object> { { "on", true } };
            if (current.ContainsKey("bri")) state["bri"] = MainWindow.Brightness(brightnessPercent);
            if (entry.IsWhite() && white) state["ct"] = HueColor.Mired(entry.Kelvin, light.MinCt, light.MaxCt);
            else if (entry.IsWhite() && color) { var tint = HueColor.FromKelvin(entry.Kelvin); AddColor(state, HueColor.FromRgb(tint.R, tint.G, tint.B)); }
            else if (!entry.IsWhite() && color) AddColor(state, HueColor.FromRgb(entry.R, entry.G, entry.B));
            else if (!entry.IsWhite() && white) state["ct"] = NearestWhite(entry, light);
            state["transitiontime"] = Math.Max(0, Math.Min(MaxTransition, transitionDeciseconds));
            return state;
        }
        static void AddColor(Dictionary<string, object> state, HueHsv hsv) { state["hue"] = hsv.Hue; state["sat"] = hsv.Sat; }
        // Warm hues become the bulb's warmest white, blues its coolest, everything else a neutral white.
        static int NearestWhite(PaletteEntry entry, Light light)
        {
            var hsv = HueColor.FromRgb(entry.R, entry.G, entry.B);
            double degrees = hsv.Hue * 360.0 / 65535;
            int neutral = (light.MinCt + light.MaxCt) / 2;
            if (hsv.Sat < 40) return neutral;
            if (degrees < 70 || degrees >= 330) return light.MaxCt;
            if (degrees >= 160 && degrees < 260) return light.MinCt;
            return neutral;
        }
    }

    // Reports on the caller's thread. Progress<T> would post to the thread pool in a console app and reorder lines.
    sealed class Reporter : IProgress<string>
    {
        readonly Action<string> write;
        public Reporter(Action<string> write) { this.write = write; }
        public void Report(string value) { write(value); }
    }

    // Runs an ambience until cancelled. Each command asks the bridge to fade over the whole step
    // (transitiontime), so HueReka sends one command per light per step and the bridge does the rest.
    sealed class AmbienceRunner
    {
        // Replaceable so tests can run hours of ambience instantly.
        internal static Func<TimeSpan, CancellationToken, Task> Delay = (span, cancel) => Task.Delay(span, cancel);
        internal static Func<DateTime> Clock = () => DateTime.UtcNow;
        // The bridge accepts about 10 light commands per second.
        public static readonly TimeSpan CommandSpacing = TimeSpan.FromMilliseconds(100);
        // The first fade is short so starting an ambience is visible right away.
        public static readonly TimeSpan FirstFade = TimeSpan.FromSeconds(2);

        readonly Bridge bridge;
        readonly AmbienceOptions options;
        readonly List<Light> lights;
        readonly HashSet<string> excluded = new HashSet<string>();
        readonly object gate = new object();

        public AmbienceRunner(Bridge bridge, AmbienceOptions options, IEnumerable<Light> lights)
        {
            if (bridge == null) throw new ArgumentNullException("bridge");
            if (options == null || options.Palette == null || options.Palette.Problem(new string[0]) != null) throw new ArgumentException("Choose a valid palette.");
            if (options.SpeedSeconds < AmbienceSpeed.MinSeconds || options.SpeedSeconds > AmbienceSpeed.MaxSeconds) throw new ArgumentException("Speed must be from 10 to 600 seconds.");
            if (options.BrightnessPercent < 1 || options.BrightnessPercent > 100) throw new ArgumentException("Brightness must be from 1 to 100.");
            this.lights = (lights ?? Enumerable.Empty<Light>()).Where(AmbiencePlanner.CanJoin).GroupBy(light => light.Id).Select(group => group.First()).ToList();
            if (this.lights.Count == 0) throw new ArgumentException("None of these lights can change color or brightness.");
            this.bridge = bridge; this.options = options;
        }

        public AmbienceOptions Options { get { return options; } }
        public List<string> LightIds { get { lock (gate) return lights.Where(light => !excluded.Contains(light.Id)).Select(light => light.Id).ToList(); } }
        public int LightCount { get { return LightIds.Count; } }
        // Takes lights out, for example when the user changes them by hand.
        public void Exclude(IEnumerable<string> ids) { lock (gate) foreach (string id in ids) excluded.Add(id); }
        bool Active(Light light) { lock (gate) return !excluded.Contains(light.Id); }

        public async Task Run(IProgress<string> report, CancellationToken cancel)
        {
            var steps = lights.ToDictionary(light => light.Id, light => 0);
            var due = lights.ToDictionary(light => light.Id, light => Clock());
            bool lost = false;
            try
            {
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (LightCount == 0) { Say(report, "Ambience stopped: every light was changed by hand."); return; }
                    for (int i = 0; i < lights.Count; i++)
                    {
                        var light = lights[i];
                        if (!Active(light) || due[light.Id] > Clock()) continue;
                        int step = steps[light.Id];
                        TimeSpan fade = step == 0 ? FirstFade : TimeSpan.FromSeconds(AmbiencePlanner.StepSeconds(options, light.Id));
                        var entries = options.Palette.Entries;
                        var entry = entries[AmbiencePlanner.EntryIndex(options.Mode, step, i, lights.Count, entries.Count)];
                        try
                        {
                            await bridge.Set(light.Id, AmbiencePlanner.State(entry, light, options.BrightnessPercent, (int)(fade.TotalMilliseconds / 100)));
                            if (lost) { lost = false; Say(report, "Back in touch with the bridge. The ambience continues."); }
                        }
                        catch (WebException) { if (!lost) { lost = true; Say(report, "Lost contact with the bridge. The ambience will keep trying."); } }
                        catch (Exception error) { Say(report, light.Name + ": " + error.Message.Replace("Hue Bridge: ", "")); }
                        steps[light.Id] = step + 1;
                        due[light.Id] = Clock() + fade;
                        await Delay(CommandSpacing, cancel);
                    }
                    var active = lights.Where(Active).ToList();
                    if (active.Count == 0) continue;
                    TimeSpan wait = active.Min(light => due[light.Id]) - Clock();
                    if (wait > TimeSpan.Zero) await Delay(wait, cancel);
                }
            }
            catch (OperationCanceledException) { }
        }

        static void Say(IProgress<string> report, string message) { if (report != null) report.Report(message); }
    }
}
