using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    class MainForm : DarkForm
    {
        readonly Api api;
        readonly Settings st;
        readonly string user;
        readonly List<ServerItem> servers = new List<ServerItem>();
        ServerItem cur;
        bool closingForLogin;

        // layout
        DockerView docker;
        PlayerView players;
        Panel craftyPanel;
        NavItem[] nav;
        TerminalView terminal;
        FilesView files;
        FirewallView firewall;
        UserView users;
        EconomyView economy;
        FinanzenView finanzen;
        ModerationView moderation;
        DatenschutzView datenschutz;
        NetzView netz;
        KiView ki;
        ModClient modClient;
        int mode;
        bool dockerMode { get { return mode != 0; } }
        TabBtn tabMetrics, tabAdmin;
        Panel pageMetrics, pageAdmin;
        LineChart chPlayers, chCpu, chRam;
        Label metricsInfo;
        ProgressLine fileProgress;
        ListBox admBans, admIpBans, admOps, admWhite;
        readonly Timer pulseTimer = new Timer { Interval = 40 };
        double pulse;
        ServerList list;
        Label summary, status, hName, hSub;
        Pill hPill;
        TabBtn tabOverview, tabConsole, tabFiles;
        Panel pageOverview, pageConsole, pageFiles;

        // overview
        StatCard cStatus, cCpu, cRam, cPlayers, cVersion, cWorld;
        FlatBtn bStart, bStop, bRestart, bKill, bBackup;
        ListBox playerList;
        Label playersTitle;

        // console
        RichTextBox console;
        Field cmdField;
        CheckBox autoScroll;
        List<string> logPrev = new List<string>();
        readonly List<string> history = new List<string>();
        int historyPos;
        bool logBusy, logLoaded;

        // files
        ListView fileList;
        Label pathLabel, editorTitle;
        TextBox editor;
        FlatBtn bSave, bReload;
        string curDir = "";
        string openPath;
        object openEpoch;
        bool openCrlf, dirty, suppressDirty;

        readonly Timer statsTimer = new Timer { Interval = 3000 };
        readonly Timer logTimer = new Timer { Interval = 1200 };
        bool statsBusy;

        readonly TeamSession team;
        TeamView teamView;

        /// <summary>Owner (team == null) may do everything; team members only what their rank allows.</summary>
        bool Can(string perm) { return team == null || (perm != "#owner" && team.Has(perm)); }

        /// <summary>Shows a control only if the current user is allowed to use it (also re-shows it when a right was granted later).</summary>
        T Need<T>(T c, string perm) where T : Control { c.Visible = Can(perm); return c; }

        readonly Timer permTimer = new Timer { Interval = 60000 };
        readonly Timer updateTimer = new Timer { Interval = 3 * 3600 * 1000 };
        DateTime permChecked = DateTime.MinValue;
        bool permBusy;
        Label whoLbl, unameLbl;

        // gateway outage banner + auto reconnect (5s -> 60s exponential backoff)
        readonly Timer reconnectTimer = new Timer { Interval = 1000 };
        Panel connBanner;
        Label connLbl;
        int reconnectDelay = 5, reconnectLeft;
        bool reconnectBusy;

        void OnConnState(Api.ConnState c)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke(new Action(() => ApplyConnState(c))); } catch { }
        }

        void EnsureBanner()
        {
            if (connBanner != null) return;
            connBanner = new Panel { Dock = DockStyle.Top, Height = 34, BackColor = Color.FromArgb(120, 60, 20), Visible = false };
            var btn = new Button { Text = "Jetzt erneut versuchen", Dock = DockStyle.Right, Width = 190, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = Color.FromArgb(160, 90, 30), Cursor = Cursors.Hand };
            btn.FlatAppearance.BorderSize = 0;
            btn.Click += async (s, e) => { reconnectLeft = 1; await ReconnectTick(); };
            connLbl = new Label { Dock = DockStyle.Fill, ForeColor = Color.White, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(14, 0, 0, 0), AutoEllipsis = true };
            connBanner.Controls.Add(connLbl);
            connBanner.Controls.Add(btn);
            Controls.Add(connBanner);
        }

        void ApplyConnState(Api.ConnState c)
        {
            if (closingForLogin || IsDisposed) return;
            if (c == Api.ConnState.Disrupted)
            {
                EnsureBanner();
                if (!connBanner.Visible)
                {
                    reconnectDelay = 5; reconnectLeft = reconnectDelay;
                    connBanner.Visible = true;
                    reconnectTimer.Start();
                }
                UpdateBannerText();
            }
            else if (c == Api.ConnState.Online)
            {
                reconnectTimer.Stop();
                if (connBanner != null && connBanner.Visible)
                {
                    connBanner.Visible = false;
                    SetStatus("Verbindung zum Server wiederhergestellt.", Theme.Green);
                }
            }
            else reconnectTimer.Stop(); // session expired: api.Unauthorized sends the user back to login
        }

        void UpdateBannerText()
        {
            if (connLbl != null)
                connLbl.Text = reconnectBusy || reconnectLeft <= 0 ? "Verbindung zum Server unterbrochen – Versuch läuft …"
                    : "Verbindung zum Server unterbrochen – neuer Versuch in " + reconnectLeft + "s …";
        }

        async Task ReconnectTick()
        {
            if (reconnectBusy || closingForLogin || IsDisposed) return;
            if (--reconnectLeft > 0) { UpdateBannerText(); return; }
            reconnectBusy = true;
            UpdateBannerText();
            try
            {
                await api.Get("/panel/me"); // success flips Api.State to Online and hides the banner
                if (Api.State == Api.ConnState.Disrupted)
                {
                    reconnectDelay = Math.Min(60, reconnectDelay * 2);
                    reconnectLeft = reconnectDelay;
                }
            }
            finally { reconnectBusy = false; UpdateBannerText(); }
        }

        public MainForm(Api api, Settings st, string user, TeamSession team = null)
        {
            this.api = api;
            this.st = st;
            this.user = user;
            this.team = team;
            api.Unauthorized += () => BeginInvoke(new Action(SessionExpired));
            if (team != null)
            {
                Api.StateChanged += OnConnState;
                reconnectTimer.Tick += async (s, e) => await ReconnectTick();
                Disposed += (s, e) => { Api.StateChanged -= OnConnState; reconnectTimer.Stop(); };
            }

            Text = "PXMX";
            ClientSize = new Size(1320, 820);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;

            BuildLayout();
            statsTimer.Tick += async (s, e) => await RefreshStats();
            logTimer.Tick += async (s, e) => await PollLog();
            Shown += async (s, e) =>
            {
                if (team == null && docker.Ssh.KeyExists)
                {
                    var dev = await HardwareId.CheckOwner(docker.Ssh);
                    if (dev == false) { HardwareId.ShowBlocked(this); dirty = false; Close(); return; }
                    if (dev == null) SetStatus("Owner-Gerätebindung nicht aktiv.", Theme.Muted);
                }
                await LoadServers();
                if (team == null && docker.Ssh.KeyExists) await teamView.CheckPending();
            };
            if (team != null)
            {
                // Rights changed by the owner apply without logging in again: poll the gateway, and check right away
                // when the window comes back to the front.
                permTimer.Tick += async (s, e) => await RefreshTeamPerms();
                permTimer.Start();
                Activated += async (s, e) => { if ((DateTime.Now - permChecked).TotalSeconds > 15) await RefreshTeamPerms(); };
            }
            FormClosing += OnClosingCheck;
        }

        // ───────────────────────────── Layout ─────────────────────────────
        void BuildLayout()
        {
            var main = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(24, 16, 24, 8) };
            var side = new Panel { Dock = DockStyle.Left, Width = 228, BackColor = Theme.Side };
            var bar = new Panel { Dock = DockStyle.Bottom, Height = 28, BackColor = Theme.Side, Padding = new Padding(14, 0, 14, 0) };
            Controls.Add(main);
            Controls.Add(side);
            Controls.Add(bar);

            // status bar
            status = Theme.Lbl("Bereit", Theme.F(9f), Theme.Muted, Theme.Side);
            status.Dock = DockStyle.Fill;
            status.AutoSize = false;
            status.AutoEllipsis = true;
            status.TextAlign = ContentAlignment.MiddleLeft;
            var who = whoLbl = Theme.Lbl(team != null ? "Team-Zugang  ·  Rang " + team.Rank : (api.DisplayUrl ?? api.BaseUrl).Replace("https://", ""), Theme.F(9f), team != null ? Theme.SoftGreen : Theme.Muted, Theme.Side);
            who.Dock = DockStyle.Right;
            who.AutoSize = false;
            who.Width = 220;
            who.TextAlign = ContentAlignment.MiddleRight;
            bar.Controls.Add(status);
            bar.Controls.Add(who);
            Ssh.NotfallActivated += m => { try { BeginInvoke(new Action(() => SetStatus(m, Theme.Yellow))); } catch { } };

            // ── sidebar: logo, search, categorised navigation, account ──
            var sideTop = new Panel { Dock = DockStyle.Top, Height = 66, BackColor = Theme.Side };
            var logo = new GradientText("PXMX", Theme.F(19f, FontStyle.Bold)) { Location = new Point(16, 10), BackColor = Theme.Side };
            var tag = Theme.Lbl("CONTROL PANEL  ·  v" + AppInfo.Version,Theme.F(7.5f, FontStyle.Bold), Theme.Muted, Theme.Side);
            tag.Location = new Point(20, 46);
            sideTop.Controls.AddRange(new Control[] { logo, tag });

            var searchBtn = new SearchButton { Dock = DockStyle.Top, Height = 36 }.Tip("Alles durchsuchen: Bereiche, Aktionen, Spieler, Server  (Strg+K)");
            searchBtn.Click += (s, e) => OpenPalette("");

            var navPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Side, AutoScroll = true };
            var navDefs = new[]
            {
                new object[] { "MINECRAFT", null, null, null },
                new object[] { null, 0, Icons.Server, "Server-Steuerung", "Starten, Konsole, Dateien und Metriken der Minecraft-Server" },
                new object[] { null, 2, Icons.Globe, "Spieler & VPN", "Wer ist online, Login-Verlauf, VPN/Proxy-Erkennung" },
                new object[] { null, 3, Icons.People, "Nutzer & Ränge", "Money, Coins, Ränge, Permissions, Bans" },
                new object[] { null, 8, Icons.Chart, "Wirtschaft", "Geldfluss, Shop-Preise, Crates – wie viel Geld ist im Umlauf?" },
                new object[] { null, 9, Icons.Block, "Moderation", "Reports, Strafen (Ban/Mute/Kick/Warn) und Discord-Verify – netzwerkweit" },
                new object[] { "ÜBERWACHUNG", null, null, null },
                new object[] { null, 10, Icons.Lock, "Datenschutz", "Verbindungs-Wächter, DSGVO-Löschungen, Auskunft, Plugin-Doku, Verzeichnis" },
                new object[] { null, 12, Icons.Robot, "KI-Support", "Fragen an die KI, Eskalationen, Wissen, Limits" },
                new object[] { null, 11, Icons.Network, "Netzwerk-Monitor", "Paketverlust, Latenz, Ausfälle – mit Beweiskette" },
                new object[] { "SERVER & SYSTEM", null, null, null },
                new object[] { null, 4, Icons.Terminal, "Terminal", "SSH-Konsole des Servers" },
                new object[] { null, 5, Icons.Folder, "Dateien", "Alle Dateien des Servers (SFTP)" },
                new object[] { null, 1, Icons.Package, "Docker", "Container, Stacks, Images" },
                new object[] { null, 6, Icons.Shield, "Firewall", "Ports öffnen/schließen, IPs sperren" },
                new object[] { "VERWALTUNG", null, null, null },
                new object[] { null, 7, Icons.Key, "Team & Zugänge", "Team-Logins, Rechte pro Rang, IP-Freigaben, Protokoll" },
                new object[] { null, 13, Icons.Coins, "Finanzen", "Buchhaltung: Ausgaben, Einnahmen, Belege, Jahresübersicht, Export" },
            };
            nav = new NavItem[14];
            var navCtrls = new List<Control>();
            foreach (var d in navDefs)
            {
                if (d[0] != null)
                {
                    var cat = Theme.Lbl((string)d[0], Theme.F(7.75f, FontStyle.Bold), Theme.Muted, Theme.Side);
                    cat.AutoSize = false; cat.Dock = DockStyle.Top; cat.Height = 27; cat.Padding = new Padding(20, 10, 0, 0);
                    if (team != null && (string)d[0] != "MINECRAFT" && (string)d[0] != "ÜBERWACHUNG") cat.Visible = false;
                    navCtrls.Add(cat);
                    continue;
                }
                int idx = (int)d[1];
                var it = new NavItem((string)d[2], (string)d[3]) { Dock = DockStyle.Top, Height = 35 }.Tip((string)d[4]);
                it.Click += (s, e) => SetMode(idx);
                nav[idx] = it;
                navCtrls.Add(it);
            }
            navCtrls.Reverse();
            navPanel.Controls.AddRange(navCtrls.ToArray());
            nav[0].Active = true;

            var sideBottom = new Panel { Dock = DockStyle.Bottom, Height = 92, BackColor = Theme.Side, Padding = new Padding(16, 6, 12, 12) };
            summary = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Theme.Side);
            summary.Dock = DockStyle.Top;
            summary.AutoSize = false;
            summary.Height = 34;
            var acct = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Theme.Side };
            var avatar = new Label { Text = user.Length > 0 ? user.Substring(0, 1).ToUpperInvariant() : "?", Size = new Size(30, 30), Location = new Point(0, 3), TextAlign = ContentAlignment.MiddleCenter, Font = Theme.F(10f, FontStyle.Bold), ForeColor = Color.White, BackColor = Theme.Accent };
            var uname = unameLbl = Theme.Lbl(team != null ? user + "  ·  " + team.Rank : user, Theme.F(9.5f, FontStyle.Bold), Theme.Text, Theme.Side);
            uname.Location = new Point(38, 8);
            if (team != null)
            {
                uname.Cursor = avatar.Cursor = Cursors.Hand;
                uname.Tip("Klicken: Passwort ändern oder 2FA einrichten");
                avatar.Tip("Klicken: Passwort ändern oder 2FA einrichten");
                var kontoMenu = new ContextMenuStrip();
                kontoMenu.Items.Add("Passwort ändern …", null, async (s, e) => { if (await TeamSession.ChangePassword(this, api, null, false)) SetStatus("Passwort geändert.", Theme.Green); });
                kontoMenu.Items.Add("Zwei-Faktor-Anmeldung (2FA) …", null, async (s, e) => { if (await ZweiFaktorDialog.Open(this, api, false)) SetStatus("2FA ist aktiv.", Theme.Green); });
                EventHandler pw = (s, e) => { var c = (Control)s; kontoMenu.Show(c, new Point(0, c.Height)); };
                uname.Click += pw;
                avatar.Click += pw;
            }
            var logout = new FlatBtn("Abmelden", Theme.CardHi) { Size = new Size(84, 30), Font = Theme.F(8.5f, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            acct.Resize += (s, e) => logout.Location = new Point(acct.Width - logout.Width, 3);
            logout.Click += (s, e) => Logout();
            acct.Controls.AddRange(new Control[] { avatar, uname, logout });
            sideBottom.Controls.Add(summary);
            sideBottom.Controls.Add(acct);
            sideBottom.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Theme.Border });

            side.Controls.Add(navPanel);
            side.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 6, BackColor = Theme.Side });
            side.Controls.Add(searchBtn);
            side.Controls.Add(sideTop);
            side.Controls.Add(sideBottom);

            // ── Minecraft page: server strip on top, then server header + tabs ──
            list = new ServerList { Dock = DockStyle.Top, Height = 58, Horizontal = true, ItemWidth = 232, BackColor = Theme.Bg };
            list.DrawEntry = DrawServerItem;
            list.SelectedChanged += async (s, e) => await SelectServer(list.SelectedItem);
            var stripHead = new Panel { Dock = DockStyle.Top, Height = 24, BackColor = Theme.Bg };
            var stripLbl = Theme.Lbl("MINECRAFT-SERVER  ·  zum Auswählen anklicken", Theme.F(7.75f, FontStyle.Bold), Theme.Muted, Theme.Bg);
            stripLbl.Location = new Point(2, 2);
            var refresh = new FlatBtn("↻ Aktualisieren", Theme.Bg) { Size = new Size(112, 24), Font = Theme.F(8f, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right }.Tip("Serverliste neu laden");
            stripHead.Resize += (s, e) => refresh.Location = new Point(stripHead.Width - refresh.Width, 0);
            refresh.Click += async (s, e) => await LoadServers();
            stripHead.Controls.AddRange(new Control[] { stripLbl, refresh });

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0) };
            var header = new Panel { Dock = DockStyle.Top, Height = 100, BackColor = Theme.Bg };
            hName = Theme.Lbl("Server wählen", Theme.F(17f, FontStyle.Bold), Theme.Text, Theme.Bg);
            hName.Location = new Point(0, 10);
            hSub = Theme.Lbl("", Theme.F(9.25f), Theme.Muted, Theme.Bg);
            hSub.Location = new Point(2, 44);
            hPill = new Pill { Location = new Point(0, 14), BackColor = Theme.Bg };
            hPill.Set("–", Theme.Muted);
            hName.SizeChanged += (s, e) => hPill.Left = hName.Right + 12;
            tabOverview = new TabBtn("Übersicht") { Location = new Point(0, 62) };
            tabConsole = new TabBtn("Konsole") { Location = new Point(tabOverview.Right + 4, 62) };
            tabFiles = new TabBtn("Dateien") { Location = new Point(tabConsole.Right + 4, 62) };
            tabMetrics = new TabBtn("Metriken") { Location = new Point(tabFiles.Right + 4, 62) };
            tabAdmin = new TabBtn("Verwaltung") { Location = new Point(tabMetrics.Right + 4, 62) };
            var line = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border };
            line.Paint += (s, e) =>
            {
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, Math.Max(1, line.Width), 1), Theme.Alpha(Theme.SoftGreen, 90), Theme.Alpha(Theme.Mint, 10), 0f))
                    e.Graphics.FillRectangle(b, 0, 0, line.Width, 1);
            };
            header.Controls.AddRange(new Control[] { hName, hSub, hPill, tabOverview, tabConsole, tabFiles, tabMetrics, tabAdmin, line });
            tabOverview.Click += (s, e) => ShowPage(0);
            tabConsole.Click += (s, e) => ShowPage(1);
            tabFiles.Click += (s, e) => ShowPage(2);
            tabMetrics.Click += (s, e) => ShowPage(3);
            tabAdmin.Click += (s, e) => ShowPage(4);

            pageOverview = BuildOverview();
            pageConsole = BuildConsole();
            pageFiles = BuildFiles();
            pageMetrics = BuildMetrics();
            pageAdmin = BuildAdmin();
            content.Controls.Add(pageOverview);
            content.Controls.Add(pageConsole);
            content.Controls.Add(pageFiles);
            content.Controls.Add(pageMetrics);
            content.Controls.Add(pageAdmin);

            craftyPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            craftyPanel.Controls.Add(content);
            craftyPanel.Controls.Add(header);
            craftyPanel.Controls.Add(list);
            craftyPanel.Controls.Add(stripHead);
            docker = new DockerView(st, SetStatus) { Visible = false };
            players = new PlayerView(st, () => docker.Ssh, () => docker.Ssh.KeyExists, SetStatus) { Visible = false };
            Func<Ssh> getSsh = () => docker.Ssh;
            Func<bool> sshOk = () => docker.Ssh.KeyExists;
            terminal = new TerminalView(getSsh, sshOk, SetStatus) { Visible = false };
            firewall = new FirewallView(getSsh, sshOk, SetStatus) { Visible = false };
            files = new FilesView(getSsh, sshOk, SetStatus,
                () => servers.Select(s => new KeyValuePair<string, string>(s.Name, "/opt/crafty/docker/servers/" + s.Id)).ToList()) { Visible = false };
            files.OpenTerminalAt = dir => { SetMode(4); terminal.Open(System.IO.Path.GetFileName(dir.TrimEnd('/')) + "/", dir, null); };
            users = new UserView(api, () => servers.ToList(), getSsh, sshOk, SetStatus, () => firewall) { Visible = false };
            economy = new EconomyView(api, team != null, Can("economy.edit"), getSsh, sshOk, SetStatus) { Visible = false };
            finanzen = new FinanzenView(api, team != null, getSsh, sshOk, SetStatus) { Visible = false };
            modClient = new ModClient(api, team != null, getSsh) { Actor = user + " (Panel)" };
            moderation = new ModerationView(modClient, SetStatus) { Visible = false };
            users.Mod = modClient;
            datenschutz = new DatenschutzView(modClient, SetStatus) { Visible = false, Can = Can };
            datenschutz.BadgeChanged += (a, b) => { if (nav[10] != null) { nav[10].Badge = a + b > 0 ? (a + b).ToString() : null; nav[10].Invalidate(); } };
            netz = new NetzView(api, team != null, getSsh, sshOk, SetStatus) { Visible = false };
            ki = new KiView(modClient, SetStatus) { Visible = false, CanEdit = Can("ki.edit") };
            teamView = new TeamView(getSsh, sshOk, SetStatus, () => team == null ? api.Token : null) { Visible = false, GetFirewall = () => firewall };
            teamView.PendingChanged = n => { nav[7].Badge = n > 0 ? n.ToString() : null; nav[7].Invalidate(); };
            main.Controls.Add(teamView);
            main.Controls.Add(craftyPanel);
            main.Controls.Add(docker);
            main.Controls.Add(players);
            main.Controls.Add(users);
            main.Controls.Add(economy);
            main.Controls.Add(finanzen);
            main.Controls.Add(moderation);
            main.Controls.Add(datenschutz);
            main.Controls.Add(netz);
            main.Controls.Add(ki);
            main.Controls.Add(terminal);
            main.Controls.Add(files);
            main.Controls.Add(firewall);
            fileProgress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };
            main.Controls.Add(fileProgress);
            ApplyPermissions();
            ShowPage(0);

            pulseTimer.Tick += (s, e) =>
            {
                pulse = (pulse + 0.05) % (Math.PI * 2);
                for (int i = 0; i < list.Items.Count; i++)
                    if (list.Items[i].State == "online")
                    {
                        var r = list.ItemRect(i);
                        list.Invalidate(new Rectangle(r.X, r.Y, Theme.Px(44), r.Height));
                    }
            };
            pulseTimer.Start();
        }

        // ───────────────────────────── Window size & search ─────────────────────────────
        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e); // scales the whole layout to the Windows display scaling
            // Restore the last position if it is still on one of the connected screens (works with several monitors).
            var saved = new Rectangle(st.WinX, st.WinY, st.WinW, st.WinH);
            var screen = st.WinW > 200 && st.WinH > 200 ? Screen.AllScreens.FirstOrDefault(s => { var i = Rectangle.Intersect(s.WorkingArea, saved); return i.Width > 200 && i.Height > 150; }) : null;
            if (screen == null) screen = Screen.FromPoint(Cursor.Position);
            var wa = screen.WorkingArea;
            MinimumSize = new Size(Math.Min(Theme.Px(860), wa.Width), Math.Min(Theme.Px(560), wa.Height));
            if (st.WinW > 200 && Screen.AllScreens.Contains(screen) && Rectangle.Intersect(wa, saved).Width > 200)
            {
                int w = Math.Min(saved.Width, wa.Width), h = Math.Min(saved.Height, wa.Height);
                int x = Math.Max(wa.X, Math.Min(saved.X, wa.Right - w)), y = Math.Max(wa.Y, Math.Min(saved.Y, wa.Bottom - h));
                Bounds = new Rectangle(x, y, w, h);
                if (st.WinMax) WindowState = FormWindowState.Maximized;
            }
            else
            {
                int w = Math.Min(Theme.Px(1320), wa.Width - 16), h = Math.Min(Theme.Px(840), wa.Height - 16);
                Bounds = new Rectangle(wa.X + (wa.Width - w) / 2, wa.Y + (wa.Height - h) / 2, w, h);
                if (wa.Width < Theme.Px(1280) || wa.Height < Theme.Px(760)) WindowState = FormWindowState.Maximized;
            }
            FormClosed += (s, a) =>
            {
                var r = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
                st.WinX = r.X; st.WinY = r.Y; st.WinW = r.Width; st.WinH = r.Height;
                st.WinMax = WindowState == FormWindowState.Maximized;
                st.Save();
            };
        }

        /// <summary>When the window is dragged to another monitor, keep the minimum size valid for that screen.</summary>
        protected override void OnMove(EventArgs e)
        {
            base.OnMove(e);
            if (!IsHandleCreated) return;
            var wa = Screen.FromControl(this).WorkingArea;
            var min = new Size(Math.Min(Theme.Px(860), wa.Width), Math.Min(Theme.Px(560), wa.Height));
            if (MinimumSize != min) MinimumSize = min;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            bool inTerminal = terminal != null && terminal.Visible && terminal.ContainsFocus;
            if ((keyData == (Keys.Control | Keys.K) && !inTerminal) || keyData == (Keys.Control | Keys.Shift | Keys.K) || (keyData == (Keys.Control | Keys.F) && mode != 0 && mode != 4 && mode != 5))
            {
                OpenPalette("");
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void OpenPalette(string q)
        {
            var p = new CommandPalette(this, PaletteItems);
            p.Show(this);
        }

        static PaletteItem PI(string cat, string glyph, string title, string hint, Action run, string kw = "")
        {
            return new PaletteItem { Category = cat, Glyph = glyph, Title = title, Hint = hint, Run = run, Keywords = kw };
        }

        IEnumerable<PaletteItem> PaletteItems(string q)
        {
            if (team != null) return TeamPaletteItems(q);
            var all = new List<PaletteItem>
            {
                PI("Bereich", Icons.Server, "Server-Steuerung", "Minecraft-Server starten, stoppen, Konsole, Dateien", () => SetMode(0), "minecraft crafty"),
                PI("Bereich", Icons.Globe, "Spieler & VPN", "Online-Spieler, Login-Verlauf, VPN-Erkennung", () => SetMode(2), "velocity ip"),
                PI("Bereich", Icons.People, "Nutzer & Ränge", "Money, Coins, Ränge, Permissions, Bans", () => SetMode(3), "user luckperms rang rank perms geld"),
                PI("Bereich", Icons.Chart, "Wirtschaft", "Geldmenge, Shop, Preise, Crates", () => SetMode(8), "economy geld money shop preise crates inflation"),
                PI("Bereich", Icons.Block, "Moderation", "Reports, Strafen, Verify, Spieler bestrafen", () => SetMode(9), "report ban mute kick warn verwarnen strafe verify discord link"),
                PI("Bereich", Icons.Coins, "Finanzen", "Buchhaltung: Ausgaben, Einnahmen, Belege, Export", () => SetMode(13), "buchhaltung kosten ausgaben einnahmen beleg rechnung export steuer"),
                PI("Aktion", Icons.Block, "Spieler bestrafen", "Verwarnen, Kicken, Muten, Bannen – netzwerkweit", async () => { SetMode(9); await PunishDialog.Run(this, modClient, "", "warn", "", SetStatus); }, "ban mute kick warn"),
                PI("Bereich", Icons.Lock, "Datenschutz", "Wächter, DSGVO-Löschungen, Auskunft, Plugin-Doku", () => SetMode(10), "dsgvo gdpr löschen auskunft wächter verbindungen plugins verzeichnis"),
                PI("Bereich", Icons.Robot, "KI-Support", "Fragen, Eskalationen, Wissen, Limits", () => SetMode(12), "ki ai support frage eskalation wissen bot"),
                PI("Bereich", Icons.Network, "Netzwerk-Monitor", "Paketverlust, Latenz, Ausfälle", () => SetMode(11), "netz ping verlust latenz ausfall hoster"),
                PI("Bereich", Icons.Terminal, "Terminal", "SSH-Konsole des Servers", () => SetMode(4), "ssh shell bash konsole"),
                PI("Bereich", Icons.Folder, "Dateien", "Alle Server-Dateien (SFTP)", () => SetMode(5), "sftp upload download"),
                PI("Bereich", Icons.Package, "Docker", "Container, Stacks, Images", () => SetMode(1), "container"),
                PI("Bereich", Icons.Shield, "Firewall", "Ports und IP-Sperren", () => SetMode(6), "ports iptables"),
            };
            Action<int, string[], string, Action<int>> tabs = (m, names, area, go) =>
            {
                for (int i = 0; i < names.Length; i++) { int k = i; all.Add(PI("Seite", nav[m].Glyph, area + " › " + names[i], "Direkt zu diesem Tab", () => { SetMode(m); go(k); })); }
            };
            tabs(0, new[] { "Übersicht", "Konsole", "Dateien", "Metriken", "Verwaltung" }, "Server", i => ShowPage(i));
            tabs(2, new[] { "Live", "Verlauf", "VPN-Statistik" }, "Spieler & VPN", i => players.GoTab(i));
            tabs(3, new[] { "Spieler", "Ränge", "Protokoll" }, "Nutzer", i => users.GoTab(i));
            tabs(8, new[] { "Übersicht", "Spieler", "Shop", "Preise", "Crates" }, "Wirtschaft", i => economy.GoTab(i));
            tabs(9, new[] { "Reports", "Strafen", "Verify" }, "Moderation", i => moderation.GoTab(i));
            tabs(1, new[] { "Container", "Stacks", "Images", "Netzwerke & Volumes", "Host" }, "Docker", i => docker.GoTab(i));
            tabs(6, new[] { "Ports", "Regeln", "Gesperrte IPs", "Verbindungen" }, "Firewall", i => firewall.GoTab(i));

            var srvName = cur != null ? cur.Name : "aktueller Server";
            all.Add(PI("Aktion", Icons.Play, "Server starten", srvName, () => { SetMode(0); bStart.PerformClick(); }, "start"));
            all.Add(PI("Aktion", Icons.Stop, "Server stoppen", srvName, () => { SetMode(0); bStop.PerformClick(); }, "stop"));
            all.Add(PI("Aktion", Icons.Refresh, "Server neu starten", srvName, () => { SetMode(0); bRestart.PerformClick(); }, "restart neustart"));
            all.Add(PI("Aktion", Icons.Save, "Backup erstellen", srvName, () => { SetMode(0); bBackup.PerformClick(); }, "sicherung"));
            all.Add(PI("Aktion", Icons.Console, "Konsolenbefehl senden", srvName, () => { SetMode(0); ShowPage(1); }, "command befehl"));
            all.Add(PI("Aktion", Icons.Console, "Nachricht an alle Server", "Broadcast an alle laufenden Server", async () => await Broadcast(), "say broadcast"));
            all.Add(PI("Aktion", Icons.Terminal, "Neues Terminal öffnen", "SSH-Shell in neuem Tab", () => { SetMode(4); terminal.Open(null, null, null); }, "ssh"));
            all.Add(PI("Aktion", Icons.Upload, "Dateien hochladen", "Im Datei-Manager", () => SetMode(5), "upload"));
            all.Add(PI("Aktion", Icons.Add, "Port öffnen", "Firewall-Regel hinzufügen", () => { SetMode(6); firewall.OpenRuleDialog("allow"); }, "allow freigeben"));
            all.Add(PI("Aktion", Icons.Block, "Port schließen", "Port von außen sperren", () => { SetMode(6); firewall.OpenRuleDialog("deny"); }, "deny sperren"));
            all.Add(PI("Aktion", Icons.Block, "IP-Adresse sperren", "In der Firewall blockieren", async () => { SetMode(6); await firewall.BlockIp(null, null); }, "ban block"));
            all.Add(PI("Aktion", Icons.Settings, "SSH-Verbindung einrichten", "Schlüssel & Server für Terminal/Dateien/Firewall", () => SetMode(1), "key schlüssel"));
            all.Add(PI("Aktion", Icons.Power, "Abmelden", "", Logout, "logout"));

            foreach (var s in servers)
            {
                var sv = s;
                all.Add(PI("Server", Icons.Server, sv.Name, sv.StateText + (sv.Running ? "  ·  " + (int)J.N(sv.Stats, "online") + " Spieler" : ""), () => { SetMode(0); list.SelectedItem = sv; }));
            }
            var ranked = CommandPalette.Rank(all, q).ToList();
            if (string.IsNullOrWhiteSpace(q)) return ranked;

            foreach (var p in CommandPalette.Rank(users.Players.Select(pl => PI("Spieler", Icons.Person, pl.Name,
                         (pl.Online ? "online  ·  " : "") + pl.Primary + (pl.Money.HasValue ? "  ·  Money " + Ui.Num(pl.Money.Value) : "") + "  ·  " + pl.Uuid,
                         () => { SetMode(3); users.OpenPlayer(pl.Uuid); }, pl.Uuid + " " + pl.Ip)), q).Take(15))
                ranked.Add(p);
            var qq = q.Trim();
            ranked.Add(PI("Suche", Icons.People, "Spieler suchen: „" + qq + "“", "In der Nutzerverwaltung", () => { SetMode(3); users.SearchFor(qq); }));
            ranked.Add(PI("Suche", Icons.Folder, "Dateien suchen: „" + qq + "“", "Auf dem Server (Datei-Manager)", () => { SetMode(5); files.SearchFor(qq); }));
            if (FwRule.ValidSource(qq) && qq != "any")
                ranked.Insert(0, PI("Aktion", Icons.Block, "IP " + qq + " in der Firewall sperren", "Blockiert alles von dieser IP", async () => { SetMode(6); await firewall.BlockIp(qq, "per Suche"); }));
            return ranked;
        }
        /// <summary>Team members: hide tabs/buttons their rank may not use and close the gaps.</summary>
        void ApplyPermissions()
        {
            if (team == null) return;
            // sidebar: team members see the server area and – with economy.view – the economy tab
            for (int i = 1; i < nav.Length; i++) if (nav[i] != null) nav[i].Visible = (i == 8 && Can("economy.view")) || (i == 9 && Can("moderation.view"))
                || (i == 10 && Can("datenschutz.view")) || (i == 11 && Can("netzwerk.view")) || (i == 12 && Can("ki.view")) || (i == 13 && Can("finanzen.view"));
            datenschutz.ApplyPerms(); ki.CanEdit = Can("ki.edit"); ki.ApplyPerms();
            if ((mode == 10 && !Can("datenschutz.view")) || (mode == 11 && !Can("netzwerk.view")) || (mode == 12 && !Can("ki.view")) || (mode == 13 && !Can("finanzen.view"))) SetMode(0);
            finanzen.SetCanEdit(Can("finanzen.edit"));
            modClient.CanView = Can("moderation.view"); modClient.CanReports = Can("moderation.reports");
            modClient.CanPunish = Can("moderation.punish"); modClient.CanBan = Can("moderation.ban");
            moderation.ApplyPerms();
            if (mode == 9 && !Can("moderation.view")) SetMode(0);
            economy.SetCanEdit(Can("economy.edit"));
            if (mode == 8 && !Can("economy.view")) SetMode(0);
            Need(tabConsole, "console.view");
            Need(tabFiles, "files.view");
            Need(tabMetrics, "servers.view");
            Need(tabAdmin, "console.command");
            Need(bStart, "server.control"); Need(bStop, "server.control"); Need(bRestart, "server.control");
            Need(bKill, "server.kill");
            Need(bBackup, "server.backup");
            Need(bSave, "files.edit");
            int x = 0;
            foreach (var t in new[] { tabOverview, tabConsole, tabFiles, tabMetrics, tabAdmin })
            {
                if (!t.Visible) continue;
                t.Left = x;
                x = t.Right + 4;
            }
            // the open server tab was taken away -> back to the overview
            if ((pageConsole.Visible && !tabConsole.Visible) || (pageFiles.Visible && !tabFiles.Visible) ||
                (pageMetrics.Visible && !tabMetrics.Visible) || (pageAdmin.Visible && !tabAdmin.Visible))
                ShowPage(0);
        }

        /// <summary>Team members: reloads rank + rights from the gateway and updates the UI if the owner changed them.</summary>
        async Task RefreshTeamPerms()
        {
            if (team == null || permBusy || closingForLogin) return;
            permBusy = true;
            permChecked = DateTime.Now;
            try
            {
                var r = await api.Get("/panel/me");
                if (!r.Ok || r.Data == null) return;
                var fresh = TeamSession.From(r.Data);
                if (fresh.Perms.SetEquals(team.Perms) && fresh.Rank == team.Rank) return;
                bool hadEco = Can("economy.view");
                team.Perms.Clear();
                team.Perms.UnionWith(fresh.Perms);
                team.Rank = fresh.Rank;
                whoLbl.Text = "Team-Zugang  ·  Rang " + team.Rank;
                unameLbl.Text = user + "  ·  " + team.Rank;
                ApplyPermissions();
                if (!hadEco && Can("economy.view")) SetStatus("Deine Rechte wurden geändert – der Bereich „Wirtschaft“ ist jetzt freigeschaltet.", Theme.Green);
                else SetStatus("Deine Rechte wurden vom Owner geändert (Rang " + team.Rank + ").", Theme.SoftGreen);
            }
            finally { permBusy = false; }
        }

        IEnumerable<PaletteItem> TeamPaletteItems(string q)
        {
            var all = new List<PaletteItem> { PI("Bereich", Icons.Server, "Server-Steuerung", "Server und Status", () => SetMode(0)) };
            if (Can("economy.view")) all.Add(PI("Bereich", Icons.Chart, "Wirtschaft", "Geldmenge, Shop, Preise, Crates", () => SetMode(8), "economy geld shop preise crates"));
            if (Can("finanzen.view")) all.Add(PI("Bereich", Icons.Coins, "Finanzen", "Buchhaltung, Belege, Export", () => SetMode(13), "buchhaltung kosten ausgaben einnahmen beleg"));
            if (Can("moderation.view")) all.Add(PI("Bereich", Icons.Block, "Moderation", "Reports, Strafen, Verify", () => SetMode(9), "report ban mute kick warn strafe verify"));
            if (Can("datenschutz.view")) all.Add(PI("Bereich", Icons.Lock, "Datenschutz", "Wächter, Löschungen, Auskunft", () => SetMode(10), "dsgvo löschen auskunft wächter"));
            if (Can("netzwerk.view")) all.Add(PI("Bereich", Icons.Network, "Netzwerk-Monitor", "Paketverlust, Latenz, Ausfälle", () => SetMode(11), "netz ping verlust"));
            if (Can("ki.view")) all.Add(PI("Bereich", Icons.Robot, "KI-Support", "Fragen, Eskalationen, Wissen", () => SetMode(12), "ki ai support"));
            if (Can("moderation.punish") || Can("moderation.ban")) all.Add(PI("Aktion", Icons.Block, "Spieler bestrafen", "Verwarnen, Kicken, Muten, Bannen", async () => { SetMode(9); await PunishDialog.Run(this, modClient, "", "warn", "", SetStatus); }, "ban mute kick warn"));
            var tabs = new[] { new object[] { "Übersicht", 0, "servers.view" }, new object[] { "Konsole", 1, "console.view" }, new object[] { "Dateien", 2, "files.view" },
                               new object[] { "Metriken", 3, "servers.view" }, new object[] { "Verwaltung", 4, "console.command" } };
            foreach (var t in tabs)
            {
                if (!Can((string)t[2])) continue;
                int k = (int)t[1];
                all.Add(PI("Seite", Icons.Server, "Server › " + t[0], "Direkt zu diesem Tab", () => { SetMode(0); ShowPage(k); }));
            }
            var srvName = cur != null ? cur.Name : "aktueller Server";
            if (Can("server.control"))
            {
                all.Add(PI("Aktion", Icons.Play, "Server starten", srvName, () => bStart.PerformClick(), "start"));
                all.Add(PI("Aktion", Icons.Stop, "Server stoppen", srvName, () => bStop.PerformClick(), "stop"));
                all.Add(PI("Aktion", Icons.Refresh, "Server neu starten", srvName, () => bRestart.PerformClick(), "restart neustart"));
            }
            if (Can("server.backup")) all.Add(PI("Aktion", Icons.Save, "Backup erstellen", srvName, () => bBackup.PerformClick(), "sicherung"));
            if (Can("console.command")) all.Add(PI("Aktion", Icons.Console, "Konsolenbefehl senden", srvName, () => ShowPage(1), "command befehl"));
            all.Add(PI("Aktion", Icons.Power, "Abmelden", "", Logout, "logout"));
            foreach (var s in servers)
            {
                var sv = s;
                all.Add(PI("Server", Icons.Server, sv.Name, sv.StateText, () => { SetMode(0); list.SelectedItem = sv; }));
            }
            return CommandPalette.Rank(all, q).ToList();
        }
        void SetMode(int m)
        {
            if (m == mode) return;
            if (mode == 1 && docker.HasUnsaved && !Dialogs.Confirm(this, "Ungespeichert", "Ungespeicherte Änderungen am Stack verwerfen?")) return;
            mode = m;
            for (int i = 0; i < nav.Length; i++) nav[i].Active = m == i;
            craftyPanel.Visible = m == 0;
            docker.Visible = m == 1;
            players.Visible = m == 2;
            users.Visible = m == 3;
            terminal.Visible = m == 4;
            files.Visible = m == 5;
            firewall.Visible = m == 6;
            teamView.Visible = m == 7;
            economy.Visible = m == 8;
            moderation.Visible = m == 9;
            datenschutz.Visible = m == 10;
            netz.Visible = m == 11;
            ki.Visible = m == 12;
            finanzen.Visible = m == 13;
            if (m != 9) moderation.Deactivate();
            if (m != 10) datenschutz.Deactivate();
            if (m != 12) ki.Deactivate();
            if (m != 13) finanzen.Deactivate();
            if (m != 7) teamView.Deactivate();
            if (m != 1) docker.Deactivate();
            if (m != 2) players.Deactivate();
            if (m == 0) ShowPage(CurrentPage());
            else logTimer.Stop();
            if (m == 1) docker.Activate();
            if (m == 2) players.Activate();
            if (m == 3) users.Activate();
            if (m == 4) terminal.Activate();
            if (m == 5) files.Activate();
            if (m == 6) firewall.Activate();
            if (m == 7) teamView.Activate();
            if (m == 8) economy.Activate();
            if (m == 9) moderation.Activate();
            if (m == 10) datenschutz.Activate();
            if (m == 11) netz.Activate();
            if (m == 12) ki.Activate();
            if (m == 13) finanzen.Activate();
        }

        int CurrentPage()
        {
            if (pageConsole.Visible) return 1;
            if (pageFiles.Visible) return 2;
            if (pageMetrics.Visible) return 3;
            if (pageAdmin.Visible) return 4;
            return 0;
        }

        Panel BuildOverview()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };

            var cards = new CardGrid { Dock = DockStyle.Top, MinCardWidth = 150, CardHeight = 104 };
            cStatus = new StatCard("Status");
            cCpu = new StatCard("CPU");
            cRam = new StatCard("Arbeitsspeicher");
            cPlayers = new StatCard("Spieler");
            cVersion = new StatCard("Version");
            cWorld = new StatCard("Welt");
            cards.Controls.AddRange(new Control[] { cStatus, cCpu, cRam, cPlayers, cVersion, cWorld });

            var actions = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 58, BackColor = Theme.Bg, WrapContents = false, Padding = new Padding(0, 4, 0, 0) };
            bStart = new FlatBtn("▶  Starten", Theme.Mix(Theme.Green, Theme.Bg, 0.25));
            bStop = new FlatBtn("■  Stoppen", Theme.Mix(Theme.Red, Theme.Bg, 0.2));
            bRestart = new FlatBtn("↻  Neustart", Theme.Accent);
            bKill = new FlatBtn("✕  Kill", Theme.DarkRed);
            bBackup = new FlatBtn("⛁  Backup", Theme.CardHi);
            foreach (var b in new[] { bStart, bStop, bRestart, bKill, bBackup })
            {
                b.Height = 40;
                b.Width += 12;
                b.Margin = new Padding(0, 0, 10, 0);
                actions.Controls.Add(b);
            }
            bStart.Click += async (s, e) => await Action("start_server", "Start");
            bStop.Click += async (s, e) =>
            {
                if (Dialogs.Confirm(this, "Server stoppen", "„" + cur.Name + "“ wirklich stoppen?")) await Action("stop_server", "Stopp");
            };
            bRestart.Click += async (s, e) =>
            {
                if (Dialogs.Confirm(this, "Server neu starten", "„" + cur.Name + "“ wirklich neu starten?")) await Action("restart_server", "Neustart");
            };
            bKill.Click += async (s, e) =>
            {
                if (Dialogs.Confirm(this, "Server killen", "„" + cur.Name + "“ hart beenden?\n\nDabei wird NICHT gespeichert – nur nutzen, wenn der Server hängt."))
                    await Action("kill_server", "Kill");
            };
            bBackup.Click += async (s, e) => await Backup();

            var spacer = new Panel { Dock = DockStyle.Top, Height = 14, BackColor = Theme.Bg };

            var players = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(18, 48, 18, 18), MinimumSize = new Size(0, 220) };
            playersTitle = Theme.Lbl("SPIELER ONLINE", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, players.Fill);
            playersTitle.Location = new Point(18, 18);
            players.Controls.Add(playersTitle);
            var pButtons = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 170, BackColor = Theme.Card, FlowDirection = FlowDirection.TopDown, WrapContents = false, Padding = new Padding(14, 0, 0, 0) };
            var bMsg = new FlatBtn("Nachricht", Theme.CardHi);
            var bKick = new FlatBtn("Kicken", Theme.CardHi);
            var bBan = new FlatBtn("Bannen", Theme.Mix(Theme.Red, Theme.Card, 0.35));
            var bOp = new FlatBtn("OP geben", Theme.CardHi);
            var bTp = new FlatBtn("Gamemode", Theme.CardHi);
            foreach (var b in new[] { bMsg, bKick, bBan, bOp, bTp })
            {
                b.Width = 150;
                b.Margin = new Padding(0, 0, 0, 8);
                pButtons.Controls.Add(b);
                Need(b, "console.command");
            }
            playerList = new ListBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                Font = Theme.F(10.5f),
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 32,
                IntegralHeight = false
            };
            playerList.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(Theme.Card)) g.FillRectangle(b, e.Bounds);
                var r = new Rectangle(e.Bounds.X, e.Bounds.Y + 2, e.Bounds.Width - 4, e.Bounds.Height - 4);
                if (sel) using (var p = Theme.RoundRect(r, 8)) using (var b = new SolidBrush(Theme.CardHi)) g.FillPath(b, p);
                var name = playerList.Items[e.Index].ToString();
                using (var b = new SolidBrush(Theme.Mix(Theme.Accent, Theme.SoftGreen, (name.GetHashCode() & 0xff) / 255.0)))
                    g.FillEllipse(b, r.X + 10, r.Y + 7, 14, 14);
                TextRenderer.DrawText(g, name, playerList.Font, new Point(r.X + 34, r.Y + 4), Theme.Text);
            };
            players.Controls.Add(playerList);
            players.Controls.Add(pButtons);

            bMsg.Click += async (s, e) =>
            {
                var p = SelectedPlayer(); if (p == null) return;
                var m = Dialogs.Ask(this, "Nachricht", "Private Nachricht an " + p + ":", "");
                if (!string.IsNullOrWhiteSpace(m)) await SendCommand("tell " + p + " " + m);
            };
            bKick.Click += async (s, e) =>
            {
                var p = SelectedPlayer(); if (p == null) return;
                var m = Dialogs.Ask(this, "Kicken", "Grund für den Kick von " + p + ":", "Bitte halte dich an die Regeln.");
                if (m != null) await SendCommand("kick " + p + " " + m);
            };
            bBan.Click += async (s, e) =>
            {
                var p = SelectedPlayer(); if (p == null) return;
                var m = Dialogs.Ask(this, "Bannen", "Grund für den Bann von " + p + ":", "");
                if (m != null && Dialogs.Confirm(this, "Bannen", p + " wirklich bannen?")) await SendCommand("ban " + p + " " + m);
            };
            bOp.Click += async (s, e) =>
            {
                var p = SelectedPlayer(); if (p == null) return;
                if (Dialogs.Confirm(this, "OP", p + " Operator-Rechte geben?")) await SendCommand("op " + p);
            };
            bTp.Click += async (s, e) =>
            {
                var p = SelectedPlayer(); if (p == null) return;
                var m = Dialogs.Ask(this, "Gamemode", "Gamemode für " + p + " (survival, creative, adventure, spectator):", "survival");
                if (!string.IsNullOrWhiteSpace(m)) await SendCommand("gamemode " + m.Trim() + " " + p);
            };

            page.Controls.Add(players);
            page.Controls.Add(spacer);
            page.Controls.Add(actions);
            page.Controls.Add(cards);
            return page;
        }

        Panel BuildConsole()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var box = new RoundPanel { Dock = DockStyle.Fill, Fill = Theme.Input, BorderColor = Theme.Border, Padding = new Padding(14, 12, 6, 12) };
            console = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Input,
                ForeColor = Theme.Text2,
                Font = Theme.Mono,
                ReadOnly = true,
                WordWrap = true,
                DetectUrls = false,
                HideSelection = false,
                ScrollBars = RichTextBoxScrollBars.Vertical
            };
            box.Controls.Add(console);

            var bottom = Need(new Panel { Dock = DockStyle.Bottom, Height = 58, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0) }, "console.command");
            cmdField = new Field(false) { Dock = DockStyle.Fill };
            cmdField.Box.Font = Theme.Mono;
            var send = new FlatBtn("Senden", Theme.Accent) { Dock = DockStyle.Right, Width = 110, Height = 40 };
            var gap = new Panel { Dock = DockStyle.Right, Width = 10, BackColor = Theme.Bg };
            var clear = new FlatBtn("Leeren", Theme.CardHi) { Dock = DockStyle.Right, Width = 90, Height = 40 };
            var gap2 = new Panel { Dock = DockStyle.Right, Width = 10, BackColor = Theme.Bg };
            autoScroll = new CheckBox { Text = "Auto-Scroll", Checked = true, ForeColor = Theme.Text2, BackColor = Theme.Bg, Dock = DockStyle.Right, Width = 110, Padding = new Padding(12, 0, 0, 0) };
            bottom.Controls.Add(cmdField);
            bottom.Controls.Add(gap);
            bottom.Controls.Add(send);
            bottom.Controls.Add(gap2);
            bottom.Controls.Add(clear);
            bottom.Controls.Add(autoScroll);

            send.Click += async (s, e) => await SendFromBox();
            clear.Click += (s, e) => console.Clear();
            cmdField.Box.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    await SendFromBox();
                }
                else if (e.KeyCode == Keys.Up && history.Count > 0)
                {
                    e.SuppressKeyPress = true;
                    historyPos = Math.Max(0, historyPos - 1);
                    cmdField.Box.Text = history[historyPos];
                    cmdField.Box.SelectionStart = cmdField.Box.TextLength;
                }
                else if (e.KeyCode == Keys.Down && history.Count > 0)
                {
                    e.SuppressKeyPress = true;
                    historyPos = Math.Min(history.Count, historyPos + 1);
                    cmdField.Box.Text = historyPos < history.Count ? history[historyPos] : "";
                    cmdField.Box.SelectionStart = cmdField.Box.TextLength;
                }
            };

            page.Controls.Add(box);
            page.Controls.Add(bottom);
            return page;
        }

        Panel BuildFiles()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                BackColor = Theme.Bg,
                SplitterWidth = 12,
                FixedPanel = FixedPanel.Panel1
            };
            split.Panel1.BackColor = Theme.Bg;
            split.Panel2.BackColor = Theme.Bg;
            page.Controls.Add(split);
            UiX.Ratio(split, 0.34, 300, 360);

            // left: browser
            var left = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 98, 10, 12) };
            var tools = new FlowLayoutPanel { Location = new Point(10, 52), Size = new Size(340, 40), BackColor = Theme.Card, WrapContents = false };
            var bUpFiles = new FlatBtn("⇪ Dateien", Theme.Accent) { Height = 34, Width = 84 };
            var bUpDir = new FlatBtn("⇪ Ordner", Theme.Accent) { Height = 34, Width = 80 };
            var bMkdir = new FlatBtn("+ Ordner", Theme.CardHi) { Height = 34, Width = 76 };
            var bRename = new FlatBtn("✎", Theme.CardHi) { Height = 34, Width = 38 };
            var bDelete = new FlatBtn("✕", Theme.Mix(Theme.Red, Theme.Card, 0.35)) { Height = 34, Width = 38 };
            var bSftp = new FlatBtn("SFTP", Theme.CardHi) { Height = 34, Width = 50 };
            foreach (var b in new[] { bUpFiles, bUpDir, bMkdir, bRename, bDelete, bSftp }) { b.Font = Theme.F(8.75f, FontStyle.Bold); b.Margin = new Padding(0, 0, 6, 0); tools.Controls.Add(b); Need(b, b == bSftp ? "#owner" : "files.edit"); }
            bSftp.Click += (s, e) =>
            {
                if (cur == null) return;
                var dir = "/opt/crafty/docker/servers/" + cur.Id + (string.IsNullOrEmpty(curDir) ? "" : "/" + curDir.Trim('/'));
                SetMode(5);
                files.OpenPath(dir);
            };
            left.Controls.Add(tools);
            left.Resize += (s, e) => tools.Width = left.Width - 20;
            bUpFiles.Click += async (s, e) =>
            {
                using (var d = new OpenFileDialog { Multiselect = true, Title = "Dateien hochladen (z. B. Plugins)", Filter = "Alle Dateien|*.*|Plugins (*.jar)|*.jar" })
                    if (d.ShowDialog(this) == DialogResult.OK) await UploadPaths(d.FileNames);
            };
            bUpDir.Click += async (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Ordner hochladen – inklusive Unterordner und leerer Ordner", ShowNewFolderButton = false })
                    if (d.ShowDialog(this) == DialogResult.OK) await UploadPaths(new[] { d.SelectedPath });
            };
            bMkdir.Click += async (s, e) =>
            {
                var n = Dialogs.Ask(this, "Neuer Ordner", "Name des neuen Ordners in /" + curDir + ":", "");
                if (string.IsNullOrWhiteSpace(n)) return;
                if (await MakeDir(curDir, n.Trim(), true)) await LoadDir(curDir);
            };
            bRename.Click += async (s, e) => await RenameSelected();
            bDelete.Click += async (s, e) => await DeleteSelected();
            var up = new FlatBtn("⬆", Theme.CardHi) { Location = new Point(10, 10), Size = new Size(38, 34) };
            var rf = new FlatBtn("↻", Theme.CardHi) { Location = new Point(54, 10), Size = new Size(38, 34) };
            pathLabel = Theme.Lbl("/", Theme.F(9.5f, FontStyle.Bold), Theme.Text2, left.Fill);
            pathLabel.AutoSize = false;
            pathLabel.AutoEllipsis = true;
            pathLabel.Location = new Point(100, 10);
            pathLabel.Size = new Size(240, 34);
            pathLabel.TextAlign = ContentAlignment.MiddleLeft;
            pathLabel.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
            left.Resize += (s, e) => pathLabel.Width = Math.Max(40, left.Width - 110);
            up.Click += async (s, e) => await GoUp();
            rf.Click += async (s, e) => await LoadDir(curDir);

            fileList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Card,
                ForeColor = Theme.Text,
                Font = Theme.F(9.75f),
                OwnerDraw = true,
                MultiSelect = false,
                HideSelection = false
            };
            fileList.Columns.Add("Name", 190);
            fileList.Columns.Add("Größe", 70, HorizontalAlignment.Right);
            fileList.Columns.Add("Geändert", 120);
            var icons = new ImageList { ImageSize = new Size(18, 18), ColorDepth = ColorDepth.Depth32Bit };
            icons.Images.Add("dir", MakeIcon(true));
            icons.Images.Add("file", MakeIcon(false));
            fileList.SmallImageList = icons;
            fileList.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), Theme.F(8f, FontStyle.Bold),
                    new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), Theme.Muted,
                    TextFormatFlags.VerticalCenter | (e.Header.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left));
            };
            fileList.DrawItem += (s, e) => { };
            fileList.DrawSubItem += (s, e) =>
            {
                var g = e.Graphics;
                bool sel = e.Item.Selected;
                using (var b = new SolidBrush(sel ? Theme.CardHi : Theme.Card)) g.FillRectangle(b, e.Bounds);
                var bounds = e.Bounds;
                if (e.ColumnIndex == 0)
                {
                    var img = fileList.SmallImageList.Images[e.Item.ImageKey];
                    if (img != null) g.DrawImage(img, bounds.X + 4, bounds.Y + (bounds.Height - 18) / 2, 18, 18);
                    bounds = new Rectangle(bounds.X + 28, bounds.Y, bounds.Width - 28, bounds.Height);
                }
                var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
                            (fileList.Columns[e.ColumnIndex].TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
                TextRenderer.DrawText(g, e.SubItem.Text, fileList.Font, new Rectangle(bounds.X + 4, bounds.Y, bounds.Width - 8, bounds.Height),
                    e.ColumnIndex == 0 ? Theme.Text : Theme.Muted, flags);
            };
            fileList.ItemActivate += async (s, e) => await OpenSelected();
            fileList.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Back) await GoUp();
                else if (e.KeyCode == Keys.Delete) await DeleteSelected();
                else if (e.KeyCode == Keys.F2) await RenameSelected();
            };
            fileList.AllowDrop = true;
            fileList.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            fileList.DragDrop += async (s, e) =>
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null && paths.Length > 0) await UploadPaths(paths);
            };
            left.Controls.Add(fileList);
            left.Controls.Add(up);
            left.Controls.Add(rf);
            left.Controls.Add(pathLabel);
            split.Panel1.Controls.Add(left);

            // right: editor
            var right = new RoundPanel { Dock = DockStyle.Fill, Fill = Theme.Card, Padding = new Padding(10, 54, 6, 10) };
            editorTitle = Theme.Lbl("Keine Datei geöffnet", Theme.F(10f, FontStyle.Bold), Theme.Text2, right.Fill);
            editorTitle.AutoSize = false;
            editorTitle.AutoEllipsis = true;
            editorTitle.Location = new Point(14, 10);
            editorTitle.Size = new Size(400, 34);
            editorTitle.TextAlign = ContentAlignment.MiddleLeft;
            bSave = new FlatBtn("Speichern  (Strg+S)", Theme.Accent) { Size = new Size(170, 34), Enabled = false };
            bReload = new FlatBtn("Neu laden", Theme.CardHi) { Size = new Size(100, 34), Enabled = false };
            right.Resize += (s, e) =>
            {
                bSave.Location = new Point(right.Width - bSave.Width - 10, 10);
                bReload.Location = new Point(bSave.Left - bReload.Width - 8, 10);
                editorTitle.Width = Math.Max(60, bReload.Left - 24);
            };
            editor = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsTab = true,
                AcceptsReturn = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Input,
                ForeColor = Theme.Text,
                Font = Theme.Mono,
                MaxLength = 0,
                ReadOnly = true
            };
            editor.TextChanged += (s, e) =>
            {
                if (suppressDirty || openPath == null) return;
                if (!dirty) { dirty = true; UpdateEditorTitle(); }
            };
            editor.KeyDown += async (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; await SaveFile(); }
                else if (e.Control && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; editor.SelectAll(); }
            };
            bSave.Click += async (s, e) => await SaveFile();
            bReload.Click += async (s, e) =>
            {
                if (openPath == null) return;
                if (dirty && !Dialogs.Confirm(this, "Neu laden", "Ungespeicherte Änderungen verwerfen?")) return;
                await OpenFile(openPath);
            };
            right.Controls.Add(editor);
            right.Controls.Add(editorTitle);
            right.Controls.Add(bReload);
            right.Controls.Add(bSave);
            split.Panel2.Controls.Add(right);
            return page;
        }

        static Bitmap MakeIcon(bool dir)
        {
            var bmp = new Bitmap(18, 18);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                if (dir)
                {
                    using (var b = new SolidBrush(Theme.Hex("#e0b04a")))
                    {
                        using (var p = Theme.RoundRect(new Rectangle(1, 3, 8, 4), 1)) g.FillPath(b, p);
                        using (var p = Theme.RoundRect(new Rectangle(1, 5, 16, 11), 2)) g.FillPath(b, p);
                    }
                }
                else
                {
                    using (var b = new SolidBrush(Theme.Hex("#7d8599")))
                    using (var p = Theme.RoundRect(new Rectangle(3, 1, 12, 16), 2)) g.FillPath(b, p);
                    using (var pen = new Pen(Theme.Card, 1.4f))
                    {
                        g.DrawLine(pen, 6, 7, 12, 7);
                        g.DrawLine(pen, 6, 10, 12, 10);
                        g.DrawLine(pen, 6, 13, 10, 13);
                    }
                }
            }
            return bmp;
        }

        void ShowPage(int i)
        {
            pageOverview.Visible = i == 0;
            pageConsole.Visible = i == 1;
            pageFiles.Visible = i == 2;
            pageMetrics.Visible = i == 3;
            pageAdmin.Visible = i == 4;
            tabOverview.Active = i == 0;
            tabConsole.Active = i == 1;
            tabFiles.Active = i == 2;
            tabMetrics.Active = i == 3;
            tabAdmin.Active = i == 4;
            if (i == 3) { var t3 = LoadMetrics(); }
            if (i == 4) { var t4 = LoadAdmin(); }
            if (i == 1)
            {
                logTimer.Start();
                var t = PollLog();
                cmdField.Box.Focus();
            }
            else logTimer.Stop();
            if (i == 2 && cur != null && fileList.Items.Count == 0)
            {
                var t = LoadDir(curDir);
            }
        }

        void SetStatus(string text, Color c)
        {
            status.Text = DateTime.Now.ToString("HH:mm:ss") + "   " + text;
            status.ForeColor = c;
        }

        // ───────────────────────────── Server list ─────────────────────────────
        void DrawServerItem(Graphics g, ServerItem it, Rectangle bounds, int index, double hover, double sel)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(bounds.X, bounds.Y + 2, bounds.Width - Theme.Px(10), bounds.Height - 5);
            double hl = Math.Max(sel, hover * 0.45);
            using (var p = Theme.RoundRect(r, 10))
            {
                using (var b = new SolidBrush(Theme.Mix(Theme.Card, Theme.CardHi, hl))) g.FillPath(b, p);
                using (var pen = new Pen(sel > 0.5 ? Theme.Alpha(Theme.Accent, 160) : Theme.Alpha(Color.White, 14))) g.DrawPath(pen, p);
            }
            if (hl > 0.001)
            {
                if (sel > 0.001)
                {
                    int h = (int)((r.Height - Theme.Px(20)) * sel);
                    using (var b = new LinearGradientBrush(new Rectangle(r.X, r.Y, 3, r.Height), Theme.SoftGreen, Theme.Mint, 90f))
                        g.FillRectangle(b, r.X, r.Y + r.Height / 2 - h / 2, 3, Math.Max(1, h));
                }
            }
            Func<double, int> P = v => Theme.Px(v);
            var c = it.StateColor;
            int cx = r.X + P(20), cy = r.Y + r.Height / 2;
            if (it.State == "online")
            {
                double k = 0.5 + 0.5 * Math.Sin(pulse + index);
                float sz = (float)((12 + 8 * k) * Theme.S);
                using (var b = new SolidBrush(Color.FromArgb((int)(25 + 45 * (1 - k)), c)))
                    g.FillEllipse(b, cx - sz / 2, cy - sz / 2, sz, sz);
            }
            using (var b = new SolidBrush(c)) g.FillEllipse(b, cx - P(4), cy - P(4), P(8), P(8));
            int top = r.Y + (r.Height - P(42)) / 2;
            TextRenderer.DrawText(g, it.Name, Theme.F(10f, FontStyle.Bold), new Rectangle(r.X + P(36), top, r.Width - P(42), P(22)), Theme.Text,
                TextFormatFlags.EndEllipsis | TextFormatFlags.Left | TextFormatFlags.NoPrefix);
            string sub = it.StateText;
            if (it.Running) sub += "  ·  " + (int)J.N(it.Stats, "online") + "/" + (int)J.N(it.Stats, "max");
            if (it.Port > 0) sub += "  ·  :" + it.Port;
            TextRenderer.DrawText(g, sub, Theme.F(8.5f), new Rectangle(r.X + P(36), top + P(22), r.Width - P(42), P(20)), Theme.Muted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.Left);
        }
        async Task LoadServers()
        {
            SetStatus("Lade Server…", Theme.Muted);
            var r = await api.Get("/api/v2/servers");
            if (!r.Ok) { SetStatus("Server konnten nicht geladen werden: " + r.Error, Theme.Red); return; }
            var selId = cur != null ? cur.Id : null;
            var old = servers.ToDictionary(s => s.Id, s => s);
            servers.Clear();
            foreach (var o in J.L(r.Data))
            {
                var id = J.S(o, "server_id");
                if (string.IsNullOrEmpty(id)) continue;
                ServerItem it;
                if (!old.TryGetValue(id, out it)) it = new ServerItem { Id = id };
                it.Name = J.S(o, "server_name") ?? id;
                it.Type = J.S(o, "type") ?? "";
                it.Port = (int)J.N(o, "server_port");
                it.XmxBytes = ServerItem.ParseXmx(J.S(o, "execution_command"));
                servers.Add(it);
            }
            servers.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            list.SetItems(servers);
            SetStatus(servers.Count + " Server geladen", Theme.Muted);
            await RefreshStats();
            statsTimer.Start();
            var keep = servers.FirstOrDefault(s => s.Id == selId) ?? servers.FirstOrDefault();
            if (keep != null) list.SelectedItem = keep;
        }

        async Task RefreshStats()
        {
            if (statsBusy || servers.Count == 0) return;
            statsBusy = true;
            try
            {
                var snapshot = servers.ToList();
                var tasks = snapshot.Select(s => api.Get("/api/v2/servers/" + s.Id + "/stats")).ToList();
                var results = await Task.WhenAll(tasks);
                for (int i = 0; i < snapshot.Count; i++)
                {
                    if (results[i].Ok) snapshot[i].Stats = J.D(results[i].Data);
                }
                list.Invalidate();
                int on = servers.Count(s => s.Running);
                int players = servers.Where(s => s.Running).Sum(s => (int)J.N(s.Stats, "online"));
                summary.Text = on + " von " + servers.Count + " Servern online\n" + players + " Spieler gesamt (inkl. Proxy)";
                UpdateOverview();
            }
            finally { statsBusy = false; }
        }

        async Task SelectServer(ServerItem it)
        {
            if (it == null || it == cur) return;
            if (dirty && !Dialogs.Confirm(this, "Ungespeichert", "Die geöffnete Datei hat ungespeicherte Änderungen. Verwerfen?"))
            {
                list.SelectedItem = cur;
                return;
            }
            cur = it;
            // reset per-server state
            logPrev = new List<string>();
            logLoaded = false;
            console.Clear();
            curDir = "";
            CloseEditor();
            fileList.Items.Clear();
            UpdateOverview();
            if (pageConsole.Visible) await PollLog();
            if (pageFiles.Visible) await LoadDir("");
        }

        void UpdateOverview()
        {
            if (cur == null) return;
            var s = cur.Stats;
            hName.Text = cur.Name;
            hPill.Left = hName.Right + 12;
            hPill.Set(cur.StateText, cur.StateColor);
            string type = cur.Type;
            if (type.StartsWith("minecraft-")) type = "Minecraft " + type.Substring(10).Replace("java", "Java").Replace("bedrock", "Bedrock");
            hSub.Text = type + (cur.Port > 0 ? "  ·  Port " + cur.Port : "") + "  ·  ID " + cur.Id;

            bool running = cur.Running;
            bStart.Enabled = !running;
            bStop.Enabled = running;
            bRestart.Enabled = running;
            bKill.Enabled = running;

            cStatus.Set(cur.StateText, running ? "Läuft seit " + Util.Uptime(J.S(s, "started")) : "", cur.StateColor);
            double cpu = J.N(s, "cpu");
            cCpu.Set(running ? cpu.ToString("0.#", CultureInfo.InvariantCulture) + " %" : "–", running ? "Auslastung" : "", Theme.Text);
            cCpu.Bar = running ? cpu / 100.0 : -1;
            cCpu.BarColor = cpu > 85 ? Theme.Red : cpu > 60 ? Theme.Yellow : Theme.SoftGreen;
            double memp = J.N(s, "mem_percent");
            var memText = FormatMem(J.Get(s, "mem"));
            double rss = ServerItem.ParseSize(memText);
            string memSub = cur.XmxBytes > 0 ? "Heap-Limit (Xmx) " + Theme.Bytes(cur.XmxBytes) : memp.ToString("0.#", CultureInfo.InvariantCulture) + " % vom System";
            cRam.Set(running ? (rss > 0 ? Theme.Bytes(rss) : memText) : "–", running ? memSub : (cur.XmxBytes > 0 ? "Xmx " + Theme.Bytes(cur.XmxBytes) : ""), Theme.Text);
            double ratio = cur.XmxBytes > 0 && rss > 0 ? rss / (cur.XmxBytes * 1.35) : memp / 100.0;
            cRam.Bar = running ? ratio : -1;
            cRam.BarColor = ratio > 0.9 ? Theme.Red : ratio > 0.75 ? Theme.Yellow : Theme.Accent;
            int online = (int)J.N(s, "online"), max = (int)J.N(s, "max");
            cPlayers.Set(running ? online + " / " + max : "–", running ? "online" : "", Theme.Text);
            cPlayers.Bar = running && max > 0 ? (double)online / max : -1;
            cPlayers.BarColor = Theme.Green;
            var ver = J.S(s, "version");
            cVersion.Set(string.IsNullOrEmpty(ver) || ver == "False" ? "–" : ver, StripMotd(J.S(s, "desc")), Theme.Text);
            var ws = J.S(s, "world_size");
            cWorld.Set(string.IsNullOrEmpty(ws) ? "–" : ws, "Weltgröße", Theme.Text);
            foreach (var c in new[] { cCpu, cRam, cPlayers }) c.Invalidate();

            var players = cur.Players;
            playersTitle.Text = "SPIELER ONLINE  (" + players.Count + ")";
            var sel = playerList.SelectedItem as string;
            var currentItems = playerList.Items.Cast<object>().Select(o => o.ToString()).ToList();
            if (!currentItems.SequenceEqual(players))
            {
                playerList.BeginUpdate();
                playerList.Items.Clear();
                foreach (var p in players) playerList.Items.Add(p);
                if (sel != null && players.Contains(sel)) playerList.SelectedItem = sel;
                playerList.EndUpdate();
            }
        }

        /// <summary>Crafty reports "mem" as raw bytes (number or numeric string); older versions send "2.1GB".</summary>
        static string FormatMem(object v)
        {
            if (v == null) return "–";
            var s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim();
            double b;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out b)) return Theme.Bytes(b);
            return s;
        }

        static string StripMotd(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "False") return "";
            s = Util.StripAnsi(s).Replace("\n", " ").Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, @"<[^>]+>|&[0-9a-fk-or]|&#[0-9a-fA-F]{6}", "");
            return s.Length > 30 ? s.Substring(0, 30) + "…" : s;
        }

        string SelectedPlayer()
        {
            var p = playerList.SelectedItem as string;
            if (p == null) SetStatus("Bitte zuerst einen Spieler auswählen.", Theme.Yellow);
            return p;
        }

        // ───────────────────────────── Actions ─────────────────────────────
        async Task Action(string action, string label)
        {
            if (cur == null) return;
            var target = cur;
            SetStatus(label + " für " + target.Name + " wird gesendet…", Theme.Muted);
            var r = await api.Send("POST", "/api/v2/servers/" + target.Id + "/action/" + action, "", "application/json");
            if (r.Ok) SetStatus(label + " für " + target.Name + " gesendet.", Theme.Green);
            else SetStatus(label + " fehlgeschlagen: " + r.Error, Theme.Red);
            await Task.Delay(1200);
            await RefreshStats();
        }

        async Task Backup()
        {
            if (cur == null) return;
            var target = cur;
            var r = await api.Get("/api/v2/servers/" + target.Id + "/backups");
            if (!r.Ok) { SetStatus("Backups konnten nicht geladen werden: " + r.Error, Theme.Red); return; }
            var backups = J.L(r.Data).ToList();
            if (backups.Count == 0) { SetStatus("Für diesen Server ist in Crafty kein Backup eingerichtet.", Theme.Yellow); return; }
            var b = backups.FirstOrDefault(x => J.B(x, "default")) ?? backups[0];
            var name = J.S(b, "backup_name") ?? "Backup";
            if (!Dialogs.Confirm(this, "Backup", "Backup „" + name + "“ für " + target.Name + " jetzt starten?")) return;
            var a = await api.Send("POST", "/api/v2/servers/" + target.Id + "/action/backup_server/" + J.S(b, "backup_id"), "", "application/json");
            if (a.Ok) SetStatus("Backup „" + name + "“ gestartet.", Theme.Green);
            else SetStatus("Backup fehlgeschlagen: " + a.Error, Theme.Red);
        }

        async Task SendCommand(string cmd)
        {
            if (cur == null || string.IsNullOrWhiteSpace(cmd)) return;
            if (!cur.Running) { SetStatus("Der Server ist offline – Befehl nicht gesendet.", Theme.Yellow); return; }
            var r = await api.Send("POST", "/api/v2/servers/" + cur.Id + "/stdin", cmd, "text/plain; charset=utf-8");
            if (r.Ok) SetStatus("Befehl gesendet: " + cmd, Theme.Green);
            else SetStatus("Befehl fehlgeschlagen: " + r.Error, Theme.Red);
        }

        async Task SendFromBox()
        {
            var cmd = cmdField.Box.Text.Trim();
            if (cmd.StartsWith("/")) cmd = cmd.Substring(1);
            if (cmd == "") return;
            if (history.Count == 0 || history[history.Count - 1] != cmd) history.Add(cmd);
            historyPos = history.Count;
            cmdField.Box.Text = "";
            AppendLog(new List<string> { "> " + cmd });
            await SendCommand(cmd);
            await Task.Delay(400);
            await PollLog();
        }

        // ───────────────────────────── Console ─────────────────────────────
        async Task PollLog()
        {
            if (cur == null || logBusy || !pageConsole.Visible) return;
            logBusy = true;
            var target = cur;
            try
            {
                var r = await api.Get("/api/v2/servers/" + target.Id + "/logs?raw=true");
                if (target != cur) return;
                if (!r.Ok) { if (!logLoaded) SetStatus("Konsole: " + r.Error, Theme.Red); return; }
                var lines = J.L(r.Data).Select(o => Util.StripAnsi(Convert.ToString(o)).TrimEnd('\r', '\n')).ToList();
                if (!logLoaded && lines.Count == 0)
                {
                    var f = await api.Get("/api/v2/servers/" + target.Id + "/logs?file=true&raw=true");
                    if (target != cur) return;
                    if (f.Ok)
                    {
                        var fl = J.L(f.Data).Select(o => Util.StripAnsi(Convert.ToString(o)).TrimEnd('\r', '\n')).ToList();
                        if (fl.Count > 0)
                        {
                            AppendLog(new List<string> { "── Letzte Zeilen aus logs/latest.log (Server offline) ──" });
                            AppendLog(fl);
                        }
                    }
                    logLoaded = true;
                    return;
                }
                var fresh = Util.NewLines(logPrev, lines);
                if (fresh == null)
                {
                    console.Clear();
                    fresh = lines;
                }
                logPrev = lines;
                logLoaded = true;
                AppendLog(fresh);
            }
            finally { logBusy = false; }
        }

        static Color LineColor(string l)
        {
            if (l.StartsWith("> ")) return Theme.SoftGreen;
            if (l.StartsWith("──")) return Theme.Muted;
            if (l.Contains("ERROR") || l.Contains("SEVERE") || l.Contains("Exception") || l.StartsWith("\tat ") || l.StartsWith("Caused by")) return Theme.Red;
            if (l.Contains("WARN")) return Theme.Yellow;
            if (l.Contains("joined the game") || l.Contains("left the game") || l.Contains("has connected") || l.Contains("logged in")) return Theme.Green;
            if (l.Contains("issued server command")) return Theme.Accent;
            return Theme.Text2;
        }

        void AppendLog(List<string> lines)
        {
            if (lines.Count == 0) return;
            const int WM_SETREDRAW = 0x0B;
            Theme.SendMessage(console.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            try
            {
                foreach (var l in lines)
                {
                    console.SelectionStart = console.TextLength;
                    console.SelectionLength = 0;
                    console.SelectionColor = LineColor(l);
                    console.AppendText(l + "\n");
                }
                int count = console.GetLineFromCharIndex(console.TextLength);
                if (count > 3000)
                {
                    int idx = console.GetFirstCharIndexFromLine(count - 2500);
                    if (idx > 0)
                    {
                        console.ReadOnly = false;
                        console.Select(0, idx);
                        console.SelectedText = "";
                        console.ReadOnly = true;
                    }
                }
            }
            finally
            {
                Theme.SendMessage(console.Handle, WM_SETREDRAW, new IntPtr(1), IntPtr.Zero);
                console.Invalidate();
            }
            if (autoScroll.Checked)
            {
                console.SelectionStart = console.TextLength;
                console.SelectionLength = 0;
                console.ScrollToCaret();
            }
        }

        // ───────────────────────────── Files ─────────────────────────────
        async Task LoadDir(string path)
        {
            if (cur == null) return;
            var target = cur;
            var r = await api.PostJson("/api/v2/servers/" + target.Id + "/files", new Dictionary<string, object> { { "path", path } });
            if (target != cur) return;
            if (!r.Ok) { SetStatus("Ordner konnte nicht geladen werden: " + r.Error, Theme.Red); return; }
            var data = J.D(r.Data);
            if (data == null || !data.ContainsKey("root_path")) { SetStatus("Das ist kein Ordner.", Theme.Yellow); return; }
            curDir = path;
            pathLabel.Text = "/" + path;
            fileList.BeginUpdate();
            fileList.Items.Clear();
            foreach (var kv in data)
            {
                if (kv.Key == "root_path") continue;
                bool isDir = J.B(kv.Value, "dir");
                var item = new ListViewItem(kv.Key, isDir ? "dir" : "file");
                item.SubItems.Add(isDir ? "" : (J.S(kv.Value, "size") ?? ""));
                item.SubItems.Add(J.S(kv.Value, "modified") ?? "");
                item.Tag = new object[] { J.S(kv.Value, "path") ?? (path == "" ? kv.Key : path + "/" + kv.Key), isDir, J.Get(kv.Value, "can_open") };
                fileList.Items.Add(item);
            }
            fileList.EndUpdate();
            SetStatus(fileList.Items.Count + " Einträge in /" + path, Theme.Muted);
        }

        async Task GoUp()
        {
            if (curDir == "") return;
            int i = curDir.LastIndexOf('/');
            await LoadDir(i < 0 ? "" : curDir.Substring(0, i));
        }

        async Task OpenSelected()
        {
            if (fileList.SelectedItems.Count == 0) return;
            var tag = (object[])fileList.SelectedItems[0].Tag;
            var path = ((string)tag[0]).Replace('\\', '/');
            if ((bool)tag[1]) { await LoadDir(path); return; }
            if (tag[2] is bool && !(bool)tag[2])
            {
                SetStatus("Diese Datei ist binär und kann nicht im Editor geöffnet werden.", Theme.Yellow);
                return;
            }
            if (dirty && !Dialogs.Confirm(this, "Ungespeichert", "Ungespeicherte Änderungen an " + openPath + " verwerfen?")) return;
            await OpenFile(path);
        }

        async Task OpenFile(string path)
        {
            var target = cur;
            SetStatus("Öffne " + path + "…", Theme.Muted);
            var r = await api.PostJson("/api/v2/servers/" + target.Id + "/files", new Dictionary<string, object> { { "path", path } });
            if (target != cur) return;
            if (!r.Ok) { SetStatus("Datei konnte nicht geöffnet werden: " + r.Error, Theme.Red); return; }
            var content = J.S(r.Data, "content");
            if (content == null) { SetStatus("Keine Textdatei.", Theme.Yellow); return; }
            openPath = path;
            openEpoch = J.Get(J.Get(r.Data, "attributes"), "modified_epoch");
            openCrlf = content.Contains("\r\n");
            suppressDirty = true;
            editor.Text = openCrlf ? content : content.Replace("\n", "\r\n");
            editor.SelectionStart = 0;
            editor.ScrollToCaret();
            suppressDirty = false;
            editor.ReadOnly = !Can("files.edit");
            dirty = false;
            bSave.Enabled = Can("files.edit");
            bReload.Enabled = true;
            UpdateEditorTitle();
            SetStatus("Geöffnet: " + path + "  (" + (J.S(J.Get(r.Data, "attributes"), "size") ?? "") + ")", Theme.Muted);
            editor.Focus();
        }

        void CloseEditor()
        {
            openPath = null;
            openEpoch = null;
            dirty = false;
            suppressDirty = true;
            editor.Text = "";
            suppressDirty = false;
            editor.ReadOnly = true;
            bSave.Enabled = false;
            bReload.Enabled = false;
            UpdateEditorTitle();
        }

        void UpdateEditorTitle()
        {
            editorTitle.Text = openPath == null ? "Keine Datei geöffnet" : (dirty ? "● " : "") + openPath;
            editorTitle.ForeColor = dirty ? Theme.Yellow : Theme.Text2;
        }

        async Task SaveFile()
        {
            if (openPath == null || cur == null) return;
            var text = editor.Text;
            if (!openCrlf) text = text.Replace("\r\n", "\n");
            var body = new Dictionary<string, object> { { "path", openPath }, { "contents", text } };
            if (openEpoch != null) body["modified_epoch"] = openEpoch;
            bSave.Enabled = false;
            try
            {
                var r = await api.Send("PATCH", "/api/v2/servers/" + cur.Id + "/files", J.ToJson(body), "application/json");
                if (r.Status == 409)
                {
                    SetStatus("Die Datei wurde zwischenzeitlich auf dem Server geändert – nicht gespeichert.", Theme.Yellow);
                    MessageBox.Show(this, "Die Datei wurde seit dem Öffnen auf dem Server geändert (z. B. vom Plugin selbst).\n\nKopiere deine Änderungen, klicke auf „Neu laden“ und füge sie wieder ein.",
                        "Konflikt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                if (!r.Ok) { SetStatus("Speichern fehlgeschlagen: " + r.Error, Theme.Red); return; }
                var ep = J.Get(J.Get(r.Data, "attributes"), "modified_epoch");
                if (ep != null) openEpoch = ep;
                dirty = false;
                UpdateEditorTitle();
                SetStatus("Gespeichert: " + openPath + (cur.Running ? "  –  Hinweis: Plugin neu laden oder Server neu starten, damit es wirkt." : ""), Theme.Green);
            }
            finally { bSave.Enabled = openPath != null; }
        }

        // ───────────────────────────── Upload / file management ─────────────────────────────
        static string RJoin(string a, string b)
        {
            a = (a ?? "").Trim('/');
            b = (b ?? "").Trim('/');
            return a.Length == 0 ? b : b.Length == 0 ? a : a + "/" + b;
        }

        static string Rel(string root, string full)
        {
            var r = full.Substring(root.TrimEnd('\\').Length).TrimStart('\\');
            return r.Replace('\\', '/');
        }

        async Task<bool> MakeDir(string parent, string name, bool report)
        {
            var r = await api.Send("PUT", "/api/v2/servers/" + cur.Id + "/files/create",
                J.ToJson(new Dictionary<string, object> { { "parent", parent }, { "name", name }, { "directory", true } }), "application/json");
            if (r.Ok) { if (report) SetStatus("Ordner erstellt: /" + RJoin(parent, name), Theme.Green); return true; }
            if ((J.S(r.Json, "error") ?? "") == "FILE EXISTS") return true;
            SetStatus("Ordner „" + name + "“ konnte nicht erstellt werden: " + r.Error, Theme.Red);
            return false;
        }

        async Task UploadPaths(string[] paths)
        {
            if (cur == null) return;
            var target = cur;
            var baseDir = curDir;
            var dirs = new List<string>();
            var files = new List<KeyValuePair<string, string>>();
            try
            {
                foreach (var p in paths)
                {
                    if (Directory.Exists(p))
                    {
                        var root = Path.GetFileName(p.TrimEnd('\\'));
                        dirs.Add(RJoin(baseDir, root));
                        foreach (var d in Directory.GetDirectories(p, "*", SearchOption.AllDirectories))
                            dirs.Add(RJoin(baseDir, root + "/" + Rel(p, d)));
                        foreach (var f in Directory.GetFiles(p, "*", SearchOption.AllDirectories))
                        {
                            var relDir = Rel(p, Path.GetDirectoryName(f));
                            files.Add(new KeyValuePair<string, string>(f, RJoin(baseDir, RJoin(root, relDir))));
                        }
                    }
                    else if (File.Exists(p)) files.Add(new KeyValuePair<string, string>(p, baseDir));
                }
            }
            catch (Exception ex) { SetStatus("Ordner konnte nicht gelesen werden: " + ex.Message, Theme.Red); return; }

            const long maxSize = 95L * 1024 * 1024;
            var tooBig = files.Where(f => new FileInfo(f.Key).Length > maxSize).ToList();
            files = files.Except(tooBig).ToList();
            long total = files.Sum(f => new FileInfo(f.Key).Length);
            if (files.Count == 0 && dirs.Count == 0) { SetStatus("Nichts zum Hochladen.", Theme.Yellow); return; }
            var msg = (files.Count > 0 ? files.Count + " Datei(en) (" + Theme.Bytes(total) + ")" : "") +
                      (dirs.Count > 0 ? (files.Count > 0 ? " und " : "") + dirs.Count + " Ordner" : "") +
                      " nach /" + baseDir + " auf „" + target.Name + "“ hochladen?\n\nVorhandene Dateien mit gleichem Namen werden überschrieben." +
                      (tooBig.Count > 0 ? "\n\n" + tooBig.Count + " Datei(en) über 95 MB werden übersprungen (Crafty-Limit) – die bitte per WinSCP hochladen." : "") +
                      (target.Running && files.Any(f => f.Key.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)) ? "\n\nHinweis: Neue Plugins werden erst nach einem Neustart geladen." : "");
            if (!Dialogs.Confirm(this, "Hochladen", msg)) return;

            fileProgress.Set(0);
            int done = 0, failed = 0;
            string lastErr = null;
            try
            {
                foreach (var d in dirs.Distinct().OrderBy(x => x.Count(c => c == '/')))
                {
                    int i = d.LastIndexOf('/');
                    if (!await MakeDir(i < 0 ? "" : d.Substring(0, i), i < 0 ? d : d.Substring(i + 1), false)) failed++;
                }
                long sent = 0;
                foreach (var f in files)
                {
                    if (target != cur) break;
                    var name = Path.GetFileName(f.Key);
                    SetStatus("Lade hoch: " + name + "  (" + (done + 1) + "/" + files.Count + ")", Theme.Muted);
                    byte[] bytes;
                    try { bytes = File.ReadAllBytes(f.Key); }
                    catch (Exception ex) { failed++; lastErr = ex.Message; continue; }
                    var headers = new Dictionary<string, string>
                    {
                        { "fileName", name }, { "location", f.Value.Length == 0 ? "." : f.Value },
                        { "fileSize", bytes.Length.ToString() }, { "fileId", Guid.NewGuid().ToString() }
                    };
                    var r = await api.SendRaw("POST", "/api/v2/servers/" + target.Id + "/files/upload", bytes, "application/octet-stream", headers, 600000);
                    if (!r.Ok) { failed++; lastErr = name + ": " + r.Error; }
                    done++;
                    sent += bytes.Length;
                    fileProgress.Set(total > 0 ? (double)sent / total : 1);
                }
            }
            finally { fileProgress.Set(-1); }
            await LoadDir(baseDir);
            if (failed == 0) SetStatus("Hochgeladen: " + done + " Datei(en), " + dirs.Count + " Ordner nach /" + baseDir, Theme.Green);
            else SetStatus(failed + " Fehler beim Hochladen – " + lastErr, Theme.Red);
        }

        object[] SelectedFileTag()
        {
            if (fileList.SelectedItems.Count == 0) { SetStatus("Bitte zuerst eine Datei oder einen Ordner auswählen.", Theme.Yellow); return null; }
            return (object[])fileList.SelectedItems[0].Tag;
        }

        async Task DeleteSelected()
        {
            var tag = SelectedFileTag();
            if (tag == null || cur == null) return;
            var path = ((string)tag[0]).Replace('\\', '/');
            bool dir = (bool)tag[1];
            if (!Dialogs.Confirm(this, "Löschen", (dir ? "Ordner" : "Datei") + " /" + path + (dir ? " mit ALLEM Inhalt" : "") + " endgültig löschen?")) return;
            if (openPath == path) CloseEditor();
            var r = await api.Send("DELETE", "/api/v2/servers/" + cur.Id + "/files",
                J.ToJson(new Dictionary<string, object> { { "file_system_objects", new[] { new Dictionary<string, object> { { "filename", path } } } } }), "application/json");
            if (r.Ok) SetStatus("Gelöscht: /" + path, Theme.Green);
            else SetStatus("Löschen fehlgeschlagen: " + r.Error, Theme.Red);
            await LoadDir(curDir);
        }

        async Task RenameSelected()
        {
            var tag = SelectedFileTag();
            if (tag == null || cur == null) return;
            var path = ((string)tag[0]).Replace('\\', '/');
            var oldName = path.Substring(path.LastIndexOf('/') + 1);
            var n = Dialogs.Ask(this, "Umbenennen", "Neuer Name für " + oldName + ":", oldName);
            if (string.IsNullOrWhiteSpace(n) || n.Trim() == oldName) return;
            if (n.Contains("/") || n.Contains("\\")) { SetStatus("Der Name darf keine / enthalten.", Theme.Red); return; }
            var r = await api.Send("PATCH", "/api/v2/servers/" + cur.Id + "/files/create",
                J.ToJson(new Dictionary<string, object> { { "path", path }, { "new_name", n.Trim() } }), "application/json");
            if (r.Ok) SetStatus("Umbenannt in " + n.Trim(), Theme.Green);
            else SetStatus("Umbenennen fehlgeschlagen: " + r.Error, Theme.Red);
            await LoadDir(curDir);
        }

        // ───────────────────────────── Metrics ─────────────────────────────
        Panel BuildMetrics()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Bg };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            chPlayers = new LineChart { Title = "Spieler online", LineColor = Theme.Green };
            chCpu = new LineChart { Title = "CPU", Unit = " %", LineColor = Theme.SoftGreen };
            chRam = new LineChart { Title = "RAM", Unit = " %", LineColor = Theme.Accent, FixedMax = 100 };
            int i = 0;
            foreach (var ch in new[] { chPlayers, chCpu, chRam })
            {
                var card = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(4, 4, 6, 10), Margin = new Padding(0, 0, 12, 12) };
                ch.Dock = DockStyle.Fill;
                card.Controls.Add(ch);
                grid.Controls.Add(card, i % 2, i / 2);
                i++;
            }
            var infoCard = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(20), Margin = new Padding(0, 0, 12, 12) };
            metricsInfo = Theme.Lbl("", Theme.F(10f), Theme.Text2, Color.Transparent);
            metricsInfo.Location = new Point(20, 18);
            var reload = new FlatBtn("↻ Aktualisieren", Theme.CardHi) { Location = new Point(20, 150) };
            reload.Click += async (s, e) => await LoadMetrics();
            infoCard.Controls.Add(metricsInfo);
            infoCard.Controls.Add(reload);
            grid.Controls.Add(infoCard, 1, 1);
            page.Controls.Add(grid);
            return page;
        }

        async Task LoadMetrics()
        {
            if (cur == null) return;
            var target = cur;
            metricsInfo.Text = "Lade Verlauf …";
            var r = await api.Get("/api/v2/servers/" + target.Id + "/history");
            if (target != cur) return;
            if (!r.Ok) { metricsInfo.Text = "Verlauf nicht verfügbar: " + r.Error; return; }
            var players = new List<double?>();
            var cpu = new List<double?>();
            var ram = new List<double?>();
            var labels = new List<string>();
            Func<object, double?> num = o =>
            {
                if (o == null) return null;
                double d;
                return double.TryParse(Convert.ToString(o, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out d) ? (double?)d : null;
            };
            Func<string, string> lbl = s =>
            {
                DateTime t;
                if (s != null && (DateTime.TryParseExact(s, "yyyy/MM/dd, HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out t) ||
                                  DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out t)))
                    return t.Date == DateTime.Today ? t.ToString("HH:mm") : t.ToString("dd.MM. HH:mm");
                return s ?? "";
            };
            var data = r.Data;
            if (J.D(data) != null && J.Get(data, "dates") != null)
            {
                labels = J.L(J.Get(data, "dates")).Select(o => lbl(Convert.ToString(o))).ToList();
                players = J.L(J.Get(data, "players")).Select(num).ToList();
                cpu = J.L(J.Get(data, "cpu")).Select(num).ToList();
                ram = J.L(J.Get(data, "ram_percent")).Select(num).ToList();
            }
            else
            {
                foreach (var o in J.L(data))
                {
                    labels.Add(lbl(J.S(o, "created")));
                    players.Add(num(J.Get(o, "online")));
                    cpu.Add(num(J.Get(o, "cpu")));
                    ram.Add(num(J.Get(o, "mem_percent")));
                }
            }
            var lb = labels.ToArray();
            chPlayers.SetData(players.ToArray(), lb, true);
            chCpu.SetData(cpu.ToArray(), lb, true);
            chRam.SetData(ram.ToArray(), lb, true);
            Func<List<double?>, string> peak = l => { var v = l.Where(x => x.HasValue).Select(x => x.Value).ToList(); return v.Count == 0 ? "–" : v.Max().ToString("0.#", CultureInfo.InvariantCulture); };
            Func<List<double?>, string> avg = l => { var v = l.Where(x => x.HasValue).Select(x => x.Value).ToList(); return v.Count == 0 ? "–" : v.Average().ToString("0.#", CultureInfo.InvariantCulture); };
            metricsInfo.Text = "Verlauf von Crafty  ·  " + labels.Count + " Messpunkte\n" +
                               (labels.Count > 0 ? "Zeitraum: " + labels.First() + "  –  " + labels.Last() + "\n\n" : "\n") +
                               "Spieler:  Spitze " + peak(players) + "   ·   Schnitt " + avg(players) + "\n" +
                               "CPU:        Spitze " + peak(cpu) + " %   ·   Schnitt " + avg(cpu) + " %\n" +
                               "RAM:        Spitze " + peak(ram) + " %   ·   Schnitt " + avg(ram) + " %";
        }

        // ───────────────────────────── Administration ─────────────────────────────
        Panel BuildAdmin()
        {
            var page = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 52, BackColor = Theme.Bg, WrapContents = false };
            var bBroadcast = new FlatBtn("📢  Nachricht an alle Server", Theme.Accent) { Height = 38, Margin = new Padding(0, 0, 10, 0) };
            bBroadcast.Text = "Nachricht an alle Server";
            var bWlOn = new FlatBtn("Whitelist an", Theme.CardHi) { Height = 38, Margin = new Padding(0, 0, 10, 0) };
            var bWlOff = new FlatBtn("Whitelist aus", Theme.CardHi) { Height = 38, Margin = new Padding(0, 0, 10, 0) };
            var bRef = new FlatBtn("↻", Theme.CardHi) { Height = 38, Width = 42 };
            bar.Controls.AddRange(new Control[] { bBroadcast, bWlOn, bWlOff, bRef });
            bBroadcast.Click += async (s, e) => await Broadcast();
            bWlOn.Click += async (s, e) => { if (Dialogs.Confirm(this, "Whitelist", "Whitelist auf „" + cur.Name + "“ einschalten? Nur eingetragene Spieler kommen dann rein.")) await SendCommand("whitelist on"); };
            bWlOff.Click += async (s, e) => await SendCommand("whitelist off");
            bRef.Click += async (s, e) => await LoadAdmin();

            var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
            admBans = AdminList(grid, 0, 0, "Gebannte Spieler", "Bannen", "Entbannen",
                n => "ban " + n, n => "pardon " + n, "Spielername und Grund (z. B. „Steve Hacking“):");
            admIpBans = AdminList(grid, 1, 0, "Gebannte IPs", "IP bannen", "IP entbannen",
                n => "ban-ip " + n, n => "pardon-ip " + n, "IP-Adresse oder Spielername:");
            admOps = AdminList(grid, 0, 1, "Operatoren (OP)", "OP geben", "OP entziehen",
                n => "op " + n, n => "deop " + n, "Spielername:");
            admWhite = AdminList(grid, 1, 1, "Whitelist", "Hinzufügen", "Entfernen",
                n => "whitelist add " + n, n => "whitelist remove " + n, "Spielername:");
            page.Controls.Add(grid);
            page.Controls.Add(bar);
            return page;
        }

        ListBox AdminList(TableLayoutPanel grid, int col, int row, string title, string addText, string removeText,
                          Func<string, string> addCmd, Func<string, string> removeCmd, string prompt)
        {
            var card = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(14, 46, 14, 58), Margin = new Padding(0, 0, 12, 12) };
            var t = Theme.Lbl(title.ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Color.Transparent);
            t.Location = new Point(16, 16);
            var lb = new ListBox
            {
                Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text,
                Font = Theme.F(10f), IntegralHeight = false, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 28
            };
            lb.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0;
                using (var b = new SolidBrush(sel ? Theme.CardHi : Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                var text = lb.Items[e.Index].ToString();
                var tab = text.IndexOf('\t');
                TextRenderer.DrawText(e.Graphics, tab < 0 ? text : text.Substring(0, tab), lb.Font, new Point(e.Bounds.X + 8, e.Bounds.Y + 5), Theme.Text);
                if (tab >= 0)
                    TextRenderer.DrawText(e.Graphics, text.Substring(tab + 1), Theme.F(8.75f), new Rectangle(e.Bounds.X + 170, e.Bounds.Y, e.Bounds.Width - 176, e.Bounds.Height),
                        Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            var add = new FlatBtn(addText, Theme.CardHi) { Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Height = 34 };
            var rem = new FlatBtn(removeText, Theme.CardHi) { Anchor = AnchorStyles.Left | AnchorStyles.Bottom, Height = 34 };
            card.Resize += (s, e) => { add.Location = new Point(14, card.Height - 50); rem.Location = new Point(add.Right + 8, card.Height - 50); };
            add.Click += async (s, e) =>
            {
                var v = Dialogs.Ask(this, title, prompt, "");
                if (string.IsNullOrWhiteSpace(v)) return;
                await SendCommand(addCmd(v.Trim()));
                await Task.Delay(800);
                await LoadAdmin();
            };
            rem.Click += async (s, e) =>
            {
                if (lb.SelectedItem == null) { SetStatus("Bitte zuerst einen Eintrag auswählen.", Theme.Yellow); return; }
                var v = lb.SelectedItem.ToString().Split('\t')[0];
                if (!Dialogs.Confirm(this, title, removeText + ": " + v + "?")) return;
                await SendCommand(removeCmd(v));
                await Task.Delay(800);
                await LoadAdmin();
            };
            card.Controls.Add(lb);
            card.Controls.Add(t);
            card.Controls.Add(add);
            card.Controls.Add(rem);
            grid.Controls.Add(card, col, row);
            return lb;
        }

        async Task LoadAdmin()
        {
            if (cur == null) return;
            var target = cur;
            var files = new[] { "banned-players.json", "banned-ips.json", "ops.json", "whitelist.json" };
            var boxes = new[] { admBans, admIpBans, admOps, admWhite };
            for (int i = 0; i < files.Length; i++)
            {
                var r = await api.PostJson("/api/v2/servers/" + target.Id + "/files", new Dictionary<string, object> { { "path", files[i] } });
                if (target != cur) return;
                var box = boxes[i];
                box.BeginUpdate();
                box.Items.Clear();
                if (r.Ok)
                {
                    object arr = null;
                    try { arr = J.Parse(J.S(r.Data, "content") ?? "[]"); } catch { }
                    foreach (var o in J.L(arr))
                    {
                        var name = J.S(o, "name") ?? J.S(o, "ip") ?? "?";
                        var extra = i == 2 ? "Level " + J.S(o, "level") : (J.S(o, "reason") ?? "") + (J.S(o, "expires") != null && J.S(o, "expires") != "forever" ? "  · bis " + J.S(o, "expires") : "");
                        box.Items.Add(name + "\t" + extra.Trim());
                    }
                }
                else box.Items.Add("(nicht verfügbar)\t" + (J.S(r.Json, "error") ?? r.Error));
                box.EndUpdate();
            }
        }

        async Task Broadcast()
        {
            var targets = servers.Where(s => s.Running && !(s.Name ?? "").ToLowerInvariant().Contains("velocity") &&
                                             !(J.S(s.Stats, "version") ?? "").ToLowerInvariant().Contains("velocity")).ToList();
            if (targets.Count == 0) { SetStatus("Kein laufender Minecraft-Server gefunden.", Theme.Yellow); return; }
            var m = Dialogs.Ask(this, "Nachricht an alle Server", "Wird per /say auf " + targets.Count + " Server(n) gesendet (" + string.Join(", ", targets.Select(t => t.Name)) + "):", "");
            if (string.IsNullOrWhiteSpace(m)) return;
            int ok = 0;
            foreach (var t in targets)
            {
                var r = await api.Send("POST", "/api/v2/servers/" + t.Id + "/stdin", "say " + m.Trim(), "text/plain; charset=utf-8");
                if (r.Ok) ok++;
            }
            SetStatus("Nachricht an " + ok + "/" + targets.Count + " Server gesendet.", ok == targets.Count ? Theme.Green : Theme.Yellow);
        }

        // ───────────────────────────── Session ─────────────────────────────
        void SessionExpired()
        {
            if (closingForLogin || IsDisposed) return;
            closingForLogin = true;
            statsTimer.Stop();
            logTimer.Stop();
            if (team != null) st.TeamToken = null; else st.Token = null;
            st.Save();
            MessageBox.Show(this, team != null ? "Deine Team-Sitzung wurde beendet (abgelaufen, vom Owner abgemeldet oder von einer anderen IP benutzt). Bitte melde dich erneut an." : "Deine Sitzung ist abgelaufen. Bitte melde dich erneut an.", "RyvenPanel", MessageBoxButtons.OK, MessageBoxIcon.Information);
            dirty = false;
            DialogResult = DialogResult.Retry;
        }

        void Logout()
        {
            if (dirty && !Dialogs.Confirm(this, "Abmelden", "Ungespeicherte Änderungen verwerfen und abmelden?")) return;
            closingForLogin = true;
            dirty = false;
            if (team != null) { st.TeamToken = null; var t = Task.Run(() => api.Send("POST", "/panel/logout", "", "application/json")); t.Wait(3000); } // Task.Run: no UI-context continuations, else Wait always runs into the 3 s timeout
            else st.Token = null;
            st.Save();
            DialogResult = DialogResult.Retry;
        }

        void OnClosingCheck(object sender, FormClosingEventArgs e)
        {
            if (dirty && !Dialogs.Confirm(this, "Beenden", "Ungespeicherte Änderungen an " + openPath + " verwerfen?"))
            {
                e.Cancel = true;
                return;
            }
            if (!closingForLogin && docker.HasUnsaved && !Dialogs.Confirm(this, "Beenden", "Ungespeicherte Änderungen am Stack verwerfen?"))
            {
                e.Cancel = true;
                return;
            }
            if (!closingForLogin && files.HasUnsaved && !Dialogs.Confirm(this, "Beenden", "Ungespeicherte Änderungen im Datei-Editor verwerfen?"))
            {
                e.Cancel = true;
                return;
            }
            terminal.CloseAll();
            docker.Deactivate();
            permTimer.Stop();
            updateTimer.Stop();
            statsTimer.Stop();
            logTimer.Stop();
        }
    }
}
