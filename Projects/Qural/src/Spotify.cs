using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Qural
{
    // Spotify Web API — только «Любимые треки»: лайкнут ли трек, что играет, и лайк / снять лайк.
    // Вход один раз — своё приложение на developer.spotify.com (Client ID), вход PKCE без секрета:
    // Qural открывает в браузере страницу Spotify «Разрешить?», ответ ловит на http://127.0.0.1:8899/callback.
    // Токены — только в %APPDATA%\Qural\spotify.json, уходят только на accounts.spotify.com и api.spotify.com.
    // Все методы блокирующие — вызывать из фонового потока.
    sealed class Spotify
    {
        public const int Port = 8899;
        public static string Redirect { get { return "http://127.0.0.1:" + Port + "/callback"; } }
        const string Scopes = "user-library-read user-library-modify user-read-currently-playing";
        static readonly string CfgPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qural", "spotify.json");

        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly object gate = new object();
        Dictionary<string, object> cfg;
        volatile bool loggingIn;

        public Spotify()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            try { cfg = json.DeserializeObject(File.ReadAllText(CfgPath)) as Dictionary<string, object>; } catch (Exception) { }
            if (cfg == null) cfg = new Dictionary<string, object>();
        }

        string Get(string k) { lock (gate) { object v; return cfg.TryGetValue(k, out v) && v != null ? v.ToString() : ""; } }
        void Save()
        {
            try { lock (gate) { Directory.CreateDirectory(Path.GetDirectoryName(CfgPath)); File.WriteAllText(CfgPath, json.Serialize(cfg)); } }
            catch (Exception) { }
        }

        public bool Ready { get { return Get("refresh").Length > 0; } }
        public string ClientId { get { return Get("clientId"); } }
        public bool LoggingIn { get { return loggingIn; } }

        // ── вход: открыть в браузере «Разрешить?» и дождаться ответа (до 3 минут) ──
        public string Login(string clientId)
        {
            clientId = (clientId ?? "").Trim();
            if (clientId.Length < 16) return "Нужен Client ID приложения Spotify";
            if (loggingIn) return "Уже жду ответа в браузере";
            loggingIn = true;
            TcpListener server = null;
            try
            {
                try { server = new TcpListener(IPAddress.Loopback, Port); server.Start(); }
                catch (Exception) { return "Порт " + Port + " занят — закройте программу, которая его держит"; }
                string verifier = Rand(64), state = Rand(16);
                string challenge;
                using (var sha = SHA256.Create()) challenge = B64Url(sha.ComputeHash(Encoding.ASCII.GetBytes(verifier)));
                string url = "https://accounts.spotify.com/authorize?response_type=code&client_id=" + Uri.EscapeDataString(clientId) +
                             "&scope=" + Uri.EscapeDataString(Scopes) + "&redirect_uri=" + Uri.EscapeDataString(Redirect) +
                             "&code_challenge_method=S256&code_challenge=" + challenge + "&state=" + state;
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

                // ждём, пока браузер вернётся на /callback?code=…&state=…
                var until = DateTime.UtcNow.AddMinutes(3);
                while (DateTime.UtcNow < until)
                {
                    if (!server.Pending()) { System.Threading.Thread.Sleep(200); continue; }
                    using (var c = server.AcceptTcpClient())
                    {
                        c.ReceiveTimeout = 3000;
                        var s = c.GetStream();
                        var buf = new byte[8192]; int n = s.Read(buf, 0, buf.Length);
                        string req = Encoding.ASCII.GetString(buf, 0, n);
                        string line = req.Split('\n')[0];                 // GET /callback?code=… HTTP/1.1
                        string path = line.Split(' ').Length > 1 ? line.Split(' ')[1] : "";
                        if (!path.StartsWith("/callback")) { Reply(s, 404, "—"); continue; }
                        var q = Query(path);
                        string err, code;
                        if (q.TryGetValue("error", out err)) { Reply(s, 200, "Spotify: доступ не дан (" + err + "). Окно можно закрыть."); return "Spotify не дал доступ: " + err; }
                        if (!q.TryGetValue("code", out code) || Get(q, "state") != state) { Reply(s, 400, "Неверный ответ"); continue; }
                        string e2 = Token(new Dictionary<string, string> {
                            { "grant_type", "authorization_code" }, { "code", code }, { "redirect_uri", Redirect },
                            { "client_id", clientId }, { "code_verifier", verifier } }, clientId);
                        Reply(s, 200, e2 == null ? "Готово — Qural подключён к Spotify. Окно можно закрыть." : "Не вышло: " + e2);
                        return e2;
                    }
                }
                return "Не дождался ответа из браузера (3 минуты)";
            }
            catch (Exception e) { return e.Message; }
            finally { loggingIn = false; if (server != null) try { server.Stop(); } catch (Exception) { } }
        }

        static string Get(Dictionary<string, string> d, string k) { string v; return d.TryGetValue(k, out v) ? v : ""; }

        static void Reply(Stream s, int status, string text)
        {
            string body = "<!doctype html><meta charset=utf-8><title>Qural</title><body style=\"font:16px Segoe UI;background:#111;color:#eee;padding:40px\">" + WebUtility.HtmlEncode(text) + "</body>";
            byte[] b = Encoding.UTF8.GetBytes(body);
            byte[] h = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + " OK\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n");
            try { s.Write(h, 0, h.Length); s.Write(b, 0, b.Length); s.Flush(); } catch (Exception) { }
        }

        static Dictionary<string, string> Query(string path)
        {
            var d = new Dictionary<string, string>();
            int i = path.IndexOf('?'); if (i < 0) return d;
            foreach (var kv in path.Substring(i + 1).Split('&'))
            {
                int e = kv.IndexOf('='); if (e <= 0) continue;
                d[Uri.UnescapeDataString(kv.Substring(0, e))] = Uri.UnescapeDataString(kv.Substring(e + 1).Replace('+', ' '));
            }
            return d;
        }

        static string Rand(int n)
        {
            const string abc = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
            var b = new byte[n]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b);
            var sb = new StringBuilder(n); foreach (var x in b) sb.Append(abc[x % abc.Length]); return sb.ToString();
        }
        static string B64Url(byte[] b) { return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_'); }

        // обмен кода / обновление токена → null или текст ошибки
        string Token(Dictionary<string, string> form, string clientId)
        {
            var sb = new StringBuilder();
            foreach (var kv in form) { if (sb.Length > 0) sb.Append('&'); sb.Append(kv.Key).Append('=').Append(Uri.EscapeDataString(kv.Value)); }
            int status; string body = Http("POST", "https://accounts.spotify.com/api/token", null, sb.ToString(), "application/x-www-form-urlencoded", out status);
            var j = Parse(body);
            if (status != 200 || j == null || !j.ContainsKey("access_token"))
            {
                string err = j != null && j.ContainsKey("error") ? j["error"] + (j.ContainsKey("error_description") ? ": " + j["error_description"] : "") : "HTTP " + status;
                return err;
            }
            lock (gate)
            {
                cfg["clientId"] = clientId;
                cfg["access"] = j["access_token"].ToString();
                if (j.ContainsKey("refresh_token") && j["refresh_token"] != null) cfg["refresh"] = j["refresh_token"].ToString();
                int exp = 3600; try { exp = Convert.ToInt32(j["expires_in"]); } catch (Exception) { }
                cfg["expires"] = DateTime.UtcNow.AddSeconds(exp - 120).Ticks.ToString();
            }
            Save();
            return null;
        }

        string Access()
        {
            if (!Ready) return null;
            long t; long.TryParse(Get("expires"), out t);
            if (Get("access").Length > 0 && DateTime.UtcNow.Ticks < t) return Get("access");
            string err = Token(new Dictionary<string, string> { { "grant_type", "refresh_token" }, { "refresh_token", Get("refresh") }, { "client_id", ClientId } }, ClientId);
            if (err != null && err.StartsWith("invalid_grant")) { lock (gate) { cfg.Remove("refresh"); cfg.Remove("access"); } Save(); } // доступ отозван — войти заново
            return err == null ? Get("access") : null;
        }

        // id трека, который сейчас играет в Spotify (null — ничего или не трек: подкаст, реклама)
        public string CurrentTrackId(out string error)
        {
            error = null;
            string tok = Access();
            if (tok == null) { error = Ready ? "Spotify не отвечает" : "Spotify не подключён — войдите в настройке кнопки «Сейчас играет»"; return null; }
            int status; string body = Http("GET", "https://api.spotify.com/v1/me/player/currently-playing", tok, null, null, out status);
            if (status == 204) { error = "В Spotify сейчас ничего не играет"; return null; }
            var j = Parse(body);
            if (status != 200 || j == null) { error = "Spotify: HTTP " + status; return null; }
            var item = j.ContainsKey("item") ? j["item"] as Dictionary<string, object> : null;
            if (item == null || !item.ContainsKey("id") || item["id"] == null) { error = "Это не трек (подкаст или реклама)"; return null; }
            return item["id"].ToString();
        }

        // в «Любимых треках»? null — не удалось узнать
        public bool? IsLiked(string id, out string error)
        {
            error = null;
            string tok = Access(); if (tok == null) { error = "Spotify не подключён"; return null; }
            // новый адрес библиотеки (/me/library); старый /me/tracks/contains новым приложениям отвечает 403
            int status; string body = Http("GET", "https://api.spotify.com/v1/me/library/contains?uris=" + Uri.EscapeDataString("spotify:track:" + id), tok, null, null, out status);
            if (status != 200) body = Http("GET", "https://api.spotify.com/v1/me/tracks/contains?ids=" + Uri.EscapeDataString(id), tok, null, null, out status);
            if (status != 200) { error = "Spotify: HTTP " + status; return null; }
            try { var arr = json.DeserializeObject(body) as object[]; if (arr != null && arr.Length > 0) return Convert.ToBoolean(arr[0]); } catch (Exception) { }
            error = "Spotify: непонятный ответ"; return null;
        }

        // добавить в «Любимые треки» (like = true) или убрать; null — получилось
        public string SetLiked(string id, bool like)
        {
            string tok = Access(); if (tok == null) return "Spotify не подключён";
            int status; Http(like ? "PUT" : "DELETE", "https://api.spotify.com/v1/me/library?uris=" + Uri.EscapeDataString("spotify:track:" + id), tok, null, null, out status);
            if (status < 200 || status >= 300) Http(like ? "PUT" : "DELETE", "https://api.spotify.com/v1/me/tracks?ids=" + Uri.EscapeDataString(id), tok, null, null, out status);
            return status >= 200 && status < 300 ? null : "Spotify: HTTP " + status;
        }

        static string Http(string method, string url, string bearer, string body, string type, out int status)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.Method = method; r.Timeout = 8000; r.ReadWriteTimeout = 8000;
            if (bearer != null) r.Headers["Authorization"] = "Bearer " + bearer;
            if (body != null)
            {
                byte[] b = Encoding.UTF8.GetBytes(body);
                r.ContentType = type; r.ContentLength = b.Length;
                using (var s = r.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            else if (method == "PUT" || method == "DELETE") r.ContentLength = 0;
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)r.GetResponse(); }
            catch (WebException e) { resp = e.Response as HttpWebResponse; if (resp == null) { status = 0; return ""; } }
            using (resp)
            {
                status = (int)resp.StatusCode;
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) return sr.ReadToEnd();
            }
        }

        Dictionary<string, object> Parse(string s)
        {
            try { return json.DeserializeObject(s) as Dictionary<string, object>; } catch (Exception) { return null; }
        }
    }
}
