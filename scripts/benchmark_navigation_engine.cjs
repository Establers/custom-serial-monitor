// Reproducible metadata-only navigation checks. Run: node --expose-gc scripts/benchmark_navigation_engine.cjs
// No browser, serial port, profile, UI or disk-log writer is started.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const vm = require('node:vm');
const { create } = require('../SerialMonitor.WinUI/Assets/xterm/log-navigation.js');

const epoch = 1793149200000;
const mib = value => value / 1048576;
function timing(action) { const start = performance.now(); const value = action(); return { milliseconds: performance.now() - start, value }; }
function distribution(values) {
  const sorted = [...values].sort((a, b) => a - b);
  return { medianMs: sorted[Math.floor(sorted.length / 2)], p95Ms: sorted[Math.min(sorted.length - 1, Math.floor(sorted.length * .95))], maxMs: sorted.at(-1) };
}
function sourceFixture(size, mode) {
  let offset = 0, revision = 0, reads = 0;
  const timestamp = ordinal => mode === 'equal' ? epoch : mode === 'clock-regression'
    ? epoch + ordinal * 10 - (ordinal >= Math.floor(size * .75) ? 1000 : 0) : epoch + ordinal * 10;
  const source = {
    count: () => size,
    revision: () => revision,
    timeOrder: () => mode === 'clock-regression' ? 'unordered' : 'ascending',
    at(index) {
      assert.ok(index >= 0 && index < size, `metadata index ${index} outside ${size}`);
      reads++;
      return { id: String(offset + index + 1), timestamp: timestamp(offset + index) };
    }
  };
  return { source, get reads() { return reads; }, get offset() { return offset; },
    advance(amount) { offset += amount; revision++; }, timestamp };
}

function engineCase(size, mode) {
  global.gc?.();
  const memoryBefore = process.memoryUsage();
  const fixture = sourceFixture(size, mode);
  const engine = create(fixture.source);
  const beforeCold = fixture.reads;
  const cold = timing(() => engine.range());
  const coldReads = fixture.reads - beforeCold;
  assert.equal(cold.value.count, size);
  if (mode === 'clock-regression') assert.ok(coldReads >= size && coldReads <= size + 32);
  else assert.equal(coldReads, 2, 'ordinary range lookup must read only the two boundary stamps');
  const queries = [], readsPerQuery = [];
  for (let index = 0; index < 1000; index++) {
    const reads = fixture.reads;
    const sample = timing(() => {
      const range = engine.range();
      const old = engine.indexAtTime(mode === 'equal' ? epoch : epoch + 5000);
      const id = engine.indexById(String(Math.floor(size * .001) + 1));
      return { range, old, id };
    });
    assert.equal(sample.value.old, mode === 'equal' ? 0 : 500);
    assert.equal(sample.value.id, Math.floor(size * .001));
    queries.push(sample.milliseconds); readsPerQuery.push(fixture.reads - reads);
  }
  const logReads = 2 * Math.ceil(Math.log2(size)) + 5;
  assert.ok(Math.max(...readsPerQuery) <= logReads, 'range/time/ID metadata reads must stay logarithmic');
  const rolling = [];
  let maxRollingReads = 0;
  if (mode === 'clock-regression') {
    for (let round = 0; round < 100; round++) {
      fixture.advance(500);
      const reads = fixture.reads;
      const sample = timing(() => engine.range());
      rolling.push(sample.milliseconds);
      maxRollingReads = Math.max(maxRollingReads, fixture.reads - reads);
      assert.equal(sample.value.count, size);
      assert.equal(engine.indexById(String(fixture.offset)), -1);
      assert.equal(engine.indexById(String(fixture.offset + 1)), 0);
    }
    assert.ok(maxRollingReads <= 1100, 'rolling eviction must reuse retained blocks instead of scanning history');
  }
  global.gc?.();
  const memoryAfter = process.memoryUsage();
  const retainedAuxiliaryMiB = mib(Math.max(0, memoryAfter.heapUsed - memoryBefore.heapUsed));
  assert.ok(retainedAuxiliaryMiB < 16, 'navigation summaries should not duplicate history-sized payload arrays');
  return { rows: size, mode, coldRangeMs: cold.milliseconds, coldMetadataReads: coldReads,
    queries: { ...distribution(queries), maxMetadataReads: Math.max(...readsPerQuery) },
    rolling: rolling.length ? { ...distribution(rolling), maxMetadataReads: maxRollingReads, appendedAndEvicted: 50000 } : null,
    retainedAuxiliaryMiB };
}

function metadataResizeCase(size) {
  const html = fs.readFileSync(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html'), 'utf8');
  const section = (start, end) => {
    const from = html.indexOf(start), to = html.indexOf(end, from);
    assert.ok(from >= 0 && to > from);
    return html.slice(from, to);
  };
  const terminal = { options: { scrollback: size }, rows: 30, parser: { registerOscHandler() {} } };
  const context = vm.createContext({ terminal, clearNavigationHighlight() {}, window: {} });
  vm.runInContext(section('    const appendQueue = [];', '    const contextMenu =') +
    section('    function resetParsedLogLineIds(', '    function normalizeLogText(') +
    'globalThis.metrics = () => ({ count: parsedLogLineIdCount, capacity: parsedLogLineIds.length, rowBytes: parsedLogLineRows.byteLength });', context);
  const populated = timing(() => { for (let index = 0; index < size; index++) context.appendParsedLogLineId(String(index + 1), epoch + index); });
  const resizeSamples = [];
  for (let sample = 0; sample < 5; sample++) {
    terminal.rows = sample % 2 ? 30 : 31;
    resizeSamples.push(timing(() => context.trimParsedLogLineIds()).milliseconds);
  }
  const stats = context.metrics();
  assert.equal(stats.count, size);
  assert.ok(stats.capacity <= size + Math.ceil((terminal.rows + 1) / 256) * 256);
  assert.equal(stats.rowBytes, stats.capacity * 8);
  for (let index = size; index < size + 10000; index++) context.appendParsedLogLineId(String(index + 1), epoch + index);
  const retained = context.metrics();
  assert.equal(retained.count, retained.capacity);
  assert.equal(retained.capacity, stats.capacity);
  return { rows: size, populateMetadataMs: populated.milliseconds, heightResizeMetadataCopy: distribution(resizeSamples),
    capacityAfter10000MoreRows: retained.capacity, physicalRowAnchorMiB: mib(retained.rowBytes) };
}

const report = {
  generatedAt: new Date().toISOString(),
  environment: { node: process.version, v8: process.versions.v8, platform: process.platform, arch: process.arch,
    cpu: os.cpus()[0]?.model, logicalProcessors: os.cpus().length, physicalMemoryGiB: os.totalmem() / 1073741824, explicitGc: !!global.gc },
  limitations: 'Synthetic metadata sources measure lookup/retention costs, not renderer, WebView transport, native picker layout, hardware UART or disk logging. Memory deltas exclude the existing history source. No timings are cross-machine guarantees.',
  engine: [], metadata: []
};
for (const rows of [100000, 1000000]) for (const mode of ['ordered', 'equal', 'clock-regression']) report.engine.push(engineCase(rows, mode));
for (const rows of [100000, 1000000]) report.metadata.push(metadataResizeCase(rows));
const output = path.resolve(__dirname, '../artifacts/navigation-performance/engine-benchmark.json');
fs.mkdirSync(path.dirname(output), { recursive: true });
fs.writeFileSync(output, JSON.stringify(report, null, 2));
console.log(JSON.stringify({ ...report, reportPath: output }, null, 2));
