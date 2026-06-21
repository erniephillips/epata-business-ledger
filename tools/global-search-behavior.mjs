import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const sandbox = {};
sandbox.globalThis = sandbox;

const helperSource = await readFile(new URL('../wwwroot/js/global-search.js', import.meta.url), 'utf8');
vm.runInNewContext(helperSource, sandbox, { filename: 'global-search.js' });

const {
  normalizeSearchQuery,
  resultOpenCall,
  resultSubtitleParts,
  resultTitle,
  rowMatchesQuery,
  searchRowsByConfig,
} = sandbox.EpataGlobalSearch;

const asArray = value => JSON.parse(JSON.stringify(value));

const entries = [
  {
    key: 'sales',
    config: { nav: 'Sales', title: 'Sales / Income' },
    rows: [
      {
        id: 11,
        customerName: 'Acme Dental',
        orderNumber: 'ETSY-4455',
        productName: 'Tray organizer',
        sourceProof: 'order proof (paid).pdf',
        platform: 'Etsy',
        status: 'Paid',
      },
    ],
  },
  {
    key: 'receivables',
    config: { nav: 'AR Ledger', title: 'AR Ledger Entry / Money Owed Tracker' },
    rows: [
      {
        id: 22,
        customerName: "O'Neil Labs",
        invoiceNumber: 'INV-2026-0142',
        projectName: 'Fixture #A/B',
        customerEmail: 'ap.oneil@example.com',
        status: 'Partial',
      },
    ],
  },
  {
    key: 'auditDocs',
    config: { nav: 'Audit Docs', title: 'Audit Docs / Proof Index' },
    rows: [
      {
        id: 33,
        fileName: 'receipt spaces (final).pdf',
        relatedRecordNumber: 'INV-2026-0142',
        filePathOrUrl: 'UploadedDocs/receipt spaces (final).pdf',
      },
    ],
  },
  {
    key: 'expenses',
    config: { nav: 'Expenses', title: 'Expenses / Paid Purchases' },
    rows: [
      {
        id: 44,
        vendorName: 'Bambu Store',
        receiptProof: 'AMS receipt.pdf',
        description: 'Filament refill',
      },
    ],
  },
  {
    key: 'products',
    config: { nav: 'Products / Costing', title: 'Products & Costing' },
    rows: [
      {
        id: 55,
        name: 'Gridfinity Bin',
        sku: 'GRID-BIN-01',
        material: 'PLA Matte',
      },
    ],
  },
  {
    key: 'customerJobs',
    config: { nav: 'Customer Jobs', title: 'Customer Jobs' },
    rows: [
      {
        id: 66,
        customerName: 'Delta Fabrication',
        jobName: 'Panel bracket prototype',
        relatedOrderNumber: 'JOB/DELTA/77',
      },
    ],
  },
];

assert.equal(normalizeSearchQuery('  Acme Dental  '), 'acme dental');
assert.equal(rowMatchesQuery(entries[0].rows[0], 'ACME'), true);
assert.equal(rowMatchesQuery(entries[0].rows[0], 'ETSY-4455'), true);
assert.equal(rowMatchesQuery(entries[0].rows[0], 'order proof (paid).pdf'), true);
assert.equal(rowMatchesQuery(entries[1].rows[0], 'INV-2026-0142'), true);
assert.equal(rowMatchesQuery(entries[1].rows[0], "O'Neil"), true);
assert.equal(rowMatchesQuery(entries[1].rows[0], '#A/B'), true);
assert.equal(rowMatchesQuery(entries[1].rows[0], 'ap.oneil@example.com'), true);
assert.equal(rowMatchesQuery(entries[3].rows[0], 'Bambu Store'), true);
assert.equal(rowMatchesQuery(entries[4].rows[0], 'GRID-BIN-01'), true);
assert.equal(rowMatchesQuery(entries[5].rows[0], 'Panel bracket prototype'), true);

const acmeResults = searchRowsByConfig(entries, 'acme dental', 8);
assert.equal(acmeResults.length, 1);
assert.equal(acmeResults[0].key, 'sales');
assert.equal(acmeResults[0].row.id, 11);

const invoiceResults = searchRowsByConfig(entries, 'INV-2026-0142', 8);
assert.deepEqual(invoiceResults.map(result => result.key), ['receivables', 'auditDocs']);

const jobResults = searchRowsByConfig(entries, 'JOB/DELTA/77', 8);
assert.equal(jobResults.length, 1);
assert.equal(jobResults[0].key, 'customerJobs');

assert.deepEqual(searchRowsByConfig(entries, 'no-row-will-match-this', 8), []);

assert.equal(resultTitle(entries[0].rows[0], 'Fallback'), 'Acme Dental');
assert.equal(resultTitle({ id: 99 }, 'Fallback'), 'Fallback');
assert.deepEqual(asArray(resultSubtitleParts(entries[1].rows[0])), ['INV-2026-0142', 'Partial']);
assert.equal(resultOpenCall('sales', { id: 11 }), "openLedgerEntityRecord('sales', 11)");
assert.equal(resultOpenCall('receivables', { id: 22 }), "openLedgerEntityRecord('receivables', 22)");
assert.equal(resultOpenCall('customerJobs', { id: 66 }), "openLedgerEntityRecord('customerJobs', 66)");
assert.equal(resultOpenCall('sales', {}), "showPage('sales')");
assert.equal(resultOpenCall('sales;alert(1)', { id: 1 }), "openLedgerEntityRecord('salesalert1', 1)");

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');

const helperScriptIndex = indexSource.indexOf('/js/global-search.js?v=1');
const appScriptIndex = indexSource.indexOf('/js/app.js');
assert.ok(helperScriptIndex > -1, 'Main shell should load the global search helper.');
assert.ok(appScriptIndex > -1 && helperScriptIndex < appScriptIndex, 'Global search helper should load before app.js.');
assert.ok(appSource.includes('EpataGlobalSearch?.searchRowsByConfig'), 'Global search should use shared match behavior.');
assert.ok(appSource.includes('EpataGlobalSearch?.resultOpenCall'), 'Global search result cards should use shared open-route behavior.');
assert.ok(appSource.includes('openLedgerEntityRecord'), 'Global search should be able to open matching row modals.');
assert.ok(appSource.includes('escapeHtml(title)'), 'Global search result titles should escape user-entered text.');
assert.ok(appSource.includes('escapeHtml(subtitle'), 'Global search result subtitles should escape user-entered text.');

console.log(JSON.stringify({
  GlobalSearchBehavior: 'pass',
  GlobalSearchRound35: 'pass',
  SearchFields: ['customer', 'invoice', 'order', 'proof', 'product', 'vendor', 'job', 'special-characters'],
  OpenActions: 'record-modal-or-page',
}));
