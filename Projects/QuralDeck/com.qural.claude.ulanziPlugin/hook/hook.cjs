// Хук Claude Code → плагин «Qural — Claude» на Ulanzi Deck (канал \\.\pipe\Qural.Claude.<пользователь>).
// Вызов: node hook.cjs <Событие>; данные события — JSON на stdin.
// Обычные события: передать и сразу выйти. PermissionRequest: подождать нажатия «Разрешить/Отклонить» на деке
// (до 15 с); не нажали или деки нет — ничего не выводить, Claude покажет обычный запрос.
// Плагин не запущен — выходим сразу, ничего не делая.
'use strict';
const net = require('net');
const os = require('os');

const PIPE = '\\\\.\\pipe\\Qural.Claude.' + (process.env.USERNAME || os.userInfo().username);
const WAIT_MS = 15000;
const ASK_TOOLS = new Set(['AskUserQuestion', 'ExitPlanMode']);
const CTX_EVENTS = new Set(['UserPromptSubmit', 'PostToolUse', 'Stop', 'SessionStart']);
const event = process.argv[2] || '';
if (process.env.QURAL_DECK_SKIP) process.exit(0); // служебный запуск claude -p самого плагина — на деке не показываем

let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', c => { input += c; });
process.stdin.on('end', run);
process.stdin.on('error', run);
setTimeout(() => process.exit(0), event === 'PermissionRequest' ? WAIT_MS + 3000 : 2000).unref();

let started = false;
function run() {
  if (started) return;
  started = true;
  let d = {};
  try { d = JSON.parse(input); } catch {}
  const ev = event || d.hook_event_name || '';
  const msg = {
    event: ev,
    session: String(d.session_id || ''),
    cwd: String(d.cwd || ''),
    tool: String(d.tool_name || ''),
    type: String(d.notification_type || ''),
    error: String(d.error || ''),
    text: summary(d),
  };
  // сколько контекста занято: последний ответ модели в журнале сессии (читаем только хвост файла)
  if (CTX_EVENTS.has(ev) && typeof d.transcript_path === 'string') {
    const c = contextFrom(d.transcript_path);
    if (c) msg.ctx = c;
  }
  let wait = ev === 'PermissionRequest';
  // вопрос пользователю и план — не разрешения: отвечать на них должен человек в окне Claude, деку не ждём
  if (wait && ASK_TOOLS.has(msg.tool)) { msg.event = 'Question'; wait = false; }

  let done = false, buf = '';
  const finish = out => {
    if (done) return;
    done = true;
    if (out) process.stdout.write(out);
    try { s.destroy(); } catch {}
    process.exit(0);
  };
  const s = net.connect(PIPE);
  s.setEncoding('utf8');
  s.on('error', () => finish());
  s.on('close', () => finish());
  s.on('connect', () => {
    s.write(JSON.stringify(msg) + '\n');
    if (!wait) { s.end(); return; }
    setTimeout(() => finish(), WAIT_MS);
  });
  s.on('data', c => {
    buf += c;
    const i = buf.indexOf('\n');
    if (i < 0) return;
    let r = {};
    try { r = JSON.parse(buf.slice(0, i)); } catch {}
    if (r.decision === 'allow')
      finish(JSON.stringify({ hookSpecificOutput: { hookEventName: 'PermissionRequest', decision: { behavior: 'allow' } } }));
    else if (r.decision === 'deny')
      finish(JSON.stringify({ hookSpecificOutput: { hookEventName: 'PermissionRequest', decision: { behavior: 'deny', message: 'Отклонено кнопкой на Ulanzi Deck' } } }));
    else finish();
  });
}

// { tokens, model } — из последней записи ответа с usage: вход + кэш (то, что сейчас в окне контекста)
function contextFrom(file) {
  try {
    const fs = require('fs');
    const size = fs.statSync(file).size, len = Math.min(size, 512 * 1024);
    const fd = fs.openSync(file, 'r'), buf = Buffer.alloc(len);
    fs.readSync(fd, buf, 0, len, size - len); fs.closeSync(fd);
    const lines = buf.toString('utf8').split('\n');
    for (let i = lines.length - 1; i >= 0; i--) {
      const l = lines[i];
      if (l.indexOf('"usage"') < 0 || l.indexOf('"assistant"') < 0) continue;
      let j; try { j = JSON.parse(l); } catch { continue; }
      const m = j.message || {}, u = m.usage;
      if (!u || j.isSidechain) continue;
      const tokens = (u.input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.cache_creation_input_tokens || 0);
      if (tokens > 0) return { tokens, model: String(m.model || '') };
    }
  } catch {}
  return null;
}

// Короткая подпись запроса: команда, файл или имя инструмента (уходит только в плагин на этом компьютере)
function summary(d) {
  const t = d.tool_input || {};
  let s = '';
  if (typeof t.command === 'string') s = t.command;
  else if (typeof t.file_path === 'string') s = t.file_path.split(/[\\/]/).pop();
  else if (typeof t.url === 'string') s = t.url;
  else if (typeof d.message === 'string') s = d.message;
  s = s.replace(/\s+/g, ' ').trim();
  return s.length > 80 ? s.slice(0, 79) + '…' : s;
}
