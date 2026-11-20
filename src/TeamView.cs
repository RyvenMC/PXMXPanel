using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>Owner area: install the team gateway, create username/password accounts (stored on the server), map ranks to panel rights,
    /// approve IPs, audit log.</summary>
    class TeamView : Panel
    {
        readonly Func<Ssh> getSsh;
        readonly Func<bool> sshReady;
        readonly Action<string, Color> setStatus;
        readonly Func<string> craftyToken;
        public Action<int> PendingChanged;
        public Func<FirewallView> GetFirewall;

        Label hSub;
        TabBtn[] tabs;
        Panel[] pages;
        Panel setup, body;
        ProgressLine progress;
        Field fPort;
        Label setupInfo;
        ListView rankList, accList, reqList, logList, apiList, sessList;
        Field fSearch;
        ComboBox cRes, cWho, cSpan;
        List<object> apiKeys = new List<object>();
        List<string> apiPerms = new List<string>();
        bool logFiltered;
        FlowLayoutPanel permBox;
        Label permTitle, addrLbl;
        TextBox blocked, tfaRanks;
        readonly Dictionary<string, CheckBox> permChecks = new Dictionary<string, CheckBox>();
        Dictionary<string, object> state;
        Dictionary<string, List<string>> roles = new Dictionary<string, List<string>>();
        string selRank, thumb;
        int port = 8444;
        bool busy, installed;
        /// <summary>Rechte-Kästchen wurden geändert, aber noch nicht gespeichert – das Neuladen darf sie dann nicht überschreiben.</summary>
        bool permsDirty, settingChecks;
        readonly Timer poll = new Timer { Interval = 20000 };

        const string Py = "python3 /opt/pxmx/gateway.py";

        public TeamView(Func<Ssh> getSsh, Func<bool> sshReady, Action<string, Color> setStatus, Func<string> craftyToken)
        {
            this.getSsh = getSsh;
            this.sshReady = sshReady;
            this.setStatus = setStatus;
            this.craftyToken = craftyToken;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
            // Nicht neu laden, solange ein Dialog (z. B. „Passwort zurücksetzen“) offen ist – sonst kollidiert das mit der Aktion.
            poll.Tick += async (s, e) => { if (Visible && !busy && Form.ActiveForm == FindForm()) await Reload(false); };
        }

        public async void Activate()
        {
            if (!sshReady())
            {
                hSub.Text = "Für den Team-Zugang braucht die App die SSH-Verbindung – richte sie einmal unter „Docker“ ein.";
                hSub.ForeColor = Theme.Yellow;
                return;
            }
            poll.Start();
            await Reload(true);
        }

        public void Deactivate() { poll.Stop(); }

        /// <summary>Quietly checks for pending IP requests (for the sidebar badge).</summary>
        public async Task CheckPending()
        {
            if (!sshReady()) return;
            var r = await getSsh().Run("[ -f /opt/pxmx/gateway.py ] && " + Py + " admin state 2>/dev/null || true", 30000);
            var d = Parse(r.Out);
            if (d != null && PendingChanged != null) PendingChanged(J.L(J.Get(d, "requests")).Count());
            if (d != null) await EnsureCurrent(d);
        }

        bool updateTried;

        /// <summary>"2.10" &gt; "2.9"; leere/kaputte Versionen gelten als 0.</summary>
        static int[] Teile(string v)
        {
            return Regex.Split(v ?? "", "[^0-9]+").Where(s => s.Length > 0).Select(s => { int n; return int.TryParse(s, out n) ? n : 0; }).ToArray();
        }

        static int VersionCompare(string a, string b)
        {
            var x = Teile(a);
            var y = Teile(b);
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int p = i < x.Length ? x[i] : 0, q = i < y.Length ? y[i] : 0;
                if (p != q) return p.CompareTo(q);
            }
            return 0;
        }

        /// <summary>The gateway ships inside this exe – if the server runs an older version, replace it (once per app start).</summary>
        async Task<bool> EnsureCurrent(Dictionary<string, object> st)
        {
            var want = Regex.Match(GatewaySource(), "^VERSION = \"([^\"]+)\"", RegexOptions.Multiline).Groups[1].Value;
            var have = J.S(st, "version") ?? "";
            // nur ältere Server-Versionen ersetzen – eine neuere (z. B. für das Web-Panel) niemals zurücksetzen
            if (updateTried || want.Length == 0 || VersionCompare(have, want) >= 0) return false;
            updateTried = true;
            var script =
                "set -e\ncd /opt/pxmx\n" +
                "printf '%s\\n' " + Ssh.B64(GatewaySource()) + " > gateway.py.new\n" +
                "python3 -m py_compile gateway.py.new\n" +
                "cp -p gateway.py gateway.py.v" + Regex.Replace(have, "[^0-9.]", "") + ".bak\n" +
                "printf '%s\\n' " + Ssh.B64(EcoSource()) + " > eco.py.new && mv eco.py.new eco.py\n" +
                "printf '%s\\n' " + Ssh.B64(ModSource()) + " > mod.py.new && python3 -m py_compile mod.py.new && mv mod.py.new mod.py\n" +
                "mv gateway.py.new gateway.py\n" +
                "systemctl restart pxmx-gateway\nsleep 2\nsystemctl is-active pxmx-gateway\n";
            var r = await getSsh().Run(script, 60000);
            if (r.Ok && r.Out.Contains("active"))
            {
                setStatus("Team-Dienst auf Version " + want + " aktualisiert (vorher " + (have.Length > 0 ? have : "?") + ").", Theme.Green);
                return true;
            }
            setStatus("Team-Dienst konnte nicht aktualisiert werden: " + r.Message, Theme.Red);
            return false;
        }

        // ───────────────────────────── Layout ─────────────────────────────
        void Build()
        {
            var header = Ui.Header("Team & Zugänge", out hSub, new[] { "Accounts", "Ränge & Rechte", "IP-Freigaben", "API-Schlüssel", "Sitzungen", "Protokoll", "Einstellungen" }, out tabs, i => SetTab(i));
            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };
            body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0), Visible = false };
            pages = new[] { BuildAccounts(), BuildRights(), BuildRequests(), BuildApi(), BuildSessions(), BuildLog(), BuildSettings() };
            foreach (var p in pages) body.Controls.Add(p);
            BuildSetup();
            Controls.Add(body);
            Controls.Add(setup);
            Controls.Add(progress);
            Controls.Add(header);
            SetTab(0);
        }

        void SetTab(int i) { for (int k = 0; k < pages.Length; k++) { pages[k].Visible = k == i; tabs[k].Active = k == i; } }

        void BuildSetup()
        {
            setup = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true, Visible = false, Padding = new Padding(0, 12, 0, 0) };
            var card = new RoundPanel { Location = new Point(0, 12), Size = new Size(760, 640), Padding = new Padding(26, 20, 26, 20) };
            setup.Controls.Add(card);
            setup.Resize += (s, e) => card.Width = Math.Max(Theme.Px(520), Math.Min(Theme.Px(820), setup.ClientSize.Width - 4));
            int y = 20;
            Action<string, Font, Color, int> text = (t, f, c, h) =>
            {
                var l = Theme.Lbl(t, f, c, Color.Transparent);
                l.Location = new Point(26, y); l.MaximumSize = new Size(700, 0);
                card.Controls.Add(l);
                y += h;
            };
            text("Team-Zugang einrichten", Theme.F(15f, FontStyle.Bold), Theme.Text, 40);
            text("Teammitglieder melden sich mit Benutzername und Passwort an. Die Accounts legst du hier an – gespeichert wird alles\n" +
                 "nur auf dem RyvenMC-Server (Passwörter verschlüsselt als Hash). Jeder Account wird an die IP seines ersten Logins\n" +
                 "gebunden – neue IPs musst du freigeben. Das Team bekommt nie deinen SSH-Schlüssel oder Crafty-Zugang;\n" +
                 "ein kleiner Dienst auf dem Server prüft jede Aktion.",
                 Theme.F(9.25f), Theme.Text2, 86);
            text("1.  Dienst installieren", Theme.F(10f, FontStyle.Bold), Theme.Text, 28);
            var l2 = Theme.Lbl("PORT", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Color.Transparent); l2.Location = new Point(26, y);
            fPort = new Field(false) { Location = new Point(26, y + 20), Size = new Size(100, 38) };
            fPort.Box.Text = "8444";
            card.Controls.AddRange(new Control[] { l2, fPort });
            y += 72;
            var bInstall = new FlatBtn("Team-Zugang installieren", Theme.Accent) { Location = new Point(26, y), Size = new Size(260, 42) };
            bInstall.Click += async (s, e) => await Install(fPort.Box.Text.Trim());
            card.Controls.Add(bInstall);
            y += 58;
            text("2.  Accounts anlegen", Theme.F(10f, FontStyle.Bold), Theme.Text, 24);
            text("Danach unter „Accounts“ für jedes Teammitglied Benutzername + Startpasswort vergeben. Rechte kommen entweder\n" +
                 "vom LuckPerms-Rang eines verknüpften Minecraft-Spielers oder von einem festen Rang. Beim ersten Login muss\n" +
                 "jedes Mitglied ein eigenes Passwort festlegen.",
                 Theme.F(9f), Theme.Muted, 60);
            setupInfo = Theme.Lbl("", Theme.F(9f), Theme.Muted, Color.Transparent);
            setupInfo.Location = new Point(26, y); setupInfo.MaximumSize = new Size(700, 0);
            card.Controls.Add(setupInfo);
            card.Height = y + 60;
        }

        Panel BuildRights()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12 };
            split.Panel1.BackColor = split.Panel2.BackColor = Theme.Bg;
            UiX.Ratio(split, 0.42, 280, 360);
            rankList = Ui.List(new[] { "Rang (LuckPerms)", "Gewicht", "Panel-Rechte" }, new[] { 160, 70, 200 });
            var c1 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            c1.Controls.Add(rankList);
            split.Panel1.Controls.Add(c1);

            var c2 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 14, 14, 12) };
            permTitle = Theme.Lbl("Rang links auswählen", Theme.F(12f, FontStyle.Bold), Theme.Text, Color.Transparent);
            permTitle.Dock = DockStyle.Top; permTitle.AutoSize = false; permTitle.Height = 34;
            var hint = Theme.Lbl("Ein Spieler bekommt die Rechte seines höchsten Rangs (nach Gewicht), für den hier etwas freigegeben ist.\n" +
                                 "Ränge ohne Haken haben keinen Zugang. Änderungen gelten sofort – angemeldete Mitglieder sehen neue Bereiche nach spätestens 1 Minute.",
                                 Theme.F(8.75f), Theme.Muted, Color.Transparent);
            hint.Dock = DockStyle.Top; hint.AutoSize = false; hint.Height = 42;
            permBox = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Card, Padding = new Padding(0, 8, 0, 0) };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, BackColor = Theme.Card, Padding = new Padding(0, 6, 0, 0) };
            var bSave = new FlatBtn("Rechte speichern", Theme.Accent) { Height = 34, Margin = new Padding(0, 0, 8, 0) };
            var bAll = new FlatBtn("Alles", Theme.CardHi) { Height = 34, Margin = new Padding(0, 0, 8, 0) };
            var bRead = new FlatBtn("Nur lesen", Theme.CardHi) { Height = 34, Margin = new Padding(0, 0, 8, 0) }.Tip("Server sehen + Konsole lesen");
            var bNone = new FlatBtn("Kein Zugang", Theme.CardHi) { Height = 34, Margin = new Padding(0, 0, 8, 0) };
            bar.Controls.AddRange(new Control[] { bSave, bAll, bRead, bNone });
            c2.Controls.Add(permBox);
            c2.Controls.Add(bar);
            c2.Controls.Add(hint);
            c2.Controls.Add(permTitle);
            split.Panel2.Controls.Add(c2);
            p.Controls.Add(split);

            rankList.SelectedIndexChanged += (s, e) =>
            {
                var r = Ui.Sel(rankList);
                if (r == null || (r == selRank && permsDirty)) return;   // Neuladen wählt denselben Rang neu aus → Änderungen behalten
                if (permsDirty && selRank != null && !Dialogs.Confirm(FindForm(), "Nicht gespeichert",
                        "Die Rechte für „" + selRank + "“ wurden geändert, aber nicht gespeichert.\n\nVerwerfen und zu „" + r + "“ wechseln?")) return;
                ShowRank(r);
            };
            Action<Func<string, bool>> setAll = f => { foreach (var kv in permChecks) kv.Value.Checked = f(kv.Key); permsDirty = true; };
            bAll.Click += (s, e) => setAll(k => true);
            bRead.Click += (s, e) => setAll(k => k == "servers.view" || k == "console.view");
            bNone.Click += (s, e) => setAll(k => false);
            bSave.Click += async (s, e) =>
            {
                if (selRank == null) return;
                roles[selRank] = permChecks.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToList();
                if (selRank == "owner" && !roles[selRank].Contains("team.manage") && !Dialogs.Confirm(FindForm(), "Achtung",
                        "Der Rang „owner“ verliert „team.manage“ – im Web-Panel kann dann niemand mehr das Team verwalten (dieses Fenster bleibt über SSH nutzbar).\n\nWirklich speichern?")) return;
                if (roles[selRank].Contains("console.command.unrestricted") && !Dialogs.Confirm(FindForm(), "Achtung",
                        "„Alle Konsolenbefehle“ erlaubt auch op, lp, stop usw. – damit kann sich der Rang selbst alle Rechte geben.\n\nWirklich freigeben?")) return;
                if (await Admin("roles", roles.Where(kv => kv.Value.Count > 0).ToDictionary(kv => kv.Key, kv => (object)kv.Value), "Rechte für „" + selRank + "“ gespeichert"))
                {
                    permsDirty = false;
                    ShowRank(selRank);
                }
            };
            return p;
        }

        Panel BuildAccounts()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var bNew = Ui.Btn("+ Account anlegen", Theme.Accent);
            var bEdit = Ui.Btn("Bearbeiten", Theme.CardHi).Tip("Verknüpften Spieler oder festen Rang ändern");
            var bPw = Ui.Btn("Passwort zurücksetzen", Theme.CardHi).Tip("Neues Startpasswort vergeben – das Mitglied muss es beim nächsten Login ändern");
            var bIp = Ui.Btn("IPs ▾", Theme.CardHi).Tip("Weitere IP freigeben oder eine entfernen");
            var bKick = Ui.Btn("Abmelden erzwingen", Theme.CardHi);
            var bTfa = Ui.Btn("2FA zurücksetzen", Theme.CardHi).Tip("Zwei-Faktor-Authentifizierung des Accounts löschen (z. B. Handy verloren); das Mitglied richtet sie neu ein");
            var bFreeze = Ui.Btn("Sperren / Entsperren", Theme.Mix(Theme.Red, Theme.Bg, 0.35));
            var bDel = Ui.Btn("Löschen", Theme.Mix(Theme.Red, Theme.Bg, 0.35));
            var bar = Ui.Bar(46, bNew, bEdit, bPw, bTfa, bIp, bKick, bFreeze, bDel);
            accList = Ui.List(new[] { "Benutzer", "Rechte von", "Rang", "Freigegebene IPs", "Letzter Login", "Status", "2FA" }, new[] { 130, 170, 100, 200, 130, 130, 70 });
            var c = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            c.Controls.Add(accList);
            p.Controls.Add(c);
            p.Controls.Add(bar);
            Func<object> sel = () => Ui.SelData<object>(accList);
            Func<object, string> name = a => J.S(a, "username");
            Func<object, string, Dictionary<string, object>> act = (a, action) => new Dictionary<string, object> { { "username", name(a) }, { "action", action } };

            bNew.Click += async (s, e) => await EditAccount(null);
            bEdit.Click += async (s, e) => { var a = sel(); if (a != null) await EditAccount(a); };
            accList.DoubleClick += async (s, e) => { var a = sel(); if (a != null) await EditAccount(a); };
            bPw.Click += async (s, e) =>
            {
                var a = sel(); if (a == null) return;
                var user = name(a);
                var pw = Dialogs.Ask(FindForm(), "Passwort zurücksetzen", "Neues Startpasswort für „" + user + "“ (beim nächsten Login muss ein eigenes gewählt werden):", NewPassword());
                if (pw == null) return;
                pw = pw.Trim();
                var problem = PasswordProblem(pw);
                if (problem != null) { MessageBox.Show(FindForm(), problem, "Passwort zurücksetzen", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
                var d = act(a, "password"); d["password"] = pw;
                if (await Admin("user", d, "Passwort von " + user + " zurückgesetzt", true)) ShowCredentials(user, pw);
            };
            bIp.Click += (s, e) =>
            {
                var a = sel(); if (a == null) return;
                var m = Ui.Menu();
                m.Item("+ Weitere IP freigeben …", async () =>
                {
                    var ip = Dialogs.Ask(FindForm(), "IP freigeben", "Zusätzliche IP für " + name(a) + ":", "");
                    if (ip == null || !Regex.IsMatch(ip.Trim(), @"^[0-9a-fA-F:.]{3,45}$")) return;
                    await Admin("ip", new Dictionary<string, object> { { "username", name(a) }, { "ip", ip.Trim() }, { "action", "add" } }, "IP freigegeben");
                });
                var ips = J.L(J.Parse(J.S(a, "ips") ?? "[]")).Select(Convert.ToString).ToList();
                if (ips.Count > 0) m.Sep();
                foreach (var ip in ips)
                {
                    var x = ip;
                    m.Item("Entfernen: " + x, async () => await Admin("ip", new Dictionary<string, object> { { "username", name(a) }, { "ip", x }, { "action", "remove" } }, "IP entfernt"));
                }
                Ui.DropDown(bIp, m);
            };
            bTfa.Click += async (s, e) =>
            {
                var a = sel(); if (a == null) return;
                if (J.N(a, "totp_on") <= 0) { MessageBox.Show(FindForm(), "Für „" + name(a) + "“ ist keine 2FA aktiv.", "2FA", MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
                if (!Dialogs.Confirm(FindForm(), "2FA zurücksetzen", "2FA von „" + name(a) + "“ löschen? Alle Sitzungen enden; die Person muss 2FA neu einrichten.")) return;
                await Admin("tfa", act(a, "reset"), "2FA von " + name(a) + " zurückgesetzt");
            };
            bKick.Click += async (s, e) => { var a = sel(); if (a != null) await Admin("user", act(a, "kick"), "Sitzung beendet"); };
            bFreeze.Click += async (s, e) =>
            {
                var a = sel(); if (a == null) return;
                bool frozen = J.N(a, "frozen") > 0 || J.N(a, "locked_until") > NowUnix();
                await Admin("user", act(a, frozen ? "unfreeze" : "freeze"), frozen ? "Account entsperrt" : "Account gesperrt");
            };
            bDel.Click += async (s, e) =>
            {
                var a = sel(); if (a == null) return;
                if (!Dialogs.Confirm(FindForm(), "Löschen", "Panel-Account „" + name(a) + "“ endgültig löschen?")) return;
                await Admin("user", act(a, "delete"), "Account gelöscht");
            };
            return p;
        }

        static double NowUnix() { return (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds; }

        /// <summary>Dieselben Regeln wie im Team-Dienst (pw_problem) – so kommt die Meldung sofort und verständlich.</summary>
        static string PasswordProblem(string pw)
        {
            if (string.IsNullOrEmpty(pw) || pw.Length < 8) return "Das Passwort muss mindestens 8 Zeichen lang sein.";
            if (pw.Length > 200) return "Das Passwort ist zu lang.";
            bool mixed = pw.ToLowerInvariant() != pw && pw.ToUpperInvariant() != pw && pw.Any(char.IsDigit);
            if (!mixed && pw.Length < 14) return "Das Passwort braucht Groß- und Kleinbuchstaben und eine Zahl (oder mindestens 14 Zeichen).";
            return null;
        }

        /// <summary>Random start password (no look-alike characters).</summary>
        static string NewPassword()
        {
            const string chars = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            var bytes = new byte[14];
            using (var rng = new System.Security.Cryptography.RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        void ShowCredentials(string user, string pw)
        {
            var text = "RyvenPanel – Team-Zugang\r\nTeam-Server: https://" + getSsh().Host + ":" + port +
                       (string.IsNullOrEmpty(thumb) ? "" : "\r\nZertifikat (SHA-1): " + thumb) +
                       "\r\nBenutzername: " + user + "\r\nStartpasswort: " + pw + "\r\n(Beim ersten Login ein eigenes Passwort festlegen.)";
            try { Clipboard.SetText(text); } catch { }
            MessageBox.Show(FindForm(), text + "\r\n\r\nDiese Zugangsdaten wurden in die Zwischenablage kopiert. Gib sie nur privat weiter.\r\n" +
                            "Das Passwort wird nirgends im Klartext gespeichert – es kann später nur zurückgesetzt werden.",
                "Zugangsdaten", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        /// <summary>Create (a == null) or edit an account: linked Minecraft player (LuckPerms rank) or a fixed rank.</summary>
        async Task EditAccount(object a)
        {
            const string fromLp = "– keiner (Rang aus LuckPerms) –";
            var ranks = new List<string> { fromLp };
            ranks.AddRange(roles.Where(kv => kv.Value.Count > 0).Select(kv => kv.Key).OrderBy(x => x));
            var groups = J.D(J.Get(state, "groups"));
            if (groups != null) ranks.AddRange(groups.Keys.Where(g => !ranks.Contains(g)).OrderBy(x => x));
            bool create = a == null;
            using (var d = new FormDialog(create ? "Team-Account anlegen" : "Account „" + J.S(a, "username") + "“ bearbeiten",
                "Rechte kommen entweder vom LuckPerms-Rang des verknüpften Minecraft-Spielers (Rangänderungen im Spiel wirken\n" +
                "automatisch) oder von einem festen Rang. Was jeder Rang darf, stellst du unter „Rechte“ ein."))
            {
                Field fName = null, fPw = null;
                if (create)
                {
                    fName = d.AddText("Benutzername (a–z, 0–9, _ . -)", "");
                    fPw = d.AddText("Startpasswort (muss beim ersten Login geändert werden)", NewPassword());
                }
                var fMc = d.AddText("Minecraft-Spieler verknüpfen (optional)", create ? "" : J.S(a, "mc_name") ?? "");
                var cRank = d.AddCombo("…oder fester Rang", ranks, create || string.IsNullOrEmpty(J.S(a, "role")) ? fromLp : J.S(a, "role"));
                d.AddNote("Ist ein Spieler eingetragen, gilt immer sein LuckPerms-Rang. Der Spieler muss einmal auf dem Server gewesen sein.", Theme.Muted);
                if (!d.Run(FindForm(), create ? "Anlegen" : "Speichern")) return;
                var mc = fMc.Box.Text.Trim();
                var role = cRank.SelectedItem as string == fromLp ? "" : (cRank.SelectedItem as string ?? "");
                if (mc.Length == 0 && role.Length == 0) { setStatus("Entweder einen Minecraft-Spieler verknüpfen oder einen festen Rang wählen.", Theme.Red); return; }
                if (role.Length > 0 && (!roles.ContainsKey(role) || roles[role].Count == 0))
                    setStatus("Hinweis: Rang „" + role + "“ hat noch keine Panel-Rechte – unter „Rechte“ freigeben.", Theme.Yellow);
                var data = new Dictionary<string, object> { { "action", create ? "create" : "update" }, { "mc_name", mc }, { "role", role } };
                if (create)
                {
                    var user = fName.Box.Text.Trim().ToLowerInvariant();
                    if (!Regex.IsMatch(user, @"^[a-z0-9_.\-]{3,32}$")) { setStatus("Benutzername: 3–32 Zeichen, nur a–z, 0–9, _ . -", Theme.Red); return; }
                    data["username"] = user;
                    data["password"] = fPw.Box.Text;
                    if (await Admin("user", data, "Account „" + user + "“ angelegt")) ShowCredentials(user, fPw.Box.Text);
                }
                else
                {
                    data["username"] = J.S(a, "username");
                    await Admin("user", data, "Account gespeichert");
                }
            }
        }

        Panel BuildRequests()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var bOk = Ui.Btn("✓ Freigeben", Theme.Mix(Theme.Green, Theme.Bg, 0.35));
            var bNo = Ui.Btn("✕ Ablehnen", Theme.Mix(Theme.Red, Theme.Bg, 0.35));
            var bFw = Ui.Btn("IP in Firewall sperren", Theme.DarkRed).Tip("Diese IP komplett vom Server aussperren");
            var info = Theme.Lbl("Versucht sich ein Account von einer neuen IP anzumelden, wird der Login verweigert und landet hier.", Theme.F(8.75f), Theme.Muted, Theme.Bg);
            info.Margin = new Padding(4, 10, 0, 0);
            var bar = Ui.Bar(46, bOk, bNo, bFw, info);
            reqList = Ui.List(new[] { "Zeitpunkt", "Benutzer", "Neue IP", "Ort / Provider", "Bisher erlaubt" }, new[] { 130, 150, 130, 220, 200 });
            var c = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            c.Controls.Add(reqList);
            p.Controls.Add(c);
            p.Controls.Add(bar);
            Func<string, Task> act = async a =>
            {
                var r = Ui.SelData<object>(reqList); if (r == null) return;
                await Admin("ip", new Dictionary<string, object> { { "username", J.S(r, "username") }, { "ip", J.S(r, "ip") }, { "action", a } }, a == "approve" ? "IP freigegeben" : "Anfrage abgelehnt");
            };
            bOk.Click += async (s, e) => await act("approve");
            bNo.Click += async (s, e) => await act("deny");
            bFw.Click += async (s, e) =>
            {
                var r = Ui.SelData<object>(reqList); if (r == null || GetFirewall == null) return;
                if (!Dialogs.Confirm(FindForm(), "Firewall", "IP " + J.S(r, "ip") + " komplett vom Server aussperren (auch Minecraft)?")) return;
                await act("deny");
                await GetFirewall().BlockIp(J.S(r, "ip"), "Panel-Login von " + J.S(r, "username") + " abgelehnt");
            };
            return p;
        }

        Panel BuildApi()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var bNew = Ui.Btn("+ Schlüssel erstellen", Theme.Accent);
            var bInfo = Ui.Btn("Rechte & IPs anzeigen", Theme.CardHi);
            var bRev = Ui.Btn("Sperren", Theme.Mix(Theme.Red, Theme.Bg, 0.35)).Tip("Schlüssel sofort widerrufen");
            var info = Theme.Lbl("Schlüssel gelten nur von den hinterlegten IPs und nie für „team.manage“. Der Klartext wird nur einmal beim Erstellen angezeigt.", Theme.F(8.75f), Theme.Muted, Theme.Bg);
            info.Margin = new Padding(4, 10, 0, 0);
            var bar = Ui.Bar(46, bNew, bInfo, bRev, info);
            apiList = Ui.List(new[] { "Name", "Präfix", "Scopes (Rechte)", "IPs", "Erstellt", "Läuft ab", "Zuletzt benutzt", "Nutzungen", "Status" }, new[] { 120, 90, 260, 140, 110, 110, 130, 80, 100 });
            var c = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            c.Controls.Add(apiList);
            p.Controls.Add(c);
            p.Controls.Add(bar);
            bNew.Click += async (s, e) => await CreateApiKey();
            Func<object> sel = () => Ui.SelData<object>(apiList);
            Action show = () =>
            {
                var k = sel(); if (k == null) return;
                MessageBox.Show(FindForm(), "Schlüssel „" + J.S(k, "name") + "“ (" + J.S(k, "prefix") + "…)\r\n\r\nIPs: " + string.Join(", ", J.L(J.Parse(J.S(k, "ips") ?? "[]")).Select(Convert.ToString)) +
                    "\r\nLetzte IP: " + (J.S(k, "last_ip") ?? "–") + "\r\nNotiz: " + (J.S(k, "note") ?? "") + "\r\n\r\nRechte:\r\n" +
                    string.Join("\r\n", J.L(J.Parse(J.S(k, "perms") ?? "[]")).Select(Convert.ToString).Select(x => "  • " + PermLabel(x))), "API-Schlüssel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            bInfo.Click += (s, e) => show();
            apiList.DoubleClick += (s, e) => show();
            bRev.Click += async (s, e) =>
            {
                var k = sel(); if (k == null) return;
                if (J.N(k, "revoked") > 0) { setStatus("Dieser Schlüssel ist schon gesperrt.", Theme.Yellow); return; }
                if (!Dialogs.Confirm(FindForm(), "Schlüssel sperren", "API-Schlüssel „" + J.S(k, "name") + "“ endgültig sperren? Folgeprojekte mit diesem Schlüssel verlieren sofort den Zugriff.")) return;
                await Admin("apikey", new Dictionary<string, object> { { "action", "revoke" }, { "name", J.S(k, "name") } }, "Schlüssel gesperrt");
            };
            return p;
        }

        string PermLabel(string key)
        {
            foreach (var p in J.L(J.Get(state, "perms"))) { var a = J.L(p).Select(Convert.ToString).ToArray(); if (a.Length > 1 && a[0] == key) return a[1] + " (" + key + ")"; }
            return key;
        }

        async Task CreateApiKey()
        {
            var perms = apiPerms.Count > 0 ? apiPerms : J.L(J.Get(state, "perms")).Select(x => J.L(x).Select(Convert.ToString).First()).Where(x => x != "team.manage").ToList();
            using (var d = new DarkForm())
            {
                d.Text = "API-Schlüssel erstellen";
                d.FormBorderStyle = FormBorderStyle.FixedDialog; d.MaximizeBox = d.MinimizeBox = false; d.ShowInTaskbar = false;
                d.StartPosition = FormStartPosition.CenterParent;
                d.ClientSize = new Size(500, 640);
                Func<string, int, Label> cap = (t, yy) => { var l = Theme.Lbl(t, Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Bg); l.Location = new Point(20, yy); d.Controls.Add(l); return l; };
                Func<string, int, string, Field> fld = (t, yy, v) => { cap(t, yy); var f = new Field(false) { Location = new Point(20, yy + 20), Size = new Size(460, 40) }; f.Box.Text = v; d.Controls.Add(f); return f; };
                var fName = fld("NAME (a–z, 0–9, _ -)", 14, "");
                var fIps = fld("ERLAUBTE IPs (kommagetrennt, öffentlich)", 72, "");
                var fDays = fld("GÜLTIG FÜR TAGE (0 = unbegrenzt)", 130, "0");
                var fNote = fld("NOTIZ (optional)", 188, "");
                cap("RECHTE", 246);
                var box = new FlowLayoutPanel { Location = new Point(20, 268), Size = new Size(460, 300), FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, BackColor = Theme.Card };
                var checks = new Dictionary<string, CheckBox>();
                foreach (var k in perms)
                {
                    var cb = new CheckBox { Text = PermLabel(k), UseMnemonic = false, AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Card, Margin = new Padding(6, 3, 0, 3) };
                    cb.Tip(k);
                    checks[k] = cb; box.Controls.Add(cb);
                }
                d.Controls.Add(box);
                var ok = new FlatBtn("Erstellen", Theme.Accent) { Location = new Point(360, 584), Size = new Size(120, 40), DialogResult = DialogResult.OK };
                var cancel = new FlatBtn("Abbrechen", Theme.CardHi) { Location = new Point(240, 584), Size = new Size(110, 40), DialogResult = DialogResult.Cancel };
                d.Controls.Add(ok); d.Controls.Add(cancel); d.AcceptButton = ok; d.CancelButton = cancel;
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;
                var name = fName.Box.Text.Trim().ToLowerInvariant();
                var ps = checks.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToList();
                var ips = fIps.Box.Text.Split(new[] { ',', ' ', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToList();
                int days; int.TryParse(fDays.Box.Text.Trim(), out days);
                if (!Regex.IsMatch(name, "^[a-z0-9_-]{3,32}$")) { setStatus("Name: 3–32 Zeichen, nur a–z, 0–9, _ und -.", Theme.Red); return; }
                if (ps.Count == 0) { setStatus("Bitte mindestens ein Recht wählen.", Theme.Red); return; }
                if (ips.Count == 0) { setStatus("Bitte mindestens eine IP angeben.", Theme.Red); return; }
                var token = await AdminData("apikey", new Dictionary<string, object> { { "action", "create" }, { "name", name }, { "perms", ps }, { "ips", ips }, { "tage", days }, { "note", fNote.Box.Text.Trim() } }, "Schlüssel „" + name + "“ erstellt", "token");
                if (token == null) return;
                try { Clipboard.SetText(token); } catch { }
                MessageBox.Show(FindForm(), "API-Schlüssel „" + name + "“:\r\n\r\n" + token + "\r\n\r\nWurde in die Zwischenablage kopiert. Er wird nur jetzt einmal angezeigt und ist danach nicht mehr abrufbar.",
                    "API-Schlüssel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        Panel BuildSessions()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var bOne = Ui.Btn("Sitzung beenden", Theme.CardHi);
            var bUser = Ui.Btn("Alle Sitzungen des Nutzers", Theme.CardHi).Tip("Meldet den Nutzer überall ab");
            var bAll = Ui.Btn("Alle beenden", Theme.Mix(Theme.Red, Theme.Bg, 0.35)).Tip("Alle angemeldeten Mitglieder abmelden");
            var bar = Ui.Bar(46, bOne, bUser, bAll);
            sessList = Ui.List(new[] { "Benutzer", "Rang", "IP", "Angemeldet seit", "Zuletzt aktiv", "Läuft ab" }, new[] { 140, 110, 200, 140, 140, 140 });
            var c = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            c.Controls.Add(sessList);
            p.Controls.Add(c);
            p.Controls.Add(bar);
            bOne.Click += async (s, e) =>
            {
                var x = Ui.SelData<object>(sessList); if (x == null) return;
                await Admin("session", new Dictionary<string, object> { { "action", "kick" }, { "hash", J.S(x, "token_hash") } }, "Sitzung von " + J.S(x, "username") + " beendet");
            };
            bUser.Click += async (s, e) =>
            {
                var x = Ui.SelData<object>(sessList); if (x == null) return;
                await Admin("user", new Dictionary<string, object> { { "username", J.S(x, "username") }, { "action", "kick" } }, J.S(x, "username") + " überall abgemeldet");
            };
            bAll.Click += async (s, e) =>
            {
                if (!Dialogs.Confirm(FindForm(), "Alle abmelden", "Wirklich ALLE Team-Sitzungen beenden? Alle müssen sich neu anmelden.")) return;
                await Admin("session", new Dictionary<string, object> { { "action", "kickall" } }, "Alle Sitzungen beendet");
            };
            return p;
        }

        Panel BuildLog()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            fSearch = new Field(false) { Size = new Size(240, 36), Margin = new Padding(0, 4, 8, 0), Placeholder = "Suche in Aktion, Details, IP" };
            cWho = new DarkCombo { Width = 150, Margin = new Padding(0, 6, 8, 0) };
            cRes = new DarkCombo { Width = 120, Margin = new Padding(0, 6, 8, 0) };
            cSpan = new DarkCombo { Width = 130, Margin = new Padding(0, 6, 8, 0) };
            cWho.Items.Add("Alle Nutzer"); cWho.SelectedIndex = 0;
            foreach (var x in new[] { "Alle Ergebnisse", "Nur OK", "Nur Fehler" }) cRes.Items.Add(x);
            cRes.SelectedIndex = 0;
            foreach (var x in new[] { "Gesamte Zeit", "Letzte 24 h", "Letzte 7 Tage" }) cSpan.Items.Add(x);
            cSpan.SelectedIndex = 0;
            var bGo = Ui.Btn("Suchen", Theme.Accent);
            var bReset = Ui.Btn("Zurücksetzen", Theme.CardHi);
            var bar = Ui.Bar(46, fSearch, cWho, cRes, cSpan, bGo, bReset);
            logList = Ui.List(new[] { "Zeit", "Wer", "IP", "Aktion", "Details", "Ergebnis" }, new[] { 130, 120, 120, 170, 300, 110 });
            var c = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            c.Controls.Add(logList);
            p.Controls.Add(c);
            p.Controls.Add(bar);
            Func<Task> go = async () =>
            {
                var d = new Dictionary<string, object> { { "q", fSearch.Box.Text.Trim() }, { "limit", 1000 }, { "result", cRes.SelectedIndex == 1 ? "ok" : cRes.SelectedIndex == 2 ? "fehler" : "" } };
                if (cWho.SelectedIndex > 0) d["who"] = cWho.SelectedItem as string;
                if (cSpan.SelectedIndex > 0) d["since"] = NowUnix() - (cSpan.SelectedIndex == 1 ? 86400 : 7 * 86400);
                logFiltered = fSearch.Box.Text.Trim().Length > 0 || cWho.SelectedIndex > 0 || cRes.SelectedIndex > 0 || cSpan.SelectedIndex > 0;
                var r = await Query("audit", d);
                if (r == null) return;
                FillLog(J.L(J.Get(r, "audit")));
                setStatus(logList.Items.Count + " Einträge" + (logFiltered ? " (gefiltert)" : "") + ".", Theme.Muted);
            };
            bGo.Click += async (s, e) => await go();
            fSearch.Box.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await go(); } };
            bReset.Click += (s, e) =>
            {
                fSearch.Box.Text = ""; cWho.SelectedIndex = 0; cRes.SelectedIndex = 0; cSpan.SelectedIndex = 0;
                logFiltered = false;
                FillLog(J.L(J.Get(state, "audit")));
            };
            return p;
        }

        void FillLog(IEnumerable<object> entries)
        {
            var lrows = new List<ListViewItem>();
            foreach (var l in entries)
            {
                var res = J.S(l, "result") ?? "";
                bool bad = res != "ok";
                var row = Ui.Row(new RowTag { Key = J.S(l, "id"), Dot = bad ? Theme.Red : Theme.Green, Data = bad },
                    When(J.N(l, "time")), J.S(l, "name"), J.S(l, "ip"), J.S(l, "action"), J.S(l, "detail"), res);
                if (bad) row.SubItems[5].ForeColor = Theme.Red;
                lrows.Add(row);
            }
            Ui.Fill(logList, lrows);
        }

        Panel BuildSettings()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            var card = new RoundPanel { Dock = DockStyle.Top, Height = 560, Padding = new Padding(22, 16, 22, 16) };
            int y = 16;
            Func<string, Label> cap = t => { var l = Theme.Lbl(t, Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Color.Transparent); l.Location = new Point(22, y); card.Controls.Add(l); y += 22; return l; };
            cap("ADRESSE FÜR DEIN TEAM (in der App unter „Team-Server“ eintragen)");
            addrLbl = Theme.Lbl("", Theme.Mono, Theme.SoftGreen, Color.Transparent);
            addrLbl.Location = new Point(22, y); card.Controls.Add(addrLbl);
            var bCopy = new FlatBtn("Kopieren", Theme.CardHi) { Location = new Point(560, y - 6), Size = new Size(100, 30), Font = Theme.F(8.5f, FontStyle.Bold) };
            bCopy.Click += (s, e) => { try { Clipboard.SetText(addrLbl.Text); } catch { } };
            card.Controls.Add(bCopy);
            y += 50;
            cap("GESPERRTE KONSOLENBEFEHLE (gelten für alle Ränge ohne „Alle Konsolenbefehle“) – einer pro Zeile");
            blocked = new TextBox { Location = new Point(22, y), Size = new Size(430, 200), Multiline = true, ScrollBars = ScrollBars.Vertical, BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.Mono };
            var bBlk = new FlatBtn("Speichern", Theme.Accent) { Location = new Point(462, y), Size = new Size(110, 34) };
            card.Controls.Add(blocked); card.Controls.Add(bBlk);
            y += 216;
            cap("2FA-PFLICHT FÜR RÄNGE (Komma-getrennt, z. B. owner, admin) – Mitglieder dieser Ränge müssen 2FA einrichten");
            tfaRanks = new TextBox { Location = new Point(22, y), Size = new Size(430, 26), BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = Theme.F(9.5f) };
            var bTfaSave = new FlatBtn("Speichern", Theme.Accent) { Location = new Point(462, y - 4), Size = new Size(110, 34) };
            card.Controls.Add(tfaRanks); card.Controls.Add(bTfaSave);
            bTfaSave.Click += async (s, e) =>
            {
                var rs = tfaRanks.Text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToArray();
                await Admin("config", new Dictionary<string, object> { { "tfa_required_ranks", rs } }, rs.Length == 0 ? "2FA-Pflicht aufgehoben" : "2FA-Pflicht für: " + string.Join(", ", rs));
            };
            y += 50;
            cap("DIENST");
            var bUpd = new FlatBtn("Aktualisieren / neu installieren", Theme.CardHi) { Location = new Point(22, y), Size = new Size(240, 34) };
            var bRestart = new FlatBtn("Neu starten", Theme.CardHi) { Location = new Point(270, y), Size = new Size(120, 34) };
            var bLog = new FlatBtn("Dienst-Log", Theme.CardHi) { Location = new Point(398, y), Size = new Size(120, 34) };
            card.Controls.AddRange(new Control[] { bUpd, bRestart, bLog });
            y += 56;
            cap("DIESES GERÄT (Owner-Gerätebindung)");
            var idLbl = Theme.Lbl("Geräte-ID: " + HardwareId.Short, Theme.Mono, Theme.SoftGreen, Color.Transparent);
            idLbl.Location = new Point(22, y); card.Controls.Add(idLbl);
            var bId = new FlatBtn("Kopieren", Theme.CardHi) { Location = new Point(270, y - 6), Size = new Size(100, 30), Font = Theme.F(8.5f, FontStyle.Bold) };
            bId.Click += (s, e) => { try { Clipboard.SetText(HardwareId.Compute()); } catch { } };
            card.Controls.Add(bId);
            y += 56;
            cap("NOTFALL-TUNNEL (SSH über Cloudflare Access)");
            var nfLbl = Theme.Lbl(Ssh.NotfallInfo(), Theme.Mono, Theme.SoftGreen, Color.Transparent);
            nfLbl.Location = new Point(22, y); card.Controls.Add(nfLbl);
            var bNf = new FlatBtn("Notfall-Tunnel testen", Theme.CardHi) { Location = new Point(22, y + 28), Size = new Size(200, 34) };
            bNf.Click += async (s, e) =>
            {
                var r = await getSsh().TestNotfall();
                nfLbl.Text = Ssh.NotfallInfo();
                var ok = r.Ok && (r.Out ?? "").Trim() == "ok";
                setStatus(ok ? "Notfall-Tunnel getestet: ok" : "Notfall-Tunnel Test fehlgeschlagen: " + r.Message, ok ? Theme.Green : Theme.Red);
            };
            card.Controls.Add(bNf);
            y += 40;
            card.Height = y + 60;
            p.Controls.Add(card);

            bBlk.Click += async (s, e) =>
            {
                var list = blocked.Text.Split(new[] { '\r', '\n', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim().TrimStart('/')).Where(x => x.Length > 0).Distinct().ToArray();
                await Admin("config", new Dictionary<string, object> { { "blocked_commands", list } }, "Sperrliste gespeichert (" + list.Length + " Befehle)");
            };
            bUpd.Click += async (s, e) => await Install(port.ToString());
            bRestart.Click += async (s, e) => { var r = await getSsh().Run("systemctl restart pxmx-gateway && sleep 1 && systemctl is-active pxmx-gateway", 30000); setStatus("Dienst: " + r.Out.Trim() + r.Err.Trim(), r.Ok ? Theme.Green : Theme.Red); };
            bLog.Click += async (s, e) =>
            {
                var r = await getSsh().Run("journalctl -u pxmx-gateway -n 40 --no-pager 2>&1", 30000);
                MessageBox.Show(FindForm(), r.Out.Length > 3000 ? r.Out.Substring(r.Out.Length - 3000) : r.Out, "Dienst-Log", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            return p;
        }

        // ───────────────────────────── Data ─────────────────────────────
        static Dictionary<string, object> Parse(string output)
        {
            foreach (var line in (output ?? "").Split('\n').Reverse())
            {
                var l = line.Trim();
                if (!l.StartsWith("{")) continue;
                try { return J.D(J.Parse(l)); } catch { }
            }
            return null;
        }

        async Task Reload(bool showProgress)
        {
            if (busy) return;
            busy = true;
            if (showProgress) progress.Set(double.NaN);
            try
            {
                var r = await getSsh().Run(
                    "if [ ! -f /opt/pxmx/gateway.py ]; then echo '@@NOTINSTALLED'; exit 0; fi\n" +
                    "echo \"@@ACTIVE $(systemctl is-active pxmx-gateway)\"\n" +
                    "echo \"@@THUMB $(openssl x509 -noout -fingerprint -sha1 -in /etc/pxmx/gw.crt 2>/dev/null | cut -d= -f2)\"\n" +
                    Py + " admin state", 45000);
                if (!r.Ok) { hSub.Text = "Fehler: " + r.Message; hSub.ForeColor = Theme.Red; return; }
                if (r.Out.Contains("@@NOTINSTALLED"))
                {
                    installed = false;
                    setup.Visible = true; body.Visible = false;
                    foreach (var t in tabs) t.Visible = false;
                    hSub.ForeColor = Theme.Muted;
                    hSub.Text = "Noch nicht eingerichtet – folge den drei Schritten.";
                    return;
                }
                installed = true;
                var active = Regex.Match(r.Out, @"@@ACTIVE (\S+)").Groups[1].Value;
                thumb = Regex.Match(r.Out, @"@@THUMB ([0-9A-F:]+)").Groups[1].Value;
                state = Parse(r.Out);
                if (state == null) { hSub.Text = "Antwort des Dienstes unlesbar: " + r.Message; hSub.ForeColor = Theme.Red; return; }
                setup.Visible = false; body.Visible = true;
                foreach (var t in tabs) t.Visible = true;
                port = (int)Math.Max(1, J.N(state, "port"));
                Render(active);
                await LoadApi();
            }
            finally { busy = false; progress.Set(-1); }
            if (state != null && !updateTried && await EnsureCurrent(state)) await Reload(false);
        }

        void Render(string active)
        {
            var host = getSsh().Host;
            var accounts = J.L(J.Get(state, "users")).ToList();
            var requests = J.L(J.Get(state, "requests")).ToList();
            var sessions = J.L(J.Get(state, "sessions")).ToList();
            hSub.ForeColor = active == "active" ? Theme.Muted : Theme.Yellow;
            hSub.Text = (active == "active" ? "Dienst läuft" : "Dienst: " + active) + "  ·  https://" + host + ":" + port + "  ·  " + accounts.Count + " Accounts, " +
                        sessions.Count + " angemeldet" + (requests.Count > 0 ? "  ·  " + requests.Count + " IP-Anfrage(n) offen" : "") +
                        (accounts.Count == 0 ? "  ·  noch keine Accounts – unter „Accounts“ anlegen" : "");
            tabs[2].Text = requests.Count > 0 ? "IP-Freigaben (" + requests.Count + ")" : "IP-Freigaben";
            tabs[2].Width = Theme.Measure(tabs[2].Text, tabs[2].Font).Width + 30;
            if (PendingChanged != null) PendingChanged(requests.Count);
            addrLbl.Text = "https://" + host + ":" + port + (string.IsNullOrEmpty(thumb) ? "" : "      SHA-1 " + thumb);
            if (!blocked.Focused) blocked.Text = string.Join("\r\n", J.L(J.Get(state, "blocked_commands")).Select(Convert.ToString));
            if (!tfaRanks.Focused) tfaRanks.Text = string.Join(", ", J.L(J.Get(state, "tfa_required_ranks")).Select(Convert.ToString));

            // ranks
            roles = new Dictionary<string, List<string>>();
            var rd = J.D(J.Get(state, "roles"));
            if (rd != null) foreach (var kv in rd) roles[kv.Key] = J.L(kv.Value).Select(Convert.ToString).ToList();
            var groups = J.D(J.Get(state, "groups")) ?? new Dictionary<string, object>();
            var rows = new List<ListViewItem>();
            foreach (var g in groups.OrderByDescending(x => J.N(groups, x.Key)).ThenBy(x => x.Key))
            {
                List<string> ps;
                roles.TryGetValue(g.Key, out ps);
                int n = ps == null ? 0 : ps.Count;
                var row = Ui.Row(new RowTag { Key = g.Key, Dot = n == 0 ? Theme.Muted : ps.Contains("console.command.unrestricted") || ps.Contains("files.edit") ? Theme.Red : Theme.Green },
                    g.Key, ((int)J.N(groups, g.Key)).ToString(), n == 0 ? "kein Zugang" : n == PermCount ? "alles" : n + " Rechte");
                row.SubItems[2].ForeColor = n == 0 ? Theme.Muted : Theme.Green;
                rows.Add(row);
            }
            Ui.Fill(rankList, rows);
            if (permChecks.Count == 0) BuildPermChecks();
            // Ungespeicherte Änderungen an den Kästchen nicht überschreiben (das Neuladen läuft alle 20 s im Hintergrund)
            if (selRank != null && !permsDirty) ShowRank(selRank);
            if (!string.IsNullOrEmpty(J.S(state, "groups_error"))) setStatus("LuckPerms: " + J.S(state, "groups_error"), Theme.Yellow);

            // accounts
            var online = new HashSet<string>(sessions.Select(s => J.S(s, "username")));
            var arows = new List<ListViewItem>();
            var now = NowUnix();
            foreach (var a in accounts)
            {
                var u = J.S(a, "username");
                bool frozen = J.N(a, "frozen") > 0, locked = J.N(a, "locked_until") > now, on = online.Contains(u), fresh = J.N(a, "last_login") <= 0;
                var ips = string.Join(", ", J.L(J.Parse(J.S(a, "ips") ?? "[]")).Select(Convert.ToString));
                var src = !string.IsNullOrEmpty(J.S(a, "mc_name")) ? "Spieler " + J.S(a, "mc_name") : "fester Rang " + J.S(a, "role");
                var stat = frozen ? "gesperrt" : locked ? "vorübergehend gesperrt" : on ? "angemeldet" : fresh ? "noch nie angemeldet" : J.N(a, "must_change") > 0 ? "Passwort zurückgesetzt" : "abgemeldet";
                var row = Ui.Row(new RowTag { Key = u, Data = a, Dot = frozen || locked ? Theme.Red : on ? Theme.Green : Theme.Muted },
                    u, src, string.IsNullOrEmpty(J.S(a, "rank")) ? "–" : J.S(a, "rank"), ips.Length > 0 ? ips : "(beim ersten Login)", When(J.N(a, "last_login")), stat, J.N(a, "totp_on") > 0 ? "aktiv" : "aus");
                row.SubItems[5].ForeColor = frozen || locked ? Theme.Red : on ? Theme.Green : Theme.Muted;
                row.SubItems[6].ForeColor = J.N(a, "totp_on") > 0 ? Theme.Green : Theme.Muted;
                arows.Add(row);
            }
            Ui.Fill(accList, arows);

            // requests
            var rrows = new List<ListViewItem>();
            foreach (var q in requests)
            {
                var acc = accounts.FirstOrDefault(a => J.S(a, "username") == J.S(q, "username"));
                var info = IpIntel.Get(J.S(q, "ip") ?? "");
                var row = Ui.Row(new RowTag { Key = J.S(q, "username") + J.S(q, "ip"), Data = q, Dot = Theme.Yellow },
                    When(J.N(q, "time")), J.S(q, "username"), J.S(q, "ip"),
                    info != null ? ((info.City ?? "") + " " + (info.CountryCode ?? "") + "  ·  " + (info.Org ?? info.Isp ?? "") + (info.Suspicious ? "  ⚠ " + info.Flags : "")).Trim() : "–",
                    acc != null ? string.Join(", ", J.L(J.Parse(J.S(acc, "ips") ?? "[]")).Select(Convert.ToString)) : "");
                rrows.Add(row);
            }
            Ui.Fill(reqList, rrows);

            // audit (eine gefilterte Ansicht nicht beim Neuladen überschreiben)
            if (!logFiltered) FillLog(J.L(J.Get(state, "audit")));
            var whoNames = accounts.Select(a => J.S(a, "username")).Concat(new[] { "Owner" }).Distinct().OrderBy(x => x).ToList();
            if (cWho.Items.Count - 1 != whoNames.Count)
            {
                var keep = cWho.SelectedIndex > 0 ? cWho.SelectedItem as string : null;
                cWho.Items.Clear(); cWho.Items.Add("Alle Nutzer");
                foreach (var w in whoNames) cWho.Items.Add(w);
                cWho.SelectedItem = keep ?? "Alle Nutzer";
            }

            // sessions
            var srows = new List<ListViewItem>();
            foreach (var x in sessions)
                srows.Add(Ui.Row(new RowTag { Key = J.S(x, "token_hash"), Data = x, Dot = Theme.Green },
                    J.S(x, "username"), string.IsNullOrEmpty(J.S(x, "rank")) ? "–" : J.S(x, "rank"), J.S(x, "ip"), When(J.N(x, "created")), When(J.N(x, "last_seen")), When(J.N(x, "expires"))));
            Ui.Fill(sessList, srows);
            tabs[4].Text = sessions.Count > 0 ? "Sitzungen (" + sessions.Count + ")" : "Sitzungen";
            tabs[4].Width = Theme.Measure(tabs[4].Text, tabs[4].Font).Width + 30;
        }

        void RenderApi()
        {
            var rows = new List<ListViewItem>();
            var now = NowUnix();
            foreach (var k in apiKeys)
            {
                bool rev = J.N(k, "revoked") > 0, exp = J.N(k, "expires") > 0 && J.N(k, "expires") < now;
                var ips = string.Join(", ", J.L(J.Parse(J.S(k, "ips") ?? "[]")).Select(Convert.ToString));
                var row = Ui.Row(new RowTag { Key = J.S(k, "name"), Data = k, Dot = rev || exp ? Theme.Red : Theme.Green },
                    J.S(k, "name"), (J.S(k, "prefix") ?? "") + "…", string.Join(", ", J.L(J.Parse(J.S(k, "perms") ?? "[]")).Select(Convert.ToString)), ips,
                    When(J.N(k, "created")), J.N(k, "expires") > 0 ? When(J.N(k, "expires")) : "nie", When(J.N(k, "last_used")), ((int)J.N(k, "uses")).ToString(),
                    rev ? "gesperrt" : exp ? "abgelaufen" : "aktiv");
                row.SubItems[8].ForeColor = rev || exp ? Theme.Red : Theme.Green;
                rows.Add(row);
            }
            Ui.Fill(apiList, rows);
        }

        /// <summary>Read-only admin query (no state change, no reload). Returns null and shows the error on failure.</summary>
        async Task<Dictionary<string, object>> Query(string cmd, object data)
        {
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(J.ToJson(data ?? new Dictionary<string, object>())));
            var r = await getSsh().Run("printf '%s' '" + b64 + "' | " + Py + " admin " + cmd + " -", 45000);
            var d = Parse(r.Out);
            if (!r.Ok || d == null || !J.B(d, "ok")) { setStatus("Fehler: " + (J.S(d, "error") ?? r.Message), Theme.Red); return null; }
            return d;
        }

        async Task LoadApi()
        {
            var d = await Query("apikeys", null);
            if (d == null) return;
            apiKeys = J.L(J.Get(d, "keys")).ToList();
            apiPerms = J.L(J.Get(d, "api_perms")).Select(Convert.ToString).ToList();
            RenderApi();
        }

        /// <summary>Like Admin, but returns one string field of the reply (e.g. a freshly created API key) or null on failure.</summary>
        async Task<string> AdminData(string cmd, object data, string okText, string field)
        {
            for (int i = 0; busy && i < 100; i++) await Task.Delay(100);
            if (busy) { setStatus("Fehler: Das Panel ist noch beschäftigt – bitte gleich noch einmal versuchen.", Theme.Red); return null; }
            busy = true;
            progress.Set(double.NaN);
            string val;
            try
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(J.ToJson(data)));
                var r = await getSsh().Run("printf '%s' '" + b64 + "' | " + Py + " admin " + cmd + " -", 45000);
                var d = Parse(r.Out);
                if (!r.Ok || d == null || !J.B(d, "ok")) { setStatus("Fehler: " + (J.S(d, "error") ?? (string.IsNullOrWhiteSpace(r.Message) ? "Keine Antwort vom Server." : r.Message)), Theme.Red); return null; }
                setStatus(okText, Theme.Green);
                val = J.S(d, field);
            }
            finally { busy = false; progress.Set(-1); }
            await Reload(false);
            return val;
        }

        int PermCount { get { return J.L(J.Get(state, "perms")).Count(); } }

        static string GroupOf(string key)
        {
            var p = key.Split('.')[0];
            switch (p)
            {
                case "servers": case "server": return "Server";
                case "console": return "Konsole";
                case "files": return "Dateien";
                case "economy": return "Wirtschaft";
                case "moderation": return "Moderation";
                case "datenschutz": return "Datenschutz";
                case "netzwerk": return "Netzwerk";
                case "users": return "Nutzer & Ränge";
                case "team": return "Team";
                case "ki": return "KI";
                default: return p;
            }
        }

        void BuildPermChecks()
        {
            permBox.Controls.Clear();
            permChecks.Clear();
            string lastGrp = null;
            foreach (var p in J.L(J.Get(state, "perms")))
            {
                var arr = J.L(p).Select(Convert.ToString).ToArray();
                if (arr.Length < 2) continue;
                var grp = GroupOf(arr[0]);
                if (grp != lastGrp)
                {
                    lastGrp = grp;
                    var h = Theme.Lbl(grp.ToUpperInvariant() + "   (anklicken: Gruppe an/aus)", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Card);
                    h.Margin = new Padding(0, 10, 0, 2); h.Cursor = Cursors.Hand;
                    var g = grp;
                    h.Click += (s, e) =>
                    {
                        var m = permChecks.Where(kv => GroupOf(kv.Key) == g && kv.Value.Enabled).Select(kv => kv.Value).ToList();
                        bool on = m.Any(c => !c.Checked);
                        foreach (var c in m) c.Checked = on;
                    };
                    permBox.Controls.Add(h);
                    DarkForm.ScaleLate(h);
                }
                var cb =new CheckBox { Text = arr[1], UseMnemonic = false, AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Card, Font = Theme.F(10f), Margin = new Padding(0, 4, 0, 6), Enabled = false };
                if (arr[0] == "console.command.unrestricted" || arr[0] == "files.edit") cb.ForeColor = Theme.Mix(Theme.Text, Theme.Red, 0.35);
                cb.Tip(arr[0]);
                cb.CheckedChanged += (s, e) => { if (!settingChecks) permsDirty = true; };
                permChecks[arr[0]] = cb;
                permBox.Controls.Add(cb);
                DarkForm.ScaleLate(cb);
            }
        }

        void ShowRank(string rank)
        {
            selRank = rank;
            permTitle.Text = "Panel-Rechte für Rang „" + rank + "“";
            List<string> ps;
            roles.TryGetValue(rank, out ps);
            settingChecks = true;
            try { foreach (var kv in permChecks) { kv.Value.Enabled = true; kv.Value.Checked = ps != null && ps.Contains(kv.Key); } }
            finally { settingChecks = false; }
            permsDirty = false;
        }

        static string When(double unix)
        {
            if (unix <= 0) return "–";
            var d = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(unix).ToLocalTime();
            return d.ToString("dd.MM.yy HH:mm:ss");
        }

        /// <summary>Runs an owner command on the server. The payload goes through stdin (printf is a shell builtin), so passwords never show up in the process list.</summary>
        async Task<bool> Admin(string cmd, object data, string okText, bool fehlerAlsDialog = false)
        {
            // Läuft gerade ein Neuladen im Hintergrund, kurz warten statt die Aktion stillschweigend zu verwerfen
            for (int i = 0; busy && i < 100; i++) await Task.Delay(100);
            if (busy) { setStatus("Fehler: Das Panel ist noch beschäftigt – bitte gleich noch einmal versuchen.", Theme.Red); return false; }
            busy = true;
            progress.Set(double.NaN);
            bool ok = false;
            try
            {
                var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(J.ToJson(data)));
                var r = await getSsh().Run("printf '%s' '" + b64 + "' | " + Py + " admin " + cmd + " -", 45000);
                var d = Parse(r.Out);
                if (!r.Ok || d == null || !J.B(d, "ok"))
                {
                    var fehler = J.S(d, "error") ?? (string.IsNullOrWhiteSpace(r.Message) ? "Keine Antwort vom Server." : r.Message);
                    setStatus("Fehler: " + fehler, Theme.Red);
                    if (fehlerAlsDialog) MessageBox.Show(FindForm(), fehler, "Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                setStatus(okText, Theme.Green);
                ok = true;
            }
            finally { busy = false; progress.Set(-1); }
            await Reload(false);
            return ok;
        }

        // ───────────────────────────── Install ─────────────────────────────
        static string GatewaySource()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("gateway.py"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        static string EcoSource()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("eco.py"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        public static string ModSource()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("mod.py"))
            using (var r = new StreamReader(s, Encoding.UTF8)) return r.ReadToEnd();
        }

        async Task Install(string portText)
        {
            int p;
            if (!int.TryParse(portText, out p) || p < 1024 || p > 65535 || p == 8443 || p == 25565) { setStatus("Ungültiger Port (1024–65535, nicht 8443/25565).", Theme.Red); return; }
            var token = craftyToken();
            if (string.IsNullOrEmpty(token)) { setStatus("Kein Crafty-Token – bitte als Owner über Crafty anmelden.", Theme.Red); return; }
            if (!Dialogs.Confirm(FindForm(), "Team-Zugang", "Der Team-Dienst wird auf dem Server installiert (Port " + p + ", Systemdienst „pxmx-gateway“).\n\n" +
                    "Er nutzt deinen Crafty-Zugang stellvertretend – gibt ihn aber nie an das Team weiter.\n\nFortfahren?")) return;
            busy = true;
            progress.Set(double.NaN);
            setStatus("Team-Zugang wird installiert …", Theme.Muted);
            try
            {
                var unit = "[Unit]\nDescription=RyvenPanel Team-Gateway\nAfter=network-online.target docker.service\n\n[Service]\nExecStart=/usr/bin/python3 /opt/pxmx/gateway.py\n" +
                           "Restart=always\nRestartSec=3\n\n[Install]\nWantedBy=multi-user.target\n";
                var script =
                    "set -e\nmkdir -p /opt/pxmx /etc/pxmx /var/lib/pxmx\nchmod 700 /etc/pxmx /var/lib/pxmx\n" +
                    "printf '%s\\n' " + Ssh.B64(GatewaySource()) + " > /opt/pxmx/gateway.py\n" +
                    "python3 -m py_compile /opt/pxmx/gateway.py\n" +
                    "printf '%s\\n' " + Ssh.B64(EcoSource()) + " > /opt/pxmx/eco.py\n" +
                    "printf '%s\\n' " + Ssh.B64(ModSource()) + " > /opt/pxmx/mod.py\n" +
                    "[ -f /etc/pxmx/gw.key ] || openssl req -x509 -newkey rsa:2048 -nodes -keyout /etc/pxmx/gw.key -out /etc/pxmx/gw.crt -days 3650 -subj '/CN=RyvenPanel Team' >/dev/null 2>&1\n" +
                    "chmod 600 /etc/pxmx/gw.key\n" +
                    "LP=$(for d in /opt/crafty/docker/servers/*/; do [ -f \"$d/plugins/LuckPerms/config.yml\" ] && [ ! -f \"$d/velocity.toml\" ] && echo \"$d/plugins/LuckPerms/config.yml\"; done | head -1)\n" +
                    "[ -n \"$LP\" ] || { echo 'Kein Minecraft-Server mit LuckPerms gefunden.' >&2; exit 4; }\n" +
                    "RP_CRAFTY_TOKEN=" + Ssh.B64(token) + " RP_LP_CONFIG=\"$LP\" RP_PORT=" + p + " python3 /opt/pxmx/gateway.py --init\n" +
                    "printf '%s' " + Ssh.B64(unit) + " > /etc/systemd/system/pxmx-gateway.service\n" +
                    "systemctl daemon-reload\nsystemctl enable pxmx-gateway >/dev/null 2>&1\nsystemctl restart pxmx-gateway\n" +
                    "if [ -f /etc/pxmx/firewall.conf ] && grep -qx 'mode strict' /etc/pxmx/firewall.conf && ! grep -q '^allow tcp " + p + " ' /etc/pxmx/firewall.conf; then\n" +
                    "  echo 'allow tcp " + p + " any # RyvenPanel Team-Zugang' >> /etc/pxmx/firewall.conf; /usr/local/sbin/pxmx-fw apply >/dev/null; echo FIREWALL_OPENED; fi\n" +
                    "sleep 2\nsystemctl is-active pxmx-gateway\n";
                var r = await getSsh().Run(script, 120000);
                if (!r.Ok || !r.Out.Contains("active"))
                {
                    var log = await getSsh().Run("journalctl -u pxmx-gateway -n 15 --no-pager 2>&1", 20000);
                    setStatus("Installation fehlgeschlagen: " + r.Message, Theme.Red);
                    if (setupInfo != null) { setupInfo.ForeColor = Theme.Red; setupInfo.Text = "Fehler: " + r.Message + "\n" + log.Out.Trim(); }
                    return;
                }
                setStatus("Team-Zugang läuft auf Port " + p + (r.Out.Contains("FIREWALL_OPENED") ? " (Firewall-Port geöffnet)" : "") + ".", Theme.Green);
            }
            finally { busy = false; progress.Set(-1); }
            await Reload(true);
        }
    }
}
