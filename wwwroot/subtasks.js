'use strict';

// Важность и шаги заданий: разметка карточки и обработчики. Загружается после app.js,
// который вызывает priorityTag, stepsToggle и stepsBlock при отрисовке списка.
const openSteps = new Set();
const priorityTags = {Important:'Важно', Urgent:'Срочно'};
const priorityRank = {Urgent:0, Important:1, Normal:2};
const MAX_STEPS = 30;
let stepBusy = false;

const stepsDone = task => task.subtasks.filter(step => step.done).length;
const findTask = id => state.assignments.find(a => a.id === id);
const findStepTask = stepId => state.assignments.find(a => a.subtasks.some(step => step.id === stepId));

function priorityTag(task) {
  const text = priorityTags[task.priority];
  return text ? `<span class="priority-tag priority-${task.priority.toLowerCase()}">${icon('alert')}${text}</span>` : '';
}

function stepsToggle(task) {
  const count = task.subtasks.length;
  const label = count ? `${stepsDone(task)} из ${count}` : 'Шаги';
  return `<button class="steps-toggle" type="button" data-steps="${task.id}" aria-expanded="${openSteps.has(task.id)}" aria-controls="steps-${task.id}" aria-label="Шаги задания ${escapeHtml(task.title)}: ${count ? label : 'пока нет'}">${icon('chevron')}${label}</button>`;
}

function stepRow(step, index, last) {
  return `<li class="step-row">
    <input type="checkbox" data-step-check="${step.id}" ${step.done ? 'checked' : ''} aria-label="Шаг выполнен: ${escapeHtml(step.title)}">
    <input type="text" class="step-title ${step.done ? 'done' : ''}" data-step-title="${step.id}" value="${escapeHtml(step.title)}" maxlength="140" aria-label="Название шага ${index + 1}">
    <span class="step-buttons">
      <button class="icon-button" type="button" data-step-up="${step.id}" aria-label="Поднять шаг «${escapeHtml(step.title)}» выше" ${index === 0 ? 'disabled' : ''}>↑</button>
      <button class="icon-button" type="button" data-step-down="${step.id}" aria-label="Опустить шаг «${escapeHtml(step.title)}» ниже" ${last ? 'disabled' : ''}>↓</button>
      <button class="icon-button" type="button" data-step-delete="${step.id}" aria-label="Удалить шаг «${escapeHtml(step.title)}»">${icon('trash')}</button>
    </span>
  </li>`;
}

function stepsBlock(task) {
  const count = task.subtasks.length;
  const full = count >= MAX_STEPS;
  const everyDone = count > 0 && stepsDone(task) === count && task.status !== 'Done';
  return `${everyDone ? `<div class="steps-ready"><span>Все шаги готовы. Отметить задание выполненным?</span><button class="secondary-button" type="button" data-toggle="${task.id}">Отметить выполненным</button></div>` : ''}
    <div class="steps-panel" id="steps-${task.id}" ${openSteps.has(task.id) ? '' : 'hidden'}>
      ${task.groupId != null ? '<p class="steps-note">Шаги видишь только ты: другие студенты и староста их не увидят.</p>' : ''}
      ${count ? `<ul class="step-list">${task.subtasks.map((step, i) => stepRow(step, i, i === count - 1)).join('')}</ul>` : ''}
      <input type="text" class="step-new" data-step-new="${task.id}" maxlength="140" ${full ? 'disabled' : ''} placeholder="${full ? `Больше ${MAX_STEPS} шагов не добавить` : 'Добавить шаг и нажать Enter'}" aria-label="Добавить шаг к заданию ${escapeHtml(task.title)}">
    </div>`;
}

function setStepsDisabled(taskId, disabled) {
  for (const control of document.querySelectorAll(`#steps-${taskId} button, #steps-${taskId} input`)) control.disabled = disabled;
}

// Одно изменение шагов — один запрос; пока он идёт, элементы панели заблокированы.
// request() возвращает ответ сервера, apply(task, ответ) правит локальные данные, finish(успех) возвращает фокус.
async function changeSteps(taskId, request, apply, finish) {
  if (stepBusy) return;
  const generation = sessionGeneration;
  let ok = false;
  stepBusy = true;
  setStepsDisabled(taskId, true);
  try {
    const result = await request();
    if (generation !== sessionGeneration || !currentUser) return;
    const task = findTask(taskId);
    if (task) apply(task, result);
    ok = true;
  } catch (error) {
    if (generation !== sessionGeneration || !currentUser || error.status === 401) return;
    if (error.status === 404) {
      notify('Задание изменилось. Список обновлён.', true);
      await loadData().catch(() => {});
    } else notify(error.message || 'Сервер недоступен.', true);
  } finally {
    stepBusy = false;
    if (generation === sessionGeneration && currentUser) {
      renderAssignments(weekDates());
      if (finish) finish(ok);
    }
  }
}

const focusSelector = selector => document.querySelector(selector)?.focus();

function addStep(field) {
  const taskId = Number(field.dataset.stepNew);
  const title = field.value.trim();
  if (!title) return;
  changeSteps(taskId,
    () => api(`assignments/${taskId}/subtasks`, 'POST', {title}),
    (task, step) => { task.subtasks = [...task.subtasks, step]; },
    ok => {
      const next = document.querySelector(`[data-step-new="${taskId}"]`);
      if (!next) return;
      if (!ok) next.value = title;
      next.focus();
    });
}

function renameStep(field) {
  const stepId = Number(field.dataset.stepTitle);
  const task = findStepTask(stepId);
  if (!task) return;
  const title = field.value.trim();
  const step = task.subtasks.find(s => s.id === stepId);
  if (title === step.title) { field.value = step.title; return; }
  changeSteps(task.id,
    () => api(`assignments/${task.id}/subtasks/${stepId}`, 'PUT', {title}),
    (t, saved) => { t.subtasks = t.subtasks.map(s => s.id === saved.id ? saved : s); });
}

function checkStep(box) {
  const stepId = Number(box.dataset.stepCheck);
  const task = findStepTask(stepId);
  if (!task) return;
  changeSteps(task.id,
    () => api(`assignments/${task.id}/subtasks/${stepId}`, 'PUT', {done: box.checked}),
    (t, saved) => { t.subtasks = t.subtasks.map(s => s.id === saved.id ? saved : s); },
    () => focusSelector(`[data-step-check="${stepId}"]`));
}

function deleteStep(stepId) {
  const task = findStepTask(stepId);
  if (!task) return;
  changeSteps(task.id,
    () => api(`assignments/${task.id}/subtasks/${stepId}`, 'DELETE'),
    t => { t.subtasks = t.subtasks.filter(s => s.id !== stepId).map((s, i) => ({...s, position: i})); },
    () => focusSelector(`[data-step-new="${task.id}"]`));
}

function moveStep(stepId, shift) {
  const task = findStepTask(stepId);
  if (!task) return;
  const ids = task.subtasks.map(s => s.id);
  const from = ids.indexOf(stepId);
  const to = from + shift;
  if (to < 0 || to >= ids.length) return;
  [ids[from], ids[to]] = [ids[to], ids[from]];
  changeSteps(task.id,
    () => api(`assignments/${task.id}/subtasks/order`, 'PUT', {ids}),
    (t, steps) => { t.subtasks = steps; },
    () => {
      const same = document.querySelector(`[data-step-${shift < 0 ? 'up' : 'down'}="${stepId}"]`);
      if (same && !same.disabled) same.focus(); else focusSelector(`[data-step-title="${stepId}"]`);
    });
}

const taskList = document.getElementById('assignment-list');

taskList.addEventListener('click', event => {
  const toggle = event.target.closest('[data-steps]');
  if (toggle) {
    const id = Number(toggle.dataset.steps);
    if (openSteps.has(id)) openSteps.delete(id); else openSteps.add(id);
    renderAssignments(weekDates());
    focusSelector(`[data-steps="${id}"]`);
    return;
  }
  const button = event.target.closest('button');
  if (!button || button.disabled) return;
  if (button.dataset.stepDelete) deleteStep(Number(button.dataset.stepDelete));
  if (button.dataset.stepUp) moveStep(Number(button.dataset.stepUp), -1);
  if (button.dataset.stepDown) moveStep(Number(button.dataset.stepDown), 1);
});

taskList.addEventListener('change', event => {
  const field = event.target;
  if (field.dataset.stepCheck) checkStep(field);
  if (field.dataset.stepTitle) renameStep(field);
});

taskList.addEventListener('keydown', event => {
  const field = event.target;
  if (event.key === 'Enter' && field.dataset.stepNew) { event.preventDefault(); addStep(field); }
  if (field.dataset.stepTitle && (event.key === 'Enter' || event.key === 'Escape')) {
    event.preventDefault();
    if (event.key === 'Escape') field.value = findStepTask(Number(field.dataset.stepTitle))?.subtasks.find(s => s.id === Number(field.dataset.stepTitle))?.title ?? field.value;
    field.blur();
  }
});

document.addEventListener('account-cleared', () => { openSteps.clear(); stepBusy = false; });
