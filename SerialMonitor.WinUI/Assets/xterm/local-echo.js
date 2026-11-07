/* Display-only editing for non-echoing devices. Wire data is never buffered or
 * rewritten here. A device response relinquishes ownership of the local span.
 */
(function (root) {
  'use strict';
  class LocalEcho {
    constructor(terminal) {
      this.terminal = terminal;
      this.segmenter = new Intl.Segmenter(undefined, { granularity: 'grapheme' });
      this.reset();
    }
    reset() {
      this.marker?.dispose();
      this.marker = null;
      this.column = 0;
      this.characters = [];
      this.cursor = 0;
      this.renderedWidths = [];
    }
    needsWrap() {
      return !this.marker && this.terminal.buffer.active.cursorX >= this.terminal.cols;
    }
    width(text) {
      // Use the same Unicode provider as the pinned bundled xterm, rather than
      // a second wcwidth table that disagrees on wide/combining characters.
      return this.terminal._core.unicodeService.getStringCellWidth(text);
    }
    position(widths, count = widths.length) {
      let row = 0, column = this.column;
      for (let i = 0; i < count; i++) {
        if (column >= this.terminal.cols || column + widths[i] > this.terminal.cols) {
          row++; column = 0;
        }
        column += widths[i];
      }
      return { row, column };
    }
    move(originRow, position) {
      if (position.column >= this.terminal.cols) position = { row: position.row + 1, column: 0 };
      return `\x1b[${originRow + position.row + 1};${position.column + 1}H`;
    }
    insert(text) {
      if (!text) return;
      const before = this.characters.slice(0, this.cursor).join('') + text;
      if (before.length + this.characters.slice(this.cursor).join('').length > 16_384)
        throw new Error('Local echo input exceeds 16,384 characters.');
      const next = Array.from(this.segmenter.segment(before), value => value.segment);
      if (!this.cursor && next.length && !this.width(next[0])) next[0] = '\u25cc' + next[0];
      this.characters = next.concat(this.characters.slice(this.cursor));
      this.cursor = next.length;
    }
    render(data) {
      if (data === '\r' || data === '\n' || data === '\r\n' || data === '\x03') {
        let prefix = '';
        if (this.marker && !this.marker.isDisposed) {
          const end = this.position(this.renderedWidths);
          const interruptNeedsWrap = data === '\x03' && end.column >= this.terminal.cols;
          // CR clears pending wrap, so a full final row needs no extra row.
          end.column = Math.min(end.column, this.terminal.cols - 1);
          prefix = this.move(this.marker.line - this.terminal.buffer.active.baseY, end);
          if (interruptNeedsWrap) prefix += '\r\n';
        }
        this.reset();
        return prefix + (data === '\x03' ? '^C\r\n' : '\r\n');
      }
      const previousCursor = this.cursor;
      switch (data) {
        case '\x1b[D': case '\x1bOD': this.cursor = Math.max(0, this.cursor - 1); break;
        case '\x1b[C': case '\x1bOC': this.cursor = Math.min(this.characters.length, this.cursor + 1); break;
        case '\x1b[H': case '\x1bOH': case '\x1b[1~': this.cursor = 0; break;
        case '\x1b[F': case '\x1bOF': case '\x1b[4~': this.cursor = this.characters.length; break;
        case '\x1b[3~':
          if (this.cursor === this.characters.length) return '';
          this.characters.splice(this.cursor, 1); break;
        default:
          if (data.startsWith('\x1b')) { this.reset(); return ''; }
          let printable = '';
          for (const { segment } of this.segmenter.segment(data)) {
            if (segment === '\b' || segment === '\x7f') {
              this.insert(printable); printable = '';
              if (this.cursor) this.characters.splice(--this.cursor, 1);
            } else if (segment.codePointAt(0) >= 0x20) {
              printable += segment;
            }
          }
          this.insert(printable);
      }
      if (!this.characters.length && !this.renderedWidths.length && previousCursor === this.cursor) return '';
      const text = this.characters.join('');
      if (text.length > 16_384) throw new Error('Local echo input exceeds 16,384 characters.');
      if (!this.marker) {
        this.marker = this.terminal.registerMarker(0);
        this.column = this.terminal.buffer.active.cursorX;
      }
      const originRow = this.marker?.line - this.terminal.buffer.active.baseY;
      if (!this.marker || this.marker.isDisposed || originRow < 0)
        throw new Error('Local echo input has left the editable viewport.');
      // Erase only cells previously drawn by us, including wrapped rows; the
      // prompt, earlier logs and text outside this span are never cleared.
      let result = '', remaining = this.renderedWidths.reduce((sum, width) => sum + width, 0);
      let row = 0, column = this.column;
      for (const width of this.renderedWidths) {
        if (column >= this.terminal.cols || column + width > this.terminal.cols) {
          result += `\x1b[${originRow + row + 1};${(row ? 0 : this.column) + 1}H\x1b[0K`;
          row++; column = 0;
        }
        column += width;
      }
      if (remaining) {
        const start = row ? 0 : this.column;
        result += `\x1b[${originRow + row + 1};${start + 1}H\x1b[${column - start}X`;
      }
      const widths = this.characters.map(character => this.width(character));
      result += this.move(originRow, { row: 0, column: this.column }) + text;
      if (this.cursor < this.characters.length || !text)
        result += this.move(originRow, this.position(widths, this.cursor));
      this.renderedWidths = widths;
      return result;
    }
  }
  root.SerialMonitorLocalEcho = LocalEcho;
})(globalThis);
