import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { performance } from 'node:perf_hooks';
import vm from 'node:vm';

const sandbox = {};
sandbox.globalThis = sandbox;

const helperSource = await readFile(new URL('../wwwroot/js/entity-table-state.js', import.meta.url), 'utf8');
vm.runInNewContext(helperSource, sandbox, { filename: 'entity-table-state.js' });

const {
  buildEntityTableModel,
  clampPage,
  filterRows,
  matchesStatus,
  normalizeText,
  pageWindow,
} = sandbox.EpataEntityTableState;

const statuses = ['Open', 'Sent', 'Unpaid', 'Partial', 'Waiting', 'Lead', 'Quoted', 'In Progress', 'Paid', 'Done', 'Completed', 'Fulfilled', 'Refunded', 'Needs Review'];
const rows = Array.from({ length: 1200 }, (_, index) => {
  const id = index + 1;
  return {
    id,
    customerName: `Customer ${String(id).padStart(4, '0')}`,
    productName: `Widget ${id % 20}`,
    status: statuses[index % statuses.length],
    sourceProof: `proof-${id}.pdf`,
    needsReview: id % 11 === 0,
    customerPaid: id * 1.25,
  };
});

assert.equal(normalizeText('  Customer 1100  '), 'customer 1100');
assert.equal(clampPage(99, 100, 25), 3);
const lastWindow = pageWindow(100, 3, 25);
assert.equal(lastWindow.page, 3);
assert.equal(lastWindow.pageSize, 25);
assert.equal(lastWindow.totalPages, 4);
assert.equal(lastWindow.startRow, 76);
assert.equal(lastWindow.endRow, 100);

const proofMatch = filterRows(rows, 'proof-1199', '');
assert.equal(proofMatch.length, 1);
assert.equal(proofMatch[0].id, 1199, 'Text search should include proof/reference fields through row data.');

const productRows = [
  {
    id: 101,
    name: 'Gridfinity bin set',
    sku: 'GRID-BLK-001',
    category: 'Storage',
    material: 'PLA Matte',
    color: 'Galaxy Black',
    grams: 84,
    printHours: 5.5,
    targetPrice: 28,
    needsReview: false,
  },
  {
    id: 102,
    name: 'Printer bracket',
    sku: 'BRKT-PETG-002',
    category: 'Replacement Part',
    material: 'PETG-CF',
    color: 'Safety Orange',
    grams: 42,
    printHours: 2.25,
    targetPrice: 18,
    needsReview: true,
  },
  {
    id: 103,
    name: 'Cable clip',
    sku: 'CLIP-PLA-003',
    category: 'Organizer',
    material: 'PLA Basic',
    color: 'White',
    grams: 12,
    printHours: 0.75,
    targetPrice: 6,
    needsReview: false,
  },
];

const productSkuMatch = filterRows(productRows, 'GRID-BLK-001', '');
assert.deepEqual(productSkuMatch.map(row => row.id), [101], 'Product catalog search should match SKU values.');
const productMaterialMatch = filterRows(productRows, 'petg-cf', '');
assert.deepEqual(productMaterialMatch.map(row => row.id), [102], 'Product catalog search should match material values.');
const productColorMatch = buildEntityTableModel(productRows, {
  filter: 'safety orange',
  statusFilter: '',
  state: { page: 0, pageSize: 25 },
});
assert.equal(productColorMatch.totalRows, 1, 'Product catalog search should match color values.');
assert.equal(productColorMatch.pageRows[0].sku, 'BRKT-PETG-002');

const reviewRows = filterRows(rows, '', 'needs-review');
assert.ok(reviewRows.length > 90);
assert.ok(reviewRows.every(row => row.needsReview === true || row.status === 'Needs Review'));

const openRows = filterRows(rows, '', 'open');
assert.ok(openRows.some(row => row.status === 'Partial'));
assert.ok(openRows.every(row => !['Paid', 'Done', 'Completed', 'Fulfilled', 'Refunded'].includes(row.status)));
assert.equal(matchesStatus({ status: 'Paid', needsReview: true }, 'paid'), true);
assert.equal(matchesStatus({ status: 'Draft', needsReview: true }, 'needs-review'), true);
assert.equal(matchesStatus({ status: 'Needs Review', needsReview: false }, 'needs-review'), true);
assert.equal(matchesStatus({ status: 'Refunded', needsReview: false }, 'paid'), true);
assert.equal(matchesStatus({ status: 'Refunded', needsReview: false }, 'open'), false);
const saleStatusRows = filterRows([
  { id: 1, status: 'Refunded', needsReview: false },
  { id: 2, status: 'Needs Review', needsReview: false },
  { id: 3, status: 'Paid', needsReview: false },
  { id: 4, status: 'Sent', needsReview: false },
], '', 'paid');
assert.deepEqual(saleStatusRows.map(row => row.id), [1, 3]);
const saleReviewRows = filterRows([
  { id: 1, status: 'Refunded', needsReview: false },
  { id: 2, status: 'Needs Review', needsReview: false },
  { id: 3, status: 'Paid', needsReview: true },
], '', 'needs-review');
assert.deepEqual(saleReviewRows.map(row => row.id), [2, 3]);

const model = buildEntityTableModel(rows, {
  filter: 'customer 11',
  statusFilter: '',
  state: { page: 99, pageSize: 25 },
  sortRows: input => [...input].sort((a, b) => b.id - a.id),
});

assert.equal(model.totalRows, 100);
assert.equal(model.totalPages, 4);
assert.equal(model.page, 3, 'Out-of-range filtered table pages should clamp to the last page.');
assert.equal(model.startRow, 76);
assert.equal(model.endRow, 100);
assert.equal(model.pageRows.length, 25);
assert.deepEqual(model.pageRows.map(row => row.id).slice(0, 3), [1124, 1123, 1122]);
assert.equal(model.pageRows.at(-1).id, 1100);
assert.ok(model.pageRows.every(row => row.id && row.customerName), 'Paged rows must preserve row ids for Edit/Archive actions.');

const start = performance.now();
for (let i = 0; i < 40; i += 1) {
  buildEntityTableModel(rows, {
    filter: i % 2 ? 'widget 7' : 'customer 10',
    statusFilter: i % 3 === 0 ? 'open' : '',
    state: { page: i, pageSize: 50 },
    sortRows: input => [...input].sort((a, b) => a.customerName.localeCompare(b.customerName)),
  });
}
const elapsedMs = performance.now() - start;
assert.ok(elapsedMs < 1500, `Medium-data filtering/paging took too long: ${elapsedMs.toFixed(1)}ms`);

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');

assert.ok(indexSource.includes('/js/entity-table-state.js?v=1'), 'Main shell should load the entity table helper before app.js.');
assert.ok(appSource.includes('EpataEntityTableState?.buildEntityTableModel'), 'Generic ledger tables should use the entity table helper.');
assert.ok(appSource.includes('appState.globalSearchTimer = setTimeout(() => showPage'), 'Global search should debounce page rendering.');
assert.ok(appSource.includes('), 220);'), 'Global search debounce should have a stable delay.');
assert.ok(appSource.includes('pageRows.map(row =>'), 'Generic table render should render actions from the paged row set.');
assert.ok(appSource.includes('data-edit="${row.id}"'), 'Filtered/paged rows should preserve Edit row actions.');
assert.ok(appSource.includes('data-delete="${row.id}"'), 'Filtered/paged rows should preserve Archive row actions.');
assert.ok(appSource.includes('sorted.find(r => r.id == btn.dataset.edit)'), 'Edit actions should resolve against the filtered/sorted row set.');
assert.ok(appSource.includes("columns: ['name','sku','material','color','grams'"), 'Product catalog should show color next to SKU and material.');
assert.ok(appSource.includes("'Refunded','Needs Review'"), 'Sales status options should expose Refunded and Needs Review.');
assert.ok(appSource.includes('Paid / done / refunded'), 'Closed status filter label should mention refunded rows.');
assert.ok(appSource.includes("v.includes('refunded')"), 'Refunded status should render as a bad badge.');

console.log(JSON.stringify({
  EntityTableStateBehavior: 'pass',
  EntitySearchRound32: 'pass',
  EntityTableActionsRound32: 'pass',
  NeedsReviewFilterRound39: 'pass',
  TextProofSearchRound39: 'pass',
  SaleStatusRound42: 'pass',
  ProductCatalogSearchRound50: 'pass',
  MediumRows: rows.length,
  MediumLoopMs: Number(elapsedMs.toFixed(1)),
}));
