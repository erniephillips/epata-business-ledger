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

async function showMainPage(page, pageId) {
  await page.evaluate(target => window.showPage(target), pageId);
  await expect(page.locator('#app')).not.toContainText('Loading...');
  await expect(page.locator('#app')).not.toContainText('Something broke');
}

function marketplaceDraft(orderNumber, customerName, customerPaid = 25) {
  return {
    receipt: {
      engine: 'Local rules',
      provider: 'Deterministic test fixture',
      reads: ['Pasted marketplace order text'],
      writes: ['Reviewable Sale draft'],
      useWhen: 'Testing paid order intake.',
      safety: 'Nothing is saved until confirmed.',
    },
    sale: {
      platform: 'Etsy',
      paymentMethod: 'Etsy Payments',
      orderNumber,
      saleDate: '2026-09-10',
      customerName,
      productName: `${customerName} Product`,
      quantity: 1,
      itemSales: customerPaid,
      shippingCharged: 0,
      salesTaxCollected: 0,
      customerPaid,
      platformFees: null,
      shippingLabelCost: null,
      estimatedCogs: null,
      status: 'Paid',
      needsReview: true,
    },
    customer: {
      name: customerName,
      defaultPlatform: 'Etsy',
    },
    job: {
      jobDate: '2026-09-10',
      customerName,
      platform: 'Etsy',
      relatedOrderNumber: orderNumber,
      jobName: `${customerName} Product`,
      productName: `${customerName} Product`,
      paymentMethod: 'Etsy Payments',
      invoiceAmount: customerPaid,
      amountPaid: customerPaid,
      status: 'Paid',
    },
    detectedOrderNumbers: [orderNumber],
    possibleDuplicateSales: [],
    possibleDuplicateJobs: [],
    possibleCustomerMatches: [],
    suggestedCreateJob: true,
    warnings: ['Marketplace fees still need review.'],
    questions: [],
  };
}

function estimateDraft(customerName, marker) {
  return {
    usedAi: false,
    provider: 'Local rules test fixture',
    sourceName: marker,
    executionReceipt: {
      engine: 'Local rules',
      provider: 'Deterministic test fixture',
      reads: ['Pasted estimate request'],
      writes: ['Unsaved estimate draft'],
      useWhen: 'Testing estimate intake.',
      safety: 'Nothing is saved automatically.',
    },
    prefill: {
      docType: 'ESTIMATE',
      customerName,
      preparedFor: customerName,
      customerEmail: 'estimate@example.test',
      projectName: `${marker} Project`,
      projectDescription: `${marker} Description`,
      material: 'PETG',
      color: 'Blue',
      infill: '25%',
      paymentMethod: 'Unknown / Review',
      subtotal: 80,
      total: 80,
      lineItems: [{ description: `${marker} Item`, details: 'Mapped from source', quantity: 2, rate: 40 }],
    },
    pricing: { lineSubtotal: 80 },
    questions: [],
    warnings: [],
  };
}

test.describe('Document Intake and AI Operations state machines', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'State-machine coverage runs once; responsive coverage lives in the navigation suite.');
  });

  test('an exact upload retry is explicit, keeps its context through Back, and clears only on request', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const existingDocument = {
      id: 98101,
      documentDate: '2026-09-10T00:00:00',
      documentType: 'Etsy Order',
      relatedRecordType: 'Sale',
      relatedRecordNumber: 'PW-EXACT-RETRY-001',
      fileName: 'playwright-exact-retry.txt',
      filePathOrUrl: 'UploadedDocs/playwright-exact-retry.txt',
      needsReview: false,
    };

    await page.route('**/api/documents/upload', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        count: 1,
        createdCount: 0,
        duplicateUploadCount: 1,
        documents: [existingDocument],
        duplicateUploads: [{
          auditDocumentId: existingDocument.id,
          fileName: existingDocument.fileName,
          relatedRecordType: existingDocument.relatedRecordType,
          relatedRecordNumber: existingDocument.relatedRecordNumber,
          message: 'This exact file upload was already saved. The existing proof was returned and no duplicate record or file was created.',
        }],
        suggestions: [{
          auditDocumentId: existingDocument.id,
          lane: 'Sale',
          confidence: 'High',
          title: 'This suggestion must be hidden for an exact retry.',
        }],
        marketplaceImports: [],
        invoiceDocumentImports: [],
      }),
    }));

    await page.goto('/');
    await showMainPage(page, 'documentIntake');
    await page.locator('#relatedTypeTrigger').click();
    await page.locator('[data-related-area="Sale"]').click();
    await page.locator('#relatedNumber').fill(existingDocument.relatedRecordNumber);
    await page.locator('#docFiles').setInputFiles({
      name: existingDocument.fileName,
      mimeType: 'text/plain',
      buffer: Buffer.from('same exact upload'),
    });
    await page.locator('#uploadDocsBtn').click();

    await expect(page.locator('#uploadResult')).toContainText('Already saved — nothing was duplicated');
    await expect(page.locator('#uploadResult')).toContainText('no duplicate record or file was created');
    await expect(page.locator('#uploadResult')).not.toContainText('This suggestion must be hidden');
    await expect(page.locator('#toast-container')).toContainText('safely reused 1 exact upload');

    await page.getByRole('button', { name: 'Open Existing Proof' }).click();
    await expect(page.locator('#app h2').first()).toContainText('Audit Docs / Proof Index');
    await page.locator('#appBackBtn').click();
    await expect(page.locator('#app h2').first()).toContainText('Document Intake');
    await expect(page.locator('#uploadResult')).toContainText('Already saved — nothing was duplicated');
    await expect(page.locator('#relatedType')).toHaveValue('Sale');
    await expect(page.locator('#relatedNumber')).toHaveValue(existingDocument.relatedRecordNumber);

    await page.locator('#clearDocumentIntakeResultsBtn').click();
    await expect(page.locator('#uploadResult')).toContainText('No upload yet.');
    await expect(page.locator('#clearDocumentIntakeResultsBtn')).toBeDisabled();
    expect(failures).toEqual([]);
  });

  test('a mapped invoice opens the saved populated record and one Back returns to every upload result', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const invoiceDocument = {
      id: 98401,
      docType: 'INVOICE',
      docNumber: 'INV-PW-INTAKE-98401',
      status: 'Sent',
      docDate: '2026-09-10',
      dueDate: '2026-09-24',
      customerName: 'Mapped Intake Customer',
      preparedFor: 'Pat Intake',
      customerEmail: 'pat@example.test',
      projectName: 'Mapped Intake Project',
      projectDescription: 'Every extracted field should reach the saved builder record.',
      material: 'ASA',
      color: 'Black',
      infill: '35%',
      paymentMethod: 'Unknown / Review',
      subtotal: 125,
      total: 125,
      amountPaid: 0,
      balance: 125,
      lineItems: [{ sortOrder: 1, description: 'Mapped bracket set', details: '12 pieces from the source PDF', quantity: 12, rate: 10.4166667, amount: 125 }],
    };
    const auditDocument = {
      id: 98402,
      documentDate: '2026-09-10T00:00:00',
      documentType: 'Invoice',
      relatedRecordType: 'Invoice',
      relatedRecordNumber: invoiceDocument.docNumber,
      fileName: 'playwright-mapped-invoice.pdf',
      filePathOrUrl: 'UploadedDocs/playwright-mapped-invoice.pdf',
      needsReview: false,
    };

    await page.route('**/api/documents/upload', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        count: 1,
        createdCount: 1,
        duplicateUploadCount: 0,
        documents: [auditDocument],
        duplicateUploads: [],
        suggestions: [],
        marketplaceImports: [],
        invoiceDocumentImports: [{
          action: 'Created',
          invoiceDocument,
          auditDocument,
          sourceDocumentNumber: invoiceDocument.docNumber,
          message: 'Created and linked the populated invoice record.',
          warnings: [],
        }],
      }),
    }));
    await page.route(`**/api/documents/${invoiceDocument.id}`, route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify(invoiceDocument),
    }));

    await page.goto('/');
    await showMainPage(page, 'documentIntake');
    await page.locator('#relatedTypeTrigger').click();
    await page.locator('[data-related-area="Invoice"]').click();
    await page.locator('#docFiles').setInputFiles({
      name: auditDocument.fileName,
      mimeType: 'application/pdf',
      buffer: Buffer.from('%PDF-1.4 mocked by browser route'),
    });
    await page.locator('#uploadDocsBtn').click();
    await expect(page.locator('#uploadResult')).toContainText('Mapped Intake Customer');
    await expect(page.locator('#uploadResult')).toContainText('Mapped Intake Project');

    await page.getByRole('button', { name: 'Open Invoice' }).click();
    await expect(page).toHaveURL(/#invoices$/);
    await expect(page.locator('#epataInvoiceMerged #docNumber')).toHaveValue(invoiceDocument.docNumber);
    await expect(page.locator('#epataInvoiceMerged #customerName')).toHaveValue(invoiceDocument.customerName);
    await expect(page.locator('#epataInvoiceMerged #projectName')).toHaveValue(invoiceDocument.projectName);
    await expect(page.locator('#epataInvoiceMerged .item-desc').first()).toHaveValue('Mapped bracket set');

    await page.locator('#appBackBtn').click();
    await expect(page.locator('#app h2').first()).toContainText('Document Intake');
    await expect(page.locator('#uploadResult')).toContainText(invoiceDocument.docNumber);
    await expect(page.locator('#uploadResult')).toContainText('Mapped Intake Customer');
    expect(failures).toEqual([]);
  });

  test('latest estimate intake wins and its source, draft, and builder prefill survive navigation', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let markFirstStarted;
    let releaseFirst;
    const firstStarted = new Promise(resolve => { markFirstStarted = resolve; });
    const firstGate = new Promise(resolve => { releaseFirst = resolve; });

    await page.route('**/api/ai/estimate-draft/upload', async route => {
      const body = route.request().postData() || '';
      if (body.includes('FIRST-ESTIMATE-RACE')) {
        markFirstStarted();
        await firstGate;
        try {
          await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(estimateDraft('First Slow Estimate Customer', 'FIRST-ESTIMATE-RACE')) });
        } catch {
          // A newer request intentionally cancels this stale one.
        }
        return;
      }
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(estimateDraft('Second Current Estimate Customer', 'SECOND-ESTIMATE-RACE')) });
    });

    await page.goto('/');
    await showMainPage(page, 'aiEstimate');
    await page.locator('#aiEstimateSource').fill('FIRST-ESTIMATE-RACE');
    await page.locator('#analyzeAiEstimateBtn').click();
    await firstStarted;
    await showMainPage(page, 'dashboard');
    await showMainPage(page, 'aiEstimate');
    await expect(page.locator('#aiEstimateSource')).toHaveValue('FIRST-ESTIMATE-RACE');
    await page.locator('#aiEstimateSource').fill('SECOND-ESTIMATE-RACE');
    await page.locator('#analyzeAiEstimateBtn').click();
    await expect(page.locator('#aiEstimateResult')).toContainText('Second Current Estimate Customer');
    releaseFirst();
    await page.waitForTimeout(150);
    await expect(page.locator('#aiEstimateResult')).not.toContainText('First Slow Estimate Customer');

    await page.locator('#openAiEstimateDraftBtn').click();
    await expect(page.locator('#epataInvoiceMerged #customerName')).toHaveValue('Second Current Estimate Customer');
    await expect(page.locator('#epataInvoiceMerged .item-desc').first()).toHaveValue('SECOND-ESTIMATE-RACE Item');
    await page.locator('#appBackBtn').click();
    await expect(page.locator('#app h2').first()).toContainText('Turn a customer conversation');
    await expect(page.locator('#aiEstimateSource')).toHaveValue('SECOND-ESTIMATE-RACE');
    await expect(page.locator('#aiEstimateResult')).toContainText('Second Current Estimate Customer');
    expect(failures).toEqual([]);
  });

  test('latest marketplace analysis wins after navigate-away/back and reviewed edits persist', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let markFirstStarted;
    let releaseFirst;
    const firstStarted = new Promise(resolve => { markFirstStarted = resolve; });
    const firstGate = new Promise(resolve => { releaseFirst = resolve; });

    await page.route('**/api/ai/operations/marketplace-order-import', async route => {
      if (new URL(route.request().url()).pathname.endsWith('/save')) return route.fallback();
      const body = route.request().postData() || '';
      if (body.includes('FIRST-RACE-ORDER')) {
        markFirstStarted();
        await firstGate;
        try {
          await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(marketplaceDraft('FIRST-RACE-ORDER', 'First Slow Buyer')) });
        } catch {
          // The superseding request intentionally aborts this response.
        }
        return;
      }
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(marketplaceDraft('SECOND-RACE-ORDER', 'Second Current Buyer', 42)) });
    });

    await page.goto('/');
    await showMainPage(page, 'aiOperations');
    await page.locator('#aiMarketplaceOrderText').fill('FIRST-RACE-ORDER');
    await page.locator('#runMarketplaceOrderImportBtn').click();
    await firstStarted;

    await showMainPage(page, 'dashboard');
    await showMainPage(page, 'aiOperations');
    await expect(page.locator('#aiMarketplaceOrderText')).toHaveValue('FIRST-RACE-ORDER');
    await page.locator('#aiMarketplaceOrderText').fill('SECOND-RACE-ORDER');
    await page.locator('#runMarketplaceOrderImportBtn').click();
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('SECOND-RACE-ORDER');
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('Second Current Buyer');

    releaseFirst();
    await page.waitForTimeout(150);
    await expect(page.locator('#aiMarketplaceOrderResult')).not.toContainText('FIRST-RACE-ORDER');

    await page.locator('#marketplace_customer_name').fill("Second O'Neil Buyer");
    await page.locator('#marketplace_customer_address1').fill('42 Persistent State Road');
    await page.locator('#marketplace_customerPaid').fill('47.25');
    await page.locator('#marketplaceCreateJob').uncheck();
    await showMainPage(page, 'dashboard');
    await showMainPage(page, 'aiOperations');

    await expect(page.locator('#aiMarketplaceOrderText')).toHaveValue('SECOND-RACE-ORDER');
    await expect(page.locator('#marketplace_customer_name')).toHaveValue("Second O'Neil Buyer");
    await expect(page.locator('#marketplace_customer_address1')).toHaveValue('42 Persistent State Road');
    await expect(page.locator('#marketplace_customerPaid')).toHaveValue('47.25');
    await expect(page.locator('#marketplaceCreateJob')).not.toBeChecked();
    expect(failures).toEqual([]);
  });

  test('an older marketplace save cannot clear a newer analyzed draft', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let analysisCount = 0;
    let markSaveStarted;
    let releaseSave;
    const saveStarted = new Promise(resolve => { markSaveStarted = resolve; });
    const saveGate = new Promise(resolve => { releaseSave = resolve; });

    await page.route('**/api/ai/operations/marketplace-order-import/save', async route => {
      markSaveStarted();
      await saveGate;
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          receipt: marketplaceDraft('SAVE-A', 'Saved Buyer').receipt,
          sale: { id: 98201, orderNumber: 'SAVE-A' },
          customer: { id: 98202, name: 'Saved Buyer' },
          customerSaveAction: 'Created',
          job: { id: 98203 },
          auditDocuments: [],
        }),
      });
    });
    await page.route('**/api/ai/operations/marketplace-order-import', async route => {
      analysisCount += 1;
      const draft = analysisCount === 1
        ? marketplaceDraft('SAVE-A', 'Save A Buyer', 30)
        : marketplaceDraft('SAVE-B', 'Save B Current Buyer', 55);
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(draft) });
    });

    page.on('dialog', dialog => dialog.accept());
    await page.goto('/');
    await showMainPage(page, 'aiOperations');
    await page.locator('#aiMarketplaceOrderText').fill('SAVE-A');
    await page.locator('#runMarketplaceOrderImportBtn').click();
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('SAVE-A');
    await page.locator('#saveMarketplaceOrderBtn').click();
    await saveStarted;

    await showMainPage(page, 'dashboard');
    await showMainPage(page, 'aiOperations');
    await page.locator('#aiMarketplaceOrderText').fill('SAVE-B');
    await page.locator('#runMarketplaceOrderImportBtn').click();
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('SAVE-B');
    releaseSave();
    await page.waitForTimeout(150);

    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('SAVE-B');
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('Save B Current Buyer');
    await expect(page.locator('#aiMarketplaceOrderResult')).not.toContainText('Paid marketplace order saved');
    expect(failures).toEqual([]);
  });

  test('a completed marketplace save and all AI source fields survive ordinary navigation', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let saveBody = '';
    let directSalePosts = 0;
    page.on('request', request => {
      if (new URL(request.url()).pathname === '/api/sales' && request.method() === 'POST') directSalePosts += 1;
    });
    await page.route('**/api/ai/operations/marketplace-order-import/save', async route => {
      saveBody = route.request().postData() || '';
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          receipt: marketplaceDraft('SAVE-PERSIST-001', 'Persistent Buyer').receipt,
          sale: { id: 98301, orderNumber: 'SAVE-PERSIST-001', customerName: 'Persistent Buyer', productName: 'Persistent Product' },
          customer: { id: 98302, name: 'Persistent Buyer' },
          customerSaveAction: 'Created',
          job: null,
          auditDocuments: [{ id: 98303 }],
        }),
      });
    });
    await page.route('**/api/ai/operations/marketplace-order-import', async route => {
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(marketplaceDraft('SAVE-PERSIST-001', 'Persistent Buyer', 70)) });
    });
    await page.route('**/api/sales*', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify([{ id: 98301, saleDate: '2026-09-10', platform: 'Etsy', paymentMethod: 'Etsy Payments', orderNumber: 'SAVE-PERSIST-001', customerName: 'Persistent Buyer', productName: 'Persistent Product', quantity: 1, customerPaid: 77.25, status: 'Paid' }]),
    }));

    page.on('dialog', dialog => dialog.accept());
    await page.goto('/');
    await showMainPage(page, 'aiOperations');
    await page.locator('#aiMarketplaceOrderText').fill('SAVE-PERSIST-001');
    await page.locator('#aiProductUrls').fill('https://example.com/persistent-product');
    await page.locator('#aiProductText').fill('Persistent product notes');
    await page.locator('#aiJobText').fill('Persistent job-plan notes');
    await page.locator('#aiSlicerText').fill('Persistent slicer notes');
    await page.locator('#aiListingPlatform').selectOption('Etsy');
    await page.locator('#aiListingInstructions').fill('Persistent listing instructions');
    await page.locator('#aiLedgerQuestion').fill('Persistent ledger question');
    await page.locator('#runMarketplaceOrderImportBtn').click();
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('SAVE-PERSIST-001');
    await page.locator('#openMarketplaceSaleDraftBtn').click();
    await expect(page.locator('#modalTitle')).toHaveText('Edit Reviewed Marketplace Sale Draft');
    await expect(page.locator('#modalSave')).toHaveText('Apply to Order Review');
    await page.locator('#field_sku').fill('PW-PERSIST-SKU');
    await page.locator('#field_variation').fill('Set of 3');
    await page.locator('#modalSave').click();
    await expect(page.locator('#modal')).toBeHidden();
    await expect(page.locator('#toast-container')).toContainText('Nothing has been saved yet');
    expect(directSalePosts).toBe(0);
    await page.locator('#marketplace_customerPaid').fill('77.25');
    await page.locator('#marketplaceCreateJob').uncheck();
    await page.locator('#marketplace_orderNumber').fill('');
    await page.locator('#saveMarketplaceOrderBtn').click();
    await expect(page.locator('#toast-container')).toContainText('Order Number is required');
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('Persistent Buyer');
    expect(saveBody).toBe('');
    await page.locator('#marketplace_orderNumber').fill('SAVE-PERSIST-001');
    await page.locator('#saveMarketplaceOrderBtn').click();
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('Paid marketplace order saved');
    expect(saveBody).toContain('77.25');
    expect(saveBody).toContain('PW-PERSIST-SKU');
    expect(saveBody).toContain('Set of 3');
    expect(saveBody).toContain('createJob');
    expect(saveBody).toContain('false');

    await showMainPage(page, 'dashboard');
    await page.locator('#appBackBtn').click();
    await expect(page.locator('#app h2').first()).toContainText('Use AI for the repetitive work');
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('Paid marketplace order saved');
    await expect(page.locator('#aiProductUrls')).toHaveValue('https://example.com/persistent-product');
    await expect(page.locator('#aiProductText')).toHaveValue('Persistent product notes');
    await expect(page.locator('#aiJobText')).toHaveValue('Persistent job-plan notes');
    await expect(page.locator('#aiSlicerText')).toHaveValue('Persistent slicer notes');
    await expect(page.locator('#aiListingPlatform')).toHaveValue('Etsy');
    await expect(page.locator('#aiListingInstructions')).toHaveValue('Persistent listing instructions');
    await expect(page.locator('#aiLedgerQuestion')).toHaveValue('Persistent ledger question');

    await page.locator('#aiMarketplaceOrderResult').getByRole('button', { name: 'Open Sale' }).click();
    await expect(page.locator('#modal')).toBeVisible();
    await expect(page.locator('#field_orderNumber')).toHaveValue('SAVE-PERSIST-001');
    await page.locator('#modalClose').click();
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('Paid marketplace order saved');
    expect(failures).toEqual([]);
  });
});
