using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>One booking row as delivered by finanzen.py (amounts in cents).</summary>
    class Buchung
    {
        public long Id;
        public string Belegnr, Datum, Art, Kategorie, Beschreibung, Zahlungsart, Partner, Intervall, BelegDatei, StorniertVon, StornoGrund, ErstelltAm, ErstelltVon, StornoZuBelegnr, StorniertVonBelegnr;
        public long Cent;
        public bool HatBeleg;
        public bool IsExpense { get { return (Art ?? "").ToLowerInvariant().StartsWith("aus"); } }
        public bool Storniert { get { return !string.IsNullOrEmpty(StorniertVon); } }
        public bool IsStornoRow { get { return Cent < 0 || !string.IsNullOrEmpty(StornoZuBelegnr); } }
        public bool Grey { get { return Storniert || IsStornoRow; } }
        public bool IsTemplate { get { return !string.IsNullOrEmpty(Intervall) && Intervall != "keine"; } }
        public int Month
        {
            get
            {
                DateTime d;
                if (DateTime.TryParse(Datum, CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return d.Month;
                return 0;
            }
        }

        public static Buchung From(object o)
        {
            Func<string, string> s = k => { var v = J.S(o, k); return v == "null" ? null : v; };
            return new Buchung
            {
                Id = (long)J.N(o, "id"), Belegnr = s("belegnr"), Datum = s("datum"), Art = s("art"), Kategorie = s("kategorie"),
                Beschreibung = s("beschreibung"), Cent = (long)J.N(o, "betrag_cent"), Zahlungsart = s("zahlungsart"), Partner = s("partner"),
                Intervall = s("intervall"), BelegDatei = s("beleg_datei"), HatBeleg = J.B(o, "hat_beleg"), StorniertVon = s("storniert_von"),
                StornoGrund = s("storno_grund"), ErstelltAm = s("erstellt_am"), ErstelltVon = s("erstellt_von"), StornoZuBelegnr = s("storno_zu_belegnr"), StorniertVonBelegnr = s("storniert_von_belegnr")
            };
        }
    }

    /// <summary>Grouped monthly bars (Ausgaben / Einnahmen) with hover tooltip. Values in euros.</summary>
    class FinBarChart : Control
    {
        double[] aus = new double[12], ein = new double[12];
        static readonly string[] Mon = { "Jan", "Feb", "Mär", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez" };
        int hover = -1;
        double reveal = 1;

        public FinBarChart()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Font = Theme.F(8.5f);
        }

        public void SetData(double[] a, double[] e)
        {
            aus = a; ein = e; hover = -1;
            Anim.Run(this, 700, p => { reveal = p; Invalidate(); });
        }

        Rectangle Plot { get { return new Rectangle(Theme.Px(52), Theme.Px(30), Math.Max(10, Width - Theme.Px(52) - Theme.Px(10)), Math.Max(10, Height - Theme.Px(30) - Theme.Px(26))); } }

        static double NiceMax(double m)
        {
            if (m <= 0) return 100;
            double p = Math.Pow(10, Math.Floor(Math.Log10(m)));
            foreach (var f in new[] { 1.0, 2, 2.5, 5, 10 }) if (m <= f * p) return f * p;
            return 10 * p;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var pl = Plot;
            int i = e.X < pl.Left || e.X > pl.Right ? -1 : (int)((e.X - pl.Left) / (pl.Width / 12.0));
            if (i > 11) i = -1;
            if (i != hover) { hover = i; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var pl = Plot;
            double max = NiceMax(Math.Max(aus.Max(), ein.Max()));
            using (var pen = new Pen(Theme.Border))
            {
                for (int k = 0; k <= 4; k++)
                {
                    int y = pl.Bottom - (int)(pl.Height * k / 4.0);
                    g.DrawLine(pen, pl.Left, y, pl.Right, y);
                    TextRenderer.DrawText(g, LineChart.CompactNum(max * k / 4), Font, new Rectangle(0, y - Theme.Px(9), pl.Left - Theme.Px(6), Theme.Px(18)), Theme.Muted,
                        TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
                }
            }
            double gw = pl.Width / 12.0;
            float bw = (float)(gw * 0.30);
            for (int i = 0; i < 12; i++)
            {
                float cx = (float)(pl.Left + gw * (i + 0.5));
                if (i == hover) using (var hb = new SolidBrush(Theme.Alpha(Theme.CardHi, 140))) g.FillRectangle(hb, (float)(pl.Left + gw * i), pl.Top, (float)gw, pl.Height);
                DrawBar(g, cx - bw - 1, bw, aus[i] / max * pl.Height * reveal, pl.Bottom, Theme.Red);
                DrawBar(g, cx + 1, bw, ein[i] / max * pl.Height * reveal, pl.Bottom, Theme.Green);
                TextRenderer.DrawText(g, Mon[i], Font, new Rectangle((int)(pl.Left + gw * i), pl.Bottom + Theme.Px(4), (int)gw, Theme.Px(18)), i == hover ? Theme.Text : Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix);
            }
            // legend
            int lx = pl.Left;
            foreach (var it in new[] { new KeyValuePair<string, Color>("Ausgaben", Theme.Red), new KeyValuePair<string, Color>("Einnahmen", Theme.Green) })
            {
                using (var b = new SolidBrush(it.Value)) g.FillEllipse(b, lx, Theme.Px(10), Theme.Px(9), Theme.Px(9));
                TextRenderer.DrawText(g, it.Key, Font, new Point(lx + Theme.Px(14), Theme.Px(6)), Theme.Text2, TextFormatFlags.NoPrefix);
                lx += Theme.Px(24) + Theme.Measure(it.Key, Font).Width;
            }
            if (hover >= 0)
            {
                var lines = new[] { Mon[hover], "Ausgaben  " + FinanzenView.Eur(aus[hover]), "Einnahmen  " + FinanzenView.Eur(ein[hover]) };
                int w = lines.Max(l => Theme.Measure(l, Font).Width) + Theme.Px(20), h = Theme.Px(16) * 3 + Theme.Px(12);
                int x = (int)(pl.Left + gw * (hover + 0.5)) + Theme.Px(14);
                if (x + w > Width - 4) x = (int)(pl.Left + gw * (hover + 0.5)) - w - Theme.Px(14);
                var r = new Rectangle(Math.Max(4, x), pl.Top + Theme.Px(4), w, h);
                using (var path = Theme.RoundRect(r, 8))
                using (var b = new SolidBrush(Theme.CardHi)) using (var p = new Pen(Theme.Border)) { g.FillPath(b, path); g.DrawPath(p, path); }
                for (int i = 0; i < 3; i++)
                    TextRenderer.DrawText(g, lines[i], Font, new Point(r.X + Theme.Px(10), r.Y + Theme.Px(6) + i * Theme.Px(16)), i == 0 ? Theme.Text : Theme.Text2, TextFormatFlags.NoPrefix);
            }
        }

        static void DrawBar(Graphics g, float x, float w, double h, int bottom, Color c)
        {
            if (h < 1) return;
            var r = new RectangleF(x, (float)(bottom - h), w, (float)h);
            using (var b = new SolidBrush(c)) g.FillRectangle(b, r);
        }
    }

    /// <summary>Category list with a share bar per row. Height follows the row count.</summary>
    class FinCatBars : Control
    {
        public class Item { public string Name, Art; public double Euro, Percent; }
        List<Item> items = new List<Item>();
        double reveal = 1;

        public FinCatBars()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            Font = Theme.F(9.5f);
        }

        int RowH { get { return Theme.Px(40); } }
        public int NeededHeight { get { return RowH * Math.Max(1, items.Count) + Theme.Px(4); } }

        public void SetItems(List<Item> l)
        {
            items = l ?? new List<Item>();
            Anim.Run(this, 700, p => { reveal = p; Invalidate(); });
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Card);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "Noch keine Buchungen in diesem Jahr", Font, ClientRectangle, Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            double max = Math.Max(0.01, items.Max(i => i.Euro));
            for (int i = 0; i < items.Count; i++)
            {
                var it = items[i];
                bool exp = (it.Art ?? "").ToLowerInvariant().StartsWith("aus");
                var col = exp ? Theme.Red : Theme.Green;
                int y = i * RowH;
                TextRenderer.DrawText(g, it.Name, Theme.F(9.5f, FontStyle.Bold), new Rectangle(0, y, Width / 2, Theme.Px(20)), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                TextRenderer.DrawText(g, FinanzenView.Eur(it.Euro) + "   " + it.Percent.ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + " %", Font,
                    new Rectangle(Width / 2, y, Width / 2, Theme.Px(20)), Theme.Text2, TextFormatFlags.Right | TextFormatFlags.NoPrefix);
                var track = new Rectangle(0, y + Theme.Px(24), Width, Theme.Px(7));
                using (var p = Theme.RoundRect(track, 3)) using (var b = new SolidBrush(Theme.Input)) g.FillPath(b, p);
                int w = (int)(track.Width * it.Euro / max * reveal);
                if (w > 2)
                    using (var p = Theme.RoundRect(new Rectangle(track.X, track.Y, w, track.Height), 3)) using (var b = new SolidBrush(col)) g.FillPath(b, p);
            }
        }
    }

    /// <summary>
    /// Finanzen: simple bookkeeping (Ausgaben/Einnahmen with Belegnummer and receipts, immutable – corrections only via Storno).
    /// Data comes from finanzen.py on the server – via SSH (owner) or the team gateway (/panel/finanzen, rights finanzen.view / finanzen.edit).
    /// </summary>
    class FinanzenView : Panel
    {
        const string ArtAus = "ausgabe", ArtEin = "einnahme";
        static readonly string[] IntervalNames = { "keine", "monatlich", "jährlich" };
        static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
        static readonly string[] MonCols = { "Jan", "Feb", "Mär", "Apr", "Mai", "Jun", "Jul", "Aug", "Sep", "Okt", "Nov", "Dez" };
        const int MaxBeleg = 10 * 1024 * 1024;

        readonly Api api;
        readonly bool viaGateway;
        readonly Func<Ssh> getSsh;
        readonly Func<bool> sshReady;
        readonly Action<string, Color> setStatus;
        bool canEdit;

        Label hSub;
        TabBtn[] tabs;
        Panel[] pages;
        ProgressLine progress;
        ComboBox yearBox, artBox, katBox;
        Field search;
        FlatBtn bNew, bRefresh, bCsv, bZip;
        KpiCard cAus, cEin, cSaldo, cVor;
        FinBarChart chart;
        FinCatBars cats;
        RoundPanel catCard;
        ListView matrix, list;
        Label listCount, exportInfo;
        Timer searchTimer;

        object overview;
        List<Buchung> yearRows = new List<Buchung>();
        List<Buchung> rows = new List<Buchung>();
        List<KeyValuePair<string, string>> kategorien = new List<KeyValuePair<string, string>>();
        int year = DateTime.Now.Year;
        bool busy, installed, loaded, updating;

        public FinanzenView(Api api, bool viaGateway, Func<Ssh> getSsh, Func<bool> sshReady, Action<string, Color> setStatus)
        {
            this.api = api;
            this.viaGateway = viaGateway;
            this.getSsh = getSsh;
            this.sshReady = sshReady;
            this.setStatus = setStatus;
            canEdit = !viaGateway;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
        }

        public void SetCanEdit(bool v)
        {
            canEdit = v;
            bNew.Visible = v;
        }

        public void Deactivate() { }

        public async void Activate()
        {
            if (!viaGateway && !sshReady())
            {
                hSub.Text = "Die Buchhaltung wird per SSH vom Server gelesen – richte SSH einmal unter „Docker“ ein.";
                hSub.ForeColor = Theme.Yellow;
                return;
            }
            if (!loaded) await Reload();
        }

        // ───────────────────────────── formatting ─────────────────────────────
        public static string Eur(double euro) { return euro.ToString("#,##0.00", De) + " €"; }
        static string Cent(long c) { return Eur(c / 100.0); }
        static string Signed(Buchung b) { return b.Cent < 0 ? "−" + Cent(-b.Cent) : (b.IsExpense ? "−" : "+") + Cent(b.Cent); }

        // ───────────────────────────── server calls ─────────────────────────────
        static string ScriptSource()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("finanzen.py"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        /// <summary>Key = data, Value = error text (null on success).</summary>
        async Task<KeyValuePair<object, string>> Call(string cmd, Dictionary<string, object> args)
        {
            if (viaGateway)
            {
                var r = await api.SendRaw("POST", "/panel/finanzen", Encoding.UTF8.GetBytes(J.ToJson(new Dictionary<string, object> { { "cmd", cmd }, { "args", args } })),
                                          "application/json", null, 150000);
                return r.Ok ? new KeyValuePair<object, string>(r.Data, null) : new KeyValuePair<object, string>(null, r.Error);
            }
            var sb = new StringBuilder();
            if (!installed)
                sb.Append(Ssh.InstallIfNewer("/opt/pxmx/finanzen.py", ScriptSource()));
            if (!args.ContainsKey("actor")) args["actor"] = "Owner (Panel)";
            sb.Append("python3 /opt/pxmx/finanzen.py ").Append(cmd).Append(' ')
              .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(J.ToJson(args))));
            var res = await getSsh().Run(sb.ToString(), 150000);
            if (!res.Ok) return new KeyValuePair<object, string>(null, res.Message);
            installed = true;
            object j;
            try { j = J.Parse(res.Out.Trim()); } catch { return new KeyValuePair<object, string>(null, "Unerwartete Antwort: " + res.Message); }
            if (J.S(j, "status") != "ok") return new KeyValuePair<object, string>(null, J.S(j, "error") ?? "Fehler");
            return new KeyValuePair<object, string>(J.Get(j, "data"), null);
        }

        static Dictionary<string, object> Args(params object[] kv)
        {
            var d = new Dictionary<string, object>();
            for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
            return d;
        }

        // ───────────────────────────── layout ─────────────────────────────
        void Build()
        {
            var header = Ui.Header("Finanzen", out hSub, new[] { "Übersicht", "Buchungen", "Export" }, out tabs, i => SetTab(i));
            hSub.Text = "Ausgaben und Einnahmen mit Belegnummer – Buchungen sind unveränderlich, Korrekturen nur per Storno.";
            yearBox = Ui.Combo(90);
            bNew = new FlatBtn("+ Neue Buchung", Theme.Accent) { Size = new Size(Theme.Px(150), 34) }.Tip("Ausgabe oder Einnahme erfassen");
            bRefresh = new FlatBtn("↻", Theme.CardHi) { Size = new Size(38, 34) }.Tip("Neu laden");
            foreach (Control c in new Control[] { yearBox, bNew, bRefresh }) { c.Anchor = AnchorStyles.Top | AnchorStyles.Right; header.Controls.Add(c); }
            header.Resize += (s, e) =>
            {
                bRefresh.Location = new Point(header.Width - bRefresh.Width - 2, 0);
                bNew.Location = new Point(bRefresh.Left - bNew.Width - 8, 0);
                yearBox.Location = new Point(bNew.Left - yearBox.Width - 10, 3);
            };
            yearBox.SelectedIndexChanged += async (s, e) =>
            {
                if (updating || yearBox.SelectedItem == null) return;
                int y;
                if (int.TryParse(Convert.ToString(yearBox.SelectedItem), out y) && y != year) { year = y; if (!busy) await Reload(); }
            };
            bRefresh.Click += async (s, e) => await Reload();
            bNew.Click += async (s, e) => await NewBooking();

            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0) };
            pages = new[] { BuildOverview(), BuildList(), BuildExport() };
            foreach (var p in pages) content.Controls.Add(p);
            Controls.Add(content);
            Controls.Add(progress);
            Controls.Add(header);
            SetTab(0);
        }

        void SetTab(int i) { for (int k = 0; k < pages.Length; k++) { pages[k].Visible = k == i; tabs[k].Active = k == i; } }

        static RoundPanel Card(Control inner, int height, string title, string caption = null)
        {
            var card = new RoundPanel { Dock = DockStyle.Top, Height = height, Padding = new Padding(12, title == null ? 8 : 46, 12, 12) };
            if (title != null)
            {
                var t = Theme.Lbl(title, Theme.F(10.5f, FontStyle.Bold), Theme.Text, Color.Transparent);
                t.Location = new Point(18, 14);
                card.Controls.Add(t);
                if (caption != null)
                {
                    var c = Theme.Lbl(caption, Theme.F(8.5f), Theme.Muted, Color.Transparent);
                    card.Controls.Add(c);
                    t.SizeChanged += (s, e) => c.Location = new Point(t.Right + 8, t.Top + (t.Height - c.Height) / 2 + 1);
                    c.Location = new Point(t.Right + 8, 17);
                }
            }
            inner.Dock = DockStyle.Fill;
            card.Controls.Add(inner);
            return card;
        }

        static Panel Gap(int h) { return new Panel { Dock = DockStyle.Top, Height = h, BackColor = Theme.Bg }; }

        static void Stack(Control parent, params Control[] top)
        {
            for (int i = top.Length - 1; i >= 0; i--) parent.Controls.Add(top[i]);
        }

        Panel BuildOverview()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(0, 0, 8, 16) };
            var cards = new CardGrid { Dock = DockStyle.Top, MinCardWidth = 200, CardHeight = 124, Gap = 14 };
            cAus = new KpiCard(Icons.Block, "Ausgaben", Theme.Red);
            cEin = new KpiCard(Icons.Add, "Einnahmen", Theme.Green);
            cSaldo = new KpiCard(Icons.Coins, "Saldo", Theme.Yellow);
            cVor = new KpiCard(Icons.Chart, "Ausgaben vs. Vorjahr", Theme.Accent);
            cards.Controls.AddRange(new Control[] { cAus, cEin, cSaldo, cVor });

            chart = new FinBarChart();
            var chartCard = Card(chart, 300, "Monatsverlauf", "Ausgaben und Einnahmen je Monat");
            cats = new FinCatBars();
            catCard = Card(cats, 200, "Kategorien", "Anteil an der jeweiligen Gesamtsumme");

            matrix = Ui.List(new[] { "Kategorie" }.Concat(MonCols).Concat(new[] { "Summe" }).ToArray(),
                             new[] { 170, 78, 78, 78, 78, 78, 78, 78, 78, 78, 78, 78, 78, 100 }, false, false);
            for (int i = 1; i < matrix.Columns.Count; i++) matrix.Columns[i].TextAlign = HorizontalAlignment.Right;
            var matCard = Card(matrix, 520, "Kosten Jahrgang", "automatische Tabelle: Kategorie × Monat");
            Stack(page, cards, Gap(14), chartCard, Gap(14), catCard, Gap(14), matCard);
            return page;
        }

        Panel BuildList()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            artBox = Ui.Combo(130);
            artBox.Items.AddRange(new object[] { "Alle Arten", "Ausgaben", "Einnahmen" });
            artBox.SelectedIndex = 0;
            katBox = Ui.Combo(180);
            katBox.Items.Add("Alle Kategorien");
            katBox.SelectedIndex = 0;
            search = new Field(false) { Width = 240, Height = 36 };
            search.Placeholder = "Suchen (Beschreibung, Partner, Belegnr) …";
            listCount = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Theme.Bg);
            listCount.Margin = new Padding(0, 10, 0, 0);
            var bar = Ui.Bar(46, artBox, katBox, search, listCount);

            list = Ui.List(new[] { "Belegnr", "Datum", "Art", "Kategorie", "Beschreibung", "Partner", "Betrag", "Beleg" },
                           new[] { 120, 90, 90, 140, 260, 150, 110, 60 }, false, false);
            list.Columns[6].TextAlign = HorizontalAlignment.Right;
            list.Columns[7].TextAlign = HorizontalAlignment.Center;
            list.DrawSubItem += (s, e) =>
            {
                var b = e.Item.Tag as RowTag != null ? ((RowTag)e.Item.Tag).Data as Buchung : null;
                if (b == null || !b.Grey) return;
                var size = TextRenderer.MeasureText(e.SubItem.Text, list.Font);
                var right = list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right;
                int w = Math.Min(size.Width, e.Bounds.Width - 14);
                int x = right ? e.Bounds.Right - 6 - w : (list.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Center ? e.Bounds.X + (e.Bounds.Width - w) / 2 : e.Bounds.X + 8);
                if (e.SubItem.Text.Length > 0)
                    using (var p = new Pen(Theme.Muted)) e.Graphics.DrawLine(p, x, e.Bounds.Y + e.Bounds.Height / 2, x + w, e.Bounds.Y + e.Bounds.Height / 2);
            };
            list.DoubleClick += async (s, e) => { var b = Ui.SelData<Buchung>(list); if (b != null) await ShowDetail(b); };
            var card = Card(list, 100, "Buchungen", "Doppelklick zeigt Details, Beleg und Storno");
            card.Dock = DockStyle.Fill;
            page.Controls.Add(card);
            page.Controls.Add(bar);

            artBox.SelectedIndexChanged += async (s, e) => { if (!updating) await LoadList(); };
            katBox.SelectedIndexChanged += async (s, e) => { if (!updating) await LoadList(); };
            searchTimer = new Timer { Interval = 450 };
            searchTimer.Tick += async (s, e) => { searchTimer.Stop(); await LoadList(); };
            search.Box.TextChanged += (s, e) => { searchTimer.Stop(); searchTimer.Start(); };
            return page;
        }

        Panel BuildExport()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            var inner = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
            var t = Theme.Lbl("Jahresexport", Theme.F(12f, FontStyle.Bold), Theme.Text, Theme.Card);
            t.Location = new Point(8, 6);
            exportInfo = Theme.Lbl("", Theme.F(9.5f), Theme.Text2, Theme.Card);
            exportInfo.Location = new Point(8, 40);
            exportInfo.MaximumSize = new Size(720, 0);
            exportInfo.AutoSize = true;
            exportInfo.Text = "Exportiert alle Buchungen des oben gewählten Jahres (inklusive Stornos).\n\n" +
                              "CSV: Tabelle für Excel / Steuerberater.\nZIP: CSV plus alle angehängten Belege (Dateinamen = Belegnummer).";
            bCsv = new FlatBtn("CSV exportieren", Theme.Accent) { Location = new Point(8, 150), Size = new Size(Theme.Px(170), 38) };
            bZip = new FlatBtn("ZIP mit Belegen", Theme.CardHi) { Location = new Point(bCsv.Right + 10, 150), Size = new Size(Theme.Px(170), 38) };
            bCsv.Click += async (s, e) => await Export("csv");
            bZip.Click += async (s, e) => await Export("zip");
            inner.Controls.AddRange(new Control[] { t, exportInfo, bCsv, bZip });
            var card = new RoundPanel { Dock = DockStyle.Top, Height = 230, Padding = new Padding(18, 14, 18, 14) };
            inner.Dock = DockStyle.Fill;
            card.Controls.Add(inner);
            page.Controls.Add(card);
            return page;
        }

        // ───────────────────────────── loading ─────────────────────────────
        async Task Reload()
        {
            if (busy) return;
            busy = true;
            progress.Set(double.NaN);
            hSub.ForeColor = Theme.Muted;
            hSub.Text = "Lade Buchhaltung …";
            try
            {
                var r = await Call("uebersicht", Args("jahr", year));
                if (r.Value != null) { hSub.Text = "Fehler: " + r.Value; hSub.ForeColor = Theme.Red; return; }
                overview = r.Key;
                var all = await Call("liste", Args("jahr", year));
                if (all.Value != null) { hSub.Text = "Fehler: " + all.Value; hSub.ForeColor = Theme.Red; return; }
                yearRows = J.L(J.Get(all.Key, "buchungen")).Select(Buchung.From).ToList();
                var k = await Call("kategorien", Args());
                if (k.Value == null)
                    kategorien = J.L(J.Get(k.Key, "kategorien")).Select(o => new KeyValuePair<string, string>(J.S(o, "name"), J.S(o, "art"))).ToList();
                FillYears();
                FillKategorien();
                loaded = true;
                RenderOverview();
                busy = false;
                await LoadList();
                hSub.Text = "Ausgaben und Einnahmen mit Belegnummer – Buchungen sind unveränderlich, Korrekturen nur per Storno.";
            }
            finally { busy = false; progress.Set(-1); }
        }

        void FillYears()
        {
            var ys = J.L(J.Get(overview, "jahre")).Select(o => (int)Convert.ToDouble(o, CultureInfo.InvariantCulture)).ToList();
            if (!ys.Contains(year)) ys.Add(year);
            if (!ys.Contains(DateTime.Now.Year)) ys.Add(DateTime.Now.Year);
            ys.Sort((a, b) => b.CompareTo(a));
            updating = true;
            yearBox.Items.Clear();
            foreach (var y in ys) yearBox.Items.Add(y.ToString());
            yearBox.SelectedItem = year.ToString();
            updating = false;
        }

        void FillKategorien()
        {
            var cur = katBox.SelectedItem as string;
            updating = true;
            katBox.Items.Clear();
            katBox.Items.Add("Alle Kategorien");
            foreach (var n in kategorien.Select(x => x.Key).Distinct().OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) katBox.Items.Add(n);
            katBox.SelectedItem = cur != null && katBox.Items.Contains(cur) ? cur : "Alle Kategorien";
            updating = false;
        }

        async Task LoadList()
        {
            if (!loaded) return;
            var a = Args("jahr", year);
            if (artBox.SelectedIndex == 1) a["art"] = ArtAus; else if (artBox.SelectedIndex == 2) a["art"] = ArtEin;
            if (katBox.SelectedIndex > 0) a["kategorie"] = (string)katBox.SelectedItem;
            var q = search.Box.Text.Trim();
            if (q.Length > 0) a["suche"] = q;
            progress.Set(double.NaN);
            try
            {
                var r = await Call("liste", a);
                if (r.Value != null) { setStatus("Buchungen konnten nicht geladen werden: " + r.Value, Theme.Red); return; }
                rows = J.L(J.Get(r.Key, "buchungen")).Select(Buchung.From).ToList();
                RenderList();
            }
            finally { progress.Set(-1); }
        }

        // ───────────────────────────── rendering ─────────────────────────────
        void RenderOverview()
        {
            double aus = J.N(overview, "summe_ausgaben") / 100.0, ein = J.N(overview, "summe_einnahmen") / 100.0;
            var vor = J.Get(overview, "vorjahr");
            double vAus = J.N(vor, "summe_ausgaben") / 100.0, vEin = J.N(vor, "summe_einnahmen") / 100.0;
            double saldo = ein - aus;
            cAus.Set(Eur(aus), "im Jahr " + year, Theme.Text);
            cEin.Set(Eur(ein), "im Jahr " + year, Theme.Text);
            cSaldo.Set(Eur(saldo), saldo >= 0 ? "Überschuss" : "Defizit", saldo >= 0 ? Theme.Green : Theme.Red);
            if (vAus > 0)
            {
                double pct = (aus - vAus) / vAus * 100;
                cVor.Set((pct >= 0 ? "+" : "") + pct.ToString("0.#", De) + " %", "Vorjahr: " + Eur(vAus) + (vEin > 0 ? "  ·  Einnahmen " + Eur(vEin) : ""), Theme.Text,
                         pct > 0 ? "teurer" : (pct < 0 ? "günstiger" : "gleich"), pct > 0 ? Theme.Red : (pct < 0 ? Theme.Green : Theme.Muted));
            }
            else cVor.Set("–", "kein Vorjahr zum Vergleich", Theme.Muted);

            var ma = new double[12]; var me = new double[12];
            foreach (var m in J.L(J.Get(overview, "monate")))
            {
                int i = (int)J.N(m, "monat") - 1;
                if (i < 0 || i > 11) continue;
                ma[i] = J.N(m, "ausgaben") / 100.0; me[i] = J.N(m, "einnahmen") / 100.0;
            }
            chart.SetData(ma, me);

            var items = J.L(J.Get(overview, "kategorien")).Select(o => new FinCatBars.Item
            {
                Name = J.S(o, "kategorie"), Art = J.S(o, "art"), Euro = J.N(o, "summe") / 100.0, Percent = J.N(o, "anteil_prozent")
            }).OrderByDescending(i => i.Euro).ToList();
            cats.SetItems(items);
            catCard.Height = Theme.Px(70) + cats.NeededHeight;
            RenderMatrix();
        }

        void RenderMatrix()
        {
            // category × month from the booking list (cancelled bookings and their reversal entries are ignored)
            var valid = yearRows.Where(b => !b.Grey && b.Cent > 0 && b.Month >= 1).ToList();
            var result = new List<ListViewItem>();
            double[] gAus = new double[13], gEin = new double[13];
            Action<bool> section = exp =>
            {
                var sub = valid.Where(b => b.IsExpense == exp).ToList();
                var tot = exp ? gAus : gEin;
                foreach (var grp in sub.GroupBy(b => b.Kategorie ?? "–").OrderBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
                {
                    var cells = new string[14];
                    cells[0] = grp.Key;
                    double sum = 0;
                    for (int m = 1; m <= 12; m++)
                    {
                        double v = grp.Where(b => b.Month == m).Sum(b => b.Cent) / 100.0;
                        cells[m] = v == 0 ? "" : v.ToString("#,##0.00", De);
                        tot[m - 1] += v; sum += v;
                    }
                    tot[12] += sum;
                    cells[13] = sum.ToString("#,##0.00", De);
                    result.Add(Ui.Row(new RowTag { Key = (exp ? "a:" : "e:") + grp.Key, Dot = exp ? Theme.Red : Theme.Green }, cells));
                }
                var tc = new string[14];
                tc[0] = exp ? "SUMME AUSGABEN" : "SUMME EINNAHMEN";
                for (int m = 0; m < 12; m++) tc[m + 1] = tot[m] == 0 ? "" : tot[m].ToString("#,##0.00", De);
                tc[13] = tot[12].ToString("#,##0.00", De);
                var it = Ui.Row(new RowTag { Key = exp ? "sa" : "se" }, tc);
                foreach (ListViewItem.ListViewSubItem si in it.SubItems) si.ForeColor = exp ? Theme.Red : Theme.Green;
                result.Add(it);
            };
            section(true);
            section(false);
            var sc = new string[14];
            sc[0] = "SALDO";
            for (int m = 0; m < 13; m++) { double v = gEin[m] - gAus[m]; sc[m + 1] = (m < 12 && gEin[m] == 0 && gAus[m] == 0) ? "" : v.ToString("#,##0.00", De); }
            var sit = Ui.Row(new RowTag { Key = "saldo" }, sc);
            for (int i = 1; i < sit.SubItems.Count; i++)
            {
                double v;
                sit.SubItems[i].ForeColor = double.TryParse(sc[i], NumberStyles.Number, De, out v) && v < 0 ? Theme.Red : Theme.Text;
            }
            result.Add(sit);
            Ui.Fill(matrix, result);
        }

        void RenderList()
        {
            var items = new List<ListViewItem>();
            foreach (var b in rows)
            {
                var it = Ui.Row(new RowTag { Key = b.Id.ToString(), Data = b },
                    b.Belegnr, FormatDate(b.Datum), b.IsExpense ? "Ausgabe" : "Einnahme", b.Kategorie,
                    (b.IsTemplate ? "↻ wiederkehrend · " : "") + (b.IsStornoRow ? "Storno zu " + b.StornoZuBelegnr + (string.IsNullOrEmpty(b.Beschreibung) ? "" : " – " + b.Beschreibung) : (b.Storniert ? "storniert durch " + b.StorniertVonBelegnr + " – " + b.Beschreibung : b.Beschreibung)), b.Partner,
                    Signed(b), b.HatBeleg ? "📎" : "–");
                if (b.Grey)
                    foreach (ListViewItem.ListViewSubItem si in it.SubItems) si.ForeColor = Theme.Muted;
                else
                {
                    it.SubItems[6].ForeColor = b.IsExpense ? Theme.Red : Theme.Green;
                    it.SubItems[7].ForeColor = b.HatBeleg ? Theme.Text2 : Theme.Muted;
                }
                items.Add(it);
            }
            Ui.Fill(list, items);
            long aus = rows.Where(b => !b.Grey && b.IsExpense).Sum(b => b.Cent), ein = rows.Where(b => !b.Grey && !b.IsExpense).Sum(b => b.Cent);
            listCount.Text = rows.Count + " Buchungen  ·  Ausgaben " + Cent(aus) + "  ·  Einnahmen " + Cent(ein);
        }

        static string FormatDate(string iso)
        {
            DateTime d;
            return DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out d) ? d.ToString("dd.MM.yyyy", De) : iso;
        }

        // ───────────────────────────── detail / storno / beleg ─────────────────────────────
        async Task ShowDetail(Buchung b)
        {
            bool doStorno = false, doBeleg = false;
            using (var f = new DarkForm())
            {
                f.Text = "Buchung " + b.Belegnr;
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                int y = 18;
                Action<string, string, Color> kv = (k, v, c) =>
                {
                    if (string.IsNullOrEmpty(v)) return;
                    var l = Theme.Lbl(k.ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Bg);
                    l.Location = new Point(20, y);
                    var t = Theme.Lbl(v, Theme.F(9.75f), c, Theme.Bg);
                    t.Location = new Point(150, y - 2);
                    t.MaximumSize = new Size(330, 0);
                    f.Controls.Add(l); f.Controls.Add(t);
                    y += Math.Max(24, t.PreferredHeight + 8);
                };
                kv("Belegnr", b.Belegnr, Theme.Text);
                kv("Datum", FormatDate(b.Datum), Theme.Text2);
                kv("Art", b.IsExpense ? "Ausgabe" : "Einnahme", Theme.Text2);
                kv("Betrag", Signed(b), b.IsExpense ? Theme.Red : Theme.Green);
                kv("Kategorie", b.Kategorie, Theme.Text2);
                kv("Beschreibung", b.Beschreibung, Theme.Text2);
                kv("Partner", b.Partner, Theme.Text2);
                kv("Zahlungsart", b.Zahlungsart, Theme.Text2);
                kv("Wiederkehrend", b.IsTemplate ? b.Intervall + " (Vorlage)" : null, Theme.Text2);
                kv("Beleg", b.HatBeleg ? (b.BelegDatei ?? "vorhanden") : "kein Beleg angehängt", b.HatBeleg ? Theme.Text2 : Theme.Muted);
                kv("Erfasst", (b.ErstelltAm ?? "") + (string.IsNullOrEmpty(b.ErstelltVon) ? "" : " von " + b.ErstelltVon), Theme.Muted);
                if (b.Grey)
                {
                    kv(b.IsStornoRow ? "Stornobuchung" : "Storniert", b.IsStornoRow ? "Storno zu " + b.StornoZuBelegnr : "storniert durch " + b.StorniertVonBelegnr, Theme.Yellow);
                    kv("Grund", b.StornoGrund, Theme.Yellow);
                }
                y += 8;
                var close = new FlatBtn("Schließen", Theme.CardHi) { Width = 100 };
                var bBeleg = new FlatBtn("Beleg öffnen", Theme.Accent) { Width = 130, Enabled = b.HatBeleg };
                var bSt = new FlatBtn("Stornieren …", Theme.DarkRed) { Width = 130, Visible = canEdit && !b.Grey && b.Cent > 0 };
                close.DialogResult = DialogResult.Cancel;
                bBeleg.Click += (s, e) => { doBeleg = true; f.Close(); };
                bSt.Click += (s, e) => { doStorno = true; f.Close(); };
                int x = 20;
                foreach (var c in new Control[] { bBeleg, bSt, close }) { if (!c.Visible) continue; c.Location = new Point(x, y); x += c.Width + 8; f.Controls.Add(c); }
                f.CancelButton = close;
                f.ClientSize = new Size(500, y + 56);
                f.ShowDialog(FindForm());
            }
            if (doBeleg) await OpenBeleg(b);
            else if (doStorno) await Storno(b);
        }

        async Task OpenBeleg(Buchung b)
        {
            progress.Set(double.NaN);
            try
            {
                var r = await Call("beleg", Args("id", b.Id));
                if (r.Value != null) { setStatus("Beleg konnte nicht geladen werden: " + r.Value, Theme.Red); return; }
                var name = J.S(r.Key, "name") ?? (b.Belegnr + ".bin");
                name = new string(Path.GetFileName(name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                var dir = Path.Combine(Path.GetTempPath(), "PXMX-ServerSuite-Belege");
                Directory.CreateDirectory(dir);
                var path = Path.Combine(dir, b.Belegnr + "_" + name);
                File.WriteAllBytes(path, Convert.FromBase64String(J.S(r.Key, "b64") ?? ""));
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex) { setStatus("Beleg konnte nicht geöffnet werden: " + ex.Message, Theme.Red); }
            finally { progress.Set(-1); }
        }

        async Task Storno(Buchung b)
        {
            string grund = null;
            while (true)
            {
                grund = Dialogs.Ask(FindForm(), "Buchung " + b.Belegnr + " stornieren",
                                    "Grund der Stornierung (Pflicht). Es entsteht eine Gegenbuchung – die ursprüngliche Buchung bleibt erhalten.", grund ?? "");
                if (grund == null) return;
                grund = grund.Trim();
                if (grund.Length >= 3) break;
                setStatus("Bitte einen Stornogrund angeben (mindestens 3 Zeichen).", Theme.Yellow);
            }
            progress.Set(double.NaN);
            try
            {
                var r = await Call("stornieren", Args("id", b.Id, "grund", grund));
                if (r.Value != null) { setStatus("Storno fehlgeschlagen: " + r.Value, Theme.Red); return; }
                setStatus("Buchung " + b.Belegnr + " storniert (Gegenbuchung " + J.S(r.Key, "belegnr") + ").", Theme.Green);
            }
            finally { progress.Set(-1); }
            loaded = false;
            await Reload();
        }

        // ───────────────────────────── new booking ─────────────────────────────
        class Draft
        {
            public DateTime Datum = DateTime.Today;
            public bool Ausgabe = true;
            public string Kategorie = "", Beschreibung = "", Betrag = "", Zahlungsart = "Überweisung", Partner = "", BelegPfad;
            public int Intervall;
        }

        static bool ParseCents(string s, out long cents)
        {
            cents = 0;
            s = (s ?? "").Replace("€", "").Replace("EUR", "").Replace(" ", "").Trim();
            decimal v;
            if (!decimal.TryParse(s, NumberStyles.Number, De, out v) || v <= 0 || v > 100000000m) return false;
            cents = (long)Math.Round(v * 100m, MidpointRounding.AwayFromZero);
            return cents > 0;
        }

        async Task NewBooking()
        {
            if (!canEdit) return;
            var d = new Draft();
            while (true)
            {
                if (!BookingDialog(d)) return;
                long cents;
                ParseCents(d.Betrag, out cents);
                var a = Args("datum", d.Datum.ToString("yyyy-MM-dd"), "art", d.Ausgabe ? ArtAus : ArtEin, "kategorie", d.Kategorie, "beschreibung", d.Beschreibung,
                             "betrag_cent", cents, "zahlungsart", d.Zahlungsart, "partner", d.Partner, "intervall", d.Intervall == 0 ? "" : IntervalNames[d.Intervall]);
                if (!string.IsNullOrEmpty(d.BelegPfad))
                {
                    try
                    {
                        a["beleg_name"] = Path.GetFileName(d.BelegPfad);
                        a["beleg_b64"] = Convert.ToBase64String(File.ReadAllBytes(d.BelegPfad));
                    }
                    catch (Exception ex) { setStatus("Beleg nicht lesbar: " + ex.Message, Theme.Red); continue; }
                }
                progress.Set(double.NaN);
                KeyValuePair<object, string> r;
                try { r = await Call("buchen", a); }
                finally { progress.Set(-1); }
                if (r.Value != null) { setStatus("Buchung nicht gespeichert: " + r.Value, Theme.Red); continue; }
                var nr = J.S(r.Key, "belegnr");
                setStatus("Gebucht als " + nr + ".", Theme.Green);
                using (var ok = new FormDialog("Buchung gespeichert",
                    "Die Buchung wurde mit der Belegnummer " + nr + " festgehalten."))
                {
                    ok.AddNote("Nächste Schritte: Schreibe die Belegnummer " + nr + " auf den Papierbeleg" +
                               (string.IsNullOrEmpty(d.BelegPfad) ? " oder hänge den Beleg später per Storno + Neubuchung an." : " (Beleg ist bereits angehängt).") +
                               " Gebuchte Einträge sind unveränderlich – Korrekturen laufen über „Stornieren“ und eine neue Buchung.", Theme.Text2);
                    ok.Run(FindForm(), "Fertig");
                }
                if (d.Datum.Year != year && yearBox.Items.Contains(d.Datum.Year.ToString())) year = d.Datum.Year;
                loaded = false;
                await Reload();
                return;
            }
        }

        /// <summary>Shows the booking form; fills <paramref name="d"/> and returns true when the entries are valid.</summary>
        bool BookingDialog(Draft d)
        {
            using (var f = new DarkForm())
            {
                f.Text = "Neue Buchung";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                int y = 16;
                var steps = Theme.Lbl("1  Angaben ausfüllen    ›    2  Beleg anhängen (empfohlen)    ›    3  Speichern – Belegnummer wird vergeben",
                                      Theme.F(8.75f, FontStyle.Bold), Theme.Accent, Theme.Bg);
                steps.Location = new Point(20, y);
                steps.MaximumSize = new Size(520, 0);
                f.Controls.Add(steps);
                y += steps.PreferredHeight + 12;

                Func<string, int> caption = text =>
                {
                    var l = Theme.Lbl(text.ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Bg);
                    l.Location = new Point(20, y);
                    f.Controls.Add(l);
                    return y += 20;
                };

                caption("Datum und Art");
                var dp = new DateTimePicker { Format = DateTimePickerFormat.Short, Value = d.Datum, Location = new Point(20, y), Width = 150, Font = Theme.F(10.5f), CalendarMonthBackground = Theme.Input, CalendarForeColor = Theme.Text };
                var art = new DarkCombo { Location = new Point(180, y), Width = 160 };
                art.Items.AddRange(new object[] { "Ausgabe", "Einnahme" });
                art.SelectedIndex = d.Ausgabe ? 0 : 1;
                f.Controls.Add(dp); f.Controls.Add(art);
                y += 46;

                caption("Kategorie");
                var kat = new DarkCombo { Location = new Point(20, y), Width = 520 };
                const string NewCat = "+ Neue Kategorie …";
                int lastKat = -1;
                Action fillKat = () =>
                {
                    string art2 = art.SelectedIndex == 0 ? ArtAus : ArtEin;
                    var cur = kat.SelectedItem as string ?? d.Kategorie;
                    kat.Items.Clear();
                    foreach (var k in kategorien.Where(x => (x.Value ?? "").ToLowerInvariant().StartsWith(art2.Substring(0, 3)) || string.IsNullOrEmpty(x.Value)).Select(x => x.Key).OrderBy(x => x, StringComparer.CurrentCultureIgnoreCase)) kat.Items.Add(k);
                    kat.Items.Add(NewCat);
                    if (cur != null && kat.Items.Contains(cur)) kat.SelectedItem = cur; else if (kat.Items.Count > 1) kat.SelectedIndex = 0; else kat.SelectedIndex = -1;
                    lastKat = kat.SelectedIndex;
                };
                fillKat();
                art.SelectedIndexChanged += (s, e) => fillKat();
                kat.SelectedIndexChanged += (s, e) =>
                {
                    if ((kat.SelectedItem as string) != NewCat) { lastKat = kat.SelectedIndex; return; }
                    var name = Dialogs.Ask(f, "Neue Kategorie", "Name der neuen Kategorie (" + (art.SelectedIndex == 0 ? "Ausgabe" : "Einnahme") + "):", "");
                    name = (name ?? "").Trim();
                    if (name.Length == 0) { kat.SelectedIndex = Math.Min(lastKat, kat.Items.Count - 1); return; }
                    if (!kategorien.Any(x => string.Equals(x.Key, name, StringComparison.CurrentCultureIgnoreCase)))
                    {
                        var res = CallSync("kategorie_neu", Args("name", name, "art", art.SelectedIndex == 0 ? ArtAus : ArtEin));
                        if (res != null) { setStatus("Kategorie nicht angelegt: " + res, Theme.Red); kat.SelectedIndex = Math.Min(lastKat, kat.Items.Count - 1); return; }
                        kategorien.Add(new KeyValuePair<string, string>(name, art.SelectedIndex == 0 ? ArtAus : ArtEin));
                    }
                    d.Kategorie = name;
                    fillKat();
                    kat.SelectedItem = kategorien.First(x => string.Equals(x.Key, name, StringComparison.CurrentCultureIgnoreCase)).Key;
                };
                f.Controls.Add(kat);
                y += 46;

                caption("Beschreibung");
                var desc = new Field(false) { Location = new Point(20, y), Size = new Size(520, 40) };
                desc.Box.Text = d.Beschreibung;
                f.Controls.Add(desc);
                y += 52;

                caption("Betrag in Euro (z. B. 12,50)");
                var amt = new Field(false) { Location = new Point(20, y), Size = new Size(160, 40) };
                amt.Box.Text = d.Betrag;
                var zahl = new DarkCombo { Location = new Point(190, y + 4), Width = 170 };
                zahl.Items.AddRange(new object[] { "Überweisung", "PayPal", "Karte", "Bar", "Lastschrift", "Sonstige" });
                zahl.Text = d.Zahlungsart;
                if (zahl.SelectedIndex < 0) zahl.SelectedIndex = 0;
                var zl = Theme.Lbl("Zahlungsart", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Bg);
                zl.Location = new Point(190, y - 20);
                f.Controls.Add(zl); f.Controls.Add(amt); f.Controls.Add(zahl);
                var ivl = new DarkCombo { Location = new Point(370, y + 4), Width = 170 };
                ivl.Items.AddRange(new object[] { "keine", "monatlich", "jährlich" });
                ivl.SelectedIndex = d.Intervall;
                var il = Theme.Lbl("Wiederkehrend", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Bg);
                il.Location = new Point(370, y - 20);
                f.Controls.Add(il); f.Controls.Add(ivl);
                y += 52;

                caption("Partner (Lieferant / Kunde, optional)");
                var partner = new Field(false) { Location = new Point(20, y), Size = new Size(520, 40) };
                partner.Box.Text = d.Partner;
                f.Controls.Add(partner);
                y += 52;

                caption("Beleg (PDF, PNG, JPG, WebP – max. 10 MB)");
                var pick = new FlatBtn("Datei wählen …", Theme.CardHi) { Location = new Point(20, y), Width = 150, Height = 36 };
                var pickLbl = Theme.Lbl(string.IsNullOrEmpty(d.BelegPfad) ? "kein Beleg" : Path.GetFileName(d.BelegPfad), Theme.F(9.25f), Theme.Text2, Theme.Bg);
                pickLbl.AutoSize = false; pickLbl.AutoEllipsis = true; pickLbl.Location = new Point(180, y + 9); pickLbl.Size = new Size(360, 20);
                string belegPfad = d.BelegPfad;
                pick.Click += (s, e) =>
                {
                    using (var od = new OpenFileDialog { Title = "Beleg wählen", Filter = "Belege (PDF, PNG, JPG, WebP)|*.pdf;*.png;*.jpg;*.jpeg;*.webp" })
                    {
                        if (od.ShowDialog(f) != DialogResult.OK) return;
                        long len = new FileInfo(od.FileName).Length;
                        if (len > MaxBeleg) { MessageBox.Show(f, "Der Beleg ist größer als 10 MB.", "Beleg zu groß", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                        belegPfad = od.FileName;
                        pickLbl.Text = Path.GetFileName(od.FileName) + "  (" + Theme.Bytes(len) + ")";
                    }
                };
                f.Controls.Add(pick); f.Controls.Add(pickLbl);
                y += 50;

                var err = Theme.Lbl("", Theme.F(9f), Theme.Red, Theme.Bg);
                err.Location = new Point(20, y);
                err.MaximumSize = new Size(520, 0);
                f.Controls.Add(err);
                y += 26;
                var save = new FlatBtn("Buchen", Theme.Accent) { Location = new Point(430, y), Width = 110 };
                var cancel = new FlatBtn("Abbrechen", Theme.CardHi) { Location = new Point(312, y), Width = 110 };
                cancel.DialogResult = DialogResult.Cancel;
                f.Controls.Add(save); f.Controls.Add(cancel);
                f.CancelButton = cancel;
                f.ClientSize = new Size(560, y + 56);

                save.Click += (s, e) =>
                {
                    long c;
                    var k = kat.SelectedItem as string;
                    if (k == null || k == NewCat) { err.Text = "Bitte eine Kategorie wählen."; return; }
                    if (desc.Box.Text.Trim().Length < 2) { err.Text = "Bitte eine Beschreibung eingeben."; return; }
                    if (!ParseCents(amt.Box.Text, out c)) { err.Text = "Betrag ungültig – bitte z. B. 12,50 eingeben (größer als 0)."; return; }
                    d.Datum = dp.Value.Date; d.Ausgabe = art.SelectedIndex == 0; d.Kategorie = k; d.Beschreibung = desc.Box.Text.Trim();
                    d.Betrag = amt.Box.Text.Trim(); d.Zahlungsart = zahl.Text; d.Partner = partner.Box.Text.Trim(); d.Intervall = Math.Max(0, ivl.SelectedIndex);
                    d.BelegPfad = belegPfad;
                    f.DialogResult = DialogResult.OK;
                };
                f.AcceptButton = null;
                return f.ShowDialog(FindForm()) == DialogResult.OK;
            }
        }

        /// <summary>Blocking call for the small category creation inside the modal form (returns an error text or null).</summary>
        string CallSync(string cmd, Dictionary<string, object> args)
        {
            try
            {
                var t = Task.Run(() => Call(cmd, args));
                if (!t.Wait(60000)) return "Zeitüberschreitung";
                return t.Result.Value;
            }
            catch (Exception ex) { return ex.InnerException != null ? ex.InnerException.Message : ex.Message; }
        }

        // ───────────────────────────── export ─────────────────────────────
        async Task Export(string format)
        {
            progress.Set(double.NaN);
            try
            {
                var r = await Call("export", Args("jahr", year, "format", format));
                if (r.Value != null) { setStatus("Export fehlgeschlagen: " + r.Value, Theme.Red); return; }
                var name = J.S(r.Key, "name") ?? ("finanzen-" + year + "." + format);
                using (var sd = new SaveFileDialog { FileName = name, Filter = format == "zip" ? "ZIP-Archiv|*.zip" : "CSV (Excel)|*.csv", Title = "Export speichern" })
                {
                    if (sd.ShowDialog(FindForm()) != DialogResult.OK) return;
                    File.WriteAllBytes(sd.FileName, Convert.FromBase64String(J.S(r.Key, "b64") ?? ""));
                    setStatus("Export " + year + " gespeichert: " + sd.FileName, Theme.Green);
                }
            }
            catch (Exception ex) { setStatus("Export fehlgeschlagen: " + ex.Message, Theme.Red); }
            finally { progress.Set(-1); }
        }
    }
}
