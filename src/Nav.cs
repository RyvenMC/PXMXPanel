using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RyvenPanel
{
    static class Icons
    {
        static Font font;
        public static Font Font(float size)
        {
            var fam = FontFamily.Families.Any(f => f.Name == "Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";
            return new Font(fam, size);
        }
        public static Font Default { get { return font ?? (font = Font(11f)); } }
        public const string Network = "", Robot = "";

        public const string Server = "", Globe = "", People = "", Terminal = "", Folder = "",
            Package = "", Shield = "", Search = "", Power = "", Play = "", Stop = "",
            Refresh = "", Upload = "", Download = "", Add = "", Block = "", Chart = "",
            Settings = "", Console = "", Person = "", Coins = "", Key = "", Lock = "", Save = "";
    }

    /// <summary>Sidebar navigation entry: icon + label, animated hover/active state.</summary>
    class NavItem : Control
    {
        public readonly string Glyph;
        bool active;
        double hover, act;
        public string Badge;

        public NavItem(string glyph, string text)
        {
            Glyph = glyph;
            Text = text;
            Height = 36;
            Cursor = Cursors.Hand;
            Font = Theme.F(10f, FontStyle.Bold);
            BackColor = Theme.Side;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }

        public bool Active
        {
            get { return active; }
            set
            {
                if (active == value) return;
                active = value;
                var from = act; double to = value ? 1 : 0;
                Anim.Run("a" + GetHashCode(), 220, p => { act = from + (to - from) * p; Invalidate(); });
            }
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); var f = hover; Anim.Run("h" + GetHashCode(), 140, p => { hover = f + (1 - f) * p; Invalidate(); }); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); var f = hover; Anim.Run("h" + GetHashCode(), 180, p => { hover = f * (1 - p); Invalidate(); }); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(10, 2, Width - 20, Height - 4);
            double hl = Math.Max(act, hover * 0.5);
            if (hl > 0.01)
                using (var p = Theme.RoundRect(r, 8))
                using (var b = new SolidBrush(Theme.Mix(BackColor, Theme.CardHi, hl))) g.FillPath(b, p);
            if (act > 0.01)
            {
                int h = (int)((r.Height - 14) * act);
                using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Y, 3, r.Height), Theme.SoftGreen, Theme.Mint, 90f))
                    g.FillRectangle(b, r.X, r.Y + r.Height / 2 - h / 2, 3, Math.Max(1, h));
            }
            var ic = Theme.Mix(Theme.Muted, Theme.SoftGreen, act);
            TextRenderer.DrawText(g, Glyph, Icons.Default, new Rectangle(r.X + 12, r.Y, 22, r.Height), ic, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPadding);
            var tc = Theme.Mix(Theme.Mix(Theme.Text2, Theme.Text, hover), Theme.Text, act);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(r.X + 42, r.Y, r.Width - 46, r.Height), tc, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (!string.IsNullOrEmpty(Badge))
            {
                var sz = TextRenderer.MeasureText(Badge, Theme.F(8f, FontStyle.Bold));
                var br = new Rectangle(r.Right - sz.Width - 14, r.Y + (r.Height - 18) / 2, sz.Width + 8, 18);
                using (var p = Theme.RoundRect(br, 9)) using (var b = new SolidBrush(Theme.Alpha(Theme.Green, 50))) g.FillPath(b, p);
                TextRenderer.DrawText(g, Badge, Theme.F(8f, FontStyle.Bold), br, Theme.Green, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
        }
    }

    /// <summary>Fake search field in the sidebar that opens the command palette.</summary>
    class SearchButton : Control
    {
        double hover;
        public SearchButton()
        {
            Height = 36;
            Cursor = Cursors.Hand;
            BackColor = Theme.Side;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hover = 1; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = 0; Invalidate(); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(10, 0, Width - 21, Height - 1);
            using (var p = Theme.RoundRect(r, 9))
            {
                using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Mix(Theme.Border, Theme.Accent, hover))) g.DrawPath(pen, p);
            }
            TextRenderer.DrawText(g, Icons.Search, Icons.Font(10f), new Rectangle(r.X + 8, r.Y, 22, r.Height), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.HorizontalCenter);
            TextRenderer.DrawText(g, "Suchen …", Theme.F(9.5f), new Rectangle(r.X + 32, r.Y, r.Width - 80, r.Height), Theme.Muted, TextFormatFlags.VerticalCenter);
            var k = new Rectangle(r.Right - 52, r.Y + 8, 44, r.Height - 16);
            using (var p = Theme.RoundRect(k, 5)) using (var b = new SolidBrush(Theme.CardHi)) g.FillPath(b, p);
            TextRenderer.DrawText(g, "Strg+K", Theme.F(7.5f, FontStyle.Bold), k, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }
    }

    class PaletteItem
    {
        public string Title, Hint, Category, Glyph = Icons.Search, Keywords = "";
        public Action Run;
        public int Score;
    }

    /// <summary>Strg+K: search across pages, tabs, actions, players and servers.</summary>
    class CommandPalette : DarkForm
    {
        readonly Func<string, IEnumerable<PaletteItem>> source;
        readonly Field box;
        readonly ListBox list;
        List<PaletteItem> items = new List<PaletteItem>();

        public CommandPalette(Form owner, Func<string, IEnumerable<PaletteItem>> source)
        {
            this.source = source;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Theme.Border;
            Padding = new Padding(1);
            Size = new Size(640, 430);
            Owner = owner;
            Load += (s, e) =>
            {
                var r = owner.RectangleToScreen(owner.ClientRectangle);
                Width = Math.Min(Width, r.Width - 40);
                Height = Math.Min(Height, r.Height - 60);
                Location = new Point(r.X + (r.Width - Width) / 2, r.Y + Math.Max(20, Math.Min(r.Height / 8, r.Height - Height - 20)));
            };
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(12) };
            box = new Field(false) { Dock = DockStyle.Top, Height = 44 };
            box.Box.Font = Theme.F(12f);
            box.Placeholder = "Wohin möchtest du? Seite, Aktion, Spieler, Server …";
            var hint = Theme.Lbl("↑↓ auswählen   ·   Enter öffnen   ·   Esc schließen", Theme.F(8.5f), Theme.Muted, Theme.Card);
            hint.Dock = DockStyle.Bottom; hint.AutoSize = false; hint.Height = 22; hint.TextAlign = ContentAlignment.BottomLeft;
            list = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text,
                DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = Theme.Px(44), IntegralHeight = false
            };
            var gap = new Panel { Dock = DockStyle.Top, Height = 10, BackColor = Theme.Card };
            inner.Controls.Add(list);
            inner.Controls.Add(gap);
            inner.Controls.Add(box);
            inner.Controls.Add(hint);
            Controls.Add(inner);

            list.DrawItem += (s, e) =>
            {
                if (e.Index < 0 || e.Index >= items.Count) return;
                var it = items[e.Index];
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, e.Bounds);
                var rr = new Rectangle(e.Bounds.X, e.Bounds.Y + 2, e.Bounds.Width - 2, e.Bounds.Height - 4);
                if (sel) using (var p = Theme.RoundRect(rr, 8)) using (var b = new SolidBrush(Theme.CardHi)) g.FillPath(b, p);
                var ib = new Rectangle(rr.X + 8, rr.Y + 6, 28, rr.Height - 12);
                using (var p = Theme.RoundRect(ib, 6)) using (var b = new SolidBrush(sel ? Theme.Alpha(Theme.Accent, 90) : Theme.Input)) g.FillPath(b, p);
                TextRenderer.DrawText(g, it.Glyph, Icons.Font(10f), ib, sel ? Color.White : Theme.SoftGreen, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                var cat = it.Category ?? "";
                var csz = TextRenderer.MeasureText(cat, Theme.F(8.25f, FontStyle.Bold));
                TextRenderer.DrawText(g, it.Title, Theme.F(10.5f, FontStyle.Bold), new Rectangle(rr.X + 46, rr.Y + 3, rr.Width - 60 - csz.Width, 20), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, it.Hint ?? "", Theme.F(8.75f), new Rectangle(rr.X + 46, rr.Y + 21, rr.Width - 56, 18), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, cat.ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold), new Rectangle(rr.Right - csz.Width - 10, rr.Y + 4, csz.Width + 4, 18), Theme.Muted, TextFormatFlags.Right);
            };
            box.Box.TextChanged += (s, e) => Refill();
            box.Box.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Down) { e.SuppressKeyPress = true; if (list.SelectedIndex < items.Count - 1) list.SelectedIndex++; }
                else if (e.KeyCode == Keys.Up) { e.SuppressKeyPress = true; if (list.SelectedIndex > 0) list.SelectedIndex--; }
                else if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Execute(); }
                else if (e.KeyCode == Keys.Escape) { e.SuppressKeyPress = true; Close(); }
            };
            list.MouseClick += (s, e) => { int i = list.IndexFromPoint(e.Location); if (i >= 0) { list.SelectedIndex = i; Execute(); } };
            Deactivate += (s, e) => { if (!IsDisposed) Close(); };
            Shown += (s, e) => { box.Box.Focus(); Refill(); };
        }

        void Refill()
        {
            var q = box.Box.Text.Trim();
            items = source(q).Take(60).ToList();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (var i in items) list.Items.Add(i.Title);
            if (items.Count > 0) list.SelectedIndex = 0;
            list.EndUpdate();
        }

        void Execute()
        {
            if (list.SelectedIndex < 0 || list.SelectedIndex >= items.Count) return;
            var it = items[list.SelectedIndex];
            Close();
            if (it.Run != null) it.Run();
        }

        /// <summary>Simple ranking: prefix of title &gt; word start &gt; contained in title &gt; contained in hint/keywords; all words must match.</summary>
        public static IEnumerable<PaletteItem> Rank(IEnumerable<PaletteItem> all, string q)
        {
            if (string.IsNullOrWhiteSpace(q)) return all;
            var words = q.ToLowerInvariant().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var res = new List<PaletteItem>();
            foreach (var it in all)
            {
                var t = it.Title.ToLowerInvariant();
                var hay = t + " " + (it.Hint ?? "").ToLowerInvariant() + " " + (it.Category ?? "").ToLowerInvariant() + " " + it.Keywords.ToLowerInvariant();
                if (!words.All(w => hay.Contains(w))) continue;
                int s = 0;
                foreach (var w in words)
                {
                    if (t.StartsWith(w)) s += 100;
                    else if (t.Contains(" " + w) || t.Contains("›" + w) || t.Contains("› " + w)) s += 60;
                    else if (t.Contains(w)) s += 40;
                    else s += 10;
                }
                it.Score = s;
                res.Add(it);
            }
            return res.OrderByDescending(x => x.Score).ThenBy(x => x.Title.Length);
        }
    }

    /// <summary>Lays out StatCards in equal columns that adapt to the available width (no horizontal overflow).</summary>
    class CardGrid : Panel
    {
        public int MinCardWidth = 170, CardHeight = 100, Gap = 12;

        public CardGrid()
        {
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var cards = Controls.Cast<Control>().Where(c => c.Visible).ToList();
            if (cards.Count == 0 || Width < 50) return;
            int gap = Theme.Px(Gap), minW = Theme.Px(MinCardWidth), ch = Theme.Px(CardHeight);
            int cols = Math.Max(1, Math.Min(cards.Count, (Width + gap) / (minW + gap)));
            if (cols < cards.Count && cards.Count % cols != 0) { int rows = (cards.Count + cols - 1) / cols; cols = (cards.Count + rows - 1) / rows; }
            int w = (Width - gap * (cols - 1)) / cols;
            for (int i = 0; i < cards.Count; i++)
                cards[i].SetBounds((i % cols) * (w + gap), (i / cols) * (ch + gap), w, ch);
            int h = ((cards.Count + cols - 1) / cols) * (ch + gap);
            if (Height != h) Height = h;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); PerformLayout(); }
    }

    static class UiX
    {
        static readonly ToolTip tip = new ToolTip { InitialDelay = 400, ReshowDelay = 100, AutoPopDelay = 12000 };

        public static T Tip<T>(this T c, string text) where T : Control { tip.SetToolTip(c, text); return c; }

        /// <summary>Keeps a SplitContainer at a ratio until the user drags the splitter.</summary>
        public static void Ratio(SplitContainer s, double frac, int min1, int min2)
        {
            min1 = Theme.Px(min1); min2 = Theme.Px(min2);   // once – scaling twice squeezed split views on 125/150 % displays
            bool user = false, setting = false;
            Action apply = () =>
            {
                if (user || s.Width < min1 + min2 + 20) return;
                int d = (int)(s.Width * frac);
                d = Math.Max(min1, Math.Min(s.Width - min2 - s.SplitterWidth, d));
                if (Math.Abs(s.SplitterDistance - d) < 2) return;
                setting = true;
                try { s.SplitterDistance = d; } catch { }
                setting = false;
            };
            s.MouseDown += (a, b) => user = true;
            s.SizeChanged += (a, b) => apply();
            s.VisibleChanged += (a, b) => apply();
            s.HandleCreated += (a, b) => apply();
        }

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr h, string app, string id);

        /// <summary>Dark native scrollbars (Windows 10 1809+), applied to the whole control tree.</summary>
        public static void DarkScroll(Control root)
        {
            Action<Control> apply = null;
            apply = c =>
            {
                if (c is ListView || c is TextBoxBase || c is ListBox || c is TreeView || (c is ScrollableControl && ((ScrollableControl)c).AutoScroll))
                {
                    var theme = c is ListView ? "DarkMode_ItemsView" : "DarkMode_Explorer";
                    if (c.IsHandleCreated) try { SetWindowTheme(c.Handle, theme, null); } catch { }
                    else c.HandleCreated += (s, e) => { try { SetWindowTheme(c.Handle, theme, null); } catch { } };
                }
                foreach (Control k in c.Controls) apply(k);
            };
            apply(root);
        }

        /// <summary>
        /// Columns keep their proportions and fill the list. They never shrink below what their header needs
        /// (at least 70 % of the designed width) – a narrow window gets a horizontal scrollbar instead of "C…" columns.
        /// </summary>
        public static void FitColumns(ListView lv)
        {
            int[] baseW = null, minW = null;
            bool busy = false;
            lv.Resize += (s, e) =>
            {
                if (busy || lv.Columns.Count == 0 || lv.ClientSize.Width < 50) return;
                if (baseW == null)
                {
                    baseW = lv.Columns.Cast<ColumnHeader>().Select(c => c.Width).ToArray();
                    var hf = Theme.F(8f, FontStyle.Bold);
                    // the first column (usually the name) keeps its full width, the others may shrink to 70 %
                    minW = lv.Columns.Cast<ColumnHeader>().Select((c, i) => i == 0 ? baseW[0] :
                        Math.Min(baseW[i], Math.Max((int)(baseW[i] * 0.7), TextRenderer.MeasureText(c.Text.ToUpperInvariant(), hf).Width + Theme.Px(22)))).ToArray();
                }
                busy = true;
                int total = baseW.Sum(), avail = lv.ClientSize.Width - 2, used = 0;
                bool changed = false;
                for (int i = 0; i < baseW.Length; i++)
                {
                    int w = i == baseW.Length - 1 ? Math.Max(minW[i], avail - used) : Math.Max(minW[i], baseW[i] * avail / total);
                    if (lv.Columns[i].Width != w) { lv.Columns[i].Width = w; changed = true; }
                    used += w;
                }
                busy = false;
                if (changed) lv.Invalidate();
            };
            lv.ColumnWidthChanged += (s, e) => { if (!busy) lv.Invalidate(); };
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        public static void Cue(TextBox t, string text)
        {
            Action set = () => SendMessage(t.Handle, 0x1501, (IntPtr)1, text);
            if (t.IsHandleCreated) set(); else t.HandleCreated += (s, e) => set();
        }
    }
}
