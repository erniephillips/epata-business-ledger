const { expect, test } = require('@playwright/test');

const MAIN_PAGES = [
  'dashboard', 'quickAdd', 'invoiceCenter', 'estimates', 'invoices', 'pricingCalculator',
  'invoiceRecords', 'invoiceRateCard', 'invoiceSettings', 'jobTimeline', 'aiEstimate', 'aiOperations',
  'documentIntake', 'sales', 'receivables', 'bills', 'expenses', 'accounts',
  'customerJobs', 'communications', 'printerQueue', 'customers', 'vendors',
  'products', 'assets', 'makerworld', 'aiReview', 'localAi', 'auditDocs',
  'actions', 'taxPrep', 'taxObligations', 'mileage', 'orderLosses', 'importExport',
  'admin', 'ledgerMap', 'workflowGuide', 'globalSearch', 'help',
];

const INVOICE_VIEWS = ['dashboard', 'calculator', 'builder', 'records', 'ratecard', 'settings'];

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

async function waitForSettledSurface(page) {
  await expect(page.locator('body')).not.toContainText('Something broke');
  await expect(page.locator('body')).not.toContainText('Loading...');
  await page.waitForTimeout(50);
}

async function inventoryVisibleControls(page, surface) {
  return page.evaluate(surfaceName => {
    const visible = element => {
      const style = getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return style.display !== 'none' && style.visibility !== 'hidden' &&
        Number(style.opacity) !== 0 && rect.width > 0 && rect.height > 0;
    };
    const normalized = value => String(value || '').replace(/\s+/g, ' ').trim();
    const nameOf = element => {
      const labels = element.labels ? [...element.labels].map(label => label.textContent).join(' ') : '';
      return normalized(
        element.getAttribute('aria-label') ||
        element.getAttribute('aria-labelledby')?.split(/\s+/).map(id => document.getElementById(id)?.textContent).join(' ') ||
        labels || element.textContent || element.getAttribute('title') || element.getAttribute('placeholder') ||
        (element.type === 'file' ? element.name || element.id : '')
      );
    };
    const selector = [
      'button', 'a[href]', 'input:not([type="hidden"])', 'select', 'textarea',
      '[role="button"]', '[onclick]', '[tabindex]:not([tabindex="-1"])',
    ].join(',');
    const elements = [...new Set([...document.querySelectorAll(selector)].filter(visible))];
    return elements.map((element, index) => {
      const rect = element.getBoundingClientRect();
      const nativeInteractive = /^(BUTTON|A|INPUT|SELECT|TEXTAREA|SUMMARY)$/.test(element.tagName);
      return {
        surface: surfaceName,
        index,
        tag: element.tagName.toLowerCase(),
        id: element.id || '',
        type: element.getAttribute('type') || '',
        role: element.getAttribute('role') || '',
        name: nameOf(element),
        disabled: Boolean(element.disabled || element.getAttribute('aria-disabled') === 'true'),
        disabledReason: normalized(element.getAttribute('title') || element.getAttribute('aria-description')),
        keyboardAccessible: nativeInteractive || element.tabIndex >= 0,
        clipped: rect.left < -1 || rect.top < -1 || rect.right > innerWidth + 1 || rect.bottom > innerHeight + 1,
        signature: element.id || `${element.tagName.toLowerCase()}:${nameOf(element).slice(0, 80)}`,
      };
    });
  }, surface);
}

function collectControlQualityIssues(inventory, issues) {
  issues.unnamed.push(...inventory.filter(control => !control.name && control.type !== 'color'));
  issues.inaccessible.push(...inventory.filter(control => !control.keyboardAccessible));
  issues.unexplainedDisabled.push(...inventory.filter(control => control.disabled && !control.disabledReason));
}

function summarizeIssues(controls) {
  return [...new Map(controls.map(control => [
    `${control.surface}|${control.signature}`,
    `${control.surface} :: ${control.signature}`,
  ])).values()];
}

test.describe('rendered control contract', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'The full control inventory runs once; responsive behavior has dedicated coverage.');
  });

  test('inventories every main-shell route and enforces baseline control quality', async ({ page }, testInfo) => {
    const failures = collectBrowserFailures(page);
    const report = {};
    const issues = { unnamed: [], inaccessible: [], unexplainedDisabled: [] };
    await page.goto('/');
    await waitForSettledSurface(page);

    for (const pageId of MAIN_PAGES) {
      await page.evaluate(target => window.showPage(target), pageId);
      const navButton = page.locator(`button.nav-button[data-page="${pageId}"]`);
      if (await navButton.count()) await expect(navButton).toHaveClass(/active/);
      await waitForSettledSurface(page);
      const inventory = await inventoryVisibleControls(page, `main:${pageId}`);
      expect(inventory.length, `${pageId} should expose at least one usable control`).toBeGreaterThan(0);
      collectControlQualityIssues(inventory, issues);
      report[pageId] = inventory;
    }

    const duplicateIds = await page.evaluate(() => [...document.querySelectorAll('[id]')]
      .map(element => element.id)
      .filter((id, index, ids) => id && ids.indexOf(id) !== index));
    await testInfo.attach('main-control-inventory.json', {
      body: Buffer.from(JSON.stringify(report, null, 2)),
      contentType: 'application/json',
    });
    expect([...new Set(duplicateIds)], 'Rendered DOM IDs must be unique').toEqual([]);
    expect(summarizeIssues(issues.unnamed), 'Every visible control needs an accessible name').toEqual([]);
    expect(summarizeIssues(issues.inaccessible), 'Every clickable element must be keyboard accessible').toEqual([]);
    expect(summarizeIssues(issues.unexplainedDisabled), 'Every disabled control must explain why it is unavailable').toEqual([]);
    expect(failures).toEqual([]);
  });

  test('inventories every invoice workspace and enforces baseline control quality', async ({ page }, testInfo) => {
    const failures = collectBrowserFailures(page);
    const report = {};
    const issues = { unnamed: [], inaccessible: [], unexplainedDisabled: [] };
    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#db-status-text')).toContainText('Ready');

    for (const viewId of INVOICE_VIEWS) {
      await page.evaluate(target => window._invoiceToolShowView(target), viewId);
      await expect(page.locator(`#view-${viewId}`)).toBeVisible();
      await waitForSettledSurface(page);
      const inventory = await inventoryVisibleControls(page, `invoice:${viewId}`);
      expect(inventory.length, `${viewId} should expose at least one usable control`).toBeGreaterThan(0);
      collectControlQualityIssues(inventory, issues);
      report[viewId] = inventory;
    }

    const duplicateIds = await page.evaluate(() => [...document.querySelectorAll('[id]')]
      .map(element => element.id)
      .filter((id, index, ids) => id && ids.indexOf(id) !== index));
    await testInfo.attach('invoice-control-inventory.json', {
      body: Buffer.from(JSON.stringify(report, null, 2)),
      contentType: 'application/json',
    });
    expect([...new Set(duplicateIds)], 'Rendered DOM IDs must be unique').toEqual([]);
    expect(summarizeIssues(issues.unnamed), 'Every visible control needs an accessible name').toEqual([]);
    expect(summarizeIssues(issues.inaccessible), 'Every clickable element must be keyboard accessible').toEqual([]);
    expect(summarizeIssues(issues.unexplainedDisabled), 'Every disabled control must explain why it is unavailable').toEqual([]);
    expect(failures).toEqual([]);
  });

  test('inventories data-backed customer and vendor detail controls', async ({ page }, testInfo) => {
    const failures = collectBrowserFailures(page);
    const suffix = Date.now();
    const customerName = `PW Control Customer ${suffix}`;
    const vendorName = `PW Control Vendor ${suffix}`;
    const customerResponse = await page.request.post('/api/parties', {
      data: { name: customerName, partyType: 'Customer', email: 'customer@example.test' },
    });
    const vendorResponse = await page.request.post('/api/parties', {
      data: { name: vendorName, partyType: 'Vendor', email: 'vendor@example.test' },
    });
    expect(customerResponse.ok()).toBe(true);
    expect(vendorResponse.ok()).toBe(true);
    const customer = await customerResponse.json();
    const vendor = await vendorResponse.json();
    const report = {};
    const issues = { unnamed: [], inaccessible: [], unexplainedDisabled: [] };

    try {
      await page.goto('/');
      for (const [mode, name] of [['customer', customerName], ['vendor', vendorName]]) {
        const route = `${mode}Detail:${encodeURIComponent(name)}`;
        await page.evaluate(target => window.showPage(target), route);
        await waitForSettledSurface(page);
        await expect(page.locator('#app h2').first()).toContainText(name);
        const inventory = await inventoryVisibleControls(page, `main:${mode}Detail`);
        expect(inventory.length).toBeGreaterThan(5);
        collectControlQualityIssues(inventory, issues);
        report[mode] = inventory;
      }
    } finally {
      await page.request.delete(`/api/parties/${customer.id}`);
      await page.request.delete(`/api/parties/${vendor.id}`);
    }

    await testInfo.attach('relationship-detail-control-inventory.json', {
      body: Buffer.from(JSON.stringify(report, null, 2)),
      contentType: 'application/json',
    });
    expect(summarizeIssues(issues.unnamed), 'Every visible detail control needs an accessible name').toEqual([]);
    expect(summarizeIssues(issues.inaccessible), 'Every detail control must be keyboard accessible').toEqual([]);
    expect(summarizeIssues(issues.unexplainedDisabled), 'Every disabled detail control must explain why it is unavailable').toEqual([]);
    expect(failures).toEqual([]);
  });
});
