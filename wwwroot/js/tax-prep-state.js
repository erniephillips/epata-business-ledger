(function (global) {
  function equalsText(value, expected) {
    return String(value || '').trim().toLowerCase() === String(expected || '').trim().toLowerCase();
  }

  function percentLabel(value) {
    return `${Math.min(100, Math.max(0, Number(value ?? 100))).toFixed(0)}%`;
  }

  function taxAssetAmount(asset) {
    const businessUse = Math.min(100, Math.max(0, Number(asset?.businessUsePercent ?? 100))) / 100;
    return Number(asset?.cost ?? 0) * businessUse;
  }

  function isFullyExpensedAsset(asset) {
    if (asset?.countedExpenseThisYear !== true) return false;
    return equalsText(asset?.taxTreatment, 'Section 179') || equalsText(asset?.taxTreatment, 'De Minimis Expense');
  }

  function taxPrepAssetHandlingRows(assets = [], assetExpenses = []) {
    const assetRows = assets.map(asset => {
      const counted = isFullyExpensedAsset(asset);
      const treatment = asset.taxTreatment || 'Review';
      let handling = 'Asset review';
      if (counted) handling = 'Asset deduction';
      else if (equalsText(treatment, 'Depreciation')) handling = 'Asset/depreciation review';
      else if (equalsText(treatment, 'Not Deductible')) handling = 'Excluded asset';

      return {
        name: asset.name || asset.vendorName || `Asset #${asset.id}`,
        taxTreatment: treatment,
        taxPrepHandling: handling,
        cost: asset.cost,
        businessUsePercent: percentLabel(asset.businessUsePercent),
        countedDeduction: counted ? taxAssetAmount(asset) : 0,
        needsReview: asset.needsReview || equalsText(treatment, 'Review') || equalsText(treatment, 'Depreciation')
      };
    });

    const expenseRows = assetExpenses.map(expense => ({
      name: expense.description || expense.vendorName || `Expense #${expense.id}`,
      taxTreatment: 'Expense marked Asset',
      taxPrepHandling: 'Excluded from ordinary expenses',
      cost: expense.total ?? expense.amount,
      businessUsePercent: percentLabel(expense.businessUsePercent),
      countedDeduction: 0,
      needsReview: expense.needsReview || equalsText(expense.deductibleStatus, 'Review')
    }));

    return [...assetRows, ...expenseRows].sort((a, b) => String(a.name).localeCompare(String(b.name)));
  }

  function taxPrepRewardIncomeRows(rewards = []) {
    return rewards
      .map(reward => {
        const countsAsIncome = equalsText(reward.incomeStatus, 'Yes - Count as income');
        return {
          rewardDate: reward.rewardDate,
          rewardType: reward.rewardType,
          incomeStatus: reward.incomeStatus || 'Review',
          giftCardAmount: reward.giftCardAmount,
          pointsChange: reward.pointsChange ?? 0,
          incomeAmount: countsAsIncome ? Number(reward.giftCardAmount || 0) : 0,
          needsReview: reward.needsReview || equalsText(reward.incomeStatus, 'Review')
        };
      })
      .sort((a, b) => String(a.rewardDate || '').localeCompare(String(b.rewardDate || '')));
  }

  global.EpataTaxPrepState = {
    isFullyExpensedAsset,
    percentLabel,
    taxPrepAssetHandlingRows,
    taxPrepRewardIncomeRows
  };
})(globalThis);
