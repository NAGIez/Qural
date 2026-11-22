using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace Qural
{
    // Анимированный GIF для кнопок деки (бегущая строка, смена обложки) — без библиотек.
    // Палитра общая на все кадры: цветов ≤ 255 — как есть, больше (обложка) — «медианным разрезом» до 255.
    // Кадры после первого — только изменившийся прямоугольник, неизменные точки прозрачные: файл маленький.
    static class GifWriter
    {
        // Экономно по памяти: кадры рисуются и пишутся по одному (frame(i) — новый Bitmap, после записи освобождается),
        // в памяти — только текущий и предыдущий кадр. Палитра — по кадрам paletteFrom (обычно первый и последний:
        // в них все цвета обложек и подписей), остальные цвета — ближайшие из неё.
        public static byte[] Encode(int n, Func<int, Bitmap> frame, Func<int, int> delay, int[] paletteFrom, bool loop = true, bool delta = true)
        {
            int w = 0, h = 0;
            var count = new Dictionary<int, int>();
            int[] buf = null;
            foreach (int pi in paletteFrom)
                using (var b = frame(pi))
                {
                    w = b.Width; h = b.Height;
                    if (buf == null) buf = new int[w * h];
                    Pixels(b, buf);
                    foreach (int c in buf) { int k; count.TryGetValue(c, out k); count[c] = k + 1; }
                }
            var palette = count.Count <= 255 ? new List<int>(count.Keys) : MedianCut(count, 255);
            var map = new Dictionary<int, byte>();
            for (int i = 0; i < palette.Count; i++) map[palette[i]] = (byte)i;
            int tr = palette.Count;
            var pal = palette.ToArray();
            while (palette.Count < 256) palette.Add(0);

            byte[] cur = new byte[w * h], prev = new byte[w * h], sub = new byte[w * h];
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                Header(bw, w, h, palette, loop);
                for (int f = 0; f < n; f++)
                {
                    using (var b = frame(f)) Pixels(b, buf);
                    for (int i = 0; i < buf.Length; i++) cur[i] = Index(buf[i], map, pal);
                    WriteFrame(bw, cur, f > 0 && delta ? prev : null, w, h, tr, delay(f), sub);
                    var t = prev; prev = cur; cur = t;
                }
                bw.Write((byte)0x3B);
                bw.Flush();
                var res = ms.ToArray();
                Trim();
                return res;
            }
        }

        // Куча разрослась (большие массивы кадров лежат в куче больших объектов, сама она не сжимается) —
        // не чаще раза в минуту сжать и вернуть память системе
        static DateTime lastTrim;
        static void Trim()
        {
            if ((DateTime.UtcNow - lastTrim).TotalSeconds < 60 || GC.GetTotalMemory(false) < 40L * 1024 * 1024) return;
            lastTrim = DateTime.UtcNow;
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        }

        static byte Index(int c, Dictionary<int, byte> map, int[] pal)
        {
            byte b;
            if (map.TryGetValue(c, out b)) return b;
            int best = 0, bd = int.MaxValue, r = c >> 16 & 255, g = c >> 8 & 255, bl = c & 255;
            for (int i = 0; i < pal.Length; i++)
            {
                int q = pal[i], dr = r - (q >> 16 & 255), dg = g - (q >> 8 & 255), db = bl - (q & 255);
                int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                if (d < bd) { bd = d; best = i; }
            }
            if (map.Count < 65536) map[c] = (byte)best;
            return (byte)best;
        }

        static void Header(BinaryWriter bw, int w, int h, List<int> palette, bool loop)
        {
            bw.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
            bw.Write((ushort)w); bw.Write((ushort)h);
            bw.Write((byte)0xF7); bw.Write((byte)0); bw.Write((byte)0);
            foreach (var c in palette) { bw.Write((byte)(c >> 16 & 255)); bw.Write((byte)(c >> 8 & 255)); bw.Write((byte)(c & 255)); }
            if (loop)
            {
                bw.Write(new byte[] { 0x21, 0xFF, 0x0B });
                bw.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                bw.Write(new byte[] { 3, 1, 0, 0, 0 });
            }
        }

        // кадр: prev != null — только изменившийся прямоугольник, неизменные точки прозрачные (индекс tr)
        static void WriteFrame(BinaryWriter bw, byte[] cur, byte[] prev, int w, int h, int tr, int delay, byte[] sub)
        {
            int x0 = 0, y0 = 0, rw = w, rh = h; byte[] data = cur; int len = w * h;
            bool t = prev != null;
            if (t)
            {
                int ax = w, ay = h, bx = -1, by = -1;
                for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                    if (cur[y * w + x] != prev[y * w + x]) { if (x < ax) ax = x; if (x > bx) bx = x; if (y < ay) ay = y; if (y > by) by = y; }
                if (bx < 0) { ax = ay = bx = by = 0; }
                x0 = ax; y0 = ay; rw = bx - ax + 1; rh = by - ay + 1; len = rw * rh;
                for (int y = 0; y < rh; y++) for (int x = 0; x < rw; x++)
                {
                    int p = (y0 + y) * w + x0 + x;
                    sub[y * rw + x] = cur[p] == prev[p] ? (byte)tr : cur[p];
                }
                data = sub;
            }
            bw.Write(new byte[] { 0x21, 0xF9, 4, (byte)(t ? 0x05 : 0x04) }); bw.Write((ushort)Math.Max(2, delay)); bw.Write((byte)(t ? tr : 0)); bw.Write((byte)0);
            bw.Write((byte)0x2C); bw.Write((ushort)x0); bw.Write((ushort)y0); bw.Write((ushort)rw); bw.Write((ushort)rh); bw.Write((byte)0);
            bw.Write((byte)8);
            var lzw = Lzw(data, len);
            for (int p = 0; p < lzw.Length; p += 255) { int k = Math.Min(255, lzw.Length - p); bw.Write((byte)k); bw.Write(lzw, p, k); }
            bw.Write((byte)0);
        }

        // delays — в сотых долях секунды; loop — повторять бесконечно (иначе один раз)
        public static byte[] Encode(List<Bitmap> frames, int[] delays, bool loop = true, bool delta = true)
        {
            int w = frames[0].Width, h = frames[0].Height;
            var pix = new List<int[]>();
            foreach (var f in frames) pix.Add(Pixels(f));

            // палитра
            var count = new Dictionary<int, int>();
            foreach (var p in pix) foreach (int c in p) { int n; count.TryGetValue(c, out n); count[c] = n + 1; }
            var palette = count.Count <= 255 ? new List<int>(count.Keys) : MedianCut(count, 255);
            var exact = new Dictionary<int, byte>();
            for (int i = 0; i < palette.Count; i++) exact[palette[i]] = (byte)i;
            var near = new Dictionary<int, byte>();
            Func<int, byte> index = c =>
            {
                byte b;
                if (exact.TryGetValue(c, out b) || near.TryGetValue(c, out b)) return b;
                int best = 0, bd = int.MaxValue, r = c >> 16 & 255, g = c >> 8 & 255, bl = c & 255;
                for (int i = 0; i < palette.Count; i++)
                {
                    int q = palette[i], dr = r - (q >> 16 & 255), dg = g - (q >> 8 & 255), db = bl - (q & 255);
                    int d = dr * dr * 3 + dg * dg * 4 + db * db * 2;
                    if (d < bd) { bd = d; best = i; }
                }
                near[c] = (byte)best; return (byte)best;
            };
            int tr = palette.Count; // прозрачный индекс
            while (palette.Count < 256) palette.Add(0);

            var idx = new List<byte[]>();
            foreach (var p in pix) { var a = new byte[p.Length]; for (int i = 0; i < p.Length; i++) a[i] = index(p[i]); idx.Add(a); }

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(new[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
                bw.Write((ushort)w); bw.Write((ushort)h);
                bw.Write((byte)0xF7); bw.Write((byte)0); bw.Write((byte)0);
                foreach (var c in palette) { bw.Write((byte)(c >> 16 & 255)); bw.Write((byte)(c >> 8 & 255)); bw.Write((byte)(c & 255)); }
                if (loop)
                {
                    bw.Write(new byte[] { 0x21, 0xFF, 0x0B });
                    bw.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
                    bw.Write(new byte[] { 3, 1, 0, 0, 0 });
                }
                for (int f = 0; f < idx.Count; f++)
                {
                    byte[] cur = idx[f], data; int x0 = 0, y0 = 0, rw = w, rh = h; bool t = delta && f > 0;
                    if (t)
                    {
                        byte[] prev = idx[f - 1];
                        int ax = w, ay = h, bx = -1, by = -1;
                        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
                            if (cur[y * w + x] != prev[y * w + x]) { if (x < ax) ax = x; if (x > bx) bx = x; if (y < ay) ay = y; if (y > by) by = y; }
                        if (bx < 0) { ax = ay = bx = by = 0; }
                        x0 = ax; y0 = ay; rw = bx - ax + 1; rh = by - ay + 1;
                        data = new byte[rw * rh];
                        for (int y = 0; y < rh; y++) for (int x = 0; x < rw; x++)
                        {
                            int p = (y0 + y) * w + x0 + x;
                            data[y * rw + x] = cur[p] == prev[p] ? (byte)tr : cur[p];
                        }
                    }
                    else data = cur;
                    bw.Write(new byte[] { 0x21, 0xF9, 4, (byte)(t ? 0x05 : 0x04) }); bw.Write((ushort)Math.Max(2, delays[f])); bw.Write((byte)(t ? tr : 0)); bw.Write((byte)0);
                    bw.Write((byte)0x2C); bw.Write((ushort)x0); bw.Write((ushort)y0); bw.Write((ushort)rw); bw.Write((ushort)rh); bw.Write((byte)0);
                    bw.Write((byte)8);
                    var lzw = Lzw(data);
                    for (int p = 0; p < lzw.Length; p += 255) { int n = Math.Min(255, lzw.Length - p); bw.Write((byte)n); bw.Write(lzw, p, n); }
                    bw.Write((byte)0);
                }
                bw.Write((byte)0x3B);
                bw.Flush();
                return ms.ToArray();
            }
        }

        // медианный разрез: делим «коробки» цветов по самой длинной оси, пока их не станет n; цвет коробки — средний
        static List<int> MedianCut(Dictionary<int, int> count, int n)
        {
            var boxes = new List<List<KeyValuePair<int, int>>> { new List<KeyValuePair<int, int>>(count) };
            while (boxes.Count < n)
            {
                int bi = -1, bestRange = 0, axis = 0;
                for (int i = 0; i < boxes.Count; i++)
                {
                    if (boxes[i].Count < 2) continue;
                    for (int ax = 0; ax < 3; ax++)
                    {
                        int lo = 255, hi = 0, sh = 16 - ax * 8;
                        foreach (var kv in boxes[i]) { int v = kv.Key >> sh & 255; if (v < lo) lo = v; if (v > hi) hi = v; }
                        if (hi - lo > bestRange) { bestRange = hi - lo; bi = i; axis = ax; }
                    }
                }
                if (bi < 0) break;
                var box = boxes[bi]; int s = 16 - axis * 8;
                box.Sort((a, b) => (a.Key >> s & 255).CompareTo(b.Key >> s & 255));
                long total = 0; foreach (var kv in box) total += kv.Value;
                long acc = 0; int cut = 1;
                for (int i = 0; i < box.Count - 1; i++) { acc += box[i].Value; if (acc * 2 >= total) { cut = i + 1; break; } }
                boxes[bi] = box.GetRange(0, cut);
                boxes.Add(box.GetRange(cut, box.Count - cut));
            }
            var pal = new List<int>();
            foreach (var box in boxes)
            {
                long r = 0, g = 0, b = 0, t = 0;
                foreach (var kv in box) { r += (kv.Key >> 16 & 255) * (long)kv.Value; g += (kv.Key >> 8 & 255) * (long)kv.Value; b += (kv.Key & 255) * (long)kv.Value; t += kv.Value; }
                if (t > 0) pal.Add((int)(r / t) << 16 | (int)(g / t) << 8 | (int)(b / t));
            }
            return pal;
        }

        // RGB без прозрачности (кнопка непрозрачная: прозрачное — как чёрное)
        static int[] Pixels(Bitmap b) { var res = new int[b.Width * b.Height]; Pixels(b, res); return res; }

        static void Pixels(Bitmap b, int[] res)
        {
            var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < b.Height; y++) Marshal.Copy(d.Scan0 + y * d.Stride, res, y * b.Width, b.Width);
            }
            finally { b.UnlockBits(d); }
            for (int i = 0; i < res.Length; i++)
            {
                int c = res[i], a = c >> 24 & 255;
                if (a == 255) res[i] = c & 0xFFFFFF;
                else res[i] = ((c >> 16 & 255) * a / 255) << 16 | ((c >> 8 & 255) * a / 255) << 8 | (c & 255) * a / 255;
            }
        }

        static byte[] Lzw(byte[] px) { return Lzw(px, px.Length); }

        static byte[] Lzw(byte[] px, int len)
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
            for (int i = 1; i < len; i++)
            {
                int k = px[i], key = (prefix << 8) | k, code;
                if (dict.TryGetValue(key, out code)) { prefix = code; continue; }
                emit(prefix);
                if (next < 4096)
                {
                    dict[key] = next++;
                    if (next > (1 << size) && size < 12) size++;
                }
                else { emit(clear); dict.Clear(); next = eoi + 1; size = min + 1; }
                prefix = k;
            }
            emit(prefix);
            emit(eoi);
            if (bitCount > 0) output.Add((byte)(bitBuf & 255));
            return output.ToArray();
        }
    }
}
