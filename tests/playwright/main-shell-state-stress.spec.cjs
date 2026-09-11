const { expect, test } = require('@playwright/test');

function collectBrowserFailures(page) {
  const failures = [];
  page.on('console', message => {
    if (message.type() === 'error') failures.push(`console error: ${message.text()}`);
  });
  page.on('pageerror', error => failures.push(`page error: ${error.message}`));
  page.on('response', response => {
    if (response.url().includes('/api/') && response.status() >= 500) {
      failures.push(`failed API response ${response.status()}: ${response.url()}`);
    }
  });
  return failures;
}

function jsonResponse(body, status = 200) {
  return { status, contentType: 'application/json', body: JSON.stringify(body) };
}

async function openQuickModal(page, configKey, preset, overrides = {}) {
  await page.evaluate(([key, kind, values]) => window.quickOpen(key, kind, values), [configKey, preset, overrides]);
  await expect(page.locator('#modal')).toBeVisible();
}

function recordRow(page, docNumber) {
  return page.locator('#recordsBody tr')
    .filter({ has: page.locator('td.doc-number', { hasText: docNumber }) })
    .first();
}

test.describe('main shell async state stress', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'Race and history behavior is covered once on desktop.');
  });

  test('a slow previous route cannot replace or bind over the newer route', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let releaseSales;
    let salesStartedResolve;
    const salesStarted = new Promise(resolve => { salesStartedResolve = resolve; });
    const salesRelease = new Promise(resolve => { releaseSales = resolve; });

    await page.goto('/');
    await expect(page.locator('#app')).toContainText('Dashboard');
    await page.route('**/api/sales*', async route => {
      if (new URL(route.request().url()).pathname !== '/api/sales') return route.continue();
      salesStartedResolve();
      await salesRelease;
      await route.fulfill(jsonResponse([{
        id: 71001,
        saleDate: '2026-09-10',
        customerName: 'STALE SALE MUST NOT RENDER',
        platform: 'Etsy',
        paymentMethod: 'Etsy Payments',
        customerPaid: 45,
        status: 'Paid',
      }]));
    });
    await page.route('**/api/expenses*', async route => {
      if (new URL(route.request().url()).pathname !== '/api/expenses') return route.continue();
      await route.fulfill(jsonResponse([{
        id: 72001,
        expenseDate: '2026-09-10',
        vendorName: 'CURRENT EXPENSE REMAINS VISIBLE',
        description: 'Route race fixture',
        paymentMethod: 'Credit Card',
        total: 12,
      }]));
    });

    await page.locator('button.nav-button[data-page="sales"]').click();
    await salesStarted;
    await page.locator('button.nav-button[data-page="expenses"]').click();
    await expect(page.locator('button.nav-button[data-page="expenses"]')).toHaveClass(/active/);
    await expect(page.locator('#app')).toContainText('CURRENT EXPENSE REMAINS VISIBLE');

    releaseSales();
    await page.waitForTimeout(150);

    await expect(page.locator('button.nav-button[data-page="expenses"]')).toHaveClass(/active/);
    await expect(page.locator('#app')).toContainText('CURRENT EXPENSE REMAINS VISIBLE');
    await expect(page.locator('#app')).not.toContainText('STALE SALE MUST NOT RENDER');
    expect(failures).toEqual([]);
  });

  test('an old modal save cannot close or rebind a newer modal', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let releaseSale;
    let saleStartedResolve;
    const saleStarted = new Promise(resolve => { saleStartedResolve = resolve; });
    const saleRelease = new Promise(resolve => { releaseSale = resolve; });

    await page.goto('/');
    await page.route('**/api/sales*', async route => {
      if (route.request().method() !== 'POST' || new URL(route.request().url()).pathname !== '/api/sales') return route.continue();
      saleStartedResolve();
      await saleRelease;
      await route.fulfill(jsonResponse({ id: 73001 }, 201));
    });

    await openQuickModal(page, 'sales', 'directPaid', { customerName: 'Slow modal A', customerPaid: 19 });
    await page.locator('#modalSave').click();
    await saleStarted;
    await page.locator('#modalClose').click();
    await expect(page.locator('#modal')).toBeHidden();

    await openQuickModal(page, 'expenses', 'expense', { vendorName: 'New modal B' });
    await page.locator('#field_vendorName').fill('New modal B remains open');
    releaseSale();
    await expect(page.locator('#toast-container')).toContainText('Saved.');

    await expect(page.locator('#modal')).toBeVisible();
    await expect(page.locator('#modalTitle')).toContainText('Expenses');
    await expect(page.locator('#field_vendorName')).toHaveValue('New modal B remains open');
    expect(failures).toEqual([]);
  });

  test('proof attachment blocks save, replaces old proof cleanly, and saves the final path', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let releaseFirstUpload;
    let firstUploadStartedResolve;
    const firstUploadStarted = new Promise(resolve => { firstUploadStartedResolve = resolve; });
    const firstUploadRelease = new Promise(resolve => { releaseFirstUpload = resolve; });
    let uploadCount = 0;
    let salePostCount = 0;
    const archivedProofIds = [];
    let savedPayload = null;

    await page.goto('/');
    await page.route('**/api/documents/upload', async route => {
      uploadCount += 1;
      if (uploadCount === 1) {
        firstUploadStartedResolve();
        await firstUploadRelease;
      }
      const id = uploadCount === 1 ? 74001 : 74002;
      await route.fulfill(jsonResponse({ documents: [{ id, fileName: `proof-${id}.txt`, filePathOrUrl: `UploadedDocs/proof-${id}.txt` }] }));
    });
    await page.route('**/api/audit-documents/*', async route => {
      if (route.request().method() !== 'DELETE') return route.continue();
      archivedProofIds.push(Number(new URL(route.request().url()).pathname.split('/').pop()));
      await route.fulfill(jsonResponse({ archived: true }));
    });
    await page.route('**/api/sales*', async route => {
      if (route.request().method() !== 'POST' || new URL(route.request().url()).pathname !== '/api/sales') return route.continue();
      salePostCount += 1;
      savedPayload = route.request().postDataJSON();
      await route.fulfill(jsonResponse({ id: 74003 }, 201));
    });

    await openQuickModal(page, 'sales', 'directPaid', { customerName: 'Proof race customer', customerPaid: 28 });
    await page.locator('#proof_file_sourceProof').setInputFiles({ name: 'first-proof.txt', mimeType: 'text/plain', buffer: Buffer.from('first') });
    await firstUploadStarted;
    await expect(page.locator('#modalSave')).toBeDisabled();
    await expect(page.locator('#modalSave')).toHaveText('Attaching proof...');

    await page.evaluate(() => window.saveModal());
    expect(salePostCount).toBe(0);
    releaseFirstUpload();
    await expect(page.locator('#field_sourceProof')).toHaveValue('UploadedDocs/proof-74001.txt');
    await expect(page.locator('#modalSave')).toBeEnabled();

    await page.locator('#proof_file_sourceProof').setInputFiles({ name: 'replacement-proof.txt', mimeType: 'text/plain', buffer: Buffer.from('replacement') });
    await expect(page.locator('#field_sourceProof')).toHaveValue('UploadedDocs/proof-74002.txt');
    await page.locator('#modalSave').click();
    await expect(page.locator('#modal')).toBeHidden();

    expect(salePostCount).toBe(1);
    expect(savedPayload.sourceProof).toBe('UploadedDocs/proof-74002.txt');
    expect(archivedProofIds).toEqual([74001]);
    expect(failures).toEqual([]);
  });

  test('replacing an attached proof with a typed path cleans up the unused upload', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const archivedProofIds = [];
    let savedPayload = null;

    await page.goto('/');
    await page.route('**/api/documents/upload', route => route.fulfill(jsonResponse({
      documents: [{ id: 74101, fileName: 'unused-upload.txt', filePathOrUrl: 'UploadedDocs/unused-upload.txt' }],
    })));
    await page.route('**/api/audit-documents/74101', async route => {
      if (route.request().method() !== 'DELETE') return route.continue();
      archivedProofIds.push(74101);
      await route.fulfill(jsonResponse({ archived: true }));
    });
    await page.route('**/api/sales*', async route => {
      if (route.request().method() !== 'POST' || new URL(route.request().url()).pathname !== '/api/sales') return route.continue();
      savedPayload = route.request().postDataJSON();
      await route.fulfill(jsonResponse({ id: 74102 }, 201));
    });

    await openQuickModal(page, 'sales', 'directPaid', { customerName: 'Manual proof path customer', customerPaid: 31 });
    await page.locator('#proof_file_sourceProof').setInputFiles({ name: 'unused-upload.txt', mimeType: 'text/plain', buffer: Buffer.from('unused') });
    await expect(page.locator('#field_sourceProof')).toHaveValue('UploadedDocs/unused-upload.txt');
    await page.locator('#field_sourceProof').fill('OneDrive/receipts/final-proof.pdf');
    await page.locator('#modalSave').click();
    await expect(page.locator('#modal')).toBeHidden();

    expect(savedPayload.sourceProof).toBe('OneDrive/receipts/final-proof.pdf');
    expect(archivedProofIds).toEqual([74101]);
    expect(failures).toEqual([]);
  });

  test('editing one modal field preserves a newer server value in every untouched field', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const original = {
      id: 75001,
      updatedAtUtc: '2026-09-10T14:00:00Z',
      saleDate: '2026-09-10',
      platform: 'Direct',
      paymentMethod: 'Cash',
      customerName: 'Original customer',
      productName: 'Original product name',
      customerPaid: 20,
      status: 'Paid',
      includeInDashboard: true,
      needsReview: false,
    };
    let getCount = 0;
    let putPayload = null;
    let putVersionHeader = null;

    await page.goto('/');
    await page.route('**/api/sales*', async route => {
      const url = new URL(route.request().url());
      if (url.pathname !== '/api/sales') return route.continue();
      if (route.request().method() === 'GET') {
        getCount += 1;
        const row = getCount === 1
          ? original
          : { ...original, updatedAtUtc: '2026-09-10T14:01:00Z', productName: 'Concurrent server product name' };
        return route.fulfill(jsonResponse([row]));
      }
      return route.continue();
    });
    await page.route('**/api/sales/75001', async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      putPayload = route.request().postDataJSON();
      putVersionHeader = route.request().headers()['x-epata-updated-at'];
      await route.fulfill(jsonResponse(putPayload));
    });

    await page.locator('button.nav-button[data-page="sales"]').click();
    await expect(page.locator('[data-edit="75001"]')).toBeVisible();
    await page.locator('[data-edit="75001"]').click();
    await page.locator('#field_customerName').fill('User-edited customer');
    await page.locator('#modalSave').click();
    await expect(page.locator('#modal')).toBeHidden();

    expect(putPayload.customerName).toBe('User-edited customer');
    expect(putPayload.productName).toBe('Concurrent server product name');
    expect(putVersionHeader).toBe('2026-09-10T14:01:00Z');
    expect(failures).toEqual([]);
  });

  test('text columns containing “at” sort as text instead of invalid dates', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await page.route('**/api/products*', async route => {
      if (route.request().method() !== 'GET') return route.continue();
      await route.fulfill(jsonResponse([
        { id: 76001, name: 'A default-first product', sku: 'A-1', material: 'Zulu', color: 'Blue', grams: 5, needsReview: false },
        { id: 76002, name: 'Z material-first product', sku: 'Z-1', material: 'Alpha', color: 'Red', grams: 7, needsReview: false },
      ]));
    });

    await page.goto('/');
    await page.locator('button.nav-button[data-page="products"]').click();
    await expect(page.locator('#tableArea tbody tr').first()).toContainText('A default-first product');
    await page.locator('[data-sort-col="material"]').click();
    await expect(page.locator('#tableArea tbody tr').first()).toContainText('Z material-first product');
    await expect(page.locator('#tableArea tbody tr').first()).toContainText('Alpha');
    expect(failures).toEqual([]);
  });

  test('standalone recent AR opens the exact main-ledger record', async ({ page }) => {
    const suffix = `${Date.now()}-${Math.floor(Math.random() * 10000)}`;
    const invoiceNumber = `PW-AR-BRIDGE-${suffix}`;
    const createResponse = await page.request.post('/api/receivable-invoices', {
      data: {
        invoiceNumber,
        invoiceDate: '2026-09-11',
        dueDate: '2026-09-25',
        customerName: 'Standalone Bridge Customer',
        projectName: 'Exact AR Bridge',
        status: 'Sent',
        invoiceTotal: 38,
        amountPaid: 0,
        paymentMethod: 'Cash',
        needsReview: false,
      },
    });
    expect(createResponse.ok(), await createResponse.text()).toBe(true);

    page.on('dialog', dialog => dialog.accept());
    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#db-status-text')).toContainText('Ready');
    await expect(page.locator('#dashRecentBody')).toContainText(invoiceNumber);
    await page.locator('#dashRecentBody .record-link', { hasText: invoiceNumber }).click();

    await expect(page).toHaveURL(/#receivables$/);
    await expect(page.locator('#modal')).toBeVisible();
    await expect(page.locator('#field_invoiceNumber')).toHaveValue(invoiceNumber);
  });

  test('exact invoice-record filters and archive visibility survive app Back', async ({ page }) => {
    const suffix = `${Date.now()}-${Math.floor(Math.random() * 10000)}`;
    const createResponse = await page.request.post('/api/documents', {
      data: {
        docType: 'INVOICE',
        status: 'Sent',
        docDate: '2026-09-11',
        dueDate: '2026-09-25',
        customerName: 'Record State Customer',
        projectName: `Record State ${suffix}`,
        pageSize: 'LETTER',
        total: 44,
        amountPaid: 0,
        paymentMethod: 'Cash',
        lineItems: [{ sortOrder: 1, description: 'Record-state line', quantity: 1, rate: 44 }],
      },
    });
    const createText = await createResponse.text();
    expect(createResponse.ok(), createText).toBe(true);
    const documentRecord = JSON.parse(createText);

    const archiveResponse = await page.request.delete(`/api/documents/${documentRecord.id}`);
    expect(archiveResponse.ok(), await archiveResponse.text()).toBe(true);
    const archivedRecordsResponse = await page.request.get('/api/documents?includeArchived=true');
    expect(archivedRecordsResponse.ok(), await archivedRecordsResponse.text()).toBe(true);
    const archivedRecord = (await archivedRecordsResponse.json())
      .find(row => Number(row.id) === Number(documentRecord.id) && row.sourceKind !== 'receivable');
    expect(archivedRecord).toMatchObject({ docNumber: documentRecord.docNumber, isArchived: true });

    await page.goto('/');
    const includeArchivedResponse = page.waitForResponse(response => {
      const url = new URL(response.url());
      return response.request().method() === 'GET'
        && url.pathname === '/api/documents'
        && url.searchParams.get('includeArchived') === 'true'
        && response.ok();
    });
    await page.evaluate(number => window.openInvoiceRecordSource(number, true), documentRecord.docNumber);
    await includeArchivedResponse;
    await expect(page.locator('#recSearch')).toHaveValue(documentRecord.docNumber);
    await expect(page.locator('#recIncludeArchived')).toBeChecked();
    const archivedRow = recordRow(page, documentRecord.docNumber);
    await expect(archivedRow).toHaveClass(/archived-record/);
    await expect(archivedRow.getByRole('button', { name: 'Restore', exact: true })).toBeVisible();

    await page.locator('button.nav-button[data-page="sales"]').click();
    await expect(page.locator('button.nav-button[data-page="sales"]')).toHaveClass(/active/);
    await page.locator('#appBackBtn').click();
    await expect(page).toHaveURL(/#invoiceRecords$/);
    await expect(page.locator('#recSearch')).toHaveValue(documentRecord.docNumber);
    await expect(page.locator('#recIncludeArchived')).toBeChecked();
    await expect(recordRow(page, documentRecord.docNumber)).toHaveClass(/archived-record/);
    await expect(recordRow(page, documentRecord.docNumber).getByRole('button', { name: 'Restore', exact: true })).toBeVisible();
  });

  test('rapid archive toggles roll back to committed rows and CSV waits for the newest refresh', async ({ page }) => {
    const activeNumber = `INV-ACTIVE-${Date.now()}`;
    const archivedNumber = `INV-ARCHIVED-${Date.now()}`;
    const activeRows = [{
      id: 98001, sourceKind: 'document', sourceId: 98001, docNumber: activeNumber,
      docType: 'INVOICE', status: 'Sent', customerName: 'Active Export Customer',
      projectName: 'Committed active dataset', total: 21, amountPaid: 0, balance: 21,
      docDate: '2026-09-11', updatedAt: '2026-09-11T12:00:00Z', isArchived: false,
    }];
    const archivedRows = [...activeRows, {
      id: 98002, sourceKind: 'document', sourceId: 98002, docNumber: archivedNumber,
      docType: 'INVOICE', status: 'Void', customerName: 'Archived Export Customer',
      projectName: 'Archived dataset', total: 13, amountPaid: 0, balance: 13,
      docDate: '2026-09-10', updatedAt: '2026-09-10T12:00:00Z', isArchived: true,
    }];
    const deferred = () => {
      let resolve;
      const promise = new Promise(done => { resolve = done; });
      return { promise, resolve };
    };
    const firstArchived = deferred();
    const firstArchivedStarted = deferred();
    const failedActiveStarted = deferred();
    const exportArchived = deferred();
    const exportArchivedStarted = deferred();
    const exportActive = deferred();
    const exportActiveStarted = deferred();
    let listCall = 0;

    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#db-status-text')).toContainText('Ready');
    await page.route('**/api/documents*', async route => {
      const url = new URL(route.request().url());
      if (route.request().method() !== 'GET' || url.pathname !== '/api/documents') return route.continue();
      listCall += 1;
      if (listCall === 1) return route.fulfill(jsonResponse(activeRows));
      if (listCall === 2) {
        firstArchivedStarted.resolve();
        await firstArchived.promise;
        return route.fulfill(jsonResponse(archivedRows));
      }
      if (listCall === 3) {
        failedActiveStarted.resolve();
        return route.fulfill(jsonResponse({ message: 'Simulated current refresh failure.' }, 409));
      }
      if (listCall === 4) {
        exportArchivedStarted.resolve();
        await exportArchived.promise;
        return route.fulfill(jsonResponse(archivedRows));
      }
      if (listCall === 5) {
        exportActiveStarted.resolve();
        await exportActive.promise;
        return route.fulfill(jsonResponse(activeRows));
      }
      return route.fulfill(jsonResponse(url.searchParams.get('includeArchived') === 'true' ? archivedRows : activeRows));
    });

    await page.locator('button.nav-item[data-view="records"]').click();
    await expect(page.locator('#recordsBody')).toContainText(activeNumber);
    const archiveToggle = page.locator('#recIncludeArchived');

    await archiveToggle.check();
    await firstArchivedStarted.promise;
    await archiveToggle.uncheck();
    await failedActiveStarted.promise;
    await expect(page.locator('#toast-container')).toContainText('Archive visibility could not change');
    await expect(archiveToggle).not.toBeChecked();
    await expect(page.locator('#recordsBody')).toContainText(activeNumber);
    await expect(page.locator('#recordsBody')).not.toContainText(archivedNumber);
    const staleArchivedResponse = page.waitForResponse(response => {
      const url = new URL(response.url());
      return url.pathname === '/api/documents' && url.searchParams.get('includeArchived') === 'true';
    });
    firstArchived.resolve();
    await staleArchivedResponse;
    await expect(archiveToggle).not.toBeChecked();
    await expect(page.locator('#recordsBody')).not.toContainText(archivedNumber);

    await archiveToggle.check();
    await exportArchivedStarted.promise;
    let downloadStarted = false;
    const downloadPromise = page.waitForEvent('download').then(download => {
      downloadStarted = true;
      return download;
    });
    await page.locator('#btnExportCsv').click();
    await archiveToggle.uncheck();
    await exportActiveStarted.promise;
    exportArchived.resolve();
    await page.waitForTimeout(100);
    expect(downloadStarted).toBe(false);
    exportActive.resolve();
    const download = await downloadPromise;
    const stream = await download.createReadStream();
    const chunks = [];
    for await (const chunk of stream) chunks.push(chunk);
    const csv = Buffer.concat(chunks).toString('utf8');
    expect(csv).toContain(activeNumber);
    expect(csv).not.toContain(archivedNumber);
    await expect(archiveToggle).not.toBeChecked();
    expect(listCall).toBe(5);
  });

  test('completed printer lane caps cards without understating its total', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const completedRows = Array.from({ length: 13 }, (_, index) => ({
      id: 99000 + index,
      customerName: `Completed Customer ${index + 1}`,
      jobName: `Completed Print ${index + 1}`,
      printerName: 'Bambu P1S',
      status: 'Completed',
      priority: 'Normal',
      progressPercent: 100,
      quantity: 1,
      plateCount: 1,
      estimatedHours: 1,
      actualHours: 1,
      isArchived: false,
    }));

    await page.goto('/');
    await expect(page.locator('#app')).toContainText('Dashboard');
    await page.route('**/api/printer-queue-items*', route => {
      const url = new URL(route.request().url());
      if (route.request().method() !== 'GET' || url.pathname !== '/api/printer-queue-items') return route.continue();
      return route.fulfill(jsonResponse(completedRows));
    });
    await page.route('**/api/customer-jobs*', route => {
      const url = new URL(route.request().url());
      if (route.request().method() !== 'GET' || url.pathname !== '/api/customer-jobs') return route.continue();
      return route.fulfill(jsonResponse([]));
    });

    await page.evaluate(() => window.showPage('printerQueue'));
    const completedColumn = page.locator('.printer-board-column').filter({
      has: page.locator('h3', { hasText: /^Completed$/ }),
    });
    await expect(completedColumn).toHaveCount(1);
    await expect(completedColumn.locator('.printer-board-column-head .badge')).toHaveText('13');
    await expect(completedColumn.locator('.printer-queue-card')).toHaveCount(12);
    await expect(completedColumn).toContainText('Showing the latest 12 of 13 completed prints.');
    expect(failures).toEqual([]);
  });

  test('invoice draft survives another route, app Back, and a direct revisit with truthful hashes', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await page.goto('/');
    await page.locator('button.nav-button[data-page="estimates"]').click();
    await expect(page.locator('#epataInvoiceMerged')).toBeVisible();
    await expect(page.locator('#docNumber')).toHaveValue(/^EST-\d{4}-\d{4}$/);
    await page.locator('#customerName').fill('Back-stack draft customer');
    await page.locator('#projectName').fill('Back-stack draft project');

    await page.evaluate(() => {
      const originalDispose = window._invoiceToolDispose;
      window.__invoiceDisposeCalls = 0;
      window._invoiceToolDispose = (...args) => {
        window.__invoiceDisposeCalls += 1;
        return originalDispose(...args);
      };
    });

    await page.locator('button.nav-button[data-page="sales"]').click();
    await expect(page.locator('button.nav-button[data-page="sales"]')).toHaveClass(/active/);
    await expect.poll(() => page.evaluate(() => window.__invoiceDisposeCalls)).toBe(1);
    await page.locator('#appBackBtn').click();
    await expect(page).toHaveURL(/#estimates$/);
    await expect(page.locator('#epataInvoiceMerged')).toBeVisible();
    await expect(page.locator('#customerName')).toHaveValue('Back-stack draft customer');
    await expect(page.locator('#projectName')).toHaveValue('Back-stack draft project');

    await page.locator('button.nav-button[data-page="sales"]').click();
    await page.locator('button.nav-button[data-page="estimates"]').click();
    await expect(page.locator('#customerName')).toHaveValue('Back-stack draft customer');
    await expect(page.locator('#projectName')).toHaveValue('Back-stack draft project');

    await page.locator('#epataInvoiceMerged button.nav-item[data-view="settings"]').click();
    await expect(page).toHaveURL(/#invoiceSettings$/);
    await expect(page.locator('#view-settings')).toBeVisible();
    await page.goBack();
    await expect(page).toHaveURL(/#estimates$/);
    await expect(page.locator('#view-builder')).toBeVisible();
    await expect(page.locator('#customerName')).toHaveValue('Back-stack draft customer');
    await expect(page.locator('#projectName')).toHaveValue('Back-stack draft project');
    expect(failures).toEqual([]);
  });

  test('New Invoice initialization cannot insert a false Estimates route into Back history', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const customerName = 'Invoice route truth customer';

    await page.goto('/');
    await page.evaluate(name => window.showPage(`customerDetail:${name}`), customerName);
    await expect(page).toHaveURL(/#customerDetail%3AInvoice%20route%20truth%20customer$/);
    const newInvoiceButton = page.getByRole('button', { name: 'New Invoice', exact: true }).first();
    await expect(newInvoiceButton).toBeVisible();

    await newInvoiceButton.click();
    await expect(page).toHaveURL(/#invoices$/);
    await expect(page.locator('#epataInvoiceMerged')).toBeVisible();
    await expect(page.locator('#docType')).toHaveValue('INVOICE');

    await page.locator('#appBackBtn').click();
    await expect(page).toHaveURL(/#customerDetail%3AInvoice%20route%20truth%20customer$/);
    await expect(page.locator('#app')).toContainText(customerName);
    await expect(page.locator('#epataInvoiceMerged')).toHaveCount(0);
    expect(failures).toEqual([]);
  });
});
