(() => {
  const COLORS = ['blue', 'teal', 'purple', 'orange', 'pink', 'slate'];
  const PRIORITIES = { High: 'Высокий приоритет', Low: 'Низкий приоритет' };
  const title = document.getElementById('title');
  const note = document.getElementById('note');
  const list = document.getElementById('list');

  const show = text => { note.textContent = text; note.hidden = false; list.hidden = true; };
  const day = iso => new Date(iso + 'T00:00:00').toLocaleDateString('ru-RU', { weekday: 'short', day: 'numeric', month: 'long' });
  const part = (parent, tag, className, text) => {
    const node = document.createElement(tag);
    if (className) node.className = className;
    node.textContent = text;
    parent.append(node);
    return node;
  };

  const draw = data => {
    title.textContent = data.name;
    document.title = data.name + ' — Зачёт';
    list.replaceChildren();
    const now = new Date();
    const today = [now.getFullYear(), String(now.getMonth() + 1).padStart(2, '0'), String(now.getDate()).padStart(2, '0')].join('-');
    for (const task of data.assignments) {
      const item = document.createElement('li');
      item.className = 'item ' + (COLORS.includes(task.color) ? task.color : 'slate') + (task.dueDate < today ? ' past' : '');
      const meta = part(item, 'div', 'meta', '');
      part(meta, 'span', '', task.subject);
      part(meta, 'span', '', day(task.dueDate));
      if (PRIORITIES[task.priority]) part(meta, 'span', task.priority === 'High' ? 'high' : '', PRIORITIES[task.priority]);
      part(item, 'p', 'item-title', task.title);
      if (task.description) part(item, 'p', 'item-text', task.description);
      list.append(item);
    }
    if (!data.assignments.length) return show('Ближайших заданий нет.');
    note.hidden = true;
    list.hidden = false;
  };

  let key = '';
  try { key = decodeURIComponent(location.hash.slice(1)); } catch { key = ''; }
  if (!key) return show('Ссылка недействительна.');
  fetch('/public/group?key=' + encodeURIComponent(key), { cache: 'no-store' })
    .then(response => {
      if (response.status === 404) return show('Ссылка недействительна.');
      if (response.status === 429) return show('Слишком много обращений. Попробуйте через минуту.');
      if (!response.ok) return show('Не удалось загрузить задания.');
      return response.json().then(draw);
    })
    .catch(() => show('Не удалось загрузить задания.'));
})();
