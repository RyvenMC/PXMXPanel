using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace RyvenPanel
{
    static class J
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 512 };

        public static object Parse(string s) { return Ser.DeserializeObject(s); }
        public static string ToJson(object o) { return Ser.Serialize(o); }
        public static Dictionary<string, object> D(object o) { return o as Dictionary<string, object>; }

        public static object Get(object d, string k)
        {
            var dict = D(d);
            if (dict == null || !dict.ContainsKey(k)) return null;
            return dict[k];
        }

        public static string S(object d, string k)
        {
            var v = Get(d, k);
            if (v == null) return null;
            return Convert.ToString(v, CultureInfo.InvariantCulture);
        }

        public static double N(object d, string k)
        {
            var v = Get(d, k);
            if (v == null) return 0;
            if (v is bool) return (bool)v ? 1 : 0;
            double r;
            if (double.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out r)) return r;
            return 0;
        }

        public static bool B(object d, string k)
        {
            var v = Get(d, k);
            if (v == null) return false;
            if (v is bool) return (bool)v;
            var s = Convert.ToString(v, CultureInfo.InvariantCulture).Trim().ToLowerInvariant();
            return s == "true" || s == "1" || s == "yes";
        }

        public static IEnumerable<object> L(object o)
        {
            var a = o as object[];
            if (a != null) return a;
            var al = o as ArrayList;
            if (al != null) return al.Cast<object>();
            return new object[0];
        }
    }

    class ApiResult
    {
        public int Status;
        public Dictionary<string, object> Json;
        public string Raw;

        public bool Ok
        {
            get { return Status >= 200 && Status < 300 && (Json == null || J.S(Json, "status") != "error"); }
        }

        public object Data { get { return J.Get(Json, "data"); } }

        public string Error
        {
            get
            {
                if (Status == 0) return Raw ?? "Keine Verbindung";
                var e = J.S(Json, "error_data");
                if (string.IsNullOrEmpty(e)) e = J.S(Json, "error");
                if (string.IsNullOrEmpty(e)) e = "HTTP " + Status;
                return e;
            }
        }
    }

    class Api
    {
        public static string PinnedThumb, GatewayThumb;

        public string BaseUrl;
        /// <summary>Configured address for display (BaseUrl may be a local SSH forward).</summary>
        public string DisplayUrl;
        public string Token;
        public event Action Unauthorized;

        public enum ConnState { Online, Disrupted, SessionExpired }
        static ConnState connState = ConnState.Online;
        public static ConnState State { get { return connState; } }
        /// <summary>Raised (from any thread) when the shared gateway connection state changes.</summary>
        public static event Action<ConnState> StateChanged;
        public static string StateText(ConnState c)
        {
            return c == ConnState.Online ? "Online" : c == ConnState.Disrupted ? "Verbindung gestört" : "Sitzung abgelaufen";
        }
        static void SetState(ConnState c)
        {
            if (connState == c) return;
            connState = c;
            var h = StateChanged;
            if (h != null) { try { h(c); } catch { } }
        }
        /// <summary>True for network failures (Status 0 except certificate problems) and gateway 502/503/504.</summary>
        public static bool IsTransient(ApiResult r)
        {
            if (r.Status == 0) return (r.Raw ?? "").IndexOf("Zertifikat", StringComparison.Ordinal) < 0;
            return r.Status == 502 || r.Status == 503 || r.Status == 504;
        }

        public Api(string baseUrl, string token)
        {
            BaseUrl = baseUrl.TrimEnd('/');
            Token = token;
        }

        public static bool ValidateCert(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) return true;
            if (cert == null) return false;
            var tp = new X509Certificate2(cert).Thumbprint;
            return (!string.IsNullOrEmpty(PinnedThumb) && string.Equals(tp, PinnedThumb, StringComparison.OrdinalIgnoreCase)) ||
                   (!string.IsNullOrEmpty(GatewayThumb) && string.Equals(tp, GatewayThumb, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Reads the server certificate (without trusting it) so the user can pin it.</summary>
        public static string FetchCert(string url, out bool publiclyValid)
        {
            var u = new Uri(url);
            string thumb = null;
            bool valid = false;
            using (var tcp = new TcpClient())
            {
                var ar = tcp.BeginConnect(u.Host, u.Port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(8000)) throw new Exception("Keine Verbindung zu " + u.Host + ":" + u.Port);
                tcp.EndConnect(ar);
                using (var ssl = new SslStream(tcp.GetStream(), false, (s, c, ch, e) =>
                {
                    if (c != null) thumb = new X509Certificate2(c).Thumbprint;
                    valid = e == SslPolicyErrors.None;
                    return true;
                }))
                {
                    ssl.AuthenticateAsClient(u.Host, null, (System.Security.Authentication.SslProtocols)3072, false);
                }
            }
            publiclyValid = valid;
            return thumb;
        }

        public Task<ApiResult> Get(string path) { return Send("GET", path, null, null); }
        public Task<ApiResult> PostJson(string path, object body) { return Send("POST", path, J.ToJson(body), "application/json"); }

        public Task<ApiResult> Send(string method, string path, string body, string contentType)
        {
            return SendRaw(method, path, body == null ? null : Encoding.UTF8.GetBytes(body), contentType, null, 20000);
        }

        public async Task<ApiResult> SendRaw(string method, string path, byte[] bytes, string contentType, Dictionary<string, string> headers, int timeoutMs)
        {
            var r = await SendOnce(method, path, bytes, contentType, headers, timeoutMs);
            // idempotent reads: one retry after a short backoff on network errors / gateway restarts
            if (method == "GET" && IsTransient(r))
            {
                await Task.Delay(700);
                r = await SendOnce(method, path, bytes, contentType, headers, timeoutMs);
            }
            if (IsTransient(r)) SetState(ConnState.Disrupted);
            else if (r.Status == 401) SetState(ConnState.SessionExpired);
            else SetState(ConnState.Online);
            return r;
        }

        async Task<ApiResult> SendOnce(string method, string path, byte[] bytes, string contentType, Dictionary<string, string> headers, int timeoutMs)
        {
            HttpWebRequest req;
            try
            {
                req = (HttpWebRequest)WebRequest.Create(BaseUrl + path);
            }
            catch (Exception ex)
            {
                return new ApiResult { Status = 0, Raw = ex.Message };
            }
            req.Method = method;
            req.Accept = "application/json";
            req.UserAgent = "PXMX-ServerSuite/0.1.0";
            req.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (!string.IsNullOrEmpty(Token)) req.Headers["Authorization"] = "Bearer " + Token;
            if (headers != null) foreach (var h in headers) req.Headers[h.Key] = h.Value;
            req.AllowWriteStreamBuffering = bytes == null || bytes.Length < 8 * 1024 * 1024;

            HttpWebResponse resp = null;
            try
            {
                if (bytes != null)
                {
                    req.ContentType = contentType ?? "application/json";
                    req.ContentLength = bytes.Length;
                    using (var s = await req.GetRequestStreamAsync())
                        await s.WriteAsync(bytes, 0, bytes.Length);
                }
                var t = req.GetResponseAsync();
                if (await Task.WhenAny(t, Task.Delay(timeoutMs)) != t)
                {
                    req.Abort();
                    return new ApiResult { Status = 0, Raw = "Zeitüberschreitung" };
                }
                resp = (HttpWebResponse)await t;
            }
            catch (WebException ex)
            {
                resp = ex.Response as HttpWebResponse;
                if (resp == null)
                {
                    var msg = ex.Message;
                    if (ex.Status == WebExceptionStatus.TrustFailure)
                        msg = "Zertifikat des Panels wird nicht vertraut (hat es sich geändert?).";
                    return new ApiResult { Status = 0, Raw = msg };
                }
            }
            catch (Exception ex)
            {
                return new ApiResult { Status = 0, Raw = ex.Message };
            }

            using (resp)
            {
                string text;
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    text = await sr.ReadToEndAsync();
                var r = new ApiResult { Status = (int)resp.StatusCode, Raw = text };
                try { r.Json = J.D(J.Parse(text)); } catch { }
                if ((r.Status == 401 || (r.Status == 403 && J.S(r.Json, "error") == "ACCESS_DENIED")) && Unauthorized != null)
                    Unauthorized();
                return r;
            }
        }
    }

    class Settings
    {
        public string Url = "";
        public string Username = "";
        public string Thumb;
        public string TokenEnc;
        public string SshHost = "";
        public string SshUser = "root";
        public int SshPort = 22;
        public string SshKey;
        public string VpnKeyEnc;
        public string DbPasswordEnc;
        public string DbHost = "mariadb";
        public string DbName = "minecraft";
        public string DbUser = "pxmx";
        public int WinX, WinY, WinW, WinH;
        public string GatewayUrl, GatewayThumb, TeamUser, TeamTokenEnc;
        /// <summary>Last login mode on the login screen: true = team account (gateway), false = owner (Crafty).</summary>
        public bool TeamMode = true;
        public bool SetupComplete;
        public bool WinMax;

        static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PXMX-ServerSuite"); }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.json"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (File.Exists(FilePath))
                {
                    var d = J.D(J.Parse(File.ReadAllText(FilePath, Encoding.UTF8)));
                    if (d != null)
                    {
                        s.Url = J.S(d, "url") ?? s.Url;
                        s.Username = J.S(d, "username") ?? "";
                        s.Thumb = J.S(d, "thumb");
                        s.TokenEnc = J.S(d, "token");
                        s.SshHost = J.S(d, "ssh_host") ?? s.SshHost;
                        s.SshUser = J.S(d, "ssh_user") ?? s.SshUser;
                        if (J.N(d, "ssh_port") > 0) s.SshPort = (int)J.N(d, "ssh_port");
                        s.SshKey = J.S(d, "ssh_key");
                        s.VpnKeyEnc = J.S(d, "vpnapi_key");
                        s.DbPasswordEnc = J.S(d, "db_password");
                        s.DbHost = J.S(d, "db_host") ?? s.DbHost;
                        s.DbName = J.S(d, "db_name") ?? s.DbName;
                        s.DbUser = J.S(d, "db_user") ?? s.DbUser;
                        s.WinX = (int)J.N(d, "win_x"); s.WinY = (int)J.N(d, "win_y");
                        s.WinW = (int)J.N(d, "win_w"); s.WinH = (int)J.N(d, "win_h");
                        s.WinMax = J.B(d, "win_max");
                        s.GatewayUrl = J.S(d, "gateway_url"); s.GatewayThumb = J.S(d, "gateway_thumb");
                        s.TeamUser = J.S(d, "team_user"); s.TeamTokenEnc = J.S(d, "team_token");
                        s.SetupComplete = J.B(d, "setup_complete");
                        if (J.Get(d, "team_mode") != null) s.TeamMode = J.B(d, "team_mode");
                        else s.TeamMode = string.IsNullOrEmpty(s.TokenEnc) && string.IsNullOrEmpty(s.Username);
                    }
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                var d = new Dictionary<string, object>
                {
                    { "url", Url }, { "username", Username }, { "thumb", Thumb }, { "token", TokenEnc },
                    { "ssh_host", SshHost }, { "ssh_user", SshUser }, { "ssh_port", SshPort }, { "ssh_key", SshKey }, { "vpnapi_key", VpnKeyEnc },
                    { "db_password", DbPasswordEnc }, { "db_host", DbHost }, { "db_name", DbName }, { "db_user", DbUser },
                    { "win_x", WinX }, { "win_y", WinY }, { "win_w", WinW }, { "win_h", WinH }, { "win_max", WinMax },
                    { "gateway_url", GatewayUrl }, { "gateway_thumb", GatewayThumb }, { "team_user", TeamUser }, { "team_token", TeamTokenEnc },
                    { "team_mode", TeamMode }, { "setup_complete", SetupComplete }
                };
                File.WriteAllText(FilePath, J.ToJson(d), Encoding.UTF8);
            }
            catch { }
        }

        public string VpnKey
        {
            get
            {
                if (string.IsNullOrEmpty(VpnKeyEnc)) return null;
                try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(VpnKeyEnc), null, DataProtectionScope.CurrentUser)); }
                catch { return null; }
            }
            set
            {
                VpnKeyEnc = string.IsNullOrEmpty(value) ? null : Convert.ToBase64String(
                    ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
            }
        }

        static string Dec(string enc)
        {
            if (string.IsNullOrEmpty(enc)) return null;
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(enc), null, DataProtectionScope.CurrentUser)); }
            catch { return null; }
        }

        static string Enc(string v)
        {
            return string.IsNullOrEmpty(v) ? null : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(v), null, DataProtectionScope.CurrentUser));
        }

        /// <summary>Gateway session token of a team member (DPAPI-encrypted).</summary>
        public string TeamToken { get { return Dec(TeamTokenEnc); } set { TeamTokenEnc = Enc(value); } }
        public string DatabasePassword { get { return Dec(DbPasswordEnc); } set { DbPasswordEnc = Enc(value); } }

        /// <summary>Token is stored encrypted with Windows DPAPI (only your Windows user can decrypt it).</summary>
        public string Token
        {
            get
            {
                if (string.IsNullOrEmpty(TokenEnc)) return null;
                try
                {
                    return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(TokenEnc), null, DataProtectionScope.CurrentUser));
                }
                catch { return null; }
            }
            set
            {
                TokenEnc = value == null ? null : Convert.ToBase64String(
                    ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
            }
        }
    }

    class ServerItem
    {
        public string Id, Name, Type;
        public int Port;
        public double XmxBytes;
        public Dictionary<string, object> Stats;

        /// <summary>Parses "-Xmx12G" / "-Xmx4096M" from the start command.</summary>
        public static double ParseXmx(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return 0;
            var m = Regex.Match(cmd, @"-Xmx(\d+)([kKmMgGtT]?)");
            if (!m.Success) return 0;
            double v = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            switch (m.Groups[2].Value.ToUpperInvariant())
            {
                case "K": return v * 1024;
                case "M": return v * 1024 * 1024;
                case "G": return v * 1024 * 1024 * 1024;
                case "T": return v * 1024.0 * 1024 * 1024 * 1024;
                default: return v;
            }
        }

        /// <summary>Parses Crafty's human readable sizes like "12.3GB" back to bytes.</summary>
        public static double ParseSize(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            var m = Regex.Match(s.Trim(), @"^([\d.]+)\s*([KMGTP]?)i?B?$", RegexOptions.IgnoreCase);
            if (!m.Success) return 0;
            double v;
            double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
            int p = " KMGTP".IndexOf(m.Groups[2].Value.ToUpperInvariant() == "" ? " " : m.Groups[2].Value.ToUpperInvariant());
            return v * Math.Pow(1024, Math.Max(0, p));
        }

        public override string ToString() { return Name; }

        public bool Running { get { return Stats != null && J.B(Stats, "running"); } }

        public string State
        {
            get
            {
                if (Stats == null) return "unknown";
                if (J.B(Stats, "crashed")) return "crashed";
                if (J.B(Stats, "running")) return "online";
                if (J.B(Stats, "waiting_start")) return "starting";
                if (J.B(Stats, "updating")) return "updating";
                return "offline";
            }
        }

        public string StateText
        {
            get
            {
                switch (State)
                {
                    case "online": return "Online";
                    case "crashed": return "Abgestürzt";
                    case "starting": return "Startet…";
                    case "updating": return "Update läuft";
                    case "offline": return "Offline";
                    default: return "Lädt…";
                }
            }
        }

        public System.Drawing.Color StateColor
        {
            get
            {
                switch (State)
                {
                    case "online": return Theme.Green;
                    case "crashed": return Theme.Red;
                    case "starting":
                    case "updating": return Theme.Yellow;
                    default: return Theme.Muted;
                }
            }
        }

        public List<string> Players { get { return Util.ParsePlayers(J.Get(Stats, "players")); } }
    }

    static class Util
    {
        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;?]*[ -/]*[@-~]|\x1B[@-Z\\-_]|§.", RegexOptions.Compiled);
        static readonly Regex Quoted = new Regex(@"'([^']*)'|""([^""]*)""", RegexOptions.Compiled);

        public static string StripAnsi(string s) { return Ansi.Replace(s ?? "", ""); }

        public static List<string> ParsePlayers(object v)
        {
            var res = new List<string>();
            if (v == null || v is bool) return res;
            var s = v as string;
            if (s != null)
            {
                s = s.Trim();
                if (s.StartsWith("[") || s.StartsWith("{"))
                {
                    try
                    {
                        var parsed = J.Parse(s.Replace('\'', '"'));
                        return ParsePlayers(parsed is string ? null : parsed);
                    }
                    catch { }
                    foreach (Match m in Quoted.Matches(s))
                        res.Add(m.Groups[1].Success && m.Groups[1].Length > 0 ? m.Groups[1].Value : m.Groups[2].Value);
                    return res.Where(x => x.Length > 0).ToList();
                }
                return res;
            }
            foreach (var o in J.L(v))
            {
                if (o is string) res.Add((string)o);
                else
                {
                    var n = J.S(o, "name");
                    if (!string.IsNullOrEmpty(n)) res.Add(n);
                }
            }
            return res;
        }

        public static string Uptime(string started)
        {
            DateTime t;
            if (string.IsNullOrEmpty(started) || started == "False") return "–";
            if (!DateTime.TryParse(started, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out t)) return started;
            var span = DateTime.Now - t;
            if (span.TotalSeconds < 0) return "gerade eben";
            if (span.TotalDays >= 1) return string.Format("{0} T {1} Std", (int)span.TotalDays, span.Hours);
            if (span.TotalHours >= 1) return string.Format("{0} Std {1} Min", (int)span.TotalHours, span.Minutes);
            return string.Format("{0} Min", Math.Max(1, (int)span.TotalMinutes));
        }

        public static string Thumb(string t)
        {
            if (string.IsNullOrEmpty(t)) return "?";
            var sb = new StringBuilder();
            for (int i = 0; i < t.Length; i += 2)
            {
                if (sb.Length > 0) sb.Append(':');
                sb.Append(t.Substring(i, Math.Min(2, t.Length - i)));
            }
            return sb.ToString();
        }

        /// <summary>Returns lines in "now" that come after the tail of "prev"; null if no overlap (full reload needed).</summary>
        public static List<string> NewLines(List<string> prev, List<string> now)
        {
            if (prev.Count == 0) return now;
            int tail = Math.Min(3, prev.Count);
            for (int i = now.Count - 1; i >= tail - 1; i--)
            {
                bool match = true;
                for (int k = 0; k < tail; k++)
                {
                    if (now[i - k] != prev[prev.Count - 1 - k]) { match = false; break; }
                }
                if (match) return now.Skip(i + 1).ToList();
            }
            return null;
        }
    }
}
