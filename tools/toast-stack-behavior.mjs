import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

class FakeElement {
  constructor(tagName, documentRef) {
    this.tagName = tagName;
    this.documentRef = documentRef;
    this.children = [];
    this.parent = null;
    this.attributes = {};
    this.style = {};
    this.className = '';
    this.textContent = '';
    this.innerHTML = '';
    this._id = '';
  }

  get id() {
    return this._id;
  }

  set id(value) {
    if (this._id) this.documentRef.byId.delete(this._id);
    this._id = value;
    if (value) this.documentRef.byId.set(value, this);
  }

  appendChild(child) {
    child.parent = this;
    this.children.push(child);
    return child;
  }

  remove() {
    if (!this.parent) return;
    this.parent.children = this.parent.children.filter(child => child !== this);
    this.parent = null;
  }

  setAttribute(name, value) {
    this.attributes[name] = String(value);
  }

  getAttribute(name) {
    return this.attributes[name];
  }
}

class FakeDocument {
  constructor() {
    this.byId = new Map();
    this.body = new FakeElement('body', this);
  }

  createElement(tagName) {
    return new FakeElement(tagName, this);
  }

  getElementById(id) {
    return this.byId.get(id) || null;
  }
}

const documentRef = new FakeDocument();
const timers = [];
const sandbox = {
  document: documentRef,
  setTimeout: callback => {
    timers.push(callback);
    return timers.length;
  },
};
sandbox.globalThis = sandbox;

const source = await readFile(new URL('../wwwroot/js/toast-stack.js', import.meta.url), 'utf8');
vm.runInNewContext(source, sandbox, { filename: 'toast-stack.js' });

const { showToast, escapeHtml } = sandbox.EpataToastStack;
assert.equal(escapeHtml('<b>duplicate</b>'), '&lt;b&gt;duplicate&lt;/b&gt;');

const first = showToast('Duplicate document number <EST-2026-0014>', {
  type: 'error',
  duration: 0,
  documentRef,
});
const second = showToast('Save failed: original unchanged', {
  type: 'error',
  duration: 0,
  documentRef,
});

const container = documentRef.getElementById('toast-container');
assert.ok(container, 'Toast container was not created.');
assert.equal(container.children.length, 2, 'Toast stack should keep multiple visible messages.');
assert.equal(first.getAttribute('role'), 'alert');
assert.equal(second.getAttribute('aria-live'), 'assertive');
assert.ok(first.innerHTML.includes('&lt;EST-2026-0014&gt;'), 'Toast text was not escaped.');
assert.ok(!first.innerHTML.includes('<EST-2026-0014>'), 'Toast emitted raw angle-bracket text.');

const expiring = showToast('Temporary notice', {
  duration: 5,
  documentRef,
  setTimeoutFn: sandbox.setTimeout,
});
assert.equal(container.children.length, 3);
timers.shift()();
assert.equal(expiring.style.opacity, '0');
timers.shift()();
assert.equal(container.children.length, 2, 'Expired toast should remove itself without removing older messages.');

console.log(JSON.stringify({
  ToastStackBehavior: 'pass',
  VisibleMessages: container.children.length,
  EscapedHtml: 'pass',
  Expiry: 'pass',
}));
