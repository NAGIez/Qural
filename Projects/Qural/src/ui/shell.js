// Qural — оболочка: связь с программой, меню слева, страницы, тема, своя шапка окна, «Общие», вкл/выкл инструментов.
(() => {
  const host = window.chrome && chrome.webview;
  const handlers = {};
  const $ = (id) => document.getElementById(id);
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

  // Q.send(mod, {...}) — сообщение программе; Q.on(mod, fn) — сообщения от неё; Q.state — последние состояния
  window.Q = {
    host: !!host,
    esc,
    state: { shell: null, color: null, sound: null, shot: null, rec: null },
    view: 'home',
    send(mod, m) { if (host) host.postMessage(Object.assign({ mod }, m)); },
    on(mod, fn) { (handlers[mod] = handlers[mod] || []).push(fn); },
    emit(m) { (handlers[m.mod] || []).forEach((fn) => fn(m)); },
    show, openColorProfile, openSoundApp,
    addColor: () => {}, addSound: () => {},   // задают color.js и sound.js
    icon(a) { return a.icon ? `<img src="${a.icon}" alt="">` : `<span class="qr-icon">${esc((a.title || a.process || '?')[0].toUpperCase())}</span>`; },
  };
  const send = (m) => Q.send('shell', m);

  const ICONS = {
    home: '<path d="M2.5 7.2L8 2.8l5.5 4.4V13a.5.5 0 01-.5.5H10V9.5H6v4H3a.5.5 0 01-.5-.5z"/>',
    color: '<rect x="1.5" y="2.5" width="13" height="8.5" rx="1.6"/><path d="M6 13.5h4M8 11v2.5"/>',
    sound: '<path d="M2.5 6v4h2.5l3.5 3V3L5 6z"/><path d="M11 5.5a3.5 3.5 0 010 5M12.8 3.8a6 6 0 010 8.4"/>',
    general: '<path d="M2.5 4.5h11M2.5 11.5h11"/><circle cx="6" cy="4.5" r="1.8" fill="var(--card)"/><circle cx="10.5" cy="11.5" r="1.8" fill="var(--card)"/>',
    focus: '<rect x="2" y="3" width="12" height="10" rx="2"/><path d="M2 6h12"/><path d="M7.5 8.5l1.2 3.5.6-1.4 1.4-.6z" fill="currentColor"/>',
    profile: '<circle cx="8" cy="8" r="5.5"/><path d="M8 2.5v11"/><path d="M8 2.5a5.5 5.5 0 010 11z" fill="currentColor"/>',
    rec: '<rect x="1.5" y="3.5" width="9.5" height="9" rx="1.6"/><path d="M11 7l3.5-2v6L11 9"/><circle cx="6.2" cy="8" r="1.6" fill="currentColor"/>',
    shot: '<path d="M2.5 5.5v-2a1 1 0 011-1h2M10.5 2.5h2a1 1 0 011 1v2M13.5 10.5v2a1 1 0 01-1 1h-2M5.5 13.5h-2a1 1 0 01-1-1v-2"/><circle cx="8" cy="8" r="2.2"/>',
  };
  Q.svg = (name, cls) => `<svg class="${cls || 'qr-ni'}" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">${ICONS[name]}</svg>`;
  const CHEV = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M4 6l4 4 4-4"/></svg>';
  const PLUS = '<svg class="qr-ni" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"><path d="M8 3v10M3 8h10"/></svg>';

  // ---------- страницы ----------
  const PAGE = { home: 'home', color: 'color', cprofile: 'color', sound: 'sound', sapp: 'sound', shot: 'shot', rec: 'rec', general: 'general' };
  function show(view) {
    const old = Q.view;
    Q.view = view;
    document.querySelectorAll('.qr-view').forEach((s) => (s.hidden = s.id !== 'view-' + view));
    document.querySelectorAll('#view-' + view + ' [data-seg]').forEach((g) => GW.seg(g));
    renderNav();
    if (old !== view) scrollTo({ top: 0, behavior: 'instant' });
    if (PAGE[old] !== PAGE[view] || !Q._pageSent) { Q._pageSent = true; send({ type: 'page', page: PAGE[view] }); }
    Q.emit({ mod: 'view', view });
  }
  function openColorProfile(i) {
    const c = Q.state.color;
    if (c && i !== c.active) Q.send('color', { type: 'select', index: i });
    show('cprofile');
  }
  function openSoundApp(i) {
    const s = Q.state.sound;
    if (s && i !== s.active) { s.active = i; Q.send('sound', { type: 'select', index: i }); Q.emit({ mod: 'sound', type: 'local' }); }
    show('sapp');
  }

  // ---------- меню слева ----------
  const closed = (g) => { try { return localStorage.getItem('qr-closed-' + g) === '1'; } catch { return false; } };
  const setClosed = (g, v) => { try { localStorage.setItem('qr-closed-' + g, v ? '1' : '0'); } catch {} };

  function selectedKey() {
    const c = Q.state.color, s = Q.state.sound;
    switch (Q.view) {
      case 'cprofile': return 'cp' + (c ? c.active : 0);
      case 'sapp': return 'sa' + (s ? s.active : -2);
      default: return Q.view;
    }
  }

  function renderNav() {
    const c = Q.state.color, s = Q.state.sound, g = Q.state.shell || {};
    const sel = selectedKey();
    const on = (k) => (k === sel ? ' class="on"' : '');
    const offChip = (isOn) => (isOn === false ? '<span class="gw-chip gray">выкл</span>' : '');
    let h = `<button data-view="home"${on('home')}>${Q.svg('home')}<span class="qr-pn">Главная</span></button>`;
    // выключенные инструменты в меню не показываем — включить их можно на «Главной» и в «Общих»
    if (g.colorOn !== false) h += `<div class="qr-group${closed('color') ? ' closed' : ''}${g.colorOn === false ? ' off' : ''}" data-group="color">` +
      `<button class="qr-ghead${sel === 'color' ? ' on' : ''}" data-view="color">${Q.svg('color')}<span class="qr-pn">Цвет</span>${offChip(g.colorOn)}<span class="qr-tw" data-toggle="color" title="Свернуть / развернуть">${CHEV}</span></button>` +
      '<div class="qr-kids">' +
      (c ? c.profiles.map((p, i) => `<button data-cp="${i}"${on('cp' + i)}><span class="qr-pn">${esc(p.name)}</span></button>`).join('') : '') +
      `<button class="qr-addrow" data-add="color">${PLUS}<span class="qr-pn">Добавить профиль</span></button>` +
      '</div></div>';

    if (g.soundOn !== false) h += `<div class="qr-group${closed('sound') ? ' closed' : ''}${g.soundOn === false ? ' off' : ''}" data-group="sound">` +
      `<button class="qr-ghead${sel === 'sound' ? ' on' : ''}" data-view="sound">${Q.svg('sound')}<span class="qr-pn">Звук</span>${offChip(g.soundOn)}<span class="qr-tw" data-toggle="sound" title="Свернуть / развернуть">${CHEV}</span></button>` +
      '<div class="qr-kids">' +
      `<button data-sa="-2"${on('sa-2')}><span class="qr-pn">Активное окно</span>` +
        (s && s.touched && s.touched.length ? `<span class="gw-chip gray" title="Программ с изменённой громкостью">${s.touched.length}</span>` : '') + '</button>' +
      (s ? s.apps.map((a, i) => `<button data-sa="${i}"${on('sa' + i)}>${Q.icon(a)}<span class="qr-pn">${esc(a.title)}</span>` +
        (a.running ? (a.muted ? '<span class="gw-chip red">выкл</span>' : `<span class="gw-chip gray">${a.volume}%</span>`) : '') + '</button>').join('') : '') +
      `<button class="qr-addrow" data-add="sound">${PLUS}<span class="qr-pn">Добавить программу</span></button>` +
      '</div></div>';

    if (g.shotOn !== false) h += `<button data-view="shot"${on('shot')}>${Q.svg('shot')}<span class="qr-pn">Скриншоты</span>${offChip(g.shotOn)}</button>`;
    if (g.recOn !== false || g.recording) h += `<button data-view="rec"${on('rec')}>${Q.svg('rec')}<span class="qr-pn">Запись экрана</span>${g.recording ? '<span class="gw-chip red">REC</span>' : offChip(g.recOn)}</button>`;
    h += '<div class="qr-sep"></div>';
    h += `<button data-view="general"${on('general')}>${Q.svg('general')}<span class="qr-pn">Общие</span></button>`;
    // Пункты лежат во внутреннем блоке, а подсветка выбранного (бегунок GlassWise) — рядом с ним в #side:
    // перерисовка пунктов её не пересоздаёт, она просто переезжает. И перерисовываем, только если что-то изменилось —
    // раньше меню собиралось заново каждую секунду (обновление громкости) и подсветка мигала.
    if (Q.setHTML(sideItems(), h)) GW.seg($('side'));
  }

  function sideItems() {
    let box = $('sideItems');
    if (!box) { box = document.createElement('div'); box.id = 'sideItems'; box.className = 'qr-sideitems'; $('side').append(box); }
    return box;
  }

  // Заменить содержимое, только если HTML действительно другой. Возвращает true, если заменили.
  // Если el — переключатель GlassWise, его бегунок (.seg-thumb) возвращаем на место в том же проходе:
  // он сохраняет положение и видимость и просто переезжает к новому выбранному пункту, без мигания.
  Q.setHTML = (el, html) => {
    if (el.__qrHtml === html) return false;
    el.__qrHtml = html;
    const thumb = el.querySelector(':scope > .seg-thumb');
    el.innerHTML = html;
    if (thumb) el.prepend(thumb);
    return true;
  };

  $('side').addEventListener('click', (e) => {
    const t = e.target.closest('[data-toggle]');
    if (t) {
      e.stopPropagation();
      const grp = t.dataset.toggle, box = t.closest('.qr-group');
      const now = !box.classList.contains('closed');
      box.classList.toggle('closed', now); setClosed(grp, now);
      sideItems().__qrHtml = null; // класс поменяли руками — при следующей отрисовке собрать заново
      GW.seg($('side'));
      return;
    }
    const b = e.target.closest('button'); if (!b) return;
    if (b.dataset.view) show(b.dataset.view);
    else if (b.dataset.cp !== undefined) openColorProfile(+b.dataset.cp);
    else if (b.dataset.sa !== undefined) openSoundApp(+b.dataset.sa);
    else if (b.dataset.add === 'color') Q.addColor();
    else if (b.dataset.add === 'sound') Q.addSound();
  });

  // ---------- вкл / выкл инструментов (переключатели data-module на любых страницах) ----------
  document.addEventListener('change', (e) => {
    const m = e.target.closest('[data-module]'); if (!m) return;
    send({ type: 'module', name: m.dataset.module, on: m.checked });
  });
  function syncModules(g) {
    const isOn = (mod) => (mod === 'color' ? g.colorOn : mod === 'shot' ? g.shotOn !== false : mod === 'rec' ? g.recOn !== false : g.soundOn);
    document.querySelectorAll('[data-module]').forEach((i) => (i.checked = isOn(i.dataset.module)));
    document.querySelectorAll('[data-offnote]').forEach((n) => (n.hidden = isOn(n.dataset.offnote)));
  }

  // ---------- тема ----------
  // щелчок — сразу светлая ⇄ тёмная; правый щелчок — список (как в системе / светлая / тёмная) под кнопкой
  const names = { auto: 'Как в системе', light: 'Светлая', dark: 'Тёмная' };
  const themeBtn = $('themeBtn');
  const themeTitle = () => (themeBtn.title = 'Тема: ' + names[GW.theme.get()].toLowerCase() + '. Щелчок — светлая/тёмная, правый щелчок — выбрать');
  themeBtn.onclick = () => {
    GW.closeMenu();
    GW.theme.set(document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark');
    themeTitle();
  };
  themeBtn.addEventListener('contextmenu', (e) => {
    e.preventDefault();
    GW.menu(themeBtn, Object.keys(names).map((v) => ({ value: v, label: names[v], on: GW.theme.get() === v })), (v) => { GW.theme.set(v); themeTitle(); });
  });
  themeTitle();
  const tellTheme = () => send({ type: 'theme', dark: document.documentElement.dataset.theme === 'dark' });
  new MutationObserver(tellTheme).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });

  // ---------- окно: свои кнопки и края ----------
  document.querySelectorAll('[data-win]').forEach((b) => (b.onclick = () => send({ type: 'win', action: b.dataset.win })));
  for (const dir of ['t', 'b', 'l', 'r', 'tl', 'tr', 'bl', 'br']) {
    const e = document.createElement('div'); e.className = 'qr-edge'; e.dataset.dir = dir;
    e.addEventListener('pointerdown', (ev) => { if (ev.button === 0) { ev.preventDefault(); send({ type: 'resize', dir }); } });
    document.body.append(e);
  }
  const MAX_ICON = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4"><rect x="3.5" y="3.5" width="9" height="9" rx="1.8"/></svg>';
  const RESTORE_ICON = '<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round"><rect x="3" y="5.5" width="7.5" height="7.5" rx="1.6"/><path d="M5.5 5.5V4.6A1.6 1.6 0 017.1 3H11.4A1.6 1.6 0 0113 4.6v4.3a1.6 1.6 0 01-1.6 1.6h-.9"/></svg>';

  // ---------- «Общие» ----------
  const TRAY = { open: 'Открыть окно', next: 'Следующий профиль цвета' };
  $('g-auto').addEventListener('change', (e) => send({ type: 'autostart', value: e.target.checked }));
  $('g-trayBtn').onclick = (e) => GW.menu(e.currentTarget,
    Object.keys(TRAY).map((v) => ({ value: v, label: TRAY[v], on: Q.state.shell && Q.state.shell.trayClick === v })),
    (v) => send({ type: 'trayClick', value: v }));

  // ---------- обновления ----------
  let upd = null;
  function renderUpdate() {
    const u = upd; if (!u) return;
    $('u-cur').textContent = u.current;
    $('u-auto').checked = u.auto;
    const btn = $('u-btn'), note = $('u-note');
    btn.disabled = u.state === 'checking' || u.state === 'downloading';
    btn.classList.toggle('main', u.state === 'available');
    btn.textContent = u.state === 'available' ? 'Обновить до ' + u.latest : u.state === 'downloading' ? 'Скачиваю… ' + u.progress + '%' : 'Проверить';
    $('u-page').hidden = !(u.state === 'available' && u.page);
    note.textContent =
      u.state === 'checking' ? 'Проверяю…' :
      u.state === 'downloading' ? 'Скачиваю установщик — потом Qural перезапустится сам' :
      u.state === 'available' ? 'Доступна версия ' + u.latest :
      u.state === 'latest' ? 'Установлена последняя версия' + (u.checkedAt ? ' · проверено ' + u.checkedAt : '') :
      u.state === 'error' ? u.error :
      u.installed ? 'Ещё не проверял' : 'Эта копия не установлена установщиком — обновлять её нужно вручную';
  }
  $('u-btn').onclick = () => send({ type: 'update', action: upd && upd.state === 'available' ? 'install' : 'check' });
  $('u-page').onclick = () => send({ type: 'update', action: 'page' });
  $('u-auto').addEventListener('change', (e) => send({ type: 'updateAuto', value: e.target.checked }));
  Q.on('shell', (m) => { if (m.type === 'update') { upd = m; renderUpdate(); } });

  // ---------- команды для Ulanzi Deck / Stream Deck ----------
  // аргумент командной строки: в кавычках, если есть пробелы; кавычки внутри — через \"
  // правила Windows (CommandLineToArgvW): обратные косые перед кавычкой и в конце удваиваются
  const arg = (v) => {
    const s = String(v);
    if (!/[\s"]/.test(s)) return s;
    return '"' + s.replace(/(\\*)"/g, '$1$1\\"').replace(/(\\+)$/, '$1$1') + '"';
  };
  // профиль с повторяющимся или «служебным» названием (next, число) — по номеру, иначе по названию
  const profileArg = (p, i, all) => {
    const n = p.name.trim().toLowerCase();
    const clash = n === 'next' || /^\d+$/.test(n) || all.some((q, j) => j !== i && q.name.trim().toLowerCase() === n);
    return clash ? String(i + 1) : p.name;
  };
  let cmds = [];
  function renderCmds() {
    const g = Q.state.shell, c = Q.state.color, s = Q.state.sound;
    if (g && g.exe) $('g-exe').textContent = g.exe;
    cmds = [];
    const grp = (t) => cmds.push({ group: t });
    const add = (label, args) => cmds.push({ label, args: args.map(arg).join(' ') });
    grp('Цвет');
    if (c) c.profiles.forEach((p, i) => add(`Профиль «${p.name}»`, ['--profile', profileArg(p, i, c.profiles)]));
    add('Следующий профиль цвета', ['--profile', 'next']);
    add('Сбросить мониторы к стандартным', ['--reset-colors']);
    add('Включить / выключить «Цвет»', ['--color', 'toggle']);
    grp('Звук');
    add('Активное окно · громче', ['--volume', 'active', 'up']);
    add('Активное окно · тише', ['--volume', 'active', 'down']);
    add('Активное окно · без звука / вернуть', ['--volume', 'active', 'mute']);
    if (s) s.apps.forEach((a) => {
      add(`${a.title} · громче`, ['--volume', a.process, 'up']);
      add(`${a.title} · тише`, ['--volume', a.process, 'down']);
      add(`${a.title} · без звука / вернуть`, ['--volume', a.process, 'mute']);
    });
    add('Включить / выключить «Звук»', ['--sound', 'toggle']);
    grp('Скриншоты');
    add('Выделить область', ['--shot']);
    add('Весь экран (монитор под курсором) — в папку и буфер', ['--shot', 'full']);
    add('Окно в фокусе — в папку и буфер', ['--shot', 'window']);
    grp('Запись экрана');
    add('Начать / остановить (выбрать область или окно)', ['--rec']);
    add('Записать весь экран', ['--rec', 'start', 'screen']);
    add('Записать окно в фокусе', ['--rec', 'start', 'window']);
    add('Пауза / продолжить', ['--rec', 'pause']);
    grp('Окно');
    add('Открыть Qural', ['--open']);
    Q.setHTML($('g-cmds'), cmds.map((x, i) => x.group
      ? `<li class="qr-cmdgrp">${esc(x.group)}</li>`
      : `<li><div class="gw-t"><b>${esc(x.label)}</b><code>${esc(x.args)}</code></div><button class="gw-pill" data-cmd="${i}">Копировать</button></li>`).join(''));
  }
  $('g-cmds').addEventListener('click', (e) => { const b = e.target.closest('[data-cmd]'); if (b && cmds[+b.dataset.cmd]) send({ type: 'copy', text: cmds[+b.dataset.cmd].args }); });
  $('g-exeCopy').onclick = () => { const g = Q.state.shell; if (g && g.exe) send({ type: 'copy', text: g.exe }); };
  $('g-makeLinks').onclick = () => send({ type: 'shortcuts', list: cmds.filter((x) => !x.group).map((x) => ({ label: x.label, args: x.args })) });
  Q.on('shell', (m) => {
    if (m.type === 'shortcutsDone') {
      $('g-linksDone').textContent = `Готово: ${m.count} ярлыков в папке «${m.dir}»`;
      GW.toast('Ярлыки созданы — папка открыта');
    } else if (m.type === 'toast') GW.toast(m.text);
  });
  Q.on('shell', (m) => { if (m.type === 'copied') GW.toast('Скопировано'); else if (m.type === 'copyFailed') GW.toast('Не получилось скопировать — буфер обмена занят'); });
  const cmdsLater = () => queueMicrotask(renderCmds);
  Q.on('shell', (m) => m.type === 'state' && cmdsLater());
  Q.on('color', (m) => m.type === 'state' && cmdsLater());
  Q.on('sound', (m) => m.type === 'state' && cmdsLater());

  // ---------- сообщения ----------
  Q.on('shell', (m) => {
    if (m.type === 'win') {
      document.documentElement.classList.toggle('qr-max', !!m.max);
      document.documentElement.classList.toggle('qr-inactive', !m.active);
      $('maxBtn').innerHTML = m.max ? RESTORE_ICON : MAX_ICON;
      $('maxBtn').title = m.max ? 'Вернуть размер' : 'Развернуть';
    } else if (m.type === 'state') {
      const first = !Q.state.shell;
      Q.state.shell = m;
      $('g-auto').checked = m.autostart;
      $('g-trayV').textContent = TRAY[m.trayClick] || TRAY.open;
      $('g-nvNote').hidden = m.nvapi;
      syncModules(m);
      if (first) show(({ home: 'home', color: 'cprofile', sound: 'sapp', shot: 'shot', rec: 'rec', general: 'general' })[m.page] || ($('view-' + m.page) ? m.page : 'home'));
      else renderNav();
    }
  });
  // состояния инструментов запоминаем до того, как их отрисуют color.js / sound.js; меню — после
  Q.on('color', (m) => { if (m.type === 'state') { Q.state.color = m.state; queueMicrotask(renderNav); } });
  Q.on('rec', (m) => { if (m.type === 'state') { (Q.state.shell || {}).recording = m.state.recording; queueMicrotask(renderNav); } });
  Q.on('sound', (m) => { if (m.type === 'state') Q.state.sound = m.state; if (m.type === 'state' || m.type === 'live' || m.type === 'local') queueMicrotask(renderNav); });

  if (host) {
    host.addEventListener('message', (e) => Q.emit(e.data));
    addEventListener('DOMContentLoaded', () => { tellTheme(); send({ type: 'ready' }); });
  } else {
    // открыто просто в браузере — образец
    addEventListener('DOMContentLoaded', () => {
      Q.emit({ mod: 'shell', type: 'state', page: new URLSearchParams(location.search).get('page') || 'home', trayClick: 'open', autostart: true, nvapi: true, fallback: true, colorOn: true, soundOn: true, busy: '', welcome: new URLSearchParams(location.search).has('welcome') });
    });
  }
})();
