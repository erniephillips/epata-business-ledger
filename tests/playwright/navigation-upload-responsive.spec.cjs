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

  test('document intake and standalone invoice import buttons expose real file inputs', async ({ page }) => {
    const failures = collectBrowserFailures(page);

    await page.goto('/');
    await page.locator('button.nav-button[data-page="documentIntake"]').click();
    await expectReady(page);
    await expect(page.locator('#docFiles')).toHaveAttribute('type', 'file');
    await expect(page.locator('#docFiles')).toHaveAttribute('multiple', '');
    await expect(page.locator('#uploadDocsBtn')).toBeVisible();
    await page.locator('#uploadDocsBtn').click();
    await expect(page.locator('#toast-container')).toContainText('Choose a file first.');

    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#db-status-text')).toContainText('Ready');
    await expect(page.locator('#view-dashboard')).toBeVisible();
    await page.locator('button.nav-item[data-view="records"]').click();
    await expect(page.locator('#view-records')).toBeVisible();
    await expect(page.locator('#invoicePdfImportFile')).toHaveAttribute('accept', /pdf/i);
    await expect(page.locator('#importDbFile')).toHaveAttribute('accept', /sqlite|db/i);

    await page.locator('#invoicePdfImportFile').evaluate(input => {
      input.dataset.clickedByVisibleButton = 'false';
      input.click = function markClicked() {
        this.dataset.clickedByVisibleButton = 'true';
      };
    });
    await page.locator('#btnImportPdfDraft').click();
    await expect(page.locator('#invoicePdfImportFile')).toHaveAttribute('data-clicked-by-visible-button', 'true');

    await page.locator('#importDbFile').evaluate(input => {
      input.dataset.clickedByVisibleButton = 'false';
      input.click = function markClicked() {
        this.dataset.clickedByVisibleButton = 'true';
      };
    });
    await page.getByRole('button', { name: /import db/i }).click();
    await expect(page.locator('#importDbFile')).toHaveAttribute('data-clicked-by-visible-button', 'true');

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
