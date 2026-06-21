const fs = require('node:fs');
const path = require('node:path');
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

async function freezeVisualNoise(page) {
  await page.addStyleTag({
    content: `
      *, *::before, *::after {
        animation-duration: 0s !important;
        animation-delay: 0s !important;
        transition-duration: 0s !important;
        transition-delay: 0s !important;
        caret-color: transparent !important;
      }
    `,
  });
}

async function expectReady(page) {
  await expect(page.locator('body')).not.toContainText('Something broke');
  await expect(page.locator('body')).not.toContainText('Loading...');
}

async function captureOrCompare(page, testInfo, snapshotName) {
  const screenshot = await page.screenshot({
    animations: 'disabled',
    fullPage: true,
  });
  const snapshotPath = testInfo.snapshotPath(snapshotName);

  if (!fs.existsSync(snapshotPath) || process.env.EPATA_UPDATE_VISUAL_BASELINES === '1') {
    fs.mkdirSync(path.dirname(snapshotPath), { recursive: true });
    fs.writeFileSync(snapshotPath, screenshot);
    await testInfo.attach(snapshotName, { body: screenshot, contentType: 'image/png' });
    return;
  }

  expect(screenshot).toMatchSnapshot(snapshotName, {
    maxDiffPixelRatio: 0.02,
    threshold: 0.2,
  });
}

async function openMainPage(page, pageId) {
  await page.goto('/');
  await freezeVisualNoise(page);
  await expect.poll(() => page.evaluate(() => typeof window.showPage)).toBe('function');
  await page.evaluate(targetPage => window.showPage(targetPage), pageId);
  await expectReady(page);
}

async function openInvoiceBuilderView(page, viewId) {
  await page.goto('/invoice-builder/index.html');
  await freezeVisualNoise(page);
  await expect(page.locator('#db-status-text')).toContainText('Ready');
  await expect.poll(() => page.evaluate(() => typeof window._invoiceToolShowView)).toBe('function');
  await page.evaluate(targetView => window._invoiceToolShowView(targetView), viewId);
  await expect(page.locator(`#view-${viewId}`)).toBeVisible();
}

test.describe('visual regression screenshot baselines', () => {
  const viewports = [
    { name: 'desktop', width: 1440, height: 900 },
    { name: 'laptop', width: 1280, height: 720 },
    { name: 'mobile', width: 390, height: 844 },
  ];

  for (const viewport of viewports) {
    for (const target of [
      { name: 'dashboard', open: page => openMainPage(page, 'dashboard') },
      { name: 'document-intake', open: page => openMainPage(page, 'documentIntake') },
      { name: 'tax-prep', open: page => openMainPage(page, 'taxPrep') },
      { name: 'ai-estimate', open: page => openMainPage(page, 'aiEstimate') },
      { name: 'ai-operations', open: page => openMainPage(page, 'aiOperations') },
      { name: 'ai-review', open: page => openMainPage(page, 'aiReview') },
      { name: 'local-ai', open: page => openMainPage(page, 'localAi') },
      { name: 'invoice-builder', open: page => openInvoiceBuilderView(page, 'builder') },
      { name: 'invoice-records', open: page => openInvoiceBuilderView(page, 'records') },
    ]) {
      test(`${target.name} visual baseline at ${viewport.name}`, async ({ page }, testInfo) => {
        const failures = collectBrowserFailures(page);
        await page.setViewportSize({ width: viewport.width, height: viewport.height });
        await target.open(page);
        await captureOrCompare(page, testInfo, `${target.name}-${viewport.name}.png`);
        expect(failures).toEqual([]);
      });
    }
  }

  for (const colorScheme of ['light', 'dark']) {
    for (const target of [
      { name: 'dashboard', open: page => openMainPage(page, 'dashboard') },
      { name: 'tax-prep', open: page => openMainPage(page, 'taxPrep') },
      { name: 'invoice-builder', open: page => openInvoiceBuilderView(page, 'builder') },
      { name: 'invoice-records', open: page => openInvoiceBuilderView(page, 'records') },
    ]) {
      test(`${target.name} ${colorScheme} preference visual baseline`, async ({ page }, testInfo) => {
        const failures = collectBrowserFailures(page);
        await page.setViewportSize({ width: 1440, height: 900 });
        await page.emulateMedia({ colorScheme });
        await target.open(page);
        await captureOrCompare(page, testInfo, `${target.name}-${colorScheme}-preference.png`);
        expect(failures).toEqual([]);
      });
    }
  }
});
