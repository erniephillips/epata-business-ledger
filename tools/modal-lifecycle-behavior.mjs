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

const documentRef = new FakeDocument();
const sandbox = { document: documentRef };
sandbox.globalThis = sandbox;

const lifecycleSource = await readFile(new URL('../wwwroot/js/modal-lifecycle.js', import.meta.url), 'utf8');
vm.runInNewContext(lifecycleSource, sandbox, { filename: 'modal-lifecycle.js' });

const { openModalSurface, closeModalSurface, isModalOpen } = sandbox.EpataModalLifecycle;

const opener = new FakeElement('button', documentRef, 'openCustomerModal');
const backdrop = new FakeElement('div', documentRef, 'modalBackdrop');
const modal = new FakeElement('section', documentRef, 'modal');
const closeButton = new FakeElement('button', documentRef, 'modalClose');
const form = new FakeElement('form', documentRef, 'modalForm');
const firstInput = new FakeElement('input', documentRef, 'field_customerName');
const saveButton = new FakeElement('button', documentRef, 'modalSave');

backdrop.classList.add('hidden');
modal.classList.add('hidden');
documentRef.body.appendChild(opener);
documentRef.body.appendChild(backdrop);
documentRef.body.appendChild(modal);
modal.appendChild(closeButton);
modal.appendChild(form);
form.appendChild(firstInput);
modal.appendChild(saveButton);

opener.focus();
assert.equal(documentRef.activeElement, opener);
const focused = openModalSurface(modal, backdrop, {
  documentRef,
  preferredSelectors: ['#modalForm input, #modalForm select, #modalForm textarea', '#modalSave', '#modalClose'],
});
assert.equal(focused, firstInput, 'Opening the data-entry modal should focus the first form field.');
assert.equal(documentRef.activeElement, firstInput);
assert.equal(isModalOpen(modal), true);
assert.equal(modal.classList.contains('hidden'), false);
assert.equal(backdrop.classList.contains('hidden'), false);
assert.equal(modal.getAttribute('tabindex'), '-1');

const restored = closeModalSurface(modal, backdrop, { documentRef });
assert.equal(restored, opener, 'Closing the modal should restore focus to the opener.');
assert.equal(documentRef.activeElement, opener);
assert.equal(isModalOpen(modal), false);
assert.equal(modal.classList.contains('hidden'), true);
assert.equal(backdrop.classList.contains('hidden'), true);

firstInput.disabled = true;
openModalSurface(modal, backdrop, {
  documentRef,
  preferredSelectors: ['#modalForm input, #modalSave', '#modalClose'],
});
assert.equal(documentRef.activeElement, saveButton, 'Disabled fields should be skipped when choosing modal focus.');

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');

assert.ok(indexSource.includes('/js/modal-lifecycle.js?v=1'), 'Main shell should load the modal lifecycle helper before app.js.');
assert.ok(appSource.includes('EpataModalLifecycle?.openModalSurface'), 'Main shell should open modals through the lifecycle helper.');
assert.ok(appSource.includes('EpataModalLifecycle?.closeModalSurface'), 'Main shell should close modals through the lifecycle helper.');
assert.ok(appSource.includes("qs('#modalClose').onclick = closeModal"), 'Close button should close the generic modal.');
assert.ok(appSource.includes("qs('#modalBackdrop').onclick = closeModal"), 'Backdrop should close the generic modal.');
assert.ok(appSource.includes("e.key === 'Escape'"), 'Escape key should be wired for modal closing.');
assert.ok(appSource.includes('closeDashboardBreakdown();'), 'Escape key should also close the dashboard breakdown modal.');

console.log(JSON.stringify({
  ModalLifecycleBehavior: 'pass',
  ModalKeyboardRound30: 'pass',
  FocusOnOpen: focused.id,
  FocusRestoredTo: restored.id,
}));
