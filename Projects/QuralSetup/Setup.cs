using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Установка Qural")]
[assembly: AssemblyProduct("Qural")]

// Установщик Qural: один exe, внутри payload.zip (app\ — программа, plugins\ — плагины Ulanzi Studio).
// Ставит в %LOCALAPPDATA%\Programs\Qural без прав администратора, ярлык в «Пуске», запись в «Приложениях».
static class SetupApp
{
    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new SetupForm());
    }
}

class SetupForm : Form
{
    static readonly Color Bg = Color.FromArgb(11, 13, 20), Card = Color.FromArgb(24, 27, 36), Label2 = Color.FromArgb(170, 180, 200), Blue = Color.FromArgb(10, 132, 255);
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Qural";

    readonly TextBox path = new TextBox();
    readonly CheckBox desktop = new CheckBox(), plugins = new CheckBox(), claude = new CheckBox();
    readonly Button go = new Button(), browse = new Button();
    readonly ProgressBar bar = new ProgressBar();
    readonly Label status = new Label(), warn = new Label();
    string installedExe;

    string UlanziPlugins { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ulanzi", "UlanziDeck", "Plugins"); } }
    static string UlanziExe { get { return @"C:\Program Files (x86)\Ulanzi Studio\UlanziDeck.exe"; } }

    public SetupForm()
    {
        Text = "Установка Qural " + Version.Text;
        Font = new Font("Segoe UI", 9.5f);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(560, 430);
        BackColor = Bg; ForeColor = Color.White;
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

        var title = new Label { Text = "Qural", Font = new Font("Segoe UI Semibold", 24f), AutoSize = true, Location = new Point(28, 22) };
        var sub = new Label { Text = "Цвет мониторов, громкость программ, скриншоты и запись экрана — в одной иконке в трее.", ForeColor = Label2, AutoSize = false, Size = new Size(500, 40), Location = new Point(30, 70) };
        var pl = new Label { Text = "Папка", ForeColor = Label2, AutoSize = true, Location = new Point(30, 118) };
        path.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Qural");
        path.Location = new Point(30, 140); path.Size = new Size(400, 26); path.BackColor = Card; path.ForeColor = Color.White; path.BorderStyle = BorderStyle.FixedSingle;
        Style(browse, "Изменить…", false); browse.Location = new Point(440, 138); browse.Size = new Size(90, 30);
        browse.Click += (s, e) =>
        {
            using (var d = new FolderBrowserDialog { Description = "Куда установить Qural", ShowNewFolderButton = true })
                if (d.ShowDialog(this) == DialogResult.OK) path.Text = Path.Combine(d.SelectedPath, "Qural");
        };

        Check(desktop, "Ярлык на рабочем столе", 186, true);
        bool hasUlanzi = File.Exists(UlanziExe) || Directory.Exists(UlanziPlugins);
        Check(plugins, "Плагины для Ulanzi Studio: громкость, цвет, скриншот, запись, Discord", 214, hasUlanzi);
        Check(claude, "Плагин Claude для деки (добавит хуки в настройки Claude Code)", 242, false);
        plugins.Enabled = claude.Enabled = hasUlanzi;
        if (!hasUlanzi) plugins.Text += " — Ulanzi Studio не найдена";

        warn.ForeColor = Color.FromArgb(255, 159, 10); warn.AutoSize = false; warn.Size = new Size(500, 40); warn.Location = new Point(30, 276);
        if (!HasWebView2())
        {
            warn.Text = "Нет Microsoft Edge WebView2 Runtime — без него не откроется окно настроек. Нажмите сюда, чтобы скачать у Microsoft.";
            warn.Cursor = Cursors.Hand;
            warn.Click += (s, e) => { try { Process.Start("https://go.microsoft.com/fwlink/p/?LinkId=2124703"); } catch (Exception) { } };
        }

        bar.Location = new Point(30, 326); bar.Size = new Size(500, 8); bar.Visible = false;
        status.ForeColor = Label2; status.AutoSize = false; status.Size = new Size(330, 40); status.Location = new Point(30, 362);
        Style(go, "Установить", true); go.Location = new Point(380, 360); go.Size = new Size(150, 40);
        go.Click += (s, e) => { if (installedExe != null) { Launch(); return; } Install(); };
        AcceptButton = go;

        Controls.AddRange(new Control[] { title, sub, pl, path, browse, desktop, plugins, claude, warn, bar, status, go });
    }

    void Check(CheckBox c, string text, int y, bool on)
    {
        c.Text = text; c.Checked = on; c.AutoSize = true; c.Location = new Point(30, y); c.ForeColor = Color.White; c.FlatStyle = FlatStyle.System;
    }

    static void Style(Button b, string text, bool main)
    {
        b.Text = text; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.Cursor = Cursors.Hand;
        b.BackColor = main ? Blue : Color.FromArgb(40, 44, 56); b.ForeColor = Color.White;
        if (main) b.Font = new Font("Segoe UI Semibold", 10.5f);
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

    void Install()
    {
        string dir = path.Text.Trim();
        if (dir.Length < 4) { status.Text = "Укажите папку"; return; }
        go.Enabled = browse.Enabled = path.Enabled = desktop.Enabled = plugins.Enabled = claude.Enabled = false;
        bar.Visible = true; bar.Value = 0;
        bool wantDesktop = desktop.Checked, wantPlugins = plugins.Checked, wantClaude = claude.Checked;
        var t = new Thread(() =>
        {
            string err = null;
            try { DoInstall(dir, wantDesktop, wantPlugins, wantClaude); }
            catch (Exception ex) { err = ex.Message; }
            BeginInvoke(new Action(() =>
            {
                bar.Visible = false;
                if (err != null)
                {
                    status.Text = "Не получилось: " + err;
                    go.Enabled = browse.Enabled = path.Enabled = desktop.Enabled = true; plugins.Enabled = claude.Enabled = Directory.Exists(UlanziPlugins);
                    return;
                }
                installedExe = Path.Combine(dir, "Qural.exe");
                status.Text = "Готово. Qural установлен в\n" + dir;
                go.Text = "Запустить Qural"; go.Enabled = true;
            }));
        }) { IsBackground = true };
        t.Start();
    }

    void Progress(string text, int p) { BeginInvoke(new Action(() => { status.Text = text; bar.Value = Math.Max(0, Math.Min(100, p)); })); }

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
