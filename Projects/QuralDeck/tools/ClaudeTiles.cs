// Картинки кнопок плагина «Qural — Claude» (196×196, чёрный фон):
//  • статус — белый пиксельный персонаж Claude (по мотивам логотипа Claude Code) с тенью, глазами и анимацией,
//    подпись шрифтом Inter; каждое состояние — GIF (status_*.gif) и её первый кадр (status_*.png);
//  • claude_wide.gif — персонаж гуляет по широкому экрану деки (режим «Своя картинка / GIF»);
//  • разрешить / отклонить / стоп / новый чат / диктовка — белые контурные значки, как в наборе пользователя.
// Сборка и запуск — tools\make-icons.ps1.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

static class ClaudeTiles
{
    const int S = 196;
    static readonly Color W = Color.White, Dim = Color.FromArgb(96, 96, 98), Red = Color.FromArgb(255, 69, 58), Bk = Color.Black;
    const string FontName = "Inter 24pt 24pt SemiBold", FontFallback = "Segoe UI Semibold";
    static string glyphDir, outDir;

    // ════════════ персонаж ════════════
    // Рисуется по клеткам 4×4 px. Тело 24×14 клеток, руки 4×4, четыре ножки 2×4, глаза 2×4.
    enum Eye { Open, Closed, Happy, X, Strain, None }   // None — вид со спины, глаз не видно
    enum Arm { Side, Up, Wave1, Wave2, Down, Flex }

    sealed class Pal { public Color Main, Shade, Eye; }
    static readonly Pal Light = new Pal { Main = W, Shade = Color.FromArgb(178, 178, 182), Eye = Bk };
    static readonly Pal OnPlate = new Pal { Main = Bk, Shade = Color.FromArgb(70, 70, 72), Eye = W };
    static readonly Pal Sleepy = new Pal { Main = Color.FromArgb(110, 110, 114), Shade = Color.FromArgb(72, 72, 76), Eye = Bk };

    sealed class Sp
    {
        public int Dx = 0, Dy;         // сдвиг всего персонажа, клетки
        public int Ex, Ey;             // куда смотрят глаза
        public Eye Eyes = Eye.Open;
        public Arm L = Arm.Side, R = Arm.Side;
        public int Legs;               // 0 — стоит, 1/2 — шаг, 3 — поджал (прыжок)
        public bool Squash;            // присел перед прыжком / при приземлении
        public int Fat;                // раздулся (контекст переполнен): на сколько клеток шире и выше
        public bool Glasses, Crown;    // тёмные очки и корона — «самый важный» (Fable)
        public int GlassesDy, CrownDx; // очки сползли, корона съехала (на пределе)
        public int Stack, StackTilt;   // стопка блоков на голове (контекст) и её крен
        public Sp Clone() { return (Sp)MemberwiseClone(); }
    }

    const int U = 4;
    static Graphics cg; static int cx, cy; static int cu = U;
    static void R(int x, int y, int w, int h, Color c)
    {
        using (var br = new SolidBrush(c)) cg.FillRectangle(br, cx + x * cu, cy + y * cu, w * cu, h * cu);
    }

    // ox, oy — левый верхний угол поля персонажа (36×22 клетки) в пикселях
    static void DrawSprite(Graphics g, Sp s, Pal p, int ox, int oy, int unit)
    {
        cg = g; cu = unit; cx = ox + s.Dx * unit; cy = oy + s.Dy * unit;
        var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
        var pom = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.Half;

        int x0 = 6, x1 = 29, y0 = 4, y1 = 17;                     // тело
        if (s.Squash) { x0 -= 1; x1 += 1; y0 += 2; }
        if (s.Fat > 0) { x0 -= s.Fat; x1 += s.Fat; y0 -= s.Fat; }
        // ножки (под телом)
        int[] lx = { 9, 13, 21, 25 };
        for (int i = 0; i < 4; i++)
        {
            int h = 4;
            if (s.Legs == 3) h = 2;
            else if (s.Legs == 1 && (i == 0 || i == 2)) h = 3;
            else if (s.Legs == 2 && (i == 1 || i == 3)) h = 3;
            if (s.Squash) h = 2;
            R(lx[i], y1 + 1, 2, h, p.Main);
            R(lx[i] + 1, y1 + 1, 1, h, p.Shade);
        }
        // тело со скруглёнными углами и тенью справа и снизу
        R(x0, y0, x1 - x0 + 1, y1 - y0 + 1, p.Main);
        R(x1 - 1, y0 + 1, 2, y1 - y0, p.Shade);
        R(x0 + 1, y1 - 1, x1 - x0, 2, p.Shade);
        Hole(x0, y0); Hole(x1, y0); Hole(x0, y1); Hole(x1, y1);
        // руки
        DrawArm(s.L, true, x0, y0, p);
        DrawArm(s.R, false, x1, y0, p);
        // корона над телом
        if (s.Crown)
        {
            var gold = Color.FromArgb(255, 196, 46); var gold2 = Color.FromArgb(200, 140, 20);
            int cx0 = (x0 + x1) / 2 - 6 + s.CrownDx;
            R(cx0, y0 - 2, 13, 2, gold); R(cx0, y0 - 1, 13, 1, gold2);
            R(cx0, y0 - 5, 2, 3, gold); R(cx0 + 5, y0 - 6, 3, 4, gold); R(cx0 + 11, y0 - 5, 2, 3, gold);
            R(cx0 + 6, y0 - 4, 1, 1, Color.FromArgb(255, 69, 58));
        }
        // стопка блоков на голове: чем больше, тем полнее контекст
        for (int i = 0; i < s.Stack; i++)
        {
            int bw = 12 - (i % 2) * 2, bx = (x0 + x1) / 2 - bw / 2 + (s.StackTilt * (i + 1)) / 2;
            int by0 = y0 - 3 - i * 3;
            R(bx, by0, bw, 2, p.Main); R(bx + bw - 1, by0, 1, 2, p.Shade); R(bx, by0 + 1, bw, 1, p.Shade);
        }
        // глаза
        int ey = y0 + 4 + s.Ey, exl = 10 + s.Ex, exr = 24 + s.Ex;
        if (s.Squash) ey += 0;
        switch (s.Eyes)
        {
            case Eye.Open: R(exl, ey, 2, 4, p.Eye); R(exr, ey, 2, 4, p.Eye); break;
            case Eye.Closed: R(exl - 1, ey + 2, 4, 1, p.Eye); R(exr - 1, ey + 2, 4, 1, p.Eye); break;
            case Eye.Happy:
                foreach (int ex in new[] { exl, exr })
                { R(ex - 1, ey + 3, 1, 1, p.Eye); R(ex, ey + 2, 2, 1, p.Eye); R(ex + 2, ey + 3, 1, 1, p.Eye); }
                break;
            case Eye.Strain:   // зажмурился от натуги: «>» и «<»
                R(exl - 1, ey + 1, 1, 1, p.Eye); R(exl, ey + 2, 2, 1, p.Eye); R(exl - 1, ey + 3, 1, 1, p.Eye);
                R(exr + 2, ey + 1, 1, 1, p.Eye); R(exr, ey + 2, 2, 1, p.Eye); R(exr + 2, ey + 3, 1, 1, p.Eye);
                break;
            case Eye.X:
                foreach (int ex in new[] { exl, exr })
                {
                    R(ex - 1, ey, 1, 1, p.Eye); R(ex + 2, ey, 1, 1, p.Eye);
                    R(ex, ey + 1, 2, 2, p.Eye);
                    R(ex - 1, ey + 3, 1, 1, p.Eye); R(ex + 2, ey + 3, 1, 1, p.Eye);
                }
                break;
        }
        // тёмные очки поверх глаз
        if (s.Glasses)
        {
            int gy = y0 + 4 + s.GlassesDy;
            R(exl - 2, gy, exr - exl + 6, 1, p.Eye);
            R(exl - 2, gy + 1, 5, 3, p.Eye); R(exr - 1, gy + 1, 5, 3, p.Eye);
            R(exl - 1, gy + 1, 1, 1, Color.FromArgb(200, 200, 210)); R(exr, gy + 1, 1, 1, Color.FromArgb(200, 200, 210));
        }
        g.SmoothingMode = mode; g.PixelOffsetMode = pom;
    }

    // угол тела — вырезаем (рисуем цветом фона поверх нельзя: фон бывает белой плашкой) — поэтому
    // углы закрашиваются цветом, который лежит под персонажем
    static Color under = Bk;
    static void Hole(int x, int y) { R(x, y, 1, 1, under); }

    static void DrawArm(Arm a, bool left, int bx, int by, Pal p)
    {
        // bx — край тела (x0 для левой, x1 для правой), by — верх тела
        int sx = left ? bx - 4 : bx + 1;      // начало руки снаружи тела
        switch (a)
        {
            case Arm.Side:
                R(sx, by + 6, 4, 4, p.Main); R(sx, by + 9, 4, 1, p.Shade);
                break;
            case Arm.Down:
                R(left ? bx - 2 : bx + 1, by + 8, 2, 6, p.Main); R(left ? bx - 2 : bx + 2, by + 13, 1, 1, p.Shade);
                break;
            case Arm.Up:
                R(sx, by + 4, 4, 2, p.Main);
                R(left ? sx : sx + 2, by - 3, 2, 7, p.Main);
                R(left ? sx + 1 : sx + 3, by - 3, 1, 7, p.Shade);
                break;
            case Arm.Flex:    // бицепс: толстое плечо и кулак вверх
                R(sx, by + 5, 4, 4, p.Main); R(sx, by + 8, 4, 1, p.Shade);
                R(left ? sx - 2 : sx + 2, by, 4, 6, p.Main);
                R(left ? sx - 2 : sx + 5, by, 1, 6, p.Shade);
                R(left ? sx - 3 : sx + 2, by - 3, 5, 3, p.Main);
                break;
            case Arm.Wave1:   // рука вверх, кисть наружу
                R(sx, by + 4, 4, 2, p.Main);
                R(left ? sx : sx + 2, by - 2, 2, 6, p.Main);
                R(left ? sx - 2 : sx + 4, by - 4, 2, 3, p.Main);
                break;
            case Arm.Wave2:   // рука вверх, кисть внутрь
                R(sx, by + 4, 4, 2, p.Main);
                R(left ? sx + 1 : sx + 1, by - 3, 2, 7, p.Main);
                R(left ? sx + 2 : sx, by - 5, 2, 3, p.Main);
                break;
        }
    }

    // ── мелочи вокруг персонажа (в клетках, от того же угла) ──
    static void Bubbles(int n, Color c)   // мысли: квадратики вверх-вправо
    {
        int[][] pts = { new[] { 31, 2, 2 }, new[] { 33, -1, 3 }, new[] { 35, -5, 4 } };
        for (int i = 0; i < n && i < 3; i++) R(pts[i][0], pts[i][1], pts[i][2], pts[i][2], c);
    }

    static void Sparkle(int x, int y, int size, Color c)   // искра-плюсик
    {
        if (size <= 0) return;
        if (size == 1) { R(x, y, 1, 1, c); return; }
        R(x - 1, y, 3, 1, c); R(x, y - 1, 1, 3, c);
        if (size >= 3) { R(x - 2, y, 1, 1, c); R(x + 2, y, 1, 1, c); R(x, y - 2, 1, 1, c); R(x, y + 2, 1, 1, c); }
    }

    static void Question(int x, int y, Color c)   // «?» 5×7
    {
        string[] q = { ".###.", "#...#", "....#", "..##.", "..#..", ".....", "..#.." };
        for (int j = 0; j < q.Length; j++) for (int i = 0; i < 5; i++) if (q[j][i] == '#') R(x + i, y + j, 1, 1, c);
    }

    static void Zed(int x, int y, int size, Color c)   // «z» size×size
    {
        R(x, y, size, 1, c); R(x, y + size - 1, size, 1, c);
        for (int i = 1; i < size - 1; i++) R(x + size - 1 - i, y + i, 1, 1, c);
    }

    static void Hourglass(int x, int y, int phase, Color c, Color sand)   // песочные часы 5×9
    {
        R(x, y, 5, 1, c); R(x, y + 8, 5, 1, c);
        R(x, y + 1, 1, 3, c); R(x + 4, y + 1, 1, 3, c); R(x + 1, y + 4, 3, 1, c); R(x, y + 5, 1, 3, c); R(x + 4, y + 5, 1, 3, c);
        int top = 3 - phase, bottom = phase;           // песок пересыпается: phase 0..3
        if (top > 0) R(x + 1, y + 4 - top, 3, top, sand);
        if (bottom > 0) R(x + 1, y + 8 - bottom, 3, bottom, sand);
        if (phase > 0 && phase < 3) R(x + 2, y + 5, 1, 2, sand);
    }

    static void Calendar(int x, int y, int page, Color c, Color ink)     // календарь 7×7; page — листок отлетает
    {
        R(x, y + 1, 7, 6, c); R(x, y + 1, 7, 2, Color.FromArgb(255, 69, 58));
        R(x + 1, y, 1, 2, ink); R(x + 5, y, 1, 2, ink);
        R(x + 1, y + 4, 1, 1, ink); R(x + 3, y + 4, 1, 1, ink); R(x + 5, y + 4, 1, 1, ink); R(x + 1, y + 5, 1, 1, ink); R(x + 3, y + 5, 1, 1, ink);
        if (page == 1) R(x + 5, y + 5, 2, 2, ink);
        if (page == 2) { R(x + 6, y - 2, 2, 2, c); R(x + 8, y - 4, 2, 2, c); }
    }

    // ── реквизит для статуса (в клетках, от угла поля персонажа) ──
    static void Cursor(int x, int y, Color c) { R(x, y, 2, 4, c); }   // мигающий курсор ввода

    // облачко мысли 11×6, внутри бегут три точки (phase 0..3)
    static void Cloud(int x, int y, int phase, Color c, Color ink)
    {
        R(x + 1, y, 9, 6, c); R(x, y + 1, 11, 4, c); R(x + 2, y - 1, 3, 1, c); R(x + 6, y - 1, 3, 1, c);
        for (int i = 0; i < 3; i++) if (i < phase) R(x + 2 + i * 3, y + 2, 2, 2, ink);
    }

    // ноутбук справа: экран с мигающими строчками кода и клавиатура
    static void Laptop(int x, int y, int frame, Color c, Color ink)
    {
        var dim = Color.FromArgb(110, 110, 114);
        R(x, y, 11, 8, c); R(x + 1, y + 1, 9, 6, ink);                // экран
        for (int l = 0; l < 3; l++)
        {
            int len = 2 + ((frame + l * 3) % 6);
            R(x + 2, y + 2 + l * 2, Math.Min(7, len), 1, l == frame % 3 ? Color.FromArgb(255, 196, 46) : dim);
        }
        R(x - 2, y + 8, 14, 2, c); R(x - 2, y + 9, 14, 1, Color.FromArgb(178, 178, 182));   // клавиатура
        R(x + 1 + (frame * 3) % 9, y + 8, 2, 1, ink);                 // нажатая клавиша
    }

    // табличка с «?» над головой
    static void Sign(int x, int y, Color board, Color ink, bool bright)
    {
        R(x, y, 16, 8, board); R(x + 7, y + 8, 2, 2, board);
        string[] q = { ".###.", "#...#", "...#.", "..#..", ".....", "..#.." };
        for (int j = 0; j < q.Length; j++) for (int i = 0; i < 5; i++) if (q[j][i] == '#') R(x + 6 + i, y + 1 + j, 1, 1, bright ? ink : Color.FromArgb(200, 200, 204));
    }

    // три карточки-варианта, выбранная приподнята и светлая
    static void Cards(int x, int y, int pick, bool pop)
    {
        string[][] digits = {
            new[] { ".#.", "##.", ".#.", ".#.", "###" },
            new[] { "##.", "..#", ".#.", "#..", "###" },
            new[] { "##.", "..#", ".#.", "..#", "##." } };
        for (int k = 0; k < 3; k++)
        {
            bool on = k == pick;
            int cx = x + k * 11, cy = y + (on ? (pop ? -2 : -1) : 0);
            var face = on ? W : Color.FromArgb(70, 70, 74);
            var ink = on ? Bk : Color.FromArgb(150, 150, 156);
            R(cx, cy, 8, 7, face);
            for (int j = 0; j < 5; j++) for (int i = 0; i < 3; i++) if (digits[k][j][i] == '#') R(cx + 2 + i, cy + 1 + j, 1, 1, ink);
        }
    }

    // конфетти: разноцветные квадратики разлетаются от персонажа
    static void Confetti(int frame)
    {
        if (frame < 1) return;
        Color[] cs = { Color.FromArgb(255, 196, 46), Color.FromArgb(140, 200, 255), Color.FromArgb(255, 105, 97), W, Color.FromArgb(120, 220, 140) };
        int[][] p = { new[] { 2, 2 }, new[] { 33, 1 }, new[] { 5, -2 }, new[] { 30, -3 }, new[] { 0, 9 }, new[] { 35, 8 }, new[] { 17, -4 }, new[] { 12, -2 }, new[] { 24, -2 } };
        for (int i = 0; i < p.Length; i++)
        {
            int fall = Math.Max(0, frame - 3) + (i % 3);
            R(p[i][0] + ((frame + i) % 2), p[i][1] + fall, 1 + (i % 2), 1 + ((i + 1) % 2), cs[i % cs.Length]);
        }
    }

    // большой монитор: бегущий разноцветный код, в конце — зелёный «OK»
    static void Monitor(int x, int y, int frame, bool ok)
    {
        var frame_c = Color.FromArgb(200, 200, 206);
        R(x, y, 17, 13, frame_c); R(x + 1, y + 1, 15, 11, Bk);
        R(x + 7, y + 13, 3, 2, frame_c); R(x + 4, y + 15, 9, 1, frame_c);
        if (ok)
        {
            var g = Color.FromArgb(120, 220, 140);
            if (frame % 2 == 0 || frame > 23)
            {
                // «OK» 3×5 пикселей на букву
                string[] o = { "###", "#.#", "#.#", "#.#", "###" }, k = { "#.#", "##.", "#..", "##.", "#.#" };
                for (int j = 0; j < 5; j++) for (int i = 0; i < 3; i++)
                {
                    if (o[j][i] == '#') R(x + 4 + i, y + 4 + j, 1, 1, g);
                    if (k[j][i] == '#') R(x + 9 + i, y + 4 + j, 1, 1, g);
                }
            }
            return;
        }
        Color[] cs = { Color.FromArgb(255, 196, 46), Color.FromArgb(140, 200, 255), Color.FromArgb(255, 105, 97), Color.FromArgb(120, 220, 140), Color.FromArgb(150, 150, 156) };
        for (int l = 0; l < 5; l++)
        {
            int line = l + frame;                       // строки ползут вверх
            int indent = (line * 7) % 3, len = 3 + (line * 5) % 8;
            R(x + 2 + indent, y + 2 + l * 2, Math.Min(12 - indent, len), 1, cs[line % cs.Length]);
        }
        if (frame % 2 == 0) R(x + 2 + ((frame * 3) % 10), y + 10, 1, 1, W);   // курсор
    }

    // кружка кофе с паром
    static void Mug(int x, int y, int frame)
    {
        var mug = Color.FromArgb(255, 105, 97);
        R(x, y, 4, 4, mug); R(x + 4, y + 1, 1, 2, mug);
        var steam = Color.FromArgb(150, 150, 156);
        int a = frame % 3;
        R(x + 1 - (a == 1 ? 1 : 0), y - 2 - a, 1, 1, steam);
        R(x + 2 + (a == 2 ? 1 : 0), y - 4 - (a + 1) % 3, 1, 1, steam);
    }

    static void Drop(int x, int y, Color c)   // капля пота
    {
        R(x + 1, y, 1, 1, c); R(x, y + 1, 3, 2, c); R(x + 1, y + 3, 1, 1, c);
    }

    static void Smoke(int x, int y, int size, Color c)
    {
        R(x, y, size, size, c); R(x + size - 1, y - 1, size, size, c);
    }

    // ════════════ кадры состояний ════════════
    sealed class Fr
    {
        public Sp Sp; public Pal Pal = Light; public bool Plate;
        public string Label; public Color Text; public string Mark;
        public int Delay; public int ShakePx;
        public Action Extra;            // мелочи вокруг (мысли, искры, «?», «z»), рисуются в клетках
    }

    static Sp P(Action<Sp> set) { var s = new Sp(); if (set != null) set(s); return s; }

    static void Main(string[] a)
    {
        glyphDir = a[0]; outDir = a[1];
        Directory.CreateDirectory(outDir);
        foreach (var f in Directory.GetFiles(outDir, "status_*")) File.Delete(f);
        StatusAnims();
        Rest();
    }

    // раскладка кнопки статуса (поля ~30 px — края экрана кнопки скруглены): клетка персонажа 3 px, поле 108×66 по центру,
    // белая плашка, подпись (крупно, если влезает, иначе мельче) и значки под ней
    static int KU = 3, OX = (S - 36 * 3) / 2, OY = 32, PlateIn = 18, LabelScale = 3, LabelY = 104, MarkY = 138;

    static void StatusAnims()
    {
        var L = new List<Fr>();
        Func<string, Color, Fr> fr = (label, text) => new Fr { Label = label, Text = text };

        // ── жду промпт: притоптывает, поглядывает, рядом мигает курсор ввода ──
        L.Clear();
        for (int i = 0; i < 24; i++)
        {
            var f = fr("ваш ход", W); int ii = i; f.Delay = 14;
            f.Sp = P(s =>
            {
                s.Dx = -3;
                s.Ex = new[] { 0, 1, 0, -1 }[ii / 6];
                s.Legs = ii % 4 == 1 ? 2 : 0;                 // постукивает ножкой
                s.Eyes = ii == 14 ? Eye.Closed : Eye.Open;
                s.Dy = ii % 8 < 4 ? 0 : 1;                    // дышит
            });
            f.Extra = () => { if (ii % 4 < 2) Cursor(36, 10, W); R(33, 15, 6, 1, Color.FromArgb(90, 90, 94)); };
            L.Add(f);
        }
        Anim("idle", L);
        Anim("waiting", L);

        // ── думает: чешет голову, облачко мысли, в нём бегут точки ──
        L.Clear();
        for (int i = 0; i < 12; i++)
        {
            var f = fr("думает", W); int ii = i; f.Delay = 12;
            f.Sp = P(s =>
            {
                s.Dx = -2; s.Ex = 1; s.Ey = -1;
                s.L = ii % 6 < 3 ? Arm.Wave2 : Arm.Wave1;    // почёсывает макушку
                s.Eyes = ii == 7 ? Eye.Closed : Eye.Open;
                s.Dy = ii % 6 < 3 ? 0 : 1;
            });
            f.Extra = () => { Cloud(27, -3, ii % 4, W, Bk); R(26, 3, 1, 1, W); R(24, 5, 1, 1, W); };
            f.Mark = "dots" + (ii % 3 + 1);
            L.Add(f);
        }
        Anim("thinking", L);

        // ── работает: печатает на ноутбуке, на экране бегут строчки кода ──
        L.Clear();
        for (int i = 0; i < 24; i++)
        {
            var f = fr("работает", W); int ii = i; f.Delay = 8;
            f.Sp = P(s =>
            {
                s.Dx = -5; s.Ex = 1; s.Ey = 1;
                s.R = ii % 2 == 0 ? Arm.Side : Arm.Down;     // стучит по клавишам
                s.L = ii % 2 == 0 ? Arm.Down : Arm.Side;
                s.Dy = ii % 2;
                s.Eyes = ii == 17 ? Eye.Closed : Eye.Open;
            });
            f.Extra = () => Laptop(29, 9, ii, W, Bk);
            f.Mark = "run" + (ii % 8);
            L.Add(f);
        }
        Anim("working", L);

        // ── разрешить?: белая плашка, подпрыгивает с табличкой «?» над головой ──
        L.Clear();
        var hop = new[] {
            new { dy = 0, sq = true,  legs = 0, d = 10 }, new { dy = -2, sq = false, legs = 3, d = 7 },
            new { dy = -3, sq = false, legs = 3, d = 9 }, new { dy = -2, sq = false, legs = 3, d = 7 },
            new { dy = 0, sq = true,  legs = 0, d = 8 },  new { dy = 0, sq = false, legs = 0, d = 30 } };
        for (int k = 0; k < hop.Length; k++)
        {
            var j = hop[k]; var f = fr("разреши?", Bk); f.Plate = true; f.Pal = OnPlate; f.Delay = j.d; int kk = k;
            f.Sp = P(s => { s.Dy = j.dy; s.Squash = j.sq; s.Legs = j.legs; s.L = Arm.Up; s.R = Arm.Up; s.Ey = -1; });
            f.Extra = () => Sign(10, -4 + j.dy + (j.sq ? 2 : 0), Bk, W, kk % 2 == 0);
            L.Add(f);
        }
        Anim("permission", L);

        // ── выберите: Claude предлагает варианты — три карточки, показывает на них по очереди ──
        L.Clear();
        for (int i = 0; i < 12; i++)
        {
            var f = fr("выберите", W); int ii = i, pick = (i / 4) % 3; f.Delay = 12;
            f.Sp = P(s =>
            {
                s.Ex = pick - 1; s.Ey = -1; s.Dy = 1;
                s.L = pick == 0 ? Arm.Up : Arm.Side;
                s.R = pick == 2 ? Arm.Up : Arm.Side;
                if (pick == 1) { s.L = Arm.Wave2; }
                if (ii % 4 == 3) s.Dy = 2;
            });
            f.Extra = () => Cards(3, -3, pick, ii % 4 == 0);
            L.Add(f);
        }
        Anim("question", L);

        // ── готово: прыгает от радости, вокруг конфетти ──
        L.Clear();
        var dj = new[] {
            new { dy = 0, sq = true,  legs = 0, up = false, d = 8 },  new { dy = -3, sq = false, legs = 3, up = true, d = 7 },
            new { dy = -4, sq = false, legs = 3, up = true, d = 9 },  new { dy = -2, sq = false, legs = 3, up = true, d = 7 },
            new { dy = 0, sq = true,  legs = 0, up = true, d = 8 },   new { dy = 0, sq = false, legs = 0, up = true, d = 14 },
            new { dy = 0, sq = false, legs = 0, up = false, d = 14 }, new { dy = 0, sq = false, legs = 0, up = true, d = 14 },
            new { dy = 0, sq = false, legs = 0, up = false, d = 14 }, new { dy = 0, sq = false, legs = 0, up = false, d = 80 } };
        for (int k = 0; k < dj.Length; k++)
        {
            var j = dj[k]; var f = fr("готово", W); f.Delay = j.d; f.Mark = "check"; int kk = k;
            f.Sp = P(s => { s.Dy = j.dy; s.Squash = j.sq; s.Legs = j.legs; s.L = j.up ? Arm.Up : Arm.Side; s.R = j.up ? Arm.Up : Arm.Side; s.Eyes = Eye.Happy; });
            f.Extra = () => { if (kk >= 1 && kk <= 7) Confetti(kk); };   // к паузе конфетти уже нет — новый прыжок без скачка
            L.Add(f);
        }
        Anim("done", L);

        // ── сценка по нажатию на «Статус»: Claude кодит за большим монитором, пьёт кофе, на экране «OK» — радуется ──
        L.Clear();
        for (int i = 0; i < 30; i++)
        {
            int ii = i; bool ok = i >= 20;
            var f = fr(ok ? "готово!" : "кодит…", ok ? Color.FromArgb(120, 220, 140) : W);
            f.Delay = i < 20 ? 7 : (i == 29 ? 120 : 10);
            f.Sp = P(s =>
            {
                s.Dx = -6; s.Ex = 1; s.Ey = ok ? 0 : 1;
                if (!ok) { s.R = ii % 2 == 0 ? Arm.Side : Arm.Down; s.L = ii % 2 == 0 ? Arm.Down : Arm.Side; s.Dy = ii % 2; if (ii == 11) s.Eyes = Eye.Closed; }
                else { s.L = Arm.Up; s.R = Arm.Up; s.Eyes = Eye.Happy; s.Dy = ii % 2 == 0 ? -1 : 0; }
            });
            f.Extra = () =>
            {
                Monitor(25, 1, ii, ok);
                Mug(-5, 13, ii);
                if (ok) Confetti(ii - 18);
            };
            L.Add(f);
        }
        Anim("fun", L);

        // ── ошибка: глаза-крестики, трясётся, дымок ──
        L.Clear();
        int[] shake = { -6, 6, -6, 6, -3, 0, 0, 0 };
        for (int i = 0; i < shake.Length; i++)
        {
            var f = fr("ошибка", Red); f.Delay = i < 5 ? 6 : 30; f.ShakePx = shake[i]; int ii = i;
            f.Sp = P(s => { s.Eyes = Eye.X; s.L = Arm.Down; s.R = Arm.Down; });
            f.Extra = () =>
            {
                var c = Color.FromArgb(120, 120, 124);
                Smoke(16, 1 - (ii % 4), 2, c);              // три струйки, каждая по кругу — стыка нет
                Smoke(21, 0 - ((ii + 2) % 4), 2, c);
                Smoke(12, -1 - ((ii + 1) % 3), 1, c);
            };
            L.Add(f);
        }
        Anim("error", L);

        // ── нет связи: серый, спит, «z» поднимаются ──
        L.Clear();
        for (int i = 0; i < 6; i++)
        {
            var f = fr("нет связи", Dim); f.Pal = Sleepy; f.Delay = 40; int ii = i;
            f.Sp = P(s => { s.Eyes = Eye.Closed; s.Dy = ii % 2; s.L = Arm.Down; s.R = Arm.Down; });
            f.Extra = () =>
            {
                var c = Color.FromArgb(150, 150, 154);
                Zed(30, 0 - ii % 3, 3, c);
                if (ii % 3 >= 1) Zed(33, -3 - ii % 3, 4, c);
            };
            L.Add(f);
        }
        Anim("offline", L);

        // ── сжать?: контекст заполнен на 70 % и больше — раздувается, пыхтит, капает пот ──
        L.Clear();
        var amber = Color.FromArgb(255, 159, 10);
        var sweat = Color.FromArgb(140, 200, 255);
        int[] fat = { 0, 1, 2, 2, 2, 1, 2, 2, 1, 0 };
        for (int i = 0; i < fat.Length; i++)
        {
            var f = fr("сжать?", amber); int ii = i, ff = fat[i];
            f.Delay = ff == 2 ? 14 : 10;
            f.Sp = P(s => { s.Fat = ff; s.Eyes = ff >= 2 ? Eye.Strain : Eye.Open; s.L = ff >= 1 ? Arm.Up : Arm.Side; s.R = ff >= 1 ? Arm.Up : Arm.Side; s.Legs = ff >= 2 ? 3 : 0; });
            f.Extra = () =>
            {
                if (ii >= 2 && ii <= 8) Drop(1, 6 + (ii - 2) * 2, sweat);
                if (ii >= 4 && ii <= 8) Drop(33, 4 + (ii - 4) * 2, sweat);
            };
            f.Mark = "full" + (ii % 2);
            L.Add(f);
        }
        Anim("full", L);
    }

    static void Rest()
    {
        Wide();
        Show();
        Autumn();
        Minis();
        Runners();

        // ── кнопки управления ──
        Icon("allow_off", false, Dim, (g, c) => Check(g, c));
        Icon("allow_on", true, Bk, (g, c) => Check(g, c));
        Icon("deny_off", false, Dim, (g, c) => Cross(g, c));
        Icon("deny_on", true, Bk, (g, c) => Cross(g, c));
        Icon("stop", false, W, (g, c) => StopSign(g, c));
        Icon("stop_on", true, Bk, (g, c) => StopSign(g, c));
        Icon("stop_off", false, Dim, (g, c) => StopSign(g, c));
        Icon("newchat", false, W, (g, c) => NewChat(g, c));
        Icon("dictation", false, W, (g, c) => Mic(g, c));
        Icon("dictation_off", false, Dim, (g, c) =>
        {
            Mic(g, c);
            using (var pen = new Pen(Bk, 18f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, 56, 50, 140, 150);
            using (var pen = new Pen(c, 9f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawLine(pen, 56, 50, 140, 150);
        });
        DictationOn();
        Icon("browser", false, W, (g, c) => Glyph(g, "browser.png", new RectangleF(30, 30, 136, 136)));
        DialAsk("dial_ask_on", true);
        DialAsk("dial_ask_off", false);

        // значок плагина в списке Ulanzi Studio — персонаж на прозрачном фоне
        using (var b = new Bitmap(S, S, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(b))
        {
            under = Color.Transparent;
            g.CompositingMode = CompositingMode.SourceCopy;
            DrawSprite(g, new Sp(), Light, 26, 40, U);
            b.Save(Path.Combine(outDir, "plugin.png"), ImageFormat.Png);
        }
        Console.WriteLine("ok");
    }


    static void Anim(string name, List<Fr> frames)
    {
        var bmps = new List<Bitmap>();
        foreach (var f in frames) bmps.Add(Render(f));
        bmps[0].Save(Path.Combine(outDir, "status_" + name + ".png"), ImageFormat.Png);
        using (var d = RenderDial(frames[Math.Min(frames.Count - 1, DialFrame(name))])) d.Save(Path.Combine(outDir, "dial_" + name + ".png"), ImageFormat.Png);
        var delays = new int[frames.Count];
        for (int i = 0; i < frames.Count; i++) delays[i] = frames[i].Delay;
        Gif.Write(Path.Combine(outDir, "status_" + name + ".gif"), bmps, delays);
        foreach (var b in bmps) b.Dispose();
    }

    static Bitmap Render(Fr f)
    {
        var b = new Bitmap(S, S, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        {
            Setup(g);
            g.Clear(Bk);
            if (f.Plate) using (var p = Rounded(new RectangleF(PlateIn, PlateIn, S - 2 * PlateIn, S - 2 * PlateIn), 26)) using (var br = new SolidBrush(W)) g.FillPath(br, p);
            under = f.Plate ? W : Bk;
            DrawSprite(g, f.Sp, f.Pal, OX + f.ShakePx * KU / U, OY, KU);
            if (f.Extra != null)
            {
                // мелочи — в тех же клетках, но без сдвига персонажа
                cg = g; cu = KU; cx = OX; cy = OY;
                var m = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
                var pom = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.Half;
                f.Extra();
                g.SmoothingMode = m; g.PixelOffsetMode = pom;
            }
            // подпись: длинная («НЕТ СВЯЗИ») не влезает в поля крупно — мельче
            int ls = LabelScale == 3 && f.Label.Length * 18 - 3 > S - 48 ? 2 : LabelScale;
            if (ls > 0) PixText(g, f.Label, S / 2, LabelY + (LabelScale == 3 && ls == 2 ? 4 : 0), ls, f.Text);
            Mark(g, f.Mark, f.Text);
        }
        return b;
    }

    // кадр для экрана крутилки — самый выразительный
    static int DialFrame(string name)
    {
        switch (name) { case "question": return 4; case "working": return 2; case "idle": return 1; case "full": return 3; case "done": return 2; case "permission": return 2; case "error": return 6; case "offline": return 2; case "thinking": return 2; default: return 0; }
    }

    // экран крутилки: место под картинку 100×100 — рисуем 200×200 без подписи (подпись выводит Ulanzi)
    static Bitmap RenderDial(Fr f)
    {
        const int D = 200;
        var b = new Bitmap(D, D, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        {
            Setup(g);
            g.Clear(Bk);
            if (f.Plate) using (var p = Rounded(new RectangleF(4, 4, D - 8, D - 8), 30)) using (var br = new SolidBrush(W)) g.FillPath(br, p);
            under = f.Plate ? W : Bk;
            int ox = 28, oy = 46;
            DrawSprite(g, f.Sp, f.Pal, ox + f.ShakePx, oy, U);
            if (f.Extra != null)
            {
                cg = g; cu = U; cx = ox; cy = oy;
                var m = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
                var pom = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.Half;
                f.Extra();
                g.SmoothingMode = m; g.PixelOffsetMode = pom;
            }
        }
        return b;
    }

    // экран крутилки «разрешить / отклонить»: ✕ слева, ✓ справа, стрелки поворота
    static void DialAsk(string name, bool on)
    {
        const int D = 200;
        using (var b = new Bitmap(D, D, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(b))
        {
            Setup(g); g.Clear(Bk);
            var c = on ? W : Dim;
            if (on) using (var p = Rounded(new RectangleF(4, 4, D - 8, D - 8), 30)) using (var br = new SolidBrush(W)) g.FillPath(br, p);
            var ink = on ? Bk : Dim;
            using (var pen = new Pen(ink, 10f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                // ✕ слева
                g.DrawLine(pen, 34, 76, 70, 112); g.DrawLine(pen, 70, 76, 34, 112);
                // ✓ справа
                g.DrawLines(pen, new[] { new PointF(126, 96), new PointF(142, 112), new PointF(170, 78) });
            }
            using (var pen = new Pen(ink, 6f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                // дуга поворота со стрелками в обе стороны
                g.DrawArc(pen, 40, 128, 120, 50, 200, 140);
            }
            using (var font = MakeFont(22f))
            {
                Text(g, "◀", font, ink, new RectangleF(22, 140, 40, 30));
                Text(g, "▶", font, ink, new RectangleF(138, 140, 40, 30));
            }
            b.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
        }
    }

    // ── маленький персонаж для кнопки лимитов: свой у каждого окна, поведение по заполненности ──
    // Файл assets\mini_<окно>_<уровень>.bin: «QMNI», ширина, высота, число кадров (u16), затем на каждый кадр
    // задержка (u16, сотые секунды) и пиксели RGBA. Плагин накладывает их поверх своих цифр и собирает GIF.
    //   five  — с песочными часами;  week — с календарём;  fable — в очках и короне, с бицепсами («самый важный»);
    //   ctx   — несёт на голове стопку блоков: чем полнее контекст, тем выше стопка.
    const int MU = 2;                     // клетка 2×2 px
    const int MW = 104, MH = 72;          // поле 52×36 клеток; персонаж (36 клеток) — с 8-й клетки слева, с 9-й сверху
    const int MOX = 8, MOY = 9;
    static readonly string[] Bands = { "calm", "busy", "full", "panic" };

    static List<MF> BaseFrames(string band)
    {
        var sweat = Color.FromArgb(140, 200, 255);
        var L = new List<MF>();
        switch (band)
        {
            case "calm":   // дышит, оглядывается, моргает
                L.Add(M(P(s => { }), 60, null)); L.Add(M(P(s => { s.Dy = 1; }), 40, null));
                L.Add(M(P(s => { s.Ex = -1; }), 50, null)); L.Add(M(P(s => { s.Eyes = Eye.Closed; }), 10, null));
                L.Add(M(P(s => { s.Ex = 1; s.Dy = 1; }), 50, null));
                break;
            case "busy":   // шагает
                L.Add(M(P(s => { s.Legs = 1; s.Ex = 1; }), 12, null)); L.Add(M(P(s => { s.Legs = 2; s.Dy = 1; s.Ex = 1; s.L = Arm.Down; }), 12, null));
                L.Add(M(P(s => { s.Legs = 1; s.Ex = 1; }), 12, null)); L.Add(M(P(s => { s.Legs = 2; s.Dy = 1; s.Ex = 1; s.R = Arm.Down; }), 12, null));
                break;
            case "full":   // раздувается, потеет
                L.Add(M(P(s => { s.Fat = 1; s.L = Arm.Up; s.R = Arm.Up; }), 14, () => Drop(1, 8, sweat)));
                L.Add(M(P(s => { s.Fat = 2; s.Eyes = Eye.Strain; s.L = Arm.Up; s.R = Arm.Up; s.Legs = 3; }), 18, () => { Drop(1, 10, sweat); Drop(33, 6, sweat); }));
                L.Add(M(P(s => { s.Fat = 2; s.Eyes = Eye.Strain; s.L = Arm.Up; s.R = Arm.Up; s.Legs = 3; }), 18, () => { Drop(1, 12, sweat); Drop(33, 8, sweat); }));
                L.Add(M(P(s => { s.Fat = 1; s.L = Arm.Up; s.R = Arm.Up; }), 14, () => Drop(33, 10, sweat)));
                break;
            default:       // на пределе: раздут, трясётся
                L.Add(M(P(s => { s.Fat = 3; s.Dx = -1; s.Eyes = Eye.Strain; s.L = Arm.Up; s.R = Arm.Up; s.Legs = 3; }), 7, () => { Drop(0, 7, sweat); Drop(34, 5, sweat); }));
                L.Add(M(P(s => { s.Fat = 3; s.Dx = 1; s.Eyes = Eye.X; s.L = Arm.Up; s.R = Arm.Up; s.Legs = 3; }), 7, () => { Drop(0, 9, sweat); Drop(34, 7, sweat); }));
                L.Add(M(P(s => { s.Fat = 3; s.Dx = -1; s.Eyes = Eye.Strain; s.L = Arm.Up; s.R = Arm.Up; s.Legs = 3; }), 7, () => { Drop(0, 11, sweat); Drop(34, 9, sweat); }));
                L.Add(M(P(s => { s.Fat = 2; s.Eyes = Eye.Strain; s.L = Arm.Up; s.R = Arm.Up; }), 30, () => { Drop(0, 13, sweat); Drop(34, 11, sweat); }));
                break;
        }
        return L;
    }

    static void Minis()
    {
        foreach (var f in Directory.GetFiles(outDir, "mini_*.bin")) File.Delete(f);
        Color sand = Color.FromArgb(255, 196, 46), glass = Color.FromArgb(200, 200, 210);
        for (int bi = 0; bi < Bands.Length; bi++)
        {
            string band = Bands[bi];
            {   // 5 часов: песочные часы справа, песок пересыпается
                var L = BaseFrames(band); int n = L.Count;
                for (int i = 0; i < n; i++) { var prev = L[i].Extra; int ph = i * 4 / n; L[i].Extra = () => { if (prev != null) prev(); Hourglass(38, 10, ph, glass, sand); }; }
                Mini("five_" + band, L);
            }
            {   // неделя: календарь слева, листок отрывается
                var L = BaseFrames(band); int n = L.Count;
                for (int i = 0; i < n; i++) { var prev = L[i].Extra; int pg = bi >= 2 ? i % 3 : (i == n - 1 ? 1 : 0); L[i].Extra = () => { if (prev != null) prev(); Calendar(-7, 9, pg, W, Bk); }; }
                Mini("week_" + band, L);
            }
            {   // Fable: очки, корона, бицепсы; на пределе очки сползают, корона съезжает
                var L = BaseFrames(band); int n = L.Count;
                for (int i = 0; i < n; i++)
                {
                    var sp = L[i].Sp;
                    sp.Glasses = true; sp.Crown = true;
                    if (band == "calm" || band == "busy") { sp.L = i % 2 == 0 ? Arm.Flex : Arm.Side; sp.R = i % 2 == 0 ? Arm.Side : Arm.Flex; }
                    else { sp.L = Arm.Flex; sp.R = Arm.Flex; }
                    if (band == "panic") { sp.GlassesDy = i % 2 + 1; sp.CrownDx = i % 2 == 0 ? -2 : 2; }
                    if (sp.Eyes == Eye.Closed) sp.Eyes = Eye.Open;   // в очках не моргает
                }
                Mini("fable_" + band, L);
            }
            {   // контекст: стопка блоков на голове, чем полнее — тем выше; на пределе шатается
                var L = BaseFrames(band); int n = L.Count;
                for (int i = 0; i < n; i++)
                {
                    var sp = L[i].Sp; sp.Stack = bi + 1;
                    sp.StackTilt = band == "panic" ? (i % 2 == 0 ? -2 : 2) : (band == "full" ? (i % 2 == 0 ? -1 : 1) : 0);
                    if (sp.L == Arm.Up) sp.L = Arm.Wave2;
                    if (sp.R == Arm.Up) sp.R = Arm.Wave2;   // придерживает стопку
                }
                Mini("ctx_" + band, L);
            }
        }
    }

    // ── бегуны для забега по трассе (кнопка лимитов, по нажатию): клетка 1 px ──
    // assets\run_<окно>.bin, формат как у mini: кадры 0–1 — бег, 2 — радуется, 3 — запыхался, 4 — без сил.
    const int RW = 48, RH = 34, ROX = 6, ROY = 10;
    static void Runners()
    {
        foreach (var f in Directory.GetFiles(outDir, "run_*.bin")) File.Delete(f);
        var sweat = Color.FromArgb(140, 200, 255);
        foreach (var who in new[] { "five", "week", "fable", "ctx" })
        {
            Action<Sp> look = s =>
            {
                if (who == "fable") { s.Glasses = true; s.Crown = true; }
                if (who == "ctx") s.Stack = 2;
            };
            var L = new List<MF>
            {
                M(P(s => { look(s); s.Legs = 1; s.Ex = 1; s.L = Arm.Side; s.R = Arm.Down; }), 6, null),
                M(P(s => { look(s); s.Legs = 2; s.Dy = 1; s.Ex = 1; s.L = Arm.Down; s.R = Arm.Side; }), 6, null),
                M(P(s => { look(s); s.L = who == "fable" ? Arm.Flex : Arm.Up; s.R = who == "fable" ? Arm.Flex : Arm.Up; s.Eyes = who == "fable" ? Eye.Open : Eye.Happy; }), 10, null),
                M(P(s => { look(s); s.Fat = 1; s.Eyes = Eye.Strain; s.L = Arm.Down; s.R = Arm.Down; }), 10, () => { Drop(3, 6, sweat); Drop(32, 8, sweat); }),
                M(P(s => { look(s); s.Squash = true; s.Eyes = Eye.X; s.L = Arm.Down; s.R = Arm.Down; s.Legs = 3; if (who == "fable") s.GlassesDy = 2; }), 10, null),
            };
            WriteFrames("run_" + who, L, RW, RH, 1, ROX, ROY);
        }
    }

    static void WriteFrames(string name, List<MF> frames, int w, int h, int unit, int ox, int oy)
    {
        using (var fs = new FileStream(Path.Combine(outDir, name + ".bin"), FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(System.Text.Encoding.ASCII.GetBytes("QMNI"));
            bw.Write((ushort)w); bw.Write((ushort)h); bw.Write((ushort)frames.Count);
            foreach (var f in frames)
                using (var b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.Transparent);
                        g.CompositingMode = CompositingMode.SourceCopy;
                        under = Color.Transparent;
                        DrawSprite(g, f.Sp, Light, ox * unit, oy * unit, unit);
                        if (f.Extra != null) { cg = g; cu = unit; cx = ox * unit; cy = oy * unit; f.Extra(); }
                    }
                    bw.Write((ushort)f.Delay);
                    var d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var px = new byte[w * h * 4];
                    System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
                    b.UnlockBits(d);
                    for (int i = 0; i < px.Length; i += 4) { byte t = px[i]; px[i] = px[i + 2]; px[i + 2] = t; }
                    bw.Write(px);
                }
        }
    }

    sealed class MF { public Sp Sp; public int Delay; public Action Extra; }
    static MF M(Sp s, int d, Action extra) { return new MF { Sp = s, Delay = d, Extra = extra }; }

    static void Mini(string name, List<MF> frames)
    {
        using (var fs = new FileStream(Path.Combine(outDir, "mini_" + name + ".bin"), FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(System.Text.Encoding.ASCII.GetBytes("QMNI"));
            bw.Write((ushort)MW); bw.Write((ushort)MH); bw.Write((ushort)frames.Count);
            foreach (var f in frames)
                using (var b = new Bitmap(MW, MH, PixelFormat.Format32bppArgb))
                {
                    using (var g = Graphics.FromImage(b))
                    {
                        g.Clear(Color.Transparent);
                        g.CompositingMode = CompositingMode.SourceCopy;   // вырезанные углы — прозрачные
                        under = Color.Transparent;
                        DrawSprite(g, f.Sp, Light, MOX * MU, MOY * MU, MU);
                        if (f.Extra != null) { cg = g; cu = MU; cx = MOX * MU; cy = MOY * MU; f.Extra(); }
                    }
                    bw.Write((ushort)f.Delay);
                    var d = b.LockBits(new Rectangle(0, 0, MW, MH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                    var px = new byte[MW * MH * 4];
                    System.Runtime.InteropServices.Marshal.Copy(d.Scan0, px, 0, px.Length);
                    b.UnlockBits(d);
                    // GDI+ хранит BGRA — переставляем в RGBA
                    for (int i = 0; i < px.Length; i += 4) { byte t = px[i]; px[i] = px[i + 2]; px[i + 2] = t; }
                    bw.Write(px);
                }
        }
    }

    // ── широкий экран 458×196: спокойная петля — Claude ходит туда-сюда и занимается своими делами ──
    // Заходит → машет → думает → лампочка-идея → идёт к монитору → печатает (код, кофе) → «OK», конфетти → танцует → уходит.
    const int SW = 458, SH = 196, SU = 5, FLOOR = 170;
    static void Show()
    {
        var frames = new List<Bitmap>(); var delays = new List<int>();
        int charY = FLOOR - 22 * SU;            // верх поля персонажа: ножки стоят на полу
        int home = 130, desk = 172, plantX = 40;
        int t = 0;
        var specs = new List<Action<int, int>>();
        // x — где персонаж; mon — экран (0 заставка, 1 код); grow — цветок подрос; water — капли (номер кадра полива или −1);
        // held — что в руке (рисуется в клетках персонажа)
        Action<int, Sp, int, int, int, int, Action> add = (x, sp, mon, d, grow, water, held) =>
        {
            delays.Add(d);
            specs.Add((ft, total) =>
            {
                t = ft;
                var b = new Bitmap(SW, SH, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(b))
                {
                    Setup(g); g.Clear(Bk);
                    At(g, 0, 0, SU);
                    var pom = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.Half;
                    var sm = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
                    Stars(t, total);
                    for (int fx = 0; fx < SW / SU; fx += 3) R(fx, FLOOR / SU + 1, 2, 1, Color.FromArgb(48, 48, 52));
                    Monitor2(70, 18, t / 2, mon);
                    Plant(2, 22, grow);
                    g.PixelOffsetMode = pom; g.SmoothingMode = sm;
                    under = Bk;
                    DrawSprite(g, sp, Light, x, charY, SU);
                    g.PixelOffsetMode = PixelOffsetMode.Half; g.SmoothingMode = SmoothingMode.None;
                    if (water >= 0)
                    {
                        At(g, 0, 0, SU);
                        var drop = Color.FromArgb(140, 200, 255);
                        for (int k = 0; k < 3; k++) { int yy = 21 + ((water + k * 2) % 6); R(7 + k, yy, 1, 1, drop); }
                    }
                    if (held != null) { At(g, x, charY + sp.Dy * SU, SU); held(); }
                    g.PixelOffsetMode = pom; g.SmoothingMode = sm;
                }
                frames.Add(b);
            });
        };
        Action<int, int, int, int> walk = (from, to, grow, d) =>
        {
            int dir = to > from ? 1 : -1, n = Math.Abs(to - from) / 6;
            for (int i = 1; i <= n; i++)
            {
                int ii = i;
                add(from + dir * i * 6, P(s => { s.Legs = ii % 2 == 0 ? 1 : 2; s.Dy = (ii / 2) % 2; s.Ex = dir; s.L = ii % 4 < 2 ? Arm.Side : Arm.Down; s.R = ii % 4 < 2 ? Arm.Down : Arm.Side; }), 0, d, grow, -1, null);
            }
        };

        // 1. стоит дома: дышит, моргает
        add(home, P(s => { }), 0, 40, 0, -1, null);
        add(home, P(s => { s.Dy = 1; }), 0, 40, 0, -1, null);
        add(home, P(s => { s.Eyes = Eye.Closed; }), 0, 12, 0, -1, null);
        // 2. пьёт чай: поднимает кружку, прихлёбывает, над ней пар
        for (int i = 0; i < 10; i++)
        {
            int ii = i; bool sip = i >= 3 && i <= 6;
            add(home, P(s => { s.R = Arm.Up; s.Eyes = sip ? Eye.Happy : Eye.Open; s.Ex = 1; s.Dy = sip ? 1 : 0; }), 0, 16, 0, -1, () => TeaCup(31, sip ? -1 : -3, ii));
        }
        add(home, P(s => { s.Ex = 1; }), 0, 30, 0, -1, null);
        // 3. не спеша идёт к монитору, неспешно печатает
        walk(home, desk, 0, 11);
        for (int i = 0; i < 14; i++) { int ii = i; add(desk, P(s => { s.Ex = 1; s.Ey = 1; s.R = ii % 2 == 0 ? Arm.Side : Arm.Down; s.L = Arm.Down; s.Dy = (ii / 2) % 2; if (ii == 8) s.Eyes = Eye.Closed; }), 1, 14, 0, -1, null); }
        // 4. потягивается, зевает
        add(desk, P(s => { s.L = Arm.Up; s.R = Arm.Up; s.Eyes = Eye.Closed; s.Dy = -1; }), 0, 40, 0, -1, null);
        add(desk, P(s => { s.L = Arm.Up; s.R = Arm.Up; s.Eyes = Eye.Happy; s.Dy = -1; }), 0, 30, 0, -1, null);
        // 5. перекус: бутерброд, откусывает — бутерброд меньше, крошки падают
        for (int i = 0; i < 12; i++)
        {
            int ii = i, bites = Math.Min(3, i / 3); bool chew = i % 3 == 1;
            add(desk, P(s => { s.L = Arm.Up; s.Ex = -1; s.Eyes = chew ? Eye.Happy : Eye.Open; s.Dy = chew ? 1 : 0; }), 0, 15, 0, -1,
                () => { if (bites < 3) Sandwich(-3, chew ? -1 : -3, bites); Crumbs(4, 18, ii); });
        }
        add(desk, P(s => { s.Ex = -1; s.Eyes = Eye.Happy; }), 0, 30, 0, -1, null);
        // 6. идёт налево к цветку и поливает его — тот подрастает
        walk(desk, plantX, 0, 11);
        for (int i = 0; i < 10; i++) { int ii = i; add(plantX, P(s => { s.Ex = -1; s.Ey = 1; s.L = ii % 2 == 0 ? Arm.Side : Arm.Down; }), 0, 14, ii < 5 ? 0 : 1, ii, null); }
        // 7. любуется, оглядывается
        add(plantX, P(s => { s.Ex = -1; s.Eyes = Eye.Happy; }), 0, 40, 1, -1, null);
        add(plantX, P(s => { s.Ex = 1; }), 0, 30, 1, -1, null);
        add(plantX, P(s => { s.Eyes = Eye.Closed; s.Ex = 1; }), 0, 10, 1, -1, null);
        // 8. возвращается домой — к позе первого кадра (петля без стыка), цветок снова «как был»
        walk(plantX, home, 1, 11);
        add(home, P(s => { }), 0, 30, 0, -1, null);

        for (int i = 0; i < specs.Count; i++) specs[i](i, specs.Count);
        frames[0].Save(Path.Combine(outDir, "claude_show.png"), ImageFormat.Png);
        Gif.Write(Path.Combine(outDir, "claude_show.gif"), frames, delays.ToArray());
        foreach (var b in frames) b.Dispose();
    }

    // ── широкий экран 458×196: осенний закат у моря (клетка 3 px — мелкие детали). Claude со спины сидит в траве
    // на склоне и смотрит на солнце; рядом кружка горячего чая с паром; слева осеннее дерево, с него медленно
    // падают листья; розово-оранжевое небо с облаками, золотая дорожка на воде, птицы. Петля 64 кадра (~8 кадров/с) без стыка:
    // всё движение — с периодом, который делит 64. Небо, море и трава — ровными ступенями, без шахматки: деке легче. GIF пишется «разницей кадров» — лёгкий. ──
    static void Autumn()
    {
        const int N = 64, D = 12, U = 3;              // кадров, задержка (1/100 с) — ~8 кадров/с (деке так легче), петля 7,7 с; клетка 3 px
        int cols = SW / U + 1, rows = SH / U + 1;      // 153 × 66 клеток
        Func<int, int, int, Color> C = (r, g, b) => Color.FromArgb(r, g, b);
        Func<int, int, int> hash = (x, y) => { unchecked { int h = x * 73856093 ^ y * 19349663; h ^= h >> 13; h *= 0x5bd1e995; return (h ^ (h >> 15)) & 0x7fffffff; } };
        int[,] bayer = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
        Func<int, int, double, bool> dith = (x, y, f) => (bayer[y & 3, x & 3] + 0.5) / 16.0 < f;   // доля f — «да»
        Func<double, double, double, double, double, double, double> ell = (x, y, cx0, cy0, rx, ry) => (x - cx0) * (x - cx0) / (rx * rx) + (y - cy0) * (y - cy0) / (ry * ry);

        const int HOR = 40;                            // горизонт
        // небо: плавный переход сверху вниз — 14 ступеней с шахматкой Байера между ними
        int[][] skyStops = { new[] { 0, 206, 120, 158 }, new[] { 14, 232, 146, 160 }, new[] { 26, 248, 172, 148 }, new[] { 34, 253, 196, 138 }, new[] { 40, 255, 214, 140 } };
        const int LEVELS = 14;
        var skyCol = new Color[LEVELS + 1];
        for (int k = 0; k <= LEVELS; k++)
        {
            double y = 40.0 * k / LEVELS; int s = 0; while (s + 2 < skyStops.Length && y > skyStops[s + 1][0]) s++;
            double f = (y - skyStops[s][0]) / (skyStops[s + 1][0] - skyStops[s][0]); f = Math.Max(0, Math.Min(1, f));
            skyCol[k] = C((int)(skyStops[s][1] + (skyStops[s + 1][1] - skyStops[s][1]) * f), (int)(skyStops[s][2] + (skyStops[s + 1][2] - skyStops[s][2]) * f), (int)(skyStops[s][3] + (skyStops[s + 1][3] - skyStops[s][3]) * f));
        }
        var sun = C(255, 232, 160); var sunCore = C(255, 248, 210); var halo = C(255, 222, 158);
        var cloud = C(230, 114, 140); var cloudTop = C(242, 142, 156); var cloudLit = C(255, 206, 150); var cloudLit2 = C(246, 164, 148);
        var seaA = C(236, 158, 156); var seaB = C(214, 128, 146); var seaWave = C(196, 112, 138);
        var glint = C(255, 212, 128); var glint2 = C(255, 242, 196);
        var hillFar = C(190, 120, 150); var hillNear = C(150, 88, 122); var coast = C(112, 66, 98); var cypress = C(74, 42, 66);
        var grassTop = C(110, 116, 60); var grass = C(88, 98, 52); var grassDark = C(62, 72, 40); var bladeLit = C(220, 172, 72); var bladeOr = C(226, 130, 60); var bladeGr = C(146, 152, 72);
        var trunk = C(84, 52, 42); var trunkDark = C(56, 34, 30); var trunkLit = C(128, 78, 56);
        Color[] leaf = { C(252, 182, 60), C(240, 132, 48), C(222, 92, 40), C(190, 56, 44), C(140, 40, 42) };   // от светлого к тёмному
        var bird = C(150, 76, 104);
        // фирменный цвет Claude (терракота); со спины, солнце справа — правый край и низ светлее; тёмный контур
        var back = new Pal { Main = C(205, 106, 76), Shade = C(248, 170, 120), Eye = Bk };
        var outline = C(112, 50, 46);
        Func<int, int> groundY = x => 45 + x * 10 / 152;       // склон: слева выше, справа ниже

        // облака — «пухлые» овалы; рваный край по хешу
        double[][] clouds = {
            new double[] { 50, 7, 12, 3.6 }, new double[] { 63, 5, 10, 4.2 }, new double[] { 76, 7.5, 10, 3 }, new double[] { 40, 9, 6, 2 },
            new double[] { 103, 16, 13, 3 }, new double[] { 116, 15, 10, 3.6 }, new double[] { 130, 17, 12, 2.4 },
            new double[] { 43, 23, 10, 2 }, new double[] { 90, 27, 12, 1.6 }, new double[] { 140, 25, 10, 1.8 }, new double[] { 20, 30, 8, 1.4 } };
        Func<int, int, bool> inCloud = (x, y) =>
        {
            foreach (var c in clouds) { double e = ell(x, y, c[0], c[1], c[2], c[3]); if (e <= 0.9 || (c[3] >= 2.5 && e <= 1.1 && hash(x, y) % 3 != 0)) return true; }
            return false;
        };
        // крона дерева
        double[][] crown = { new double[] { 14, 15, 13, 9 }, new double[] { 28, 11, 9, 7 }, new double[] { 2, 19, 8, 6 }, new double[] { 23, 21, 10, 5 }, new double[] { 8, 8, 8, 5 } };
        Func<int, int, double> inCrown = (x, y) =>     // 0 — нет, иначе «глубина» (меньше — ближе к краю)
        {
            double best = 0;
            foreach (var c in crown) { double e = ell(x, y, c[0], c[1], c[2], c[3]); if (e <= 1) best = Math.Max(best, 1 - e); else if (e <= 1.25 && hash(x, y) % 3 == 0) best = Math.Max(best, 0.01); }
            return best;
        };
        int charX = 50 * U, charY = 31 * U;            // поле персонажа: тело в клетках 56…79, строки 35…48 — низ в траве
        int mugX = 85;
        // листья: где срываются (клетки), сдвиг во времени, цвет
        int[][] fall = { new[] { 6, 12, 0, 1 }, new[] { 20, 8, 6, 0 }, new[] { 30, 12, 13, 2 }, new[] { 12, 20, 19, 1 }, new[] { 25, 18, 26, 3 },
                         new[] { 3, 16, 32, 2 }, new[] { 34, 9, 38, 0 }, new[] { 17, 14, 45, 1 }, new[] { 9, 22, 51, 3 }, new[] { 28, 21, 58, 2 } };
        // цветы на склоне
        int[][] flowers = { new[] { 22, 58 }, new[] { 36, 62 }, new[] { 98, 60 }, new[] { 114, 63 }, new[] { 70, 61 }, new[] { 140, 62 }, new[] { 8, 61 } };

        var frames = new List<Bitmap>(); var delays = new List<int>();
        for (int t = 0; t < N; t++)
        {
            var b = new Bitmap(SW, SH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Bk);
                g.SmoothingMode = SmoothingMode.None; g.PixelOffsetMode = PixelOffsetMode.Half;
                At(g, 0, 0, U);
                // небо
                for (int y = 0; y < HOR; y++)
                {
                    double v = y * LEVELS / 40.0; int k = (int)v; double f = v - k;
                    R(0, y, cols, 1, skyCol[Math.Min(LEVELS, f > 0.5 ? k + 1 : k)]);
                }
                // солнце с ореолом
                for (int y = 14; y < HOR; y++) for (int x = 100; x <= 140; x++)
                {
                    double d = Math.Sqrt((x - 120) * (x - 120) + (y - 31.5) * (y - 31.5));
                    if (d <= 6.5) R(x, y, 1, 1, sunCore);
                    else if (d <= 10) R(x, y, 1, 1, sun);
                    else if (d <= 13.5 && dith(x, y, (13.5 - d) / 3.5 * 0.8)) R(x, y, 1, 1, halo);
                }
                // облака: снизу золотая подсветка, сверху светлее
                for (int y = 0; y < HOR - 6; y++) for (int x = 0; x < cols; x++)
                {
                    if (!inCloud(x, y)) continue;
                    int below = 0; while (below < 3 && inCloud(x, y + below + 1)) below++;
                    bool top = !inCloud(x, y - 1);
                    R(x, y, 1, 1, below == 0 ? cloudLit : below == 1 ? cloudLit2 : top ? cloudTop : cloud);
                }
                // птицы у солнца: летят направо, машут крыльями
                for (int k = 0; k < 3; k++)
                {
                    int bx = (t * 152 / N + 70 + k * 7) % 152, by = 18 + k * 3 - (k == 1 ? 2 : 0) + ((t / 4 + k) % 2);
                    bool up = (t / 3 + k) % 2 == 0;
                    R(bx, by, 1, 1, bird); R(bx - 1, by + (up ? -1 : 0), 1, 1, bird); R(bx + 1, by + (up ? -1 : 0), 1, 1, bird);
                    R(bx - 2, by + (up ? -2 : 1), 1, 1, bird); R(bx + 2, by + (up ? -2 : 1), 1, 1, bird);
                }
                // дальние холмы и берег с кипарисами
                for (int x = 0; x < cols; x++)
                {
                    int hl = x < 46 ? (int)Math.Round(3 + 2 * Math.Sin(x / 7.0) + Math.Sin(x / 2.5) * 0.6) : 0;
                    int hr = x > 128 ? (int)Math.Round(4 + 2 * Math.Sin((x - 128) / 6.0)) : 0;
                    if (hl > 0) R(x, HOR - hl, 1, hl, hillFar);
                    if (hr > 0) R(x, HOR - hr, 1, hr, hillNear);
                    if (x > 132) R(x, HOR, 1, 2, coast);
                }
                foreach (int cx0 in new[] { 136, 140, 141, 146, 150 })
                {
                    int hgt = 6 + hash(cx0, 3) % 4;
                    R(cx0, HOR - hgt, 1, hgt, cypress); R(cx0 - 1, HOR - hgt + 2, 3, hgt - 3, cypress);
                }
                // море: от светлого у горизонта к тёмному, редкие волны-чёрточки, золотая дорожка
                for (int y = HOR; y < rows; y++)
                {
                    double f = (y - HOR) / 18.0;
                    for (int x = 0; x < cols; x++)
                    {
                        if (x > 132 && y < HOR + 2) continue;
                        R(x, y, 1, 1, f > 0.5 ? seaB : seaA);
                    }
                    if ((y - HOR) % 3 == 1)
                        for (int x = 0; x < cols; x++) if ((x + y * 7 + (t / 16) * 3) % 23 < 3) R(x, y, 1, 1, seaWave);
                    int half = 7 + (y - HOR) * 2 / 3, sh = (t / 8) % 2;
                    for (int x = 120 - half; x <= 120 + half; x++)
                    {
                        int run = (x + y * 3 + sh * 2) % 6;
                        if (run < 3 && hash(x, y) % 5 != 0) R(x, y, 1, 1, Math.Abs(x - 120) <= half / 3 && run == 1 ? glint2 : glint);
                    }
                }
                // дерево: ствол с корой, ветки
                for (int y = 18; y < 52; y++)
                {
                    int w = y > 46 ? 6 + (y - 46) / 2 : 5, x0 = 12 - (w - 5) / 2;
                    for (int x = x0; x < x0 + w; x++)
                        R(x, y, 1, 1, x == x0 + w - 1 ? trunkDark : x == x0 + 1 && hash(x, y) % 3 != 0 ? trunkLit : hash(x, y) % 7 == 0 ? trunkDark : trunk);
                }
                for (int k = 0; k < 11; k++) { R(16 + k, 22 - k, 2, 1, trunk); if (k < 7) R(11 - k, 25 - k, 2, 1, trunk); if (k < 6) R(17 + k, 27 - k / 2, 1, 1, trunkDark); }
                // крона: светлее вверху справа (солнце), темнее внизу слева, листва «зёрнами»
                for (int y = 0; y < 30; y++) for (int x = -8; x < 42; x++)
                {
                    double dpt = inCrown(x, y);
                    if (dpt <= 0) continue;
                    double light = 0.4 + (x - 14) / 90.0 - (y - 15) / 34.0 + (hash(x, y) % 100) / 160.0;
                    int li = light > 1.0 ? 0 : light > 0.72 ? 1 : light > 0.46 ? 2 : light > 0.26 ? 3 : 4;
                    if (dpt < 0.08 && li < 2) li = 2;
                    R(x, y, 1, 1, leaf[li]);
                }
            }
            // персонаж со спины — на прозрачном слое (углы тела прозрачные), вокруг — тёмный контур
            using (var layer = new Bitmap(SW, SH, PixelFormat.Format32bppArgb))
            {
                using (var lg = Graphics.FromImage(layer))
                {
                    lg.Clear(Color.Transparent); lg.CompositingMode = CompositingMode.SourceCopy;
                    under = Color.Transparent;
                    int tt = t;
                    // сидит, смотрит на закат; в середине петли опускает руку к кружке и обратно
                    var sp = P(s => { s.Eyes = Eye.None; s.Legs = 3; if (tt >= 30 && tt < 42) s.R = Arm.Down; });
                    DrawSprite(lg, sp, back, charX, charY, U);
                }
                using (var g = Graphics.FromImage(b))
                {
                    g.SmoothingMode = SmoothingMode.None; g.PixelOffsetMode = PixelOffsetMode.Half;
                    At(g, 0, 0, U);
                    Func<int, int, bool> on = (x, y) => x >= 0 && y >= 0 && x * U + 1 < SW && y * U + 1 < SH && layer.GetPixel(x * U + 1, y * U + 1).A > 0;
                    for (int y = 20; y < rows; y++) for (int x = 40; x < 100; x++)
                        if (!on(x, y) && (on(x - 1, y) || on(x + 1, y) || on(x, y - 1) || on(x, y + 1))) R(x, y, 1, 1, outline);
                    g.DrawImage(layer, 0, 0);
                }
                under = Bk;
            }
            // склон с травой — поверх низа персонажа; цветы, листья, кружка, пар, падающие листья
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.None; g.PixelOffsetMode = PixelOffsetMode.Half;
                At(g, 0, 0, U);
                for (int x = 0; x < cols; x++)
                {
                    int gy = groundY(x);
                    for (int y = gy; y < rows; y++)
                    {
                        double f = (y - gy) / 12.0;
                        var c = y == gy ? grassTop : f > 0.6 ? grassDark : grass;
                        if (f <= 0.6 && hash(x, y) % 17 == 0) c = grassDark;
                        R(x, y, 1, 1, c);
                    }
                }
                // травинки на кромке: золотые, рыжие, зелёные; верх качается на ветру (период 16 кадров)
                for (int x = 0; x < cols; x++)
                {
                    int gy = groundY(x), h = 1 + hash(x, 9) % (x >= 54 && x <= 82 ? 2 : 4);   // перед Claude травинки ниже — руки видно
                    var c = hash(x, 7) % 4 == 0 ? bladeOr : hash(x, 7) % 4 == 1 ? bladeGr : bladeLit;
                    int sway = ((t / 8 + x / 5) % 2 == 0) ? 0 : 1;
                    for (int k = 1; k <= h; k++) R(x + (k >= 3 ? sway : 0), gy - k, 1, 1, c);
                    // травинки и пониже, в глубине склона
                    if (hash(x, 13) % 3 == 0) { int y2 = gy + 4 + hash(x, 17) % 12; if (y2 < rows) { R(x, y2, 1, 1, bladeGr); R(x + sway, y2 - 1, 1, 1, bladeGr); } }
                }
                foreach (var fl in flowers)
                {
                    var pet = C(250, 244, 236); var pet2 = C(232, 220, 212);
                    R(fl[0], fl[1], 1, 1, C(255, 200, 70)); R(fl[0] - 1, fl[1], 1, 1, pet); R(fl[0] + 1, fl[1], 1, 1, pet2); R(fl[0], fl[1] - 1, 1, 1, pet); R(fl[0], fl[1] + 1, 1, 1, pet2);
                    R(fl[0], fl[1] + 2, 1, 2, bladeGr);
                }
                // опавшие листья под деревом
                for (int y = 46; y < rows; y++) for (int x = 0; x < 44; x++)
                    if (y > groundY(x) && hash(x, y + 100) % (x < 30 ? 8 : 16) == 0) R(x, y, 1, 1, leaf[1 + hash(x, y) / 7 % 4]);
                // кружка чая на траве справа от Claude
                int mugY = groundY(mugX + 3) - 5;
                var cup = C(255, 105, 97); var cupDark = C(204, 72, 72); var cupLit = C(255, 150, 130); var tea = C(150, 92, 52);
                R(mugX, mugY, 6, 6, cup); R(mugX + 5, mugY, 1, 6, cupDark); R(mugX, mugY + 5, 6, 1, cupDark); R(mugX + 1, mugY + 1, 1, 3, cupLit);
                R(mugX, mugY, 6, 1, cupLit); R(mugX + 1, mugY, 4, 1, tea);
                R(mugX + 6, mugY + 1, 2, 1, cup); R(mugX + 7, mugY + 2, 1, 2, cup); R(mugX + 6, mugY + 4, 2, 1, cupDark);
                // пар: три струйки завиваются и тают (период 16 кадров)
                for (int k = 0; k < 3; k++)
                {
                    for (int j = 0; j < 3; j++)
                    {
                        int p = (t + k * 5 + j * 5) % 16;           // пар поднимается на 12 клеток за 16 кадров
                        int sy = mugY - 1 - p * 12 / 16;
                        int sx = mugX + 1 + k * 2 + (int)Math.Round(Math.Sin((p + k * 4) * Math.PI / 5) * 1.5);
                        var sc = p < 5 ? C(255, 252, 248) : p < 10 ? C(255, 230, 218) : C(250, 206, 196);
                        if (p < 14) R(sx, sy, 1, 1, sc);
                    }
                }
                // падающие листья — поверх всего: кружатся (2×1 / 1×2 / точка) и сносятся ветром вправо
                foreach (var f in fall)
                {
                    int p = (t + f[2]) % N;
                    int y = f[1] + p * (50 - f[1]) / N;
                    int x = f[0] + p * 30 / N + (int)Math.Round(Math.Sin(p * Math.PI / 8) * 2);
                    if (y >= groundY(x)) continue;
                    var c = leaf[f[3]]; var c2 = leaf[Math.Min(4, f[3] + 1)];
                    switch ((p / 2) % 4)
                    {
                        case 0: R(x, y, 2, 1, c); R(x + 1, y, 1, 1, c2); break;
                        case 1: R(x, y, 1, 1, c); R(x + 1, y + 1, 1, 1, c2); break;
                        case 2: R(x, y, 1, 2, c); break;
                        default: R(x + 1, y, 1, 1, c); R(x, y + 1, 1, 1, c2); break;
                    }
                }
            }
            frames.Add(b); delays.Add(D);
        }
        frames[0].Save(Path.Combine(outDir, "claude_autumn.png"), ImageFormat.Png);
        Gif.Write(Path.Combine(outDir, "claude_autumn.gif"), frames, delays.ToArray(), true);
        foreach (var b in frames) b.Dispose();
    }

    // кружка чая в руке и пар над ней
    static void TeaCup(int x, int y, int frame)
    {
        var cup = Color.FromArgb(255, 105, 97); var tea = Color.FromArgb(170, 110, 60);
        R(x, y, 4, 4, cup); R(x + 4, y + 1, 1, 2, cup); R(x, y, 4, 1, tea);
        var steam = Color.FromArgb(150, 150, 156);
        int a = frame % 3;
        R(x + 1 - (a == 1 ? 1 : 0), y - 2 - a, 1, 1, steam);
        R(x + 2 + (a == 2 ? 1 : 0), y - 4 - (a + 1) % 3, 1, 1, steam);
    }

    // бутерброд: хлеб, салат, сыр; bites — сколько откусил (0..2)
    static void Sandwich(int x, int y, int bites)
    {
        var bread = Color.FromArgb(214, 160, 90); var leaf = Color.FromArgb(120, 220, 140); var cheese = Color.FromArgb(255, 210, 80);
        int w = 7 - bites * 2;
        R(x, y, w, 1, bread); R(x, y + 1, w, 1, leaf); R(x, y + 2, w, 1, cheese); R(x, y + 3, w, 1, bread);
    }

    // крошки сыплются из-под бутерброда
    static void Crumbs(int x, int y, int frame)
    {
        var c = Color.FromArgb(214, 160, 90);
        for (int k = 0; k < 3; k++)
        {
            int f = frame - k * 2;
            if (f < 0) continue;
            R(x - k * 2, y - 14 + Math.Min(14, f * 2), 1, 1, c);
        }
    }

    // цветок в горшке; grow 0/1 — подрос после полива
    static void Plant(int x, int y, int grow)
    {
        var pot = Color.FromArgb(200, 110, 70); var leaf = Color.FromArgb(120, 220, 140); var leaf2 = Color.FromArgb(70, 170, 100);
        R(x, y + 8, 6, 4, pot); R(x - 1, y + 7, 8, 1, pot);
        int h = 5 + grow * 2;
        R(x + 3, y + 7 - h, 1, h, leaf2);
        R(x + 1, y + 4 - grow, 2, 1, leaf); R(x + 4, y + 3 - grow, 2, 1, leaf);
        R(x + 2, y + 6 - h, 3, 1, leaf); R(x + 3, y + 5 - h, 1, 1, leaf);
        if (grow > 0) { R(x + 2, y + 4 - h, 1, 1, Color.FromArgb(255, 105, 97)); R(x + 4, y + 4 - h, 1, 1, Color.FromArgb(255, 105, 97)); R(x + 3, y + 3 - h, 1, 1, Color.FromArgb(255, 196, 46)); }
    }

    static void At(Graphics g, int px, int py, int unit) { cg = g; cx = px; cy = py; cu = unit; }

    // звёздное небо: точки мерцают по очереди
    static void Stars(int t, int total)
    {
        int[][] st = { new[] { 4, 3 }, new[] { 15, 7 }, new[] { 27, 2 }, new[] { 40, 5 }, new[] { 52, 2 }, new[] { 61, 8 }, new[] { 73, 3 }, new[] { 86, 6 }, new[] { 10, 12 }, new[] { 34, 10 }, new[] { 57, 13 }, new[] { 79, 11 } };
        for (int i = 0; i < st.Length; i++)
        {
            int ph = (t * 3 * 8 / total + i * 5) % 8;   // 6 полных циклов мерцания за петлю — стык незаметен
            var c = ph == 0 ? W : (ph < 3 ? Color.FromArgb(120, 120, 128) : Color.FromArgb(55, 55, 62));
            R(st[i][0], st[i][1], 1, 1, c);
            if (ph == 0) { R(st[i][0] - 1, st[i][1], 1, 1, Color.FromArgb(90, 90, 98)); R(st[i][0] + 1, st[i][1], 1, 1, Color.FromArgb(90, 90, 98)); }
        }
    }

    // монитор на подставке: 0 — заставка (мигает курсор), 1 — бежит код, 2 — «OK»
    static void Monitor2(int x, int y, int t, int mode)
    {
        if (mode == 0)
        {
            var fc = Color.FromArgb(200, 200, 206);
            R(x, y, 17, 13, fc); R(x + 1, y + 1, 15, 11, Bk);
            R(x + 7, y + 13, 3, 2, fc); R(x + 4, y + 15, 9, 1, fc);
            if (t % 6 < 3) R(x + 2, y + 2, 1, 2, Color.FromArgb(150, 150, 156));
            return;
        }
        Monitor(x, y, t, mode == 2);
    }

    // лампочка-идея с лучами
    static void Bulb(int x, int y, int ph)
    {
        var glow = Color.FromArgb(255, 220, 90); var dark = Color.FromArgb(120, 100, 40);
        var c = ph == 0 ? dark : glow;
        R(x + 1, y, 3, 1, c); R(x, y + 1, 5, 3, c); R(x + 1, y + 4, 3, 1, c);
        R(x + 1, y + 5, 3, 1, Color.FromArgb(160, 160, 166)); R(x + 2, y + 6, 1, 1, Color.FromArgb(160, 160, 166));
        if (ph > 0 && ph % 2 == 1)
        {
            R(x - 2, y + 2, 1, 1, glow); R(x + 6, y + 2, 1, 1, glow); R(x + 2, y - 2, 1, 1, glow);
            R(x - 1, y - 1, 1, 1, glow); R(x + 5, y - 1, 1, 1, glow);
        }
    }

    // ── широкий экран: персонаж заходит слева, останавливается, машет, моргает и уходит вправо ──
    static void Wide()
    {
        const int WW = 412, WH = 196, unit = 5;              // два места кнопок с промежутком
        int spriteW = 36 * unit;
        var frames = new List<Bitmap>(); var delays = new List<int>();
        Action<Sp, int, int> add = (s, x, d) =>
        {
            var b = new Bitmap(WW, WH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                Setup(g); g.Clear(Bk); under = Bk;
                DrawSprite(g, s, Light, x, (WH - 22 * unit) / 2 + 6, unit);
            }
            frames.Add(b); delays.Add(d);
        };
        int center = (WW - spriteW) / 2;
        int step = 10, i = 0;
        for (int x = -spriteW; x < center; x += step, i++)
            add(P(s => { s.Legs = i % 2 == 0 ? 1 : 2; s.Dy = i % 2; s.Ex = 1; }), x, 8);
        add(P(s => { s.Ex = 0; }), center, 40);
        add(P(s => { s.Eyes = Eye.Closed; }), center, 10);
        add(P(s => { s.Ex = -1; }), center, 40);
        for (int k = 0; k < 6; k++) { int kk = k; add(P(s => { s.R = kk % 2 == 0 ? Arm.Wave1 : Arm.Wave2; s.Eyes = Eye.Happy; }), center, 14); }
        add(P(s => { s.Ex = 1; }), center, 40);
        i = 0;
        for (int x = center + step; x < WW; x += step, i++)
            add(P(s => { s.Legs = i % 2 == 0 ? 1 : 2; s.Dy = i % 2; s.Ex = 1; }), x, 8);
        add(new Sp(), WW + 10, 150);                           // пустой экран — пауза
        frames[0].Save(Path.Combine(outDir, "claude_wide.png"), ImageFormat.Png);
        Gif.Write(Path.Combine(outDir, "claude_wide.gif"), frames, delays.ToArray());
        foreach (var b in frames) b.Dispose();
    }

    // ── пиксельный шрифт 5×7 — тот же, что у плагина (plugin\pixpng.js), чтобы подписи везде были одинаковые ──
    static Dictionary<char, string[]> pixFont;
    static void LoadPixFont()
    {
        pixFont = new Dictionary<char, string[]>();
        string js = File.ReadAllText(Path.Combine(Path.GetDirectoryName(outDir.TrimEnd('\\', '/')), "plugin", "pixpng.js"));
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(js, @"'(.)':\s*\[([^\]]*)\]"))
        {
            var rows = System.Text.RegularExpressions.Regex.Matches(m.Groups[2].Value, @"'([.#]{5})'");
            if (rows.Count != 7) continue;
            var g = new string[7];
            for (int i = 0; i < 7; i++) g[i] = rows[i].Groups[1].Value;
            pixFont[m.Groups[1].Value[0]] = g;
        }
    }

    static void PixText(Graphics g, string text, int centerX, int y, int s, Color c)
    {
        if (pixFont == null) LoadPixFont();
        text = text.ToUpperInvariant();
        int w = text.Length * 6 * s - s, x = centerX - w / 2;
        var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
        var pom = g.PixelOffsetMode; g.PixelOffsetMode = PixelOffsetMode.Half;
        using (var br = new SolidBrush(c))
            foreach (char ch in text)
            {
                string[] gl;
                if (pixFont.TryGetValue(ch, out gl))
                    for (int j = 0; j < 7; j++) for (int i = 0; i < 5; i++) if (gl[j][i] == '#') g.FillRectangle(br, x + i * s, y + j * s, s, s);
                x += 6 * s;
            }
        g.SmoothingMode = mode; g.PixelOffsetMode = pom;
    }

    static Font MakeFont(float size)
    {
        var f = new Font(FontName, size, FontStyle.Regular, GraphicsUnit.Pixel);
        if (f.Name != FontName) { f.Dispose(); f = new Font(FontFallback, size, FontStyle.Regular, GraphicsUnit.Pixel); }
        return f;
    }

    // значки под подписью — тоже из пикселей
    static void Mark(Graphics g, string mark, Color c)
    {
        if (mark == null) return;
        var mode = g.SmoothingMode; g.SmoothingMode = SmoothingMode.None;
        using (var br = new SolidBrush(c))
        using (var dim = new SolidBrush(Color.FromArgb(70, 70, 72)))
        {
            if (mark.StartsWith("dots"))
            {
                int n = mark[4] - '0';
                for (int i = 0; i < 3; i++) g.FillRectangle(i < n ? br : dim, 78 + i * 15, MarkY, 9, 9);
            }
            else if (mark.StartsWith("run"))
            {
                // бегущая полоска: светлый отрезок едет по дорожке
                int n = int.Parse(mark.Substring(3));
                for (int i = 0; i < 8; i++) g.FillRectangle(((i - n + 8) % 8 < 3) ? br : dim, 50 + i * 12, MarkY + 2, 8, 6);
            }
            else if (mark.StartsWith("full"))
            {
                // полоска контекста: почти вся оранжевая, последний квадрат мигает
                var amber = new SolidBrush(Color.FromArgb(255, 159, 10));
                bool blink = mark == "full1";
                for (int i = 0; i < 8; i++) g.FillRectangle(i < 6 || (i == 6 && blink) ? amber : dim, 50 + i * 12, MarkY + 2, 8, 6);
                amber.Dispose();
            }
            else if (mark == "check")
            {
                int[,] pts = { { 0, 2 }, { 1, 3 }, { 2, 4 }, { 3, 3 }, { 4, 2 }, { 5, 1 }, { 6, 0 } };
                for (int i = 0; i < pts.GetLength(0); i++) g.FillRectangle(br, 81 + pts[i, 0] * 5, MarkY - 4 + pts[i, 1] * 5, 5, 5);
            }
        }
        g.SmoothingMode = mode;
    }

    // ════════════ кнопки управления ════════════
    static void Icon(string name, bool inverted, Color c, Action<Graphics, Color> draw)
    {
        using (var b = new Bitmap(S, S, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(b))
        {
            Setup(g);
            g.Clear(Bk);
            if (inverted)
                using (var p = Rounded(new RectangleF(10, 10, S - 20, S - 20), 30)) using (var br = new SolidBrush(W)) g.FillPath(br, p);
            draw(g, c);
            b.Save(Path.Combine(outDir, name + ".png"), ImageFormat.Png);
        }
    }

    static Pen P(Color c) { return new Pen(c, 11f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }; }

    static void Check(Graphics g, Color c)
    {
        using (var p = P(c)) { g.DrawEllipse(p, 46, 46, 104, 104); g.DrawLines(p, new[] { new PointF(74, 99), new PointF(91, 116), new PointF(123, 82) }); }
    }

    static void Cross(Graphics g, Color c)
    {
        using (var p = P(c)) { g.DrawEllipse(p, 46, 46, 104, 104); g.DrawLine(p, 79, 79, 117, 117); g.DrawLine(p, 117, 79, 79, 117); }
    }

    static void StopSign(Graphics g, Color c)
    {
        using (var p = P(c)) g.DrawEllipse(p, 46, 46, 104, 104);
        using (var path = Rounded(new RectangleF(79, 79, 38, 38), 6)) using (var br = new SolidBrush(c)) g.FillPath(br, path);
    }

    static void NewChat(Graphics g, Color c)
    {
        Glyph(g, "chat.png", new RectangleF(34, 40, 116, 116));
        using (var br = new SolidBrush(Bk)) g.FillEllipse(br, 112, 104, 62, 62);
        using (var p = new Pen(c, 9f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
        {
            g.DrawEllipse(p, 118, 110, 50, 50);
            g.DrawLine(p, 143, 124, 143, 146); g.DrawLine(p, 132, 135, 154, 135);
        }
    }

    static void Mic(Graphics g, Color c)
    {
        using (var p = P(c))
        {
            using (var cap = Rounded(new RectangleF(78, 40, 40, 70), 20)) g.DrawPath(p, cap);
            g.DrawArc(p, 60, 64, 76, 70, 0, 180);
            g.DrawLine(p, 98, 134, 98, 154);
            g.DrawLine(p, 78, 156, 118, 156);
        }
    }

    // диктовка включена: белая плашка, микрофон и плавно расходящиеся волны (16 кадров, без резких скачков)
    static void DictationOn()
    {
        var frames = new List<Bitmap>(); var delays = new List<int>();
        const int N = 16;
        for (int i = 0; i < N; i++)
        {
            var b = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                Setup(g); g.Clear(Bk);
                using (var p = Rounded(new RectangleF(10, 10, S - 20, S - 20), 30)) using (var br = new SolidBrush(W)) g.FillPath(br, p);
                Mic(g, Bk);
                for (int w = 0; w < 3; w++)
                {
                    // волна «бежит» наружу: яркость по синусу со сдвигом для каждой дуги
                    double ph = 2 * Math.PI * ((double)i / N - w / 3.0);
                    int alpha = (int)(30 + 225 * (0.5 + 0.5 * Math.Cos(ph)));
                    float r = 18 + w * 14 + (float)(2 * Math.Sin(ph));
                    using (var pen = new Pen(Color.FromArgb(alpha, 0, 0, 0), 7f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    {
                        g.DrawArc(pen, 98 - 20 - r, 75 - r, 2 * r, 2 * r, 150, 60);
                        g.DrawArc(pen, 98 + 20 - r, 75 - r, 2 * r, 2 * r, -30, 60);
                    }
                }
            }
            frames.Add(b); delays.Add(6);
        }
        frames[0].Save(Path.Combine(outDir, "dictation_on.png"), ImageFormat.Png);
        Gif.Write(Path.Combine(outDir, "dictation_on.gif"), frames, delays.ToArray());
        foreach (var b in frames) b.Dispose();
    }

    static void Glyph(Graphics g, string file, RectangleF r)
    {
        using (var img = Image.FromFile(Path.Combine(glyphDir, file)))
        {
            float k = img.Width / 196f;
            var src = new RectangleF(30 * k, 30 * k, 136 * k, 136 * k);
            g.DrawImage(img, new[] { new PointF(r.X, r.Y), new PointF(r.Right, r.Y), new PointF(r.X, r.Bottom) }, src, GraphicsUnit.Pixel);
        }
    }

    static void Setup(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
    }

    static void Text(Graphics g, string s, Font f, Color c, RectangleF r)
    {
        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
        using (var br = new SolidBrush(c)) g.DrawString(s, f, br, r, sf);
    }

    static GraphicsPath Rounded(RectangleF r, float rad)
    {
        var p = new GraphicsPath(); float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}

// Простой кодировщик GIF89a: общая палитра до 256 цветов, бесконечный повтор, кадры целиком.
static class Gif
{
    public static void Write(string path, List<Bitmap> frames, int[] delays, bool delta = false)
    {
        int w = frames[0].Width, h = frames[0].Height;
        var palette = new List<int>();
        var index = new Dictionary<int, byte>();
        var data = new List<byte[]>();
        bool quantize = false;
        foreach (var f in frames)
            foreach (var c in Pixels(f)) if (!index.ContainsKey(c)) { if (palette.Count >= 256) { quantize = true; break; } index[c] = (byte)palette.Count; palette.Add(c); }
        if (quantize)
        {
            // цветов больше 256 — сводим к 6×6×6 уровням
            palette.Clear(); index.Clear();
            for (int r = 0; r < 6; r++) for (int g = 0; g < 6; g++) for (int b = 0; b < 6; b++) palette.Add((r * 51 << 16) | (g * 51 << 8) | b * 51);
        }
        foreach (var f in frames)
        {
            var px = Pixels(f);
            var idx = new byte[px.Length];
            for (int i = 0; i < px.Length; i++)
            {
                int c = px[i];
                if (quantize) idx[i] = (byte)(((c >> 16 & 255) + 25) / 51 * 36 + ((c >> 8 & 255) + 25) / 51 * 6 + ((c & 255) + 25) / 51);
                else idx[i] = index[c];
            }
            data.Add(idx);
        }
        // delta: кадр после первого — только прямоугольник с изменениями, неизменные точки прозрачные
        // (кадр рисуется поверх предыдущего) — файл в разы легче, когда двигается немногое
        int tr = -1;
        var rects = new List<int[]>();
        for (int i = 0; i < data.Count; i++) rects.Add(new[] { 0, 0, w, h });
        if (delta && !quantize && palette.Count < 256)
        {
            tr = palette.Count; palette.Add(0);
            for (int i = data.Count - 1; i >= 1; i--)
            {
                byte[] cur = data[i], prev = data[i - 1];
                int x0 = w, y0 = h, x1 = -1, y1 = -1;
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    if (cur[y * w + x] != prev[y * w + x]) { if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y; }
                if (x1 < 0) { x0 = 0; y0 = 0; x1 = 0; y1 = 0; }
                int rw = x1 - x0 + 1, rh = y1 - y0 + 1;
                var sub = new byte[rw * rh];
                for (int y = 0; y < rh; y++) for (int x = 0; x < rw; x++)
                {
                    int p = (y0 + y) * w + x0 + x;
                    sub[y * rw + x] = cur[p] == prev[p] ? (byte)tr : cur[p];
                }
                data[i] = sub; rects[i] = new[] { x0, y0, rw, rh };
            }
        }
        while (palette.Count < 256) palette.Add(0);

        using (var fs = new FileStream(path, FileMode.Create))
        using (var bw = new BinaryWriter(fs))
        {
            bw.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
            bw.Write((ushort)w); bw.Write((ushort)h);
            bw.Write((byte)0xF7); bw.Write((byte)0); bw.Write((byte)0);
            foreach (var c in palette) { bw.Write((byte)(c >> 16 & 255)); bw.Write((byte)(c >> 8 & 255)); bw.Write((byte)(c & 255)); }
            // NETSCAPE2.0 — повторять бесконечно
            bw.Write(new byte[] { 0x21, 0xFF, 0x0B });
            bw.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            bw.Write(new byte[] { 3, 1, 0, 0, 0 });
            for (int i = 0; i < data.Count; i++)
            {
                bool t = tr >= 0 && i > 0;
                bw.Write(new byte[] { 0x21, 0xF9, 4, (byte)(t ? 0x05 : 0x04) }); bw.Write((ushort)Math.Max(2, delays[i])); bw.Write((byte)(t ? tr : 0)); bw.Write((byte)0);
                var r = rects[i];
                bw.Write((byte)0x2C); bw.Write((ushort)r[0]); bw.Write((ushort)r[1]); bw.Write((ushort)r[2]); bw.Write((ushort)r[3]); bw.Write((byte)0);
                bw.Write((byte)8);
                var lzw = Lzw(data[i]);
                for (int p = 0; p < lzw.Length; p += 255)
                {
                    int n = Math.Min(255, lzw.Length - p);
                    bw.Write((byte)n); bw.Write(lzw, p, n);
                }
                bw.Write((byte)0);
            }
            bw.Write((byte)0x3B);
        }
    }

    static int[] Pixels(Bitmap b)
    {
        var res = new int[b.Width * b.Height];
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        System.Runtime.InteropServices.Marshal.Copy(d.Scan0, res, 0, res.Length); // stride = ширина × 4 для 32bpp
        b.UnlockBits(d);
        for (int i = 0; i < res.Length; i++)
        {
            int a = res[i] >> 24 & 255;
            if (a == 255) res[i] &= 0xFFFFFF;
            else // полупрозрачное — смешиваем с чёрным фоном
                res[i] = ((res[i] >> 16 & 255) * a / 255 << 16) | ((res[i] >> 8 & 255) * a / 255 << 8) | ((res[i] & 255) * a / 255);
        }
        return res;
    }

    // LZW для GIF: коды переменной длины, младшие биты первыми
    static byte[] Lzw(byte[] px)
    {
        const int min = 8, clear = 1 << min, eoi = clear + 1;
        var output = new List<byte>();
        int bitBuf = 0, bitCount = 0, size = min + 1, next = eoi + 1;
        var dict = new Dictionary<int, int>();
        Action<int> emit = code =>
        {
            bitBuf |= code << bitCount; bitCount += size;
            while (bitCount >= 8) { output.Add((byte)(bitBuf & 255)); bitBuf >>= 8; bitCount -= 8; }
        };
        emit(clear);
        int prefix = px[0];
        for (int i = 1; i < px.Length; i++)
        {
            int k = px[i], key = (prefix << 8) | k, code;
            if (dict.TryGetValue(key, out code)) { prefix = code; continue; }
            emit(prefix);
            if (next < 4096)
            {
                dict[key] = next++;
                if (next > (1 << size) && size < 12) size++;
            }
            else
            {
                emit(clear);
                dict.Clear(); next = eoi + 1; size = min + 1;
            }
            prefix = k;
        }
        emit(prefix);
        emit(eoi);
        if (bitCount > 0) output.Add((byte)(bitBuf & 255));
        return output.ToArray();
    }
}
