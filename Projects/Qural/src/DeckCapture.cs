using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;

namespace Qural
{
    // Кнопки «Скриншот» и «Запись экрана» на деке, пока что-то идёт (196×196). Пиксельный стиль, как у статусов Claude:
    // клетка 3 px, шрифт 5×7, чистый чёрный фон, всё белое, цвет — только акцентами; анимация GIF-ом (~33 кадра/с):
    //   выделение — пиксельная стрелка тянет пунктирную рамку («бегущие муравьи»), по углам ручки: голубые —
    //     скриншот (рамка вспыхивает, как затвор), красные — запись (в рамке мигает красная точка); подпись снизу;
    //   отсчёт — крупная пиксельная цифра, под ней 10 красных клеток гаснут за секунду (GIF на каждую цифру);
    //   запись — «● REC» (мигает только точка), крупное время, белая «волна» из столбиков (GIF на 1 с, каждую
    //     секунду новый); пауза — жёлтые «II» и «ПАУЗА», время мигает, столбики лежат серыми клетками.
    static class CaptureTiles
    {
        const int S = 196, D = 3, U = 3;   // D — задержка кадра (1/100 с), U — размер клетки
        static readonly Color W = Color.White, Blue = Color.FromArgb(10, 132, 255), Red = Color.FromArgb(255, 59, 48),
            RedOff = Color.FromArgb(96, 22, 18), Amber = Color.FromArgb(255, 196, 0),
            Dim = Color.FromArgb(70, 70, 72), Gray = Color.FromArgb(150, 150, 154);

        const int ShotN = 60;   // выделение: 1,8 с по кругу
        static byte[] shotGif, pickGif;
        public static byte[] ShotGif() { return shotGif ?? (shotGif = Gif(ShotN, p => Shot(p, Blue, false), true, ShotPalette())); }
        public static byte[] PickGif() { return pickGif ?? (pickGif = Gif(ShotN, p => Shot(p, Red, true), true, ShotPalette())); }
        public static byte[] RecGif(TimeSpan t) { string s = Time(t); return Gif(33, p => Rec(p, s), true, null); }
        public static byte[] PausedGif(TimeSpan t) { string s = Time(t); return Gif(40, p => Paused(p, s), true, null); }
        public static byte[] CountdownGif(int n) { return Gif(30, p => Count(p, n), false, null); }

        // палитра — из кадров, где есть все цвета (цветов мало, палитра выходит точной)
        static int[] ShotPalette() { return new[] { 0, ShotN / 4, (int)(ShotN * .5f), (int)(ShotN * .5f) + 2, 3 * ShotN / 4 }; }

        static byte[] Gif(int n, Func<float, Bitmap> frame, bool loop, int[] pal)
        {
            return GifWriter.Encode(n, i => frame(i / (float)n), i => D, pal ?? new[] { 0, n / 4, n / 2, 3 * n / 4 }, loop, true);
        }

        // ── выделение: стрелка тянет рамку ──
        // клетки считаем от точки (1, 1): клетка c — это x = 1 + 3c
        const int AX = 12, AY = 11, BX = 46, BY = 27;   // рамка от (AX, AY) до (BX, BY), в клетках
        static Bitmap Shot(float p, Color accent, bool rec)
        {
            var b = New(); using (var g = Graphics.FromImage(b))
            {
                // 0–.08 стрелка на месте; .08–.5 тянет; .5 — готово (вспышка / точка); .86–1 рамка пропала, стрелка едет назад
                float drag = Smooth((p - .08f) / .42f);
                int ex = AX + (int)Math.Round((BX - AX) * drag), ey = AY + (int)Math.Round((BY - AY) * drag);
                int cxp = ex, cyp = ey;
                bool shown = p < .86f && drag > 0;
                if (p >= .86f)
                {
                    float back = Smooth((p - .86f) / .14f);
                    cxp = BX + (int)Math.Round((AX - BX) * back); cyp = BY + (int)Math.Round((AY - BY) * back);
                }
                int fi = (int)Math.Round(p * ShotN) - (int)(ShotN * .5f);   // номер кадра после «готово»
                if (shown)
                {
                    if (!rec && fi >= 0 && fi < 5) Fill(g, AX, AY, ex, ey, fi < 1 ? W : fi < 3 ? Gray : Dim);   // затвор: белая вспышка, гаснет
                    Ants(g, AX, AY, ex, ey, (int)Math.Round(p * ShotN) / 3, drag >= 1 ? W : Gray);
                    if (drag >= 1)
                    {
                        Handle(g, AX, AY, accent); Handle(g, ex, AY, accent); Handle(g, AX, ey, accent); Handle(g, ex, ey, accent);
                        if (rec && fi >= 0)
                        {
                            bool on = (fi / 5) % 2 == 0;   // мигает
                            Dot(g, (AX + BX) / 2 - 2, (AY + BY) / 2 - 2, on ? Red : RedOff);
                        }
                    }
                }
                Arrow(g, cxp, cyp);
                Label(g, rec ? "ЗАПИСЬ" : "СКРИН", S / 2, 128, 3, W);
            }
            return b;
        }

        // пунктир по краю рамки; off — сдвиг, муравьи бегут по часовой
        static void Ants(Graphics g, int x0, int y0, int x1, int y1, int off, Color c)
        {
            if (x1 <= x0 || y1 <= y0) { Cell(g, x0, y0, c); return; }
            var pts = new List<Point>();
            for (int x = x0; x < x1; x++) pts.Add(new Point(x, y0));
            for (int y = y0; y < y1; y++) pts.Add(new Point(x1, y));
            for (int x = x1; x > x0; x--) pts.Add(new Point(x, y1));
            for (int y = y1; y > y0; y--) pts.Add(new Point(x0, y));
            for (int k = 0; k < pts.Count; k++)
                if (((k - off) % 4 + 4) % 4 < 2) Cell(g, pts[k].X, pts[k].Y, c);
        }

        static void Fill(Graphics g, int x0, int y0, int x1, int y1, Color c)
        {
            using (var br = new SolidBrush(c)) g.FillRectangle(br, 1 + x0 * U, 1 + y0 * U, (x1 - x0 + 1) * U, (y1 - y0 + 1) * U);
        }

        static void Handle(Graphics g, int x, int y, Color c)   // ручка 2×2 клетки на углу
        {
            using (var br = new SolidBrush(c)) g.FillRectangle(br, 1 + x * U - 2, 1 + y * U - 2, 2 * U + 1, 2 * U + 1);
        }

        static readonly string[] ArrowSp = {   // # — белый контур, o — тёмная заливка
            "#......", "##.....", "#o#....", "#oo#...", "#ooo#..", "#oooo#.", "#oo####", "#o#o#..", "##.#o#.", "....##." };
        static void Arrow(Graphics g, int x, int y)   // остриё — в клетке (x, y)
        {
            using (var w = new SolidBrush(W)) using (var d = new SolidBrush(Dim))
                for (int j = 0; j < ArrowSp.Length; j++)
                    for (int i = 0; i < ArrowSp[j].Length; i++)
                    {
                        char ch = ArrowSp[j][i];
                        if (ch != '.') g.FillRectangle(ch == '#' ? w : d, 1 + (x + i) * U, 1 + (y + j) * U, U, U);
                    }
        }

        static readonly string[] DotSp = { ".###.", "#####", "#####", "#####", ".###." };
        static void Dot(Graphics g, int x, int y, Color c)
        {
            using (var br = new SolidBrush(c))
                for (int j = 0; j < 5; j++) for (int i = 0; i < 5; i++)
                        if (DotSp[j][i] == '#') g.FillRectangle(br, 1 + (x + i) * U, 1 + (y + j) * U, U, U);
        }

        static void Cell(Graphics g, int x, int y, Color c) { using (var br = new SolidBrush(c)) g.FillRectangle(br, 1 + x * U, 1 + y * U, U, U); }

        // ── отсчёт: крупная цифра, 10 клеток гаснут за секунду ──
        static Bitmap Count(float p, int n)
        {
            var b = New(); using (var g = Graphics.FromImage(b))
            {
                int s = p < .1f ? 14 : 12;   // «щелчок» в начале секунды
                Label(g, n.ToString(), S / 2, 72 - 7 * s / 2, s, W);
                int lit = (int)Math.Ceiling(10 * (1 - p) - 1e-4);
                const int sq = 9, gap = 3, y = 138; int x0 = (S - (10 * sq + 9 * gap)) / 2;
                using (var on = new SolidBrush(Red)) using (var off = new SolidBrush(Dim))
                    for (int i = 0; i < 10; i++) g.FillRectangle(i < lit ? on : off, x0 + i * (sq + gap), y, sq, sq);
            }
            return b;
        }

        // ── запись ──
        static Bitmap Rec(float p, string time)
        {
            var b = New(); using (var g = Graphics.FromImage(b))
            {
                // «● REC»: точка 5×5 клеток и надпись — по центру
                int w = 5 * U + 3 * U + TextW("REC", 3), x = (S - w) / 2;
                DotPx(g, x, 37, p < .6f ? Red : RedOff);   // мигает только точка
                Text(g, "REC", x + 8 * U, 34, 3, W);
                TimeText(g, time, W);
                Bars(g, p, false);
            }
            return b;
        }

        static Bitmap Paused(float p, string time)
        {
            var b = New(); using (var g = Graphics.FromImage(b))
            {
                int w = 5 * U + 3 * U + TextW("ПАУЗА", 3), x = (S - w) / 2;
                using (var br = new SolidBrush(Amber))   // жёлтые «II»
                {
                    g.FillRectangle(br, x, 34, 2 * U, 7 * U);
                    g.FillRectangle(br, x + 3 * U, 34, 2 * U, 7 * U);
                }
                Text(g, "ПАУЗА", x + 8 * U, 34, 3, W);
                TimeText(g, time, p < .55f ? W : Gray);
                Bars(g, p, true);
            }
            return b;
        }

        // 9 столбиков по 3 клетки; высота — нечётное число клеток (1…11), к краям ниже; на паузе — по одной серой клетке
        static void Bars(Graphics g, float p, bool still)
        {
            const int n = 9, bw = 3 * U, gap = 2 * U, mid = 145;   // mid — верх средней клетки
            int x0 = (S - (n * bw + (n - 1) * gap)) / 2;
            double ph = p * Math.PI * 2;
            using (var br = new SolidBrush(still ? Dim : W))
                for (int i = 0; i < n; i++)
                {
                    int h = 1;
                    if (!still)
                    {
                        double a = Math.Abs(Math.Sin(ph * 2 + i * .7) * .6 + Math.Sin(ph + i * 1.4) * .4);
                        a = .25 + .75 * a;
                        a *= 1 - Math.Abs(i - (n - 1) / 2.0) / ((n - 1) / 2.0) * .35;
                        h = 1 + 2 * (int)Math.Round(5 * Math.Min(1, a * 1.3));
                    }
                    g.FillRectangle(br, x0 + i * (bw + gap), mid - (h - 1) / 2 * U, bw, h * U);
                }
        }

        static void TimeText(Graphics g, string time, Color c)
        {
            int s = time.Length > 5 ? 4 : 5;
            Label(g, time, S / 2, 89 - 7 * s / 2, s, c);
        }

        static string Time(TimeSpan t)
        {
            int h = (int)t.TotalHours;
            return h > 0 ? h + ":" + t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00")
                         : t.Minutes.ToString("00") + ":" + t.Seconds.ToString("00");
        }

        // ── пиксельный шрифт 5×7 — тот же, что у плагинов (plugin\pixpng.js); двоеточие узкое, в 1 столбец ──
        static readonly Dictionary<char, string[]> Font = new Dictionary<char, string[]>
        {
            { '0', new[] { ".###.", "#...#", "#..##", "#.#.#", "##..#", "#...#", ".###." } },
            { '1', new[] { "..#..", ".##..", "..#..", "..#..", "..#..", "..#..", ".###." } },
            { '2', new[] { ".###.", "#...#", "....#", "...#.", "..#..", ".#...", "#####" } },
            { '3', new[] { "####.", "....#", "....#", ".###.", "....#", "....#", "####." } },
            { '4', new[] { "...#.", "..##.", ".#.#.", "#..#.", "#####", "...#.", "...#." } },
            { '5', new[] { "#####", "#....", "####.", "....#", "....#", "#...#", ".###." } },
            { '6', new[] { "..##.", ".#...", "#....", "####.", "#...#", "#...#", ".###." } },
            { '7', new[] { "#####", "....#", "...#.", "..#..", ".#...", ".#...", ".#..." } },
            { '8', new[] { ".###.", "#...#", "#...#", ".###.", "#...#", "#...#", ".###." } },
            { '9', new[] { ".###.", "#...#", "#...#", ".####", "....#", "...#.", ".##.." } },
            { ':', new[] { ".", "#", "#", ".", "#", "#", "." } },
            { 'R', new[] { "####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#" } },
            { 'E', new[] { "#####", "#....", "#....", "####.", "#....", "#....", "#####" } },
            { 'C', new[] { ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###." } },
            { 'С', new[] { ".###.", "#...#", "#....", "#....", "#....", "#...#", ".###." } },
            { 'К', new[] { "#...#", "#..#.", "#.#..", "##...", "#.#..", "#..#.", "#...#" } },
            { 'Р', new[] { "####.", "#...#", "#...#", "####.", "#....", "#....", "#...." } },
            { 'И', new[] { "#...#", "#...#", "#..##", "#.#.#", "##..#", "#...#", "#...#" } },
            { 'Н', new[] { "#...#", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" } },
            { 'З', new[] { ".###.", "#...#", "....#", "..##.", "....#", "#...#", ".###." } },
            { 'А', new[] { ".###.", "#...#", "#...#", "#####", "#...#", "#...#", "#...#" } },
            { 'П', new[] { "#####", "#...#", "#...#", "#...#", "#...#", "#...#", "#...#" } },
            { 'Ь', new[] { "#....", "#....", "#....", "####.", "#...#", "#...#", "####." } },
            { 'У', new[] { "#...#", "#...#", "#...#", ".####", "....#", "#...#", ".###." } },
        };

        static int TextW(string t, int s)
        {
            int w = 0;
            foreach (char ch in t) { string[] gl; w += (Font.TryGetValue(ch, out gl) ? gl[0].Length : 5) + 1; }
            return Math.Max(0, w - 1) * s;
        }

        static void Text(Graphics g, string t, int x, int y, int s, Color c)
        {
            using (var br = new SolidBrush(c))
                foreach (char ch in t)
                {
                    string[] gl;
                    if (!Font.TryGetValue(ch, out gl)) { x += 6 * s; continue; }
                    for (int j = 0; j < 7; j++) for (int i = 0; i < gl[j].Length; i++)
                            if (gl[j][i] == '#') g.FillRectangle(br, x + i * s, y + j * s, s, s);
                    x += (gl[0].Length + 1) * s;
                }
        }

        static void Label(Graphics g, string t, int centerX, int y, int s, Color c) { Text(g, t, centerX - TextW(t, s) / 2, y, s, c); }

        static void DotPx(Graphics g, int x, int y, Color c)
        {
            using (var br = new SolidBrush(c))
                for (int j = 0; j < 5; j++) for (int i = 0; i < 5; i++)
                        if (DotSp[j][i] == '#') g.FillRectangle(br, x + i * U, y + j * U, U, U);
        }

        static float Smooth(float x) { x = Math.Max(0, Math.Min(1, x)); return x * x * (3 - 2 * x); }

        static Bitmap New()
        {
            var b = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b)) g.Clear(Color.Black);
            return b;
        }
    }
}
