(function (global) {
  function openProductDraftPlan(result) {
    const product = result && result.product ? { ...result.product } : {};
    return {
      configKey: 'products',
      row: {
        ...product,
        id: 0,
        isArchived: false,
        needsReview: product.needsReview !== false
      },
      isNew: true,
      shouldSaveAutomatically: false
    };
  }

  global.EpataAiProductDraft = {
    openProductDraftPlan
  };
})(globalThis);
