// EPATA Invoice Tool - shared input constraints and normalization.

const PHONE_IDS = new Set(['customerPhone', 'businessPhone']);
const EMAIL_IDS = new Set(['customerEmail', 'businessEmail']);
const URL_IDS = new Set(['businessWebsite']);
const REQUIRED_DOCUMENT_FIELDS = {
  docDate: 'Document date',
  customerName: 'Customer name',
  paymentMethod: 'Payment method',
};

const TEXT_LIMITS = {
  docNumber: 80,
  customerName: 160,
  preparedFor: 160,
  customerEmail: 160,
  customerAddress: 500,
  projectName: 220,
  material: 80,
  color: 80,
  infill: 80,
  projectDescription: 4000,
  projectNotes: 8000,
  pricingGuide: 8000,
  termsNotes: 8000,
  standardTurnaround: 500,
  rushTurnaround: 500,
  businessName: 160,
  businessLocation: 160,
  businessEmail: 160,
  businessEtsy: 300,
  businessInstagram: 160,
  businessFacebook: 300,
  recSearch: 220,
};

const NUMBER_LIMITS = {
  grams: [0, 1_000_000],
  gramRate: [0, 1_000],
  hours: [0, 100_000],
  hourRate: [0, 10_000],
  designHours: [0, 100_000],
  designRate: [0, 10_000],
  setupFee: [0, 1_000_000_000],
  postFee: [0, 1_000_000_000],
  rush: [0, 200],
  discount: [0, 1_000_000_000],
  taxRate: [0, 30],
  minimum: [0, 1_000_000_000],
  docDiscount: [0, 1_000_000_000],
  docRushPercent: [0, 200],
  docTaxRate: [0, 30],
  amountPaid: [0, 1_000_000_000],
  sCalcGramRate: [0, 1_000],
  sCalcHourRate: [0, 10_000],
  sCalcDesignRate: [0, 10_000],
  sCalcSetupFee: [0, 1_000_000_000],
  sCalcPostFee: [0, 1_000_000_000],
  sCalcMinimum: [0, 1_000_000_000],
};

let initialized = false;

export function initInputValidation() {
  if (initialized) return;
  initialized = true;

  document.querySelectorAll('input, textarea').forEach(node => {
    configureNode(node);
    normalizeNode(node);
  });
  document.addEventListener('input', onInput, true);
  document.addEventListener('blur', onBlur, true);
  document.addEventListener('change', onBlur, true);
}

export function validateDocumentInputs() {
  return validateNodes(documentNodes());
}

export function normalizeDocumentInputs() {
  normalizeNodes(documentNodes());
}

export function normalizeSettingsInputs() {
  normalizeNodes([
    ...document.querySelectorAll('#view-settings input, #view-settings textarea'),
  ]);
}

function documentNodes() {
  return [
    ...document.querySelectorAll('#view-builder input, #view-builder textarea, #view-builder select'),
    ...document.querySelectorAll('#view-calculator input:not([type="hidden"])'),
  ];
}

export function validateCalculatorInputs() {
  return validateNodes([
    ...document.querySelectorAll('#view-calculator input:not([type="hidden"])'),
  ]);
}

export function validateSettingsInputs() {
  return validateNodes([
    ...document.querySelectorAll('#view-settings input, #view-settings textarea, #view-settings select'),
  ]);
}

export function validateDocumentState(state = {}) {
  const docType = String(state.docType || 'ESTIMATE').toUpperCase();
  if (!['ESTIMATE', 'INVOICE'].includes(docType)) {
    return invalidState('docType', 'Document type must be Estimate or Invoice.');
  }

  for (const [field, label] of Object.entries(REQUIRED_DOCUMENT_FIELDS)) {
    if (!String(state[field] ?? '').trim()) {
      return invalidState(field, `${label} is required.`);
    }
  }

  const docNumber = String(state.docNumber || '').trim();
  if (docNumber && !/^[A-Z0-9]+(?:-[A-Z0-9]+)*$/.test(docNumber)) {
    return invalidState('docNumber', 'Document number can only use uppercase letters, numbers, and single hyphens.');
  }

  if (!isCompleteOrBlankUsPhone(state.customerPhone)) {
    return invalidState('customerPhone', 'Enter a complete 10-digit US phone number in the format (555) 123-4567.');
  }

  if (!isBlankOrValidEmail(state.customerEmail)) {
    return invalidState('customerEmail', 'Enter a valid email address or leave email blank.');
  }

  return { valid: true };
}

export function normalizeNumberValue(id, value, options = {}) {
  if (value === '') return '';
  const number = Number(value);
  if (!Number.isFinite(number)) return '';

  const [min, max] = numberLimitsForDescriptor(id, options.classNames || []);
  const step = Number(options.step);
  let normalized = Math.min(max, Math.max(min, number));
  if (Number.isFinite(step) && step > 0) {
    normalized = min + Math.round((normalized - min) / step) * step;
    normalized = Number(normalized.toFixed(decimalPlaces(step)));
  }
  return normalized;
}

export function formatUsPhone(value) {
  let digits = String(value ?? '').replace(/\D/g, '');
  if (digits.length === 11 && digits.startsWith('1')) digits = digits.slice(1);
  digits = digits.slice(0, 10);
  if (!digits) return '';
  if (digits.length < 4) return `(${digits}`;
  if (digits.length < 7) return `(${digits.slice(0, 3)}) ${digits.slice(3)}`;
  return `(${digits.slice(0, 3)}) ${digits.slice(3, 6)}-${digits.slice(6)}`;
}

export function isCompleteOrBlankUsPhone(value) {
  const digits = String(value ?? '').replace(/\D/g, '');
  return digits.length === 0 || digits.length === 10 || (digits.length === 11 && digits.startsWith('1'));
}

export function normalizeEmail(value) {
  return sanitizeText(value, TEXT_LIMITS.customerEmail).trim().toLowerCase();
}

export function isBlankOrValidEmail(value) {
  const clean = normalizeEmail(value);
  return !clean || /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(clean);
}

function validateNodes(nodes) {
  for (const node of nodes) {
    configureNode(node);
    normalizeNode(node);
    if (!node.checkValidity()) {
      return {
        valid: false,
        element: node,
        view: node.closest('#view-calculator') ? 'calculator'
          : node.closest('#view-settings') ? 'settings'
          : 'builder',
        message: node.validationMessage || `Check the value entered for ${fieldLabel(node)}.`,
      };
    }
  }
  return { valid: true };
}

function normalizeNodes(nodes) {
  nodes.forEach(node => {
    configureNode(node);
    normalizeNode(node);
  });
}

function onInput(event) {
  const node = event.target;
  if (!(node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement)) return;
  configureNode(node);

  if (PHONE_IDS.has(node.id)) {
    node.value = formatUsPhone(node.value);
    setPhoneValidity(node);
    return;
  }

  if (node.id === 'docNumber') {
    node.value = node.value.toUpperCase()
      .replace(/[^A-Z0-9-]/g, '')
      .replace(/-{2,}/g, '-');
  } else if (isPlainTextNode(node)) {
    node.value = sanitizeText(node.value, node.maxLength);
  }
}

function onBlur(event) {
  const node = event.target;
  if (!(node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement)) return;
  configureNode(node);
  normalizeNode(node);
}

function configureNode(node) {
  if (!(node instanceof HTMLInputElement || node instanceof HTMLTextAreaElement)) return;
  if (node instanceof HTMLInputElement && ['file', 'hidden', 'color', 'checkbox'].includes(node.type)) return;

  if (Object.hasOwn(REQUIRED_DOCUMENT_FIELDS, node.id)) {
    node.required = true;
  }

  if (PHONE_IDS.has(node.id)) {
    node.inputMode = 'tel';
    node.maxLength = 14;
    node.pattern = '\\(\\d{3}\\) \\d{3}-\\d{4}';
    node.title = 'Use a 10-digit US phone number: (555) 123-4567';
    return;
  }

  if (EMAIL_IDS.has(node.id)) {
    node.maxLength = 160;
    node.inputMode = 'email';
  }

  if (URL_IDS.has(node.id)) node.maxLength = 500;

  if (node.id === 'docNumber') {
    node.maxLength = 80;
    node.pattern = '[A-Z0-9]+(?:-[A-Z0-9]+)*';
    node.title = 'Use uppercase letters, numbers, and single hyphens only.';
  }

  if (node.type === 'number') {
    const limits = numberLimitsFor(node);
    node.min = String(limits[0]);
    node.max = String(limits[1]);
    node.inputMode = 'decimal';
  }

  if (node instanceof HTMLTextAreaElement || ['text', 'search', 'tel', 'url', 'email'].includes(node.type)) {
    const maxLength = textLimitFor(node);
    if (maxLength > 0) node.maxLength = maxLength;
  }
}

function normalizeNode(node) {
  if (node instanceof HTMLInputElement && ['file', 'hidden', 'color', 'checkbox'].includes(node.type)) return;

  if (PHONE_IDS.has(node.id)) {
    node.value = formatUsPhone(node.value);
    setPhoneValidity(node);
    return;
  }

  if (node.type === 'number') {
    normalizeNumber(node);
    return;
  }

  if (EMAIL_IDS.has(node.id)) node.value = normalizeEmail(node.value);
  else if (URL_IDS.has(node.id)) node.value = sanitizeText(node.value, node.maxLength).trim();
  else if (node.id === 'docNumber') node.value = node.value.replace(/^-+|-+$/g, '');
  else if (isPlainTextNode(node)) node.value = sanitizeText(node.value, node.maxLength);
}

function setPhoneValidity(node) {
  node.setCustomValidity(isCompleteOrBlankUsPhone(node.value)
    ? ''
    : 'Enter a complete 10-digit US phone number in the format (555) 123-4567.');
}

function normalizeNumber(node) {
  if (node.value === '') return;
  const normalized = normalizeNumberValue(node.id, node.value, {
    classNames: [...node.classList],
    step: node.step,
  });
  if (normalized === '') {
    node.value = '';
    return;
  }
  node.value = String(normalized);
}

function numberLimitsFor(node) {
  return numberLimitsForDescriptor(node.id, [...node.classList]);
}

function numberLimitsForDescriptor(id, classNames = []) {
  const classes = new Set(classNames);
  if (classes.has('item-qty')) return [0, 1_000_000];
  if (classes.has('item-rate')) return [0, 1_000_000_000];
  return NUMBER_LIMITS[id] || [0, 1_000_000_000];
}

function textLimitFor(node) {
  if (node.classList.contains('item-desc')) return 240;
  if (node.classList.contains('item-details')) return 4000;
  return TEXT_LIMITS[node.id] || (node instanceof HTMLTextAreaElement ? 8000 : 500);
}

function isPlainTextNode(node) {
  return node instanceof HTMLTextAreaElement
    || ['text', 'search', 'tel'].includes(node.type);
}

function sanitizeText(value, maxLength) {
  const clean = String(value ?? '').replace(/[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]/g, '');
  return maxLength > 0 ? clean.slice(0, maxLength) : clean;
}

function decimalPlaces(value) {
  const text = String(value);
  return text.includes('.') ? text.length - text.indexOf('.') - 1 : 0;
}

function invalidState(field, message) {
  return {
    valid: false,
    field,
    view: ['grams', 'gramRate', 'hours', 'hourRate', 'designHours', 'designRate', 'setupFee', 'postFee', 'rush', 'discount', 'taxRate', 'minimum'].includes(field)
      ? 'calculator'
      : 'builder',
    message,
  };
}

function fieldLabel(node) {
  return node.closest('.field')?.querySelector('label')?.textContent?.trim()
    || node.getAttribute('placeholder')
    || node.id
    || 'this field';
}
