using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Установка Qural")]
[assembly: AssemblyProduct("Qural")]

// Установщик Qural: один exe, внутри payload.zip (app\ — программа, plugins\ — плагины Ulanzi Studio).
// Ставит в %LOCALAPPDATA%\Programs\Qural без прав администратора, ярлык в «Пуске», запись в «Приложениях».
// Плагины Qural для Ulanzi Studio ставятся сами, если она есть; плагин Claude — только по выбору (меняет настройки Claude Code).
static class SetupApp
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    // QuralSetup.exe --update "<папка>" — обновление из Qural: ставит поверх с прежними параметрами и запускает Qural
    [STAThread]
    static void Main(string[] args)
    {
        try { SetProcessDPIAware(); } catch (Exception) { } // чёткий текст при масштабе 125–200 %
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string update = args.Length >= 2 && args[0] == "--update" && Directory.Exists(args[1]) ? args[1] : null;
        Application.Run(new SetupForm(update));
    }
}

// цвета как у Qural (GlassWise): светлая тема по умолчанию, тёмная — по кнопке в заголовке
sealed class Theme
{
    public Color Bg, Card, Label, Label2, Sep, Fill, Blue, Red, Warn, Green;
    public static readonly Theme Light = new Theme
    {
        Bg = Color.FromArgb(242, 244, 248), Card = Color.White, Label = Color.FromArgb(11, 18, 32), Label2 = Color.FromArgb(87, 93, 106),
        Sep = Color.FromArgb(223, 227, 234), Fill = Color.FromArgb(229, 232, 238), Blue = Color.FromArgb(0, 122, 255),
        Red = Color.FromArgb(255, 59, 48), Warn = Color.FromArgb(196, 110, 0), Green = Color.FromArgb(52, 199, 89)
    };
    public static readonly Theme Dark = new Theme
    {
        Bg = Color.FromArgb(11, 13, 20), Card = Color.FromArgb(25, 28, 37), Label = Color.White, Label2 = Color.FromArgb(170, 178, 196),
        Sep = Color.FromArgb(42, 46, 58), Fill = Color.FromArgb(38, 42, 54), Blue = Color.FromArgb(10, 132, 255),
        Red = Color.FromArgb(255, 69, 58), Warn = Color.FromArgb(255, 159, 10), Green = Color.FromArgb(48, 209, 88)
    };
}

static class Ui
{
    public static float K = 1f;   // масштаб экрана: 1 при 100 %, 1.5 при 150 %
    public static int S(float v) { return (int)Math.Round(v * K); }
    public static GraphicsPath Round(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
    public static void Smooth(Graphics g) { g.SmoothingMode = SmoothingMode.AntiAlias; g.PixelOffsetMode = PixelOffsetMode.HighQuality; g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; }
    public static Color Mix(Color a, Color b, float k) { return Color.FromArgb((int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k)); }
}

// всё нарисованное само: фон берёт у того, на чём лежит (форма или карточка)
class Painted : Control
{
    protected Theme T = Theme.Light;
    public Color Under = Theme.Light.Bg;
    protected bool hot, down;
    public Painted()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }
    public virtual void Apply(Theme t, Color under) { T = t; Under = under; Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hot = false; down = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); down = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); down = false; Invalidate(); }
    protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }
}

// кнопки в заголовке окна: тема, свернуть, закрыть (как в самом Qural)
sealed class TitleBtn : Painted
{
    public readonly string Kind;
    public TitleBtn(string kind) { Kind = kind; Size = new Size(Ui.S(36), Ui.S(30)); Cursor = Cursors.Hand; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Smooth(g); g.Clear(Under);
        bool close = Kind == "close";
        if (hot) using (var p = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Ui.S(8)))
            using (var b = new SolidBrush(close ? Ui.Mix(Under, T.Red, .16f) : T.Fill)) g.FillPath(b, p);
        Color c = hot ? (close ? T.Red : T.Label) : T.Label2;
        float cx = Width / 2f, cy = Height / 2f, r = Ui.S(5);
        using (var pen = new Pen(c, Math.Max(1f, Ui.K * 1.3f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            if (Kind == "min") g.DrawLine(pen, cx - r, cy, cx + r, cy);
            else if (close) { g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r); g.DrawLine(pen, cx + r, cy - r, cx - r, cy + r); }
            else
            {
                // тема: круг, наполовину залитый (как в Qural)
                float rr = Ui.S(7);
                using (var b = new SolidBrush(T.Blue)) g.FillPie(b, cx - rr, cy - rr, 2 * rr, 2 * rr, 90, 180);
                using (var p2 = new Pen(T.Blue, Math.Max(1f, Ui.K * 1.6f))) g.DrawEllipse(p2, cx - rr, cy - rr, 2 * rr, 2 * rr);
            }
        }
    }
}

// кнопка-«таблетка»: главная — синяя, обычная — серая
sealed class Pill : Painted
{
    public bool Main;
    public Pill(string text, bool main) { Text = text; Main = main; Cursor = Cursors.Hand; }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Smooth(g); g.Clear(Under);
        Color fill = Main ? T.Blue : T.Fill;
        if (Enabled && (hot || down)) fill = Main ? Ui.Mix(fill, Color.Black, down ? .18f : .08f) : Ui.Mix(fill, T.Label, down ? .14f : .07f);
        if (!Enabled) fill = Ui.Mix(fill, Under, .45f);
        using (var p = Ui.Round(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f)) using (var b = new SolidBrush(fill)) g.FillPath(b, p);
        Color tc = Main ? Color.White : T.Label; if (!Enabled) tc = Ui.Mix(tc, Under, .4f);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, tc, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}

// строка настройки: название, пояснение мелко, переключатель справа (как gw-opt + gw-switch)
sealed class SwitchRow : Painted
{
    bool on; public string Sub = "";
    public event Action Changed;
    public bool Checked { get { return on; } set { if (on == value) return; on = value; Invalidate(); if (Changed != null) Changed(); } }
    public SwitchRow(string text, string sub, bool on) { Text = text; Sub = sub ?? ""; this.on = on; Cursor = Cursors.Hand; }
    protected override void OnClick(EventArgs e) { base.OnClick(e); if (Enabled) Checked = !Checked; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Smooth(g); g.Clear(Under);
        int pad = Ui.S(12), sw = Ui.S(42), sh = Ui.S(25);
        var textR = new Rectangle(pad, 0, Width - pad * 2 - sw - Ui.S(12), Height);
        Color lc = Enabled ? T.Label : T.Label2;
        using (var f2 = new Font(Font.FontFamily, Font.Size * .86f))
        {
            if (Sub.Length > 0)
            {
                int h1 = TextRenderer.MeasureText(g, "Ag", Font).Height, h2 = TextRenderer.MeasureText(g, "Ag", f2).Height;
                int top = (Height - h1 - h2) / 2;
                TextRenderer.DrawText(g, Text, Font, new Rectangle(textR.X, top, textR.Width, h1), lc, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, Sub, f2, new Rectangle(textR.X, top + h1, textR.Width, h2), T.Label2, TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
            else TextRenderer.DrawText(g, Text, Font, textR, lc, TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
        var tr = new RectangleF(Width - pad - sw, (Height - sh) / 2f, sw, sh);
        Color track = on ? T.Green : Ui.Mix(T.Fill, T.Label, .08f);
        if (!Enabled) track = Ui.Mix(track, Under, .5f);
        using (var p = Ui.Round(tr, sh / 2f)) using (var b = new SolidBrush(track)) g.FillPath(b, p);
        float d = sh - Ui.S(4), x = on ? tr.Right - d - Ui.S(2) : tr.X + Ui.S(2);
        using (var sb = new SolidBrush(Color.FromArgb(40, 0, 0, 0))) g.FillEllipse(sb, x, tr.Y + Ui.S(2) + Ui.K, d, d);
        using (var b = new SolidBrush(Color.White)) g.FillEllipse(b, x, tr.Y + Ui.S(2), d, d);
    }
}

// тонкая полоса прогресса
sealed class Bar : Painted
{
    int v; public int Value { get { return v; } set { v = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Smooth(g); g.Clear(Under);
        var r = new RectangleF(0, 0, Width - 1, Height - 1);
        using (var p = Ui.Round(r, Height / 2f)) using (var b = new SolidBrush(T.Fill)) g.FillPath(b, p);
        if (v > 0) using (var p = Ui.Round(new RectangleF(0, 0, Math.Max(Height, (Width - 1) * v / 100f), Height - 1), Height / 2f)) using (var b = new SolidBrush(T.Blue)) g.FillPath(b, p);
    }
}

// карточка: белая скруглённая плашка с тонкой рамкой, внутри — строки
sealed class Card : Panel
{
    Theme T = Theme.Light; public Color Under = Theme.Light.Bg;
    public readonly List<int> Lines = new List<int>();   // где провести разделители (y)
    public Card() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true); }
    public void Apply(Theme t, Color under) { T = t; Under = under; BackColor = t.Card; Invalidate(); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; Ui.Smooth(g); g.Clear(Under);
        using (var p = Ui.Round(new RectangleF(.5f, .5f, Width - 2, Height - 2), Ui.S(14)))
        {
            using (var b = new SolidBrush(T.Card)) g.FillPath(b, p);
            using (var pen = new Pen(T.Sep, 1f)) g.DrawPath(pen, p);
        }
        using (var pen = new Pen(T.Sep, 1f)) foreach (int y in Lines) g.DrawLine(pen, Ui.S(16), y, Width - Ui.S(16), y);
    }
}

class SetupForm : Form
{
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Qural";
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    Theme T = Theme.Light;
    readonly TextBox path = new TextBox();
    readonly SwitchRow desktop, claude;
    readonly Pill go, browse;
    readonly TitleBtn themeBtn = new TitleBtn("theme"), minBtn = new TitleBtn("min"), closeBtn = new TitleBtn("close");
    readonly Bar bar = new Bar();
    readonly Label heading = new Label(), sub = new Label(), folderL = new Label(), optL = new Label(), note = new Label(), status = new Label(), warn = new Label();
    readonly Card folderCard = new Card(), optCard = new Card();
    readonly bool hasUlanzi;
    readonly Icon appIcon;
    readonly int titleH;
    readonly string updateDir;   // не null — обновление: всё само, без вопросов
    string installedExe;

    string UlanziPlugins { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ulanzi", "UlanziDeck", "Plugins"); } }
    static string UlanziExe { get { return @"C:\Program Files (x86)\Ulanzi Studio\UlanziDeck.exe"; } }

    // масштаб — до создания полей-кнопок (их размеры считаются через Ui.S)
    static SetupForm() { using (var g = Graphics.FromHwnd(IntPtr.Zero)) Ui.K = g.DpiX / 96f; }

    public SetupForm(string updateDir)
    {
        this.updateDir = updateDir;
        Text = "Установка Qural " + Version.Text;
        Font = new Font("Segoe UI", 10f);
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.None;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); Icon = appIcon; } catch (Exception) { }
        hasUlanzi = File.Exists(UlanziExe) || Directory.Exists(UlanziPlugins);

        int W = Ui.S(560), pad = Ui.S(28), inner = W - 2 * pad;
        titleH = Ui.S(48);

        // заголовок окна: кнопки справа
        int bx = W - Ui.S(10);
        foreach (var b in new[] { closeBtn, minBtn, themeBtn }) { bx -= b.Width + Ui.S(2); b.Location = new Point(bx, (titleH - b.Height) / 2); }
        themeBtn.Click += (s, e) => ApplyTheme(T == Theme.Light ? Theme.Dark : Theme.Light);
        minBtn.Click += (s, e) => WindowState = FormWindowState.Minimized;
        closeBtn.Click += (s, e) => Close();
        new ToolTip().SetToolTip(themeBtn, "Светлая / тёмная тема");

        int y = titleH + Ui.S(14);
        heading.Text = "Установка Qural"; heading.Font = new Font("Segoe UI Semibold", 17f); heading.AutoSize = true; heading.Location = new Point(pad - Ui.S(2), y);
        y += Ui.S(40);
        sub.Text = "Цвет мониторов, громкость программ, скриншоты и запись экрана — в одной иконке в трее. Версия " + Version.Text + ".";
        sub.AutoSize = false; sub.Size = new Size(inner, Ui.S(42)); sub.Location = new Point(pad, y);
        y += Ui.S(54);

        folderL.Text = "Папка установки"; folderL.AutoSize = true; folderL.Font = new Font("Segoe UI Semibold", 9.5f); folderL.Location = new Point(pad + Ui.S(4), y);
        y += Ui.S(24);
        folderCard.Location = new Point(pad, y); folderCard.Size = new Size(inner, Ui.S(52));
        browse = new Pill("Изменить…", false) { Size = new Size(Ui.S(104), Ui.S(32)) };
        browse.Location = new Point(inner - browse.Width - Ui.S(10), (folderCard.Height - browse.Height) / 2);
        path.BorderStyle = BorderStyle.None; path.Font = new Font("Segoe UI", 10f);
        path.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Qural");
        path.Location = new Point(Ui.S(16), (folderCard.Height - path.PreferredHeight) / 2); path.Width = browse.Left - Ui.S(28);
        folderCard.Controls.AddRange(new Control[] { path, browse });
        browse.Click += (s, e) =>
        {
            using (var d = new FolderBrowserDialog { Description = "Куда установить Qural", ShowNewFolderButton = true })
                if (d.ShowDialog(this) == DialogResult.OK) path.Text = Path.Combine(d.SelectedPath, "Qural");
        };
        y += folderCard.Height + Ui.S(18);

        optL.Text = "Параметры"; optL.AutoSize = true; optL.Font = folderL.Font; optL.Location = new Point(pad + Ui.S(4), y);
        y += Ui.S(24);
        int rowH = Ui.S(58);
        desktop = new SwitchRow("Ярлык на рабочем столе", "Ярлык в меню «Пуск» появится всегда", true);
        claude = new SwitchRow("Плагин Claude для деки", hasUlanzi ? "Кнопки управления Claude Code; добавит хуки в его настройки" : "Нужна Ulanzi Studio — она не найдена", false);
        claude.Enabled = hasUlanzi;
        // строки с отступом от краёв — не закрывают скруглённые углы карточки
        int top = Ui.S(5), side = Ui.S(4);
        desktop.Location = new Point(side, top); desktop.Size = new Size(inner - 2 * side, rowH - 1);
        // плагин Claude в окне не показываем: ставится только при обновлении, если уже был установлен
        claude.Visible = false;
        optCard.Controls.Add(desktop);
        optCard.Location = new Point(pad, y); optCard.Size = new Size(inner, rowH + 2 * top);
        y += optCard.Height + Ui.S(8);

        note.AutoSize = false; note.Size = new Size(inner - Ui.S(8), Ui.S(22)); note.Location = new Point(pad + Ui.S(4), y);
        note.Font = new Font("Segoe UI", 9f);
        note.Text = hasUlanzi ? "Плагины Qural для Ulanzi Studio (громкость, цвет, скриншот, запись) установятся сами." : "";
        y += Ui.S(26);

        warn.AutoSize = false; warn.Size = new Size(inner - Ui.S(8), 0); warn.Location = new Point(pad + Ui.S(4), y); warn.Font = note.Font;
        if (!HasWebView2())
        {
            warn.Text = "Нет Microsoft Edge WebView2 Runtime — без него не откроется окно настроек. Нажмите, чтобы скачать у Microsoft.";
            warn.Height = Ui.S(40); warn.Cursor = Cursors.Hand;
            warn.Click += (s, e) => { try { Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703"); } catch (Exception) { } };
            y += Ui.S(44);
        }

        y += Ui.S(10);
        bar.Location = new Point(pad, y); bar.Size = new Size(inner, Ui.S(6)); bar.Visible = false;
        y += Ui.S(20);
        go = new Pill("Установить", true) { Size = new Size(Ui.S(170), Ui.S(42)), Font = new Font("Segoe UI Semibold", 10.5f) };
        go.Location = new Point(W - pad - go.Width, y);
        status.AutoSize = false; status.Size = new Size(inner - go.Width - Ui.S(16), Ui.S(42)); status.Location = new Point(pad, y);
        status.TextAlign = ContentAlignment.MiddleLeft; status.Font = note.Font;
        go.Click += (s, e) => { if (installedExe != null) { Launch(); return; } Install(); };
        y += go.Height + pad;

        if (updateDir != null)
        {
            // обновление: та же папка и те же параметры, что при установке
            heading.Text = "Обновление Qural";
            sub.Text = "Ставлю версию " + Version.Text + " поверх установленной — настройки сохранятся. Qural запустится сам.";
            path.Text = updateDir; path.ReadOnly = true;
            desktop.Checked = File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Qural.lnk"));
            string marker = Path.Combine(updateDir, "install.txt");
            try { claude.Checked = hasUlanzi && File.Exists(marker) && File.ReadAllText(marker).IndexOf("com.qural.claude", StringComparison.OrdinalIgnoreCase) >= 0; } catch (Exception) { }
        }
        ClientSize = new Size(W, y);
        Controls.AddRange(new Control[] { themeBtn, minBtn, closeBtn, heading, sub, folderL, folderCard, optL, optCard, note, warn, bar, status, go });
        ApplyTheme(Theme.Light);
    }

    void ApplyTheme(Theme t)
    {
        T = t;
        BackColor = t.Bg; ForeColor = t.Label;
        foreach (var b in new[] { themeBtn, minBtn, closeBtn }) b.Apply(t, t.Bg);
        foreach (var l in new[] { heading, folderL, optL }) { l.BackColor = t.Bg; l.ForeColor = t.Label; }
        foreach (var l in new[] { sub, note, status }) { l.BackColor = t.Bg; l.ForeColor = t.Label2; }
        warn.BackColor = t.Bg; warn.ForeColor = t.Warn;
        folderCard.Apply(t, t.Bg); optCard.Apply(t, t.Bg);
        path.BackColor = t.Card; path.ForeColor = t.Label;
        browse.Apply(t, t.Card); desktop.Apply(t, t.Card); claude.Apply(t, t.Card);
        bar.Apply(t, t.Bg); go.Apply(t, t.Bg);
        if (IsHandleCreated) DarkFrame();
        Invalidate(true);
    }

    // Windows 11: скруглённые углы и рамка окна в цвет темы
    void DarkFrame()
    {
        try
        {
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
            int border = ColorTranslator.ToWin32(T.Sep); DwmSetWindowAttribute(Handle, 34, ref border, 4);
        }
        catch (Exception) { }
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); DarkFrame(); }
    protected override void OnShown(EventArgs e) { base.OnShown(e); if (updateDir != null) Install(); }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.Style |= 0x00020000 /*MINIMIZEBOX: сворачивается с панели задач*/ | 0x00080000 /*SYSMENU*/;
            cp.ClassStyle |= 0x00020000; // тень
            return cp;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics; Ui.Smooth(g);
        // значок и «Qural» слева в заголовке — как в окне Qural
        int x = Ui.S(18), sz = Ui.S(20);
        if (appIcon != null) using (var ic = new Icon(appIcon, sz, sz)) g.DrawIcon(ic, new Rectangle(x, (titleH - sz) / 2, sz, sz));
        using (var f = new Font("Segoe UI Semibold", 10.5f))
            TextRenderer.DrawText(g, "Qural", f, new Rectangle(x + sz + Ui.S(8), 0, Ui.S(200), titleH), T.Label, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        using (var pen = new Pen(T.Sep, 1f)) g.DrawLine(pen, 0, titleH - 1, Width, titleH - 1);
        if (Environment.OSVersion.Version.Build < 22000) using (var pen = new Pen(T.Sep, 1f)) g.DrawRectangle(pen, 0, 0, Width - 1, Height - 1); // Windows 10: рамки нет — рисуем свою
    }

    // перетаскивание за заголовок (и за пустое место окна)
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        ReleaseCapture(); SendMessage(Handle, 0xA1 /*WM_NCLBUTTONDOWN*/, (IntPtr)2 /*HTCAPTION*/, IntPtr.Zero);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape && go.Enabled && !bar.Visible) Close();
        else if (e.KeyCode == Keys.Enter && go.Enabled) { e.Handled = true; if (installedExe != null) Launch(); else Install(); }
    }

    static bool HasWebView2()
    {
        const string id = @"\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var k in new[] { @"SOFTWARE\WOW6432Node" + id, @"SOFTWARE" + id })
        {
            try
            {
                using (var r = Registry.LocalMachine.OpenSubKey(k)) if (r != null && (r.GetValue("pv") as string ?? "0.0.0.0") != "0.0.0.0") return true;
                using (var r = Registry.CurrentUser.OpenSubKey(k)) if (r != null && (r.GetValue("pv") as string ?? "0.0.0.0") != "0.0.0.0") return true;
            }
            catch (Exception) { }
        }
        return false;
    }

    void Lock(bool locked)
    {
        go.Enabled = !locked; browse.Enabled = path.Enabled = desktop.Enabled = !locked;
        claude.Enabled = !locked && hasUlanzi;
    }

    void Install()
    {
        string dir = path.Text.Trim();
        if (dir.Length < 4) { status.Text = "Укажите папку"; return; }
        Lock(true);
        bar.Visible = true; bar.Value = 0;
        bool wantDesktop = desktop.Checked, wantPlugins = hasUlanzi, wantClaude = hasUlanzi && claude.Checked;
        var t = new Thread(() =>
        {
            string err = null;
            try { DoInstall(dir, wantDesktop, wantPlugins, wantClaude); }
            catch (Exception ex) { err = ex.Message; }
            BeginInvoke(new Action(() =>
            {
                bar.Visible = false;
                if (err != null) { status.ForeColor = T.Red; status.Text = "Не получилось: " + err; Lock(false); return; }
                installedExe = Path.Combine(dir, "Qural.exe");
                status.ForeColor = T.Label2; status.Text = updateDir != null ? "Готово — Qural обновлён." : "Готово — Qural установлен.";
                go.Text = "Запустить Qural"; go.Enabled = true;
                if (updateDir != null) { var tm = new System.Windows.Forms.Timer { Interval = 900 }; tm.Tick += (s2, e2) => { tm.Stop(); Launch(); }; tm.Start(); }
            }));
        }) { IsBackground = true };
        t.Start();
    }

    void Progress(string text, int p) { BeginInvoke(new Action(() => { status.Text = text; bar.Value = p; })); }

    void DoInstall(string dir, bool wantDesktop, bool wantPlugins, bool wantClaude)
    {
        // Qural из этой папки работает — закрыть, иначе файлы заняты
        Progress("Закрываю запущенный Qural…", 2);
        foreach (var name in new[] { "Qural", "ffmpeg" })
            foreach (var p in Process.GetProcessesByName(name))
                try { if (p.MainModule.FileName.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(5000); } } catch (Exception) { }

        Directory.CreateDirectory(dir);
        var log = new List<string> { "version=" + Version.Text, "installed=" + DateTime.Now.ToString("s") };
        bool ulanziWasRunning = false;

        using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
        using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
        {
            var app = new List<ZipArchiveEntry>(); var plug = new List<ZipArchiveEntry>();
            foreach (var e in zip.Entries)
            {
                string n = EntryName(e);
                if (n.StartsWith("app/")) app.Add(e);
                else if (n.StartsWith("plugins/")) plug.Add(e);
            }
            int done = 0, total = app.Count + (wantPlugins ? plug.Count : 0);
            foreach (var e in app)
            {
                Extract(e, Path.Combine(dir, EntryName(e).Substring(4).Replace('/', Path.DirectorySeparatorChar)));
                if (++done % 5 == 0) Progress("Копирую файлы… " + e.Name, 5 + 85 * done / Math.Max(1, total));
            }

            if (wantPlugins && plug.Count > 0)
            {
                // Ulanzi Studio держит файлы плагинов — закрыть и потом запустить снова
                foreach (var p in Process.GetProcessesByName("UlanziDeck")) { ulanziWasRunning = true; try { p.Kill(); p.WaitForExit(5000); } catch (Exception) { } }
                KillPluginNodes();
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var e in plug) { var parts = EntryName(e).Split('/'); if (parts.Length > 2) names.Add(parts[1]); }
                foreach (var n in names)
                {
                    if (n.IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 && !wantClaude) continue;
                    string dst = Path.Combine(UlanziPlugins, n);
                    try { if (Directory.Exists(dst)) Directory.Delete(dst, true); } catch (Exception) { }
                    log.Add("plugin=" + dst);
                }
                foreach (var e in plug)
                {
                    var parts = EntryName(e).Split('/');
                    if (parts.Length < 3) continue;
                    if (parts[1].IndexOf("claude", StringComparison.OrdinalIgnoreCase) >= 0 && !wantClaude) continue;
                    Extract(e, Path.Combine(UlanziPlugins, string.Join(Path.DirectorySeparatorChar.ToString(), parts, 1, parts.Length - 1)));
                    if (++done % 5 == 0) Progress("Плагины для деки… " + e.Name, 5 + 85 * done / Math.Max(1, total));
                }
                // хуки Claude Code — тем же скриптом, что и install.ps1
                string hook = Path.Combine(UlanziPlugins, "com.qural.claude.ulanziPlugin", "hook", "setup.cjs");
                string node = @"C:\Program Files (x86)\Ulanzi Studio\nodejs\node.exe";
                if (wantClaude && File.Exists(hook) && File.Exists(node))
                    try { using (var p = Process.Start(new ProcessStartInfo(node, "\"" + hook + "\" install") { CreateNoWindow = true, UseShellExecute = false })) p.WaitForExit(15000); } catch (Exception) { }
            }
        }

        Progress("Ярлыки…", 92);
        string exe = Path.Combine(dir, "Qural.exe");
        string start = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Qural.lnk");
        Shortcut(start, exe, dir); log.Add("shortcut=" + start);
        if (wantDesktop) { string d = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Qural.lnk"); Shortcut(d, exe, dir); log.Add("shortcut=" + d); }
        File.WriteAllLines(Path.Combine(dir, "install.txt"), log.ToArray());

        // «Приложения» Windows: название, версия, размер, кнопка «Удалить»
        long size = 0; foreach (var f in new DirectoryInfo(dir).GetFiles("*", SearchOption.AllDirectories)) size += f.Length;
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", "Qural");
            k.SetValue("DisplayVersion", Version.Text);
            k.SetValue("Publisher", "NAGIez");
            k.SetValue("InstallLocation", dir);
            k.SetValue("DisplayIcon", exe + ",0");
            k.SetValue("UninstallString", "\"" + exe + "\" --uninstall");
            k.SetValue("EstimatedSize", (int)(size / 1024), RegistryValueKind.DWord);
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        if (ulanziWasRunning && File.Exists(UlanziExe)) try { Process.Start(UlanziExe, "--autorun"); } catch (Exception) { }
        Progress("Готово", 100);
    }

    // в архивах, собранных .NET Framework, разделитель — «\»
    static string EntryName(ZipArchiveEntry e) { return e.FullName.Replace('\\', '/'); }

    static void Extract(ZipArchiveEntry e, string dst)
    {
        if (EntryName(e).EndsWith("/")) { Directory.CreateDirectory(dst); return; }
        Directory.CreateDirectory(Path.GetDirectoryName(dst));
        for (int i = 0; ; i++)
        {
            try { using (var src = e.Open()) using (var f = File.Create(dst)) src.CopyTo(f); return; }
            catch (IOException) { if (i >= 10) throw; Thread.Sleep(300); } // файл ещё занят закрывающимся процессом
        }
    }

    // процессы Node плагинов Qural (их запускает Ulanzi Studio) держат файлы
    static void KillPluginNodes()
    {
        try
        {
            using (var q = new System.Management.ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='node.exe'"))
                foreach (System.Management.ManagementObject o in q.Get())
                {
                    string cmd = o["CommandLine"] as string ?? "";
                    if (cmd.IndexOf("com.qural.", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { Process.GetProcessById(Convert.ToInt32(o["ProcessId"])).Kill(); } catch (Exception) { }
                }
        }
        catch (Exception) { }
    }

    static void Shortcut(string lnk, string target, string workDir)
    {
        var t = Type.GetTypeFromProgID("WScript.Shell");
        object sh = Activator.CreateInstance(t);
        object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { lnk });
        var st = sc.GetType();
        st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
        st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
        st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Qural — цвет, звук, скриншоты, запись экрана" });
        st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
        st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
    }

    void Launch()
    {
        try { Process.Start(new ProcessStartInfo(installedExe) { WorkingDirectory = Path.GetDirectoryName(installedExe) }); } catch (Exception) { }
        Close();
    }
}
