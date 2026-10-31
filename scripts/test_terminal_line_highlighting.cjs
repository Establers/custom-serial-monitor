// Real bundled xterm/Edge regressions; NODE_PATH may point to bundled packages.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1100, height: 700 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    page.on('console', message => {
      if (message.type() === 'error' && message.text().startsWith('Rule line highlighting failed')) errors.push(message.text());
    });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    const passed = await page.evaluate(async () => {
      const passed = [];
      let request = 0;
      const check = (value, message) => { if (!value) throw Error(message); };
      const wait = async () => {
        const deadline = Date.now() + 15000;
        while (Date.now() < deadline) {
          const q = window.serialMonitorGetAppendQueueState();
          if (!q.writing && !q.queueLength && !navigationRows.isBusy() && !lineHighlighting.pendingCount()) return;
          await new Promise(resolve => setTimeout(resolve, 5));
        }
        throw Error('Highlighting did not settle');
      };
      const tag = id => `\x1b]777;${id},1793408400000\x07`;
      const paint = (id, sgr = '31') => `\x1b]778;${id};${sgr}\x07`;
      const append = async text => { window.serialMonitorAppendLog(text, false, ++request); await wait(); };
      const reset = async (columns = 30, rows = 12, scrollback = 1000) => {
        window.serialMonitorClear();
        window.serialMonitorSetScrollback(scrollback);
        terminal.resize(columns, rows);
        await wait();
      };
      const texts = () => Array.from({ length: terminal.buffer.active.length }, (_, row) =>
        terminal.buffer.active.getLine(row).translateToString(true)).filter(Boolean);
      const assertColor = (start, end, fg, fgMode = 0x1000000, bg = -1, bgMode = 0) => {
        for (let row = start; row < end; row++) {
          const line = terminal.buffer.active.getLine(row);
          for (let col = 0; col < terminal.cols; col++) {
            const cell = line.getCell(col);
            if (!cell.getChars()) continue;
            check(cell.getFgColor() === fg && cell.getFgColorMode() === fgMode,
              `FG row ${row} col ${col}: ${cell.getChars()} / ${cell.getFgColorMode()}:${cell.getFgColor()} expected ${fgMode}:${fg}`);
            check(cell.getBgColor() === bg && cell.getBgColorMode() === bgMode, `BG row ${row} col ${col}`);
          }
        }
      };
      const colorId = (id, ...colors) => {
        const range = navigationRows.rowsById(String(id));
        check(!!range, `No retained rows for ${id}`);
        assertColor(range.start, range.end, ...colors);
      };

      await reset();
      await append(tag(1) + '[2026-10-31] prefix ' + 'x'.repeat(120) + 'FA');
      terminal.select(0, 0, 8);
      const selection = terminal.getSelection();
      await append(paint(1) + '\x1b[31mULT injected_dup=0 injected_crc=0\x1b[0m\r\n');
      colorId(1, 1);
      check(terminal.getSelection() === selection, 'Recoloring preserves selection');
      check(texts().join('').endsWith('FAULT injected_dup=0 injected_crc=0'), 'No text or wraps lost');
      passed.push('late split keyword, wrapped STATUS tail, timestamp and selection');

      await reset();
      await append(tag(1) + '\x1b[1;3;38;2;207;232;255m[INFO] 타임 e\u0301 😀 FA\x1b[0m');
      const before = texts().join('');
      await append(paint(1, '38;5;16;43') + '\x1b[38;5;16;43mULT\x1b[0m');
      colorId(1, 16, 0x2000000, 3, 0x1000000);
      check(terminal.buffer.active.getLine(0).getCell(0).isBold(), 'Bold retained');
      check(terminal.buffer.active.getLine(0).getCell(0).isItalic(), 'Italic retained');
      check(texts().join('') === before + 'ULT', 'Unicode/combining/emoji retained');
      passed.push('ANSI flags, palette foreground, background, Korean/combining/emoji');

      await reset();
      await append(tag(1) + '\x1b[31mWARN\x1b[0m');
      await append(paint(1, '0') + ' IGNORE tail');
      colorId(1, -1, 0);
      passed.push('default-color priority winner clears an earlier color');

      await reset();
      await append(tag(1) + 'prefix FA\r\n' + tag(2) + '\x1b[36mSTATUS?\x1b[0m\r\n');
      const cursor = [terminal.buffer.active.cursorX, terminal.buffer.active.cursorY];
      const view = terminal.buffer.active.viewportY;
      await append(paint(1));
      check(JSON.stringify(cursor) === JSON.stringify([terminal.buffer.active.cursorX, terminal.buffer.active.cursorY]), 'Cursor unchanged');
      check(terminal.buffer.active.viewportY === view, 'Viewport unchanged');
      await append(tag(3) + '\x1b[31mULT tail\x1b[0m\r\n' + tag(4) + 'unmatched next\r\n');
      colorId(1, 1);
      colorId(2, 6);
      colorId(3, 1);
      colorId(4, -1, 0);
      passed.push('interleaved TX, unrelated next line, cursor/viewport preserved');

      await reset(20, 12);
      await append(tag(1) + 'cold '.repeat(2400) + '\r\n' + tag(2) + 'next');
      compactScrollback.compact();
      const cold = terminal._core._bufferService.buffers.normal.lines.get(0);
      check(!!cold._packedCells || !!cold._denseCells, 'Test includes compact historical cells');
      await append(paint(1));
      colorId(1, 1);
      colorId(2, -1, 0);
      compactScrollback.compact();
      colorId(1, 1);
      passed.push('long cold/compressed rows recolored without changing another line');

      await reset(20, 12, 1000);
      await append(tag(1) + 'x'.repeat(26000) + '\r\n' + tag(2) + 'next');
      check(terminal.buffer.active.getLine(0).isWrapped, 'Prefix really evicted');
      await append(paint(1));
      colorId(1, 1);
      colorId(2, -1, 0);
      passed.push('soft-wrapped suffix recolored after original prefix eviction');

      await reset(45, 12);
      await append(tag(1) + 'prefix '.repeat(80) + '\r\n' + tag(2) + 'next');
      terminal.resize(17, 12);
      window.serialMonitorAppendLog(paint(1, '94'), false, ++request);
      await wait();
      colorId(1, 12);
      colorId(2, -1, 0);
      terminal.resize(60, 14);
      await wait();
      colorId(1, 12);
      colorId(2, -1, 0);
      passed.push('concurrent resize reflow and later widening preserve colors/identity');

      await reset();
      await append(tag(1) + 'plain');
      await append(paint(999) + '\x1b]778;1;500\x07\x1b]778;1;38;5;999\x07');
      colorId(1, -1, 0);
      terminal.resize(15, 12);
      window.serialMonitorAppendLog(paint(1), false, ++request);
      window.serialMonitorClear();
      await wait();
      await append(tag(1) + 'new identity');
      colorId(1, -1, 0);
      passed.push('invalid/expired commands ignored and clear cancels pending recoloring');

      const failures = [];
      const broken = SerialMonitorLineHighlighting.install(terminal, () => { throw Error('injected adapter failure'); },
        error => failures.push(String(error)));
      await append(paint(1));
      await append(paint(1));
      check(failures.length === 1 && failures[0].includes('injected adapter failure'), 'Errors reported once without an unhandled exception');
      check(broken.pendingCount() === 0, 'Failure releases pending work');
      broken.dispose();
      passed.push('adapter failure reported once and pending work safely released');
      return passed;
    });
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ passed: passed.length, cases: passed }, null, 2));
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
