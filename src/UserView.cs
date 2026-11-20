using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    class McPlayer
    {
        public string Uuid, Name, Ip, Primary = "default";
        public double? Money, MobCoins;
        public long? Coins;
        public DateTime? Login, Logout;
        public bool Online;
        public readonly List<Membership> Groups = new List<Membership>();
        public bool Bedrock { get { return Uuid != null && Uuid.StartsWith("00000000-0000-0000-"); } }
        public DateTime LastSeen { get { return Online ? DateTime.Now : (Logout ?? Login ?? DateTime.MinValue); } }
    }

    class Membership { public string Group, Server, World; public long Expiry; }

    class McGroup
    {
        public string Name, Prefix = "", Display = "";
        public int Weight;
        public int Members;
        public Color Color = Theme.Text2;
    }

    class Backend { public string Id, Name, Dir; public override string ToString() { return Name; } }

    /// <summary>Player administration: economy (Essentials/PlayerPoints/MobCoins), ranks + permissions (LuckPerms), moderation.</summary>
    class UserView : Panel
    {
        readonly Api api;
        readonly Func<List<ServerItem>> getServers;
        readonly Func<Ssh> getSsh;
        readonly Func<bool> sshReady;
        readonly Action<string, Color> setStatus;
        readonly Func<FirewallView> getFirewall;

        Label hSub, countLbl, outLbl;
        TabBtn[] tabs;
        Panel[] pages;
        ProgressLine progress;
        ComboBox serverBox, filterBox;
        Field search;
        ListView pList, gList, mList, histList, rankList, rankPerms, logList;
        Label dName, dInfo, dAlts;
        Pill dRank, dOnline;
        StatCard eMoney, eCoins, eMob;
        Panel detail;
        Panel detailEmpty;
        List<Backend> backends = new List<Backend>();
        List<McPlayer> players = new List<McPlayer>();
        Dictionary<string, McGroup> groups = new Dictionary<string, McGroup>();
        McPlayer sel;
        bool busy, loadedOnce;
        string lastError;
        string currency = "$";

        static readonly Regex UuidRx = new Regex("^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$");
        static readonly Regex NameRx = new Regex(@"^[A-Za-z0-9_.*]{1,25}$");
        static readonly Regex GroupRx = new Regex(@"^[a-z0-9_\-]{1,36}$");
        static readonly Regex PermRx = new Regex(@"^[A-Za-z0-9_.\-*:]{1,200}$");
        static readonly Regex DurRx = new Regex(@"^(\d+(mo|[smhdwy]))+$");
        static readonly Regex CtxRx = new Regex(@"^[A-Za-z0-9_\-]{1,40}$");

        public UserView(Api api, Func<List<ServerItem>> getServers, Func<Ssh> getSsh, Func<bool> sshReady, Action<string, Color> setStatus, Func<FirewallView> getFirewall)
        {
            this.api = api;
            this.getServers = getServers;
            this.getSsh = getSsh;
            this.sshReady = sshReady;
            this.setStatus = setStatus;
            this.getFirewall = getFirewall;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
        }

        public async void Activate()
        {
            if (!sshReady())
            {
                hSub.Text = "Die Nutzerverwaltung liest Datenbank & Plugin-Dateien über SSH – richte sie einmal unter „Docker“ ein.";
                hSub.ForeColor = Theme.Yellow;
                return;
            }
            if (!loadedOnce) await Reload();
            else MarkOnline();
        }

        public IEnumerable<McPlayer> Players { get { return players; } }
        public async void GoTab(int i) { await ShowTab(i); }

        public void SearchFor(string q) { SetTab(0); search.Box.Text = q; }

        public async void OpenPlayer(string uuid)
        {
            SetTab(0);
            var p = players.FirstOrDefault(x => x.Uuid == uuid);
            if (p == null) return;
            search.Box.Text = p.Name;
            foreach (ListViewItem it in pList.Items) if (((RowTag)it.Tag).Key == uuid) { it.Selected = true; it.EnsureVisible(); }
            if (sel != p) await ShowPlayer(p);
        }
        public void Deactivate() { }

        // ───────────────────────────── Layout ─────────────────────────────
        void Build()
        {
            var header = Ui.Header("Nutzerverwaltung", out hSub, new[] { "Spieler", "Ränge", "Protokoll" }, out tabs, async i => await ShowTab(i));
            serverBox = Ui.Combo(220);
            serverBox.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            var sl = Theme.Lbl("Server:", Theme.F(9f, FontStyle.Bold), Theme.Muted, Theme.Bg);
            serverBox.Tip("Von diesem Server werden Money & MobCoins gelesen und Befehle ausgeführt");
            sl.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            header.Controls.Add(serverBox);
            header.Controls.Add(sl);
            header.Resize += (s, e) => { serverBox.Location = new Point(header.Width - serverBox.Width - 2, 0); sl.Location = new Point(serverBox.Left - sl.Width - 8, 8); };
            serverBox.SelectedIndexChanged += async (s, e) => { if (!busy && loadedOnce) await Reload(); };

            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0) };
            pages = new[] { BuildPlayers(), BuildRanks(), BuildLog() };
            foreach (var p in pages) content.Controls.Add(p);
            Controls.Add(content);
            Controls.Add(progress);
            Controls.Add(header);
            SetTab(0);
        }

        void SetTab(int i) { for (int k = 0; k < pages.Length; k++) { pages[k].Visible = k == i; tabs[k].Active = k == i; } }

        async Task ShowTab(int i)
        {
            SetTab(i);
            if (!loadedOnce) await Reload();
            if (i == 2) await LoadLog();
        }

        Panel BuildPlayers()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12 };
            split.Panel1.BackColor = split.Panel2.BackColor = Theme.Bg;
            UiX.Ratio(split, 0.44, 340, 420);

            // left: list
            search = new Field(false) { Width = 210, Height = 36 };
            search.Placeholder = "Name, UUID oder IP …";
            filterBox = Ui.Combo(110);
            filterBox.Items.AddRange(new object[] { "Alle", "Online", "Team", "Bedrock", "Reichste", "Neueste" });
            filterBox.SelectedIndex = 0;
            filterBox.Tip("Filter: Online, Team (Ränge mit Gewicht), Bedrock-Spieler, Reichste, Neueste");
            var bRefresh = Ui.Btn("↻", Theme.CardHi).Tip("Alle Daten neu laden"); bRefresh.Width = 40;
            countLbl = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Theme.Bg);
            countLbl.Margin = new Padding(0, 10, 0, 0);
            var bar = Ui.Bar(46, search, filterBox, bRefresh, countLbl);
            pList = Ui.List(new[] { "Spieler", "Rang", "Money", "Coins", "MobCoins", "Zuletzt" }, new[] { 140, 90, 100, 60, 70, 90 });
            foreach (int c in new[] { 2, 3, 4 }) pList.Columns[c].TextAlign = HorizontalAlignment.Right;
            var lc = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            lc.Controls.Add(pList);
            split.Panel1.Controls.Add(lc);
            split.Panel1.Controls.Add(bar);
            search.Box.TextChanged += (s, e) => RenderList();
            filterBox.SelectedIndexChanged += (s, e) => RenderList();
            bRefresh.Click += async (s, e) => await Reload();
            pList.SelectedIndexChanged += async (s, e) =>
            {
                var pl = Ui.SelData<McPlayer>(pList);
                if (pl != null && pl != sel) await ShowPlayer(pl);
            };

            // right: detail card with its own tabs
            var right = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 10, 12, 10) };
            detail = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Visible = false };
            detailEmpty = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
            var el = Theme.Lbl("← Spieler links auswählen\n\nHier verwaltest du dann Money, Coins, Ränge,\nPermissions und Bans des Spielers.", Theme.F(10f), Theme.Muted, Theme.Card);
            el.Location = new Point(10, 14);
            detailEmpty.Controls.Add(el);
            right.Controls.Add(detail);
            right.Controls.Add(detailEmpty);
            split.Panel2.Controls.Add(right);
            BuildDetail();

            p.Controls.Add(split);
            return p;
        }

        TabBtn[] dTabs;
        Panel[] dPages;
        Action layoutTop;

        void SetDetailTab(int i) { for (int k = 0; k < dPages.Length; k++) { dPages[k].Visible = k == i; dTabs[k].Active = k == i; } }

        static FlowLayoutPanel ButtonRow(params Control[] buttons)
        {
            var bf = new WrapBar { Dock = DockStyle.Bottom, BackColor = Theme.Card, Padding = new Padding(0, 8, 0, 0) };
            foreach (var b in buttons) { b.Height = 32; b.Margin = new Padding(0, 0, 6, 6); b.Font = Theme.F(8.75f, FontStyle.Bold); bf.Controls.Add(b); }
            return bf;
        }

        Panel ListPage(ListView lv, params Control[] buttons)
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
            lv.Dock = DockStyle.Fill;
            page.Controls.Add(lv);
            if (buttons.Length > 0) page.Controls.Add(ButtonRow(buttons));
            return page;
        }

        void BuildDetail()
        {
            // ── header: name, rank, online, facts, quick actions ──
            var top = new Panel { Dock = DockStyle.Top, Height = 150, BackColor = Theme.Card };
            dName = Theme.Lbl("", Theme.F(15f, FontStyle.Bold), Theme.Text, Theme.Card);
            dName.Location = new Point(0, 0);
            dRank = new Pill { Location = new Point(200, 6), BackColor = Theme.Card };
            dOnline = new Pill { Location = new Point(320, 6), BackColor = Theme.Card };
            dName.SizeChanged += (s, e) => { dRank.Left = dName.Right + 8; dOnline.Left = dRank.Right + 6; };
            dInfo = Theme.Lbl("", Theme.F(9f), Theme.Text2, Theme.Card);
            dInfo.Location = new Point(2, 38);
            dInfo.AutoSize = false;
            dInfo.Size = new Size(600, 54);
            dAlts = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Theme.Card);
            dAlts.Location = new Point(2, 92);
            dAlts.AutoSize = false; dAlts.AutoEllipsis = true; dAlts.Size = new Size(600, 18);
            top.Resize += (s, e) => { dAlts.Width = top.Width - 4; dInfo.Width = top.Width - 4; };
            var bUuid = new FlatBtn("UUID kopieren", Theme.CardHi).Tip("UUID in die Zwischenablage");
            var bIp = new FlatBtn("IP kopieren", Theme.CardHi);
            var bIntel = new FlatBtn("IP prüfen", Theme.CardHi).Tip("Ort, Provider und VPN-Verdacht nachschlagen");
            var bNameMc = new FlatBtn("NameMC", Theme.CardHi).Tip("Profil auf namemc.com öffnen (nur Java)");
            var quick = new FlowLayoutPanel { Location = new Point(0, 114), Size = new Size(600, 36), BackColor = Theme.Card, WrapContents = true };
            foreach (var b in new[] { bUuid, bIp, bIntel, bNameMc }) { b.Height = 30; b.Font = Theme.F(8.5f, FontStyle.Bold); b.Margin = new Padding(0, 0, 6, 6); quick.Controls.Add(b); }
            top.Controls.AddRange(new Control[] { dName, dRank, dOnline, dInfo, dAlts, quick });
            // the header grows with its content: long IP/provider lines wrap, buttons wrap on narrow windows
            layoutTop = () =>
            {
                int w = Math.Max(Theme.Px(200), top.Width - Theme.Px(4));
                int y = dName.Bottom + Theme.Px(6);
                var ih = TextRenderer.MeasureText(dInfo.Text.Length > 0 ? dInfo.Text : "X", dInfo.Font, new Size(w, 0), TextFormatFlags.WordBreak).Height;
                dInfo.SetBounds(Theme.Px(2), y, w, ih + Theme.Px(2));
                y = dInfo.Bottom + Theme.Px(2);
                dAlts.SetBounds(Theme.Px(2), y, w, Theme.Px(18));
                y = dAlts.Bottom + Theme.Px(6);
                quick.SetBounds(0, y, top.Width, Math.Max(Theme.Px(36), quick.GetPreferredSize(new Size(top.Width, 0)).Height));
                int h = quick.Bottom + Theme.Px(4);
                if (top.Height != h) top.Height = h;
            };
            top.Resize += (s, e) => layoutTop();
            bUuid.Click += (s, e) => { if (sel != null) { Clipboard.SetText(sel.Uuid); setStatus("UUID kopiert: " + sel.Uuid, Theme.Green); } };
            bIp.Click += (s, e) => { if (sel != null && !string.IsNullOrEmpty(sel.Ip)) { Clipboard.SetText(sel.Ip); setStatus("IP kopiert.", Theme.Green); } };
            bNameMc.Click += (s, e) => { if (sel != null && !sel.Bedrock) System.Diagnostics.Process.Start("https://namemc.com/profile/" + sel.Uuid); };
            bIntel.Click += async (s, e) =>
            {
                if (sel == null || string.IsNullOrEmpty(sel.Ip) || IpIntel.IsPrivate(sel.Ip)) return;
                if (!Dialogs.Confirm(FindForm(), "IP prüfen", "IP von " + sel.Name + " bei einem IP-Dienst nachschlagen?\n(Die IP wird an diesen Dienst übermittelt.)")) return;
                try { await Task.Run(() => IpIntel.Lookup(sel.Ip, null)); IpIntel.Save(); } catch (Exception ex) { setStatus("IP-Prüfung: " + ex.Message, Theme.Red); }
                RenderDetail();
            };

            // ── tab strip ──
            var names = new[] { "Wirtschaft", "Ränge", "Permissions", "Moderation", "Verlauf" };
            var strip = new Panel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Card };
            dTabs = new TabBtn[names.Length];
            int x = 0;
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                dTabs[i] = new TabBtn(names[i]) { Location = new Point(x, 2), BackColor = Theme.Card, Font = Theme.F(9.5f, FontStyle.Bold) };
                dTabs[i].Size = new Size(Theme.Measure(names[i], dTabs[i].Font).Width + 22, 36);
                x = dTabs[i].Right + 2;
                dTabs[i].Click += (s, e) => SetDetailTab(idx);
                strip.Controls.Add(dTabs[i]);
            }
            // tabs wrap into a second row instead of running out of the card
            strip.Resize += (s, e) =>
            {
                int tx = 0, ty = Theme.Px(2), rowH = dTabs[0].Height + Theme.Px(2);
                foreach (var t in dTabs)
                {
                    if (tx > 0 && tx + t.Width > strip.Width) { tx = 0; ty += rowH; }
                    t.Location = new Point(tx, ty);
                    tx = t.Right + Theme.Px(2);
                }
                int h = ty + rowH + Theme.Px(2);
                if (strip.Height != h) strip.Height = h;
            };
            strip.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border });

            // ── page: economy ──
            var eco = new CardGrid { Dock = DockStyle.Top, MinCardWidth = 180, CardHeight = 146, BackColor = Theme.Card };
            eMoney = new StatCard("Money") { BarColor = Theme.Green };
            eCoins = new StatCard("Coins") { BarColor = Theme.Yellow };
            eMob = new StatCard("MobCoins") { BarColor = Theme.Mint };
            foreach (var xx in new[] { new { C = eMoney, K = "money", T = "Essentials-Geld – gilt nur auf dem oben gewählten Server" },
                                        new { C = eCoins, K = "coins", T = "PlayerPoints – global auf allen Servern" },
                                        new { C = eMob, K = "mob", T = "DeluxeMobCoins – gilt nur auf dem oben gewählten Server" } })
            {
                var col = new Panel { BackColor = Theme.Card };
                xx.C.Dock = DockStyle.Top; xx.C.Height = 100; xx.C.Margin = Padding.Empty;
                xx.C.Tip(xx.T);
                var row = new TableLayoutPanel { Dock = DockStyle.Bottom, Height = 38, ColumnCount = 3, BackColor = Theme.Card };
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
                var k = xx.K;
                foreach (var op in new[] { "Setzen", "+", "−" })
                {
                    var b = new FlatBtn(op, op == "Setzen" ? Theme.Accent : Theme.CardHi) { Dock = DockStyle.Fill, Margin = new Padding(0, 4, 4, 0), Font = Theme.F(9f, FontStyle.Bold) };
                    b.Tip(op == "Setzen" ? "Betrag genau setzen" : op == "+" ? "Betrag hinzufügen" : "Betrag abziehen");
                    var o = op;
                    b.Click += async (s, e) => await Economy(k, o == "Setzen" ? "set" : o == "+" ? "give" : "take");
                    row.Controls.Add(b);
                }
                col.Controls.Add(xx.C);
                col.Controls.Add(row);
                eco.Controls.Add(col);
            }
            var ecoHint = Theme.Lbl("Läuft der gewählte Server, werden Befehle (eco / points / mobcoins) gesendet – sonst wird direkt in Datei bzw. Datenbank geschrieben.", Theme.F(8.5f), Theme.Muted, Theme.Card);
            ecoHint.Dock = DockStyle.Top; ecoHint.AutoSize = false; ecoHint.Height = 40; ecoHint.Padding = new Padding(0, 10, 0, 0);
            var pEco = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(0, 10, 0, 0), AutoScroll = true };
            pEco.Controls.Add(ecoHint);
            pEco.Controls.Add(eco);

            // ── page: ranks ──
            gList = Ui.List(new[] { "Gruppe", "Ablauf", "Kontext" }, new[] { 170, 150, 120 });
            var bSetRank = new FlatBtn("Rang setzen", Theme.Accent).Tip("Ersetzt den dauerhaften Rang (parent set)");
            var bAddGroup = new FlatBtn("+ Gruppe", Theme.CardHi).Tip("Zusätzliche Gruppe dauerhaft geben");
            var bTempGroup = new FlatBtn("+ Temporär", Theme.CardHi).Tip("Gruppe für eine bestimmte Zeit geben, z. B. 30d");
            var bRemGroup = new FlatBtn("Entfernen", Theme.Mix(Theme.Red, Theme.Card, 0.35)).Tip("Markierte Gruppe entfernen");
            var pRanks = ListPage(gList, bSetRank, bAddGroup, bTempGroup, bRemGroup);
            bSetRank.Click += async (s, e) => await GroupAction("set");
            bAddGroup.Click += async (s, e) => await GroupAction("add");
            bTempGroup.Click += async (s, e) => await GroupAction("addtemp");
            bRemGroup.Click += async (s, e) => await GroupAction("remove");

            // ── page: permissions ──
            mList = Ui.List(new[] { "Permission", "Wert", "Kontext", "Ablauf" }, new[] { 230, 56, 110, 130 });
            var bAddPerm = new FlatBtn("+ Permission", Theme.Accent);
            var bTempPerm = new FlatBtn("+ Temporär", Theme.CardHi);
            var bToggle = new FlatBtn("true ⇄ false", Theme.CardHi).Tip("Markierte Permission umschalten");
            var bRemPerm = new FlatBtn("Entfernen", Theme.Mix(Theme.Red, Theme.Card, 0.35));
            var bCheck = new FlatBtn("Prüfen …", Theme.CardHi).Tip("Prüft, ob der Spieler eine Permission hat – inklusive Gruppen");
            var pPerms = ListPage(mList, bAddPerm, bTempPerm, bToggle, bRemPerm, bCheck);
            bAddPerm.Click += async (s, e) => await PermAction("set", null);
            bTempPerm.Click += async (s, e) => await PermAction("settemp", null);
            bToggle.Click += async (s, e) => { var pr = Ui.SelData<object[]>(mList); if (pr != null) await PermAction("toggle", pr); };
            bRemPerm.Click += async (s, e) => { var pr = Ui.SelData<object[]>(mList); if (pr != null) await PermAction("unset", pr); };
            bCheck.Click += async (s, e) =>
            {
                if (sel == null) return;
                var perm = Dialogs.Ask(FindForm(), "Permission prüfen", "Welche Permission soll für " + sel.Name + " geprüft werden (inkl. Gruppen-Vererbung)?", "essentials.fly");
                if (perm == null || !PermRx.IsMatch(perm.Trim())) return;
                await Command(new[] { "lp user " + sel.Uuid + " permission check " + perm.Trim() }, false, null);
            };

            // ── page: moderation ──
            var pMod = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(0, 10, 0, 0), AutoScroll = true };
            Func<string, Color, string, FlatBtn> mb = (t, c, tip) => new FlatBtn(t, c) { Height = 34, Margin = new Padding(0, 0, 8, 8) }.Tip(tip);
            var g1 = new WrapBar { Dock = DockStyle.Top, BackColor = Theme.Card };
            var g2 = new WrapBar { Dock = DockStyle.Top, BackColor = Theme.Card };
            var g3 = new WrapBar { Dock = DockStyle.Top, BackColor = Theme.Card };
            var bWarn = mb("Verwarnen", Theme.CardHi, "Verwarnung mit Grund – wird protokolliert");
            var bKick = mb("Kicken", Theme.CardHi, "Vom Netzwerk werfen (mit Grund)");
            var bMute = mb("Muten", Theme.CardHi, "Chat sperren – Dauer frei oder per Vorlage");
            var bUnmute = mb("Entmuten", Theme.CardHi, "Chat-Sperre aufheben");
            var bBan = mb("Bannen", Theme.Mix(Theme.Red, Theme.Card, 0.45), "Bann – Dauer frei, per Vorlage oder permanent; auch IP-Bann");
            var bUnban = mb("Entbannen", Theme.CardHi, "Bann aufheben");
            var bFile = mb("Akte", Theme.CardHi, "Alle Strafen, Reports und Discord-Verknüpfung des Spielers");
            var bFw = mb("IP in Firewall sperren", Theme.DarkRed, "Blockiert die IP komplett auf Server-Ebene");
            g1.Controls.AddRange(new Control[] { bWarn, bKick, bMute, bUnmute, bFile });
            g2.Controls.AddRange(new Control[] { bBan, bUnban });
            g3.Controls.AddRange(new Control[] { bFw });
            Func<string, Label> cap = t => { var l = Theme.Lbl(t, Theme.F(8f, FontStyle.Bold), Theme.Muted, Theme.Card); l.Dock = DockStyle.Top; l.AutoSize = false; l.Height = 22; return l; };
            var modHint = Theme.Lbl("Strafen gelten netzwerkweit (RyvenBans) und erscheinen im Discord-Strafen-Log und unter Moderation.", Theme.F(8.75f), Theme.Muted, Theme.Card);
            modHint.Dock = DockStyle.Top; modHint.AutoSize = false; modHint.Height = 40;
            pMod.Controls.Add(g3); pMod.Controls.Add(cap("FIREWALL (Server-Ebene)"));
            pMod.Controls.Add(g2); pMod.Controls.Add(cap("BANNS"));
            pMod.Controls.Add(g1); pMod.Controls.Add(cap("VERWARNEN, KICKEN, CHAT"));
            pMod.Controls.Add(modHint);
            bWarn.Click += async (s, e) => await Moderate("warn");
            bKick.Click += async (s, e) => await Moderate("kick");
            bMute.Click += async (s, e) => await Moderate("mute");
            bBan.Click += async (s, e) => await Moderate("ban");
            bUnban.Click += async (s, e) => { if (sel != null && Mod != null && await PunishDialog.Revoke(FindForm(), Mod, sel.Name, "ban", setStatus)) await LoadDetail(); };
            bUnmute.Click += async (s, e) => { if (sel != null && Mod != null && await PunishDialog.Revoke(FindForm(), Mod, sel.Name, "mute", setStatus)) await LoadDetail(); };
            bFile.Click += (s, e) => { if (sel != null && Mod != null) using (var f = new PlayerFileForm(Mod, sel.Name, setStatus)) f.ShowDialog(FindForm()); };
            bFw.Click += async (s, e) =>
            {
                if (sel == null || string.IsNullOrEmpty(sel.Ip)) { setStatus("Keine IP bekannt.", Theme.Yellow); return; }
                if (!Dialogs.Confirm(FindForm(), "Firewall", "IP " + sel.Ip + " von " + sel.Name + " in der Server-Firewall komplett sperren?\n\nDas blockiert ALLES von dieser IP (auch andere Spieler im selben Haushalt).")) return;
                await getFirewall().BlockIp(sel.Ip, "Spieler " + sel.Name);
            };

            // ── page: history ──
            histList = Ui.List(new[] { "Zeit", "Von", "Aktion" }, new[] { 110, 110, 300 });
            var pHist = ListPage(histList);

            dPages = new[] { pEco, pRanks, pPerms, pMod, pHist };
            var host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card, Padding = new Padding(0, 8, 0, 0) };
            foreach (var pg in dPages) host.Controls.Add(pg);

            // ── console answer ──
            outLbl = Theme.Lbl("Antworten der Server-Konsole erscheinen hier.", Theme.Mono, Theme.Muted, Theme.Input);
            outLbl.AutoSize = false;
            outLbl.Dock = DockStyle.Fill;
            var outHost = new RoundPanel { Dock = DockStyle.Bottom, Height = 64, Fill = Theme.Input, Elevated = false, Flat = true, Padding = new Padding(10, 6, 8, 6) };
            outHost.Controls.Add(outLbl);

            detail.Controls.Add(host);
            detail.Controls.Add(outHost);
            detail.Controls.Add(strip);
            detail.Controls.Add(top);
            SetDetailTab(0);
        }
        Panel BuildRanks()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var bNew = Ui.Btn("+ Rang erstellen", Theme.Accent);
            var bDel = Ui.Btn("Rang löschen", Theme.Mix(Theme.Red, Theme.Bg, 0.35));
            var bWeight = Ui.Btn("Gewicht", Theme.CardHi);
            var bPrefix = Ui.Btn("Präfix", Theme.CardHi);
            var bAddPerm = Ui.Btn("+ Permission", Theme.CardHi);
            var bRemPerm = Ui.Btn("− Permission", Theme.CardHi);
            var bParent = Ui.Btn("Erbt von …", Theme.CardHi);
            var bar = Ui.Bar(50, bNew, bDel, bWeight, bPrefix, bAddPerm, bRemPerm, bParent);
            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12 };
            split.Panel1.BackColor = split.Panel2.BackColor = Theme.Bg;
            p.HandleCreated += (s, e) => { try { split.SplitterDistance = Math.Max(380, split.Width * 40 / 100); } catch { } };
            rankList = Ui.List(new[] { "Rang", "Gewicht", "Präfix", "Mitglieder" }, new[] { 140, 70, 160, 80 });
            rankPerms = Ui.List(new[] { "Permission", "Wert", "Kontext", "Ablauf" }, new[] { 320, 60, 140, 120 });
            var c1 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) }; c1.Controls.Add(rankList);
            var c2 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) }; c2.Controls.Add(rankPerms);
            split.Panel1.Controls.Add(c1);
            split.Panel2.Controls.Add(c2);
            p.Controls.Add(split);
            p.Controls.Add(bar);

            rankList.SelectedIndexChanged += async (s, e) => await LoadRankPerms();
            Func<string> selRank = () => Ui.Sel(rankList);
            bNew.Click += async (s, e) =>
            {
                var n = Dialogs.Ask(FindForm(), "Rang erstellen", "Name des neuen Rangs (klein, ohne Leerzeichen):", "");
                if (n == null) return; n = n.Trim().ToLowerInvariant();
                if (!GroupRx.IsMatch(n)) { setStatus("Ungültiger Rangname.", Theme.Red); return; }
                await Command(new[] { "lp creategroup " + n }, true, null);
            };
            bDel.Click += async (s, e) =>
            {
                var g = selRank(); if (g == null) return;
                if (g == "default") { setStatus("„default“ kann nicht gelöscht werden.", Theme.Yellow); return; }
                if (!Dialogs.Confirm(FindForm(), "Rang löschen", "Rang „" + g + "“ wirklich löschen? Mitglieder verlieren ihn.")) return;
                await Command(new[] { "lp deletegroup " + g }, true, null);
            };
            bWeight.Click += async (s, e) =>
            {
                var g = selRank(); if (g == null) return;
                var w = Dialogs.Ask(FindForm(), "Gewicht", "Gewicht für „" + g + "“ (höher = wichtiger):", groups.ContainsKey(g) ? groups[g].Weight.ToString() : "0");
                int wi; if (w == null || !int.TryParse(w.Trim(), out wi) || wi < 0) return;
                await Command(new[] { "lp group " + g + " setweight " + wi }, true, null);
            };
            bPrefix.Click += async (s, e) =>
            {
                var g = selRank(); if (g == null) return;
                var d = new FormDialog("Präfix für " + g, "Farbcodes mit & (z. B. &c&l[Admin] &7). Leer = Präfix entfernen.");
                var t = d.AddText("Präfix", groups.ContainsKey(g) ? groups[g].Prefix : "");
                var pr = d.AddText("Priorität", groups.ContainsKey(g) && groups[g].Weight > 0 ? groups[g].Weight.ToString() : "100");
                using (d)
                {
                    if (!d.Run(FindForm())) return;
                    int prio; if (!int.TryParse(pr.Box.Text.Trim(), out prio)) prio = 100;
                    var text = Clean(t.Box.Text).Replace("\"", "'");
                    var cmds = new List<string> { "lp group " + g + " meta removeprefix " + prio };
                    if (text.Length > 0) cmds.Add("lp group " + g + " meta setprefix " + prio + " \"" + text + "\"");
                    await Command(cmds.ToArray(), true, null);
                }
            };
            bAddPerm.Click += async (s, e) =>
            {
                var g = selRank(); if (g == null) return;
                var d = new FormDialog("Permission für " + g, null);
                var perm = d.AddText("Permission", "");
                var val = d.AddCombo("Wert", new[] { "true", "false" }, "true");
                var srv = d.AddText("Nur auf Server (leer = überall)", "");
                using (d)
                {
                    if (!d.Run(FindForm())) return;
                    var pm = perm.Box.Text.Trim();
                    if (!PermRx.IsMatch(pm)) { setStatus("Ungültige Permission.", Theme.Red); return; }
                    var ctx = srv.Box.Text.Trim();
                    if (ctx.Length > 0 && !CtxRx.IsMatch(ctx)) return;
                    await Command(new[] { "lp group " + g + " permission set " + pm + " " + val.SelectedItem + (ctx.Length > 0 ? " server=" + ctx : "") }, false, LoadRankPerms);
                }
            };
            bRemPerm.Click += async (s, e) =>
            {
                var g = selRank(); var pr = Ui.SelData<object[]>(rankPerms);
                if (g == null || pr == null) return;
                await Command(new[] { "lp group " + g + " permission " + ((long)pr[4] > 0 ? "unsettemp " : "unset ") + pr[0] + CtxArgs((string)pr[2], (string)pr[3]) }, false, LoadRankPerms);
            };
            bParent.Click += async (s, e) =>
            {
                var g = selRank(); if (g == null) return;
                var d = new FormDialog("Vererbung", "„" + g + "“ erbt alle Rechte des gewählten Rangs.");
                var parent = d.AddCombo("Erbt von", groups.Keys.Where(k => k != g).OrderBy(k => k), "default");
                var remove = d.AddCheck("Vererbung stattdessen entfernen", false);
                using (d)
                {
                    if (!d.Run(FindForm())) return;
                    await Command(new[] { "lp group " + g + " parent " + (remove.Checked ? "remove " : "add ") + parent.SelectedItem }, false, LoadRankPerms);
                }
            };
            return p;
        }

        Panel BuildLog()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var bRef = Ui.Btn("↻ Aktualisieren", Theme.CardHi);
            var info = Theme.Lbl("Letzte 300 LuckPerms-Änderungen und PlayerPoints-Buchungen (wer hat wem was gegeben).", Theme.F(9f), Theme.Muted, Theme.Bg);
            info.Margin = new Padding(6, 12, 0, 0);
            var bar = Ui.Bar(50, bRef, info);
            logList = Ui.List(new[] { "Zeit", "Quelle", "Von", "Ziel", "Aktion" }, new[] { 140, 110, 140, 140, 400 });
            var c = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            c.Controls.Add(logList);
            p.Controls.Add(c);
            p.Controls.Add(bar);
            bRef.Click += async (s, e) => await LoadLog();
            return p;
        }

        // ───────────────────────────── Remote helpers ─────────────────────────────
        Backend Cur { get { return serverBox.SelectedItem as Backend; } }

        ServerItem CurServer
        {
            get { var b = Cur; return b == null ? null : getServers().FirstOrDefault(s => s.Id == b.Id); }
        }

        /// <summary>Bash prelude: finds plugin dir, DB container and credentials from the plugin configs. Defines lpq / ppq (SQL from stdin).</summary>
        string Prelude()
        {
            var dir = Cur != null ? Cur.Dir : "";
            return
"B=" + Ssh.B64(dir + "/plugins") + "\n" +
"yv() { awk -v k=\"$2\" -v sec=\"$3\" 'BEGIN{ins=(sec==\"\")} sec!=\"\" && $0 ~ \"^\"sec\":\" {ins=1; next} sec!=\"\" && ins && /^[^ #]/ {ins=0} ins && $0 ~ \"^[ ]*\"k\":\" {sub(\"^[ ]*\"k\":[ ]*\",\"\"); gsub(/^[\"\\x27]|[\"\\x27][ ]*$/,\"\"); print; exit}' \"$1\" 2>/dev/null; }\n" +
"LC=$B/LuckPerms/config.yml; PC=$B/PlayerPoints/config.yml\n" +
"LU=$(yv $LC username data); LPW=$(yv $LC password data); LD=$(yv $LC database data); LT=$(yv $LC table-prefix data); LT=${LT:-luckperms_}\n" +
"LH=$(yv $LC address data); LH=${LH%%:*}\n" +
"PU=$(yv $PC user-name mysql-settings); PPW=$(yv $PC user-password mysql-settings); PD=$(yv $PC database-name mysql-settings)\n" +
"DBC=$(docker ps --format '{{.Names}}' | grep -xF \"$LH\" | head -1); [ -z \"$DBC\" ] && DBC=$(docker ps --format '{{.Names}}' | grep -iE 'mariadb|mysql' | head -1)\n" +
"lpq() { sed \"s/{lp}/$LT/g\" | docker exec -i -e MYSQL_PWD=\"$LPW\" \"$DBC\" sh -c 'exec $(command -v mariadb || command -v mysql) -u\"$0\" -D \"$1\" -N -B' \"$LU\" \"$LD\"; }\n" +
"ppq() { docker exec -i -e MYSQL_PWD=\"$PPW\" \"$DBC\" sh -c 'exec $(command -v mariadb || command -v mysql) -u\"$0\" -D \"$1\" -N -B' \"$PU\" \"$PD\"; }\n";
        }

        static string Heredoc(string fn, string sql) { return fn + " <<'RPSQL'\n" + sql + "\nRPSQL\n"; }

        async Task<bool> DetectBackends()
        {
            var r = await getSsh().Run("for d in /opt/crafty/docker/servers/*/; do [ -d \"$d/plugins/Essentials\" ] || [ -d \"$d/plugins/LuckPerms\" ] || continue; " +
                                        "n=$(basename \"$d\"); e=0; [ -d \"$d/plugins/Essentials/userdata\" ] && e=$(ls \"$d/plugins/Essentials/userdata\" | wc -l); echo \"$n $e\"; done", 30000);
            if (!r.Ok) { lastError = r.Message; return false; }
            var servers = getServers();
            var list = new List<Backend>();
            var counts = new Dictionary<string, int>();
            foreach (var l in r.Out.Split('\n'))
            {
                var f = l.Trim().Split(' ');
                if (f.Length < 2 || !UuidRx.IsMatch(f[0])) continue;
                var s = servers.FirstOrDefault(x => x.Id == f[0]);
                if (s != null && s.Type != null && s.Type.Contains("velocity")) continue;
                int c; int.TryParse(f[1], out c);
                counts[f[0]] = c;
                list.Add(new Backend { Id = f[0], Name = s != null ? s.Name : f[0].Substring(0, 8), Dir = "/opt/crafty/docker/servers/" + f[0] });
            }
            var prev = Cur != null ? Cur.Id : null;
            backends = list.OrderByDescending(b => counts[b.Id]).ToList();
            serverBox.Items.Clear();
            foreach (var b in backends) serverBox.Items.Add(b);
            var keep = backends.FirstOrDefault(b => b.Id == prev) ?? backends.FirstOrDefault();
            if (keep != null) serverBox.SelectedItem = keep;
            return backends.Count > 0;
        }

        // ───────────────────────────── Load ─────────────────────────────
        async Task Reload()
        {
            if (busy || !sshReady()) return;
            busy = true;
            progress.Set(double.NaN);
            try
            {
                if (backends.Count == 0 && !await DetectBackends())
                {
                    hSub.Text = "Kein Minecraft-Server mit Essentials/LuckPerms gefunden" + (lastError != null ? ": " + lastError : ".");
                    hSub.ForeColor = Theme.Yellow;
                    return;
                }
                var essAwk = "function v(s){sub(/^[^:]*:[ ]*/,\"\",s);gsub(/^['\"]|['\"][ ]*$/,\"\",s);return s}\n" +
                             "FNR==1{if(u!=\"\")print \"E\\t\"u\"\\t\"n\"\\t\"m\"\\t\"ip\"\\t\"li\"\\t\"lo; u=FILENAME;sub(/.*\\//,\"\",u);sub(/\\.yml$/,\"\",u);n=m=ip=li=lo=\"\";ts=0}\n" +
                             "/^last-account-name:/{n=v($0)} /^money:/{m=v($0)} /^ip-address:/{ip=v($0)}\n" +
                             "/^timestamps:/{ts=1;next} /^[^ ]/{ts=0} ts&&/^  login:/{li=v($0)} ts&&/^  logout:/{lo=v($0)}\n" +
                             "END{if(u!=\"\")print \"E\\t\"u\"\\t\"n\"\\t\"m\"\\t\"ip\"\\t\"li\"\\t\"lo}";
                var script = Prelude() +
                    "read -r -d '' PROG <<'RPAWK'\n" + essAwk + "\nRPAWK\n" +
                    "echo \"@@CUR $(yv $B/Essentials/config.yml currency-symbol)\"\n" +
                    "[ -d $B/Essentials/userdata ] && find $B/Essentials/userdata -maxdepth 1 -name '*.yml' -print0 | xargs -0 -r awk \"$PROG\"\n" +
                    "[ -d $B/DeluxeMobCoins/playerdata ] && find $B/DeluxeMobCoins/playerdata -maxdepth 1 -name '*.yml' -print0 | xargs -0 -r awk 'FNR==1{u=FILENAME;sub(/.*\\//,\"\",u);sub(/\\.yml$/,\"\",u)} /^balance:/{print \"M\\t\"u\"\\t\"$2}'\n" +
                    "if [ -n \"$LU\" ] && [ -n \"$DBC\" ]; then\n" +
                    Heredoc("lpq", "select 'P', uuid, username, primary_group from {lp}players;\n" +
                                   "select 'G', uuid, substring(permission,7), expiry, server, world from {lp}user_permissions where permission like 'group.%' and value=1;\n" +
                                   "select 'R', name, '' from {lp}groups;\n" +
                                   "select 'W', name, permission from {lp}group_permissions where permission like 'weight.%' or permission like 'prefix.%' or permission like 'displayname.%';") +
                    "else echo '@@ERR LuckPerms-Datenbank nicht gefunden (Zugangsdaten in LuckPerms/config.yml?)'; fi\n" +
                    "if [ -n \"$PU\" ] && [ -n \"$DBC\" ]; then\n" +
                    Heredoc("ppq", "select 'Q', uuid, points from playerpoints_points;\nselect 'N', uuid, username from playerpoints_username_cache;") +
                    "fi\ntrue";
                var r = await getSsh().Run(script, 90000);
                if (!r.Ok && r.Out.Length == 0) { hSub.Text = "Fehler: " + r.Message; hSub.ForeColor = Theme.Red; return; }
                Parse(r.Out);
                loadedOnce = true;
                var errs = r.Out.Split('\n').Where(l => l.StartsWith("@@ERR ")).Select(l => l.Substring(6)).ToList();
                if (!string.IsNullOrWhiteSpace(r.Err) && r.Err.Contains("ERROR")) errs.Add(r.Err.Trim().Split('\n').Last());
                var srv = CurServer;
                hSub.ForeColor = errs.Count > 0 ? Theme.Yellow : Theme.Muted;
                hSub.Text = errs.Count > 0 ? string.Join("  ·  ", errs) :
                    players.Count + " Spieler  ·  " + groups.Count + " Ränge  ·  Money & MobCoins von „" + (Cur != null ? Cur.Name : "?") + "“  ·  " +
                    (srv != null && srv.Running ? "Server läuft → Änderungen per Konsolenbefehl" : "Server offline → Änderungen direkt in Datei/DB");
                RenderList();
                RenderRanks();
                if (sel != null)
                {
                    var again = players.FirstOrDefault(p => p.Uuid == sel.Uuid);
                    if (again != null) { sel = again; RenderDetail(); var t = LoadDetail(); }
                }
            }
            finally { busy = false; progress.Set(-1); }
        }

        void Parse(string text)
        {
            var map = new Dictionary<string, McPlayer>();
            groups = new Dictionary<string, McGroup>();
            Func<string, McPlayer> get = u =>
            {
                u = u.Trim().ToLowerInvariant();
                McPlayer p;
                if (!map.TryGetValue(u, out p)) map[u] = p = new McPlayer { Uuid = u };
                return p;
            };
            foreach (var raw in text.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                if (l.StartsWith("@@CUR ")) { var c = l.Substring(6).Trim(); currency = c.Length > 0 && c.Length < 4 ? c : "$"; continue; }
                var f = l.Split('\t');
                if (f.Length < 3) continue;
                double d; long n;
                switch (f[0])
                {
                    case "E":
                        if (!UuidRx.IsMatch(f[1].ToLowerInvariant()) || f.Length < 7) break;
                        var e = get(f[1]);
                        if (f[2].Length > 0) e.Name = f[2];
                        if (double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) e.Money = d;
                        if (f[4].Length > 0) e.Ip = f[4];
                        if (double.TryParse(f[5], out d) && d > 0) e.Login = Ui.FromUnixMs(d);
                        if (double.TryParse(f[6], out d) && d > 0) e.Logout = Ui.FromUnixMs(d);
                        break;
                    case "M":
                        if (UuidRx.IsMatch(f[1].ToLowerInvariant()) && double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out d)) get(f[1]).MobCoins = d;
                        break;
                    case "P":
                        if (f.Length < 4 || !UuidRx.IsMatch(f[1])) break;
                        var p = get(f[1]);
                        if (string.IsNullOrEmpty(p.Name) && f[2] != "null") p.Name = f[2];
                        p.Primary = f[3];
                        break;
                    case "G":
                        if (f.Length < 6 || !UuidRx.IsMatch(f[1])) break;
                        long.TryParse(f[3], out n);
                        if (n > 0 && n < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) break;
                        get(f[1]).Groups.Add(new Membership { Group = f[2], Expiry = n, Server = f[4], World = f[5] });
                        break;
                    case "R":
                        if (!groups.ContainsKey(f[1])) groups[f[1]] = new McGroup { Name = f[1] };
                        break;
                    case "W":
                        McGroup g;
                        if (!groups.TryGetValue(f[1], out g)) groups[f[1]] = g = new McGroup { Name = f[1] };
                        if (f[2].StartsWith("weight.")) int.TryParse(f[2].Substring(7), out g.Weight);
                        else if (f[2].StartsWith("prefix."))
                        {
                            var parts = f[2].Split(new[] { '.' }, 3);
                            if (parts.Length == 3) { g.Prefix = parts[2]; g.Color = McColor(parts[2]); }
                        }
                        else if (f[2].StartsWith("displayname.")) g.Display = f[2].Substring(12);
                        break;
                    case "Q":
                        if (UuidRx.IsMatch(f[1]) && long.TryParse(f[2], out n)) get(f[1]).Coins = n;
                        break;
                    case "N":
                        if (UuidRx.IsMatch(f[1])) { var q = get(f[1]); if (string.IsNullOrEmpty(q.Name)) q.Name = f[2]; }
                        break;
                }
            }
            foreach (var p in map.Values)
            {
                if (string.IsNullOrEmpty(p.Name)) p.Name = p.Uuid.Substring(0, 8);
                if (groups.ContainsKey(p.Primary)) groups[p.Primary].Members++;
            }
            players = map.Values.ToList();
            MarkOnline();
        }

        void MarkOnline()
        {
            var online = new HashSet<string>(getServers().Where(s => s.Running).SelectMany(s => s.Players), StringComparer.OrdinalIgnoreCase);
            foreach (var p in players) p.Online = online.Contains(p.Name);
        }

        static Color McColor(string s)
        {
            var m = Regex.Match(s ?? "", @"&#([0-9a-fA-F]{6})|[&§]([0-9a-fA-F])");
            if (!m.Success) return Theme.Text2;
            if (m.Groups[1].Success) return Theme.Hex(m.Groups[1].Value);
            var codes = new[] { "#000000", "#0000AA", "#00AA00", "#00AAAA", "#AA0000", "#AA00AA", "#FFAA00", "#AAAAAA", "#555555", "#5555FF", "#55FF55", "#55FFFF", "#FF5555", "#FF55FF", "#FFFF55", "#FFFFFF" };
            var c = Theme.Hex(codes[Convert.ToInt32(m.Groups[2].Value, 16)]);
            return c.GetBrightness() < 0.25 ? Theme.Text2 : c;
        }

        static string StripColors(string s)
        {
            s = (s ?? "").Replace("\\\\", "\\").Replace("\\.", ".");
            return Regex.Replace(s, @"&#[0-9a-fA-F]{6}|[&§][0-9a-fk-orA-FK-OR]|<[^>]+>|%[A-Za-z0-9_]+%", "").Trim();
        }

        McGroup TopGroup(McPlayer p)
        {
            var names = p.Groups.Select(g => g.Group).Concat(new[] { p.Primary }).Distinct();
            return names.Select(n => groups.ContainsKey(n) ? groups[n] : null).Where(g => g != null).OrderByDescending(g => g.Weight).FirstOrDefault();
        }

        void RenderList()
        {
            var q = search.Box.Text.Trim();
            IEnumerable<McPlayer> src = players;
            if (q.Length > 0) src = src.Where(p => p.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || p.Uuid.Contains(q.ToLowerInvariant()) || (p.Ip ?? "") == q);
            switch (filterBox.SelectedIndex)
            {
                case 1: src = src.Where(p => p.Online); break;
                case 2: src = src.Where(p => { var g = TopGroup(p); return g != null && g.Name != "default" && g.Weight > 0; }); break;
                case 3: src = src.Where(p => p.Bedrock); break;
            }
            if (filterBox.SelectedIndex == 4) src = src.OrderByDescending(p => p.Money ?? 0);
            else if (filterBox.SelectedIndex == 5) src = src.OrderByDescending(p => p.Login ?? DateTime.MinValue);
            else if (filterBox.SelectedIndex == 2) src = src.OrderByDescending(p => TopGroup(p).Weight);
            else src = src.OrderByDescending(p => p.Online).ThenByDescending(p => p.LastSeen);
            var rows = new List<ListViewItem>();
            foreach (var p in src.Take(2000))
            {
                var g = TopGroup(p);
                var row = Ui.Row(new RowTag { Key = p.Uuid, Data = p, Dot = p.Online ? Theme.Green : Theme.Muted },
                    (p.Bedrock ? "◆ " : "") + p.Name, g != null ? g.Name : p.Primary,
                    p.Money.HasValue ? currency + Ui.Num(p.Money.Value) : "–", p.Coins.HasValue ? Ui.Num(p.Coins.Value) : "–",
                    p.MobCoins.HasValue ? Ui.Num(p.MobCoins.Value) : "–",
                    p.Online ? "online" : p.LastSeen > DateTime.MinValue ? Ui.Ago(p.LastSeen) : "–");
                if (g != null) row.SubItems[1].ForeColor = g.Color;
                if (p.Online) row.SubItems[5].ForeColor = Theme.Green;
                rows.Add(row);
            }
            Ui.Fill(pList, rows);
            countLbl.Text = rows.Count + " / " + players.Count + "  ·  " + players.Count(p => p.Online) + " online";
        }

        async Task ShowPlayer(McPlayer p)
        {
            sel = p;
            detailEmpty.Visible = false;
            detail.Visible = true;
            foreach (Control c in detail.Controls) c.Width = Math.Max(300, detail.ClientSize.Width - 14);
            RenderDetail();
            await LoadDetail();
        }

        void RenderDetail()
        {
            var p = sel;
            if (p == null) return;
            dName.Text = p.Name;
            var g = TopGroup(p);
            dRank.Set(g != null ? (StripColors(g.Prefix).Length > 0 ? StripColors(g.Prefix) : g.Name) : p.Primary, g != null ? g.Color : Theme.Muted);
            dOnline.Set(p.Online ? "Online" : "Offline", p.Online ? Theme.Green : Theme.Muted);
            dRank.Left = dName.Right + 10; dOnline.Left = dRank.Right + 8;
            var info = IpIntel.Get(p.Ip ?? "");
            var sb = new StringBuilder();
            sb.AppendLine("UUID  " + p.Uuid + (p.Bedrock ? "  ·  Bedrock / Floodgate" : "  ·  Java"));
            sb.AppendLine("IP  " + (p.Ip ?? "unbekannt") + (info != null ? "  ·  " + (info.City != null ? info.City + ", " : "") + (info.CountryCode ?? "") + "  ·  " + (info.Org ?? info.Isp ?? "") + (info.Suspicious ? "  ·  ⚠ " + info.Flags : "") : ""));
            sb.Append("Login  " + (p.Login.HasValue ? p.Login.Value.ToString("dd.MM.yyyy HH:mm") + " (" + Ui.Ago(p.Login.Value) + ")" : "–") +
                      "  ·  Logout  " + (p.Logout.HasValue ? p.Logout.Value.ToString("dd.MM.yyyy HH:mm") : "–"));
            dInfo.Font = Theme.F(9f);
            dInfo.Text = sb.ToString();
            if (layoutTop != null) layoutTop();
            var alts = string.IsNullOrEmpty(p.Ip) ? new List<McPlayer>() : players.Where(o => o != p && o.Ip == p.Ip).ToList();
            dAlts.Text = alts.Count > 0 ? "⚠ Gleiche IP wie: " + string.Join(", ", alts.Select(a => a.Name)) : "Keine anderen Accounts mit dieser IP.";
            dAlts.ForeColor = alts.Count > 0 ? Theme.Yellow : Theme.Muted;

            eMoney.Set(p.Money.HasValue ? currency + Ui.Num(p.Money.Value) : "–", "Essentials · " + (Cur != null ? Cur.Name : ""), Theme.Green);
            eCoins.Set(p.Coins.HasValue ? Ui.Num(p.Coins.Value) : "–", "PlayerPoints · global", Theme.Yellow);
            eMob.Set(p.MobCoins.HasValue ? Ui.Num(p.MobCoins.Value) : "–", "DeluxeMobCoins · " + (Cur != null ? Cur.Name : ""), Theme.Mint);

            var rows = new List<ListViewItem>();
            foreach (var m in p.Groups.OrderByDescending(x => groups.ContainsKey(x.Group) ? groups[x.Group].Weight : 0))
            {
                var gg = groups.ContainsKey(m.Group) ? groups[m.Group] : null;
                var row = Ui.Row(new RowTag { Key = m.Group + "|" + m.Expiry + "|" + m.Server + "|" + m.World, Data = m, Dot = gg != null ? gg.Color : Theme.Muted },
                    m.Group + (m.Group == p.Primary ? "  (primär)" : ""), m.Expiry > 0 ? Expiry(m.Expiry) : "dauerhaft", Ctx(m.Server, m.World));
                if (m.Expiry > 0) row.SubItems[1].ForeColor = Theme.Yellow;
                rows.Add(row);
            }
            Ui.Fill(gList, rows);
        }

        static string Ctx(string server, string world)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(server) && server != "global") parts.Add("server=" + server);
            if (!string.IsNullOrEmpty(world) && world != "global") parts.Add("world=" + world);
            return parts.Count == 0 ? "überall" : string.Join(" ", parts);
        }

        static string CtxArgs(string server, string world)
        {
            var s = "";
            if (!string.IsNullOrEmpty(server) && server != "global" && CtxRx.IsMatch(server)) s += " server=" + server;
            if (!string.IsNullOrEmpty(world) && world != "global" && CtxRx.IsMatch(world)) s += " world=" + world;
            return s;
        }

        static string Expiry(long unix)
        {
            var d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix).ToLocalTime();
            var left = d - DateTime.Now;
            return d.ToString("dd.MM.yy HH:mm") + "  (" + (left.TotalDays >= 1 ? (int)left.TotalDays + " T" : left.TotalHours >= 1 ? (int)left.TotalHours + " h" : Math.Max(0, (int)left.TotalMinutes) + " min") + ")";
        }

        async Task LoadDetail()
        {
            var p = sel;
            if (p == null || !UuidRx.IsMatch(p.Uuid)) return;
            var script = Prelude() +
                Heredoc("lpq", "select 'X', permission, value, server, world, expiry from {lp}user_permissions where uuid='" + p.Uuid + "' and permission not like 'group.%';\n" +
                               "select 'A', time, actor_name, concat(type, ' ', action) from {lp}actions where acted_uuid='" + p.Uuid + "' order by id desc limit 60;") +
                Heredoc("ppq", "select 'T', unix_timestamp(timestamp), coalesce((select username from playerpoints_username_cache c where c.uuid=t.source), t.source, 'Konsole'), concat(transaction_type, ' ', amount, ' – ', description) from playerpoints_transaction_log t where receiver='" + p.Uuid + "' order by timestamp desc limit 60;") +
                "true";
            var r = await getSsh().Run(script, 45000);
            if (p != sel) return;
            var perms = new List<ListViewItem>();
            var hist = new List<KeyValuePair<long, string[]>>();
            foreach (var l in r.Out.Split('\n'))
            {
                var f = l.TrimEnd('\r').Split('\t');
                if (f[0] == "X" && f.Length >= 6)
                {
                    long exp; long.TryParse(f[5], out exp);
                    if (exp > 0 && exp < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) continue;
                    bool val = f[2] == "1";
                    var kind = f[1].StartsWith("prefix.") ? "Präfix" : f[1].StartsWith("suffix.") ? "Suffix" : f[1].StartsWith("meta.") ? "Meta" : null;
                    var row = Ui.Row(new RowTag { Key = f[1] + "|" + f[3] + "|" + f[4] + "|" + exp, Data = new object[] { f[1], val, f[3], f[4], exp }, Dot = val ? Theme.Green : Theme.Red },
                        (kind != null ? "[" + kind + "] " : "") + f[1], val ? "true" : "false", Ctx(f[3], f[4]), exp > 0 ? Expiry(exp) : "dauerhaft");
                    row.SubItems[1].ForeColor = val ? Theme.Green : Theme.Red;
                    perms.Add(row);
                }
                else if ((f[0] == "A" || f[0] == "T") && f.Length >= 4)
                {
                    long t; long.TryParse(f[1], out t);
                    hist.Add(new KeyValuePair<long, string[]>(t, new[] { f[0] == "A" ? "Rang/Recht" : "Coins", f[2], f[3] }));
                }
            }
            Ui.Fill(mList, perms.OrderBy(x => x.Text).ToList());
            var hrows = hist.OrderByDescending(h => h.Key).Select(h =>
            {
                var d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(h.Key).ToLocalTime();
                var row = Ui.Row(new RowTag { Key = h.Key + h.Value[2], Dot = h.Value[0] == "Coins" ? Theme.Yellow : Theme.Accent }, d.ToString("dd.MM.yy HH:mm"), h.Value[1], h.Value[2]);
                return row;
            }).ToList();
            Ui.Fill(histList, hrows);
        }

        void RenderRanks()
        {
            var rows = new List<ListViewItem>();
            foreach (var g in groups.Values.OrderByDescending(x => x.Weight).ThenBy(x => x.Name))
            {
                int members = players.Count(p => p.Primary == g.Name || p.Groups.Any(m => m.Group == g.Name));
                var row = Ui.Row(new RowTag { Key = g.Name, Data = g, Dot = g.Color }, g.Name, g.Weight.ToString(), StripColors(g.Prefix), members.ToString());
                row.SubItems[2].ForeColor = g.Color;
                rows.Add(row);
            }
            Ui.Fill(rankList, rows);
        }

        async Task LoadRankPerms()
        {
            var g = Ui.Sel(rankList);
            if (g == null || !GroupRx.IsMatch(g)) return;
            var r = await getSsh().Run(Prelude() + Heredoc("lpq", "select permission, value, server, world, expiry from {lp}group_permissions where name='" + g + "' order by permission;"), 30000);
            if (Ui.Sel(rankList) != g) return;
            var rows = new List<ListViewItem>();
            foreach (var l in r.Out.Split('\n'))
            {
                var f = l.TrimEnd('\r').Split('\t');
                if (f.Length < 5) continue;
                long exp; long.TryParse(f[4], out exp);
                bool val = f[1] == "1";
                var row = Ui.Row(new RowTag { Key = string.Join("|", f), Data = new object[] { f[0], val, f[2], f[3], exp }, Dot = f[0].StartsWith("group.") ? Theme.Accent : val ? Theme.Green : Theme.Red },
                    f[0].StartsWith("group.") ? "⇧ erbt von " + f[0].Substring(6) : f[0], val ? "true" : "false", Ctx(f[2], f[3]), exp > 0 ? Expiry(exp) : "dauerhaft");
                row.SubItems[1].ForeColor = val ? Theme.Green : Theme.Red;
                rows.Add(row);
            }
            Ui.Fill(rankPerms, rows);
        }

        async Task LoadLog()
        {
            if (!sshReady() || backends.Count == 0) return;
            progress.Set(double.NaN);
            try
            {
                var r = await getSsh().Run(Prelude() +
                    Heredoc("lpq", "select 'A', time, actor_name, acted_name, concat(type, ' ', action) from {lp}actions order by id desc limit 300;") +
                    Heredoc("ppq", "select 'T', unix_timestamp(timestamp), coalesce((select username from playerpoints_username_cache c where c.uuid=t.source), t.source, 'Konsole'), coalesce((select username from playerpoints_username_cache c where c.uuid=t.receiver), t.receiver), concat(transaction_type, ' ', amount, ' – ', description) from playerpoints_transaction_log t order by timestamp desc limit 300;") +
                    "true", 45000);
                var rows = new List<KeyValuePair<long, ListViewItem>>();
                foreach (var l in r.Out.Split('\n'))
                {
                    var f = l.TrimEnd('\r').Split('\t');
                    if (f.Length < 5) continue;
                    long t; long.TryParse(f[1], out t);
                    var d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(t).ToLocalTime();
                    var row = Ui.Row(new RowTag { Key = f[0] + t + f[4], Dot = f[0] == "T" ? Theme.Yellow : Theme.Accent }, d.ToString("dd.MM.yy HH:mm:ss"), f[0] == "T" ? "Coins" : "LuckPerms", f[2], f[3], f[4]);
                    rows.Add(new KeyValuePair<long, ListViewItem>(t, row));
                }
                Ui.Fill(logList, rows.OrderByDescending(x => x.Key).Select(x => x.Value).ToList());
                if (!r.Ok) setStatus("Protokoll: " + r.Message, Theme.Yellow);
            }
            finally { progress.Set(-1); }
        }

        // ───────────────────────────── Commands ─────────────────────────────
        static string Clean(string s) { return Regex.Replace(s ?? "", @"[\r\n\t\x00-\x1f]", " ").Trim(); }

        /// <summary>Sends console commands to the selected backend and shows the console's answer.</summary>
        async Task<bool> Command(string[] cmds, bool reloadAll, Func<Task> after)
        {
            var srv = CurServer;
            if (srv == null || !srv.Running)
            {
                var any = getServers().FirstOrDefault(s => s.Running && backends.Any(b => b.Id == s.Id));
                if (any == null) { setStatus("Kein Minecraft-Server mit LuckPerms läuft – Befehl nicht möglich. Bitte einen Server starten.", Theme.Yellow); return false; }
                srv = any;
            }
            progress.Set(double.NaN);
            try
            {
                var before = await api.Get("/api/v2/servers/" + srv.Id + "/logs?raw=true");
                var prev = before.Ok ? J.L(before.Data).Select(o => Util.StripAnsi(Convert.ToString(o)).TrimEnd()).ToList() : new List<string>();
                foreach (var c in cmds)
                {
                    var r = await api.Send("POST", "/api/v2/servers/" + srv.Id + "/stdin", c, "text/plain; charset=utf-8");
                    if (!r.Ok) { setStatus("Befehl fehlgeschlagen: " + r.Error, Theme.Red); return false; }
                }
                await Task.Delay(1300);
                var afterLog = await api.Get("/api/v2/servers/" + srv.Id + "/logs?raw=true");
                var now = afterLog.Ok ? J.L(afterLog.Data).Select(o => Util.StripAnsi(Convert.ToString(o)).TrimEnd()).ToList() : new List<string>();
                var fresh = Util.NewLines(prev, now) ?? now.Skip(Math.Max(0, now.Count - 8)).ToList();
                var shown = fresh.Where(x => x.Trim().Length > 0).Select(x => Regex.Replace(x, @"^\[[\d:]+ [A-Z]+\]:?\s*", "")).Take(12).ToList();
                outLbl.Text = "> " + string.Join("\n> ", cmds) + "\n" + string.Join("\n", shown);
                bool err = shown.Any(x => Regex.IsMatch(x, "(?i)unknown|not found|invalid|error|fehler|nicht gefunden|usage|benutzung|does not exist"));
                outLbl.ForeColor = err ? Theme.Yellow : Theme.Text2;
                setStatus((err ? "Antwort prüfen: " : "Ausgeführt auf " + srv.Name + ": ") + cmds.Last(), err ? Theme.Yellow : Theme.Green);
            }
            finally { progress.Set(-1); }
            await Task.Delay(400);
            if (reloadAll) await Reload();
            else if (after != null) await after();
            else { await Reload(); }
            return true;
        }

        /// <summary>Direct write when the server is offline: SQL (LuckPerms / PlayerPoints) or YAML edit (Essentials / MobCoins).</summary>
        async Task<bool> Direct(string label, string script)
        {
            if (!Dialogs.Confirm(FindForm(), "Server offline", "„" + (Cur != null ? Cur.Name : "") + "“ läuft gerade nicht.\n\n" + label +
                "\n\nDirekt in Datenbank/Datei schreiben? (Sicher, solange der Server aus ist.)")) return false;
            progress.Set(double.NaN);
            try
            {
                var r = await getSsh().Run(Prelude() + script, 45000);
                if (!r.Ok) { setStatus(label + " fehlgeschlagen: " + r.Message, Theme.Red); return false; }
                outLbl.Text = "Direkt geschrieben: " + label + (r.Out.Trim().Length > 0 ? "\n" + r.Out.Trim() : "");
                outLbl.ForeColor = Theme.Text2;
                setStatus(label + " – direkt gespeichert.", Theme.Green);
                foreach (var s in getServers().Where(x => x.Running && backends.Any(b => b.Id == x.Id)))
                    await api.Send("POST", "/api/v2/servers/" + s.Id + "/stdin", "lp sync", "text/plain; charset=utf-8");
            }
            finally { progress.Set(-1); }
            await Reload();
            return true;
        }

        bool Online { get { var s = CurServer; return s != null && s.Running; } }

        async Task Economy(string kind, string op)
        {
            var p = sel;
            if (p == null) return;
            var names = new Dictionary<string, string> { { "money", "Money" }, { "coins", "Coins" }, { "mob", "MobCoins" } };
            double cur = kind == "money" ? p.Money ?? 0 : kind == "coins" ? p.Coins ?? 0 : p.MobCoins ?? 0;
            var verb = op == "set" ? "setzen auf" : op == "give" ? "hinzufügen" : "abziehen";
            var a = Dialogs.Ask(FindForm(), names[kind] + " – " + p.Name, names[kind] + " " + verb + " (aktuell " + Ui.Num(cur) + ").  Kürzel: 5k, 2.5m", op == "set" ? Ui.Num(cur).Replace(".", "") : "");
            double v;
            if (a == null) return;
            if (!Ui.TryNum(a, out v)) { setStatus("Ungültiger Betrag.", Theme.Red); return; }
            if (kind == "coins") v = Math.Round(v);
            var amount = v.ToString("0.##", CultureInfo.InvariantCulture);
            double target = op == "set" ? v : op == "give" ? cur + v : Math.Max(0, cur - v);
            if (op == "take" && v > cur && !Dialogs.Confirm(FindForm(), "Zu wenig", p.Name + " hat nur " + Ui.Num(cur) + ". Auf 0 setzen?")) return;
            if (!NameRx.IsMatch(p.Name)) { setStatus("Spielername enthält ungültige Zeichen.", Theme.Red); return; }
            if (Online)
            {
                string cmd;
                if (kind == "money") cmd = "eco " + op + " " + p.Name + " " + amount;
                else if (kind == "coins") cmd = "points " + op + " " + p.Name + " " + amount;
                else cmd = op == "give" ? "mobcoins give " + p.Name + " " + amount : "mobcoins set " + p.Name + " " + target.ToString("0.##", CultureInfo.InvariantCulture);
                await Command(new[] { cmd }, false, null);
                return;
            }
            var t = target.ToString("0.##", CultureInfo.InvariantCulture);
            if (kind == "coins")
                await Direct("Coins von " + p.Name + " → " + t,
                    Heredoc("ppq", "insert into playerpoints_points (uuid, points) values ('" + p.Uuid + "', " + (long)target + ") on duplicate key update points=" + (long)target + ";\n" +
                                   "insert into playerpoints_transaction_log (transaction_type, description, source, receiver, amount) values ('SET', 'RyvenPanel', null, '" + p.Uuid + "', " + (long)target + ");"));
            else
            {
                var file = kind == "money" ? "$B/Essentials/userdata/" + p.Uuid + ".yml" : "$B/DeluxeMobCoins/playerdata/" + p.Uuid + ".yml";
                var key = kind == "money" ? "money" : "balance";
                var val = kind == "money" ? "'" + t + "'" : t;
                await Direct(names[kind] + " von " + p.Name + " → " + t,
                    "F=" + file + "\n[ -f \"$F\" ] || { echo 'Datei fehlt: '$F >&2; exit 4; }\n" +
                    "if grep -q '^" + key + ":' \"$F\"; then sed -i \"s/^" + key + ":.*/" + key + ": " + val + "/\" \"$F\"; else echo \"" + key + ": " + val + "\" >> \"$F\"; fi\ngrep '^" + key + ":' \"$F\"");
            }
        }

        static long ParseDuration(string d)
        {
            long secs = 0;
            foreach (Match m in Regex.Matches(d, @"(\d+)(mo|[smhdwy])"))
            {
                long n = long.Parse(m.Groups[1].Value);
                switch (m.Groups[2].Value)
                {
                    case "s": secs += n; break;
                    case "m": secs += n * 60; break;
                    case "h": secs += n * 3600; break;
                    case "d": secs += n * 86400; break;
                    case "w": secs += n * 604800; break;
                    case "mo": secs += n * 2592000; break;
                    case "y": secs += n * 31536000; break;
                }
            }
            return secs;
        }

        async Task GroupAction(string op)
        {
            var p = sel;
            if (p == null) return;
            string group = null, dur = null, server = null;
            Membership m = null;
            if (op == "remove")
            {
                m = Ui.SelData<Membership>(gList);
                if (m == null) { setStatus("Bitte eine Gruppe in der Liste auswählen.", Theme.Yellow); return; }
                if (!Dialogs.Confirm(FindForm(), "Gruppe entfernen", "„" + m.Group + "“ von " + p.Name + " entfernen?")) return;
                group = m.Group;
            }
            else
            {
                var d = new FormDialog(op == "set" ? "Rang setzen – " + p.Name : "Gruppe hinzufügen – " + p.Name,
                    op == "set" ? "Ersetzt alle dauerhaften Gruppen durch den gewählten Rang (LuckPerms „parent set“)." : null);
                var gb = d.AddCombo("Rang", groups.Values.OrderByDescending(g => g.Weight).Select(g => g.Name), p.Primary);
                Field durF = null, srvF = null;
                if (op == "addtemp") durF = d.AddText("Dauer (z. B. 30d, 12h, 1mo, 1w2d)", "30d");
                if (op != "set") srvF = d.AddText("Nur auf Server (leer = überall)", "");
                using (d)
                {
                    if (!d.Run(FindForm())) return;
                    group = gb.SelectedItem as string;
                    if (durF != null) dur = durF.Box.Text.Trim().ToLowerInvariant();
                    if (srvF != null) server = srvF.Box.Text.Trim();
                }
            }
            if (group == null || !GroupRx.IsMatch(group)) { setStatus("Ungültiger Rang.", Theme.Red); return; }
            if (dur != null && !DurRx.IsMatch(dur)) { setStatus("Ungültige Dauer (z. B. 30d, 12h, 1mo).", Theme.Red); return; }
            if (!string.IsNullOrEmpty(server) && !CtxRx.IsMatch(server)) { setStatus("Ungültiger Servername.", Theme.Red); return; }
            var ctx = string.IsNullOrEmpty(server) ? "" : " server=" + server;
            var u = p.Uuid;
            if (Online)
            {
                string cmd;
                if (op == "set") cmd = "lp user " + u + " parent set " + group;
                else if (op == "add") cmd = "lp user " + u + " parent add " + group + ctx;
                else if (op == "addtemp") cmd = "lp user " + u + " parent addtemp " + group + " " + dur + ctx;
                else cmd = "lp user " + u + " parent " + (m.Expiry > 0 ? "removetemp " : "remove ") + group + CtxArgs(m.Server, m.World);
                await Command(new[] { cmd }, false, null);
                return;
            }
            var srvCol = string.IsNullOrEmpty(server) ? "global" : server;
            string sql;
            if (op == "set")
                sql = "delete from {lp}user_permissions where uuid='" + u + "' and permission like 'group.%' and expiry=0 and server='global' and world='global';\n" +
                      "insert into {lp}user_permissions (uuid, permission, value, server, world, expiry, contexts) values ('" + u + "', 'group." + group + "', 1, 'global', 'global', 0, '{}');\n" +
                      "insert into {lp}players (uuid, username, primary_group) values ('" + u + "', '" + p.Name.ToLowerInvariant() + "', '" + group + "') on duplicate key update primary_group='" + group + "';";
            else if (op == "add" || op == "addtemp")
            {
                long exp = op == "addtemp" ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ParseDuration(dur) : 0;
                sql = "insert into {lp}user_permissions (uuid, permission, value, server, world, expiry, contexts) values ('" + u + "', 'group." + group + "', 1, '" + srvCol + "', 'global', " + exp + ", '{}');";
            }
            else
                sql = "delete from {lp}user_permissions where uuid='" + u + "' and permission='group." + group + "' and expiry=" + m.Expiry + " and server='" + (CtxRx.IsMatch(m.Server ?? "") ? m.Server : "global") + "';\n" +
                      "update {lp}players set primary_group='default' where uuid='" + u + "' and primary_group='" + group + "';";
            await Direct("Rang-Änderung für " + p.Name + " (" + op + " " + group + ")", Heredoc("lpq", sql));
        }

        async Task PermAction(string op, object[] row)
        {
            var p = sel;
            if (p == null) return;
            string perm, val = "true", dur = null, server = "", world = "";
            long exp = 0;
            if (row != null)
            {
                perm = (string)row[0]; val = (bool)row[1] ? "true" : "false"; server = (string)row[2]; world = (string)row[3]; exp = (long)row[4];
                if (op == "unset" && !Dialogs.Confirm(FindForm(), "Permission entfernen", "„" + perm + "“ von " + p.Name + " entfernen?")) return;
            }
            else
            {
                var d = new FormDialog((op == "settemp" ? "Temporäre Permission – " : "Permission – ") + p.Name, "Beispiele: essentials.fly · worldedit.* · -essentials.home (false = verbieten)");
                var pf = d.AddText("Permission", "");
                var vf = d.AddCombo("Wert", new[] { "true", "false" }, "true");
                Field df = null;
                if (op == "settemp") df = d.AddText("Dauer (z. B. 1d, 12h, 30m)", "1d");
                var sf = d.AddText("Nur auf Server (leer = überall)", "");
                var wf = d.AddText("Nur in Welt (leer = überall)", "");
                using (d)
                {
                    if (!d.Run(FindForm())) return;
                    perm = pf.Box.Text.Trim(); val = (string)vf.SelectedItem;
                    if (df != null) dur = df.Box.Text.Trim().ToLowerInvariant();
                    server = sf.Box.Text.Trim(); world = wf.Box.Text.Trim();
                }
            }
            if (!PermRx.IsMatch(perm ?? "")) { setStatus("Ungültige Permission (keine Leerzeichen).", Theme.Red); return; }
            if (dur != null && !DurRx.IsMatch(dur)) { setStatus("Ungültige Dauer.", Theme.Red); return; }
            if ((server.Length > 0 && server != "global" && !CtxRx.IsMatch(server)) || (world.Length > 0 && world != "global" && !CtxRx.IsMatch(world))) { setStatus("Ungültiger Kontext.", Theme.Red); return; }
            var ctx = CtxArgs(server, world);
            var u = p.Uuid;
            if (op == "toggle") val = val == "true" ? "false" : "true";
            if (Online)
            {
                string cmd;
                if (op == "set" || (op == "toggle" && exp == 0)) cmd = "lp user " + u + " permission set " + perm + " " + val + ctx;
                else if (op == "settemp") cmd = "lp user " + u + " permission settemp " + perm + " " + val + " " + dur + ctx;
                else if (op == "toggle") { setStatus("Temporäre Rechte bitte entfernen und neu setzen.", Theme.Yellow); return; }
                else cmd = "lp user " + u + " permission " + (exp > 0 ? "unsettemp " : "unset ") + perm + ctx;
                await Command(new[] { cmd }, false, LoadDetail);
                return;
            }
            var sc = server.Length > 0 ? server : "global";
            var wc = world.Length > 0 ? world : "global";
            var where = "uuid='" + u + "' and permission='" + perm + "' and server='" + sc + "' and world='" + wc + "'";
            string sql;
            if (op == "unset") sql = "delete from {lp}user_permissions where " + where + " and expiry=" + exp + ";";
            else if (op == "toggle") sql = "update {lp}user_permissions set value=" + (val == "true" ? 1 : 0) + " where " + where + " and expiry=" + exp + ";";
            else
            {
                long e2 = op == "settemp" ? DateTimeOffset.UtcNow.ToUnixTimeSeconds() + ParseDuration(dur) : 0;
                sql = "delete from {lp}user_permissions where " + where + " and expiry=" + e2 + ";\n" +
                      "insert into {lp}user_permissions (uuid, permission, value, server, world, expiry, contexts) values ('" + u + "', '" + perm + "', " + (val == "true" ? 1 : 0) + ", '" + sc + "', '" + wc + "', " + e2 + ", '{}');";
            }
            await Direct("Permission " + perm + " für " + p.Name, Heredoc("lpq", sql));
        }

        /// <summary>Set by the main window – network-wide punishments through RyvenBans (mod.py).</summary>
        public ModClient Mod;

        async Task Moderate(string kind)
        {
            var p = sel;
            if (p == null) return;
            if (Mod == null) { setStatus("Moderation nicht verfügbar.", Theme.Red); return; }
            if (!NameRx.IsMatch(p.Name)) { setStatus("Ungültiger Spielername.", Theme.Red); return; }
            if (await PunishDialog.Run(FindForm(), Mod, p.Name, kind, kind == "kick" ? "Bitte halte dich an die Regeln." : "", setStatus)) await LoadDetail();
        }
    }
}