import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const mainHtml = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');
const mainApp = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const builderHtml = await readFile(new URL('../wwwroot/invoice-builder/index.html', import.meta.url), 'utf8');
const recordsApp = await readFile(new URL('../wwwroot/invoice-builder/js/records.js', import.meta.url), 'utf8');
const modalLifecycle = await readFile(new URL('../wwwroot/js/modal-lifecycle.js', import.meta.url), 'utf8');

function mustInclude(source, text, label) {
  assert.ok(source.includes(text), label);
}

function mustMatch(source, pattern, label) {
  assert.match(source, pattern, label);
}

mustMatch(
  mainHtml,
  /id="sidebarToggle"[^>]*aria-controls="primarySidebar"[^>]*aria-expanded="true"[^>]*aria-label="Collapse menu"/,
  'Sidebar toggle should expose controls, expanded state, and an accessible label.'
);
mustInclude(mainHtml, 'id="sidebarScrim"', 'Sidebar scrim should exist for mobile dismissal.');
mustMatch(mainHtml, /id="sidebarScrim"[^>]*aria-label="Close menu"/, 'Sidebar scrim should expose a close-menu label.');

for (const modalId of ['modal', 'breakdownModal']) {
  mustMatch(
    mainHtml,
    new RegExp(`id="${modalId}"[^>]*role="dialog"[^>]*aria-modal="true"[^>]*aria-labelledby=`),
    `${modalId} should expose dialog semantics.`
  );
}
mustMatch(mainHtml, /id="modalClose"[^>]*aria-label="Close"/, 'Main modal close button should expose a close label.');
mustMatch(mainHtml, /id="breakdownModalClose"[^>]*aria-label="Close"/, 'Breakdown modal close button should expose a close label.');
mustInclude(modalLifecycle, 'aria-hidden', 'Modal lifecycle helper should maintain aria-hidden state.');
mustInclude(modalLifecycle, 'focus()', 'Modal lifecycle helper should actively move focus.');

mustMatch(
  builderHtml,
  /id="invoicePdfImportFile"[^>]*aria-label="Import invoice or estimate PDF"/,
  'Standalone builder PDF import file input should be labelled.'
);
mustMatch(
  builderHtml,
  /id="importDbFile"[^>]*aria-label="Import database backup file"/,
  'Standalone builder database import file input should be labelled.'
);
mustMatch(builderHtml, /data-view="builder"[\s\S]*?<span>Builder<\/span>/, 'Invoice builder tab should have visible text.');
mustMatch(builderHtml, /data-view="records"[\s\S]*?<span>Records<\/span>/, 'Invoice records tab should have visible text.');

mustInclude(mainApp, 'aria-label="Choose proof files to upload"', 'Document Intake upload input should be labelled.');
mustInclude(mainApp, 'aria-label="Attach proof file for ${escapeAttr(field.label || field.name)}"', 'Generated proof upload file inputs should be labelled.');
mustInclude(mainApp, 'aria-label="Remove line item"', 'Invoice line remove icon button should be labelled.');
mustInclude(mainApp, 'id="pgFirst" aria-label="First page"', 'Entity table first-page icon button should be labelled.');
mustInclude(mainApp, 'id="pgLast" aria-label="Last page"', 'Entity table last-page icon button should be labelled.');

mustInclude(recordsApp, 'aria-label="Duplicate ${escapeHtml(r.docNumber ||', 'Records duplicate icon button should be labelled.');
mustInclude(recordsApp, 'aria-label="Archive ${escapeHtml(r.docNumber ||', 'Records archive icon button should be labelled.');
mustInclude(recordsApp, 'id="recPageFirst" aria-label="First records page"', 'Records first-page icon button should be labelled.');
mustInclude(recordsApp, 'id="recPageLast" aria-label="Last records page"', 'Records last-page icon button should be labelled.');

console.log(JSON.stringify({
  AccessibilityStaticBehavior: 'pass',
  ScreenReaderLabelsRound103: 'pass',
  CheckedAreas: [
    'sidebar',
    'modals',
    'file-inputs',
    'invoice-tabs',
    'generated-icon-buttons',
    'table-pagers'
  ]
}));
