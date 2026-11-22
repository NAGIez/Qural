// Строка состояния Claude Code: показывает лимиты плана и передаёт их плагину «Qural — Claude» на Ulanzi Deck.
// Claude Code передаёт на stdin JSON, где есть rate_limits.five_hour / seven_day (used_percentage, resets_at).
// В плагин уходят только эти цифры; ключи и тексты разговоров не трогаются.
'use strict';
const net = require('net');
const os = require('os');

const PIPE = '\\\\.\\pipe\\Qural.Claude.' + (process.env.USERNAME || os.userInfo().username);
if (process.env.QURAL_DECK_SKIP) process.exit(0);
let input = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', c => { input += c; });
process.stdin.on('end', run);
setTimeout(() => process.exit(0), 1500).unref();

function run() {
  let d = {};
  try { d = JSON.parse(input); } catch {}
  const rl = d.rate_limits || null;
  const pct = w => (w && typeof w.used_percentage === 'number') ? Math.round(w.used_percentage) + '%' : null;
  const parts = [];
  if (rl && pct(rl.five_hour)) parts.push('5ч ' + pct(rl.five_hour));
  if (rl && pct(rl.seven_day)) parts.push('неделя ' + pct(rl.seven_day));
  process.stdout.write(parts.length ? 'лимиты: ' + parts.join(' · ') : '');
  if (!rl && !d.context_window) return process.exit(0);
  // весь rate_limits (вдруг там есть и лимиты по модели) и заполненность контекста этой сессии
  const msg = { event: 'Limits', session: String(d.session_id || ''), rate_limits: rl, context_window: d.context_window || null,
    keys: Object.keys(d) };
  const s = net.connect(PIPE);
  s.on('error', () => process.exit(0));
  s.on('close', () => process.exit(0));
  s.on('connect', () => { s.write(JSON.stringify(msg) + '\n'); s.end(); });
}
