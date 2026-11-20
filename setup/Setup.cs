using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("PXMX Server Suite Setup")]
[assembly: AssemblyDescription("Installiert PXMX Server Suite")]

namespace RyvenSetup
{
    /// <summary>
    /// Installer + uninstaller for PXMX Server Suite. Installs per user (no admin rights) into %LOCALAPPDATA%\Programs\PXMX Server Suite,
    /// creates Start menu / desktop shortcuts and an entry under "Apps" (Uninstall.exe = this program with --uninstall).
    /// Per-user install keeps the folder writable, so the panel can update itself.
    ///   PXMX-ServerSuite-Setup.exe      install (window)
    ///   PXMX-ServerSuite-Setup.exe /S   install silently
    ///   Uninstall.exe --uninstall [/S]  remove
    /// </summary>
    static class Program
    {
        public const string AppName = "PXMX Server Suite";
        public const string ExeName = "PXMX";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PXMX-ServerSuite";

        public static string InstallDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName); } }
        public static string ExePath { get { return Path.Combine(InstallDir, ExeName + ".exe"); } }
        public static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        public static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }
        public static string SettingsDir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PXMX-ServerSuite"); } }

        [STAThread]
        static int Main(string[] args)
        {
            bool silent = args.Any(a => a.Equals("/S", StringComparison.OrdinalIgnoreCase) || a == "--silent");
            bool uninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase) || a.Equals("/U", StringComparison.OrdinalIgnoreCase));
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try
            {
                if (uninstall)
                {
                    if (!silent && MessageBox.Show("PXMX Server Suite wirklich deinstallieren?", "PXMX Server Suite deinstallieren", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return 1;
                    if (!CloseRunning(silent)) return 1;
                    bool wipe = !silent && MessageBox.Show("Auch deine Einstellungen und gespeicherten Anmeldungen löschen?\n(" + SettingsDir + ")\n\nDein SSH-Schlüssel bleibt erhalten.",
                        "PXMX Server Suite deinstallieren", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
                    Uninstall(wipe);
                    if (!silent) MessageBox.Show("PXMX Server Suite wurde entfernt.", "PXMX Server Suite", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }
                if (silent)
                {
                    if (!CloseRunning(true)) return 1;
                    Install(true);
                    return 0;
                }
                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show(ex.Message, "PXMX Server Suite Setup – Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }

        public static string InstalledVersion()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                    if (k != null) return k.GetValue("DisplayVersion") as string;
            }
            catch { }
            return null;
        }

        /// <summary>Ends running panels from the install folder (asks first unless silent).</summary>
        public static bool CloseRunning(bool silent)
        {
            var exePath = Path.GetFullPath(ExePath);
            var legacyExePath = Path.GetFullPath(Path.Combine(InstallDir, AppName + ".exe"));
            var running = Process.GetProcessesByName(ExeName).Concat(Process.GetProcessesByName(AppName)).Where(p =>
            {
                try
                {
                    var path = Path.GetFullPath(p.MainModule.FileName);
                    return string.Equals(path, exePath, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(path, legacyExePath, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            }).ToList();
            if (running.Count == 0) return true;
            if (!silent && MessageBox.Show("PXMX Server Suite läuft noch. Jetzt beenden und fortfahren?\n(Nicht gespeicherte Änderungen gehen verloren.)", "PXMX Server Suite Setup",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return false;
            foreach (var p in running)
            {
                try { p.CloseMainWindow(); if (!p.WaitForExit(4000)) { p.Kill(); p.WaitForExit(4000); } } catch { }
            }
            Thread.Sleep(300);
            return true;
        }

        public static void Install(bool desktop)
        {
            Directory.CreateDirectory(InstallDir);
            byte[] app;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("PXMXServerSuite.exe"))
            using (var ms = new MemoryStream()) { s.CopyTo(ms); app = ms.ToArray(); }

            // A running/locked exe can still be renamed – the panel deletes leftovers on its next start.
            foreach (var oldPath in new[] { ExePath, Path.Combine(InstallDir, AppName + ".exe") })
            {
                if (!File.Exists(oldPath)) continue;
                try { File.Delete(oldPath); }
                catch { File.Move(oldPath, Path.Combine(InstallDir, AppName + "." + DateTime.Now.Ticks + ".old")); }
            }
            File.WriteAllBytes(ExePath, app);

            var self = Assembly.GetExecutingAssembly().Location;
            var uninst = Path.Combine(InstallDir, "Uninstall.exe");
            if (!string.Equals(Path.GetFullPath(self), Path.GetFullPath(uninst), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(self, uninst, true); } catch { }
            }

            Shortcut(StartMenuLink, ExePath, "PXMX Server Suite");
            if (desktop) Shortcut(DesktopLink, ExePath, "PXMX Server Suite");
            else if (File.Exists(DesktopLink)) try { File.Delete(DesktopLink); } catch { }

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", RyvenPanel.AppInfo.Version);
                k.SetValue("Publisher", "PXMX");
                k.SetValue("DisplayIcon", ExePath + ",0");
                k.SetValue("InstallLocation", InstallDir);
                k.SetValue("UninstallString", "\"" + uninst + "\" --uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninst + "\" --uninstall /S");
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)Math.Max(1, (app.Length + new FileInfo(self).Length) / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            }
        }

        static void Shortcut(string link, string target, string desc)
        {
            object shell = null, lnk = null;
            try
            {
                var t = Type.GetTypeFromProgID("WScript.Shell");
                shell = Activator.CreateInstance(t);
                lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                var lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(target) });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { desc });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
            }
            finally
            {
                if (lnk != null) Marshal.ReleaseComObject(lnk);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        static void Uninstall(bool wipeSettings)
        {
            foreach (var l in new[] { StartMenuLink, DesktopLink }) if (File.Exists(l)) try { File.Delete(l); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            if (wipeSettings && Directory.Exists(SettingsDir)) try { Directory.Delete(SettingsDir, true); } catch { }
            // Uninstall.exe lives inside the folder – remove it after this process has ended
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + InstallDir + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
    }

    /// <summary>Small dark setup window in the panel's look.</summary>
    class SetupForm : Form
    {
        static readonly Color Bg = Color.FromArgb(15, 17, 22), Card = Color.FromArgb(27, 31, 40), Text1 = Color.FromArgb(236, 238, 244),
                              Muted = Color.FromArgb(135, 141, 160), Accent = Color.FromArgb(139, 108, 255), Cyan = Color.FromArgb(85, 224, 255),
                              Pink = Color.FromArgb(217, 102, 255), Green = Color.FromArgb(61, 220, 132), Red = Color.FromArgb(255, 92, 108);
        readonly CheckBox cDesk, cStart;
        readonly Button bInstall, bCancel;
        readonly Label status;
        bool done;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public SetupForm()
        {
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96f, 96f);
            Text = "PXMX Server Suite " + RyvenPanel.AppInfo.Version + " – Setup";
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Text1;
            Font = new Font("Segoe UI", 9.5f);
            ClientSize = new Size(520, 330);

            var head = new Panel { Dock = DockStyle.Top, Height = 96, BackColor = Bg };
            head.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                using (var f = new Font("Segoe UI", 20f, FontStyle.Bold))
                using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, 0, 220, 40), Cyan, Pink, 0f))
                    g.DrawString("PXMX Server Suite", f, b, 26, 18);
                using (var f = new Font("Segoe UI", 8.5f, FontStyle.Bold))
                using (var b = new SolidBrush(Muted))
                    g.DrawString("CONTROL PANEL  ·  VERSION " + RyvenPanel.AppInfo.Version, f, b, 30, 62);
                using (var b = new System.Drawing.Drawing2D.LinearGradientBrush(new Rectangle(0, head.Height - 1, head.Width, 1), Color.FromArgb(120, Cyan), Color.FromArgb(10, Pink), 0f))
                    g.FillRectangle(b, 0, head.Height - 1, head.Width, 1);
            };
            Controls.Add(head);

            var have = Program.InstalledVersion();
            var info = new Label
            {
                AutoSize = false, Location = new Point(28, 112), Size = new Size(470, 62), ForeColor = Muted, BackColor = Bg,
                Text = (have != null ? "Installiert ist Version " + have + " – sie wird ersetzt. Einstellungen und Anmeldungen bleiben erhalten.\n"
                                     : "Wird nur für deinen Windows-Benutzer installiert (keine Adminrechte nötig).\n") +
                       "Ziel: " + Program.InstallDir
            };
            Controls.Add(info);

            cDesk = Check("Verknüpfung auf dem Desktop", 182, have == null || File.Exists(Program.DesktopLink));
            cStart = Check("PXMX Server Suite nach der Installation starten", 210, true);

            status = new Label { AutoSize = false, Location = new Point(28, 246), Size = new Size(470, 22), ForeColor = Muted, BackColor = Bg };
            Controls.Add(status);

            bInstall = Btn(have != null ? "Aktualisieren" : "Installieren", Accent, new Point(368, 278), 124);
            bCancel = Btn("Abbrechen", Card, new Point(254, 278), 106);
            bCancel.Click += (s, e) => Close();
            bInstall.Click += (s, e) => DoInstall();
            AcceptButton = bInstall;
            CancelButton = bCancel;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            try { int v = 1; if (DwmSetWindowAttribute(Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(Handle, 19, ref v, 4); } catch { }
        }

        CheckBox Check(string text, int y, bool on)
        {
            var c = new CheckBox { Text = text, Checked = on, AutoSize = true, Location = new Point(30, y), ForeColor = Text1, BackColor = Bg };
            Controls.Add(c);
            return c;
        }

        Button Btn(string text, Color bg, Point loc, int w)
        {
            var b = new Button { Text = text, Location = loc, Size = new Size(w, 36), FlatStyle = FlatStyle.Flat, BackColor = bg, ForeColor = Color.White,
                                 Font = new Font("Segoe UI", 9.5f, FontStyle.Bold), Cursor = Cursors.Hand, UseVisualStyleBackColor = false };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = ControlPaint.Light(bg, 0.2f);
            Controls.Add(b);
            return b;
        }

        void DoInstall()
        {
            if (done)
            {
                if (cStart.Checked) try { Process.Start(new ProcessStartInfo(Program.ExePath) { UseShellExecute = true, WorkingDirectory = Program.InstallDir }); } catch { }
                Close();
                return;
            }
            if (!Program.CloseRunning(false)) return;
            bInstall.Enabled = bCancel.Enabled = false;
            status.ForeColor = Muted;
            status.Text = "Wird installiert …";
            Refresh();
            try
            {
                Program.Install(cDesk.Checked);
                done = true;
                status.ForeColor = Green;
                status.Text = "✔ PXMX Server Suite " + RyvenPanel.AppInfo.Version + " ist installiert – zu finden im Startmenü.";
                bInstall.Text = "Fertig";
                bInstall.Enabled = true;
                bCancel.Visible = false;
                cDesk.Enabled = false;
            }
            catch (Exception ex)
            {
                status.ForeColor = Red;
                status.Text = "Fehler: " + ex.Message;
                bInstall.Enabled = bCancel.Enabled = true;
            }
        }
    }
}
