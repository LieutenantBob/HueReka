using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HueReka
{
    static class Tests
    {
        const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
        const string NotPressed = "[{\"error\":{\"type\":101,\"description\":\"link button not pressed\"}}]";

        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        static T Field<T>(MainWindow window, string name) { return (T)typeof(MainWindow).GetField(name, Private).GetValue(window); }

        public static void Run()
        {
            Assert(MainWindow.Brightness(1) == 3 && MainWindow.Brightness(100) == 254, "Brightness mapping");
            try { Bridge.ValidateAddress("192.168.1.2/api/other"); throw new Exception("Address accepted path"); } catch (InvalidOperationException) { }
            try { Json.Parse(NotPressed); throw new Exception("Pairing error ignored"); }
            catch (LinkButtonException error) { Assert(error.Message.Contains("round link button"), "Pairing guidance"); }
            try { Json.Parse("[{\"success\":{}},{\"error\":{\"type\":7,\"description\":\"invalid value\"}}]"); throw new Exception("Partial error ignored"); }
            catch (InvalidOperationException error) { Assert(error.Message.Contains("invalid value"), "Partial command failure"); }
            PairingTests();
            var calls = new List<string>();
            var bridge = new Bridge(new Settings { Address = "192.168.1.2" }, (method, path, body) => {
                calls.Add(method + " " + path + " " + body);
                if (path == "/api") return "[{\"success\":{\"username\":\"test-key\"}}]";
                if (method == "GET") return "{\"1\":{\"name\":\"Desk\",\"state\":{\"on\":true,\"bri\":127,\"reachable\":true}},\"2\":{\"name\":\"Plug\",\"state\":{\"on\":false,\"reachable\":false}}}";
                Assert(Json.Map(Json.Parse(body)).ContainsKey("on"), "Command body");
                return "[{\"success\":{\"/lights/1/state/on\":true}}]";
            });
            bridge.Pair().GetAwaiter().GetResult();
            var lights = bridge.Lights().GetAwaiter().GetResult();
            Assert(lights.Count == 2 && lights[0].Name == "Desk" && !lights[1].Reachable, "Light parsing");
            bridge.Set("1", new { on = true, bri = 127 }).GetAwaiter().GetResult();
            bridge.All(false).GetAwaiter().GetResult();
            Assert(calls.Select(call => call.Substring(0, call.LastIndexOf(' '))).SequenceEqual(new[] { "POST /api", "GET /api/test-key/lights", "PUT /api/test-key/lights/1/state", "PUT /api/test-key/groups/0/action" }), "API routes");
            byte[] plain = Encoding.UTF8.GetBytes("pairing-secret");
            Assert(ProtectedData.Unprotect(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser), null, DataProtectionScope.CurrentUser).SequenceEqual(plain), "Credential encryption");
            InsideMessageLoop(() => WindowTests(bridge, calls));
        }

        // Window tests must run inside Application.Run like the real app. Otherwise there is no WinForms
        // synchronization context, code after each await resumes on a thread-pool thread, and UI bugs hide.
        static void InsideMessageLoop(Action tests)
        {
            Exception failure = null;
            var loop = new ApplicationContext();
            using (var starter = new System.Windows.Forms.Timer { Interval = 1 })
            {
                starter.Tick += (sender, args) =>
                {
                    starter.Stop();
                    try
                    {
                        Assert(SynchronizationContext.Current is WindowsFormsSynchronizationContext, "UI synchronization context is installed");
                        tests();
                    }
                    catch (Exception error) { failure = error; }
                    finally { loop.ExitThread(); }
                };
                starter.Start();
                Application.Run(loop);
            }
            if (failure != null) throw new Exception("Window tests failed", failure);
        }

        static void PairingTests()
        {
            Bridge.PairPollInterval = TimeSpan.FromMilliseconds(5);
            int attempts = 0, waits = 0;
            var late = new Bridge(new Settings { Address = "192.168.1.3" }, (method, path, body) => ++attempts < 3 ? NotPressed : "[{\"success\":{\"username\":\"late-key\"}}]");
            late.PairWhenReady(TimeSpan.FromSeconds(5), secondsLeft => waits++, CancellationToken.None).GetAwaiter().GetResult();
            Assert(late.Connection.Key == "late-key" && attempts == 3 && waits == 2, "Pairing waits for the link button instead of failing");
            var never = new Bridge(new Settings { Address = "192.168.1.3" }, (method, path, body) => NotPressed);
            try { never.PairWhenReady(TimeSpan.FromMilliseconds(40), null, CancellationToken.None).GetAwaiter().GetResult(); throw new Exception("Pairing never timed out"); }
            catch (TimeoutException) { }
            using (var cancel = new CancellationTokenSource())
            {
                var cancelled = new Bridge(new Settings { Address = "192.168.1.3" }, (method, path, body) => { cancel.Cancel(); return NotPressed; });
                try { cancelled.PairWhenReady(TimeSpan.FromSeconds(5), null, cancel.Token).GetAwaiter().GetResult(); throw new Exception("Pairing ignored cancel"); }
                catch (OperationCanceledException) { }
            }
        }

        static List<Light> DisplayLights()
        {
            return new List<Light> {
                new Light("1", Json.Map(Json.Parse("{\"name\":\"Living room\",\"state\":{\"on\":true,\"bri\":183,\"hue\":7600,\"sat\":135,\"ct\":300,\"reachable\":true}}"))),
                new Light("2", Json.Map(Json.Parse("{\"name\":\"Desk lamp\",\"state\":{\"on\":true,\"bri\":127,\"reachable\":true}}"))),
                new Light("3", Json.Map(Json.Parse("{\"name\":\"Bedroom\",\"state\":{\"on\":false,\"bri\":80,\"reachable\":true}}"))),
                new Light("4", Json.Map(Json.Parse("{\"name\":\"Hallway\",\"state\":{\"on\":false,\"reachable\":false}}")))
            };
        }

        static void Select(ListBox list, params int[] indexes)
        {
            list.ClearSelected();
            foreach (int index in indexes) list.SetSelected(index, true);
        }

        static void WaitIdle(MainWindow window)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Field<bool>(window, "busy") && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(10); }
            Assert(!Field<bool>(window, "busy"), "UI recovers after request");
        }

        static void Show(MainWindow window, List<Light> lights) { typeof(MainWindow).GetMethod("ShowLights", Private).Invoke(window, new object[] { lights }); Application.DoEvents(); }

        static void WindowTests(Bridge bridge, List<string> calls)
        {
            using (var window = new MainWindow(true))
            {
                window.ShowInTaskbar = false; window.StartPosition = FormStartPosition.Manual;
                window.Location = new Point(-30000, -30000); window.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save("preview.png"); }
                typeof(MainWindow).GetField("bridge", Private).SetValue(window, bridge);
                Show(window, DisplayLights());
                Field<Label>(window, "status").Text = "Preview with simulated lights. Your actual lights appear after connecting.";
                var colorButton = Field<GlassButton>(window, "color");
                var lightList = Field<LightList>(window, "lights");
                Assert(lightList.SelectedIndices.Count == 1 && colorButton.Enabled, "First light is selected and a color bulb enables color controls");
                using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save("preview-connected.png"); }
                window.Size = window.MinimumSize; Application.DoEvents();
                var toolbar = Field<Panel>(window, "allRow");
                foreach (Control sibling in toolbar.Parent.Controls)
                    if (sibling is Label) Assert(!sibling.Bounds.IntersectsWith(toolbar.Bounds), "Header labels must not cover toolbar buttons at minimum size");
                using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save("preview-small.png"); }
                Select(lightList, 1); Assert(!colorButton.Enabled, "White bulb disables color controls");
                Select(lightList, 3);
                var on = Field<GlassButton>(window, "onButton");
                Assert(!on.Enabled, "Unreachable bulb disables power");
                Select(lightList, 0);
                var off = Field<GlassButton>(window, "offButton");
                int beforeClick = calls.Count;
                off.PerformClick(); WaitIdle(window);
                Assert(calls.Skip(beforeClick).Any(call => call.StartsWith("PUT /api/test-key/lights/1/state")), "Power control sends bridge request");
                Assert(lightList.SelectedItems.Cast<Light>().Single().Id == "1", "Selection survives a refresh");
                int keyboardCalls = calls.Count;
                typeof(GlassButton).GetMethod("OnKeyDown", Private).Invoke(off, new object[] { new KeyEventArgs(Keys.Space) });
                typeof(GlassButton).GetMethod("OnKeyUp", Private).Invoke(off, new object[] { new KeyEventArgs(Keys.Space) });
                WaitIdle(window);
                Assert(calls.Skip(keyboardCalls).Any(call => call.StartsWith("PUT /api/test-key/lights/1/state")), "Space activates the custom button");
                MultiSelectTests(window, calls, lightList, on);
                PairingOverlayTests(window);
                window.Close();
            }
        }

        static void WaitUntil(Func<bool> condition, string message)
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!condition() && DateTime.UtcNow < deadline) { Application.DoEvents(); Thread.Sleep(5); }
            Assert(condition(), message);
        }

        // Captures the overlay itself: Form.DrawToBitmap paints sibling controls in reverse z-order,
        // so a whole-window capture would show the app on top of the overlay.
        static void Screenshot(Control control, string file)
        {
            using (var bitmap = new Bitmap(control.Width, control.Height)) { control.DrawToBitmap(bitmap, new Rectangle(Point.Empty, control.Size)); bitmap.Save(file); }
        }

        static void StartPairing(MainWindow window)
        {
            var connectOrPair = typeof(MainWindow).GetMethod("ConnectOrPair", Private);
            Func<Task> action = () => (Task)connectOrPair.Invoke(window, new object[] { true });
            typeof(MainWindow).GetMethod("Run", Private).Invoke(window, new object[] { action });
        }

        static void PairingOverlayTests(MainWindow window)
        {
            var overlay = Field<PairingOverlay>(window, "pairingOverlay");
            var root = Field<GlassCanvas>(window, "root");
            Func<string, GlassButton> overlayButton = name => (GlassButton)typeof(PairingOverlay).GetField(name, Private).GetValue(overlay);
            Func<PairingOverlay.Stage, bool> showing = stage => overlay.Visible && overlay.Current == stage;
            window.Size = new Size(1136, 860);
            Field<ComboBox>(window, "address").Text = "192.168.1.9";
            MainWindow.PairedCelebration = TimeSpan.FromMilliseconds(50);
            bool pressed = false;
            window.CreateBridge = settings => new Bridge(settings, (method, path, body) => {
                if (method != "POST") return "{\"7\":{\"name\":\"Porch\",\"state\":{\"on\":true,\"bri\":200,\"reachable\":true}}}";
                if (pressed) return "[{\"success\":{\"username\":\"paired-key\"}}]";
                Thread.Sleep(20); return NotPressed;
            });

            StartPairing(window);
            WaitUntil(() => showing(PairingOverlay.Stage.Waiting), "Pairing shows the full-window overlay");
            Assert(!root.Enabled, "The app is blocked behind the pairing overlay");
            var settle = DateTime.UtcNow.AddMilliseconds(400);
            while (DateTime.UtcNow < settle) { Application.DoEvents(); Thread.Sleep(10); }
            Screenshot(overlay, "preview-pairing.png");
            pressed = true;
            WaitIdle(window);
            Assert(!overlay.Visible && root.Enabled, "Overlay closes once the bridge pairs");
            Assert(Field<LightList>(window, "lights").Items.Cast<Light>().Any(light => light.Name == "Porch"), "Paired bridge's lights are shown");

            pressed = false;
            MainWindow.PairTimeout = TimeSpan.FromMilliseconds(400);
            StartPairing(window);
            WaitUntil(() => showing(PairingOverlay.Stage.TimedOut), "Running out of time offers to try again");
            Screenshot(overlay, "preview-pairing-timeout.png");
            var retry = overlayButton("retry");
            Assert(retry.Visible && !overlayButton("cancel").Visible, "Timed-out card shows Try again instead of Cancel");
            retry.PerformClick();
            WaitUntil(() => showing(PairingOverlay.Stage.Waiting), "Try again waits for the button again");
            WaitUntil(() => showing(PairingOverlay.Stage.TimedOut), "Second attempt times out too");
            overlayButton("notNow").PerformClick();
            WaitIdle(window);
            Assert(!overlay.Visible && root.Enabled && Field<Label>(window, "status").Text.Contains("whenever you're ready"), "Not now closes the overlay");

            MainWindow.PairTimeout = TimeSpan.FromSeconds(30);
            StartPairing(window);
            WaitUntil(() => showing(PairingOverlay.Stage.Waiting), "Overlay shows for a new attempt");
            typeof(MainWindow).GetMethod("OnKeyDown", Private).Invoke(window, new object[] { new KeyEventArgs(Keys.Escape) });
            WaitIdle(window);
            Assert(!overlay.Visible && root.Enabled && Field<Label>(window, "status").Text.StartsWith("Cancelled"), "Escape cancels pairing and unblocks the app");
        }

        static void MultiSelectTests(MainWindow window, List<string> calls, LightList lightList, GlassButton on)
        {
            Show(window, DisplayLights());
            Select(lightList, 0, 1, 3);
            Assert(Field<Label>(window, "selected").Text == "3 lights selected" && on.Enabled, "Multi-selection heading and controls");
            Assert(Field<GlassButton>(window, "color").Enabled, "Color stays available when any selected light supports it");
            int before = calls.Count;
            on.PerformClick(); WaitIdle(window);
            var writes = calls.Skip(before).Where(call => call.StartsWith("PUT")).ToList();
            Assert(writes.Count == 2 && writes[0].StartsWith("PUT /api/test-key/lights/1/state") && writes[1].StartsWith("PUT /api/test-key/lights/2/state"), "On applies to every reachable selected light, skipping unreachable ones");

            Show(window, DisplayLights());
            Select(lightList, 0, 1);
            var slider = Field<GlassSlider>(window, "brightness");
            before = calls.Count;
            slider.Value = 40; slider.Commit(); WaitIdle(window);
            writes = calls.Skip(before).Where(call => call.StartsWith("PUT")).ToList();
            Assert(writes.Count == 2 && writes.All(call => call.Contains("\"bri\":102")), "Releasing the slider applies brightness to all selected lights");

            Show(window, DisplayLights());
            Select(lightList, 0, 1);
            before = calls.Count;
            typeof(ListBox).GetMethod("OnDoubleClick", Private).Invoke(lightList, new object[] { EventArgs.Empty });
            WaitIdle(window);
            writes = calls.Skip(before).Where(call => call.StartsWith("PUT")).ToList();
            Assert(writes.Count == 2 && writes.All(call => call.Contains("{\"on\":false}")), "Toggling switches selected lights off when any are on");

            Select(lightList, 0);
            typeof(MainWindow).GetMethod("SelectAll", Private).Invoke(window, null);
            Assert(lightList.SelectedIndices.Count == lightList.Items.Count, "Ctrl+A selects every light");
        }
    }
}
