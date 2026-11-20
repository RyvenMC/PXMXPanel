using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PXMX")]
[assembly: AssemblyDescription("PXMX Server Suite")]

namespace RyvenPanel
{
    class UpdateInfo
    {
        public string Version, Sha256, Notes, Date, Signatur;
        public long Size;
    }

    /// <summary>
    /// Self-update. Releases live in /opt/pxmx/dist (PXMX.exe + version.json):
    /// the owner publishes them from his panel (SSH), team members download them through the team gateway
    /// (/panel/update – same pinned certificate as the login), the owner via SSH. The new exe is checked against the
    /// SHA-256 from version.json, swapped in next to the running one (a running exe can be renamed, not overwritten)
    /// and started; the old file is deleted on the next start.
    /// Every release must carry an ECDSA signature (version.json "signatur", see UpdateSignature) over version, SHA-256
    /// and size, made with the owner's local key – a compromised server alone cannot push a different exe.
    /// </summary>
    static class Updater
    {
        public const string RemoteDir = "/opt/pxmx/dist";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PXMX-ServerSuite";
        static readonly HashSet<string> declined = new HashSet<string>();
        static bool askedPublish, busy;

        public static Version Parse(string s)
        {
            Version v;
            return Version.TryParse((s ?? "").Trim(), out v) ? v : new Version(0, 0);
        }

        public static bool IsNewer(string v) { return Parse(v) > Parse(AppInfo.Version); }

        /// <summary>Removes the previous exe left behind by an update.</summary>
        public static void CleanupOld()
        {
            try
            {
                var dir = Path.GetDirectoryName(Application.ExecutablePath);
                foreach (var f in Directory.GetFiles(dir, "PXMX.*.old")) try { File.Delete(f); } catch { }
                var neu = Application.ExecutablePath + ".new";
                if (File.Exists(neu)) try { File.Delete(neu); } catch { }
            }
            catch { }
        }

        static UpdateInfo From(object d)
        {
            if (d == null) return null;
            var u = new UpdateInfo { Version = J.S(d, "version"), Sha256 = J.S(d, "sha256"), Notes = J.S(d, "notes"), Date = J.S(d, "date"), Size = (long)J.N(d, "size"), Signatur = J.S(d, "signatur") };
            return string.IsNullOrEmpty(u.Version) || string.IsNullOrEmpty(u.Sha256) ? null : u;
        }

        static async Task<T> WithTimeout<T>(Task<T> t, int ms) where T : class
        {
            return await Task.WhenAny(t, Task.Delay(ms)) == t ? t.Result : null;
        }

        // ───────────────────────────── sources ─────────────────────────────
        public static async Task<UpdateInfo> CheckGateway(string baseUrl)
        {
            try
            {
                var r = await WithTimeout(new Api(baseUrl, null).Get("/panel/update"), 8000);
                return r != null && r.Ok ? From(r.Data) : null;
            }
            catch { return null; }
        }

        public static async Task<UpdateInfo> CheckSsh(Ssh ssh)
        {
            if (ssh == null || !ssh.KeyExists || string.IsNullOrEmpty(ssh.Host)) return null;
            var r = await WithTimeout(ssh.Run("cat " + RemoteDir + "/version.json 2>/dev/null || true", 20000), 20000);
            if (r == null || !r.Ok) return null;
            var t = (r.Out ?? "").Trim();
            if (!t.StartsWith("{")) return null;
            try { return From(J.Parse(t)); } catch { return null; }
        }

        static Task<byte[]> DownloadGateway(string baseUrl)
        {
            return Task.Run(() =>
            {
                var req = (HttpWebRequest)WebRequest.Create(baseUrl.TrimEnd('/') + "/panel/update/PXMX.exe");
                req.Timeout = req.ReadWriteTimeout = 180000;
                req.UserAgent = "PXMX-ServerSuite/" + AppInfo.Version;
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var s = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
            });
        }

        static async Task<byte[]> DownloadSsh(Ssh ssh)
        {
            var r = await ssh.Run("base64 -w0 " + RemoteDir + "/PXMX.exe", 240000);
            if (!r.Ok) throw new Exception(r.Message);
            return Convert.FromBase64String(r.Out.Trim());
        }

        // ───────────────────────────── install ─────────────────────────────
        static string Sha(byte[] data)
        {
            using (var h = SHA256.Create()) return BitConverter.ToString(h.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Verifies, swaps the exe and restarts. Does not return on success.</summary>
        static void Install(byte[] data, UpdateInfo u)
        {
            if (!UpdateSignature.Verify(u.Version, u.Sha256, u.Size, u.Signatur))
                throw new Exception("Das Update ist nicht gültig signiert und wird aus Sicherheitsgründen nicht installiert.");
            if (data.LongLength != u.Size || !string.Equals(Sha(data), u.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Die heruntergeladene Datei passt nicht zur signierten Prüfsumme – sie ist unvollständig, beschädigt oder wurde verändert. Update abgebrochen.");
            var exe = Application.ExecutablePath;
            var dir = Path.GetDirectoryName(exe);
            var neu = exe + ".new";
            try { File.WriteAllBytes(neu, data); }
            catch (UnauthorizedAccessException)
            {
                throw new Exception("Keine Schreibrechte in " + dir + ".\nInstalliere das Panel mit PXMXPanel-Setup.exe, dann klappen Updates automatisch.");
            }
            try
            {
                var an = AssemblyName.GetAssemblyName(neu);
                if (an.Name != "PXMXServerSuite") throw new Exception("Die Datei ist keine PXMX Server Suite.");
            }
            catch (BadImageFormatException) { File.Delete(neu); throw new Exception("Die heruntergeladene Datei ist kein gültiges Programm."); }
            var old = Path.Combine(dir, "PXMX." + DateTime.Now.Ticks + ".old");
            File.Move(exe, old);
            try { File.Move(neu, exe); }
            catch { File.Move(old, exe); throw; }
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey, true))
                    if (k != null) k.SetValue("DisplayVersion", u.Version);
            }
            catch { }
            Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true, WorkingDirectory = dir });
            Environment.Exit(0);
        }

        static async Task<bool> Offer(IWin32Window owner, UpdateInfo u, Func<Task<byte[]>> download, Action<string, Color> status)
        {
            if (u == null || busy || !IsNewer(u.Version) || declined.Contains(u.Version)) return false;
            if (!UpdateSignature.Verify(u.Version, u.Sha256, u.Size, u.Signatur))
            {
                declined.Add(u.Version);
                if (status != null) status("Update " + u.Version + " abgelehnt: keine gültige Signatur.", Theme.Red);
                MessageBox.Show(owner, "Auf dem Server liegt Version " + u.Version + ", sie ist aber " +
                    (string.IsNullOrWhiteSpace(u.Signatur) ? "nicht signiert" : "nicht gültig signiert") + ".\n\n" +
                    "Das Update wird aus Sicherheitsgründen NICHT installiert. Bitte informiere den Inhaber – möglicherweise wurde der Server manipuliert.",
                    "PXMX – Update abgelehnt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            var text = "Version " + u.Version + " ist verfügbar (installiert: " + AppInfo.Version + ").\n\n" +
                       (string.IsNullOrWhiteSpace(u.Notes) ? "" : "Neu:\n" + u.Notes.Trim() + "\n\n") +
                       "Jetzt aktualisieren? Das Panel startet danach neu – Einstellungen und Anmeldung bleiben erhalten.";
            if (MessageBox.Show(owner, text, "Update verfügbar", MessageBoxButtons.YesNo, MessageBoxIcon.Information) != DialogResult.Yes)
            {
                declined.Add(u.Version);
                if (status != null) status("Update " + u.Version + " übersprungen – wird beim nächsten Start wieder angeboten.", Theme.Muted);
                return false;
            }
            busy = true;
            try
            {
                if (status != null) status("Update " + u.Version + " wird geladen …", Theme.SoftGreen);
                var data = await download();
                Install(data, u);
                return true;
            }
            catch (Exception ex)
            {
                if (status != null) status("Update fehlgeschlagen: " + ex.Message, Theme.Red);
                MessageBox.Show(owner, "Das Update konnte nicht installiert werden:\n\n" + ex.Message, "PXMX Server Suite – Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            finally { busy = false; }
        }

        // ───────────────────────────── entry points ─────────────────────────────
        /// <summary>Login window: team members ask the gateway (once its certificate is known), the owner the server via SSH.</summary>
        public static async Task StartupCheck(IWin32Window owner, Settings st, string gatewayUrl)
        {
            try
            {
                if (st.TeamMode)
                {
                    if (string.IsNullOrEmpty(st.GatewayThumb) || string.IsNullOrEmpty(gatewayUrl)) return;
                    var url = gatewayUrl;
                    await Offer(owner, await CheckGateway(url), () => DownloadGateway(url), null);
                }
                else
                {
                    if (string.IsNullOrEmpty(st.SshHost)) return;
                    var ssh = new Ssh { Host = st.SshHost, User = st.SshUser, Port = st.SshPort, KeyPath = string.IsNullOrEmpty(st.SshKey) ? Ssh.DefaultKey : st.SshKey };
                    await Offer(owner, await CheckSsh(ssh), () => DownloadSsh(ssh), null);
                }
            }
            catch { }
        }

        /// <summary>Team panel: periodic check through the gateway.</summary>
        public static async Task CheckTeam(IWin32Window owner, string gatewayUrl, Action<string, Color> status)
        {
            await Offer(owner, await CheckGateway(gatewayUrl), () => DownloadGateway(gatewayUrl), status);
        }

        /// <summary>
        /// Owner panel: installs a newer release from the server, or – if this exe is newer than the published one – offers to
        /// publish it for the whole team.
        /// </summary>
        public static async Task OwnerSync(IWin32Window owner, Ssh ssh, Action<string, Color> status)
        {
            if (ssh == null || !ssh.KeyExists) return;
            var remote = await CheckSsh(ssh);
            if (remote != null && IsNewer(remote.Version)) { await Offer(owner, remote, () => DownloadSsh(ssh), status); return; }
            if (remote != null && Parse(remote.Version) >= Parse(AppInfo.Version)) return;
            if (askedPublish) return;
            askedPublish = true;
            if (MessageBox.Show(owner, "Du nutzt PXMX Server Suite " + AppInfo.Version + ", auf dem Server ist " + (remote == null ? "noch keine Version" : "Version " + remote.Version) +
                    " veröffentlicht.\n\nDiese Version jetzt für alle verteilen? Teammitglieder bekommen sie beim nächsten Start (oder spätestens nach ein paar Stunden) als Update angeboten.",
                    "Update verteilen", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var notes = Dialogs.Ask(owner, "Update verteilen", "Was ist neu in Version " + AppInfo.Version + "? (optional – wird beim Update angezeigt)", "") ?? "";
            await Publish(ssh, notes, status);
        }

        public static async Task<bool> Publish(Ssh ssh, string notes, Action<string, Color> status)
        {
            byte[] data;
            try { data = File.ReadAllBytes(Application.ExecutablePath); }
            catch (Exception ex) { status("Update nicht verteilt: " + ex.Message, Theme.Red); return false; }
            var sha = Sha(data);
            string sig;   // signed locally (key never leaves this PC) before anything is uploaded
            try { sig = UpdateSignature.Sign(AppInfo.Version, sha, data.LongLength); }
            catch (Exception ex) { status("Update nicht verteilt: " + ex.Message, Theme.Red); return false; }
            var info = new Dictionary<string, object>
            {
                { "version", AppInfo.Version }, { "sha256", sha }, { "size", data.LongLength },
                { "notes", notes ?? "" }, { "date", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ") }, { "signatur", sig }
            };
            status("Version " + AppInfo.Version + " wird auf den Server geladen …", Theme.SoftGreen);
            var tmp = RemoteDir + "/PXMX.exe.tmp";
            var script =
                "set -e\nmkdir -p " + RemoteDir + "\n" +
                "base64 -d > " + tmp + " <<'RP_EOF'\n" + Convert.ToBase64String(data, Base64FormattingOptions.InsertLineBreaks) + "\nRP_EOF\n" +
                "echo '" + sha + "  " + tmp + "' | sha256sum -c --quiet\n" +
                "[ -f " + RemoteDir + "/PXMX.exe ] && cp -p " + RemoteDir + "/PXMX.exe " + RemoteDir + "/PXMX.prev.exe || true\n" +
                "mv " + tmp + " " + RemoteDir + "/PXMX.exe\n" +
                "printf '%s\\n' " + Ssh.B64(J.ToJson(info)) + " > " + RemoteDir + "/version.json\n" +
                "chmod 644 " + RemoteDir + "/PXMX.exe " + RemoteDir + "/version.json\necho PXMX_PUBLISHED\n";
            var r = await ssh.Run(script, 240000);
            if (!r.Ok || !r.Out.Contains("PXMX_PUBLISHED")) { status("Update nicht verteilt: " + r.Message, Theme.Red); return false; }
            status("Version " + AppInfo.Version + " ist veröffentlicht – das Team bekommt sie als Update angeboten.", Theme.Green);
            return true;
        }
    }
}
