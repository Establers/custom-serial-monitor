// Console preferences affect physical keys and the interactive RX display only.
// Pasted data, retained normal logs and byte-exact logging keep their own paths.
(function (root) {
  'use strict';
  class ConsoleInput {
    constructor() {
      this.enter = 'Cr';
      this.backspace = 'Del';
      this.receiveNewline = 'Preserve';
      this.resetReceive();
    }
    configure(settings) {
      if (!settings || !['Cr', 'Lf', 'CrLf'].includes(settings.enter) ||
          !['Del', 'Bs'].includes(settings.backspace) ||
          !['Preserve', 'Lf', 'Cr', 'Auto'].includes(settings.receiveNewline)) return false;
      if (this.receiveNewline !== settings.receiveNewline) this.resetReceive();
      this.enter = settings.enter;
      this.backspace = settings.backspace;
      this.receiveNewline = settings.receiveNewline;
      return true;
    }
    keyData(event) {
      // Ctrl/Alt shortcuts and escape sequences retain xterm's own handling.
      if (event.type !== 'keydown') return null;
      if (root.serialMonitorShortcuts.matches('ConsoleEnter', event))
        return this.enter === 'Lf' ? '\n' : this.enter === 'CrLf' ? '\r\n' : '\r';
      if (root.serialMonitorShortcuts.matches('ConsoleBackspace', event))
        return this.backspace === 'Bs' ? '\b' : '\x7f';
      return null;
    }
    resetReceive() { this.previousWasCr = false; }
    normalizeReceived(text) {
      if (this.receiveNewline === 'Preserve') return text;
      let result = '';
      for (const character of text) {
        if (character === '\r') {
          result += this.receiveNewline === 'Lf' ? '\r' : '\r\n';
        } else if (character === '\n') {
          if (this.previousWasCr) result += this.receiveNewline === 'Lf' ? '\n' : '';
          else result += this.receiveNewline === 'Cr' ? '\n' : '\r\n';
        } else result += character;
        // Keep only one bit across bounded RX batches. A split CRLF remains
        // one newline, without delaying a lone CR or buffering incoming text.
        this.previousWasCr = character === '\r';
      }
      return result;
    }
  }
  root.SerialMonitorConsoleInput = ConsoleInput;
})(globalThis);
