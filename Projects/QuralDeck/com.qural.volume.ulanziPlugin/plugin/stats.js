// Кнопка «Нагрузка ПК» пиксельным шрифтом (как часы): три строки — процессор, видеокарта, оперативная память.
// В строке: название и температура (у памяти — занято ГБ) слева, загрузка справа, полоска снизу.
// Цифры присылает Qural (сообщение stats); −1 — нет данных. Перегрев — температура и полоска жёлтые (от 75°)
// и красные (от 85°); память — жёлтая от 80 %, красная от 90 %. Поля ~28 px — края экрана кнопки скруглены.
import { Canvas } from './pixpng.js';

const W = [255, 255, 255], DIM = [150, 150, 156], OFF = [48, 48, 50], AMBER = [255, 159, 10], RED = [255, 69, 58];
const heat = t => t >= 85 ? RED : t >= 75 ? AMBER : null;
const full = p => p >= 90 ? RED : p >= 80 ? AMBER : null;

// note — текст после названия (температура или ГБ), warn — цвет предупреждения или null
// строка: название и температура / занято гигабайт (G) слева, загрузка крупно справа, полоска снизу
function row(c, y, label, pct, note, warn) {
  c.text(label, 28, y, 2, DIM);
  if (note) c.text(note, 28 + 44, y, 2, warn || DIM);
  // «100%» крупно налезает на температуру — его мельче
  if (pct >= 100) c.textRight('100%', 169, y, 2, W);
  else c.textRight(pct < 0 ? '-' : pct + '%', 169, y - 3, 3, pct < 0 ? DIM : W);
  c.bar(28, y + 22, 141, 6, 12, Math.max(0, pct), warn || W, OFF);
}

// size 196 — кнопка (чёрный фон), 200 — экран крутилки (прозрачный)
export function statsPng(s, size = 196) {
  const c = new Canvas(size, size, size === 200 ? [0, 0, 0, 0] : [0, 0, 0, 255]);
  const y0 = 36 + (size === 200 ? 2 : 0);
  row(c, y0, 'CPU', s.cpu, s.cpuTemp >= 0 ? s.cpuTemp + '°' : '', s.cpuTemp >= 0 ? heat(s.cpuTemp) : null);
  row(c, y0 + 46, 'GPU', s.gpu, s.gpuTemp >= 0 ? s.gpuTemp + '°' : '', s.gpuTemp >= 0 ? heat(s.gpuTemp) : null);
  row(c, y0 + 92, 'RAM', s.ram, s.ramGb >= 0 ? s.ramGb + 'G' : '', s.ram >= 0 ? full(s.ram) : null);   // занято гигабайт
  return c.png();
}

export function statsTitle(s) {
  const p = [];
  if (s.cpu >= 0) p.push('CPU ' + s.cpu + '%' + (s.cpuTemp >= 0 ? ' ' + s.cpuTemp + '°' : ''));
  if (s.gpu >= 0) p.push('GPU ' + s.gpu + '%' + (s.gpuTemp >= 0 ? ' ' + s.gpuTemp + '°' : ''));
  if (s.ram >= 0) p.push('RAM ' + s.ram + '%');
  return p.join(' · ') || 'нет данных';
}
