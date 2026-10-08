using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HueReka
{
    static class AmbienceWindowTests
    {
        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        static T Get<T>(object owner, string name) { return (T)owner.GetType().GetField(name, Tests.Private).GetValue(owner); }
        static List<string> Puts(List<string> calls, int from) { lock (calls) return calls.Skip(from).Where(call => call.StartsWith("PUT")).ToList(); }
        static int Count(List<string> calls) { lock (calls) return calls.Count; }
        static void Pause(int milliseconds) { var until = DateTime.UtcNow.AddMilliseconds(milliseconds); while (DateTime.UtcNow < until) { Application.DoEvents(); Thread.Sleep(5); } }

        public static void Run(MainWindow window, List<string> calls)
        {
            var oldDelay = AmbienceRunner.Delay; var oldClock = AmbienceRunner.Clock;
            var fakeNow = DateTime.UtcNow;
            // Time runs fast: every wait takes a few real milliseconds, so the ambience keeps sending commands.
            AmbienceRunner.Clock = () => fakeNow;
            AmbienceRunner.Delay = async (span, cancel) => { await Task.Delay(3, cancel); fakeNow += span; };
            try
            {
                LayoutAndChoices(window);
                RunAndStop(window, calls);
                ClosingStops(calls);
            }
            finally { AmbienceRunner.Delay = oldDelay; AmbienceRunner.Clock = oldClock; }
        }

        static void LayoutAndChoices(MainWindow window)
        {
            var root = Tests.Field<GlassCanvas>(window, "root");
            var lightTab = Tests.Field<GlassButton>(window, "lightTab"); var ambienceTab = Tests.Field<GlassButton>(window, "ambienceTab");
            var subtitle = root.Controls.OfType<Label>().Single(label => label.Text.StartsWith("A little light"));
            Assert(TextRenderer.MeasureText(subtitle.Text, subtitle.Font).Width <= subtitle.Width && !subtitle.Bounds.IntersectsWith(lightTab.Bounds), "Subtitle fits beside the tabs");
            Assert(lightTab.Bottom <= Tests.Field<GlassPanel>(window, "controls").Top && lightTab.Primary && !ambienceTab.Primary, "Tabs sit above the panel and Light is chosen first");
            var panel = Tests.Field<AmbiencePanel>(window, "ambiencePanel"); var lightPage = Tests.Field<Panel>(window, "lightPage");
            Assert(lightPage.Visible && !panel.Visible, "The Light tab shows first");
            ambienceTab.PerformClick(); Application.DoEvents();
            Assert(panel.Visible && !lightPage.Visible && ambienceTab.Primary, "The Ambience tab shows the ambience panel");
            Tests.Screenshot(window, "preview-ambience.png");

            var saved = Tests.Field<Settings>(window, "saved");
            var keep = new Settings(); keep.KeepPreferencesFrom(saved);
            saved.AmbienceSpeedSeconds = 5; saved.AmbienceBrightness = 0; saved.AmbiencePalette = "Gone"; saved.CustomPalettes = null;
            typeof(MainWindow).GetMethod("ShowAmbienceChoices", Tests.Private).Invoke(window, null);
            Assert(panel.SpeedSeconds == 10 && panel.BrightnessPercent == 1 && panel.SelectedPalette.Name == "Sunset", "Bad saved choices are clamped and a missing palette falls back to Sunset");
            saved.KeepPreferencesFrom(keep);
            typeof(MainWindow).GetMethod("ShowAmbienceChoices", Tests.Private).Invoke(window, null);
            Assert(panel.SpeedSeconds == 120 && panel.BrightnessPercent == 70 && panel.Mode == AmbienceMode.Drift, "Default choices");
            Assert(!Get<GlassButton>(panel, "editPalette").Enabled && !Get<GlassButton>(panel, "deletePalette").Enabled, "Built-in palettes can't be edited or deleted");
        }

        static void RunAndStop(MainWindow window, List<string> calls)
        {
            var panel = Tests.Field<AmbiencePanel>(window, "ambiencePanel"); var lightList = Tests.Field<LightList>(window, "lights");
            var start = Get<GlassButton>(panel, "start");
            Tests.Show(window, Tests.DisplayLights());
            Tests.Select(lightList, 0, 1, 3);
            Assert(Get<Label>(panel, "targets").Text.Contains("2 lights you selected"), "Ambience uses the selected lights that can join");
            int before = Count(calls);
            start.PerformClick();
            Tests.WaitUntil(() => Puts(calls, before).Count >= 2, "Starting sends the first fade to each light");
            var first = Puts(calls, before);
            Assert(first[0].StartsWith("PUT /api/test-key/lights/1/state") && first[1].StartsWith("PUT /api/test-key/lights/2/state") && first.Take(2).All(call => call.Contains("\"transitiontime\":20")), "First fades go to the selected reachable lights");
            Assert(!Tests.Field<bool>(window, "busy") && panel.Running && start.Text == "Stop ambience", "A running ambience doesn't block the other controls");
            Assert(Tests.Field<Settings>(window, "saved").AmbiencePalette == "Sunset", "Starting remembers the choices");

            var runner = Tests.Field<AmbienceRunner>(window, "ambience");
            Get<GlassButton>(panel, "together").PerformClick();
            Tests.WaitUntil(() => { var now = Tests.Field<AmbienceRunner>(window, "ambience"); return now != null && now != runner && now.Options.Mode == AmbienceMode.Together; }, "Changing the mode restarts the ambience with the new choice");
            runner = Tests.Field<AmbienceRunner>(window, "ambience");
            Assert(runner.LightIds.SequenceEqual(new[] { "1", "2" }), "A restart keeps the same lights");

            Tests.Field<GlassButton>(window, "lightTab").PerformClick();
            Tests.Select(lightList, lightList.Items.Cast<Light>().ToList().FindIndex(light => light.Id == "1"));
            Tests.Field<GlassButton>(window, "onButton").PerformClick(); Tests.WaitIdle(window);
            Assert(Tests.Field<AmbienceRunner>(window, "ambience") == runner && runner.LightIds.SequenceEqual(new[] { "2" }), "Changing a light by hand takes only that light out");
            int afterExclude = Count(calls); Pause(60);
            Assert(Puts(calls, afterExclude).All(call => !call.Contains("/lights/1/state")), "The light changed by hand gets no more ambience commands");

            Tests.Show(window, Tests.DisplayLights());
            Tests.Select(lightList, lightList.Items.Cast<Light>().ToList().FindIndex(light => light.Id == "2"));
            Tests.Field<GlassButton>(window, "offButton").PerformClick(); Tests.WaitIdle(window);
            Assert(Tests.Field<AmbienceRunner>(window, "ambience") == null && !panel.Running, "Changing the last light by hand ends the ambience right away");

            Tests.Field<GlassButton>(window, "ambienceTab").PerformClick();
            Tests.Show(window, Tests.DisplayLights()); Tests.Select(lightList, 0);
            start.PerformClick();
            Tests.WaitUntil(() => Tests.Field<AmbienceRunner>(window, "ambience") != null, "Ambience starts again");
            start.PerformClick();
            Assert(Tests.Field<AmbienceRunner>(window, "ambience") == null && !panel.Running && start.Text == "Start ambience", "Stop ends the ambience");
            Pause(30); int afterStop = Count(calls); Pause(120);
            Assert(Puts(calls, afterStop).Count == 0, "No commands are sent after Stop");

            start.PerformClick();
            Tests.WaitUntil(() => Tests.Field<AmbienceRunner>(window, "ambience") != null, "Ambience starts before All lights off");
            var switchAll = typeof(MainWindow).GetMethod("SwitchAll", Tests.Private);
            Func<Task> allOff = () => (Task)switchAll.Invoke(window, new object[] { false });
            typeof(MainWindow).GetMethod("Run", Tests.Private).Invoke(window, new object[] { allOff });
            Tests.WaitIdle(window);
            Assert(Tests.Field<AmbienceRunner>(window, "ambience") == null, "All lights off stops the ambience");
            Tests.Field<GlassButton>(window, "lightTab").PerformClick();
        }

        static void ClosingStops(List<string> calls)
        {
            var bridge = new Bridge(new Settings { Address = "192.168.1.2", Key = "test-key" }, (method, path, body) => {
                lock (calls) calls.Add(method + " " + path + " " + body);
                return method == "GET" ? "{\"1\":{\"name\":\"Desk\",\"state\":{\"on\":true,\"bri\":127,\"reachable\":true}}}" : "[{\"success\":{}}]";
            });
            var window = new MainWindow(true) { ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-30000, -30000) };
            window.Show(); Application.DoEvents();
            typeof(MainWindow).GetField("bridge", Tests.Private).SetValue(window, bridge);
            Tests.Show(window, Tests.DisplayLights());
            Tests.Field<GlassButton>(window, "ambienceTab").PerformClick();
            Get<GlassButton>(Tests.Field<AmbiencePanel>(window, "ambiencePanel"), "start").PerformClick();
            int before = Count(calls);
            Tests.WaitUntil(() => Puts(calls, before).Count > 0, "Ambience runs in the window being closed");
            window.Close(); window.Dispose();
            Pause(30); int afterClose = Count(calls); Pause(150);
            Assert(Puts(calls, afterClose).Count == 0, "Closing the window stops the ambience");
        }
    }
}
