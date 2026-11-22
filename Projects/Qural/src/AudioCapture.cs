using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace Qural
{
    // Захват звука через WASAPI: звук ПК (loopback устройства вывода) или микрофон.
    // Всегда отдаёт 48 кГц, 2 канала, float32 — Windows сама переводит формат устройства (AUTOCONVERTPCM).
    // Звук ПК в тишине Windows не присылает — дописываем нули по часам, чтобы дорожка не «съезжала» относительно видео.
    sealed class AudioCapture : IDisposable
    {
        public const int Rate = 48000, Channels = 2, BytesPerFrame = 8;

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCo { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IMMDeviceCollection devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
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
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
            [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
            [PreserveSig] int GetState(out int state);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int count);
            [PreserveSig] int GetAt(int index, out PROPERTYKEY key);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        }

        [StructLayout(LayoutKind.Sequential)] struct PROPERTYKEY { public Guid fmtid; public int pid; }
        [StructLayout(LayoutKind.Explicit, Size = 24)] struct PROPVARIANT { [FieldOffset(0)] public short vt; [FieldOffset(8)] public IntPtr p; }
        [DllImport("ole32.dll")] static extern int PropVariantClear(ref PROPVARIANT pv);

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out uint frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out uint frames);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defPeriod, out long minPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object obj);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
            [PreserveSig] int ReleaseBuffer(uint frames);
            [PreserveSig] int GetNextPacketSize(out uint frames);
        }

        const int eRender = 0, eCapture = 1, eConsole = 0, DEVICE_STATE_ACTIVE = 1;
        const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000, AUTOCONVERTPCM = unchecked((int)0x80000000), SRC_DEFAULT_QUALITY = 0x08000000;
        const uint AUDCLNT_BUFFERFLAGS_SILENT = 2;

        public class Device { public string Id, Name; public bool Default; }

        // список микрофонов (или устройств вывода) с понятными названиями
        public static List<Device> List(bool capture)
        {
            var list = new List<Device>();
            try
            {
                var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                string defId = null;
                IMMDevice def;
                if (en.GetDefaultAudioEndpoint(capture ? eCapture : eRender, eConsole, out def) == 0 && def != null) { def.GetId(out defId); Marshal.ReleaseComObject(def); }
                IMMDeviceCollection col;
                if (en.EnumAudioEndpoints(capture ? eCapture : eRender, DEVICE_STATE_ACTIVE, out col) != 0) return list;
                int n; col.GetCount(out n);
                for (int i = 0; i < n; i++)
                {
                    IMMDevice d;
                    if (col.Item(i, out d) != 0 || d == null) continue;
                    string id; d.GetId(out id);
                    list.Add(new Device { Id = id, Name = FriendlyName(d) ?? id, Default = id == defId });
                    Marshal.ReleaseComObject(d);
                }
                Marshal.ReleaseComObject(col);
                Marshal.ReleaseComObject(en);
            }
            catch (Exception) { }
            return list;
        }

        static string FriendlyName(IMMDevice d)
        {
            IPropertyStore ps;
            if (d.OpenPropertyStore(0, out ps) != 0 || ps == null) return null;
            try
            {
                var key = new PROPERTYKEY { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };
                PROPVARIANT v;
                if (ps.GetValue(ref key, out v) != 0) return null;
                string s = v.vt == 31 /*VT_LPWSTR*/ ? Marshal.PtrToStringUni(v.p) : null;
                PropVariantClear(ref v);
                return s;
            }
            finally { Marshal.ReleaseComObject(ps); }
        }

        readonly bool loopback;
        readonly string deviceId;
        Thread thread;
        volatile bool running;
        public volatile float Peak;      // уровень для индикатора, 0–1
        public float TakePeak() { float p = Peak; Peak = 0; return p; }
        public volatile bool Muted;      // выключили во время записи — пишем тишину, дорожка не прерывается
        public string Error;             // не удалось открыть устройство
        public string DeviceName;

        public AudioCapture(bool loopback, string deviceId)
        {
            this.loopback = loopback;
            this.deviceId = string.IsNullOrEmpty(deviceId) ? null : deviceId;
        }

        // Пишет звук в output, пока не вызовут Stop(). Поток свой.
        public void Start(Stream output)
        {
            running = true;
            thread = new Thread(() => Run(output)) { IsBackground = true, Name = loopback ? "Qural audio PC" : "Qural audio mic", Priority = ThreadPriority.AboveNormal };
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();
        }

        public void Stop()
        {
            running = false;
            if (thread != null && !thread.Join(2000)) { try { thread.Abort(); } catch (Exception) { } }
            thread = null;
        }

        public void Dispose() { Stop(); }

        void Run(Stream output)
        {
            IMMDeviceEnumerator en = null; IMMDevice dev = null; IAudioClient client = null; IAudioCaptureClient cap = null;
            IntPtr fmt = IntPtr.Zero;
            var silence = new byte[Rate / 10 * BytesPerFrame]; // 100 мс нулей
            byte[] buf = new byte[Rate * BytesPerFrame];
            try
            {
                en = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
                int hr = deviceId != null ? en.GetDevice(deviceId, out dev) : en.GetDefaultAudioEndpoint(loopback ? eRender : eCapture, eConsole, out dev);
                if (hr != 0 || dev == null)
                {
                    // выбранного микрофона больше нет — берём основной
                    hr = en.GetDefaultAudioEndpoint(loopback ? eRender : eCapture, eConsole, out dev);
                    if (hr != 0 || dev == null) { Error = loopback ? "нет устройства вывода звука" : "нет микрофона"; WriteSilenceUntilStop(output, silence); return; }
                }
                DeviceName = FriendlyName(dev);
                var iid = typeof(IAudioClient).GUID;
                object o;
                Check(dev.Activate(ref iid, 1 /*CLSCTX_INPROC_SERVER*/, IntPtr.Zero, out o));
                client = (IAudioClient)o;

                // WAVEFORMATEX: float32, 48 кГц, стерео
                fmt = Marshal.AllocCoTaskMem(18);
                Marshal.WriteInt16(fmt, 0, 3);                       // WAVE_FORMAT_IEEE_FLOAT
                Marshal.WriteInt16(fmt, 2, Channels);
                Marshal.WriteInt32(fmt, 4, Rate);
                Marshal.WriteInt32(fmt, 8, Rate * BytesPerFrame);
                Marshal.WriteInt16(fmt, 12, BytesPerFrame);
                Marshal.WriteInt16(fmt, 14, 32);
                Marshal.WriteInt16(fmt, 16, 0);
                int flags = AUTOCONVERTPCM | SRC_DEFAULT_QUALITY | (loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0);
                Check(client.Initialize(0 /*SHARED*/, flags, 2000000 /*200 мс*/, 0, fmt, IntPtr.Zero));
                var ciid = typeof(IAudioCaptureClient).GUID;
                Check(client.GetService(ref ciid, out o));
                cap = (IAudioCaptureClient)o;
                Check(client.Start());

                var clock = System.Diagnostics.Stopwatch.StartNew();
                long written = 0;
                while (running)
                {
                    bool got = false;
                    uint packet;
                    while (running && cap.GetNextPacketSize(out packet) == 0 && packet > 0)
                    {
                        IntPtr data; uint frames, bflags; ulong pos, qpc;
                        if (cap.GetBuffer(out data, out frames, out bflags, out pos, out qpc) != 0) break;
                        int bytes = (int)frames * BytesPerFrame;
                        if (buf.Length < bytes) buf = new byte[bytes];
                        if ((bflags & AUDCLNT_BUFFERFLAGS_SILENT) != 0 || Muted) Array.Clear(buf, 0, bytes);
                        else Marshal.Copy(data, buf, 0, bytes);
                        cap.ReleaseBuffer(frames);
                        Meter(buf, bytes);
                        output.Write(buf, 0, bytes);
                        written += frames;
                        got = true;
                    }
                    // тишина на звуке ПК: Windows молчит — дописываем нули по часам (с запасом 20 мс)
                    long expected = (long)(clock.Elapsed.TotalSeconds * Rate);
                    if (!got && expected - written > Rate / 50)
                    {
                        long gap = expected - written - Rate / 100;
                        while (gap > 0)
                        {
                            int n = (int)Math.Min(gap, silence.Length / BytesPerFrame);
                            output.Write(silence, 0, n * BytesPerFrame);
                            written += n; gap -= n;
                        }
                        Peak = 0;
                    }
                    Thread.Sleep(8);
                }
                client.Stop();
            }
            catch (IOException) { }                        // FFmpeg закрыл канал — запись закончилась
            catch (ThreadAbortException) { }
            catch (Exception ex) { Error = ex.Message; try { WriteSilenceUntilStop(output, silence); } catch (Exception) { } }
            finally
            {
                if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt);
                if (cap != null) Marshal.ReleaseComObject(cap);
                if (client != null) Marshal.ReleaseComObject(client);
                if (dev != null) Marshal.ReleaseComObject(dev);
                if (en != null) Marshal.ReleaseComObject(en);
                try { output.Flush(); } catch (Exception) { }
            }
        }

        // устройство не открылось — дорожка всё равно идёт (тишиной), чтобы запись не сломалась
        void WriteSilenceUntilStop(Stream output, byte[] silence)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long written = 0;
            while (running)
            {
                long expected = (long)(clock.Elapsed.TotalSeconds * Rate);
                while (expected - written > 0)
                {
                    int n = (int)Math.Min(expected - written, silence.Length / BytesPerFrame);
                    output.Write(silence, 0, n * BytesPerFrame);
                    written += n;
                }
                Thread.Sleep(20);
            }
        }

        void Meter(byte[] b, int bytes)
        {
            float peak = 0;
            for (int i = 0; i + 3 < bytes; i += 16) // каждый второй кадр — хватит для индикатора
            {
                float v = Math.Abs(BitConverter.ToSingle(b, i));
                if (v > peak) peak = v;
            }
            // самый громкий пакет с прошлого взгляда индикатора (TakePeak) — короткие пики не теряются
            float cur = Peak; if (peak > cur) Peak = Math.Min(1f, peak);
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    }
}
