/* Snapshot the rendered terminal, not the retained RX/TX records. Matching is
 * performed by the existing bounded/cancellable host search engine, so case,
 * whole-word and regex options have the same semantics in both modes. */
(function (root) {
  'use strict';
  class DirectSearch {
    constructor(terminal, selectMatch, changed) {
      this.terminal = terminal;
      this.selectMatch = selectMatch;
      this.changed = changed;
      this.sessionId = 0; this.snapshotId = 0; this.revision = 0;
      this.entries = new Map();
      this.writeListener = terminal.onWriteParsed(() => this.markChanged());
      this.resizeListener = terminal.onResize(() => { this.clear(); this.markChanged(); });
    }
    clear() {
      for (const value of this.entries.values()) value.marker?.dispose();
      this.entries.clear();
      this.snapshotId++;
    }
    setSession(sessionId) {
      if (sessionId === this.sessionId) return;
      this.clear();
      clearTimeout(this.timer); this.timer = 0; this.notified = false;
      this.sessionId = sessionId; this.revision = 0;
    }
    markChanged() {
      if (!this.sessionId) return;
      this.revision++;
      if (this.notified || this.timer) return;
      this.timer = setTimeout(() => {
        this.timer = 0; this.notified = true;
        this.changed({ type: 'terminalSearchChanged', sessionId: this.sessionId, revision: this.revision });
      }, 50);
    }
    readLogicalLine(startRow) {
      const buffer = this.terminal.buffer.active;
      const rows = [], stringStarts = [];
      let logicalText = '', row = startRow;
      do {
        const line = buffer.getLine(row);
        if (!line) break;
        rows.push(row); stringStarts.push(logicalText.length);
        const next = buffer.getLine(row + 1);
        const continues = !!next?.isWrapped;
        const last = line.getCell(this.terminal.cols - 1);
        const widePlaceholder = continues && last?.getCode() === 0 && last.getWidth() === 1 && next.getCell(0)?.getWidth() === 2;
        // trimRight removes only empty padding cells, preserving real spaces and
        // private-use characters. Wrapped rows retain their interior padding.
        logicalText += line.translateToString(!continues, 0, this.terminal.cols - (widePlaceholder ? 1 : 0));
        row++;
        if (!continues) break;
      } while (row < buffer.length);
      return { rows, stringStarts, logicalText };
    }
    text(logical) { return logical.logicalText; }
    capture(sessionId) {
      if (!sessionId || sessionId !== this.sessionId) throw new Error('The direct terminal session has ended.');
      this.clear();
      this.terminal.clearSelection();
      clearTimeout(this.timer); this.timer = 0; this.notified = false;
      const buffer = this.terminal.buffer.active;
      this.buffer = buffer;
      const lines = [];
      let characters = 0;
      for (let row = 0; row < buffer.length;) {
        const logical = this.readLogicalLine(row);
        if (!logical.rows.length) break;
        const text = this.text(logical);
        characters += text.length;
        if (characters > 16 * 1024 * 1024) {
          this.clear();
          throw new Error('Terminal search exceeds 16 Mi characters. Reduce terminal scrollback and retry.');
        }
        const lineId = lines.length + 1;
        lines.push({ lineId, text });
        this.entries.set(String(lineId), {
          text, row,
          marker: buffer.type === 'normal'
            ? this.terminal.registerMarker(row - (buffer.baseY + buffer.cursorY)) : null
        });
        row = logical.rows[logical.rows.length - 1] + 1;
      }
      return { ok: true, sessionId, snapshotId: this.snapshotId, revision: this.revision, lines };
    }
    select(request) {
      if (request.terminalSessionId !== this.sessionId || request.terminalSnapshotId !== this.snapshotId ||
          this.terminal.buffer.active !== this.buffer) return false;
      const entry = this.entries.get(String(request.targetLineId));
      if (!entry || entry.marker?.isDisposed ||
          !Number.isInteger(request.targetPayloadOffset) || !Number.isInteger(request.matchLength)) return false;
      const row = entry.marker ? entry.marker.line : entry.row;
      const logical = this.readLogicalLine(row);
      if (this.text(logical) !== entry.text) return false;
      const start = request.targetPayloadOffset, end = start + request.matchLength;
      if (start < 0 || end > entry.text.length || entry.text.slice(start, end) !== request.expectedText) return false;
      this.selectMatch({ rows: logical.rows, stringStarts: logical.stringStarts, startOffset: start, endOffset: end });
      return true;
    }
    dispose() {
      clearTimeout(this.timer); this.clear();
      this.writeListener.dispose(); this.resizeListener.dispose();
    }
  }
  root.SerialMonitorDirectSearch = DirectSearch;
})(globalThis);
