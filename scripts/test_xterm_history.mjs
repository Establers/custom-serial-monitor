// Run: node --expose-gc --test scripts/test_xterm_history.mjs
// Includes the shipped xterm parser/buffer, without a DOM renderer.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';

const html = readFileSync(new URL('../SerialMonitor.WinUI/Assets/xterm/index.html', import.meta.url), 'utf8');
function section(start, end) {
  const from = html.indexOf(start), to = html.indexOf(end, from);
  assert.ok(from >= 0 && to > from);
  return html.slice(from, to);
}

test('history metadata wraps, shrinks, grows and clears with exact IDs and timestamps', () => {
  const terminal = { options: { scrollback: 1000 }, rows: 30, parser: { registerOscHandler() {} } };
  const context = { terminal };
  runInNewContext(
    section('    const appendQueue = [];', '    const contextMenu =') +
    section('    function resetParsedLogLineIds()', '    function normalizeLogText(') +
    section('    function getTimestampForLogicalLineIndex(', '    function getSelectionDeltaMilliseconds('), context);
  const base = 9007199254740993n; // IDs remain exact beyond Number.MAX_SAFE_INTEGER.
  for (let i = 0; i < 5000; i++) context.appendParsedLogLineId(String(base + BigInt(i)), i);
  assert.equal(context.getParsedLogLineIdCount(), 1031);
  assert.equal(context.findParsedLogLineIdIndex(String(base + 3968n)), -1);
  assert.equal(context.findParsedLogLineIdIndex(String(base + 3969n)), 0);
  assert.equal(context.getTimestampForLogicalLineIndex(0, 1031), 3969);
  assert.equal(context.getTimestampForLogicalLineIndex(1030, 1031), 4999);
  terminal.options.scrollback = 100;
  assert.equal(context.getParsedLogLineIdCount(), 131);
  assert.equal(context.getTimestampForLogicalLineIndex(0, 131), 4869);
  terminal.options.scrollback = 1_000_000;
  assert.equal(context.getParsedLogLineIdCount(), 131);
  context.appendParsedLogLineId(String(base + 5000n), 5000);
  assert.equal(context.findParsedLogLineIdIndex(String(base + 5000n)), 131);
  context.resetParsedLogLineIds();
  assert.equal(context.getParsedLogLineIdCount(), 0);
  assert.equal(context.findParsedLogLineIdIndex(String(base + 5000n)), -1);
});

test('shipped xterm retains one million rows and plateaus across three million writes', async t => {
  const { Terminal } = createRequire(import.meta.url)('../SerialMonitor.WinUI/Assets/xterm/xterm.js');
  const terminal = new Terminal({ cols: 120, rows: 30, scrollback: 1_000_000 });
  const { install } = createRequire(import.meta.url)('../SerialMonitor.WinUI/Assets/xterm/compact-scrollback.js');
  const compressed = process.env.COMPACT_HISTORY !== '0';
  const compact = compressed
    ? install(terminal, { onError(error) { throw error; } })
    : { compact() {}, dispose() {} };
  const context = { terminal, window: {}, invalidateLogicalLineStartRows() {}, trimParsedLogLineIds() {} };
  runInNewContext(section('    window.serialMonitorSetScrollback =', '    window.serialMonitorSetFont ='), context);
  assert.equal(context.window.serialMonitorSetScrollback(1_000_000), true);
  assert.equal(terminal.options.scrollback, 1_000_000);
  let firstMemory;
  try {
    for (let pass = 0; pass < 3; pass++) {
      const start = performance.now();
      for (let batch = 0; batch < 1000; batch++) {
        const id = pass * 1_000_000 + batch * 1000;
        const text = Array.from({ length: 1000 }, (_, i) => `packet ${id + i} temperature=25.3 voltage=3.300 status=OK\r\n`).join('');
        await new Promise(resolve => terminal.write(text, resolve));
        compact.compact(Infinity, Infinity);
      }
      global.gc?.();
      const memory = process.memoryUsage();
      if (global.gc && compressed) assert.ok(memory.heapUsed + memory.arrayBuffers < 600 * 1048576,
        'The compact million-row history must stay below 600 MiB for this fixture.');
      t.diagnostic(`Pass ${pass + 1}: ${((performance.now() - start) / 1000).toFixed(2)}s, RSS ${(memory.rss / 1048576).toFixed(1)} MiB, live JS+cells ${((memory.heapUsed + memory.arrayBuffers) / 1048576).toFixed(1)} MiB`);
      assert.ok(terminal.buffer.active.length <= 1_000_030);
      if (pass === 0) firstMemory = memory.heapUsed + memory.arrayBuffers;
      else if (global.gc) assert.ok(memory.heapUsed + memory.arrayBuffers < firstMemory + 64 * 1048576);
    }
    const buffer = terminal.buffer.active;
    assert.equal(buffer.length, 1_000_030);
    assert.match(buffer.getLine(buffer.length - 2).translateToString(true), /^packet 2999999 /);
    assert.match(buffer.getLine(0).translateToString(true), /^packet 1999971 /);
  } finally {
    compact.dispose();
    terminal.dispose();
  }
});
