using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Qural
{
    // «Сейчас играет» из Windows (Global System Media Transport Controls): любой плеер, который показывает себя
    // в системном окошке громкости, — Spotify, браузер, Яндекс Музыка… Название, исполнитель, обложка, позиция;
    // пауза/воспроизведение, следующий/предыдущий трек.
    // Всё общение с Windows — в своём фоновом потоке (WinRT ждём без блокировки окна Qural). Опрос раз в секунду —
    // только пока на деке видна кнопка «Сейчас играет» (Watching); команды выполняются и без него.
    sealed class Media : IDisposable
    {
        public sealed class Info
        {
            public string App = "", Title = "", Artist = "";
            public bool Has, Playing;
            public double Position, Duration;   // секунды
            public byte[] Cover;                // PNG/JPEG обложки или null
            public bool? Liked;                 // в «Любимых треках» Spotify (null — не Spotify или не подключён); ставит DeckLink
        }

        Info current = new Info();
        public Info Current { get { lock (gate) return current; } }
        public event Action Changed;            // вызывается в фоновом потоке

        readonly object gate = new object();
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        readonly Queue<Action<GlobalSystemMediaTransportControlsSessionManager>> jobs = new Queue<Action<GlobalSystemMediaTransportControlsSessionManager>>();
        volatile bool watching, stopping;
        string coverKey = "";
        byte[] cover;

        public Media()
        {
            new Thread(Loop) { IsBackground = true, Name = "Qural.Media" }.Start();
        }

        public bool Watching
        {
            get { return watching; }
            set { if (watching == value) return; watching = value; if (value) wake.Set(); }
        }

        // команда плееру: app — имя программы (Spotify, chrome…), "active" — программа в фокусе, "" — текущий плеер;
        // value — playpause | next | prev. done(null) — выполнено, done("текст") — не вышло (нет такого плеера и т. п.)
        public void Command(string app, string value, Action<string> done)
        {
            lock (jobs) jobs.Enqueue(mgr =>
            {
                string err = null;
                try
                {
                    var s = Find(mgr, app);
                    if (s == null) err = string.IsNullOrEmpty(app) ? "Сейчас ничего не играет" : "«" + app + "» сейчас ничего не играет";
                    else
                    {
                        bool ok;
                        switch ((value ?? "").ToLowerInvariant())
                        {
                            case "next": ok = Wait(s.TrySkipNextAsync()); break;
                            case "prev": ok = Wait(s.TrySkipPreviousAsync()); break;
                            case "play": ok = Wait(s.TryPlayAsync()); break;
                            case "pause": ok = Wait(s.TryPauseAsync()); break;
                            default: ok = Wait(s.TryTogglePlayPauseAsync()); break;
                        }
                        if (!ok) err = "Плеер не принял команду";
                    }
                }
                catch (Exception e) { err = e.Message; }
                if (done != null) done(err);
                Refresh(mgr, true);
            });
            wake.Set();
        }

        static GlobalSystemMediaTransportControlsSession Find(GlobalSystemMediaTransportControlsSessionManager mgr, string app)
        {
            app = (app ?? "").Trim();
            if (app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app = app.Substring(0, app.Length - 4);
            if (app.Length == 0) return mgr.GetCurrentSession();
            if (app.Equals("active", StringComparison.OrdinalIgnoreCase)) app = Foreground.ProcessName() ?? "";
            if (app.Length == 0) return null;
            // программа: «Spotify.exe», «Chrome», «308046B0AF4A39CB» (Firefox), «Microsoft.ZuneMusic_…!Microsoft.ZuneMusic»
            GlobalSystemMediaTransportControlsSession best = null;
            foreach (var s in mgr.GetSessions())
            {
                string id = s.SourceAppUserModelId ?? "";
                if (id.IndexOf(app, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (best == null || s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) best = s;
            }
            return best;
        }

        void Loop()
        {
            GlobalSystemMediaTransportControlsSessionManager mgr = null;
            while (!stopping)
            {
                wake.WaitOne(watching ? 1000 : Timeout.Infinite);
                if (stopping) break;
                try
                {
                    if (mgr == null) mgr = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
                    while (true)
                    {
                        Action<GlobalSystemMediaTransportControlsSessionManager> job;
                        lock (jobs) { if (jobs.Count == 0) break; job = jobs.Dequeue(); }
                        job(mgr);
                    }
                    if (watching) Refresh(mgr, false);
                }
                catch (Exception) { mgr = null; Thread.Sleep(1000); } // служба медиа перезапустилась — подключимся заново
            }
        }

        void Refresh(GlobalSystemMediaTransportControlsSessionManager mgr, bool force)
        {
            var info = new Info();
            try
            {
                var s = mgr.GetCurrentSession();
                if (s != null)
                {
                    var p = Wait(s.TryGetMediaPropertiesAsync());
                    var pb = s.GetPlaybackInfo();
                    var tl = s.GetTimelineProperties();
                    info.Has = true;
                    info.App = AppName(s.SourceAppUserModelId);
                    info.Title = p != null ? p.Title ?? "" : "";
                    info.Artist = p != null ? p.Artist ?? "" : "";
                    info.Playing = pb.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    info.Duration = (tl.EndTime - tl.StartTime).TotalSeconds;
                    double pos = tl.Position.TotalSeconds;
                    // позиция обновляется плеером не каждую секунду — досчитываем от времени последнего обновления
                    if (info.Playing) pos += (DateTimeOffset.Now - tl.LastUpdatedTime).TotalSeconds;
                    info.Position = Math.Max(0, info.Duration > 0 ? Math.Min(info.Duration, pos) : pos);
                    // обложку читаем, только когда сменился трек
                    string key = info.App + "|" + info.Title + "|" + info.Artist;
                    if (key != coverKey)
                    {
                        coverKey = key; cover = null;
                        if (p != null && p.Thumbnail != null)
                            try
                            {
                                var st = Wait(p.Thumbnail.OpenReadAsync());
                                var r = new DataReader(st);
                                uint n = Wait(r.LoadAsync((uint)Math.Min(st.Size, 4 * 1024 * 1024)));
                                var buf = new byte[n]; r.ReadBytes(buf); cover = buf;
                            }
                            catch (Exception) { cover = null; }
                    }
                    info.Cover = cover;
                }
            }
            catch (Exception) { info = new Info(); }
            Info old;
            lock (gate) { old = current; current = info; }
            bool changed = force || old.Has != info.Has || old.Title != info.Title || old.Artist != info.Artist || old.App != info.App ||
                           old.Playing != info.Playing || !ReferenceEquals(old.Cover, info.Cover) || (int)old.Position != (int)info.Position;
            if (changed) { var h = Changed; if (h != null) try { h(); } catch (Exception) { } }
        }

        // «Spotify.exe» → «Spotify», «Chrome» → «Chrome», «Microsoft.ZuneMusic_8wekyb…!Microsoft.ZuneMusic» → «ZuneMusic»
        static string AppName(string id)
        {
            id = id ?? "";
            int bang = id.IndexOf('!'); if (bang > 0) id = id.Substring(bang + 1);
            if (id.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) id = id.Substring(0, id.Length - 4);
            int dot = id.LastIndexOf('.'); if (dot >= 0 && dot < id.Length - 1) id = id.Substring(dot + 1);
            return id;
        }

        // ждём асинхронную операцию WinRT без System.Runtime.WindowsRuntime (ему нужен Windows.winmd из SDK)
        static T Wait<T>(IAsyncOperation<T> op)
        {
            var info = (IAsyncInfo)op;
            var sw = Stopwatch.StartNew();
            while (info.Status == AsyncStatus.Started)
            {
                if (sw.ElapsedMilliseconds > 3000) { try { info.Cancel(); } catch (Exception) { } throw new TimeoutException("WinRT: нет ответа"); }
                Thread.Sleep(5);
            }
            if (info.Status != AsyncStatus.Completed) throw new InvalidOperationException("WinRT: " + info.Status);
            return op.GetResults();
        }

        public void Dispose() { stopping = true; wake.Set(); }
    }
}
