using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace Qural
{
    // «Сеть» — модуль Qural: все подключения сразу (Ethernet, Wi-Fi, телефон). Внутри — net-merge
    // (github.com/net-merge/net-merge, MIT), он лежит в папке Qural (net-merge\), отдельно ставить ничего не нужно.
    //   • Запуск: задача Windows «Qural net-merge» (с правами администратора — им нужен виртуальный адаптер).
    //     Создаётся один раз (окно UAC), дальше Qural запускает её без вопросов. Задача запускает Qural.exe --net-run,
    //     а он — net-merge-tray.exe run с нужными параметрами: без окна и без значка в трее.
    //   • Пауза — остановить net-merge (метка ctl\off), продолжить — запустить снова. Автопауза — Qural сам
    //     останавливает его, когда подключений с интернетом меньше двух, и запускает, когда их снова два.
    //   • Читает файлы net-merge в %LOCALAPPDATA%\net-merge: status.json (раз в секунду, пока работает),
    //     settings.json (игровой режим, «беречь телефон» — net-merge читает при запуске), limits.json.
    //   • Скорость и трафик подключений Qural считает сам по счётчикам Windows, за день и месяц — в %APPDATA%\Qural\net.json.
    //   • Обновления net-merge: раз в 6 часов спрашивает GitHub; «Обновить» — скачать, сверить SHA-256, заменить файлы.
    // Опрос раз в секунду — только пока на это смотрят (вкладка «Сеть», кнопка на деке); иначе раз в 10 секунд.
    sealed class NetMerge : IDisposable
    {
        public const string TaskName = "Qural net-merge";
        const int Hist = 120;   // секунд на графике

        public static string Folder { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "net-merge"); } }
        public static string Exe { get { return Path.Combine(Folder, "net-merge.exe"); } }          // командная строка (limit, alias)
        public static string RunExe { get { return Path.Combine(Folder, "net-merge-tray.exe"); } }  // та же программа без консоли
        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "net-merge"); } }
        public static bool Installed { get { return File.Exists(Exe) && File.Exists(RunExe) && File.Exists(Path.Combine(Folder, "wintun.dll")); } }
        /// Что запустить задаче: net-merge и/или помощник «Программы» (пишет Qural перед запуском задачи).
        static string WantFile { get { return Path.Combine(NetMon.Folder, "net-want.txt"); } }

        public static string FilesVersion
        {
            get { try { return File.ReadAllText(Path.Combine(Folder, "VERSION")).Trim(); } catch (Exception) { return ""; } }
        }

        // ── что показываем ──
        public sealed class Adapter
        {
            public string Name = "", Label = "", Isp = "", IP = "", IP6 = "", State = "", State6 = "", Kind = "other";
            public bool Present, Phone, Merged, Online;   // Merged — net-merge использует это подключение; Online — есть шлюз
            public double Weight, LimitMbps, CoolMbps;
            public int Active, Total, Failed;
            public double Down, Up;                     // байт/с по счётчикам Windows (весь трафик подключения)
            public double ViaDown, ViaUp;               // байт/с, которые прошли через net-merge
            public long DayIn, DayOut, MonthIn, MonthOut;
            public float[] HistDown = new float[Hist], HistUp = new float[Hist];   // Мбит/с, старые слева

            public Adapter Copy()
            {
                var c = (Adapter)MemberwiseClone();
                c.HistDown = (float[])HistDown.Clone(); c.HistUp = (float[])HistUp.Clone();
                return c;
            }
        }

        public sealed class Snapshot
        {
            // missing — нет файлов net-merge; off — модуль выключен; setup — нужно разрешение (задача ещё не создана);
            // paused — пауза вручную; waiting — автопауза: меньше двух подключений; starting — запускается; on — работает
            public string State = "missing";
            public string Version = "", V6 = "", Error = "", Update = "";
            public DateTime Since;
            public List<Adapter> Adapters = new List<Adapter>();
            public double Down, Up;                     // сумма по подключениям, байт/с
            public float[] HistDown = new float[Hist], HistUp = new float[Hist];
            public bool IspNames = true, GameMode, PhoneCooling, Updates = true, UseAll = true, AutoPause = true, Updating;
            public int Online;                          // подключений с интернетом (есть шлюз)
            public bool AppsOn = true, AppsLive;        // считать трафик программ; помощник работает
            public List<App> Apps = new List<App>();
        }

        // программа: скорость, график, через что идёт, трафик за день и месяц
        public sealed class App
        {
            public string Name = "", Path = "", Route = "";   // Route: "merge" — через объединение, "local" — внутри компьютера, иначе подключение
            public double Down, Up;                            // байт/с
            public long DayIn, DayOut, MonthIn, MonthOut;
            public float[] Hist = new float[60];              // Мбит/с (приём + отдача), последняя минута
            public List<KeyValuePair<string, double>> Split = new List<KeyValuePair<string, double>>();   // подключение → байт/с (через объединение)
        }

        public Snapshot Current { get { lock (gate) return snap; } }
        public readonly NetSpeed Speed = new NetSpeed();   // замер скорости («Сеть» → «Скорость»)
        public event Action Changed;      // в фоновом потоке

        readonly TrayApp app;
        readonly object gate = new object();
        Snapshot snap = new Snapshot();
        readonly Dictionary<string, Adapter> adapters = new Dictionary<string, Adapter>(StringComparer.OrdinalIgnoreCase);
        readonly float[] histDown = new float[Hist], histUp = new float[Hist];
        readonly Dictionary<string, long[]> lastCounters = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        DateTime lastAt = DateTime.MinValue;
        readonly NetUsage cfg = NetUsage.Load();
        DateTime usageSaved = DateTime.UtcNow, taskChecked = DateTime.MinValue, startedAt = DateTime.MinValue, updateChecked = DateTime.MinValue;
        bool taskOk;
        string lastState = "", error = "", update = "";
        volatile bool updating;
        readonly System.Threading.Timer timer;
        int watchers;
        volatile bool disposed;
        // программы: накопленное помощником (по имени) и прошлый замер — для скорости
        readonly Dictionary<string, long[]> appLast = new Dictionary<string, long[]>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<string, long>> viaLast = new Dictionary<string, Dictionary<string, long>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, App> apps = new Dictionary<string, App>(StringComparer.OrdinalIgnoreCase);
        long appMonPid, appT; DateTime monStartAt = DateTime.MinValue;

        public NetMerge(TrayApp app)
        {
            this.app = app;
            timer = new System.Threading.Timer(_ => Tick(), null, 1500, 10000);
        }

        bool ModuleOn { get { return !app.General.NetOff; } }

        /// Кто-то смотрит (вкладка, дека): опрос раз в секунду. Снять — Unwatch.
        public void Watch() { if (Interlocked.Increment(ref watchers) == 1) Retime(); }
        public void Unwatch() { if (Interlocked.Decrement(ref watchers) <= 0) { watchers = 0; Retime(); } }
        void Retime() { if (!disposed) try { timer.Change(0, watchers > 0 ? 1000 : 10000); } catch (ObjectDisposedException) { } }

        /// Обновить сейчас (например, перед показом меню трея).
        public void Refresh() { Tick(); }

        int ticking;
        void Tick()
        {
            if (disposed || Interlocked.Exchange(ref ticking, 1) == 1) return;
            try { Sample(); Auto(); CheckUpdate(false); }
            catch (Exception ex) { RecLog.Write("сеть: " + ex.Message); }
            finally { ticking = 0; }
            var h = Changed; if (h != null) try { h(); } catch (Exception) { }
        }

        static bool Running { get { return Process.GetProcessesByName("net-merge-tray").Length > 0; } }

        void Sample()
        {
            var s = new Snapshot();
            var now = DateTime.UtcNow;
            double dt = lastAt == DateTime.MinValue ? 0 : (now - lastAt).TotalSeconds;
            lastAt = now;
            s.IspNames = !cfg.NoIsp; s.UseAll = !cfg.Ipv6; s.AutoPause = !cfg.NoAutoPause; s.Updates = !cfg.NoUpdateCheck;
            s.Error = error; s.Update = update; s.Updating = updating;
            s.Version = FilesVersion;

            // задача запуска — не чаще раза в минуту
            if (Installed && (now - taskChecked).TotalSeconds > 60) { taskChecked = now; taskOk = TaskMatches(); }

            var status = ReadJson(Path.Combine(Dir, "status.json"));
            DateTime updated = status != null ? Date(status, "Updated") : DateTime.MinValue;
            bool fresh = status != null && (DateTime.Now - updated).TotalSeconds < 6;
            bool running = Running;
            if (!Installed) s.State = "missing";
            else if (!ModuleOn) s.State = running && fresh ? "on" : "off";
            else if (fresh && running) s.State = "on";
            else if (running || (now - startedAt).TotalSeconds < 12) s.State = "starting";
            else if (!taskOk) s.State = "setup";
            else if (cfg.UserPaused) s.State = "paused";
            else s.State = "waiting";
            if (fresh) { s.V6 = Str(status, "V6"); s.Since = Date(status, "Since"); }

            var set = ReadJson(Path.Combine(Dir, "settings.json"));
            if (set != null) { s.GameMode = Bool(set, "game_mode"); s.PhoneCooling = Bool(set, "phone_cooling"); }
            var limits = Dict(ReadJson(Path.Combine(Dir, "limits.json")), "limits");
            var aliases = Dict(ReadJson(Path.Combine(Dir, "tray-preferences.json")), "aliases");

            // подключения: что знает net-merge + счётчики Windows
            var nics = new Dictionary<string, Dictionary<string, object>>(StringComparer.OrdinalIgnoreCase);
            if (status != null && fresh)
            {
                object l; if (status.TryGetValue("NICs", out l))
                    foreach (var o in AsList(l)) { var d = o as Dictionary<string, object>; if (d != null) nics[Str(d, "Name")] = d; }
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            double totalDown = 0, totalUp = 0;
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (!Physical(ni) && !nics.ContainsKey(ni.Name)) continue;
                if (ni.OperationalStatus != OperationalStatus.Up && !nics.ContainsKey(ni.Name)) continue;
                Adapter ad;
                if (!adapters.TryGetValue(ni.Name, out ad)) { ad = new Adapter { Name = ni.Name }; adapters[ni.Name] = ad; }
                seen.Add(ni.Name);
                ad.Present = ni.OperationalStatus == OperationalStatus.Up;
                ad.Kind = KindOf(ni);
                ad.Online = ad.Present && HasGateway(ni);
                if (ad.Online) s.Online++;
                long rx = 0, tx = 0;
                try { var st = ni.GetIPStatistics(); rx = st.BytesReceived; tx = st.BytesSent; } catch (Exception) { }
                long[] last;
                double down = 0, up = 0;
                if (lastCounters.TryGetValue(ni.Name, out last) && dt > 0.2)
                {
                    long dIn = rx - last[0], dOut = tx - last[1];
                    if (dIn >= 0 && dOut >= 0 && dIn < (long)(dt * 2e9) && dOut < (long)(dt * 2e9))
                    {
                        down = dIn / dt; up = dOut / dt;
                        cfg.Add(ni.Name, dIn, dOut);
                    }
                }
                lastCounters[ni.Name] = new[] { rx, tx };
                ad.Down = down; ad.Up = up;
                totalDown += down; totalUp += up;
                if (dt > 0) { Push(ad.HistDown, (float)(down * 8 / 1e6)); Push(ad.HistUp, (float)(up * 8 / 1e6)); }

                Dictionary<string, object> n;
                ad.Merged = nics.TryGetValue(ni.Name, out n);
                if (ad.Merged)
                {
                    ad.IP = Str(n, "IP"); ad.IP6 = Str(n, "IP6"); ad.State = Str(n, "State"); ad.State6 = Str(n, "State6");
                    ad.Weight = Num(n, "Weight"); ad.Active = (int)Num(n, "Active"); ad.Total = (int)Num(n, "Total"); ad.Failed = (int)Num(n, "Failed");
                    ad.ViaDown = Num(n, "RateIn"); ad.ViaUp = Num(n, "RateOut");
                    ad.Phone = Bool(n, "Phone"); ad.CoolMbps = Num(n, "CoolMbps"); ad.LimitMbps = Num(n, "LimitMbps");
                    if (ad.Phone) ad.Kind = "phone";
                }
                else
                {
                    ad.IP = Ipv4(ni); ad.State = ad.Online ? "up" : "down";
                    ad.Active = ad.Total = ad.Failed = 0; ad.ViaDown = ad.ViaUp = 0; ad.Weight = 0;
                    object lv; double lim;
                    ad.LimitMbps = limits != null && limits.TryGetValue(ni.Name, out lv) && double.TryParse(Convert.ToString(lv, System.Globalization.CultureInfo.InvariantCulture),
                        System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out lim) ? lim : 0;
                }
                object al; ad.Label = aliases != null && aliases.TryGetValue(ni.Name, out al) && al != null && al.ToString().Length > 0 ? al.ToString() : ni.Name;
                var u = cfg.Of(ni.Name);
                ad.DayIn = u.DayIn; ad.DayOut = u.DayOut; ad.MonthIn = u.MonthIn; ad.MonthOut = u.MonthOut;
                ad.Isp = s.IspNames ? Isp.Get(ni, ad.IP) : "";
                s.Adapters.Add(ad.Copy());
            }
            foreach (var k in adapters.Keys.Where(k => !seen.Contains(k)).ToList()) adapters.Remove(k);
            // сначала те, что net-merge использует, потом по имени
            s.Adapters.Sort((x, y) => x.Merged != y.Merged ? (x.Merged ? -1 : 1) : string.Compare(x.Name, y.Name, StringComparison.CurrentCultureIgnoreCase));
            s.Down = totalDown; s.Up = totalUp;
            if (dt > 0) { Push(histDown, (float)(totalDown * 8 / 1e6)); Push(histUp, (float)(totalUp * 8 / 1e6)); }
            Array.Copy(histDown, s.HistDown, Hist); Array.Copy(histUp, s.HistUp, Hist);

            s.AppsOn = !cfg.NoApps;
            if (Installed && ModuleOn && s.AppsOn) SampleApps(s);

            if (s.State != lastState) RecLog.Write("сеть: " + s.State + " (подключений с интернетом: " + s.Online + ")");
            lastState = s.State;
            if ((now - usageSaved).TotalSeconds > 60) { usageSaved = now; cfg.Save(); }
            lock (gate) snap = s;
        }

        // Программы: итоги помощника (net-apps.json) → скорость за прошедшее время, график, через что идёт, за день и месяц
        void SampleApps(Snapshot s)
        {
            var doc = ReadJson(NetMon.OutFile);
            var now = DateTime.UtcNow;
            long t = doc != null ? (long)Num(doc, "t") : 0;
            bool live = doc != null && Math.Abs((now - new DateTime(1970, 1, 1)).TotalMilliseconds - t) < 8000;
            s.AppsLive = live;
            if (!live) { foreach (var a in apps.Values) { a.Down = a.Up = 0; } }
            else
            {
                long pid = (long)Num(doc, "pid");
                if (pid != appMonPid) { appMonPid = pid; appLast.Clear(); viaLast.Clear(); appT = 0; }   // помощник перезапустился — счёт с нуля
                if (t == appT) goto Done;                       // новых итогов ещё нет — скорость прежняя
                double dt = appT == 0 ? 0 : (t - appT) / 1000.0;  // по часам помощника: ровно за то время, что он считал
                appT = t;
                // адреса подключений: какой адрес — какое подключение; адрес net-merge — «через объединение»
                var addrName = new Dictionary<string, string>();
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    bool tun = (ni.Name + " " + ni.Description).IndexOf("net-merge", StringComparison.OrdinalIgnoreCase) >= 0;
                    bool loop = ni.NetworkInterfaceType == NetworkInterfaceType.Loopback;
                    try { foreach (var u in ni.GetIPProperties().UnicastAddresses) addrName[u.Address.ToString()] = tun ? "merge" : loop ? "local" : ni.Name; } catch (Exception) { }
                }
                var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var ad in s.Adapters) labels[ad.Name] = ad.Label;
                object arr; doc.TryGetValue("apps", out arr);
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in AsList(arr))
                {
                    var d = o as Dictionary<string, object>; if (d == null) continue;
                    string name = Str(d, "n");
                    if (name.Equals("net-merge-tray", StringComparison.OrdinalIgnoreCase) || name.Equals("net-merge", StringComparison.OrdinalIgnoreCase)) continue;   // пересылает чужой трафик
                    long cin = (long)Num(d, "i"), cout = (long)Num(d, "o");
                    App a; if (!apps.TryGetValue(name, out a)) { a = new App { Name = name }; apps[name] = a; }
                    if (a.Path.Length == 0) a.Path = Str(d, "p");
                    long[] last; long dIn = 0, dOut = 0;
                    if (appLast.TryGetValue(name, out last)) { dIn = Math.Max(0, cin - last[0]); dOut = Math.Max(0, cout - last[1]); }
                    appLast[name] = new[] { cin, cout };
                    if (dIn + dOut > 0) cfg.AddApp(name, dIn, dOut);
                    a.Down = dt > 0.2 ? dIn / dt : 0; a.Up = dt > 0.2 ? dOut / dt : 0;
                    // через что: больше всего байт через какой адрес
                    var loc = Dict(d, "l"); string best = ""; double bestB = -1;
                    if (loc != null)
                        foreach (var kv in loc)
                        {
                            double b = 0;
                            foreach (var x in AsList(kv.Value)) b += Convert.ToDouble(x, System.Globalization.CultureInfo.InvariantCulture);
                            string via; if (!addrName.TryGetValue(kv.Key, out via)) via = kv.Key == "127.0.0.1" || kv.Key == "::1" ? "local" : "";
                            if (b > bestB && via.Length > 0) { bestB = b; best = via; }
                        }
                    string lab; a.Route = best == "merge" || best == "local" ? best : labels.TryGetValue(best, out lab) ? lab : best;
                    // через объединение: сколько ушло через каждое настоящее подключение (с прошлого замера)
                    var viaMap = Dict(d, "v");
                    Dictionary<string, long> vl; if (!viaLast.TryGetValue(name, out vl)) { vl = new Dictionary<string, long>(); viaLast[name] = vl; }
                    var split = new Dictionary<string, double>();
                    if (viaMap != null)
                        foreach (var kv in viaMap)
                        {
                            long b = 0; foreach (var x in AsList(kv.Value)) b += Convert.ToInt64(x, System.Globalization.CultureInfo.InvariantCulture);
                            long prev; vl.TryGetValue(kv.Key, out prev); vl[kv.Key] = b;
                            string adName; if (!addrName.TryGetValue(kv.Key, out adName) || adName == "merge") continue;
                            string l2; string shown = labels.TryGetValue(adName, out l2) ? l2 : adName;
                            double rate = dt > 0.2 ? Math.Max(0, b - prev) / dt : 0;
                            double cur; split.TryGetValue(shown, out cur); split[shown] = cur + rate;
                        }
                    a.Split = split.OrderByDescending(kv => kv.Value).ToList();
                    seen.Add(name);
                }
                foreach (var a in apps.Values) if (!seen.Contains(a.Name)) { a.Down = a.Up = 0; a.Split = new List<KeyValuePair<string, double>>(); }
            }
            Done:
            foreach (var a in apps.Values)
            {
                Push(a.Hist, (float)((a.Down + a.Up) * 8 / 1e6));
                var u = cfg.AppOf(a.Name);
                a.DayIn = u.DayIn; a.DayOut = u.DayOut; a.MonthIn = u.MonthIn; a.MonthOut = u.MonthOut;
            }
            // в список: кто сейчас качает или качал сегодня; сверху — самые быстрые сейчас, потом — больше всего за сегодня
            s.Apps = apps.Values.Where(a => a.Down + a.Up > 0 || a.DayIn + a.DayOut > 0)
                .OrderByDescending(a => a.Down + a.Up > 1000 ? a.Down + a.Up : 0).ThenByDescending(a => a.DayIn + a.DayOut)
                .Take(40).Select(a => new App { Name = a.Name, Path = a.Path, Route = a.Route, Down = a.Down, Up = a.Up, DayIn = a.DayIn, DayOut = a.DayOut,
                    MonthIn = a.MonthIn, MonthOut = a.MonthOut, Hist = (float[])a.Hist.Clone(), Split = a.Split }).ToList();
        }

        // Автозапуск и автопауза: модуль включён, не на паузе, разрешение есть —
        // работает, когда подключений с интернетом два и больше (или автопауза выключена), иначе ждёт.
        void Auto()
        {
            var s = Current;
            if (!Installed || !ModuleOn || !taskOk || updating) return;
            // помощник «Программы»: должен работать, пока модуль включён (и подсчёт не выключен)
            if (!cfg.NoApps && !NetMon.Alive && (DateTime.UtcNow - monStartAt).TotalSeconds > 30)
            {
                monStartAt = DateTime.UtcNow;
                RecLog.Write("сеть: запускаю помощник трафика программ");
                StartTask(Running ? false : !cfg.UserPaused && (cfg.NoAutoPause || s.Online >= 2), true);
            }
            if (cfg.UserPaused) return;
            bool want = cfg.NoAutoPause || s.Online >= 2;
            bool running = Running;
            if (want && !running && (DateTime.UtcNow - startedAt).TotalSeconds > 20) { RecLog.Write("сеть: запускаю net-merge"); StartTask(); }
            else if (!want && running && s.State == "on") { RecLog.Write("сеть: автопауза — подключений с интернетом " + s.Online); StopNow(false); }
        }

        static void Push(float[] a, float v) { Array.Copy(a, 1, a, 0, a.Length - 1); a[a.Length - 1] = v; }

        static bool Physical(NetworkInterface ni)
        {
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback || ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel) return false;
            string n = (ni.Name + " " + ni.Description).ToLowerInvariant();
            foreach (var bad in new[] { "net-merge", "virtual", "vmware", "hyper-v", "vethernet", "tap-", "wintun", "wireguard", "tailscale", "zerotier", "vpn", "bluetooth", "loopback", "pseudo", "teredo", "isatap", "npcap", "check point", "radmin", "hamachi" })
                if (n.Contains(bad)) return false;
            return ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet || ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                || ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet || ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT
                || ni.NetworkInterfaceType == NetworkInterfaceType.Ppp || ni.NetworkInterfaceType == NetworkInterfaceType.Wman
                || ni.NetworkInterfaceType == NetworkInterfaceType.Wwanpp || ni.NetworkInterfaceType == NetworkInterfaceType.Wwanpp2;
        }

        static bool HasGateway(NetworkInterface ni)
        {
            try { return ni.GetIPProperties().GatewayAddresses.Any(g => g.Address != null && !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)); }
            catch (Exception) { return false; }
        }

        static string KindOf(NetworkInterface ni)
        {
            string d = (ni.Description ?? "").ToLowerInvariant();
            if (d.Contains("remote ndis") || d.Contains("apple mobile") || d.Contains("iphone") || d.Contains("android")) return "phone";
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211) return "wifi";
            if (ni.NetworkInterfaceType == NetworkInterfaceType.Wwanpp || ni.NetworkInterfaceType == NetworkInterfaceType.Wwanpp2 || ni.NetworkInterfaceType == NetworkInterfaceType.Wman) return "mobile";
            return "ethernet";
        }

        static string Ipv4(NetworkInterface ni)
        {
            try
            {
                foreach (var u in ni.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork) return u.Address.ToString();
            }
            catch (Exception) { }
            return "";
        }

        // ── задача запуска «Qural net-merge» ──
        static string QuralExe { get { return Application.ExecutablePath; } }

        /// Задача есть и запускает именно этот Qural.exe.
        static bool TaskMatches()
        {
            string xml = Run("schtasks.exe", "/Query /TN " + Q(TaskName) + " /XML", 5000);
            if (!xml.Contains("<Task")) return false;
            string cmd = Between(xml, "<Command>", "</Command>") ?? "";
            return string.Equals(cmd.Trim('"', ' '), QuralExe, StringComparison.OrdinalIgnoreCase);
        }

        /// Создать задачу (окно UAC). XML — потому что из командной строки schtasks не снять ограничения «только от сети».
        string CreateTask()
        {
            string user = Environment.UserDomainName + "\\" + Environment.UserName;
            string xml = "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\n" +
                "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\n" +
                "  <RegistrationInfo><Description>Qural: объединение подключений (net-merge). Запускает Qural по команде, без окна UAC.</Description></RegistrationInfo>\n" +
                "  <Principals><Principal id=\"Author\"><UserId>" + Esc(user) + "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>\n" +
                "  <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><ExecutionTimeLimit>PT0S</ExecutionTimeLimit><AllowStartOnDemand>true</AllowStartOnDemand>" +
                "<Priority>5</Priority><StartWhenAvailable>false</StartWhenAvailable><IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd></IdleSettings></Settings>\n" +
                "  <Actions Context=\"Author\"><Exec><Command>" + Esc(QuralExe) + "</Command><Arguments>--net-run</Arguments></Exec></Actions>\n" +
                "</Task>\n";
            string tmp = Path.Combine(Path.GetTempPath(), "qural-net-task.xml");
            try { File.WriteAllText(tmp, xml, Encoding.Unicode); }
            catch (Exception ex) { return ex.Message; }
            string err = RunElevated("schtasks.exe", "/Create /F /TN " + Q(TaskName) + " /XML " + Q(tmp));
            try { File.Delete(tmp); } catch (Exception) { }
            taskOk = err == null && TaskMatches();
            taskChecked = DateTime.UtcNow;
            if (err == null && !taskOk) err = "Задача не создалась";
            if (err != null) RecLog.Write("сеть: задача — " + err); else RecLog.Write("сеть: задача «" + TaskName + "» создана");
            return err;
        }

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s) ?? ""; }

        /// Запустить задачу: она прочтёт, что нужно — net-merge и/или помощник «Программы».
        void StartTask() { StartTask(true, !cfg.NoApps); }
        void StartTask(bool netmerge, bool monitor)
        {
            try
            {
                Directory.CreateDirectory(NetMon.Folder);
                File.WriteAllText(WantFile, "netmerge=" + (netmerge ? 1 : 0) + "\nmonitor=" + (monitor ? 1 : 0) + "\n");
                if (monitor) try { File.Delete(NetMon.StopFile); } catch (Exception) { }
            }
            catch (Exception) { }
            if (netmerge) startedAt = DateTime.UtcNow;
            string r = Run("schtasks.exe", "/Run /TN " + Q(TaskName), 6000);
            if (r.ToLowerInvariant().Contains("error") || r.Contains("ОШИБКА")) { error = "Не удалось запустить: " + r.Trim(); RecLog.Write("сеть: " + error); }
            else error = "";
        }

        /// Остановить net-merge: метка off — он сам уберёт адаптер и маршруты и выйдет.
        void StopNow(bool wait)
        {
            try
            {
                string ctl = Path.Combine(Dir, "ctl");
                Directory.CreateDirectory(ctl);
                File.WriteAllBytes(Path.Combine(ctl, "off"), new byte[0]);
            }
            catch (Exception ex) { RecLog.Write("сеть: метка off — " + ex.Message); }
            if (wait) for (int i = 0; i < 60 && Running; i++) Thread.Sleep(250);
        }

        // ── команды (в фоновом потоке) ──
        /// Включить: нет разрешения — спросить (UAC) и создать задачу; потом запустить.
        public string Resume()
        {
            if (!Installed) return "Нет файлов net-merge в папке Qural — переустановите Qural";
            cfg.UserPaused = false; cfg.Save();
            if (!taskOk) { taskOk = TaskMatches(); if (!taskOk) { string err = CreateTask(); if (err != null) return err; } }
            StartTask();
            Soon(1500);
            return null;
        }

        public string Pause()
        {
            cfg.UserPaused = true; cfg.Save();
            StopNow(false);
            Soon(1500);
            return null;
        }

        public string Toggle()
        {
            var s = Current;
            if (s.State == "missing") return "Нет файлов net-merge в папке Qural — переустановите Qural";
            if (s.State == "off") return "«Сеть» выключена — включите её в Qural";
            return s.State == "on" || s.State == "starting" ? Pause() : Resume();
        }

        public string SetOn(bool on) { return on ? Resume() : Pause(); }

        /// Модуль выключили — остановить; включили — запустить (если разрешение уже есть).
        public void ModuleChanged(bool on)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (!on) { StopMonitor(); if (Running) StopNow(true); }
                else if (taskOk && !cfg.UserPaused) StartTask();
                Soon(800);
            });
        }

        static void StopMonitor() { try { Directory.CreateDirectory(NetMon.Folder); File.WriteAllText(NetMon.StopFile, ""); } catch (Exception) { } }

        /// Перезапуск — net-merge перечитает настройки, которые читает только при запуске.
        string Restart()
        {
            if (!Running) return null;
            StopNow(true);
            StartTask();
            Soon(1500);
            return null;
        }

        public string SetLimit(string adapter, double mbps)
        {
            if (!Installed) return "Нет файлов net-merge";
            string r = Run(Exe, "limit " + Q(adapter) + " " + (mbps <= 0 ? "0" : mbps.ToString(System.Globalization.CultureInfo.InvariantCulture)), 6000);
            Soon();
            return r.ToLowerInvariant().Contains("error") ? r.Trim() : null;
        }

        public string SetAlias(string adapter, string alias)
        {
            if (!Installed) return "Нет файлов net-merge";
            string r = Run(Exe, "alias " + Q(adapter) + (string.IsNullOrWhiteSpace(alias) ? "" : " " + Q(alias.Trim())), 6000);
            Soon();
            return r.ToLowerInvariant().Contains("error") ? r.Trim() : null;
        }

        /// field: isp | game | cool | updates | useAll | autoPause
        public string Set(string field, bool on)
        {
            RecLog.Write("сеть: настройка " + field + " = " + on);
            switch (field)
            {
                case "isp": cfg.NoIsp = !on; cfg.Save(); Soon(); return null;
                case "updates": cfg.NoUpdateCheck = !on; cfg.Save(); if (on) updateChecked = DateTime.MinValue; Soon(); return null;
                case "autoPause": cfg.NoAutoPause = !on; cfg.Save(); Soon(); return null;
                case "apps":
                    cfg.NoApps = !on; cfg.Save();
                    if (!on) StopMonitor(); else monStartAt = DateTime.MinValue;
                    Soon(); return null;
                case "useAll": cfg.Ipv6 = !on; cfg.Save(); return Restart();
                case "game": case "cool":
                    {
                        string err = WriteSetting(field == "game" ? "game_mode" : "phone_cooling", on);
                        return err ?? Restart();
                    }
            }
            return null;
        }

        string WriteSetting(string key, bool value)
        {
            string path = Path.Combine(Dir, "settings.json");
            var d = ReadJson(path) ?? new Dictionary<string, object>();
            d["version"] = 1;
            foreach (var k in new[] { "phone_cooling", "game_mode", "no_update_check", "hide_speed" }) if (!d.ContainsKey(k)) d[k] = false;
            d[key] = value;
            try
            {
                Directory.CreateDirectory(Dir);
                var js = new JavaScriptSerializer();
                var sb = new StringBuilder("{\n");
                var keys = d.Keys.ToList();
                for (int i = 0; i < keys.Count; i++)
                    sb.Append("  ").Append(js.Serialize(keys[i])).Append(": ").Append(js.Serialize(d[keys[i]])).Append(i < keys.Count - 1 ? ",\n" : "\n");
                sb.Append("}\n");
                File.WriteAllText(path + ".tmp", sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(path + ".tmp", path, null); else File.Move(path + ".tmp", path);
                return null;
            }
            catch (Exception ex) { return "Не удалось сохранить настройку: " + ex.Message; }
        }

        void Soon(int ms = 600) { ThreadPool.QueueUserWorkItem(_ => { Thread.Sleep(ms); Tick(); }); }

        // ── запуск net-merge из задачи: Qural.exe --net-run (уже с правами администратора) ──
        /// Запустить net-merge без окна с параметрами из настроек Qural и выйти.
        public static void RunFromTask()
        {
            try
            {
                bool wantNet = true, wantMon = false;
                try
                {
                    foreach (var line in File.ReadAllLines(WantFile))
                    {
                        if (line.StartsWith("netmerge=")) wantNet = line.EndsWith("1");
                        if (line.StartsWith("monitor=")) wantMon = line.EndsWith("1");
                    }
                }
                catch (Exception) { }
                if (wantMon && !NetMon.Alive)
                    try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--net-mon") { UseShellExecute = false, CreateNoWindow = true }); } catch (Exception) { }
                if (!wantNet || !File.Exists(RunExe)) return;
                if (Process.GetProcessesByName("net-merge-tray").Length > 0) return;   // уже работает
                var cfg = NetUsage.Load();
                Directory.CreateDirectory(Dir);
                try { File.Delete(Path.Combine(Path.Combine(Dir, "ctl"), "off")); } catch (Exception) { }   // старая метка остановила бы сразу
                string log = Path.Combine(Dir, "qural-run.log");
                try { if (File.Exists(log) && new FileInfo(log).Length > 2 * 1024 * 1024) File.Delete(log); } catch (Exception) { }
                string args = "run -stats 0 -log " + Q(log) + (cfg.Ipv6 ? " -ipv6 on" : "");
                Process.Start(new ProcessStartInfo(RunExe, args) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Folder });
            }
            catch (Exception ex) { try { RecLog.Write("сеть: --net-run — " + ex.Message); } catch (Exception) { } }
        }

        // ── обновления net-merge ──
        /// Раз в 6 часов: есть ли новая версия на GitHub (в фоне).
        public void CheckUpdate(bool force)
        {
            if (!Installed || (cfg.NoUpdateCheck && !force)) return;
            if (!force && (DateTime.UtcNow - updateChecked).TotalHours < 6) return;
            updateChecked = DateTime.UtcNow;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var rel = LatestRelease();
                    Version cur, got;
                    update = rel != null && Version.TryParse(FilesVersion, out cur) && Version.TryParse(rel.Item1, out got) && got > cur ? rel.Item1 : "";
                    if (update.Length > 0) RecLog.Write("сеть: есть net-merge " + update);
                }
                catch (Exception ex) { RecLog.Write("сеть: проверка обновлений — " + ex.Message); }
                Soon(10);
            });
        }

        /// (версия, ссылка на переносной zip, sha256)
        static Tuple<string, string, string> LatestRelease()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            string json;
            using (var w = new WebClient())
            {
                w.Headers[HttpRequestHeader.UserAgent] = "Qural";
                w.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                json = w.DownloadString("https://api.github.com/repos/net-merge/net-merge/releases/latest");
            }
            var m = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (m == null) return null;
            string ver = Str(m, "tag_name").TrimStart('v');
            object a; if (!m.TryGetValue("assets", out a)) return null;
            foreach (var o in AsList(a))
            {
                var d = o as Dictionary<string, object>; if (d == null) continue;
                string name = Str(d, "name");
                if (!name.EndsWith("-portable.zip", StringComparison.OrdinalIgnoreCase) || !name.Contains(ver)) continue;
                string dg = Str(d, "digest");
                if (!dg.StartsWith("sha256:")) return null;   // без контрольной суммы не ставим
                return Tuple.Create(ver, Str(d, "browser_download_url"), dg.Substring(7).ToLowerInvariant());
            }
            return null;
        }

        /// Скачать новую версию, сверить SHA-256, остановить, заменить файлы, запустить снова.
        public string InstallUpdate()
        {
            if (updating) return null;
            updating = true; Soon(10);
            try
            {
                var rel = LatestRelease();
                if (rel == null) return "Не нашёл переносную сборку net-merge на GitHub";
                string tmp = Path.Combine(Path.GetTempPath(), "qural-net-merge-" + rel.Item1);
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                string zip = Path.Combine(tmp, "nm.zip");
                using (var w = new WebClient()) { w.Headers[HttpRequestHeader.UserAgent] = "Qural"; w.DownloadFile(rel.Item2, zip); }
                string sum;
                using (var f = File.OpenRead(zip)) using (var h = SHA256.Create()) sum = BitConverter.ToString(h.ComputeHash(f)).Replace("-", "").ToLowerInvariant();
                if (sum != rel.Item3) return "Контрольная сумма не совпала — обновление не поставлено";
                string x = Path.Combine(tmp, "x");
                ZipFile.ExtractToDirectory(zip, x);
                bool was = Running;
                if (was) StopNow(true);
                foreach (var f in Directory.GetFiles(x, "*", SearchOption.AllDirectories))
                    File.Copy(f, Path.Combine(Folder, Path.GetFileName(f)), true);
                File.WriteAllText(Path.Combine(Folder, "VERSION"), rel.Item1);
                try { Directory.Delete(tmp, true); } catch (Exception) { }
                update = "";
                RecLog.Write("сеть: net-merge обновлён до " + rel.Item1);
                if (was) StartTask();
                return null;
            }
            catch (Exception ex) { return "Обновление не удалось: " + ex.Message; }
            finally { updating = false; Soon(1000); }
        }

        // ── мелочи ──
        static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "\\\"") + "\""; }

        static string Run(string exe, string args, int timeoutMs)
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
                };
                using (var p = Process.Start(psi))
                {
                    var err = p.StandardError.ReadToEndAsync();
                    string o = p.StandardOutput.ReadToEnd();
                    p.WaitForExit(timeoutMs);
                    return o + err.Result;
                }
            }
            catch (Exception ex) { return "error: " + ex.Message; }
        }

        static string RunElevated(string exe, string args)
        {
            try
            {
                using (var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden }))
                {
                    p.WaitForExit(30000);
                    return p.ExitCode == 0 ? null : "Windows не дала создать задачу (код " + p.ExitCode + ")";
                }
            }
            catch (System.ComponentModel.Win32Exception) { return "Нужно разрешение администратора — окно UAC закрыли"; }
            catch (Exception ex) { return ex.Message; }
        }

        static string Between(string s, string a, string b)
        {
            int i = s.IndexOf(a, StringComparison.Ordinal); if (i < 0) return null;
            int j = s.IndexOf(b, i + a.Length, StringComparison.Ordinal); if (j < 0) return null;
            return WebUtility.HtmlDecode(s.Substring(i + a.Length, j - i - a.Length));
        }

        static Dictionary<string, object> ReadJson(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text;
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var r = new StreamReader(fs, Encoding.UTF8)) text = r.ReadToEnd();
                return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.DeserializeObject(text) as Dictionary<string, object>;
            }
            catch (Exception) { return null; }
        }

        static Dictionary<string, object> Dict(Dictionary<string, object> m, string k)
        {
            object v; return m != null && m.TryGetValue(k, out v) ? v as Dictionary<string, object> : null;
        }
        static IEnumerable<object> AsList(object v)
        {
            var arr = v as object[]; if (arr != null) return arr;
            var al = v as System.Collections.ArrayList; if (al != null) return al.ToArray();
            return new object[0];
        }
        static string Str(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
        static bool Bool(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) && v is bool && (bool)v; }
        static double Num(Dictionary<string, object> m, string k)
        {
            object v; if (!m.TryGetValue(k, out v) || v == null) return 0;
            try { return Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture); } catch (Exception) { return 0; }
        }
        static DateTime Date(Dictionary<string, object> m, string k)
        {
            DateTime d; return DateTime.TryParse(Str(m, k), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeLocal, out d) ? d : DateTime.MinValue;
        }

        /// Qural закрывается: net-merge оставляем работать (интернет без него тоже работает); сохранить итоги.
        public void Dispose()
        {
            Speed.Stop();
            disposed = true;
            try { timer.Dispose(); } catch (Exception) { }
            cfg.Save();
        }
    }

    static class NetText
    {
        /// Мбит/с из байт/с: до 10 — с одной цифрой после запятой, дальше — целые.
        public static string Mbps(double bytesPerSec)
        {
            double v = bytesPerSec * 8 / 1e6;
            return v < 10 ? v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : Math.Round(v).ToString("0");
        }
    }

    // Настройки «Сети» и трафик подключений за сегодня и за месяц — %APPDATA%\Qural\net.json.
    // Настройки хранятся «наоборот» (No…), чтобы у нового файла всё было включено.
    [DataContract]
    sealed class NetUsage
    {
        [DataContract]
        public sealed class Item
        {
            [DataMember] public long DayIn, DayOut, MonthIn, MonthOut;
        }
        [DataMember] public string Day = "", Month = "";
        [DataMember] public Dictionary<string, Item> Adapters = new Dictionary<string, Item>();
        [DataMember] public bool UserPaused;       // пауза вручную: не запускать самим
        [DataMember] public bool Ipv6;             // «Использовать все подключения» выключено (-ipv6 on)
        [DataMember] public bool NoAutoPause, NoIsp, NoUpdateCheck, NoApps;
        [DataMember] public Dictionary<string, Item> Apps = new Dictionary<string, Item>();   // трафик программ за день и месяц

        static readonly object gate = new object();   // поле экземпляра не годится: при чтении из файла конструктор не вызывается

        public static NetUsage Load()
        {
            var u = Store.Load<NetUsage>("net.json", null) ?? new NetUsage();
            if (u.Adapters == null) u.Adapters = new Dictionary<string, Item>();
            if (u.Apps == null) u.Apps = new Dictionary<string, Item>();
            if (u.Day == null) u.Day = "";
            if (u.Month == null) u.Month = "";
            return u;
        }

        public void Save() { lock (gate) Store.Save("net.json", this, false); }

        void Roll()
        {
            string day = DateTime.Now.ToString("yyyy-MM-dd"), month = DateTime.Now.ToString("yyyy-MM");
            if (Month != month)
            {
                foreach (var i in Adapters.Values.Concat(Apps.Values)) { i.MonthIn = i.MonthOut = 0; }
                Month = month;
                Apps.Clear();   // программы прошлого месяца не нужны — файл не растёт
            }
            if (Day != day) { foreach (var i in Adapters.Values.Concat(Apps.Values)) { i.DayIn = i.DayOut = 0; } Day = day; }
        }

        public void Add(string name, long inBytes, long outBytes)
        {
            lock (gate)
            {
                Roll();
                Item it; if (!Adapters.TryGetValue(name, out it)) { it = new Item(); Adapters[name] = it; }
                it.DayIn += inBytes; it.DayOut += outBytes; it.MonthIn += inBytes; it.MonthOut += outBytes;
            }
        }

        public void AddApp(string name, long inBytes, long outBytes)
        {
            lock (gate)
            {
                Roll();
                Item it; if (!Apps.TryGetValue(name, out it)) { it = new Item(); Apps[name] = it; }
                it.DayIn += inBytes; it.DayOut += outBytes; it.MonthIn += inBytes; it.MonthOut += outBytes;
            }
        }

        public Item AppOf(string name)
        {
            lock (gate)
            {
                Roll();
                Item it; return Apps.TryGetValue(name, out it) ? new Item { DayIn = it.DayIn, DayOut = it.DayOut, MonthIn = it.MonthIn, MonthOut = it.MonthOut } : new Item();
            }
        }

        public Item Of(string name)
        {
            lock (gate)
            {
                Roll();
                Item it; return Adapters.TryGetValue(name, out it) ? new Item { DayIn = it.DayIn, DayOut = it.DayOut, MonthIn = it.MonthIn, MonthOut = it.MonthOut } : new Item();
            }
        }
    }

    // Провайдер подключения: запрос к ipwho.is именно через это подключение (IP_UNICAST_IF — мимо net-merge),
    // сервис видит публичный IP подключения. Один раз на адрес; выключается в настройках «Сети».
    static class Isp
    {
        static readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        static readonly HashSet<string> pending = new HashSet<string>();

        public static string Get(NetworkInterface ni, string ip)
        {
            if (string.IsNullOrEmpty(ip)) return "";
            string key = ni.Name + "|" + ip;
            lock (cache)
            {
                string v; if (cache.TryGetValue(key, out v)) return v;
                if (pending.Contains(key)) return "";
                pending.Add(key);
            }
            int index = 0;
            try { index = ni.GetIPProperties().GetIPv4Properties().Index; } catch (Exception) { }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string name = "";
                try { name = Lookup(index, ip); } catch (Exception) { }
                lock (cache) { cache[key] = name; pending.Remove(key); }
            });
            return "";
        }

        static string Lookup(int ifIndex, string ip)
        {
            string body = Https("ipwho.is", "/", ifIndex, ip);
            var m = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>;
            object c; Dictionary<string, object> conn = m != null && m.TryGetValue("connection", out c) ? c as Dictionary<string, object> : null;
            object isp; if (conn != null && conn.TryGetValue("isp", out isp) && isp != null && isp.ToString().Length > 0) return isp.ToString();
            if (conn != null && conn.TryGetValue("org", out isp) && isp != null) return isp.ToString();
            body = Https("ipinfo.io", "/json", ifIndex, ip);
            m = new JavaScriptSerializer().DeserializeObject(body) as Dictionary<string, object>;
            object org; if (m != null && m.TryGetValue("org", out org) && org != null)
            {
                string s = org.ToString();
                if (s.StartsWith("AS")) { int sp = s.IndexOf(' '); if (sp > 0) s = s.Substring(sp + 1); }
                return s;
            }
            return "";
        }

        // HTTPS-запрос через конкретное подключение (без библиотек: сокет с IP_UNICAST_IF + SslStream)
        static string Https(string host, string path, int ifIndex, string localIp)
        {
            var addr = Dns.GetHostAddresses(host).First(a => a.AddressFamily == AddressFamily.InterNetwork);
            using (var sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                if (ifIndex > 0) sock.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31 /*IP_UNICAST_IF*/, BitConverter.GetBytes(IPAddress.HostToNetworkOrder(ifIndex)));
                sock.Bind(new IPEndPoint(IPAddress.Parse(localIp), 0));
                sock.ReceiveTimeout = sock.SendTimeout = 8000;
                var ar = sock.BeginConnect(addr, 443, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(8000)) throw new TimeoutException();
                sock.EndConnect(ar);
                using (var ns = new NetworkStream(sock, false))
                using (var ssl = new SslStream(ns, false))
                {
                    ssl.AuthenticateAsClient(host, null, (System.Security.Authentication.SslProtocols)3072 /*TLS 1.2*/, true);
                    byte[] req = Encoding.ASCII.GetBytes("GET " + path + " HTTP/1.1\r\nHost: " + host + "\r\nUser-Agent: Qural\r\nAccept: application/json\r\nConnection: close\r\n\r\n");
                    ssl.Write(req);
                    var ms = new MemoryStream();
                    ssl.CopyTo(ms);
                    string resp = Encoding.UTF8.GetString(ms.ToArray());
                    int b = resp.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    string body = b >= 0 ? resp.Substring(b + 4) : resp;
                    if (resp.IndexOf("Transfer-Encoding: chunked", StringComparison.OrdinalIgnoreCase) >= 0) body = Dechunk(body);
                    return body;
                }
            }
        }

        static string Dechunk(string s)
        {
            var sb = new StringBuilder(); int i = 0;
            while (i < s.Length)
            {
                int nl = s.IndexOf("\r\n", i, StringComparison.Ordinal); if (nl < 0) break;
                int n; if (!int.TryParse(s.Substring(i, nl - i).Split(';')[0].Trim(), System.Globalization.NumberStyles.HexNumber, null, out n) || n == 0) break;
                sb.Append(s, nl + 2, Math.Min(n, s.Length - nl - 2)); i = nl + 2 + n + 2;
            }
            return sb.ToString();
        }
    }
}
