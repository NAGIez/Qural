// Лимиты плана с сервера Anthropic (тот же неофициальный адрес, что у AgentDeck и у /usage в Claude Code).
// Ключ входа берётся из ~/.claude/.credentials.json (его ведёт Claude Code) и уходит только на api.anthropic.com.
// Ключ живёт около 8 часов; истёк — запускаем короткий «claude -p» на Haiku (≈ $0,003 по цене API),
// и Claude Code сам его обновляет. Не чаще раза в 30 минут. Ключ не сохраняется и не пишется в журнал.
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawn } from 'node:child_process';

const CREDS = path.join(os.homedir(), '.claude', '.credentials.json');
const URL = 'https://api.anthropic.com/api/oauth/usage';
const POLL_MS = 5 * 60e3, REFRESH_GAP_MS = 30 * 60e3;

export class Usage {
  constructor(onData, log) {
    this.onData = onData; this.log = log;
    this.timer = null; this.busy = false; this.lastRefresh = 0; this.enabled = false;
    this.state = ''; // для подписи: '', 'нет входа', 'ошибка сервера'
  }

  setEnabled(on) {
    if (on === this.enabled) return;
    this.enabled = on;
    clearInterval(this.timer); this.timer = null;
    if (on) { this.timer = setInterval(() => this.poll(), POLL_MS); this.timer.unref(); this.poll(); }
    this.log('usage: ' + (on ? 'включено' : 'выключено'));
  }

  token() {
    try {
      const o = JSON.parse(fs.readFileSync(CREDS, 'utf8')).claudeAiOauth;
      return o && o.accessToken ? { value: o.accessToken, expires: +o.expiresAt || 0 } : null;
    } catch { return null; }
  }

  // ключ истёк — пусть Claude Code обновит его сам: один короткий запрос к Haiku
  refreshToken() {
    if (Date.now() - this.lastRefresh < REFRESH_GAP_MS) return Promise.resolve(false);
    this.lastRefresh = Date.now();
    const cmd = path.join(process.env.APPDATA || '', 'npm', 'claude.cmd');
    if (!fs.existsSync(cmd)) { this.log('usage: claude.cmd не найден'); return Promise.resolve(false); }
    this.log('usage: обновляю ключ через claude -p');
    return new Promise(res => {
      // QURAL_DECK_SKIP — чтобы наши же хуки не показали этот запрос на деке как работу Claude
      const p = spawn(`"${cmd}"`, ['-p', '--model', 'haiku', 'ok'], {
        shell: true, windowsHide: true, cwd: os.tmpdir(), env: { ...process.env, QURAL_DECK_SKIP: '1' }, stdio: 'ignore' });
      const t = setTimeout(() => { try { p.kill(); } catch {} res(false); }, 90e3);
      p.on('exit', () => { clearTimeout(t); res(true); });
      p.on('error', () => { clearTimeout(t); res(false); });
    });
  }

  async poll() {
    if (!this.enabled || this.busy) return;
    this.busy = true;
    try {
      let t = this.token();
      if (!t) { this.state = 'нет входа в Claude Code'; return; }
      if (t.expires && t.expires - Date.now() < 2 * 60e3) { await this.refreshToken(); t = this.token(); }
      let r = await this.get(t.value);
      if (r.status === 401 && await this.refreshToken()) { t = this.token(); r = await this.get(t.value); }
      if (r.status !== 200 || !r.json) { this.state = 'сервер: ' + r.status; this.log('usage: HTTP ' + r.status); return; }
      this.state = '';
      this.onData(Usage.convert(r.json));
    } catch (e) {
      this.state = 'нет связи с сервером';
      this.log('usage: ' + e.message);
    } finally { this.busy = false; }
  }

  async get(token) {
    const res = await fetch(URL, { headers: { Authorization: 'Bearer ' + token, 'anthropic-beta': 'oauth-2025-04-20', 'User-Agent': 'qural-deck' },
      signal: AbortSignal.timeout(15e3) });
    let json = null;
    try { json = await res.json(); } catch {}
    return { status: res.status, json };
  }

  // ответ сервера → то же, что даёт строка состояния: five_hour / seven_day + лимиты по модели
  static convert(j) {
    const sec = s => s ? Math.floor(Date.parse(s) / 1000) : null;
    const rl = {};
    if (j.five_hour) rl.five_hour = { used_percentage: j.five_hour.utilization, resets_at: sec(j.five_hour.resets_at) };
    if (j.seven_day) rl.seven_day = { used_percentage: j.seven_day.utilization, resets_at: sec(j.seven_day.resets_at) };
    for (const l of Array.isArray(j.limits) ? j.limits : []) {
      if (l.kind !== 'weekly_scoped' || typeof l.percent !== 'number') continue;
      const name = l.scope && l.scope.model && l.scope.model.display_name || 'model';
      rl['scoped_' + name.toLowerCase()] = { used_percentage: l.percent, resets_at: sec(l.resets_at), label: name };
    }
    return rl;
  }
}
