(function (global) {
  function normalizeText(value) {
    return String(value ?? '').trim().toLowerCase();
  }

  function rowBlob(row) {
    return JSON.stringify(row ?? {}).toLowerCase();
  }

  function statusBlob(row) {
    return Object.entries(row ?? {})
      .filter(([key]) => /status$/i.test(key))
      .map(([, value]) => normalizeText(value))
      .filter(Boolean)
      .join(' ');
  }

  function matchesStatus(row, statusFilter = '') {
    const filter = normalizeText(statusFilter);
    if (!filter) return true;

    const statuses = statusBlob(row);
    if (filter === 'archived') return row?.isArchived === true;
    if (row?.isArchived === true) return false;
    if (filter === 'needs-review') return row?.needsReview === true || statuses.includes('review');
    if (filter === 'open') return /open|sent|unpaid|partial|waiting|lead|quoted|progress/.test(statuses);
    if (filter === 'paid') return /paid|done|completed|fulfilled|refunded/.test(statuses);
    return true;
  }

  function filterRows(rows = [], filter = '', statusFilter = '') {
    const term = normalizeText(filter);
    return rows.filter(row => {
      const blob = rowBlob(row);
      if (term && !blob.includes(term)) return false;
      return matchesStatus(row, statusFilter);
    });
  }

  function clampPage(page, totalRows, pageSize) {
    const size = Math.max(1, Number(pageSize) || 25);
    const totalPages = Math.max(1, Math.ceil(Number(totalRows || 0) / size));
    return Math.min(Math.max(0, Number(page) || 0), totalPages - 1);
  }

  function pageWindow(totalRows, page, pageSize) {
    const size = Math.max(1, Number(pageSize) || 25);
    const safePage = clampPage(page, totalRows, size);
    return {
      page: safePage,
      pageSize: size,
      totalPages: Math.max(1, Math.ceil(Number(totalRows || 0) / size)),
      startRow: totalRows ? (safePage * size) + 1 : 0,
      endRow: Math.min(Number(totalRows || 0), (safePage + 1) * size),
    };
  }

  function buildEntityTableModel(rows = [], options = {}) {
    const state = options.state || {};
    const pageSize = Math.max(1, Number(state.pageSize || options.pageSize) || 25);
    const filteredRows = filterRows(rows, options.filter || '', options.statusFilter || '');
    const sortedRows = typeof options.sortRows === 'function'
      ? options.sortRows(filteredRows)
      : [...filteredRows];
    const windowInfo = pageWindow(sortedRows.length, state.page, pageSize);
    const pageRows = sortedRows.slice(
      windowInfo.page * windowInfo.pageSize,
      (windowInfo.page + 1) * windowInfo.pageSize,
    );

    return {
      ...windowInfo,
      filteredRows,
      sortedRows,
      pageRows,
      totalRows: sortedRows.length,
    };
  }

  global.EpataEntityTableState = {
    buildEntityTableModel,
    clampPage,
    filterRows,
    matchesStatus,
    normalizeText,
    pageWindow,
  };
})(globalThis);
