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

async function openInvoiceBuilder(page) {
  await page.goto('/invoice-builder/index.html');
  await expect(page.locator('#db-status-text')).toContainText(/Ready .+ new estimate/);
  await expect(page.locator('#docNumber')).toHaveValue(/^EST-\d{4}-\d{4}$/);
  await page.locator('button.nav-item[data-view="builder"]').click();
  await expect(page.locator('#view-builder')).toBeVisible();
}

async function waitForDocumentSave(page, expectedMethod = null) {
  const response = await page.waitForResponse(resp => {
    const method = resp.request().method();
    return resp.url().includes('/api/documents') &&
      ['POST', 'PUT'].includes(method) &&
      (!expectedMethod || method === expectedMethod) &&
      resp.status() < 400;
  });
  return response.json();
}

async function clickSaveAndWait(page, expectedMethod = null) {
  const savePromise = waitForDocumentSave(page, expectedMethod);
  await page.locator('#btnSaveDraft').click();
  const saved = await savePromise;
  await waitForVisibleSavedState(page, saved);
  return saved;
}

async function clickSaveAsNewAndWait(page, expectedMethod = null) {
  const savePromise = waitForDocumentSave(page, expectedMethod);
  await page.locator('#btnSaveNew').click();
  const saved = await savePromise;
  await waitForVisibleSavedState(page, saved);
  return saved;
}

async function waitForVisibleSavedState(page, saved) {
  await expect(page.locator('#view-builder')).toBeVisible();
  if (saved?.docNumber) {
    await expect(page.locator('#activeRecordText')).toContainText(saved.docNumber);
  }
  await expect(page.locator('#db-status-text')).toContainText('Ready');
}

async function waitForNoSaveInFlight(page) {
  await page.waitForFunction(() => !window._invoiceToolIsSaving || !window._invoiceToolIsSaving());
}

function recordRow(page, docNumber) {
  return page.locator('#recordsBody tr')
    .filter({ has: page.locator('td.doc-number', { hasText: docNumber }) })
    .first();
}

async function openRecord(page, docNumber, id = null) {
  const listResponse = page.waitForResponse(resp =>
    resp.url().includes('/api/documents') &&
    resp.request().method() === 'GET' &&
    resp.status() < 400,
  ).catch(() => null);
  await page.locator('button.nav-item[data-view="records"]').click();
  await expect(page.locator('#view-records')).toBeVisible();
  await listResponse;
  await page.locator('#recSearch').fill(docNumber);
  const row = recordRow(page, docNumber);
  await expect(row.locator('td.doc-number')).toHaveText(docNumber);
  const openButton = row.locator('button', { hasText: /^Open$/ }).first();
  if (id) {
    await expect(openButton).toHaveAttribute('data-record-action', 'load');
    await expect(openButton).toHaveAttribute('data-record-id', String(id));
  }
  await openButton.click();
  await expect(page.locator('#view-builder')).toBeVisible();
  await expect(page.locator('#activeRecordText')).toContainText(docNumber);
  await expect(page.locator('#docNumber')).toHaveValue(docNumber);
}

async function setField(page, selector, value) {
  const locator = page.locator(selector);
  const tag = await locator.evaluate(node => node.tagName);
  if (tag === 'SELECT') await locator.selectOption(value);
  else await locator.fill(String(value));
  await locator.dispatchEvent('change');
  await locator.dispatchEvent('blur');
}

async function addLineItems(page, items) {
  const rows = page.locator('#lineItemsBody tr');
  while (await rows.count() < items.length) {
    await page.getByRole('button', { name: /\+ Add Line Item/i }).last().click();
  }
  while (await rows.count() > items.length) {
    await rows.last().locator('button[aria-label="Remove line item"]').click();
  }

  for (let i = 0; i < items.length; i++) {
    const row = rows.nth(i);
    await row.locator('.item-desc').fill(items[i].description);
    await row.locator('.item-details').fill(items[i].details);
    await row.locator('.item-qty').fill(String(items[i].quantity));
    await row.locator('.item-rate').fill(String(items[i].rate));
    await row.locator('.item-rate').dispatchEvent('input');
    await row.locator('.item-rate').dispatchEvent('change');
  }
}

async function fillDocument(page, profile) {
  await setField(page, '#docType', profile.docType);
  await setField(page, '#docStatus', profile.status);
  await setField(page, '#pageSize', profile.pageSize);
  await setField(page, '#docDate', profile.docDate);
  await setField(page, '#dueDate', profile.dueDate);
  await setField(page, '#customerName', profile.customerName);
  await setField(page, '#preparedFor', profile.preparedFor);
  await setField(page, '#customerPhone', profile.customerPhone);
  await setField(page, '#customerEmail', profile.customerEmail);
  await setField(page, '#customerAddress', profile.customerAddress);
  await setField(page, '#projectName', profile.projectName);
  await setField(page, '#material', profile.material);
  await setField(page, '#color', profile.color);
  await setField(page, '#infill', profile.infill);
  await setField(page, '#projectDescription', profile.projectDescription);
  await setField(page, '#projectNotes', profile.projectNotes);
  await addLineItems(page, profile.lineItems);
  await setField(page, '#docDiscount', profile.discount);
  await setField(page, '#docRushPercent', profile.rushPercent);
  await setField(page, '#docTaxRate', profile.taxRate);
  await setField(page, '#amountPaid', profile.amountPaid);
  await setField(page, '#paymentMethod', profile.paymentMethod);
  await setField(page, '#pricingGuide', profile.pricingGuide);
  await setField(page, '#termsNotes', profile.termsNotes);
  await setField(page, '#standardTurnaround', profile.standardTurnaround);
  await setField(page, '#rushTurnaround', profile.rushTurnaround);
}

async function expectFormFields(page, profile) {
  await expect(page.locator('#docType')).toHaveValue(profile.docType);
  await expect(page.locator('#docStatus')).toHaveValue(profile.status);
  await expect(page.locator('#pageSize')).toHaveValue(profile.pageSize);
  await expect(page.locator('#docDate')).toHaveValue(profile.docDate);
  await expect(page.locator('#dueDate')).toHaveValue(profile.dueDate);
  await expect(page.locator('#customerName')).toHaveValue(profile.customerName);
  await expect(page.locator('#preparedFor')).toHaveValue(profile.preparedFor);
  await expect(page.locator('#customerPhone')).toHaveValue(profile.expectedPhone || profile.customerPhone);
  await expect(page.locator('#customerEmail')).toHaveValue(profile.expectedEmail || profile.customerEmail);
  await expect(page.locator('#customerAddress')).toHaveValue(profile.customerAddress);
  await expect(page.locator('#projectName')).toHaveValue(profile.projectName);
  await expect(page.locator('#material')).toHaveValue(profile.material);
  await expect(page.locator('#color')).toHaveValue(profile.color);
  await expect(page.locator('#infill')).toHaveValue(profile.infill);
  await expect(page.locator('#projectDescription')).toHaveValue(profile.projectDescription);
  await expect(page.locator('#projectNotes')).toHaveValue(new RegExp(escapeRegExp(profile.projectNotes)));
  await expect(page.locator('#docDiscount')).toHaveValue(String(profile.discount));
  await expect(page.locator('#docRushPercent')).toHaveValue(String(profile.rushPercent));
  await expect(page.locator('#docTaxRate')).toHaveValue(String(profile.taxRate));
  await expect(page.locator('#paymentMethod')).toHaveValue(profile.paymentMethod);
  await expect(page.locator('#pricingGuide')).toHaveValue(profile.pricingGuide);
  await expect(page.locator('#termsNotes')).toHaveValue(new RegExp(escapeRegExp(profile.termsNotes)));
  await expect(page.locator('#standardTurnaround')).toHaveValue(profile.standardTurnaround);
  await expect(page.locator('#rushTurnaround')).toHaveValue(profile.rushTurnaround);

  for (let i = 0; i < profile.lineItems.length; i++) {
    const row = page.locator('#lineItemsBody tr').nth(i);
    await expect(row.locator('.item-desc')).toHaveValue(profile.lineItems[i].description);
    await expect(row.locator('.item-details')).toHaveValue(profile.lineItems[i].details);
    await expect(row.locator('.item-qty')).toHaveValue(String(profile.lineItems[i].quantity));
    await expect(row.locator('.item-rate')).toHaveValue(String(profile.lineItems[i].rate));
  }
}

async function expectApiFields(page, doc, profile) {
  const persisted = await page.request.get(`/api/documents/${doc.id}`).then(resp => resp.json());
  expect(persisted.docNumber).toBe(doc.docNumber);
  expect(persisted.docType).toBe(profile.docType);
  expect(persisted.status).toBe(profile.status);
  expect(persisted.pageSize).toBe(profile.pageSize);
  expect(persisted.docDate).toBe(profile.docDate);
  expect(persisted.dueDate).toBe(profile.dueDate);
  expect(persisted.customerName).toBe(profile.customerName);
  expect(persisted.preparedFor).toBe(profile.preparedFor);
  expect(persisted.customerPhone).toBe(profile.expectedPhone || profile.customerPhone);
  expect(persisted.customerEmail).toBe(profile.expectedEmail || profile.customerEmail);
  expect(persisted.customerAddress).toBe(profile.customerAddress);
  expect(persisted.projectName).toBe(profile.projectName);
  expect(persisted.material).toBe(profile.material);
  expect(persisted.color).toBe(profile.color);
  expect(persisted.infill).toBe(profile.infill);
  expect(persisted.projectDescription).toBe(profile.projectDescription);
  expect(persisted.projectNotes).toContain(profile.projectNotes);
  expect(persisted.paymentMethod).toBe(profile.paymentMethod);
  expect(persisted.pricingGuide).toBe(profile.pricingGuide);
  expect(persisted.termsNotes).toContain(profile.termsNotes);
  expect(persisted.standardTurnaround).toBe(profile.standardTurnaround);
  expect(persisted.rushTurnaround).toBe(profile.rushTurnaround);
  expect(persisted.lineItems).toHaveLength(profile.lineItems.length);
  for (let i = 0; i < profile.lineItems.length; i++) {
    expect(persisted.lineItems[i].description).toBe(profile.lineItems[i].description);
    expect(persisted.lineItems[i].details).toBe(profile.lineItems[i].details);
    expect(Number(persisted.lineItems[i].quantity)).toBeCloseTo(Number(profile.lineItems[i].quantity), 2);
    expect(Number(persisted.lineItems[i].rate)).toBeCloseTo(Number(profile.lineItems[i].rate), 2);
  }
  return persisted;
}

async function createDocumentByApi(page, profile) {
  const response = await page.request.post('/api/documents', {
    data: {
      docType: profile.docType,
      status: profile.status,
      docDate: profile.docDate,
      dueDate: profile.dueDate,
      customerName: profile.customerName,
      customerPhone: profile.expectedPhone || profile.customerPhone,
      customerAddress: profile.customerAddress,
      customerEmail: profile.expectedEmail || profile.customerEmail,
      preparedFor: profile.preparedFor,
      projectName: profile.projectName,
      material: profile.material,
      color: profile.color,
      infill: profile.infill,
      projectDescription: profile.projectDescription,
      projectNotes: profile.projectNotes,
      pageSize: profile.pageSize,
      subtotal: 0,
      discountAmount: Number(profile.discount || 0),
      rushAmount: 0,
      taxAmount: 0,
      total: 0,
      amountPaid: Number(profile.amountPaid || 0),
      balance: 0,
      paymentMethod: profile.paymentMethod,
      pricingGuide: profile.pricingGuide,
      termsNotes: profile.termsNotes,
      standardTurnaround: profile.standardTurnaround,
      rushTurnaround: profile.rushTurnaround,
      calcTaxRate: Number(profile.taxRate || 0),
      lineItems: profile.lineItems.map((line, index) => ({
        sortOrder: index + 1,
        description: line.description,
        details: line.details,
        quantity: Number(line.quantity),
        rate: Number(line.rate),
      })),
    },
  });
  expect(response.ok()).toBe(true);
  return response.json();
}

async function latestToastText(page) {
  return page.locator('#toast-container .toast').last().textContent();
}

function escapeRegExp(value) {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}

const estimateProfile = {
  docType: 'ESTIMATE',
  status: 'Sent',
  pageSize: 'LETTER',
  docDate: '2026-01-15',
  dueDate: '2026-02-01',
  customerName: 'Stress Estimate Customer',
  preparedFor: 'Casey Estimate Contact',
  customerPhone: '5551234567',
  expectedPhone: '(555) 123-4567',
  customerEmail: 'FIELD-ESTIMATE@EXAMPLE.COM',
  expectedEmail: 'field-estimate@example.com',
  customerAddress: '123 Estimate Lane, Test City, NJ 07001',
  projectName: 'Stress Estimate Widget',
  material: 'PETG-CF',
  color: 'Transparent Blue',
  infill: '35%',
  projectDescription: 'Estimate field persistence description.',
  projectNotes: 'Private estimate note must not jump records.',
  lineItems: [
    { description: 'Estimate design labor', details: 'CAD cleanup and setup', quantity: '2', rate: '40' },
    { description: 'Estimate material', details: 'PETG-CF spool allocation', quantity: '1', rate: '20' },
  ],
  discount: '10',
  rushPercent: '10',
  taxRate: '7',
  amountPaid: '0',
  paymentMethod: 'Zelle',
  pricingGuide: 'Estimate pricing guide field text.',
  termsNotes: 'Estimate custom terms stay with estimate.',
  standardTurnaround: 'Estimate standard turnaround 5 business days.',
  rushTurnaround: 'Estimate rush turnaround 48 hours.',
};

const invoiceProfile = {
  docType: 'INVOICE',
  status: 'Partial',
  pageSize: 'LEGAL',
  docDate: '2026-03-10',
  dueDate: '2026-03-17',
  customerName: 'Stress Invoice Customer',
  preparedFor: 'Jordan Invoice Contact',
  customerPhone: '5559876543',
  expectedPhone: '(555) 987-6543',
  customerEmail: 'FIELD-INVOICE@EXAMPLE.COM',
  expectedEmail: 'field-invoice@example.com',
  customerAddress: '987 Invoice Road, Test City, NJ 07002',
  projectName: 'Stress Invoice Assembly',
  material: 'ASA',
  color: 'Matte Black',
  infill: '50%',
  projectDescription: 'Invoice field persistence description.',
  projectNotes: 'Private invoice note must not jump records.',
  lineItems: [
    { description: 'Invoice production run', details: 'Printed and inspected parts', quantity: '1', rate: '100' },
  ],
  discount: '0',
  rushPercent: '0',
  taxRate: '0',
  amountPaid: '25',
  paymentMethod: 'PayPal',
  pricingGuide: 'Invoice pricing guide field text.',
  termsNotes: 'Invoice custom terms stay with invoice.',
  standardTurnaround: 'Invoice standard turnaround complete.',
  rushTurnaround: 'Invoice rush turnaround not used.',
};

test.describe('invoice and estimate state stress', () => {
  test('round-trips every builder field while switching estimate and invoice records', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await openInvoiceBuilder(page);

    await fillDocument(page, estimateProfile);
    const estimate = await clickSaveAndWait(page, 'POST');
    expect(estimate.docType).toBe('ESTIMATE');
    await expectApiFields(page, estimate, estimateProfile);

    await setField(page, '#docType', 'INVOICE');
    await page.locator('#docNumber').fill('');
    await fillDocument(page, invoiceProfile);
    const invoice = await clickSaveAsNewAndWait(page, 'POST');
    expect(invoice.docType).toBe('INVOICE');
    const persistedInvoice = await expectApiFields(page, invoice, invoiceProfile);
    expect(Number(persistedInvoice.amountPaid)).toBeCloseTo(25, 2);
    expect(Number(persistedInvoice.balance)).toBeCloseTo(75, 2);

    await openRecord(page, estimate.docNumber, estimate.id);
    await expectFormFields(page, estimateProfile);
    await expect(page.locator('#activeRecordText')).toContainText(estimate.docNumber);

    await openRecord(page, invoice.docNumber, invoice.id);
    await expectFormFields(page, invoiceProfile);
    await expect(page.locator('#activeRecordText')).toContainText(invoice.docNumber);

    await setField(page, '#material', 'ASA - edited only on invoice');
    const savedInvoice = await clickSaveAndWait(page, 'PUT');
    expect(savedInvoice.id).toBe(invoice.id);

    await openRecord(page, estimate.docNumber, estimate.id);
    await expect(page.locator('#material')).toHaveValue(estimateProfile.material);
    const unchangedEstimate = await page.request.get(`/api/documents/${estimate.id}`).then(resp => resp.json());
    expect(unchangedEstimate.material).toBe(estimateProfile.material);

    await openRecord(page, invoice.docNumber, invoice.id);
    await expect(page.locator('#material')).toHaveValue('ASA - edited only on invoice');
    const changedInvoice = await page.request.get(`/api/documents/${invoice.id}`).then(resp => resp.json());
    expect(changedInvoice.material).toBe('ASA - edited only on invoice');

    expect(failures).toEqual([]);
  });

  test('reopening a paid invoice to sent restores the live balance due', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await openInvoiceBuilder(page);

    await fillDocument(page, {
      ...invoiceProfile,
      status: 'Sent',
      customerName: 'Paid Toggle Invoice Customer',
      projectName: 'Paid Toggle Invoice Project',
      amountPaid: '0',
      paymentMethod: 'Credit Card',
    });

    await expect(page.locator('#amountPaid')).toHaveValue('0');
    await expect(page.locator('#bPaid')).toHaveText('$0.00');
    await expect(page.locator('#bBalance')).toHaveText('$100.00');

    await setField(page, '#docStatus', 'Paid');
    await expect(page.locator('#amountPaid')).toHaveValue('100.00');
    await expect(page.locator('#bPaid')).toHaveText('$100.00');
    await expect(page.locator('#bBalance')).toHaveText('$0.00');

    await setField(page, '#docStatus', 'Sent');
    await expect(page.locator('#amountPaid')).toHaveValue('0');
    await expect(page.locator('#bPaid')).toHaveText('$0.00');
    await expect(page.locator('#bBalance')).toHaveText('$100.00');

    await setField(page, '#docStatus', 'Partial');
    await setField(page, '#amountPaid', '25');
    await expect(page.locator('#bPaid')).toHaveText('$25.00');
    await expect(page.locator('#bBalance')).toHaveText('$75.00');

    await setField(page, '#docStatus', 'Paid');
    await expect(page.locator('#amountPaid')).toHaveValue('100.00');
    await expect(page.locator('#bBalance')).toHaveText('$0.00');

    await setField(page, '#docStatus', 'Partial');
    await expect(page.locator('#amountPaid')).toHaveValue('25');
    await expect(page.locator('#bPaid')).toHaveText('$25.00');
    await expect(page.locator('#bBalance')).toHaveText('$75.00');

    await setField(page, '#docStatus', 'Sent');
    await expect(page.locator('#amountPaid')).toHaveValue('0');
    await expect(page.locator('#bBalance')).toHaveText('$100.00');

    const saved = await clickSaveAndWait(page, 'POST');
    expect(saved.docType).toBe('INVOICE');
    expect(saved.status).toBe('Sent');
    expect(Number(saved.amountPaid)).toBeCloseTo(0, 2);
    expect(Number(saved.balance)).toBeCloseTo(100, 2);

    const persisted = await page.request.get(`/api/documents/${saved.id}`).then(resp => resp.json());
    expect(persisted.status).toBe('Sent');
    expect(Number(persisted.amountPaid)).toBeCloseTo(0, 2);
    expect(Number(persisted.balance)).toBeCloseTo(100, 2);

    const receivables = await page.request.get('/api/receivable-invoices?includeArchived=true').then(resp => resp.json());
    const arRow = receivables.find(row => row.invoiceNumber === saved.docNumber);
    expect(arRow).toBeTruthy();
    expect(arRow.status).toBe('Sent');
    expect(Number(arRow.amountPaid)).toBeCloseTo(0, 2);
    expect(Number(arRow.balanceDue)).toBeCloseTo(100, 2);

    const sales = await page.request.get('/api/sales?includeArchived=true').then(resp => resp.json());
    expect(sales.filter(row => row.invoiceNumber === saved.docNumber).every(row => !row.includeInDashboard)).toBe(true);

    await openRecord(page, saved.docNumber, saved.id);
    await expect(page.locator('#docStatus')).toHaveValue('Sent');
    await expect(page.locator('#amountPaid')).toHaveValue('0');
    await expect(page.locator('#bBalance')).toHaveText('$100.00');

    expect(failures).toEqual([]);
  });

  test('saved estimate type-change save creates a new invoice and leaves the estimate intact', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await openInvoiceBuilder(page);
    await fillDocument(page, { ...estimateProfile, customerName: 'Type Change Estimate Customer', projectName: 'Type Change Estimate Project' });
    const estimate = await clickSaveAndWait(page, 'POST');

    await setField(page, '#docType', 'INVOICE');
    await expect(page.locator('#docNumber')).toHaveValue('');
    await setField(page, '#docStatus', 'Paid');
    await setField(page, '#paymentMethod', 'Credit Card');
    const createdInvoice = await clickSaveAndWait(page, 'POST');

    expect(createdInvoice.docType).toBe('INVOICE');
    expect(createdInvoice.id).not.toBe(estimate.id);
    expect(createdInvoice.docNumber).toMatch(/^INV-/);
    await expect(page.locator('#activeRecordText')).toContainText(createdInvoice.docNumber);

    const original = await page.request.get(`/api/documents/${estimate.id}`).then(resp => resp.json());
    expect(original.docType).toBe('ESTIMATE');
    expect(original.docNumber).toBe(estimate.docNumber);
    expect(original.customerName).toBe('Type Change Estimate Customer');
    expect(original.projectName).toBe('Type Change Estimate Project');
    expect(original.projectNotes || '').not.toContain(`Created as a new INVOICE from ESTIMATE ${estimate.docNumber}`);

    const invoice = await page.request.get(`/api/documents/${createdInvoice.id}`).then(resp => resp.json());
    expect(invoice.docType).toBe('INVOICE');
    expect(invoice.projectNotes || '').toContain(`Created as a new INVOICE from ESTIMATE ${estimate.docNumber}`);
    expect(Number(invoice.amountPaid)).toBeCloseTo(Number(invoice.total), 2);

    const sales = await page.request.get('/api/sales').then(resp => resp.json());
    expect(sales.some(row => row.invoiceNumber === invoice.docNumber && row.paymentMethod === 'Credit Card')).toBe(true);
    expect(sales.some(row => row.invoiceNumber === estimate.docNumber)).toBe(false);

    await openRecord(page, estimate.docNumber, estimate.id);
    await expect(page.locator('#docType')).toHaveValue('ESTIMATE');
    await expect(page.locator('#activeRecordText')).toContainText(estimate.docNumber);

    await openRecord(page, invoice.docNumber, invoice.id);
    await expect(page.locator('#docType')).toHaveValue('INVOICE');
    await expect(page.locator('#activeRecordText')).toContainText(invoice.docNumber);

    expect(failures).toEqual([]);
  });

  test('records Create Invoice opens the new invoice while the old estimate stays openable', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await openInvoiceBuilder(page);
    await fillDocument(page, { ...estimateProfile, customerName: 'Records Convert Customer', projectName: 'Records Convert Project' });
    const estimate = await clickSaveAndWait(page, 'POST');

    await page.locator('button.nav-item[data-view="records"]').click();
    await expect(page.locator('#view-records')).toBeVisible();
    await page.locator('#recSearch').fill(estimate.docNumber);
    const convertResponse = page.waitForResponse(resp =>
      resp.url().includes(`/api/documents/${estimate.id}/convert-to-invoice`) &&
      resp.request().method() === 'POST' &&
      resp.status() < 400,
    );
    await recordRow(page, estimate.docNumber).locator('button', { hasText: 'Create Invoice' }).click();
    const invoice = await (await convertResponse).json();

    expect(invoice.docType).toBe('INVOICE');
    expect(invoice.id).not.toBe(estimate.id);
    await expect(page.locator('#view-builder')).toBeVisible();
    await expect(page.locator('#activeRecordText')).toContainText(invoice.docNumber);
    await expect(page.locator('#docType')).toHaveValue('INVOICE');
    await expect(page.locator('#docStatus')).toHaveValue('Draft');

    const convertedEstimate = await page.request.get(`/api/documents/${estimate.id}`).then(resp => resp.json());
    expect(convertedEstimate.docType).toBe('ESTIMATE');
    expect(convertedEstimate.status).toBe('Accepted');
    expect(convertedEstimate.projectNotes || '').toContain(`Converted to invoice ${invoice.docNumber}.`);

    await openRecord(page, estimate.docNumber, estimate.id);
    await expect(page.locator('#docType')).toHaveValue('ESTIMATE');
    await expect(page.locator('#docStatus')).toHaveValue('Accepted');
    await expect(page.locator('#activeRecordText')).toContainText(estimate.docNumber);

    await openRecord(page, invoice.docNumber, invoice.id);
    await expect(page.locator('#docType')).toHaveValue('INVOICE');
    await expect(page.locator('#activeRecordText')).toContainText(invoice.docNumber);

    expect(failures).toEqual([]);
  });

  test('slow save cannot overwrite active identity after user opens another record', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await openInvoiceBuilder(page);
    const first = await createDocumentByApi(page, { ...estimateProfile, customerName: 'Slow Save First Customer', projectName: 'Slow Save First Project' });
    const second = await createDocumentByApi(page, { ...invoiceProfile, customerName: 'Slow Save Second Customer', projectName: 'Slow Save Second Project' });

    let releaseSave;
    let putStartedResolve;
    const putStarted = new Promise(resolve => { putStartedResolve = resolve; });
    const release = new Promise(resolve => { releaseSave = resolve; });
    await page.route('**/api/documents/**', async route => {
      const request = route.request();
      if (request.method() === 'PUT' && new URL(request.url()).pathname === `/api/documents/${first.id}`) {
        putStartedResolve();
        await release;
      }
      await route.continue();
    });

    await openRecord(page, first.docNumber, first.id);
    await setField(page, '#projectName', 'Slow Save First Project Edited');
    const saveResponse = waitForDocumentSave(page, 'PUT');
    await page.locator('#btnSaveDraft').click();
    await putStarted;

    await openRecord(page, second.docNumber, second.id);
    await expect(page.locator('#activeRecordText')).toContainText(second.docNumber);
    await expect(page.locator('#projectName')).toHaveValue('Slow Save Second Project');

    releaseSave();
    const savedFirst = await saveResponse;
    expect(savedFirst.id).toBe(first.id);
    await waitForNoSaveInFlight(page);

    await expect(page.locator('#activeRecordText')).toContainText(second.docNumber);
    await expect(page.locator('#projectName')).toHaveValue('Slow Save Second Project');

    await setField(page, '#material', 'Second record material after slow save');
    const savedSecond = await clickSaveAndWait(page, 'PUT');
    expect(savedSecond.id).toBe(second.id);

    const firstAfter = await page.request.get(`/api/documents/${first.id}`).then(resp => resp.json());
    const secondAfter = await page.request.get(`/api/documents/${second.id}`).then(resp => resp.json());
    expect(firstAfter.projectName).toBe('Slow Save First Project Edited');
    expect(firstAfter.material).toBe(estimateProfile.material);
    expect(secondAfter.projectName).toBe('Slow Save Second Project');
    expect(secondAfter.material).toBe('Second record material after slow save');

    expect(failures).toEqual([]);
  });

  test('record updates carry the loaded revision and refresh it after each save', async ({ page }) => {
    await openInvoiceBuilder(page);
    const doc = await createDocumentByApi(page, {
      ...estimateProfile,
      customerName: 'Revision Header Customer',
      projectName: 'Revision Header Original',
    });
    expect(doc.updatedAt).toBeTruthy();

    const expectedRevisionHeaders = [];
    await page.route('**/api/documents/**', async route => {
      const request = route.request();
      if (request.method() === 'PUT' && new URL(request.url()).pathname === `/api/documents/${doc.id}`) {
        expectedRevisionHeaders.push(request.headers()['x-epata-updated-at'] || '');
        const response = await route.fetch();
        await route.fulfill({ response });
        return;
      }
      await route.continue();
    });

    await openRecord(page, doc.docNumber, doc.id);
    await setField(page, '#projectName', 'Revision Header First Save');
    const firstSave = await clickSaveAndWait(page, 'PUT');
    expect(firstSave.updatedAt).toBeTruthy();
    expect(expectedRevisionHeaders).toEqual([doc.updatedAt]);

    await setField(page, '#projectName', 'Revision Header Second Save');
    const secondSave = await clickSaveAndWait(page, 'PUT');
    expect(secondSave.updatedAt).toBeTruthy();
    expect(expectedRevisionHeaders).toEqual([doc.updatedAt, firstSave.updatedAt]);
  });

  test('backend save failure keeps active record and unsaved field values visible', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await openInvoiceBuilder(page);
    const doc = await createDocumentByApi(page, { ...invoiceProfile, customerName: 'Injected Failure Customer', projectName: 'Injected Failure Original' });
    await openRecord(page, doc.docNumber, doc.id);
    await setField(page, '#projectName', 'Injected Failure Unsaved Edit');

    await page.route('**/api/documents/**', async route => {
      const request = route.request();
      if (request.method() === 'PUT' && new URL(request.url()).pathname === `/api/documents/${doc.id}`) {
        await route.fulfill({
          status: 500,
          contentType: 'application/json',
          body: JSON.stringify({ message: 'Injected invoice save failure' }),
        });
        return;
      }
      await route.continue();
    });

    await page.locator('#btnSaveDraft').click();
    await expect(page.locator('#toast-container')).toContainText('Save failed');
    await expect(page.locator('#activeRecordText')).toContainText(doc.docNumber);
    await expect(page.locator('#projectName')).toHaveValue('Injected Failure Unsaved Edit');

    const persisted = await page.request.get(`/api/documents/${doc.id}`).then(resp => resp.json());
    expect(persisted.projectName).toBe('Injected Failure Original');

    const failureText = await latestToastText(page);
    expect(failureText).toContain('Injected invoice save failure');
    expect(failures.filter(message =>
      !message.includes('Injected invoice save failure') &&
      !message.includes('500 (Internal Server Error)') &&
      !message.includes('/api/documents/'))).toEqual([]);
  });

  test('latest Open or New intent wins when an earlier record load finishes late', async ({ page }) => {
    await openInvoiceBuilder(page);
    const first = await createDocumentByApi(page, {
      ...estimateProfile,
      customerName: 'Intent Race First Customer',
      projectName: 'Intent Race First Project',
    });
    const second = await createDocumentByApi(page, {
      ...invoiceProfile,
      customerName: 'Intent Race Second Customer',
      projectName: 'Intent Race Second Project',
    });

    let activeGate = null;
    await page.route('**/api/documents/*', async route => {
      const request = route.request();
      const path = new URL(request.url()).pathname;
      if (request.method() === 'GET' && path === `/api/documents/${first.id}` && activeGate) {
        const gate = activeGate;
        activeGate = null;
        gate.started();
        await gate.release;
      }
      await route.continue();
    });
    const armFirstLoad = () => {
      let started;
      let release;
      const startedPromise = new Promise(resolve => { started = resolve; });
      const releasePromise = new Promise(resolve => { release = resolve; });
      activeGate = { started, release: releasePromise };
      return { started: startedPromise, release };
    };

    await page.locator('button.nav-item[data-view="records"]').click();
    await page.locator('#recSearch').fill('Intent Race');
    const firstGate = armFirstLoad();
    await recordRow(page, first.docNumber).getByRole('button', { name: 'Open' }).click();
    await firstGate.started;
    await recordRow(page, second.docNumber).getByRole('button', { name: 'Open' }).click();
    await expect(page.locator('#activeRecordText')).toContainText(second.docNumber);
    firstGate.release();
    await expect(page.locator('#projectName')).toHaveValue('Intent Race Second Project');
    await page.waitForTimeout(150);
    await expect(page.locator('#activeRecordText')).toContainText(second.docNumber);

    await page.locator('button.nav-item[data-view="records"]').click();
    await page.locator('#recSearch').fill(first.docNumber);
    const newGate = armFirstLoad();
    await recordRow(page, first.docNumber).getByRole('button', { name: 'Open' }).click();
    await newGate.started;
    await page.locator('#view-records .view-actions button', { hasText: '+ New Invoice' }).click();
    await expect(page.locator('#view-builder')).toBeVisible();
    await expect(page.locator('#docType')).toHaveValue('INVOICE');
    await expect(page.locator('#activeRecordText')).toContainText('New document');
    newGate.release();
    await page.waitForTimeout(150);
    await expect(page.locator('#docType')).toHaveValue('INVOICE');
    await expect(page.locator('#customerName')).toHaveValue('');
    await expect(page.locator('#activeRecordText')).toContainText('New document');
  });

  test('initial builder route announces the requested invoice type only after it is applied', async ({ page }) => {
    await openInvoiceBuilder(page);

    const events = await page.evaluate(async () => {
      const observed = [];
      const listener = event => observed.push({ ...event.detail });
      window.addEventListener('epata:invoice-view-changed', listener);
      try {
        const module = await import('/invoice-builder/js/app.js?v=41');
        await module.init({
          initialView: 'builder',
          newType: 'INVOICE',
          prefill: { docType: 'INVOICE', customerName: 'Route Timing Customer' },
        });
      } finally {
        window.removeEventListener('epata:invoice-view-changed', listener);
      }
      return observed;
    });

    expect(events).toEqual([{ view: 'builder', docType: 'INVOICE' }]);
    await expect(page.locator('#docType')).toHaveValue('INVOICE');
    await expect(page.locator('#customerName')).toHaveValue('Route Timing Customer');
  });

  test('immediate Ctrl+S during delayed startup saves the visible draft without late overwrite', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    let releaseStartupHealth;
    const startupHealthGate = new Promise(resolve => { releaseStartupHealth = resolve; });
    let healthRequests = 0;
    const documentPosts = [];

    await page.route('**/api/health', async route => {
      healthRequests += 1;
      if (healthRequests === 1) await startupHealthGate;
      await route.continue();
    });
    page.on('request', request => {
      const url = new URL(request.url());
      if (request.method() === 'POST' && url.pathname === '/api/documents') {
        documentPosts.push(request.postDataJSON());
      }
    });

    try {
      await page.goto('/invoice-builder/index.html');
      await page.locator('button.nav-item[data-view="builder"]').click();
      await expect(page.locator('#view-builder')).toBeVisible();
      await expect.poll(() => healthRequests).toBe(1);

      const initialDate = await page.locator('#docDate').inputValue();
      const initialDueDate = await page.locator('#dueDate').inputValue();
      expect(initialDate).toMatch(/^\d{4}-\d{2}-\d{2}$/);
      expect(initialDueDate).toMatch(/^\d{4}-\d{2}-\d{2}$/);

      await page.locator('#customerName').fill('Immediate Startup Save Customer');
      const firstLine = page.locator('#lineItemsBody tr').first();
      await firstLine.locator('.item-desc').fill('Immediate startup print');
      await firstLine.locator('.item-qty').fill('2');
      await firstLine.locator('.item-rate').fill('12.50');

      const savedResponse = waitForDocumentSave(page, 'POST');
      await page.keyboard.press('Control+s');
      const saved = await savedResponse;
      await waitForVisibleSavedState(page, saved);

      expect(documentPosts).toHaveLength(1);
      expect(documentPosts[0].customerName).toBe('Immediate Startup Save Customer');
      expect(documentPosts[0].docDate).toBe(initialDate);
      expect(documentPosts[0].dueDate).toBe(initialDueDate);
      expect(documentPosts[0].lineItems[0]).toMatchObject({
        description: 'Immediate startup print',
        quantity: 2,
        rate: 12.5,
      });

      const startupFinished = page.waitForResponse(response =>
        new URL(response.url()).pathname === '/api/documents/stats' &&
        response.request().method() === 'GET' &&
        response.status() < 400,
      );
      releaseStartupHealth();
      await startupFinished;

      await expect(page.locator('#customerName')).toHaveValue('Immediate Startup Save Customer');
      await expect(firstLine.locator('.item-desc')).toHaveValue('Immediate startup print');
      await expect(firstLine.locator('.item-qty')).toHaveValue('2');
      await expect(firstLine.locator('.item-rate')).toHaveValue('12.5');
      await expect(page.locator('#docDate')).toHaveValue(initialDate);
      await expect(page.locator('#dueDate')).toHaveValue(initialDueDate);
      await expect(page.locator('#docNumber')).toHaveValue(saved.docNumber);
      await expect.poll(() => documentPosts.length).toBe(1);

      const persisted = await page.request.get(`/api/documents/${saved.id}`).then(response => response.json());
      expect(persisted.customerName).toBe('Immediate Startup Save Customer');
      expect(persisted.docDate).toBe(initialDate);
      expect(persisted.dueDate).toBe(initialDueDate);
      expect(persisted.lineItems[0]).toMatchObject({
        description: 'Immediate startup print',
        quantity: 2,
        rate: 12.5,
      });
      expect(failures).toEqual([]);
    } finally {
      releaseStartupHealth();
    }
  });

  test('both calculator Push to Builder controls transfer the live calculation without duplicate placeholder rows', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await openInvoiceBuilder(page);
    await page.locator('button.nav-item[data-view="calculator"]').click();
    await expect(page.locator('#view-calculator')).toBeVisible();

    for (const [selector, value] of [
      ['#grams', '100'], ['#gramRate', '0.05'],
      ['#hours', '2'], ['#hourRate', '3'],
      ['#designHours', '1'], ['#designRate', '25'],
      ['#setupFee', '10'], ['#postFee', '4'],
      ['#rush', '25'], ['#discount', '5'],
      ['#taxRate', '7'], ['#minimum', '0'],
    ]) {
      await page.locator(selector).fill(value);
    }
    await page.locator('#difficultyGrid .diff-card[data-val="1.2"]').click();
    await expect(page.locator('#totalOut')).toHaveText('$74.90');

    await page.locator('#btnPushToBuilder').click();
    await expect(page.locator('#view-builder')).toBeVisible();
    await expect(page.locator('#toast-container')).toContainText('Calculator values pushed to builder');
    await expect(page.locator('#docDiscount')).toHaveValue('5.00');
    await expect(page.locator('#docRushPercent')).toHaveValue('25');
    await expect(page.locator('#docTaxRate')).toHaveValue('7');
    await expect(page.locator('#bTotal')).toHaveText('$74.90');

    const firstPush = await page.locator('#lineItemsBody tr').evaluateAll(rows => rows.map(row => ({
      description: row.querySelector('.item-desc')?.value,
      quantity: Number(row.querySelector('.item-qty')?.value),
      rate: Number(row.querySelector('.item-rate')?.value),
      source: row.dataset.source,
    })));
    expect(firstPush).toHaveLength(6);
    expect(firstPush.map(line => line.description)).toEqual([
      'Print setup / file preparation',
      'Material usage',
      'Machine print time',
      'Design / modeling time',
      'Post-processing / handling',
      'Material / difficulty surcharge',
    ]);
    expect(firstPush.find(line => line.description === 'Material usage')).toMatchObject({ quantity: 100, rate: 0.05, source: 'calculator' });

    await page.locator('button.nav-item[data-view="calculator"]').click();
    await page.locator('#grams').fill('120');
    await page.locator('#btnPushToBuilderCard').click();
    await expect(page.locator('#view-builder')).toBeVisible();
    const pushedRows = page.locator('#lineItemsBody tr');
    await expect(pushedRows).toHaveCount(6);
    const materialIndex = await pushedRows.evaluateAll(rows => rows.findIndex(row => row.querySelector('.item-desc')?.value === 'Material usage'));
    expect(materialIndex).toBeGreaterThanOrEqual(0);
    const materialRow = pushedRows.nth(materialIndex);
    await expect(materialRow.locator('.item-qty')).toHaveValue('120');
    expect(await page.evaluate(() => window._invoiceToolIsDirty())).toBe(true);

    await page.locator('#customerName').fill('Calculator Push Customer');
    const saved = await clickSaveAndWait(page, 'POST');
    const persisted = await page.request.get(`/api/documents/${saved.id}`).then(response => response.json());
    expect(persisted.lineItems).toHaveLength(6);
    expect(persisted.lineItems.map(line => line.sortOrder)).toEqual([1, 2, 3, 4, 5, 6]);
    expect(failures).toEqual([]);
  });

  test('an edit made during save is persisted by one trailing save', async ({ page }) => {
    await openInvoiceBuilder(page);
    const doc = await createDocumentByApi(page, {
      ...invoiceProfile,
      customerName: 'Trailing Save Customer',
      projectName: 'Trailing Save Original',
    });
    await openRecord(page, doc.docNumber, doc.id);

    let releaseFirst;
    let releaseThird;
    let firstStartedResolve;
    let secondStartedResolve;
    let thirdStartedResolve;
    const firstStarted = new Promise(resolve => { firstStartedResolve = resolve; });
    const secondStarted = new Promise(resolve => { secondStartedResolve = resolve; });
    const thirdStarted = new Promise(resolve => { thirdStartedResolve = resolve; });
    const release = new Promise(resolve => { releaseFirst = resolve; });
    const thirdRelease = new Promise(resolve => { releaseThird = resolve; });
    const putBodies = [];
    await page.route('**/api/documents/*', async route => {
      const request = route.request();
      if (request.method() === 'PUT' && new URL(request.url()).pathname === `/api/documents/${doc.id}`) {
        putBodies.push(request.postDataJSON());
        if (putBodies.length === 1) {
          firstStartedResolve();
          await release;
        } else if (putBodies.length === 2) {
          secondStartedResolve();
        } else if (putBodies.length === 3) {
          thirdStartedResolve();
          await thirdRelease;
        }
      }
      await route.continue();
    });

    await setField(page, '#projectName', 'Trailing Save First Revision');
    await page.locator('#btnSaveDraft').click();
    await firstStarted;
    await setField(page, '#projectName', 'Trailing Save Final Revision');
    await page.locator('#btnSaveDraft').click();
    releaseFirst();
    await secondStarted;
    await waitForNoSaveInFlight(page);

    expect(putBodies).toHaveLength(2);
    expect(putBodies[0].projectName).toBe('Trailing Save First Revision');
    expect(putBodies[1].projectName).toBe('Trailing Save Final Revision');
    const persisted = await page.request.get(`/api/documents/${doc.id}`).then(response => response.json());
    expect(persisted.projectName).toBe('Trailing Save Final Revision');
    await expect(page.locator('#activeRecordText')).not.toContainText('Unsaved changes');

    await setField(page, '#projectName', 'Explicit Save Snapshot');
    await page.locator('#btnSaveDraft').click();
    await thirdStarted;
    await setField(page, '#projectName', 'Edited After Last Save Click');
    releaseThird();
    await waitForNoSaveInFlight(page);
    await page.waitForTimeout(250);

    expect(putBodies).toHaveLength(3);
    const snapshotPersisted = await page.request.get(`/api/documents/${doc.id}`).then(response => response.json());
    expect(snapshotPersisted.projectName).toBe('Explicit Save Snapshot');
    await expect(page.locator('#projectName')).toHaveValue('Edited After Last Save Click');
    await expect(page.locator('#activeRecordText')).toContainText('Unsaved changes');
  });

  test('Download PDF reserves its print window before awaiting a delayed save', async ({ page }) => {
    await openInvoiceBuilder(page);
    const doc = await createDocumentByApi(page, {
      ...invoiceProfile,
      customerName: 'PDF Window Customer',
      projectName: 'PDF Window Original',
    });
    await openRecord(page, doc.docNumber, doc.id);

    let releaseSave;
    let saveStartedResolve;
    const saveStarted = new Promise(resolve => { saveStartedResolve = resolve; });
    const saveRelease = new Promise(resolve => { releaseSave = resolve; });
    await page.route('**/api/documents/*', async route => {
      const request = route.request();
      if (request.method() === 'PUT' && new URL(request.url()).pathname === `/api/documents/${doc.id}`) {
        saveStartedResolve();
        await saveRelease;
      }
      await route.continue();
    });

    await setField(page, '#projectName', 'PDF Window Saved');
    const popupPromise = page.waitForEvent('popup');
    await page.locator('#btnDownloadPdf').click();
    const popup = await popupPromise;
    await saveStarted;
    expect(popup.isClosed()).toBe(false);

    releaseSave();
    await waitForNoSaveInFlight(page);
    await expect.poll(() => popup.title()).toContain(doc.docNumber);
    await popup.close();
  });

  test('archiving the active record clears its identity and stale form before another save', async ({ page }) => {
    await openInvoiceBuilder(page);
    const doc = await createDocumentByApi(page, {
      ...estimateProfile,
      customerName: 'Archive Active Customer',
      projectName: 'Archive Active Original',
    });
    await openRecord(page, doc.docNumber, doc.id);
    await setField(page, '#projectName', 'Archive Active Unsaved Edit');
    await page.locator('button.nav-item[data-view="records"]').click();
    await page.locator('#recSearch').fill(doc.docNumber);

    page.once('dialog', async dialog => {
      expect(dialog.message()).toContain('unsaved edits');
      await dialog.accept();
    });
    const archivedResponse = page.waitForResponse(response =>
      response.request().method() === 'DELETE' &&
      new URL(response.url()).pathname === `/api/documents/${doc.id}` &&
      response.status() < 400,
    );
    await recordRow(page, doc.docNumber).locator(`button[data-record-action="archive"]`).click();
    await archivedResponse;

    await expect(page.locator('#activeRecordText')).toContainText('New document');
    await expect(page.locator('#activeRecordText')).not.toContainText(doc.docNumber);
    await expect(page.locator('#customerName')).toHaveValue('');
    await expect(page.locator('#projectName')).toHaveValue('');
    await expect.poll(() => page.evaluate(() => window._invoiceToolIsDirty?.())).toBe(false);
    await expect(page.locator('#docNumber')).toHaveValue(/^EST-\d{4}-\d{4}$/);
  });

  test('committed duplicate and conversion remain successful when records refresh fails', async ({ page }) => {
    await openInvoiceBuilder(page);
    const source = await createDocumentByApi(page, {
      ...estimateProfile,
      customerName: 'Post Commit Refresh Customer',
      projectName: 'Post Commit Refresh Estimate',
    });
    let failNextList = false;
    await page.route('**/api/documents**', async route => {
      const request = route.request();
      const url = new URL(request.url());
      if (failNextList && request.method() === 'GET' && url.pathname === '/api/documents') {
        failNextList = false;
        await route.fulfill({ status: 503, contentType: 'application/json', body: JSON.stringify({ message: 'Injected refresh failure' }) });
        return;
      }
      await route.continue();
    });

    await page.locator('button.nav-item[data-view="records"]').click();
    await page.locator('#recSearch').fill(source.docNumber);
    failNextList = true;
    const duplicateResponsePromise = page.waitForResponse(response =>
      response.request().method() === 'POST' &&
      new URL(response.url()).pathname === `/api/documents/${source.id}/duplicate` &&
      response.status() < 400,
    );
    await recordRow(page, source.docNumber).locator('button[data-record-action="duplicate"]').click();
    const duplicate = await (await duplicateResponsePromise).json();
    await expect(page.locator('#toast-container')).toContainText('The change was saved, but the records list could not refresh');
    const persistedDuplicate = await page.request.get(`/api/documents/${duplicate.id}`);
    expect(persistedDuplicate.ok()).toBe(true);

    await page.locator('#recSearch').fill(source.docNumber);
    failNextList = true;
    const conversionResponsePromise = page.waitForResponse(response =>
      response.request().method() === 'POST' &&
      new URL(response.url()).pathname === `/api/documents/${source.id}/convert-to-invoice` &&
      response.status() < 400,
    );
    await recordRow(page, source.docNumber).getByRole('button', { name: 'Create Invoice' }).click();
    const invoice = await (await conversionResponsePromise).json();
    await expect(page.locator('#view-builder')).toBeVisible();
    await expect(page.locator('#activeRecordText')).toContainText(invoice.docNumber);
    await expect(page.locator('#toast-container')).toContainText('The change was saved, but the records list could not refresh');
    const persistedInvoice = await page.request.get(`/api/documents/${invoice.id}`);
    expect(persistedInvoice.ok()).toBe(true);
  });

  test('repeated init replaces global shortcuts instead of multiplying save handlers', async ({ page }) => {
    await openInvoiceBuilder(page);
    const doc = await createDocumentByApi(page, {
      ...invoiceProfile,
      customerName: 'Init Idempotence Customer',
      projectName: 'Init Idempotence Original',
    });
    await openRecord(page, doc.docNumber, doc.id);

    let putCount = 0;
    await page.route('**/api/documents/*', async route => {
      const request = route.request();
      if (request.method() === 'PUT' && new URL(request.url()).pathname === `/api/documents/${doc.id}`) putCount += 1;
      await route.continue();
    });

    await page.evaluate(async () => {
      const module = await import('/invoice-builder/js/app.js?v=41');
      const snapshot = window._invoiceToolSnapshot();
      window._invoiceToolDispose();
      document.dispatchEvent(new KeyboardEvent('keydown', {
        key: 's', ctrlKey: true, bubbles: true, cancelable: true,
      }));
      await module.init({ initialView: 'builder', restoreSnapshot: snapshot });
      await module.init({ initialView: 'builder', restoreSnapshot: window._invoiceToolSnapshot() });
    });
    await expect(page.locator('#db-status-text')).toContainText('Ready');
    expect(putCount).toBe(0);
    await setField(page, '#projectName', 'Init Idempotence Edited');
    await page.keyboard.press('Control+s');
    await waitForNoSaveInFlight(page);
    await page.waitForTimeout(250);
    expect(putCount).toBe(1);
  });

  test('zero tax and zero calculator defaults remain explicit, and type changes stay Draft', async ({ page }) => {
    await openInvoiceBuilder(page);

    await setField(page, '#docStatus', 'Accepted');
    await setField(page, '#docType', 'INVOICE');
    await expect(page.locator('#docStatus')).toHaveValue('Draft');
    await expect(page.locator('#amountPaid')).toHaveValue('0');
    const nextInvoiceNumber = await page.request.get('/api/documents/next-number?type=INVOICE').then(response => response.json());
    await setField(page, '#docNumber', nextInvoiceNumber.number);

    await page.locator('button.nav-item[data-view="calculator"]').click();
    await setField(page, '#taxRate', '8.25');
    await page.locator('button.nav-item[data-view="builder"]').click();
    await setField(page, '#docTaxRate', '0');
    await setField(page, '#customerName', 'Explicit Zero Customer');
    await setField(page, '#projectName', 'Explicit Zero Project');
    await page.locator('#lineItemsBody tr').first().locator('.item-desc').fill('Explicit zero line');
    await page.locator('#lineItemsBody tr').first().locator('.item-rate').fill('100');
    const saved = await clickSaveAndWait(page, 'POST');
    expect(Number(saved.calcTaxRate)).toBe(0);
    expect(Number(saved.taxAmount)).toBe(0);

    const originalConfig = await page.request.get('/api/config').then(response => response.json());
    try {
      await page.locator('button.nav-item[data-view="settings"]').click();
      for (const selector of ['#sCalcGramRate', '#sCalcHourRate', '#sCalcDesignRate', '#sCalcMinimum']) {
        await setField(page, selector, '0');
      }
      const configResponsePromise = page.waitForResponse(response =>
        response.request().method() === 'PUT' && new URL(response.url()).pathname === '/api/config' && response.status() < 400,
      );
      await page.locator('#btnSaveSettings').click();
      const savedConfig = await (await configResponsePromise).json();
      expect(Number(savedConfig.calcGramRate)).toBe(0);
      expect(Number(savedConfig.calcHourRate)).toBe(0);
      expect(Number(savedConfig.calcDesignRate)).toBe(0);
      expect(Number(savedConfig.calcMinimum)).toBe(0);
    } finally {
      await page.request.put('/api/config', { data: originalConfig });
    }
  });
});
