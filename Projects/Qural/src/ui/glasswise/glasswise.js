// GlassWise UI — поведение оформления «Стекло». Один файл, без библиотек. Подключайте в <head>:
//   <script src="glasswise/glasswise.js"></script>
// Что делает сам: ставит тему до отрисовки, включает свою полосу прокрутки и плавную прокрутку колесом,
// ставит бегунки во все переключатели [data-seg]. Если человек просил уменьшить движение — всё встаёт на место сразу.
// Вызовы для страницы:
//   GW.theme.get() / GW.theme.set('auto' | 'light' | 'dark')  — тема (хранится в localStorage)
//   GW.seg(root?)          — после перерисовки: бегунок переезжает к пункту с классом .on
//   GW.enter(el)           — мягкое появление нового содержимого
//   GW.menu(button, items, onPick) — всплывающее меню под кнопкой; items: [{ value, label, hint?, on?, danger? } | '-']
//   GW.sheet(el) / GW.sheet.close(el) — окно снизу (.gw-sheet > .gw-sh)
//   GW.toast(text, { action, onAction, ms }) — уведомление внизу, с кнопкой («Вернуть» и т. п.)
// Настройки до подключения: window.GW_OPTIONS = { bars:false, smooth:false, themeKey:'my-theme' }.
(() => {
  if (self.GW) return;
  const OPT = Object.assign({ bars: true, smooth: true, themeKey: 'gw-theme' }, self.GW_OPTIONS || {});
  const RM = matchMedia('(prefers-reduced-motion: reduce)');
  const ready = (fn) => (document.readyState === 'loading' ? document.addEventListener('DOMContentLoaded', fn) : fn());

  // ---------- тема ----------
  const theme = {
    mq: matchMedia('(prefers-color-scheme: dark)'),
    get() { try { return localStorage.getItem(OPT.themeKey) || 'auto'; } catch { return 'auto'; } },
    set(v) { try { localStorage.setItem(OPT.themeKey, v); } catch {} theme.apply(); },
    apply() { const v = theme.get(); document.documentElement.dataset.theme = v === 'auto' ? (theme.mq.matches ? 'dark' : 'light') : v; }
  };
  theme.apply();
  theme.mq.addEventListener('change', () => theme.apply());
  addEventListener('storage', (e) => { if (e.key === OPT.themeKey) theme.apply(); });

  // ---------- бегунок переключателей ----------
  // Страница может перерисовать переключатель посреди движения: бегунок каждый кадр находим заново по имени (data-seg)
  // и меряем место выбранного пункта заново — без рывка и отката.
  const DUR = 300, IN = 260;
  const ease = (t) => 1 - Math.pow(1 - t, 3);
  const last = {}, run = {}, shown = {};
  let raf = 0;
  const groupOf = (name) => document.querySelector(`[data-seg="${CSS.escape(name)}"]`);
  const onOf = (g) => g.querySelector(':scope .on:not(.seg-thumb)');
  function thumbOf(g) {
    let th = g.querySelector(':scope > .seg-thumb');
    if (!th) { th = document.createElement('i'); th.className = 'seg-thumb'; th.setAttribute('aria-hidden', 'true'); g.prepend(th); }
    return th;
  }
  function place(g, on) {
    let x = 0, y = 0;
    for (let e = on; e && e !== g; e = e.offsetParent) { x += e.offsetLeft; y += e.offsetTop; if (e.offsetParent && !g.contains(e.offsetParent)) break; }
    return { x, y, w: on.offsetWidth, h: on.offsetHeight };
  }
  const put = (th, p) => { th.style.translate = `${p.x}px ${p.y}px`; th.style.width = p.w + 'px'; th.style.height = p.h + 'px'; th.style.opacity = 1; };
  const mix = (a, b, k) => ({ x: a.x + (b.x - a.x) * k, y: a.y + (b.y - a.y) * k, w: a.w + (b.w - a.w) * k, h: a.h + (b.h - a.h) * k });
  const near = (a, b) => Math.abs(a.x - b.x) < 0.5 && Math.abs(a.y - b.y) < 0.5 && Math.abs(a.w - b.w) < 0.5 && Math.abs(a.h - b.h) < 0.5;
  const now = (name) => run[name]?.at || run[name]?.from || last[name];
  function frame(t) {
    raf = 0;
    for (const name in run) {
      const g = groupOf(name), on = g && onOf(g);
      if (!on || !on.offsetWidth) { delete run[name]; continue; }
      const r = run[name]; if (r.t0 === Infinity) r.t0 = t;
      const to = place(g, on), k = Math.max(0, Math.min(1, (t - r.t0) / DUR));
      r.to = to; r.at = k >= 1 ? to : mix(r.from, to, ease(k));
      put(thumbOf(g), r.at); last[name] = to;
      if (k >= 1) delete run[name];
    }
    if (Object.keys(run).length) raf = requestAnimationFrame(frame);
  }
  function seg(root = document) {
    const groups = root.matches?.('[data-seg]') ? [root] : root.querySelectorAll('[data-seg]');
    for (const g of groups) {
      const name = g.dataset.seg, th = thumbOf(g), on = onOf(g);
      if (!on || !on.offsetWidth) { th.style.opacity = 0; delete last[name]; delete run[name]; continue; }
      const to = place(g, on), cur = now(name);
      if (run[name] && near(run[name].to, to)) { put(th, cur); continue; }
      if (cur && !RM.matches && !near(cur, to)) {
        run[name] = { from: cur, to, t0: Infinity }; put(th, cur);
        if (!raf) raf = requestAnimationFrame(frame);
      } else { delete run[name]; put(th, to); }
      last[name] = to;
    }
  }
  addEventListener('resize', () => { for (const k in last) delete last[k]; for (const k in run) delete run[k]; seg(); });
  // щелчок по пункту переключателя: пункт становится выбранным (если страница не делает этого сама — data-seg-auto)
  document.addEventListener('click', (e) => {
    const b = e.target.closest?.('[data-seg-auto] > :not(.seg-thumb)'); if (!b) return;
    b.parentElement.querySelectorAll(':scope > .on').forEach((x) => x.classList.remove('on'));
    b.classList.add('on'); seg(b.parentElement);
  });

  // ---------- мягкое появление ----------
  const KF = [{ opacity: 0, translate: '0 6px' }, { opacity: 1, translate: '0 0' }];
  function enter(el, key = el?.id || '') {
    if (!el || RM.matches) return;
    shown[key] = performance.now();
    el.getAnimations().forEach((a) => a.id === 'gw-in' && a.cancel());
    const a = el.animate(KF, { duration: IN, easing: 'cubic-bezier(.2,.8,.2,1)' }); a.id = 'gw-in';
  }

  // ---------- всплывающее меню ----------
  let openMenu = null;
  function closeMenu() { if (!openMenu) return; openMenu.btn.setAttribute('aria-expanded', 'false'); openMenu.el.remove(); openMenu = null; }
  function menu(btn, items, onPick) {
    const same = openMenu?.btn === btn; closeMenu(); if (same) return;
    const el = document.createElement('div'); el.className = 'gw-menu'; el.setAttribute('role', 'menu');
    for (const it of items) {
      if (it === '-') { el.append(Object.assign(document.createElement('div'), { className: 'gw-msep' })); continue; }
      const b = document.createElement('button');
      b.className = 'gw-item' + (it.on ? ' on' : '') + (it.danger ? ' danger' : ''); b.setAttribute('role', 'menuitem');
      b.innerHTML = `<span style="flex:1;min-width:0">${esc(it.label)}${it.hint ? `<small>${esc(it.hint)}</small>` : ''}</span>` +
        '<svg class="chk" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M3.5 8.5l3 3 6-7"/></svg>';
      b.onclick = () => { closeMenu(); onPick?.(it.value, it); };
      el.append(b);
    }
    document.body.append(el);
    const r = btn.getBoundingClientRect(), w = Math.max(el.offsetWidth, r.width);
    el.style.minWidth = r.width + 'px';
    el.style.left = Math.max(8, Math.min(r.left, innerWidth - w - 8)) + 'px';
    const below = r.bottom + 6, h = el.offsetHeight;
    el.style.top = (below + h > innerHeight - 8 && r.top - h - 6 > 8 ? r.top - h - 6 : below) + 'px';
    btn.setAttribute('aria-expanded', 'true');
    openMenu = { btn, el };
    el.querySelector('.gw-item.on, .gw-item')?.focus({ preventScroll: true });
  }
  document.addEventListener('pointerdown', (e) => { if (openMenu && !openMenu.el.contains(e.target) && !openMenu.btn.contains(e.target)) closeMenu(); }, true);
  addEventListener('keydown', (e) => {
    if (e.key === 'Escape') { if (openMenu) { const b = openMenu.btn; closeMenu(); b.focus(); } else document.querySelectorAll('.gw-sheet.show').forEach(sheetClose); }
    if (openMenu && (e.key === 'ArrowDown' || e.key === 'ArrowUp')) {
      e.preventDefault();
      const all = [...openMenu.el.querySelectorAll('.gw-item')], i = all.indexOf(document.activeElement);
      all[(i + (e.key === 'ArrowDown' ? 1 : -1) + all.length) % all.length]?.focus();
    }
  });
  addEventListener('resize', closeMenu);
  addEventListener('scroll', closeMenu, { passive: true, capture: true });
  const esc = (s) => String(s ?? '').replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[c]);

  // ---------- окно снизу ----------
  function sheet(el) {
    el.hidden = false;
    requestAnimationFrame(() => requestAnimationFrame(() => el.classList.add('show')));
    if (!el.__gw) { el.__gw = true; el.addEventListener('pointerdown', (e) => { if (e.target === el) sheetClose(el); }); }
    el.querySelector('input, textarea, button')?.focus({ preventScroll: true });
  }
  function sheetClose(el) {
    el.classList.remove('show');
    setTimeout(() => { if (!el.classList.contains('show')) el.hidden = true; }, RM.matches ? 0 : 300);
  }
  sheet.close = sheetClose;

  // ---------- уведомление ----------
  let toastEl = null, toastT = 0;
  function toast(text, { action, onAction, ms = 4000 } = {}) {
    toastEl?.remove(); clearTimeout(toastT);
    const el = document.createElement('div'); el.className = 'gw-toast'; el.setAttribute('role', 'status');
    el.append(Object.assign(document.createElement('span'), { textContent: text }));
    if (action) { const b = document.createElement('button'); b.textContent = action; b.onclick = () => { el.remove(); onAction?.(); }; el.append(b); }
    document.body.append(el); toastEl = el;
    toastT = setTimeout(() => el.remove(), ms);
  }

  // ---------- своя полоса прокрутки ----------
  // Тонкая полоска у правого края: видна секунду после прокрутки и под курсором, её можно тянуть мышью.
  // Полоса страницы начинается под шапкой с атрибутом data-bar-top. У прокручиваемых блоков — атрибут data-bar.
  function bar(el) {
    const page = !el, sc = () => (page ? document.scrollingElement || document.documentElement : el);
    const b = document.createElement('div'); b.className = 'gw-bar'; b.hidden = true; b.setAttribute('aria-hidden', 'true');
    const edge = page ? document.createElement('div') : null;
    if (edge) { edge.className = 'gw-edge'; document.body.append(edge); }
    document.body.append(b);
    let timer = 0, drag = null;
    function track() {
      if (page) { const h = document.querySelector('[data-bar-top]'), top = (h ? h.getBoundingClientRect().bottom : 0) + 4; return { top, len: innerHeight - top - 4, right: 0 }; }
      const r = el.getBoundingClientRect(), top = Math.max(r.top, 0) + 4, bottom = Math.min(r.bottom, innerHeight) - 4;
      return { top, len: bottom - top, right: innerWidth - r.right };
    }
    function sync(show) {
      const s = sc(), H = s.scrollHeight, h = s.clientHeight, t = track();
      if (H <= h + 1 || t.len < 40) { b.hidden = true; if (edge) edge.hidden = true; return; }
      const len = Math.max(36, Math.round(t.len * h / H));
      b.hidden = false;
      Object.assign(b.style, { height: len + 'px', top: t.top + (t.len - len) * Math.min(1, s.scrollTop / (H - h)) + 'px', right: t.right + 'px' });
      if (edge) { edge.hidden = false; edge.style.top = t.top + 'px'; }
      if (show) { b.classList.add('on'); clearTimeout(timer); timer = setTimeout(hide, 1000); }
    }
    const hover = () => b.matches(':hover') || (edge ? edge.matches(':hover') : el.matches(':hover'));
    function hide() { if (!drag && !hover()) b.classList.remove('on'); else { clearTimeout(timer); timer = setTimeout(hide, 1000); } }
    (page ? window : el).addEventListener('scroll', () => sync(true), { passive: true });
    if (!page) addEventListener('scroll', () => sync(), { passive: true });
    addEventListener('resize', () => sync());
    const ro = new ResizeObserver(() => sync()); ro.observe(document.body); if (!page) ro.observe(el);
    new MutationObserver(() => sync()).observe(page ? document.body : el, { childList: true, subtree: true });
    (edge || el).addEventListener('pointerenter', () => sync(true));
    b.addEventListener('pointerdown', (e) => { e.preventDefault(); drag = { y: e.clientY, s: sc().scrollTop }; b.setPointerCapture(e.pointerId); b.classList.add('on', 'drag'); });
    b.addEventListener('pointermove', (e) => {
      if (!drag) return;
      const s = sc(), free = Math.max(1, track().len - b.offsetHeight);
      s.scrollTo({ top: drag.s + (e.clientY - drag.y) * (s.scrollHeight - s.clientHeight) / free, behavior: 'instant' });
    });
    const up = () => { if (!drag) return; drag = null; b.classList.remove('drag'); sync(true); };
    b.addEventListener('pointerup', up); b.addEventListener('pointercancel', up);
    edge?.addEventListener('pointerdown', (e) => { if (e.target === edge) scrollBy({ top: (e.clientY > b.getBoundingClientRect().top ? 1 : -1) * (track().len - 40) }); });
    sync();
  }

  // ---------- плавная прокрутка колесом ----------
  // Шаг колеса доезжает плавно. Едет ближайший к курсору блок, которому есть куда ехать, иначе страница.
  // Поля ввода, Ctrl (масштаб), Shift и горизонтальная прокрутка — как в браузере.
  function smooth() {
    const runs = new Map();
    const root = () => document.scrollingElement || document.documentElement;
    const maxOf = (el) => el.scrollHeight - el.clientHeight;
    const scrollable = (el) => {
      if (el === root()) return maxOf(el) > 1;
      const o = getComputedStyle(el).overflowY;
      return (o === 'auto' || o === 'scroll' || o === 'overlay') && maxOf(el) > 1;
    };
    const room = (el, dy) => { const y = runs.get(el)?.to ?? el.scrollTop; return dy > 0 ? y < maxOf(el) - 0.5 : y > 0.5; };
    function target(e, dy) {
      for (const n of e.composedPath()) {
        if (!(n instanceof Element)) continue;
        if (n.matches('textarea, input, select, [contenteditable=""], [contenteditable="true"]')) return null;
        if (n === document.body || n === document.documentElement) break;
        if (scrollable(n)) { if (room(n, dy)) return n; if (getComputedStyle(n).overscrollBehaviorY === 'contain') return 'stop'; }
      }
      const r = root(); return scrollable(r) && room(r, dy) ? r : null;
    }
    function step(el) {
      const s = runs.get(el); if (!s) return;
      const moved = el.scrollTop - s.at;
      if (Math.abs(moved) > 40) { runs.delete(el); return; }
      if (moved) s.to = Math.max(0, Math.min(maxOf(el), s.to + moved));
      const d = s.to - el.scrollTop, y = Math.abs(d) < 0.6 ? s.to : el.scrollTop + d * 0.2;
      el.scrollTo({ top: y, behavior: 'instant' }); s.at = el.scrollTop;
      if (y === s.to || Math.abs(el.scrollTop - s.to) < 0.6) { runs.delete(el); return; }
      requestAnimationFrame(() => step(el));
    }
    addEventListener('wheel', (e) => {
      if (RM.matches || e.defaultPrevented || e.ctrlKey || e.shiftKey || Math.abs(e.deltaX) > Math.abs(e.deltaY) || !e.deltaY) return;
      const dy = e.deltaY * (e.deltaMode === 1 ? 18 : e.deltaMode === 2 ? innerHeight * 0.9 : 1);
      const el = target(e, dy);
      if (el === 'stop') { e.preventDefault(); return; }
      if (!el) return;
      e.preventDefault();
      const s = runs.get(el) || { to: el.scrollTop, at: el.scrollTop };
      s.to = Math.max(0, Math.min(maxOf(el), s.to + dy));
      if (!runs.has(el)) { runs.set(el, s); requestAnimationFrame(() => step(el)); }
    }, { passive: false });
  }

  if (OPT.smooth) smooth();
  ready(() => {
    if (OPT.bars) { document.documentElement.classList.add('gw-bars'); bar(null); document.querySelectorAll('[data-bar]').forEach(bar); }
    seg();
  });
  self.GW = { theme, seg, enter, menu, closeMenu, sheet, toast, bar };
})();
