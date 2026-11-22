// Часы пиксельным шрифтом для обычной кнопки: время крупно, двоеточие мигает (GIF из двух кадров),
// ниже день недели и дата. Картинка меняется раз в минуту.
import { Canvas } from './pixpng.js';
import { encodeGif } from './gif.js';

const W = [255, 255, 255], DIM = [150, 150, 156], NOTE = [190, 190, 196];
const DAYS = ['ВС', 'ПН', 'ВТ', 'СР', 'ЧТ', 'ПТ', 'СБ'];
const p2 = n => String(n).padStart(2, '0');

export function clockStamp(d = new Date()) {
  return p2(d.getHours()) + ':' + p2(d.getMinutes()) + ' ' + d.toDateString();
}

export function clockGif(size = 196, d = new Date()) {
  const hh = p2(d.getHours()), mm = p2(d.getMinutes());
  const frame = colon => {
    const c = new Canvas(size, size), s = 6, mid = size / 2;
    // ЧЧ и ММ по бокам от двоеточия; двоеточие — два квадрата, во втором кадре гаснет
    const digitW = 6 * s - s, gap = 3 * s, y = 50;
    c.text(hh, mid - gap / 2 - (2 * digitW + s), y, s, W);
    c.text(mm, mid + gap / 2 + s, y, s, W);
    if (colon) { c.rect(mid - s / 2, y + 2 * s - 1, s, s, W); c.rect(mid - s / 2, y + 5 * s - 1, s, s, W); }
    c.textCenter(DAYS[d.getDay()] + ' ' + p2(d.getDate()) + '.' + p2(d.getMonth() + 1), mid, 128, 3, NOTE);
    // полоска: сколько прошло от суток
    const passed = (d.getHours() * 60 + d.getMinutes()) / 1440 * 100;
    c.bar(30, 164, size - 60, 6, 24, passed, DIM, [40, 40, 42]);
    return c;
  };
  const on = frame(true), off = frame(false);
  return { png: on.png(), gif: encodeGif(size, size, [on.px, off.px], [100, 100]) };
}
