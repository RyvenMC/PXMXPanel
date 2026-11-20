using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>Flicker-free, fully double-buffered item list with animated hover/selection (replaces the native ListBox).</summary>
    class ServerList : Control
    {
        public readonly List<ServerItem> Items = new List<ServerItem>();
        public int ItemHeight = 64;
        public int ItemWidth = 230;
        public bool Horizontal;
        public Action<Graphics, ServerItem, Rectangle, int, double, double> DrawEntry; // g, item, bounds, index, hover(0..1), selected(0..1)
        public event EventHandler SelectedChanged;

        int selected = -1, hoverIdx = -1, scroll;
        readonly Dictionary<ServerItem, double> hoverAmt = new Dictionary<ServerItem, double>();
        readonly Dictionary<ServerItem, double> selAmt = new Dictionary<ServerItem, double>();

        public ServerList()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Side;
            Cursor = Cursors.Hand;
        }

        public void SetItems(IEnumerable<ServerItem> items)
        {
            var sel = SelectedItem;
            Items.Clear();
            Items.AddRange(items);
            selected = sel != null ? Items.IndexOf(sel) : -1;
            ClampScroll();
            Invalidate();
        }

        public ServerItem SelectedItem
        {
            get { return selected >= 0 && selected < Items.Count ? Items[selected] : null; }
            set
            {
                int idx = value == null ? -1 : Items.IndexOf(value);
                if (idx == selected) return;
                var old = SelectedItem;
                selected = idx;
                Animate(selAmt, old, 0, 220);
                Animate(selAmt, value, 1, 220);
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        double Get(Dictionary<ServerItem, double> d, ServerItem it)
        {
            double v;
            return it != null && d.TryGetValue(it, out v) ? v : 0;
        }

        void Animate(Dictionary<ServerItem, double> d, ServerItem it, double to, int ms)
        {
            if (it == null) return;
            double from = Get(d, it);
            Anim.Run(new KeyValuePair<object, ServerItem>(d, it), ms, p => { d[it] = from + (to - from) * p; Invalidate(ItemRect(Items.IndexOf(it))); });
        }

        public Rectangle ItemRect(int i)
        {
            if (i < 0) return Rectangle.Empty;
            if (Horizontal) return new Rectangle(i * Theme.Px(ItemWidth) - scroll, 0, Theme.Px(ItemWidth), Height);
            return new Rectangle(0, i * Theme.Px(ItemHeight) - scroll, Width, Theme.Px(ItemHeight));
        }

        public int IndexFromPoint(Point p)
        {
            int i = Horizontal ? (p.X + scroll) / Theme.Px(ItemWidth) : (p.Y + scroll) / Theme.Px(ItemHeight);
            return (Horizontal ? p.X : p.Y) < 0 || i < 0 || i >= Items.Count ? -1 : i;
        }

        void ClampScroll()
        {
            int max = Horizontal ? Math.Max(0, Items.Count * Theme.Px(ItemWidth) - Width) : Math.Max(0, Items.Count * Theme.Px(ItemHeight) - Height);
            scroll = Math.Max(0, Math.Min(max, scroll));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            if (DrawEntry == null) return;
            int first = Math.Max(0, scroll / Theme.Px(Horizontal ? ItemWidth : ItemHeight));
            for (int i = first; i < Items.Count; i++)
            {
                var r = ItemRect(i);
                if (Horizontal ? r.Left > Width : r.Top > Height) break;
                if (!r.IntersectsWith(e.ClipRectangle)) continue;
                DrawEntry(g, Items[i], r, i, Get(hoverAmt, Items[i]), Math.Max(Get(selAmt, Items[i]), i == selected ? 0.001 : 0));
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = IndexFromPoint(e.Location);
            if (i == hoverIdx) return;
            if (hoverIdx >= 0 && hoverIdx < Items.Count) Animate(hoverAmt, Items[hoverIdx], 0, 180);
            hoverIdx = i;
            if (i >= 0) Animate(hoverAmt, Items[i], 1, 140);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hoverIdx >= 0 && hoverIdx < Items.Count) Animate(hoverAmt, Items[hoverIdx], 0, 180);
            hoverIdx = -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            int i = IndexFromPoint(e.Location);
            if (i >= 0) SelectedItem = Items[i];
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            scroll -= e.Delta / 120 * (Theme.Px(Horizontal ? ItemWidth : ItemHeight) / 2);
            ClampScroll();
            Invalidate();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            ClampScroll();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            return keyData == Keys.Up || keyData == Keys.Down || keyData == Keys.Left || keyData == Keys.Right || base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Items.Count == 0) return;
            if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Right) SelectedItem = Items[Math.Min(Items.Count - 1, selected + 1)];
            else if (e.KeyCode == Keys.Up || e.KeyCode == Keys.Left) SelectedItem = Items[Math.Max(0, selected - 1)];
        }
    }
}
