const { expect, test } = require('@playwright/test');

const stamp = `PW-CONTROL-${Date.now()}`;

const scenarios = [
  { name: 'customer job', page: 'customerJobs', api: 'customer-jobs', body: { jobDate: '2026-09-10', customerName: `${stamp} Job Customer`, jobName: `${stamp} Job`, status: 'Open', needsReview: true } },
  { name: 'sale', page: 'sales', api: 'sales', body: { saleDate: '2026-09-10', platform: 'Direct', orderNumber: `${stamp}-SALE`, customerName: `${stamp} Sale Customer`, productName: `${stamp} Product`, status: 'Paid', itemSales: 12, customerPaid: 12, includeInDashboard: true, needsReview: true } },
  { name: 'receivable', page: 'receivables', api: 'receivable-invoices', body: { invoiceNumber: `${stamp}-AR`, invoiceDate: '2026-09-10', customerName: `${stamp} AR Customer`, projectName: `${stamp} AR`, status: 'Sent', invoiceTotal: 18, amountPaid: 0, needsReview: true } },
  { name: 'bill', page: 'bills', api: 'bills', body: { billNumber: `${stamp}-BILL`, billDate: '2026-09-10', vendorName: `${stamp} Bill Vendor`, description: `${stamp} Bill`, status: 'Unpaid', total: 9, amountPaid: 0, needsReview: true } },
  { name: 'expense', page: 'expenses', api: 'expenses', body: { expenseDate: '2026-09-10', vendorName: `${stamp} Expense Vendor`, description: `${stamp} Expense`, total: 8, amount: 8, needsReview: true } },
  { name: 'order loss', page: 'orderLosses', api: 'order-loss-incidents', body: { incidentDate: '2026-09-10', orderNumber: `${stamp}-LOSS`, customerName: `${stamp} Loss Customer`, productName: `${stamp} Loss Product`, replacementCogs: 4, reimbursementReceived: 1, needsReview: true } },
  { name: 'product', page: 'products', api: 'products', body: { name: `${stamp} Product Costing`, sku: `${stamp}-SKU`, targetPrice: 20, needsReview: true } },
  { name: 'asset', page: 'assets', api: 'assets', body: { name: `${stamp} Printer`, purchaseDate: '2026-09-10', vendorName: `${stamp} Asset Vendor`, cost: 99, needsReview: true } },
  { name: 'MakerWorld reward', page: 'makerworld', api: 'makerworld-rewards', body: { rewardDate: '2026-09-10', rewardType: 'Points', pointsChange: 25, status: 'Available', needsReview: true } },
  { name: 'audit document', page: 'auditDocs', api: 'audit-documents', body: { documentDate: '2026-09-10', documentType: 'Receipt', relatedRecordType: 'Control Test', relatedRecordNumber: stamp, fileName: `${stamp}.txt`, needsReview: true } },
  { name: 'business account', page: 'accounts', api: 'business-accounts', body: { name: `${stamp} Account`, accountType: 'Cash', currentBalance: 20, isActive: true, notes: `${stamp} initial` } },
  { name: 'action item', page: 'actions', api: 'action-items', body: { title: `${stamp} Action`, area: 'General', priority: 'Normal', status: 'Open', notes: `${stamp} initial` } },
  { name: 'tax obligation', page: 'taxObligations', api: 'tax-obligations', body: { taxYear: 2026, title: `${stamp} Tax Item`, jurisdiction: 'Federal', status: 'Review Applicability', needsReview: true } },
  { name: 'mileage trip', page: 'mileage', api: 'mileage-logs', body: { tripDate: '2026-09-10', vehicle: `${stamp} Vehicle`, startLocation: 'A', endLocation: 'B', businessPurpose: `${stamp} Delivery`, businessMiles: 5, needsReview: true } },
];

function collectBrowserFailures(page) {
  const failures = [];
  page.on('pageerror', error => failures.push(`page error: ${error.message}`));
  page.on('console', message => {
    if (message.type() === 'error') failures.push(`console error: ${message.text()}`);
  });
  page.on('response', response => {
    if (response.url().includes('/api/') && response.status() >= 500) {
      failures.push(`API ${response.status()}: ${response.request().method()} ${response.url()}`);
    }
  });
  return failures;
}

async function waitForPage(page) {
  await expect(page.locator('#app')).not.toContainText('Loading...');
  await expect(page.locator('#app')).not.toContainText('Something broke');
}

test.describe('generic ledger control action matrix', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'The generic mutation matrix runs once against the disposable browser database.');
    page.on('dialog', dialog => dialog.accept());
  });

  for (const scenario of scenarios) {
    test(`${scenario.name}: Add, filter, sort, edit, review, and archive controls stay coherent`, async ({ page }) => {
      const failures = collectBrowserFailures(page);
      const createResponse = await page.request.post(`/api/${scenario.api}`, { data: scenario.body });
      expect(createResponse.ok(), await createResponse.text()).toBe(true);
      const created = await createResponse.json();

      await page.goto('/');
      await waitForPage(page);
      await page.evaluate(target => window.showPage(target), scenario.page);
      await waitForPage(page);

      const addButton = page.locator('#addRowBtn');
      if (await addButton.count()) {
        await addButton.click();
        await expect(page.locator('#modal')).toBeVisible();
        await page.locator('#modalCancel').click();
        await expect(page.locator('#modal')).toBeHidden();
      }

      const reviewQueue = page.locator('#reviewQueueBtn');
      if (scenario.body.needsReview === true) {
        await expect(reviewQueue).toBeVisible();
        await reviewQueue.click();
        await expect(page.locator(`[data-edit="${created.id}"]`)).toBeVisible();
      } else {
        await expect(reviewQueue).toHaveCount(0);
      }

      const sortButtons = page.locator('[data-sort-col]');
      for (let index = 0; index < await sortButtons.count(); index += 1) {
        const key = await sortButtons.nth(index).getAttribute('data-sort-col');
        await page.locator(`[data-sort-col="${key}"]`).click();
        await expect(page.locator(`[data-sort-col="${key}"]`)).toHaveClass(/active/);
      }

      const editButton = page.locator(`[data-edit="${created.id}"]`);
      await expect(editButton).toBeVisible();
      await editButton.click();
      await expect(page.locator('#modal')).toBeVisible();
      const updatedNote = `${stamp} updated ${scenario.name}`;
      const notes = page.locator('#field_notes');
      if (await notes.count()) await notes.fill(updatedNote);
      const saveResponsePromise = page.waitForResponse(response =>
        response.request().method() === 'PUT' &&
        response.url().includes(`/api/${scenario.api}/${created.id}`));
      await page.locator('#modalSave').click();
      const saveResponse = await saveResponsePromise;
      expect(saveResponse.ok(), await saveResponse.text()).toBe(true);
      await expect(page.locator('#modal')).toBeHidden();

      const afterEditResponse = await page.request.get(`/api/${scenario.api}/${created.id}`);
      expect(afterEditResponse.ok()).toBe(true);
      const afterEdit = await afterEditResponse.json();
      if (Object.hasOwn(afterEdit, 'notes')) expect(afterEdit.notes).toBe(updatedNote);

      const reviewedButton = page.locator(`[data-reviewed="${created.id}"]`);
      if (await reviewedButton.count()) {
        const reviewResponsePromise = page.waitForResponse(response =>
          response.request().method() === 'PUT' &&
          response.url().includes(`/api/${scenario.api}/${created.id}`));
        await reviewedButton.click();
        expect((await reviewResponsePromise).ok()).toBe(true);
        const reviewed = await (await page.request.get(`/api/${scenario.api}/${created.id}`)).json();
        expect(reviewed.needsReview).toBe(false);
        const hasUnresolvedReviewStatus = Object.entries(reviewed).some(([key, value]) =>
          /status$/i.test(key) && String(value || '').toLowerCase().includes('review'));
        if (hasUnresolvedReviewStatus) {
          await expect(page.locator(`[data-edit="${created.id}"]`)).toBeVisible();
          await expect(page.locator(`[data-reviewed="${created.id}"]`)).toHaveCount(0);
        } else {
          await expect(page.locator(`[data-edit="${created.id}"]`)).toHaveCount(0);
        }
        await expect(reviewQueue).toHaveText('Show All');
        await reviewQueue.click();
        await expect(page.locator(`[data-edit="${created.id}"]`)).toBeVisible();
      }

      const archiveButton = page.locator(`[data-delete="${created.id}"]`);
      await expect(archiveButton).toBeVisible();
      const archiveResponsePromise = page.waitForResponse(response =>
        response.request().method() === 'DELETE' &&
        response.url().includes(`/api/${scenario.api}/${created.id}`));
      await archiveButton.click();
      expect((await archiveResponsePromise).ok()).toBe(true);
      const archived = await (await page.request.get(`/api/${scenario.api}/${created.id}`)).json();
      expect(archived.isArchived).toBe(true);
      expect(failures).toEqual([]);
    });
  }
});
