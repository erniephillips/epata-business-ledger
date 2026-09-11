// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Pricing Calculator
// ═══════════════════════════════════════════════════════

import { el, money, plainMoney } from './utils.js?v=5';

const INPUT_IDS = [
  'grams','hours','designHours','setupFee','postFee',
  'gramRate','hourRate','designRate','minimum',
  'difficulty','rush','discount','taxRate',
];

const CALCULATOR_LINE_DESCRIPTIONS = [
  'Print setup / file preparation',
  'Material usage',
  'Machine print time',
  'Design / modeling time',
  'Post-processing / handling',
  'Material / difficulty surcharge',
  'Minimum charge adjustment',
];
const initializedCalculatorRoots = new WeakSet();

export function initCalculator() {
  const root = el('view-calculator');
  if (root && initializedCalculatorRoots.has(root)) return;
  if (root) initializedCalculatorRoots.add(root);
  const run = () => calculate();
  INPUT_IDS.forEach(id => {
    const node = el(id);
    if (!node) return;
    node.addEventListener('input', run);
    node.addEventListener('change', run);
  });

  syncDifficultyButtons();
  calculate();
}

export function calculatePricingFromState(state = {}) {
  const setup    = numberFromState(state, 'setupFee');
  const post     = numberFromState(state, 'postFee');
  const material = numberFromState(state, 'grams') * numberFromState(state, 'gramRate');
  const machine  = numberFromState(state, 'hours') * numberFromState(state, 'hourRate');
  const design   = numberFromState(state, 'designHours') * numberFromState(state, 'designRate');

  const baseSubtotal    = setup + post + material + machine + design;
  const difficultyFactor = numberFromState(state, 'difficulty') || 1;
  const rushPercent      = numberFromState(state, 'rush');
  const difficultyFee   = baseSubtotal * Math.max(0, difficultyFactor - 1);
  const afterDifficulty = baseSubtotal + difficultyFee;
  const rushFee         = afterDifficulty * Math.max(0, rushPercent / 100);
  const adjustedSubtotal = afterDifficulty + rushFee;
  const discount         = numberFromState(state, 'discount');
  const minimum          = numberFromState(state, 'minimum');
  const taxableAmount    = Math.max(minimum, adjustedSubtotal - discount);
  const tax              = taxableAmount * (numberFromState(state, 'taxRate') / 100);
  const total            = taxableAmount + tax;

  return {
    setup, post, material, machine, design,
    baseSubtotal, difficultyFactor, difficultyFee,
    rushPercent, rushFactor: 1 + rushPercent / 100, rushFee,
    adjustedSubtotal, discount, minimum, taxableAmount, tax, total,
  };
}

export function calculate() {
  const state = getCalcState();
  const calc = calculatePricingFromState(state);
  const {
    setup, post, material, machine, design,
    difficultyFee, rushFee, discount, tax, total,
  } = calc;

  setText('setupOut',    money(setup));
  setText('materialOut', money(material));
  setText('machineOut',  money(machine));
  setText('designOut',   money(design));
  setText('postOut',     money(post));
  setText('difficultyOut', money(difficultyFee));
  setText('rushOut',     money(rushFee));
  setText('discountOut', difficultyFee > 0 || rushFee > 0 ? '-' + money(discount) : '-' + money(discount));
  setText('taxOut',      money(tax));
  setText('totalOut',    money(total));
  updateFormulaText(calc, state);

  return calc;
}

function setText(id, text) {
  const e = el(id);
  if (e) e.textContent = text;
}

function updateFormulaText(calc, state = {}) {
  setText('materialFormula', `(${fmt(numberFromState(state, 'grams'))}g x $${fmt(numberFromState(state, 'gramRate'), 3)})`);
  setText('machineFormula', `(${fmt(numberFromState(state, 'hours'))}h x $${fmt(numberFromState(state, 'hourRate'))})`);
  setText('designFormula', `(${fmt(numberFromState(state, 'designHours'))}h x $${fmt(numberFromState(state, 'designRate'))})`);
  setText('difficultyFormula', calc.difficultyFactor > 1
    ? `(${money(calc.baseSubtotal)} x ${fmt(calc.difficultyFactor - 1, 2)})`
    : '(none)');
  setText('rushFormula', calc.rushPercent > 0
    ? `(${money(calc.baseSubtotal + calc.difficultyFee)} x ${fmt(calc.rushPercent)}%)`
    : '(none)');
  setText('taxFormula', numberFromState(state, 'taxRate') > 0
    ? `(${money(calc.taxableAmount)} x ${fmt(numberFromState(state, 'taxRate'))}%)`
    : '(none)');

  const minimumApplied = calc.taxableAmount === calc.minimum && calc.adjustedSubtotal - calc.discount < calc.minimum;
  const minRow = el('rMinimum');
  if (minRow) minRow.style.display = minimumApplied ? 'flex' : 'none';
  setText('minimumOut', minimumApplied ? money(calc.minimum) : '');

  setText(
    'baseFormulaText',
    `Base = ${money(calc.setup)} setup + ${money(calc.material)} material + ${money(calc.machine)} machine + ${money(calc.design)} design + ${money(calc.post)} post = ${money(calc.baseSubtotal)}`
  );
  setText(
    'difficultyExplainText',
    calc.difficultyFactor > 1
      ? `Difficulty adds ${money(calc.difficultyFee)} because ${money(calc.baseSubtotal)} x ${parseFloat(((calc.difficultyFactor - 1) * 100).toFixed(4))}% = ${money(calc.difficultyFee)}.`
      : 'Difficulty adds $0.00 because the multiplier is 1.0x.'
  );
  setText(
    'rushExplainText',
    calc.rushPercent > 0
      ? `Rush adds ${money(calc.rushFee)} from the difficulty-adjusted subtotal of ${money(calc.baseSubtotal + calc.difficultyFee)}.`
      : 'Rush adds $0.00. Use 25% to 50% when the job jumps the queue.'
  );
  setText(
    'minimumExplainText',
    minimumApplied
      ? `Minimum applied: ${money(calc.adjustedSubtotal - calc.discount)} is below your ${money(calc.minimum)} floor.`
      : `Minimum check passed: ${money(calc.adjustedSubtotal - calc.discount)} is above the ${money(calc.minimum)} floor.`
  );
  setText(
    'finalFormulaText',
    `Final = max(${money(calc.minimum)}, ${money(calc.adjustedSubtotal)} - ${money(calc.discount)}) + ${money(calc.tax)} tax = ${money(calc.total)}`
  );
}

function fmt(n, digits = 2) {
  const num = Number(n) || 0;
  return Number.isInteger(num) ? String(num) : num.toFixed(digits).replace(/0+$/, '').replace(/\.$/, '');
}

function numberFromState(state, key) {
  const n = parseFloat(state?.[key]);
  return isNaN(n) ? 0 : n;
}

export function getCalcState() {
  return Object.fromEntries(INPUT_IDS.map(id => [id, el(id)?.value ?? '']));
}

export function buildDefaultCalculatorState(config = {}) {
  return {
    grams: 0,
    hours: 0,
    designHours: 0,
    setupFee: config.calcSetupFee ?? 0,
    postFee: config.calcPostFee ?? 0,
    gramRate: config.calcGramRate ?? 0.05,
    hourRate: config.calcHourRate ?? 3,
    designRate: config.calcDesignRate ?? 25,
    minimum: config.calcMinimum ?? 15,
    difficulty: 1,
    rush: 0,
    discount: 0,
    taxRate: 0,
  };
}

export function restoreCalcState(state = {}) {
  INPUT_IDS.forEach(id => { if (el(id) && state[id] != null) el(id).value = state[id]; });
  syncDifficultyButtons();
  calculate();
}

export function syncDifficultyButtons() {
  const current = parseFloat(document.getElementById('difficulty')?.value) || 1;
  document.querySelectorAll('#difficultyGrid .diff-card').forEach(btn => {
    btn.classList.toggle('active', parseFloat(btn.dataset.val) === current);
  });
}

export function applyConfigDefaults(cfg) {
  if (!cfg) return;
  const map = {
    gramRate:   cfg.calcGramRate,
    hourRate:   cfg.calcHourRate,
    designRate: cfg.calcDesignRate,
    setupFee:   cfg.calcSetupFee,
    postFee:    cfg.calcPostFee,
    minimum:    cfg.calcMinimum,
  };
  Object.entries(map).forEach(([id, val]) => {
    if (val != null) { const e = el(id); if (e && !e.value) e.value = val; }
  });
  calculate();
}

export function buildCalculatorPushPlan(calc, state = {}, context = {}) {
  const r2 = n => Math.round((Number(n) || 0) * 100) / 100;
  const productName = String(context.productName || '').trim();
  const productDetails = String(context.productDetails || '').trim();
  const grams = numberFromState(state, 'grams');
  const hours = numberFromState(state, 'hours');
  const designHours = numberFromState(state, 'designHours');
  const gramRate = numberFromState(state, 'gramRate');
  const hourRate = numberFromState(state, 'hourRate');
  const designRate = numberFromState(state, 'designRate');
  const rushPercent = numberFromState(state, 'rush');
  const taxRate = numberFromState(state, 'taxRate');
  const lineItems = [];

  const add = (item) => lineItems.push({ ...item, source: 'calculator' });
  const addIf = (cond, desc, details, qty, rate) => {
    if (cond) add({ desc, details, qty, rate });
  };

  add({
    desc: productName || 'Print setup / file preparation',
    details: productName
      ? [productDetails, 'Calculated quote: slicing, orientation, supports, settings review, and project setup.'].filter(Boolean).join('\n')
      : 'Slicing, orientation, supports, settings review, and project setup.',
    qty: 1,
    rate: r2(calc.setup),
  });

  addIf(grams > 0, 'Material usage',
    `${grams} grams × ${plainMoney(gramRate)}/g`,
    grams, gramRate);

  addIf(hours > 0, 'Machine print time',
    `${hours} print hours × ${plainMoney(hourRate)}/hr`,
    hours, hourRate);

  addIf(designHours > 0, 'Design / modeling time',
    `${designHours} design hours × ${plainMoney(designRate)}/hr`,
    designHours, designRate);

  addIf(calc.post > 0, 'Post-processing / handling',
    'Cleanup, support removal, packaging, or special handling.',
    1, r2(calc.post));

  addIf(calc.difficultyFee > 0, 'Material / difficulty surcharge',
    `ABS/ASA or higher-risk print. Multiplier: ${calc.difficultyFactor.toFixed(2)}×`,
    1, r2(calc.difficultyFee));

  const preMinimumSubtotal = calc.baseSubtotal + calc.difficultyFee;
  const rushMultiplier = 1 + Math.max(0, rushPercent / 100);
  const minimumAdjustment = Math.max(0, ((calc.minimum + calc.discount) / rushMultiplier) - preMinimumSubtotal);
  addIf(minimumAdjustment > 0, 'Minimum charge adjustment',
    `Minimum quote floor: ${money(calc.minimum)}.`,
    1, r2(minimumAdjustment));

  return {
    lineItems,
    builderFields: {
      docDiscount: calc.discount.toFixed(2),
      docRushPercent: String(Math.round(Math.max(0, (calc.rushFactor - 1) * 100))),
      docTaxRate: taxRate.toFixed(3).replace(/\.?0+$/, ''),
    },
  };
}

export function pushToBuilder(calc, lineItemsFn, addLineItemFn, context = {}) {
  const tbody = el('lineItemsBody');
  if (!tbody) return;

  const calcDescriptions = new Set(CALCULATOR_LINE_DESCRIPTIONS);
  const productName = String(context.productName || '').trim();
  const plan = buildCalculatorPushPlan(calc, getCalcState(), context);

  Array.from(tbody.querySelectorAll('tr')).forEach(row => {
    const desc = row.querySelector('.item-desc')?.value?.trim() || '';
    const details = row.querySelector('.item-details')?.value?.trim() || '';
    const quantityText = row.querySelector('.item-qty')?.value?.trim() || '';
    const rateText = row.querySelector('.item-rate')?.value?.trim() || '';
    const isSelectedProductRow = productName && desc.toLowerCase() === productName.toLowerCase();
    const isPristinePlaceholder = !desc && !details &&
      (!quantityText || Number(quantityText) === 1) &&
      (!rateText || Number(rateText) === 0);
    if (row.dataset.source === 'calculator' || calcDescriptions.has(desc) || isSelectedProductRow || isPristinePlaceholder) {
      row.remove();
    }
  });

  plan.lineItems.forEach(item => addLineItemFn(item));

  if (!tbody.children.length) addLineItemFn({});

  // Sync discount / rush / tax to builder
  const setV = (id, v) => { const e = el(id); if (e) e.value = v; };
  Object.entries(plan.builderFields).forEach(([id, value]) => setV(id, value));

  ['docDiscount', 'docRushPercent', 'docTaxRate'].forEach(id => {
    const node = el(id);
    if (node) node.dispatchEvent(new Event('input', { bubbles: true }));
  });
}
