// Qural — громкость программ на кнопках и крутилках Ulanzi Deck.
// Ulanzi Studio запускает этот файл своим Node: app.js <адрес> <порт> <язык> <версия>.
// Громкость, значки и картинки берёт Qural (канал \\.\pipe\Qural.Deck.<пользователь>); плагин только передаёт их деке.
import WebSocket from 'ws';
import net from 'node:net';
import os from 'node:os';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { statsPng, statsTitle } from './stats.js';

const MAIN = 'com.qural.deck.volume';
const COLOR_ACTION = 'com.qural.deck.volume.color'; // кнопка профиля цвета
const MEDIA_ACTION = 'com.qural.deck.volume.media'; // «Сейчас играет»; остальные — громкость
const isColor = a => a.uuid === COLOR_ACTION;
const isMedia = a => a.uuid === MEDIA_ACTION;
const STATS_ACTION = 'com.qural.deck.volume.stats'; // «Нагрузка ПК»: цифры от Qural, картинку рисует плагин
const isStats = a => a.uuid === STATS_ACTION;
const SHOT_ACTION = 'com.qural.deck.volume.shot'; // «Скриншот»: картинка из manifest, нажатие — команда Qural
const isShot = a => a.uuid === SHOT_ACTION;
const REC_ACTION = 'com.qural.deck.volume.rec';   // «Запись экрана»: начать / остановить или пауза
const isRec = a => a.uuid === REC_ACTION;
const isStatic = a => isShot(a) || isRec(a);     // картинка из manifest
const HOLD_REC = 600; // мс — «Скриншот» дольше этого: запись экрана
function shot(ctx, s) { if (pipeOk) toQural({ cmd: 'shot', id: ctx, value: s.mode }); else alert(ctx); }
const SKIP_GAP = 350; // мс — один щелчок крутилки = один трек, даже если крутить быстро
const [address = '127.0.0.1', port = '3906'] = process.argv.slice(2);
const pluginDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');

// ── журнал (stdout плагина Ulanzi никуда не пишет) ──
const logFile = path.join(pluginDir, 'plugin.log');
try { if (fs.statSync(logFile).size > 512 * 1024) fs.truncateSync(logFile, 0); } catch {}
function log(...a) {
  try { fs.appendFileSync(logFile, new Date().toISOString() + ' ' + a.map(x => typeof x === 'string' ? x : JSON.stringify(x)).join(' ') + '\n'); } catch {}
}
process.on('uncaughtException', e => log('ERR', e && e.stack || String(e)));
process.on('unhandledRejection', e => log('ERR', e && e.stack || String(e)));

// ── кнопки: context → { uuid, key, actionid, controller, settings, active } ──
const actions = new Map();
const ctxOf = j => j.uuid + '___' + j.key + '___' + j.actionid;

function settingsOf(a) {
  const s = a.settings || {};
  if (isColor(a)) return { profile: (s.profile || 'next').trim() || 'next' };
  if (isShot(a)) return {
    mode: ['region', 'full', 'window'].includes(s.mode) ? s.mode : 'region',
    hold: ['screen', 'region', 'window', 'off'].includes(s.hold) ? s.hold : 'screen',   // долгое нажатие — запись экрана
  };
  if (isRec(a)) return { mode: ['region', 'screen', 'window'].includes(s.mode) ? s.mode : 'screen', press: s.press === 'pause' ? 'pause' : 'toggle' };
  return {
    app: (s.app || 'active').trim() || 'active',
    press: s.press || 'mute',          // кнопка: mute | up | down
    step: parseInt(s.step, 10) || 0,   // 0 — шаг из Qural
  };
}

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
  ws.on('close', () => { log('host: closed'); process.exit(0); }); // Ulanzi закрылась или перезапускает плагин
  ws.on('error', e => log('host error', e.message));
}

function sendHost(obj) {
  if (ws && ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(obj));
}

function onHost(d) {
  const cmd = d.cmd;
  if (cmd === 'clear') {
    for (const p of d.param || []) actions.delete(ctxOf(p));
    watch();
    return;
  }
  if (!d.uuid || d.key === undefined) return;
  const ctx = ctxOf(d);
  let a = actions.get(ctx);

  if (cmd === 'add') {
    a = { uuid: d.uuid, key: d.key, actionid: d.actionid, controller: d.controller || (a && a.controller) || 'Keypad', settings: d.param || {}, active: true };
    actions.set(ctx, a);
    watch(true);
    return;
  }
  if (!a) {
    // Ulanzi иногда шлёт события раньше add (например, после перезапуска плагина) — заводим кнопку сами
    if (!['paramfromapp', 'paramfromplugin', 'didReceiveSettings', 'keydown', 'dialdown', 'dialup', 'dialrotate', 'setactive', 'sendToPlugin'].includes(cmd)) return;
    a = { uuid: d.uuid, key: d.key, actionid: d.actionid, controller: d.controller || (/_3$/.test(d.key) ? 'Encoder' : 'Keypad'), settings: d.param || {}, active: true };
    actions.set(ctx, a);
  }
  if (d.controller) a.controller = d.controller;

  switch (cmd) {
    case 'paramfromapp':
    case 'paramfromplugin':
      if (d.param) { a.settings = d.param; watch(true); }
      break;
    case 'didReceiveSettings':
      if (d.settings) { a.settings = d.settings; watch(true); }
      break;
    case 'setactive':
      a.active = d.active !== false;
      a.offline = false; // страницу показали снова — картинку надо прислать заново
      watch(a.active);
      break;
    case 'keydown': {
      if (d.param && Object.keys(d.param).length) a.settings = d.param;
      const s = settingsOf(a);
      if (isColor(a)) { color(ctx, s.profile); break; }
      if (isShot(a)) {
        // короткое нажатие — скриншот (по отпусканию), долгое — начать / остановить запись экрана
        if (s.hold === 'off') { shot(ctx, s); break; }
        a.down = true; a.holdFired = false;
        clearTimeout(a.holdTimer);
        a.holdTimer = setTimeout(() => {
          a.holdFired = true;
          log('shot hold → rec', s.hold);
          if (pipeOk) toQural({ cmd: 'rec', id: ctx, value: 'toggle', mode: s.hold }); else alert(ctx);
        }, HOLD_REC);
        break;
      }
      if (isRec(a)) { log('rec key', s.press, s.mode); if (pipeOk) toQural({ cmd: 'rec', id: ctx, value: s.press, mode: s.mode }); else alert(ctx); break; }
      if (isMedia(a)) { holdLike(ctx, a); break; }   // нажатия считаем по отпусканию; долгое — лайк
      if (isStats(a)) break;   // «Нагрузка ПК» только показывает — нажатие ничего не делает
      volume(ctx, s.app, s.press === 'up' ? (s.step ? '+' + s.step : 'up') : s.press === 'down' ? (s.step ? '-' + s.step : 'down') : 'mute');
      break;
    }
    case 'keyup':
      if (isShot(a)) {
        clearTimeout(a.holdTimer);
        if (a.down && !a.holdFired) shot(ctx, settingsOf(a));
        a.down = false; a.holdFired = false;
        break;
      }
      if (isMedia(a)) { clearTimeout(a.likeTimer); if (!a.likeFired && a.down) clicks(ctx, a); a.likeFired = false; a.down = false; }
      break;
    case 'dialdown':
      // «без звука» — только по отпусканию и если крутилку не крутили зажатой (тогда это был крупный шаг)
      a.pressed = true; a.rotated = false;
      if (isMedia(a)) holdLike(ctx, a);
      break;
    case 'dialup': {
      const s = settingsOf(a);
      clearTimeout(a.likeTimer);
      if (a.pressed && !a.rotated && !a.likeFired) { if (isColor(a)) color(ctx, s.profile); else if (isMedia(a)) clicks(ctx, a); else if (!isStats(a)) volume(ctx, s.app, 'mute'); }
      a.pressed = false; a.likeFired = false; a.down = false;
      break;
    }
    case 'dialrotate': {
      a.rotated = true;
      clearTimeout(a.likeTimer);   // крутят зажатой — это не лайк
      if (d.param && Object.keys(d.param).length) a.settings = d.param;
      const s = settingsOf(a);
      const right = String(d.rotateEvent || '').includes('right');
      if (isColor(a)) { color(ctx, right ? 'next' : 'prev'); break; }
      if (isStats(a)) break;
      const big = String(d.rotateEvent || '').startsWith('hold');
      // «Сейчас играет»: поворот — следующий / предыдущий трек
      if (isMedia(a)) { skip(ctx, a, '', right, ''); break; }
      // громкость с зажатой крутилкой — трек вперёд/назад у этой программы (Spotify, браузер…);
      // нет у неё плеера — как раньше, громкость крупным шагом
      if (big) { skip(ctx, a, s.app, right, s.step ? (right ? '+' : '-') + s.step * 2 : (right ? '+10' : '-10')); break; }
      const step = s.step || 0;
      volume(ctx, s.app, step ? (right ? '+' : '-') + step : (right ? 'up' : 'down'));
      break;
    }
    case 'sendToPlugin': {
      const p = d.payload || {};
      // окно настройки кнопки просит список программ
      if (p.request === 'apps') { piWaiting.add(ctx); toQural({ cmd: 'apps' }); }
      // окно настройки «Сейчас играет»: вход в Spotify (лайки) и его состояние
      if (p.request === 'spotify') { piSpotify.add(ctx); if (!toQural({ cmd: 'spotify-status' })) toSpotifyPI({ ready: false, note: 'Qural не запущен' }); }
      if (p.spotifyLogin) { piSpotify.add(ctx); if (!toQural({ cmd: 'spotify-login', clientId: String(p.spotifyLogin) })) toSpotifyPI({ ready: false, note: 'Qural не запущен' }); }
      break;
    }
  }
}

// «Нагрузка ПК»: картинка — только когда цифры поменялись
function drawStats(a, s) {
  const dial = a.controller === 'Encoder';
  const sig = [s.cpu, s.cpuTemp, s.gpu, s.gpuTemp, s.ram, s.ramGb].join('|');
  if (a.statsSig === sig && !a.offline) return;
  a.statsSig = sig; a.offline = false;
  const data = 'data:image/png;base64,' + statsPng(s, dial ? 200 : 196).toString('base64');
  if (dial) sendHost({ cmd: 'setFeedback', uuid: a.uuid, key: a.key, actionid: a.actionid, layout: { icon: { value: data }, title: { text: statsTitle(s) } } });
  else sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
    param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, type: 1, data, textData: '', showtext: false }] } });
}

// «Сейчас играет»: держать ≥ 0,6 с — лайк / снять лайк в Spotify («Любимые треки»)
const LIKE_HOLD = 600;
function holdLike(ctx, a) {
  a.down = true; a.likeFired = false;
  clearTimeout(a.likeTimer);
  a.likeTimer = setTimeout(() => { a.likeFired = true; clearTimeout(a.clickTimer); a.clicks = 0; media(ctx, '', 'like'); }, LIKE_HOLD);
}

const piSpotify = new Set();
function toSpotifyPI(payload) {
  for (const ctx of piSpotify) {
    const a = actions.get(ctx);
    if (a) sendHost({ cmd: 'sendToPropertyInspector', uuid: a.uuid, key: a.key, actionid: a.actionid, payload: { spotify: payload } });
  }
}

// «Сейчас играет»: 1 нажатие — пауза, 2 — следующий трек, 3 — предыдущий (ждём CLICK_GAP после последнего нажатия)
const CLICK_GAP = 330;
function clicks(ctx, a) {
  a.clicks = (a.clicks || 0) + 1;
  clearTimeout(a.clickTimer);
  if (a.clicks >= 3) { a.clicks = 0; media(ctx, '', 'prev'); return; }
  a.clickTimer = setTimeout(() => { const n = a.clicks; a.clicks = 0; media(ctx, '', n === 2 ? 'next' : 'playpause'); }, CLICK_GAP);
}

// анимация (GIF) — файлом в папке cache: Ulanzi показывает GIF на кнопке только по пути к файлу
const cacheDir = path.join(pluginDir, 'cache');
function gifFile(b64) {
  const buf = Buffer.from(b64, 'base64');
  let h = 0; for (let i = 0; i < buf.length; i += 7) h = (h * 31 + buf[i]) >>> 0;
  const file = path.join(cacheDir, 'media_' + h.toString(16) + '_' + buf.length + '.gif');
  try {
    fs.mkdirSync(cacheDir, { recursive: true });
    if (!fs.existsSync(file)) {
      fs.writeFileSync(file, buf);
      // старые файлы — не больше 16
      const old = fs.readdirSync(cacheDir).filter(n => n.startsWith('media_')).map(n => ({ n, t: fs.statSync(path.join(cacheDir, n)).mtimeMs })).sort((x, y) => y.t - x.t);
      for (const o of old.slice(16)) try { fs.unlinkSync(path.join(cacheDir, o.n)); } catch {}
    }
  } catch (e) { log('ERR gif cache', e.message); return null; }
  return file.split(path.sep).join('/');
}

// проверка анимаций: файл test-media.txt в папке плагина с путём к GIF — кнопки «Сейчас играет» показывают его
const testMedia = path.join(pluginDir, 'test-media.txt');
let testGif = '';
setInterval(() => {
  let s = ''; try { s = fs.readFileSync(testMedia, 'utf8').trim(); } catch {}
  if (s && !fs.existsSync(s)) s = '';
  if (s === testGif) return;
  testGif = s; log('test media: ' + (s || 'выкл'));
  for (const a of actions.values()) {
    if (!isMedia(a) || !a.active || a.controller === 'Encoder') continue;
    if (s) sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
      param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, type: 4, gifpath: s.split(path.sep).join('/'), textData: '', showtext: false }] } });
  }
  if (!s) watch(true);   // вернуть настоящую картинку
}, 500).unref();

function setImage(a, png, title, value, gif) {
  if (testGif && isMedia(a) && a.controller !== 'Encoder') return;
  a.offline = false;
  if (a.controller === 'Encoder') {
    if (!png) return;
    sendHost({ cmd: 'setFeedback', uuid: a.uuid, key: a.key, actionid: a.actionid,
      layout: { icon: { value: 'data:image/png;base64,' + png }, title: { text: isColor(a) ? '' : (title + ' ' + value).trim() } } });
    return;
  }
  const file = gif ? gifFile(gif) : null;
  const item = file ? { type: 4, gifpath: file } : png ? { type: 1, data: 'data:image/png;base64,' + png } : null;
  if (!item) return;
  sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
    param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, ...item, textData: '', showtext: false }] } });
}

// Qural не запущен — обычная картинка из manifest
// Qural не отвечает (не запущен, закрылся) — логотип Qural и жёлтый «!» (рисует tools\make-qural-offline.ps1)
const OFFLINE_KEY = (() => { try { return 'data:image/png;base64,' + fs.readFileSync(path.join(pluginDir, 'assets', 'qural_offline.png')).toString('base64'); } catch { return ''; } })();
const OFFLINE_DIAL = (() => { try { return 'data:image/png;base64,' + fs.readFileSync(path.join(pluginDir, 'assets', 'qural_offline_dial.png')).toString('base64'); } catch { return ''; } })();
function setOffline(a) {
  if (a.offline) return;
  a.offline = true;
  if (isStatic(a)) a.needDefault = true;
  if (a.controller === 'Encoder') {
    sendHost({ cmd: 'setFeedback', uuid: a.uuid, key: a.key, actionid: a.actionid,
      layout: { icon: { value: OFFLINE_DIAL || path.join(pluginDir, 'assets', 'action.png').replace(/\\/g, '/') }, title: { text: 'Qural не отвечает' } } });
  } else {
    sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
      param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, ...(OFFLINE_KEY ? { type: 1, data: OFFLINE_KEY } : { type: 0, state: 0 }), textData: '', showtext: false }] } });
  }
}

// значок из manifest (у «Скриншота» и «Записи экрана» — когда ничего не идёт)
function setDefault(a) {
  a.offline = false;
  sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
    param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, type: 0, state: 0, textData: '', showtext: false }] } });
}

function alert(ctx) {
  const a = actions.get(ctx);
  if (a) sendHost({ cmd: 'showAlert', uuid: a.uuid, key: a.key, actionid: a.actionid });
}

// ════════════ Qural ════════════
const pipeName = '\\\\.\\pipe\\Qural.Deck.' + (process.env.USERNAME || os.userInfo().username);
let pipe = null, pipeOk = false, buf = '', retry = null;
const piWaiting = new Set();

function connectQural() {
  retry = null;
  const p = net.connect(pipeName);
  pipe = p;
  p.setEncoding('utf8');
  p.on('connect', () => {
    pipeOk = true; buf = '';
    for (const a of actions.values()) a.offline = false;
    log('qural: connected');
    toQural({ cmd: 'hello' });
    watch(true);
  });
  p.on('data', chunk => {
    buf += chunk;
    let i;
    while ((i = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, i); buf = buf.slice(i + 1);
      if (!line) continue;
      try { onQural(JSON.parse(line)); } catch (e) { log('ERR qural msg', e.message); }
    }
  });
  const down = () => {
    if (pipe !== p) return;
    if (pipeOk) { log('qural: disconnected'); for (const a of actions.values()) { a.offline = false; if (a.active) setOffline(a); } }
    pipeOk = false; pipe = null;
    if (!retry) retry = setTimeout(connectQural, 2000); // Qural перезапускают или он ещё не стартовал
  };
  p.on('error', down);
  p.on('close', down);
}

function toQural(obj) {
  if (!pipe || !pipeOk) return false;
  pipe.write(JSON.stringify(obj) + '\n');
  return true;
}

// сказать Qural, какие кнопки сейчас видно на деке
let watchTimer = null, lastWatch = '', pendingForce = false;
function watch(force) {
  pendingForce = pendingForce || !!force; // несколько вызовов подряд сливаются — «перерисовать всё» не теряется
  clearTimeout(watchTimer);
  watchTimer = setTimeout(() => {
    force = pendingForce; pendingForce = false;
    if (force) lastWatch = '';
    const tiles = [];
    for (const [ctx, a] of actions) {
      if (!a.active) continue;
      if (isStatic(a)) {
        // картинка своя (из manifest): после «Qural не отвечает» вернуть её.
        // Флаг отдельный: при подключении a.offline сбрасывается раньше, чем сюда доходит очередь
        if (pipeOk && a.needDefault) { a.needDefault = false; setDefault(a); }
        // Qural присылает анимацию, пока идёт скриншот, отсчёт или запись; у «Скриншота» app — что по долгому нажатию
        tiles.push({ id: ctx, app: isShot(a) ? settingsOf(a).hold : '', kind: isShot(a) ? 'shot-key' : 'rec-key' });
        continue;
      }
      const dial = a.controller === 'Encoder';
      if (isColor(a)) tiles.push({ id: ctx, app: settingsOf(a).profile, kind: dial ? 'color-dial' : 'color-key' });
      else if (isMedia(a)) tiles.push({ id: ctx, app: '', kind: dial ? 'media-dial' : 'media-key' });
      else if (isStats(a)) { a.statsSig = ''; tiles.push({ id: ctx, app: '', kind: dial ? 'stats-dial' : 'stats-key' }); }
      else tiles.push({ id: ctx, app: settingsOf(a).app, kind: dial ? 'dial' : 'key' });
    }
    const text = JSON.stringify(tiles);
    if (!pipeOk) {
      // Qural не запущен — новым и снова видимым кнопкам тоже показать «Qural?»
      for (const a of actions.values()) if (a.active) setOffline(a);
      if (!pipe && !retry) connectQural();
      return;
    }
    if (text === lastWatch && !force) return;
    lastWatch = text;
    if (force) toQural({ cmd: 'watch', tiles: [] }); // сбросить — Qural пришлёт все картинки заново
    toQural({ cmd: 'watch', tiles });
  }, 30);
}

function color(ctx, value) {
  if (!pipeOk) { alert(ctx); return; }
  toQural({ cmd: 'color', id: ctx, value });
}

// плееру: app '' — то, что сейчас играет; иначе плеер этой программы. orVolume — если у программы нет плеера
function media(ctx, app, value, orVolume) {
  if (!pipeOk) { alert(ctx); return; }
  toQural(orVolume ? { cmd: 'media', id: ctx, app, value, orVolume } : { cmd: 'media', id: ctx, app, value });
}

function skip(ctx, a, app, right, orVolume) {
  const now = Date.now();
  if (now - (a.skipAt || 0) < SKIP_GAP) return;
  a.skipAt = now;
  media(ctx, app, right ? 'next' : 'prev', orVolume);
}

function volume(ctx, app, value) {
  if (!pipeOk) { alert(ctx); return; }
  toQural({ cmd: 'volume', id: ctx, app, value });
}

function onQural(m) {
  if (m.type === 'tiles') {
    for (const t of m.tiles) {
      const a = actions.get(t.id);
      if (!a || !a.active) continue;
      if (t.reset) setDefault(a); else setImage(a, t.png, t.title, t.value, t.gif);
    }
  } else if (m.type === 'apps') {
    for (const ctx of piWaiting) {
      const a = actions.get(ctx);
      if (a) sendHost({ cmd: 'sendToPropertyInspector', uuid: a.uuid, key: a.key, actionid: a.actionid, payload: { apps: m.apps, soundOn: m.soundOn, profiles: m.profiles, activeProfile: m.activeProfile, colorOn: m.colorOn } });
    }
    piWaiting.clear();
  } else if (m.type === 'stats') {
    for (const a of actions.values()) if (isStats(a) && a.active) drawStats(a, m);
  } else if (m.type === 'spotify') {
    toSpotifyPI({ ready: !!m.ready, clientId: m.clientId || '', waiting: !!m.waiting, redirect: m.redirect || '', note: m.note || '' });
  } else if (m.type === 'error') {
    if (m.id) alert(m.id);
    log('qural error', m.text);
  }
}

log('start', process.argv.slice(2).join(' '));
connectHost();
connectQural();
