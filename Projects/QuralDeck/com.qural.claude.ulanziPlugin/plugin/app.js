// Qural — Claude: статус и управление приложением Claude с кнопок Ulanzi Deck.
// Ulanzi Studio запускает этот файл своим Node: app.js <адрес> <порт> <язык> <версия>.
//  • Статус и запросы разрешения приходят от хуков Claude Code (hook/hook.cjs) по каналу \\.\pipe\Qural.Claude.<пользователь>.
//  • «Стоп», «Новый чат», «Диктовка» и вывод окна вперёд делает Qural (канал Qural.Deck, команда keys).
import WebSocket from 'ws';
import net from 'node:net';
import os from 'node:os';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Limits } from './limits.js';
import { Usage } from './usage.js';
import { Focus } from './focus.js';
import { clockGif, clockStamp } from './clock.js';
import { Page } from './page.js';

const MAIN = 'com.qural.deck.claude';
const [address = '127.0.0.1', port = '3906'] = process.argv.slice(2);
const pluginDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const user = process.env.USERNAME || os.userInfo().username;
const limits = new Limits(pluginDir);
const usage = new Usage(rl => { if (limits.update(rl)) redraw(); }, (...a) => log(...a));
// лимиты с сервера — пока на деке есть кнопка/крутилка «Лимиты» и в её настройке не выключено
function syncUsage() {
  let on = false;
  for (const a of actions.values()) if (a.kind === 'limits' && a.settings.server !== 'off') on = true;
  usage.setEnabled(on);
}

// ── журнал (stdout плагина Ulanzi никуда не пишет) ──
const logFile = path.join(pluginDir, 'plugin.log');
try { if (fs.statSync(logFile).size > 512 * 1024) fs.truncateSync(logFile, 0); } catch {}
function log(...a) {
  try { fs.appendFileSync(logFile, new Date().toISOString() + ' ' + a.map(x => typeof x === 'string' ? x : JSON.stringify(x)).join(' ') + '\n'); } catch {}
}
process.on('uncaughtException', e => log('ERR', e && e.stack || String(e)));
process.on('unhandledRejection', e => log('ERR', e && e.stack || String(e)));

// ── картинки (assets/*.png → data:) ──
const images = {};
function img(name) {
  if (!images[name]) {
    try { images[name] = 'data:image/png;base64,' + fs.readFileSync(path.join(pluginDir, 'assets', name + '.png')).toString('base64'); }
    catch { images[name] = ''; }
  }
  return images[name];
}

// действие → что делает и какие клавиши по умолчанию
const KIND = {
  [MAIN + '.status']: 'status',
  [MAIN + '.allow']: 'allow',
  [MAIN + '.deny']: 'deny',
  [MAIN + '.stop']: 'stop',
  [MAIN + '.newchat']: 'newchat',
  [MAIN + '.dictation']: 'dictation',
  [MAIN + '.browser']: 'browser',
  [MAIN + '.ask']: 'ask',          // крутилка «разрешить / отклонить»
  [MAIN + '.limits']: 'limits',
  [MAIN + '.clock']: 'clock',      // часы пиксельным шрифтом
};
const DEFAULT_KEYS = { stop: 'esc', newchat: 'ctrl+n', dictation: 'ctrl+d', browser: 'ctrl+shift+b', status: '' };
const LABEL = { question: 'выберите', idle: 'ваш ход', thinking: 'думает', working: 'работает', permission: 'разреши?', waiting: 'ваш ход', done: 'готово', error: 'ошибка', offline: 'нет связи', full: 'сжать?' };

// ── кнопки: context → { uuid, key, actionid, kind, settings, active, shown } ──
const actions = new Map();
const ctxOf = j => j.uuid + '___' + j.key + '___' + j.actionid;
const page = new Page(pluginDir, 'claude', actions, null, fs.existsSync(path.join(pluginDir, 'debug-img')) ? (...m) => log(...m) : null);   // какие кнопки сейчас на экране (page.js)

// ════════════ Ulanzi Studio ════════════
let ws = null;
function connectHost() {
  ws = new WebSocket(`ws://${address}:${port}`);
  ws.on('open', () => { log('host: connected'); ws.send(JSON.stringify({ code: 0, cmd: 'connected', uuid: MAIN })); });
  ws.on('message', buf => {
    let d; try { d = JSON.parse(buf.toString()); } catch { return; }
    if (!d || (typeof d.code !== 'undefined' && d.cmdType !== 'REQUEST')) return;
    sendHost({ ...d, code: 0 }); // основной сервис подтверждает каждое событие (как в SDK Ulanzi)
    try { onHost(d); } catch (e) { log('ERR onHost', e.stack); }
  });
  ws.on('close', () => { log('host: closed'); process.exit(0); });
  ws.on('error', e => log('host error', e.message));
}

// Картинки на деку. Ulanzi шлёт «кнопка видна» по два раза, и две картинки одной кнопке за пару миллисекунд
// дека не переваривает — кнопка гаснет до следующей картинки. Поэтому: одной кнопке — одно сообщение
// за 50 мс (последнее побеждает), и ту же самую картинку повторно — не раньше чем через секунду.
const imgPending = new Map(), imgLast = new Map();
function sendHost(obj) {
  const img = obj.cmd === 'state' || obj.cmd === 'setFeedback';
  if (!img) { if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj)); return; }
  const k = ctxOf(obj);
  clearTimeout(imgPending.get(k));
  imgPending.set(k, setTimeout(() => {
    imgPending.delete(k);
    { const b = actions.get(k); if (b && !b.active) return; }   // кнопку уже спрятали (страницу сменили) — не слать
    const s = JSON.stringify(obj), last = imgLast.get(k), now = Date.now();
    if (last && last.s === s && now - last.at < 1000) return;
    imgLast.set(k, { s, at: now });
    if (ws && ws.readyState === WebSocket.OPEN) { ws.send(s); if (DEBUG_IMG) log('IMG', obj.key, obj.cmd, s.length); }
  }, 50));
}
const DEBUG_IMG = (() => { try { return fs.existsSync(path.join(pluginDir, 'debug-img')); } catch { return false; } })();

function onHost(d) {
  if (DEBUG_IMG && d.cmd && !/^(keydown|keyup|dialdown|dialup|dialrotate)$/.test(d.cmd)) log('EV', d.cmd, d.key, 'active=' + d.active, String(d.actionid || '').slice(0, 6));   // отладка: файл debug-img в папке плагина
  if (d.cmd === 'clear') {
    for (const p of d.param || []) actions.delete(ctxOf(p));
    syncUsage();
    return;
  }
  if (!d.uuid || d.key === undefined) return;
  const kind = KIND[d.uuid];
  if (!kind) return;
  const ctx = ctxOf(d);
  let a = actions.get(ctx);
  if (!a) {
    a = { uuid: d.uuid, key: d.key, actionid: d.actionid, kind, settings: {}, active: false, shown: '', controller: '' };
    actions.set(ctx, a);
  }
  // кнопка или крутилка: Ulanzi сообщает в add; без него — крутилки в нижнем ряду (ключи «x_3»)
  if (d.controller) a.controller = d.controller;
  else if (!a.controller) a.controller = a.kind === 'ask' || /_3$/.test(String(d.key)) ? 'Encoder' : 'Keypad';
  if (d.param && typeof d.param === 'object' && !Array.isArray(d.param) && Object.keys(d.param).length) a.settings = d.param;

  switch (d.cmd) {
    case 'add':
      page.shown(a); a.shown = ''; draw(a); again(a);
      if (a.kind === 'limits') syncUsage();
      break;
    // параметры кнопки (дека подключилась, открыли настройку в приложении) — видимость не меняют:
    // их присылают и кнопкам другой страницы
    case 'paramfromapp':
    case 'paramfromplugin':
      if (a.active) { a.shown = ''; draw(a); again(a); }
      if (a.kind === 'limits') syncUsage();
      break;
    case 'didReceiveSettings':
      if (d.settings) { a.settings = d.settings; a.shown = ''; a.view = undefined; redraw(); syncUsage(); } // например, выключили лимиты с сервера
      break;
    case 'setactive':
      if (d.active === false) { a.active = false; break; }
      page.shown(a); a.shown = ''; draw(a); again(a);
      break;
    case 'keydown':
      if (!a.active) { page.mark(a); a.shown = ''; draw(a); }   // нажали — значит, на экране
      if (a.kind === 'status') { a.downAt = Date.now(); break; }   // решаем на отпускании: коротко или долго
      press(ctx, a);
      break;
    case 'keyup':
      if (a.kind === 'status' && a.downAt) {
        const long = Date.now() - a.downAt >= 600;
        a.downAt = 0;
        if (long) keys(ctx, a, '');      // долгое — открыть окно Claude
        else playFun(a);                 // короткое — сценка «Claude кодит»
      }
      break;
    case 'dialdown':
      a.pressed = true; a.rotated = false;
      break;
    case 'dialup':
      if (a.pressed && !a.rotated) dialPress(ctx, a);
      a.pressed = false;
      break;
    case 'dialrotate':
      a.rotated = true;
      rotate(ctx, a, String(d.rotateEvent || '').includes('right') ? 1 : -1);
      break;
  }
}

function keys(ctx, a, combo) {
  if (!toQural({ cmd: 'keys', id: ctx, app: 'claude', keys: combo })) alert(a);
}

// нажатие крутилки: открыть Claude (у лимитов — обновить)
function dialPress(ctx, a) {
  if (a.kind === 'limits') { refreshLimits(true); return; }
  keys(ctx, a, '');
}

// поворот: статус — переключить сессию; «разрешить/отклонить» — вправо да, влево нет; лимиты — 5 ч / неделя
let lastAnswerAt = 0;
function rotate(ctx, a, dir) {
  switch (a.kind) {
    case 'status': keys(ctx, a, dir > 0 ? 'ctrl+tab' : 'ctrl+shift+tab'); break;
    case 'ask': {
      if (Date.now() - lastAnswerAt < 700) return; // один щелчок — один ответ, даже если крутить быстро
      const p = oldestPending();
      if (!p) { alert(a); return; }
      lastAnswerAt = Date.now();
      answer(p, dir > 0 ? 'allow' : 'deny');
      break;
    }
    case 'limits': {
      // по кругу: 5 часов → неделя → лимиты по модели → контекст
      const views = limits.views(currentContext());
      const i = views.indexOf(a.view);
      a.view = views[(i + (dir > 0 ? 1 : views.length - 1)) % views.length]; a.fellBack = false;   // выбрали сами
      a.shown = ''; draw(a);
      break;
    }
  }
}

function press(ctx, a) {
  switch (a.kind) {
    case 'allow': case 'deny': {
      const p = oldestPending();
      if (!p) { alert(a); return; }
      answer(p, a.kind);
      break;
    }
    case 'limits': {
      // контекст: первое нажатие — «СЖАТЬ?», второе за 5 секунд — /compact в открытом чате Claude
      if (a.view === 'ctx') { compactPress(ctx, a); break; }
      // одно окно (5 часов, неделя, Fable) — с персонажем или без — забег по трассе до своего процента;
      // «всё вместе» — следующее окно
      if (a.view && a.view !== 'all' && !a.racing) {
        const race = limits.race(196, a.view, currentContext());
        if (race) {
          a.racing = true;
          sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
            param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, type: 4, gifpath: cacheGif(race.gif), textData: '', showtext: false }] } });
          setTimeout(() => { a.racing = false; a.shown = ''; draw(a); }, race.ms);
          break;
        }
      }
      if (a.racing) break;
      // нажатие — следующее окно: всё вместе → 5 часов → неделя → Fable → контекст
      const views = limits.views(currentContext());
      a.view = views[(views.indexOf(a.view) + 1) % views.length]; a.fellBack = false;
      a.shown = ''; draw(a);
      if (!limits.data) usage.poll();
      break;
    }
    case 'ask': keys(ctx, a, ''); break;
    case 'clock': break;
    case 'stop': {
      // Esc в окно Claude; хука Stop после прерывания не будет — сразу считаем открытый чат остановленным
      // (если Claude продолжит работать, следующий хук вернёт «работает»)
      const sent = toQural({ cmd: 'keys', id: ctx, app: 'claude', keys: (a.settings.keys || '').trim() || DEFAULT_KEYS.stop });
      if (!sent) { alert(a); break; }
      const cli = focus.focusedCli();
      for (const [id, s] of sessions)
        if ((cli ? id === cli : true) && (s.state === 'working' || s.state === 'thinking')) { s.state = 'idle'; s.at = Date.now(); dropPendingOf(id); }
      redraw();
      break;
    }
    default: keys(ctx, a, (a.settings.keys || '').trim() || DEFAULT_KEYS[a.kind] || '');
  }
}

// жёлтый значок Ulanzi «не получилось»; сам он не уходит — через 1,5 с возвращаем кнопке её картинку
function alert(a) {
  sendHost({ cmd: 'showAlert', uuid: a.uuid, key: a.key, actionid: a.actionid });
  clearTimeout(a.alertTimer);
  a.alertTimer = setTimeout(() => { a.shown = ''; draw(a); }, 1500);
}

// ── картинка кнопки по текущему состоянию ──
function imageFor(a) {
  switch (a.kind) {
    case 'status': return 'status_' + focusedState();
    case 'allow': return pending.size ? 'allow_on' : 'allow_off';
    case 'deny': return pending.size ? 'deny_on' : 'deny_off';
    case 'stop': return canStop() ? 'stop_on' : 'stop_off';
    case 'dictation': return micOn ? 'dictation_on' : 'dictation_off';   // белая — есть что остановить, серая — нечего
    default: return a.kind;
  }
}

// экран крутилки: картинка (путь к PNG) и подпись
function feedbackFor(a) {
  if (a.kind === 'status') {
    const st = focusedState(), n = activeSessions();
    const c = currentContext();
    return { image: 'dial_' + st, title: LABEL[st] + (c ? ' · контекст ' + c.pct + '%' : '') + (n > 1 ? ' · сессий ' + n : '') };
  }
  if (a.kind === 'ask') {
    const p = oldestPending();
    if (!p) return { image: 'dial_ask_off', title: 'нет запросов' };
    const what = (p.p.tool || 'запрос') + (p.p.text ? ': ' + p.p.text : '');
    return { image: 'dial_ask_on', title: (pending.size > 1 ? '(' + pending.size + ') ' : '') + (what.length > 26 ? what.slice(0, 25) + '…' : what) };
  }
  if (a.kind === 'limits') return limitsFeedback(a);
  return { image: a.kind, title: '' };
}

function showGif(a, buf) {
  sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
    param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, type: 4, gifpath: cacheGif(buf), textData: '', showtext: false }] } });
}

function compactPress(ctx, a) {
  clearTimeout(a.compactTimer);
  const back = ms => { a.compactTimer = setTimeout(() => { a.compactUntil = 0; a.racing = false; a.shown = ''; draw(a); }, ms); };
  if (a.compactUntil && Date.now() < a.compactUntil) {
    // подтвердили: окно Claude вперёд, в поле ввода «/compact» (заменяя недописанное), Enter
    a.compactUntil = 0;
    if (!toQural({ cmd: 'keys', id: ctx, app: 'claude', text: '/compact', replace: true, keys: 'enter' })) { a.racing = false; alert(a); return; }
    // первый Enter только выбирает команду в меню подсказок «/…» — через долю секунды жмём Enter ещё раз, чтобы отправить
    setTimeout(() => toQural({ cmd: 'keys', id: ctx, app: 'claude', keys: 'enter' }), 400);
    log('compact: отправлено');
    showGif(a, limits.compactGif(196, currentContext(), 'doing').gif);
    back(2500);
    return;
  }
  a.racing = true;                       // пока спрашиваем — обычная картинка не перебивает
  a.compactUntil = Date.now() + 5000;
  showGif(a, limits.compactGif(196, currentContext(), 'ask').gif);
  back(5000);
}

// сценка по короткому нажатию на «Статус»: Claude кодит за монитором (assets/status_fun.gif), потом снова статус
function playFun(a) {
  if (a.playing) return;
  const gif = path.join(pluginDir, 'assets', 'status_fun.gif');
  if (!fs.existsSync(gif)) return;
  a.playing = true;
  sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
    param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, type: 4, gifpath: gif.split(path.sep).join('/'), textData: '', showtext: false }] } });
  setTimeout(() => { a.playing = false; a.shown = ''; draw(a); }, 3600);
}

function draw(a) {
  if (!a.active || a.playing) return;
  if (a.controller === 'Encoder') { drawDial(a); return; }
  if (a.kind === 'limits') { drawLimitsKey(a); return; }
  if (a.kind === 'clock') { drawClock(a); return; }
  const name = imageFor(a);
  if (a.shown === name) return;
  // статус — анимация (assets/status_*.gif, путь к файлу); остальные кнопки — картинка PNG
  const gif = path.join(pluginDir, 'assets', name + '.gif');
  let item;
  if ((a.kind === 'status' || a.kind === 'dictation') && fs.existsSync(gif)) item = { type: 4, gifpath: gif.replace(/\\/g, '/') };
  else { const data = img(name); if (!data) return; item = { type: 1, data }; }
  a.shown = name;
  sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
    param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, ...item, textData: '', showtext: false }] } });
}

// кнопка только что появилась на экране (старт деки, смена страницы): дека иногда пропускает первую картинку,
// а в приложении при этом всё видно — присылаем ещё раз через 1,5 с
function again(a) {
  clearTimeout(a.again1); clearTimeout(a.again2);
  const re = () => { if (a.active && !a.playing && !a.racing) { a.shown = ''; draw(a); } };
  a.again1 = setTimeout(re, 1500);
}

// ── лимиты ──
// что показывает кнопка: выбранное в настройке, а если его сейчас нет (контекст не найден, лимит по модели не пришёл) —
// временно «всё вместе»; как только выбранное появится — вернуться к нему
function viewOf(a, ctx) {
  const want = a.settings.show || 'all';
  if (a.fellBack) {
    const v = limits.resolve(want, ctx);
    if (v !== 'all') { a.view = v; a.fellBack = false; }
  }
  if (!limits.views(ctx).includes(a.view)) {
    a.view = limits.resolve(want, ctx);
    a.fellBack = a.view === 'all' && want !== 'all';
  }
  return a.view;
}

function limitsFeedback(a) {
  const ctx = currentContext();
  viewOf(a, ctx);
  const view = a.view, style = a.settings.style === 'num' ? 'num' : 'char';
  const png = limits.render(200, view, ctx, style).png;
  return { data: 'data:image/png;base64,' + png.toString('base64'), image: 'limits:' + limits.stamp(view, ctx, style), title: limits.dialTitle(view, ctx) };
}

function drawLimitsKey(a) {
  if (a.racing) return;   // идёт забег — не перебиваем
  const ctx = currentContext();
  viewOf(a, ctx);
  const style = a.settings.style === 'num' ? 'num' : 'char';
  const stamp = 'limits:' + limits.stamp(a.view, ctx, style);
  if (a.shown === stamp) return;
  a.shown = stamp;
  const r = limits.render(196, a.view, ctx, style);
  let item;
  if (r.gif) item = { type: 4, gifpath: cacheGif(r.gif) };   // анимация — файлом, как у статуса
  else item = { type: 1, data: 'data:image/png;base64,' + r.png.toString('base64') };
  sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
    param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, ...item, textData: '', showtext: false }] } });
}

// GIF лимитов — во временную папку плагина; у каждого содержимого своё имя (Ulanzi кэширует по пути),
// старые файлы убираем, оставляя последние 12
const cacheDir = path.join(pluginDir, 'cache');
function cacheGif(buf) {
  try { fs.mkdirSync(cacheDir, { recursive: true }); } catch {}
  let h = 0; for (let i = 0; i < buf.length; i += 7) h = (h * 31 + buf[i]) >>> 0;
  const file = path.join(cacheDir, 'lim_' + h.toString(16) + '_' + buf.length + '.gif');
  if (!fs.existsSync(file)) {
    fs.writeFileSync(file, buf);
    try {
      const old = fs.readdirSync(cacheDir).map(n => ({ n, t: fs.statSync(path.join(cacheDir, n)).mtimeMs })).sort((a, b) => b.t - a.t);
      for (const o of old.slice(12)) fs.unlinkSync(path.join(cacheDir, o.n));
    } catch {}
  }
  return file.split(path.sep).join('/');
}

// ── часы: GIF с мигающим двоеточием, меняется раз в минуту ──
function drawClock(a) {
  const stamp = 'clock:' + clockStamp();
  if (a.shown === stamp) return;
  a.shown = stamp;
  const r = clockGif(196);
  sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
    param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, type: 4, gifpath: cacheGif(r.gif), textData: '', showtext: false }] } });
}
// ровно в начале каждой минуты
(function tick() {
  const now = new Date();
  setTimeout(() => { for (const a of actions.values()) if (a.kind === 'clock') draw(a); tick(); }, 60000 - now.getSeconds() * 1000 - now.getMilliseconds() + 50).unref();
})();

// нажатие на лимиты: перерисовать (и предупредить, если данных ещё нет)
function refreshLimits(force) {
  if (force) usage.poll();
  for (const a of actions.values()) if (a.kind === 'limits') { if (force) a.shown = ''; draw(a); if (!limits.data && !currentContext()) alert(a); }
}
// время сброса прошло — окно обнуляется: раз в минуту перерисовываем
setInterval(() => { for (const a of actions.values()) if (a.kind === 'limits') draw(a); }, 60e3).unref();

function drawDial(a) {
  const f = feedbackFor(a);
  const value = f.data || path.join(pluginDir, 'assets', f.image + '.png').split(path.sep).join('/');
  const key = (f.data ? 'data:' + f.image : f.image) + '|' + f.title;
  if (a.shown === key) return;
  a.shown = key;
  sendHost({ cmd: 'setFeedback', uuid: a.uuid, key: a.key, actionid: a.actionid,
    layout: { icon: { value }, title: { text: f.title } } });
}

let drawTimer = null;
function redraw() {
  clearTimeout(drawTimer);
  drawTimer = setTimeout(() => { for (const a of actions.values()) draw(a); }, 20);
}

// ════════════ Состояние Claude (от хуков) ════════════
// session → { state, at }. state: idle | thinking | working | permission | waiting | done | error
const sessions = new Map();
const pending = new Map(); // запросы разрешения, которые ждут деку: id → { socket, session, tool, text, at }
let pendingSeq = 0;

const PRIORITY = ['permission', 'error', 'full', 'working', 'thinking', 'waiting', 'done', 'idle'];

// ── контекст сессии ──
// размер окна контекста по модели: в журнале его нет. Модель хоть раз ушла за 200 тыс. — значит, у неё окно 1 млн;
// запоминаем в ctxwin.json. В настройке кнопки статуса можно задать вручную.
const ctxWinFile = path.join(pluginDir, 'ctxwin.json');
let ctxWin = {};
try { ctxWin = JSON.parse(fs.readFileSync(ctxWinFile, 'utf8')); } catch {}
// окно контекста по модели: известные — сразу (приложение Claude показывает для Opus 5.5 «/ 1 млн»),
// «[1m]» в имени — 1 млн; остальные — 200 тыс., пока токенов не станет больше (тогда запоминаем 1 или 2 млн)
const MODEL_WIN = [[/\[1m\]/i, 1000000], [/^claude-opus-5-5/i, 1000000]];
function windowFor(model, tokens) {
  const base = (MODEL_WIN.find(([re]) => re.test(model || '')) || [0, 200000])[1];
  const known = ctxWin[model] || 0;
  let w = Math.max(base, known);
  if (tokens > w) { w = tokens > 1000000 ? 2000000 : 1000000; }
  if (w > base && w !== known && model) { ctxWin[model] = w; try { fs.writeFileSync(ctxWinFile, JSON.stringify(ctxWin)); } catch {} }
  return w;
}
let lastCtxSession = '';
const FULL_AT = 70; // с этого процента — «переполнен, пора сжать»

function contextPct(sess) {
  if (!sess || !sess.ctx) return null;
  const manual = manualWindow();
  const win = manual || windowFor(sess.ctx.model, sess.ctx.tokens);
  return Math.min(100, Math.round(sess.ctx.tokens * 100 / win));
}
function manualWindow() {
  for (const a of actions.values()) if (a.kind === 'status' && +a.settings.ctxwin > 0) return +a.settings.ctxwin;
  return 0;
}
// контекст открытого в Claude чата (а если открытый не найден — той сессии, что последней что-то делала)
const focus = new Focus(changed => { if (changed) { for (const a of actions.values()) a.shown = ''; } redraw(); }, (...a) => log(...a));
let lastCtx = null;   // последний найденный контекст — если на миг не нашли (чат только создан, файл пишется), показываем его
function currentContext() {
  const c = findContext();
  if (c) lastCtx = c;
  return c || lastCtx;
}
function findContext() {
  const f = focus.focused();
  if (f) {
    const c = focus.context(f.cli);
    if (c) {
      const pct = contextPct({ ctx: { tokens: c.tokens, model: c.model || f.model } });
      return { pct, tokens: c.tokens, title: f.title };
    }
    // открыт новый чат, модель ещё не отвечала — контекст пустой, а не сводка лимитов вместо него
    if (focus.transcriptOf(f.cli)) return { pct: 0, tokens: 0, title: f.title };
  }
  const s = sessions.get(lastCtxSession);
  const pct = contextPct(s);
  return pct === null ? null : { pct, tokens: s.ctx.tokens };
}

// Claude сейчас думает или работает — значит, «Стоп» имеет смысл
// Состояние чата, открытого в приложении Claude: ваш ход / думает / работает / разреши? / выберите / готово /
// ошибка / сжать?. «Готово» держится минуту, потом — «ваш ход». Открытый чат не найден — сводка по всем сессиям.
// проверка картинок: в папке плагина файл test-status.txt с именем состояния (idle, working…) — кнопка «Статус»
// показывает его, пока файл есть. Без файла — обычная работа.
// Вызывать удобно через tools\test-status.ps1.
const testFile = path.join(pluginDir, 'test-status.txt');
let testState = '';
setInterval(() => {
  let s = ''; try { s = fs.readFileSync(testFile, 'utf8').trim(); } catch {}
  if (s && !/^[a-z]+$/.test(s) || s && !fs.existsSync(path.join(pluginDir, 'assets', 'status_' + s + '.gif'))) s = '';
  if (s !== testState) { testState = s; log('test status: ' + (s || 'выкл')); redraw(); }
}, 500).unref();

function focusedState() {
  if (testState) return testState;
  const cli = focus.focusedCli();
  if (!cli) return overall();
  for (const p of pending.values()) if (p.session === cli) return 'permission';
  prune();
  const s = sessions.get(cli);
  let st = s ? stateOf(cli, s) : 'idle';
  if (st === 'waiting') st = 'idle';
  if (st === 'done' && Date.now() - s.at > 60e3) st = 'idle';
  return st;
}
// «готово» → «ваш ход» через минуту: перерисовать
setInterval(() => redraw(), 15e3).unref();

// «работает/думает», но в журнале после этого «[Request interrupted by user]» — Claude остановили, ждёт вас
function stateOf(cli, s) {
  if (s.state !== 'working' && s.state !== 'thinking') return s.state;
  const c = focus.context(cli);
  if (c && c.interruptedAt > s.at) { s.state = 'idle'; s.at = c.interruptedAt; dropPendingOf(cli); }
  return s.state;
}

function canStop() {
  prune();
  const cli = focus.focusedCli();
  if (cli) { const s = sessions.get(cli); if (!s) return false; const st = stateOf(cli, s); return st === 'working' || st === 'thinking'; }
  for (const s of sessions.values()) if (s.state === 'working' || s.state === 'thinking') return true;
  return false;
}

// сессии, которые сейчас что-то делают или ждут (без «свободен»)
function activeSessions() {
  let n = 0;
  for (const s of sessions.values()) if (s.state !== 'idle') n++;
  return n;
}

function overall() {
  if (pending.size) return 'permission';
  prune();
  let best = 'idle';
  for (const s of sessions.values()) { const p = contextPct(s); if (p !== null && p >= FULL_AT && s.state !== 'idle') s.full = true; else s.full = false; }
  for (const s of sessions.values()) {
    const st = s.state;
    if (PRIORITY.indexOf(st) < PRIORITY.indexOf(best)) best = st;
  }
  return best;
}

// забытые сессии: Claude закрыли без SessionEnd, «работает» без событий 10 минут, «готово/ошибка» через 2 часа
function prune() {
  const now = Date.now();
  for (const [id, s] of sessions) {
    const age = now - s.at;
    if ((s.state === 'working' || s.state === 'thinking' || s.state === 'permission') && age > 10 * 60e3) sessions.delete(id);
    else if (age > 2 * 3600e3) sessions.delete(id);
  }
}
setInterval(() => { const before = overall(); prune(); if (overall() !== before) redraw(); }, 60e3).unref();

function setState(session, state) {
  if (!session) session = '_';
  const old = sessions.get(session);
  sessions.set(session, { state, at: Date.now(), ctx: old ? old.ctx : null });
}

let loggedShape = false;
function onHook(m, socket) {
  const sid = m.session || '_';
  if (m.ctx && typeof m.ctx.tokens === 'number') {
    if (!sessions.has(sid)) setState(sid, 'idle');
    sessions.get(sid).ctx = { tokens: m.ctx.tokens, model: String(m.ctx.model || '') };
    lastCtxSession = sid;
  }
  log('hook', m.event, m.type || '', sid.slice(0, 8));
  switch (m.event) {
    case 'SessionStart': setState(sid, 'idle'); break;
    case 'SessionEnd': sessions.delete(sid); dropPendingOf(sid); break;
    case 'UserPromptSubmit': setState(sid, 'thinking'); break;
    case 'PreToolUse': case 'PostToolUse': case 'PostToolUseFailure': case 'SubagentStart': case 'SubagentStop':
      // запросы этой сессии не сбрасываем: инструменты могут идти параллельно, и разрешение ждёт другой из них
      setState(sid, 'working'); break;
    case 'Stop': setState(sid, 'done'); dropPendingOf(sid); break;
    case 'StopFailure': setState(sid, 'error'); dropPendingOf(sid); break;
    case 'Notification':
      // обычный запрос в окне Claude; но вопрос с вариантами (AskUserQuestion) через несколько секунд тоже шлёт
      // permission_prompt — тогда остаётся «выберите»
      if (m.type === 'permission_prompt') { const cur = sessions.get(sid); if (!cur || cur.state !== 'question') setState(sid, 'permission'); }
      else if (m.type === 'elicitation_dialog') setState(sid, 'question');
      else if (m.type === 'idle_prompt' || m.type === 'agent_needs_input') setState(sid, 'waiting');
      break;
    case 'Question': setState(sid, 'question'); break; // Claude предлагает варианты — ждёт выбора в окне
    case 'Limits': // лимиты и контекст — от строки состояния
      if (!loggedShape) { loggedShape = true; log('statusline fields', m.keys, 'rate_limits:', m.rate_limits ? Object.keys(m.rate_limits) : null, 'context_window:', m.context_window); }
      if (limits.update(m.rate_limits)) redraw();
      return;
    case 'PermissionRequest': {
      setState(sid, 'permission');
      const id = ++pendingSeq;
      pending.set(id, { socket, session: sid, tool: m.tool, text: m.text, at: Date.now() });
      socket.on('close', () => { if (pending.delete(id)) redraw(); }); // хук не дождался (15 с) — решает окно Claude
      log('permission', m.tool, m.text);
      break;
    }
  }
  redraw();
}

function dropPendingOf(sid) {
  for (const [id, p] of pending) if (p.session === sid) { try { p.socket.destroy(); } catch {} pending.delete(id); }
}

function oldestPending() {
  let best = null;
  for (const [id, p] of pending) if (!best || p.at < best.p.at) best = { id, p };
  return best;
}

function answer(item, decision) {
  pending.delete(item.id);
  try { item.p.socket.write(JSON.stringify({ decision }) + '\n'); } catch {}
  setState(item.p.session, decision === 'allow' ? 'working' : 'thinking');
  log('answer', decision, item.p.tool);
  redraw();
}

// канал для хуков
function startHookServer() {
  const name = '\\\\.\\pipe\\Qural.Claude.' + user;
  const server = net.createServer(sock => {
    sock.setEncoding('utf8');
    let buf = '', got = false;
    sock.on('data', c => {
      if (got) return;
      buf += c;
      const i = buf.indexOf('\n');
      if (i < 0) { if (buf.length > 64 * 1024) sock.destroy(); return; }
      got = true;
      let m = null;
      try { m = JSON.parse(buf.slice(0, i)); } catch {}
      if (!m || typeof m.event !== 'string') { sock.destroy(); return; }
      onHook(m, sock);
      if (m.event !== 'PermissionRequest') sock.end();
    });
    sock.on('error', () => {});
  });
  server.on('error', e => {
    log('hook server error', e.message);
    setTimeout(startHookServer, 3000); // канал занят (старый плагин ещё не закрылся) — пробуем снова
  });
  server.listen(name, () => log('hooks: listening'));
}

// ════════════ Qural (клавиши в окно Claude) ════════════
const quralPipe = '\\\\.\\pipe\\Qural.Deck.' + user;
let qural = null, quralOk = false, qbuf = '', qretry = null;

function connectQural() {
  qretry = null;
  const p = net.connect(quralPipe);
  qural = p;
  p.setEncoding('utf8');
  p.on('connect', () => { quralOk = true; qbuf = ''; log('qural: connected'); });
  p.on('data', chunk => {
    qbuf += chunk;
    let i;
    while ((i = qbuf.indexOf('\n')) >= 0) {
      const line = qbuf.slice(0, i); qbuf = qbuf.slice(i + 1);
      let m; try { m = JSON.parse(line); } catch { continue; }
      if (m.type === 'error' && m.id) { const a = actions.get(m.id); if (a) alert(a); log('qural error', m.text); }
      if (m.type === 'mic') { const on = !!m.active; if (on !== micOn) { micOn = on; redraw(); } }
    }
  });
  const down = () => {
    if (qural !== p) return;
    quralOk = false; qural = null;
    if (!qretry) qretry = setTimeout(connectQural, 3000);
  };
  p.on('error', down);
  p.on('close', down);
}

// раз в секунду спрашиваем Qural про микрофон — только пока на деке видна кнопка диктовки
let micOn = false;
setInterval(() => {
  for (const a of actions.values()) if (a.kind === 'dictation' && a.active) { toQural({ cmd: 'mic', id: '', app: 'Claude' }); return; }
}, 1000).unref();

function toQural(obj) {
  if (!qural || !quralOk) return false;
  qural.write(JSON.stringify(obj) + '\n');
  return true;
}

log('start', process.argv.slice(2).join(' '));
startHookServer();
connectHost();
connectQural();
