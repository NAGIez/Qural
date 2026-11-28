// Какая страница деки сейчас на экране — общее для плагинов Qural (Claude, Discord, громкость; файл один и тот же).
// Ulanzi не говорит «кнопка скрыта»: при смене страницы приходит только «видна» (setactive) кнопкам новой страницы.
// А картинка для кнопки скрытой страницы попадает на то же место текущей — и там гаснет или мигает чужое.
// Поэтому: кнопки, которым пришло «видна» одной пачкой, — на экране; остальные кнопки этого плагина — скрыты.
// Плагин, у которого на новой странице кнопок нет, пачки не получит — о смене страницы ему скажет файл
// qural-page.json в папке плагинов: туда каждый плагин сразу пишет время начала своей пачки.
import fs from 'node:fs';
import path from 'node:path';

export class Page {
  // actions: Map context → кнопка { active, shownAt }; onHide() — что-то спрятали
  constructor(pluginDir, name, actions, onHide, log) {
    this.log = log || null;
    this.dir = path.join(pluginDir, '..');
    this.file = path.join(this.dir, 'qural-page.json');
    this.name = name; this.actions = actions; this.onHide = onHide || (() => {});
    this.burstAt = 0; this.seen = 0;
    try { this.seen = fs.statSync(this.file).mtimeMs; } catch {}
    // о смене страницы — сразу (слежение за папкой), опрос раз в 250 мс — на случай, если слежение молчит
    try { fs.watch(this.dir, (ev, f) => { if (f === 'qural-page.json') this.poll(); }).on('error', () => {}); } catch {}
    setInterval(() => this.poll(), 250).unref();
  }

  // кнопке пришло «видна» (add, setactive). Первое «видна» новой пачки — новая страница:
  // остальные кнопки этого плагина сразу прячем и говорим другим плагинам
  shown(a) {
    const now = Date.now();
    a.active = true; a.shownAt = now;
    if (now - this.burstAt <= 150) return;   // пачка одной страницы приходит за ~80 мс
    this.burstAt = now;
    if (this.log) this.log('PAGE new burst', a.key);
    this.hideBefore(now - 50);
    // через временный файл: другой плагин не прочитает его наполовину записанным
    const tmp = this.file + '.' + this.name;
    try { fs.writeFileSync(tmp, JSON.stringify({ at: now, by: this.name, pid: process.pid })); fs.renameSync(tmp, this.file); }
    catch { try { fs.writeFileSync(this.file, JSON.stringify({ at: now, by: this.name, pid: process.pid })); } catch {} }
  }

  // кнопку нажали — она точно на экране, но это не смена страницы (других не прячем)
  mark(a) { a.active = true; a.shownAt = Date.now(); }

  // кнопки, которым «видна» приходило раньше этого времени, — на другой странице
  hideBefore(t) {
    let hid = false;
    for (const a of this.actions.values()) if (a.active && (a.shownAt || 0) < t) { a.active = false; hid = true; if (this.log) this.log('PAGE hide', a.key); }
    if (hid) this.onHide();
  }

  // другой плагин получил пачку — страница сменилась. Запас 0,1 с: своя пачка той же страницы приходит в пределах ~50 мс
  // (больший запас не даёт спрятать кнопки, когда страницы листают быстро)
  poll() {
    let st; try { st = fs.statSync(this.file); } catch { return; }
    if (st.mtimeMs === this.seen) return;
    // прочитанным считаем, только если разобрали: файл могли поймать посреди записи — тогда перечитать в следующий раз
    let j; try { j = JSON.parse(fs.readFileSync(this.file, 'utf8')); } catch { return; }
    this.seen = st.mtimeMs;
    if (!j || j.by === this.name || !j.at) return;
    if (this.log) this.log('PAGE other', j.by, Date.now() - j.at);
    this.hideBefore(j.at - 100);
  }
}
