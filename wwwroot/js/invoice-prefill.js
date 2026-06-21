(function(root) {
  const UNKNOWN_PAYMENT_METHOD = 'Unknown / Review';

  function text(value) {
    return String(value ?? '').trim();
  }

  function money(value) {
    const n = Number(value);
    return Number.isFinite(n) ? Math.max(0, n) : 0;
  }

  function roundMoney(value) {
    return Math.round(money(value) * 100) / 100;
  }

  function roundPercent(value) {
    const n = Number(value);
    return Number.isFinite(n) ? Math.max(0, Math.round(n * 1000) / 1000) : 0;
  }

  function dateOnly(value) {
    return text(value).slice(0, 10);
  }

  function statusFromReceivable(status) {
    const s = text(status).toLowerCase();
    if (s === 'paid') return 'Paid';
    if (s === 'partial' || s === 'partially paid') return 'Partial';
    if (s === 'void' || s === 'cancelled' || s === 'canceled') return 'Void';
    if (s === 'sent' || s === 'open' || s === 'unpaid' || s === 'overdue') return 'Sent';
    return 'Draft';
  }

  function taxRateFromReceivable(row, taxableBase) {
    const explicitRate = Number(row?.taxRatePercent);
    if (Number.isFinite(explicitRate) && explicitRate >= 0) return roundPercent(explicitRate);

    const tax = money(row?.salesTax);
    return taxableBase > 0 ? roundPercent((tax / taxableBase) * 100) : 0;
  }

  function invoicePrefillFromReceivable(row = {}) {
    const subtotal = roundMoney(row.subtotal);
    const discount = roundMoney(row.discount);
    const rushFee = roundMoney(row.rushFee);
    const taxableBase = Math.max(0, subtotal + rushFee - discount);
    const salesTax = roundMoney(row.salesTax);
    const invoiceTotal = money(row.invoiceTotal) || roundMoney(taxableBase + salesTax);
    const amountPaid = Math.min(invoiceTotal, roundMoney(row.amountPaid));
    const invoiceNumber = text(row.invoiceNumber);
    const sourceProof = text(row.sourceProof || row.proofReference || row.receiptProof);
    const projectName = text(row.projectName) || invoiceNumber || 'Customer invoice';
    const detailParts = [
      invoiceNumber ? `AR ledger invoice ${invoiceNumber}` : '',
      sourceProof ? `Source proof: ${sourceProof}` : '',
    ].filter(Boolean);

    return {
      docType: 'INVOICE',
      docStatus: statusFromReceivable(row.status),
      docDate: dateOnly(row.invoiceDate) || dateOnly(row.createdAt),
      dueDate: dateOnly(row.dueDate),
      preparedFor: text(row.customerName),
      customerName: text(row.customerName),
      projectName,
      projectDescription: '',
      docDiscount: discount,
      docRushPercent: subtotal > 0 ? roundPercent((rushFee / subtotal) * 100) : 0,
      docTaxRate: taxRateFromReceivable(row, taxableBase),
      amountPaid,
      paymentMethod: text(row.paymentMethod) || UNKNOWN_PAYMENT_METHOD,
      termsNotes: 'Payment due by the due date shown above.',
      assistanceSource: 'AR Ledger prefill',
      sourceReceivableId: row.id ?? null,
      sourceReceivableInvoiceNumber: invoiceNumber,
      lineItems: [{
        sortOrder: 1,
        description: projectName,
        details: detailParts.join(' | '),
        quantity: 1,
        rate: subtotal,
        amount: subtotal,
      }],
    };
  }

  root.EpataInvoicePrefill = {
    invoicePrefillFromReceivable,
    statusFromReceivable,
  };
})(typeof window !== 'undefined' ? window : globalThis);
