// Лимиты плана Claude: данные приходят от строки состояния Claude Code (hook/statusline.cjs → событие Limits),
// хранятся в limits.json рядом с плагином и рисуются на кнопке и экране крутилки вместе с контекстом сессии.
// rate_limits: five_hour / seven_day { used_percentage, resets_at (секунды) }. Если Claude Code передаёт ещё
// лимиты по модели (любой другой ключ с used_percentage) — они показываются тоже, с названием из ключа.
import fs from 'node:fs';
import path from 'node:path';
import { Canvas } from './pixpng.js';
import { encodeGif } from './gif.js';

const W = [255, 255, 255], DIM = [150, 150, 156], NOTE = [190, 190, 196], OFF = [40, 40, 42], RED = [255, 69, 58], AMBER = [255, 159, 10];

export class Limits {
  constructor(dir) {
    this.file = path.join(dir, 'limits.json');
    this.data = null; // { five, week, extra: [{key,label,pct,resets}], at }
    try { this.data = JSON.parse(fs.readFileSync(this.file, 'utf8')); } catch {}
    // маленький персонаж: кадры по уровням (assets/mini_*.bin, рисует tools\ClaudeTiles.cs)
    this.minis = {};
    for (const who of ['five', 'week', 'fable', 'ctx']) for (const band of ['calm', 'busy', 'full', 'panic']) {
      try {
        const b = fs.readFileSync(path.join(dir, 'assets', 'mini_' + who + '_' + band + '.bin'));
        const w = b.readUInt16LE(4), h = b.readUInt16LE(6), n = b.readUInt16LE(8), frames = [];
        let o = 10;
        for (let i = 0; i < n; i++) { const delay = b.readUInt16LE(o); o += 2; frames.push({ delay, px: b.subarray(o, o + w * h * 4) }); o += w * h * 4; }
        (this.minis[who] = this.minis[who] || {})[band] = { w, h, frames };
      } catch {}
    }
    // бегуны для забега (assets/run_*.bin): кадры 0–1 бег, 2 радуется, 3 запыхался, 4 без сил
    this.runs = {};
    for (const who of ['five', 'week', 'fable', 'ctx']) {
      try { this.runs[who] = Limits.readFrames(path.join(dir, 'assets', 'run_' + who + '.bin')); } catch {}
    }
  }

  update(rl) {
    if (!rl || typeof rl !== 'object') return false;
    const pick = w => w && typeof w.used_percentage === 'number'
      ? { pct: Math.round(w.used_percentage), resets: typeof w.resets_at === 'number' ? w.resets_at * 1000 : null } : null;
    const extra = [];
    for (const [k, v] of Object.entries(rl)) {
      if (k === 'five_hour' || k === 'seven_day' || !v || typeof v !== 'object') continue;
      const w = pick(v);
      if (w) extra.push({ key: k, label: Limits.labelOf(k, v), ...w });
    }
    const next = { five: pick(rl.five_hour), week: pick(rl.seven_day), extra, at: Date.now() };
    if (!next.five && !next.week && !extra.length) return false;
    const was = this.data ? JSON.stringify([this.data.five, this.data.week, this.data.extra]) : '';
    this.data = next;
    try { fs.writeFileSync(this.file, JSON.stringify(next)); } catch {}
    return was !== JSON.stringify([next.five, next.week, next.extra]);
  }

  // «seven_day_fable» → «FABLE», если есть поле с названием — оно
  static labelOf(key, v) {
    const named = v.model || v.label || v.name || v.display_name;
    const s = String(named || key.replace(/^(seven_day|five_hour|weekly|daily)_?/, '') || key);
    return s.replace(/[_-]+/g, ' ').toUpperCase().slice(0, 10);
  }

  // окно, у которого прошло время сброса, считаем обнулившимся
  window(name) {
    let w = this.data && (name === 'five' || name === 'week' ? this.data[name] : (this.data.extra || []).find(x => x.key === name));
    if (!w) return null;
    if (w.resets && Date.now() > w.resets) return { ...w, pct: 0, resets: null };
    return w;
  }

  // что показывать: 5 часов, неделя, лимиты по модели, контекст сессии
  views(ctx) {
    const v = ['all', 'five', 'week'];
    for (const x of (this.data && this.data.extra) || []) v.push(x.key);
    if (ctx) v.push('ctx');
    return v;
  }

  static color(pct) { return pct >= 90 ? RED : pct >= 70 ? AMBER : W; }

  static when(ms, week) {
    if (!ms) return '';
    const d = new Date(ms), p = n => String(n).padStart(2, '0');
    if (!week) return p(d.getHours()) + ':' + p(d.getMinutes());
    return p(d.getDate()) + '.' + p(d.getMonth() + 1);
  }

  row(view, ctx) {
    if (view === 'ctx') return ctx ? { label: 'КОНТЕКСТ', pct: ctx.pct, note: Math.round(ctx.tokens / 1000) + 'K' } : null;
    const w = this.window(view);
    const label = view === 'five' ? '5 ЧАСОВ' : view === 'week' ? 'НЕДЕЛЯ' : (w ? w.label : view);
    if (!w) return { label, pct: null, note: '' };
    return { label, pct: w.pct, note: Limits.when(w.resets, view !== 'five') };
  }

  // настройка «Показывать» (five / week / fable / ctx / all) → окно из списка
  resolve(want, ctx) {
    const views = this.views(ctx);
    if (!want) return 'all';
    return views.find(v => v === want || v === 'scoped_' + want) || 'all';
  }

  // кнопка 196×196: всё вместе (до четырёх строк) или одно окно крупно
  keyPng(ctx, view, size = 196) {
    if (view && view !== 'all') return this.single(size, view, ctx);
    const c = new Canvas(size, size);
    const views = this.views(ctx).filter(v => v !== 'all').slice(0, 4);
    const h = Math.floor(176 / views.length);
    views.forEach((v, i) => {
      const r = this.row(v, ctx), y = 12 + i * h;
      const big = views.length <= 2;
      c.text(r.label, 20, y, 2, DIM);
      if (r.pct === null) { c.textRight('-', 176, y, 2, DIM); c.bar(20, y + 20, 156, 8, 12, 0, W, OFF); return; }
      const col = Limits.color(r.pct);
      if (big) {
        if (r.note) c.textRight(r.note, 176, y, 2, NOTE);
        c.text(r.pct + '%', 20, y + 20, 5, col);
        c.bar(20, y + 64, 156, 10, 10, r.pct, col, OFF);
      } else {
        c.textRight(r.pct + '%', 176, y - 2, 3, col);
        c.bar(20, y + 22, 156, 8, 12, r.pct, col, OFF);
        if (r.note && h >= 46) c.text(r.note, 20, y + 34, 1, NOTE);
      }
    });
    return c.png();
  }

  // экран крутилки 200×200: всё вместе или одно окно крупно
  dialPng(view, ctx) {
    return view === 'all' ? this.keyPng(ctx, 'all', 200) : this.single(200, view, ctx);
  }

  static readFrames(file) {
    const b = fs.readFileSync(file);
    const w = b.readUInt16LE(4), h = b.readUInt16LE(6), n = b.readUInt16LE(8), frames = [];
    let o = 10;
    for (let i = 0; i < n; i++) { const delay = b.readUInt16LE(o); o += 2; frames.push({ delay, px: b.subarray(o, o + w * h * 4) }); o += w * h * 4; }
    return { w, h, frames };
  }

  // Забег по нажатию: персонаж бежит по трассе от старта до своего процента, счётчик растёт вместе с ним,
  // на финише — радуется / запыхался / без сил. Возвращает { gif, ms } — длительность до возврата обычного вида.
  race(size, view, ctx) {
    const r = this.row(view, ctx) || { label: 'КОНТЕКСТ', pct: null, note: '' };
    const pct = r.pct === null ? 0 : r.pct;
    const who = view === 'five' || view === 'week' || view === 'ctx' ? view : 'fable';
    const run = this.runs[who];
    if (!run) return null;
    // поля как у остальных видов: по бокам ~30 px (флажок финиша входит внутрь), сверху и снизу ~22 px
    const x0 = 30, x1 = size - 38, trackY = 94, mid = size / 2;
    const frames = [], delays = [];
    const N = 24;
    const draw = (cur, sprite, step) => {
      const c = new Canvas(size, size);
      c.textCenter(r.label, mid, 24, 2, DIM);
      // трасса: серая дорожка, пройденное — цветом процента, отметки каждые 25 %, финишный флажок
      c.rect(x0, trackY, x1 - x0, 3, OFF);
      const px = x0 + (x1 - x0) * cur / 100;
      c.rect(x0, trackY, px - x0, 3, Limits.color(cur));
      for (let k = 0; k <= 4; k++) c.rect(Math.round(x0 + (x1 - x0) * k / 4) - 1, trackY + 4, 2, k % 2 ? 3 : 5, DIM);
      for (let fy = 0; fy < 4; fy++) for (let fx = 0; fx < 3; fx++) c.rect(x1 - 1 + fx * 3, trackY - 26 + fy * 3, 3, 3, (fx + fy) % 2 ? W : [30, 30, 32]);
      c.rect(x1 - 2, trackY - 26, 1, 26, DIM);
      // пыль из-под ног
      if (step >= 0 && cur > 2) for (let d = 0; d < 3; d++) if ((step + d) % 2) c.rect(px - 22 - d * 6, trackY - 4 - (d % 2) * 3, 3, 3, [90, 90, 94]);
      c.blit(sprite.px, run.w, run.h, Math.round(px - 24), trackY - 32);
      c.textCenter(Math.round(cur) + '%', mid, 110, 5, Limits.color(cur));
      if (r.note) c.textCenter((view === 'ctx' ? '' : 'СБРОС ') + r.note, mid, 154, 2, NOTE);
      return c.px;
    };
    for (let i = 0; i <= N; i++) {
      const t = i / N, cur = pct * (1 - (1 - t) * (1 - t));
      frames.push(draw(cur, run.frames[i % 2], i)); delays.push(6);
    }
    const end = pct >= 90 ? 4 : pct >= 70 ? 3 : 2;
    frames.push(draw(pct, run.frames[end], -1)); delays.push(220);
    const ms = delays.reduce((a, b) => a + b, 0) * 10;
    return { gif: encodeGif(size, size, frames, delays), ms };
  }

  // Кнопка контекста перед /compact: stage 'ask' — «СЖАТЬ?» и подсказка «ЕЩЁ РАЗ — ДА», 'doing' — «СЖИМАЮ…».
  // Персонаж со стопкой блоков (переполнен). Возвращает { gif }.
  compactGif(size, ctx, stage) {
    const mini = (this.minis.ctx || {})[stage === 'doing' ? 'calm' : 'panic'];
    const pct = ctx ? ctx.pct : 0, mid = size / 2;
    const frames = [], delays = [];
    const list = mini ? mini.frames : [{ delay: 50, px: null }];
    list.forEach((f, i) => {
      const c = new Canvas(size, size);
      // поля как у остальных кнопок: персонаж с 12 px, текст не шире ~148 px, снизу до ~160 px
      if (stage === 'ask') {
        c.textCenter('СЖАТЬ?', mid, 86, 4, AMBER);
        c.textCenter('КОНТЕКСТ ' + pct + '%', mid, 122, 2, NOTE);
        // подсказка мигает
        if (i % 2 === 0) c.textCenter('ЕЩЁ РАЗ - ДА', mid, 146, 2, W);
      } else {
        c.textCenter('СЖИМАЮ…', mid, 92, 3, W);
        c.textCenter('/COMPACT', mid, 126, 2, NOTE);
      }
      if (f.px) c.blit(f.px, mini.w, mini.h, Math.round((size - mini.w) / 2), 12);
      frames.push(c.px); delays.push(stage === 'ask' ? Math.max(f.delay, 20) : f.delay);
    });
    return { gif: encodeGif(size, size, frames, delays) };
  }

  static band(pct) { return pct >= 90 ? 'panic' : pct >= 70 ? 'full' : pct >= 50 ? 'busy' : 'calm'; }

  // Кнопка или экран крутилки. style: 'char' — одно окно с маленьким персонажем (GIF), иначе только цифра.
  // Возвращает { png, gif? }: png — для крутилки и как первый кадр, gif — анимация для кнопки.
  render(size, view, ctx, style) {
    if (view === 'all' || style !== 'char') return { png: view === 'all' ? this.keyPng(ctx, 'all', size) : this.single(size, view, ctx) };
    const r = this.row(view, ctx) || { label: 'КОНТЕКСТ', pct: null, note: '' };
    // поля как у вида «только цифра»: персонаж сверху (виден с 12 px), снизу — до 170 px, по бокам 30 px
    const base = new Canvas(size, size), mid = size / 2, pad = 30, sy = 12;
    base.textCenter(r.label, mid, sy + 70, 2, DIM);
    const pct = r.pct === null ? 0 : r.pct, col = r.pct === null ? DIM : Limits.color(pct);
    base.textCenter(r.pct === null ? '-' : pct + '%', mid, sy + 88, 5, col);
    base.bar(pad, sy + 128, size - 2 * pad, 10, 10, pct, col, OFF);
    if (r.note) base.textCenter((view === 'ctx' ? '' : 'СБРОС ') + r.note, mid, sy + 144, 2, NOTE);
    const who = view === 'five' || view === 'week' || view === 'ctx' ? view : 'fable';   // лимит по модели — «самый важный»
    const mini = (this.minis[who] || {})[Limits.band(pct)];
    if (!mini) return { png: base.png() };
    const x = Math.round((size - mini.w) / 2), frames = [], delays = [];
    let first = null;
    for (const f of mini.frames) {
      const c = base.copy();
      c.blit(f.px, mini.w, mini.h, x, sy);
      if (!first) first = c;
      frames.push(c.px); delays.push(f.delay);
    }
    return { png: first.png(), gif: encodeGif(size, size, frames, delays) };
  }

  // одно окно крупно: подпись, проценты, полоска (и время сброса).
  // Поля ~34 px со всех сторон — края экрана кнопки скруглены и обрезают картинку.
  single(size, view, ctx) {
    const r = this.row(view, ctx) || { label: 'КОНТЕКСТ', pct: null, note: '' };
    const c = new Canvas(size, size), mid = size / 2, pad = 30, top = Math.round((size - 128) / 2);
    const ls = Canvas.textWidth(r.label, 3) <= size - 48 ? 3 : 2;
    c.textCenter(r.label, mid, top + (ls === 3 ? 0 : 4), ls, DIM);
    if (r.pct === null) { c.textCenter('-', mid, top + 30, 7, DIM); c.bar(pad, top + 90, size - 2 * pad, 12, 10, 0, W, OFF); return c.png(); }
    const col = Limits.color(r.pct);
    c.textCenter(r.pct + '%', mid, top + 30 + (r.pct >= 100 ? 4 : 0), r.pct >= 100 ? 6 : 7, col);
    c.bar(pad, top + 90, size - 2 * pad, 12, 10, r.pct, col, OFF);
    if (r.note) c.textCenter((view === 'ctx' ? '' : 'СБРОС ') + r.note, mid, top + 114, 2, NOTE);
    return c.png();
  }

  dialTitle(view, ctx) {
    if (view === 'all') {
      const f = this.window('five');
      return 'лимиты' + (f && f.resets ? ' · 5 ч сброс ' + Limits.when(f.resets, false) : '');
    }
    if (view === 'ctx') return ctx ? 'контекст · ' + Math.round(ctx.tokens / 1000) + ' тыс. токенов' : 'контекст: нет данных';
    const w = this.window(view);
    const name = view === 'five' ? '5 часов' : view === 'week' ? 'неделя' : (w ? w.label.toLowerCase() : view);
    if (!w) return name + ': нет данных';
    return name + (w.resets ? ' · сброс ' + Limits.when(w.resets, view !== 'five') : '');
  }

  // отпечаток картинки: одно окно — только его строка (5 часов не перерисовываются от каждого ответа Claude,
  // контекст — от обновления лимитов); «всё вместе» — всё
  stamp(view, ctx, style) {
    if (view && view !== 'all') return JSON.stringify([view, style || '', this.row(view, ctx)]);
    return JSON.stringify([view || '', style || '', this.data && [this.data.five, this.data.week, this.data.extra], ctx && ctx.pct, ctx && Math.round(ctx.tokens / 1000)]);
  }
}
