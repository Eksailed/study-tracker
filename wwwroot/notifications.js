'use strict';

let notificationTimer;
let notificationVersion = 0;
let notificationCodeDeadline = 0;
let notificationWaitingForTest = false;
let notificationLoaded = false;
let notificationConnected = false;

function clearNotificationState() {
  clearTimeout(notificationTimer);
  notificationVersion++;
  notificationCodeDeadline = 0;
  notificationWaitingForTest = false;
  notificationLoaded = false;
  notificationConnected = false;
  $('#notifications-form').reset();
  $('#notifications-fields').disabled = true;
  $('#telegram-open').removeAttribute('href');
  $('#telegram-link-panel').hidden = true;
  $('#telegram-problem').textContent = '';
  $('#telegram-status').textContent = 'Проверяем подключение…';
  $('#telegram-connect').disabled = true;
  $('#telegram-test').disabled = false;
  $('#telegram-test').hidden = true;
  $('#telegram-disconnect').disabled = false;
  $('#telegram-disconnect').hidden = true;
  $('#notifications-refresh').disabled = false;
  $('#notification-last').textContent = 'Отправок пока нет.';
  $('.form-error',$('#notifications-form')).textContent = '';
}

async function loadNotificationSettings(fill=false) {
  const generation = sessionGeneration, version = notificationVersion;
  const data = await api('notifications/settings');
  if (!currentUser || generation !== sessionGeneration || version !== notificationVersion || !$('#notifications-dialog').open) return;
  const form = $('#notifications-form');
  const connected = data.connected;
  notificationConnected = connected;
  const usable = connected && data.ready && !data.connectionProblem;
  $('#telegram-status').textContent = connected ? 'Личный чат подключён' : data.configured ? data.ready ? 'Подключи личный чат с ботом' : data.botStatus : 'Бот пока не настроен. Задания работают как обычно.';
  $('#telegram-problem').textContent = data.connectionProblem || '';
  $('#telegram-connect').disabled = !data.ready;
  $('#telegram-connect').textContent = connected ? 'Переподключить Telegram' : notificationCodeDeadline > Date.now() ? 'Новая ссылка' : 'Подключить Telegram';
  $('#telegram-test').hidden = !usable;
  $('#telegram-disconnect').hidden = !connected && !data.codeExpiresAt;
  if (connected) {
    $('#telegram-link-panel').hidden = true;
    $('#telegram-open').removeAttribute('href');
    notificationCodeDeadline = 0;
  } else if (notificationCodeDeadline && notificationCodeDeadline <= Date.now()) {
    $('#telegram-link-panel').hidden = true;
    $('#telegram-open').removeAttribute('href');
    $('#telegram-problem').textContent = 'Срок ссылки истёк. Получи новую ссылку.';
    notificationCodeDeadline = 0;
  }
  if (fill || !notificationLoaded) {
    form.elements.enabled.checked = data.enabled;
    form.elements.timeZone.value = data.timeZone;
    form.elements.beforeEnabled.checked = data.beforeEnabled;
    form.elements.beforeTime.value = data.beforeTime;
    form.elements.dueEnabled.checked = data.dueEnabled;
    form.elements.dueTime.value = data.dueTime;
    notificationLoaded = true;
  }
  $('#notifications-fields').disabled = false;
  form.elements.enabled.disabled = !usable;
  if (!usable) form.elements.enabled.checked = false;
  const labels = {Pending:'Ожидает отправки',Sending:'Отправляется',Sent:'Отправлено',Failed:'Не отправлено: ошибка',Unknown:'Результат неизвестен. Автоматический повтор не выполняется.',Cancelled:'Отменено',Skipped:'Пропущено: время отправки истекло'};
  $('#notification-last').textContent = data.lastDelivery ? labels[data.lastDelivery.state] || 'Результат неизвестен' : 'Отправок пока нет.';
  if (data.lastDelivery && !['Pending','Sending'].includes(data.lastDelivery.state)) notificationWaitingForTest = false;
  clearTimeout(notificationTimer);
  if (notificationCodeDeadline > Date.now() || notificationWaitingForTest) {
    notificationTimer = setTimeout(async()=> {
      if (!$('#notifications-dialog').open || !currentUser || generation !== sessionGeneration) return;
      try { await loadNotificationSettings(); }
      catch(error) { if (error.status !== 401) $('#telegram-problem').textContent = 'Не удалось обновить состояние. Нажми «Обновить».'; }
    },5000);
  }
}

$('#open-notifications').addEventListener('click',async()=> {
  if (!ready) return;
  clearNotificationState();
  $('#notifications-dialog').showModal();
  try { await loadNotificationSettings(true); }
  catch(error) { if (error.status !== 401) $('#telegram-problem').textContent = error.message || 'Сервер недоступен.'; }
});
$('#notifications-dialog').addEventListener('close',clearNotificationState);
document.addEventListener('account-cleared',clearNotificationState);

async function notificationAction(button, action) {
  const version = notificationVersion;
  button.disabled = true; $('#telegram-problem').textContent = '';
  try { await action(); }
  catch(error) { if (version === notificationVersion && error.status !== 401) $('#telegram-problem').textContent = error.message || 'Сервер недоступен.'; }
  finally { if (version === notificationVersion) button.disabled = false; }
}

$('#notifications-refresh').addEventListener('click',event => notificationAction(event.currentTarget,()=>loadNotificationSettings()));
$('#telegram-connect').addEventListener('click',event => notificationAction(event.currentTarget,async()=> {
  const version = notificationVersion;
  if (notificationConnected) {
    $('#telegram-problem').textContent = 'Для новой привязки сначала нажми «Удалить привязку».';
    return;
  }
  const data = await api('notifications/code','POST');
  if (version !== notificationVersion || !$('#notifications-dialog').open) return;
  $('#telegram-open').href = data.url;
  $('#telegram-link-panel').hidden = false;
  notificationCodeDeadline = Date.now()+Math.max(0,data.expiresAt-data.serverNow);
  $('#telegram-link-expiry').textContent = 'Ссылка действует 10 минут и используется один раз.';
  await loadNotificationSettings();
}));
$('#telegram-disconnect').addEventListener('click',event => notificationAction(event.currentTarget,async()=> {
  await api('notifications/connection','DELETE');
  clearNotificationState();
  await loadNotificationSettings(true);
  notify('Привязка удалена. Ожидающие сообщения отменены.');
}));
$('#telegram-test').addEventListener('click',event => notificationAction(event.currentTarget,async()=> {
  const version = notificationVersion;
  await api('notifications/test','POST');
  if (version !== notificationVersion || !$('#notifications-dialog').open) return;
  notificationWaitingForTest = true;
  await loadNotificationSettings();
  notify('Проверочное сообщение поставлено в очередь');
}));
$('#notifications-form').addEventListener('submit',event => submitForm(event,async form=> {
  if (!notificationLoaded) throw new Error('Сначала дождись загрузки настроек.');
  await api('notifications/settings','PUT',{
    enabled:form.elements.enabled.checked,timeZone:form.elements.timeZone.value,
    beforeEnabled:form.elements.beforeEnabled.checked,beforeTime:form.elements.beforeTime.value,
    dueEnabled:form.elements.dueEnabled.checked,dueTime:form.elements.dueTime.value
  });
  await loadNotificationSettings(true);
  notify('Настройки уведомлений сохранены');
}));
