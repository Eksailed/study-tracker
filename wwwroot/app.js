'use strict';

const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const icon = name => `<svg class="icon" aria-hidden="true"><use href="#i-${name}"/></svg>`;
const escapeHtml = value => String(value).replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
const dateKey = date => `${date.getFullYear()}-${String(date.getMonth()+1).padStart(2,'0')}-${String(date.getDate()).padStart(2,'0')}`;
const localDate = key => new Date(`${key}T12:00:00`);
const today = () => dateKey(new Date());
const formatDate = key => localDate(key).toLocaleDateString('ru-RU', {day:'numeric', month:'short'});
const statuses = {Todo:'Нужно сделать', InProgress:'В работе', Done:'Выполнено'};
const titles = {active:'Мои задания', upcoming:'На этой неделе', done:'Выполненные', all:'Все задания', overdue:'Срок прошёл'};
const state = {subjects:[], assignments:[], view:'active', subject:null, status:'any', query:'', sort:'due', date:null};
let csrfToken;
let editingId = null;
let pendingTask = false;
let deleting = null;
let toastTimer;
let ready = false;
let currentUser = null;
let sessionGeneration = 0;
let authMode = 'login';
let allowRegistration = false;
let savedDraft = null;

function weekDates() {
  const first = new Date();
  first.setHours(12,0,0,0);
  first.setDate(first.getDate() - (first.getDay()+6)%7);
  return Array.from({length:7}, (_,i) => {
    const day = new Date(first);
    day.setDate(first.getDate()+i);
    return dateKey(day);
  });
}

async function api(path, method='GET', data) {
  const generation = sessionGeneration;
  const response = await fetch(`/api/${path}`, {
    method, credentials:'same-origin',
    headers:method === 'GET' ? {} : {'Content-Type':'application/json', 'X-CSRF-TOKEN':csrfToken},
    body:data === undefined ? undefined : JSON.stringify(data)
  });
  const account = response.headers.get('X-Account-Id');
  if ((path.startsWith('assignments') || path.startsWith('subjects')) && currentUser && account && account !== currentUser.id) {
    savedDraft = null;
    showLogin('В другой вкладке изменился аккаунт. Войдите, чтобы продолжить.',false);
    const error = new Error('Аккаунт изменился. Войдите снова.'); error.status = 401; throw error;
  }
  if (!response.ok) {
    const result = await response.json().catch(() => ({}));
    if (response.status === 401 && !path.startsWith('auth/')) showLogin('Сеанс завершился. Войдите снова.');
    const error = new Error(result.error || (response.status === 404 ? 'Запись уже удалена. Обновите страницу.' : response.status === 401 ? 'Войдите в аккаунт.' : 'Не удалось выполнить действие. Проверьте данные и попробуйте снова.'));
    error.status = response.status;
    throw error;
  }
  if ((path.startsWith('assignments') || path.startsWith('subjects')) && generation !== sessionGeneration)
    throw new Error('Аккаунт изменился. Повторите действие.');
  return response.status === 204 ? null : response.json();
}

function notify(message, error=false) {
  clearTimeout(toastTimer);
  const toast = $('#toast');
  toast.textContent = message;
  toast.classList.toggle('error',error);
  toast.hidden = false;
  toastTimer = setTimeout(() => { toast.hidden = true; }, error ? 7000 : 3500);
}

async function loadData() {
  const generation = sessionGeneration;
  const [subjects,assignments] = await Promise.all([api('subjects'),api('assignments')]);
  if (generation !== sessionGeneration || !currentUser) return;
  state.subjects = subjects;
  state.assignments = assignments;
  ready = true;
  render();
}

async function refreshAfterChange(message) {
  try { await loadData(); notify(message); }
  catch (error) { if (error.status !== 401) notify('Изменения сохранены, но список не обновился. Обновите страницу.',true); }
}

function subjectOf(task) {
  return state.subjects.find(s => s.id === task.subjectId) || {name:'Предмет удалён',color:'slate'};
}

function deadline(task) {
  if (task.status === 'Done') return formatDate(task.dueDate);
  if (task.dueDate === today()) return 'Сегодня';
  const tomorrow = new Date(); tomorrow.setDate(tomorrow.getDate()+1);
  if (task.dueDate === dateKey(tomorrow)) return 'Завтра';
  return `${formatDate(task.dueDate)}${task.dueDate < today() ? ' · срок прошёл' : ''}`;
}

function render() {
  const active = state.assignments.filter(a => a.status !== 'Done');
  const done = state.assignments.length - active.length;
  const overdue = active.filter(a => a.dueDate < today()).length;
  const week = weekDates();
  const inWeek = active.filter(a => week.includes(a.dueDate)).length;
  $('#today-label').textContent = new Date().toLocaleDateString('ru-RU',{weekday:'long',day:'numeric',month:'long'});
  const hour = new Date().getHours();
  $('#greeting').textContent = hour < 6 ? 'Даже ночью — в своём темпе' : hour < 12 ? 'Доброе утро. Начнём без спешки' : hour < 18 ? 'Хорошего учебного дня' : 'Добрый вечер. Что осталось на сегодня?';
  $('#active-count').textContent = active.length;
  $('#stat-active').textContent = active.length;
  $('#stat-overdue').textContent = overdue;
  $('#stat-week').textContent = inWeek;
  $('#stat-done').textContent = done;
  $('#page-title').textContent = state.subject ? state.subjects.find(s => s.id === state.subject)?.name || titles[state.view] : titles[state.view];
  $('#breadcrumb').textContent = $('#page-title').textContent;
  $$('[data-view]').forEach(button => {
    const selected = button.dataset.view === state.view;
    button.classList.toggle('selected',selected);
    if (selected) button.setAttribute('aria-current','page'); else button.removeAttribute('aria-current');
  });
  $$('.tab').forEach(button => {
    button.classList.toggle('active',button.dataset.status === state.status);
    button.setAttribute('aria-pressed',String(button.dataset.status === state.status));
  });
  $('.tabs').hidden = state.view === 'done';
  renderSubjects(active);
  renderAssignments(week);
  renderPlanning(active,done,week);
}

function renderSubjects(active) {
  $('#subjects').innerHTML = state.subjects.length ? state.subjects.map(subject => `
    <div class="subject-row ${state.subject === subject.id ? 'selected' : ''}">
      <button class="subject-link ${subject.color}" data-subject="${subject.id}" aria-pressed="${state.subject === subject.id}">
        <span class="subject-dot"></span><span class="subject-name">${escapeHtml(subject.name)}</span>
        <span class="subject-number">${active.filter(a => a.subjectId === subject.id).length}</span>
      </button>
      <button class="icon-button" data-delete-subject="${subject.id}" aria-label="Удалить предмет ${escapeHtml(subject.name)}">${icon('trash')}</button>
    </div>`).join('') : '<p class="sidebar-empty">Добавь первый предмет<br>с помощью кнопки «+».</p>';
}

function renderAssignments(week) {
  let tasks = state.assignments.filter(a => {
    if (state.view === 'active' && a.status === 'Done') return false;
    if (state.view === 'done' && a.status !== 'Done') return false;
    if (state.view === 'overdue' && (a.status === 'Done' || a.dueDate >= today())) return false;
    if (state.view === 'upcoming' && (a.status === 'Done' || !week.includes(a.dueDate))) return false;
    if (state.subject && a.subjectId !== state.subject) return false;
    if (state.status !== 'any' && a.status !== state.status) return false;
    if (state.date && a.dueDate !== state.date) return false;
    return `${a.title} ${a.description} ${subjectOf(a).name}`.toLocaleLowerCase('ru').includes(state.query.toLocaleLowerCase('ru'));
  });
  tasks.sort((a,b) => state.sort === 'new' ? b.id-a.id : state.sort === 'subject'
    ? subjectOf(a).name.localeCompare(subjectOf(b).name,'ru') || a.dueDate.localeCompare(b.dueDate)
    : a.dueDate.localeCompare(b.dueDate) || a.id-b.id);
  const filtered = Boolean(state.subject || state.date || state.query || state.status !== 'any');
  $('#filter-info').hidden = !filtered;
  $('#filter-caption').textContent = [state.subject && subjectOf({subjectId:state.subject}).name,
    state.date && formatDate(state.date),state.query && `Поиск: ${state.query}`,
    state.status !== 'any' && statuses[state.status]].filter(Boolean).join(' · ');
  $('#list-caption').textContent = state.view === 'done' ? 'Уже позади' : 'Твои задания';
  $('#list-total').textContent = `Показано заданий: ${tasks.length}`;
  if (!tasks.length) {
    const noData = state.assignments.length === 0;
    const noSubjects = state.subjects.length === 0;
    $('#assignment-list').innerHTML = `<div class="empty-state">
      <div class="empty-notebook" aria-hidden="true">${icon('check')}</div>
      <h2>${noData ? 'Новый семестр — чистый лист' : filtered ? 'Ничего не нашлось' : state.view === 'done' ? 'Здесь будут твои победы' : 'Можно выдохнуть'}</h2>
      <p>${noData ? noSubjects ? 'Добавь первый предмет, а затем задание.<br>Сроки и планы больше не придётся держать в голове.' : 'Добавь первое задание.<br>Мы поможем следить за сроками и видеть прогресс.' : filtered ? 'Попробуй другой запрос или сбрось фильтры.' : state.view === 'done' ? 'Отмечай задания выполненными — они появятся здесь.' : 'В этом разделе пока нет заданий.<br>Можно переключиться на все задания или добавить новое.'}</p>
      <button class="${filtered ? 'secondary' : 'primary'}-button" data-empty-action="${filtered ? 'reset' : noSubjects ? 'subject' : 'task'}">${icon(filtered ? 'close' : 'plus')}${filtered ? 'Сбросить фильтры' : noSubjects ? 'Добавить первый предмет' : 'Добавить задание'}</button>
    </div>`;
    return;
  }
  $('#assignment-list').innerHTML = tasks.map(task => {
    const subject = subjectOf(task);
    const isDone = task.status === 'Done';
    return `<article class="task-row ${isDone ? 'done' : ''}">
      <button class="task-check" data-toggle="${task.id}" aria-pressed="${isDone}" aria-label="${isDone ? 'Вернуть в работу' : 'Отметить выполненным'}: ${escapeHtml(task.title)}">${icon('check')}</button>
      <div class="task-main"><button class="task-title" data-edit="${task.id}">${escapeHtml(task.title)}</button>
      ${task.description ? `<p class="task-description">${escapeHtml(task.description)}</p>` : ''}
      <div class="task-meta"><span class="subject-tag ${subject.color}">${escapeHtml(subject.name)}</span>
      <span class="due-label ${!isDone && task.dueDate < today() ? 'overdue' : ''}">${icon('calendar')}${deadline(task)}</span>
      <span class="status-tag status-${task.status.toLowerCase()}">${statuses[task.status]}</span></div></div>
      <div class="task-actions"><button class="icon-button" data-edit="${task.id}" aria-label="Изменить задание ${escapeHtml(task.title)}">${icon('edit')}</button><button class="icon-button" data-delete-task="${task.id}" aria-label="Удалить задание ${escapeHtml(task.title)}">${icon('trash')}</button></div>
    </article>`;
  }).join('');
}

function renderPlanning(active,done,week) {
  $('#week-range').textContent = `${formatDate(week[0])} — ${formatDate(week[6])}`;
  $('#week-days').innerHTML = week.map((key,i) => {
    const count = active.filter(a => a.dueDate === key).length;
    return `<button class="week-day ${key === today() ? 'today' : ''} ${state.date === key ? 'chosen' : ''}" data-date="${key}" aria-label="${formatDate(key)}, невыполненных заданий: ${count}" aria-pressed="${state.date === key}"><span>${['пн','вт','ср','чт','пт','сб','вс'][i]}</span><strong>${localDate(key).getDate()}</strong><i class="day-dot ${count ? 'has-tasks' : ''}"></i></button>`;
  }).join('');
  const nearest = [...active].sort((a,b) => a.dueDate.localeCompare(b.dueDate) || a.id-b.id)[0];
  $('#nearest-task').innerHTML = nearest ? `<p class="nearest-name">${escapeHtml(nearest.title)}</p><div class="nearest-subject">${escapeHtml(subjectOf(nearest).name)}</div><div class="nearest-deadline">${icon('clock')}${deadline(nearest)}</div>` : '<p class="nearest-empty">Пока нет заданий со сроком.<br>Здесь появится самое ближайшее.</p>';
  const percent = state.assignments.length ? Math.round(done/state.assignments.length*100) : 0;
  $('#progress-percent').innerHTML = `${percent}<span>%</span>`;
  $('#progress-circle').setAttribute('stroke-dasharray',`${percent} ${100-percent}`);
  $('#progress-caption').textContent = !done ? 'Первое выполненное задание — начало прогресса.' : active.length ? `Выполнено ${done} из ${state.assignments.length}. Продолжай в своём темпе.` : 'Все задания выполнены. Время для заслуженного отдыха!';
  $('#legend-done').textContent = done;
  $('#legend-left').textContent = active.length;
}

function resetFilters() {
  Object.assign(state,{subject:null,status:'any',query:'',date:null});
  $('#search').value = '';
  render();
}

function setView(view) { state.view = view; resetFilters(); }

function openSubject() {
  const form = $('#subject-form'); form.reset(); $('.form-error',form).textContent = '';
  $('#subject-dialog').showModal();
}

function openTask(id=null) {
  if (!ready) return notify('Данные ещё загружаются. Попробуйте через несколько секунд.',true);
  if (!state.subjects.length) { pendingTask = true; openSubject(); return; }
  editingId = id;
  const task = state.assignments.find(a => a.id === id);
  const form = $('#task-form'); form.reset();
  $('.form-error',form).textContent = '';
  $('#task-dialog-title').textContent = task ? 'Изменить задание' : 'Новое задание';
  form.elements.subjectId.innerHTML = state.subjects.map(s => `<option value="${s.id}">${escapeHtml(s.name)}</option>`).join('');
  form.elements.title.value = task?.title || '';
  form.elements.description.value = task?.description || '';
  form.elements.subjectId.value = task?.subjectId || state.subject || state.subjects[0].id;
  form.elements.dueDate.value = task?.dueDate || state.date || today();
  form.elements.status.value = task?.status || 'Todo';
  $('#task-dialog').showModal();
}

function confirmDelete(kind,id) {
  const record = (kind === 'task' ? state.assignments : state.subjects).find(a => a.id === id);
  if (!record) return;
  deleting = {kind,id};
  $('#confirm-title').textContent = kind === 'task' ? 'Удалить задание?' : 'Удалить предмет?';
  $('#confirm-message').textContent = `«${record.title || record.name}» будет удалён${kind === 'task' ? 'о' : ''}. Отменить это действие не получится.`;
  $('.form-error',$('#confirm-form')).textContent = '';
  $('#confirm-dialog').showModal();
}

async function submitForm(event,action) {
  event.preventDefault();
  const form = event.currentTarget;
  const submit = $('[type=submit]',form);
  if (submit.disabled) return;
  submit.disabled = true;
  $('.form-error',form).textContent = '';
  try { await action(form); }
  catch (error) { $('.form-error',form).textContent = error.message || 'Сервер недоступен. Проверьте подключение.'; }
  finally { submit.disabled = false; }
}

$('#task-form').addEventListener('submit',event => submitForm(event,async form => {
  const data = new FormData(form);
  await api(editingId ? `assignments/${editingId}` : 'assignments',editingId ? 'PUT' : 'POST',{
    title:data.get('title'),description:data.get('description'),subjectId:Number(data.get('subjectId')),
    dueDate:data.get('dueDate'),status:data.get('status')
  });
  $('#task-dialog').close();
  await refreshAfterChange(editingId ? 'Задание обновлено' : 'Задание добавлено');
}));

$('#subject-form').addEventListener('submit',event => submitForm(event,async form => {
  const data = new FormData(form);
  const subject = await api('subjects','POST',{name:data.get('name'),color:data.get('color')});
  const shouldOpenTask = pendingTask;
  pendingTask = false;
  $('#subject-dialog').close();
  state.subject = subject.id;
  state.view = 'active';
  await refreshAfterChange('Предмет добавлен');
  if (shouldOpenTask) openTask();
}));

$('#confirm-form').addEventListener('submit',event => submitForm(event,async () => {
  if (!deleting) return;
  await api(`${deleting.kind === 'task' ? 'assignments' : 'subjects'}/${deleting.id}`,'DELETE');
  if (deleting.kind === 'subject' && state.subject === deleting.id) state.subject = null;
  $('#confirm-dialog').close();
  await refreshAfterChange(deleting.kind === 'task' ? 'Задание удалено' : 'Предмет удалён');
  deleting = null;
}));

document.addEventListener('click',async event => {
  const button = event.target.closest('button');
  if (!button) return;
  if (button.classList.contains('close-dialog')) { button.closest('dialog').close(); return; }
  if (!ready && (button.dataset.view || button.dataset.summary || button.id === 'add-subject')) return;
  if (button.dataset.view) setView(button.dataset.view);
  if (button.dataset.summary) setView(button.dataset.summary);
  if (button.dataset.subject) {
    state.subject = state.subject === Number(button.dataset.subject) ? null : Number(button.dataset.subject);
    state.view = 'active'; state.status = 'any'; state.date = null; render();
  }
  if (button.dataset.status) { state.status = button.dataset.status; render(); }
  if (button.dataset.date) { const date = button.dataset.date; setView('all'); state.date = date; render(); }
  if (button.dataset.edit) openTask(Number(button.dataset.edit));
  if (button.dataset.deleteTask) confirmDelete('task',Number(button.dataset.deleteTask));
  if (button.dataset.deleteSubject) confirmDelete('subject',Number(button.dataset.deleteSubject));
  if (button.dataset.toggle) {
    const task = state.assignments.find(a => a.id === Number(button.dataset.toggle));
    if (!task) return;
    button.disabled = true;
    try {
      const done = task.status !== 'Done';
      await api(`assignments/${task.id}`,'PUT',{...task,status:done ? 'Done' : 'Todo'});
      await refreshAfterChange(done ? 'Готово! Ещё на одно задание меньше' : 'Задание возвращено в работу');
    } catch (error) { notify(error.message || 'Сервер недоступен.',true); button.disabled = false; }
  }
  if (button.dataset.emptyAction === 'reset') resetFilters();
  if (button.dataset.emptyAction === 'task') openTask();
  if (button.dataset.emptyAction === 'subject') { pendingTask = true; openSubject(); }
  if (button.dataset.reload !== undefined) initialize();
});

$('#add-assignment').addEventListener('click',() => openTask());
$('#add-subject').addEventListener('click',() => { if (ready) { pendingTask = false; openSubject(); } });
$('#clear-filters').addEventListener('click',resetFilters);
$('#search').addEventListener('input',event => { state.query = event.target.value; if (ready) renderAssignments(weekDates()); });
$('#sort').addEventListener('change',event => { state.sort = event.target.value; if (ready) renderAssignments(weekDates()); });
$('#subject-dialog').addEventListener('close',() => { if (!$('#task-dialog').open) pendingTask = false; });

async function initialize() {
  $('#auth-retry').hidden = true;
  try {
    csrfToken = (await api('token')).token;
    allowRegistration = (await api('auth/options')).allowRegistration;
    const user = await api('auth/me');
    await enterAccount(user);
  } catch (error) {
    showLogin(error.status === 401 ? '' : 'Не удалось подключиться. Проверьте, запущено ли приложение.');
    $('#auth-retry').hidden = error.status === 401;
    $('#auth-form').hidden = error.status !== 401;
  }
}

function setAuthMode(mode) {
  authMode = mode;
  const register = mode === 'register';
  $('#auth-title').textContent = register ? 'Создать аккаунт' : 'Вход в Зачёт';
  $('#auth-description').textContent = register ? 'Начни с чистого листа. У каждого аккаунта свои задания.' : 'Войди, чтобы продолжить работу со своими заданиями.';
  $('#auth-submit').textContent = register ? 'Зарегистрироваться' : 'Войти';
  $('#auth-switch').textContent = register ? 'Уже есть аккаунт? Войти' : 'Нет аккаунта? Зарегистрироваться';
  $('#auth-switch').hidden = !allowRegistration;
  $('#auth-password').autocomplete = register ? 'new-password' : 'current-password';
  $('#auth-password').minLength = register ? 12 : 1;
  $('#password-hint').hidden = !register;
  $('.form-error',$('#auth-form')).textContent = '';
}

function showLogin(message='', preserveDraft=true) {
  if (preserveDraft && currentUser && $('#task-dialog').open) {
    savedDraft = {owner:currentUser.id,id:editingId,data:Object.fromEntries(new FormData($('#task-form')))};
  }
  const email = currentUser?.email || $('#auth-email').value;
  currentUser = null;
  sessionGeneration++;
  ready = false;
  $$('dialog[open]').forEach(dialog => dialog.close());
  Object.assign(state,{subjects:[],assignments:[],view:'active',subject:null,status:'any',query:'',sort:'due',date:null});
  $('#search').value = ''; $('#sort').value = 'due';
  $('#task-form').reset(); $('#task-form').elements.subjectId.innerHTML = '';
  $('#confirm-message').textContent = '';
  $('#account-email').textContent = '';
  pendingTask = false; deleting = null; editingId = null;
  render();
  $('#app-shell').hidden = true;
  $('.skip-link').hidden = true;
  $('#auth-screen').hidden = false;
  $('#auth-form').hidden = false;
  $('#auth-form').reset(); $('#auth-email').value = email;
  setAuthMode('login');
  if (message) $('.form-error',$('#auth-form')).textContent = message;
}

async function enterAccount(user) {
  sessionGeneration++;
  currentUser = user;
  if (savedDraft && savedDraft.owner !== user.id) savedDraft = null;
  csrfToken = (await api('token')).token;
  await loadData();
  if (!currentUser || currentUser.id !== user.id) return;
  $('#account-email').textContent = user.email;
  $('#auth-form').reset();
  $('#auth-screen').hidden = true;
  $('#app-shell').hidden = false;
  $('.skip-link').hidden = false;
  $('#main').focus();
  if (savedDraft) $('#draft-dialog').showModal();
}

$('#auth-form').addEventListener('submit',event => submitForm(event,async form => {
  csrfToken = (await api('token')).token;
  const values = new FormData(form);
  const submittedMode = authMode;
  const user = await api(`auth/${submittedMode}`,'POST',{email:values.get('email'),password:values.get('password')});
  if (submittedMode === 'register') {
    setAuthMode('login'); $('#auth-password').value = '';
    notify('Аккаунт создан. Теперь войдите с вашим паролем.');
    $('#auth-password').focus();
  } else await enterAccount(user);
}));
$('#auth-switch').addEventListener('click',() => setAuthMode(authMode === 'login' ? 'register' : 'login'));
$('#auth-retry').addEventListener('click',initialize);
$('#logout').addEventListener('click',async event => {
  const button = event.currentTarget; button.disabled = true;
  try {
    await api('auth/logout','POST'); savedDraft = null; showLogin('',false);
    csrfToken = (await api('token')).token;
  } catch (error) {
    if (error.status === 401) { savedDraft = null; showLogin('',false); }
    else notify('Не удалось выйти. Проверьте подключение и повторите.',true);
  } finally { button.disabled = false; }
});
$('#restore-draft').addEventListener('click',() => {
  const draft = savedDraft; savedDraft = null; $('#draft-dialog').close();
  if (!draft || draft.owner !== currentUser?.id) return;
  if (!state.subjects.some(s=>s.id === Number(draft.data.subjectId)) ||
      (draft.id && !state.assignments.some(a=>a.id === draft.id)))
    return notify('Предмет или задание уже удалены. Создайте новое задание.',true);
  openTask(draft.id);
  for (const [key,value] of Object.entries(draft.data)) $('#task-form').elements[key].value = value;
});
$('#discard-draft').addEventListener('click',()=> { savedDraft = null; $('#draft-dialog').close(); });
$('#draft-dialog').addEventListener('cancel',()=> { savedDraft = null; });
initialize();
