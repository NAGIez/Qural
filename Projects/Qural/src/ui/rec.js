// Qural — «Запись экрана»: кодек, качество, звук, шумоподавление, папка, последние записи.
(() => {
  const $ = (id) => document.getElementById('rc-' + id);
  const esc = Q.esc;
  const send = (m) => Q.send('rec', m);
  let S = null, capture = null, tickT = null, since = 0;

  const QUALITY = { high: 'Высокое', medium: 'Среднее', low: 'Экономное', bitrate: 'Свой битрейт' };
  const FPS = { 15: '15', 24: '24', 30: '30', 60: '60', 120: '120' };
  const SCALE = { source: 'Как на экране', 1440: 'До 1440p', 1080: 'До 1080p', 720: 'До 720p' };
  const NOISE = { rnn: 'Сильное (RNNoise)', fft: 'Мягкое', off: 'Выключено' };
  const CONTAINER = { mp4: 'MP4 — откроется везде', mkv: 'MKV — надёжнее при сбоях' };
  const AFTER = { editor: 'Открыть редактор', save: 'Сразу сохранить файл' };
  const START = { region: 'Выбрать область или окно…', screen: 'Весь экран (монитор с курсором)', window: 'Окно в фокусе' };

  const fmtTime = (s) => { s = Math.max(0, Math.floor(s)); const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, x = s % 60; return (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(x).padStart(2, '0'); };
  const fmtSize = (b) => b > 1e9 ? (b / 1e9).toFixed(1) + ' ГБ' : Math.max(1, Math.round(b / 1e6)) + ' МБ';

  function keyText(btn, text) { if (btn === capture) return; btn.textContent = text || 'не задана'; btn.classList.toggle('empty', !text); }
  function paint(r, out, suffix) { const p = ((r.value - r.min) / (r.max - r.min)) * 100; r.style.setProperty('--p', p + '%'); out.textContent = r.value + suffix; }

  function codecLabel(st) {
    if (st.codec === 'auto' || !st.codec) return 'Авто' + (st.auto ? ' — ' + st.auto : '');
    const e = (st.encoders || []).find((x) => x.id === st.codec);
    return e ? e.label : st.codec;
  }

  function render(st) {
    S = st;
    $('noff').hidden = st.ffmpeg;
    keyText($('kToggle'), st.toggle); keyText($('kPause'), st.pause);
    document.querySelectorAll('[data-rclear]').forEach((b) => (b.hidden = !st[b.dataset.rclear]));
    $('codecV').textContent = st.probing ? 'проверяю кодеки…' : codecLabel(st);
    const enc = (st.encoders || []).find((x) => x.id === st.codec) || null;
    $('codecNote').textContent = enc && !enc.gpu ? 'Кодирует процессор — нагрузка выше, чем на видеокарте' : 'Видеокарта кодирует сама — процессор почти не занят';
    $('qualityV').textContent = QUALITY[st.quality];
    $('bitrateRow').hidden = st.quality !== 'bitrate';
    const br = $('bitrate'); if (document.activeElement !== br) br.value = st.bitrate; paint(br, $('bitrateOut'), ' Мбит/с');
    $('fpsV').textContent = st.fps; $('scaleV').textContent = SCALE[st.scale];
    $('cursor').checked = st.cursor; $('countdown').checked = st.countdown;
    $('sys').checked = st.sys; $('mic').checked = st.mic; $('separate').checked = st.separate;
    $('sysVolRow').hidden = !st.sys; $('micVolRow').hidden = !st.mic; $('micDevRow').hidden = !st.mic; $('noiseRow').hidden = !st.mic;
    for (const k of ['sysVol', 'micVol']) { const r = $(k); if (document.activeElement !== r) r.value = st[k]; paint(r, $(k + 'Out'), '%'); }
    const dev = (st.mics || []).find((x) => x.id === st.micDevice);
    $('micDevV').textContent = dev ? dev.name : 'Основной в Windows';
    $('noiseV').textContent = NOISE[st.noise];
    $('folder').textContent = st.folder; $('reset').hidden = !st.customFolder;
    $('containerV').textContent = CONTAINER[st.container]; $('afterV').textContent = AFTER[st.after];

    // кнопка сверху: начать / остановить, пауза — во время записи
    $('start').textContent = st.recording ? 'Остановить' : 'Начать запись';
    $('start').classList.toggle('qr-stop', st.recording);
    $('pause').hidden = !st.recording;
    $('pause').textContent = st.paused ? 'Продолжить' : 'Пауза';
    since = performance.now() / 1000 - st.elapsed;
    clearInterval(tickT);
    if (st.recording && !st.paused) tickT = setInterval(sub, 500);
    sub();

    Q.setHTML($('sessions'), (st.sessions || []).length
      ? st.sessions.map((x, i) => `<li><span class="qr-kic">${Q.svg('rec', '')}</span><div class="gw-t"><b>${esc(x.when)} · ${fmtTime(x.dur)}</b><small>${x.w}×${x.h} · ${fmtSize(x.size)}</small></div>` +
        `<span class="gw-row"><button class="gw-pill" data-open="${i}">Открыть в редакторе</button><button class="gw-ib del" data-del="${i}" title="Удалить запись"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round"><path d="M3 4.5h10M6.5 4.5V3h3v1.5M4.5 4.5l.7 8.5h5.6l.7-8.5"/></svg></button></span></li>`).join('')
      : '<li class="empty-row">Записей пока нет</li>');
  }

  function sub() {
    if (!S) return;
    const t = S.paused ? S.elapsed : performance.now() / 1000 - since;
    $('sub').textContent = S.recording ? (S.paused ? 'Пауза · ' : 'Идёт запись · ') + fmtTime(t) : 'Область, окно или весь экран — со звуком ПК и микрофоном';
  }

  // ---------- меню ----------
  const MENUS = {
    codec: () => [{ value: 'auto', label: 'Авто' + (S.auto ? ' — ' + S.auto : ''), on: S.codec === 'auto' }].concat((S.encoders || []).map((e) => ({ value: e.id, label: e.label, on: S.codec === e.id }))),
    quality: () => Object.keys(QUALITY).map((v) => ({ value: v, label: QUALITY[v], on: S.quality === v })),
    fps: () => Object.keys(FPS).map((v) => ({ value: v, label: FPS[v] + ' кадров', on: String(S.fps) === v })),
    scale: () => Object.keys(SCALE).map((v) => ({ value: v, label: SCALE[v], on: S.scale === v })),
    micDevice: () => [{ value: '', label: 'Основной в Windows', on: !S.micDevice }].concat((S.mics || []).map((m) => ({ value: m.id, label: m.name + (m.def ? ' (основной)' : ''), on: S.micDevice === m.id }))),
    noise: () => Object.keys(NOISE).filter((v) => v !== 'rnn' || S.rnn).map((v) => ({ value: v, label: NOISE[v], on: S.noise === v })),
    container: () => Object.keys(CONTAINER).map((v) => ({ value: v, label: CONTAINER[v], on: S.container === v })),
    after: () => Object.keys(AFTER).map((v) => ({ value: v, label: AFTER[v], on: S.after === v })),
  };
  document.querySelectorAll('[data-rmenu]').forEach((b) => (b.onclick = (e) => {
    if (!S) return;
    const field = b.dataset.rmenu;
    GW.menu(e.currentTarget, MENUS[field](), (v) => send({ type: 'set', field, value: field === 'fps' ? +v : v }));
  }));

  $('start').onclick = (e) => {
    if (!S) return;
    if (S.recording) { send({ type: 'stop' }); return; }
    GW.menu(e.currentTarget, Object.keys(START).map((v) => ({ value: v, label: START[v] })), (v) => send({ type: 'start', mode: v }));
  };
  $('pause').onclick = () => send({ type: 'pause' });
  for (const k of ['cursor', 'countdown', 'sys', 'mic', 'separate']) $(k).addEventListener('change', (e) => send({ type: 'set', field: k, value: e.target.checked }));
  for (const [k, suffix] of [['bitrate', ' Мбит/с'], ['sysVol', '%'], ['micVol', '%']]) {
    const r = $(k);
    r.addEventListener('input', () => paint(r, $(k + 'Out'), suffix));
    r.addEventListener('change', () => send({ type: 'set', field: k, value: +r.value }));
  }
  $('open').onclick = () => send({ type: 'openFolder' });
  $('pick').onclick = () => send({ type: 'folder' });
  $('reset').onclick = () => send({ type: 'folderReset' });
  $('sessions').addEventListener('click', (e) => {
    const o = e.target.closest('[data-open]'), d = e.target.closest('[data-del]');
    if (o && S) send({ type: 'open', dir: S.sessions[+o.dataset.open].dir });
    if (d && S) send({ type: 'delete', dir: S.sessions[+d.dataset.del].dir });
  });
  document.querySelectorAll('[data-rclear]').forEach((b) => (b.onclick = () => send({ type: 'hotkey', target: b.dataset.rclear, key: 0 })));

  // ---------- горячие клавиши ----------
  const MODS = ['Control', 'Shift', 'Alt', 'Meta', 'AltGraph'];
  function startCapture(btn) { if (capture === btn) return; stopCapture(); capture = btn; btn.classList.add('rec'); btn.textContent = 'нажмите сочетание…'; Q.send('shell', { type: 'captureStart' }); }
  function stopCapture() { if (!capture) return; capture.classList.remove('rec'); capture = null; Q.send('shell', { type: 'captureEnd' }); if (S) render(S); }
  for (const id of ['kToggle', 'kPause']) $(id).addEventListener('click', (e) => startCapture(e.currentTarget));
  addEventListener('keydown', (e) => {
    if (!capture || !capture.closest('#view-rec')) return;
    e.preventDefault(); e.stopPropagation();
    const mods = e.ctrlKey || e.shiftKey || e.altKey;
    if (MODS.includes(e.key)) { capture.textContent = [e.ctrlKey && 'Ctrl', e.shiftKey && 'Shift', e.altKey && 'Alt'].filter(Boolean).join('+') + '+…'; return; }
    const target = capture.dataset.target;
    if (e.key === 'Escape' && !mods) { stopCapture(); return; }
    if ((e.key === 'Backspace' || e.key === 'Delete') && !mods) { stopCapture(); send({ type: 'hotkey', target, key: 0 }); return; }
    const key = e.keyCode | (e.shiftKey ? 0x10000 : 0) | (e.ctrlKey ? 0x20000 : 0) | (e.altKey ? 0x40000 : 0);
    stopCapture(); send({ type: 'hotkey', target, key });
  }, true);
  addEventListener('blur', stopCapture);
  document.addEventListener('pointerdown', (e) => { if (capture && e.target !== capture) stopCapture(); }, true);

  // ---------- связь ----------
  Q.on('rec', (m) => {
    if (m.type === 'state') { Q.state.rec = m.state; render(m.state); }
    else if (m.type === 'toast') GW.toast(m.text);
  });
  if (!Q.host) addEventListener('DOMContentLoaded', () => Q.emit({ mod: 'rec', type: 'state', state: {
    ffmpeg: true, toggle: 'Ctrl+Alt+R', pause: 'Ctrl+Alt+P', recording: false, paused: false, elapsed: 0, probing: false,
    encoders: [{ id: 'h264_nvenc', label: 'H.264 — видеокарта NVIDIA', gpu: true }, { id: 'hevc_nvenc', label: 'HEVC (H.265) — видеокарта NVIDIA', gpu: true }, { id: 'av1_nvenc', label: 'AV1 — видеокарта NVIDIA', gpu: true }, { id: 'libx264', label: 'H.264 — процессор', gpu: false }],
    auto: 'H.264 — видеокарта NVIDIA', codec: 'auto', quality: 'high', bitrate: 12, fps: 30, scale: 'source', cursor: true, countdown: true,
    sys: true, mic: true, sysVol: 100, micVol: 100, micDevice: '', mics: [{ id: 'a', name: 'Микрофон (USB)', def: true }], noise: 'rnn', rnn: true, separate: false,
    folder: 'C:\\Users\\Вы\\Videos\\Qural', customFolder: false, container: 'mp4', after: 'editor',
    sessions: [{ dir: 'x', when: '22.11 20:15', dur: 754, size: 812000000, w: 2560, h: 1440 }] } }));
})();
