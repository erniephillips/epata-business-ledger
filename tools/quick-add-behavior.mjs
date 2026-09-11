import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const sandbox = {};
sandbox.globalThis = sandbox;

const helperSource = await readFile(new URL('../wwwroot/js/quick-add.js', import.meta.url), 'utf8');
vm.runInNewContext(helperSource, sandbox, { filename: 'quick-add.js' });

const {
  applyQuickAddOverrides,
  quickAddCards,
  quickAddPreset,
  quickAddTarget,
} = sandbox.EpataQuickAdd;

const asArray = value => JSON.parse(JSON.stringify(value));
const asObject = value => JSON.parse(JSON.stringify(value));
const fixedNow = new Date('2026-06-19T15:45:00.000Z');

const cards = asArray(quickAddCards());
assert.equal(cards.length, 17);
assert.deepEqual(cards.map(card => card.title), [
  'Estimate Sent (External)',
  'New Estimate PDF',
  'New Invoice PDF',
  'Etsy Sale',
  'AI Upload Paid Marketplace Order',
  'Direct Paid Sale',
  'Open Invoice / AR',
  'Bill / AP',
  'Paid Expense',
  'Damaged / Lost Order',
  'Equipment / Asset Purchase',
  'Customer / Vendor Contact',
  'Product / Costing Row',
  'Action Item',
  'Audit Doc / Proof Index',
  'Log Customer Communication',
  'Open Printer Queue',
]);

const modalTargets = cards.filter(card => card.type === 'modal').map(quickAddTarget).map(asObject);
assert.deepEqual(modalTargets, [
  { action: 'modal', config: 'customerJobs', kind: 'estimateSent' },
  { action: 'modal', config: 'sales', kind: 'etsy' },
  { action: 'modal', config: 'sales', kind: 'directPaid' },
  { action: 'modal', config: 'receivables', kind: 'invoice' },
  { action: 'modal', config: 'bills', kind: 'bill' },
  { action: 'modal', config: 'expenses', kind: 'expense' },
  { action: 'modal', config: 'orderLosses', kind: 'orderLoss' },
  { action: 'modal', config: 'assets', kind: 'assetPurchase' },
  { action: 'modal', config: 'parties', kind: 'partyContact' },
  { action: 'modal', config: 'products', kind: 'productCosting' },
  { action: 'modal', config: 'actions', kind: 'actionItem' },
  { action: 'modal', config: 'auditDocs', kind: 'auditDoc' },
  { action: 'modal', config: 'communications', kind: 'communication' },
]);
assert.deepEqual(cards.filter(card => card.type === 'page').map(quickAddTarget).map(asObject), [
  { action: 'page', page: 'estimates' },
  { action: 'page', page: 'invoices' },
  { action: 'page', page: 'aiOperations' },
  { action: 'page', page: 'printerQueue' },
]);

const estimate = asObject(quickAddPreset('estimateSent', fixedNow));
assert.equal(estimate.platform, 'Direct');
assert.equal(estimate.status, 'Quoted');
assert.equal(estimate.jobDate, '2026-06-19');
assert.equal(estimate.jobType, 'Estimate');
assert.equal(estimate.paymentMethod, 'Unknown / Review');
assert.equal(estimate.needsReview, false);

const etsy = asObject(quickAddPreset('etsy', fixedNow));
assert.equal(etsy.platform, 'Etsy');
assert.equal(etsy.paymentMethod, 'Etsy Payments');
assert.equal(etsy.status, 'Paid');
assert.equal(etsy.saleDate, '2026-06-19');
assert.equal(etsy.quantity, 1);
assert.equal(etsy.includeInDashboard, true);
assert.equal(etsy.needsReview, true);

const directPaid = asObject(quickAddPreset('directPaid', fixedNow));
assert.equal(directPaid.platform, 'Direct');
assert.equal(directPaid.paymentMethod, 'Unknown / Review');
assert.equal(directPaid.status, 'Paid');
assert.equal(directPaid.includeInDashboard, true);

const invoice = asObject(quickAddPreset('invoice', fixedNow));
assert.equal(invoice.status, 'Sent');
assert.equal(invoice.invoiceDate, '2026-06-19');
assert.equal(invoice.includeInCashReports, false);
assert.equal(invoice.needsReview, false);

const bill = asObject(quickAddPreset('bill', fixedNow));
assert.equal(bill.status, 'Unpaid');
assert.equal(bill.billDate, '2026-06-19');
assert.equal(bill.taxDeductible, true);

const expense = asObject(quickAddPreset('expense', fixedNow));
assert.equal(expense.expenseDate, '2026-06-19');
assert.equal(expense.countedExpense, true);
assert.equal(expense.businessUsePercent, 100);
assert.equal(expense.taxBucket, 'Review');
assert.equal(expense.deductibleStatus, 'Review');
assert.equal(expense.needsReview, true);

const orderLoss = asObject(quickAddPreset('orderLoss', fixedNow));
assert.equal(orderLoss.incidentDate, '2026-06-19');
assert.equal(orderLoss.platform, 'Other');
assert.equal(orderLoss.incidentType, 'Damaged in transit');
assert.equal(orderLoss.resolution, 'Replacement / reship');
assert.equal(orderLoss.countInTaxReports, true);
assert.equal(orderLoss.needsReview, true);

const asset = asObject(quickAddPreset('assetPurchase', fixedNow));
assert.equal(asset.purchaseDate, '2026-06-19');
assert.equal(asset.inServiceDate, '2026-06-19');
assert.equal(asset.category, 'Equipment');
assert.equal(asset.businessUsePercent, 100);
assert.equal(asset.taxTreatment, 'Review');
assert.equal(asset.countedExpenseThisYear, false);

const party = asObject(quickAddPreset('partyContact', fixedNow));
assert.equal(party.partyType, 'Customer');
assert.equal(party.defaultPlatform, 'Direct');

const product = asObject(quickAddPreset('productCosting', fixedNow));
assert.equal(product.category, '3D Printed Product');
assert.equal(product.material, 'PLA');
assert.equal(product.needsReview, true);

const actionItem = asObject(quickAddPreset('actionItem', fixedNow));
assert.equal(actionItem.area, 'General');
assert.equal(actionItem.priority, 'Normal');
assert.equal(actionItem.status, 'Open');

const auditDoc = asObject(quickAddPreset('auditDoc', fixedNow));
assert.equal(auditDoc.documentDate, '2026-06-19');
assert.equal(auditDoc.documentType, 'Other');
assert.equal(auditDoc.needsReview, true);

const communication = asObject(quickAddPreset('communication', fixedNow));
assert.equal(communication.occurredAt, '2026-06-19T15:45');
assert.equal(communication.direction, 'Outgoing');
assert.equal(communication.channel, 'Email');
assert.equal(communication.followUpStatus, 'None');

const queue = asObject(quickAddPreset('queue', fixedNow));
assert.equal(queue.queueDate, '2026-06-19');
assert.equal(queue.priority, 'Normal');
assert.equal(queue.status, 'Queued');
assert.equal(queue.quantity, 1);
assert.equal(queue.plateCount, 1);
assert.equal(queue.progressPercent, 0);
assert.equal(queue.failureCount, 0);

assert.deepEqual(asObject(applyQuickAddOverrides(etsy, { customerName: 'Acme Buyer', quantity: 3 })), {
  ...etsy,
  customerName: 'Acme Buyer',
  quantity: 3,
});
assert.deepEqual(asObject(quickAddPreset('unknown-kind', fixedNow)), {});

const cloned = quickAddCards();
cloned[0].title = 'Changed';
assert.equal(quickAddCards()[0].title, 'Estimate Sent (External)', 'quickAddCards should return a defensive copy.');

const requiredCreateConfigs = ['sales', 'expenses', 'orderLosses', 'customerJobs', 'products', 'actions', 'auditDocs', 'receivables', 'parties'];
for (const config of requiredCreateConfigs) {
  assert.ok(
    modalTargets.some(target => target.config === config),
    `Quick Add is missing a create card for ${config}.`,
  );
}

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');

const helperScriptIndex = indexSource.indexOf('/js/quick-add.js?v=1');
const appScriptIndex = indexSource.indexOf('/js/app.js');
assert.ok(helperScriptIndex > -1, 'Main shell should load the Quick Add helper.');
assert.ok(appScriptIndex > -1 && helperScriptIndex < appScriptIndex, 'Quick Add helper should load before app.js.');
assert.ok(appSource.includes('EpataQuickAdd?.quickAddCards'), 'Quick Add page should render from shared card definitions.');
assert.ok(appSource.includes('EpataQuickAdd?.quickAddPreset'), 'quickOpen should use shared preset behavior.');
assert.ok(appSource.includes('EpataQuickAdd?.applyQuickAddOverrides'), 'quickOpen should merge overrides through the helper.');
assert.ok(appSource.includes('modalSaveGuard: window.EpataModalSaveState?.createModalSaveGuard'), 'Quick Add modal saves should use the shared duplicate-click save guard.');
assert.ok(appSource.includes('if (guard ? !guard.tryStart() : session.saveInFlight)'), 'Quick Add modal saves should ignore accidental double-click submissions.');
assert.ok(appSource.includes('saveGuard: window.EpataModalSaveState?.createModalSaveGuard?.()'), 'Each Quick Add modal instance should receive an independent duplicate-click guard.');
assert.ok(appSource.includes("qsa('.quick-card[data-page]')"), 'Quick Add page cards should route to pages.');
assert.ok(appSource.includes("qsa('.quick-card[data-config]')"), 'Quick Add modal cards should route to modals.');
assert.ok(appSource.includes('data-config="${escapeAttr(config)}"'), 'Quick Add modal card config should be attribute-escaped.');
assert.ok(appSource.includes('data-page="${escapeAttr(page)}"'), 'Quick Add link card page should be attribute-escaped.');
assert.ok(appSource.includes('<strong>${escapeHtml(title)}</strong>'), 'Quick Add card titles should be HTML-escaped.');

console.log(JSON.stringify({
  QuickAddBehavior: 'pass',
  QuickAddRound36: 'pass',
  QuickAddCreatesRound37: 'pass',
  QuickAddDoubleClickRound84: 'pass',
  Cards: cards.length,
  ModalCards: modalTargets.length,
  PageCards: cards.length - modalTargets.length,
  RequiredCreateConfigs: requiredCreateConfigs,
}));
