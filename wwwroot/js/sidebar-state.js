(function (global) {
  function resolveSidebarCollapsed(savedValue, defaultCollapsed = false) {
    if (savedValue === null || savedValue === undefined) return !!defaultCollapsed;
    return String(savedValue) === 'true';
  }

  function persistSidebarCollapsed(storage, key, collapsed) {
    const value = String(!!collapsed);
    storage?.setItem?.(key, value);
    return value;
  }

  function sidebarExpandedState(state = {}) {
    if (state.isMobile) return !!state.sidebarOpen;
    return !state.sidebarCollapsed;
  }

  global.EpataSidebarState = {
    persistSidebarCollapsed,
    resolveSidebarCollapsed,
    sidebarExpandedState,
  };
})(globalThis);
