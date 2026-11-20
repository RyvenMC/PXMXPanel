using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;
using System.Threading.Tasks;

namespace RyvenPanel
{
    class SshResult
    {
        public int Exit;
        public string Out = "";
        public string Err = "";
        public bool Ok { get { return Exit == 0; } }

        public string Message
        {
            get
            {
                var e = (Err ?? "").Trim();
                if (e.Length == 0) e = (Out ?? "").Trim();
                return e.Length > 600 ? e.Substring(e.Length - 600) : e;
            }
        }
    }

    /// <summary>Runs shell scripts on the server through the Windows OpenSSH client (key auth only).</summary>
    class Ssh
    {
        public string Host, User, KeyPath;
        public int Port = 22;

        static readonly string SshExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh.exe");
        static readonly string KeygenExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "OpenSSH", "ssh-keygen.exe");

        public static string KnownHosts
        {
            get
            {
                var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PXMX-ServerSuite");
                Directory.CreateDirectory(dir);
                return Path.Combine(dir, "known_hosts");
            }
        }

        public static string DefaultKey
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "pxmx_serversuite_ed25519"); }
        }

        public bool ClientAvailable { get { return File.Exists(SshExe); } }
        public bool KeyExists { get { return File.Exists(KeyPath) && File.Exists(KeyPath + ".pub"); } }
        public string PublicKey { get { return KeyExists ? File.ReadAllText(KeyPath + ".pub").Trim() : null; } }

        static readonly Regex SafeName = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$");
        static readonly Regex SafeImage = new Regex(@"^[A-Za-z0-9][A-Za-z0-9._/:@-]{0,254}$");
        static readonly Regex SafePath = new Regex(@"^/[A-Za-z0-9._/-]{1,250}$");

        public static bool IsName(string s) { return s != null && SafeName.IsMatch(s); }
        public static bool IsImage(string s) { return s != null && SafeImage.IsMatch(s); }
        public static bool IsPath(string s) { return s != null && SafePath.IsMatch(s) && !s.Contains(".."); }

        /// <summary>Single-quotes a value for bash (callers validate names first; this is defense in depth).</summary>
        public static string Q(string s) { return "'" + (s ?? "").Replace("'", "'\\''") + "'"; }

        /// <summary>Embeds arbitrary text in a script without any quoting problems.</summary>
        /// <summary>Shell snippet: writes the embedded script to <paramref name="path"/> only if the server copy is missing or has an
        /// OLDER `VERSION = "x.y"` – a newer server copy (e.g. hotfixed there) is never downgraded by an older app.</summary>
        public static string InstallIfNewer(string path, string source)
        {
            var m = System.Text.RegularExpressions.Regex.Match(source ?? "", "^VERSION = \"([^\"]+)\"", System.Text.RegularExpressions.RegexOptions.Multiline);
            var want = m.Success ? m.Groups[1].Value : "0";
            return "mkdir -p /opt/pxmx; have=$(sed -n 's/^VERSION = \"\\(.*\\)\"/\\1/p' " + path + " 2>/dev/null | head -1); " +
                   "if [ -z \"$have\" ] || { [ \"$have\" != \"" + want + "\" ] && [ \"$(printf '%s\\n%s\\n' \"$have\" \"" + want + "\" | sort -V | tail -1)\" = \"" + want + "\" ]; }; then " +
                   "printf '%s\\n' " + B64(source) + " > " + path + ".tmp && mv " + path + ".tmp " + path + " || exit 9; fi\n";
        }

        public static string B64(string s) { return "\"$(echo " + Convert.ToBase64String(new UTF8Encoding(false).GetBytes(s ?? "")) + " | base64 -d)\""; }

        /// <param name="aliveSec">keepalive interval; ssh gives up after 3 unanswered probes (dead connection detected after ~3x this).</param>
        string BaseArgs(bool tunnel, int aliveSec = 15)
        {
            var a = "-i " + Arg(KeyPath) + " -p " + Port +
                   " -o BatchMode=yes -o IdentitiesOnly=yes -o StrictHostKeyChecking=accept-new" +
                   " -o UserKnownHostsFile=" + Arg(KnownHosts) +
                   " -o ConnectTimeout=" + (tunnel ? 25 : 10) + " -o ServerAliveInterval=" + aliveSec + " -o ServerAliveCountMax=3 ";
            if (tunnel)
                a += "-o HostKeyAlias=" + Host + " -o \"ProxyCommand=" + NotfallProxy() + " access ssh --hostname %h\" ";
            return a;
        }

        string Target(bool tunnel) { return (tunnel && string.IsNullOrEmpty(User) ? "root" : User) + "@" + (tunnel ? NotfallHost : Host); }

        // ───────────── Notfall-Tunnel (Cloudflare Access) ─────────────
        /// <summary>True once the direct connection failed and the tunnel works; reset by ProbeDirect once port 22 answers again.</summary>
        public static volatile bool ViaNotfall;
        /// <summary>Raised with a status text when the tunnel gets activated.</summary>
        public static event Action<string> NotfallActivated;
        public const string NotfallMessage = "Notfall-Tunnel aktiv – direkte SSH-Verbindung nicht erreichbar";
        static string notfallHost = "";
        static string NotfallHost { get { return notfallHost; } }

        static string NotfallFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PXMX-ServerSuite", "notfall.json"); } }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern int GetShortPathName(string longPath, StringBuilder shortPath, int size);

        static string FindCloudflared()
        {
            var list = new System.Collections.Generic.List<string>();
            var pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)");
            if (!string.IsNullOrEmpty(pf86)) list.Add(Path.Combine(pf86, "cloudflared", "cloudflared.exe"));
            list.Add(Path.Combine(Environment.GetEnvironmentVariable("ProgramFiles") ?? "", "cloudflared", "cloudflared.exe"));
            foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                try { if (d.Trim().Length > 0) list.Add(Path.Combine(d.Trim().Trim('"'), "cloudflared.exe")); } catch { }
            foreach (var c in list) { try { if (File.Exists(c)) return c; } catch { } }
            return null;
        }

        static string NotfallProxy()
        {
            var exe = FindCloudflared();
            if (exe == null) return null;
            var sb = new StringBuilder(520);
            if (GetShortPathName(exe, sb, sb.Capacity) > 0 && sb.Length > 0) exe = sb.ToString();
            return exe.Contains(" ") ? null : exe;
        }

        static string Unprotect(string hex)
        {
            if (string.IsNullOrEmpty(hex) || hex.Length % 2 != 0) return null;
            var b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return Encoding.Unicode.GetString(ProtectedData.Unprotect(b, null, DataProtectionScope.CurrentUser));
        }

        /// <summary>Reads and decrypts the service token; false if not set up or unreadable. Never logged.</summary>
        static bool LoadNotfall(out string id, out string secret, out string gueltigBis)
        {
            id = secret = gueltigBis = null;
            notfallHost = "";
            try
            {
                if (!File.Exists(NotfallFile)) return false;
                var d = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(File.ReadAllText(NotfallFile));
                object v;
                if (d.TryGetValue("gueltig_bis", out v) && v != null) gueltigBis = v.ToString();
                if (!d.TryGetValue("host", out v) || v == null || !Regex.IsMatch(v.ToString(), @"^[A-Za-z0-9.-]{3,100}$")) return false;
                notfallHost = v.ToString();
                object a, b;
                if (!d.TryGetValue("client_id", out a) || !d.TryGetValue("client_secret", out b)) return false;
                id = Unprotect(Convert.ToString(a)); secret = Unprotect(Convert.ToString(b));
                return !string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(secret);
            }
            catch { id = secret = null; return false; }
        }

        /// <summary>Token file and cloudflared are present.</summary>
        public static bool NotfallAvailable
        {
            get { string i, s, g; return File.Exists(NotfallFile) && NotfallProxy() != null && LoadNotfall(out i, out s, out g); }
        }

        /// <summary>Text for the settings page.</summary>
        public static string NotfallInfo()
        {
            string i, s, g;
            if (!File.Exists(NotfallFile)) return "Notfall-Tunnel: nicht eingerichtet";
            if (!LoadNotfall(out i, out s, out g)) return "Notfall-Tunnel: nicht eingerichtet (Token-Datei nicht lesbar)";
            if (NotfallProxy() == null) return "Notfall-Tunnel: Token vorhanden, aber cloudflared fehlt";
            return "Notfall-Tunnel: eingerichtet" + (string.IsNullOrEmpty(g) ? "" : " (gültig bis " + g + ")");
        }

        static void ApplyNotfallEnv(ProcessStartInfo psi)
        {
            string i, s, g;
            if (!LoadNotfall(out i, out s, out g)) throw new Exception("Notfall-Tunnel nicht eingerichtet.");
            psi.EnvironmentVariables["TUNNEL_SERVICE_TOKEN_ID"] = i;
            psi.EnvironmentVariables["TUNNEL_SERVICE_TOKEN_SECRET"] = s;
        }

        static bool IsConnectionError(SshResult r)
        {
            if (r.Exit != 255) return false;
            var e = r.Err ?? "";
            if (e.Contains("Permission denied") || e.Contains("REMOTE HOST IDENTIFICATION")) return false;
            foreach (var k in new[] { "Connection timed out", "Connection refused", "No route to host", "Operation timed out", "Could not resolve", "Network is unreachable" })
                if (e.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        static void Activate()
        {
            ViaNotfall = true;
            lastProbe = Environment.TickCount;
            var h = NotfallActivated;
            if (h != null) { try { h(NotfallMessage); } catch { } }
        }

        /// <summary>Quick TCP probe so interactive sessions can pick the tunnel before they start.</summary>
        bool DirectReachable()
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect(Host, Port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(4000)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Runs "echo ok" explicitly through the tunnel (settings test).</summary>
        public Task<SshResult> TestNotfall()
        {
            return Task.Run(() =>
            {
                if (!KeyExists) return new SshResult { Exit = -1, Err = "Kein SSH-Schlüssel eingerichtet." };
                if (!NotfallAvailable) return new SshResult { Exit = -1, Err = "Notfall-Tunnel nicht eingerichtet oder cloudflared fehlt." };
                return RunOnce("echo ok", 40000, true);
            });
        }

        static string Arg(string s) { return "\"" + s.Replace("\"", "") + "\""; }

        static int lastProbe;
        /// <summary>
        /// The tunnel was a one-way switch: one network blip sent every later call through cloudflared (about twice as slow per call)
        /// for the rest of the session. At most every 2 min, check (worker thread, max 4 s) whether the direct path is back.
        /// </summary>
        void ProbeDirect()
        {
            if (!ViaNotfall) return;
            int now = Environment.TickCount;
            if (unchecked(now - lastProbe) < 120000) return;
            lastProbe = now;
            if (DirectReachable()) ViaNotfall = false;
        }

        public Task<SshResult> Run(string script, int timeoutMs = 60000)
        {
            return Task.Run(() =>
            {
                ProbeDirect();
                var r = RunOnce(script, timeoutMs, ViaNotfall);
                if (!ViaNotfall && IsConnectionError(r) && NotfallAvailable)
                {
                    var t = RunOnce(script, timeoutMs, true);
                    if (t.Exit != 255 && t.Exit >= 0) { Activate(); return t; }
                }
                return r;
            });
        }

        SshResult RunOnce(string script, int timeoutMs, bool tunnel)
        {
            {
                var res = new SshResult();
                if (!ClientAvailable) { res.Exit = -1; res.Err = "OpenSSH-Client fehlt (Windows-Feature „OpenSSH-Client“)."; return res; }
                if (!KeyExists) { res.Exit = -1; res.Err = "Kein SSH-Schlüssel eingerichtet."; return res; }
                int bom = PreambleLength;
                var remote = bom > 0 ? " \"head -c " + bom + " >/dev/null; exec bash -s\"" : " \"bash -s\"";
                var psi = new ProcessStartInfo(SshExe, BaseArgs(tunnel) + "-T " + Target(tunnel) + remote)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                try
                {
                    if (tunnel) ApplyNotfallEnv(psi);
                    using (var p = Process.Start(psi))
                    {
                        NoPreamble(p);
                        var o = p.StandardOutput.ReadToEndAsync();
                        var e = p.StandardError.ReadToEndAsync();
                        var bytes = new UTF8Encoding(false).GetBytes("set -o pipefail\nexport LC_ALL=C.UTF-8\n" + script.Replace("\r\n", "\n") + "\n");
                        p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                        p.StandardInput.Close();
                        if (!p.WaitForExit(timeoutMs))
                        {
                            try { p.Kill(); } catch { }
                            res.Exit = -2;
                            res.Err = "Zeitüberschreitung nach " + (timeoutMs / 1000) + " s.";
                            return res;
                        }
                        p.WaitForExit();
                        res.Out = o.Result;
                        res.Err = e.Result;
                        res.Exit = p.ExitCode;
                        if (res.Exit == 255 && res.Err.Contains("Permission denied"))
                            res.Err = "SSH-Schlüssel wird vom Server abgelehnt. Ist er in /root/.ssh/authorized_keys eingetragen?\n\n" + res.Err;
                        if (res.Err.Contains("REMOTE HOST IDENTIFICATION HAS CHANGED"))
                            res.Err = "WARNUNG: Der Host-Schlüssel des Servers hat sich geändert! Verbindung abgebrochen.\n" +
                                      "Wenn du den Server neu installiert hast, entferne die Zeile in " + KnownHosts + ".";
                    }
                }
                catch (Exception ex)
                {
                    res.Exit = -1;
                    res.Err = ex.Message;
                }
                return res;
            }
        }

        /// <summary>
        /// Starts ssh with the script embedded in the command line (stdin/stdout stay free for raw data).
        /// tty=true allocates a remote pseudo terminal (interactive shell).
        /// </summary>
        public Process Start(string script, bool tty)
        {
            if (!ClientAvailable) throw new Exception("OpenSSH-Client fehlt (Windows-Feature „OpenSSH-Client“).");
            if (!KeyExists) throw new Exception("Kein SSH-Schlüssel eingerichtet (einmal unter „Docker“ einrichten).");
            // .NET writes the console encoding's preamble (BOM) to stdin at Process.Start – swallow exactly those bytes remotely.
            int bom = PreambleLength;
            if (bom > 0) script = (tty ? "RPS=$(stty -g); stty raw -echo; head -c " + bom + " >/dev/null; stty \"$RPS\"\n" : "head -c " + bom + " >/dev/null\n") + script;
            var b64 = Convert.ToBase64String(new UTF8Encoding(false).GetBytes(script.Replace("\r\n", "\n")));
            var remote = "\"bash -c \\\"$(echo " + b64 + " | base64 -d)\\\"\"";
            bool tunnel = ViaNotfall;
            if (!tunnel && NotfallAvailable && !DirectReachable()) { Activate(); tunnel = true; }
            var psi = new ProcessStartInfo(SshExe, BaseArgs(tunnel) + (tty ? "-tt " : "-T ") + Target(tunnel) + " " + remote)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.EnvironmentVariables["TERM"] = "xterm-256color";
            if (tunnel) ApplyNotfallEnv(psi);
            var p = Process.Start(psi);
            NoPreamble(p);
            return p;
        }

        public static int PreambleLength
        {
            get { try { return Console.InputEncoding.GetPreamble().Length; } catch { return 0; } }
        }

        /// <summary>Prevents .NET from appending a UTF-8 BOM to stdin when the writer is flushed/closed.</summary>
        public static void NoPreamble(Process p)
        {
            try
            {
                var f = typeof(StreamWriter).GetField("haveWrittenPreamble", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                if (f != null) f.SetValue(p.StandardInput, true);
            }
            catch { }
        }

        // ───────────── Lokaler Port-Forward (z. B. Crafty 8443 nur über SSH) ─────────────
        static readonly object fwdLock = new object();
        static Process fwdProc;
        static int fwdLocal, fwdRemote;
        static Ssh fwdSsh;
        static bool fwdTunnel, fwdStopping;
        static System.Threading.Thread fwdWatch;
        static IntPtr job = IntPtr.Zero;
        static bool exitHooked;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attr, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr h, int cls, IntPtr info, int len);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr proc);

        /// <summary>Job object with KILL_ON_JOB_CLOSE: ssh dies with the app, even after a crash.</summary>
        static void AddToJob(Process p)
        {
            try
            {
                if (job == IntPtr.Zero)
                {
                    job = CreateJobObject(IntPtr.Zero, null);
                    // JOBOBJECT_EXTENDED_LIMIT_INFORMATION: LimitFlags at offset 16; total size 144 (x64) / 112 (x86)
                    int size = IntPtr.Size == 8 ? 144 : 112;
                    var buf = Marshal.AllocHGlobal(size);
                    try
                    {
                        for (int i = 0; i < size; i++) Marshal.WriteByte(buf, i, 0);
                        Marshal.WriteInt32(buf, 16, 0x2000); // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
                        SetInformationJobObject(job, 9, buf, size);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                AssignProcessToJobObject(job, p.Handle);
            }
            catch { }
        }

        static int FreePort()
        {
            var l = new TcpListener(System.Net.IPAddress.Loopback, 0);
            l.Start();
            int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return port;
        }

        static bool PortOpen(int port)
        {
            try
            {
                using (var c = new TcpClient())
                {
                    var ar = c.BeginConnect("127.0.0.1", port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(500)) return false;
                    c.EndConnect(ar);
                    return true;
                }
            }
            catch { return false; }
        }

        /// <summary>Starts one ssh -N -L process and waits until the local port accepts connections.</summary>
        bool TryForward(int local, int remote, bool tunnel, int waitMs, out Process proc, out string err)
        {
            proc = null; err = "";
            // short keepalive: a stalled forward (network blip, standby) must die fast so the watchdog rebuilds it –
            // otherwise every Crafty request hangs until its 20 s timeout (+ retry) for up to 45 s.
            var psi = new ProcessStartInfo(SshExe, BaseArgs(tunnel, 5) + "-N -T -o ExitOnForwardFailure=yes -L 127.0.0.1:" + local + ":127.0.0.1:" + remote + " " + Target(tunnel))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            try { if (tunnel) ApplyNotfallEnv(psi); }
            catch (Exception ex) { err = ex.Message; return false; }
            var p = Process.Start(psi);
            AddToJob(p);
            var eb = new StringBuilder();
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (eb) eb.AppendLine(e.Data); };
            p.OutputDataReceived += (s, e) => { };
            p.BeginErrorReadLine();
            p.BeginOutputReadLine();
            try { p.StandardInput.Close(); } catch { }
            var end = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (DateTime.UtcNow < end)
            {
                if (p.HasExited) break;
                if (PortOpen(local)) { proc = p; return true; }
                System.Threading.Thread.Sleep(200);
            }
            try { if (!p.HasExited) p.Kill(); } catch { }
            lock (eb) err = eb.ToString().Trim();
            if (err.Length > 400) err = err.Substring(err.Length - 400);
            if (err.Length == 0) err = "Zeitüberschreitung beim Aufbau des SSH-Tunnels.";
            return false;
        }

        /// <summary>
        /// Forwards 127.0.0.1:(free port) to 127.0.0.1:remotePort on the server and returns the local port.
        /// Reuses a running forward; restarts it automatically if ssh dies. Throws on failure.
        /// </summary>
        public int StartForward(int remotePort)
        {
            lock (fwdLock)
            {
                if (fwdProc != null && !fwdProc.HasExited && fwdRemote == remotePort && fwdSsh != null && fwdSsh.Host == Host) return fwdLocal;
                StopForwardLocked();
                if (!ClientAvailable) throw new Exception("OpenSSH-Client fehlt (Windows-Feature „OpenSSH-Client“).");
                if (!KeyExists) throw new Exception("Kein SSH-Schlüssel eingerichtet.");
                int local = FreePort();
                Process p; string err;
                bool tunnel = ViaNotfall;
                bool ok = TryForward(local, remotePort, tunnel, tunnel ? 30000 : 15000, out p, out err);
                if (!ok && !tunnel && NotfallAvailable && !(err ?? "").Contains("Permission denied"))
                {
                    string err2;
                    ok = TryForward(local, remotePort, true, 30000, out p, out err2);
                    if (ok) { tunnel = true; Activate(); } else err = err2;
                }
                if (!ok) throw new Exception("SSH-Tunnel zu Port " + remotePort + " fehlgeschlagen: " + err);
                fwdProc = p; fwdLocal = local; fwdRemote = remotePort; fwdSsh = this; fwdTunnel = tunnel; fwdStopping = false;
                if (!exitHooked)
                {
                    exitHooked = true;
                    System.Windows.Forms.Application.ApplicationExit += (s, e) => StopForward();
                    AppDomain.CurrentDomain.ProcessExit += (s, e) => StopForward();
                }
                if (fwdWatch == null || !fwdWatch.IsAlive)
                {
                    fwdWatch = new System.Threading.Thread(WatchForward) { IsBackground = true, Name = "ssh-forward-watch" };
                    fwdWatch.Start();
                }
                return local;
            }
        }

        static void WatchForward()
        {
            while (true)
            {
                System.Threading.Thread.Sleep(2000);
                Ssh s; int local, remote; bool tunnel;
                lock (fwdLock)
                {
                    if (fwdSsh == null) { if (fwdStopping) return; continue; }
                    if (fwdProc != null && !fwdProc.HasExited) continue;
                    s = fwdSsh; local = fwdLocal; remote = fwdRemote; tunnel = fwdTunnel;
                }
                // reconnect outside the lock (up to 45 s) – StopForward on app exit / StartForward must not wait for it
                Process p; string err;
                bool viaTunnel = tunnel;
                bool ok = s.TryForward(local, remote, tunnel, tunnel ? 30000 : 15000, out p, out err);
                if (!ok && !tunnel && NotfallAvailable) { viaTunnel = true; ok = s.TryForward(local, remote, true, 30000, out p, out err); }
                if (!ok) continue;
                bool keep;
                lock (fwdLock)
                {
                    keep = fwdSsh == s && fwdLocal == local && fwdRemote == remote && (fwdProc == null || fwdProc.HasExited);
                    if (keep) { fwdProc = p; fwdTunnel = viaTunnel; }
                }
                if (!keep) { try { if (!p.HasExited) p.Kill(); } catch { } try { p.Dispose(); } catch { } continue; }
                if (viaTunnel && !tunnel) Activate();
            }
        }

        static void StopForwardLocked()
        {
            fwdStopping = true;
            var p = fwdProc;
            fwdProc = null; fwdSsh = null;
            if (p != null) { try { if (!p.HasExited) p.Kill(); } catch { } try { p.Dispose(); } catch { } }
        }

        public static void StopForward() { lock (fwdLock) StopForwardLocked(); }

        /// <summary>Effective Crafty address: through the SSH forward if the URL points at the SSH host, otherwise unchanged.</summary>
        public static string EffectiveUrl(Settings st, string url)
        {
            if (string.IsNullOrEmpty(st.SshHost)) return url;
            if (string.IsNullOrEmpty(url)) url = "https://" + st.SshHost + ":8443";
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || !string.Equals(u.Host, st.SshHost, StringComparison.OrdinalIgnoreCase)) return url;
            var ssh = new Ssh { Host = st.SshHost, User = st.SshUser, Port = st.SshPort, KeyPath = string.IsNullOrEmpty(st.SshKey) ? DefaultKey : st.SshKey };
            if (!ssh.ClientAvailable || !ssh.KeyExists) return url;
            return u.Scheme + "://127.0.0.1:" + ssh.StartForward(u.Port);
        }

        public Task<SshResult> CreateKey()
        {
            return Task.Run(() =>
            {
                var res = new SshResult();
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(KeyPath));
                    var psi = new ProcessStartInfo(KeygenExe, "-t ed25519 -N \"\" -C \"PXMX@" + Environment.MachineName + "\" -f " + Arg(KeyPath))
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        p.StandardInput.Close();
                        res.Out = p.StandardOutput.ReadToEnd();
                        res.Err = p.StandardError.ReadToEnd();
                        p.WaitForExit();
                        res.Exit = p.ExitCode;
                    }
                }
                catch (Exception ex) { res.Exit = -1; res.Err = ex.Message; }
                return res;
            });
        }

        /// <summary>Opens a visible console where the user types the root password once to install the public key.</summary>
        public void InstallKeyInteractive()
        {
            var pub = PublicKey;
            if (pub == null || pub.IndexOfAny(new[] { '\'', '"', '\r', '\n', '%', '^', '&' }) >= 0) return;
            var remote = "umask 077; mkdir -p ~/.ssh; grep -qxF '" + pub + "' ~/.ssh/authorized_keys 2>/dev/null || echo '" + pub +
                         "' >> ~/.ssh/authorized_keys; echo; echo FERTIG - Schluessel installiert. Fenster schliesst in 5 s.; sleep 5";
            var args = "/c \"\"" + SshExe + "\" -p " + Port + " -o StrictHostKeyChecking=accept-new -o UserKnownHostsFile=\"" + KnownHosts + "\" -t " +
                       User + "@" + Host + " \"" + remote + "\" || pause\"";
            Process.Start(new ProcessStartInfo("cmd.exe", args) { UseShellExecute = true });
        }
    }
}
