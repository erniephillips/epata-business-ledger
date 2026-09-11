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

async function apiRow(page, route, id) {
  const response = await page.request.get(`/api/${route}/${id}`);
  expect(response.ok(), await response.text()).toBe(true);
  return response.json();
}

async function expectQueueStatus(page, id, status) {
  await expect.poll(async () => (await apiRow(page, 'printer-queue-items', id)).status).toBe(status);
}

test.describe('stateful operational workflows', () => {
  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'The state transition journey runs once; responsive coverage is separate.');
  });

  test('printer production and communication controls advance without losing prior state', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const suffix = Date.now();
    const customerName = `O'Neil Forward ${suffix}`;
    const jobName = `Stateful dragon batch ${suffix}`;
    const subject = `Approval received ${suffix}`;
    let jobId = 0;
    let queueId = 0;
    let communicationId = 0;

    page.on('dialog', dialog => dialog.accept());

    try {
      const jobResponse = await page.request.post('/api/customer-jobs', {
        data: {
          jobDate: '2026-09-10',
          customerName,
          platform: 'Direct',
          jobNumber: `JOB-${suffix}`,
          relatedOrderNumber: `ORDER-${suffix}`,
          jobName,
          jobType: 'Print',
          status: 'Open',
          productName: 'Articulated dragon',
          material: 'PLA',
          color: 'Galaxy Purple',
          description: 'Two build plates with final fit check.',
          needsReview: false,
        },
      });
      expect(jobResponse.ok(), await jobResponse.text()).toBe(true);
      jobId = (await jobResponse.json()).id;

      await page.goto('/');
      await waitForApp(page);
      await page.evaluate(() => window.showPage('printerQueue'));
      await waitForApp(page);

      await test.step('queue-from-job Cancel is non-mutating and the second attempt keeps its prefill', async () => {
        const baseline = await page.request.get('/api/printer-queue-items');
        const baselineRows = await baseline.json();
        const baselineCount = baselineRows.filter(row => Number(row.customerJobId) === Number(jobId)).length;

        await page.locator('#printerQueueJobSelect').selectOption(String(jobId));
        await page.getByRole('button', { name: 'Add Selected Job to Queue' }).click();
        await expect(page.locator('#modal')).toBeVisible();
        await expect(page.locator('#field_customerName')).toHaveValue(customerName);
        await expect(page.locator('#field_jobName')).toHaveValue(jobName);
        await expect(page.locator('#field_material')).toHaveValue('PLA');
        await page.locator('#modalCancel').click();
        await expect(page.locator('#modal')).toBeHidden();

        const afterCancelRows = await (await page.request.get('/api/printer-queue-items')).json();
        expect(afterCancelRows.filter(row => Number(row.customerJobId) === Number(jobId))).toHaveLength(baselineCount);

        await page.locator('#printerQueueJobSelect').selectOption(String(jobId));
        await page.getByRole('button', { name: 'Add Selected Job to Queue' }).click();
        await expect(page.locator('#field_customerName')).toHaveValue(customerName);
        await expect(page.locator('#field_jobName')).toHaveValue(jobName);
      });

      await test.step('saving the queue item creates exactly the intended queued production record', async () => {
        await page.locator('#field_printerName').fill('Bambu P1S');
        await page.locator('#field_quantity').fill('3');
        await page.locator('#field_plateCount').fill('2');
        await page.locator('#field_estimatedHours').fill('7.5');
        await page.locator('#field_notes').fill('0.4 nozzle; verify joints before packing.');
        const createPromise = page.waitForResponse(response =>
          response.request().method() === 'POST' && response.url().endsWith('/api/printer-queue-items'));
        await page.locator('#modalSave').click();
        const createResponse = await createPromise;
        expect(createResponse.ok(), await createResponse.text()).toBe(true);
        queueId = (await createResponse.json()).id;
        await expect(page.locator('#modal')).toBeHidden();
        await expect(page.locator('.printer-queue-card').filter({ hasText: jobName })).toBeVisible();

        const queue = await apiRow(page, 'printer-queue-items', queueId);
        expect(queue.status).toBe('Queued');
        expect(queue.customerJobId).toBe(jobId);
        expect(queue.quantity).toBe(3);
        expect((await apiRow(page, 'customer-jobs', jobId)).status).toBe('Open');
      });

      await test.step('Ready, Start, Pause, resume, and Complete move both queue and linked job coherently', async () => {
        const queueCard = () => page.locator('.printer-queue-card').filter({ hasText: jobName });

        await queueCard().getByRole('button', { name: 'Ready', exact: true }).click();
        await expectQueueStatus(page, queueId, 'Ready');
        expect((await apiRow(page, 'customer-jobs', jobId)).status).toBe('Open');

        let printingPutCount = 0;
        await page.route(`**/api/printer-queue-items/${queueId}`, async route => {
          if (route.request().method() === 'PUT') {
            printingPutCount += 1;
            await new Promise(resolve => setTimeout(resolve, 250));
          }
          await route.continue();
        });
        await page.evaluate(id => {
          void window.updatePrinterQueueStatus(id, 'Printing');
          void window.updatePrinterQueueStatus(id, 'Printing');
        }, queueId);
        await expectQueueStatus(page, queueId, 'Printing');
        expect(printingPutCount).toBe(1);
        await page.unroute(`**/api/printer-queue-items/${queueId}`);

        let queue = await apiRow(page, 'printer-queue-items', queueId);
        expect(queue.startedAt).toBeTruthy();
        expect(queue.progressPercent).toBeGreaterThanOrEqual(1);
        expect((await apiRow(page, 'customer-jobs', jobId)).status).toBe('In Progress');

        await queueCard().getByRole('button', { name: 'Pause', exact: true }).click();
        await expectQueueStatus(page, queueId, 'Paused');
        expect((await apiRow(page, 'customer-jobs', jobId)).status).toBe('In Progress');

        await queueCard().getByRole('button', { name: 'Start', exact: true }).click();
        await expectQueueStatus(page, queueId, 'Printing');
        await queueCard().getByRole('button', { name: 'Complete', exact: true }).click();
        await expectQueueStatus(page, queueId, 'Completed');

        queue = await apiRow(page, 'printer-queue-items', queueId);
        expect(queue.progressPercent).toBe(100);
        expect(queue.completedAt).toBeTruthy();
        expect((await apiRow(page, 'customer-jobs', jobId)).status).toBe('Completed');
      });

      await test.step('editing a completed queue item retains its terminal state and timestamps', async () => {
        const card = page.locator('.printer-queue-card').filter({ hasText: jobName });
        await card.getByRole('button', { name: 'Edit', exact: true }).click();
        await expect(page.locator('#modal')).toBeVisible();
        await expect(page.locator('#field_status')).toHaveValue('Completed');
        await page.locator('#field_actualHours').fill('7.25');
        const savePromise = page.waitForResponse(response =>
          response.request().method() === 'PUT' && response.url().endsWith(`/api/printer-queue-items/${queueId}`));
        await page.locator('#modalSave').click();
        expect((await savePromise).ok()).toBe(true);
        const queue = await apiRow(page, 'printer-queue-items', queueId);
        expect(queue.status).toBe('Completed');
        expect(queue.progressPercent).toBe(100);
        expect(queue.completedAt).toBeTruthy();
        expect(queue.actualHours).toBe(7.25);
      });

      await test.step('communication search survives a page round trip, then Clear and Edit remain usable', async () => {
        await page.evaluate(() => window.showPage('communications'));
        await waitForApp(page);
        await page.getByRole('button', { name: 'Log Communication' }).click();
        await expect(page.locator('#modal')).toBeVisible();
        await page.locator('#field_customerName').fill(customerName);
        await page.locator('#field_direction').selectOption('Incoming');
        await page.locator('#field_channel').selectOption('Etsy Message');
        await page.locator('#field_subject').fill(subject);
        await page.locator('#field_customerJobId').fill(String(jobId));
        await page.locator('#field_relatedJobNumber').fill(`JOB-${suffix}`);
        await page.locator('#field_followUpStatus').selectOption('Open');
        await page.locator('#field_followUpDate').fill('2026-09-12');
        await page.locator('#field_summary').fill('Customer approved the purple material and three-unit quantity.');

        const createPromise = page.waitForResponse(response =>
          response.request().method() === 'POST' && response.url().endsWith('/api/customer-communications'));
        await page.locator('#modalSave').click();
        const createResponse = await createPromise;
        expect(createResponse.ok(), await createResponse.text()).toBe(true);
        communicationId = (await createResponse.json()).id;
        await expect(page.locator('.communication-card').filter({ hasText: subject })).toBeVisible();

        await page.locator('#communicationSearch').fill(subject);
        await expect(page.locator('#communicationSearch')).toHaveValue(subject);
        await expect(page.locator('.communication-card')).toHaveCount(1);
        await page.getByRole('button', { name: 'Open Job Timeline' }).click();
        await waitForApp(page);
        await page.locator('#appBackBtn').click();
        await waitForApp(page);
        await expect(page.locator('#communicationSearch')).toHaveValue(subject);
        await expect(page.locator('.communication-card').filter({ hasText: subject })).toBeVisible();

        await page.locator('#communicationCustomer').selectOption(customerName);
        await expect(page.locator('.communication-card')).toHaveCount(1);
        await page.locator('#communicationClear').click();
        await expect(page.locator('#communicationSearch')).toHaveValue('');
        await expect(page.locator('#communicationCustomer')).toHaveValue('');

        const communication = page.locator('.communication-card').filter({ hasText: subject });
        await communication.getByRole('button', { name: 'Edit', exact: true }).click();
        await page.locator('#field_summary').fill('Customer approved purple PLA, three units, and the final joint check.');
        const savePromise = page.waitForResponse(response =>
          response.request().method() === 'PUT' && response.url().endsWith(`/api/customer-communications/${communicationId}`));
        await page.locator('#modalSave').click();
        expect((await savePromise).ok()).toBe(true);
        await expect(page.locator('.communication-card').filter({ hasText: 'final joint check' })).toBeVisible();

        const downloadPromise = page.waitForEvent('download');
        await page.getByRole('link', { name: 'Export CSV' }).click();
        const download = await downloadPromise;
        expect(download.suggestedFilename()).toMatch(/\.csv$/i);
        expect(await download.path()).toBeTruthy();
      });

      expect(failures).toEqual([]);
    } finally {
      if (communicationId) await page.request.delete(`/api/customer-communications/${communicationId}`).catch(() => {});
      if (queueId) await page.request.delete(`/api/printer-queue-items/${queueId}`).catch(() => {});
      if (jobId) await page.request.delete(`/api/customer-jobs/${jobId}`).catch(() => {});
    }
  });
});
