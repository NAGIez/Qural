using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.NetworkInformation;

namespace Qural
{
    // Вкладка «Сеть»: все подключения сразу (внутри — net-merge). Пока вкладка открыта, данные идут раз в секунду.
    partial class MainWindow
    {
        bool netWatching;
        readonly HashSet<string> netIcons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // значки программ, уже отправленные странице

        /// Открыта вкладка «Сеть» — опрашивать раз в секунду; ушли с неё или закрыли окно — перестать.
        void NetWatch(bool on)
        {
            if (on == netWatching) return;
            netWatching = on;
            if (on) { netIcons.Clear(); app.Net.Watch(); app.Net.Speed.Changed += OnSpeed; SendNet(); SendSpeed(); }
            else { app.Net.Unwatch(); app.Net.Speed.Changed -= OnSpeed; }
        }

        void OnNet(Dictionary<string, object> m)
        {
            string type = Str(m, "type");
            string name = Str(m, "name");
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                switch (type)
                {
                    case "toggle": err = app.Net.Toggle(); break;
                    case "resume": err = app.Net.Resume(); break;
                    case "pause": err = app.Net.Pause(); break;
                    case "limit": err = app.Net.SetLimit(name, Num(m, "mbps")); break;
                    case "alias": err = app.Net.SetAlias(name, Str(m, "alias")); break;
                    case "set": err = app.Net.Set(Str(m, "field"), Bool(m, "value")); break;
                    case "update": err = app.Net.InstallUpdate(); break;
                    case "checkUpdate": app.Net.CheckUpdate(true); break;
                    case "speed": err = StartSpeed(); break;
                    case "speedStop": app.Net.Speed.Stop(); break;
                    case "source":
                        try { System.Diagnostics.Process.Start("https://github.com/net-merge/net-merge"); } catch (Exception) { }
                        break;
                }
                if (err != null) app.Invoke(() => Toast("net", err, false));
            });
        }

        public void NetChanged() { if (netWatching) SendNet(); }

        // ── замер скорости ──
        // каждое подключение с интернетом — отдельно (мимо объединения), потом «Все вместе», если объединение работает
        string StartSpeed()
        {
            var s = app.Net.Current;
            var rows = new List<NetSpeed.Row>();
            NetworkInterface[] nis;
            try { nis = NetworkInterface.GetAllNetworkInterfaces(); } catch (Exception) { nis = new NetworkInterface[0]; }
            foreach (var a in s.Adapters)
            {
                if (!a.Present || !a.Online || string.IsNullOrEmpty(a.IP)) continue;
                int index = 0;
                foreach (var ni in nis)
                {
                    try
                    {
                        if (ni.OperationalStatus != OperationalStatus.Up) continue;
                        if (!ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.ToString() == a.IP)) continue;
                        index = ni.GetIPProperties().GetIPv4Properties().Index; break;
                    }
                    catch (Exception) { }
                }
                if (index == 0) continue;
                rows.Add(new NetSpeed.Row { Key = a.Name, Label = a.Label.Length > 0 ? a.Label : a.Name, Kind = a.Phone ? "phone" : a.Kind, Ip = a.IP, IfIndex = index, Streams = 6 });
            }
            if (s.State == "on" && rows.Count >= 2)
                rows.Add(new NetSpeed.Row { Key = "*", Label = "Все вместе", Kind = "merge", Streams = 6 * rows.Count });
            if (rows.Count == 0) return "Нет подключений с интернетом";
            if (!app.Net.Speed.Start(rows)) return "Замер уже идёт";
            return null;
        }

        DateTime speedSent;
        void OnSpeed()
        {
            var sp = app.Net.Speed;
            // стрелке хватает 5 раз в секунду; начало и конец этапов — всегда
            if (sp.Running && (DateTime.UtcNow - speedSent).TotalMilliseconds < 180 && sp.Progress > 0 && sp.Progress < 1) return;
            speedSent = DateTime.UtcNow;
            app.Invoke(SendSpeed);
        }

        void SendSpeed()
        {
            if (!ready || !netWatching) return;
            var sp = app.Net.Speed;
            Post("net", new
            {
                type = "speed",
                running = sp.Running, phase = sp.Phase, current = sp.Current, mbps = Math.Round(sp.Mbps, 1), progress = sp.Progress,
                at = sp.At == DateTime.MinValue ? "" : sp.At.ToString("HH:mm"),
                rows = sp.Rows.Select(r => new { key = r.Key, label = r.Label, kind = r.Kind, ping = r.Ping, jitter = r.Jitter, server = r.Server, down = r.Down, up = r.Up, state = r.State, error = r.Error }).ToArray()
            });
        }

        static string AppTitle(NetMerge.App a)
        {
            if (a.Name.Equals("System", StringComparison.OrdinalIgnoreCase)) return "Windows (система)";
            try { return TitleFor(a.Name, a.Path); } catch (Exception) { return a.Name; }
        }

        void SendNet()
        {
            if (!ready) return;
            var s = app.Net.Current;
            Post("net", new
            {
                type = "state",
                state = new
                {
                    state = s.State,
                    version = s.Version, update = s.Update, updating = s.Updating, error = s.Error, online = s.Online,
                    since = s.Since == DateTime.MinValue ? 0 : Math.Max(0, (long)(DateTime.Now - s.Since).TotalSeconds),
                    v6 = s.V6,
                    down = s.Down, up = s.Up,
                    histDown = s.HistDown, histUp = s.HistUp,
                    isp = s.IspNames, game = s.GameMode, cool = s.PhoneCooling, updates = s.Updates, useAll = s.UseAll, autoPause = s.AutoPause,
                    apps = s.AppsOn, appsLive = s.AppsLive,
                    appList = s.Apps.Select(a => new
                    {
                        name = a.Name, title = AppTitle(a), route = a.Route, down = a.Down, up = a.Up, hist = a.Hist,
                        split = a.Split.Select(kv => new { name = kv.Key, rate = kv.Value }).ToArray(),
                        dayIn = a.DayIn, dayOut = a.DayOut, monthIn = a.MonthIn, monthOut = a.MonthOut,
                        icon = netIcons.Add(a.Name) ? IconFor(a.Name, a.Path) : null   // значок — один раз, дальше страница помнит
                    }).ToArray(),
                    adapters = s.Adapters.Select(a => new
                    {
                        name = a.Name, label = a.Label, isp = a.Isp, ip = a.IP, ip6 = a.IP6, state = a.State, state6 = a.State6, kind = a.Kind,
                        present = a.Present, online = a.Online, phone = a.Phone, merged = a.Merged, weight = a.Weight, limit = a.LimitMbps, cool = a.CoolMbps,
                        active = a.Active, total = a.Total, failed = a.Failed,
                        down = a.Down, up = a.Up, viaDown = a.ViaDown, viaUp = a.ViaUp,
                        dayIn = a.DayIn, dayOut = a.DayOut, monthIn = a.MonthIn, monthOut = a.MonthOut,
                        histDown = a.HistDown, histUp = a.HistUp
                    }).ToArray()
                }
            });
        }
    }
}
