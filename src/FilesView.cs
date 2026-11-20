using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    class RemoteEntry
    {
        public string Name, Path, Link, Owner, Group, Mode;
        public char Type;       // d, f, l (broken link), s (socket) …
        public bool IsDir, IsLink;
        public long Size;
        public DateTime MTime;
    }

    /// <summary>SFTP-like file manager over the existing SSH key: whole server, streaming transfers, editor, permissions, archives.</summary>
    class FilesView : Panel
    {
        readonly Func<Ssh> getSsh;
        readonly Func<bool> sshReady;
        readonly Action<string, Color> setStatus;
        readonly Func<List<KeyValuePair<string, string>>> bookmarksExtra;
        public Action<string> OpenTerminalAt;

        string cwd = "/opt/crafty/docker/servers";
        List<RemoteEntry> entries = new List<RemoteEntry>();
        readonly Stack<string> back = new Stack<string>();
        bool searchMode, busy, loadedOnce, showHidden;
        int sortCol;
        bool sortDesc;
        List<string> clip = new List<string>();
        bool clipCut;

        Label hSub, editorTitle, info;
        Field pathField, filter;
        ListView list;
        TextBox editor;
        FlatBtn bSave, bClose;
        SplitContainer split;
        TransferPanel transfers;
        ProgressLine progress;
        CheckBox hidden;
        string openPath;
        long openMTime;
        bool openCrlf, dirty, suppress, openLatin1;
        readonly Queue<Transfer> queue = new Queue<Transfer>();
        bool queueRunning;
        static Bitmap iDir, iFile, iLink, iArchive, iJar, iText, iImage;

        public FilesView(Func<Ssh> getSsh, Func<bool> sshReady, Action<string, Color> setStatus, Func<List<KeyValuePair<string, string>>> bookmarksExtra)
        {
            this.getSsh = getSsh;
            this.sshReady = sshReady;
            this.setStatus = setStatus;
            this.bookmarksExtra = bookmarksExtra;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;
            MakeIcons();
            Build();
        }

        public bool HasUnsaved { get { return dirty; } }

        public async void Activate()
        {
            if (!sshReady())
            {
                hSub.Text = "Für den Dateimanager braucht die App die SSH-Verbindung – richte sie einmal unter „Docker“ ein.";
                hSub.ForeColor = Theme.Yellow;
                return;
            }
            hSub.ForeColor = Theme.Muted;
            var s = getSsh();
            hSub.Text = s.User + "@" + s.Host + "  ·  kompletter Server über SSH  ·  Strg+C/X/V, Entf, F2, F5, Rücktaste, Doppelklick";
            if (!loadedOnce) await Go(cwd, false);
        }

        public async void SearchFor(string q)
        {
            filter.Box.Text = q;
            if (!sshReady()) return;
            if (!loadedOnce) await Go(cwd, false);
            await Search();
        }
        public void Deactivate() { }

        public async void OpenPath(string path)
        {
            if (!sshReady()) { Activate(); return; }
            await Go(path, true);
        }

        // ───────────────────────────── Layout ─────────────────────────────
        void Build()
        {
            TabBtn[] none;
            var header = Ui.Header("Server-Dateien", out hSub, null, out none, null);

            var bBack = Ui.Btn("←", Theme.CardHi).Tip("Zurück (zuletzt besuchter Ordner)"); bBack.Width = 40;
            var bUp = Ui.Btn("⬆", Theme.CardHi).Tip("Eine Ebene höher  (Rücktaste)"); bUp.Width = 40;
            var bRefresh = Ui.Btn("↻", Theme.CardHi).Tip("Neu laden  (F5)"); bRefresh.Width = 40;
            pathField = new Field(false) { Width = 460, Height = 36 };
            pathField.Box.Font = Theme.Mono;
            pathField.Placeholder = "Pfad eingeben und Enter drücken, z. B. /opt/crafty";
            var bMarks = Ui.Btn("★ Orte ▾", Theme.CardHi).Tip("Schnell zu wichtigen Ordnern und Minecraft-Servern springen");
            var nav = Ui.Bar(46, bBack, bUp, bRefresh, pathField, bMarks);
            nav.Resize += (s, e) =>
            {
                int used = nav.Controls.Cast<Control>().Where(c => c != pathField).Sum(c => c.Width + c.Margin.Horizontal);
                pathField.Width = Math.Max(Theme.Px(160), nav.ClientSize.Width - used - pathField.Margin.Horizontal - Theme.Px(4));
            };

            var bUpload = Ui.Btn("⇪ Hochladen ▾", Theme.Accent).Tip("Dateien oder ganze Ordner hochladen – oder einfach in die Liste ziehen");
            var bUpDir = Ui.Btn("⇪ Ordner", Theme.Accent);
            var bDown = Ui.Btn("⇩ Herunterladen", Theme.CardHi).Tip("Markierte Dateien/Ordner auf diesen PC laden");
            var bNew = Ui.Btn("+ Neu ▾", Theme.CardHi).Tip("Neuen Ordner oder neue Datei anlegen");
            var bEdit = Ui.Btn("✎ Bearbeiten ▾", Theme.CardHi).Tip("Umbenennen, Kopieren, Verschieben, Rechte");
            var bRename = Ui.Btn("✎ Umbenennen", Theme.CardHi);
            var bPerm = Ui.Btn("Rechte", Theme.CardHi);
            var bArch = Ui.Btn("Archiv ▾", Theme.CardHi).Tip("Als .zip/.tar.gz packen oder entpacken");
            var bTerm = Ui.Btn(">_ Terminal", Theme.CardHi).Tip("Terminal in diesem Ordner öffnen");
            var bDel = Ui.Btn("✕ Löschen", Theme.Mix(Theme.Red, Theme.Bg, 0.35)).Tip("Markierte Einträge endgültig löschen  (Entf)");
            filter = new Field(false) { Width = 230, Height = 36 };
            filter.Placeholder = "Filtern · Enter = auch Unterordner";
            var bFind = Ui.Btn("Suchen", Theme.CardHi);
            hidden = new CheckBox { Text = "Versteckte", ForeColor = Theme.Text2, BackColor = Theme.Bg, AutoSize = true, Margin = new Padding(0, 9, 0, 0) };
            hidden.Tip("Dateien anzeigen, die mit einem Punkt beginnen");
            var tools = Ui.Bar(48, bUpload, bDown, bNew, bEdit, bArch, bTerm, bDel, filter, hidden);
            bEdit.Click += (s, e) =>
            {
                var m = Ui.Menu();
                m.Item("Umbenennen  (F2)", async () => await Rename());
                m.Item("Kopieren  (Strg+C)", () => SetClip(false));
                m.Item("Ausschneiden  (Strg+X)", () => SetClip(true));
                m.Item("Einfügen  (Strg+V)", async () => await PasteClip(), clip.Count > 0);
                m.Sep();
                m.Item("Rechte & Besitzer …", async () => await Permissions());
                m.Item("Pfad kopieren", () => { var sl = Selected(); Clipboard.SetText(sl.Count > 0 ? sl[0].Path : cwd); });
                Ui.DropDown(bEdit, m);
            };

            progress = new ProgressLine { Dock = DockStyle.Top, BackColor = Theme.Bg };

            split = new SplitContainer { Dock = DockStyle.Fill, BackColor = Theme.Bg, SplitterWidth = 12, Panel2Collapsed = true };
            split.Panel1.BackColor = split.Panel2.BackColor = Theme.Bg;

            list = Ui.List(new[] { "Name", "Größe", "Geändert", "Rechte", "Besitzer" }, new[] { 330, 90, 130, 110, 120 }, true, true);
            list.Columns[1].TextAlign = HorizontalAlignment.Right;
            list.AllowDrop = true;
            var left = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(8, 10, 8, 34) };
            info = Theme.Lbl("", Theme.F(8.75f), Theme.Muted, Color.Transparent);
            info.AutoSize = false;
            info.Dock = DockStyle.Bottom;
            info.Height = 0;
            left.Controls.Add(list);
            var infoHost = new Panel { Dock = DockStyle.Bottom, Height = 1, BackColor = Theme.Card };
            left.Resize += (s, e) => { info.Location = new Point(14, left.Height - 30); info.Size = new Size(left.Width - 28, 20); };
            info.Dock = DockStyle.None;
            left.Controls.Add(info);
            split.Panel1.Controls.Add(left);

            var right = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(10, 54, 6, 10) };
            editorTitle = Theme.Lbl("", Theme.F(10f, FontStyle.Bold), Theme.Text2, Color.Transparent);
            editorTitle.AutoSize = false; editorTitle.AutoEllipsis = true;
            editorTitle.Location = new Point(14, 10); editorTitle.Size = new Size(300, 34);
            editorTitle.TextAlign = ContentAlignment.MiddleLeft;
            bSave = new FlatBtn("Speichern (Strg+S)", Theme.Accent) { Size = new Size(160, 34) };
            bClose = new FlatBtn("Schließen", Theme.CardHi) { Size = new Size(96, 34) };
            right.Resize += (s, e) =>
            {
                bSave.Location = new Point(right.Width - bSave.Width - 10, 10);
                bClose.Location = new Point(bSave.Left - bClose.Width - 8, 10);
                editorTitle.Width = Math.Max(60, bClose.Left - 24);
            };
            editor = new TextBox
            {
                Dock = DockStyle.Fill, Multiline = true, ScrollBars = ScrollBars.Both, WordWrap = false, AcceptsTab = true, AcceptsReturn = true,
                BorderStyle = BorderStyle.None, BackColor = Theme.Input, ForeColor = Theme.Text, Font = Theme.Mono, MaxLength = 0
            };
            right.Controls.Add(editor);
            right.Controls.Add(editorTitle);
            right.Controls.Add(bClose);
            right.Controls.Add(bSave);
            split.Panel2.Controls.Add(right);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 150, BackColor = Theme.Bg, Padding = new Padding(0, 12, 0, 0) };
            var tbox = new RoundPanel { Dock = DockStyle.Fill, Padding = new Padding(6, 4, 6, 8) };
            transfers = new TransferPanel { Dock = DockStyle.Fill };
            var clearBtn = new FlatBtn("Fertige entfernen", Theme.CardHi) { Height = 26, Width = 130, Font = Theme.F(8.25f, FontStyle.Bold), Anchor = AnchorStyles.Top | AnchorStyles.Right };
            tbox.Resize += (s, e) => clearBtn.Location = new Point(tbox.Width - 142, 6);
            clearBtn.Click += (s, e) => transfers.ClearFinished();
            tbox.Controls.Add(clearBtn);
            tbox.Controls.Add(transfers);
            bottom.Controls.Add(tbox);
            transfers.CancelClicked += t => { if (Dialogs.Confirm(FindForm(), "Abbrechen", "Übertragung „" + t.Name + "“ abbrechen?")) t.Cancel(); };

            Controls.Add(split);
            Controls.Add(bottom);
            Controls.Add(progress);
            Controls.Add(tools);
            Controls.Add(nav);
            Controls.Add(header);

            // events
            bBack.Click += async (s, e) => { if (back.Count > 0) await Go(back.Pop(), false); };
            bUp.Click += async (s, e) => await GoUp();
            bRefresh.Click += async (s, e) => await Go(cwd, false);
            pathField.Box.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Go(pathField.Box.Text.Trim(), true); } };
            filter.Box.TextChanged += (s, e) => { if (!searchMode) Render(); };
            filter.Box.KeyDown += async (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Search(); } };
            bFind.Click += async (s, e) => await Search();
            bMarks.Click += (s, e) => ShowBookmarks(bMarks);
            hidden.CheckedChanged += (s, e) => { showHidden = hidden.Checked; Render(); };
            bUpload.Click += (s, e) =>
            {
                var m = Ui.Menu();
                m.Item("Dateien auswählen …", async () =>
                {
                    using (var d = new OpenFileDialog { Multiselect = true, Title = "Dateien nach " + cwd + " hochladen" })
                        if (d.ShowDialog(this) == DialogResult.OK) await Upload(d.FileNames);
                });
                m.Item("Ordner auswählen (mit Unterordnern) …", () => bUpDir.PerformClick());
                Ui.DropDown(bUpload, m);
            };
            bUpDir.Click += async (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Ordner (mit allen Unterordnern) nach " + cwd + " hochladen", ShowNewFolderButton = false })
                    if (d.ShowDialog(this) == DialogResult.OK) await Upload(new[] { d.SelectedPath });
            };
            bDown.Click += (s, e) => Download();
            bNew.Click += (s, e) =>
            {
                var m = Ui.Menu();
                m.Item("Neuer Ordner", async () => await NewItem(true));
                m.Item("Neue Datei", async () => await NewItem(false));
                Ui.DropDown(bNew, m);
            };
            bRename.Click += async (s, e) => await Rename();
            bPerm.Click += async (s, e) => await Permissions();
            bArch.Click += (s, e) =>
            {
                var m = Ui.Menu();
                m.Item("Auswahl packen (.tar.gz)", async () => await Pack("tar.gz"));
                m.Item("Auswahl packen (.zip)", async () => await Pack("zip"));
                m.Item("Entpacken (hier)", async () => await Unpack(false));
                m.Item("Entpacken in neuen Ordner", async () => await Unpack(true));
                Ui.DropDown(bArch, m);
            };
            bTerm.Click += (s, e) => { if (OpenTerminalAt != null) OpenTerminalAt(cwd); };
            bDel.Click += async (s, e) => await Delete();

            list.ItemActivate += async (s, e) => await Activate(Selected().FirstOrDefault());
            list.ColumnClick += (s, e) => { if (sortCol == e.Column) sortDesc = !sortDesc; else { sortCol = e.Column; sortDesc = e.Column == 1 || e.Column == 2; } Render(); };
            list.SelectedIndexChanged += (s, e) => UpdateInfo();
            list.KeyDown += async (s, e) =>
            {
                if (e.KeyCode == Keys.Back) { e.SuppressKeyPress = true; await GoUp(); }
                else if (e.KeyCode == Keys.Delete) await Delete();
                else if (e.KeyCode == Keys.F2) await Rename();
                else if (e.KeyCode == Keys.F5) await Go(cwd, false);
                else if (e.Control && e.KeyCode == Keys.A) { foreach (ListViewItem i in list.Items) i.Selected = true; }
                else if (e.Control && e.KeyCode == Keys.C) SetClip(false);
                else if (e.Control && e.KeyCode == Keys.X) SetClip(true);
                else if (e.Control && e.KeyCode == Keys.V) await PasteClip();
            };
            list.MouseUp += (s, e) => { if (e.Button == MouseButtons.Right) ShowContext(e.Location); };
            list.DragEnter += (s, e) => e.Effect = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            list.DragDrop += async (s, e) =>
            {
                var paths = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (paths != null && paths.Length > 0) await Upload(paths);
            };

            editor.TextChanged += (s, e) => { if (!suppress && openPath != null && !dirty) { dirty = true; UpdateEditorTitle(); } };
            editor.KeyDown += async (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.S) { e.SuppressKeyPress = true; await Save(); }
                else if (e.Control && e.KeyCode == Keys.A) { e.SuppressKeyPress = true; editor.SelectAll(); }
                else if (e.Control && e.KeyCode == Keys.F) { e.SuppressKeyPress = true; Find(true); }
                else if (e.KeyCode == Keys.F3) { e.SuppressKeyPress = true; Find(false); }
            };
            bSave.Click += async (s, e) => await Save();
            bClose.Click += (s, e) => CloseEditor(true);
        }

        static void MakeIcons()
        {
            if (iDir != null) return;
            Func<Color, bool, string, Bitmap> mk = (c, folder, letter) =>
            {
                var bmp = new Bitmap(18, 18);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(c))
                    {
                        if (folder)
                        {
                            using (var p = Theme.RoundRect(new Rectangle(1, 3, 8, 4), 1)) g.FillPath(b, p);
                            using (var p = Theme.RoundRect(new Rectangle(1, 5, 16, 11), 2)) g.FillPath(b, p);
                        }
                        else using (var p = Theme.RoundRect(new Rectangle(3, 1, 12, 16), 2)) g.FillPath(b, p);
                    }
                    if (letter != null)
                        TextRenderer.DrawText(g, letter, new Font("Segoe UI", 6.5f, FontStyle.Bold), new Rectangle(0, folder ? 5 : 2, 18, 14), Theme.Card,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                }
                return bmp;
            };
            iDir = mk(Theme.Hex("#e0b04a"), true, null);
            iFile = mk(Theme.Hex("#7d8599"), false, null);
            iLink = mk(Theme.SoftGreen, false, "↗");
            iArchive = mk(Theme.Mint, false, "Z");
            iJar = mk(Theme.Hex("#ff8a4c"), false, "J");
            iText = mk(Theme.Hex("#c3c8d4"), false, "T");
            iImage = mk(Theme.Hex("#3ddc84"), false, "▲");
        }

        static readonly string[] TextExt = { ".yml", ".yaml", ".json", ".properties", ".txt", ".log", ".toml", ".conf", ".cfg", ".ini", ".sh", ".md", ".xml", ".sk", ".env", ".js", ".csv", ".lang", ".mcmeta", ".service", ".py", ".html", ".css" };
        static readonly string[] ArchExt = { ".zip", ".tar", ".gz", ".tgz", ".xz", ".bz2", ".7z", ".rar" };

        static Bitmap IconFor(RemoteEntry e)
        {
            if (e.IsDir) return iDir;
            if (e.IsLink) return iLink;
            var x = System.IO.Path.GetExtension(e.Name).ToLowerInvariant();
            if (x == ".jar") return iJar;
            if (ArchExt.Contains(x)) return iArchive;
            if (x == ".png" || x == ".jpg" || x == ".gif" || x == ".webp") return iImage;
            if (TextExt.Contains(x)) return iText;
            return iFile;
        }

        // ───────────────────────────── Listing ─────────────────────────────
        static string Join(string dir, string name) { return dir == "/" ? "/" + name : dir.TrimEnd('/') + "/" + name; }
        static string ParentDir(string p) { p = p.TrimEnd('/'); int i = p.LastIndexOf('/'); return i <= 0 ? "/" : p.Substring(0, i); }

        async Task GoUp() { if (searchMode) { await Go(cwd, false); return; } if (cwd != "/") await Go(ParentDir(cwd), true); }

        async Task Go(string path, bool pushHistory)
        {
            if (string.IsNullOrEmpty(path) || busy) return;
            if (!path.StartsWith("/")) path = Join(cwd, path);
            busy = true;
            progress.Set(double.NaN);
            try
            {
                var r = await getSsh().Run(
                    "D=" + Ssh.B64(path) + "\ncd -- \"$D\" 2>/dev/null || { echo \"Ordner nicht gefunden: $D\" >&2; exit 3; }\npwd -P\n" +
                    "find . -mindepth 1 -maxdepth 1 -printf '%Y\\t%y\\t%s\\t%T@\\t%m\\t%u\\t%g\\t%l\\t%f\\0'", 45000);
                if (!r.Ok) { setStatus("Ordner: " + r.Message, Theme.Red); return; }
                int nl = r.Out.IndexOf('\n');
                var real = nl > 0 ? r.Out.Substring(0, nl).Trim() : path;
                if (pushHistory && real != cwd) back.Push(cwd);
                cwd = real;
                entries = ParseFind(r.Out.Substring(nl + 1), cwd, false);
                searchMode = false;
                loadedOnce = true;
                pathField.Box.Text = cwd;
                Render();
            }
            finally { busy = false; progress.Set(-1); }
        }

        static List<RemoteEntry> ParseFind(string text, string dir, bool fullPaths)
        {
            var res = new List<RemoteEntry>();
            foreach (var rec in text.Split('\0'))
            {
                var f = rec.TrimStart('\n').Split(new[] { '\t' }, 9);
                if (f.Length < 9) continue;
                double mt;
                double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out mt);
                long size; long.TryParse(f[2], out size);
                var name = f[8];
                var e = new RemoteEntry
                {
                    Type = f[0].Length > 0 ? f[0][0] : '?', IsLink = f[1] == "l", IsDir = f[0] == "d", Size = size,
                    MTime = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(mt).ToLocalTime(),
                    Mode = f[4], Owner = f[5], Group = f[6], Link = f[7]
                };
                if (fullPaths) { e.Path = name; e.Name = name.StartsWith(dir + "/") ? name.Substring(dir.Length + 1) : name; }
                else { e.Name = name; e.Path = Join(dir, name); }
                res.Add(e);
            }
            return res;
        }

        static string ModeText(string octal, bool dir, bool link)
        {
            int m;
            try { m = Convert.ToInt32(octal, 8); } catch { return octal; }
            var sb = new StringBuilder(link ? "l" : dir ? "d" : "-");
            string rwx = "rwx";
            for (int i = 8; i >= 0; i--) sb.Append((m & (1 << i)) != 0 ? rwx[(8 - i) % 3] : '-');
            return sb + "  " + octal;
        }

        void Render()
        {
            var q = searchMode ? "" : filter.Box.Text.Trim();
            IEnumerable<RemoteEntry> src = entries;
            if (!showHidden && !searchMode) src = src.Where(e => !e.Name.StartsWith("."));
            if (q.Length > 0) src = src.Where(e => e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0);
            Func<RemoteEntry, object> key;
            switch (sortCol)
            {
                case 1: key = e => e.Size; break;
                case 2: key = e => e.MTime; break;
                case 3: key = e => e.Mode; break;
                case 4: key = e => e.Owner; break;
                default: key = e => e.Name.ToLowerInvariant(); break;
            }
            var ordered = src.OrderBy(e => e.IsDir ? 0 : 1);
            ordered = sortDesc ? ordered.ThenByDescending(key) : ordered.ThenBy(key);
            var labels = new Dictionary<string, string>();
            if (bookmarksExtra != null) foreach (var kv in bookmarksExtra()) labels[kv.Value] = kv.Key;
            var rows = new List<ListViewItem>();
            foreach (var e in ordered)
            {
                var row = Ui.Row(new RowTag { Key = e.Path, Data = e, Icon = IconFor(e) },
                    e.Name + (labels.ContainsKey(e.Path) ? "   ·  " + labels[e.Path] : "") + (e.IsLink ? "  → " + e.Link : ""), e.IsDir ? "" : Theme.Bytes(e.Size), e.MTime.ToString("dd.MM.yy HH:mm"),
                    ModeText(e.Mode, e.IsDir, e.IsLink), e.Owner + (e.Group != e.Owner ? ":" + e.Group : ""));
                if (e.Owner == "root" && cwd.StartsWith("/opt/crafty")) row.SubItems[4].ForeColor = Theme.Yellow;
                rows.Add(row);
            }
            Ui.Fill(list, rows);
            UpdateInfo();
        }

        void UpdateInfo()
        {
            var sel = Selected();
            var vis = list.Items.Count;
            if (sel.Count == 0)
                info.Text = (searchMode ? "Suchergebnisse: " : "") + vis + " Einträge  ·  " + Theme.Bytes(entries.Where(e => !e.IsDir).Sum(e => e.Size)) + " in Dateien" + (clip.Count > 0 ? "  ·  Zwischenablage: " + clip.Count + (clipCut ? " (ausschneiden)" : " (kopieren)") : "");
            else
                info.Text = sel.Count + " ausgewählt  ·  " + Theme.Bytes(sel.Where(e => !e.IsDir).Sum(e => e.Size)) + (sel.Count == 1 ? "  ·  " + sel[0].Path : "");
        }

        List<RemoteEntry> Selected()
        {
            return list.SelectedItems.Cast<ListViewItem>().Select(i => ((RowTag)i.Tag).Data as RemoteEntry).Where(e => e != null).ToList();
        }

        async Task Activate(RemoteEntry e)
        {
            if (e == null) return;
            if (e.IsDir) { await Go(e.Path, true); return; }
            if (searchMode) { var p = ParentDir(e.Path); await Go(p, true); }
            await OpenEditor(e);
        }

        async Task Search()
        {
            var q = filter.Box.Text.Trim();
            if (q.Length == 0) { if (searchMode) await Go(cwd, false); return; }
            if (busy) return;
            busy = true;
            progress.Set(double.NaN);
            setStatus("Suche „" + q + "“ unter " + cwd + " …", Theme.Muted);
            try
            {
                var r = await getSsh().Run("D=" + Ssh.B64(cwd) + "\nQ=" + Ssh.B64(q) + "\ncd -- \"$D\" || exit 3\n" +
                    "find \"$D\" -xdev -maxdepth 8 -iname \"*$Q*\" -not -path '*/proc/*' -printf '%Y\\t%y\\t%s\\t%T@\\t%m\\t%u\\t%g\\t%l\\t%p\\0' 2>/dev/null | head -z -n 1000", 90000);
                entries = ParseFind(r.Out, cwd, true);
                searchMode = true;
                Render();
                setStatus(entries.Count + " Treffer für „" + q + "“" + (entries.Count >= 1000 ? " (auf 1000 begrenzt)" : "") + "  ·  Enter im leeren Feld = zurück", Theme.Green);
            }
            finally { busy = false; progress.Set(-1); }
        }

        void ShowBookmarks(Control anchor)
        {
            var m = Ui.Menu();
            m.Item("Crafty-Server (alle)", async () => await Go("/opt/crafty/docker/servers", true));
            var extra = bookmarksExtra != null ? bookmarksExtra() : null;
            if (extra != null) foreach (var kv in extra) { var p = kv.Value; m.Item("   " + kv.Key, async () => await Go(p, true)); }
            m.Sep();
            foreach (var p in new[] { "/opt/crafty/docker", "/opt/crafty/docker/backups", "/root", "/etc", "/var/log", "/tmp", "/" })
            {
                var pp = p;
                m.Item(pp, async () => await Go(pp, true));
            }
            Ui.DropDown(anchor, m);
        }

        void ShowContext(Point at)
        {
            var sel = Selected();
            var m = Ui.Menu();
            bool one = sel.Count == 1;
            if (one && !sel[0].IsDir) m.Item("Bearbeiten / Ansehen", async () => await OpenEditor(sel[0]));
            if (one && sel[0].IsDir) m.Item("Öffnen", async () => await Go(sel[0].Path, true));
            if (sel.Count > 0) m.Item("Herunterladen …", Download);
            m.Item("Hochladen in diesen Ordner …", async () =>
            {
                using (var d = new OpenFileDialog { Multiselect = true })
                    if (d.ShowDialog(this) == DialogResult.OK) await Upload(d.FileNames);
            });
            m.Sep();
            if (sel.Count > 0) { m.Item("Kopieren  (Strg+C)", () => SetClip(false)); m.Item("Ausschneiden  (Strg+X)", () => SetClip(true)); }
            if (clip.Count > 0) m.Item("Einfügen (" + clip.Count + ")  (Strg+V)", async () => await PasteClip());
            if (one) m.Item("Umbenennen  (F2)", async () => await Rename());
            if (one) m.Item("Duplizieren", async () => { clip = new List<string> { sel[0].Path }; clipCut = false; await PasteClip(); });
            m.Sep();
            if (sel.Count > 0) m.Item("Rechte / Besitzer …", async () => await Permissions());
            if (sel.Count > 0) m.Item("Packen (.tar.gz)", async () => await Pack("tar.gz"));
            if (sel.Count > 0) m.Item("Packen (.zip)", async () => await Pack("zip"));
            if (one && IsArchive(sel[0].Name)) m.Item("Entpacken hier", async () => await Unpack(false));
            if (one && sel[0].IsDir) m.Item("Größe berechnen", async () => await DirSize(sel[0]));
            m.Sep();
            if (one) m.Item("Pfad kopieren", () => Clipboard.SetText(sel[0].Path));
            m.Item("Terminal hier öffnen", () => { if (OpenTerminalAt != null) OpenTerminalAt(one && sel[0].IsDir ? sel[0].Path : cwd); });
            if (sel.Count > 0) { m.Sep(); m.Item("Löschen  (Entf)", async () => await Delete()); }
            m.Show(list, at);
        }

        static bool IsArchive(string n)
        {
            n = n.ToLowerInvariant();
            return n.EndsWith(".zip") || n.EndsWith(".tar") || n.EndsWith(".tar.gz") || n.EndsWith(".tgz") || n.EndsWith(".tar.xz") || n.EndsWith(".tar.bz2") || n.EndsWith(".gz");
        }

        // ───────────────────────────── Operations ─────────────────────────────
        async Task<bool> Exec(string label, string script, int timeout = 120000)
        {
            setStatus(label + " …", Theme.Muted);
            progress.Set(double.NaN);
            try
            {
                var r = await getSsh().Run(script, timeout);
                if (r.Ok) setStatus(label + " – erledigt.", Theme.Green);
                else setStatus(label + " fehlgeschlagen: " + r.Message, Theme.Red);
                return r.Ok;
            }
            finally { progress.Set(-1); }
        }

        static bool ValidName(string n) { return !string.IsNullOrWhiteSpace(n) && n.IndexOf('/') < 0 && n != "." && n != ".." && n.IndexOf('\0') < 0; }

        async Task NewItem(bool dir)
        {
            var n = Dialogs.Ask(FindForm(), dir ? "Neuer Ordner" : "Neue Datei", "Name in " + cwd + ":", "");
            if (n == null) return;
            n = n.Trim();
            if (!ValidName(n)) { setStatus("Ungültiger Name.", Theme.Red); return; }
            var p = Join(cwd, n);
            var own = " && chown --reference=\"$D\" -- \"$P\" 2>/dev/null; true";
            if (await Exec((dir ? "Ordner" : "Datei") + " anlegen: " + n,
                "D=" + Ssh.B64(cwd) + "\nP=" + Ssh.B64(p) + "\n[ -e \"$P\" ] && { echo 'Existiert bereits.' >&2; exit 4; }\n" + (dir ? "mkdir -- \"$P\"" : "touch -- \"$P\"") + own))
            {
                await Go(cwd, false);
                if (!dir) { var e = entries.FirstOrDefault(x => x.Path == p); if (e != null) await OpenEditor(e); }
            }
        }

        async Task Rename()
        {
            var sel = Selected();
            if (sel.Count != 1) return;
            var e = sel[0];
            var n = Dialogs.Ask(FindForm(), "Umbenennen", "Neuer Name für „" + e.Name + "“:", e.Name);
            if (n == null || n.Trim() == e.Name) return;
            n = n.Trim();
            if (!ValidName(n)) { setStatus("Ungültiger Name.", Theme.Red); return; }
            var dst = Join(ParentDir(e.Path), n);
            if (await Exec("Umbenennen", "S=" + Ssh.B64(e.Path) + "\nT=" + Ssh.B64(dst) + "\n[ -e \"$T\" ] && { echo 'Ziel existiert bereits.' >&2; exit 4; }\nmv -- \"$S\" \"$T\"")) await Go(cwd, false);
        }

        static readonly string[] Protected = { "/", "/etc", "/usr", "/bin", "/sbin", "/lib", "/lib64", "/boot", "/root", "/var", "/opt", "/home", "/opt/crafty", "/opt/crafty/docker", "/opt/crafty/docker/servers" };

        async Task Delete()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            if (sel.Any(e => Protected.Contains(e.Path.TrimEnd('/')))) { setStatus("Systemordner werden zur Sicherheit nicht gelöscht.", Theme.Red); return; }
            var names = string.Join("\n", sel.Take(12).Select(e => (e.IsDir ? "📁 " : "   ") + e.Name)) + (sel.Count > 12 ? "\n… und " + (sel.Count - 12) + " weitere" : "");
            if (!Dialogs.Confirm(FindForm(), "Löschen", sel.Count + " Einträge endgültig löschen?\n\n" + names + "\n\nOrdner werden mit Inhalt gelöscht. Es gibt keinen Papierkorb!")) return;
            var sb = new StringBuilder();
            foreach (var e in sel) sb.Append("rm -rf -- " + Ssh.B64(e.Path) + "\n");
            if (await Exec("Löschen (" + sel.Count + ")", sb.ToString(), 300000)) await Go(cwd, false);
        }

        void SetClip(bool cut)
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            clip = sel.Select(e => e.Path).ToList();
            clipCut = cut;
            setStatus(sel.Count + " Einträge " + (cut ? "ausgeschnitten" : "kopiert") + " – im Zielordner Strg+V.", Theme.Text2);
            UpdateInfo();
        }

        async Task PasteClip()
        {
            if (clip.Count == 0) return;
            var sb = new StringBuilder("D=" + Ssh.B64(cwd) + "\n");
            foreach (var p in clip)
            {
                sb.Append("S=" + Ssh.B64(p) + "\nN=$(basename -- \"$S\"); T=\"$D/$N\"\n");
                if (clipCut) sb.Append("[ \"$(dirname -- \"$S\")\" = \"$D\" ] || { [ -e \"$T\" ] && { echo \"Existiert schon: $N\" >&2; exit 4; }; mv -- \"$S\" \"$T\" || exit 1; }\n");
                else sb.Append("if [ -e \"$T\" ]; then b=\"${N%.*}\"; x=\"${N##*.}\"; [ \"$b\" = \"$N\" ] && x='' || x=\".$x\"; i=1; while [ -e \"$D/${b}_kopie$i$x\" ]; do i=$((i+1)); done; T=\"$D/${b}_kopie$i$x\"; fi\ncp -a -- \"$S\" \"$T\" || exit 1\n");
            }
            if (await Exec((clipCut ? "Verschieben" : "Kopieren") + " (" + clip.Count + ")", sb.ToString(), 900000))
            {
                if (clipCut) clip.Clear();
                await Go(cwd, false);
            }
        }

        async Task DirSize(RemoteEntry e)
        {
            var r = await getSsh().Run("du -sb -- " + Ssh.B64(e.Path) + " | cut -f1; find " + Ssh.B64(e.Path) + " -type f | wc -l", 120000);
            var l = r.Out.Split('\n');
            double b; double.TryParse(l[0].Trim(), out b);
            setStatus(e.Name + ": " + Theme.Bytes(b) + (l.Length > 1 ? " in " + l[1].Trim() + " Dateien" : ""), r.Ok ? Theme.Green : Theme.Red);
        }

        async Task Permissions()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            var f = sel[0];
            int mode;
            try { mode = Convert.ToInt32(f.Mode, 8); } catch { mode = 0x1A4; }
            using (var d = new DarkForm())
            {
                d.Text = "Rechte – " + (sel.Count == 1 ? f.Name : sel.Count + " Einträge");
                d.FormBorderStyle = FormBorderStyle.FixedDialog; d.MaximizeBox = d.MinimizeBox = false; d.ShowInTaskbar = false;
                d.StartPosition = FormStartPosition.CenterParent;
                d.ClientSize = new Size(460, 360);
                var boxes = new CheckBox[9];
                string[] who = { "Besitzer", "Gruppe", "Andere" }, what = { "Lesen", "Schreiben", "Ausführen" };
                for (int w = 0; w < 3; w++)
                {
                    var l = Theme.Lbl(who[w], Theme.F(9.5f, FontStyle.Bold), Theme.Text, Theme.Bg);
                    l.Location = new Point(20, 24 + w * 34);
                    d.Controls.Add(l);
                    for (int k = 0; k < 3; k++)
                    {
                        int bit = 8 - (w * 3 + k);
                        var cb = new CheckBox { Text = what[k], Checked = (mode & (1 << bit)) != 0, Location = new Point(120 + k * 110, 22 + w * 34), AutoSize = true, ForeColor = Theme.Text2, BackColor = Theme.Bg };
                        boxes[w * 3 + k] = cb;
                        d.Controls.Add(cb);
                    }
                }
                var octL = Theme.Lbl("OKTAL", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Bg); octL.Location = new Point(20, 134);
                var oct = new Field(false) { Location = new Point(20, 154), Size = new Size(120, 38) };
                oct.Box.Text = Convert.ToString(mode & 0xFFF, 8).PadLeft(3, '0');
                var ownL = Theme.Lbl("BESITZER (benutzer:gruppe)", Theme.F(8.25f, FontStyle.Bold), Theme.Muted, Theme.Bg); ownL.Location = new Point(160, 134);
                var own = new Field(false) { Location = new Point(160, 154), Size = new Size(280, 38) };
                own.Box.Text = f.Owner + ":" + f.Group;
                var rec = new CheckBox { Text = "Rekursiv (Unterordner und Dateien)", Location = new Point(20, 206), AutoSize = true, ForeColor = Theme.Text2, BackColor = Theme.Bg, Enabled = sel.Any(e => e.IsDir) };
                var smart = new CheckBox { Text = "Rekursiv „intelligent“: Ordner 755 / Dateien 644", Location = new Point(20, 234), AutoSize = true, ForeColor = Theme.Text2, BackColor = Theme.Bg, Enabled = sel.Any(e => e.IsDir) };
                var hint = Theme.Lbl("Tipp: Crafty-Serverdateien müssen dem Crafty-Benutzer gehören (meist nicht root),\nsonst kann der Server sie nicht mehr schreiben.", Theme.F(8.75f), Theme.Muted, Theme.Bg);
                hint.Location = new Point(20, 264);
                bool sync = false;
                Action fromBoxes = () => { if (sync) return; sync = true; int m = 0; for (int i = 0; i < 9; i++) if (boxes[i].Checked) m |= 1 << (8 - i); oct.Box.Text = Convert.ToString((mode & 0xE00) | m, 8).PadLeft(3, '0'); sync = false; };
                foreach (var b in boxes) b.CheckedChanged += (s, e) => fromBoxes();
                oct.Box.TextChanged += (s, e) =>
                {
                    if (sync) return; int m;
                    try { m = Convert.ToInt32(oct.Box.Text.Trim(), 8); } catch { return; }
                    sync = true; for (int i = 0; i < 9; i++) boxes[i].Checked = (m & (1 << (8 - i))) != 0; sync = false;
                };
                var ok = new FlatBtn("Übernehmen", Theme.Accent) { Location = new Point(330, 308), Width = 110, DialogResult = DialogResult.OK };
                var cancel = new FlatBtn("Abbrechen", Theme.CardHi) { Location = new Point(214, 308), Width = 106, DialogResult = DialogResult.Cancel };
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.Controls.AddRange(new Control[] { octL, oct, ownL, own, rec, smart, hint, ok, cancel });
                if (d.ShowDialog(FindForm()) != DialogResult.OK) return;

                var o = oct.Box.Text.Trim();
                var ow = own.Box.Text.Trim();
                if (!System.Text.RegularExpressions.Regex.IsMatch(o, "^[0-7]{3,4}$")) { setStatus("Ungültiger Oktalwert.", Theme.Red); return; }
                if (!System.Text.RegularExpressions.Regex.IsMatch(ow, @"^[a-z0-9_][a-z0-9_.-]{0,31}(:[a-z0-9_][a-z0-9_.-]{0,31})?$|^\d+(:\d+)?$")) { setStatus("Ungültiger Besitzer.", Theme.Red); return; }
                var sb = new StringBuilder();
                bool ownChanged = ow != f.Owner + ":" + f.Group || sel.Count > 1;
                foreach (var e in sel)
                {
                    var p = Ssh.B64(e.Path);
                    if (smart.Checked && e.IsDir) sb.Append("find " + p + " -type d -exec chmod 755 {} + && find " + p + " -type f -exec chmod 644 {} + || exit 1\n");
                    else sb.Append("chmod " + (rec.Checked && e.IsDir ? "-R " : "") + o + " -- " + p + " || exit 1\n");
                    if (ownChanged) sb.Append("chown " + (rec.Checked || smart.Checked ? "-R " : "") + ow + " -- " + p + " || exit 1\n");
                }
                if (await Exec("Rechte setzen", sb.ToString(), 300000)) await Go(cwd, false);
            }
        }

        async Task Pack(string kind)
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            var def = (sel.Count == 1 ? sel[0].Name : System.IO.Path.GetFileName(cwd.TrimEnd('/')) + "_auswahl") + "_" + DateTime.Now.ToString("yyyyMMdd-HHmm") + "." + kind;
            var n = Dialogs.Ask(FindForm(), "Packen", "Name des Archivs in " + cwd + ":", def);
            if (n == null) return;
            n = n.Trim();
            if (!ValidName(n)) return;
            var names = string.Join(" ", sel.Select(e => Ssh.Q(e.Name)));
            string cmd = kind == "zip"
                ? "command -v zip >/dev/null || { echo 'zip ist nicht installiert (apt install zip).' >&2; exit 5; }\nzip -r -q -y \"$A\" " + names
                : "tar -czf \"$A\" -- " + names;
            if (await Exec("Packen: " + n, "cd -- " + Ssh.B64(cwd) + " || exit 3\nA=" + Ssh.B64(n) + "\n" + cmd + " && chown --reference=. -- \"$A\" 2>/dev/null; true", 1800000))
                await Go(cwd, false);
        }

        async Task Unpack(bool intoFolder)
        {
            var sel = Selected();
            if (sel.Count != 1 || sel[0].IsDir || !IsArchive(sel[0].Name)) { setStatus("Bitte genau ein Archiv auswählen (.zip, .tar.gz, .tar …).", Theme.Yellow); return; }
            var e = sel[0];
            var lower = e.Name.ToLowerInvariant();
            string cmd;
            if (lower.EndsWith(".zip"))
                cmd = "if command -v unzip >/dev/null; then unzip -o -q \"$A\" -d \"$T\"; elif command -v python3 >/dev/null; then python3 -m zipfile -e \"$A\" \"$T\"; else echo 'unzip fehlt (apt install unzip).' >&2; exit 5; fi";
            else if (lower.EndsWith(".gz") && !lower.EndsWith(".tar.gz")) cmd = "gunzip -k -f \"$A\"";
            else cmd = "tar -xf \"$A\" -C \"$T\"";
            var folder = intoFolder ? System.Text.RegularExpressions.Regex.Replace(e.Name, @"(\.tar)?\.(zip|gz|tgz|xz|bz2|tar)$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
            var script = "cd -- " + Ssh.B64(cwd) + " || exit 3\nA=" + Ssh.B64(e.Name) + "\nT=" + (intoFolder ? Ssh.B64(folder) + "\nmkdir -p -- \"$T\"" : ".") + "\n" + cmd +
                         " && chown -R --reference=. -- " + (intoFolder ? "\"$T\"" : ".") + " 2>/dev/null; true";
            if (intoFolder && !ValidName(folder)) return;
            if (await Exec("Entpacken: " + e.Name, script, 1800000)) await Go(cwd, false);
        }

        // ───────────────────────────── Transfers ─────────────────────────────
        async Task Upload(string[] localPaths)
        {
            if (!sshReady()) return;
            var target = cwd;
            if (searchMode) { setStatus("Bitte zuerst einen Ordner öffnen (nicht die Suche).", Theme.Yellow); return; }
            var conflicts = localPaths.Select(p => System.IO.Path.GetFileName(p.TrimEnd('\\'))).Where(n => entries.Any(e => e.Name == n)).ToList();
            if (conflicts.Count > 0 && !Dialogs.Confirm(FindForm(), "Überschreiben", conflicts.Count + " Einträge gibt es schon in " + target + ":\n\n" +
                string.Join("\n", conflicts.Take(10)) + (conflicts.Count > 10 ? "\n…" : "") + "\n\nÜberschreiben? (Ordner werden zusammengeführt)")) return;
            foreach (var lp in localPaths)
            {
                var local = lp;
                bool dir = Directory.Exists(local);
                var t = new Transfer { Name = System.IO.Path.GetFileName(local.TrimEnd('\\')) + (dir ? "/" : ""), Upload = true };
                t.Work = tr => Task.Run(() => { if (dir) UploadDir(tr, local, target); else UploadFile(tr, local, target); });
                t.OnDone = async () => { if (cwd == target && !searchMode) await Go(cwd, false); };
                Enqueue(t);
            }
            await Task.FromResult(0);
        }

        void Download()
        {
            var sel = Selected();
            if (sel.Count == 0) return;
            string dest;
            using (var d = new FolderBrowserDialog { Description = sel.Count + " Einträge herunterladen nach …", ShowNewFolderButton = true })
            {
                if (d.ShowDialog(this) != DialogResult.OK) return;
                dest = d.SelectedPath;
            }
            foreach (var e in sel)
            {
                var en = e;
                var t = new Transfer { Name = e.Name + (e.IsDir ? "/" : ""), Upload = false, Total = e.IsDir ? 0 : e.Size };
                t.Work = tr => Task.Run(() => { if (en.IsDir) DownloadDir(tr, en, dest); else DownloadFile(tr, en, dest); });
                t.OnDone = () => { };
                Enqueue(t);
            }
        }

        void Enqueue(Transfer t)
        {
            lock (transfers.Items) transfers.Items.Add(t);
            queue.Enqueue(t);
            RunQueue();
        }

        async void RunQueue()
        {
            if (queueRunning) return;
            queueRunning = true;
            try
            {
                while (queue.Count > 0)
                {
                    var t = queue.Dequeue();
                    if (t.Cancelled) { t.Finished = true; continue; }
                    t.Started = DateTime.Now;
                    t.Status = "läuft";
                    try
                    {
                        await t.Work(t);
                        if (!t.Failed && !t.Cancelled) t.Status = "fertig  ·  " + Theme.Bytes(t.Done) + " in " + Math.Max(1, (int)(DateTime.Now - t.Started).TotalSeconds) + " s";
                    }
                    catch (OperationCanceledException) { t.Cancelled = true; t.Status = "abgebrochen"; }
                    catch (Exception ex) { if (!t.Cancelled) { t.Failed = true; t.Status = "Fehler: " + ex.Message; } }
                    if (t.Cancelled) t.Status = "abgebrochen";
                    t.Finished = true;
                    if (t.Failed) setStatus((t.Upload ? "Upload " : "Download ") + t.Name + ": " + t.Status, Theme.Red);
                    else if (!t.Cancelled) setStatus((t.Upload ? "Hochgeladen: " : "Heruntergeladen: ") + t.Name, Theme.Green);
                    if (t.OnDone != null) t.OnDone();
                }
            }
            finally { queueRunning = false; }
        }

        static void CheckExit(System.Diagnostics.Process p, Transfer tr, string err)
        {
            p.WaitForExit();
            if (tr.Cancelled) throw new OperationCanceledException();
            if (p.ExitCode != 0) throw new Exception((err ?? "").Trim().Length > 0 ? err.Trim().Split('\n').Last() : "Code " + p.ExitCode);
        }

        static Task<string> ReadErr(System.Diagnostics.Process p) { return p.StandardError.ReadToEndAsync(); }

        void UploadFile(Transfer tr, string local, string dir)
        {
            tr.Total = new FileInfo(local).Length;
            var name = System.IO.Path.GetFileName(local);
            var script = "D=" + Ssh.B64(dir) + "\nN=" + Ssh.B64(name) + "\nT=\"$D/.$N.rpupload\"\n" +
                         "cat > \"$T\" || { rm -f -- \"$T\"; exit 1; }\n" +
                         "if [ -e \"$D/$N\" ]; then chmod --reference=\"$D/$N\" -- \"$T\"; chown --reference=\"$D/$N\" -- \"$T\"; else chmod 644 -- \"$T\"; chown --reference=\"$D\" -- \"$T\"; fi 2>/dev/null\n" +
                         "mv -f -- \"$T\" \"$D/$N\"";
            var p = getSsh().Start(script, false);
            tr.Track(p);
            var err = ReadErr(p);
            p.StandardOutput.ReadToEndAsync();
            try
            {
                var st = p.StandardInput.BaseStream;
                var buf = new byte[131072];
                using (var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (tr.Cancelled) throw new OperationCanceledException();
                        st.Write(buf, 0, n);
                        tr.Done += n;
                    }
                }
                p.StandardInput.Close();
            }
            catch (IOException) { if (tr.Cancelled) throw new OperationCanceledException(); }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } throw; }
            CheckExit(p, tr, err.Result);
        }

        void UploadDir(Transfer tr, string local, string dir)
        {
            tr.Total = Tar.Measure(local);
            var top = System.IO.Path.GetFileName(local.TrimEnd('\\'));
            var script = "D=" + Ssh.B64(dir) + "\nN=" + Ssh.B64(top) + "\ncd -- \"$D\" || exit 3\n" +
                         "tar -x -f - --no-same-owner -C \"$D\" || exit 1\nchown -R --reference=\"$D\" -- \"$D/$N\" 2>/dev/null; true";
            var p = getSsh().Start(script, false);
            tr.Track(p);
            var err = ReadErr(p);
            p.StandardOutput.ReadToEndAsync();
            try
            {
                using (var bs = new BufferedStream(p.StandardInput.BaseStream, 131072)) { Tar.Write(bs, local, tr); bs.Flush(); }
                p.StandardInput.Close();
            }
            catch (IOException) { if (tr.Cancelled) throw new OperationCanceledException(); }
            catch (OperationCanceledException) { try { p.Kill(); } catch { } throw; }
            CheckExit(p, tr, err.Result);
        }

        void DownloadFile(Transfer tr, RemoteEntry e, string dest)
        {
            var target = System.IO.Path.Combine(dest, SafeLocal(e.Name));
            var part = target + ".part";
            var p = getSsh().Start("cat -- " + Ssh.B64(e.Path), false);
            tr.Track(p);
            var err = ReadErr(p);
            try
            {
                using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write))
                {
                    var s = p.StandardOutput.BaseStream;
                    var buf = new byte[131072];
                    int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (tr.Cancelled) throw new OperationCanceledException();
                        fs.Write(buf, 0, n);
                        tr.Done += n;
                    }
                }
                CheckExit(p, tr, err.Result);
                if (File.Exists(target)) File.Delete(target);
                File.Move(part, target);
                try { File.SetLastWriteTime(target, e.MTime); } catch { }
            }
            catch
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                try { File.Delete(part); } catch { }
                throw;
            }
        }

        void DownloadDir(Transfer tr, RemoteEntry e, string dest)
        {
            var r = getSsh().Run("du -sb -- " + Ssh.B64(e.Path) + " | cut -f1", 120000).Result;
            long total; long.TryParse(r.Out.Trim(), out total);
            tr.Total = total;
            var p = getSsh().Start("cd -- " + Ssh.B64(ParentDir(e.Path)) + " || exit 3\ntar -c -f - -- " + Ssh.B64(e.Name), false);
            tr.Track(p);
            var err = ReadErr(p);
            try
            {
                using (var bs = new BufferedStream(p.StandardOutput.BaseStream, 131072)) Tar.Extract(bs, dest, tr);
                CheckExit(p, tr, err.Result);
            }
            catch
            {
                try { if (!p.HasExited) p.Kill(); } catch { }
                throw;
            }
        }

        static string SafeLocal(string n) { return new string(n.Select(c => "<>:\"|?*\\/".IndexOf(c) >= 0 || c < 32 ? '_' : c).ToArray()); }

        // ───────────────────────────── Editor ─────────────────────────────
        async Task OpenEditor(RemoteEntry e)
        {
            if (dirty && !Dialogs.Confirm(FindForm(), "Ungespeichert", "Änderungen an " + openPath + " verwerfen?")) return;
            if (e.Size > 5 * 1024 * 1024)
            {
                if (Dialogs.Confirm(FindForm(), "Große Datei", e.Name + " ist " + Theme.Bytes(e.Size) + " groß – zu groß für den Editor.\n\nStattdessen herunterladen?")) { list.SelectedItems.Clear(); Download(); }
                return;
            }
            progress.Set(double.NaN);
            try
            {
                var r = await getSsh().Run("F=" + Ssh.B64(e.Path) + "\nstat -c %Y -- \"$F\" || exit 3\nbase64 -w0 -- \"$F\"", 60000);
                if (!r.Ok) { setStatus("Öffnen fehlgeschlagen: " + r.Message, Theme.Red); return; }
                var parts = r.Out.Split(new[] { '\n' }, 2);
                byte[] bytes;
                try { bytes = Convert.FromBase64String(parts.Length > 1 ? parts[1].Trim() : ""); } catch { setStatus("Datei konnte nicht gelesen werden.", Theme.Red); return; }
                if (bytes.Take(8000).Any(b => b == 0))
                {
                    if (Dialogs.Confirm(FindForm(), "Binärdatei", e.Name + " ist keine Textdatei.\n\nHerunterladen?")) Download();
                    return;
                }
                string text;
                try { text = new UTF8Encoding(false, true).GetString(bytes); openLatin1 = false; }
                catch { text = Encoding.GetEncoding(28591).GetString(bytes); openLatin1 = true; }
                if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
                long.TryParse(parts[0].Trim(), out openMTime);
                openCrlf = text.Contains("\r\n");
                suppress = true;
                editor.Text = openCrlf ? text : text.Replace("\n", "\r\n");
                editor.SelectionStart = 0;
                suppress = false;
                openPath = e.Path;
                dirty = false;
                split.Panel2Collapsed = false;
                try { split.SplitterDistance = Math.Max(Theme.Px(320), split.Width * 40 / 100); } catch { }
                UpdateEditorTitle();
                editor.Focus();
            }
            finally { progress.Set(-1); }
        }

        void UpdateEditorTitle()
        {
            editorTitle.Text = (dirty ? "● " : "") + openPath + (openLatin1 ? "  (Latin-1)" : "") + (openCrlf ? "  CRLF" : "");
            editorTitle.ForeColor = dirty ? Theme.Yellow : Theme.Text2;
        }

        void CloseEditor(bool ask)
        {
            if (ask && dirty && !Dialogs.Confirm(FindForm(), "Ungespeichert", "Änderungen verwerfen?")) return;
            openPath = null; dirty = false;
            suppress = true; editor.Text = ""; suppress = false;
            split.Panel2Collapsed = true;
        }

        async Task Save()
        {
            if (openPath == null) return;
            var text = editor.Text;
            if (!openCrlf) text = text.Replace("\r\n", "\n");
            var bytes = openLatin1 ? Encoding.GetEncoding(28591).GetBytes(text) : new UTF8Encoding(false).GetBytes(text);
            var path = openPath;
            // conflict check
            var chk = await getSsh().Run("stat -c %Y -- " + Ssh.B64(path), 20000);
            long now;
            if (chk.Ok && long.TryParse(chk.Out.Trim(), out now) && now != openMTime &&
                !Dialogs.Confirm(FindForm(), "Geändert", "Die Datei wurde inzwischen auf dem Server geändert.\n\nTrotzdem überschreiben?")) return;
            var dir = ParentDir(path);
            var name = System.IO.Path.GetFileName(path);
            var script = "D=" + Ssh.B64(dir) + "\nN=" + Ssh.B64(name) + "\nT=\"$D/.$N.rpsave\"\ncat > \"$T\" || { rm -f -- \"$T\"; exit 1; }\n" +
                         "[ -e \"$D/$N\" ] && { chmod --reference=\"$D/$N\" -- \"$T\"; chown --reference=\"$D/$N\" -- \"$T\"; } 2>/dev/null\nmv -f -- \"$T\" \"$D/$N\" && stat -c %Y -- \"$D/$N\"";
            progress.Set(double.NaN);
            try
            {
                var res = await Task.Run(() =>
                {
                    var p = getSsh().Start(script, false);
                    var o = p.StandardOutput.ReadToEndAsync();
                    var er = p.StandardError.ReadToEndAsync();
                    p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
                    p.StandardInput.Close();
                    p.WaitForExit();
                    return new SshResult { Exit = p.ExitCode, Out = o.Result, Err = er.Result };
                });
                if (!res.Ok) { setStatus("Speichern fehlgeschlagen: " + res.Message, Theme.Red); return; }
                long.TryParse(res.Out.Trim(), out openMTime);
                dirty = false;
                UpdateEditorTitle();
                setStatus("Gespeichert: " + path + " (" + Theme.Bytes(bytes.Length) + ")", Theme.Green);
            }
            catch (Exception ex) { setStatus("Speichern fehlgeschlagen: " + ex.Message, Theme.Red); }
            finally { progress.Set(-1); }
        }

        string lastFind = "";

        void Find(bool ask)
        {
            if (ask || lastFind.Length == 0)
            {
                var q = Dialogs.Ask(FindForm(), "Suchen", "Text suchen (F3 = weitersuchen):", lastFind);
                if (string.IsNullOrEmpty(q)) return;
                lastFind = q;
            }
            int start = editor.SelectionStart + (editor.SelectionLength > 0 ? 1 : 0);
            int i = editor.Text.IndexOf(lastFind, Math.Min(start, editor.TextLength), StringComparison.OrdinalIgnoreCase);
            if (i < 0) i = editor.Text.IndexOf(lastFind, StringComparison.OrdinalIgnoreCase);
            if (i < 0) { setStatus("„" + lastFind + "“ nicht gefunden.", Theme.Yellow); return; }
            editor.Select(i, lastFind.Length);
            editor.ScrollToCaret();
        }
    }
}