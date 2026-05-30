// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Records View
// ═══════════════════════════════════════════════════════

import { el, money, escapeHtml, fmtDate, fmtDateTime, statusBadge, typeBadge, toast } from './utils.js';
import { api } from './api.js';

let _records  = [];
let _onLoad   = null;
let _onNew    = null;

export function initRecords({ onLoad, onNew }) {
  _onLoad = onLoad;
  _onNew  = onNew;

  el('recSearch')?.addEventListener('input', render);
  el('recType')?.addEventListener('change', render);
  el('recStatus')?.addEventListener('change', render);
  el('recSortBy')?.addEventListener('change', render);
}

export async function refreshRecords() {
  _records = await api.list();
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
    return;
  }

  tbody.innerHTML = rows.map(r => `
    <tr>
      <td class="doc-number">${escapeHtml(r.docNumber || '—')}</td>
      <td>${typeBadge(r.docType)}</td>
      <td>${statusBadge(r.status || 'Draft')}</td>
      <td>${escapeHtml(r.customerName || '—')}</td>
      <td>${escapeHtml(r.projectName || '—')}</td>
      <td class="num">${money(r.total)}</td>
      <td class="num">${money(r.amountPaid)}</td>
      <td class="muted">${fmtDate(r.updatedAt)}</td>
      <td>
        <div class="actions">
          <button class="btn-ghost btn-sm" onclick="window._loadRecord(${r.id})">Open</button>
          <button class="btn-ghost btn-sm" onclick="window._dupeRecord(${r.id})" title="Duplicate">⧉</button>
          <button class="btn-danger btn-sm" onclick="window._delRecord(${r.id})" title="Delete">✕</button>
        </div>
      </td>
    </tr>`).join('');
}

function filter(rows) {
  const q      = (el('recSearch')?.value ?? '').toLowerCase().trim();
  const type   = el('recType')?.value   ?? '';
  const status = el('recStatus')?.value ?? '';
  const sort   = el('recSortBy')?.value ?? 'updated';

  let out = rows;
  if (q)      out = out.filter(r => [r.docNumber, r.customerName, r.projectName].some(v => String(v||'').toLowerCase().includes(q)));
  if (type)   out = out.filter(r => r.docType === type);
  if (status) out = out.filter(r => r.status === status);

  const sortFns = {
    updated:  (a,b) => new Date(b.updatedAt) - new Date(a.updatedAt),
    created:  (a,b) => new Date(b.createdAt) - new Date(a.createdAt),
    number:   (a,b) => (b.docNumber || '').localeCompare(a.docNumber || ''),
    total:    (a,b) => b.total - a.total,
    customer: (a,b) => (a.customerName || '').localeCompare(b.customerName || ''),
  };
  out = [...out].sort(sortFns[sort] ?? sortFns.updated);
  return out;
}

function updateFooterStats() {
  const invoices = _records.filter(r => r.docType === 'INVOICE');
  const total    = invoices.reduce((s, r) => s + (r.total || 0), 0);
  const unpaid   = invoices.filter(r => r.status !== 'Paid' && r.status !== 'Void').reduce((s, r) => s + (r.balance || 0), 0);
  const setText = (id, v) => { const e = el(id); if (e) e.textContent = v; };
  setText('recTotalCount',    _records.length);
  setText('recInvoiceTotal',  money(total));
  setText('recUnpaidBalance', money(unpaid));
}

// ── Actions ───────────────────────────────────────────
export async function loadRecord(id, setActiveId) {
  const doc = await api.get(id);
  if (_onLoad) _onLoad(doc);
  setActiveId(id);
}

export async function duplicateRecord(id) {
  const doc = await api.duplicate(id);
  await refreshRecords();
  toast(`Duplicated as ${doc.docNumber}`, 'success');
}

export async function deleteRecord(id, activeId, setActiveId) {
  const record = _records.find(r => r.id === id);
  const label = record?.docNumber || `record #${id}`;
  if (!confirm(`Delete ${label}? This cannot be undone.`)) return;
  await api.delete(id);
  if (activeId === id) setActiveId(null);
  await refreshRecords();
  toast(`${label} deleted`, 'info');
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
