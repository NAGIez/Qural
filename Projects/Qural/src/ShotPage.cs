using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Forms;

namespace Qural
{
    // Вкладка «Скриншоты»: клавиши, папка, формат, что показывать.
    partial class MainWindow
    {
        ShotConfig Sh { get { return app.Shot; } }

        void OnShot(Dictionary<string, object> m)
        {
            switch (Str(m, "type"))
            {
                case "hotkey":
                    {
                        string target = Str(m, "target"), msg;
                        int key = Int(m, "key");
                        if (target == "region") msg = AssignHotkey(key, "shot-region", 0, k => Sh.Hotkey = k);
                        else if (target == "full") msg = AssignHotkey(key, "shot-full", 0, k => Sh.FullHotkey = k);
                        else if (target == "window") msg = AssignHotkey(key, "shot-window", 0, k => Sh.WindowHotkey = k);
                        else break;
                        if (msg != null) { Toast("shot", msg, false); SendShot(); }
                        break;
                    }

                case "set":
                    {
                        switch (Str(m, "field"))
                        {
                            case "format": Sh.Format = Str(m, "value") == "jpg" ? "jpg" : "png"; break;
                            case "quality": Sh.JpegQuality = Clamp(Int(m, "value"), 10, 100); break;
                            case "cursor": Sh.Cursor = Bool(m, "value"); break;
                            case "keep": Sh.KeepSelection = Bool(m, "value"); break;
                            case "ask": Sh.AskWhere = Bool(m, "value"); break;
                            case "notify": Sh.Notify = Bool(m, "value"); break;
                            default: return;
                        }
                        Sh.Save();
                        SendShot();
                        break;
                    }

                case "folder":
                    using (var d = new FolderBrowserDialog { Description = "Папка для скриншотов", SelectedPath = Sh.FolderPath, ShowNewFolderButton = true })
                    {
                        if (d.ShowDialog(this) != DialogResult.OK) break;
                        Sh.Folder = d.SelectedPath;
                        Sh.Save();
                        SendShot();
                    }
                    break;

                case "folderReset":
                    Sh.Folder = "";
                    Sh.Save();
                    SendShot();
                    break;

                case "openFolder":
                    try
                    {
                        Directory.CreateDirectory(Sh.FolderPath);
                        System.Diagnostics.Process.Start("explorer.exe", "\"" + Sh.FolderPath + "\"");
                    }
                    catch (Exception ex) { Toast("shot", "Папка недоступна: " + ex.Message, false); }
                    break;

                case "snipSettings": // страница Windows, где выключают «Ножницы» на PrtSc — меняет сам пользователь
                    try { System.Diagnostics.Process.Start("ms-settings:easeofaccess-keyboard"); } catch (Exception) { }
                    break;

                case "take":
                    {
                        // окно Qural не должно попасть на снимок — сворачиваем и снимаем чуть позже
                        string mode = Str(m, "mode");
                        WindowState = FormWindowState.Minimized;
                        var t = new Timer { Interval = 350 };
                        t.Tick += (s, e) =>
                        {
                            t.Stop(); t.Dispose();
                            string err = app.ShotCommand(mode);
                            if (err != null && !IsDisposed) Toast("shot", err, false);
                        };
                        t.Start();
                        break;
                    }
            }
        }

        void SendShot()
        {
            if (!ready) return;
            bool lightshot = false;
            try { lightshot = System.Diagnostics.Process.GetProcessesByName("Lightshot").Length > 0; } catch (Exception) { }
            Post("shot", new
            {
                type = "state",
                state = new
                {
                    region = HotkeyText.Format(Sh.Hotkey),
                    full = HotkeyText.Format(Sh.FullHotkey),
                    window = HotkeyText.Format(Sh.WindowHotkey),
                    folder = Sh.FolderPath,
                    customFolder = !string.IsNullOrEmpty(Sh.Folder),
                    format = Sh.Format,
                    quality = Sh.JpegQuality,
                    cursor = Sh.Cursor,
                    keep = Sh.KeepSelection,
                    ask = Sh.AskWhere,
                    notify = Sh.Notify,
                    usesPrtSc = (Sh.Hotkey & 0xFFFF) == (int)Keys.PrintScreen || (Sh.FullHotkey & 0xFFFF) == (int)Keys.PrintScreen || (Sh.WindowHotkey & 0xFFFF) == (int)Keys.PrintScreen,
                    snipping = Shooter.SnippingOnPrtSc(),
                    lightshot = lightshot
                }
            });
        }
    }
}
