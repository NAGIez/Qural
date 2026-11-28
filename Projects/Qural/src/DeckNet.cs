using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace Qural
{
    // Кнопка «Сеть» на деке (196×196, чёрный фон) — в стиле «Нагрузки ПК»: пиксельный шрифт, три строки.
    //   ↓ приём и ↑ отдача: стрелка (синяя / оранжевая) и «МБИТ» слева, скорость крупно справа, под ней полоска —
    //     скорость от пика за последние 2 минуты;
    //   состояние net-merge слева (СЕТЬ — зелёным, ПАУЗА / ЖДЁТ / ЗАПУСК — жёлтым, ВЫКЛ — серым), справа — сколько
    //   подключений объединено из доступных («2/3»), полоска — доля приёма каждого подключения (белые и серые куски).
    // Нажатие — включить / пауза (или запустить). Поля ~28 px — края экрана кнопки скруглены.
    static class NetTiles
    {
        const int S = 196, X0 = 28, XR = 169, BarW = 141;
        static readonly Color W = Color.White, Dim = Color.FromArgb(150, 150, 156), Off = Color.FromArgb(48, 48, 50),
            Blue = Color.FromArgb(10, 132, 255), Orange = Color.FromArgb(255, 159, 10),
            Green = Color.FromArgb(52, 199, 89), Yellow = Color.FromArgb(255, 196, 0);
        static readonly Color[] Parts = { W, Color.FromArgb(160, 160, 166), Color.FromArgb(220, 220, 226), Color.FromArgb(110, 110, 116) };

        // всё, что видно на кнопке — из этого и картинка, и ключ «поменялось ли»
        sealed class View
        {
            public string Down = "-", Up = "-", State = "", Count = "";
            public Color StateColor = Dim;
            public bool Live, Numbers;
            public int DownFill, UpFill;          // закрашенных квадратиков из 12
            public double[] Share = new double[0];
        }

        static View Make(NetMerge.Snapshot s)
        {
            var v = new View();
            v.Live = s.State == "on";
            switch (s.State)
            {
                case "on": v.State = "СЕТЬ"; v.StateColor = Green; break;
                case "paused": v.State = "ПАУЗА"; v.StateColor = Yellow; break;
                case "waiting": v.State = "ЖДЁТ"; v.StateColor = Yellow; break;
                case "starting": v.State = "ЗАПУСК"; v.StateColor = Yellow; break;
                case "setup": v.State = "ВКЛЮЧИТЬ"; v.StateColor = Yellow; break;   // нажать — Windows спросит разрешение
                case "missing": v.State = "НЕТ МОДУЛЯ"; break;
                default: v.State = "ВЫКЛ"; break;
            }
            v.Numbers = s.State != "missing" && s.State != "off" && s.State != "setup";
            if (!v.Numbers) return v;

            v.Down = NetText.Mbps(s.Down); v.Up = NetText.Mbps(s.Up);
            v.DownFill = Fill(s.Down, s.HistDown); v.UpFill = Fill(s.Up, s.HistUp);

            int present = 0, merged = 0; double sum = 0;
            var downs = new List<double>();
            foreach (var a in s.Adapters)
            {
                if (!a.Present) continue;
                present++; if (a.Merged) merged++;
                downs.Add(Math.Max(0, a.Down)); sum += Math.Max(0, a.Down);
            }
            if (present > 0) v.Count = merged + "/" + present;
            v.Share = new double[downs.Count];
            for (int i = 0; i < downs.Count; i++) v.Share[i] = sum > 0 ? downs[i] / sum : 0;
            return v;
        }

        // полоска: скорость от пика за историю (не меньше 1 Мбит/с, чтобы тишина не выглядела полной полоской)
        static int Fill(double bytesPerSec, float[] hist)
        {
            double now = bytesPerSec * 8 / 1e6, peak = 1;
            if (hist != null) foreach (var h in hist) if (h > peak) peak = h;
            if (now > peak) peak = now;
            return (int)Math.Round(12 * Math.Min(1, now / peak));
        }

        /// Ключ картинки: перерисовываем, только если поменялось то, что видно.
        public static string Key(NetMerge.Snapshot s)
        {
            var v = Make(s);
            var sb = new System.Text.StringBuilder(v.State).Append('|').Append(v.Down).Append('|').Append(v.Up)
                .Append('|').Append(v.DownFill).Append('|').Append(v.UpFill).Append('|').Append(v.Count);
            foreach (var p in v.Share) sb.Append('|').Append((int)Math.Round(p * 12));
            return sb.ToString();
        }

        public static Bitmap Render(NetMerge.Snapshot s)
        {
            var v = Make(s);
            var b = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.Black);
                const int y0 = 36;
                Color num = v.Live ? W : Dim;
                Row(g, y0, "↓", Blue, v.Down, num, v.DownFill, v.Numbers);
                Row(g, y0 + 46, "↑", Orange, v.Up, num, v.UpFill, v.Numbers);

                // состояние и подключения
                int y = y0 + 92;
                PixFont.Text(g, v.State, X0, y, 2, v.StateColor);
                if (v.Count.Length > 0) PixFont.Right(g, v.Count, XR, y - 3, 3, num);
                if (v.Share.Length > 0) PixFont.Split(g, X0, y + 22, BarW, 6, 12, v.Share, Parts, Off);
                else PixFont.Bar(g, X0, y + 22, BarW, 6, 12, 0, W, Off);
            }
            return b;
        }

        // строка: стрелка и «МБИТ» слева, скорость крупно справа, полоска снизу (как «Нагрузка ПК»)
        static void Row(Graphics g, int y, string arrow, Color ac, string value, Color vc, int fill, bool numbers)
        {
            PixFont.Text(g, arrow, X0, y, 2, numbers ? ac : Dim);
            PixFont.Text(g, "МБИТ", X0 + 16, y, 2, Dim);
            if (!numbers) PixFont.Right(g, "-", XR, y - 3, 3, Dim);
            // 4 знака крупно занимают 69 px — «1234» и «12.3» влезают; длиннее — мельче, чтобы не наехать на «МБИТ»
            else if (value.Length <= 4) PixFont.Right(g, value, XR, y - 3, 3, vc);
            else PixFont.Right(g, value, XR, y, 2, vc);
            PixFont.Bar(g, X0, y + 22, BarW, 6, 12, fill / 12.0, numbers ? ac : W, Off);
        }
    }
}
