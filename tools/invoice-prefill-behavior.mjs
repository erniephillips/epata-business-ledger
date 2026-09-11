import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const root = dirname(dirname(fileURLToPath(import.meta.url)));
const helperSource = readFileSync(join(root, 'wwwroot/js/invoice-prefill.js'), 'utf8');
const appSource = readFileSync(join(root, 'wwwroot/js/app.js'), 'utf8');
const indexSource = readFileSync(join(root, 'wwwroot/index.html'), 'utf8');

const sandbox = {};
vm.runInNewContext(helperSource, sandbox);

const { invoicePrefillFromReceivable, statusFromReceivable } = sandbox.EpataInvoicePrefill;

assert.equal(statusFromReceivable('Paid'), 'Paid');
assert.equal(statusFromReceivable('Partial'), 'Partial');
assert.equal(statusFromReceivable('Overdue'), 'Sent');
assert.equal(statusFromReceivable('Cancelled'), 'Void');
assert.equal(statusFromReceivable('Needs Review'), 'Draft');

const partialPrefill = invoicePrefillFromReceivable({
  id: 43,
  invoiceNumber: 'EXT-2026-0043',
  invoiceDate: '2026-06-19T10:30:00',
  dueDate: '2026-07-03',
  customerName: 'Round 43 AR Customer',
  projectName: 'Round 43 Replacement Bracket',
  status: 'Partial',
  subtotal: 120,
  discount: 10,
  rushFee: 6,
  taxRatePercent: 7,
  salesTax: 8.12,
  invoiceTotal: 124.12,
  amountPaid: 50,
  paymentMethod: 'PayPal',
  sourceProof: 'Audit Doc #99',
});

assert.equal(partialPrefill.docType, 'INVOICE');
assert.equal(partialPrefill.docStatus, 'Partial');
assert.equal(partialPrefill.docDate, '2026-06-19');
assert.equal(partialPrefill.dueDate, '2026-07-03');
assert.equal(partialPrefill.customerName, 'Round 43 AR Customer');
assert.equal(partialPrefill.preparedFor, 'Round 43 AR Customer');
assert.equal(partialPrefill.projectName, 'Round 43 Replacement Bracket');
assert.equal(partialPrefill.docDiscount, 10);
assert.equal(partialPrefill.docRushPercent, 5);
assert.equal(partialPrefill.docTaxRate, 7);
assert.equal(partialPrefill.amountPaid, 50);
assert.equal(partialPrefill.paymentMethod, 'PayPal');
assert.equal(partialPrefill.sourceReceivableId, 43);
assert.equal(partialPrefill.sourceReceivableInvoiceNumber, 'EXT-2026-0043');
assert.equal(Object.hasOwn(partialPrefill, 'docNumber'), false, 'AR invoice number must not overwrite builder numbering.');
assert.equal(partialPrefill.lineItems.length, 1);
assert.equal(partialPrefill.lineItems[0].description, 'Round 43 Replacement Bracket');
assert.equal(partialPrefill.lineItems[0].rate, 120);
assert.match(partialPrefill.lineItems[0].details, /EXT-2026-0043/);
assert.match(partialPrefill.lineItems[0].details, /Audit Doc #99/);

const inferredTaxPrefill = invoicePrefillFromReceivable({
  customerName: 'Round 43 Inferred Tax Customer',
  projectName: 'Round 43 Tax Inference',
  status: 'Paid',
  subtotal: 200,
  discount: 20,
  rushFee: 10,
  salesTax: 11.4,
  amountPaid: 999,
});

assert.equal(inferredTaxPrefill.docStatus, 'Paid');
assert.equal(inferredTaxPrefill.docRushPercent, 5);
assert.equal(inferredTaxPrefill.docTaxRate, 6);
assert.equal(inferredTaxPrefill.amountPaid, 201.4);
assert.equal(inferredTaxPrefill.paymentMethod, 'Unknown / Review');

const blankPrefill = invoicePrefillFromReceivable();
assert.equal(blankPrefill.docType, 'INVOICE');
assert.equal(blankPrefill.docStatus, 'Draft');
assert.equal(blankPrefill.paymentMethod, 'Unknown / Review');
assert.equal(blankPrefill.lineItems[0].description, 'Customer invoice');

assert.match(indexSource, /\/js\/invoice-prefill\.js\?v=1/);
assert.match(indexSource, /\/js\/app\.js\?v=[^"']+/, 'Main shell should load a cache-versioned app.js asset.');
assert.match(appSource, /startReceivablePdfInvoice/);
assert.match(appSource, /EpataInvoicePrefill\?\.invoicePrefillFromReceivable/);
assert.match(appSource, /startCustomerDocument\(name, type\)/);

console.log(JSON.stringify({
  InvoicePrefillBehavior: 'pass',
  ArPdfPrefillRound43: 'pass',
  DocNumberPreservedForBuilder: 'pass',
}));
