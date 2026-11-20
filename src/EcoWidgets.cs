using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>Pill-shaped segmented switch (e.g. the time range of the economy tab) with a sliding, animated highlight.</summary>
    class Segmented : Control
    {
        readonly string[] items;
        int sel, hover = -1;
        double pos;
        public event EventHandler SelectedChanged;

        public Segmented(string[] items, int selected)
        {
            this.items = items;
            sel = selected;
            pos = selected;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.F(9f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            BackColor = Theme.Bg;
            int w = items.Max(s => Theme.Measure(s, Font).Width) + 24;
            Size = new Size(w * items.Length + 8, 34);
        }

        public int SelectedIndex
        {
            get { return sel; }
            set
            {
                if (value == sel || value < 0 || value >= items.Length) return;
                var from = pos;
                sel = value;
                Anim.Run(this, 240, p => { pos = from + (sel - from) * p; Invalidate(); });
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        public string SelectedText { get { return items[sel]; } }

        RectangleF Seg(double i)
        {
            float pad = Theme.Px(4);
            float w = (Width - 2 * pad) / items.Length;
            return new RectangleF(pad + w * (float)i, pad, w, Height - 2 * pad - 1);
        }

        int IndexAt(int x)
        {
            float pad = Theme.Px(4);
            float w = (Width - 2 * pad) / items.Length;
            return Math.Max(0, Math.Min(items.Length - 1, (int)((x - pad) / w)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            var track = new RectangleF(0, 0, Width - 1, Height - 1);
            using (var p = Theme.RoundRect(track, track.Height / 2))
            {
                using (var b = new SolidBrush(Theme.Card)) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Alpha(Color.White, 16))) g.DrawPath(pen, p);
            }
            if (hover >= 0 && hover != sel)
            {
                var h = Seg(hover);
                using (var p = Theme.RoundRect(h, h.Height / 2))
                using (var b = new SolidBrush(Theme.Alpha(Color.White, 10))) g.FillPath(b, p);
            }
            var a = Seg(pos);
            using (var glow = Theme.RoundRect(new RectangleF(a.X - 1, a.Y + 2, a.Width + 2, a.Height), a.Height / 2))
            using (var b = new SolidBrush(Theme.Alpha(Theme.Accent, 60))) g.FillPath(b, glow);
            using (var p = Theme.RoundRect(a, a.Height / 2))
            using (var b = new LinearGradientBrush(new RectangleF(a.X - 1, a.Y, a.Width + 2, a.Height), Theme.Mix(Theme.Accent, Theme.SoftGreen, 0.15), Theme.Mix(Theme.Accent, Theme.Mint, 0.45), 0f))
                g.FillPath(b, p);
            for (int i = 0; i < items.Length; i++)
            {
                var r = Rectangle.Round(Seg(i));
                double near = Math.Max(0, 1 - Math.Abs(pos - i));
                var c = Theme.Mix(i == hover ? Theme.Text2 : Theme.Muted, Color.White, near);
                TextRenderer.DrawText(g, items[i], Font, r, c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int i = IndexAt(e.X); if (i != hover) { hover = i; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
        protected override void OnMouseClick(MouseEventArgs e) { base.OnMouseClick(e); if (e.Button == MouseButtons.Left) SelectedIndex = IndexAt(e.X); }
    }

    /// <summary>KPI tile: tinted icon chip, label, big value, caption, optional trend pill and progress bar.</summary>
    class KpiCard : RoundPanel
    {
        readonly string glyph, title;
        readonly Color accent;
        string value = "–", sub = "", trend;
        Color valueColor = Theme.Text, trendColor = Theme.Muted;
        double bar = -1, shown, glow;
        static Font iconFont;

        public KpiCard(string glyph, string title, Color accent)
        {
            this.glyph = glyph;
            this.title = title.ToUpperInvariant();
            this.accent = accent;
            Size = new Size(220, 124);
        }

        public void Set(string v, string s, Color color, string trendText = null, Color trendCol = default(Color))
        {
            value = v;
            sub = s ?? "";
            valueColor = color;
            trend = trendText;
            trendColor = trendCol == default(Color) ? Theme.Muted : trendCol;
            Invalidate();
        }

        public double Bar
        {
            get { return bar; }
            set
            {
                bar = value;
                if (value < 0) { shown = 0; Invalidate(); return; }
                var from = shown;
                var to = Math.Max(0, Math.Min(1, value));
                Anim.Run(this, 650, p => { shown = from + (to - from) * p; Invalidate(); });
            }
        }

        void Glow(double to) { var from = glow; Anim.Run("g" + GetHashCode(), 200, p => { glow = from + (to - from) * p; Invalidate(); }); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Glow(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); Glow(0); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = CardRect;
            if (r.Width < 20 || r.Height < 20) return;
            using (var clip = Theme.RoundRect(r, Radius))
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r.Right - Theme.Px(110), r.Y - Theme.Px(70), Theme.Px(190), Theme.Px(140));
                using (var pb = new PathGradientBrush(path))
                {
                    pb.CenterColor = Theme.Alpha(accent, (int)(26 + 34 * glow));
                    pb.SurroundColors = new[] { Color.FromArgb(0, accent) };
                    g.SetClip(clip);
                    g.FillPath(pb, path);
                    g.ResetClip();
                }
            }
            if (glow > 0.01)
                using (var p = Theme.RoundRect(r, Radius))
                using (var pen = new Pen(Theme.Alpha(accent, (int)(90 * glow)), 1.2f)) g.DrawPath(pen, p);

            int pad = Theme.Px(16), chip = Theme.Px(32);
            var cr = new Rectangle(r.X + pad, r.Y + pad, chip, chip);
            using (var p = Theme.RoundRect(cr, Theme.Px(9)))
            {
                using (var b = new SolidBrush(Theme.Alpha(accent, 40))) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Alpha(accent, 80))) g.DrawPath(pen, p);
            }
            if (iconFont == null) iconFont = Icons.Font(11f);
            TextRenderer.DrawText(g, glyph, iconFont, cr, accent, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);

            int tx = cr.Right + Theme.Px(10);
            int trendW = 0;
            if (!string.IsNullOrEmpty(trend))
            {
                var tf = Theme.F(8f, FontStyle.Bold);
                var ts = TextRenderer.MeasureText(trend, tf);
                trendW = ts.Width + Theme.Px(14);
                var pr = new Rectangle(r.Right - pad - trendW, cr.Y + (chip - ts.Height - Theme.Px(6)) / 2, trendW, ts.Height + Theme.Px(6));
                using (var p = Theme.RoundRect(pr, pr.Height / 2))
                using (var b = new SolidBrush(Theme.Alpha(trendColor, 36))) g.FillPath(b, p);
                TextRenderer.DrawText(g, trend, tf, pr, trendColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
            TextRenderer.DrawText(g, title, Theme.F(8f, FontStyle.Bold), new Rectangle(tx, cr.Y, r.Right - pad - tx - trendW - Theme.Px(4), chip), Theme.Muted,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            var vf = Theme.F(16.5f, FontStyle.Bold);
            int vy = cr.Bottom + Theme.Px(8);
            var vs = TextRenderer.MeasureText(value, vf);
            TextRenderer.DrawText(g, value, vf, new Rectangle(r.X + pad - Theme.Px(2), vy, r.Width - 2 * pad, vs.Height), valueColor,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, sub, Theme.F(8.5f), new Rectangle(r.X + pad, vy + vs.Height, r.Width - 2 * pad, Theme.Px(18)), Theme.Muted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            if (bar < 0) return;
            var br = new RectangleF(r.X + pad, r.Bottom - Theme.Px(12), r.Width - 2 * pad, Theme.Px(4));
            using (var p = Theme.RoundRect(br, br.Height / 2))
            using (var b = new SolidBrush(Theme.CardHi)) g.FillPath(b, p);
            float w = (float)(br.Width * shown);
            if (w > 1)
            {
                var fr = new RectangleF(br.X, br.Y, w, br.Height);
                using (var p = Theme.RoundRect(fr, br.Height / 2))
                using (var b = new LinearGradientBrush(new RectangleF(fr.X - 1, fr.Y, fr.Width + 2, fr.Height), Theme.Mix(accent, Color.White, 0.25), accent, 0f))
                    g.FillPath(b, p);
            }
        }
    }

    /// <summary>Large headline card: money supply with change pill and an inline area chart (hover shows values).</summary>
    class HeroCard : RoundPanel
    {
        public string Title = "GELDMENGE IM UMLAUF";
        string value = "–", delta = "", sub = "";
        Color deltaColor = Theme.Muted;
        double?[] series = new double?[0];
        string[] labels = new string[0];
        double reveal = 1;
        int hoverIdx = -1;
        public Func<double, string> Format = v => v.ToString("0");

        public HeroCard()
        {
            Size = new Size(600, 200);
            Radius = 18;
        }

        public void Set(string v, string d, Color dc, string s)
        {
            value = v; delta = d ?? ""; deltaColor = dc; sub = s ?? "";
            Invalidate();
        }

        public void SetSeries(double?[] s, string[] l)
        {
            series = s ?? new double?[0];
            labels = l ?? new string[0];
            Anim.Run(this, 900, p => { reveal = p; Invalidate(); });
        }

        int TextBlockW { get { return Math.Min(Theme.Px(360), (int)(CardRect.Width * 0.42)); } }

        RectangleF Plot
        {
            get
            {
                var r = CardRect;
                float x = r.X + TextBlockW + Theme.Px(16);
                return new RectangleF(x, r.Y + Theme.Px(26), Math.Max(10, r.Right - Theme.Px(22) - x), Math.Max(10, r.Height - Theme.Px(58)));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = CardRect;
            if (r.Width < 40 || r.Height < 40) return;
            using (var clip = Theme.RoundRect(r, Radius))
            {
                g.SetClip(clip);
                Blob(g, new RectangleF(r.X - Theme.Px(120), r.Y - Theme.Px(140), Theme.Px(420), Theme.Px(300)), Theme.SoftGreen, 34);
                Blob(g, new RectangleF(r.Right - Theme.Px(320), r.Bottom - Theme.Px(150), Theme.Px(460), Theme.Px(300)), Theme.Mint, 26);
                g.ResetClip();
            }

            int x = r.X + Theme.Px(26), y = r.Y + Theme.Px(24), bw = TextBlockW - Theme.Px(26);
            using (var b = new SolidBrush(Theme.SoftGreen)) g.FillEllipse(b, x, y + Theme.Px(4), Theme.Px(7), Theme.Px(7));
            TextRenderer.DrawText(g, Title, Theme.F(8.25f, FontStyle.Bold), new Point(x + Theme.Px(12), y), Theme.Text2);
            y += Theme.Px(24);
            var vf = Theme.F(27f, FontStyle.Bold);
            var vs = TextRenderer.MeasureText(value, vf);
            TextRenderer.DrawText(g, value, vf, new Rectangle(x - Theme.Px(3), y, bw + Theme.Px(3), vs.Height), Color.White, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            y += vs.Height + Theme.Px(6);
            if (delta.Length > 0)
            {
                var df = Theme.F(9f, FontStyle.Bold);
                var ds = TextRenderer.MeasureText(delta, df);
                var pr = new Rectangle(x, y, ds.Width + Theme.Px(18), ds.Height + Theme.Px(8));
                using (var p = Theme.RoundRect(pr, pr.Height / 2))
                {
                    using (var b = new SolidBrush(Theme.Alpha(deltaColor, 40))) g.FillPath(b, p);
                    using (var pen = new Pen(Theme.Alpha(deltaColor, 90))) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, delta, df, pr, deltaColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                y += pr.Height + Theme.Px(10);
            }
            TextRenderer.DrawText(g, sub, Theme.F(8.75f), new Rectangle(x, y, bw, r.Bottom - y - Theme.Px(12)), Theme.Muted, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);

            DrawChart(g);
        }

        static void Blob(Graphics g, RectangleF rect, Color c, int alpha)
        {
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(rect);
                using (var pb = new PathGradientBrush(path))
                {
                    pb.CenterColor = Theme.Alpha(c, alpha);
                    pb.SurroundColors = new[] { Color.FromArgb(0, c) };
                    g.FillPath(pb, path);
                }
            }
        }

        void Range(out double lo, out double hi)
        {
            var v = series.Where(s => s.HasValue).Select(s => s.Value).ToList();
            lo = v.Count > 0 ? v.Min() : 0;
            hi = v.Count > 0 ? v.Max() : 1;
            double span = hi - lo;
            if (span < Math.Max(1e-9, Math.Abs(hi) * 0.002)) span = Math.Max(1, Math.Abs(hi) * 0.05);
            lo -= span * 0.25;
            hi += span * 0.12;
            if (lo < 0 && v.Count > 0 && v.Min() >= 0) lo = 0;
        }

        void DrawChart(Graphics g)
        {
            var pr = Plot;
            int n = series.Length;
            if (n < 2 || series.Count(s => s.HasValue) < 2)
            {
                TextRenderer.DrawText(g, n == 0 ? "Noch keine Verlaufsdaten" : "Zu wenig Daten für einen Verlauf", Theme.F(9f), Rectangle.Round(pr), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            double lo, hi;
            Range(out lo, out hi);
            Func<int, float> X = i => pr.Left + pr.Width * i / (n - 1f);
            Func<double, float> Y = v => (float)(pr.Bottom - pr.Height * (v - lo) / (hi - lo));
            using (var pen = new Pen(Theme.Alpha(Color.White, 10)) { DashStyle = DashStyle.Dot })
                for (int i = 0; i <= 2; i++) { float yy = pr.Top + pr.Height * i / 2f; g.DrawLine(pen, pr.Left, yy, pr.Right, yy); }

            var pts = new List<PointF>();
            for (int i = 0; i < n; i++) if (series[i].HasValue) pts.Add(new PointF(X(i), Y(series[i].Value)));
            g.SetClip(new RectangleF(pr.Left - 6, pr.Top - 12, (float)(pr.Width * reveal) + 12, pr.Height + 24));
            using (var area = new GraphicsPath())
            {
                area.AddLines(pts.ToArray());
                area.AddLine(pts[pts.Count - 1], new PointF(pts[pts.Count - 1].X, pr.Bottom));
                area.AddLine(new PointF(pts[0].X, pr.Bottom), pts[0]);
                using (var b = new LinearGradientBrush(new RectangleF(pr.X, pr.Y - 1, pr.Width, pr.Height + 2), Theme.Alpha(Theme.SoftGreen, 80), Theme.Alpha(Theme.Accent, 0), 90f))
                    g.FillPath(b, area);
            }
            using (var lb = new LinearGradientBrush(new RectangleF(pr.X - 1, pr.Y, pr.Width + 2, pr.Height), Theme.SoftGreen, Theme.Mint, 0f))
            {
                using (var pen = new Pen(Theme.Alpha(Theme.SoftGreen, 45), Theme.Px(7)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts.ToArray());
                using (var pen = new Pen(lb, Theme.Px(2.4)) { LineJoin = LineJoin.Round }) g.DrawLines(pen, pts.ToArray());
            }
            g.ResetClip();
            if (reveal > 0.98)
            {
                var lp = pts[pts.Count - 1];
                using (var b = new SolidBrush(Theme.Alpha(Theme.Mint, 70))) g.FillEllipse(b, lp.X - Theme.Px(8), lp.Y - Theme.Px(8), Theme.Px(16), Theme.Px(16));
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, lp.X - Theme.Px(3.5), lp.Y - Theme.Px(3.5), Theme.Px(7), Theme.Px(7));
            }
            var lf = Theme.F(8f);
            if (labels.Length > 0)
            {
                TextRenderer.DrawText(g, labels[0], lf, new Point((int)pr.Left, (int)pr.Bottom + Theme.Px(8)), Theme.Muted);
                var last = labels[Math.Min(labels.Length, n) - 1];
                var ls = TextRenderer.MeasureText(last, lf);
                TextRenderer.DrawText(g, last, lf, new Point((int)pr.Right - ls.Width, (int)pr.Bottom + Theme.Px(8)), Theme.Muted);
            }
            if (hoverIdx >= 0 && hoverIdx < n && series[hoverIdx].HasValue)
            {
                float hx = X(hoverIdx), hy = Y(series[hoverIdx].Value);
                using (var pen = new Pen(Theme.Alpha(Color.White, 50)) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, hx, pr.Top, hx, pr.Bottom);
                using (var b = new SolidBrush(Theme.Alpha(Theme.SoftGreen, 80))) g.FillEllipse(b, hx - Theme.Px(8), hy - Theme.Px(8), Theme.Px(16), Theme.Px(16));
                using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, hx - Theme.Px(4), hy - Theme.Px(4), Theme.Px(8), Theme.Px(8));
                var tip = Format(series[hoverIdx].Value) + (hoverIdx < labels.Length ? "   " + labels[hoverIdx] : "");
                var tf = Theme.F(9f, FontStyle.Bold);
                var ts = TextRenderer.MeasureText(tip, tf);
                var tr = new Rectangle((int)Math.Min(hx + Theme.Px(12), CardRect.Right - ts.Width - Theme.Px(30)), (int)Math.Max(pr.Top - Theme.Px(14), hy - Theme.Px(40)),
                                       ts.Width + Theme.Px(16), ts.Height + Theme.Px(10));
                using (var p = Theme.RoundRect(tr, Theme.Px(8)))
                {
                    using (var b = new SolidBrush(Theme.Alpha(Theme.Bg, 240))) g.FillPath(b, p);
                    using (var pen = new Pen(Theme.Alpha(Theme.SoftGreen, 120))) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, tip, tf, new Point(tr.X + Theme.Px(8), tr.Y + Theme.Px(5)), Theme.Text);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var pr = Plot;
            int n = series.Length;
            int idx = n < 2 || e.X < pr.Left - 8 || e.X > pr.Right + 8 ? -1 : (int)Math.Round((e.X - pr.Left) / pr.Width * (n - 1));
            idx = Math.Max(-1, Math.Min(n - 1, idx));
            if (idx != hoverIdx) { hoverIdx = idx; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverIdx = -1; Invalidate(); }
    }

    /// <summary>Ranked list with one stacked bar per row (green = money in, red = money out). Height follows the number of rows.</summary>
    class FlowBars : Control
    {
        public class Item
        {
            public string Name, Left, Right, Value;
            public double In, Out;
            public Color Dot = Theme.Accent, ValueColor = Theme.Text;
        }

        List<Item> items = new List<Item>();
        int hover = -1;
        double reveal = 1;
        public bool Compact;
        public string EmptyText = "Keine Daten im Zeitraum";
        public Color InColor = Theme.Green, OutColor = Theme.Red;

        public FlowBars()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
        }

        int RowH { get { return Theme.Px(Compact ? 42 : 64); } }

        /// <summary>Height in device pixels needed for the current rows.</summary>
        public int NeededHeight { get { return RowH * Math.Max(1, items.Count) + Theme.Px(4); } }

        public void SetItems(List<Item> list)
        {
            items = list ?? new List<Item>();
            hover = -1;
            Anim.Run(this, 800, p => { reveal = p; Invalidate(); });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, EmptyText, Theme.F(9f), ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            double max = Math.Max(1e-9, items.Max(i => i.In + i.Out));
            int rh = RowH, pad = Theme.Px(10);
            var nameF = Theme.F(9.5f, FontStyle.Bold);
            var valF = Theme.F(9.5f, FontStyle.Bold);
            var smallF = Theme.F(8f);
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                int y = i * rh;
                if (y > Height) break;
                if (i == hover)
                    using (var p = Theme.RoundRect(new Rectangle(0, y + Theme.Px(1), Width - 1, rh - Theme.Px(2)), Theme.Px(10)))
                    using (var b = new SolidBrush(Theme.Alpha(Color.White, 9))) g.FillPath(b, p);
                int line1 = y + Theme.Px(Compact ? 4 : 8);
                var vs = TextRenderer.MeasureText(it.Value ?? "", valF);
                using (var b = new SolidBrush(Theme.Alpha(it.Dot, 60))) g.FillEllipse(b, pad - Theme.Px(3), line1 + Theme.Px(3), Theme.Px(14), Theme.Px(14));
                using (var b = new SolidBrush(it.Dot)) g.FillEllipse(b, pad, line1 + Theme.Px(6), Theme.Px(8), Theme.Px(8));
                TextRenderer.DrawText(g, it.Name ?? "", nameF, new Rectangle(pad + Theme.Px(16), line1, Width - 2 * pad - Theme.Px(16) - vs.Width - Theme.Px(12), vs.Height), Theme.Text,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, it.Value ?? "", valF, new Point(Width - pad - vs.Width, line1), it.ValueColor);

                var track = new RectangleF(pad, y + Theme.Px(Compact ? 28 : 33), Width - 2 * pad, Theme.Px(6));
                using (var p = Theme.RoundRect(track, track.Height / 2))
                using (var b = new SolidBrush(Theme.Alpha(Color.White, 12))) g.FillPath(b, p);
                float wIn = (float)(track.Width * it.In / max * reveal), wOut = (float)(track.Width * it.Out / max * reveal);
                g.SetClip(new RectangleF(track.X, track.Y - 2, wIn + wOut, track.Height + 4));
                using (var p = Theme.RoundRect(new RectangleF(track.X, track.Y, Math.Max(track.Height, wIn + wOut), track.Height), track.Height / 2))
                {
                    if (wIn > 0.5)
                        using (var b = new LinearGradientBrush(new RectangleF(track.X - 1, track.Y, wIn + 2, track.Height), Theme.Mix(InColor, Color.White, 0.2), InColor, 0f))
                        { g.SetClip(new RectangleF(track.X, track.Y - 2, wIn, track.Height + 4)); g.FillPath(b, p); }
                    if (wOut > 0.5)
                        using (var b = new LinearGradientBrush(new RectangleF(track.X + wIn - 1, track.Y, wOut + 2, track.Height), OutColor, Theme.Mix(OutColor, Color.White, 0.2), 0f))
                        { g.SetClip(new RectangleF(track.X + wIn, track.Y - 2, wOut, track.Height + 4)); g.FillPath(b, p); }
                }
                g.ResetClip();
                if (!Compact)
                {
                    int ly = y + Theme.Px(43);
                    TextRenderer.DrawText(g, it.Left ?? "", smallF, new Rectangle(pad, ly, (Width - 2 * pad) / 2, Theme.Px(16)), Theme.Muted, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    TextRenderer.DrawText(g, it.Right ?? "", smallF, new Rectangle(Width / 2, ly, Width / 2 - pad, Theme.Px(16)), Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int i = e.Y / Math.Max(1, RowH);
            if (i >= items.Count) i = -1;
            if (i != hover) { hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }
    }

    /// <summary>Tinted notice card (warnings) that hides itself when empty.</summary>
    class Banner : RoundPanel
    {
        readonly Label text;

        public Banner(Color c)
        {
            Elevated = false;
            Fill = Theme.Mix(Theme.Card, c, 0.10);
            BorderColor = Theme.Alpha(c, 90);
            Radius = 12;
            Padding = new Padding(16, 10, 16, 10);
            text = Theme.Lbl("", Theme.F(9.25f), Theme.Mix(c, Color.White, 0.25), Color.Transparent);
            text.AutoSize = false;
            text.Dock = DockStyle.Fill;
            text.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(text);
            Visible = false;
        }

        public void SetLines(List<string> lines)
        {
            text.Text = string.Join("\n", lines);
            Visible = lines.Count > 0;
            Height = Theme.Px(22 + 19 * Math.Max(1, lines.Count));
        }
    }
}
