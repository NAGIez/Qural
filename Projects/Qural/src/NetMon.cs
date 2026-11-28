using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace Qural
{
    // «Сеть» → «Программы»: сколько трафика у каждой программы и через какое подключение.
    // Windows отдаёт это только программе с правами администратора, поэтому считает отдельный маленький процесс:
    // Qural.exe --net-mon (его запускает та же задача «Qural net-merge», без окна UAC).
    // Он подписывается на системные сетевые события (ETW, Microsoft-Windows-Kernel-Network: TCP/UDP приём и отправка
    // с номером процесса и размером) — сеть не опрашивает, события приходят пачками раз в секунду, на каждое —
    // несколько сложений. Раз в секунду пишет итоги в %LOCALAPPDATA%\Qural\net-apps.json; основной Qural читает их.
    // Выходит сам, когда основной Qural закрыт (или появился файл net-apps.stop).
    static class NetMon
    {
        const string Session = "Qural-NetMon";
        static readonly Guid KernelNetwork = new Guid("7DD42A49-5329-4832-8DFD-43D979153A88");
        public static string Folder { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qural"); } }
        public static string OutFile { get { return Path.Combine(Folder, "net-apps.json"); } }
        public static string StopFile { get { return Path.Combine(Folder, "net-apps.stop"); } }
        public const string MutexName = "Global\\Qural_NetMon";

        sealed class Acc
        {
            public string Name = "", Path = ""; public long In, Out; public bool Relay;   // Relay — это net-merge (пересылает чужой трафик)
            public readonly Dictionary<string, long[]> ByLocal = new Dictionary<string, long[]>();
            public readonly Dictionary<string, long[]> ByVia = new Dictionary<string, long[]>();   // трафик через объединение — по настоящим подключениям
        }
        // сервер (адрес и порт) → локальный адрес подключения, через которое net-merge с ним говорит
        static readonly Dictionary<string, string> route = new Dictionary<string, string>();
        static HashSet<string> tunAddrs = new HashSet<string>();
        static readonly object gate = new object();
        static readonly Dictionary<uint, Acc> byPid = new Dictionary<uint, Acc>();
        static readonly Dictionary<string, Acc> byName = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        static HashSet<uint> local4 = new HashSet<uint>();
        static HashSet<string> local6 = new HashSet<string>();
        static EventRecordCallback callback;   // держим ссылку: иначе сборщик мусора уберёт делегат, пока Windows его вызывает
        static long events;

        public static bool Alive
        {
            get
            {
                try { var f = new FileInfo(OutFile); return f.Exists && (DateTime.UtcNow - f.LastWriteTimeUtc).TotalSeconds < 8; }
                catch (Exception) { return false; }
            }
        }

        // ── процесс-помощник ──
        public static void Run()
        {
            bool created;
            using (var m = new Mutex(true, MutexName, out created))
            {
                if (!created) return;   // уже работает
                try { File.Delete(StopFile); } catch (Exception) { }
                Directory.CreateDirectory(Folder);
                StopSession();
                long session;
                IntPtr props = Props();
                uint err = StartTrace(out session, Session, props);
                Marshal.FreeHGlobal(props);
                if (err != 0) { Log("ETW: StartTrace " + err); return; }
                var g = KernelNetwork;
                err = EnableTraceEx2(session, ref g, 1 /*ENABLE*/, 4 /*info*/, 0x30 /*IPv4 | IPv6*/, 0, 0, IntPtr.Zero);
                if (err != 0) { Log("ETW: EnableTraceEx2 " + err); StopSession(); return; }

                callback = OnEvent;
                var lf = new EVENT_TRACE_LOGFILE { LoggerName = Session, ProcessTraceMode = 0x00000100 | 0x10000000 /*REAL_TIME | EVENT_RECORD*/, EventRecordCallback = callback };
                long trace = OpenTrace(ref lf);
                if (trace == -1 || trace == 0) { Log("ETW: OpenTrace " + Marshal.GetLastWin32Error()); StopSession(); return; }
                var reader = new Thread(() => { try { ProcessTrace(new[] { trace }, 1, IntPtr.Zero, IntPtr.Zero); } catch (Exception) { } })
                { IsBackground = true, Name = "Qural.NetMon.etw" };
                reader.Start();
                Log("помощник трафика запущен");

                int lonely = 0;
                var me = Process.GetCurrentProcess().Id;
                for (int tick = 0; ; tick++)
                {
                    if (tick % 10 == 0) RefreshLocal();
                    Write();
                    Thread.Sleep(1000);
                    if (File.Exists(StopFile)) break;
                    // основной Qural (не этот процесс и не короткий --net-run) закрылся — выходим
                    int others = 0;
                    foreach (var p in Process.GetProcessesByName("Qural")) if (p.Id != me) others++;
                    lonely = others == 0 ? lonely + 1 : 0;
                    if (lonely >= 5) break;
                }
                StopSession();
                CloseTrace(trace);
                reader.Join(3000);
                try { File.Delete(OutFile); File.Delete(StopFile); } catch (Exception) { }
                Log("помощник трафика остановлен");
            }
        }

        static void Log(string s) { try { RecLog.Write("сеть: " + s); } catch (Exception) { } }

        // локальные адреса компьютера — по ним событие относится к подключению
        static void RefreshLocal()
        {
            var v4 = new HashSet<uint>(); var v6 = new HashSet<string>(); var tun = new HashSet<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                    foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    {
                        if ((ni.Name + " " + ni.Description).IndexOf("net-merge", StringComparison.OrdinalIgnoreCase) >= 0) tun.Add(u.Address.ToString());
                        if (u.Address.AddressFamily == AddressFamily.InterNetwork) v4.Add(BitConverter.ToUInt32(u.Address.GetAddressBytes(), 0));
                        else if (u.Address.AddressFamily == AddressFamily.InterNetworkV6) v6.Add(new IPAddress(u.Address.GetAddressBytes()).ToString());
                    }
            }
            catch (Exception) { }
            local4 = v4; local6 = v6; tunAddrs = tun;
            lock (gate) if (route.Count > 20000) route.Clear();   // старые соединения — не копим
        }

        // Kernel-Network: 10/11 — TCP IPv4 отправка/приём, 42/43 — UDP IPv4, 26/27 — TCP IPv6, 58/59 — UDP IPv6.
        // Данные: PID (4), размер (4), daddr, saddr (по 4 или 16 байт), порты…
        static void OnEvent(IntPtr rec)
        {
            ushort id = (ushort)Marshal.ReadInt16(rec, 40);
            bool send, v6;
            switch (id)
            {
                case 10: send = true; v6 = false; break;
                case 11: send = false; v6 = false; break;
                case 42: send = true; v6 = false; break;
                case 43: send = false; v6 = false; break;
                case 26: send = true; v6 = true; break;
                case 27: send = false; v6 = true; break;
                case 58: send = true; v6 = true; break;
                case 59: send = false; v6 = true; break;
                default: return;
            }
            IntPtr ud = Marshal.ReadIntPtr(rec, 96);
            int len = (ushort)Marshal.ReadInt16(rec, 86);
            if (ud == IntPtr.Zero || len < (v6 ? 40 : 16)) return;
            uint pid = (uint)Marshal.ReadInt32(ud, 0);
            long size = (uint)Marshal.ReadInt32(ud, 4);
            string local, remote;
            if (!v6)
            {
                uint d = (uint)Marshal.ReadInt32(ud, 8), s = (uint)Marshal.ReadInt32(ud, 12);
                ushort dp = (ushort)Marshal.ReadInt16(ud, 16), sp = (ushort)Marshal.ReadInt16(ud, 18);
                bool sLocal = local4.Contains(s) || !local4.Contains(d);
                local = new IPAddress(BitConverter.GetBytes(sLocal ? s : d)).ToString();
                remote = new IPAddress(BitConverter.GetBytes(sLocal ? d : s)).ToString() + ":" + (sLocal ? dp : sp);
            }
            else
            {
                var d = new byte[16]; var s = new byte[16];
                Marshal.Copy(ud + 8, d, 0, 16); Marshal.Copy(ud + 24, s, 0, 16);
                ushort dp = (ushort)Marshal.ReadInt16(ud, 40 > len - 4 ? 8 : 40), sp = (ushort)Marshal.ReadInt16(ud, 42 > len - 2 ? 8 : 42);
                string ds = new IPAddress(d).ToString(), ss = new IPAddress(s).ToString();
                bool sLocal = local6.Contains(ss) || !local6.Contains(ds);
                local = sLocal ? ss : ds;
                remote = "[" + (sLocal ? ds : ss) + "]:" + (sLocal ? dp : sp);
            }
            Interlocked.Increment(ref events);
            lock (gate)
            {
                Acc a = Of(pid);
                long[] b;
                if (!a.ByLocal.TryGetValue(local, out b)) { b = new long[2]; a.ByLocal[local] = b; }
                if (send) { a.Out += size; b[1] += size; } else { a.In += size; b[0] += size; }
                if (a.Relay) { if (!tunAddrs.Contains(local)) route[remote] = local; }        // net-merge говорит с сервером через это подключение
                else if (tunAddrs.Contains(local))
                {
                    string via;
                    if (route.TryGetValue(remote, out via))
                    {
                        if (!a.ByVia.TryGetValue(via, out b)) { b = new long[2]; a.ByVia[via] = b; }
                        if (send) b[1] += size; else b[0] += size;
                    }
                }
            }
        }

        // программа по номеру процесса: итоги копим по имени (у программы может быть много процессов)
        static Acc Of(uint pid)
        {
            Acc a;
            if (byPid.TryGetValue(pid, out a)) return a;
            string name = pid == 0 || pid == 4 ? "System" : "", path = "";
            if (name.Length == 0)
            {
                IntPtr h = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
                if (h != IntPtr.Zero)
                {
                    var sb = new StringBuilder(1024); int n = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref n)) path = sb.ToString();
                    CloseHandle(h);
                }
                name = path.Length > 0 ? System.IO.Path.GetFileNameWithoutExtension(path) : "pid " + pid;
            }
            if (!byName.TryGetValue(name, out a)) { a = new Acc { Name = name, Path = path, Relay = name.StartsWith("net-merge", StringComparison.OrdinalIgnoreCase) }; byName[name] = a; }
            else if (a.Path.Length == 0) a.Path = path;
            if (byPid.Count > 4096) byPid.Clear();   // номера процессов переиспользуются — не копим навсегда
            byPid[pid] = a;
            return a;
        }

        // net-apps.json: накопленные с запуска помощника байты по программам и локальным адресам
        static void Write()
        {
            var sb = new StringBuilder();
            sb.Append("{\"t\":").Append((long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds)
              .Append(",\"pid\":").Append(Process.GetCurrentProcess().Id).Append(",\"events\":").Append(Interlocked.Read(ref events)).Append(",\"apps\":[");
            lock (gate)
            {
                bool first = true;
                foreach (var a in byName.Values)
                {
                    if (a.In + a.Out == 0) continue;
                    if (!first) sb.Append(','); first = false;
                    sb.Append("{\"n\":").Append(Js(a.Name)).Append(",\"p\":").Append(Js(a.Path)).Append(",\"i\":").Append(a.In).Append(",\"o\":").Append(a.Out).Append(",\"l\":{");
                    bool f2 = true;
                    foreach (var kv in a.ByLocal)
                    {
                        if (!f2) sb.Append(','); f2 = false;
                        sb.Append(Js(kv.Key)).Append(":[").Append(kv.Value[0]).Append(',').Append(kv.Value[1]).Append(']');
                    }
                    sb.Append("},\"v\":{");
                    f2 = true;
                    foreach (var kv in a.ByVia)
                    {
                        if (!f2) sb.Append(','); f2 = false;
                        sb.Append(Js(kv.Key)).Append(":[").Append(kv.Value[0]).Append(',').Append(kv.Value[1]).Append(']');
                    }
                    sb.Append("}}");
                }
            }
            sb.Append("]}");
            try
            {
                string tmp = OutFile + ".tmp";
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(OutFile)) File.Replace(tmp, OutFile, null); else File.Move(tmp, OutFile);
            }
            catch (Exception) { }
        }

        static string Js(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s ?? "")
            {
                if (c == '"' || c == '\\') sb.Append('\\').Append(c);
                else if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        // ── ETW ──
        static IntPtr Props()
        {
            int size = Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES)) + 2048;
            IntPtr p = Marshal.AllocHGlobal(size);
            for (int i = 0; i < size; i++) Marshal.WriteByte(p, i, 0);
            var pr = new EVENT_TRACE_PROPERTIES();
            pr.Wnode.BufferSize = (uint)size;
            pr.Wnode.Flags = 0x00020000;            // WNODE_FLAG_TRACED_GUID
            pr.Wnode.ClientContext = 1;             // время — QPC
            pr.BufferSize = 64;                     // КБ
            pr.LogFileMode = 0x00000100;            // EVENT_TRACE_REAL_TIME_MODE
            pr.FlushTimer = 1;                      // сбрасывать раз в секунду
            pr.LoggerNameOffset = (uint)Marshal.SizeOf(typeof(EVENT_TRACE_PROPERTIES));
            Marshal.StructureToPtr(pr, p, false);
            return p;
        }

        static void StopSession()
        {
            IntPtr props = Props();
            ControlTrace(0, Session, props, 1 /*STOP*/);
            Marshal.FreeHGlobal(props);
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WNODE_HEADER { public uint BufferSize, ProviderId; public ulong HistoricalContext; public long TimeStamp; public Guid Guid; public uint ClientContext, Flags; }

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_TRACE_PROPERTIES
        {
            public WNODE_HEADER Wnode;
            public uint BufferSize, MinimumBuffers, MaximumBuffers, MaximumFileSize, LogFileMode, FlushTimer, EnableFlags;
            public int AgeLimit;
            public uint NumberOfBuffers, FreeBuffers, EventsLost, BuffersWritten, LogBuffersLost, RealTimeBuffersLost;
            public IntPtr LoggerThreadId;
            public uint LogFileNameOffset, LoggerNameOffset;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_TRACE_HEADER { public ushort Size, FieldTypeFlags; public uint Version, ThreadId, ProcessId; public long TimeStamp; public Guid Guid; public uint KernelTime, UserTime; }

        [StructLayout(LayoutKind.Sequential)]
        struct EVENT_TRACE { public EVENT_TRACE_HEADER Header; public uint InstanceId, ParentInstanceId; public Guid ParentGuid; public IntPtr MofData; public uint MofLength, ClientContext; }

        [StructLayout(LayoutKind.Sequential)]
        struct SYSTEMTIME { public ushort Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TIME_ZONE_INFORMATION
        {
            public int Bias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
            public SYSTEMTIME StandardDate; public int StandardBias;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
            public SYSTEMTIME DaylightDate; public int DaylightBias;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TRACE_LOGFILE_HEADER
        {
            public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
            public long EndTime;
            public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
            public Guid LogInstanceGuid;
            public IntPtr LoggerName, LogFileName;
            public TIME_ZONE_INFORMATION TimeZone;
            public long BootTime, PerfFreq, StartTime;
            public uint ReservedFlags, BuffersLost;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        delegate void EventRecordCallback(IntPtr eventRecord);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct EVENT_TRACE_LOGFILE
        {
            [MarshalAs(UnmanagedType.LPWStr)] public string LogFileName;
            [MarshalAs(UnmanagedType.LPWStr)] public string LoggerName;
            public long CurrentTime;
            public uint BuffersRead;
            public uint ProcessTraceMode;
            public EVENT_TRACE CurrentEvent;
            public TRACE_LOGFILE_HEADER LogfileHeader;
            public IntPtr BufferCallback;
            public uint BufferSize, Filled, EventsLost;
            public EventRecordCallback EventRecordCallback;
            public uint IsKernelTrace;
            public IntPtr Context;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint StartTrace(out long handle, string name, IntPtr props);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] static extern uint ControlTrace(long handle, string name, IntPtr props, uint code);
        [DllImport("advapi32.dll")] static extern uint EnableTraceEx2(long handle, ref Guid provider, uint code, byte level, ulong any, ulong all, uint timeout, IntPtr parameters);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern long OpenTrace(ref EVENT_TRACE_LOGFILE logfile);
        [DllImport("advapi32.dll")] static extern uint ProcessTrace(long[] handles, uint count, IntPtr start, IntPtr end);
        [DllImport("advapi32.dll")] static extern uint CloseTrace(long handle);
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    }
}
