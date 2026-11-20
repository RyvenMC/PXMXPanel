using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    class Transfer
    {
        public string Name, Detail;
        public bool Upload;
        public long Total, Done;
        public string Status = "wartet";
        public bool Finished, Failed, Cancelled;
        public DateTime Started;
        public double Speed;
        long lastDone;
        DateTime lastTick;
        readonly List<Process> procs = new List<Process>();
        public Func<Transfer, Task> Work;
        public Action OnDone;

        public void Track(Process p) { lock (procs) procs.Add(p); }

        public void Cancel()
        {
            if (Finished) return;
            Cancelled = true;
            Status = "abgebrochen";
            lock (procs) foreach (var p in procs) try { if (!p.HasExited) p.Kill(); } catch { }
        }

        public void Tick()
        {
            var now = DateTime.Now;
            if (lastTick == DateTime.MinValue) { lastTick = now; lastDone = Done; return; }
            var dt = (now - lastTick).TotalSeconds;
            if (dt < 0.4) return;
            var inst = (Done - lastDone) / dt;
            Speed = Speed <= 0 ? inst : Speed * 0.6 + inst * 0.4;
            lastTick = now; lastDone = Done;
        }

        public string Eta
        {
            get
            {
                if (Speed < 1 || Total <= 0 || Done >= Total) return "";
                var s = (Total - Done) / Speed;
                if (s > 3600) return ((int)(s / 3600)) + " h " + ((int)(s % 3600 / 60)) + " min";
                if (s > 60) return ((int)(s / 60)) + " min " + ((int)(s % 60)) + " s";
                return ((int)s) + " s";
            }
        }
    }

    /// <summary>Owner drawn list of transfers with progress bars.</summary>
    class TransferPanel : Control
    {
        public readonly List<Transfer> Items = new List<Transfer>();
        readonly System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 250 };
        public event Action<Transfer> CancelClicked;
        const int RowH = 30;
        int scroll;

        public TransferPanel()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Card;
            t.Tick += (s, e) =>
            {
                bool any = false;
                lock (Items) foreach (var i in Items) if (!i.Finished) { i.Tick(); any = true; }
                if (any || Visible) Invalidate();
            };
            t.Start();
        }

        public void ClearFinished() { lock (Items) Items.RemoveAll(i => i.Finished); scroll = 0; Invalidate(); }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            int max; lock (Items) max = Math.Max(0, Items.Count * RowH - Height + 30);
            scroll = Math.Max(0, Math.Min(max, scroll - e.Delta / 3));
            Invalidate();
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            int idx = (e.Y - 28 + scroll) / RowH;
            Transfer it = null;
            lock (Items) if (e.Y >= 28 && idx >= 0 && idx < Items.Count) it = Items[Items.Count - 1 - idx];
            if (it != null && e.X > Width - 40 && !it.Finished && CancelClicked != null) CancelClicked(it);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            List<Transfer> items;
            lock (Items) items = Items.AsEnumerable().Reverse().ToList();
            int active = items.Count(i => !i.Finished);
            var total = items.Where(i => !i.Finished).Sum(i => i.Speed);
            TextRenderer.DrawText(g, "ÜBERTRAGUNGEN" + (active > 0 ? "  ·  " + active + " aktiv  ·  " + Theme.Bytes(total) + "/s" : items.Count > 0 ? "  ·  fertig" : ""),
                Theme.F(8.25f, FontStyle.Bold), new Point(12, 8), Theme.Muted);
            if (items.Count == 0)
            {
                TextRenderer.DrawText(g, "Dateien hierher ziehen oder „Hochladen“ – Ordner werden komplett (mit Unterordnern) übertragen.", Theme.F(9f),
                    new Rectangle(0, 28, Width, Height - 28), Theme.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            g.SetClip(new Rectangle(0, 28, Width, Height - 28));
            int y = 28 - scroll;
            foreach (var it in items)
            {
                if (y > Height) break;
                if (y + RowH >= 28)
                {
                    var col = it.Failed ? Theme.Red : it.Cancelled ? Theme.Muted : it.Finished ? Theme.Green : Theme.SoftGreen;
                    TextRenderer.DrawText(g, it.Upload ? "↑" : "↓", Theme.F(10f, FontStyle.Bold), new Point(12, y + 5), col);
                    TextRenderer.DrawText(g, it.Name, Theme.F(9f, FontStyle.Bold), new Rectangle(30, y + 6, 260, 18), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    int bx = 300, bw = Math.Max(60, Width - bx - 330);
                    var br = new RectangleF(bx, y + 12, bw, 6);
                    using (var p = Theme.RoundRect(br, 3)) using (var b = new SolidBrush(Theme.CardHi)) g.FillPath(b, p);
                    double frac = it.Total > 0 ? Math.Min(1, (double)it.Done / it.Total) : (it.Finished && !it.Failed ? 1 : 0);
                    if (frac > 0.003)
                        using (var p = Theme.RoundRect(new RectangleF(bx, y + 12, (float)(bw * frac), 6), 3))
                        using (var b = new LinearGradientBrush(new RectangleF(bx - 1, y, bw + 2, 10), Theme.SoftGreen, it.Failed ? Theme.Red : Theme.Mint, 0f))
                            g.FillPath(b, p);
                    string info = it.Finished ? it.Status
                        : Theme.Bytes(it.Done) + (it.Total > 0 ? " / " + Theme.Bytes(it.Total) : "") + "  ·  " + Theme.Bytes(it.Speed) + "/s" + (it.Eta.Length > 0 ? "  ·  " + it.Eta : "");
                    TextRenderer.DrawText(g, info, Theme.F(8.75f), new Rectangle(bx + bw + 14, y + 6, 280, 18), it.Failed ? Theme.Red : Theme.Text2, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                    if (!it.Finished) TextRenderer.DrawText(g, "✕", Theme.F(10f, FontStyle.Bold), new Point(Width - 30, y + 5), Theme.Muted);
                }
                y += RowH;
            }
            g.ResetClip();
        }
    }

    /// <summary>Streams a tar archive (ustar + PAX for long/UTF-8 names) of local files – no external tools needed.</summary>
    static class Tar
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public static long Measure(string path)
        {
            if (File.Exists(path)) return new FileInfo(path).Length;
            long sum = 0;
            try { foreach (var f in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) try { sum += new FileInfo(f).Length; } catch { } } catch { }
            return sum;
        }

        public static void Write(Stream output, string localPath, Transfer tr)
        {
            var name = Path.GetFileName(localPath.TrimEnd('\\', '/'));
            WriteEntry(output, localPath, name, tr);
            output.Write(new byte[1024], 0, 1024);
        }

        static void WriteEntry(Stream o, string local, string name, Transfer tr)
        {
            if (tr.Cancelled) throw new OperationCanceledException();
            if (Directory.Exists(local))
            {
                Header(o, name + "/", 0, Directory.GetLastWriteTimeUtc(local), '5', "0755");
                foreach (var d in Directory.GetDirectories(local).OrderBy(x => x)) WriteEntry(o, d, name + "/" + Path.GetFileName(d), tr);
                foreach (var f in Directory.GetFiles(local).OrderBy(x => x)) WriteEntry(o, f, name + "/" + Path.GetFileName(f), tr);
                return;
            }
            var fi = new FileInfo(local);
            Header(o, name, fi.Length, fi.LastWriteTimeUtc, '0', name.EndsWith(".sh") ? "0755" : "0644");
            tr.Detail = name;
            var buf = new byte[65536];
            long left = fi.Length;
            using (var fs = new FileStream(local, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int n;
                while (left > 0 && (n = fs.Read(buf, 0, (int)Math.Min(buf.Length, left))) > 0)
                {
                    if (tr.Cancelled) throw new OperationCanceledException();
                    o.Write(buf, 0, n);
                    left -= n;
                    tr.Done += n;
                }
            }
            if (left > 0) o.Write(new byte[left], 0, (int)left); // file shrank meanwhile – keep archive valid
            int pad = (int)((512 - fi.Length % 512) % 512);
            if (pad > 0) o.Write(new byte[pad], 0, pad);
        }

        static void Header(Stream o, string name, long size, DateTime mtime, char type, string mode)
        {
            var nameBytes = Encoding.UTF8.GetBytes(name);
            bool pax = nameBytes.Length > 99 || nameBytes.Any(b => b > 127);
            if (pax)
            {
                var rec = PaxRecord("path", name);
                RawHeader(o, Encoding.ASCII.GetBytes("PaxHeader/" + Math.Abs(name.GetHashCode())), rec.Length, mtime, 'x', "0644");
                o.Write(rec, 0, rec.Length);
                int pad = (512 - rec.Length % 512) % 512;
                if (pad > 0) o.Write(new byte[pad], 0, pad);
                var shortName = new string(name.Select(c => c < 128 ? c : '_').ToArray());
                nameBytes = Encoding.ASCII.GetBytes(shortName.Length > 99 ? shortName.Substring(shortName.Length - 99) : shortName);
            }
            RawHeader(o, nameBytes, size, mtime, type, mode);
        }

        static byte[] PaxRecord(string key, string value)
        {
            var body = " " + key + "=" + value + "\n";
            int bodyLen = Encoding.UTF8.GetByteCount(body);
            int len = bodyLen + 1;
            while (len.ToString().Length + bodyLen != len) len = len.ToString().Length + bodyLen;
            return Encoding.UTF8.GetBytes(len + body);
        }

        static void RawHeader(Stream o, byte[] name, long size, DateTime mtime, char type, string mode)
        {
            var h = new byte[512];
            Array.Copy(name, h, Math.Min(100, name.Length));
            Oct(h, 100, 8, mode);
            Oct(h, 108, 8, "0");
            Oct(h, 116, 8, "0");
            Oct(h, 124, 12, Convert.ToString(size, 8));
            Oct(h, 136, 12, Convert.ToString((long)Math.Max(0, (mtime - Epoch).TotalSeconds), 8));
            for (int i = 148; i < 156; i++) h[i] = 32;
            h[156] = (byte)type;
            Encoding.ASCII.GetBytes("ustar\0").CopyTo(h, 257);
            h[263] = (byte)'0'; h[264] = (byte)'0';
            Encoding.ASCII.GetBytes("root").CopyTo(h, 265);
            Encoding.ASCII.GetBytes("root").CopyTo(h, 297);
            int sum = h.Sum(b => (int)b);
            var cs = Convert.ToString(sum, 8).PadLeft(6, '0');
            Encoding.ASCII.GetBytes(cs).CopyTo(h, 148);
            h[154] = 0; h[155] = 32;
            o.Write(h, 0, 512);
        }

        static void Oct(byte[] h, int off, int len, string v)
        {
            v = v.PadLeft(len - 1, '0');
            Encoding.ASCII.GetBytes(v).CopyTo(h, off);
            h[off + len - 1] = 0;
        }

        /// <summary>Extracts a tar stream (GNU/ustar/PAX) into a local folder. Rejects paths escaping the target.</summary>
        public static void Extract(Stream input, string dest, Transfer tr)
        {
            var root = Path.GetFullPath(dest);
            var h = new byte[512];
            string longName = null, paxName = null;
            var buf = new byte[65536];
            var mtimes = new List<KeyValuePair<string, DateTime>>();
            while (true)
            {
                if (!ReadFull(input, h, 512)) break;
                if (h.All(b => b == 0)) break;
                string name = Str(h, 0, 100);
                var prefix = Str(h, 345, 155);
                if (prefix.Length > 0 && Str(h, 257, 6).StartsWith("ustar")) name = prefix + "/" + name;
                long size = Num(h, 124, 12);
                char type = (char)h[156];
                long mt = Num(h, 136, 12);
                if (type == 'L' || type == 'x' || type == 'g' || type == 'K')
                {
                    var data = new byte[size];
                    if (!ReadFull(input, data, (int)size)) break;
                    Skip(input, (512 - size % 512) % 512);
                    if (type == 'L') longName = Encoding.UTF8.GetString(data).TrimEnd('\0');
                    else if (type == 'x') paxName = ParsePax(data) ?? paxName;
                    continue;
                }
                if (longName != null) { name = longName; longName = null; }
                if (paxName != null) { name = paxName; paxName = null; }
                var rel = string.Join("\\", name.Replace('\\', '/').Split('/').Where(p => p.Length > 0 && p != ".")
                    .Select(p => new string(p.Select(c => "<>:\"|?*".IndexOf(c) >= 0 || c < 32 ? '_' : c).ToArray()).TrimEnd('.', ' ')));
                var full = Path.GetFullPath(Path.Combine(root, rel));
                bool safe = rel.Length > 0 && !name.Split('/').Contains("..") && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
                if (type == '5')
                {
                    if (safe) { Directory.CreateDirectory(full); mtimes.Add(new KeyValuePair<string, DateTime>(full, Epoch.AddSeconds(mt))); }
                }
                else if ((type == '0' || type == '\0' || type == '7') && safe)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(full));
                    tr.Detail = rel;
                    using (var fs = new FileStream(full, FileMode.Create, FileAccess.Write))
                    {
                        long left = size;
                        while (left > 0)
                        {
                            if (tr.Cancelled) throw new OperationCanceledException();
                            int n = input.Read(buf, 0, (int)Math.Min(buf.Length, left));
                            if (n <= 0) throw new IOException("Archiv unvollständig");
                            fs.Write(buf, 0, n);
                            left -= n;
                            tr.Done += n;
                        }
                    }
                    try { File.SetLastWriteTimeUtc(full, Epoch.AddSeconds(mt)); } catch { }
                    Skip(input, (512 - size % 512) % 512);
                    continue;
                }
                Skip(input, size + (512 - size % 512) % 512); // symlinks, devices, unsafe entries
            }
            foreach (var d in mtimes.AsEnumerable().Reverse()) try { Directory.SetLastWriteTimeUtc(d.Key, d.Value); } catch { }
        }

        static string ParsePax(byte[] data)
        {
            var s = Encoding.UTF8.GetString(data);
            foreach (var line in s.Split('\n'))
            {
                int sp = line.IndexOf(' ');
                if (sp < 0) continue;
                var kv = line.Substring(sp + 1);
                if (kv.StartsWith("path=")) return kv.Substring(5);
            }
            return null;
        }

        static string Str(byte[] h, int off, int len)
        {
            int end = off;
            while (end < off + len && h[end] != 0) end++;
            return Encoding.UTF8.GetString(h, off, end - off);
        }

        static long Num(byte[] h, int off, int len)
        {
            if ((h[off] & 0x80) != 0)
            {
                long v = 0;
                for (int i = off + 1; i < off + len; i++) v = (v << 8) | h[i];
                return v;
            }
            var s = Str(h, off, len).Trim(' ', '\0');
            if (s.Length == 0) return 0;
            try { return Convert.ToInt64(s, 8); } catch { return 0; }
        }

        static bool ReadFull(Stream s, byte[] b, int n)
        {
            int got = 0;
            while (got < n)
            {
                int r = s.Read(b, got, n - got);
                if (r <= 0) return false;
                got += r;
            }
            return true;
        }

        static void Skip(Stream s, long n)
        {
            var b = new byte[8192];
            while (n > 0)
            {
                int r = s.Read(b, 0, (int)Math.Min(b.Length, n));
                if (r <= 0) return;
                n -= r;
            }
        }
    }
}
