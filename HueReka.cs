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
        readonly ComboBox address = new ComboBox { Width = 185, DropDownStyle = ComboBoxStyle.DropDown };
        readonly ListBox lights = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, IntegralHeight = false };
        readonly Label status = new Label { Dock = DockStyle.Bottom, Height = 65, Padding = new Padding(20, 12, 20, 8) };
        readonly Label selected = new Label { Text = "Choose a light", AutoSize = true, Font = new Font("Segoe UI", 17, FontStyle.Bold) };
        readonly TrackBar brightness = new TrackBar { Minimum = 1, Maximum = 100, Value = 100, Width = 255, TickStyle = TickStyle.None };
        readonly Label percentage = new Label { Text = "100%", AutoSize = true };
        readonly FlowLayoutPanel root = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(20), AutoScroll = true };
        readonly FlowLayoutPanel controls = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, Dock = DockStyle.Fill, Padding = new Padding(20), AutoScroll = true };
        readonly Button apply, color, warm, cool;
        readonly FlowLayoutPanel allRow = new FlowLayoutPanel { Width = 750, Height = 44 };
        Bridge bridge;
        Settings saved;
        bool busy;
        public MainWindow()
        {
            Text = "HueReka!"; ClientSize = new Size(850, 570); MinimumSize = new Size(850, 610);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10);
            BackColor = Color.FromArgb(245, 246, 250); ForeColor = Color.FromArgb(34, 38, 52);
            AutoScaleMode = AutoScaleMode.Dpi;
            root.Controls.Add(new Label { Text = "HueReka!", AutoSize = true, Font = new Font("Segoe UI", 25, FontStyle.Bold) });
            root.Controls.Add(new Label { Text = "Your lights. From your PC.", AutoSize = true, Margin = new Padding(3, 0, 3, 18) });
            var connectionRow = new FlowLayoutPanel { Width = 780, Height = 44 };
            connectionRow.Controls.Add(address);
            connectionRow.Controls.Add(Button("Find bridge", async () => await Discover()));
            connectionRow.Controls.Add(Button("Connect", async () => await Connect(false)));
            connectionRow.Controls.Add(Button("Pair", async () => await Connect(true)));
            root.Controls.Add(connectionRow);
            root.Controls.Add(new Label { Text = "First time? Find your bridge, press its round link button, then click Pair.", AutoSize = true, Margin = new Padding(3, 0, 3, 12) });
            allRow.Controls.Add(Button("Refresh", async () => await RefreshLights()));
            allRow.Controls.Add(Button("All lights on", async () => { await bridge.All(true); await RefreshLights(); }));
            allRow.Controls.Add(Button("All lights off", async () => { await bridge.All(false); await RefreshLights(); }));
            root.Controls.Add(allRow);
            var split = new TableLayoutPanel { Width = 790, Height = 280, ColumnCount = 2, BackColor = Color.White };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 48)); split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 52));
            split.Controls.Add(lights, 0, 0); split.Controls.Add(controls, 1, 0); root.Controls.Add(split);
            controls.Controls.Add(selected);
            var power = new FlowLayoutPanel { Width = 340, Height = 44 };
            power.Controls.Add(Button("On", async () => await Change(new { on = true })));
            power.Controls.Add(Button("Off", async () => await Change(new { on = false })));
            controls.Controls.Add(power);
            var dimming = new FlowLayoutPanel { Width = 340, Height = 43 };
            dimming.Controls.Add(brightness); dimming.Controls.Add(percentage); controls.Controls.Add(dimming);
            apply = Button("Apply brightness", async () => await Change(new { on = true, bri = Brightness(brightness.Value) }));
            controls.Controls.Add(apply);
            var colors = new FlowLayoutPanel { Width = 360, Height = 44 };
            color = Button("Color...", async () => {
                using (var dialog = new ColorDialog { FullOpen = true }) if (dialog.ShowDialog(this) == DialogResult.OK)
                    await Change(new { on = true, hue = (int)Math.Round(dialog.Color.GetHue() / 360.0 * 65535), sat = (int)Math.Round(dialog.Color.GetSaturation() * 254) });
            });
            warm = Button("Warm", async () => await Change(new { on = true, ct = Current.MaxCt }));
            cool = Button("Cool", async () => await Change(new { on = true, ct = Current.MinCt }));
            colors.Controls.Add(color); colors.Controls.Add(warm); colors.Controls.Add(cool); controls.Controls.Add(colors);
            Controls.Add(root); Controls.Add(status);
            lights.SelectedIndexChanged += (sender, args) => UpdateSelection();
            brightness.ValueChanged += (sender, args) => percentage.Text = brightness.Value + "%";
            address.TextChanged += (sender, args) => { bridge = null; lights.Items.Clear(); allRow.Enabled = false; UpdateSelection(); };
            FormClosing += (sender, args) => { if (busy) { args.Cancel = true; status.Text = "Finishing the current request. Please close again in a moment."; } };
            try { saved = Settings.Load(); address.Text = saved.Address; status.Text = "Ready. Connect to your saved bridge, or pair a new one."; }
            catch { saved = new Settings(); status.Text = "Saved connection could not be read. Find your bridge and pair again."; }
            allRow.Enabled = false; UpdateSelection();
        }
        public static int Brightness(int percent) { return Math.Max(1, Math.Min(254, (int)Math.Round(percent * 254.0 / 100))); }
        Light Current { get { return lights.SelectedItem as Light; } }
        Button Button(string text, Func<Task> action)
        {
            var button = new Button { Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Color.White, Padding = new Padding(6, 0, 6, 0) };
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
            controls.Enabled = light != null && light.Reachable;
            selected.Text = light == null ? "Choose a light" : light.Name;
            if (light == null) return;
            bool dimmable = light.State.ContainsKey("bri");
            brightness.Enabled = apply.Enabled = dimmable;
            if (dimmable) brightness.Value = Math.Max(1, Math.Min(100, (int)Math.Round(Convert.ToInt32(light.State["bri"]) * 100.0 / 254)));
            color.Enabled = light.State.ContainsKey("hue") && light.State.ContainsKey("sat");
            warm.Enabled = cool.Enabled = light.State.ContainsKey("ct");
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
            using (var window = new MainWindow())
            {
                window.ShowInTaskbar = false; window.StartPosition = FormStartPosition.Manual;
                window.Location = new Point(-30000, -30000); window.Show(); Application.DoEvents();
                using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save("preview.png"); }
                typeof(MainWindow).GetMethod("ShowLights", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(window, new object[] { lights });
                using (var bitmap = new Bitmap(window.Width, window.Height)) { window.DrawToBitmap(bitmap, new Rectangle(Point.Empty, window.Size)); bitmap.Save("preview-connected.png"); }
                window.Close();
            }
        }
    }
}
