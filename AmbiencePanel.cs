using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace HueReka
{
    // The Ambience tab. It only gathers choices; MainWindow starts and stops the ambience.
    sealed class AmbiencePanel : Panel
    {
        readonly ComboBox palettes = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 10), AccessibleName = "Palette" };
        readonly PaletteStrip strip = new PaletteStrip();
        readonly GlassSlider speed = new GlassSlider { AccessibleName = "Speed" };
        readonly GlassSlider brightness = new GlassSlider { AccessibleName = "Ambience brightness" };
        readonly Label targets, modeHint, speedText, brightnessText;
        readonly GlassButton newPalette, editPalette, deletePalette, drift, together, start;
        AmbienceMode mode = AmbienceMode.Drift;
        bool running, canStart, loading;

        public event EventHandler StartRequested, StopRequested, ChoicesChanged, NewRequested, EditRequested, DeleteRequested;

        public AmbiencePanel(ToolTip tips)
        {
            BackColor = Color.Transparent; Size = new Size(756, 530); // the design size, so anchored controls keep their margins when the window is narrower
            AddLabel("Ambience", 31, 28, 458, 49, 22, true, Glass.Ink);
            targets = AddLabel("", 34, 81, 680, 26, 10, false, Glass.Muted);
            AddLabel("PALETTE", 34, 125, 300, 22, 9, true, Glass.Muted);
            palettes.SetBounds(34, 152, 300, 30); Controls.Add(palettes);
            newPalette = AddButton("New palette...", 350, 145, 130, 42, () => Raise(NewRequested));
            editPalette = AddButton("Edit...", 486, 145, 90, 42, () => Raise(EditRequested));
            deletePalette = AddButton("Delete", 582, 145, 90, 42, () => Raise(DeleteRequested));
            tips.SetToolTip(newPalette, "Make your own palette from colors and whites.");
            strip.SetBounds(34, 198, 680, 46); Controls.Add(strip);
            AddLabel("MODE", 34, 262, 300, 22, 9, true, Glass.Muted);
            drift = AddButton("Drift", 31, 288, 110, 42, () => ChooseMode(AmbienceMode.Drift));
            together = AddButton("Together", 147, 288, 120, 42, () => ChooseMode(AmbienceMode.Together));
            modeHint = AddLabel("", 282, 297, 430, 24, 9, false, Glass.Muted);
            AddLabel("SPEED", 34, 350, 300, 22, 9, true, Glass.Muted);
            speed.SetBounds(28, 374, 330, 42); Controls.Add(speed);
            speedText = AddLabel("", 34, 418, 320, 24, 9, false, Glass.Muted);
            AddLabel("BRIGHTNESS", 400, 350, 300, 22, 9, true, Glass.Muted);
            brightness.SetBounds(394, 374, 320, 42); brightness.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right; Controls.Add(brightness);
            brightnessText = AddLabel("", 400, 418, 300, 24, 9, false, Glass.Muted);
            start = AddButton("Start ambience", 31, 455, 200, 48, () => Raise(running ? StopRequested : StartRequested));
            start.Primary = true;
            AddLabel("Runs while HueReka is open. Changing a light by hand takes it out.", 246, 467, 470, 24, 9, false, Glass.Muted);
            palettes.SelectedIndexChanged += (sender, args) => { UpdateLook(); if (!loading) Raise(ChoicesChanged); };
            speed.ValueChanged += (sender, args) => UpdateLook();
            brightness.ValueChanged += (sender, args) => UpdateLook();
            speed.Committed += (sender, args) => Raise(ChoicesChanged);
            brightness.Committed += (sender, args) => Raise(ChoicesChanged);
            UpdateLook();
        }

        public Palette SelectedPalette { get { return palettes.SelectedItem as Palette; } }
        public AmbienceMode Mode { get { return mode; } set { mode = value; UpdateLook(); } }
        public int SpeedSeconds { get { return AmbienceSpeed.FromSlider(speed.Value); } set { speed.Value = AmbienceSpeed.ToSlider(value); UpdateLook(); } }
        public int BrightnessPercent { get { return brightness.Value; } set { brightness.Value = value; UpdateLook(); } }
        public bool Running { get { return running; } set { running = value; UpdateLook(); } }
        public bool CanStart { set { canStart = value; UpdateLook(); } }
        public string Targets { set { targets.Text = value; } }

        // Replaces the palette list without raising ChoicesChanged; falls back to the first palette.
        public void ShowPalettes(IList<Palette> all, string selectedName)
        {
            loading = true;
            try
            {
                palettes.BeginUpdate();
                palettes.Items.Clear(); palettes.Items.AddRange(all.Cast<object>().ToArray());
                int index = all.ToList().FindIndex(p => String.Equals(p.Name, (selectedName ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
                if (all.Count > 0) palettes.SelectedIndex = Math.Max(0, index);
                palettes.EndUpdate();
            }
            finally { loading = false; }
            UpdateLook();
        }

        void ChooseMode(AmbienceMode chosen) { if (mode == chosen) return; Mode = chosen; Raise(ChoicesChanged); }

        void UpdateLook()
        {
            var palette = SelectedPalette;
            strip.Entries = palette == null ? new List<PaletteEntry>() : palette.Entries; strip.Invalidate();
            editPalette.Enabled = deletePalette.Enabled = palette != null && !Palettes.IsPreset(palette.Name);
            drift.Primary = mode == AmbienceMode.Drift; together.Primary = mode == AmbienceMode.Together;
            modeHint.Text = mode == AmbienceMode.Drift ? "Each light wanders through the palette at its own pace." : "All lights change color at the same time.";
            speedText.Text = "Changes " + AmbienceSpeed.Describe(SpeedSeconds);
            brightnessText.Text = brightness.Value + "%";
            start.Text = running ? "Stop ambience" : "Start ambience";
            start.Enabled = running || (canStart && palette != null);
            foreach (var button in new[] { drift, together, start, editPalette, deletePalette }) button.Invalidate();
        }

        Label AddLabel(string text, int x, int y, int width, int height, float size, bool bold, Color color)
        {
            var label = new Label { Text = text, UseMnemonic = false, Bounds = new Rectangle(x, y, width, height), BackColor = Color.Transparent, ForeColor = color, AutoEllipsis = true, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
            Controls.Add(label); return label;
        }

        GlassButton AddButton(string text, int x, int y, int width, int height, Action click)
        {
            var button = new GlassButton { Text = text, AccessibleName = text };
            button.SetBounds(x, y, width, height); button.Click += (sender, args) => click();
            Controls.Add(button); return button;
        }

        void Raise(EventHandler handler) { if (handler != null) handler(this, EventArgs.Empty); }
    }

    // A palette as a row of soft pills; whites are drawn in their approximate screen color.
    sealed class PaletteStrip : Control
    {
        public IList<PaletteEntry> Entries = new List<PaletteEntry>();
        public PaletteStrip()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent; AccessibleRole = AccessibleRole.Graphic; AccessibleName = "Palette preview";
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var shown = (Entries ?? new List<PaletteEntry>()).Where(entry => entry != null).ToList();
            if (shown.Count == 0) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float gap = 8, width = Math.Min(64, (Width - gap * (shown.Count - 1)) / shown.Count), height = Height - 8;
            for (int i = 0; i < shown.Count; i++)
            {
                var rect = new RectangleF(i * (width + gap), 4, width, height);
                using (var path = Glass.Round(rect, height / 2))
                using (var fill = new SolidBrush(shown[i].PreviewColor()))
                using (var border = new Pen(Color.FromArgb(220, Color.White), 1.5f)) { g.FillPath(fill, path); g.DrawPath(border, path); }
            }
        }
    }
}
