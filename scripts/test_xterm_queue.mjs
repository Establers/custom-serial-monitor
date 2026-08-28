// Run with: node --test scripts/test_xterm_queue.mjs
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';

const html = readFileSync(new URL('../SerialMonitor.WinUI/Assets/xterm/index.html', import.meta.url), 'utf8');
function section(start, end) {
  const from = html.indexOf(start);
  const to = html.indexOf(end, from);
  assert.ok(from >= 0 && to > from, `Missing xterm section: ${start}`);
  return html.slice(from, to);
}

function createBridge() {
  const pending = [];
  const messages = [];
  const rendered = [];
  const terminal = {
    write: (text, callback) => pending.push(() => {
      rendered.push(text);
      callback();
    }),
    scrollToBottom() {},
    clear() { rendered.length = 0; }
  };
  const window = {
    requestAnimationFrame: callback => pending.push(callback),
    chrome: { webview: { postMessage: message => messages.push(message) } }
  };
  const context = {
    window, terminal, console: { error() {} },
    invalidateLogicalLineStartRows() {},
    queueHexSelectionHintUpdate() {},
    hideHexSelectionHint() {},
    resetParsedLogLineIds() {}
  };
  runInNewContext(
    section('    const appendQueue = [];', '    const contextMenu =') +
    section('    function normalizeLogText(', '    function isAtBottom()') +
    section('    window.serialMonitorAppendLog =', '    window.serialMonitorScrollToBottom =') +
    section('    window.serialMonitorGetAppendQueueState =', '    window.serialMonitorShowRestoreOverlay ='),
    context);
  return { window, terminal, context, messages, pending, renderedText: () => rendered.join(''), flush() {
    for (let count = 0; pending.length; count++) {
      assert.ok(count < 100, 'Queue did not settle');
      pending.shift()(); // xterm invokes write callbacks asynchronously, outside write().
    }
  } };
}

test('an interrupted, uncommitted replacement does not block the next recovery', () => {
  const bridge = createBridge();
  bridge.window.serialMonitorBeginReplaceLog();
  bridge.window.serialMonitorQueueReplaceChunk('old snapshot');
  const state = bridge.window.serialMonitorGetAppendQueueState();
  assert.equal(state.queueLength, 0, 'Uncommitted chunks cannot drain and must not keep the idle wait busy');
  assert.equal(state.writing, false);
  bridge.window.serialMonitorBeginReplaceLog();
  bridge.window.serialMonitorQueueReplaceChunk('fresh snapshot');
  bridge.window.serialMonitorCommitReplaceLog(false);
  assert.equal(bridge.window.serialMonitorGetAppendQueueState().writing, true);
  bridge.window.serialMonitorAppendLog('TX after reconnect', true, 1);
  bridge.flush();
  assert.equal(bridge.messages[0].success, true);
  assert.equal(bridge.window.serialMonitorGetAppendQueueState().writing, false);
});

test('a scroll failure in an async append callback releases the queue and reports failure', () => {
  const bridge = createBridge();
  bridge.terminal.scrollToBottom = () => { throw new Error('scroll failed'); };
  bridge.window.serialMonitorAppendLog('first TX', true, 1);
  bridge.flush();
  assert.equal(bridge.messages[0].success, false);
  assert.equal(bridge.window.serialMonitorGetAppendQueueState().writing, false);
  bridge.terminal.scrollToBottom = () => {};
  bridge.window.serialMonitorAppendLog('next TX', true, 2);
  bridge.flush();
  assert.equal(bridge.messages[1].success, true);
});

test('replacement callback failure releases writes and reports the failed redraw', () => {
  const bridge = createBridge();
  bridge.window.serialMonitorBeginReplaceLog();
  bridge.window.serialMonitorQueueReplaceChunk('snapshot');
  bridge.window.serialMonitorCommitReplaceLog(false, 100);
  bridge.context.invalidateLogicalLineStartRows = () => { throw new Error('render failed'); };
  bridge.flush();
  assert.equal(bridge.window.serialMonitorGetAppendQueueState().writing, false);
  assert.equal(bridge.messages[0].type, 'xtermAppendCompleted');
  assert.equal(bridge.messages[0].requestId, 100);
  assert.equal(bridge.messages[0].success, false);
  bridge.context.invalidateLogicalLineStartRows = () => {};
  bridge.window.serialMonitorAppendLog('next TX', true, 1);
  bridge.flush();
  assert.equal(bridge.messages[1].success, true);
});

test('a replacement is acknowledged only after all chunks have finished parsing', () => {
  const bridge = createBridge();
  bridge.window.serialMonitorBeginReplaceLog();
  bridge.window.serialMonitorQueueReplaceChunk('first chunk');
  bridge.window.serialMonitorQueueReplaceChunk('last chunk');
  assert.equal(bridge.window.serialMonitorCommitReplaceLog(false, 100), true);
  assert.equal(bridge.messages.length, 0);
  bridge.pending.shift()();
  assert.equal(bridge.messages.length, 0, 'The first parsed chunk is not a completed snapshot');
  bridge.flush();
  assert.equal(bridge.messages.length, 1);
  assert.equal(bridge.messages[0].requestId, 100);
  assert.equal(bridge.messages[0].success, true);
});

test('partial replacement failure stays failed when idle, then a new replacement restores every chunk', () => {
  const bridge = createBridge();
  const chunks = ['first\r\n', 'middle\r\n', 'last\r\n'];
  let parsedChunks = 0;
  bridge.window.serialMonitorBeginReplaceLog();
  for (const chunk of chunks) bridge.window.serialMonitorQueueReplaceChunk(chunk);
  bridge.context.invalidateLogicalLineStartRows = () => {
    if (++parsedChunks === 2) throw new Error('injected middle-chunk callback failure');
  };
  bridge.window.serialMonitorCommitReplaceLog(false, 100);
  bridge.flush();

  assert.equal(bridge.renderedText(), chunks.slice(0, 2).join(''));
  assert.equal(bridge.window.serialMonitorGetAppendQueueState().queueLength, 0);
  assert.equal(bridge.window.serialMonitorGetAppendQueueState().writing, false);
  assert.equal(bridge.messages.length, 1);
  assert.equal(bridge.messages[0].requestId, 100);
  assert.equal(bridge.messages[0].success, false, 'Idle must not turn a partial render into success');

  bridge.context.invalidateLogicalLineStartRows = () => {};
  bridge.window.serialMonitorBeginReplaceLog();
  for (const chunk of chunks) bridge.window.serialMonitorQueueReplaceChunk(chunk);
  bridge.window.serialMonitorCommitReplaceLog(false, 101);
  assert.equal(bridge.messages.length, 1, 'Retry must wait for its own completion');
  bridge.flush();

  assert.equal(bridge.messages.length, 2);
  assert.equal(bridge.messages[1].requestId, 101);
  assert.equal(bridge.messages[1].success, true);
  assert.equal(bridge.renderedText(), chunks.join(''), 'Restore the full snapshot without missing or duplicate chunks');
  assert.equal(bridge.messages[0].success, false, 'Retry success must not overwrite the failed operation');
});

test('canceling a replacement drains the in-flight write before accepting the next TX', () => {
  const bridge = createBridge();
  bridge.window.serialMonitorBeginReplaceLog();
  bridge.window.serialMonitorQueueReplaceChunk('first chunk');
  bridge.window.serialMonitorQueueReplaceChunk('canceled chunk');
  bridge.window.serialMonitorCommitReplaceLog(false, 100);
  bridge.window.serialMonitorCancelPendingWrites();
  assert.equal(bridge.window.serialMonitorGetAppendQueueState().writing, true);
  assert.equal(bridge.messages.length, 0, 'Cancellation must wait for the in-flight parser callback');
  bridge.window.serialMonitorAppendLog('TX after cancel', true, 1);
  bridge.flush();
  assert.equal(bridge.messages[0].requestId, 100);
  assert.equal(bridge.messages[0].success, false);
  assert.equal(bridge.messages[1].requestId, 1);
  assert.equal(bridge.messages[1].success, true);
  assert.equal(bridge.window.serialMonitorGetAppendQueueState().writing, false);
  assert.equal(bridge.window.serialMonitorGetAppendQueueState().queueLength, 0);
});

test('an empty startup snapshot acknowledges completion without waiting for a write', () => {
  const bridge = createBridge();
  bridge.window.serialMonitorBeginReplaceLog();
  bridge.window.serialMonitorCommitReplaceLog(false, 100);
  assert.equal(bridge.messages.length, 1);
  assert.equal(bridge.messages[0].requestId, 100);
  assert.equal(bridge.messages[0].success, true);
  assert.equal(bridge.window.serialMonitorGetAppendQueueState().writing, false);
});
