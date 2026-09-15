using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
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
            WindowTests(bridge, calls);
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
                window.Close();
            }
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
