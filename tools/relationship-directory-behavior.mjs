import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const source = await readFile(new URL('../wwwroot/js/relationship-directory.js', import.meta.url), 'utf8');
const sandbox = { globalThis: {} };
sandbox.globalThis = sandbox;
vm.runInNewContext(source, sandbox, { filename: 'relationship-directory.js' });

const {
  buildCustomerRows,
  buildVendorRows,
  customerDetailSectionPlan,
  latestDate,
  personContactTarget,
  relationshipSectionPage,
  relationshipLinkedRowTarget,
  vendorDetailSectionPlan,
} = sandbox.EpataRelationshipDirectory;

const plain = value => JSON.parse(JSON.stringify(value));

assert.equal(latestDate('2026-06-01', '2026-06-02'), '2026-06-02');
assert.equal(latestDate('2026-06-03', '2026-06-02'), '2026-06-03');
assert.equal(latestDate('', '2026-06-04'), '2026-06-04');

const customerRows = buildCustomerRows(
  [
    { name: 'Round 17 Multi Customer', partyType: 'Customer', updatedAt: '2026-06-01T10:00:00Z' },
    { name: 'Round 17 Both Contact', partyType: 'Both', updatedAt: '2026-06-02T10:00:00Z' },
    { name: 'Round 17 Vendor Ignore', partyType: 'Vendor', updatedAt: '2026-06-03T10:00:00Z' },
  ],
  [
    { customerName: 'round 17 multi customer', customerPaid: 120, saleDate: '2026-06-03T10:00:00Z' },
    { customerName: 'Round 17 Sale Only', customerPaid: 75, saleDate: '2026-06-04T10:00:00Z' },
  ],
  [
    { customerName: 'Round 17 Multi Customer', invoiceTotal: 200, balanceDue: 80, status: 'Partial', invoiceDate: '2026-06-05T10:00:00Z' },
    { customerName: 'Round 17 Paid Only', invoiceTotal: 50, balanceDue: 50, status: 'Paid', invoiceDate: '2026-06-06T10:00:00Z' },
    { customerName: 'Round 17 Void Only', invoiceTotal: 60, balanceDue: 60, status: 'Void', invoiceDate: '2026-06-07T10:00:00Z' },
  ],
  [
    { customerName: 'Round 17 Multi Customer', total: 33, updatedAt: '2026-06-08T10:00:00Z' },
  ],
  [
    { customerName: 'Round 17 Multi Customer', invoiceAmount: 44, jobDate: '2026-06-09T10:00:00Z' },
  ],
  [
    { customerName: 'Round 17 Message Only', occurredAt: '2026-06-10T10:00:00Z' },
    { customerName: 'Round 17 Multi Customer', occurredAt: '2026-06-11T10:00:00Z' },
  ],
);

const customersByName = new Map(customerRows.map(row => [row.name.toLowerCase(), row]));
assert.equal(customersByName.has('round 17 vendor ignore'), false);
assert.ok(customersByName.has('round 17 sale only'), 'Sale-only customer activity should appear without a Party row.');
assert.ok(customersByName.has('round 17 message only'), 'Communication-only customer activity should appear without a Party row.');
assert.ok(customersByName.has('round 17 paid only'), 'AR-only paid customer activity should appear without a Party row.');
assert.ok(customersByName.has('round 17 void only'), 'AR-only void customer activity should appear without a Party row.');
assert.ok(customersByName.has('round 17 both contact'), 'Both-type Party should appear in the customer directory.');

const multiCustomer = customersByName.get('round 17 multi customer');
assert.equal(multiCustomer.linkedRows, 6);
assert.equal(multiCustomer.sources, 'People, Sales, AR, PDF Docs, Jobs, Communications');
assert.equal(multiCustomer.nameStatus, 'OK');
assert.equal(multiCustomer.duplicateNameWarning, '');
assert.equal(multiCustomer.salesTotal, 120);
assert.equal(multiCustomer.invoiceTotal, 233);
assert.equal(multiCustomer.openAr, 80);
assert.equal(multiCustomer.lastActivity, '2026-06-11T10:00:00Z');
assert.equal(customersByName.get('round 17 paid only').openAr, 0);
assert.equal(customersByName.get('round 17 void only').openAr, 0);

const vendorRows = buildVendorRows(
  [
    { name: 'Round 17 Multi Vendor', partyType: 'Vendor', updatedAt: '2026-06-01T10:00:00Z' },
    { name: 'Round 17 Both Contact', partyType: 'Both', updatedAt: '2026-06-02T10:00:00Z' },
    { name: 'Round 17 Customer Ignore', partyType: 'Customer', updatedAt: '2026-06-03T10:00:00Z' },
  ],
  [
    { vendorName: 'round 17 multi vendor', total: 30, expenseDate: '2026-06-04T10:00:00Z' },
    { vendorName: 'Round 17 Expense Only', total: 18, expenseDate: '2026-06-05T10:00:00Z' },
  ],
  [
    { vendorName: 'Round 17 Multi Vendor', total: 100, balanceDue: 70, status: 'Partial', dueDate: '2026-06-06T10:00:00Z' },
    { vendorName: 'Round 17 Paid Bill Only', total: 45, balanceDue: 45, status: 'Paid', dueDate: '2026-06-07T10:00:00Z' },
    { vendorName: 'Round 17 Void Bill Only', total: 55, balanceDue: 55, status: 'Void', dueDate: '2026-06-08T10:00:00Z' },
  ],
  [
    { vendorName: 'Round 17 Multi Vendor', cost: 250, purchaseDate: '2026-06-09T10:00:00Z' },
    { vendorName: 'Round 17 Asset Only', cost: 125, purchaseDate: '2026-06-10T10:00:00Z' },
  ],
);

const vendorsByName = new Map(vendorRows.map(row => [row.name.toLowerCase(), row]));
assert.equal(vendorsByName.has('round 17 customer ignore'), false);
assert.ok(vendorsByName.has('round 17 expense only'), 'Expense-only vendor activity should appear without a Party row.');
assert.ok(vendorsByName.has('round 17 asset only'), 'Asset-only vendor activity should appear without a Party row.');
assert.ok(vendorsByName.has('round 17 paid bill only'), 'AP-only paid vendor activity should appear without a Party row.');
assert.ok(vendorsByName.has('round 17 void bill only'), 'AP-only void vendor activity should appear without a Party row.');
assert.ok(vendorsByName.has('round 17 both contact'), 'Both-type Party should appear in the vendor directory.');

const multiVendor = vendorsByName.get('round 17 multi vendor');
assert.equal(multiVendor.linkedRows, 4);
assert.equal(multiVendor.sources, 'People, Expenses, AP, Assets');
assert.equal(multiVendor.nameStatus, 'OK');
assert.equal(multiVendor.duplicateNameWarning, '');
assert.equal(multiVendor.expenseTotal, 30);
assert.equal(multiVendor.openAp, 70);
assert.equal(multiVendor.assetTotal, 250);
assert.equal(multiVendor.lastActivity, '2026-06-09T10:00:00Z');
assert.equal(vendorsByName.get('round 17 paid bill only').openAp, 0);
assert.equal(vendorsByName.get('round 17 void bill only').openAp, 0);

const linkedSaleTarget = relationshipLinkedRowTarget('sales', { id: 321, customerName: 'Round 40 Customer' });
assert.deepEqual(plain(linkedSaleTarget), { kind: 'modal', configKey: 'sales', rowId: 321, label: 'Open' });
const linkedArTarget = relationshipLinkedRowTarget('receivables', { id: 322, invoiceNumber: 'AR-R40-001' });
assert.deepEqual(plain(linkedArTarget), { kind: 'modal', configKey: 'receivables', rowId: 322, label: 'Open' });
const linkedJobTarget = relationshipLinkedRowTarget('customerJobs', { id: 323, jobNumber: 'JOB-R40-001' });
assert.deepEqual(plain(linkedJobTarget), { kind: 'modal', configKey: 'customerJobs', rowId: 323, label: 'Open' });
const linkedProofTarget = relationshipLinkedRowTarget('auditDocs', { id: 324, fileName: 'round40-proof.pdf' });
assert.deepEqual(plain(linkedProofTarget), { kind: 'modal', configKey: 'auditDocs', rowId: 324, label: 'Open' });
const arOnlyRecordTarget = relationshipLinkedRowTarget('', { id: 325, sourceKind: 'receivable', sourceId: 326 });
assert.deepEqual(plain(arOnlyRecordTarget), { kind: 'ledger', configKey: 'receivables', rowId: 326, label: 'Open AR' });
const pdfRecordTarget = relationshipLinkedRowTarget(null, { id: 327, docNumber: 'INV-R40-001' });
assert.deepEqual(plain(pdfRecordTarget), { kind: 'page', page: 'invoiceRecords', rowId: 327, label: 'Open Records' });
assert.deepEqual(plain(relationshipLinkedRowTarget(null, {})), { kind: 'none', rowId: 0, label: '' });

const customerDetailSections = plain(customerDetailSectionPlan('Round 55 Relationship Customer', {
  sales: [{ id: 401, customerName: 'Round 55 Relationship Customer', orderNumber: 'R55-SALE' }],
  invoices: [{ id: 402, customerName: 'Round 55 Relationship Customer', invoiceNumber: 'R55-AR' }],
  docs: [{ id: 403, customerName: 'Round 55 Relationship Customer', docNumber: 'R55-DOC' }],
  jobs: [{ id: 404, customerName: 'Round 55 Relationship Customer', jobNumber: 'R55-JOB' }],
  communications: [{ id: 405, customerName: 'Round 55 Relationship Customer', subject: 'R55 message' }],
  auditDocs: [
    { id: 406, fileName: 'r55-customer-proof.pdf', notes: 'Proof for Round 55 Relationship Customer' },
    { id: 407, fileName: 'unrelated.pdf', notes: 'Other customer' },
  ],
}));
assert.deepEqual(customerDetailSections.map(section => section.title), [
  'Sales',
  'AR Invoices',
  'Estimate / Invoice PDFs',
  'Jobs',
  'Communications',
  'Proof / Audit Docs',
]);
assert.deepEqual(customerDetailSections.map(section => section.rows.length), [1, 1, 1, 1, 1, 1]);

const vendorDetailSections = plain(vendorDetailSectionPlan('Round 55 Relationship Vendor', {
  expenses: [{ id: 501, vendorName: 'Round 55 Relationship Vendor', description: 'R55 expense' }],
  bills: [{ id: 502, vendorName: 'Round 55 Relationship Vendor', billNumber: 'R55-BILL' }],
  assets: [{ id: 503, vendorName: 'Round 55 Relationship Vendor', name: 'R55 asset' }],
  auditDocs: [
    { id: 504, fileName: 'r55-vendor-proof.pdf', notes: 'Proof for Round 55 Relationship Vendor' },
    { id: 505, fileName: 'unrelated-vendor.pdf', notes: 'Other vendor' },
  ],
}));
assert.deepEqual(vendorDetailSections.map(section => section.title), ['Expenses', 'Bills / AP', 'Assets', 'Proof / Audit Docs']);
assert.deepEqual(vendorDetailSections.map(section => section.rows.length), [1, 1, 1, 1]);

assert.deepEqual(plain(personContactTarget('Round 55 Contact Customer', true, 0)), {
  kind: 'new',
  configKey: 'parties',
  row: { name: 'Round 55 Contact Customer', partyType: 'Customer', defaultPlatform: 'Direct' },
  isNew: true,
});
assert.deepEqual(plain(personContactTarget('Round 55 Contact Vendor', false, 0)), {
  kind: 'new',
  configKey: 'parties',
  row: { name: 'Round 55 Contact Vendor', partyType: 'Vendor', defaultPlatform: 'Vendor' },
  isNew: true,
});
assert.deepEqual(plain(personContactTarget('Existing', true, 812)), { kind: 'edit', configKey: 'parties', rowId: 812 });

const sectionRows = Array.from({ length: 27 }, (_, index) => ({ id: index + 1, label: `Row ${index + 1}` }));
const middlePage = plain(relationshipSectionPage(sectionRows, { page: 1, pageSize: 10 }));
assert.equal(middlePage.page, 1);
assert.equal(middlePage.totalPages, 3);
assert.equal(middlePage.startRow, 11);
assert.equal(middlePage.endRow, 20);
assert.deepEqual(middlePage.pageRows.map(row => row.id), [11, 12, 13, 14, 15, 16, 17, 18, 19, 20]);
const clampedPage = plain(relationshipSectionPage(sectionRows, { page: 99, pageSize: 10 }));
assert.equal(clampedPage.page, 2);
assert.equal(clampedPage.startRow, 21);
assert.equal(clampedPage.endRow, 27);

assert.deepEqual(plain(relationshipLinkedRowTarget('communications', { id: 328, subject: 'R55 message' })), { kind: 'modal', configKey: 'communications', rowId: 328, label: 'Open' });
assert.deepEqual(plain(relationshipLinkedRowTarget('expenses', { id: 329, description: 'R55 expense' })), { kind: 'modal', configKey: 'expenses', rowId: 329, label: 'Open' });
assert.deepEqual(plain(relationshipLinkedRowTarget('bills', { id: 330, billNumber: 'R55 bill' })), { kind: 'modal', configKey: 'bills', rowId: 330, label: 'Open' });
assert.deepEqual(plain(relationshipLinkedRowTarget('assets', { id: 331, name: 'R55 asset' })), { kind: 'modal', configKey: 'assets', rowId: 331, label: 'Open' });

const duplicateCustomerRows = buildCustomerRows(
  [
    { id: 901, name: 'Round 56 Duplicate Customer', partyType: 'Customer', updatedAt: '2026-06-01T10:00:00Z' },
    { id: 902, name: ' round 56 duplicate customer ', partyType: 'Customer', updatedAt: '2026-06-02T10:00:00Z' },
  ],
  [
    { id: 903, customerName: 'ROUND 56 DUPLICATE CUSTOMER', customerPaid: 30, saleDate: '2026-06-03T10:00:00Z' },
  ],
  [],
  [],
  [],
  [],
);
assert.equal(duplicateCustomerRows.length, 1, 'Duplicate customer contact names should merge into one normalized relationship row.');
assert.equal(duplicateCustomerRows[0].linkedRows, 3);
assert.equal(duplicateCustomerRows[0].nameStatus, 'Duplicate contacts');
assert.match(duplicateCustomerRows[0].duplicateNameWarning, /2 saved contact cards/);
assert.equal(duplicateCustomerRows[0].sources, 'People, Sales');

const duplicateVendorRows = buildVendorRows(
  [
    { id: 911, name: 'Round 56 Duplicate Vendor', partyType: 'Vendor', updatedAt: '2026-06-01T10:00:00Z' },
    { id: 912, name: 'ROUND 56 DUPLICATE VENDOR', partyType: 'Vendor', updatedAt: '2026-06-02T10:00:00Z' },
  ],
  [
    { id: 913, vendorName: ' round 56 duplicate vendor ', total: 22, expenseDate: '2026-06-03T10:00:00Z' },
  ],
  [],
  [],
);
assert.equal(duplicateVendorRows.length, 1, 'Duplicate vendor contact names should merge into one normalized relationship row.');
assert.equal(duplicateVendorRows[0].linkedRows, 3);
assert.equal(duplicateVendorRows[0].nameStatus, 'Duplicate contacts');
assert.match(duplicateVendorRows[0].duplicateNameWarning, /2 saved contact cards/);
assert.equal(duplicateVendorRows[0].sources, 'People, Expenses');

console.log(JSON.stringify({
  RelationshipDirectoryBehavior: 'pass',
  RelationshipOpenRound40: 'pass',
  RelationshipDetailRound55: 'pass',
  RelationshipContactRound55: 'pass',
  RelationshipPaginationRound55: 'pass',
  RelationshipDuplicateNamesRound56: 'pass',
  CustomerRows: customerRows.length,
  VendorRows: vendorRows.length,
  NameOnlyCustomers: ['Round 17 Sale Only', 'Round 17 Message Only', 'Round 17 Paid Only', 'Round 17 Void Only'],
  NameOnlyVendors: ['Round 17 Expense Only', 'Round 17 Asset Only', 'Round 17 Paid Bill Only', 'Round 17 Void Bill Only'],
}));
