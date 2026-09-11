(function (root) {
  function buildCustomerRows(parties, sales, invoices, docs, jobs, communications) {
    const map = new Map();
    const add = (name, source, row, amount, open) => {
      const clean = String(name || '').trim();
      if (!clean) return;
      const key = clean.toLowerCase();
      const item = map.get(key) || {
        name: clean,
        sources: new Set(),
        linkedRows: 0,
        salesTotal: 0,
        invoiceTotal: 0,
        openAr: 0,
        lastActivity: '',
        partyCount: 0,
        partyRecords: []
      };
      item.sources.add(source);
      item.linkedRows += 1;
      if (source === 'People') {
        item.partyCount += 1;
        item.partyRecords.push({ id: Number(row.id || 0), partyType: row.partyType || '', isArchived: row.isArchived === true });
      }
      if (source === 'Sales') item.salesTotal += Number(amount || 0);
      if (source === 'AR' || source === 'PDF Docs') item.invoiceTotal += Number(amount || 0);
      item.openAr += Number(open || 0);
      item.lastActivity = latestDate(item.lastActivity, row.updatedAtUtc || row.updatedAt || row.invoiceDate || row.saleDate || row.jobDate || row.documentDate || row.occurredAt || row.date);
      map.set(key, item);
    };

    (parties || [])
      .filter(p => ['customer', 'both'].includes(String(p.partyType || '').toLowerCase()))
      .forEach(p => add(p.name, 'People', p));
    (sales || []).forEach(r => add(r.customerName, 'Sales', r, r.customerPaid));
    (invoices || []).forEach(r => add(r.customerName, 'AR', r, r.invoiceTotal, ['Paid', 'Void'].includes(r.status) ? 0 : r.balanceDue));
    (docs || []).forEach(r => add(r.customerName, 'PDF Docs', r, r.total));
    (jobs || []).forEach(r => add(r.customerName, 'Jobs', r, r.invoiceAmount));
    (communications || []).forEach(r => add(r.customerName, 'Communications', r));

    return finalizeRelationshipRows(map);
  }

  function buildVendorRows(parties, expenses, bills, assets) {
    const map = new Map();
    const add = (name, source, row, amount, open, asset) => {
      const clean = String(name || '').trim();
      if (!clean) return;
      const key = clean.toLowerCase();
      const item = map.get(key) || {
        name: clean,
        sources: new Set(),
        linkedRows: 0,
        expenseTotal: 0,
        openAp: 0,
        assetTotal: 0,
        lastActivity: '',
        partyCount: 0,
        partyRecords: []
      };
      item.sources.add(source);
      item.linkedRows += 1;
      if (source === 'People') {
        item.partyCount += 1;
        item.partyRecords.push({ id: Number(row.id || 0), partyType: row.partyType || '', isArchived: row.isArchived === true });
      }
      item.expenseTotal += Number(amount || 0);
      item.openAp += Number(open || 0);
      item.assetTotal += Number(asset || 0);
      item.lastActivity = latestDate(item.lastActivity, row.updatedAtUtc || row.updatedAt || row.expenseDate || row.dueDate || row.purchaseDate || row.date);
      map.set(key, item);
    };

    (parties || [])
      .filter(p => ['vendor', 'both'].includes(String(p.partyType || '').toLowerCase()))
      .forEach(p => add(p.name, 'People', p));
    (expenses || []).forEach(r => add(r.vendorName, 'Expenses', r, r.total));
    (bills || []).forEach(r => add(r.vendorName, 'AP', r, 0, ['Paid', 'Void'].includes(r.status) ? 0 : r.balanceDue));
    (assets || []).forEach(r => add(r.vendorName, 'Assets', r, 0, 0, r.cost));

    return finalizeRelationshipRows(map);
  }

  function finalizeRelationshipRows(map) {
    return Array.from(map.values()).map(row => {
      const duplicateNameWarning = row.partyCount > 1
        ? `${row.partyCount} saved contact cards share this name. Activity is grouped by trimmed, case-insensitive name.`
        : '';
      return {
        ...row,
        sources: Array.from(row.sources).join(', '),
        activePartyCount: row.partyRecords.filter(party => !party.isArchived).length,
        archivedPartyCount: row.partyRecords.filter(party => party.isArchived).length,
        nameStatus: duplicateNameWarning ? 'Duplicate contacts' : 'OK',
        duplicateNameWarning
      };
    });
  }

  function latestDate(a, b) {
    if (!b) return a || '';
    if (!a) return b;
    return new Date(b).getTime() > new Date(a).getTime() ? b : a;
  }

  function sameName(a, b) {
    return String(a || '').trim().toLowerCase() === String(b || '').trim().toLowerCase();
  }

  function rowTextIncludes(row, text) {
    const needle = String(text || '').trim().toLowerCase();
    if (!needle) return false;
    return JSON.stringify(row || {}).toLowerCase().includes(needle);
  }

  function customerDetailSectionPlan(name, data = {}) {
    return [
      { title: 'Sales', configKey: 'sales', rows: (data.sales || []).filter(row => sameName(row.customerName, name)) },
      { title: 'AR Invoices', configKey: 'receivables', rows: (data.invoices || []).filter(row => sameName(row.customerName, name)) },
      { title: 'Estimate / Invoice PDFs', configKey: '', rows: (data.docs || []).filter(row => sameName(row.customerName, name)) },
      { title: 'Jobs', configKey: 'customerJobs', rows: (data.jobs || []).filter(row => sameName(row.customerName, name)) },
      { title: 'Communications', configKey: 'communications', rows: (data.communications || []).filter(row => sameName(row.customerName, name)) },
      { title: 'Proof / Audit Docs', configKey: 'auditDocs', rows: (data.auditDocs || []).filter(row => rowTextIncludes(row, name)) }
    ];
  }

  function vendorDetailSectionPlan(name, data = {}) {
    return [
      { title: 'Expenses', configKey: 'expenses', rows: (data.expenses || []).filter(row => sameName(row.vendorName, name)) },
      { title: 'Bills / AP', configKey: 'bills', rows: (data.bills || []).filter(row => sameName(row.vendorName, name)) },
      { title: 'Assets', configKey: 'assets', rows: (data.assets || []).filter(row => sameName(row.vendorName, name)) },
      { title: 'Proof / Audit Docs', configKey: 'auditDocs', rows: (data.auditDocs || []).filter(row => rowTextIncludes(row, name)) }
    ];
  }

  function personContactTarget(name, isCustomer, id = 0) {
    const rowId = Number(id || 0);
    if (rowId > 0) {
      return { kind: 'edit', configKey: 'parties', rowId };
    }
    return {
      kind: 'new',
      configKey: 'parties',
      row: {
        name,
        partyType: isCustomer ? 'Customer' : 'Vendor',
        defaultPlatform: isCustomer ? 'Direct' : 'Vendor'
      },
      isNew: true
    };
  }

  function relationshipSectionPage(rows = [], state = {}) {
    const safeRows = Array.isArray(rows) ? rows : [];
    const pageSize = Math.max(1, Number(state.pageSize || 10));
    const totalPages = Math.max(1, Math.ceil(safeRows.length / pageSize));
    const rawPage = Number.isFinite(Number(state.page)) ? Number(state.page) : 0;
    const page = Math.min(Math.max(0, rawPage), totalPages - 1);
    const startIndex = page * pageSize;
    const endIndex = Math.min(safeRows.length, startIndex + pageSize);
    return {
      page,
      pageSize,
      totalPages,
      startRow: safeRows.length ? startIndex + 1 : 0,
      endRow: endIndex,
      pageRows: safeRows.slice(startIndex, endIndex)
    };
  }

  function relationshipLinkedRowTarget(configKey, row = {}) {
    const cleanConfigKey = String(configKey || '').trim();
    const rowId = Number(row?.id || 0);
    if (cleanConfigKey) {
      return { kind: 'modal', configKey: cleanConfigKey, rowId, label: 'Open' };
    }
    if (row?.sourceKind === 'receivable') {
      return { kind: 'ledger', configKey: 'receivables', rowId: Number(row.sourceId || row.id || 0), label: 'Open AR' };
    }
    if (row?.docNumber) {
      return { kind: 'invoice-record', docNumber: String(row.docNumber), includeArchived: row.isArchived === true, label: 'Open Record' };
    }
    if (rowId > 0) {
      return { kind: 'page', page: 'invoiceRecords', rowId, label: 'Open Records' };
    }
    return { kind: 'none', rowId: 0, label: '' };
  }

  root.EpataRelationshipDirectory = {
    buildCustomerRows,
    buildVendorRows,
    customerDetailSectionPlan,
    latestDate,
    personContactTarget,
    relationshipSectionPage,
    vendorDetailSectionPlan,
    relationshipLinkedRowTarget
  };
})(typeof globalThis !== 'undefined' ? globalThis : window);
