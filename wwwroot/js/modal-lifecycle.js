(function (global) {
  const FOCUSABLE_SELECTOR = 'button, [href], input, select, textarea, [tabindex]:not([tabindex="-1"])';
  const previousFocusByModal = new WeakMap();

  function isModalOpen(modal) {
    return !!modal && !modal.classList?.contains('hidden') && !modal.hidden;
  }

  function isFocusable(element) {
    if (!element || element.disabled) return false;
    if (element.hidden || element.classList?.contains('hidden')) return false;
    if (element.getAttribute?.('aria-hidden') === 'true') return false;
    return true;
  }

  function firstFocusable(modal, selectors = []) {
    const preferred = Array.isArray(selectors) ? selectors : [selectors].filter(Boolean);
    for (const selector of preferred) {
      const match = [...(modal.querySelectorAll?.(selector) || [])].find(isFocusable);
      if (match) return match;
    }
    return [...(modal.querySelectorAll?.(FOCUSABLE_SELECTOR) || [])].find(isFocusable) || modal;
  }

  function focusFirstModalControl(modal, selectors = []) {
    if (!modal) return null;
    if (!modal.getAttribute?.('tabindex')) modal.setAttribute?.('tabindex', '-1');
    const target = firstFocusable(modal, selectors);
    target?.focus?.();
    return target;
  }

  function focusableElements(modal) {
    return [...(modal?.querySelectorAll?.(FOCUSABLE_SELECTOR) || [])].filter(isFocusable);
  }

  function trapModalFocus(modal, event, options = {}) {
    if (!isModalOpen(modal) || event?.key !== 'Tab') return false;

    const documentRef = options.documentRef || global.document;
    const focusables = focusableElements(modal);
    if (!focusables.length) {
      event.preventDefault?.();
      modal.focus?.();
      return true;
    }

    const first = focusables[0];
    const last = focusables[focusables.length - 1];
    const active = documentRef?.activeElement;

    if (event.shiftKey && (active === first || active === modal || !focusables.includes(active))) {
      event.preventDefault?.();
      last.focus?.();
      return true;
    }

    if (!event.shiftKey && (active === last || active === modal || !focusables.includes(active))) {
      event.preventDefault?.();
      first.focus?.();
      return true;
    }

    return false;
  }

  function openModalSurface(modal, backdrop, options = {}) {
    if (!modal || !backdrop) return null;
    const documentRef = options.documentRef || global.document;
    const opener = options.opener || documentRef?.activeElement || null;
    if (opener && opener !== documentRef?.body) previousFocusByModal.set(modal, opener);

    backdrop.classList?.remove('hidden');
    modal.classList?.remove('hidden');
    return focusFirstModalControl(modal, options.preferredSelectors || []);
  }

  function closeModalSurface(modal, backdrop, options = {}) {
    if (!modal || !backdrop) return null;
    const wasOpen = isModalOpen(modal);
    backdrop.classList?.add('hidden');
    modal.classList?.add('hidden');
    if (!wasOpen || options.restoreFocus === false) return null;

    const opener = previousFocusByModal.get(modal);
    previousFocusByModal.delete(modal);
    if (opener?.focus && (opener.isConnected ?? true)) {
      opener.focus();
      return opener;
    }
    return null;
  }

  global.EpataModalLifecycle = {
    FOCUSABLE_SELECTOR,
    closeModalSurface,
    focusableElements,
    focusFirstModalControl,
    isModalOpen,
    openModalSurface,
    trapModalFocus,
  };
})(globalThis);
