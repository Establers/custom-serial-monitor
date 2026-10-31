// Raw keyboard translation lives here. App handlers match action IDs, never keys.
(() => {
  'use strict';
  const CTRL = 1, ALT = 2, SHIFT = 4, WIN = 8;
  const modifier = text => {
    const flags = { CTRL, CONTROL: CTRL, ALT, MENU: ALT, SHIFT, WIN, WINDOWS: WIN, META: WIN };
    const value = flags[String(text).toUpperCase()];
    if (!value) throw new Error(`Invalid shortcut modifier: ${text}`);
    return value;
  };
  const keyName = input => {
    let key = String(input || '').toUpperCase();
    if (key === ' ') return 'SPACE';
    if (key === '+') return 'PLUS';
    if (key === '-') return 'MINUS';
    if (key === 'ESC') return 'ESCAPE';
    if (key === 'RETURN') return 'ENTER';
    if (/^ARROW(LEFT|RIGHT|UP|DOWN)$/.test(key)) key = key.slice(5);
    return /^[A-Z0-9]$/.test(key) || /^F([1-9]|1[0-9]|2[0-4])$/.test(key) ||
      /^(BACKSPACE|ENTER|TAB|SPACE|ESCAPE|INSERT|DELETE|HOME|END|PAGEUP|PAGEDOWN|LEFT|RIGHT|UP|DOWN|PLUS|MINUS)$/.test(key) ? key : null;
  };
  const parse = input => {
    const parts = String(input).split('+').map(value => value.trim());
    const key = keyName(parts.pop());
    if (!key) throw new Error(`Invalid shortcut key: ${input}`);
    let flags = 0;
    for (const part of parts) {
      const flag = modifier(part);
      if (flags & flag) throw new Error(`Duplicate shortcut modifier: ${input}`);
      flags |= flag;
    }
    return { key, flags };
  };
  const format = (key, flags) => `${flags & CTRL ? 'CTRL+' : ''}${flags & ALT ? 'ALT+' : ''}${flags & SHIFT ? 'SHIFT+' : ''}${flags & WIN ? 'WIN+' : ''}${key}`;
  const gesture = event => {
    let key = keyName(event.key);
    const flags = (event.ctrlKey ? CTRL : 0) | (event.altKey ? ALT : 0) |
      (event.shiftKey ? SHIFT : 0) | (event.metaKey ? WIN : 0);
    // WinUI records unmodified number-row keys as VirtualKey.NumberN, even
    // when the layout produces &, é or -. Limit this fallback to one printable
    // character: retain modified shortcuts, IME/dead keys and numpad navigation.
    if (flags === 0 && (key === null || key === 'PLUS' || key === 'MINUS') &&
        typeof event.key === 'string' && event.key.length === 1) {
      const digit = /^Digit([0-9])$/.exec(event.code || '');
      if (digit) key = digit[1];
    }
    return { key, flags };
  };

  function createCatalog(definitions) {
    const actions = new Map(), reserved = new Set();
    for (const definition of definitions) {
      if (!definition.action || actions.has(definition.action) || !definition.gestures?.length)
        throw new Error('Invalid or duplicate shortcut action');
      const gestures = new Set();
      actions.set(definition.action, gestures);
      let ignored = 0;
      for (const value of definition.ignoreModifiers || []) ignored |= modifier(value);
      for (const input of definition.gestures) {
        const required = parse(input);
        if (required.flags & ignored) throw new Error(`Required modifier cannot be ignored: ${input}`);
        for (let flags = 0; flags < 16; flags++) {
          const matched = definition.acceptMetaAsControl && (flags & WIN) ? ((flags & ~WIN) | CTRL) : flags;
          if ((matched & ~ignored) !== required.flags) continue;
          const text = format(required.key, flags);
          gestures.add(text);
          reserved.add(text);
        }
      }
    }
    return Object.freeze({
      matches(action, event) {
        if (!actions.has(action)) throw new Error(`Unknown shortcut action: ${action}`);
        const input = gesture(event);
        return !event.isComposing && input.key !== null && actions.get(action).has(format(input.key, input.flags));
      },
      userShortcut(event) {
        if (event.isComposing) return null;
        const input = gesture(event);
        if (input.key === null || input.key === 'PLUS' || input.key === 'MINUS' || input.flags & (SHIFT | WIN)) return null;
        const text = format(input.key, input.flags);
        if (reserved.has(text)) return null;
        if (input.flags === 0 || input.flags === CTRL && /^[0-9]$/.test(input.key) ||
            input.flags === ALT && /^[A-Z0-9]$/.test(input.key)) return text.replace('CTRL+', 'Ctrl+').replace('ALT+', 'Alt+');
        return null;
      },
      reservedKeys: Object.freeze(Array.from(reserved)),
      createCatalog
    });
  }
  globalThis.serialMonitorShortcuts = createCatalog(globalThis.serialMonitorShortcutDefinitions);
})();
