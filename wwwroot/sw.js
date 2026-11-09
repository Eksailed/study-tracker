'use strict';

const CACHE = 'zachet-shell';
const SHELL = ['/', '/index.html', '/app.css', '/app.js', '/notifications.js', '/api-access.js', '/import.js', '/calendar.js', '/subtasks.js', '/pwa.js', '/favicon.svg', '/icon-192.png', '/icon-512.png'];
const SKIP = ['/api', '/health', '/calendar.ics'];

self.addEventListener('install', event => {
  event.waitUntil(caches.open(CACHE).then(cache => cache.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener('activate', event => {
  event.waitUntil(caches.keys()
    .then(names => Promise.all(names.filter(name => name !== CACHE).map(name => caches.delete(name))))
    .then(() => self.clients.claim()));
});

/** Только оболочка: данные, проверка здоровья и лента календаря идут мимо воркера и в кэш не попадают. */
function isShell(request) {
  if (request.method !== 'GET') return false;
  const url = new URL(request.url);
  if (url.origin !== self.location.origin) return false;
  if (SKIP.some(prefix => url.pathname === prefix || url.pathname.startsWith(prefix + '/'))) return false;
  return SHELL.includes(url.pathname);
}

self.addEventListener('fetch', event => {
  const request = event.request;
  if (!isShell(request)) return;
  event.respondWith(fetch(request).then(response => {
    if (response.ok) {
      const copy = response.clone();
      caches.open(CACHE).then(cache => cache.put(request, copy));
    }
    return response;
  }).catch(() => caches.match(request, {ignoreSearch:true})));
});
