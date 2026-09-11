import assert from 'node:assert/strict';
import vm from 'node:vm';
import { readFile } from 'node:fs/promises';

const helperSource = await readFile(new URL('../wwwroot/js/dashboard-state.js', import.meta.url), 'utf8');
const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');

const sandbox = {};
vm.createContext(sandbox);
vm.runInContext(helperSource, sandbox, { filename: 'dashboard-state.js' });

const renderer = sandbox.EpataDashboardState.createDashboardRenderer({
  escapeHtml: value => String(value ?? '').replace(/[&<>"']/g, char => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
  }[char])),
  escapeAttr: value => String(value ?? '').replace(/[&<>"']/g, char => ({
    '&': '&amp;',
    '<': '&lt;',
    '>': '&gt;',
    '"': '&quot;',
    "'": '&#39;'
  }[char])),
  formatMoney: value => `$${Number(value || 0).toFixed(2)}`,
  formatShortDate: value => String(value || '').slice(0, 10),
  emptyState: (title, detail) => `<div class="empty-state"><b>${title}</b><span>${detail}</span></div>`
});
const plain = value => JSON.parse(JSON.stringify(value));

const populatedBody = renderer.renderBreakdownBody({
  title: 'Gross Receipts',
  formula: 'Item sales + shipping - refunds',
  money: true,
  total: 123.45,
  items: [
    {
      date: '2026-06-20T12:00:00Z',
      sourceType: 'Sale',
      route: 'sales',
      id: 101,
      label: 'Ada Customer: Gear <Bracket>',
      detail: 'Order R101; Etsy; Paid',
      amount: 100
    },
    {
      date: '2026-06-19T12:00:00Z',
      sourceType: 'Refund',
      route: 'sales',
      id: 102,
      label: 'Return row',
      detail: 'Refund offset',
      amount: -12.5
    }
  ]
});

assert.ok(populatedBody.includes('2 contributing records.'), 'Populated breakdown should state contributing record count.');
assert.ok(populatedBody.includes('$123.45'), 'Populated breakdown should render the total.');
assert.ok(populatedBody.includes('Ada Customer: Gear &lt;Bracket&gt;'), 'Breakdown labels should be escaped.');
assert.ok(populatedBody.includes("openDashboardBreakdownRecord('sales',101)"), 'Breakdown row should preserve the route/id open target.');
assert.ok(populatedBody.includes('breakdown-amount negative'), 'Negative contributions should be visually classified.');

const zeroBody = renderer.renderBreakdownBody({
  title: 'Open AR',
  formula: 'Invoice total - amount paid',
  money: true,
  total: 0,
  items: []
});

assert.ok(zeroBody.includes('0 contributing records.'), 'Zero breakdown should state zero contributing records.');
assert.ok(zeroBody.includes('No contributing records.'), 'Zero breakdown should render an explicit empty state.');
assert.ok(zeroBody.includes('This total is currently zero.'), 'Zero breakdown should explain the empty state.');

const countBody = renderer.renderBreakdownBody({
  title: 'Needs Review',
  formula: 'Rows marked needs review',
  money: false,
  total: 2,
  items: [
    { sourceType: 'Sale', route: 'sales', id: 201, label: 'Sale review', detail: 'Missing proof', amount: 1 },
    { sourceType: 'Bill', route: 'bills', id: 202, label: 'Bill review', detail: 'Missing category', amount: 1 }
  ]
});

assert.ok(countBody.includes('2 rows'), 'Non-money breakdown should summarize row counts.');
assert.ok(countBody.includes('Sale review'), 'Non-money breakdown should still list records.');

const emptyLine = renderer.summarizeLineChart([], [{ key: 'grossReceipts' }]);
assert.deepEqual(plain(emptyLine), { kind: 'empty', reason: 'no-rows', rows: 0, series: 1 });

const allZeroLine = renderer.summarizeLineChart(
  [{ month: '2026-01', grossReceipts: 0, estimatedNet: 0 }],
  [{ key: 'grossReceipts' }, { key: 'estimatedNet' }]
);
assert.equal(allZeroLine.kind, 'chart', 'All-zero monthly rows should still be chartable.');
assert.equal(allZeroLine.allZero, true, 'All-zero monthly chart state should be explicit.');
assert.equal(allZeroLine.max, 1, 'All-zero line chart should keep a nonzero max span.');

const nonZeroLine = renderer.summarizeLineChart(
  [{ month: '2026-01', grossReceipts: 120, estimatedNet: -20 }],
  [{ key: 'grossReceipts' }, { key: 'estimatedNet' }]
);
assert.equal(nonZeroLine.kind, 'chart');
assert.equal(nonZeroLine.min, -20);
assert.equal(nonZeroLine.max, 120);

const emptyDonut = renderer.summarizeDonutChart([
  { label: 'Net', value: 0 },
  { label: 'Costs', value: 0 }
]);
assert.deepEqual(plain(emptyDonut), { kind: 'empty', reason: 'no-positive-values', total: 0, slices: 0 });

const nonZeroDonut = renderer.summarizeDonutChart([
  { label: 'Net', value: 40 },
  { label: 'Costs', value: 25 },
  { label: 'Tax', value: 0 }
]);
assert.equal(nonZeroDonut.kind, 'chart');
assert.equal(nonZeroDonut.total, 65);
assert.equal(nonZeroDonut.slices, 2);

assert.ok(indexSource.includes('/js/dashboard-state.js?v=1'), 'Main shell should load dashboard-state before app.js.');
assert.ok(indexSource.indexOf('/js/dashboard-state.js?v=1') < indexSource.indexOf('/js/app.js'), 'Dashboard helper should load before app.js.');
assert.ok(appSource.includes('EpataDashboardState?.createDashboardRenderer'), 'Dashboard modal should use the shared renderer.');
for (const key of ['grossReceipts', 'estimatedNet', 'openReceivables', 'openPayables', 'customerPaid', 'salesTaxMemo', 'knownCosts', 'needsReview']) {
  assert.ok(appSource.includes(`'${key}'`), `Dashboard should pass ${key} into a breakdown-enabled KPI/control.`);
}
assert.ok(appSource.includes('openDashboardBreakdown(${jsStringAttr(breakdownKey)})'), 'KPI buttons should call openDashboardBreakdown with an attribute-safe JavaScript string.');
assert.ok(appSource.includes('role="img" aria-label="Revenue and net trend chart"'), 'Line chart should expose an accessible rendered chart surface.');
assert.ok(appSource.includes("emptyState('No chart data yet.'"), 'Line chart should handle no-row chart data.');
assert.ok(appSource.includes("emptyState('No money mix yet.'"), 'Donut chart should handle all-zero money mix data.');

console.log(JSON.stringify({
  DashboardStateBehavior: 'pass',
  DashboardBreakdownRound101: 'pass',
  DashboardZeroBreakdownRound101: 'pass',
  DashboardChartsRound101: 'pass'
}));
