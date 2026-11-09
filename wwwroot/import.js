'use strict';

const IMPORT_MAX_FILES = 200, IMPORT_MAX_BYTES = 100 * 1024, IMPORT_LIST_LIMIT = 50;
let importVersion = 0;
let importBusy = false;
let importFiles = [];
let importSkipped = [];
let importReport = null;

function setImportBusy(busy) {
  importBusy = busy;
  for (const id of ['#import-pick-folder','#import-pick-files','#import-due']) $(id).disabled = busy;
  updateImportApply();
}

function updateImportApply() {
  $('#import-apply').disabled = importBusy || !importReport || importReport.newCount === 0;
}

function clearImportState() {
  importVersion++;
  importFiles = [];
  importSkipped = [];
  importReport = null;
  $('#import-folder').value = '';
  $('#import-files').value = '';
  $('#import-due').value = '';
  $('#import-summary').textContent = 'Выбери папку или файлы заметок.';
  $('#import-new').replaceChildren();
  $('#import-skipped').replaceChildren();
  $('#import-new-section').hidden = true;
  $('#import-skipped-section').hidden = true;
  $('#import-problem').textContent = '';
  setImportBusy(false);
}

/** Оставляет только .md вне скрытых папок (.obsidian, .trash), читает тексты; большие файлы пропускает. */
async function pickNotes(picked) {
  const notes = picked
    .map(file => ({file, path:file.webkitRelativePath || file.name}))
    .filter(({file,path}) => /\.md$/i.test(file.name) && !path.split('/').some(part => part.startsWith('.')));
  if (notes.length > IMPORT_MAX_FILES) return {tooMany:true, files:[], skippedLocal:[]};
  const files = [], skippedLocal = [];
  for (const {file,path} of notes) {
    if (file.size > IMPORT_MAX_BYTES) skippedLocal.push({path, reason:'файл больше 100 КБ'});
    else files.push({path, text:await file.text()});
  }
  return {tooMany:false, files, skippedLocal};
}

function importLine(main, note) {
  const item = document.createElement('li');
  item.append(main);
  if (note) {
    const extra = document.createElement('span');
    extra.textContent = note;
    item.append(extra);
  }
  return item;
}

function fillImportList(list, section, entries, make) {
  const lines = entries.slice(0, IMPORT_LIST_LIMIT).map(make);
  if (entries.length > IMPORT_LIST_LIMIT) lines.push(importLine(`…и ещё ${entries.length - IMPORT_LIST_LIMIT}`));
  list.replaceChildren(...lines);
  section.hidden = entries.length === 0;
}

function renderImport() {
  const report = importReport;
  const skipped = [...importSkipped, ...(report ? report.skipped : [])];
  const parts = [];
  if (report) {
    parts.push(`Новых: ${report.newCount}`, `уже есть: ${report.existingCount}`);
  }
  parts.push(`пропущено: ${skipped.length}`);
  if (report) {
    parts.push(`не задания: ${report.notHomework}`);
    if (report.truncated) parts.push(`обрезано: ${report.truncated}`);
    if (report.newSubjects.length) parts.push(`новых предметов: ${report.newSubjects.join(', ')}`);
  }
  $('#import-summary').textContent = parts.join(' · ');
  fillImportList($('#import-new'), $('#import-new-section'), report ? report.items : [],
    item => importLine(item.title, `${item.subject} · ${formatDate(item.due)}`));
  fillImportList($('#import-skipped'), $('#import-skipped-section'), skipped,
    item => importLine(item.path, item.reason));
  updateImportApply();
}

function importBody() {
  return {files:importFiles, defaultDue:$('#import-due').value || null};
}

function importIsCurrent(generation, version) {
  return currentUser && generation === sessionGeneration && version === importVersion && $('#import-dialog').open;
}

async function runPreview() {
  if (!importFiles.length) { importReport = null; renderImport(); return; }
  const generation = sessionGeneration, version = ++importVersion;
  importReport = null;
  $('#import-problem').textContent = '';
  $('#import-summary').textContent = 'Проверяем заметки…';
  setImportBusy(true);
  try {
    const report = await api('import/obsidian/preview','POST',importBody());
    if (!importIsCurrent(generation,version)) return;
    importReport = report;
    renderImport();
  } catch (error) {
    if (error.status === 401 || !importIsCurrent(generation,version)) return;
    $('#import-summary').textContent = 'Не удалось проверить заметки.';
    $('#import-problem').textContent = error.message || 'Сервер недоступен.';
  } finally {
    if (version === importVersion) setImportBusy(false);
  }
}

async function handlePick(input) {
  const picked = Array.from(input.files);
  input.value = '';
  if (!picked.length) return;
  const generation = sessionGeneration, version = ++importVersion;
  importFiles = [];
  importSkipped = [];
  importReport = null;
  $('#import-problem').textContent = '';
  $('#import-summary').textContent = 'Читаем файлы…';
  $('#import-new-section').hidden = true;
  $('#import-skipped-section').hidden = true;
  setImportBusy(true);
  let result;
  try { result = await pickNotes(picked); }
  catch {
    if (importIsCurrent(generation,version)) {
      setImportBusy(false);
      $('#import-summary').textContent = 'Не удалось прочитать файлы.';
      $('#import-problem').textContent = 'Файлы не читаются. Выбери их ещё раз.';
    }
    return;
  }
  if (!importIsCurrent(generation,version)) return;
  setImportBusy(false);
  if (result.tooMany) {
    $('#import-summary').textContent = 'Файлы не выбраны.';
    $('#import-problem').textContent = 'Слишком много файлов. Выберите папку с заданиями.';
    return;
  }
  importFiles = result.files;
  importSkipped = result.skippedLocal;
  if (!importFiles.length) {
    renderImport();
    if (!importSkipped.length) $('#import-summary').textContent = 'В выбранном нет заметок .md.';
    return;
  }
  await runPreview();
}

async function runApply() {
  if (importBusy || !importReport || importReport.newCount === 0) return;
  const generation = sessionGeneration, version = importVersion;
  $('#import-problem').textContent = '';
  setImportBusy(true);
  try {
    const result = await api('import/obsidian','POST',importBody());
    if (!currentUser || generation !== sessionGeneration) return;
    const dialog = $('#import-dialog');
    if (dialog.open && version === importVersion) dialog.close();
    await refreshAfterChange(`Добавлено заданий: ${result.newCount}`);
  } catch (error) {
    if (error.status === 401 || !currentUser || generation !== sessionGeneration) return;
    if (importIsCurrent(generation,version)) $('#import-problem').textContent = error.message || 'Сервер недоступен.';
    else notify(error.message || 'Не удалось добавить задания.',true);
  } finally {
    if (version === importVersion) setImportBusy(false);
  }
}

$('#open-import').addEventListener('click',()=> {
  if (!ready) return;
  clearImportState();
  $('#import-dialog').showModal();
});
$('#import-dialog').addEventListener('close',clearImportState);
document.addEventListener('account-cleared',clearImportState);

$('#import-pick-folder').addEventListener('click',()=>$('#import-folder').click());
$('#import-pick-files').addEventListener('click',()=>$('#import-files').click());
$('#import-folder').addEventListener('change',event=>handlePick(event.target));
$('#import-files').addEventListener('change',event=>handlePick(event.target));
$('#import-due').addEventListener('change',()=> { if (importFiles.length && !importBusy) runPreview(); });
$('#import-apply').addEventListener('click',runApply);
