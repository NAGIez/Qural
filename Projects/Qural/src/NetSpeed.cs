using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Qural
{
    // «Сеть» → «Скорость»: замер по методу Speedtest — ближайшие серверы Speedtest (Ookla), из них — с наименьшим пингом
    // через это подключение; несколько соединений сразу; скорость снимается 5 раз в секунду, в итог не идут
    // 30% самых медленных и 10% самых быстрых отсчётов (разгон TCP и всплески). Нет списка серверов — Cloudflare.
    // Подключение — сокеты с IP_UNICAST_IF (мимо объединения); «Все вместе» — без привязки, через объединение,
    // соединения раскладываются на несколько ближайших серверов.
    sealed class NetSpeed
    {
        public sealed class Row
        {
            public string Key = "", Label = "", Kind = "other", Ip = "";
            public int IfIndex;                     // 0 — без привязки (все вместе)
            public int Streams = 6;
            public double Ping = -1, Jitter = -1, Down = -1, Up = -1;   // мс, Мбит/с; -1 — ещё не мерили
            public string Server = "";              // где мерили: «RB-K, Almaty»
            public string State = "wait", Error = "";      // wait | run | done | error | skip
            public Row Copy() { return (Row)MemberwiseClone(); }
        }

        // сервер замера: Speedtest (порт 8080, /download, /upload) или Cloudflare
        sealed class Server
        {
            public string Host = "", Name = "";
            public int Port = 443;
            public bool Ookla;
            public IPAddress Addr;
            public double Ping = -1;
            public string DownPath() { return Ookla ? "/download?nocache=" + Guid.NewGuid().ToString("N") + "&size=25000000" : "/__down?bytes=25000000"; }
            public string UpPath() { return Ookla ? "/upload?nocache=" + Guid.NewGuid().ToString("N") : "/__up"; }
        }

        const double DownSec = 10, UpSec = 8, Warm = 1.0;

        readonly object gate = new object();
        List<Row> rows = new List<Row>();
        volatile bool stop;
        public bool Running { get; private set; }
        public string Phase = "";          // servers | ping | down | up | done | stopped
        public string Current = "";        // Key строки, которую меряем
        public double Mbps, Progress;      // живая скорость для стрелки; доля текущего этапа
        public DateTime At;                // когда закончился последний замер
        public event Action Changed;

        /// Копия строк для страницы.
        public List<Row> Rows { get { lock (gate) return rows.Select(r => r.Copy()).ToList(); } }

        public bool Start(List<Row> targets)
        {
            lock (gate)
            {
                if (Running || targets.Count == 0) return false;
                rows = targets; stop = false; Running = true; Phase = "servers"; Current = ""; Mbps = 0; Progress = 0;
            }
            var t = new Thread(Run) { IsBackground = true, Name = "Qural speed test" };
            t.Start();
            Raise();
            return true;
        }

        public void Stop() { stop = true; }

        void Raise() { var h = Changed; if (h != null) try { h(); } catch (Exception) { } }

        void Run()
        {
            var servers = OoklaServers();
            if (servers.Count == 0) servers = CloudflareServer();
            var chosen = new List<Server>();      // что выбрали подключения — для «Все вместе»
            foreach (var r in rows)
            {
                if (stop) break;
                Current = r.Key;
                if (servers.Count == 0) { r.State = "error"; r.Error = "нет серверов замера"; continue; }
                r.State = "run";
                try
                {
                    Phase = "ping"; Mbps = 0; Progress = 0; Raise();
                    List<Server> use;
                    if (r.IfIndex > 0)
                    {
                        var best = Pick(r, servers);
                        if (best == null) throw new IOException("нет ответа");
                        use = new List<Server> { best };
                        chosen.Add(best);
                        r.Server = best.Name;
                    }
                    else
                    {
                        // через объединение: соединение принимает само объединение, пинг не настоящий — не показываем;
                        // потоки — на 2–3 лучших сервера подключений
                        use = chosen.Distinct().ToList();
                        foreach (var s in servers) { if (use.Count >= 3) break; if (!use.Contains(s)) use.Add(s); }
                        r.Server = string.Join(" + ", use.Select(s => s.Name.Split(',')[0]).ToArray());
                    }
                    if (stop) break;
                    Phase = "down"; Raise();
                    r.Down = Measure(r, use, true);
                    Mbps = 0; Raise();
                    if (stop) break;
                    Phase = "up"; Raise();
                    r.Up = Measure(r, use, false);
                    r.State = r.Down > 0 || r.Up > 0 ? "done" : "error";
                    if (r.State == "error" && r.Error.Length == 0) r.Error = "не удалось";
                }
                catch (Exception e) { r.State = "error"; r.Error = Short(e); }
                Mbps = 0; Raise();
            }
            foreach (var r in rows) if (r.State == "wait" || r.State == "run") r.State = "skip";
            Phase = stop ? "stopped" : "done"; Current = ""; Mbps = 0; Progress = 0;
            At = DateTime.Now; Running = false;
            Raise();
        }

        // ── серверы ──
        // ближайшие серверы Speedtest (как в приложении Speedtest): список по расстоянию
        static List<Server> OoklaServers()
        {
            var list = new List<Server>();
            try
            {
                var req = (HttpWebRequest)WebRequest.Create("https://www.speedtest.net/api/js/servers?engine=js&https_functional=true&limit=10");
                req.UserAgent = "Qural"; req.Timeout = 8000;
                string json;
                using (var resp = req.GetResponse()) using (var sr = new StreamReader(resp.GetResponseStream())) json = sr.ReadToEnd();
                var arr = new JavaScriptSerializer().DeserializeObject(json) as object[];
                if (arr == null) return list;
                foreach (var o in arr)
                {
                    var d = o as Dictionary<string, object>; if (d == null) continue;
                    string host = d.ContainsKey("host") ? Convert.ToString(d["host"]) : "";
                    int colon = host.LastIndexOf(':'); int port = 8080;
                    if (colon > 0) { int.TryParse(host.Substring(colon + 1), out port); host = host.Substring(0, colon); }
                    if (host.Length == 0) continue;
                    string name = (d.ContainsKey("sponsor") ? Convert.ToString(d["sponsor"]) : "") + ", " + (d.ContainsKey("name") ? Convert.ToString(d["name"]) : "");
                    list.Add(new Server { Host = host, Port = port, Name = name.Trim(' ', ','), Ookla = true });
                }
            }
            catch (Exception) { }
            foreach (var s in list) { try { s.Addr = Dns.GetHostAddresses(s.Host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); } catch (Exception) { } }
            return list.Where(s => s.Addr != null).ToList();
        }

        static List<Server> CloudflareServer()
        {
            var s = new Server { Host = "speed.cloudflare.com", Port = 443, Name = "Cloudflare" };
            try { s.Addr = Dns.GetHostAddresses(s.Host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork); } catch (Exception) { }
            return s.Addr != null ? new List<Server> { s } : new List<Server>();
        }

        // сервер с наименьшим пингом через это подключение (из 5 ближайших), потом — точный пинг и разброс до него
        Server Pick(Row r, List<Server> servers)
        {
            Server best = null; double bestMs = double.MaxValue;
            var cand = servers.Take(5).ToList();
            for (int i = 0; i < cand.Count && !stop; i++)
            {
                double ms = Rtt(r, cand[i], 2);
                if (ms >= 0 && ms < bestMs) { bestMs = ms; best = cand[i]; }
                Progress = (i + 1) / (cand.Count + 1.0); Raise();
            }
            if (best == null || stop) return best;
            // точный пинг: 10 рукопожатий — меньшее значение и разброс (джиттер), как в Speedtest
            var samples = new List<double>();
            for (int i = 0; i < 10 && !stop; i++) { double ms = Rtt(r, best, 1); if (ms >= 0) samples.Add(ms); }
            if (samples.Count > 0)
            {
                r.Ping = Math.Round(samples.Min());
                double j = 0; for (int i = 1; i < samples.Count; i++) j += Math.Abs(samples[i] - samples[i - 1]);
                r.Jitter = samples.Count > 1 ? Math.Round(j / (samples.Count - 1), 1) : 0;
            }
            Progress = 1; Raise();
            return best;
        }

        double Rtt(Row r, Server s, int tries)
        {
            double best = -1;
            for (int i = 0; i < tries && !stop; i++)
            {
                try
                {
                    using (var sock = NewSocket(r))
                    {
                        var sw = Stopwatch.StartNew();
                        Connect(sock, s, 2000);
                        double ms = sw.Elapsed.TotalMilliseconds;
                        if (best < 0 || ms < best) best = ms;
                    }
                }
                catch (Exception) { }
            }
            return best;
        }

        static string Short(Exception e)
        {
            var se = e as SocketException ?? e.InnerException as SocketException;
            if (se != null) return se.SocketErrorCode == SocketError.TimedOut ? "нет ответа" : se.SocketErrorCode == SocketError.NetworkUnreachable ? "нет сети" : "ошибка сети";
            if (e is TimeoutException) return "нет ответа";
            return e is IOException && e.Message.Length > 0 && e.Message.Length < 40 ? e.Message : "ошибка";
        }

        Socket NewSocket(Row r)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            if (r.IfIndex > 0)
            {
                s.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31 /*IP_UNICAST_IF*/, BitConverter.GetBytes(IPAddress.HostToNetworkOrder(r.IfIndex)));
                s.Bind(new IPEndPoint(IPAddress.Parse(r.Ip), 0));
            }
            return s;
        }

        static void Connect(Socket s, Server srv, int ms)
        {
            var ar = s.BeginConnect(srv.Addr, srv.Port, null, null);
            if (!ar.AsyncWaitHandle.WaitOne(ms)) { try { s.Close(); } catch (Exception) { } throw new TimeoutException(); }
            s.EndConnect(ar);
        }

        // приём или отдача: несколько соединений сразу (по кругу на серверы), запросы по 25 МБ подряд
        double Measure(Row r, List<Server> use, bool down)
        {
            double dur = down ? DownSec : UpSec;
            long total = 0; int fails = 0;
            var done = new ManualResetEvent(false);
            Func<bool> over = () => stop || done.WaitOne(0);
            var socks = new List<Socket>();
            var threads = new List<Thread>();
            for (int i = 0; i < r.Streams; i++)
            {
                var srv = use[i % use.Count];
                var t = new Thread(() =>
                {
                    while (!over())
                    {
                        Socket s = null;
                        try
                        {
                            s = NewSocket(r);
                            if (!down) s.SendBufferSize = 512 * 1024;
                            s.ReceiveTimeout = s.SendTimeout = 5000;
                            lock (socks) { if (over()) { s.Close(); return; } socks.Add(s); }
                            Connect(s, srv, 5000);
                            using (var ns = new NetworkStream(s, false))
                            using (var ssl = new SslStream(ns, false))
                            {
                                ssl.AuthenticateAsClient(srv.Host, null, (System.Security.Authentication.SslProtocols)3072 /*TLS 1.2*/, false);
                                string hostHdr = srv.Port == 443 ? srv.Host : srv.Host + ":" + srv.Port;
                                if (down)
                                {
                                    ssl.Write(Encoding.ASCII.GetBytes("GET " + srv.DownPath() + " HTTP/1.1\r\nHost: " + hostHdr + "\r\nUser-Agent: Qural\r\nConnection: close\r\n\r\n"));
                                    var buf = new byte[256 * 1024];
                                    int n = ssl.Read(buf, 0, buf.Length);
                                    if (n < 12 || Encoding.ASCII.GetString(buf, 0, 12).IndexOf(" 200", StringComparison.Ordinal) < 0) throw new IOException("сервер отказал");
                                    Interlocked.Add(ref total, n);
                                    while (!over() && (n = ssl.Read(buf, 0, buf.Length)) > 0) Interlocked.Add(ref total, n);
                                }
                                else
                                {
                                    const long len = 25000000;
                                    ssl.Write(Encoding.ASCII.GetBytes("POST " + srv.UpPath() + " HTTP/1.1\r\nHost: " + hostHdr + "\r\nUser-Agent: Qural\r\nContent-Type: application/octet-stream\r\nContent-Length: " + len + "\r\nConnection: close\r\n\r\n"));
                                    var chunk = new byte[64 * 1024];
                                    new Random().NextBytes(chunk);
                                    long sent = 0;
                                    while (!over() && sent < len)
                                    {
                                        int n = (int)Math.Min(chunk.Length, len - sent);
                                        ssl.Write(chunk, 0, n); sent += n; Interlocked.Add(ref total, n);
                                    }
                                    if (!over()) { var tmp = new byte[4096]; try { ssl.Read(tmp, 0, tmp.Length); } catch (Exception) { } }   // ответ сервера
                                }
                            }
                        }
                        catch (Exception) { if (!over() && Interlocked.Increment(ref fails) > r.Streams * 3) return; if (!over()) Thread.Sleep(200); }
                        finally { if (s != null) { lock (socks) socks.Remove(s); try { s.Close(); } catch (Exception) { } } }
                    }
                }) { IsBackground = true };
                threads.Add(t); t.Start();
            }

            // 5 раз в секунду: живая скорость (за последнюю секунду) для стрелки и отсчёт для итога
            var sw = Stopwatch.StartNew();
            var marks = new Queue<KeyValuePair<double, long>>();
            var samples = new List<double>();
            double prevT = 0; long prevB = 0;
            while (!stop)
            {
                Thread.Sleep(200);
                double now = sw.Elapsed.TotalSeconds; long b = Interlocked.Read(ref total);
                marks.Enqueue(new KeyValuePair<double, long>(now, b));
                while (marks.Count > 1 && now - marks.Peek().Key > 1.0) marks.Dequeue();
                var first = marks.Peek();
                Mbps = now - first.Key > 0.1 ? (b - first.Value) * 8 / 1e6 / (now - first.Key) : 0;
                if (now >= Warm && now - prevT > 0.05) samples.Add((b - prevB) * 8 / 1e6 / (now - prevT));
                prevT = now; prevB = b;
                Progress = Math.Min(1, now / dur);
                Raise();
                if (now >= dur) break;
                if (fails > r.Streams * 3) break;
            }
            long endB = Interlocked.Read(ref total);
            done.Set();
            lock (socks) foreach (var s in socks) try { s.Close(); } catch (Exception) { }
            foreach (var t in threads) t.Join(3000);
            if (endB == 0) throw new IOException(fails > 0 ? "сервер отказал" : "нет данных");
            return Math.Round(Trimmed(samples), 1);
        }

        // как в Speedtest: без 30% самых медленных и 10% самых быстрых отсчётов — среднее остальных
        static double Trimmed(List<double> s)
        {
            if (s.Count == 0) return 0;
            var a = s.OrderBy(x => x).ToList();
            int lo = (int)(a.Count * 0.3), hi = (int)(a.Count * 0.1);
            var mid = a.Skip(lo).Take(Math.Max(1, a.Count - lo - hi)).ToList();
            return mid.Average();
        }
    }
}
