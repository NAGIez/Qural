using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

namespace Qural
{
    // Окно Qural: страница в оформлении GlassWise (папка ui) внутри WebView2, со своей шапкой.
    // Пока окно закрыто, процессов WebView2 нет и процессор не тратится; в Qural остаются только загруженные DLL WebView2.
    partial class MainWindow : Form
    {
        const string Host = "qural.ui";

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("dwmapi.dll")] static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern bool IsZoomed(IntPtr h);
        [StructLayout(LayoutKind.Sequential)] struct Margins { public int Left, Right, Top, Bottom; }
        [StructLayout(LayoutKind.Sequential)] struct Rect { public int L, T, R, B; }
        const int WM_NCCALCSIZE = 0x83, WM_NCLBUTTONDOWN = 0xA1;

        readonly TrayApp app;
        readonly WebView2 web = new WebView2();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly Timer saveTimer = new Timer { Interval = 500 };
        readonly Timer live = new Timer { Interval = 1000 };   // живые уровни звука — только пока окно открыто и не свёрнуто
        bool ready;
        Rectangle normalBounds;
        FormWindowState lastState = FormWindowState.Normal;

        public MainWindow(TrayApp app)
        {
            this.app = app;
            Text = "Qural";
            Icon = AppIcon.Get(SystemInformation.IconSize);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1000, 720);
            MinimumSize = new Size(860, 600);

            bool dark = SystemDark();
            BackColor = dark ? System.Drawing.Color.FromArgb(11, 13, 20) : System.Drawing.Color.FromArgb(242, 244, 248);
            web.DefaultBackgroundColor = BackColor;
            web.Dock = DockStyle.Fill;
            Controls.Add(web);

            saveTimer.Tick += (s, e) => FlushSaves();
            live.Tick += (s, e) => { if (WindowState != FormWindowState.Minimized) PushSoundLive(); };
            HandleCreated += (s, e) => SetDarkTitle(dark);
            Resize += (s, e) => { TrackBounds(); if (ready) PostWindowState(); SleepWhenMinimized(); UpdateLiveTimer(); };
            Move += (s, e) => TrackBounds();
            // при возврате в окно сверить список мониторов (на случай, если Windows не прислала событие) и громкость
            Activated += (s, e) => { isActive = true; UpdateLiveTimer(); if (ready) { PostWindowState(); SendColor(); PushSoundLive(); } };
            Deactivate += (s, e) => { isActive = false; UpdateLiveTimer(); if (ready) PostWindowState(); };
            Load += (s, e) => InitWeb();
            FormClosing += (s, e) => { live.Stop(); saveTimer.Stop(); app.Color.Save(); app.Sound.Save(); app.General.Save(); app.Shot.Save(); app.Rec.Save(); app.RegisterHotkeys(); };
            FormClosed += (s, e) => { web.Dispose(); app.WindowClosed(this); };
        }

        // Свёрнутое окно усыпляем: WebView2 перестаёт рисовать и выполнять страницу и отдаёт часть памяти.
        // Развернули — просыпается сам, как только снова становится видимым.
        bool asleep;
        bool isActive; // окно сейчас в фокусе (в Deactivate Form.ActiveForm ещё может указывать на нас)
        async void SleepWhenMinimized()
        {
            var core = web.CoreWebView2;
            if (core == null) return;
            try
            {
                if (WindowState == FormWindowState.Minimized && !asleep)
                {
                    asleep = true;
                    web.Visible = false;                       // усыпить можно только невидимый WebView2
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Low;
                    await core.TrySuspendAsync();
                }
                else if (WindowState != FormWindowState.Minimized && asleep)
                {
                    asleep = false;
                    core.MemoryUsageTargetLevel = CoreWebView2MemoryUsageTargetLevel.Normal;
                    web.Visible = true;                        // стал видимым — WebView2 просыпается сам
                }
            }
            catch (Exception) { }
        }

        // ───────────── своя шапка ─────────────
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
            var mg = new Margins { Bottom = 1 }; DwmExtendFrameIntoClientArea(Handle, ref mg);
            SetWindowPos(Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0020);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
            {
                if (IsZoomed(Handle))
                {
                    var wa = Screen.FromHandle(Handle).WorkingArea;
                    Marshal.StructureToPtr(new Rect { L = wa.Left, T = wa.Top, R = wa.Right, B = wa.Bottom }, m.LParam, false);
                }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // WinForms при возврате из «развёрнуто» прибавляет рамку, которой у нас нет, — помним обычный размер сами
        void TrackBounds()
        {
            if (WindowState == FormWindowState.Normal)
            {
                if (lastState != FormWindowState.Normal && normalBounds.Width > 0)
                {
                    lastState = FormWindowState.Normal;
                    var b = normalBounds;
                    BeginInvoke(new Action(() => { if (WindowState == FormWindowState.Normal) Bounds = b; }));
                    return;
                }
                normalBounds = Bounds;
            }
            lastState = WindowState;
        }

        void StartResize(string dir)
        {
            int ht;
            switch (dir)
            {
                case "l": ht = 10; break; case "r": ht = 11; break; case "t": ht = 12; break; case "tl": ht = 13; break;
                case "tr": ht = 14; break; case "b": ht = 15; break; case "bl": ht = 16; break; case "br": ht = 17; break;
                default: return;
            }
            if (WindowState != FormWindowState.Normal) return;
            ReleaseCapture();
            SendMessage(Handle, WM_NCLBUTTONDOWN, (IntPtr)ht, IntPtr.Zero);
        }

        void PostWindowState() { Post("shell", new { type = "win", max = WindowState == FormWindowState.Maximized, active = Form.ActiveForm == this }); }

        void SetDarkTitle(bool dark)
        {
            if (!IsHandleCreated) return;
            int v = dark ? 1 : 0;
            DwmSetWindowAttribute(Handle, 20, ref v, 4);
        }

        static bool SystemDark()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
            {
                object v = k == null ? null : k.GetValue("AppsUseLightTheme");
                return v is int && (int)v == 0;
            }
        }

        // ───────────── WebView2 ─────────────
        async void InitWeb()
        {
            try
            {
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qural", "WebView2");
                // Без GPU-ускорения: страница простая, а так WebView2 берёт на треть меньше памяти и втрое меньше процессора
                // (замер на этом ПК: 214 → 136 МБ, 94 → 31 мс процессора за 10 с)
                var options = new CoreWebView2EnvironmentOptions("--disable-gpu");
                var env = await CoreWebView2Environment.CreateAsync(null, data, options);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Не удалось запустить WebView2:\n" + ex.Message, "Qural", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
                return;
            }
            var core = web.CoreWebView2;
            var st = core.Settings;
            st.AreDevToolsEnabled = false;
            st.AreDefaultContextMenusEnabled = false;
            st.IsStatusBarEnabled = false;
            st.AreBrowserAcceleratorKeysEnabled = false;
            st.IsZoomControlEnabled = false;
            st.IsPinchZoomEnabled = false;
            st.IsGeneralAutofillEnabled = false;
            st.IsPasswordAutosaveEnabled = false;
            st.IsNonClientRegionSupportEnabled = true; // CSS app-region: drag — шапка страницы двигает окно

            string ui = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ui");
            core.SetVirtualHostNameToFolderMapping(Host, ui, CoreWebView2HostResourceAccessKind.Deny);
            core.NewWindowRequested += (s, e) => e.Handled = true;
            core.NavigationStarting += (s, e) =>
            {
                Uri u;
                if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out u) || u.Host != Host) e.Cancel = true;
            };
            core.WebMessageReceived += OnMessage;
            core.Navigate("https://" + Host + "/index.html");
        }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = json.DeserializeObject(e.WebMessageAsJson) as Dictionary<string, object>; }
            catch (Exception) { return; }
            if (m == null) return;
            switch (Str(m, "mod"))
            {
                case "shell": OnShell(m); break;
                case "color": OnColor(m); break;
                case "sound": OnSound(m); break;
                case "shot": OnShot(m); break;
                case "rec": OnRec(m); break;
            }
        }

        void OnShell(Dictionary<string, object> m)
        {
            switch (Str(m, "type"))
            {
                case "ready":
                    ready = true;
                    PostWindowState();
                    SendShell();
                    SendColor();
                    SendSound();
                    SendShot();
                    SendRec();
                    UpdateChanged();
                    UpdateLiveTimer();
                    break;

                case "theme":
                    bool dark = Bool(m, "dark");
                    SetDarkTitle(dark);
                    BackColor = dark ? System.Drawing.Color.FromArgb(11, 13, 20) : System.Drawing.Color.FromArgb(242, 244, 248);
                    web.DefaultBackgroundColor = BackColor;
                    break;

                case "win":
                    switch (Str(m, "action"))
                    {
                        case "min": WindowState = FormWindowState.Minimized; break;
                        case "max": WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized; break;
                        case "close": Close(); break;
                    }
                    break;

                case "resize": StartResize(Str(m, "dir")); break;

                case "page":
                    string page = Str(m, "page");
                    if (page == "home" || page == "color" || page == "sound" || page == "shot" || page == "rec" || page == "general") { app.General.Page = page; app.General.Save(); }
                    if (page == "shot") SendShot(); // Lightshot и «Ножницы» могли запустить / выключить, пока окно было открыто
                    UpdateLiveTimer();
                    if (page == "sound" || page == "home") PushSoundLive();
                    break;

                case "module":
                    app.SetModule(Str(m, "name"), Bool(m, "on"));
                    break;

                case "trayClick":
                    app.General.TrayClick = Str(m, "value") == "next" ? "next" : "open";
                    app.General.Save();
                    SendShell();
                    break;

                case "autostart":
                    TrayApp.SetAutostart(Bool(m, "value"));
                    SendShell();
                    break;

                case "update":
                    if (Str(m, "action") == "install") app.InstallUpdate();
                    else if (Str(m, "action") == "page") { var r = Updater.Latest; if (r != null && r.Page.StartsWith("https://github.com/")) try { System.Diagnostics.Process.Start(r.Page); } catch (Exception) { } }
                    else app.CheckUpdate();
                    break;

                case "updateAuto":
                    app.General.NoUpdateCheck = !Bool(m, "value"); app.General.Save();
                    UpdateChanged();
                    break;

                case "welcome": // первый запуск: что включить
                    app.SetModule("color", Bool(m, "color"));
                    app.SetModule("sound", Bool(m, "sound"));
                    app.SetModule("shot", Bool(m, "shot"));
                    app.SetModule("rec", Bool(m, "rec"));
                    TrayApp.SetAutostart(Bool(m, "autostart"));
                    app.General.Setup = "done";
                    app.General.Page = "home";
                    app.General.Save();
                    SendShell();
                    break;

                case "captureStart": app.SuspendHotkeys(); break;
                case "captureEnd": app.RegisterHotkeys(); break;

                case "shortcuts": // ярлыки на все команды — для действия «Open» в Ulanzi Deck / Stream Deck
                    {
                        object list; m.TryGetValue("list", out list);
                        var items = list as object[];
                        if (items == null) { var al = list as System.Collections.ArrayList; if (al != null) items = al.ToArray(); }
                        if (items == null) break;
                        try
                        {
                            string dir = Shortcuts.Create(items);
                            Post("shell", new { type = "shortcutsDone", dir = dir, count = items.Length });
                            System.Diagnostics.Process.Start("explorer.exe", "\"" + dir + "\"");
                        }
                        catch (Exception ex) { Toast("shell", "Не удалось создать ярлыки: " + ex.Message, false); }
                        break;
                    }

                case "copy": // готовые команды для деки — в буфер обмена
                    {
                        string text = Str(m, "text");
                        if (text.Length == 0 || text.Length > 2000) break;
                        try { Clipboard.SetText(text); Post("shell", new { type = "copied" }); }
                        catch (Exception) { Post("shell", new { type = "copyFailed" }); }
                        break;
                    }
            }
        }

        void UpdateLiveTimer()
        {
            // Опрос раз в секунду ловит то, что поменяли в самом микшере Windows. Нужен, только пока на окно смотрят:
            // окно активно и не свёрнуто. Изменения клавишами показываются сразу и без опроса (SoundChanged).
            bool needed = ready && !app.General.SoundOff && isActive && WindowState != FormWindowState.Minimized;
            if (needed) { if (!live.Enabled) live.Start(); } else live.Stop();
        }

        // отложенное сохранение (ползунки) — записать сейчас (Windows выключается, окно закрывается)
        public void FlushSaves()
        {
            if (!saveTimer.Enabled) return;
            saveTimer.Stop();
            app.Color.Save(); app.Sound.Save();
        }

        public void ModulesChanged() { SendShell(); SendColor(); SendSound(); SendShot(); SendRec(); UpdateLiveTimer(); }

        // обновления: состояние для раздела «Обновления» в «Общих»
        public void UpdateChanged() { Post("shell", UpdateJson()); }
        object UpdateJson()
        {
            var r = Updater.Latest;
            return new
            {
                type = "update", current = Updater.CurrentText, state = Updater.State, error = Updater.Error, progress = Updater.Progress,
                latest = r != null ? r.Version : "", page = r != null ? r.Page : "", installed = Updater.Installed, auto = !app.General.NoUpdateCheck,
                checkedAt = Updater.CheckedAt == DateTime.MinValue ? "" : Updater.CheckedAt.ToString("d MMMM, HH:mm", new System.Globalization.CultureInfo("ru-RU"))
            };
        }

        void SendShell()
        {
            Post("shell", new
            {
                type = "state",
                page = app.General.Page,
                trayClick = app.General.TrayClick,
                autostart = TrayApp.IsAutostart(),
                nvapi = Nv.Available,
                fallback = Audio.UsedChannelFallback,
                colorOn = !app.General.ColorOff,
                soundOn = !app.General.SoundOff,
                shotOn = !app.General.ShotOff,
                recOn = !app.General.RecOff,
                welcome = app.General.Setup == "pending",
                busy = app.LastBusy,
                exe = Application.ExecutablePath
            });
        }

        // Назначить клавишу: проверить, забрать у прежнего владельца, перерегистрировать. Возвращает текст для уведомления (или null).
        string AssignHotkey(int key, object owner, int action, Action<int> set)
        {
            string bad = HotkeyText.Check(key);
            if (bad != null) return bad;
            if ((((Keys)key) & Keys.KeyCode) == Keys.None) key = 0;
            string from = app.TakeHotkey(key, owner, action);
            set(key);
            app.Color.Save(); app.Sound.Save(); app.Shot.Save(); app.Rec.Save();
            string busy = app.RegisterHotkeys();
            SendShell(); SendColor(); SendSound(); SendShot(); SendRec();
            if (key != 0 && busy.Contains(HotkeyText.Format(key))) return HotkeyText.Format(key) + " занято другой программой — выберите другое сочетание";
            if (from != null) return HotkeyText.Format(key) + " было у «" + from + "» — перенесено сюда";
            return null;
        }

        // ───────────── общее ─────────────
        void Post(string mod, object o)
        {
            if (web.CoreWebView2 == null) return;
            var d = new Dictionary<string, object>();
            foreach (var p in o.GetType().GetProperties()) d[p.Name] = p.GetValue(o, null);
            d["mod"] = mod;
            web.CoreWebView2.PostWebMessageAsJson(json.Serialize(d));
        }

        void Toast(string mod, string text, bool undo) { Post(mod, new { type = "toast", text = text, undo = undo }); }

        static string Str(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
        static int Int(Dictionary<string, object> m, string k) { return (int)Math.Round(Num(m, k)); }
        static double Num(Dictionary<string, object> m, string k)
        {
            object v;
            if (!m.TryGetValue(k, out v) || v == null) return 0;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
        }
        static bool Bool(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) && v is bool && (bool)v; }
        static string NameArg(Dictionary<string, object> m, string def)
        {
            string s = Str(m, "name").Trim();
            if (s.Length > 40) s = s.Substring(0, 40);
            return s.Length == 0 ? def : s;
        }
        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
    }
}
