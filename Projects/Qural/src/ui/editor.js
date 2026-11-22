// Редактор записи Qural: просмотр, обрезка, вырезка кусков, кадр, звук, сохранение.
(() => {
  const host = window.chrome && chrome.webview;
  const send = (m) => { if (host) host.postMessage(m); };
  const $ = (id) => document.getElementById(id);
  const v = $('video');
  const audios = {};            // sys / mic — отдельные дорожки, громкость на лету
  let I = null;                 // что прислал Qural: длительность, размеры, дорожки, кодеки
  let E = null;                 // правки
  let sel = null;               // выделенный кусок [a, b]
  const hist = [];              // для Ctrl+Z: обрезка и вырезки
  let busy = false;             // идёт сохранение

  const NOISE = { rnn: 'Сильное (RNNoise)', fft: 'Мягкое', off: 'Выключено' };
  const FORMAT = { mp4: 'MP4 — видео', mkv: 'MKV — видео', gif: 'GIF — анимация без звука', m4a: 'M4A — только звук', mp3: 'MP3 — только звук' };
  const QUALITY = { high: 'Высокое', medium: 'Среднее', low: 'Экономное', bitrate: 'Свой битрейт' };
  const SCALE = { source: 'Как в записи', 1440: 'До 1440p', 1080: 'До 1080p', 720: 'До 720p', 480: 'До 480p' };
  const ICON_PLAY = '<svg viewBox="0 0 16 16" fill="currentColor"><path d="M5 3.5v9l7.5-4.5z"/></svg>';
  const ICON_PAUSE = '<svg viewBox="0 0 16 16" fill="currentColor"><rect x="4" y="3.5" width="3" height="9" rx=".8"/><rect x="9" y="3.5" width="3" height="9" rx=".8"/></svg>';
  const ICON_VOL = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2.5 6v4h2.5l3.5 3V3L5 6z"/><path d="M11 5.5a3.5 3.5 0 010 5"/></svg>';
  const ICON_MUTE = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M2.5 6v4h2.5l3.5 3V3L5 6z"/><path d="M11 6l3.5 4M14.5 6L11 10"/></svg>';

  const fmt = (s, ms) => {
    s = Math.max(0, s || 0);
    const h = Math.floor(s / 3600), m = Math.floor(s / 60) % 60, x = Math.floor(s % 60);
    const base = (h ? h + ':' + String(m).padStart(2, '0') : m) + ':' + String(x).padStart(2, '0');
    return ms ? base + '.' + Math.floor((s % 1) * 10) : base;
  };
  const clamp = (x, a, b) => Math.max(a, Math.min(b, x));
  const paint = (r) => r.style.setProperty('--p', ((r.value - r.min) / (r.max - r.min)) * 100 + '%');

  // ── время с учётом вырезок ──
  function keep() {
    let k = [[E.start, E.end]];
    for (const c of [...E.cuts].sort((a, b) => a[0] - b[0])) {
      const n = [];
      for (const [a, b] of k) {
        if (c[1] <= a || c[0] >= b) { n.push([a, b]); continue; }
        if (c[0] > a + 0.05) n.push([a, c[0]]);
        if (c[1] < b - 0.05) n.push([c[1], b]);
      }
      k = n;
    }
    return k.filter(([a, b]) => b - a > 0.05);
  }
  const keptDur = () => keep().reduce((s, [a, b]) => s + b - a, 0);

  function snapshot() { hist.push(JSON.stringify({ start: E.start, end: E.end, cuts: E.cuts })); if (hist.length > 100) hist.shift(); }
  function undo() { const h = hist.pop(); if (!h) return; Object.assign(E, JSON.parse(h)); renderTl(); outInfo(); }

  // ── просмотр ──
  // ── звук просмотра: дорожки → усиление → ограничитель (как alimiter при сохранении) → колонки ──
  let actx = null; const gains = {};
  function route(k, a) {
    try {
      if (!actx) {
        actx = new AudioContext();
        const lim = actx.createDynamicsCompressor();
        lim.threshold.value = -1; lim.knee.value = 0; lim.ratio.value = 20; lim.attack.value = 0.002; lim.release.value = 0.08;
        lim.connect(actx.destination); actx.limiter = lim;
      }
      const g = actx.createGain();
      actx.createMediaElementSource(a).connect(g).connect(actx.limiter);
      gains[k] = g;
    } catch (e) { send({ type: 'log', text: 'web audio: ' + e.message }); }
  }
  function play() {
    if (actx && actx.state === 'suspended') actx.resume().catch(() => {});
    if (v.currentTime < E.start || v.currentTime >= E.end - 0.05) seek(E.start);
    v.play().catch(() => {});
    for (const a of Object.values(audios)) { a.currentTime = v.currentTime; a.play().catch(() => {}); }
  }
  function pause() { v.pause(); for (const a of Object.values(audios)) a.pause(); }
  function seek(t) {
    t = clamp(t, 0, I ? I.dur : 0);
    v.currentTime = t;
    for (const a of Object.values(audios)) a.currentTime = t;
    renderPh();
  }
  v.addEventListener('play', () => ($('playBtn').innerHTML = ICON_PAUSE));
  v.addEventListener('pause', () => ($('playBtn').innerHTML = ICON_PLAY));
  $('playBtn').innerHTML = ICON_PLAY;
  $('playBtn').onclick = () => (v.paused ? play() : pause());

  function loop() {
    if (I && E && !v.paused) {
      const t = v.currentTime;
      const c = E.cuts.find((c) => t >= c[0] && t < c[1] - 0.03);
      if (c) seek(c[1]);
      else if (t >= E.end - 0.03) { pause(); seek(E.end); }
      else for (const a of Object.values(audios)) if (!a.paused && Math.abs(a.currentTime - t) > 0.12) a.currentTime = t;
    }
    renderPh();
    requestAnimationFrame(loop);
  }
  requestAnimationFrame(loop);

  // ── шкала ──
  const track = $('track');
  const pct = (t) => (I && I.dur ? (t / I.dur) * 100 : 0) + '%';
  const timeAt = (x) => { const r = track.getBoundingClientRect(); return clamp(((x - r.left) / r.width) * I.dur, 0, I.dur); };

  function renderPh() {
    if (!I) return;
    $('ph').style.left = pct(v.currentTime);
    $('tNow').textContent = fmt(v.currentTime, true);
  }

  function renderTl() {
    if (!I) return;
    $('outL').style.left = '0'; $('outL').style.width = pct(E.start);
    $('outR').style.left = pct(E.end); $('outR').style.width = 'calc(100% - ' + pct(E.end) + ')';
    $('hL').style.left = pct(E.start); $('hR').style.left = pct(E.end);
    $('cuts').innerHTML = E.cuts.map((c, i) => `<div class="ed-cut" style="left:${pct(c[0])};width:calc(${pct(c[1])} - ${pct(c[0])})" title="Вырезано ${fmt(c[1] - c[0], true)}"><button data-uncut="${i}" title="Вернуть кусок">×</button></div>`).join('');
    const s = $('sel');
    s.hidden = !sel;
    if (sel) { s.style.left = pct(sel[0]); s.style.width = 'calc(' + pct(sel[1]) + ' - ' + pct(sel[0]) + ')'; }
    $('selActs').hidden = !sel; $('hint').hidden = !!sel;
    if (sel) $('selText').textContent = fmt(sel[0], true) + ' — ' + fmt(sel[1], true) + ' (' + fmt(sel[1] - sel[0], true) + ')';
    $('tDur').textContent = fmt(keptDur()) + (keptDur() < I.dur - 0.1 ? ' из ' + fmt(I.dur) : '');
    outInfo();
  }

  function renderTicks() {
    const steps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600];
    const step = steps.find((s) => I.dur / s <= 12) || 3600;
    let h = '';
    for (let t = 0; t <= I.dur + 0.001; t += step) h += `<span style="left:${pct(t)}">${fmt(t)}</span>`;
    $('ticks').innerHTML = h;
  }

  let drag = null;
  track.addEventListener('pointerdown', (e) => {
    if (!I || busy) return;
    const un = e.target.closest('[data-uncut]');
    if (un) { snapshot(); E.cuts.splice(+un.dataset.uncut, 1); renderTl(); return; }
    track.setPointerCapture(e.pointerId);
    if (e.target === $('hL') || e.target === $('hR')) { snapshot(); drag = { kind: e.target === $('hL') ? 'L' : 'R' }; return; }
    drag = { kind: 'sel', x: e.clientX, t: timeAt(e.clientX), moved: false };
  });
  track.addEventListener('pointermove', (e) => {
    if (!drag) return;
    const t = timeAt(e.clientX);
    if (drag.kind === 'L') { E.start = clamp(t, 0, E.end - 0.2); seek(E.start); renderTl(); }
    else if (drag.kind === 'R') { E.end = clamp(t, E.start + 0.2, I.dur); seek(E.end); renderTl(); }
    else {
      if (!drag.moved && Math.abs(e.clientX - drag.x) > 4) drag.moved = true;
      if (drag.moved) { sel = [Math.min(drag.t, t), Math.max(drag.t, t)]; renderTl(); }
    }
  });
  track.addEventListener('pointerup', (e) => {
    if (!drag) return;
    if (drag.kind === 'sel' && !drag.moved) { sel = null; seek(timeAt(e.clientX)); renderTl(); }
    drag = null;
  });

  function cutSel() {
    if (!sel || sel[1] - sel[0] < 0.05) return;
    snapshot();
    // слить с соседними вырезками
    let a = sel[0], b = sel[1];
    const rest = [];
    for (const c of E.cuts) { if (c[1] < a || c[0] > b) rest.push(c); else { a = Math.min(a, c[0]); b = Math.max(b, c[1]); } }
    rest.push([a, b]);
    E.cuts = rest.sort((x, y) => x[0] - y[0]);
    sel = null; renderTl();
  }
  function keepSel() {
    if (!sel) return;
    snapshot();
    E.start = sel[0]; E.end = sel[1];
    E.cuts = E.cuts.filter((c) => c[1] > E.start && c[0] < E.end);
    sel = null; seek(E.start); renderTl();
  }
  $('cutBtn').onclick = cutSel;
  $('keepBtn').onclick = keepSel;
  $('selClear').onclick = () => { sel = null; renderTl(); };

  // ── клавиши ──
  addEventListener('keydown', (e) => {
    if (!I || busy) return;
    if (e.target.closest && e.target.closest('input[type=range]') && (e.key === 'ArrowLeft' || e.key === 'ArrowRight')) return;
    const t = v.currentTime;
    if (e.code === 'Space') { e.preventDefault(); v.paused ? play() : pause(); }
    else if (e.key === 'ArrowLeft' || e.key === 'ArrowRight') { e.preventDefault(); seek(t + (e.key === 'ArrowLeft' ? -1 : 1) * (e.shiftKey ? 5 : 1)); }
    else if (e.code === 'KeyI') { sel = [t, sel ? Math.max(sel[1], t + 0.1) : Math.min(I.dur, t + 5)]; renderTl(); }
    else if (e.code === 'KeyO') { sel = [sel ? Math.min(sel[0], t - 0.1) : Math.max(0, t - 5), t]; renderTl(); }
    else if (e.key === 'Delete' || e.key === 'Backspace') { e.preventDefault(); cutSel(); }
    else if (e.key === 'Escape') { sel = null; renderTl(); }
    else if (e.ctrlKey && e.code === 'KeyZ') { e.preventDefault(); undo(); }
    else if (e.ctrlKey && e.code === 'KeyS') { e.preventDefault(); save(); }
    else if (e.key === 'Home') seek(E.start);
    else if (e.key === 'End') seek(E.end);
  });

  // ── звук ──
  function setVol(k) {
    const r = $(k + 'Vol');
    E[k + 'Vol'] = +r.value; paint(r);
    $(k + 'Out').textContent = r.value + '%';
    const muted = E[k + 'Mute'];
    $(k + 'Mute').innerHTML = muted ? ICON_MUTE : ICON_VOL;
    $(k + 'Row').classList.toggle('muted', muted);
    // громкость через Web Audio (усиление до 200 % слышно сразу); не вышло — обычная громкость, не выше 100 %
    const vol = muted ? 0 : E[k + 'Vol'] / 100;
    if (gains[k]) gains[k].gain.setTargetAtTime(vol, actx.currentTime, 0.015);
    else if (audios[k]) audios[k].volume = Math.min(1, vol);
    outInfo();
  }
  for (const k of ['sys', 'mic']) {
    $(k + 'Vol').addEventListener('input', () => setVol(k));
    $(k + 'Mute').onclick = () => { E[k + 'Mute'] = !E[k + 'Mute']; setVol(k); };
  }
  let noiseUrl = {};
  $('noiseBtn').onclick = (e) => GW.menu(e.currentTarget,
    Object.keys(NOISE).filter((k) => k !== 'rnn' || I.rnn).map((k) => ({ value: k, label: NOISE[k], on: E.noise === k })),
    (k) => { E.noise = k; $('noiseV').textContent = NOISE[k]; applyNoise(); outInfo(); });
  function applyNoise() {
    if (!I || !I.hasMic || !audios.mic) return;
    if (noiseUrl[E.noise]) { swapMic(noiseUrl[E.noise]); return; }
    $('noiseNote').textContent = 'Готовлю…';
    send({ type: 'noise', mode: E.noise });
  }
  function swapMic(url) {
    const a = audios.mic; if (!a || a.src === url) { $('noiseNote').textContent = 'Для микрофона: слышно сразу при просмотре'; return; }
    const playing = !v.paused;
    a.src = url;
    a.addEventListener('loadedmetadata', () => { a.currentTime = v.currentTime; if (playing) a.play().catch(() => {}); }, { once: true });
    $('noiseNote').textContent = 'Для микрофона: слышно сразу при просмотре';
  }
  $('separate').addEventListener('change', (e) => { E.separate = e.target.checked; outInfo(); });

  // ── кадр ──
  const cropEl = $('crop'), box = $('cropBox');
  $('cropOn').addEventListener('change', (e) => {
    if (e.target.checked) {
      if (!E.crop) { const mx = Math.round(I.width * 0.1) & ~1, my = Math.round(I.height * 0.1) & ~1; E.crop = { x: mx, y: my, w: (I.width - 2 * mx) & ~1, h: (I.height - 2 * my) & ~1 }; }
      cropEl.hidden = false; renderCrop();
    } else { E.crop = null; cropEl.hidden = true; }
    outInfo();
  });
  const kx = () => I.width / Math.max(1, v.clientWidth), ky = () => I.height / Math.max(1, v.clientHeight);
  function renderCrop() {
    if (!E.crop) return;
    const c = E.crop;
    box.style.left = c.x / kx() + 'px'; box.style.top = c.y / ky() + 'px';
    box.style.width = c.w / kx() + 'px'; box.style.height = c.h / ky() + 'px';
    $('cropSize').textContent = c.w + ' × ' + c.h;
    $('cropNote').textContent = c.w + ' × ' + c.h + ' — тяните рамку на видео';
  }
  addEventListener('resize', renderCrop);
  let cdrag = null;
  box.addEventListener('pointerdown', (e) => {
    if (busy) return;
    e.stopPropagation(); box.setPointerCapture(e.pointerId);
    cdrag = { h: e.target.dataset.h !== undefined ? +e.target.dataset.h : -1, x: e.clientX, y: e.clientY, c: { ...E.crop } };
  });
  box.addEventListener('pointermove', (e) => {
    if (!cdrag) return;
    const dx = Math.round((e.clientX - cdrag.x) * kx()), dy = Math.round((e.clientY - cdrag.y) * ky());
    let { x, y, w, h } = cdrag.c, l = x, t = y, r = x + w, b = y + h;
    if (cdrag.h < 0) { l = clamp(x + dx, 0, I.width - w); t = clamp(y + dy, 0, I.height - h); r = l + w; b = t + h; }
    else {
      const hh = cdrag.h;
      if (hh === 0 || hh === 6 || hh === 7) l = clamp(x + dx, 0, r - 32);
      if (hh === 2 || hh === 3 || hh === 4) r = clamp(x + w + dx, l + 32, I.width);
      if (hh === 0 || hh === 1 || hh === 2) t = clamp(y + dy, 0, b - 32);
      if (hh === 4 || hh === 5 || hh === 6) b = clamp(y + h + dy, t + 32, I.height);
    }
    E.crop = { x: l & ~1, y: t & ~1, w: (r - l) & ~1, h: (b - t) & ~1 };
    renderCrop();
  });
  box.addEventListener('pointerup', () => { cdrag = null; outInfo(); });

  $('scaleBtn').onclick = (e) => GW.menu(e.currentTarget, Object.keys(SCALE).map((k) => ({ value: k, label: SCALE[k], on: E.scale === k })),
    (k) => { E.scale = k; $('scaleV').textContent = SCALE[k]; outInfo(); });

  // ── сохранение ──
  const encLabel = (id) => id === 'copy' ? 'Как записано' : ((I.encoders.find((x) => x.id === id) || {}).label || id);
  $('formatBtn').onclick = (e) => GW.menu(e.currentTarget, Object.keys(FORMAT).map((k) => ({ value: k, label: FORMAT[k], on: E.format === k })), (k) => { E.format = k; syncSave(); });
  $('encBtn').onclick = (e) => GW.menu(e.currentTarget,
    [{ value: 'copy', label: 'Как записано (' + encLabel(I.encoder) + ')', on: E.encoder === 'copy' }].concat(I.encoders.map((x) => ({ value: x.id, label: x.label, on: E.encoder === x.id }))),
    (k) => { E.encoder = k; syncSave(); });
  $('qualityBtn').onclick = (e) => GW.menu(e.currentTarget, Object.keys(QUALITY).filter((k) => k !== 'bitrate').map((k) => ({ value: k, label: QUALITY[k], on: E.quality === k })), (k) => { E.quality = k; syncSave(); });
  $('gifFpsBtn').onclick = (e) => GW.menu(e.currentTarget, [8, 10, 12, 15, 20, 25].map((k) => ({ value: k, label: k + ' кадров/с', on: E.gifFps === k })), (k) => { E.gifFps = +k; syncSave(); });
  $('gifWBtn').onclick = (e) => GW.menu(e.currentTarget, [480, 640, 800, 960, 1280].map((k) => ({ value: k, label: 'до ' + k + ' px', on: E.gifWidth === k })), (k) => { E.gifWidth = +k; syncSave(); });
  $('ask').addEventListener('change', (e) => (E.ask = e.target.checked));

  function syncSave() {
    const audioOnly = E.format === 'm4a' || E.format === 'mp3', gif = E.format === 'gif';
    $('formatV').textContent = FORMAT[E.format];
    $('encV').textContent = encLabel(E.encoder);
    $('qualityV').textContent = QUALITY[E.quality] || QUALITY.high;
    $('encBtn').hidden = audioOnly || gif; $('qualityBtn').hidden = audioOnly || gif;
    $('gifRow').hidden = !gif; $('scaleBtn').hidden = audioOnly;
    $('gifFpsBtn').textContent = E.gifFps + ' кадров/с'; $('gifWBtn').textContent = 'до ' + E.gifWidth + ' px';
    outInfo();
  }

  function needsReencode() {
    const k = keep();
    const full = k.length === 1 && k[0][0] < 0.05 && k[0][1] > I.dur - 0.05;
    const limit = { 1440: 1440, 1080: 1080, 720: 720, 480: 480 }[E.scale] || 0;
    const h = E.crop ? E.crop.h : I.height;
    return E.format === 'gif' || !full || !!E.crop || (limit && h > limit) || (E.encoder !== 'copy' && E.encoder !== I.encoder);
  }
  function outInfo() {
    if (!I || !E) return;
    const audioOnly = E.format === 'm4a' || E.format === 'mp3';
    const parts = ['Получится ' + fmt(keptDur())];
    if (!audioOnly) parts.push(E.format === 'gif' ? 'GIF без звука' : needsReencode() ? 'видео перекодируется (' + encLabel(E.encoder === 'copy' ? I.encoder : E.encoder) + ')' : 'видео без перекодирования — быстро');
    const tracks = (I.hasSys && !E.sysMute ? 1 : 0) + (I.hasMic && !E.micMute ? 1 : 0);
    if (E.format !== 'gif') parts.push(tracks === 0 ? 'без звука' : tracks === 2 && E.separate && !audioOnly ? 'две звуковые дорожки' : 'звук одной дорожкой');
    if (I.hasMic && !E.micMute && E.noise !== 'off' && E.format !== 'gif') parts.push('шумоподавление: ' + NOISE[E.noise].toLowerCase());
    $('outInfo').textContent = parts.join(' · ');
  }

  function save() {
    if (!I || busy) return;
    pause();
    send(Object.assign({ type: 'export' }, {
      start: E.start, end: E.end, cuts: E.cuts,
      cropX: E.crop ? E.crop.x : 0, cropY: E.crop ? E.crop.y : 0, cropW: E.crop ? E.crop.w : 0, cropH: E.crop ? E.crop.h : 0,
      sysVol: E.sysVol, micVol: E.micVol, sysMute: !!E.sysMute, micMute: !!E.micMute, separate: !!E.separate, noise: E.noise,
      format: E.format, encoder: E.encoder, quality: E.quality, bitrate: E.bitrate, scale: E.scale, gifFps: E.gifFps, gifWidth: E.gifWidth, ask: !!E.ask,
    }));
  }
  $('saveBtn').onclick = save;

  function wait(title, p, text, cancel) {
    $('wait').hidden = false;
    $('waitTitle').textContent = title;
    $('waitBar').style.width = Math.round((p || 0) * 100) + '%';
    $('waitText').textContent = text || '';
    let c = $('waitCancel');
    if (cancel && !c) { c = document.createElement('button'); c.id = 'waitCancel'; c.className = 'gw-pill'; c.textContent = 'Отменить'; c.onclick = () => send({ type: 'cancel' }); $('waitText').after(c); }
    if (!cancel && c) c.remove();
  }
  const unwait = () => { $('wait').hidden = true; const c = $('waitCancel'); if (c) c.remove(); };

  $('frameBtn').onclick = () => send({ type: 'frame', t: v.currentTime, cropX: E && E.crop ? E.crop.x : 0, cropY: E && E.crop ? E.crop.y : 0, cropW: E && E.crop ? E.crop.w : 0, cropH: E && E.crop ? E.crop.h : 0 });
  let delArmed = 0;
  $('delBtn').onclick = () => {
    if (Date.now() - delArmed < 3000) { send({ type: 'delete' }); return; }
    delArmed = Date.now();
    GW.toast('Нажмите ещё раз, чтобы удалить запись насовсем');
  };
  $('copyFile').onclick = () => send({ type: 'copyFile' });
  $('openFolder').onclick = () => send({ type: 'openFolder' });
  $('closeEd').onclick = () => send({ type: 'close' });
  document.querySelectorAll('#doneSheet [data-close]').forEach((b) => (b.onclick = () => ($('doneSheet').hidden = true)));

  // ── сообщения от Qural ──
  function onMsg(m) {
    switch (m.type) {
      case 'init': {
        I = m;
        E = { start: 0, end: m.dur, cuts: [], crop: null, sysVol: m.defaults.sysVol, micVol: m.defaults.micVol, sysMute: false, micMute: false,
          noise: m.defaults.noise, separate: m.defaults.separate, format: m.defaults.format === 'mkv' ? 'mkv' : 'mp4', encoder: 'copy',
          quality: m.defaults.quality === 'bitrate' ? 'high' : m.defaults.quality, bitrate: m.defaults.bitrate, scale: 'source', gifFps: 12, gifWidth: 960, ask: false };
        $('info').textContent = m.started + ' · ' + m.width + '×' + m.height + ' · ' + m.fps + ' кадров/с · ' + fmt(m.dur);
        $('sysRow').hidden = !m.hasSys; $('micRow').hidden = !m.hasMic;
        $('noiseBtn').hidden = !m.hasMic; $('sepRow').hidden = !(m.hasSys && m.hasMic);
        $('sysVol').value = E.sysVol; $('micVol').value = E.micVol; setVol('sys'); setVol('mic');
        $('noiseV').textContent = NOISE[E.noise] || NOISE.off;
        $('separate').checked = E.separate;
        $('scaleV').textContent = SCALE.source;
        syncSave(); renderTicks(); renderTl();
        wait('Готовлю просмотр…', 0, 'Видео без перекодирования, звук — дорожками');
        break;
      }
      case 'prep': wait('Готовлю просмотр…', m.p, Math.round(m.p * 100) + '%'); break;
      case 'sources': {
        v.src = m.video;
        for (const k of ['sys', 'mic']) {
          if (!m[k]) continue;
          // crossOrigin — иначе Web Audio получит тишину: звук с qural.media, страница с qural.ui
          const a = new Audio(); a.crossOrigin = 'anonymous'; a.preload = 'auto'; a.src = m[k]; audios[k] = a;
          route(k, a);
        }
        noiseUrl.off = m.mic;
        setVol('sys'); setVol('mic');
        v.addEventListener('loadedmetadata', () => { unwait(); renderCrop(); seek(0); }, { once: true });
        if (E.noise !== 'off') applyNoise();
        break;
      }
      case 'noiseProgress': $('noiseNote').textContent = 'Готовлю… ' + Math.round(m.p * 100) + '%'; break;
      case 'noiseReady': noiseUrl[m.mode] = m.url; if (m.mode === E.noise) swapMic(m.url); break;
      case 'exportStart': busy = true; wait('Сохраняю «' + m.name + '»…', 0, '', true); break;
      case 'exportProgress': if (busy) wait($('waitTitle').textContent, m.p, Math.round(m.p * 100) + '%', true); break;
      case 'exportCancelled': busy = false; unwait(); break;
      case 'exportError': busy = false; unwait(); GW.toast('Не сохранилось: ' + m.text); break;
      case 'exportDone':
        busy = false; unwait();
        $('doneTitle').textContent = 'Сохранено';
        $('doneText').textContent = m.name + ' · ' + (m.size > 1e9 ? (m.size / 1e9).toFixed(2) + ' ГБ' : Math.max(1, Math.round(m.size / 1e6)) + ' МБ');
        $('doneSheet').hidden = false;
        break;
      case 'toast': GW.toast(m.text); break;
      case 'error': unwait(); GW.toast(m.text); break;
    }
  }

  if (host) { host.addEventListener('message', (e) => onMsg(e.data)); addEventListener('DOMContentLoaded', () => send({ type: 'ready' })); }
  else addEventListener('DOMContentLoaded', () => {
    // открыто просто в браузере — образец
    onMsg({ type: 'init', dur: 754, width: 2560, height: 1440, fps: 30, hasSys: true, hasMic: true, encoder: 'h264_nvenc', codec: 'h264', rnn: true,
      encoders: [{ id: 'h264_nvenc', label: 'H.264 — видеокарта NVIDIA' }, { id: 'hevc_nvenc', label: 'HEVC (H.265) — видеокарта NVIDIA' }, { id: 'av1_nvenc', label: 'AV1 — видеокарта NVIDIA' }],
      started: '22 ноября 2026, 20:15', defaults: { sysVol: 100, micVol: 100, noise: 'rnn', format: 'mp4', quality: 'high', bitrate: 12, separate: false } });
    E.cuts = [[120, 150]]; sel = [300, 340]; renderTl();
    $('wait').hidden = true;
  });
})();
