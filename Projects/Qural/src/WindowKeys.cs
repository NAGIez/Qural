using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Qural
{
    // Вывести окно программы вперёд и нажать в нём сочетание клавиш (для кнопок деки: «новый чат», «стоп» в Claude).
    static class WindowKeys
    {
        // process — имя exe без .exe; keys — "ctrl+n", "esc", "ctrl+shift+d" или пусто (только вывести окно).
        // null — получилось, иначе текст ошибки.
        // text — сначала напечатать строку (например, «/compact»); replace — перед этим выделить всё в поле (Ctrl+A),
        // чтобы строка заменила недописанное, а не приклеилась к нему.
        public static string Send(string process, string keys, string text = null, bool replace = false)
        {
            ushort[] mods, main;
            string err = Parse(keys ?? "", out mods, out main);
            if (err != null) return err;

            IntPtr w = FindMain(process);
            if (w == IntPtr.Zero) return "Окно " + process + " не найдено — программа не запущена или свёрнута в трей";
            if (!Activate(w)) return "Windows не дала вывести окно " + process + " вперёд";
            if (main.Length == 0 && string.IsNullOrEmpty(text)) return null;

            System.Threading.Thread.Sleep(60); // окно успевает принять фокус
            if (!string.IsNullOrEmpty(text))
            {
                var t = new List<INPUT>();
                if (replace) { t.Add(Key(0x11, false)); t.Add(Key(0x41, false)); t.Add(Key(0x41, true)); t.Add(Key(0x11, true)); }
                foreach (char ch in text)
                {
                    t.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wScan = ch, dwFlags = 4 /*UNICODE*/ } });
                    t.Add(new INPUT { type = 1, ki = new KEYBDINPUT { wScan = ch, dwFlags = 4 | 2 /*UNICODE|KEYUP*/ } });
                }
                var ta = t.ToArray();
                if (SendInput((uint)ta.Length, ta, Marshal.SizeOf(typeof(INPUT))) != ta.Length)
                    return "Не удалось ввести текст (" + Marshal.GetLastWin32Error() + ")";
                if (main.Length == 0) return null;
                System.Threading.Thread.Sleep(120); // поле ввода успевает показать текст (и подсказку команды)
            }
            var inputs = new List<INPUT>();
            foreach (var m in mods) inputs.Add(Key(m, false));
            foreach (var k in main) inputs.Add(Key(k, false));
            for (int i = main.Length - 1; i >= 0; i--) inputs.Add(Key(main[i], true));
            for (int i = mods.Length - 1; i >= 0; i--) inputs.Add(Key(mods[i], true));
            var arr = inputs.ToArray();
            uint sent = SendInput((uint)arr.Length, arr, Marshal.SizeOf(typeof(INPUT)));
            return sent == arr.Length ? null : "Не удалось нажать клавиши (" + Marshal.GetLastWin32Error() + ")";
        }

        // ── разбор "ctrl+shift+d" ──
        static string Parse(string keys, out ushort[] mods, out ushort[] main)
        {
            var m = new List<ushort>(); var k = new List<ushort>();
            mods = new ushort[0]; main = new ushort[0];
            foreach (var raw in keys.ToLowerInvariant().Split('+'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                switch (t)
                {
                    case "ctrl": case "control": case "cmdorctrl": m.Add(0x11); continue;
                    case "shift": m.Add(0x10); continue;
                    case "alt": m.Add(0x12); continue;
                    case "win": case "meta": m.Add(0x5B); continue;
                    case "esc": case "escape": k.Add(0x1B); continue;
                    case "enter": case "return": k.Add(0x0D); continue;
                    case "space": k.Add(0x20); continue;
                    case "tab": k.Add(0x09); continue;
                    case "backspace": k.Add(0x08); continue;
                    case "delete": case "del": k.Add(0x2E); continue;
                    case "up": k.Add(0x26); continue;
                    case "down": k.Add(0x28); continue;
                    case "left": k.Add(0x25); continue;
                    case "right": k.Add(0x27); continue;
                    case "home": k.Add(0x24); continue;
                    case "end": k.Add(0x23); continue;
                    case "pageup": k.Add(0x21); continue;
                    case "pagedown": k.Add(0x22); continue;
                }
                int f;
                if (t.Length > 1 && t[0] == 'f' && int.TryParse(t.Substring(1), out f) && f >= 1 && f <= 24) { k.Add((ushort)(0x70 + f - 1)); continue; }
                if (t.Length == 1)
                {
                    char c = t[0];
                    if (c >= 'a' && c <= 'z') { k.Add((ushort)char.ToUpperInvariant(c)); continue; }
                    if (c >= '0' && c <= '9') { k.Add(c); continue; }
                    short vk = VkKeyScan(c); // «/», «.», «;» и т. п. — по текущей раскладке
                    if (vk != -1) { k.Add((ushort)(vk & 0xFF)); continue; }
                }
                return "Непонятная клавиша «" + raw.Trim() + "» в «" + keys + "»";
            }
            if (k.Count == 0 && m.Count > 0) return "В «" + keys + "» нет основной клавиши";
            mods = m.ToArray(); main = k.ToArray();
            return null;
        }

        // ── окно программы: видимое, с заголовком, самое крупное ──
        static IntPtr FindMain(string process)
        {
            IntPtr best = IntPtr.Zero; long bestArea = -1;
            EnumWindows((h, l) =>
            {
                if (!IsWindowVisible(h) && !IsIconic(h)) return true;
                if (GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
                if ((GetWindowLong(h, -20 /*GWL_EXSTYLE*/) & 0x80 /*WS_EX_TOOLWINDOW*/) != 0) return true;
                if (GetWindowTextLength(h) == 0) return true;
                uint pid; GetWindowThreadProcessId(h, out pid);
                if (!string.Equals(NameOf(pid), process, StringComparison.OrdinalIgnoreCase)) return true;
                RECT r; GetWindowRect(h, out r);
                long area = IsIconic(h) ? 1 : (long)(r.R - r.L) * (r.B - r.T);
                if (area > bestArea) { bestArea = area; best = h; }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        static string NameOf(uint pid)
        {
            IntPtr h = OpenProcess(0x1000, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                var sb = new StringBuilder(1024); int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? System.IO.Path.GetFileNameWithoutExtension(sb.ToString()) : null;
            }
            finally { CloseHandle(h); }
        }

        // Фоновой программе Windows не даёт вывести чужое окно вперёд — ненадолго присоединяемся к потоку окна,
        // которое сейчас впереди (тот же приём, что в TrayApp.OpenWindow).
        static bool Activate(IntPtr w)
        {
            if (IsIconic(w)) ShowWindow(w, 9 /*SW_RESTORE*/);
            IntPtr fg = GetForegroundWindow();
            if (fg == w) return true;
            uint pid, fgThread = fg == IntPtr.Zero ? 0 : GetWindowThreadProcessId(fg, out pid), me = GetCurrentThreadId();
            if (fgThread != 0 && fgThread != me)
            {
                AttachThreadInput(me, fgThread, true);
                BringWindowToTop(w);
                SetForegroundWindow(w);
                AttachThreadInput(me, fgThread, false);
            }
            else SetForegroundWindow(w);
            for (int i = 0; i < 10 && GetForegroundWindow() != w; i++) System.Threading.Thread.Sleep(15);
            return GetForegroundWindow() == w;
        }

        static INPUT Key(ushort vk, bool up)
        {
            uint flags = up ? 2u /*KEYUP*/ : 0u;
            // стрелки, Home/End, Delete и т. п. — «расширенные» клавиши
            if (vk >= 0x21 && vk <= 0x2E) flags |= 1u /*EXTENDEDKEY*/;
            return new INPUT { type = 1, ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)MapVirtualKey(vk, 0), dwFlags = flags } };
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
        // INPUT: type + объединение (MOUSEINPUT — самый большой вариант, 32 байта на x64)
        [StructLayout(LayoutKind.Explicit)]
        struct INPUT
        {
            [FieldOffset(0)] public uint type;
            [FieldOffset(8)] public KEYBDINPUT ki;
            [FieldOffset(8)] public MOUSEPAD pad;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEPAD { public int dx, dy; public uint data, flags, time; public IntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int L, T, R, B; }

        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint type);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern short VkKeyScan(char c);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int idx);
        [DllImport("user32.dll")] static extern int GetWindowTextLength(IntPtr h);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
    }
}
