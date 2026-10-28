// Real Edge regression: idle-time navigation must use the frozen last log ID,
// while ordinary time lookup keeps its first-at-or-after semantics.
// NODE_PATH may point to the bundled runtime's node_modules.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { pathToFileURL } = require('node:url');
const { chromium } = require('playwright');

(async () => {
  const browser = await chromium.launch({ channel: 'msedge', headless: true });
  try {
    const page = await browser.newPage({ viewport: { width: 900, height: 550 }, timezoneId: 'Asia/Seoul' });
    const errors = [];
    page.on('pageerror', error => errors.push(error.message));
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    await page.waitForTimeout(250);
    const result = await page.evaluate(async () => {
      const idle = async () => {
        const deadline = Date.now() + 10000;
        while (Date.now() < deadline) {
          const queue = window.serialMonitorGetAppendQueueState();
          if (!queue.writing && !queue.queueLength && !window.serialMonitorGetNavigationSnapshot({ binCount: 0 }).busy) return;
          await new Promise(resolve => setTimeout(resolve, 5));
        }
        throw Error('End anchor fixture did not settle');
      };
      const timestamp = new Date(2026, 9, 28, 21, 27, 19, 123).getTime();
      const line = (id, stamp = timestamp) => `\x1b]777;${id},${stamp}\x07packet ${id}\r\n`;
      const selectedText = index => terminal.buffer.active.getLine(navigationRows.rowAt(index))?.translateToString(true);
      window.serialMonitorSetScrollback(1000);
      window.serialMonitorClear();
      window.serialMonitorAppendLog(Array.from({ length: 200 }, (_, index) => line(String(index + 1))).join(''), true, 1);
      await idle();
      const snapshot = window.serialMonitorGetNavigationSnapshot({ binCount: 0 });
      // The original clamp-to-EndTime workaround selects the first duplicate.
      const oldIdleClamp = window.serialMonitorNavigateToTime(snapshot.endTime);
      const frozenEndId = snapshot.endId;
      const frozenLast = window.serialMonitorNavigateToIndex(-1, frozenEndId);
      const frozenText = frozenLast.ok ? selectedText(frozenLast.index) : null;
      const ordinaryTime = window.serialMonitorNavigateToTime(timestamp);

      window.serialMonitorAppendLog(Array.from({ length: 10 }, (_, index) => line(String(index + 201))).join(''), true, 2);
      await idle();
      const afterAppend = window.serialMonitorGetNavigationSnapshot({ binCount: 0 });
      const stillFrozen = window.serialMonitorNavigateToIndex(-1, frozenEndId);
      const stillFrozenText = stillFrozen.ok ? selectedText(stillFrozen.index) : null;

      window.serialMonitorAppendLog(Array.from({ length: 2000 }, (_, index) =>
        line(String(index + 211), timestamp + (index + 1) * 1000)).join(''), true, 3);
      await idle();
      const replacementOrdinal = window.serialMonitorResolveIndex(199);
      const viewportBeforeExpired = terminal.buffer.active.viewportY;
      const historyBeforeExpired = navigationBackStack.length;
      const expired = window.serialMonitorNavigateToIndex(199, frozenEndId);
      const viewportAfterExpired = terminal.buffer.active.viewportY;
      const historyAfterExpired = navigationBackStack.length;

      window.serialMonitorClear();
      const emptyEndId = window.serialMonitorGetNavigationSnapshot({ binCount: 0 }).endId;
      const largeBase = 9007199254740993n;
      const largeIds = Array.from({ length: 20 }, (_, index) => String(largeBase + BigInt(index)));
      window.serialMonitorAppendLog(largeIds.map(id => line(id)).join(''), false, 4);
      await idle();
      const largeSnapshot = window.serialMonitorGetNavigationSnapshot({ binCount: 0 });
      const largeJump = window.serialMonitorNavigateToIndex(-1, largeSnapshot.endId);
      const largeText = largeJump.ok ? selectedText(largeJump.index) : null;
      return { sharedTimestamp: timestamp, snapshot, oldIdleClamp, frozenLast, frozenText, ordinaryTime,
        afterAppend, stillFrozen, stillFrozenText, replacementOrdinal, expired,
        viewportBeforeExpired, viewportAfterExpired, historyBeforeExpired, historyAfterExpired,
        emptyEndId, largeSnapshot, largeJump, largeText, expectedLargeId: largeIds.at(-1) };
    });
    const reportPath = path.resolve(__dirname, '../artifacts/navigation-end-anchor.json');
    fs.mkdirSync(path.dirname(reportPath), { recursive: true });
    fs.writeFileSync(reportPath, JSON.stringify({ ...result, errors }, null, 2));
    console.log(JSON.stringify({ ...result, errors, reportPath }, null, 2));
    assert.equal(result.oldIdleClamp.id, '1', 'Time=end must retain first-duplicate semantics; it is not a last-record anchor');
    assert.equal(result.snapshot.endId, '200');
    assert.equal(result.frozenLast.ok, true);
    assert.equal(result.frozenLast.id, '200');
    assert.equal(result.frozenLast.index, 199);
    assert.equal(result.frozenText, 'packet 200');
    assert.equal(result.ordinaryTime.id, '1', 'Ordinary timestamp navigation must keep first-at-or-after behavior');
    assert.equal(result.afterAppend.endId, '210');
    assert.equal(result.stillFrozen.id, '200', 'A frozen dialog must not silently follow the new tail');
    assert.equal(result.stillFrozenText, 'packet 200');
    assert.notEqual(result.replacementOrdinal.id, '200');
    assert.equal(result.expired.ok, false);
    assert.equal(result.expired.error, 'expired', 'Expired IDs must not fall back to a reused ordinal or timestamp');
    assert.equal(result.viewportAfterExpired, result.viewportBeforeExpired);
    assert.equal(result.historyAfterExpired, result.historyBeforeExpired);
    assert.equal(result.emptyEndId, null);
    assert.equal(result.largeSnapshot.endId, result.expectedLargeId);
    assert.equal(result.largeJump.id, result.expectedLargeId);
    assert.equal(result.largeText, `packet ${result.expectedLargeId}`);
    assert.deepEqual(errors, []);
    console.log('PASS last-record ID anchor, equal timestamps, frozen tail, eviction, exact large IDs');
  } finally { await browser.close(); }
})().catch(error => { console.error(error); process.exitCode = 1; });
