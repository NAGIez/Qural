using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Qural
{
    // Громкость программ в микшере Windows (Core Audio).
    // Сессии ищутся заново при каждом нажатии — поэтому всё работает после перезапуска ПК,
    // перезапуска программы или смены устройства вывода, ничего не надо «перезапускать из диспетчера задач».
    // Основной регулятор — ISimpleAudioVolume (ползунок микшера). Если Windows его не отдаёт
    // (так бывает на новых сборках), меняем громкость каналов программы — IChannelAudioVolume.
    static class Audio
    {
        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int Item(int index, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionManager2
        {
            [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, int flags, out IntPtr control);
            [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, int flags, out IntPtr volume);
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
        }

        [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioSessionControl2
        {
            [PreserveSig] int GetState(out int state);
            [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string name);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid ctx);
            [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string path);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid ctx);
            [PreserveSig] int GetGroupingParam(out Guid param);
            [PreserveSig] int SetGroupingParam(ref Guid param, ref Guid ctx);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
            [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetProcessId(out uint pid);
            [PreserveSig] int IsSystemSoundsSession();
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6F0D7D0A2E4C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float level, ref Guid ctx);
            [PreserveSig] int GetMasterVolume(out float level);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid ctx);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        }

        [ComImport, Guid("1C158861-B533-4B30-B1CF-E853E51C59B8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IChannelAudioVolume
        {
            [PreserveSig] int GetChannelCount(out uint count);
            [PreserveSig] int SetChannelVolume(uint index, float level, ref Guid ctx);
            [PreserveSig] int GetChannelVolume(uint index, out float level);
        }

        const int eRender = 0, DEVICE_STATE_ACTIVE = 1, CLSCTX_ALL = 23;
        static Guid ctx = Guid.Empty;

        // true — последний раз пришлось менять громкость каналов (основной регулятор недоступен)
        public static bool UsedChannelFallback;

        internal class Session
        {
            public string Process;   // имя exe без .exe, например "Discord"
            public string Path;      // полный путь к exe (может быть пустым)
            public float Volume;     // 0..1
            public bool Muted;
            internal object Com;     // RCW сессии
            internal ISimpleAudioVolume Simple;
            internal IChannelAudioVolume Channels;

            public void SetVolume(float v)
            {
                if (Simple != null) { Simple.SetMasterVolume(v, ref ctx); return; }
                uint n; Channels.GetChannelCount(out n);
                for (uint i = 0; i < n; i++) Channels.SetChannelVolume(i, v, ref ctx);
            }

            public void SetMute(bool m)
            {
                if (Simple != null) Simple.SetMute(m, ref ctx);
            }
        }

        public class Info
        {
            public string Process, Path;
            public float Volume;
            public bool Muted;
        }

        // Снимок: какие программы сейчас в микшере (по одной строке на программу).
        public static List<Info> Snapshot()
        {
            var all = GetSessions();
            var res = new List<Info>();
            try
            {
                foreach (var s in all)
                {
                    var e = res.Find(x => string.Equals(x.Process, s.Process, StringComparison.OrdinalIgnoreCase));
                    if (e == null) res.Add(new Info { Process = s.Process, Path = s.Path, Volume = s.Volume, Muted = s.Muted });
                    else { e.Volume = Math.Max(e.Volume, s.Volume); e.Muted = e.Muted && s.Muted; if (e.Path.Length == 0) e.Path = s.Path; }
                }
            }
            finally { Release(all); }
            return res;
        }

        // Изменить громкость на шаг вверх/вниз. Возвращает новую громкость или −1, если программа не в микшере.
        // memo — громкость до «без звука» (для запасного способа, где своего mute нет).
        public static float Change(string[] process, float step, int dir, Dictionary<string, float> memo)
        {
            var all = GetSessions();
            try
            {
                var mine = all.FindAll(s => Is(s, process));
                if (mine.Count == 0) return -1;
                float cur = 0;
                foreach (var s in mine) cur = Math.Max(cur, s.Volume);
                // ровная сетка: 47% и шаг 5 → 50%, а не 52%
                double k = cur / step;
                float next = (float)((dir > 0 ? Math.Floor(k + 1e-3) + 1 : Math.Ceiling(k - 1e-3) - 1) * step);
                next = Math.Max(0f, Math.Min(1f, next));
                // «громче» после «без звука» (запасной способ) — вернуть прежнюю громкость, как делает Windows
                float back;
                if (dir > 0 && cur <= 0.001f && memo.TryGetValue(Key(process), out back) && back > 0) next = back;
                foreach (var s in mine)
                {
                    s.SetVolume(next);
                    if (dir > 0 && s.Muted) s.SetMute(false); // «громче» снимает «без звука»
                }
                if (dir > 0) memo.Remove(Key(process));
                return next;
            }
            finally { Release(all); }
        }

        public static float Set(string[] process, float value)
        {
            var all = GetSessions();
            try
            {
                var mine = all.FindAll(s => Is(s, process));
                value = Math.Max(0f, Math.Min(1f, value));
                foreach (var s in mine)
                {
                    s.SetVolume(value);
                    // поставили слышимую громкость — значит, «без звука» больше не нужен (как «громче» в Change)
                    if (value > 0.001f && s.Muted) s.SetMute(false);
                }
                return mine.Count == 0 ? -1 : value;
            }
            finally { Release(all); }
        }

        // Переключить «без звука». Возвращает true — звук выключен, false — включён, null — программы нет в микшере.
        public static bool? ToggleMute(string[] process, Dictionary<string, float> memo)
        {
            var all = GetSessions();
            try
            {
                var mine = all.FindAll(s => Is(s, process));
                if (mine.Count == 0) return null;
                if (mine.TrueForAll(s => s.Simple != null))
                {
                    bool mute = mine.Exists(s => !s.Muted);
                    foreach (var s in mine) s.SetMute(mute);
                    return mute;
                }
                // запасной способ: «без звука» = громкость 0, прежнюю запоминаем
                float cur = 0;
                foreach (var s in mine) cur = Math.Max(cur, s.Volume);
                string key = Key(process);
                if (cur > 0.001f)
                {
                    memo[key] = cur;
                    foreach (var s in mine) s.SetVolume(0);
                    return true;
                }
                float back; if (!memo.TryGetValue(key, out back) || back <= 0) back = 1f;
                foreach (var s in mine) s.SetVolume(back);
                memo.Remove(key);
                return false;
            }
            finally { Release(all); }
        }

        // несколько имён exe у одной программы: память «без звука» — по первому (основному) имени
        static string Key(string[] process) { return process.Length == 0 ? "" : process[0].ToLowerInvariant(); }
        static bool Is(Session s, string[] process) { return Array.Exists(process, p => string.Equals(s.Process, p, StringComparison.OrdinalIgnoreCase)); }

        // Все сессии всех активных устройств вывода.
        static List<Session> GetSessions()
        {
            var list = new List<Session>();
            IMMDeviceEnumerator en = null; IMMDeviceCollection col = null;
            bool fallback = false;
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                if (en.EnumAudioEndpoints(eRender, DEVICE_STATE_ACTIVE, out col) != 0) return list;
                int n; col.GetCount(out n);
                for (int d = 0; d < n; d++)
                {
                    IMMDevice dev = null; object mgrObj = null; IAudioSessionEnumerator sen = null;
                    try
                    {
                        if (col.Item(d, out dev) != 0) continue;
                        Guid iid = typeof(IAudioSessionManager2).GUID;
                        if (dev.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out mgrObj) != 0) continue;
                        if (((IAudioSessionManager2)mgrObj).GetSessionEnumerator(out sen) != 0) continue;
                        int c; sen.GetCount(out c);
                        for (int i = 0; i < c; i++)
                        {
                            IAudioSessionControl2 s = null;
                            try
                            {
                                if (sen.GetSession(i, out s) != 0 || s == null) continue;
                                uint pid; s.GetProcessId(out pid);
                                string name, path;
                                string inst; s.GetSessionInstanceIdentifier(out inst);
                                if (pid == 0 || s.IsSystemSoundsSession() == 0 || !ProcessInfo(pid, inst ?? "", out name, out path))
                                {
                                    Marshal.ReleaseComObject(s); s = null; continue;
                                }
                                var ses = new Session { Process = name, Path = path, Com = s };
                                ses.Simple = s as ISimpleAudioVolume;
                                if (ses.Simple != null)
                                {
                                    float v; bool m;
                                    ses.Simple.GetMasterVolume(out v); ses.Simple.GetMute(out m);
                                    ses.Volume = v; ses.Muted = m;
                                }
                                else
                                {
                                    ses.Channels = s as IChannelAudioVolume;
                                    if (ses.Channels == null) { Marshal.ReleaseComObject(s); s = null; continue; }
                                    fallback = true;
                                    uint cn; ses.Channels.GetChannelCount(out cn);
                                    float max = 0;
                                    for (uint ci = 0; ci < cn; ci++) { float cv; ses.Channels.GetChannelVolume(ci, out cv); max = Math.Max(max, cv); }
                                    ses.Volume = max; ses.Muted = max <= 0.001f;
                                }
                                list.Add(ses);
                            }
                            catch (Exception)
                            {
                                if (s != null) try { Marshal.ReleaseComObject(s); } catch (Exception) { }
                            }
                        }
                    }
                    catch (Exception) { }
                    finally
                    {
                        if (sen != null) Marshal.ReleaseComObject(sen);
                        if (mgrObj != null) Marshal.ReleaseComObject(mgrObj);
                        if (dev != null) Marshal.ReleaseComObject(dev);
                    }
                }
            }
            catch (Exception) { }
            finally
            {
                if (col != null) Marshal.ReleaseComObject(col);
                if (en != null) Marshal.ReleaseComObject(en);
            }
            if (list.Count > 0) UsedChannelFallback = fallback;
            return list;
        }

        static void Release(List<Session> list)
        {
            foreach (var s in list)
                if (s.Com != null) { try { Marshal.ReleaseComObject(s.Com); } catch (Exception) { } s.Com = null; s.Simple = null; s.Channels = null; }
        }

        [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder name, ref int size);

        // кэш по идентификатору экземпляра сессии: он уникален для процесса, PID отдельно не проверяем
        static readonly Dictionary<string, string[]> pidCache = new Dictionary<string, string[]>();

        static bool ProcessInfo(uint pid, string inst, out string name, out string path)
        {
            name = null; path = "";
            string[] c;
            string ck = pid + "|" + inst;
            if (pidCache.TryGetValue(ck, out c)) { name = c[0]; path = c[1]; return true; }
            IntPtr h = OpenProcess(0x1000 /*QUERY_LIMITED_INFORMATION*/, false, pid);
            if (h != IntPtr.Zero)
            {
                try
                {
                    var sb = new StringBuilder(1024); int size = sb.Capacity;
                    if (QueryFullProcessImageName(h, 0, sb, ref size)) path = sb.ToString();
                }
                finally { CloseHandle(h); }
            }
            if (path.Length > 0) name = System.IO.Path.GetFileNameWithoutExtension(path);
            else
            {
                try { using (var p = System.Diagnostics.Process.GetProcessById((int)pid)) name = p.ProcessName; }
                catch (Exception) { return false; }
            }
            if (pidCache.Count > 256) pidCache.Clear();
            pidCache[ck] = new[] { name, path };
            return true;
        }
    }
}
