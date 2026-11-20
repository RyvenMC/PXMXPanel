using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    /// <summary>Minimal QR encoder: byte mode, error correction M, versions 1-10 (up to 213 bytes). No external service.</summary>
    static class QrCode
    {
        static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16, 18, 22, 22, 26 };
        static readonly int[] NumBlocks = { 0, 1, 1, 1, 2, 2, 4, 4, 4, 5, 5 };
        static readonly int[][] Align =
        {
            null, new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
            new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 }
        };

        public const int MaxBytes = 213;

        static int RawCodewords(int ver)
        {
            int r = (16 * ver + 128) * ver + 64;
            if (ver >= 2) { int n = ver / 7 + 2; r -= (25 * n - 10) * n - 55; if (ver >= 7) r -= 36; }
            return r / 8;
        }

        static int Mul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--) { z = (z << 1) ^ ((z >> 7) * 0x11D); z ^= ((y >> i) & 1) * x; }
            return z;
        }

        static byte[] RsDivisor(int degree)
        {
            var r = new byte[degree];
            r[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    r[j] = (byte)Mul(r[j], root);
                    if (j + 1 < degree) r[j] ^= r[j + 1];
                }
                root = Mul(root, 2);
            }
            return r;
        }

        public static byte[] RsRemainder(byte[] data, int degree)
        {
            var div = RsDivisor(degree);
            var r = new byte[degree];
            foreach (var b in data)
            {
                int f = b ^ r[0];
                Array.Copy(r, 1, r, 0, degree - 1);
                r[degree - 1] = 0;
                for (int i = 0; i < degree; i++) r[i] ^= (byte)Mul(div[i], f);
            }
            return r;
        }

        public static int FormatBits(int mask)
        {
            int data = (0 << 3) | mask; // ECC level M = 00
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            return ((data << 10) | rem) ^ 0x5412;
        }

        public static int VersionBits(int ver)
        {
            int rem = ver;
            for (int i = 0; i < 12; i++) rem = (rem << 1) ^ ((rem >> 11) * 0x1F25);
            return (ver << 12) | rem;
        }

        /// <summary>Returns the module matrix [x,y] (true = dark) or null when the text is too long.</summary>
        public static bool[,] Encode(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            int ver = 0, dataCw = 0;
            for (int v = 1; v <= 10; v++)
            {
                dataCw = RawCodewords(v) - EccPerBlock[v] * NumBlocks[v];
                int cbits = v < 10 ? 8 : 16;
                if (4 + cbits + 8 * bytes.Length <= dataCw * 8) { ver = v; break; }
            }
            if (ver == 0) return null;

            var bits = new List<bool>();
            Action<int, int> put = (val, n) => { for (int i = n - 1; i >= 0; i--) bits.Add(((val >> i) & 1) != 0); };
            put(4, 4);
            put(bytes.Length, ver < 10 ? 8 : 16);
            foreach (var b in bytes) put(b, 8);
            int cap = dataCw * 8;
            for (int i = 0; i < 4 && bits.Count < cap; i++) bits.Add(false);
            while (bits.Count % 8 != 0) bits.Add(false);
            var data = new byte[dataCw];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) data[i >> 3] |= (byte)(0x80 >> (i & 7));
            for (int i = bits.Count / 8, pad = 0; i < dataCw; i++, pad++) data[i] = (byte)(pad % 2 == 0 ? 0xEC : 0x11);

            var all = Interleave(data, ver);
            return Build(all, ver);
        }

        public static byte[] Interleave(byte[] data, int ver)
        {
            int nb = NumBlocks[ver], ecc = EccPerBlock[ver], raw = RawCodewords(ver);
            int shortCount = nb - raw % nb, shortLen = raw / nb;
            var blocks = new List<byte[]>();
            for (int i = 0, k = 0; i < nb; i++)
            {
                int dl = shortLen - ecc + (i < shortCount ? 0 : 1);
                var dat = new byte[dl];
                Array.Copy(data, k, dat, 0, dl);
                k += dl;
                var e = RsRemainder(dat, ecc);
                var blk = new byte[shortLen + 1];
                Array.Copy(dat, blk, dl);
                Array.Copy(e, 0, blk, shortLen + 1 - ecc, ecc); // short blocks keep a skipped gap slot at index shortLen-ecc
                blocks.Add(blk);
            }
            var res = new List<byte>();
            for (int i = 0; i < shortLen + 1; i++)
                for (int j = 0; j < nb; j++)
                    if (i != shortLen - ecc || j >= shortCount) res.Add(blocks[j][i]);
            return res.ToArray();
        }

        static bool MaskBit(int m, int x, int y)
        {
            switch (m)
            {
                case 0: return (x + y) % 2 == 0;
                case 1: return y % 2 == 0;
                case 2: return x % 3 == 0;
                case 3: return (x + y) % 3 == 0;
                case 4: return (x / 3 + y / 2) % 2 == 0;
                case 5: return x * y % 2 + x * y % 3 == 0;
                case 6: return (x * y % 2 + x * y % 3) % 2 == 0;
                default: return ((x + y) % 2 + x * y % 3) % 2 == 0;
            }
        }

        static bool[,] Build(byte[] all, int ver)
        {
            int size = ver * 4 + 17;
            var m = new bool[size, size];
            var fn = new bool[size, size];
            Action<int, int, bool> set = (x, y, d) => { m[x, y] = d; fn[x, y] = true; };

            for (int i = 0; i < size; i++) { set(6, i, i % 2 == 0); set(i, 6, i % 2 == 0); }
            Action<int, int> finder = (cx, cy) =>
            {
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int x = cx + dx, y = cy + dy;
                        if (x < 0 || y < 0 || x >= size || y >= size) continue;
                        int d = Math.Max(Math.Abs(dx), Math.Abs(dy));
                        set(x, y, d != 2 && d != 4);
                    }
            };
            finder(3, 3); finder(size - 4, 3); finder(3, size - 4);
            var al = Align[ver];
            for (int i = 0; i < al.Length; i++)
                for (int j = 0; j < al.Length; j++)
                {
                    if ((i == 0 && j == 0) || (i == 0 && j == al.Length - 1) || (i == al.Length - 1 && j == 0)) continue;
                    for (int dy = -2; dy <= 2; dy++)
                        for (int dx = -2; dx <= 2; dx++)
                            set(al[i] + dx, al[j] + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
                }
            DrawFormat(m, fn, size, 0);
            if (ver >= 7)
            {
                int vb = VersionBits(ver);
                for (int i = 0; i < 18; i++)
                {
                    bool bit = ((vb >> i) & 1) != 0;
                    int a = size - 11 + i % 3, b = i / 3;
                    set(a, b, bit); set(b, a, bit);
                }
            }

            int k = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool up = ((right + 1) & 2) == 0;
                        int y = up ? size - 1 - vert : vert;
                        if (!fn[x, y] && k < all.Length * 8)
                        {
                            m[x, y] = ((all[k >> 3] >> (7 - (k & 7))) & 1) != 0;
                            k++;
                        }
                    }
            }

            int best = 0, bestPen = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(m, fn, size, mask);
                DrawFormat(m, fn, size, mask);
                int p = Penalty(m, size);
                if (p < bestPen) { bestPen = p; best = mask; }
                ApplyMask(m, fn, size, mask);
            }
            ApplyMask(m, fn, size, best);
            DrawFormat(m, fn, size, best);
            return m;
        }

        public static void ApplyMask(bool[,] m, bool[,] fn, int size, int mask)
        {
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    if (!fn[x, y] && MaskBit(mask, x, y)) m[x, y] = !m[x, y];
        }

        static void DrawFormat(bool[,] m, bool[,] fn, int size, int mask)
        {
            int bits = FormatBits(mask);
            Func<int, bool> bit = i => ((bits >> i) & 1) != 0;
            Action<int, int, bool> set = (x, y, d) => { m[x, y] = d; fn[x, y] = true; };
            for (int i = 0; i <= 5; i++) set(8, i, bit(i));
            set(8, 7, bit(6)); set(8, 8, bit(7)); set(7, 8, bit(8));
            for (int i = 9; i < 15; i++) set(14 - i, 8, bit(i));
            for (int i = 0; i < 8; i++) set(size - 1 - i, 8, bit(i));
            for (int i = 8; i < 15; i++) set(8, size - 15 + i, bit(i));
            set(8, size - 8, true);
        }

        static int Penalty(bool[,] m, int size)
        {
            int p = 0, dark = 0;
            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < size; a++)
                {
                    int run = 1;
                    for (int b = 1; b < size; b++)
                    {
                        bool cur = pass == 0 ? m[b, a] : m[a, b], prev = pass == 0 ? m[b - 1, a] : m[a, b - 1];
                        if (cur == prev) { run++; if (run == 5) p += 3; else if (run > 5) p++; }
                        else run = 1;
                    }
                }
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (m[x, y]) dark++;
                    if (x + 1 < size && y + 1 < size && m[x, y] == m[x + 1, y] && m[x, y] == m[x, y + 1] && m[x, y] == m[x + 1, y + 1]) p += 3;
                }
            int total = size * size;
            p += (int)(Math.Ceiling(Math.Abs(dark * 20 - total * 10) / (double)total) - 1) * 10;
            return p;
        }

        public static Bitmap Render(string text, int px)
        {
            var m = Encode(text);
            if (m == null) return null;
            int n = m.GetLength(0), quiet = 4;
            var bmp = new Bitmap((n + 2 * quiet) * px, (n + 2 * quiet) * px);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        if (m[x, y]) g.FillRectangle(Brushes.Black, (x + quiet) * px, (y + quiet) * px, px, px);
            }
            return bmp;
        }
    }

    /// <summary>Two-factor (TOTP) enrolment / status dialog for team accounts.</summary>
    class ZweiFaktorDialog : DarkForm
    {
        readonly Api api;
        readonly bool forced;
        readonly Panel host = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
        const int W = 620;
        bool enabled, required;
        int recoveryLeft;
        string secret, uri;
        Label err;

        public bool Enabled { get { return enabled; } }

        ZweiFaktorDialog(Api api, bool forced)
        {
            this.api = api;
            this.forced = forced;
            Text = "PXMX Panel – Zwei-Faktor-Anmeldung (2FA)";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(W, 300);
            Controls.Add(host);
        }

        /// <summary>Opens the dialog. forced = 2FA is mandatory for this rank: the caller must log out when false is returned.</summary>
        public static async Task<bool> Open(IWin32Window owner, Api api, bool forced)
        {
            var d = new ZweiFaktorDialog(api, forced);
            using (d)
            {
                if (!await d.Reload()) return false;
                d.ShowStatus();
                d.ShowDialog(owner);
                return d.enabled;
            }
        }

        /// <summary>For tests: build the dialog with a given state without any network access.</summary>
        internal static ZweiFaktorDialog ForPreview(bool forced, string page, string secret, string uri)
        {
            var d = new ZweiFaktorDialog(null, forced) { secret = secret, uri = uri };
            switch (page)
            {
                case "setup": d.ShowSetup(); break;
                case "codes": d.ShowCodes(new[] { "AB12-CD34", "EF56-GH78", "JK90-LM12", "NP34-QR56", "ST78-UV90", "WX12-YZ34", "AB56-CD78", "EF90-GH12", "JK34-LM56", "NP78-QR90" }); break;
                case "disable": d.enabled = true; d.ShowDisable(); break;
                default: d.ShowStatus(); break;
            }
            return d;
        }

        async Task<bool> Reload()
        {
            var r = await api.PostJson("/panel/2fa", new Dictionary<string, object> { { "cmd", "status" } });
            if (!r.Ok)
            {
                MessageBox.Show(r.Status == 0 ? "Team-Server nicht erreichbar: " + r.Error : r.Error, "Zwei-Faktor-Anmeldung", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            enabled = J.B(r.Data, "enabled");
            required = J.B(r.Data, "required");
            recoveryLeft = ToInt(J.S(r.Data, "recovery_left"));
            return true;
        }

        static int ToInt(string s) { int n; return int.TryParse(s, out n) ? n : 0; }

        // ───────────── page helpers ─────────────
        Panel NewPage()
        {
            host.Controls.Clear();
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Bg };
            host.Controls.Add(p);
            err = null;
            return p;
        }

        Label Text_(Panel p, string text, int x, int y, int w, Font f, Color c)
        {
            var l = Theme.Lbl(text, f, c, Theme.Bg);
            l.Location = new Point(x, y);
            l.MaximumSize = new Size(w, 0);
            p.Controls.Add(l);
            return l;
        }

        int Bottom(Control c) { return c.Bottom; }

        void Finish(Panel p, int height)
        {
            ClientSize = new Size(W, height);
            DarkForm.ScaleLate(p);
        }

        void Err(string text, Color c) { if (err != null) { err.ForeColor = c; err.Text = text; } }

        static void Copy(string s) { try { Clipboard.SetText(s); } catch { } }

        void Busy(bool b) { UseWaitCursor = b; }

        // ───────────── status ─────────────
        void ShowStatus()
        {
            var p = NewPage();
            int y = 20;
            Text_(p, "Zwei-Faktor-Anmeldung (2FA)", 24, y, W - 48, Theme.F(15f, FontStyle.Bold), Theme.Text);
            y += 40;
            Label st = Text_(p, enabled ? "Status: AKTIV" : "Status: nicht aktiv", 24, y, W - 48, Theme.F(11f, FontStyle.Bold), enabled ? Theme.Green : Theme.Red);
            y = st.Bottom + 10;
            string info = enabled
                ? "Beim Anmelden wird zusätzlich ein 6-stelliger Code aus deiner Authenticator-App abgefragt.\nVerbleibende Wiederherstellungscodes: " + recoveryLeft
                : "Mit 2FA ist dein Konto auch dann geschützt, wenn jemand dein Passwort kennt. Du brauchst dafür eine Authenticator-App (z. B. Google Authenticator, Aegis, Microsoft Authenticator, 2FAS).";
            if (required && !enabled) info += "\n\nFür deinen Rang ist 2FA Pflicht – ohne Einrichtung kannst du das Panel nicht nutzen.";
            if (required && enabled) info += "\n\nFür deinen Rang ist 2FA Pflicht – nur der Owner kann sie zurücksetzen.";
            var li = Text_(p, info, 24, y, W - 48, Theme.F(10f), Theme.Muted);
            y = li.Bottom + 16;
            err = Text_(p, "", 24, y, W - 48, Theme.F(9.5f), Theme.Red);
            y += 28;

            var close = new FlatBtn(forced && !enabled ? "Abbrechen (abmelden)" : "Schließen", Theme.CardHi) { Location = new Point(W - 24 - 190, y) };
            close.Width = 190;
            close.Click += (s, e) => Close();
            p.Controls.Add(close);
            if (!enabled)
            {
                var setup = new FlatBtn("2FA einrichten", Theme.Accent) { Location = new Point(W - 24 - 190 - 12 - 160, y), Width = 160 };
                setup.Click += async (s, e) => await StartSetup();
                p.Controls.Add(setup);
                AcceptButton = setup;
            }
            else if (!required)
            {
                var dis = new FlatBtn("Deaktivieren", Theme.DarkRed) { Location = new Point(W - 24 - 190 - 12 - 160, y), Width = 160 };
                dis.Click += (s, e) => ShowDisable();
                p.Controls.Add(dis);
            }
            CancelButton = close;
            Finish(p, y + 36 + 24);
        }

        async Task StartSetup()
        {
            Busy(true);
            try
            {
                var r = await api.PostJson("/panel/2fa", new Dictionary<string, object> { { "cmd", "setup" } });
                if (!r.Ok) { Err(r.Status == 0 ? "Team-Server nicht erreichbar: " + r.Error : r.Error, Theme.Red); return; }
                secret = J.S(r.Data, "secret");
                uri = J.S(r.Data, "uri");
                ShowSetup();
            }
            finally { Busy(false); }
        }

        static string Grouped(string s)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++) { if (i > 0 && i % 4 == 0) sb.Append(' '); sb.Append(s[i]); }
            return sb.ToString();
        }

        // ───────────── setup ─────────────
        void ShowSetup()
        {
            var p = NewPage();
            Text_(p, "2FA einrichten", 24, 18, W - 48, Theme.F(15f, FontStyle.Bold), Theme.Text);
            var l1 = Text_(p, "1. Scanne den QR-Code mit deiner Authenticator-App – oder gib den Schlüssel von Hand ein.", 24, 56, W - 48, Theme.F(10f), Theme.Muted);
            int y = l1.Bottom + 12;

            int right = 24;
            Bitmap qr = null;
            try { qr = QrCode.Render(uri ?? "", 4); } catch { qr = null; }
            int qrSize = 0;
            if (qr != null)
            {
                qrSize = qr.Width;
                // QR-Code mittig über den Feldern (weißer Rahmen als Ruhezone, auch im dunklen Design gut scanbar)
                var pb = new PictureBox { Image = qr, SizeMode = PictureBoxSizeMode.CenterImage, Location = new Point((W - qrSize - 16) / 2, y), Size = new Size(qrSize + 16, qrSize + 16), BackColor = Color.White };
                p.Controls.Add(pb);
                y = pb.Bottom + 16;
            }
            int rw = W - right - 24;
            int ry = y;
            if (qr == null)
            {
                var n = Text_(p, "(QR-Code nicht verfügbar – bitte den Schlüssel von Hand eintragen.)", right, ry, rw, Theme.F(9.5f), Theme.Muted);
                ry = n.Bottom + 8;
            }
            Text_(p, "Geheimer Schlüssel (zeitbasiert):", right, ry, rw, Theme.F(9.5f), Theme.Muted);
            ry += 22;
            var sec = Text_(p, Grouped(secret ?? ""), right, ry, rw, new Font("Consolas", 13f, FontStyle.Bold), Theme.Text);
            ry = sec.Bottom + 8;
            var cs = new FlatBtn("Schlüssel kopieren", Theme.CardHi) { Location = new Point(right, ry), Width = 160 };
            cs.Click += (s, e) => { Copy(secret ?? ""); Err("Schlüssel kopiert.", Theme.Green); };
            p.Controls.Add(cs);
            var cu = new FlatBtn("Link kopieren", Theme.CardHi) { Location = new Point(right + 168, ry), Width = 120 };
            cu.Click += (s, e) => { Copy(uri ?? ""); Err("Link kopiert.", Theme.Green); };
            p.Controls.Add(cu);
            ry = cs.Bottom + 10;
            var uriBox = new TextBox { ReadOnly = true, Text = uri ?? "", Location = new Point(right, ry), Width = rw, BackColor = Theme.Input, ForeColor = Theme.Muted, BorderStyle = BorderStyle.FixedSingle, Font = Theme.F(8.5f) };
            p.Controls.Add(uriBox);
            ry = uriBox.Bottom + 6;

            y = Math.Max(y + qrSize, ry) + 14;
            var l2 = Text_(p, "2. Gib den aktuellen 6-stelligen Code aus der App ein:", 24, y, W - 48, Theme.F(10f), Theme.Muted);
            y = l2.Bottom + 8;
            var code = new Field(false) { Location = new Point(24, y), Size = new Size(200, 40) };
            code.Placeholder = "123456";
            p.Controls.Add(code);
            err = Text_(p, "", 240, y + 10, W - 264, Theme.F(9.5f), Theme.Red);
            y += 54;
            var back = new FlatBtn("Abbrechen", Theme.CardHi) { Location = new Point(W - 24 - 130 - 12 - 190, y), Width = 130 };
            back.Click += (s, e) => { if (api == null) Close(); else ShowStatus(); };
            var ok = new FlatBtn("Bestätigen & aktivieren", Theme.Accent) { Location = new Point(W - 24 - 190, y), Width = 190 };
            ok.Click += async (s, e) =>
            {
                var c = (code.Box.Text ?? "").Replace(" ", "").Trim();
                if (c.Length < 6) { Err("Bitte den 6-stelligen Code eingeben.", Theme.Red); return; }
                Busy(true);
                ok.Enabled = false;
                try
                {
                    var r = await api.PostJson("/panel/2fa", new Dictionary<string, object> { { "cmd", "enable" }, { "code", c } });
                    if (!r.Ok) { Err(r.Status == 0 ? "Team-Server nicht erreichbar: " + r.Error : r.Error, Theme.Red); code.Box.SelectAll(); code.Box.Focus(); return; }
                    enabled = true;
                    var codes = new List<string>();
                    foreach (var o in J.L(J.Get(r.Data, "recovery_codes"))) codes.Add(Convert.ToString(o));
                    ShowCodes(codes.ToArray());
                }
                finally { Busy(false); ok.Enabled = true; }
            };
            p.Controls.AddRange(new Control[] { back, ok });
            AcceptButton = ok;
            Finish(p, y + 36 + 24);
            code.Box.Focus();
        }

        // ───────────── recovery codes ─────────────
        void ShowCodes(string[] codes)
        {
            var p = NewPage();
            Text_(p, "2FA ist jetzt aktiv", 24, 18, W - 48, Theme.F(15f, FontStyle.Bold), Theme.Green);
            var warn = Text_(p, "WICHTIG: Speichere diese Wiederherstellungscodes JETZT an einem sicheren Ort (Passwortmanager oder ausgedruckt). Sie werden nur dieses eine Mal angezeigt. Verlierst du dein Handy ohne diese Codes, kann nur der Owner dein 2FA zurücksetzen. Jeder Code funktioniert nur einmal.",
                24, 56, W - 48, Theme.F(10f), Theme.Red);
            int y = warn.Bottom + 12;
            string text = string.Join("\r\n", codes);
            var box = new TextBox
            {
                Multiline = true, ReadOnly = true, Text = text, Location = new Point(24, y), Size = new Size(W - 48, 200),
                BackColor = Theme.Input, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle, Font = new Font("Consolas", 12f, FontStyle.Bold)
            };
            p.Controls.Add(box);
            box.SelectionStart = 0; box.SelectionLength = 0;
            y = box.Bottom + 12;
            var cp = new FlatBtn("Kopieren", Theme.CardHi) { Location = new Point(24, y), Width = 130 };
            cp.Click += (s, e) => { Copy(text); Err("Codes kopiert.", Theme.Green); };
            var sv = new FlatBtn("Als Datei speichern", Theme.CardHi) { Location = new Point(164, y), Width = 190 };
            sv.Click += (s, e) =>
            {
                using (var dlg = new SaveFileDialog { FileName = "PXMX-2FA-Wiederherstellungscodes.txt", Filter = "Textdatei (*.txt)|*.txt" })
                {
                    if (dlg.ShowDialog(this) != DialogResult.OK) return;
                    try
                    {
                        File.WriteAllText(dlg.FileName, "PXMX Panel 2FA – Wiederherstellungscodes (jeder Code nur einmal verwendbar)\r\nSicher aufbewahren, nicht weitergeben!\r\n\r\n" + text + "\r\n", Encoding.UTF8);
                        Err("Gespeichert: " + dlg.FileName, Theme.Green);
                    }
                    catch (Exception ex) { Err("Speichern fehlgeschlagen: " + ex.Message, Theme.Red); }
                }
            };
            p.Controls.AddRange(new Control[] { cp, sv });
            y = cp.Bottom + 12;
            var chk = new CheckBox { Text = "Ich habe die Wiederherstellungscodes sicher gespeichert.", AutoSize = true, ForeColor = Theme.Text, BackColor = Theme.Bg, Location = new Point(24, y) };
            p.Controls.Add(chk);
            y = chk.Bottom + 8;
            err = Text_(p, "", 24, y, W - 48 - 160, Theme.F(9.5f), Theme.Red);
            var done = new FlatBtn("Fertig", Theme.Accent) { Location = new Point(W - 24 - 140, y), Width = 140, Enabled = false };
            chk.CheckedChanged += (s, e) => done.Enabled = chk.Checked;
            done.Click += (s, e) => Close();
            p.Controls.Add(done);
            AcceptButton = null;
            Finish(p, y + 36 + 24);
        }

        // ───────────── disable ─────────────
        void ShowDisable()
        {
            var p = NewPage();
            Text_(p, "2FA deaktivieren", 24, 18, W - 48, Theme.F(15f, FontStyle.Bold), Theme.Text);
            var l = Text_(p, "Zur Bestätigung werden dein Passwort und der aktuelle 6-stellige Code benötigt.", 24, 56, W - 48, Theme.F(10f), Theme.Muted);
            int y = l.Bottom + 12;
            var pw = new Field(true) { Location = new Point(24, y), Size = new Size(W - 48, 40) };
            pw.Placeholder = "Passwort";
            y += 50;
            var code = new Field(false) { Location = new Point(24, y), Size = new Size(200, 40) };
            code.Placeholder = "6-stelliger Code";
            p.Controls.AddRange(new Control[] { pw, code });
            y += 50;
            err = Text_(p, "", 24, y, W - 48, Theme.F(9.5f), Theme.Red);
            y += 28;
            var back = new FlatBtn("Zurück", Theme.CardHi) { Location = new Point(W - 24 - 140 - 12 - 150, y), Width = 140 };
            back.Click += (s, e) => { if (api == null) Close(); else ShowStatus(); };
            var ok = new FlatBtn("Deaktivieren", Theme.DarkRed) { Location = new Point(W - 24 - 150, y), Width = 150 };
            ok.Click += async (s, e) =>
            {
                var c = (code.Box.Text ?? "").Replace(" ", "").Trim();
                if (pw.Box.Text == "" || c == "") { Err("Bitte Passwort und Code eingeben.", Theme.Red); return; }
                Busy(true);
                ok.Enabled = false;
                try
                {
                    var r = await api.PostJson("/panel/2fa", new Dictionary<string, object> { { "cmd", "disable" }, { "password", pw.Box.Text }, { "code", c } });
                    if (!r.Ok) { Err(r.Status == 0 ? "Team-Server nicht erreichbar: " + r.Error : r.Error, Theme.Red); return; }
                    enabled = false;
                    await Reload();
                    ShowStatus();
                    Err("2FA wurde deaktiviert.", Theme.Green);
                }
                finally { Busy(false); ok.Enabled = true; }
            };
            p.Controls.AddRange(new Control[] { back, ok });
            AcceptButton = ok;
            Finish(p, y + 36 + 24);
        }
    }
}
