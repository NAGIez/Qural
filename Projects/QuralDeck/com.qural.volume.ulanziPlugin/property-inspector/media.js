// Окно настройки «Сейчас играет»: вход в Spotify для лайков (Client ID своего приложения, вход PKCE — без секрета).
// Вход делает Qural: открывает браузер со страницей Spotify и ловит ответ на 127.0.0.1:8899.
let idEl, stEl, btn, redirEl;

$UD.connect();
$UD.onConnected(() => {
  idEl = document.querySelector('#clientId');
  stEl = document.querySelector('#state');
  btn = document.querySelector('#login');
  redirEl = document.querySelector('#redirect');
  document.querySelector('.uspi-wrapper').classList.remove('hidden');
  btn.addEventListener('click', () => {
    const id = idEl.value.trim();
    if (!/^[0-9a-f]{32}$/i.test(id)) { stEl.textContent = '! Client ID — 32 знака (цифры и буквы a–f) из Spotify Dashboard'; return; }
    stEl.textContent = '… Открываю браузер — нажмите там «Согласен»';
    $UD.sendToPlugin({ spotifyLogin: id });
  });
  $UD.sendToPlugin({ request: 'spotify' });
});

$UD.onSendToPropertyInspector(j => {
  const p = (j.payload || {}).spotify;
  if (!p || !stEl) return;
  if (p.clientId && !idEl.value) idEl.value = p.clientId;
  if (p.redirect) redirEl.textContent = p.redirect;
  const base = p.ready ? '● Spotify подключён — держите кнопку, чтобы лайкнуть' : p.waiting ? '… Жду ответа из браузера' : '○ Spotify не подключён';
  stEl.textContent = base + (p.note && !(p.ready && /Готово/.test(p.note)) ? ' — ' + p.note : '');
  btn.textContent = p.ready ? 'Войти заново' : 'Войти в Spotify';
});
