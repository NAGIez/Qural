using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Script.Serialization;

namespace Qural
{
    // Обновления из релизов GitHub (NAGIez/Qural): последний релиз, в нём установщик QuralSetup-<версия>.exe.
    // Проверка — раз в 12 часов (первая через минуту после запуска), только у установленной копии (рядом install.txt)
    // и если не выключено в «Общих». Обновить: скачать установщик во временную папку, сверить SHA-256 (если GitHub
    // его дал), запустить с --update <папка> и выйти — установщик заменит файлы и запустит Qural снова.
    static class Updater
    {
        public const string Repo = "NAGIez/Qural";
        const string Api = "https://api.github.com/repos/" + Repo + "/releases/latest";

        public sealed class Release { public string Version = "", Url = "", Name = "", Sha256 = "", Page = ""; public long Size; }

        // состояние для окна: idle | checking | latest | available | downloading | error
        public static volatile string State = "idle";
        public static volatile string Error = "";
        public static volatile int Progress;
        public static Release Latest;
        public static DateTime CheckedAt;
        public static event Action Changed;

        public static string Dir { get { return Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location); } }
        public static bool Installed { get { return File.Exists(Path.Combine(Dir, "install.txt")); } }
        public static Version Current { get { var v = Assembly.GetExecutingAssembly().GetName().Version; return new Version(v.Major, v.Minor, Math.Max(0, v.Build)); } }
        public static string CurrentText { get { return Current.ToString(3); } }

        static void Fire() { var h = Changed; if (h != null) try { h(); } catch (Exception) { } }

        static WebClient Client()
        {
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; // TLS 1.2 — без него GitHub не ответит
            var w = new WebClient();
            w.Headers[HttpRequestHeader.UserAgent] = "Qural-Updater";
            return w;
        }

        // проверить в фоне; done — на потоке пула (вызывающий сам переносит на свой поток)
        public static void CheckAsync(Action done)
        {
            if (State == "checking" || State == "downloading") return;
            State = "checking"; Error = ""; Fire();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    string text;
                    using (var w = Client()) { w.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json"; text = w.DownloadString(Api); }
                    Latest = Parse(text);
                    State = Latest != null && Newer(Latest.Version) ? "available" : "latest";
                    CheckedAt = DateTime.Now;
                }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    State = "error";
                    Error = r != null && r.StatusCode == HttpStatusCode.NotFound ? "Релизов пока нет (или репозиторий закрыт)" : "Нет связи с GitHub: " + ex.Message;
                }
                catch (Exception ex) { State = "error"; Error = ex.Message; }
                RecLog.Write("обновления: " + State + (Latest != null ? " " + Latest.Version : "") + (Error.Length > 0 ? " — " + Error : ""));
                Fire();
                if (done != null) done();
            });
        }

        static Release Parse(string json)
        {
            var m = new JavaScriptSerializer().DeserializeObject(json) as Dictionary<string, object>;
            if (m == null) return null;
            var r = new Release { Version = Str(m, "tag_name").TrimStart('v', 'V'), Page = Str(m, "html_url") };
            object assets; m.TryGetValue("assets", out assets);
            var list = assets as object[];
            if (list != null)
                foreach (var o in list)
                {
                    var a = o as Dictionary<string, object>;
                    if (a == null) continue;
                    string name = Str(a, "name");
                    if (!name.StartsWith("QuralSetup", StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    r.Name = name; r.Url = Str(a, "browser_download_url");
                    long size; long.TryParse(Str(a, "size"), out size); r.Size = size;
                    string dg = Str(a, "digest");   // "sha256:…" — GitHub считает сам
                    if (dg.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) r.Sha256 = dg.Substring(7).ToLowerInvariant();
                    break;
                }
            return r.Url.Length > 0 ? r : null;
        }

        static bool Newer(string v)
        {
            Version x;
            return Version.TryParse(v, out x) && new Version(x.Major, x.Minor, Math.Max(0, x.Build)) > Current;
        }

        // скачать и запустить установщик; done(null) — установщик запущен, Qural надо закрыть; иначе текст ошибки
        public static void InstallAsync(Action<string> done)
        {
            var rel = Latest;
            if (rel == null || State == "downloading") return;
            State = "downloading"; Progress = 0; Error = ""; Fire();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string err = null;
                try
                {
                    string dir = Path.Combine(Path.GetTempPath(), "Qural-update");
                    Directory.CreateDirectory(dir);
                    string file = Path.Combine(dir, rel.Name);
                    using (var w = Client())
                    {
                        var finished = new ManualResetEvent(false); Exception fail = null;
                        w.DownloadProgressChanged += (s, e) => { if (e.ProgressPercentage != Progress) { Progress = e.ProgressPercentage; Fire(); } };
                        w.DownloadFileCompleted += (s, e) => { fail = e.Error; finished.Set(); };
                        w.DownloadFileAsync(new Uri(rel.Url), file);
                        finished.WaitOne();
                        if (fail != null) throw fail;
                    }
                    var fi = new FileInfo(file);
                    if (rel.Size > 0 && fi.Length != rel.Size) throw new Exception("файл скачался не целиком");
                    if (rel.Sha256.Length > 0 && Sha256(file) != rel.Sha256) { try { fi.Delete(); } catch (Exception) { } throw new Exception("контрольная сумма не совпала — файл повреждён"); }
                    RecLog.Write("обновления: запускаю " + file);
                    Process.Start(new ProcessStartInfo(file, "--update \"" + Dir + "\"") { UseShellExecute = true, WorkingDirectory = dir });
                }
                catch (Exception ex) { err = ex.Message; }
                State = err == null ? "available" : "error"; Error = err ?? "";
                if (err != null) RecLog.Write("обновления: ошибка " + err);
                Fire();
                done(err);
            });
        }

        static string Sha256(string file)
        {
            using (var s = File.OpenRead(file)) using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        static string Str(Dictionary<string, object> m, string k) { object v; return m.TryGetValue(k, out v) && v != null ? v.ToString() : ""; }
    }
}
