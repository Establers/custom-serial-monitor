// Isolated headless Edge regression/performance probe; no user browser profile.
// NODE_PATH may point to the bundled runtime's node_modules.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 1400, height: 850 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    const result = await page.evaluate(async () => {
      const count = 100_000;
      const payload = Array.from({ length: 32 }, (_, i) => i.toString(16).padStart(2, '0').toUpperCase()).join(' ');
      window.serialMonitorSetScrollback(400_000);
      window.serialMonitorSetHexSelectionHintEnabled(true);
      for (let batch = 0; batch < count / 1000; batch++) {
        const text = Array.from({ length: 1000 }, (_, i) => {
          const id = batch * 1000 + i;
          return `\x1b]777;${id + 1},${1700000000000 + id}\x07RX < ${payload}\r\n`;
        }).join('');
        await new Promise(resolve => terminal.write(text, resolve));
        onTerminalWriteParsed();
      }
      function measure(fn) {
        const start = performance.now();
        const value = fn();
        return { ms: performance.now() - start, value };
      }
      const smallPosition = { start: { x: 0, y: 0 }, end: { x: 1, y: 1 } };
      invalidateLogicalLineStartRows();
      const coldDelta = measure(() => getSelectionDeltaMilliseconds(smallPosition));
      const cachedDelta = measure(() => getSelectionDeltaMilliseconds(smallPosition));
      terminal.selectAll();
      const selection = measure(() => terminal.getSelection());
      const bytes = measure(() => countHexBytesInSelection(selection.value));
      // Use the same logical payload after a width change forces reflow.
      const resize = measure(() => terminal.resize(60, terminal.rows));
      invalidateLogicalLineStartRows();
      const starts = getLogicalLineStartRows();
      const afterDelta = getSelectionDeltaMilliseconds({
        start: { x: 0, y: starts[0] }, end: { x: 1, y: starts[1] }
      });
      terminal.selectAll();
      const afterBytes = countHexBytesInSelection(terminal.getSelection());
      // A large HEX packet must preserve byte count across wrapped rows.
      terminal.reset();
      resetParsedLogLineIds();
      invalidateLogicalLineStartRows();
      const largePayload = Array(65536).fill('AB').join(' ');
      await new Promise(resolve => terminal.write(`\x1b]777;1,1700000000000\x07RX < ${largePayload}\r\n`, resolve));
      onTerminalWriteParsed();
      terminal.selectAll();
      const largeSelection = measure(() => countHexBytesInSelection(terminal.getSelection()));
      return {
        coldDelta, cachedDelta, selectionMs: selection.ms,
        selectionChars: selection.value.length, bytes, resizeMs: resize.ms,
        logicalLinesAfterResize: starts.length, afterDelta, afterBytes, largeSelection
      };
    });
    assert.equal(result.coldDelta.value, 1);
    assert.equal(result.cachedDelta.value, 1);
    assert.equal(result.bytes.value, 3_200_000);
    assert.equal(result.logicalLinesAfterResize, 100_000);
    assert.equal(result.afterDelta, 1);
    assert.equal(result.afterBytes, 3_200_000);
    assert.equal(result.largeSelection.value, 65536);
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ ...result, errors }, null, 2));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
