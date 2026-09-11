import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import {
  ESTIMATE_STATUSES,
  INVOICE_STATUSES,
  buildLineItemRowHtml,
  calculateDocumentTotals,
  defaultTermsForDocType,
  planDocTypeChange,
  remapStatusForDocType,
  resolveTermsNotesForDocType,
  statusOptionsForDocType,
} from '../wwwroot/invoice-builder/js/builder.js';
import {
  buildDefaultCalculatorState,
  buildCalculatorPushPlan,
  calculatePricingFromState,
} from '../wwwroot/invoice-builder/js/calculator.js';
import {
  activeRecordBarText,
  buildSaveFailureUiState,
  emptyActiveRecordIdentity,
  identityAfterArchivedRecord,
  identityFromDocument,
} from '../wwwroot/invoice-builder/js/document-session.js';
import {
  buildSaveRequestPlan,
  canReuseInFlightSave,
  getSaveIntent,
  saveIntentKey,
} from '../wwwroot/invoice-builder/js/save-intent.js';
import {
  buildProductOptionsHtml,
  buildSelectedProductPatch,
  findProductByName,
} from '../wwwroot/invoice-builder/js/product-lookups.js';
import {
  formatUsPhone,
  isBlankOrValidEmail,
  isCompleteOrBlankUsPhone,
  normalizeNumberValue,
  normalizeEmail,
  validateDocumentState,
} from '../wwwroot/invoice-builder/js/validation.js';
import { generatePdf, renderInvoiceHtml } from '../wwwroot/invoice-builder/js/pdf.js';
import { escapeHtml, money, plainMoney, statusBadge, toast, todayStr, typeBadge } from '../wwwroot/invoice-builder/js/utils.js';

const assertIncludes = (html, needle, message) => {
  assert.ok(html.includes(needle), message || `Expected PDF HTML to include ${needle}`);
};
const assertMatches = (html, pattern, message) => {
  assert.match(html, pattern, message);
};
const assertExcludes = (html, needle, message) => {
  assert.ok(!html.includes(needle), message || `Expected PDF HTML to exclude ${needle}`);
};
const PRINT_TRIGGER_SCRIPT = "  <script>window.addEventListener('load',()=>setTimeout(()=>window.print(),250));</script>";
const stripAutoPrintScript = html => html.replace(`\n${PRINT_TRIGGER_SCRIPT}`, '').replace(PRINT_TRIGGER_SCRIPT, '');

assert.equal(money(65.505), '$65.51', 'Currency display should round half-cent values to the nearest cent.');
assert.equal(plainMoney(65.505), '65.51', 'Plain currency values should round half-cent values to the nearest cent.');
const previousTimeZone = process.env.TZ;
process.env.TZ = 'America/New_York';
assert.equal(
  todayStr(0, new Date('2026-09-11T01:30:00.000Z')),
  '2026-09-10',
  'Evening document dates must use the local calendar day instead of UTC.',
);
if (previousTimeZone === undefined) delete process.env.TZ;
else process.env.TZ = previousTimeZone;

assert.deepEqual(statusOptionsForDocType('ESTIMATE').map(([value]) => value), ESTIMATE_STATUSES);
assert.deepEqual(statusOptionsForDocType('INVOICE').map(([value]) => value), INVOICE_STATUSES);
assert.deepEqual(ESTIMATE_STATUSES, ['Draft', 'Sent', 'Accepted', 'Void']);
assert.deepEqual(INVOICE_STATUSES, ['Draft', 'Sent', 'Partial', 'Paid', 'Void']);

assert.equal(remapStatusForDocType('INVOICE', 'Accepted'), 'Draft');
assert.equal(remapStatusForDocType('ESTIMATE', 'Paid'), 'Draft');
assert.equal(remapStatusForDocType('ESTIMATE', 'Partial'), 'Draft');
assert.equal(remapStatusForDocType('INVOICE', 'Partial'), 'Partial');
assert.equal(remapStatusForDocType('INVOICE', 'Not Real'), 'Draft');

const estimateTerms = defaultTermsForDocType('ESTIMATE');
const invoiceTerms = defaultTermsForDocType('INVOICE');
assert.notEqual(estimateTerms, invoiceTerms);
assert.equal(resolveTermsNotesForDocType('INVOICE', estimateTerms), invoiceTerms);
assert.equal(resolveTermsNotesForDocType('ESTIMATE', invoiceTerms), estimateTerms);
assert.equal(resolveTermsNotesForDocType('INVOICE', 'Custom terms stay put'), 'Custom terms stay put');

const updateIntent = getSaveIntent(false, {
  activeRecordId: 14,
  activeRecordType: 'ESTIMATE',
  requestedDocType: 'ESTIMATE',
});
const sameUpdateIntent = getSaveIntent(false, {
  activeRecordId: 14,
  activeRecordType: 'ESTIMATE',
  requestedDocType: 'ESTIMATE',
});
const saveAsNewIntent = getSaveIntent(true, {
  activeRecordId: 14,
  activeRecordType: 'ESTIMATE',
  requestedDocType: 'ESTIMATE',
});
const typeChangeIntent = getSaveIntent(false, {
  activeRecordId: 14,
  activeRecordType: 'ESTIMATE',
  requestedDocType: 'INVOICE',
});
const newDraftIntent = getSaveIntent(false, {
  activeRecordId: null,
  activeRecordType: null,
  requestedDocType: 'INVOICE',
});

assert.equal(updateIntent.mode, 'update-existing');
assert.equal(saveAsNewIntent.mode, 'create-new');
assert.equal(typeChangeIntent.mode, 'type-change-create');
assert.equal(typeChangeIntent.typeChanged, true);
assert.equal(newDraftIntent.mode, 'create');
assert.equal(canReuseInFlightSave(updateIntent, sameUpdateIntent), true);
assert.equal(canReuseInFlightSave(updateIntent, saveAsNewIntent), false);
assert.equal(canReuseInFlightSave(updateIntent, typeChangeIntent), false);
assert.notEqual(saveIntentKey(saveAsNewIntent), saveIntentKey(typeChangeIntent));

const previousToastDocument = globalThis.document;
const previousToastSetTimeout = globalThis.setTimeout;
const invoiceToastContainer = {
  children: [],
  appendChild(node) {
    this.children.push(node);
  },
};
globalThis.document = {
  getElementById(id) {
    return id === 'toast-container' ? invoiceToastContainer : null;
  },
  createElement() {
    return {
      className: '',
      dataset: {},
      innerHTML: '',
      style: {},
      remove() {
        const index = invoiceToastContainer.children.indexOf(this);
        if (index >= 0) invoiceToastContainer.children.splice(index, 1);
      },
    };
  },
};
globalThis.setTimeout = () => 0;
try {
  toast('Still saving EST-2026-0001...', 'info', 1600, { key: 'invoice-save-status' });
  toast('Still saving EST-2026-0001...', 'info', 1600, { key: 'invoice-save-status' });
  assert.equal(invoiceToastContainer.children.length, 1, 'Repeated save-state toasts should replace the existing save toast.');
  assertIncludes(invoiceToastContainer.children[0].innerHTML, 'Still saving EST-2026-0001...', 'Save-state toast did not preserve the current saving message.');
  toast('Saved EST-2026-0001', 'success', 3000, { key: 'invoice-save-status' });
  assert.equal(invoiceToastContainer.children.length, 1, 'Saved toast should replace the stale Still saving toast.');
  assertIncludes(invoiceToastContainer.children[0].innerHTML, 'Saved EST-2026-0001', 'Saved toast did not replace the save-state message.');
  assertExcludes(invoiceToastContainer.children[0].innerHTML, 'Still saving', 'Saved toast left stale saving text visible.');
  toast('Independent import notice', 'info', 1600, { key: 'invoice-import-status' });
  assert.equal(invoiceToastContainer.children.length, 2, 'Different toast keys should not replace unrelated notices.');
} finally {
  if (previousToastDocument === undefined) {
    delete globalThis.document;
  } else {
    globalThis.document = previousToastDocument;
  }
  globalThis.setTimeout = previousToastSetTimeout;
}

const round66TypeSwitchPlan = planDocTypeChange('INVOICE', {
  docNumber: 'EST-2026-0014',
  status: 'Accepted',
  termsNotes: defaultTermsForDocType('ESTIMATE'),
});
assert.equal(round66TypeSwitchPlan.docType, 'INVOICE');
assert.equal(round66TypeSwitchPlan.docNumber, '', 'Round 66 type switch should clear the EST number before saving as invoice.');
assert.equal(round66TypeSwitchPlan.status, 'Draft', 'Changing document type must not invent a payment event.');
assert.equal(round66TypeSwitchPlan.dueDateLabel, 'Due Date');
assert.equal(round66TypeSwitchPlan.termsNotes, defaultTermsForDocType('INVOICE'));
assert.deepEqual(round66TypeSwitchPlan.statusOptions.map(([value]) => value), INVOICE_STATUSES);

const round66DraftInvoiceTotals = calculateDocumentTotals({
  docType: 'INVOICE',
  status: 'Draft',
  amountPaid: 0,
  taxRate: 10,
  lineItems: [{ description: 'Round 66 paid workflow item', quantity: 1, rate: 100, amount: 100 }],
});
assert.equal(round66DraftInvoiceTotals.total, 110);
assert.equal(round66DraftInvoiceTotals.amountPaid, 0, 'A type-switched draft invoice must not be auto-paid.');
assert.equal(round66DraftInvoiceTotals.balance, 110, 'A type-switched draft invoice must keep the full balance due.');

const round66SavePlan = buildSaveRequestPlan(false, {
  activeRecordId: 14,
  activeRecordType: 'ESTIMATE',
  activeRecordNumber: 'EST-2026-0014',
  requestedDocType: 'INVOICE',
  nextNumber: 'INV-2026-0014',
  body: {
    docType: 'INVOICE',
    docNumber: round66TypeSwitchPlan.docNumber,
    status: round66TypeSwitchPlan.status,
    projectName: 'Round 66 convert estimate to draft invoice',
    projectNotes: 'Existing private note',
    paymentMethod: 'Cash',
    ...round66DraftInvoiceTotals,
  },
});
assert.equal(round66SavePlan.action, 'create');
assert.equal(round66SavePlan.intent.mode, 'type-change-create');
assert.equal(round66SavePlan.originalUnchanged, true);
assert.equal(round66SavePlan.updateId, null);
assert.equal(round66SavePlan.body.docNumber, 'INV-2026-0014');
assert.equal(round66SavePlan.body.docType, 'INVOICE');
assert.equal(round66SavePlan.body.status, 'Draft');
assert.equal(round66SavePlan.body.amountPaid, 0);
assert.equal(round66SavePlan.body.balance, 110);
assert.match(round66SavePlan.body.projectNotes, /Created as a new INVOICE from ESTIMATE EST-2026-0014/);
assert.match(round66SavePlan.body.projectNotes, /original saved record was not overwritten/);

const round66RepeatSavePlan = buildSaveRequestPlan(false, {
  activeRecordId: 22,
  activeRecordType: 'INVOICE',
  activeRecordNumber: 'INV-2026-0014',
  requestedDocType: 'INVOICE',
  body: {
    docType: 'INVOICE',
    docNumber: 'INV-2026-0014',
    status: 'Paid',
    amountPaid: 110,
    balance: 0,
  },
});
assert.equal(round66RepeatSavePlan.action, 'update');
assert.equal(round66RepeatSavePlan.updateId, 22);
assert.equal(round66RepeatSavePlan.intent.typeChanged, false);

const round67LineItemHtml = buildLineItemRowHtml({
  description: 'Round 67 <script>alert(1)</script>',
  details: 'Editable details',
  qty: 2,
  rate: 12.5,
});
assertIncludes(round67LineItemHtml, 'textarea class="item-desc"', 'Round 67 add-line row should include editable description.');
assertIncludes(round67LineItemHtml, 'textarea class="item-details"', 'Round 67 add-line row should include editable details.');
assertIncludes(round67LineItemHtml, 'class="item-qty"', 'Round 67 add-line row should include editable quantity.');
assertIncludes(round67LineItemHtml, 'aria-label="Line item quantity"', 'Round 67 quantity needs an accessible name.');
assertIncludes(round67LineItemHtml, 'class="item-rate"', 'Round 67 add-line row should include editable rate.');
assertIncludes(round67LineItemHtml, 'aria-label="Line item rate"', 'Round 67 rate needs an accessible name.');
assertIncludes(round67LineItemHtml, 'onclick="window._removeLineItem(this)"', 'Round 67 add-line row should wire the remove button.');
assertIncludes(round67LineItemHtml, '&lt;script&gt;alert(1)&lt;/script&gt;', 'Round 67 add-line row should escape user text.');
assertExcludes(round67LineItemHtml, '<script>alert(1)</script>', 'Round 67 add-line row should not emit raw script text.');

const round67TwoLineTotals = calculateDocumentTotals({
  docType: 'INVOICE',
  status: 'Partial',
  amountPaid: 20,
  lineItems: [
    { description: 'Kept item', quantity: 1, rate: 75, amount: 75 },
    { description: 'Removed item', quantity: 2, rate: 12.5, amount: 25 },
  ],
});
const round67AfterRemoveTotals = calculateDocumentTotals({
  docType: 'INVOICE',
  status: 'Partial',
  amountPaid: 20,
  lineItems: [{ description: 'Kept item', quantity: 1, rate: 75, amount: 75 }],
});
assert.equal(round67TwoLineTotals.subtotal, 100);
assert.equal(round67AfterRemoveTotals.subtotal, 75);
assert.equal(round67AfterRemoveTotals.balance, 55);

const activeEstimateIdentity = identityFromDocument({
  id: 14,
  docType: 'ESTIMATE',
  docNumber: 'EST-2026-0014',
  updatedAt: '2026-09-10T12:30:00.0000000+00:00',
});
assert.deepEqual(activeEstimateIdentity, {
  activeRecordId: 14,
  activeRecordType: 'ESTIMATE',
  activeRecordNumber: 'EST-2026-0014',
  activeRecordUpdatedAt: '2026-09-10T12:30:00.0000000+00:00',
});
assert.equal(
  activeRecordBarText(activeEstimateIdentity, 'EST-2026-0014'),
  'Editing EST-2026-0014 — Ctrl+S to save',
);
assert.equal(
  activeRecordBarText(emptyActiveRecordIdentity(), ''),
  'New document — Ctrl+S to save',
);
assert.equal(
  activeRecordBarText({
    activeRecordId: 22,
    activeRecordType: 'INVOICE',
    activeRecordNumber: 'INV-2026-0022',
  }, ''),
  'Editing INV-2026-0022 — Ctrl+S to save',
);
assert.deepEqual(identityAfterArchivedRecord(activeEstimateIdentity, 14), emptyActiveRecordIdentity());
assert.deepEqual(identityAfterArchivedRecord(activeEstimateIdentity, 2), activeEstimateIdentity);

const failedSaveUiState = buildSaveFailureUiState({
  identity: activeEstimateIdentity,
  activeBarText: 'Editing EST-2026-0014 — Ctrl+S to save',
});
assert.deepEqual(failedSaveUiState.identity, activeEstimateIdentity);
assert.equal(failedSaveUiState.activeBarText, 'Editing EST-2026-0014 — Ctrl+S to save');
assert.equal(failedSaveUiState.autoSaveStatus, '');
assert.equal(failedSaveUiState.dbStatusText, 'Save failed');
assert.equal(failedSaveUiState.dbStatusState, 'error');
assert.equal(failedSaveUiState.shouldUpdateActiveBar, false);
assert.equal(failedSaveUiState.shouldShowSavedState, false);

const round74ProductLookups = [{
  name: 'Round 74 Dragon Widget',
  sku: 'R74-DRGN',
  category: 'Display',
  material: 'PETG',
  color: 'Emerald',
  grams: 42,
  printHours: 6.5,
  materialCostPerGram: 0.04,
  machineRatePerHour: 3.25,
  designMinutes: 45,
  packagingCost: 2.5,
  targetPrice: 39.95,
}];
assert.equal(
  buildProductOptionsHtml(round74ProductLookups),
  '<option value="Round 74 Dragon Widget">R74-DRGN · $39.95</option>',
);
assert.deepEqual(findProductByName(round74ProductLookups, ' round 74 dragon widget '), round74ProductLookups[0]);
assert.equal(findProductByName(round74ProductLookups, 'missing product'), null);
assert.deepEqual(
  buildSelectedProductPatch(round74ProductLookups[0], {
    postFee: '',
    minimum: '',
    hasMeaningfulLine: false,
  }),
  {
    fields: {
      material: 'PETG',
      color: 'Emerald',
      grams: 42,
      hours: 6.5,
      gramRate: 0.04,
      hourRate: 3.25,
      designHours: 0.75,
      postFee: 2.5,
      minimum: 39.95,
    },
    lineItem: {
      description: 'Round 74 Dragon Widget',
      details: 'R74-DRGN · Display · PETG',
      qty: 1,
      rate: 39.95,
    },
  },
);
assert.deepEqual(
  buildSelectedProductPatch(round74ProductLookups[0], {
    postFee: '7',
    minimum: '50',
    hasMeaningfulLine: true,
  }).lineItem,
  null,
);

const validDocumentState = {
  docType: 'ESTIMATE',
  docStatus: 'Draft',
  docNumber: '',
  docDate: '2026-06-19',
  customerName: 'Validation Customer',
  paymentMethod: 'Unknown / Review',
  customerPhone: '',
  customerEmail: '',
};
assert.deepEqual(validateDocumentState(validDocumentState), { valid: true });
assert.deepEqual(validateDocumentState({ ...validDocumentState, docDate: '' }), {
  valid: false,
  field: 'docDate',
  view: 'builder',
  message: 'Document date is required.',
});
assert.deepEqual(validateDocumentState({ ...validDocumentState, customerName: '   ' }), {
  valid: false,
  field: 'customerName',
  view: 'builder',
  message: 'Customer name is required.',
});
assert.deepEqual(validateDocumentState({ ...validDocumentState, paymentMethod: '' }), {
  valid: false,
  field: 'paymentMethod',
  view: 'builder',
  message: 'Payment method is required.',
});
assert.equal(validateDocumentState({ ...validDocumentState, docNumber: 'bad number!' }).field, 'docNumber');
assert.equal(validateDocumentState({ ...validDocumentState, customerPhone: '555-123' }).field, 'customerPhone');
assert.equal(validateDocumentState({ ...validDocumentState, customerEmail: 'not-an-email' }).field, 'customerEmail');
assert.equal(normalizeNumberValue('docDiscount', '-99'), 0);
assert.equal(normalizeNumberValue('docTaxRate', '999'), 30);
assert.equal(normalizeNumberValue('docRushPercent', '999'), 200);
assert.equal(normalizeNumberValue('amountPaid', 'not money'), '');
assert.equal(normalizeNumberValue('setupFee', '1.26', { step: '0.5' }), 1.5);
assert.equal(normalizeNumberValue('', '-7', { classNames: ['item-qty'] }), 0);
assert.equal(normalizeNumberValue('', '9999999999', { classNames: ['item-rate'] }), 1000000000);

assert.equal(formatUsPhone('5551234567'), '(555) 123-4567');
assert.equal(formatUsPhone('1-555-123-4567'), '(555) 123-4567');
assert.equal(isCompleteOrBlankUsPhone(''), true);
assert.equal(isCompleteOrBlankUsPhone('(555) 123-4567'), true);
assert.equal(isCompleteOrBlankUsPhone('555-123'), false);
assert.equal(normalizeEmail('  CUSTOMER@Example.TEST  '), 'customer@example.test');
assert.equal(isBlankOrValidEmail(''), true);
assert.equal(isBlankOrValidEmail('customer@example.test'), true);
assert.equal(isBlankOrValidEmail('not-an-email'), false);

const approx = (actual, expected, message) => {
  assert.ok(Math.abs(actual - expected) < 0.0001, `${message}: expected ${expected}, got ${actual}`);
};

const minimumState = {
  grams: 10,
  hours: 1,
  designHours: 0,
  setupFee: 0,
  postFee: 0,
  gramRate: 0.05,
  hourRate: 3,
  designRate: 25,
  minimum: 15,
  difficulty: 1,
  rush: 0,
  discount: 0,
  taxRate: 0,
};
const minimumCalc = calculatePricingFromState(minimumState);
approx(minimumCalc.baseSubtotal, 3.5, 'Minimum test base subtotal');
approx(minimumCalc.taxableAmount, 15, 'Minimum test taxable amount');
approx(minimumCalc.total, 15, 'Minimum test total');

const minimumPlan = buildCalculatorPushPlan(minimumCalc, minimumState);
const minimumAdjustment = minimumPlan.lineItems.find(item => item.desc === 'Minimum charge adjustment');
assert.ok(minimumAdjustment, 'Calculator minimum adjustment line item is missing.');
approx(minimumAdjustment.rate, 11.5, 'Calculator minimum adjustment amount');

const fullTransferState = {
  grams: 50,
  hours: 2.5,
  designHours: 1.25,
  setupFee: 4,
  postFee: 3,
  gramRate: 0.08,
  hourRate: 7.5,
  designRate: 30,
  minimum: 140,
  difficulty: 1.25,
  rush: 25,
  discount: 5,
  taxRate: 6.625,
};
const fullTransferCalc = calculatePricingFromState(fullTransferState);
const fullTransferPlan = buildCalculatorPushPlan(fullTransferCalc, fullTransferState, {
  productName: 'Helmet Bracket',
  productDetails: 'HB-42 · Custom · PETG · Black',
});
const byDescription = new Map(fullTransferPlan.lineItems.map(item => [item.desc, item]));
assert.deepEqual([...byDescription.keys()], [
  'Helmet Bracket',
  'Material usage',
  'Machine print time',
  'Design / modeling time',
  'Post-processing / handling',
  'Material / difficulty surcharge',
  'Minimum charge adjustment',
]);
assert.ok(byDescription.get('Helmet Bracket').details.includes('HB-42'), 'Product lookup details did not transfer.');
assert.ok(byDescription.get('Helmet Bracket').details.includes('Calculated quote'), 'Calculator setup details did not transfer.');
assert.equal(byDescription.get('Material usage').qty, 50);
assert.equal(byDescription.get('Material usage').rate, 0.08);
assert.equal(byDescription.get('Material usage').details, '50 grams × 0.08/g');
assert.equal(byDescription.get('Machine print time').qty, 2.5);
assert.equal(byDescription.get('Machine print time').rate, 7.5);
assert.equal(byDescription.get('Design / modeling time').qty, 1.25);
assert.equal(byDescription.get('Design / modeling time').rate, 30);
assert.equal(byDescription.get('Post-processing / handling').rate, 3);
approx(byDescription.get('Material / difficulty surcharge').rate, 16.81, 'Difficulty surcharge line rate');
approx(byDescription.get('Minimum charge adjustment').rate, 31.94, 'Full transfer minimum adjustment line rate');
assert.deepEqual(fullTransferPlan.builderFields, {
  docDiscount: '5.00',
  docRushPercent: '25',
  docTaxRate: '6.625',
});
approx(fullTransferCalc.total, 149.275, 'Full transfer total with tax');

const round81RoundingState = {
  grams: 33.3,
  hours: 2.75,
  designHours: 0.5,
  setupFee: 7.25,
  postFee: 3.4,
  gramRate: 0.075,
  hourRate: 8.25,
  designRate: 37.5,
  minimum: 20,
  difficulty: 1.2,
  rush: 10,
  discount: 4.33,
  taxRate: 6.625,
};
const round81Calc = calculatePricingFromState(round81RoundingState);
const round81PushPlan = buildCalculatorPushPlan(round81Calc, round81RoundingState, {
  productName: 'Round 81 Rounding Fixture',
});
const round81BuilderTotals = calculateDocumentTotals({
  docType: 'INVOICE',
  status: 'Partial',
  amountPaid: 10.11,
  discount: Number(round81PushPlan.builderFields.docDiscount),
  rushPercent: Number(round81PushPlan.builderFields.docRushPercent),
  taxRate: Number(round81PushPlan.builderFields.docTaxRate),
  lineItems: round81PushPlan.lineItems.map(item => ({
    description: item.desc,
    details: item.details,
    quantity: item.qty,
    rate: item.rate,
  })),
});
approx(round81BuilderTotals.subtotal, 65.505, 'Round 81 builder subtotal did not match rounded pushed line items.');
assert.equal(round81BuilderTotals.discountAmount, 4.33, 'Round 81 builder discount did not match calculator push fields.');
approx(round81BuilderTotals.rushAmount, 6.5505, 'Round 81 builder rush did not match calculator push fields.');
approx(round81BuilderTotals.taxAmount, 4.486814375, 'Round 81 builder tax did not match calculator push fields.');
approx(round81BuilderTotals.total, 72.212314375, 'Round 81 builder total did not match API-style document totals.');
approx(round81BuilderTotals.balance, 62.102314375, 'Round 81 builder balance did not match partial-payment math.');
const round81PdfHtml = renderInvoiceHtml({
  docType: 'INVOICE',
  status: 'Partial',
  docNumber: 'INV-2026-8100',
  docDate: '2026-06-20',
  dueDate: '2026-06-27',
  customerName: 'Round 81 Rounding Customer',
  projectName: 'Round 81 Rounding Project',
  subtotal: round81BuilderTotals.subtotal,
  discountAmount: round81BuilderTotals.discountAmount,
  rushAmount: round81BuilderTotals.rushAmount,
  taxAmount: round81BuilderTotals.taxAmount,
  total: round81BuilderTotals.total,
  amountPaid: round81BuilderTotals.amountPaid,
  balance: round81BuilderTotals.balance,
  lineItems: round81PushPlan.lineItems.map(item => ({
    description: item.desc,
    details: item.details,
    quantity: item.qty,
    rate: item.rate,
    amount: Math.round((Number(item.qty) || 0) * (Number(item.rate) || 0) * 100) / 100,
  })),
});
for (const moneyNeedle of ['$65.51', '-$4.33', '$6.55', '$4.49', '$72.21', '$10.11', '$62.10']) {
  assertIncludes(round81PdfHtml, moneyNeedle, `Round 81 PDF output missing rounded amount ${moneyNeedle}.`);
}

const configuredDefaults = buildDefaultCalculatorState({
  calcGramRate: 0.13,
  calcHourRate: 8.5,
  calcDesignRate: 42,
  calcSetupFee: 6,
  calcPostFee: 4.5,
  calcMinimum: 28,
});
assert.deepEqual(configuredDefaults, {
  grams: 0,
  hours: 0,
  designHours: 0,
  setupFee: 6,
  postFee: 4.5,
  gramRate: 0.13,
  hourRate: 8.5,
  designRate: 42,
  minimum: 28,
  difficulty: 1,
  rush: 0,
  discount: 0,
  taxRate: 0,
});

const settingsPdfHtml = renderInvoiceHtml({
  docType: 'INVOICE',
  status: 'Paid',
  docNumber: 'INV-2026-0160',
  docDate: '2026-06-19',
  dueDate: '2026-06-26',
  preparedFor: 'Round 16 Buyer',
  customerName: 'Round 16 Buyer',
  businessName: 'Round 16 Fab Lab',
  businessLocation: 'Trenton, NJ',
  businessEmail: 'round16@example.test',
  businessPhone: '(973) 555-1616',
  businessWebsite: 'https://round16.example.test/',
  businessEtsy: 'https://etsy.com/shop/round16',
  brandColor: '#0f766e',
  subtotal: 25,
  total: 25,
  amountPaid: 25,
  lineItems: [{ description: 'Settings proof item', details: 'PDF settings smoke', quantity: 1, rate: 25, amount: 25 }],
});
assert.ok(settingsPdfHtml.includes('--epata-blue: #0f766e;'), 'PDF did not use configured brand color.');
assert.ok(settingsPdfHtml.includes('ROUND 16 FAB LAB'), 'PDF did not use configured business name.');
assert.ok(settingsPdfHtml.includes('Trenton, NJ'), 'PDF did not use configured location.');
assert.ok(settingsPdfHtml.includes('round16@example.test'), 'PDF did not use configured email.');
assert.ok(settingsPdfHtml.includes('(973) 555-1616'), 'PDF did not use configured phone.');
assert.ok(settingsPdfHtml.includes('round16.example.test'), 'PDF did not use configured website.');
assert.ok(settingsPdfHtml.includes('etsy.com/shop/round16'), 'PDF did not use configured Etsy link.');
assert.ok(
  renderInvoiceHtml({ brandColor: 'url(javascript:alert(1))' }).includes('--epata-blue: #17499b;'),
  'PDF did not fall back for an invalid brand color.',
);

const round81InvoiceBuilderHtml = await readFile(new URL('../wwwroot/invoice-builder/index.html', import.meta.url), 'utf8');
for (const copyText of [
  'Custom FDM 3D printing using PLA/PETG. Price covers material consumption, machine time, setup, and basic cleanup. Final weight may vary ±10% from estimate.',
  'Quote valid for 14 days. Final price may adjust if design changes after approval. 50% deposit required for orders over $50. Timeline is provided after design review and schedule confirmation.',
  'Rush orders may carry a 25–50% surcharge depending on complexity and queue availability. Expedited service is available upon request and subject to current workload.',
  'This estimate was generated using our standard pricing model: $0.05/g filament + $3/hr machine time + any applicable design, post-processing, or difficulty fees. Minimum charge: $15.',
]) {
  assertIncludes(round81InvoiceBuilderHtml, `data-text="${copyText}"`, `Round 81 Rate Card copy block missing expected text: ${copyText}`);
}
for (const rateNeedle of [
  '<td class="rate-val">$0.05/g</td>',
  '<td class="rate-val">$3.00/hr</td>',
  '<td class="rate-val">$25/hr</td>',
  '<td class="rate-val">Starting $15</td>',
]) {
  assertIncludes(round81InvoiceBuilderHtml, rateNeedle, `Round 81 Rate Card default rate mismatch: ${rateNeedle}`);
}
assertIncludes(round81InvoiceBuilderHtml, 'click any wording block to copy', 'Round 81 Rate Card does not explain copy behavior.');
assertIncludes(round81InvoiceBuilderHtml, 'window._copyText(this.dataset.text)', 'Round 81 Rate Card copy blocks are not wired to clipboard helper.');

const round18SharedFields = {
  customerName: 'Round 18 Customer',
  customerPhone: '(973) 555-1818',
  customerEmail: 'round18@example.test',
  customerAddress: '18 Test Lane\nNewark, NJ 07102',
  preparedFor: 'Round 18 Buyer',
  projectName: 'Round 18 Project',
  projectDescription: 'Round 18 customer-facing project description',
  material: 'PETG',
  color: 'Galaxy Black',
  infill: '35%',
  subtotal: 150,
  discountAmount: 10,
  rushAmount: 15,
  taxAmount: 10.27,
  total: 165.27,
  docRushPercent: 10,
  docTaxRate: 6.625,
  pricingGuide: 'Round 18 pricing guide\n- Materials and machine time included',
  termsNotes: 'Round 18 public terms\n- Customer-facing terms only',
  standardTurnaround: 'Round 18 standard 5-7 business days',
  rushTurnaround: 'Round 18 rush 48 hours when approved',
  projectNotes: 'ROUND18_PRIVATE_IMPORT_RECEIPT should never print',
  internalNotes: 'ROUND18_INTERNAL_NOTE should never print',
  importReceipt: 'ROUND18_IMPORT_RECEIPT should never print',
  lineItems: [
    {
      description: 'Round 18 Line Item',
      details: 'Round 18 line-item details',
      quantity: 2,
      rate: 75,
      amount: 150,
    },
  ],
};

const round18EstimatePdf = renderInvoiceHtml({
  ...round18SharedFields,
  docType: 'ESTIMATE',
  status: 'Sent',
  docNumber: 'EST-2026-1818',
  docDate: '2026-08-01',
  dueDate: '2026-08-15',
});
for (const needle of [
  'ESTIMATE',
  'Estimate #',
  'EST-2026-1818',
  'August 1, 2026',
  'Valid Until',
  'August 15, 2026',
  'Round 18 Buyer',
  'Round 18 Customer',
  '18 Test Lane',
  'Newark, NJ 07102',
  'Round 18 Project',
  'Round 18 customer-facing project description',
  'PETG',
  'Galaxy Black',
  '35%',
  'Round 18 Line Item',
  'Round 18 line-item details',
  '$75.00',
  '$150.00',
  'Discount',
  '-$10.00',
  'Rush Fee (10%)',
  'Tax (6.625%)',
  'Estimated<br>Total',
  '$165.27',
  'Round 18 public terms',
  'Round 18 standard 5-7 business days',
  'Round 18 rush 48 hours when approved',
  'Approval',
  'I approve this estimate',
]) {
  assertIncludes(round18EstimatePdf, needle, `Estimate PDF missing ${needle}`);
}
for (const privateNeedle of ['ROUND18_PRIVATE_IMPORT_RECEIPT', 'ROUND18_INTERNAL_NOTE', 'ROUND18_IMPORT_RECEIPT']) {
  assertExcludes(round18EstimatePdf, privateNeedle, `Estimate PDF leaked ${privateNeedle}`);
}
assertExcludes(round18EstimatePdf, 'Invoice #', 'Estimate PDF should not use invoice number label.');
assertExcludes(round18EstimatePdf, 'Due Date', 'Estimate PDF should not use invoice due-date label.');
assertExcludes(round18EstimatePdf, 'Payment Status', 'Estimate PDF should not use invoice payment panel label.');

const round18InvoicePdf = renderInvoiceHtml({
  ...round18SharedFields,
  docType: 'INVOICE',
  status: 'Partial',
  docNumber: 'INV-2026-1818',
  docDate: '2026-08-02',
  dueDate: '2026-08-16',
  amountPaid: 65.27,
  balance: 100,
});
for (const needle of [
  'INVOICE',
  'Invoice #',
  'INV-2026-1818',
  'August 2, 2026',
  'Due Date',
  'August 16, 2026',
  'Round 18 Buyer',
  'Payment Status',
  'Status',
  'Partial',
  'Invoice Total',
  '$165.27',
  'Amount Paid',
  '$65.27',
  'Balance<br>Due',
  '$100.00',
  'Payment is due for this invoice.',
]) {
  assertIncludes(round18InvoicePdf, needle, `Invoice PDF missing ${needle}`);
}
for (const privateNeedle of ['ROUND18_PRIVATE_IMPORT_RECEIPT', 'ROUND18_INTERNAL_NOTE', 'ROUND18_IMPORT_RECEIPT']) {
  assertExcludes(round18InvoicePdf, privateNeedle, `Invoice PDF leaked ${privateNeedle}`);
}
assertExcludes(round18InvoicePdf, 'Estimate #', 'Invoice PDF should not use estimate number label.');
assertExcludes(round18InvoicePdf, 'Valid Until', 'Invoice PDF should not use estimate valid-until label.');
assertExcludes(round18InvoicePdf, 'I approve this estimate', 'Invoice PDF should not include estimate approval signature wording.');

const maliciousText = '<img src=x onerror=alert("EPATA_XSS")><script>window.EPATA_XSS=1</script><a href="javascript:alert(1)">unsafe</a>';
assert.equal(
  escapeHtml(maliciousText),
  '&lt;img src=x onerror=alert(&quot;EPATA_XSS&quot;)&gt;&lt;script&gt;window.EPATA_XSS=1&lt;/script&gt;&lt;a href=&quot;javascript:alert(1)&quot;&gt;unsafe&lt;/a&gt;',
  'Shared HTML escape helper did not escape executable markup.',
);
assertIncludes(statusBadge(maliciousText), '&lt;script&gt;', 'Status badge did not escape user text.');
assertExcludes(statusBadge(maliciousText), '<script>', 'Status badge emitted raw script markup.');
assertIncludes(typeBadge(maliciousText), '&lt;script&gt;', 'Type badge did not escape fallback type text.');
assertExcludes(typeBadge(maliciousText), '<script>', 'Type badge emitted raw script markup.');

const round25InjectedPdf = renderInvoiceHtml({
  docType: 'INVOICE',
  status: 'Partial',
  docNumber: maliciousText,
  docDate: '2026-09-01',
  dueDate: '2026-09-10',
  preparedFor: maliciousText,
  customerName: maliciousText,
  customerPhone: maliciousText,
  customerEmail: maliciousText,
  customerAddress: `First Line\n${maliciousText}`,
  businessName: maliciousText,
  businessLocation: maliciousText,
  businessEmail: maliciousText,
  businessPhone: maliciousText,
  businessWebsite: maliciousText,
  businessEtsy: maliciousText,
  businessMakerWorld: maliciousText,
  projectName: maliciousText,
  projectDescription: maliciousText,
  material: maliciousText,
  color: maliciousText,
  infill: maliciousText,
  pricingGuide: `Heading ${maliciousText}\n- Bullet ${maliciousText}`,
  termsNotes: `Term ${maliciousText}`,
  standardTurnaround: maliciousText,
  rushTurnaround: maliciousText,
  subtotal: 10,
  total: 10,
  balance: 10,
  lineItems: [{
    description: maliciousText,
    details: `Details\n${maliciousText}`,
    quantity: 1,
    rate: 10,
    amount: 10,
  }],
});
for (const rawNeedle of [
  '<script>',
  '</script>',
  '<img src=x',
  '<a href="javascript:',
  'onerror=alert("EPATA_XSS")',
]) {
assertExcludes(round25InjectedPdf, rawNeedle, `Invoice PDF emitted executable user markup: ${rawNeedle}`);
}
for (const escapedNeedle of [
  '&lt;script&gt;window.EPATA_XSS=1&lt;/script&gt;',
  '&lt;img src=x onerror=alert(&quot;EPATA_XSS&quot;)&gt;',
  '&lt;a href=&quot;javascript:alert(1)&quot;&gt;unsafe&lt;/a&gt;',
]) {
  assertIncludes(round25InjectedPdf, escapedNeedle, `Invoice PDF did not preserve escaped user text: ${escapedNeedle}`);
}

const invoiceAppSource = await readFile(new URL('../wwwroot/invoice-builder/js/app.js', import.meta.url), 'utf8');
const invoicePdfSource = await readFile(new URL('../wwwroot/invoice-builder/js/pdf.js', import.meta.url), 'utf8');
const invoiceBuilderHtml = await readFile(new URL('../wwwroot/invoice-builder/index.html', import.meta.url), 'utf8');
assertIncludes(invoiceAppSource, 'let activeRecordUpdatedAt = null;', 'Active invoice records should retain their loaded revision token.');
assertIncludes(invoiceAppSource, 'activeRecordUpdatedAt: snapshot.activeRecordUpdatedAt || doc.updatedAt || null,', 'Invoice snapshots should preserve optimistic-concurrency identity across shell navigation.');
assertIncludes(invoiceAppSource, 'result = await api.update(savePlan.updateId, saveBody, identity.activeRecordUpdatedAt);', 'Invoice updates should send the expected loaded revision.');
assertIncludes(invoiceAppSource, 'window.addLineItem     = addLineItemAndRefresh;', 'Round 67 Add Line Item button should refresh preview through the app wrapper.');
assertIncludes(invoiceAppSource, 'window._removeLineItem = removeLineItemAndRefresh;', 'Round 67 Remove Line Item button should refresh preview through the app wrapper.');
assertIncludes(invoiceAppSource, 'function addLineItemAndRefresh', 'Round 67 add-line preview wrapper is missing.');
assertIncludes(invoiceAppSource, 'function removeLineItemAndRefresh', 'Round 67 remove-line preview wrapper is missing.');
assertIncludes(invoiceAppSource, 'refreshInvoicePreview();', 'Round 67 line-item wrappers should refresh the preview.');
assertIncludes(invoiceAppSource, 'window._builderUpdate  = () => { updateTotals(); refreshInvoicePreview(); scheduleAutoSave(); };', 'Round 68 field-change handler should refresh totals, preview, and autosave.');
assertIncludes(invoiceAppSource, "builderView?.addEventListener('input', debounce(refreshInvoicePreview, 150), { signal });", 'Round 68 builder input events should refresh the live preview.');
assertIncludes(invoiceAppSource, "builderView?.addEventListener('change', debounce(refreshInvoicePreview, 150), { signal });", 'Round 68 builder change events should refresh the live preview.');
assertIncludes(invoiceAppSource, "const frame = el('invoicePreviewFrame');", 'Round 68 preview refresh should target the preview iframe.');
assertIncludes(invoiceAppSource, "const data = { ...getFormData(), ...appConfig, brandColor: appConfig.brandColor || '#17468f' };", 'Round 68 preview refresh should render current form data with app config.');
assertIncludes(invoiceAppSource, 'frame.srcdoc = renderInvoiceHtml(data);', 'Round 68 preview refresh should replace the iframe srcdoc with current rendered HTML.');
assertIncludes(invoiceAppSource, "on('btnPreviewPdf',   () => onGeneratePdf(true));", 'Round 69 Preview button should request preview mode.');
assertIncludes(invoiceAppSource, 'async function onGeneratePdf(preview = false)', 'Round 69 PDF generator handler is missing.');
assertIncludes(invoiceAppSource, 'const saved = await saveRecord(false).catch(() => null);', 'Round 69 preview mode should remain read-only while downloads always save first.');
assertIncludes(invoiceAppSource, 'const formData = getFormData();', 'Round 69 PDF generation should read current form data.');
assertIncludes(invoiceAppSource, 'pdfWindow = openPdfWindow(preview);', 'Round 69 PDF generation should reserve its popup during the user click.');
assertIncludes(invoiceAppSource, 'await generatePdf(data, preview, pdfWindow);', 'Round 69 PDF generation should reuse the reserved preview/print window.');
assertIncludes(invoiceAppSource, "on('btnDownloadPdf',  () => onGeneratePdf(false));", 'Round 70 Download PDF button should request printable output mode.');
assertIncludes(invoiceBuilderHtml, 'id="btnImportPdfDraftBuilder"', 'Round 71 Builder header should expose AI Import PDF.');
assertIncludes(invoiceBuilderHtml, "document.getElementById('invoicePdfImportFile').click()", 'Round 71 Builder header AI Import PDF should open the PDF file picker.');
assertIncludes(invoiceBuilderHtml, 'id="btnImportPdfDraft">AI Import PDF</button>', 'Round 71 Records header should expose AI Import PDF.');
assertMatches(invoiceBuilderHtml, /id="invoicePdfImportFile"[^>]*accept="\.pdf,application\/pdf"[^>]*style="display:none"/, 'Round 71 AI Import PDF file picker should accept only PDFs.');
assertIncludes(invoiceAppSource, "on('btnImportPdfDraft', () => el('invoicePdfImportFile')?.click());", 'Round 71 Records AI Import PDF button should open the file picker.');
assertIncludes(invoiceAppSource, "on('invoicePdfImportFile', (e) => onImportPdfDraft(e));", 'Round 71 AI Import PDF file picker should map selected files into drafts.');
assertIncludes(invoiceAppSource, "from './pdf.js?v=6';", 'Round 75 app should bust the PDF renderer cache after page-flow changes.');
assertIncludes(invoiceAppSource, "from './product-lookups.js?v=1';", 'Round 74 app should load the tested product lookup helper.');
assertIncludes(invoiceAppSource, 'list.innerHTML = buildProductOptionsHtml(productLookups);', 'Round 74 product datalist should render from Product lookup rows.');
assertIncludes(invoiceAppSource, "const selected = findProductByName(productLookups, textVal('projectName'));", 'Round 74 selected product should match the project-name datalist value.');
assertIncludes(invoiceAppSource, 'const patch = buildSelectedProductPatch(selected, {', 'Round 74 selected product should produce a tested field patch.');
assertIncludes(invoicePdfSource, 'overflow-wrap: anywhere;', 'Round 74 PDF panels should wrap long unbroken address text.');
assertIncludes(invoicePdfSource, 'word-break: break-word;', 'Round 74 PDF panels should break long address words when needed.');
assertIncludes(invoicePdfSource, 'display: table-header-group;', 'Round 75 PDF table header should repeat on printed page breaks.');
assertIncludes(invoicePdfSource, 'break-inside: avoid;', 'Round 75 PDF rows/panels should avoid breaking inside a row.');
assertIncludes(invoicePdfSource, 'page-break-inside: avoid;', 'Round 75 PDF rows should support legacy print engines.');
assertIncludes(invoicePdfSource, "if (normalized === 'LETTER') return { width: 1024, height: 1325, printSize: 'letter portrait' };", 'Round 75 Letter PDF page metrics are missing.');
assertIncludes(invoicePdfSource, "if (normalized === 'LEGAL') return { width: 1024, height: 1688, printSize: 'legal portrait' };", 'Round 75 Legal PDF page metrics are missing.');

const round68PreviewBefore = renderInvoiceHtml({
  ...round18SharedFields,
  docType: 'INVOICE',
  status: 'Sent',
  docNumber: 'INV-2026-6801',
  docDate: '2026-10-01',
  dueDate: '2026-10-15',
  customerName: 'Round 68 Customer Before',
  projectName: 'Round 68 Project Before',
  lineItems: [{
    description: 'Round 68 Original Line',
    details: 'Round 68 original detail',
    quantity: 1,
    rate: 44,
    amount: 44,
  }],
  subtotal: 44,
  total: 44,
  balance: 44,
});
const round68PreviewAfter = renderInvoiceHtml({
  ...round18SharedFields,
  docType: 'INVOICE',
  status: 'Sent',
  docNumber: 'INV-2026-6801',
  docDate: '2026-10-01',
  dueDate: '2026-10-15',
  customerName: 'Round 68 Customer After',
  projectName: 'Round 68 Project After',
  lineItems: [{
    description: 'Round 68 Updated Line',
    details: 'Round 68 updated detail',
    quantity: 3,
    rate: 25,
    amount: 75,
  }],
  subtotal: 75,
  total: 75,
  balance: 75,
});
assert.notEqual(round68PreviewBefore, round68PreviewAfter, 'Round 68 preview HTML should change when invoice fields change.');
for (const needle of [
  'Round 68 Customer After',
  'Round 68 Project After',
  'Round 68 Updated Line',
  'Round 68 updated detail',
  '$75.00',
]) {
  assertIncludes(round68PreviewAfter, needle, `Round 68 refreshed preview missing ${needle}`);
}
for (const staleNeedle of [
  'Round 68 Customer Before',
  'Round 68 Project Before',
  'Round 68 Original Line',
  'Round 68 original detail',
]) {
  assertExcludes(round68PreviewAfter, staleNeedle, `Round 68 refreshed preview retained stale field ${staleNeedle}`);
}

const round98PreviewStressData = {
  ...round18SharedFields,
  docNumber: 'INV-2026-9801',
  customerName: 'Round 98 Preview Stress Customer',
  projectName: 'Round 98 Preview Stress Project',
  lineItems: Array.from({ length: 24 }, (_, index) => ({
    description: `Round 98 stress line ${index + 1}`,
    details: `Preview refresh benchmark detail ${index + 1}`,
    quantity: 1,
    rate: 9.75 + index,
    amount: 9.75 + index,
  })),
  subtotal: 510,
  total: 510,
  balance: 510,
};
const round98Start = performance.now();
for (let i = 0; i < 80; i += 1) {
  renderInvoiceHtml(round98PreviewStressData);
}
const round98ElapsedMs = performance.now() - round98Start;
assert.ok(
  round98ElapsedMs < 1500,
  `Round 98 preview rendering is too slow for debounced typing refreshes: ${round98ElapsedMs.toFixed(1)}ms for 80 renders.`,
);

const round69PreviewData = {
  ...round18SharedFields,
  docType: 'INVOICE',
  status: 'Sent',
  docNumber: 'INV-2026-6901',
  docDate: '2026-11-01',
  dueDate: '2026-11-15',
  customerName: 'Round 69 Preview Customer',
  projectName: 'Round 69 Preview Project',
  lineItems: [{
    description: 'Round 69 Preview Line',
    details: 'Round 69 preview output detail',
    quantity: 2,
    rate: 39.5,
    amount: 79,
  }],
  subtotal: 79,
  total: 79,
  balance: 79,
};
const openedPdfWindows = [];
const previousWindow = globalThis.window;
globalThis.window = {
  open(url, name) {
    const chunks = [];
    const openedWindow = {
      url,
      name,
      document: {
        open() {
          openedWindow.documentOpened = true;
        },
        write(html) {
          chunks.push(html);
        },
        close() {
          openedWindow.documentClosed = true;
        },
      },
      focus() {
        openedWindow.focused = true;
      },
      get html() {
        return chunks.join('');
      },
    };
    openedPdfWindows.push(openedWindow);
    return openedWindow;
  },
};
try {
  await generatePdf(round69PreviewData, true);
} finally {
  if (previousWindow === undefined) {
    delete globalThis.window;
  } else {
    globalThis.window = previousWindow;
  }
}
assert.equal(openedPdfWindows.length, 1, 'Round 69 preview should open exactly one output window.');
assert.equal(openedPdfWindows[0].url, '', 'Round 69 preview should open a generated local document.');
assert.equal(openedPdfWindows[0].name, 'epata_invoice_preview', 'Round 69 preview should use the preview window name.');
assert.ok(openedPdfWindows[0].documentOpened, 'Round 69 preview should open the output document for writing.');
assert.ok(openedPdfWindows[0].documentClosed, 'Round 69 preview should close the output document after writing.');
assert.ok(openedPdfWindows[0].focused, 'Round 69 preview should focus the opened output window.');
for (const needle of [
  'INV-2026-6901',
  'Round 69 Preview Customer',
  'Round 69 Preview Project',
  'Round 69 Preview Line',
  'Round 69 preview output detail',
  '$79.00',
]) {
  assertIncludes(openedPdfWindows[0].html, needle, `Round 69 opened preview output missing ${needle}`);
}
assertExcludes(openedPdfWindows[0].html, 'window.print()', 'Round 69 Preview button should not trigger browser print mode.');

const round70PrintableData = {
  ...round18SharedFields,
  docType: 'INVOICE',
  status: 'Partial',
  docNumber: 'INV-2026-7001',
  docDate: '2026-12-01',
  dueDate: '2026-12-15',
  customerName: 'Round 70 Print Customer',
  projectName: 'Round 70 Printable Project',
  amountPaid: 40,
  balance: 60,
  lineItems: [{
    description: 'Round 70 Printable Line',
    details: 'Round 70 printable output detail',
    quantity: 4,
    rate: 25,
    amount: 100,
  }],
  subtotal: 100,
  total: 100,
};
const openedPrintWindows = [];
const previousPrintWindow = globalThis.window;
globalThis.window = {
  open(url, name) {
    const chunks = [];
    const openedWindow = {
      url,
      name,
      document: {
        open() {
          openedWindow.documentOpened = true;
        },
        write(html) {
          chunks.push(html);
        },
        close() {
          openedWindow.documentClosed = true;
        },
      },
      focus() {
        openedWindow.focused = true;
      },
      get html() {
        return chunks.join('');
      },
    };
    openedPrintWindows.push(openedWindow);
    return openedWindow;
  },
};
try {
  await generatePdf(round70PrintableData, false);
} finally {
  if (previousPrintWindow === undefined) {
    delete globalThis.window;
  } else {
    globalThis.window = previousPrintWindow;
  }
}
assert.equal(openedPrintWindows.length, 1, 'Round 70 download should open exactly one printable output window.');
assert.equal(openedPrintWindows[0].url, '', 'Round 70 download should open a generated local document.');
assert.equal(openedPrintWindows[0].name, 'epata_invoice_print', 'Round 70 download should use the printable output window name.');
assert.ok(openedPrintWindows[0].documentOpened, 'Round 70 printable output should open the output document for writing.');
assert.ok(openedPrintWindows[0].documentClosed, 'Round 70 printable output should close the output document after writing.');
assert.ok(openedPrintWindows[0].focused, 'Round 70 printable output should focus the opened output window.');
for (const needle of [
  'INV-2026-7001',
  'Round 70 Print Customer',
  'Round 70 Printable Project',
  'Round 70 Printable Line',
  'Round 70 printable output detail',
  '$100.00',
  '$40.00',
  '$60.00',
]) {
  assertIncludes(openedPrintWindows[0].html, needle, `Round 70 printable output missing ${needle}`);
}
assertIncludes(openedPrintWindows[0].html, 'window.print()', 'Round 70 printable output should trigger browser print mode.');
assertIncludes(openedPrintWindows[0].html, 'setTimeout(()=>window.print(),250)', 'Round 70 printable output should wait briefly before printing.');

const round72ImportedDraftPdf = renderInvoiceHtml({
  docType: 'ESTIMATE',
  status: 'Sent',
  docNumber: 'EST-2026-7203',
  docDate: '2026-12-10',
  dueDate: '2026-12-24',
  customerName: 'Round 72 Privacy Customer',
  projectName: 'Round 72 Privacy Project',
  projectDescription: 'Customer-visible project description',
  projectNotes: 'PDF IMPORT ASSISTANCE: Draft mapped from uploaded PDF. Review every recovered field before saving.',
  internalNotes: 'LOCAL RULES RECEIPT: Fixed local text-mapping rules prepared this draft.',
  importReceipt: 'PRIVATE IMPORT RECEIPT: source packet metadata',
  pricingGuide: 'Customer-visible pricing notes recovered from the PDF.',
  termsNotes: 'Customer-visible extra notes from the uploaded PDF.',
  subtotal: 88,
  total: 88,
  balance: 88,
  lineItems: [{
    description: 'Round 72 Privacy Line',
    details: 'Customer-visible line details',
    quantity: 1,
    rate: 88,
    amount: 88,
  }],
});
for (const customerNeedle of [
  'Customer-visible project description',
  'Customer-visible pricing notes recovered from the PDF.',
  'Customer-visible extra notes from the uploaded PDF.',
  'Round 72 Privacy Line',
]) {
  assertIncludes(round72ImportedDraftPdf, customerNeedle, `Round 72 imported PDF output missing customer field ${customerNeedle}`);
}
for (const privateNeedle of [
  'PDF IMPORT ASSISTANCE',
  'LOCAL RULES RECEIPT',
  'PRIVATE IMPORT RECEIPT',
  'source packet metadata',
]) {
  assertExcludes(round72ImportedDraftPdf, privateNeedle, `Round 72 customer PDF leaked private import metadata ${privateNeedle}`);
}

const round74LongAddressPdf = renderInvoiceHtml({
  ...round18SharedFields,
  docType: 'INVOICE',
  status: 'Sent',
  docNumber: 'INV-2026-7401',
  docDate: '2026-12-20',
  customerName: 'Round 74 Long Address Customer',
  customerAddress: [
    '12345 Extremely Long Industrial Parkway Suite ABCDEFGHIJKLMNOPQRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ',
    'Unit With Another Very Long Address Segment QRSTUVWXYZABCDEFGHIJKLMNOPQRSTUVWXYZ',
  ].join('\n'),
  projectName: 'Round 74 Address Wrap Project',
  subtotal: 12,
  total: 12,
  balance: 12,
  internalNotes: 'ROUND74_INTERNAL_ONLY_NOTES_SHOULD_NOT_PRINT',
  projectNotes: 'ROUND74_PRIVATE_PROJECT_NOTES_SHOULD_NOT_PRINT',
  importReceipt: 'ROUND74_PRIVATE_IMPORT_RECEIPT_SHOULD_NOT_PRINT',
  lineItems: [{ description: 'Round 74 Address Line', quantity: 1, rate: 12, amount: 12 }],
});
for (const visibleNeedle of [
  'Round 74 Long Address Customer',
  '12345 Extremely Long Industrial Parkway',
  'Unit With Another Very Long Address Segment',
  'Round 74 Address Line',
]) {
  assertIncludes(round74LongAddressPdf, visibleNeedle, `Round 74 long-address PDF missing ${visibleNeedle}`);
}
for (const privateNeedle of [
  'ROUND74_INTERNAL_ONLY_NOTES_SHOULD_NOT_PRINT',
  'ROUND74_PRIVATE_PROJECT_NOTES_SHOULD_NOT_PRINT',
  'ROUND74_PRIVATE_IMPORT_RECEIPT_SHOULD_NOT_PRINT',
]) {
  assertExcludes(round74LongAddressPdf, privateNeedle, `Round 74 customer PDF leaked private note ${privateNeedle}`);
}

const round75ManyLineItems = Array.from({ length: 24 }, (_, index) => ({
  description: `Round 75 flow line ${String(index + 1).padStart(2, '0')}`,
  details: `Round 75 flow details ${index + 1}`,
  quantity: 1,
  rate: index + 1,
  amount: index + 1,
}));
const round75LongText = 'Round75LongUnbrokenDescriptionABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789';
const round75LayoutPdf = renderInvoiceHtml({
  ...round18SharedFields,
  docType: 'INVOICE',
  status: 'Sent',
  docNumber: 'INV-2026-7501',
  docDate: '2026-12-21',
  customerName: 'Round 75 Layout Customer',
  projectName: 'Round 75 Layout Project',
  projectDescription: round75LongText,
  termsNotes: [
    '- Round 75 public term with enough text to prove long terms remain inside the customer-facing terms panel.',
    `- ${round75LongText}${round75LongText}`,
  ].join('\n'),
  lineItems: [
    ...round75ManyLineItems,
    {
      description: round75LongText,
      details: `${round75LongText}\nSecond wrapped detail line`,
      quantity: 2,
      rate: 7.5,
      amount: 15,
    },
  ],
  subtotal: 315,
  total: 315,
  balance: 315,
});
assertIncludes(round75LayoutPdf, '<td class="num-col">25</td>', 'Round 75 many-line-item PDF should render the 25th row.');
assertIncludes(round75LayoutPdf, 'Round 75 flow line 24', 'Round 75 many-line-item PDF should include later rows.');
assertIncludes(round75LayoutPdf, round75LongText, 'Round 75 PDF should include long description/details text.');
assertIncludes(round75LayoutPdf, 'display: table-header-group;', 'Round 75 PDF should repeat table headers when printed.');
assertIncludes(round75LayoutPdf, 'break-inside: avoid;', 'Round 75 PDF should avoid row breaks inside line items.');
assertIncludes(round75LayoutPdf, 'overflow-wrap: anywhere;', 'Round 75 PDF should wrap long text in cells and panels.');
assert.ok(
  round75LayoutPdf.indexOf('Round 75 flow line 24') < round75LayoutPdf.indexOf('Terms &amp; Notes'),
  'Round 75 terms should render after all line items instead of overlapping the table.',
);

for (const [pageSize, height, printSize] of [
  ['A4', '1414px', 'A4 portrait'],
  ['LETTER', '1325px', 'letter portrait'],
  ['LEGAL', '1688px', 'legal portrait'],
]) {
  const pageHtml = renderInvoiceHtml({
    ...round18SharedFields,
    docType: 'ESTIMATE',
    docNumber: `EST-2026-75-${pageSize}`,
    pageSize,
    customerName: `Round 75 ${pageSize} Customer`,
    lineItems: [{ description: `Round 75 ${pageSize} line`, quantity: 1, rate: 10, amount: 10 }],
    subtotal: 10,
    total: 10,
  });
  assertIncludes(pageHtml, `--epata-page-h: ${height};`, `Round 75 ${pageSize} page height was not applied.`);
  assertIncludes(pageHtml, `size: ${printSize};`, `Round 75 ${pageSize} print page size was not applied.`);
  assertIncludes(pageHtml, `Round 75 ${pageSize} line`, `Round 75 ${pageSize} output did not include line item content.`);
}

const round75PreviewHtml = renderInvoiceHtml({ ...round18SharedFields, docNumber: 'INV-2026-7502' }, { autoPrint: false });
const round75PrintHtml = renderInvoiceHtml({ ...round18SharedFields, docNumber: 'INV-2026-7502' }, { autoPrint: true });
assert.equal(
  stripAutoPrintScript(round75PrintHtml),
  round75PreviewHtml,
  'Round 75 preview and downloaded/printed output should match except for the print trigger script.',
);

console.log(JSON.stringify({
  InvoiceBuilderBehavior: 'pass',
  EstimateStatuses: ESTIMATE_STATUSES,
  InvoiceStatuses: INVOICE_STATUSES,
  SaveModes: ['create', 'update-existing', 'create-new', 'type-change-create'],
  DocumentSession: 'pass',
  TypeStatusSaveWorkflowRound66: 'pass',
  LineItemAddRemoveRound67: 'pass',
  LivePreviewRefreshRound68: 'pass',
  PreviewDebouncePerfRound98: 'pass',
  PreviewButtonRound69: 'pass',
  DownloadPdfRound70: 'pass',
  SaveToastDedupeUserReport: 'pass',
  PdfImportFilePickerRound71: 'pass',
  PdfImportPrivateNotesRound72: 'pass',
  ActiveRecordBarRound74: 'pass',
  FailedSaveStatusRound74: 'pass',
  ProductDatalistRound74: 'pass',
  LongAddressPdfRound74: 'pass',
  InternalNotesPdfRound74: 'pass',
  PdfManyItemsRound75: 'pass',
  PdfLongTextRound75: 'pass',
  PdfPageSizesRound75: 'pass',
  PdfLongTermsRound75: 'pass',
  PdfPreviewPrintMatchRound75: 'pass',
  CalculatorRoundingRound81: 'pass',
  RateCardCopyRound81: 'pass',
  RateCardDefaultsRound81: 'pass',
  RequiredAndNumericValidation: 'pass',
  ContactValidation: 'pass',
  CalculatorTransfer: 'pass',
  SettingsRound16: 'pass',
  PdfRound18: 'pass',
  HtmlEscapeRound25: 'pass',
}));
