using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>Talks to mod.py on the server – via SSH for the owner, via the team gateway (/panel/mod) for team members.</summary>
    class ModClient
    {
        readonly Api api;
        readonly bool viaGateway;
        readonly Func<Ssh> getSsh;
        public string Actor = "Owner (Panel)";
        bool installed;
        public bool CanView = true, CanReports = true, CanPunish = true, CanBan = true;
        List<object> templates;

        public ModClient(Api api, bool viaGateway, Func<Ssh> getSsh)
        {
            this.api = api;
            this.viaGateway = viaGateway;
            this.getSsh = getSsh;
        }

        public bool Allowed(string action)
        {
            switch (action)
            {
                case "ban": case "ipban": case "unban": return CanBan;
                default: return CanPunish;
            }
        }

        /// <summary>Only for offscreen screenshots with demo data – never set in normal use.</summary>
        public static Func<string, Dictionary<string, object>, object> Demo;

        public async Task<KeyValuePair<object, string>> Call(string cmd, Dictionary<string, object> args = null)
        {
            args = args ?? new Dictionary<string, object>();
            if (Demo != null) { await Task.Delay(1); return new KeyValuePair<object, string>(Demo(cmd, args), null); }
            if (viaGateway)
            {
                var r = await api.SendRaw("POST", "/panel/mod", Encoding.UTF8.GetBytes(J.ToJson(new Dictionary<string, object> { { "cmd", cmd }, { "args", args } })),
                                          "application/json", null, 60000);
                return r.Ok ? new KeyValuePair<object, string>(r.Data, null) : new KeyValuePair<object, string>(null, r.Error);
            }
            if (!args.ContainsKey("actor")) args["actor"] = Actor;
            var sb = new StringBuilder();
            if (!installed)
                sb.Append(Ssh.InstallIfNewer("/opt/pxmx/mod.py", TeamView.ModSource()));
            sb.Append("python3 /opt/pxmx/mod.py ").Append(cmd).Append(' ')
              .Append(Convert.ToBase64String(Encoding.UTF8.GetBytes(J.ToJson(args))));
            var res = await getSsh().Run(sb.ToString(), 60000);
            if (!res.Ok) return new KeyValuePair<object, string>(null, res.Message);
            installed = true;
            object j;
            try { j = J.Parse(res.Out.Trim()); } catch { return new KeyValuePair<object, string>(null, "Unerwartete Antwort: " + res.Message); }
            if (J.S(j, "status") != "ok") return new KeyValuePair<object, string>(null, J.S(j, "error") ?? "Fehler");
            return new KeyValuePair<object, string>(J.Get(j, "data"), null);
        }

        public async Task<List<object>> Templates()
        {
            if (templates != null) return templates;
            var r = await Call("templates");
            if (r.Value == null) templates = J.L(J.Get(r.Key, "templates")).ToList();
            return templates ?? new List<object>();
        }

        // ── display helpers shared by the moderation views ──
        public static string TypeName(string t, bool ip = false)
        {
            switch (t)
            {
                case "ban": return ip ? "IP-Bann" : "Bann";
                case "mute": return "Mute";
                case "kick": return "Kick";
                case "warn": return "Verwarnung";
                default: return t;
            }
        }

        public static Color TypeColor(string t)
        {
            switch (t)
            {
                case "ban": return Theme.Red;
                case "mute": return Theme.Yellow;
                case "kick": return Theme.Mint;
                case "warn": return Theme.SoftGreen;
                default: return Theme.Muted;
            }
        }

        public static string StatusName(string s)
        {
            switch (s)
            {
                case "open": return "offen";
                case "claimed": return "in Bearbeitung";
                case "done": return "erledigt";
                case "rejected": return "abgelehnt";
                default: return s;
            }
        }

        public static Color StatusColor(string s)
        {
            switch (s)
            {
                case "open": return Theme.Red;
                case "claimed": return Theme.Yellow;
                case "done": return Theme.Green;
                default: return Theme.Muted;
            }
        }

        /// <summary>"2026-11-13 15:17:25" → "13.11. 15:17"</summary>
        public static string When(string ts)
        {
            DateTime d;
            if (string.IsNullOrEmpty(ts) || !DateTime.TryParseExact(ts, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) return ts ?? "–";
            return d.Date == DateTime.Today ? "heute " + d.ToString("HH:mm") : d.Year == DateTime.Today.Year ? d.ToString("dd.MM. HH:mm") : d.ToString("dd.MM.yy HH:mm");
        }

        public static string Until(object p)
        {
            var t = J.S(p, "type");
            if (t != "ban" && t != "mute") return "–";
            var exp = J.S(p, "expires");
            return string.IsNullOrEmpty(exp) ? "permanent" : "bis " + When(exp);
        }

        public static string State(object p)
        {
            var t = J.S(p, "type");
            if (t != "ban" && t != "mute") return "erteilt";
            if (J.N(p, "active") > 0) return "aktiv";
            var by = J.S(p, "removed_by");
            return by == "Ablauf" ? "abgelaufen" : by == null ? "beendet" : "aufgehoben von " + by;
        }

        public static string Playtime(double sec)
        {
            if (sec <= 0) return "–";
            var h = sec / 3600;
            return h >= 1 ? h.ToString("0.#", CultureInfo.GetCultureInfo("de-DE")) + " h" : Math.Round(sec / 60) + " min";
        }
    }

    /// <summary>"Strafe vergeben": action, template (with escalation), duration, reason – executed network-wide through RyvenBans.</summary>
    static class PunishDialog
    {
        static readonly string[] Actions = { "warn", "kick", "mute", "ban", "ipban" };
        static readonly string[] ActionNames = { "Verwarnen", "Kicken", "Muten", "Bannen", "IP-Bann (alle Accounts dieser IP)" };

        /// <returns>true when something was done</returns>
        public static async Task<bool> Run(IWin32Window owner, ModClient mc, string player, string action, string reason, Action<string, Color> setStatus)
        {
            var allowed = Actions.Where(mc.Allowed).ToList();
            if (allowed.Count == 0) { setStatus("Dir fehlt das Recht zu strafen.", Theme.Red); return false; }
            var templates = await mc.Templates();
            var tplNames = new List<string> { "— keine Vorlage (eigener Grund) —" };
            foreach (var t in templates)
            {
                var ty = J.S(t, "type");
                if (!mc.Allowed(ty)) continue;
                tplNames.Add(J.S(t, "key") + " · " + J.S(t, "reason") + "  (" + ModClient.TypeName(ty) + ": " + string.Join(" → ", J.L(J.Get(t, "ladder")).Select(Convert.ToString)) + ")");
            }
            Field fPlayer, fDur, fReason;
            ComboBox cAction, cTpl;
            CheckBox cSilent;
            using (var d = new FormDialog("Strafe vergeben", "Gilt im ganzen Netzwerk (RyvenBans). Mit Vorlage steigt die Dauer bei Wiederholung automatisch."))
            {
                fPlayer = d.AddText("Spieler", player ?? "");
                cAction = d.AddCombo("Aktion", allowed.Select(a => ActionNames[Array.IndexOf(Actions, a)]), ActionNames[Array.IndexOf(Actions, allowed.Contains(action ?? "") ? action : allowed[0])]);
                cTpl = d.AddCombo("Vorlage", tplNames, tplNames[0]);
                fDur = d.AddText("Dauer (nur Mute/Bann) – z. B. 30m, 6h, 7d, 30d · leer = Vorlage bzw. permanent", "");
                fReason = d.AddText("Grund (ohne Vorlage)", reason ?? "");
                cSilent = d.AddCheck("Still – nur das Team wird benachrichtigt", false);
                cTpl.SelectedIndexChanged += (s, e) =>
                {
                    int i = cTpl.SelectedIndex - 1;
                    var tt = tplNames.Count > 1 && i >= 0 ? templates.Where(t => mc.Allowed(J.S(t, "type"))).ElementAt(i) : null;
                    fReason.Box.Enabled = tt == null;
                    if (tt != null)
                    {
                        var ty = J.S(tt, "type");
                        var idx = allowed.IndexOf(ty);
                        if (idx >= 0) cAction.SelectedIndex = idx;
                    }
                };
                if (!d.Run(owner, "Ausführen")) return false;
            }
            var name = fPlayer.Box.Text.Trim();
            var act = allowed[Math.Max(0, cAction.SelectedIndex)];
            string why = fReason.Box.Text.Trim();
            int ti = cTpl.SelectedIndex - 1;
            if (ti >= 0) why = J.S(templates.Where(t => mc.Allowed(J.S(t, "type"))).ElementAt(ti), "key");
            if (name.Length == 0) { setStatus("Bitte einen Spieler angeben.", Theme.Yellow); return false; }
            if (why.Length == 0) { setStatus("Bitte einen Grund oder eine Vorlage angeben.", Theme.Yellow); return false; }
            if ((act == "ban" || act == "ipban") && !Dialogs.Confirm(owner, "Bannen", name + " wirklich " + (act == "ipban" ? "per IP " : "") + "bannen?\n\nGrund: " + why +
                    (fDur.Box.Text.Trim().Length > 0 ? "\nDauer: " + fDur.Box.Text.Trim() : ti >= 0 ? "\nDauer: laut Vorlage" : "\nDauer: permanent"))) return false;
            setStatus(ActionNames[Array.IndexOf(Actions, act)].Split(' ')[0] + ": " + name + " …", Theme.Muted);
            var r = await mc.Call("act", new Dictionary<string, object> { { "action", act }, { "player", name }, { "duration", fDur.Box.Text.Trim() }, { "reason", why }, { "silent", cSilent.Checked } });
            if (r.Value != null) { setStatus("Nicht ausgeführt: " + r.Value, Theme.Red); return false; }
            if (!J.B(r.Key, "done")) { setStatus(J.S(r.Key, "message") ?? "Keine Bestätigung.", Theme.Yellow); return false; }
            var p = J.Get(r.Key, "punishment");
            setStatus(p != null ? ModClient.TypeName(J.S(p, "type"), J.N(p, "ip_ban") > 0) + " #" + J.S(p, "id") + " für " + J.S(p, "name") + " – " + ModClient.Until(p).Replace("–", "erteilt") + " · " + J.S(p, "reason")
                                : J.S(r.Key, "message"), Theme.Green);
            return true;
        }

        /// <summary>Lifts a ban or mute.</summary>
        public static async Task<bool> Revoke(IWin32Window owner, ModClient mc, string player, string type, Action<string, Color> setStatus)
        {
            var act = type == "ban" ? "unban" : "unmute";
            if (!mc.Allowed(act)) { setStatus("Dir fehlt das Recht dazu.", Theme.Red); return false; }
            var reason = Dialogs.Ask(owner, type == "ban" ? "Entbannen" : "Entmuten", player + " " + (type == "ban" ? "entbannen" : "entmuten") + " – Grund (optional):", "");
            if (reason == null) return false;
            var r = await mc.Call("act", new Dictionary<string, object> { { "action", act }, { "player", player }, { "reason", reason.Trim() } });
            if (r.Value != null) { setStatus("Nicht ausgeführt: " + r.Value, Theme.Red); return false; }
            setStatus(J.S(r.Key, "message") ?? "Erledigt.", J.B(r.Key, "done") ? Theme.Green : Theme.Yellow);
            return J.B(r.Key, "done");
        }
    }

    /// <summary>Player record: punishments, reports, Discord link – with quick actions.</summary>
    class PlayerFileForm : DarkForm
    {
        readonly ModClient mc;
        readonly string name;
        readonly Action<string, Color> setStatus;
        readonly Label head, info;
        readonly ListView list;
        readonly FlatBtn bPunish, bLift;

        public PlayerFileForm(ModClient mc, string name, Action<string, Color> setStatus)
        {
            this.mc = mc;
            this.name = name;
            this.setStatus = setStatus;
            Text = "Spieler-Akte – " + name;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(880, 560);
            MinimumSize = new Size(640, 420);
            Padding = new Padding(20, 16, 20, 16);
            head = Theme.Lbl(name, Theme.F(16f, FontStyle.Bold), Theme.Text, Theme.Bg);
            head.Dock = DockStyle.Top;
            info = Theme.Lbl("Lade …", Theme.F(9.25f), Theme.Text2, Theme.Bg);
            info.Dock = DockStyle.Top; info.AutoSize = false; info.Height = 64; info.Padding = new Padding(2, 6, 0, 0);
            bPunish = Ui.Btn("Bestrafen …", Theme.Accent);
            bLift = Ui.Btn("Markierte Strafe aufheben", Theme.CardHi);
            var bClose = Ui.Btn("Schließen", Theme.CardHi);
            var bar = Ui.Bar(46, bPunish, bLift, bClose);
            list = Ui.List(new[] { "Art", "#", "Grund", "Von", "Datum", "Dauer", "Status", "Server" }, new[] { 110, 58, 220, 110, 100, 110, 130, 80 });
            var card = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            card.Controls.Add(list);
            Controls.Add(card);
            Controls.Add(bar);
            Controls.Add(info);
            Controls.Add(head);
            bClose.Click += (s, e) => Close();
            bPunish.Click += async (s, e) => { if (await PunishDialog.Run(this, mc, name, "warn", "", setStatus)) await Load(); };
            bLift.Click += async (s, e) =>
            {
                var p = Ui.SelData<object>(list);
                if (p == null || J.N(p, "active") <= 0) { setStatus("Bitte eine aktive Strafe (Bann/Mute) markieren.", Theme.Yellow); return; }
                if (await PunishDialog.Revoke(this, mc, name, J.S(p, "type"), setStatus)) await Load();
            };
            bPunish.Visible = mc.CanPunish || mc.CanBan;
            Shown += async (s, e) => await Load();
        }

        async Task Load()
        {
            var r = await mc.Call("player", new Dictionary<string, object> { { "name", name } });
            if (r.Value != null) { info.Text = "Fehler: " + r.Value; info.ForeColor = Theme.Red; return; }
            var d = r.Key;
            var link = J.Get(d, "link");
            var puns = J.L(J.Get(d, "punishments")).ToList();
            var rep = J.L(J.Get(d, "reports_against")).ToList();
            head.Text = (J.S(d, "name") ?? name) + (J.N(d, "online") > 0 ? "   ● online auf " + J.S(d, "server") : "");
            info.Text = (J.S(d, "uuid") != null ? "Zuerst gesehen " + ModClient.When(J.S(d, "first_join")) + " · zuletzt " + ModClient.When(J.S(d, "last_seen")) + " · Spielzeit " + ModClient.Playtime(J.N(d, "playtime")) : "Noch nie im Netzwerk gesehen") +
                        "\nDiscord: " + (link != null ? "verknüpft seit " + ModClient.When(J.S(link, "linked")) + " · Ränge " + J.S(link, "groups") : "nicht verknüpft") +
                        "\nStrafen: " + puns.Count + " (" + puns.Count(p => J.N(p, "active") > 0) + " aktiv) · Reports gegen ihn: " + rep.Count + " · selbst gemeldet: " + (int)J.N(d, "reports_by");
            info.ForeColor = Theme.Text2;
            Ui.Fill(list, puns.Select(p => Ui.Row(new RowTag { Key = J.S(p, "id"), Data = p, Dot = ModClient.TypeColor(J.S(p, "type")) },
                ModClient.TypeName(J.S(p, "type"), J.N(p, "ip_ban") > 0), "#" + J.S(p, "id"), J.S(p, "reason"), J.S(p, "actor"), ModClient.When(J.S(p, "created")),
                ModClient.Until(p), ModClient.State(p), J.S(p, "server") ?? "–")).ToList());
        }
    }

    /// <summary>Moderation area: reports, punishments (RyvenBans) and Discord verification – with network-wide actions.</summary>
    class ModerationView : Panel
    {
        readonly ModClient mc;
        readonly Action<string, Color> setStatus;
        Label hSub, repTitle, repInfo, linkInfo;
        TabBtn[] tabs;
        Panel[] pages;
        ProgressLine progress;
        KpiCard kOpen, kBans, kMutes, kLinks;
        Segmented repFilter, punFilter;
        Field repSearch, punSearch, linkSearch;
        CheckBox punActive;
        ListView repList, punList, linkList;
        TextBox chat;
        FlatBtn bClaim, bDone, bReject, bRepPunish, bRepFile, bNew, bLift, bFile;
        readonly Timer poll = new Timer { Interval = 30000 };
        bool busy, loaded;
        object curReport;

        public ModerationView(ModClient mc, Action<string, Color> setStatus)
        {
            this.mc = mc;
            this.setStatus = setStatus;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
            poll.Tick += async (s, e) => { if (Visible && !busy) await Reload(false); };
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
            bNew.Visible = bRepPunish.Visible = bLift.Visible = mc.CanPunish || mc.CanBan;
            foreach (var b in new[] { bClaim, bDone, bReject }) b.Visible = mc.CanReports;
        }

        void SetTab(int i) { for (int k = 0; k < pages.Length; k++) { pages[k].Visible = k == i; tabs[k].Active = k == i; } }

        void Build()
        {
            var header = Ui.Header("Moderation", out hSub, new[] { "Reports", "Strafen", "Verify" }, out tabs, i => SetTab(i));
            bNew = new FlatBtn("+ Strafe vergeben", Theme.Accent) { Height = 34, Anchor = AnchorStyles.Top | AnchorStyles.Right }.Tip("Spieler verwarnen, kicken, muten oder bannen – netzwerkweit");
            var bRefresh = new FlatBtn("↻", Theme.CardHi) { Size = new Size(38, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right }.Tip("Neu laden");
            header.Controls.Add(bNew);
            header.Controls.Add(bRefresh);
            header.Resize += (s, e) => { bRefresh.Location = new Point(header.Width - bRefresh.Width - 2, 0); bNew.Location = new Point(bRefresh.Left - bNew.Width - 8, 0); };
            bNew.Visible = mc.CanPunish || mc.CanBan;
            bNew.Click += async (s, e) => { if (await PunishDialog.Run(FindForm(), mc, "", "warn", "", setStatus)) await Reload(false); };
            bRefresh.Click += async (s, e) => await Reload(true);

            var cards = new CardGrid { Dock = DockStyle.Top, MinCardWidth = 180, CardHeight = 110, Gap = 14 };
            kOpen = new KpiCard(Icons.Console, "Offene Reports", Theme.Red);
            kBans = new KpiCard(Icons.Block, "Aktive Banns", Theme.Mint);
            kMutes = new KpiCard(Icons.Lock, "Aktive Mutes", Theme.Yellow);
            kLinks = new KpiCard(Icons.People, "Discord verknüpft", Theme.SoftGreen);
            cards.Controls.AddRange(new Control[] { kOpen, kBans, kMutes, kLinks });

            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };
            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 4, 0, 0) };
            pages = new[] { BuildReports(), BuildPunishments(), BuildLinks() };
            foreach (var p in pages) content.Controls.Add(p);
            Controls.Add(content);
            Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Bg });
            Controls.Add(cards);
            Controls.Add(new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Theme.Bg });
            Controls.Add(progress);
            Controls.Add(header);
            SetTab(0);
        }

        Panel BuildReports()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            repFilter = new Segmented(new[] { "Offen", "Erledigt", "Abgelehnt", "Alle" }, 0) { Margin = new Padding(0, 1, 10, 8) };
            repSearch = new Field(false) { Width = 220, Height = 36 };
            repSearch.Placeholder = "Spieler oder Grund …";
            var bar = Ui.Bar(46, repFilter, repSearch);
            repFilter.SelectedChanged += async (s, e) => await LoadReports();
            repSearch.Box.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LoadReports(); } };

            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12 };
            split.Panel1.BackColor = split.Panel2.BackColor = Theme.Bg;
            UiX.Ratio(split, 0.58, 380, 320);
            repList = Ui.List(new[] { "Gemeldet", "#", "Von", "Grund", "Zeit", "Status" }, new[] { 125, 58, 100, 150, 80, 95 });
            var c1 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            c1.Controls.Add(repList);
            split.Panel1.Controls.Add(c1);

            var c2 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 14, 12) };
            repTitle = Theme.Lbl("Report links auswählen", Theme.F(12.5f, FontStyle.Bold), Theme.Text, Color.Transparent);
            repTitle.Dock = DockStyle.Top; repTitle.AutoSize = false; repTitle.Height = 32; repTitle.AutoEllipsis = true;
            repInfo = Theme.Lbl("", Theme.F(9f), Theme.Text2, Color.Transparent);
            repInfo.Dock = DockStyle.Top; repInfo.AutoSize = false; repInfo.Height = 70;
            bClaim = Ui.Btn("Übernehmen", Theme.CardHi, 34);
            bDone = Ui.Btn("Erledigt", Theme.Mix(Theme.Green, Theme.Card, 0.45), 34);
            bReject = Ui.Btn("Ablehnen", Theme.CardHi, 34);
            bRepPunish = Ui.Btn("Spieler bestrafen …", Theme.Accent, 34);
            bRepFile = Ui.Btn("Akte", Theme.CardHi, 34).Tip("Alle Strafen und Reports dieses Spielers");
            var actions = new WrapBar { Dock = DockStyle.Top, BackColor = Theme.Card, Padding = new Padding(0, 4, 0, 4) };
            foreach (var b in new[] { bClaim, bDone, bReject, bRepPunish, bRepFile }) { b.Margin = new Padding(0, 0, 8, 8); actions.Controls.Add(b); }
            var chatCap = Theme.Lbl("CHATVERLAUF (60 s VOR DER MELDUNG)", Theme.F(8f, FontStyle.Bold), Theme.Muted, Color.Transparent);
            chatCap.Dock = DockStyle.Top; chatCap.AutoSize = false; chatCap.Height = 24; chatCap.Padding = new Padding(0, 6, 0, 0);
            chat = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, BorderStyle = BorderStyle.None,
                                 BackColor = Theme.Input, ForeColor = Theme.Text2, Font = Theme.Mono, WordWrap = true };
            var chatHost = new RoundPanel { Dock = DockStyle.Fill, Fill = Theme.Input, Elevated = false, Flat = true, Padding = new Padding(10, 8, 6, 8) };
            chatHost.Controls.Add(chat);
            c2.Controls.Add(chatHost);
            c2.Controls.Add(chatCap);
            c2.Controls.Add(actions);
            c2.Controls.Add(repInfo);
            c2.Controls.Add(repTitle);
            split.Panel2.Controls.Add(c2);
            page.Controls.Add(split);
            page.Controls.Add(bar);

            foreach (var b in new[] { bClaim, bDone, bReject }) b.Visible = mc.CanReports;
            bRepPunish.Visible = mc.CanPunish || mc.CanBan;
            SetReportButtons(false);
            repList.SelectedIndexChanged += async (s, e) => { var r = Ui.SelData<object>(repList); if (r != null) await ShowReport(r); };
            bClaim.Click += async (s, e) => await SetReport("claimed");
            bDone.Click += async (s, e) => await SetReport("done");
            bReject.Click += async (s, e) => await SetReport("rejected");
            bRepPunish.Click += async (s, e) =>
            {
                if (curReport == null) return;
                if (await PunishDialog.Run(FindForm(), mc, J.S(curReport, "target"), "warn", J.S(curReport, "reason"), setStatus))
                {
                    if (mc.CanReports && J.S(curReport, "status") != "done" && Dialogs.Confirm(FindForm(), "Report", "Report #" + J.S(curReport, "id") + " als erledigt markieren?")) await SetReport("done");
                    else await Reload(false);
                }
            };
            bRepFile.Click += (s, e) => { if (curReport != null) OpenFile(J.S(curReport, "target")); };
            return page;
        }

        Panel BuildPunishments()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            punFilter = new Segmented(new[] { "Alle", "Banns", "Mutes", "Kicks", "Verwarnungen" }, 0) { Margin = new Padding(0, 1, 10, 8) };
            punActive = new CheckBox { Text = "nur aktive", ForeColor = Theme.Text2, BackColor = Theme.Bg, AutoSize = true, Margin = new Padding(0, 9, 12, 8), Cursor = Cursors.Hand };
            punSearch = new Field(false) { Width = 220, Height = 36 };
            punSearch.Placeholder = "Spieler, Grund, Teammitglied …";
            bLift = Ui.Btn("Aufheben", Theme.CardHi).Tip("Markierten aktiven Bann / Mute aufheben");
            bFile = Ui.Btn("Akte", Theme.CardHi).Tip("Alle Strafen und Reports des Spielers");
            var bar = Ui.Bar(46, punFilter, punActive, punSearch, bLift, bFile);
            punList = Ui.List(new[] { "Art", "#", "Spieler", "Grund", "Von", "Datum", "Dauer", "Status", "Server" }, new[] { 110, 58, 120, 230, 120, 95, 110, 120, 80 });
            var card = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            card.Controls.Add(punList);
            page.Controls.Add(card);
            page.Controls.Add(bar);
            punFilter.SelectedChanged += async (s, e) => await LoadPunishments();
            punActive.CheckedChanged += async (s, e) => await LoadPunishments();
            punSearch.Box.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LoadPunishments(); } };
            bLift.Visible = mc.CanPunish || mc.CanBan;
            bLift.Click += async (s, e) =>
            {
                var p = Ui.SelData<object>(punList);
                if (p == null || J.N(p, "active") <= 0) { setStatus("Bitte einen aktiven Bann oder Mute markieren.", Theme.Yellow); return; }
                if (await PunishDialog.Revoke(FindForm(), mc, J.S(p, "name"), J.S(p, "type"), setStatus)) await Reload(false);
            };
            bFile.Click += (s, e) => { var p = Ui.SelData<object>(punList); if (p != null) OpenFile(J.S(p, "name")); };
            punList.DoubleClick += (s, e) => { var p = Ui.SelData<object>(punList); if (p != null) OpenFile(J.S(p, "name")); };
            return page;
        }

        Panel BuildLinks()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            linkSearch = new Field(false) { Width = 240, Height = 36 };
            linkSearch.Placeholder = "Spieler oder Discord-ID …";
            linkInfo = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Theme.Bg);
            linkInfo.Margin = new Padding(0, 10, 0, 0);
            var bar = Ui.Bar(46, linkSearch, linkInfo);
            linkList = Ui.List(new[] { "Spieler", "Discord-ID", "Verknüpft", "Ränge", "Zuletzt online", "Spielzeit", "Discord-Rollen" }, new[] { 130, 160, 100, 200, 110, 80, 80 });
            var card = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            card.Controls.Add(linkList);
            page.Controls.Add(card);
            page.Controls.Add(bar);
            linkSearch.Box.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await LoadLinks(); } };
            linkList.DoubleClick += (s, e) => { var p = Ui.SelData<object>(linkList); if (p != null) OpenFile(J.S(p, "name")); };
            return page;
        }

        void OpenFile(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            using (var f = new PlayerFileForm(mc, name, setStatus)) f.ShowDialog(FindForm());
            var t = Reload(false);
        }

        // ───────────────────────────── loading ─────────────────────────────
        async Task Reload(bool showProgress)
        {
            if (busy) return;
            busy = true;
            if (showProgress) progress.Set(double.NaN);
            try
            {
                var o = await mc.Call("overview");
                if (o.Value != null) { hSub.Text = "Fehler: " + o.Value; hSub.ForeColor = Theme.Red; return; }
                var ov = o.Key;
                int open = (int)J.N(ov, "reports_open"), claimed = (int)J.N(ov, "reports_claimed");
                kOpen.Set(open.ToString(), claimed > 0 ? claimed + " in Bearbeitung" : "keine in Bearbeitung", open > 0 ? Theme.Red : Theme.Green);
                kBans.Set(((int)J.N(ov, "bans")).ToString(), "netzwerkweit gesperrt", Theme.Text);
                kMutes.Set(((int)J.N(ov, "mutes")).ToString(), (int)J.N(ov, "today") + " Strafen in 24 h", Theme.Text);
                kLinks.Set(((int)J.N(ov, "links")).ToString(), "+" + (int)J.N(ov, "links_today") + " in 24 h", Theme.Text);
                tabs[0].Text = open > 0 ? "Reports (" + open + ")" : "Reports";
                tabs[0].Width = Theme.Measure(tabs[0].Text, tabs[0].Font).Width + 30;
                hSub.ForeColor = Theme.Muted;
                hSub.Text = "Reports, Strafen und Discord-Verknüpfungen · Aktionen gelten netzwerkweit (RyvenBans) · Stand " + DateTime.Now.ToString("HH:mm:ss");
                await LoadReports();
                await LoadPunishments();
                await LoadLinks();
                loaded = true;
            }
            finally { busy = false; progress.Set(-1); }
        }

        async Task LoadReports()
        {
            var st = new[] { "active", "done", "rejected", "all" }[Math.Max(0, repFilter.SelectedIndex)];
            var r = await mc.Call("reports", new Dictionary<string, object> { { "status", st }, { "q", repSearch.Box.Text.Trim() }, { "limit", 400 } });
            if (r.Value != null) { setStatus("Reports: " + r.Value, Theme.Red); return; }
            var rows = J.L(J.Get(r.Key, "reports")).Select(x => Ui.Row(new RowTag { Key = J.S(x, "id"), Data = x, Dot = ModClient.StatusColor(J.S(x, "status")) },
                J.S(x, "target"), "#" + J.S(x, "id"), J.S(x, "reporter"), J.S(x, "reason"), ModClient.When(J.S(x, "created")),
                ModClient.StatusName(J.S(x, "status")) + (J.S(x, "status") == "claimed" && J.S(x, "handler") != null ? " · " + J.S(x, "handler") : ""))).ToList();
            foreach (var row in rows) row.SubItems[5].ForeColor = ModClient.StatusColor(J.S(((RowTag)row.Tag).Data, "status"));
            Ui.Fill(repList, rows);
            if (curReport != null)
            {
                var again = rows.Select(x => ((RowTag)x.Tag).Data).FirstOrDefault(x => J.S(x, "id") == J.S(curReport, "id"));
                if (again == null) { curReport = null; ClearReport(); }
            }
        }

        async Task LoadPunishments()
        {
            var t = new[] { "all", "ban", "mute", "kick", "warn" }[Math.Max(0, punFilter.SelectedIndex)];
            var r = await mc.Call("punishments", new Dictionary<string, object> { { "type", t }, { "active", punActive.Checked }, { "q", punSearch.Box.Text.Trim() }, { "limit", 500 } });
            if (r.Value != null) { setStatus("Strafen: " + r.Value, Theme.Red); return; }
            var rows = J.L(J.Get(r.Key, "punishments")).Select(p => Ui.Row(new RowTag { Key = J.S(p, "id"), Data = p, Dot = ModClient.TypeColor(J.S(p, "type")) },
                ModClient.TypeName(J.S(p, "type"), J.N(p, "ip_ban") > 0), "#" + J.S(p, "id"), J.S(p, "name"), J.S(p, "reason"), J.S(p, "actor"),
                ModClient.When(J.S(p, "created")), ModClient.Until(p), ModClient.State(p), J.S(p, "server") ?? "–")).ToList();
            foreach (var row in rows)
                if (ModClient.State(((RowTag)row.Tag).Data) == "aktiv") row.SubItems[7].ForeColor = Theme.Red;
            Ui.Fill(punList, rows);
            if (rows.Count == 0 && punSearch.Box.Text.Trim().Length == 0 && t == "all" && !punActive.Checked)
                setStatus("Noch keine Strafen vergeben.", Theme.Muted);
        }

        async Task LoadLinks()
        {
            var r = await mc.Call("links", new Dictionary<string, object> { { "q", linkSearch.Box.Text.Trim() }, { "limit", 1000 } });
            if (r.Value != null) { setStatus("Verify: " + r.Value, Theme.Red); return; }
            var list = J.L(J.Get(r.Key, "links")).ToList();
            Ui.Fill(linkList, list.Select(l => Ui.Row(new RowTag { Key = J.S(l, "uuid"), Data = l, Dot = J.N(l, "online") > 0 ? Theme.Green : Theme.Muted },
                J.S(l, "name"), J.S(l, "discord_id"), ModClient.When(J.S(l, "linked")), (J.S(l, "groups") ?? "").Replace(",", ", "),
                J.N(l, "online") > 0 ? "jetzt" : ModClient.When(J.S(l, "last_seen")), ModClient.Playtime(J.N(l, "playtime")),
                J.S(l, "groups") == J.S(l, "synced") ? "synchron" : "ausstehend")).ToList());
            linkInfo.Text = list.Count + " verknüpfte Konten · " + (int)J.N(r.Key, "pending_codes") + " offene /link-Codes · Doppelklick = Akte";
        }

        // ───────────────────────────── report detail ─────────────────────────────
        void SetReportButtons(bool on)
        {
            foreach (var b in new[] { bClaim, bDone, bReject, bRepPunish, bRepFile }) b.Enabled = on;
        }

        void ClearReport()
        {
            repTitle.Text = "Report links auswählen";
            repInfo.Text = "";
            chat.Text = "";
            SetReportButtons(false);
        }

        async Task ShowReport(object r)
        {
            curReport = r;
            repTitle.Text = "#" + J.S(r, "id") + " · " + J.S(r, "target");
            repInfo.Text = "Gemeldet von " + J.S(r, "reporter") + " · " + ModClient.When(J.S(r, "created")) + (J.S(r, "server") != null ? " · " + J.S(r, "server") : "") +
                           "\nGrund: " + J.S(r, "reason") + "\nStatus: " + ModClient.StatusName(J.S(r, "status")) + (J.S(r, "handler") != null ? " (" + J.S(r, "handler") + ")" : "");
            chat.Text = "Lade …";
            SetReportButtons(true);
            bClaim.Enabled = J.S(r, "status") == "open";
            var d = await mc.Call("report", new Dictionary<string, object> { { "id", J.S(r, "id") } });
            if (curReport != r) return;
            if (d.Value != null) { chat.Text = "Fehler: " + d.Value; return; }
            var log = J.S(d.Key, "chatlog");
            chat.Text = string.IsNullOrEmpty(log) ? "(kein Chatverlauf gespeichert)" : log.Replace("\r\n", "\n").Replace("\n", "\r\n");
            var puns = J.L(J.Get(d.Key, "target_punishments")).ToList();
            repInfo.Text += "\n" + J.S(r, "target") + ": " + (int)J.N(d.Key, "target_reports") + " Reports insgesamt · " + puns.Count + " Strafen" +
                            (puns.Count > 0 ? " (zuletzt " + ModClient.TypeName(J.S(puns[0], "type")) + ": " + J.S(puns[0], "reason") + ")" : "");
            repInfo.Height = Theme.Px(84);
        }

        async Task SetReport(string status)
        {
            if (curReport == null) return;
            var id = J.S(curReport, "id");
            var r = await mc.Call("report_set", new Dictionary<string, object> { { "id", id }, { "status", status } });
            if (r.Value != null) { setStatus("Report: " + r.Value, Theme.Red); return; }
            setStatus("Report #" + id + ": " + ModClient.StatusName(status) + (status == "done" || status == "rejected" ? " – der Melder bekommt ingame Bescheid." : "."), Theme.Green);
            await Reload(false);
            var again = repList.Items.Cast<ListViewItem>().FirstOrDefault(i => ((RowTag)i.Tag).Key == id);
            if (again != null) again.Selected = true; else { curReport = null; ClearReport(); }
        }
    }
}
