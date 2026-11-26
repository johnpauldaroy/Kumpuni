'use strict';

const bridge = window.chrome?.webview;
const byId = (id) => document.getElementById(id);
const steps = [...document.querySelectorAll('.steps li')];
const initialSteps = steps.map((step) => step.querySelector('p').textContent);
const tabs = [...document.querySelectorAll('[role=tab]')];
const setup = byId('setup');
const queueSelect = byId('printerSelect');
const pathInput = byId('printerPathInput');
const run = byId('run');
const change = byId('change');
const status = byId('runStatus');
const report = byId('report');
const inventory = byId('inventory');
const inventoryReport = byId('inventoryReport');
const queueResult = byId('queueResult');
let profile;
let snapshot;
let hasReport = false;
let pending = '';
let initial = true;
let selectedTab = tabs[0];

function request(type, data = {}) {
  if (!bridge || pending) return;
  pending = type;
  syncControls();
  bridge.postMessage({ type, ...data });
}

function syncControls() {
  const busy = Boolean(pending);
  run.disabled = busy;
  change.disabled = busy;
  report.disabled = busy || !hasReport;
  inventoryReport.disabled = busy || !snapshot?.available;
  byId('refreshInventory').disabled = busy;
  byId('nativeTools').disabled = busy;
  byId('setupForm').querySelector('[type=submit]').disabled = busy;
  for (const button of inventory.querySelectorAll('button')) button.disabled = busy;
  run.querySelector('span').textContent = pending === 'repair' ? 'Checking…' : profile ? 'Check and fix' : 'Set up printer';
}

function showSetup() {
  if (setup.open) return;
  const installed = [...queueSelect.options].some((option) => option.value && option.value === profile?.queue);
  queueSelect.value = installed ? profile.queue : '';
  pathInput.value = profile?.shared && !installed ? profile.queue : '';
  byId('makeDefault').checked = Boolean(profile?.makeDefault);
  byId('setupStatus').textContent = '';
  setup.showModal();
  queueSelect.focus();
}

function resetOutcome() {
  hasReport = false;
  status.textContent = 'No check run yet.';
  for (const [index, step] of steps.entries()) {
    step.className = '';
    step.removeAttribute('aria-current');
    step.querySelector('p').textContent = initialSteps[index];
  }
  byId('repairDetail')?.remove();
  byId('knownIssues')?.remove();
}

function renderProfile(data) {
  profile = data.profile;
  byId('savedLabel').textContent = profile ? 'Saved printer' : 'Printer setup';
  byId('printerName').textContent = profile?.name ?? 'Choose your printer';
  byId('printerPath').textContent = profile?.queue ?? 'Installed printer or Windows share';
  byId('connectionType').textContent = profile ? (profile.shared ? 'Windows share' : 'Installed queue') : '—';
  byId('connectionHost').textContent = profile?.location ?? '—';
  byId('connectionDefault').textContent = profile ? (profile.makeDefault ? 'Set when ready' : 'Keep current') : '—';
  change.querySelector('span').textContent = profile ? 'Change printer' : 'Set up printer';
  if (data.saved) {
    resetOutcome();
    setup.close();
    run.focus();
  }
  if (initial) {
    initial = false;
    if (!profile) showSetup();
    if (data.error) byId('setupStatus').textContent = data.error;
    request('refresh');
  }
  syncControls();
}

function setStep(index, state, text) {
  const step = steps[index];
  if (!step) return;
  step.className = state;
  step.querySelector('p').textContent = text;
  if (state === 'active') step.setAttribute('aria-current', 'step');
  else step.removeAttribute('aria-current');
}

function renderProgress(data) {
  status.textContent = data.message;
  if (data.phase > 0) setStep(0, 'complete', 'Windows returned the initial check.');
  if (data.phase === 1) setStep(2, '', initialSteps[2]);
  if (data.phase === 2) setStep(1, 'complete', 'Repair attempted.');
  setStep(data.phase, 'active', data.message);
}

function renderOutcome(outcome) {
  hasReport = true;
  status.textContent = outcome.title;
  setStep(0, outcome.inspected ? 'complete' : 'blocked',
    outcome.inspected ? 'Windows returned the initial check.' : 'Queue check unavailable.');
  const applied = outcome.steps.length > 0;
  const confirmed = applied && outcome.checkedAfterRepair && outcome.steps.every((step) => step.verified);
  const repairStatus = applied ? (confirmed ? 'Applied and confirmed.' : 'See the action results below.')
    : (outcome.ready ? 'No repair needed.' : 'No eligible repair applied.');
  setStep(1, applied && !confirmed ? 'blocked' : 'complete', repairStatus);
  setStep(2, outcome.ready ? 'complete' : 'blocked', outcome.detail);
  byId('repairDetail')?.remove();
  if (applied) {
    const detail = document.createElement('details');
    detail.id = 'repairDetail';
    detail.className = 'queue-result';
    const summary = document.createElement('summary');
    summary.textContent = 'Repair actions';
    detail.append(summary);
    const actions = document.createElement('ul');
    for (const step of outcome.steps) {
      const item = document.createElement('li');
      const code = step.code ? ' (Windows ' + step.code + ')' : '';
      item.textContent = step.action + ': ' + step.message + code;
      actions.append(item);
    }
    detail.append(actions);
    byId('last-check').append(detail);
  }
  byId('knownIssues')?.remove();
  if (outcome.knownIssues?.length) {
    const known = document.createElement('details');
    known.id = 'knownIssues';
    known.className = 'queue-result';
    known.open = !outcome.ready;
    const summary = document.createElement('summary');
    summary.textContent = 'Known Windows issues for this PC (' + outcome.knownIssues.length + ')';
    known.append(summary);
    const list = document.createElement('ul');
    for (const issue of outcome.knownIssues) {
      const item = document.createElement('li');
      const title = document.createElement('strong');
      title.textContent = issue.title;
      const status = document.createElement('p');
      status.textContent = issue.status;
      const text = document.createElement('p');
      text.textContent = issue.detail;
      item.append(title, status, text);
      if (issue.url) {
        const source = document.createElement('p');
        source.textContent = 'Source: ' + issue.url;
        item.append(source);
      }
      list.append(item);
    }
    known.append(list);
    byId('last-check').append(known);
  }
  syncControls();
}

function updateOptions() {
  const current = queueSelect.value;
  queueSelect.replaceChildren(new Option('Choose a printer', ''));
  for (const queue of snapshot.queues ?? []) queueSelect.add(new Option(queue.name, queue.name));
  queueSelect.value = [...queueSelect.options].some((item) => item.value === current) ? current : '';
}

function showQueueResult(text) {
  queueResult.hidden = !text;
  queueResult.textContent = text ?? '';
}

function renderInventory() {
  if (!snapshot?.available) {
    const unavailable = document.createElement('p');
    unavailable.className = 'queue-result';
    unavailable.textContent = snapshot?.status ?? 'Reading the Windows printer inventory…';
    inventory.replaceChildren(unavailable);
    return;
  }
  const type = selectedTab.dataset.tab;
  const table = document.createElement('table');
  const caption = table.createCaption();
  caption.className = 'sr-only';
  caption.textContent = 'Windows printer ' + type;
  const head = table.createTHead().insertRow();
  const columns = type === 'queues' ? ['Printer', 'Connection', 'Status', 'Jobs', 'Action']
    : type === 'drivers' ? ['Driver', 'Environment', 'Version'] : ['Port', 'Type', 'Monitor'];
  for (const title of columns) {
    const th = document.createElement('th');
    th.scope = 'col';
    th.textContent = title;
    head.append(th);
  }
  const body = table.createTBody();
  if (type === 'queues') body.id = 'inventoryRows';
  const items = snapshot[type];
  if (items.length === 0) {
    const cell = body.insertRow().insertCell();
    cell.colSpan = columns.length;
    cell.textContent = 'Windows reported no ' + type + '.';
  }
  for (const item of items) {
    const row = body.insertRow();
    const label = document.createElement('th');
    label.scope = 'row';
    label.textContent = item.name;
    row.append(label);
    if (type === 'queues') {
      row.insertCell().textContent = item.connection;
      const badge = document.createElement('span');
      badge.textContent = item.status;
      badge.className = 'status ' + (item.health === 'Available' || item.health === 'Busy' ? 'ready' : 'attention');
      row.insertCell().append(badge);
      row.insertCell().textContent = item.jobs ?? '—';
      const check = document.createElement('button');
      check.className = 'button text-button';
      check.type = 'button';
      check.textContent = 'Check queue';
      check.setAttribute('aria-label', 'Check queue ' + item.name);
      check.disabled = Boolean(pending);
      check.addEventListener('click', () => {
        showQueueResult('Checking the selected Windows queue…');
        request('check-queue', { queue: item.name });
      });
      row.insertCell().append(check);
    } else {
      row.insertCell().textContent = type === 'drivers' ? item.environment : item.kind;
      row.insertCell().textContent = (type === 'drivers' ? item.version : item.monitor) ?? '—';
    }
  }
  inventory.replaceChildren(table);
}

function selectTab(tab) {
  selectedTab = tab;
  for (const item of tabs) {
    item.setAttribute('aria-selected', item === tab);
    item.tabIndex = item === tab ? 0 : -1;
  }
  inventory.setAttribute('aria-labelledby', tab.id);
  renderInventory();
}

for (const [index, tab] of tabs.entries()) {
  tab.addEventListener('click', () => selectTab(tab));
  tab.addEventListener('keydown', (event) => {
    const positions = { ArrowLeft: (index + tabs.length - 1) % tabs.length,
      ArrowRight: (index + 1) % tabs.length, Home: 0, End: tabs.length - 1 };
    if (!Object.hasOwn(positions, event.key)) return;
    event.preventDefault();
    selectTab(tabs[positions[event.key]]);
    selectedTab.focus();
  });
}
for (const link of document.querySelectorAll('.sidebar .nav-link')) {
  link.addEventListener('click', () => {
    document.querySelector('.sidebar [aria-current]')?.removeAttribute('aria-current');
    link.setAttribute('aria-current', 'location');
  });
}

byId('setupForm').addEventListener('submit', (event) => {
  event.preventDefault();
  const queue = pathInput.value.trim() || queueSelect.value;
  if (!queue) {
    byId('setupStatus').textContent = 'Choose an installed printer or enter a shared path.';
    return;
  }
  request('save', { queue, makeDefault: byId('makeDefault').checked });
});
for (const id of ['closeSetup', 'cancelSetup']) byId(id).addEventListener('click', () => setup.close());
run.addEventListener('click', () => {
  if (!profile) {
    showSetup();
    return;
  }
  resetOutcome();
  setStep(0, 'active', 'Reading the Windows queue…');
  status.textContent = 'Checking your printer…';
  request('repair');
  if (byId('last-check').getBoundingClientRect().top > innerHeight - 120)
    byId('last-check').scrollIntoView({ block: 'start' });
});
change.addEventListener('click', showSetup);
byId('refreshInventory').addEventListener('click', () => request('refresh'));
byId('nativeTools').addEventListener('click', () => request('advanced'));
report.addEventListener('click', () => request('export-repair'));
inventoryReport.addEventListener('click', () => request('export-inventory'));

bridge?.addEventListener('message', ({ data }) => {
  switch (data.type) {
    case 'profile': renderProfile(data); break;
    case 'progress': renderProgress(data); break;
    case 'outcome': renderOutcome(data); break;
    case 'setup-error': byId('setupStatus').textContent = data.message; break;
    case 'inventory':
      snapshot = data;
      updateOptions();
      renderInventory();
      if (data.status) showQueueResult(data.status);
      break;
    case 'queue-result': showQueueResult(data.message); break;
    case 'exported':
      if (data.scope === 'repair') status.textContent = data.message;
      else showQueueResult(data.message);
      break;
    case 'idle': pending = ''; syncControls(); break;
    case 'error':
      if (setup.open) byId('setupStatus').textContent = data.message;
      else if (steps.some((step) => step.classList.contains('active'))) {
        resetOutcome();
        status.textContent = data.message;
      } else showQueueResult(data.message);
      break;
  }
});

const gsap = window.gsap;
const trigger = window.ScrollTrigger;
if (gsap && trigger && window.Lenis) {
  gsap.registerPlugin(trigger);
  gsap.matchMedia().add('(prefers-reduced-motion: no-preference)', () => {
    const lenis = new window.Lenis({
      autoRaf: false, anchors: { offset: -22 }, prevent: (node) => node instanceof HTMLDialogElement
    });
    const frame = (seconds) => lenis.raf(seconds * 1000);
    lenis.on('scroll', trigger.update);
    gsap.ticker.add(frame);
    gsap.timeline({ defaults: { duration: .6, ease: 'power2.out' } })
      .from('.hero-copy', { y: 8, opacity: .8 })
      .from('.hero-photo', { clipPath: 'inset(0 0 0 10%)' }, '<')
      .from('.details', { y: 6, opacity: .85 }, '<.12');
    gsap.to('.hero-photo', { yPercent: -4, scale: 1.06, ease: 'none',
      scrollTrigger: { trigger: '.overview', start: 'top top', end: 'bottom top', scrub: true } });
    gsap.from('.step-line', { scaleX: .4, duration: .6, stagger: .1,
      scrollTrigger: { trigger: '#last-check', start: 'top 84%', once: true } });
    gsap.from('#technician', { clipPath: 'inset(0 0 10px round 15px)', duration: .55,
      scrollTrigger: { trigger: '#technician', start: 'top 88%', once: true } });
    return () => {
      gsap.ticker.remove(frame);
      lenis.destroy();
    };
  });
}

if (bridge) bridge.postMessage({ type: 'ready' });
