using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Qural")]
[assembly: AssemblyProduct("Qural")]
[assembly: AssemblyVersion("1.0.2.0")]
[assembly: AssemblyFileVersion("1.0.2.0")]

namespace Qural
{
    // Qural.exe --uninstall — «Удалить» в «Приложениях» Windows. Работает только для копии, поставленной установщиком
    // (рядом лежит install.txt со списком того, что он поставил).
    static class Uninstaller
    {
        public const string Key = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Qural";

        public static void Run()
        {
            Application.EnableVisualStyles();
            string dir = Path.GetDirectoryName(Application.ExecutablePath);
            string marker = Path.Combine(dir, "install.txt");
            if (!File.Exists(marker))
            {
                MessageBox.Show("Эта копия Qural поставлена не установщиком — удалите её папку вручную:\n" + dir, "Qural", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            if (MessageBox.Show("Удалить Qural с этого компьютера?", "Удаление Qural", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            bool settings = MessageBox.Show("Удалить и настройки Qural (профили цвета, клавиши, записи экрана)?\n\nНет — они останутся, и при новой установке всё будет как было.",
                "Удаление Qural", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) == DialogResult.Yes;

            // работающий Qural и его FFmpeg
            int me = Process.GetCurrentProcess().Id;
            foreach (var name in new[] { "Qural", "ffmpeg" })
                foreach (var p in Process.GetProcessesByName(name))
                {
                    try
                    {
                        if (p.Id == me) continue;
                        string path = p.MainModule.FileName;
                        if (path.StartsWith(dir, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); }
                    }
                    catch (Exception) { }
                }

            try { TrayApp.SetAutostart(false); } catch (Exception) { }

            // «Сеть»: остановить net-merge (он уберёт виртуальный адаптер) и помощник трафика, убрать их задачу Windows
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string ctl = Path.Combine(Path.Combine(local, "net-merge"), "ctl");
                if (Process.GetProcessesByName("net-merge-tray").Length > 0) { Directory.CreateDirectory(ctl); File.WriteAllBytes(Path.Combine(ctl, "off"), new byte[0]); }
                File.WriteAllText(NetMon.StopFile, "");
                for (int i = 0; i < 40 && Process.GetProcessesByName("net-merge-tray").Length > 0; i++) System.Threading.Thread.Sleep(250);
                // задача создана с правами администратора — удалить её можно так же (окно UAC)
                var q = new ProcessStartInfo("schtasks.exe", "/Query /TN \"" + NetMerge.TaskName + "\"") { CreateNoWindow = true, UseShellExecute = false };
                using (var p = Process.Start(q)) { p.WaitForExit(5000); if (p.ExitCode == 0)
                    using (var d = Process.Start(new ProcessStartInfo("schtasks.exe", "/Delete /F /TN \"" + NetMerge.TaskName + "\"") { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden })) d.WaitForExit(30000); }
            }
            catch (Exception) { }

            // что поставил установщик: ярлыки и плагины деки
            foreach (var line in File.ReadAllLines(marker))
            {
                int i = line.IndexOf('=');
                if (i < 0) continue;
                string k = line.Substring(0, i).Trim(), v = line.Substring(i + 1).Trim();
                try
                {
                    if (k == "shortcut" && v.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && File.Exists(v)) File.Delete(v);
                    if (k == "plugin" && v.EndsWith(".ulanziPlugin", StringComparison.OrdinalIgnoreCase) && Directory.Exists(v)) Directory.Delete(v, true);
                }
                catch (Exception) { }
            }

            try { Registry.CurrentUser.DeleteSubKeyTree(Key, false); } catch (Exception) { }

            if (settings)
            {
                foreach (var d in new[] { Store.Dir, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Qural") })
                    try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (Exception) { }
            }

            // папку программы удаляем после выхода (сами себя удалить не можем)
            try
            {
                Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + dir + "\"")
                { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            }
            catch (Exception) { }
            MessageBox.Show("Qural удалён.", "Qural", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
