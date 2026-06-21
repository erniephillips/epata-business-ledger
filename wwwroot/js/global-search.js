(function (global) {
  const titleFields = [
    'customerName',
    'vendorName',
    'name',
    'title',
    'invoiceNumber',
    'orderNumber',
    'jobName',
    'productName',
    'fileName',
  ];

  const subtitleFields = [
    'invoiceNumber',
    'orderNumber',
    'relatedOrderNumber',
    'relatedInvoiceNumber',
    'relatedRecordNumber',
    'status',
    'platform',
  ];

  function normalizeSearchQuery(value = '') {
    return String(value || '').trim().toLowerCase();
  }

  function rowMatchesQuery(row = {}, rawQuery = '') {
    const query = normalizeSearchQuery(rawQuery);
    if (!query) return false;
    try {
      return JSON.stringify(row || {}).toLowerCase().includes(query);
    } catch {
      return false;
    }
  }

  function searchRowsByConfig(entries = [], rawQuery = '', maxPerConfig = 8) {
    const limit = Math.max(1, Number(maxPerConfig) || 8);
    return entries.flatMap(entry => {
      const rows = Array.isArray(entry.rows) ? entry.rows : [];
      return rows
        .filter(row => rowMatchesQuery(row, rawQuery))
        .slice(0, limit)
        .map(row => ({ key: entry.key, config: entry.config, row }));
    });
  }

  function firstPresent(row = {}, fields = []) {
    for (const field of fields) {
      const value = row?.[field];
      if (value !== undefined && value !== null && String(value).trim()) return value;
    }
    return '';
  }

  function resultTitle(row = {}, fallbackTitle = '') {
    return firstPresent(row, titleFields) || fallbackTitle;
  }

  function resultSubtitleParts(row = {}) {
    return subtitleFields
      .map(field => row?.[field])
      .filter(value => value !== undefined && value !== null && String(value).trim())
      .map(value => String(value));
  }

  function safeRoute(route = '') {
    return String(route || '').replace(/[^A-Za-z0-9_-]/g, '');
  }

  function resultOpenCall(route = '', row = {}) {
    const page = safeRoute(route);
    const id = Number(row?.id || 0);
    if (page && id > 0) return `openLedgerEntityRecord('${page}', ${id})`;
    return `showPage('${page || 'dashboard'}')`;
  }

  global.EpataGlobalSearch = {
    normalizeSearchQuery,
    resultOpenCall,
    resultSubtitleParts,
    resultTitle,
    rowMatchesQuery,
    searchRowsByConfig,
  };
})(globalThis);
