(function (global) {
  const longSummaryLength = 280;
  const longUnbrokenTextLength = 80;

  function text(value) {
    return String(value ?? '').trim();
  }

  function communicationCardPlan(row = {}, now = new Date()) {
    const direction = text(row.direction) || 'Note';
    const customerName = text(row.customerName);
    const channel = text(row.channel);
    const subject = text(row.subject) || `${channel || 'Communication'} with ${customerName || 'customer'}`;
    const summary = String(row.summary ?? '');
    const followUpStatus = text(row.followUpStatus);
    const followUpDate = text(row.followUpDate);
    const followUpOpen = followUpStatus.toLowerCase() === 'open';
    const dueAt = followUpDate ? new Date(`${followUpDate.substring(0, 10)}T23:59:59`) : null;
    const compareAt = now instanceof Date ? now : new Date(now);
    const overdue = Boolean(followUpOpen && dueAt && !Number.isNaN(dueAt.getTime()) && dueAt < compareAt);
    const reference = [row.relatedJobNumber, row.relatedOrderNumber, row.relatedInvoiceNumber]
      .map(text)
      .filter(Boolean)
      .join(' · ');
    const hasLongSummary = summary.length > longSummaryLength || new RegExp(`\\S{${longUnbrokenTextLength},}`).test(summary);

    return {
      direction,
      customerName,
      channel,
      subject,
      summary,
      followUpStatus,
      followUpDate,
      followUpOpen,
      followUpBadge: followUpOpen ? (overdue ? 'Follow-up overdue' : 'Follow-up open') : '',
      overdue,
      reference,
      hasLongSummary
    };
  }

  global.EpataCommunicationState = {
    communicationCardPlan
  };
})(globalThis);
