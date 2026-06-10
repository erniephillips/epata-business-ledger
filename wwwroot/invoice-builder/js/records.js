// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Records View
// ═══════════════════════════════════════════════════════

import { el, money, escapeHtml, fmtDate, fmtDateTime, statusBadge, typeBadge, toast } from './utils.js?v=2';
import { api } from './api.js?v=4';

let _records  = [];
let _onLoad   = null;
let _onNew    = null;
let _sortBy   = 'updated';
let _sortDir  = 'desc';
let _page     = 0;
let _pageSize = 25;

export function initRecords({ onLoad, onNew }) {
  _onLoad = onLoad;
  _onNew  = onNew;

  el('recSearch')?.addEventListener('input', () => { _page = 0; render(); });
  el('recType')?.addEventListener('change', () => { _page = 0; render(); });
  el('recStatus')?.addEventListener('change', () => { _page = 0; render(); });
  el('recIncludeArchived')?.addEventListener('change', refreshRecords);
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

// ── Render ────────────────────────────────────────────
function render() {
  const tbody = el('recordsBody');
  if (!tbody) return;

  const rows = filter(_records);
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

  const totalPages = Math.max(1, Math.ceil(rows.length / _pageSize));
  if (_page >= totalPages) _page = totalPages - 1;
  const pageRows = rows.slice(_page * _pageSize, (_page + 1) * _pageSize);

  tbody.innerHTML = pageRows.map(r => `
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
              ? `<button class="btn-ghost btn-sm" onclick="window._restoreRecord(${r.id})">Restore</button>`
              : `<button class="btn-ghost btn-sm" onclick="window._loadRecord(${r.id})">Open</button>
                 ${r.docType === 'ESTIMATE' ? `<button class="btn-ghost btn-sm" onclick="window._convertEstimate(${r.id})">Create Invoice</button>` : ''}
                 <button class="btn-ghost btn-sm" onclick="window._dupeRecord(${r.id})" title="Duplicate">⧉</button>
                 <button class="btn-danger btn-sm" onclick="window._delRecord(${r.id})" title="Archive">✕</button>`}
        </div>
      </td>
    </tr>`).join('');
  renderPager(rows.length);
  updateSortHeaders();
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
      <button class="btn-ghost btn-sm" id="recPageFirst" ${_page === 0 ? 'disabled' : ''}>«</button>
      <button class="btn-ghost btn-sm" id="recPagePrev" ${_page === 0 ? 'disabled' : ''}>‹ Prev</button>
      <select id="recPageSize" class="records-page-size">
        ${[10,25,50,100].map(n => `<option value="${n}"${n === _pageSize ? ' selected' : ''}>${n} / page</option>`).join('')}
      </select>
      <span>Page ${_page + 1} of ${totalPages}</span>
      <button class="btn-ghost btn-sm" id="recPageNext" ${_page >= totalPages - 1 ? 'disabled' : ''}>Next ›</button>
      <button class="btn-ghost btn-sm" id="recPageLast" ${_page >= totalPages - 1 ? 'disabled' : ''}>»</button>
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
  const q      = (el('recSearch')?.value ?? '').toLowerCase().trim();
  const type   = el('recType')?.value   ?? '';
  const status = el('recStatus')?.value ?? '';
  const sort   = _sortBy || el('recSortBy')?.value || 'updated';

  let out = rows;
  if (q)      out = out.filter(r => [r.docNumber, r.customerName, r.projectName].some(v => String(v||'').toLowerCase().includes(q)));
  if (type)   out = out.filter(r => r.docType === type);
  if (status) out = out.filter(r => r.status === status);

  const sortFns = {
    updated:  (a,b) => new Date(a.updatedAt) - new Date(b.updatedAt),
    created:  (a,b) => new Date(a.createdAt) - new Date(b.createdAt),
    number:   (a,b) => (a.docNumber || '').localeCompare(b.docNumber || '', undefined, { numeric: true, sensitivity: 'base' }),
    type:     (a,b) => (a.docType || '').localeCompare(b.docType || ''),
    status:   (a,b) => (a.status || '').localeCompare(b.status || ''),
    total:    (a,b) => Number(a.total || 0) - Number(b.total || 0),
    paid:     (a,b) => Number(a.amountPaid || 0) - Number(b.amountPaid || 0),
    customer: (a,b) => (a.customerName || '').localeCompare(b.customerName || '', undefined, { numeric: true, sensitivity: 'base' }),
    project:  (a,b) => (a.projectName || '').localeCompare(b.projectName || '', undefined, { numeric: true, sensitivity: 'base' }),
  };
  const factor = _sortDir === 'desc' ? -1 : 1;
  out = [...out].sort((a, b) => factor * (sortFns[sort] ?? sortFns.updated)(a, b));
  return out;
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
  const invoices = _records.filter(r => r.docType === 'INVOICE' && !r.isArchived);
  const total    = invoices.filter(r => !['Draft', 'Void'].includes(r.status)).reduce((s, r) => s + (r.total || 0), 0);
  const unpaid   = invoices.filter(r => !['Draft', 'Paid', 'Void'].includes(r.status)).reduce((s, r) => s + Math.max(0, r.balance || 0), 0);
  const setText = (id, v) => { const e = el(id); if (e) e.textContent = v; };
  setText('recTotalCount',    _records.length);
  setText('recInvoiceTotal',  money(total));
  setText('recUnpaidBalance', money(unpaid));
}

// ── Actions ───────────────────────────────────────────
export async function loadRecord(id, setActiveId) {
  const record = _records.find(r => r.id === id);
  if (record?.sourceKind === 'receivable') {
    window.openLedgerEntityRecord && window.openLedgerEntityRecord('receivables', record.sourceId || record.id);
    return;
  }
  const doc = await api.get(id);
  if (_onLoad) _onLoad(doc);
  setActiveId(id);
}

export async function duplicateRecord(id) {
  const doc = await api.duplicate(id);
  await refreshRecords();
  toast(`Duplicated as ${doc.docNumber}`, 'success');
}

export async function convertEstimateToInvoice(id, onConverted) {
  const source = _records.find(r => r.id === id);
  const sourceNumber = source?.docNumber || 'estimate';
  const doc = await api.convertToInvoice(id);
  await refreshRecords();
  toast(`${sourceNumber} converted to ${doc.docNumber}`, 'success');
  if (onConverted) await onConverted(doc);
}

export async function deleteRecord(id, activeId, setActiveId) {
  const record = _records.find(r => r.id === id);
  const label = record?.docNumber || `record #${id}`;
  if (!confirm(`Archive ${label}? It will be hidden from normal records and totals, but kept in the database and can be restored from Show Archived.`)) return;
  await api.delete(id);
  if (activeId === id) setActiveId(null);
  await refreshRecords();
  toast(`${label} archived`, 'info');
}

export async function restoreRecord(id) {
  const record = _records.find(r => r.id === id);
  const label = record?.docNumber || `record #${id}`;
  await api.restore(id);
  await refreshRecords();
  toast(`${label} restored`, 'success');
}

// ── Export CSV ────────────────────────────────────────
export function exportCsv() {
  const rows = filter(_records);
  if (!rows.length) { toast('No records to export', 'error'); return; }

  const cols = ['DocNumber','DocType','Status','CustomerName','ProjectName','Total','AmountPaid','Balance','DocDate','UpdatedAt'];
  const csv  = [cols.join(','), ...rows.map(r =>
    cols.map(c => JSON.stringify(r[c[0].toLowerCase() + c.slice(1)] ?? '')).join(',')
  )].join('\n');

  const blob = new Blob([csv], { type: 'text/csv' });
  const url  = URL.createObjectURL(blob);
  const a    = document.createElement('a');
  a.href     = url;
  a.download = `EPATA_Records_${new Date().toISOString().slice(0,10)}.csv`;
  a.click();
  URL.revokeObjectURL(url);
  toast('CSV exported', 'success');
}
