using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace Qural
{
    // ───────────── настройки «Скриншотов» (shot.json) ─────────────
    [DataContract]
    class ShotConfig
    {
        [DataMember] public int Hotkey;          // выделить область
        [DataMember] public int FullHotkey;      // весь монитор под курсором — сразу в файл и буфер
        [DataMember] public int WindowHotkey;    // окно в фокусе — сразу в файл и буфер
        [DataMember] public string Folder;       // пусто — «Изображения\Qural»
        [DataMember] public string LastDir;      // куда сохраняли через «Сохранить как» в прошлый раз
        [DataMember] public string Format;       // png | jpg
        [DataMember] public int JpegQuality;
        [DataMember] public bool Cursor;         // рисовать курсор на снимке
        [DataMember] public bool KeepSelection;  // открывать с прошлой рамкой
        [DataMember] public bool AskWhere;       // «Сохранить» спрашивает имя и папку
        [DataMember] public bool Notify;         // уведомление после копирования и сохранения
        [DataMember] public int LastX, LastY, LastW, LastH; // прошлая рамка, в координатах экрана
        [DataMember] public int Color;           // цвет рисования (ARGB)
        [DataMember] public int Width;           // толщина 1–30

        public ShotConfig() { Defaults(); }
        [OnDeserializing] void OnLoad(StreamingContext c) { Defaults(); }

        void Defaults()
        {
            Hotkey = (int)Keys.PrintScreen;
            FullHotkey = (int)(Keys.Shift | Keys.PrintScreen);
            WindowHotkey = (int)(Keys.Control | Keys.PrintScreen);
            Folder = ""; LastDir = "";
            Format = "png"; JpegQuality = 90;
            Cursor = false; KeepSelection = false; AskWhere = true; Notify = true;
            Color = unchecked((int)0xFFFF3B30); Width = 3;
        }

        public string FolderPath
        {
            get
            {
                return string.IsNullOrEmpty(Folder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Qural")
                    : Folder;
            }
        }

        public static ShotConfig Load()
        {
            var c = Store.Load<ShotConfig>("shot.json", null) ?? new ShotConfig();
            if (c.Format != "jpg") c.Format = "png";
            if (c.JpegQuality < 10 || c.JpegQuality > 100) c.JpegQuality = 90;
            if (c.Width < 1 || c.Width > 30) c.Width = 3;
            if (c.Folder == null) c.Folder = "";
            if (c.LastDir == null) c.LastDir = "";
            if ((c.Color >> 24 & 0xFF) == 0) c.Color = unchecked((int)0xFFFF3B30);
            return c;
        }

        public void Save() { Store.Save("shot.json", this); }
        public void SaveQuiet() { Store.Save("shot.json", this, false); }
    }

    // ───────────── снимок экрана, файл, буфер обмена ─────────────
    static class Shooter
    {
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO info);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int w, int h, int step, IntPtr brush, int flags);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetCursorPos(out Point p);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
        [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
        delegate bool EnumWin(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWin cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        delegate bool EnumMon(IntPtr m, IntPtr dc, ref RECT r, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, EnumMon cb, IntPtr l);

        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; public Rectangle ToRect() { return Rectangle.FromLTRB(L, T, R, B); } }
        [StructLayout(LayoutKind.Sequential)] struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public Point pt; }
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        // Снимок и окно выделения — в «настоящих» пикселях каждого монитора, даже если у мониторов разный масштаб.
        public static IntPtr PerMonitor()
        {
            try { return SetThreadDpiAwarenessContext(new IntPtr(-4)); } catch (Exception) { return IntPtr.Zero; }
        }
        public static void Restore(IntPtr old)
        {
            if (old == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(old); } catch (Exception) { }
        }

        public static Rectangle Virtual()
        {
            return new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
        }

        public static List<Rectangle> Monitors()
        {
            var list = new List<Rectangle>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref RECT r, IntPtr l) => { list.Add(r.ToRect()); return true; }, IntPtr.Zero);
            if (list.Count == 0) list.Add(Virtual());
            return list;
        }

        public static Point CursorPos() { Point p; GetCursorPos(out p); return p; }

        public static Rectangle MonitorAt(Point p)
        {
            foreach (var r in Monitors()) if (r.Contains(p)) return r;
            return Monitors()[0];
        }

        public static Bitmap Capture(Rectangle r, bool cursor)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CopyFromScreen(r.Location, Point.Empty, r.Size, CopyPixelOperation.SourceCopy);
                if (cursor) DrawCursor(g, r.Location);
            }
            return bmp;
        }

        static void DrawCursor(Graphics g, Point origin)
        {
            var ci = new CURSORINFO { cbSize = Marshal.SizeOf(typeof(CURSORINFO)) };
            if (!GetCursorInfo(ref ci) || (ci.flags & 1) == 0 || ci.hCursor == IntPtr.Zero) return;
            ICONINFO ii;
            int hx = 0, hy = 0;
            if (GetIconInfo(ci.hCursor, out ii))
            {
                hx = ii.xHotspot; hy = ii.yHotspot;
                if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
            }
            IntPtr dc = g.GetHdc();
            try { DrawIconEx(dc, ci.pt.X - hx - origin.X, ci.pt.Y - hy - origin.Y, ci.hCursor, 0, 0, 0, IntPtr.Zero, 3); }
            finally { g.ReleaseHdc(dc); }
        }

        // окно в фокусе — без невидимой рамки, которую Windows 10/11 добавляет вокруг окон
        public static Rectangle ForegroundRect()
        {
            IntPtr w = GetForegroundWindow();
            return w == IntPtr.Zero ? Rectangle.Empty : WindowRect(w);
        }

        static Rectangle WindowRect(IntPtr w)
        {
            RECT r;
            if (DwmGetWindowAttribute(w, 9 /*EXTENDED_FRAME_BOUNDS*/, out r, Marshal.SizeOf(typeof(RECT))) == 0) return r.ToRect();
            return GetWindowRect(w, out r) ? r.ToRect() : Rectangle.Empty;
        }

        // Видимые окна сверху вниз — для «щелчок по окну снимает окно»
        public static List<Rectangle> Windows() { return Windows(null); }

        // handles — заодно окна (для записи окна), в том же порядке, что и рамки
        public static List<Rectangle> Windows(List<IntPtr> handles)
        {
            var list = new List<Rectangle>();
            var cls = new System.Text.StringBuilder(64);
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) || IsIconic(h)) return true;
                int cloaked;
                if (DwmGetWindowAttribute(h, 14 /*CLOAKED*/, out cloaked, 4) == 0 && cloaked != 0) return true;
                int ex = GetWindowLong(h, -20);
                if ((ex & 0x20) != 0) return true; // прозрачные для мыши слои (оверлеи NVIDIA, Discord и т. п.)
                cls.Length = 0; GetClassName(h, cls, 64);
                string c = cls.ToString();
                if (c == "Progman" || c == "WorkerW") return true;
                bool tray = c == "Shell_TrayWnd" || c == "Shell_SecondaryTrayWnd";
                if (!tray && GetWindowTextLength(h) == 0) return true;
                var r = WindowRect(h);
                if (r.Width > 4 && r.Height > 4) { list.Add(r); if (handles != null) handles.Add(h); }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public static string NextPath(ShotConfig cfg)
        {
            string dir = cfg.FolderPath;
            Directory.CreateDirectory(dir);
            string ext = cfg.Format == "jpg" ? ".jpg" : ".png";
            string name = "Скриншот " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            string path = Path.Combine(dir, name + ext);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, name + " (" + i + ")" + ext);
            return path;
        }

        public static string DefaultName(ShotConfig cfg)
        {
            return "Скриншот " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss") + (cfg.Format == "jpg" ? ".jpg" : ".png");
        }

        public static void SaveFile(Bitmap b, string path, int quality)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".jpg" || ext == ".jpeg")
            {
                ImageCodecInfo jpeg = null;
                foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) jpeg = c;
                using (var ps = new EncoderParameters(1))
                {
                    ps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)quality);
                    b.Save(path, jpeg, ps);
                }
            }
            else if (ext == ".bmp") b.Save(path, ImageFormat.Bmp);
            else b.Save(path, ImageFormat.Png);
        }

        // в буфер: картинкой и PNG (браузеры, Telegram, Discord берут PNG)
        public static bool ToClipboard(Bitmap b)
        {
            var png = new MemoryStream();
            b.Save(png, ImageFormat.Png);
            var data = new DataObject();
            data.SetData(DataFormats.Bitmap, true, b);
            data.SetData("PNG", false, png);
            for (int i = 0; i < 5; i++)
            {
                try { Clipboard.SetDataObject(data, true); return true; }
                catch (ExternalException) { System.Threading.Thread.Sleep(80); }
            }
            return false;
        }

        // снимок готовой картинки в 24 бита — для файла и буфера
        public static Bitmap Flat(Bitmap src, Rectangle part)
        {
            var b = new Bitmap(part.Width, part.Height, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(b))
                g.DrawImage(src, new Rectangle(0, 0, part.Width, part.Height), part, GraphicsUnit.Pixel);
            return b;
        }

        // По PrtSc в Windows 11 открываются «Ножницы» (если это не выключено в параметрах клавиатуры)
        public static bool SnippingOnPrtSc()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard"))
                {
                    object v = k == null ? null : k.GetValue("PrintScreenKeyForSnippingEnabled");
                    if (v is int) return (int)v != 0;
                }
                return Environment.OSVersion.Version.Build >= 22000; // Windows 11: по умолчанию включено
            }
            catch (Exception) { return false; }
        }
    }

    // ───────────── экран выделения и рисования ─────────────
    class ShotForm : Form
    {
        enum Tool { None, Hand, Pencil, Line, Arrow, Rect, Ellipse, Marker, Text, Blur, Highlight, Step }
        enum Drag { None, Select, Move, Resize, Draw, Pick, Scale }

        class Shape
        {
            public Tool Kind; public Color Color; public int Width;
            public List<Point> Pts = new List<Point>();
            public string Text = ""; public int Number;
            public float Size; // размер шрифта надписи / радиус номера, если его потянули «Рукой» (0 — по толщине)
            public Bitmap Cache; public Rectangle CacheRect;
        }

        class Btn { public Rectangle R; public string Id; public string Tip; public Tool T; }

        static readonly Color[] Palette = {
            Color.FromArgb(255, 59, 48), Color.FromArgb(255, 149, 0), Color.FromArgb(255, 204, 0), Color.FromArgb(52, 199, 89),
            Color.FromArgb(48, 176, 199), Color.FromArgb(10, 132, 255), Color.FromArgb(175, 82, 222), Color.FromArgb(255, 45, 85),
            Color.White, Color.FromArgb(142, 142, 147), Color.Black };
        static readonly Color Accent = Color.FromArgb(10, 132, 255);
        static readonly Color BarBg = Color.FromArgb(242, 18, 20, 26);

        const int B = 32, G = 2, P = 5;   // кнопка, промежуток, поля панели

        readonly ShotConfig cfg;
        readonly Action<string, string> notify; // текст, путь к файлу (или null)
        readonly Rectangle virt;                // все мониторы, координаты экрана
        readonly List<Rectangle> monitors = new List<Rectangle>(); // в координатах окна
        readonly List<Rectangle> wins = new List<Rectangle>();     // окна, в координатах окна
        Bitmap shot, dim;

        Rectangle sel = Rectangle.Empty, hover = Rectangle.Empty, lastDirty = Rectangle.Empty;
        Drag drag = Drag.None; Point down, mouse; Rectangle selAtDown; int handle = -1; bool moved;
        Tool tool = Tool.None; Color color; int width;
        readonly List<Shape> shapes = new List<Shape>();
        Shape cur, editing;
        Shape picked; Point pickFrom; int pickDX, pickDY;       // «Рука»: выбранная фигура и куда её сдвинули
        int editIndex = -1; string editOld;                    // правка уже добавленного текста
        readonly Stack<Action> undo = new Stack<Action>();     // шаги для Ctrl+Z: добавление, перенос, правка, удаление
        readonly List<Btn> btns = new List<Btn>();
        Rectangle vbar, hbar, palRect;
        bool palOpen;
        Btn Hot { get { return hotId == null ? null : btns.Find(x => x.Id == hotId); } }
        string flash = ""; DateTime flashUntil;
        bool caretOn = true, done;
        string hotId; // кнопка под курсором (панели пересобираются при каждой отрисовке)
        readonly Timer tick = new Timer { Interval = 500 };
        readonly Font ui, uiBold, uiSmall;

        // Режим «что записывать» (pick != null): без инструментов рисования, Enter / кнопка — начать запись.
        // pick получает область в координатах экрана и окно, если выбрали щелчком именно окно.
        readonly Action<Rectangle, IntPtr> pick;
        readonly List<IntPtr> winH = new List<IntPtr>();
        IntPtr pickHwnd; Rectangle pickHwndRect;

        public ShotForm(ShotConfig cfg, Bitmap shot, Rectangle virt, List<Rectangle> monitorsOnScreen, List<Rectangle> windowsOnScreen, Action<string, string> notify)
            : this(cfg, shot, virt, monitorsOnScreen, windowsOnScreen, null, notify, null) { }

        public ShotForm(ShotConfig cfg, Bitmap shot, Rectangle virt, List<Rectangle> monitorsOnScreen, List<Rectangle> windowsOnScreen, List<IntPtr> handles,
                        Action<string, string> notify, Action<Rectangle, IntPtr> pick)
        {
            this.cfg = cfg; this.shot = shot; this.virt = virt; this.notify = notify; this.pick = pick;
            foreach (var m in monitorsOnScreen) monitors.Add(ToClient(m));
            for (int i = 0; i < windowsOnScreen.Count; i++)
            {
                var r = Rectangle.Intersect(ToClient(windowsOnScreen[i]), new Rectangle(Point.Empty, virt.Size));
                if (r.Width > 4 && r.Height > 4) { wins.Add(r); winH.Add(handles != null && i < handles.Count ? handles[i] : IntPtr.Zero); }
            }
            color = Color.FromArgb(cfg.Color); width = cfg.Width;

            // затемнённая копия — фон вокруг рамки
            dim = new Bitmap(shot.Width, shot.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(dim))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(shot, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
                using (var br = new SolidBrush(Color.FromArgb(120, 0, 0, 0))) g.FillRectangle(br, 0, 0, dim.Width, dim.Height);
            }

            ui = new Font("Segoe UI", 13f, GraphicsUnit.Pixel);
            uiBold = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel);
            uiSmall = new Font("Segoe UI", 11.5f, GraphicsUnit.Pixel);

            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            Text = "Qural — скриншот";
            Bounds = virt;
            Cursor = Aim;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Opaque, true);

            if (pick != null) Text = "Qural — что записывать";
            if (pick == null && cfg.KeepSelection && cfg.LastW > 1 && cfg.LastH > 1)
            {
                var last = Rectangle.Intersect(ToClient(new Rectangle(cfg.LastX, cfg.LastY, cfg.LastW, cfg.LastH)), new Rectangle(Point.Empty, virt.Size));
                if (last.Width > 1 && last.Height > 1) sel = last;
            }
            mouse = PointToClientSafe(Shooter.CursorPos());
            hover = sel.IsEmpty ? WindowAt(mouse) : Rectangle.Empty;

            tick.Tick += (s, e) =>
            {
                caretOn = !caretOn;
                if (flash.Length > 0 && DateTime.UtcNow > flashUntil) flash = "";
                if (editing == null && flash.Length == 0) tick.Stop();
                Invalidate();
            };
        }

        // ── свой прицел вместо курсора Windows: тонкий белый крест с тёмной обводкой и просветом в центре ──
        [DllImport("user32.dll")] static extern bool GetIconInfo(IntPtr h, out ICONINFO info);
        [DllImport("user32.dll")] static extern IntPtr CreateIconIndirect(ref ICONINFO info);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct ICONINFO { public bool fIcon; public int xHotspot, yHotspot; public IntPtr hbmMask, hbmColor; }

        static Cursor aim;
        static Cursor Aim { get { if (aim == null) aim = MakeAim(); return aim; } }

        static Cursor MakeAim()
        {
            const int S = 32, c = 15, gap = 4, arm = 11;
            try
            {
                using (var b = new Bitmap(S, S, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.Transparent);
                        using (var dark = new SolidBrush(Color.FromArgb(190, 0, 0, 0)))
                        {
                            g.FillRectangle(dark, c - 1, c - gap - arm - 1, 3, arm + 2);   // верх
                            g.FillRectangle(dark, c - 1, c + gap, 3, arm + 2);             // низ
                            g.FillRectangle(dark, c - gap - arm - 1, c - 1, arm + 2, 3);   // лево
                            g.FillRectangle(dark, c + gap, c - 1, arm + 2, 3);             // право
                            g.FillRectangle(dark, c - 1, c - 1, 3, 3);                     // точка
                        }
                        g.FillRectangle(Brushes.White, c, c - gap - arm, 1, arm);
                        g.FillRectangle(Brushes.White, c, c + gap + 1, 1, arm);
                        g.FillRectangle(Brushes.White, c - gap - arm, c, arm, 1);
                        g.FillRectangle(Brushes.White, c + gap + 1, c, arm, 1);
                        using (var dot = new SolidBrush(Accent)) g.FillRectangle(dot, c, c, 1, 1);
                    }
                    IntPtr icon = b.GetHicon();
                    ICONINFO ii;
                    if (!GetIconInfo(icon, out ii)) { DestroyIcon(icon); return Cursors.Cross; }
                    ii.fIcon = false; ii.xHotspot = c; ii.yHotspot = c;
                    IntPtr cur = CreateIconIndirect(ref ii);
                    if (ii.hbmMask != IntPtr.Zero) DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) DeleteObject(ii.hbmColor);
                    DestroyIcon(icon);
                    return cur == IntPtr.Zero ? Cursors.Cross : new Cursor(cur);
                }
            }
            catch (Exception) { return Cursors.Cross; }
        }

        Point PointToClientSafe(Point screen) { return new Point(screen.X - virt.X, screen.Y - virt.Y); }
        Rectangle ToClient(Rectangle r) { r.Offset(-virt.X, -virt.Y); return r; }
        Rectangle ToScreen(Rectangle r) { r.Offset(virt.X, virt.Y); return r; }

        protected override bool ShowWithoutActivation { get { return false; } }

        [StructLayout(LayoutKind.Sequential)] struct WINDOWPOS { public IntPtr hwnd, after; public int x, y, cx, cy; public uint flags; }
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out Shooter.RECT r);

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x02E0) { m.Result = IntPtr.Zero; return; } // WM_DPICHANGED: размер не трогаем, рисуем в пикселях
            // Окно лежит ровно на всех мониторах. Если его кто-то сдвигает (Windows, оверлеи, программы созвонов),
            // картинка левого монитора уезжает на основной — не даём сдвинуть ни на точку.
            if (m.Msg == 0x0046 /*WM_WINDOWPOSCHANGING*/ && m.LParam != IntPtr.Zero)
            {
                var wp = (WINDOWPOS)Marshal.PtrToStructure(m.LParam, typeof(WINDOWPOS));
                bool changed = false;
                if ((wp.flags & 0x2 /*NOMOVE*/) == 0 && (wp.x != virt.X || wp.y != virt.Y)) { wp.x = virt.X; wp.y = virt.Y; changed = true; }
                if ((wp.flags & 0x1 /*NOSIZE*/) == 0 && (wp.cx != virt.Width || wp.cy != virt.Height)) { wp.cx = virt.Width; wp.cy = virt.Height; changed = true; }
                if (changed) Marshal.StructureToPtr(wp, m.LParam, false);
            }
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Place();
            Fore.Bring(Handle);
            Activate();
            Focus();
            Place();
        }

        // поставить окно ровно на все мониторы и проверить; не вышло — записать в журнал для разбора
        void Place()
        {
            SetWindowPos(Handle, new IntPtr(-1) /*TOPMOST*/, virt.X, virt.Y, virt.Width, virt.Height, 0x0010 /*NOACTIVATE*/ | 0x0040 /*SHOWWINDOW*/);
            Shooter.RECT r;
            if (GetWindowRect(Handle, out r) && (r.L != virt.X || r.T != virt.Y || r.R - r.L != virt.Width || r.B - r.T != virt.Height))
                try
                {
                    File.AppendAllText(Path.Combine(Store.Dir, "shot.log"), DateTime.Now.ToString("s") + " окно " + r.L + "," + r.T + " " + (r.R - r.L) + "x" + (r.B - r.T) +
                        " вместо " + virt + Environment.NewLine);
                }
                catch (Exception) { }
        }

        // ── рисование ──
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var clip = e.ClipRectangle;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(dim, clip, clip, GraphicsUnit.Pixel);
            Rectangle bright = !sel.IsEmpty ? sel : (drag == Drag.None ? hover : Rectangle.Empty);
            if (!bright.IsEmpty)
            {
                var r = Rectangle.Intersect(bright, clip);
                if (!r.IsEmpty) g.DrawImage(shot, r, r, GraphicsUnit.Pixel);
            }
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (!sel.IsEmpty)
            {
                var state = g.Save();
                g.SetClip(sel, CombineMode.Intersect);
                DrawShapes(g, true);
                g.Restore(state);
                if (picked != null && editing == null) DrawPicked(g, picked);
                DrawFrame(g);
                DrawSizeLabel(g);
                LayoutBars();
                if (drag != Drag.Select) DrawBars(g);
            }
            else if (!hover.IsEmpty && drag == Drag.None)
            {
                using (var pen = new Pen(Accent, 2f)) g.DrawRectangle(pen, hover.X + 1, hover.Y + 1, hover.Width - 2, hover.Height - 2);
            }

            if (sel.IsEmpty || drag == Drag.Select || drag == Drag.Resize) DrawHint(g);
            if ((sel.IsEmpty || drag == Drag.Select || drag == Drag.Resize) && pick == null) DrawMagnifier(g);
            if (flash.Length > 0) DrawFlash(g);
        }

        void DrawFrame(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.None;
            if (pick != null) // запись — красная рамка, чтобы не спутать со скриншотом
                using (var pen = new Pen(RecRed, 2f)) g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
            else
                using (var pen = new Pen(Color.FromArgb(230, 255, 255, 255), 1f) { DashPattern = new float[] { 4, 3 } })
                    g.DrawRectangle(pen, sel.X, sel.Y, sel.Width - 1, sel.Height - 1);
            if (drag != Drag.Draw)
                foreach (var h in Handles())
                {
                    g.FillRectangle(Brushes.White, h);
                    using (var pen = new Pen(Color.FromArgb(160, 0, 0, 0))) g.DrawRectangle(pen, h.X, h.Y, h.Width - 1, h.Height - 1);
                }
            g.SmoothingMode = SmoothingMode.AntiAlias;
        }

        Rectangle[] Handles()
        {
            int s = 7, x0 = sel.Left - s / 2, x1 = sel.Left + sel.Width / 2 - s / 2, x2 = sel.Right - 1 - s / 2;
            int y0 = sel.Top - s / 2, y1 = sel.Top + sel.Height / 2 - s / 2, y2 = sel.Bottom - 1 - s / 2;
            // 0 лв, 1 в, 2 пв, 3 п, 4 пн, 5 н, 6 лн, 7 л
            return new[] {
                new Rectangle(x0, y0, s, s), new Rectangle(x1, y0, s, s), new Rectangle(x2, y0, s, s), new Rectangle(x2, y1, s, s),
                new Rectangle(x2, y2, s, s), new Rectangle(x1, y2, s, s), new Rectangle(x0, y2, s, s), new Rectangle(x0, y1, s, s) };
        }

        Rectangle SizeLabelRect(Graphics g)
        {
            string t = sel.Width + " × " + sel.Height;
            var sz = g.MeasureString(t, uiBold);
            int w = (int)sz.Width + 14, h = 22;
            int y = sel.Top - h - 6;
            var scr = ScreenOf(sel);
            if (y < scr.Top + 2) y = sel.Top + 6;
            return new Rectangle(sel.Left, y, w, h);
        }

        void DrawSizeLabel(Graphics g)
        {
            var r = SizeLabelRect(g);
            Pill(g, r, pick != null ? RecRed : BarBg);
            TextAt(g, sel.Width + " × " + sel.Height, uiBold, Color.White, r);
        }

        static readonly Color RecRed = Color.FromArgb(255, 59, 48);

        void DrawHint(Graphics g)
        {
            var r = HintRect(g);
            if (r.IsEmpty) return;
            Pill(g, r, pick != null ? RecRed : BarBg);
            TextAt(g, HintText(), ui, Color.FromArgb(235, 255, 255, 255), r);
        }

        string HintText()
        {
            if (pick != null && sel.IsEmpty)
                return "●  ЗАПИСЬ ЭКРАНА  —  выделите область  ·  щелчок по окну — записывать окно  ·  Ctrl+A — весь экран  ·  Esc — отмена";
            return sel.IsEmpty
                ? "Выделите область  ·  щелчок по окну — снимок окна  ·  Ctrl+A — весь экран  ·  C — копировать цвет  ·  Esc — выход"
                : "Отпустите кнопку мыши";
        }

        Rectangle HintRect(Graphics g)
        {
            if (!sel.IsEmpty && drag != Drag.Select) return Rectangle.Empty;
            var scr = MonitorOf(mouse);
            var sz = g.MeasureString(HintText(), ui);
            int w = (int)sz.Width + 24, h = 30;
            return new Rectangle(scr.Left + (scr.Width - w) / 2, scr.Top + 18, w, h);
        }

        // пипетка и лупа: 11×11 точек под курсором в 10 раз крупнее и цвет точки
        const int MagCells = 11, MagZoom = 10;
        Rectangle MagRect()
        {
            int w = MagCells * MagZoom + 2, h = w + 44;
            var scr = MonitorOf(mouse);
            int x = mouse.X + 22, y = mouse.Y + 22;
            if (x + w > scr.Right) x = mouse.X - 22 - w;
            if (y + h > scr.Bottom) y = mouse.Y - 22 - h;
            return new Rectangle(x, y, w, h);
        }

        void DrawMagnifier(Graphics g)
        {
            if (!new Rectangle(Point.Empty, ClientSize).Contains(mouse)) return;
            var r = MagRect();
            var box = new Rectangle(r.X, r.Y, r.Width, r.Width);
            g.FillRectangle(Brushes.Black, box);
            var src = new Rectangle(mouse.X - MagCells / 2, mouse.Y - MagCells / 2, MagCells, MagCells);
            var part = Rectangle.Intersect(src, new Rectangle(0, 0, shot.Width, shot.Height));
            if (!part.IsEmpty)
            {
                var st = g.Save();
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.None;
                var dst = new Rectangle(box.X + 1 + (part.X - src.X) * MagZoom, box.Y + 1 + (part.Y - src.Y) * MagZoom, part.Width * MagZoom, part.Height * MagZoom);
                g.DrawImage(shot, dst, part, GraphicsUnit.Pixel);
                g.Restore(st);
            }
            g.SmoothingMode = SmoothingMode.None;
            using (var pen = new Pen(Color.FromArgb(200, 255, 255, 255))) g.DrawRectangle(pen, box.X, box.Y, box.Width - 1, box.Height - 1);
            int c = box.X + 1 + (MagCells / 2) * MagZoom, cy = box.Y + 1 + (MagCells / 2) * MagZoom;
            using (var pen = new Pen(Color.FromArgb(220, 255, 255, 255))) g.DrawRectangle(pen, c - 1, cy - 1, MagZoom + 1, MagZoom + 1);
            using (var pen = new Pen(Color.FromArgb(200, 0, 0, 0))) g.DrawRectangle(pen, c, cy, MagZoom - 1, MagZoom - 1);
            g.SmoothingMode = SmoothingMode.AntiAlias;

            Color px = PixelAt(mouse);
            var info = new Rectangle(r.X, box.Bottom + 4, r.Width, 40);
            Pill(g, info, BarBg);
            var sw = new Rectangle(info.X + 8, info.Y + 8, 12, 12);
            using (var br = new SolidBrush(px)) g.FillRectangle(br, sw);
            using (var pen = new Pen(Color.FromArgb(140, 255, 255, 255))) g.DrawRectangle(pen, sw);
            using (var br = new SolidBrush(Color.White)) g.DrawString(Hex(px), uiBold, br, info.X + 25, info.Y + 4);
            Point scrPt = new Point(mouse.X + virt.X, mouse.Y + virt.Y);
            using (var br = new SolidBrush(Color.FromArgb(180, 255, 255, 255))) g.DrawString("X " + scrPt.X + "  Y " + scrPt.Y, uiSmall, br, info.X + 8, info.Y + 22);
        }

        Color PixelAt(Point p)
        {
            if (p.X < 0 || p.Y < 0 || p.X >= shot.Width || p.Y >= shot.Height) return Color.Black;
            var c = shot.GetPixel(p.X, p.Y);
            return Color.FromArgb(255, c.R, c.G, c.B);
        }

        static string Hex(Color c) { return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2"); }

        void DrawFlash(Graphics g)
        {
            var r = FlashRect(g);
            Pill(g, r, BarBg);
            TextAt(g, flash, uiBold, Color.White, r);
        }

        Rectangle FlashRect(Graphics g)
        {
            var scr = MonitorOf(mouse);
            var sz = g.MeasureString(flash, uiBold);
            int w = (int)sz.Width + 28, h = 32;
            return new Rectangle(scr.Left + (scr.Width - w) / 2, scr.Bottom - h - 60, w, h);
        }

        void Flash(string text)
        {
            flash = text; flashUntil = DateTime.UtcNow.AddMilliseconds(1300);
            tick.Start();
            Invalidate();
        }

        // ── панели инструментов ──
        void LayoutBars()
        {
            btns.Clear();
            if (sel.IsEmpty) { vbar = hbar = palRect = Rectangle.Empty; return; }
            var scr = ScreenOf(sel);
            if (pick != null)
            {
                // запись: только «Начать запись» и «Отмена»
                vbar = palRect = Rectangle.Empty;
                const int RecW = 150; // широкая кнопка «● Начать запись»
                int pw = P * 2 + RecW + B + G + 8, ph = P * 2 + B;
                int px = Math.Max(scr.Left + 4, Math.Min(scr.Right - 4 - pw, sel.Right - pw));
                int py = sel.Bottom + 8;
                if (py + ph > scr.Bottom) py = sel.Top - 8 - ph;
                if (py < scr.Top) py = sel.Bottom - ph - 8;
                hbar = new Rectangle(px, py, pw, ph);
                btns.Add(new Btn { R = new Rectangle(px + P, py + P, RecW, B), Id = "rec", Tip = "Начать запись (Enter)" });
                btns.Add(new Btn { R = new Rectangle(px + P + RecW + G + 8, py + P, B, B), Id = "close", Tip = "Отмена (Esc)" });
                return;
            }
            string[] ids = { "hand", "pencil", "line", "arrow", "rect", "ellipse", "marker", "text", "blur", "highlight", "step", "color", "undo" };
            string[] tips = { "Рука — двигать нарисованное, двойной щелчок по тексту — править, Delete — удалить",
                              "Карандаш", "Линия", "Стрелка", "Прямоугольник", "Окружность", "Маркер", "Текст", "Размытие",
                              "Подсветка области — всё вокруг затемняется", "Номер шага",
                              "Цвет", "Отменить (Ctrl+Z)" };
            Tool[] tools = { Tool.Hand, Tool.Pencil, Tool.Line, Tool.Arrow, Tool.Rect, Tool.Ellipse, Tool.Marker, Tool.Text, Tool.Blur, Tool.Highlight, Tool.Step, Tool.None, Tool.None };
            int vw = P * 2 + B, vh = P * 2 + ids.Length * B + (ids.Length - 1) * G + 8;
            int x = sel.Right + 8;
            if (x + vw > scr.Right) x = sel.Left - 8 - vw;
            if (x < scr.Left) x = sel.Right - vw - 8;
            int y = sel.Top;
            if (y + vh > scr.Bottom) y = scr.Bottom - vh - 4;
            if (y < scr.Top) y = scr.Top + 4;
            vbar = new Rectangle(x, y, vw, vh);
            int by = y + P;
            for (int i = 0; i < ids.Length; i++)
            {
                if (ids[i] == "color") by += 8;
                btns.Add(new Btn { R = new Rectangle(x + P, by, B, B), Id = ids[i], Tip = tips[i], T = tools[i] });
                by += B + G;
            }

            string[] hids = { "copy", "save", "close" };
            string[] htips = { "Копировать (Ctrl+C)", "Сохранить (Ctrl+S)", "Закрыть (Esc)" };
            int hw = P * 2 + 3 * B + 2 * G + 8, hh = P * 2 + B;
            int hx = sel.Right - hw;
            if (hx < scr.Left + 4) hx = scr.Left + 4;
            if (hx + hw > scr.Right - 4) hx = scr.Right - 4 - hw;
            int hy = sel.Bottom + 8;
            if (hy + hh > scr.Bottom) hy = sel.Top - 8 - hh;
            if (hy < scr.Top) hy = sel.Bottom - hh - 8;
            // не наезжать на боковую панель
            if (new Rectangle(hx, hy, hw, hh).IntersectsWith(vbar)) hx = Math.Max(scr.Left + 4, vbar.Left - hw - 6);
            hbar = new Rectangle(hx, hy, hw, hh);
            int bx = hx + P;
            for (int i = 0; i < hids.Length; i++)
            {
                if (i == 2) bx += 8;
                btns.Add(new Btn { R = new Rectangle(bx, hy + P, B, B), Id = hids[i], Tip = htips[i] });
                bx += B + G;
            }

            if (palOpen)
            {
                var cb = btns.Find(b => b.Id == "color");
                int n = Palette.Length + 1, sw = 22, pw = P * 2 + n * sw + (n - 1) * 4, ph = P * 2 + sw;
                int px = vbar.Left - pw - 6;
                if (px < scr.Left + 4) px = vbar.Right + 6;
                palRect = new Rectangle(px, cb.R.Y + (B - ph) / 2, pw, ph);
            }
            else palRect = Rectangle.Empty;
        }

        Rectangle Swatch(int i)
        {
            const int sw = 22;
            return new Rectangle(palRect.X + P + i * (sw + 4), palRect.Y + P, sw, sw);
        }

        void DrawBars(Graphics g)
        {
            Panel(g, vbar);
            Panel(g, hbar);
            using (var pen = new Pen(Color.FromArgb(50, 255, 255, 255)))
            {
                var colorBtn = btns.Find(b => b.Id == "color");
                if (colorBtn != null) g.DrawLine(pen, vbar.X + 9, colorBtn.R.Y - 5, vbar.Right - 9, colorBtn.R.Y - 5);
                var closeBtn = btns.Find(b => b.Id == "close");
                if (closeBtn != null) g.DrawLine(pen, closeBtn.R.X - 5, hbar.Y + 9, closeBtn.R.X - 5, hbar.Bottom - 9);
            }
            foreach (var b in btns)
            {
                bool on = (b.T != Tool.None && b.T == tool) || (b.Id == "color" && palOpen);
                if (on) Fill(g, b.R, 8, Color.FromArgb(70, 255, 255, 255));
                else if (b.Id == hotId) Fill(g, b.R, 8, Color.FromArgb(34, 255, 255, 255));
                bool disabled = b.Id == "undo" && undo.Count == 0 && editing == null;
                if (b.Id == "rec")
                {
                    using (var red = new SolidBrush(RecRed)) g.FillEllipse(red, b.R.X + 12, b.R.Y + 10, 12, 12);
                    TextAt(g, "Начать запись", uiBold, Color.White, new Rectangle(b.R.X + 30, b.R.Y, b.R.Width - 34, b.R.Height));
                }
                else DrawIcon(g, b.Id, b.R, disabled ? Color.FromArgb(90, 255, 255, 255) : Color.White);
            }
            if (palOpen)
            {
                Panel(g, palRect);
                for (int i = 0; i <= Palette.Length; i++)
                {
                    var r = Swatch(i);
                    if (i < Palette.Length)
                    {
                        using (var br = new SolidBrush(Palette[i])) g.FillEllipse(br, r.X + 2, r.Y + 2, r.Width - 4, r.Height - 4);
                        if (Palette[i].ToArgb() == color.ToArgb())
                            using (var pen = new Pen(Color.White, 2f)) g.DrawEllipse(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
                        else if (Palette[i].GetBrightness() < 0.15f)
                            using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255))) g.DrawEllipse(pen, r.X + 2, r.Y + 2, r.Width - 5, r.Height - 5);
                    }
                    else
                    {
                        using (var pen = new Pen(Color.White, 1.6f))
                        {
                            g.DrawEllipse(pen, r.X + 3, r.Y + 3, r.Width - 7, r.Height - 7);
                            g.DrawLine(pen, r.X + r.Width / 2f, r.Y + 7, r.X + r.Width / 2f, r.Bottom - 7);
                            g.DrawLine(pen, r.X + 7, r.Y + r.Height / 2f, r.Right - 7, r.Y + r.Height / 2f);
                        }
                    }
                }
            }
            var hb = Hot;
            if (hb != null && drag == Drag.None) DrawTip(g, hb);
        }

        Rectangle TipRect(Graphics g, Btn b)
        {
            var sz = g.MeasureString(b.Tip, ui);
            int w = (int)sz.Width + 16, h = 26;
            var scr = ScreenOf(sel);
            bool vertical = vbar.Contains(b.R);
            Rectangle r;
            if (vertical)
            {
                r = new Rectangle(vbar.Right + 6, b.R.Y + (B - h) / 2, w, h);
                if (r.Right > scr.Right) r.X = vbar.Left - 6 - w;
            }
            else
            {
                r = new Rectangle(b.R.X + B / 2 - w / 2, hbar.Bottom + 6, w, h);
                if (r.Bottom > scr.Bottom) r.Y = hbar.Top - 6 - h;
                if (r.Right > scr.Right) r.X = scr.Right - w - 4;
            }
            return r;
        }

        void DrawTip(Graphics g, Btn b)
        {
            var r = TipRect(g, b);
            Pill(g, r, BarBg);
            TextAt(g, b.Tip, ui, Color.White, r);
        }

        static void Panel(Graphics g, Rectangle r)
        {
            if (r.IsEmpty) return;
            Fill(g, r, 12, BarBg);
            using (var path = Round(r, 12))
            using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255))) g.DrawPath(pen, path);
        }

        static void Pill(Graphics g, Rectangle r, Color c) { Fill(g, r, Math.Min(r.Height / 2, 12), c); }

        static void Fill(Graphics g, Rectangle r, int rad, Color c)
        {
            using (var path = Round(r, rad))
            using (var br = new SolidBrush(c)) g.FillPath(br, path);
        }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = Math.Max(1, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void TextAt(Graphics g, string t, Font f, Color c, Rectangle r)
        {
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            using (var br = new SolidBrush(c)) g.DrawString(t, f, br, r, sf);
        }

        // значки 16×16, белыми линиями
        void DrawIcon(Graphics g, string id, Rectangle r, Color c)
        {
            var st = g.Save();
            g.TranslateTransform(r.X + (r.Width - 20) / 2f, r.Y + (r.Height - 20) / 2f);
            g.ScaleTransform(1.25f, 1.25f);
            using (var pen = new Pen(c, 1.35f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var br = new SolidBrush(c))
            {
                switch (id)
                {
                    case "hand":
                        g.DrawLine(pen, 8, 2, 8, 14);
                        g.DrawLine(pen, 2, 8, 14, 8);
                        g.DrawLines(pen, new[] { new PointF(6, 4), new PointF(8, 2), new PointF(10, 4) });
                        g.DrawLines(pen, new[] { new PointF(6, 12), new PointF(8, 14), new PointF(10, 12) });
                        g.DrawLines(pen, new[] { new PointF(4, 6), new PointF(2, 8), new PointF(4, 10) });
                        g.DrawLines(pen, new[] { new PointF(12, 6), new PointF(14, 8), new PointF(12, 10) });
                        break;
                    case "highlight":
                        // затемнение вокруг, светлое окно в центре
                        using (var fog = new SolidBrush(Color.FromArgb(90, c)))
                        {
                            g.FillRectangle(fog, 1.5f, 2.5f, 13, 3);
                            g.FillRectangle(fog, 1.5f, 10.5f, 13, 3);
                            g.FillRectangle(fog, 1.5f, 5.5f, 3.5f, 5);
                            g.FillRectangle(fog, 11, 5.5f, 3.5f, 5);
                        }
                        g.DrawRectangle(pen, 5, 5.5f, 6, 5);
                        break;
                    case "pencil":
                        g.DrawPolygon(pen, new[] { new PointF(3, 13), new PointF(3.8f, 10.2f), new PointF(10.8f, 3.2f), new PointF(12.8f, 5.2f), new PointF(5.8f, 12.2f) });
                        g.DrawLine(pen, 9.3f, 4.7f, 11.3f, 6.7f);
                        break;
                    case "line": g.DrawLine(pen, 3, 13, 13, 3); break;
                    case "arrow":
                        g.DrawLine(pen, 3, 13, 13, 3);
                        g.DrawLines(pen, new[] { new PointF(7.5f, 3), new PointF(13, 3), new PointF(13, 8.5f) });
                        break;
                    case "rect": g.DrawRectangle(pen, 2.5f, 3.5f, 11, 9); break;
                    case "ellipse": g.DrawEllipse(pen, 2, 3, 12, 10); break;
                    case "marker":
                        using (var thick = new Pen(Color.FromArgb(150, c), 4.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLine(thick, 3.5f, 11.5f, 12.5f, 4.5f);
                        g.DrawLine(pen, 2.5f, 14, 13.5f, 14);
                        break;
                    case "text":
                        g.DrawLine(pen, 3, 3.5f, 13, 3.5f);
                        g.DrawLine(pen, 8, 3.5f, 8, 13);
                        g.DrawLine(pen, 6, 13, 10, 13);
                        break;
                    case "blur":
                        g.DrawRectangle(pen, 2.5f, 2.5f, 11, 11);
                        for (int i = 0; i < 4; i++) for (int j = 0; j < 4; j++)
                                if ((i + j) % 2 == 0) g.FillRectangle(br, 2.5f + i * 2.75f, 2.5f + j * 2.75f, 2.75f, 2.75f);
                        break;
                    case "step":
                        g.DrawEllipse(pen, 2, 2, 12, 12);
                        g.DrawLines(pen, new[] { new PointF(6.6f, 6), new PointF(8.4f, 4.6f), new PointF(8.4f, 11.4f) });
                        break;
                    case "color":
                        using (var cb = new SolidBrush(color)) g.FillEllipse(cb, 2.5f, 2.5f, 11, 11);
                        g.DrawEllipse(pen, 2.5f, 2.5f, 11, 11);
                        break;
                    case "undo":
                        g.DrawLines(pen, new[] { new PointF(5.5f, 3), new PointF(2.5f, 6), new PointF(5.5f, 9) });
                        g.DrawBezier(pen, 2.5f, 6, 10, 6, 14.5f, 8, 13, 11.5f);
                        g.DrawLine(pen, 13, 11.5f, 10.5f, 13);
                        break;
                    case "copy":
                        g.DrawRectangle(pen, 5.5f, 5.5f, 8, 8);
                        g.DrawLines(pen, new[] { new PointF(2.5f, 10.5f), new PointF(2.5f, 2.5f), new PointF(10.5f, 2.5f) });
                        break;
                    case "save":
                        g.DrawLine(pen, 8, 2.5f, 8, 10);
                        g.DrawLines(pen, new[] { new PointF(4.8f, 6.8f), new PointF(8, 10), new PointF(11.2f, 6.8f) });
                        g.DrawLines(pen, new[] { new PointF(2.5f, 10.5f), new PointF(2.5f, 13.5f), new PointF(13.5f, 13.5f), new PointF(13.5f, 10.5f) });
                        break;
                    case "rec":
                        using (var red = new SolidBrush(Color.FromArgb(255, 59, 48))) g.FillEllipse(red, 3, 3, 10, 10);
                        break;
                    case "close":
                        g.DrawLine(pen, 4, 4, 12, 12);
                        g.DrawLine(pen, 12, 4, 4, 12);
                        break;
                }
            }
            g.Restore(st);
        }

        // ── фигуры ──
        // Подсветка — одним слоем под остальным: всё в рамке, кроме подсвеченных мест, затемняется.
        // Стрелки и надписи рисуются поверх и не тускнеют.
        void DrawShapes(Graphics g, bool live)
        {
            var spots = new List<Rectangle>();
            foreach (var s in shapes) if (s.Kind == Tool.Highlight && s.Pts.Count >= 2) spots.Add(Norm(s.Pts[0], s.Pts[1]));
            if (live && cur != null && cur.Kind == Tool.Highlight && cur.Pts.Count >= 2) spots.Add(Norm(cur.Pts[0], cur.Pts[1]));
            if (spots.Count > 0)
                using (var region = new Region(sel))
                {
                    foreach (var r in spots)
                    {
                        if (r.Width < 2 || r.Height < 2) continue;
                        using (var path = Round(r, Math.Max(1, Math.Min(8, Math.Min(r.Width, r.Height) / 2)))) region.Exclude(path);
                    }
                    using (var br = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillRegion(br, region);
                }
            foreach (var s in shapes) if (s.Kind != Tool.Highlight) DrawShape(g, s);
            if (!live) return;
            if (cur != null && cur.Kind != Tool.Highlight) DrawShape(g, cur);
            if (editing != null) { DrawShape(g, editing); if (caretOn) DrawCaret(g, editing); }
        }

        // рамка вокруг фигуры, выбранной «Рукой»
        void DrawPicked(Graphics g, Shape s)
        {
            var r = ShapeBox(s); r.Inflate(6, 6);
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.None;
            using (var pen = new Pen(Color.FromArgb(200, 0, 0, 0), 3f)) g.DrawRectangle(pen, r);
            using (var pen = new Pen(Accent, 1f) { DashPattern = new float[] { 4, 3 } }) g.DrawRectangle(pen, r);
            foreach (var h in PickHandles(s))
            {
                if (h.IsEmpty) continue;
                g.FillRectangle(Brushes.White, h);
                using (var pen = new Pen(Accent)) g.DrawRectangle(pen, h.X, h.Y, h.Width - 1, h.Height - 1);
            }
            g.Restore(st);
        }

        // Рамка фигуры для ручек размера: у надписи — сам текст, у номера — круг, у остального — крайние точки
        Rectangle ShapeBox(Shape s)
        {
            if (Uniform(s)) return ShapeBounds(s);
            int l = int.MaxValue, t = int.MaxValue, rr = int.MinValue, b = int.MinValue;
            foreach (var p in s.Pts) { l = Math.Min(l, p.X); t = Math.Min(t, p.Y); rr = Math.Max(rr, p.X); b = Math.Max(b, p.Y); }
            return Rectangle.FromLTRB(l, t, rr, b);
        }

        static bool Uniform(Shape s) { return s.Kind == Tool.Text || s.Kind == Tool.Step; }

        // 8 ручек, как у рамки выделения (0 лв, 1 в, 2 пв, 3 п, 4 пн, 5 н, 6 лн, 7 л); у надписи и номера — только углы
        Rectangle[] PickHandles(Shape s)
        {
            var r = ShapeBox(s); r.Inflate(6, 6);
            const int z = 8;
            int x0 = r.Left - z / 2, x1 = r.Left + r.Width / 2 - z / 2, x2 = r.Right - z / 2;
            int y0 = r.Top - z / 2, y1 = r.Top + r.Height / 2 - z / 2, y2 = r.Bottom - z / 2;
            bool u = Uniform(s);
            return new[] {
                new Rectangle(x0, y0, z, z), u ? Rectangle.Empty : new Rectangle(x1, y0, z, z), new Rectangle(x2, y0, z, z),
                u ? Rectangle.Empty : new Rectangle(x2, y1, z, z), new Rectangle(x2, y2, z, z), u ? Rectangle.Empty : new Rectangle(x1, y2, z, z),
                new Rectangle(x0, y2, z, z), u ? Rectangle.Empty : new Rectangle(x0, y1, z, z) };
        }

        int HitPickHandle(Point p)
        {
            if (picked == null) return -1;
            var hs = PickHandles(picked);
            for (int i = 0; i < hs.Length; i++) { if (hs[i].IsEmpty) continue; var r = hs[i]; r.Inflate(4, 4); if (r.Contains(p)) return i; }
            return -1;
        }

        // ── размер фигуры «Рукой» ──
        int scaleHandle; Rectangle scaleBox; List<Point> scalePts; float scaleSize, scaleSizeRaw;

        void BeginScale(int h, Point p)
        {
            drag = Drag.Scale; scaleHandle = h; down = p;
            scaleBox = ShapeBox(picked);
            scalePts = new List<Point>(picked.Pts);
            scaleSizeRaw = picked.Size;
            scaleSize = picked.Kind == Tool.Text ? TextPx(picked) : picked.Kind == Tool.Step ? StepRad(picked) : 0;
        }

        void ScaleTo(Point p)
        {
            var s = picked; var o = scaleBox;
            int dx = p.X - down.X, dy = p.Y - down.Y;
            int l = o.Left, t = o.Top, r = o.Right, b = o.Bottom;
            switch (scaleHandle)
            {
                case 0: l += dx; t += dy; break;
                case 1: t += dy; break;
                case 2: r += dx; t += dy; break;
                case 3: r += dx; break;
                case 4: r += dx; b += dy; break;
                case 5: b += dy; break;
                case 6: l += dx; b += dy; break;
                case 7: l += dx; break;
            }
            if (Uniform(s))
            {
                // надпись и номер растут без искажений: от противоположного угла, шрифт / круг — во столько же раз
                float fw = (float)(r - l) / Math.Max(1, o.Width), fh = (float)(b - t) / Math.Max(1, o.Height);
                float f = Math.Abs(fw - 1) > Math.Abs(fh - 1) ? fw : fh;
                float lo = (s.Kind == Tool.Text ? 8f : 6f) / scaleSize, hi = (s.Kind == Tool.Text ? 400f : 200f) / scaleSize;
                f = Math.Max(lo, Math.Min(hi, f));
                int nw = (int)Math.Round(o.Width * f), nh = (int)Math.Round(o.Height * f);
                bool left = scaleHandle == 0 || scaleHandle == 6, top = scaleHandle == 0 || scaleHandle == 2;
                int nl = left ? o.Right - nw : o.Left, nt = top ? o.Bottom - nh : o.Top;
                s.Size = scaleSize * f;
                s.Pts[0] = s.Kind == Tool.Text ? new Point(nl, nt) : new Point(nl + nw / 2, nt + nh / 2);
                return;
            }
            // линии, фигуры, карандаш: точки растягиваются вместе с рамкой (перетянуть через край — отразить)
            for (int i = 0; i < scalePts.Count; i++)
            {
                var q = scalePts[i];
                int x = o.Width < 2 ? q.X + (l - o.Left) : l + (int)Math.Round((q.X - o.Left) * (double)(r - l) / o.Width);
                int y = o.Height < 2 ? q.Y + (t - o.Top) : t + (int)Math.Round((q.Y - o.Top) * (double)(b - t) / o.Height);
                s.Pts[i] = new Point(x, y);
            }
            if (s.Cache != null) { s.Cache.Dispose(); s.Cache = null; }
        }

        void EndScale()
        {
            var s = picked; if (s == null) return;
            var oldPts = scalePts; float oldSize = scaleSizeRaw;
            bool same = s.Size == oldSize;
            for (int i = 0; same && i < s.Pts.Count; i++) if (s.Pts[i] != oldPts[i]) same = false;
            if (same) return;
            undo.Push(() =>
            {
                s.Pts.Clear(); s.Pts.AddRange(oldPts); s.Size = oldSize;
                if (s.Cache != null) { s.Cache.Dispose(); s.Cache = null; }
            });
        }

        Rectangle ShapeBounds(Shape s)
        {
            switch (s.Kind)
            {
                case Tool.Text:
                    using (var g = CreateGraphics()) return TextBounds(g, s);
                case Tool.Step:
                    {
                        int rad = (int)Math.Ceiling(StepRad(s));
                        return new Rectangle(s.Pts[0].X - rad, s.Pts[0].Y - rad, rad * 2, rad * 2);
                    }
                default:
                    {
                        var r = new Rectangle(s.Pts[0], new Size(1, 1));
                        foreach (var p in s.Pts) r = Rectangle.Union(r, new Rectangle(p, new Size(1, 1)));
                        int w = s.Kind == Tool.Marker ? s.Width * 4 + 8 : s.Kind == Tool.Arrow ? s.Width * 4 + 10 : s.Width;
                        r.Inflate(w / 2 + 1, w / 2 + 1);
                        return r;
                    }
            }
        }

        Rectangle TextBounds(Graphics g, Shape s)
        {
            using (var f = TextFont(s))
            using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
            {
                sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                var sz = g.MeasureString(s.Text.Length > 0 ? s.Text : " ", f, PointF.Empty, sf);
                int lines = s.Text.Split('\n').Length;
                return new Rectangle(s.Pts[0].X, s.Pts[0].Y, (int)Math.Ceiling(sz.Width) + 3, (int)Math.Ceiling(Math.Max(sz.Height, f.GetHeight(g) * lines)));
            }
        }

        // верхняя фигура под точкой
        Shape HitShape(Point p)
        {
            for (int i = shapes.Count - 1; i >= 0; i--) if (HitOne(shapes[i], p)) return shapes[i];
            return null;
        }

        bool HitOne(Shape s, Point p)
        {
            if (s.Pts.Count == 0) return false;
            float tol = s.Width / 2f + 6;
            switch (s.Kind)
            {
                case Tool.Pencil:
                case Tool.Marker:
                    {
                        if (s.Kind == Tool.Marker) tol = (s.Width * 4 + 8) / 2f + 4;
                        if (s.Pts.Count == 1) return Dist(p, s.Pts[0]) <= tol;
                        for (int i = 1; i < s.Pts.Count; i++) if (SegDist(p, s.Pts[i - 1], s.Pts[i]) <= tol) return true;
                        return false;
                    }
                case Tool.Line:
                case Tool.Arrow:
                    return s.Pts.Count >= 2 && SegDist(p, s.Pts[0], s.Pts[1]) <= tol + (s.Kind == Tool.Arrow ? s.Width + 4 : 0);
                case Tool.Step:
                    return Dist(p, s.Pts[0]) <= StepRad(s) + 3;
                case Tool.Text:
                    {
                        var r = ShapeBounds(s); r.Inflate(4, 4);
                        return r.Contains(p);
                    }
                default: // прямоугольник, окружность, размытие, подсветка — за всю площадь
                    {
                        if (s.Pts.Count < 2) return false;
                        var r = Norm(s.Pts[0], s.Pts[1]); r.Inflate((int)tol, (int)tol);
                        return r.Contains(p);
                    }
            }
        }

        static float Dist(Point a, Point b) { float dx = a.X - b.X, dy = a.Y - b.Y; return (float)Math.Sqrt(dx * dx + dy * dy); }

        static float SegDist(Point p, Point a, Point b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y, len2 = dx * dx + dy * dy;
            if (len2 < 0.5f) return Dist(p, a);
            float t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2));
            float x = a.X + t * dx - p.X, y = a.Y + t * dy - p.Y;
            return (float)Math.Sqrt(x * x + y * y);
        }

        static void MoveShape(Shape s, int dx, int dy)
        {
            for (int i = 0; i < s.Pts.Count; i++) s.Pts[i] = new Point(s.Pts[i].X + dx, s.Pts[i].Y + dy);
            if (s.Cache != null) { s.Cache.Dispose(); s.Cache = null; } // размытие берёт то, что под ним на новом месте
        }

        void AddShape(Shape s)
        {
            shapes.Add(s);
            undo.Push(() => shapes.Remove(s));
        }

        void ClearShapes() { shapes.Clear(); undo.Clear(); picked = null; }

        void DrawShape(Graphics g, Shape s)
        {
            switch (s.Kind)
            {
                case Tool.Pencil:
                case Tool.Marker:
                    {
                        bool marker = s.Kind == Tool.Marker;
                        float w = marker ? s.Width * 4 + 8 : s.Width;
                        var c = marker ? Color.FromArgb(110, s.Color) : s.Color;
                        if (s.Pts.Count == 1)
                            using (var br = new SolidBrush(c)) g.FillEllipse(br, s.Pts[0].X - w / 2, s.Pts[0].Y - w / 2, w, w);
                        else if (s.Pts.Count > 1)
                            using (var pen = new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                                g.DrawLines(pen, s.Pts.ToArray());
                        break;
                    }
                case Tool.Line:
                    if (s.Pts.Count < 2) break;
                    using (var pen = new Pen(s.Color, s.Width) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, s.Pts[0], s.Pts[1]);
                    break;
                case Tool.Arrow:
                    if (s.Pts.Count < 2) break;
                    DrawArrow(g, s);
                    break;
                case Tool.Rect:
                    if (s.Pts.Count < 2) break;
                    using (var pen = new Pen(s.Color, s.Width) { LineJoin = LineJoin.Miter }) { var r = Norm(s.Pts[0], s.Pts[1]); g.DrawRectangle(pen, r); }
                    break;
                case Tool.Ellipse:
                    if (s.Pts.Count < 2) break;
                    using (var pen = new Pen(s.Color, s.Width)) g.DrawEllipse(pen, Norm(s.Pts[0], s.Pts[1]));
                    break;
                case Tool.Blur:
                    if (s.Pts.Count < 2) break;
                    DrawBlur(g, s);
                    break;
                case Tool.Step:
                    {
                        if (s.Pts.Count < 1) break;
                        float rad = StepRad(s);
                        var p = s.Pts[0];
                        using (var br = new SolidBrush(s.Color)) g.FillEllipse(br, p.X - rad, p.Y - rad, rad * 2, rad * 2);
                        using (var pen = new Pen(Color.FromArgb(150, 255, 255, 255), 1.5f)) g.DrawEllipse(pen, p.X - rad, p.Y - rad, rad * 2, rad * 2);
                        bool light = s.Color.GetBrightness() > 0.7f || (s.Color.R > 200 && s.Color.G > 180);
                        using (var f = new Font("Segoe UI Semibold", rad * 1.05f, GraphicsUnit.Pixel))
                        using (var br = new SolidBrush(light ? Color.Black : Color.White))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString(s.Number.ToString(), f, br, new RectangleF(p.X - rad, p.Y - rad + 1, rad * 2, rad * 2), sf);
                        break;
                    }
                case Tool.Text:
                    {
                        if (s.Pts.Count < 1) break;
                        using (var f = TextFont(s))
                        using (var br = new SolidBrush(s.Color))
                            g.DrawString(s.Text, f, br, s.Pts[0], StringFormat.GenericTypographic);
                        break;
                    }
            }
        }

        static float TextPx(Shape s) { return s.Size > 0 ? s.Size : 14 + s.Width * 3; }
        static float StepRad(Shape s) { return s.Size > 0 ? s.Size : 11 + s.Width * 1.6f; }
        static Font TextFont(Shape s) { return new Font("Segoe UI Semibold", TextPx(s), GraphicsUnit.Pixel); }

        void DrawCaret(Graphics g, Shape s)
        {
            using (var f = TextFont(s))
            {
                string[] lines = s.Text.Split('\n');
                string last = lines[lines.Length - 1];
                float lh = f.GetHeight(g);
                float w = 0;
                if (last.Length > 0)
                    using (var sf = (StringFormat)StringFormat.GenericTypographic.Clone())
                    {
                        sf.FormatFlags |= StringFormatFlags.MeasureTrailingSpaces;
                        w = g.MeasureString(last, f, PointF.Empty, sf).Width;
                    }
                float x = s.Pts[0].X + w + 1, y = s.Pts[0].Y + lh * (lines.Length - 1);
                using (var pen = new Pen(s.Color, 2f)) g.DrawLine(pen, x, y + 2, x, y + lh - 2);
            }
        }

        static void DrawArrow(Graphics g, Shape s)
        {
            PointF a = s.Pts[0], b = s.Pts[1];
            float dx = b.X - a.X, dy = b.Y - a.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            float ux = dx / len, uy = dy / len;
            float head = Math.Min(len * 0.6f, 10 + s.Width * 3.2f);
            var baseC = new PointF(b.X - ux * head, b.Y - uy * head);
            var shaftEnd = new PointF(b.X - ux * head * 0.7f, b.Y - uy * head * 0.7f);
            using (var pen = new Pen(s.Color, s.Width) { StartCap = LineCap.Round, EndCap = LineCap.Flat }) g.DrawLine(pen, a, shaftEnd);
            float hw = head * 0.55f;
            var pts = new[] { b, new PointF(baseC.X - uy * hw, baseC.Y + ux * hw), new PointF(baseC.X + uy * hw, baseC.Y - ux * hw) };
            using (var br = new SolidBrush(s.Color)) g.FillPolygon(br, pts);
        }

        // «Размытие» — крупные квадраты: текст под ними не прочитать
        void DrawBlur(Graphics g, Shape s)
        {
            var r = Rectangle.Intersect(Norm(s.Pts[0], s.Pts[1]), new Rectangle(0, 0, shot.Width, shot.Height));
            if (r.Width < 2 || r.Height < 2) return;
            int block = 6 + s.Width * 2;
            if (s.Cache == null || s.CacheRect != r)
            {
                if (s.Cache != null) s.Cache.Dispose();
                int w = Math.Max(1, (r.Width + block - 1) / block), h = Math.Max(1, (r.Height + block - 1) / block);
                s.Cache = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
                using (var sg = Graphics.FromImage(s.Cache))
                {
                    sg.InterpolationMode = InterpolationMode.HighQualityBilinear;
                    sg.PixelOffsetMode = PixelOffsetMode.Half;
                    sg.DrawImage(shot, new Rectangle(0, 0, w, h), r, GraphicsUnit.Pixel);
                }
                s.CacheRect = r;
            }
            var st = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.SetClip(r, CombineMode.Intersect);
            g.DrawImage(s.Cache, new Rectangle(r.X, r.Y, s.Cache.Width * block, s.Cache.Height * block), new Rectangle(0, 0, s.Cache.Width, s.Cache.Height), GraphicsUnit.Pixel);
            g.Restore(st);
        }

        static Rectangle Norm(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        // ── где что ──
        Rectangle MonitorOf(Point p)
        {
            foreach (var m in monitors) if (m.Contains(p)) return m;
            return monitors.Count > 0 ? monitors[0] : new Rectangle(Point.Empty, ClientSize);
        }

        Rectangle ScreenOf(Rectangle r)
        {
            Rectangle best = MonitorOf(new Point(r.X + r.Width / 2, r.Y + r.Height / 2));
            return best;
        }

        Rectangle WindowAt(Point p)
        {
            foreach (var w in wins) if (w.Contains(p)) return w;
            return Rectangle.Empty;
        }

        Btn HitBtn(Point p)
        {
            if (sel.IsEmpty || drag == Drag.Select) return null;
            LayoutBars();
            foreach (var b in btns) if (b.R.Contains(p)) return b;
            return null;
        }

        int HitHandle(Point p)
        {
            var hs = Handles();
            for (int i = 0; i < hs.Length; i++) { var r = hs[i]; r.Inflate(4, 4); if (r.Contains(p)) return i; }
            // за край рамки тоже можно тянуть
            const int e = 4;
            bool inY = p.Y >= sel.Top - e && p.Y <= sel.Bottom + e, inX = p.X >= sel.Left - e && p.X <= sel.Right + e;
            if (inY && Math.Abs(p.X - sel.Left) <= e) return 7;
            if (inY && Math.Abs(p.X - (sel.Right - 1)) <= e) return 3;
            if (inX && Math.Abs(p.Y - sel.Top) <= e) return 1;
            if (inX && Math.Abs(p.Y - (sel.Bottom - 1)) <= e) return 5;
            return -1;
        }

        // ── мышь ──
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            var p = e.Location;
            mouse = p;
            if (e.Button == MouseButtons.Right) { RightClick(); return; }
            if (e.Button != MouseButtons.Left) return;

            if (palOpen)
            {
                LayoutBars();
                for (int i = 0; i <= Palette.Length; i++)
                    if (Swatch(i).Contains(p))
                    {
                        if (i < Palette.Length) SetColor(Palette[i]);
                        else PickCustomColor();
                        palOpen = false; Redraw(true);
                        return;
                    }
                palOpen = false;
                var pb = HitBtn(p);
                if (pb == null || pb.Id == "color") { Redraw(true); return; } // щелчок мимо или по «Цвет» — просто закрыть
            }

            var b = HitBtn(p);
            if (b != null) { Press(b); return; }

            if (editing != null) CommitText();

            if (!sel.IsEmpty)
            {
                // ручки выбранной фигуры — раньше ручек рамки: иначе у края рамки фигуру не растянуть
                if (tool == Tool.Hand && picked != null)
                {
                    int ph = HitPickHandle(p);
                    if (ph >= 0) { BeginScale(ph, p); return; }
                }
                int h = HitHandle(p);
                if (h >= 0) { drag = Drag.Resize; handle = h; selAtDown = sel; down = p; return; }
                if (sel.Contains(p) && tool == Tool.Hand)
                {
                    picked = HitShape(p);
                    if (picked != null) { drag = Drag.Pick; pickFrom = p; pickDX = pickDY = 0; }
                    Redraw(true);
                    return;
                }
                if (sel.Contains(p) && tool != Tool.None) { BeginShape(p); return; }
                if (sel.Contains(p)) { drag = Drag.Move; selAtDown = sel; down = p; Cursor = Cursors.SizeAll; return; }
                if (shapes.Count > 0) return; // уже нарисовано — рамку случайным щелчком не сбрасываем
            }
            drag = Drag.Select; down = p; moved = false;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var p = e.Location;
            mouse = p;
            switch (drag)
            {
                case Drag.Select:
                    if (!moved && (Math.Abs(p.X - down.X) > 3 || Math.Abs(p.Y - down.Y) > 3)) { moved = true; ClearShapes(); }
                    if (moved)
                    {
                        var r = Norm(down, p); r.Width += 1; r.Height += 1;
                        sel = Rectangle.Intersect(r, new Rectangle(Point.Empty, ClientSize));
                    }
                    break;
                case Drag.Move:
                    {
                        var r = selAtDown;
                        r.Offset(p.X - down.X, p.Y - down.Y);
                        r.X = Math.Max(0, Math.Min(ClientSize.Width - r.Width, r.X));
                        r.Y = Math.Max(0, Math.Min(ClientSize.Height - r.Height, r.Y));
                        sel = r;
                        break;
                    }
                case Drag.Resize: ResizeTo(p); break;
                case Drag.Scale: if (picked != null) ScaleTo(p); break;
                case Drag.Pick:
                    if (picked != null && (p.X != pickFrom.X || p.Y != pickFrom.Y))
                    {
                        int dx = p.X - pickFrom.X, dy = p.Y - pickFrom.Y;
                        MoveShape(picked, dx, dy);
                        pickDX += dx; pickDY += dy; pickFrom = p;
                    }
                    break;
                case Drag.Draw:
                    if (cur == null) break;
                    if (cur.Kind == Tool.Pencil || cur.Kind == Tool.Marker)
                    {
                        var last = cur.Pts[cur.Pts.Count - 1];
                        if (last != p) cur.Pts.Add(p);
                    }
                    else
                    {
                        var q = p;
                        if ((ModifierKeys & Keys.Shift) != 0) q = Constrain(cur.Kind, cur.Pts[0], p);
                        if (cur.Pts.Count < 2) cur.Pts.Add(q); else cur.Pts[1] = q;
                    }
                    break;
                default:
                    if (sel.IsEmpty) hover = WindowAt(p);
                    break;
            }
            UpdateCursor(p);
            Redraw(false);
        }

        // Shift: линия и стрелка — по 45°, прямоугольник и окружность — ровные
        static Point Constrain(Tool t, Point a, Point b)
        {
            int dx = b.X - a.X, dy = b.Y - a.Y;
            if (t == Tool.Rect || t == Tool.Ellipse || t == Tool.Blur || t == Tool.Highlight)
            {
                int s = Math.Max(Math.Abs(dx), Math.Abs(dy));
                return new Point(a.X + Math.Sign(dx == 0 ? 1 : dx) * s, a.Y + Math.Sign(dy == 0 ? 1 : dy) * s);
            }
            double ang = Math.Atan2(dy, dx), step = Math.PI / 4;
            ang = Math.Round(ang / step) * step;
            double len = Math.Sqrt(dx * dx + dy * dy);
            return new Point(a.X + (int)Math.Round(Math.Cos(ang) * len), a.Y + (int)Math.Round(Math.Sin(ang) * len));
        }

        void ResizeTo(Point p)
        {
            int l = selAtDown.Left, t = selAtDown.Top, r = selAtDown.Right, b = selAtDown.Bottom;
            int dx = p.X - down.X, dy = p.Y - down.Y;
            switch (handle)
            {
                case 0: l += dx; t += dy; break;
                case 1: t += dy; break;
                case 2: r += dx; t += dy; break;
                case 3: r += dx; break;
                case 4: r += dx; b += dy; break;
                case 5: b += dy; break;
                case 6: l += dx; b += dy; break;
                case 7: l += dx; break;
            }
            var n = Rectangle.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
            n = Rectangle.Intersect(n, new Rectangle(Point.Empty, ClientSize));
            if (n.Width < 1) n.Width = 1;
            if (n.Height < 1) n.Height = 1;
            sel = n;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            switch (drag)
            {
                case Drag.Select:
                    if (!moved)
                    {
                        // щелчок без протягивания — окно под курсором
                        var w = WindowAt(e.Location);
                        if (!w.IsEmpty)
                        {
                            sel = w; ClearShapes();
                            int wi = wins.IndexOf(w);
                            pickHwnd = wi >= 0 ? winH[wi] : IntPtr.Zero; pickHwndRect = w; // запись: именно это окно
                        }
                    }
                    else if (sel.Width < 3 || sel.Height < 3) sel = Rectangle.Empty;
                    hover = Rectangle.Empty;
                    break;
                case Drag.Draw:
                    if (cur != null)
                    {
                        bool keep = cur.Kind == Tool.Pencil || cur.Kind == Tool.Marker ||
                                    (cur.Pts.Count >= 2 && (Math.Abs(cur.Pts[1].X - cur.Pts[0].X) > 1 || Math.Abs(cur.Pts[1].Y - cur.Pts[0].Y) > 1));
                        if (keep) AddShape(cur);
                        cur = null;
                    }
                    break;
                case Drag.Scale: EndScale(); break;
                case Drag.Pick:
                    if (picked != null && (pickDX != 0 || pickDY != 0))
                    {
                        var s = picked; int dx = pickDX, dy = pickDY;
                        undo.Push(() => MoveShape(s, -dx, -dy));
                    }
                    break;
            }
            drag = Drag.None;
            UpdateCursor(e.Location);
            Redraw(true);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            // «Рука» или «Текст»: двойной щелчок по надписи — править её
            if (e.Button == MouseButtons.Left && (tool == Tool.Hand || tool == Tool.Text) && sel.Contains(e.Location))
            {
                var t = editing != null && HitOne(editing, e.Location) ? null : HitShape(e.Location);
                if (t != null && t.Kind == Tool.Text) EditText(t);
                return;
            }
            // двойной щелчок внутри рамки без инструмента — скопировать и закрыть
            if (e.Button == MouseButtons.Left && !sel.IsEmpty && tool == Tool.None && sel.Contains(e.Location) && HitBtn(e.Location) == null) Copy();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            // надпись, выбранная «Рукой» или открытая для правки, — колёсико меняет размер шрифта
            Shape txt = editing != null ? editing : (tool == Tool.Hand && picked != null && picked.Kind == Tool.Text ? picked : null);
            if (txt != null) { WheelText(txt, e.Delta > 0); return; }
            int nw = Math.Max(1, Math.Min(30, width + (e.Delta > 0 ? 1 : -1)));
            if (nw == width) return;
            width = nw;
            Flash("Толщина: " + width);
        }

        Shape wheelShape; DateTime wheelAt; // несколько щелчков колёсика подряд — один шаг для Ctrl+Z

        void WheelText(Shape s, bool up)
        {
            float old = s.Size, px = TextPx(s);
            float next = (float)Math.Round(Math.Max(8, Math.Min(400, up ? px * 1.1f + 1 : px / 1.1f - 1)));
            if (next == px) return;
            s.Size = next;
            // правку текста целиком отменяет Esc / Ctrl+Z при правке, отдельный шаг не нужен
            if (s != editing && (s != wheelShape || (DateTime.UtcNow - wheelAt).TotalMilliseconds > 900))
                undo.Push(() => s.Size = old);
            wheelShape = s; wheelAt = DateTime.UtcNow;
            Flash("Шрифт: " + (int)next + " px");
        }

        void BeginShape(Point p)
        {
            if (tool == Tool.Text)
            {
                // щелчок по своей надписи — править её, а не начинать новую
                var hit = HitShape(p);
                if (hit != null && hit.Kind == Tool.Text) { EditText(hit); return; }
                editIndex = -1;
                editing = new Shape { Kind = Tool.Text, Color = color, Width = width };
                editing.Pts.Add(p);
                caretOn = true; tick.Start();
                Redraw(true);
                return;
            }
            if (tool == Tool.Step)
            {
                int n = 1;
                foreach (var s in shapes) if (s.Kind == Tool.Step) n = Math.Max(n, s.Number + 1);
                var st = new Shape { Kind = Tool.Step, Color = color, Width = width, Number = n };
                st.Pts.Add(p);
                AddShape(st);
                Redraw(true);
                return;
            }
            cur = new Shape { Kind = tool, Color = color, Width = width };
            cur.Pts.Add(p);
            drag = Drag.Draw;
        }

        // Уже добавленную надпись снова открыть для правки: убираем её из списка, пока она редактируется
        void EditText(Shape s)
        {
            CommitText();
            int i = shapes.IndexOf(s);
            if (i < 0) return;
            shapes.RemoveAt(i);
            editIndex = i; editOld = s.Text;
            editing = s; picked = null;
            caretOn = true; tick.Start();
            Redraw(true);
        }

        void CommitText()
        {
            if (editing == null) return;
            var s = editing; editing = null;
            bool empty = s.Text.Trim().Length == 0;
            if (editIndex >= 0)
            {
                int idx = Math.Min(editIndex, shapes.Count); string old = editOld;
                editIndex = -1;
                if (empty)
                {
                    // стёрли надпись целиком — удалена; Ctrl+Z вернёт прежний текст
                    s.Text = old;
                    undo.Push(() => shapes.Insert(Math.Min(idx, shapes.Count), s));
                }
                else
                {
                    shapes.Insert(idx, s);
                    if (s.Text != old) undo.Push(() => s.Text = old);
                }
            }
            else if (!empty) AddShape(s);
            Redraw(true);
        }

        // отменить правку: новая надпись пропадает, старая возвращается как была
        void CancelEdit()
        {
            if (editing == null) return;
            var s = editing; editing = null;
            if (editIndex >= 0) { s.Text = editOld; shapes.Insert(Math.Min(editIndex, shapes.Count), s); editIndex = -1; }
            Redraw(true);
        }

        void RightClick()
        {
            if (editing != null) { CommitText(); return; }
            if (palOpen) { palOpen = false; Redraw(true); return; }
            if (tool != Tool.None) { tool = Tool.None; picked = null; UpdateCursor(mouse); Redraw(true); return; }
            if (!sel.IsEmpty && shapes.Count == 0) { sel = Rectangle.Empty; hover = WindowAt(mouse); Redraw(true); return; }
            if (sel.IsEmpty) Close();
        }

        void Press(Btn b)
        {
            switch (b.Id)
            {
                case "color": palOpen = !palOpen; break;
                case "undo": Undo(); break;
                case "copy": Copy(); return;
                case "rec": Copy(); return;
                case "save": Save(); return;
                case "close": Close(); return;
                default:
                    if (editing != null) CommitText();
                    tool = tool == b.T ? Tool.None : b.T;
                    if (tool != Tool.Hand) picked = null;
                    palOpen = false;
                    break;
            }
            Redraw(true);
        }

        void Undo()
        {
            if (editing != null) { CancelEdit(); return; }
            if (undo.Count == 0) return;
            undo.Pop()();
            if (picked != null && !shapes.Contains(picked)) picked = null;
            Redraw(true);
        }

        void SetColor(Color c)
        {
            color = Color.FromArgb(255, c.R, c.G, c.B);
            if (editing != null) editing.Color = color;
        }

        void PickCustomColor()
        {
            using (var d = new ColorDialog { Color = color, FullOpen = true, AnyColor = true })
                if (d.ShowDialog(this) == DialogResult.OK) SetColor(d.Color);
        }

        void UpdateCursor(Point p)
        {
            Cursor c = Aim;
            var b = drag == Drag.None ? HitBtn(p) : null;
            hotId = b == null ? null : b.Id;
            if (b != null || (palOpen && palRect.Contains(p))) c = Cursors.Hand;
            else if (!sel.IsEmpty && drag == Drag.None)
            {
                int h = tool == Tool.Hand ? HitPickHandle(p) : -1;
                if (h < 0) h = HitHandle(p);
                if (h == 0 || h == 4) c = Cursors.SizeNWSE;
                else if (h == 2 || h == 6) c = Cursors.SizeNESW;
                else if (h == 1 || h == 5) c = Cursors.SizeNS;
                else if (h == 3 || h == 7) c = Cursors.SizeWE;
                else if (sel.Contains(p))
                    c = tool == Tool.None ? Cursors.SizeAll
                      : tool == Tool.Hand ? (HitShape(p) != null ? Cursors.SizeAll : Cursors.Default)
                      : tool == Tool.Text ? Cursors.IBeam
                      : Aim;
            }
            else if (drag == Drag.Move || drag == Drag.Pick) c = Cursors.SizeAll;
            else if (drag == Drag.Scale || drag == Drag.Resize) c = Cursor; // во время растягивания курсор не меняем
            if (Cursor != c) Cursor = c;
        }

        // ── клавиатура ──
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            Keys code = keyData & Keys.KeyCode;
            bool ctrl = (keyData & Keys.Control) != 0, shift = (keyData & Keys.Shift) != 0;

            if (editing != null)
            {
                if (code == Keys.Escape) { CommitText(); return true; }
                if (ctrl && code == Keys.V)
                {
                    try { if (Clipboard.ContainsText()) { editing.Text += Clipboard.GetText().Replace("\r\n", "\n").Replace('\r', '\n'); Redraw(true); } } catch (Exception) { }
                    return true;
                }
                if (ctrl && code == Keys.Z) { Undo(); return true; }
                if (code == Keys.Enter && ctrl) { CommitText(); return true; }
                return base.ProcessCmdKey(ref msg, keyData); // буквы — в OnKeyPress
            }

            if (code == Keys.Escape) { Close(); return true; }
            if ((code == Keys.Delete || code == Keys.Back) && picked != null)
            {
                // удалить выбранную «Рукой» фигуру; Ctrl+Z вернёт
                var s = picked; int idx = shapes.IndexOf(s);
                if (idx >= 0) { shapes.RemoveAt(idx); undo.Push(() => shapes.Insert(Math.Min(idx, shapes.Count), s)); }
                picked = null; Redraw(true);
                return true;
            }
            if (picked != null && tool == Tool.Hand && !ctrl && (code == Keys.Left || code == Keys.Right || code == Keys.Up || code == Keys.Down))
            {
                int d = shift ? 10 : 1, dx = code == Keys.Left ? -d : code == Keys.Right ? d : 0, dy = code == Keys.Up ? -d : code == Keys.Down ? d : 0;
                var s = picked;
                MoveShape(s, dx, dy);
                undo.Push(() => MoveShape(s, -dx, -dy));
                Redraw(true);
                return true;
            }
            if (ctrl && code == Keys.C) { if (!sel.IsEmpty) Copy(); return true; }
            if (ctrl && code == Keys.S) { if (!sel.IsEmpty) Save(); return true; }
            if (ctrl && code == Keys.Z) { Undo(); return true; }
            if (code == Keys.Enter) { if (!sel.IsEmpty) Copy(); return true; }
            if (ctrl && code == Keys.A)
            {
                // первый раз — монитор под курсором, ещё раз — все мониторы
                var mon = MonitorOf(mouse);
                sel = sel == mon && monitors.Count > 1 ? new Rectangle(Point.Empty, ClientSize) : mon;
                ClearShapes(); hover = Rectangle.Empty;
                Redraw(true);
                return true;
            }
            if (code == Keys.C && !ctrl)
            {
                string hex = Hex(PixelAt(mouse));
                try { Clipboard.SetText(hex); Flash("Цвет " + hex + " скопирован"); } catch (Exception) { Flash("Буфер обмена занят"); }
                return true;
            }
            if (!sel.IsEmpty && (code == Keys.Left || code == Keys.Right || code == Keys.Up || code == Keys.Down))
            {
                int d = shift ? 10 : 1, dx = code == Keys.Left ? -d : code == Keys.Right ? d : 0, dy = code == Keys.Up ? -d : code == Keys.Down ? d : 0;
                var r = sel;
                if (ctrl) { r.Width = Math.Max(1, r.Width + dx); r.Height = Math.Max(1, r.Height + dy); }   // Ctrl — размер
                else r.Offset(dx, dy);
                r = Rectangle.Intersect(r, new Rectangle(Point.Empty, ClientSize));
                if (r.Width > 0 && r.Height > 0) sel = r;
                Redraw(true);
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            base.OnKeyPress(e);
            if (editing == null) return;
            char c = e.KeyChar;
            if (c == '\b') { if (editing.Text.Length > 0) editing.Text = editing.Text.Substring(0, editing.Text.Length - 1); }
            else if (c == '\r') editing.Text += "\n";
            else if (!char.IsControl(c)) editing.Text += c;
            else return;
            caretOn = true;
            e.Handled = true;
            Redraw(true);
        }

        // ── итог ──
        Bitmap Render()
        {
            CommitText();
            var b = Shooter.Flat(shot, sel);
            using (var g = Graphics.FromImage(b))
            {
                g.TranslateTransform(-sel.X, -sel.Y);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                DrawShapes(g, false);
            }
            return b;
        }

        void Copy()
        {
            if (sel.IsEmpty || done) return;
            if (pick != null)
            {
                // запись: окно — если выбрали щелчком и рамку потом не трогали
                var r = ToScreen(sel);
                IntPtr h = sel == pickHwndRect ? pickHwnd : IntPtr.Zero;
                done = true;
                Close();
                pick(r, h);
                return;
            }
            using (var b = Render())
            {
                bool ok = Shooter.ToClipboard(b);
                done = true;
                Close();
                notify(ok ? "Скриншот скопирован в буфер обмена" : "Не удалось скопировать: буфер обмена занят другой программой", null);
            }
        }

        void Save()
        {
            if (sel.IsEmpty || done || pick != null) return;
            string path;
            if (cfg.AskWhere)
            {
                using (var d = new SaveFileDialog
                {
                    Title = "Сохранить скриншот",
                    Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|BMP (*.bmp)|*.bmp",
                    FilterIndex = cfg.Format == "jpg" ? 2 : 1,
                    FileName = Shooter.DefaultName(cfg),
                    InitialDirectory = Directory.Exists(cfg.LastDir) ? cfg.LastDir : cfg.FolderPath,
                    AddExtension = true, OverwritePrompt = true
                })
                {
                    try { Directory.CreateDirectory(cfg.FolderPath); } catch (Exception) { }
                    if (d.ShowDialog(this) != DialogResult.OK) { Activate(); return; }
                    path = d.FileName;
                    try { cfg.LastDir = Path.GetDirectoryName(path); } catch (Exception) { }
                }
            }
            else
            {
                try { path = Shooter.NextPath(cfg); }
                catch (Exception ex) { Flash("Папка недоступна: " + ex.Message); return; }
            }
            try
            {
                using (var b = Render()) Shooter.SaveFile(b, path, cfg.JpegQuality);
            }
            catch (Exception ex) { Flash("Не сохранилось: " + ex.Message); return; }
            done = true;
            Close();
            notify("Скриншот сохранён: " + Path.GetFileName(path) + ". Щёлкните, чтобы открыть папку", path);
        }

        void Redraw(bool full)
        {
            if (full) { Invalidate(); lastDirty = Rectangle.Empty; return; }
            Rectangle now;
            using (var g = CreateGraphics()) now = Dirty(g);
            var r = lastDirty.IsEmpty ? now : Rectangle.Union(lastDirty, now);
            lastDirty = now;
            if (!r.IsEmpty) Invalidate(r);
        }

        // всё, что меняется при движении мыши
        Rectangle Dirty(Graphics g)
        {
            Rectangle d = Rectangle.Empty;
            Action<Rectangle> add = r => { if (!r.IsEmpty) d = d.IsEmpty ? r : Rectangle.Union(d, r); };
            if (!sel.IsEmpty)
            {
                var s = sel; s.Inflate(8, 8); add(s);
                add(SizeLabelRect(g));
                LayoutBars();
                var v = vbar; v.Inflate(2, 2); add(v);
                var h = hbar; h.Inflate(2, 2); add(h);
                add(palRect);
                var hb = Hot;
                if (hb != null) { var t = TipRect(g, hb); t.Inflate(2, 2); add(t); }
            }
            if (!hover.IsEmpty) { var h = hover; h.Inflate(3, 3); add(h); }
            var m = MagRect(); m.Inflate(2, 2); add(m);
            add(HintRect(g));
            if (cur != null)
            {
                var b = Norm(cur.Pts[0], cur.Pts[cur.Pts.Count - 1]);
                foreach (var p in cur.Pts) b = Rectangle.Union(b, new Rectangle(p, new Size(1, 1)));
                int pad = (cur.Width * 4 + 8) / 2 + 30; b.Inflate(pad, pad); add(b);
            }
            return d;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop(); tick.Dispose();
            if (!sel.IsEmpty && pick == null) { var s = ToScreen(sel); cfg.LastX = s.X; cfg.LastY = s.Y; cfg.LastW = s.Width; cfg.LastH = s.Height; }
            cfg.Color = color.ToArgb(); cfg.Width = width;
            try { cfg.Save(); } catch (Exception) { }
            foreach (var s in shapes) if (s.Cache != null) s.Cache.Dispose();
            if (shot != null) { shot.Dispose(); shot = null; }
            if (dim != null) { dim.Dispose(); dim = null; }
            ui.Dispose(); uiBold.Dispose(); uiSmall.Dispose();
            base.OnFormClosed(e);
        }
    }

    // Вывести окно вперёд, даже если Qural сейчас фоновая программа (кнопка деки)
    static class Fore
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        public static void Bring(IntPtr h)
        {
            IntPtr fg = GetForegroundWindow();
            uint pid, fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid), me = GetCurrentThreadId();
            if (fgThread != 0 && fgThread != me)
            {
                AttachThreadInput(me, fgThread, true);
                BringWindowToTop(h);
                SetForegroundWindow(h);
                AttachThreadInput(me, fgThread, false);
            }
            else SetForegroundWindow(h);
        }
    }
}
