// Окно настройки кнопок «Qural — Claude»: пояснение и, для «Стоп / Новый чат / Диктовка», клавиши.
const MAIN = 'com.qural.deck.claude';
const uuid = Utils.getQueryParams('uuid') || '';
const kind = uuid.startsWith(MAIN + '.') ? uuid.slice(MAIN.length + 1) : '';

const INFO = {
  status: { ctx: true, note: 'Что сейчас делает Claude во вкладке Code: <b>думает</b>, <b>работает</b>, <b>ждёт вас</b>, <b>разрешить?</b>, <b>готово</b>, <b>ошибка</b>. Контекст сессии занят на 70 % и больше — <b>сжать?</b> (пора сделать /compact). Нажатие — открыть окно Claude; на крутилке поворот переключает сессии.' },
  browser: { keys: 'ctrl+shift+b', note: 'Показать или скрыть браузер в окне Claude.' },
  limits: { server: true, note: 'Сколько лимита плана израсходовано: <b>5 часов</b>, <b>неделя</b>, лимит по модели (Fable) и <b>контекст</b> текущей сессии. <b>Показывать</b> — всё вместе или одно окно крупно; <b>Вид</b> одного окна — цифра или цифра с маленьким Claude, который ведёт себя по заполненности (спокоен → работает → раздувается → на пределе); нажатие на одном окне — забег персонажа до своего процента (на контексте — /compact), на «всё вместе» — следующее окно; поворот крутилки переключает по кругу.<br><b>С сервера</b>: раз в 5 минут спрашивать проценты у Anthropic по ключу входа Claude Code. Ключ живёт 8 часов; истёк — плагин запускает короткий <b>claude -p</b> на Haiku (≈ $0,003 по цене API). Выключено — лимиты только из терминального Claude Code, контекст считается всегда.' },
  clock: { note: 'Часы пиксельным шрифтом: время (двоеточие мигает), день недели, дата и полоска — сколько прошло от суток.' },
  ask: { note: 'Крутилка для запросов разрешения: <b>вправо</b> — разрешить, <b>влево</b> — отклонить. На экране — что именно просит Claude. Нажатие — открыть окно Claude.' },
  allow: { note: 'Когда Claude просит разрешение, кнопка становится белой — нажмите, чтобы <b>разрешить</b>. Если 15 секунд не нажимать, запрос появится в окне Claude как обычно.' },
  deny: { note: 'Когда Claude просит разрешение, кнопка становится белой — нажмите, чтобы <b>отклонить</b>. Если 15 секунд не нажимать, запрос появится в окне Claude как обычно.' },
  stop: { keys: 'esc', note: 'Остановить ответ Claude: окно Claude выводится вперёд и нажимается клавиша.' },
  newchat: { keys: 'ctrl+n', note: 'Новый разговор (сессия) в Claude.' },
  dictation: { keys: 'ctrl+d', note: 'Включить или выключить диктовку в Claude. Если в настройках Claude клавиша другая — впишите её сюда.' },
};
const info = INFO[kind] || { note: '' };
let S = {};
let form, keys;

$UD.connect();

$UD.onConnected(() => {
  form = document.querySelector('#property-inspector');
  keys = document.querySelector('#keys');
  document.querySelector('#note').innerHTML = info.note +
    (info.keys ? '<br>Клавиши пишутся так: <b>ctrl+n</b>, <b>esc</b>, <b>ctrl+shift+d</b>. Пусто — <b>' + info.keys + '</b>. Нужен запущенный Qural.' : '');
  if (info.keys) {
    document.querySelector('#keysRow').classList.remove('hidden');
    keys.placeholder = info.keys;
    keys.value = S.keys || '';
    keys.addEventListener('input', Utils.debounce(() => { S = { ...S, keys: keys.value.trim() }; $UD.setSettings(S); }));
  }
  if (info.server) {
    const show = document.querySelector('#show');
    document.querySelector('#showRow').classList.remove('hidden');
    show.value = S.show || 'all';
    show.addEventListener('change', () => { S = { ...S, show: show.value }; $UD.setSettings(S); });
    const style = document.querySelector('#style');
    document.querySelector('#styleRow').classList.remove('hidden');
    style.value = S.style || 'char';
    style.addEventListener('change', () => { S = { ...S, style: style.value }; $UD.setSettings(S); });
    const sel = document.querySelector('#server');
    document.querySelector('#serverRow').classList.remove('hidden');
    sel.value = S.server || 'on';
    sel.addEventListener('change', () => { S = { ...S, server: sel.value }; $UD.setSettings(S); });
  }
  if (info.ctx) {
    const sel = document.querySelector('#ctxwin');
    document.querySelector('#ctxRow').classList.remove('hidden');
    sel.value = S.ctxwin || '';
    sel.addEventListener('change', () => { S = { ...S, ctxwin: sel.value }; $UD.setSettings(S); });
  }
  document.querySelector('.uspi-wrapper').classList.remove('hidden');
});

function apply(p) {
  S = { ...S, ...p };
  if (keys) keys.value = S.keys || '';
  const sel = document.querySelector('#ctxwin');
  if (sel) sel.value = S.ctxwin || '';
  const show = document.querySelector('#show');
  if (show) show.value = S.show || 'all';
  const style = document.querySelector('#style');
  if (style) style.value = S.style || 'char';
  const srv = document.querySelector('#server');
  if (srv) srv.value = S.server || 'on';
}
$UD.onParamFromApp(j => apply(j.param || {}));
$UD.onAdd(j => apply(j.param || {}));
$UD.onDidReceiveSettings(j => { if (j && j.settings) apply(j.settings); });
