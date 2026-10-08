using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Windows.Forms;

namespace HueReka
{
    // Modal editor for a custom palette. It edits a copy; Result holds the palette after DialogResult.OK.
    sealed class PaletteEditor : Form
    {
        readonly TextBox name = new TextBox { Font = new Font("Segoe UI", 11), AccessibleName = "Palette name", MaxLength = Palette.MaxNameLength + 10 };
        readonly ListBox entries = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 40, BorderStyle = BorderStyle.None, IntegralHeight = false, BackColor = Color.FromArgb(246, 245, 252), AccessibleName = "Colors and whites" };
        readonly Label problem = new Label { AutoSize = false, ForeColor = Glass.Muted, BackColor = Color.Transparent, Font = new Font("Segoe UI", 9) };
        readonly List<PaletteEntry> items;
        readonly List<string> taken;
        readonly GlassButton addColor, addWarm, addNeutral, addCool, remove, moveUp, moveDown, save;
        public Palette Result { get; private set; }

        public PaletteEditor(Palette existing, IEnumerable<string> takenNames)
        {
            Text = existing == null ? "New palette" : "Edit palette";
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = MinimizeBox = false; ShowInTaskbar = false; StartPosition = FormStartPosition.CenterParent;
            AutoScaleDimensions = new SizeF(96, 96); AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(520, 520); Font = new Font("Segoe UI", 10); BackColor = Color.FromArgb(239, 237, 249); ForeColor = Glass.Ink;
            taken = takenNames.ToList();
            var source = existing == null ? new Palette() : existing.Copy();
            items = (source.Entries ?? new List<PaletteEntry>()).Where(entry => entry != null).ToList();
            AddLabel("NAME", 24, 20); name.SetBounds(24, 44, 300, 28); name.Text = source.Name; Controls.Add(name);
            AddLabel("COLORS AND WHITES", 24, 88); entries.SetBounds(24, 112, 300, 280); Controls.Add(entries);
            addColor = AddButton("Add color...", 112, PickColor);
            addWarm = AddButton("Warm white", 158, () => AddEntry(PaletteEntry.White(2200)));
            addNeutral = AddButton("Neutral white", 204, () => AddEntry(PaletteEntry.White(4000)));
            addCool = AddButton("Cool white", 250, () => AddEntry(PaletteEntry.White(6500)));
            moveUp = AddButton("Move up", 304, () => Move(-1));
            moveDown = AddButton("Move down", 350, () => Move(1));
            remove = AddButton("Remove", 396, Remove);
            problem.SetBounds(24, 404, 300, 44); Controls.Add(problem);
            save = AddButton("Save", 466, Save); save.SetBounds(300, 466, 100, 40); save.Primary = true;
            var cancel = AddButton("Cancel", 466, Close); cancel.SetBounds(406, 466, 100, 40); cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = save; CancelButton = cancel;
            entries.DrawItem += DrawEntry;
            entries.SelectedIndexChanged += (sender, args) => UpdateState();
            name.TextChanged += (sender, args) => UpdateState();
            ShowItems(items.Count > 0 ? 0 : -1);
        }

        internal void AddEntry(PaletteEntry entry)
        {
            if (entry == null || items.Count >= Palette.MaxEntries) return;
            items.Add(entry); ShowItems(items.Count - 1);
        }

        internal void Save()
        {
            var candidate = Candidate();
            if (candidate.Problem(taken) != null) return;
            // Setting DialogResult closes a modal dialog; Close() would dispose an editor that was never shown (tests).
            Result = candidate; DialogResult = DialogResult.OK;
        }

        void PickColor()
        {
            using (var dialog = new ColorDialog { FullOpen = true })
                if (dialog.ShowDialog(this) == DialogResult.OK) AddEntry(PaletteEntry.Rgb(dialog.Color.R, dialog.Color.G, dialog.Color.B));
        }

        void Remove()
        {
            int index = entries.SelectedIndex;
            if (index < 0) return;
            items.RemoveAt(index); ShowItems(Math.Min(index, items.Count - 1));
        }

        new void Move(int delta) // hides the inherited Move event on purpose; the name matches the Move up/down buttons
        {
            int from = entries.SelectedIndex, to = from + delta;
            if (from < 0 || to < 0 || to >= items.Count) return;
            var moved = items[from]; items.RemoveAt(from); items.Insert(to, moved); ShowItems(to);
        }

        Palette Candidate() { return new Palette((name.Text ?? "").Trim(), items.ToArray()); }

        void ShowItems(int select)
        {
            entries.BeginUpdate(); entries.Items.Clear(); entries.Items.AddRange(items.Cast<object>().ToArray());
            if (select >= 0 && select < items.Count) entries.SelectedIndex = select;
            entries.EndUpdate(); UpdateState();
        }

        void UpdateState()
        {
            string reason = Candidate().Problem(taken);
            problem.Text = reason ?? ""; save.Enabled = reason == null;
            addColor.Enabled = addWarm.Enabled = addNeutral.Enabled = addCool.Enabled = items.Count < Palette.MaxEntries;
            int index = entries.SelectedIndex;
            remove.Enabled = index >= 0; moveUp.Enabled = index > 0; moveDown.Enabled = index >= 0 && index < items.Count - 1;
        }

        void DrawEntry(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= items.Count) return;
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            bool active = (e.State & DrawItemState.Selected) != 0;
            using (var back = new SolidBrush(active ? Color.FromArgb(229, 224, 250) : entries.BackColor)) g.FillRectangle(back, e.Bounds);
            using (var fill = new SolidBrush(items[e.Index].PreviewColor())) g.FillEllipse(fill, e.Bounds.X + 10, e.Bounds.Y + 7, 26, 26);
            using (var pen = new Pen(Color.White, 1.5f)) g.DrawEllipse(pen, e.Bounds.X + 10, e.Bounds.Y + 7, 26, 26);
            TextRenderer.DrawText(g, items[e.Index].Label(), entries.Font, new Rectangle(e.Bounds.X + 46, e.Bounds.Y, e.Bounds.Width - 50, e.Bounds.Height), Glass.Ink, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }

        void AddLabel(string text, int x, int y)
        {
            Controls.Add(new Label { Text = text, Bounds = new Rectangle(x, y, 300, 22), ForeColor = Glass.Muted, BackColor = Color.Transparent, Font = new Font("Segoe UI", 9, FontStyle.Bold) });
        }

        GlassButton AddButton(string text, int y, Action click)
        {
            var button = new GlassButton { Text = text, AccessibleName = text };
            button.SetBounds(340, y, 156, 40); button.Click += (sender, args) => click();
            Controls.Add(button); return button;
        }
    }
}
