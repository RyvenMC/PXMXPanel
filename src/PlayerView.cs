using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    class PlayerView : Panel
    {
        readonly Settings st;
        readonly Func<Ssh> getSsh;
        readonly Func<bool> sshReady;
        readonly Action<string, Color> setStatus;

        List<Session> sessions = new List<Session>();
        List<Session> live = new List<Session>();
        readonly List<double?> liveOnline = new List<double?>();
        readonly List<double?> liveVpn = new List<double?>();
        readonly List<string> liveLabels = new List<string>();

        Label hSub;
        TabBtn[] tabs;
        Panel[] pages;
        int page;
        ProgressLine progress;
        readonly Timer liveTimer = new Timer { Interval = 10000 };
        bool active, loading, liveBusy, loadedOnce, lookupBusy;

        // live
        StatCard lOnline, lVpn, lHosting, lCountries;
        LineChart lChart;
        ListView lList;
        CheckBox autoCheck;
        Label lInfo;

        // history
        Field search;
        ListView pList, sList;
        Label detailTitle, alts;
        StatCard cPlayers, cSessions, cVpn;

        // vpn stats
        StatCard vIps, vChecked, vVpn, vProxy, vTor, vHosting;
        LineChart vChart;
        ListView vProviders, vCountries;

        static readonly Regex LoginRx = new Regex(@"^\[(\d\d):(\d\d):(\d\d)\].*\[connected player\] (\S+) \(/(.+):\d+\) has connected", RegexOptions.Compiled);
        static readonly Regex LogoutRx = new Regex(@"^\[(\d\d):(\d\d):(\d\d)\].*\[connected player\] (\S+) \(/(.+):\d+\) has disconnected(?:: (.*))?$", RegexOptions.Compiled);
        static readonly Regex SwitchRx = new Regex(@"^\[(\d\d):(\d\d):(\d\d)\].*\[server connection\] (\S+) -> (\S+) has connected", RegexOptions.Compiled);
        static readonly Regex BootRx = new Regex(@"^\[(\d\d):(\d\d):(\d\d)\].*Booting up Velocity", RegexOptions.Compiled);

        const string LogPrelude =
"T=$(ls -1 /opt/crafty/docker/servers/*/velocity.toml 2>/dev/null | head -1)\n" +
"[ -z \"$T\" ] && { echo 'Kein Velocity-Server unter /opt/crafty/docker/servers gefunden.' >&2; exit 5; }\n" +
"cd \"$(dirname \"$T\")/logs\" || exit 6\n" +
"P='\\[connected player\\] .* has (connected|disconnected)|\\[server connection\\] .* -> .* has connected|Booting up Velocity'\n";

        public PlayerView(Settings st, Func<Ssh> getSsh, Func<bool> sshReady, Action<string, Color> setStatus)
        {
            this.st = st;
            this.getSsh = getSsh;
            this.sshReady = sshReady;
            this.setStatus = setStatus;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
            liveTimer.Tick += async (s, e) => await RefreshLive();
        }

        public async void Activate()
        {
            active = true;
            liveTimer.Start();
            if (page == 0) await RefreshLive();
            else if (!loadedOnce) await Reload();
        }

        public async void GoTab(int i) { await ShowPage(i); }

        public void Deactivate()
        {
            active = false;
            liveTimer.Stop();
        }

        // ───────────────────────────── Layout ─────────────────────────────
        void Build()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 104, BackColor = Theme.Bg };
            var title = Theme.Lbl("Spieler & VPN", Theme.F(17f, FontStyle.Bold), Theme.Text, Theme.Bg);
            title.Location = new Point(0, 0);
            hSub = Theme.Lbl("Quelle: Velocity-Logs (Join, Quit, Serverwechsel, IP)", Theme.F(9.5f), Theme.Muted, Theme.Bg);
            hSub.Location = new Point(2, 36);
            header.Controls.Add(title);
            header.Controls.Add(hSub);
            var names = new[] { "Live", "Verlauf", "VPN-Statistik" };
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

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 14, 0, 0) };
            pages = new[] { BuildLive(), BuildHistory(), BuildVpnStats() };
            foreach (var p in pages) content.Controls.Add(p);
            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };

            Controls.Add(content);
            Controls.Add(progress);
            Controls.Add(header);
            SetTab(0);
        }

        void SetTab(int i)
        {
            page = i;
            for (int k = 0; k < pages.Length; k++)
            {
                pages[k].Visible = k == i;
                tabs[k].Active = k == i;
            }
        }

        async Task ShowPage(int i)
        {
            SetTab(i);
            if (i == 0) await RefreshLive();
            else if (!loadedOnce) await Reload();
            else if (i == 2) RenderVpnStats();
        }

        static ListView MakeList(string[] cols, int[] widths)
        {
            var lv = new ListView
            {
                Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
                BorderStyle = BorderStyle.None, BackColor = Theme.Card, ForeColor = Theme.Text, Font = Theme.F(9.5f), OwnerDraw = true,
                MultiSelect = false, HideSelection = false, SmallImageList = new ImageList { ImageSize = new Size(1, Theme.Px(28)) }
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
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var tag = e.Item.Tag as RowTag;
                var bg = e.Item.Selected ? Theme.CardHi : Theme.Card;
                if (tag != null && tag.Data is bool && (bool)tag.Data) bg = Theme.Mix(bg, Theme.Red, e.Item.Selected ? 0.16 : 0.09);
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, e.Bounds);
                var bounds = e.Bounds;
                if (e.ColumnIndex == 0 && tag != null && tag.Dot != Color.Empty)
                {
                    using (var b = new SolidBrush(tag.Dot)) g.FillEllipse(b, bounds.X + 10, bounds.Y + bounds.Height / 2 - 4, 8, 8);
                    bounds = new Rectangle(bounds.X + 20, bounds.Y, bounds.Width - 20, bounds.Height);
                }
                var c = e.SubItem.ForeColor == lv.ForeColor ? (e.ColumnIndex == 0 ? Theme.Text : Theme.Text2) : e.SubItem.ForeColor;
                TextRenderer.DrawText(g, e.SubItem.Text, e.ColumnIndex == 0 ? Theme.F(9.5f, FontStyle.Bold) : lv.Font,
                    new Rectangle(bounds.X + 8, bounds.Y, bounds.Width - 12, bounds.Height), c, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
            };
            UiX.FitColumns(lv);

            return lv;
        }

        static void FillList(ListView lv, List<ListViewItem> rows)
        {
            var sel = lv.SelectedItems.Count > 0 ? ((RowTag)lv.SelectedItems[0].Tag).Key : null;
            lv.BeginUpdate();
            lv.Items.Clear();
            lv.Items.AddRange(rows.ToArray());
            if (sel != null) foreach (ListViewItem it in lv.Items) if (((RowTag)it.Tag).Key == sel) { it.Selected = true; break; }
            lv.EndUpdate();
        }

        static ListViewItem Row(RowTag tag, params string[] cells)
        {
            var it = new ListViewItem(cells[0]) { Tag = tag, UseItemStyleForSubItems = false };
            for (int i = 1; i < cells.Length; i++) it.SubItems.Add(cells[i] ?? "");
            return it;
        }

        static Panel CardRow(params StatCard[] cards)
        {
            var row = new CardGrid { Dock = DockStyle.Top, MinCardWidth = 150, CardHeight = 104 };
            foreach (var c in cards) row.Controls.Add(c);
            var wrap = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, BackColor = Theme.Bg };
            return row;
        }

        // ───────────────────────────── Live ─────────────────────────────
        Panel BuildLive()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            lOnline = new StatCard("Online jetzt") { BarColor = Theme.Green };
            lVpn = new StatCard("davon VPN / Proxy / Tor") { BarColor = Theme.Red };
            lHosting = new StatCard("Rechenzentrum-IPs") { BarColor = Theme.Yellow };
            lCountries = new StatCard("Länder") { BarColor = Theme.SoftGreen };
            var cards = CardRow(lOnline, lVpn, lHosting, lCountries);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 46, BackColor = Theme.Bg, WrapContents = false };
            var bNow = new FlatBtn("↻ Jetzt aktualisieren", Theme.CardHi) { Height = 36, Margin = new Padding(0, 0, 12, 0) };
            autoCheck = new CheckBox
            {
                Text = "Neue IPs automatisch prüfen", Checked = true, ForeColor = Theme.Text2, BackColor = Theme.Bg,
                AutoSize = true, Padding = new Padding(0, 8, 12, 0)
            };
            lInfo = Theme.Lbl("", Theme.F(9f), Theme.Muted, Theme.Bg);
            lInfo.Padding = new Padding(0, 10, 0, 0);
            bar.Controls.AddRange(new Control[] { bNow, autoCheck, lInfo });
            bNow.Click += async (s, e) => await RefreshLive();

            var chartCard = new RoundPanel { Dock = DockStyle.Top, Height = 230, Padding = new Padding(4, 4, 6, 10) };
            p.Resize += (s, e) => chartCard.Height = Math.Max(Theme.Px(120), Math.Min(Theme.Px(230), p.Height * 30 / 100));
            lChart = new LineChart { Dock = DockStyle.Fill, Title = "Live: verbundene Spieler", LineColor = Theme.Green, Line2Color = Theme.Red, Name1 = "online", Name2 = "VPN/Proxy" };
            chartCard.Controls.Add(lChart);
            var gap = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Theme.Bg };

            lList = MakeList(new[] { "Spieler", "online seit", "Dauer", "Server", "IP", "Provider / ASN", "Ort", "Prüfung" },
                             new[] { 150, 110, 80, 110, 120, 220, 140, 120 });
            var listCard = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 10) };
            listCard.Controls.Add(lList);

            p.Controls.Add(listCard);
            p.Controls.Add(gap);
            p.Controls.Add(chartCard);
            p.Controls.Add(bar);
            p.Controls.Add(cards);
            return p;
        }

        async Task RefreshLive()
        {
            if (!active || page != 0 || liveBusy) return;
            if (!sshReady())
            {
                hSub.Text = "Für Live-Daten braucht die App die SSH-Verbindung – richte sie einmal unter „Docker“ ein.";
                hSub.ForeColor = Theme.Yellow;
                return;
            }
            liveBusy = true;
            try
            {
                var script = LogPrelude +
                    "L=$(ls -1 *.log.gz 2>/dev/null | sort | tail -1)\n" +
                    "[ -n \"$L\" ] && { echo \"@@FILE $L\"; zcat \"$L\" | grep -aE \"$P\"; }\n" +
                    "[ -f latest.log ] && { echo \"@@LATEST $(date -r latest.log +%Y-%m-%d)\"; grep -aE \"$P\" latest.log; }\n" +
                    "true";
                var r = await getSsh().Run(script, 30000);
                if (!r.Ok) { lInfo.Text = "Fehler: " + r.Message; lInfo.ForeColor = Theme.Red; return; }
                live = Parse(r.Out).Where(s => !s.End.HasValue).ToList();
                hSub.ForeColor = Theme.Muted;
                hSub.Text = "Live aus logs/latest.log des Velocity-Proxys  ·  IP-Daten: " + (string.IsNullOrEmpty(st.VpnKey) ? "ipwho.is (ohne VPN-Erkennung)" : "vpnapi.io");

                int vpn = live.Count(s => { var i = IpIntel.Get(s.Ip); return i != null && (i.Vpn || i.Proxy || i.Tor || i.Relay); });
                liveOnline.Add(live.Count);
                liveVpn.Add(vpn);
                liveLabels.Add(DateTime.Now.ToString("HH:mm:ss"));
                while (liveOnline.Count > 180) { liveOnline.RemoveAt(0); liveVpn.RemoveAt(0); liveLabels.RemoveAt(0); }
                lChart.SetData(liveOnline.ToArray(), liveLabels.ToArray(), liveOnline.Count == 1);
                lChart.SetSecond(liveVpn.ToArray());
                RenderLive();
                lInfo.ForeColor = Theme.Muted;
                lInfo.Text = "Stand " + DateTime.Now.ToString("HH:mm:ss") + "  ·  alle 10 s";

                if (autoCheck.Checked && !lookupBusy)
                {
                    var unknown = live.Select(s => s.Ip).Distinct().Where(ip => !IpIntel.IsPrivate(ip) && IpIntel.Get(ip) == null).Take(15).ToList();
                    if (unknown.Count > 0) await Lookup(unknown, false);
                }
            }
            finally { liveBusy = false; }
        }

        void RenderLive()
        {
            var rows = new List<ListViewItem>();
            int vpn = 0, hosting = 0;
            var countries = new HashSet<string>();
            foreach (var s in live.OrderBy(x => x.Start))
            {
                var info = IpIntel.Get(s.Ip);
                bool bad = info != null && (info.Vpn || info.Proxy || info.Tor || info.Relay);
                if (bad) vpn++;
                if (info != null && info.Hosting && !bad) hosting++;
                if (info != null && !string.IsNullOrEmpty(info.CountryCode)) countries.Add(info.CountryCode);
                var row = Row(new RowTag { Key = s.Name, Dot = bad ? Theme.Red : Theme.Green, Data = bad },
                    s.Name, s.Start.ToString("HH:mm:ss"), Dur(s.Duration), s.Servers.Count > 0 ? s.Servers[s.Servers.Count - 1] : "–", s.Ip,
                    ProviderText(info, s.Ip), PlaceText(info), info == null ? (IpIntel.IsPrivate(s.Ip) ? "lokal" : "ausstehend") : (info.Suspicious ? info.Flags : "sauber"));
                row.SubItems[7].ForeColor = info == null ? Theme.Muted : info.Suspicious ? (bad ? Theme.Red : Theme.Yellow) : Theme.Green;
                rows.Add(row);
            }
            FillList(lList, rows);
            int n = live.Count;
            lOnline.Set(n.ToString(), "über Velocity verbunden", Theme.Green);
            lOnline.Bar = n > 0 ? 1 : 0;
            lVpn.Set(vpn.ToString(), n > 0 ? (100.0 * vpn / n).ToString("0", CultureInfo.InvariantCulture) + " % der Spieler" : "", vpn > 0 ? Theme.Red : Theme.Text);
            lVpn.Bar = n > 0 ? (double)vpn / n : 0;
            lHosting.Set(hosting.ToString(), "Server/Cloud-Adressen", hosting > 0 ? Theme.Yellow : Theme.Text);
            lHosting.Bar = n > 0 ? (double)hosting / n : 0;
            lCountries.Set(countries.Count.ToString(), string.Join(", ", countries.Take(6)), Theme.Text);
        }

        // ───────────────────────────── History ─────────────────────────────
        Panel BuildHistory()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            cPlayers = new StatCard("Spieler gesamt");
            cSessions = new StatCard("Sessions");
            cVpn = new StatCard("Spieler mit VPN/Proxy/RZ") { BarColor = Theme.Red };
            var cards = CardRow(cPlayers, cSessions, cVpn);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, BackColor = Theme.Bg, WrapContents = false };
            search = new Field(false) { Size = new Size(260, 40), Margin = new Padding(0, 0, 10, 0) };
            var bReload = new FlatBtn("↻ Alle Logs neu einlesen", Theme.CardHi) { Height = 40, Margin = new Padding(0, 0, 10, 0) };
            var bIntel = new FlatBtn("Alle IPs prüfen", Theme.Accent) { Height = 40, Margin = new Padding(0, 0, 10, 0) };
            var bKey = new FlatBtn("vpnapi.io-Key", Theme.CardHi) { Height = 40, Margin = new Padding(0, 0, 10, 0) };
            bar.Controls.AddRange(new Control[] { search, bReload, bIntel, bKey });
            search.Box.TextChanged += (s, e) => RenderHistory();
            bReload.Click += async (s, e) => await Reload();
            bIntel.Click += async (s, e) => await LookupAll();
            bKey.Click += (s, e) =>
            {
                var k = Dialogs.Ask(FindForm(), "vpnapi.io",
                    "API-Key von vpnapi.io. Leer lassen = ipwho.is (nur ASN/Provider, keine VPN-Erkennung).\nDer Key wird verschlüsselt gespeichert.", st.VpnKey ?? "");
                if (k == null) return;
                st.VpnKey = k.Trim();
                st.Save();
                setStatus(string.IsNullOrEmpty(st.VpnKey) ? "Kein vpnapi.io-Key – nutze ipwho.is." : "vpnapi.io-Key gespeichert.", Theme.Green);
            };

            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12 };
            split.Panel1.BackColor = Theme.Bg;
            split.Panel2.BackColor = Theme.Bg;
            UiX.Ratio(split, 0.42, 300, 360);
            pList = MakeList(new[] { "Spieler", "Zuletzt", "Spielzeit", "Sessions", "IPs", "Risiko" }, new[] { 150, 120, 80, 70, 40, 90 });
            var left = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 10) };
            left.Controls.Add(pList);
            split.Panel1.Controls.Add(left);

            var right = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 74, 8, 10) };
            detailTitle = Theme.Lbl("Spieler auswählen", Theme.F(12f, FontStyle.Bold), Theme.Text, Color.Transparent);
            detailTitle.Location = new Point(16, 14);
            alts = Theme.Lbl("", Theme.F(9f), Theme.Muted, Color.Transparent);
            alts.Location = new Point(18, 44);
            alts.AutoSize = false;
            alts.AutoEllipsis = true;
            alts.Size = new Size(600, 20);
            right.Resize += (s, e) => alts.Width = right.Width - 36;
            sList = MakeList(new[] { "Online von", "bis", "Dauer", "Server", "IP", "Provider / ASN", "Ort", "Prüfung" }, new[] { 120, 120, 70, 140, 110, 180, 120, 90 });
            sList.ShowItemToolTips = true;
            right.Controls.Add(sList);
            right.Controls.Add(detailTitle);
            right.Controls.Add(alts);
            split.Panel2.Controls.Add(right);
            pList.SelectedIndexChanged += (s, e) => RenderDetail();

            p.Controls.Add(split);
            p.Controls.Add(bar);
            p.Controls.Add(cards);
            return p;
        }

        async Task Reload()
        {
            if (loading) return;
            if (!sshReady())
            {
                hSub.Text = "Für das Tracking braucht die App die SSH-Verbindung – richte sie einmal unter „Docker“ ein.";
                hSub.ForeColor = Theme.Yellow;
                return;
            }
            loading = true;
            progress.Set(double.NaN);
            setStatus("Lese alle Velocity-Logs …", Theme.Muted);
            try
            {
                var script = LogPrelude +
                    "for f in $(ls -1 *.log.gz 2>/dev/null | sort); do echo \"@@FILE $f\"; zcat \"$f\" | grep -aE \"$P\"; done\n" +
                    "[ -f latest.log ] && { echo \"@@LATEST $(date -r latest.log +%Y-%m-%d)\"; grep -aE \"$P\" latest.log; }\n" +
                    "[ -f " + ManualFile + " ] && { echo '@@MANUAL'; cat " + ManualFile + "; }\n" +
                    "true";
                var r = await getSsh().Run(script, 120000);
                if (!r.Ok) { setStatus("Logs konnten nicht gelesen werden: " + r.Message, Theme.Red); return; }
                var outText = r.Out.Replace("\r", "");
                var cut = outText.IndexOf("\n@@MANUAL\n", StringComparison.Ordinal);
                string logPart = outText, manualPart = "";
                if (outText.StartsWith("@@MANUAL\n")) { logPart = ""; manualPart = outText.Substring(9); }
                else if (cut >= 0) { logPart = outText.Substring(0, cut + 1); manualPart = outText.Substring(cut + 10); }
                sessions = Parse(logPart);
                sessions.AddRange(ParseManual(manualPart));
                loadedOnce = true;
                RenderHistory();
                RenderVpnStats();
                var first = sessions.Count > 0 ? sessions.Min(x => x.Start) : DateTime.Now;
                hSub.ForeColor = Theme.Muted;
                hSub.Text = "Quelle: alle Velocity-Logs  ·  " + sessions.Count + " Sessions seit " + first.ToString("dd.MM.yyyy") +
                            "  ·  IP-Daten: " + (string.IsNullOrEmpty(st.VpnKey) ? "ipwho.is (ohne VPN-Erkennung)" : "vpnapi.io");
                setStatus("Spieler-Verlauf aktualisiert.", Theme.Green);
            }
            finally
            {
                loading = false;
                progress.Set(-1);
            }
        }

        static List<Session> Parse(string text)
        {
            var result = new List<Session>();
            var open = new Dictionary<string, Session>(StringComparer.OrdinalIgnoreCase);
            var blocks = new List<KeyValuePair<DateTime?, List<string>>>();
            List<string> curLines = null;
            DateTime? curDate = null;
            bool latest = false;
            DateTime latestEnd = DateTime.Today;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                if (line.StartsWith("@@FILE ") || line.StartsWith("@@LATEST "))
                {
                    if (curLines != null) blocks.Add(new KeyValuePair<DateTime?, List<string>>(latest ? (DateTime?)null : curDate, curLines));
                    curLines = new List<string>();
                    latest = line.StartsWith("@@LATEST ");
                    var part = line.Substring(latest ? 9 : 7);
                    DateTime d;
                    if (DateTime.TryParseExact(part.Length >= 10 ? part.Substring(0, 10) : part, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d))
                    {
                        if (latest) latestEnd = d;
                        curDate = d;
                    }
                    continue;
                }
                if (curLines != null && line.Length > 10) curLines.Add(line);
            }
            if (curLines != null) blocks.Add(new KeyValuePair<DateTime?, List<string>>(latest ? (DateTime?)null : curDate, curLines));

            foreach (var block in blocks)
            {
                var lines = block.Value;
                DateTime day;
                if (block.Key.HasValue) day = block.Key.Value;
                else
                {
                    int wraps = 0;
                    TimeSpan prev = TimeSpan.Zero;
                    foreach (var l in lines) { var t = TimeOf(l); if (t.HasValue) { if (t.Value < prev) wraps++; prev = t.Value; } }
                    day = latestEnd.AddDays(-wraps);
                }
                TimeSpan last = TimeSpan.Zero;
                foreach (var l in lines)
                {
                    var tod = TimeOf(l);
                    if (!tod.HasValue) continue;
                    if (tod.Value < last) day = day.AddDays(1);
                    last = tod.Value;
                    var ts = day + tod.Value;
                    Match m;
                    if ((m = BootRx.Match(l)).Success)
                    {
                        foreach (var s in open.Values) { s.End = ts; s.EndedByRestart = true; s.Reason = "Proxy-Neustart"; }
                        open.Clear();
                    }
                    else if ((m = LoginRx.Match(l)).Success)
                    {
                        var name = m.Groups[4].Value;
                        Session prevS;
                        if (open.TryGetValue(name, out prevS)) { prevS.End = ts; prevS.Reason = "neu verbunden"; }
                        var s = new Session { Name = name, Ip = m.Groups[5].Value.Trim('[', ']'), Start = ts };
                        open[name] = s;
                        result.Add(s);
                    }
                    else if ((m = LogoutRx.Match(l)).Success)
                    {
                        Session s;
                        if (open.TryGetValue(m.Groups[4].Value, out s))
                        {
                            s.End = ts;
                            s.Reason = m.Groups[6].Success ? m.Groups[6].Value : "";
                            open.Remove(m.Groups[4].Value);
                        }
                    }
                    else if ((m = SwitchRx.Match(l)).Success)
                    {
                        Session s;
                        if (open.TryGetValue(m.Groups[4].Value, out s))
                        {
                            var srv = m.Groups[5].Value;
                            if (s.Servers.Count == 0 || s.Servers[s.Servers.Count - 1] != srv) s.Servers.Add(srv);
                        }
                    }
                }
            }
            return result;
        }

        /// <summary>Hand-made entries for VPN / alt tracking. One per line:
        /// <c>YYYY-MM-DD[ HH:MM] Name IP [note …]</c> – '#' starts a comment.</summary>
        public const string ManualFile = "/var/lib/pxmx/manual-ips.txt";

        static List<Session> ParseManual(string text)
        {
            var result = new List<Session>();
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#")) continue;
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                DateTime d;
                if (parts.Count < 3 || !DateTime.TryParseExact(parts[0], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out d)) continue;
                parts.RemoveAt(0);
                TimeSpan t;
                if (parts.Count >= 3 && parts[0].Contains(":") && TimeSpan.TryParseExact(parts[0], @"h\:mm", CultureInfo.InvariantCulture, out t))
                {
                    d += t;
                    parts.RemoveAt(0);
                }
                IPAddress ip;
                if (parts.Count < 2 || !IPAddress.TryParse(parts[1], out ip)) continue;
                var note = string.Join(" ", parts.Skip(2));
                result.Add(new Session { Name = parts[0], Ip = parts[1], Start = d, End = d, Manual = true, Reason = "manuell" + (note.Length > 0 ? ": " + note : "") });
            }
            return result;
        }

        static TimeSpan? TimeOf(string l)
        {
            if (l.Length < 10 || l[0] != '[' || l[9] != ']') return null;
            int h, mi, s;
            if (int.TryParse(l.Substring(1, 2), out h) && int.TryParse(l.Substring(4, 2), out mi) && int.TryParse(l.Substring(7, 2), out s))
                return new TimeSpan(h, mi, s);
            return null;
        }

        static string Dur(TimeSpan t)
        {
            if (t.TotalSeconds < 60) return Math.Max(0, (int)t.TotalSeconds) + " s";
            if (t.TotalHours < 1) return (int)t.TotalMinutes + " min";
            if (t.TotalDays < 1) return (int)t.TotalHours + " h " + t.Minutes + " min";
            return (int)t.TotalDays + " T " + t.Hours + " h";
        }

        static string When(DateTime d)
        {
            if (d.Date == DateTime.Today) return "heute " + d.ToString("HH:mm");
            if (d.Date == DateTime.Today.AddDays(-1)) return "gestern " + d.ToString("HH:mm");
            return d.ToString("dd.MM. HH:mm");
        }

        static string ProviderText(IpInfo info, string ip)
        {
            if (info == null) return IpIntel.IsPrivate(ip) ? "privat/lokal" : "noch nicht geprüft";
            return (info.Org ?? info.Isp ?? "?") + (string.IsNullOrEmpty(info.Asn) ? "" : "  (" + info.Asn + ")");
        }

        static string PlaceText(IpInfo info)
        {
            if (info == null) return "";
            return (!string.IsNullOrEmpty(info.City) ? info.City + ", " : "") + (info.CountryCode ?? info.Country ?? "");
        }

        void RenderHistory()
        {
            var q = search.Box.Text.Trim();
            var byPlayer = sessions.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            var rows = new List<ListViewItem>();
            int vpnPlayers = 0;
            foreach (var g in byPlayer.OrderByDescending(x => x.Max(s => s.End ?? DateTime.MaxValue)))
            {
                var name = g.First().Name;
                var ips = g.Select(s => s.Ip).Distinct().ToList();
                bool isOnline = g.Any(s => !s.End.HasValue);
                var infos = ips.Select(IpIntel.Get).Where(i => i != null).ToList();
                bool risky = infos.Any(i => i.Suspicious);
                if (risky) vpnPlayers++;
                if (q.Length > 0 && name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && !ips.Any(ip => ip.Contains(q)) &&
                    !infos.Any(i => (i.Org ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (i.Asn ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                    continue;
                var total = TimeSpan.FromTicks(g.Sum(s => Math.Max(0, s.Duration.Ticks)));
                var lastSeen = g.Max(s => s.End ?? DateTime.Now);
                var risk = risky ? string.Join(", ", infos.Where(i => i.Suspicious).Select(i => i.Flags).Distinct()) : (infos.Count < ips.Count ? "?" : "sauber");
                var row = Row(new RowTag { Key = name, Dot = isOnline ? Theme.Green : Theme.Muted, Data = risky },
                    name, isOnline ? "online" : When(lastSeen), Dur(total), g.Count().ToString(), ips.Count.ToString(), risk);
                if (isOnline) row.SubItems[1].ForeColor = Theme.Green;
                row.SubItems[5].ForeColor = risky ? Theme.Red : risk == "sauber" ? Theme.Green : Theme.Muted;
                rows.Add(row);
            }
            FillList(pList, rows);
            cPlayers.Set(byPlayer.Count.ToString(), "eindeutige Namen", Theme.Text);
            cSessions.Set(sessions.Count.ToString(), "Logins insgesamt", Theme.Text);
            cVpn.Set(vpnPlayers.ToString(), "mit auffälliger IP", vpnPlayers > 0 ? Theme.Red : Theme.Text);
            cVpn.Bar = byPlayer.Count > 0 ? (double)vpnPlayers / byPlayer.Count : 0;
            RenderDetail();
        }

        void RenderDetail()
        {
            var name = pList.SelectedItems.Count > 0 ? ((RowTag)pList.SelectedItems[0].Tag).Key : null;
            if (name == null) { detailTitle.Text = "Spieler auswählen"; alts.Text = ""; sList.Items.Clear(); return; }
            var mine = sessions.Where(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).OrderByDescending(s => s.Start).ToList();
            var total = TimeSpan.FromTicks(mine.Sum(s => Math.Max(0, s.Duration.Ticks)));
            detailTitle.Text = name + "   ·   " + mine.Count + " Sessions   ·   " + Dur(total) + " gesamt";
            var ips = mine.Select(s => s.Ip).Distinct().ToList();
            var others = sessions.Where(s => ips.Contains(s.Ip) && !string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                                 .Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            alts.Text = others.Count > 0 ? "⚠ Gleiche IP wie: " + string.Join(", ", others) + "  (mögliche Zweit-Accounts oder gleicher Haushalt)" : "Keine anderen Accounts mit derselben IP.";
            alts.ForeColor = others.Count > 0 ? Theme.Yellow : Theme.Muted;
            var rows = new List<ListViewItem>();
            foreach (var s in mine)
            {
                var info = IpIntel.Get(s.Ip);
                bool bad = info != null && info.Suspicious;
                var row = Row(new RowTag { Key = s.Start.Ticks + s.Ip + s.Manual, Dot = s.Manual ? Theme.SoftGreen : !s.End.HasValue ? Theme.Green : s.EndedByRestart ? Theme.Yellow : Theme.Muted, Data = bad },
                    s.Start.ToString(s.Manual ? "dd.MM.yy" : "dd.MM.yy HH:mm:ss"),
                    s.Manual ? "manuell" : s.End.HasValue ? s.End.Value.ToString("dd.MM.yy HH:mm:ss") + (s.EndedByRestart ? " *" : "") : "online",
                    s.Manual ? "–" : Dur(s.Duration), s.Manual ? "–" : s.Servers.Count > 0 ? string.Join(" → ", s.Servers) : "–", s.Ip, ProviderText(info, s.Ip), PlaceText(info),
                    info == null ? "" : bad ? info.Flags : "sauber");
                if (bad) row.SubItems[7].ForeColor = Theme.Red;
                else if (info != null) row.SubItems[7].ForeColor = Theme.Green;
                if (!s.End.HasValue) row.SubItems[1].ForeColor = Theme.Green;
                if (!string.IsNullOrEmpty(s.Reason) && s.Reason != "Proxy-Neustart") row.ToolTipText = "Grund: " + s.Reason;
                rows.Add(row);
            }
            sList.BeginUpdate();
            sList.Items.Clear();
            sList.Items.AddRange(rows.ToArray());
            sList.EndUpdate();
        }

        // ───────────────────────────── VPN statistics ─────────────────────────────
        Panel BuildVpnStats()
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            vIps = new StatCard("IPs gesamt");
            vChecked = new StatCard("geprüft") { BarColor = Theme.SoftGreen };
            vVpn = new StatCard("VPN") { BarColor = Theme.Red };
            vProxy = new StatCard("Proxy") { BarColor = Theme.Red };
            vTor = new StatCard("Tor / Relay") { BarColor = Theme.Red };
            vHosting = new StatCard("Rechenzentrum") { BarColor = Theme.Yellow };
            foreach (var c in new[] { vIps, vChecked, vVpn, vProxy, vTor, vHosting }) c.Width = 172;
            var cards = CardRow(vIps, vChecked, vVpn, vProxy, vTor, vHosting);

            var chartCard = new RoundPanel { Dock = DockStyle.Top, Height = 230, Padding = new Padding(4, 4, 6, 10) };
            p.Resize += (s, e) => chartCard.Height = Math.Max(Theme.Px(120), Math.Min(Theme.Px(230), p.Height * 30 / 100));
            vChart = new LineChart { Dock = DockStyle.Fill, Title = "Spieler pro Tag (30 Tage)", LineColor = Theme.SoftGreen, Line2Color = Theme.Red, Name1 = "Spieler", Name2 = "mit VPN/Proxy" };
            chartCard.Controls.Add(vChart);
            var gap = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = Theme.Bg };

            var split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12 };
            split.Panel1.BackColor = Theme.Bg;
            split.Panel2.BackColor = Theme.Bg;
            UiX.Ratio(split, 0.6, 260, 200);
            vProviders = MakeList(new[] { "Provider", "ASN", "Spieler", "Sessions", "Typ" }, new[] { 240, 90, 70, 80, 120 });
            vCountries = MakeList(new[] { "Land", "Spieler", "VPN-Anteil" }, new[] { 160, 70, 90 });
            var c1 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 10) };
            c1.Controls.Add(vProviders);
            var c2 = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 10) };
            c2.Controls.Add(vCountries);
            split.Panel1.Controls.Add(c1);
            split.Panel2.Controls.Add(c2);

            p.Controls.Add(split);
            p.Controls.Add(gap);
            p.Controls.Add(chartCard);
            p.Controls.Add(cards);
            return p;
        }

        void RenderVpnStats()
        {
            var ips = sessions.Select(s => s.Ip).Distinct().Where(ip => !IpIntel.IsPrivate(ip)).ToList();
            var infos = ips.Select(IpIntel.Get).Where(i => i != null).ToList();
            int n = ips.Count, k = infos.Count;
            Action<StatCard, int, string> set = (c, v, sub) =>
            {
                c.Set(v.ToString(), sub, v > 0 && c != vIps && c != vChecked ? c.BarColor : Theme.Text);
                c.Bar = k > 0 && c != vIps ? (double)v / Math.Max(1, c == vChecked ? n : k) : -1;
            };
            set(vIps, n, "aus allen Sessions");
            set(vChecked, k, n > 0 ? (100 * k / n) + " % abgefragt" : "");
            set(vVpn, infos.Count(i => i.Vpn), "VPN-Anbieter");
            set(vProxy, infos.Count(i => i.Proxy), "offene Proxys");
            set(vTor, infos.Count(i => i.Tor || i.Relay), "Tor / Apple Relay");
            set(vHosting, infos.Count(i => i.Hosting), "Cloud / Hoster");

            // per-day chart
            var since = DateTime.Today.AddDays(-29);
            var days = Enumerable.Range(0, 30).Select(d => since.AddDays(d)).ToList();
            var total = new double?[30];
            var bad = new double?[30];
            for (int d = 0; d < 30; d++)
            {
                var day = days[d];
                var onDay = sessions.Where(s => s.Start.Date <= day && (s.End ?? DateTime.Now).Date >= day).ToList();
                var players = onDay.Select(s => s.Name.ToLowerInvariant()).Distinct().Count();
                var badPlayers = onDay.Where(s => { var i = IpIntel.Get(s.Ip); return i != null && (i.Vpn || i.Proxy || i.Tor || i.Relay); })
                                      .Select(s => s.Name.ToLowerInvariant()).Distinct().Count();
                total[d] = players;
                bad[d] = badPlayers;
            }
            vChart.SetData(total, days.Select(d => d.ToString("dd.MM.")).ToArray(), true);
            vChart.SetSecond(bad);

            // providers
            var prov = new List<ListViewItem>();
            foreach (var g in infos.GroupBy(i => (i.Asn ?? "?") + "|" + (i.Org ?? i.Isp ?? "?")).OrderByDescending(g => g.Count()))
            {
                var ipSet = new HashSet<string>(g.Select(i => i.Ip));
                var ss = sessions.Where(s => ipSet.Contains(s.Ip)).ToList();
                var f = g.First();
                var type = g.Any(i => i.Vpn) ? "VPN" : g.Any(i => i.Proxy) ? "Proxy" : g.Any(i => i.Tor || i.Relay) ? "Tor/Relay" : g.Any(i => i.Hosting) ? "Rechenzentrum" : "Privat/ISP";
                bool flag = type != "Privat/ISP";
                var row = Row(new RowTag { Key = g.Key, Dot = flag ? (type == "Rechenzentrum" ? Theme.Yellow : Theme.Red) : Theme.Green, Data = flag && type != "Rechenzentrum" },
                    f.Org ?? f.Isp ?? "?", f.Asn ?? "", ss.Select(s => s.Name.ToLowerInvariant()).Distinct().Count().ToString(), ss.Count.ToString(), type);
                row.SubItems[4].ForeColor = flag ? (type == "Rechenzentrum" ? Theme.Yellow : Theme.Red) : Theme.Green;
                prov.Add(row);
            }
            FillList(vProviders, prov);

            var ctry = new List<ListViewItem>();
            foreach (var g in infos.GroupBy(i => i.Country ?? i.CountryCode ?? "?").OrderByDescending(g => g.Count()))
            {
                var ipSet = new HashSet<string>(g.Select(i => i.Ip));
                var players = sessions.Where(s => ipSet.Contains(s.Ip)).Select(s => s.Name.ToLowerInvariant()).Distinct().Count();
                double share = g.Count() > 0 ? 100.0 * g.Count(i => i.Vpn || i.Proxy || i.Tor || i.Relay) / g.Count() : 0;
                var row = Row(new RowTag { Key = g.Key, Dot = Theme.SoftGreen }, g.Key, players.ToString(), share.ToString("0", CultureInfo.InvariantCulture) + " %");
                if (share > 0) row.SubItems[2].ForeColor = Theme.Red;
                ctry.Add(row);
            }
            FillList(vCountries, ctry);
        }

        // ───────────────────────────── IP lookups ─────────────────────────────
        async Task LookupAll()
        {
            if (!loadedOnce) await Reload();
            var ips = sessions.Select(s => s.Ip).Concat(live.Select(s => s.Ip)).Distinct().Where(ip => !IpIntel.IsPrivate(ip))
                              .Where(ip => { var i = IpIntel.Get(ip); return i == null || (DateTime.Now - i.Fetched).TotalDays > 14; }).ToList();
            if (ips.Count == 0) { setStatus("Alle IPs sind bereits geprüft (Cache 14 Tage).", Theme.Muted); return; }
            if (!Dialogs.Confirm(FindForm(), "IP-Prüfung",
                    ips.Count + " IP-Adressen bei " + (string.IsNullOrEmpty(st.VpnKey) ? "ipwho.is" : "vpnapi.io") + " prüfen?\n\n" +
                    "Hinweis: Die IP-Adressen deiner Spieler werden dabei an diesen Dienst übermittelt (personenbezogene Daten).")) return;
            await Lookup(ips, true);
        }

        async Task Lookup(List<string> ips, bool showProgress)
        {
            if (lookupBusy) return;
            lookupBusy = true;
            var key = st.VpnKey;
            int done = 0, failed = 0;
            string lastErr = null;
            if (showProgress) progress.Set(0);
            try
            {
                foreach (var ip in ips)
                {
                    try { await Task.Run(() => IpIntel.Lookup(ip, key)); }
                    catch (Exception ex)
                    {
                        failed++;
                        lastErr = ex.Message;
                        if (ex.Message.Contains("429") || ex.Message.ToLowerInvariant().Contains("limit")) break;
                    }
                    done++;
                    if (showProgress)
                    {
                        progress.Set((double)done / ips.Count);
                        setStatus("IP-Prüfung: " + done + " / " + ips.Count + (failed > 0 ? "  (" + failed + " Fehler)" : ""), Theme.Muted);
                    }
                    if (done % 10 == 0) RefreshViews();
                    await Task.Delay(string.IsNullOrEmpty(key) ? 700 : 120);
                }
            }
            finally
            {
                IpIntel.Save();
                lookupBusy = false;
                if (showProgress) progress.Set(-1);
            }
            RefreshViews();
            if (showProgress || failed > 0)
                setStatus("IP-Prüfung fertig: " + (done - failed) + " geprüft" + (failed > 0 ? ", " + failed + " Fehler – " + lastErr : "."), failed > 0 ? Theme.Yellow : Theme.Green);
        }

        void RefreshViews()
        {
            RenderLive();
            if (loadedOnce) { RenderHistory(); RenderVpnStats(); }
        }
    }
}
