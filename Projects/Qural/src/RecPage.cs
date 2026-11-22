using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace Qural
{
    // Вкладка «Запись»: кодек, качество, звук, шумоподавление, папка, последние записи.
    partial class MainWindow
    {
        RecConfig Rc { get { return app.Rec; } }
        bool recProbeHooked;

        public void RecChanged() { SendRec(); }

        void OnRec(Dictionary<string, object> m)
        {
            switch (Str(m, "type"))
            {
                case "hotkey":
                    {
                        string target = Str(m, "target"), msg;
                        int key = Int(m, "key");
                        if (target == "toggle") msg = AssignHotkey(key, "rec-toggle", 0, k => Rc.Hotkey = k);
                        else if (target == "pause") msg = AssignHotkey(key, "rec-pause", 0, k => Rc.PauseHotkey = k);
                        else break;
                        if (msg != null) { Toast("rec", msg, false); SendRec(); }
                        break;
                    }

                case "set":
                    {
                        string v = Str(m, "value");
                        switch (Str(m, "field"))
                        {
                            case "codec": Rc.Codec = v.Length == 0 ? "auto" : v; break;
                            case "quality": Rc.Quality = v; break;
                            case "bitrate": Rc.Bitrate = Clamp(Int(m, "value"), 1, 200); break;
                            case "fps": Rc.Fps = Int(m, "value"); break;
                            case "scale": Rc.Scale = v; break;
                            case "cursor": Rc.Cursor = Bool(m, "value"); break;
                            case "countdown": Rc.Countdown = Bool(m, "value"); break;
                            case "sys": Rc.SysAudio = Bool(m, "value"); break;
                            case "mic": Rc.Mic = Bool(m, "value"); break;
                            case "sysVol": Rc.SysVolume = Clamp(Int(m, "value"), 0, 200); break;
                            case "micVol": Rc.MicVolume = Clamp(Int(m, "value"), 0, 200); break;
                            case "micDevice": Rc.MicDevice = v; break;
                            case "noise": Rc.Noise = v; break;
                            case "separate": Rc.SeparateTracks = Bool(m, "value"); break;
                            case "container": Rc.Container = v == "mkv" ? "mkv" : "mp4"; break;
                            case "after": Rc.AfterStop = v == "save" ? "save" : "editor"; break;
                            default: return;
                        }
                        Rc.Save();
                        SendRec();
                        break;
                    }

                case "folder":
                    using (var d = new FolderBrowserDialog { Description = "Папка для записей", SelectedPath = Rc.FolderPath, ShowNewFolderButton = true })
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) break;
                        Rc.Folder = d.SelectedPath; Rc.Save(); SendRec();
                    }
                    break;
                case "folderReset": Rc.Folder = ""; Rc.Save(); SendRec(); break;
                case "openFolder":
                    try { Directory.CreateDirectory(Rc.FolderPath); System.Diagnostics.Process.Start("explorer.exe", "\"" + Rc.FolderPath + "\""); }
                    catch (Exception ex) { Toast("rec", "Папка недоступна: " + ex.Message, false); }
                    break;

                case "start":
                    {
                        string mode = Str(m, "mode");
                        // окно Qural не должно попасть в запись — сворачиваем
                        WindowState = FormWindowState.Minimized;
                        var t = new Timer { Interval = 350 };
                        t.Tick += (s, e) => { t.Stop(); t.Dispose(); string err = app.RecCommand("start", mode); if (err != null && !IsDisposed) Toast("rec", err, false); };
                        t.Start();
                        break;
                    }
                case "stop": app.RecCommand("stop", null); break;
                case "pause": app.RecCommand("pause", null); break;

                case "open":
                    {
                        var s = RecSession.Load(Str(m, "dir"));
                        if (s == null || !File.Exists(s.Full)) { Toast("rec", "Эта запись не сохранилась", false); SendRec(); break; }
                        app.OpenEditor(s);
                        break;
                    }
                case "delete":
                    {
                        string dir = Str(m, "dir");
                        // удаляем только папки записей Qural
                        if (dir.StartsWith(RecSession.Root, StringComparison.OrdinalIgnoreCase)) try { Directory.Delete(dir, true); } catch (Exception) { }
                        SendRec();
                        break;
                    }
            }
        }

        void SendRec()
        {
            if (!ready) return;
            if (!recProbeHooked) { recProbeHooked = true; Ff.Probed += () => app.Invoke(() => { if (!IsDisposed) SendRec(); }); }
            Ff.ProbeAsync();
            var w = Ff.Working;
            var encs = new List<object>();
            foreach (var e in Ff.All) if (w != null && w.Contains(e.Id)) encs.Add(new { id = e.Id, label = e.Label, gpu = e.Gpu });
            var mics = new List<object>();
            foreach (var d in AudioCapture.List(true)) mics.Add(new { id = d.Id, name = d.Name, def = d.Default });
            var sessions = new List<object>();
            try
            {
                if (Directory.Exists(RecSession.Root))
                {
                    var dirs = new List<DirectoryInfo>(new DirectoryInfo(RecSession.Root).GetDirectories());
                    dirs.Sort((a, b) => b.CreationTimeUtc.CompareTo(a.CreationTimeUtc));
                    foreach (var d in dirs)
                    {
                        var s = RecSession.Load(d.FullName);
                        if (s == null || !File.Exists(s.Full)) continue;
                        long size = 0; try { size = new FileInfo(s.Full).Length; } catch (Exception) { }
                        sessions.Add(new { dir = d.FullName, when = s.Started.ToString("dd.MM HH:mm"), dur = s.Duration, size = size, w = s.Width, h = s.Height });
                    }
                }
            }
            catch (Exception) { }
            var rec = app.Recorder;
            var auto = Ff.Pick("auto");
            Post("rec", new
            {
                type = "state",
                state = new
                {
                    ffmpeg = Ff.Available,
                    toggle = HotkeyText.Format(Rc.Hotkey), pause = HotkeyText.Format(Rc.PauseHotkey),
                    recording = rec.Recording, paused = rec.Paused, elapsed = rec.Elapsed.TotalSeconds,
                    probing = w == null, encoders = encs, auto = auto == null ? "" : auto.Label,
                    codec = Rc.Codec, quality = Rc.Quality, bitrate = Rc.Bitrate, fps = Rc.Fps, scale = Rc.Scale,
                    cursor = Rc.Cursor, countdown = Rc.Countdown,
                    sys = Rc.SysAudio, mic = Rc.Mic, sysVol = Rc.SysVolume, micVol = Rc.MicVolume, micDevice = Rc.MicDevice, mics = mics,
                    noise = Rc.Noise, rnn = File.Exists(Path.Combine(Ff.Dir, "std.rnnn")), separate = Rc.SeparateTracks,
                    folder = Rc.FolderPath, customFolder = !string.IsNullOrEmpty(Rc.Folder), container = Rc.Container, after = Rc.AfterStop,
                    sessions = sessions
                }
            });
        }
    }
}
