// Окно настройки кнопки «Скриншот»: что снимать.
let S = { mode: 'region' };
let sel, note;
const NOTES = {
  region: 'Откроется рамка выделения: рисование, текст, размытие, номера шагов. <b>Ctrl+C</b> — в буфер, <b>Ctrl+S</b> — сохранить.',
  full: 'Монитор, на котором курсор, сразу сохраняется в папку скриншотов Qural и копируется в буфер обмена.',
  window: 'Окно, которое сейчас в фокусе, сразу сохраняется в папку скриншотов Qural и копируется в буфер обмена.',
};

$UD.connect();

$UD.onConnected(() => {
  sel = document.querySelector('#mode');
  note = document.querySelector('#note');
  render();
  document.querySelector('.uspi-wrapper').classList.remove('hidden');
  sel.addEventListener('change', () => { S = { ...S, mode: sel.value }; $UD.setSettings(S); render(); });
});

function render() {
  if (!sel) return;
  const m = NOTES[S.mode] ? S.mode : 'region';
  sel.value = m;
  note.innerHTML = NOTES[m] + '<br><br>Нужен запущенный Qural с включёнными «Скриншотами».';
}

function apply(p) { S = { ...S, ...p }; render(); }

$UD.onParamFromApp(j => { const p = j.param || {}; if (p.mode) apply(p); else $UD.setSettings(S); });
$UD.onAdd(j => { const p = j.param || {}; if (p.mode) apply(p); });
$UD.onDidReceiveSettings(j => { if (j && j.settings) apply(j.settings); });
