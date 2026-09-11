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

async function openQuickAdd(page) {
  await page.evaluate(() => window.showPage('quickAdd'));
  await waitForApp(page);
  await expect(page.locator('button.nav-button[data-page="quickAdd"]')).toHaveClass(/active/);
}

function quickAddChoice(page, name) {
  return page.locator('.quick-card').filter({
    has: page.locator('strong').filter({ hasText: name }),
  }).first();
}

test.describe('high-risk rendered control gaps', () => {
  test.describe.configure({ mode: 'serial' });

  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'Stateful control-gap coverage runs once against the disposable browser ledger.');
  });

  test('every Quick Add choice reaches the promised destination and Cancel remains non-mutating', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await page.goto('/');
    await waitForApp(page);
    await openQuickAdd(page);

    const modalChoices = [
      ['Estimate Sent (External)', '#field_jobType', 'Estimate', '#field_status', 'Quoted'],
      ['Etsy Sale', '#field_platform', 'Etsy', '#field_paymentMethod', 'Etsy Payments'],
      ['Direct Paid Sale', '#field_platform', 'Direct', '#field_status', 'Paid'],
      ['Open Invoice / AR', '#field_status', 'Sent', '#field_paymentMethod', 'Unknown / Review'],
      ['Bill / AP', '#field_status', 'Unpaid', '#field_paymentMethod', 'Unknown / Review'],
      ['Paid Expense', '#field_taxBucket', 'Review', '#field_businessUsePercent', '100'],
      ['Damaged / Lost Order', '#field_status', 'Resolved', '#field_resolution', 'Replacement / reship'],
      ['Equipment / Asset Purchase', '#field_taxTreatment', 'Review', '#field_businessUsePercent', '100'],
      ['Customer / Vendor Contact', '#field_partyType', 'Customer', '#field_defaultPlatform', 'Direct'],
      ['Product / Costing Row', '#field_category', '3D Printed Product', '#field_material', 'PLA'],
      ['Action Item', '#field_area', 'General', '#field_status', 'Open'],
      ['Audit Doc / Proof Index', '#field_documentType', 'Other', '#field_needsReview', true],
      ['Log Customer Communication', '#field_direction', 'Outgoing', '#field_channel', 'Email'],
    ];

    const countRoutes = [
      'customer-jobs', 'sales', 'receivable-invoices', 'bills', 'expenses',
      'order-loss-incidents', 'assets', 'parties', 'products', 'action-items',
      'audit-documents', 'customer-communications',
    ];
    const beforeCounts = await page.evaluate(async routes => {
      const pairs = await Promise.all(routes.map(async route => [route, (await fetch(`/api/${route}`).then(r => r.json())).length]));
      return Object.fromEntries(pairs);
    }, countRoutes);

    for (const [name, firstSelector, firstValue, secondSelector, secondValue] of modalChoices) {

      await quickAddChoice(page, name).click();
      await expect(page.locator('#modal')).toBeVisible();
      if (typeof firstValue === 'boolean') await expect(page.locator(firstSelector)).toBeChecked({ checked: firstValue });
      else await expect(page.locator(firstSelector)).toHaveValue(firstValue);
      if (typeof secondValue === 'boolean') await expect(page.locator(secondSelector)).toBeChecked({ checked: secondValue });
      else await expect(page.locator(secondSelector)).toHaveValue(secondValue);
      await page.locator('#modalCancel').click();
      await expect(page.locator('#modal')).toBeHidden();
      await expect(page.locator('button.nav-button[data-page="quickAdd"]')).toHaveClass(/active/);

    }

    const afterCounts = await page.evaluate(async routes => {
      const pairs = await Promise.all(routes.map(async route => [route, (await fetch(`/api/${route}`).then(r => r.json())).length]));
      return Object.fromEntries(pairs);
    }, countRoutes);
    expect(afterCounts, 'Canceling every Quick Add modal must not create any ledger row').toEqual(beforeCounts);

    const pageChoices = [
      ['New Estimate PDF', 'estimates', '#view-builder'],
      ['New Invoice PDF', 'invoices', '#view-builder'],
      ['AI Upload Paid Marketplace Order', 'aiOperations', '#aiMarketplaceOrderCard'],
      ['Open Printer Queue', 'printerQueue', '#printerQueueJobSelect'],
    ];
    for (const [name, destination, surface] of pageChoices) {
      await quickAddChoice(page, name).click();
      await waitForApp(page);
      await expect(page.locator(`button.nav-button[data-page="${destination}"]`)).toHaveClass(/active/);
      await expect(page.locator(surface)).toBeVisible();
      if (destination === 'estimates') await expect(page.locator('#docType')).toHaveValue('ESTIMATE');
      if (destination === 'invoices') await expect(page.locator('#docType')).toHaveValue('INVOICE');
      await page.locator('#appBackBtn').click();
      await waitForApp(page);
      await expect(page.locator('button.nav-button[data-page="quickAdd"]')).toHaveClass(/active/);
    }

    await page.getByRole('button', { name: 'Workflow Guide', exact: true }).click();
    await waitForApp(page);
    await expect(page.locator('button.nav-button[data-page="workflowGuide"]')).toHaveClass(/active/);
    await page.locator('#appBackBtn').click();
    await waitForApp(page);
    await expect(page.locator('button.nav-button[data-page="quickAdd"]')).toHaveClass(/active/);
    expect(failures).toEqual([]);
  });

  test('Job Timeline filter, sort, expand/collapse, event open, and Back continue from the same state', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const suffix = Date.now();
    const customerName = `Timeline O'Neil ${suffix}`;
    const jobName = `Timeline continuity ${suffix}`;
    let jobId = 0;

    try {
      const response = await page.request.post('/api/customer-jobs', {
        data: {
          jobDate: '2026-09-10', customerName, jobNumber: `TL-${suffix}`,
          jobName, jobType: 'Print', status: 'Open', productName: 'Timeline fixture',
          platform: 'Direct', needsReview: false,
        },
      });
      expect(response.ok(), await response.text()).toBe(true);
      jobId = (await response.json()).id;

      await page.goto('/');
      await waitForApp(page);
      await page.evaluate(() => window.showPage('jobTimeline'));
      await waitForApp(page);

      const filteredTimelineResponse = page.waitForResponse(response => {
        const url = new URL(response.url());
        return response.request().method() === 'GET'
          && url.pathname === '/api/job-timeline'
          && url.searchParams.get('q') === jobName
          && response.ok();
      });
      await page.locator('#timelineSearch').fill(jobName);
      await filteredTimelineResponse;
      await expect(page.locator('.timeline-card')).toHaveCount(1);
      await expect(page.locator('.timeline-card')).toContainText(customerName);

      await page.locator('#timelineCollapseAll').click();
      await expect(page.locator('.timeline-card')).not.toHaveAttribute('open', '');
      await page.locator('#timelineExpandAll').click();
      await expect(page.locator('.timeline-card')).toHaveAttribute('open', '');

      await page.locator('#timelineSort').selectOption('customerAsc');
      await waitForApp(page);
      await expect(page.locator('#timelineSearch')).toHaveValue(jobName);
      await expect(page.locator('#timelineSort')).toHaveValue('customerAsc');
      await page.locator('#timelineCustomer').selectOption(customerName);
      await waitForApp(page);
      await expect(page.locator('#timelineCustomer')).toHaveValue(customerName);
      await expect(page.locator('.timeline-card')).toHaveCount(1);

      const jobEvent = page.locator(`.timeline-event[data-timeline-id="${jobId}"]`).first();
      await expect(jobEvent).toBeVisible();
      await jobEvent.click();
      await expect(page.locator('#modal')).toBeVisible();
      await expect(page.locator('#field_customerName')).toHaveValue(customerName);
      await page.locator('#modalCancel').click();
      await expect(page.locator('#timelineSearch')).toHaveValue(jobName);

      await page.getByRole('button', { name: 'Upload Proof', exact: true }).click();
      await waitForApp(page);
      await page.locator('#appBackBtn').click();
      await waitForApp(page);
      await expect(page.locator('#timelineSearch')).toHaveValue(jobName);
      await expect(page.locator('#timelineCustomer')).toHaveValue(customerName);
      await expect(page.locator('#timelineSort')).toHaveValue('customerAsc');

      await page.locator('#timelineClear').click();
      await waitForApp(page);
      await expect(page.locator('#timelineSearch')).toHaveValue('');
      await expect(page.locator('#timelineCustomer')).toHaveValue('');
      expect(failures).toEqual([]);
    } finally {
      if (jobId) await page.request.delete(`/api/customer-jobs/${jobId}`).catch(() => {});
    }
  });

  test('Local AI power controls serialize repeated clicks without launching a real local process', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let statusCalls = 0;
    let settingsCalls = 0;
    let startCalls = 0;
    let stopCalls = 0;
    let modelReady = false;
    let lastSettings = null;

    const statusBody = () => ({
      state: modelReady ? 'Ready' : 'Stopped',
      message: modelReady ? 'Mock model ready.' : 'Mock model stopped.',
      safety: 'Loopback-only test response.',
      lmsInstalled: true,
      lmsPath: 'C:\\Mock\\lms.exe',
      serverOnline: modelReady,
      modelReady,
      baseUrl: 'http://127.0.0.1:1234/v1',
      modelIdentifier: lastSettings?.modelIdentifier || 'epata-local-test',
      contextLength: lastSettings?.contextLength || 8192,
      idleUnloadSeconds: lastSettings?.idleUnloadSeconds || 1800,
      availableModels: [{ name: 'mock-model.gguf', fullPath: 'C:\\Mock\\mock-model.gguf', bytes: 1048576 }],
      selectedModelPath: 'C:\\Mock\\mock-model.gguf',
      loadedModels: modelReady ? ['epata-local-test'] : [],
    });

    await page.route('**/api/ai/local/status', async route => {
      statusCalls += 1;
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(statusBody()) });
    });
    await page.route('**/api/ai/local/settings', async route => {
      settingsCalls += 1;
      lastSettings = route.request().postDataJSON();
      await new Promise(resolve => setTimeout(resolve, 120));
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ ...statusBody(), ...lastSettings }) });
    });
    await page.route('**/api/ai/local/start', async route => {
      startCalls += 1;
      await new Promise(resolve => setTimeout(resolve, 120));
      modelReady = true;
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ success: true, message: 'Mock Local AI started.', status: statusBody() }) });
    });
    await page.route('**/api/ai/local/stop', async route => {
      stopCalls += 1;
      await new Promise(resolve => setTimeout(resolve, 120));
      modelReady = false;
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({ success: true, message: 'Mock Local AI stopped.', status: statusBody() }) });
    });

    await page.goto('/');
    await waitForApp(page);
    await page.evaluate(() => window.showPage('localAi'));
    await waitForApp(page);
    await expect(page.locator('#localAiStateBadge')).toHaveText('Stopped');

    await page.locator('#localAiIdentifier').fill('gap-audit-model');
    await page.locator('#localAiContextLength').fill('16384');
    await page.locator('#localAiIdleSeconds').fill('3600');
    await page.evaluate(() => {
      document.querySelector('#saveLocalAiSettingsBtn').click();
      document.querySelector('#saveLocalAiSettingsBtn').click();
    });
    await expect.poll(() => settingsCalls).toBe(1);
    await expect.poll(() => lastSettings?.modelIdentifier).toBe('gap-audit-model');

    await page.evaluate(() => {
      document.querySelector('#startLocalAiBtn').click();
      document.querySelector('#startLocalAiBtn').click();
    });
    await expect.poll(() => startCalls).toBe(1);
    await expect(page.locator('#localAiStateBadge')).toHaveText('Ready');

    await page.evaluate(() => {
      document.querySelector('#stopLocalAiBtn').click();
      document.querySelector('#stopLocalAiBtn').click();
    });
    await expect.poll(() => stopCalls).toBe(1);
    await expect(page.locator('#localAiStateBadge')).toHaveText('Stopped');

    const callsBeforeRefresh = statusCalls;
    await page.locator('#refreshLocalAiBtn').click();
    await expect.poll(() => statusCalls).toBeGreaterThan(callsBeforeRefresh);
    await expect(page.locator('#localAiStateBadge')).toHaveText('Stopped');
    expect(settingsCalls).toBe(2); // explicit Save + the settings save performed before Start
    expect(failures).toEqual([]);
  });

  test('Tax Setup save is single-flight and cannot pull the user back after navigation', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const originalResponse = await page.request.get('/api/tax-profile');
    expect(originalResponse.ok(), await originalResponse.text()).toBe(true);
    const original = await originalResponse.json();
    const note = `Tax navigation checkpoint ${Date.now()}`;
    let putCount = 0;

    await page.route('**/api/tax-profile', async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      putCount += 1;
      await new Promise(resolve => setTimeout(resolve, 350));
      await route.continue();
    });

    try {
      await page.goto('/');
      await waitForApp(page);
      await page.evaluate(() => window.showPage('taxPrep'));
      await waitForApp(page);
      await page.locator('#taxProfileNotes').fill(note);
      await page.locator('#taxUsesVehicle').selectOption('Yes');
      await page.evaluate(() => {
        const button = [...document.querySelectorAll('button')].find(node => node.textContent.trim() === 'Save Tax Setup');
        button.click();
        button.click();
      });
      await expect.poll(() => putCount).toBe(1);

      await page.evaluate(() => window.showPage('actions'));
      await waitForApp(page);
      await expect(page.locator('button.nav-button[data-page="actions"]')).toHaveClass(/active/);
      await page.waitForTimeout(500);
      await expect(page.locator('button.nav-button[data-page="actions"]')).toHaveClass(/active/);

      const saved = await page.request.get('/api/tax-profile').then(response => response.json());
      expect(saved.notes).toBe(note);
      expect(saved.usesVehicle).toBe('Yes');

      await page.locator('#appBackBtn').click();
      await waitForApp(page);
      await expect(page.locator('#taxProfileNotes')).toHaveValue(note);
      expect(failures).toEqual([]);
    } finally {
      await page.unroute('**/api/tax-profile');
      await page.request.put('/api/tax-profile', { data: original });
    }
  });

  test('Invoice AI Intake controls fill only an unsaved draft and a closed stale request cannot replace newer work', async ({ page }) => {
    test.setTimeout(30_000);
    const failures = collectBrowserFailures(page);
    let statusCalls = 0;
    let chatCalls = 0;
    let draftCalls = 0;
    let documentCreates = 0;
    page.on('dialog', dialog => dialog.accept());
    page.on('request', request => {
      if (request.method() === 'POST' && new URL(request.url()).pathname === '/api/documents') documentCreates += 1;
    });

    const readyStatus = {
      state: 'Ready', message: 'Mock model ready.', safety: 'Loopback-only test response.',
      lmsInstalled: true, serverOnline: true, modelReady: true,
      baseUrl: 'http://127.0.0.1:1234/v1', modelIdentifier: 'mock-ai-intake',
      contextLength: 8192, loadedModels: ['mock-ai-intake'],
    };
    await page.route('**/api/ai/local/status', async route => {
      statusCalls += 1;
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(readyStatus) });
    });
    await page.route('**/api/ai/estimate/status', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ limits: { maxFiles: 25, maxFileMegabytes: 20, maxTotalUploadMegabytes: 75, maxCombinedTextCharacters: 500000 } }),
    }));
    await page.route('**/api/ai/estimate-chat/upload', async route => {
      chatCalls += 1;
      await new Promise(resolve => setTimeout(resolve, 40));
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ answer: chatCalls === 1 ? 'Ask for the delivery deadline and final color.' : 'The material and dimensions still need confirmation.', warnings: [] }),
      });
    });
    await page.route('**/api/ai/estimate-draft/upload', async route => {
      draftCalls += 1;
      const requestNumber = draftCalls;
      await new Promise(resolve => setTimeout(resolve, requestNumber === 1 ? 60 : 450));
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({
          usedAi: true,
          provider: 'Mock local model',
          prefill: {
            docType: 'INVOICE', status: 'Draft',
            customerName: requestNumber === 1 ? "AI O'Neil Customer" : 'STALE AI CUSTOMER',
            projectName: requestNumber === 1 ? 'AI mapped bracket set' : 'STALE AI PROJECT',
            material: 'PETG', color: 'Blue', infill: '20%', paymentMethod: 'Unknown / Review',
            lineItems: [{ description: 'Mapped bracket pair', quantity: 2, rate: 12.5 }],
          },
          pricing: { total: 25 }, warnings: [], questions: [],
        }),
      });
    });

    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#db-status-text')).toContainText('Ready');
    await page.locator('button.nav-item[data-view="builder"]').click();
    await expect(page.locator('#view-builder')).toBeVisible();
    const openButton = page.locator('[data-ai-assistant-open]:visible').first();
    await openButton.click();
    await expect(page.locator('#aiAssistantModal')).toBeVisible();
    await expect(page.locator('#aiAssistantStatePill')).toHaveText('Local AI ready');

    const callsBeforeRefresh = statusCalls;
    await page.locator('#btnAiAssistantRefresh').click();
    await expect.poll(() => statusCalls).toBeGreaterThan(callsBeforeRefresh);

    await page.locator('#aiAssistantFiles').setInputFiles({
      name: 'customer-note.txt', mimeType: 'text/plain', buffer: Buffer.from('Need two blue PETG brackets by Friday.'),
    });
    await expect(page.locator('#aiAssistantFileList')).toContainText('customer-note.txt');
    await page.getByRole('button', { name: 'Remove customer-note.txt' }).click();
    await expect(page.locator('#aiAssistantFileList')).toHaveText('No files selected.');

    await page.locator('#aiAssistantContext').fill('Need two blue PETG brackets by Friday. Quote 25 dollars total.');
    await page.locator('#aiAssistantQuestion').fill('What should I confirm?');
    await page.locator('#btnAiAssistantAsk').click();
    await expect(page.locator('#aiAssistantChatLog')).toContainText('Ask for the delivery deadline and final color.');
    await page.locator('#btnAiAssistantMissing').click();
    await expect(page.locator('#aiAssistantChatLog')).toContainText('The material and dimensions still need confirmation.');

    await page.getByRole('button', { name: 'Close AI intake modal' }).click();
    await expect(page.locator('#aiAssistantModal')).toBeHidden();
    await openButton.click();
    await expect(page.locator('#aiAssistantContext')).toHaveValue(/Need two blue PETG brackets/);
    await page.locator('#btnAiAssistantClear').click();
    await expect(page.locator('#aiAssistantContext')).toHaveValue('');

    await page.locator('#aiAssistantContext').fill('Create the same bracket request as an invoice draft.');
    await page.locator('#aiAssistantDocType').selectOption('INVOICE');
    await page.locator('#btnAiAssistantGenerate').click();
    await expect(page.locator('#aiAssistantModal')).toBeHidden();
    await expect(page.locator('#view-builder')).toBeVisible();
    await expect(page.locator('#docType')).toHaveValue('INVOICE');
    await expect(page.locator('#customerName')).toHaveValue("AI O'Neil Customer");
    await expect(page.locator('#projectName')).toHaveValue('AI mapped bracket set');
    await expect(page.locator('#lineItemsBody')).toContainText('Mapped bracket pair');
    expect(documentCreates, 'AI Intake must not save a document without an explicit Save').toBe(0);

    await page.locator('[data-ai-assistant-open]:visible').first().click();
    await page.locator('#aiAssistantContext').fill('This deliberately slow draft must become stale.');
    await page.locator('#btnAiAssistantGenerate').click();
    await page.getByRole('button', { name: 'Close AI intake modal' }).click();
    await page.keyboard.press('Control+Shift+N');
    await expect(page.locator('#docType')).toHaveValue('ESTIMATE');
    await page.locator('#customerName').fill('Newer manual work wins');
    await expect.poll(() => draftCalls).toBe(2);
    await page.waitForTimeout(550);
    await expect(page.locator('#customerName')).toHaveValue('Newer manual work wins');
    await expect(page.locator('#projectName')).not.toHaveValue('STALE AI PROJECT');
    expect(documentCreates).toBe(0);
    expect(failures).toEqual([]);
  });

  test('a newer Invoice Settings save wins when an older save finishes late', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let persisted = {
      businessName: 'Gap Audit Original', businessLocation: '', businessEmail: '', businessPhone: '',
      businessWebsite: '', businessEtsy: '', businessInstagram: '', businessFacebook: '', brandColor: '#17468f',
      calcGramRate: 0.05, calcHourRate: 3, calcDesignRate: 25,
      calcSetupFee: 0, calcPostFee: 0, calcMinimum: 15,
    };
    let putCount = 0;

    await page.route('**/api/config', async route => {
      if (route.request().method() === 'GET') {
        await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(persisted) });
        return;
      }
      putCount += 1;
      const thisRequest = putCount;
      const body = route.request().postDataJSON();
      await new Promise(resolve => setTimeout(resolve, thisRequest === 1 ? 350 : 40));
      persisted = { ...persisted, ...body };
      await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify(persisted) });
    });

    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#db-status-text')).toContainText('Ready');
    await page.locator('button.nav-item[data-view="settings"]').click();
    await page.locator('#businessName').fill('Gap Audit First Save');
    await page.locator('#btnSaveSettings').click();
    await page.locator('#businessName').fill('Gap Audit Final Save');
    await page.locator('#btnSaveSettings').click();

    await expect.poll(() => putCount).toBe(2);
    await expect.poll(() => persisted.businessName, { timeout: 5000 }).toBe('Gap Audit Final Save');
    await page.reload();
    await expect(page.locator('#db-status-text')).toContainText('Ready');
    await page.locator('button.nav-item[data-view="settings"]').click();
    await expect(page.locator('#businessName')).toHaveValue('Gap Audit Final Save');
    expect(failures).toEqual([]);
  });

  test('generic and relationship pagers preserve page, size, and filters across navigation', async ({ page }) => {
    test.setTimeout(60_000);
    const failures = collectBrowserFailures(page);
    const suffix = Date.now();
    const fixturePrefix = `PW Pager ${suffix}`;
    const firstCustomer = `${fixturePrefix} Customer 00`;
    const createdSaleIds = [];

    try {
      for (let index = 0; index < 38; index += 1) {
        const customerIndex = index < 27 ? index : 0;
        const response = await page.request.post('/api/sales', {
          data: {
            saleDate: '2026-09-10',
            platform: 'Direct',
            orderNumber: `PW-PAGER-${suffix}-${String(index).padStart(2, '0')}`,
            customerName: `${fixturePrefix} Customer ${String(customerIndex).padStart(2, '0')}`,
            productName: `Pager fixture ${String(index).padStart(2, '0')}`,
            itemSales: 1,
            customerPaid: index % 2 === 0 ? 1 : 0,
            status: index % 2 === 0 ? 'Paid' : 'Open',
            includeInDashboard: false,
          },
        });
        expect(response.ok(), await response.text()).toBe(true);
        createdSaleIds.push((await response.json()).id);
      }

      await page.goto('/');
      await waitForApp(page);

      await test.step('generic table pager and filters remain stable', async () => {
        await page.evaluate(() => window.showPage('sales'));
        await waitForApp(page);
        await page.locator('#searchBox').fill(fixturePrefix);
        await expect(page.locator('#tableArea .pager-info')).toContainText('Showing 1-25 of 38 rows');
        await expect(page.locator('#tableArea tbody tr')).toHaveCount(25);

        await page.locator('#pgLast').click();
        await expect(page.locator('#tableArea .pager-info')).toContainText('Showing 26-38 of 38 rows');
        await page.locator('#pgFirst').click();
        await expect(page.locator('#tableArea .pager-info')).toContainText('Page 1 of 2');
        await page.locator('#pgNext').click();
        await expect(page.locator('#tableArea .pager-info')).toContainText('Page 2 of 2');
        await page.locator('#pgPrev').click();
        await expect(page.locator('#tableArea .pager-info')).toContainText('Page 1 of 2');

        await page.locator('#pgSize').selectOption('50');
        await expect(page.locator('#tableArea .pager-info')).toContainText('Showing 1-38 of 38 rows');
        await page.locator('#statusFilter').selectOption('paid');
        await expect(page.locator('#tableArea .pager-info')).toContainText('Showing 1-19 of 19 rows');
        await expect(page.locator('#tableArea tbody tr')).toHaveCount(19);
        await expect(page.locator('#tableArea tbody tr').filter({ hasNotText: 'Paid' })).toHaveCount(0);
        await page.locator('#statusFilter').selectOption('open');
        await expect(page.locator('#tableArea .pager-info')).toContainText('Showing 1-19 of 19 rows');
        await expect(page.locator('#tableArea tbody tr').filter({ hasNotText: 'Open' })).toHaveCount(0);

        await page.locator('#statusFilter').selectOption('');
        await page.locator('#pgSize').selectOption('25');
        await page.locator('#pgNext').click();
        await page.evaluate(() => window.showPage('actions'));
        await waitForApp(page);
        await page.locator('#appBackBtn').click();
        await waitForApp(page);
        await expect(page.locator('#searchBox')).toHaveValue(fixturePrefix);
        await expect(page.locator('#statusFilter')).toHaveValue('');
        await expect(page.locator('#pgSize')).toHaveValue('25');
        await expect(page.locator('#tableArea .pager-info')).toContainText('Page 2 of 2');
      });

      await test.step('relationship directory and detail-section pagers remain stable', async () => {
        await page.evaluate(() => window.showPage('customers'));
        await waitForApp(page);
        await page.locator('#relationshipSearch').fill(fixturePrefix);
        await expect(page.locator('#relationshipTable .pager-info')).toContainText('Showing 1-25 of 27 rows');
        await page.locator('#relationshipTable [data-rel-page="last"]').click();
        await expect(page.locator('#relationshipTable .pager-info')).toContainText('Showing 26-27 of 27 rows');
        await page.locator('#relationshipTable [data-rel-page="first"]').click();
        await page.locator('#relationshipTable [data-rel-page="next"]').click();
        await expect(page.locator('#relationshipTable .pager-info')).toContainText('Page 2 of 2');
        await page.locator('#relationshipTable [data-rel-page="prev"]').click();
        await expect(page.locator('#relationshipTable .pager-info')).toContainText('Page 1 of 2');
        await page.locator('#relationshipTable [data-rel-page-size]').selectOption('50');
        await expect(page.locator('#relationshipTable .pager-info')).toContainText('Showing 1-27 of 27 rows');

        await page.locator('#relationshipTable [data-rel-page-size]').selectOption('25');
        await page.locator('#relationshipTable [data-rel-page="next"]').click();
        await page.evaluate(() => window.showPage('expenses'));
        await waitForApp(page);
        await page.locator('#appBackBtn').click();
        await waitForApp(page);
        await expect(page.locator('#relationshipSearch')).toHaveValue(fixturePrefix);
        await expect(page.locator('#relationshipTable [data-rel-page-size]')).toHaveValue('25');
        await expect(page.locator('#relationshipTable .pager-info')).toContainText('Page 2 of 2');

        await page.evaluate(name => window.showPage(`customerDetail:${encodeURIComponent(name)}`), firstCustomer);
        await waitForApp(page);
        const salesSection = page.locator('section.relationship-section').filter({
          has: page.getByRole('heading', { name: 'Sales', exact: true }),
        });
        await expect(salesSection.locator('.pager-info')).toContainText('Showing 1-10 of 12 rows');
        await salesSection.getByRole('button', { name: 'Next page' }).click();
        await expect(salesSection.locator('.pager-info')).toContainText('Showing 11-12 of 12 rows');
        await salesSection.getByRole('button', { name: 'First page' }).click();
        await expect(salesSection.locator('.pager-info')).toContainText('Page 1 of 2');
        await salesSection.getByRole('button', { name: 'Last page' }).click();
        await expect(salesSection.locator('.pager-info')).toContainText('Page 2 of 2');
        await salesSection.getByRole('button', { name: 'Previous page' }).click();
        await expect(salesSection.locator('.pager-info')).toContainText('Page 1 of 2');
        await salesSection.locator('select.pager-size').selectOption('25');
        await expect(salesSection.locator('.pager-info')).toContainText('Showing 1-12 of 12 rows');

        await salesSection.locator('select.pager-size').selectOption('10');
        await salesSection.getByRole('button', { name: 'Last page' }).click();
        await page.evaluate(() => window.showPage('auditDocs'));
        await waitForApp(page);
        await page.locator('#appBackBtn').click();
        await waitForApp(page);
        await expect(salesSection.locator('select.pager-size')).toHaveValue('10');
        await expect(salesSection.locator('.pager-info')).toContainText('Page 2 of 2');
      });

      expect(failures).toEqual([]);
    } finally {
      for (const id of createdSaleIds) {
        await page.request.delete(`/api/sales/${id}`).catch(() => {});
      }
    }
  });

  test('TurboTax Setup save is single-flight, persistent, and cannot snap navigation back', async ({ page }) => {
    test.setTimeout(45_000);
    const failures = collectBrowserFailures(page);
    const taxYear = 2026;
    const originalResponse = await page.request.get(`/api/turbotax-setup?year=${taxYear}`);
    expect(originalResponse.ok(), await originalResponse.text()).toBe(true);
    const original = await originalResponse.json();
    const businessName = `EPATA TurboTax checkpoint ${Date.now()}`;
    const notes = `TurboTax async navigation proof ${Date.now()}`;
    let putCount = 0;

    await page.route('**/api/turbotax-setup', async route => {
      if (route.request().method() !== 'PUT') return route.continue();
      putCount += 1;
      await new Promise(resolve => setTimeout(resolve, 350));
      await route.continue();
    });

    try {
      await page.goto('/');
      await waitForApp(page);
      await page.evaluate(() => window.showPage('taxPrep'));
      await waitForApp(page);
      await page.locator('#ttBusinessName').fill(businessName);
      await page.locator('#ttNotes').fill(notes);
      await page.evaluate(() => {
        const button = [...document.querySelectorAll('button')].find(node => node.textContent.trim() === 'Save TurboTax Setup');
        button.click();
        button.click();
      });
      await expect.poll(() => putCount).toBe(1);

      await page.evaluate(() => window.showPage('expenses'));
      await waitForApp(page);
      await expect(page.locator('button.nav-button[data-page="expenses"]')).toHaveClass(/active/);
      await page.waitForTimeout(500);
      await expect(page.locator('button.nav-button[data-page="expenses"]')).toHaveClass(/active/);

      await expect.poll(async () => {
        const response = await page.request.get(`/api/turbotax-setup?year=${taxYear}`);
        return (await response.json()).businessName;
      }).toBe(businessName);
      const saved = await page.request.get(`/api/turbotax-setup?year=${taxYear}`).then(response => response.json());
      expect(saved.notes).toBe(notes);

      await page.locator('#appBackBtn').click();
      await waitForApp(page);
      await expect(page.locator('#ttBusinessName')).toHaveValue(businessName);
      await expect(page.locator('#ttNotes')).toHaveValue(notes);
      expect(failures).toEqual([]);
    } finally {
      await page.unroute('**/api/turbotax-setup');
      await page.request.put('/api/turbotax-setup', { data: original });
    }
  });

  test('AI Review finding sync is single-flight, stale-navigation safe, and recoverable after failure', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let syncCalls = 0;
    page.on('dialog', dialog => dialog.accept());

    await page.route('**/api/ai/review/status', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        engine: 'Mock deterministic review', reads: ['mock ledger rows'],
        safety: 'Read-only mock review.', useWhen: 'Testing rendered sync controls.',
      }),
    }));
    await page.route('**/api/ai/review', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({
        engine: 'Mock deterministic review', generatedAtUtc: '2026-09-10T12:00:00Z', usedAi: false,
        items: [{
          priority: 'High', area: 'Sales', route: 'sales', title: 'Mock verified gap',
          why: 'A deterministic test finding exists.', recommendedAction: 'Review the source row.',
          evidence: 'Fixture evidence only.', engine: 'Local Rules',
        }],
      }),
    }));
    await page.route('**/api/ai/local/status', route => route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ modelReady: false, state: 'Stopped', message: 'Mock stopped.', safety: 'Loopback only.' }),
    }));
    await page.route('**/api/action-items/sync-findings', async route => {
      syncCalls += 1;
      if (syncCalls === 1) {
        await new Promise(resolve => setTimeout(resolve, 350));
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ createdCount: 1, skippedExistingCount: 0 }),
        });
        return;
      }
      await route.fulfill({
        status: 400,
        contentType: 'application/json',
        body: JSON.stringify({ message: 'Mock sync validation failure.' }),
      });
    });

    await page.goto('/');
    await waitForApp(page);
    await page.evaluate(() => window.showPage('aiReview'));
    await waitForApp(page);
    const syncButton = page.getByRole('button', { name: 'Sync Verified Findings to Actions', exact: true });
    await expect(syncButton).toBeVisible();
    await page.evaluate(() => {
      const button = [...document.querySelectorAll('button')].find(node => node.textContent.trim() === 'Sync Verified Findings to Actions');
      button.click();
      button.click();
    });
    await expect.poll(() => syncCalls).toBe(1);
    await page.evaluate(() => window.showPage('expenses'));
    await waitForApp(page);
    await page.waitForTimeout(450);
    await expect(page.locator('button.nav-button[data-page="expenses"]')).toHaveClass(/active/);

    await page.evaluate(() => window.showPage('aiReview'));
    await waitForApp(page);
    await page.getByRole('button', { name: 'Sync Verified Findings to Actions', exact: true }).click();
    await expect.poll(() => syncCalls).toBe(2);
    await expect(page.locator('#toast-container')).toContainText('Mock sync validation failure.');
    await expect(page.getByRole('button', { name: 'Sync Verified Findings to Actions', exact: true })).toBeEnabled();
    await expect(page.locator('button.nav-button[data-page="aiReview"]')).toHaveClass(/active/);
    expect(failures.filter(failure => !failure.includes('400 (Bad Request)'))).toEqual([]);
  });

  test('legacy invoice import handles success, failure, stale navigation, and duplicate clicks', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let importCalls = 0;

    await page.route('**/api/import/invoice-app', async route => {
      importCalls += 1;
      if (importCalls === 1) {
        await new Promise(resolve => setTimeout(resolve, 250));
        await route.fulfill({
          status: 200,
          contentType: 'application/json',
          body: JSON.stringify({ imported: 3, skipped: 1, source: 'Mock legacy app' }),
        });
        return;
      }
      if (importCalls === 2) {
        await route.fulfill({
          status: 400,
          contentType: 'application/json',
          body: JSON.stringify({ message: 'Mock legacy app is unavailable.' }),
        });
        return;
      }
      await new Promise(resolve => setTimeout(resolve, 300));
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        body: JSON.stringify({ imported: 0, skipped: 4, source: 'Mock stale response' }),
      });
    });

    await page.goto('/');
    await waitForApp(page);
    await page.evaluate(() => window.showPage('importExport'));
    await waitForApp(page);
    await page.evaluate(() => {
      document.querySelector('#tryImportBtn').click();
      document.querySelector('#tryImportBtn').click();
    });
    await expect.poll(() => importCalls).toBe(1);
    await expect(page.locator('#importResult')).toContainText('"imported": 3');
    await expect(page.locator('#tryImportBtn')).toBeEnabled();

    await page.locator('#tryImportBtn').click();
    await expect.poll(() => importCalls).toBe(2);
    await expect(page.locator('#importResult')).toContainText('Mock legacy app is unavailable.');
    await expect(page.locator('#tryImportBtn')).toBeEnabled();

    await page.locator('#tryImportBtn').click();
    await expect.poll(() => importCalls).toBe(3);
    await page.evaluate(() => window.showPage('dashboard'));
    await waitForApp(page);
    await page.waitForTimeout(400);
    await expect(page.locator('button.nav-button[data-page="dashboard"]')).toHaveClass(/active/);
    expect(failures.filter(failure => !failure.includes('400 (Bad Request)'))).toEqual([]);
  });

  test('every rendered export endpoint and representative download control has a working contract', async ({ page }, testInfo) => {
    test.setTimeout(120_000);
    const failures = collectBrowserFailures(page);
    const inventory = [];

    async function inventorySurface(pageName) {
      await page.evaluate(name => window.showPage(name), pageName);
      await waitForApp(page);
      const links = await page.locator('a[href^="/api/export/"]').evaluateAll((anchors, surface) => anchors.map(anchor => ({
        surface,
        text: anchor.textContent.trim(),
        href: `${anchor.getAttribute('href')}`,
        target: anchor.getAttribute('target') || '',
        rel: anchor.getAttribute('rel') || '',
      })), pageName);
      inventory.push(...links);
      for (const link of links.filter(item => item.target === '_blank')) {
        expect(link.rel.split(/\s+/)).toContain('noopener');
      }
    }

    await page.goto('/');
    await waitForApp(page);
    for (const surface of ['sales', 'actions', 'communications', 'printerQueue', 'taxObligations', 'importExport', 'taxPrep']) {
      await inventorySurface(surface);
    }

    const importExportLinks = inventory.filter(item => item.surface === 'importExport');
    expect(importExportLinks.length, 'Import / Export must expose one link for every configured ledger route').toBe(17);
    const uniqueHrefs = [...new Set(inventory.map(item => item.href))].sort();
    expect(inventory.length, 'Every rendered export link across the audited surfaces must remain inventoried').toBe(49);
    expect(uniqueHrefs.length, 'Rendered export inventory should cover generic, queue, communications, and tax exports').toBe(30);

    const endpointResults = [];
    for (const href of uniqueHrefs) {
      const response = await page.request.get(href);
      const body = await response.body();
      endpointResults.push({
        href,
        status: response.status(),
        contentType: response.headers()['content-type'] || '',
        disposition: response.headers()['content-disposition'] || '',
        bytes: body.length,
      });
      expect(response.ok(), `${href} should return a successful export response`).toBe(true);
      expect(body.length, `${href} should return a non-empty export payload`).toBeGreaterThan(0);
    }

    await page.evaluate(() => window.showPage('importExport'));
    await waitForApp(page);
    const backupControls = await page.locator('[onclick*="backupDb"]').count();
    expect(backupControls).toBeGreaterThan(0);
    const mainBackup = page.waitForEvent('download');
    await page.locator('#backupBtn').click();
    const mainBackupDownload = await mainBackup;
    expect(mainBackupDownload.suggestedFilename()).toMatch(/\.db$/i);

    const exportSeedResponse = await page.request.post('/api/documents', {
      data: {
        docType: 'INVOICE',
        status: 'Sent',
        docDate: '2026-09-11',
        dueDate: '2026-09-25',
        customerName: 'Export Contract Customer',
        projectName: 'Export Contract Fixture',
        pageSize: 'LETTER',
        total: 25,
        amountPaid: 0,
        paymentMethod: 'Cash',
        lineItems: [{ sortOrder: 1, description: 'Export contract line', quantity: 1, rate: 25 }],
      },
    });
    const exportSeedText = await exportSeedResponse.text();
    expect(exportSeedResponse.ok(), exportSeedText).toBe(true);
    const exportSeed = JSON.parse(exportSeedText);

    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#db-status-text')).toContainText('Ready');
    await page.locator('button.nav-item[data-view="records"]').click();
    await expect(page.locator('#view-records')).toBeVisible();
    await expect(page.locator('#recordsBody')).toContainText(exportSeed.docNumber);
    const csvDownloadEvent = page.waitForEvent('download');
    await page.locator('#btnExportCsv').click();
    const csvDownload = await csvDownloadEvent;
    expect(csvDownload.suggestedFilename()).toMatch(/\.csv$/i);
    const dbDownloadEvent = page.waitForEvent('download');
    await page.locator('#btnExportDb').click();
    const dbDownload = await dbDownloadEvent;
    expect(dbDownload.suggestedFilename()).toMatch(/\.db$/i);

    await testInfo.attach('rendered-export-control-inventory.json', {
      body: Buffer.from(JSON.stringify({ controls: inventory, endpoints: endpointResults }, null, 2)),
      contentType: 'application/json',
    });
    expect(failures).toEqual([]);
  });
});
