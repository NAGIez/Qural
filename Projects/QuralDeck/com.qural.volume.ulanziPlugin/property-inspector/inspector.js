// Окно настройки кнопки: какая программа, что делает нажатие, шаг.
// Список программ присылает плагин (берёт у Qural): программы из Qural и всё, что сейчас в микшере.
let S = { app: 'active', press: 'mute', step: '' };
let apps = [];
let form, sel, custom, customRow, hidden, note;
const OTHER = '__other__';
const isDial = (Utils.getQueryParams('controller') || '') === 'Encoder';

$UD.connect();

$UD.onConnected(() => {
  form = document.querySelector('#property-inspector');
  sel = document.querySelector('#appSel');
  custom = document.querySelector('#custom');
  customRow = document.querySelector('#customRow');
  hidden = document.querySelector('#app');
  note = document.querySelector('#note');
  document.querySelector('#pressRow').classList.toggle('hidden', isDial);
  note.innerHTML = isDial
    ? 'Крутилка: <b>вправо</b> — громче, <b>влево</b> — тише, с зажатой — шаг больше. <b>Нажатие</b> — без звука.'
    : 'На кнопке видно, сколько сейчас стоит громкость программы.';
  renderApps();
  document.querySelector('.uspi-wrapper').classList.remove('hidden');

  sel.addEventListener('change', () => {
    if (sel.value === OTHER) {
      // своё имя: подставить текущее и сохранять только когда что-то введено
      if (!custom.value.trim() && S.app && S.app !== 'active') custom.value = S.app;
      customRow.classList.remove('hidden'); custom.focus();
      if (!custom.value.trim()) return;
      hidden.value = custom.value.trim();
    }
    else { customRow.classList.add('hidden'); hidden.value = sel.value; }
    save();
  });
  custom.addEventListener('input', Utils.debounce(() => { const v = custom.value.trim(); if (!v) return; hidden.value = v; save(); }));
  form.addEventListener('change', e => { if (e.target !== sel && e.target !== custom) save(); });

  $UD.sendToPlugin({ request: 'apps' });
});

function apply(p) {
  S = { ...S, ...p };
  Utils.setFormValue({ press: S.press, step: S.step }, form);
  hidden.value = S.app || 'active';
  renderApps();
}

function save() {
  const v = Utils.getFormValue(form);
  S = { ...S, press: v.press || 'mute', step: v.step || '', app: hidden.value || 'active' };
  $UD.setSettings(S);
}

const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

function renderApps() {
  if (!sel) return;
  const cur = (S.app || 'active').toLowerCase();
  let html = '<option value="active">Активное окно (в фокусе)</option>';
  let found = cur === 'active';
  for (const a of apps) {
    const state = a.running ? a.volume + ' %' : 'нет звука';
    html += `<option value="${esc(a.process)}">${esc(a.title)} — ${esc(state)}${a.inQural ? '' : ' · ' + esc(a.process)}</option>`;
    if (a.process.toLowerCase() === cur) found = true;
  }
  if (!found) html += `<option value="${esc(S.app)}">${esc(S.app)} (сейчас не в микшере)</option>`;
  html += `<option value="${OTHER}">Другая программа…</option>`;
  sel.innerHTML = html;
  const opt = Array.from(sel.options).find(o => o.value.toLowerCase() === cur);
  sel.value = opt ? opt.value : 'active';
}

$UD.onParamFromApp(j => { const p = j.param || {}; if (p.app) apply(p); else save(); });
$UD.onAdd(j => { const p = j.param || {}; if (p.app) apply(p); });
$UD.onDidReceiveSettings(j => { if (j && j.settings) apply(j.settings); });
$UD.onSendToPropertyInspector(j => {
  const p = j.payload || {};
  if (Array.isArray(p.apps)) { apps = p.apps; renderApps(); }
  if (note && p.soundOn === false) note.innerHTML = '«Звук» в Qural выключен — включите его, иначе кнопка не будет менять громкость.';
});
