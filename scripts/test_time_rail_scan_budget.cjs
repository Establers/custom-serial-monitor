// Deterministic time-rail scheduling/scan budget checks; no browser or real timers.
// Run: node scripts/test_time_rail_scan_budget.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const { create } = require('../SerialMonitor.WinUI/Assets/xterm/log-navigation.js');

let now = 0, nextTimer = 1;
const timers = new Map();
const events = new Map();
const schedule = (callback, delay = 0) => { const id = nextTimer++; timers.set(id, { at: now + delay, callback }); return id; };
function advance(milliseconds) {
  const end = now + milliseconds;
  let callbacks = 0;
  for (;;) {
    const next = [...timers].filter(([, timer]) => timer.at <= end).sort((a, b) => a[1].at - b[1].at)[0];
    if (!next) break;
    assert.ok(++callbacks < 1000, 'a rail refresh must not recursively schedule an unbounded idle loop');
    timers.delete(next[0]); now = next[1].at; next[1].callback();
  }
  now = end;
}
class Element {
  constructor(tag, doc) {
    this.tag = tag; this.doc = doc; this.children = []; this.style = {}; this.dataset = {};
    this.attributes = new Map(); this.handlers = new Map(); this.hidden = false; this.className = '';
    this.clientHeight = 600; this.clientWidth = 18; this.offsetWidth = 180; this.offsetHeight = 34;
    const classes = new Set();
    this.classList = { add: value => classes.add(value), remove: value => classes.delete(value),
      toggle(value, force) { const wanted = force ?? !classes.has(value); if (wanted) classes.add(value); else classes.delete(value); return wanted; } };
  }
  appendChild(child) { child.parent = this; this.children.push(child); return child; }
  replaceChildren() { this.children = []; }
  remove() { if (this.parent) this.parent.children = this.parent.children.filter(child => child !== this); }
  setAttribute(name, value) { this.attributes.set(name, value); }
  removeAttribute(name) { this.attributes.delete(name); }
  addEventListener(name, callback) { if (!this.handlers.has(name)) this.handlers.set(name, new Set()); this.handlers.get(name).add(callback); }
  removeEventListener(name, callback) { this.handlers.get(name)?.delete(callback); }
  getBoundingClientRect() { return { top: 0, bottom: 600, left: 0, right: 18, width: 18, height: 600 }; }
  focus() { this.doc.activeElement = this; }
  fire(name, event) { for (const handler of this.handlers.get(name) || []) handler(event); }
}
const doc = { documentElement: { lang: 'en' }, createElement(tag) { return new Element(tag, this); } };
doc.body = new Element('body', doc);
const sandbox = {
  document: doc, navigator: { language: 'en' }, innerWidth: 1000, innerHeight: 700, performance: { now: () => now },
  setTimeout: schedule, clearTimeout: id => timers.delete(id), requestAnimationFrame: callback => schedule(callback, 16),
  cancelAnimationFrame: id => timers.delete(id),
  addEventListener(name, callback) { if (!events.has(name)) events.set(name, new Set()); events.get(name).add(callback); },
  removeEventListener(name, callback) { events.get(name)?.delete(callback); }
};
sandbox.window = sandbox;
vm.runInNewContext(fs.readFileSync(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/time-rail.js'), 'utf8'), sandbox);
let reads = 0, stateCalls = 0, resolveCalls = 0, timeJumps = 0;
const epoch = 1793149200000;
const engine = create({ count: () => 1000000, revision: () => 1, timeOrder: () => 'ascending',
  at(index) { reads++; return { id: String(index + 1), timestamp: epoch + index * 10 }; } });
const state = () => { stateCalls++; const range = engine.range(); return { ...range, viewportStartTime: epoch + 100, viewportEndTime: epoch + 200 }; };
const rail = sandbox.SerialMonitorTimeRail.install({ getState: state,
  resolveTime(timestamp) { resolveCalls++; const index = engine.indexAtTime(timestamp); return { ok: index >= 0, index, id: String(index + 1), timestamp: epoch + index * 10 }; },
  navigateTime(timestamp) { timeJumps++; const index = engine.indexAtTime(timestamp); return { ok: index >= 0, index, timestamp: epoch + index * 10 }; },
  onLayoutChanged() {}, onError(error) { throw error; }
});
const root = doc.body.children.find(node => node.id === 'timeRail');
assert.ok(root);
const readsAfterInstall = reads;
advance(60000);
assert.equal(stateCalls, 1);
assert.equal(reads, readsAfterInstall, 'an untouched idle rail must perform zero extra metadata scans');

for (let request = 0; request < 100000; request++) rail.refresh();
assert.equal(timers.size, 1, 'refresh requests must share one pending timer');
advance(250);
assert.equal(stateCalls, 2);
assert.equal(reads - readsAfterInstall, 2, 'one rail refresh only reads ordered range boundary stamps');

const beforeHover = reads;
for (let request = 0; request < 100000; request++) root.fire('pointermove', { clientY: request % 600 });
assert.equal(timers.size, 1, 'hover events must share one animation frame');
advance(16);
assert.equal(resolveCalls, 1);
assert.ok(reads - beforeHover <= 20, 'a million-row hover must use one binary timestamp lookup');
const beforeActivation = reads;
root.fire('keydown', { key: 'Enter', repeat: false, ctrlKey: false, altKey: false, metaKey: false,
  preventDefault() {}, stopPropagation() {} });
assert.equal(timeJumps, 1);
assert.ok(reads - beforeActivation <= 45);
root.fire('pointerleave', {});
root.fire('keydown', { key: 'Escape', preventDefault() {}, stopPropagation() {} });
advance(1000);
const beforeIdle = { reads, stateCalls, resolveCalls };
advance(60000);
assert.deepEqual({ reads, stateCalls, resolveCalls }, beforeIdle);
assert.ok(root.children.find(child => child.className === 'time-rail-ticks').children.length <= 81);
rail.dispose();
assert.equal(timers.size, 0);
assert.equal(doc.body.children.length, 0);
assert.ok([...events.values()].every(handlers => handlers.size === 0));
console.log(JSON.stringify({ passed: true, retainedRows: 1000000, idleMinutesWithoutMetadataReads: 2,
  coalescedRefreshRequests: 100000, coalescedHoverEvents: 100000, stateCalls, resolveCalls, timeJumps,
  limitation: 'Deterministic scheduling and metadata read counts; browser rendering is covered separately.' }, null, 2));
