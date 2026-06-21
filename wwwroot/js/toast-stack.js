(function (global) {
  const DEFAULT_DURATION = 3200;

  function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>'"]/g, character => ({
      '&': '&amp;',
      '<': '&lt;',
      '>': '&gt;',
      "'": '&#39;',
      '"': '&quot;',
    }[character]));
  }

  function ensureContainer(documentRef = global.document) {
    if (!documentRef) return null;

    const existing = documentRef.getElementById('toast-container');
    if (existing) return existing;

    const legacy = documentRef.getElementById('toast');
    if (legacy) {
      legacy.id = 'toast-container';
      legacy.className = 'toast-container';
      legacy.textContent = '';
      return legacy;
    }

    const container = documentRef.createElement('div');
    container.id = 'toast-container';
    container.className = 'toast-container';
    documentRef.body?.appendChild(container);
    return container;
  }

  function showToast(message, options = {}) {
    const documentRef = options.documentRef || global.document;
    const setTimeoutFn = options.setTimeoutFn || global.setTimeout;
    const container = ensureContainer(documentRef);
    if (!container) return null;

    const type = options.type || 'info';
    const duration = Number.isFinite(options.duration) ? options.duration : DEFAULT_DURATION;
    const toast = documentRef.createElement('div');
    toast.className = `toast ${type}`;
    toast.setAttribute('role', type === 'error' ? 'alert' : 'status');
    toast.setAttribute('aria-live', type === 'error' ? 'assertive' : 'polite');
    toast.innerHTML = `<span class="toast-text">${escapeHtml(message)}</span>`;
    container.appendChild(toast);

    if (duration > 0 && typeof setTimeoutFn === 'function') {
      setTimeoutFn(() => {
        toast.style.opacity = '0';
        toast.style.transition = 'opacity 0.25s ease';
        setTimeoutFn(() => toast.remove(), 260);
      }, duration);
    }

    return toast;
  }

  global.EpataToastStack = {
    escapeHtml,
    ensureContainer,
    showToast,
  };
})(globalThis);
