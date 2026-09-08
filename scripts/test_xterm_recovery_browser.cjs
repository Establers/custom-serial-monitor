// Isolated real-renderer regression for the host's streamed replacement protocol.
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
    await page.addInitScript(() => {
      window.recoveryAcks = new Map();
      window.chrome ??= {};
      window.chrome.webview = { postMessage(message) {
        if (message.type === 'xtermAppendCompleted') {
          window.recoveryAcks.get(message.requestId)?.(message);
          window.recoveryAcks.delete(message.requestId);
        } else if (message.type === 'xtermHistoryError') throw new Error(message.error);
      } };
    });
    await page.goto(pathToFileURL(path.resolve(__dirname, '../SerialMonitor.WinUI/Assets/xterm/index.html')).href);
    await page.waitForSelector('.xterm-screen');
    await page.evaluate(() => {
      window.serialMonitorSetScrollback(1_000_000);
      window.serialMonitorShowRestoreOverlay('Restoring log view...', '1,000,000 lines');
      window.serialMonitorBeginReplaceLog();
    });
    const started = performance.now();
    const pendingLive = [];
    for (let batch = 0; batch < 500; batch++) {
      const text = Array.from({ length: 2000 }, (_, i) => {
        const id = batch * 2000 + i;
        return `\x1b]777;${id + 1},${1700000000000 + id}\x07packet ${id} 한글 \x1b[31mRED\x1b[0m temperature=25.3\r\n`;
      }).join('');
      assert.ok(text.length <= 256 * 1024);
      // New input accepted while the host holds its append gate. It must be
      // appended after the snapshot, not used to request another full redraw.
      pendingLive.push(`live ${batch}\r\n`);
      const ack = await page.evaluate(({ text, id }) => new Promise(resolve => {
        window.recoveryAcks.set(id, resolve);
        if (!window.serialMonitorQueueReplaceChunk(text) || !window.serialMonitorCommitReplaceLog(false, id)) {
          throw new Error('replacement rejected');
        }
      }), { text, id: batch + 1 });
      assert.equal(ack.success, true);
    }
    const restoreSeconds = (performance.now() - started) / 1000;
    await page.evaluate(async text => {
      await new Promise(resolve => {
        window.recoveryAcks.set(1001, resolve);
        window.serialMonitorAppendLog(text, true, 1001);
      });
      window.serialMonitorHideRestoreOverlay();
      terminal.scrollToBottom();
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
    }, pendingLive.join(''));
    const result = await page.evaluate(() => ({
      last: terminal.buffer.active.getLine(terminal.buffer.active.length - 2).translateToString(true),
      queue: window.serialMonitorGetAppendQueueState(),
      count: terminal.buffer.active.length,
      metadata: getParsedLogLineIdCount(),
      retainedChunkReferences: replaceQueue.filter(Boolean).length + appendQueue.filter(Boolean).length
    }));
    assert.equal(result.last, 'live 499');
    assert.equal(result.queue.queueLength, 0);
    assert.equal(result.queue.writing, false);
    assert.equal(result.retainedChunkReferences, 0);
    assert.equal(result.metadata, 1_000_000);
    assert.deepEqual(errors, []);
    console.log(JSON.stringify({ restoreSeconds, ...result, errors }, null, 2));
  } finally {
    await browser.close();
  }
})().catch(error => { console.error(error); process.exitCode = 1; });
