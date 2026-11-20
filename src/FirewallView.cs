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
    class FwRule
    {
        public string Kind;     // allow | deny | block
        public string Proto;    // tcp | udp | both (block: -)
        public string Port;     // "25565" or "25500:25600" (block: -)
        public string Source;   // ip/cidr or "any"
        public string Comment = "";
        public long Packets, Bytes;

        public string Line()
        {
            var c = Comment.Replace("\n", " ").Replace("\r", "").Trim();
            if (Kind == "block") return "block " + Source + (c.Length > 0 ? " # " + c : "");
            return Kind + " " + Proto + " " + Port + " " + (string.IsNullOrEmpty(Source) ? "any" : Source) + (c.Length > 0 ? " # " + c : "");
        }

        static readonly Regex Ip = new Regex(@"^(\d{1,3}(\.\d{1,3}){3}(/\d{1,2})?|[0-9a-fA-F:]{2,39}(/\d{1,3})?|any)$");
        static readonly Regex PortRx = new Regex(@"^\d{1,5}(:\d{1,5})?$");

        public static bool ValidSource(string s) { return s != null && Ip.IsMatch(s) && (!s.Contains(".") || s.Split('/')[0].Split('.').All(o => int.Parse(o) <= 255)); }
        public static bool ValidPort(string p)
        {
            if (p == null || !PortRx.IsMatch(p)) return false;
            return p.Split(':').All(x => { int v; return int.TryParse(x, out v) && v > 0 && v < 65536; });
        }

        public static FwRule Parse(string line)
        {
            var l = line.Trim();
            if (l.Length == 0 || l.StartsWith("#")) return null;
            string comment = "";
            int h = l.IndexOf(" #");
            if (h >= 0) { comment = l.Substring(h + 2).Trim(); l = l.Substring(0, h).Trim(); }
            var p = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 2 && p[0] == "block" && ValidSource(p[1])) return new FwRule { Kind = "block", Proto = "-", Port = "-", Source = p[1], Comment = comment };
            if (p.Length >= 3 && (p[0] == "allow" || p[0] == "deny") && (p[1] == "tcp" || p[1] == "udp" || p[1] == "both") && ValidPort(p[2]))
                return new FwRule { Kind = p[0], Proto = p[1], Port = p[2], Source = p.Length > 3 && ValidSource(p[3]) ? p[3] : "any", Comment = comment };
            return null;
        }
    }

    /// <summary>
    /// Firewall on plain iptables/ip6tables (works with Docker): own chains RYVEN-IN (INPUT) and RYVEN-FWD (DOCKER-USER),
    /// rules file /etc/pxmx/firewall.conf, systemd unit for reboot persistence and auto-rollback on changes.
    /// </summary>
    class FirewallView : Panel
    {
        readonly Func<Ssh> getSsh;
        readonly Func<bool> sshReady;
        readonly Action<string, Color> setStatus;

        Label hSub, modeInfo;
        TabBtn[] tabs;
        Panel[] pages;
        int page;
        ProgressLine progress;
        StatCard cMode, cOpen, cBlocked, cDropped;
        ListView portList, ruleList, blockList, liveList;
        RichTextBox output;
        FlatBtn bStrict;
        bool busy, installed, strict;
        string extIf = "eth0";
        int sshPort = 22;
        List<FwRule> rules = new List<FwRule>();
        List<PortInfo> ports = new List<PortInfo>();

        class PortInfo
        {
            public string Proto, Bind, Process;
            public int Port;
            public bool Docker, Public;
            public string Container;
        }

        const string ConfPath = "/etc/pxmx/firewall.conf";

        public FirewallView(Func<Ssh> getSsh, Func<bool> sshReady, Action<string, Color> setStatus)
        {
            this.getSsh = getSsh;
            this.sshReady = sshReady;
            this.setStatus = setStatus;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            Build();
        }

        public async void Activate()
        {
            if (!sshReady())
            {
                hSub.Text = "Für die Firewall braucht die App die SSH-Verbindung – richte sie einmal unter „Docker“ ein.";
                hSub.ForeColor = Theme.Yellow;
                return;
            }
            hSub.ForeColor = Theme.Muted;
            await Reload();
        }

        public void GoTab(int i) { SetTab(i); }
        public async void OpenRuleDialog(string kind) { await AddRule(kind, null); }

        public void Deactivate() { }

        // ───────────────────────────── Layout ─────────────────────────────
        void Build()
        {
            var header = Ui.Header("Firewall & Ports", out hSub, new[] { "Ports", "Regeln", "Gesperrte IPs", "Verbindungen" }, out tabs, i => { SetTab(i); });
            cMode = new StatCard("Modus") { BarColor = Theme.Green };
            cOpen = new StatCard("Öffentliche Ports") { BarColor = Theme.SoftGreen };
            cBlocked = new StatCard("Gesperrte IPs") { BarColor = Theme.Red };
            cDropped = new StatCard("Abgewiesene Pakete") { BarColor = Theme.Yellow };
            var cards = new CardGrid { Dock = DockStyle.Top, MinCardWidth = 150, CardHeight = 104 };
            cards.Controls.AddRange(new Control[] { cMode, cOpen, cBlocked, cDropped });

            var bRefresh = Ui.Btn("↻ Aktualisieren", Theme.CardHi);
            var bOpen = Ui.Btn("+ Port öffnen", Theme.Mix(Theme.Green, Theme.Bg, 0.3));
            var bClose = Ui.Btn("− Port schließen", Theme.Mix(Theme.Red, Theme.Bg, 0.3));
            var bBlock = Ui.Btn("IP sperren", Theme.Mix(Theme.Red, Theme.Bg, 0.3));
            bStrict = Ui.Btn("Strikt-Modus aktivieren", Theme.Accent);
            var bRaw = Ui.Btn("iptables anzeigen", Theme.CardHi);
            modeInfo = Theme.Lbl("", Theme.F(9f), Theme.Muted, Theme.Bg);
            modeInfo.Margin = new Padding(6, 10, 0, 0);
            var bar = Ui.Bar(50, bRefresh, bOpen, bClose, bBlock, bStrict, bRaw);
            bOpen.Tip("Port von außen erreichbar machen (auch Docker-Ports)");
            bClose.Tip("Port von außen sperren – der Dienst läuft weiter");
            bBlock.Tip("Eine IP oder ein Netz komplett blockieren");
            bStrict.Tip("Strikt = alles zu außer erlaubte Ports · Offen = alles erlaubt außer gesperrte");
            bar.Padding = new Padding(0, 6, 0, 0);
            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };

            var content = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(0, 6, 0, 0) };
            portList = Ui.List(new[] { "Port", "Protokoll", "Gebunden an", "Dienst / Container", "Erreichbar", "Firewall" }, new[] { 90, 90, 150, 260, 130, 200 });
            ruleList = Ui.List(new[] { "#", "Aktion", "Protokoll", "Port", "Quelle", "Treffer", "Kommentar" }, new[] { 40, 100, 90, 120, 160, 110, 200 });
            blockList = Ui.List(new[] { "IP / Netz", "Treffer", "Kommentar" }, new[] { 220, 120, 300 });
            liveList = Ui.List(new[] { "Port", "Remote-IP", "Verbindungen", "Status" }, new[] { 100, 220, 120, 160 });
            pages = new Panel[4];
            var lists = new[] { portList, ruleList, blockList, liveList };
            for (int i = 0; i < 4; i++)
            {
                var card = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 8) };
                card.Controls.Add(lists[i]);
                pages[i] = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
                pages[i].Controls.Add(card);
                content.Controls.Add(pages[i]);
            }

            outPanel = new Panel { Dock = DockStyle.Bottom, Height = 140, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0), Visible = false };
            var outBox = new RoundPanel { Dock = DockStyle.Fill, Fill = Theme.Input, BorderColor = Theme.Border, Padding = new Padding(14, 10, 6, 10) };
            output = new RichTextBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text2, Font = Theme.Mono, ReadOnly = true, WordWrap = false, DetectUrls = false };
            var hideOut = new FlatBtn("×", Theme.Input) { Dock = DockStyle.Right, Width = 30, Height = 26 }.Tip("Ausgabe ausblenden");
            hideOut.Click += (s, e) => outPanel.Visible = false;
            outBox.Controls.Add(output);
            outBox.Controls.Add(hideOut);
            outPanel.Controls.Add(outBox);

            Controls.Add(content);
            Controls.Add(outPanel);
            Controls.Add(progress);
            Controls.Add(bar);
            Controls.Add(cards);
            Controls.Add(header);
            SetTab(0);

            bRefresh.Click += async (s, e) => await Reload();
            bOpen.Click += async (s, e) => await AddRule("allow", null);
            bClose.Click += async (s, e) => await AddRule("deny", null);
            bBlock.Click += async (s, e) => await BlockIp(null, null);
            bStrict.Click += async (s, e) => await ToggleStrict();
            bRaw.Click += async (s, e) =>
            {
                var r = await getSsh().Run("echo '# IPv4'; iptables -S RYVEN-IN 2>&1; iptables -S RYVEN-FWD 2>&1; iptables -S INPUT | head -5; iptables -S DOCKER-USER; echo; echo '# IPv6'; ip6tables -S RYVEN-IN 2>&1 | head -40", 30000);
                Out("iptables", r.Out + r.Err, !r.Ok);
            };

            portList.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) PortMenu(e.Location); };
            portList.ItemActivate += (s, e) => PortMenu(portList.PointToClient(Cursor.Position));
            ruleList.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) RuleMenu(ruleList, e.Location); };
            ruleList.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Delete) await RemoveSelected(ruleList); };
            blockList.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) RuleMenu(blockList, e.Location); };
            blockList.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Delete) await RemoveSelected(blockList); };
            liveList.MouseUp += (s, e) =>
            {
                if (e.Button != MouseButtons.Right || liveList.SelectedItems.Count == 0) return;
                var ip = liveList.SelectedItems[0].SubItems[1].Text;
                var m = Ui.Menu();
                m.Item("IP " + ip + " sperren", async () => await BlockIp(ip, "aus Verbindungsliste"));
                m.Item("IP kopieren", () => Clipboard.SetText(ip));
                m.Show(liveList, e.Location);
            };
        }

        void SetTab(int i)
        {
            page = i;
            for (int k = 0; k < pages.Length; k++) { pages[k].Visible = k == i; tabs[k].Active = k == i; }
            if (i == 3 && !busy) { var t = LoadLive(); }
        }

        Panel outPanel;

        void Out(string title, string text, bool err)
        {
            outPanel.Visible = true;
            output.Clear();
            output.SelectionColor = Theme.Muted;
            output.AppendText("── " + title + " ──\n");
            output.SelectionColor = err ? Theme.Red : Theme.Text2;
            output.AppendText((text ?? "").Replace("\r\n", "\n").Trim() + "\n");
        }

        // ───────────────────────────── Data ─────────────────────────────
        async Task Reload()
        {
            if (busy) return;
            busy = true;
            progress.Set(double.NaN);
            try
            {
                var script =
                    "echo \"@@IF $(ip route show default 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i==\"dev\"){print $(i+1); exit}}')\"\n" +
                    "echo \"@@SSH $(sshd -T 2>/dev/null | awk '/^port /{print $2; exit}')\"\n" +
                    "echo \"@@INST $( [ -x /usr/local/sbin/pxmx-fw ] && echo 1 || echo 0) $(systemctl is-enabled pxmx-fw 2>/dev/null)\"\n" +
                    "echo '@@CONF'; cat " + ConfPath + " 2>/dev/null; echo\n" +
                    "echo '@@SS'; ss -H -tulpn 2>/dev/null\n" +
                    "echo '@@DOCKER'; docker ps --format '{{.Names}}\\t{{.Ports}}' 2>/dev/null\n" +
                    "echo '@@CNT'; for t in iptables ip6tables; do for c in RYVEN-IN RYVEN-FWD; do $t -L $c -n -v -x 2>/dev/null | awk '/rp:/{for(i=1;i<=NF;i++) if($i ~ /^rp:/){print $i\"\\t\"$1\"\\t\"$2}}'; done; done\n" +
                    "echo '@@END'";
                var r = await getSsh().Run(script, 45000);
                if (!r.Ok) { Out("Fehler", r.Message, true); setStatus("Firewall: " + r.Message, Theme.Red); return; }
                Parse(r.Out);
                Render();
            }
            finally { busy = false; progress.Set(-1); }
        }

        void Parse(string text)
        {
            string sec = null;
            var conf = new List<string>();
            var ss = new List<string>();
            var dock = new List<string>();
            var counters = new Dictionary<int, long[]>();
            foreach (var raw in text.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                if (l.StartsWith("@@IF ")) { var v = l.Substring(5).Trim(); if (Regex.IsMatch(v, @"^[A-Za-z0-9_.@-]{1,15}$")) extIf = v; continue; }
                if (l.StartsWith("@@SSH ")) { int p; if (int.TryParse(l.Substring(6).Trim(), out p)) sshPort = p; continue; }
                if (l.StartsWith("@@INST ")) { var v = l.Substring(7).Trim(); installed = v.StartsWith("1"); continue; }
                if (l.StartsWith("@@")) { sec = l; continue; }
                if (sec == "@@CONF") conf.Add(l);
                else if (sec == "@@SS" && l.Trim().Length > 0) ss.Add(l);
                else if (sec == "@@DOCKER" && l.Trim().Length > 0) dock.Add(l);
                else if (sec == "@@CNT")
                {
                    var f = l.Split('\t');
                    int idx; long pk, by;
                    if (f.Length == 3 && int.TryParse(f[0].Substring(3), out idx) && long.TryParse(f[1], out pk) && long.TryParse(f[2], out by))
                    {
                        long[] c; if (!counters.TryGetValue(idx, out c)) counters[idx] = c = new long[2];
                        c[0] += pk; c[1] += by;
                    }
                }
            }
            strict = conf.Any(c => c.Trim() == "mode strict");
            rules = conf.Select(FwRule.Parse).Where(x => x != null).ToList();
            for (int i = 0; i < rules.Count; i++) { long[] c; if (counters.TryGetValue(i, out c)) { rules[i].Packets = c[0]; rules[i].Bytes = c[1]; } }
            long[] dropAll;
            dropped = counters.TryGetValue(9999, out dropAll) ? dropAll[0] : 0;

            // ports
            ports = new List<PortInfo>();
            var dockerMap = new Dictionary<string, string>();
            foreach (var d in dock)
            {
                var f = d.Split(new[] { '\t' }, 2);
                if (f.Length < 2) continue;
                foreach (Match m in Regex.Matches(f[1], @"(?:[\d.]+|\[::\]):(\d+)(?:-(\d+))?->\d+(?:-\d+)?/(tcp|udp)"))
                    dockerMap[m.Groups[3].Value + ":" + m.Groups[1].Value] = f[0];
            }
            foreach (var l in ss)
            {
                var f = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 5) continue;
                var local = f[4];
                int c = local.LastIndexOf(':');
                if (c < 0) continue;
                int port;
                if (!int.TryParse(local.Substring(c + 1), out port)) continue;
                var bind = local.Substring(0, c);
                var proc = f.Length > 6 ? Regex.Match(string.Join(" ", f.Skip(6)), "\\(\\(\"([^\"]+)\"").Groups[1].Value : "";
                var pi = new PortInfo { Proto = f[0], Port = port, Bind = bind, Process = proc };
                pi.Public = !(bind.StartsWith("127.") || bind == "[::1]" || bind.StartsWith("[fe80") || bind.StartsWith("%lo"));
                string cont;
                if (dockerMap.TryGetValue(pi.Proto + ":" + port, out cont)) { pi.Docker = true; pi.Container = cont; }
                else if (proc == "docker-proxy") pi.Docker = true;
                var existing = ports.FirstOrDefault(x => x.Proto == pi.Proto && x.Port == pi.Port);
                if (existing != null) { if (!existing.Bind.Contains(bind)) existing.Bind += ", " + bind; existing.Public |= pi.Public; continue; }
                ports.Add(pi);
            }
        }

        long dropped;

        /// <summary>What the firewall does for traffic to this port: "offen", "gesperrt", "nur …".</summary>
        string Verdict(PortInfo p, out Color c)
        {
            if (!p.Public) { c = Theme.Muted; return "nur lokal (127.0.0.1)"; }
            if (Ephemeral(p)) { c = Theme.Muted; return "ausgehend (" + p.Process + ", kein Dienst)"; }
            if (p.Port == sshPort && p.Proto == "tcp") { c = Theme.Green; return "offen (SSH – immer erlaubt)"; }
            Func<FwRule, bool> match = r => (r.Proto == "both" || r.Proto == p.Proto) && PortIn(p.Port, r.Port);
            var deny = rules.FirstOrDefault(r => r.Kind == "deny" && match(r));
            if (deny != null) { c = Theme.Red; return deny.Source == "any" ? "geschlossen" : "gesperrt für " + deny.Source; }
            var allow = rules.Where(r => r.Kind == "allow" && match(r)).ToList();
            if (allow.Any(r => r.Source == "any")) { c = Theme.Green; return "offen (Regel)"; }
            if (allow.Count > 0) { c = Theme.Yellow; return strict ? "nur von " + string.Join(", ", allow.Select(a => a.Source)) : "offen"; }
            if (strict) { c = Theme.Red; return "geschlossen (Strikt-Modus)"; }
            c = Theme.Yellow; return "offen (keine Regel)";
        }

        static bool Ephemeral(PortInfo p) { return p.Proto == "udp" && !p.Docker && p.Port >= 32768; }

        static bool PortIn(int port, string spec)
        {
            var p = spec.Split(':');
            int a = int.Parse(p[0]), b = p.Length > 1 ? int.Parse(p[1]) : a;
            return port >= a && port <= b;
        }

        void Render()
        {
            hSub.Text = "iptables + ip6tables  ·  extern: " + extIf + "  ·  SSH-Port " + sshPort + (installed ? "  ·  RyvenPanel-Firewall aktiv (bootfest)" : "  ·  noch keine Regeln angelegt");
            cMode.Tip(strict ? "Strikt: alles zu, außer erlaubte Ports" : "Offen: alles erlaubt, außer gesperrte Ports/IPs");
            modeInfo.Text = strict ? "Strikt: alles zu, außer erlaubte Ports" : "Offen: alles erlaubt, außer gesperrte Ports/IPs";
            bStrict.Text = strict ? "Strikt-Modus beenden" : "Strikt-Modus aktivieren";
            bStrict.Width = TextRenderer.MeasureText(bStrict.Text, bStrict.Font).Width + 32;

            var rows = new List<ListViewItem>();
            int open = 0;
            foreach (var p in ports.OrderBy(x => x.Public ? 0 : 1).ThenBy(x => x.Port))
            {
                Color c;
                var v = Verdict(p, out c);
                if (p.Public && c != Theme.Red && c != Theme.Muted) open++;
                var row = Ui.Row(new RowTag { Key = p.Proto + ":" + p.Port, Dot = c, Data = p },
                    p.Port.ToString(), p.Proto.ToUpperInvariant(), p.Bind, p.Docker ? "🐳 " + (p.Container ?? "Docker") : p.Process,
                    p.Public ? "öffentlich" : "lokal", v);
                row.SubItems[5].ForeColor = c;
                row.SubItems[4].ForeColor = p.Public ? Theme.Text2 : Theme.Muted;
                rows.Add(row);
            }
            Ui.Fill(portList, rows);

            var rr = new List<ListViewItem>();
            var br = new List<ListViewItem>();
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                var key = i + "|" + r.Line();
                if (r.Kind == "block")
                {
                    var row = Ui.Row(new RowTag { Key = key, Dot = Theme.Red, Data = r }, r.Source, r.Packets + " Pakete", r.Comment);
                    br.Add(row);
                }
                else
                {
                    var c = r.Kind == "allow" ? Theme.Green : Theme.Red;
                    var row = Ui.Row(new RowTag { Key = key, Dot = c, Data = r }, (i + 1).ToString(), r.Kind == "allow" ? "Erlauben" : "Blockieren",
                        r.Proto == "both" ? "TCP+UDP" : r.Proto.ToUpperInvariant(), r.Port, r.Source == "any" ? "alle" : r.Source,
                        r.Packets + " / " + Theme.Bytes(r.Bytes), r.Comment);
                    row.SubItems[1].ForeColor = c;
                    rr.Add(row);
                }
            }
            Ui.Fill(ruleList, rr);
            Ui.Fill(blockList, br);

            cMode.Set(strict ? "Strikt" : "Offen", strict ? "Standard: blockieren" : "Standard: erlauben", strict ? Theme.Green : Theme.Yellow);
            cMode.BarColor = strict ? Theme.Green : Theme.Yellow;
            cOpen.Set(open.ToString(), ports.Count(p => p.Public) + " lauschen öffentlich", Theme.Text);
            cBlocked.Set(rules.Count(r => r.Kind == "block").ToString(), "Sperren auch für Docker", Theme.Text);
            cDropped.Set(dropped.ToString("#,0", CultureInfo.GetCultureInfo("de-DE")), "durch Strikt-Modus", Theme.Text);
        }

        async Task LoadLive()
        {
            if (!sshReady()) return;
            var r = await getSsh().Run(
                "ss -H -tn state established 2>/dev/null | awk '{l=$3; p=$4; sub(/.*:/,\"\",l); sub(/:[0-9]+$/,\"\",p); gsub(/^\\[|\\]$/,\"\",p); sub(/^::ffff:/,\"\",p); print l\"\\t\"p}' | sort | uniq -c | sort -rn | head -300;" +
                "echo @@NAT; conntrack -L -p tcp --state ESTABLISHED 2>/dev/null | awk '{for(i=1;i<=NF;i++){if($i ~ /^src=/ && !s){s=substr($i,5)} if($i ~ /^dport=/ && !d){d=substr($i,7)}} print d\"\\t\"s; s=\"\"; d=\"\"}' | sort | uniq -c | sort -rn | head -300", 30000);
            var rows = new List<ListViewItem>();
            bool nat = false;
            var seen = new HashSet<string>();
            foreach (var l in r.Out.Split('\n'))
            {
                if (l.Trim() == "@@NAT") { nat = true; continue; }
                var m = Regex.Match(l.Trim(), @"^(\d+)\s+(\d+)\t(\S+)$");
                if (!m.Success) continue;
                var ip = m.Groups[3].Value;
                if (ip.StartsWith("127.") || ip == "::1" || ip.StartsWith("172.")) continue;
                var key = m.Groups[2].Value + "|" + ip;
                if (!seen.Add(key)) continue;
                var info = IpIntel.Get(ip);
                var row = Ui.Row(new RowTag { Key = key, Dot = rules.Any(x => x.Kind == "block" && x.Source == ip) ? Theme.Red : Theme.Green },
                    m.Groups[2].Value, ip, m.Groups[1].Value, (nat ? "Docker" : "Host") + (info != null ? "  ·  " + (info.CountryCode ?? "") + " " + (info.Org ?? info.Isp ?? "") : ""));
                rows.Add(row);
            }
            Ui.Fill(liveList, rows);
        }

        // ───────────────────────────── Menus ─────────────────────────────
        void PortMenu(Point at)
        {
            var p = Ui.SelData<PortInfo>(portList);
            if (p == null) return;
            var m = Ui.Menu();
            var proto = p.Proto;
            m.Item("Port " + p.Port + "/" + proto + " öffnen (für alle)", async () => await Apply(rules.Concat(new[] { new FwRule { Kind = "allow", Proto = proto, Port = p.Port.ToString(), Source = "any", Comment = p.Container ?? p.Process } }).Where(r => !(r.Kind == "deny" && r.Proto == proto && r.Port == p.Port.ToString())).ToList(), "Port " + p.Port + " geöffnet"));
            m.Item("Port " + p.Port + "/" + proto + " schließen", async () =>
            {
                if (p.Port == sshPort) { setStatus("Der SSH-Port bleibt immer offen (Schutz vor Aussperren).", Theme.Yellow); return; }
                if (!Dialogs.Confirm(FindForm(), "Port schließen", "Port " + p.Port + "/" + proto + " (" + (p.Container ?? p.Process) + ") von außen sperren?\n\nDer Dienst läuft weiter, ist aber aus dem Internet nicht mehr erreichbar.")) return;
                await Apply(rules.Where(r => !(r.Kind == "allow" && r.Proto == proto && r.Port == p.Port.ToString())).Concat(new[] { new FwRule { Kind = "deny", Proto = proto, Port = p.Port.ToString(), Source = "any", Comment = p.Container ?? p.Process } }).ToList(), "Port " + p.Port + " geschlossen");
            });
            m.Item("Nur für bestimmte IP erlauben …", async () => await AddRule("allow", p));
            m.Show(portList, at);
        }

        void RuleMenu(ListView lv, Point at)
        {
            if (lv.SelectedItems.Count == 0) return;
            var m = Ui.Menu();
            m.Item("Regel entfernen  (Entf)", async () => await RemoveSelected(lv));
            var r = Ui.SelData<FwRule>(lv);
            if (r != null && r.Kind == "block") m.Item("IP kopieren", () => Clipboard.SetText(r.Source));
            m.Show(lv, at);
        }

        async Task RemoveSelected(ListView lv)
        {
            var sel = lv.SelectedItems.Cast<ListViewItem>().Select(i => ((RowTag)i.Tag).Data as FwRule).Where(x => x != null).ToList();
            if (sel.Count == 0) return;
            if (!Dialogs.Confirm(FindForm(), "Regel entfernen", sel.Count + " Regel(n) entfernen?\n\n" + string.Join("\n", sel.Select(x => x.Line())))) return;
            await Apply(rules.Where(x => !sel.Contains(x)).ToList(), sel.Count + " Regel(n) entfernt");
        }

        // ───────────────────────────── Actions ─────────────────────────────
        async Task AddRule(string kind, PortInfo p)
        {
            var d = new FormDialog(kind == "allow" ? "Port öffnen" : "Port schließen",
                kind == "allow" ? "Erlaubt eingehende Verbindungen auf diesen Port. Gilt auch für Docker-Container." :
                                  "Sperrt den Port von außen (Dienst läuft weiter). Gilt auch für Docker-Container.");
            var port = d.AddText("Port oder Bereich (z. B. 25565 oder 25500:25600)", p != null ? p.Port.ToString() : "");
            var proto = d.AddCombo("Protokoll", new[] { "tcp", "udp", "both" }, p != null ? p.Proto : "tcp");
            var src = d.AddText("Quelle (IP/Netz, leer = alle)", "");
            var comment = d.AddText("Kommentar", p != null ? (p.Container ?? p.Process) : "");
            using (d)
            {
                if (!d.Run(FindForm(), kind == "allow" ? "Öffnen" : "Schließen")) return;
                var pt = port.Box.Text.Trim().Replace("-", ":");
                var s = src.Box.Text.Trim(); if (s.Length == 0) s = "any";
                if (!FwRule.ValidPort(pt)) { setStatus("Ungültiger Port.", Theme.Red); return; }
                if (!FwRule.ValidSource(s)) { setStatus("Ungültige IP/Netz.", Theme.Red); return; }
                if (kind == "deny" && PortIn(sshPort, pt) && (string)proto.SelectedItem != "udp") { setStatus("Der SSH-Port " + sshPort + " kann nicht gesperrt werden (Schutz vor Aussperren).", Theme.Red); return; }
                var nr = new FwRule { Kind = kind, Proto = (string)proto.SelectedItem, Port = pt, Source = s, Comment = comment.Box.Text.Trim() };
                await Apply(rules.Concat(new[] { nr }).ToList(), (kind == "allow" ? "Port geöffnet: " : "Port geschlossen: ") + pt);
            }
        }

        /// <summary>Public so the user/player views can block a player's IP.</summary>
        public async Task BlockIp(string ip, string comment)
        {
            if (ip == null)
            {
                var d = new FormDialog("IP sperren", "Blockiert jeglichen Verkehr dieser IP – auch zu Minecraft (Docker). Sofort wirksam und bootfest.");
                var f = d.AddText("IP oder Netz (z. B. 1.2.3.4 oder 1.2.3.0/24)", "");
                var c = d.AddText("Grund / Kommentar", "");
                using (d)
                {
                    if (!d.Run(FindForm(), "Sperren")) return;
                    ip = f.Box.Text.Trim(); comment = c.Box.Text.Trim();
                }
            }
            if (!FwRule.ValidSource(ip) || ip == "any") { setStatus("Ungültige IP.", Theme.Red); return; }
            if (!sshReady()) { setStatus("SSH nicht eingerichtet.", Theme.Yellow); return; }
            if (rules.Count == 0 && !installed) await Reload();
            if (rules.Any(r => r.Kind == "block" && r.Source == ip)) { setStatus(ip + " ist bereits gesperrt.", Theme.Muted); return; }
            await Apply(rules.Concat(new[] { new FwRule { Kind = "block", Proto = "-", Port = "-", Source = ip, Comment = comment ?? "" } }).ToList(), "IP gesperrt: " + ip);
        }

        async Task ToggleStrict()
        {
            if (!strict)
            {
                var pub = ports.Where(p => p.Public && !Ephemeral(p) && !(p.Port == sshPort && p.Proto == "tcp")).ToList();
                var missing = pub.Where(p => !rules.Any(r => r.Kind == "allow" && (r.Proto == p.Proto || r.Proto == "both") && PortIn(p.Port, r.Port))).ToList();
                var msg = "Im Strikt-Modus wird alles von außen blockiert, was nicht ausdrücklich erlaubt ist.\n\n" +
                          "SSH (Port " + sshPort + ") bleibt immer offen. Bestehende Verbindungen bleiben bestehen.\n\n" +
                          (missing.Count > 0 ? "Diese aktuell öffentlichen Ports werden automatisch erlaubt (damit nichts kaputtgeht):\n" +
                              string.Join("\n", missing.Select(p => "  • " + p.Port + "/" + p.Proto + "  " + (p.Container ?? p.Process))) + "\n\n" : "") +
                          "Danach testet die App eine neue SSH-Verbindung. Klappt das nicht, setzt der Server die Firewall nach 60 s automatisch zurück.";
                if (!Dialogs.Confirm(FindForm(), "Strikt-Modus", msg)) return;
                var nr = rules.ToList();
                foreach (var p in missing) nr.Add(new FwRule { Kind = "allow", Proto = p.Proto, Port = p.Port.ToString(), Source = "any", Comment = "auto: " + (p.Container ?? p.Process) });
                await Apply(nr, "Strikt-Modus aktiv", true);
            }
            else
            {
                if (!Dialogs.Confirm(FindForm(), "Strikt-Modus", "Strikt-Modus beenden? Danach ist wieder alles erreichbar, außer gesperrten Ports/IPs.")) return;
                await Apply(rules, "Strikt-Modus beendet", false);
            }
        }

        /// <summary>Writes the rules file, (re)installs the apply script + unit, applies with auto-rollback and verifies with a fresh SSH connection.</summary>
        async Task Apply(List<FwRule> newRules, string label, bool? strictMode = null)
        {
            if (busy) return;
            bool st = strictMode ?? strict;
            var conf = new StringBuilder("# RyvenPanel Firewall – wird von RyvenPanel verwaltet\n# Format: mode open|strict · allow|deny <tcp|udp|both> <port[:bis]> [quelle] [# kommentar] · block <ip> [# kommentar]\n");
            conf.Append("mode " + (st ? "strict" : "open") + "\n");
            foreach (var r in newRules) conf.Append(r.Line() + "\n");

            busy = true;
            progress.Set(double.NaN);
            setStatus(label + " – wird angewendet …", Theme.Muted);
            try
            {
                var script = InstallScript() +
                    "mkdir -p /etc/pxmx\n" +
                    "[ -f " + ConfPath + " ] && cp -f " + ConfPath + " " + ConfPath + ".prev || : > " + ConfPath + ".prev\n" +
                    "printf '%s\\n' " + Ssh.B64(conf.ToString()) + " > " + ConfPath + ".new && mv -f " + ConfPath + ".new " + ConfPath + "\n" +
                    "touch /run/pxmx-fw.pending\n" +
                    "setsid sh -c 'sleep 60; if [ -f /run/pxmx-fw.pending ]; then cp -f " + ConfPath + ".prev " + ConfPath + "; /usr/local/sbin/pxmx-fw apply; rm -f /run/pxmx-fw.pending; logger pxmx-fw: rollback; fi' >/dev/null 2>&1 < /dev/null &\n" +
                    "/usr/local/sbin/pxmx-fw apply\n";
                var r = await getSsh().Run(script, 60000);
                Out(label, r.Out + (r.Err.Length > 0 ? "\n" + r.Err : ""), !r.Ok);
                if (!r.Ok) { setStatus(label + " fehlgeschlagen: " + r.Message, Theme.Red); return; }
                // verify with a brand-new connection, then confirm (cancels the rollback)
                var ok = await getSsh().Run("rm -f /run/pxmx-fw.pending && echo bestätigt", 20000);
                if (!ok.Ok)
                {
                    setStatus("Neue SSH-Verbindung nach der Änderung fehlgeschlagen – der Server setzt die Firewall in 60 s automatisch zurück.", Theme.Red);
                    Out("Rollback", "Die Bestätigung kam nicht durch: " + ok.Message + "\nDie alte Konfiguration wird automatisch wiederhergestellt.", true);
                    return;
                }
                setStatus(label + " ✓", Theme.Green);
            }
            finally { busy = false; progress.Set(-1); }
            await Reload();
        }

        /// <summary>Installs /usr/local/sbin/pxmx-fw and the systemd unit (idempotent).</summary>
        static string InstallScript()
        {
            const string fw = @"#!/bin/bash
# RyvenPanel firewall – rebuilds RYVEN-IN (INPUT) and RYVEN-FWD (DOCKER-USER) from /etc/pxmx/firewall.conf
set -u
CONF=/etc/pxmx/firewall.conf
EXT=$(ip route show default 2>/dev/null | awk '{for(i=1;i<=NF;i++) if($i==""dev""){print $(i+1); exit}}')
[ -z ""$EXT"" ] && EXT=eth0
SSHP=$(sshd -T 2>/dev/null | awk '/^port /{print $2; exit}')
[ -z ""$SSHP"" ] && SSHP=22
MODE=open
[ -f ""$CONF"" ] && grep -qx 'mode strict' ""$CONF"" && MODE=strict

setup() { # $1 = iptables|ip6tables
  local T=$1
  command -v $T >/dev/null || return 0
  $T -N RYVEN-IN 2>/dev/null; $T -F RYVEN-IN
  $T -N RYVEN-FWD 2>/dev/null; $T -F RYVEN-FWD
  $T -N DOCKER-USER 2>/dev/null
  $T -C INPUT -j RYVEN-IN 2>/dev/null || $T -I INPUT 1 -j RYVEN-IN
  $T -C DOCKER-USER -j RYVEN-FWD 2>/dev/null || $T -I DOCKER-USER 1 -j RYVEN-FWD
  $T -A RYVEN-IN -i lo -j ACCEPT
  $T -A RYVEN-FWD ! -i ""$EXT"" -j RETURN
}

isv6() { case ""$1"" in *:*) return 0;; *) return 1;; esac; }

rule() { # tool chain args...
  local T=$1; shift
  command -v $T >/dev/null && $T ""$@""
}

for T in iptables ip6tables; do setup $T; done

# blocks first (both chains), then SSH, then deny, then allow
i=0
if [ -f ""$CONF"" ]; then
  while IFS= read -r line || [ -n ""$line"" ]; do
    line=${line%%#*}; set -- $line
    [ $# -eq 0 ] && continue
    case ""$1"" in
      mode) continue;;
      block)
        src=$2
        if isv6 ""$src""; then T=ip6tables; else T=iptables; fi
        rule $T -A RYVEN-IN -s ""$src"" -m comment --comment ""rp:$i"" -j DROP
        rule $T -A RYVEN-FWD -s ""$src"" -m comment --comment ""rp:$i"" -j DROP
        ;;
    esac
    i=$((i+1))
  done < ""$CONF""
fi

for T in iptables ip6tables; do
  rule $T -A RYVEN-IN -m conntrack --ctstate ESTABLISHED,RELATED -j ACCEPT
  rule $T -A RYVEN-FWD -m conntrack --ctstate ESTABLISHED,RELATED -j RETURN
  rule $T -A RYVEN-IN -p tcp --dport ""$SSHP"" -j ACCEPT
done

pass() { # kind  -> emits rules for allow/deny entries
  local want=$1 i=0
  [ -f ""$CONF"" ] || return 0
  while IFS= read -r line || [ -n ""$line"" ]; do
    line=${line%%#*}; set -- $line
    [ $# -eq 0 ] && continue
    [ ""$1"" = mode ] && continue
    if [ ""$1"" = ""$want"" ]; then
      local proto=$2 port=$3 src=${4:-any} protos tool tools
      [ ""$proto"" = both ] && protos=""tcp udp"" || protos=$proto
      if [ ""$src"" = any ]; then tools=""iptables ip6tables""; elif isv6 ""$src""; then tools=ip6tables; else tools=iptables; fi
      for tool in $tools; do for p in $protos; do
        local S=()
        [ ""$src"" != any ] && S=(-s ""$src"")
        if [ ""$want"" = deny ]; then
          rule $tool -A RYVEN-IN -i ""$EXT"" -p $p --dport ""$port"" ""${S[@]}"" -m comment --comment ""rp:$i"" -j DROP
          rule $tool -A RYVEN-FWD -p $p -m conntrack --ctorigdstport ""$port"" --ctdir ORIGINAL ""${S[@]}"" -m comment --comment ""rp:$i"" -j DROP
        else
          rule $tool -A RYVEN-IN -p $p --dport ""$port"" ""${S[@]}"" -m comment --comment ""rp:$i"" -j ACCEPT
          rule $tool -A RYVEN-FWD -p $p -m conntrack --ctorigdstport ""$port"" --ctdir ORIGINAL ""${S[@]}"" -m comment --comment ""rp:$i"" -j RETURN
        fi
      done; done
    fi
    i=$((i+1))
  done < ""$CONF""
}
pass deny
pass allow

for T in iptables ip6tables; do
  if [ ""$MODE"" = strict ]; then
    rule $T -A RYVEN-IN -p icmp -j ACCEPT 2>/dev/null
    rule $T -A RYVEN-IN -p ipv6-icmp -j ACCEPT 2>/dev/null
    rule $T -A RYVEN-IN -i ""$EXT"" -m comment --comment rp:9999 -j DROP
    rule $T -A RYVEN-FWD -m conntrack --ctstate NEW -m comment --comment rp:9999 -j DROP
  fi
  rule $T -A RYVEN-IN -j RETURN
  rule $T -A RYVEN-FWD -j RETURN
done
echo ""RyvenPanel-Firewall angewendet: Modus $MODE, extern $EXT, SSH $SSHP, $(grep -cE '^(allow|deny|block) ' ""$CONF"" 2>/dev/null || echo 0) Regeln.""
";
            const string unit = @"[Unit]
Description=RyvenPanel Firewall (iptables, Docker-kompatibel)
After=network-online.target docker.service
Wants=network-online.target

[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=/usr/local/sbin/pxmx-fw apply

[Install]
WantedBy=multi-user.target
";
            return "printf '%s' " + Ssh.B64(fw) + " > /usr/local/sbin/pxmx-fw && chmod 755 /usr/local/sbin/pxmx-fw || exit 1\n" +
                   "printf '%s' " + Ssh.B64(unit) + " > /etc/systemd/system/pxmx-fw.service\n" +
                   "systemctl daemon-reload >/dev/null 2>&1; systemctl enable pxmx-fw >/dev/null 2>&1\n" +
                   "mkdir -p /etc/systemd/system/docker.service.d\n" +
                   "printf '[Service]\\nExecStartPost=-/usr/local/sbin/pxmx-fw apply\\n' > /etc/systemd/system/docker.service.d/pxmx-fw.conf\n" +
                   "systemctl daemon-reload >/dev/null 2>&1\n";
        }
    }
}
