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
    await page.route('**/api/sales', async route => {
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
    await page.route('**/api/expenses', async route => {
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
    await page.route('**/api/sales', async route => {
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
    await page.route('**/api/sales', async route => {
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
    await page.route('**/api/sales', async route => {
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
    await page.route('**/api/sales', async route => {
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
    await page.route('**/api/products', async route => {
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
