const fs = require('node:fs');
const path = require('node:path');
const { expect, test } = require('@playwright/test');

test.describe.configure({ mode: 'serial' });

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

async function openStandaloneBuilder(page) {
  await page.goto('/invoice-builder/index.html');
  await expect(page.locator('#db-status-text')).toContainText('Ready');
  await page.locator('button.nav-item[data-view="builder"]').click();
  await expect(page.locator('#view-builder')).toBeVisible();
}

async function setField(page, selector, value) {
  const locator = page.locator(selector);
  const tag = await locator.evaluate(node => node.tagName);
  if (tag === 'SELECT') await locator.selectOption(String(value));
  else await locator.fill(String(value));
  await locator.dispatchEvent('input');
  await locator.dispatchEvent('change');
  await locator.dispatchEvent('blur');
}

async function setLineItems(page, items) {
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
    await row.locator('.item-details').fill(items[i].details || '');
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
  await setField(page, '#docNumber', profile.docNumber);
  await setField(page, '#docDate', profile.docDate);
  await setField(page, '#dueDate', profile.dueDate);
  await setField(page, '#customerName', profile.customerName);
  await setField(page, '#preparedFor', profile.preparedFor || profile.customerName);
  await setField(page, '#customerPhone', profile.customerPhone || '(555) 111-2222');
  await setField(page, '#customerEmail', profile.customerEmail || 'playwright@example.test');
  await setField(page, '#customerAddress', profile.customerAddress || '1 Browser Test Ave, Testville, NJ 07001');
  await setField(page, '#projectName', profile.projectName);
  await setField(page, '#material', profile.material || 'PLA');
  await setField(page, '#color', profile.color || 'Black');
  await setField(page, '#infill', profile.infill || '20%');
  await setField(page, '#projectDescription', profile.projectDescription || 'Browser closure document.');
  await setField(page, '#projectNotes', profile.projectNotes || 'Closure test private note.');
  await setLineItems(page, profile.lineItems);
  await setField(page, '#docDiscount', profile.discount ?? '0');
  await setField(page, '#docRushPercent', profile.rushPercent ?? '0');
  await setField(page, '#docTaxRate', profile.taxRate ?? '0');
  await setField(page, '#amountPaid', profile.amountPaid ?? '0');
  await setField(page, '#paymentMethod', profile.paymentMethod || 'Unknown / Review');
  await setField(page, '#pricingGuide', profile.pricingGuide || 'Closure pricing guide.');
  await setField(page, '#termsNotes', profile.termsNotes || 'Closure terms and notes.');
  await setField(page, '#standardTurnaround', profile.standardTurnaround || 'Standard closure turnaround.');
  await setField(page, '#rushTurnaround', profile.rushTurnaround || 'Rush closure turnaround.');
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

async function saveDraft(page, expectedMethod = null) {
  const savePromise = waitForDocumentSave(page, expectedMethod);
  await page.locator('#btnSaveDraft').click();
  const saved = await savePromise;
  await expect(page.locator('#db-status-text')).toContainText('Ready');
  return saved;
}

async function documentCount(page, docNumber) {
  const response = await page.request.get(`/api/documents?includeArchived=true&q=${encodeURIComponent(docNumber)}`);
  expect(response.ok()).toBe(true);
  const rows = await response.json();
  return rows.filter(row => row.docNumber === docNumber).length;
}

async function expectPreviewText(page, text) {
  await expect.poll(async () => page.locator('#invoicePreviewFrame').evaluate(frame =>
    frame.contentDocument?.body?.innerText || frame.srcdoc || '',
  )).toContain(text);
}

async function capturePreviewClip(page) {
  const frame = page.locator('#invoicePreviewFrame');
  await frame.scrollIntoViewIfNeeded();
  const box = await frame.boundingBox();
  if (!box) throw new Error('Invoice preview frame was not visible for screenshot capture.');
  return page.screenshot({
    animations: 'disabled',
    clip: {
      x: Math.max(0, box.x),
      y: Math.max(0, box.y),
      width: Math.max(1, Math.min(box.width, 900)),
      height: Math.max(1, Math.min(box.height, 1100)),
    },
  });
}

async function renderTextPdf(page, outputPath, text) {
  const pdfPage = await page.context().newPage();
  await pdfPage.setContent(`<!doctype html>
    <html>
      <head>
        <meta charset="utf-8">
        <style>
          body { font-family: Arial, sans-serif; font-size: 16px; line-height: 1.45; padding: 36px; }
          pre { white-space: pre-wrap; font-family: Arial, sans-serif; }
        </style>
      </head>
      <body><pre>${escapeHtml(text)}</pre></body>
    </html>`);
  await pdfPage.pdf({ path: outputPath, format: 'Letter', printBackground: true });
  await pdfPage.close();
}

async function renderPreviewDocument(page, data) {
  await page.evaluate(async documentData => {
    const { renderInvoiceHtml } = await import('/invoice-builder/js/pdf.js');
    const frame = document.querySelector('#invoicePreviewFrame');
    frame.srcdoc = renderInvoiceHtml(documentData);
  }, data);
}

function escapeHtml(value) {
  return String(value)
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

function pdfSourceText(profile) {
  const total = profile.lineItems.reduce((sum, item) => sum + Number(item.quantity) * Number(item.rate), 0);
  const discount = Number(profile.discount || 0);
  const tax = Number(profile.tax || 0);
  const grand = Math.max(0, total - discount + tax);
  const amountPaid = Number(profile.amountPaid || 0);
  return `${profile.docType === 'INVOICE' ? 'INVOICE' : 'ESTIMATE'}
${profile.docType === 'INVOICE' ? 'Invoice #' : 'Estimate #'} ${profile.docNumber}
Date ${profile.prettyDate}
${profile.docType === 'INVOICE' ? 'Due Date' : 'Valid Until'} ${profile.prettyDueDate}
Prepared For ${profile.preparedFor}

Bill To
${profile.customerName}
${profile.customerPhone}
${profile.customerEmail}
${profile.customerAddress}

Project Details
Project Name ${profile.projectName}
Description ${profile.projectDescription}
Material ${profile.material}
Color ${profile.color}
Infill ${profile.infill}

Pricing Summary
Subtotal $${total.toFixed(2)}
Discount -$${discount.toFixed(2)}
Tax (0%) $${tax.toFixed(2)}
${profile.docType === 'INVOICE' ? 'Invoice Total' : 'Estimated Total'} $${grand.toFixed(2)}

${profile.docType === 'INVOICE' ? 'INVOICE' : 'ESTIMATE'} Breakdown
# Description Calculation / Details Qty Rate Amount
${profile.lineItems.map((line, index) => `${index + 1} ${line.description} - ${line.details || 'Reviewed imported line'} ${line.quantity} $${Number(line.rate).toFixed(2)} $${(Number(line.quantity) * Number(line.rate)).toFixed(2)}`).join('\n')}

Pricing Guide
Recovered pricing guide

Terms & Notes
Recovered terms and notes

Payment Status
Status ${profile.status}
Amount Paid $${amountPaid.toFixed(2)}
Balance Due $${Math.max(0, grand - amountPaid).toFixed(2)}
`;
}

async function importPdfAndVerify(page, profile, pdfPath) {
  await page.goto('/invoice-builder/index.html');
  await expect(page.locator('#db-status-text')).toContainText('Ready');
  await expect(documentCount(page, profile.docNumber)).resolves.toBe(0);

  const importPromise = page.waitForResponse(resp =>
    resp.url().includes('/api/ai/invoice-document-draft/upload') &&
    resp.request().method() === 'POST' &&
    resp.status() < 400,
  );
  await page.locator('#invoicePdfImportFile').setInputFiles(pdfPath);
  const imported = await (await importPromise).json();

  expect(imported.prefill.docNumber).toBe(profile.docNumber);
  expect(imported.prefill.docType).toBe(profile.docType);
  await expect(page.locator('#view-builder')).toBeVisible();
  await expect(page.locator('#docNumber')).toHaveValue(profile.docNumber);
  await expect(page.locator('#docType')).toHaveValue(profile.docType);
  await expect(page.locator('#docStatus')).toHaveValue(profile.expectedStatus || profile.status);
  await expect(page.locator('#customerName')).toHaveValue(profile.customerName);
  await expect(page.locator('#projectName')).toHaveValue(profile.projectName);
  await expect(page.locator('#material')).toHaveValue(profile.material);
  await expect(page.locator('#color')).toHaveValue(profile.color);
  await expect(page.locator('#infill')).toHaveValue(profile.infill);
  await expect(page.locator('#projectDescription')).toHaveValue(new RegExp(profile.projectDescription));
  await expect(page.locator('#lineItemsBody .item-desc').first()).toHaveValue(profile.lineItems[0].description);
  await expect(documentCount(page, profile.docNumber)).resolves.toBe(0);

  await setField(page, '#paymentMethod', profile.paymentMethod);
  const saved = await saveDraft(page, 'POST');
  expect(saved.docNumber).toBe(profile.docNumber);
  expect(saved.docType).toBe(profile.docType);
  await expect(documentCount(page, profile.docNumber)).resolves.toBe(1);
  return saved;
}

async function createJson(page, route, body) {
  const response = await page.request.post(route, { data: body });
  if (!response.ok()) {
    throw new Error(`${route} failed: ${await response.text().catch(() => '')}`);
  }
  return response.json();
}

async function showMainPage(page, pageId) {
  await page.goto('/');
  await expect.poll(() => page.evaluate(() => typeof window.showPage)).toBe('function');
  await page.evaluate(target => window.showPage(target), pageId);
  await expectReady(page);
}

async function uploadDocuments(page, files) {
  const responsePromise = page.waitForResponse(resp =>
    resp.url().includes('/api/documents/upload') &&
    resp.request().method() === 'POST',
  );
  await page.locator('#docFiles').setInputFiles(files);
  await page.locator('#uploadDocsBtn').click();
  return responsePromise;
}

async function cleanupUploadedDocs(docs = []) {
  for (const doc of docs) {
    const filePath = doc.filePathOrUrl;
    if (!filePath || !path.basename(filePath).includes('playwright-final')) continue;
    try {
      fs.rmSync(filePath, { force: true });
    } catch {
      // Best effort cleanup for files created by this disposable browser test.
    }
  }
}

async function expectReadableContrast(page) {
  const lowContrast = await page.evaluate(() => {
    function parseRgb(value) {
      const match = String(value || '').match(/rgba?\((\d+),\s*(\d+),\s*(\d+)(?:,\s*([.\d]+))?\)/);
      if (!match) return null;
      return {
        r: Number(match[1]),
        g: Number(match[2]),
        b: Number(match[3]),
        a: match[4] === undefined ? 1 : Number(match[4]),
      };
    }
    function luminance({ r, g, b }) {
      const channel = [r, g, b].map(v => {
        const c = v / 255;
        return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
      });
      return (0.2126 * channel[0]) + (0.7152 * channel[1]) + (0.0722 * channel[2]);
    }
    function contrast(a, b) {
      const l1 = luminance(a);
      const l2 = luminance(b);
      return (Math.max(l1, l2) + 0.05) / (Math.min(l1, l2) + 0.05);
    }
    function effectiveBackground(element) {
      for (let node = element; node; node = node.parentElement) {
        const parsed = parseRgb(getComputedStyle(node).backgroundColor);
        if (parsed && parsed.a > 0.9) return parsed;
      }
      return parseRgb(getComputedStyle(document.body).backgroundColor) || { r: 255, g: 255, b: 255 };
    }
    return [...document.querySelectorAll('body *')]
      .filter(el => {
        const rect = el.getBoundingClientRect();
        const style = getComputedStyle(el);
        if (el.closest('.muted, .badge, small, .field-hint, .empty-desc, .help-text')) return false;
        return rect.width > 0 &&
          rect.height > 0 &&
          style.visibility !== 'hidden' &&
          style.display !== 'none' &&
          (el.textContent || '').trim().length >= 3 &&
          (['H1', 'H2', 'H3', 'BUTTON', 'LABEL', 'INPUT', 'TEXTAREA', 'SELECT', 'TD', 'TH'].includes(el.tagName) ||
            el.matches('.card p, .workspace-hero p, .kpi-card strong, .summary-line, .table-wrap td, .table-wrap th'));
      })
      .slice(0, 220)
      .map(el => {
        const fg = parseRgb(getComputedStyle(el).color);
        const bg = effectiveBackground(el);
        return fg && bg ? { text: (el.textContent || '').trim().slice(0, 80), ratio: contrast(fg, bg) } : null;
      })
      .filter(Boolean)
      .filter(item => item.ratio < 2.2)
      .slice(0, 10);
  });
  expect(lowContrast).toEqual([]);
}

const importedInvoice = {
  docType: 'INVOICE',
  docNumber: 'INV-2026-9701',
  status: 'Paid',
  expectedStatus: 'Paid',
  prettyDate: 'June 1, 2026',
  prettyDueDate: 'June 8, 2026',
  customerName: 'Browser Imported Invoice Customer',
  preparedFor: 'Browser Imported Invoice Customer',
  customerPhone: '(555) 970-1001',
  customerEmail: 'imported.invoice@example.test',
  customerAddress: '9701 Invoice Recovery Road, Testville, NJ 07001',
  projectName: 'Recovered Paid Invoice Fixture',
  projectDescription: 'Imported invoice field mapping proof.',
  material: 'PETG',
  color: 'Blue',
  infill: '35%',
  lineItems: [{ description: 'Recovered paid invoice line', details: 'PDF text recovered item', quantity: 2, rate: 55 }],
  amountPaid: 110,
  paymentMethod: 'PayPal',
};

const importedEstimate = {
  docType: 'ESTIMATE',
  docNumber: 'EST-2026-9702',
  status: 'Sent',
  expectedStatus: 'Sent',
  prettyDate: 'June 2, 2026',
  prettyDueDate: 'June 16, 2026',
  customerName: 'Browser Imported Estimate Customer',
  preparedFor: 'Browser Imported Estimate Customer',
  customerPhone: '(555) 970-2002',
  customerEmail: 'imported.estimate@example.test',
  customerAddress: '9702 Estimate Recovery Road, Testville, NJ 07002',
  projectName: 'Recovered Estimate Fixture',
  projectDescription: 'Imported estimate field mapping proof.',
  material: 'PLA',
  color: 'Red',
  infill: '20%',
  lineItems: [{ description: 'Recovered estimate line', details: 'PDF text recovered estimate item', quantity: 1, rate: 75 }],
  amountPaid: 0,
  paymentMethod: 'Unknown / Review',
};

test.describe('final checklist closure browser pass', () => {
  test.setTimeout(180_000);

  test.beforeEach(async ({}, testInfo) => {
    test.skip(testInfo.project.name !== 'chromium-desktop', 'Final checklist closure runs once on desktop Chromium.');
  });

  test('uploads existing EPATA invoice and estimate PDFs, verifies mapped fields, then saves', async ({ page }, testInfo) => {
    const failures = collectBrowserFailures(page);
    const invoicePdf = testInfo.outputPath('playwright-final-imported-invoice.pdf');
    const estimatePdf = testInfo.outputPath('playwright-final-imported-estimate.pdf');
    await renderTextPdf(page, invoicePdf, pdfSourceText(importedInvoice));
    await renderTextPdf(page, estimatePdf, pdfSourceText(importedEstimate));

    const savedInvoice = await importPdfAndVerify(page, importedInvoice, invoicePdf);
    expect(savedInvoice.status).toBe('Paid');
    const sales = await page.request.get('/api/sales').then(resp => resp.json());
    expect(sales.some(row => row.invoiceNumber === importedInvoice.docNumber && row.paymentMethod === importedInvoice.paymentMethod)).toBe(true);

    const savedEstimate = await importPdfAndVerify(page, importedEstimate, estimatePdf);
    expect(savedEstimate.status).toBe('Sent');
    const receivables = await page.request.get('/api/receivable-invoices').then(resp => resp.json());
    expect(receivables.some(row => row.invoiceNumber === importedEstimate.docNumber)).toBe(false);

    expect(failures).toEqual([]);
  });

  test('rehearses live preview, browser print output, and page sizes without script errors', async ({ page }, testInfo) => {
    const failures = collectBrowserFailures(page);
    await openStandaloneBuilder(page);
    await fillDocument(page, {
      docType: 'INVOICE',
      status: 'Partial',
      pageSize: 'LETTER',
      docNumber: 'INV-2026-9703',
      docDate: '2026-06-03',
      dueDate: '2026-06-10',
      customerName: 'Print Workflow Customer',
      preparedFor: 'Print Workflow Contact',
      projectName: 'Print Workflow Fixture',
      projectDescription: 'Preview and print workflow closure.',
      lineItems: [{ description: 'Printable line item', details: 'Letter A4 Legal coverage', quantity: 1, rate: 125 }],
      amountPaid: '25',
      paymentMethod: 'Check',
      termsNotes: 'Print workflow terms stay readable.',
    });
    await expectPreviewText(page, 'Print Workflow Customer');
    await expectPreviewText(page, 'Printable line item');
    await testInfo.attach('live-pdf-preview-letter.png', {
      body: await capturePreviewClip(page),
      contentType: 'image/png',
    });

    const pageSizeExpectations = [
      ['LETTER', 'size: letter portrait;', '--epata-page-h: 1325px;'],
      ['A4', 'size: A4 portrait;', '--epata-page-h: 1414px;'],
      ['LEGAL', 'size: legal portrait;', '--epata-page-h: 1688px;'],
    ];

    for (const [pageSize, printSize, heightCss] of pageSizeExpectations) {
      await setField(page, '#pageSize', pageSize);
      await expectPreviewText(page, 'Print Workflow Fixture');
      const popupPromise = page.waitForEvent('popup');
      await page.locator('#btnDownloadPdf').click();
      const popup = await popupPromise;
      await popup.waitForLoadState('domcontentloaded');
      await expect.poll(() => popup.content()).toContain('window.print()');
      const html = await popup.content();
      expect(html).toContain(printSize);
      expect(html).toContain(heightCss);
      expect(html).toContain('Printable line item');
      await popup.close();
    }

    expect(failures).toEqual([]);
  });

  test('captures invoice PDF preview screenshots for critical document variants', async ({ page }, testInfo) => {
    const failures = collectBrowserFailures(page);
    await openStandaloneBuilder(page);
    const variants = [
      { name: 'estimate', docType: 'ESTIMATE', status: 'Sent', docNumber: 'EST-2026-9710', amountPaid: '0' },
      { name: 'invoice', docType: 'INVOICE', status: 'Sent', docNumber: 'INV-2026-9711', amountPaid: '0' },
      { name: 'paid-invoice', docType: 'INVOICE', status: 'Paid', docNumber: 'INV-2026-9712', amountPaid: '90' },
      { name: 'partial-invoice', docType: 'INVOICE', status: 'Partial', docNumber: 'INV-2026-9713', amountPaid: '30' },
      {
        name: 'long-address',
        docType: 'INVOICE',
        status: 'Sent',
        docNumber: 'INV-2026-9714',
        customerAddress: '12345 Very Long Customer Address With Suite, Department, Building, Floor, Mail Stop, Testville, NJ 07001',
      },
      {
        name: 'long-terms',
        docType: 'ESTIMATE',
        status: 'Sent',
        docNumber: 'EST-2026-9715',
        termsNotes: 'Long terms paragraph. '.repeat(45),
      },
      {
        name: 'twenty-plus-lines',
        docType: 'INVOICE',
        status: 'Sent',
        docNumber: 'INV-2026-9716',
        lineItems: Array.from({ length: 22 }, (_, index) => ({
          description: `Line ${index + 1} production item`,
          details: 'Multi-page preview rehearsal',
          quantity: 1,
          rate: 5 + index,
        })),
      },
    ];

    for (const variant of variants) {
      const documentData = {
        pageSize: 'LETTER',
        docDate: '2026-06-04',
        dueDate: '2026-06-18',
        customerName: `Variant ${variant.name} Customer`,
        preparedFor: `Variant ${variant.name} Customer`,
        customerPhone: '(555) 111-2222',
        customerEmail: 'playwright@example.test',
        customerAddress: '1 Browser Test Ave, Testville, NJ 07001',
        projectName: `Variant ${variant.name} Project`,
        projectDescription: `Variant ${variant.name} PDF screenshot proof.`,
        material: 'PLA',
        color: 'Black',
        infill: '20%',
        lineItems: [{ description: `Variant ${variant.name} line`, details: 'Screenshot proof', quantity: 1, rate: 90 }],
        paymentMethod: 'Zelle',
        subtotal: 90,
        total: 90,
        balance: variant.status === 'Paid' ? 0 : 90,
        pricingGuide: 'Variant pricing guide.',
        termsNotes: 'Variant terms and notes.',
        standardTurnaround: 'Variant standard turnaround.',
        rushTurnaround: 'Variant rush turnaround.',
        businessName: 'EPATA 3D PRINTS',
        ...variant,
      };
      if (documentData.lineItems?.length) {
        documentData.subtotal = documentData.lineItems.reduce((sum, line) => sum + (Number(line.quantity || 0) * Number(line.rate || 0)), 0);
        documentData.total = documentData.subtotal;
        documentData.balance = documentData.status === 'Paid' ? 0 : documentData.total - Number(documentData.amountPaid || 0);
      }
      await renderPreviewDocument(page, documentData);
      await expectPreviewText(page, `Variant ${variant.name}`);
      await testInfo.attach(`invoice-preview-${variant.name}.png`, {
        body: await capturePreviewClip(page),
        contentType: 'image/png',
      });
    }

    expect(failures).toEqual([]);
  });

  test('covers sidebar, history, keyboard shortcut, records table state, and responsive layout', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    await page.goto('/');
    await expect(page.locator('#app')).toContainText('Dashboard');

    for (const pageId of ['sales', 'invoiceRecords', 'customerJobs', 'documentIntake']) {
      await page.locator(`button.nav-button[data-page="${pageId}"]`).click();
      await expectReady(page);
      await expect(page).toHaveURL(new RegExp(`#${pageId}$`));
    }
    await page.goBack();
    await expect(page).toHaveURL(/#customerJobs$/);
    await page.goBack();
    await expect(page).toHaveURL(/#invoiceRecords$/);
    await page.goForward();
    await expect(page).toHaveURL(/#customerJobs$/);

    await page.locator('button.nav-button[data-page="estimates"]').click();
    await expect(page.locator('#epataInvoiceMerged')).toBeVisible();
    await page.locator('#epataInvoiceMerged #customerName').fill('Keyboard Save Customer');
    await page.locator('#epataInvoiceMerged #lineItemsBody .item-desc').first().fill('Keyboard save line');
    const savePromise = page.waitForResponse(resp =>
      resp.url().includes('/api/documents') &&
      resp.request().method() === 'POST' &&
      resp.status() < 400,
    );
    await page.keyboard.press('Control+S');
    const shortcutSaved = await (await savePromise).json();
    expect(shortcutSaved.docNumber).toMatch(/^EST-\d{4}-\d{4}$/);

    for (let i = 0; i < 12; i++) {
      await createJson(page, '/api/documents', {
        docType: i % 2 === 0 ? 'ESTIMATE' : 'INVOICE',
        status: i % 2 === 0 ? 'Sent' : 'Partial',
        customerName: `Records Closure Customer ${String(i).padStart(2, '0')}`,
        projectName: `Records Closure Project ${String(i).padStart(2, '0')}`,
        docDate: '2026-06-05',
        dueDate: '2026-06-19',
        pageSize: 'LETTER',
        total: 10 + i,
        amountPaid: i,
        paymentMethod: 'Cash',
        lineItems: [{ sortOrder: 1, description: `Records Closure Line ${i}`, quantity: 1, rate: 10 + i }],
      });
    }

    await page.goto('/invoice-builder/index.html');
    await expect(page.locator('#db-status-text')).toContainText('Ready');
    await page.locator('button.nav-item[data-view="records"]').click();
    await expect(page.locator('#view-records')).toBeVisible();
    await page.locator('#recSearch').fill('Records Closure');
    await expect(page.locator('#recordsPager')).toContainText('of 12');
    await page.locator('#recPageSize').selectOption('10');
    await expect(page.locator('#recordsPager')).toContainText('Page 1 of 2');
    await page.locator('#recPageNext').click();
    await expect(page.locator('#recordsPager')).toContainText('Page 2 of 2');
    await page.locator('button[data-record-sort="customer"]').click();
    await expect(page.locator('#recordsBody tr').first()).toContainText('Records Closure Customer');
    await page.locator('#recType').selectOption('INVOICE');
    await expect(page.locator('#recCount')).toContainText('6 records');

    for (const viewport of [
      { width: 390, height: 844 },
      { width: 768, height: 1024 },
      { width: 1440, height: 900 },
    ]) {
      await page.setViewportSize(viewport);
      await expect(page.locator('#view-records')).toBeVisible();
      await expect(page.locator('#recordsBody')).toBeVisible();
    }

    expect(failures).toEqual([]);
  });

  test('runs document intake upload edge matrix through the real UI', async ({ page }, testInfo) => {
    const failures = collectBrowserFailures(page);
    await showMainPage(page, 'documentIntake');

    await page.locator('#uploadDocsBtn').click();
    await expect(page.locator('#toast-container')).toContainText('Choose a file first.');

    const zeroBytePath = testInfo.outputPath('playwright-final-zero-byte.txt');
    const oversizedPath = testInfo.outputPath('playwright-final-oversized.txt');
    fs.writeFileSync(zeroBytePath, '');
    fs.writeFileSync(oversizedPath, Buffer.alloc((20 * 1024 * 1024) + 1, 'x'));

    let response = await uploadDocuments(page, zeroBytePath);
    expect(response.status()).toBe(400);
    await expect(page.locator('#uploadResult')).toContainText('empty');

    response = await uploadDocuments(page, [{ name: 'playwright-final-wrong-extension.exe', mimeType: 'application/octet-stream', buffer: Buffer.from('not allowed') }]);
    expect(response.status()).toBe(400);
    await expect(page.locator('#uploadResult')).toContainText('not a supported proof upload type');

    response = await uploadDocuments(page, oversizedPath);
    expect(response.status()).toBe(400);
    await expect(page.locator('#uploadResult')).toContainText('larger than the 20 MB');

    const batchResponses = [];
    const captureBatchResponse = candidate => {
      if (candidate.url().includes('/api/documents/upload') && candidate.request().method() === 'POST') {
        batchResponses.push(candidate);
      }
    };
    page.on('response', captureBatchResponse);
    await page.locator('#docFiles').setInputFiles([
      { name: 'playwright-final duplicate.txt', mimeType: 'text/plain', buffer: Buffer.from('duplicate one') },
      { name: 'playwright-final duplicate.txt', mimeType: 'text/plain', buffer: Buffer.from('duplicate two') },
      { name: 'playwright-final spaces and parentheses (copy).md', mimeType: 'text/markdown', buffer: Buffer.from('# spaces') },
      { name: "playwright-final apostrophe's receipt.csv", mimeType: 'text/csv', buffer: Buffer.from('name,total\napostrophe,1') },
      { name: '..\\playwright-final-traversal-style.txt', mimeType: 'text/plain', buffer: Buffer.from('traversal style filename') },
    ]);
    await page.locator('#uploadDocsBtn').click();
    await expect(page.locator('#uploadResult')).toContainText('Uploaded and indexed 5 documents');
    await expect.poll(() => batchResponses.length).toBe(5);
    page.off('response', captureBatchResponse);
    const batchResults = await Promise.all(batchResponses.map(candidate => candidate.json()));
    const batchDocuments = batchResults.flatMap(result => result.documents || []);
    expect(batchResponses.every(candidate => candidate.ok())).toBe(true);
    expect(batchResults.reduce((sum, result) => sum + Number(result.count || 0), 0)).toBe(5);
    expect(batchDocuments).toHaveLength(5);
    expect(batchDocuments.every(doc => !String(doc.filePathOrUrl || '').includes('..'))).toBe(true);
    await cleanupUploadedDocs(batchDocuments);

    expect(failures.filter(failure => !failure.includes('400 (Bad Request)'))).toEqual([]);
  });

  test('runs AI Operations local-rules browser pass and open-record actions', async ({ page }) => {
    const failures = collectBrowserFailures(page);
    const product = await createJson(page, '/api/products', {
      name: 'Playwright Final Listing Product',
      sku: 'PW-FINAL-LISTING',
      category: '3D Printed Product',
      material: 'PLA',
      color: 'Black',
      grams: 42,
      printHours: 2.5,
      targetPrice: 35,
      needsReview: false,
    });
    const job = await createJson(page, '/api/customer-jobs', {
      jobDate: '2026-06-06',
      customerName: 'Playwright Final Job Customer',
      jobName: 'Playwright Final Job Plan',
      productName: 'Playwright Final Listing Product',
      status: 'Quoted',
      paymentMethod: 'Unknown / Review',
      description: 'Needs production planning and packing steps.',
    });
    const sale = await createJson(page, '/api/sales', {
      saleDate: '2026-06-06',
      platform: 'Direct',
      paymentMethod: 'Cash',
      salesTaxHandling: 'Seller collected and remitted',
      orderNumber: 'PW-FINAL-LEDGER',
      customerName: 'Playwright Final Ledger Customer',
      productName: 'Playwright Final Listing Product',
      quantity: 1,
      itemSales: 35,
      shippingCharged: 0,
      salesTaxCollected: 0,
      customerPaid: 35,
      platformFees: 0,
      shippingLabelCost: 0,
      refunds: 0,
      estimatedCogs: 4,
      status: 'Paid',
      includeInDashboard: true,
      needsReview: false,
    });
    expect(product.id).toBeGreaterThan(0);
    expect(job.id).toBeGreaterThan(0);
    expect(sale.id).toBeGreaterThan(0);

    await showMainPage(page, 'aiOperations');
    await expect(page.locator('#aiMarketplaceOrderCard')).toBeVisible();

    await page.locator('#aiMarketplaceOrderText').fill(`Etsy order PW-FINAL-MARKETPLACE
Order date 2026-06-06
Buyer Playwright Marketplace Buyer
Item Playwright Ornament
Quantity 1
Item sales $42.00
Shipping $5.00
Sales tax $3.00
Order total $50.00
Payment Etsy Payments`);
    let responsePromise = page.waitForResponse(resp => resp.url().includes('/api/ai/operations/marketplace-order-import') && resp.request().method() === 'POST');
    await page.locator('#runMarketplaceOrderImportBtn').click();
    expect((await responsePromise).ok()).toBe(true);
    await expect(page.locator('#aiMarketplaceOrderResult')).toContainText('Paid Sale');
    await page.locator('#openMarketplaceSaleDraftBtn').click();
    await expect(page.locator('#modal')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('#modal')).toBeHidden();

    await page.locator('#aiProductText').fill('Product: Playwright Final Product Import. SKU PW-IMPORT. PLA black fixture. 42 grams. 2.5 print hours. Target price $35.');
    responsePromise = page.waitForResponse(resp => resp.url().includes('/api/ai/operations/product-import') && resp.request().method() === 'POST');
    await page.locator('#runProductImportBtn').click();
    expect((await responsePromise).ok()).toBe(true);
    await expect(page.locator('#aiProductImportResult')).toContainText('Open Unsaved Product Draft');
    await page.locator('#openAiProductDraftBtn').click();
    await expect(page.locator('#modal')).toBeVisible();
    await page.keyboard.press('Escape');

    await page.locator('#aiJobSelect').selectOption(String(job.id));
    await page.locator('#aiJobText').fill('Plan printing, inspection, packaging, and customer handoff.');
    responsePromise = page.waitForResponse(resp => resp.url().includes('/api/ai/operations/job-plan') && resp.request().method() === 'POST');
    await page.locator('#runJobPlanBtn').click();
    expect((await responsePromise).ok()).toBe(true);
    await expect(page.locator('#aiJobPlanResult')).toContainText('Create These Action Items');

    await page.locator('#aiSlicerText').fill('Bambu slicer summary: PLA, black, 184.6g, print time 8h 42m, 2 plates, 4 copies.');
    responsePromise = page.waitForResponse(resp => resp.url().includes('/api/ai/operations/slicer-read') && resp.request().method() === 'POST');
    await page.locator('#runSlicerReadBtn').click();
    expect((await responsePromise).ok()).toBe(true);
    await expect(page.locator('#aiSlicerResult')).toContainText('Open Unsaved Product Draft');

    await page.locator('#aiListingProduct').selectOption(String(product.id));
    await page.locator('#aiListingPlatform').selectOption('Etsy');
    await page.locator('#aiListingInstructions').fill('Keep copy factual and concise.');
    responsePromise = page.waitForResponse(resp => resp.url().includes('/api/ai/operations/listing') && resp.request().method() === 'POST');
    await page.locator('#runListingWriterBtn').click();
    expect((await responsePromise).ok()).toBe(true);
    await expect(page.locator('#aiListingResult')).toContainText('Listing Preview');

    await page.locator('#aiLedgerQuestion').fill('Find Playwright Final Ledger Customer');
    responsePromise = page.waitForResponse(resp => resp.url().includes('/api/ai/operations/ask-ledger') && resp.request().method() === 'POST');
    await page.locator('#askLedgerBtn').click();
    expect((await responsePromise).ok()).toBe(true);
    await expect(page.locator('#aiLedgerAnswer')).toContainText('Playwright Final Ledger Customer');
    await page.locator('#aiLedgerAnswer button', { hasText: 'Open Record' }).first().click();
    await expect(page.locator('#modal')).toBeVisible();
    await expect(page.locator('#field_customerName')).toHaveValue('Playwright Final Ledger Customer');

    expect(failures).toEqual([]);
  });

  test('checks dark and light color-scheme readability on major app and invoice surfaces', async ({ page }, testInfo) => {
    const failures = collectBrowserFailures(page);
    for (const colorScheme of ['light', 'dark']) {
      await page.emulateMedia({ colorScheme });
      await showMainPage(page, 'dashboard');
      await testInfo.attach(`dashboard-${colorScheme}-contrast.png`, {
        body: await page.screenshot({ fullPage: true, animations: 'disabled' }),
        contentType: 'image/png',
      });

      await showMainPage(page, 'taxPrep');
      await testInfo.attach(`tax-prep-${colorScheme}-contrast.png`, {
        body: await page.screenshot({ fullPage: true, animations: 'disabled' }),
        contentType: 'image/png',
      });

      await openStandaloneBuilder(page);
      await fillDocument(page, {
        docType: 'INVOICE',
        status: 'Sent',
        pageSize: 'LETTER',
        docNumber: colorScheme === 'light' ? 'INV-2026-9720' : 'INV-2026-9721',
        docDate: '2026-06-07',
        dueDate: '2026-06-14',
        customerName: `${colorScheme} Contrast Customer`,
        projectName: `${colorScheme} Contrast Invoice`,
        lineItems: [{ description: `${colorScheme} contrast line`, details: 'Readable preview proof', quantity: 1, rate: 44 }],
        paymentMethod: 'Cash',
      });
      await expectPreviewText(page, `${colorScheme} Contrast Customer`);
      await testInfo.attach(`invoice-preview-${colorScheme}-contrast.png`, {
        body: await capturePreviewClip(page),
        contentType: 'image/png',
      });
    }
    expect(failures).toEqual([]);
  });
});
