'use strict';

let apiTokens = [];
let apiTokenVersion = 0;

function clearApiTokenState() {
  apiTokenVersion++;
  apiTokens = [];
  $('#token-form').reset();
  $('#token-value').value = '';
  $('#token-issued').hidden = true;
  $('#token-list').replaceChildren();
  $('#token-problem').textContent = '';
  $('.form-error',$('#token-form')).textContent = '';
  $('#token-copy').textContent = 'Скопировать';
}

function tokenDate(ms, withTime=false) {
  const date = new Date(ms);
  return withTime
    ? date.toLocaleString('ru-RU',{day:'2-digit',month:'2-digit',year:'numeric',hour:'2-digit',minute:'2-digit'})
    : date.toLocaleDateString('ru-RU');
}

function tokenScopes(scopes) {
  const names = {'tasks:read':'чтение','tasks:write':'запись'};
  return scopes.map(scope => names[scope] || scope).join(' и ');
}

function renderApiTokens() {
  const list = $('#token-list');
  if (!apiTokens.length) {
    const empty = document.createElement('p');
    empty.className = 'notification-help';
    empty.textContent = 'Токенов пока нет.';
    list.replaceChildren(empty);
    return;
  }
  list.replaceChildren(...apiTokens.map(token => {
    const inactive = token.revoked || token.expired;
    const item = document.createElement('div');
    item.className = 'token-item' + (inactive ? ' inactive' : '');
    const info = document.createElement('div');
    const title = document.createElement('strong');
    title.textContent = token.name;
    const state = document.createElement('span');
    state.className = 'token-state' + (inactive ? ' off' : '');
    state.textContent = token.revoked ? 'отозван' : token.expired ? 'просрочен' : 'действует';
    title.append(state);
    const code = document.createElement('code');
    code.textContent = `${token.prefix}…`;
    const meta = document.createElement('p');
    meta.append(code,` · доступ: ${tokenScopes(token.scopes)}`);
    const dates = document.createElement('p');
    dates.textContent = `Создан ${tokenDate(token.createdAt)} · действует до ${tokenDate(token.expiresAt)} · `
      + (token.lastUsedAt ? `использовался ${tokenDate(token.lastUsedAt,true)}` : 'ещё не использовался');
    info.append(title,meta,dates);
    item.append(info);
    if (!token.revoked) {
      const revoke = document.createElement('button');
      revoke.type = 'button';
      revoke.className = 'secondary-button';
      revoke.textContent = 'Отозвать';
      revoke.setAttribute('aria-label',`Отозвать токен «${token.name}»`);
      revoke.addEventListener('click',()=>confirmDelete('token',token.id));
      item.append(revoke);
    }
    return item;
  }));
}

async function loadApiTokens() {
  const generation = sessionGeneration, version = apiTokenVersion;
  const data = await api('tokens');
  if (!currentUser || generation !== sessionGeneration || version !== apiTokenVersion || !$('#tokens-dialog').open) return;
  apiTokens = data;
  renderApiTokens();
}

async function afterTokenRevoked() {
  try { await loadApiTokens(); }
  catch(error) { if (error.status !== 401) $('#token-problem').textContent = error.message || 'Сервер недоступен.'; }
  notify('Токен отозван');
}

$('#open-tokens').addEventListener('click',async()=> {
  if (!ready) return;
  clearApiTokenState();
  $('#tokens-dialog').showModal();
  try { await loadApiTokens(); }
  catch(error) {
    if (error.status === 401) return;
    $('#token-list').replaceChildren();
    $('#token-problem').textContent = error.message || 'Сервер недоступен.';
  }
});
$('#tokens-dialog').addEventListener('close',clearApiTokenState);
document.addEventListener('account-cleared',clearApiTokenState);

$('#token-form').addEventListener('submit',event => submitForm(event,async form=> {
  const version = apiTokenVersion;
  const scopes = [];
  if (form.elements.read.checked) scopes.push('tasks:read');
  if (form.elements.write.checked) scopes.push('tasks:write');
  const created = await api('tokens','POST',{name:form.elements.name.value,scopes,days:Number(form.elements.days.value)});
  if (version !== apiTokenVersion || !$('#tokens-dialog').open) return;
  $('#token-value').value = created.token;
  $('#token-issued').hidden = false;
  $('#token-copy').textContent = 'Скопировать';
  form.reset();
  $('#token-value').focus();
  $('#token-value').select();
  await loadApiTokens();
}));

$('#token-copy').addEventListener('click',async()=> {
  const field = $('#token-value');
  field.select();
  try {
    await navigator.clipboard.writeText(field.value);
    $('#token-copy').textContent = 'Скопировано';
    notify('Токен скопирован');
  } catch {
    notify('Не удалось скопировать автоматически. Выдели токен и скопируй вручную.',true);
  }
});
