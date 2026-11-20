using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>Shared building blocks for the SSH based views (lists, menus, headers).</summary>
    static class Ui
    {
        public static ListView List(string[] cols, int[] widths, bool multi = false, bool sortable = false)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true,
                HeaderStyle = sortable ? ColumnHeaderStyle.Clickable : ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.F(9.5f), OwnerDraw = true,
                MultiSelect = multi, HideSelection = false, SmallImageList = new ImageList { ImageSize = new Size(1, Theme.Px(28)) }
            };
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(lv, true, null);
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
            lv.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), Theme.F(8f, FontStyle.Bold),
                    new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), Theme.Muted,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : 0));
            };
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var tag = e.Item.Tag as RowTag;
                var bg = e.Item.Selected ? Theme.CardHi : Theme.Card;
                if (tag != null && tag.Data is bool && (bool)tag.Data) bg = Theme.Mix(bg, Theme.Red, e.Item.Selected ? 0.16 : 0.09);
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
                var bounds = e.Bounds;
                if (e.ColumnIndex == 0 && tag != null && tag.Icon != null)
                {
                    g.DrawImage(tag.Icon, bounds.X + 6, bounds.Y + (bounds.Height - 18) / 2, 18, 18);
                    bounds = new Rectangle(bounds.X + 24, bounds.Y, bounds.Width - 24, bounds.Height);
                }
                else if (e.ColumnIndex == 0 && tag != null && tag.Dot != Color.Empty)
                {
                    using (var b = new SolidBrush(tag.Dot)) g.FillEllipse(b, bounds.X + 10, bounds.Y + bounds.Height / 2 - 4, 8, 8);
                    bounds = new Rectangle(bounds.X + 20, bounds.Y, bounds.Width - 20, bounds.Height);
                }
                var c = e.SubItem.ForeColor == lv.ForeColor ? (e.ColumnIndex == 0 ? Theme.Text : Theme.Text2) : e.SubItem.ForeColor;
                var right = lv.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right;
                TextRenderer.DrawText(g, e.SubItem.Text, e.ColumnIndex == 0 ? Theme.F(9.5f, FontStyle.Bold) : lv.Font,
                    new Rectangle(bounds.X + 8, bounds.Y, bounds.Width - 14, bounds.Height), c,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | (right ? TextFormatFlags.Right : 0));
            };
            UiX.FitColumns(lv);

            return lv;
        }

        public static ListViewItem Row(RowTag tag, params string[] cells)
        {
            var it = new ListViewItem(cells[0] ?? "") { Tag = tag, UseItemStyleForSubItems = false };
            for (int i = 1; i < cells.Length; i++) it.SubItems.Add(cells[i] ?? "");
            return it;
        }

        public static void Fill(ListView lv, List<ListViewItem> rows)
        {
            var sel = new HashSet<string>(lv.SelectedItems.Cast<ListViewItem>().Select(i => ((RowTag)i.Tag).Key));
            int top = lv.TopItem != null ? lv.TopItem.Index : 0;
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.Items.AddRange(rows.ToArray());
            if (sel.Count > 0)
                foreach (ListViewItem it in lv.Items)
                    if (sel.Contains(((RowTag)it.Tag).Key)) it.Selected = true;
            lv.EndUpdate();
            if (top > 0 && top < lv.Items.Count) try { lv.TopItem = lv.Items[top]; } catch { }
        }

        public static string Sel(ListView lv) { return lv.SelectedItems.Count > 0 ? ((RowTag)lv.SelectedItems[0].Tag).Key : null; }
        public static T SelData<T>(ListView lv) where T : class { return lv.SelectedItems.Count > 0 ? ((RowTag)lv.SelectedItems[0].Tag).Data as T : null; }

        public static FlatBtn Btn(string text, Color c, int h = 36)
        {
            return new FlatBtn(text, c) { Height = h, Margin = new Padding(0, 0, 8, 0) };
        }

        /// <summary>Toolbar that wraps onto a second line instead of cutting buttons off.</summary>
        public static FlowLayoutPanel Bar(int height, params Control[] items)
        {
            var bar = new WrapBar { Dock = DockStyle.Top, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 8) };
            foreach (var c in items) { c.Margin = new Padding(0, c.Margin.Top == 3 ? 0 : c.Margin.Top, 8, 8); bar.Controls.Add(c); }
            return bar;
        }

        /// <summary>Standard page header (title, subtitle, optional tabs).</summary>
        public static Panel Header(string title, out Label sub, string[] tabNames, out TabBtn[] tabs, Action<int> onTab)
        {
            var header = new Panel { Dock = DockStyle.Top, Height = tabNames == null ? 70 : 104, BackColor = Theme.Bg };
            var t = Theme.Lbl(title, Theme.F(17f, FontStyle.Bold), Theme.Text, Theme.Bg);
            t.Location = new Point(0, 0);
            sub = Theme.Lbl("", Theme.F(9.25f), Theme.Muted, Theme.Bg);
            sub.Location = new Point(2, 36);
            sub.AutoSize = false;
            sub.AutoEllipsis = true;
            sub.Size = new Size(900, 20);
            sub.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            var subRef = sub;
            header.Resize += (s, e) => subRef.Width = Math.Max(100, header.Width - subRef.Left - 4);
            header.Controls.Add(t);
            header.Controls.Add(sub);
            tabs = null;
            if (tabNames != null)
            {
                tabs = new TabBtn[tabNames.Length];
                int x = 0;
                for (int i = 0; i < tabNames.Length; i++)
                {
                    int idx = i;
                    tabs[i] = new TabBtn(tabNames[i]) { Location = new Point(x, 62) };
                    x = tabs[i].Right + 4;
                    tabs[i].Click += (s, e) => onTab(idx);
                    header.Controls.Add(tabs[i]);
                }
            }
            header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border });
            return header;
        }

        public static ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false, Font = Theme.F(9.5f), BackColor = Theme.Card, ForeColor = Theme.Text };
            return m;
        }

        public static ToolStripMenuItem Item(this ContextMenuStrip m, string text, Action a, bool enabled = true)
        {
            var it = new ToolStripMenuItem(text) { ForeColor = Theme.Text, Enabled = enabled, Padding = new Padding(4, 3, 4, 3) };
            it.Click += (s, e) => a();
            m.Items.Add(it);
            return it;
        }

        public static void Sep(this ContextMenuStrip m) { m.Items.Add(new ToolStripSeparator()); }

        /// <summary>Shows a dropdown menu below a button.</summary>
        public static void DropDown(Control anchor, ContextMenuStrip m) { m.Show(anchor, new Point(0, anchor.Height)); }

        public static ComboBox Combo(int width)
        {
            return new DarkCombo { Width = width, Margin = new Padding(0, 0, 8, 0) };
        }

        public static string Num(double v)
        {
            if (Math.Abs(v - Math.Round(v)) < 0.005) return Math.Round(v).ToString("#,0", CultureInfo.GetCultureInfo("de-DE"));
            return v.ToString("#,0.00", CultureInfo.GetCultureInfo("de-DE"));
        }

        public static bool TryNum(string s, out double v)
        {
            s = (s ?? "").Trim().Replace(" ", "").Replace("_", "");
            if (s.Contains(",") && !s.Contains(".")) s = s.Replace(",", ".");
            else s = s.Replace(",", "");
            var m = 1.0;
            if (s.EndsWith("k", StringComparison.OrdinalIgnoreCase)) { m = 1e3; s = s.Substring(0, s.Length - 1); }
            else if (s.EndsWith("m", StringComparison.OrdinalIgnoreCase)) { m = 1e6; s = s.Substring(0, s.Length - 1); }
            var ok = double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            v *= m;
            return ok && v >= 0 && !double.IsInfinity(v);
        }

        public static string Ago(DateTime d)
        {
            var t = DateTime.Now - d;
            if (t.TotalSeconds < 0) return d.ToString("dd.MM.yy HH:mm");
            if (t.TotalMinutes < 1) return "gerade eben";
            if (t.TotalHours < 1) return "vor " + (int)t.TotalMinutes + " Min";
            if (t.TotalDays < 1) return "vor " + (int)t.TotalHours + " Std";
            if (t.TotalDays < 30) return "vor " + (int)t.TotalDays + " T";
            return d.ToString("dd.MM.yyyy");
        }

        public static DateTime FromUnixMs(double ms) { return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime(); }
    }

    /// <summary>Dropdown that is fully painted in the dark theme (the native combo draws a white frame).</summary>
    class DarkCombo : ComboBox
    {
        bool hot;

        public DarkCombo()
        {
            DropDownStyle = ComboBoxStyle.DropDownList;
            FlatStyle = FlatStyle.Flat;
            DrawMode = DrawMode.OwnerDrawFixed;
            BackColor = Theme.Input;
            ForeColor = Theme.Text;
            Font = Theme.F(9.75f);
            ItemHeight = Theme.Px(26);
            Height = 36;
            Cursor = Cursors.Hand;
        }

        protected override void OnDrawItem(DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (var b = new SolidBrush(sel ? Theme.CardHi : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height),
                Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; Invalidate(); }
        protected override void OnSelectedIndexChanged(EventArgs e) { base.OnSelectedIndexChanged(e); Invalidate(); }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x000F && IsHandleCreated) // WM_PAINT: paint over the native frame
            {
                using (var g = Graphics.FromHwnd(Handle))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    var parentBack = Parent != null ? Parent.BackColor : Theme.Bg;
                    if (parentBack.A < 255) parentBack = Theme.Card;
                    g.Clear(parentBack);
                    var r = new Rectangle(0, 0, Width - 1, Height - 1);
                    using (var p = Theme.RoundRect(r, Theme.Px(8)))
                    {
                        using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, p);
                        using (var pen = new Pen(Focused || DroppedDown ? Theme.Accent : hot ? Theme.Mix(Theme.Border, Theme.Text2, 0.3) : Theme.Border)) g.DrawPath(pen, p);
                    }
                    var text = SelectedIndex >= 0 ? GetItemText(SelectedItem) : "";
                    TextRenderer.DrawText(g, text, Font, new Rectangle(Theme.Px(10), 0, Width - Theme.Px(34), Height), Enabled ? Theme.Text : Theme.Muted,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, "", Icons.Font(8f), new Rectangle(Width - Theme.Px(28), 0, Theme.Px(22), Height), Theme.Muted,
                        TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
                }
            }
        }
    }
    /// <summary>FlowLayoutPanel docked at the top whose height follows its wrapped content.</summary>
    class WrapBar : FlowLayoutPanel
    {
        public WrapBar() { WrapContents = true; Height = 44; }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int bottom = 0;
            foreach (Control c in Controls) if (c.Visible) bottom = Math.Max(bottom, c.Bottom + c.Margin.Bottom);
            int h = Math.Max(Theme.Px(10), bottom + Padding.Bottom - Theme.Px(8));
            if (Height != h) Height = h;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); PerformLayout(); }
    }

    class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e) { e.ArrowColor = Theme.Text2; base.OnRenderArrow(e); }
    }

    class DarkColors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground { get { return Theme.Card; } }
        public override Color MenuBorder { get { return Theme.Border; } }
        public override Color MenuItemBorder { get { return Theme.CardHi; } }
        public override Color MenuItemSelected { get { return Theme.CardHi; } }
        public override Color MenuItemSelectedGradientBegin { get { return Theme.CardHi; } }
        public override Color MenuItemSelectedGradientEnd { get { return Theme.CardHi; } }
        public override Color ImageMarginGradientBegin { get { return Theme.Card; } }
        public override Color ImageMarginGradientMiddle { get { return Theme.Card; } }
        public override Color ImageMarginGradientEnd { get { return Theme.Card; } }
        public override Color SeparatorDark { get { return Theme.Border; } }
        public override Color SeparatorLight { get { return Theme.Card; } }
    }

    /// <summary>A small modal form with labelled fields – used for multi-value inputs.</summary>
    class FormDialog : DarkForm
    {
        readonly List<Control> inputs = new List<Control>();
        int y = 18;

        public FormDialog(string title, string intro)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(480, 100);
            if (!string.IsNullOrEmpty(intro))
            {
                var l = Theme.Lbl(intro, Theme.F(9.5f), Theme.Text2, Theme.Bg);
                l.Location = new Point(20, y);
                l.MaximumSize = new Size(440, 0);
                Controls.Add(l);
                y += l.PreferredHeight + 14;
            }
        }

        Label Caption(string text)
        {
            var l = Theme.Lbl(text.ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Bg);
            l.Location = new Point(20, y);
            Controls.Add(l);
            y += 20;
            return l;
        }

        public Field AddText(string label, string value)
        {
            Caption(label);
            var f = new Field(false) { Location = new Point(20, y), Size = new Size(440, 40) };
            f.Box.Text = value ?? "";
            Controls.Add(f);
            inputs.Add(f);
            y += 52;
            return f;
        }

        public ComboBox AddCombo(string label, IEnumerable<string> items, string value, bool editable = false)
        {
            Caption(label);
            var c = editable ? new ComboBox { DropDownStyle = ComboBoxStyle.DropDown, FlatStyle = FlatStyle.Flat, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.F(10.5f) } : new DarkCombo();
            c.Location = new Point(20, y); c.Width = 440;
            foreach (var i in items) c.Items.Add(i);
            if (value != null) { if (c.Items.Contains(value)) c.SelectedItem = value; else if (editable) c.Text = value; }
            if (c.SelectedIndex < 0 && !editable && c.Items.Count > 0) c.SelectedIndex = 0;
            Controls.Add(c);
            inputs.Add(c);
            y += 44;
            return c;
        }

        public CheckBox AddCheck(string text, bool value)
        {
            var c = new CheckBox { Text = text, Checked = value, Location = new Point(20, y), AutoSize = true, ForeColor = Theme.Text2, BackColor = Theme.Bg };
            Controls.Add(c);
            inputs.Add(c);
            y += 32;
            return c;
        }

        public Label AddNote(string text, Color c)
        {
            var l = Theme.Lbl(text, Theme.F(9f), c, Theme.Bg);
            l.Location = new Point(20, y);
            l.MaximumSize = new Size(440, 0);
            Controls.Add(l);
            y += l.PreferredHeight + 10;
            return l;
        }

        public bool Run(IWin32Window owner, string okText = "OK")
        {
            y += 8;
            var ok = new FlatBtn(okText, Theme.Accent) { Location = new Point(460 - Math.Max(100, TextRenderer.MeasureText(okText, Theme.F(9.5f, FontStyle.Bold)).Width + 32), y) };
            ok.Width = 460 - ok.Left;
            var cancel = new FlatBtn("Abbrechen", Theme.CardHi) { Location = new Point(ok.Left - 114, y), Width = 106 };
            ok.DialogResult = DialogResult.OK;
            cancel.DialogResult = DialogResult.Cancel;
            AcceptButton = ok;
            CancelButton = cancel;
            Controls.Add(ok);
            Controls.Add(cancel);
            ClientSize = new Size(480, y + 56);
            Shown += (s, e) => { var f = inputs.OfType<Field>().FirstOrDefault(); if (f != null) { f.Box.Focus(); f.Box.SelectAll(); } };
            return ShowDialog(owner) == DialogResult.OK;
        }
    }
}
