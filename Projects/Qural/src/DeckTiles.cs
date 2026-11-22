using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;

namespace Qural
{
    // Картинки для кнопок деки в стиле набора иконок пользователя: чёрный фон, белый контурный значок по центру.
    // Значки программ — белые PNG 196×196 с прозрачным фоном из папок deck-icons (рядом с Qural.exe и в %APPDATA%\Qural).
    // Имя файла = имя exe (spotify.png, discord.png); «pubg 1.png», «figma@1x.png» тоже подходят.
    static class DeckTiles
    {
        public sealed class State
        {
            public string Process = "", Title = "";
            public bool Running, Muted, Active, SoundOff, Unknown; // Unknown — «Активное окно», а окна нет
            public int Volume;
        }

        static readonly Color White = Color.White, Dim = Color.FromArgb(90, 90, 92), Track = Color.FromArgb(48, 48, 50),
            Red = Color.FromArgb(255, 69, 58);

        // ── значки ──
        static readonly Dictionary<string, string> aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "tslgame", "pubg" }, { "claude", "claude-c" }, { "msedge", "edge" }, { "firefox", "firefox" },
            { "applemusic", "music" }, { "yandexmusic", "yandex-music" }
        };
        static Dictionary<string, string> files; // имя → путь
        static DateTime filesAt;
        static readonly Dictionary<string, Bitmap> glyphs = new Dictionary<string, Bitmap>(StringComparer.OrdinalIgnoreCase);

        public static IEnumerable<string> Folders()
        {
            yield return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "deck-icons");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Qural", "deck-icons");
        }

        static string Norm(string name)
        {
            string n = name.ToLowerInvariant().Replace("@1x", "").Replace("@2x", "").Trim();
            // «pubg 1», «figma 2» — номер варианта в конце отбрасываем
            int sp = n.LastIndexOf(' ');
            if (sp > 0) { int d; if (int.TryParse(n.Substring(sp + 1), out d)) n = n.Substring(0, sp); }
            return n.Replace(" ", "");
        }

        static void Scan()
        {
            // папки пересматриваем не чаще раза в 10 секунд — можно подложить значок, не перезапуская Qural
            if (files != null && (DateTime.UtcNow - filesAt).TotalSeconds < 10) return;
            var res = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dir in Folders())
                try
                {
                    if (!Directory.Exists(dir)) continue;
                    foreach (var f in Directory.GetFiles(dir, "*.png"))
                        res[Norm(Path.GetFileNameWithoutExtension(f))] = f; // файлы из %APPDATA% (вторая папка) важнее
                }
                catch (Exception) { }
            bool changed = files == null || res.Count != files.Count;
            if (!changed) foreach (var kv in res) { string p; if (!files.TryGetValue(kv.Key, out p) || p != kv.Value) { changed = true; break; } }
            if (changed) { foreach (var b in glyphs.Values) if (b != null) b.Dispose(); glyphs.Clear(); }
            files = res; filesAt = DateTime.UtcNow;
        }

        public static Bitmap Glyph(string process)
        {
            if (string.IsNullOrEmpty(process)) return null;
            Scan();
            Bitmap b;
            if (glyphs.TryGetValue(process, out b)) return b;
            string key = Norm(process), path = null, alias;
            if (!files.TryGetValue(key, out path) && aliases.TryGetValue(key, out alias)) files.TryGetValue(alias, out path);
            b = null;
            if (path != null)
                try { using (var img = Image.FromFile(path)) b = new Bitmap(img); } catch (Exception) { b = null; }
            glyphs[process] = b;
            return b;
        }

        // ── кнопка 196×196 ──
        public static Bitmap Key(State s)
        {
            const int S = 196;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Setup(g);
                g.Clear(Color.Black);
                bool live = s.Running && !s.SoundOff && !s.Unknown;
                bool bright = live && !s.Muted;
                var glyph = Glyph(s.Process);

                // значок: вверху, чуть меньше, чем у обычных кнопок, — под ним громкость
                var ir = new Rectangle((S - 104) / 2, 14, 104, 104);
                if (glyph != null) DrawGlyph(g, glyph, ir, bright ? 1f : .38f);
                else
                {
                    Speaker(g, new RectangleF(ir.X + 14, ir.Y + 6, 76, 76), bright ? White : Dim, s.Muted && live);
                    using (var f = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel))
                        Text(g, s.Unknown ? "Активное окно" : s.Title, f, bright ? White : Dim, new RectangleF(6, ir.Y + 84, S - 12, 22));
                }
                if (s.Active) Badge(g, new RectangleF(ir.Right - 22, ir.Y + 2, 26, 26));
                if (s.Muted && live && glyph != null) Slash(g, ir);

                // громкость
                string big; Color c = White; float size = 34f;
                if (s.SoundOff) { big = "звук выкл."; c = Dim; size = 22f; }
                else if (!live) { big = "—"; c = Dim; }
                else if (s.Muted) { big = "без звука"; c = Red; size = 24f; }
                else big = s.Volume + "%";
                using (var f = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel))
                    Text(g, big, f, c, new RectangleF(4, 122, S - 8, 40));

                // тонкая полоска
                var bar = new RectangleF(34, 170, S - 68, 6);
                using (var p = Rounded(bar, 3f)) using (var br = new SolidBrush(Track)) g.FillPath(br, p);
                if (live && s.Volume > 0)
                {
                    var fill = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * Math.Min(100, s.Volume) / 100f), bar.Height);
                    using (var p = Rounded(fill, 3f)) using (var br = new SolidBrush(s.Muted ? Dim : White)) g.FillPath(br, p);
                }
            }
            return bmp;
        }

        // ── экран крутилки: место под картинку 100×100 (рисуем вдвое крупнее), подпись Ulanzi выводит сама ──
        public static Bitmap Dial(State s)
        {
            const int S = 200;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Setup(g);
                g.Clear(Color.Transparent);
                bool live = s.Running && !s.SoundOff && !s.Unknown;
                bool bright = live && !s.Muted;
                var ring = new RectangleF(12, 12, S - 24, S - 24);
                using (var pen = new Pen(Track, 12f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, ring, 135, 270);
                if (live && s.Volume > 0)
                    using (var pen = new Pen(s.Muted ? Dim : White, 12f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(pen, ring, 135, Math.Max(2f, 270f * Math.Min(100, s.Volume) / 100f));
                var ir = new Rectangle((S - 112) / 2, (S - 112) / 2, 112, 112);
                var glyph = Glyph(s.Process);
                if (glyph != null) DrawGlyph(g, glyph, ir, bright ? 1f : .38f);
                else Speaker(g, new RectangleF(ir.X + 16, ir.Y + 16, 80, 80), bright ? White : Dim, s.Muted && live);
                if (s.Muted && live && glyph != null) Slash(g, ir);
                if (s.Active) Badge(g, new RectangleF(ir.Right - 26, ir.Bottom - 30, 32, 32));
            }
            return bmp;
        }

        // ── кнопки профиля цвета: монитор с номером профиля, под ним название ──
        public sealed class ColorState
        {
            public string Name = "";
            public int Number;
            public bool Next, On, Missing, ColorOff;
        }

        public static Bitmap ColorKey(ColorState s)
        {
            const int S = 196;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Setup(g);
                g.Clear(Color.Black);
                // только значок, без надписей: включённый профиль — белый, остальные — серые
                bool bright = !s.ColorOff && !s.Missing && (s.On || s.Next);
                Color c = bright ? White : Dim;
                // «следующий» выглядит так же: номер текущего профиля, по нажатию — следующий
                Monitor(g, new RectangleF(36, 50, 124, 98), c, s.Missing ? "?" : s.Number.ToString(), 10f);
            }
            return bmp;
        }

        public static Bitmap ColorDial(ColorState s)
        {
            const int S = 200;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Setup(g);
                g.Clear(Color.Transparent);
                bool bright = !s.ColorOff && !s.Missing;
                Monitor(g, new RectangleF(24, 34, 152, 104), bright ? White : Dim, s.Missing ? "?" : s.Number.ToString(), 12f);
            }
            return bmp;
        }

        // монитор контуром, как «Цвет NVIDIA 1» из набора: экран, ножка, подставка, номер внутри
        static void Monitor(Graphics g, RectangleF r, Color c, string text, float t)
        {
            using (var pen = new Pen(c, t) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                float h = r.Height * .78f;
                using (var p = Rounded(new RectangleF(r.X, r.Y, r.Width, h), r.Width * .12f)) g.DrawPath(pen, p);
                float cx = r.X + r.Width / 2, by = r.Bottom;
                g.DrawLine(pen, cx, r.Y + h, cx, by - t * .3f);
                g.DrawLine(pen, cx - r.Width * .22f, by, cx + r.Width * .22f, by);
                using (var f = new Font("Segoe UI", h * .52f, FontStyle.Bold, GraphicsUnit.Pixel))
                    Text(g, text, f, c, new RectangleF(r.X, r.Y + 2, r.Width, h));
            }
        }

        // две стрелки по кругу — «следующий профиль»
        static void Cycle(Graphics g, RectangleF r, Color c)
        {
            using (var pen = new Pen(c, r.Width * .13f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                var a = RectangleF.Inflate(r, -r.Width * .14f, -r.Width * .14f);
                g.DrawArc(pen, a, 200, 120);
                g.DrawArc(pen, a, 20, 120);
                float cx = a.X + a.Width / 2, cy = a.Y + a.Height / 2, rad = a.Width / 2, k = r.Width * .2f;
                // наконечники в концах дуг (320° и 140°)
                foreach (var deg in new[] { 320.0, 140.0 })
                {
                    double ang = deg * Math.PI / 180;
                    float x = cx + (float)(rad * Math.Cos(ang)), y = cy + (float)(rad * Math.Sin(ang));
                    double tan = ang + Math.PI / 2; // направление движения по дуге
                    for (int sgn = -1; sgn <= 1; sgn += 2)
                    {
                        double back = tan + Math.PI + sgn * .6;
                        g.DrawLine(pen, x, y, x + (float)(k * Math.Cos(back)), y + (float)(k * Math.Sin(back)));
                    }
                }
            }
        }

        // ── «Сейчас играет»: обложка сверху, название и исполнитель, тонкая полоска позиции ──
        // обложки: держим последние три (для смены трека нужны старая и новая)
        static readonly List<KeyValuePair<byte[], Bitmap>> covers = new List<KeyValuePair<byte[], Bitmap>>();
        static Bitmap Cover(byte[] data)
        {
            if (data == null) return null;
            foreach (var kv in covers) if (ReferenceEquals(kv.Key, data)) return kv.Value;
            Bitmap b = null;
            try { using (var ms = new MemoryStream(data)) using (var img = Image.FromStream(ms)) b = new Bitmap(img); } catch (Exception) { b = null; }
            covers.Add(new KeyValuePair<byte[], Bitmap>(data, b));
            while (covers.Count > 3) { if (covers[0].Value != null) covers[0].Value.Dispose(); covers.RemoveAt(0); }
            return b;
        }

        // обложка квадратом: у видео (16:9) берём середину
        static void DrawCover(Graphics g, Bitmap c, RectangleF r, float radius, float alpha)
        {
            float side = Math.Min(c.Width, c.Height);
            var src = new RectangleF((c.Width - side) / 2, (c.Height - side) / 2, side, side);
            var state = g.Save();
            using (var clip = Rounded(r, radius)) g.SetClip(clip);
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = alpha });
                ia.SetWrapMode(WrapMode.TileFlipXY);   // без светлой каймы по краю
                g.DrawImage(c, new[] { new PointF(r.X, r.Y), new PointF(r.Right, r.Y), new PointF(r.X, r.Bottom) }, src, GraphicsUnit.Pixel, ia);
            }
            g.Restore(state);
        }

        // нота контуром — когда обложки нет
        static void Note(Graphics g, RectangleF r, Color c)
        {
            float w = r.Width, x = r.X, y = r.Y;
            using (var pen = new Pen(c, w * .09f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                g.DrawLines(pen, new[] { new PointF(x + w * .38f, y + w * .74f), new PointF(x + w * .38f, y + w * .18f), new PointF(x + w * .82f, y + w * .08f), new PointF(x + w * .82f, y + w * .64f) });
                g.DrawEllipse(pen, x + w * .12f, y + w * .64f, w * .26f, w * .2f);
                g.DrawEllipse(pen, x + w * .56f, y + w * .54f, w * .26f, w * .2f);
            }
        }

        // сердечко «Любимые треки» в углу обложки: зелёное (как в Spotify) — в любимых, белый контур — нет
        static void Heart(Graphics g, float cx, float cy, float d, bool liked)
        {
            using (var br = new SolidBrush(Color.Black)) g.FillEllipse(br, cx - d / 2, cy - d / 2, d, d);
            float w = d * .56f, x = cx - w / 2, y = cy - w * .42f;
            using (var p = new GraphicsPath())
            {
                p.AddBezier(x + w * .5f, y + w * .95f, x - w * .05f, y + w * .55f, x - w * .02f, y + w * .02f, x + w * .5f, y + w * .25f);
                p.AddBezier(x + w * .5f, y + w * .25f, x + w * 1.02f, y + w * .02f, x + w * 1.05f, y + w * .55f, x + w * .5f, y + w * .95f);
                p.CloseFigure();
                if (liked) using (var br = new SolidBrush(Color.FromArgb(30, 215, 96))) g.FillPath(br, p);
                else using (var pen = new Pen(White, d * .07f) { LineJoin = LineJoin.Round }) g.DrawPath(pen, p);
            }
        }

        // значок паузы поверх обложки (плеер на паузе)
        static void PauseMark(Graphics g, RectangleF r)
        {
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, h = r.Height * .36f, w = r.Width * .11f;
            using (var br = new SolidBrush(Color.FromArgb(150, 0, 0, 0))) g.FillEllipse(br, cx - h * .95f, cy - h * .95f, h * 1.9f, h * 1.9f);
            using (var br = new SolidBrush(White))
            {
                using (var p = Rounded(new RectangleF(cx - w * 1.6f, cy - h / 2, w, h), w / 2)) g.FillPath(br, p);
                using (var p = Rounded(new RectangleF(cx + w * .6f, cy - h / 2, w, h), w / 2)) g.FillPath(br, p);
            }
        }

        public static Bitmap MediaKey(Media.Info m) { return MediaFrame(m, 0f, null, 1f, 1); }

        const int MediaS = 196, TitleX = 16, TitleW = MediaS - 32, Gap = 44;
        static Font titleFont, artistFont;
        static Font TitleFont { get { return titleFont ?? (titleFont = new Font("Segoe UI Semibold", 18f, GraphicsUnit.Pixel)); } }
        static Font ArtistFont { get { return artistFont ?? (artistFont = new Font("Segoe UI Semibold", 15f, GraphicsUnit.Pixel)); } }

        // ширина названия в пикселях (не влезает в TitleW — бегущая строка)
        public static float TitleWidth(string title)
        {
            using (var b = new Bitmap(1, 1)) using (var g = Graphics.FromImage(b))
            {
                Setup(g);
                using (var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces })
                    return g.MeasureString(title ?? "", TitleFont, 10000, sf).Width;
            }
        }

        // Кадр кнопки «Сейчас играет». scroll — сдвиг бегущей строки (пиксели); old и t (0…1) — смена трека:
        // старая обложка уезжает влево, новая въезжает справа (dir = -1, предыдущий трек, — наоборот), подписи плавно сменяются.
        static Bitmap MediaFrame(Media.Info m, float scroll, Media.Info old, float t, int dir)
        {
            const int S = MediaS;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Setup(g);
                g.Clear(Color.Black);
                var cr = new RectangleF((S - 100) / 2f, 14, 100, 100);
                float e = 1f - (1f - t) * (1f - t);                   // плавно тормозит в конце
                if (old != null && t < 1f)
                {
                    var co = old.Has ? Cover(old.Cover) : null;
                    var r0 = new RectangleF(cr.X - dir * e * (S - cr.X + 8), cr.Y, cr.Width, cr.Height);
                    if (co != null) DrawCover(g, co, r0, 14, (old.Playing ? 1f : .5f) * (1f - e * .6f));
                    else Note(g, RectangleF.Inflate(r0, -14, -14), Dim);
                    cr = new RectangleF(cr.X + dir * (1f - e) * (S - cr.X + 8), cr.Y, cr.Width, cr.Height);
                }
                var c = m.Has ? Cover(m.Cover) : null;
                if (c != null) DrawCover(g, c, cr, 14, m.Playing ? 1f : .5f);
                else Note(g, RectangleF.Inflate(cr, -14, -14), m.Has && m.Playing ? White : Dim);
                if (m.Has && !m.Playing && (old == null || t >= 1f)) PauseMark(g, cr);
                if (m.Liked.HasValue) Heart(g, cr.Right - 6, cr.Y + 6, 34, m.Liked.Value);

                // подписи: старые гаснут в первой половине перехода, новые проявляются во второй — не наслаиваются
                float ta = old != null && t < 1f ? Math.Max(0f, 2f * t - 1f) : 1f;
                if (old != null && t < .5f && old.Has) Captions(g, old, 0f, 1f - 2f * t);
                if (!m.Has)
                {
                    using (var f = new Font("Segoe UI Semibold", 17f, GraphicsUnit.Pixel)) Text(g, "Тишина", f, Fade(Dim, ta), new RectangleF(10, 124, S - 20, 26));
                    return bmp;
                }
                Captions(g, m, scroll, ta);
                // позиция в треке
                var bar = new RectangleF(34, 170, S - 68, 6);
                using (var p = Rounded(bar, 3f)) using (var br = new SolidBrush(Track)) g.FillPath(br, p);
                if (m.Duration > 1)
                {
                    var fill = new RectangleF(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * (float)Math.Min(1, m.Position / m.Duration)), bar.Height);
                    using (var p = Rounded(fill, 3f)) using (var br = new SolidBrush(m.Playing ? White : Dim)) g.FillPath(br, p);
                }
            }
            return bmp;
        }

        static Color Fade(Color c, float a) { return Color.FromArgb((int)(255 * Math.Max(0, Math.Min(1, a))), c); }

        // название (бегущей строкой, если не влезает) и исполнитель
        static void Captions(Graphics g, Media.Info m, float scroll, float alpha)
        {
            var tc = Fade(m.Playing ? White : Dim, alpha);
            float w = TitleWidth(m.Title);
            if (w <= TitleW) Text(g, m.Title, TitleFont, tc, new RectangleF(TitleX, 120, TitleW, 24));
            else
            {
                var state = g.Save();
                g.SetClip(new RectangleF(TitleX, 118, TitleW, 28));
                using (var br = new SolidBrush(tc))
                using (var sf = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces })
                {
                    float x = TitleX - scroll;
                    g.DrawString(m.Title, TitleFont, br, x, 121, sf);
                    g.DrawString(m.Title, TitleFont, br, x + w + Gap, 121, sf);
                }
                g.Restore(state);
                // края строки мягко уходят в чёрное
                for (int i = 0; i < 10; i++)
                    using (var br = new SolidBrush(Color.FromArgb(255 - i * 25, 0, 0, 0)))
                    { g.FillRectangle(br, TitleX + i, 118, 1, 28); g.FillRectangle(br, TitleX + TitleW - 1 - i, 118, 1, 28); }
            }
            // исполнитель: светлее и чуть крупнее (серый 150 на деке почти не читался)
            Text(g, m.Artist.Length > 0 ? m.Artist : m.App, ArtistFont, Fade(m.Playing ? Color.FromArgb(214, 214, 220) : Color.FromArgb(160, 160, 166), alpha), new RectangleF(14, 143, MediaS - 28, 21));
        }

        // ── бегущая строка кусками: 1 пиксель за кадр, кадр 30 мс (~33 кадра/с — на деке плавно). Ulanzi не показывает
        // GIF длиннее ~300 кадров (377 — «?»), поэтому длинное название едет несколькими GIF подряд по SegLen кадров:
        // каждый следующий начинается там, где кончился предыдущий. ──
        public const int SegLen = 230;

        // сколько пикселей проехать, чтобы показался конец названия (0 — влезает, бегущая строка не нужна)
        public static float MarqueeRun(Media.Info m)
        {
            if (!m.Has) return 0;
            float tw = TitleWidth(m.Title);
            return tw <= TitleW ? 0 : tw - TitleW + 8;
        }

        // кусок бегущей строки from…to; startHold — в начале пауза 1,5 с, endHold — в конце пауза 1,2 с; ms — длительность
        // кадры рисуются и пишутся по одному (GifWriter), в памяти их не копим
        public static byte[] MarqueeGif(Media.Info m, float from, float to, bool startHold, bool endHold, bool loop, out int ms)
        {
            int n = Math.Max(1, (int)Math.Ceiling(to - from));
            Func<int, int> delay = i => i == 0 && startHold ? 150 : i == n && endHold ? 120 : 3;
            ms = 0; for (int i = 0; i <= n; i++) ms += delay(i) * 10;
            return GifWriter.Encode(n + 1, i => MediaFrame(m, Math.Min(to, from + i), null, 1f, 1), delay, new[] { 0, n }, loop, true);
        }

        // смена трека: 14 кадров по 30 мс; dir = -1 — «предыдущий», обложки едут в обратную сторону.
        // Палитра — по первому, среднему и последнему кадру: в них есть обе обложки целиком
        public static byte[] TransitionGif(Media.Info m, Media.Info old, int dir, out int ms)
        {
            const int TN = 14;
            ms = (TN - 1) * 30;
            int d = dir < 0 ? -1 : 1;
            return GifWriter.Encode(TN, i => MediaFrame(m, 0f, old, (i + 1) / (float)TN, d), i => i == TN - 1 ? 300 : 3, new[] { 0, TN / 2, TN - 1 }, false, true);
        }

        // Анимация кнопки: смена трека (old != null) — короткий переход; длинное название — бегущая строка по кругу.
        // Возвращает null, если анимация не нужна (тогда — обычная картинка). loopMs — длительность одного круга.
        // бегущая строка: 1 пиксель за кадр, кадр 30 мс (~33 кадра/с) — на деке выбран как самый плавный (варианты 16 и 25 к/с дёргались)
        public static byte[] MediaKeyGif(Media.Info m, Media.Info old, out int loopMs, int dir = 1) { return MediaKeyGif(m, old, out loopMs, dir, 1f, 3, true); }

        // step — пикселей за кадр, d — задержка кадра (1/100 с), delta — писать в кадр только изменения
        public static byte[] MediaKeyGif(Media.Info m, Media.Info old, out int loopMs, int dir, float step, int d, bool delta)
        {
            loopMs = 0;
            var frames = new List<Bitmap>(); var delays = new List<int>();
            try
            {
                if (old != null)
                {
                    // смена трека: 14 кадров по 0,03 с (~33 кадра/с, как у бегущей строки), последний держится,
                    // пока не придёт обычный вид
                    const int TN = 14;
                    for (int i = 1; i <= TN; i++) { frames.Add(MediaFrame(m, 0f, old, i / (float)TN, dir < 0 ? -1 : 1)); delays.Add(i == TN ? 300 : 3); }
                    loopMs = (TN - 1) * 30;
                    return GifWriter.Encode(frames, delays.ToArray(), false);
                }
                float tw = TitleWidth(m.Title);
                if (!m.Has || tw <= TitleW) return null;
                // Бегущая строка: в начале пауза 1,5 с, строка едет, пока не покажется конец названия, пауза 1,2 с —
                // и снова с начала. Кадров не больше MaxFrames: Ulanzi не показывает слишком длинные GIF (377 кадров —
                // «?» вместо картинки); у очень длинных названий шаг больше, скорость та же.
                const int MaxFrames = 240;
                float run = tw - TitleW + 8;                                 // сколько проехать, чтобы показался конец
                float st = Math.Max(step, (float)Math.Ceiling(run / (MaxFrames - 2)));
                int dd = (int)Math.Max(d, Math.Round(d * st / step));        // шаг больше — кадр дольше: скорость прежняя
                int n = (int)Math.Ceiling(run / st);
                frames.Add(MediaFrame(m, 0f, null, 1f, 1)); delays.Add(150);
                for (int i = 1; i <= n; i++) { frames.Add(MediaFrame(m, Math.Min(run, i * st), null, 1f, 1)); delays.Add(i == n ? 120 : dd); }
                loopMs = 1500 + (n - 1) * dd * 10 + 1200;
                return GifWriter.Encode(frames, delays.ToArray(), true, delta);
            }
            finally { foreach (var b in frames) b.Dispose(); }
        }

        // экран крутилки: обложка в кольце позиции; название и исполнителя Ulanzi пишет подписью
        public static Bitmap MediaDial(Media.Info m)
        {
            const int S = 200;
            var bmp = new Bitmap(S, S, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                Setup(g);
                g.Clear(Color.Transparent);
                var ring = new RectangleF(12, 12, S - 24, S - 24);
                using (var pen = new Pen(Track, 12f) { StartCap = LineCap.Round, EndCap = LineCap.Round }) g.DrawArc(pen, ring, 135, 270);
                if (m.Has && m.Duration > 1)
                    using (var pen = new Pen(m.Playing ? White : Dim, 12f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                        g.DrawArc(pen, ring, 135, Math.Max(2f, 270f * (float)Math.Min(1, m.Position / m.Duration)));
                var cr = new RectangleF((S - 112) / 2f, (S - 112) / 2f, 112, 112);
                var c = m.Has ? Cover(m.Cover) : null;
                if (c != null) DrawCover(g, c, cr, 18, m.Playing ? 1f : .5f);
                else Note(g, RectangleF.Inflate(cr, -16, -16), m.Has && m.Playing ? White : Dim);
                if (m.Has && !m.Playing) PauseMark(g, cr);
                if (m.Liked.HasValue) Heart(g, cr.Right - 8, cr.Y + 8, 40, m.Liked.Value);
            }
            return bmp;
        }

        public static string ToBase64(Bitmap b)
        {
            using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return Convert.ToBase64String(ms.ToArray()); }
        }

        // ── рисование ──
        static void Setup(Graphics g)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }

        // значки рисованы на поле 196×196 с отступами — вырезаем середину, чтобы значок не мельчал
        static void DrawGlyph(Graphics g, Bitmap glyph, Rectangle r, float alpha)
        {
            float k = glyph.Width / 196f;
            var src = new RectangleF(30 * k, 30 * k, 136 * k, 136 * k);
            using (var ia = new ImageAttributes())
            {
                var cm = new ColorMatrix { Matrix33 = alpha };
                ia.SetColorMatrix(cm);
                g.DrawImage(glyph, new[] { new PointF(r.X, r.Y), new PointF(r.Right, r.Y), new PointF(r.X, r.Bottom) }, src, GraphicsUnit.Pixel, ia);
            }
        }

        // перечёркнут — без звука
        static void Slash(Graphics g, Rectangle r)
        {
            using (var pen = new Pen(Color.Black, 16f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, r.X + 8, r.Bottom - 8, r.Right - 8, r.Y + 8);
            using (var pen = new Pen(Red, 8f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawLine(pen, r.X + 8, r.Bottom - 8, r.Right - 8, r.Y + 8);
        }

        // динамик контуром, как остальные значки набора
        static void Speaker(Graphics g, RectangleF r, Color c, bool muted)
        {
            float w = r.Width, x = r.X, y = r.Y, t = w * .11f;
            using (var pen = new Pen(c, t) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                var body = new[]
                {
                    new PointF(x + w * .08f, y + w * .38f), new PointF(x + w * .28f, y + w * .38f), new PointF(x + w * .52f, y + w * .16f),
                    new PointF(x + w * .52f, y + w * .84f), new PointF(x + w * .28f, y + w * .62f), new PointF(x + w * .08f, y + w * .62f)
                };
                g.DrawPolygon(pen, body);
                if (muted)
                {
                    g.DrawLine(pen, x + w * .70f, y + w * .38f, x + w * .94f, y + w * .62f);
                    g.DrawLine(pen, x + w * .94f, y + w * .38f, x + w * .70f, y + w * .62f);
                }
                else
                {
                    g.DrawArc(pen, x + w * .48f, y + w * .34f, w * .26f, w * .32f, -55, 110);
                    g.DrawArc(pen, x + w * .46f, y + w * .18f, w * .48f, w * .64f, -55, 110);
                }
            }
        }

        // метка «Активное окно»: белый кружок со стрелкой-курсором
        static void Badge(Graphics g, RectangleF r)
        {
            using (var br = new SolidBrush(Color.Black)) g.FillEllipse(br, RectangleF.Inflate(r, 4, 4));
            using (var br = new SolidBrush(White)) g.FillEllipse(br, r);
            float x = r.X, y = r.Y, w = r.Width;
            var pts = new[] { new PointF(x + w * .32f, y + w * .24f), new PointF(x + w * .74f, y + w * .52f), new PointF(x + w * .54f, y + w * .57f), new PointF(x + w * .45f, y + w * .77f) };
            using (var br = new SolidBrush(Color.Black)) g.FillPolygon(br, pts);
        }

        static void Text(Graphics g, string s, Font f, Color c, RectangleF r)
        {
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap })
            using (var br = new SolidBrush(c))
                g.DrawString(s ?? "", f, br, r, sf);
        }

        static GraphicsPath Rounded(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
