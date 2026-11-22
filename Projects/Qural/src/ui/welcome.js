// Qural — первый запуск: какие инструменты включить.
(() => {
  const $ = (id) => document.getElementById(id);
  const MODS = [
    { id: 'color', title: 'Регулировка цвета', text: 'Профили мониторов: яркость, контраст, гамма, насыщенность NVIDIA — по горячей клавише или с деки' },
    { id: 'sound', title: 'Микшер звука', text: 'Громкость каждой программы своими клавишами: игра тише, Discord громче, Spotify без звука' },
    { id: 'shot', title: 'Скриншоты', text: 'PrtSc — выделить область, нарисовать стрелки, текст, размытие, скопировать или сохранить' },
    { id: 'rec', title: 'Запись экрана', text: 'Область, окно или весь экран со звуком и шумоподавлением; редактор для обрезки' },
  ];
  const on = { color: false, sound: false, shot: false, rec: false };   // по умолчанию ничего не выбрано — решает пользователь

  function render() {
    $('w-mods').innerHTML = MODS.map((m) =>
      `<button class="gw-card qr-wcard${on[m.id] ? ' on' : ''}" data-w="${m.id}" aria-pressed="${on[m.id]}">` +
      `<span class="qr-wic">${Q.svg(m.id, '')}</span>` +
      `<span class="qr-wt"><b>${m.title}</b><small>${m.text}</small></span>` +
      `<span class="qr-wcheck"><svg viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3.5 8.5l3 3 6-7"/></svg></span></button>`).join('');
    $('w-go').disabled = false;
  }

  $('w-mods').addEventListener('click', (e) => {
    const b = e.target.closest('[data-w]'); if (!b) return;
    on[b.dataset.w] = !on[b.dataset.w];
    render();
  });
  $('w-go').onclick = () => {
    Q.send('shell', { type: 'welcome', color: on.color, sound: on.sound, shot: on.shot, rec: on.rec, autostart: $('w-auto').checked });
  };

  // окно открыто впервые — показать выбор; после «Начать» Qural пришлёт welcome: false
  Q.on('shell', (m) => {
    if (m.type !== 'state') return;
    const was = document.documentElement.classList.contains('qr-first');
    document.documentElement.classList.toggle('qr-first', !!m.welcome);
    if (m.welcome) { render(); Q.show('welcome'); }
    else if (was) Q.show('home');
  });
})();
