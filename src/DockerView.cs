using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    class RowTag
    {
        public string Key;
        public Color Dot = Color.Empty;
        public object Data;
        public Image Icon;
    }

    class DockerView : Panel
    {
        readonly Settings st;
        readonly Ssh ssh;
        readonly Action<string, Color> setStatus;

        Panel setupPanel, body, content;
        Label setupInfo, hSub;
        TextBox pubKeyBox;
        Field fHost, fUser, fPort;
        TabBtn[] tabs;
        Panel[] pages;
        int page;
        RichTextBox output;
        Label outTitle;
        Panel outPanel;
        FlatBtn outToggle;
        bool outOpen;   // starts collapsed, opens with the first output
        readonly Timer timer = new Timer { Interval = 4000 };
        bool busy, active, connected;

        ListView cList;
        CheckBox liveLogs;
        ListView sList;
        TextBox sEditor;
        Label sTitle;
        string sFile, sProject;
        string[] sFiles;
        bool sDirty, sSuppress, sNew;
        FlatBtn sSave, sDeploy, sStop, sDown, sUpdate, sLogs;
        ListView iList, nList, vList;
        StatCard hCpu, hRam, hDisk, hCont;
        Label hInfo;

        public DockerView(Settings st, Action<string, Color> setStatus)
        {
            this.st = st;
            this.setStatus = setStatus;
            ssh = new Ssh
            {
                Host = st.SshHost,
                User = st.SshUser,
                Port = st.SshPort,
                KeyPath = string.IsNullOrEmpty(st.SshKey) ? Ssh.DefaultKey : st.SshKey
            };
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            BuildSetup();
            BuildBody();
            Controls.Add(body);
            Controls.Add(setupPanel);
            body.Visible = false;
            timer.Tick += async (s, e) => await Tick();
        }

        public bool HasUnsaved { get { return sDirty; } }
        public Ssh Ssh { get { return ssh; } }

        // ───────────────────────────── Public lifecycle ─────────────────────────────
        public async void Activate()
        {
            active = true;
            if (!connected) await TestConnection(false);
            if (connected)
            {
                timer.Start();
                await RefreshPage();
            }
        }

        public async void GoTab(int i) { if (connected) await ShowPage(i); else SetActiveTab(i); }

        public void Deactivate()
        {
            active = false;
            timer.Stop();
        }

        async Task Tick()
        {
            if (!active || !connected || busy) return;
            if (page == 0 || page == 4) await RefreshPage();
            if (page == 0 && liveLogs.Checked) await ShowLogs(false);
        }

        // ───────────────────────────── Setup (SSH key) ─────────────────────────────
        void BuildSetup()
        {
            setupPanel = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            var card = new RoundPanel { Location = new Point(0, 10), Size = new Size(760, 620) };
            setupPanel.Controls.Add(card);

            var title = Theme.Lbl("Docker-Verbindung einrichten", Theme.F(17f, FontStyle.Bold), Theme.Text, card.Fill);
            title.Location = new Point(26, 22);
            var info = Theme.Lbl(
                "Die App steuert Docker über SSH mit einem eigenen Schlüssel – ohne Passwort im Programm und ohne offenen Docker-Port.\n" +
                "Der private Schlüssel bleibt auf diesem PC (" + ssh.KeyPath + ").",
                Theme.F(9.5f), Theme.Muted, card.Fill);
            info.Location = new Point(28, 62);
            info.MaximumSize = new Size(700, 0);
            card.Controls.Add(title);
            card.Controls.Add(info);

            int y = 120;
            fHost = SetupField(card, "Server", 28, y, 330, st.SshHost);
            fUser = SetupField(card, "Benutzer", 372, y, 170, st.SshUser);
            fPort = SetupField(card, "SSH-Port", 556, y, 100, st.SshPort.ToString());
            y += 86;

            var b1 = new FlatBtn("1.  Schlüssel erstellen", Theme.CardHi) { Location = new Point(28, y), Width = 210, Height = 40 };
            var b2 = new FlatBtn("2.  Auf Server installieren", Theme.CardHi) { Location = new Point(248, y), Width = 230, Height = 40 };
            var b3 = new FlatBtn("3.  Verbindung testen", Theme.Accent) { Location = new Point(488, y), Width = 200, Height = 40 };
            card.Controls.AddRange(new Control[] { b1, b2, b3 });
            y += 56;

            var kl = Theme.Lbl("ÖFFENTLICHER SCHLÜSSEL (darf man teilen)", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, card.Fill);
            kl.Location = new Point(28, y);
            card.Controls.Add(kl);
            pubKeyBox = new TextBox
            {
                Location = new Point(28, y + 22),
                Size = new Size(560, 60),
                Multiline = true,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Input,
                ForeColor = Theme.Text2,
                Font = Theme.Mono
            };
            var copy = new FlatBtn("Kopieren", Theme.CardHi) { Location = new Point(598, y + 22), Width = 90 };
            card.Controls.Add(pubKeyBox);
            card.Controls.Add(copy);
            y += 100;

            setupInfo = Theme.Lbl("", Theme.F(9.5f), Theme.Text2, card.Fill);
            setupInfo.Location = new Point(28, y);
            setupInfo.MaximumSize = new Size(700, 0);
            card.Controls.Add(setupInfo);
            y += 120;

            var help = Theme.Lbl(
                "Schritt 2 öffnet ein Konsolenfenster. Dort gibst du einmal das root-Passwort des Servers ein, dann wird der Schlüssel\n" +
                "in /root/.ssh/authorized_keys eingetragen. Alternativ kopierst du den Schlüssel selbst per WinSCP dorthin.\n\n" +
                "Tipp: Wenn der Schlüssel funktioniert, solltest du den Passwort-Login für root abschalten (PermitRootLogin prohibit-password).",
                Theme.F(9f), Theme.Muted, card.Fill);
            help.Location = new Point(28, y);
            help.MaximumSize = new Size(700, 0);
            card.Controls.Add(help);

            b1.Click += async (s, e) =>
            {
                ApplySetupFields();
                if (ssh.KeyExists && !Dialogs.Confirm(FindForm(), "Schlüssel", "Es gibt schon einen Schlüssel. Trotzdem behalten? (Nein = abbrechen)")) return;
                if (ssh.KeyExists) { UpdateSetupInfo(); return; }
                var r = await ssh.CreateKey();
                setupInfo.ForeColor = r.Ok ? Theme.Green : Theme.Red;
                setupInfo.Text = r.Ok ? "Schlüssel erstellt. Jetzt Schritt 2." : "Fehler: " + r.Message;
                UpdateSetupInfo();
            };
            b2.Click += (s, e) =>
            {
                ApplySetupFields();
                if (!ssh.KeyExists) { setupInfo.ForeColor = Theme.Yellow; setupInfo.Text = "Bitte zuerst Schritt 1."; return; }
                ssh.InstallKeyInteractive();
                setupInfo.ForeColor = Theme.Text2;
                setupInfo.Text = "Konsole geöffnet – Passwort eingeben. Danach Schritt 3.";
            };
            b3.Click += async (s, e) => { ApplySetupFields(); await TestConnection(true); };
            copy.Click += (s, e) => { if (!string.IsNullOrEmpty(pubKeyBox.Text)) Clipboard.SetText(pubKeyBox.Text); };
            setupPanel.Resize += (s, e) => card.Left = Math.Max(0, (setupPanel.ClientSize.Width - card.Width) / 2);
            UpdateSetupInfo();
        }

        Field SetupField(Control parent, string label, int x, int y, int w, string value)
        {
            var l = Theme.Lbl(label.ToUpperInvariant(), Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Card);
            l.Location = new Point(x, y);
            var f = new Field(false) { Location = new Point(x, y + 22), Size = new Size(w, 40) };
            f.Box.Text = value;
            parent.Controls.Add(l);
            parent.Controls.Add(f);
            return f;
        }

        void ApplySetupFields()
        {
            int port;
            ssh.Host = fHost.Box.Text.Trim();
            ssh.User = fUser.Box.Text.Trim();
            if (int.TryParse(fPort.Box.Text.Trim(), out port) && port > 0 && port < 65536) ssh.Port = port;
            st.SshHost = ssh.Host;
            st.SshUser = ssh.User;
            st.SshPort = ssh.Port;
            st.SshKey = ssh.KeyPath;
            st.Save();
        }

        void UpdateSetupInfo()
        {
            pubKeyBox.Text = ssh.PublicKey ?? "(noch kein Schlüssel – Schritt 1)";
        }

        async Task TestConnection(bool fromSetup)
        {
            if (!ssh.KeyExists)
            {
                ShowSetup("Noch kein SSH-Schlüssel vorhanden – starte mit Schritt 1.", Theme.Text2);
                return;
            }
            if (fromSetup) { setupInfo.ForeColor = Theme.Text2; setupInfo.Text = "Teste Verbindung…"; }
            var r = await ssh.Run("docker version --format '{{.Server.Version}}' && docker compose version --short", 30000);
            if (r.Ok)
            {
                connected = true;
                var v = r.Out.Trim().Split('\n');
                hSub.Text = ssh.User + "@" + ssh.Host + "  ·  Docker " + v[0].Trim() + (v.Length > 1 ? "  ·  Compose " + v[1].Trim() : "");
                setupPanel.Visible = false;
                body.Visible = true;
                setStatus("Docker verbunden: " + ssh.Host, Theme.Green);
                if (active) { timer.Start(); await RefreshPage(); }
            }
            else
            {
                connected = false;
                ShowSetup("Verbindung fehlgeschlagen: " + r.Message, Theme.Red);
            }
        }

        void ShowSetup(string msg, Color c)
        {
            body.Visible = false;
            setupPanel.Visible = true;
            setupInfo.ForeColor = c;
            setupInfo.Text = msg;
            UpdateSetupInfo();
        }

        // ───────────────────────────── Body layout ─────────────────────────────
        void BuildBody()
        {
            body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var header = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Theme.Bg };
            var title = Theme.Lbl("Docker & Infrastruktur", Theme.F(17f, FontStyle.Bold), Theme.Text, Theme.Bg);
            title.Location = new Point(0, 0);
            hSub = Theme.Lbl("", Theme.F(9.5f), Theme.Muted, Theme.Bg);
            hSub.Location = new Point(2, 36);
            var settingsBtn = new FlatBtn("SSH-Einstellungen", Theme.CardHi) { Anchor = AnchorStyles.Top | AnchorStyles.Right, Width = 150 };
            header.Resize += (s, e) => settingsBtn.Location = new Point(header.Width - settingsBtn.Width, 6);
            settingsBtn.Click += (s, e) => { timer.Stop(); connected = false; ShowSetup("Einstellungen ändern und mit Schritt 3 neu verbinden.", Theme.Text2); };
            header.Controls.Add(title);
            header.Controls.Add(hSub);
            header.Controls.Add(settingsBtn);
            var names = new[] { "Container", "Stacks", "Images", "Netzwerke & Volumes", "Host" };
            tabs = new TabBtn[names.Length];
            int x = 0;
            for (int i = 0; i < names.Length; i++)
            {
                int idx = i;
                tabs[i] = new TabBtn(names[i]) { Location = new Point(x, 62) };
                x = tabs[i].Right + 4;
                tabs[i].Click += async (s, e) => await ShowPage(idx);
                header.Controls.Add(tabs[i]);
            }
            header.Controls.Add(new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Border });

            content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 16, 0, 0) };
            pages = new[] { BuildContainers(), BuildStacks(), BuildImages(), BuildNetVol(), BuildHost() };
            foreach (var p in pages) content.Controls.Add(p);

            outPanel = new Panel { Dock = DockStyle.Bottom, Height = 220, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0) };
            var outBox = new RoundPanel { Dock = DockStyle.Fill, Fill = Theme.Input, BorderColor = Theme.Border, Padding = new Padding(14, 36, 6, 10) };
            outTitle = Theme.Lbl("AUSGABE", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Input);
            outTitle.Location = new Point(14, 12);
            output = new RichTextBox
            {
                Dock = DockStyle.Fill,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Input,
                ForeColor = Theme.Text2,
                Font = Theme.Mono,
                ReadOnly = true,
                DetectUrls = false,
                WordWrap = false
            };
            outBox.Controls.Add(output);
            outBox.Controls.Add(outTitle);
            outToggle = new FlatBtn("Ausgabe einklappen", Theme.CardHi) { Height = 26, Font = Theme.F(8f, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right }.Tip("Ausgabe-Bereich ein-/ausklappen – öffnet sich automatisch bei neuer Ausgabe");
            outBox.Controls.Add(outToggle);
            outBox.Resize += (s, e) => outToggle.Location = new Point(outBox.Width - outToggle.Width - Theme.Px(12), Theme.Px(6));
            outToggle.Click += (s, e) => SetOut(!outOpen);
            body.Resize += (s, e) => SetOut(outOpen);
            outPanel.Controls.Add(outBox);

            body.Controls.Add(content);
            body.Controls.Add(outPanel);
            body.Controls.Add(header);
            SetActiveTab(0);
        }

        void SetActiveTab(int i)
        {
            page = i;
            for (int k = 0; k < pages.Length; k++)
            {
                pages[k].Visible = k == i;
                tabs[k].Active = k == i;
                tabs[k].Invalidate();
            }
        }

        async Task ShowPage(int i)
        {
            if (page == 1 && i != 1 && sDirty && !Dialogs.Confirm(FindForm(), "Ungespeichert", "Ungespeicherte Änderungen am Stack verwerfen?")) return;
            if (page == 1 && i != 1) { sDirty = false; }
            SetActiveTab(i);
            await RefreshPage();
        }

        async Task RefreshPage()
        {
            if (!connected || busy) return;
            busy = true;
            try
            {
                switch (page)
                {
                    case 0: await LoadContainers(); break;
                    case 1: await LoadStacks(); break;
                    case 2: await LoadImages(); break;
                    case 3: await LoadNetVol(); break;
                    case 4: await LoadHost(); break;
                }
            }
            finally { busy = false; }
        }

        /// <summary>Output area: collapsed = only its title bar; open = about a third of the page (scales with the window).</summary>
        void SetOut(bool open)
        {
            outOpen = open;
            int h = open ? Math.Max(Theme.Px(150), Math.Min(Theme.Px(320), (int)(body.Height * 0.32))) : Theme.Px(56);
            if (outPanel.Height != h) outPanel.Height = h;
            output.Visible = open;
            outToggle.Text = open ? "Ausgabe einklappen" : "Ausgabe anzeigen";
            outToggle.Width = Theme.Px(Theme.Measure(outToggle.Text, outToggle.Font).Width + 24);
        }

        void SetOutput(string title, string text, bool error)
        {
            outTitle.Text = title.ToUpperInvariant();
            if (!outOpen) SetOut(true);
            output.Clear();
            output.SelectionColor = error ? Theme.Red : Theme.Text2;
            output.AppendText((text ?? "").Replace("\r\n", "\n").TrimEnd() + "\n");
            output.SelectionStart = output.TextLength;
            output.ScrollToCaret();
        }

        async Task<SshResult> Exec(string label, string script, int timeoutMs = 120000, bool showOutput = true)
        {
            setStatus(label + " …", Theme.Muted);
            UseWaitCursor = true;
            SshResult r;
            try { r = await ssh.Run(script, timeoutMs); }
            finally { UseWaitCursor = false; }
            if (r.Ok) setStatus(label + " – erledigt.", Theme.Green);
            else setStatus(label + " – fehlgeschlagen (Code " + r.Exit + ").", Theme.Red);
            if (showOutput || !r.Ok)
            {
                var text = (r.Out ?? "") + (string.IsNullOrWhiteSpace(r.Err) ? "" : (r.Out.Length > 0 ? "\n" : "") + r.Err);
                SetOutput(label, text.Trim().Length == 0 ? (r.Ok ? "OK" : r.Message) : text, !r.Ok);
            }
            return r;
        }

        // ───────────────────────────── List helper ─────────────────────────────
        static ListView MakeList(string[] cols, int[] widths)
        {
            var lv = new ListView
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
                HideSelection = false,
                SmallImageList = new ImageList { ImageSize = new Size(1, Theme.Px(28)) }
            };
            typeof(Control).GetProperty("DoubleBuffered", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(lv, true, null);
            for (int i = 0; i < cols.Length; i++) lv.Columns.Add(cols[i], widths[i]);
            lv.DrawColumnHeader += (s, e) =>
            {
                using (var b = new SolidBrush(Theme.Card)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, e.Header.Text.ToUpperInvariant(), Theme.F(8f, FontStyle.Bold),
                    new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 12, e.Bounds.Height), Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            lv.DrawItem += (s, e) => { };
            lv.DrawSubItem += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(e.Item.Selected ? Theme.CardHi : Theme.Card)) g.FillRectangle(b, e.Bounds);
                var bounds = e.Bounds;
                var tag = e.Item.Tag as RowTag;
                if (e.ColumnIndex == 0 && tag != null && tag.Dot != Color.Empty)
                {
                    using (var b = new SolidBrush(tag.Dot)) g.FillEllipse(b, bounds.X + 10, bounds.Y + bounds.Height / 2 - 4, 8, 8);
                    bounds = new Rectangle(bounds.X + 20, bounds.Y, bounds.Width - 20, bounds.Height);
                }
                var c = e.ColumnIndex == 0 ? Theme.Text : (e.SubItem.ForeColor == lv.ForeColor ? Theme.Text2 : e.SubItem.ForeColor);
                TextRenderer.DrawText(g, e.SubItem.Text, e.ColumnIndex == 0 ? Theme.F(9.75f, FontStyle.Bold) : lv.Font,
                    new Rectangle(bounds.X + 8, bounds.Y, bounds.Width - 12, bounds.Height), c, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            UiX.FitColumns(lv);

            return lv;
        }

        static ListViewItem Row(RowTag tag, params string[] cells)
        {
            var it = new ListViewItem(cells[0]) { Tag = tag, UseItemStyleForSubItems = false };
            for (int i = 1; i < cells.Length; i++) it.SubItems.Add(cells[i] ?? "");
            return it;
        }

        static void Fill(ListView lv, List<ListViewItem> rows)
        {
            string sel = lv.SelectedItems.Count > 0 ? ((RowTag)lv.SelectedItems[0].Tag).Key : null;
            int top = lv.TopItem != null ? lv.TopItem.Index : 0;
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.Items.AddRange(rows.ToArray());
            if (sel != null)
                foreach (ListViewItem it in lv.Items)
                    if (((RowTag)it.Tag).Key == sel) { it.Selected = true; it.Focused = true; break; }
            lv.EndUpdate();
            if (top > 0 && top < lv.Items.Count) try { lv.TopItem = lv.Items[top]; } catch { }
        }

        static string Selected(ListView lv)
        {
            return lv.SelectedItems.Count > 0 ? ((RowTag)lv.SelectedItems[0].Tag).Key : null;
        }

        static IEnumerable<Dictionary<string, object>> JsonLines(string text)
        {
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var l = raw.Trim();
                if (!l.StartsWith("{")) continue;
                Dictionary<string, object> d = null;
                try { d = J.D(J.Parse(l)); } catch { }
                if (d != null) yield return d;
            }
        }

        static Panel Toolbar(params Control[] items)
        {
            var bar = new WrapBar { Dock = DockStyle.Top, BackColor = Theme.Bg, Padding = new Padding(0, 0, 0, 10) };   // wraps instead of cutting buttons off
            foreach (var c in items)
            {
                c.Margin = new Padding(0, 0, 8, 8);
                bar.Controls.Add(c);
            }
            return bar;
        }

        static FlatBtn Btn(string text, Color c) { return new FlatBtn(text, c) { Height = 36 }; }

        static string DeTime(string s)
        {
            if (s == null) return "";
            var r = s;
            r = Regex.Replace(r, @"^up\s+", "");
            r = Regex.Replace(r, @"About an hour", "ca. 1 Std");
            r = Regex.Replace(r, @"About a minute", "ca. 1 Min");
            r = Regex.Replace(r, @"Less than a second", "< 1 Sek");
            r = Regex.Replace(r, @"\b(\d+) (weeks?)\b", "$1 Wo.");
            r = Regex.Replace(r, @"\b(\d+) (days?)\b", "$1 T");
            r = Regex.Replace(r, @"\b(\d+) (hours?)\b", "$1 Std");
            r = Regex.Replace(r, @"\b(\d+) (minutes?)\b", "$1 Min");
            r = Regex.Replace(r, @"\b(\d+) (seconds?)\b", "$1 Sek");
            r = Regex.Replace(r, @"\b(\d+) (months?)\b", "$1 Mon.");
            r = Regex.Replace(r, @"\b(\d+) (years?)\b", "$1 J.");
            r = Regex.Replace(r, @"\ban? (hour|day|week|month|year|minute)\b", m => "1 " + new Dictionary<string, string> { { "hour", "Std" }, { "day", "T" }, { "week", "Wo." }, { "month", "Mon." }, { "year", "J." }, { "minute", "Min" } }[m.Groups[1].Value]);
            r = r.Replace(" ago", "").Replace("(healthy)", "· gesund").Replace("(unhealthy)", "· ungesund").Replace("(Paused)", "· pausiert");
            return r;
        }

        static string Bytes(double b)
        {
            string[] u = { "B", "KB", "MB", "GB", "TB" };
            int i = 0;
            while (b >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
            return b.ToString(i >= 3 ? "0.0" : "0", CultureInfo.InvariantCulture) + " " + u[i];
        }

        bool IsCritical(string name)
        {
            return name != null && (name.Contains("crafty") || name == "mariadb" || name == "db");
        }

        // ───────────────────────────── Containers ─────────────────────────────
        Panel BuildContainers()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            cList = MakeList(new[] { "Name", "Status", "Image", "CPU", "RAM", "Stack", "Ports" }, new[] { 190, 180, 230, 70, 150, 90, 200 });
            var card = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            card.Controls.Add(cList);

            var bStart = Btn("▶ Start", Theme.Mix(Theme.Green, Theme.Bg, 0.25));
            var bStop = Btn("■ Stop", Theme.Mix(Theme.Red, Theme.Bg, 0.2));
            var bRestart = Btn("↻ Neustart", Theme.Accent);
            var bKill = Btn("✕ Kill", Theme.DarkRed);
            var bRm = Btn("Löschen", Theme.CardHi);
            var bLogs = Btn("Logs", Theme.CardHi);
            var bExec = Btn("Befehl ausführen", Theme.CardHi);
            var bInspect = Btn("Details", Theme.CardHi);
            liveLogs = new CheckBox { Text = "Live-Logs", ForeColor = Theme.Text2, BackColor = Theme.Bg, AutoSize = true, Padding = new Padding(6, 9, 0, 0) };
            var bar = Toolbar(bStart, bStop, bRestart, bKill, bRm, bLogs, bExec, bInspect, liveLogs);

            bStart.Click += async (s, e) => await CAction("start", "Start");
            bStop.Click += async (s, e) => await CAction("stop", "Stopp");
            bRestart.Click += async (s, e) => await CAction("restart", "Neustart");
            bKill.Click += async (s, e) => await CAction("kill", "Kill");
            bRm.Click += async (s, e) => await CAction("rm -f", "Löschen");
            bLogs.Click += async (s, e) => await ShowLogs(true);
            cList.ItemActivate += async (s, e) => await ShowLogs(true);
            liveLogs.CheckedChanged += async (s, e) => { if (liveLogs.Checked) await ShowLogs(true); };
            bInspect.Click += async (s, e) =>
            {
                var n = SelectedContainer(); if (n == null) return;
                await Exec("Details: " + n, "docker inspect " + Ssh.Q(n));
            };
            bExec.Click += async (s, e) =>
            {
                var n = SelectedContainer(); if (n == null) return;
                var cmd = Dialogs.Ask(FindForm(), "Befehl im Container", "Shell-Befehl in „" + n + "“ ausführen (sh -c):", "ls -la");
                if (string.IsNullOrWhiteSpace(cmd)) return;
                await Exec(n + " $ " + cmd, "docker exec " + Ssh.Q(n) + " sh -c " + Ssh.B64(cmd) + " 2>&1", 120000);
            };

            p.Controls.Add(card);
            p.Controls.Add(bar);
            return p;
        }

        string SelectedContainer()
        {
            var n = Selected(cList);
            if (n == null) setStatus("Bitte zuerst einen Container auswählen.", Theme.Yellow);
            else if (!Ssh.IsName(n)) { setStatus("Ungültiger Containername.", Theme.Red); return null; }
            return n;
        }

        async Task CAction(string verb, string label)
        {
            var n = SelectedContainer();
            if (n == null) return;
            if (verb != "start")
            {
                string warn = IsCritical(n) ? "\n\nACHTUNG: Das ist ein zentraler Container (" + n + "). " + (n.Contains("crafty") ? "Alle Minecraft-Server darin werden mitbetroffen!" : "Andere Dienste hängen davon ab.") : "";
                if (verb == "rm -f") warn += "\n\nDer Container wird entfernt. Daten in Volumes/Bind-Mounts bleiben erhalten, alles andere im Container ist weg.";
                if (!Dialogs.Confirm(FindForm(), label, label + " für Container „" + n + "“?" + warn)) return;
            }
            await Exec(label + ": " + n, "docker " + verb + " " + Ssh.Q(n) + " 2>&1", 180000);
            await RefreshPage();
        }

        async Task ShowLogs(bool announce)
        {
            var n = Selected(cList);
            if (n == null || !Ssh.IsName(n)) { if (announce) setStatus("Bitte zuerst einen Container auswählen.", Theme.Yellow); return; }
            var r = await ssh.Run("docker logs --tail 300 " + Ssh.Q(n) + " 2>&1", 30000);
            var text = Util.StripAnsi(r.Ok ? r.Out : r.Message);
            outTitle.Text = ("Logs: " + n + (liveLogs.Checked ? "  (live)" : "")).ToUpperInvariant();
            if (!outOpen) SetOut(true);
            output.Text = text.Replace("\r\n", "\n");
            output.SelectionStart = output.TextLength;
            output.ScrollToCaret();
        }

        async Task LoadContainers()
        {
            var r = await ssh.Run("docker ps -a --no-trunc --format '{{json .}}'; echo '@@STATS'; docker stats --no-stream --format '{{json .}}'", 45000);
            if (!r.Ok) { setStatus("Container konnten nicht geladen werden: " + r.Message, Theme.Red); return; }
            var parts = r.Out.Split(new[] { "@@STATS" }, StringSplitOptions.None);
            var stats = new Dictionary<string, Dictionary<string, object>>();
            if (parts.Length > 1)
                foreach (var d in JsonLines(parts[1])) { var nm = J.S(d, "Name"); if (nm != null) stats[nm] = d; }
            var rows = new List<ListViewItem>();
            foreach (var d in JsonLines(parts[0]).OrderBy(x => J.S(x, "Names")))
            {
                var name = J.S(d, "Names") ?? "?";
                var state = J.S(d, "State") ?? "";
                var statusText = J.S(d, "Status") ?? "";
                Color dot = state == "running" ? Theme.Green
                    : state == "paused" || state == "restarting" || state == "created" ? Theme.Yellow
                    : Regex.IsMatch(statusText, @"Exited \((0|137|143)\)") ? Theme.Muted : Theme.Red;
                if (statusText.Contains("unhealthy")) dot = Theme.Yellow;
                string stTxt = state == "running" ? "Läuft · " + DeTime(statusText.Replace("Up ", ""))
                    : state == "exited" ? "Beendet " + DeTime(Regex.Replace(statusText, @"^Exited ", ""))
                    : state == "restarting" ? "Startet neu…" : state == "paused" ? "Pausiert" : state == "created" ? "Erstellt" : statusText;
                Dictionary<string, object> s;
                stats.TryGetValue(name, out s);
                var labels = J.S(d, "Labels") ?? "";
                var m = Regex.Match(labels, @"com\.docker\.compose\.project=([^,]+)");
                var row = Row(new RowTag { Key = name, Dot = dot, Data = d },
                    name, stTxt, J.S(d, "Image"),
                    s != null ? J.S(s, "CPUPerc") : "",
                    s != null ? (J.S(s, "MemUsage") ?? "").Replace("GiB", " GB").Replace("MiB", " MB").Replace(" / ", " / ") : "",
                    m.Success ? m.Groups[1].Value : "–",
                    ShortPorts(J.S(d, "Ports")));
                if (s != null)
                {
                    double cpu;
                    if (double.TryParse((J.S(s, "CPUPerc") ?? "").TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out cpu))
                        row.SubItems[3].ForeColor = cpu > 150 ? Theme.Red : cpu > 80 ? Theme.Yellow : Theme.Text2;
                }
                rows.Add(row);
            }
            Fill(cList, rows);
            int running = rows.Count(x => ((RowTag)x.Tag).Dot == Theme.Green);
            setStatus(rows.Count + " Container, " + running + " laufen", Theme.Muted);
        }

        static string ShortPorts(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            var list = new List<string>();
            foreach (var part in p.Split(new[] { ", " }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!part.Contains("->") || part.StartsWith("[::]")) continue;
                var s = part.Replace("0.0.0.0:", "").Replace("->", "→");
                if (!list.Contains(s)) list.Add(s);
            }
            return list.Count == 0 ? "intern" : string.Join(", ", list);
        }

        // ───────────────────────────── Stacks ─────────────────────────────
        Panel BuildStacks()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12, FixedPanel = FixedPanel.Panel1 };
            split.Panel1.BackColor = Theme.Bg;
            split.Panel2.BackColor = Theme.Bg;
            UiX.Ratio(split, 0.3, 240, 360);

            var left = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 58, 8, 8) };
            var bNew = new FlatBtn("+  Neuer Stack", Theme.Accent) { Location = new Point(10, 10), Size = new Size(150, 36) };
            var bRef = new FlatBtn("↻", Theme.CardHi) { Location = new Point(168, 10), Size = new Size(38, 36) };
            sList = MakeList(new[] { "Stack", "Status" }, new[] { 150, 140 });
            left.Controls.Add(sList);
            left.Controls.Add(bNew);
            left.Controls.Add(bRef);
            split.Panel1.Controls.Add(left);

            var right = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 104, 6, 10) };
            sTitle = Theme.Lbl("Stack auswählen oder neu anlegen", Theme.F(10f, FontStyle.Bold), Theme.Text2, right.Fill);
            sTitle.AutoSize = false;
            sTitle.AutoEllipsis = true;
            sTitle.Location = new Point(14, 10);
            sTitle.Size = new Size(600, 30);
            sTitle.TextAlign = ContentAlignment.MiddleLeft;
            var actions = new FlowLayoutPanel { Location = new Point(10, 50), Size = new Size(900, 44), BackColor = Theme.Card, WrapContents = false };
            sSave = Btn("Speichern", Theme.Accent);
            sDeploy = Btn("▶ Deploy (up -d)", Theme.Mix(Theme.Green, Theme.Bg, 0.25));
            sUpdate = Btn("⇣ Update (pull + up)", Theme.CardHi);
            sStop = Btn("■ Stoppen", Theme.CardHi);
            sDown = Btn("Entfernen (down)", Theme.Mix(Theme.Red, Theme.Card, 0.35));
            sLogs = Btn("Logs", Theme.CardHi);
            foreach (var b in new[] { sSave, sDeploy, sUpdate, sStop, sDown, sLogs })
            {
                b.Margin = new Padding(0, 0, 8, 0);
                b.Enabled = false;
                actions.Controls.Add(b);
            }
            right.Resize += (s, e) => { actions.Width = right.Width - 20; sTitle.Width = right.Width - 28; };
            sEditor = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                AcceptsTab = false,
                AcceptsReturn = true,
                BorderStyle = BorderStyle.None,
                BackColor = Theme.Input,
                ForeColor = Theme.Text,
                Font = Theme.Mono,
                MaxLength = 0,
                ReadOnly = true
            };
            sEditor.TextChanged += (s, e) => { if (!sSuppress && sFile != null && !sDirty) { sDirty = true; UpdateStackTitle(); } };
            sEditor.KeyDown += async (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; await SaveStack(); }
                else if (e.Control && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; sEditor.SelectAll(); }
                else if (e.KeyCode == Keys.Tab && !e.Control) { e.SuppressKeyPress = true; sEditor.SelectedText = "  "; }
            };
            right.Controls.Add(sEditor);
            right.Controls.Add(sTitle);
            right.Controls.Add(actions);
            split.Panel2.Controls.Add(right);
            p.Controls.Add(split);

            bRef.Click += async (s, e) => await RefreshPage();
            bNew.Click += (s, e) => NewStack();
            sList.SelectedIndexChanged += async (s, e) =>
            {
                var key = Selected(sList);
                if (key == null || key == sProject) return;
                if (sDirty && !Dialogs.Confirm(FindForm(), "Ungespeichert", "Ungespeicherte Änderungen verwerfen?")) return;
                await OpenStack(key);
            };
            sSave.Click += async (s, e) => await SaveStack();
            sDeploy.Click += async (s, e) => await StackCmd("up -d --remove-orphans", "Deploy", false);
            sUpdate.Click += async (s, e) => await StackCmd("pull && $DC up -d --remove-orphans", "Update", true);
            sStop.Click += async (s, e) => await StackCmd("stop", "Stoppen", true);
            sDown.Click += async (s, e) => await StackCmd("down", "Entfernen", true);
            sLogs.Click += async (s, e) => await StackCmd("logs --tail 200 --no-color", "Logs", false);
            return p;
        }

        async Task LoadStacks()
        {
            var r = await ssh.Run("docker compose ls -a --format json; echo; echo '@@DIRS'; for f in /opt/stacks/*/docker-compose.yml; do [ -f \"$f\" ] && echo \"$f\"; done; true", 30000);
            if (!r.Ok) { setStatus("Stacks konnten nicht geladen werden: " + r.Message, Theme.Red); return; }
            var parts = r.Out.Split(new[] { "@@DIRS" }, StringSplitOptions.None);
            var rows = new List<ListViewItem>();
            var known = new HashSet<string>();
            object parsed = null;
            try { parsed = J.Parse(parts[0].Trim().Length == 0 ? "[]" : parts[0].Trim()); } catch { }
            foreach (var o in J.L(parsed))
            {
                var name = J.S(o, "Name");
                if (name == null) continue;
                known.Add(name);
                var status = J.S(o, "Status") ?? "";
                var files = (J.S(o, "ConfigFiles") ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).ToArray();
                var dot = status.StartsWith("running") && !status.Contains("exited") ? Theme.Green : status.Contains("running") ? Theme.Yellow : Theme.Muted;
                var de = Regex.Replace(status, @"running\((\d+)\)", "$1 läuft").Replace("exited", "beendet").Replace("paused", "pausiert").Replace("restarting", "startet neu").Replace("created", "erstellt");
                rows.Add(Row(new RowTag { Key = name, Dot = dot, Data = files }, name, de));
            }
            if (parts.Length > 1)
            {
                foreach (var l in parts[1].Split('\n'))
                {
                    var f = l.Trim();
                    if (!f.StartsWith("/opt/stacks/")) continue;
                    var name = f.Substring(12, f.Length - 12 - "/docker-compose.yml".Length);
                    if (known.Contains(name)) continue;
                    rows.Add(Row(new RowTag { Key = name, Dot = Theme.Border, Data = new[] { f } }, name, "nicht deployed"));
                }
            }
            Fill(sList, rows);
            setStatus(rows.Count + " Stacks", Theme.Muted);
        }

        async Task OpenStack(string key)
        {
            ListViewItem item = null;
            foreach (ListViewItem it in sList.Items) if (((RowTag)it.Tag).Key == key) item = it;
            if (item == null) return;
            var files = (string[])((RowTag)item.Tag).Data;
            if (files.Length == 0 || !files.All(Ssh.IsPath)) { setStatus("Ungültiger Pfad für Stack " + key, Theme.Red); return; }
            var r = await ssh.Run("cat " + Ssh.Q(files[0]), 30000);
            if (!r.Ok) { SetOutput("Stack " + key, r.Message, true); return; }
            sProject = key;
            sFiles = files;
            sFile = files[0];
            sNew = false;
            LoadEditor(r.Out);
        }

        void LoadEditor(string text)
        {
            sSuppress = true;
            sEditor.Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n");
            sEditor.SelectionStart = 0;
            sEditor.ScrollToCaret();
            sSuppress = false;
            sEditor.ReadOnly = false;
            sDirty = sNew;
            foreach (var b in new[] { sSave, sDeploy, sUpdate, sStop, sDown, sLogs }) b.Enabled = true;
            UpdateStackTitle();
        }

        void UpdateStackTitle()
        {
            if (sFile == null) { sTitle.Text = "Stack auswählen oder neu anlegen"; return; }
            sTitle.Text = (sDirty ? "● " : "") + sProject + "   —   " + sFile + (sNew ? "   (neu, noch nicht gespeichert)" : "");
            sTitle.ForeColor = sDirty ? Theme.Yellow : Theme.Text2;
        }

        async Task SaveStack()
        {
            if (sFile == null || !Ssh.IsPath(sFile)) return;
            var text = sEditor.Text.Replace("\r\n", "\n");
            if (!text.EndsWith("\n")) text += "\n";
            var b64 = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(text));
            var script =
                "F=" + Ssh.Q(sFile) + "\n" +
                (sNew ? "[ -e \"$F\" ] && { echo 'Stack existiert bereits.' >&2; exit 4; }\nmkdir -p \"$(dirname \"$F\")\"\n" : "") +
                "TMP=\"$F.pxmx-new\"\n" +
                "echo " + b64 + " | base64 -d > \"$TMP\"\n" +
                "cd \"$(dirname \"$F\")\" && docker compose -f \"$TMP\" config -q 2>&1 || { rm -f \"$TMP\"; echo 'Compose-Datei ungültig – nicht gespeichert.' >&2; exit 3; }\n" +
                "if [ -f \"$F\" ]; then cp -p \"$F\" \"$F.bak-$(date +%Y%m%d-%H%M%S)\"; cat \"$TMP\" > \"$F\"; rm -f \"$TMP\"; else mv \"$TMP\" \"$F\"; fi\n" +
                "echo \"Gespeichert: $F\"";
            var r = await Exec("Speichern: " + sProject, script, 60000);
            if (!r.Ok) return;
            bool wasNew = sNew;
            sNew = false;
            sDirty = false;
            UpdateStackTitle();
            if (wasNew)
            {
                await LoadStacks();
                foreach (ListViewItem it in sList.Items) if (((RowTag)it.Tag).Key == sProject) { it.Selected = true; break; }
            }
        }

        async Task StackCmd(string cmd, string label, bool confirm)
        {
            if (sFile == null || sProject == null) return;
            if (sNew || sDirty)
            {
                if (!Dialogs.Confirm(FindForm(), label, "Es gibt ungespeicherte Änderungen. Erst speichern?")) return;
                await SaveStack();
                if (sDirty || sNew) return;
            }
            if (!Ssh.IsName(sProject) || !sFiles.All(Ssh.IsPath)) { setStatus("Ungültiger Stack-Name oder Pfad.", Theme.Red); return; }
            if (confirm)
            {
                var warn = sProject == "crafty" ? "\n\nACHTUNG: Das ist der Crafty-Stack – alle Minecraft-Server und Velocity gehen dabei offline!" :
                           sProject == "db" ? "\n\nACHTUNG: Das ist die Datenbank – Plugins, die MariaDB nutzen, verlieren die Verbindung." : "";
                if (label == "Entfernen") warn += "\n\n„down“ entfernt die Container und Netzwerke des Stacks. Volumes und Daten-Ordner bleiben erhalten.";
                if (!Dialogs.Confirm(FindForm(), label, label + " für Stack „" + sProject + "“?" + warn)) return;
            }
            // project name and paths are regex-validated (no spaces/quotes), so plain word splitting of $DC is safe
            var dc = "docker compose -p " + sProject + string.Concat(sFiles.Select(f => " -f " + f));
            var dir = sFile.Substring(0, sFile.LastIndexOf('/'));
            var script = "DC=" + Ssh.Q(dc) + "\n{ cd " + Ssh.Q(dir) + " && $DC " + cmd + "; } 2>&1";
            await Exec(label + ": " + sProject, script, 900000);
            await LoadStacks();
        }

        void NewStack()
        {
            if (sDirty && !Dialogs.Confirm(FindForm(), "Ungespeichert", "Ungespeicherte Änderungen verwerfen?")) return;
            string name, template;
            if (!StackTemplates.Ask(FindForm(), out name, out template)) return;
            sProject = name;
            sFile = "/opt/stacks/" + name + "/docker-compose.yml";
            sFiles = new[] { sFile };
            sNew = true;
            sList.SelectedItems.Clear();
            LoadEditor(StackTemplates.Build(template, name));
            SetOutput("Neuer Stack", "Vorlage geladen. Passe Ports, Passwörter und Speicher an, dann „Speichern“ und „Deploy“.\n" +
                      "Der Stack wird unter " + sFile + " angelegt.", false);
        }

        // ───────────────────────────── Images ─────────────────────────────
        Panel BuildImages()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            iList = MakeList(new[] { "Image", "ID", "Größe", "Erstellt" }, new[] { 380, 130, 110, 160 });
            var card = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            card.Controls.Add(iList);
            var bPull = Btn("⇣ Image laden (pull)", Theme.Accent);
            var bRm = Btn("Löschen", Theme.CardHi);
            var bPrune = Btn("Ungenutzte aufräumen", Theme.CardHi);
            var bRef = Btn("↻", Theme.CardHi);
            p.Controls.Add(card);
            p.Controls.Add(Toolbar(bPull, bRm, bPrune, bRef));
            bRef.Click += async (s, e) => await RefreshPage();
            bPull.Click += async (s, e) =>
            {
                var img = Dialogs.Ask(FindForm(), "Image laden", "Image-Name (z. B. itzg/minecraft-server:latest):", "");
                if (string.IsNullOrWhiteSpace(img)) return;
                img = img.Trim();
                if (!Ssh.IsImage(img)) { setStatus("Ungültiger Image-Name.", Theme.Red); return; }
                await Exec("Pull: " + img, "docker pull " + Ssh.Q(img) + " 2>&1", 900000);
                await RefreshPage();
            };
            bRm.Click += async (s, e) =>
            {
                var id = Selected(iList); if (id == null) return;
                if (!Ssh.IsImage(id)) return;
                if (!Dialogs.Confirm(FindForm(), "Image löschen", "Image " + id + " löschen?\nWird es noch von einem Container benutzt, verweigert Docker das.")) return;
                await Exec("Image löschen", "docker rmi " + Ssh.Q(id) + " 2>&1");
                await RefreshPage();
            };
            bPrune.Click += async (s, e) =>
            {
                if (!Dialogs.Confirm(FindForm(), "Aufräumen", "Alle Images ohne Tag und ohne Container löschen (docker image prune)?")) return;
                await Exec("Images aufräumen", "docker image prune -f 2>&1");
                await RefreshPage();
            };
            return p;
        }

        async Task LoadImages()
        {
            var r = await ssh.Run("docker images --format '{{json .}}'", 30000);
            if (!r.Ok) { setStatus("Images: " + r.Message, Theme.Red); return; }
            var rows = new List<ListViewItem>();
            foreach (var d in JsonLines(r.Out))
            {
                var repo = J.S(d, "Repository") + ":" + J.S(d, "Tag");
                var id = J.S(d, "ID");
                rows.Add(Row(new RowTag { Key = id, Dot = repo.Contains("<none>") ? Theme.Muted : Theme.SoftGreen }, repo, id, J.S(d, "Size"), DeTime(J.S(d, "CreatedSince"))));
            }
            Fill(iList, rows);
            setStatus(rows.Count + " Images", Theme.Muted);
        }

        // ───────────────────────────── Networks & volumes ─────────────────────────────
        Panel BuildNetVol()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12 };
            split.Panel1.BackColor = Theme.Bg;
            split.Panel2.BackColor = Theme.Bg;
            UiX.Ratio(split, 0.5, 200, 200);

            nList = MakeList(new[] { "Netzwerk", "Treiber", "Scope" }, new[] { 220, 100, 80 });
            vList = MakeList(new[] { "Volume", "Treiber" }, new[] { 320, 100 });
            var nCard = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            nCard.Controls.Add(nList);
            var vCard = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
            vCard.Controls.Add(vList);
            var bNet = Btn("+ Netzwerk", Theme.Accent);
            var bNetRm = Btn("Netzwerk löschen", Theme.CardHi);
            var bVolRm = Btn("Volume löschen", Theme.Mix(Theme.Red, Theme.Bg, 0.3));
            split.Panel1.Controls.Add(nCard);
            split.Panel1.Controls.Add(Toolbar(bNet, bNetRm));
            split.Panel2.Controls.Add(vCard);
            split.Panel2.Controls.Add(Toolbar(bVolRm));
            p.Controls.Add(split);

            bNet.Click += async (s, e) =>
            {
                var n = Dialogs.Ask(FindForm(), "Netzwerk erstellen", "Name des neuen Bridge-Netzwerks:", "ryven_net");
                if (string.IsNullOrWhiteSpace(n)) return;
                n = n.Trim();
                if (!Ssh.IsName(n)) { setStatus("Ungültiger Name (nur Buchstaben, Zahlen, . _ -).", Theme.Red); return; }
                await Exec("Netzwerk erstellen: " + n, "docker network create " + Ssh.Q(n) + " 2>&1");
                await RefreshPage();
            };
            bNetRm.Click += async (s, e) =>
            {
                var n = Selected(nList); if (n == null || !Ssh.IsName(n)) return;
                if (n == "bridge" || n == "host" || n == "none") { setStatus("Standard-Netzwerke kann man nicht löschen.", Theme.Yellow); return; }
                if (!Dialogs.Confirm(FindForm(), "Netzwerk löschen", "Netzwerk „" + n + "“ löschen?")) return;
                await Exec("Netzwerk löschen: " + n, "docker network rm " + Ssh.Q(n) + " 2>&1");
                await RefreshPage();
            };
            bVolRm.Click += async (s, e) =>
            {
                var v = Selected(vList); if (v == null || !Ssh.IsName(v)) return;
                var typed = Dialogs.Ask(FindForm(), "Volume löschen",
                    "ACHTUNG: Alle Daten im Volume „" + v + "“ werden endgültig gelöscht!\nZum Bestätigen den Namen des Volumes eintippen:", "");
                if (typed == null || typed.Trim() != v) { if (typed != null) setStatus("Name stimmt nicht – nichts gelöscht.", Theme.Yellow); return; }
                await Exec("Volume löschen: " + v, "docker volume rm " + Ssh.Q(v) + " 2>&1");
                await RefreshPage();
            };
            return p;
        }

        async Task LoadNetVol()
        {
            var r = await ssh.Run("docker network ls --format '{{json .}}'; echo '@@VOL'; docker volume ls --format '{{json .}}'", 30000);
            if (!r.Ok) { setStatus("Netzwerke/Volumes: " + r.Message, Theme.Red); return; }
            var parts = r.Out.Split(new[] { "@@VOL" }, StringSplitOptions.None);
            var nrows = JsonLines(parts[0]).Select(d => Row(new RowTag { Key = J.S(d, "Name"), Dot = Theme.Accent }, J.S(d, "Name"), J.S(d, "Driver"), J.S(d, "Scope"))).ToList();
            var vrows = parts.Length > 1 ? JsonLines(parts[1]).Select(d => Row(new RowTag { Key = J.S(d, "Name"), Dot = Theme.SoftGreen }, J.S(d, "Name"), J.S(d, "Driver"))).ToList() : new List<ListViewItem>();
            Fill(nList, nrows);
            Fill(vList, vrows);
            setStatus(nrows.Count + " Netzwerke, " + vrows.Count + " Volumes", Theme.Muted);
        }

        // ───────────────────────────── Host ─────────────────────────────
        Panel BuildHost()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, AutoScroll = true };
            var cards = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Bg };
            hCpu = new StatCard("CPU-Last");
            hRam = new StatCard("Arbeitsspeicher");
            hDisk = new StatCard("Festplatte /");
            hCont = new StatCard("Container");
            foreach (var c in new[] { hCpu, hRam, hDisk, hCont }) { c.Width = 240; cards.Controls.Add(c); }
            var info = new RoundPanel { Dock = DockStyle.Top, Height = 150, Padding = new Padding(18) };
            hInfo = Theme.Lbl("", Theme.F(10f), Theme.Text2, info.Fill);
            hInfo.Location = new Point(18, 16);
            info.Controls.Add(hInfo);
            p.Controls.Add(info);
            p.Controls.Add(cards);
            return p;
        }

        async Task LoadHost()
        {
            var script = "nproc; cat /proc/loadavg; free -b | awk '/^Mem:/{print $2, $3, $7}'; df -B1 --output=size,used,avail / | tail -1; uptime -p; hostname; " +
                         "docker info --format '{{.ContainersRunning}} {{.Containers}} {{.Images}} {{.ServerVersion}}'; . /etc/os-release; echo \"$PRETTY_NAME\"; uname -r";
            var r = await ssh.Run(script, 30000);
            if (!r.Ok) { setStatus("Host: " + r.Message, Theme.Red); return; }
            var l = r.Out.Split('\n').Select(x => x.Trim()).ToArray();
            if (l.Length < 10) return;
            Func<string, double> num = s => { double d; double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d); return d; };
            double cores = num(l[0]);
            var load = l[1].Split(' ');
            double load1 = num(load[0]);
            double pct = cores > 0 ? load1 / cores : 0;
            hCpu.Set((pct * 100).ToString("0", CultureInfo.InvariantCulture) + " %", "Load " + load[0] + " / " + load[1] + " / " + load[2] + "  ·  " + cores + " Kerne", Theme.Text);
            hCpu.Bar = pct;
            hCpu.BarColor = pct > 0.9 ? Theme.Red : pct > 0.7 ? Theme.Yellow : Theme.SoftGreen;
            var mem = l[2].Split(' ');
            double mt = num(mem[0]), mu = num(mem[1]), ma = num(mem.Length > 2 ? mem[2] : "0");
            hRam.Set(Bytes(mt - ma) + " / " + Bytes(mt), Bytes(ma) + " verfügbar", Theme.Text);
            hRam.Bar = mt > 0 ? (mt - ma) / mt : 0;
            hRam.BarColor = hRam.Bar > 0.9 ? Theme.Red : hRam.Bar > 0.75 ? Theme.Yellow : Theme.Accent;
            var df = Regex.Split(l[3], @"\s+");
            double ds = num(df[0]), du = num(df[1]), da = num(df.Length > 2 ? df[2] : "0");
            hDisk.Set(Bytes(du) + " / " + Bytes(ds), Bytes(da) + " frei", Theme.Text);
            hDisk.Bar = ds > 0 ? du / ds : 0;
            hDisk.BarColor = hDisk.Bar > 0.9 ? Theme.Red : hDisk.Bar > 0.8 ? Theme.Yellow : Theme.Green;
            var di = l[6].Split(' ');
            hCont.Set(di[0] + " / " + (di.Length > 1 ? di[1] : "?"), (di.Length > 2 ? di[2] : "?") + " Images  ·  Docker " + (di.Length > 3 ? di[3] : ""), Theme.Text);
            hCont.Bar = di.Length > 1 && num(di[1]) > 0 ? num(di[0]) / num(di[1]) : -1;
            hCont.BarColor = Theme.Green;
            foreach (var c in new[] { hCpu, hRam, hDisk, hCont }) c.Invalidate();
            hInfo.Text = "Hostname:   " + l[5] + "\nSystem:        " + l[7] + "\nKernel:         " + l[8] + "\nLaufzeit:      " + DeTime(l[4]) +
                         "\nVerbindung:  " + ssh.User + "@" + ssh.Host + ":" + ssh.Port;
        }
    }

    static class StackTemplates
    {
        static readonly string[] Names =
        {
            "Minecraft-Server (Paper)",
            "Velocity-Proxy",
            "MariaDB-Datenbank",
            "Redis",
            "Uptime Kuma (Monitoring)",
            "Leer"
        };

        static readonly string[] Info =
        {
            "itzg/minecraft-server – Paper, Daten in ./data. Port nur lokal (127.0.0.1), gedacht für den Betrieb hinter Velocity.",
            "itzg/mc-proxy – Velocity-Proxy, Daten in ./server. Öffentlicher Port 25577.",
            "mariadb:11.4 – Passwörter werden zufällig erzeugt. Port nur lokal erreichbar.",
            "redis:7-alpine – Cache/Messaging, nur lokal erreichbar, mit Passwort.",
            "louislam/uptime-kuma:1 – Status-Seite & Monitoring, nur lokal auf 3001 (per SSH-Tunnel/Reverse-Proxy erreichbar).",
            "Leeres Gerüst zum selbst Ausfüllen."
        };

        public static bool Ask(IWin32Window owner, out string name, out string template)
        {
            name = null;
            template = null;
            using (var f = new DarkForm())
            {
                f.Text = "Neuer Stack";
                f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = f.MinimizeBox = false;
                f.ShowInTaskbar = false;
                f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(520, 330);
                var l1 = Theme.Lbl("NAME (Kleinbuchstaben, Zahlen, - und _)", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Bg);
                l1.Location = new Point(20, 18);
                var field = new Field(false) { Location = new Point(20, 40), Size = new Size(480, 40) };
                var l2 = Theme.Lbl("VORLAGE", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Bg);
                l2.Location = new Point(20, 96);
                var combo = new ComboBox
                {
                    Location = new Point(20, 118),
                    Width = 480,
                    DropDownStyle = ComboBoxStyle.DropDownList,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Theme.Input,
                    ForeColor = Theme.Text,
                    Font = Theme.F(10.5f)
                };
                combo.Items.AddRange(Names);
                combo.SelectedIndex = 0;
                var info = Theme.Lbl(Info[0], Theme.F(9f), Theme.Text2, Theme.Bg);
                info.Location = new Point(20, 162);
                info.MaximumSize = new Size(480, 0);
                combo.SelectedIndexChanged += (s, e) => info.Text = Info[combo.SelectedIndex];
                var err = Theme.Lbl("", Theme.F(9f), Theme.Red, Theme.Bg);
                err.Location = new Point(20, 230);
                var ok = new FlatBtn("Anlegen", Theme.Accent) { Location = new Point(390, 272), Width = 110 };
                var cancel = new FlatBtn("Abbrechen", Theme.CardHi) { Location = new Point(274, 272), Width = 108 };
                cancel.DialogResult = DialogResult.Cancel;
                f.CancelButton = cancel;
                f.AcceptButton = ok;
                string resultName = null;
                ok.Click += (s, e) =>
                {
                    var n = field.Box.Text.Trim();
                    if (!Regex.IsMatch(n, @"^[a-z0-9][a-z0-9_-]{1,40}$")) { err.Text = "Ungültiger Name."; return; }
                    if (n == "crafty" || n == "db") { err.Text = "Der Name ist schon vergeben."; return; }
                    resultName = n;
                    f.DialogResult = DialogResult.OK;
                };
                f.Controls.AddRange(new Control[] { l1, field, l2, combo, info, err, ok, cancel });
                f.Shown += (s, e) => field.Box.Focus();
                if (f.ShowDialog(owner) != DialogResult.OK) return false;
                name = resultName;
                template = Names[combo.SelectedIndex];
                return true;
            }
        }

        static string Secret(int len)
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789";
            var bytes = new byte[len];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(bytes);
            var sb = new StringBuilder();
            foreach (var b in bytes) sb.Append(chars[b % chars.Length]);
            return sb.ToString();
        }

        public static string Build(string template, string name)
        {
            switch (Array.IndexOf(Names, template))
            {
                case 0:
                    return
"# Minecraft-Server (Paper) – Doku: https://docker-minecraft-server.readthedocs.io\n" +
"services:\n" +
"  mc:\n" +
"    image: itzg/minecraft-server:latest\n" +
"    container_name: " + name + "\n" +
"    restart: unless-stopped\n" +
"    tty: true\n" +
"    stdin_open: true\n" +
"    ports:\n" +
"      # nur lokal: Spieler kommen über Velocity rein\n" +
"      - \"127.0.0.1:25570:25565\"\n" +
"    environment:\n" +
"      EULA: \"TRUE\"\n" +
"      TYPE: PAPER\n" +
"      VERSION: LATEST\n" +
"      MEMORY: 4G\n" +
"      ONLINE_MODE: \"FALSE\"   # Velocity übernimmt die Anmeldung\n" +
"      TZ: Europe/Berlin\n" +
"    volumes:\n" +
"      - ./data:/data\n";
                case 1:
                    return
"# Velocity-Proxy – Doku: https://docker-minecraft-server.readthedocs.io/en/latest/misc/related-projects/\n" +
"services:\n" +
"  proxy:\n" +
"    image: itzg/mc-proxy:latest\n" +
"    container_name: " + name + "\n" +
"    restart: unless-stopped\n" +
"    ports:\n" +
"      - \"25577:25577\"\n" +
"    environment:\n" +
"      TYPE: VELOCITY\n" +
"      MEMORY: 512m\n" +
"      TZ: Europe/Berlin\n" +
"    volumes:\n" +
"      - ./server:/server\n";
                case 2:
                    return
"# MariaDB – Passwörter wurden zufällig erzeugt, bitte notieren!\n" +
"services:\n" +
"  db:\n" +
"    image: mariadb:11.4\n" +
"    container_name: " + name + "\n" +
"    restart: unless-stopped\n" +
"    ports:\n" +
"      - \"127.0.0.1:3307:3306\"\n" +
"    environment:\n" +
"      MARIADB_ROOT_PASSWORD: \"" + Secret(24) + "\"\n" +
"      MARIADB_DATABASE: " + name.Replace('-', '_') + "\n" +
"      MARIADB_USER: " + name.Replace('-', '_') + "\n" +
"      MARIADB_PASSWORD: \"" + Secret(24) + "\"\n" +
"      TZ: Europe/Berlin\n" +
"    volumes:\n" +
"      - ./mysql:/var/lib/mysql\n";
                case 3:
                    return
"services:\n" +
"  redis:\n" +
"    image: redis:7-alpine\n" +
"    container_name: " + name + "\n" +
"    restart: unless-stopped\n" +
"    command: [\"redis-server\", \"--appendonly\", \"yes\", \"--requirepass\", \"" + Secret(24) + "\"]\n" +
"    ports:\n" +
"      - \"127.0.0.1:6379:6379\"\n" +
"    volumes:\n" +
"      - ./data:/data\n";
                case 4:
                    return
"services:\n" +
"  kuma:\n" +
"    image: louislam/uptime-kuma:1\n" +
"    container_name: " + name + "\n" +
"    restart: unless-stopped\n" +
"    ports:\n" +
"      - \"127.0.0.1:3001:3001\"\n" +
"    volumes:\n" +
"      - ./data:/app/data\n";
                default:
                    return
"services:\n" +
"  app:\n" +
"    image: \n" +
"    container_name: " + name + "\n" +
"    restart: unless-stopped\n";
            }
        }
    }
}
