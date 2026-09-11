import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const mainApp = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const builderApp = await readFile(new URL('../wwwroot/invoice-builder/js/app.js', import.meta.url), 'utf8');
const recordsApp = await readFile(new URL('../wwwroot/invoice-builder/js/records.js', import.meta.url), 'utf8');
const builderIndex = await readFile(new URL('../wwwroot/invoice-builder/index.html', import.meta.url), 'utf8');
const modalSave = await readFile(new URL('../wwwroot/js/modal-save-state.js', import.meta.url), 'utf8');
const invoiceSaveIntent = await readFile(new URL('../wwwroot/invoice-builder/js/save-intent.js', import.meta.url), 'utf8');

function mustInclude(source, text, label) {
  assert.ok(source.includes(text), label);
}

mustInclude(modalSave, 'tryStart()', 'Generic modal save helper should expose a tryStart in-flight guard.');
mustInclude(mainApp, 'modalSaveGuard: window.EpataModalSaveState?.createModalSaveGuard', 'Main app should create a modal save guard.');
mustInclude(mainApp, 'if (guard ? !guard.tryStart() : session.saveInFlight)', 'Generic modal save should ignore duplicate submits per modal instance.');
mustInclude(mainApp, "session.saveInFlight ? 'Saving...'", 'Generic modal save should show an in-flight label.');
mustInclude(mainApp, 'guard?.finish?.();', 'Generic modal save should release the guard in finally.');

mustInclude(mainApp, 'asyncActionKeys: new Set()', 'Main app should track non-modal in-flight actions.');
mustInclude(mainApp, 'async function runExclusiveAction', 'Main app should expose a shared rapid-submit guard.');
mustInclude(mainApp, 'appState.asyncActionKeys.has(actionKey)', 'Main app guard should block duplicate action keys.');
mustInclude(mainApp, 'button.disabled = true;', 'Main app guard should disable clicked buttons.');
mustInclude(mainApp, "archiveRow(config, btn.dataset.delete, btn)", 'Ledger archive buttons should pass the clicked button into the guard.');
mustInclude(mainApp, "markReviewed(config, sorted.find(r => r.id == btn.dataset.reviewed), btn)", 'Mark-reviewed buttons should pass the clicked button into the guard.');
mustInclude(mainApp, "runExclusiveAction(`archive:${config.route}:${id}`", 'Ledger archive should be keyed by route and id.');
mustInclude(mainApp, "runExclusiveAction(`review:${config.route}:${row.id}`", 'Mark-reviewed should be keyed by route and id.');
mustInclude(mainApp, "runExclusiveAction('legacy-invoice-import'", 'Legacy invoice import should be guarded.');
mustInclude(mainApp, "runExclusiveAction('document-intake-upload'", 'Document Intake upload should be guarded.');
mustInclude(mainApp, "runExclusiveAction(`invoice-doc-save:${id}`", 'Unified invoice editor save should be guarded.');
mustInclude(mainApp, "runExclusiveAction(`invoice-doc-duplicate:${id}`", 'Unified invoice duplicate should be guarded.');
mustInclude(mainApp, "runExclusiveAction(`invoice-doc-convert:${id}`", 'Unified estimate conversion should be guarded.');
mustInclude(mainApp, "runExclusiveAction(`invoice-doc-archive:${id}`", 'Unified invoice archive should be guarded.');

for (const pair of [
  ['runMarketplaceOrderImportBtn', 'Analyze Paid Order'],
  ['runProductImportBtn', 'Build Product Draft'],
  ['runJobPlanBtn', 'Build Job Plan'],
  ['runSlicerReadBtn', 'Read Slicer Output'],
  ['runListingWriterBtn', 'Write Listing'],
  ['askLedgerBtn', 'Ask Ledger']
]) {
  const [id, restoreLabel] = pair;
  mustInclude(mainApp, `qs('#${id}')`, `${id} should be wired.`);
  mustInclude(mainApp, `button.disabled = true`, `${id} should disable during work.`);
  mustInclude(mainApp, `button.textContent = '${restoreLabel}'`, `${id} should restore its label after work.`);
}
mustInclude(mainApp, 'appState.localAiActionInFlight', 'Local AI start/stop should track in-flight action state.');
mustInclude(mainApp, 'Local AI ${appState.localAiActionInFlight} is already in progress.', 'Local AI start/stop should reject duplicate rapid clicks.');
mustInclude(mainApp, "runExclusiveAction('marketplace-order-save'", 'Marketplace order save should use the shared cross-render rapid-submit guard.');
mustInclude(mainApp, "{ button, busyText: 'Saving paid order...' }", 'Marketplace order save should show in-flight state and restore its original label in finally.');

mustInclude(builderApp, 'let saveInFlight    = null;', 'Standalone builder should track save in-flight state.');
mustInclude(builderApp, 'if (saveInFlight)', 'Standalone builder should check existing save before starting another.');
mustInclude(builderApp, 'canReuseInFlightSave(saveInFlightIntent, requestedIntent)', 'Standalone builder should reuse identical in-flight saves.');
mustInclude(builderApp, 'Finish the current save before starting a different save action.', 'Standalone builder should block conflicting save actions.');
mustInclude(builderApp, 'saveInFlight = null;', 'Standalone builder should clear save in-flight state in finally.');
mustInclude(invoiceSaveIntent, 'canReuseInFlightSave', 'Save intent helper should expose in-flight save comparison.');
mustInclude(builderApp, 'const actionInFlight = new Set();', 'Standalone builder should track non-save in-flight actions.');
mustInclude(builderApp, 'async function runExclusiveToolAction', 'Standalone builder should expose a tool action guard.');
mustInclude(builderApp, "runExclusiveToolAction('pdf-draft-import'", 'Standalone PDF draft import should be guarded.');
mustInclude(builderIndex, 'Database restore unavailable', 'Standalone destructive DB restore should remain unavailable during live sessions.');

mustInclude(recordsApp, 'const _recordActionsInFlight = new Set();', 'Standalone records should track in-flight actions.');
mustInclude(recordsApp, 'async function runRecordAction', 'Standalone records should expose a record action guard.');
mustInclude(recordsApp, "function recordActionKey(id, kind = 'document')", 'Standalone record actions should share a typed record-level conflict key.');
mustInclude(recordsApp, 'return `${kind}:${Number(id || 0)}`;', 'Standalone record actions should be keyed by record kind and id.');
mustInclude(recordsApp, "return runRecordAction(id, 'Another action for this record is already in progress.'", 'Standalone duplicate should use the record-level guard.');
mustInclude(recordsApp, "return runRecordAction(id, 'Another action for this estimate is already in progress.'", 'Standalone conversion should use the record-level guard.');
mustInclude(recordsApp, 'return runRecordAction(id, `Another action for ${label} is already in progress.`', 'Standalone archive and restore should use the record-level guard.');

console.log(JSON.stringify({
  RapidSubmitBehavior: 'pass',
  RapidSubmitRound102: 'pass',
  GuardedAreas: [
    'modal-save',
    'ledger-archive-review',
    'document-upload',
    'legacy-import',
    'unified-invoice-editor',
    'standalone-builder-save-import',
    'standalone-record-actions',
    'ai-operations'
  ]
}));
