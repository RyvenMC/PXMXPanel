using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    class Session
    {
        public string Name, Ip, Reason;
        public DateTime Start;
        public DateTime? End;
        public bool EndedByRestart;
        /// <summary>Entered by hand in /var/lib/pxmx/manual-ips.txt instead of read from a proxy log.</summary>
        public bool Manual;
        public readonly List<string> Servers = new List<string>();
        public TimeSpan Duration { get { return (End ?? DateTime.Now) - Start; } }
    }

    class IpInfo
    {
        public string Ip, Asn, Org, Isp, Country, CountryCode, City, Source;
        public bool Vpn, Proxy, Tor, Relay, Hosting;
        public DateTime Fetched;

        public bool Suspicious { get { return Vpn || Proxy || Tor || Relay || Hosting; } }

        public string Flags
        {
            get
            {
                var l = new List<string>();
                if (Vpn) l.Add("VPN");
                if (Proxy) l.Add("Proxy");
                if (Tor) l.Add("Tor");
                if (Relay) l.Add("Relay");
                if (Hosting && !Vpn && !Proxy) l.Add("Rechenzentrum");
                return l.Count == 0 ? "–" : string.Join(", ", l);
            }
        }

        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                { "ip", Ip }, { "asn", Asn }, { "org", Org }, { "isp", Isp }, { "country", Country }, { "cc", CountryCode }, { "city", City },
                { "vpn", Vpn }, { "proxy", Proxy }, { "tor", Tor }, { "relay", Relay }, { "hosting", Hosting }, { "src", Source },
                { "at", Fetched.ToString("o") }
            };
        }

        public static IpInfo FromDict(object d)
        {
            DateTime at;
            DateTime.TryParse(J.S(d, "at"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out at);
            return new IpInfo
            {
                Ip = J.S(d, "ip"), Asn = J.S(d, "asn"), Org = J.S(d, "org"), Isp = J.S(d, "isp"), Country = J.S(d, "country"),
                CountryCode = J.S(d, "cc"), City = J.S(d, "city"), Vpn = J.B(d, "vpn"), Proxy = J.B(d, "proxy"), Tor = J.B(d, "tor"),
                Relay = J.B(d, "relay"), Hosting = J.B(d, "hosting"), Source = J.S(d, "src"), Fetched = at
            };
        }
    }

    /// <summary>IP → ASN / provider / VPN lookup (vpnapi.io with key, ipwho.is fallback) with a local cache.</summary>
    static class IpIntel
    {
        static readonly Dictionary<string, IpInfo> cache = new Dictionary<string, IpInfo>();
        static bool loaded;

        static readonly Regex HostingOrg = new Regex(
            @"hosting|host|cloud|data ?cent|datacenter|server|vps|colo|ovh|hetzner|digitalocean|amazon|aws|google|microsoft|azure|oracle|" +
            @"m247|choopa|vultr|linode|akamai|contabo|leaseweb|scaleway|ionos|netcup|strato|psychz|datacamp|cdn77|g-core|gcore|" +
            @"nordvpn|expressvpn|surfshark|proton|mullvad|private internet|cyberghost|tefincom|packethub|zenlayer|cogent",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        static string CachePath
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PXMX-ServerSuite");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "ipcache.json");
            }
        }

        static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(CachePath)) return;
                foreach (var o in J.L(J.Parse(File.ReadAllText(CachePath, Encoding.UTF8))))
                {
                    var i = IpInfo.FromDict(o);
                    if (i.Ip != null) cache[i.Ip] = i;
                }
            }
            catch { }
        }

        public static void Save()
        {
            try { File.WriteAllText(CachePath, J.ToJson(cache.Values.Select(v => v.ToDict()).ToList()), Encoding.UTF8); } catch { }
        }

        public static IpInfo Get(string ip)
        {
            Load();
            IpInfo i;
            return ip != null && cache.TryGetValue(ip, out i) ? i : null;
        }

        public static bool IsPrivate(string ip)
        {
            IPAddress a;
            if (!IPAddress.TryParse(ip, out a)) return true;
            if (IPAddress.IsLoopback(a)) return true;
            var b = a.GetAddressBytes();
            if (b.Length == 4) return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] == 169 && b[1] == 254;
            return a.IsIPv6LinkLocal || a.IsIPv6SiteLocal || (b[0] & 0xfe) == 0xfc;
        }

        static string Fetch(string url)
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "PXMX-ServerSuite/0.1.0";
            req.Timeout = 12000;
            req.Accept = "application/json";
            try
            {
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    return sr.ReadToEnd();
            }
            catch (WebException ex)
            {
                var r = ex.Response as HttpWebResponse;
                if (r != null)
                    using (r)
                    using (var sr = new StreamReader(r.GetResponseStream(), Encoding.UTF8))
                        throw new Exception("HTTP " + (int)r.StatusCode + ": " + sr.ReadToEnd());
                throw;
            }
        }

        public static IpInfo Lookup(string ip, string vpnKey)
        {
            Load();
            IpInfo info;
            if (!string.IsNullOrEmpty(vpnKey))
            {
                var d = J.Parse(Fetch("https://vpnapi.io/api/" + Uri.EscapeDataString(ip) + "?key=" + Uri.EscapeDataString(vpnKey)));
                if (J.Get(d, "security") == null) throw new Exception(J.S(d, "message") ?? "Unerwartete Antwort von vpnapi.io");
                var sec = J.Get(d, "security");
                var loc = J.Get(d, "location");
                var net = J.Get(d, "network");
                info = new IpInfo
                {
                    Ip = ip,
                    Vpn = J.B(sec, "vpn"), Proxy = J.B(sec, "proxy"), Tor = J.B(sec, "tor"), Relay = J.B(sec, "relay"),
                    Country = J.S(loc, "country"), CountryCode = J.S(loc, "country_code"), City = J.S(loc, "city"),
                    Asn = J.S(net, "autonomous_system_number"), Org = J.S(net, "autonomous_system_organization"),
                    Source = "vpnapi.io"
                };
                info.Isp = info.Org;
            }
            else
            {
                var d = J.Parse(Fetch("https://ipwho.is/" + Uri.EscapeDataString(ip)));
                if (!J.B(d, "success")) throw new Exception(J.S(d, "message") ?? "ipwho.is: keine Daten");
                var c = J.Get(d, "connection");
                info = new IpInfo
                {
                    Ip = ip,
                    Country = J.S(d, "country"), CountryCode = J.S(d, "country_code"), City = J.S(d, "city"),
                    Asn = J.S(c, "asn"), Org = J.S(c, "org"), Isp = J.S(c, "isp"),
                    Source = "ipwho.is"
                };
            }
            if (!string.IsNullOrEmpty(info.Asn) && !info.Asn.StartsWith("AS")) info.Asn = "AS" + info.Asn;
            info.Hosting = HostingOrg.IsMatch((info.Org ?? "") + " " + (info.Isp ?? ""));
            info.Fetched = DateTime.Now;
            cache[ip] = info;
            return info;
        }
    }

}
