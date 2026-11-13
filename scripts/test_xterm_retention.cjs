// No browser or desktop automation: run the shipped parser/bridge in Node.
// Run: node --test scripts/test_xterm_retention.cjs
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { readFileSync } = require('node:fs');
const vm = require('node:vm');
const { Terminal } = require('../SerialMonitor.WinUI/Assets/xterm/xterm.js');
const { create } = require('../SerialMonitor.WinUI/Assets/xterm/log-navigation-terminal.js');
const { install } = require('../SerialMonitor.WinUI/Assets/xterm/compact-scrollback.js');
const html = readFileSync(require.resolve('../SerialMonitor.WinUI/Assets/xterm/index.html'), 'utf8');
function section(start, end) {
  const from = html.indexOf(start), to = html.indexOf(end, from);
  assert.ok(from >= 0 && to > from, `Missing bridge section: ${start}`);
  return html.slice(from, to);
}
function fixture({ cols = 40, rows = 8, scrollback = 2000, compact = false } = {}) {
  const terminal = new Terminal({ cols, rows, scrollback });
  const acks = new Map(), errors = [];
  const compression = compact
    ? install(terminal, { hotRows: 0, cacheRows: 8, onError: error => errors.push(String(error)) })
    : { compact() {}, dispose() {} };
  let request = 0, resets = 0;
  const reset = terminal.reset.bind(terminal);
  terminal.reset = () => { resets++; reset(); };
  const context = vm.createContext({
    terminal, setTimeout, clearTimeout, console, directInputSessionId: 0, pendingLocalEchoChunks: 0,
    compactScrollback: compression,
    invalidateLogicalLineStartRows() {}, queueHexSelectionHintUpdate() {}, hideHexSelectionHint() {},
    clearNavigationHighlight() {},
    window: {
      requestAnimationFrame: callback => setTimeout(callback, 0),
      chrome: { webview: { postMessage(message) {
        if (message.type === 'xtermAppendCompleted') acks.get(message.requestId)?.(message);
        else if (message.error) errors.push(message.error);
      } } }
    }
  });
  vm.runInContext(
    section('    const appendQueue = [];', '    const contextMenu =') +
    section('    function resetParsedLogLineIds(', '    function normalizeLogText(') +
    section('    function normalizeLogText(', '    function isAtBottom()') +
    section('    window.serialMonitorAppendLog =', '    window.serialMonitorScrollToBottom =') +
    `globalThis.raw = {
      count: () => parsedLogLineIdCount,
      at: index => { const i = (parsedLogLineIdHead + index) % parsedLogLineIds.length;
        return { id: parsedLogLineIds[i], row: parsedLogLineRows[i] }; },
      setRow: (index, row) => { parsedLogLineRows[(parsedLogLineIdHead + index) % parsedLogLineIds.length] = row; },
      findId: id => findParsedLogLineIdIndex(id)
    };
    terminal.onLineFeed(() => { lastParsedLogLineIsOpen = false; });`, context);
  const adapter = create(terminal, context.raw, {
    isLastLineOpen: () => vm.runInContext('lastParsedLogLineIsOpen', context),
    onError: error => errors.push(String(error))
  });
  context.adapter = adapter;
  vm.runInContext('navigationRows = adapter;', context);
  async function append(text) {
    const id = ++request;
    const message = await new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error('Append acknowledgement timed out')), 10000);
      acks.set(id, value => { clearTimeout(timer); resolve(value); });
      assert.equal(context.window.serialMonitorAppendLog(text, false, id), true);
    });
    acks.delete(id);
    assert.equal(message.success, true, message.error);
    assert.deepEqual(errors, []);
  }
  const records = (start, end, payload = ' 한글 \x1b[31mRED\x1b[0m') =>
    Array.from({ length: end - start + 1 }, (_, i) => `\x1b]777;${start + i}\x07ID${start + i}${payload}\r\n`).join('');
  const boundary = id => `\x1b]779;${id}\x07`;
  const first = () => terminal.buffer.active.getLine(0).translateToString(true);
  return { terminal, adapter, raw: context.raw, append, records, boundary, first,
    resets: () => resets, close() { adapter.dispose(); compression.dispose(); terminal.dispose(); } };
}

test('retention drops cells and metadata without copying the surviving suffix or resetting xterm', async () => {
  const f = fixture();
  try {
    await f.append(f.records(1, 1000));
    const buffer = f.terminal._core._bufferService.buffer;
    const evicted = buffer.lines.get(0);
    const retained = buffer.lines.get(f.adapter.rowsById('950').start);
    f.terminal.scrollToLine(f.adapter.rowsById('960').start);
    const viewportText = f.terminal.buffer.active.getLine(buffer.ydisp).translateToString(true);
    await f.append(f.boundary(900));
    assert.match(f.first(), /^ID900 한글 RED$/);
    assert.equal(f.raw.count(), 101);
    assert.equal(f.raw.at(0).id, '900');
    assert.equal(buffer.lines.get(f.adapter.rowsById('950').start), retained);
    assert.ok(!buffer.lines._array.includes(evicted), 'evicted cell references must be released');
    assert.equal(f.terminal.buffer.active.getLine(buffer.ydisp).translateToString(true), viewportText);
    assert.equal(f.resets(), 0);
    await f.append(f.records(1001, 1010) + f.boundary(910));
    assert.match(f.first(), /^ID910 /);
    assert.equal(f.adapter.indexById('1010'), 100);
  } finally { f.close(); }
});

test('trimming into the live viewport preserves cursor position and pads blank rows', async () => {
  const f = fixture({ rows: 10 });
  try {
    await f.append(f.records(1, 4) + f.boundary(4));
    assert.match(f.first(), /^ID4 /);
    assert.equal(f.terminal.buffer.active.length, 10);
    assert.equal(f.terminal.buffer.active.cursorY, 1);
    await f.append(f.records(5, 5));
    assert.match(f.terminal.buffer.active.getLine(1).translateToString(true), /^ID5 /);
    assert.equal(f.raw.count(), 2);
  } finally { f.close(); }
});

test('evicting an unfinished row clears its cells before the next record starts', async () => {
  const f = fixture();
  try {
    await f.append('\x1b]777;1\x07OLD_UNFINISHED');
    await f.append(f.boundary(2));
    assert.equal(f.first(), '');
    assert.equal(f.terminal.buffer.active.cursorX, 0);
    assert.equal(f.raw.count(), 0);
    await f.append(f.records(2, 2));
    assert.match(f.first(), /^ID2 /);
    assert.equal(f.adapter.indexById('2'), 0);
  } finally { f.close(); }
});

test('same-batch eviction and transport-split boundaries retain only the latest suffix', async () => {
  const f = fixture();
  try {
    await f.append(f.records(1, 100) + f.boundary(90) + f.records(101, 200) + '\x1b]779;1');
    await f.append('50\x07');
    assert.match(f.first(), /^ID150 /);
    assert.equal(f.raw.count(), 51);
    assert.equal(f.resets(), 0);
  } finally { f.close(); }
});

test('retention waits for width reflow and removes complete wrapped Unicode records', async () => {
  const f = fixture({ cols: 80, rows: 5, scrollback: 1000 });
  try {
    await f.append(f.records(1, 200, ' 한글\te\u0301 [] ' + 'payload '.repeat(5)));
    f.terminal.resize(25, 7);
    await f.append(f.boundary(180));
    assert.match(f.first(), /^ID180 /);
    for (let id = 180; id <= 200; id++) {
      const row = f.adapter.rowAt(f.adapter.indexById(String(id)));
      assert.notEqual(row, null);
      assert.match(f.terminal.buffer.active.getLine(row).translateToString(true), new RegExp(`^ID${id} `));
    }
    f.terminal.resize(100, 12);
    await f.append(f.records(201, 201) + f.boundary(190));
    assert.match(f.first(), /^ID190 /);
    assert.equal(f.resets(), 0);
  } finally { f.close(); }
});

test('a reset cancels a retention operation waiting for reflow', async () => {
  const f = fixture({ cols: 80, rows: 5, scrollback: 10000 });
  try {
    await f.append(f.records(1, 5000));
    f.terminal.resize(30, 5);
    assert.equal(f.adapter.isBusy(), true);
    const pending = f.adapter.trimBefore('4900');
    f.terminal.reset();
    f.adapter.reset();
    assert.equal(await pending, false);
    assert.equal(f.first(), '');
    assert.equal(f.terminal.buffer.active.cursorX, 0);
  } finally { f.close(); }
});

for (const compact of [false, true]) {
  test(`randomized retention preserves Unicode, row anchors and subsequent writes (compact=${compact})`, async () => {
    const f = fixture({ scrollback: 50000, compact });
    let seed = 0x12345678, next = 1, first = 1;
    const random = max => { seed = (Math.imul(seed, 1664525) + 1013904223) >>> 0; return seed % max; };
    try {
      for (let pass = 0; pass < 120; pass++) {
        const count = 1 + random(20);
        const payload = ' 한글🙂 e\u0301\t\x1b[31mRED\x1b[0m ' + 'payload '.repeat(random(30));
        let text = f.records(next, next + count - 1, payload);
        next += count;
        first = Math.max(first, next - random(101));
        text += f.boundary(first);
        if (pass % 10 === 0) f.terminal.resize(10 + random(90), 5 + random(10));
        await f.append(text);
        assert.equal(f.raw.count(), next - first, `metadata count at pass ${pass}`);
        if (first < next) {
          assert.equal(f.raw.at(0).id, String(first));
          assert.match(f.first(), new RegExp(`^ID${first}( |$)`));
          for (let id = first; id < next; id++) {
            const index = f.adapter.indexById(String(id));
            assert.equal(index, id - first, `ID${id} at pass ${pass}`);
            const row = f.adapter.rowAt(index);
            assert.match(f.terminal.buffer.active.getLine(row).translateToString(true), new RegExp(`^ID${id}( |$)`));
          }
        } else assert.equal(f.first(), '');
      }
      assert.equal(f.resets(), 0);
    } finally { f.close(); }
  });
}
