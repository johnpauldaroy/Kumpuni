'use strict';

const byId = (id) => document.getElementById(id);
const selectionKey = 'kumpuni.mockup.printer.v1';
const queues = {
  office: {
    name: 'Office printer', path: '\\\\PRINT-HOST\\Office', connection: 'Shared printer',
    inventoryType: 'Windows share', location: 'PRINT-HOST', publicId: 'Q1',
    health: 'Connection issue', jobs: 0, driver: 'D1', port: 'P1',
    result: 'repaired',
    steps: ['Found a stale Windows share connection.', 'Reconnected the shared queue.', 'The queue responded to verification.'],
    summary: 'Repaired. The shared queue is ready.'
  },
  reception: {
    name: 'Reception printer', path: 'USB001', connection: 'USB printer',
    inventoryType: 'USB', location: 'This PC', publicId: 'Q2',
    health: 'Ready', jobs: 0, driver: 'D2', port: 'P2',
    result: 'healthy',
    steps: ['The USB queue is responding.', 'No repair needed.', 'The queue passed verification.'],
    summary: 'Ready. No repair was needed.'
  },
  floor2: {
    name: 'Floor 2 printer', path: '192.0.2.42', connection: 'TCP/IP printer',
    inventoryType: 'TCP/IP', location: '192.0.2.42', publicId: 'Q3',
    health: 'Offline', jobs: 1, driver: 'D1', port: 'P3',
    result: 'needs_attention',
    steps: ['The printer did not respond.', 'No eligible automatic repair.', 'Still offline. Check power and network, then retry.'],
    summary: 'Needs attention. Check power and network, then retry.'
  }
};

const driverRows = [['D1', 'Universal PCL6', 'x64'], ['D2', 'Windows class driver', 'x64']];
const portRows = [['P1', 'Office share', 'Network share'], ['P2', 'USB001', 'USB'], ['P3', '192.0.2.42', 'Standard TCP/IP']];
const idleSteps = ['Read the Windows queue.', 'Run an eligible repair.', 'Check the queue again.'];
const activeSteps = ['Checking the queue…', 'Checking repair options…', 'Verifying…'];
const prefersReducedMotion = matchMedia('(prefers-reduced-motion: reduce)');
const stepItems = [...document.querySelectorAll('.steps li')];
const navLinks = [...document.querySelectorAll('.sidebar .nav-link')];
const tabs = [...document.querySelectorAll('[role=tab]')];
const runButton = byId('run');
const setup = byId('setup');
const changeButton = byId('change');
const reportButton = byId('report');
const inventory = byId('inventory');
const queueResult = byId('queueResult');
let selection = { id: 'office', makeDefault: false };
let hasSavedSelection = false;
let lastReport = null;

try {
  const stored = JSON.parse(localStorage.getItem(selectionKey));
  if (stored && Object.hasOwn(queues, stored.id)) {
    selection = { id: stored.id, makeDefault: stored.makeDefault === true };
    hasSavedSelection = true;
  }
} catch {
  // The demo remains usable without browser storage.
}

function renderPrinter() {
  const queue = queues[selection.id];
  byId('savedLabel').textContent = hasSavedSelection ? 'Saved printer' : 'Demo printer';
  changeButton.querySelector('span').textContent = hasSavedSelection ? 'Change printer' : 'Set up printer';
  byId('printerName').textContent = queue.name;
  byId('printerPath').textContent = queue.path;
  byId('connectionType').textContent = queue.connection;
  byId('connectionHost').textContent = queue.location;
  byId('connectionDefault').textContent = selection.makeDefault ? 'Yes' : 'No';
}

function resetCheck() {
  lastReport = null;
  reportButton.disabled = true;
  byId('runStatus').textContent = 'No check run yet.';
  for (const [i, step] of stepItems.entries()) {
    step.className = '';
    step.removeAttribute('aria-current');
    byId('step-' + i).textContent = idleSteps[i];
  }
}

changeButton.addEventListener('click', () => {
  byId('printerSelect').value = selection.id;
  byId('makeDefault').checked = selection.makeDefault;
  setup.showModal();
  byId('printerSelect').focus();
});

for (const control of [byId('closeSetup'), byId('cancelSetup')]) {
  control.addEventListener('click', () => setup.close());
}

byId('setupForm').addEventListener('submit', (event) => {
  event.preventDefault();
  const id = byId('printerSelect').value;
  if (!Object.hasOwn(queues, id)) return;
  if (selection.id !== id) resetCheck();
  selection = { id, makeDefault: byId('makeDefault').checked };
  hasSavedSelection = true;
  try {
    localStorage.setItem(selectionKey, JSON.stringify(selection));
  } catch {
    // Keep the current selection for this session.
  }
  renderPrinter();
  setup.close();
});

runButton.addEventListener('click', async () => {
  if (runButton.disabled) return;
  const queue = queues[selection.id];
  resetCheck();
  runButton.disabled = true;
  changeButton.disabled = true;
  const runLabel = runButton.querySelector('span');
  const runStatus = byId('runStatus');
  runLabel.textContent = 'Checking…';
  const checkPanel = byId('last-check');
  if (checkPanel.getBoundingClientRect().top > innerHeight - 96) {
    checkPanel.scrollIntoView({ block: 'start' });
  }
  try {
    for (const [i, step] of stepItems.entries()) {
      const detail = byId('step-' + i);
      step.className = 'active';
      step.setAttribute('aria-current', 'step');
      runStatus.textContent = activeSteps[i];
      detail.textContent = activeSteps[i];
      await new Promise((resolve) => setTimeout(resolve, prefersReducedMotion.matches ? 70 : 520));
      step.removeAttribute('aria-current');
      step.className = queue.result === 'needs_attention' && i > 0 ? 'blocked' : 'complete';
      detail.textContent = queue.steps[i];
    }
    lastReport = {
      schemaVersion: 1, mode: 'demo', type: 'repair', queue: queue.publicId,
      result: queue.result, steps: queue.steps,
      checkedAt: new Date().toISOString()
    };
    runStatus.textContent = queue.summary;
    if (queue.result === 'repaired') {
      queue.health = 'Ready';
      queue.result = 'healthy';
      queue.steps = ['The Windows share is responding.', 'No repair needed.', 'The queue passed verification.'];
      queue.summary = 'Ready. No repair was needed.';
      renderQueueHealth();
    }
    reportButton.disabled = false;
    if (!prefersReducedMotion.matches) {
      runStatus.animate([{ opacity: .45, transform: 'translateY(3px)' }, { opacity: 1, transform: 'none' }], { duration: 330, easing: 'ease-out' });
    }
  } finally {
    runLabel.textContent = 'Check and fix';
    runButton.disabled = false;
    changeButton.disabled = false;
  }
});

function downloadReport(filename, data) {
  const href = URL.createObjectURL(new Blob([JSON.stringify(data, null, 2)], { type: 'application/json' }));
  const link = document.createElement('a');
  link.href = href;
  link.download = filename;
  document.body.append(link);
  link.click();
  link.remove();
  setTimeout(() => URL.revokeObjectURL(href), 5000);
}

reportButton.addEventListener('click', () => {
  if (lastReport) downloadReport('kumpuni-repair-demo.json', lastReport);
});

byId('inventoryReport').addEventListener('click', () => {
  const redactedQueues = Object.values(queues).map((queue) => ({
    queue: queue.publicId, connection: queue.inventoryType, health: queue.health,
    jobs: queue.jobs, driver: queue.driver, port: queue.port
  }));
  downloadReport('kumpuni-inventory-demo.json', {
    schemaVersion: 1, mode: 'demo', type: 'inventory', queues: redactedQueues
  });
});

const queueTable = inventory.innerHTML;
function renderQueueHealth() {
  for (const control of inventory.querySelectorAll('button[data-queue]')) {
    const health = queues[control.dataset.queue].health;
    const status = control.closest('tr').querySelector('.status');
    status.textContent = health;
    status.className = 'status ' + (health === 'Ready' ? 'ready' : 'attention');
  }
}
function showInventory(tab) {
  for (const item of tabs) {
    item.setAttribute('aria-selected', String(item === tab));
    item.tabIndex = item === tab ? 0 : -1;
  }
  inventory.setAttribute('aria-labelledby', tab.id);
  queueResult.hidden = true;
  if (tab.dataset.tab === 'queues') {
    inventory.innerHTML = queueTable;
    renderQueueHealth();
    return;
  }

  const drivers = tab.dataset.tab === 'drivers';
  const columns = drivers ? ['ID', 'Driver', 'Architecture'] : ['ID', 'Port', 'Type'];
  const table = document.createElement('table');
  const caption = table.createCaption();
  caption.className = 'sr-only';
  caption.textContent = drivers ? 'Demo Windows printer drivers' : 'Demo Windows printer ports';
  const head = table.createTHead().insertRow();
  for (const column of columns) {
    const cell = document.createElement('th');
    cell.scope = 'col';
    cell.textContent = column;
    head.append(cell);
  }
  const body = table.createTBody();
  for (const values of drivers ? driverRows : portRows) {
    const row = body.insertRow();
    for (const value of values) row.insertCell().textContent = value;
  }
  inventory.replaceChildren(table);
}

for (const [index, tab] of tabs.entries()) {
  tab.addEventListener('click', () => showInventory(tab));
  tab.addEventListener('keydown', (event) => {
    const positions = { ArrowLeft: (index + tabs.length - 1) % tabs.length, ArrowRight: (index + 1) % tabs.length, Home: 0, End: tabs.length - 1 };
    if (!Object.hasOwn(positions, event.key)) return;
    event.preventDefault();
    const next = tabs[positions[event.key]];
    showInventory(next);
    next.focus();
  });
}

inventory.addEventListener('click', (event) => {
  const button = event.target.closest('button[data-queue]');
  if (!button) return;
  const queue = queues[button.dataset.queue];
  queueResult.hidden = false;
  queueResult.textContent = queue.name + ': ' + (queue.health === 'Ready'
    ? 'Queue responded. No pending jobs.' : queue.health === 'Offline'
      ? 'Queue is offline. 1 pending job. Check power and network.'
      : 'The Windows share connection is stale. Run Check and fix to reconnect it.');
});

for (const link of navLinks) {
  link.addEventListener('click', () => {
    for (const item of navLinks) item.removeAttribute('aria-current');
    link.setAttribute('aria-current', 'location');
  });
}

renderPrinter();

const gsap = window.gsap;
const ScrollTrigger = window.ScrollTrigger;
const Lenis = window.Lenis;

if (gsap && ScrollTrigger && Lenis) {
  gsap.registerPlugin(ScrollTrigger);
  const media = gsap.matchMedia();
  media.add('(prefers-reduced-motion: no-preference)', () => {
    const lenis = new Lenis({ autoRaf: false, anchors: { offset: -22 } });
    const frame = (seconds) => lenis.raf(seconds * 1000);
    lenis.on('scroll', ScrollTrigger.update);
    gsap.ticker.add(frame);

    gsap.timeline({ defaults: { duration: .6, ease: 'power2.out' } })
      .from('.hero-copy', { y: 8, opacity: .8 })
      .from('.hero-photo', { clipPath: 'inset(0 0 0 10%)' }, '<')
      .from('.details', { y: 6, opacity: .85 }, '<.12');

    gsap.to('.hero-photo', {
      yPercent: -4, scale: 1.06, ease: 'none',
      scrollTrigger: { trigger: '.overview', start: 'top top', end: 'bottom top', scrub: true }
    });
    gsap.from('.step-line', {
      scaleX: .4, duration: .6, stagger: .1, ease: 'power2.out',
      scrollTrigger: { trigger: '#last-check', start: 'top 84%', once: true }
    });
    gsap.from('#technician', {
      clipPath: 'inset(0 0 10px round 15px)', duration: .55, ease: 'power2.out',
      scrollTrigger: { trigger: '#technician', start: 'top 88%', once: true }
    });

    return () => {
      gsap.ticker.remove(frame);
      lenis.destroy();
    };
  });
}
