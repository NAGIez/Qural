using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Qural
{
    // Ярлыки на команды Qural — для действия «Open» в Ulanzi Deck / Stream Deck (оно открывает файл, а ярлык
    // уже содержит параметры). Папка «Ярлыки для деки» рядом с Qural.exe; при каждом создании собирается заново.
    static class Shortcuts
    {
        public static string Dir { get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Ярлыки для деки"); } }

        // items: [{ label, args }] со страницы. Возвращает папку.
        public static string Create(object[] items)
        {
            string dir = Dir;
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir, "*.lnk")) File.Delete(old); // только наши ярлыки — папка своя

            Type shellType = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(shellType);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (var o in items)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string label = d.ContainsKey("label") && d["label"] != null ? d["label"].ToString() : "";
                    string args = d.ContainsKey("args") && d["args"] != null ? d["args"].ToString() : "";
                    // только команды Qural: начинаются с -- и без переводов строк
                    if (!args.StartsWith("--") || args.Length > 300 || args.IndexOfAny(new[] { '\r', '\n' }) >= 0) continue;

                    string name = FileName(label);
                    string unique = name; int n = 2;
                    while (!used.Add(unique)) unique = name + " (" + n++ + ")";
                    string path = Path.Combine(dir, unique + ".lnk");

                    object lnk = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                    try
                    {
                        Type t = lnk.GetType();
                        t.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath });
                        t.InvokeMember("Arguments", BindingFlags.SetProperty, null, lnk, new object[] { args });
                        t.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Path.GetDirectoryName(Application.ExecutablePath) });
                        t.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { Application.ExecutablePath + ",0" });
                        t.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Qural: " + label });
                        t.InvokeMember("WindowStyle", BindingFlags.SetProperty, null, lnk, new object[] { 7 }); // свёрнуто — без мелькания
                        t.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                    }
                    finally { Marshal.ReleaseComObject(lnk); }
                }
            }
            finally { Marshal.ReleaseComObject(shell); }
            return dir;
        }

        // «Spotify · громче» → «Spotify — громче»; убрать символы, запрещённые в именах файлов
        static string FileName(string label)
        {
            string s = (label ?? "команда").Replace(" · ", " — ").Replace("«", "").Replace("»", "").Replace("/", "или");
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c.ToString(), "");
            s = s.Trim().TrimEnd('.');
            if (s.Length > 80) s = s.Substring(0, 80).Trim();
            return s.Length == 0 ? "команда" : s;
        }
    }
}
