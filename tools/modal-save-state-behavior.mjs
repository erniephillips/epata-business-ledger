import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const sandbox = {};
sandbox.globalThis = sandbox;

const helperSource = await readFile(new URL('../wwwroot/js/modal-save-state.js', import.meta.url), 'utf8');
vm.runInNewContext(helperSource, sandbox, { filename: 'modal-save-state.js' });

const { createModalSaveGuard, extractErrorMessage, modalSaveOutcome } = sandbox.EpataModalSaveState;

assert.equal(extractErrorMessage(new Error('Plain backend failure')), 'Plain backend failure');
assert.equal(
  extractErrorMessage(new Error('{"message":"Customer Name is required."}')),
  'Customer Name is required.',
);
assert.equal(
  extractErrorMessage(new Error('{"title":"Validation failed."}')),
  'Validation failed.',
);

const success = modalSaveOutcome({ ok: true });
assert.equal(success.closeModal, true);
assert.equal(success.refreshPage, true);
assert.equal(success.toastMessage, 'Saved.');
assert.equal(success.toastType, 'success');

const failure = modalSaveOutcome({
  ok: false,
  error: new Error('{"message":"Vendor Name is required."}'),
});
assert.equal(failure.closeModal, false);
assert.equal(failure.refreshPage, false);
assert.equal(failure.toastMessage, 'Save failed: Vendor Name is required.');
assert.equal(failure.toastType, 'error');

const customSuccess = modalSaveOutcome({ success: true, successMessage: 'Updated.' });
assert.equal(customSuccess.toastMessage, 'Updated.');
assert.equal(customSuccess.closeModal, true);

const guard = createModalSaveGuard();
assert.equal(guard.isInFlight(), false);
assert.equal(guard.tryStart(), true, 'First save attempt should enter the in-flight state.');
assert.equal(guard.isInFlight(), true);
assert.equal(guard.tryStart(), false, 'Second save attempt while in flight should be blocked.');
guard.finish();
assert.equal(guard.isInFlight(), false);
assert.equal(guard.tryStart(), true);
guard.reset();
assert.equal(guard.isInFlight(), false);

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');

assert.ok(indexSource.includes('/js/modal-save-state.js?v=1'), 'Main shell should load the modal save-state helper before app.js.');
assert.ok(appSource.includes('EpataModalSaveState?.modalSaveOutcome({ ok: true })'), 'saveModal should use an explicit success close outcome.');
assert.ok(appSource.includes('EpataModalSaveState?.modalSaveOutcome({ ok: false, error: err })'), 'saveModal should use an explicit failed-save outcome.');
assert.ok(appSource.includes('EpataModalSaveState?.createModalSaveGuard'), 'saveModal should create a shared in-flight guard.');
assert.ok(appSource.includes('if (guard ? !guard.tryStart() : appState.modalSaveInFlight) return;'), 'saveModal should ignore duplicate clicks while a save is in flight.');
assert.ok(appSource.includes("saveButton.textContent = 'Saving...'"), 'saveModal should show an in-flight saving state.');
assert.ok(appSource.includes('saveButton.disabled = true'), 'saveModal should disable the save button while saving.');
assert.ok(appSource.includes('guard?.finish?.();'), 'saveModal should release the in-flight guard after success or failure.');
assert.ok(appSource.includes('if (outcome.closeModal) closeModal();'), 'saveModal should close only when the save outcome allows it.');
assert.ok(appSource.includes('if (outcome.refreshPage) await showPage(appState.currentPage);'), 'saveModal should refresh only when the save outcome allows it.');

console.log(JSON.stringify({
  ModalSaveStateBehavior: 'pass',
  ModalFailedSaveRound31: 'pass',
  ModalSaveDoubleClickRound84: 'pass',
  FailedSaveCloses: failure.closeModal,
  FailedSaveRefreshes: failure.refreshPage,
}));
