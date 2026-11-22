using System;
using System.Collections.Generic;
using System.Linq;

namespace Qural
{
    // Вкладка «Цвет»: профили, мониторы, яркость / контраст / гамма / интенсивность / оттенок.
    partial class MainWindow
    {
        Profile deletedProfile; int deletedProfileIndex;
        string lastColorError = ""; DateTime lastColorErrorAt;

        ColorConfig C { get { return app.Color; } }

        public void ColorChanged() { SendColor(); }

        void OnColor(Dictionary<string, object> m)
        {
            var p = C.ActiveProfile;
            switch (Str(m, "type"))
            {
                case "select":
                    app.SwitchColor(Int(m, "index"));
                    break;

                case "add":
                    {
                        var np = new Profile { Name = UniqueProfileName(NameArg(m, "Профиль " + (C.Profiles.Count + 1)), null) };
                        foreach (var mon in Displays.GetActive()) np.For(mon, true);
                        np.Hotkey = FreeColorHotkey();
                        C.Profiles.Add(np);
                        C.Save();
                        app.RegisterHotkeys();
                        app.SwitchColor(C.Profiles.Count - 1);
                        break;
                    }

                case "copy":
                    if (p == null) break;
                    {
                        var cp = p.Clone(UniqueProfileName(p.Name + " (копия)", null));
                        cp.Hotkey = FreeColorHotkey();
                        C.Profiles.Add(cp);
                        C.Save();
                        app.RegisterHotkeys();
                        app.SwitchColor(C.Profiles.Count - 1);
                        Toast("color", "Создана копия «" + p.Name + "»", false);
                        break;
                    }

                case "rename":
                    if (p == null) break;
                    p.Name = UniqueProfileName(NameArg(m, p.Name), p);
                    C.Save();
                    app.UpdateTooltip();
                    SendColor();
                    break;

                case "delete":
                    if (p == null) break;
                    if (C.Profiles.Count == 1) { Toast("color", "Нужен хотя бы один профиль", false); break; }
                    deletedProfile = p; deletedProfileIndex = C.Active;
                    C.Profiles.RemoveAt(C.Active);
                    C.Save();
                    app.RegisterHotkeys();
                    app.SwitchColor(Math.Max(0, deletedProfileIndex - 1));
                    Toast("color", "Профиль «" + p.Name + "» удалён", true);
                    break;

                case "restore":
                    if (deletedProfile == null) break;
                    app.TakeHotkey(deletedProfile.Hotkey, deletedProfile, 0);
                    C.Profiles.Insert(Math.Min(deletedProfileIndex, C.Profiles.Count), deletedProfile);
                    int idx = C.Profiles.IndexOf(deletedProfile);
                    deletedProfile = null;
                    C.Save(); app.Sound.Save();
                    app.RegisterHotkeys();
                    app.SwitchColor(idx);
                    break;

                case "move":
                    {
                        int i = C.Active, j = i + Int(m, "dir");
                        if (j < 0 || j >= C.Profiles.Count) break;
                        var t = C.Profiles[i]; C.Profiles[i] = C.Profiles[j]; C.Profiles[j] = t;
                        C.Active = j;
                        C.Save();
                        app.RegisterHotkeys();
                        SendColor();
                        break;
                    }

                case "set":
                    {
                        MonitorInfo mon; MonitorSettings s;
                        if (!FindMonitor(Str(m, "key"), out mon, out s)) break;
                        double v = Num(m, "value");
                        switch (Str(m, "field"))
                        {
                            case "brightness": s.Brightness = Clamp((int)Math.Round(v), 0, 100); break;
                            case "contrast": s.Contrast = Clamp((int)Math.Round(v), 0, 100); break;
                            case "gamma": s.Gamma = Math.Max(0.3, Math.Min(2.8, Math.Round(v, 2))); break;
                            case "vibrance": s.Vibrance = Clamp((int)Math.Round(v), 0, 100); break;
                            case "hue": s.Hue = Clamp((int)Math.Round(v), 0, 359); break;
                            default: return;
                        }
                        if (s.Enabled) ApplyAndReport(mon, s);
                        saveTimer.Stop(); saveTimer.Start();
                        break;
                    }

                case "enabled":
                    {
                        MonitorInfo mon; MonitorSettings s;
                        if (!FindMonitor(Str(m, "key"), out mon, out s)) break;
                        s.Enabled = Bool(m, "value");
                        if (s.Enabled) ApplyAndReport(mon, s);
                        C.Save();
                        SendColor();
                        break;
                    }

                case "resetMonitor":
                    {
                        MonitorInfo mon; MonitorSettings s;
                        if (!FindMonitor(Str(m, "key"), out mon, out s)) break;
                        s.ResetValues();
                        if (s.Enabled) ApplyAndReport(mon, s);
                        C.Save();
                        SendColor();
                        break;
                    }

                case "hotkey":
                    {
                        string target = Str(m, "target");
                        int key = Int(m, "key");
                        string msg;
                        if (target == "next") msg = AssignHotkey(key, "next", 0, k => C.NextHotkey = k);
                        else
                        {
                            if (p == null) break;
                            msg = AssignHotkey(key, p, 0, k => p.Hotkey = k);
                        }
                        if (msg != null) { Toast("color", msg, false); SendColor(); }
                        break;
                    }

                case "osd":
                    C.ShowOsd = Bool(m, "value");
                    C.Save();
                    SendColor();
                    break;

                case "resetAll":
                    Applier.ResetAll();
                    Toast("color", "Все мониторы сброшены к стандартным значениям", false);
                    break;
            }
        }

        // Названия профилей уникальны (без учёта регистра) — по ним работают команды деки (--profile "Имя").
        // Занято другим профилем — добавляем « 2», « 3»…
        string UniqueProfileName(string name, Profile except)
        {
            string b = name.Trim(), n = b; int i = 2;
            while (C.Profiles.Exists(x => x != except && string.Equals(x.Name.Trim(), n, StringComparison.OrdinalIgnoreCase)))
                n = b + " " + i++;
            return n;
        }

        // свободная Ctrl+Alt+цифра для нового профиля — с учётом клавиш звука
        int FreeColorHotkey()
        {
            const int ctrlAlt = 0x20000 | 0x40000;
            for (int n = 1; n <= 9; n++)
            {
                int k = ctrlAlt | (0x30 + n);
                bool used = C.NextHotkey == k || C.Profiles.Exists(x => x.Hotkey == k);
                foreach (var a in app.Sound.AllEntries()) for (int i = 0; i < 3; i++) if (a.Get(i) == k) used = true;
                if (!used) return k;
            }
            return 0;
        }

        void ApplyAndReport(MonitorInfo mon, MonitorSettings s)
        {
            if (app.General.ColorOff) return; // «Цвет» выключен — настройки сохраняются, на мониторы не идут
            string err = Applier.ApplyOne(mon, s);
            if (err.Length == 0) { lastColorError = ""; return; }
            if (err.Contains("гамма")) err += ". Windows не даёт ставить слишком сильные значения";
            if (err == lastColorError && (DateTime.Now - lastColorErrorAt).TotalSeconds < 3) return;
            lastColorError = err; lastColorErrorAt = DateTime.Now;
            Toast("color", err, false);
        }

        bool FindMonitor(string key, out MonitorInfo mon, out MonitorSettings s)
        {
            s = null;
            mon = Displays.GetActive().Find(x => x.Key == key);
            var p = C.ActiveProfile;
            if (mon == null || p == null) return false;
            s = p.For(mon, false);
            return true;
        }

        void SendColor()
        {
            if (!ready) return;
            var p = C.ActiveProfile;
            var mons = Displays.GetActive();
            Post("color", new
            {
                type = "state",
                state = new
                {
                    profiles = C.Profiles.Select(x => new { name = x.Name, hotkey = HotkeyText.Format(x.Hotkey) }).ToArray(),
                    active = C.Active,
                    next = HotkeyText.Format(C.NextHotkey),
                    osd = C.ShowOsd,
                    nvapi = Nv.Available,
                    monitors = mons.Select(mon =>
                    {
                        var s = p == null ? MonitorSettings.Default(mon, false) : p.For(mon, false);
                        return new { key = mon.Key, name = mon.Name, enabled = s.Enabled, brightness = s.Brightness, contrast = s.Contrast,
                                     gamma = s.Gamma, vibrance = s.Vibrance, hue = s.Hue };
                    }).ToArray()
                }
            });
        }
    }
}
