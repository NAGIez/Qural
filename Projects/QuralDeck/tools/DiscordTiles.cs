// Картинки кнопки «Qural — Discord» (196×196): обычный, микрофон заглушен, звук выключен, нет связи, нужно разрешение.
// Стиль как у остальных кнопок Qural: чёрный фон, белая плашка — «включено», серый/красный знак — «выключено».
// Сборка: tools\make-discord-icons.ps1
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

static class DiscordTiles
{
    const int S = 196;
    static readonly Color Black = Color.Black;
    static readonly Color White = Color.White;
    static readonly Color Grey = Color.FromArgb(110, 110, 116);
    static readonly Color Red = Color.FromArgb(242, 63, 67);
    static readonly Color Amber = Color.FromArgb(255, 184, 0);

    static void Main(string[] args)
    {
        string dir = args.Length > 0 ? args[0] : ".";
        Directory.CreateDirectory(dir);

        // обычный: чёрный фон, белые микрофон и наушники — «микрофон активен»
        Save(dir, "discord_normal", g => { Headset(g, White, 100); Mic(g, White, 1f); });
        // микрофон заглушен: серый микрофон, перечёркнут красным; наушники белые — «слышу»
        Save(dir, "discord_mute", g => { Headset(g, White, 100); Mic(g, Red, 1f); MicSlash(g, Red); });
        // звук выключен: красные наушники перечёркнуты, микрофон тоже серый
        Save(dir, "discord_deaf", g => { Headset(g, Red, 100); Mic(g, Red, 1f); Slash(g, Red); });
        // нет связи с Discord (не запущен)
        Save(dir, "discord_offline", g => { Headset(g, Grey, 100); Mic(g, Grey, 1f); });
        // нужно настроить / разрешить: серые наушники и жёлтый знак «!»
        Save(dir, "discord_auth", g => { Headset(g, Grey, 100); Mic(g, Grey, 1f); Badge(g, Amber); });
        // значок плагина
        Save(dir, "action", g => { Headset(g, White, 100); Mic(g, White, 1f); });
    }

    static void Save(string dir, string name, Action<Graphics> draw)
    {
        using (var b = new Bitmap(S, S, PixelFormat.Format32bppArgb))
        using (var g = Graphics.FromImage(b))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.Clear(Black);
            draw(g);
            b.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png);
        }
    }

    static GraphicsPath Round(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath(); float d = r * 2;
        p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
        p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    // белая плашка со скруглением, как у «Разрешить»
    static void Plate(Graphics g, Color c)
    {
        using (var p = Round(6, 6, S - 12, S - 12, 28)) using (var br = new SolidBrush(c)) g.FillPath(br, p);
    }

    static Pen P(Color c, float w) { return new Pen(c, w) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round }; }

    // наушники: дуга-оголовье и две чашки по бокам
    static void Headset(Graphics g, Color c, int cy)
    {
        using (var pen = P(c, 11)) g.DrawArc(pen, 42, 38, 112, 112, 180, 180);
        using (var br = new SolidBrush(c))
        {
            using (var l = Round(34, 92, 26, 48, 10)) g.FillPath(br, l);
            using (var r = Round(136, 92, 26, 48, 10)) g.FillPath(br, r);
        }
    }

    // микрофон по центру между чашками: капсула, дужка-подставка, ножка
    static void Mic(Graphics g, Color c, float k)
    {
        using (var br = new SolidBrush(c))
        using (var cap = Round(84, 70, 28, 52, 14)) g.FillPath(br, cap);
        using (var pen = P(c, 8))
        {
            g.DrawArc(pen, 72, 88, 52, 50, 0, 180);
            g.DrawLine(pen, 98, 138, 98, 154);
            g.DrawLine(pen, 84, 156, 112, 156);
        }
    }

    // красная черта по диагонали с чёрной обводкой, чтобы читалась поверх знака
    static void Slash(Graphics g, Color c)
    {
        using (var back = P(Black, 22)) g.DrawLine(back, 44, 44, 152, 160);
        using (var pen = P(c, 11)) g.DrawLine(pen, 44, 44, 152, 160);
    }

    // короткая черта только через микрофон (наушники целы — «слышу»)
    static void MicSlash(Graphics g, Color c)
    {
        using (var back = P(Black, 20)) g.DrawLine(back, 68, 70, 128, 150);
        using (var pen = P(c, 10)) g.DrawLine(pen, 68, 70, 128, 150);
    }

    // жёлтый кружок с «!» в правом верхнем углу
    static void Badge(Graphics g, Color c)
    {
        using (var br = new SolidBrush(c)) g.FillEllipse(br, 128, 14, 52, 52);
        using (var pen = P(Black, 8)) { g.DrawLine(pen, 154, 26, 154, 42); }
        using (var br = new SolidBrush(Black)) g.FillEllipse(br, 149.5f, 49, 9, 9);
    }
}
