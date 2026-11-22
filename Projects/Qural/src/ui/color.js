// Qural — вкладка «Цвет».
(() => {
  const $ = (id) => document.getElementById('c-' + id);
  const root = document.getElementById('view-cprofile');
  const send = (m) => Q.send('color', m);
  const esc = Q.esc;

  const FIELDS = [
    { f: 'brightness', label: 'Яркость', min: 0, max: 100, step: 1, def: 50, fmt: (v) => Math.round(v) + '%' },
    { f: 'contrast', label: 'Контрастность', min: 0, max: 100, step: 1, def: 50, fmt: (v) => Math.round(v) + '%' },
    { f: 'gamma', label: 'Гамма', min: 0.3, max: 2.8, step: 0.01, def: 1, fmt: (v) => (+v).toFixed(2) },
    { f: 'vibrance', label: 'Цифровая интенсивность', min: 0, max: 100, step: 1, def: 50, nv: true, fmt: (v) => Math.round(v) + '%' },
    { f: 'hue', label: 'Оттенок', min: 0, max: 359, step: 1, def: 0, nv: true, fmt: (v) => Math.round(v) + '°' },
  ];

  let S = null, mon = 0, capture = null, dragging = null;

  // ---------- ползунки ----------
  $('sliders').innerHTML = FIELDS.map((x) =>
    `<label class="qr-crow" data-f="${x.f}"><span>${x.label}</span>` +
    `<input type="range" class="qr-range" min="${x.min}" max="${x.max}" step="${x.step}"><output></output></label>`).join('');
  const rows = FIELDS.map((x) => {
    const row = root.querySelector(`.qr-crow[data-f="${x.f}"]`);
    return { ...x, row, input: row.querySelector('input'), out: row.querySelector('output') };
  });
  const paint = (r) => {
    const v = +r.input.value;
    r.input.style.setProperty('--p', ((v - r.min) / (r.max - r.min)) * 100 + '%');
    r.out.textContent = r.fmt(v);
  };
  const pending = {};
  let raf = 0;
  function push(r) {
    const m = S && S.monitors[mon]; if (!m) return;
    const v = +r.input.value; m[r.f] = v; paint(r);
    pending[r.f] = { type: 'set', key: m.key, field: r.f, value: v };
    if (!raf) raf = requestAnimationFrame(() => { raf = 0; Object.values(pending).forEach(send); for (const k in pending) delete pending[k]; });
  }
  for (const r of rows) {
    r.input.addEventListener('input', () => push(r));
    r.input.addEventListener('pointerdown', () => (dragging = r.input));
    r.input.addEventListener('dblclick', () => { r.input.value = r.def; push(r); });
  }
  addEventListener('pointerup', () => (dragging = null));

  // ---------- отрисовка ----------
  function render(st) {
    S = st;
    // список профилей на странице «Цвет»
    Q.setHTML($('list'), st.profiles.map((p, i) =>
      `<li data-i="${i}"><span class="qr-kic">${Q.svg('profile', '')}</span><div class="gw-t"><b>${esc(p.name)}</b>` +
      `<small>${i === st.active ? 'включён сейчас' : 'щёлкните, чтобы включить и настроить'}</small></div>` +
      (p.hotkey ? `<span class="qr-kbds">${kbds(p.hotkey)}</span>` : '<span class="gw-chip gray">без клавиши</span>') + '</li>').join(''));
    const p = st.profiles[st.active] || { name: '', hotkey: '' };
    $('pName').textContent = p.name || ' ';
    $('upBtn').disabled = st.active <= 0;
    $('downBtn').disabled = st.active >= st.profiles.length - 1;
    $('delBtn').disabled = st.profiles.length <= 1;
    keyText($('pKey'), p.hotkey); $('pKeyClear').hidden = !p.hotkey;
    keyText($('nKey'), st.next); $('nKeyClear').hidden = !st.next;
    $('osd').checked = st.osd;

    if (mon >= st.monitors.length) mon = 0;
    renderTabs();
    renderMonitor();
    $('nvNote').hidden = st.nvapi;
  }

  function renderMonitor() {
    const m = S && S.monitors[mon];
    $('monCard').hidden = !m;
    if (!m) return;
    $('monOn').checked = m.enabled;
    for (const r of rows) {
      const off = !m.enabled || (!!r.nv && !S.nvapi);
      r.input.disabled = off;
      r.row.classList.toggle('off', off);
      if (r.input !== dragging) r.input.value = m[r.f];
      paint(r);
    }
    $('resetMon').disabled = !m.enabled;
  }

  function keyText(btn, text) {
    if (btn === capture) return;
    btn.textContent = text || 'не задана';
    btn.classList.toggle('empty', !text);
  }

  // клавиши сочетания по отдельности: «Ctrl+Alt+F21» → Ctrl · Alt · F21
  function kbds(text) { return String(text).split('+').filter(Boolean).map((k) => `<span class="qr-kbd">${esc(k)}</span>`).join(''); }
  Q.kbds = kbds;

  // ---------- профили ----------
  $('list').addEventListener('click', (e) => {
    const li = e.target.closest('li[data-i]'); if (li) Q.openColorProfile(+li.dataset.i);
  });
  const addProfile = () => askName('Новый профиль', 'Профиль ' + ((S ? S.profiles.length : 0) + 1), (name) => { send({ type: 'add', name }); Q.show('cprofile'); });
  Q.addColor = addProfile;
  $('addBtn2').onclick = addProfile;
  $('renameBtn').onclick = () => S && askName('Переименовать профиль', S.profiles[S.active].name, (name) => send({ type: 'rename', name }));
  $('copyBtn').onclick = () => send({ type: 'copy' });
  $('upBtn').onclick = () => send({ type: 'move', dir: -1 });
  $('downBtn').onclick = () => send({ type: 'move', dir: 1 });
  $('delBtn').onclick = () => send({ type: 'delete' });

  let nameDone = null;
  function askName(title, value, done) {
    $('nameTitle').textContent = title;
    $('nameInput').value = value;
    nameDone = done;
    GW.sheet($('nameSheet'));
    setTimeout(() => $('nameInput').select(), 30);
  }
  $('nameForm').addEventListener('submit', (e) => {
    e.preventDefault();
    const v = $('nameInput').value.trim();
    if (!v) { $('nameInput').focus(); return; }
    GW.sheet.close($('nameSheet'));
    nameDone && nameDone(v); nameDone = null;
  });
  $('nameSheet').querySelectorAll('[data-close]').forEach((b) => (b.onclick = () => GW.sheet.close($('nameSheet'))));

  // ---------- мониторы ----------
  // вкладки мониторов: одна функция и для обновления, и для щелчка — тогда запомненный HTML всегда совпадает с экраном
  function renderTabs() {
    if (!S) return;
    const tabs = $('monTabs');
    Q.setHTML(tabs, S.monitors.map((m, i) => `<button data-i="${i}" class="${i === mon ? 'on' : ''}">${esc(m.name)}</button>`).join(''));
    GW.seg(tabs);
  }
  $('monTabs').addEventListener('click', (e) => {
    const b = e.target.closest('button[data-i]'); if (!b) return;
    mon = +b.dataset.i;
    renderTabs();
    renderMonitor();
  });
  $('monOn').addEventListener('change', (e) => {
    const m = S && S.monitors[mon]; if (!m) return;
    m.enabled = e.target.checked; renderMonitor();
    send({ type: 'enabled', key: m.key, value: m.enabled });
  });
  $('resetMon').onclick = () => { const m = S && S.monitors[mon]; if (m) send({ type: 'resetMonitor', key: m.key }); };

  // ---------- ещё ----------
  $('osd').addEventListener('change', (e) => send({ type: 'osd', value: e.target.checked }));
  $('resetAll').onclick = () => send({ type: 'resetAll' });

  // ---------- горячие клавиши ----------
  const MODS = ['Control', 'Shift', 'Alt', 'Meta', 'AltGraph'];
  function startCapture(btn) {
    if (capture === btn) return;
    stopCapture();
    capture = btn;
    btn.classList.add('rec');
    btn.textContent = 'нажмите сочетание…';
    Q.send('shell', { type: 'captureStart' });
  }
  function stopCapture() {
    if (!capture) return;
    capture.classList.remove('rec'); capture = null;
    Q.send('shell', { type: 'captureEnd' });
    if (S) render(S);
  }
  for (const id of ['pKey', 'nKey']) $(id).addEventListener('click', (e) => startCapture(e.currentTarget));
  $('pKeyClear').onclick = () => send({ type: 'hotkey', target: 'profile', key: 0 });
  $('nKeyClear').onclick = () => send({ type: 'hotkey', target: 'next', key: 0 });

  addEventListener('keydown', (e) => {
    if (!capture) return;
    e.preventDefault(); e.stopPropagation();
    const mods = e.ctrlKey || e.shiftKey || e.altKey;
    if (MODS.includes(e.key)) {
      capture.textContent = [e.ctrlKey && 'Ctrl', e.shiftKey && 'Shift', e.altKey && 'Alt'].filter(Boolean).join('+') + '+…';
      return;
    }
    const target = capture.dataset.target;
    if (e.key === 'Escape' && !mods) { stopCapture(); return; }
    if ((e.key === 'Backspace' || e.key === 'Delete') && !mods) { stopCapture(); send({ type: 'hotkey', target, key: 0 }); return; }
    const key = e.keyCode | (e.shiftKey ? 0x10000 : 0) | (e.ctrlKey ? 0x20000 : 0) | (e.altKey ? 0x40000 : 0);
    stopCapture();
    send({ type: 'hotkey', target, key });
  }, true);
  addEventListener('keyup', (e) => { if (capture && MODS.includes(e.key)) e.preventDefault(); }, true);
  addEventListener('blur', stopCapture);
  document.addEventListener('pointerdown', (e) => { if (capture && e.target !== capture) stopCapture(); }, true);

  // ---------- связь ----------
  Q.on('color', (m) => {
    if (m.type === 'state') render(m.state);
    else if (m.type === 'toast') GW.toast(m.text, m.undo ? { action: 'Вернуть', onAction: () => send({ type: 'restore' }), ms: 6000 } : {});
  });
  if (!Q.host) addEventListener('DOMContentLoaded', () => Q.emit({ mod: 'color', type: 'state', state: {
    profiles: [{ name: 'Обычный', hotkey: 'Ctrl+Alt+F21' }, { name: 'Профиль 2', hotkey: 'Ctrl+Alt+F22' }], active: 0, next: 'Ctrl+Alt+0', osd: true, nvapi: true,
    monitors: [
      { key: 'a', name: 'Odyssey G60SD', enabled: true, brightness: 50, contrast: 50, gamma: 1, vibrance: 50, hue: 0 },
      { key: 'b', name: 'Mi Monitor', enabled: true, brightness: 50, contrast: 50, gamma: 1, vibrance: 50, hue: 0 },
    ],
  } }));
})();
