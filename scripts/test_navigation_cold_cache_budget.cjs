// Shipped xterm/compact-history cache stress, without a browser or serial port.
// Run: node --expose-gc scripts/test_navigation_cold_cache_budget.cjs
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { Terminal } = require('../SerialMonitor.WinUI/Assets/xterm/xterm.js');
const { install } = require('../SerialMonitor.WinUI/Assets/xterm/compact-scrollback.js');

(async () => {
  const terminal = new Terminal({ cols: 120, rows: 30, scrollback: 100000 });
  const compact = install(terminal, { onError(error) { throw error; } });
  const count = 100000;
  let nextId = 1;
  const write = text => new Promise(resolve => terminal.write(text, resolve));
  const append = async amount => {
    for (let done = 0; done < amount; done += 1000) {
      const size = Math.min(1000, amount - done);
      await write(Array.from({ length: size }, () => `packet ${nextId++} payload=0123456789abcdef status=OK\r\n`).join(''));
      compact.compact(Infinity, Infinity);
    }
  };
  const memory = () => { global.gc?.(); const value = process.memoryUsage(); return (value.heapUsed + value.arrayBuffers) / 1048576; };
  const cells = () => {
    const lines = terminal._core._bufferService.buffers.normal.lines;
    let archived = 0, inflatedCold = 0;
    for (let index = 0; index < lines.length; index++) {
      const row = lines.get(index);
      if (Object.hasOwn(row, '_denseCells')) { archived++; if (row._denseCells) inflatedCold++; }
    }
    return { archived, inflatedCold, retainedPhysicalRows: lines.length };
  };
  const report = { node: process.version, fixtureRows: count, gcAvailable: !!global.gc,
    limitation: 'Headless shipped xterm cache/scroll retention; does not measure native rendering, UART or file I/O.', passes: [] };
  try {
    const started = performance.now();
    await append(count);
    report.loadMs = performance.now() - started;
    const initial = cells();
    assert.ok(initial.archived >= count - 330);
    const baseline = memory();
    report.baselineLiveJsAndBuffersMiB = baseline;
    let seed = 42;
    for (let pass = 0; pass < 3; pass++) {
      for (let query = 0; query < 2500; query++) {
        seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
        const row = seed % (count - 1000);
        const text = terminal.buffer.active.getLine(row).translateToString(true);
        assert.match(text, new RegExp(`^packet ${row + 1} `));
      }
      const state = cells();
      const retainedMiB = memory();
      assert.ok(state.inflatedCold <= 512, 'reading arbitrary old rows must retain at most the configured 512-row cold cache');
      if (global.gc) assert.ok(retainedMiB < baseline + 8, 'repeated old reads must not retain a second history-sized cell array');
      report.passes.push({ pass: pass + 1, oldRowsRead: (pass + 1) * 2500, ...state, liveJsAndBuffersMiB: retainedMiB });
    }
    terminal.scrollToLine(50000);
    const before = terminal.buffer.active.getLine(terminal.buffer.active.viewportY).translateToString(true);
    await append(10000);
    const after = terminal.buffer.active.getLine(terminal.buffer.active.viewportY).translateToString(true);
    assert.equal(after, before, 'RX plus retention trim must keep a surviving historical row anchored');
    assert.ok(terminal.buffer.active.length <= 100030);
    const final = cells();
    assert.ok(final.inflatedCold <= 512);
    report.historicalViewPreservedAcross10000Writes = { before, after, ...final };
    const output = path.resolve(__dirname, '../artifacts/navigation-performance/cold-cache-budget.json');
    fs.mkdirSync(path.dirname(output), { recursive: true });
    fs.writeFileSync(output, JSON.stringify(report, null, 2));
    console.log(JSON.stringify({ ...report, reportPath: output }, null, 2));
  } finally { compact.dispose(); terminal.dispose(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
