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

async function expectReady(page) {
  await expect(page.locator('body')).not.toContainText('Something broke');
  await expect(page.locator('body')).not.toContainText('Loading...');
}

async function expectNoClippedControls(page) {
  const clipped = await page.evaluate(() => {
    const visible = element => {
      const style = window.getComputedStyle(element);
      const rect = element.getBoundingClientRect();
      return style.display !== 'none' &&
        style.visibility !== 'hidden' &&
        rect.width > 0 &&
        rect.height > 0;
    };

    return [...document.querySelectorAll('button, a.primary-button, a.ghost-button, .btn, .btn-ghost, .nav-button, .nav-item')]
      .filter(visible)
      .filter(element => !element.closest('#sidebar, #primarySidebar'))
      .filter(element =>
        !element.classList.contains('nav-item') &&
        !element.classList.contains('kpi-button') &&
        !element.classList.contains('control-item-button'))
      .filter(element => element.scrollWidth > element.clientWidth + 2 || element.scrollHeight > element.clientHeight + 2)
      .map(element => ({
        text: (element.textContent || element.getAttribute('aria-label') || element.id || element.className || element.tagName).trim().slice(0, 80),
        id: element.id || '',
        className: element.className || '',
      }));
  });
  expect(clipped).toEqual([]);
}

test.describe('navigation, upload controls, and responsive browser checks', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'This spec sets its own desktop/laptop/mobile viewports.');
  });

  test('document intake, invoice PDF import, and safe database backup controls are wired', async ({ page }) => {
    const failures = collectBrowserFailures(page);

    await page.goto('/');
    await page.locator('button.nav-button[data-page="documentIntake"]').click();
    await expectReady(page);
    await expect(page.locator('#docFiles')).toHaveAttribute('type', 'file');
    await expect(page.locator('#docFiles')).toHaveAttribute('multiple', '');
    await expect(page.locator('#uploadDocsBtn')).toBeVisible();
    await expect(page.locator('#relatedTypeTrigger')).toContainText('Smart intake');
    await expect(page.locator('#relatedTypeTrigger')).toContainText('Recommended for Etsy PDFs');
    await page.locator('#relatedTypeTrigger').click();
    await expect(page.locator('.descriptive-select-divider')).toContainText(['Automatic routing', 'Money coming in', 'Money going out', 'Work, people & records']);
    const saleChoice = page.locator('[data-related-area="Sale"]');
    await expect(saleChoice).toContainText('A customer already paid you');
    const saleChoiceStyles = await saleChoice.evaluate(element => {
      const style = getComputedStyle(element);
      const title = element.querySelector('strong').getBoundingClientRect();
      const description = element.querySelector('small').getBoundingClientRect();
      const menuElement = element.closest('.descriptive-select-menu');
      const menu = menuElement.getBoundingClientRect();
      const uploadButton = document.querySelector('#uploadDocsBtn').getBoundingClientRect();
      return {
        alignItems: style.alignItems,
        borderRadius: style.borderRadius,
        justifyContent: style.justifyContent,
        justifyItems: style.justifyItems,
        textAlign: style.textAlign,
        copyAligned: Math.abs(title.left - description.left) < 1,
        menuPosition: getComputedStyle(menuElement).position,
        menuClearsUploadButton: menu.bottom <= uploadButton.top,
      };
    });
    expect(saleChoiceStyles).toEqual({
      alignItems: 'start',
      borderRadius: '0px',
      justifyContent: 'stretch',
      justifyItems: 'start',
      textAlign: 'left',
      copyAligned: true,
      menuPosition: 'static',
      menuClearsUploadButton: true,
    });
    await saleChoice.click();
    await expect(page.locator('#relatedType')).toHaveValue('Sale');
    await expect(page.locator('#relatedTypeTrigger')).toContainText('including a completed Etsy order');
    await page.locator('#uploadDocsBtn').click();
    await expect(page.locator('#toast-container')).toContainText('Choose a file first.');

    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#db-status-text')).toContainText('Ready');
    await expect(page.locator('#view-dashboard')).toBeVisible();
    await page.locator('button.nav-item[data-view="records"]').click();
    await expect(page.locator('#view-records')).toBeVisible();
    await expect(page.locator('#invoicePdfImportFile')).toHaveAttribute('accept', /pdf/i);
    await expect(page.locator('#importDbFile')).toHaveCount(0);

    const restoreUnavailable = page.locator('#view-records').getByRole('button', { name: 'Database restore unavailable' });
    await expect(restoreUnavailable).toBeDisabled();
    await expect(restoreUnavailable).toHaveAttribute('aria-disabled', 'true');
    await expect(restoreUnavailable).toHaveAttribute('title', /disabled.*replacing the live ledger.*Download Backup/i);

    await page.locator('#invoicePdfImportFile').evaluate(input => {
      input.dataset.clickedByVisibleButton = 'false';
      input.click = function markClicked() {
        this.dataset.clickedByVisibleButton = 'true';
      };
    });
    await page.locator('#btnImportPdfDraft').click();
    await expect(page.locator('#invoicePdfImportFile')).toHaveAttribute('data-clicked-by-visible-button', 'true');

    const backupDownload = page.waitForEvent('download');
    await page.locator('#btnExportDb').click();
    const download = await backupDownload;
    expect(download.suggestedFilename()).toMatch(/\.(db|sqlite|zip)$/i);

    expect(failures).toEqual([]);
  });

  test('document intake upload results survive Open Job and Open Proof back navigation', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const orderNumber = 'PW-INTAKE-RESTORE-001';
    const auditDocument = {
      id: 91001,
      documentDate: '2026-09-10T00:00:00',
      documentType: 'Etsy Order',
      relatedRecordType: 'Sale',
      relatedRecordNumber: orderNumber,
      fileName: 'playwright-intake-restore.txt',
      filePathOrUrl: 'UploadedDocs/playwright-intake-restore.txt',
      needsReview: false,
      notes: 'Synthetic browser-only intake result.',
    };
    const uploadResult = {
      count: 1,
      documents: [auditDocument],
      suggestions: [],
      marketplaceImports: [{
        recognized: true,
        action: 'Created',
        message: `Created and linked Etsy order ${orderNumber}.`,
        usedAi: false,
        sale: {
          id: 92001,
          platform: 'Etsy',
          orderNumber,
          customerName: 'Playwright Intake Customer',
          productName: 'Playwright Intake Fixture',
          customerPaid: 24.50,
          needsReview: true,
        },
        job: {
          id: 93001,
          relatedOrderNumber: orderNumber,
          jobName: 'Playwright Intake Fixture',
        },
        customer: {
          id: 94001,
          name: 'Playwright Intake Customer',
        },
        auditDocument,
        warnings: [],
      }],
      marketplaceCreated: 1,
      marketplaceLinked: 0,
      marketplaceNeedsReview: 0,
    };

    await page.route('**/api/documents/upload', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(uploadResult),
    }));

    await page.goto('/');
    await page.locator('button.nav-button[data-page="documentIntake"]').click();
    await expectReady(page);
    await page.locator('#docFiles').setInputFiles({
      name: auditDocument.fileName,
      mimeType: 'text/plain',
      buffer: Buffer.from('Synthetic browser fixture; the upload API response is mocked.'),
    });
    await page.locator('#uploadDocsBtn').click();

    const restoredResult = page.locator('#uploadResult');
    const marketplaceCard = () => page.locator('#uploadResult .assistant-suggestion-card').filter({ hasText: orderNumber });
    await expect(restoredResult).toContainText('Uploaded and indexed 1 document');
    await expect(marketplaceCard()).toContainText(orderNumber);
    await expect(marketplaceCard()).toContainText('Playwright Intake Fixture');
    await expect(page.locator('#docFiles')).toHaveValue('');

    for (const destination of [
      { buttonName: /^Open Jobs?$/, heading: 'Customer Jobs', back: 'app' },
      { buttonName: /^Open Proof$/, heading: 'Audit Docs / Proof Index', back: 'browser' },
    ]) {
      await marketplaceCard().getByRole('button', { name: destination.buttonName }).click();
      await expectReady(page);
      await expect(page.locator('#app h2').first()).toContainText(destination.heading);
      await expect(page.locator('#appBackBtn')).toBeVisible();
      if (destination.back === 'browser') await page.goBack();
      else await page.locator('#appBackBtn').click();
      await expectReady(page);
      await expect(page.locator('#app h2').first()).toContainText('Document Intake');
      await expect(restoredResult).toContainText('Uploaded and indexed 1 document');
      await expect(marketplaceCard()).toContainText(orderNumber);
      await expect(marketplaceCard()).toContainText('Playwright Intake Fixture');
    }

    expect(failures).toEqual([]);
  });

  for (const viewport of [
    { name: 'desktop', width: 1440, height: 900 },
    { name: 'laptop', width: 1280, height: 720 },
    { name: 'mobile', width: 390, height: 844 },
  ]) {
    test(`main shell and invoice builder controls are not clipped at ${viewport.name}`, async ({ page }) => {
      const failures = collectBrowserFailures(page);
      await page.setViewportSize({ width: viewport.width, height: viewport.height });

      await page.goto('/');
      await expectReady(page);
      for (const pageId of ['dashboard', 'invoiceRecords', 'documentIntake', 'taxPrep', 'aiReview']) {
        await page.evaluate(targetPage => window.showPage(targetPage), pageId);
        await expectReady(page);
        await expectNoClippedControls(page);
      }

      await page.goto('/invoice-builder/index.html');
      await expect(page.locator('#db-status-text')).toContainText('Ready');
      for (const viewId of ['dashboard', 'builder', 'records', 'settings']) {
        await page.evaluate(targetView => window._invoiceToolShowView(targetView), viewId);
        await expect(page.locator(`#view-${viewId}`)).toBeVisible();
        await expectNoClippedControls(page);
      }

      expect(failures).toEqual([]);
    });
  }
});
