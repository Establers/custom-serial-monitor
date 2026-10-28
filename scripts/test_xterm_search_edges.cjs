// Isolated headless Edge regressions for source offsets and surrounding logs.
// NODE_PATH may point to the bundled runtime's node_modules.
const assert = require('node:assert/strict');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 900, height: 500 } });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    // Let initial fit/font work settle before deliberately testing wrapped rows.
    await page.waitForTimeout(150);
    const result = await page.evaluate(async () => {
      const waitForIdle = async () => {
        while (window.serialMonitorGetAppendQueueState().writing ||
               window.serialMonitorGetAppendQueueState().queueLength) {
          await new Promise(resolve => setTimeout(resolve, 5));
        }
      };
      const request = (id, payload, text) => {
        const offset = payload.indexOf(text);
        const tabs = value => (value.match(/\t/g) || []).length;
        return window.serialMonitorSearch({
          text, targetLineId: String(id), targetPayloadStart: 5,
          targetPayloadOffset: offset, matchLength: text.length, expectedText: text,
          tabsBeforeMatch: tabs(payload.slice(0, offset)),
          tabsBeforeMatchEnd: tabs(payload.slice(0, offset + text.length))
        });
      };

      const cases = [
        { name: 'combining mark', payload: 'cafe\u0301 END', text: '\u0301', selected: 'e\u0301' },
        { name: 'base inside combined cell', payload: 'cafe\u0301 END', text: 'e', selected: 'e\u0301' },
        { name: 'multiple combining marks', payload: 'abn\u0301\u0327 END', text: '\u0327', selected: 'n\u0301\u0327' },
        { name: 'emoji before match', payload: '한글 😀 target', text: 'target', selected: 'target' },
        { name: 'wide character wrap', payload: 'abcdefghijklmnop한target', text: '한target', selected: '한target' },
        { name: 'tabs before match', payload: '\t\talpha\ttarget', text: 'target', selected: 'target' },
        { name: 'tab within match', payload: 'alpha\ttarget END', text: 'alpha\ttarget', selected: 'alpha      target' }
      ];
      terminal.resize(22, 20);
      const matches = [];
      for (const [index, item] of cases.entries()) {
        window.serialMonitorClear();
        window.serialMonitorAppendLog(`\x1b]777;${index + 1}\x07RX < ${item.payload}\r\n`, false, index + 1);
        await waitForIdle();
        const found = request(index + 1, item.payload, item.text);
        matches.push({ name: item.name, found, selection: terminal.getSelection(), expected: item.selected });
      }

      terminal.resize(80, 20);
      window.serialMonitorClear();
      window.serialMonitorSetAutoScrollEnabled(true);
      const line = id => `\x1b]777;${id}\x07RX < packet ${id} target\r\n`;
      window.serialMonitorAppendLog(Array.from({ length: 100 }, (_, index) => line(index + 1)).join(''), true, 100);
      await waitForIdle();
      // A visible result close to the viewport edge still needs surrounding logs.
      terminal.scrollToLine(20);
      const edgeFound = request(39, 'packet 39 target', 'target');
      const edgePosition = terminal.getSelectionPosition();
      const edgeViewport = terminal.buffer.active.viewportY;
      const edgeContext = {
        before: edgePosition.start.y - edgeViewport,
        after: edgeViewport + terminal.rows - 1 - edgePosition.end.y
      };
      window.serialMonitorSetAutoScrollEnabled(true);
      const foundWhileFollowing = request(40, 'packet 40 target', 'target');
      const followingAfterFound = document.getElementById('autoScrollMenuItem').getAttribute('aria-checked');
      const viewportAfterFound = terminal.buffer.active.viewportY;
      window.serialMonitorAppendLog(line(101), true, 101);
      await waitForIdle();
      const viewportAfterAppend = terminal.buffer.active.viewportY;
      const selectionAfterAppend = terminal.getSelection();
      // A failed jump must not unexpectedly turn following off.
      window.serialMonitorSetAutoScrollEnabled(true);
      const missing = request(999, 'packet 999 target', 'target');
      const followingAfterMissing = document.getElementById('autoScrollMenuItem').getAttribute('aria-checked');
      return {
        matches, edgeFound, edgeContext, foundWhileFollowing, followingAfterFound,
        viewportAfterFound, viewportAfterAppend, selectionAfterAppend, missing, followingAfterMissing
      };
    });
    for (const item of result.matches) {
      assert.equal(item.found.found, true, item.name);
      assert.equal(item.selection, item.expected, item.name);
    }
    assert.equal(result.edgeFound.found, true);
    assert.ok(result.edgeContext.before >= 3, 'A result near the edge should reveal preceding logs');
    assert.ok(result.edgeContext.after >= 3, 'A result near the edge should reveal following logs');
    assert.equal(result.foundWhileFollowing.found, true);
    assert.equal(result.followingAfterFound, 'false', 'A successful jump must stop following new RX');
    assert.equal(result.viewportAfterAppend, result.viewportAfterFound, 'RX must preserve the selected search location');
    assert.equal(result.selectionAfterAppend, 'target');
    assert.equal(result.missing.found, false);
    assert.equal(result.followingAfterMissing, 'true');
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ ...result, errors }, null, 2));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
