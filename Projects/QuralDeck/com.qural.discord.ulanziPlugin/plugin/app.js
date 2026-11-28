// Qural — Discord: статус голоса на кнопке Ulanzi Deck (обычный / микрофон заглушен / звук выключен).
// Нажатие — заглушить/вернуть микрофон, долгое нажатие — выключить/вернуть звук.
// Ulanzi Studio запускает этот файл своим Node: app.js <адрес> <порт> <язык> <версия>.
import WebSocket from 'ws';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { Discord } from './discord.js';
import { Speak } from './speak.js';
import { Page } from './page.js';

const MAIN = 'com.qural.deck.discord';
const SPEAK = 'com.qural.deck.discord.speaking';   // кнопка «Кто говорит»
const [address = '127.0.0.1', port = '3906'] = process.argv.slice(2);
const pluginDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const HOLD = 600; // мс — долгое нажатие

const logFile = path.join(pluginDir, 'plugin.log');
try { if (fs.statSync(logFile).size > 512 * 1024) fs.truncateSync(logFile, 0); } catch {}
function log(...a) {
  try { fs.appendFileSync(logFile, new Date().toISOString() + ' ' + a.map(x => typeof x === 'string' ? x : JSON.stringify(x)).join(' ') + '\n'); } catch {}
}
process.on('uncaughtException', e => log('ERR', e && e.stack || String(e)));
process.on('unhandledRejection', e => log('ERR', e && e.stack || String(e)));

// картинки состояний — один раз в base64
const IMG = {};
for (const s of ['normal', 'mute', 'deaf', 'offline', 'auth'])
  IMG[s] = 'data:image/png;base64,' + fs.readFileSync(path.join(pluginDir, 'assets', 'discord_' + s + '.png')).toString('base64');
const TITLE = { normal: 'Discord', mute: 'Микрофон выкл', deaf: 'Звук выкл', offline: 'Discord?', auth: 'Подключить', setup: 'Настроить' };

const actions = new Map(); // context → { uuid, key, actionid, controller, active, shown }
const ctxOf = j => j.uuid + '___' + j.key + '___' + j.actionid;
const page = new Page(pluginDir, 'discord', actions, null, fs.existsSync(path.join(pluginDir, 'debug-img')) ? (...m) => log(...m) : null);   // какие кнопки сейчас на экране (page.js)
const pis = new Set();     // кнопки, у которых открыто окно настройки

let cur = { state: 'setup', note: 'Нужны ID и секрет приложения' };
const discord = new Discord(st => { cur = st; log('discord: ' + st.state + (st.note ? ' — ' + st.note : '')); redraw(); toPI(); }, log);

// «Кто говорит»: события речи частые — перерисовываем не чаще раза в 120 мс
const speak = new Speak(() => drawSpeakSoon(), log);
let speakTimer = null, speakLater = null, lastChannel = '';
discord.onVoice = ch => {
  const name = ch ? ch.name + ' (' + ch.members.size + ')' : '—';
  if (name !== lastChannel) { lastChannel = name; log('discord: голосовой канал — ' + name); }
  drawSpeakSoon();
};
function drawSpeakSoon() {
  if (speakTimer) return;
  speakTimer = setTimeout(() => { speakTimer = null; for (const a of actions.values()) if (a.uuid === SPEAK) draw(a); }, 120);
}

// ════════════ Ulanzi Studio ════════════
let ws = null;
function connectHost() {
  ws = new WebSocket(`ws://${address}:${port}`);
  ws.on('open', () => { log('host: connected'); ws.send(JSON.stringify({ code: 0, cmd: 'connected', uuid: MAIN })); });
  ws.on('message', buf => {
    let d; try { d = JSON.parse(buf.toString()); } catch { return; }
    if (!d || (typeof d.code !== 'undefined' && d.cmdType !== 'REQUEST')) return;
    sendHost({ ...d, code: 0 });
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
  const cmd = d.cmd;
  if (cmd === 'clear') { for (const p of d.param || []) { actions.delete(ctxOf(p)); pis.delete(ctxOf(p)); } return; }
  if (!d.uuid || d.key === undefined) return;
  const ctx = ctxOf(d);
  let a = actions.get(ctx);
  if (!a) {
    a = { uuid: d.uuid, key: d.key, actionid: d.actionid, controller: d.controller || (/_3$/.test(d.key) ? 'Encoder' : 'Keypad'), active: false, shown: '' };
    actions.set(ctx, a);
  }
  if (d.controller) a.controller = d.controller;

  switch (cmd) {
    // add — кнопку поставили; paramfromapp/paramfromplugin — дека подключилась или загрузила страницу:
    // картинку надо прислать заново, иначе на самой деке кнопка чёрная (в приложении — старая картинка)
    case 'add':
      page.shown(a); a.shown = ''; draw(a);
      again(a);
      break;
    // параметры и настройки — видимость не меняют: их присылают и кнопкам другой страницы
    case 'paramfromapp':
    case 'paramfromplugin':
    case 'didReceiveSettings':
      if (a.active) { a.shown = ''; draw(a); again(a); }
      break;
    case 'setactive':
      if (d.active === false) { a.active = false; break; }
      page.shown(a); a.shown = ''; draw(a); again(a);
      break;
    case 'keydown':
    case 'dialdown':
      a.down = Date.now(); a.held = false;
      clearTimeout(a.holdTimer);
      a.holdTimer = setTimeout(() => { a.held = true; press(a, 'deaf'); }, HOLD);
      break;
    case 'keyup':
    case 'dialup':
      clearTimeout(a.holdTimer);
      if (a.down && !a.held) press(a, 'mute');
      a.down = 0;
      break;
    case 'dialrotate':
      // крутилка: вправо — вернуть звук и микрофон, влево — заглушить микрофон
      press(a, String(d.rotateEvent || '').includes('right') ? 'unmute' : 'mute-on');
      break;
    case 'sendToPlugin': {
      pis.add(ctx);
      const p = d.payload || {};
      if (p.app) discord.setApp(p.app.clientId, p.app.clientSecret);
      if (p.connect) discord.toggle('mute');
      toPI();
      break;
    }
  }
}

async function press(a, what) {
  if (cur.state === 'setup') { alert(a); return; }
  if (cur.state === 'offline') { alert(a); return; }
  if (what === 'unmute' || what === 'mute-on') {
    const v = discord.voice || {};
    if (what === 'unmute' && !v.mute && !v.deaf) return;
    if (what === 'mute-on' && (v.mute || v.deaf)) return;
    what = 'mute';
  }
  const ok = await discord.toggle(what);
  if (!ok && cur.state !== 'auth') alert(a);
}

function alert(a) { sendHost({ cmd: 'showAlert', uuid: a.uuid, key: a.key, actionid: a.actionid }); setTimeout(() => { a.shown = ''; draw(a); }, 1500); }

function drawSpeak(a) {
  const dial = a.controller === 'Encoder';
  const r = speak.render(dial ? 200 : 196, discord.channel);
  // говоривший замолчал — через долю секунды кнопка вернётся к каналу
  if (r.until) { clearTimeout(speakLater); speakLater = setTimeout(drawSpeakSoon, Math.max(30, r.until - Date.now() + 20)); }
  const sig = r.sig + (dial ? '|d' : '');
  if (a.shown === sig) return;
  a.shown = sig;
  // отладка: файл debug-speak в папке плагина — писать события речи в журнал и сохранять картинку кнопки
  discord.debug = fs.existsSync(path.join(pluginDir, 'debug-speak'));
  if (discord.debug) { try { fs.writeFileSync(path.join(pluginDir, 'speak_last' + (dial ? '_dial' : '') + '.png'), r.png); } catch {} log('speak: ' + r.sig + ' — ' + r.title); }
  const data = 'data:image/png;base64,' + r.png.toString('base64');
  if (dial) sendHost({ cmd: 'setFeedback', uuid: a.uuid, key: a.key, actionid: a.actionid, layout: { icon: { value: data }, title: { text: r.title } } });
  else sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
    param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, type: 1, data, textData: '', showtext: false }] } });
}

function draw(a) {
  if (!a.active) return;
  if (a.uuid === SPEAK) { drawSpeak(a); return; }
  const st = cur.state === 'setup' ? 'auth' : cur.state;
  const sig = st + '|' + cur.note;
  if (a.shown === sig) return;
  a.shown = sig;
  if (a.controller === 'Encoder') {
    sendHost({ cmd: 'setFeedback', uuid: a.uuid, key: a.key, actionid: a.actionid,
      layout: { icon: { value: IMG[st] }, title: { text: TITLE[cur.state] || 'Discord' } } });
  } else {
    sendHost({ cmd: 'state', uuid: a.uuid, key: a.key, actionid: a.actionid,
      param: { statelist: [{ uuid: a.uuid, key: a.key, actionid: a.actionid, type: 1, data: IMG[st], textData: '', showtext: false }] } });
  }
}
function redraw() { for (const a of actions.values()) draw(a); }

// кнопка только что появилась на экране (старт деки, смена страницы): дека иногда пропускает первую картинку,
// а в приложении при этом всё видно — присылаем ещё раз через 1,5 с
function again(a) {
  clearTimeout(a.again1); clearTimeout(a.again2);
  const re = () => { if (a.active) { a.shown = ''; draw(a); } };
  a.again1 = setTimeout(re, 1500);
}

// окну настройки — состояние и сохранён ли ID (секрет обратно не отдаём)
function toPI() {
  for (const ctx of pis) {
    const a = actions.get(ctx); if (!a) continue;
    sendHost({ cmd: 'sendToPropertyInspector', uuid: a.uuid, key: a.key, actionid: a.actionid,
      payload: { state: cur.state, note: cur.note, clientId: discord.cfg.clientId || '', hasSecret: !!discord.cfg.clientSecret } });
  }
}

log('start', address, port);
connectHost();
