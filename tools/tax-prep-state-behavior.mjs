import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import vm from 'node:vm';

const sandbox = {};
sandbox.globalThis = sandbox;

const helperSource = await readFile(new URL('../wwwroot/js/tax-prep-state.js', import.meta.url), 'utf8');
vm.runInNewContext(helperSource, sandbox, { filename: 'tax-prep-state.js' });

const {
  isFullyExpensedAsset,
  percentLabel,
  taxPrepAssetHandlingRows,
  taxPrepRewardIncomeRows,
} = sandbox.EpataTaxPrepState;

assert.equal(percentLabel(150), '100%');
assert.equal(percentLabel(-10), '0%');

const assets = [
  {
    id: 1,
    name: 'Section 179 Printer',
    taxTreatment: 'Section 179',
    countedExpenseThisYear: true,
    cost: 1000,
    businessUsePercent: 80,
    needsReview: false,
  },
  {
    id: 2,
    name: 'Depreciation Printer',
    taxTreatment: 'Depreciation',
    countedExpenseThisYear: true,
    cost: 900,
    businessUsePercent: 100,
    needsReview: false,
  },
  {
    id: 3,
    name: 'Personal Tool',
    taxTreatment: 'Not Deductible',
    countedExpenseThisYear: true,
    cost: 250,
    businessUsePercent: 100,
    needsReview: false,
  },
];

const assetExpenses = [
  {
    id: 4,
    description: 'Asset-tagged filament dryer',
    taxBucket: 'Asset',
    total: 75,
    businessUsePercent: 100,
    deductibleStatus: 'Yes',
    needsReview: false,
  },
];

assert.equal(isFullyExpensedAsset(assets[0]), true);
assert.equal(isFullyExpensedAsset(assets[1]), false);
assert.equal(isFullyExpensedAsset(assets[2]), false);

const assetRows = taxPrepAssetHandlingRows(assets, assetExpenses);
const assetByName = Object.fromEntries(assetRows.map(row => [row.name, row]));
assert.equal(assetByName['Section 179 Printer'].taxPrepHandling, 'Asset deduction');
assert.equal(assetByName['Section 179 Printer'].countedDeduction, 800);
assert.equal(assetByName['Section 179 Printer'].businessUsePercent, '80%');
assert.equal(assetByName['Depreciation Printer'].taxPrepHandling, 'Asset/depreciation review');
assert.equal(assetByName['Depreciation Printer'].countedDeduction, 0);
assert.equal(assetByName['Depreciation Printer'].needsReview, true);
assert.equal(assetByName['Personal Tool'].taxPrepHandling, 'Excluded asset');
assert.equal(assetByName['Personal Tool'].countedDeduction, 0);
assert.equal(assetByName['Asset-tagged filament dryer'].taxPrepHandling, 'Excluded from ordinary expenses');
assert.equal(assetByName['Asset-tagged filament dryer'].countedDeduction, 0);

const rewards = [
  {
    rewardDate: '2026-06-01',
    rewardType: 'Gift Card',
    incomeStatus: 'Yes - Count as income',
    giftCardAmount: 40,
    pointsChange: 500,
    needsReview: false,
  },
  {
    rewardDate: '2026-06-02',
    rewardType: 'Points',
    incomeStatus: 'Yes - Count as income',
    giftCardAmount: 0,
    pointsChange: 1200,
    needsReview: false,
  },
  {
    rewardDate: '2026-06-03',
    rewardType: 'Gift Card',
    incomeStatus: 'Review',
    giftCardAmount: 25,
    pointsChange: 300,
    needsReview: false,
  },
];

const rewardRows = taxPrepRewardIncomeRows(rewards);
assert.equal(rewardRows[0].incomeStatus, 'Yes - Count as income');
assert.equal(rewardRows[0].giftCardAmount, 40);
assert.equal(rewardRows[0].pointsChange, 500);
assert.equal(rewardRows[0].incomeAmount, 40, 'Gift card income should count once.');
assert.equal(rewardRows[1].pointsChange, 1200);
assert.equal(rewardRows[1].incomeAmount, 0, 'Points-only rows should not be double-counted as dollar income.');
assert.equal(rewardRows[2].incomeStatus, 'Review');
assert.equal(rewardRows[2].incomeAmount, 0);
assert.equal(rewardRows[2].needsReview, true);

const appSource = await readFile(new URL('../wwwroot/js/app.js', import.meta.url), 'utf8');
const indexSource = await readFile(new URL('../wwwroot/index.html', import.meta.url), 'utf8');

assert.ok(indexSource.includes('/js/tax-prep-state.js?v=1'), 'Main shell should load the Tax Prep state helper before app.js.');
assert.ok(appSource.includes('EpataTaxPrepState?.taxPrepAssetHandlingRows'), 'Tax Prep page should use shared asset handling rows.');
assert.ok(appSource.includes('EpataTaxPrepState?.taxPrepRewardIncomeRows'), 'Tax Prep page should use shared reward income rows.');
assert.ok(appSource.includes('Asset Tax Handling'), 'Tax Prep page should show asset-specific handling.');
assert.ok(appSource.includes('MakerWorld Reward Income Status'), 'Tax Prep page should show reward income status.');
assert.ok(appSource.includes('Points are shown for review and are not added again as dollars.'), 'Tax Prep page should explain points are not dollar income.');

console.log(JSON.stringify({
  TaxPrepStateBehavior: 'pass',
  AssetTaxPrepRound51: 'pass',
  RewardIncomeRound51: 'pass',
  RewardNoDoubleCountRound51: 'pass',
  AssetRows: assetRows.length,
  RewardRows: rewardRows.length,
}));
