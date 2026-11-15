// Run: node --test scripts/test_xterm_fit.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { runInNewContext } from 'node:vm';
import { test } from 'node:test';

const html = readFileSync(new URL('../SerialMonitor.WinUI/Assets/xterm/index.html', import.meta.url), 'utf8');
const start = html.indexOf('    let pendingFit = 0;');
const end = html.indexOf('    function clearNavigationHighlight()', start);
assert.ok(start >= 0 && end > start);

function fixture() {
  let now = 0, nextId = 0, fits = 0;
  const timers = new Map();
  const window = {
    clearTimeout: id => timers.delete(id),
    setTimeout(callback, delay) {
      const id = ++nextId;
      timers.set(id, { at: now + delay, callback });
      return id;
    },
    serialMonitorFit: () => { fits++; }
  };
  const context = { window };
  runInNewContext(html.slice(start, end), context);
  return {
    window, queue: context.queueFit, fits: () => fits, pending: () => timers.size,
    advance(ms) {
      const target = now + ms;
      while (true) {
        const next = [...timers].sort((a, b) => a[1].at - b[1].at)[0];
        if (!next || next[1].at > target) break;
        now = next[1].at;
        timers.delete(next[0]);
        next[1].callback();
      }
      now = target;
    }
  };
}

test('side dragging at 25 Hz defers expensive history reflow until the viewport settles', () => {
  const f = fixture();
  for (let i = 0; i < 100; i++) {
    f.queue();
    assert.equal(f.pending(), 1, 'Only one trailing fit timer is retained');
    f.advance(40);
  }
  assert.equal(f.fits(), 0, 'Dragging must not reflow all historical rows at each pointer step');
  f.advance(200);
  assert.equal(f.fits(), 1);
  assert.equal(f.pending(), 0);
});

test('native fit requests and browser resize events share one trailing fit', () => {
  const f = fixture();
  for (let i = 0; i < 100; i++) {
    f.window.serialMonitorRequestFit();
    f.advance(15);
    f.queue();
    f.advance(35);
  }
  assert.equal(f.fits(), 0);
  f.advance(200);
  assert.equal(f.fits(), 1);
  f.window.serialMonitorRequestFit();
  f.advance(200);
  assert.equal(f.fits(), 2, 'Later requests must still fit the final size');
});
