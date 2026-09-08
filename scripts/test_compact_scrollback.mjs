import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';

const require = createRequire(import.meta.url);
const { Terminal } = require('../SerialMonitor.WinUI/Assets/xterm/xterm.js');
const { install, pack, unpack } = require('../SerialMonitor.WinUI/Assets/xterm/compact-scrollback.js');
const write = (terminal, text) => new Promise(resolve => terminal.write(text, resolve));

test('xterm dependency is pinned to the cell layout validated by these tests', () => {
  const source = readFileSync(new URL('../SerialMonitor.WinUI/Assets/xterm/xterm.js', import.meta.url));
  assert.equal(createHash('sha256').update(source).digest('hex'),
    '14903579ff54664cd72f8e8699e6961a6272c21863ec1c3b118cdc8af5d4a972',
    'An xterm update requires reviewing the compact adapter and rerunning the differential and memory tests.');
});

test('cell compression round-trips all uint32 bits, long runs and incompressible data', () => {
  let seed = 431;
  for (const cols of [0, 1, 80, 120, 512, 4096]) {
    const data = new Uint32Array(cols * 3);
    for (let i = 0; i < data.length; i++) {
      seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0;
      data[i] = seed;
    }
    assert.deepEqual(unpack(pack(data)), data);
    assert.ok(pack(data).byteLength <= data.byteLength + 1);
    data.fill(0xffffffff);
    assert.deepEqual(unpack(pack(data)), data);
  }
});

function compare(actual, expected) {
  for (const name of ['normal', 'alt']) {
    const a = actual._core._bufferService.buffers[name];
    const b = expected._core._bufferService.buffers[name];
    assert.equal(a.lines.length, b.lines.length, name);
    for (const key of ['x', 'y', 'ybase', 'ydisp']) assert.equal(a[key], b[key], key);
    for (let i = 0; i < a.lines.length; i++) {
      const left = a.lines.get(i), right = b.lines.get(i);
      assert.equal(left.length, right.length, `width row ${i}`);
      assert.equal(left.isWrapped, right.isWrapped, `wrap row ${i}`);
      assert.deepEqual(left._data, right._data, `cells row ${i}`);
      assert.deepEqual(left._combined, right._combined, `combined row ${i}`);
      assert.deepEqual(left._extendedAttrs, right._extendedAttrs, `attributes row ${i}`);
      assert.equal(left.translateToString(true), right.translateToString(true), `text row ${i}`);
    }
  }
}

test('compact history matches native xterm through rollover, Unicode, reflow, edits, alternate screen and clear', async () => {
  const options = { cols: 80, rows: 20, scrollback: 1200, allowProposedApi: true };
  const actual = new Terminal(options), expected = new Terminal(options);
  // Small cache forces eviction during reflow and scans.
  const compact = install(actual, { hotRows: 8, cacheRows: 8, onError(error) { throw error; } });
  async function both(text) {
    await Promise.all([write(actual, text), write(expected, text)]);
    compact.compact(Infinity, Infinity);
  }
  try {
    for (let pass = 0; pass < 3; pass++) {
      await both(Array.from({ length: 900 }, (_, i) =>
        `\x1b[38;2;${i % 256};128;64m${pass}:${i} 한글 中文 😀 e\u0301\t` +
        `\x1b[4:3m${'abc '.repeat(i % 40)}\x1b[0m\r\n`).join(''));
      compare(actual, expected);
      actual.resize(37, 25); expected.resize(37, 25);
      compact.compact(Infinity, Infinity);
      compare(actual, expected);
      actual.resize(132, 15); expected.resize(132, 15);
      compact.compact(Infinity, Infinity);
      compare(actual, expected);
      await both('\x1b[H\x1b[2Linsert\x1b[3P\x1b[4@\x1b[2M\x1b[0J');
      compare(actual, expected);
    }
    await both('\x1b[?1049hAlternate\r\n\x1b[31mred\x1b[0m\x1b[?1049l');
    compare(actual, expected);
    actual.options.scrollback = 100; expected.options.scrollback = 100;
    compact.compact(Infinity, Infinity);
    compare(actual, expected);
    actual.options.scrollback = 1800; expected.options.scrollback = 1800;
    await both('after capacity change\r\n'.repeat(500));
    compare(actual, expected);
    actual.clear(); expected.clear();
    await both('after clear\r\n'.repeat(250));
    compare(actual, expected);
    actual.reset(); expected.reset();
    await both('after reset\r\n'.repeat(250));
    compare(actual, expected);
  } finally {
    compact.dispose(); actual.dispose(); expected.dispose();
  }
});

test('reading and copying old history keeps decompressed storage bounded', async () => {
  const terminal = new Terminal({ cols: 120, rows: 20, scrollback: 10_000 });
  const compact = install(terminal, { hotRows: 16, cacheRows: 32, onError(error) { throw error; } });
  try {
    await write(terminal, 'retained history with color \x1b[31mred\x1b[0m\r\n'.repeat(10_100));
    compact.compact(Infinity, Infinity);
    const lines = terminal._core._bufferService.buffers.normal.lines;
    for (let pass = 0; pass < 2; pass++) {
      for (let i = 0; i < lines.length - 20; i++) {
        assert.match(lines.get(i).translateToString(true), /retained history with color red/);
      }
      let dense = 0, packedBytes = 0;
      for (let i = 0; i < lines.length; i++) {
        const line = lines.get(i);
        if (line._denseCells) dense++;
        packedBytes += line._packedCells?.byteLength ?? 0;
      }
      assert.ok(dense <= 32);
      assert.ok(packedBytes < 10_000 * 150);
    }
  } finally {
    compact.dispose(); terminal.dispose();
  }
});

test('a background compaction failure is reported once and preserves the original row', async () => {
  const terminal = new Terminal({ cols: 80, rows: 20, scrollback: 1000 });
  const errors = [];
  const compact = install(terminal, { onError: error => errors.push(error.message) });
  try {
    await write(terminal, 'original row\r\n'.repeat(800));
    const lines = terminal._core._bufferService.buffers.normal.lines;
    const first = lines.get(0), data = first._data;
    Object.defineProperty(first, '_data', { configurable: true, get() { throw new Error('simulated compression error'); } });
    compact.compact(0); // Exercise the timer callback, outside the write try/catch.
    await new Promise(resolve => setTimeout(resolve, 30));
    assert.deepEqual(errors, ['simulated compression error']);
    assert.equal(lines.get(0), first);
    Object.defineProperty(first, '_data', { configurable: true, writable: true, value: data });
    compact.compact();
    assert.equal(errors.length, 1);
    await write(terminal, 'still receiving\r\n');
    assert.equal(lines.get(lines.length - 2).translateToString(true), 'still receiving');
  } finally {
    compact.dispose(); terminal.dispose();
  }
});
