const { expect, test } = require('@playwright/test');

function collectBrowserFailures(page) {
  const failures = [];
  page.on('console', message => {
    if (message.type() === 'error') failures.push(`console error: ${message.text()}`);
  });
  page.on('pageerror', error => failures.push(`page error: ${error.message}`));
  page.on('response', response => {
    const url = response.url();
    if (url.includes('/api/') && response.status() >= 500) {
      failures.push(`failed API response ${response.status()}: ${url}`);
    }
  });
  return failures;
}

async function waitForDocumentSave(page) {
  const response = await page.waitForResponse(resp =>
    resp.url().includes('/api/documents') &&
    ['POST', 'PUT'].includes(resp.request().method()) &&
    resp.status() < 400,
  );
  return response.json();
}

function recordRow(page, docNumber) {
  return page.locator('#recordsBody tr')
    .filter({ has: page.locator('td.doc-number', { hasText: docNumber }) })
    .first();
}

async function expectPreviewText(page, expectedText) {
  await expect.poll(async () => page.locator('#invoicePreviewFrame').evaluate(frame =>
    frame.contentDocument?.body?.innerText || frame.srcdoc || '',
  )).toContain(expectedText);
}

test.describe('standalone invoice builder browser workflow', () => {
  test('creates estimate, reopens, converts to paid invoice, archives, restores, and exports', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#view-dashboard')).toBeVisible();

    await page.locator('button.nav-item[data-view="builder"]').click();
    await expect(page.locator('#view-builder')).toBeVisible();

    await page.locator('#customerName').fill('Playwright Round Customer');
    await page.locator('#projectName').fill('Playwright Estimate Widget');
    await page.locator('#paymentMethod').selectOption('Unknown / Review');
    await page.locator('#lineItemsBody .item-desc').first().fill('Browser-created estimate line');
    await page.locator('#lineItemsBody .item-qty').first().fill('2');
    await page.locator('#lineItemsBody .item-rate').first().fill('30');
    await expectPreviewText(page, 'Playwright Round Customer');
    await expectPreviewText(page, 'Browser-created estimate line');

    const savePromise = waitForDocumentSave(page);
    await page.locator('#btnSaveDraft').click();
    const estimate = await savePromise;
    expect(estimate.docType).toBe('ESTIMATE');
    expect(estimate.id).toBeGreaterThan(0);
    await expect(page.locator('#activeRecordText')).toContainText(estimate.docNumber);

    await page.locator('button.nav-item[data-view="records"]').click();
    await expect(page.locator('#view-records')).toBeVisible();
    await page.locator('#recSearch').fill(estimate.docNumber);
    await expect(page.locator('#recordsBody')).toContainText(estimate.docNumber);
    const estimateRow = recordRow(page, estimate.docNumber);
    await expect(estimateRow.locator('td.doc-number')).toHaveText(estimate.docNumber);
    await estimateRow.locator('button', { hasText: 'Open' }).click();
    await expect(page.locator('#view-builder')).toBeVisible();
    await expect(page.locator('#activeRecordText')).toContainText(estimate.docNumber);

    await page.locator('button.nav-item[data-view="records"]').click();
    await expect(page.locator('#view-records')).toBeVisible();
    await page.locator('#recSearch').fill(estimate.docNumber);
    const convertResponse = page.waitForResponse(resp =>
      resp.url().includes(`/api/documents/${estimate.id}/convert-to-invoice`) &&
      resp.request().method() === 'POST' &&
      resp.status() < 400,
    );
    await recordRow(page, estimate.docNumber).locator('button', { hasText: 'Create Invoice' }).click();
    const invoice = await (await convertResponse).json();
    expect(invoice.docType).toBe('INVOICE');
    expect(invoice.id).not.toBe(estimate.id);
    const fetchedInvoice = await page.request.get(`/api/documents/${invoice.id}`).then(resp => resp.json());
    expect(fetchedInvoice.docNumber).toBe(invoice.docNumber);
    await expect(page.locator('#view-builder')).toBeVisible();
    await expect(page.locator('#activeRecordText')).toContainText(invoice.docNumber);

    await page.locator('#docStatus').selectOption('Paid');
    await page.locator('#paymentMethod').selectOption('Credit Card');
    const paidSavePromise = waitForDocumentSave(page);
    await page.locator('#btnSaveDraft').click();
    const paidInvoice = await paidSavePromise;
    expect(paidInvoice.status).toBe('Paid');
    expect(paidInvoice.docType).toBe('INVOICE');

    const sales = await page.request.get('/api/sales').then(resp => resp.json());
    expect(sales.some(row => row.invoiceNumber === paidInvoice.docNumber && row.paymentMethod === 'Credit Card')).toBe(true);
    const receivables = await page.request.get('/api/receivable-invoices').then(resp => resp.json());
    expect(receivables.some(row => row.invoiceNumber === paidInvoice.docNumber && row.status === 'Paid')).toBe(true);

    await page.locator('button.nav-item[data-view="records"]').click();
    await expect(page.locator('#view-records')).toBeVisible();
    await page.locator('#recSearch').fill(paidInvoice.docNumber);
    const archiveResponse = page.waitForResponse(resp =>
      resp.url().includes(`/api/documents/${paidInvoice.id}`) &&
      resp.request().method() === 'DELETE' &&
      resp.status() < 400,
    );
    page.once('dialog', dialog => dialog.accept());
    await recordRow(page, paidInvoice.docNumber).locator('button[aria-label^="Archive"]').click();
    await archiveResponse;

    await page.locator('#recIncludeArchived').check();
    await expect(page.locator('#view-records')).toBeVisible();
    await page.locator('#recSearch').fill(paidInvoice.docNumber);
    const restoreResponse = page.waitForResponse(resp =>
      resp.url().includes(`/api/documents/${paidInvoice.id}/restore`) &&
      resp.request().method() === 'POST' &&
      resp.status() < 400,
    );
    await recordRow(page, paidInvoice.docNumber).locator('button', { hasText: 'Restore' }).click();
    await restoreResponse;

    const downloadPromise = page.waitForEvent('download');
    await page.locator('#btnExportCsv').click();
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toMatch(/EPATA_Records_\d{4}-\d{2}-\d{2}\.csv/i);

    expect(failures).toEqual([]);
  });
});
