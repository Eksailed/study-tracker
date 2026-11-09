'use strict';

/* Диалоги «Группа» и «Владелец». Тексты только через textContent, адреса гостя живут в памяти до закрытия диалога. */

function gel(tag, className, text, attrs) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined && text !== null) node.textContent = text;
  if (attrs) for (const [name, value] of Object.entries(attrs)) node.setAttribute(name, value);
  return node;
}

function gbtn(label, className, onclick, attrs) {
  const node = gel('button', className, label, {type: 'button', ...attrs});
  node.addEventListener('click', onclick);
  return node;
}

const groupTime = value => new Date(value).toLocaleString('ru-RU', {day: '2-digit', month: '2-digit', year: 'numeric', hour: '2-digit', minute: '2-digit'});

function syncRoleButtons() {
  $('#open-owner').hidden = !currentUser?.isOwner;
}

/** Состояние группы и роль владельца в памяти страницы обновляем из /api/auth/me. */
function adoptAccount(me) {
  if (!currentUser || me.id !== currentUser.id) return;
  currentUser.isOwner = me.isOwner;
  currentUser.group = me.group;
  syncRoleButtons();
}

async function refreshAccount() {
  adoptAccount(await api('auth/me'));
  try { await loadData(); } catch { /* список обновится при следующем действии */ }
}

/** Общая оболочка диалога: поколение сеанса и версия закрывают гонки, на время запроса кнопки внутри блокируются. */
function makePanel({dialog, problem, body, load, draw, clear, show}) {
  const panel = {
    dialog: $(dialog), problem: $(problem), body: $(body), data: null, ui: {}, version: 0, busy: false,
    show(node) { if (show) show(node); else panel.body.replaceChildren(node); },
    current(generation, version) {
      return currentUser && generation === sessionGeneration && version === panel.version && panel.dialog.open;
    },
    reset() {
      panel.version++;
      panel.busy = false;
      panel.data = null;
      panel.ui = {};
      panel.problem.textContent = '';
      clear(panel);
    },
    fail(error, generation, version) {
      if (error.status === 401) { if (currentUser) showLogin('Сеанс завершился. Войдите снова.'); return; }
      if (!panel.current(generation, version)) return;
      panel.problem.textContent = error.message || 'Сервер недоступен.';
    },
    async reload() {
      const generation = sessionGeneration, version = panel.version;
      const data = await load(panel);
      if (panel.current(generation, version)) panel.data = data;
    },
    async run(action) {
      if (panel.busy) return;
      const generation = sessionGeneration, version = panel.version;
      panel.problem.textContent = '';
      panel.busy = true;
      $$('button', panel.body).forEach(item => { item.disabled = true; });
      try { await action(); }
      catch (error) { panel.fail(error, generation, version); }
      finally {
        if (version === panel.version) panel.busy = false;
        if (panel.current(generation, version)) draw(panel);
      }
    },
    async open() {
      if (!ready) return;
      panel.reset();
      panel.show(gel('p', 'calendar-state', 'Загружаем…'));
      panel.dialog.showModal();
      const generation = sessionGeneration, version = panel.version;
      try {
        const data = await load(panel);
        if (!panel.current(generation, version)) return;
        panel.data = data;
        draw(panel);
      } catch (error) {
        panel.fail(error, generation, version);
        if (panel.current(generation, version)) panel.show(gel('p', 'calendar-state', 'Не удалось загрузить данные.'));
      }
    }
  };
  panel.dialog.addEventListener('close', () => panel.reset());
  document.addEventListener('account-cleared', () => panel.reset());
  return panel;
}

/* ---------- Группа ---------- */

async function loadGroup() {
  const me = await api('auth/me');
  adoptAccount(me);
  const data = {group: me.group, groups: [], members: [], link: null};
  if (!me.group) data.groups = await api('groups');
  else if (me.group.role === 'Head' && me.group.status === 'Active') {
    [data.members, data.link] = await Promise.all([api(`groups/${me.group.id}/members`), api(`groups/${me.group.id}/guest-link`)]);
  }
  return data;
}

/** Состав группы или список групп меняется — перечитываем и аккаунт, и задания: у студента они зависят от членства. */
async function groupChanged(panel, message) {
  await panel.reload();
  try { await loadData(); } catch { /* список обновится при следующем действии */ }
  if (message) notify(message);
}

const groupPanel = makePanel({
  dialog: '#group-dialog', problem: '#group-problem', body: '#group-body', load: loadGroup,
  draw: drawGroup, clear: panel => panel.body.replaceChildren()
});

function rowOf(main, sub, actions) {
  const row = gel('li', 'group-row');
  const text = gel('div', 'group-row-main', main);
  if (sub) text.append(gel('span', '', sub));
  row.append(text);
  if (actions.length) {
    const box = gel('div', 'group-row-actions');
    box.append(...actions);
    row.append(box);
  }
  return row;
}

function confirmBox(text, yesLabel, onYes, onNo) {
  const box = gel('section', 'calendar-confirm', null, {'aria-label': 'Подтверждение'});
  box.append(gel('p', '', text));
  const actions = gel('div', 'dialog-actions');
  actions.append(gbtn('Отмена', 'secondary-button', onNo), gbtn(yesLabel, 'danger-button', onYes));
  box.append(actions);
  return box;
}

function drawGroup(panel) {
  const {data, ui, body} = panel;
  body.replaceChildren();
  if (!data) return;
  const group = data.group;
  const redraw = () => drawGroup(panel);
  if (!group) return drawGroupChoice(panel);
  body.append(gel('h3', 'group-name', group.name));
  if (group.status === 'Pending') {
    body.append(gel('p', 'calendar-state', `Заявка в группу «${group.name}» ждёт подтверждения старосты. Задания группы появятся, когда её примут.`));
    body.append(gbtn('Отменить заявку', 'secondary-button', () => panel.run(async () => {
      await api('group/membership', 'DELETE');
      await groupChanged(panel, 'Заявка отменена');
    })));
    return;
  }
  const head = group.role === 'Head';
  body.append(gel('p', 'calendar-state', head
    ? 'Вы староста этой группы: ведёте её предметы, задания и сроки, принимаете заявки и выдаёте ссылку для гостей.'
    : 'Вы студент этой группы. Задания группы показаны вместе с вашими, отметки и шаги видны только вам.'));
  if (head) return drawHead(panel);
  if (ui.confirm === 'leave') {
    body.append(confirmBox('Ваши отметки и шаги по заданиям группы будут удалены. Выйти из группы?', 'Выйти', () => panel.run(async () => {
      await api('group/membership', 'DELETE');
      await groupChanged(panel, 'Вы вышли из группы');
    }), () => { ui.confirm = null; redraw(); }));
    return;
  }
  body.append(gbtn('Выйти из группы', 'secondary-button', () => { ui.confirm = 'leave'; redraw(); }));
}

function drawGroupChoice(panel) {
  const {data, body} = panel;
  body.append(gel('p', 'calendar-state', 'Вы пока не состоите в группе. Выберите свою и подайте заявку: староста подтвердит её, и задания группы появятся в вашем списке.'));
  if (!data.groups.length) {
    body.append(gel('p', 'notification-help', 'Групп пока нет. Их создаёт владелец.'));
    return;
  }
  const list = gel('ul', 'group-list');
  for (const item of data.groups) {
    list.append(rowOf(item.name, '', [gbtn('Подать заявку', 'primary-button', () => panel.run(async () => {
      await api(`groups/${item.id}/join`, 'POST', {});
      await groupChanged(panel, 'Заявка отправлена');
    }), {'aria-label': `Подать заявку в группу ${item.name}`})]));
  }
  body.append(list);
}

function drawHead(panel) {
  const {data, ui, body} = panel;
  const tab = ui.tab || 'members';
  const tabs = gel('div', 'group-tabs', null, {role: 'tablist', 'aria-label': 'Разделы группы'});
  for (const [id, label] of [['members', 'Участники'], ['guests', 'Для гостей']]) {
    tabs.append(gbtn(label, 'group-tab', () => { ui.tab = id; ui.confirm = null; drawGroup(panel); },
      {role: 'tab', 'aria-selected': String(id === tab)}));
  }
  body.append(tabs);
  if (tab === 'guests') return drawGuests(panel);
  const pending = data.members.filter(m => m.status === 'Pending');
  const active = data.members.filter(m => m.status === 'Active');
  const gid = data.group.id;
  const remove = (member, message) => panel.run(async () => {
    await api(`groups/${gid}/members/${encodeURIComponent(member.userId)}`, 'DELETE');
    await groupChanged(panel, message);
  });
  body.append(gel('h3', 'group-subhead', `Заявки: ${pending.length}`));
  if (pending.length) {
    const list = gel('ul', 'group-list');
    for (const member of pending) {
      list.append(rowOf(member.email, '', [
        gbtn('Принять', 'primary-button', () => panel.run(async () => {
          await api(`groups/${gid}/members/${encodeURIComponent(member.userId)}/approve`, 'POST', {});
          await groupChanged(panel, 'Студент принят');
        }), {'aria-label': `Принять ${member.email}`}),
        gbtn('Отклонить', 'secondary-button', () => remove(member, 'Заявка отклонена'), {'aria-label': `Отклонить ${member.email}`})
      ]));
    }
    body.append(list);
  } else body.append(gel('p', 'notification-help', 'Новых заявок нет.'));
  body.append(gel('h3', 'group-subhead', `Участники: ${active.length}`));
  const list = gel('ul', 'group-list');
  for (const member of active) {
    const mine = member.userId === currentUser.id;
    const role = member.role === 'Head' ? 'староста' : 'студент';
    list.append(rowOf(member.email, mine ? `${role} · это вы` : role, member.role === 'Head' ? [] : [
      gbtn('Убрать', 'secondary-button', () => { ui.removing = member.userId; drawGroup(panel); }, {'aria-label': `Убрать ${member.email}`})
    ]));
    if (ui.removing === member.userId) {
      const item = gel('li', 'group-row');
      item.append(confirmBox(`Убрать ${member.email} из группы? Его отметки и шаги по заданиям группы будут удалены.`, 'Убрать',
        () => remove(member, 'Студент убран из группы'), () => { ui.removing = null; drawGroup(panel); }));
      list.append(item);
    }
  }
  body.append(list);
}

async function copyField(input, copyButton) {
  input.select();
  try {
    await navigator.clipboard.writeText(input.value);
    copyButton.textContent = 'Скопировано';
    notify('Ссылка скопирована');
  } catch {
    notify('Не удалось скопировать автоматически. Выдели ссылку и скопируй вручную.', true);
  }
}

function addressField(label, value) {
  const wrap = gel('div', 'guest-address');
  wrap.append(gel('label', '', label));
  const row = gel('div', 'token-copy');
  const input = gel('input', '', null, {type: 'text', readonly: '', autocomplete: 'off', spellcheck: 'false', 'aria-label': label});
  input.value = value;
  const copy = gbtn('Скопировать', 'secondary-button', () => copyField(input, copy));
  row.append(input, copy);
  wrap.append(row);
  return wrap;
}

function drawGuests(panel) {
  const {data, ui, body} = panel;
  const gid = data.group.id, link = data.link;
  body.append(gel('p', 'calendar-state', link.exists
    ? `Ссылка для гостей включена. Выдана ${groupTime(link.createdAt)}.` : 'Ссылки для гостей пока нет.'));
  body.append(gel('p', 'notification-help', 'Гость открывает страницу с заданиями группы без входа и может подписаться на ленту календаря. Он видит предмет, название, описание, срок и важность, но не видит участников и ничьих отметок.'));
  if (ui.issued) {
    const issued = gel('section', 'token-issued', null, {'aria-label': 'Новые ссылки'});
    issued.append(gel('h3', '', 'Ссылки готовы'));
    const warning = gel('p', 'notification-help');
    warning.append(gel('strong', '', 'Ссылки показываются один раз.'),
      ' Скопируй их сейчас. Любой, кому ты их перешлёшь, увидит задания группы; потеряешь или передашь не тем — перевыпусти: старые перестанут работать.');
    issued.append(warning, addressField('Страница группы', ui.issued.page), addressField('Лента календаря (.ics)', ui.issued.feed));
    body.append(issued);
  }
  if (ui.confirm === 'reissue') {
    body.append(confirmBox('Старые ссылки перестанут работать: открытые страницы покажут «Ссылка недействительна», календари перестанут обновляться. Перевыпустить?',
      'Перевыпустить', () => issueGuest(panel), () => { ui.confirm = null; drawGroup(panel); }));
  }
  if (ui.confirm === 'revoke') {
    body.append(confirmBox('Страница и лента перестанут открываться у всех, у кого есть ссылка. Отозвать?', 'Отозвать',
      () => panel.run(async () => {
        await api(`groups/${gid}/guest-link`, 'DELETE');
        ui.issued = null;
        ui.confirm = null;
        await panel.reload();
        notify('Ссылка отозвана');
      }), () => { ui.confirm = null; drawGroup(panel); }));
  }
  const actions = gel('div', 'dialog-actions');
  if (link.exists) actions.append(gbtn('Отозвать', 'secondary-button', () => { ui.confirm = 'revoke'; drawGroup(panel); }));
  actions.append(gbtn(link.exists ? 'Перевыпустить' : 'Создать ссылку', 'primary-button',
    () => { if (link.exists) { ui.confirm = 'reissue'; drawGroup(panel); } else issueGuest(panel); }));
  body.append(actions);
}

function issueGuest(panel) {
  return panel.run(async () => {
    const gid = panel.data.group.id;
    const created = await api(`groups/${gid}/guest-link`, 'POST', {});
    if (!panel.current(sessionGeneration, panel.version)) return;
    panel.ui.issued = {
      page: `${location.origin}/g.html#${encodeURIComponent(created.key)}`,
      feed: `${location.origin}/group.ics?key=${encodeURIComponent(created.key)}`
    };
    panel.ui.confirm = null;
    await panel.reload();
    notify('Ссылки для гостей готовы');
  });
}

$('#open-group').addEventListener('click', () => groupPanel.open());

/* ---------- Владелец ---------- */

async function loadOwner(panel) {
  const [groups, users] = await Promise.all([api('owner/groups'), api(`owner/users?q=${encodeURIComponent(panel.ui.query || '')}`)]);
  return {groups, users};
}

const ownerPanel = makePanel({
  dialog: '#owner-dialog', problem: '#owner-problem', body: '#owner-body', load: loadOwner,
  draw: drawOwner,
  show: node => {
    $('#owner-groups').replaceChildren(node);
    $('#owner-users').replaceChildren();
  },
  clear: () => {
    $('#owner-groups').replaceChildren();
    $('#owner-users').replaceChildren();
    $('#owner-search').value = '';
    $('#owner-new-name').value = '';
  }
});

async function ownerChanged(message) {
  await ownerPanel.reload();
  await refreshAccount();
  if (message) notify(message);
}

function drawOwner(panel) {
  const {data, ui} = panel;
  const groupsBox = $('#owner-groups'), usersBox = $('#owner-users');
  groupsBox.replaceChildren();
  usersBox.replaceChildren();
  if (!data) return;
  const redraw = () => drawOwner(panel);
  if (!data.groups.length) groupsBox.append(gel('p', 'notification-help', 'Групп пока нет. Создай первую ниже.'));
  const list = gel('ul', 'group-list');
  for (const item of data.groups) {
    const heads = item.heads.length ? `староста: ${item.heads.map(h => h.email).join(', ')}` : 'старосты нет';
    const info = `участников: ${item.members} · заявок: ${item.pending} · ${heads}`;
    if (ui.rename === item.id) {
      const row = gel('li', 'group-row');
      const input = gel('input', '', null, {type: 'text', maxlength: '60', 'aria-label': 'Новое название группы'});
      input.value = item.name;
      const save = () => panel.run(async () => {
        await api(`owner/groups/${item.id}`, 'PUT', {name: input.value});
        ui.rename = null;
        await ownerChanged('Группа переименована');
      });
      input.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); save(); } });
      const box = gel('div', 'group-row-actions');
      box.append(gbtn('Сохранить', 'primary-button', save), gbtn('Отмена', 'secondary-button', () => { ui.rename = null; redraw(); }));
      row.append(input, box);
      list.append(row);
      continue;
    }
    list.append(rowOf(item.name, info, [
      gbtn('Переименовать', 'secondary-button', () => { ui.rename = item.id; ui.confirm = null; redraw(); }, {'aria-label': `Переименовать группу ${item.name}`}),
      gbtn('Удалить', 'secondary-button', () => { ui.confirm = item.id; ui.rename = null; redraw(); }, {'aria-label': `Удалить группу ${item.name}`})
    ]));
    if (ui.confirm === item.id) {
      const row = gel('li', 'group-row');
      row.append(confirmBox(`Удалить группу «${item.name}»? Вместе с ней удалятся её предметы, задания, отметки и шаги студентов. Отменить это нельзя.`, 'Удалить',
        () => panel.run(async () => {
          await api(`owner/groups/${item.id}`, 'DELETE');
          ui.confirm = null;
          await ownerChanged('Группа удалена');
        }), () => { ui.confirm = null; redraw(); }));
      list.append(row);
    }
  }
  groupsBox.append(list);
  if (!data.users.length) usersBox.append(gel('p', 'notification-help', 'Никого не нашлось.'));
  const people = gel('ul', 'group-list');
  for (const person of data.users) people.append(ownerUserRow(panel, person));
  usersBox.append(people);
}

function ownerUserRow(panel, person) {
  const membership = person.group;
  const where = !membership ? 'без группы'
    : `${membership.name} · ${membership.status === 'Pending' ? 'ждёт подтверждения' : membership.role === 'Head' ? 'староста' : 'студент'}`;
  const name = person.isOwner ? `${person.email} (владелец)` : person.email;
  if (membership?.role === 'Head' && membership.status === 'Active') {
    return rowOf(name, where, [gbtn('Снять', 'secondary-button', () => panel.run(async () => {
      await api(`owner/groups/${membership.id}/heads/${encodeURIComponent(person.id)}`, 'DELETE');
      await ownerChanged('Староста снят');
    }), {'aria-label': `Снять старосту ${person.email}`})]);
  }
  const choice = gel('select', 'owner-group-select', null, {'aria-label': `Группа для ${person.email}`});
  for (const item of panel.data.groups) {
    if (membership && item.id !== membership.id) continue;
    const option = gel('option', '', item.name);
    option.value = String(item.id);
    choice.append(option);
  }
  if (!choice.options.length) return rowOf(name, where, []);
  const assign = gbtn('Назначить старостой', 'primary-button', () => panel.run(async () => {
    await api(`owner/groups/${choice.value}/heads/${encodeURIComponent(person.id)}`, 'PUT', {});
    await ownerChanged('Староста назначен');
  }), {'aria-label': `Назначить старостой ${person.email}`});
  const row = rowOf(name, where, [choice, assign]);
  return row;
}

$('#open-owner').addEventListener('click', () => ownerPanel.open());

$('#owner-create').addEventListener('submit', event => {
  event.preventDefault();
  const input = $('#owner-new-name');
  ownerPanel.run(async () => {
    await api('owner/groups', 'POST', {name: input.value});
    input.value = '';
    await ownerChanged('Группа создана');
  });
});

let ownerSearchTimer;
let ownerSearchSeq = 0;
$('#owner-search').addEventListener('input', event => {
  clearTimeout(ownerSearchTimer);
  const query = event.target.value;
  ownerSearchTimer = setTimeout(async () => {
    if (!ownerPanel.dialog.open || !ownerPanel.data) return;
    const generation = sessionGeneration, version = ownerPanel.version, seq = ++ownerSearchSeq;
    ownerPanel.ui.query = query;
    try {
      const users = await api(`owner/users?q=${encodeURIComponent(query)}`);
      if (!ownerPanel.current(generation, version) || seq !== ownerSearchSeq || ownerPanel.busy) return;
      ownerPanel.data.users = users;
      ownerPanel.problem.textContent = '';
      drawOwner(ownerPanel);
    } catch (error) { ownerPanel.fail(error, generation, version); }
  }, 250);
});

document.addEventListener('account-entered', syncRoleButtons);
document.addEventListener('account-cleared', () => { clearTimeout(ownerSearchTimer); $('#open-owner').hidden = true; $('#group-card').hidden = true; });
$('#group-card-open').addEventListener('click', () => $('#open-group').click());
