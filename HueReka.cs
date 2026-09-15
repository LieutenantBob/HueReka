using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace HueReka
{
    static class Json
    {
        public static readonly JavaScriptSerializer Serializer = new JavaScriptSerializer();
        public static Dictionary<string, object> Map(object value) { return (Dictionary<string, object>)value; }
        public static object Parse(string text)
        {
            object result = Serializer.DeserializeObject(text);
            var array = result as object[];
            if (array != null) foreach (object item in array)
            {
                var entry = Map(item);
                if (!entry.ContainsKey("error")) continue;
                var error = Map(entry["error"]);
                int type = Convert.ToInt32(error["type"]);
                if (type == 101) throw new InvalidOperationException("Press the round link button on your Hue Bridge, then click Pair again.");
                if (type == 1) throw new InvalidOperationException("Pairing has expired or was removed. Press the bridge button and click Pair again.");
                throw new InvalidOperationException("Hue Bridge: " + Convert.ToString(error["description"]));
            }
            return result;
        }
    }

    sealed class Settings
    {
        public string Address = "";
        public string Key = "";
        public string Fingerprint = "";
        static string FileName { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HueReka!", "connection.dat"); } }
        public void Save()
        {
            byte[] data = Encoding.UTF8.GetBytes(Json.Serializer.Serialize(this));
            byte[] encrypted = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(FileName));
            string temporary = FileName + ".tmp";
            File.WriteAllBytes(temporary, encrypted);
            if (File.Exists(FileName)) File.Replace(temporary, FileName, null);
            else File.Move(temporary, FileName);
        }
        public static Settings Load()
        {
            string loadPath = FileName;
            if (!File.Exists(loadPath)) loadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Huellywood", "connection.dat");
            if (!File.Exists(loadPath)) return new Settings();
            byte[] data = ProtectedData.Unprotect(File.ReadAllBytes(loadPath), null, DataProtectionScope.CurrentUser);
            return Json.Serializer.Deserialize<Settings>(Encoding.UTF8.GetString(data));
        }
    }

    sealed class Bridge
    {
        public readonly Settings Connection;
        readonly Func<string, string, string, string> transport;
        public Bridge(Settings settings, Func<string, string, string, string> testTransport = null)
        { Connection = settings; transport = testTransport ?? Send; }
        public static string ValidateAddress(string address)
        {
            IPAddress ip;
            if (!IPAddress.TryParse(address.Trim(), out ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("Enter the bridge's IPv4 address, for example 192.168.1.20.");
            return ip.ToString();
        }
        string Send(string method, string path, string body)
        {
            var request = (HttpWebRequest)WebRequest.Create("https://" + ValidateAddress(Connection.Address) + path);
            request.Method = method;
            request.Proxy = null;
            request.AllowAutoRedirect = false;
            request.KeepAlive = false;
            request.ConnectionGroupName = Connection.Address + ":" + Connection.Fingerprint;
            request.Timeout = 7000;
            request.ReadWriteTimeout = 7000;
            // Trust on first pairing, then pin this bridge's certificate on every request.
            // This callback belongs only to this request, never to global HTTPS traffic.
            request.ServerCertificateValidationCallback = (sender, cert, chain, errors) =>
            {
                if (cert == null) return false;
                string fingerprint;
                using (var sha = SHA256.Create()) fingerprint = BitConverter.ToString(sha.ComputeHash(cert.GetRawCertData())).Replace("-", "");
                if (String.IsNullOrEmpty(Connection.Fingerprint)) Connection.Fingerprint = fingerprint;
                return String.Equals(Connection.Fingerprint, fingerprint, StringComparison.Ordinal);
            };
            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                request.ContentType = "application/json";
                request.ContentLength = bytes.Length;
                using (Stream stream = request.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
            }
            using (var response = (HttpWebResponse)request.GetResponse())
            {
                if ((int)response.StatusCode >= 300) throw new InvalidOperationException("Unexpected bridge response.");
                using (var reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
            }
        }
        public Task<object> Request(string method, string path, object body = null)
        {
            string serialized = body == null ? null : Json.Serializer.Serialize(body);
            return Task.Run(() => Json.Parse(transport(method, path, serialized)));
        }
        string Api { get { return "/api/" + Uri.EscapeDataString(Connection.Key); } }
        public async Task Pair()
        {
            object result = await Request("POST", "/api", new { devicetype = "HueReka!#windows" });
            var first = Json.Map(((object[])result)[0]);
            Connection.Key = Convert.ToString(Json.Map(first["success"])["username"]);
            if (String.IsNullOrWhiteSpace(Connection.Key)) throw new InvalidOperationException("The bridge returned no pairing key.");
        }
        public async Task<List<Light>> Lights()
        {
            var result = Json.Map(await Request("GET", Api + "/lights"));
            return result.Select(pair => new Light(pair.Key, Json.Map(pair.Value))).OrderBy(light => light.Name).ToList();
        }
        public Task<object> Set(string id, object state) { return Request("PUT", Api + "/lights/" + Uri.EscapeDataString(id) + "/state", state); }
        public Task<object> All(bool on) { return Request("PUT", Api + "/groups/0/action", new { on = on }); }
    }

    sealed class Light
    {
        public string Id, Name;
        public Dictionary<string, object> State;
        public int MinCt = 153, MaxCt = 500;
        public Light(string id, Dictionary<string, object> value)
        {
            Id = id; Name = Convert.ToString(value["name"]); State = Json.Map(value["state"]);
            if (value.ContainsKey("capabilities"))
            {
                var caps = Json.Map(value["capabilities"]);
                if (caps.ContainsKey("control"))
                {
                    var control = Json.Map(caps["control"]);
                    if (control.ContainsKey("ct"))
                    {
                        var ct = Json.Map(control["ct"]);
                        MinCt = Convert.ToInt32(ct["min"]); MaxCt = Convert.ToInt32(ct["max"]);
                    }
                }
            }
            if (MinCt <= 0 || MaxCt < MinCt) { MinCt = 153; MaxCt = 500; }
        }
        public bool Reachable { get { return !State.ContainsKey("reachable") || Convert.ToBoolean(State["reachable"]); } }
        public override string ToString() { return Name + (Reachable ? (Convert.ToBoolean(State["on"]) ? "   /   On" : "   /   Off") : "   /   Unreachable"); }
    }

    sealed class MainWindow : Form
    {
        readonly ComboBox address = new ComboBox { Width = 230, DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10), AccessibleName = "Bridge IP address" };
        readonly LightList lights = new LightList();
        readonly Label status = new Label { AutoSize = false, ForeColor = Glass.Muted, BackColor = Color.Transparent, Font = new Font("Segoe UI", 9) };
        readonly Label selected = new Label { Text = "Find your glow", AutoSize = false, AutoEllipsis = true, Font = new Font("Segoe UI", 22, FontStyle.Bold), BackColor = Color.Transparent, ForeColor = Glass.Ink };
        readonly GlassSlider brightness = new GlassSlider();
        readonly Label percentage = new Label { Text = "100%", AutoSize = false, Font = new Font("Segoe UI", 24, FontStyle.Bold), BackColor = Color.Transparent, ForeColor = Glass.Ink };
        readonly GlassCanvas root = new GlassCanvas { Dock = DockStyle.Fill };
        readonly GlassPanel controls = new GlassPanel();
        readonly GlassPanel sidebar = new GlassPanel();
        readonly GlassButton apply, color, warm, cool, onButton, offButton;
        readonly Panel allRow = new Panel { BackColor = Color.Transparent };
        readonly LightOrb orb = new LightOrb();
        readonly Label lightDetail = new Label { AutoSize = false, BackColor = Color.Transparent, ForeColor = Glass.Muted, AutoEllipsis = true };
        readonly Label lightCount = new Label { AutoSize = false, BackColor = Color.Transparent, ForeColor = Glass.Muted };
        readonly Label connectionBadge = new Label { AutoSize = false, BackColor = Color.Transparent, ForeColor = Glass.Muted, TextAlign = ContentAlignment.MiddleRight };
        readonly Label empty = new Label { Text = "Your lights will appear here.\n\nConnect to your bridge below\nto get started.", BackColor = Color.FromArgb(239, 240, 249), ForeColor = Glass.Muted, TextAlign = ContentAlignment.MiddleCenter };
        readonly Label colorHint = new Label { AutoSize = false, BackColor = Color.Transparent, ForeColor = Glass.Muted, Font = new Font("Segoe UI", 9) };
        readonly System.Collections.Generic.List<GlassButton> swatches = new System.Collections.Generic.List<GlassButton>();
        Bridge bridge;
        Settings saved;
        bool busy;
        public MainWindow(bool preview = false)
        {
            Text = "HueReka!"; ClientSize = new Size(1120, 820); MinimumSize = new Size(1080, 860);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10);
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(239, 237, 249); ForeColor = Glass.Ink;
            Controls.Add(root);
            var logo = new LightOrb { Bounds = new Rectangle(22, 12, 64, 64) }; root.Controls.Add(logo);
            AddLabel(root, "HueReka!", 85, 27, 220, 35, 18, true);
            connectionBadge.SetBounds(780, 25, 300, 35); connectionBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right; root.Controls.Add(connectionBadge);
            AddLabel(root, "Set the mood.", 32, 99, 760, 54, 32, true);
            AddLabel(root, "A little light. A whole different feeling.", 35, 158, 570, 28, 11, false).ForeColor = Glass.Muted;
            allRow.SetBounds(743, 125, 346, 48); allRow.Anchor = AnchorStyles.Top | AnchorStyles.Right; root.Controls.Add(allRow);
            var refresh = Button("Refresh", async () => await RefreshLights()); refresh.SetBounds(0, 0, 94, 42); allRow.Controls.Add(refresh);
            var allOn = Button("All lights on", async () => { await bridge.All(true); await RefreshLights(); }); allOn.SetBounds(100, 0, 117, 42); allRow.Controls.Add(allOn);
            var allOff = Button("All lights off", async () => { await bridge.All(false); await RefreshLights(); }); allOff.SetBounds(223, 0, 117, 42); allRow.Controls.Add(allOff);
            sidebar.SetBounds(28, 209, 292, 530); sidebar.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left; root.Controls.Add(sidebar);
            AddLabel(sidebar, "Your lights", 23, 23, 215, 29, 15, true);
            lightCount.SetBounds(25, 60, 240, 24); sidebar.Controls.Add(lightCount);
            lights.SetBounds(19, 97, 252, 216); lights.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; sidebar.Controls.Add(lights);
            empty.Bounds = lights.Bounds; empty.Anchor = lights.Anchor; sidebar.Controls.Add(empty);
            var bridgeArea = new Panel { BackColor = Color.Transparent, Bounds = new Rectangle(23, 329, 246, 181), Anchor = AnchorStyles.Bottom | AnchorStyles.Left };
            sidebar.Controls.Add(bridgeArea);
            AddLabel(bridgeArea, "HUE BRIDGE", 2, 0, 235, 24, 8.5f, true).ForeColor = Glass.Muted;
            address.SetBounds(3, 28, 236, 30); bridgeArea.Controls.Add(address);
            var find = Button("Find bridge", async () => await Discover()); find.SetBounds(0, 66, 115, 39); bridgeArea.Controls.Add(find);
            var connect = Button("Connect", async () => await Connect(false)); connect.Primary = true; connect.SetBounds(121, 66, 120, 39); bridgeArea.Controls.Add(connect);
            var pair = Button("Pair", async () => await Connect(true)); pair.SetBounds(0, 114, 74, 38); bridgeArea.Controls.Add(pair);
            AddLabel(bridgeArea, "First time? Press the bridge\nbutton, then Pair.", 84, 114, 159, 47, 8.5f, false).ForeColor = Glass.Muted;
            controls.SetBounds(336, 209, 756, 530); controls.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; root.Controls.Add(controls);
            selected.SetBounds(31, 28, 458, 49); selected.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; controls.Controls.Add(selected);
            lightDetail.SetBounds(34, 81, 450, 26); lightDetail.Anchor = selected.Anchor; controls.Controls.Add(lightDetail);
            onButton = Button("On", async () => await Change(new { on = true })); onButton.SetBounds(559, 34, 78, 43); onButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; controls.Controls.Add(onButton);
            offButton = Button("Off", async () => await Change(new { on = false })); offButton.SetBounds(642, 34, 78, 43); offButton.Anchor = onButton.Anchor; controls.Controls.Add(offButton);
            orb.SetBounds(43, 125, 267, 236); controls.Controls.Add(orb);
            var orbCaption = AddLabel(controls, "A little atmosphere, on demand.", 29, 361, 300, 24, 9, false); orbCaption.TextAlign = ContentAlignment.MiddleCenter; orbCaption.ForeColor = Glass.Muted;
            AddLabel(controls, "BRIGHTNESS", 365, 154, 170, 25, 9, true).ForeColor = Glass.Muted;
            percentage.SetBounds(363, 188, 300, 49); controls.Controls.Add(percentage);
            brightness.SetBounds(357, 246, 352, 42); brightness.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; controls.Controls.Add(brightness);
            apply = Button("Apply brightness", async () => await Change(new { on = true, bri = Brightness(brightness.Value) })); apply.Primary = true; apply.SetBounds(363, 302, 175, 43); controls.Controls.Add(apply);
            AddLabel(controls, "COLOR & WARMTH", 34, 405, 300, 22, 9, true).ForeColor = Glass.Muted;
            colorHint.SetBounds(34, 486, 660, 27); colorHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; controls.Controls.Add(colorHint);
            Color[] colors = { Color.FromArgb(255, 179, 94), Color.FromArgb(246, 135, 170), Color.FromArgb(178, 145, 237), Color.FromArgb(122, 179, 238), Color.FromArgb(120, 207, 180) };
            string[] names = { "Amber", "Rose", "Lavender", "Sky", "Mint" };
            for (int i = 0; i < colors.Length; i++)
            {
                Color tint = colors[i];
                var swatch = Button(names[i], async () => await SetColor(tint)); swatch.Swatch = tint; swatch.AccessibleName = names[i];
                swatch.SetBounds(31 + i * 46, 439, 39, 39); controls.Controls.Add(swatch); swatches.Add(swatch);
            }
            color = Button("Custom color", async () => { using (var dialog = new ColorDialog { FullOpen = true }) if (dialog.ShowDialog(this) == DialogResult.OK) await SetColor(dialog.Color); });
            color.SetBounds(274, 437, 138, 43); controls.Controls.Add(color);
            warm = Button("Warm", async () => await Change(new { on = true, ct = Current.MaxCt })); warm.SetBounds(434, 437, 111, 43); controls.Controls.Add(warm);
            cool = Button("Cool", async () => await Change(new { on = true, ct = Current.MinCt })); cool.SetBounds(551, 437, 111, 43); controls.Controls.Add(cool);
            status.SetBounds(35, 757, 1047, 49); status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; root.Controls.Add(status);
            lights.SelectedIndexChanged += (sender, args) => UpdateSelection();
            brightness.ValueChanged += (sender, args) => percentage.Text = brightness.Value + "%";
            address.TextChanged += (sender, args) => { bridge = null; lights.Items.Clear(); allRow.Enabled = false; UpdateSelection(); };
            FormClosing += (sender, args) => { if (busy) { args.Cancel = true; status.Text = "Finishing the current request. Please close again in a moment."; } };
            try { saved = preview ? new Settings() : Settings.Load(); address.Text = saved.Address; status.Text = "Ready when you are. Connect to your bridge, or pair a new one."; }
            catch { saved = new Settings(); status.Text = "Saved connection could not be read. Find your bridge and pair again."; }
            allRow.Enabled = false; UpdateSelection();
        }
        Label AddLabel(Control parent, string text, int x, int y, int width, int height, float size, bool bold)
        {
            var label = new Label { Text = text, UseMnemonic = false, Bounds = new Rectangle(x, y, width, height), BackColor = Color.Transparent, ForeColor = Glass.Ink, AutoEllipsis = true, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
            parent.Controls.Add(label); return label;
        }
        Task SetColor(Color tint)
        {
            double max = Math.Max(tint.R, Math.Max(tint.G, tint.B));
            double min = Math.Min(tint.R, Math.Min(tint.G, tint.B));
            return Change(new { on = true, hue = (int)Math.Round(tint.GetHue() / 360.0 * 65535), sat = max == 0 ? 0 : (int)Math.Round((max - min) / max * 254) });
        }
        public static int Brightness(int percent) { return Math.Max(1, Math.Min(254, (int)Math.Round(percent * 254.0 / 100))); }
        Light Current { get { return lights.SelectedItem as Light; } }
        GlassButton Button(string text, Func<Task> action)
        {
            var button = new GlassButton { Text = text, AccessibleName = text };
            button.Click += async (sender, args) => await Run(action);
            return button;
        }
        async Task Run(Func<Task> action)
        {
            if (busy) return;
            busy = true; root.Enabled = false; UseWaitCursor = true; status.Text = "Working...";
            try { await action(); }
            catch (WebException) { status.Text = "Cannot reach or trust the bridge. Check its IP address and your network. If its certificate changed, press its link button and Pair again."; }
            catch (Exception error) { status.Text = error.Message; }
            finally { busy = false; root.Enabled = true; UseWaitCursor = false; UpdateSelection(); }
        }
        async Task Discover()
        {
            string json = await Task.Run(() => {
                var request = (HttpWebRequest)WebRequest.Create("https://discovery.meethue.com/");
                request.Timeout = 7000; request.ReadWriteTimeout = 7000;
                using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
            });
            var found = ((object[])Json.Parse(json)).Select(value => Bridge.ValidateAddress(Convert.ToString(Json.Map(value)["internalipaddress"]))).Distinct().ToArray();
            address.Items.Clear(); address.Items.AddRange(found);
            if (found.Length > 0) address.SelectedIndex = 0;
            status.Text = found.Length == 0 ? "No bridges found. Enter the IP address from the Hue app's bridge settings." : "Bridge found. Select an address, then Connect or Pair.";
        }
        async Task Connect(bool pair)
        {
            string ip = Bridge.ValidateAddress(address.Text);
            bridge = null; lights.Items.Clear(); allRow.Enabled = false;
            var connection = pair ? new Settings { Address = ip } : saved;
            if (!pair && (saved.Address != ip || String.IsNullOrEmpty(saved.Key))) throw new InvalidOperationException("Press the bridge's link button, then click Pair to connect for the first time.");
            var candidate = new Bridge(connection);
            if (pair) await candidate.Pair();
            var result = await candidate.Lights();
            bridge = candidate; saved = connection;
            ShowLights(result);
            saved.Save();
            status.Text = "Connected to " + ip + ". " + result.Count + " light(s).";
        }
        void ShowLights(List<Light> result)
        {
            string id = Current == null ? null : Current.Id;
            lights.BeginUpdate(); lights.Items.Clear(); lights.Items.AddRange(result.ToArray()); lights.EndUpdate();
            if (result.Count > 0) lights.SelectedIndex = Math.Max(0, result.FindIndex(light => light.Id == id));
            allRow.Enabled = bridge != null; UpdateSelection();
        }
        async Task RefreshLights()
        {
            ShowLights(await bridge.Lights()); status.Text = lights.Items.Count + " light(s). Updated " + DateTime.Now.ToShortTimeString() + ".";
        }
        async Task Change(object state)
        {
            if (Current == null) return;
            await bridge.Set(Current.Id, state); await RefreshLights();
        }
        void UpdateSelection()
        {
            var light = Current;
            bool available = light != null && light.Reachable && bridge != null;
            onButton.Enabled = offButton.Enabled = available;
            brightness.Enabled = apply.Enabled = available && light.State.ContainsKey("bri");
            color.Enabled = available && light.State.ContainsKey("hue") && light.State.ContainsKey("sat");
            foreach (var swatch in swatches) swatch.Enabled = color.Enabled;
            warm.Enabled = cool.Enabled = available && light.State.ContainsKey("ct");
            empty.Visible = lights.Items.Count == 0;
            lightCount.Text = lights.Items.Count == 0 ? "A home full of possibilities" : lights.Items.Count + " lights in your home";
            connectionBadge.Text = bridge == null ? "Not connected  /  Local control" : "Connected  /  " + bridge.Connection.Address;
            connectionBadge.ForeColor = bridge == null ? Glass.Muted : Color.FromArgb(49, 123, 106);
            selected.Text = light == null ? "Find your glow" : light.Name;
            lightDetail.Text = light == null ? "Connect a bridge and choose a light to begin." : !light.Reachable ? "Unreachable / check the physical power switch" : "Light " + light.Id + "  /  " + (Convert.ToBoolean(light.State["on"]) ? "On" : "Off");
            colorHint.Text = light == null ? "Color and warmth controls appear for compatible lights." : color.Enabled ? "Choose a color, or dial in a warmer shade of white." : "Color controls require a color-capable light.";
            if (light == null) { percentage.Text = "--"; orb.Lit = true; orb.Tint = Color.FromArgb(188, 157, 240); orb.Invalidate(); return; }
            bool on = Convert.ToBoolean(light.State["on"]);
            onButton.Primary = on; offButton.Primary = !on; onButton.Invalidate(); offButton.Invalidate();
            if (light.State.ContainsKey("bri"))
            {
                brightness.Value = Math.Max(1, Math.Min(100, (int)Math.Round(Convert.ToInt32(light.State["bri"]) * 100.0 / 254)));
                percentage.Text = brightness.Value + "%";
            }
            else percentage.Text = "--";
            orb.Lit = on && light.Reachable;
            orb.Tint = Color.FromArgb(246, 199, 136);
            if (light.State.ContainsKey("hue") && light.State.ContainsKey("sat") && (!light.State.ContainsKey("colormode") || Convert.ToString(light.State["colormode"]) != "ct"))
            {
                double h = Convert.ToDouble(light.State["hue"]) / 65535 * 6, s = Convert.ToDouble(light.State["sat"]) / 254;
                double c = s, x = c * (1 - Math.Abs(h % 2 - 1)), m = 1 - c;
                double r = 0, g = 0, b = 0;
                if (h < 1) { r = c; g = x; } else if (h < 2) { r = x; g = c; } else if (h < 3) { g = c; b = x; }
                else if (h < 4) { g = x; b = c; } else if (h < 5) { r = x; b = c; } else { r = c; b = x; }
                orb.Tint = Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
            }
            orb.Invalidate();
        }
    }
    static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            if (args.Contains("--test"))
            {
                try { Tests.Run(); return 0; } catch (Exception error) { File.WriteAllText("test-failure.txt", error.ToString()); return 1; }
            }
            Application.Run(new MainWindow()); return 0;
        }
    }

    static class Tests
    {
        static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
        public static void Run()
        {
            Assert(MainWindow.Brightness(1) == 3 && MainWindow.Brightness(100) == 254, "Brightness mapping");
            try { Bridge.ValidateAddress("192.168.1.2/api/other"); throw new Exception("Address accepted path"); } catch (InvalidOperationException) { }
            try { Json.Parse("[{\"error\":{\"type\":101,\"description\":\"link button not pressed\"}}]"); throw new Exception("Pairing error ignored"); }
            catch (InvalidOperationException error) { Assert(error.Message.Contains("round link button"), "Pairing guidance"); }
            try { Json.Parse("[{\"success\":{}},{\"error\":{\"type\":7,\"description\":\"invalid value\"}}]"); throw new Exception("Partial error ignored"); }
            catch (InvalidOperationException error) { Assert(error.Message.Contains("invalid value"), "Partial command failure"); }
            var calls = new List<string>();
            var bridge = new Bridge(new Settings { Address = "192.168.1.2" }, (method, path, body) => {
                calls.Add(method + " " + path);
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
            Assert(calls.SequenceEqual(new[] { "POST /api", "GET /api/test-key/lights", "PUT /api/test-key/lights/1/state", "PUT /api/test-key/groups/0/action" }), "API routes");
            byte[] plain = Encoding.UTF8.GetBytes("pairing-secret");
            Assert(ProtectedData.Unprotect(ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser), null, DataProtectionScope.CurrentUser).SequenceEqual(plain), "Credential encryption");
            using (var window = new MainWindow(true))
            {
                window.ShowInTaskbar = false; window.StartPosition = FormStartPosition.Manual;
                window.Location = new Point(-30000, -30000); window.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save("preview.png"); }
                var binding = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var displayLights = new List<Light> {
                    new Light("1", Json.Map(Json.Parse("{\"name\":\"Living room\",\"state\":{\"on\":true,\"bri\":183,\"hue\":7600,\"sat\":135,\"ct\":300,\"reachable\":true}}"))),
                    new Light("2", Json.Map(Json.Parse("{\"name\":\"Desk lamp\",\"state\":{\"on\":true,\"bri\":127,\"reachable\":true}}"))),
                    new Light("3", Json.Map(Json.Parse("{\"name\":\"Bedroom\",\"state\":{\"on\":false,\"bri\":80,\"reachable\":true}}"))),
                    new Light("4", Json.Map(Json.Parse("{\"name\":\"Hallway\",\"state\":{\"on\":false,\"reachable\":false}}")))
                };
                typeof(MainWindow).GetField("bridge", binding).SetValue(window, bridge);
                typeof(MainWindow).GetMethod("ShowLights", binding).Invoke(window, new object[] { displayLights });
                ((Label)typeof(MainWindow).GetField("status", binding).GetValue(window)).Text = "Preview with simulated lights. Your actual lights appear after connecting.";
                Application.DoEvents();
                var colorButton = (GlassButton)typeof(MainWindow).GetField("color", binding).GetValue(window);
                Assert(colorButton.Enabled, "Color bulb enables color controls");
                using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save("preview-connected.png"); }
                window.Size = window.MinimumSize; Application.DoEvents();
                using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save("preview-small.png"); }
                var lightList = (LightList)typeof(MainWindow).GetField("lights", binding).GetValue(window);
                lightList.SelectedIndex = 1; Assert(!colorButton.Enabled, "White bulb disables color controls");
                lightList.SelectedIndex = 3;
                var on = (GlassButton)typeof(MainWindow).GetField("onButton", binding).GetValue(window);
                Assert(!on.Enabled, "Unreachable bulb disables power");
                lightList.SelectedIndex = 0;
                var off = (GlassButton)typeof(MainWindow).GetField("offButton", binding).GetValue(window);
                int beforeClick = calls.Count;
                off.PerformClick();
                var deadline = DateTime.UtcNow.AddSeconds(5);
                while ((bool)typeof(MainWindow).GetField("busy", binding).GetValue(window) && DateTime.UtcNow < deadline) { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
                Assert(calls.Skip(beforeClick).Contains("PUT /api/test-key/lights/1/state"), "Redesigned power control sends bridge request");
                Assert(!(bool)typeof(MainWindow).GetField("busy", binding).GetValue(window), "UI recovers after request");
                window.Close();
            }
        }
    }
}
