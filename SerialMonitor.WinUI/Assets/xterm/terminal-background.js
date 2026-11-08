// One image layer behind the terminal. Only this layer receives opacity;
// terminal text, ANSI highlights and selection remain fully opaque.
(() => {
  'use strict';
  class SerialMonitorTerminalBackground {
    constructor(image, forcedColors, onVisibilityChanged, onError) {
      this.image = image;
      this.forcedColors = forcedColors;
      this.onVisibilityChanged = onVisibilityChanged;
      this.onError = onError;
      this.source = '';
      this.opacity = 0;
      this.loaded = false;
      this.visible = false;
      this.pending = null;
      this.generation = 0;
    }

    set(settings) {
      if (!settings || typeof settings.source !== 'string' || settings.source.length > 4096 ||
          !Number.isFinite(settings.opacity) || settings.opacity < 0 || settings.opacity > 1) return false;
      if (settings.source) {
        try {
          const url = new URL(settings.source);
          if (url.protocol !== 'https:' || url.hostname !== 'serialmonitor-background.local') return false;
        } catch { return false; }
      }

      this.opacity = settings.opacity;
      if (this.source === settings.source) {
        this.refresh();
        return true;
      }

      this.source = settings.source;
      this.loaded = false;
      const generation = ++this.generation;
      if (this.pending) {
        this.pending.onload = this.pending.onerror = null;
        this.pending.removeAttribute('src');
        this.pending = null;
      }
      this.image.removeAttribute('src');
      this.refresh();
      if (!this.source) return true;

      const image = new Image();
      image.id = this.image.id;
      image.alt = '';
      image.setAttribute('aria-hidden', 'true');
      image.draggable = false;
      image.decoding = 'async';
      image.hidden = true;
      this.pending = image;
      image.onload = () => {
        if (generation !== this.generation) return;
        image.onload = image.onerror = null;
        this.pending = null;
        this.image.replaceWith(image);
        this.image = image;
        this.loaded = true;
        this.refresh();
      };
      image.onerror = () => {
        if (generation !== this.generation) return;
        image.onload = image.onerror = null;
        this.pending = null;
        this.loaded = false;
        this.refresh();
        this.onError();
      };
      image.src = this.source;
      return true;
    }

    refresh() {
      const visible = this.loaded && this.opacity > 0 && !this.forcedColors.matches;
      this.image.style.opacity = String(this.opacity);
      this.image.hidden = !visible;
      document.documentElement.dataset.terminalBackground = String(visible);
      if (this.visible !== visible) {
        this.visible = visible;
        this.onVisibilityChanged();
      }
    }
  }
  window.SerialMonitorTerminalBackground = SerialMonitorTerminalBackground;
})();
