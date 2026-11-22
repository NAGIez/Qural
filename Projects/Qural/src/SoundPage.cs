using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Qural
{
    // Вкладка «Звук»: громкость программ по горячим клавишам.
    partial class MainWindow
    {
        static readonly Dictionary<string, string> iconCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        AppEntry removedApp; int removedAppIndex;

        SoundConfig S { get { return app.Sound; } }

        // нажали клавишу громкости — сразу показать новый уровень (проценты в меню видны на любой странице).
        // При удержании клавиши нажатия идут пачкой — склеиваем их в одно обновление.
        bool livePending;
        public void SoundChanged()
        {
            if (!ready || livePending || !IsHandleCreated) return;
            livePending = true;
            BeginInvoke(new Action(() => { livePending = false; PushSoundLive(); }));
        }

        // список «изменены через Активное окно» пополнился — отправить состояние целиком (с названиями и значками)
        public void SoundListChanged()
        {
            if (!ready || !IsHandleCreated) return;
            BeginInvoke(new Action(SendSound));
        }

        AppEntry CurrentEntry
        {
            get
            {
                if (S.Active == SoundConfig.FocusedIndex) return S.Focused;
                return S.Active >= 0 && S.Active < S.Apps.Count ? S.Apps[S.Active] : null;
            }
        }

        AppEntry CurrentApp { get { var a = CurrentEntry; return a == S.Focused ? null : a; } }

        void OnSound(Dictionary<string, object> m)
        {
            AppEntry a;
            switch (Str(m, "type"))
            {
                case "select":
                    S.Active = Int(m, "index"); // −2 — «Активное окно»
                    S.Save();
                    SendSound();
                    break;

                case "sessions":
                    SendSessions();
                    break;

                case "add":
                    {
                        string proc = CleanProcess(Str(m, "process"));
                        if (proc.Length == 0) { Toast("sound", "Укажите имя программы, например Discord или chrome.exe", false); break; }
                        int existing = S.Apps.FindIndex(x => x.Has(proc));
                        if (existing >= 0) { S.Active = existing; Toast("sound", "Эта программа уже в списке", false); SendSound(); break; }
                        S.Apps.Add(new AppEntry { Process = proc, Title = TitleFor(proc, Str(m, "path")), Step = 5, Repeat = true });
                        S.Untouch(proc); // теперь у неё своя строка — в списке «изменённых через Активное окно» она не нужна
                        S.Active = S.Apps.Count - 1;
                        S.Save();
                        app.RegisterHotkeys();
                        SendSound();
                        break;
                    }

                case "remove":
                    a = CurrentApp;
                    if (a == null) break;
                    removedApp = a; removedAppIndex = S.Active;
                    S.Apps.RemoveAt(S.Active);
                    S.Active = S.Apps.Count == 0 ? SoundConfig.FocusedIndex : Math.Max(0, removedAppIndex - 1);
                    S.Save();
                    app.RegisterHotkeys();
                    SendSound();
                    Toast("sound", "«" + a.Title + "» убрана из списка", true);
                    break;

                case "restore":
                    if (removedApp == null) break;
                    for (int i = 0; i < 3; i++) app.TakeHotkey(removedApp.Get(i), removedApp, i);
                    S.Apps.Insert(Math.Min(removedAppIndex, S.Apps.Count), removedApp);
                    S.Active = S.Apps.IndexOf(removedApp);
                    removedApp = null;
                    S.Save(); C.Save();
                    app.RegisterHotkeys();
                    SendSound(); SendColor();
                    break;

                case "addName":
                    {
                        a = CurrentApp;
                        if (a == null) break;
                        string name = CleanProcess(Str(m, "name"));
                        if (name.Length == 0) break;
                        if (a.Has(name)) { Toast("sound", name + ".exe уже есть у этой программы", false); break; }
                        var owner = S.Apps.Find(x => x != a && x.Has(name));
                        if (owner != null) { Toast("sound", name + ".exe уже есть у «" + owner.Title + "» — сначала уберите его там", false); break; }
                        a.Extra.Add(name);
                        S.Save();
                        SendSound();
                        break;
                    }

                case "removeName":
                    {
                        a = CurrentApp;
                        if (a == null) break;
                        string name = Str(m, "name");
                        var names = a.Names();
                        if (names.Length <= 1) { Toast("sound", "Нужно хотя бы одно имя exe", false); break; }
                        if (string.Equals(a.Process, name, StringComparison.OrdinalIgnoreCase))
                        {
                            a.Process = names[1];
                            a.Extra.RemoveAll(x => string.Equals(x, names[1], StringComparison.OrdinalIgnoreCase));
                        }
                        else a.Extra.RemoveAll(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
                        S.Save();
                        SendSound();
                        break;
                    }

                case "step":
                    a = CurrentEntry;
                    if (a == null) break;
                    a.Step = Math.Max(1, Math.Min(50, Int(m, "value")));
                    saveTimer.Stop(); saveTimer.Start();
                    break;

                case "repeat":
                    a = CurrentEntry;
                    if (a == null) break;
                    a.Repeat = Bool(m, "value");
                    S.Save();
                    app.RegisterHotkeys();
                    SendSound();
                    break;

                case "volume":
                    a = CurrentApp;
                    if (a == null) break;
                    Audio.Set(a.Names(), (float)(Num(m, "value") / 100.0));
                    app.DeckChanged();
                    break;

                case "mute":
                    a = CurrentApp;
                    if (a == null) break;
                    if (Audio.ToggleMute(a.Names(), S.MuteMemo) == null) Toast("sound", "«" + a.Title + "» сейчас не в микшере — включите в ней звук", false);
                    saveTimer.Stop(); saveTimer.Start();
                    PushSoundLive();
                    app.DeckChanged();
                    break;

                case "hotkey":
                    {
                        a = CurrentEntry;
                        int action = Int(m, "action");
                        if (a == null || action < 0 || action > 2) break;
                        var entry = a;
                        string msg = AssignHotkey(Int(m, "key"), entry, action, k => entry.Set(action, k));
                        if (msg != null) { Toast("sound", msg, false); SendSound(); }
                        break;
                    }

                // ── программы, изменённые через «Активное окно» ──
                case "touchedSet":
                    {
                        string name = Str(m, "name");
                        if (!S.Touched.Exists(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase))) break;
                        if (Audio.Set(new[] { name }, (float)(Num(m, "value") / 100.0)) < 0)
                            Toast("sound", name + ".exe сейчас не в микшере — включите в ней звук", false);
                        S.MuteMemo.Remove(name.ToLowerInvariant());
                        app.DeckChanged();
                        break;
                    }

                case "touchedReset":
                    {
                        string name = Str(m, "name");
                        // Audio.Set при громкости больше 0 сам снимает и настоящий «без звука»
                        if (Audio.Set(new[] { name }, 1f) < 0) { Toast("sound", name + ".exe сейчас не в микшере — вернуть громкость можно, когда она снова включит звук", false); break; }
                        S.MuteMemo.Remove(name.ToLowerInvariant());
                        saveTimer.Stop(); saveTimer.Start();
                        PushSoundLive();
                        app.DeckChanged();
                        Toast("sound", name + ".exe — громкость возвращена на 100%", false);
                        break;
                    }

                case "touchedForget":
                    S.Forget(Str(m, "name")); // убрали руками — перенос эту программу больше не добавит
                    S.Save();
                    SendSound();
                    break;

                case "touchedAdopt":
                    {
                        string name = CleanProcess(Str(m, "name"));
                        if (name.Length == 0 || S.Apps.Exists(x => x.Has(name))) { S.Untouch(name); SendSound(); break; }
                        string path = "";
                        foreach (var i in Audio.Snapshot()) if (string.Equals(i.Process, name, StringComparison.OrdinalIgnoreCase)) path = i.Path;
                        S.Apps.Add(new AppEntry { Process = name, Title = TitleFor(name, path), Step = S.Focused.Step, Repeat = true });
                        S.Untouch(name);
                        S.Active = S.Apps.Count - 1;
                        S.Save();
                        app.RegisterHotkeys();
                        SendSound();
                        Toast("sound", "«" + TitleFor(name, path) + "» теперь отдельная программа — назначьте ей свои клавиши", false);
                        break;
                    }
            }
        }

        // изменённые через «Активное окно», кроме тех, у кого уже своя строка
        List<string> TouchedList() { return S.Touched.Where(t => !S.Apps.Exists(a => a.Has(t))).ToList(); }

        object TouchedRow(List<Audio.Info> snap, string name, bool full)
        {
            var l = Level(snap, new AppEntry { Process = name, Extra = new List<string>() });
            if (!full) return new { process = name, running = l.Running, volume = l.Volume, muted = l.Muted };
            return new { process = name, title = TitleFor(name, l.Path), icon = IconFor(name, l.Path), running = l.Running, volume = l.Volume, muted = l.Muted };
        }

        // ── в страницу ──
        void SendSound()
        {
            if (!ready) return;
            var snap = Audio.Snapshot();
            if (S.Seed(snap)) { saveTimer.Stop(); saveTimer.Start(); }
            Post("sound", new
            {
                type = "state",
                state = new
                {
                    apps = S.Apps.Select(x =>
                    {
                        var l = Level(snap, x);
                        return new
                        {
                            process = x.Process,
                            names = x.Names(),
                            title = x.Title,
                            icon = IconFor(x.Process, l.Path),
                            keys = new[] { HotkeyText.Format(x.Up), HotkeyText.Format(x.Down), HotkeyText.Format(x.Mute) },
                            step = x.Step,
                            repeat = x.Repeat,
                            running = l.Running,
                            volume = l.Volume,
                            muted = l.Muted
                        };
                    }).ToArray(),
                    focused = new
                    {
                        keys = new[] { HotkeyText.Format(S.Focused.Up), HotkeyText.Format(S.Focused.Down), HotkeyText.Format(S.Focused.Mute) },
                        step = S.Focused.Step,
                        repeat = S.Focused.Repeat
                    },
                    touched = TouchedList().Select(t => TouchedRow(snap, t, true)).ToArray(),
                    active = S.Active
                }
            });
        }

        void PushSoundLive()
        {
            if (!ready) return;
            var snap = Audio.Snapshot();
            // перенос нашёл новую программу — странице нужен полный список (с названием и значком)
            if (S.Seed(snap)) { saveTimer.Stop(); saveTimer.Start(); SendSound(); return; }
            var levels = S.Apps.Select(x => { var l = Level(snap, x); return new { running = l.Running, volume = l.Volume, muted = l.Muted }; }).ToArray();
            var touched = TouchedList().Select(t => TouchedRow(snap, t, false)).ToArray();
            Post("sound", new { type = "live", levels = levels, touched = touched, fallback = Audio.UsedChannelFallback });
        }

        void SendSessions()
        {
            var list = Audio.Snapshot()
                .Where(s => !string.Equals(s.Process, "Qural", StringComparison.OrdinalIgnoreCase))
                .Select(s => new
                {
                    process = s.Process,
                    title = TitleFor(s.Process, s.Path),
                    path = s.Path,
                    icon = IconFor(s.Process, s.Path),
                    volume = (int)Math.Round(s.Volume * 100),
                    added = S.Apps.Exists(a => a.Has(s.Process))
                }).OrderBy(s => s.title).ToArray();
            Post("sound", new { type = "sessions", list = list });
        }

        class Lvl { public bool Running, Muted = true; public int Volume; public string Path = ""; }
        static Lvl Level(List<Audio.Info> snap, AppEntry x)
        {
            var r = new Lvl();
            foreach (var s in snap)
            {
                if (!x.Has(s.Process)) continue;
                r.Running = true;
                r.Volume = Math.Max(r.Volume, (int)Math.Round(s.Volume * 100));
                r.Muted = r.Muted && s.Muted;
                if (r.Path.Length == 0 || string.Equals(s.Process, x.Process, StringComparison.OrdinalIgnoreCase)) r.Path = s.Path ?? "";
            }
            if (!r.Running) r.Muted = false;
            return r;
        }

        // ── значки и названия программ ──
        static string IconFor(string process, string path)
        {
            string cached;
            if (iconCache.TryGetValue(process, out cached) && (cached.Length > 0 || string.IsNullOrEmpty(path))) return cached;
            string res = "";
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    using (var ico = Icon.ExtractAssociatedIcon(path))
                    using (var bmp = ico.ToBitmap())
                    using (var ms = new MemoryStream())
                    {
                        bmp.Save(ms, ImageFormat.Png);
                        res = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
                    }
            }
            catch (Exception) { }
            iconCache[process] = res;
            return res;
        }

        static string TitleFor(string process, string path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    string d = FileVersionInfo.GetVersionInfo(path).FileDescription;
                    if (!string.IsNullOrWhiteSpace(d) && d.Length <= 40) return d.Trim();
                }
            }
            catch (Exception) { }
            return process.Length > 0 ? char.ToUpper(process[0]) + process.Substring(1) : process;
        }

        static string CleanProcess(string s)
        {
            s = (s ?? "").Trim().Trim('"');
            if (s.Contains("\\") || s.Contains("/")) s = Path.GetFileName(s);
            if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) s = s.Substring(0, s.Length - 4);
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }
    }
}
