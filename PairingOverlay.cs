using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace HueReka
{
    // Covers the whole window while pairing, so the "press the bridge button" step is impossible to miss.
    // Stays up until the bridge pairs, the user cancels, or time runs out (then offers to try again).
    sealed class PairingOverlay : Control, IGlassSurface
    {
        public enum Stage { Waiting, TimedOut, Paired }

        const int CardWidth = 540, CardHeight = 440;
        static readonly Color Veil = Color.FromArgb(120, 37, 40, 67);
        static readonly Color Success = Color.FromArgb(62, 163, 124);
        readonly GlassButton cancel = new GlassButton { Text = "Cancel", AccessibleName = "Cancel pairing" };
        readonly GlassButton retry = new GlassButton { Text = "Try again", AccessibleName = "Try pairing again", Primary = true };
        readonly GlassButton notNow = new GlassButton { Text = "Not now", AccessibleName = "Stop pairing for now" };
        readonly System.Windows.Forms.Timer animation = new System.Windows.Forms.Timer { Interval = 40 };
        readonly Font titleFont = new Font("Segoe UI", 18, FontStyle.Bold);
        readonly Font bodyFont = new Font("Segoe UI", 10.5f);
        readonly Font footerFont = new Font("Segoe UI", 9);
        Bitmap frosted;
        string address = "";
        TimeSpan timeout;
        DateTime started, deadline;
        TaskCompletionSource<bool> choice;

        public Stage Current { get; private set; }
        public event EventHandler CancelRequested;

        public PairingOverlay()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true; Visible = false; Dock = DockStyle.Fill;
            AccessibleRole = AccessibleRole.Dialog; AccessibleName = "Pair with your Hue Bridge";
            Controls.Add(cancel); Controls.Add(retry); Controls.Add(notNow);
            cancel.Click += (sender, args) => { if (CancelRequested != null) CancelRequested(this, EventArgs.Empty); };
            retry.Click += (sender, args) => Choose(true);
            notNow.Click += (sender, args) => Choose(false);
            animation.Tick += (sender, args) => Invalidate(Card);
        }

        Rectangle Card
        {
            get
            {
                int width = Math.Min(CardWidth, Width - 32), height = Math.Min(CardHeight, Height - 32);
                return new Rectangle((Width - width) / 2, (Height - height) / 2, width, height);
            }
        }

        int SecondsLeft { get { return Math.Max(0, (int)Math.Ceiling((deadline - DateTime.UtcNow).TotalSeconds)); } }

        public void ShowWaiting(Bitmap backdrop, string bridgeAddress, TimeSpan waitTime)
        {
            SetBackdrop(backdrop);
            address = bridgeAddress;
            Visible = true; BringToFront();
            Restart(waitTime);
            Focus();
        }

        public void Restart(TimeSpan waitTime)
        {
            timeout = waitTime; started = DateTime.UtcNow; deadline = started + waitTime;
            SetStage(Stage.Waiting);
            animation.Start();
            Focus();
        }

        public void ShowPaired() { SetStage(Stage.Paired); }

        // Shows the timed-out card and completes with true for "Try again", false for "Not now".
        public Task<bool> AskRetry()
        {
            Choose(false);
            choice = new TaskCompletionSource<bool>();
            SetStage(Stage.TimedOut);
            retry.Focus();
            return choice.Task;
        }

        // Escape or closing the window: cancel while waiting, or decline to retry.
        public void Dismiss()
        {
            if (Current == Stage.TimedOut) Choose(false);
            else if (Current == Stage.Waiting && CancelRequested != null) CancelRequested(this, EventArgs.Empty);
        }

        public void Close()
        {
            animation.Stop();
            Choose(false);
            Visible = false;
            SetBackdrop(null);
        }

        void Choose(bool tryAgain)
        {
            var pending = choice;
            choice = null;
            if (pending != null) pending.TrySetResult(tryAgain);
        }

        void SetStage(Stage stage)
        {
            Current = stage;
            cancel.Visible = stage == Stage.Waiting;
            retry.Visible = notNow.Visible = stage == Stage.TimedOut;
            if (stage != Stage.Waiting) animation.Stop();
            LayoutButtons();
            Invalidate(true);
        }

        void SetBackdrop(Bitmap backdrop)
        {
            if (frosted != null) { frosted.Dispose(); frosted = null; }
            if (backdrop == null) return;
            using (backdrop) frosted = Frost(backdrop);
        }

        // Cheap frosted-glass blur: shrink the snapshot, scale it back up smoothly, then dim it.
        static Bitmap Frost(Bitmap source)
        {
            int width = Math.Max(1, source.Width / 12), height = Math.Max(1, source.Height / 12);
            var result = new Bitmap(source.Width, source.Height);
            using (var small = new Bitmap(width, height))
            using (var edges = new ImageAttributes())
            {
                edges.SetWrapMode(WrapMode.TileFlipXY);
                using (var g = Graphics.FromImage(small))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, source.Width, source.Height, GraphicsUnit.Pixel, edges);
                }
                using (var g = Graphics.FromImage(result))
                using (var veil = new SolidBrush(Veil))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.DrawImage(small, new Rectangle(0, 0, source.Width, source.Height), 0, 0, width, height, GraphicsUnit.Pixel, edges);
                    g.FillRectangle(veil, 0, 0, source.Width, source.Height);
                }
            }
            return result;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutButtons(); }

        void LayoutButtons()
        {
            var card = Card;
            int y = card.Bottom - 74, center = card.X + card.Width / 2;
            cancel.SetBounds(center - 65, y, 130, 44);
            retry.SetBounds(center - 138, y, 140, 44);
            notNow.SetBounds(center + 10, y, 128, 44);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Enter && Current == Stage.TimedOut) { e.Handled = true; Choose(true); }
        }

        public void PaintSurface(Graphics g)
        {
            if (Width <= 0 || Height <= 0) return;
            if (frosted != null) g.DrawImage(frosted, new Rectangle(0, 0, Width, Height));
            else using (var fill = new SolidBrush(Color.FromArgb(96, 99, 128))) g.FillRectangle(fill, ClientRectangle);
            var card = Card;
            if (card.Width < 40 || card.Height < 40) return;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // Soft shadow: stacked, slightly larger translucent shapes fade out instead of a hard edge.
            using (var brush = new SolidBrush(Color.FromArgb(9, 20, 18, 45)))
                for (int spread = 14; spread >= 1; spread -= 1)
                    using (var shadow = Glass.Round(new RectangleF(card.X - spread, card.Y + 10 - spread, card.Width + spread * 2, card.Height + spread * 2), 32 + spread))
                        g.FillPath(brush, shadow);
            using (var path = Glass.Round(card, 32))
            using (var fill = new LinearGradientBrush(card, Color.FromArgb(252, 252, 255), Color.FromArgb(241, 238, 252), 90))
            using (var border = new Pen(Color.White, 1.5f)) { g.FillPath(fill, path); g.DrawPath(border, path); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            PaintSurface(g);
            var card = Card;
            if (card.Width < 200 || card.Height < 300) return;
            DrawBridge(g, new PointF(card.X + card.Width / 2f, card.Y + 118));
            string title, body, footer;
            if (Current == Stage.Waiting)
            {
                title = "Press the button on your Hue Bridge";
                body = "It's the large round button on top of the bridge.\nHueReka is waiting and will finish pairing by itself.";
                footer = SecondsLeft + " seconds left  /  Bridge at " + address;
            }
            else if (Current == Stage.TimedOut)
            {
                title = "No button press yet";
                body = "Walk over to your Hue Bridge, then choose Try again.\nYou'll have another " + Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)) + " seconds to press its button.";
                footer = "Bridge at " + address;
            }
            else
            {
                title = "Paired!";
                body = "Your bridge said hello.\nLoading your lights...";
                footer = "";
            }
            const TextFormatFlags centered = TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, title, titleFont, new Rectangle(card.X + 24, card.Y + 218, card.Width - 48, 40), Glass.Ink, centered | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, body, bodyFont, new Rectangle(card.X + 36, card.Y + 264, card.Width - 72, 50), Glass.Muted, centered);
            TextRenderer.DrawText(g, footer, footerFont, new Rectangle(card.X + 24, card.Y + 322, card.Width - 48, 22), Glass.Muted, centered);
        }

        void DrawBridge(Graphics g, PointF c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var body = new RectangleF(c.X - 70, c.Y - 70, 140, 140);
            using (var brush = new SolidBrush(Color.FromArgb(28, 40, 30, 90))) g.FillEllipse(brush, c.X - 64, c.Y + 58, 128, 22);
            using (var path = Glass.Round(body, 34))
            using (var fill = new LinearGradientBrush(body, Color.White, Color.FromArgb(232, 231, 242), 90))
            using (var border = new Pen(Color.FromArgb(214, 214, 230), 1.5f)) { g.FillPath(fill, path); g.DrawPath(border, path); }

            using (var track = new Pen(Color.FromArgb(226, 223, 242), 5)) g.DrawEllipse(track, c.X - 46, c.Y - 46, 92, 92);
            if (Current == Stage.Waiting)
            {
                double elapsed = (DateTime.UtcNow - started).TotalSeconds;
                for (int wave = 0; wave < 2; wave++)
                {
                    double t = (elapsed / 1.6 + wave * 0.5) % 1.0;
                    float radius = 34 + (float)t * 62;
                    using (var pulse = new Pen(Color.FromArgb((int)((1 - t) * 150), Glass.Accent), 3)) g.DrawEllipse(pulse, c.X - radius, c.Y - radius, radius * 2, radius * 2);
                }
                float remaining = timeout.TotalMilliseconds <= 0 ? 0 : (float)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds / timeout.TotalMilliseconds);
                using (var ring = new Pen(Glass.Accent, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    if (remaining > 0) g.DrawArc(ring, c.X - 46, c.Y - 46, 92, 92, -90, 360 * remaining);
            }
            var button = new RectangleF(c.X - 32, c.Y - 32, 64, 64);
            Color top = Current == Stage.Paired ? ControlPaint.Light(Success) : Color.FromArgb(252, 252, 255);
            Color bottom = Current == Stage.Paired ? Success : Current == Stage.TimedOut ? Color.FromArgb(214, 214, 226) : Color.FromArgb(226, 224, 240);
            using (var fill = new LinearGradientBrush(button, top, bottom, 90)) g.FillEllipse(fill, button);
            using (var border = new Pen(Color.FromArgb(205, 206, 224), 1.5f)) g.DrawEllipse(border, button);
            if (Current == Stage.Paired)
                using (var check = new Pen(Color.White, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(check, new[] { new PointF(c.X - 14, c.Y + 1), new PointF(c.X - 4, c.Y + 11), new PointF(c.X + 15, c.Y - 11) });
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                animation.Dispose(); titleFont.Dispose(); bodyFont.Dispose(); footerFont.Dispose();
                if (frosted != null) frosted.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
