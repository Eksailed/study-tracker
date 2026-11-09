'use strict';

/** Верхние вкладки: скользящий индикатор под выбранной вкладкой и меню аккаунта по аватару. */
const mainNav = $('.main-nav');
const accountButton = $('#account-button');
const accountMenu = $('#account-menu');
const tabIndicator = document.createElement('span');
let indicatedTab = null;

tabIndicator.className = 'tab-indicator none';
tabIndicator.setAttribute('aria-hidden', 'true');
mainNav.prepend(tabIndicator);

function placeIndicator() {
  const selected = mainNav.querySelector('.nav-item.selected');
  if (!selected || selected.hidden || !selected.offsetWidth) {
    tabIndicator.classList.add('none');
    indicatedTab = null;
    return;
  }
  // Скользит только при смене вкладки; сдвиг из-за раскладки (появилась вкладка «Владелец», изменилась ширина) — сразу.
  const instant = indicatedTab !== selected || tabIndicator.classList.contains('none');
  if (instant) tabIndicator.classList.add('instant');
  tabIndicator.style.left = `${selected.offsetLeft}px`;
  tabIndicator.style.width = `${selected.offsetWidth}px`;
  tabIndicator.classList.remove('none');
  if (instant) {
    void tabIndicator.offsetWidth;
    tabIndicator.classList.remove('instant');
  }
  if (indicatedTab !== selected && mainNav.scrollWidth > mainNav.clientWidth) selected.scrollIntoView({inline:'center', block:'nearest'});
  indicatedTab = selected;
}

new MutationObserver(records => {
  if (records.some(record => record.target !== tabIndicator)) placeIndicator();
}).observe(mainNav, {attributes:true, attributeFilter:['class', 'hidden'], subtree:true});
new ResizeObserver(placeIndicator).observe(mainNav);
document.fonts.ready.then(placeIndicator);

function setAccountMenu(open) {
  accountMenu.hidden = !open;
  accountButton.setAttribute('aria-expanded', String(open));
}

accountButton.addEventListener('click', () => setAccountMenu(accountMenu.hidden));
// Фокус уходит на кнопку до обработчика пункта: диалог запомнит её и вернёт фокус на видимый элемент.
accountMenu.addEventListener('click', event => {
  if (!event.target.closest('button')) return;
  setAccountMenu(false);
  accountButton.focus();
}, true);
document.addEventListener('click', event => {
  if (!accountMenu.hidden && !accountMenu.contains(event.target) && !accountButton.contains(event.target)) setAccountMenu(false);
});
document.addEventListener('keydown', event => {
  if (event.key !== 'Escape' || accountMenu.hidden) return;
  setAccountMenu(false);
  accountButton.focus();
});
document.addEventListener('account-entered', () => {
  $('#account-avatar').textContent = ($('#account-email').textContent.trim().charAt(0) || '?').toUpperCase();
});
document.addEventListener('account-cleared', () => {
  setAccountMenu(false);
  $('#account-avatar').textContent = '';
});
