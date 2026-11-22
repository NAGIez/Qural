using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Qural
{
    // ───────────── настройки записи (rec.json) ─────────────
    [DataContract]
    class RecConfig
    {
        [DataMember] public int Hotkey, PauseHotkey;
        [DataMember] public string Folder;        // пусто — «Видео\Qural»
        [DataMember] public string Codec;         // auto или имя кодировщика FFmpeg
        [DataMember] public string Quality;       // high | medium | low | bitrate
        [DataMember] public int Bitrate;          // Мбит/с для quality=bitrate
        [DataMember] public int Fps;
        [DataMember] public string Scale;         // source | 1440 | 1080 | 720
        [DataMember] public bool Cursor, Countdown, SysAudio, Mic, SeparateTracks;
        [DataMember] public int SysVolume, MicVolume;
        [DataMember] public string MicDevice;     // пусто — основной микрофон
        [DataMember] public string Noise;         // rnn | fft | off — для микрофона
        [DataMember] public string Container;     // mp4 | mkv
        [DataMember] public string AfterStop;     // editor | save

        public RecConfig() { Defaults(); }
        [OnDeserializing] void OnLoad(StreamingContext c) { Defaults(); }

        void Defaults()
        {
            Hotkey = (int)(Keys.Control | Keys.Alt | Keys.R);
            PauseHotkey = (int)(Keys.Control | Keys.Alt | Keys.P);
            Folder = ""; Codec = "auto"; Quality = "high"; Bitrate = 12; Fps = 30; Scale = "source";
            Cursor = true; Countdown = true; SysAudio = true; Mic = true; SeparateTracks = false;
            SysVolume = 100; MicVolume = 100; MicDevice = ""; Noise = "rnn"; Container = "mp4"; AfterStop = "editor";
        }

        public string FolderPath
        {
            get
            {
                return string.IsNullOrEmpty(Folder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Qural")
                    : Folder;
            }
        }

        public static RecConfig Load()
        {
            var c = Store.Load<RecConfig>("rec.json", null) ?? new RecConfig();
            if (c.Fps != 15 && c.Fps != 24 && c.Fps != 30 && c.Fps != 60 && c.Fps != 120) c.Fps = 30;
            if (c.Quality != "medium" && c.Quality != "low" && c.Quality != "bitrate") c.Quality = "high";
            if (c.Bitrate < 1 || c.Bitrate > 200) c.Bitrate = 12;
            if (c.Scale != "1440" && c.Scale != "1080" && c.Scale != "720") c.Scale = "source";
            if (c.Noise != "fft" && c.Noise != "off") c.Noise = "rnn";
            if (c.Container != "mkv") c.Container = "mp4";
            if (c.AfterStop != "save") c.AfterStop = "editor";
            if (c.Codec == null) c.Codec = "auto";
            if (c.Folder == null) c.Folder = "";
            if (c.MicDevice == null) c.MicDevice = "";
            c.SysVolume = Math.Max(0, Math.Min(200, c.SysVolume)); c.MicVolume = Math.Max(0, Math.Min(200, c.MicVolume));
            return c;
        }

        public void Save() { Store.Save("rec.json", this); }
        public void SaveQuiet() { Store.Save("rec.json", this, false); }
    }

    // ───────────── FFmpeg: где лежит, какие кодировщики работают на этом ПК ─────────────
    static class Ff
    {
        public static string Dir { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ffmpeg"); } }
        public static string Exe { get { return Path.Combine(Dir, "ffmpeg.exe"); } }
        public static string Probe { get { return Path.Combine(Dir, "ffprobe.exe"); } }
        public static bool Available { get { return File.Exists(Exe); } }

        public class Enc { public string Id, Label, Codec, Family; public bool Gpu; }

        // по порядку предпочтения; «auto» берёт первый работающий H.264 — его откроет любой плеер и мессенджер
        public static readonly Enc[] All = {
            new Enc { Id = "h264_nvenc", Label = "H.264 — видеокарта NVIDIA", Codec = "h264", Family = "nvenc", Gpu = true },
            new Enc { Id = "hevc_nvenc", Label = "HEVC (H.265) — видеокарта NVIDIA", Codec = "hevc", Family = "nvenc", Gpu = true },
            new Enc { Id = "av1_nvenc", Label = "AV1 — видеокарта NVIDIA", Codec = "av1", Family = "nvenc", Gpu = true },
            new Enc { Id = "h264_amf", Label = "H.264 — видеокарта AMD", Codec = "h264", Family = "amf", Gpu = true },
            new Enc { Id = "hevc_amf", Label = "HEVC (H.265) — видеокарта AMD", Codec = "hevc", Family = "amf", Gpu = true },
            new Enc { Id = "av1_amf", Label = "AV1 — видеокарта AMD", Codec = "av1", Family = "amf", Gpu = true },
            new Enc { Id = "h264_qsv", Label = "H.264 — графика Intel", Codec = "h264", Family = "qsv", Gpu = true },
            new Enc { Id = "hevc_qsv", Label = "HEVC (H.265) — графика Intel", Codec = "hevc", Family = "qsv", Gpu = true },
            new Enc { Id = "av1_qsv", Label = "AV1 — графика Intel", Codec = "av1", Family = "qsv", Gpu = true },
            new Enc { Id = "libx264", Label = "H.264 — процессор", Codec = "h264", Family = "x264", Gpu = false },
            new Enc { Id = "libx265", Label = "HEVC (H.265) — процессор", Codec = "hevc", Family = "x265", Gpu = false },
        };

        static List<string> working;
        static bool probing;
        public static event Action Probed;

        public static List<string> Working { get { return working; } }

        // Проверяем один раз за запуск, в фоне: кодировщик есть в FFmpeg, но может не работать (нет такой видеокарты)
        public static void ProbeAsync()
        {
            if (working != null || probing || !Available) return;
            probing = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                var ok = new List<string>();
                foreach (var e in All)
                {
                    try
                    {
                        string err;
                        if (Run("-hide_banner -loglevel error -f lavfi -i color=c=black:s=640x360:r=30 -frames:v 5 -c:v " + e.Id + " -f null -", 15000, out err) == 0) ok.Add(e.Id);
                    }
                    catch (Exception) { }
                }
                working = ok; probing = false;
                var h = Probed; if (h != null) h();
            });
        }

        public static Enc Find(string id) { foreach (var e in All) if (e.Id == id) return e; return null; }

        // выбранный в настройках кодировщик, если он работает; иначе первый работающий H.264
        public static Enc Pick(string id)
        {
            var w = working;
            if (id != "auto" && id != null && (w == null || w.Contains(id))) { var e = Find(id); if (e != null) return e; }
            if (w != null) foreach (var e in All) if (e.Codec == "h264" && w.Contains(e.Id)) return e;
            return Find(w == null ? "h264_nvenc" : "libx264");
        }

        // параметры качества для кодировщика
        public static string VideoArgs(Enc e, string quality, int mbps, int fps)
        {
            int q;
            var sb = new StringBuilder();
            sb.Append("-c:v ").Append(e.Id).Append(' ');
            bool br = quality == "bitrate";
            switch (e.Family)
            {
                case "nvenc":
                    q = quality == "low" ? 30 : quality == "medium" ? 25 : 20;
                    sb.Append("-preset p5 -tune hq ");
                    if (br) sb.AppendFormat("-rc vbr -b:v {0}M -maxrate {1}M -bufsize {2}M ", mbps, mbps * 3 / 2, mbps * 2);
                    else sb.AppendFormat("-rc vbr -cq {0} -b:v 0 ", q);
                    break;
                case "amf":
                    q = quality == "low" ? 30 : quality == "medium" ? 25 : 20;
                    sb.Append("-quality balanced ");
                    if (br) sb.AppendFormat("-rc vbr_peak -b:v {0}M -maxrate {1}M ", mbps, mbps * 3 / 2);
                    else sb.AppendFormat("-rc cqp -qp_i {0} -qp_p {0} ", q);
                    break;
                case "qsv":
                    q = quality == "low" ? 30 : quality == "medium" ? 25 : 21;
                    if (br) sb.AppendFormat("-b:v {0}M -maxrate {1}M ", mbps, mbps * 3 / 2);
                    else sb.AppendFormat("-global_quality {0} ", q);
                    break;
                default: // x264 / x265
                    q = quality == "low" ? 30 : quality == "medium" ? 25 : 21;
                    if (e.Family == "x265") q += 2;
                    sb.Append("-preset veryfast ");
                    if (br) sb.AppendFormat("-b:v {0}M -maxrate {1}M -bufsize {2}M ", mbps, mbps * 3 / 2, mbps * 2);
                    else sb.AppendFormat("-crf {0} ", q);
                    sb.Append("-pix_fmt yuv420p ");
                    break;
            }
            sb.AppendFormat("-g {0} ", Math.Max(1, fps * 2)); // ключевой кадр раз в 2 с — редактор режет точнее
            return sb.ToString();
        }

        public static ProcessStartInfo Info(string args)
        {
            return new ProcessStartInfo(Exe, args)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Dir,
                RedirectStandardError = true, RedirectStandardOutput = true, RedirectStandardInput = true,
                StandardErrorEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8
            };
        }

        // запустить и дождаться (короткие задачи: проверка, склейка без перекодирования)
        public static int Run(string args, int timeoutMs, out string stderr)
        {
            using (var p = Process.Start(Info(args)))
            {
                var err = new StringBuilder();
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (err) { if (err.Length < 20000) err.AppendLine(e.Data); } };
                p.OutputDataReceived += (s, e) => { };
                p.BeginErrorReadLine(); p.BeginOutputReadLine();
                p.StandardInput.Close();
                if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch (Exception) { } stderr = "FFmpeg не ответил вовремя"; return -1; }
                p.WaitForExit();
                lock (err) stderr = err.ToString();
                return p.ExitCode;
            }
        }

        // длительность файла в секундах
        public static double Duration(string file)
        {
            try
            {
                var pi = new ProcessStartInfo(Probe, "-v error -show_entries format=duration -of default=nw=1:nk=1 \"" + file + "\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(pi))
                {
                    string s = p.StandardOutput.ReadToEnd(); p.WaitForExit(10000);
                    double d;
                    return double.TryParse(s.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d) ? d : 0;
                }
            }
            catch (Exception) { return 0; }
        }

        public static string Q(string path) { return "\"" + path + "\""; }
        public static string N(double v) { return v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture); }
    }

    // журнал записи: %APPDATA%\Qural\rec.log (последние ~200 КБ)
    static class RecLog
    {
        public static void Write(string s)
        {
            try
            {
                string p = Path.Combine(Store.Dir, "rec.log");
                if (File.Exists(p) && new FileInfo(p).Length > 200000) File.Delete(p);
                File.AppendAllText(p, DateTime.Now.ToString("s") + " " + s + Environment.NewLine);
            }
            catch (Exception) { }
        }
    }

    // что записываем
    class RecTarget
    {
        public string Kind;          // screen | region | window
        public IntPtr Hwnd, HMonitor;
        public Rectangle Monitor;    // монитор, координаты экрана
        public Rectangle Area;       // записываемая область, координаты экрана
        public string Title = "";
    }

    // папка одной записи: куски (пауза = новый кусок), затем full.mkv
    [DataContract]
    class RecSession
    {
        [DataMember] public string Dir;
        [DataMember] public string Encoder, Codec;
        [DataMember] public int Fps, Width, Height;
        [DataMember] public bool HasSys, HasMic;
        [DataMember] public double Duration;
        [DataMember] public DateTime Started;
        [DataMember] public List<string> Segments = new List<string>();

        public string Full { get { return Path.Combine(Dir, "full.mkv"); } }

        public void Save()
        {
            try { using (var fs = File.Create(Path.Combine(Dir, "session.json"))) new DataContractSerializer(typeof(RecSession)).WriteObject(fs, this); } catch (Exception) { }
        }

        public static RecSession Load(string dir)
        {
            try { using (var fs = File.OpenRead(Path.Combine(dir, "session.json"))) return (RecSession)new DataContractSerializer(typeof(RecSession)).ReadObject(fs); }
            catch (Exception) { return null; }
        }

        public static string Root { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qural", "rec"); } }

        // старые записи (их уже сохранили или бросили) — оставляем 5 последних
        public static void Cleanup()
        {
            try
            {
                if (!Directory.Exists(Root)) return;
                var dirs = new List<DirectoryInfo>(new DirectoryInfo(Root).GetDirectories());
                dirs.Sort((a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
                for (int i = 5; i < dirs.Count; i++) try { dirs[i].Delete(true); } catch (Exception) { }
            }
            catch (Exception) { }
        }
    }

    // ───────────── запись ─────────────
    class Recorder
    {
        readonly TrayApp app;
        RecConfig Cfg { get { return app.Rec; } }

        RecSession session;
        RecTarget target;
        Process ff;
        NamedPipeServerStream sysPipe, micPipe;
        AudioCapture sys, mic;
        readonly StringBuilder log = new StringBuilder();
        RecPanel panel;
        RecFrame frame;
        readonly System.Diagnostics.Stopwatch clock = new System.Diagnostics.Stopwatch();
        bool stopping;
        int segIndex;

        public Recorder(TrayApp app) { this.app = app; }

        public bool Recording { get { return session != null; } }
        public bool Paused { get; private set; }
        public TimeSpan Elapsed { get { return clock.Elapsed; } }
        // самый громкий звук с прошлого вызова (читает только панель записи)
        public float SysPeak { get { var a = sys; return a == null ? 0 : a.TakePeak(); } }
        public float MicPeak { get { var a = mic; return a == null ? 0 : a.TakePeak(); } }
        public event Action Changed;
        void Fire() { var h = Changed; if (h != null) h(); }

        // начать запись выбранного (после отсчёта)
        public void Start(RecTarget t)
        {
            if (Recording) return;
            if (!Ff.Available) { app.RecNotify("Нет FFmpeg рядом с Qural — переустановите Qural", null); return; }
            RecSession.Cleanup();
            target = t;
            var enc = Ff.Pick(Cfg.Codec);
            var dir = Path.Combine(RecSession.Root, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(dir);
            session = new RecSession { Dir = dir, Encoder = enc.Id, Codec = enc.Codec, Fps = Cfg.Fps, HasSys = Cfg.SysAudio, HasMic = Cfg.Mic, Started = DateTime.Now };
            segIndex = 0; Paused = false; stopping = false;
            clock.Reset();
            if (!StartSegment()) { Abort(); return; }
            clock.Start();
            ShowPanel();
            Fire();
        }

        bool StartSegment()
        {
            var enc = Ff.Find(session.Encoder) ?? Ff.Pick("auto");
            string seg = Path.Combine(session.Dir, "seg" + segIndex.ToString("000") + ".mkv");
            string tag = Process.GetCurrentProcess().Id + "-" + DateTime.Now.Ticks;
            var args = new StringBuilder("-hide_banner -loglevel warning -y ");
            args.Append("-f lavfi -i ").Append(Ff.Q(Source(enc))).Append(' ');
            int inputs = 1, sysIdx = -1, micIdx = -1;
            string sysName = null, micName = null;
            if (session.HasSys) { sysName = "qural-rec-sys-" + tag; args.Append("-f f32le -ar 48000 -ac 2 -i ").Append(Ff.Q(@"\\.\pipe\" + sysName)).Append(' '); sysIdx = inputs++; }
            if (session.HasMic) { micName = "qural-rec-mic-" + tag; args.Append("-f f32le -ar 48000 -ac 2 -i ").Append(Ff.Q(@"\\.\pipe\" + micName)).Append(' '); micIdx = inputs++; }
            args.Append("-map 0:v ");
            if (sysIdx > 0) args.Append("-map ").Append(sysIdx).Append(":a ");
            if (micIdx > 0) args.Append("-map ").Append(micIdx).Append(":a ");
            args.AppendFormat("-fps_mode cfr -r {0} ", session.Fps);
            args.Append(Ff.VideoArgs(enc, Cfg.Quality, Cfg.Bitrate, session.Fps));
            if (inputs > 1) args.Append("-c:a flac ");
            int a = 0;
            if (sysIdx > 0) args.AppendFormat("-metadata:s:a:{0} title=\"Звук ПК\" ", a++);
            if (micIdx > 0) args.AppendFormat("-metadata:s:a:{0} title=\"Микрофон\" ", a++);
            args.Append(Ff.Q(seg));

            RecLog.Write("ffmpeg " + args);
            try
            {
                if (sysName != null) sysPipe = new NamedPipeServerStream(sysName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
                if (micName != null) micPipe = new NamedPipeServerStream(micName, PipeDirection.Out, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 1 << 20);
                lock (log) log.Length = 0;
                ff = new Process { StartInfo = Ff.Info(args.ToString()), EnableRaisingEvents = true };
                ff.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (log) { if (log.Length > 8000) log.Remove(0, 4000); log.AppendLine(e.Data); } };
                ff.OutputDataReceived += (s, e) => { };
                var me = ff;
                ff.Exited += (s, e) => OnExited(me);
                ff.Start();
                try { ff.PriorityClass = ProcessPriorityClass.AboveNormal; } catch (Exception) { }
                ff.BeginErrorReadLine(); ff.BeginOutputReadLine();
                // звук пишем, как только FFmpeg подключился к каналу
                if (sysPipe != null) { sys = new AudioCapture(true, null); var p = sysPipe; var c = sys; p.BeginWaitForConnection(r => { try { p.EndWaitForConnection(r); c.Start(p); } catch (Exception) { } }, null); }
                if (micPipe != null) { mic = new AudioCapture(false, Cfg.MicDevice); var p = micPipe; var c = mic; p.BeginWaitForConnection(r => { try { p.EndWaitForConnection(r); c.Start(p); } catch (Exception) { } }, null); }
                session.Segments.Add(seg);
                segIndex++;
                return true;
            }
            catch (Exception ex)
            {
                app.RecNotify("Запись не началась: " + ex.Message, null);
                CloseAudio();
                return false;
            }
        }

        // источник картинки для FFmpeg: окно, монитор или часть монитора — всё через Windows Graphics Capture на видеокарте
        string Source(Ff.Enc enc)
        {
            var t = target;
            var sb = new StringBuilder("gfxcapture=");
            int fps = session.Fps;
            int srcW, srcH;
            if (t.Kind == "window")
            {
                sb.Append("hwnd=").Append(t.Hwnd.ToInt64());
                srcW = t.Area.Width; srcH = t.Area.Height;
            }
            else
            {
                sb.Append("hmonitor=").Append(t.HMonitor.ToInt64());
                var m = t.Monitor; var r = Rectangle.Intersect(t.Area, m);
                if (r.Width < 16 || r.Height < 16) r = m;
                int cl = r.Left - m.Left, ct = r.Top - m.Top, cr = m.Right - r.Right, cb = m.Bottom - r.Bottom;
                if ((r.Width & 1) == 1) cr++;           // кодировщикам нужны чётные размеры
                if ((r.Height & 1) == 1) cb++;
                if (cl > 0 || ct > 0 || cr > 0 || cb > 0) sb.AppendFormat(":crop_left={0}:crop_top={1}:crop_right={2}:crop_bottom={3}", cl, ct, cr, cb);
                srcW = r.Width & ~1; srcH = r.Height & ~1;
            }
            sb.AppendFormat(":max_framerate={0}:capture_cursor={1}", fps, Cfg.Cursor ? 1 : 0);
            int limit = Cfg.Scale == "1440" ? 1440 : Cfg.Scale == "1080" ? 1080 : Cfg.Scale == "720" ? 720 : 0;
            if (limit > 0 && srcH > limit)
            {
                int w = (int)Math.Round(srcW * (double)limit / srcH) & ~1;
                sb.AppendFormat(":width={0}:height={1}:resize_mode=scale_aspect:scale_mode=bicubic", w, limit);
                session.Width = w; session.Height = limit;
            }
            else
            {
                if (t.Kind == "window") sb.Append(":width=-2:height=-2");
                session.Width = srcW; session.Height = srcH;
            }
            // кодировщикам не видеокарты NVIDIA — кадры в обычную память
            if (enc.Family != "nvenc") sb.Append(",hwdownload,format=bgra");
            return sb.ToString();
        }

        void OnExited(Process p)
        {
            // FFmpeg закрылся сам посреди записи — ошибка (окно закрыли, видеокарта отказала)
            try { app.Invoke(() => { if (p == ff && !stopping && !Paused) Fail(); }); } catch (Exception) { }
        }

        void Fail()
        {
            string why;
            lock (log) { why = LastLines(log.ToString(), 3); RecLog.Write("FFmpeg закрылся сам: " + log); }
            int segs = session == null ? 0 : session.Segments.Count;
            app.RecNotify("Запись остановилась: " + (why.Length > 0 ? why : "FFmpeg закрылся"), null);
            ff = null;
            CloseAudio();
            if (segs > 0 && File.Exists(session.Segments[segs - 1]) && new FileInfo(session.Segments[segs - 1]).Length > 4096) Stop(); // что успели — сохраним
            else Abort();
        }

        static string LastLines(string s, int n)
        {
            var lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            var take = new List<string>();
            for (int i = Math.Max(0, lines.Length - n); i < lines.Length; i++) take.Add(lines[i].Trim());
            return string.Join(" ", take.ToArray());
        }

        // завершить текущий кусок: FFmpeg — «q», дождаться, закрыть звук
        void EndSegment()
        {
            var p = ff;
            ff = null;
            if (p != null)
            {
                try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch (Exception) { }
                if (!p.WaitForExit(15000)) { try { p.Kill(); } catch (Exception) { } }
                p.Dispose();
            }
            CloseAudio();
        }

        void CloseAudio()
        {
            if (sys != null) { sys.Stop(); sys = null; }
            if (mic != null) { mic.Stop(); mic = null; }
            if (sysPipe != null) { try { sysPipe.Dispose(); } catch (Exception) { } sysPipe = null; }
            if (micPipe != null) { try { micPipe.Dispose(); } catch (Exception) { } micPipe = null; }
        }

        public void TogglePause()
        {
            if (!Recording || stopping) return;
            if (!Paused)
            {
                Paused = true; clock.Stop();
                EndSegment();
            }
            else
            {
                if (!StartSegment()) { Paused = true; return; }
                Paused = false; clock.Start();
            }
            if (panel != null) panel.Invalidate();
            Fire();
        }

        public void Stop()
        {
            if (!Recording || stopping) return;
            stopping = true;
            clock.Stop();
            EndSegment();
            HidePanel();
            var s = session;
            session = null; Paused = false;
            Fire();
            if (panel == null) { }
            app.RecNotify("Запись остановлена — готовлю файл…", null, false);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = Join(s);
                app.Invoke(() =>
                {
                    stopping = false;
                    if (err != null) { app.RecNotify(err, null); return; }
                    if (Cfg.AfterStop == "editor") app.OpenEditor(s);
                    else app.QuickExport(s);
                });
            });
        }

        // выход из Qural во время записи: дописать кусок, файлы остаются в папке записи
        public void Shutdown()
        {
            if (!Recording) return;
            stopping = true;
            EndSegment();
            HidePanel();
            if (session != null) session.Save();
            session = null;
        }

        // бросить без сохранения (не началась)
        void Abort()
        {
            stopping = true;
            EndSegment();
            HidePanel();
            var s = session; session = null; Paused = false; stopping = false;
            if (s != null) try { Directory.Delete(s.Dir, true); } catch (Exception) { }
            Fire();
        }

        // куски → один full.mkv без перекодирования
        static string Join(RecSession s)
        {
            try
            {
                var segs = s.Segments.FindAll(f => File.Exists(f) && new FileInfo(f).Length > 1024);
                if (segs.Count == 0) return "Запись пустая — ничего не сохранилось";
                if (segs.Count == 1) File.Move(segs[0], s.Full);
                else
                {
                    string list = Path.Combine(s.Dir, "list.txt");
                    var sb = new StringBuilder();
                    foreach (var f in segs) sb.Append("file '").Append(f.Replace("\\", "/").Replace("'", "'\\''")).Append("'\n");
                    File.WriteAllText(list, sb.ToString(), new UTF8Encoding(false));
                    string err;
                    if (Ff.Run("-hide_banner -loglevel error -y -f concat -safe 0 -i " + Ff.Q(list) + " -map 0 -c copy " + Ff.Q(s.Full), 600000, out err) != 0)
                        return "Не удалось склеить запись: " + LastLines(err, 2);
                    foreach (var f in segs) try { File.Delete(f); } catch (Exception) { }
                }
                s.Duration = Ff.Duration(s.Full);
                s.Save();
                return null;
            }
            catch (Exception ex) { return "Не удалось сохранить запись: " + ex.Message; }
        }

        void ShowPanel()
        {
            panel = new RecPanel(this, target.Monitor);
            panel.Show();
            if (target.Kind == "region") { frame = new RecFrame(target.Area); frame.Show(); }
        }

        void HidePanel()
        {
            if (panel != null) { panel.Close(); panel = null; }
            if (frame != null) { frame.Close(); frame = null; }
        }
    }

    // окна Qural, которых не должно быть на записи
    static class NoCapture
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
        public static void Apply(IntPtr h) { try { SetWindowDisplayAffinity(h, 0x11 /*WDA_EXCLUDEFROMCAPTURE*/); } catch (Exception) { } }
    }

    // ───────────── панель во время записи: время, пауза, стоп ─────────────
    class RecPanel : Form
    {
        readonly Recorder rec;
        // ~30 кадров в секунду: полоски звука движутся плавно (быстро вверх, медленно вниз), точка мигает раз в полсекунды
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 33 };
        readonly Font font = new Font("Segoe UI Semibold", 13f, GraphicsUnit.Pixel);
        readonly Stopwatch since = Stopwatch.StartNew();
        Rectangle pauseBtn, stopBtn; string hot; bool blink;
        float sysLevel, micLevel;
        Point dragFrom; bool dragging;

        public RecPanel(Recorder rec, Rectangle monitor)
        {
            this.rec = rec;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            Size = new Size(232, 40);
            Location = new Point(monitor.Left + (monitor.Width - Width) / 2, monitor.Top + 10);
            BackColor = Color.FromArgb(18, 20, 26);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            using (var p = Round(new Rectangle(0, 0, Width, Height), 20)) Region = new Region(p);
            pauseBtn = new Rectangle(Width - 76, 6, 28, 28); stopBtn = new Rectangle(Width - 42, 6, 28, 28);
            tick.Tick += (s, e) =>
            {
                blink = since.ElapsedMilliseconds / 500 % 2 == 0;
                sysLevel = Smooth(sysLevel, rec.SysPeak); micLevel = Smooth(micLevel, rec.MicPeak);
                Invalidate();
            };
            tick.Start();
            Text = "Qural — запись";
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/; return cp; } }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); NoCapture.Apply(Handle); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(BackColor);
            bool paused = rec.Paused;
            using (var br = new SolidBrush(paused ? Color.FromArgb(255, 204, 0) : (blink ? Color.FromArgb(255, 59, 48) : Color.FromArgb(150, 255, 59, 48))))
                g.FillEllipse(br, 14, 14, 12, 12);
            var t = rec.Elapsed;
            string time = ((int)t.TotalHours).ToString("00") + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
            using (var br = new SolidBrush(Color.White)) g.DrawString(time, font, br, 34, 10);
            // уровень звука: ПК и микрофон
            Level(g, 112, 13, sysLevel); Level(g, 112, 22, micLevel);
            Btn(g, pauseBtn, "pause", paused); Btn(g, stopBtn, "stop", false);
        }

        // уровень 0–1 на экране: громче — догоняет за пару кадров, тише — опускается плавно (~0,8 с до нуля)
        static float Smooth(float shown, float peak)
        {
            float target = Math.Min(1f, (float)Math.Sqrt(Math.Max(0f, peak)));
            if (target > shown) return shown + (target - shown) * .55f;
            return Math.Max(target, shown - .04f);
        }

        static void Level(Graphics g, int x, int y, float v)
        {
            using (var bg = new SolidBrush(Color.FromArgb(50, 255, 255, 255))) g.FillRectangle(bg, x, y, 40, 4);
            float w = 40 * Math.Min(1f, v);
            using (var br = new SolidBrush(v > 0.95f ? Color.FromArgb(255, 59, 48) : Color.FromArgb(52, 199, 89))) g.FillRectangle(br, (float)x, y, w, 4f);
        }

        void Btn(Graphics g, Rectangle r, string id, bool paused)
        {
            if (hot == id) using (var br = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillEllipse(br, r);
            using (var br = new SolidBrush(Color.White))
            {
                if (id == "stop") g.FillRectangle(br, r.X + 9, r.Y + 9, 10, 10);
                else if (paused) g.FillPolygon(br, new[] { new Point(r.X + 10, r.Y + 8), new Point(r.X + 20, r.Y + 14), new Point(r.X + 10, r.Y + 20) });
                else { g.FillRectangle(br, r.X + 9, r.Y + 8, 3, 12); g.FillRectangle(br, r.X + 16, r.Y + 8, 3, 12); }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging) { Location = new Point(Location.X + e.X - dragFrom.X, Location.Y + e.Y - dragFrom.Y); return; }
            string h = pauseBtn.Contains(e.Location) ? "pause" : stopBtn.Contains(e.Location) ? "stop" : null;
            if (h != hot) { hot = h; Invalidate(); }
            Cursor = h != null ? Cursors.Hand : Cursors.SizeAll;
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != null) { hot = null; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (pauseBtn.Contains(e.Location)) { rec.TogglePause(); return; }
            if (stopBtn.Contains(e.Location)) { rec.Stop(); return; }
            dragging = true; dragFrom = e.Location; Capture = true;
        }

        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; Capture = false; }

        protected override void OnFormClosed(FormClosedEventArgs e) { tick.Stop(); tick.Dispose(); font.Dispose(); base.OnFormClosed(e); }

        static GraphicsPath Round(Rectangle r, int rad)
        {
            var p = new GraphicsPath(); int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    // красная пунктирная рамка вокруг записываемой области (на записи её нет)
    class RecFrame : Form
    {
        public RecFrame(Rectangle area)
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            var r = area; r.Inflate(3, 3); Bounds = r;
            BackColor = Color.Magenta; TransparencyKey = Color.Magenta;
        }
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x20 /*TRANSPARENT*/ | 0x80000 /*LAYERED*/; return cp; } }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); NoCapture.Apply(Handle); }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Color.Magenta);
            using (var pen = new Pen(Color.FromArgb(255, 59, 48), 2f) { DashPattern = new float[] { 4, 3 } })
                e.Graphics.DrawRectangle(pen, 1, 1, Width - 3, Height - 3);
        }
    }

    // Отсчёт перед записью: тёмная плашка вверху монитора (там же, где потом панель записи):
    // «● Запись через 3», полоска тает за 3 секунды, цифра чуть «подпрыгивает» на каждой секунде. Щелчок — отмена.
    // Окно со своей прозрачностью (UpdateLayeredWindow): ровные скруглённые края и мягкая тень. На запись не попадает.
    class RecCountdown : Form
    {
        const int Total = 3000, W = 300, H = 58, M = 14;   // M — поле под тень
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 15 };
        readonly Stopwatch clock = new Stopwatch();
        readonly Action go; readonly Action<int> onNumber;
        readonly Point at;
        readonly Font big = new Font("Segoe UI", 28f, FontStyle.Bold, GraphicsUnit.Pixel);
        readonly Font label = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel);
        readonly Font small = new Font("Segoe UI", 12f, GraphicsUnit.Pixel);
        int shown = -1; bool done, cancelled;

        public RecCountdown(Rectangle monitor, Action go, Action<int> onNumber)
        {
            this.go = go; this.onNumber = onNumber;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual; AutoScaleMode = AutoScaleMode.None;
            at = new Point(monitor.Left + (monitor.Width - W) / 2 - M, monitor.Top + 10 - M);
            Bounds = new Rectangle(at, new Size(W + 2 * M, H + 2 * M));
            tick.Tick += (s, e) => Step();
            Cursor = Cursors.Hand;
            Text = "Qural — запись через…";
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= 0x80 | 0x08000000 | 0x80000 /*LAYERED*/; return cp; } }
        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); NoCapture.Apply(Handle); Render(0); }
        protected override void OnShown(EventArgs e) { base.OnShown(e); clock.Start(); tick.Start(); Step(); }
        protected override void OnMouseDown(MouseEventArgs e) { Cancel(); }
        public void Cancel() { if (done) return; cancelled = true; Finish(); }
        protected override void OnPaint(PaintEventArgs e) { }
        protected override void OnPaintBackground(PaintEventArgs e) { }

        void Step()
        {
            if (done) return;
            long t = clock.ElapsedMilliseconds;
            if (t >= Total) { Finish(); return; }
            int n = 3 - (int)(t / 1000);
            if (n != shown) { shown = n; if (onNumber != null) onNumber(n); }
            Render(t);
        }

        void Finish()
        {
            if (done) return;
            done = true; tick.Stop();
            if (!cancelled) go();       // сначала запись, потом убрать плашку — на деке нет «пустого» мига
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            tick.Stop(); tick.Dispose(); big.Dispose(); label.Dispose(); small.Dispose();
            if (onNumber != null) onNumber(0);
            base.OnFormClosed(e);
        }

        static float Ease(float x) { x = Math.Max(0, Math.Min(1, x)); return 1 - (1 - x) * (1 - x) * (1 - x); }

        void Render(long t)
        {
            if (!IsHandleCreated) return;
            using (var bmp = Draw(t)) Layered.Show(this, bmp, at, (byte)(255 * Ease(t / 220f)));
        }

        // кадр плашки в момент t (мс от начала)
        Bitmap Draw(long t)
        {
            float appear = Ease(t / 220f);                       // въезд сверху и проявление
            int n = Math.Max(1, 3 - (int)(t / 1000));
            float inSec = (t % 1000) / 1000f;
            float pop = t < 1000 ? 1f : 1f + .28f * (1 - Ease(inSec / .35f));   // цифра «подпрыгивает» на 2 и 1
            float left = 1f - t / (float)Total;                  // сколько полоски осталось
            var bmp = new Bitmap(W + 2 * M, H + 2 * M, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            {
                using (var g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);
                    g.TranslateTransform(0, -8 * (1 - appear));
                    var card = new RectangleF(M, M, W, H);
                    // мягкая тень
                    for (int i = 6; i >= 1; i--)
                        using (var p = Round(new RectangleF(card.X - i, card.Y - i + 3, card.Width + 2 * i, card.Height + 2 * i), 18 + i))
                        using (var br = new SolidBrush(Color.FromArgb(10, 0, 0, 0))) g.FillPath(br, p);
                    using (var p = Round(card, 18))
                    {
                        using (var br = new SolidBrush(Color.FromArgb(244, 22, 24, 30))) g.FillPath(br, p);
                        // полоска внизу карточки тает справа налево
                        var clip = g.Clip; g.SetClip(p);
                        using (var br = new SolidBrush(Color.FromArgb(40, 255, 255, 255))) g.FillRectangle(br, card.X, card.Bottom - 3, card.Width, 3);
                        using (var br = new SolidBrush(Color.FromArgb(255, 59, 48))) g.FillRectangle(br, card.X, card.Bottom - 3, card.Width * left, 3);
                        g.Clip = clip;
                        using (var pen = new Pen(Color.FromArgb(28, 255, 255, 255), 1f)) g.DrawPath(pen, p);
                    }
                    // пульсирующая красная точка с ореолом
                    float cx = card.X + 26, cy = card.Y + H / 2f - 1;
                    float ph = (float)((Math.Sin(t / 1000.0 * Math.PI * 2 - Math.PI / 2) + 1) / 2);
                    float halo = 9 + 6 * ph;
                    using (var br = new SolidBrush(Color.FromArgb((int)(70 * (1 - ph) + 20), 255, 59, 48))) g.FillEllipse(br, cx - halo, cy - halo, halo * 2, halo * 2);
                    using (var br = new SolidBrush(Color.FromArgb(255, 59, 48))) g.FillEllipse(br, cx - 6, cy - 6, 12, 12);
                    // подписи
                    using (var br = new SolidBrush(Color.White)) g.DrawString("Запись через", label, br, card.X + 46, card.Y + 10);
                    using (var br = new SolidBrush(Color.FromArgb(150, 255, 255, 255))) g.DrawString("щелчок — отмена", small, br, card.X + 47, card.Y + 30);
                    // цифра справа
                    var st = g.Save();
                    float dx = card.Right - 40, dy = card.Y + H / 2f - 1;
                    g.TranslateTransform(dx, dy); g.ScaleTransform(pop, pop);
                    using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    using (var br = new SolidBrush(Color.FromArgb((int)(255 * Math.Min(1f, .55f + inSec * 3)), 255, 255, 255)))
                        g.DrawString(n.ToString(), big, br, new RectangleF(-30, -24, 60, 48), sf);
                    g.Restore(st);
                }
            }
            return bmp;
        }

        static GraphicsPath Round(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure(); return p;
        }
    }

    // окно с прозрачностью по точкам: картинка 32 бита с альфой → UpdateLayeredWindow
    static class Layered
    {
        [StructLayout(LayoutKind.Sequential)] struct PT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] struct SZ { public int W, H; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLEND { public byte Op, Flags, Alpha, Format; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dst, ref PT pos, ref SZ size, IntPtr src, ref PT srcPos, int key, ref BLEND blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);

        public static void Show(Form f, Bitmap bmp, Point at, byte alpha)
        {
            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hb = IntPtr.Zero, old = IntPtr.Zero;
            try
            {
                hb = bmp.GetHbitmap(Color.FromArgb(0)); old = SelectObject(mem, hb);
                var pos = new PT { X = at.X, Y = at.Y }; var size = new SZ { W = bmp.Width, H = bmp.Height }; var src = new PT();
                var bl = new BLEND { Op = 0, Flags = 0, Alpha = alpha, Format = 1 /*AC_SRC_ALPHA*/ };
                UpdateLayeredWindow(f.Handle, screen, ref pos, ref size, mem, ref src, 0, ref bl, 2 /*ULW_ALPHA*/);
            }
            finally
            {
                if (old != IntPtr.Zero) SelectObject(mem, old);
                if (hb != IntPtr.Zero) DeleteObject(hb);
                DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            }
        }
    }
}
