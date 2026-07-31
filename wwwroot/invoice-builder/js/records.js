// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Records View
// ═══════════════════════════════════════════════════════

import { el, money, escapeHtml, fmtDate, fmtDateTime, statusBadge, typeBadge, toast } from './utils.js?v=2';
import { api } from './api.js?v=6';

export const RECORD_PAGE_SIZES = [10, 25, 50, 100];
export const RECORD_SORT_KEYS = ['updated', 'created', 'number', 'type', 'status', 'total', 'paid', 'customer', 'project'];
export const RECORD_CSV_COLUMNS = ['DocNumber', 'DocType', 'Status', 'CustomerName', 'ProjectName', 'Total', 'AmountPaid', 'Balance', 'DocDate', 'UpdatedAt'];

let _records  = [];
let _onLoad   = null;
let _onNew    = null;
let _sortBy   = 'updated';
let _sortDir  = 'desc';
let _page     = 0;
let _pageSize = 25;
const _recordActionsInFlight = new Set();

export function initRecords({ onLoad, onNew }) {
  _onLoad = onLoad;
  _onNew  = onNew;

  el('recSearch')?.addEventListener('input', () => { _page = 0; render(); });
  el('recType')?.addEventListener('change', () => { _page = 0; render(); });
  el('recStatus')?.addEventListener('change', () => { _page = 0; render(); });
  el('recIncludeArchived')?.addEventListener('change', refreshRecords);
  el('recordsBody')?.addEventListener('click', onRecordsBodyClick);
  el('recSortBy')?.addEventListener('change', e => {
    _sortBy = e.target.value || 'updated';
    _sortDir = ['customer', 'project'].includes(_sortBy) ? 'asc' : 'desc';
    _page = 0;
    render();
  });
  document.querySelectorAll('[data-record-sort]').forEach(btn => {
    btn.addEventListener('click', () => {
      const next = btn.dataset.recordSort || 'updated';
      _sortDir = _sortBy === next && _sortDir === 'asc' ? 'desc' : 'asc';
      _sortBy = next;
      _page = 0;
      const dropdown = el('recSortBy');
      if (dropdown && [...dropdown.options].some(o => o.value === next)) dropdown.value = next;
      render();
    });
  });
}

export async function refreshRecords() {
  const includeArchived = !!el('recIncludeArchived')?.checked;
  _records = await api.list(includeArchived ? { includeArchived: 'true' } : {});
  render();
  updateFooterStats();
}

export function getRecords() { return _records; }

function findDocumentRecord(id) {
  const numericId = Number(id || 0);
  return _records.find(r => Number(r.id) === numericId && r.sourceKind !== 'receivable');
}

export function buildRecordViewModel(rows = [], state = {}) {
  const normalized = normalizeRecordViewState(state);
  const filteredRows = filterAndSortRecords(rows, normalized);
  const totalRows = filteredRows.length;
  const totalPages = Math.max(1, Math.ceil(totalRows / normalized.pageSize));
  const page = Math.min(Math.max(0, normalized.page), totalPages - 1);
  const startIndex = page * normalized.pageSize;
  const pageRows = filteredRows.slice(startIndex, startIndex + normalized.pageSize);

  return {
    state: { ...normalized, page },
    filteredRows,
    pageRows,
    totalRows,
    totalPages,
    startRow: totalRows ? startIndex + 1 : 0,
    endRow: Math.min(totalRows, startIndex + normalized.pageSize),
    footer: computeRecordFooterStats(rows),
  };
}

export function filterAndSortRecords(rows = [], state = {}) {
  const normalized = normalizeRecordViewState(state);
  const q = normalized.q;
  let out = [...rows];

  if (q) {
    out = out.filter(r => [r.docNumber, r.customerName, r.projectName].some(v => String(v || '').toLowerCase().includes(q)));
  }
  if (normalized.type) {
    out = out.filter(r => r.docType === normalized.type);
  }
  if (normalized.status) {
    out = out.filter(r => r.status === normalized.status);
  }

  const sortFns = {
    updated:  (a, b) => dateValue(a.updatedAt) - dateValue(b.updatedAt),
    created:  (a, b) => dateValue(a.createdAt) - dateValue(b.createdAt),
    number:   (a, b) => textCompare(a.docNumber, b.docNumber),
    type:     (a, b) => textCompare(a.docType, b.docType),
    status:   (a, b) => textCompare(a.status, b.status),
    total:    (a, b) => Number(a.total || 0) - Number(b.total || 0),
    paid:     (a, b) => Number(a.amountPaid || 0) - Number(b.amountPaid || 0),
    customer: (a, b) => textCompare(a.customerName, b.customerName),
    project:  (a, b) => textCompare(a.projectName, b.projectName),
  };

  const factor = normalized.sortDir === 'desc' ? -1 : 1;
  const compare = sortFns[normalized.sortBy] ?? sortFns.updated;
  return out.sort((a, b) => {
    const primary = compare(a, b);
    if (primary !== 0) return factor * primary;
    return textCompare(a.docNumber, b.docNumber);
  });
}

export function computeRecordFooterStats(rows = []) {
  const invoices = rows.filter(r => r.docType === 'INVOICE' && !r.isArchived);
  const activeInvoices = invoices.filter(r => !['Draft', 'Void'].includes(r.status));
  const unpaidInvoices = invoices.filter(r => !['Draft', 'Paid', 'Void'].includes(r.status));
  return {
    totalRecords: rows.length,
    activeInvoiceCount: activeInvoices.length,
    activeInvoiceTotal: activeInvoices.reduce((s, r) => s + Number(r.total || 0), 0),
    unpaidBalance: unpaidInvoices.reduce((s, r) => s + Math.max(0, Number(r.balance || 0)), 0),
  };
}

export function recordsToCsv(rows = [], state = {}) {
  const model = buildRecordViewModel(rows, state);
  return [
    RECORD_CSV_COLUMNS.join(','),
    ...model.filteredRows.map(r => RECORD_CSV_COLUMNS.map(c => csvCell(recordColumnValue(r, c))).join(',')),
  ].join('\n');
}

// ── Render ────────────────────────────────────────────
function render() {
  const tbody = el('recordsBody');
  if (!tbody) return;

  const model = buildRecordViewModel(_records, currentRecordViewState());
  _page = model.state.page;
  const rows = model.filteredRows;
  const count = el('recCount');
  if (count) count.textContent = `${rows.length} record${rows.length !== 1 ? 's' : ''}`;

  if (!rows.length) {
    tbody.innerHTML = `
      <tr><td colspan="9">
        <div class="empty-state">
          <div class="empty-icon">📄</div>
          <div class="empty-title">No records found</div>
          <div class="empty-desc">Try adjusting your filters or create a new estimate.</div>
        </div>
      </td></tr>`;
    renderPager(0);
    return;
  }

  tbody.innerHTML = model.pageRows.map(r => `
    <tr class="${r.sourceKind === 'receivable' ? 'ledger-record' : ''} ${r.isArchived ? 'archived-record' : ''}">
      <td class="doc-number">${escapeHtml(r.docNumber || '—')}</td>
      <td>${recordTypeCell(r)}${r.isArchived ? ` <span class="badge badge-gray" title="${escapeHtml(r.archiveReason || 'Archived record retained in the database.')}">Archived</span>` : ''}</td>
      <td>${statusBadge(r.status || 'Draft')}</td>
      <td>${customerCell(r.customerName)}</td>
      <td>${escapeHtml(r.projectName || '—')}</td>
      <td class="num">${money(r.total)}</td>
      <td class="num">${money(r.amountPaid)}</td>
      <td class="muted">${fmtDate(r.updatedAt)}</td>
      <td>
        <div class="actions">
          ${r.sourceKind === 'receivable'
            ? `<button class="btn-ghost btn-sm" onclick="window.openLedgerEntityRecord && window.openLedgerEntityRecord('receivables', ${r.sourceId || r.id})" title="Open the AR ledger row for payment/status tracking. This row does not have a builder PDF document.">Open AR Ledger</button>`
            : r.isArchived
              ? `<button type="button" class="btn-ghost btn-sm" data-record-action="restore" data-record-id="${r.id}">Restore</button>`
              : `<button type="button" class="btn-ghost btn-sm" data-record-action="load" data-record-id="${r.id}">Open</button>
                 ${r.docType === 'ESTIMATE' ? `<button type="button" class="btn-ghost btn-sm" data-record-action="convert" data-record-id="${r.id}">Create Invoice</button>` : ''}
                 <button type="button" class="btn-ghost btn-sm" data-record-action="duplicate" data-record-id="${r.id}" title="Duplicate" aria-label="Duplicate ${escapeHtml(r.docNumber || 'record')}">⧉</button>
                 <button type="button" class="btn-danger btn-sm" data-record-action="archive" data-record-id="${r.id}" title="Archive" aria-label="Archive ${escapeHtml(r.docNumber || 'record')}">✕</button>`}
        </div>
      </td>
    </tr>`).join('');
  renderPager(rows.length);
  updateSortHeaders();
}

function onRecordsBodyClick(event) {
  const button = event.target?.closest?.('[data-record-action][data-record-id]');
  if (!button || !el('recordsBody')?.contains(button)) return;

  event.preventDefault();
  const id = Number(button.dataset.recordId || 0);
  if (!Number.isFinite(id) || id <= 0) return;

  const action = button.dataset.recordAction;
  if (action === 'load') window._loadRecord?.(id);
  else if (action === 'convert') window._convertEstimate?.(id);
  else if (action === 'duplicate') window._dupeRecord?.(id);
  else if (action === 'archive') window._delRecord?.(id);
  else if (action === 'restore') window._restoreRecord?.(id);
}

function customerCell(name) {
  const clean = String(name || '').trim();
  if (!clean) return '—';
  return `<button class="record-link" onclick="window.openCustomerDetail && window.openCustomerDetail(decodeURIComponent('${encodeURIComponent(clean)}'))">${escapeHtml(clean)}</button>`;
}

function renderPager(totalRows) {
  const pager = el('recordsPager');
  if (!pager) return;
  if (totalRows <= 0) {
    pager.innerHTML = '';
    return;
  }
  const totalPages = Math.max(1, Math.ceil(totalRows / _pageSize));
  const start = totalRows ? (_page * _pageSize) + 1 : 0;
  const end = Math.min(totalRows, (_page + 1) * _pageSize);
  pager.innerHTML = `
    <div class="records-pager-info">Showing ${start}-${end} of ${totalRows}</div>
    <div class="records-pager-controls">
      <button class="btn-ghost btn-sm" id="recPageFirst" aria-label="First records page" ${_page === 0 ? 'disabled' : ''}>«</button>
      <button class="btn-ghost btn-sm" id="recPagePrev" ${_page === 0 ? 'disabled' : ''}>‹ Prev</button>
      <select id="recPageSize" class="records-page-size">
        ${RECORD_PAGE_SIZES.map(n => `<option value="${n}"${n === _pageSize ? ' selected' : ''}>${n} / page</option>`).join('')}
      </select>
      <span>Page ${_page + 1} of ${totalPages}</span>
      <button class="btn-ghost btn-sm" id="recPageNext" ${_page >= totalPages - 1 ? 'disabled' : ''}>Next ›</button>
      <button class="btn-ghost btn-sm" id="recPageLast" aria-label="Last records page" ${_page >= totalPages - 1 ? 'disabled' : ''}>»</button>
    </div>`;
  el('recPageFirst')?.addEventListener('click', () => { _page = 0; render(); });
  el('recPagePrev')?.addEventListener('click', () => { _page = Math.max(0, _page - 1); render(); });
  el('recPageNext')?.addEventListener('click', () => { _page = Math.min(totalPages - 1, _page + 1); render(); });
  el('recPageLast')?.addEventListener('click', () => { _page = totalPages - 1; render(); });
  el('recPageSize')?.addEventListener('change', e => { _pageSize = Number(e.target.value) || 25; _page = 0; render(); });
}

function recordTypeCell(r) {
  if (r.sourceKind === 'receivable') {
    return `<span class="badge badge-gray" title="Accounts Receivable ledger row: tracks whether a customer owes or paid money.">AR Ledger</span> <span class="badge badge-gray" title="This was not made in the estimate/invoice PDF builder, so there is no PDF document to open here.">No PDF</span>`;
  }
  return typeBadge(r.docType);
}

function filter(rows) {
  return filterAndSortRecords(rows, currentRecordViewState());
}

function updateSortHeaders() {
  document.querySelectorAll('[data-record-sort]').forEach(btn => {
    const active = btn.dataset.recordSort === _sortBy;
    const label = btn.textContent.replace(/[↑↓↕]/g, '').trim();
    btn.classList.toggle('active', active);
    btn.textContent = `${label} ${active ? (_sortDir === 'desc' ? '↓' : '↑') : '↕'}`;
  });
}

function updateFooterStats() {
  const stats = computeRecordFooterStats(_records);
  const setText = (id, v) => { const e = el(id); if (e) e.textContent = v; };
  setText('recTotalCount',    stats.totalRecords);
  setText('recInvoiceTotal',  money(stats.activeInvoiceTotal));
  setText('recUnpaidBalance', money(stats.unpaidBalance));
}

// ── Actions ───────────────────────────────────────────
export async function loadRecord(id, setActiveId) {
  const doc = await api.get(id);
  if (_onLoad) _onLoad(doc);
  setActiveId(id);
}

export async function duplicateRecord(id) {
  return runRecordAction(`duplicate-document:${id}`, 'Duplicate already in progress.', async () => {
    const doc = await api.duplicate(id);
    await refreshRecords();
    toast(`Duplicated as ${doc.docNumber}`, 'success');
  });
}

export async function convertEstimateToInvoice(id) {
  const source = findDocumentRecord(id);
  const sourceNumber = source?.docNumber || 'estimate';
  return runRecordAction(`convert-document:${id}`, 'Conversion already in progress.', async () => {
    const doc = await api.convertToInvoice(id);
    await refreshRecords();
    toast(`${sourceNumber} converted to ${doc.docNumber}`, 'success');
    return doc;
  });
}

export async function deleteRecord(id, activeId, setActiveId) {
  const record = findDocumentRecord(id);
  const label = record?.docNumber || `record #${id}`;
  return runRecordAction(`archive-document:${id}`, `Still archiving ${label}...`, async () => {
    if (!confirm(`Archive ${label}? It will be hidden from normal records and totals, but kept in the database and can be restored from Show Archived.`)) return;
    await api.delete(id);
    if (activeId === id) setActiveId(null);
    await refreshRecords();
    toast(`${label} archived`, 'info');
  });
}

export async function restoreRecord(id) {
  const record = findDocumentRecord(id);
  const label = record?.docNumber || `record #${id}`;
  return runRecordAction(`restore-document:${id}`, `Still restoring ${label}...`, async () => {
    await api.restore(id);
    await refreshRecords();
    toast(`${label} restored`, 'success');
  });
}

async function runRecordAction(key, busyMessage, action) {
  const actionKey = String(key || 'record-action');
  if (_recordActionsInFlight.has(actionKey)) {
    toast(busyMessage || 'Action already in progress.', 'info');
    return null;
  }

  _recordActionsInFlight.add(actionKey);
  try {
    return await action();
  } finally {
    _recordActionsInFlight.delete(actionKey);
  }
}

// ── Export CSV ────────────────────────────────────────
export function exportCsv() {
  const model = buildRecordViewModel(_records, currentRecordViewState());
  if (!model.filteredRows.length) { toast('No records to export', 'error'); return; }
  const csv = recordsToCsv(_records, currentRecordViewState());

  const blob = new Blob([csv], { type: 'text/csv' });
  const url  = URL.createObjectURL(blob);
  const a    = document.createElement('a');
  a.href     = url;
  a.download = `EPATA_Records_${new Date().toISOString().slice(0,10)}.csv`;
  a.click();
  URL.revokeObjectURL(url);
  toast('CSV exported', 'success');
}

function normalizeRecordViewState(state = {}) {
  const sortBy = RECORD_SORT_KEYS.includes(state.sortBy) ? state.sortBy : 'updated';
  const pageSize = RECORD_PAGE_SIZES.includes(Number(state.pageSize)) ? Number(state.pageSize) : 25;
  const sortDir = state.sortDir === 'asc' || state.sortDir === 'desc'
    ? state.sortDir
    : ['customer', 'project', 'number'].includes(sortBy) ? 'asc' : 'desc';
  return {
    q: String(state.q ?? '').toLowerCase().trim(),
    type: state.type || '',
    status: state.status || '',
    sortBy,
    sortDir,
    page: Math.max(0, Number(state.page) || 0),
    pageSize,
  };
}

function currentRecordViewState() {
  return {
    q: el('recSearch')?.value ?? '',
    type: el('recType')?.value ?? '',
    status: el('recStatus')?.value ?? '',
    sortBy: _sortBy || el('recSortBy')?.value || 'updated',
    sortDir: _sortDir,
    page: _page,
    pageSize: _pageSize,
  };
}

function dateValue(value) {
  const time = new Date(value || 0).getTime();
  return Number.isFinite(time) ? time : 0;
}

function textCompare(a, b) {
  return String(a || '').localeCompare(String(b || ''), undefined, { numeric: true, sensitivity: 'base' });
}

function recordColumnValue(row, column) {
  const key = column[0].toLowerCase() + column.slice(1);
  return row[key] ?? '';
}

function csvCell(value) {
  const text = String(value ?? '');
  return JSON.stringify(/^[=+\-@\t\r]/.test(text) ? `'${text}` : text);
}
