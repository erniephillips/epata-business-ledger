// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Document Builder
// ═══════════════════════════════════════════════════════

import { el, val, money, textVal, setVal, todayStr, escapeHtml } from './utils.js?v=5';

const FORM_FIELD_IDS = [
  'docType','pageSize','docNumber','docDate','dueDate',
  'preparedFor',
  'customerName','customerPhone','customerAddress','customerEmail',
  'projectName','material','color','infill',
  'projectDescription','projectNotes',
  'docDiscount','docRushPercent','docTaxRate','amountPaid','paymentMethod',
  'pricingGuide','termsNotes','standardTurnaround','rushTurnaround',
  'docStatus',
];

const UNPAID_INVOICE_STATUSES = new Set(['Draft', 'Sent']);
const initializedBuilderRoots = new WeakSet();

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

export const ESTIMATE_STATUSES = ['Draft', 'Sent', 'Accepted', 'Void'];
export const INVOICE_STATUSES = ['Draft', 'Sent', 'Partial', 'Paid', 'Void'];

export function statusOptionsForDocType(type) {
  return (type === 'INVOICE' ? INVOICE_STATUSES : ESTIMATE_STATUSES)
    .map(status => [status, status]);
}

export function remapStatusForDocType(type, current) {
  // Changing document type must never invent a payment or acceptance event.
  if (type === 'INVOICE' && current === 'Accepted') return 'Draft';
  if (type === 'ESTIMATE' && (current === 'Paid' || current === 'Partial')) return 'Draft';
  return statusOptionsForDocType(type).some(([value]) => value === current) ? current : 'Draft';
}

export function defaultTermsForDocType(type) {
  return type === 'INVOICE' ? DEFAULT_INVOICE_TERMS_NOTES : DEFAULT_TERMS_NOTES;
}

export function resolveTermsNotesForDocType(type, current) {
  const clean = String(current || '').trim();
  const knownDefaults = [DEFAULT_TERMS_NOTES, DEFAULT_INVOICE_TERMS_NOTES].map(s => s.trim());
  return !clean || knownDefaults.includes(clean) ? defaultTermsForDocType(type) : current;
}

export function shouldClearDocNumberForDocType(type, docNumber) {
  const cleanType = type === 'INVOICE' ? 'INVOICE' : 'ESTIMATE';
  const cleanNumber = String(docNumber || '').trim().toUpperCase();
  return (cleanType === 'INVOICE' && cleanNumber.startsWith('EST-'))
    || (cleanType === 'ESTIMATE' && cleanNumber.startsWith('INV-'));
}

export function planDocTypeChange(type, current = {}) {
  const cleanType = type === 'INVOICE' ? 'INVOICE' : 'ESTIMATE';
  const docNumber = String(current.docNumber || '');
  return {
    docType: cleanType,
    docNumber: shouldClearDocNumberForDocType(cleanType, docNumber) ? '' : docNumber,
    status: remapStatusForDocType(cleanType, current.status),
    termsNotes: resolveTermsNotesForDocType(cleanType, current.termsNotes),
    dueDateLabel: cleanType === 'INVOICE' ? 'Due Date' : 'Valid Until',
    statusOptions: statusOptionsForDocType(cleanType),
  };
}

export function initBuilder() {
  const root = el('view-builder');
  if (root && initializedBuilderRoots.has(root)) return;
  if (root) initializedBuilderRoots.add(root);
  FORM_FIELD_IDS.forEach(id => {
    const node = el(id);
    if (!node) return;
    if (id === 'docStatus') {
      node.addEventListener('change', onDocStatusChange);
      return;
    }
    if (id === 'amountPaid') {
      const handler = () => {
        rememberEditableAmountPaid();
        updateTotals();
      };
      node.addEventListener('input', handler);
      node.addEventListener('change', handler);
      return;
    }
    node.addEventListener('input', updateTotals);
    node.addEventListener('change', updateTotals);
  });
  el('docType')?.addEventListener('change', onDocTypeChange);
  syncPaymentStateMarkers(true);
  addLineItem();  // Start with one blank row
}

// ── Line Items ────────────────────────────────────────
export function buildLineItemRowHtml(item = {}) {
  return `
    <td class="li-num"></td>
    <td class="li-desc">
      <textarea class="item-desc" aria-label="Line item description" placeholder="Description…" oninput="window._builderUpdate()">${escapeHtml(item.desc ?? item.description ?? '')}</textarea>
    </td>
    <td class="li-details">
      <textarea class="item-details" aria-label="Line item details" placeholder="Details / notes…" oninput="window._builderUpdate()">${escapeHtml(item.details ?? '')}</textarea>
    </td>
    <td class="li-qty">
      <input class="item-qty" aria-label="Line item quantity" type="number" min="0" step="0.01" value="${escapeHtml(item.qty ?? 1)}" oninput="window._builderUpdate()" />
    </td>
    <td class="li-rate">
      <input class="item-rate" aria-label="Line item rate" type="number" min="0" step="0.01" value="${escapeHtml(item.rate ?? 0)}" oninput="window._builderUpdate()" />
    </td>
    <td class="li-amount num">$0.00</td>
    <td class="li-del">
      <button class="btn-danger btn-sm btn-icon" type="button" title="Remove row" aria-label="Remove line item" onclick="window._removeLineItem(this)">✕</button>
    </td>`;
}

export function addLineItem(item = {}) {
  const tbody = el('lineItemsBody');
  if (!tbody) return;

  const row = document.createElement('tr');
  if (item.source) row.dataset.source = item.source;
  row.innerHTML = buildLineItemRowHtml(item);
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
  const items = Array.from(document.querySelectorAll('#lineItemsBody tr')).map(row => {
    const desc    = row.querySelector('.item-desc')?.value?.trim()    ?? '';
    const details = row.querySelector('.item-details')?.value?.trim() ?? '';
    const qty     = clamp(row.querySelector('.item-qty')?.value);
    const rate    = clamp(row.querySelector('.item-rate')?.value);
    const amount  = qty * rate;

    // Update displayed amount
    const amtCell = row.querySelector('.li-amount');
    if (amtCell) amtCell.textContent = money(amount);

    return { description: desc, details, quantity: qty, rate, amount };
  }).filter(li => li.description || li.details || li.quantity || li.rate);
  return items.map((item, index) => ({ ...item, sortOrder: index + 1 }));
}

function clamp(v) {
  const n = parseFloat(v);
  return isNaN(n) ? 0 : Math.max(0, n);
}

// ── Totals ────────────────────────────────────────────
export function calculateDocumentTotals({
  docType = 'ESTIMATE',
  status = 'Draft',
  amountPaid = 0,
  discount = 0,
  rushPercent = 0,
  taxRate = 0,
  lineItems = [],
} = {}) {
  const subtotal = lineItems.reduce((sum, item) => {
    const amount = Number(item?.amount);
    const quantity = Number(item?.quantity ?? item?.qty ?? 0);
    const rate = Number(item?.rate ?? 0);
    return sum + (Number.isFinite(amount) ? amount : Math.max(0, quantity) * Math.max(0, rate));
  }, 0);
  const cleanDiscount = Math.max(0, Number(discount) || 0);
  const cleanRushPercent = Math.max(0, Number(rushPercent) || 0);
  const cleanTaxRate = Math.max(0, Number(taxRate) || 0);
  const rush = subtotal * (cleanRushPercent / 100);
  const taxable = Math.max(0, subtotal + rush - cleanDiscount);
  const tax = taxable * (cleanTaxRate / 100);
  const total = taxable + tax;
  const isInvoice = docType === 'INVOICE';
  const isPaid = isInvoice && status === 'Paid';
  const isVoid = isInvoice && status === 'Void';
  const isUnpaid = isInvoice && isUnpaidInvoiceStatus(status);
  const editablePaid = Math.min(total, Math.max(0, Number(amountPaid) || 0));
  const paid = isInvoice && !isVoid ? (isPaid ? total : isUnpaid ? 0 : editablePaid) : 0;
  const balance = isInvoice && !isVoid ? Math.max(0, total - paid) : 0;

  return { subtotal, discountAmount: cleanDiscount, rushAmount: rush, taxAmount: tax, total, amountPaid: paid, balance };
}

export function updateTotals() {
  const items    = getLineItems();
  const closedStatus = textVal('docStatus');
  const docType = textVal('docType');
  const totals = calculateDocumentTotals({
    docType,
    status: closedStatus,
    amountPaid: val('amountPaid'),
    discount: val('docDiscount'),
    rushPercent: val('docRushPercent'),
    taxRate: val('docTaxRate'),
    lineItems: items,
  });
  const isInvoice = docType === 'INVOICE';
  const isPaid   = isInvoice && closedStatus === 'Paid';
  const isVoid   = isInvoice && closedStatus === 'Void';
  const isUnpaid = isInvoice && isUnpaidInvoiceStatus(closedStatus);

  if (isPaid) setVal('amountPaid', totals.total.toFixed(2));
  else if (isInvoice && closedStatus === 'Partial') {
    const enteredPaid = Number(el('amountPaid')?.value || 0);
    if (enteredPaid < 0 || enteredPaid > totals.total) setVal('amountPaid', normalizePaidInput(totals.amountPaid));
  }
  else if (!isInvoice || isVoid || isUnpaid) setVal('amountPaid', '0');

  setText('bSubtotal',  money(totals.subtotal));
  setText('bDiscount',  '-' + money(totals.discountAmount));
  setText('bRush',      '+' + money(totals.rushAmount));
  setText('bTax',       money(totals.taxAmount));
  setText('bTotal',     money(totals.total));
  setText('bPaid',      money(totals.amountPaid));
  setText('bBalance',   money(totals.balance));

  document.dispatchEvent(new CustomEvent('epata:totals-updated', { detail: totals }));
  return totals;
}

function setText(id, text) {
  const e = el(id);
  if (e) e.textContent = text;
}

function isUnpaidInvoiceStatus(status) {
  return UNPAID_INVOICE_STATUSES.has(status);
}

function onDocStatusChange() {
  applyInvoiceStatusTransition();
  updateTotals();
}

function applyInvoiceStatusTransition() {
  const statusNode = el('docStatus');
  const paidNode = el('amountPaid');
  if (!statusNode || !paidNode) return;

  const docType = textVal('docType');
  const status = statusNode.value || 'Draft';
  const previousStatus = statusNode.dataset.previousInvoiceStatus || '';

  if (docType !== 'INVOICE') {
    paidNode.dataset.lastEditableInvoicePaid = '0';
    statusNode.dataset.previousInvoiceStatus = status;
    return;
  }

  if (status === 'Paid' && previousStatus !== 'Paid') {
    if (previousStatus !== 'Partial' || !paidNode.dataset.lastEditableInvoicePaid) {
      paidNode.dataset.lastEditableInvoicePaid = normalizePaidInput(paidNode.value);
    }
  } else if (previousStatus === 'Paid' && status === 'Partial') {
    setVal('amountPaid', paidNode.dataset.lastEditableInvoicePaid || '0');
  } else if (status === 'Void' || isUnpaidInvoiceStatus(status)) {
    setVal('amountPaid', '0');
    paidNode.dataset.lastEditableInvoicePaid = '0';
  } else if (status === 'Partial') {
    rememberEditableAmountPaid();
  }

  statusNode.dataset.previousInvoiceStatus = status;
}

function rememberEditableAmountPaid() {
  const paidNode = el('amountPaid');
  if (!paidNode || textVal('docType') !== 'INVOICE') return;

  const status = textVal('docStatus');
  if (status === 'Partial') {
    paidNode.dataset.lastEditableInvoicePaid = normalizePaidInput(paidNode.value);
  } else if (status === 'Void' || isUnpaidInvoiceStatus(status)) {
    paidNode.dataset.lastEditableInvoicePaid = '0';
  }
}

function syncPaymentStateMarkers(resetEditableAmount = false) {
  const statusNode = el('docStatus');
  const paidNode = el('amountPaid');
  if (!statusNode || !paidNode) return;

  const status = statusNode.value || 'Draft';
  statusNode.dataset.previousInvoiceStatus = status;
  if (!resetEditableAmount) return;

  paidNode.dataset.lastEditableInvoicePaid =
    textVal('docType') === 'INVOICE' && status === 'Partial'
      ? normalizePaidInput(paidNode.value)
      : '0';
}

function normalizePaidInput(value) {
  const paid = Math.max(0, Number(value) || 0);
  return Number.isInteger(paid) ? String(paid) : paid.toFixed(2);
}

// ── Doc Type changes ──────────────────────────────────
function onDocTypeChange() {
  const type = textVal('docType');
  const plan = planDocTypeChange(type, {
    docNumber: textVal('docNumber'),
    status: textVal('docStatus'),
    termsNotes: textVal('termsNotes'),
  });
  if (plan.docNumber !== textVal('docNumber')) {
    setVal('docNumber', plan.docNumber);
  }

  // Adjust due date label & default
  const dueDateLabel = el('dueDateLabel');
  if (dueDateLabel) dueDateLabel.textContent = plan.dueDateLabel;

  // Adjust status dropdown — estimates can be Accepted, invoices can be Paid
  syncStatusOptionsToDocType(type);

  // Swap the default terms boilerplate when it still matches the OTHER type's default
  syncTermsNotesToDocType(type);

  updateTotals();
  syncPaymentStateMarkers(true);
}

function syncStatusOptionsToDocType(type) {
  const sel = el('docStatus');
  if (!sel) return;
  const opts = statusOptionsForDocType(type);
  const remapped = remapStatusForDocType(type, sel.value);

  sel.innerHTML = opts.map(([v, lbl]) => `<option value="${v}">${lbl}</option>`).join('');
  sel.value = remapped;
}

function syncTermsNotesToDocType(type) {
  const node = el('termsNotes');
  if (!node) return;
  setVal('termsNotes', resolveTermsNotesForDocType(type, node.value));
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
  const formValues = state.formValues ?? {};
  const requestedStatus = formValues.docStatus;
  Object.entries(formValues).forEach(([id, value]) => {
    if (id === 'docStatus') return;
    if (!BUSINESS_IDS.includes(id) && el(id)) el(id).value = value;
  });

  // Sync the status dropdown options + value to whatever docType was restored.
  // Handles legacy data where an estimate was saved with status="Paid", etc.
  const restoredDocType = textVal('docType') || 'ESTIMATE';
  syncStatusOptionsToDocType(restoredDocType);
  const dueDateLabel = el('dueDateLabel');
  if (dueDateLabel) dueDateLabel.textContent = restoredDocType === 'INVOICE' ? 'Due Date' : 'Valid Until';
  if (el('docStatus')) {
    setVal('docStatus', remapStatusForDocType(restoredDocType, requestedStatus || 'Draft'));
  }

  const tbody = el('lineItemsBody');
  if (tbody) tbody.innerHTML = '';
  const items = state.lineItems ?? [];
  items.forEach(item => addLineItem(item));
  if (!items.length) addLineItem();

  updateTotals();
  syncPaymentStateMarkers(true);
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
    paymentMethod: textVal('paymentMethod') || 'Unknown / Review',
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
  setVal('paymentMethod', 'Unknown / Review');
  setVal('pricingGuide', DEFAULT_PRICING_GUIDE);
  setVal('termsNotes', defaultTermsForDocType(type));
  setVal('standardTurnaround', 'Estimated timeline provided after design review and schedule confirmation');
  setVal('rushTurnaround', 'Expedited service available upon request, subject to current workload');

  const tbody = el('lineItemsBody');
  if (tbody) tbody.innerHTML = '';
  addLineItem();
  updateTotals();
  syncPaymentStateMarkers(true);
}
