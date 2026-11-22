using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Qural
{
    // ───────────── сохранение записи с правками ─────────────
    static class Exporter
    {
        public class Edits
        {
            public double Start, End;                          // обрезка начала и конца, секунды
            public List<double[]> Cuts = new List<double[]>(); // вырезанные куски
            public int CropX, CropY, CropW, CropH;             // кадр (0 — весь)
            public int SysVol = 100, MicVol = 100;
            public bool SysMute, MicMute, Separate;
            public string Noise = "rnn";                       // rnn | fft | off
            public string Format = "mp4";                      // mp4 | mkv | gif | m4a | mp3
            public string Encoder = "copy";                    // copy — как записано (без перекодирования, если можно)
            public string Quality = "high"; public int Bitrate = 12;
            public string Scale = "source";                    // source | 1440 | 1080 | 720 | 480
            public int GifFps = 12, GifWidth = 960;

            public static Edits Default(RecSession s, RecConfig c)
            {
                return new Edits
                {
                    Start = 0, End = s.Duration, SysVol = c.SysVolume, MicVol = c.MicVolume, Noise = c.Noise,
                    Separate = c.SeparateTracks, Format = c.Container, Encoder = "copy", Quality = c.Quality, Bitrate = c.Bitrate
                };
            }
        }

        public static string NextPath(RecConfig c, string ext)
        {
            string dir = c.FolderPath;
            Directory.CreateDirectory(dir);
            string name = "Запись " + DateTime.Now.ToString("yyyy-MM-dd HH-mm-ss");
            string path = Path.Combine(dir, name + "." + ext);
            for (int i = 2; File.Exists(path); i++) path = Path.Combine(dir, name + " (" + i + ")." + ext);
            return path;
        }

        // что оставить: [начало, конец] без вырезанных кусков
        public static List<double[]> Keep(Edits e, double dur)
        {
            double a = Math.Max(0, e.Start), b = e.End > 0 ? Math.Min(dur, e.End) : dur;
            var keep = new List<double[]> { new[] { a, b } };
            var cuts = new List<double[]>(e.Cuts);
            cuts.Sort((x, y) => x[0].CompareTo(y[0]));
            foreach (var c in cuts)
            {
                var next = new List<double[]>();
                foreach (var k in keep)
                {
                    if (c[1] <= k[0] || c[0] >= k[1]) { next.Add(k); continue; }
                    if (c[0] > k[0] + 0.05) next.Add(new[] { k[0], c[0] });
                    if (c[1] < k[1] - 0.05) next.Add(new[] { c[1], k[1] });
                }
                keep = next;
            }
            return keep.FindAll(k => k[1] - k[0] > 0.05);
        }

        // RNNoise в FFmpeg иногда выдаёт кадр NaN (10 мс) — кодировщик AAC на нём падает и звук обрывается.
        // Поэтому сразу после фильтра заменяем NaN нулём.
        public static string Noise(string mode)
        {
            if (mode == "rnn" && File.Exists(Path.Combine(Ff.Dir, "std.rnnn")))
                // arnndn иногда выдаёт NaN / бесконечность — кодер AAC на них падает; заменяем нулём, остальное — в пределах ±1
                return "arnndn=m=std.rnnn,aeval='if(isnan(val(0))+isinf(val(0)),0,clip(val(0),-1,1))|if(isnan(val(1))+isinf(val(1)),0,clip(val(1),-1,1))':c=same";
            if (mode == "rnn" || mode == "fft") return "afftdn=nr=14:nf=-40:tn=1";
            return null;
        }

        // аргументы FFmpeg; outDur — длительность результата (для прогресса)
        public static string Args(RecSession s, Edits e, string outPath, out double outDur)
        {
            double dur = s.Duration > 0 ? s.Duration : Ff.Duration(s.Full);
            var keep = Keep(e, dur);
            if (keep.Count == 0) throw new InvalidOperationException("После обрезки ничего не осталось");
            outDur = 0; foreach (var k in keep) outDur += k[1] - k[0];
            string fmt = e.Format;
            bool gif = fmt == "gif", audioOnly = fmt == "m4a" || fmt == "mp3";
            bool crop = e.CropW > 0 && e.CropH > 0 && (e.CropW < s.Width || e.CropH < s.Height);
            int limit = e.Scale == "1440" ? 1440 : e.Scale == "1080" ? 1080 : e.Scale == "720" ? 720 : e.Scale == "480" ? 480 : 0;
            int h = crop ? e.CropH : s.Height;
            bool scale = limit > 0 && h > limit;
            bool full = keep.Count == 1 && keep[0][0] < 0.05 && keep[0][1] > dur - 0.05;
            bool reencode = gif || !full || crop || scale || (e.Encoder != "copy" && e.Encoder != s.Encoder);
            var enc = e.Encoder == "copy" ? (Ff.Find(s.Encoder) ?? Ff.Pick("auto")) : Ff.Pick(e.Encoder);

            // дорожки: 0 — звук ПК, 1 — микрофон (если обе)
            int sysIdx = s.HasSys ? 0 : -1, micIdx = s.HasMic ? (s.HasSys ? 1 : 0) : -1;
            bool useSys = sysIdx >= 0 && !e.SysMute, useMic = micIdx >= 0 && !e.MicMute;
            if (gif) useSys = useMic = false;

            var a = new StringBuilder("-hide_banner -loglevel error -y -nostats -progress pipe:1 ");
            if (reencode && !audioOnly) a.Append("-hwaccel auto ");
            a.Append("-i ").Append(Ff.Q(s.Full)).Append(' ');
            var fc = new StringBuilder();
            string v = null, sa = null, ma = null;

            if (full)
            {
                if (!audioOnly) v = "0:v";
                if (useSys) sa = "[0:a:" + sysIdx + "]";
                if (useMic) ma = "[0:a:" + micIdx + "]";
                if (reencode && !audioOnly) { fc.Append("[0:v]null[vc];"); v = "[vc]"; }
            }
            else
            {
                int n = keep.Count; var cat = new StringBuilder();
                for (int i = 0; i < n; i++)
                {
                    string t = "start=" + Ff.N(keep[i][0]) + ":end=" + Ff.N(keep[i][1]);
                    if (!audioOnly) { fc.AppendFormat("[0:v]trim={0},setpts=PTS-STARTPTS[v{1}];", t, i); cat.AppendFormat("[v{0}]", i); }
                    if (useSys) { fc.AppendFormat("[0:a:{0}]atrim={1},asetpts=PTS-STARTPTS[s{2}];", sysIdx, t, i); cat.AppendFormat("[s{0}]", i); }
                    if (useMic) { fc.AppendFormat("[0:a:{0}]atrim={1},asetpts=PTS-STARTPTS[m{2}];", micIdx, t, i); cat.AppendFormat("[m{0}]", i); }
                }
                int na = (useSys ? 1 : 0) + (useMic ? 1 : 0);
                fc.Append(cat).AppendFormat("concat=n={0}:v={1}:a={2}", n, audioOnly ? 0 : 1, na);
                if (!audioOnly) fc.Append("[vc]");
                if (useSys) fc.Append("[sc]");
                if (useMic) fc.Append("[mc]");
                fc.Append(';');
                if (!audioOnly) v = "[vc]";
                if (useSys) sa = "[sc]";
                if (useMic) ma = "[mc]";
            }

            // картинка: кадр, размер, GIF
            if (!audioOnly && reencode)
            {
                var chain = new List<string>();
                if (crop) chain.Add(string.Format("crop={0}:{1}:{2}:{3}", e.CropW & ~1, e.CropH & ~1, e.CropX, e.CropY));
                if (gif)
                {
                    chain.Add("fps=" + Math.Max(5, Math.Min(30, e.GifFps)));
                    chain.Add("scale='min(" + Math.Max(160, e.GifWidth) + ",iw)':-1:flags=lanczos");
                    fc.Append(v).Append(string.Join(",", chain.ToArray())).Append(",split[g1][g2];[g1]palettegen=stats_mode=diff[pal];[g2][pal]paletteuse=dither=bayer:bayer_scale=4[vo];");
                }
                else
                {
                    if (scale) chain.Add("scale=-2:" + limit + ":flags=bicubic");
                    if (enc.Family != "nvenc" || chain.Count > 0) chain.Add("format=yuv420p");
                    fc.Append(v).Append(chain.Count > 0 ? string.Join(",", chain.ToArray()) : "null").Append("[vo];");
                }
                v = "[vo]";
            }

            // звук: громкость, шумоподавление на микрофоне, сведение (или две дорожки)
            if (sa != null) { fc.Append(sa).AppendFormat(CultureInfo.InvariantCulture, "volume={0:0.##}[sv];", e.SysVol / 100.0); sa = "[sv]"; }
            if (ma != null)
            {
                string nz = Noise(e.Noise);
                fc.Append(ma).Append(nz != null ? nz + "," : "").AppendFormat(CultureInfo.InvariantCulture, "volume={0:0.##}[mv];", e.MicVol / 100.0); ma = "[mv]";
            }
            var audioOut = new List<string>();
            if (sa != null && ma != null && !(e.Separate && !audioOnly))
            {
                fc.Append(sa).Append(ma).Append("amix=inputs=2:normalize=0:duration=longest,alimiter=limit=0.97:level=0[ao];");
                audioOut.Add("[ao]");
            }
            else { if (sa != null) audioOut.Add(sa); if (ma != null) audioOut.Add(ma); }

            string graph = fc.ToString().TrimEnd(';');
            if (graph.Length > 0) a.Append("-filter_complex ").Append(Ff.Q(graph)).Append(' ');

            if (!audioOnly && v != null) a.Append("-map ").Append(v.StartsWith("[") ? v : v).Append(' ');
            foreach (var o in audioOut) a.Append("-map ").Append(o).Append(' ');

            if (gif) a.Append("-loop 0 ");
            else if (!audioOnly)
            {
                if (reencode) a.Append(Ff.VideoArgs(enc, e.Quality, e.Bitrate, Math.Max(1, s.Fps)));
                else a.Append("-c:v copy ");
                string codec = reencode ? enc.Codec : s.Codec;
                if (fmt == "mp4" && codec == "hevc") a.Append("-tag:v hvc1 ");
            }
            if (audioOut.Count > 0 && !gif)
            {
                if (fmt == "mp3") a.Append("-c:a libmp3lame -q:a 2 ");
                else a.Append("-c:a aac -b:a 192k ");
                if (audioOut.Count == 2) a.Append("-metadata:s:a:0 title=\"Звук ПК\" -metadata:s:a:1 title=\"Микрофон\" ");
            }
            else if (!gif) a.Append("-an ");
            if (fmt == "mp4" || fmt == "m4a") a.Append("-movflags +faststart ");
            a.Append(Ff.Q(outPath));
            return a.ToString();
        }

        // запуск в фоне; progress — 0..1, done — null или текст ошибки
        public static Process RunAsync(RecSession s, Edits e, string outPath, Action<double> progress, Action<string> done)
        {
            double outDur;
            string args;
            try { args = Args(s, e, outPath, out outDur); }
            catch (Exception ex) { done(ex.Message); return null; }
            var p = new Process { StartInfo = Ff.Info(args), EnableRaisingEvents = true };
            var err = new StringBuilder();
            p.ErrorDataReceived += (o, d) => { if (d.Data != null) lock (err) { if (err.Length < 8000) err.AppendLine(d.Data); } };
            p.OutputDataReceived += (o, d) =>
            {
                // -progress: out_time_us=…
                if (d.Data == null || progress == null || !d.Data.StartsWith("out_time_us=")) return;
                long us; if (long.TryParse(d.Data.Substring(12), out us) && outDur > 0) progress(Math.Min(1, us / 1e6 / outDur));
            };
            p.Exited += (o, d) =>
            {
                int code = -1; try { code = p.ExitCode; } catch (Exception) { }
                string text; lock (err) text = err.ToString().Trim();
                if (code != 0) { try { if (File.Exists(outPath)) File.Delete(outPath); } catch (Exception) { } }
                done(code == 0 ? null : (text.Length > 0 ? Last(text) : "FFmpeg завершился с ошибкой " + code));
            };
            try
            {
                p.Start();
                p.BeginErrorReadLine(); p.BeginOutputReadLine();
                p.StandardInput.Close();
            }
            catch (Exception ex) { done(ex.Message); return null; }
            return p;
        }

        static string Last(string s)
        {
            var lines = s.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length == 0 ? s : lines[lines.Length - 1];
        }
    }

    // ───────────── окно редактора (WebView2, страница ui/editor.html) ─────────────
    class Editor : Form
    {
        const string UiHost = "qural.ui", MediaHost = "qural.media";
        readonly TrayApp app;
        readonly RecSession s;
        readonly WebView2 web = new WebView2();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 1 << 24 };
        bool ready, prepared;
        Process job;          // сохранение
        string lastOut;

        public Editor(TrayApp app, RecSession s)
        {
            this.app = app; this.s = s;
            Text = "Qural — редактор записи";
            Icon = AppIcon.Get(SystemInformation.IconSize);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(1280, 840);
            MinimumSize = new Size(960, 640);
            BackColor = Color.FromArgb(11, 13, 20);
            web.DefaultBackgroundColor = BackColor;
            web.Dock = DockStyle.Fill;
            Controls.Add(web);
            Load += (o, e) => Init();
            FormClosing += (o, e) => { if (job != null) try { if (!job.HasExited) job.Kill(); } catch (Exception) { } };
            FormClosed += (o, e) => web.Dispose();
        }

        async void Init()
        {
            try
            {
                // своя папка данных: окну настроек GPU отключён, а видео лучше с ним
                string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qural", "WebView2-editor");
                var env = await CoreWebView2Environment.CreateAsync(null, data, new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex) { MessageBox.Show(this, "Не удалось запустить WebView2:\n" + ex.Message, "Qural", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); return; }
            var core = web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            string ui = Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "ui");
            core.SetVirtualHostNameToFolderMapping(UiHost, ui, CoreWebView2HostResourceAccessKind.Deny);
            core.SetVirtualHostNameToFolderMapping(MediaHost, s.Dir, CoreWebView2HostResourceAccessKind.Allow);
            core.NewWindowRequested += (o, e) => e.Handled = true;
            core.NavigationStarting += (o, e) => { Uri u; if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out u) || u.Host != UiHost) e.Cancel = true; };
            core.WebMessageReceived += OnMessage;
            core.Navigate("https://" + UiHost + "/editor.html");
        }

        void Post(object o)
        {
            if (web.CoreWebView2 == null || IsDisposed) return;
            web.CoreWebView2.PostWebMessageAsJson(json.Serialize(o));
        }

        void PostUi(object o) { app.Invoke(() => Post(o)); }

        void OnMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            Dictionary<string, object> m;
            try { m = json.DeserializeObject(e.WebMessageAsJson) as Dictionary<string, object>; } catch (Exception) { return; }
            if (m == null) return;
            switch (Str(m, "type"))
            {
                case "ready": ready = true; SendInit(); Prepare(); break;
                case "noise": MakeNoisePreview(Str(m, "mode")); break;
                case "log": RecLog.Write("редактор: " + Str(m, "text")); break;
                case "export": Export(m); break;
                case "cancel": if (job != null) try { if (!job.HasExited) job.Kill(); } catch (Exception) { } break;
                case "openFolder": if (lastOut != null) try { Process.Start("explorer.exe", "/select,\"" + lastOut + "\""); } catch (Exception) { } break;
                case "copyFile":
                    if (lastOut != null && File.Exists(lastOut))
                        try { var sc = new StringCollection(); sc.Add(lastOut); Clipboard.SetFileDropList(sc); Post(new { type = "toast", text = "Файл скопирован — вставьте его в чат или папку" }); }
                        catch (Exception) { Post(new { type = "toast", text = "Буфер обмена занят" }); }
                    break;
                case "frame": CopyFrame(Num(m, "t"), m); break;
                case "delete":
                    try { Directory.Delete(s.Dir, true); } catch (Exception) { }
                    Close();
                    break;
                case "close": Close(); break;
            }
        }

        void SendInit()
        {
            var w = Ff.Working;
            var encs = new List<object>();
            foreach (var x in Ff.All) if (w == null || w.Contains(x.Id)) encs.Add(new { id = x.Id, label = x.Label });
            var d = Exporter.Edits.Default(s, app.Rec);
            Post(new
            {
                type = "init",
                dur = s.Duration, width = s.Width, height = s.Height, fps = s.Fps,
                hasSys = s.HasSys, hasMic = s.HasMic,
                encoder = s.Encoder, codec = s.Codec,
                encoders = encs,
                started = s.Started.ToString("d MMMM yyyy, HH:mm", new CultureInfo("ru-RU")),
                defaults = new { sysVol = d.SysVol, micVol = d.MicVol, noise = d.Noise, format = d.Format, quality = d.Quality, bitrate = d.Bitrate, separate = d.Separate },
                rnn = File.Exists(Path.Combine(Ff.Dir, "std.rnnn"))
            });
        }

        // файлы для просмотра: видео H.264/AV1 (браузер играет сам) и звук дорожками отдельно — громкость меняется на лету
        void Prepare()
        {
            if (prepared) { SendSources(); return; }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try
                {
                    bool playable = s.Codec == "h264" || s.Codec == "av1";
                    var a = new StringBuilder("-hide_banner -loglevel error -y -nostats -progress pipe:1 ");
                    if (!playable) a.Append("-hwaccel auto ");
                    a.Append("-i ").Append(Ff.Q(s.Full)).Append(' ');
                    a.Append("-map 0:v ");
                    if (playable) a.Append("-c:v copy ");
                    else
                    {
                        var enc = Ff.Pick("auto");
                        a.Append("-vf \"scale=-2:'min(1080,ih)',format=yuv420p\" ").Append(Ff.VideoArgs(enc, "medium", 8, Math.Max(1, s.Fps)));
                    }
                    a.Append("-an -movflags +faststart ").Append(Ff.Q(Path.Combine(s.Dir, "preview.mp4"))).Append(' ');
                    int idx = 0;
                    if (s.HasSys) a.Append("-map 0:a:").Append(idx++).Append(" -vn -c:a aac -b:a 160k ").Append(Ff.Q(Path.Combine(s.Dir, "sys.m4a"))).Append(' ');
                    if (s.HasMic) a.Append("-map 0:a:").Append(idx++).Append(" -vn -c:a aac -b:a 160k ").Append(Ff.Q(Path.Combine(s.Dir, "mic.m4a"))).Append(' ');
                    err = RunWithProgress(a.ToString(), s.Duration, p => PostUi(new { type = "prep", p = p }));
                }
                catch (Exception ex) { err = ex.Message; }
                app.Invoke(() =>
                {
                    if (IsDisposed) return;
                    if (err != null) { Post(new { type = "error", text = "Не удалось подготовить просмотр: " + err }); return; }
                    prepared = true;
                    SendSources();
                });
            });
        }

        void SendSources()
        {
            string b = "https://" + MediaHost + "/";
            Post(new { type = "sources", video = b + "preview.mp4", sys = s.HasSys ? b + "sys.m4a" : null, mic = s.HasMic ? b + "mic.m4a" : null });
        }

        readonly HashSet<string> noiseBusy = new HashSet<string>();

        // как будет звучать микрофон со шумоподавлением — готовим отдельный файл один раз
        void MakeNoisePreview(string mode)
        {
            if (!s.HasMic) return;
            if (mode != "rnn" && mode != "fft") { Post(new { type = "noiseReady", mode = mode, url = "https://" + MediaHost + "/mic.m4a" }); return; }
            string file = "mic_" + mode + ".m4a", path = Path.Combine(s.Dir, file);
            if (File.Exists(path)) { Post(new { type = "noiseReady", mode = mode, url = "https://" + MediaHost + "/" + file }); return; }
            int idx = s.HasSys ? 1 : 0;
            string nz = Exporter.Noise(mode);
            lock (noiseBusy) if (!noiseBusy.Add(mode)) return;   // уже готовится — второй FFmpeg в тот же файл не запускаем
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string args = "-hide_banner -loglevel error -y -nostats -progress pipe:1 -i " + Ff.Q(s.Full) + " -map 0:a:" + idx + " -af " + Ff.Q(nz) + " -c:a aac -b:a 160k " + Ff.Q(path);
                string err = RunWithProgress(args, s.Duration, p => PostUi(new { type = "noiseProgress", p = p }));
                if (err != null)
                {
                    // сбой бывает случайным — одна повторная попытка
                    RecLog.Write("редактор: шумоподавление " + mode + " ошибка, ещё раз: " + err);
                    err = RunWithProgress(args, s.Duration, p => PostUi(new { type = "noiseProgress", p = p }));
                }
                lock (noiseBusy) noiseBusy.Remove(mode);
                RecLog.Write("редактор: шумоподавление " + mode + (err == null ? " готово" : " ошибка: " + err));
                if (err != null) try { File.Delete(path); } catch (Exception) { } // недоделанный файл не оставляем — иначе в следующий раз взяли бы его
                PostUi(err == null ? (object)new { type = "noiseReady", mode = mode, url = "https://" + MediaHost + "/" + file } : new { type = "error", text = "Шумоподавление: " + err });
            });
        }

        static string RunWithProgress(string args, double dur, Action<double> progress)
        {
            using (var p = Process.Start(Ff.Info(args)))
            {
                var err = new StringBuilder();
                p.ErrorDataReceived += (o, d) => { if (d.Data != null) lock (err) { if (err.Length < 6000) err.AppendLine(d.Data); } };
                p.OutputDataReceived += (o, d) =>
                {
                    if (d.Data == null || !d.Data.StartsWith("out_time_us=")) return;
                    long us; if (long.TryParse(d.Data.Substring(12), out us) && dur > 0) progress(Math.Min(1, us / 1e6 / dur));
                };
                p.BeginErrorReadLine(); p.BeginOutputReadLine();
                p.StandardInput.Close();
                p.WaitForExit();
                if (p.ExitCode == 0) return null;
                lock (err) { var t = err.ToString().Trim(); return t.Length > 0 ? t.Split('\n')[t.Split('\n').Length - 1].Trim() : "ошибка " + p.ExitCode; }
            }
        }

        void Export(Dictionary<string, object> m)
        {
            if (job != null) return;
            var e = new Exporter.Edits
            {
                Start = Num(m, "start"), End = Num(m, "end"),
                CropX = (int)Num(m, "cropX"), CropY = (int)Num(m, "cropY"), CropW = (int)Num(m, "cropW"), CropH = (int)Num(m, "cropH"),
                SysVol = (int)Num(m, "sysVol"), MicVol = (int)Num(m, "micVol"),
                SysMute = Bool(m, "sysMute"), MicMute = Bool(m, "micMute"), Separate = Bool(m, "separate"),
                Noise = Str(m, "noise"), Format = Str(m, "format"), Encoder = Str(m, "encoder"),
                Quality = Str(m, "quality"), Bitrate = Math.Max(1, (int)Num(m, "bitrate")), Scale = Str(m, "scale"),
                GifFps = (int)Num(m, "gifFps"), GifWidth = (int)Num(m, "gifWidth")
            };
            object cuts; m.TryGetValue("cuts", out cuts);
            var list = cuts as object[] ?? (cuts is System.Collections.ArrayList ? ((System.Collections.ArrayList)cuts).ToArray() : null);
            if (list != null)
                foreach (var c in list)
                {
                    var pair = c as object[] ?? (c is System.Collections.ArrayList ? ((System.Collections.ArrayList)c).ToArray() : null);
                    if (pair != null && pair.Length == 2) e.Cuts.Add(new[] { ToD(pair[0]), ToD(pair[1]) });
                }
            string[] formats = { "mp4", "mkv", "gif", "m4a", "mp3" };
            if (Array.IndexOf(formats, e.Format) < 0) e.Format = "mp4";

            string path;
            if (Bool(m, "ask"))
            {
                string filter = e.Format == "gif" ? "GIF (*.gif)|*.gif" : e.Format == "mkv" ? "MKV (*.mkv)|*.mkv" : e.Format == "m4a" ? "M4A (*.m4a)|*.m4a" : e.Format == "mp3" ? "MP3 (*.mp3)|*.mp3" : "MP4 (*.mp4)|*.mp4";
                using (var d = new SaveFileDialog { Title = "Сохранить запись", Filter = filter, FileName = Path.GetFileName(Exporter.NextPath(app.Rec, e.Format)), InitialDirectory = app.Rec.FolderPath, OverwritePrompt = true })
                {
                    if (d.ShowDialog(this) != DialogResult.OK) { Post(new { type = "exportCancelled" }); return; }
                    path = d.FileName;
                }
            }
            else path = Exporter.NextPath(app.Rec, e.Format);

            Post(new { type = "exportStart", name = Path.GetFileName(path) });
            job = Exporter.RunAsync(s, e, path, p => PostUi(new { type = "exportProgress", p = p }), err => app.Invoke(() =>
            {
                job = null;
                if (IsDisposed) { if (err == null) app.RecNotify("Запись сохранена: " + Path.GetFileName(path), path); return; }
                RecLog.Write("редактор: сохранение " + path + (err == null ? " готово" : " ошибка: " + err));
                if (err != null) { Post(new { type = "exportError", text = err }); return; }
                lastOut = path;
                long size = 0; try { size = new FileInfo(path).Length; } catch (Exception) { }
                Post(new { type = "exportDone", path = path, name = Path.GetFileName(path), size = size });
            }));
        }

        // текущий кадр (с кадрированием) — в буфер обмена картинкой
        void CopyFrame(double t, Dictionary<string, object> m)
        {
            int cw = (int)Num(m, "cropW"), ch = (int)Num(m, "cropH"), cx = (int)Num(m, "cropX"), cy = (int)Num(m, "cropY");
            string png = Path.Combine(s.Dir, "frame.png");
            string vf = cw > 0 && ch > 0 ? " -vf crop=" + cw + ":" + ch + ":" + cx + ":" + cy : "";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err;
                int code = Ff.Run("-hide_banner -loglevel error -y -ss " + Ff.N(Math.Max(0, t)) + " -i " + Ff.Q(s.Full) + " -frames:v 1" + vf + " " + Ff.Q(png), 30000, out err);
                app.Invoke(() =>
                {
                    if (IsDisposed) return;
                    if (code != 0 || !File.Exists(png)) { Post(new { type = "toast", text = "Кадр не получился" }); return; }
                    try
                    {
                        using (var fs = new FileStream(png, FileMode.Open, FileAccess.Read))
                        using (var img = new Bitmap(fs))
                        using (var b = new Bitmap(img)) Shooter.ToClipboard(b);
                        Post(new { type = "toast", text = "Кадр скопирован в буфер обмена" });
                    }
                    catch (Exception) { Post(new { type = "toast", text = "Буфер обмена занят" }); }
                });
            });
        }

        static string Str(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
        static bool Bool(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) && v is bool && (bool)v; }
        static double Num(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) ? ToD(v) : 0; }
        static double ToD(object v) { try { return Convert.ToDouble(v, CultureInfo.InvariantCulture); } catch (Exception) { return 0; } }
    }
}
