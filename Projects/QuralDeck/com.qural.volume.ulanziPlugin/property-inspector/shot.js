// Окно настройки кнопки «Скриншот»: что снимать по нажатию и что записывать по долгому нажатию.
let S = { mode: 'region', hold: 'screen' };
let sel, hold, note;
const NOTES = {
  region: 'Нажатие — рамка выделения: рисование, текст, размытие, номера шагов. <b>Ctrl+C</b> — в буфер, <b>Ctrl+S</b> — сохранить.',
  full: 'Нажатие — монитор с курсором сразу в папку скриншотов Qural и в буфер обмена.',
  window: 'Нажатие — окно в фокусе сразу в папку скриншотов Qural и в буфер обмена.',
};
const HOLD = {
  screen: '<b>Держать</b> (больше полсекунды) — начать запись всего экрана с курсором; ещё раз держать — остановить.',
  region: '<b>Держать</b> — выбрать область или окно для записи (красная рамка); ещё раз держать — остановить.',
  window: '<b>Держать</b> — записывать окно в фокусе; ещё раз держать — остановить.',
  off: 'Долгое нажатие ничего не делает — скриншот сразу при нажатии.',
};

$UD.connect();

$UD.onConnected(() => {
  sel = document.querySelector('#mode');
  hold = document.querySelector('#hold');
  note = document.querySelector('#note');
  render();
  document.querySelector('.uspi-wrapper').classList.remove('hidden');
  sel.addEventListener('change', () => { S = { ...S, mode: sel.value }; $UD.setSettings(S); render(); });
  hold.addEventListener('change', () => { S = { ...S, hold: hold.value }; $UD.setSettings(S); render(); });
});

function render() {
  if (!sel) return;
  const m = NOTES[S.mode] ? S.mode : 'region';
  const h = HOLD[S.hold] ? S.hold : 'screen';
  sel.value = m; hold.value = h;
  note.innerHTML = NOTES[m] + '<br><br>' + HOLD[h] + '<br><br>Нужен запущенный Qural с включёнными «Скриншотами» и «Записью экрана».';
}

function apply(p) { S = { ...S, ...p }; render(); }

$UD.onParamFromApp(j => { const p = j.param || {}; if (p.mode || p.hold) apply(p); else $UD.setSettings(S); });
$UD.onAdd(j => { const p = j.param || {}; if (p.mode || p.hold) apply(p); });
$UD.onDidReceiveSettings(j => { if (j && j.settings) apply(j.settings); });
