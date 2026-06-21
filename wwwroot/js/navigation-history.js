(function (global) {
  const DEFAULT_MAX_HISTORY = 60;

  function nextPageHistory(historyStack = [], previousPage = '', nextPage = '', options = {}) {
    const stack = [...historyStack];
    const maxLength = Math.max(1, Number(options.maxLength) || DEFAULT_MAX_HISTORY);
    if (previousPage && previousPage !== nextPage && !options.skipHistory) {
      if (stack.at(-1) !== previousPage) stack.push(previousPage);
      while (stack.length > maxLength) stack.shift();
    }
    return stack;
  }

  function popBackTarget(historyStack = [], fallbackPage = 'dashboard') {
    const stack = [...historyStack];
    return {
      page: stack.pop() || fallbackPage,
      history: stack,
    };
  }

  function pageHash(page = 'dashboard') {
    return page === 'dashboard' ? '' : `#${encodeURIComponent(page)}`;
  }

  function pageFromStateOrHash(state = null, hash = '') {
    return state?.page || decodeURIComponent(String(hash || '').replace(/^#/, '')) || 'dashboard';
  }

  function browserHistoryMode(currentPage = '', historyStatePage = '', options = {}) {
    if (options.fromPop) return 'none';
    if (options.replace || !historyStatePage) return 'replace';
    if (currentPage !== historyStatePage) return 'push';
    return 'none';
  }

  function historyAfterPop(historyStack = [], targetPage = '') {
    const stack = [...historyStack];
    if (stack.at(-1) === targetPage) stack.pop();
    return stack;
  }

  global.EpataNavigationHistory = {
    browserHistoryMode,
    historyAfterPop,
    nextPageHistory,
    pageFromStateOrHash,
    pageHash,
    popBackTarget,
  };
})(globalThis);
