using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>Row payload for sortable lists: numeric sort keys per column (NaN = sort by text) + the source object.</summary>
    class EcoRow
    {
        public double[] N;
        public object Src;
    }

    class EcoSorter : IComparer
    {
        readonly int col;
        readonly bool desc;
        public EcoSorter(int col, bool desc) { this.col = col; this.desc = desc; }

        public int Compare(object a, object b)
        {
            var x = (ListViewItem)a;
            var y = (ListViewItem)b;
            var rx = (x.Tag as RowTag) != null ? ((RowTag)x.Tag).Data as EcoRow : null;
            var ry = (y.Tag as RowTag) != null ? ((RowTag)y.Tag).Data as EcoRow : null;
            int c;
            if (rx != null && ry != null && col < rx.N.Length && col < ry.N.Length && !double.IsNaN(rx.N[col]) && !double.IsNaN(ry.N[col]))
                c = rx.N[col].CompareTo(ry.N[col]);
            else
                c = string.Compare(x.SubItems[col].Text, y.SubItems[col].Text, StringComparison.CurrentCultureIgnoreCase);
            return desc ? -c : c;
        }
    }

    /// <summary>
    /// Economy tab: money supply ("Kurs"), money created/destroyed per source, income per player, shop items (most sold/bought),
    /// price editor for EconomyShopGUI and crate evaluation. Data comes from eco.py on the server – via SSH (owner) or the
    /// team gateway (/panel/eco, rights economy.view / economy.edit).
    /// </summary>
    class EconomyView : Panel
    {
        readonly Api api;
        readonly bool viaGateway;
        bool canEdit;
        readonly Func<Ssh> getSsh;
        readonly Func<bool> sshReady;
        readonly Action<string, Color> setStatus;

        static readonly int[] Periods = { 1, 3, 7, 14, 30 };
        static readonly string[] PeriodNames = { "24 Stunden", "3 Tage", "7 Tage", "14 Tage", "1 Monat" };
        static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");

        Label hSub, priceInfo, playerCount;
        TabBtn[] tabs;
        Panel[] pages;
        ProgressLine progress;
        Segmented periodBox;
        ComboBox serverBox, sectionBox;
        Field playerSearch, priceSearch;
        HeroCard hero;
        Banner warn;
        KpiCard cIn, cOut, cNet, cShop, cCrates, cTax, cOpens, cCrateMoney, cCrateAvg;
        LineChart chFlow, chShop, chCrates;
        FlowBars srcBars, topBars;
        RoundPanel srcCard;
        Panel warnGap;
        ListView playerList, soldList, boughtList, priceList, crateList, rewardList;
        FlatBtn bEdit, bPercent, bAdd, bReload, bTax;

        object data;                       // last summary
        List<object> items = new List<object>();
        bool busy, installed, loaded;
        int days = 7;
        string server = "all";

        public EconomyView(Api api, bool viaGateway, bool canEdit, Func<Ssh> getSsh, Func<bool> sshReady, Action<string, Color> setStatus)
        {
            this.api = api;
            this.viaGateway = viaGateway;
            this.canEdit = canEdit;
            this.getSsh = getSsh;
            this.sshReady = sshReady;
            this.setStatus = setStatus;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
        }

        public void GoTab(int i) { SetTab(i); Activate(); }

        /// <summary>Team members: the right "economy.edit" can be granted or taken away while the panel is open.</summary>
        public void SetCanEdit(bool v)
        {
            canEdit = v;
            foreach (var bb in new[] { bEdit, bPercent, bAdd, bReload, bTax }) bb.Visible = v;
            priceInfo.Text = v ? "Änderungen werden sofort in die Shop-Datei geschrieben (mit Sicherung) – mit „Shop neu laden“ übernehmen."
                               : "Nur Ansicht – zum Ändern fehlt das Recht „Shop-Preise ändern“.";
        }

        public async void Activate()
        {
            if (!viaGateway && !sshReady())
            {
                hSub.Text = "Die Wirtschaft wird per SSH vom Server gelesen – richte SSH einmal unter „Docker“ ein.";
                hSub.ForeColor = Theme.Yellow;
                return;
            }
            if (!loaded) await Reload();
        }

        // ───────────────────────────── server calls ─────────────────────────────
        static string ScriptSource()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("eco.py"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        async Task<KeyValuePair<object, string>> Call(string cmd, Dictionary<string, object> args)
        {
            if (viaGateway)
            {
                var r = await api.SendRaw("POST", "/panel/eco", Encoding.UTF8.GetBytes(J.ToJson(new Dictionary<string, object> { { "cmd", cmd }, { "args", args } })),
                                          "application/json", null, 150000);
                return r.Ok ? new KeyValuePair<object, string>(r.Data, null) : new KeyValuePair<object, string>(null, r.Error);
            }
            var sb = new StringBuilder();
            if (!installed)
                sb.Append(Ssh.InstallIfNewer("/opt/pxmx/eco.py", ScriptSource()));
            if (!args.ContainsKey("actor")) args["actor"] = "Owner (Panel)";
            sb.Append("python3 /opt/pxmx/eco.py ").Append(cmd).Append(' ')
              .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(J.ToJson(args))));
            var res = await getSsh().Run(sb.ToString(), 150000);
            if (!res.Ok) return new KeyValuePair<object, string>(null, res.Message);
            installed = true;
            object j;
            try { j = J.Parse(res.Out.Trim()); } catch { return new KeyValuePair<object, string>(null, "Unerwartete Antwort: " + res.Message); }
            if (J.S(j, "status") != "ok") return new KeyValuePair<object, string>(null, J.S(j, "error") ?? "Fehler");
            return new KeyValuePair<object, string>(J.Get(j, "data"), null);
        }

        // ───────────────────────────── layout ─────────────────────────────
        void Build()
        {
            var header = Ui.Header("Wirtschaft", out hSub, new[] { "Übersicht", "Spieler", "Shop", "Preise", "Crates" }, out tabs, i => SetTab(i));
            periodBox = new Segmented(new[] { "24 h", "3 T", "7 T", "14 T", "30 T" }, 2).Tip("Zeitraum für alle Auswertungen");
            serverBox = Ui.Combo(130);
            serverBox.Items.Add("Alle Server");
            serverBox.SelectedIndex = 0;
            serverBox.Tip("Server, dessen Wirtschaft ausgewertet wird");
            var bRefresh = new FlatBtn("↻", Theme.CardHi) { Size = new Size(38, 34) }.Tip("Neu laden");
            foreach (Control c in new Control[] { periodBox, serverBox, bRefresh }) { c.Anchor = AnchorStyles.Top | AnchorStyles.Right; header.Controls.Add(c); }
            header.Resize += (s, e) =>
            {
                bRefresh.Location = new Point(header.Width - bRefresh.Width - 2, 0);
                serverBox.Location = new Point(bRefresh.Left - serverBox.Width - 8, 3);
                periodBox.Location = new Point(serverBox.Left - periodBox.Width - 10, 0);
            };
            periodBox.SelectedChanged += async (s, e) => { days = Periods[Math.Max(0, periodBox.SelectedIndex)]; if (!busy) await Reload(); };
            serverBox.SelectedIndexChanged += async (s, e) =>
            {
                server = serverBox.SelectedIndex <= 0 ? "all" : (string)serverBox.SelectedItem;
                if (!busy) await Reload();
            };
            bRefresh.Click += async (s, e) => await Reload();

            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0) };
            pages = new[] { BuildOverview(), BuildPlayers(), BuildShop(), BuildPrices(), BuildCrates() };
            foreach (var p in pages) content.Controls.Add(p);
            Controls.Add(content);
            Controls.Add(progress);
            Controls.Add(header);
            SetTab(0);
        }

        void SetTab(int i) { for (int k = 0; k < pages.Length; k++) { pages[k].Visible = k == i; tabs[k].Active = k == i; } }

        /// <summary>Card with a bold title and an optional muted caption next to it.</summary>
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

        /// <summary>Adds controls top-to-bottom into a Dock=Top stack (WinForms docks in reverse order).</summary>
        static void Stack(Control parent, params Control[] top)
        {
            for (int i = top.Length - 1; i >= 0; i--) parent.Controls.Add(top[i]);
        }

        static TableLayoutPanel Row2(Control a, Control b, int height)
        {
            var t = new TableLayoutPanel { Dock = DockStyle.Top, Height = height, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            t.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            foreach (var c in new[] { a, b }) { c.Dock = DockStyle.Fill; c.Margin = new Padding(0, 0, 12, 0); }
            t.Controls.Add(a, 0, 0);
            t.Controls.Add(b, 1, 0);
            return t;
        }

        static RoundPanel ChartCard(LineChart ch)
        {
            var card = new RoundPanel { Padding = new Padding(6, 8, 8, 10) };
            ch.Dock = DockStyle.Fill;
            card.Controls.Add(ch);
            return card;
        }

        static ListView SortList(string[] cols, int[] widths, int[] rightCols, bool multi = false)
        {
            var lv = Ui.List(cols, widths, multi, true);
            foreach (var c in rightCols) lv.Columns[c].TextAlign = HorizontalAlignment.Right;
            int col = -1;
            bool desc = true;
            lv.ColumnClick += (s, e) =>
            {
                if (e.Column == col) desc = !desc; else { col = e.Column; desc = true; }
                lv.ListViewItemSorter = new EcoSorter(e.Column, desc);
                lv.Sort();
            };
            return lv;
        }

        Panel BuildOverview()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(0, 0, 8, 16) };
            warn = new Banner(Theme.Yellow) { Dock = DockStyle.Top };
            warnGap = Gap(12);
            warnGap.Visible = false;

            hero = new HeroCard { Dock = DockStyle.Top, Height = 210, Format = Full };

            var cards = new CardGrid { Dock = DockStyle.Top, MinCardWidth = 200, CardHeight = 124, Gap = 14 };
            cIn = new KpiCard(Icons.Add, "Geld erzeugt", Theme.Green);
            cOut = new KpiCard(Icons.Block, "Geld vernichtet", Theme.Red);
            cNet = new KpiCard(Icons.Chart, "Netto (Inflation)", Theme.Yellow);
            cShop = new KpiCard(Icons.Package, "Shop: Verkauf / Kauf", Theme.Mint);
            cCrates = new KpiCard(Icons.Key, "Geld aus Crates", Theme.Accent);
            cTax = new KpiCard(Icons.Coins, "Verkaufssteuer", Theme.SoftGreen);
            cards.Controls.AddRange(new Control[] { cIn, cOut, cNet, cShop, cCrates, cTax });

            chFlow = new LineChart { Title = "Geld rein / raus", LineColor = Theme.Green, Line2Color = Theme.Red, Compact = true, Name1 = "Erzeugt", Name2 = "Vernichtet" };
            topBars = new FlowBars { Compact = true, EmptyText = "Noch keine Einnahmen im Zeitraum" };
            var topCard = Card(topBars, 100, "Top-Verdiener", "Einnahmen im Zeitraum");

            srcBars = new FlowBars();
            srcCard = Card(srcBars, 200, "Woher kommt das Geld?", "grün = erzeugt, rot = vernichtet");
            Stack(page, warn, warnGap, hero, Gap(14), cards, Gap(2), Row2(ChartCard(chFlow), topCard, 340), Gap(14), srcCard);
            return page;
        }

        Panel BuildPlayers()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            playerSearch = new Field(false) { Width = 220, Height = 36 };
            playerSearch.Placeholder = "Spieler suchen …";
            playerCount = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Theme.Bg);
            playerCount.Margin = new Padding(0, 10, 0, 0);
            var bar = Ui.Bar(46, playerSearch, playerCount);
            playerList = SortList(new[] { "Spieler", "Einnahmen", "Ausgaben", "Netto", "Shop-Verkauf", "Shop-Kauf", "Crates", "Pay erhalten", "Pay gesendet", "Jobs", "Sonstiges", "Kontostand" },
                                  new[] { 140, 95, 95, 95, 95, 85, 80, 90, 90, 75, 80, 105 }, new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11 });
            var card = Card(playerList, 100, "Spieler", "Einnahmen und Ausgaben je Quelle im Zeitraum");
            card.Dock = DockStyle.Fill;
            page.Controls.Add(card);
            page.Controls.Add(bar);
            playerSearch.Box.TextChanged += (s, e) => RenderPlayers();
            return page;
        }

        Panel BuildShop()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            chShop = new LineChart { Title = "Shop: Spieler verkaufen / kaufen", LineColor = Theme.Green, Line2Color = Theme.Mint, Compact = true, Name1 = "Verkauf (Geld rein)", Name2 = "Kauf (Geld raus)" };
            var chartCard = ChartCard(chShop);
            chartCard.Dock = DockStyle.Top;
            chartCard.Height = 260;
            soldList = SortList(new[] { "Item", "Menge", "Erlös", "Ø Preis", "Verkaufspreis", "Spieler" }, new[] { 170, 80, 100, 70, 90, 60 }, new[] { 1, 2, 3, 4, 5 });
            boughtList = SortList(new[] { "Item", "Menge", "Umsatz", "Ø Preis", "Kaufpreis", "Spieler" }, new[] { 170, 80, 100, 70, 90, 60 }, new[] { 1, 2, 3, 4, 5 });
            var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Bg };
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            var a = Card(soldList, 100, "Meistverkauft", "Spieler verkaufen – Geld entsteht");
            var b = Card(boughtList, 100, "Meistgekauft", "Geld wird vernichtet");
            foreach (var c in new[] { a, b }) { c.Dock = DockStyle.Fill; c.Margin = new Padding(0, 0, 12, 0); }
            split.Controls.Add(a, 0, 0);
            split.Controls.Add(b, 1, 0);
            page.Controls.Add(split);
            page.Controls.Add(Gap(12));
            page.Controls.Add(chartCard);
            return page;
        }

        Panel BuildPrices()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            priceSearch = new Field(false) { Width = 220, Height = 36 };
            priceSearch.Placeholder = "Item oder Material suchen …";
            sectionBox = Ui.Combo(150);
            sectionBox.Items.Add("Alle Shops");
            sectionBox.SelectedIndex = 0;
            bEdit = Ui.Btn("✎ Preis ändern", Theme.Accent).Tip("Kauf- und Verkaufspreis des markierten Items ändern (Doppelklick geht auch)");
            bPercent = Ui.Btn("± % für Markierte", Theme.CardHi).Tip("Preise aller markierten Items um einen Prozentsatz anheben/senken");
            bAdd = Ui.Btn("+ Item hinzufügen", Theme.CardHi).Tip("Neues Item in einen Shop aufnehmen");
            bReload = Ui.Btn("⟳ Shop neu laden", Theme.CardHi).Tip("Führt /sreload auf dem Server aus – erst danach gelten geänderte Preise im Spiel");
            bTax = Ui.Btn("% Steuer", Theme.CardHi).Tip("Verkaufssteuer auf Shop-Verkäufe einstellen (wird abgezogen und vernichtet)");
            foreach (var bb in new[] { bEdit, bPercent, bAdd, bReload, bTax }) bb.Visible = canEdit;
            var bar = Ui.Bar(46, priceSearch, sectionBox, bEdit, bPercent, bAdd, bReload, bTax);
            priceInfo = Theme.Lbl(canEdit ? "Änderungen werden sofort in die Shop-Datei geschrieben (mit Sicherung) – mit „Shop neu laden“ übernehmen."
                                          : "Nur Ansicht – zum Ändern fehlt das Recht „Shop-Preise ändern“.", Theme.F(8.75f), Theme.Muted, Theme.Bg);
            priceInfo.AutoSize = false;
            priceInfo.Dock = DockStyle.Top;
            priceInfo.Height = 22;
            priceList = SortList(new[] { "Item", "Material", "Shop", "Kaufpreis", "Verkaufspreis", "Verkauft", "Erlös", "Gekauft", "Hinweis" },
                                 new[] { 170, 150, 100, 90, 100, 80, 95, 80, 160 }, new[] { 3, 4, 5, 6, 7 }, true);
            var card = Card(priceList, 100, "Shop-Preise", "Doppelklick zum Bearbeiten · Strg/Shift für mehrere");
            card.Dock = DockStyle.Fill;
            page.Controls.Add(card);
            page.Controls.Add(priceInfo);
            page.Controls.Add(bar);
            priceSearch.Box.TextChanged += (s, e) => RenderPrices();
            sectionBox.SelectedIndexChanged += (s, e) => RenderPrices();
            priceList.DoubleClick += async (s, e) => { if (canEdit) await EditPrice(); };
            bEdit.Click += async (s, e) => await EditPrice();
            bPercent.Click += async (s, e) => await PercentChange();
            bAdd.Click += async (s, e) => await AddItem();
            bReload.Click += async (s, e) => await ReloadShop();
            bTax.Click += async (s, e) => await EditTax();
            return page;
        }

        Panel BuildCrates()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Padding = new Padding(0, 0, 8, 12) };
            var cards = new CardGrid { Dock = DockStyle.Top, MinCardWidth = 200, CardHeight = 124, Gap = 14 };
            cOpens = new KpiCard(Icons.Key, "Crate-Öffnungen", Theme.Accent);
            cCrateMoney = new KpiCard(Icons.Coins, "Geld aus Crates", Theme.Yellow);
            cCrateAvg = new KpiCard(Icons.Chart, "Ø Geld je Öffnung", Theme.SoftGreen);
            cards.Controls.AddRange(new Control[] { cOpens, cCrateMoney, cCrateAvg });
            chCrates = new LineChart { Title = "Geld aus Crates im Zeitverlauf", LineColor = Theme.Yellow, Compact = true, Name1 = "Geld" };
            var chartCard = ChartCard(chCrates);
            chartCard.Dock = DockStyle.Top;
            chartCard.Height = 250;
            crateList = SortList(new[] { "Crate", "Öffnungen", "Geld-Gewinne", "Geld gesamt", "Ø je Öffnung", "Spieler", "Meiste Öffnungen" },
                                 new[] { 170, 85, 95, 110, 95, 70, 220 }, new[] { 1, 2, 3, 4, 5 });
            rewardList = SortList(new[] { "Crate", "Belohnung", "Gewonnen", "Anteil", "Geld je Gewinn", "Geld gesamt" },
                                  new[] { 160, 220, 80, 70, 110, 110 }, new[] { 2, 3, 4, 5 });
            Stack(page, cards, Gap(2), chartCard, Gap(14), Card(crateList, 270, "Crates im Zeitraum", "Spaltenkopf anklicken zum Sortieren"), Gap(14),
                  Card(rewardList, 400, "Gewinne", "was wie oft gezogen wurde"));
            return page;
        }

        // ───────────────────────────── loading ─────────────────────────────
        async Task Reload()
        {
            if (busy) return;
            busy = true;
            progress.Set(double.NaN);
            hSub.ForeColor = Theme.Muted;
            hSub.Text = "Lade Wirtschaftsdaten …";
            try
            {
                var r = await Call("summary", new Dictionary<string, object> { { "days", days }, { "server", server } });
                if (r.Value != null) { hSub.Text = "Fehler: " + r.Value; hSub.ForeColor = Theme.Red; return; }
                data = r.Key;
                var it = await Call("items", new Dictionary<string, object> { { "server", server } });
                if (it.Value == null)
                {
                    items = J.L(J.Get(it.Key, "items")).ToList();
                    var secs = J.L(J.Get(it.Key, "sections")).Select(x => Convert.ToString(x)).ToList();
                    var cur = sectionBox.SelectedItem as string;
                    sectionBox.BeginUpdate();
                    sectionBox.Items.Clear();
                    sectionBox.Items.Add("Alle Shops");
                    foreach (var s in secs) sectionBox.Items.Add(s);
                    sectionBox.SelectedItem = cur != null && sectionBox.Items.Contains(cur) ? cur : "Alle Shops";
                    sectionBox.EndUpdate();
                }
                FillServers();
                loaded = true;
                Render();
            }
            finally { busy = false; progress.Set(-1); }
        }

        void FillServers()
        {
            var all = J.L(J.Get(data, "all_servers")).Select(x => Convert.ToString(x)).ToList();
            if (serverBox.Items.Count == all.Count + 1) return;
            busy = true;
            serverBox.Items.Clear();
            serverBox.Items.Add("Alle Server");
            foreach (var s in all) serverBox.Items.Add(s);
            serverBox.SelectedItem = server == "all" ? "Alle Server" : server;
            busy = false;
        }

        string[] Labels()
        {
            var step = J.N(data, "step");
            return J.L(J.Get(data, "buckets")).Select(b =>
            {
                var t = Ui.FromUnixMs(Convert.ToDouble(b, CultureInfo.InvariantCulture));
                if (step >= 86400000) return t.ToString("dd.MM.");
                return days <= 1 ? t.ToString("HH:mm") : t.ToString("dd.MM. HH") + "h";
            }).ToArray();
        }

        static double?[] Series(object o) { return J.L(o).Select(x => (double?)Convert.ToDouble(x, CultureInfo.InvariantCulture)).ToArray(); }

        static string M(double v) { return LineChart.CompactNum(v) + " $"; }
        static string Full(double v) { return Ui.Num(Math.Round(v)) + " $"; }
        static string Pct(double v) { return (v >= 0 ? "+" : "") + v.ToString("0.#", De) + " %"; }

        void Render()
        {
            var d = data;
            string period = PeriodNames[Math.Max(0, periodBox.SelectedIndex)];
            double supplyNow = J.N(d, "supply_now"), supplyStart = J.N(d, "supply_start");
            double tin = J.N(d, "total_in"), tout = J.N(d, "total_out"), net = tin - tout;
            var shop = J.Get(d, "shop");
            var crates = J.Get(d, "crates");
            double crateMoney = J.L(J.Get(crates, "list")).Sum(c => J.N(c, "money"));
            int opens = (int)J.L(J.Get(crates, "list")).Sum(c => J.N(c, "opens"));
            double growth = supplyStart > 0 ? (supplyNow - supplyStart) / supplyStart * 100 : 0;

            hSub.ForeColor = Theme.Muted;
            hSub.Text = string.Join(", ", J.L(J.Get(d, "servers")).Select(x => Convert.ToString(x))) + "  ·  " + period + "  ·  " +
                        (int)J.N(d, "accounts") + " Konten  ·  Stand " + DateTime.Now.ToString("HH:mm") +
                        (J.N(J.Get(d, "ausgeblendet"), "anzahl") > 0 ? "  ·  " + (int)J.N(J.Get(d, "ausgeblendet"), "anzahl") + " Team-Konten ausgeblendet" : "");
            var warns = J.L(J.Get(d, "warnings")).Select(x => "⚠ " + Convert.ToString(x)).ToList();
            if (growth > 25 && supplyStart > 0) warns.Insert(0, "⚠ Die Geldmenge ist im Zeitraum um " + Pct(growth) + " gewachsen – es kommt deutlich mehr Geld rein als raus.");
            warn.SetLines(warns);
            warnGap.Visible = warns.Count > 0;

            var labels = Labels();
            hero.Set(Full(supplyNow), supplyStart > 0 ? (growth >= 0 ? "▲ " : "▼ ") + Pct(growth) + "  in " + period : "Verlauf wird noch aufgebaut",
                     supplyStart <= 0 ? Theme.Muted : growth > 25 ? Theme.Red : growth > 5 ? Theme.Yellow : Theme.Green,
                     "Summe aller Kontostände" + (supplyStart > 0 ? " · zu Beginn " + M(supplyStart) : "") + ". Wächst die Geldmenge schneller als die Spielerzahl, verliert Geld an Wert.");
            hero.SetSeries(Series(J.Get(d, "supply")), labels);

            double vol = tin + tout;
            cIn.Set(M(tin), "Shop, Crates, Pay, Jobs …", Theme.Green, vol > 0 ? (tin / vol * 100).ToString("0", De) + " %" : null, Theme.Green);
            cOut.Set(M(tout), "Shop-Käufe, Steuer u. a.", Theme.Red, vol > 0 ? (tout / vol * 100).ToString("0", De) + " %" : null, Theme.Red);
            cNet.Set((net >= 0 ? "+" : "") + M(net), net > 0 ? "mehr Geld im Umlauf" : "Geld wird knapper", net > 0 ? Theme.Yellow : Theme.Green,
                     net > 0 ? "Inflation" : net < 0 ? "Deflation" : null, net > 0 ? Theme.Yellow : Theme.Green);
            cShop.Set(LineChart.CompactNum(J.N(shop, "sell")) + " / " + LineChart.CompactNum(J.N(shop, "buy")), Ui.Num(J.N(shop, "sell_n")) + " Verkäufe · " + Ui.Num(J.N(shop, "buy_n")) + " Käufe", Theme.Text);
            cCrates.Set(M(crateMoney), Ui.Num(opens) + " Öffnungen", Theme.Text);
            RenderTax();
            cIn.Bar = vol > 0 ? tin / vol : -1;
            cOut.Bar = vol > 0 ? tout / vol : -1;

            chFlow.SetData(Series(J.Get(d, "flow_in")), labels, true);
            chFlow.SetSecond(Series(J.Get(d, "flow_out")));

            // sources – one bar per source, green = created, red = destroyed
            double svol = J.L(J.Get(d, "sources")).Sum(s => J.N(s, "in") + J.N(s, "out"));
            var bars = new List<FlowBars.Item>();
            foreach (var s in J.L(J.Get(d, "sources")).OrderByDescending(s => J.N(s, "in") + J.N(s, "out")))
            {
                double i = J.N(s, "in"), o = J.N(s, "out"), share = svol > 0 ? (i + o) / svol * 100 : 0;
                var parts = new List<string>();
                if (i > 0) parts.Add("erzeugt " + Full(i));
                if (o > 0) parts.Add("vernichtet " + Full(o));
                bars.Add(new FlowBars.Item
                {
                    Name = SourceName(J.S(s, "source")), Dot = SourceColor(J.S(s, "source")), In = i, Out = o,
                    Value = Math.Abs(i - o) < 0.5 ? "±0 $" : (i - o > 0 ? "+" : "−") + M(Math.Abs(i - o)),
                    ValueColor = Math.Abs(i - o) < 0.5 ? Theme.Muted : i - o > 0 ? Theme.Yellow : Theme.Green,
                    Left = share.ToString("0.#", De) + " % Anteil · " + Ui.Num(J.N(s, "count")) + " Vorgänge · " + Ui.Num(J.N(s, "players")) + " Spieler",
                    Right = string.Join(" · ", parts)
                });
            }
            srcBars.SetItems(bars);
            srcCard.Height = Theme.Px(46 + 12 + 6) + srcBars.NeededHeight;

            // top earners
            var top = new List<FlowBars.Item>();
            foreach (var p in J.L(J.Get(d, "players")).OrderByDescending(p => J.N(p, "in")).Take(6))
            {
                if (J.N(p, "in") <= 0) break;
                top.Add(new FlowBars.Item
                {
                    Name = J.S(p, "name") ?? J.S(p, "uuid"), Dot = Theme.SoftGreen, In = J.N(p, "in"), Out = J.N(p, "out"),
                    Value = M(J.N(p, "in")), ValueColor = Theme.Text
                });
            }
            topBars.SetItems(top);

            RenderPlayers();
            RenderShop();
            RenderPrices();
            RenderCrates();
        }

        static string SourceName(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "economyshopgui": return "Shop (EconomyShopGUI)";
                case "excellentcrates": return "Crates";
                case "pay": return "Pay (Spieler → Spieler)";
                case "admin": return "Admin (/eco)";
                case "jobs": return "Jobs";
                case "mrvotetrain": return "Votes (MrVoteTrain)";
                case "dailyrewards": return "Tägliche Belohnung";
                case "lottery": return "Lotterie";
                case "ultracoinflip": return "CoinFlip";
                case "crazyauctions": return "Auktionshaus";
                case "notbounties": return "Kopfgelder";
                case "axsellwands": return "Sellwands";
                case "shopkeepers": return "Spieler-Shops (Shopkeepers)";
                case "skript": return "Skript (eigene Skripte)";
                case "deluxemenus": return "Menüs (DeluxeMenus)";
                case "essentials-sell": return "Essentials /sell";
                case "steuer": return "Verkaufssteuer (vernichtet)";
                default: return s;
            }
        }

        static Color SourceColor(string s)
        {
            switch ((s ?? "").ToLowerInvariant())
            {
                case "economyshopgui": return Theme.Mint;
                case "excellentcrates": return Theme.Yellow;
                case "pay": return Theme.SoftGreen;
                case "admin": return Theme.Red;
                case "steuer": return Theme.Red;
                case "jobs": return Theme.Green;
                default: return Theme.Accent;
            }
        }

        void RenderPlayers()
        {
            if (data == null) return;
            var q = playerSearch.Box.Text.Trim().ToLowerInvariant();
            var seen = new HashSet<string>();
            var rows = new List<ListViewItem>();
            Action<object, bool> add = (p, flows) =>
            {
                var uuid = J.S(p, "uuid") ?? "";
                if (!seen.Add(uuid)) return;
                var name = J.S(p, "name") ?? uuid;
                if (q.Length > 0 && !name.ToLowerInvariant().Contains(q)) return;
                double i = J.N(p, "in"), o = J.N(p, "out");
                var n = new[] { double.NaN, i, o, i - o, J.N(p, "shop_sell"), J.N(p, "shop_buy"), J.N(p, "crates"), J.N(p, "pay_in"), J.N(p, "pay_out"), J.N(p, "jobs"), J.N(p, "other"), J.N(p, "balance") };
                var cells = new List<string> { name };
                for (int k = 1; k < n.Length; k++) cells.Add(k == 3 ? (n[k] >= 0 ? "+" : "") + Full(n[k]) : (Math.Abs(n[k]) < 0.005 && k != 11 ? "–" : Full(n[k])));
                var it = Ui.Row(new RowTag { Key = uuid, Data = new EcoRow { N = n, Src = p } }, cells.ToArray());
                it.SubItems[3].ForeColor = i - o > 0 ? Theme.Yellow : Theme.Text2;
                rows.Add(it);
            };
            foreach (var p in J.L(J.Get(data, "players"))) add(p, true);
            foreach (var p in J.L(J.Get(data, "richest"))) add(p, false);
            Ui.Fill(playerList, rows);
            playerCount.Text = rows.Count + " Spieler  ·  Spaltenkopf anklicken zum Sortieren";
        }

        void RenderShop()
        {
            var shop = J.Get(data, "shop");
            var labels = Labels();
            chShop.SetData(Series(J.Get(shop, "sell_b")), labels, true);
            chShop.SetSecond(Series(J.Get(shop, "buy_b")));
            var sold = new List<ListViewItem>();
            var bought = new List<ListViewItem>();
            foreach (var it in J.L(J.Get(shop, "items")))
            {
                string name = J.S(it, "name");
                double sAmt = J.N(it, "sold"), sMoney = J.N(it, "sold_money"), bAmt = J.N(it, "bought"), bMoney = J.N(it, "bought_money");
                if (sAmt > 0)
                    sold.Add(Ui.Row(new RowTag { Key = J.S(it, "path"), Data = new EcoRow { N = new[] { double.NaN, sAmt, sMoney, sMoney / sAmt, J.N(it, "sell_price"), J.N(it, "players") } } },
                        name, Ui.Num(sAmt), Full(sMoney), Ui.Num(sMoney / sAmt), Price(J.N(it, "sell_price")), Ui.Num(J.N(it, "players"))));
                if (bAmt > 0)
                    bought.Add(Ui.Row(new RowTag { Key = J.S(it, "path"), Data = new EcoRow { N = new[] { double.NaN, bAmt, bMoney, bMoney / bAmt, J.N(it, "buy_price"), J.N(it, "players") } } },
                        name, Ui.Num(bAmt), Full(bMoney), Ui.Num(bMoney / bAmt), Price(J.N(it, "buy_price")), Ui.Num(J.N(it, "players"))));
            }
            Ui.Fill(soldList, sold.OrderByDescending(r => ((EcoRow)((RowTag)r.Tag).Data).N[2]).ToList());
            Ui.Fill(boughtList, bought.OrderByDescending(r => ((EcoRow)((RowTag)r.Tag).Data).N[2]).ToList());
        }

        static string Price(double v) { return v < 0 ? "–" : Ui.Num(v); }

        void RenderPrices()
        {
            if (data == null) return;
            var stats = new Dictionary<string, object>();
            foreach (var it in J.L(J.Get(J.Get(data, "shop"), "items"))) stats[J.S(it, "path") ?? ""] = it;
            var q = priceSearch.Box.Text.Trim().ToLowerInvariant();
            var sec = sectionBox.SelectedIndex <= 0 ? null : (string)sectionBox.SelectedItem;
            var rows = new List<ListViewItem>();
            foreach (var it in items)
            {
                string path = J.S(it, "path"), name = J.S(it, "name") ?? "", mat = J.S(it, "material") ?? "", section = J.S(it, "section");
                if (sec != null && section != sec) continue;
                if (q.Length > 0 && !name.ToLowerInvariant().Contains(q) && !mat.ToLowerInvariant().Contains(q) && !(path ?? "").ToLowerInvariant().Contains(q)) continue;
                double buy = J.N(it, "buy"), sell = J.N(it, "sell");
                object st;
                stats.TryGetValue(path ?? "", out st);
                double sold = J.N(st, "sold"), soldMoney = J.N(st, "sold_money"), bought = J.N(st, "bought");
                string hint = "";
                if (buy >= 0 && sell > buy) hint = "⚠ Verkauf > Kauf (Gewinn ohne Arbeit)";
                else if (soldMoney > 0 && soldMoney >= 1e6) hint = "viel verkauft – Preis prüfen?";
                var row = Ui.Row(new RowTag { Key = path, Data = new EcoRow { N = new[] { double.NaN, double.NaN, double.NaN, buy, sell, sold, soldMoney, bought, double.NaN }, Src = it } },
                                 name, mat, section, Price(buy), Price(sell), sold > 0 ? Ui.Num(sold) : "–", soldMoney > 0 ? Full(soldMoney) : "–", bought > 0 ? Ui.Num(bought) : "–", hint);
                if (hint.StartsWith("⚠")) row.SubItems[8].ForeColor = Theme.Red;
                else if (hint.Length > 0) row.SubItems[8].ForeColor = Theme.Yellow;
                rows.Add(row);
            }
            Ui.Fill(priceList, rows);
        }

        void RenderCrates()
        {
            var crates = J.Get(data, "crates");
            var list = J.L(J.Get(crates, "list")).ToList();
            double money = list.Sum(c => J.N(c, "money"));
            int opens = (int)list.Sum(c => J.N(c, "opens"));
            cOpens.Set(Ui.Num(opens), list.Count + " Crates im Zeitraum", Theme.Text);
            cCrateMoney.Set(M(money), Full(money), Theme.Yellow);
            cCrateAvg.Set(opens > 0 ? M(money / opens) : "–", "pro geöffneter Crate", Theme.Text);
            chCrates.SetData(Series(J.Get(crates, "money_b")), Labels(), true);
            var rows = new List<ListViewItem>();
            foreach (var c in list)
            {
                double o = J.N(c, "opens"), m = J.N(c, "money");
                var top = string.Join(", ", J.L(J.Get(c, "top")).Select(t => { var a = J.L(t).ToList(); return a.Count == 2 ? a[0] + " (" + a[1] + "×)" : ""; }));
                rows.Add(Ui.Row(new RowTag { Key = J.S(c, "id"), Data = new EcoRow { N = new[] { double.NaN, o, J.N(c, "money_wins"), m, o > 0 ? m / o : 0, J.N(c, "players"), double.NaN } } },
                    J.S(c, "name"), Ui.Num(o), Ui.Num(J.N(c, "money_wins")), m > 0 ? Full(m) : "–", o > 0 && m > 0 ? Full(m / o) : "–", Ui.Num(J.N(c, "players")), top));
            }
            Ui.Fill(crateList, rows);
            var opensByCrate = list.ToDictionary(c => J.S(c, "id") ?? "", c => J.N(c, "opens"));
            var rrows = new List<ListViewItem>();
            foreach (var r in J.L(J.Get(crates, "rewards")))
            {
                double n = J.N(r, "count"), each = J.N(r, "money_each"), tot = J.N(r, "money");
                double co;
                opensByCrate.TryGetValue(J.S(r, "crate_id") ?? "", out co);
                double share = co > 0 ? n / co * 100 : 0;
                var row = Ui.Row(new RowTag { Key = J.S(r, "crate_id") + "/" + J.S(r, "id"), Data = new EcoRow { N = new[] { double.NaN, double.NaN, n, share, each, tot } } },
                    J.S(r, "crate"), J.S(r, "name"), Ui.Num(n), share.ToString("0.#", De) + " %", each > 0 ? Full(each) : "–", tot > 0 ? Full(tot) : "–");
                if (tot > 0) row.SubItems[5].ForeColor = Theme.Yellow;
                rrows.Add(row);
            }
            Ui.Fill(rewardList, rrows);
        }

        // ───────────────────────────── sell tax ─────────────────────────────
        void RenderTax()
        {
            var tax = J.Get(data, "tax");
            bool on = J.B(tax, "enabled");
            double pct = J.N(tax, "percent");
            double taken = J.L(J.Get(data, "sources")).Where(s => (J.S(s, "source") ?? "").ToLowerInvariant() == "steuer").Sum(s => J.N(s, "out"));
            cTax.Set(on ? pct.ToString("0.##", De) + " %" : "aus", on ? M(taken) + " eingezogen im Zeitraum" : "keine Steuer auf Verkäufe", on ? Theme.Text : Theme.Muted,
                     on ? "aktiv" : "inaktiv", on ? Theme.Green : Theme.Muted);
            bTax.Text = on ? "% Steuer: " + pct.ToString("0.##", De) + " %" : "% Steuer: aus";
            bTax.Width = Theme.Px(Theme.Measure(bTax.Text, bTax.Font).Width + 32);
        }

        async Task EditTax()
        {
            var tax = J.Get(data, "tax");
            Field fPct;
            CheckBox cOn;
            using (var d = new FormDialog("Verkaufssteuer", "Wird bei jedem Verkauf im Shop (/shop, /sellall, /sellgui) und mit Sellwands von der Auszahlung abgezogen. " +
                                          "Das Geld verschwindet aus dem Umlauf – das bremst die Inflation. Spieler sehen den Abzug im Chat."))
            {
                cOn = d.AddCheck("Steuer aktiv", J.B(tax, "enabled"));
                fPct = d.AddText("Steuersatz in %", J.N(tax, "percent").ToString("0.##", CultureInfo.InvariantCulture));
                d.AddNote("Gilt sofort auf allen Servern mit RyvenEco (kein Neustart nötig). Ränge mit dem Recht ryveneco.tax.bypass zahlen keine Steuer.", Theme.Muted);
                if (!d.Run(FindForm(), "Speichern")) return;
            }
            double pct;
            if (!double.TryParse(fPct.Box.Text.Trim().Replace("%", "").Replace(",", "."), NumberStyles.Float, CultureInfo.InvariantCulture, out pct) || pct < 0 || pct > 90)
            { setStatus("Steuersatz muss zwischen 0 und 90 % liegen.", Theme.Red); return; }
            progress.Set(double.NaN);
            try
            {
                var r = await Call("settax", new Dictionary<string, object> { { "percent", pct }, { "enabled", cOn.Checked } });
                if (r.Value != null) { setStatus("Steuer nicht gespeichert: " + r.Value, Theme.Red); return; }
                var t = J.D(J.Get(data, "tax"));
                if (t != null) { t["percent"] = pct; t["enabled"] = cOn.Checked && pct > 0; }
                RenderTax();
                setStatus(cOn.Checked && pct > 0 ? "Verkaufssteuer: " + pct.ToString("0.##", De) + " % – gilt ab sofort." : "Verkaufssteuer ausgeschaltet.", Theme.Green);
            }
            finally { progress.Set(-1); }
        }

        // ───────────────────────────── price editing ─────────────────────────────
        List<object> SelectedItems()
        {
            return priceList.SelectedItems.Cast<ListViewItem>().Select(i => ((EcoRow)((RowTag)i.Tag).Data).Src).Where(x => x != null).ToList();
        }

        static string PriceText(double v) { return v < 0 ? "-1" : v.ToString("0.##", CultureInfo.InvariantCulture); }

        static bool ParsePrice(string s, out double v)
        {
            s = (s ?? "").Trim();
            if (s == "-1" || s == "-" || s.Equals("aus", StringComparison.OrdinalIgnoreCase)) { v = -1; return true; }
            return Ui.TryNum(s, out v);
        }

        async Task<bool> SetPrice(string path, double? buy, double? sell)
        {
            var args = new Dictionary<string, object> { { "server", server }, { "path", path } };
            if (buy.HasValue) args["buy"] = buy.Value;
            if (sell.HasValue) args["sell"] = sell.Value;
            var r = await Call("setprice", args);
            if (r.Value != null) { setStatus("Preis nicht geändert: " + r.Value, Theme.Red); return false; }
            return true;
        }

        void ApplyLocal(string path, double? buy, double? sell)
        {
            foreach (var it in items)
            {
                if (J.S(it, "path") != path) continue;
                var d = J.D(it);
                if (buy.HasValue) d["buy"] = buy.Value;
                if (sell.HasValue) d["sell"] = sell.Value;
            }
        }

        async Task EditPrice()
        {
            var sel = SelectedItems();
            if (sel.Count != 1) { setStatus("Bitte genau ein Item markieren.", Theme.Yellow); return; }
            var it = sel[0];
            Field fBuy, fSell;
            using (var d = new FormDialog("Preis ändern – " + J.S(it, "name"), J.S(it, "section") + " · " + J.S(it, "material") + "\nKaufpreis = was Spieler im Shop zahlen, Verkaufspreis = was sie beim Verkaufen bekommen."))
            {
                fBuy = d.AddText("Kaufpreis (-1 = nicht kaufbar)", PriceText(J.N(it, "buy")));
                fSell = d.AddText("Verkaufspreis (-1 = nicht verkaufbar)", PriceText(J.N(it, "sell")));
                d.AddNote("Tipp: 1,5k = 1500. Danach „Shop neu laden“, damit es im Spiel gilt.", Theme.Muted);
                if (!d.Run(FindForm(), "Speichern")) return;
            }
            double buy, sell;
            if (!ParsePrice(fBuy.Box.Text, out buy) || !ParsePrice(fSell.Box.Text, out sell)) { setStatus("Ungültiger Preis.", Theme.Red); return; }
            if (buy >= 0 && sell > buy && !Dialogs.Confirm(FindForm(), "Achtung", "Der Verkaufspreis ist höher als der Kaufpreis – Spieler können dann endlos Geld machen (kaufen → verkaufen).\n\nTrotzdem speichern?")) return;
            progress.Set(double.NaN);
            try
            {
                if (await SetPrice(J.S(it, "path"), buy, sell))
                {
                    ApplyLocal(J.S(it, "path"), buy, sell);
                    RenderPrices();
                    setStatus("Preis von " + J.S(it, "name") + " gespeichert – „Shop neu laden“ übernimmt ihn im Spiel.", Theme.Green);
                }
            }
            finally { progress.Set(-1); }
        }

        async Task PercentChange()
        {
            var sel = SelectedItems();
            if (sel.Count == 0) { setStatus("Bitte Items markieren (Strg/Shift für mehrere).", Theme.Yellow); return; }
            Field fPct;
            ComboBox which;
            using (var d = new FormDialog("Preise prozentual ändern", sel.Count + " Item(s) markiert. Beispiel: -20 senkt um 20 %, 15 erhöht um 15 %."))
            {
                fPct = d.AddText("Änderung in %", "-10");
                which = d.AddCombo("Welche Preise", new[] { "Nur Verkaufspreis (Geld rein)", "Nur Kaufpreis (Geld raus)", "Beide" }, "Nur Verkaufspreis (Geld rein)");
                if (!d.Run(FindForm(), "Anwenden")) return;
            }
            double pct;
            var t = fPct.Box.Text.Trim().Replace("%", "").Replace(",", ".");
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out pct) || pct <= -100 || pct > 1000) { setStatus("Ungültiger Prozentwert.", Theme.Red); return; }
            bool doSell = which.SelectedIndex != 1, doBuy = which.SelectedIndex != 0;
            if (!Dialogs.Confirm(FindForm(), "Preise ändern", sel.Count + " Item(s): " + (doSell && doBuy ? "Kauf- und Verkaufspreis" : doSell ? "Verkaufspreis" : "Kaufpreis") + " " + Pct(pct) + "?")) return;
            int ok = 0;
            progress.Set(0);
            try
            {
                for (int i = 0; i < sel.Count; i++)
                {
                    var it = sel[i];
                    double b = J.N(it, "buy"), s = J.N(it, "sell");
                    double? nb = doBuy && b > 0 ? (double?)Math.Round(b * (1 + pct / 100), 2) : null;
                    double? ns = doSell && s > 0 ? (double?)Math.Round(s * (1 + pct / 100), 2) : null;
                    if (nb == null && ns == null) continue;
                    if (await SetPrice(J.S(it, "path"), nb, ns)) { ApplyLocal(J.S(it, "path"), nb, ns); ok++; }
                    progress.Set((i + 1.0) / sel.Count);
                }
            }
            finally { progress.Set(-1); }
            RenderPrices();
            setStatus(ok + " Preis(e) geändert – „Shop neu laden“ übernimmt sie im Spiel.", ok > 0 ? Theme.Green : Theme.Yellow);
        }

        async Task AddItem()
        {
            var secs = sectionBox.Items.Cast<object>().Skip(1).Select(x => Convert.ToString(x)).ToList();
            if (secs.Count == 0) { setStatus("Keine Shops gefunden.", Theme.Red); return; }
            Field fMat, fName, fBuy, fSell;
            ComboBox fSec;
            using (var d = new FormDialog("Item hinzufügen", "Das Item wird auf der letzten Seite des gewählten Shops angehängt."))
            {
                fSec = d.AddCombo("Shop", secs, sectionBox.SelectedIndex > 0 ? (string)sectionBox.SelectedItem : secs[0]);
                fMat = d.AddText("Material (Minecraft-ID, z. B. DIAMOND, OAK_LOG)", "");
                fName = d.AddText("Anzeigename (optional, &-Farbcodes erlaubt)", "");
                fBuy = d.AddText("Kaufpreis (-1 = nicht kaufbar)", "100");
                fSell = d.AddText("Verkaufspreis (-1 = nicht verkaufbar)", "25");
                if (!d.Run(FindForm(), "Hinzufügen")) return;
            }
            double buy, sell;
            if (!ParsePrice(fBuy.Box.Text, out buy) || !ParsePrice(fSell.Box.Text, out sell)) { setStatus("Ungültiger Preis.", Theme.Red); return; }
            var mat = fMat.Box.Text.Trim().ToUpperInvariant().Replace("MINECRAFT:", "").Replace(' ', '_');
            progress.Set(double.NaN);
            try
            {
                var r = await Call("additem", new Dictionary<string, object> { { "server", server }, { "section", (string)fSec.SelectedItem }, { "material", mat },
                                                                                { "name", fName.Box.Text.Trim() }, { "buy", buy }, { "sell", sell } });
                if (r.Value != null) { setStatus("Nicht hinzugefügt: " + r.Value, Theme.Red); return; }
                setStatus(mat + " wurde zu " + fSec.SelectedItem + " hinzugefügt – „Shop neu laden“ übernimmt es im Spiel.", Theme.Green);
            }
            finally { progress.Set(-1); }
            await Reload();
            SetTab(3);
        }

        async Task ReloadShop()
        {
            if (!Dialogs.Confirm(FindForm(), "Shop neu laden", "/sreload auf dem Server ausführen? Geänderte Preise und neue Items gelten danach sofort im Spiel.")) return;
            progress.Set(double.NaN);
            try
            {
                var r = await Call("reload", new Dictionary<string, object> { { "server", server } });
                if (r.Value != null) setStatus("Neu laden fehlgeschlagen: " + r.Value, Theme.Red);
                else setStatus("Shop auf " + J.S(r.Key, "server") + " neu geladen.", Theme.Green);
            }
            finally { progress.Set(-1); }
        }
    }
}
