// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Pricing Calculator
// ═══════════════════════════════════════════════════════

import { el, val, money, plainMoney } from './utils.js';

const INPUT_IDS = [
  'grams','hours','designHours','setupFee','postFee',
  'gramRate','hourRate','designRate','minimum',
  'difficulty','rush','discount','taxRate',
];

export function initCalculator() {
  const run = () => calculate();
  INPUT_IDS.forEach(id => {
    const node = el(id);
    if (!node) return;
    node.addEventListener('input', run);
    node.addEventListener('change', run);
  });

  // Wire difficulty preset cards (ABS, Nylon, Exotic, PLA)
  document.querySelectorAll('#difficultyGrid .diff-card').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('#difficultyGrid .diff-card').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      const hidden = el('difficulty');
      if (hidden) { hidden.value = btn.dataset.val; hidden.dispatchEvent(new Event('change')); }
    });
  });

  syncDifficultyButtons();
  calculate();
}

export function calculate() {
  const setup    = val('setupFee');
  const post     = val('postFee');
  const material = val('grams') * val('gramRate');
  const machine  = val('hours') * val('hourRate');
  const design   = val('designHours') * val('designRate');

  const baseSubtotal    = setup + post + material + machine + design;
  const difficultyFactor = val('difficulty') || 1;
  const rushPercent      = val('rush');
  const difficultyFee   = baseSubtotal * Math.max(0, difficultyFactor - 1);
  const afterDifficulty = baseSubtotal + difficultyFee;
  const rushFee         = afterDifficulty * Math.max(0, rushPercent / 100);
  const adjustedSubtotal = afterDifficulty + rushFee;
  const discount         = val('discount');
  const minimum          = val('minimum');
  const taxableAmount    = Math.max(minimum, adjustedSubtotal - discount);
  const tax              = taxableAmount * (val('taxRate') / 100);
  const total            = taxableAmount + tax;

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
  updateFormulaText({
    setup, post, material, machine, design,
    baseSubtotal, difficultyFactor, difficultyFee,
    rushPercent, rushFee, adjustedSubtotal,
    discount, minimum, taxableAmount, tax, total,
  });

  return {
    setup, post, material, machine, design,
    baseSubtotal, difficultyFactor, difficultyFee,
    rushPercent, rushFactor: 1 + rushPercent / 100, rushFee,
    adjustedSubtotal, discount, minimum, taxableAmount, tax, total,
  };
}

function setText(id, text) {
  const e = el(id);
  if (e) e.textContent = text;
}

function updateFormulaText(calc) {
  setText('materialFormula', `(${fmt(val('grams'))}g x $${fmt(val('gramRate'), 3)})`);
  setText('machineFormula', `(${fmt(val('hours'))}h x $${fmt(val('hourRate'))})`);
  setText('designFormula', `(${fmt(val('designHours'))}h x $${fmt(val('designRate'))})`);
  setText('difficultyFormula', calc.difficultyFactor > 1
    ? `(${money(calc.baseSubtotal)} x ${fmt(calc.difficultyFactor - 1, 2)})`
    : '(none)');
  setText('rushFormula', calc.rushPercent > 0
    ? `(${money(calc.baseSubtotal + calc.difficultyFee)} x ${fmt(calc.rushPercent)}%)`
    : '(none)');
  setText('taxFormula', val('taxRate') > 0
    ? `(${money(calc.taxableAmount)} x ${fmt(val('taxRate'))}%)`
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

export function getCalcState() {
  return Object.fromEntries(INPUT_IDS.map(id => [id, el(id)?.value ?? '']));
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

export function pushToBuilder(calc, lineItemsFn, addLineItemFn, context = {}) {
  const tbody = el('lineItemsBody');
  if (!tbody) return;

  // Round computed money values to cents to avoid JS float artifacts
  // like 30.499999999999993 showing up in rate fields.
  const r2 = n => Math.round((Number(n) || 0) * 100) / 100;
  const calcDescriptions = new Set([
    'Print setup / file preparation',
    'Material usage',
    'Machine print time',
    'Design / modeling time',
    'Post-processing / handling',
    'Material / difficulty surcharge',
    'Minimum charge adjustment',
  ]);
  const productName = String(context.productName || '').trim();
  const productDetails = String(context.productDetails || '').trim();

  Array.from(tbody.querySelectorAll('tr')).forEach(row => {
    const desc = row.querySelector('.item-desc')?.value?.trim() || '';
    const isSelectedProductRow = productName && desc.toLowerCase() === productName.toLowerCase();
    if (row.dataset.source === 'calculator' || calcDescriptions.has(desc) || isSelectedProductRow) {
      row.remove();
    }
  });

  const addIf = (cond, desc, details, qty, rate) => {
    if (cond) addLineItemFn({ desc, details, qty, rate, source: 'calculator' });
  };

  addLineItemFn({
    desc: productName || 'Print setup / file preparation',
    details: productName
      ? [productDetails, 'Calculated quote: slicing, orientation, supports, settings review, and project setup.'].filter(Boolean).join('\n')
      : 'Slicing, orientation, supports, settings review, and project setup.',
    qty: 1,
    rate: r2(calc.setup),
    source: 'calculator',
  });

  addIf(val('grams') > 0, 'Material usage',
    `${val('grams')} grams × ${plainMoney(val('gramRate'))}/g`,
    val('grams'), val('gramRate'));

  addIf(val('hours') > 0, 'Machine print time',
    `${val('hours')} print hours × ${plainMoney(val('hourRate'))}/hr`,
    val('hours'), val('hourRate'));

  addIf(val('designHours') > 0, 'Design / modeling time',
    `${val('designHours')} design hours × ${plainMoney(val('designRate'))}/hr`,
    val('designHours'), val('designRate'));

  addIf(calc.post > 0, 'Post-processing / handling',
    'Cleanup, support removal, packaging, or special handling.',
    1, r2(calc.post));

  addIf(calc.difficultyFee > 0, 'Material / difficulty surcharge',
    `ABS/ASA or higher-risk print. Multiplier: ${calc.difficultyFactor.toFixed(2)}×`,
    1, r2(calc.difficultyFee));

  const preMinimumSubtotal = calc.baseSubtotal + calc.difficultyFee;
  const rushMultiplier = 1 + Math.max(0, val('rush') / 100);
  const minimumAdjustment = Math.max(0, ((calc.minimum + calc.discount) / rushMultiplier) - preMinimumSubtotal);
  addIf(minimumAdjustment > 0, 'Minimum charge adjustment',
    `Minimum quote floor: ${money(calc.minimum)}.`,
    1, r2(minimumAdjustment));

  if (!tbody.children.length) addLineItemFn({});

  // Sync discount / rush / tax to builder
  const setV = (id, v) => { const e = el(id); if (e) e.value = v; };
  setV('docDiscount',    calc.discount.toFixed(2));
  setV('docRushPercent', String(Math.round(Math.max(0, (calc.rushFactor - 1) * 100))));
  setV('docTaxRate',     val('taxRate').toFixed(3).replace(/\.?0+$/, ''));

  ['docDiscount', 'docRushPercent', 'docTaxRate'].forEach(id => {
    const node = el(id);
    if (node) node.dispatchEvent(new Event('input', { bubbles: true }));
  });
}
