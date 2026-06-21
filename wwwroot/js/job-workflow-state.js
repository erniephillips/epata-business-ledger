(function (global) {
  const routeToConfig = {
    sales: 'sales',
    receivables: 'receivables',
    customerJobs: 'customerJobs',
    communications: 'communications',
    printerQueue: 'printerQueue',
    expenses: 'expenses',
    auditDocs: 'auditDocs',
    actions: 'actions'
  };

  function timelineEventTarget(event = {}) {
    const routePage = String(event.routePage || '').trim();
    const rowId = Number(event.recordId || event.rowId || event.id || 0);
    const configKey = routeToConfig[routePage];
    if (configKey && rowId > 0) {
      return { kind: 'modal', page: routePage, configKey, rowId };
    }
    if (routePage) {
      return { kind: 'page', page: routePage, configKey: '', rowId: 0 };
    }
    return { kind: 'none', page: '', configKey: '', rowId: 0 };
  }

  function printerQueuePrefillFromJob(job = {}) {
    return {
      customerJobId: job.id,
      customerName: job.customerName,
      jobName: job.jobName,
      relatedOrderNumber: job.relatedOrderNumber,
      relatedInvoiceNumber: job.relatedInvoiceNumber,
      productName: job.productName,
      material: job.material,
      color: job.color,
      notes: job.description
    };
  }

  global.EpataJobWorkflowState = {
    printerQueuePrefillFromJob,
    timelineEventTarget
  };
})(globalThis);
