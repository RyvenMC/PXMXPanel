using System;
using System.Collections;
using System.Collections.Generic;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RyvenPanel
{
    /// <summary>Hardware fingerprint of this PC (Owner-Gerätebindung).</summary>
    static class HardwareId
    {
        static string cached;

        static string Norm(string s) { return (s ?? "").Trim().ToUpperInvariant(); }

        static string MachineGuid()
        {
            try
            {
                using (var b = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var k = b.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography"))
                    return k == null ? "" : Convert.ToString(k.GetValue("MachineGuid"));
            }
            catch { return ""; }
        }

        static string Wmi(string cls, string prop)
        {
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT " + prop + " FROM " + cls))
                    foreach (ManagementObject o in s.Get())
                    {
                        var v = o[prop];
                        if (v != null) return Convert.ToString(v);
                    }
            }
            catch { }
            return "";
        }

        public static string Compute()
        {
            if (cached != null) return cached;
            var raw = Norm(MachineGuid()) + "|" + Norm(Wmi("Win32_ComputerSystemProduct", "UUID")) + "|" + Norm(Wmi("Win32_BaseBoard", "SerialNumber"));
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(raw));
                var sb = new StringBuilder();
                foreach (var x in h) sb.Append(x.ToString("x2"));
                cached = sb.ToString();
            }
            return cached;
        }

        public static string Short { get { return Compute().Substring(0, 16); } }

        /// <summary>null = Datei fehlt (Bindung nicht aktiv), true = erlaubt, false = Gerät nicht gelistet. Bei SSH-Fehler: null.</summary>
        public static async Task<bool?> CheckOwner(Ssh ssh)
        {
            var r = await ssh.Run("if [ -f /etc/pxmx/owner-devices.json ]; then cat /etc/pxmx/owner-devices.json; else echo __NOFILE__; fi");
            if (!r.Ok) return null;
            var txt = (r.Out ?? "").Trim();
            if (txt == "__NOFILE__") return null;
            var me = Compute();
            try
            {
                var root = new JavaScriptSerializer().DeserializeObject(txt) as IDictionary<string, object>;
                var list = root != null && root.ContainsKey("geraete") ? root["geraete"] as IEnumerable : null;
                if (list == null) return false;
                foreach (var it in list)
                {
                    var d = it as IDictionary<string, object>;
                    if (d != null && d.ContainsKey("hwid") && string.Equals(Convert.ToString(d["hwid"]).Trim(), me, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch { }
            return false;
        }

        public static void ShowBlocked(IWin32Window owner)
        {
            MessageBox.Show(owner,
                "Dieses Gerät ist nicht als Owner-Gerät freigegeben.\n\nGeräte-ID: " + Short +
                "\n\nOwner-Funktionen sind auf diesem PC gesperrt. Lass die Geräte-ID in der Owner-Geräteliste freigeben oder melde dich als Team-Mitglied an.\n\nDie App wird jetzt beendet.",
                "RyvenPanel – Gerät nicht freigegeben", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }
}
