using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Windows.Forms;

namespace Qural
{
    // ───────────── горячие клавиши: текст и правила ─────────────
    static class HotkeyText
    {
        public static string Format(int key)
        {
            if (key == 0) return "";
            var k = (Keys)key;
            var sb = new StringBuilder();
            if ((k & Keys.Control) != 0) sb.Append("Ctrl+");
            if ((k & Keys.Shift) != 0) sb.Append("Shift+");
            if ((k & Keys.Alt) != 0) sb.Append("Alt+");
            sb.Append(KeyName(k & Keys.KeyCode));
            return sb.ToString();
        }

        static string KeyName(Keys code)
        {
            if (code >= Keys.D0 && code <= Keys.D9) return ((int)(code - Keys.D0)).ToString();
            if (code >= Keys.NumPad0 && code <= Keys.NumPad9) return "Num " + (int)(code - Keys.NumPad0);
            switch (code)
            {
                case Keys.Multiply: return "Num *";
                case Keys.Add: return "Num +";
                case Keys.Subtract: return "Num -";
                case Keys.Divide: return "Num /";
                case Keys.Decimal: return "Num .";
                case Keys.Oemtilde: return "~";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.OemCloseBrackets: return "]";
                case Keys.OemSemicolon: return ";";
                case Keys.OemQuotes: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.OemPipe: return "\\";
                case Keys.Up: return "↑";
                case Keys.Down: return "↓";
                case Keys.Left: return "←";
                case Keys.Right: return "→";
                case Keys.PageUp: return "PageUp";
                case Keys.PageDown: return "PageDown";
                case Keys.Scroll: return "ScrollLock";
                case Keys.PrintScreen: return "PrtSc";
                case Keys.Back: return "Backspace";
                case Keys.Return: return "Enter";
                case Keys.VolumeUp: return "Громкость +";
                case Keys.VolumeDown: return "Громкость −";
                case Keys.VolumeMute: return "Без звука";
                case Keys.MediaPlayPause: return "Пауза";
                case Keys.MediaNextTrack: return "След. трек";
                case Keys.MediaPreviousTrack: return "Пред. трек";
                default: return code.ToString();
            }
        }

        // Без Ctrl/Alt/Shift можно только клавиши, которые обычно ни для чего не заняты.
        public static bool CanBeAlone(Keys code)
        {
            return (code >= Keys.F1 && code <= Keys.F24)
                || (code >= Keys.NumPad0 && code <= Keys.NumPad9)
                || code == Keys.Multiply || code == Keys.Add || code == Keys.Subtract || code == Keys.Divide
                || code == Keys.Pause || code == Keys.Scroll || code == Keys.Insert || code == Keys.PrintScreen;
        }

        // Проверка сочетания; null — подходит, иначе текст, что не так.
        public static string Check(int key)
        {
            var k = (Keys)key;
            Keys code = k & Keys.KeyCode, mods = k & Keys.Modifiers;
            if (code == Keys.None) return null;
            if (mods == Keys.None && !CanBeAlone(code)) return "Добавьте Ctrl, Alt или Shift";
            if (mods == Keys.Shift && !CanBeAlone(code)) return "Только Shift нельзя — добавьте Ctrl или Alt";
            return null;
        }
    }

    // ───────────── хранение ─────────────
    static class Store
    {
        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qural"); } }

        public static T Load<T>(string name, string migrateFrom) where T : class
        {
            string path = Path.Combine(Dir, name);
            try
            {
                // первый запуск Qural — берём настройки старой программы (ColorSwitch / MixKeys), старый файл не трогаем
                if (!File.Exists(path) && migrateFrom != null && File.Exists(migrateFrom))
                {
                    Directory.CreateDirectory(Dir);
                    File.Copy(migrateFrom, path);
                }
            }
            catch (Exception) { }
            if (!File.Exists(path)) return null;

            // При старте Windows файл может быть на миг занят (антивирус, OneDrive) — несколько попыток.
            // Не читается совсем — берём резервную копию .bak. Не вышло и с ней — запоминаем, что файл
            // испорчен, и НЕ перезаписываем его (иначе профили пропадут насовсем).
            for (int attempt = 0; attempt < 10; attempt++)
            {
                try
                {
                    using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                        return (T)Serializer(typeof(T)).ReadObject(fs);
                }
                catch (IOException) { System.Threading.Thread.Sleep(300); }
                catch (UnauthorizedAccessException) { System.Threading.Thread.Sleep(300); }
                catch (Exception) { break; } // файл прочитался, но разобрать не вышло
            }
            try { File.Copy(path, path + ".bad", true); } catch (Exception) { }
            try
            {
                if (File.Exists(path + ".bak"))
                    using (var fs = File.OpenRead(path + ".bak"))
                    {
                        var res = (T)Serializer(typeof(T)).ReadObject(fs);
                        LoadNotes.Add(name + ": файл не прочитался, взята резервная копия");
                        return res;
                    }
            }
            catch (Exception) { }
            Broken.Add(name);
            LoadNotes.Add(name + ": файл не прочитался — Qural не будет его перезаписывать, копия: " + name + ".bad");
            return null;
        }

        // файлы, которые не удалось прочитать: их не перезаписываем, пока пользователь сам что-то не изменит
        public static readonly HashSet<string> Broken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public static readonly List<string> LoadNotes = new List<string>();
        public static string LastError;

        // force — пользователь сам что-то поменял: сохраняем, даже если файл при загрузке не прочитался
        public static void Save(string name, object o) { Save(name, o, true); }

        public static void Save(string name, object o, bool force)
        {
            if (!force && Broken.Contains(name)) return;
            string path = Path.Combine(Dir, name), tmp = path + ".tmp";
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        using (var w = JsonReaderWriterFactory.CreateJsonWriter(fs, new UTF8Encoding(false), false, true, "  "))
                        {
                            Serializer(o.GetType()).WriteObject(w, o);
                            w.Flush();
                        }
                        fs.Flush(true); // на диск, а не в кэш — чтобы пережить внезапное выключение
                    }
                    // прежний файл остаётся резервной копией .bak
                    if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", true);
                    else File.Move(tmp, path);
                    Broken.Remove(name);
                    LastError = null;
                    return;
                }
                catch (Exception e)
                {
                    LastError = name + ": " + e.Message;
                    System.Threading.Thread.Sleep(100);
                }
            }
        }

        static DataContractJsonSerializer Serializer(Type t)
        {
            return new DataContractJsonSerializer(t, new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
        }

        public static string Old(string app) { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), app, "config.json"); }
    }

    // ───────────── цвет ─────────────
    [DataContract]
    class MonitorSettings
    {
        [DataMember] public string Key;
        [DataMember] public string Name;
        [DataMember] public bool Enabled;
        [DataMember] public int Brightness = 50;
        [DataMember] public int Contrast = 50;
        [DataMember] public double Gamma = 1.0;
        [DataMember] public int Vibrance = 50;
        [DataMember] public int Hue = 0;

        public static MonitorSettings Default(MonitorInfo m, bool enabled)
        {
            return new MonitorSettings { Key = m.Key, Name = m.Name, Enabled = enabled, Brightness = 50, Contrast = 50, Gamma = 1.0, Vibrance = 50, Hue = 0 };
        }

        public void ResetValues() { Brightness = 50; Contrast = 50; Gamma = 1.0; Vibrance = 50; Hue = 0; }
        public MonitorSettings Clone() { return (MonitorSettings)MemberwiseClone(); }
    }

    [DataContract]
    class Profile
    {
        [DataMember] public string Name;
        [DataMember] public int Hotkey;
        [DataMember] public List<MonitorSettings> Monitors = new List<MonitorSettings>();

        public MonitorSettings For(MonitorInfo m, bool createEnabled)
        {
            if (Monitors == null) Monitors = new List<MonitorSettings>();
            var s = Monitors.Find(x => x.Key == m.Key);
            if (s == null) { s = MonitorSettings.Default(m, createEnabled); Monitors.Add(s); }
            s.Name = m.Name;
            return s;
        }

        public Profile Clone(string newName)
        {
            var p = new Profile { Name = newName };
            foreach (var s in Monitors) p.Monitors.Add(s.Clone());
            return p;
        }
    }

    [DataContract]
    class ColorConfig
    {
        [DataMember] public List<Profile> Profiles = new List<Profile>();
        [DataMember] public int Active;
        [DataMember] public bool ShowOsd = true;
        [DataMember] public int NextHotkey;
        [DataMember] public int HotkeysVersion;

        const int CtrlAlt = (int)(Keys.Control | Keys.Alt);

        public Profile ActiveProfile
        {
            get
            {
                if (Profiles.Count == 0) return null;
                if (Active < 0 || Active >= Profiles.Count) Active = 0;
                return Profiles[Active];
            }
        }

        public static ColorConfig Load()
        {
            var c = Store.Load<ColorConfig>("color.json", Store.Old("ColorSwitch")) ?? new ColorConfig { ShowOsd = true };
            if (c.Profiles == null) c.Profiles = new List<Profile>();
            foreach (var p in c.Profiles) if (p.Monitors == null) p.Monitors = new List<MonitorSettings>();
            if (c.Profiles.Count == 0)
            {
                var p = new Profile { Name = "Обычный" };
                foreach (var m in Displays.GetActive()) p.For(m, true);
                c.Profiles.Add(p);
                c.Active = 0;
            }
            if (c.HotkeysVersion < 1)
            {
                for (int i = 0; i < c.Profiles.Count && i < 9; i++)
                    if (c.Profiles[i].Hotkey == 0) c.Profiles[i].Hotkey = CtrlAlt | (int)(Keys.D1 + i);
                c.NextHotkey = CtrlAlt | (int)Keys.D0;
                c.HotkeysVersion = 1;
            }
            return c;
        }

        public void Save() { Store.Save("color.json", this); }
        // сохранение «на всякий случай» (старт, выход): испорченный при загрузке файл не трогаем
        public void SaveQuiet() { Store.Save("color.json", this, false); }
    }

    static class Applier
    {
        public static string Apply(Profile p)
        {
            if (p == null) return "";
            var errors = new List<string>();
            foreach (var m in Displays.GetActive())
            {
                var s = p.Monitors.Find(x => x.Key == m.Key);
                if (s == null || !s.Enabled) continue;
                string e = ApplyOne(m, s);
                if (e.Length > 0) errors.Add(e);
            }
            return string.Join("\n", errors.ToArray());
        }

        public static string ApplyOne(MonitorInfo m, MonitorSettings s)
        {
            var bad = new List<string>();
            if (!Gamma.Apply(m.GdiName, s.Brightness, s.Contrast, s.Gamma)) bad.Add("яркость/контраст/гамма");
            if (Nv.Available)
            {
                if (!Nv.SetVibrance(m.GdiName, s.Vibrance)) bad.Add("интенсивность");
                if (!Nv.SetHue(m.GdiName, s.Hue)) bad.Add("оттенок");
            }
            return bad.Count == 0 ? "" : m.Name + ": не применилось — " + string.Join(", ", bad.ToArray());
        }

        public static void ResetAll()
        {
            foreach (var m in Displays.GetActive())
            {
                Gamma.Apply(m.GdiName, 50, 50, 1.0);
                if (Nv.Available) { Nv.SetVibrance(m.GdiName, 50); Nv.SetHue(m.GdiName, 0); }
            }
        }
    }

    // ───────────── звук ─────────────
    [DataContract]
    class AppEntry
    {
        [DataMember] public string Process;   // основное имя exe без .exe
        [DataMember] public List<string> Extra = new List<string>();
        [DataMember] public string Title;
        [DataMember] public int Up, Down, Mute;
        [DataMember] public int Step = 5;
        [DataMember] public bool Repeat = true;

        public int Get(int action) { return action == 0 ? Up : action == 1 ? Down : Mute; }
        public void Set(int action, int key) { if (action == 0) Up = key; else if (action == 1) Down = key; else Mute = key; }

        public string[] Names()
        {
            var list = new List<string>();
            if (!string.IsNullOrEmpty(Process)) list.Add(Process);
            if (Extra != null)
                foreach (var e in Extra)
                    if (!string.IsNullOrEmpty(e) && !list.Exists(x => string.Equals(x, e, StringComparison.OrdinalIgnoreCase))) list.Add(e);
            return list.ToArray();
        }

        public bool Has(string exe) { return Array.Exists(Names(), n => string.Equals(n, exe, StringComparison.OrdinalIgnoreCase)); }
    }

    [DataContract]
    class SoundConfig
    {
        [DataMember] public List<AppEntry> Apps = new List<AppEntry>();
        [DataMember] public AppEntry Focused;
        [DataMember] public int Active;          // −2 — «Активное окно»
        [DataMember] public Dictionary<string, float> MuteMemo = new Dictionary<string, float>();
        // программы (имя exe без .exe), которым меняли громкость клавишами «Активного окна» — чтобы их можно было найти и вернуть
        [DataMember] public List<string> Touched = new List<string>();
        // Перенос изменений, сделанных до появления списка: пока не прошёл этот срок, при каждом снимке микшера
        // программы с громкостью не 100% (и без своей строки) попадают в список. Одного снимка при старте мало —
        // нужная программа могла быть закрыта или молчать.
        [DataMember] public DateTime TouchedSeedUntil;
        // убраны из списка руками — перенос их больше не добавляет (а клавиши «Активного окна» — добавят снова)
        [DataMember] public List<string> TouchedIgnored = new List<string>();

        public const int FocusedIndex = -2;

        // клавишей «Активного окна» поменяли громкость. true — программа добавлена в список впервые
        public bool Touch(string process)
        {
            if (string.IsNullOrEmpty(process)) return false;
            TouchedIgnored.RemoveAll(x => string.Equals(x, process, StringComparison.OrdinalIgnoreCase));
            if (Touched.Exists(x => string.Equals(x, process, StringComparison.OrdinalIgnoreCase))) return false;
            Touched.Add(process);
            return true;
        }

        public void Untouch(string process)
        {
            Touched.RemoveAll(x => string.Equals(x, process, StringComparison.OrdinalIgnoreCase));
        }

        // пользователь сам убрал из списка
        public void Forget(string process)
        {
            Untouch(process);
            if (!TouchedIgnored.Exists(x => string.Equals(x, process, StringComparison.OrdinalIgnoreCase))) TouchedIgnored.Add(process);
        }

        // перенос по снимку микшера; true — что-то добавили (надо сохранить и обновить страницу)
        public bool Seed(List<Audio.Info> snap)
        {
            if (DateTime.Now > TouchedSeedUntil) return false;
            bool added = false;
            foreach (var i in snap)
            {
                if (i.Volume >= 0.995f && !i.Muted) continue;
                if (Apps.Exists(a => a.Has(i.Process))) continue;
                if (TouchedIgnored.Exists(x => string.Equals(x, i.Process, StringComparison.OrdinalIgnoreCase))) continue;
                if (Touched.Exists(x => string.Equals(x, i.Process, StringComparison.OrdinalIgnoreCase))) continue;
                Touched.Add(i.Process);
                added = true;
            }
            return added;
        }

        public IEnumerable<AppEntry> AllEntries()
        {
            foreach (var a in Apps) yield return a;
            yield return Focused;
        }

        public static SoundConfig Load()
        {
            var c = Store.Load<SoundConfig>("sound.json", Store.Old("MixKeys")) ?? new SoundConfig();
            if (c.Apps == null) c.Apps = new List<AppEntry>();
            if (c.MuteMemo == null) c.MuteMemo = new Dictionary<string, float>();
            if (c.Touched == null) c.Touched = new List<string>();
            if (c.TouchedIgnored == null) c.TouchedIgnored = new List<string>();
            if (c.TouchedSeedUntil == default(DateTime)) c.TouchedSeedUntil = DateTime.Now.AddDays(14); // первый запуск с этой функцией
            if (c.Focused == null) c.Focused = new AppEntry { Process = "", Title = "Активное окно", Step = 5, Repeat = true };
            foreach (var a in c.AllEntries())
            {
                if (a.Step < 1 || a.Step > 50) a.Step = 5;
                if (a.Extra == null) a.Extra = new List<string>();
                if (string.IsNullOrEmpty(a.Title)) a.Title = a.Process;
            }
            if (c.Active == -1) c.Active = c.Apps.Count > 0 ? 0 : FocusedIndex; // «Общие» теперь отдельная вкладка
            return c;
        }

        public void Save() { Store.Save("sound.json", this); }
        // сохранение «на всякий случай» (старт, выход): испорченный при загрузке файл не трогаем
        public void SaveQuiet() { Store.Save("sound.json", this, false); }
    }

    // ───────────── общее ─────────────
    [DataContract]
    class GeneralConfig
    {
        [DataMember] public string TrayClick = "open"; // "open" — открыть окно, "next" — следующий профиль цвета
        [DataMember] public string Page = "home";      // открытая страница окна: home / color / sound / general
        [DataMember] public bool ColorOff, SoundOff;   // инструмент выключен целиком
        [DataMember] public bool ShotOff, RecOff;
        [DataMember] public bool NetOff;               // «Сеть» (net-merge) выключена
        [DataMember] public string TrayTheme;          // тема меню трея: как в окне (dark / light), null — как у Windows
        [DataMember] public bool NoUpdateCheck;        // не проверять обновления на GitHub
        [DataMember] public string UpdateNotified;     // о какой версии уже напомнили в трее (второй раз не напоминаем)
        // Первый запуск: "pending" — окно спросит, какие инструменты включить. У старых настроек поля нет (null) — уже настроено.
        [DataMember] public string Setup;

        public static GeneralConfig Load()
        {
            var c = Store.Load<GeneralConfig>("general.json", null);
            // нет файла и он не испорчен — Qural запускают впервые
            if (c == null) c = new GeneralConfig { Setup = Store.Broken.Contains("general.json") ? null : "pending" };
            if (c.TrayClick != "next") c.TrayClick = "open";
            if (c.Page != "color" && c.Page != "sound" && c.Page != "shot" && c.Page != "rec" && c.Page != "net" && c.Page != "general") c.Page = "home";
            return c;
        }

        public void Save() { Store.Save("general.json", this); }
        // сохранение «на всякий случай» (старт, выход): испорченный при загрузке файл не трогаем
        public void SaveQuiet() { Store.Save("general.json", this, false); }
    }
}
