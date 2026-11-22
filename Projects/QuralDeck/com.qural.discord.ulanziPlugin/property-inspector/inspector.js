// Окно настройки кнопки Discord: ID и секрет своего приложения Discord, состояние связи.
// Секрет уходит только плагину (он хранит его в %APPDATA%\Qural\deck-discord.json); обратно в окно не приходит.
const STATE = {
  normal: '● Подключено — микрофон включён',
  mute: '● Подключено — микрофон заглушен',
  deaf: '● Подключено — звук выключен',
  offline: '○ Discord не запущен',
  auth: '! Нужно разрешение Discord',
  setup: '! Введите ID и секрет приложения',
};
let idEl, secEl, stEl, btn;

$UD.connect();
$UD.onConnected(() => {
  idEl = document.querySelector('#clientId');
  secEl = document.querySelector('#clientSecret');
  stEl = document.querySelector('#state');
  btn = document.querySelector('#save');
  document.querySelector('.uspi-wrapper').classList.remove('hidden');
  btn.addEventListener('click', () => {
    const clientId = idEl.value.trim(), clientSecret = secEl.value.trim();
    if (!/^\d{15,22}$/.test(clientId)) { stEl.textContent = '! Client ID — это число из 17–20 цифр'; return; }
    if (!clientSecret && !secEl.dataset.saved) { stEl.textContent = '! Вставьте Client Secret'; return; }
    $UD.sendToPlugin({ app: { clientId, clientSecret } });
    secEl.value = ''; secEl.placeholder = 'сохранён'; secEl.dataset.saved = '1';
    stEl.textContent = '… Подключаюсь — смотрите окно в Discord';
  });
  $UD.sendToPlugin({ hello: true });
});

$UD.onSendToPropertyInspector(j => {
  const p = j.payload || {};
  if (!stEl) return;
  stEl.textContent = (STATE[p.state] || p.state) + (p.note && !/^(normal|mute|deaf)$/.test(p.state) ? ' — ' + p.note : '');
  if (p.clientId && !idEl.value) idEl.value = p.clientId;
  if (p.hasSecret) { secEl.placeholder = 'сохранён'; secEl.dataset.saved = '1'; }
});
