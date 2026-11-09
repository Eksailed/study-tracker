'use strict';

/** Светлая и тёмная тема: выбор хранится в localStorage, пока его нет — берётся системная. */
(() => {
  const KEY = 'theme';
  const COLORS = {light: '#6c47ff', dark: '#15132a'};
  const root = document.documentElement;
  const system = matchMedia('(prefers-color-scheme: dark)');

  function stored() {
    try {
      const value = localStorage.getItem(KEY);
      return value === 'dark' || value === 'light' ? value : null;
    } catch {
      return null;
    }
  }

  function apply(theme) {
    root.dataset.theme = theme;
    const meta = document.querySelector('meta[name="theme-color"]');
    if (meta) meta.content = COLORS[theme];
    const toggle = document.getElementById('theme-toggle');
    if (toggle) toggle.setAttribute('aria-pressed', String(theme === 'dark'));
  }

  function current() {
    return stored() || (system.matches ? 'dark' : 'light');
  }

  function choose(theme) {
    try {
      localStorage.setItem(KEY, theme);
    } catch {
      // приватный режим: тема переключится, но не запомнится
    }
    root.classList.add('theme-fade');
    apply(theme);
    setTimeout(() => root.classList.remove('theme-fade'), 400);
  }

  apply(current());
  system.addEventListener('change', () => { if (!stored()) apply(current()); });
  addEventListener('storage', event => { if (event.key === KEY) apply(current()); });
  document.addEventListener('DOMContentLoaded', () => {
    const toggle = document.getElementById('theme-toggle');
    if (!toggle) return;
    toggle.setAttribute('aria-pressed', String(root.dataset.theme === 'dark'));
    toggle.addEventListener('click', () => choose(root.dataset.theme === 'dark' ? 'light' : 'dark'));
  });
})();
