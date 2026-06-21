import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const sandbox = {};
sandbox.globalThis = sandbox;

const sidebarStateSource = await readFile(new URL('../wwwroot/js/sidebar-state.js', import.meta.url), 'utf8');
vm.runInNewContext(sidebarStateSource, sandbox, { filename: 'sidebar-state.js' });

const {
  persistSidebarCollapsed,
  resolveSidebarCollapsed,
  sidebarExpandedState,
} = sandbox.EpataSidebarState;

assert.equal(resolveSidebarCollapsed(null, true), true);
assert.equal(resolveSidebarCollapsed(null, false), false);
assert.equal(resolveSidebarCollapsed('true', false), true);
assert.equal(resolveSidebarCollapsed('false', true), false);
assert.equal(sidebarExpandedState({ isMobile: false, sidebarCollapsed: true }), false);
assert.equal(sidebarExpandedState({ isMobile: false, sidebarCollapsed: false }), true);
assert.equal(sidebarExpandedState({ isMobile: true, sidebarOpen: true }), true);
assert.equal(sidebarExpandedState({ isMobile: true, sidebarOpen: false }), false);

const fakeStorage = new Map();
const storage = {
  setItem(key, value) {
    fakeStorage.set(key, value);
  },
};
assert.equal(persistSidebarCollapsed(storage, 'epataSidebarCollapsed', true), 'true');
assert.equal(fakeStorage.get('epataSidebarCollapsed'), 'true');
assert.equal(persistSidebarCollapsed(storage, 'epataSidebarCollapsed', false), 'false');
assert.equal(fakeStorage.get('epataSidebarCollapsed'), 'false');

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');

const expectedGroups = [
  ['Command', ['dashboard', 'quickAdd', 'estimates', 'invoices', 'pricingCalculator', 'invoiceRecords', 'jobTimeline', 'aiEstimate', 'aiOperations', 'documentIntake']],
  ['Books', ['sales', 'receivables', 'bills', 'expenses', 'accounts']],
  ['Operations', ['customerJobs', 'communications', 'printerQueue', 'customers', 'vendors', 'products', 'assets', 'makerworld']],
  ['Control', ['aiReview', 'localAi', 'auditDocs', 'actions', 'taxPrep', 'taxObligations', 'mileage', 'importExport', 'admin', 'ledgerMap', 'workflowGuide', 'help']],
];

for (const [group, pages] of expectedGroups) {
  assert.ok(appSource.includes(`label: '${group}'`), `Missing sidebar group ${group}.`);
  for (const page of pages) {
    assert.ok(appSource.includes(`['${page}'`), `Missing sidebar page ${page} in ${group}.`);
  }
}

const explicitRoutes = [
  'dashboard',
  'globalSearch',
  'quickAdd',
  'invoiceCenter',
  'estimates',
  'invoices',
  'pricingCalculator',
  'invoiceRecords',
  'jobTimeline',
  'communications',
  'printerQueue',
  'aiEstimate',
  'aiOperations',
  'aiReview',
  'actions',
  'localAi',
  'customers',
  'vendors',
  'ledgerMap',
  'workflowGuide',
  'documentIntake',
  'taxPrep',
  'importExport',
  'admin',
  'help',
];
for (const page of explicitRoutes) {
  assert.ok(appSource.includes(`page === '${page}'`), `showPage is missing explicit route ${page}.`);
}

const configRoutes = ['sales', 'receivables', 'bills', 'expenses', 'accounts', 'customerJobs', 'products', 'assets', 'makerworld', 'auditDocs', 'taxObligations', 'mileage'];
for (const page of configRoutes) {
  assert.ok(appSource.includes(`${page}: {`), `Missing config route ${page}.`);
}
assert.ok(appSource.includes('else await renderEntity(renderTarget, configs[page]);'), 'showPage should fall back to config-backed entity pages.');

assert.ok(indexSource.includes('/js/sidebar-state.js?v=1'), 'Main shell should load the sidebar state helper before app.js.');
assert.ok(appSource.includes('EpataSidebarState?.resolveSidebarCollapsed'), 'initializeSidebar should use the sidebar state helper.');
assert.ok(appSource.includes('EpataSidebarState?.persistSidebarCollapsed'), 'toggleSidebar should persist collapsed state through the helper.');
assert.ok(appSource.includes('EpataSidebarState?.sidebarExpandedState'), 'updateSidebarToggle should use the sidebar state helper.');
assert.ok(appSource.includes("localStorage.getItem(sidebarPreferenceKey)"), 'Sidebar preference should be read from localStorage.');
assert.ok(appSource.includes("document.body.classList.toggle('sidebar-collapsed', startCollapsed)"), 'Saved sidebar preference should apply to body class on initialization.');
assert.ok(appSource.includes("navGroups.map(group"), 'renderNav should render all configured sidebar groups.');
assert.ok(appSource.includes('data-page="${id}"'), 'renderNav should attach route ids to nav buttons.');

const totalSidebarPages = expectedGroups.reduce((sum, [, pages]) => sum + pages.length, 0);

console.log(JSON.stringify({
  SidebarNavigationBehavior: 'pass',
  SidebarNavRound33: 'pass',
  SidebarPreferenceRound33: 'pass',
  Groups: expectedGroups.map(([group]) => group),
  SidebarPages: totalSidebarPages,
}));
