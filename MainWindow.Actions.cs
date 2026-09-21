using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HueReka
{
    sealed partial class MainWindow
    {
        // Hue's link button stays active for 30 seconds once pressed; allow time to walk to the bridge first.
        internal static TimeSpan PairTimeout = TimeSpan.FromSeconds(90);
        const string EmptyText = "Your lights will appear here.\n\nConnect to your bridge below\nto get started.";
        bool lostContact;

        public static int Brightness(int percent) { return Math.Max(1, Math.Min(254, (int)Math.Round(percent * 254.0 / 100))); }
        static bool IsOn(Light light) { return light.State.ContainsKey("on") && Convert.ToBoolean(light.State["on"]); }
        static bool SupportsBrightness(Light light) { return light.State.ContainsKey("bri"); }
        static bool SupportsColor(Light light) { return light.State.ContainsKey("hue") && light.State.ContainsKey("sat"); }
        static bool SupportsWhite(Light light) { return light.State.ContainsKey("ct"); }
        List<Light> Selection { get { return lights.SelectedItems.Cast<Light>().ToList(); } }

        async Task Run(Func<Task> action)
        {
            if (busy) return;
            busy = true; UseWaitCursor = true;
            try { await action(); }
            catch (OperationCanceledException) { status.Text = "Cancelled. Press Connect whenever you're ready."; }
            catch (WebException) { status.Text = "Can't reach the bridge. Check that it's powered on and on the same network as this PC. If it was reset or replaced, use Pair again."; }
            catch (Exception error) { status.Text = error.Message; }
            finally
            {
                busy = false; UseWaitCursor = false; UpdateSelection();
                if (closeWhenIdle) BeginInvoke(new Action(Close));
            }
        }

        async Task StartUp()
        {
            if (!String.IsNullOrEmpty(saved.Key) && !String.IsNullOrEmpty(saved.Address)) { await ConnectOrPair(false); return; }
            status.Text = "Welcome! Looking for your Hue Bridge...";
            string[] found;
            try { found = await DiscoverAddresses(); }
            catch (WebException) { status.Text = "Welcome! Enter your bridge's IP address (see the Hue app's bridge settings), then press Connect."; return; }
            ShowFound(found);
            if (found.Length == 1) await ConnectOrPair(false);
        }

        async Task Discover()
        {
            status.Text = "Searching for Hue Bridges on your network...";
            ShowFound(await DiscoverAddresses());
        }

        void ShowFound(string[] found)
        {
            address.Items.Clear(); address.Items.AddRange(found);
            if (found.Length > 0) address.SelectedIndex = 0;
            status.Text = found.Length == 0 ? "No bridges found. Enter the IP address from the Hue app's bridge settings, then press Connect."
                : found.Length == 1 ? "Found your bridge at " + found[0] + "."
                : found.Length + " bridges found. Choose one from the list, then press Connect.";
        }

        static async Task<string[]> DiscoverAddresses()
        {
            string json = await Task.Run(() =>
            {
                var request = (HttpWebRequest)WebRequest.Create("https://discovery.meethue.com/");
                request.Timeout = 7000; request.ReadWriteTimeout = 7000;
                using (var response = request.GetResponse()) using (var reader = new StreamReader(response.GetResponseStream())) return reader.ReadToEnd();
            });
            var addresses = new List<string>();
            foreach (object entry in Json.Parse(json) as object[] ?? new object[0])
            {
                // One malformed entry should not hide the other bridges, so it is skipped.
                try { addresses.Add(Bridge.ValidateAddress(Convert.ToString(Json.Map(entry)["internalipaddress"]))); }
                catch (InvalidOperationException) { }
                catch (InvalidCastException) { }
                catch (KeyNotFoundException) { }
            }
            return addresses.Distinct().ToArray();
        }

        async Task ConnectOrPair(bool forcePair)
        {
            if (String.IsNullOrWhiteSpace(address.Text))
            {
                await Discover();
                if (address.Items.Count != 1) return;
            }
            string ip = Bridge.ValidateAddress(address.Text);
            bridgeArea.Enabled = false;
            try
            {
                if (!forcePair && saved.Address == ip && !String.IsNullOrEmpty(saved.Key)) await ConnectSaved();
                else await PairWith(ip);
            }
            finally { bridgeArea.Enabled = true; }
        }

        async Task ConnectSaved()
        {
            status.Text = "Connecting to your bridge at " + saved.Address + "...";
            bool unreachable = false;
            try { await Open(saved); }
            catch (WebException) { unreachable = true; }
            if (!unreachable) { status.Text = "Connected. " + LightsSummary(); return; }
            status.Text = "Your bridge didn't answer at " + saved.Address + ". Looking for it on the network...";
            string movedTo = await TryMovedBridge();
            if (movedTo == null) throw new WebException("Bridge unreachable.");
            status.Text = "Your bridge moved to " + movedTo + ". Reconnected. " + LightsSummary();
        }

        // Routers can hand the bridge a new IP address. The pinned certificate proves a bridge
        // found at another address is the same one, so the saved key can safely be reused there.
        async Task<string> TryMovedBridge()
        {
            if (String.IsNullOrEmpty(saved.Fingerprint)) return null;
            string[] found;
            try { found = await DiscoverAddresses(); }
            catch (WebException) { return null; }
            foreach (string ip in found.Where(candidate => candidate != saved.Address))
            {
                bool reconnected = true;
                try { await Open(new Settings { Address = ip, Key = saved.Key, Fingerprint = saved.Fingerprint, FavoriteLight = saved.FavoriteLight }); }
                catch (WebException) { reconnected = false; }
                catch (InvalidOperationException) { reconnected = false; }
                if (reconnected) return ip;
            }
            return null;
        }

        async Task PairWith(string ip)
        {
            var connection = new Settings { Address = ip, FavoriteLight = ip == saved.Address ? saved.FavoriteLight : "" };
            var candidate = CreateBridge(connection);
            ShowPairingOverlay(ip);
            try
            {
                while (!await WaitForLinkButton(candidate))
                {
                    if (!await pairingOverlay.AskRetry()) { status.Text = "No problem. Press Connect whenever you're ready to pair."; return; }
                    pairingOverlay.Restart(PairTimeout);
                }
                pairingOverlay.ShowPaired();
                await Open(connection);
                await Task.Delay(PairedCelebration);
                status.Text = "Paired and connected. " + LightsSummary() + " HueReka will connect automatically next time.";
            }
            finally { HidePairingOverlay(); }
        }

        internal static TimeSpan PairedCelebration = TimeSpan.FromMilliseconds(900);

        // Returns false when time ran out; cancellation and connection errors propagate.
        async Task<bool> WaitForLinkButton(Bridge candidate)
        {
            pairing = new CancellationTokenSource();
            bool pressed = true;
            try { await candidate.PairWhenReady(PairTimeout, null, pairing.Token); }
            catch (TimeoutException) { pressed = false; }
            finally { var finished = pairing; pairing = null; finished.Dispose(); }
            return pressed;
        }

        void ShowPairingOverlay(string ip)
        {
            Bitmap snapshot = null;
            if (root.Width > 0 && root.Height > 0)
            {
                snapshot = new Bitmap(root.Width, root.Height);
                root.DrawToBitmap(snapshot, new Rectangle(Point.Empty, root.Size));
            }
            root.Enabled = false; UseWaitCursor = false;
            status.Text = "Waiting for the button on your Hue Bridge...";
            pairingOverlay.ShowWaiting(snapshot, ip, PairTimeout);
        }

        void HidePairingOverlay()
        {
            pairingOverlay.Close();
            root.Enabled = true; UseWaitCursor = busy;
        }

        void CancelPairing() { if (pairing != null) pairing.Cancel(); }

        async Task Open(Settings connection)
        {
            var candidate = CreateBridge(connection);
            var result = await candidate.Lights();
            bridge = candidate; saved = connection; lostContact = false;
            SetAddress(connection.Address);
            changeVersion++;
            ShowLights(result);
            SaveSettings(saved);
        }

        string LightsSummary() { return lights.Items.Count == 1 ? "1 light found." : lights.Items.Count + " lights found."; }

        void ShowLights(List<Light> result)
        {
            var selectedIds = new HashSet<string>(Selection.Select(light => light.Id));
            string signature = String.Join("|", result.Select(light => light.Id + ":" + light.Name + ":" + Json.Serializer.Serialize(light.State)));
            if (signature != shownSignature)
            {
                // Rebuild only when something changed, keeping the selection and scroll position.
                shownSignature = signature;
                int top = lights.TopIndex;
                lights.BeginUpdate();
                lights.Items.Clear(); lights.Items.AddRange(result.ToArray());
                for (int i = 0; i < result.Count; i++) if (selectedIds.Contains(result[i].Id)) lights.SetSelected(i, true);
                if (lights.SelectedIndices.Count == 0 && result.Count > 0) lights.SetSelected(Math.Max(0, result.FindIndex(light => light.Id == saved.FavoriteLight)), true);
                if (result.Count > 0) lights.TopIndex = Math.Min(top, result.Count - 1);
                lights.EndUpdate();
            }
            lights.FavoriteId = saved.FavoriteLight;
            allRow.Enabled = bridge != null; UpdateSelection();
        }

        // Stars the selected light, or clears the star if it is already the favorite. Only one light is the favorite.
        Task ToggleFavorite()
        {
            var chosen = Selection;
            if (bridge == null || chosen.Count != 1) return Task.FromResult(0);
            string previous = saved.FavoriteLight;
            string next = chosen[0].Id == previous ? "" : chosen[0].Id;
            saved.FavoriteLight = next;
            try { SaveSettings(saved); }
            catch { saved.FavoriteLight = previous; throw; }
            lights.FavoriteId = next;
            status.Text = next == "" ? chosen[0].Name + " is no longer your favorite." : chosen[0].Name + " is now your favorite. HueReka will select it when it opens.";
            return Task.FromResult(0);
        }

        async Task RefreshLights() { changeVersion++; ShowLights(await bridge.Lights()); }

        async Task RefreshWithStatus()
        {
            if (bridge == null) return;
            await RefreshLights();
            status.Text = "Up to date as of " + DateTime.Now.ToShortTimeString() + ". " + LightsSummary();
        }

        async void AutoRefresh()
        {
            if (bridge == null || busy || refreshing || brightness.Dragging || MouseButtons != MouseButtons.None) return;
            refreshing = true;
            int version = changeVersion; var source = bridge;
            try
            {
                var result = await source.Lights();
                // Discard results that raced with a user change or with closing the window.
                if (IsDisposed || version != changeVersion || source != bridge || busy) return;
                ShowLights(result);
                if (lostContact) { lostContact = false; status.Text = "Back in touch with your bridge."; }
            }
            catch (WebException) { if (!IsDisposed) { lostContact = true; status.Text = "Lost contact with the bridge. HueReka will keep trying."; } }
            catch (Exception error) { if (!IsDisposed) status.Text = error.Message; }
            finally { refreshing = false; }
        }

        async Task SwitchAll(bool on)
        {
            changeVersion++;
            await bridge.All(on); await RefreshLights();
            status.Text = on ? "All lights switched on." : "All lights switched off.";
        }

        Task ToggleSelection()
        {
            bool anyOn = Selection.Any(light => light.Reachable && IsOn(light));
            return Change(light => true, light => new { on = !anyOn });
        }

        Task SetColor(Color tint)
        {
            double max = Math.Max(tint.R, Math.Max(tint.G, tint.B));
            double min = Math.Min(tint.R, Math.Min(tint.G, tint.B));
            int hue = (int)Math.Round(tint.GetHue() / 360.0 * 65535), sat = max == 0 ? 0 : (int)Math.Round((max - min) / max * 254);
            return Change(SupportsColor, light => new { on = true, hue = hue, sat = sat });
        }

        async Task PickColor()
        {
            Color chosen;
            using (var dialog = new ColorDialog { FullOpen = true, Color = orb.Tint })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                chosen = dialog.Color;
            }
            await SetColor(chosen);
        }

        // Applies a state to every selected light that can use it, one request at a time to respect
        // the bridge's rate limit. Other lights still update if one fails; the first error is reported.
        async Task Change(Func<Light, bool> supports, Func<Light, object> state)
        {
            if (bridge == null) return;
            var chosen = Selection;
            var targets = chosen.Where(light => light.Reachable && supports(light)).ToList();
            if (targets.Count == 0) return;
            changeVersion++;
            Exception failure = null;
            foreach (var light in targets)
            {
                try { await bridge.Set(light.Id, state(light)); }
                catch (Exception error) { if (failure == null) failure = error; }
            }
            await RefreshLights();
            if (failure != null) throw failure;
            int skipped = chosen.Count - targets.Count;
            status.Text = (targets.Count == 1 ? targets[0].Name + " updated." : targets.Count + " lights updated.")
                + (skipped > 0 ? " Skipped " + skipped + " that can't do this or are unreachable." : "");
        }

        void UpdateSelection()
        {
            var chosen = Selection;
            var usable = bridge == null ? new List<Light>() : chosen.Where(light => light.Reachable).ToList();
            onButton.Enabled = offButton.Enabled = usable.Count > 0;
            brightness.Enabled = usable.Any(SupportsBrightness);
            color.Enabled = usable.Any(SupportsColor);
            foreach (var swatch in swatches) swatch.Enabled = color.Enabled;
            warm.Enabled = cool.Enabled = usable.Any(SupportsWhite);
            bool starred = chosen.Count == 1 && chosen[0].Id == saved.FavoriteLight;
            favorite.Enabled = bridge != null && chosen.Count == 1;
            favorite.Text = starred ? "★" : "☆";
            favorite.AccessibleName = starred ? "Remove favorite" : "Make favorite";
            brightnessHint.Visible = brightness.Enabled;
            empty.Visible = lights.Items.Count == 0;
            empty.Text = bridge == null && busy ? "Looking for your lights..." : EmptyText;
            lightCount.Text = lights.Items.Count == 0 ? "A home full of possibilities" : lights.Items.Count == 1 ? "1 light in your home" : lights.Items.Count + " lights  /  Ctrl-click to pick several";
            connectionBadge.Text = bridge == null ? "Not connected  /  Local control" : "Connected  /  " + bridge.Connection.Address;
            connectionBadge.ForeColor = bridge == null ? Glass.Muted : Color.FromArgb(49, 123, 106);
            UpdateHeading(chosen);
            UpdateLook(chosen, usable);
        }

        void UpdateHeading(List<Light> chosen)
        {
            if (chosen.Count == 0)
            {
                selected.Text = "Find your glow";
                lightDetail.Text = bridge == null ? "Connect a bridge and choose a light to begin." : "Choose a light on the left.";
            }
            else if (chosen.Count == 1)
            {
                var light = chosen[0];
                selected.Text = light.Name;
                lightDetail.Text = !light.Reachable ? "Unreachable / check the physical power switch" : "Light " + light.Id + "  /  " + (IsOn(light) ? "On" : "Off") + "  /  Double-click to switch";
            }
            else
            {
                int on = chosen.Count(light => light.Reachable && IsOn(light)), unreachable = chosen.Count(light => !light.Reachable);
                selected.Text = chosen.Count + " lights selected";
                lightDetail.Text = on + " on  /  " + (chosen.Count - on - unreachable) + " off" + (unreachable > 0 ? "  /  " + unreachable + " unreachable" : "") + "  /  Changes apply to all";
            }
            colorHint.Text = chosen.Count == 0 ? "Color and warmth controls appear for compatible lights."
                : !color.Enabled && !warm.Enabled ? "Color controls require a color-capable light."
                : chosen.Count > 1 ? "Colors and warmth apply to every selected light that supports them."
                : "Choose a color, or dial in a warmer shade of white.";
        }

        void UpdateLook(List<Light> chosen, List<Light> usable)
        {
            var light = usable.FirstOrDefault(IsOn) ?? usable.FirstOrDefault() ?? chosen.FirstOrDefault();
            if (light == null)
            {
                percentage.Text = "--"; orb.Lit = true; orb.Tint = Color.FromArgb(188, 157, 240);
                onButton.Primary = offButton.Primary = false;
            }
            else
            {
                onButton.Primary = usable.Count > 0 && usable.All(IsOn);
                offButton.Primary = usable.Count > 0 && !usable.Any(IsOn);
                var dimmable = chosen.Where(SupportsBrightness).ToList();
                if (dimmable.Count == 0) percentage.Text = "--";
                else if (!brightness.Dragging)
                {
                    brightness.Value = Math.Max(1, Math.Min(100, (int)Math.Round(dimmable.Average(item => Convert.ToDouble(item.State["bri"])) * 100.0 / 254)));
                    percentage.Text = brightness.Value + "%";
                }
                orb.Lit = usable.Any(IsOn);
                orb.Tint = TintOf(light);
            }
            onButton.Invalidate(); offButton.Invalidate(); orb.Invalidate();
        }

        static Color TintOf(Light light)
        {
            var state = light.State;
            if (!SupportsColor(light) || (state.ContainsKey("colormode") && Convert.ToString(state["colormode"]) == "ct")) return Color.FromArgb(246, 199, 136);
            double h = Convert.ToDouble(state["hue"]) / 65535 * 6, s = Convert.ToDouble(state["sat"]) / 254;
            double c = s, x = c * (1 - Math.Abs(h % 2 - 1)), m = 1 - c;
            double r = 0, g = 0, b = 0;
            if (h < 1) { r = c; g = x; } else if (h < 2) { r = x; g = c; } else if (h < 3) { g = c; b = x; }
            else if (h < 4) { g = x; b = c; } else if (h < 5) { r = x; b = c; } else { r = c; b = x; }
            return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
        }
    }
}
