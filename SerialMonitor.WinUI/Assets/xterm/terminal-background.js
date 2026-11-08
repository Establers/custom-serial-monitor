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
      this.layout = null;
    }

    set(settings) {
      if (!settings || typeof settings.source !== 'string' || settings.source.length > 4096 ||
          !Number.isFinite(settings.opacity) || settings.opacity < 0 || settings.opacity > 1) return false;
      const layout = settings.layout;
      if (layout != null && (!['left', 'top', 'width', 'height'].every(key => Number.isFinite(layout[key])) ||
          layout.width <= 0 || layout.height <= 0)) return false;
      if (settings.source) {
        try {
          const url = new URL(settings.source);
          if (url.protocol !== 'https:' || !['serialmonitor-background.local', 'serialmonitor-background-bundled.local'].includes(url.hostname)) return false;
        } catch { return false; }
      }

      this.opacity = settings.opacity;
      this.layout = layout == null ? null : { left: layout.left, top: layout.top, width: layout.width, height: layout.height };
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
      // The native image is fitted to the whole window. Crop that same image
      // through the terminal viewport rather than fitting another copy here.
      const layout = this.layout || { left: 0, top: 0, width: 1, height: 1 };
      this.image.style.left = `${layout.left * 100}%`;
      this.image.style.top = `${layout.top * 100}%`;
      this.image.style.width = `${layout.width * 100}%`;
      this.image.style.height = `${layout.height * 100}%`;
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
