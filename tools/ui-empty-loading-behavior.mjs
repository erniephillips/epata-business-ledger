import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const mainApp = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const builderHtml = await readFile(new URL('../wwwroot/invoice-builder/index.html', import.meta.url), 'utf8');
const builderApp = await readFile(new URL('../wwwroot/invoice-builder/js/app.js', import.meta.url), 'utf8');
const recordsApp = await readFile(new URL('../wwwroot/invoice-builder/js/records.js', import.meta.url), 'utf8');

function mustInclude(source, text, label) {
  assert.ok(source.includes(text), label);
}

function mustMatch(source, pattern, label) {
  assert.match(source, pattern, label);
}

function mustNotInclude(source, text, label) {
  assert.ok(!source.includes(text), label);
}

const mainEmptyStates = [...mainApp.matchAll(/emptyState\('([^']+)',\s*'([^']+)'\)/g)]
  .map(match => ({ title: match[1], detail: match[2] }));

assert.ok(mainEmptyStates.length >= 20, 'Main shell should have broad empty-state coverage across pages.');
for (const state of mainEmptyStates) {
  assert.ok(state.title.trim().length >= 8, `Empty-state title is too terse: ${state.title}`);
  assert.ok(state.detail.trim().length >= 16, `Empty-state detail is too terse for ${state.title}`);
}

for (const expected of [
  'No monthly data yet.',
  'No chart data yet.',
  'No money mix yet.',
  'No matching timelines.',
  'No matching customer communications.',
  'No matching rows.',
  'No linked rows.',
  'No upload yet.',
  'No analysis yet.',
  'No paid marketplace order analyzed yet.',
  'No Product draft yet.',
  'No job plan yet.',
  'No slicer analysis yet.',
  'No listing draft yet.',
  'No question asked yet.',
  'No duplicate or reconciliation findings.',
  'No review recommendations.',
  'Start typing above.',
  'No results found.',
  'No estimates or invoices yet.',
  'No calculation audit findings.',
  'No tax obligations generated yet.',
  'No category totals yet.',
]) {
  mustInclude(mainApp, expected, `Missing specific empty state: ${expected}`);
}

mustNotInclude(mainApp, '<p class="muted">Nothing here.</p>', 'Printer queue should not use vague empty copy.');
mustInclude(mainApp, 'No ${escapeHtml(status.toLowerCase())} prints.', 'Printer queue columns should name the empty status.');
mustInclude(mainApp, 'Queue items will appear here when their status changes.', 'Printer queue empty states should explain what happens next.');

mustMatch(
  mainApp,
  /el\.innerHTML = `<div class="card"><p>Loading\.\.\.<\/p><\/div>`;\s*try \{/,
  'Main shell should show a loading placeholder immediately before async page rendering.'
);
mustInclude(mainApp, 'const renderTarget = guardedRenderTarget(el, renderSequence);', 'Main shell should guard async page rendering against stale writes.');
mustInclude(mainApp, 'if (page === \'dashboard\') await renderDashboard(renderTarget);', 'Dashboard async render should replace loading content.');
mustInclude(mainApp, 'else if (page === \'taxPrep\') await renderTaxPrep(renderTarget);', 'Tax Prep async render should replace loading content.');
mustInclude(mainApp, 'else if (page === \'admin\') await renderAdmin(renderTarget);', 'Admin async render should replace loading content.');
mustInclude(mainApp, 'if (renderSequence === pageRenderSequence) {', 'Page render errors should not replace a newer page render.');
mustInclude(mainApp, 'el.innerHTML = `<div class="card"><h2>Something broke</h2><p>${escapeHtml(err.message)}</p></div>`;', 'Page render errors should replace loading content with visible error copy.');

mustInclude(builderHtml, 'Loading…', 'Standalone builder dashboard should show a loading state before records arrive.');
mustInclude(builderApp, "setDbStatus('Connecting…', 'loading');", 'Standalone builder should expose connecting state.');
mustInclude(builderApp, "setDbStatus('Ready', 'ready');", 'Standalone builder should clear loading state after successful health check.');
mustInclude(builderApp, "setDbStatus('Server offline', 'error');", 'Standalone builder should clear loading state with an offline error.');
mustInclude(builderApp, 'Saved estimates and invoices will appear here after you create them.', 'Standalone builder recent-records empty state should explain what happens next.');
mustInclude(recordsApp, 'No records found', 'Standalone records should show a no-records empty title.');
mustInclude(recordsApp, 'Try adjusting your filters or create a new estimate.', 'Standalone records empty state should explain recovery.');

console.log(JSON.stringify({
  UiEmptyLoadingBehavior: 'pass',
  EmptyStatesRound105: 'pass',
  LoadingStatesRound105: 'pass',
  EmptyStateCount: mainEmptyStates.length
}));
