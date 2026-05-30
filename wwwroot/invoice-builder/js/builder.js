// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Document Builder
// ═══════════════════════════════════════════════════════

import { el, val, money, textVal, setVal, todayStr, escapeHtml } from './utils.js';

const FORM_FIELD_IDS = [
  'docType','pageSize','docNumber','docDate','dueDate',
  'preparedFor',
  'customerName','customerPhone','customerAddress','customerEmail',
  'projectName','material','color','infill',
  'projectDescription','projectNotes',
  'docDiscount','docRushPercent','docTaxRate','amountPaid',
  'pricingGuide','termsNotes','standardTurnaround','rushTurnaround',
  'docStatus',
];

const DEFAULT_PRICING_GUIDE = `Print-Only Jobs
- $15 minimum, or setup + material + machine time
Basic Modeling
- $25/hour (1 hour minimum)
Revisions
- 1 small revision included
- Additional revisions: $15-$25 or hourly
Rush Fee
- Add 25%-50%
Material Notes
- ABS/ASA prints include a 20%-35% handling surcharge due to increased time and risk.`;

const DEFAULT_TERMS_NOTES = `- This estimate is valid for 14 days from the date above.
- Final price may vary based on model changes or unexpected print issues.
- Payment is due before printing begins.
- Thank you for supporting EPATA 3D Prints!`;

const DEFAULT_INVOICE_TERMS_NOTES = `- Payment is due by the due date shown above unless already paid.
- This invoice reflects the approved work, materials, and services listed.
- Paid invoices serve as a receipt for your records.
- Thank you for supporting EPATA 3D Prints!`;

export function initBuilder() {
  FORM_FIELD_IDS.forEach(id => {
    const node = el(id);
    if (!node) return;
    node.addEventListener('input', updateTotals);
    node.addEventListener('change', updateTotals);
  });
  el('docType')?.addEventListener('change', onDocTypeChange);
  el('docStatus')?.addEventListener('change', () => {});
  addLineItem();  // Start with one blank row
}

// ── Line Items ────────────────────────────────────────
export function addLineItem(item = {}) {
  const tbody = el('lineItemsBody');
  if (!tbody) return;

  const row = document.createElement('tr');
  row.innerHTML = `
    <td class="li-num"></td>
    <td class="li-desc">
      <textarea class="item-desc" placeholder="Description…" oninput="window._builderUpdate()">${escapeHtml(item.desc ?? item.description ?? '')}</textarea>
    </td>
    <td class="li-details">
      <textarea class="item-details" placeholder="Details / notes…" oninput="window._builderUpdate()">${escapeHtml(item.details ?? '')}</textarea>
    </td>
    <td class="li-qty">
      <input class="item-qty" type="number" min="0" step="0.01" value="${item.qty ?? 1}" oninput="window._builderUpdate()" />
    </td>
    <td class="li-rate">
      <input class="item-rate" type="number" min="0" step="0.01" value="${item.rate ?? 0}" oninput="window._builderUpdate()" />
    </td>
    <td class="li-amount num">$0.00</td>
    <td class="li-del">
      <button class="btn-danger btn-sm btn-icon" type="button" title="Remove row" onclick="window._removeLineItem(this)">✕</button>
    </td>`;
  tbody.appendChild(row);
  renumber();
  updateTotals();
}

export function removeLineItem(btn) {
  const row = btn?.closest('tr');
  if (row) row.remove();
  if (!el('lineItemsBody')?.children.length) addLineItem();
  renumber();
  updateTotals();
}

function renumber() {
  document.querySelectorAll('#lineItemsBody tr').forEach((row, i) => {
    const cell = row.querySelector('.li-num');
    if (cell) cell.textContent = i + 1;
  });
}

export function getLineItems() {
  return Array.from(document.querySelectorAll('#lineItemsBody tr')).map((row, i) => {
    const desc    = row.querySelector('.item-desc')?.value?.trim()    ?? '';
    const details = row.querySelector('.item-details')?.value?.trim() ?? '';
    const qty     = clamp(row.querySelector('.item-qty')?.value);
    const rate    = clamp(row.querySelector('.item-rate')?.value);
    const amount  = qty * rate;

    // Update displayed amount
    const amtCell = row.querySelector('.li-amount');
    if (amtCell) amtCell.textContent = money(amount);

    return { sortOrder: i, description: desc, details, quantity: qty, rate, amount };
  }).filter(li => li.description || li.details || li.quantity || li.rate);
}

function clamp(v) {
  const n = parseFloat(v);
  return isNaN(n) ? 0 : Math.max(0, n);
}

// ── Totals ────────────────────────────────────────────
export function updateTotals() {
  const items    = getLineItems();
  const subtotal = items.reduce((s, li) => s + li.amount, 0);
  const discount = val('docDiscount');
  const rush     = (subtotal - discount) * (val('docRushPercent') / 100);
  const afterRush = subtotal - discount + rush;
  const tax      = afterRush * (val('docTaxRate') / 100);
  const total    = afterRush + tax;
  // Treat estimate "Accepted" the same as invoice "Paid" — both mean closed/finalized
  const closedStatus = textVal('docStatus');
  const isPaid   = closedStatus === 'Paid' || closedStatus === 'Accepted';
  const paid     = isPaid ? total : val('amountPaid');
  const balance  = isPaid ? 0 : total - paid;

  if (isPaid) setVal('amountPaid', total.toFixed(2));

  setText('bSubtotal',  money(subtotal));
  setText('bDiscount',  '-' + money(discount));
  setText('bRush',      '+' + money(rush));
  setText('bTax',       money(tax));
  setText('bTotal',     money(total));
  setText('bPaid',      money(paid));
  setText('bBalance',   money(balance));

  const totals = { subtotal, discountAmount: discount, rushAmount: rush, taxAmount: tax, total, amountPaid: paid, balance };
  document.dispatchEvent(new CustomEvent('epata:totals-updated', { detail: totals }));
  return totals;
}

function setText(id, text) {
  const e = el(id);
  if (e) e.textContent = text;
}

// ── Doc Type changes ──────────────────────────────────
function onDocTypeChange() {
  const type = textVal('docType');
  const num  = textVal('docNumber');
  if (type === 'INVOICE' && num.startsWith('EST-')) setVal('docNumber', num.replace('EST-', 'INV-'));
  if (type === 'ESTIMATE' && num.startsWith('INV-')) setVal('docNumber', num.replace('INV-', 'EST-'));

  // Adjust due date label & default
  const dueDateLabel = el('dueDateLabel');
  if (dueDateLabel) dueDateLabel.textContent = type === 'INVOICE' ? 'Due Date' : 'Valid Until';

  // Adjust status dropdown — estimates can be Accepted, invoices can be Paid
  syncStatusOptionsToDocType(type);

  // Swap the default terms boilerplate when it still matches the OTHER type's default
  syncTermsNotesToDocType(type);

  updateTotals();
}

function syncStatusOptionsToDocType(type) {
  const sel = el('docStatus');
  if (!sel) return;
  const current = sel.value;

  // Map between equivalents when switching types so we don't leave an invalid value
  let remapped = current;
  if (type === 'INVOICE' && current === 'Accepted') remapped = 'Paid';
  else if (type === 'ESTIMATE' && current === 'Paid') remapped = 'Accepted';

  const opts = type === 'INVOICE'
    ? [['Draft','Draft'], ['Sent','Sent'], ['Paid','Paid'], ['Void','Void']]
    : [['Draft','Draft'], ['Sent','Sent'], ['Accepted','Accepted'], ['Void','Void']];

  sel.innerHTML = opts.map(([v, lbl]) => `<option value="${v}">${lbl}</option>`).join('');
  sel.value = opts.some(([v]) => v === remapped) ? remapped : 'Draft';
}

function syncTermsNotesToDocType(type) {
  const node = el('termsNotes');
  if (!node) return;
  const current = (node.value || '').trim();
  // If user customized the text, leave it alone. Only swap when the text
  // still matches one of the known default templates.
  const KNOWN = [DEFAULT_TERMS_NOTES, DEFAULT_INVOICE_TERMS_NOTES].map(s => s.trim());
  if (!current || KNOWN.includes(current)) {
    setVal('termsNotes', type === 'INVOICE' ? DEFAULT_INVOICE_TERMS_NOTES : DEFAULT_TERMS_NOTES);
  }
}

// ── Capture / restore state ───────────────────────────
export function captureState() {
  const formValues = {};
  FORM_FIELD_IDS.forEach(id => { if (el(id)) formValues[id] = el(id).value; });

  return {
    version: 3,
    savedAt: new Date().toISOString(),
    formValues,
    lineItems: getLineItems().map(li => ({
      desc: li.description, details: li.details,
      qty: li.quantity, rate: li.rate,
    })),
  };
}

export function restoreState(state) {
  if (!state) return;
  const BUSINESS_IDS = ['businessName','businessLocation','businessEmail','businessPhone',
                        'businessWebsite','businessEtsy','businessInstagram','businessFacebook','brandBlue'];
  Object.entries(state.formValues ?? {}).forEach(([id, value]) => {
    if (!BUSINESS_IDS.includes(id) && el(id)) el(id).value = value;
  });

  // Sync the status dropdown options + value to whatever docType was restored.
  // Handles legacy data where an estimate was saved with status="Paid", etc.
  syncStatusOptionsToDocType(textVal('docType') || 'ESTIMATE');

  const tbody = el('lineItemsBody');
  if (tbody) tbody.innerHTML = '';
  const items = state.lineItems ?? [];
  items.forEach(item => addLineItem(item));
  if (!items.length) addLineItem();

  updateTotals();
}

export function getFormData() {
  const totals = updateTotals();
  const items  = getLineItems();
  return {
    docNumber: textVal('docNumber'),
    docType:   textVal('docType') || 'ESTIMATE',
    status:    textVal('docStatus') || 'Draft',
    customerName: textVal('customerName'),
    customerPhone: textVal('customerPhone'),
    customerAddress: textVal('customerAddress'),
    customerEmail: textVal('customerEmail'),
    preparedFor: textVal('preparedFor'),
    projectName: textVal('projectName'),
    material: textVal('material'),
    color: textVal('color'),
    infill: textVal('infill'),
    projectDescription: textVal('projectDescription'),
    projectNotes: textVal('projectNotes'),
    pageSize: textVal('pageSize'),
    docDate:  textVal('docDate'),
    dueDate:  textVal('dueDate'),
    docRushPercent: val('docRushPercent'),
    docTaxRate: val('docTaxRate'),
    pricingGuide: textVal('pricingGuide'),
    termsNotes: textVal('termsNotes'),
    standardTurnaround: textVal('standardTurnaround'),
    rushTurnaround: textVal('rushTurnaround'),
    ...totals,
    lineItems: items,
  };
}

export function newDocument(type = 'ESTIMATE', nextNumber = '') {
  const prefix = type === 'INVOICE' ? 'INV' : 'EST';
  setVal('docType', type);
  // Rebuild the status dropdown for this type BEFORE picking a status,
  // otherwise the Estimate dropdown will still be showing "Accepted" when
  // the user changes from the default Draft value.
  syncStatusOptionsToDocType(type);
  setVal('docStatus', 'Draft');
  // Keep the due-date label in sync too.
  const dueDateLabel = el('dueDateLabel');
  if (dueDateLabel) dueDateLabel.textContent = type === 'INVOICE' ? 'Due Date' : 'Valid Until';
  setVal('docNumber', nextNumber);
  setVal('docDate', todayStr(0));
  setVal('dueDate', todayStr(type === 'INVOICE' ? 7 : 14));
  setVal('preparedFor',   '');
  setVal('customerName',  '');
  setVal('customerPhone', '');
  setVal('customerAddress', '');
  setVal('customerEmail', '');
  setVal('projectName',   '');
  setVal('material',      '');
  setVal('color',         '');
  setVal('infill',        '');
  setVal('projectDescription', '');
  setVal('projectNotes',  '');
  setVal('docDiscount',   '0');
  setVal('docRushPercent','0');
  setVal('docTaxRate',    '0');
  setVal('amountPaid',    '0');
  setVal('pricingGuide', DEFAULT_PRICING_GUIDE);
  setVal('termsNotes', type === 'INVOICE' ? DEFAULT_INVOICE_TERMS_NOTES : DEFAULT_TERMS_NOTES);
  setVal('standardTurnaround', 'Estimated timeline provided after design review and schedule confirmation');
  setVal('rushTurnaround', 'Expedited service available upon request, subject to current workload');

  const tbody = el('lineItemsBody');
  if (tbody) tbody.innerHTML = '';
  addLineItem();
  updateTotals();
}
