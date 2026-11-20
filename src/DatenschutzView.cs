using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>
    /// "Datenschutz &amp; Compliance": outgoing-connection watchdog, DSGVO deletions, Art. 15 information, report, plugin documentation,
    /// databases and record of processing. Same commands as the web panel (mod.py: verbindungen, ds_antraege, plugins, …).
    /// </summary>
    class DatenschutzView : Panel
    {
        static readonly CultureInfo De = CultureInfo.GetCultureInfo("de-DE");
        static readonly string[] Rechtsgrundlagen =
        {
            "", "Art. 6 Abs. 1 lit. b DSGVO (Vertrag / Spielbetrieb)", "Art. 6 Abs. 1 lit. f DSGVO (berechtigtes Interesse)",
            "Art. 6 Abs. 1 lit. a DSGVO (Einwilligung)", "Art. 6 Abs. 1 lit. c DSGVO (rechtliche Pflicht)", "keine personenbezogenen Daten"
        };

        readonly ModClient mc;
        readonly Action<string, Color> setStatus;

        /// <summary>Permission check ("datenschutz.view|edit|loeschen|auskunft"). Owner = everything (default); team members: set from the gateway permissions.</summary>
        public Func<string, bool> Can = p => true;
        /// <summary>Raised after ds_status: (unbekannte Ziele, Plugins ohne Doku).</summary>
        public event Action<int, int> BadgeChanged;
        public int BadgeUnknown, BadgeUndocumented;

        Label hSub;
        TabBtn[] tabs;
        Panel[] pages;
        ProgressLine progress;
        int tab;
        bool busy;
        readonly Timer poll = new Timer { Interval = 5000 };
        int tick;

        // Wächter
        CheckBox wAuto;
        Segmented wFilter;
        KpiCard[] wK;
        ListView wLive, wHist;
        FlatBtn bMark;
        object wData;
        // Löschungen
        Field lName;
        FlatBtn bDelete, bFree, bReject;
        ListView lList;
        // Auskunft
        Field aName;
        FlatBtn bAusk, bKat, bKatAlle;
        FlowLayoutPanel kPanel;
        Panel kHost;
        List<string> kIds = new List<string>();
        List<CheckBox> kBoxes = new List<CheckBox>();
        bool kLoaded;
        Label aInfo;
        ListView aList;
        // Bericht
        KpiCard[] rK;
        TextBox rText;
        ListView rDienste, rUnklar;
        // Plugins
        object plug;
        KpiCard[] pK;
        Segmented pFilter;
        Field pSearch;
        ListView pList;
        Panel pDetail;
        string pSel;
        // Datenbanken
        KpiCard[] dK;
        ListView dList;
        // Verzeichnis
        Label vInfo;
        ListView vList;
        FlatBtn bCsv;

        public DatenschutzView(ModClient mc, Action<string, Color> setStatus)
        {
            this.mc = mc;
            this.setStatus = setStatus;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
            poll.Tick += async (s, e) => { if (Visible && !busy) await Tick(); };
        }

        // ───────────────────────────── public API ─────────────────────────────
        public void GoTab(int i) { SetTab(Math.Max(0, Math.Min(pages.Length - 1, i))); Activate(); }

        public async void Activate()
        {
            poll.Start();
            ApplyPerms();
            await LoadTab(true);
            await RefreshBadge();
        }

        public void Deactivate() { poll.Stop(); }

        public void ApplyPerms()
        {
            bool edit = Can("datenschutz.edit"), del = Can("datenschutz.loeschen"), ausk = Can("datenschutz.auskunft");
            bMark.Visible = edit;
            lName.Visible = bDelete.Visible = bFree.Visible = bReject.Visible = del;
            aName.Visible = bAusk.Visible = ausk;
        }

        /// <summary>Reads ds_status (unknown targets + undocumented plugins) for a sidebar badge.</summary>
        public async Task RefreshBadge()
        {
            if (!Can("datenschutz.view")) return;
            var r = await mc.Call("ds_status");
            if (r.Value != null) return;
            BadgeUnknown = (int)J.N(r.Key, "unbekannt");
            BadgeUndocumented = (int)J.N(r.Key, "undokumentiert");
            var h = BadgeChanged;
            if (h != null) h(BadgeUnknown, BadgeUndocumented);
        }

        // ───────────────────────────── helpers ─────────────────────────────
        static string Fmt(double n) { return n.ToString("N0", De); }
        static string Sz(double b) { return b >= 1048576 ? (b / 1048576).ToString("0.0", De) + " MB" : Fmt(b / 1024) + " KB"; }
        static string Z(object unix)
        {
            double s;
            if (unix == null || !double.TryParse(Convert.ToString(unix, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out s) || s <= 0) return "–";
            var d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(s).ToLocalTime();
            return d.Year == DateTime.Today.Year ? d.ToString("dd.MM. HH:mm") : d.ToString("dd.MM.yy HH:mm");
        }
        static string ZS(object o, string k) { return Z(J.Get(o, k)); }
        static List<string> Strs(object o, string k) { return J.L(J.Get(o, k)).Select(Convert.ToString).ToList(); }
        static string Join(IEnumerable<string> l, string def = "–") { var s = string.Join(", ", l); return s.Length == 0 ? def : s; }
        IWin32Window Owner { get { return FindForm(); } }

        static RoundPanel Card(Control c) { var p = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) }; p.Controls.Add(c); return p; }
        static Label Cap(string text)
        {
            var l = Theme.Lbl(text.ToUpperInvariant(), Theme.F(8f, FontStyle.Bold), Theme.Muted, Theme.Bg);
            l.Dock = DockStyle.Top; l.AutoSize = false; l.Height = Theme.Px(26); l.Padding = new Padding(0, 8, 0, 0);
            return l;
        }
        static Label Note(string text, int h = 24)
        {
            var l = Theme.Lbl(text, Theme.F(8.75f), Theme.Muted, Theme.Bg);
            l.Dock = DockStyle.Bottom; l.AutoSize = false; l.Height = Theme.Px(h); l.Padding = new Padding(2, 6, 0, 0);
            return l;
        }
        static CardGrid Kpis(out KpiCard[] k, params KpiCard[] cards)
        {
            var g = new CardGrid { Dock = DockStyle.Top, MinCardWidth = 180, CardHeight = 110, Gap = 14 };
            g.Controls.AddRange(cards);
            k = cards;
            return g;
        }
        static Panel Gap(int h) { return new Panel { Dock = DockStyle.Top, Height = Theme.Px(h), BackColor = Theme.Bg }; }
        static void Color3(KpiCard k, string v, string sub, bool bad, bool warn = false)
        {
            k.Set(v, sub, bad ? Theme.Red : warn ? Theme.Yellow : Theme.Text);
        }
        static Color Dot(bool known) { return known ? Theme.Green : Theme.Red; }
        static void Tint(ListViewItem row, int col, Color c) { row.SubItems[col].ForeColor = c; }

        void SetTab(int i)
        {
            tab = i;
            for (int k = 0; k < pages.Length; k++) { pages[k].Visible = k == i; tabs[k].Active = k == i; }
        }

        async Task Tick()
        {
            tick++;
            if (tab == 0 && wAuto.Checked) await LoadWaechter(false);
            else if (tab == 1 && tick % 2 == 0) await LoadAntraege();
            if (tick % 12 == 0) await RefreshBadge();
        }

        async Task LoadTab(bool progressBar)
        {
            if (busy) return;
            busy = true;
            if (progressBar) progress.Set(double.NaN);
            try
            {
                switch (tab)
                {
                    case 0: await LoadWaechter(true); break;
                    case 1: await LoadAntraege(); break;
                    case 2: await LoadAuskunft(); break;
                    case 3: await LoadBericht(); break;
                    default: await LoadPlugins(); break;
                }
                if (tab != 0) hSub.Text = "Ausgehende Verbindungen, Plugins, Datenbanken und Verarbeitungsverzeichnis · Stand " + DateTime.Now.ToString("HH:mm:ss");
            }
            finally { busy = false; progress.Set(-1); }
        }

        // ───────────────────────────── layout ─────────────────────────────
        void Build()
        {
            var header = Ui.Header("Datenschutz & Compliance", out hSub,
                new[] { "Verbindungs-Wächter", "Löschungen", "Auskunft", "Bericht", "Plugins", "Datenbanken", "Verzeichnis" }, out tabs,
                async i => { SetTab(i); await LoadTab(true); });
            var bRefresh = new FlatBtn("↻", Theme.CardHi) { Size = new Size(38, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right }.Tip("Neu laden");
            header.Controls.Add(bRefresh);
            header.Resize += (s, e) => bRefresh.Location = new Point(header.Width - bRefresh.Width - 2, 0);
            bRefresh.Click += async (s, e) => { await LoadTab(true); await RefreshBadge(); };

            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
            pages = new[] { BuildWaechter(), BuildLoeschungen(), BuildAuskunft(), BuildBericht(), BuildPlugins(), BuildDatenbanken(), BuildVerzeichnis() };
            foreach (var p in pages) content.Controls.Add(p);
            Controls.Add(content);
            Controls.Add(Gap(8));
            Controls.Add(progress);
            Controls.Add(header);
            SetTab(0);
            ApplyPerms();
        }

        // ───────────────────────────── Verbindungs-Wächter ─────────────────────────────
        Panel BuildWaechter()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            wAuto = new CheckBox { Text = "Live (alle 5 s)", Checked = true, ForeColor = Theme.Text2, BackColor = Theme.Bg, AutoSize = true, Margin = new Padding(0, 9, 12, 8), Cursor = Cursors.Hand };
            wFilter = new Segmented(new[] { "Alle", "Minecraft-Server", "Andere Container" }, 0) { Margin = new Padding(0, 1, 10, 8) };
            bMark = Ui.Btn("Als geprüft markieren …", Theme.CardHi).Tip("Markiertes unbekanntes Ziel als geprüft markieren (nur wenn der Dienst in der Datenschutzerklärung steht!)");
            var bar = Ui.Bar(46, wAuto, wFilter, bMark);
            var cards = Kpis(out wK, new KpiCard(Icons.Globe, "Jetzt offen", Theme.SoftGreen), new KpiCard(Icons.Block, "Davon unbekannt", Theme.Red),
                new KpiCard(Icons.Shield, "Bekannte Ziele", Theme.Green), new KpiCard(Icons.Search, "Unbekannte Ziele", Theme.Mint));
            wLive = Ui.List(new[] { "Server / Container", "Ziel", "Dienst", "Verb.", "Status", "Freigabe / Grund" }, new[] { 190, 170, 280, 60, 130, 360 });
            wHist = Ui.List(new[] { "Server / Container", "Ziel", "Dienst", "Zertifikat / rDNS", "Zuerst", "Zuletzt", "Gesehen", "Status", "Freigabe / Grund" }, new[] { 170, 160, 230, 230, 100, 100, 80, 130, 360 });
            var liveCard = Card(wLive); liveCard.Dock = DockStyle.Top; liveCard.Height = Theme.Px(200);
            page.Controls.Add(Card(wHist));
            page.Controls.Add(Cap("Verlauf – alles, was der Wächter je gesehen hat (wird jede Minute aufgezeichnet)"));
            page.Controls.Add(Gap(6));
            page.Controls.Add(liveCard);
            page.Controls.Add(Cap("Live – jetzt gerade offen (alle Container)"));
            page.Controls.Add(Gap(6));
            page.Controls.Add(cards);
            page.Controls.Add(bar);
            wFilter.SelectedChanged += (s, e) => ShowWaechter();
            bMark.Click += async (s, e) => await MarkSelected();
            wLive.DoubleClick += async (s, e) => await MarkSelected();
            wHist.DoubleClick += async (s, e) => await MarkSelected();
            return page;
        }

        async Task LoadWaechter(bool force)
        {
            var r = await mc.Call("verbindungen");
            if (r.Value != null) { hSub.Text = "Fehler: " + r.Value; hSub.ForeColor = Theme.Red; return; }
            hSub.ForeColor = Theme.Muted;
            wData = r.Key;
            hSub.Text = "Grün = bekannt und in der Datenschutzerklärung · Rot = unbekannt (Discord-Bot meldet neue Ziele im Team-Kanal) · Stand " + DateTime.Now.ToString("HH:mm:ss");
            ShowWaechter();
        }

        void ShowWaechter()
        {
            if (wData == null) return;
            var d = wData;
            var live = J.L(J.Get(d, "live")).ToList();
            var hist = J.L(J.Get(d, "verlauf")).ToList();
            string f = new[] { "alle", "mc", "andere" }[Math.Max(0, wFilter.SelectedIndex)];
            var liveF = live.Where(c => f == "alle" || (f == "mc" ? (J.S(c, "container") ?? "crafty_container") == "crafty_container" : (J.S(c, "container") ?? "crafty_container") != "crafty_container")).ToList();
            int unb = live.Count(c => !J.B(c, "bekannt"));
            int cont = live.Select(c => J.S(c, "container") ?? "crafty_container").Distinct().Count();
            wK[0].Set(Fmt(live.Count), "aus " + cont + " Containern", Theme.Text);
            wK[1].Set(Fmt(unb), unb > 0 ? "bitte prüfen!" : "alles bekannt", unb > 0 ? Theme.Red : Theme.Green);
            wK[2].Set(Fmt(J.N(d, "bekannt")), "in der Datenschutzerklärung", Theme.Green);
            wK[3].Set(Fmt(J.N(d, "unbekannt")), "im Verlauf", J.N(d, "unbekannt") > 0 ? Theme.Red : Theme.Text);
            Func<object, string> st = c => J.B(c, "bekannt") ? "bekannt" : (J.B(c, "neu") ? "NEU – ungeprüft" : "unbekannt");
            var lrows = liveF.Select(c => Ui.Row(new RowTag { Key = J.S(c, "ip") + ":" + J.S(c, "port") + "|" + J.S(c, "server_name"), Data = c, Dot = Dot(J.B(c, "bekannt")) },
                J.S(c, "server_name") + ((J.S(c, "container") ?? "crafty_container") != "crafty_container" ? " (Container)" : ""),
                J.S(c, "ip") + ":" + J.S(c, "port"), J.S(c, "dienst") ?? "–", J.S(c, "verbindungen"), st(c), FG(c))).ToList();
            foreach (var row in lrows) Tint(row, 4, J.B(((RowTag)row.Tag).Data, "bekannt") ? Theme.Green : Theme.Red);
            Ui.Fill(wLive, lrows);
            var hrows = hist.Select(h => Ui.Row(new RowTag { Key = J.S(h, "ip") + ":" + J.S(h, "port") + "|" + J.S(h, "server_name"), Data = h, Dot = Dot(J.B(h, "bekannt")) },
                J.S(h, "server_name"), J.S(h, "ip") + ":" + J.S(h, "port"), J.S(h, "dienst") ?? "–", J.S(h, "zertifikat") ?? J.S(h, "rdns") ?? "–",
                ZS(h, "zuerst"), ZS(h, "zuletzt"), Fmt(J.N(h, "anzahl")) + "×", J.B(h, "bekannt") ? "bekannt" : "unbekannt", FG(h))).ToList();
            foreach (var row in hrows) Tint(row, 7, J.B(((RowTag)row.Tag).Data, "bekannt") ? Theme.Green : Theme.Red);
            Ui.Fill(wHist, hrows);
        }

        static string FG(object c)
        {
            string fr = J.S(c, "freigabe"), gr = J.S(c, "grund");
            if (string.IsNullOrEmpty(fr) && string.IsNullOrEmpty(gr)) return J.B(c, "bekannt") ? "fehlt" : "–";
            return (string.IsNullOrEmpty(fr) ? "–" : fr) + " · " + (string.IsNullOrEmpty(gr) ? "–" : gr);
        }

        async Task MarkSelected()
        {
            if (!Can("datenschutz.edit")) return;
            var c = Ui.SelData<object>(wLive) ?? Ui.SelData<object>(wHist);
            if (c == null) { setStatus("Bitte ein Ziel markieren.", Theme.Yellow); return; }
            if (J.B(c, "bekannt")) { setStatus("Dieses Ziel ist bereits bekannt.", Theme.Yellow); return; }
            var ip = J.S(c, "ip");
            string dienst, grund;
            using (var d = new FormDialog("Ziel als geprüft markieren", "Nur markieren, wenn der Dienst in der Datenschutzerklärung steht!"))
            {
                var f = d.AddText("Welcher Dienst ist " + ip + "?  (z. B. bStats – Plugin-Statistik)", J.S(c, "dienst") ?? "");
                var g = d.AddText("Grund / Rechtsgrundlage (optional)", "");
                if (!d.Run(Owner, "Markieren")) return;
                dienst = f.Box.Text.Trim();
                grund = g.Box.Text.Trim();
            }
            if (dienst.Length == 0) { setStatus("Bitte den Dienst angeben.", Theme.Yellow); return; }
            var r = await mc.Call("verbindung_ok", new Dictionary<string, object> { { "ip", ip }, { "dienst", dienst }, { "grund", grund } });
            if (r.Value != null) { setStatus("Nicht markiert: " + r.Value, Theme.Red); return; }
            setStatus(ip + " als geprüft markiert.", Theme.Green);
            await LoadWaechter(true);
            await RefreshBadge();
        }

        // ───────────────────────────── Löschungen ─────────────────────────────
        Panel BuildLoeschungen()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            lName = new Field(false) { Width = 260, Height = 36 };
            lName.Placeholder = "Spielername oder UUID";
            bDelete = Ui.Btn("Daten löschen …", Theme.DarkRed).Tip("Spieler muss nicht online sein – kein Antrag nötig");
            bFree = Ui.Btn("Antrag freigeben & löschen …", Theme.Mix(Theme.Red, Theme.Card, 0.55));
            bReject = Ui.Btn("Antrag ablehnen …", Theme.CardHi);
            var bar = Ui.Bar(46, lName, bDelete, bFree, bReject);
            lList = Ui.List(new[] { "#", "Spieler", "Status", "Gestellt", "Bearbeitet", "Fortschritt", "Auskunft" }, new[] { 55, 170, 180, 100, 160, 200, 140 });
            page.Controls.Add(Card(lList));
            page.Controls.Add(Note("Ablauf: Antrag → Freigabe → der Proxy exportiert die Daten (Discord-DM, falls verknüpft), löscht Datenbanken und schwärzt Logs; dann erledigt jeder Server seinen Teil. Danach bleiben nur UUID + Zeitpunkte als Nachweis.", 40));
            page.Controls.Add(bar);
            bDelete.Click += async (s, e) =>
            {
                var n = lName.Box.Text.Trim();
                if (n.Length == 0) { setStatus("Bitte einen Spieler angeben.", Theme.Yellow); return; }
                if (await Loeschen(n, 0)) { lName.Box.Text = ""; await LoadAntraege(); }
            };
            lName.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; bDelete.PerformClick(); } };
            bFree.Click += async (s, e) =>
            {
                var a = Ui.SelData<object>(lList);
                if (a == null || J.S(a, "status") != "offen") { setStatus("Bitte einen offenen Antrag markieren.", Theme.Yellow); return; }
                if (await Loeschen(J.S(a, "name"), (int)J.N(a, "id"))) await LoadAntraege();
            };
            bReject.Click += async (s, e) =>
            {
                var a = Ui.SelData<object>(lList);
                if (a == null || J.S(a, "status") != "offen") { setStatus("Bitte einen offenen Antrag markieren.", Theme.Yellow); return; }
                var grund = Dialogs.Ask(Owner, "Antrag #" + J.S(a, "id") + " ablehnen", "Grund (sieht der Spieler):", "");
                if (grund == null) return;
                grund = grund.Trim();
                if (grund.Length == 0) { setStatus("Bitte einen Grund angeben.", Theme.Yellow); return; }
                var r = await mc.Call("ds_ablehnen", new Dictionary<string, object> { { "id", (int)J.N(a, "id") }, { "grund", grund } });
                if (r.Value != null) { setStatus("Nicht abgelehnt: " + r.Value, Theme.Red); return; }
                setStatus("Antrag #" + J.S(a, "id") + " abgelehnt.", Theme.Green);
                await LoadAntraege();
            };
            return page;
        }

        /// <summary>Deletion with preview + confirmation (same flow as /datenschutz freigeben).</summary>
        async Task<bool> Loeschen(string spieler, int antragId)
        {
            object v = null;
            if (antragId == 0)
            {
                var p = await mc.Call("ds_vorschau", new Dictionary<string, object> { { "spieler", spieler } });
                if (p.Value != null) { setStatus("Vorschau: " + p.Value, Theme.Red); return false; }
                v = p.Key;
            }
            var name = v != null ? J.S(J.Get(v, "spieler"), "name") ?? spieler : spieler;
            var antrag = v != null ? J.Get(v, "antrag") : null;
            if (antrag != null && J.S(antrag, "status") == "laeuft") { setStatus("Für " + name + " läuft bereits eine Löschung (#" + J.S(antrag, "id") + ").", Theme.Yellow); return false; }
            CheckBox ok;
            using (var d = new FormDialog("Daten von " + name + " löschen", null))
            {
                d.AddNote("Endgültig – das lässt sich nicht rückgängig machen.", Theme.Red);
                d.AddNote("Gelöscht werden auf allen Servern: Datenbank-Einträge (Geld, Coins, Ränge, Plots, Homes, Statistiken …), Spielerdateien, Discord-Verknüpfung; Logs werden geschwärzt. " +
                          "Der Spieler wird getrennt und bis zum Abschluss gesperrt. Aktive Strafen bleiben bis zum Ablauf.", Theme.Text2);
                if (v != null)
                    d.AddNote(J.B(v, "discord") ? "Discord verknüpft: Der Bot schickt die Datenauskunft vorher per DM." : "Kein Discord: Es gibt keine Datenauskunft per DM – die Daten werden direkt gelöscht.",
                              J.B(v, "discord") ? Theme.Green : Theme.Yellow);
                if (antrag != null) d.AddNote("Offener Antrag #" + J.S(antrag, "id") + " wird damit freigegeben.", Theme.Muted);
                ok = d.AddCheck("Ich habe geprüft, dass die Löschung gewünscht bzw. rechtlich nötig ist.", false);
                if (!d.Run(Owner, "Jetzt löschen")) return false;
            }
            if (!ok.Checked) { setStatus("Nicht gelöscht – die Bestätigung fehlte.", Theme.Yellow); return false; }
            if (!Dialogs.Confirm(Owner, "Endgültig löschen", "Alle Daten von " + name + " jetzt endgültig löschen?")) return false;
            setStatus("Löschung wird gestartet …", Theme.Muted);
            var args = antragId != 0 ? new Dictionary<string, object> { { "id", antragId } } : new Dictionary<string, object> { { "spieler", spieler } };
            var r = await mc.Call("ds_loeschen", args);
            if (r.Value != null) { setStatus("Löschung fehlgeschlagen: " + r.Value, Theme.Red); return false; }
            setStatus("Löschung #" + J.S(r.Key, "id") + " für " + J.S(r.Key, "name") + " gestartet – die Server arbeiten sie jetzt ab.", Theme.Green);
            await RefreshBadge();
            return true;
        }

        static string AntragStatus(string s)
        {
            switch (s) { case "offen": return "Antrag offen"; case "laeuft": return "Läuft"; case "erledigt": return "Erledigt"; case "abgelehnt": return "Abgelehnt"; default: return s; }
        }
        static Color AntragColor(string s)
        {
            switch (s) { case "offen": return Theme.Yellow; case "laeuft": return Theme.SoftGreen; case "erledigt": return Theme.Green; default: return Theme.Muted; }
        }
        static string ExportName(string e)
        {
            switch (e) { case "gesendet": return "per DM gesendet"; case "bereit": return "wird gesendet"; case "fehlgeschlagen": return "DM fehlgeschlagen"; case "keins": return "kein Discord"; default: return e; }
        }

        async Task LoadAntraege()
        {
            var r = await mc.Call("ds_antraege");
            if (r.Value != null) { setStatus("Löschanträge: " + r.Value, Theme.Red); return; }
            hSub.Text = "Löschanträge nach DSGVO Art. 17 · Stand " + DateTime.Now.ToString("HH:mm:ss");
            var rows = J.L(J.Get(r.Key, "antraege")).Select(a =>
            {
                var st = J.S(a, "status");
                int jobs = (int)J.N(a, "jobs");
                string prog = jobs > 0 ? J.S(a, "jobs_fertig") + "/" + jobs + " Server" + (J.S(a, "jobs_offen") != null && J.S(a, "jobs_offen") != "" ? " (wartet: " + J.S(a, "jobs_offen") + ")" : "")
                                       : st == "laeuft" ? "Proxy arbeitet …" : "–";
                if (J.Get(a, "fertig") != null && J.N(a, "fertig") > 0) prog += " · fertig " + ZS(a, "fertig");
                var exp = J.S(a, "export");
                var row = Ui.Row(new RowTag { Key = J.S(a, "id"), Data = a, Dot = AntragColor(st) },
                    "#" + J.S(a, "id"), J.S(a, "name"), AntragStatus(st) + (!string.IsNullOrEmpty(J.S(a, "grund")) ? " – " + J.S(a, "grund") : ""),
                    ZS(a, "erstellt"), !string.IsNullOrEmpty(J.S(a, "handler")) ? J.S(a, "handler") + " · " + ZS(a, "bearbeitet") : "–", prog,
                    !string.IsNullOrEmpty(exp) ? ExportName(exp) : J.B(a, "discord") ? "Discord verknüpft" : "kein Discord");
                Tint(row, 2, AntragColor(st));
                return row;
            }).ToList();
            Ui.Fill(lList, rows);
            if (rows.Count == 0) setStatus("Noch keine Löschanträge.", Theme.Muted);
        }

        // ───────────────────────────── Auskunft ─────────────────────────────
        Panel BuildAuskunft()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            aName = new Field(false) { Width = 260, Height = 36 };
            aName.Placeholder = "Spielername oder UUID";
            bAusk = Ui.Btn("Daten einsehen …", Theme.Accent);
            aInfo = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Theme.Bg);
            aInfo.Margin = new Padding(0, 10, 0, 0);
            bKat = Ui.Btn("Kategorien laden", Theme.SoftGreen);
            bKatAlle = Ui.Btn("Alle abwählen", Theme.Muted);
            bKatAlle.Visible = false;
            var bar = Ui.Bar(46, aName, bKat, bKatAlle, bAusk, aInfo);
            kPanel = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Card };
            kHost = Card(kPanel);
            kHost.Dock = DockStyle.Top; kHost.Height = Theme.Px(200); kHost.Visible = false;
            aList = Ui.List(new[] { "Zeit", "Spieler", "Weg", "An", "Umfang", "Kategorien", "Von", "Gültig bis" }, new[] { 105, 150, 150, 220, 170, 190, 130, 150 });
            page.Controls.Add(Card(aList));
            page.Controls.Add(Note("Auskunft nach Art. 15 DSGVO. Bevorzugt per Discord-DM: Die Verknüpfung entsteht ingame über /link und belegt, dass der Discord-Account zum Minecraft-Account gehört. " +
                                   "Links sind 7 Tage gültig und werden danach gelöscht. Per E-Mail nur senden, wenn die Adresse dem Spieler gehört.", 40));
            page.Controls.Add(kHost);
            page.Controls.Add(bar);
            aName.Box.TextChanged += (s, e) => KatReset();
            bKat.Click += async (s, e) => await LoadKategorien();
            bKatAlle.Click += (s, e) =>
            {
                bool any = kBoxes.Any(b => b.Checked);
                foreach (var b in kBoxes) b.Checked = !any;
            };
            bAusk.Click += async (s, e) =>
            {
                var n = aName.Box.Text.Trim();
                if (n.Length == 0) { setStatus("Bitte einen Spieler angeben.", Theme.Yellow); return; }
                if (kLoaded && !kBoxes.Any(b => b.Checked)) { setStatus("Bitte mindestens eine Kategorie auswählen.", Theme.Yellow); return; }
                if (await Auskunft(n)) { aName.Box.Text = ""; await LoadAuskunft(); }
            };
            aName.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; bAusk.PerformClick(); } };
            return page;
        }

        static string Groesse(double b)
        {
            if (b >= 1048576) return (b / 1048576).ToString("0.0", De) + " MB";
            return (b / 1024).ToString("0.0", De) + " KB";
        }

        void KatReset()
        {
            kLoaded = false;
            kIds.Clear(); kBoxes.Clear();
            kPanel.Controls.Clear();
            kHost.Visible = false;
            bKatAlle.Visible = false;
            bAusk.Enabled = true;
        }

        void KatUpdate()
        {
            bool any = kBoxes.Any(b => b.Checked);
            bAusk.Enabled = !kLoaded || any;
            bKatAlle.Text = any ? "Alle abwählen" : "Alle auswählen";
        }

        async Task LoadKategorien()
        {
            var n = aName.Box.Text.Trim();
            if (n.Length == 0) { setStatus("Bitte einen Spieler angeben.", Theme.Yellow); return; }
            setStatus("Kategorien werden geladen …", Theme.Muted);
            var r = await mc.Call("ds_auskunft_kategorien", new Dictionary<string, object> { { "spieler", n } });
            if (r.Value != null)
            {
                var low = r.Value.ToLowerInvariant();
                bool old = low.Contains("unbekannt") || low.Contains("unknown") || low.Contains("befehl") || low.Contains("command");
                setStatus(old ? "Server-Update nötig: Kategorien werden vom Server noch nicht unterstützt (" + r.Value + ")." : "Kategorien: " + r.Value, Theme.Red);
                return;
            }
            KatReset();
            kPanel.SuspendLayout();
            foreach (var k in J.L(J.Get(r.Key, "kategorien")))
            {
                var id = J.S(k, "id");
                if (string.IsNullOrEmpty(id)) continue;
                var cb = new CheckBox
                {
                    AutoSize = true, Checked = true, ForeColor = Theme.Text, BackColor = Theme.Card, Font = Theme.F(9.5f),
                    Margin = new Padding(6, 4, 6, 4), Cursor = Cursors.Hand,
                    Text = (J.S(k, "titel") ?? id) + "  -  " + (J.S(k, "beschreibung") ?? "") + "  (" + Fmt(J.N(k, "anzahl")) + " Einträge, " + Groesse(J.N(k, "groesse")) + ")"
                };
                cb.CheckedChanged += (s2, e2) => KatUpdate();
                kIds.Add(id); kBoxes.Add(cb);
                kPanel.Controls.Add(cb);
            }
            kPanel.ResumeLayout();
            if (kIds.Count == 0) { setStatus("Zu " + n + " sind keine Daten gespeichert.", Theme.Muted); return; }
            kLoaded = true;
            kHost.Visible = true;
            bKatAlle.Visible = true;
            KatUpdate();
            setStatus(kIds.Count + " Kategorien geladen - Auswahl treffen und \"Daten einsehen …\" klicken.", Theme.Green);
        }

        async Task<bool> Auskunft(string spieler)
        {
            var pv = await mc.Call("ds_vorschau", new Dictionary<string, object> { { "spieler", spieler } });
            if (pv.Value != null) { setStatus("Vorschau: " + pv.Value, Theme.Red); return false; }
            var st = await mc.Call("ds_auskunft_status");
            if (st.Value != null) { setStatus("Auskunft: " + st.Value, Theme.Red); return false; }
            var v = pv.Key;
            var sp = J.Get(v, "spieler");
            var name = J.S(sp, "name") ?? spieler;
            bool discord = J.B(v, "discord"), mail = J.B(st.Key, "mail");
            const string optD = "Link per Discord-DM (empfohlen)", optM = "Per E-Mail", optL = "Herunterladen (ZIP speichern)";
            var opts = new List<string>();
            if (discord) opts.Add(optD);
            if (mail) opts.Add(optM);
            opts.Add(optL);
            ComboBox cWeg; Field fMail;
            using (var d = new FormDialog("Daten einsehen – " + name, "Erstellt eine ZIP mit allen gespeicherten Daten (Datenbanken, Spielstände, Plugin-Daten, Logs, CoreProtect). Es wird nichts gelöscht."))
            {
                cWeg = d.AddCombo("Zustellung", opts, opts[0]);
                d.AddNote(discord ? "Discord: Der Bot schickt einen persönlichen Download-Link (7 Tage gültig). Identität ist über /link belegt."
                                  : "Discord: nicht möglich – kein Discord verknüpft.", discord ? Theme.Green : Theme.Muted);
                d.AddNote(mail ? "E-Mail: Absender " + J.S(st.Key, "absender") + " – vorher prüfen, dass die Adresse wirklich zum Spieler gehört!"
                                : "E-Mail: noch nicht eingerichtet (kein Postfach).", mail ? Theme.Yellow : Theme.Muted);
                fMail = d.AddText("E-Mail-Adresse des Spielers (nur bei E-Mail)", "");
                if (!d.Run(Owner, "Auskunft erstellen")) return false;
            }
            var sel = cWeg.SelectedItem as string;
            string modus = sel == optD ? "discord" : sel == optM ? "senden" : "download";
            var email = fMail.Box.Text.Trim();
            if (modus == "senden" && !email.Contains("@")) { setStatus("Bitte eine E-Mail-Adresse angeben.", Theme.Yellow); return false; }
            if (modus == "senden" && !Dialogs.Confirm(Owner, "E-Mail senden", "Die Auskunft von " + name + " an " + email + " senden?\n\nStimmt die Adresse wirklich mit dem Spieler überein?")) return false;
            setStatus("Auskunft wird erstellt – das kann bis zu einer Minute dauern …", Theme.Muted);
            progress.Set(double.NaN);
            KeyValuePair<object, string> r;
            try
            {
                var args = new Dictionary<string, object> { { "spieler", J.S(sp, "uuid") }, { "modus", modus }, { "email", email } };
                if (kLoaded)
                {
                    var chosen = new List<object>();
                    for (int i = 0; i < kBoxes.Count; i++) if (kBoxes[i].Checked) chosen.Add(kIds[i]);
                    if (chosen.Count == 0) { setStatus("Bitte mindestens eine Kategorie auswählen.", Theme.Yellow); return false; }
                    if (chosen.Count < kBoxes.Count) args["kategorien"] = chosen;
                }
                r = await mc.Call("ds_auskunft", args);
            }
            finally { progress.Set(-1); }
            if (r.Value != null) { setStatus("Auskunft fehlgeschlagen: " + r.Value, Theme.Red); return false; }
            var x = r.Key;
            if (modus == "download")
            {
                var zip = J.S(x, "zip");
                if (string.IsNullOrEmpty(zip)) { setStatus("Auskunft: leere Antwort.", Theme.Red); return false; }
                using (var sd = new SaveFileDialog { FileName = J.S(x, "dateiname") ?? "Datenauskunft.zip", Filter = "ZIP-Archiv|*.zip", Title = "Datenauskunft speichern" })
                {
                    if (sd.ShowDialog(Owner) != DialogResult.OK) { setStatus("Auskunft erstellt, aber nicht gespeichert.", Theme.Yellow); return true; }
                    try { File.WriteAllBytes(sd.FileName, Convert.FromBase64String(zip)); }
                    catch (Exception ex) { setStatus("Speichern fehlgeschlagen: " + ex.Message, Theme.Red); return false; }
                }
                setStatus("Auskunft für " + name + ": " + Fmt(J.N(x, "dateien")) + " Einträge, " + (J.N(x, "groesse") / 1048576).ToString("0.0", De) + " MB gespeichert.", Theme.Green);
            }
            else if (modus == "discord")
                setStatus("Download-Link an " + name + " per Discord geschickt (gültig bis " + ZS(x, "ablauf") + ").", Theme.Green);
            else
                setStatus("Auskunft per Mail an " + J.S(x, "email") + " geschickt.", Theme.Green);
            return true;
        }

        static string KatText(object k)
        {
            if (k == null) return "alle";
            var str = k as string;
            if (str != null) return str.Length == 0 ? "alle" : str;
            var names = new List<string>();
            foreach (var o in J.L(k)) { var t = o as string; if (t != null) names.Add(t); }
            return names.Count == 0 ? "alle" : string.Join(", ", names.ToArray());
        }

        async Task LoadAuskunft()
        {
            var r = await mc.Call("ds_auskunft_status");
            if (r.Value != null) { setStatus("Auskunft: " + r.Value, Theme.Red); return; }
            aInfo.Text = J.B(r.Key, "mail") ? "Mail-Absender: " + J.S(r.Key, "absender") : "E-Mail-Versand noch nicht eingerichtet";
            var rows = J.L(J.Get(r.Key, "verlauf")).Select(e =>
            {
                var modus = J.S(e, "modus");
                bool err = !string.IsNullOrEmpty(J.S(e, "fehler"));
                var weg = err ? "Fehler: " + J.S(e, "fehler") : modus == "discord" ? "Discord-Link" : modus == "senden" ? "E-Mail" : "Heruntergeladen";
                var ablauf = J.N(e, "ablauf");
                var row = Ui.Row(new RowTag { Key = J.S(e, "zeit") + J.S(e, "uuid"), Data = e, Dot = err ? Theme.Red : modus == "discord" ? Theme.Green : modus == "senden" ? Theme.SoftGreen : Theme.Muted },
                    ZS(e, "zeit"), J.S(e, "spieler"), weg, J.S(e, "email") ?? (modus == "discord" ? "Discord-DM" : "–"),
                    Fmt(J.N(e, "dateien")) + " Einträge · " + (J.N(e, "groesse") / 1048576).ToString("0.0", De) + " MB", KatText(J.Get(e, "kategorien")), J.S(e, "von"),
                    ablauf > 0 ? Z(ablauf) + (new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ablauf) < DateTime.UtcNow ? " (gelöscht)" : "") : "–");
                if (err) Tint(row, 2, Theme.Red);
                return row;
            }).ToList();
            Ui.Fill(aList, rows);
            if (rows.Count == 0) setStatus("Noch keine Auskünfte erteilt.", Theme.Muted);
        }

        // ───────────────────────────── Bericht ─────────────────────────────
        Panel BuildBericht()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var cards = Kpis(out rK, new KpiCard(Icons.Globe, "Gesehene Ziel-IPs", Theme.SoftGreen), new KpiCard(Icons.Package, "Dahinter: Dienste", Theme.Accent),
                new KpiCard(Icons.Server, "Über Cloudflare", Theme.Yellow), new KpiCard(Icons.Block, "Ungeklärt", Theme.Red));
            rText = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                                  BackColor = Theme.Input, ForeColor = Theme.Text2, Font = Theme.F(9.5f), WordWrap = true };
            var textHost = new RoundPanel { Dock = DockStyle.Top, Height = Theme.Px(210), Fill = Theme.Input, Elevated = false, Flat = true, Padding = new Padding(14, 10, 10, 10) };
            textHost.Controls.Add(rText);
            rDienste = Ui.List(new[] { "Dienst", "Art", "Hostnamen", "IPs", "davon Cloudflare", "Server" }, new[] { 190, 120, 300, 60, 120, 200 });
            rUnklar = Ui.List(new[] { "Ziel", "Server", "Hostname", "Netz", "Zuerst" }, new[] { 170, 160, 300, 100, 110 });
            var unklarCard = Card(rUnklar); unklarCard.Dock = DockStyle.Bottom; unklarCard.Height = Theme.Px(170);
            page.Controls.Add(Card(rDienste));
            var cap2 = Cap("Noch ungeklärt"); cap2.Dock = DockStyle.Bottom;
            page.Controls.Add(cap2);
            page.Controls.Add(unklarCard);
            page.Controls.Add(Cap("Dienste hinter den IPs"));
            page.Controls.Add(textHost);
            page.Controls.Add(Gap(6));
            page.Controls.Add(cards);
            return page;
        }

        async Task LoadBericht()
        {
            var r = await mc.Call("waechter_bericht");
            if (r.Value != null) { setStatus("Bericht: " + r.Value, Theme.Red); return; }
            var b = r.Key;
            var dienste = J.L(J.Get(b, "dienste")).ToList();
            var unklar = J.L(J.Get(b, "unklar")).ToList();
            double ips = J.N(b, "ips"), cf = J.N(b, "cloudflare_ips");
            int proz = ips > 0 ? (int)Math.Round(cf / ips * 100) : 0;
            rK[0].Set(Fmt(ips), Fmt(J.N(b, "eintraege")) + " Einträge im Verlauf", Theme.Text);
            rK[1].Set(Fmt(dienste.Count), Fmt(J.N(b, "hosts")) + " Hostnamen erkannt", Theme.Text);
            rK[2].Set(proz + " %", Fmt(cf) + " der IPs wechseln ständig", Theme.Text);
            rK[3].Set(Fmt(unklar.Count), unklar.Count > 0 ? "Hostname noch nicht gesehen" : "alles zugeordnet", unklar.Count > 0 ? Theme.Red : Theme.Green);
            var kat = J.D(J.Get(b, "kategorien")) ?? new Dictionary<string, object>();
            var sb = new StringBuilder();
            sb.Append("Warum tauchen ständig „neue“ IPs auf?\r\n\r\n");
            sb.Append("Kurz: Es sind fast immer dieselben Dienste – nur mit wechselnden Adressen. ").Append(proz).Append(" % der gesehenen Ziel-IPs gehören zu Cloudflare. ");
            sb.Append("Dienste wie bStats, CraftingStore, GeyserMC, Discord oder die Update-Server der Plugins liegen dahinter und liefern bei jeder DNS-Abfrage eine andere IP aus einem riesigen Pool.\r\n\r\n");
            sb.Append("Was jetzt anders ist: Der Dienst PXMX-sni liest beim Verbindungsaufbau nur den Hostnamen mit (TLS-SNI bzw. HTTP-Host – keine Inhalte, keine Spielerdaten). ");
            sb.Append("Der Wächter bewertet Ziele nach Hostname; steht der Dienst in der Datenschutzerklärung, ist eine neue IP automatisch bekannt. Gemeldet werden nur wirklich neue Hostnamen. ");
            sb.Append(J.B(b, "sni_aktiv") ? "[Mitschnitt aktiv]" : "[Mitschnitt noch ohne aktuelle Daten]").Append("\r\n\r\n");
            sb.Append("Votifier / Vote-Seiten: eingehender Verkehr (Port 8195) – der Wächter zählt nur Verbindungen, die unsere Server nach außen aufbauen.");
            if (J.N(b, "votes_heute") > 0) sb.Append(" Heute ca. ").Append(Fmt(J.N(b, "votes_heute"))).Append(" Log-Zeilen zu Votes.");
            sb.Append("\r\n\r\nHerkunft der ausgehenden Verbindungen: ")
              .Append(string.Join(" · ", kat.OrderByDescending(k => Convert.ToDouble(k.Value, CultureInfo.InvariantCulture)).Select(k => k.Key + ": " + k.Value + " IPs")));
            rText.Text = sb.ToString();
            Ui.Fill(rDienste, dienste.Select(d => Ui.Row(new RowTag { Key = J.S(d, "dienst") }, J.S(d, "dienst"), J.S(d, "kategorie"), Join(Strs(d, "hosts")),
                Fmt(J.N(d, "ips")), Fmt(J.N(d, "cloudflare")), Join(Strs(d, "server")))).ToList());
            Ui.Fill(rUnklar, unklar.Select(u => Ui.Row(new RowTag { Key = J.S(u, "ip") + ":" + J.S(u, "port") + J.S(u, "server"), Dot = Theme.Red },
                J.S(u, "ip") + ":" + J.S(u, "port"), J.S(u, "server"), J.S(u, "host") ?? J.S(u, "rdns") ?? "– (wird beim nächsten Aufruf erkannt)",
                J.B(u, "cloudflare") ? "Cloudflare" : "anderes", ZS(u, "zuerst"))).ToList());
        }

        // ───────────────────────────── Plugins / Datenbanken / Verzeichnis ─────────────────────────────
        Panel BuildPlugins()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var cards = Kpis(out pK, new KpiCard(Icons.Package, "Plugins", Theme.SoftGreen), new KpiCard(Icons.Block, "Nicht dokumentiert", Theme.Red),
                new KpiCard(Icons.Globe, "Mit Telemetrie", Theme.Mint), new KpiCard(Icons.Search, "Mit externen Adressen", Theme.Accent), new KpiCard(Icons.Server, "Mit MariaDB", Theme.Green));
            pFilter = new Segmented(new[] { "Alle", "Offen", "Extern", "Datenbank", "Dokumentiert" }, 0) { Margin = new Padding(0, 1, 10, 8) };
            pSearch = new Field(false) { Width = 240, Height = 36 };
            pSearch.Placeholder = "Plugin, Entwickler, Datenbank …";
            var bar = Ui.Bar(46, pFilter, pSearch);
            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12 };
            split.Panel1.BackColor = split.Panel2.BackColor = Theme.Bg;
            UiX.Ratio(split, 0.34, 300, 420);
            pList = Ui.List(new[] { "Plugin", "Version", "Status" }, new[] { 170, 90, 120 });
            split.Panel1.Controls.Add(Card(pList));
            var detailCard = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(16, 12, 8, 10) };
            pDetail = new Panel { Dock = DockStyle.Fill, BackColor = Color.Transparent, AutoScroll = true };
            detailCard.Controls.Add(pDetail);
            split.Panel2.Controls.Add(detailCard);
            page.Controls.Add(split);
            page.Controls.Add(bar);
            page.Controls.Add(Gap(6));
            page.Controls.Add(cards);
            pFilter.SelectedChanged += (s, e) => FillPlugins();
            pSearch.Box.TextChanged += (s, e) => FillPlugins();
            pList.SelectedIndexChanged += (s, e) => { var p = Ui.SelData<object>(pList); if (p != null && J.S(p, "id") != pSel) { pSel = J.S(p, "id"); ShowPlugin(p); } };
            ShowPluginHint();
            return page;
        }

        async Task LoadPlugins()
        {
            var r = await mc.Call("plugins");
            if (r.Value != null) { setStatus("Plugins: " + r.Value, Theme.Red); return; }
            plug = r.Key;
            var P = J.L(J.Get(plug, "plugins")).ToList();
            int offen = P.Count(p => J.S(p, "status") == "offen");
            int geaendert = P.Count(p => J.S(p, "status") == "geändert");
            pK[0].Set(Fmt(P.Count), "auf " + P.SelectMany(p => J.L(J.Get(p, "server")).Select(s => J.S(s, "server"))).Distinct().Count() + " Servern", Theme.Text);
            pK[1].Set(Fmt(offen), geaendert > 0 ? geaendert + " mit neuer Version" : "Datenkategorien fehlen", offen > 0 ? Theme.Red : Theme.Green);
            pK[2].Set(Fmt(P.Count(p => J.L(J.Get(p, "telemetrie")).Any())), "bStats, Update-Prüfung u. a.", Theme.Text);
            pK[3].Set(Fmt(P.Count(p => J.L(J.Get(p, "hosts")).Any())), "laut Config", Theme.Text);
            pK[4].Set(Fmt(P.Count(p => J.L(J.Get(p, "datenbanken")).Any())), "eigene Datenbank", Theme.Text);
            FillPlugins();
            FillDatenbanken();
            FillVerzeichnis();
            if (pSel != null)
            {
                var again = P.FirstOrDefault(p => J.S(p, "id") == pSel);
                if (again != null) ShowPlugin(again);
            }
        }

        static string PStatus(string s)
        {
            switch (s) { case "offen": return "nicht dokumentiert"; case "geändert": return "Version geändert"; case "dokumentiert": return "dokumentiert"; default: return s ?? "–"; }
        }
        static Color PColor(string s) { return s == "dokumentiert" ? Theme.Green : s == "geändert" ? Theme.Yellow : Theme.Red; }

        void FillPlugins()
        {
            if (plug == null) return;
            var q = pSearch.Box.Text.Trim().ToLowerInvariant();
            int f = Math.Max(0, pFilter.SelectedIndex);
            var l = J.L(J.Get(plug, "plugins")).Where(p =>
                (q.Length == 0 || (J.S(p, "name") + " " + string.Join(" ", Strs(p, "autoren")) + " " + string.Join(" ", Strs(p, "datenbanken")) + " " + string.Join(" ", Strs(p, "hosts"))).ToLowerInvariant().Contains(q)) &&
                (f == 0 || (f == 1 && J.S(p, "status") != "dokumentiert") || (f == 2 && J.B(p, "extern")) || (f == 3 && J.L(J.Get(p, "datenbanken")).Any()) || (f == 4 && J.S(p, "status") == "dokumentiert"))).ToList();
            var rows = l.Select(p =>
            {
                var row = Ui.Row(new RowTag { Key = J.S(p, "id"), Data = p, Dot = PColor(J.S(p, "status")) },
                    J.S(p, "name") + (J.L(J.Get(p, "telemetrie")).Any() ? "  ·  Telemetrie" : ""), Join(Strs(p, "versionen")), PStatus(J.S(p, "status")));
                Tint(row, 2, PColor(J.S(p, "status")));
                return row;
            }).ToList();
            Ui.Fill(pList, rows);
        }

        void ShowPluginHint()
        {
            pDetail.Controls.Clear();
            pDetail.Controls.Add(Theme.Lbl("← Plugin auswählen\r\nDann siehst du Version, Entwickler, Datenbanken, externe Adressen und dokumentierst die verarbeiteten Daten.", Theme.F(9.5f), Theme.Muted, Color.Transparent));
        }

        void ShowPlugin(object p)
        {
            bool edit = Can("datenschutz.edit");
            var doc = J.Get(p, "doku");
            var vor = J.Get(p, "vorschlag");
            var src = doc ?? vor;
            var kat = src != null ? Strs(src, "kategorien") : new List<string>();
            var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Color.Transparent };
            Action<Control> add = c => { c.Margin = new Padding(0, 0, 0, 6); flow.Controls.Add(c); };
            Func<string, Font, Color, Label> txt = (t, fnt, col) =>
            {
                var l = Theme.Lbl(t, fnt, col, Color.Transparent);
                l.MaximumSize = new Size(Math.Max(300, pDetail.Width - 40), 0);
                return l;
            };
            Func<string, Label> cap = t => { var l = Theme.Lbl(t.ToUpperInvariant(), Theme.F(8f, FontStyle.Bold), Theme.Muted, Color.Transparent); l.Margin = new Padding(0, 8, 0, 2); return l; };

            add(txt(J.S(p, "art") + " · " + Join(Strs(p, "autoren"), "Entwickler unbekannt"), Theme.F(9f), Theme.Muted));
            add(txt(J.S(p, "name") + "   [" + PStatus(J.S(p, "status")) + "]", Theme.F(14f, FontStyle.Bold), PColor(J.S(p, "status"))));
            if (!string.IsNullOrEmpty(J.S(p, "website"))) add(txt(J.S(p, "website"), Theme.F(9f), Theme.SoftGreen));
            if (!string.IsNullOrEmpty(J.S(p, "beschreibung"))) add(txt(J.S(p, "beschreibung"), Theme.F(9f), Theme.Text2));
            add(cap("Installiert auf"));
            add(txt(Join(J.L(J.Get(p, "server")).Select(s => J.S(s, "server") + " v" + J.S(s, "version") + " · " + Sz(J.N(s, "groesse")) + " · " + ZS(s, "geaendert")).ToList()), Theme.F(9f), Theme.Text2));
            add(cap("Speicherung"));
            var sp = Strs(p, "speicher");
            var dbs = Strs(p, "datenbanken");
            var lokal = J.L(J.Get(p, "lokal")).Select(x => J.S(x, "datei") + " (" + Sz(J.N(x, "groesse")) + ", " + J.S(x, "server") + ")").ToList();
            add(txt((sp.Count > 0 ? Join(sp) : "nichts erkannt") + (dbs.Count > 0 ? "\r\nMariaDB: " + Join(dbs) : "") + (lokal.Count > 0 ? "\r\nLokale Dateien: " + Join(lokal) : ""), Theme.F(9f), Theme.Text2));
            add(cap("Externe Verbindungen"));
            var tel = Strs(p, "telemetrie"); var hosts = Strs(p, "hosts");
            add(txt(tel.Count == 0 && hosts.Count == 0 ? "keine erkannt" : (tel.Count > 0 ? "Telemetrie: " + Join(tel) : "") + (hosts.Count > 0 ? (tel.Count > 0 ? "\r\n" : "") + "laut Config: " + Join(hosts) : ""), Theme.F(9f), Theme.Text2));
            add(cap("Datenschutz-Dokumentation"));
            add(txt(doc != null && J.N(doc, "geprueft_am") > 0 ? "geprüft von " + J.S(doc, "geprueft_von") + " · " + ZS(doc, "geprueft_am") + " · v" + (J.S(doc, "version") ?? "?")
                    : vor != null ? "Vorschlag – bitte prüfen" : "noch nicht dokumentiert", Theme.F(9f), doc != null ? Theme.Green : Theme.Yellow));

            var checks = new List<CheckBox>();
            var kg = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Color.Transparent, MaximumSize = new Size(Math.Max(300, pDetail.Width - 40), 0) };
            foreach (var k in Strs(plug, "kategorien"))
            {
                var cb = new CheckBox { Text = k, Checked = kat.Contains(k), AutoSize = true, ForeColor = Theme.Text2, BackColor = Color.Transparent, Enabled = edit, Margin = new Padding(0, 0, 14, 4) };
                checks.Add(cb); kg.Controls.Add(cb);
            }
            add(cap("Datenkategorien")); add(kg);
            Func<string, string, Field> fld = (label, val) =>
            {
                add(cap(label));
                var f = new Field(false) { Width = Math.Max(300, pDetail.Width - 60), Height = 36 };
                f.Box.Text = val ?? ""; f.Box.Enabled = edit;
                add(f);
                return f;
            };
            var fZweck = fld("Zweck", J.S(src, "zweck"));
            add(cap("Rechtsgrundlage"));
            var cR = new DarkCombo { Width = Math.Max(300, pDetail.Width - 60), Enabled = edit };
            foreach (var x in Rechtsgrundlagen) cR.Items.Add(x);
            var cur = J.S(src, "rechtsgrundlage") ?? "";
            if (!cR.Items.Contains(cur)) cR.Items.Add(cur);
            cR.SelectedItem = cur;
            add(cR);
            var fDauer = fld("Speicherdauer (z. B. bis zur Löschanfrage, 30 Tage)", J.S(src, "speicherdauer"));
            var fExtern = fld("Übermittlung an Dritte", J.S(doc, "extern") ?? string.Join(", ", tel.Concat(hosts)));
            var fNotiz = fld("Notiz", J.S(doc, "notiz"));
            if (edit)
            {
                var bSave = Ui.Btn("Geprüft & speichern", Theme.Accent);
                var bDel = Ui.Btn("Doku entfernen", Theme.DarkRed);
                bDel.Visible = doc != null;
                var row = new FlowLayoutPanel { AutoSize = true, BackColor = Color.Transparent, Margin = new Padding(0, 10, 0, 10) };
                row.Controls.Add(bSave); row.Controls.Add(bDel);
                add(row);
                bSave.Click += async (s, e) =>
                {
                    var args = new Dictionary<string, object> { { "id", J.S(p, "id") }, { "version", string.Join(", ", Strs(p, "versionen")) },
                        { "kategorien", checks.Where(c => c.Checked).Select(c => c.Text).ToList() }, { "zweck", fZweck.Box.Text.Trim() }, { "rechtsgrundlage", cR.SelectedItem as string ?? "" },
                        { "speicherdauer", fDauer.Box.Text.Trim() }, { "extern", fExtern.Box.Text.Trim() }, { "notiz", fNotiz.Box.Text.Trim() } };
                    var r = await mc.Call("plugin_doku", args);
                    if (r.Value != null) { setStatus("Nicht gespeichert: " + r.Value, Theme.Red); return; }
                    setStatus(J.S(p, "name") + " dokumentiert.", Theme.Green);
                    await LoadPlugins();
                    await RefreshBadge();
                };
                bDel.Click += async (s, e) =>
                {
                    if (!Dialogs.Confirm(Owner, "Doku entfernen", "Dokumentation von " + J.S(p, "name") + " löschen?")) return;
                    var r = await mc.Call("plugin_doku", new Dictionary<string, object> { { "id", J.S(p, "id") }, { "loeschen", true } });
                    if (r.Value != null) { setStatus("Nicht entfernt: " + r.Value, Theme.Red); return; }
                    setStatus("Doku von " + J.S(p, "name") + " entfernt.", Theme.Green);
                    await LoadPlugins();
                    await RefreshBadge();
                };
            }
            pDetail.Controls.Clear();
            pDetail.Controls.Add(flow);
        }

        Panel BuildDatenbanken()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var cards = Kpis(out dK, new KpiCard(Icons.Server, "Datenbanken", Theme.SoftGreen), new KpiCard(Icons.Package, "Tabellen", Theme.Green),
                new KpiCard(Icons.Globe, "Aktive Zugriffe", Theme.Accent), new KpiCard(Icons.Block, "Ohne Plugin", Theme.Yellow));
            dList = Ui.List(new[] { "Datenbank", "Größe", "Tabellen", "Zeilen", "Plugins", "Zugriffsrechte", "Jetzt verbunden" }, new[] { 170, 80, 80, 90, 260, 200, 200 });
            page.Controls.Add(Card(dList));
            page.Controls.Add(Gap(6));
            page.Controls.Add(cards);
            return page;
        }

        void FillDatenbanken()
        {
            var D = J.L(J.Get(plug, "datenbanken")).ToList();
            var ohne = D.Count(x => !J.L(J.Get(x, "plugins")).Any());
            dK[0].Set(Fmt(D.Count), Sz(D.Sum(x => J.N(x, "groesse"))) + " insgesamt", Theme.Text);
            dK[1].Set(Fmt(D.Sum(x => J.N(x, "tabellen"))), "≈ " + Fmt(D.Sum(x => J.N(x, "zeilen"))) + " Zeilen", Theme.Text);
            dK[2].Set(Fmt(D.Sum(x => J.L(J.Get(x, "aktiv")).Sum(y => J.N(y, "verbindungen")))), "offene DB-Verbindungen", Theme.Text);
            dK[3].Set(Fmt(ohne), ohne > 0 ? "evtl. Altlasten – prüfen/löschen" : "alles zugeordnet", ohne > 0 ? Theme.Yellow : Theme.Green);
            Ui.Fill(dList, D.Select(x =>
            {
                var aktiv = J.L(J.Get(x, "aktiv")).ToList();
                var pl = Strs(x, "plugins");
                var row = Ui.Row(new RowTag { Key = J.S(x, "name"), Data = x, Dot = aktiv.Count > 0 ? Theme.Green : Theme.Muted },
                    J.S(x, "name"), Sz(J.N(x, "groesse")), Fmt(J.N(x, "tabellen")), "≈ " + Fmt(J.N(x, "zeilen")), pl.Count > 0 ? Join(pl) : "keins erkannt",
                    Join(Strs(x, "benutzer")), aktiv.Count > 0 ? Join(aktiv.Select(a => J.S(a, "benutzer") + " (" + J.S(a, "verbindungen") + ")").ToList()) : "–");
                if (pl.Count == 0) Tint(row, 4, Theme.Yellow);
                return row;
            }).ToList());
        }

        Panel BuildVerzeichnis()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            bCsv = Ui.Btn("Als CSV speichern …", Theme.Accent);
            vInfo = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Theme.Bg);
            vInfo.Margin = new Padding(0, 10, 0, 0);
            var bar = Ui.Bar(46, bCsv, vInfo);
            vList = Ui.List(new[] { "Plugin", "Datenkategorien", "Zweck", "Rechtsgrundlage", "Speicherung", "Dauer", "Dritte", "Stand" }, new[] { 170, 200, 220, 220, 160, 110, 180, 190 });
            page.Controls.Add(Card(vList));
            page.Controls.Add(Note("Das Verzeichnis ist eine Arbeitsgrundlage aus den technischen Daten und euren Angaben – es ersetzt keine rechtliche Prüfung.", 26));
            page.Controls.Add(bar);
            bCsv.Click += (s, e) => ExportCsv();
            return page;
        }

        List<string[]> VzRows()
        {
            var res = new List<string[]>();
            if (plug == null) return res;
            foreach (var p in J.L(J.Get(plug, "plugins")))
            {
                var doc = J.Get(p, "doku"); var vor = J.Get(p, "vorschlag");
                if (doc == null && vor == null) continue;
                var x = doc ?? vor;
                var tel = Strs(p, "telemetrie"); var hosts = Strs(p, "hosts");
                res.Add(new[]
                {
                    J.S(p, "name"), Join(Strs(p, "versionen"), ""), Join(J.L(J.Get(p, "server")).Select(s => J.S(s, "server")).ToList(), ""),
                    string.Join(", ", Strs(x, "kategorien")), J.S(x, "zweck") ?? "", J.S(x, "rechtsgrundlage") ?? "",
                    string.Join(", ", Strs(p, "speicher").Concat(Strs(p, "datenbanken"))), J.S(x, "speicherdauer") ?? "",
                    J.S(x, "extern") ?? string.Join(", ", tel.Concat(hosts)),
                    doc != null ? "geprüft " + ZS(doc, "geprueft_am") + " (" + (J.S(doc, "geprueft_von") ?? "") + ")" : "Vorschlag, ungeprüft"
                });
            }
            return res;
        }

        void FillVerzeichnis()
        {
            var rows = VzRows();
            var P = J.L(J.Get(plug, "plugins")).ToList();
            vInfo.Text = P.Count(p => J.Get(p, "doku") != null) + " geprüft · " + P.Count(p => J.Get(p, "doku") == null && J.Get(p, "vorschlag") != null) + " nur Vorschlag · " +
                         P.Count(p => J.Get(p, "doku") == null && J.Get(p, "vorschlag") == null) + " ohne Angaben (nicht enthalten)";
            Ui.Fill(vList, rows.Select(r =>
            {
                var row = Ui.Row(new RowTag { Key = r[0], Dot = r[9].StartsWith("Vorschlag") ? Theme.Yellow : Theme.Green },
                    r[0] + " " + r[1], r[3].Length > 0 ? r[3] : "–", r[4].Length > 0 ? r[4] : "–", r[5].Length > 0 ? r[5] : "–", r[6].Length > 0 ? r[6] : "–",
                    r[7].Length > 0 ? r[7] : "–", r[8].Length > 0 ? r[8] : "–", r[9]);
                Tint(row, 7, r[9].StartsWith("Vorschlag") ? Theme.Yellow : Theme.Green);
                return row;
            }).ToList());
        }

        void ExportCsv()
        {
            var rows = VzRows();
            if (rows.Count == 0) { setStatus("Noch nichts dokumentiert – im Reiter „Plugins“ beginnen.", Theme.Yellow); return; }
            using (var sd = new SaveFileDialog { FileName = "RyvenMC-Verarbeitungsverzeichnis-" + DateTime.Now.ToString("yyyy-MM-dd") + ".csv", Filter = "CSV (Excel)|*.csv", Title = "Verarbeitungsverzeichnis speichern" })
            {
                if (sd.ShowDialog(Owner) != DialogResult.OK) return;
                Func<string, string> q = v => "\"" + (v ?? "").Replace("\"", "\"\"") + "\"";
                var head = new[] { "Plugin", "Version", "Server", "Datenkategorien", "Zweck", "Rechtsgrundlage", "Speicherung", "Speicherdauer", "Übermittlung an Dritte", "Stand" };
                var sb = new StringBuilder();
                sb.Append(string.Join(";", head.Select(q))).Append("\r\n");
                foreach (var r in rows) sb.Append(string.Join(";", r.Select(q))).Append("\r\n");
                try { File.WriteAllText(sd.FileName, sb.ToString(), new UTF8Encoding(true)); setStatus("Verarbeitungsverzeichnis gespeichert: " + sd.FileName, Theme.Green); }
                catch (Exception ex) { setStatus("Speichern fehlgeschlagen: " + ex.Message, Theme.Red); }
            }
        }
    }
}
