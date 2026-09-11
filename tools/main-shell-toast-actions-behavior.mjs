import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const readProjectFile = path => readFile(new URL(`../${path}`, import.meta.url), 'utf8');

const [
  mainShell,
  invoiceApp,
  invoiceRecords,
  invoiceUtils,
  modalSaveState,
  toastStack,
] = await Promise.all([
  readProjectFile('wwwroot/js/app.js'),
  readProjectFile('wwwroot/invoice-builder/js/app.js'),
  readProjectFile('wwwroot/invoice-builder/js/records.js'),
  readProjectFile('wwwroot/invoice-builder/js/utils.js'),
  readProjectFile('wwwroot/js/modal-save-state.js'),
  readProjectFile('wwwroot/js/toast-stack.js'),
]);

function includes(source, expected, label) {
  assert.ok(source.includes(expected), `${label} missing expected toast coverage: ${expected}`);
}

function matches(source, expected, label) {
  assert.match(source, expected, `${label} missing expected toast coverage: ${expected}`);
}

const checks = [
  () => includes(mainShell, 'EpataToastStack.showToast', 'main shell stacked toast integration'),
  () => includes(toastStack, 'toast-container', 'stacked toast helper container'),
  () => includes(invoiceUtils, "export function toast(msg, type = 'info', duration = 3000, options = {})", 'invoice-builder toast utility'),

  () => includes(mainShell, 'EpataModalSaveState?.modalSaveOutcome({ ok: true })', 'generic save success'),
  () => includes(mainShell, 'EpataModalSaveState?.modalSaveOutcome({ ok: false, error: err })', 'generic save failure'),
  () => includes(modalSaveState, "result.successMessage || 'Saved.'", 'generic save success message'),
  () => includes(modalSaveState, 'Save failed:', 'generic save failure message'),
  () => includes(invoiceApp, "toast(`Still saving ${textVal('docNumber') || 'document'}...`, 'info', 1600, { key: 'invoice-save-status' })", 'invoice-builder in-flight save dedupe'),
  () => includes(invoiceApp, "`${savePlan.action === 'update' ? 'Saved' : 'Created'} ${savedLabel}`, 'success', 3000, { key: 'invoice-save-status' })", 'invoice-builder save success'),
  () => includes(invoiceApp, "toast('Save failed: ' + err.message, 'error', 3000, { key: 'invoice-save-status' })", 'invoice-builder save failure'),

  () => includes(mainShell, "toast('Archived.', 'success')", 'generic archive success'),
  () => includes(mainShell, "toast('Document archived.')", 'document archive success'),
  () => includes(invoiceRecords, "refreshAfterCommittedMutation(`${label} archived`, 'info')", 'records archive success'),
  () => includes(invoiceApp, "toast('Archive failed: ' + e.message, 'error')", 'records archive failure'),

  () => includes(invoiceRecords, 'refreshAfterCommittedMutation(`${label} restored`)', 'records restore success'),
  () => includes(invoiceApp, "toast('Restore failed: ' + e.message, 'error')", 'records restore failure'),

  () => includes(mainShell, "toast('Proof file attached and indexed.', 'success')", 'proof attach upload success'),
  () => includes(mainShell, 'toast(`Proof upload failed: ${friendlyApiError(err.message)}`, \'error\')', 'proof attach upload failure'),
  () => includes(mainShell, 'toast(aggregate.errors.length ?', 'document intake upload completion'),
  () => includes(mainShell, "file${aggregate.errors.length === 1 ? '' : 's'} failed.", 'document intake per-file failure summary'),
  () => matches(invoiceApp, /toast\(`Mapped \$\{prefill\.docType === 'INVOICE' \? 'invoice' : 'estimate'\} PDF into an unsaved draft\.\$\{warningText\}`,\s*'success',\s*7000\)/, 'invoice PDF import upload success'),
  () => includes(invoiceApp, "toast('PDF import failed: ' + e.message, 'error', 7000)", 'invoice PDF import upload failure'),

  () => includes(mainShell, "toast('Listing text copied.', 'success')", 'listing copy success'),
  () => includes(mainShell, "toast('Calculator line copied.')", 'calculator copy success'),
  () => includes(invoiceApp, "toast('Copied to clipboard!', 'success', 2000)", 'invoice-builder copy success'),
  () => includes(invoiceApp, "toast('Copy failed', 'error')", 'invoice-builder copy failure'),
];

checks.forEach(check => check());

console.log(JSON.stringify({
  MainShellToastActionsBehavior: 'pass',
  ActionToastRound29: 'pass',
  CheckedActions: [
    'save',
    'archive',
    'restore',
    'upload',
    'failure',
    'copy',
  ],
}));
