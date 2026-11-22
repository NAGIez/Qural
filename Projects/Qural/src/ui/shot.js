// Qural — «Скриншоты»: клавиши, папка, формат, параметры снимка.
(() => {
  const $ = (id) => document.getElementById(id.startsWith('sh-') ? id : 'sh-' + id);
  const send = (m) => Q.send('shot', m);
  const FORMAT = { png: 'PNG — без потери качества', jpg: 'JPG — файлы меньше' };
  let S = null, capture = null;

  function keyText(btn, text) {
    if (btn === capture) return;
    btn.textContent = text || 'не задана';
    btn.classList.toggle('empty', !text);
  }

  function render(st) {
    S = st;
    keyText($('kRegion'), st.region); keyText($('kFull'), st.full); keyText($('kWindow'), st.window);
    document.querySelectorAll('#view-shot [data-clear]').forEach((b) => (b.hidden = !st[b.dataset.clear]));
    $('folder').textContent = st.folder;
    $('reset').hidden = !st.customFolder;
    $('formatV').textContent = FORMAT[st.format] || FORMAT.png;
    $('qualityRow').hidden = st.format !== 'jpg';
    const q = $('quality');
    if (document.activeElement !== q) q.value = st.quality;
    paintRange(q);
    $('ask').checked = st.ask; $('cursor').checked = st.cursor; $('keep').checked = st.keep; $('notify').checked = st.notify;
    $('lightshot').hidden = !st.lightshot;
    $('snip').hidden = !(st.snipping && st.usesPrtSc);
  }

  function paintRange(r) {
    const p = ((r.value - r.min) / (r.max - r.min)) * 100;
    r.style.setProperty('--p', p + '%');
    $('qualityOut').textContent = r.value + '%';
  }

  $('take').onclick = () => send({ type: 'take', mode: 'region' });
  $('open').onclick = () => send({ type: 'openFolder' });
  $('pick').onclick = () => send({ type: 'folder' });
  $('reset').onclick = () => send({ type: 'folderReset' });
  $('snipBtn').onclick = () => send({ type: 'snipSettings' });
  $('formatBtn').onclick = (e) => GW.menu(e.currentTarget,
    Object.keys(FORMAT).map((v) => ({ value: v, label: FORMAT[v], on: S && S.format === v })),
    (v) => send({ type: 'set', field: 'format', value: v }));
  const q = $('quality');
  q.addEventListener('input', () => paintRange(q));
  q.addEventListener('change', () => send({ type: 'set', field: 'quality', value: +q.value }));
  for (const [id, field] of [['ask', 'ask'], ['cursor', 'cursor'], ['keep', 'keep'], ['notify', 'notify']])
    $(id).addEventListener('change', (e) => send({ type: 'set', field, value: e.target.checked }));
  document.querySelectorAll('#view-shot [data-clear]').forEach((b) => (b.onclick = () => send({ type: 'hotkey', target: b.dataset.clear, key: 0 })));

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
  const code = (e, kc) => kc | (e.shiftKey ? 0x10000 : 0) | (e.ctrlKey ? 0x20000 : 0) | (e.altKey ? 0x40000 : 0);
  for (const id of ['kRegion', 'kFull', 'kWindow']) $(id).addEventListener('click', (e) => startCapture(e.currentTarget));

  addEventListener('keydown', (e) => {
    if (!capture || !capture.closest('#view-shot')) return;
    e.preventDefault(); e.stopPropagation();
    const mods = e.ctrlKey || e.shiftKey || e.altKey;
    if (MODS.includes(e.key)) {
      capture.textContent = [e.ctrlKey && 'Ctrl', e.shiftKey && 'Shift', e.altKey && 'Alt'].filter(Boolean).join('+') + '+…';
      return;
    }
    if (e.key === 'PrintScreen') return; // придёт в keyup
    const target = capture.dataset.target;
    if (e.key === 'Escape' && !mods) { stopCapture(); return; }
    if ((e.key === 'Backspace' || e.key === 'Delete') && !mods) { stopCapture(); send({ type: 'hotkey', target, key: 0 }); return; }
    const key = code(e, e.keyCode);
    stopCapture();
    send({ type: 'hotkey', target, key });
  }, true);
  // PrtSc в браузере приходит только при отпускании
  addEventListener('keyup', (e) => {
    if (!capture || !capture.closest('#view-shot')) return;
    if (MODS.includes(e.key)) { e.preventDefault(); return; }
    if (e.key !== 'PrintScreen') return;
    e.preventDefault();
    const target = capture.dataset.target;
    stopCapture();
    send({ type: 'hotkey', target, key: code(e, 44) });
  }, true);
  addEventListener('blur', stopCapture);
  document.addEventListener('pointerdown', (e) => { if (capture && e.target !== capture) stopCapture(); }, true);

  // ---------- связь ----------
  Q.on('shot', (m) => {
    if (m.type === 'state') { Q.state.shot = m.state; render(m.state); }
    else if (m.type === 'toast') GW.toast(m.text);
  });
  if (!Q.host) addEventListener('DOMContentLoaded', () => Q.emit({ mod: 'shot', type: 'state', state: {
    region: 'PrtSc', full: 'Shift+PrtSc', window: 'Ctrl+PrtSc', folder: 'C:\\Users\\Вы\\Pictures\\Qural', customFolder: false,
    format: 'png', quality: 90, cursor: false, keep: false, ask: true, notify: true, usesPrtSc: true, snipping: true, lightshot: true } }));
})();
