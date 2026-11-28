// Связь с Discord через его локальный канал (RPC): \\.\pipe\discord-ipc-0…9.
// Кадр: код (int32 LE) + длина (int32 LE) + JSON. Коды: 0 — рукопожатие, 1 — кадр, 2 — закрыть, 3 — ping, 4 — pong.
// Порядок: рукопожатие с ID приложения → AUTHENTICATE сохранённым токеном (или AUTHORIZE — Discord спросит
// «Разрешить?», код меняем на токен через discord.com) → GET_VOICE_SETTINGS + подписка VOICE_SETTINGS_UPDATE.
// Токены и секрет лежат в %APPDATA%\Qural\deck-discord.json — только на этом компьютере; в журнал не пишутся.
import net from 'node:net';
import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import os from 'node:os';

const SCOPES = ['rpc', 'rpc.voice.read', 'rpc.voice.write'];
const API = 'https://discord.com/api/v10/oauth2/token';
// при автозапуске Windows UlanziDeck может стартовать плагин без APPDATA — берём путь от домашней папки
const ROAMING = process.env.APPDATA || path.join(process.env.USERPROFILE || os.homedir(), 'AppData', 'Roaming');
const CFG = path.join(ROAMING, 'Qural', 'deck-discord.json');

export class Discord {
  // onState({ state: 'normal'|'mute'|'deaf'|'offline'|'setup'|'auth', note })
  constructor(onState, log) {
    this.onState = onState; this.log = log;
    this.cfg = Discord.load(log);
    if (!this.hasApp()) log('discord: нет настроек в ' + CFG + (process.env.APPDATA ? '' : ' (APPDATA пуст)'));
    this.sock = null; this.buf = Buffer.alloc(0);
    this.ready = false; this.authed = false;
    this.wait = new Map();             // nonce → resolve
    this.voice = null;                 // { mute, deaf }
    this.state = ''; this.note = '';
    this.authorizing = false;
    // голосовой канал, где вы сейчас: { id, name, members: Map(userId → { id, name, avatar, speaking, at }) } или null
    this.channel = null;
    this.onVoice = null;               // вызывается при любом изменении канала, участников, «кто говорит»
    this.connect();
  }

  // ── голосовой канал: кто в нём и кто сейчас говорит ──
  async voiceInit() {
    const sel = await this.cmd('GET_SELECTED_VOICE_CHANNEL');
    if (sel.evt === 'ERROR') this.log('discord: канал — ' + (sel.data && sel.data.message));
    else await this.setChannel(sel.data);
    const sub = await this.cmd('SUBSCRIBE', {}, 'VOICE_CHANNEL_SELECT');
    if (sub.evt === 'ERROR') this.log('discord: подписка на канал — ' + (sub.data && sub.data.message));
  }

  static member(vs) {
    const u = (vs && vs.user) || {};
    return { id: String(u.id || ''), name: String((vs && vs.nick) || u.global_name || u.username || '?'), avatar: u.avatar || '', speaking: false, at: 0 };
  }

  async setChannel(ch) {
    const old = this.channel;
    if (old && (!ch || ch.id !== old.id))
      for (const evt of ['SPEAKING_START', 'SPEAKING_STOP', 'VOICE_STATE_CREATE', 'VOICE_STATE_UPDATE', 'VOICE_STATE_DELETE'])
        this.cmd('UNSUBSCRIBE', { channel_id: old.id }, evt).catch(() => {});
    if (!ch || !ch.id) { this.channel = null; this.voiceChanged(); return; }
    const members = new Map();
    for (const vs of ch.voice_states || []) { const m = Discord.member(vs); if (m.id) members.set(m.id, m); }
    this.channel = { id: String(ch.id), name: String(ch.name || ''), members };
    this.voiceChanged();
    if (!old || old.id !== this.channel.id)
      for (const evt of ['SPEAKING_START', 'SPEAKING_STOP', 'VOICE_STATE_CREATE', 'VOICE_STATE_UPDATE', 'VOICE_STATE_DELETE']) {
        const r = await this.cmd('SUBSCRIBE', { channel_id: this.channel.id }, evt);
        if (r.evt === 'ERROR') this.log('discord: подписка ' + evt + ' — ' + (r.data && r.data.message));
      }
  }

  voiceChanged() { if (this.onVoice) try { this.onVoice(this.channel); } catch (e) { this.log('discord: onVoice — ' + e.message); } }

  onVoiceEvent(evt, d) {
    if (evt === 'VOICE_CHANNEL_SELECT') {
      if (!d || !d.channel_id) { this.setChannel(null); return; }
      this.cmd('GET_CHANNEL', { channel_id: d.channel_id }).then(r => { if (r.evt !== 'ERROR') this.setChannel(r.data); else this.log('discord: канал — ' + (r.data && r.data.message)); });
      return;
    }
    const ch = this.channel; if (!ch || !d) return;
    if (evt === 'SPEAKING_START' || evt === 'SPEAKING_STOP') {
      const id = String(d.user_id || ''), m = ch.members.get(id);
      if (this.debug) this.log('discord: ' + evt + ' ' + (m ? m.name : 'неизвестный ' + id));
      if (!m) return;
      m.speaking = evt === 'SPEAKING_START'; m.at = Date.now();
    } else if (evt === 'VOICE_STATE_DELETE') {
      ch.members.delete(String((d.user && d.user.id) || ''));
    } else {
      const m = Discord.member(d);
      if (!m.id) return;
      const was = ch.members.get(m.id);
      if (was) { m.speaking = was.speaking; m.at = was.at; }
      ch.members.set(m.id, m);
    }
    this.voiceChanged();
  }

  static load(log) {
    if (!fs.existsSync(CFG)) Discord.migrate(log);
    try { return JSON.parse(fs.readFileSync(CFG, 'utf8')); }
    catch (e) { if (log && e.code !== 'ENOENT') log('discord: не прочитал настройки — ' + (e.code || e.name)); return {}; }
  }

  // Ulanzi или Qural, запущенные из приложения Claude, работают внутри его пакета, и новые файлы в %APPDATA%\Qural
  // попадают в его скрытую копию: %LOCALAPPDATA%\Packages\Claude_…\LocalCache\Roaming\Qural. После перезагрузки
  // Ulanzi стартует сама и этих файлов не видит. Нет настроек — ищем их там и переносим в настоящую папку
  // (заодно spotify.json Qural — лайки в Spotify). Только на этом компьютере; что уже есть — не трогаем.
  static migrate(log) {
    const pk = path.join(process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local'), 'Packages');
    let dirs = [];
    try { dirs = fs.readdirSync(pk).filter(d => /^Claude_/.test(d)).map(d => path.join(pk, d, 'LocalCache', 'Roaming', 'Qural')); } catch { return; }
    for (const name of ['deck-discord.json', 'spotify.json']) {
      const dst = path.join(path.dirname(CFG), name);
      if (fs.existsSync(dst)) continue;
      const src = dirs.map(d => path.join(d, name)).find(f => fs.existsSync(f));
      if (!src) continue;
      try {
        JSON.parse(fs.readFileSync(src, 'utf8'));   // целый ли
        fs.mkdirSync(path.dirname(dst), { recursive: true });
        fs.copyFileSync(src, dst, fs.constants.COPYFILE_EXCL);
        if (log) log('discord: перенёс ' + name + ' из копии AppData приложения Claude');
      } catch (e) { if (log) log('discord: не перенёс ' + name + ' — ' + (e.code || e.name)); }
    }
  }
  save() {
    try { fs.mkdirSync(path.dirname(CFG), { recursive: true }); fs.writeFileSync(CFG, JSON.stringify(this.cfg)); }
    catch (e) { this.log('discord: не могу сохранить настройки — ' + e.message); }
  }

  hasApp() { return !!(this.cfg.clientId && this.cfg.clientSecret); }

  // из окна настройки: новые ID и секрет — забыть старые токены и подключиться заново (Discord спросит разрешение)
  setApp(clientId, clientSecret) {
    clientId = String(clientId || '').trim(); clientSecret = String(clientSecret || '').trim();
    const same = clientId === this.cfg.clientId && (!clientSecret || clientSecret === this.cfg.clientSecret);
    if (same && this.authed) return;
    this.cfg = { clientId, clientSecret: clientSecret || this.cfg.clientSecret || '' };
    this.save();
    this.authorizeNext = true;
    this.reconnect();
  }

  set(state, note = '') {
    if (state === this.state && note === this.note) return;
    this.state = state; this.note = note;
    this.onState({ state, note });
  }

  reconnect() { this.authed = false; this.ready = false; try { this.sock && this.sock.destroy(); } catch {} this.sock = null; clearTimeout(this.retry); this.connect(); }

  connect(i = 0) {
    clearTimeout(this.retry);
    // настроек нет — перечитать файл (при загрузке Windows профиль может быть ещё не готов) и проверить позже
    if (!this.hasApp()) this.cfg = Discord.load(this.log);
    if (!this.hasApp()) { this.set('setup', 'Нужны ID и секрет приложения'); this.retry = setTimeout(() => this.connect(), 5000); return; }
    if (i > 9) { this.set('offline', 'Discord не запущен'); this.retry = setTimeout(() => this.connect(), 5000); return; }
    const s = net.connect('\\\\.\\pipe\\discord-ipc-' + i);
    let opened = false;
    s.once('connect', () => {
      opened = true; this.sock = s; this.buf = Buffer.alloc(0);
      this.send(0, { v: 1, client_id: this.cfg.clientId });
    });
    s.on('data', d => this.onData(d));
    s.on('error', () => { if (!opened) this.connect(i + 1); });
    s.on('close', () => {
      if (!opened || this.sock !== s) return;
      this.sock = null; this.ready = false; this.authed = false;
      if (this.channel) { this.channel = null; this.voiceChanged(); }
      for (const r of this.wait.values()) r({ evt: 'ERROR', data: { message: 'closed' } });
      this.wait.clear();
      const why = this.closeReason; this.closeReason = '';
      // неверный ID — ждать, пока введут другой (повторять бессмысленно)
      if (why && /client id/i.test(why)) { this.set('setup', 'Discord не знает такой Client ID'); return; }
      this.set('offline', 'Discord не запущен');
      this.retry = setTimeout(() => this.connect(), 5000);
    });
  }

  send(op, obj) {
    if (!this.sock) return;
    const body = Buffer.from(JSON.stringify(obj), 'utf8'), head = Buffer.alloc(8);
    head.writeInt32LE(op, 0); head.writeInt32LE(body.length, 4);
    this.sock.write(Buffer.concat([head, body]));
  }

  // команда RPC → ответ (или { evt:'ERROR' } через 60 с — окно «Разрешить?» может висеть долго)
  cmd(cmd, args = {}, evt) {
    const nonce = crypto.randomUUID();
    return new Promise(res => {
      const t = setTimeout(() => { this.wait.delete(nonce); res({ evt: 'ERROR', data: { message: 'timeout' } }); }, 60000);
      this.wait.set(nonce, m => { clearTimeout(t); res(m); });
      this.send(1, evt ? { cmd, args, evt, nonce } : { cmd, args, nonce });
    });
  }

  onData(d) {
    this.buf = Buffer.concat([this.buf, d]);
    while (this.buf.length >= 8) {
      const op = this.buf.readInt32LE(0), len = this.buf.readInt32LE(4);
      if (this.buf.length < 8 + len) break;
      let m = null; try { m = JSON.parse(this.buf.subarray(8, 8 + len).toString('utf8')); } catch {}
      this.buf = this.buf.subarray(8 + len);
      if (op === 3) { this.send(4, m); continue; }
      if (op === 2) { this.log('discord: закрыл связь — ' + (m && m.message)); this.closeReason = m && m.message; continue; }
      if (m) this.onMessage(m);
    }
  }

  onMessage(m) {
    if (m.nonce && this.wait.has(m.nonce)) { const r = this.wait.get(m.nonce); this.wait.delete(m.nonce); r(m); return; }
    if (m.cmd === 'DISPATCH' && m.evt === 'READY') { this.ready = true; this.login().catch(e => this.log('discord: ' + e.message)); return; }
    if (m.cmd === 'DISPATCH' && m.evt === 'VOICE_SETTINGS_UPDATE') { this.apply(m.data); return; }
    if (m.cmd === 'DISPATCH' && /^(VOICE_CHANNEL_SELECT|SPEAKING_START|SPEAKING_STOP|VOICE_STATE_CREATE|VOICE_STATE_UPDATE|VOICE_STATE_DELETE)$/.test(m.evt)) { this.onVoiceEvent(m.evt, m.data); return; }
  }

  apply(v) {
    if (!v) return;
    this.voice = { mute: !!v.mute, deaf: !!v.deaf };
    this.set(this.voice.deaf ? 'deaf' : this.voice.mute ? 'mute' : 'normal');
  }

  async login() {
    let ok = false;
    if (this.cfg.accessToken) {
      if (this.cfg.expiresAt && Date.now() > this.cfg.expiresAt - 3600e3 && this.cfg.refreshToken) await this.token({ grant_type: 'refresh_token', refresh_token: this.cfg.refreshToken });
      ok = await this.authenticate();
      if (!ok && this.cfg.refreshToken && await this.token({ grant_type: 'refresh_token', refresh_token: this.cfg.refreshToken })) ok = await this.authenticate();
    }
    if (!ok) {
      // окно «Разрешить?» в Discord — только когда человек сам попросил (сохранил ID/секрет или нажал кнопку)
      if (!this.authorizeNext) { this.set('auth', 'Нажмите кнопку — Discord спросит разрешение'); return; }
      this.authorizeNext = false;
      ok = await this.authorize();
      if (!ok) return;
    }
    this.authed = true;
    const v = await this.cmd('GET_VOICE_SETTINGS');
    if (v.evt === 'ERROR') { this.log('discord: голос — ' + (v.data && v.data.message)); this.set('auth', 'Нет доступа к голосу'); return; }
    this.apply(v.data);
    const sub = await this.cmd('SUBSCRIBE', {}, 'VOICE_SETTINGS_UPDATE');
    if (sub.evt === 'ERROR') this.log('discord: подписка — ' + (sub.data && sub.data.message));
    this.log('discord: подключён');
    await this.voiceInit();
  }

  async authenticate() {
    const r = await this.cmd('AUTHENTICATE', { access_token: this.cfg.accessToken });
    if (r.evt === 'ERROR') { this.log('discord: токен не подошёл — ' + (r.data && r.data.message)); return false; }
    return true;
  }

  async authorize() {
    this.set('auth', 'Подтвердите в окне Discord');
    this.authorizing = true;
    const r = await this.cmd('AUTHORIZE', { client_id: this.cfg.clientId, scopes: SCOPES });
    this.authorizing = false;
    if (r.evt === 'ERROR' || !r.data || !r.data.code) {
      const msg = (r.data && r.data.message) || 'нет ответа';
      this.log('discord: разрешение — ' + msg);
      // права на голос Discord даёт только владельцу/тестерам приложения (App Testers) или одобренным приложениям
      this.set('auth', /invalid_scope/.test(msg) ? 'Discord не дал права на голос — добавьте себя в App Testers приложения' : 'Discord не дал разрешение: ' + msg);
      return false;
    }
    if (!await this.token({ grant_type: 'authorization_code', code: r.data.code })) return false;
    return this.authenticate();
  }

  // обмен кода/обновление токена на discord.com (секрет уходит только туда)
  async token(params) {
    const tries = params.grant_type === 'authorization_code' ? [null, 'http://localhost'] : [null];
    for (const redirect of tries) {
      const body = new URLSearchParams({ client_id: this.cfg.clientId, client_secret: this.cfg.clientSecret, ...params });
      if (redirect) body.set('redirect_uri', redirect);
      try {
        const res = await fetch(API, { method: 'POST', body, headers: { 'Content-Type': 'application/x-www-form-urlencoded' } });
        const j = await res.json().catch(() => ({}));
        if (res.ok && j.access_token) {
          this.cfg.accessToken = j.access_token; this.cfg.refreshToken = j.refresh_token || this.cfg.refreshToken;
          this.cfg.expiresAt = Date.now() + (j.expires_in || 604800) * 1000;
          this.save();
          return true;
        }
        const err = (j.error || res.status) + (j.error_description ? ': ' + j.error_description : '');
        this.log('discord: токен — ' + err);
        if (!/redirect/i.test(err)) { this.set('auth', 'Не получил токен: ' + err); return false; }
        this.lastErr = err;
      } catch (e) { this.log('discord: токен — ' + e.message); this.set('auth', 'Нет связи с discord.com'); return false; }
    }
    this.set('auth', 'Добавьте http://localhost в Redirects приложения');
    return false;
  }

  // нажатие: «не подключён» — попросить разрешение; иначе переключить микрофон или звук
  async toggle(what) {
    if (!this.hasApp()) return false;
    if (!this.authed) {
      if (this.ready && !this.authorizing) { this.authorizeNext = true; this.login().catch(e => this.log('discord: ' + e.message)); }
      else if (!this.sock) this.connect();
      return false;
    }
    const v = this.voice || { mute: false, deaf: false };
    const args = what === 'deaf' ? { deaf: !v.deaf } : (v.deaf ? { deaf: false, mute: false } : { mute: !v.mute });
    const r = await this.cmd('SET_VOICE_SETTINGS', args);
    if (r.evt === 'ERROR') { this.log('discord: переключить — ' + (r.data && r.data.message)); return false; }
    this.apply(r.data);
    return true;
  }
}
