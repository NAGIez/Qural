// Подключить или убрать хуки «Qural — Claude» в ~/.claude/settings.json.
//   node setup.cjs install  — добавить свои хуки (и убрать хуки старого AgentDeck); копия файла — settings.json.bak-<дата>
//   node setup.cjs remove   — убрать свои хуки
// Остальные настройки и чужие хуки не трогаются.
'use strict';
const fs = require('fs');
const path = require('path');
const os = require('os');

const mode = process.argv[2] || 'install';
const file = path.join(os.homedir(), '.claude', 'settings.json');
const hook = path.join(__dirname, 'hook.cjs').replace(/\\/g, '/');
const statusline = path.join(__dirname, 'statusline.cjs').replace(/\\/g, '/');
const MARK = 'qural.claude.ulanziPlugin';
const isOurs = h => h && typeof h.command === 'string' && h.command.includes(MARK);
const isAgentDeck = h => h && typeof h.command === 'string' && /agentdeck-hook\.ps1/i.test(h.command);

// события: что слушаем; PermissionRequest ждёт ответа деки, остальные — в фоне, Claude их не ждёт
const EVENTS = ['SessionStart', 'SessionEnd', 'UserPromptSubmit', 'PreToolUse', 'PostToolUse', 'PostToolUseFailure',
  'Stop', 'StopFailure', 'Notification', 'PermissionRequest'];
const TOOL_EVENTS = new Set(['PreToolUse', 'PostToolUse', 'PostToolUseFailure', 'PermissionRequest']);

let text = '{}';
try { text = fs.readFileSync(file, 'utf8'); } catch {}
let cfg;
try { cfg = JSON.parse(text.replace(/^﻿/, '')); } catch (e) { console.error('settings.json не разобрать: ' + e.message); process.exit(1); }

if (text.trim() !== '{}') {
  const stamp = new Date().toISOString().replace(/[:.]/g, '-');
  fs.writeFileSync(file + '.bak-' + stamp, text);
}

cfg.hooks = cfg.hooks || {};
let removedAgentDeck = 0;
for (const ev of Object.keys(cfg.hooks)) {
  const groups = Array.isArray(cfg.hooks[ev]) ? cfg.hooks[ev] : [];
  const kept = [];
  for (const g of groups) {
    const hs = (g.hooks || []).filter(h => {
      if (isOurs(h)) return false;
      if (mode === 'install' && isAgentDeck(h)) { removedAgentDeck++; return false; }
      return true;
    });
    if (hs.length) kept.push({ ...g, hooks: hs });
  }
  if (kept.length) cfg.hooks[ev] = kept; else delete cfg.hooks[ev];
}

if (mode === 'install') {
  for (const ev of EVENTS) {
    const h = { type: 'command', command: `node "${hook}" ${ev}` };
    if (ev === 'PermissionRequest') h.timeout = 30;
    else h.async = true;
    const group = { hooks: [h] };
    if (TOOL_EVENTS.has(ev)) group.matcher = '*';
    (cfg.hooks[ev] = cfg.hooks[ev] || []).push(group);
  }
}
if (!Object.keys(cfg.hooks).length) delete cfg.hooks;

// строка состояния: через неё Claude Code отдаёт лимиты плана. Чужую строку состояния не трогаем.
const slOurs = cfg.statusLine && typeof cfg.statusLine.command === 'string' && cfg.statusLine.command.includes(MARK);
let slNote = '';
if (mode === 'install') {
  if (!cfg.statusLine || slOurs) cfg.statusLine = { type: 'command', command: `node "${statusline}"`, padding: 0 };
  else slNote = ' (строка состояния уже своя — лимиты на деку приходить не будут)';
} else if (slOurs) delete cfg.statusLine;

fs.writeFileSync(file, JSON.stringify(cfg, null, 2) + '\n');
console.log(mode === 'install'
  ? `Хуки Qural — Claude подключены (${EVENTS.length} событий)` + (removedAgentDeck ? `, хуков AgentDeck убрано: ${removedAgentDeck}` : '') + slNote
  : 'Хуки Qural — Claude убраны');
