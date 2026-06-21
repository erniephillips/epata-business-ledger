import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const sandbox = {};
sandbox.globalThis = sandbox;

const helperSource = await readFile(new URL('../wwwroot/js/ai-product-draft.js', import.meta.url), 'utf8');
vm.runInNewContext(helperSource, sandbox, { filename: 'ai-product-draft.js' });

const { openProductDraftPlan } = sandbox.EpataAiProductDraft;

const draft = {
  product: {
    id: 927,
    name: 'Round49 Imported Bracket',
    sku: 'R49-BRACKET',
    category: '3D Printed Product',
    material: 'PETG',
    color: 'Black',
    grams: 184.6,
    printHours: 8.7,
    targetPrice: 34.99,
    needsReview: true,
    isArchived: true
  }
};

const plan = openProductDraftPlan(draft);
assert.equal(plan.configKey, 'products');
assert.equal(plan.isNew, true);
assert.equal(plan.shouldSaveAutomatically, false);
assert.equal(plan.row.id, 0);
assert.equal(plan.row.isArchived, false);
assert.equal(plan.row.name, 'Round49 Imported Bracket');
assert.equal(plan.row.material, 'PETG');
assert.equal(plan.row.needsReview, true);

plan.row.name = 'Mutated locally';
assert.equal(draft.product.name, 'Round49 Imported Bracket', 'Product draft plan should not mutate the AI result.');

const defaultPlan = openProductDraftPlan({ product: { name: 'Needs Review Default' } });
assert.equal(defaultPlan.row.needsReview, true);
assert.equal(defaultPlan.shouldSaveAutomatically, false);

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');

const helperScriptIndex = indexSource.indexOf('/js/ai-product-draft.js?v=1');
const appScriptIndex = indexSource.indexOf('/js/app.js');
assert.ok(helperScriptIndex > -1, 'Main shell should load the AI Product draft helper.');
assert.ok(appScriptIndex > -1 && helperScriptIndex < appScriptIndex, 'AI Product draft helper should load before app.js.');
assert.ok(appSource.includes('EpataAiProductDraft?.openProductDraftPlan(appState.aiProductImportDraft)'), 'Product import draft button should use the shared unsaved-draft plan.');
assert.ok(appSource.includes('openModal(configs[plan.configKey], plan.row, plan.isNew)'), 'Product import draft should open the Product modal from the plan.');

console.log(JSON.stringify({
  AiProductDraftBehavior: 'pass',
  ProductImportDraftRound49: 'pass',
  IsNew: plan.isNew,
  ShouldSaveAutomatically: plan.shouldSaveAutomatically
}));
