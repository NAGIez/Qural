using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Qural
{
    // NVIDIA: цифровая интенсивность и оттенок (те же значения, что в Панели управления NVIDIA).
    static class Nv
    {
        [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
        static extern IntPtr QueryInterface(uint id);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int InitD();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int EnumD(int index, out IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int NameD(IntPtr handle, StringBuilder name);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int DvcD(IntPtr handle, uint outputId, ref DvcInfoEx info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HueGetD(IntPtr handle, uint outputId, ref HueInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int HueSetD(IntPtr handle, uint outputId, uint angle);
        // яркость/контраст/гамма так же, как Панель NVIDIA: таблица в самом драйвере (функции не описаны NVIDIA,
        // номера и формат — как в открытых nvBrightness и NvProfileSwitcher)
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int IdByNameD([MarshalAs(UnmanagedType.LPStr)] string name, out uint id);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TargetGammaD(uint displayId, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        struct DvcInfoEx { public uint Version; public int Current, Min, Max, Default; }
        [StructLayout(LayoutKind.Sequential)]
        struct HueInfo { public uint Version, Current, Default; }

        const uint DvcVersion = 20 | (1 << 16);
        const uint HueVersion = 12 | (1 << 16);

        static bool inited, ok;
        static EnumD enumHandles; static NameD getName;
        static DvcD getDvc, setDvc; static HueGetD getHue; static HueSetD setHue;
        static IdByNameD idByName; static TargetGammaD setTargetGamma;

        public static bool Available { get { Init(); return ok; } }

        static void Init()
        {
            if (inited) return;
            inited = true;
            try
            {
                var init = Get<InitD>(0x0150E828);
                enumHandles = Get<EnumD>(0x9ABDD40D);
                getName = Get<NameD>(0x22A78B05);
                getDvc = Get<DvcD>(0x0E45002D);
                setDvc = Get<DvcD>(0x4A82C2B1);
                getHue = Get<HueGetD>(0x95B64341);
                setHue = Get<HueSetD>(0xF5A0F22C);
                idByName = Get<IdByNameD>(0xAE457190);
                setTargetGamma = Get<TargetGammaD>(0x7082A053);
                ok = init != null && enumHandles != null && getName != null && getDvc != null
                     && setDvc != null && getHue != null && setHue != null && init() == 0;
            }
            catch (Exception) { ok = false; }
        }

        static T Get<T>(uint id) where T : class
        {
            IntPtr p = QueryInterface(id);
            return p == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer(p, typeof(T)) as T;
        }

        static IntPtr Find(string gdiName)
        {
            if (!Available) return IntPtr.Zero;
            for (int i = 0; i < 32; i++)
            {
                IntPtr h;
                if (enumHandles(i, out h) != 0) break;
                var sb = new StringBuilder(64);
                if (getName(h, sb) == 0 && string.Equals(sb.ToString(), gdiName, StringComparison.OrdinalIgnoreCase))
                    return h;
            }
            return IntPtr.Zero;
        }

        public static bool SetVibrance(string gdiName, int level)
        {
            IntPtr h = Find(gdiName);
            if (h == IntPtr.Zero) return false;
            var info = new DvcInfoEx { Version = DvcVersion, Current = Clamp(level, 0, 100) };
            return setDvc(h, 0, ref info) == 0;
        }

        public static bool SetHue(string gdiName, int angle)
        {
            IntPtr h = Find(gdiName);
            if (h == IntPtr.Zero) return false;
            return setHue(h, 0, (uint)(((angle % 360) + 360) % 360)) == 0;
        }

        public static bool Read(string gdiName, out int vibrance, out int hue)
        {
            vibrance = 50; hue = 0;
            IntPtr h = Find(gdiName);
            if (h == IntPtr.Zero) return false;
            var d = new DvcInfoEx { Version = DvcVersion };
            var u = new HueInfo { Version = HueVersion };
            if (getDvc(h, 0, ref d) != 0 || getHue(h, 0, ref u) != 0) return false;
            vibrance = d.Current; hue = (int)u.Current;
            return true;
        }

        // Таблица NV_GAMMA_CORRECTION_EX: версия, 1024 × (R,G,B) float 0..1, служебное поле = 1. Всего 12296 байт.
        const int GammaPoints = 1024, GammaBytes = 4 + GammaPoints * 3 * 4 + 4;
        static bool targetGammaBroken;

        public static bool CanSetGamma { get { return Available && idByName != null && setTargetGamma != null && !targetGammaBroken; } }

        static bool DisplayId(string gdiName, out uint id)
        {
            id = 0;
            // драйвер ждёт «\\DISPLAY1»; на всякий случай пробуем и «\\.\DISPLAY1»
            string alt = gdiName.StartsWith(@"\\.\") ? @"\\" + gdiName.Substring(4) : gdiName;
            return idByName(alt, out id) == 0 || idByName(gdiName, out id) == 0;
        }

        public static bool SetGamma(string gdiName, int brightness, int contrast, double gamma)
        {
            if (!CanSetGamma) return false;
            uint id;
            try { if (!DisplayId(gdiName, out id)) return false; }
            catch (Exception) { targetGammaBroken = true; return false; }
            IntPtr mem = Marshal.AllocHGlobal(GammaBytes);
            try
            {
                Marshal.WriteInt32(mem, 0, GammaBytes | (1 << 16));
                var row = new float[GammaPoints * 3];
                for (int i = 0; i < GammaPoints; i++)
                {
                    float v = (float)Gamma.Value(i / (double)(GammaPoints - 1), brightness, contrast, gamma);
                    row[i * 3] = row[i * 3 + 1] = row[i * 3 + 2] = v;
                }
                Marshal.Copy(row, 0, mem + 4, row.Length);
                Marshal.WriteInt32(mem, GammaBytes - 4, 1);
                return setTargetGamma(id, mem) == 0;
            }
            catch (Exception) { targetGammaBroken = true; return false; }
            finally { Marshal.FreeHGlobal(mem); }
        }

        static int Clamp(int v, int lo, int hi) { return v < lo ? lo : v > hi ? hi : v; }
    }

    // Яркость / контраст / гамма: таблицей в драйвере NVIDIA, иначе гамма-таблицей Windows.
    static class Gamma
    {
        [DllImport("gdi32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDC(string driver, string device, string output, IntPtr devMode);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool SetDeviceGammaRamp(IntPtr dc, ushort[] ramp);

        public static bool IsDefault(int brightness, int contrast, double gamma)
        {
            return brightness == 50 && contrast == 50 && Math.Abs(gamma - 1.0) < 0.001;
        }

        // Формула Панели NVIDIA. Ползунки Qural 0–100 (50 — без изменений) = ползунки панели;
        // внутри драйвера это 80–120 (100 — без изменений). Гамма 0.30–2.80.
        public static double Value(double x, int brightness, int contrast, double gamma)
        {
            double b = (80 + 0.4 * Math.Max(0, Math.Min(100, brightness)) - 100) / 100.0; // −0.2 .. +0.2
            double c = (80 + 0.4 * Math.Max(0, Math.Min(100, contrast)) - 100) / 100.0;   // −0.2 .. +0.2
            double g = Math.Max(0.3, Math.Min(2.8, gamma));
            double v = c <= 0 ? (c + 1) * (x - 0.5) : (x - 0.5) / (1 - c);
            v = v + b + 0.5;
            if (v < 0) v = 0; if (v > 1) v = 1;
            v = Math.Pow(v, 1.0 / g);
            return v < 0 ? 0 : v > 1 ? 1 : v;
        }

        // Сначала — как Панель NVIDIA, таблицей в драйвере: её не сбрасывают игры на весь экран.
        // Тогда таблица Windows — без изменений, чтобы не наложилось дважды.
        // Нет NVIDIA или драйвер отказал — таблица Windows по той же формуле.
        public static bool Apply(string gdiName, int brightness, int contrast, double gamma)
        {
            if (Nv.SetGamma(gdiName, brightness, contrast, gamma))
            {
                SetRamp(gdiName, 50, 50, 1.0);
                return true;
            }
            return SetRamp(gdiName, brightness, contrast, gamma);
        }

        static bool SetRamp(string gdiName, int brightness, int contrast, double gamma)
        {
            var ramp = new ushort[768];
            for (int i = 0; i < 256; i++)
            {
                ushort v = (ushort)Math.Round(Value(i / 255.0, brightness, contrast, gamma) * 65535);
                ramp[i] = ramp[256 + i] = ramp[512 + i] = v;
            }
            IntPtr dc = CreateDC(null, gdiName, null, IntPtr.Zero);
            if (dc == IntPtr.Zero) return false;
            try { return SetDeviceGammaRamp(dc, ramp); }
            finally { DeleteDC(dc); }
        }
    }

    class MonitorInfo
    {
        public string Key;      // путь устройства, не меняется между перезагрузками
        public string Name;     // «Samsung Odyssey»
        public string GdiName;  // \\.\DISPLAY6
    }

    // Список подключённых мониторов с их настоящими именами (из EDID).
    static class Displays
    {
        [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }

        [StructLayout(LayoutKind.Sequential)]
        struct PathSource { public LUID Adapter; public uint Id; public uint ModeIdx; public uint Status; }
        [StructLayout(LayoutKind.Sequential)]
        struct PathTarget
        {
            public LUID Adapter; public uint Id; public uint ModeIdx; public uint OutputTech; public uint Rotation;
            public uint Scaling; public uint RefreshNum, RefreshDen; public uint ScanLine; public int Available; public uint Status;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct PathInfo { public PathSource Source; public PathTarget Target; public uint Flags; }
        [StructLayout(LayoutKind.Sequential, Size = 64)]
        struct ModeInfo { public uint InfoType; }

        [StructLayout(LayoutKind.Sequential)]
        struct Header { public uint Type; public uint Size; public LUID Adapter; public uint Id; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SourceName { public Header H; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Gdi; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TargetName
        {
            public Header H; public uint Flags; public uint OutputTech; public ushort Manufacturer; public ushort Product;
            public uint Connector;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Friendly;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
        }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PathInfo[] p, ref uint modes, [Out] ModeInfo[] m, IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SourceName s);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TargetName t);

        const uint QDC_ONLY_ACTIVE_PATHS = 2;

        public static List<MonitorInfo> GetActive()
        {
            var list = new List<MonitorInfo>();
            uint np, nm;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return list;
            var paths = new PathInfo[np];
            var modes = new ModeInfo[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return list;

            for (int i = 0; i < np; i++)
            {
                var src = new SourceName();
                src.H.Type = 1; src.H.Size = (uint)Marshal.SizeOf(typeof(SourceName));
                src.H.Adapter = paths[i].Source.Adapter; src.H.Id = paths[i].Source.Id;
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;

                var tgt = new TargetName();
                tgt.H.Type = 2; tgt.H.Size = (uint)Marshal.SizeOf(typeof(TargetName));
                tgt.H.Adapter = paths[i].Target.Adapter; tgt.H.Id = paths[i].Target.Id;
                DisplayConfigGetDeviceInfo(ref tgt);

                var m = new MonitorInfo();
                m.GdiName = src.Gdi;
                m.Key = string.IsNullOrEmpty(tgt.Path) ? src.Gdi : tgt.Path;
                m.Name = string.IsNullOrEmpty(tgt.Friendly) ? "Монитор " + (i + 1) : tgt.Friendly;
                if (list.Exists(x => x.Key == m.Key)) continue; // режим клонирования
                list.Add(m);
            }
            // одинаковые имена — добавить номер
            var dup = new HashSet<string>();
            foreach (var m in list)
                if (list.FindAll(x => x.Name == m.Name).Count > 1) dup.Add(m.Name);
            foreach (var m in list)
                if (dup.Contains(m.Name)) m.Name = m.Name + " (" + m.GdiName.Replace(@"\\.\", "") + ")";
            return list;
        }
    }
}
