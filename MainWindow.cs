using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HueReka
{
    sealed partial class MainWindow : Form
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
        GlassButton color, warm, cool, onButton, offButton;
        readonly PairingOverlay pairingOverlay = new PairingOverlay();
        // Replaceable so tests can pair with a simulated bridge without touching saved credentials.
        internal Func<Settings, Bridge> CreateBridge = settings => new Bridge(settings);
        internal Action<Settings> SaveSettings = settings => settings.Save();
        readonly Panel allRow = new Panel { BackColor = Color.Transparent };
        readonly Panel bridgeArea = new Panel { BackColor = Color.Transparent };
        readonly LightOrb orb = new LightOrb();
        readonly Label lightDetail = new Label { AutoSize = false, BackColor = Color.Transparent, ForeColor = Glass.Muted, AutoEllipsis = true };
        readonly Label lightCount = new Label { AutoSize = false, BackColor = Color.Transparent, ForeColor = Glass.Muted, Font = new Font("Segoe UI", 9) };
        readonly Label connectionBadge = new Label { AutoSize = false, BackColor = Color.Transparent, ForeColor = Glass.Muted, TextAlign = ContentAlignment.MiddleRight };
        readonly Label empty = new Label { Text = "Your lights will appear here.\n\nConnect to your bridge below\nto get started.", BackColor = Color.FromArgb(239, 240, 249), ForeColor = Glass.Muted, TextAlign = ContentAlignment.MiddleCenter };
        readonly Label colorHint = new Label { AutoSize = false, BackColor = Color.Transparent, ForeColor = Glass.Muted, Font = new Font("Segoe UI", 9) };
        readonly Label brightnessHint = new Label { Text = "Slide to adjust. Changes apply when you let go.", AutoSize = false, BackColor = Color.Transparent, ForeColor = Glass.Muted, Font = new Font("Segoe UI", 9) };
        readonly List<GlassButton> swatches = new List<GlassButton>();
        readonly ToolTip tips = new ToolTip { AutoPopDelay = 8000, InitialDelay = 500 };
        readonly System.Windows.Forms.Timer refreshTimer = new System.Windows.Forms.Timer { Interval = 15000 };
        readonly bool preview;
        Bridge bridge;
        Settings saved;
        bool busy, refreshing, closeWhenIdle;
        int changeVersion;
        string shownSignature;
        CancellationTokenSource pairing;

        public MainWindow(bool preview = false)
        {
            this.preview = preview;
            if (preview) SaveSettings = settings => { };
            Text = "HueReka!"; ClientSize = new Size(1120, 820); MinimumSize = new Size(1080, 860);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            StartPosition = FormStartPosition.CenterScreen; Font = new Font("Segoe UI", 10); KeyPreview = true;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(239, 237, 249); ForeColor = Glass.Ink;
            Controls.Add(root);
            Controls.Add(pairingOverlay); pairingOverlay.BringToFront();
            BuildHeader();
            BuildSidebar();
            BuildControls();
            status.SetBounds(35, 757, 1047, 49); status.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; root.Controls.Add(status);
            WireEvents();
            try { saved = preview ? new Settings() : Settings.Load(); SetAddress(saved.Address); status.Text = "Ready when you are."; }
            catch (Exception) { saved = new Settings(); status.Text = "Your saved connection could not be read. Press Connect to pair your bridge again."; }
            allRow.Enabled = false; UpdateSelection();
        }

        void BuildHeader()
        {
            var logo = new LightOrb { Bounds = new Rectangle(22, 12, 64, 64) }; root.Controls.Add(logo);
            AddLabel(root, "HueReka!", 85, 27, 220, 35, 18, true);
            connectionBadge.SetBounds(780, 25, 300, 35); connectionBadge.Anchor = AnchorStyles.Top | AnchorStyles.Right; root.Controls.Add(connectionBadge);
            AddLabel(root, "Set the mood.", 32, 99, 650, 54, 32, true);
            AddLabel(root, "A little light. A whole different feeling.", 35, 158, 570, 28, 11, false).ForeColor = Glass.Muted;
            allRow.SetBounds(743, 125, 346, 48); allRow.Anchor = AnchorStyles.Top | AnchorStyles.Right; root.Controls.Add(allRow);
            var refresh = Button("Refresh", RefreshWithStatus); refresh.SetBounds(0, 0, 94, 42); allRow.Controls.Add(refresh);
            tips.SetToolTip(refresh, "Reload light states from the bridge (F5). HueReka also refreshes automatically.");
            var allOn = Button("All lights on", () => SwitchAll(true)); allOn.SetBounds(100, 0, 117, 42); allRow.Controls.Add(allOn);
            var allOff = Button("All lights off", () => SwitchAll(false)); allOff.SetBounds(223, 0, 117, 42); allRow.Controls.Add(allOff);
        }

        void BuildSidebar()
        {
            sidebar.SetBounds(28, 209, 292, 530); sidebar.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left; root.Controls.Add(sidebar);
            AddLabel(sidebar, "Your lights", 23, 23, 215, 29, 15, true);
            lightCount.SetBounds(25, 60, 250, 24); sidebar.Controls.Add(lightCount);
            lights.SetBounds(19, 97, 252, 216); lights.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; sidebar.Controls.Add(lights);
            tips.SetToolTip(lights, "Ctrl+click or Shift+click to select several lights. Ctrl+A selects all.\nDouble-click a light to switch it on or off. Space switches all selected lights.");
            empty.Bounds = lights.Bounds; empty.Anchor = lights.Anchor; sidebar.Controls.Add(empty);
            bridgeArea.Bounds = new Rectangle(23, 329, 246, 181); bridgeArea.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
            sidebar.Controls.Add(bridgeArea);
            AddLabel(bridgeArea, "HUE BRIDGE", 2, 0, 235, 24, 8.5f, true).ForeColor = Glass.Muted;
            address.SetBounds(3, 28, 236, 30); bridgeArea.Controls.Add(address);
            tips.SetToolTip(address, "Your bridge's IP address. Press Enter to connect.");
            var find = Button("Find bridge", Discover); find.SetBounds(0, 66, 115, 39); bridgeArea.Controls.Add(find);
            tips.SetToolTip(find, "Search your network for Hue Bridges");
            var connect = Button("Connect", () => ConnectOrPair(false)); connect.Primary = true; connect.SetBounds(121, 66, 120, 39); bridgeArea.Controls.Add(connect);
            tips.SetToolTip(connect, "Connect to the bridge. New bridges are paired automatically; just press the bridge's button when asked.");
            var pair = Button("Pair again", () => ConnectOrPair(true)); pair.SetBounds(0, 114, 100, 38); bridgeArea.Controls.Add(pair);
            tips.SetToolTip(pair, "Pair again if your bridge was reset, replaced, or its certificate changed");
            AddLabel(bridgeArea, "New bridge? Just press\nConnect.", 108, 114, 136, 47, 8.5f, false).ForeColor = Glass.Muted;
        }

        void BuildControls()
        {
            controls.SetBounds(336, 209, 756, 530); controls.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right; root.Controls.Add(controls);
            selected.SetBounds(31, 28, 458, 49); selected.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; controls.Controls.Add(selected);
            lightDetail.SetBounds(34, 81, 500, 26); lightDetail.Anchor = selected.Anchor; controls.Controls.Add(lightDetail);
            onButton = Button("On", () => Change(light => true, light => new { on = true })); onButton.SetBounds(559, 34, 78, 43); onButton.Anchor = AnchorStyles.Top | AnchorStyles.Right; controls.Controls.Add(onButton);
            offButton = Button("Off", () => Change(light => true, light => new { on = false })); offButton.SetBounds(642, 34, 78, 43); offButton.Anchor = onButton.Anchor; controls.Controls.Add(offButton);
            orb.SetBounds(43, 125, 267, 236); controls.Controls.Add(orb);
            var orbCaption = AddLabel(controls, "A little atmosphere, on demand.", 29, 361, 300, 24, 9, false); orbCaption.TextAlign = ContentAlignment.MiddleCenter; orbCaption.ForeColor = Glass.Muted;
            AddLabel(controls, "BRIGHTNESS", 365, 154, 170, 25, 9, true).ForeColor = Glass.Muted;
            percentage.SetBounds(363, 188, 300, 49); controls.Controls.Add(percentage);
            brightness.SetBounds(357, 246, 352, 42); brightness.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; controls.Controls.Add(brightness);
            brightnessHint.SetBounds(365, 300, 340, 24); controls.Controls.Add(brightnessHint);
            AddLabel(controls, "COLOR & WARMTH", 34, 405, 300, 22, 9, true).ForeColor = Glass.Muted;
            colorHint.SetBounds(34, 486, 680, 27); colorHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; controls.Controls.Add(colorHint);
            Color[] colors = { Color.FromArgb(255, 179, 94), Color.FromArgb(246, 135, 170), Color.FromArgb(178, 145, 237), Color.FromArgb(122, 179, 238), Color.FromArgb(120, 207, 180) };
            string[] names = { "Amber", "Rose", "Lavender", "Sky", "Mint" };
            for (int i = 0; i < colors.Length; i++)
            {
                Color tint = colors[i];
                var swatch = Button(names[i], () => SetColor(tint)); swatch.Swatch = tint; swatch.AccessibleName = names[i];
                swatch.SetBounds(31 + i * 46, 439, 39, 39); controls.Controls.Add(swatch); swatches.Add(swatch);
                tips.SetToolTip(swatch, names[i]);
            }
            color = Button("Custom color", PickColor);
            color.SetBounds(274, 437, 138, 43); controls.Controls.Add(color);
            warm = Button("Warm", () => Change(SupportsWhite, light => new { on = true, ct = light.MaxCt })); warm.SetBounds(434, 437, 111, 43); controls.Controls.Add(warm);
            cool = Button("Cool", () => Change(SupportsWhite, light => new { on = true, ct = light.MinCt })); cool.SetBounds(551, 437, 111, 43); controls.Controls.Add(cool);
        }

        void WireEvents()
        {
            lights.SelectedIndexChanged += (sender, args) => UpdateSelection();
            lights.DoubleClick += async (sender, args) => await Run(ToggleSelection);
            brightness.ValueChanged += (sender, args) => percentage.Text = brightness.Value + "%";
            brightness.Committed += async (sender, args) => await Run(() => Change(SupportsBrightness, light => new { on = true, bri = Brightness(brightness.Value) }));
            address.TextChanged += (sender, args) =>
            {
                if (bridge != null && !busy && address.Text.Trim() != bridge.Connection.Address)
                    status.Text = "Press Enter or Connect to switch bridges. Controls still act on " + bridge.Connection.Address + " until then.";
            };
            address.KeyDown += async (sender, args) => { if (args.KeyCode != Keys.Enter) return; args.SuppressKeyPress = true; await Run(() => ConnectOrPair(false)); };
            pairingOverlay.CancelRequested += (sender, args) => CancelPairing();
            refreshTimer.Tick += (sender, args) => AutoRefresh();
            Activated += (sender, args) => AutoRefresh();
            Shown += async (sender, args) => { if (!preview) { refreshTimer.Start(); await Run(StartUp); } };
            FormClosing += (sender, args) =>
            {
                if (!busy) return;
                args.Cancel = true; closeWhenIdle = true;
                if (pairingOverlay.Visible) pairingOverlay.Dismiss(); else CancelPairing();
                status.Text = "Finishing the current request, then closing...";
            };
            FormClosed += (sender, args) => { refreshTimer.Dispose(); tips.Dispose(); };
        }

        Label AddLabel(Control parent, string text, int x, int y, int width, int height, float size, bool bold)
        {
            var label = new Label { Text = text, UseMnemonic = false, Bounds = new Rectangle(x, y, width, height), BackColor = Color.Transparent, ForeColor = Glass.Ink, AutoEllipsis = true, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
            parent.Controls.Add(label); return label;
        }

        GlassButton Button(string text, Func<Task> action)
        {
            var button = new GlassButton { Text = text, AccessibleName = text };
            button.Click += async (sender, args) => await Run(action);
            return button;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (pairingOverlay.Visible) { if (e.KeyCode == Keys.Escape) { pairingOverlay.Dismiss(); e.Handled = true; } }
            else if (e.KeyCode == Keys.F5 && bridge != null) { e.Handled = true; var ignored = Run(RefreshWithStatus); }
            else if (lights.Focused && e.Control && e.KeyCode == Keys.A) { e.Handled = e.SuppressKeyPress = true; SelectAll(); }
            else if (lights.Focused && e.KeyCode == Keys.Space && !e.Control) { e.Handled = e.SuppressKeyPress = true; var ignored = Run(ToggleSelection); }
        }

        void SelectAll()
        {
            lights.BeginUpdate();
            for (int i = 0; i < lights.Items.Count; i++) lights.SetSelected(i, true);
            lights.EndUpdate();
        }

        void SetAddress(string value)
        {
            if (!String.IsNullOrEmpty(value) && !address.Items.Contains(value)) address.Items.Add(value);
            address.Text = value;
        }
    }
}
