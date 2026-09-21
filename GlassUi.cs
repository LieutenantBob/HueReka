using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace HueReka
{
    // A control that draws a background custom glass controls should appear on top of.
    interface IGlassSurface
    {
        void PaintSurface(Graphics graphics);
    }

    static class Glass
    {
        public static readonly Color Ink = Color.FromArgb(37, 40, 67);
        public static readonly Color Muted = Color.FromArgb(101, 104, 132);
        public static readonly Color Accent = Color.FromArgb(105, 83, 210);
        public static void PaintBackdrop(Control child, Graphics graphics)
        {
            // Theme parent-background painting can copy sibling text or leave black
            // pixels on a live HWND. Compose only our actual background surfaces,
            // in their own coordinates, without invoking native parent painting.
            var layers = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<Control, Point>>();
            Point offset = child.Location;
            for (Control parent = child.Parent; parent != null; parent = parent.Parent)
            {
                layers.Add(new System.Collections.Generic.KeyValuePair<Control, Point>(parent, offset));
                offset.Offset(parent.Left, parent.Top);
            }
            using (var fill = new SolidBrush(Color.FromArgb(239, 237, 249))) graphics.FillRectangle(fill, child.ClientRectangle);
            for (int i = layers.Count - 1; i >= 0; i--)
            {
                var surface = layers[i].Key as IGlassSurface;
                if (surface == null) continue;
                var state = graphics.Save();
                try
                {
                    graphics.TranslateTransform(-layers[i].Value.X, -layers[i].Value.Y);
                    surface.PaintSurface(graphics);
                }
                finally { graphics.Restore(state); }
            }
        }
        public static GraphicsPath Round(RectangleF rect, float radius)
        {
            var path = new GraphicsPath(); float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            path.AddArc(rect.X, rect.Y, d, d, 180, 90); path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90); path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90); path.CloseFigure(); return path;
        }
        public static void Glow(Graphics g, RectangleF bounds, Color color)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(bounds);
                using (var brush = new PathGradientBrush(path))
                { brush.CenterColor = color; brush.SurroundColors = new[] { Color.FromArgb(0, color) }; g.FillEllipse(brush, bounds); }
            }
        }
    }

    sealed class GlassCanvas : Panel, IGlassSurface
    {
        public GlassCanvas() { DoubleBuffered = true; ResizeRedraw = true; }
        protected override void OnPaintBackground(PaintEventArgs e)
        {
            PaintSurface(e.Graphics);
        }
        public void PaintSurface(Graphics g)
        {
            if (Width <= 0 || Height <= 0) return;
            using (var fill = new LinearGradientBrush(ClientRectangle, Color.FromArgb(239, 237, 249), Color.FromArgb(223, 233, 245), 45)) g.FillRectangle(fill, ClientRectangle);
            Glass.Glow(g, new RectangleF(Width * .3f, -180, Width * .8f, Height * .9f), Color.FromArgb(155, 209, 192, 246));
            Glass.Glow(g, new RectangleF(-250, Height * .3f, 850, 700), Color.FromArgb(130, 179, 209, 245));
            Glass.Glow(g, new RectangleF(Width * .5f, Height * .45f, 700, 650), Color.FromArgb(145, 249, 206, 186));
        }
    }

    sealed class GlassPanel : Panel, IGlassSurface
    {
        public GlassPanel() { SetStyle(ControlStyles.SupportsTransparentBackColor, true); BackColor = Color.Transparent; DoubleBuffered = true; ResizeRedraw = true; }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e); PaintSurface(e.Graphics);
        }
        public void PaintSurface(Graphics graphics)
        {
            if (Width < 8 || Height < 8) return;
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var bounds = new RectangleF(2, 2, Width - 5, Height - 7);
            using (var shadow = Glass.Round(new RectangleF(3, 5, Width - 6, Height - 7), 26))
            using (var brush = new SolidBrush(Color.FromArgb(15, 72, 66, 104))) graphics.FillPath(brush, shadow);
            using (var path = Glass.Round(bounds, 26))
            using (var fill = new LinearGradientBrush(bounds, Color.FromArgb(211, 255, 255, 255), Color.FromArgb(135, 255, 255, 255), 110))
            using (var border = new Pen(Color.FromArgb(235, 255, 255, 255), 1.3f))
            { graphics.FillPath(fill, path); graphics.DrawPath(border, path); }
        }
    }

    sealed class GlassButton : Control, IButtonControl
    {
        public bool Primary;
        public Color Swatch = Color.Empty;
        bool hover, pressed;
        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { Invalidate(); }
        public void PerformClick() { if (Enabled && CanSelect) OnClick(EventArgs.Empty); }
        public GlassButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.Selectable | ControlStyles.StandardClick | ControlStyles.ResizeRedraw, true);
            BackColor = Color.FromArgb(239, 237, 249); TabStop = true; AccessibleRole = AccessibleRole.PushButton;
            Size = new Size(116, 42); Cursor = Cursors.Hand; Font = new Font("Segoe UI", 10, FontStyle.Regular);
        }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { Focus(); pressed = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { pressed = true; Invalidate(); e.Handled = e.SuppressKeyPress = true; }
        }
        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (pressed && (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)) { pressed = false; Invalidate(); PerformClick(); e.Handled = true; }
        }
        protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override AccessibleObject CreateAccessibilityInstance() { return new GlassButtonAccessibility(this); }
        sealed class GlassButtonAccessibility : ControlAccessibleObject
        {
            readonly GlassButton owner;
            public GlassButtonAccessibility(GlassButton owner) : base(owner) { this.owner = owner; }
            public override string DefaultAction { get { return "Press"; } }
            public override void DoDefaultAction() { owner.PerformClick(); }
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Glass.PaintBackdrop(this, e.Graphics);
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var rect = new RectangleF(2, pressed ? 4 : 2, Width - 5, Height - 5);
            bool swatch = !Swatch.IsEmpty;
            Color top = swatch ? Swatch : Primary ? Color.FromArgb(135, 114, 233) : Color.FromArgb(245, 255, 255, 255);
            Color bottom = swatch ? ControlPaint.Dark(Swatch, .06f) : Primary ? Glass.Accent : Color.FromArgb(170, 239, 239, 250);
            if (hover && Enabled) { top = ControlPaint.Light(top, .13f); bottom = ControlPaint.Light(bottom, .1f); }
            if (!Enabled) { top = Color.FromArgb(225, 232, 233, 241); bottom = Color.FromArgb(210, 231, 232, 239); }
            using (var path = Glass.Round(rect, swatch ? 22 : 15))
            using (var fill = new LinearGradientBrush(rect, top, bottom, 90))
            using (var border = new Pen(Color.FromArgb(220, 255, 255, 255), 1.3f))
            { g.FillPath(fill, path); g.DrawPath(border, path); }
            if (Focused && ShowFocusCues)
                using (var focus = Glass.Round(new RectangleF(0.8f, .8f, Width - 2, Height - 2), 17))
                using (var pen = new Pen(Glass.Accent, 2)) g.DrawPath(pen, focus);
            if (!swatch) TextRenderer.DrawText(g, Text, Font, Rectangle.Round(rect), !Enabled ? Color.FromArgb(139, 141, 158) : Primary ? Color.White : Glass.Ink, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    sealed class GlassSlider : Control
    {
        int value = 100;
        public event EventHandler ValueChanged;
        public int Value { get { return value; } set { int next = Math.Max(1, Math.Min(100, value)); if (this.value == next) return; this.value = next; Invalidate(); if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); } }
        public GlassSlider()
        {
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
            BackColor = Color.Transparent; Height = 42; TabStop = true; AccessibleRole = AccessibleRole.Slider; AccessibleName = "Brightness";
        }
        // Raised when the user finishes adjusting (mouse released or key released), not on every step.
        public event EventHandler Committed;
        bool keyAdjusting;
        public bool Dragging { get { return Capture || keyAdjusting; } }
        internal void Commit() { if (Enabled && Committed != null) Committed(this, EventArgs.Empty); }
        static bool IsAdjustKey(Keys key) { return key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down || key == Keys.PageUp || key == Keys.PageDown || key == Keys.Home || key == Keys.End; }
        void SetFromMouse(int x) { Value = 1 + (int)Math.Round((x - 14) * 99.0 / Math.Max(1, Width - 28)); }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button != MouseButtons.Left) return; Focus(); Capture = true; SetFromMouse(e.X); }
        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture) SetFromMouse(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { bool wasDragging = Capture; Capture = false; base.OnMouseUp(e); if (wasDragging) Commit(); }
        protected override bool IsInputKey(Keys keyData) { return IsAdjustKey(keyData) || base.IsInputKey(keyData); }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down) Value--; else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up) Value++;
            else if (e.KeyCode == Keys.PageDown) Value -= 10; else if (e.KeyCode == Keys.PageUp) Value += 10;
            else if (e.KeyCode == Keys.Home) Value = 1; else if (e.KeyCode == Keys.End) Value = 100; else return;
            e.Handled = true; keyAdjusting = true;
        }
        protected override void OnKeyUp(KeyEventArgs e) { base.OnKeyUp(e); if (IsAdjustKey(e.KeyCode)) { e.Handled = true; keyAdjusting = false; Commit(); } }
        protected override void OnLostFocus(EventArgs e) { keyAdjusting = false; base.OnLostFocus(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = 14 + (Width - 28) * (Value - 1) / 99f;
            using (var path = Glass.Round(new RectangleF(12, 17, Width - 24, 8), 4))
            using (var fill = new SolidBrush(Color.FromArgb(220, 220, 229))) g.FillPath(fill, path);
            using (var path = Glass.Round(new RectangleF(12, 17, Math.Max(8, x - 10), 8), 4))
            using (var fill = new SolidBrush(Enabled ? Glass.Accent : Color.FromArgb(185, 185, 198))) g.FillPath(fill, path);
            using (var shadow = new SolidBrush(Color.FromArgb(22, 62, 48, 101))) g.FillEllipse(shadow, x - 13, 10, 28, 28);
            using (var fill = new LinearGradientBrush(new RectangleF(x - 12, 8, 24, 24), Color.White, Color.FromArgb(237, 234, 249), 90)) g.FillEllipse(fill, x - 12, 8, 24, 24);
            using (var pen = new Pen(Focused ? Glass.Accent : Color.White, 2)) g.DrawEllipse(pen, x - 12, 8, 24, 24);
        }
    }

    sealed class LightList : ListBox
    {
        string favoriteId = "";
        public string FavoriteId { get { return favoriteId; } set { favoriteId = value ?? ""; Invalidate(); } }
        public LightList()
        {
            DrawMode = DrawMode.OwnerDrawFixed; ItemHeight = 72; BorderStyle = BorderStyle.None; IntegralHeight = false;
            SelectionMode = SelectionMode.MultiExtended;
            BackColor = Color.FromArgb(239, 240, 249); Font = new Font("Segoe UI", 10); AccessibleName = "Lights";
        }
        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= Items.Count) return;
            var light = (Light)Items[e.Index]; var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var brush = new SolidBrush(BackColor)) g.FillRectangle(brush, e.Bounds);
            bool active = (e.State & DrawItemState.Selected) != 0;
            var rect = new RectangleF(e.Bounds.X + 2, e.Bounds.Y + 3, e.Bounds.Width - 5, e.Bounds.Height - 7);
            if (active)
                using (var path = Glass.Round(rect, 17))
                using (var brush = new LinearGradientBrush(rect, Color.FromArgb(255, 255, 255), Color.FromArgb(229, 224, 250), 25))
                using (var border = new Pen(Color.White, 1.5f)) { g.FillPath(brush, path); g.DrawPath(border, path); }
            bool on = Convert.ToBoolean(light.State["on"]);
            Color tint = !light.Reachable ? Color.FromArgb(190, 192, 207) : on ? Color.FromArgb(239, 186, 109) : Color.FromArgb(165, 166, 191);
            using (var brush = new SolidBrush(Color.FromArgb(45, tint))) g.FillEllipse(brush, rect.X + 12, rect.Y + 12, 38, 38);
            using (var pen = new Pen(tint, 2)) { g.DrawEllipse(pen, rect.X + 24, rect.Y + 20, 14, 16); g.DrawLine(pen, rect.X + 27, rect.Y + 39, rect.X + 35, rect.Y + 39); }
            bool favorite = light.Id == favoriteId;
            int starSpace = favorite ? 26 : 0;
            using (var font = new Font("Segoe UI", 10, active ? FontStyle.Bold : FontStyle.Regular)) TextRenderer.DrawText(g, light.Name, font, new Rectangle((int)rect.X + 61, (int)rect.Y + 10, (int)rect.Width - 68 - starSpace, 24), Glass.Ink, TextFormatFlags.EndEllipsis);
            if (favorite)
                using (var font = new Font("Segoe UI Symbol", 11)) TextRenderer.DrawText(g, "★", font, new Rectangle((int)rect.Right - 32, (int)rect.Y + 8, 26, 26), Color.FromArgb(232, 164, 60), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            string detail = !light.Reachable ? "Unreachable" : on ? "On" : "Off";
            if (light.Reachable && on && light.State.ContainsKey("bri")) detail += "  /  " + Math.Round(Convert.ToInt32(light.State["bri"]) * 100.0 / 254) + "%";
            using (var font = new Font("Segoe UI", 8.5f)) TextRenderer.DrawText(g, detail, font, new Rectangle((int)rect.X + 61, (int)rect.Y + 34, (int)rect.Width - 68, 21), Glass.Muted, TextFormatFlags.EndEllipsis);
            if ((e.State & DrawItemState.Focus) != 0) ControlPaint.DrawFocusRectangle(g, Rectangle.Round(rect), Glass.Accent, BackColor);
        }
    }

    sealed class LightOrb : Control
    {
        public Color Tint = Color.FromArgb(188, 157, 240);
        public bool Lit = true;
        public LightOrb() { SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true); BackColor = Color.Transparent; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float d = Math.Min(Width, Height) - 38, x = (Width - d) / 2, y = (Height - d) / 2;
            Color tint = Lit ? Tint : Color.FromArgb(174, 182, 205);
            Glass.Glow(g, new RectangleF(x - 24, y - 24, d + 48, d + 48), Color.FromArgb(Lit ? 125 : 40, tint));
            using (var pen = new Pen(Color.FromArgb(210, Color.White), 1)) g.DrawEllipse(pen, x - 10, y - 10, d + 20, d + 20);
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(x, y, d, d);
                using (var brush = new PathGradientBrush(path))
                {
                    brush.CenterPoint = new PointF(x + d * .32f, y + d * .25f);
                    brush.CenterColor = Color.FromArgb(255, 253, 250); brush.SurroundColors = new[] { tint }; g.FillEllipse(brush, x, y, d, d);
                }
            }
            using (var pen = new Pen(Color.FromArgb(240, Color.White), 2)) g.DrawArc(pen, x + 3, y + 3, d - 6, d - 6, 192, 110);
            using (var fill = new SolidBrush(Color.FromArgb(95, Color.White))) g.FillEllipse(fill, x + d * .21f, y + d * .14f, d * .3f, d * .12f);
        }
    }
}
