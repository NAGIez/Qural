using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

// Логотип Qural (Құрал — «инструмент»): три ползунка-регулятора с круглыми ручками (серый, синий, серый).
// Ползунки — от звука (MixKeys), круглые цветные ручки — от цвета (ColorSwitch).
public static class IconGen
{
    // серый — синий — серый: --gray и --blue из GlassWise
    static readonly Color[] Colors = { Color.FromArgb(142, 142, 147), Color.FromArgb(0, 122, 255), Color.FromArgb(142, 142, 147) };
    static readonly float[] Levels = { 0.62f, 0.30f, 0.50f }; // где ручка: доля высоты сверху

    public static Bitmap Render(int size)
    {
        int big = Math.Max(size * 4, 64);
        using (var hi = new Bitmap(big, big, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(hi))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float w = big * 0.13f, top = big * 0.06f, h = big * 0.88f, knob = big * 0.30f;
                float[] cx = { big * 0.19f, big * 0.50f, big * 0.81f };
                for (int i = 0; i < 3; i++)
                {
                    using (var track = new SolidBrush(Color.FromArgb(90, Colors[i])))
                        FillPill(g, track, cx[i] - w / 2, top, w, h);
                    float ky = top + h * Levels[i];
                    using (var fill = new SolidBrush(Colors[i]))
                    {
                        FillPill(g, fill, cx[i] - w / 2, ky, w, top + h - ky);
                        g.FillEllipse(fill, cx[i] - knob / 2, ky - knob / 2, knob, knob);
                    }
                    if (size >= 32)
                        using (var dot = new SolidBrush(Color.FromArgb(235, 255, 255, 255)))
                            g.FillEllipse(dot, cx[i] - knob * 0.17f, ky - knob * 0.17f, knob * 0.34f, knob * 0.34f);
                }
            }
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(hi, new Rectangle(0, 0, size, size));
            }
            return bmp;
        }
    }

    static void FillPill(Graphics g, Brush b, float x, float y, float w, float h)
    {
        if (h <= 0) return;
        using (var p = new GraphicsPath())
        {
            float d = Math.Min(w, h);
            p.AddArc(x, y, d, d, 180, 180);
            p.AddArc(x, y + h - d, d, d, 0, 180);
            p.CloseFigure();
            g.FillPath(b, p);
        }
    }

    public static void WriteIco(string path, string pngPreview)
    {
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 256 };
        var frames = new List<byte[]>();
        foreach (int s in sizes)
            using (var bmp = Render(s))
            {
                if (s == 256)
                {
                    using (var ms = new MemoryStream()) { bmp.Save(ms, ImageFormat.Png); frames.Add(ms.ToArray()); }
                    if (pngPreview != null) bmp.Save(pngPreview, ImageFormat.Png);
                }
                else frames.Add(Dib(bmp));
            }
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                byte d = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
                w.Write(d); w.Write(d); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32);
                w.Write((uint)frames[i].Length); w.Write((uint)offset);
                offset += frames[i].Length;
            }
            foreach (var f in frames) w.Write(f);
        }
    }

    static byte[] Dib(Bitmap bmp)
    {
        int s = bmp.Width, maskStride = ((s + 31) / 32) * 4;
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms))
        {
            w.Write(40); w.Write(s); w.Write(s * 2); w.Write((ushort)1); w.Write((ushort)32);
            w.Write(0); w.Write(s * s * 4 + maskStride * s); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
            for (int y = s - 1; y >= 0; y--)
                for (int x = 0; x < s; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                }
            w.Write(new byte[maskStride * s]);
            return ms.ToArray();
        }
    }
}
