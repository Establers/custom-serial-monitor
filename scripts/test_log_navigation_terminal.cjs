// Run: node --test scripts/test_log_navigation_terminal.cjs
const assert = require('node:assert/strict');
const { test } = require('node:test');
const { Terminal } = require('../SerialMonitor.WinUI/Assets/xterm/xterm.js');
const { create } = require('../SerialMonitor.WinUI/Assets/xterm/log-navigation-terminal.js');

function fixture({ cols = 20, rows = 5, scrollback = 100 } = {}) {
  const terminal = new Terminal({ cols, rows, scrollback });
  const metadata = [];
  let open = false;
  let nextId = 1;
  const errors = [];
  const raw = {
    count: () => metadata.length,
    at: index => metadata[index],
    setRow: (index, row) => { metadata[index].row = row; },
    findId: id => metadata.findIndex(item => item.id === String(id))
  };
  const adapter = create(terminal, raw, { isLastLineOpen: () => open, onError: error => errors.push(error) });
  terminal.onLineFeed(() => { open = false; });
  terminal.parser.registerOscHandler(777, text => {
    metadata.push({ id: text, timestamp: Number(text), row: adapter.recordRow() });
    const limit = terminal.options.scrollback + Math.ceil((terminal.rows + 1) / 256) * 256;
    while (metadata.length > limit) metadata.shift();
    open = true;
    return true;
  });
  const write = text => new Promise(resolve => terminal.write(text, resolve));
  async function append(count, payload = 'abcdefghijklmnopqrstuvwxyz') {
    await write(Array.from({ length: count }, () => {
      const id = nextId++;
      return `\x1b]777;${id}\x07ID${id} ${payload}\r\n`;
    }).join(''));
  }
  async function ready() {
    const deadline = Date.now() + 5000;
    while (adapter.isBusy()) {
      assert.ok(Date.now() < deadline, 'row rebuild did not complete');
      await new Promise(resolve => setTimeout(resolve, 1));
    }
    assert.deepEqual(errors, []);
  }
  function checkRows(stride = 1) {
    const start = adapter.sourceStart();
    assert.ok(start < raw.count());
    for (let i = 0; i < raw.count() - start; i += stride) {
      const record = raw.at(start + i);
      const row = adapter.rowAt(i);
      assert.notEqual(row, null, `missing row for ID${record.id}`);
      assert.match(terminal.buffer.active.getLine(row).translateToString(true),
        new RegExp(`^ID${record.id}( |$)`), `wrong anchor for ID${record.id} at row ${row}`);
      assert.equal(adapter.indexById(record.id), i);
    }
  }
  return { terminal, metadata, adapter, raw, append, write, ready, checkRows,
    close() { adapter.dispose(); terminal.dispose(); } };
}

test('OSC row anchors survive physical scrollback trim and wrapped rows', async () => {
  const f = fixture({ scrollback: 10 });
  try {
    await f.append(70);
    f.checkRows();
    assert.ok(f.adapter.sourceStart() > 0, 'metadata prefix should outlive wrapped physical rows');
    assert.ok(f.metadata.some(item => item.id === '1'), 'bounded headroom should retain an already expired metadata prefix in this fixture');
    assert.equal(f.adapter.indexById('1'), -1);
    const range = f.adapter.viewportRange();
    assert.ok(range.startIndex <= f.adapter.currentIndex());
    assert.ok(f.adapter.currentIndex() <= range.endIndex);
    const lastId = f.metadata.at(-1).id;
    await f.append(2);
    assert.ok(f.adapter.indexById(lastId) >= 0, 'retained IDs remain stable when relative indexes move');
    f.checkRows();
  } finally { f.close(); }
});

test('width and height resize rebuild anchors from retained logical starts', async () => {
  const f = fixture({ scrollback: 50 });
  try {
    await f.append(100);
    for (const [cols, rows] of [[45, 7], [14, 4], [60, 10], [20, 5]]) {
      f.terminal.resize(cols, rows);
      assert.equal(f.adapter.isBusy(), true);
      await f.ready();
      f.checkRows();
    }
  } finally { f.close(); }
});

for (const compressed of [false, true]) {
  test(`wide characters and tabs preserve every log ID through resize and RX (compact=${compressed})`, async () => {
    const f = fixture({ cols: 88, rows: 23, scrollback: 1000 });
    const compact = compressed
      ? require('../SerialMonitor.WinUI/Assets/xterm/compact-scrollback.js').install(f.terminal,
        { onError(error) { throw error; } })
      : null;
    try {
      await f.append(800, '가😀x\t'.repeat(17));
      compact?.compact(Infinity, Infinity);
      f.terminal.resize(34, 23);
      await f.ready();
      f.checkRows();

      // Narrowing a full buffer can leave its unused cursor row marked as a
      // soft wrap. A newly tagged log must remain a distinct logical line.
      await f.append(1, '가😀x\t'.repeat(8));
      f.checkRows();
      compact?.compact(Infinity, Infinity);
      f.terminal.resize(91, 6);
      await f.ready();
      f.checkRows();
      assert.ok(f.adapter.indexById('801') >= 0, 'the newest tagged line must survive reflow');
      assert.equal(f.metadata[f.adapter.sourceStart()].id, '601');
      for (let id = 601; id <= 801; id++) {
        const index = f.adapter.indexById(String(id));
        const start = f.adapter.rowAt(index);
        const end = f.adapter.rowAt(index + 1) ??
          f.terminal.buffer.active.baseY + f.terminal.buffer.active.cursorY;
        let text = '';
        for (let row = start; row < end; row++)
          text += f.terminal.buffer.active.getLine(row).translateToString(true);
        assert.equal(text.replace(/\s/g, ''), `ID${id}` + '가😀x'.repeat(id === 801 ? 8 : 17),
          'reflow and the next RX must not duplicate stale cursor cells or lose payload characters');
      }
    } finally { compact?.dispose(); f.close(); }
  });
}

test('reset resubscribes to the new xterm line ring', async () => {
  const f = fixture({ scrollback: 10 });
  try {
    await f.append(30);
    f.metadata.length = 0;
    f.terminal.reset();
    f.adapter.reset();
    assert.equal(f.adapter.sourceStart(), 0);
    assert.equal(f.adapter.currentIndex(), -1);
    await f.append(50);
    f.checkRows();
  } finally { f.close(); }
});

test('height-only resize preserves anchors without rebuilding the history', async () => {
  const f = fixture({ scrollback: 50 });
  try {
    await f.append(100);
    for (const rows of [12, 3, 20, 5]) {
      f.terminal.resize(20, rows);
      assert.equal(f.adapter.isBusy(), false, 'height changes must not scan the retained history');
      f.checkRows();
      await f.append(3);
      f.checkRows();
    }
  } finally { f.close(); }
});

test('empty tagged partial row is indexed after resize', async () => {
  const f = fixture();
  try {
    await f.append(3);
    await f.write('\x1b]777;999\x07\t');
    f.terminal.resize(25, 6);
    await f.ready();
    assert.ok(f.adapter.indexById('999') >= 0);
    const row = f.adapter.rowAt(f.adapter.indexById('999'));
    assert.equal(row, f.terminal.buffer.active.baseY + f.terminal.buffer.active.cursorY);
  } finally { f.close(); }
});

test('chunked resize allows RX between chunks and preserves appended anchors', async () => {
  const f = fixture({ cols: 40, rows: 10, scrollback: 16000 });
  try {
    await f.append(12000, 'abcdefghijklmnopqrstuvwxyz');
    f.terminal.resize(30, 10);
    assert.equal(f.adapter.isBusy(), true);
    await new Promise(resolve => setTimeout(resolve, 0));
    await f.append(100, 'new RX during rebuild');
    await f.ready();
    f.checkRows(37);
    const last = f.metadata.at(-1);
    assert.notEqual(f.adapter.rowAt(f.adapter.indexById(last.id)), null);
  } finally { f.close(); }
});
