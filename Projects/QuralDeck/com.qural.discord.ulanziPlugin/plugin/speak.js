// Картинка кнопки «Кто говорит»: аватар того, кто говорит сейчас, с зелёным кольцом (как в Discord) и имя;
// если все молчат — аватары участников канала и его название; не в голосовом канале — «НЕ В КАНАЛЕ».
// Аватары берём с cdn.discordapp.com (PNG 128 px), кешируем в памяти.
import { Canvas } from './pixpng.js';
import { decodePng } from './png.js';

const W = [255, 255, 255], DIM = [150, 150, 156], GREEN = [35, 165, 90], BG = [0, 0, 0];
const HOLD = 700;   // мс — сколько ещё держать говорившего после паузы (без мигания между словами)

export class Speak {
  constructor(onAvatar, log) { this.cache = new Map(); this.onAvatar = onAvatar; this.log = log; }

  static url(m) {
    if (m.avatar) return 'https://cdn.discordapp.com/avatars/' + m.id + '/' + m.avatar + '.png?size=128';
    let i = 0; try { i = Number((BigInt(m.id) >> 22n) % 6n); } catch {}
    return 'https://cdn.discordapp.com/embed/avatars/' + i + '.png';
  }

  avatar(m) {
    const key = m.id + ':' + m.avatar;
    if (this.cache.has(key)) { const v = this.cache.get(key); return v === 'loading' ? null : v; }
    this.cache.set(key, 'loading');
    fetch(Speak.url(m)).then(r => r.ok ? r.arrayBuffer() : null).then(b => {
      const img = b ? decodePng(Buffer.from(b)) : null;
      if (!img) this.log('discord: аватар не разобрался — ' + m.name);
      this.cache.set(key, img);
      this.onAvatar();
    }).catch(e => { this.cache.set(key, null); this.log('discord: аватар — ' + e.message); });
    return null;
  }

  // кто «говорит» сейчас: говорит или замолчал меньше HOLD назад; первым — кто заговорил последним
  static talkers(ch, now) {
    if (!ch) return [];
    return [...ch.members.values()].filter(m => m.speaking || now - m.at < HOLD).sort((a, b) => b.at - a.at);
  }

  // → { png, title, sig, until }: until — когда перерисовать (кончится «удержание» говорившего)
  render(size, ch) {
    const now = Date.now(), talk = Speak.talkers(ch, now), mid = size / 2, dial = size === 200;
    let until = 0;
    for (const m of talk) if (!m.speaking) until = Math.max(until, m.at + HOLD);
    const c = new Canvas(size, size, dial ? [0, 0, 0, 0] : [0, 0, 0, 255]);
    let title, sig;
    if (!ch) {
      title = 'не в канале'; sig = 'none';
      if (!dial) c.textCenter('НЕ В КАНАЛЕ', mid, mid - 7, 2, DIM);
      else this.headset(c, mid, mid, DIM);
    } else if (talk.length) {
      const m = talk[0], img = this.avatar(m);
      sig = 'talk|' + talk.map(t => t.id).join(',') + '|' + (img ? 1 : 0);
      title = m.name + (talk.length > 1 ? ' +' + (talk.length - 1) : '');
      const r = dial ? 66 : 46, cy = dial ? mid : 66;
      this.circle(c, img, m, mid, cy, r, GREEN);
      if (!dial) c.textCenter(Speak.fit(m.name, size - 24, 2), mid, 136, 2, W);
      if (talk.length > 1) c.textRight('+' + (talk.length - 1), size - 14, 14, 2, GREEN);
    } else {
      const list = [...ch.members.values()];
      const show = list.slice(0, 4), imgs = show.map(m => this.avatar(m));
      sig = 'quiet|' + ch.id + '|' + ch.name + '|' + list.map(m => m.id).join(',') + '|' + imgs.map(i => i ? 1 : 0).join('');
      title = ch.name || 'голосовой канал';
      const r = show.length <= 2 ? 30 : 20, gap = r * 2 + 8, cy = dial ? mid : 70;
      const x0 = mid - (show.length - 1) * gap / 2;
      show.forEach((m, i) => this.circle(c, imgs[i], m, x0 + i * gap, cy, r, null));
      if (!show.length) this.headset(c, mid, cy, DIM);
      if (!dial) {
        c.textCenter(Speak.fit(ch.name || 'КАНАЛ', size - 24, 2), mid, 122, 2, W);
        c.textCenter(list.length + ' В КАНАЛЕ', mid, 146, 2, DIM);
      }
    }
    return { png: c.png(), title, sig, until };
  }

  // в пиксельном шрифте только буквы, цифры и немного знаков: эмодзи и прочее («🔊 Общий», «user_») убираем
  static clean(text) {
    return String(text || '').replace(/[^A-Za-zА-Яа-яЁё0-9%:.\-?!/+… ]+/g, ' ').replace(/\s+/g, ' ').trim();
  }

  static fit(text, width, s) {
    let t = Speak.clean(text).toUpperCase() || '?';
    if (Canvas.textWidth(t, s) <= width) return t;
    while (t.length > 1 && Canvas.textWidth(t + '…', s) > width) t = t.slice(0, -1);
    return t + '…';
  }

  // аватар кружком с мягким краем; нет картинки — цветной круг с первой буквой; ring — кольцо вокруг
  circle(c, img, m, cx, cy, r, ring) {
    const rw = 5, gap = 3, R = ring ? r + gap + rw : r;
    let h = 0; for (const ch of m.name) h = (h * 31 + ch.charCodeAt(0)) >>> 0;
    const fill = [[88, 101, 242], [237, 66, 69], [87, 242, 135], [254, 231, 92], [235, 69, 158]][h % 5];
    for (let y = Math.floor(cy - R - 1); y <= Math.ceil(cy + R + 1); y++)
      for (let x = Math.floor(cx - R - 1); x <= Math.ceil(cx + R + 1); x++) {
        if (x < 0 || y < 0 || x >= c.w || y >= c.h) continue;
        const d = Math.hypot(x + 0.5 - cx, y + 0.5 - cy);
        let col = null, a = 0;
        if (d <= r + 0.5) {
          a = Math.min(1, r + 0.5 - d);
          if (img) {
            const u = (x + 0.5 - (cx - r)) / (2 * r) * img.w - 0.5, v = (y + 0.5 - (cy - r)) / (2 * r) * img.h - 0.5;
            col = Speak.sample(img, u, v);
            a *= col[3] / 255;
          } else col = fill;
        } else if (ring && d >= r + gap - 0.5 && d <= R + 0.5) {
          col = ring; a = Math.min(1, d - (r + gap - 0.5), R + 0.5 - d);
        }
        if (!col || a <= 0) continue;
        const o = (y * c.w + x) * 4;
        const ba = c.px[o + 3] / 255;
        for (let k = 0; k < 3; k++) c.px[o + k] = Math.round(col[k] * a + c.px[o + k] * (1 - a));
        c.px[o + 3] = Math.round(255 * Math.min(1, a + ba * (1 - a)));
      }
    if (!img) {
      const s = r >= 40 ? 6 : r >= 26 ? 4 : 3, light = fill[0] * 0.3 + fill[1] * 0.59 + fill[2] * 0.11 > 160;
      c.textCenter((Speak.clean(m.name).slice(0, 1) || '?').toUpperCase(), cx, Math.round(cy - 3.5 * s), s, light ? [20, 20, 24] : W);
    }
  }

  static sample(img, u, v) {
    const x0 = Math.max(0, Math.min(img.w - 1, Math.floor(u))), y0 = Math.max(0, Math.min(img.h - 1, Math.floor(v)));
    const x1 = Math.min(img.w - 1, x0 + 1), y1 = Math.min(img.h - 1, y0 + 1), fx = Math.max(0, Math.min(1, u - x0)), fy = Math.max(0, Math.min(1, v - y0));
    const out = [0, 0, 0, 0];
    for (let k = 0; k < 4; k++) {
      const p = (x, y) => img.px[(y * img.w + x) * 4 + k];
      out[k] = (p(x0, y0) * (1 - fx) + p(x1, y0) * fx) * (1 - fy) + (p(x0, y1) * (1 - fx) + p(x1, y1) * fx) * fy;
    }
    return out;
  }

  // простые наушники — «не в канале» на крутилке
  headset(c, cx, cy, col) {
    for (let a = 180; a <= 360; a += 2) {
      const t = a * Math.PI / 180;
      c.rect(cx + Math.cos(t) * 34 - 3, cy + Math.sin(t) * 34 - 3, 6, 6, col);
    }
    c.rect(cx - 40, cy - 4, 12, 26, col); c.rect(cx + 28, cy - 4, 12, 26, col);
  }
}
