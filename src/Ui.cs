using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RyvenPanel
{
    static class Theme
    {
        public static readonly Color Bg = Hex("#0f1116");
        public static readonly Color Side = Hex("#14171e");
        public static readonly Color Card = Hex("#1b1f28");
        public static readonly Color CardHi = Hex("#262b37");
        public static readonly Color Input = Hex("#12151b");
        public static readonly Color Border = Hex("#2b3040");
        public static readonly Color Text = Hex("#eceef4");
        public static readonly Color Text2 = Hex("#c3c8d4");
        public static readonly Color Muted = Hex("#878da0");
        public static readonly Color Accent = Hex("#238552");
        public static readonly Color SoftGreen = Hex("#83dda2");
        public static readonly Color Mint = Hex("#b2e8c2");
        public static readonly Color Green = Hex("#3ddc84");
        public static readonly Color Red = Hex("#ff5c6c");
        public static readonly Color DarkRed = Hex("#a8323e");
        public static readonly Color Yellow = Hex("#ffc857");

        static readonly Dictionary<string, Font> Fonts = new Dictionary<string, Font>();

        /// <summary>Layout scale = Windows display scaling (1.0 at 100 %, 1.5 at 150 %). RP_SCALE overrides it for testing.</summary>
        public static readonly float S = InitScale();
        /// <summary>Extra font factor, only used when RP_SCALE simulates another DPI.</summary>
        static float fontFake = 1f;

        static float InitScale()
        {
            float real = 1f;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) real = g.DpiX / 96f; } catch { }
            float v;
            var env = Environment.GetEnvironmentVariable("RP_SCALE");
            if (!string.IsNullOrEmpty(env) && float.TryParse(env, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && v >= 0.75f && v <= 3f)
            {
                fontFake = v / real;
                return v;
            }
            return Math.Max(1f, real);
        }

        /// <summary>Logical (96 dpi) pixels → device pixels, for things created after a form was scaled.</summary>
        public static int Px(double v) { return (int)Math.Round(v * S); }

        public static readonly Font Mono = new Font("Consolas", 10f * fontFake);

        public static Font F(float size, FontStyle style = FontStyle.Regular)
        {
            var key = size + "|" + style;
            Font f;
            if (!Fonts.TryGetValue(key, out f))
            {
                f = new Font("Segoe UI", size * fontFake, style);
                Fonts[key] = f;
            }
            return f;
        }

        /// <summary>Measures text in logical pixels (so later form scaling does not apply twice).</summary>
        public static Size Measure(string text, Font font)
        {
            var s = TextRenderer.MeasureText(text, font);
            return new Size((int)Math.Ceiling(s.Width / S), (int)Math.Ceiling(s.Height / S));
        }

        public static Color Hex(string h)
        {
            h = h.TrimStart('#');
            return Color.FromArgb(Convert.ToInt32(h.Substring(0, 2), 16), Convert.ToInt32(h.Substring(2, 2), 16), Convert.ToInt32(h.Substring(4, 2), 16));
        }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(Math.Max(0, Math.Min(255, a)), c.R, c.G, c.B); }

        public static GraphicsPath RoundRect(Rectangle r, int radius)
        {
            return RoundRect(new RectangleF(r.X, r.Y, r.Width, r.Height), radius);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 1) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        /// <summary>Soft drop shadow: several expanding translucent rounded rects.</summary>
        public static void Shadow(Graphics g, Rectangle r, int radius, int depth)
        {
            for (int i = depth; i >= 1; i--)
            {
                var rr = new Rectangle(r.X - i + 1, r.Y - i + 3, r.Width + 2 * i - 2, r.Height + 2 * i - 2);
                using (var p = RoundRect(rr, radius + i))
                using (var b = new SolidBrush(Color.FromArgb(Math.Max(2, 34 / i), 0, 0, 0)))
                    g.FillPath(b, p);
            }
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static void DarkTitle(Form f)
        {
            try
            {
                int v = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
                int corner = 2; // round corners on Windows 11
                DwmSetWindowAttribute(f.Handle, 33, ref corner, 4);
                int caption = 0x00161411; // COLORREF (BGR) close to Side
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);
            }
            catch { }
        }

        public static Icon AppIcon
        {
            get
            {
                try { return Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { return null; }
            }
        }

        public static Label Lbl(string text, Font font, Color color, Color back)
        {
            return new Label { Text = text, Font = font, ForeColor = color, BackColor = back, AutoSize = true, UseMnemonic = false };
        }

        public static string Bytes(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return b.ToString(i >= 3 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + u[i];
        }
    }

    /// <summary>Tiny animation engine on a shared UI timer (~60 fps).</summary>
    static class Anim
    {
        class Item
        {
            public object Key;
            public DateTime Start;
            public double Ms;
            public Action<double> Step;
            public Action Done;
        }

        static readonly Timer timer = new Timer { Interval = 15 };
        static readonly List<Item> items = new List<Item>();

        static Anim() { timer.Tick += Tick; }

        public static double Ease(double p) { return 1 - Math.Pow(1 - p, 3); }

        public static void Run(object key, double ms, Action<double> step, Action done = null)
        {
            if (key != null) items.RemoveAll(i => Equals(i.Key, key));
            items.Add(new Item { Key = key, Start = DateTime.Now, Ms = Math.Max(1, ms), Step = step, Done = done });
            timer.Start();
        }

        static void Tick(object s, EventArgs e)
        {
            var now = DateTime.Now;
            foreach (var it in items.ToArray())
            {
                double p = Math.Min(1, (now - it.Start).TotalMilliseconds / it.Ms);
                try { it.Step(Ease(p)); } catch { }
                if (p >= 1)
                {
                    items.Remove(it);
                    if (it.Done != null) try { it.Done(); } catch { }
                }
            }
            if (items.Count == 0) timer.Stop();
        }
    }

    class DarkForm : Form
    {
        public DarkForm()
        {
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.F(9.5f);
            Icon = Theme.AppIcon;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
        }

        public bool Scaled { get; private set; }

        /// <summary>All layouts are written in 96-dpi pixels; scale the whole tree once before the form appears.</summary>
        protected override void OnLoad(EventArgs e)
        {
            if (!Scaled && Math.Abs(Theme.S - 1f) > 0.01f)
            {
                Scale(new SizeF(Theme.S, Theme.S));
                // Resize handlers that position children ran before the children themselves were scaled – run them again.
                Action<Control> relayout = null;
                relayout = c => { foreach (Control k in c.Controls) { int w = k.Width; k.Width = w + 1; k.Width = w; relayout(k); } };
                relayout(this);
            }
            UiX.DarkScroll(this);
            Scaled = true;
            base.OnLoad(e);
        }

        /// <summary>Scales a control that is added after the form was shown.</summary>
        public static void ScaleLate(Control c)
        {
            var f = c.FindForm() as DarkForm;
            if (f != null && f.Scaled && Math.Abs(Theme.S - 1f) > 0.01f) c.Scale(new SizeF(Theme.S, Theme.S));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitle(this);
        }
    }

    /// <summary>Elevated card: soft shadow, subtle top-lit gradient, hairline highlight.</summary>
    class RoundPanel : Panel
    {
        public Color Fill = Theme.Card;
        public Color BorderColor = Color.Empty;
        public int Radius = 16;
        public bool Elevated = true;
        public bool Flat;

        public RoundPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
        }

        protected Rectangle CardRect { get { return new Rectangle(1, 0, Width - 3, Height - (Elevated ? 5 : 1)); } }

        protected override void OnControlAdded(ControlEventArgs e)
        {
            base.OnControlAdded(e);
            if (e.Control is Label || e.Control is CheckBox) e.Control.BackColor = Color.Transparent;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = CardRect;
            if (r.Width <= 2 || r.Height <= 2) return;
            if (Elevated) Theme.Shadow(g, r, Radius, 7);
            using (var p = Theme.RoundRect(r, Radius))
            {
                using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2), Flat ? Fill : Theme.Mix(Fill, Color.White, 0.05), Fill, 90f))
                    g.FillPath(b, p);
                var bc = BorderColor != Color.Empty ? BorderColor : Theme.Alpha(Color.White, 18);
                using (var pen = new Pen(bc)) g.DrawPath(pen, p);
            }
            using (var hl = Theme.RoundRect(new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), Radius - 1))
            using (var pen = new Pen(Theme.Alpha(Color.White, 12)))
            {
                g.SetClip(new Rectangle(r.X, r.Y, r.Width, Radius + 2));
                g.DrawPath(pen, hl);
                g.ResetClip();
            }
        }
    }

    class FlatBtn : Button
    {
        Color baseColor;
        double hover, press;

        public FlatBtn(string text, Color bg)
        {
            Text = text;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = Theme.F(9.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            Height = 36;
            Width = Theme.Measure(text, Font).Width + 32;
            UseVisualStyleBackColor = false;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
            SetColor(bg);
        }

        public void SetColor(Color bg)
        {
            var from = baseColor;
            baseColor = bg;
            BackColor = bg;
            ForeColor = Color.White;
            if (from != Color.Empty && from != bg && IsHandleCreated)
            {
                var target = bg;
                Anim.Run("c" + GetHashCode(), 200, p => { BackColor = Theme.Mix(from, target, p); Invalidate(); }, () => { BackColor = target; Invalidate(); });
            }
        }

        void AnimateHover(double to)
        {
            var from = hover;
            Anim.Run(this, 160, p => { hover = from + (to - from) * p; Invalidate(); });
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (Enabled) AnimateHover(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); AnimateHover(0); press = 0; }
        protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); press = 1; Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); press = 0; Invalidate(); }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); hover = 0; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            Color parentColor = Theme.Bg;
            for (var c = Parent; c != null; c = c.Parent)
            {
                if (c is RoundPanel) { parentColor = ((RoundPanel)c).Fill; break; }
                if (c.BackColor.A == 255) { parentColor = c.BackColor; break; }
            }
            g.Clear(parentColor);
            Color fill = Enabled ? BackColor : Theme.Mix(BackColor, parentColor, 0.62);
            if (Enabled) fill = Theme.Mix(fill, Color.White, 0.10 * hover);
            if (press > 0) fill = Theme.Mix(fill, Color.Black, 0.18);
            int lift = Enabled ? (int)Math.Round(hover) : 0;
            var r = new Rectangle(0, 1 - lift, Width - 1, Height - 3);
            if (Enabled && hover > 0.05)
                using (var p = Theme.RoundRect(new Rectangle(1, 3, Width - 3, Height - 4), 9))
                using (var b = new SolidBrush(Theme.Alpha(BackColor, (int)(70 * hover))))
                    g.FillPath(b, p);
            using (var p = Theme.RoundRect(r, 9))
            {
                using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Y - 1, r.Width, r.Height + 2), Theme.Mix(fill, Color.White, 0.10), fill, 90f))
                    g.FillPath(b, p);
                using (var pen = new Pen(Theme.Alpha(Color.White, Enabled ? 22 : 8))) g.DrawPath(pen, p);
            }
            var tc = Enabled ? ForeColor : Theme.Mix(ForeColor, fill, 0.55);
            TextRenderer.DrawText(g, Text, Font, r, tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
                using (var p = Theme.RoundRect(new Rectangle(1, 2 - lift, Width - 3, Height - 5), 8))
                using (var pen = new Pen(Theme.Alpha(Color.White, 110)))
                    g.DrawPath(pen, p);
        }
    }

    class TabBtn : Control
    {
        bool active;
        double hover, line;

        public TabBtn(string text)
        {
            Text = text;
            Font = Theme.F(10.5f, FontStyle.Bold);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
            Size = new Size(Theme.Measure(text, Font).Width + 30, 40);
        }

        public bool Active
        {
            get { return active; }
            set
            {
                if (active == value) return;
                active = value;
                var from = line;
                double to = value ? 1 : 0;
                Anim.Run(this, 260, p => { line = from + (to - from) * p; Invalidate(); });
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (hover > 0.01 && !active)
                using (var p = Theme.RoundRect(new Rectangle(4, 4, Width - 9, Height - 12), 8))
                using (var b = new SolidBrush(Theme.Alpha(Color.White, (int)(10 * hover))))
                    g.FillPath(b, p);
            var c = Theme.Mix(Theme.Mix(Theme.Muted, Theme.Text2, hover), Theme.Text, line);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height - 4), c, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if (line > 0.01)
            {
                float w = (float)((Width - 24) * line);
                var r = new RectangleF((Width - w) / 2f, Height - 3, Math.Max(1, w), 3);
                using (var b = new LinearGradientBrush(new RectangleF(r.X - 1, r.Y, r.Width + 2, r.Height), Theme.SoftGreen, Theme.Mint, 0f))
                using (var p = Theme.RoundRect(r, 1.5f))
                    g.FillPath(b, p);
                using (var b = new SolidBrush(Theme.Alpha(Theme.Accent, (int)(40 * line))))
                    g.FillRectangle(b, r.X, r.Y - 3, r.Width, 3);
            }
        }

        void AnimHover(double to)
        {
            var from = hover;
            Anim.Run("h" + GetHashCode(), 150, p => { hover = from + (to - from) * p; Invalidate(); });
        }

        protected override void OnMouseEnter(EventArgs e) { AnimHover(1); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { AnimHover(0); base.OnMouseLeave(e); }
    }

    class GradientText : Control
    {
        public GradientText(string text, Font font)
        {
            Text = text;
            Font = font;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            var sz = Theme.Measure(text, font);
            Size = new Size(sz.Width + 8, sz.Height + 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            var sz = g.MeasureString(Text, Font);
            using (var glow = new SolidBrush(Theme.Alpha(Theme.Green, 38)))
                g.DrawString(Text, Font, glow, 1, 2);
            using (var b = new LinearGradientBrush(new RectangleF(0, 0, Math.Max(1, sz.Width), Math.Max(1, sz.Height)), Theme.Mix(Theme.Green, Color.White, 0.62), Theme.Green, 0f))
                g.DrawString(Text, Font, b, 0, 0);
        }
    }

    class Pill : Control
    {
        public Color Color = Theme.Muted;

        public Pill()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Font = Theme.F(9f, FontStyle.Bold);
            Height = 26;
        }

        public void Set(string text, Color c)
        {
            if (Text == text && Color == c) return;
            var from = Color;
            Text = text;
            Width = TextRenderer.MeasureText(text, Font).Width + Theme.Px(38);
            Anim.Run(this, 300, p => { Color = Theme.Mix(from, c, p); Invalidate(); });
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);
            using (var p = Theme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
            {
                using (var b = new SolidBrush(Theme.Mix(Theme.Bg, Color, 0.16))) g.FillPath(b, p);
                using (var pen = new Pen(Theme.Alpha(Color, 70))) g.DrawPath(pen, p);
            }
            using (var b = new SolidBrush(Theme.Alpha(Color, 60))) g.FillEllipse(b, 9, Height / 2 - 7, 14, 14);
            using (var b = new SolidBrush(Color)) g.FillEllipse(b, 12, Height / 2 - 4, 8, 8);
            TextRenderer.DrawText(g, Text, Font, new Rectangle(26, 0, Width - 30, Height), Color, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    class StatCard : RoundPanel
    {
        readonly Label value, sub;
        double bar = -1, shown = 0, glow;
        public Color BarColor = Theme.Accent;
        public Color Accent = Theme.Accent;

        public StatCard(string title)
        {
            Size = new Size(206, 102);
            Margin = new Padding(0, 0, 14, 14);
            var t = Theme.Lbl(title.ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Color.Transparent);
            t.Location = new Point(18, 14);
            value = Theme.Lbl("–", Theme.F(17f, FontStyle.Bold), Theme.Text, Color.Transparent);
            value.Location = new Point(15, 33);
            sub = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Color.Transparent);
            sub.Location = new Point(18, 69);
            Controls.Add(t);
            Controls.Add(value);
            Controls.Add(sub);
            foreach (Control c in Controls) { c.MouseEnter += (s, e) => Glow(1); c.MouseLeave += (s, e) => Glow(0); }
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
                if (Math.Abs(from - to) < 0.002) return;
                Anim.Run(this, 650, p => { shown = from + (to - from) * p; Invalidate(); });
            }
        }

        public void Set(string v, string s, Color color)
        {
            value.Text = v;
            value.ForeColor = color;
            sub.Text = s ?? "";
        }

        void Glow(double to)
        {
            var from = glow;
            Anim.Run("g" + GetHashCode(), 200, p => { glow = from + (to - from) * p; Invalidate(); });
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); Glow(1); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) Glow(0); }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            base.OnPaintBackground(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = CardRect;
            // accent glow in the top-right corner
            using (var path = new GraphicsPath())
            {
                path.AddEllipse(r.Right - 90, r.Y - 60, 160, 120);
                using (var pb = new PathGradientBrush(path))
                {
                    pb.CenterColor = Theme.Alpha(BarColor, (int)(28 + 30 * glow));
                    pb.SurroundColors = new[] { Color.FromArgb(0, BarColor) };
                    using (var clip = Theme.RoundRect(r, Radius))
                    {
                        g.SetClip(clip);
                        g.FillPath(pb, path);
                        g.ResetClip();
                    }
                }
            }
            if (glow > 0.01)
                using (var p = Theme.RoundRect(r, Radius))
                using (var pen = new Pen(Theme.Alpha(BarColor, (int)(90 * glow)), 1.2f))
                    g.DrawPath(pen, p);
            if (bar < 0) return;
            var br = new RectangleF(18, r.Bottom - 14, r.Width - 34, 5);
            using (var p = Theme.RoundRect(br, 2.5f))
            using (var b = new SolidBrush(Theme.CardHi)) g.FillPath(b, p);
            float w = (float)(br.Width * shown);
            if (w > 1)
            {
                var fr = new RectangleF(br.X, br.Y, w, br.Height);
                using (var p = Theme.RoundRect(fr, 2.5f))
                using (var b = new LinearGradientBrush(new RectangleF(fr.X - 1, fr.Y, fr.Width + 2, fr.Height), Theme.Mix(BarColor, Color.White, 0.25), BarColor, 0f))
                    g.FillPath(b, p);
                using (var b = new SolidBrush(Theme.Alpha(BarColor, 45)))
                    g.FillEllipse(b, fr.Right - 7, fr.Y - 4, 12, 13);
            }
        }
    }

    /// <summary>Animated area/line chart with hover crosshair.</summary>
    class LineChart : Control
    {
        public string Title = "";
        public string Unit = "";
        public Color LineColor = Theme.SoftGreen;
        public double? FixedMax;
        double?[] values = new double?[0];
        double?[] values2;
        public Color Line2Color = Theme.Red;
        public string Name1, Name2;
        string[] labels = new string[0];

        public void SetSecond(double?[] v2) { values2 = v2; Invalidate(); }
        double reveal = 1;
        int hoverIdx = -1;

        public LineChart()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Font = Theme.F(8.5f);
        }

        public void SetData(double?[] v, string[] l, bool animate)
        {
            values = v ?? new double?[0];
            labels = l ?? new string[0];
            if (animate) Anim.Run(this, 900, p => { reveal = p; Invalidate(); });
            else Invalidate();
        }

        RectangleF Plot { get { int l = Compact ? 66 : 52; return new RectangleF(l, 40, Math.Max(10, Width - l - 18), Math.Max(10, Height - 66)); } }

        double Max
        {
            get
            {
                if (FixedMax.HasValue) return FixedMax.Value;
                var all = values2 == null ? values : values.Concat(values2).ToArray();
                var m = all.Where(x => x.HasValue).Select(x => x.Value).DefaultIfEmpty(1).Max();
                if (m <= 0) return 1;
                double mag = Math.Pow(10, Math.Floor(Math.Log10(m)));
                return Math.Ceiling(m / mag * 1.1) * mag;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            TextRenderer.DrawText(g, Title.ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold), new Point(14, 12), Theme.Muted);
            var last = values.LastOrDefault(x => x.HasValue);
            if (last.HasValue)
            {
                var txt = Fmt(last.Value) + Unit;
                var sz = TextRenderer.MeasureText(txt, Theme.F(11f, FontStyle.Bold));
                TextRenderer.DrawText(g, txt, Theme.F(11f, FontStyle.Bold), new Point(Width - sz.Width - 14, 8), LineColor);
            }
            var pr = Plot;
            double max = Max;
            using (var pen = new Pen(Theme.Alpha(Color.White, 12)))
            {
                for (int i = 0; i <= 4; i++)
                {
                    float y = pr.Bottom - pr.Height * i / 4f;
                    g.DrawLine(pen, pr.Left, y, pr.Right, y);
                    TextRenderer.DrawText(g, Fmt(max * i / 4), Font, new Rectangle(0, (int)y - 8, (int)pr.Left - 6, 16), Theme.Muted, TextFormatFlags.Right);
                }
            }
            int n = values.Length;
            if (n < 2)
            {
                TextRenderer.DrawText(g, n == 0 ? "Keine Daten" : "Zu wenig Daten", Font, Rectangle.Round(pr), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            Func<int, float> X = i => pr.Left + pr.Width * i / (n - 1f);
            Func<double, float> Y = v => (float)(pr.Bottom - pr.Height * Math.Min(1, v / max));
            // x labels
            int step = Math.Max(1, n / 6);
            for (int i = 0; i < n; i += step)
                if (i < labels.Length)
                    TextRenderer.DrawText(g, labels[i], Font, new Rectangle((int)X(i) - 50, (int)pr.Bottom + 6, 100, 16), Theme.Muted, TextFormatFlags.HorizontalCenter);

            g.SetClip(new RectangleF(pr.Left - 2, pr.Top - 10, (float)(pr.Width * reveal) + 4, pr.Height + 12));
            DrawSeries(g, values, LineColor, X, Y, pr);
            if (values2 != null && values2.Length == n) DrawSeries(g, values2, Line2Color, X, Y, pr);
            g.ResetClip();
            if (Name1 != null)
            {
                int lx = 14 + TextRenderer.MeasureText(Title.ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold)).Width + 14;
                foreach (var leg in new[] { new KeyValuePair<string, Color>(Name1, LineColor), new KeyValuePair<string, Color>(Name2, Line2Color) })
                {
                    if (leg.Key == null) continue;
                    using (var b = new SolidBrush(leg.Value)) g.FillEllipse(b, lx, 16, 8, 8);
                    TextRenderer.DrawText(g, leg.Key, Font, new Point(lx + 11, 12), Theme.Text2);
                    lx += TextRenderer.MeasureText(leg.Key, Font).Width + 26;
                }
            }

            if (hoverIdx >= 0 && hoverIdx < n && values[hoverIdx].HasValue)
            {
                float hx = X(hoverIdx), hy = Y(values[hoverIdx].Value);
                using (var pen = new Pen(Theme.Alpha(Color.White, 40)) { DashStyle = DashStyle.Dash })
                    g.DrawLine(pen, hx, pr.Top, hx, pr.Bottom);
                using (var b = new SolidBrush(Theme.Alpha(LineColor, 70))) g.FillEllipse(b, hx - 8, hy - 8, 16, 16);
                using (var b = new SolidBrush(LineColor)) g.FillEllipse(b, hx - 4, hy - 4, 8, 8);
                var tip = (Name1 != null ? Name1 + " " : "") + Fmt(values[hoverIdx].Value) + Unit +
                          (values2 != null && hoverIdx < values2.Length && values2[hoverIdx].HasValue ? "  ·  " + (Name2 ?? "") + " " + Fmt(values2[hoverIdx].Value) + Unit : "") +
                          (hoverIdx < labels.Length ? "   " + labels[hoverIdx] : "");
                var sz = TextRenderer.MeasureText(tip, Theme.F(9f, FontStyle.Bold));
                var tr = new Rectangle((int)Math.Min(hx + 10, Width - sz.Width - 24), (int)Math.Max(pr.Top, hy - 34), sz.Width + 16, sz.Height + 10);
                using (var p = Theme.RoundRect(tr, 7))
                {
                    using (var b = new SolidBrush(Theme.Alpha(Theme.Bg, 235))) g.FillPath(b, p);
                    using (var pen = new Pen(Theme.Alpha(LineColor, 120))) g.DrawPath(pen, p);
                }
                TextRenderer.DrawText(g, tip, Theme.F(9f, FontStyle.Bold), new Point(tr.X + 8, tr.Y + 5), Theme.Text);
            }
        }

        static void DrawSeries(Graphics g, double?[] vals, Color color, Func<int, float> X, Func<double, float> Y, RectangleF pr)
        {
            var segment = new List<PointF>();
            Action flush = () =>
            {
                if (segment.Count >= 2)
                {
                    using (var area = new GraphicsPath())
                    {
                        area.AddLines(segment.ToArray());
                        area.AddLine(segment[segment.Count - 1], new PointF(segment[segment.Count - 1].X, pr.Bottom));
                        area.AddLine(new PointF(segment[0].X, pr.Bottom), segment[0]);
                        using (var b = new LinearGradientBrush(new RectangleF(pr.X, pr.Y - 1, pr.Width, pr.Height + 2), Theme.Alpha(color, 70), Theme.Alpha(color, 0), 90f))
                            g.FillPath(b, area);
                    }
                    using (var pen = new Pen(Theme.Alpha(color, 50), 6f) { LineJoin = LineJoin.Round })
                        g.DrawLines(pen, segment.ToArray());
                    using (var pen = new Pen(color, 2f) { LineJoin = LineJoin.Round })
                        g.DrawLines(pen, segment.ToArray());
                }
                else if (segment.Count == 1)
                    using (var b = new SolidBrush(color)) g.FillEllipse(b, segment[0].X - 3, segment[0].Y - 3, 6, 6);
                segment.Clear();
            };
            for (int i = 0; i < vals.Length; i++)
            {
                if (vals[i].HasValue) segment.Add(new PointF(X(i), Y(vals[i].Value)));
                else flush();
            }
            flush();
        }

        /// <summary>Large values as 12,3k / 4,56 Mio / 1,2 Mrd (money charts).</summary>
        public bool Compact;

        string Fmt(double v)
        {
            if (Compact) return CompactNum(v);
            return v >= 100 ? v.ToString("0", CultureInfo.InvariantCulture) : v.ToString("0.#", CultureInfo.InvariantCulture);
        }

        public static string CompactNum(double v)
        {
            var de = CultureInfo.GetCultureInfo("de-DE");
            double a = Math.Abs(v);
            if (a >= 1e9) return (v / 1e9).ToString(a >= 1e11 ? "0" : "0.##", de) + " Mrd";
            if (a >= 1e6) return (v / 1e6).ToString(a >= 1e8 ? "0" : "0.##", de) + " Mio";
            if (a >= 1e4) return (v / 1e3).ToString("0.#", de) + "k";
            return v.ToString(a >= 100 ? "#,0" : "0.#", de);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var pr = Plot;
            int n = values.Length;
            int idx = n < 2 || e.X < pr.Left - 6 || e.X > pr.Right + 6 ? -1 : (int)Math.Round((e.X - pr.Left) / pr.Width * (n - 1));
            idx = Math.Max(-1, Math.Min(n - 1, idx));
            if (idx != hoverIdx) { hoverIdx = idx; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hoverIdx = -1; Invalidate(); }
    }

    /// <summary>Thin animated progress line (indeterminate shimmer or determinate).</summary>
    class ProgressLine : Control
    {
        double value = -1, phase;
        readonly Timer t = new Timer { Interval = 16 };

        public ProgressLine()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Height = 3;
            BackColor = Theme.Side;
            t.Tick += (s, e) => { phase = (phase + 0.012) % 1.0; Invalidate(); };
            Visible = false;
        }

        /// <summary>v &lt; 0 hides, 0..1 determinate, double.NaN indeterminate.</summary>
        public void Set(double v)
        {
            value = v;
            Visible = !(v < 0);
            if (Visible) t.Start(); else t.Stop();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            float w, x;
            if (double.IsNaN(value)) { w = Width * 0.3f; x = (float)(phase * (Width + w)) - w; }
            else { w = (float)(Width * Math.Max(0, Math.Min(1, value))); x = 0; }
            if (w < 1) return;
            using (var b = new LinearGradientBrush(new RectangleF(x - 1, 0, w + 2, Height), Theme.SoftGreen, Theme.Mint, 0f))
                g.FillRectangle(b, x, 0, w, Height);
        }
    }

    /// <summary>A text box inside a rounded dark field with animated focus ring.</summary>
    class Field : RoundPanel
    {
        public readonly TextBox Box;
        double focus;

        public Field(bool password)
        {
            Fill = Theme.Input;
            Radius = 9;
            Elevated = false; Flat = true;
            Height = 40;
            BorderColor = Theme.Border;
            Box = new TextBox
            {
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Input,
                ForeColor = Theme.Text,
                Font = Theme.F(10.5f),
                UseSystemPasswordChar = password
            };
            Controls.Add(Box);
            Resize += (s, e) => { Box.Location = new Point(Theme.Px(12), (Height - Box.Height) / 2); Box.Width = Width - Theme.Px(24); };
            Click += (s, e) => Box.Focus();
            Box.GotFocus += (s, e) => AnimFocus(1);
            Box.LostFocus += (s, e) => AnimFocus(0);
        }

        /// <summary>Grey hint text shown while the field is empty.</summary>
        public string Placeholder { set { UiX.Cue(Box, value); } }

        void AnimFocus(double to)
        {
            var from = focus;
            Anim.Run(this, 180, p => { focus = from + (to - from) * p; BorderColor = Theme.Mix(Theme.Border, Theme.Accent, focus); Invalidate(); });
        }
    }

    static class Dialogs
    {
        public static string Ask(IWin32Window owner, string title, string prompt, string def)
        {
            using (var f = new DarkForm())
            {
                f.Text = title;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                var l = Theme.Lbl(prompt, Theme.F(10f), Theme.Text, Theme.Bg);
                l.Location = new Point(20, 18);
                l.MaximumSize = new Size(440, 0);
                f.Controls.Add(l);
                int y = 18 + l.PreferredHeight + 14;
                f.ClientSize = new Size(480, y + 110);
                var field = new Field(false) { Location = new Point(20, y), Size = new Size(440, 40) };
                field.Box.Text = def ?? "";
                var ok = new FlatBtn("OK", Theme.Accent) { Location = new Point(360, y + 58), Width = 100 };
                var cancel = new FlatBtn("Abbrechen", Theme.CardHi) { Location = new Point(246, y + 58), Width = 106 };
                ok.DialogResult = DialogResult.OK;
                cancel.DialogResult = DialogResult.Cancel;
                f.AcceptButton = ok;
                f.CancelButton = cancel;
                f.Controls.AddRange(new Control[] { field, ok, cancel });
                f.Shown += (s, e) => { field.Box.Focus(); field.Box.SelectAll(); };
                return f.ShowDialog(owner) == DialogResult.OK ? field.Box.Text : null;
            }
        }

        public static bool Confirm(IWin32Window owner, string title, string text)
        {
            return MessageBox.Show(owner, text, title, MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
        }
    }
}
