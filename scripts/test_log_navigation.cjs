'use strict';

const assert = require('node:assert/strict');
const { create } = require('../SerialMonitor.WinUI/Assets/xterm/log-navigation.js');

const local = (year, month, day, hour = 0, minute = 0, second = 0, millisecond = 0) => new Date(year, month - 1, day, hour, minute, second, millisecond).getTime();
const epoch = local(2026, 10, 28, 14);
function fixture(timestamps, ordered = true) {
  let revision = 0;
  let nextId = timestamps.length + 1;
  const rows = timestamps.map((timestamp, index) => ({ id: String(index + 1), timestamp }));
  let reads = 0;
  const source = { count: () => rows.length, at: index => { reads++; return rows[index]; }, timeOrder: () => ordered ? 'ascending' : 'unordered', revision: () => revision };
  return {
    engine: create(source), rows, source,
    append(timestamp) { rows.push({ id: String(nextId++), timestamp }); revision++; },
    evict(amount) { rows.splice(0, amount); revision++; },
    reset(timestamps) { rows.splice(0, rows.length, ...timestamps.map(timestamp => ({ id: String(nextId++), timestamp }))); revision++; },
    get reads() { return reads; }
  };
}

let checks = 0;
function test(name, action) { action(); checks++; console.log(`PASS ${name}`); }

test('line and percentage coordinates refer to retained logical logs', () => {
  const { engine } = fixture([epoch, epoch + 1000, epoch + 2000, epoch + 3000, epoch + 4000]);
  assert.equal(engine.resolve('#1', 3).index, 0);
  assert.equal(engine.resolve('#5', 0).index, 4);
  assert.equal(engine.resolve('0%', 0).index, 0);
  assert.equal(engine.resolve('50%', 0).index, 2);
  assert.equal(engine.resolve('100%', 0).index, 4);
  assert.equal(engine.resolve('#0', 0).error, 'out-of-range');
  assert.equal(engine.resolve('#6', 0).error, 'out-of-range');
  assert.equal(engine.resolve('#2.5', 0).error, 'invalid-line');
  assert.equal(engine.resolve('101%', 0).error, 'out-of-range');
  assert.equal(engine.resolve('a%', 0).error, 'invalid-percent');
});

test('absolute times use the first log at or after the requested time', () => {
  const { engine } = fixture([epoch, epoch + 1000, epoch + 1000, epoch + 2000]);
  assert.equal(engine.resolve('14:00:01', 0).index, 1);
  assert.equal(engine.resolve('14:00:00.500', 0).index, 1);
  assert.equal(engine.resolve('2026-10-28 14:00:02', 0).index, 3);
  assert.equal(engine.resolve('2026-10-28T14:00:01.1', 0).index, 3);
  assert.equal(engine.resolve('14:00:03', 0).error, 'out-of-range');
  assert.equal(engine.resolve('13:59:59', 0).error, 'out-of-range');
  assert.equal(engine.indexAtTime(epoch + 500), 1);
  assert.equal(engine.indexAtTime(epoch + 2001), -1);
});

test('fractional graph queries choose the first following integer timestamp', () => {
  const ordered = fixture([epoch, epoch + 1, epoch + 2]).engine;
  assert.equal(ordered.indexAtTime(epoch + 0.25), 1);
  assert.equal(ordered.indexAtTime(epoch + 1.75), 2);
  assert.equal(ordered.indexAtTime(epoch + 2.25), -1);
  assert.equal(ordered.indexAtTime(NaN), -1);
  assert.equal(ordered.indexAtTime(Infinity), -1);
  assert.equal(ordered.indexAtTime(null), -1);
  assert.equal(ordered.indexAtTime(Number.MAX_SAFE_INTEGER + 1), -1);
  const unordered = fixture([epoch + 1, null, epoch, epoch + 2], false).engine;
  assert.equal(unordered.indexAtTime(epoch + 0.25), 0);
  assert.equal(unordered.indexAtTime(epoch + 1.25), 3);
});

test('coarse absolute input includes the oldest retained fractional second or minute', () => {
  const first = local(2026, 10, 28, 14, 32, 10, 123);
  const engine = fixture([first, first + 1000, first + 30000]).engine;
  assert.equal(engine.resolve('14:32:10', 0).index, 0);
  assert.equal(engine.resolve('2026-10-28 14:32:10', 0).index, 0);
  assert.equal(engine.resolve('14:32', 0).index, 0);
  assert.equal(engine.resolve('2026-10-28 14:32', 0).index, 0);
  assert.equal(engine.resolve('14:32:09', 0).error, 'out-of-range');
  assert.equal(engine.resolve('14:31', 0).error, 'out-of-range');
  assert.equal(engine.resolve('14:32:10.000', 0).error, 'out-of-range');
  assert.equal(engine.resolve('14:32:10.122', 0).error, 'out-of-range');
  assert.equal(engine.resolve('14:32:10.123', 0).index, 0);
  assert.equal(engine.resolve('-1ms', 0).error, 'out-of-range');
  const boundary = fixture([local(2026, 10, 28, 14, 33, 0, 0)]).engine;
  assert.equal(boundary.resolve('14:32', 0).error, 'out-of-range');
  assert.equal(boundary.resolve('14:32:59', 0).error, 'out-of-range');
});

test('ID anchors survive ordinal movement and expire only when evicted', () => {
  const data = fixture([epoch, epoch + 1, epoch + 2, epoch + 3]);
  assert.equal(data.engine.indexById('3'), 2);
  assert.equal(data.engine.indexById(3), 2);
  assert.equal(data.engine.indexById(null), -1);
  assert.equal(data.engine.indexById(undefined), -1);
  assert.equal(data.engine.indexById('invalid'), -1);
  assert.equal(data.engine.indexById('3.5'), -1);
  data.evict(2);
  data.append(epoch + 4);
  assert.equal(data.engine.indexById('1'), -1);
  assert.equal(data.engine.indexById('3'), 0);
  assert.equal(data.engine.indexById('4'), 1);
  assert.equal(data.engine.indexById('5'), 2);
  assert.equal(data.engine.indexById('6'), -1);
  data.evict(3);
  assert.equal(data.engine.indexById('3'), -1);
});

test('relative durations use the current log rather than wall clock or latest RX', () => {
  const { engine } = fixture(Array.from({ length: 601 }, (_, index) => epoch + index * 1000));
  assert.equal(engine.resolve('-5m', 300).index, 0);
  assert.equal(engine.resolve('+30s', 300).index, 330);
  assert.equal(engine.resolve('+0.5m', 300).index, 330);
  assert.equal(engine.resolve('+1ms', 300).index, 301);
  assert.equal(engine.resolve('-1h', 300).error, 'out-of-range');
  assert.equal(engine.resolve('+1d', 300).error, 'out-of-range');
  assert.equal(engine.resolve('+1s', -1).error, 'no-current-time');
  assert.equal(engine.resolve('5m', 0).error, 'invalid-input');
});

test('calendar input rejects normalization and requires dates for multi-day history', () => {
  const { engine } = fixture([local(2026, 10, 27, 23, 59), local(2026, 10, 28, 0, 1)]);
  assert.equal(engine.resolve('00:00', 0).error, 'ambiguous-time');
  assert.equal(engine.resolve('00:00', 0).ambiguous, true);
  assert.equal(engine.resolve('2026-10-28 00:00', 0).index, 1);
  assert.equal(engine.resolve('2026-02-30 00:00', 0).error, 'invalid-date');
  assert.equal(engine.resolve('2026-10-28 24:00', 0).error, 'invalid-date');
  assert.equal(engine.resolve('2026-10-28 00:61', 0).error, 'invalid-date');
  assert.equal(engine.resolve('2026-10-28 00:00:61', 0).error, 'invalid-date');
  assert.equal(engine.resolve('tomorrow', 0).error, 'invalid-input');
});

test('local DST gaps reject nonexistent times and relative moves use elapsed time', () => {
  const originalTimeZone = process.env.TZ;
  const originalResolvedTimeZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
  try {
    process.env.TZ = 'America/New_York';
    const before = local(2026, 3, 8, 1, 30);
    const after = local(2026, 3, 8, 3, 30);
    const spring = fixture([before, after]).engine;
    assert.equal(after - before, 3600000);
    assert.equal(spring.resolve('2026-03-08 02:30', 0).error, 'invalid-date');
    assert.equal(spring.resolve('+1h', 0).index, 1);
    assert.equal(spring.resolve('2026-03-08 03:30', 0).index, 1);
    const firstOccurrence = local(2026, 11, 1, 1, 30);
    const autumn = fixture([firstOccurrence, firstOccurrence + 3600000]).engine;
    assert.equal(autumn.resolve('2026-11-01 01:30', 1).index, 0);
    assert.equal(autumn.resolve('+1h', 0).index, 1);
  } finally {
    // Windows Node does not consistently restore the OS zone on deleting TZ.
    process.env.TZ = originalTimeZone || originalResolvedTimeZone;
  }
});

test('empty, missing timestamps, and malformed input return actionable errors', () => {
  assert.equal(fixture([]).engine.resolve('#1', 0).error, 'no-logs');
  assert.equal(fixture([]).engine.resolve(' ', 0).error, 'empty-input');
  const missing = fixture([null, null], false).engine;
  assert.equal(missing.resolve('14:00', 0).error, 'no-timestamps');
  assert.equal(missing.resolve('+1s', 0).error, 'no-current-time');
  assert.equal(missing.resolve('#2', 0).index, 1);
  assert.equal(missing.histogram(100).count, 0);
  assert.equal(missing.indexAtTime(epoch), -1);
  assert.equal(missing.range().startTime, null);
});

test('dense histogram includes duplicate timestamps and idle gaps exactly', () => {
  const { engine } = fixture([epoch, epoch, epoch + 1, epoch + 1000, epoch + 1000]);
  const graph = engine.histogram(1001);
  assert.equal(graph.bins.length, 1001);
  assert.equal(graph.bins[0], 2);
  assert.equal(graph.bins[1], 1);
  assert.equal(graph.bins[500], 0);
  assert.equal(graph.bins[1000], 2);
  assert.equal(graph.count, 5);
  assert.equal(graph.bins.reduce((a, b) => a + b, 0), 5);
  assert.equal(graph.peak, 2);
  const zoom = engine.histogram(10, 2, 3);
  assert.equal(zoom.count, 2);
  assert.equal(zoom.startTime, epoch + 1);
  assert.equal(zoom.endTime, epoch + 1000);
  assert.equal(engine.histogram(100000).bins.length, 2048);
});

test('time-range histogram preserves exact zoom bounds and includes duplicate endpoints', () => {
  const { engine } = fixture([epoch, epoch + 1000, epoch + 1000, epoch + 1000, epoch + 2000]);
  const clipped = engine.histogramRange(100, epoch + 100, epoch + 1000);
  assert.equal(clipped.startTime, epoch + 100);
  assert.equal(clipped.endTime, epoch + 1000);
  assert.equal(clipped.count, 3);
  assert.equal(clipped.bins.reduce((a, b) => a + b, 0), 3);
  assert.equal(clipped.startIndex, 1);
  assert.equal(clipped.endIndex, 3);
  const between = engine.histogramRange(100, epoch, epoch + 1500);
  assert.equal(between.endTime, epoch + 1500);
  assert.equal(between.count, 4, 'a sample after the requested end is excluded');
  const gap = engine.histogramRange(100, epoch + 200, epoch + 800);
  assert.equal(gap.startTime, epoch + 200);
  assert.equal(gap.endTime, epoch + 800);
  assert.equal(gap.count, 0);
  assert.equal(gap.peak, 0);
  assert.ok(gap.bins.every(value => value === 0));
});

test('time-range histogram clamps partial boundaries and resets stale disjoint zooms', () => {
  const { engine } = fixture([epoch, epoch + 1000, epoch + 2000]);
  const before = engine.histogramRange(10, epoch - 1000, epoch + 1000);
  assert.equal(before.startTime, epoch);
  assert.equal(before.endTime, epoch + 1000);
  assert.equal(before.count, 2);
  const after = engine.histogramRange(10, epoch + 1000, epoch + 3000);
  assert.equal(after.endTime, epoch + 2000);
  assert.equal(after.count, 2);
  for (const [start, end] of [[epoch + 3000, epoch + 4000], [epoch - 2000, epoch - 1000], [epoch + 1000, epoch]]) {
    const reset = engine.histogramRange(10, start, end);
    assert.equal(reset.startTime, epoch);
    assert.equal(reset.endTime, epoch + 2000);
    assert.equal(reset.count, 3);
  }
  const missing = fixture([null, null], false).engine.histogramRange(100, epoch, epoch + 1000);
  assert.equal(missing.count, 0);
  assert.equal(missing.startTime, null);
  assert.equal(missing.endTime, null);
});

test('unordered time-range histogram filters every acquisition segment by timestamp', () => {
  const { engine } = fixture([epoch, epoch + 3000, epoch + 1000, null, epoch + 2000, epoch + 1000, epoch + 4000], false);
  const graph = engine.histogramRange(10, epoch + 500, epoch + 2000);
  assert.equal(graph.ordered, false);
  assert.equal(graph.count, 3);
  assert.equal(graph.startTime, epoch + 500);
  assert.equal(graph.endTime, epoch + 2000);
  assert.equal(graph.startIndex, 2);
  assert.equal(graph.endIndex, 5);
  assert.equal(graph.bins.reduce((a, b) => a + b, 0), 3);
  assert.equal(graph.bins[3], 2);
  assert.equal(graph.bins[9], 1);
});

test('clock regressions and null metadata are counted without fabricating order', () => {
  const data = fixture([epoch + 2000, null, epoch, epoch + 1000, epoch + 3000], false);
  const graph = data.engine.histogram(4);
  assert.equal(graph.ordered, false);
  assert.deepEqual(graph.bins, [1, 1, 1, 1]);
  assert.equal(graph.count, 4);
  assert.equal(data.engine.range().count, 5);
  assert.equal(data.engine.range().validCount, 4);
  assert.equal(data.engine.indexAtTime(epoch + 500), 0, 'first acquisition-order log at or after the target');
  assert.equal(data.engine.resolve('14:00:00.500', 3).index, 0);
  assert.equal(data.engine.indexAtTime(epoch + 3001), -1);
});

test('unordered block cache survives retention eviction, appends, and resets', () => {
  const timestamps = Array.from({ length: 5000 }, (_, index) => epoch + Math.floor(index / 10));
  const data = fixture(timestamps, false);
  assert.equal(data.engine.histogram(100).count, 5000);
  const readsAfterFirst = data.reads;
  data.engine.histogram(100);
  assert.equal(data.reads, readsAfterFirst, 'unchanged chart is cached');
  data.evict(256);
  for (let index = 0; index < 50; index++) data.append(epoch + 500 + index);
  const before = data.reads;
  const graph = data.engine.histogram(20);
  assert.equal(graph.count, 4794);
  assert.equal(graph.bins.reduce((sum, value) => sum + value, 0), 4794);
  assert.ok(data.reads - before < 3000, 'updating a rolling chart reuses complete blocks');
  assert.equal(data.engine.range().startTime, timestamps[256]);
  data.reset([epoch + 50, null, epoch + 10]);
  assert.equal(data.engine.histogram(10).count, 2);
  assert.equal(data.engine.range().startTime, epoch + 10);
  assert.equal(data.engine.indexAtTime(epoch + 20), 0);
});

test('million-line ordered graph and time lookup have bounded metadata reads', () => {
  let reads = 0;
  const engine = create({ count: () => 1000000, at: index => { reads++; return { id: String(index + 1), timestamp: epoch + index }; }, timeOrder: () => 'ascending', revision: () => 1 });
  const started = performance.now();
  const graph = engine.histogram(800);
  const milliseconds = performance.now() - started;
  assert.equal(graph.count, 1000000);
  assert.equal(graph.bins.reduce((a, b) => a + b, 0), 1000000);
  assert.ok(reads < 17000, `${reads} metadata reads should scale with bins, not history size`);
  const before = reads;
  assert.equal(engine.indexAtTime(epoch + 678901), 678901);
  assert.ok(reads - before <= 20);
  const beforeId = reads;
  assert.equal(engine.indexById('678902'), 678901);
  assert.ok(reads - beforeId <= 21, 'ID reanchoring must use binary search');
  const beforeCached = reads;
  engine.histogram(800);
  assert.equal(reads, beforeCached);
  const beforeZoom = reads;
  const zoom = engine.histogramRange(800, epoch + 400000, epoch + 600000);
  assert.equal(zoom.count, 200001);
  assert.ok(reads - beforeZoom < 17000);
  console.log(`  1,000,000 lines, 800 bins: ${milliseconds.toFixed(2)} ms, ${reads} metadata reads`);
});

test('histogram correctness across randomized disorder and rolling eviction', () => {
  let random = 42;
  const next = () => { random = (Math.imul(random, 1664525) + 1013904223) >>> 0; return random; };
  const data = fixture(Array.from({ length: 2049 }, () => next() % 13 ? epoch + next() % 10000 : null), false);
  for (let round = 0; round < 8; round++) {
    const start = round * 7;
    const end = data.rows.length - 1 - round;
    const graph = data.engine.histogram(97, start, end);
    const values = data.rows.slice(start, end + 1).map(row => row.timestamp).filter(Number.isFinite);
    const min = Math.min(...values);
    const max = Math.max(...values);
    const expected = new Array(97).fill(0);
    for (const value of values) expected[Math.min(96, Math.floor((value - min) / Math.max(1, max - min + 1) * 97))]++;
    assert.deepEqual(graph.bins, expected);
    const timeStart = epoch + 2500 + round * 50;
    const timeEnd = epoch + 7000 - round * 30;
    const timeGraph = data.engine.histogramRange(97, timeStart, timeEnd);
    const timeExpected = new Array(97).fill(0);
    for (const row of data.rows) {
      if (!Number.isFinite(row.timestamp) || row.timestamp < timeStart || row.timestamp > timeEnd) continue;
      timeExpected[Math.min(96, Math.floor((row.timestamp - timeStart) / (timeEnd - timeStart + 1) * 97))]++;
    }
    assert.deepEqual(timeGraph.bins, timeExpected);
    data.evict(29);
    for (let append = 0; append < 31; append++) data.append(next() % 13 ? epoch + next() % 10000 : null);
  }
});

console.log(`${checks} navigation engine checks passed.`);
