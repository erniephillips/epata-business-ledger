const { expect, test } = require('@playwright/test');

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

async function waitForApp(page) {
  await expect(page.locator('#app')).not.toContainText('Loading...');
  await expect(page.locator('#app')).not.toContainText('Something broke');
}

test.describe('persistent shell controls', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'Global shell controls are covered once; viewport behavior is covered separately.');
  });

  test('sidebar, global search, Back, and header destinations preserve their contracts', async ({ page }) => {
    page.setDefaultTimeout(5000);
    page.setDefaultNavigationTimeout(10000);
    const failures = collectBrowserFailures(page);
    const suffix = Date.now();
    const customerName = `PW Global Search ${suffix}`;
    const createdResponse = await page.request.post('/api/sales', {
      data: {
        saleDate: '2026-09-10', platform: 'Direct', orderNumber: `PW-SEARCH-${suffix}`,
        customerName, productName: 'Searchable control fixture', itemSales: 11,
        customerPaid: 11, status: 'Paid', includeInDashboard: true,
      },
    });
    expect(createdResponse.ok()).toBe(true);
    const created = await createdResponse.json();

    try {
      await test.step('collapse state survives a reload and can be restored', async () => {
        await page.goto('/');
        await waitForApp(page);
        if (await page.locator('#sidebarToggle').getAttribute('aria-expanded') === 'false') {
          await page.locator('#sidebarToggle').click();
          await expect(page.locator('#sidebarToggle')).toHaveAttribute('aria-expanded', 'true');
        }
        await page.locator('#sidebarToggle').click();
        await expect(page.locator('#sidebarToggle')).toHaveAttribute('aria-expanded', 'false');
        await page.reload({ waitUntil: 'domcontentloaded' });
        await waitForApp(page);
        await expect(page.locator('#sidebarToggle')).toHaveAttribute('aria-expanded', 'false');
        await page.locator('#sidebarToggle').click();
        await expect(page.locator('#sidebarToggle')).toHaveAttribute('aria-expanded', 'true');
      });

      await test.step('global search opens the matching record', async () => {
        await page.locator('#globalSearch').fill(customerName);
        await expect(page.locator('#app h2').first()).toContainText('Search Results');
        const result = page.locator('.result-card').filter({ hasText: customerName }).first();
        await expect(result).toBeVisible();
        await result.click();
        await expect(page.locator('#modal')).toBeVisible();
        await expect(page.locator('#field_customerName')).toHaveValue(customerName);
        await page.locator('#modalClose').click();
      });

      await test.step('Back returns from Local AI without losing shell state', async () => {
        await page.locator('#localAiHeaderStatus').click();
        await waitForApp(page);
        await expect(page.locator('button.nav-button[data-page="localAi"]')).toHaveClass(/active/);
        await expect(page.locator('#appBackBtn')).toBeVisible();
        await page.locator('#appBackBtn').click();
        await waitForApp(page);
        await expect(page.locator('#app h2').first()).toContainText('Search Results');
        await expect(page.locator('#globalSearch')).toHaveValue(customerName);
        await expect(page.locator('.result-card').filter({ hasText: customerName }).first()).toBeVisible();
      });

      await test.step('Estimates / Invoices opens the builder', async () => {
        await page.getByRole('button', { name: 'Estimates / Invoices' }).click();
        await expect(page.locator('button.nav-button[data-page="estimates"]')).toHaveClass(/active/);
        await expect(page.locator('#view-builder')).toBeVisible();
      });
    } finally {
      await page.request.delete(`/api/sales/${created.id}`, { timeout: 5000 });
    }

    expect(failures).toEqual([]);
  });

  test('dashboard action and breakdown controls navigate and close cleanly', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await page.goto('/');
    await waitForApp(page);

    const kpi = page.locator('.kpi-button').first();
    await expect(kpi).toBeVisible();
    await kpi.click();
    await expect(page.locator('#breakdownModal')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('#breakdownModal')).toBeHidden();

    for (const [name, destination] of [
      ['Add Transaction', 'quickAdd'],
      ['Upload Proof', 'documentIntake'],
      ['Open AI Review', 'aiReview'],
      ['AI Operations', 'aiOperations'],
    ]) {
      await page.evaluate(() => window.showPage('dashboard'));
      await waitForApp(page);
      await page.getByRole('button', { name, exact: true }).first().click();
      await waitForApp(page);
      await expect(page.locator(`button.nav-button[data-page="${destination}"]`)).toHaveClass(/active/);
    }

    expect(failures).toEqual([]);
  });

  test('Backup DB control produces a real SQLite download from the disposable test ledger', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await page.goto('/');
    await waitForApp(page);
    const downloadPromise = page.waitForEvent('download');
    await page.locator('#backupBtn').click();
    const download = await downloadPromise;
    expect(download.suggestedFilename()).toMatch(/\.db$/i);
    const path = await download.path();
    expect(path).toBeTruthy();
    expect(failures).toEqual([]);
  });
});
