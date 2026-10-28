// Last-corner benchmark for the shipped xterm width-reflow and compact history.
// Run: node --expose-gc scripts/benchmark_xterm_width_resize.cjs
// A supervising process stops the worker at 60 seconds, including synchronous resize work.
const fs = require('node:fs');
const path = require('node:path');
const output = path.resolve(__dirname, '../artifacts/navigation-performance/width-resize-benchmark.json');
fs.mkdirSync(path.dirname(output), { recursive: true });

if (!process.argv.includes('--worker')) {
  const { spawn } = require('node:child_process');
  const child = spawn(process.execPath, [...process.execArgv, __filename, '--worker'], { stdio: 'inherit', windowsHide: true });
  let timedOut = false;
  const timeout = setTimeout(() => { timedOut = true; child.kill(); }, 60000);
  child.on('exit', code => {
    clearTimeout(timeout);
    if (timedOut) {
      const report = fs.existsSync(output) ? JSON.parse(fs.readFileSync(output, 'utf8')) : {};
      report.timedOutAt60Seconds = true;
      fs.writeFileSync(output, JSON.stringify(report, null, 2));
      console.error('WIDTH BENCHMARK stopped at 60 seconds. Partial results:', output);
      process.exitCode = 2;
    } else process.exitCode = code || 0;
  });
} else {
  const assert = require('node:assert/strict');
  const os = require('node:os');
  const { Terminal } = require('../SerialMonitor.WinUI/Assets/xterm/xterm.js');
  const { install } = require('../SerialMonitor.WinUI/Assets/xterm/compact-scrollback.js');
  const terminal = new Terminal({ cols: 120, rows: 30, scrollback: 1000000 });
  const compact = install(terminal, { onError(error) { throw error; } });
  const report = {
    generatedAt: new Date().toISOString(), node: process.version, cpu: os.cpus()[0]?.model,
    fixtureRows: 1000000, fixtureColumns: 120, terminalRows: 30, gcAvailable: !!global.gc,
    limitation: 'Headless shipped xterm plus lossless compact history. No browser renderer, native picker, UART or disk-log work.',
    phase: 'loading', resizes: []
  };
  function save() { fs.writeFileSync(output, JSON.stringify(report, null, 2)); }
  function memory() {
    global.gc?.();
    const value = process.memoryUsage();
    return { heapMiB: value.heapUsed / 1048576, arrayBuffersMiB: value.arrayBuffers / 1048576,
      liveJsAndBuffersMiB: (value.heapUsed + value.arrayBuffers) / 1048576, rssMiB: value.rss / 1048576 };
  }
  function cacheState() {
    const lines = terminal._core._bufferService.buffers.normal.lines;
    let archived = 0, inflated = 0;
    for (let index = 0; index < lines.length; index++) {
      const row = lines.get(index);
      if (Object.hasOwn(row, '_denseCells')) { archived++; if (row._denseCells) inflated++; }
    }
    return { physicalRows: lines.length, archivedRows: archived, inflatedArchiveRows: inflated, nativeRows: lines.length - archived };
  }
  const rowText = index => terminal.buffer.active.getLine(index).translateToString(true);
  const topText = () => rowText(terminal.buffer.active.viewportY);
  save();
  (async () => {
    const start = performance.now();
    for (let batch = 0; batch < 1000; batch++) {
      const text = Array.from({ length: 1000 }, (_, index) =>
        `packet ${String(batch * 1000 + index + 1).padStart(7, '0')} temperature=25.3 voltage=3.300 status=OK flag=0\r\n`).join('');
      await new Promise(resolve => terminal.write(text, resolve));
      compact.compact(Infinity, Infinity);
      if ((batch + 1) % 100 === 0) {
        report.loadedRows = (batch + 1) * 1000;
        report.loadElapsedMs = performance.now() - start;
        save();
        console.log(`Loaded ${report.loadedRows} rows in ${report.loadElapsedMs.toFixed(1)} ms`);
      }
    }
    report.baseline = { memory: memory(), cache: cacheState() };
    terminal.scrollToLine(500000);
    const anchor = topText();
    for (const columns of [100, 80, 120]) {
      report.phase = `resizing-to-${columns}`;
      save();
      const began = performance.now();
      terminal.resize(columns, 30);
      const resizeMs = performance.now() - began;
      const immediate = { memory: memory(), cache: cacheState() };
      const compactStarted = performance.now();
      compact.compact(Infinity, Infinity);
      const compactDrainMs = performance.now() - compactStarted;
      const final = { memory: memory(), cache: cacheState() };
      const after = topText();
      const result = { columns, resizeMs, compactDrainMs, immediate, final,
        anchorPreserved: after === anchor, viewportY: terminal.buffer.active.viewportY };
      report.resizes.push(result);
      save();
      console.log(JSON.stringify(result));
      assert.equal(after, anchor, 'all fixture logs fit at 80 columns, so width changes must retain the historical top log');
      assert.equal(final.cache.physicalRows, report.baseline.cache.physicalRows);
      assert.ok(final.cache.inflatedArchiveRows <= 512);
      for (const index of [0, 500000, 999999]) assert.match(rowText(index), new RegExp(`^packet ${String(index + 1).padStart(7, '0')} `));
    }
    report.phase = 'complete';
    report.elapsedMs = performance.now() - start;
    save();
    console.log('WIDTH BENCHMARK COMPLETE', JSON.stringify({ elapsedMs: report.elapsedMs, reportPath: output }));
  })().catch(error => { report.phase += '-failed'; report.error = error.stack; save(); console.error(error); process.exitCode = 1; })
    .finally(() => { compact.dispose(); terminal.dispose(); });
}
