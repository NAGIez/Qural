// Окно настройки кнопки «Профиль цвета»: какой профиль включать (или следующий по кругу).
// Список профилей присылает плагин (берёт у Qural).
let S = { profile: 'next' };
let profiles = [];
let form, sel, note;
const isDial = (Utils.getQueryParams('controller') || '') === 'Encoder';

$UD.connect();

$UD.onConnected(() => {
  form = document.querySelector('#property-inspector');
  sel = document.querySelector('#profile');
  note = document.querySelector('#note');
  note.innerHTML = isDial
    ? 'Крутилка: <b>вправо</b> — следующий профиль, <b>влево</b> — предыдущий. <b>Нажатие</b> — выбранный здесь профиль.'
    : 'Нажатие включает профиль. На кнопке видно, какой профиль сейчас включён.';
  render();
  document.querySelector('.uspi-wrapper').classList.remove('hidden');
  sel.addEventListener('change', () => { S = { ...S, profile: sel.value }; $UD.setSettings(S); });
  $UD.sendToPlugin({ request: 'apps' });
});

const esc = s => String(s).replace(/[&<>"]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));

function render() {
  if (!sel) return;
  const cur = S.profile || 'next';
  let html = '<option value="next">Следующий по кругу</option>';
  let found = cur === 'next';
  profiles.forEach((p, i) => {
    html += `<option value="${esc(p)}">${i + 1}. ${esc(p)}</option>`;
    if (p.trim().toLowerCase() === cur.trim().toLowerCase()) found = true;
  });
  if (!found) html += `<option value="${esc(cur)}">${esc(cur)} (нет в Qural)</option>`;
  sel.innerHTML = html;
  const opt = Array.from(sel.options).find(o => o.value.trim().toLowerCase() === cur.trim().toLowerCase());
  sel.value = opt ? opt.value : 'next';
}

function apply(p) { S = { ...S, ...p }; render(); }

$UD.onParamFromApp(j => { const p = j.param || {}; if (p.profile) apply(p); else $UD.setSettings(S); });
$UD.onAdd(j => { const p = j.param || {}; if (p.profile) apply(p); });
$UD.onDidReceiveSettings(j => { if (j && j.settings) apply(j.settings); });
$UD.onSendToPropertyInspector(j => {
  const p = j.payload || {};
  if (Array.isArray(p.profiles)) { profiles = p.profiles; render(); }
  if (note && p.colorOn === false) note.innerHTML = '«Цвет» в Qural выключен — включите его, иначе профиль не применится к мониторам.';
});
