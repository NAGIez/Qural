using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Qural
{
    // Связь с плагином Qural для Ulanzi Studio: именованный канал \\.\pipe\Qural.Deck.<пользователь>, строки JSON.
    //   плагин → Qural:
    //     {"cmd":"hello"}
    //     {"cmd":"watch","tiles":[{"id":"…","app":"Spotify"|"active","kind":"key"|"dial"}]}   — что сейчас видно на деке
    //     {"cmd":"volume","id":"…","app":"Spotify"|"active","value":"up|down|+N|-N|N|mute"}
    //     {"cmd":"apps"}                                                                        — список для окна настройки
    //     {"cmd":"keys","id":"…","app":"claude","keys":"ctrl+n"}                                — окно вперёд и нажать клавиши
//     {"cmd":"watch",...,"kind":"color-key"|"color-dial","app":"next"|"<профиль>"}            — кнопки профиля цвета
//     {"cmd":"color","id":"…","value":"next"|"prev"|"<профиль>"|"reset"}                     — сменить профиль цвета
//     {"cmd":"watch",...,"kind":"media-key"|"media-dial"}                                     — кнопка «Сейчас играет»
//     {"cmd":"watch",...,"kind":"stats-key"|"stats-dial"}                                     — «Нагрузка ПК»: Qural шлёт
//       {"type":"stats","cpu","cpuTemp","gpu","gpuTemp","gpuPower"} (−1 — нет данных), рисует плагин
//     {"cmd":"media","id":"…","app":""|"Spotify"|"active","value":"playpause|next|prev","orVolume":"+10"}
//                                                — плееру; нет у программы плеера и есть orVolume — изменить громкость
    //   Qural → плагин:
    //     {"type":"tiles","tiles":[{id,png,title,value,volume,muted,running}]}  — только изменившиеся картинки
    //     {"type":"apps","apps":[{process,title,running,volume,inQural}],"soundOn":bool,"profiles":[…],"activeProfile":N,"colorOn":bool}
    //     {"type":"error","id":"…","text":"…"}
    // Нет подключений или на деке не видно наших кнопок — Qural ничего не опрашивает. Видно — сверяется с микшером
    // раз в секунду (громкость могли поменять в самой программе) и сразу после своих изменений.
    sealed class DeckLink : IDisposable
    {
        public static string PipeName { get { return "Qural.Deck." + Environment.UserName; } }

        readonly TrayApp app;
        readonly SynchronizationContext ui;
        readonly object gate = new object();
        readonly List<Client> clients = new List<Client>();
        readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 16 * 1024 * 1024 };
        readonly System.Windows.Forms.Timer poll = new System.Windows.Forms.Timer { Interval = 1000 };
        // «Скриншот» / «Запись экрана»: пока идёт запись — время на кнопке (проверяем 4 раза в секунду, шлём раз в секунду)
        readonly System.Windows.Forms.Timer capPoll = new System.Windows.Forms.Timer { Interval = 250 };
        static bool IsCapture(string kind) { return kind == "shot-key" || kind == "rec-key"; }
        volatile bool stopping;
        IntPtr fgHook = IntPtr.Zero;
        WinEventProc fgProc; // держим ссылку, чтобы делегат не собрал сборщик мусора

        sealed class Tile
        {
            public string Id, App, Kind, Last = "";
            // «Сейчас играет»: какой трек показан, когда отправлена анимация и сколько длится её круг, до какого времени
            // не трогать кнопку (идёт переход между треками)
            public Media.Info Shown; public string Track = ""; public DateTime SentAt; public int LoopMs; public DateTime HoldUntil;
            public int Seg, Chain;   // кусок бегущей строки и номер «цепочки» (старые таймеры по нему узнают, что устарели)
            public bool? ShownLiked; // какое сердечко показано
        }

        readonly Media media = new Media();
        readonly Stats stats = new Stats();

        // «Нагрузка ПК» — цифры тем, у кого на деке видна такая кнопка (картинку рисует плагин)
        void SendStats()
        {
            var s = stats.Current;
            string msg = json.Serialize(new { type = "stats", cpu = s.Cpu, cpuTemp = s.CpuTemp, gpu = s.Gpu, gpuTemp = s.GpuTemp, gpuPower = s.GpuPower, ram = s.Ram, ramGb = s.RamUsedGb });
            foreach (var c in Clients)
                foreach (var t in c.Tiles.Values) if (t.Kind.StartsWith("stats")) { c.Send(msg); break; }
        }
        int skipDir = 1; DateTime skipAt;   // последняя команда «следующий / предыдущий» с деки
        // «Любимые треки» Spotify: трек (название|исполнитель) → лайкнут ли; проверяем один раз при смене трека
        readonly Spotify spotify = new Spotify();
        readonly Dictionary<string, bool> liked = new Dictionary<string, bool>();
        string likeAsked = "";

        static string LikeKey(Media.Info m) { return m.Title + "|" + m.Artist; }
        static bool IsSpotify(Media.Info m) { return m.Has && m.App.Equals("Spotify", StringComparison.OrdinalIgnoreCase); }

        // на потоке интерфейса: узнать у Spotify, лайкнут ли новый трек (в фоне), потом перерисовать
        void CheckLiked(Media.Info m)
        {
            if (!IsSpotify(m) || !spotify.Ready) return;
            string k = LikeKey(m);
            if (k == likeAsked || liked.ContainsKey(k)) return;
            likeAsked = k;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string err; bool? v = null;
                // трек в Spotify мог смениться чуть раньше, чем Windows сообщила, — сверяем позже на всякий случай
                string id = spotify.CurrentTrackId(out err);
                if (id != null) v = spotify.IsLiked(id, out err);
                // не вышло — не переспрашиваем каждую секунду: следующая попытка — на следующем треке
                try { ui.Post(__ => { if (v.HasValue) liked[k] = v.Value; Push(false); }, null); } catch (Exception) { }
            });
        }

        // долгое нажатие: лайк / снять лайк текущему треку Spotify
        void ToggleLike(Client c, string id)
        {
            var m = media.Current;
            if (!IsSpotify(m)) return;   // играет не Spotify — долгое нажатие просто ничего не делает (без «!» на кнопке)
            string k = LikeKey(m);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string err; string tid = spotify.CurrentTrackId(out err);
                bool? now = tid != null ? spotify.IsLiked(tid, out err) : null;
                bool? after = null;
                if (now.HasValue) { err = spotify.SetLiked(tid, !now.Value); if (err == null) after = !now.Value; }
                try
                {
                    ui.Post(__ =>
                    {
                        if (after.HasValue) liked[k] = after.Value;
                        else c.Send(json.Serialize(new { type = "error", id = id, text = err ?? "Spotify не ответил" }));
                        Push(false);
                    }, null);
                }
                catch (Exception) { }
            });
        }

        // состояние входа в Spotify — окну настройки кнопки
        string SpotifyJson(string note)
        {
            return json.Serialize(new { type = "spotify", ready = spotify.Ready, clientId = spotify.ClientId, waiting = spotify.LoggingIn, redirect = Spotify.Redirect, note = note ?? "" });
        }

        public DeckLink(TrayApp app)
        {
            this.app = app;
            ui = SynchronizationContext.Current ?? new System.Windows.Forms.WindowsFormsSynchronizationContext();
            poll.Tick += (s, e) => Push(false);
            capPoll.Tick += (s, e) => { if (app.RecOn && !app.RecPaused) Push(false, true); };
            media.Changed += () => { try { ui.Post(_ => Push(false), null); } catch (Exception) { } };
            stats.Changed += () => { try { ui.Post(_ => { if (!stopping) SendStats(); }, null); } catch (Exception) { } };
            new Thread(AcceptLoop) { IsBackground = true, Name = "Qural.DeckLink" }.Start();
        }

        // ── приём подключений (свой поток; в ожидании процессор не тратит) ──
        void AcceptLoop()
        {
            while (!stopping)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = CreatePipe();
                    pipe.WaitForConnection();
                    if (stopping) { pipe.Dispose(); break; }
                    var c = new Client(this, pipe);
                    lock (gate) clients.Add(c);
                    c.Start();
                }
                catch (Exception)
                {
                    if (pipe != null) try { pipe.Dispose(); } catch (Exception) { }
                    if (!stopping) Thread.Sleep(500); // например, имя канала занято — не крутимся впустую
                }
            }
        }

        // Доступ к каналу — явно текущему пользователю, с обычным уровнем целостности: иначе, если Qural запущен
        // от администратора (например, его запустила дека с правами администратора), плагин без прав не подключится.
        static bool plainPipe;
        static NamedPipeServerStream CreatePipe()
        {
            if (!plainPipe)
                try
                {
                    string sid = System.Security.Principal.WindowsIdentity.GetCurrent().User.Value;
                    var ps = new PipeSecurity();
                    ps.SetSecurityDescriptorSddlForm("D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;" + sid + ")S:(ML;;NW;;;ME)");
                    return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, ps);
                }
                catch (Exception) { plainPipe = true; } // не вышло (например, нет прав на метку) — как раньше
            return new NamedPipeServerStream(PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        }

        void Remove(Client c)
        {
            lock (gate) clients.Remove(c);
            try { ui.Post(_ => UpdateWatching(), null); } catch (Exception) { }
        }

        List<Client> Clients { get { lock (gate) return new List<Client>(clients); } }

        // на потоке интерфейса: следить за микшером и окном в фокусе, только если есть что показывать
        void UpdateWatching()
        {
            if (stopping) return;
            bool any = false, active = false, music = false, load = false, cap = false;
            foreach (var c in Clients)
                foreach (var t in c.Tiles.Values)
                {
                    if (t.Kind.StartsWith("media")) { music = true; continue; }   // «Сейчас играет» обновляет Media сам
                    if (t.Kind.StartsWith("stats")) { load = true; continue; }    // «Нагрузка ПК» — Stats сам
                    if (IsCapture(t.Kind)) { cap = true; continue; }             // скриншот / запись — по событиям
                    any = true; if (t.App == "active" && !t.Kind.StartsWith("color")) active = true;
                }
            if (any) { if (!poll.Enabled) poll.Start(); } else poll.Stop();
            if (cap) { if (!capPoll.Enabled) capPoll.Start(); } else capPoll.Stop();
            media.Watching = music;
            stats.Watching = load;
            if (active && fgHook == IntPtr.Zero)
            {
                fgProc = OnForeground;
                fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, fgProc, 0, 0, WINEVENT_OUTOFCONTEXT);
            }
            else if (!active && fgHook != IntPtr.Zero) { UnhookWinEvent(fgHook); fgHook = IntPtr.Zero; }
        }

        void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time) { Push(false); }

        // ── сообщения от плагина (приходят с потока клиента — выполняем на потоке интерфейса) ──
        void OnMessage(Client c, string line)
        {
            Dictionary<string, object> m;
            try { m = json.DeserializeObject(line) as Dictionary<string, object>; } catch (Exception) { return; }
            if (m == null) return;
            try { ui.Post(_ => Handle(c, m), null); } catch (Exception) { }
        }

        void Handle(Client c, Dictionary<string, object> m)
        {
            if (stopping) return;
            string cmd = Str(m, "cmd");
            if (cmd == "hello" || cmd == "apps") c.Send(AppsJson());
            else if (cmd == "watch")
            {
                var tiles = new Dictionary<string, Tile>();
                foreach (var o in List(m, "tiles"))
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string id = Str(d, "id"), kind = Str(d, "kind");
                    if (kind != "dial" && kind != "color-key" && kind != "color-dial" && kind != "media-key" && kind != "media-dial" &&
                        kind != "stats-key" && kind != "stats-dial" && kind != "shot-key" && kind != "rec-key") kind = "key";
                    // у кнопок цвета в app — профиль: next или название
                    // у «Скриншота» в app — что делает долгое нажатие (off — запись на этой кнопке не показывать)
                    string a = kind.StartsWith("color") ? (Str(d, "app").Trim().Length > 0 ? Str(d, "app").Trim() : "next")
                             : IsCapture(kind) ? Str(d, "app").Trim() : Target(Str(d, "app"));
                    if (id.Length == 0) continue;
                    Tile old;
                    // та же кнопка с тем же содержимым — картинку заново не шлём
                    if (c.Tiles.TryGetValue(id, out old) && old.App == a && old.Kind == kind) tiles[id] = old;   // со всем, что уже показано
                    else tiles[id] = new Tile { Id = id, App = a, Kind = kind };
                }
                c.Tiles = tiles;
                UpdateWatching();
                Push(false);
                SendStats();   // новой кнопке «Нагрузка ПК» — сразу последние цифры
            }
            else if (cmd == "mic")
            {
                // занят ли сейчас микрофон программой: {"cmd":"mic","id":"…","app":"Claude"} → {"type":"mic","id":"…","active":true}
                c.Send(json.Serialize(new { type = "mic", id = Str(m, "id"), active = MicInUse(Str(m, "app")) }));
            }
            else if (cmd == "keys")
            {
                // вывести окно программы вперёд и нажать сочетание: {"cmd":"keys","id":"…","app":"claude","keys":"ctrl+n"}
                string target = Str(m, "app");
                if (target.Length == 0) return;
                string err;
                try { err = WindowKeys.Send(target, Str(m, "keys"), Str(m, "text"), Str(m, "replace") == "True" || Str(m, "replace") == "true"); }
                catch (Exception e) { err = e.Message; }
                if (err != null) c.Send(json.Serialize(new { type = "error", id = Str(m, "id"), text = err }));
            }
            else if (cmd == "color")
            {
                string v = Str(m, "value").Trim(), err = null;
                if (v.Length == 0 || v.Equals("next", StringComparison.OrdinalIgnoreCase)) app.NextColor();
                else if (v.Equals("prev", StringComparison.OrdinalIgnoreCase)) app.PrevColor();
                else if (v.Equals("reset", StringComparison.OrdinalIgnoreCase)) app.ResetColors();
                else if (!app.SwitchColorByName(v)) err = "Нет профиля цвета «" + v + "»";
                if (err == null && app.General.ColorOff) err = "«Цвет» в Qural выключен — профиль выбран, но не применён";
                if (err != null) c.Send(json.Serialize(new { type = "error", id = Str(m, "id"), text = err }));
                Push(false);
            }
            else if (cmd == "rec")
            {
                // запись с кнопки деки: {"cmd":"rec","id":"…","value":"toggle|pause","mode":"region|screen|window"}
                RecLog.Write("дека: запись " + Str(m, "value") + " " + Str(m, "mode"));
                string err = app.RecCommand(Str(m, "value"), Str(m, "mode"));
                if (err != null) c.Send(json.Serialize(new { type = "error", id = Str(m, "id"), text = err }));
            }
            else if (cmd == "shot")
            {
                // скриншот с кнопки деки: {"cmd":"shot","id":"…","value":"region|full|window"}
                string err = app.ShotCommand(Str(m, "value"));
                if (err != null) c.Send(json.Serialize(new { type = "error", id = Str(m, "id"), text = err }));
            }
            else if (cmd == "spotify-status") c.Send(SpotifyJson(null));
            else if (cmd == "spotify-login")
            {
                // вход в Spotify: браузер «Разрешить?», ответ ловим на 127.0.0.1:8899 (в фоне, до 3 минут)
                string cid = Str(m, "clientId");
                System.Threading.ThreadPool.QueueUserWorkItem(_ =>
                {
                    string err = spotify.Login(cid);
                    try { ui.Post(__ => { liked.Clear(); likeAsked = ""; c.Send(SpotifyJson(err ?? "Готово — Spotify подключён")); Push(true); }, null); } catch (Exception) { }
                });
                c.Send(SpotifyJson("Откройте окно браузера и нажмите «Согласен»"));
            }
            else if (cmd == "media" && Str(m, "value") == "like") ToggleLike(c, Str(m, "id"));
            else if (cmd == "media")
            {
                string id = Str(m, "id"), target = Str(m, "app"), orVolume = Str(m, "orVolume");
                // направление для анимации смены трека: «назад» — обложки едут в обратную сторону
                string v = Str(m, "value").ToLowerInvariant();
                if (v == "prev" || v == "next") { skipDir = v == "prev" ? -1 : 1; skipAt = DateTime.UtcNow; }
                media.Command(target == "media" ? "" : target, Str(m, "value"), err => ui.Post(_ =>
                {
                    if (stopping) return;
                    // у программы нет плеера (например, Discord) — крутилка с зажатием просто меняет громкость крупно
                    if (err != null && orVolume.Length > 0) err = app.VolumeCommand(Target(target), orVolume);
                    if (err != null) c.Send(json.Serialize(new { type = "error", id = id, text = err }));
                    Push(false);
                }, null));
            }
            else if (cmd == "volume")
            {
                string value = Str(m, "value");
                if (value.Length == 0) return;
                string err = app.VolumeCommand(Target(Str(m, "app")), value);
                if (err != null) c.Send(json.Serialize(new { type = "error", id = Str(m, "id"), text = err }));
                Push(false);
            }
        }

        // Windows отмечает, какая программа пользуется микрофоном: ConsentStore\microphone\<программа>,
        // LastUsedTimeStop = 0 — пользуется прямо сейчас. Программы из Store — по имени пакета (Claude_…),
        // обычные — в NonPackaged по пути к exe (C:#…#claude.exe).
        static bool MicInUse(string app)
        {
            if (string.IsNullOrEmpty(app)) return false;
            const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root))
                {
                    if (k == null) return false;
                    foreach (var name in k.GetSubKeyNames())
                        if (name.StartsWith(app + "_", StringComparison.OrdinalIgnoreCase) && Active(k, name)) return true;
                    using (var np = k.OpenSubKey("NonPackaged"))
                        if (np != null)
                            foreach (var name in np.GetSubKeyNames())
                                if (name.EndsWith("#" + app + ".exe", StringComparison.OrdinalIgnoreCase) && Active(np, name)) return true;
                }
            }
            catch (Exception) { }
            return false;
        }

        static bool Active(Microsoft.Win32.RegistryKey parent, string name)
        {
            using (var s = parent.OpenSubKey(name))
            {
                if (s == null) return false;
                object start = s.GetValue("LastUsedTimeStart"), stop = s.GetValue("LastUsedTimeStop");
                return start is long && (long)start > 0 && stop is long && (long)stop == 0;
            }
        }

        static string Target(string a)
        {
            a = (a ?? "").Trim();
            if (a.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) a = a.Substring(0, a.Length - 4);
            return a.Length == 0 || a.Equals("active", StringComparison.OrdinalIgnoreCase) ? "active" : a;
        }

        static string Str(Dictionary<string, object> m, string k)
        {
            object v; return m.TryGetValue(k, out v) && v != null ? v.ToString() : "";
        }

        static IEnumerable<object> List(Dictionary<string, object> m, string k)
        {
            object v;
            if (!m.TryGetValue(k, out v) || v == null) return new object[0];
            var arr = v as object[]; if (arr != null) return arr;
            var al = v as System.Collections.ArrayList; if (al != null) return al.ToArray();
            return new object[0];
        }

        // ── состояние ──
        // Громкость программы по всем её именам exe из Qural. target — имя exe, название из Qural или active.
        DeckTiles.State Find(string target, List<Audio.Info> snap)
        {
            var st = new DeckTiles.State { Active = target == "active", SoundOff = app.General.SoundOff };
            string process = target;
            if (st.Active)
            {
                process = Foreground.ProcessName();
                if (string.IsNullOrEmpty(process) || process.Equals("Qural", StringComparison.OrdinalIgnoreCase))
                { st.Unknown = true; st.Title = "Активное окно"; return st; }
            }
            var entry = app.Sound.Apps.Find(x => x.Has(process) || (!st.Active && string.Equals(x.Title, process, StringComparison.OrdinalIgnoreCase)));
            string[] names = entry != null ? entry.Names() : new[] { process };
            string path = "";
            st.Muted = true;
            foreach (var s in snap)
                if (Array.Exists(names, n => string.Equals(n, s.Process, StringComparison.OrdinalIgnoreCase)))
                {
                    st.Running = true;
                    st.Volume = Math.Max(st.Volume, (int)Math.Round(s.Volume * 100));
                    st.Muted = st.Muted && s.Muted;
                    if (path.Length == 0) path = s.Path ?? "";
                }
            if (!st.Running) st.Muted = false;
            // значок: сначала по имени, которое видно в микшере, потом по остальным именам программы
            st.Process = process;
            if (DeckTiles.Glyph(process) == null)
                foreach (var n in names) if (DeckTiles.Glyph(n) != null) { st.Process = n; break; }
            st.Title = entry != null ? entry.Title : TitleOf(process, path);
            return st;
        }

        // скриншот открылся / закрылся, отсчёт, запись началась / пауза / остановилась
        public void CaptureChanged() { try { ui.Post(_ => Push(false, true), null); } catch (Exception) { } }

        // Вызывать на потоке интерфейса. force — перерисовать все кнопки; captureOnly — только «Скриншот» и «Запись экрана».
        public void Push(bool force, bool captureOnly = false)
        {
            if (stopping) return;
            var list = Clients;
            bool any = false;
            foreach (var c in list) if (c.Tiles.Count > 0) { any = true; break; }
            if (!any) return;

            // микшер опрашиваем, только если на деке есть кнопки громкости
            bool needSnap = false;
            if (!captureOnly)
                foreach (var c in list) foreach (var t in c.Tiles.Values) if (!t.Kind.StartsWith("color") && !t.Kind.StartsWith("media") && !t.Kind.StartsWith("stats") && !IsCapture(t.Kind)) { needSnap = true; break; }
            List<Audio.Info> snap = null;
            if (needSnap) try { snap = Audio.Snapshot(); } catch (Exception) { return; }
            var cache = new Dictionary<string, DeckTiles.State>(StringComparer.OrdinalIgnoreCase);

            foreach (var c in list)
            {
                var changed = new List<object>();
                foreach (var t in c.Tiles.Values)
                {
                    if (IsCapture(t.Kind))
                    {
                        object tile = CaptureTile(t, force);
                        if (tile != null) changed.Add(tile);
                        continue;
                    }
                    if (captureOnly) continue;
                    if (t.Kind.StartsWith("color"))
                    {
                        object tile = ColorTile(t, force);
                        if (tile != null) changed.Add(tile);
                        continue;
                    }
                    if (t.Kind.StartsWith("stats")) continue;   // рисует плагин по сообщению stats
                    if (t.Kind.StartsWith("media"))
                    {
                        object tile = MediaTile(t, force);
                        if (tile != null) changed.Add(tile);
                        continue;
                    }
                    DeckTiles.State st;
                    if (!cache.TryGetValue(t.App, out st)) { try { st = Find(t.App, snap); } catch (Exception) { continue; } cache[t.App] = st; }
                    string value = st.SoundOff ? "звук выкл." : st.Unknown || !st.Running ? "—" : st.Muted ? "без звука" : st.Volume + "%";
                    // громкость — отдельно: «без звука» тоже рисует полоску/кольцо по громкости
                    string key = t.Kind + "|" + st.Process + "|" + st.Title + "|" + value + "|" + st.Volume + "|" + st.Active + "|" + (DeckTiles.Glyph(st.Process) != null);
                    if (!force && key == t.Last) continue;
                    string png;
                    try { using (var b = t.Kind == "dial" ? DeckTiles.Dial(st) : DeckTiles.Key(st)) png = DeckTiles.ToBase64(b); }
                    catch (Exception) { continue; }
                    t.Last = key;
                    changed.Add(new { id = t.Id, png = png, title = st.Title, value = value, volume = st.Volume, muted = st.Muted, running = st.Running });
                }
                if (changed.Count > 0) c.Send(json.Serialize(new { type = "tiles", tiles = changed }));
            }
        }

        // «Скриншот» и «Запись экрана»: пока идёт выделение, отсчёт или запись — своя картинка (часто анимация),
        // иначе reset — плагин возвращает значок из manifest. На «Скриншоте» запись видна, если долгое нажатие её включает.
        object CaptureTile(Tile t, bool force)
        {
            bool recKey = t.Kind == "rec-key";
            bool showRec = recKey || t.App != "off";
            string mode = app.ShotMode;
            int cd = app.CountdownN;
            var el = app.RecElapsed;
            string state;
            if (mode == "shot" && !recKey) state = "shot";
            else if (mode == "pick" && showRec) state = "pick";
            else if (showRec && cd > 0) state = "cd|" + cd;
            else if (showRec && app.RecOn) state = (app.RecPaused ? "pause|" : "rec|") + (long)el.TotalSeconds;
            else state = "idle";
            if (!force && state == t.Last) return null;
            string png = null, gif = null;
            try
            {
                if (state == "idle") { t.Last = state; return new { id = t.Id, reset = true }; }
                if (state == "shot") gif = Convert.ToBase64String(CaptureTiles.ShotGif());
                else if (state == "pick") gif = Convert.ToBase64String(CaptureTiles.PickGif());
                else if (state.StartsWith("cd|")) gif = Convert.ToBase64String(CaptureTiles.CountdownGif(cd));
                else if (state.StartsWith("pause|")) gif = Convert.ToBase64String(CaptureTiles.PausedGif(TimeSpan.FromSeconds((long)el.TotalSeconds)));
                else gif = Convert.ToBase64String(CaptureTiles.RecGif(TimeSpan.FromSeconds((long)el.TotalSeconds)));
            }
            catch (Exception) { return null; }
            t.Last = state;
            return new { id = t.Id, png = png, gif = gif, title = "", value = "", volume = 0, muted = false, running = true };
        }

        // кнопка профиля цвета: next — показывает текущий профиль, название — светится, когда этот профиль включён
        object ColorTile(Tile t, bool force)
        {
            var profiles = app.Color.Profiles;
            bool next = t.App.Equals("next", StringComparison.OrdinalIgnoreCase);
            int idx = next ? app.Color.Active : profiles.FindIndex(p => string.Equals(p.Name.Trim(), t.App, StringComparison.OrdinalIgnoreCase));
            int num;
            if (!next && idx < 0 && int.TryParse(t.App, out num) && num >= 1 && num <= profiles.Count) idx = num - 1;
            var st = new DeckTiles.ColorState
            {
                Next = next,
                Missing = idx < 0 || idx >= profiles.Count,
                Number = idx + 1,
                Name = idx >= 0 && idx < profiles.Count ? profiles[idx].Name : t.App,
                On = idx == app.Color.Active,
                ColorOff = app.General.ColorOff
            };
            string key = t.Kind + "|" + st.Next + "|" + st.Missing + "|" + st.Number + "|" + st.Name + "|" + st.On + "|" + st.ColorOff;
            if (!force && key == t.Last) return null;
            string png;
            try { using (var b = t.Kind == "color-dial" ? DeckTiles.ColorDial(st) : DeckTiles.ColorKey(st)) png = DeckTiles.ToBase64(b); }
            catch (Exception) { return null; }
            t.Last = key;
            return new { id = t.Id, png = png, title = st.Name, value = st.ColorOff ? "цвет выкл." : st.On ? "включён" : "", volume = 0, muted = false, running = st.On };
        }

        // кнопка «Сейчас играет»: обложка, название, исполнитель, полоска позиции (шагами по 2 %, чтобы не слать картинку каждую секунду)
        // Кнопка (не крутилка) анимируется: смена трека — переход (обложка уезжает, новая въезжает), длинное название —
        // бегущая строка (GIF по кругу). Пока крутится бегущая строка, полоску позиции обновляем только в конце круга,
        // иначе строка дёргалась бы к началу каждые несколько секунд.
        object MediaTile(Tile t, bool force)
        {
            var mi = media.Current;
            var now = DateTime.UtcNow;
            // сердечко «Любимые треки» (только Spotify и если вход выполнен)
            bool lk;
            mi.Liked = IsSpotify(mi) && spotify.Ready && liked.TryGetValue(LikeKey(mi), out lk) ? lk : (bool?)null;
            CheckLiked(mi);
            int prog = mi.Duration > 1 ? (int)(mi.Position * 50 / mi.Duration) : -1;
            string track = mi.Has + "|" + mi.App + "|" + mi.Title + "|" + mi.Artist;
            bool likeChanged = t.Shown != null && t.ShownLiked != mi.Liked;   // Info общий — сравниваем с запомненным
            // идёт переход или кусок бегущей строки — не перебиваем (кроме смены трека и лайка)
            if (!force && now < t.HoldUntil && track == t.Track && !likeChanged) return null;
            string key = t.Kind + "|" + track + "|" + mi.Liked + "|" + mi.Playing + "|" + prog + "|" + (mi.Cover != null ? mi.Cover.Length : 0);
            if (!force && key == t.Last) return null;
            bool key2 = t.Kind != "media-dial";
            string title = !mi.Has ? "Ничего не играет" : mi.Artist.Length > 0 ? mi.Artist + " — " + mi.Title : mi.Title;
            bool sameTrack = track == t.Track;
            // идёт бегущая строка того же трека и изменилась только позиция — ждём конца круга
            if (!force && key2 && sameTrack && !likeChanged && t.LoopMs > 0 && t.Shown != null && t.Shown.Playing == mi.Playing &&
                ReferenceEquals(t.Shown.Cover, mi.Cover) && (now - t.SentAt).TotalMilliseconds < t.LoopMs) return null;
            string png = null, gif = null;
            try
            {
                if (key2)
                {
                    int ms; byte[] g = null;
                    int chain = ++t.Chain;
                    // смена трека (был показан другой, оба настоящие) — сначала переход, потом обычный вид
                    var old = !sameTrack && t.Shown != null && t.Shown.Has && mi.Has ? t.Shown : null;
                    if (!sameTrack) t.Seg = 0;
                    float run = DeckTiles.MarqueeRun(mi);
                    int segs = run > 0 ? (int)Math.Ceiling(run / DeckTiles.SegLen) : 0;
                    if (old != null)
                    {
                        int dir = skipDir < 0 && (now - skipAt).TotalSeconds < 5 ? -1 : 1;   // недавно нажали «предыдущий»
                        g = DeckTiles.TransitionGif(mi, old, dir, out ms);
                        After(t, chain, ms, now, null);                                     // потом — обычный вид
                    }
                    else if (segs == 1)
                    {
                        // название чуть длиннее кнопки — один GIF по кругу; позицию обновляем в конце круга
                        g = DeckTiles.MarqueeGif(mi, 0, run, true, true, true, out ms);
                        t.Last = key; t.LoopMs = ms; t.HoldUntil = DateTime.MinValue;
                    }
                    else if (segs > 1)
                    {
                        // длинное название — куски по очереди, каждый продолжает предыдущий; последний — пауза и снова сначала
                        if (t.Seg >= segs) t.Seg = 0;
                        float a = t.Seg * DeckTiles.SegLen, b = Math.Min(run, a + DeckTiles.SegLen);
                        g = DeckTiles.MarqueeGif(mi, a, b, t.Seg == 0, t.Seg == segs - 1, false, out ms);
                        int next = (t.Seg + 1) % segs;
                        After(t, chain, ms, now, () => t.Seg = next);
                    }
                    else { t.Last = key; t.LoopMs = 0; t.HoldUntil = DateTime.MinValue; }
                    if (g != null) gif = Convert.ToBase64String(g);
                    t.SentAt = now;
                }
                else t.Last = key;
                if (gif == null) using (var b = key2 ? DeckTiles.MediaKey(mi) : DeckTiles.MediaDial(mi)) png = DeckTiles.ToBase64(b);
            }
            catch (Exception) { return null; }
            t.Shown = mi; t.Track = track; t.ShownLiked = mi.Liked;
            return new { id = t.Id, png = png, gif = gif, title = title, value = "", volume = 0, muted = !mi.Playing, running = mi.Has };
        }

        // через ms (пока играет GIF) — следующий шаг: then() (например, следующий кусок строки) и перерисовать кнопку.
        // Старый таймер (кнопку уже перерисовали заново) ничего не делает.
        void After(Tile t, int chain, int ms, DateTime now, Action then)
        {
            t.HoldUntil = now.AddMilliseconds(ms);
            t.Last = ""; t.LoopMs = 0;
            var tm = new System.Windows.Forms.Timer { Interval = Math.Max(50, ms + 40) };
            tm.Tick += (s, e) =>
            {
                tm.Stop(); tm.Dispose();
                if (stopping || t.Chain != chain) return;
                if (then != null) then();
                t.HoldUntil = DateTime.MinValue;
                Push(false);
            };
            tm.Start();
        }

        // список программ для окна настройки кнопки: из Qural и всё, что сейчас в микшере
        string AppsJson()
        {
            var apps = new List<object>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<Audio.Info> snap;
            try { snap = Audio.Snapshot(); } catch (Exception) { snap = new List<Audio.Info>(); }
            foreach (var a in app.Sound.Apps)
            {
                bool running = false; int vol = 0;
                foreach (var s in snap) if (a.Has(s.Process)) { running = true; vol = Math.Max(vol, (int)Math.Round(s.Volume * 100)); }
                foreach (var n in a.Names()) seen.Add(n);
                apps.Add(new { process = a.Process, title = a.Title, running = running, volume = vol, inQural = true });
            }
            foreach (var s in snap)
            {
                if (seen.Contains(s.Process) || s.Process.Equals("Qural", StringComparison.OrdinalIgnoreCase)) continue;
                seen.Add(s.Process);
                apps.Add(new { process = s.Process, title = TitleOf(s.Process, s.Path), running = true, volume = (int)Math.Round(s.Volume * 100), inQural = false });
            }
            var profiles = new List<string>();
            foreach (var p in app.Color.Profiles) profiles.Add(p.Name);
            return json.Serialize(new { type = "apps", apps = apps, soundOn = !app.General.SoundOff, profiles = profiles, activeProfile = app.Color.Active, colorOn = !app.General.ColorOff });
        }

        static readonly Dictionary<string, string> titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // название программы из описания exe («Spotify», «Google Chrome»), иначе имя exe с большой буквы
        static string TitleOf(string process, string path)
        {
            string t;
            if (titles.TryGetValue(process ?? "", out t)) return t;
            t = null;
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    string d = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(d) && d.Trim().Length <= 32) t = d.Trim();
                }
            }
            catch (Exception) { }
            if (t == null) t = string.IsNullOrEmpty(process) ? "" : char.ToUpper(process[0]) + process.Substring(1);
            if (!string.IsNullOrEmpty(path)) titles[process] = t; // без пути название ещё может уточниться
            return t;
        }

        public void Dispose()
        {
            stopping = true;
            poll.Stop(); capPoll.Stop();
            media.Dispose();
            stats.Dispose();
            if (fgHook != IntPtr.Zero) { UnhookWinEvent(fgHook); fgHook = IntPtr.Zero; }
            List<Client> list; lock (gate) { list = new List<Client>(clients); clients.Clear(); }
            foreach (var c in list) c.Close();
            // разбудить поток приёма: подключаемся к себе и сразу отключаемся
            try { using (var p = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut)) p.Connect(200); } catch (Exception) { }
        }

        // ── один подключённый плагин: чтение и запись в своих потоках ──
        sealed class Client
        {
            readonly DeckLink owner;
            readonly NamedPipeServerStream pipe;
            readonly Queue<string> outbox = new Queue<string>();
            readonly AutoResetEvent hasOut = new AutoResetEvent(false);
            volatile bool closed;
            int removed;
            public Dictionary<string, Tile> Tiles = new Dictionary<string, Tile>(); // только на потоке интерфейса

            public Client(DeckLink owner, NamedPipeServerStream pipe) { this.owner = owner; this.pipe = pipe; }

            public void Start()
            {
                new Thread(ReadLoop) { IsBackground = true, Name = "Qural.DeckLink.read" }.Start();
                new Thread(WriteLoop) { IsBackground = true, Name = "Qural.DeckLink.write" }.Start();
            }

            void ReadLoop()
            {
                try
                {
                    var reader = new StreamReader(pipe, new UTF8Encoding(false));
                    string line;
                    while (!closed && (line = reader.ReadLine()) != null)
                        if (line.Length > 0 && line.Length < 256 * 1024) owner.OnMessage(this, line);
                }
                catch (Exception) { }
                Close();
            }

            void WriteLoop()
            {
                var enc = new UTF8Encoding(false);
                try
                {
                    while (!closed)
                    {
                        hasOut.WaitOne();
                        while (!closed)
                        {
                            string s;
                            lock (outbox) { if (outbox.Count == 0) break; s = outbox.Dequeue(); }
                            byte[] b = enc.GetBytes(s + "\n");
                            pipe.Write(b, 0, b.Length);
                            pipe.Flush();
                        }
                    }
                }
                catch (Exception) { }
                Close();
            }

            public void Send(string s)
            {
                if (closed) return;
                lock (outbox)
                {
                    if (outbox.Count > 64) return; // плагин завис и не читает — не копим память
                    outbox.Enqueue(s);
                }
                hasOut.Set();
            }

            public void Close()
            {
                closed = true;
                hasOut.Set();
                try { pipe.Dispose(); } catch (Exception) { }
                if (Interlocked.Exchange(ref removed, 1) == 0) owner.Remove(this);
            }
        }

        // ── смена окна в фокусе: событие Windows вместо опроса ──
        delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int obj, int child, uint thread, uint time);
        [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
        const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;
    }
}
