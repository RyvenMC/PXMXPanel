using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; // TLS 1.2
            ServicePointManager.DefaultConnectionLimit = 16;
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.ServerCertificateValidationCallback = Api.ValidateCert;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += (s, e) =>
                MessageBox.Show(e.Exception.Message, "PXMX Server Suite – Fehler", MessageBoxButtons.OK, MessageBoxIcon.Error);

            var settings = Settings.Load();
            Api.PinnedThumb = settings.Thumb;
            Api.GatewayThumb = settings.GatewayThumb;

            if (!settings.SetupComplete)
            {
                using (var setup = new ServerSetupForm(settings))
                {
                    if (setup.ShowDialog() != DialogResult.OK) return;
                }
            }

            while (true)
            {
                Api api;
                string user;
                TeamSession team;
                using (var login = new LoginForm(settings))
                {
                    if (login.ShowDialog() != DialogResult.OK) return;
                    api = login.Result;
                    user = login.UserName;
                    team = login.Team;
                }
                using (var main = new MainForm(api, settings, user, team))
                {
                    if (main.ShowDialog() != DialogResult.Retry) return;
                }
            }
        }
    }

    /// <summary>Panel session of a team member (logged in through the gateway with username + password).</summary>
    class TeamSession
    {
        public string Name, Rank;
        public bool MustChange, TfaOn, TfaSetupRequired;
        public readonly HashSet<string> Perms = new HashSet<string>();
        public bool Has(string p) { return Perms.Contains(p); }

        public static TeamSession From(object data)
        {
            var t = new TeamSession { Name = J.S(data, "username"), Rank = J.S(data, "rank"), MustChange = J.B(data, "must_change"), TfaOn = J.B(data, "tfa_on"), TfaSetupRequired = J.B(data, "tfa_setup_required") };
            foreach (var p in J.L(J.Get(data, "perms"))) t.Perms.Add(Convert.ToString(p));
            return t;
        }

        /// <summary>Lets a team member set a new password. <paramref name="knownOld"/> skips asking for the current one (forced change right after login).</summary>
        public static async Task<bool> ChangePassword(IWin32Window owner, Api api, string knownOld, bool forced)
        {
            string error = null;
            while (true)
            {
                Field fOld = null, fNew, fRep;
                using (var d = new FormDialog(forced ? "Eigenes Passwort festlegen" : "Passwort ändern",
                    forced ? "Du meldest dich zum ersten Mal an (oder der Owner hat dein Passwort zurückgesetzt).\nLege jetzt ein eigenes Passwort fest – nur du kennst es."
                           : "Dein Passwort wird verschlüsselt (gehasht) auf dem RyvenMC-Server gespeichert."))
                {
                    if (knownOld == null) { fOld = d.AddText("Bisheriges Passwort", ""); fOld.Box.UseSystemPasswordChar = true; }
                    fNew = d.AddText("Neues Passwort", ""); fNew.Box.UseSystemPasswordChar = true;
                    fRep = d.AddText("Neues Passwort wiederholen", ""); fRep.Box.UseSystemPasswordChar = true;
                    d.AddNote("Mindestens 8 Zeichen mit Groß-/Kleinbuchstaben und Zahl – oder einfach 14+ Zeichen.", Theme.Muted);
                    if (error != null) d.AddNote(error, Theme.Red);
                    if (!d.Run(owner, "Passwort speichern")) return false;
                    var oldPw = knownOld ?? fOld.Box.Text;
                    if (fNew.Box.Text != fRep.Box.Text) { error = "Die beiden neuen Passwörter stimmen nicht überein."; continue; }
                    if (fNew.Box.Text.Length < 8) { error = "Das Passwort muss mindestens 8 Zeichen lang sein."; continue; }
                    var r = await api.PostJson("/panel/password", new Dictionary<string, object> { { "old", oldPw }, { "new", fNew.Box.Text } });
                    if (r.Ok) return true;
                    error = r.Error;
                }
            }
        }
    }

    class LoginForm : DarkForm
    {
        readonly Settings st;
        readonly Field fUrl, fUser, fPass, fTotp;
        readonly Label lUrl, lUser, lPass, lTotp;
        readonly CheckBox remember;
        readonly Label err, teamInfo;
        readonly FlatBtn btn;
        readonly TabBtn tTeam, tOwner;
        bool teamMode;

        public Api Result;
        public string UserName;
        public TeamSession Team;

        public LoginForm(Settings s)
        {
            st = s;
            Text = "PXMX – Anmelden";
            ClientSize = new Size(440, 690);
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;

            var logo = new GradientText("PXMX", Theme.F(28f, FontStyle.Bold)) { Location = new Point(36, 30) };
            var sub = Theme.Lbl("Server-Panel", Theme.F(10.5f), Theme.Muted, Theme.Bg);
            sub.Location = new Point(42, 86);
            Controls.Add(logo);
            Controls.Add(sub);

            tTeam = new TabBtn("Team-Login") { Location = new Point(34, 118) };
            tOwner = new TabBtn("Owner (Crafty)") { Location = new Point(34 + tTeam.Width + 4, 118) };
            tTeam.Click += (a, b) => SetMode(true);
            tOwner.Click += (a, b) => SetMode(false);
            Controls.Add(tTeam);
            Controls.Add(tOwner);

            fUrl = AddField("Panel-Adresse", false, out lUrl);
            fUser = AddField("Benutzername", false, out lUser);
            fPass = AddField("Passwort", true, out lPass);
            fTotp = AddField("2FA-Code (nur falls aktiviert)", false, out lTotp);
            fUrl.Box.Text = s.Url;

            remember = new CheckBox { Text = "Angemeldet bleiben", Checked = true, ForeColor = Theme.Text2, BackColor = Theme.Bg, AutoSize = true, Cursor = Cursors.Hand };
            btn = new FlatBtn("Anmelden", Theme.Accent) { Size = new Size(360, 44) };
            err = Theme.Lbl("", Theme.F(9f), Theme.Red, Theme.Bg);
            err.AutoSize = false;
            err.Size = new Size(360, 64);
            teamInfo = Theme.Lbl("", Theme.F(8.5f), Theme.Muted, Theme.Bg);
            teamInfo.Cursor = Cursors.Hand;
            teamInfo.Click += (a, b) => AskGateway();
            fUrl.Placeholder = "https://dein-server:8443";
            Controls.AddRange(new Control[] { remember, btn, err, teamInfo });

            AcceptButton = btn;
            btn.Click += async (a, b) => { if (teamMode) await TeamLogin(); else await DoLogin(); };
            SetMode(st.TeamMode);
            Shown += async (a, b) =>
            {
                if (teamMode && st.TeamToken != null) await TryTeamToken();
                else if (!teamMode && st.Token != null) await TryToken();
                else FocusFirst();
            };
        }

        Field AddField(string label, bool password, out Label l)
        {
            l = Theme.Lbl(label, Theme.F(9f, FontStyle.Bold), Theme.Muted, Theme.Bg);
            var f = new Field(password) { Size = new Size(360, 40) };
            Controls.Add(l);
            Controls.Add(f);
            return f;
        }

        void SetMode(bool team)
        {
            if (Visible && team == teamMode && tTeam.Active == team) return;
            // remember what was typed for the other mode
            if (tTeam.Active || tOwner.Active) { if (teamMode) st.TeamUser = fUser.Box.Text.Trim(); else st.Username = fUser.Box.Text.Trim(); }
            teamMode = team;
            tTeam.Active = team;
            tOwner.Active = !team;
            fUser.Box.Text = team ? (st.TeamUser ?? "") : (st.Username ?? "");
            fPass.Box.Text = "";
            fTotp.Box.Text = "";
            err.Text = "";

            // layout is written in 96-dpi pixels; after the form was scaled, positions must be scaled too
            Func<int, int, Point> at = (x, yy) => Scaled ? new Point(Theme.Px(x), Theme.Px(yy)) : new Point(x, yy);
            int y = 176;
            Action<Label, Field, bool> place = (l, f, show) =>
            {
                l.Visible = f.Visible = show;
                if (!show) return;
                l.Location = at(40, y);
                f.Location = at(40, y + 22);
                y += 76;
            };
            place(lUrl, fUrl, !team);
            place(lUser, fUser, true);
            place(lPass, fPass, true);
            place(lTotp, fTotp, !team);
            remember.Location = at(40, y); y += 38;
            btn.Location = at(40, y); y += 58;
            err.Location = at(40, y);
            teamInfo.Visible = team;
            teamInfo.Location = at(40, y + 70);
            UpdateTeamInfo();
            if (Visible) FocusFirst();
        }

        void FocusFirst()
        {
            if (!teamMode && fUrl.Box.Text.Trim().Length == 0) fUrl.Box.Focus();
            else if (fUser.Box.Text.Length > 0) fPass.Box.Focus();
            else fUser.Box.Focus();
        }

        string DefaultGateway()
        {
            Uri u;
            return Uri.TryCreate(st.Url, UriKind.Absolute, out u) ? "https://" + u.Host + ":8444" : "";
        }

        void UpdateTeamInfo()
        {
            var gw = st.GatewayUrl ?? DefaultGateway();
            teamInfo.Text = gw == "" ? "Team-Server: noch nicht eingestellt   (hier klicken)" : "Team-Server: " + gw.Replace("https://", "") + "   (ändern)";
        }

        /// <summary>Asks for the team server address (from the owner). "play.example.de" becomes https://play.example.de:8444.</summary>
        bool AskGateway()
        {
            var v = Dialogs.Ask(this, "Team-Server", "Adresse des Team-Zugangs (bekommst du vom Owner), z. B. play.deinserver.de:", st.GatewayUrl ?? DefaultGateway());
            if (string.IsNullOrWhiteSpace(v)) return false;
            v = v.Trim().TrimEnd('/');
            if (!v.StartsWith("http", StringComparison.OrdinalIgnoreCase)) v = "https://" + v;
            Uri u;
            if (!Uri.TryCreate(v, UriKind.Absolute, out u)) { err.Text = "Ungültige Adresse."; return false; }
            if (u.IsDefaultPort) v = "https://" + u.Host + ":8444";
            st.GatewayUrl = v; st.Save(); UpdateTeamInfo();
            return true;
        }

        /// <summary>Trust-on-first-use for self-signed certificates (Crafty and Team gateway).</summary>
        async Task<bool> PinCert(string url, bool gateway)
        {
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed) || parsed.Scheme != "https") return true;
            bool valid = false;
            string thumb = null;
            Exception fe = null;
            await Task.Run(() => { try { thumb = Api.FetchCert(url, out valid); } catch (Exception ex) { fe = ex; } });
            if (fe != null) { err.Text = (gateway ? "Team-Server" : "Panel") + " nicht erreichbar: " + fe.Message; return false; }
            var known = gateway ? st.GatewayThumb : st.Thumb;
            if (valid || string.Equals(thumb, known, StringComparison.OrdinalIgnoreCase)) return true;
            bool changed = !string.IsNullOrEmpty(known);
            string msg = (changed
                ? "ACHTUNG: Das Zertifikat hat sich geändert!\nWenn der Server nicht neu eingerichtet wurde, könnte jemand die Verbindung abfangen.\n\n"
                : (gateway ? "Der Team-Server nutzt ein eigenes Zertifikat.\nVergleiche den Fingerabdruck einmalig mit dem, den dir der Owner zeigt.\n\n"
                           : "Crafty nutzt ein selbst signiertes Zertifikat.\nPrüfe einmalig den Fingerabdruck, danach merkt sich die App das Zertifikat.\n\n"))
                + "Fingerabdruck (SHA-1):\n" + Util.Thumb(thumb) + "\n\nDiesem Zertifikat vertrauen?";
            if (MessageBox.Show(this, msg, "Zertifikat prüfen", MessageBoxButtons.YesNo, changed ? MessageBoxIcon.Warning : MessageBoxIcon.Question,
                    changed ? MessageBoxDefaultButton.Button2 : MessageBoxDefaultButton.Button1) != DialogResult.Yes)
            {
                err.Text = "Anmeldung abgebrochen.";
                return false;
            }
            if (gateway) { st.GatewayThumb = thumb; Api.GatewayThumb = thumb; }
            else { st.Thumb = thumb; Api.PinnedThumb = thumb; }
            st.Save();
            return true;
        }

        void SetBusy(bool busy)
        {
            btn.Enabled = !busy;
            tTeam.Enabled = tOwner.Enabled = !busy;
            btn.Text = busy ? "Verbinde…" : "Anmelden";
            UseWaitCursor = busy;
        }

        // ───────────────────────────── Team (gateway) ─────────────────────────────
        async Task TryTeamToken()
        {
            SetBusy(true);
            try
            {
                var api = new Api(st.GatewayUrl ?? DefaultGateway(), st.TeamToken);
                var r = await api.Get("/panel/me");
                if (r.Ok)
                {
                    var t = TeamSession.From(r.Data);
                    if (t.MustChange && !await TeamSession.ChangePassword(this, api, null, true))
                    {
                        await api.Send("POST", "/panel/logout", "", "application/json");
                        st.TeamToken = null; st.Save();
                        err.ForeColor = Theme.Muted; err.Text = "Ohne eigenes Passwort ist keine Anmeldung möglich.";
                        return;
                    }
                    if (!await EnsureTfa(api, t)) { st.TeamToken = null; st.Save(); return; }
                    Finish(api, t);
                    return;
                }
                if (r.Status != 0) { st.TeamToken = null; st.Save(); }
                err.Text = r.Status == 0 ? "Team-Server nicht erreichbar: " + r.Error : "Team-Sitzung abgelaufen – bitte neu anmelden.";
                FocusFirst();
            }
            finally { SetBusy(false); }
        }

        async Task TeamLogin()
        {
            err.Text = "";
            err.ForeColor = Theme.Red;
            var user = fUser.Box.Text.Trim().ToLowerInvariant();
            var pass = fPass.Box.Text;
            if (user == "" || pass == "") { err.Text = "Bitte Benutzername und Passwort eingeben."; return; }
            if ((st.GatewayUrl ?? DefaultGateway()) == "" && !AskGateway())
            {
                err.Text = "Bitte zuerst die Adresse des Team-Servers eintragen (bekommst du vom Owner).";
                return;
            }
            var gw = st.GatewayUrl ?? DefaultGateway();
            SetBusy(true);
            try
            {
                if (!await PinCert(gw, true)) return;
                var api = new Api(gw, null);
                var r = await api.PostJson("/panel/login", new Dictionary<string, object> { { "username", user }, { "password", pass } });
                if (r.Ok && J.B(r.Data, "need_2fa"))
                {
                    var ticket = J.S(r.Data, "ticket");
                    for (int tries = 0; tries < 5; tries++)
                    {
                        var code = Dialogs.Ask(this, "Zwei-Faktor-Code", "6-stelliger Code aus der Authenticator-App (oder ein Wiederherstellungscode):", "");
                        if (string.IsNullOrWhiteSpace(code)) { err.ForeColor = Theme.Muted; err.Text = "Anmeldung abgebrochen."; return; }
                        r = await api.PostJson("/panel/login/2fa", new Dictionary<string, object> { { "ticket", ticket }, { "code", code.Trim() } });
                        if (r.Ok || r.Status == 0) break;
                        if (!(r.Error ?? "").StartsWith("Code falsch")) break;
                        MessageBox.Show(this, r.Error, "Zwei-Faktor-Code", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }
                if (!r.Ok) { err.Text = r.Status == 0 ? "Team-Server nicht erreichbar: " + r.Error : r.Error; fPass.Box.SelectAll(); fPass.Box.Focus(); return; }
                api.Token = J.S(r.Data, "token");
                var t = TeamSession.From(r.Data);
                st.GatewayUrl = gw;
                st.TeamUser = user;
                st.TeamMode = true;
                if (t.MustChange && !await TeamSession.ChangePassword(this, api, pass, true))
                {
                    await api.Send("POST", "/panel/logout", "", "application/json");
                    st.Save();
                    err.ForeColor = Theme.Muted; err.Text = "Ohne eigenes Passwort ist keine Anmeldung möglich.";
                    return;
                }
                if (!await EnsureTfa(api, t)) { st.TeamToken = null; st.Save(); return; }
                fPass.Box.Text = "";
                st.TeamToken = remember.Checked ? api.Token : null;
                st.Save();
                Finish(api, t);
            }
            finally { SetBusy(false); }
        }

        /// <summary>Forces 2FA enrolment when the account's rank requires it (gateway answers TFA_SETUP on everything else).</summary>
        async Task<bool> EnsureTfa(Api api, TeamSession t)
        {
            if (!t.TfaSetupRequired) return true;
            MessageBox.Show(this, "Für deinen Rang ist die Zwei-Faktor-Anmeldung (2FA) Pflicht.\nBitte richte sie jetzt ein – du brauchst dafür eine Authenticator-App auf deinem Handy.",
                "2FA erforderlich", MessageBoxButtons.OK, MessageBoxIcon.Information);
            if (await ZweiFaktorDialog.Open(this, api, true)) { t.TfaSetupRequired = false; t.TfaOn = true; return true; }
            await api.Send("POST", "/panel/logout", "", "application/json");
            err.ForeColor = Theme.Muted; err.Text = "Ohne 2FA ist für deinen Rang keine Anmeldung möglich.";
            return false;
        }

        void Finish(Api api, TeamSession t)
        {
            t.MustChange = false;
            Team = t;
            UserName = t.Name;
            Result = api;
            DialogResult = DialogResult.OK;
        }

        // ───────────────────────────── Owner (Crafty) ─────────────────────────────
        async Task TryToken()
        {
            SetBusy(true);
            try
            {
                string eff;
                try { eff = await Task.Run(() => Ssh.EffectiveUrl(st, st.Url)); }
                catch (Exception ex) { err.Text = ex.Message; FocusFirst(); return; }
                var api = new Api(eff, st.Token) { DisplayUrl = st.Url };
                var r = await api.Get("/api/v2/users/@me");
                if (r.Ok)
                {
                    UserName = J.S(r.Data, "username") ?? st.Username;
                    Result = api;
                    DialogResult = DialogResult.OK;
                    return;
                }
                if (r.Status != 0) { st.Token = null; st.Save(); }
                err.Text = r.Status == 0 ? "Verbindung fehlgeschlagen: " + r.Error : "Sitzung abgelaufen – bitte neu anmelden.";
                FocusFirst();
            }
            finally { SetBusy(false); }
        }

        async Task DoLogin()
        {
            err.Text = "";
            err.ForeColor = Theme.Red;
            string url = fUrl.Box.Text.Trim().TrimEnd('/');
            if (url.Length == 0 && !string.IsNullOrEmpty(st.SshHost)) url = "https://" + st.SshHost + ":8443";
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            Uri parsed;
            if (!Uri.TryCreate(url, UriKind.Absolute, out parsed)) { err.Text = "Ungültige Panel-Adresse."; return; }
            if (fUser.Box.Text.Trim() == "" || fPass.Box.Text == "") { err.Text = "Bitte Benutzername und Passwort eingeben."; return; }

            SetBusy(true);
            try
            {
                string eff;
                try { eff = await Task.Run(() => Ssh.EffectiveUrl(st, url)); }
                catch (Exception ex) { err.Text = ex.Message; return; }
                if (!await PinCert(eff, false)) return;
                var api = new Api(eff, null) { DisplayUrl = url };
                var body = new Dictionary<string, object> { { "username", fUser.Box.Text.Trim() }, { "password", fPass.Box.Text } };
                if (fTotp.Box.Text.Trim() != "") body["totp"] = fTotp.Box.Text.Trim();
                var r = await api.PostJson("/api/v2/auth/login", body);
                if (!r.Ok)
                {
                    err.Text = r.Status == 0 ? "Verbindung fehlgeschlagen: " + r.Error : "Anmeldung fehlgeschlagen: " + r.Error;
                    return;
                }
                var token = J.S(r.Data, "token");
                if (string.IsNullOrEmpty(token))
                {
                    err.Text = "Anmeldung fehlgeschlagen: " + (J.S(r.Json, "error_data") ?? "Kein Token erhalten (2FA-Code nötig?)");
                    return;
                }
                api.Token = token;
                st.Url = url;
                st.Username = fUser.Box.Text.Trim();
                st.Token = remember.Checked ? token : null;
                st.TeamMode = false;
                st.Save();
                fPass.Box.Text = "";
                UserName = st.Username;
                Result = api;
                DialogResult = DialogResult.OK;
            }
            finally { SetBusy(false); }
        }
    }
}
