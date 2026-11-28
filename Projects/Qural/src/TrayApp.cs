using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Qural
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int RegisterWindowMessage(string s);
        public static readonly int ShowMessage = RegisterWindowMessage("Qural_ShowWindow");

        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--net-run") { NetMerge.RunFromTask(); return; }
            if (args.Length > 0 && args[0] == "--net-mon") { NetMon.Run(); return; }          // «Сеть» → «Программы»: помощник с правами администратора   // «Сеть»: задача с правами администратора запускает net-merge
            if (Array.Exists(args, a => a == "--uninstall")) { Uninstaller.Run(); return; } // «Удалить» в «Приложениях» Windows
            bool isCommand = Commands.IsCommand(args); // запуск с командой (кнопка Ulanzi Deck, ярлык)
            // Быстрый путь для кнопок деки: Qural уже работает — сразу передаём команду, без мьютекса и его настройки
            // прав (она дорогая: ~0,3 с на загрузку модулей безопасности .NET). Так нажатие отрабатывает за ~30 мс.
            if (isCommand && Commands.TrySendOnce(args)) return;
            bool created;
            Mutex mtx;
            try
            {
                // Доступ к мьютексу даём текущему пользователю явно: если первый Qural запустила дека от имени
                // администратора, второй запуск без повышения иначе получил бы «Отказано в доступе».
                var sec = new System.Security.AccessControl.MutexSecurity();
                sec.AddAccessRule(new System.Security.AccessControl.MutexAccessRule(
                    System.Security.Principal.WindowsIdentity.GetCurrent().User,
                    System.Security.AccessControl.MutexRights.Synchronize | System.Security.AccessControl.MutexRights.Modify,
                    System.Security.AccessControl.AccessControlType.Allow));
                mtx = new Mutex(true, "Qural_SingleInstance_v1", out created, sec);
            }
            catch (UnauthorizedAccessException) { mtx = null; created = false; } // мьютекс чужой, с повышенными правами — значит, Qural уже запущен
            using (mtx)
            {
                if (!created)
                {
                    // Qural уже работает: команду передаём ему и сразу выходим, без окна
                    if (isCommand) { if (!Commands.SendToRunning(args)) Environment.ExitCode = 1; }
                    else PostMessage((IntPtr)0xFFFF, ShowMessage, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                // с командой Qural запускается тихо в трей и выполняет её после старта
                bool openWindow = !isCommand && !Array.Exists(args, a => a == "--tray");
                Application.Run(new TrayApp(openWindow, isCommand ? args : null));
            }
        }
    }

    static class AppIcon
    {
        public static Icon Get(Size size)
        {
            using (var s = typeof(AppIcon).Assembly.GetManifestResourceStream("Qural.app.ico"))
                return s == null ? Icon.ExtractAssociatedIcon(Application.ExecutablePath) : new Icon(s, size);
        }
    }

    // Сейчас на переднем плане полноэкранная игра или программа?
    // 1) Windows сама говорит: «идёт полноэкранное приложение / Direct3D на весь экран / режим презентации»;
    // 2) или окно в фокусе закрывает весь свой монитор (игры в режиме «без рамки на весь экран»).
    static class Fullscreen
    {
        [DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, System.Text.StringBuilder s, int n);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        public static bool Active()
        {
            int st;
            if (SHQueryUserNotificationState(out st) == 0 && (st == 2 || st == 3 || st == 4)) return true; // BUSY, D3D_FULL_SCREEN, PRESENTATION
            IntPtr w = GetForegroundWindow();
            if (w == IntPtr.Zero || w == GetShellWindow() || w == GetDesktopWindow()) return false;
            var cls = new System.Text.StringBuilder(64); GetClassName(w, cls, 64);
            string c = cls.ToString();
            if (c == "Progman" || c == "WorkerW" || c == "Shell_TrayWnd") return false; // рабочий стол и панель задач
            // Окно с заголовком — обычное окно, даже развёрнутое: на мониторе без панели задач оно тоже «закрывает весь экран»
            // (с невидимой рамкой по 8 px за краями). Игры «без рамки на весь экран» и F11 в браузере — без заголовка.
            if ((GetWindowLong(w, -16 /*GWL_STYLE*/) & 0x00C00000 /*WS_CAPTION*/) == 0x00C00000) return false;
            RECT r;
            if (!GetWindowRect(w, out r)) return false;
            var b = Screen.FromHandle(w).Bounds;
            return r.L <= b.Left && r.T <= b.Top && r.R >= b.Right && r.B >= b.Bottom;
        }
    }

    // Какая программа сейчас на переднем плане (имя exe без .exe).
    static class Foreground
    {
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        delegate bool EnumProc(IntPtr hWnd, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumProc cb, IntPtr l);
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, System.Text.StringBuilder name, ref int size);

        public static string ProcessName()
        {
            IntPtr w = GetForegroundWindow();
            if (w == IntPtr.Zero) return null;
            uint pid; GetWindowThreadProcessId(w, out pid);
            string name = NameOf(pid);
            // приложения из Microsoft Store живут внутри ApplicationFrameHost — берём настоящий процесс
            if (string.Equals(name, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                uint inner = 0;
                EnumChildWindows(w, (c, l) =>
                {
                    uint p; GetWindowThreadProcessId(c, out p);
                    if (p != pid) { inner = p; return false; }
                    return true;
                }, IntPtr.Zero);
                if (inner != 0) name = NameOf(inner);
            }
            return name;
        }

        static string NameOf(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new System.Text.StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? System.IO.Path.GetFileNameWithoutExtension(sb.ToString()) : null;
            }
            finally { CloseHandle(h); }
        }
    }

    // Скрытое окно: все горячие клавиши Qural и сигнал от второго экземпляра.
    // id: 1 — следующий профиль цвета; 1000+i — профиль цвета i; 90..92 — звук «Активное окно»; 100+i*4+a — звук программы i.
    class MessageWindow : NativeWindow, IDisposable
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mod, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        const int WM_HOTKEY = 0x0312;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_NOREPEAT = 0x4000;
        public const int ColorNext = 1, ColorBase = 1000, FocusedBase = 90, SoundBase = 100;
        public const int ShotRegion = 50, ShotFull = 51, ShotWindow = 52, RecToggle = 60, RecPause = 61;

        public event Action<int> Hotkey;
        public event Action ShowRequested;
        public event Action<string[]> CommandReceived; // команда от второго запуска Qural.exe (WM_COPYDATA)
        readonly List<int> ids = new List<int>();

        [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, uint msg, uint action, IntPtr info);

        // заголовок — чтобы второй запуск нашёл это скрытое окно и передал ему команду
        public MessageWindow()
        {
            CreateHandle(new CreateParams { Caption = Commands.WindowTitle });
            // Если Qural запущен с правами администратора (его запустила такая дека), Windows не пропускает сообщения
            // от обычных программ. Команды и «показать окно» безобидны и проверяются по метке — разрешаем их принимать.
            ChangeWindowMessageFilterEx(Handle, Commands.WM_COPYDATA, 1 /*MSGFLT_ALLOW*/, IntPtr.Zero);
            ChangeWindowMessageFilterEx(Handle, (uint)Program.ShowMessage, 1, IntPtr.Zero);
        }

        public void UnregisterAll()
        {
            foreach (int id in ids) UnregisterHotKey(Handle, id);
            ids.Clear();
        }

        // выключенный инструмент клавиши не держит
        public string RegisterAll(ColorConfig color, SoundConfig sound, ShotConfig shot, RecConfig rec, bool colorOn, bool soundOn, bool shotOn, bool recOn)
        {
            UnregisterAll();
            var failed = new List<string>();
            if (recOn)
            {
                Reg(RecToggle, rec.Hotkey, false, failed);
                Reg(RecPause, rec.PauseHotkey, false, failed);
            }
            if (shotOn)
            {
                Reg(ShotRegion, shot.Hotkey, false, failed);
                Reg(ShotFull, shot.FullHotkey, false, failed);
                Reg(ShotWindow, shot.WindowHotkey, false, failed);
            }
            if (colorOn)
            {
                Reg(ColorNext, color.NextHotkey, false, failed);
                for (int i = 0; i < color.Profiles.Count; i++) Reg(ColorBase + i, color.Profiles[i].Hotkey, false, failed);
            }
            if (soundOn)
            {
                for (int a = 0; a < 3; a++) Reg(FocusedBase + a, sound.Focused.Get(a), a < 2 && sound.Focused.Repeat, failed);
                for (int i = 0; i < sound.Apps.Count && i < 200; i++)
                    for (int a = 0; a < 3; a++) Reg(SoundBase + i * 4 + a, sound.Apps[i].Get(a), a < 2 && sound.Apps[i].Repeat, failed);
            }
            return string.Join(", ", failed.ToArray());
        }

        void Reg(int id, int key, bool repeat, List<string> failed)
        {
            if (key == 0) return;
            var k = (Keys)key;
            uint mod = repeat ? 0 : MOD_NOREPEAT;
            if ((k & Keys.Control) != 0) mod |= MOD_CONTROL;
            if ((k & Keys.Alt) != 0) mod |= MOD_ALT;
            if ((k & Keys.Shift) != 0) mod |= MOD_SHIFT;
            if (RegisterHotKey(Handle, id, mod, (uint)(k & Keys.KeyCode))) ids.Add(id);
            else failed.Add(HotkeyText.Format(key));
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && Hotkey != null) Hotkey(m.WParam.ToInt32());
            else if (m.Msg == Program.ShowMessage && ShowRequested != null) ShowRequested();
            else if (m.Msg == Commands.WM_COPYDATA)
            {
                string[] args = Commands.Read(m.LParam);
                if (args != null)
                {
                    m.Result = (IntPtr)1;
                    // выполняем после возврата из SendMessage, чтобы второй запуск не ждал
                    if (CommandReceived != null) { var a = args; var cb = CommandReceived; BeginInvokeOnIdle(() => cb(a)); }
                    return;
                }
            }
            base.WndProc(ref m);
        }

        // NativeWindow не умеет BeginInvoke — откладываем через одноразовый таймер на этом же потоке
        static void BeginInvokeOnIdle(Action a)
        {
            var t = new System.Windows.Forms.Timer { Interval = 1 };
            t.Tick += (s, e) => { t.Stop(); t.Dispose(); a(); };
            t.Start();
        }

        public void Dispose() { UnregisterAll(); DestroyHandle(); }
    }

    class TrayApp : ApplicationContext
    {
        readonly NotifyIcon tray = new NotifyIcon();
        readonly MessageWindow msg = new MessageWindow();
        readonly OsdForm osd = new OsdForm();
        readonly System.Windows.Forms.Timer reapply = new System.Windows.Forms.Timer { Interval = 1500 };
        readonly System.Windows.Forms.Timer memoSave = new System.Windows.Forms.Timer { Interval = 2000 };
        public readonly ColorConfig Color;
        public readonly SoundConfig Sound;
        public readonly GeneralConfig General;
        public readonly ShotConfig Shot;
        public readonly RecConfig Rec;
        readonly Recorder recorder;
        readonly SynchronizationContext ui;
        MainWindow window;

        public Recorder Recorder { get { return recorder; } }

        // выполнить на потоке интерфейса (из FFmpeg, потоков звука и т. п.)
        public void Invoke(Action a) { ui.Post(_ => { try { a(); } catch (Exception) { } }, null); }

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public TrayApp(bool openWindow, string[] startupCommand)
        {
            Color = ColorConfig.Load(); Color.SaveQuiet();
            Sound = SoundConfig.Load();
            // громкость, которую поменяли клавишами «Активного окна» ещё до появления списка, тоже надо найти
            // (дальше то же делается при каждом снимке микшера в окне, пока не пройдёт срок переноса)
            Sound.Seed(Audio.Snapshot());
            Sound.SaveQuiet();
            General = GeneralConfig.Load();
            Shot = ShotConfig.Load();
            // клавиши скриншота при запуске — в журнал (жалоба: после перезагрузки стояла PrtSc вместо своей)
            RecLog.Write("запуск: скриншот — область " + HotkeyText.Format(Shot.Hotkey) + ", экран " + HotkeyText.Format(Shot.FullHotkey) +
                         ", окно " + HotkeyText.Format(Shot.WindowHotkey) + (Store.LoadNotes.Count > 0 ? "; " + string.Join("; ", Store.LoadNotes.ToArray()) : ""));
            Rec = RecConfig.Load();
            ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            recorder = new Recorder(this);
            Net = new NetMerge(this);
            recorder.Changed += () => { BuildMenu(); if (window != null && !window.IsDisposed) window.RecChanged(); DeckCapture(); };

            tray.Icon = AppIcon.Get(SystemInformation.SmallIconSize);
            tray.ContextMenuStrip = new ContextMenuStrip();
            tray.ContextMenuStrip.Opening += (s, e) => BuildMenu();
            BuildMenu();
            tray.MouseClick += (s, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                if (General.TrayClick == "next" && !General.ColorOff) NextColor(); else OpenWindow();
            };
            tray.BalloonTipClicked += OnBalloonClick;
            tray.Visible = true;
            UpdateTooltip();

            msg.Hotkey += OnHotkey;
            msg.ShowRequested += OpenWindow;
            msg.CommandReceived += RunCommand;
            memoSave.Tick += (s, e) => { memoSave.Stop(); Sound.Save(); };

            // Windows сбрасывает гамму после сна, блокировки и смены разрешения — вернём профиль цвета
            reapply.Tick += (s, e) =>
            {
                reapply.Stop();
                if (!General.ColorOff) Applier.Apply(Color.ActiveProfile);
                RefreshWindowMonitors();
            };
            SystemEvents.DisplaySettingsChanged += OnSystemChange;
            SystemEvents.PowerModeChanged += OnPower;
            SystemEvents.SessionSwitch += OnSession;
            // выключение / перезагрузка / выход из Windows: успеть всё записать
            SystemEvents.SessionEnding += OnSessionEnding;

            string err = General.ColorOff ? "" : Applier.Apply(Color.ActiveProfile);
            string busy = RegisterHotkeys();
            if (Store.LoadNotes.Count > 0) tray.ShowBalloonTip(8000, "Qural — настройки", string.Join(Environment.NewLine, Store.LoadNotes.ToArray()), ToolTipIcon.Warning);
            else if (err.Length > 0) tray.ShowBalloonTip(5000, "Qural", err, ToolTipIcon.Warning);
            else if (busy.Length > 0) tray.ShowBalloonTip(5000, "Qural", "Заняты другой программой: " + busy, ToolTipIcon.Warning);

            trim.Tick += (s, e) => { trim.Stop(); TrimMemory(); };
            if (openWindow || General.Setup == "pending") OpenWindow(); // первый запуск — сразу окно с выбором инструментов
            else TrimMemorySoon(); // запустились тихо в трей — отдать Windows всё, что понадобилось только при старте
            if (startupCommand != null) RunCommand(startupCommand); // Qural не был запущен — выполняем команду сами
            deck = new DeckLink(this); // плагин Ulanzi Studio: громкость программ на кнопках деки

            // обновления: первая проверка через минуту, потом раз в 12 часов (только у установленной копии)
            Updater.Changed += () => Invoke(() => { BuildMenu(); if (window != null && !window.IsDisposed) window.UpdateChanged(); });
            updateTimer.Tick += (s, e) => { updateTimer.Interval = 12 * 3600 * 1000; AutoCheckUpdate(); };
            updateTimer.Start();

            Net.Changed += () => Invoke(() =>
            {
                if (window != null && !window.IsDisposed) window.NetChanged();
                if (deck != null) deck.NetChanged();
            });
        }

        // ── обновления ──
        readonly System.Windows.Forms.Timer updateTimer = new System.Windows.Forms.Timer { Interval = 60 * 1000 };
        bool updateBalloon; DateTime updateBalloonAt;

        void AutoCheckUpdate()
        {
            if (General.NoUpdateCheck || !Updater.Installed) return;
            Updater.CheckAsync(() => Invoke(() =>
            {
                var r = Updater.Latest;
                if (Updater.State != "available" || r == null || General.UpdateNotified == r.Version || Fullscreen.Active()) return;
                General.UpdateNotified = r.Version; General.Save();
                updateBalloon = true; updateBalloonAt = DateTime.UtcNow; shotPath = null;
                tray.ShowBalloonTip(8000, "Qural — обновление", "Доступна версия " + r.Version + ". Щёлкните, чтобы установить", ToolTipIcon.None);
            }));
        }

        public void CheckUpdate() { Updater.CheckAsync(null); }

        // скачать и запустить установщик; Qural закрывается, установщик запустит его снова
        public void InstallUpdate()
        {
            if (recorder.Recording) { tray.ShowBalloonTip(4000, "Qural — обновление", "Идёт запись экрана — обновлю после того, как вы её остановите", ToolTipIcon.None); return; }
            Updater.InstallAsync(err => Invoke(() =>
            {
                if (err != null) { tray.ShowBalloonTip(5000, "Qural — обновление", "Не получилось: " + err, ToolTipIcon.Warning); return; }
                ExitThread();
            }));
        }

        DeckLink deck;
        // «Сеть»: net-merge (объединение подключений) — состояние для окна, меню трея и деки
        public NetMerge Net;
        // громкость поменялась (клавишей, командой, в окне) — обновить кнопки деки, если они есть
        public void DeckChanged() { if (deck != null) deck.Push(false); }

        // для кнопок «Скриншот» и «Запись экрана» на деке: что сейчас идёт
        string shotMode = "";   // "" — ничего, shot — выделение для скриншота, pick — выбор области для записи
        int countdownN;         // 3, 2, 1 — идёт отсчёт перед записью; 0 — нет
        RecCountdown countdownForm;
        public string ShotMode { get { return shotForm != null && !shotForm.IsDisposed ? shotMode : ""; } }
        public int CountdownN { get { return countdownN; } }
        public bool RecOn { get { return recorder != null && recorder.Recording; } }
        public bool RecPaused { get { return recorder != null && recorder.Paused; } }
        public TimeSpan RecElapsed { get { return recorder != null ? recorder.Elapsed : TimeSpan.Zero; } }
        void DeckCapture() { if (deck != null) deck.CaptureChanged(); }

        // ── скриншоты ──
        ShotForm shotForm;
        string shotPath; DateTime shotAt; // последнее уведомление о скриншоте: щелчок открывает папку

        // region — выделить область; full — монитор под курсором; window — окно в фокусе. null — успешно, иначе текст ошибки
        public string ShotCommand(string mode)
        {
            if (General.ShotOff) return "«Скриншоты» выключены — включите их в Qural";
            string m = (mode ?? "").Trim().ToLowerInvariant();
            if (m.Length == 0 || m == "region") { OpenShot(); return null; }
            if (m == "full" || m == "screen" || m == "window") { QuickShot(m == "window"); return null; }
            return "--shot: вместо «" + mode + "» нужно region, full или window";
        }

        void OpenShot()
        {
            if (shotForm != null && !shotForm.IsDisposed) { Fore.Bring(shotForm.Handle); return; } // уже открыт
            IntPtr old = Shooter.PerMonitor();
            try
            {
                Rectangle virt = Shooter.Virtual();
                var mons = Shooter.Monitors();
                var wins = Shooter.Windows();
                Bitmap bmp;
                try { bmp = Shooter.Capture(virt, Shot.Cursor); }
                catch (Exception ex) { tray.ShowBalloonTip(4000, "Qural — скриншот", "Не удалось снять экран: " + ex.Message, ToolTipIcon.Warning); return; }
                shotForm = new ShotForm(Shot, bmp, virt, mons, wins, ShotNotify);
                shotForm.FormClosed += (s, e) => { shotForm = null; shotMode = ""; DeckCapture(); TrimMemorySoon(); };
                shotMode = "shot";
                shotForm.Show();
                DeckCapture();
            }
            finally { Shooter.Restore(old); }
        }

        // сразу в файл и в буфер, без окна выделения
        void QuickShot(bool window)
        {
            IntPtr old = Shooter.PerMonitor();
            try
            {
                Rectangle r = window ? Shooter.ForegroundRect() : Shooter.MonitorAt(Shooter.CursorPos());
                r = Rectangle.Intersect(r, Shooter.Virtual());
                if (r.Width < 2 || r.Height < 2) { ShotNotify("Нет окна в фокусе — снимать нечего", null); return; }
                string path = Shooter.NextPath(Shot);
                using (var raw = Shooter.Capture(r, Shot.Cursor))
                using (var b = Shooter.Flat(raw, new Rectangle(0, 0, r.Width, r.Height)))
                {
                    Shooter.SaveFile(b, path, Shot.JpegQuality);
                    Shooter.ToClipboard(b);
                }
                ShotNotify((window ? "Окно" : "Экран") + " сохранён и скопирован: " + System.IO.Path.GetFileName(path) + ". Щёлкните, чтобы открыть папку", path);
            }
            catch (Exception ex) { ShotNotify("Скриншот не сохранился: " + ex.Message, null); }
            finally { Shooter.Restore(old); TrimMemorySoon(); }
        }

        void ShotNotify(string text, string path)
        {
            if (!Shot.Notify || Fullscreen.Active()) return;
            shotPath = path; shotAt = DateTime.UtcNow;
            tray.ShowBalloonTip(3000, "Qural — скриншот", text, ToolTipIcon.None);
        }

        // ── запись экрана ──
        // action: toggle | start | stop | pause; mode: region | screen | window (по умолчанию — выбрать рамкой)
        public string RecCommand(string action, string mode)
        {
            if (General.RecOff) return "«Запись» выключена — включите её в Qural";
            string a = (action ?? "toggle").Trim().ToLowerInvariant();
            // идёт отсчёт: ещё раз «начать / остановить» — отменить его
            if (countdownForm != null && !countdownForm.IsDisposed) { if (a == "toggle" || a == "stop") countdownForm.Cancel(); return null; }
            if (a == "pause") { if (recorder.Recording) recorder.TogglePause(); return null; }
            if (a == "stop" || (a == "toggle" && recorder.Recording)) { if (recorder.Recording) recorder.Stop(); return null; }
            if (a != "start" && a != "toggle") return "--rec: вместо «" + action + "» нужно start, stop, toggle или pause";
            if (recorder.Recording) return null;
            if (!Ff.Available) return "Нет папки ffmpeg рядом с Qural.exe";
            Ff.ProbeAsync();
            string m = (mode ?? "region").Trim().ToLowerInvariant();
            if (m == "screen" || m == "full") { BeginRec(Target(Shooter.MonitorAt(Shooter.CursorPos()), IntPtr.Zero)); return null; }
            if (m == "window")
            {
                IntPtr fg = GetForegroundWindow();
                var r = Shooter.ForegroundRect();
                if (fg == IntPtr.Zero || r.Width < 16) return "Нет окна в фокусе";
                BeginRec(Target(r, fg));
                return null;
            }
            PickRecArea();
            return null;
        }

        void PickRecArea()
        {
            if (shotForm != null && !shotForm.IsDisposed) { Fore.Bring(shotForm.Handle); return; }
            IntPtr old = Shooter.PerMonitor();
            try
            {
                Rectangle virt = Shooter.Virtual();
                var mons = Shooter.Monitors();
                var handles = new List<IntPtr>();
                var wins = Shooter.Windows(handles);
                Bitmap bmp;
                try { bmp = Shooter.Capture(virt, false); }
                catch (Exception ex) { RecNotify("Не удалось снять экран: " + ex.Message, null); return; }
                shotForm = new ShotForm(Shot, bmp, virt, mons, wins, handles, ShotNotify, (r, h) => BeginRec(Target(r, h)));
                shotForm.FormClosed += (s, e) => { shotForm = null; shotMode = ""; DeckCapture(); TrimMemorySoon(); };
                shotMode = "pick";
                shotForm.Show();
                DeckCapture();
            }
            finally { Shooter.Restore(old); }
        }

        [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref Shooter.RECT r, uint flags);

        // что записывать: окно (если выбрано окно), весь монитор или часть монитора
        static RecTarget Target(Rectangle area, IntPtr hwnd)
        {
            IntPtr old = Shooter.PerMonitor();
            try
            {
                var center = new Point(area.X + area.Width / 2, area.Y + area.Height / 2);
                var mon = Shooter.MonitorAt(center);
                var rr = new Shooter.RECT { L = mon.Left + 1, T = mon.Top + 1, R = mon.Left + 2, B = mon.Top + 2 };
                var t = new RecTarget { Monitor = mon, HMonitor = MonitorFromRect(ref rr, 2), Area = area, Hwnd = hwnd };
                if (hwnd != IntPtr.Zero) t.Kind = "window";
                else if (Rectangle.Intersect(area, mon) == mon) { t.Kind = "screen"; t.Area = mon; }
                else { t.Kind = "region"; t.Area = Rectangle.Intersect(area, mon); }
                return t;
            }
            finally { Shooter.Restore(old); }
        }

        void BeginRec(RecTarget t)
        {
            if (recorder.Recording || (countdownForm != null && !countdownForm.IsDisposed)) return;
            RecLog.Write("старт: " + t.Kind + " " + t.Area + " окно " + t.Hwnd + " монитор " + t.Monitor);
            Action go = () => { try { recorder.Start(t); } catch (Exception ex) { RecLog.Write(ex.ToString()); RecNotify("Запись не началась: " + ex.Message, null); } };
            if (Rec.Countdown)
            {
                countdownForm = new RecCountdown(t.Monitor, go, n => { countdownN = n; DeckCapture(); });
                countdownForm.FormClosed += (s, e) => countdownForm = null;
                countdownForm.Show();
            }
            else go();
        }

        public void RecNotify(string text, string path) { RecNotify(text, path, true); }
        public void RecNotify(string text, string path, bool always)
        {
            RecLog.Write(text);
            if (Fullscreen.Active() && !always) return;
            shotPath = path; shotAt = DateTime.UtcNow;
            tray.ShowBalloonTip(4000, "Qural — запись", text, ToolTipIcon.None);
        }

        Editor editor;
        public void OpenEditor(RecSession s)
        {
            if (editor != null && !editor.IsDisposed) editor.Close();
            editor = new Editor(this, s);
            editor.FormClosed += (o, e) => { editor = null; TrimMemorySoon(); };
            editor.Show();
            Fore.Bring(editor.Handle);
        }

        // «Сохранять сразу»: без редактора — звук свести, видео не перекодировать
        public void QuickExport(RecSession s)
        {
            var edit = Exporter.Edits.Default(s, Rec);
            string path = Exporter.NextPath(Rec, Rec.Container);
            Exporter.RunAsync(s, edit, path, null, err => Invoke(() =>
            {
                if (err != null) { RecNotify("Не удалось сохранить запись: " + err + ". Откройте её в редакторе", null); OpenEditor(s); return; }
                RecNotify("Запись сохранена: " + System.IO.Path.GetFileName(path) + ". Щёлкните, чтобы открыть папку", path);
            }));
        }

        void OnBalloonClick(object s, EventArgs e)
        {
            if (updateBalloon && (DateTime.UtcNow - updateBalloonAt).TotalSeconds < 60) { updateBalloon = false; InstallUpdate(); return; }
            updateBalloon = false;
            string p = shotPath; shotPath = null;
            if (p == null || (DateTime.UtcNow - shotAt).TotalSeconds > 30) return;
            try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + p + "\""); } catch (Exception) { }
        }

        // ── команды (Ulanzi Deck, ярлыки) ──
        string lastCmdErr = ""; DateTime lastCmdErrAt;
        void RunCommand(string[] args)
        {
            string err = Commands.Run(this, args);
            if (err.Length > 0) RecLog.Write("команда " + string.Join(" ", args) + ": " + err);
            if (err.Length == 0 || Fullscreen.Active()) return;
            // кнопку деки могут жать часто — одну и ту же ошибку показываем не чаще раза в 10 секунд
            if (err == lastCmdErr && (DateTime.UtcNow - lastCmdErrAt).TotalSeconds < 10) return;
            lastCmdErr = err; lastCmdErrAt = DateTime.UtcNow;
            tray.ShowBalloonTip(4000, "Qural — команда", err, ToolTipIcon.Warning);
        }

        // профиль: сначала точное название (без учёта регистра), затем слово next, затем номер с 1
        public bool SwitchColorByName(string name)
        {
            string n = (name ?? "").Trim();
            int idx = Color.Profiles.FindIndex(p => string.Equals(p.Name.Trim(), n, StringComparison.OrdinalIgnoreCase));
            if (idx < 0 && n.Equals("next", StringComparison.OrdinalIgnoreCase)) { NextColor(); return Color.Profiles.Count > 0; }
            int num;
            if (idx < 0 && int.TryParse(n, out num) && num >= 1 && num <= Color.Profiles.Count) idx = num - 1;
            if (idx < 0) return false;
            SwitchColor(idx);
            return true;
        }

        public void ResetColors()
        {
            Applier.ResetAll();
            if (Color.ShowOsd && !Fullscreen.Active()) osd.ShowText("По умолчанию");
        }

        // --volume <программа|active> <up|down|+N|-N|N|mute>. null — успешно, иначе текст ошибки
        public string VolumeCommand(string target, string value)
        {
            if (General.SoundOff) return "«Звук» выключен — включите его в Qural";
            string kind; int amount;
            if (!Commands.ParseVolume(value, out kind, out amount))
                return "--volume: вместо «" + value + "» нужно up, down, +N, -N, число 0–100 или mute";

            string t = (target ?? "").Trim().Trim('"');
            if (t.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) t = t.Substring(0, t.Length - 4);
            string[] names; int step; string touched = null;
            AppEntry entry;
            if (t.Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                // программа в фокусе. Кнопка деки нажата явно, поэтому управляем ею, даже если у неё есть своя строка
                string process = Foreground.ProcessName();
                if (string.IsNullOrEmpty(process) || process.Equals("Qural", StringComparison.OrdinalIgnoreCase)) return null;
                entry = Sound.Apps.Find(x => x.Has(process));
                if (entry == null) touched = process;
                names = entry != null ? entry.Names() : new[] { process };
                step = entry != null ? entry.Step : Sound.Focused.Step;
            }
            else
            {
                // программа из списка — по имени exe или по названию; иначе просто по имени exe
                entry = Sound.Apps.Find(x => x.Has(t) || string.Equals(x.Title, t, StringComparison.OrdinalIgnoreCase));
                names = entry != null ? entry.Names() : new[] { t };
                step = entry != null ? entry.Step : 5;
            }

            bool changed;
            switch (kind)
            {
                case "up": changed = Audio.Change(names, step / 100f, 1, Sound.MuteMemo) >= 0; break;
                case "down": changed = Audio.Change(names, step / 100f, -1, Sound.MuteMemo) >= 0; break;
                case "inc": changed = Audio.Change(names, amount / 100f, 1, Sound.MuteMemo) >= 0; break;
                case "dec": changed = Audio.Change(names, amount / 100f, -1, Sound.MuteMemo) >= 0; break;
                case "set": changed = Audio.Set(names, amount / 100f) >= 0; break;
                default: changed = Audio.ToggleMute(names, Sound.MuteMemo) != null; break;
            }
            if (!changed) return (entry != null ? entry.Title : t) + " сейчас не в микшере — включите в ней звук";

            bool newTouched = touched != null && Sound.Touch(touched);
            memoSave.Stop(); memoSave.Start();
            if (window != null && !window.IsDisposed)
            {
                if (newTouched) window.SoundListChanged(); else window.SoundChanged();
            }
            DeckChanged();
            return null;
        }

        // ── память в трее ──
        // После запуска и после закрытия окна в процессе остаётся то, что больше не нужно (загрузка настроек, WebView2,
        // разбор страниц). Через несколько секунд собираем мусор .NET и отдаём неиспользуемые страницы памяти Windows.
        // Одноразовый таймер: в простое Qural по-прежнему ничего не делает.
        readonly System.Windows.Forms.Timer trim = new System.Windows.Forms.Timer { Interval = 4000 };
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr proc, IntPtr min, IntPtr max);

        public void TrimMemorySoon() { trim.Stop(); trim.Start(); }

        // Окно закрыли: отпускаем ссылку на него, иначе закрытая форма вместе с WebView2-обёртками
        // и разборщиком JSON осталась бы в памяти до следующего открытия и уборка её бы не собрала.
        public void WindowClosed(MainWindow w)
        {
            if (window == w) window = null; // окно уже открыли заново — новое не трогаем
            TrimMemorySoon();
        }

        void TrimMemory()
        {
            if (window != null && !window.IsDisposed) return; // окно снова открыли — не мешаем
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            SetProcessWorkingSetSize(GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }

        // монитор включили/выключили/подключили, сменилось разрешение — сразу обновить вкладки мониторов,
        // а через полторы секунды (когда Windows закончит) ещё раз применить профиль и обновить
        void OnSystemChange(object s, EventArgs e)
        {
            RefreshWindowMonitors();
            reapply.Stop(); reapply.Start();
        }

        void SaveAllQuiet()
        {
            if (window != null && !window.IsDisposed) window.FlushSaves();
            Color.SaveQuiet(); Sound.SaveQuiet(); General.SaveQuiet(); Shot.SaveQuiet(); Rec.SaveQuiet();
        }

        void OnSessionEnding(object s, SessionEndingEventArgs e) { SaveAllQuiet(); }

        void RefreshWindowMonitors() { if (window != null && !window.IsDisposed) window.ColorChanged(); }
        void OnPower(object s, PowerModeChangedEventArgs e) { if (e.Mode == PowerModes.Resume) OnSystemChange(s, e); }
        void OnSession(object s, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock || e.Reason == SessionSwitchReason.ConsoleConnect
                || e.Reason == SessionSwitchReason.RemoteConnect) OnSystemChange(s, e);
        }

        // ── горячие клавиши ──
        // клавиши, которые не удалось занять (их держит другая программа) — показываются на главной
        public string LastBusy = "";

        public string RegisterHotkeys()
        {
            LastBusy = msg.RegisterAll(Color, Sound, Shot, Rec, !General.ColorOff, !General.SoundOff, !General.ShotOff, !General.RecOff);
            return LastBusy;
        }

        // включить / выключить инструмент целиком
        public void SetModule(string name, bool on)
        {
            if (name == "color")
            {
                General.ColorOff = !on;
                if (on) Applier.Apply(Color.ActiveProfile);
                else Applier.ResetAll(); // выключили «Цвет» — мониторы к стандартным значениям
                UpdateTooltip();
                DeckChanged();
            }
            else if (name == "sound") { General.SoundOff = !on; DeckChanged(); }
            else if (name == "shot") General.ShotOff = !on;
            else if (name == "rec") { General.RecOff = !on; if (!on && recorder.Recording) recorder.Stop(); }
            else if (name == "net") { General.NetOff = !on; Net.ModuleChanged(on); DeckChanged(); }
            else return;
            General.Save();
            RegisterHotkeys();
            if (window != null && !window.IsDisposed) window.ModulesChanged();
        }
        public void SuspendHotkeys() { msg.UnregisterAll(); }

        void OnHotkey(int id)
        {
            if (id == MessageWindow.RecToggle) { RecCommand("toggle", null); return; }
            if (id == MessageWindow.RecPause) { RecCommand("pause", null); return; }
            if (id == MessageWindow.ShotRegion) { ShotCommand("region"); return; }
            if (id == MessageWindow.ShotFull) { ShotCommand("full"); return; }
            if (id == MessageWindow.ShotWindow) { ShotCommand("window"); return; }
            if (id == MessageWindow.ColorNext) { NextColor(); return; }
            if (id >= MessageWindow.ColorBase) { SwitchColor(id - MessageWindow.ColorBase); return; }
            AppEntry a; string[] names; int action; string touchedNow = null;
            if (id >= MessageWindow.FocusedBase && id < MessageWindow.FocusedBase + 3)
            {
                a = Sound.Focused; action = id - MessageWindow.FocusedBase;
                string process = Foreground.ProcessName();
                if (string.IsNullOrEmpty(process) || process.Equals("Qural", StringComparison.OrdinalIgnoreCase)) return;
                // у программы есть своя строка (например, Spotify) — ей управляют только её клавиши
                if (Sound.Apps.Exists(x => x.Has(process))) return;
                names = new[] { process };
                touchedNow = process; // запомним после успешной смены — для списка на странице «Активное окно»
            }
            else if (id >= MessageWindow.SoundBase)
            {
                int i = (id - MessageWindow.SoundBase) / 4; action = (id - MessageWindow.SoundBase) % 4;
                if (i >= Sound.Apps.Count || action > 2) return;
                a = Sound.Apps[i]; names = a.Names();
            }
            else return;
            bool changed = action == 2
                ? Audio.ToggleMute(names, Sound.MuteMemo) != null
                : Audio.Change(names, a.Step / 100f, action == 0 ? 1 : -1, Sound.MuteMemo) >= 0;
            // программа была в микшере и громкость поменялась — запомнить для списка на странице «Активное окно»
            bool newTouched = changed && touchedNow != null && Sound.Touch(touchedNow);
            memoSave.Stop(); memoSave.Start();
            if (window != null && !window.IsDisposed)
            {
                if (newTouched) window.SoundListChanged(); // новая программа в списке — странице нужен полный список
                else window.SoundChanged();
            }
            DeckChanged();
        }

        // Забрать клавишу у всех, кроме нового владельца. Возвращает, у кого забрали (или null).
        // owner: Profile (профиль цвета), "next" (следующий профиль цвета) или AppEntry вместе с action.
        public string TakeHotkey(int key, object owner, int action)
        {
            if (key == 0) return null;
            string from = null;
            foreach (var p in Color.Profiles)
                if (p != owner && p.Hotkey == key) { p.Hotkey = 0; from = "Цвет · " + p.Name; }
            if (!"next".Equals(owner) && Color.NextHotkey == key) { Color.NextHotkey = 0; from = "Цвет · следующий профиль"; }
            string[] acts = { "громче", "тише", "без звука" };
            foreach (var a in Sound.AllEntries())
                for (int i = 0; i < 3; i++)
                    if (a.Get(i) == key && !(a == owner && i == action)) { a.Set(i, 0); from = "Звук · " + a.Title + " · " + acts[i]; }
            if (!"shot-region".Equals(owner) && Shot.Hotkey == key) { Shot.Hotkey = 0; from = "Скриншоты · область"; }
            if (!"shot-full".Equals(owner) && Shot.FullHotkey == key) { Shot.FullHotkey = 0; from = "Скриншоты · весь экран"; }
            if (!"shot-window".Equals(owner) && Shot.WindowHotkey == key) { Shot.WindowHotkey = 0; from = "Скриншоты · окно"; }
            if (!"rec-toggle".Equals(owner) && Rec.Hotkey == key) { Rec.Hotkey = 0; from = "Запись · начать / остановить"; }
            if (!"rec-pause".Equals(owner) && Rec.PauseHotkey == key) { Rec.PauseHotkey = 0; from = "Запись · пауза"; }
            return from;
        }

        // ── цвет ──
        public void PrevColor()
        {
            if (Color.Profiles.Count == 0) return;
            SwitchColor((Color.Active - 1 + Color.Profiles.Count) % Color.Profiles.Count);
        }

        public void NextColor()
        {
            if (Color.Profiles.Count == 0) return;
            SwitchColor((Color.Active + 1) % Color.Profiles.Count);
        }

        public void SwitchColor(int index)
        {
            if (index < 0 || index >= Color.Profiles.Count) return;
            Color.Active = index;
            Color.Save();
            UpdateTooltip();
            if (!General.ColorOff) // выключенный «Цвет»: профиль можно выбрать и настроить, но на мониторы он не идёт
            {
                string err = Applier.Apply(Color.ActiveProfile);
                // поверх полноэкранной игры надпись не показываем — окно «поверх всех» может её свернуть или дёрнуть
                bool fullscreen = Fullscreen.Active();
                if (Color.ShowOsd && !fullscreen) osd.ShowText(Color.ActiveProfile.Name);
                if (err.Length > 0 && !fullscreen) tray.ShowBalloonTip(4000, "Qural", err, ToolTipIcon.Warning);
            }
            if (window != null && !window.IsDisposed) window.ColorChanged();
            DeckChanged();
        }

        public void UpdateTooltip()
        {
            var p = Color.ActiveProfile;
            string t = General.ColorOff ? "Qural — цвет выключен" : "Qural — цвет: " + (p == null ? "нет профилей" : p.Name);
            tray.Text = t.Length > 63 ? t.Substring(0, 63) : t;
        }

        // Меню трея (оформление — TrayMenuStyle): действия, «Сеть», профили цвета, инструменты переключателями, выход
        void BuildMenu()
        {
            var menu = tray.ContextMenuStrip;
            var st = new TrayMenuStyle(TrayMenuStyle.IsDark(General.TrayTheme));
            menu.Items.Clear();
            var open = new ToolStripMenuItem("Открыть Qural", st.Icon("open"), (s, e) => OpenWindow());
            menu.Items.Add(open);
            menu.Items.Add(new ToolStripSeparator());

            // действия: дать меню закрыться, иначе оно попадёт на снимок / в запись
            Action<Action> later = act =>
            {
                var t = new System.Windows.Forms.Timer { Interval = 250 };
                t.Tick += (s2, e2) => { t.Stop(); t.Dispose(); act(); };
                t.Start();
            };
            bool any = false;
            if (!General.ShotOff)
            {
                var shotItem = new ToolStripMenuItem("Сделать скриншот", st.Icon("shot"), (s, e) => later(() => ShotCommand("region")));
                if (Shot.Hotkey != 0) shotItem.ShortcutKeyDisplayString = HotkeyText.Format(Shot.Hotkey);
                menu.Items.Add(shotItem);
                any = true;
            }
            if (!General.RecOff)
            {
                if (recorder.Recording)
                {
                    menu.Items.Add(new ToolStripMenuItem("Остановить запись", st.Icon("rec"), (s, e) => recorder.Stop()) { ShortcutKeyDisplayString = HotkeyText.Format(Rec.Hotkey), Tag = "accent" });
                    menu.Items.Add(new ToolStripMenuItem(recorder.Paused ? "Продолжить запись" : "Пауза", null, (s, e) => recorder.TogglePause()) { ShortcutKeyDisplayString = HotkeyText.Format(Rec.PauseHotkey) });
                }
                else
                {
                    Action<string> rec = mode => later(() => { string err = RecCommand("start", mode); if (err != null) RecNotify(err, null); });
                    var recMenu = new ToolStripMenuItem("Запись экрана", st.Icon("rec"));
                    recMenu.DropDownItems.Add(new ToolStripMenuItem("Выбрать область или окно…", null, (s, e) => rec("region")) { ShortcutKeyDisplayString = HotkeyText.Format(Rec.Hotkey) });
                    recMenu.DropDownItems.Add(new ToolStripMenuItem("Весь экран", null, (s, e) => rec("screen")));
                    menu.Items.Add(recMenu);
                }
                any = true;
            }
            if (NetMerge.Installed && !General.NetOff) { NetMenu(menu, st); any = true; }
            if (any) menu.Items.Add(new ToolStripSeparator());

            if (!General.ColorOff && Color.Profiles.Count > 0)
            {
                menu.Items.Add(TrayMenuStyle.Header("Профили цвета"));
                for (int i = 0; i < Color.Profiles.Count; i++)
                {
                    int idx = i;
                    var it = new ToolStripMenuItem(Color.Profiles[i].Name, null, (s, e) => SwitchColor(idx)) { Checked = i == Color.Active };
                    if (Color.Profiles[i].Hotkey != 0) it.ShortcutKeyDisplayString = HotkeyText.Format(Color.Profiles[i].Hotkey);
                    menu.Items.Add(it);
                }
                menu.Items.Add(new ToolStripMenuItem("Сбросить мониторы", st.Icon("reset"), (s, e) =>
                {
                    Applier.ResetAll();
                    if (Color.ShowOsd && !Fullscreen.Active()) osd.ShowText("По умолчанию");
                }));
                menu.Items.Add(new ToolStripSeparator());
            }

            menu.Items.Add(TrayMenuStyle.Header("Инструменты"));
            menu.Items.Add(TrayMenuStyle.Switch("Цвет", !General.ColorOff, (s, e) => SetModule("color", General.ColorOff)));
            menu.Items.Add(TrayMenuStyle.Switch("Звук", !General.SoundOff, (s, e) => SetModule("sound", General.SoundOff)));
            menu.Items.Add(TrayMenuStyle.Switch("Скриншоты", !General.ShotOff, (s, e) => SetModule("shot", General.ShotOff)));
            menu.Items.Add(TrayMenuStyle.Switch("Запись экрана", !General.RecOff, (s, e) => SetModule("rec", General.RecOff)));
            if (NetMerge.Installed) menu.Items.Add(TrayMenuStyle.Switch("Сеть", !General.NetOff, (s, e) => SetModule("net", General.NetOff)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(TrayMenuStyle.Switch("Запускать вместе с Windows", IsAutostart(), (s, e) => SetAutostart(!IsAutostart())));
            if (Updater.State == "available" && Updater.Latest != null)
                menu.Items.Add(new ToolStripMenuItem("Обновить Qural до " + Updater.Latest.Version, st.Icon("update"), (s, e) => InstallUpdate()) { Tag = "accent" });
            else if (Updater.State == "downloading")
                menu.Items.Add(new ToolStripMenuItem("Скачиваю обновление… " + Updater.Progress + "%", st.Icon("update")) { Enabled = false });
            menu.Items.Add(new ToolStripMenuItem("Выход", st.Icon("power"), (s, e) => ExitThread()));

            st.Apply(menu);
            open.Font = new Font("Segoe UI Semibold", 9.75f);
        }

        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

        // «Сеть» в меню трея: состояние и скорость (↓ синим, ↑ оранжевым), вкл / пауза, подключения
        void NetMenu(ContextMenuStrip menu, TrayMenuStyle st)
        {
            var s = Net.Current;
            if ((DateTime.UtcNow - netMenuAt).TotalSeconds > 2) { netMenuAt = DateTime.UtcNow; Net.Refresh(); s = Net.Current; }
            bool on = s.State == "on";
            string head = on ? "Сеть" : s.State == "starting" ? "Сеть: запускается…" : s.State == "waiting" ? "Сеть: ждёт второе подключение"
                        : s.State == "paused" ? "Сеть: пауза" : s.State == "setup" ? "Сеть: нужно включить" : "Сеть: выключена";
            var root = on ? TrayMenuStyle.Speed(head, st.Icon("net"), "↓ " + NetText.Mbps(s.Down), "↑ " + NetText.Mbps(s.Up) + " Мбит/с")
                          : new ToolStripMenuItem(head, st.Icon("net"));
            if (on || s.State == "starting" || s.State == "waiting")
                root.DropDownItems.Add(new ToolStripMenuItem("Пауза", null, (o, e) => ThreadPool.QueueUserWorkItem(_ => Net.Pause())));
            else root.DropDownItems.Add(new ToolStripMenuItem("Включить объединение", null, (o, e) => ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = Net.Resume();
                if (err != null) Invoke(() => tray.ShowBalloonTip(5000, "Qural — сеть", err, ToolTipIcon.Warning));
            })) { Tag = "accent" });
            if (s.Adapters.Count > 0)
            {
                root.DropDownItems.Add(new ToolStripSeparator());
                root.DropDownItems.Add(TrayMenuStyle.Header("Подключения"));
            }
            foreach (var a in s.Adapters)
            {
                var it = TrayMenuStyle.Speed(a.Label + (a.Isp.Length > 0 ? " · " + a.Isp : "") + (a.LimitMbps > 0 ? " · до " + a.LimitMbps : ""), null, "↓ " + NetText.Mbps(a.Down), "↑ " + NetText.Mbps(a.Up));
                it.Enabled = a.Present;
                root.DropDownItems.Add(it);
            }
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(new ToolStripMenuItem("Открыть «Сеть»", null, (o, e) => OpenPage("net")));
            menu.Items.Add(root);
        }
        DateTime netMenuAt;

        /// Открыть окно на нужной вкладке.
        public void OpenPage(string page)
        {
            if (Array.IndexOf(new[] { "home", "color", "sound", "shot", "rec", "net", "general" }, page) < 0) page = "home";
            General.Page = page; General.Save();
            bool had = window != null && !window.IsDisposed;
            OpenWindow();
            if (had) window.ShowPage(page);
        }

        public void OpenWindow()
        {
            if (window == null || window.IsDisposed) window = new MainWindow(this);
            window.Show();
            if (window.WindowState == FormWindowState.Minimized) window.WindowState = FormWindowState.Normal;
            // Windows не даёт фоновой программе (а Qural, вызванный кнопкой деки, — фоновая) вывести окно вперёд:
            // ненадолго присоединяемся к потоку окна, которое сейчас впереди, — тогда разрешено.
            IntPtr fg = GetForegroundWindow();
            uint pid, fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid), me = GetCurrentThreadId();
            if (fgThread != 0 && fgThread != me)
            {
                AttachThreadInput(me, fgThread, true);
                BringWindowToTop(window.Handle);
                SetForegroundWindow(window.Handle);
                AttachThreadInput(me, fgThread, false);
            }
            window.Activate();
        }

        public static bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue("Qural") != null;
        }

        public static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue("Qural", "\"" + Application.ExecutablePath + "\" --tray");
                else k.DeleteValue("Qural", false);
            }
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.DisplaySettingsChanged -= OnSystemChange;
            SystemEvents.PowerModeChanged -= OnPower;
            SystemEvents.SessionSwitch -= OnSession;
            SaveAllQuiet();
            SystemEvents.SessionEnding -= OnSessionEnding;
            tray.Visible = false;
            tray.Dispose();
            msg.Dispose();
            if (deck != null) deck.Dispose();
            Net.Dispose();
            recorder.Shutdown();
            if (shotForm != null && !shotForm.IsDisposed) shotForm.Close();
            if (window != null && !window.IsDisposed) window.Close();
            osd.Dispose();
            base.ExitThreadCore();
        }
    }

    // Надпись с названием профиля по центру экрана, сама исчезает.
    class OsdForm : Form
    {
        readonly System.Windows.Forms.Timer hide = new System.Windows.Forms.Timer { Interval = 1200 };
        string text = "";

        public OsdForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = System.Drawing.Color.FromArgb(26, 32, 48); // --pop тёмной темы GlassWise
            Opacity = 0.94;
            Font = new Font("Segoe UI Variable Text Semibold", 18f);
            if (Font.Name != "Segoe UI Variable Text Semibold") Font = new Font("Segoe UI Semibold", 18f);
            hide.Tick += (s, e) => { hide.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x80 | 0x08000000 | 0x20;
                return cp;
            }
        }

        public void ShowText(string t)
        {
            text = t;
            Size sz = TextRenderer.MeasureText(t, Font);
            Width = sz.Width + 56; Height = sz.Height + 28;
            var area = Screen.FromPoint(Cursor.Position).Bounds;
            Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + area.Height / 6);
            using (var path = Rounded(new Rectangle(0, 0, Width, Height), 14)) Region = new Region(path);
            Invalidate();
            if (!Visible) Show();
            hide.Stop(); hide.Start();
        }

        static GraphicsPath Rounded(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            TextRenderer.DrawText(e.Graphics, text, Font, ClientRectangle, System.Drawing.Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}
