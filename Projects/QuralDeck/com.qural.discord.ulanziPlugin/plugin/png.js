// Маленький разбор PNG → RGBA (для аватарок Discord). Поддерживает 8 бит на канал, без чересстрочности:
// серый, серый+альфа, RGB, RGBA, палитра (с прозрачностью tRNS). Остальное — null (тогда рисуем инициалы).
import zlib from 'node:zlib';

export function decodePng(buf) {
  try {
    if (buf.readUInt32BE(0) !== 0x89504e47) return null;
    let o = 8, w = 0, h = 0, depth = 0, type = 0, interlace = 0, pal = null, trns = null;
    const idat = [];
    while (o + 8 <= buf.length) {
      const len = buf.readUInt32BE(o), kind = buf.toString('ascii', o + 4, o + 8), d = buf.subarray(o + 8, o + 8 + len);
      if (kind === 'IHDR') { w = d.readUInt32BE(0); h = d.readUInt32BE(4); depth = d[8]; type = d[9]; interlace = d[12]; }
      else if (kind === 'PLTE') pal = d;
      else if (kind === 'tRNS') trns = d;
      else if (kind === 'IDAT') idat.push(d);
      else if (kind === 'IEND') break;
      o += 12 + len;
    }
    if (depth !== 8 || interlace || !w || !h || w > 2048 || h > 2048) return null;
    const ch = { 0: 1, 2: 3, 3: 1, 4: 2, 6: 4 }[type];
    if (!ch || (type === 3 && !pal)) return null;
    const raw = zlib.inflateSync(Buffer.concat(idat));
    const stride = w * ch, cur = Buffer.alloc(stride), prev = Buffer.alloc(stride), px = Buffer.alloc(w * h * 4);
    let p = 0;
    for (let y = 0; y < h; y++) {
      const f = raw[p++];
      for (let x = 0; x < stride; x++) {
        const a = x >= ch ? cur[x - ch] : 0, b = prev[x], c = x >= ch ? prev[x - ch] : 0, v = raw[p++];
        let r;
        switch (f) {
          case 1: r = v + a; break;
          case 2: r = v + b; break;
          case 3: r = v + ((a + b) >> 1); break;
          case 4: { const pa = Math.abs(b - c), pb = Math.abs(a - c), pc = Math.abs(a + b - 2 * c); r = v + (pa <= pb && pa <= pc ? a : pb <= pc ? b : c); break; }
          default: r = v;
        }
        cur[x] = r & 255;
      }
      for (let x = 0; x < w; x++) {
        const q = (y * w + x) * 4, s = x * ch;
        if (type === 6) { px[q] = cur[s]; px[q + 1] = cur[s + 1]; px[q + 2] = cur[s + 2]; px[q + 3] = cur[s + 3]; }
        else if (type === 2) { px[q] = cur[s]; px[q + 1] = cur[s + 1]; px[q + 2] = cur[s + 2]; px[q + 3] = 255; }
        else if (type === 0) { px[q] = px[q + 1] = px[q + 2] = cur[s]; px[q + 3] = 255; }
        else if (type === 4) { px[q] = px[q + 1] = px[q + 2] = cur[s]; px[q + 3] = cur[s + 1]; }
        else { const i = cur[s]; px[q] = pal[i * 3]; px[q + 1] = pal[i * 3 + 1]; px[q + 2] = pal[i * 3 + 2]; px[q + 3] = trns && i < trns.length ? trns[i] : 255; }
      }
      cur.copy(prev);
    }
    return { w, h, px };
  } catch { return null; }
}
