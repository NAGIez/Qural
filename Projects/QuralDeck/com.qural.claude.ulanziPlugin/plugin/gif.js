// Простой кодировщик GIF89a (как в tools\ClaudeTiles.cs): общая палитра до 256 цветов, бесконечный повтор.
// frames — RGBA-буферы одного размера, delays — сотые доли секунды.
export function encodeGif(w, h, frames, delays) {
  const index = new Map(), palette = [];
  let quantize = false;
  const key = (b, i) => (b[i] << 16) | (b[i + 1] << 8) | b[i + 2];
  outer: for (const f of frames)
    for (let i = 0; i < f.length; i += 4) {
      const c = key(f, i);
      if (!index.has(c)) { if (palette.length >= 256) { quantize = true; break outer; } index.set(c, palette.length); palette.push(c); }
    }
  if (quantize) {
    palette.length = 0; index.clear();
    for (let r = 0; r < 6; r++) for (let g = 0; g < 6; g++) for (let b = 0; b < 6; b++) palette.push((r * 51 << 16) | (g * 51 << 8) | b * 51);
  }
  while (palette.length < 256) palette.push(0);

  const out = [];
  const u16 = v => out.push(v & 255, (v >> 8) & 255);
  out.push(...Buffer.from('GIF89a')); u16(w); u16(h); out.push(0xF7, 0, 0);
  for (const c of palette) out.push((c >> 16) & 255, (c >> 8) & 255, c & 255);
  out.push(0x21, 0xFF, 0x0B, ...Buffer.from('NETSCAPE2.0'), 3, 1, 0, 0, 0);
  frames.forEach((f, n) => {
    const idx = new Uint8Array(w * h);
    for (let i = 0, p = 0; i < f.length; i += 4, p++) {
      if (quantize) idx[p] = Math.round(f[i] / 51) * 36 + Math.round(f[i + 1] / 51) * 6 + Math.round(f[i + 2] / 51);
      else idx[p] = index.get(key(f, i));
    }
    out.push(0x21, 0xF9, 4, 0x04); u16(Math.max(2, delays[n] || 10)); out.push(0, 0);
    out.push(0x2C); u16(0); u16(0); u16(w); u16(h); out.push(0);
    out.push(8);
    const lzw = encodeLzw(idx);
    for (let p = 0; p < lzw.length; p += 255) { const k = Math.min(255, lzw.length - p); out.push(k); for (let i = 0; i < k; i++) out.push(lzw[p + i]); }
    out.push(0);
  });
  out.push(0x3B);
  return Buffer.from(out);
}

function encodeLzw(px) {
  const min = 8, clear = 1 << min, eoi = clear + 1;
  const out = [];
  let bitBuf = 0, bitCount = 0, size = min + 1, next = eoi + 1;
  let dict = new Map();
  const emit = code => {
    bitBuf |= code << bitCount; bitCount += size;
    while (bitCount >= 8) { out.push(bitBuf & 255); bitBuf >>>= 8; bitCount -= 8; }
  };
  emit(clear);
  let prefix = px[0];
  for (let i = 1; i < px.length; i++) {
    const k = px[i], key = (prefix << 8) | k, code = dict.get(key);
    if (code !== undefined) { prefix = code; continue; }
    emit(prefix);
    if (next < 4096) { dict.set(key, next++); if (next > (1 << size) && size < 12) size++; }
    else { emit(clear); dict = new Map(); next = eoi + 1; size = min + 1; }
    prefix = k;
  }
  emit(prefix); emit(eoi);
  if (bitCount > 0) out.push(bitBuf & 255);
  return out;
}
