using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>Line chart with axes, grid, red outage bands and a hover cursor (time + values of all series).</summary>
    class NetChart : Control
    {
        public class Series
        {
            public string Name;
            public Color Color;
            public double?[] V;
        }

        static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
        public string Title = "", Unit = "", Empty = "Keine Daten";
        public double MinMax = 1;          // y axis never smaller than this
        public bool FixedZero = true;
        public Func<double, string> Fmt;
        long[] t = new long[0];
        bool[] gap = new bool[0];
        List<Series> series = new List<Series>();
        int hover = -1;

        public NetChart()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Height = Theme.Px(200);
            Fmt = v => v.ToString("0.#", De);
        }

        public void SetData(long[] times, bool[] gaps, List<Series> s)
        {
            t = times; gap = gaps; series = s; hover = -1;
            Invalidate();
        }

        Rectangle Plot() { return new Rectangle(Theme.Px(52), Theme.Px(34), Math.Max(10, Width - Theme.Px(52) - Theme.Px(14)), Math.Max(10, Height - Theme.Px(34) - Theme.Px(24))); }

        static string Clock(long ts, bool secs)
        {
            var d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToLocalTime();
            return d.ToString(secs ? "dd.MM. HH:mm:ss" : "dd.MM. HH:mm", De);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = Plot();
            int h = -1;
            if (t.Length > 0 && p.Contains(e.Location))
                h = t.Length == 1 ? 0 : (int)Math.Round((e.X - p.Left) / (double)p.Width * (t.Length - 1));
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hover != -1) { hover = -1; Invalidate(); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var p = Plot();
            TextRenderer.DrawText(g, Title.ToUpperInvariant(), Theme.F(8f, FontStyle.Bold), new Point(Theme.Px(12), Theme.Px(8)), Theme.Muted);
            // legend
            int lx = Width - Theme.Px(12);
            for (int i = series.Count - 1; i >= 0; i--)
            {
                var sz = TextRenderer.MeasureText(series[i].Name, Theme.F(8.5f));
                lx -= sz.Width;
                TextRenderer.DrawText(g, series[i].Name, Theme.F(8.5f), new Point(lx, Theme.Px(7)), Theme.Text2);
                lx -= Theme.Px(12);
                using (var b = new SolidBrush(series[i].Color)) g.FillEllipse(b, lx, Theme.Px(12), Theme.Px(8), Theme.Px(8));
                lx -= Theme.Px(10);
            }
            double max = MinMax;
            foreach (var s in series) foreach (var v in s.V) if (v.HasValue && v.Value > max) max = v.Value;
            max = Nice(max);
            // grid + y labels
            using (var pen = new Pen(Theme.Border))
                for (int i = 0; i <= 4; i++)
                {
                    int y = p.Bottom - (int)(p.Height * i / 4.0);
                    g.DrawLine(pen, p.Left, y, p.Right, y);
                    var txt = Fmt(max * i / 4.0);
                    var sz = TextRenderer.MeasureText(txt, Theme.F(8f));
                    TextRenderer.DrawText(g, txt, Theme.F(8f), new Point(p.Left - sz.Width - 2, y - sz.Height / 2), Theme.Muted);
                }
            if (t.Length == 0)
            {
                TextRenderer.DrawText(g, Empty, Theme.F(9.5f), p, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            int n = t.Length;
            Func<int, float> X = i => n == 1 ? p.Left + p.Width / 2f : p.Left + p.Width * (float)i / (n - 1);
            Func<double, float> Y = v => p.Bottom - (float)(Math.Min(v, max) / max * p.Height);
            // x labels
            int ticks = Math.Max(2, p.Width / Theme.Px(110));
            bool secs = n > 1 && t[n - 1] - t[0] < 3600;
            for (int k = 0; k <= ticks; k++)
            {
                int i = n == 1 ? 0 : (int)Math.Round((n - 1) * k / (double)ticks);
                var txt = Clock(t[i], secs && false);
                var sz = TextRenderer.MeasureText(txt, Theme.F(8f));
                TextRenderer.DrawText(g, txt, Theme.F(8f), new Point((int)Math.Max(0, Math.Min(Width - sz.Width, X(i) - sz.Width / 2)), p.Bottom + Theme.Px(4)), Theme.Muted);
            }
            // outage bands (red)
            using (var b = new SolidBrush(Color.FromArgb(55, Theme.Red)))
            {
                float w = Math.Max(2f, p.Width / (float)Math.Max(1, n - 1));
                for (int i = 0; i < n; i++) if (i < gap.Length && gap[i]) g.FillRectangle(b, X(i) - w / 2, p.Top, w, p.Height);
            }
            g.SetClip(p);
            foreach (var s in series)
            {
                var run = new List<PointF>();
                Action flush = () =>
                {
                    if (run.Count == 1) using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, run[0].X - 2, run[0].Y - 2, 4, 4);
                    else if (run.Count > 1) using (var pen = new Pen(s.Color, 1.8f) { LineJoin = LineJoin.Round }) g.DrawLines(pen, run.ToArray());
                    run.Clear();
                };
                for (int i = 0; i < n; i++)
                {
                    var v = i < s.V.Length ? s.V[i] : null;
                    if (v.HasValue) run.Add(new PointF(X(i), Y(v.Value))); else flush();
                }
                flush();
            }
            g.ResetClip();
            // hover cursor
            if (hover >= 0 && hover < n)
            {
                float x = X(hover);
                using (var pen = new Pen(Theme.Muted) { DashStyle = DashStyle.Dash }) g.DrawLine(pen, x, p.Top, x, p.Bottom);
                var lines = new List<KeyValuePair<string, Color>>();
                lines.Add(new KeyValuePair<string, Color>(Clock(t[hover], true), Theme.Text));
                foreach (var s in series)
                {
                    var v = hover < s.V.Length ? s.V[hover] : null;
                    if (v.HasValue)
                    {
                        using (var b = new SolidBrush(s.Color)) g.FillEllipse(b, x - 3.5f, Y(v.Value) - 3.5f, 7, 7);
                        lines.Add(new KeyValuePair<string, Color>(s.Name + ": " + Fmt(v.Value) + " " + Unit, s.Color));
                    }
                }
                if (hover < gap.Length && gap[hover]) lines.Add(new KeyValuePair<string, Color>("Messausfall", Theme.Red));
                var f = Theme.F(8.5f);
                int w = 0, h = Theme.Px(8);
                foreach (var l in lines) { var sz = TextRenderer.MeasureText(l.Key, f); w = Math.Max(w, sz.Width); h += sz.Height; }
                w += Theme.Px(16);
                int bx = (int)x + Theme.Px(10); if (bx + w > Width - 2) bx = (int)x - w - Theme.Px(10);
                int by = p.Top + 2;
                using (var b = new SolidBrush(Theme.Mix(Theme.Bg, Theme.Card, 0.5))) g.FillRectangle(b, bx, by, w, h);
                using (var pen = new Pen(Theme.Border)) g.DrawRectangle(pen, bx, by, w, h);
                int ty = by + Theme.Px(4);
                foreach (var l in lines)
                {
                    TextRenderer.DrawText(g, l.Key, f, new Point(bx + Theme.Px(8), ty), l.Value);
                    ty += TextRenderer.MeasureText(l.Key, f).Height;
                }
            }
        }

        static double Nice(double v)
        {
            if (v <= 0) return 1;
            double e = Math.Pow(10, Math.Floor(Math.Log10(v)));
            double m = v / e;
            double nm = m <= 1 ? 1 : m <= 2 ? 2 : m <= 2.5 ? 2.5 : m <= 5 ? 5 : 10;
            return nm * e;
        }
    }

    /// <summary>
    /// Network monitor tab: status cards, live chart (10 s refresh while visible), history, outages/events and CSV export.
    /// Data comes from netmon.py – via SSH (owner) or the team gateway (/panel/netz, right netzwerk.view).
    /// </summary>
    class NetzView : Panel
    {
        readonly Api api;
        readonly bool viaGateway;
        readonly Func<Ssh> getSsh;
        readonly Func<bool> sshReady;
        readonly Action<string, Color> setStatus;

        static readonly double[] HistHours = { 1, 6, 24, 72, 168, 720 };
        static readonly string[] HistNames = { "1 h", "6 h", "24 h", "3 T", "7 T", "30 T" };
        static readonly int[] ExportDays = { 1, 3, 7, 14, 31 };
        static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
        static readonly Color Orange = Theme.Yellow;

        Label hSub, evInfo;
        ProgressLine progress;
        Panel body;
        Panel livePanel, histPanel, evPanel;
        KpiCard cState, cTcp, cPing, cRtt, cNet, cOut;
        NetChart liveLoss, liveRtt, histLoss, histRtt, histNet;
        Segmented histBox, evBox;
        ListView evList;
        FlatBtn bRefresh, bExport, bCheck;
        readonly Timer timer = new Timer { Interval = 10000 };

        bool busyStatic, busyLive, loaded;
        int histIdx = 2, evIdx = 2;
        static readonly int[] EvDays = { 1, 3, 7, 14, 30 };

        public NetzView(Api api, bool viaGateway, Func<Ssh> getSsh, Func<bool> sshReady, Action<string, Color> setStatus)
        {
            this.api = api;
            this.viaGateway = viaGateway;
            this.getSsh = getSsh;
            this.sshReady = sshReady;
            this.setStatus = setStatus;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
            timer.Tick += async (s, e) => await LiveTick();
            VisibleChanged += (s, e) =>
            {
                if (Visible) { timer.Start(); Activate(); } else timer.Stop();
            };
            Disposed += (s, e) => timer.Dispose();
        }

        /// <summary>Called when the tab is shown: loads once, then the 10 s timer keeps the live view fresh.</summary>
        public async void Activate()
        {
            if (!viaGateway && !sshReady())
            {
                hSub.Text = "Der Netzwerk-Monitor wird per SSH vom Server gelesen – richte SSH einmal unter „Docker“ ein.";
                hSub.ForeColor = Theme.Yellow;
                return;
            }
            if (Visible) timer.Start();
            if (!loaded) await ReloadAll();
        }

        // ───────────────────────────── server calls ─────────────────────────────
        /// <summary>Runs one netmon.py command. Gateway: POST /panel/netz {cmd,args}; owner: python3 /opt/pxmx/netmon.py cmd base64(args) over SSH.</summary>
        async Task<KeyValuePair<object, string>> Call(string cmd, Dictionary<string, object> args, int timeout = 60000)
        {
            if (viaGateway)
            {
                var r = await api.SendRaw("POST", "/panel/netz", Encoding.UTF8.GetBytes(J.ToJson(new Dictionary<string, object> { { "cmd", cmd }, { "args", args } })),
                                          "application/json", null, timeout);
                if (!r.Ok) return new KeyValuePair<object, string>(null, r.Error);
                return new KeyValuePair<object, string>(r.Json, null);
            }
            if (!sshReady()) return new KeyValuePair<object, string>(null, "SSH ist nicht eingerichtet.");
            var cmdline = "python3 /opt/pxmx/netmon.py " + cmd + " " + Convert.ToBase64String(Encoding.UTF8.GetBytes(J.ToJson(args)));
            var res = await getSsh().Run(cmdline, timeout);
            if (!res.Ok) return new KeyValuePair<object, string>(null, res.Message);
            object j;
            try { j = J.Parse(res.Out.Trim()); } catch { return new KeyValuePair<object, string>(null, "Unerwartete Antwort: " + res.Message); }
            if (J.S(j, "status") != "ok") return new KeyValuePair<object, string>(null, J.S(j, "error") ?? "Fehler");
            return new KeyValuePair<object, string>(j, null);
        }

        // ───────────────────────────── layout ─────────────────────────────
        void Build()
        {
            var header = Ui.Header("Netzwerk", out hSub, null, out _tabsUnused, null);
            hSub.Text = "Unabhängige Messung des Server-Anschlusses: Ping, TCP-Verlust, Durchsatz, Ausfälle – Nachweis für den Hoster";
            hSub.ForeColor = Theme.Muted;
            bRefresh = new FlatBtn("↻", Theme.CardHi) { Size = new Size(38, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            bRefresh.Tip("Neu laden");
            bCheck = new FlatBtn("Kette prüfen", Theme.CardHi) { Height = 34, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            bCheck.Tip("Prüft die SHA-256-Verkettung aller Messzeilen auf nachträgliche Änderungen");
            bExport = new FlatBtn("Export (CSV)", Theme.Accent) { Height = 34, Anchor = AnchorStyles.Top | AnchorStyles.Right };
            foreach (Control c in new Control[] { bRefresh, bCheck, bExport }) header.Controls.Add(c);
            header.Resize += (s, e) =>
            {
                bRefresh.Location = new Point(header.Width - bRefresh.Width - 2, 0);
                bCheck.Location = new Point(bRefresh.Left - bCheck.Width - 8, 0);
                bExport.Location = new Point(bCheck.Left - bExport.Width - 8, 0);
            };
            bRefresh.Click += async (s, e) => await ReloadAll();
            bExport.Click += async (s, e) => await DoExport();
            bCheck.Click += async (s, e) => await DoCheck();

            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };

            body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            UiX.DarkScroll(body);

            cState = new KpiCard(Icons.Server, "Messdienst", Theme.Green);
            cTcp = new KpiCard(Icons.Globe, "TCP-Verlust (1 min)", Theme.SoftGreen);
            cPing = new KpiCard(Icons.Chart, "Ping-Verlust (1 min)", Theme.Accent);
            cRtt = new KpiCard(Icons.Play, "Latenz Ø", Theme.Mint);
            cNet = new KpiCard(Icons.Download, "Durchsatz", Theme.Green);
            cOut = new KpiCard(Icons.Block, "Ausfälle (24 h)", Theme.Red);
            foreach (var c in new KpiCard[] { cState, cTcp, cPing, cRtt, cNet, cOut }) { c.Size = new Size(Theme.Px(200), Theme.Px(112)); body.Controls.Add(c); }

            livePanel = Section("LIVE – letzte 15 Minuten (alle 10 s)", false, out liveLoss, out liveRtt, out _noNet);
            histBox = new Segmented(HistNames, histIdx);
            histPanel = Section("VERLAUF", true, out histLoss, out histRtt, out histNet);
            histBox.Location = new Point(Theme.Px(110), 0);
            histPanel.Controls.Add(histBox);
            histBox.SelectedChanged += async (s, e) => { histIdx = Math.Max(0, histBox.SelectedIndex); await LoadHist(); };

            evPanel = new Panel { BackColor = Theme.Bg, Height = Theme.Px(320) };
            var evTitle = Theme.Lbl("AUSFÄLLE & EREIGNISSE", Theme.F(9f, FontStyle.Bold), Theme.Muted, Theme.Bg);
            evTitle.Location = new Point(2, Theme.Px(8));
            evBox = new Segmented(new[] { "24 h", "3 T", "7 T", "14 T", "30 T" }, evIdx);
            evBox.Location = new Point(Theme.Px(210), 0);
            evBox.SelectedChanged += async (s, e) => { evIdx = Math.Max(0, evBox.SelectedIndex); await LoadEvents(); };
            evInfo = Theme.Lbl("", Theme.F(9f), Theme.Muted, Theme.Bg);
            evInfo.AutoSize = true;
            evList = Ui.List(new[] { "Art", "Beginn", "Ende", "Dauer", "Beschreibung" }, new[] { 110, 150, 150, 90, 520 });
            evList.Dock = DockStyle.None;
            evPanel.Controls.Add(evTitle);
            evPanel.Controls.Add(evBox);
            evPanel.Controls.Add(evInfo);
            evPanel.Controls.Add(evList);
            body.Controls.Add(livePanel);
            body.Controls.Add(histPanel);
            body.Controls.Add(evPanel);
            body.Resize += (s, e) => Relayout();

            Controls.Add(body);
            Controls.Add(progress);
            Controls.Add(header);
            Relayout();
        }

        TabBtn[] _tabsUnused;

        NetChart _noNet;

        Panel Section(string title, bool withNet, out NetChart a, out NetChart b, out NetChart c)
        {
            var p = new Panel { BackColor = Theme.Bg };
            var t = Theme.Lbl(title, Theme.F(9f, FontStyle.Bold), Theme.Muted, Theme.Bg);
            t.Location = new Point(2, Theme.Px(8));
            p.Controls.Add(t);
            a = NewChart("Paketverlust", "%", v => v.ToString("0.#", De), 1);
            b = NewChart("Latenz", "ms", v => v.ToString("0.#", De), 5);
            c = withNet ? NewChart("Durchsatz", "Mbit/s", v => v.ToString("0.##", De), 1) : null;
            p.Controls.Add(a);
            p.Controls.Add(b);
            if (c != null) p.Controls.Add(c);
            return p;
        }

        static NetChart NewChart(string title, string unit, Func<double, string> fmt, double minMax)
        {
            return new NetChart { Title = title + " (" + unit + ")", Unit = unit, Fmt = fmt, MinMax = minMax, Empty = "Noch keine Messdaten" };
        }

        void Relayout()
        {
            int w = Math.Max(300, body.ClientSize.Width - Theme.Px(4));
            int gap = Theme.Px(10);
            var cards = new KpiCard[] { cState, cTcp, cPing, cRtt, cNet, cOut };
            int perRow = Math.Max(1, Math.Min(cards.Length, (w + gap) / (Theme.Px(200) + gap)));
            int cw = (w - gap * (perRow - 1)) / perRow;
            int y = 0;
            for (int i = 0; i < cards.Length; i++)
            {
                cards[i].Width = cw;
                cards[i].Location = new Point((i % perRow) * (cw + gap), y + (i / perRow) * (cards[i].Height + gap));
            }
            y += ((cards.Length + perRow - 1) / perRow) * (cState.Height + gap) + gap;
            bool two = w >= Theme.Px(900);
            y = LayoutSection(livePanel, new[] { liveLoss, liveRtt }, y, w, two);
            y = LayoutSection(histPanel, new[] { histLoss, histRtt, histNet }, y, w, two);
            evPanel.SetBounds(0, y, w, Theme.Px(320));
            evInfo.Location = new Point(evBox.Right + Theme.Px(12), Theme.Px(8));
            evList.SetBounds(0, Theme.Px(44), w, Theme.Px(270));
            evList.Columns[4].Width = Math.Max(200, w - 110 - 150 - 150 - 90 - Theme.Px(20));
        }

        static int LayoutSection(Panel p, NetChart[] charts, int y, int w, bool two)
        {
            int top = Theme.Px(40), gap = Theme.Px(10), ch = Theme.Px(190);
            int cw = two ? (w - gap) / 2 : w;
            int cy = top, col = 0, rowH = ch;
            for (int i = 0; i < charts.Length; i++)
            {
                charts[i].SetBounds(col * (cw + gap), cy, cw, ch);
                if (two && col == 0 && i < charts.Length - 1) col = 1;
                else { col = 0; cy += ch + gap; }
            }
            if (col == 1) cy += ch + gap;
            p.SetBounds(0, y, w, cy);
            return y + cy + gap;
        }

        // ───────────────────────────── data ─────────────────────────────
        async Task ReloadAll()
        {
            if (busyStatic) return;
            busyStatic = true;
            progress.Set(double.NaN);
            try
            {
                await LoadStatus();
                await LoadLive();
                await LoadHist();
                await LoadEvents();
                loaded = true;
            }
            finally { busyStatic = false; progress.Set(-1); }
        }

        async Task LiveTick()
        {
            if (!Visible || !loaded || busyLive || busyStatic) return;
            busyLive = true;
            try { await LoadStatus(); await LoadLive(); }
            finally { busyLive = false; }
        }

        bool Fail(string err)
        {
            hSub.Text = "Netzwerk-Monitor: " + err;
            hSub.ForeColor = Theme.Red;
            setStatus("Netzwerk-Monitor: " + err, Theme.Red);
            return false;
        }

        static string Pct(double v) { return v.ToString("0.#", De) + " %"; }
        static string Ms(double v) { return v.ToString("0.#", De) + " ms"; }
        static string Rate(double bps) { return (bps / 1e6).ToString("0.##", De) + " Mbit/s"; }
        static string Dur(double s)
        {
            if (s < 90) return ((int)s) + " s";
            if (s < 5400) return ((int)Math.Round(s / 60)) + " min";
            return (s / 3600).ToString("0.#", De) + " h";
        }

        static double? Mean(IEnumerable<double> v) { var l = v.ToList(); return l.Count == 0 ? (double?)null : l.Average(); }

        async Task LoadStatus()
        {
            var r = await Call("status", new Dictionary<string, object>());
            if (r.Value != null) { Fail(r.Value); return; }
            var j = r.Key;
            hSub.Text = "Unabhängige Messung des Server-Anschlusses: Ping, TCP-Verlust, Durchsatz, Ausfälle – Nachweis für den Hoster";
            hSub.ForeColor = Theme.Muted;
            if (!J.B(j, "laeuft"))
            {
                var hint = J.S(j, "hinweis");
                cState.Set("Aus", hint ?? ("Letzte Messung vor " + Dur(J.N(j, "alter"))), Theme.Red);
                if (J.Get(j, "letzte") == null) return;
            }
            else cState.Set("Läuft", "alle " + J.N(j, "intervall") + " s · seit " + (J.S(J.Get(j, "seit"), "berlin") ?? "?"), Theme.Green);
            double? tcp = J.Get(j, "tcp_extern_1min") == null ? (double?)null : J.N(j, "tcp_extern_1min");
            double? png = J.Get(j, "verlust_extern_1min") == null ? (double?)null : J.N(j, "verlust_extern_1min");
            cTcp.Set(tcp.HasValue ? Pct(tcp.Value) : "–", "extern, Mittel letzte Minute", tcp.HasValue && tcp.Value >= 20 ? Theme.Red : tcp.HasValue && tcp.Value > 0 ? Orange : Theme.Green);
            cPing.Set(png.HasValue ? Pct(png.Value) : "–", "extern ohne Gateway", png.HasValue && png.Value >= 20 ? Theme.Red : png.HasValue && png.Value > 0 ? Orange : Theme.Green);
            var m1 = J.Get(J.Get(j, "min1"), "ping");
            var rtts = new List<double>();
            var d1 = J.D(m1);
            if (d1 != null) foreach (var kv in d1) if (kv.Key != "gateway" && J.Get(kv.Value, "rtt") != null) rtts.Add(J.N(kv.Value, "rtt"));
            var rt = Mean(rtts);
            cRtt.Set(rt.HasValue ? Ms(rt.Value) : "–", "Ping extern, letzte Minute", Theme.Text);
            var last = J.Get(j, "letzte");
            if (last != null && J.Get(last, "rx_bps") != null)
                cNet.Set("↓ " + Rate(J.N(last, "rx_bps")), "↑ " + Rate(J.N(last, "tx_bps")), Theme.Text);
            else cNet.Set("–", "", Theme.Text);
            double luecken = J.N(j, "luecken_24h"), sek = J.N(j, "ausfall_24h_s");
            cOut.Set(luecken == 0 ? "Keine" : luecken.ToString("0") + "×", luecken == 0 ? "Messung lückenlos" : "Messausfall gesamt " + Dur(sek), luecken == 0 ? Theme.Green : Theme.Red);
        }

        /// <summary>Converts a "verlauf" answer into the three chart data sets.</summary>
        static void Fill(object j, NetChart loss, NetChart rtt, NetChart net)
        {
            var pts = J.L(J.Get(j, "punkte")).ToList();
            int n = pts.Count;
            var t = new long[n]; var gap = new bool[n];
            var tcpL = new double?[n]; var pngL = new double?[n]; var tcpR = new double?[n]; var pngR = new double?[n];
            var rx = new double?[n]; var tx = new double?[n];
            for (int i = 0; i < n; i++)
            {
                var pt = pts[i];
                t[i] = (long)J.N(pt, "t");
                gap[i] = J.B(pt, "leer");
                Action<string, double?[], double?[], bool> agg = (key, lo, ro, skipGw) =>
                {
                    var d = J.D(J.Get(pt, key));
                    if (d == null || d.Count == 0) return;
                    var lv = new List<double>(); var rv = new List<double>();
                    foreach (var kv in d)
                    {
                        if (skipGw && kv.Key == "gateway") continue;
                        if (J.Get(kv.Value, "verlust") != null) lv.Add(J.N(kv.Value, "verlust"));
                        if (J.Get(kv.Value, "rtt") != null) rv.Add(J.N(kv.Value, "rtt"));
                    }
                    lo[i] = Mean(lv); ro[i] = Mean(rv);
                };
                agg("q", tcpL, tcpR, false);
                agg("p", pngL, pngR, true);
                if (J.Get(pt, "rx") != null) rx[i] = J.N(pt, "rx") / 1e6;
                if (J.Get(pt, "tx") != null) tx[i] = J.N(pt, "tx") / 1e6;
            }
            loss.SetData(t, gap, new List<NetChart.Series> {
                new NetChart.Series { Name = "TCP", Color = Theme.SoftGreen, V = tcpL }, new NetChart.Series { Name = "Ping", Color = Theme.Accent, V = pngL } });
            rtt.SetData(t, gap, new List<NetChart.Series> {
                new NetChart.Series { Name = "TCP", Color = Theme.SoftGreen, V = tcpR }, new NetChart.Series { Name = "Ping", Color = Theme.Accent, V = pngR } });
            if (net != null)
                net.SetData(t, gap, new List<NetChart.Series> {
                    new NetChart.Series { Name = "Empfang", Color = Theme.Green, V = rx }, new NetChart.Series { Name = "Senden", Color = Theme.Mint, V = tx } });
        }

        async Task LoadLive()
        {
            var r = await Call("verlauf", new Dictionary<string, object> { { "stunden", 0.25 } });
            if (r.Value != null) { Fail(r.Value); return; }
            Fill(r.Key, liveLoss, liveRtt, null);
        }

        async Task LoadHist()
        {
            progress.Set(double.NaN);
            try
            {
                var r = await Call("verlauf", new Dictionary<string, object> { { "stunden", HistHours[histIdx] } });
                if (r.Value != null) { Fail(r.Value); return; }
                Fill(r.Key, histLoss, histRtt, histNet);
            }
            finally { if (!busyStatic) progress.Set(-1); }
        }

        async Task LoadEvents()
        {
            var r = await Call("ereignisse", new Dictionary<string, object> { { "tage", EvDays[evIdx] } });
            if (r.Value != null) { Fail(r.Value); return; }
            evList.BeginUpdate();
            evList.Items.Clear();
            int n = 0;
            foreach (var ev in J.L(J.Get(r.Key, "ereignisse")))
            {
                var art = J.S(ev, "art");
                var name = art == "neustart" ? "Neustart" : art == "luecke" ? "Messausfall" : "Paketverlust";
                var it = new ListViewItem(name);
                it.SubItems.Add(J.S(J.Get(ev, "von"), "berlin") ?? "");
                it.SubItems.Add(J.S(J.Get(ev, "bis"), "berlin") ?? "");
                it.SubItems.Add(Dur(J.N(ev, "sekunden")));
                it.SubItems.Add(J.S(ev, "text") ?? "");
                it.Tag = new RowTag { Data = art != "neustart" };
                evList.Items.Add(it);
                n++;
            }
            evList.EndUpdate();
            evInfo.Text = n == 0 ? "Keine Ereignisse im Zeitraum" : n + " Ereignisse";
        }

        // ───────────────────────────── export / check ─────────────────────────────
        async Task DoExport()
        {
            var menu = Ui.Menu();
            for (int i = 0; i < ExportDays.Length; i++)
            {
                int d = ExportDays[i];
                menu.Item(d == 1 ? "Letzte 24 Stunden" : d == 31 ? "Letzte 31 Tage (Maximum)" : "Letzte " + d + " Tage", async () => await ExportRange(d));
            }
            Ui.DropDown(bExport, menu);
            await Task.FromResult(0);
        }

        async Task ExportRange(int days)
        {
            if (busyStatic) return;
            busyStatic = true;
            progress.Set(double.NaN);
            try
            {
                long bis = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;
                var r = await Call("export", new Dictionary<string, object> { { "von", bis - days * 86400L }, { "bis", bis } }, 240000);
                if (r.Value != null) { MessageBox.Show(r.Value, "Export fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                var csv = J.S(r.Key, "csv") ?? "";
                using (var dlg = new SaveFileDialog { FileName = J.S(r.Key, "dateiname") ?? "netzwerk.csv", Filter = "CSV-Datei (*.csv)|*.csv", Title = "Netzwerk-Messung speichern" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    File.WriteAllText(dlg.FileName, csv, new UTF8Encoding(false));   // csv already starts with a BOM character
                    setStatus("Export gespeichert (" + J.S(r.Key, "zeilen") + " Zeilen): " + Path.GetFileName(dlg.FileName), Theme.Green);
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Export fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            finally { busyStatic = false; progress.Set(-1); }
        }

        async Task DoCheck()
        {
            if (busyStatic) return;
            busyStatic = true;
            progress.Set(double.NaN);
            try
            {
                var r = await Call("pruefen", new Dictionary<string, object>(), 120000);
                if (r.Value != null) { MessageBox.Show(r.Value, "Prüfung fehlgeschlagen", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                double fehler = J.N(r.Key, "fehler");
                var msg = J.N(r.Key, "geprueft").ToString("0") + " Zeilen geprüft – " + (fehler == 0 ? "keine Abweichung, Kette intakt." : fehler.ToString("0") + " Abweichung(en), erste bei " + (J.S(J.Get(r.Key, "erster_fehler"), "berlin") ?? "?") + ".");
                var hint = J.S(r.Key, "hinweis");
                MessageBox.Show(msg + (hint != null ? "\n\n" + hint : ""), "Integritätsprüfung", MessageBoxButtons.OK, fehler == 0 ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            finally { busyStatic = false; progress.Set(-1); }
        }
    }
}
