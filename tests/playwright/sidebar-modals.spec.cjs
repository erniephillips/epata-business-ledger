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

test.describe('main shell sidebar and modal browser smoke', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'Desktop sidebar/modal smoke is covered once; responsive layout has separate viewport checks.');
  });

  test('opens primary sidebar pages without browser errors', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await page.goto('/');
    await expect(page.locator('#app')).toContainText('Dashboard');

    const pages = [
      'dashboard',
      'quickAdd',
      'estimates',
      'invoices',
      'pricingCalculator',
      'invoiceRecords',
      'jobTimeline',
      'aiEstimate',
      'aiOperations',
      'documentIntake',
      'sales',
      'receivables',
      'bills',
      'expenses',
      'accounts',
      'customerJobs',
      'communications',
      'printerQueue',
      'customers',
      'vendors',
      'products',
      'assets',
      'makerworld',
      'aiReview',
      'localAi',
      'auditDocs',
      'actions',
      'taxPrep',
      'taxObligations',
      'mileage',
      'importExport',
      'admin',
      'ledgerMap',
      'workflowGuide',
      'help',
    ];

    for (const pageId of pages) {
      await page.locator(`button.nav-button[data-page="${pageId}"]`).click();
      await expect(page.locator('#app')).not.toContainText('Loading...');
      await expect(page.locator('#app')).not.toContainText('Something broke');
      await expect(page.locator(`button.nav-button[data-page="${pageId}"]`)).toHaveClass(/active/);
    }

    expect(failures).toEqual([]);
  });

  test('opens standalone invoice builder tabs without browser errors', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#db-status-text')).toContainText('Ready');

    for (const viewId of ['dashboard', 'calculator', 'builder', 'records', 'ratecard', 'settings']) {
      await page.locator(`button.nav-item[data-view="${viewId}"]`).click();
      await expect(page.locator(`#view-${viewId}`)).toBeVisible();
      await expect(page.locator(`button.nav-item[data-view="${viewId}"]`)).toHaveClass(/active/);
    }

    expect(failures).toEqual([]);
  });

  test('generic primary modals focus, trap Tab, and close from keyboard', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await page.goto('/');
    await expect(page.locator('#app')).toContainText('Dashboard');

    const modalTargets = [
      ['sales', 'etsy'],
      ['receivables', 'invoice'],
      ['bills', 'bill'],
      ['expenses', 'expense'],
      ['customerJobs', 'estimateSent'],
      ['communications', 'communication'],
      ['printerQueue', 'queue'],
      ['parties', 'partyContact'],
      ['products', 'productCosting'],
      ['assets', 'assetPurchase'],
      ['auditDocs', 'auditDoc'],
      ['actions', 'actionItem'],
    ];

    for (const [configKey, kind] of modalTargets) {
      await page.evaluate(([key, preset]) => window.quickOpen(key, preset), [configKey, kind]);
      const modal = page.locator('#modal');
      await expect(modal).toBeVisible();
      await expect(modal).toHaveAttribute('role', 'dialog');
      await expect(modal).toHaveAttribute('aria-modal', 'true');

      const activeTag = await page.evaluate(() => document.activeElement?.tagName);
      expect(['INPUT', 'SELECT', 'TEXTAREA', 'BUTTON']).toContain(activeTag);

      await page.keyboard.press('Shift+Tab');
      const focusedInsideAfterShiftTab = await page.evaluate(() => document.querySelector('#modal')?.contains(document.activeElement));
      expect(focusedInsideAfterShiftTab).toBe(true);

      await page.keyboard.press('Tab');
      const focusedInsideAfterTab = await page.evaluate(() => document.querySelector('#modal')?.contains(document.activeElement));
      expect(focusedInsideAfterTab).toBe(true);

      await page.keyboard.press('Escape');
      await expect(modal).toBeHidden();
    }

    expect(failures).toEqual([]);
  });
});
