using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("HueReka!")]
[assembly: AssemblyDescription("Local control for Philips Hue lights")]
[assembly: AssemblyProduct("HueReka!")]
[assembly: AssemblyVersion("1.0.1.0")]
[assembly: AssemblyFileVersion("1.0.1.0")]
[assembly: AssemblyInformationalVersion("1.0.1")]

namespace HueReka
{
    sealed class LinkButtonException : InvalidOperationException
    {
        public LinkButtonException() : base("Press the round link button on your Hue Bridge to finish pairing.") { }
    }

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
                if (type == 101) throw new LinkButtonException();
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
        public static TimeSpan PairPollInterval = TimeSpan.FromSeconds(1.5);
        // Keeps asking the bridge to pair until its link button is pressed, so the user
        // never has to press the button and then come back to click Pair again.
        public async Task PairWhenReady(TimeSpan timeout, Action<int> waiting, CancellationToken cancel)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            while (true)
            {
                cancel.ThrowIfCancellationRequested();
                try { await Pair(); return; }
                catch (LinkButtonException) { }
                int secondsLeft = (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds);
                if (secondsLeft <= 0) throw new TimeoutException("The bridge's link button wasn't pressed in time. Press Connect to try again.");
                if (waiting != null) waiting(secondsLeft);
                await Task.Delay(PairPollInterval, cancel);
            }
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
}
