using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Qural
{
    // Команды из командной строки — для кнопок Ulanzi Deck / Stream Deck, ярлыков и планировщика.
    //   Qural.exe --profile "Обычный" | --profile 2 | --profile next
    //   Qural.exe --reset-colors
    //   Qural.exe --volume Spotify up | down | +10 | -10 | 50 | mute       (вместо имени — active: программа в фокусе)
    //   Qural.exe --color on | off | toggle      Qural.exe --sound on | off | toggle
    //   Qural.exe --open
    //   Qural.exe --shot | --shot full | --shot window   (область / монитор под курсором / окно в фокусе)
    // Если Qural уже запущен, второй запуск находит его скрытое окно, передаёт команду (WM_COPYDATA) и сразу выходит.
    static class Commands
    {
        public const string WindowTitle = "Qural.Commands.v1";
        public const int WM_COPYDATA = 0x004A;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SendMessageTimeout(IntPtr h, int msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int pid);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out int pid);

        [StructLayout(LayoutKind.Sequential)]
        struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

        const int Marker = 0x51524C31; // «QRL1» — отличаем свои сообщения от чужих

        static readonly string[] Known = { "--profile", "--reset-colors", "--volume", "--color", "--sound", "--open", "--shot", "--rec" };

        public static bool IsCommand(string[] args)
        {
            foreach (var a in args)
                if (Array.IndexOf(Known, a.ToLowerInvariant()) >= 0) return true;
            return false;
        }

        // Одна попытка без ожидания — если Qural уже работает и его окно на месте
        public static bool TrySendOnce(string[] args) { return Send(args, 1); }

        // Передать команду запущенному Qural. Окно может ещё создаваться — пробуем до 3 секунд.
        public static bool SendToRunning(string[] args) { return Send(args, 30); }

        static bool Send(string[] args, int attempts)
        {
            string text = string.Join("\0", args);
            byte[] bytes = Encoding.Unicode.GetBytes(text);
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                IntPtr w = FindWindow(null, WindowTitle);
                if (w != IntPtr.Zero)
                {
                    // если у этого запуска есть право на передний план — передаём его Qural (для --open); нет — Qural справится сам
                    int pid; GetWindowThreadProcessId(w, out pid);
                    if (pid != 0) AllowSetForegroundWindow(pid);
                    IntPtr mem = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
                    try
                    {
                        Marshal.Copy(bytes, 0, mem, bytes.Length);
                        var cds = new COPYDATASTRUCT { dwData = (IntPtr)Marker, cbData = bytes.Length, lpData = mem };
                        IntPtr res;
                        if (SendMessageTimeout(w, WM_COPYDATA, IntPtr.Zero, ref cds, 0x0002 /*ABORTIFHUNG*/, 2000, out res) != IntPtr.Zero && res != IntPtr.Zero)
                            return true;
                        if (Marshal.GetLastWin32Error() == 5) return false; // отказано в доступе — ждать бесполезно
                    }
                    finally { Marshal.FreeHGlobal(mem); }
                }
                if (attempt + 1 < attempts) System.Threading.Thread.Sleep(100);
            }
            return false;
        }

        // Прочитать команду из WM_COPYDATA (null — сообщение не наше)
        public static string[] Read(IntPtr lParam)
        {
            try
            {
                var cds = (COPYDATASTRUCT)Marshal.PtrToStructure(lParam, typeof(COPYDATASTRUCT));
                if (cds.dwData != (IntPtr)Marker || cds.cbData < 0 || cds.cbData > 64 * 1024) return null;
                var bytes = new byte[cds.cbData];
                if (cds.cbData > 0) Marshal.Copy(cds.lpData, bytes, 0, cds.cbData);
                return Encoding.Unicode.GetString(bytes).Split('\0');
            }
            catch (Exception) { return null; }
        }

        // Выполнить команды. Возвращает текст ошибок (пусто — всё хорошо).
        public static string Run(TrayApp app, string[] args)
        {
            var errors = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].ToLowerInvariant();
                string next = i + 1 < args.Length ? args[i + 1] : null;
                switch (a)
                {
                    case "--tray": break;

                    case "--open": app.OpenWindow(); break;

                    case "--rec":
                        {
                            // --rec [toggle|start|stop|pause] [region|screen|window]
                            string action = "toggle", mode = null;
                            if (next != null && !next.StartsWith("--")) { action = next; i++; }
                            string third = i + 1 < args.Length ? args[i + 1] : null;
                            if (third != null && !third.StartsWith("--")) { mode = third; i++; }
                            string err = app.RecCommand(action, mode);
                            if (err != null) errors.Add(err);
                            break;
                        }

                    case "--shot":
                        {
                            // --shot [region|full|window]; без слова — выделить область
                            string mode = "region";
                            if (next != null && !next.StartsWith("--")) { mode = next; i++; }
                            string err = app.ShotCommand(mode);
                            if (err != null) errors.Add(err);
                            break;
                        }

                    case "--reset-colors": app.ResetColors(); break;

                    case "--profile":
                        if (next == null) { errors.Add("--profile: укажите название, номер или next"); break; }
                        i++;
                        if (!app.SwitchColorByName(next)) errors.Add("Нет профиля цвета «" + next + "»");
                        else if (app.General.ColorOff && !TurnsColorOnLater(args, i))
                            errors.Add("«Цвет» выключен — профиль выбран, но не применён. Включите «Цвет» в Qural (или --color on)");
                        break;

                    case "--color":
                    case "--sound":
                        {
                            if (next == null) { errors.Add(a + ": укажите on, off или toggle"); break; }
                            i++;
                            string mod = a == "--color" ? "color" : "sound";
                            bool now = mod == "color" ? !app.General.ColorOff : !app.General.SoundOff;
                            string v = next.ToLowerInvariant();
                            if (v == "on") app.SetModule(mod, true);
                            else if (v == "off") app.SetModule(mod, false);
                            else if (v == "toggle") app.SetModule(mod, !now);
                            else errors.Add(a + ": вместо «" + next + "» нужно on, off или toggle");
                            break;
                        }

                    case "--volume":
                        {
                            string value = i + 2 < args.Length ? args[i + 2] : null;
                            if (next == null || value == null) { errors.Add("--volume: нужно имя программы и действие, например --volume Spotify up"); i = args.Length; break; }
                            i += 2;
                            string err = app.VolumeCommand(next, value);
                            if (err != null) errors.Add(err);
                            break;
                        }

                    default:
                        errors.Add("Неизвестная команда: " + args[i]);
                        break;
                }
            }
            return string.Join("\n", errors.ToArray());
        }

        // дальше в той же команде есть --color on/toggle — тогда выключенный «Цвет» включат следом, предупреждать не надо
        static bool TurnsColorOnLater(string[] args, int from)
        {
            for (int j = from + 1; j + 1 < args.Length; j++)
                if (args[j].Equals("--color", StringComparison.OrdinalIgnoreCase) &&
                    (args[j + 1].Equals("on", StringComparison.OrdinalIgnoreCase) || args[j + 1].Equals("toggle", StringComparison.OrdinalIgnoreCase))) return true;
            return false;
        }

        // Разобрать действие громкости: up / down / +N / -N / N / N% / mute
        public static bool ParseVolume(string s, out string kind, out int amount)
        {
            kind = null; amount = 0;
            s = (s ?? "").Trim().ToLowerInvariant().TrimEnd('%');
            if (s == "up" || s == "down" || s == "mute") { kind = s; return true; }
            int n;
            if ((s.StartsWith("+") || s.StartsWith("-")) && int.TryParse(s.Substring(1), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n >= 1 && n <= 100)
            { kind = s[0] == '+' ? "inc" : "dec"; amount = n; return true; }
            if (int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n) && n >= 0 && n <= 100) { kind = "set"; amount = n; return true; }
            return false;
        }
    }
}
