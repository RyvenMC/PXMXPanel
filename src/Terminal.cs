using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace RyvenPanel
{
    struct Cell
    {
        public char Ch;
        public int Fg, Bg;   // -1 = default, 0..255 palette, 0x1000000 | rgb = true color
        public byte Attr;    // 1 bold, 2 underline, 4 inverse, 8 dim
    }

    /// <summary>Minimal but solid VT100/xterm screen model (enough for bash, htop, nano, vim, less, mc).</summary>
    class VtScreen
    {
        public int Cols, Rows;
        Cell[][] main, alt, lines;
        public readonly List<Cell[]> Scrollback = new List<Cell[]>();
        public const int MaxScrollback = 5000;
        public int CX, CY;
        bool wrapPending;
        int top, bottom;
        int fg = -1, bg = -1;
        byte attr;
        int sx, sy, sfg = -1, sbg = -1; byte sattr;
        public bool CursorVisible = true, AppCursor, AltActive, AutoWrap = true, BracketedPaste;
        bool g0Gfx, g1Gfx, shifted;
        char lastChar = ' ';
        public Action<string> Reply;
        public Action<string, string> Osc;
        public int Version;

        int state;
        readonly StringBuilder buf = new StringBuilder();
        char prefix;
        string inter = "";
        int designate;

        const string Gfx = "◆▒␉␌␍␊°±␤␋┘┐┌└┼⎺⎻─⎼⎽├┤┴┬│≤≥π≠£·";

        public VtScreen(int cols, int rows)
        {
            Cols = Math.Max(10, cols);
            Rows = Math.Max(3, rows);
            main = NewLines(Rows);
            alt = NewLines(Rows);
            lines = main;
            top = 0; bottom = Rows - 1;
        }

        Cell[][] NewLines(int n) { var a = new Cell[n][]; for (int i = 0; i < n; i++) a[i] = Blank(-1); return a; }

        Cell[] Blank(int b)
        {
            var l = new Cell[Cols];
            for (int i = 0; i < Cols; i++) { l[i].Ch = ' '; l[i].Fg = -1; l[i].Bg = b; }
            return l;
        }

        public Cell[] Line(int y) { return lines[y]; }

        public void Resize(int cols, int rows)
        {
            cols = Math.Max(10, cols); rows = Math.Max(3, rows);
            if (cols == Cols && rows == Rows) return;
            Func<Cell[][], bool, Cell[][]> conv = (src, isMain) =>
            {
                int shift = 0;
                if (isMain && src == lines && CY >= rows) shift = CY - rows + 1;
                if (isMain) for (int i = 0; i < shift; i++) PushScrollback(src[i]);
                var dst = new Cell[rows][];
                for (int y = 0; y < rows; y++)
                {
                    var l = new Cell[cols];
                    for (int x = 0; x < cols; x++) { l[x].Ch = ' '; l[x].Fg = -1; l[x].Bg = -1; }
                    int sy2 = y + shift;
                    if (sy2 < src.Length) Array.Copy(src[sy2], l, Math.Min(cols, src[sy2].Length));
                    dst[y] = l;
                }
                if (src == lines && shift > 0) CY -= shift;
                return dst;
            };
            bool onAlt = AltActive;
            var nm = conv(main, true);
            var na = conv(alt, false);
            Cols = cols; Rows = rows;
            main = nm; alt = na;
            lines = onAlt ? alt : main;
            top = 0; bottom = Rows - 1;
            CX = Math.Min(CX, Cols - 1);
            CY = Math.Max(0, Math.Min(CY, Rows - 1));
            wrapPending = false;
            Version++;
        }

        void PushScrollback(Cell[] l)
        {
            Scrollback.Add((Cell[])l.Clone());
            if (Scrollback.Count > MaxScrollback) Scrollback.RemoveRange(0, Scrollback.Count - MaxScrollback);
        }

        // ───────────── parser ─────────────
        public void Feed(string s)
        {
            foreach (var c in s) Step(c);
            Version++;
        }

        void Step(char c)
        {
            switch (state)
            {
                case 0: Ground(c); return;
                case 1: Escape(c); return;
                case 2:
                    if (c >= 0x40 && c <= 0x7E) { state = 0; Csi(c); }
                    else if (c == '?' || c == '>' || c == '=' || c == '!' || c == '<') { if (buf.Length == 0) prefix = c; }
                    else if (c >= 0x20 && c <= 0x2F) inter += c;
                    else if (c == 0x1B) state = 1;
                    else if (c < 0x20) Control(c);
                    else buf.Append(c);
                    return;
                case 3: // OSC
                    if (c == 0x07) { state = 0; DoOsc(); }
                    else if (c == 0x1B) state = 4;
                    else if (buf.Length < 4096) buf.Append(c);
                    return;
                case 4:
                    state = 0;
                    if (c == '\\') DoOsc();
                    return;
                case 5:
                    state = 0;
                    if (designate == 0) g0Gfx = c == '0'; else if (designate == 1) g1Gfx = c == '0';
                    return;
                case 6: state = 0; return;
                case 7: // DCS / APC / PM: swallow until ST
                    if (c == 0x1B) state = 8; else if (c == 0x07) state = 0;
                    return;
                case 8: state = c == '\\' ? 0 : 7; return;
            }
        }

        void Ground(char c)
        {
            if (c < 0x20 || c == 0x7F) { Control(c); return; }
            Put(c);
        }

        void Control(char c)
        {
            switch (c)
            {
                case '\x1b': state = 1; buf.Clear(); prefix = '\0'; inter = ""; break;
                case '\r': CX = 0; wrapPending = false; break;
                case '\n': case '\x0b': case '\x0c': LineFeed(); break;
                case '\b': if (CX > 0) CX--; wrapPending = false; break;
                case '\t': CX = Math.Min(Cols - 1, (CX / 8 + 1) * 8); wrapPending = false; break;
                case '\x0e': shifted = true; break;
                case '\x0f': shifted = false; break;
            }
        }

        void Escape(char c)
        {
            state = 0;
            switch (c)
            {
                case '[': state = 2; buf.Clear(); prefix = '\0'; inter = ""; break;
                case ']': state = 3; buf.Clear(); break;
                case 'P': case '_': case '^': case 'X': state = 7; break;
                case '(': state = 5; designate = 0; break;
                case ')': state = 5; designate = 1; break;
                case '*': case '+': state = 5; designate = 2; break;
                case '#': case '%': case ' ': state = 6; break;
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': LineFeed(); break;
                case 'E': CX = 0; LineFeed(); break;
                case 'M': ReverseIndex(); break;
                case 'c': Reset(); break;
            }
        }

        void Reset()
        {
            if (AltActive) { lines = main; AltActive = false; }
            for (int y = 0; y < Rows; y++) lines[y] = Blank(-1);
            CX = CY = 0; top = 0; bottom = Rows - 1; fg = bg = -1; attr = 0;
            CursorVisible = true; AppCursor = false; AutoWrap = true; g0Gfx = g1Gfx = shifted = false; wrapPending = false;
        }

        void DoOsc()
        {
            var s = buf.ToString();
            int i = s.IndexOf(';');
            if (i < 0 || Osc == null) return;
            Osc(s.Substring(0, i), s.Substring(i + 1));
        }

        void Put(char c)
        {
            if (char.IsLowSurrogate(c) || c == '﻿' || c == '​') return;
            if (char.IsHighSurrogate(c)) c = '□';
            bool gfx = shifted ? g1Gfx : g0Gfx;
            if (gfx && c >= 0x60 && c <= 0x7E) c = Gfx[c - 0x60];
            if (wrapPending && AutoWrap) { CX = 0; LineFeed(); }
            wrapPending = false;
            var l = lines[CY];
            l[CX].Ch = c; l[CX].Fg = fg; l[CX].Bg = bg; l[CX].Attr = attr;
            lastChar = c;
            if (CX >= Cols - 1) { CX = Cols - 1; wrapPending = true; }
            else CX++;
        }

        void LineFeed()
        {
            wrapPending = false;
            if (CY == bottom) ScrollUp(1);
            else if (CY < Rows - 1) CY++;
        }

        void ReverseIndex()
        {
            wrapPending = false;
            if (CY == top) ScrollDown(1);
            else if (CY > 0) CY--;
        }

        void ScrollUp(int n)
        {
            for (int k = 0; k < n; k++)
            {
                if (top == 0 && !AltActive) PushScrollback(lines[top]);
                for (int y = top; y < bottom; y++) lines[y] = lines[y + 1];
                lines[bottom] = Blank(bg);
            }
        }

        void ScrollDown(int n)
        {
            for (int k = 0; k < n; k++)
            {
                for (int y = bottom; y > top; y--) lines[y] = lines[y - 1];
                lines[top] = Blank(bg);
            }
        }

        void SaveCursor() { sx = CX; sy = CY; sfg = fg; sbg = bg; sattr = attr; }
        void RestoreCursor() { CX = Math.Min(sx, Cols - 1); CY = Math.Min(sy, Rows - 1); fg = sfg; bg = sbg; attr = sattr; wrapPending = false; }

        void EraseCells(int y, int x0, int x1)
        {
            var l = lines[y];
            for (int x = Math.Max(0, x0); x <= Math.Min(Cols - 1, x1); x++) { l[x].Ch = ' '; l[x].Fg = -1; l[x].Bg = bg; l[x].Attr = 0; }
        }

        int[] Params()
        {
            var s = buf.ToString().Replace(':', ';');
            if (s.Length == 0) return new int[0];
            return s.Split(';').Select(p => { int v; return int.TryParse(p, out v) ? v : 0; }).ToArray();
        }

        static int P(int[] p, int i, int def) { return i < p.Length && p[i] > 0 ? p[i] : def; }

        void Csi(char f)
        {
            var p = Params();
            if (inter.Length > 0) return; // cursor style etc.
            if (prefix == '?')
            {
                if (f == 'h' || f == 'l') foreach (var m in p) PrivateMode(m, f == 'h');
                return;
            }
            if (prefix == '>')
            {
                if (f == 'c' && Reply != null) Reply("\x1b[>0;276;0c");
                return;
            }
            if (prefix != '\0') return;
            int n = P(p, 0, 1);
            switch (f)
            {
                case '@':
                    {
                        var l = lines[CY];
                        n = Math.Min(n, Cols - CX);
                        for (int x = Cols - 1; x >= CX + n; x--) l[x] = l[x - n];
                        EraseCells(CY, CX, CX + n - 1);
                        break;
                    }
                case 'A': CY = Math.Max(CY >= top ? top : 0, CY - n); wrapPending = false; break;
                case 'B': case 'e': CY = Math.Min(CY <= bottom ? bottom : Rows - 1, CY + n); wrapPending = false; break;
                case 'C': case 'a': CX = Math.Min(Cols - 1, CX + n); wrapPending = false; break;
                case 'D': CX = Math.Max(0, CX - n); wrapPending = false; break;
                case 'E': CY = Math.Min(bottom, CY + n); CX = 0; wrapPending = false; break;
                case 'F': CY = Math.Max(top, CY - n); CX = 0; wrapPending = false; break;
                case 'G': case '`': CX = Math.Max(0, Math.Min(Cols - 1, n - 1)); wrapPending = false; break;
                case 'd': CY = Math.Max(0, Math.Min(Rows - 1, n - 1)); wrapPending = false; break;
                case 'H': case 'f':
                    CY = Math.Max(0, Math.Min(Rows - 1, P(p, 0, 1) - 1));
                    CX = Math.Max(0, Math.Min(Cols - 1, P(p, 1, 1) - 1));
                    wrapPending = false;
                    break;
                case 'I': for (int i = 0; i < n; i++) CX = Math.Min(Cols - 1, (CX / 8 + 1) * 8); break;
                case 'Z': for (int i = 0; i < n; i++) CX = Math.Max(0, (CX - 1) / 8 * 8); break;
                case 'J':
                    {
                        int m = p.Length > 0 ? p[0] : 0;
                        if (m == 0) { EraseCells(CY, CX, Cols - 1); for (int y = CY + 1; y < Rows; y++) EraseCells(y, 0, Cols - 1); }
                        else if (m == 1) { EraseCells(CY, 0, CX); for (int y = 0; y < CY; y++) EraseCells(y, 0, Cols - 1); }
                        else if (m == 2) { for (int y = 0; y < Rows; y++) EraseCells(y, 0, Cols - 1); }
                        else if (m == 3) Scrollback.Clear();
                        break;
                    }
                case 'K':
                    {
                        int m = p.Length > 0 ? p[0] : 0;
                        if (m == 0) EraseCells(CY, CX, Cols - 1);
                        else if (m == 1) EraseCells(CY, 0, CX);
                        else EraseCells(CY, 0, Cols - 1);
                        break;
                    }
                case 'L':
                    if (CY >= top && CY <= bottom)
                        for (int k = 0; k < n; k++) { for (int y = bottom; y > CY; y--) lines[y] = lines[y - 1]; lines[CY] = Blank(bg); }
                    CX = 0;
                    break;
                case 'M':
                    if (CY >= top && CY <= bottom)
                        for (int k = 0; k < n; k++) { for (int y = CY; y < bottom; y++) lines[y] = lines[y + 1]; lines[bottom] = Blank(bg); }
                    CX = 0;
                    break;
                case 'P':
                    {
                        var l = lines[CY];
                        n = Math.Min(n, Cols - CX);
                        for (int x = CX; x < Cols - n; x++) l[x] = l[x + n];
                        EraseCells(CY, Cols - n, Cols - 1);
                        break;
                    }
                case 'S': { int t = top; bool a = AltActive; ScrollUp(n); break; }
                case 'T': ScrollDown(n); break;
                case 'X': EraseCells(CY, CX, CX + n - 1); break;
                case 'b': for (int i = 0; i < Math.Min(n, 2000); i++) Put(lastChar); break;
                case 'c': if (Reply != null) Reply("\x1b[?1;2c"); break;
                case 'm': Sgr(p); break;
                case 'n':
                    if (Reply != null)
                    {
                        if (P(p, 0, 0) == 5) Reply("\x1b[0n");
                        else if (P(p, 0, 0) == 6) Reply("\x1b[" + (CY + 1) + ";" + (CX + 1) + "R");
                    }
                    break;
                case 'r':
                    {
                        int t = P(p, 0, 1) - 1, b = P(p, 1, Rows) - 1;
                        if (t < b && b < Rows) { top = t; bottom = b; }
                        else { top = 0; bottom = Rows - 1; }
                        CX = 0; CY = 0; wrapPending = false;
                        break;
                    }
                case 's': SaveCursor(); break;
                case 'u': RestoreCursor(); break;
            }
        }

        void PrivateMode(int m, bool on)
        {
            switch (m)
            {
                case 1: AppCursor = on; break;
                case 7: AutoWrap = on; break;
                case 25: CursorVisible = on; break;
                case 2004: BracketedPaste = on; break;
                case 47: case 1047: case 1049:
                    if (on && !AltActive)
                    {
                        if (m == 1049) SaveCursor();
                        alt = NewLines(Rows);
                        lines = alt; AltActive = true;
                        top = 0; bottom = Rows - 1;
                    }
                    else if (!on && AltActive)
                    {
                        lines = main; AltActive = false;
                        top = 0; bottom = Rows - 1;
                        if (m == 1049) RestoreCursor();
                    }
                    break;
            }
        }

        void Sgr(int[] p)
        {
            if (p.Length == 0) p = new[] { 0 };
            for (int i = 0; i < p.Length; i++)
            {
                int v = p[i];
                if (v == 0) { fg = bg = -1; attr = 0; }
                else if (v == 1) attr |= 1;
                else if (v == 2) attr |= 8;
                else if (v == 4) attr |= 2;
                else if (v == 7) attr |= 4;
                else if (v == 22) attr &= unchecked((byte)~(1 | 8));
                else if (v == 24) attr &= unchecked((byte)~2);
                else if (v == 27) attr &= unchecked((byte)~4);
                else if (v >= 30 && v <= 37) fg = v - 30;
                else if (v == 39) fg = -1;
                else if (v >= 40 && v <= 47) bg = v - 40;
                else if (v == 49) bg = -1;
                else if (v >= 90 && v <= 97) fg = v - 90 + 8;
                else if (v >= 100 && v <= 107) bg = v - 100 + 8;
                else if (v == 38 || v == 48)
                {
                    int col = -1;
                    if (i + 2 < p.Length && p[i + 1] == 5) { col = p[i + 2] & 255; i += 2; }
                    else if (i + 4 < p.Length && p[i + 1] == 2) { col = 0x1000000 | ((p[i + 2] & 255) << 16) | ((p[i + 3] & 255) << 8) | (p[i + 4] & 255); i += 4; }
                    if (v == 38) fg = col; else bg = col;
                }
            }
        }
    }

    /// <summary>One interactive ssh session (ssh.exe -tt) feeding a VtScreen.</summary>
    class TerminalSession
    {
        public readonly VtScreen Screen;
        public readonly object Lock = new object();
        public string Title, Tty;
        public bool Alive;
        public event Action Changed, Exited;
        Process p;
        readonly Ssh ssh;
        readonly StringBuilder err = new StringBuilder();
        System.Threading.Timer resizeTimer;
        static readonly Regex TtyRx = new Regex(@"^/dev/pts/\d{1,4}$");

        public TerminalSession(Ssh ssh, string title, int cols, int rows)
        {
            this.ssh = ssh;
            Title = title;
            Screen = new VtScreen(cols, rows);
            Screen.Reply = Send;
            Screen.Osc = (code, text) =>
            {
                if (code == "777" && text.StartsWith("tty;")) { var t = text.Substring(4).Trim(); if (TtyRx.IsMatch(t)) Tty = t; }
            };
        }

        /// <summary>exec = command that replaces the login shell (null = login shell).</summary>
        public void Start(string cwd, string exec)
        {
            var sb = new StringBuilder();
            sb.Append("printf '\\033]777;tty;%s\\007' \"$(tty)\"\n");
            sb.Append("stty rows " + Screen.Rows + " cols " + Screen.Cols + " 2>/dev/null\n");
            sb.Append("export TERM=xterm-256color COLORTERM=truecolor\n");
            if (!string.IsNullOrEmpty(cwd)) sb.Append("cd " + Ssh.B64(cwd) + " 2>/dev/null\n");
            if (!string.IsNullOrEmpty(exec)) sb.Append(exec + "\n");
            else sb.Append("exec \"${SHELL:-/bin/bash}\" -l\n");
            try { p = ssh.Start(sb.ToString(), true); }
            catch (Exception ex)
            {
                lock (Lock) Screen.Feed("\x1b[31m" + ex.Message + "\x1b[0m\r\n");
                if (Changed != null) Changed();
                return;
            }
            Alive = true;
            var proc = p;
            new Thread(() => ReadLoop(proc)) { IsBackground = true, Name = "term-out" }.Start();
            new Thread(() =>
            {
                try
                {
                    var r = proc.StandardError;
                    var b = new char[1024];
                    int n;
                    while ((n = r.Read(b, 0, b.Length)) > 0) lock (err) err.Append(b, 0, n);
                }
                catch { }
            }) { IsBackground = true, Name = "term-err" }.Start();
        }

        void ReadLoop(Process proc)
        {
            var dec = new UTF8Encoding(false).GetDecoder();
            var bytes = new byte[16384];
            var chars = new char[16384 + 4];
            try
            {
                var s = proc.StandardOutput.BaseStream;
                int n;
                while ((n = s.Read(bytes, 0, bytes.Length)) > 0)
                {
                    int c = dec.GetChars(bytes, 0, n, chars, 0);
                    lock (Lock) Screen.Feed(new string(chars, 0, c));
                    var h = Changed; if (h != null) h();
                }
            }
            catch { }
            try { proc.WaitForExit(3000); } catch { }
            Alive = false;
            string e;
            lock (err) e = err.ToString().Trim();
            int code = -1;
            try { code = proc.ExitCode; } catch { }
            lock (Lock)
            {
                Screen.Feed("\r\n\x1b[0m\x1b[90m── Verbindung beendet" + (code > 0 ? " (Code " + code + ")" : "") + " ──\x1b[0m\r\n");
                if (e.Length > 0 && !e.StartsWith("Connection to")) Screen.Feed("\x1b[31m" + e.Replace("\n", "\r\n") + "\x1b[0m\r\n");
            }
            var h2 = Changed; if (h2 != null) h2();
            var x = Exited; if (x != null) x();
        }

        public void Send(string s)
        {
            if (!Alive || p == null) return;
            try
            {
                var b = Encoding.UTF8.GetBytes(s);
                var st = p.StandardInput.BaseStream;
                st.Write(b, 0, b.Length);
                st.Flush();
            }
            catch { }
        }

        public void Resize(int cols, int rows)
        {
            lock (Lock) Screen.Resize(cols, rows);
            if (resizeTimer != null) resizeTimer.Dispose();
            resizeTimer = new System.Threading.Timer(_ =>
            {
                var t = Tty;
                if (!Alive || t == null || !TtyRx.IsMatch(t)) return;
                int c, r;
                lock (Lock) { c = Screen.Cols; r = Screen.Rows; }
                ssh.Run("stty -F " + t + " rows " + r + " cols " + c + " 2>/dev/null; pkill -WINCH -t " + t.Substring(5) + " 2>/dev/null; true", 15000);
            }, null, 350, Timeout.Infinite);
        }

        public void Close()
        {
            Alive = false;
            try { if (p != null && !p.HasExited) p.Kill(); } catch { }
        }
    }

    /// <summary>Renders a VtScreen and turns keyboard/mouse input into terminal input.</summary>
    class TerminalControl : Control
    {
        TerminalSession session;
        Font font, bold;
        int cw, ch;
        int viewOffset;
        bool selecting;
        Point selA, selB;   // X = column, Y = absolute line index
        bool hasSel;
        bool cursorOn = true;
        int lastVersion = -1;
        readonly System.Windows.Forms.Timer repaint = new System.Windows.Forms.Timer { Interval = 16 };
        readonly System.Windows.Forms.Timer blink = new System.Windows.Forms.Timer { Interval = 530 };
        volatile bool dirtyFlag;
        public float FontSize = 10.5f;
        public static readonly Color Back = Theme.Hex("#0c0e13");

        static readonly Color[] Base16 =
        {
            Theme.Hex("#1b1f28"), Theme.Hex("#ff5c6c"), Theme.Hex("#3ddc84"), Theme.Hex("#ffc857"),
            Theme.Hex("#6c9cff"), Theme.Hex("#d966ff"), Theme.Hex("#55e0ff"), Theme.Hex("#c3c8d4"),
            Theme.Hex("#5a6072"), Theme.Hex("#ff8a96"), Theme.Hex("#7af0ad"), Theme.Hex("#ffdd8f"),
            Theme.Hex("#9dbcff"), Theme.Hex("#e79bff"), Theme.Hex("#93ecff"), Theme.Hex("#ffffff")
        };

        public TerminalControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Back;
            Cursor = Cursors.IBeam;
            SetFont(FontSize);
            repaint.Tick += (s, e) => { if (dirtyFlag) { dirtyFlag = false; Invalidate(); } };
            repaint.Start();
            blink.Tick += (s, e) => { cursorOn = !cursorOn; if (Focused) Invalidate(CursorRect()); };
            blink.Start();
        }

        public void SetFont(float size)
        {
            FontSize = Math.Max(7f, Math.Min(24f, size));
            string fam = FontFamily.Families.Any(f => f.Name == "Cascadia Mono") ? "Cascadia Mono" : "Consolas";
            font = new Font(fam, FontSize);
            bold = new Font(fam, FontSize, FontStyle.Bold);
            var sz = TextRenderer.MeasureText("WWWWWWWWWWWWWWWWWWWW", font, new Size(9999, 999), TextFormatFlags.NoPadding);
            cw = Math.Max(4, (int)Math.Round(sz.Width / 20.0));
            ch = Math.Max(8, sz.Height);
            OnResize(EventArgs.Empty);
            Invalidate();
        }

        public TerminalSession Session
        {
            get { return session; }
            set
            {
                if (session != null) session.Changed -= OnChanged;
                session = value;
                viewOffset = 0; hasSel = false;
                if (session != null)
                {
                    session.Changed += OnChanged;
                    var gs = GridSize();
                    session.Resize(gs.Width, gs.Height);
                }
                Invalidate();
            }
        }

        void OnChanged() { dirtyFlag = true; }

        Size GridSize() { return new Size(Math.Max(10, (ClientSize.Width - 16) / cw), Math.Max(3, (ClientSize.Height - 12) / ch)); }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (session == null || cw == 0 || ClientSize.Width < 50 || ClientSize.Height < 30) return;
            var gs = GridSize();
            if (gs.Width != session.Screen.Cols || gs.Height != session.Screen.Rows) session.Resize(gs.Width, gs.Height);
        }

        Rectangle CursorRect()
        {
            if (session == null) return Rectangle.Empty;
            return new Rectangle(8 + session.Screen.CX * cw, 6 + (session.Screen.CY + viewOffset) * ch, cw, ch);
        }

        Color Pal(int c, bool isFg, byte attr)
        {
            if (c < 0) return isFg ? Theme.Hex("#d6dae4") : Back;
            if ((c & 0x1000000) != 0) return Color.FromArgb((c >> 16) & 255, (c >> 8) & 255, c & 255);
            if (c < 16) { if (isFg && (attr & 1) != 0 && c < 8) c += 8; return Base16[c]; }
            if (c < 232)
            {
                c -= 16;
                int[] lv = { 0, 95, 135, 175, 215, 255 };
                return Color.FromArgb(lv[c / 36], lv[(c / 6) % 6], lv[c % 6]);
            }
            int g = 8 + (c - 232) * 10;
            return Color.FromArgb(g, g, g);
        }

        Cell[] VisibleLine(VtScreen s, int row, out int abs)
        {
            int sb = s.AltActive ? 0 : s.Scrollback.Count;
            abs = sb - viewOffset + row;
            if (s.AltActive) return s.Line(row);
            if (abs < sb) return abs >= 0 ? s.Scrollback[abs] : null;
            return s.Line(abs - sb);
        }

        bool InSel(int abs, int x)
        {
            if (!hasSel) return false;
            Point a = selA, b = selB;
            if (b.Y < a.Y || (b.Y == a.Y && b.X < a.X)) { var t = a; a = b; b = t; }
            if (abs < a.Y || abs > b.Y) return false;
            if (abs == a.Y && x < a.X) return false;
            if (abs == b.Y && x > b.X) return false;
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Back);
            if (session == null)
            {
                TextRenderer.DrawText(g, "Kein Terminal geöffnet – „+ Neues Terminal“ klicken.", Theme.F(10f), ClientRectangle, Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                return;
            }
            const TextFormatFlags tf = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.PreserveGraphicsClipping;
            lock (session.Lock)
            {
                var s = session.Screen;
                lastVersion = s.Version;
                if (viewOffset > s.Scrollback.Count) viewOffset = s.Scrollback.Count;
                if (s.AltActive) viewOffset = 0;
                var run = new StringBuilder();
                for (int row = 0; row < s.Rows; row++)
                {
                    int y = 6 + row * ch;
                    if (y > e.ClipRectangle.Bottom || y + ch < e.ClipRectangle.Top) continue;
                    int abs;
                    var line = VisibleLine(s, row, out abs);
                    if (line == null) continue;
                    int x = 0;
                    while (x < s.Cols)
                    {
                        Cell c0 = x < line.Length ? line[x] : new Cell { Ch = ' ', Fg = -1, Bg = -1 };
                        bool sel0 = InSel(abs, x);
                        int start = x;
                        run.Clear();
                        while (x < s.Cols)
                        {
                            Cell c = x < line.Length ? line[x] : new Cell { Ch = ' ', Fg = -1, Bg = -1 };
                            if (c.Fg != c0.Fg || c.Bg != c0.Bg || c.Attr != c0.Attr || InSel(abs, x) != sel0) break;
                            if (c.Ch >= 0x2500 && run.Length > 0) break;
                            run.Append(c.Ch == '\0' ? ' ' : c.Ch);
                            x++;
                            if (c.Ch >= 0x2500) break;
                        }
                        var fgc = Pal(c0.Fg, true, c0.Attr);
                        var bgc = Pal(c0.Bg, false, c0.Attr);
                        if ((c0.Attr & 4) != 0) { var t = fgc; fgc = bgc; bgc = t; }
                        if ((c0.Attr & 8) != 0) fgc = Theme.Mix(fgc, bgc, 0.4);
                        if (sel0) { bgc = Theme.Mix(Theme.Accent, Back, 0.35); fgc = Color.White; }
                        var rect = new Rectangle(8 + start * cw, y, (x - start) * cw, ch);
                        if (bgc != Back) using (var b = new SolidBrush(bgc)) g.FillRectangle(b, rect);
                        var text = run.ToString();
                        if (text.Trim().Length > 0)
                            TextRenderer.DrawText(g, text, (c0.Attr & 1) != 0 ? bold : font, new Point(rect.X, rect.Y), fgc, bgc, tf);
                        if ((c0.Attr & 2) != 0) using (var pen = new Pen(fgc)) g.DrawLine(pen, rect.X, rect.Bottom - 2, rect.Right, rect.Bottom - 2);
                    }
                }
                // cursor
                if (s.CursorVisible && viewOffset == 0 && session.Alive)
                {
                    var r = new Rectangle(8 + s.CX * cw, 6 + s.CY * ch, cw, ch);
                    if (Focused && cursorOn)
                    {
                        using (var b = new SolidBrush(Theme.Alpha(Theme.SoftGreen, 200))) g.FillRectangle(b, r);
                        var c = s.Line(s.CY)[Math.Min(s.CX, s.Cols - 1)];
                        if (c.Ch != ' ' && c.Ch != '\0') TextRenderer.DrawText(g, c.Ch.ToString(), font, r.Location, Back, tf);
                    }
                    else if (!Focused) using (var pen = new Pen(Theme.Alpha(Theme.SoftGreen, 160))) g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                }
                // scrollbar
                int total = (s.AltActive ? 0 : s.Scrollback.Count) + s.Rows;
                if (total > s.Rows)
                {
                    float h = Math.Max(24, (float)ClientSize.Height * s.Rows / total);
                    float pos = (float)(total - s.Rows - viewOffset) / (total - s.Rows);
                    float yy = (ClientSize.Height - h) * pos;
                    using (var b = new SolidBrush(Theme.Alpha(Color.White, viewOffset > 0 ? 70 : 25)))
                        g.FillRectangle(b, ClientSize.Width - 5, yy, 3, h);
                }
                if (viewOffset > 0)
                {
                    var tip = "↑ " + viewOffset + " Zeilen zurück – Taste drücken oder scrollen, um zurückzukehren";
                    var sz = TextRenderer.MeasureText(tip, Theme.F(8.5f));
                    var r = new Rectangle(ClientSize.Width - sz.Width - 28, 6, sz.Width + 16, sz.Height + 6);
                    using (var b = new SolidBrush(Theme.Alpha(Theme.Accent, 200))) g.FillRectangle(b, r);
                    TextRenderer.DrawText(g, tip, Theme.F(8.5f), new Point(r.X + 8, r.Y + 3), Color.White);
                }
            }
        }

        // ───────────── input ─────────────
        protected override bool IsInputKey(Keys k)
        {
            var kc = k & Keys.KeyCode;
            if (kc == Keys.Tab || kc == Keys.Up || kc == Keys.Down || kc == Keys.Left || kc == Keys.Right ||
                kc == Keys.Home || kc == Keys.End || kc == Keys.PageUp || kc == Keys.PageDown || kc == Keys.Escape || kc == Keys.Enter)
                return true;
            return base.IsInputKey(k);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            // keep Alt+Fx / F10 etc. inside the terminal instead of the form menu logic
            if ((keyData & Keys.KeyCode) == Keys.F10 || (keyData & Keys.KeyCode) == Keys.Tab) { OnKeyDown(new KeyEventArgs(keyData)); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Send(string s)
        {
            if (session == null) return;
            if (viewOffset != 0) { viewOffset = 0; Invalidate(); }
            hasSel = false;
            cursorOn = true;
            session.Send(s);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (session == null) return;
            string seq = null;
            bool app = session.Screen.AppCursor;
            int mod = 1 + (e.Shift ? 1 : 0) + (e.Alt ? 2 : 0) + (e.Control ? 4 : 0);
            Func<string, string, string> cur = (a, x) => mod > 1 ? "\x1b[1;" + mod + x : (app ? "\x1bO" + x : "\x1b[" + x);
            Func<int, string> tilde = n => mod > 1 ? "\x1b[" + n + ";" + mod + "~" : "\x1b[" + n + "~";
            if (e.Control && e.Shift && e.KeyCode == Keys.C) { CopySelection(); e.SuppressKeyPress = true; return; }
            if ((e.Control && e.Shift && e.KeyCode == Keys.V) || (e.Shift && e.KeyCode == Keys.Insert)) { Paste(); e.SuppressKeyPress = true; return; }
            if (e.Shift && e.KeyCode == Keys.PageUp) { Scroll(session.Screen.Rows - 1); e.SuppressKeyPress = true; return; }
            if (e.Shift && e.KeyCode == Keys.PageDown) { Scroll(-(session.Screen.Rows - 1)); e.SuppressKeyPress = true; return; }
            if (e.Control && (e.KeyCode == Keys.Oemplus || e.KeyCode == Keys.Add)) { SetFont(FontSize + 1); e.SuppressKeyPress = true; return; }
            if (e.Control && (e.KeyCode == Keys.OemMinus || e.KeyCode == Keys.Subtract)) { SetFont(FontSize - 1); e.SuppressKeyPress = true; return; }
            switch (e.KeyCode)
            {
                case Keys.Up: seq = cur("", "A"); break;
                case Keys.Down: seq = cur("", "B"); break;
                case Keys.Right: seq = cur("", "C"); break;
                case Keys.Left: seq = cur("", "D"); break;
                case Keys.Home: seq = cur("", "H"); break;
                case Keys.End: seq = cur("", "F"); break;
                case Keys.PageUp: seq = tilde(5); break;
                case Keys.PageDown: seq = tilde(6); break;
                case Keys.Insert: seq = tilde(2); break;
                case Keys.Delete: seq = tilde(3); break;
                case Keys.Back: seq = e.Control ? "\x08" : (e.Alt ? "\x1b\x7f" : "\x7f"); break;
                case Keys.Tab: seq = e.Shift ? "\x1b[Z" : "\t"; break;
                case Keys.Enter: seq = e.Alt ? "\x1b\r" : "\r"; break;
                case Keys.Escape: seq = "\x1b"; break;
                case Keys.F1: seq = "\x1bOP"; break;
                case Keys.F2: seq = "\x1bOQ"; break;
                case Keys.F3: seq = "\x1bOR"; break;
                case Keys.F4: seq = "\x1bOS"; break;
                case Keys.F5: seq = tilde(15); break;
                case Keys.F6: seq = tilde(17); break;
                case Keys.F7: seq = tilde(18); break;
                case Keys.F8: seq = tilde(19); break;
                case Keys.F9: seq = tilde(20); break;
                case Keys.F10: seq = tilde(21); break;
                case Keys.F11: seq = tilde(23); break;
                case Keys.F12: seq = tilde(24); break;
                case Keys.Space: if (e.Control) seq = "\0"; break;
            }
            if (seq == null && e.Alt && !e.Control && e.KeyCode >= Keys.A && e.KeyCode <= Keys.Z)
                seq = "\x1b" + (char)((e.Shift ? 'A' : 'a') + (e.KeyCode - Keys.A));
            if (seq == null && e.Control && !e.Alt)
            {
                if (e.KeyCode == Keys.OemOpenBrackets) seq = "\x1b";
                else if (e.KeyCode == Keys.Oem5 || e.KeyCode == Keys.OemBackslash) seq = "\x1c";
                else if (e.KeyCode == Keys.OemCloseBrackets) seq = "\x1d";
            }
            if (seq != null) { Send(seq); e.SuppressKeyPress = true; e.Handled = true; }
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (session == null || e.Handled) return;
            var c = e.KeyChar;
            if (c == '\b' || c == '\r' || c == '\t' || c == '\x1b') return; // handled in KeyDown
            Send(c.ToString());
            e.Handled = true;
        }

        public void Paste()
        {
            if (session == null || !Clipboard.ContainsText()) return;
            var t = Clipboard.GetText().Replace("\r\n", "\r").Replace("\n", "\r");
            if (session.Screen.BracketedPaste) t = "\x1b[200~" + t + "\x1b[201~";
            Send(t);
        }

        public string SelectionText()
        {
            if (!hasSel || session == null) return null;
            Point a = selA, b = selB;
            if (b.Y < a.Y || (b.Y == a.Y && b.X < a.X)) { var t = a; a = b; b = t; }
            var sb = new StringBuilder();
            lock (session.Lock)
            {
                var s = session.Screen;
                int sbc = s.AltActive ? 0 : s.Scrollback.Count;
                for (int y = a.Y; y <= b.Y; y++)
                {
                    Cell[] line = s.AltActive ? (y >= 0 && y < s.Rows ? s.Line(y) : null) : (y < sbc ? (y >= 0 ? s.Scrollback[y] : null) : (y - sbc < s.Rows ? s.Line(y - sbc) : null));
                    if (line == null) continue;
                    int x0 = y == a.Y ? a.X : 0, x1 = y == b.Y ? b.X : line.Length - 1;
                    var l = new StringBuilder();
                    for (int x = Math.Max(0, x0); x <= Math.Min(line.Length - 1, x1); x++) l.Append(line[x].Ch == '\0' ? ' ' : line[x].Ch);
                    sb.Append(l.ToString().TrimEnd());
                    if (y != b.Y) sb.Append("\r\n");
                }
            }
            return sb.ToString();
        }

        public void CopySelection()
        {
            var t = SelectionText();
            if (!string.IsNullOrEmpty(t)) try { Clipboard.SetText(t); } catch { }
        }

        void Scroll(int lines)
        {
            if (session == null) return;
            lock (session.Lock)
            {
                if (session.Screen.AltActive) return;
                viewOffset = Math.Max(0, Math.Min(session.Screen.Scrollback.Count, viewOffset + lines));
            }
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (session != null && session.Screen.AltActive)
            {
                // in full-screen apps (less, htop, vim) the wheel sends arrow keys
                var k = e.Delta > 0 ? (session.Screen.AppCursor ? "\x1bOA" : "\x1b[A") : (session.Screen.AppCursor ? "\x1bOB" : "\x1b[B");
                for (int i = 0; i < 3; i++) session.Send(k);
                return;
            }
            Scroll(e.Delta / 40);
        }

        Point CellAt(Point p)
        {
            var s = session.Screen;
            int col = Math.Max(0, Math.Min(s.Cols - 1, (p.X - 8) / cw));
            int row = Math.Max(0, Math.Min(s.Rows - 1, (p.Y - 6) / ch));
            int abs = (s.AltActive ? 0 : s.Scrollback.Count) - viewOffset + row;
            return new Point(col, abs);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (session == null) return;
            if (e.Button == MouseButtons.Right)
            {
                if (hasSel) { CopySelection(); hasSel = false; Invalidate(); }
                else Paste();
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            if (e.Clicks == 2) { SelectWord(CellAt(e.Location)); return; }
            selA = selB = CellAt(e.Location);
            selecting = true;
            hasSel = false;
            Invalidate();
        }

        void SelectWord(Point c)
        {
            lock (session.Lock)
            {
                var s = session.Screen;
                int sbc = s.AltActive ? 0 : s.Scrollback.Count;
                var line = c.Y < sbc ? (c.Y >= 0 ? s.Scrollback[c.Y] : null) : (c.Y - sbc < s.Rows ? s.Line(c.Y - sbc) : null);
                if (line == null) return;
                Func<int, bool> w = x => x >= 0 && x < line.Length && line[x].Ch != ' ' && line[x].Ch != '\0' && "\"'()[]{}<>,;|".IndexOf(line[x].Ch) < 0;
                if (!w(c.X)) return;
                int a = c.X, b = c.X;
                while (w(a - 1)) a--;
                while (w(b + 1)) b++;
                selA = new Point(a, c.Y); selB = new Point(b, c.Y);
                hasSel = true;
            }
            CopySelection();
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!selecting || session == null) return;
            if (e.Y < 0) Scroll(1);
            else if (e.Y > ClientSize.Height) Scroll(-1);
            selB = CellAt(e.Location);
            hasSel = selA != selB;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!selecting) return;
            selecting = false;
            if (hasSel) CopySelection();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); cursorOn = true; Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    }

    /// <summary>The "Terminal" mode: several ssh sessions in tabs plus quick actions.</summary>
    class TerminalView : Panel
    {
        readonly Func<Ssh> getSsh;
        readonly Func<bool> sshReady;
        readonly Action<string, Color> setStatus;
        readonly List<TerminalSession> sessions = new List<TerminalSession>();
        TerminalSession current;
        readonly TerminalControl term;
        readonly FlowLayoutPanel tabStrip;
        Label hSub;
        int counter;

        public TerminalView(Func<Ssh> getSsh, Func<bool> sshReady, Action<string, Color> setStatus)
        {
            this.getSsh = getSsh;
            this.sshReady = sshReady;
            this.setStatus = setStatus;
            Dock = DockStyle.Fill;
            BackColor = Theme.Bg;

            TabBtn[] none;
            var header = Ui.Header("Terminal", out hSub, null, out none, null);
            hSub.Text = "Echtes SSH-Terminal (xterm-256color) – htop, nano, vim, Docker-Shells. Markieren = kopieren, Rechtsklick = einfügen.";

            var bNew = Ui.Btn("+ Neues Terminal", Theme.Accent);
            var bCont = Ui.Btn("Container-Shell ▾", Theme.CardHi);
            var bQuick = Ui.Btn("Schnellbefehle ▾", Theme.CardHi);
            var bCopy = Ui.Btn("Kopieren", Theme.CardHi);
            var bPaste = Ui.Btn("Einfügen", Theme.CardHi);
            var bMinus = Ui.Btn("A−", Theme.CardHi); bMinus.Width = 44;
            var bPlus = Ui.Btn("A+", Theme.CardHi); bPlus.Width = 44;
            var bReconnect = Ui.Btn("↻ Neu verbinden", Theme.CardHi);
            var bar = Ui.Bar(50, bNew, bCont, bQuick, bCopy, bPaste, bMinus, bPlus, bReconnect);
            bar.Padding = new Padding(0, 8, 0, 0);

            tabStrip = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, BackColor = Theme.Bg, WrapContents = false, AutoScroll = false, Padding = new Padding(0, 4, 0, 0) };

            var box = new RoundPanel { Dock = DockStyle.Fill, Fill = TerminalControl.Back, BorderColor = Theme.Border, Padding = new Padding(6, 8, 4, 8) };
            term = new TerminalControl { Dock = DockStyle.Fill };
            box.Controls.Add(term);

            Controls.Add(box);
            Controls.Add(tabStrip);
            Controls.Add(bar);
            Controls.Add(header);

            bNew.Click += (s, e) => Open(null, null, null);
            bCopy.Click += (s, e) => { term.CopySelection(); term.Focus(); };
            bPaste.Click += (s, e) => { term.Paste(); term.Focus(); };
            bMinus.Click += (s, e) => { term.SetFont(term.FontSize - 1); term.Focus(); };
            bPlus.Click += (s, e) => { term.SetFont(term.FontSize + 1); term.Focus(); };
            bReconnect.Click += (s, e) =>
            {
                if (current == null) { Open(null, null, null); return; }
                var old = current;
                old.Close();
                var idx = sessions.IndexOf(old);
                var ns = Create(old.Title, null, null);
                sessions[idx] = ns;
                Select(ns);
            };
            bQuick.Click += (s, e) =>
            {
                var m = Ui.Menu();
                foreach (var q in new[]
                {
                    new[] { "htop / top – Prozesse", "command -v htop >/dev/null && htop || top" },
                    new[] { "Docker-Container", "docker ps --format 'table {{.Names}}\\t{{.Status}}\\t{{.Ports}}'" },
                    new[] { "Docker-Statistik live", "docker stats" },
                    new[] { "Speicherplatz", "df -h -x tmpfs -x overlay" },
                    new[] { "Arbeitsspeicher", "free -h" },
                    new[] { "Größte Ordner in Crafty", "du -h --max-depth=2 /opt/crafty/docker 2>/dev/null | sort -rh | head -25" },
                    new[] { "Offene Ports", "ss -tulpn" },
                    new[] { "Letzte Logins", "last -n 20" },
                    new[] { "System-Log live", "journalctl -f -n 100" },
                    new[] { "Crafty-Log live", "docker logs -f --tail 200 crafty_container" },
                })
                {
                    var cmd = q[1];
                    m.Item(q[0], () => { if (current == null || !current.Alive) Open(null, null, null); current.Send(cmd + "\r"); term.Focus(); });
                }
                Ui.DropDown(bQuick, m);
            };
            bCont.Click += async (s, e) =>
            {
                if (!sshReady()) { setStatus("SSH ist noch nicht eingerichtet (Bereich „Docker“).", Theme.Yellow); return; }
                var r = await getSsh().Run("docker ps --format '{{.Names}}'", 20000);
                var m = Ui.Menu();
                if (!r.Ok) m.Item("Fehler: " + r.Message, () => { }, false);
                foreach (var n in r.Out.Split('\n').Select(x => x.Trim()).Where(Ssh.IsName))
                {
                    var name = n;
                    m.Item(name, () => Open(name, null,
                        "exec docker exec -it -e TERM=xterm-256color " + Ssh.Q(name) + " sh -c 'if command -v bash >/dev/null 2>&1; then exec bash; else exec sh; fi'"));
                }
                Ui.DropDown(bCont, m);
            };
        }

        public void Activate()
        {
            if (!sshReady())
            {
                hSub.Text = "Für das Terminal braucht die App die SSH-Verbindung – richte sie einmal unter „Docker“ ein.";
                hSub.ForeColor = Theme.Yellow;
                return;
            }
            hSub.ForeColor = Theme.Muted;
            if (sessions.Count == 0) Open(null, null, null);
            term.Focus();
        }

        public void Deactivate() { }

        /// <summary>Opens a new tab. cwd = start folder, exec = command instead of a login shell.</summary>
        public void Open(string title, string cwd, string exec)
        {
            if (!sshReady()) { setStatus("SSH ist noch nicht eingerichtet (Bereich „Docker“).", Theme.Yellow); return; }
            var s = Create(title ?? ("Shell " + (++counter)), cwd, exec);
            sessions.Add(s);
            Select(s);
        }

        TerminalSession Create(string title, string cwd, string exec)
        {
            var ssh = getSsh();
            var s = new TerminalSession(ssh, title, 120, 32);
            s.Exited += () => { try { BeginInvoke(new Action(RenderTabs)); } catch { } };
            s.Start(cwd, exec);
            return s;
        }

        void Select(TerminalSession s)
        {
            current = s;
            term.Session = s;
            RenderTabs();
            term.Focus();
        }

        void CloseTab(TerminalSession s)
        {
            if (s.Alive && !Dialogs.Confirm(FindForm(), "Terminal schließen", "„" + s.Title + "“ beenden? Laufende Programme werden abgebrochen.")) return;
            s.Close();
            int i = sessions.IndexOf(s);
            sessions.Remove(s);
            if (current == s)
            {
                current = sessions.Count > 0 ? sessions[Math.Max(0, Math.Min(i, sessions.Count - 1))] : null;
                term.Session = current;
            }
            RenderTabs();
        }

        void RenderTabs()
        {
            tabStrip.SuspendLayout();
            foreach (Control c in tabStrip.Controls.Cast<Control>().ToList()) c.Dispose();
            tabStrip.Controls.Clear();
            foreach (var s in sessions)
            {
                var ss = s;
                var t = new FlatBtn((s.Alive ? "● " : "○ ") + s.Title, s == current ? Theme.CardHi : Theme.Side) { Height = 32, Margin = new Padding(0, 0, 2, 0) };
                t.Font = Theme.F(9f, FontStyle.Bold);
                t.ForeColor = s.Alive ? Color.White : Theme.Muted;
                t.Click += (a, b) => Select(ss);
                t.MouseUp += (a, b) => { if (b.Button == MouseButtons.Middle) CloseTab(ss); };
                var x = new FlatBtn("×", s == current ? Theme.CardHi : Theme.Side) { Height = 32, Width = 30, Margin = new Padding(0, 0, 10, 0) };
                x.Click += (a, b) => CloseTab(ss);
                tabStrip.Controls.Add(t);
                tabStrip.Controls.Add(x);
                DarkForm.ScaleLate(t);
                DarkForm.ScaleLate(x);
            }
            tabStrip.ResumeLayout();
        }

        public void CloseAll() { foreach (var s in sessions) s.Close(); }
    }
}
