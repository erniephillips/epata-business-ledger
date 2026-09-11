import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

class FakeClassList {
  constructor(...initial) {
    this.values = new Set(initial);
  }

  add(value) {
    this.values.add(value);
  }

  remove(value) {
    this.values.delete(value);
  }

  contains(value) {
    return this.values.has(value);
  }
}

class FakeElement {
  constructor(tagName, documentRef, id = '') {
    this.tagName = tagName.toLowerCase();
    this.documentRef = documentRef;
    this.id = id;
    this.children = [];
    this.parent = null;
    this.attributes = {};
    this.classList = new FakeClassList();
    this.disabled = false;
    this.hidden = false;
    this.isConnected = true;
  }

  appendChild(child) {
    child.parent = this;
    this.children.push(child);
    return child;
  }

  focus() {
    this.documentRef.activeElement = this;
  }

  setAttribute(name, value) {
    this.attributes[name] = String(value);
  }

  getAttribute(name) {
    return this.attributes[name] || '';
  }

  querySelectorAll(selector) {
    const selectors = selector.split(',').map(item => item.trim()).filter(Boolean);
    return descendantsOf(this).filter(element => selectors.some(part => matchesSelector(element, part)));
  }
}

class FakeDocument {
  constructor() {
    this.body = new FakeElement('body', this, 'body');
    this.activeElement = this.body;
  }
}

function descendantsOf(element) {
  return element.children.flatMap(child => [child, ...descendantsOf(child)]);
}

function matchesSelector(element, selector) {
  const parts = selector.split(/\s+/).filter(Boolean);
  if (parts.length > 1) {
    return matchesSelector(element, parts.at(-1)) && hasAncestorMatching(element, parts.slice(0, -1).join(' '));
  }
  if (selector.startsWith('#')) return element.id === selector.slice(1);
  if (selector === '[href]') return !!element.getAttribute('href');
  if (selector === '[tabindex]:not([tabindex="-1"])') return element.getAttribute('tabindex') !== '-1' && element.getAttribute('tabindex') !== '';
  return element.tagName === selector.toLowerCase();
}

function hasAncestorMatching(element, selector) {
  let current = element.parent;
  while (current) {
    if (matchesSelector(current, selector)) return true;
    current = current.parent;
  }
  return false;
}

function keyEvent(key, shiftKey = false) {
  return {
    key,
    shiftKey,
    prevented: false,
    preventDefault() {
      this.prevented = true;
    },
  };
}

function mustInclude(source, text, label) {
  assert.ok(source.includes(text), label);
}

function mustMatch(source, pattern, label) {
  assert.match(source, pattern, label);
}

const documentRef = new FakeDocument();
const sandbox = { document: documentRef };
sandbox.globalThis = sandbox;

const lifecycleSource = await readFile(new URL('../wwwroot/js/modal-lifecycle.js', import.meta.url), 'utf8');
vm.runInNewContext(lifecycleSource, sandbox, { filename: 'modal-lifecycle.js' });

const {
  focusableElements,
  openModalSurface,
  trapModalFocus,
} = sandbox.EpataModalLifecycle;

const opener = new FakeElement('button', documentRef, 'openModalButton');
const backdrop = new FakeElement('div', documentRef, 'modalBackdrop');
const modal = new FakeElement('section', documentRef, 'modal');
const firstInput = new FakeElement('input', documentRef, 'firstField');
const middleSelect = new FakeElement('select', documentRef, 'middleField');
const saveButton = new FakeElement('button', documentRef, 'modalSave');
const disabledButton = new FakeElement('button', documentRef, 'disabledAction');
disabledButton.disabled = true;

backdrop.classList.add('hidden');
modal.classList.add('hidden');
documentRef.body.appendChild(opener);
documentRef.body.appendChild(backdrop);
documentRef.body.appendChild(modal);
modal.appendChild(firstInput);
modal.appendChild(middleSelect);
modal.appendChild(saveButton);
modal.appendChild(disabledButton);

opener.focus();
openModalSurface(modal, backdrop, { documentRef });
assert.equal(JSON.stringify(focusableElements(modal).map(element => element.id)), JSON.stringify(['firstField', 'middleField', 'modalSave']));

saveButton.focus();
const forwardWrap = keyEvent('Tab');
assert.equal(trapModalFocus(modal, forwardWrap, { documentRef }), true, 'Tab from the last modal control should be trapped.');
assert.equal(forwardWrap.prevented, true);
assert.equal(documentRef.activeElement, firstInput, 'Tab from the last modal control should wrap to the first control.');

firstInput.focus();
const backwardWrap = keyEvent('Tab', true);
assert.equal(trapModalFocus(modal, backwardWrap, { documentRef }), true, 'Shift+Tab from the first modal control should be trapped.');
assert.equal(backwardWrap.prevented, true);
assert.equal(documentRef.activeElement, saveButton, 'Shift+Tab from the first modal control should wrap to the last control.');

middleSelect.focus();
const normalTab = keyEvent('Tab');
assert.equal(trapModalFocus(modal, normalTab, { documentRef }), false, 'Tab in the middle of the modal should keep normal browser tab order.');
assert.equal(normalTab.prevented, false);

const [
  mainCss,
  mainHtml,
  mainApp,
  builderCss,
  builderHtml,
  builderApp,
  builderRows,
  builderRecords,
  shellNavigationBehavior,
] = await Promise.all([
  readFile(new URL('../wwwroot/css/site.css', import.meta.url), 'utf8'),
  readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8'),
  readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8'),
  readFile(new URL('../wwwroot/invoice-builder/css/app.css', import.meta.url), 'utf8'),
  readFile(new URL('../wwwroot/invoice-builder/index.html', import.meta.url), 'utf8'),
  readFile(new URL('../wwwroot/invoice-builder/js/app.js', import.meta.url), 'utf8'),
  readFile(new URL('../wwwroot/invoice-builder/js/builder.js', import.meta.url), 'utf8'),
  readFile(new URL('../wwwroot/invoice-builder/js/records.js', import.meta.url), 'utf8'),
  readFile(new URL('../tools/shell-navigation-file-picker-behavior.mjs', import.meta.url), 'utf8'),
]);

for (const css of [mainCss, builderCss]) {
  mustInclude(css, 'button:focus-visible', 'Keyboard users need visible button focus.');
  mustInclude(css, 'a:focus-visible', 'Keyboard users need visible link focus.');
  mustInclude(css, 'input:focus-visible', 'Keyboard users need visible input focus.');
  mustInclude(css, 'outline-offset: 2px;', 'Focus rings should not be hidden under borders.');
}

mustInclude(mainApp, "if (e.key === 'Tab')", 'Main shell should handle Tab inside open modals.');
mustInclude(mainApp, "trapModalFocus?.(qs('#modal'), e)", 'Generic modal should trap Tab focus.');
mustInclude(mainApp, "trapModalFocus?.(qs('#breakdownModal'), e)", 'Dashboard breakdown modal should trap Tab focus.');
mustInclude(mainApp, "e.key === 'Escape'", 'Main shell should support Escape to close modal surfaces.');
mustInclude(mainApp, "qs('#modalSave').onclick = e => { e.preventDefault(); saveModal(); };", 'Generic modal save should be reachable as a real button.');
mustInclude(mainHtml, 'id="modalSave" class="primary-button"', 'Generic modal save control should be a real button.');
mustInclude(mainHtml, 'id="modalCancel" class="ghost-button"', 'Generic modal cancel control should be a real button.');
mustInclude(mainHtml, 'id="breakdownModalDone" class="primary-button"', 'Breakdown modal done control should be a real button.');
mustInclude(mainApp, '<button class="nav-button" data-page="${id}"', 'Sidebar navigation should render real buttons.');
mustInclude(mainApp, '<button class="table-link"', 'Generic table edit/archive actions should use real buttons.');
mustMatch(mainApp, /<button class="sort-header \$\{active \? 'active' : ''\}" type="button" data-sort-col=/, 'Generic table sort headers should use real buttons.');
mustInclude(mainApp, 'id="pgFirst" aria-label="First page"', 'Generic pager first control should be a labelled button.');
mustInclude(mainApp, 'id="pgLast" aria-label="Last page"', 'Generic pager last control should be a labelled button.');

for (const id of ['btnNewEstimate', 'btnNewInvoice', 'btnPreviewPdf', 'btnSaveNew', 'btnSaveDraft', 'btnDownloadPdf', 'btnImportPdfDraft', 'btnExportCsv', 'btnExportDb']) {
  mustMatch(builderHtml, new RegExp(`<button[^>]+id="${id}"`), `Standalone invoice builder control ${id} should be a real button.`);
}

mustInclude(builderRows, 'aria-label="Remove line item"', 'Standalone invoice line remove control should be keyboard-reachable and labelled.');
mustInclude(builderApp, '<button class="record-link" type="button"', 'Standalone recent-record document numbers should be real buttons.');
mustInclude(builderApp, 'escapeHtml(r.docNumber ||', 'Standalone recent-record document numbers should be escaped.');
mustMatch(builderRecords, /<button\s+type="button"\s+class="record-link"/, 'Standalone records customer links should be real buttons.');
mustInclude(builderRecords, 'id="recPageFirst" aria-label="First records page"', 'Standalone records first pager control should be labelled.');
mustInclude(builderRecords, 'id="recPageLast" aria-label="Last records page"', 'Standalone records last pager control should be labelled.');
mustInclude(builderApp, "document.addEventListener('keydown'", 'Standalone invoice builder should register keyboard shortcuts.');
mustInclude(shellNavigationBehavior, "if (key === 's')", 'Existing shell behavior should verify Ctrl+S save path.');
mustInclude(shellNavigationBehavior, "if (key === 'n' && e.shiftKey)", 'Existing shell behavior should verify Ctrl+Shift+N path.');
mustInclude(shellNavigationBehavior, 'e\\.preventDefault', 'Existing shell behavior should verify shortcut event suppression.');

console.log(JSON.stringify({
  KeyboardAccessibilityBehavior: 'pass',
  KeyboardReachabilityRound110: 'pass',
  ModalFocusTrapRound110: 'pass',
  FocusVisibleRound110: 'pass',
  InvoiceBuilderKeyboardRound110: 'pass',
  FocusableModalControls: ['firstField', 'middleField', 'modalSave'],
}));
