const { expect, test } = require('@playwright/test');

async function waitForApp(page) {
  await expect(page.locator('#app')).not.toContainText('Loading...');
  await expect(page.locator('#app')).not.toContainText('Something broke');
}

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

async function openAndCancelPrefilledModal(page, controls, buttonName, fieldSelector, expectedValue) {
  await controls.getByRole('button', { name: buttonName, exact: true }).click();
  await expect(page.locator('#modal')).toBeVisible();
  await expect(page.locator(fieldSelector)).toHaveValue(expectedValue);
  await page.locator('#modalCancel').click();
  await expect(page.locator('#modal')).toBeHidden();
}

test.describe('customer and vendor relationship controls', () => {
  test.beforeEach(async ({ page }, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'The stateful relationship journey runs once against the disposable ledger.');
    page.setDefaultTimeout(8000);
  });

  test('names with punctuation, detail actions, nested Back, and directory filters remain coherent', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const suffix = Date.now();
    const customerName = `D'Angelo Controls ${suffix}`;
    const vendorName = `O'Neil Supply ${suffix}`;
    const created = [];

    const create = async (route, data) => {
      const response = await page.request.post(`/api/${route}`, { data });
      expect(response.ok(), await response.text()).toBe(true);
      const row = await response.json();
      created.push({ route, id: row.id });
      return row;
    };

    await create('parties', { name: customerName, partyType: 'Customer', defaultPlatform: 'Direct' });
    await create('sales', {
      saleDate: '2026-09-10', platform: 'Direct', customerName,
      orderNumber: `REL-C-${suffix}`, productName: 'Relationship control fixture',
      itemSales: 17, customerPaid: 17, status: 'Paid', includeInDashboard: true,
    });
    await create('parties', { name: vendorName, partyType: 'Vendor', defaultPlatform: 'Vendor' });
    await create('expenses', {
      expenseDate: '2026-09-10', vendorName, description: 'Relationship control fixture',
      amount: 6, total: 6, category: 'Supplies', taxCategory: 'Supplies',
    });

    try {
      await page.goto('/');
      await waitForApp(page);

      await test.step('customer and vendor table links handle punctuation and preserve their filters on Back', async () => {
        await page.evaluate(() => window.showPage('sales'));
        await waitForApp(page);
        await page.locator('#searchBox').fill(customerName);
        await page.getByRole('button', { name: customerName, exact: true }).click();
        await waitForApp(page);
        await expect(page.locator('#app h2').first()).toHaveText(customerName);
        await page.locator('#appBackBtn').click();
        await waitForApp(page);
        await expect(page.locator('button.nav-button[data-page="sales"]')).toHaveClass(/active/);
        await expect(page.locator('#searchBox')).toHaveValue(customerName);
        await expect(page.getByRole('button', { name: customerName, exact: true })).toBeVisible();

        await page.evaluate(() => window.showPage('expenses'));
        await waitForApp(page);
        await page.locator('#searchBox').fill(vendorName);
        await page.getByRole('button', { name: vendorName, exact: true }).click();
        await waitForApp(page);
        await expect(page.locator('#app h2').first()).toHaveText(vendorName);
        await page.locator('#appBackBtn').click();
        await waitForApp(page);
        await expect(page.locator('button.nav-button[data-page="expenses"]')).toHaveClass(/active/);
        await expect(page.locator('#searchBox')).toHaveValue(vendorName);
        await expect(page.getByRole('button', { name: vendorName, exact: true })).toBeVisible();
      });

      await test.step('customer detail controls preserve the exact name', async () => {
        await page.evaluate(() => window.showPage('customers'));
        await waitForApp(page);
        await page.locator('#relationshipSearch').fill(customerName);
        await page.getByRole('button', { name: customerName, exact: true }).click();
        await expect(page.locator('#app h2').first()).toHaveText(customerName);

        await page.getByRole('button', { name: 'Edit Contact', exact: true }).click();
        await expect(page.locator('#field_name')).toHaveValue(customerName);
        await page.locator('#modalCancel').click();

        const controls = page.locator('.page-head .actions');
        for (const [buttonName, field] of [
          ['Log Communication', '#field_customerName'],
          ['Add Job', '#field_customerName'],
          ['Add AR', '#field_customerName'],
          ['Add Sale', '#field_customerName'],
        ]) {
          await openAndCancelPrefilledModal(page, controls, buttonName, field, customerName);
        }

        for (const [buttonName, documentType] of [
          ['New Estimate', 'ESTIMATE'],
          ['New Invoice', 'INVOICE'],
        ]) {
          await page.locator('.page-head .actions').getByRole('button', { name: buttonName, exact: true }).click();
          await expect(page.locator('#view-builder')).toBeVisible();
          await expect(page.locator('#docType')).toHaveValue(documentType);
          await expect(page.locator('#customerName')).toHaveValue(customerName);
          await page.locator('#appBackBtn').click();
          await waitForApp(page);
          await expect(page.locator('#app h2').first()).toHaveText(customerName);
        }

        await page.locator('.page-head .actions').getByRole('button', { name: 'Upload Proof', exact: true }).click();
        await waitForApp(page);
        await expect(page.locator('#relatedType')).toHaveValue('Customer');
        await expect(page.locator('#relatedNumber')).toHaveValue(customerName);
        await page.locator('#appBackBtn').click();
        await waitForApp(page);
        await expect(page.locator('#app h2').first()).toHaveText(customerName);

        await page.locator('.page-head .actions').getByRole('button', { name: 'Back', exact: true }).click();
        await waitForApp(page);
        await expect(page.locator('#relationshipSearch')).toHaveValue(customerName);
        await expect(page.getByRole('button', { name: customerName, exact: true })).toBeVisible();
      });

      await test.step('vendor detail controls and nested Back preserve the exact name', async () => {
        await page.evaluate(() => window.showPage('vendors'));
        await waitForApp(page);
        await page.locator('#relationshipSearch').fill(vendorName);
        await page.getByRole('button', { name: vendorName, exact: true }).click();
        await expect(page.locator('#app h2').first()).toHaveText(vendorName);

        await page.getByRole('button', { name: 'Edit Contact', exact: true }).click();
        await expect(page.locator('#field_name')).toHaveValue(vendorName);
        await page.locator('#modalCancel').click();

        const controls = page.locator('.page-head .actions');
        for (const [buttonName, field] of [
          ['Add Asset', '#field_vendorName'],
          ['Add Bill', '#field_vendorName'],
          ['Add Expense', '#field_vendorName'],
        ]) {
          await openAndCancelPrefilledModal(page, controls, buttonName, field, vendorName);
        }

        await controls.getByRole('button', { name: 'Upload Proof', exact: true }).click();
        await waitForApp(page);
        await expect(page.locator('#relatedType')).toHaveValue('Vendor');
        await expect(page.locator('#relatedNumber')).toHaveValue(vendorName);
        await page.locator('#appBackBtn').click();
        await waitForApp(page);
        await expect(page.locator('#app h2').first()).toHaveText(vendorName);

        await page.locator('.page-head .actions').getByRole('button', { name: 'Back', exact: true }).click();
        await waitForApp(page);
        await expect(page.locator('#relationshipSearch')).toHaveValue(vendorName);
        await expect(page.getByRole('button', { name: vendorName, exact: true })).toBeVisible();
      });

      expect(failures).toEqual([]);
    } finally {
      for (const row of created.reverse()) {
        await page.request.delete(`/api/${row.route}/${row.id}`, { timeout: 5000 });
      }
    }
  });
});
