using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>KI-Support of the Discord bot: statistics, history with escalations, knowledge files and limits. Uses mod.py ki_* commands (rights ki.view / ki.edit).</summary>
    class KiView : Panel
    {
        readonly ModClient mc;
        readonly Action<string, Color> setStatus;
        Label hSub, detTitle, detInfo, wissInfo, cfgInfo;
        TabBtn[] tabs;
        Panel[] pages;
        ProgressLine progress;
        KpiCard kState, kAsked, kRate, kOpen;
        Label ovInfo;
        Segmented vFilter, vSource;
        Field vSearch, fCooldown, fDaily;
        ListView vList, wList;
        TextBox vQuestion, vAnswer, wText;
        FlatBtn bDone, bReopen, bPrev, bNext, bSave, bCfgSave;
        readonly Timer poll = new Timer { Interval = 60000 };
        bool busy, loaded, more;
        int offset;
        object cur;
        string curFile;
        /// <summary>Right ki.edit (set by the host; default true for the owner).</summary>
        public bool CanEdit = true;

        public KiView(ModClient mc, Action<string, Color> setStatus)
        {
            this.mc = mc;
            this.setStatus = setStatus;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
            poll.Tick += async (s, e) => { if (Visible && !busy && pages[0].Visible) await Reload(false); };
        }

        public void GoTab(int i) { SetTab(i); Activate(); }

        public async void Activate()
        {
            poll.Start();
            if (!loaded) await Reload(true);
        }

        public void Deactivate() { poll.Stop(); }

        /// <summary>Team rights can change while the panel is open.</summary>
        public void ApplyPerms()
        {
            bDone.Visible = bReopen.Visible = bSave.Visible = bCfgSave.Visible = CanEdit;
            wText.ReadOnly = !CanEdit || curFile == "auto.md";
            fCooldown.Box.ReadOnly = fDaily.Box.ReadOnly = !CanEdit;
        }

        void SetTab(int i)
        {
            for (int k = 0; k < pages.Length; k++) { pages[k].Visible = k == i; tabs[k].Active = k == i; }
            if (loaded && i == 1 && vList.Items.Count == 0) { var t = LoadHistory(); }
            if (loaded && i == 2 && wList.Items.Count == 0) { var t = LoadKnowledge(); }
            if (loaded && i == 3) { var t = LoadConfig(); }
        }

        void Build()
        {
            var header = Ui.Header("KI-Support", out hSub, new[] { "Übersicht", "Verlauf", "Wissen", "Einstellungen" }, out tabs, i => SetTab(i));
            var bRefresh = new FlatBtn("↻", Theme.CardHi) { Size = new Size(38, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right }.Tip("Neu laden");
            header.Controls.Add(bRefresh);
            header.Resize += (s, e) => bRefresh.Location = new Point(header.Width - bRefresh.Width - 2, 0);
            bRefresh.Click += async (s, e) => await Reload(true);

            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 10, 0, 0) };
            pages = new[] { BuildOverview(), BuildHistory(), BuildKnowledge(), BuildConfig() };
            foreach (var p in pages) content.Controls.Add(p);
            Controls.Add(content);
            Controls.Add(progress);
            Controls.Add(header);
            SetTab(0);
            ApplyPerms();
        }

        static Panel Page() { return new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg }; }

        static Label Caption(string text)
        {
            var l = Theme.Lbl(text, Theme.F(8f, FontStyle.Bold), Theme.Muted, Color.Transparent);
            l.Dock = DockStyle.Top; l.AutoSize = false; l.Height = 24; l.Padding = new Padding(0, 6, 0, 0);
            return l;
        }

        static TextBox Box(bool readOnly)
        {
            return new TextBox { Multiline = true, ReadOnly = readOnly, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                                 BackColor = Theme.Input, ForeColor = Theme.Text2, Font = Theme.Mono, WordWrap = true, AcceptsReturn = true };
        }

        static RoundPanel Host(TextBox t)
        {
            var h = new RoundPanel { Dock = DockStyle.Fill, Fill = Theme.Input, Elevated = false, Flat = true, Padding = new Padding(10, 8, 6, 8) };
            h.Controls.Add(t);
            return h;
        }

        Panel BuildOverview()
        {
            var page = Page();
            var cards = new CardGrid { Dock = DockStyle.Top, MinCardWidth = 190, CardHeight = 110, Gap = 14 };
            kState = new KpiCard(Icons.Console, "Status", Theme.Green);
            kAsked = new KpiCard(Icons.People, "Fragen heute", Theme.SoftGreen);
            kRate = new KpiCard(Icons.Block, "Eskalationsrate (7 Tage)", Theme.Yellow);
            kOpen = new KpiCard(Icons.Lock, "Offene Eskalationen", Theme.Red);
            cards.Controls.AddRange(new Control[] { kState, kAsked, kRate, kOpen });
            ovInfo = Theme.Lbl("", Theme.F(9.25f), Theme.Text2, Theme.Bg);
            ovInfo.Dock = DockStyle.Top; ovInfo.AutoSize = false; ovInfo.Height = 60; ovInfo.Padding = new Padding(2, 14, 0, 0);
            page.Controls.Add(ovInfo);
            page.Controls.Add(cards);
            return page;
        }

        Panel BuildHistory()
        {
            var page = Page();
            vFilter = new Segmented(new[] { "Alle", "Eskaliert", "Offen", "Erledigt" }, 0) { Margin = new Padding(0, 1, 10, 8) };
            vSource = new Segmented(new[] { "Alle Quellen", "Discord", "Ingame" }, 0) { Margin = new Padding(0, 1, 10, 8) };
            vSearch = new Field(false) { Width = 220, Height = 36 };
            vSearch.Placeholder = "Spieler, Frage oder Antwort …";
            bPrev = Ui.Btn("‹ Neuer", Theme.CardHi);
            bNext = Ui.Btn("Älter ›", Theme.CardHi);
            var bar = Ui.Bar(46, vFilter, vSource, vSearch, bPrev, bNext);
            vFilter.SelectedChanged += async (s, e) => { offset = 0; await LoadHistory(); };
            vSource.SelectedChanged += async (s, e) => { offset = 0; await LoadHistory(); };
            vSearch.Box.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; offset = 0; await LoadHistory(); } };
            bPrev.Click += async (s, e) => { offset = Math.Max(0, offset - 50); await LoadHistory(); };
            bNext.Click += async (s, e) => { offset += 50; await LoadHistory(); };

            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12 };
            split.Panel1.BackColor = split.Panel2.BackColor = Theme.Bg;
            UiX.Ratio(split, 0.55, 380, 320);
            vList = Ui.List(new[] { "Von", "#", "Frage", "Quelle", "Zeit", "Status" }, new[] { 110, 58, 240, 70, 90, 90 });
            var c1 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            c1.Controls.Add(vList);
            split.Panel1.Controls.Add(c1);

            var c2 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 14, 12) };
            detTitle = Theme.Lbl("Eintrag links auswählen", Theme.F(12.5f, FontStyle.Bold), Theme.Text, Color.Transparent);
            detTitle.Dock = DockStyle.Top; detTitle.AutoSize = false; detTitle.Height = 32; detTitle.AutoEllipsis = true;
            detInfo = Theme.Lbl("", Theme.F(9f), Theme.Text2, Color.Transparent);
            detInfo.Dock = DockStyle.Top; detInfo.AutoSize = false; detInfo.Height = 24;
            bDone = Ui.Btn("Als erledigt markieren", Theme.Mix(Theme.Green, Theme.Card, 0.45), 34);
            bReopen = Ui.Btn("Wieder öffnen", Theme.CardHi, 34);
            var actions = new WrapBar { Dock = DockStyle.Top, BackColor = Theme.Card, Padding = new Padding(0, 4, 0, 4) };
            foreach (var b in new[] { bDone, bReopen }) { b.Margin = new Padding(0, 0, 8, 8); actions.Controls.Add(b); }
            vQuestion = Box(true); vAnswer = Box(true);
            var qHost = Host(vQuestion); qHost.Dock = DockStyle.Top; qHost.Height = 120;
            var aHost = Host(vAnswer);
            c2.Controls.Add(aHost);
            c2.Controls.Add(Caption("ANTWORT DER KI"));
            c2.Controls.Add(qHost);
            c2.Controls.Add(Caption("FRAGE"));
            c2.Controls.Add(actions);
            c2.Controls.Add(detInfo);
            c2.Controls.Add(detTitle);
            split.Panel2.Controls.Add(c2);
            page.Controls.Add(split);
            page.Controls.Add(bar);

            SetDetailButtons(null);
            vList.SelectedIndexChanged += (s, e) => { var x = Ui.SelData<object>(vList); if (x != null) ShowEntry(x); };
            bDone.Click += async (s, e) => await SetStatus("done");
            bReopen.Click += async (s, e) => await SetStatus("open");
            return page;
        }

        Panel BuildKnowledge()
        {
            var page = Page();
            wissInfo = Theme.Lbl("Die KI lädt das Wissen stündlich neu – Änderungen sind spätestens nach 60 Minuten aktiv. Vor jedem Speichern wird eine Sicherung angelegt. auto.md wird täglich automatisch erzeugt (nur Ansicht).",
                                 Theme.F(8.75f), Theme.Muted, Theme.Bg);
            wissInfo.Dock = DockStyle.Top; wissInfo.AutoSize = false; wissInfo.Height = 36;
            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12 };
            split.Panel1.BackColor = split.Panel2.BackColor = Theme.Bg;
            UiX.Ratio(split, 0.30, 260, 320);
            wList = Ui.List(new[] { "Datei", "Größe", "Geändert" }, new[] { 130, 70, 110 });
            var c1 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            c1.Controls.Add(wList);
            split.Panel1.Controls.Add(c1);

            var c2 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 14, 12) };
            wText = Box(true);
            wText.ForeColor = Theme.Text;
            bSave = Ui.Btn("Speichern", Theme.Accent, 34);
            var actions = new WrapBar { Dock = DockStyle.Bottom, BackColor = Theme.Card, Padding = new Padding(0, 8, 0, 0), Height = 50 };
            bSave.Margin = new Padding(0, 0, 8, 8);
            actions.Controls.Add(bSave);
            c2.Controls.Add(Host(wText));
            c2.Controls.Add(actions);
            split.Panel2.Controls.Add(c2);
            page.Controls.Add(split);
            page.Controls.Add(wissInfo);

            wList.SelectedIndexChanged += async (s, e) => { var f = Ui.SelData<object>(wList); if (f != null) await OpenFileText(J.S(f, "name")); };
            bSave.Click += async (s, e) => await SaveFile();
            return page;
        }

        Panel BuildConfig()
        {
            var page = Page();
            var card = new RoundPanel { Dock = DockStyle.Top, Height = 270, Padding = new Padding(20, 16, 20, 14) };
            fCooldown = new Field(false) { Width = 140, Height = 36 };
            fDaily = new Field(false) { Width = 140, Height = 36 };
            bCfgSave = Ui.Btn("Speichern", Theme.Accent, 34);
            cfgInfo = Theme.Lbl("Der Bot liest diese Werte nur beim Start – sie gelten erst nach einem Neustart des Containers „ryvenlink-bot“.", Theme.F(8.75f), Theme.Muted, Color.Transparent);
            var l1 = Theme.Lbl("Wartezeit zwischen zwei Fragen (Sekunden)", Theme.F(9.25f), Theme.Text2, Color.Transparent);
            var l2 = Theme.Lbl("Fragen pro Spieler und Tag", Theme.F(9.25f), Theme.Text2, Color.Transparent);
            int y = 0;
            foreach (var c in new Control[] { l1, fCooldown, l2, fDaily, cfgInfo, bCfgSave })
            {
                c.Location = new Point(0, y);
                c.AutoSize = c is Label ? true : false;
                y += c is Label ? 28 : 46;
                card.Controls.Add(c);
            }
            cfgInfo.AutoSize = false; cfgInfo.Size = new Size(760, 22);
            bCfgSave.Width = 140;
            page.Controls.Add(card);
            bCfgSave.Click += async (s, e) => await SaveConfig();
            return page;
        }

        // ───────────────────────────── loading ─────────────────────────────
        async Task Reload(bool showProgress)
        {
            if (busy) return;
            busy = true;
            if (showProgress) progress.Set(double.NaN);
            try
            {
                var o = await mc.Call("ki_overview");
                if (o.Value != null) { hSub.Text = "Fehler: " + o.Value; hSub.ForeColor = Theme.Red; return; }
                var ov = o.Key;
                bool active = J.B(ov, "aktiv");
                kState.Set(active ? "aktiv" : "aus", J.B(ov, "laeuft") ? (J.B(ov, "api_key") ? "Bot läuft, API-Key gesetzt" : "Bot läuft, kein API-Key") : "Bot-Container läuft nicht", active ? Theme.Green : Theme.Red);
                kAsked.Set(((int)J.N(ov, "heute")).ToString(), (int)J.N(ov, "tage7") + " in 7 Tagen · " + (int)J.N(ov, "gesamt") + " gesamt", Theme.Text);
                kRate.Set(J.N(ov, "eskalationsrate").ToString("0.0") + " %", (int)J.N(ov, "eskaliert7") + " von " + (int)J.N(ov, "tage7"), Theme.Text);
                int open = (int)J.N(ov, "offen");
                kOpen.Set(open.ToString(), (int)J.N(ov, "fehler7") + " Fehler in 7 Tagen · " + (int)J.N(ov, "wartend") + " wartend", open > 0 ? Theme.Red : Theme.Green);
                ovInfo.Text = "Letzte 7 Tage: " + (int)J.N(ov, "discord7") + " Fragen über Discord · " + (int)J.N(ov, "ingame7") + " ingame." + (open > 0 ? "  Offene Eskalationen stehen im Tab „Verlauf“ (Filter „Offen“)." : "");
                tabs[1].Text = open > 0 ? "Verlauf (" + open + ")" : "Verlauf";
                tabs[1].Width = Theme.Measure(tabs[1].Text, tabs[1].Font).Width + 30;
                hSub.ForeColor = Theme.Muted;
                hSub.Text = "Support-Assistent des Discord-Bots · Fragen, Eskalationen und Wissen · Stand " + DateTime.Now.ToString("HH:mm:ss");
                loaded = true;
                if (pages[1].Visible || vList.Items.Count == 0) await LoadHistory();
                if (pages[2].Visible && wList.Items.Count == 0) await LoadKnowledge();
                if (pages[3].Visible) await LoadConfig();
            }
            finally { busy = false; progress.Set(-1); }
        }

        async Task LoadHistory()
        {
            var filter = new[] { "alle", "eskaliert", "offen", "erledigt" }[Math.Max(0, vFilter.SelectedIndex)];
            var src = new[] { "", "discord", "ingame" }[Math.Max(0, vSource.SelectedIndex)];
            var r = await mc.Call("ki_verlauf", new Dictionary<string, object> { { "filter", filter }, { "source", src }, { "q", vSearch.Box.Text.Trim() }, { "offset", offset }, { "limit", 50 } });
            if (r.Value != null) { setStatus("KI-Verlauf: " + r.Value, Theme.Red); return; }
            more = J.B(r.Key, "mehr");
            bPrev.Enabled = offset > 0;
            bNext.Enabled = more;
            var rows = J.L(J.Get(r.Key, "items")).Select(x =>
            {
                int esc = (int)J.N(x, "escalated");
                var row = Ui.Row(new RowTag { Key = J.S(x, "id"), Data = x, Dot = esc == 1 ? Theme.Red : esc == 2 ? Theme.Green : Color.Empty },
                    J.S(x, "user"), "#" + J.S(x, "id"), Flat(J.S(x, "question"), 120), J.S(x, "source"), ModClient.When(J.S(x, "created")),
                    esc == 1 ? "offen" : esc == 2 ? "erledigt" : J.S(x, "status"));
                if (esc == 1) row.SubItems[5].ForeColor = Theme.Red;
                return row;
            }).ToList();
            Ui.Fill(vList, rows);
            if (cur != null && !rows.Any(x => J.S(((RowTag)x.Tag).Data, "id") == J.S(cur, "id"))) { cur = null; ClearEntry(); }
        }

        static string Flat(string s, int max)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ");
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        void ClearEntry()
        {
            detTitle.Text = "Eintrag links auswählen"; detInfo.Text = ""; vQuestion.Text = ""; vAnswer.Text = "";
            SetDetailButtons(null);
        }

        void SetDetailButtons(object x)
        {
            int esc = x == null ? 0 : (int)J.N(x, "escalated");
            bDone.Visible = CanEdit && esc == 1;
            bReopen.Visible = CanEdit && esc == 2;
        }

        void ShowEntry(object x)
        {
            cur = x;
            int esc = (int)J.N(x, "escalated");
            detTitle.Text = "#" + J.S(x, "id") + " · " + J.S(x, "user");
            detInfo.Text = J.S(x, "source") + " · " + ModClient.When(J.S(x, "created")) + " · Status " + J.S(x, "status") + (esc == 1 ? " · Eskalation offen" : esc == 2 ? " · Eskalation erledigt" : "");
            detInfo.ForeColor = esc == 1 ? Theme.Red : Theme.Text2;
            vQuestion.Text = (J.S(x, "question") ?? "").Replace("\n", "\r\n");
            vAnswer.Text = (J.S(x, "answer") ?? "(keine Antwort)").Replace("\n", "\r\n");
            SetDetailButtons(x);
        }

        async Task SetStatus(string st)
        {
            if (cur == null || !CanEdit) return;
            var r = await mc.Call("ki_set_status", new Dictionary<string, object> { { "id", (long)J.N(cur, "id") }, { "status", st } });
            if (r.Value != null) { setStatus("Nicht gespeichert: " + r.Value, Theme.Red); return; }
            setStatus("#" + J.S(cur, "id") + (st == "done" ? ": Eskalation erledigt." : ": wieder offen."), Theme.Green);
            cur = null; ClearEntry();
            await Reload(false);
            await LoadHistory();
        }

        async Task LoadKnowledge()
        {
            var r = await mc.Call("ki_wissen");
            if (r.Value != null) { setStatus("KI-Wissen: " + r.Value, Theme.Red); return; }
            Ui.Fill(wList, J.L(J.Get(r.Key, "dateien")).Select(f => Ui.Row(new RowTag { Key = J.S(f, "name"), Data = f },
                J.S(f, "name") + (J.B(f, "auto") ? " (auto)" : ""), (J.N(f, "groesse") / 1024).ToString("0.0") + " KB",
                new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(J.N(f, "geaendert")).ToLocalTime().ToString("dd.MM. HH:mm"))).ToList());
        }

        async Task OpenFileText(string name)
        {
            var r = await mc.Call("ki_wissen_lesen", new Dictionary<string, object> { { "name", name } });
            if (r.Value != null) { setStatus("KI-Wissen: " + r.Value, Theme.Red); return; }
            curFile = J.S(r.Key, "name");
            wText.Text = (J.S(r.Key, "text") ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
            wText.ReadOnly = !CanEdit || J.B(r.Key, "auto");
            bSave.Visible = CanEdit && !J.B(r.Key, "auto");
        }

        async Task SaveFile()
        {
            if (curFile == null || !CanEdit) return;
            var r = await mc.Call("ki_wissen_speichern", new Dictionary<string, object> { { "name", curFile }, { "text", wText.Text.Replace("\r\n", "\n") } });
            if (r.Value != null) { setStatus("Nicht gespeichert: " + r.Value, Theme.Red); return; }
            setStatus(curFile + " gespeichert. " + J.S(r.Key, "hinweis"), Theme.Green);
            await LoadKnowledge();
        }

        async Task LoadConfig()
        {
            var r = await mc.Call("ki_config");
            if (r.Value != null) { setStatus("KI-Einstellungen: " + r.Value, Theme.Red); return; }
            fCooldown.Box.Text = ((int)J.N(r.Key, "cooldown_seconds")).ToString();
            fDaily.Box.Text = ((int)J.N(r.Key, "daily_limit")).ToString();
        }

        async Task SaveConfig()
        {
            int cd, dl;
            if (!int.TryParse(fCooldown.Box.Text.Trim(), out cd) || !int.TryParse(fDaily.Box.Text.Trim(), out dl)) { setStatus("Bitte ganze Zahlen eingeben.", Theme.Yellow); return; }
            var r = await mc.Call("ki_config_set", new Dictionary<string, object> { { "cooldown_seconds", cd }, { "daily_limit", dl } });
            if (r.Value != null) { setStatus("Nicht gespeichert: " + r.Value, Theme.Red); return; }
            setStatus("Gespeichert – gilt nach dem nächsten Neustart des Containers ryvenlink-bot.", Theme.Green);
        }
    }
}
