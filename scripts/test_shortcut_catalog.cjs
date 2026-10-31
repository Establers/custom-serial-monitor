// No npm/browser dependencies: also runs in the Windows CI build matrix.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const root = path.resolve(__dirname, '..');
const context = vm.createContext({});
for (const file of ['app-shortcuts.json.js', 'shortcut-keys.js']) {
  vm.runInContext(fs.readFileSync(path.join(root, 'SerialMonitor.WinUI/Assets/xterm', file), 'utf8'), context);
}
const catalog = context.serialMonitorShortcuts;
const eventFor = text => {
  const parts = text.toUpperCase().split('+');
  return { key: parts.pop(), ctrlKey: parts.includes('CTRL'), altKey: parts.includes('ALT'),
    shiftKey: parts.includes('SHIFT'), metaKey: parts.includes('WIN') };
};
for (const gesture of catalog.reservedKeys) assert.equal(catalog.userShortcut(eventFor(gesture)), null, gesture);

// WinUI records number-row keys as VirtualKey.NumberN even when the layout
// produces punctuation or accented letters. Leave modified keys unchanged.
const numberRows = {
  French: ['à', '&', 'é', '"', "'", '(', '-', 'è', '_', 'ç'],
  Czech: ['é', '+', 'ě', 'š', 'č', 'ř', 'ž', 'ý', 'á', 'í']
};
for (const [layout, characters] of Object.entries(numberRows)) {
  for (const [digit, key] of characters.entries()) {
    const event = { key, code: `Digit${digit}` };
    assert.equal(catalog.userShortcut(event), String(digit), `${layout}: ${event.code}`);
    for (const flag of ['ctrlKey', 'altKey', 'shiftKey', 'metaKey', 'isComposing']) {
      assert.equal(catalog.userShortcut({ ...event, [flag]: true }), null, `${layout}: ${flag} + ${event.code}`);
    }
  }
}

for (let digit = 0; digit < 10; digit++) {
  for (const code of [`Digit${digit}`, `Numpad${digit}`]) {
    assert.equal(catalog.userShortcut({ key: String(digit), code }), String(digit));
  }
}
// A physical position must not reinterpret existing letters, IME/dead keys,
// numpad navigation with NumLock off, or browser zoom shortcuts.
assert.equal(catalog.userShortcut({ key: 'a', code: 'KeyQ' }), 'A');
assert.equal(catalog.userShortcut({ key: 'z', code: 'KeyY' }), 'Z');
assert.equal(catalog.userShortcut({ key: '1', code: 'Digit1', ctrlKey: true }), 'Ctrl+1');
for (const event of [
  { key: '&' }, { key: '&', code: 'Numpad1' },
  { key: 'Dead', code: 'Digit6' }, { key: 'Process', code: 'Digit1' },
  { key: 'Unidentified', code: 'Digit1' }, { key: 'End', code: 'Numpad1' },
  { key: '-', code: 'Digit6', ctrlKey: true },
  { key: '+', code: 'Digit1', ctrlKey: true },
  { key: '@', code: 'Digit2', ctrlKey: true, altKey: true }
]) assert.equal(catalog.userShortcut(event), null, JSON.stringify(event));
assert.equal(catalog.matches('TextNavigation', { key: 'End', code: 'Numpad1' }), true);
assert.equal(catalog.matches('BrowserControls', { key: '-', code: 'Digit6', ctrlKey: true }), true);

const futureDefinitions = JSON.parse(JSON.stringify(context.serialMonitorShortcutDefinitions));
futureDefinitions.find(definition => definition.action === 'GoTo').gestures.push('F2');
const future = catalog.createCatalog(futureDefinitions);
assert.equal(catalog.matches('GoTo', eventFor('F2')), false);
assert.equal(future.matches('GoTo', eventFor('F2')), true);
assert.equal(future.userShortcut(eventFor('F2')), null);

const digitDefinitions = JSON.parse(JSON.stringify(context.serialMonitorShortcutDefinitions));
digitDefinitions.find(definition => definition.action === 'GoTo').gestures.push('6');
const digitReserved = catalog.createCatalog(digitDefinitions);
assert.equal(digitReserved.matches('GoTo', { key: '-', code: 'Digit6' }), true);
assert.equal(digitReserved.userShortcut({ key: '-', code: 'Digit6' }), null);

let nativeBindings = 0;
if (process.argv[2]) {
  const bindings = JSON.parse(fs.readFileSync(path.resolve(process.argv[2]), 'utf8'));
  const nativeReserved = new Set();
  for (const binding of bindings) {
    assert.equal(catalog.matches(binding.action, eventFor(binding.gesture)), true, `${binding.action}: ${binding.gesture}`);
    nativeReserved.add(binding.gesture.toUpperCase());
  }
  assert.deepEqual([...nativeReserved].sort(), [...catalog.reservedKeys].sort(), 'Native and browser reservations must match exactly');
  nativeBindings = bindings.length;
}
console.log(JSON.stringify({ actions: context.serialMonitorShortcutDefinitions.length,
  reservedGestures: catalog.reservedKeys.length, nativeBindings, futureShortcutAutomaticallyReserved: true,
  nonEnglishNumberRows: Object.keys(numberRows) }));
