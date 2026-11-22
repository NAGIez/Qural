using System;
using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using System.Threading;

namespace Qural
{
    // Нагрузка ПК для кнопки деки: загрузка процессора (GetSystemTimes), занятая память, видеокарта NVIDIA — температура, загрузка,
    // мощность (nvml.dll из драйвера, без nvidia-smi). Температура процессора — только если запущен
    // LibreHardwareMonitor (или OpenHardwareMonitor): Windows сама её не отдаёт, а свой драйвер мы не ставим.
    // Опрос раз в секунду в своём потоке и только пока на деке видна такая кнопка (Watching).
    sealed class Stats : IDisposable
    {
        public sealed class Info { public int Cpu = -1, CpuTemp = -1, Gpu = -1, GpuTemp = -1, GpuPower = -1, Ram = -1, RamUsedGb = -1; }

        Info current = new Info();
        readonly object gate = new object();
        public Info Current { get { lock (gate) return current; } }
        public event Action Changed;            // в фоновом потоке

        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool watching, stopping;

        public Stats() { new Thread(Loop) { IsBackground = true, Name = "Qural.Stats" }.Start(); }

        public bool Watching
        {
            get { return watching; }
            set { if (watching == value) return; watching = value; if (value) wake.Set(); }
        }

        void Loop()
        {
            IntPtr gpu = IntPtr.Zero; bool nvml = false, nvmlTried = false;
            string tempNs = null; DateTime tempLook = DateTime.MinValue;
            while (!stopping)
            {
                wake.WaitOne(watching ? 1000 : Timeout.Infinite);
                if (stopping || !watching) continue;
                var s = new Info();
                // процессор: доля времени не в простое между замерами (GetSystemTimes — не зависит от языка Windows;
                // счётчики производительности на русской Windows называются по-русски)
                try
                {
                    long idle, kernel, user;
                    if (GetSystemTimes(out idle, out kernel, out user))
                    {
                        long dIdle = idle - lastIdle, dTotal = (kernel - lastKernel) + (user - lastUser);   // kernel включает idle
                        if (lastKernel != 0 && dTotal > 0) s.Cpu = (int)Math.Round(Math.Min(100, Math.Max(0, 100.0 * (dTotal - dIdle) / dTotal)));
                        lastIdle = idle; lastKernel = kernel; lastUser = user;
                    }
                }
                catch (Exception) { }
                // оперативная память: занято, % и ГБ
                try
                {
                    var ms = new MemStatus { Length = (uint)Marshal.SizeOf(typeof(MemStatus)) };
                    if (GlobalMemoryStatusEx(ref ms) && ms.TotalPhys > 0)
                    {
                        s.Ram = (int)ms.MemoryLoad;
                        s.RamUsedGb = (int)Math.Round((ms.TotalPhys - ms.AvailPhys) / 1073741824.0);
                    }
                }
                catch (Exception) { }
                // видеокарта NVIDIA
                try
                {
                    if (!nvmlTried) { nvmlTried = true; nvml = nvmlInit_v2() == 0 && nvmlDeviceGetHandleByIndex_v2(0, out gpu) == 0; }
                    if (nvml)
                    {
                        uint t, mw; NvUtil u;
                        if (nvmlDeviceGetTemperature(gpu, 0, out t) == 0) s.GpuTemp = (int)t;
                        if (nvmlDeviceGetUtilizationRates(gpu, out u) == 0) s.Gpu = (int)u.Gpu;
                        if (nvmlDeviceGetPowerUsage(gpu, out mw) == 0) s.GpuPower = (int)(mw / 1000);
                    }
                }
                catch (Exception) { nvml = false; }   // нет драйвера NVIDIA — видеокарту не показываем
                // температура процессора из LibreHardwareMonitor / OpenHardwareMonitor (если запущены)
                try
                {
                    if (tempNs == null && DateTime.UtcNow > tempLook)
                    {
                        tempLook = DateTime.UtcNow.AddSeconds(30);   // ищем программу не чаще раза в 30 с
                        foreach (var ns in new[] { @"root\LibreHardwareMonitor", @"root\OpenHardwareMonitor" })
                            if (CpuTemp(ns) > 0) { tempNs = ns; break; }
                    }
                    if (tempNs != null) { s.CpuTemp = CpuTemp(tempNs); if (s.CpuTemp <= 0) { s.CpuTemp = -1; tempNs = null; } }
                }
                catch (Exception) { tempNs = null; }

                Info old;
                lock (gate) { old = current; current = s; }
                if (old.Cpu != s.Cpu || old.CpuTemp != s.CpuTemp || old.Gpu != s.Gpu || old.GpuTemp != s.GpuTemp || old.GpuPower != s.GpuPower ||
                    old.Ram != s.Ram || old.RamUsedGb != s.RamUsedGb)
                { var h = Changed; if (h != null) try { h(); } catch (Exception) { } }
            }
            if (nvml) try { nvmlShutdown(); } catch (Exception) { }
        }

        // самый подходящий датчик температуры процессора: Tctl/Tdie (AMD), Package (Intel), иначе первый процессорный
        static int CpuTemp(string ns)
        {
            try
            {
                using (var q = new ManagementObjectSearcher(ns, "SELECT Name, Value, Identifier FROM Sensor WHERE SensorType='Temperature'"))
                {
                    double best = -1; int rank = 0;
                    foreach (ManagementObject o in q.Get())
                    {
                        string id = (o["Identifier"] ?? "").ToString().ToLowerInvariant(), name = (o["Name"] ?? "").ToString().ToLowerInvariant();
                        if (!id.Contains("cpu")) continue;
                        double v = Convert.ToDouble(o["Value"]);
                        int r = name.Contains("tctl") || name.Contains("tdie") ? 3 : name.Contains("package") ? 2 : 1;
                        if (r > rank && v > 0) { rank = r; best = v; }
                    }
                    return best > 0 ? (int)Math.Round(best) : -1;
                }
            }
            catch (Exception) { return -1; }   // программы нет — пространства имён WMI тоже нет
        }

        public void Dispose() { stopping = true; wake.Set(); }

        long lastIdle, lastKernel, lastUser;
        [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
        [StructLayout(LayoutKind.Sequential)] struct NvUtil { public uint Gpu, Memory; }
        [StructLayout(LayoutKind.Sequential)]
        struct MemStatus { public uint Length, MemoryLoad; public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual; }
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool GlobalMemoryStatusEx(ref MemStatus s);
        [DllImport("nvml.dll")] static extern int nvmlInit_v2();
        [DllImport("nvml.dll")] static extern int nvmlShutdown();
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetTemperature(IntPtr device, int sensor, out uint temp);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvUtil util);
        [DllImport("nvml.dll")] static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);
    }
}
