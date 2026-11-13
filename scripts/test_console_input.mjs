import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import { runInNewContext } from 'node:vm';

const context = {};
for (const file of ['app-shortcuts.json.js', 'shortcut-keys.js', 'console-input.js'])
  runInNewContext(readFileSync(new URL(`../SerialMonitor.WinUI/Assets/xterm/${file}`, import.meta.url), 'utf8'), context);
const create = receiveNewline => {
  const input = new context.SerialMonitorConsoleInput();
  assert.equal(input.configure({ enter: 'Cr', backspace: 'Del', receiveNewline }), true);
  return input;
};

const source = 'a\rb\nc\r\nd\r\r\ne\n\n';
for (const [mode, expected] of [
  ['Preserve', source],
  ['Lf', 'a\rb\r\nc\r\nd\r\r\ne\r\n\r\n'],
  ['Cr', 'a\r\nb\nc\r\nd\r\n\r\ne\n\n'],
  ['Auto', 'a\r\nb\r\nc\r\nd\r\n\r\ne\r\n\r\n']
]) test(`${mode} preserves CRLF across every possible RX batch split`, () => {
  for (let split = 0; split <= source.length; split++) {
    const input = create(mode);
    assert.equal(input.normalizeReceived(source.slice(0, split)) + input.normalizeReceived(source.slice(split)), expected);
  }
  const input = create(mode);
  assert.equal(Array.from(source, character => input.normalizeReceived(character)).join(''), expected);
});

test('key preference updates retain a split CRLF; receive-mode changes and new sessions reset it', () => {
  const input = create('Auto');
  assert.equal(input.normalizeReceived('one\r'), 'one\r\n');
  assert.equal(input.configure({ enter: 'Lf', backspace: 'Bs', receiveNewline: 'Auto' }), true);
  assert.equal(input.normalizeReceived('\ntwo'), 'two');
  input.normalizeReceived('\r');
  input.configure({ enter: 'Lf', backspace: 'Bs', receiveNewline: 'Lf' });
  assert.equal(input.normalizeReceived('\n'), '\r\n');
  input.normalizeReceived('\r');
  input.resetReceive();
  assert.equal(input.normalizeReceived('\n'), '\r\n');
});

test('invalid configuration is rejected as a whole without changing current preferences', () => {
  const input = create('Auto');
  for (const settings of [null, {}, { enter: 'Lf', backspace: 'invalid', receiveNewline: 'Preserve' },
    { enter: 'invalid', backspace: 'Bs', receiveNewline: 'Cr' }, { enter: 'Lf', backspace: 'Bs', receiveNewline: 'invalid' }]) {
    assert.equal(input.configure(settings), false);
    assert.equal(input.keyData({ type: 'keydown', key: 'Enter' }), '\r');
    assert.equal(input.keyData({ type: 'keydown', key: 'Backspace' }), '\x7f');
    assert.equal(input.receiveNewline, 'Auto');
  }
});

test('control shortcuts and non-keydown events retain xterm handling', () => {
  const input = create('Preserve');
  for (const key of ['Enter', 'Backspace']) {
    for (const modifier of ['ctrlKey', 'altKey', 'metaKey'])
      assert.equal(input.keyData({ type: 'keydown', key, [modifier]: true }), null);
    assert.equal(input.keyData({ type: 'keyup', key }), null);
    assert.equal(input.keyData({ type: 'keydown', key, isComposing: true }), null);
    assert.equal(input.keyData({ type: 'keydown', key, keyCode: 229 }), null);
  }
  assert.equal(input.keyData({ type: 'keydown', key: 'ArrowUp' }), null);
});
