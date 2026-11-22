// Qural — вкладка «Звук».
(() => {
  const $ = (id) => document.getElementById('s-' + id);
  const root = document.getElementById('view-sapp');
  const send = (m) => Q.send('sound', m);
  const esc = Q.esc;

  const ACTIONS = [
    { label: 'Громче', hint: 'Повысить на шаг', icon: '<path d="M2.5 6v4h2.5l3.5 3V3L5 6z"/><path d="M11 5.5a3.5 3.5 0 010 5M12.8 3.8a6 6 0 010 8.4"/>' },
    { label: 'Тише', hint: 'Понизить на шаг', icon: '<path d="M2.5 6v4h2.5l3.5 3V3L5 6z"/><path d="M11 5.5a3.5 3.5 0 010 5"/>' },
    { label: 'Без звука', hint: 'Выключить, второе нажатие — вернуть', icon: '<path d="M2.5 6v4h2.5l3.5 3V3L5 6z"/><path d="M11 6l3.5 4M14.5 6L11 10"/>' },
  ];
  const svg = (inner, w) => `<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="${w || 1.6}" stroke-linecap="round" stroke-linejoin="round">${inner}</svg>`;
  const ICON_ON = svg(ACTIONS[0].icon), ICON_OFF = svg(ACTIONS[2].icon);
  const FOCUS_ICON = svg('<rect x="2" y="3" width="12" height="10" rx="2"/><path d="M2 6h12"/><path d="M7.5 8.5l1.2 3.5.6-1.4 1.4-.6z" fill="currentColor"/>', 1.4);
  const PRESETS = [1, 2, 5, 10];
  const FOCUSED = -2;

  let S = null, capture = null, dragging = null;
  const cur = () => (S ? (S.active === FOCUSED ? S.focused : S.apps[S.active]) : null);
  const iconHtml = (a) => a.icon ? `<img src="${a.icon}" alt="">` : `<span class="qr-icon">${esc((a.title || a.process || '?')[0].toUpperCase())}</span>`;
  const paint = (input, out, fmt) => {
    const min = +input.min, max = +input.max, v = +input.value;
    input.style.setProperty('--p', ((v - min) / (max - min)) * 100 + '%');
    out.textContent = fmt(v);
  };

  $('keys').innerHTML = ACTIONS.map((a, i) =>
    `<div class="gw-opt"><span class="qr-act">${svg(a.icon)}<span>${a.label}<small>${a.hint}</small></span></span>` +
    `<span class="qr-keywrap"><button class="qr-key" data-action="${i}"></button>` +
    `<button class="gw-ib" data-clear="${i}" title="Убрать клавишу">${svg('<path d="M4.5 4.5l7 7M11.5 4.5l-7 7"/>', 1.8)}</button></span></div>`).join('');
  $('presets').innerHTML = PRESETS.map((p) => `<button class="gw-pill" data-step="${p}">${p}%</button>`).join('');

  // ---------- отрисовка ----------
  function render(st) {
    S = st;
    renderList();
    const focused = st.active === FOCUSED;
    const a = cur();
    if (!a) return;

    $('removeBtn').hidden = focused;
    $('liveBlock').hidden = focused;
    $('namesBlock').hidden = focused;
    $('focusInfo').hidden = !focused;
    $('touchedBlock').hidden = !focused;
    if (focused) renderTouched();
    if (focused) {
      $('aIcon').innerHTML = FOCUS_ICON;
      $('aTitle').textContent = 'Активное окно';
      $('aSub').textContent = 'программа, окно которой в фокусе в момент нажатия';
    } else {
      $('aIcon').innerHTML = iconHtml(a);
      $('aTitle').textContent = a.title;
      renderLive(a);
      const names = a.names || [a.process];
      $('names').innerHTML = names.map((n) =>
        `<span class="gw-tag">${svg('<path d="M4 2.5h5.5L12.5 5.5v8H4z"/><path d="M9.5 2.5v3h3"/>')}${esc(n)}.exe` +
        (names.length > 1 ? `<button class="gw-ib" data-name="${esc(n)}" title="Убрать">${svg('<path d="M4.5 4.5l7 7M11.5 4.5l-7 7"/>', 2)}</button>` : '') + '</span>').join('');
    }
    root.querySelectorAll('.qr-key').forEach((b) => keyText(b, a.keys[+b.dataset.action]));
    root.querySelectorAll('[data-clear]').forEach((b) => (b.hidden = !a.keys[+b.dataset.clear]));
    if ($('stepRange') !== dragging) $('stepRange').value = a.step;
    paint($('stepRange'), $('stepOut'), (v) => v + '%');
    root.querySelectorAll('[data-step]').forEach((b) => b.classList.toggle('tint', +b.dataset.step === a.step));
    $('repeat').checked = a.repeat;
  }

  function chip(a) {
    if (!a.running) return '';
    return a.muted ? '<span class="gw-chip red">выкл</span>' : `<span class="gw-chip gray">${a.volume}%</span>`;
  }

  // список на странице «Звук»: «Активное окно» и программы
  function renderList() {
    if (!S) return;
    const keysOf = (k) => k.filter(Boolean).length;
    Q.setHTML($('list'),
      `<li data-i="${FOCUSED}"><span class="qr-kic">${FOCUS_ICON}</span><div class="gw-t"><b>Активное окно</b><small>программа в фокусе · клавиш: ${keysOf(S.focused.keys)} из 3</small></div></li>` +
      S.apps.map((a, i) => `<li data-i="${i}"><span class="qr-kic">${iconHtml(a)}</span><div class="gw-t"><b>${esc(a.title)}</b>` +
        `<small>${(a.names || [a.process]).map((n) => esc(n) + '.exe').join(', ')} · клавиш: ${keysOf(a.keys)} из 3</small></div>${chip(a)}</li>`).join(''));
  }
  $('list').addEventListener('click', (e) => { const li = e.target.closest('li[data-i]'); if (li) Q.openSoundApp(+li.dataset.i); });

  function renderLive(a) {
    $('aSub').textContent = (a.names || [a.process]).map((n) => n + '.exe').join(', ') + ' · ' + (a.running ? (a.muted ? 'без звука' : 'в микшере') : 'сейчас без звука');
    $('notRunning').hidden = a.running;
    const r = $('volRange');
    r.disabled = !a.running;
    if (r !== dragging) r.value = a.volume;
    paint(r, $('volOut'), (v) => (a.running ? v + '%' : '—'));
    const mb = $('muteBtn');
    mb.disabled = !a.running;
    mb.classList.toggle('muted', a.running && a.muted);
    mb.innerHTML = a.running && a.muted ? ICON_OFF : ICON_ON;
    mb.title = a.muted ? 'Вернуть звук' : 'Без звука';
  }

  function keyText(btn, text) {
    if (btn === capture) return;
    btn.textContent = text || 'не задана';
    btn.classList.toggle('empty', !text);
  }

  function live(levels, fallback, touched) {
    $('fallbackCard').hidden = !fallback;
    if (!S) return;
    levels.forEach((l, i) => { if (S.apps[i]) Object.assign(S.apps[i], l); });
    if (touched && S.touched) touched.forEach((l) => { const t = S.touched.find((x) => x.process === l.process); if (t) Object.assign(t, l); });
    renderList();
    if (S.active >= 0 && S.apps[S.active]) renderLive(S.apps[S.active]);
    if (S.active === FOCUSED) renderTouched();
  }

  // ---------- изменённые через «Активное окно» ----------
  // Строки собираются один раз (Q.setHTML по именам), а уровни громкости обновляются на месте —
  // иначе ползунок пересоздавался бы под пальцем каждую секунду.
  const MORE = '<svg viewBox="0 0 16 16" fill="currentColor"><circle cx="3.5" cy="8" r="1.3"/><circle cx="8" cy="8" r="1.3"/><circle cx="12.5" cy="8" r="1.3"/></svg>';
  function renderTouched() {
    const list = (S && S.touched) || [];
    Q.setHTML($('touched'), list.length ? list.map((t) =>
      `<li data-name="${esc(t.process)}"><span class="qr-kic">${iconHtml(t)}</span>` +
      `<div class="gw-t"><b>${esc(t.title)}</b><small></small></div>` +
      '<input type="range" class="qr-range" min="0" max="100" step="1"><output></output>' +
      '<button class="gw-pill" data-t="reset" title="Вернуть громкость 100%">100%</button>' +
      `<button class="gw-ib" data-t="more" title="Ещё" aria-haspopup="menu">${MORE}</button></li>`).join('')
      : '<li class="empty-row">Пока ни одной — здесь появятся программы, которым вы меняли громкость клавишами «Активного окна».</li>');
    $('touched').querySelectorAll('li[data-name]').forEach((li) => {
      const t = list.find((x) => x.process === li.dataset.name); if (!t) return;
      const r = li.querySelector('input'), o = li.querySelector('output');
      li.classList.toggle('idle', !t.running);
      li.querySelector('small').textContent = t.process + '.exe · ' + (t.running ? (t.muted ? 'без звука' : 'в микшере') : 'сейчас без звука — громкость видна, когда программа играет');
      r.disabled = !t.running;
      if (r !== dragging) r.value = t.volume;
      paint(r, o, (v) => (t.running ? (t.muted ? 'выкл' : v + '%') : '—'));
    });
  }
  let touchRaf = 0, touchPending = null;
  $('touched').addEventListener('pointerdown', (e) => { if (e.target.matches('input')) dragging = e.target; });
  $('touched').addEventListener('input', (e) => {
    const li = e.target.closest('li[data-name]'); if (!li) return;
    const t = S.touched.find((x) => x.process === li.dataset.name); if (t) t.volume = +e.target.value;
    paint(e.target, li.querySelector('output'), (v) => v + '%');
    touchPending = { type: 'touchedSet', name: li.dataset.name, value: +e.target.value };
    if (!touchRaf) touchRaf = requestAnimationFrame(() => { touchRaf = 0; send(touchPending); });
  });
  $('touched').addEventListener('click', (e) => {
    const b = e.target.closest('[data-t]'); if (!b) return;
    const name = b.closest('li').dataset.name;
    if (b.dataset.t === 'reset') send({ type: 'touchedReset', name });
    else GW.menu(b, [
      { value: 'adopt', label: 'Сделать отдельной программой', hint: 'Своя строка в списке и свои клавиши' },
      '-',
      { value: 'forget', label: 'Убрать из списка', hint: 'Громкость останется как есть', danger: true },
    ], (v) => { if (v === 'adopt') { send({ type: 'touchedAdopt', name }); Q.show('sapp'); } else send({ type: 'touchedForget', name }); });
  });

  // ---------- громкость сейчас ----------
  let volRaf = 0;
  $('volRange').addEventListener('pointerdown', (e) => (dragging = e.currentTarget));
  $('volRange').addEventListener('input', (e) => {
    const a = S && S.apps[S.active]; if (!a) return;
    a.volume = +e.target.value; paint(e.target, $('volOut'), (v) => v + '%');
    if (!volRaf) volRaf = requestAnimationFrame(() => { volRaf = 0; send({ type: 'volume', value: +$('volRange').value }); });
  });
  $('muteBtn').onclick = () => send({ type: 'mute' });

  // ---------- шаг ----------
  $('stepRange').addEventListener('pointerdown', (e) => (dragging = e.currentTarget));
  $('stepRange').addEventListener('input', (e) => {
    const a = cur(); if (!a) return;
    a.step = +e.target.value; paint(e.target, $('stepOut'), (v) => v + '%');
    root.querySelectorAll('[data-step]').forEach((b) => b.classList.toggle('tint', +b.dataset.step === a.step));
    send({ type: 'step', value: a.step });
  });
  $('presets').addEventListener('click', (e) => {
    const b = e.target.closest('[data-step]'); if (!b) return;
    $('stepRange').value = b.dataset.step; $('stepRange').dispatchEvent(new Event('input'));
  });
  $('repeat').addEventListener('change', (e) => send({ type: 'repeat', value: e.target.checked }));
  addEventListener('pointerup', () => (dragging = null));

  // ---------- добавить / убрать ----------
  function openAdd() {
    $('sessions').innerHTML = '<li><span class="gw-ring"></span><div class="gw-t"><small>Смотрю, что сейчас в микшере…</small></div></li>';
    $('manual').value = '';
    GW.sheet($('addSheet'));
    send({ type: 'sessions' });
  }
  Q.addSound = openAdd;
  $('addBtn2').onclick = openAdd;
  $('addSheet').querySelectorAll('[data-close]').forEach((b) => (b.onclick = () => GW.sheet.close($('addSheet'))));
  function showSessions(list) {
    $('sessions').innerHTML = list.length ? list.map((s, i) =>
      `<li data-i="${i}" class="${s.added ? 'added' : ''}">${iconHtml(s)}<div class="gw-t"><b>${esc(s.title)}</b><small>${esc(s.process)}.exe · ${s.volume}%</small></div>` +
      (s.added ? '<span class="gw-chip green">уже добавлена</span>' : '<span class="gw-chip">добавить</span>') + '</li>').join('')
      : '<li><div class="gw-t"><small>Ни одна программа сейчас не выводит звук. Включите в нужной звук или впишите имя ниже.</small></div></li>';
    $('sessions').onclick = (e) => {
      const li = e.target.closest('li[data-i]'); if (!li) return;
      const s = list[+li.dataset.i]; if (s.added) return;
      GW.sheet.close($('addSheet'));
      send({ type: 'add', process: s.process, path: s.path }); Q.show('sapp');
    };
  }
  $('manualForm').addEventListener('submit', (e) => {
    e.preventDefault();
    const v = $('manual').value.trim(); if (!v) { $('manual').focus(); return; }
    GW.sheet.close($('addSheet'));
    send({ type: 'add', process: v }); Q.show('sapp');
  });
  $('removeBtn').onclick = () => send({ type: 'remove' });

  // ---------- имена exe ----------
  $('nameForm').addEventListener('submit', (e) => {
    e.preventDefault();
    const v = $('nameInput').value.trim(); if (!v) { $('nameInput').focus(); return; }
    $('nameInput').value = '';
    send({ type: 'addName', name: v });
  });
  $('names').addEventListener('click', (e) => {
    const b = e.target.closest('[data-name]'); if (b) send({ type: 'removeName', name: b.dataset.name });
  });

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
  $('keys').addEventListener('click', (e) => {
    const k = e.target.closest('.qr-key'); if (k) { startCapture(k); return; }
    const c = e.target.closest('[data-clear]'); if (c) send({ type: 'hotkey', action: +c.dataset.clear, key: 0 });
  });
  addEventListener('keydown', (e) => {
    if (!capture) return;
    e.preventDefault(); e.stopPropagation();
    const mods = e.ctrlKey || e.shiftKey || e.altKey;
    if (MODS.includes(e.key)) {
      capture.textContent = [e.ctrlKey && 'Ctrl', e.shiftKey && 'Shift', e.altKey && 'Alt'].filter(Boolean).join('+') + '+…';
      return;
    }
    const action = +capture.dataset.action;
    if (e.key === 'Escape' && !mods) { stopCapture(); return; }
    if ((e.key === 'Backspace' || e.key === 'Delete') && !mods) { stopCapture(); send({ type: 'hotkey', action, key: 0 }); return; }
    const key = e.keyCode | (e.shiftKey ? 0x10000 : 0) | (e.ctrlKey ? 0x20000 : 0) | (e.altKey ? 0x40000 : 0);
    stopCapture();
    send({ type: 'hotkey', action, key });
  }, true);
  addEventListener('keyup', (e) => { if (capture && MODS.includes(e.key)) e.preventDefault(); }, true);
  addEventListener('blur', stopCapture);
  document.addEventListener('pointerdown', (e) => { if (capture && e.target !== capture) stopCapture(); }, true);

  // ---------- связь ----------
  Q.on('sound', (m) => {
    if (m.type === 'state') render(m.state);
    else if (m.type === 'local') render(S);   // выбрали другую программу в меню — показать её сразу
    else if (m.type === 'live') live(m.levels, m.fallback, m.touched);
    else if (m.type === 'sessions') showSessions(m.list);
    else if (m.type === 'toast') GW.toast(m.text, m.undo ? { action: 'Вернуть', onAction: () => send({ type: 'restore' }), ms: 6000 } : {});
  });
  if (!Q.host) addEventListener('DOMContentLoaded', () => Q.emit({ mod: 'sound', type: 'state', state: {
    apps: [
      { process: 'Spotify', names: ['Spotify'], title: 'Spotify', icon: '', keys: ['Ctrl+Alt+F10', 'Ctrl+Alt+F9', 'Ctrl+Alt+F11'], step: 5, repeat: true, running: true, volume: 100, muted: false },
      { process: 'Discord', names: ['Discord'], title: 'Discord', icon: '', keys: ['F18', 'F19', 'F20'], step: 5, repeat: true, running: false, volume: 0, muted: false },
    ], focused: { keys: ['F16', 'F15', 'F17'], step: 5, repeat: true },
    touched: [
      { process: 'chrome', title: 'Google Chrome', icon: '', running: true, volume: 40, muted: false },
      { process: 'Telegram', title: 'Telegram', icon: '', running: false, volume: 0, muted: false },
    ], active: Number(new URLSearchParams(location.search).get('sa') || 0),
  } }));
})();
