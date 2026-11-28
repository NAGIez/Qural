// Qural — «Главная»: состояние инструментов, быстрые действия, все сочетания клавиш одним списком.
(() => {
  const $ = (id) => document.getElementById(id);
  const esc = Q.esc;
  const ACT = ['громче', 'тише', 'без звука'];
  const ACT_ICON = [
    '<path d="M2.5 6v4h2.5l3.5 3V3L5 6z"/><path d="M11 5.5a3.5 3.5 0 010 5M12.8 3.8a6 6 0 010 8.4"/>',
    '<path d="M2.5 6v4h2.5l3.5 3V3L5 6z"/><path d="M11 5.5a3.5 3.5 0 010 5"/>',
    '<path d="M2.5 6v4h2.5l3.5 3V3L5 6z"/><path d="M11 6l3.5 4M14.5 6L11 10"/>',
  ];
  const svg = (inner) => `<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">${inner}</svg>`;
  const kbds = (t) => String(t).split('+').filter(Boolean).map((k) => `<span class="qr-kbd">${esc(k)}</span>`).join('');
  let rows = [];

  function render() {
    if (Q.view !== 'home') return;
    const g = Q.state.shell || {}, c = Q.state.color, s = Q.state.sound, sh = Q.state.shot, rc = Q.state.rec;

    // ── инструменты ──
    const cp = c && c.profiles[c.active];
    const playing = s ? s.apps.filter((a) => a.running) : [];
    const card = (mod, on, title, text) =>
      `<div class="gw-card qr-mod${on ? '' : ' off'}"><div class="qr-mod-head"><span class="qr-mod-ic">${Q.svg(mod, '')}</span><b>${title}</b>` +
      `<span class="gw-switch"><input type="checkbox" data-module="${mod}"${on ? ' checked' : ''}></span></div>` +
      `<p>${text}</p><button class="gw-link" data-go="${mod}">Настроить<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"><path d="M6 3.5L10.5 8 6 12.5"/></svg></button></div>`;
    Q.setHTML($('h-modules'),
      card('color', g.colorOn !== false, 'Цвет', g.colorOn === false ? 'Выключен — мониторы на стандартных значениях, клавиши цвета не работают.'
        : c ? `Включён профиль «${esc(cp ? cp.name : '')}» · мониторов: ${c.monitors.length} · профилей: ${c.profiles.length}` : '…') +
      card('sound', g.soundOn !== false, 'Звук', g.soundOn === false ? 'Выключен — клавиши звука не работают.'
        : s ? `Программ: ${s.apps.length}` + (playing.length ? ' · в микшере: ' + playing.map((a) => `${esc(a.title)} ${a.muted ? 'выкл' : a.volume + '%'}`).join(', ') : ' · сейчас ни одна не в микшере') : '…') +
      card('shot', g.shotOn !== false, 'Скриншоты', g.shotOn === false ? 'Выключены — клавиши скриншотов свободны.'
        : sh ? (sh.region ? `Область — ${esc(sh.region)}` : 'Клавиша не задана') + ' · файлы: ' + esc(sh.folder) : '…') +
      card('rec', g.recOn !== false, 'Запись экрана', g.recOn === false ? 'Выключена — клавиши записи свободны.'
        : rc ? (rc.recording ? 'Идёт запись' : (rc.toggle ? `Начать — ${esc(rc.toggle)}` : 'Клавиша не задана') + ' · ' + esc(rc.codec === 'auto' ? rc.auto : rc.codec)) : '…') +
      (g.netInstalled ? card('net', g.netOn !== false, 'Сеть', g.netOn === false ? 'Выключена — интернет идёт через одно подключение.'
        : 'Все подключения сразу: кабель, Wi-Fi и телефон — в одно. Скорость каждого и трафик за день и месяц.') : ''));

    // ── быстрые действия ──
    let q = '';
    if (c && g.colorOn !== false) {
      q += c.profiles.map((p, i) => `<button class="gw-pill${i === c.active ? ' tint' : ''}" data-profile="${i}">${esc(p.name)}</button>`).join('');
      q += '<button class="gw-pill" data-act="resetAll">Сбросить мониторы</button>';
    } else q += '<span class="gw-sub">Включите «Цвет», чтобы переключать профили отсюда.</span>';
    if (g.shotOn !== false) q += '<button class="gw-pill" data-act="shot">Скриншот</button>';
    if (g.recOn !== false) q += rc && rc.recording ? '<button class="gw-pill" data-act="recStop">Остановить запись</button>' : '<button class="gw-pill" data-act="rec">Запись экрана</button>';
    Q.setHTML($('h-quick'), q);

    // ── все сочетания клавиш ──
    rows = [];
    if (c) {
      c.profiles.forEach((p, i) => p.hotkey && rows.push({ icon: Q.svg('profile', ''), title: `Включить профиль «${p.name}»`, sub: 'Цвет', key: p.hotkey, off: g.colorOn === false, go: () => Q.openColorProfile(i) }));
      if (c.next) rows.push({ icon: Q.svg('color', ''), title: 'Следующий профиль цвета', sub: 'Цвет', key: c.next, off: g.colorOn === false, go: () => Q.show('color') });
    }
    if (s) {
      s.focused.keys.forEach((k, a) => k && rows.push({ icon: svg(ACT_ICON[a]), title: 'Активное окно · ' + ACT[a], sub: 'Звук', key: k, off: g.soundOn === false, go: () => Q.openSoundApp(-2) }));
      s.apps.forEach((app, i) => app.keys.forEach((k, a) => k && rows.push({ icon: Q.icon(app), title: app.title + ' · ' + ACT[a], sub: 'Звук', key: k, off: g.soundOn === false, go: () => Q.openSoundApp(i) })));
    }
    if (sh) [['region', 'Скриншот · выделить область'], ['full', 'Скриншот · весь экран'], ['window', 'Скриншот · окно']].forEach(([k, t]) =>
      sh[k] && rows.push({ icon: Q.svg('shot', ''), title: t, sub: 'Скриншоты', key: sh[k], off: g.shotOn === false, go: () => Q.show('shot') }));
    if (rc) [['toggle', 'Запись · начать / остановить'], ['pause', 'Запись · пауза']].forEach(([k, t]) =>
      rc[k] && rows.push({ icon: Q.svg('rec', ''), title: t, sub: 'Запись экрана', key: rc[k], off: g.recOn === false, go: () => Q.show('rec') }));
    Q.setHTML($('h-keys'), rows.length
      ? rows.map((r, i) => `<li data-r="${i}" class="${r.off ? 'off' : ''}"><span class="qr-kic">${r.icon}</span><div class="gw-t"><b>${esc(r.title)}</b><small>${r.sub}${r.off ? ' · выключен' : ''}</small></div><span class="qr-kbds">${kbds(r.key)}</span></li>`).join('')
      : '<li class="empty-row">Пока ни одной клавиши — назначьте их на страницах «Цвет» и «Звук».</li>');

    const busy = (g.busy || '').trim();
    const cf = $('h-conflicts');
    cf.className = 'qr-conflicts ' + (busy ? 'bad' : 'ok');
    cf.textContent = busy ? 'Заняты другой программой: ' + busy : 'Конфликтов нет';
  }

  $('h-keys').addEventListener('click', (e) => { const li = e.target.closest('li[data-r]'); if (li && rows[+li.dataset.r]) rows[+li.dataset.r].go(); });
  $('h-modules').addEventListener('click', (e) => { const b = e.target.closest('[data-go]'); if (b) Q.show(b.dataset.go); });
  $('h-quick').addEventListener('click', (e) => {
    const p = e.target.closest('[data-profile]');
    if (p) { Q.send('color', { type: 'select', index: +p.dataset.profile }); return; }
    if (e.target.closest('[data-act="resetAll"]')) Q.send('color', { type: 'resetAll' });
    if (e.target.closest('[data-act="shot"]')) Q.send('shot', { type: 'take', mode: 'region' });
    if (e.target.closest('[data-act="rec"]')) Q.send('rec', { type: 'start', mode: 'region' });
    if (e.target.closest('[data-act="recStop"]')) Q.send('rec', { type: 'stop' });
  });

  const later = () => queueMicrotask(render);
  Q.on('shell', (m) => m.type === 'state' && later());
  Q.on('color', (m) => m.type === 'state' && later());
  Q.on('sound', (m) => (m.type === 'state' || m.type === 'live') && later());
  Q.on('shot', (m) => m.type === 'state' && later());
  Q.on('rec', (m) => m.type === 'state' && later());
  Q.on('view', later);
})();
