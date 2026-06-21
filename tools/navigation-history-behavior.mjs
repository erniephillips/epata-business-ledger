import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const sandbox = {};
sandbox.globalThis = sandbox;

const helperSource = await readFile(new URL('../wwwroot/js/navigation-history.js', import.meta.url), 'utf8');
vm.runInNewContext(helperSource, sandbox, { filename: 'navigation-history.js' });

const {
  browserHistoryMode,
  historyAfterPop,
  nextPageHistory,
  pageFromStateOrHash,
  pageHash,
  popBackTarget,
} = sandbox.EpataNavigationHistory;

const asArray = value => JSON.parse(JSON.stringify(value));
const asObject = value => JSON.parse(JSON.stringify(value));

assert.deepEqual(asArray(nextPageHistory([], '', 'dashboard')), []);
assert.deepEqual(asArray(nextPageHistory([], 'dashboard', 'sales')), ['dashboard']);
assert.deepEqual(asArray(nextPageHistory(['dashboard'], 'dashboard', 'sales')), ['dashboard']);
assert.deepEqual(asArray(nextPageHistory(['dashboard'], 'sales', 'receivables', { skipHistory: true })), ['dashboard']);

const longStack = Array.from({ length: 65 }, (_, index) => `page-${index}`);
assert.deepEqual(
  asArray(nextPageHistory(longStack, 'page-65', 'page-66', { maxLength: 60 })).slice(0, 3),
  ['page-6', 'page-7', 'page-8'],
);
assert.equal(nextPageHistory(longStack, 'page-65', 'page-66', { maxLength: 60 }).length, 60);

assert.deepEqual(asObject(popBackTarget(['dashboard', 'sales'], 'dashboard')), {
  page: 'sales',
  history: ['dashboard'],
});
assert.deepEqual(asObject(popBackTarget([], 'dashboard')), {
  page: 'dashboard',
  history: [],
});

assert.equal(pageHash('dashboard'), '');
assert.equal(pageHash('customerDetail:Acme Co'), '#customerDetail%3AAcme%20Co');
assert.equal(pageFromStateOrHash({ page: 'sales' }, '#dashboard'), 'sales');
assert.equal(pageFromStateOrHash(null, '#customerDetail%3AAcme%20Co'), 'customerDetail:Acme Co');
assert.equal(pageFromStateOrHash(null, ''), 'dashboard');

assert.equal(browserHistoryMode('sales', 'dashboard', {}), 'push');
assert.equal(browserHistoryMode('sales', 'dashboard', { replace: true }), 'replace');
assert.equal(browserHistoryMode('sales', 'dashboard', { fromPop: true }), 'none');
assert.equal(browserHistoryMode('sales', 'sales', {}), 'none');
assert.equal(browserHistoryMode('dashboard', '', {}), 'replace');

assert.deepEqual(asArray(historyAfterPop(['dashboard', 'sales'], 'sales')), ['dashboard']);
assert.deepEqual(asArray(historyAfterPop(['dashboard', 'sales'], 'receivables')), ['dashboard', 'sales']);

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');

const helperScriptIndex = indexSource.indexOf('/js/navigation-history.js?v=1');
const appScriptIndex = indexSource.indexOf('/js/app.js');
assert.ok(helperScriptIndex > -1, 'Main shell should load the navigation history helper.');
assert.ok(appScriptIndex > -1 && helperScriptIndex < appScriptIndex, 'Navigation history helper should load before app.js.');
assert.ok(appSource.includes('EpataNavigationHistory?.nextPageHistory'), 'showPage should use shared page-history behavior.');
assert.ok(appSource.includes('browserHistoryMode'), 'syncBrowserHistory should use shared browser-history mode behavior.');
assert.ok(appSource.includes('popBackTarget'), 'Back button should pop history through the helper.');
assert.ok(appSource.includes('pageFromStateOrHash'), 'Initial load and popstate should parse routes through the helper.');
assert.ok(appSource.includes('historyAfterPop'), 'Browser back/forward should keep app page history in sync.');
assert.ok(appSource.includes("window.addEventListener('popstate'"), 'Browser popstate handling is missing.');
assert.ok(appSource.includes('async function goBack()'), 'App Back button handler is missing.');
assert.ok(appSource.includes("qs('#appBackBtn').onclick = goBack"), 'App Back button is not wired.');

console.log(JSON.stringify({
  NavigationHistoryBehavior: 'pass',
  BrowserHistoryRound34: 'pass',
  BrowserModes: ['replace', 'push', 'none'],
  MaxHistory: 60,
}));
