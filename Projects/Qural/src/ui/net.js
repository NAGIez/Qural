// Qural — вкладка «Сеть»: все подключения сразу (внутри — net-merge). Данные приходят раз в секунду, пока вкладка открыта.
(() => {
  const $ = (id) => document.getElementById('n-' + id);
  const send = (m) => Q.send('net', m);
  const esc = (s) => String(s ?? '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' })[c]);
  let S = null;

  // ---------- числа ----------
  const mbps = (bytesPerSec) => { const v = (bytesPerSec * 8) / 1e6; return v < 10 ? v.toFixed(1) : String(Math.round(v)); };
  function bytes(n) {
    if (n < 1e3) return n + ' Б';
    if (n < 1e6) return (n / 1e3).toFixed(0) + ' КБ';
    if (n < 1e9) return (n / 1e6).toFixed(n < 1e7 ? 1 : 0) + ' МБ';
    return (n / 1e9).toFixed(2) + ' ГБ';
  }
  function since(sec) {
    if (!sec) return '';
    if (sec < 60) return 'меньше минуты';
    const m = Math.floor(sec / 60), h = Math.floor(m / 60), d = Math.floor(h / 24);
    if (d) return d + ' д ' + (h % 24) + ' ч';
    if (h) return h + ' ч ' + (m % 60) + ' мин';
    return m + ' мин';
  }

  // ---------- значки подключений ----------
  const KIND_SVG = {
    wifi: '<path d="M2 6.2a8.5 8.5 0 0112 0M4.2 8.6a5.3 5.3 0 017.6 0M6.4 11a2.2 2.2 0 013.2 0"/><circle cx="8" cy="13" r=".6" fill="currentColor"/>',
    ethernet: '<rect x="2.5" y="3" width="11" height="8" rx="1.5"/><path d="M5.5 13.5h5M8 11v2.5M5.5 6.5v2M8 6.5v2M10.5 6.5v2"/>',
    phone: '<rect x="4.5" y="1.8" width="7" height="12.4" rx="1.6"/><path d="M7 12h2"/>',
    mobile: '<path d="M3 13V10M6.3 13V7.5M9.6 13V5M13 13V2.5"/>',
    other: '<circle cx="8" cy="8" r="5.5"/><path d="M2.5 8h11M8 2.5c2 2 2 9 0 11M8 2.5c-2 2-2 9 0 11"/>',
    merge: '<circle cx="2.8" cy="4" r="1.2"/><circle cx="2.8" cy="12" r="1.2"/><path d="M4 4c3 0 3.5 4 6 4M4 12c3 0 3.5-4 6-4M10 8h3.5M11.8 6.2L13.6 8l-1.8 1.8"/>',
  };
  const kindIcon = (k) => `<svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round">${KIND_SVG[k] || KIND_SVG.other}</svg>`;
  const KIND_NAME = { wifi: 'Wi-Fi', ethernet: 'Кабель', phone: 'Телефон', mobile: 'Мобильная сеть', other: 'Подключение' };

  // ---------- графики ----------
  const css = (v) => getComputedStyle(document.documentElement).getPropertyValue(v).trim();
  function graph(canvas, down, up) {
    const dpr = devicePixelRatio || 1, w = canvas.clientWidth, h = canvas.clientHeight;
    if (!w || !h) return;
    if (canvas.width !== Math.round(w * dpr) || canvas.height !== Math.round(h * dpr)) { canvas.width = Math.round(w * dpr); canvas.height = Math.round(h * dpr); }
    const g = canvas.getContext('2d');
    g.setTransform(dpr, 0, 0, dpr, 0, 0);
    g.clearRect(0, 0, w, h);
    const max = Math.max(1, ...down, ...up) * 1.15, n = down.length;
    const x = (i) => (i / (n - 1)) * w, y = (v) => h - 2 - (v / max) * (h - 4);
    const blue = css('--blue') || '#0A84FF', orange = css('--orange') || '#FF9F0A';
    // приём — заливка, отдача — линия
    g.beginPath(); g.moveTo(0, h);
    down.forEach((v, i) => g.lineTo(x(i), y(v)));
    g.lineTo(w, h); g.closePath();
    const grad = g.createLinearGradient(0, 0, 0, h);
    grad.addColorStop(0, blue + '66'); grad.addColorStop(1, blue + '08');
    g.fillStyle = grad; g.fill();
    g.beginPath(); down.forEach((v, i) => (i ? g.lineTo(x(i), y(v)) : g.moveTo(x(i), y(v))));
    g.strokeStyle = blue; g.lineWidth = 1.5; g.stroke();
    g.beginPath(); up.forEach((v, i) => (i ? g.lineTo(x(i), y(v)) : g.moveTo(x(i), y(v))));
    g.strokeStyle = orange; g.lineWidth = 1.2; g.stroke();
  }

  // ---------- карточки подключений ----------
  function stateChip(a, st) {
    if (!a.present) return '<span class="gw-chip gray">отключено</span>';
    if (!a.merged) return st === 'on' ? '<span class="gw-chip gray">не в объединении</span>' : '<span class="gw-chip gray">работает</span>';
    if (a.state !== 'up') return '<span class="gw-chip red">нет интернета</span>';
    return '<span class="gw-chip green">в объединении</span>';
  }

  // каркас карточек пересобираем, только когда меняется набор подключений; цифры и графики — на месте
  let shape = '';
  function renderCards(st) {
    const box = $('adapters');
    const sig = st.adapters.map((a) => a.name).join('|');
    if (sig !== shape) {
      shape = sig;
      box.innerHTML = st.adapters.map((a, i) => `
        <div class="gw-card qr-netcard" data-i="${i}">
          <div class="qr-nc-top">
            <span class="qr-nc-ic">${kindIcon(a.kind)}</span>
            <div class="qr-nc-name"><b data-f="label"></b><small data-f="sub"></small></div>
            <span data-f="chip"></span>
          </div>
          <div class="qr-nc-rates"><span class="qr-down" data-f="down"></span><span class="qr-up" data-f="up"></span></div>
          <canvas class="qr-nc-graph"></canvas>
          <div class="qr-nc-foot"><span data-f="day"></span><span data-f="month"></span></div>
          <div class="qr-nc-foot qr-nc-acts">
            <span data-f="flows"></span>
            <span class="gw-row"><button class="gw-pill" data-limit="${esc(a.name)}"></button><button class="gw-ib" data-alias="${esc(a.name)}" title="Переименовать"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><path d="M10.5 2.8l2.7 2.7L6 12.7l-3.2.5.5-3.2z"/></svg></button></span>
          </div>
        </div>`).join('');
    }
    st.adapters.forEach((a, i) => {
      const card = box.querySelector(`[data-i="${i}"]`); if (!card) return;
      const f = (k) => card.querySelector(`[data-f="${k}"]`);
      card.classList.toggle('off', !a.present || (st.state === 'on' && !a.merged));
      f('label').textContent = a.label || a.name;
      f('sub').textContent = [a.isp, a.ip, a.phone ? 'раздача с телефона' : KIND_NAME[a.kind]].filter(Boolean).join(' · ');
      f('chip').innerHTML = stateChip(a, st.state);
      f('down').innerHTML = `↓ ${mbps(a.down)} <small>Мбит/с</small>`;
      f('up').innerHTML = `↑ ${mbps(a.up)} <small>Мбит/с</small>`;
      f('day').textContent = `Сегодня ↓ ${bytes(a.dayIn)}  ↑ ${bytes(a.dayOut)}`;
      f('month').textContent = `Месяц ↓ ${bytes(a.monthIn)}  ↑ ${bytes(a.monthOut)}`;
      f('flows').textContent = a.merged
        ? `Соединений ${a.active} · всего ${a.total}` + (a.failed ? ` · сбоев ${a.failed}` : '') + (a.cool ? ` · бережёт телефон: ${a.cool} Мбит/с` : '')
        : (st.state === 'on' ? 'это подключение сейчас не используется' : '');
      const lim = card.querySelector('[data-limit]');
      lim.textContent = a.limit > 0 ? `Лимит ${a.limit} Мбит/с` : 'Без лимита';
      lim.classList.toggle('on', a.limit > 0);
      graph(card.querySelector('canvas'), a.histDown, a.histUp);
    });
  }

  // ---------- программы ----------
  const icons = {};   // значки приходят один раз — дальше берём отсюда
  let showAll = false;
  const ROUTE = (r) => r === 'merge' ? '<span class="gw-chip green">все подключения</span>'
    : r === 'local' ? '<span class="gw-chip gray">внутри компьютера</span>'
    : r ? `<span class="gw-chip blue">${esc(r)}</span>` : '';
  function spark(canvas, h) {
    const dpr = devicePixelRatio || 1, w = canvas.clientWidth, hh = canvas.clientHeight;
    if (!w || !hh) return;
    if (canvas.width !== Math.round(w * dpr)) { canvas.width = Math.round(w * dpr); canvas.height = Math.round(hh * dpr); }
    const g = canvas.getContext('2d'); g.setTransform(dpr, 0, 0, dpr, 0, 0); g.clearRect(0, 0, w, hh);
    const max = Math.max(0.5, ...h), n = h.length, blue = css('--blue') || '#0A84FF';
    g.beginPath(); g.moveTo(0, hh);
    h.forEach((v, i) => g.lineTo((i / (n - 1)) * w, hh - 1 - (v / max) * (hh - 2)));
    g.lineTo(w, hh); g.closePath(); g.fillStyle = blue + '40'; g.fill();
    g.beginPath(); h.forEach((v, i) => (i ? g.lineTo : g.moveTo).call(g, (i / (n - 1)) * w, hh - 1 - (v / max) * (hh - 2)));
    g.strokeStyle = blue; g.lineWidth = 1.2; g.stroke();
  }
  let appShape = '';
  const SPLIT_COLORS = ['var(--blue)', 'var(--green)', 'var(--orange)', 'var(--purple)'];
  function renderApps(st) {
    const card = $('appsCard');
    card.hidden = !st.apps || st.state === 'missing' || st.state === 'off' || st.state === 'setup';
    if (card.hidden) return;
    const list = st.appList || [];
    list.forEach((a) => { if (a.icon) icons[a.name] = a.icon; });
    $('appsSub').textContent = !st.appsLive ? 'запускается подсчёт…' : list.length ? `${list.filter((a) => a.down + a.up > 1000).length} качают сейчас · ${list.length} за сегодня` : 'пока ни одна программа не качала';
    const shown = showAll ? list : list.slice(0, 10);
    $('appsMore').hidden = list.length <= 10;
    $('appsMore').textContent = showAll ? 'Свернуть' : `Показать все (${list.length})`;
    const maxRate = Math.max(1, ...shown.map((a) => Math.max(a.down, a.up)));
    const sig = shown.map((a) => a.name).join('|');
    const box = $('apps');
    if (sig !== appShape) {
      appShape = sig;
      box.innerHTML = shown.map((a, i) => `
        <div class="qr-na-row" data-i="${i}">
          <span class="qr-na-app"><span class="qr-na-ic" data-f="ic"></span><span class="qr-na-name"><b data-f="title"></b><span data-f="route"></span></span></span>
          <canvas class="qr-na-spark"></canvas>
          <span class="qr-na-rate"><span class="qr-down" data-f="down"></span><i class="qr-na-bar"><i data-f="bd"></i></i></span>
          <span class="qr-na-rate"><span class="qr-up" data-f="up"></span><i class="qr-na-bar up"><i data-f="bu"></i></i></span>
          <span class="qr-na-used"><b data-f="day"></b><small data-f="month"></small></span>
        </div>`).join('');
    }
    shown.forEach((a, i) => {
      const row = box.querySelector(`[data-i="${i}"]`); if (!row) return;
      const f = (k) => row.querySelector(`[data-f="${k}"]`);
      const ic = icons[a.name];
      f('ic').innerHTML = ic ? `<img src="${ic}" alt="">` : `<span class="qr-icon">${esc((a.title || a.name).slice(0, 1).toUpperCase())}</span>`;
      f('title').textContent = a.title || a.name;
      // через объединение — доли подключений сейчас; иначе — одно подключение
      const tot = (a.split || []).reduce((x, s) => x + s.rate, 0);
      f('route').innerHTML = a.route === 'merge' && tot > 2000
        ? `<span class="qr-na-split">${a.split.filter((s) => s.rate / tot >= 0.01).map((s, k) =>
            `<span><i style="background:${SPLIT_COLORS[k % SPLIT_COLORS.length]}"></i>${esc(s.name)} ${Math.round((s.rate / tot) * 100)}%</span>`).join('')}</span>`
        : ROUTE(a.route);
      f('down').textContent = mbps(a.down) + ' Мбит/с';
      f('up').textContent = mbps(a.up) + ' Мбит/с';
      f('bd').style.width = Math.min(100, (a.down / maxRate) * 100) + '%';
      f('bu').style.width = Math.min(100, (a.up / maxRate) * 100) + '%';
      f('day').textContent = bytes(a.dayIn + a.dayOut) + ' сегодня';
      f('month').textContent = 'за месяц ' + bytes(a.monthIn + a.monthOut);
      spark(row.querySelector('canvas'), a.hist);
    });
  }
  $('appsMore').onclick = () => { showAll = !showAll; appShape = ''; if (S) renderApps(S); };

  // ---------- скорость: циферблат со стрелкой ----------
  // шкала неравномерная, как у спидометров: от 0 до 1000 Мбит/с, каждый отрезок — одинаковый угол
  const MARKS = [0, 5, 10, 25, 50, 100, 250, 500, 1000];
  const GA = 135, GR = 92;
  const gpt = (deg, r) => { const a = (deg * Math.PI) / 180; return [(120 + r * Math.sin(a)).toFixed(2), (120 - r * Math.cos(a)).toFixed(2)]; };
  function gfrac(v) {
    if (!(v > 0)) return 0;
    for (let i = 0; i < MARKS.length - 1; i++)
      if (v < MARKS[i + 1]) return (i + (v - MARKS[i]) / (MARKS[i + 1] - MARKS[i])) / (MARKS.length - 1);
    return 1;
  }
  (function gaugeInit() {
    const arc = `M ${gpt(-GA, GR).join(' ')} A ${GR} ${GR} 0 1 1 ${gpt(GA, GR).join(' ')}`;
    document.querySelector('#n-gauge .qr-sp-track').setAttribute('d', arc);
    $('spArc').setAttribute('d', arc);
    $('spTicks').innerHTML = MARKS.map((m, i) => {
      const deg = -GA + (i / (MARKS.length - 1)) * GA * 2, [x1, y1] = gpt(deg, GR - 12), [x2, y2] = gpt(deg, GR - 17), [tx, ty] = gpt(deg, GR - 30);
      return `<line x1="${x1}" y1="${y1}" x2="${x2}" y2="${y2}"/><text x="${tx}" y="${ty}">${m}</text>`;
    }).join('');
  })();
  function gauge(v, up) {
    const f = gfrac(v);
    $('spArc').setAttribute('stroke-dasharray', `${(f * 100).toFixed(2)} 100`);
    $('spNeedle').style.transform = `rotate(${(-GA + f * GA * 2).toFixed(1)}deg)`;
    $('gauge').parentNode.classList.toggle('up', !!up);
  }
  gauge(0);

  let SP = null;
  const fmt = (v) => (v < 0 ? '' : v < 10 ? v.toFixed(1) : String(Math.round(v)));
  const PHASE = { ping: 'выбор сервера и пинг', down: 'приём', up: 'отдача' };
  function renderSpeed(sp) {
    SP = sp;
    const rows = sp.rows || [], cur = rows.find((r) => r.key === sp.current);
    const live = sp.running && (sp.phase === 'down' || sp.phase === 'up');
    let shown = live ? sp.mbps : 0, isUp = live && sp.phase === 'up';
    if (!sp.running) {
      const all = rows.find((r) => r.key === '*' && r.down > 0);
      shown = all ? all.down : Math.max(0, ...rows.map((r) => r.down));
    }
    gauge(shown, isUp);
    $('spVal').textContent = sp.running ? (live ? fmt(sp.mbps) || '0' : '…') : shown > 0 ? fmt(shown) : '—';
    $('spPhase').textContent = sp.running ? (cur ? `${cur.label} · ${PHASE[sp.phase] || ''}` : 'подключаюсь…')
      : sp.phase === 'stopped' ? 'Остановлено' : sp.at ? (rows.some((r) => r.key === '*' && r.down > 0) ? 'все вместе · ' : 'лучшее · ') + 'замер в ' + sp.at : '\u00a0';
    $('spBtn').textContent = sp.running ? 'Остановить' : rows.length ? 'Проверить снова' : 'Проверить скорость';
    $('spBtn').classList.toggle('main', !sp.running);
    const cell = (r, v, cls, unit, phase) => {
      if (r.state === 'run' && sp.phase === phase) return `<span class="${cls}">${phase === 'ping' ? '…' : fmt(sp.mbps) || '…'}</span>`;
      if (v < 0) return '<span class="dim">—</span>';
      if (phase === 'ping') return `<span title="${r.jitter >= 0 ? 'разброс ' + r.jitter + ' мс' : ''}">${Math.round(v)}<small class="dim"> ${unit}</small></span>`;
      return `<span class="${cls}">${fmt(v)}<small class="dim"> ${unit}</small></span>`;
    };
    $('spRows').innerHTML = !rows.length ? '' :
      `<div class="qr-sp-row head"><span>Подключение</span><span>Пинг</span><span>Приём</span><span>Отдача</span></div>` +
      rows.map((r) => `
        <div class="qr-sp-row${r.state === 'run' ? ' run' : ''}${r.key === '*' ? ' total' : ''}">
          <span class="qr-sp-name"><span class="qr-sp-ic">${kindIcon(r.kind)}</span><span class="qr-sp-nm"><b>${esc(r.label)}</b>${r.server ? `<small>${esc(r.server)}</small>` : ''}</span>${r.state === 'error' ? `<span class="gw-chip red">${esc(r.error || 'ошибка')}</span>` : r.state === 'skip' ? '<span class="gw-chip gray">пропущено</span>' : ''}</span>
          ${cell(r, r.ping, '', 'мс', 'ping')}
          ${cell(r, r.down, 'qr-down', 'Мбит/с', 'down')}
          ${cell(r, r.up, 'qr-up', 'Мбит/с', 'up')}
        </div>`).join('');
  }
  $('spBtn').onclick = () => {
    if (SP && SP.running) { send({ type: 'speedStop' }); return; }
    gauge(0); $('spVal').textContent = '…';
    send({ type: 'speed' });
  };

  // ---------- состояние ----------
  // missing — нет файлов; off — модуль выключен; setup — нужно разрешение; paused — пауза; waiting — ждёт второе
  // подключение (автопауза); starting — запускается; on — работает
  const SUB = {
    missing: 'Нет файлов сетевого модуля',
    off: 'Выключена — интернет идёт через одно подключение',
    setup: 'Нужно один раз разрешить',
    paused: 'Пауза — интернет идёт через одно подключение',
    waiting: 'Ждёт второе подключение с интернетом',
    starting: 'Запускается…',
  };
  function render(st) {
    S = st;
    const s = st.state, active = s === 'on' || s === 'starting' || s === 'waiting';
    $('missing').hidden = s !== 'missing';
    $('setup').hidden = s !== 'setup';
    $('wait').hidden = s !== 'waiting';
    $('update').hidden = !st.update || s === 'missing';
    $('updateV').textContent = st.update || '';
    $('updateBtn').disabled = !!st.updating;
    $('updateBtn').textContent = st.updating ? 'Обновляю…' : 'Обновить';
    $('toggle').hidden = s === 'missing' || s === 'off' || s === 'setup';
    $('toggle').textContent = active ? 'Пауза' : 'Включить';
    $('toggle').classList.toggle('main', !active);
    $('head').hidden = s === 'missing';
    $('sub').textContent = s === 'on' ? `Все подключения сразу · включено ${since(st.since)}` + (st.error ? ' · ' + st.error : '') : (SUB[s] || '') + (st.error ? ' · ' + st.error : '');
    $('down').textContent = `↓ ${mbps(st.down)} Мбит/с`;
    $('up').textContent = `↑ ${mbps(st.up)} Мбит/с`;
    graph($('total'), st.histDown, st.histUp);
    renderCards(st);
    document.querySelectorAll('[data-nset]').forEach((i) => { i.checked = !!st[i.dataset.nset]; i.disabled = s === 'missing'; });
    $('version').textContent = st.version ? `Сетевой модуль ${st.version} (на основе net-merge, MIT)` : '';
    renderApps(st);
    $('speedCard').hidden = s === 'missing';
  }

  // ---------- действия ----------
  $('toggle').onclick = () => send({ type: 'toggle' });
  $('setupBtn').onclick = () => { GW.toast('Windows спросит разрешение — нажмите «Да»'); send({ type: 'resume' }); };
  $('updateBtn').onclick = () => send({ type: 'update' });
  document.addEventListener('change', (e) => {
    const i = e.target.closest('[data-nset]'); if (!i) return;
    const k = i.dataset.nset;
    if ((k === 'game' || k === 'cool' || k === 'useAll') && S && S.state === 'on') GW.toast('Перезапускаю сетевой модуль — на секунду интернет может прерваться');
    send({ type: 'set', field: k, value: i.checked });
  });

  const LIMITS = [0, 1, 2, 5, 10, 20, 50, 100, 200, 500];
  $('adapters').addEventListener('click', (e) => {
    const lim = e.target.closest('[data-limit]');
    if (lim) {
      const name = lim.dataset.limit, a = S && S.adapters.find((x) => x.name === name), cur = a ? a.limit : 0;
      GW.menu(lim, LIMITS.map((v) => ({ value: v, label: v ? `${v} Мбит/с` : 'Без лимита', on: v === cur })),
        (v) => send({ type: 'limit', name, mbps: v }));
      return;
    }
    const al = e.target.closest('[data-alias]');
    if (al) {
      const name = al.dataset.alias, a = S && S.adapters.find((x) => x.name === name);
      aliasFor = name;
      $('aliasOf').textContent = '«' + name + '»';
      $('aliasInput').value = a && a.label !== name ? a.label : '';
      GW.sheet($('aliasSheet'));
      setTimeout(() => $('aliasInput').focus(), 30);
    }
  });
  let aliasFor = null;
  $('aliasForm').addEventListener('submit', (e) => {
    e.preventDefault();
    GW.sheet.close($('aliasSheet'));
    if (aliasFor) send({ type: 'alias', name: aliasFor, alias: $('aliasInput').value.trim() });
  });
  $('aliasReset').onclick = () => { GW.sheet.close($('aliasSheet')); if (aliasFor) send({ type: 'alias', name: aliasFor, alias: '' }); };
  $('aliasSheet').querySelectorAll('[data-close]').forEach((b) => (b.onclick = () => GW.sheet.close($('aliasSheet'))));

  addEventListener('resize', () => { if (S && Q.view === 'net') render(S); });

  Q.on('net', (m) => {
    if (m.type === 'state') { Q.state.net = m.state; render(m.state); }
    else if (m.type === 'speed') renderSpeed(m);
    else if (m.type === 'toast') GW.toast(m.text);
  });
})();
