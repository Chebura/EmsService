'use strict';

const $ = (s) => document.querySelector(s);
const $$ = (s) => Array.from(document.querySelectorAll(s));

// TimeSpan in .NET "c" format: [d.]hh:mm[:ss[.fffffff]]
const TIMESPAN_RE = /^(\d+\.)?\d{1,2}:\d{1,2}(:\d{1,2}(\.\d{1,7})?)?$/;

const RESULTS = {
  Completed: 'завершена',
  Failed: 'ошибка',
  Stopped: 'остановлена'
};

let settings = null;        // settings being edited (saved on "Сохранить")
let savedGroups = new Set(); // groups present in the saved settings file
let currentGroup = null;
let status = null;

// ---------- helpers ----------

async function api(method, url, body) {
  const res = await fetch(url, {
    method,
    headers: body !== undefined ? { 'Content-Type': 'application/json' } : {},
    body: body !== undefined ? JSON.stringify(body) : undefined
  });
  if (res.status === 204 || res.status === 202) return null;
  const data = await res.json().catch(() => null);
  if (!res.ok) {
    const errors = data?.errors || data?.Errors || [data?.detail || data?.title || res.statusText];
    throw new Error(errors.join('\n'));
  }
  return data;
}

let toastTimer;
function toast(message, isError) {
  const el = $('#toast');
  el.textContent = message;
  el.className = isError ? 'error' : '';
  el.hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.hidden = true, isError ? 8000 : 3000);
}

function fmtDate(value) {
  return value ? new Date(value).toLocaleString() : '—';
}

function esc(value) {
  const d = document.createElement('div');
  d.textContent = value ?? '';
  return d.innerHTML;
}

function getPath(obj, path) {
  return path.split('.').reduce((o, k) => (o == null ? undefined : o[k]), obj);
}

function setPath(obj, path, value) {
  const keys = path.split('.');
  const last = keys.pop();
  const target = keys.reduce((o, k) => (o[k] ??= {}), obj);
  target[last] = value;
}

function checkTimespans(root) {
  let ok = true;
  root.querySelectorAll('[data-timespan]').forEach(input => {
    const valid = input.value.trim() === '' || TIMESPAN_RE.test(input.value.trim());
    input.classList.toggle('invalid', !valid);
    ok = ok && valid;
  });
  return ok;
}

// ---------- theme ----------

const darkQuery = window.matchMedia('(prefers-color-scheme: dark)');

function currentTheme() {
  return document.documentElement.dataset.theme || (darkQuery.matches ? 'dark' : 'light');
}

function renderThemeButton() {
  const dark = currentTheme() === 'dark';
  const btn = $('#btn-theme');
  btn.textContent = dark ? '☀' : '☾';
  btn.title = dark ? 'Светлая тема' : 'Тёмная тема';
  btn.setAttribute('aria-label', btn.title);
}

$('#btn-theme').addEventListener('click', () => {
  const theme = currentTheme() === 'dark' ? 'light' : 'dark';
  document.documentElement.dataset.theme = theme;
  try {
    localStorage.setItem('ems-theme', theme);
  } catch (e) {
    // Storage is unavailable: the choice lasts until the page is reloaded.
  }
  renderThemeButton();
});

// Follow the system theme while the user has not chosen one.
darkQuery.addEventListener('change', renderThemeButton);
renderThemeButton();

// ---------- tabs ----------

$$('.tab').forEach(btn => btn.addEventListener('click', () => {
  $$('.tab').forEach(b => b.classList.toggle('active', b === btn));
  $$('.panel').forEach(p => p.hidden = p.id !== 'tab-' + btn.dataset.tab);
  if (btn.dataset.tab === 'logs') loadLogs();
  if (btn.dataset.tab === 'run') loadTasks();
}));

// ---------- status & run ----------

async function loadStatus() {
  try {
    const data = await api('GET', '/api/status');
    status = data.status;
    $('#version').textContent = 'v' + data.version;

    const state = $('#run-state');
    if (status.isRunning) {
      state.textContent = 'выполняется рассылка';
      state.className = 'state-running';
    } else {
      state.textContent = 'ожидание';
      state.className = '';
    }

    const details = [];
    if (status.lastRunStartedAt) details.push('Последний запуск: ' + fmtDate(status.lastRunStartedAt));
    if (!status.isRunning && status.lastRunFinishedAt) {
      details.push('завершён: ' + fmtDate(status.lastRunFinishedAt) + ' (' + (RESULTS[status.lastRunResult] || status.lastRunResult) + ')');
    }
    $('#run-details').textContent = details.join(', ') || 'Рассылка ещё не запускалась.';
    $('#run-error').textContent = status.lastError || '';

    $('#btn-run').disabled = status.isRunning;
    $('#btn-stop').disabled = !status.isRunning;
  } catch (e) {
    $('#run-state').textContent = 'нет связи с сервисом';
    $('#run-state').className = 'state-failed';
  }
}

$('#btn-run').addEventListener('click', async () => {
  try {
    await api('POST', '/api/run');
    toast('Рассылка запущена.');
    setTimeout(refreshRun, 500);
  } catch (e) {
    toast(e.message, true);
  }
});

$('#btn-stop').addEventListener('click', async () => {
  try {
    await api('POST', '/api/stop');
    toast('Остановка рассылки…');
    setTimeout(refreshRun, 500);
  } catch (e) {
    toast(e.message, true);
  }
});

function refreshRun() {
  loadStatus();
  loadTasks();
}

// ---------- tasks ----------

async function loadTasks() {
  let tasks;
  try {
    tasks = await api('GET', '/api/tasks');
  } catch (e) {
    return;
  }
  const body = $('#tasks');
  if (!tasks.length) {
    body.innerHTML = '<tr><td colspan="9" class="muted">Задач нет.</td></tr>';
    return;
  }
  body.innerHTML = tasks.map(t => `
    <tr>
      <td><button class="link" data-show="${esc(t.name)}">${esc(t.name)}</button>${t.error ? `<div class="error">${esc(t.error)}</div>` : ''}</td>
      <td>${fmtDate(t.created)}</td>
      <td>${fmtDate(t.modified)}</td>
      <td class="num">${t.total}</td>
      <td class="num">${t.sent}</td>
      <td class="num">${t.waiting}</td>
      <td class="num">${t.failed}</td>
      <td class="num">${t.dead}</td>
      <td><button class="danger" data-delete="${esc(t.name)}">Удалить</button></td>
    </tr>`).join('');
}

$('#tasks').addEventListener('click', async (e) => {
  const show = e.target.dataset.show;
  const del = e.target.dataset.delete;
  if (show) await showTask(show);
  if (del) {
    if (!confirmDelete(`Удалить задачу ${del}? При следующем запуске письма этой задачи будут отправлены заново.`)) return;
    try {
      await api('DELETE', '/api/tasks/' + encodeURIComponent(del));
      toast('Задача удалена.');
      loadTasks();
    } catch (err) {
      toast(err.message, true);
    }
  }
});

// Native confirm() blocks the page; a second click on the same button is used instead.
let pendingConfirm = null;
function confirmDelete(message) {
  if (pendingConfirm === message) {
    pendingConfirm = null;
    return true;
  }
  pendingConfirm = message;
  toast(message + '\nНажмите ещё раз для подтверждения.');
  setTimeout(() => { if (pendingConfirm === message) pendingConfirm = null; }, 5000);
  return false;
}

async function showTask(name) {
  try {
    const task = await api('GET', '/api/tasks/' + encodeURIComponent(name));
    const now = Date.now();
    $('#task-details-title').textContent = name;
    $('#task-items').innerHTML = (task.items || []).map(i => {
      let state;
      if (i.attempt && !i.failed) state = 'отправлено';
      else if (i.attemptsLeft !== null && i.attemptsLeft <= 0 || new Date(i.lifetime).getTime() < now) state = 'не отправлено';
      else if (i.failed) state = 'ожидает повтора';
      else state = 'ожидает';
      return `<tr>
        <td>${esc(i.receiver)}</td>
        <td>${state}</td>
        <td>${fmtDate(i.attempt)}</td>
        <td class="num">${i.attemptsLeft ?? '—'}</td>
        <td class="wrap">${esc(i.failure)}</td>
      </tr>`;
    }).join('');
    $('#task-details').hidden = false;
    $('#task-details').scrollIntoView({ behavior: 'smooth' });
  } catch (e) {
    toast(e.message, true);
  }
}

$('#btn-task-close').addEventListener('click', () => $('#task-details').hidden = true);
$('#btn-tasks-refresh').addEventListener('click', loadTasks);

// ---------- settings ----------

async function loadSettings() {
  const data = await api('GET', '/api/settings');
  settings = data.settings;
  settings.groups ??= {};
  savedGroups = new Set(Object.keys(settings.groups));
  $('#settings-path').textContent = data.filePath;

  const form = $('#settings-form');
  form.querySelectorAll('input[name]').forEach(input => {
    const value = getPath(settings, camel(input.name));
    if (input.type === 'checkbox') input.checked = !!value;
    else input.value = value ?? '';
  });
  const password = form.querySelector('[name="SmtpClient.Password"]');
  password.value = '';
  password.placeholder = data.smtpPasswordSet ? 'задан (оставьте пустым, чтобы не менять)' : 'не задан';

  renderRunGroups();
  renderGroupList();
}

// Form names use C# property names; JSON from the API is camelCase.
function camel(path) {
  return path.split('.').map(k => k[0].toLowerCase() + k.slice(1)).join('.');
}

function readSettingsForm() {
  const form = $('#settings-form');
  form.querySelectorAll('input[name]').forEach(input => {
    let value;
    if (input.type === 'checkbox') value = input.checked;
    else if (input.type === 'number') value = input.value === '' ? null : Number(input.value);
    else value = input.value.trim() === '' ? null : input.value.trim();
    setPath(settings, camel(input.name), value);
  });
  // Null password keeps the stored one.
  settings.rollUpStrategy.delay ??= '00:00:00';
  settings.rollUpStrategy.maxCount ??= 1;
}

async function saveSettings() {
  await api('PUT', '/api/settings', settings);
  toast('Настройки сохранены.');
  await loadSettings();
}

$('#settings-form').addEventListener('submit', async (e) => {
  e.preventDefault();
  if (!checkTimespans($('#settings-form'))) {
    toast('Интервалы времени указываются в формате [д.]чч:мм:сс', true);
    return;
  }
  try {
    readSettingsForm();
    await saveSettings();
  } catch (err) {
    toast(err.message, true);
  }
});

function renderRunGroups() {
  const names = settings.defaultGroups
    ? settings.defaultGroups.split(';').map(x => x.trim()).filter(Boolean)
    : Object.keys(settings.groups);
  $('#run-groups').textContent = names.length ? names.join(', ') : 'нет групп';
}

// ---------- groups ----------

function renderGroupList() {
  const list = $('#group-list');
  const names = Object.keys(settings.groups).sort((a, b) => a.localeCompare(b));
  list.innerHTML = names.map(n => `
    <li data-name="${esc(n)}" class="${n === currentGroup ? 'active' : ''}">
      <span>${esc(n)}</span><span class="muted">${(settings.groups[n].receivers || []).length}</span>
    </li>`).join('') || '<li class="muted">Групп нет</li>';

  if (currentGroup && !settings.groups[currentGroup]) currentGroup = null;
  $('#group-editor').hidden = !currentGroup;
}

$('#group-list').addEventListener('click', (e) => {
  const li = e.target.closest('li[data-name]');
  if (!li) return;
  if (!applyGroupEditor()) return;
  selectGroup(li.dataset.name);
});

function selectGroup(name) {
  currentGroup = name;
  const g = settings.groups[name];
  $('#g-name').value = name;
  $('#g-subject').value = g.subject ?? '';
  $('#g-file').value = g.plainTextFileName ?? '';
  $('#g-encoding').value = g.encoding ?? '';
  $('#g-actuality').value = g.actuality ?? '';
  $('#g-receivers').value = (g.receivers || []).join('\n');
  updateReceiversCount();
  renderGroupList();
  loadGroupText();
}

// Copies editor values into the settings model. Returns false when values are invalid.
function applyGroupEditor() {
  if (!currentGroup) return true;
  if (!checkTimespans($('#group-editor'))) {
    toast('Актуальность указывается в формате [д.]чч:мм:сс', true);
    return false;
  }
  const name = $('#g-name').value.trim();
  if (!name) {
    toast('Имя группы не может быть пустым.', true);
    return false;
  }
  if (name !== currentGroup && settings.groups[name]) {
    toast(`Группа ${name} уже существует.`, true);
    return false;
  }
  const g = settings.groups[currentGroup];
  g.subject = $('#g-subject').value;
  g.plainTextFileName = $('#g-file').value.trim() || null;
  g.encoding = $('#g-encoding').value.trim() || null;
  g.actuality = $('#g-actuality').value.trim() || null;
  g.receivers = $('#g-receivers').value.split(/[\s,;]+/).map(x => x.trim()).filter(Boolean);
  if (name !== currentGroup) {
    delete settings.groups[currentGroup];
    settings.groups[name] = g;
    currentGroup = name;
  }
  return true;
}

function updateReceiversCount() {
  const n = $('#g-receivers').value.split(/[\s,;]+/).filter(Boolean).length;
  $('#g-count').textContent = `(${n})`;
}
$('#g-receivers').addEventListener('input', updateReceiversCount);

$('#btn-group-add').addEventListener('click', () => {
  if (!applyGroupEditor()) return;
  let i = 1;
  while (settings.groups['group' + i]) i++;
  const name = 'group' + i;
  settings.groups[name] = {
    plainTextFileName: `texts/${name}.txt`,
    subject: '',
    actuality: '7.00:00:00',
    receivers: []
  };
  selectGroup(name);
});

$('#btn-group-delete').addEventListener('click', () => {
  if (!currentGroup) return;
  if (!confirmDelete(`Удалить группу ${currentGroup}? Изменение вступит в силу после сохранения.`)) return;
  delete settings.groups[currentGroup];
  currentGroup = null;
  renderGroupList();
});

$('#btn-group-save').addEventListener('click', async () => {
  if (!applyGroupEditor()) return;
  try {
    const keep = currentGroup;
    await saveSettings();
    if (keep && settings.groups[keep]) selectGroup(keep);
  } catch (e) {
    toast(e.message, true);
  }
});

async function loadGroupText() {
  const info = $('#g-text-info');
  const text = $('#g-text');
  const saveBtn = $('#btn-text-save');
  if (!savedGroups.has(currentGroup)) {
    info.textContent = 'Сохраните группы, чтобы редактировать текст письма.';
    text.value = '';
    text.disabled = saveBtn.disabled = true;
    return;
  }
  text.disabled = saveBtn.disabled = false;
  try {
    const data = await api('GET', `/api/groups/${encodeURIComponent(currentGroup)}/text`);
    info.textContent = `Файл: ${data.fileName}, кодировка: ${data.encoding}` + (data.exists ? '' : ' (файл ещё не создан)');
    text.value = data.text ?? '';
  } catch (e) {
    info.textContent = e.message;
    text.value = '';
  }
}

$('#btn-text-save').addEventListener('click', async () => {
  try {
    await api('PUT', `/api/groups/${encodeURIComponent(currentGroup)}/text`, { text: $('#g-text').value });
    toast('Текст письма сохранён.');
    loadGroupText();
  } catch (e) {
    toast(e.message, true);
  }
});

// ---------- logs ----------

async function loadLogs() {
  try {
    const lines = await api('GET', '/api/logs?count=500');
    const pre = $('#logs');
    const atBottom = pre.scrollTop + pre.clientHeight >= pre.scrollHeight - 20;
    pre.textContent = lines.join('\n');
    if (atBottom) pre.scrollTop = pre.scrollHeight;
  } catch (e) {
    // Ignore: status line shows connectivity problems.
  }
}

$('#btn-logs-refresh').addEventListener('click', loadLogs);

// ---------- startup ----------

(async () => {
  try {
    await loadSettings();
  } catch (e) {
    toast('Не удалось загрузить настройки: ' + e.message, true);
  }
  refreshRun();
  setInterval(() => {
    loadStatus();
    if (!$('#tab-run').hidden && status?.isRunning) loadTasks();
    if (!$('#tab-logs').hidden && $('#logs-auto').checked) loadLogs();
  }, 3000);
})();
