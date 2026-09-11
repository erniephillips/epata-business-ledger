(function (global) {
  const cards = [
    { type: 'modal', title: 'Estimate Sent (External)', description: 'You sent a quote made outside this app and are waiting for approval. The built-in Estimates builder tracks these automatically.', config: 'customerJobs', kind: 'estimateSent' },
    { type: 'page', title: 'New Estimate PDF', description: 'Build a real customer estimate with calculator, line items, and PDF preview.', page: 'estimates' },
    { type: 'page', title: 'New Invoice PDF', description: 'Build a real customer invoice with line items, AR sync, and PDF preview.', page: 'invoices' },
    { type: 'modal', title: 'Etsy Sale', description: 'A paid Etsy order or marketplace sale.', config: 'sales', kind: 'etsy' },
    { type: 'page', title: 'AI Upload Paid Marketplace Order', description: 'Upload an Etsy/marketplace order PDF or screenshot, review the extracted Sale, then save it with proof.', page: 'aiOperations' },
    { type: 'modal', title: 'Direct Paid Sale', description: 'Customer already paid you directly.', config: 'sales', kind: 'directPaid' },
    { type: 'modal', title: 'Open Invoice / AR', description: 'You sent a direct invoice and are waiting for payment.', config: 'receivables', kind: 'invoice' },
    { type: 'modal', title: 'Bill / AP', description: 'You owe a vendor and have not paid yet.', config: 'bills', kind: 'bill' },
    { type: 'modal', title: 'Paid Expense', description: 'You already bought supplies, labels, software, etc.', config: 'expenses', kind: 'expense' },
    { type: 'modal', title: 'Damaged / Lost Order', description: 'Record a refund, replacement cost, reship postage, or carrier recovery from any sales channel.', config: 'orderLosses', kind: 'orderLoss' },
    { type: 'modal', title: 'Equipment / Asset Purchase', description: 'A printer, AMS, durable tool, computer, or higher-value item that needs tax-treatment review.', config: 'assets', kind: 'assetPurchase' },
    { type: 'modal', title: 'Customer / Vendor Contact', description: 'Add a customer, vendor, or both so future ledgers can link back to the right person.', config: 'parties', kind: 'partyContact' },
    { type: 'modal', title: 'Product / Costing Row', description: 'Start a reusable product or costing record for pricing, material, and repeat work.', config: 'products', kind: 'productCosting' },
    { type: 'modal', title: 'Action Item', description: 'Create a follow-up task for cleanup, collection, tax review, production, or proof work.', config: 'actions', kind: 'actionItem' },
    { type: 'modal', title: 'Audit Doc / Proof Index', description: 'Manually index a proof file, receipt, PDF, screenshot, or local path.', config: 'auditDocs', kind: 'auditDoc' },
    { type: 'modal', title: 'Log Customer Communication', description: 'Record an email, text, call, approval, question, or follow-up.', config: 'communications', kind: 'communication' },
    { type: 'page', title: 'Open Printer Queue', description: 'Schedule active jobs, assign printers, and track production progress.', page: 'printerQueue' },
  ];

  function quickAddCards() {
    return cards.map(card => ({ ...card }));
  }

  function isoDate(now = new Date()) {
    return new Date(now).toISOString().substring(0, 10);
  }

  function isoMinute(now = new Date()) {
    return new Date(now).toISOString().substring(0, 16);
  }

  function quickAddPreset(kind = '', now = new Date()) {
    const today = isoDate(now);
    const presets = {
      estimateSent: { platform: 'Direct', status: 'Quoted', jobDate: today, jobType: 'Estimate', paymentMethod: 'Unknown / Review', needsReview: false, notes: 'Estimate entered manually. Do not count as income or AR until approved/invoiced.' },
      etsy: { platform: 'Etsy', paymentMethod: 'Etsy Payments', status: 'Paid', saleDate: today, quantity: 1, includeInDashboard: true, needsReview: true, notes: 'Remember to enter Etsy fees, actual label cost, packaging, and COGS.' },
      directPaid: { platform: 'Direct', paymentMethod: 'Unknown / Review', status: 'Paid', saleDate: today, quantity: 1, includeInDashboard: true },
      invoice: { status: 'Sent', invoiceDate: today, paymentMethod: 'Unknown / Review', includeInCashReports: false, needsReview: false },
      bill: { status: 'Unpaid', billDate: today, paymentMethod: 'Unknown / Review', taxDeductible: true },
      expense: { expenseDate: today, paymentMethod: 'Unknown / Review', taxDeductible: true, taxBucket: 'Review', deductibleStatus: 'Review', countedExpense: true, businessUsePercent: 100, needsReview: true, notes: 'Classify as COGS/Materials, Operating Expense, Asset, or Memo Only before filing.' },
      orderLoss: { incidentDate: today, platform: 'Other', incidentType: 'Damaged in transit', resolution: 'Replacement / reship', status: 'Resolved', countInTaxReports: true, needsReview: true, notes: 'Enter only additional incident amounts here. Do not duplicate the same refund or cost on a Sale or Expense.' },
      assetPurchase: { purchaseDate: today, inServiceDate: today, category: 'Equipment', paymentMethod: 'Unknown / Review', businessUsePercent: 100, taxTreatment: 'Review', countedExpenseThisYear: false, needsReview: true, notes: 'Review with tax preparer before choosing Section 179, De Minimis Expense, or Depreciation.' },
      partyContact: { partyType: 'Customer', defaultPlatform: 'Direct' },
      productCosting: { category: '3D Printed Product', material: 'PLA', needsReview: true },
      actionItem: { area: 'General', priority: 'Normal', status: 'Open' },
      auditDoc: { documentDate: today, documentType: 'Other', needsReview: true },
      communication: { occurredAt: isoMinute(now), direction: 'Outgoing', channel: 'Email', followUpStatus: 'None', needsReview: false },
      queue: { queueDate: today, priority: 'Normal', status: 'Queued', quantity: 1, plateCount: 1, progressPercent: 0, failureCount: 0, needsReview: false },
    };
    return { ...(presets[kind] || {}) };
  }

  function quickAddTarget(card = {}) {
    if (card.type === 'page') return { action: 'page', page: card.page || 'quickAdd' };
    return { action: 'modal', config: card.config || '', kind: card.kind || '' };
  }

  function applyQuickAddOverrides(preset = {}, overrides = {}) {
    return { ...preset, ...overrides };
  }

  global.EpataQuickAdd = {
    applyQuickAddOverrides,
    quickAddCards,
    quickAddPreset,
    quickAddTarget,
  };
})(globalThis);
