'use strict';

let calendarVersion = 0;
let calendarExists = false;
let calendarBusy = false;

function setCalendarBusy(busy) {
  calendarBusy = busy;
  for (const id of ['#calendar-create','#calendar-disable','#calendar-confirm-yes','#calendar-confirm-no']) $(id).disabled = busy;
}

function clearCalendarState() {
  calendarVersion++;
  calendarExists = false;
  $('#calendar-url').value = '';
  $('#calendar-issued').hidden = true;
  $('#calendar-confirm').hidden = true;
  $('#calendar-copy').textContent = 'Скопировать';
  $('#calendar-state').textContent = 'Загружаем…';
  $('#calendar-problem').textContent = '';
  $('#calendar-disable').hidden = true;
  $('#calendar-create').hidden = true;
  setCalendarBusy(false);
}

function renderCalendar(status) {
  calendarExists = status.exists;
  $('#calendar-state').textContent = status.exists
    ? `Лента включена. Ссылка выдана ${new Date(status.createdAt).toLocaleString('ru-RU',{day:'2-digit',month:'2-digit',year:'numeric',hour:'2-digit',minute:'2-digit'})}.`
    : 'Ленты пока нет.';
  $('#calendar-create').textContent = status.exists ? 'Перевыпустить и отозвать старую' : 'Создать ссылку';
  $('#calendar-create').hidden = false;
  $('#calendar-disable').hidden = !status.exists;
}

function calendarIsCurrent(generation, version) {
  return currentUser && generation === sessionGeneration && version === calendarVersion && $('#calendar-dialog').open;
}

async function runCalendar(action) {
  if (calendarBusy) return;
  const generation = sessionGeneration, version = calendarVersion;
  $('#calendar-problem').textContent = '';
  $('#calendar-confirm').hidden = true;
  setCalendarBusy(true);
  try {
    await action(generation, version);
  } catch (error) {
    if (error.status === 401 || !calendarIsCurrent(generation,version)) return;
    $('#calendar-problem').textContent = error.message || 'Сервер недоступен.';
  } finally {
    if (version === calendarVersion) setCalendarBusy(false);
  }
}

async function issueCalendar(generation, version) {
  const created = await api('calendar','POST',{});
  if (!calendarIsCurrent(generation,version)) return;
  $('#calendar-url').value = `${location.origin}/calendar.ics?key=${encodeURIComponent(created.key)}`;
  $('#calendar-issued').hidden = false;
  $('#calendar-copy').textContent = 'Скопировать';
  renderCalendar({exists:true, createdAt:created.createdAt});
  $('#calendar-url').focus();
  $('#calendar-url').select();
}

async function disableCalendar(generation, version) {
  await api('calendar','DELETE');
  if (!calendarIsCurrent(generation,version)) return;
  $('#calendar-url').value = '';
  $('#calendar-issued').hidden = true;
  renderCalendar({exists:false});
  notify('Лента выключена');
}

$('#open-calendar').addEventListener('click',async()=> {
  if (!ready) return;
  clearCalendarState();
  $('#calendar-dialog').showModal();
  const generation = sessionGeneration, version = calendarVersion;
  try {
    const status = await api('calendar');
    if (calendarIsCurrent(generation,version)) renderCalendar(status);
  } catch (error) {
    if (error.status === 401 || !calendarIsCurrent(generation,version)) return;
    $('#calendar-state').textContent = 'Не удалось загрузить состояние ленты.';
    $('#calendar-problem').textContent = error.message || 'Сервер недоступен.';
  }
});
$('#calendar-dialog').addEventListener('close',clearCalendarState);
document.addEventListener('account-cleared',clearCalendarState);

$('#calendar-create').addEventListener('click',()=> {
  if (calendarBusy) return;
  if (calendarExists) $('#calendar-confirm').hidden = false;
  else runCalendar(issueCalendar);
});
$('#calendar-confirm-no').addEventListener('click',()=> { $('#calendar-confirm').hidden = true; });
$('#calendar-confirm-yes').addEventListener('click',()=>runCalendar(issueCalendar));
$('#calendar-disable').addEventListener('click',()=>runCalendar(disableCalendar));

$('#calendar-copy').addEventListener('click',async()=> {
  const field = $('#calendar-url');
  field.select();
  try {
    await navigator.clipboard.writeText(field.value);
    $('#calendar-copy').textContent = 'Скопировано';
    notify('Ссылка скопирована');
  } catch {
    notify('Не удалось скопировать автоматически. Выдели ссылку и скопируй вручную.',true);
  }
});
