// Isolated headless Edge regression probe; no user browser profile.
// NODE_PATH may point to the bundled runtime's node_modules.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 900, height: 400 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    const result = await page.evaluate(async () => {
      const waitForIdle = async () => {
        while (window.serialMonitorGetAppendQueueState().writing ||
               window.serialMonitorGetAppendQueueState().queueLength) {
          await new Promise(resolve => setTimeout(resolve, 5));
        }
      };
      const taggedLine = id => `\x1b]777;${id},${1700000000000 + id}\x07RX < packet ${id} target\r\n`;
      const search = id => window.serialMonitorSearch({
        text: 'target', targetLineId: String(id),
        targetPayloadStart: 5, targetPayloadOffset: `packet ${id} `.length,
        matchLength: 6, expectedText: 'target', tabsBeforeMatch: 0, tabsBeforeMatchEnd: 0
      });
      const state = () => {
        const position = terminal.getSelectionPosition();
        const viewport = terminal.buffer.active.viewportY;
        let startRow = position?.start.y;
        while (startRow > 0 && terminal.buffer.active.getLine(startRow)?.isWrapped) startRow--;
        return {
          selection: terminal.getSelection(), viewport,
          logicalText: position ? readLogicalLine(startRow).logicalText.replace(/\uE000/g, ' ').trimEnd() : '',
          visible: !!position && position.start.y >= viewport && position.start.y < viewport + terminal.rows,
          following: document.getElementById('autoScrollMenuItem').getAttribute('aria-checked') === 'true'
        };
      };

      window.serialMonitorSetAutoScrollEnabled(false);
      window.serialMonitorAppendLog(Array.from({ length: 200 }, (_, i) => taggedLine(i + 1)).join(''), true, 1);
      await waitForIdle();
      const found = search(10);
      const afterSearch = state();
      for (let batch = 0; batch < 10; batch++) {
        window.serialMonitorAppendLog(Array.from({ length: 20 }, (_, i) => taggedLine(201 + batch * 20 + i)).join(''), true, batch + 2);
        await waitForIdle();
      }
      const afterIncomingLogs = state();

      // Keep receiving while Auto Scroll is OFF, including an in-flight parser callback.
      window.serialMonitorAppendLog(taggedLine(401), true, 20);
      drainAppendQueue();
      const appendWasInFlight = window.serialMonitorGetAppendQueueState().writing;
      const inFlightFound = search(20);
      await waitForIdle();
      const afterInFlightAppend = state();

      const nextFound = search(150);
      const afterNext = state();
      const previousFound = search(10);
      const afterPrevious = state();
      terminal.resize(12, terminal.rows);
      const reflowFound = search(150);
      const afterReflow = state();

      // Explicitly enabling Auto Scroll resumes following new logs.
      window.serialMonitorSetAutoScrollEnabled(true);
      window.serialMonitorAppendLog(taggedLine(402), true, 21);
      await waitForIdle();
      const resumedAtBottom = terminal.buffer.active.viewportY === terminal.buffer.active.baseY;
      const missing = search(9999);
      const followingAfterMissing = state().following;

      window.serialMonitorSetAutoScrollEnabled(false);
      window.serialMonitorAppendLog('\x1b]777;403,1700000000403\x07\t', false, 22);
      await waitForIdle();
      const emptyPartialFound = search(150);
      const afterEmptyPartial = state();

      // A redraw during an unterminated RX must not keep its text/cursor before the snapshot.
      window.serialMonitorAppendLog('unfinished RX', false, 23);
      await waitForIdle();
      window.serialMonitorBeginReplaceLog();
      window.serialMonitorQueueReplaceChunk(Array.from({ length: 40 }, (_, i) => taggedLine(i + 1)).join(''));
      window.serialMonitorCommitReplaceLog(false, 24);
      await waitForIdle();
      const restoredFirstFound = search(1);
      const afterRestore = state();

      window.serialMonitorAppendLog('\x1b]777;41,1700000000041\x07RX < unfinished', false, 25);
      await waitForIdle();
      window.serialMonitorClear();
      window.serialMonitorAppendLog(taggedLine(42), false, 26);
      await waitForIdle();
      const afterClearFound = search(42);
      const afterClear = state();
      return {
        found, afterSearch, afterIncomingLogs, appendWasInFlight, inFlightFound, afterInFlightAppend,
        nextFound, afterNext, previousFound, afterPrevious, reflowFound, afterReflow,
        resumedAtBottom, missing, followingAfterMissing,
        emptyPartialFound, afterEmptyPartial, restoredFirstFound, afterRestore, afterClearFound, afterClear
      };
    });
    assert.equal(result.found.found, true);
    assert.equal(result.afterSearch.visible, true);
    assert.equal(result.afterIncomingLogs.visible, true, 'Incoming RX must keep the search match in view');
    assert.equal(result.afterIncomingLogs.viewport, result.afterSearch.viewport);
    assert.equal(result.afterSearch.following, false);
    assert.equal(result.appendWasInFlight, true);
    assert.equal(result.inFlightFound.found, true);
    assert.equal(result.afterInFlightAppend.visible, true, 'An in-flight append must not undo search navigation');
    for (const name of ['Next', 'Previous', 'Reflow']) {
      assert.equal(result[`${name.toLowerCase()}Found`].found, true);
      assert.equal(result[`after${name}`].selection, 'target');
      assert.equal(result[`after${name}`].visible, true);
    }
    assert.equal(result.resumedAtBottom, true);
    assert.equal(result.missing.found, false);
    assert.equal(result.followingAfterMissing, true, 'A missing target must not alter Auto Scroll');
    assert.equal(result.emptyPartialFound.found, true, 'An unfinished tab-only RX must not shift every earlier line ID');
    assert.equal(result.afterEmptyPartial.selection, 'target');
    assert.equal(result.afterEmptyPartial.logicalText, 'RX < packet 150 target', 'A match must belong to the requested line ID');
    assert.equal(result.afterEmptyPartial.visible, true);
    assert.equal(result.restoredFirstFound.found, true, 'A redraw during partial RX must preserve the first match offset');
    assert.equal(result.afterRestore.selection, 'target');
    assert.equal(result.afterClearFound.found, true, 'Clear during partial RX must not prepend stale text to the next log');
    assert.equal(result.afterClear.logicalText, 'RX < packet 42 target');
    assert.equal(result.afterClear.visible, true);
    await page.evaluate(() => {
      window.searchShortcutMessages = [];
      window.chrome.webview = { postMessage: message => window.searchShortcutMessages.push(message) };
      terminal.focus();
    });
    await page.keyboard.press('F3');
    await page.keyboard.press('Shift+F3');
    await page.keyboard.press('F4');
    await page.keyboard.press('Control+F4');
    const shortcuts = await page.evaluate(() => window.searchShortcutMessages.filter(message => message.type === 'searchShortcut'));
    assert.deepEqual(shortcuts.map(message => message.action), ['next', 'previous', 'previous']);
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ ...result, shortcuts, errors }, null, 2));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
