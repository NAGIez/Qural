// Какой чат сейчас открыт в приложении Claude и сколько в нём занято контекста.
// Приложение ведёт файл на каждую сессию Code: %LOCALAPPDATA%\Claude-Data\<пакет>\claude-code-sessions\…\local_<id>.json
// с полями lastFocusedAt (когда его открывали), cliSessionId, model, title, isArchived. Открытый чат — самый свежий
// lastFocusedAt. Контекст — по журналу этой сессии в ~/.claude/projects/*/<cliSessionId>.jsonl (читаем только хвост).
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

const SESSION_FILE = /^local_.*\.json$/;

export class Focus {
  constructor(onChange, log) {
    this.onChange = onChange; this.log = log;
    this.sessions = new Map();   // file → { cli, focusedAt, model, title, archived }
    this.mtimes = new Map();     // file → mtime (для страховочного опроса)
    this.transcripts = new Map(); // cli → путь к журналу
    this.ctxCache = new Map();   // cli → { mtime, size, ctx }
    this.timer = null;
    this.root = Focus.findRoot();
    if (!this.root) { log('focus: папка сессий Claude не найдена'); return; }
    this.scan();
    this.lastCli = this.focusedCli();
    this.watch();
    // страховка: fs.watch на Windows иногда молча перестаёт присылать события — раз в 2 с сверяем время файлов
    setInterval(() => this.poll(), 2000).unref();
  }

  static findRoot() {
    const base = path.join(process.env.LOCALAPPDATA || '', 'Claude-Data');
    try {
      for (const d of fs.readdirSync(base)) {
        const p = path.join(base, d, 'claude-code-sessions');
        if (fs.existsSync(p)) return p;
      }
    } catch {}
    return null;
  }

  watch() {
    try {
      const w = fs.watch(this.root, { recursive: true }, (ev, name) => {
        if (name && SESSION_FILE.test(path.basename(name))) this.queue(path.join(this.root, name));
      });
      w.on('error', e => {
        this.log('focus: слежение упало — ' + e.message);
        try { w.close(); } catch {}
        setTimeout(() => this.watch(), 3000);
      });
    } catch (e) { this.log('focus: не могу следить за папкой — ' + e.message); }
  }

  // все файлы сессий: file → mtime
  files() {
    const out = new Map();
    const walk = dir => {
      let list = [];
      try { list = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; }
      for (const e of list) {
        const p = path.join(dir, e.name);
        if (e.isDirectory()) walk(p);
        else if (SESSION_FILE.test(e.name)) { try { out.set(p, fs.statSync(p).mtimeMs); } catch {} }
      }
    };
    walk(this.root);
    return out;
  }

  // при старте — сессии, которые трогали за последние двое суток
  scan() {
    const since = Date.now() - 2 * 86400e3;
    for (const [p, m] of this.files()) { this.mtimes.set(p, m); if (m > since) this.read(p); }
  }

  poll() {
    for (const [p, m] of this.files())
      if (this.mtimes.get(p) !== m) { this.mtimes.set(p, m); this.queue(p); }
  }

  queue(file) {
    this.pending = this.pending || new Set();
    this.pending.add(file);
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.flush(), 250);
  }

  flush() {
    for (const f of this.pending || []) this.read(f);
    this.pending = null;
    const f = this.focused(), after = f ? f.cli : '';
    const changed = after !== this.lastCli;
    if (changed) { this.lastCli = after; this.log('focus: открыт «' + (f ? f.title : '—') + '»'); }
    this.onChange(changed);
  }

  read(file) {
    try {
      const j = JSON.parse(fs.readFileSync(file, 'utf8'));
      this.sessions.set(file, { cli: String(j.cliSessionId || ''), focusedAt: +j.lastFocusedAt || 0,
        model: String(j.model || ''), title: String(j.title || ''), archived: !!j.isArchived });
    } catch { /* файл как раз пишется — прочитаем при следующем событии */ }
  }

  focused() {
    let best = null;
    for (const s of this.sessions.values())
      if (!s.archived && s.cli && (!best || s.focusedAt > best.focusedAt)) best = s;
    return best;
  }
  focusedCli() { const f = this.focused(); return f ? f.cli : ''; }

  transcriptOf(cli) {
    const cached = this.transcripts.get(cli);
    if (cached && fs.existsSync(cached)) return cached;
    const projects = path.join(os.homedir(), '.claude', 'projects');
    let found = null;
    try {
      for (const d of fs.readdirSync(projects)) {
        const p = path.join(projects, d, cli + '.jsonl');
        if (fs.existsSync(p)) { found = p; break; }
      }
    } catch {}
    if (found) this.transcripts.set(cli, found);
    return found;
  }

  // { tokens, model } открытого чата (по последнему ответу модели в журнале)
  context(cli) {
    const file = cli && this.transcriptOf(cli);
    if (!file) return null;
    let st; try { st = fs.statSync(file); } catch { return null; }
    const c = this.ctxCache.get(cli);
    if (c && c.mtime === st.mtimeMs && c.size === st.size) return c.ctx;
    const ctx = Focus.tail(file, st.size);
    this.ctxCache.set(cli, { mtime: st.mtimeMs, size: st.size, ctx });
    return ctx;
  }

  // читаем хвост журнала; если последний ответ модели не влез (в чат вставили картинку или видео — одна строка
  // журнала бывает в мегабайты), берём хвост больше: 512 КБ → 4 МБ → 32 МБ → весь файл
  static tail(file, size) {
    for (const want of [512 * 1024, 4 << 20, 32 << 20, size]) {
      const len = Math.min(size, want);
      const r = Focus.scan(file, size, len);
      if (r || len >= size) return r;
    }
    return null;
  }

  static scan(file, size, len) {
    try {
      const buf = Buffer.alloc(len);
      const fd = fs.openSync(file, 'r'); fs.readSync(fd, buf, 0, len, size - len); fs.closeSync(fd);
      const lines = buf.toString('utf8').split('\n');
      // Claude остановили (Esc, «Стоп» в приложении): хука Stop при этом нет, в журнале — строка пользователя
      // «[Request interrupted by user…]». Время последней такой строки после последнего ответа — interruptedAt.
      let interruptedAt = 0;
      for (let i = lines.length - 1; i >= 0; i--) {
        const l = lines[i];
        if (!interruptedAt && l.indexOf('[Request interrupted by user') >= 0 && l.indexOf('"type":"user"') >= 0) {
          interruptedAt = Focus.interruptTime(l);
          if (interruptedAt) continue;
        }
        // после /compact и до первого ответа — размер уже сжатого чата
        if (l.indexOf('"compact_boundary"') >= 0) {
          try { const m = JSON.parse(l).compactMetadata || {}; if (m.postTokens > 0) return { tokens: m.postTokens, model: '', interruptedAt }; } catch {}
          continue;
        }
        if (l.indexOf('"usage"') < 0 || l.indexOf('"assistant"') < 0) continue;
        let j; try { j = JSON.parse(l); } catch { continue; }
        const m = j.message || {}, u = m.usage;
        if (!u || j.isSidechain) continue;
        const tokens = (u.input_tokens || 0) + (u.cache_read_input_tokens || 0) + (u.cache_creation_input_tokens || 0);
        if (tokens > 0) return { tokens, model: String(m.model || ''), interruptedAt };
      }
    } catch {}
    return null;
  }

  // строка — сообщение пользователя, которое целиком «[Request interrupted by user…]» (не цитата внутри текста)
  static interruptTime(line) {
    try {
      const j = JSON.parse(line);
      if (j.type !== 'user' || j.isSidechain) return 0;
      const c = j.message && j.message.content;
      const text = typeof c === 'string' ? c : Array.isArray(c) && c.length === 1 && c[0] && c[0].type === 'text' ? c[0].text : '';
      return /^\[Request interrupted by user/.test(String(text || '')) ? Date.parse(j.timestamp) || 0 : 0;
    } catch { return 0; }
  }
}
