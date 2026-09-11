import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import {
  RECORD_PAGE_SIZES,
  buildRecordViewModel,
  computeRecordFooterStats,
  recordsToCsv,
} from '../wwwroot/invoice-builder/js/records.js';

const records = [
  record(1, 'INV-2026-0010', 'INVOICE', 'Paid', 'Zed Parts', 'Zeta Project', 300, 300, 0, '2026-06-10T10:00:00Z', '2026-06-11T10:00:00Z'),
  record(2, 'INV-2026-0002', 'INVOICE', 'Partial', 'Alpha Shop', 'Bracket', 120, 40, 80, '2026-06-11T10:00:00Z', '2026-06-12T10:00:00Z'),
  record(3, 'EST-2026-0005', 'ESTIMATE', 'Accepted', 'Beta Works', 'Adapter', 80, 0, 0, '2026-06-12T10:00:00Z', '2026-06-13T10:00:00Z'),
  record(4, 'INV-2026-0001', 'INVOICE', 'Sent', 'Cody Lab', 'Clamp', 60, 0, 60, '2026-06-13T10:00:00Z', '2026-06-14T10:00:00Z'),
  record(5, 'INV-2026-0003', 'INVOICE', 'Draft', 'Draft Buyer', 'Draft Project', 999, 0, 999, '2026-06-14T10:00:00Z', '2026-06-15T10:00:00Z'),
  record(6, 'INV-2026-0004', 'INVOICE', 'Void', 'Void Buyer', 'Void Project', 70, 0, 70, '2026-06-15T10:00:00Z', '2026-06-16T10:00:00Z'),
  { ...record(7, 'INV-2026-0005', 'INVOICE', 'Paid', 'Archived Buyer', 'Archived Project', 500, 500, 0, '2026-06-16T10:00:00Z', '2026-06-17T10:00:00Z'), isArchived: true },
  { ...record(8, 'INV-2026-0200', 'INVOICE', 'Partial', 'AR Only', 'Outside Invoice', 200, 75, 125, '2026-06-17T10:00:00Z', '2026-06-18T10:00:00Z'), sourceKind: 'receivable', sourceId: 108 },
  record(9, 'INV-2026-0999', 'INVOICE', 'Sent', 'Formula Guard', '=CSV Guard', 10, 0, 10, '2026-06-18T10:00:00Z', '2026-06-19T10:00:00Z'),
];

assert.equal(firstId({ sortBy: 'updated', sortDir: 'desc' }), 9);
assert.equal(firstId({ sortBy: 'created', sortDir: 'asc' }), 1);
assert.deepEqual(ids({ type: 'INVOICE', sortBy: 'number', sortDir: 'asc' }).slice(0, 3), [4, 2, 5]);
assert.equal(firstId({ sortBy: 'total', sortDir: 'desc' }), 5);
assert.equal(firstId({ sortBy: 'paid', sortDir: 'desc' }), 7);
assert.equal(firstId({ sortBy: 'customer', sortDir: 'asc' }), 2);
assert.equal(firstId({ sortBy: 'project', sortDir: 'asc' }), 9);

assert.deepEqual(ids({ q: 'outside', type: 'INVOICE', status: 'Partial', sortBy: 'number', sortDir: 'asc' }), [8]);

const pageRows = Array.from({ length: 105 }, (_, index) =>
  record(index + 100, `INV-2026-${String(index + 1).padStart(4, '0')}`, 'INVOICE', 'Sent', `Customer ${index}`, `Project ${index}`, index, 0, index, '2026-01-01T00:00:00Z', '2026-01-01T00:00:00Z'));
for (const pageSize of RECORD_PAGE_SIZES) {
  const model = buildRecordViewModel(pageRows, { pageSize, sortBy: 'number', sortDir: 'asc' });
  assert.equal(model.pageRows.length, Math.min(pageSize, pageRows.length));
  assert.equal(model.startRow, 1);
  assert.equal(model.endRow, Math.min(pageSize, pageRows.length));
}
const clampedPage = buildRecordViewModel(pageRows, { pageSize: 50, page: 99, sortBy: 'number', sortDir: 'asc' });
assert.equal(clampedPage.state.page, 2);
assert.equal(clampedPage.pageRows.length, 5);
assert.equal(clampedPage.startRow, 101);
assert.equal(clampedPage.endRow, 105);

const footer = computeRecordFooterStats(records);
assert.equal(footer.totalRecords, records.length);
assert.equal(footer.activeInvoiceTotal, 690);
assert.equal(footer.unpaidBalance, 275);

const filteredCsv = recordsToCsv(records, { type: 'INVOICE', status: 'Partial', sortBy: 'paid', sortDir: 'desc' });
const filteredCsvLines = filteredCsv.split('\n');
assert.equal(filteredCsvLines.length, 3);
assert.ok(filteredCsvLines[1].startsWith('"INV-2026-0200"'));
assert.ok(filteredCsvLines[2].startsWith('"INV-2026-0002"'));

const guardedCsv = recordsToCsv(records, { q: 'csv guard' });
assert.ok(guardedCsv.includes('"\'=CSV Guard"'));

const recordsSource = await readFile(new URL('../wwwroot/invoice-builder/js/records.js', import.meta.url), 'utf8');
const apiSource = await readFile(new URL('../wwwroot/invoice-builder/js/api.js', import.meta.url), 'utf8');
assert.ok(recordsSource.includes('data-record-action="restore-ledger"'), 'Archived AR-only rows should expose their own restore action.');
assert.ok(recordsSource.includes('api.restoreReceivable(id)'), 'AR-only Restore should call the owning ledger route in both embedded and standalone modes.');
assert.ok(apiSource.includes('restoreReceivable:'), 'Standalone invoice records API should provide AR restore directly.');
assert.ok(recordsSource.includes("target.searchParams.set('openLedger', 'receivables')"), 'Standalone Open AR should preserve the exact AR target in the main-shell navigation URL.');
assert.ok(recordsSource.includes("target.hash = 'receivables'"), 'Standalone Open AR should navigate to the main AR ledger.');
assert.ok(recordsSource.includes('window.location.assign(`/#${encodeURIComponent(page)}`)'), 'Standalone customer links should navigate to the matching main-app detail page.');

console.log(JSON.stringify({
  RecordsViewBehavior: 'pass',
  SortKeys: ['updated', 'created', 'number', 'total', 'paid', 'customer', 'project'],
  PageSizes: RECORD_PAGE_SIZES,
  CsvRowsChecked: filteredCsvLines.length - 1,
  Footer: footer,
}));

function ids(state) {
  return buildRecordViewModel(records, state).filteredRows.map(row => row.id);
}

function firstId(state) {
  return ids(state)[0];
}

function record(id, docNumber, docType, status, customerName, projectName, total, amountPaid, balance, createdAt, updatedAt) {
  return {
    id,
    docNumber,
    docType,
    status,
    customerName,
    projectName,
    total,
    amountPaid,
    balance,
    docDate: createdAt.slice(0, 10),
    createdAt,
    updatedAt,
    isArchived: false,
    sourceKind: 'document',
    sourceId: id,
  };
}
