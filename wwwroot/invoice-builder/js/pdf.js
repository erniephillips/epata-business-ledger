// EPATA Invoice Tool - HTML invoice renderer + browser PDF output.

import { money } from './utils.js';

const ASSET_ROOT = '/invoice-builder/img/invoice';
const REVIEW_URL = 'https://share.google/ME4Y7hOEFEg9ZoFRw';
const AI_USE_DISCLOSURE = 'AI-assisted tools may be used during design, development, or production; all final deliverables are reviewed and approved by EPATA LLC.';

export async function generatePdf(data, preview = false) {
  const html = renderInvoiceHtml(data, { autoPrint: !preview });
  const win = window.open('', preview ? 'epata_invoice_preview' : 'epata_invoice_print');
  if (!win) throw new Error('Popup blocked. Allow popups for this local app and try again.');

  win.document.open();
  win.document.write(html);
  win.document.close();
  win.focus();
}

export function renderInvoiceHtml(input, options = {}) {
  const d = normalizeData(input);
  const title = d.docType === 'INVOICE' ? 'INVOICE' : 'ESTIMATE';
  const docNumberLabel = d.docType === 'INVOICE' ? 'Invoice #' : 'Estimate #';
  const dueDateLabel = d.docType === 'INVOICE' ? 'Due Date' : 'Valid Until';
  const totalLabel = d.docType === 'INVOICE' ? 'Balance<br>Due' : 'Estimated<br>Total';
  const summaryTotalValue = d.docType === 'INVOICE' ? Math.max(0, d.balance) : d.total;
  const statusStamp = buildStatusStamp(d.status);
  const rows = buildRows(d);
  const autoPrintScript = options.autoPrint
    ? `<script>window.addEventListener('load',()=>setTimeout(()=>window.print(),250));<\/script>`
    : '';

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>${esc(d.docNumber || title)} - EPATA 3D Prints</title>
  ${invoiceStyles()}
</head>

<body class="bg-body-secondary text-body">
  <main class="epata-page position-relative bg-white overflow-hidden mx-auto shadow-lg">
    <header class="epata-header position-relative flex-shrink-0">
      <img class="top-logo position-absolute object-fit-contain z-3" alt="EPATA 3D Prints logo" src="${ASSET_ROOT}/epata-top-logo_new.png" />

      <h1 class="top-title position-absolute m-0 lh-1 text-nowrap z-3">${esc(d.businessName)}</h1>

      <div class="contact-block position-absolute d-flex flex-column gap-2 z-3 mt">
        ${contactRow('location', d.businessLocation)}
        ${contactRow('email', d.businessEmail)}
        ${contactRow('phone', d.businessPhone)}

        <div class="review-block">
          <img class="review-qr" src="${ASSET_ROOT}/google-review-qr.png" alt="QR code linking to Google review page" />
          <div class="review-copy">
            <div class="review-title">Rate me</div>
            <div class="review-help">Scan the QR code or click the link. Your rating helps my small business grow.</div>
            <a class="review-link" href="${REVIEW_URL}" target="_blank" rel="noopener noreferrer">${REVIEW_URL}</a>
          </div>
        </div>
      </div>

      <div class="doc-heading position-absolute text-uppercase text-nowrap">${title}</div>
      ${statusStamp}

      <div class="doc-meta position-absolute">
        ${metaRow(docNumberLabel, d.docNumber)}
        ${metaRow('Date', prettyDate(d.docDate))}
        ${metaRow(dueDateLabel, prettyDate(d.dueDate))}
        ${metaRow('Prepared For', d.preparedFor || d.customerName, false)}
      </div>

      <div class="epata-blue-line top-rule position-absolute z-3"></div>
    </header>

    <img class="center-watermark position-absolute object-fit-contain pe-none z-0" alt="EPATA center watermark" src="${ASSET_ROOT}/epata-center-watermark.png" />

    <div class="epata-content-space">
      <section class="estimate-content">
        <div class="row g-3 mb-4">
          <div class="col-3">
            <section class="estimate-panel h-100">
              <div class="panel-label">Bill To</div>
              <div class="panel-body">
                ${billTo(d)}
              </div>
            </section>
          </div>

          <div class="col-5">
            <section class="estimate-panel h-100">
              <div class="panel-label">Project Details</div>
              <div class="panel-body">
                <div class="detail-grid">
                  ${detailRow('Project Name:', d.projectName)}
                  ${detailRow('Description:', d.projectDescription)}
                  ${detailRow('Material:', d.material)}
                  ${detailRow('Color:', d.color)}
                  ${detailRow('Infill:', d.infill)}
                </div>
              </div>
            </section>
          </div>

          <div class="col-4">
            <section class="estimate-panel h-100">
              <div class="panel-label">Pricing Summary</div>
              <div class="panel-body">
                ${summaryRow('Subtotal', money(d.subtotal))}
                ${summaryRow('Discount', '-' + money(d.discountAmount))}
                ${summaryRow(`Rush Fee (${d.docRushPercent.toFixed(0)}%)`, money(d.rushAmount))}
                ${summaryRow(`Tax (${formatTaxRate(d.docTaxRate)}%)`, money(d.taxAmount))}
                <div class="d-flex align-items-end justify-content-between mt-3">
                  <div class="summary-total-label">${totalLabel}</div>
                  <div class="summary-total-value">${money(summaryTotalValue)}</div>
                </div>
              </div>
            </section>
          </div>
        </div>

        <section class="breakdown-card mb-4">
          <div class="breakdown-title">${title} Breakdown</div>
          <table class="table table-bordered estimate-table">
            <thead>
              <tr>
                <th class="num-col">#</th>
                <th>Description</th>
                <th>Calculation / Details</th>
                <th class="qty-col">Qty</th>
                <th class="rate-col">Rate</th>
                <th class="amount-col">Amount</th>
              </tr>
            </thead>
            <tbody>${rows}</tbody>
          </table>
        </section>

        <div class="row g-3">
          <div class="col-4">
            <section class="small-panel">
              <div class="panel-label">Pricing Guide (For Reference)</div>
              <div class="panel-body">
                ${structuredGuide(d.pricingGuide)}
              </div>
            </section>
          </div>

          <div class="col-4">
            <section class="small-panel">
              <div class="panel-label">Terms &amp; Notes</div>
              <div class="panel-body">
                ${bulletList(d.termsNotes, 'd-flex flex-column gap-3')}
                <div class="ai-use-disclosure">${esc(AI_USE_DISCLOSURE)}</div>
              </div>
            </section>
          </div>

          <div class="col-4">
            <section class="small-panel">
              <div class="panel-label">${actionPanelLabel(d)}</div>
              <div class="panel-body">
                ${actionPanel(d, title)}
              </div>
            </section>
          </div>
        </div>
      </section>
    </div>

    <footer class="epata-footer mt-auto">
      <div class="epata-blue-line bottom-rule mb-4"></div>

      <div class="footer-inner d-flex align-items-start gap-4">
        <section class="thanks-block">
          <div class="thanks text-nowrap">Thank You!</div>
          <div class="thanks-sub mt-3 lh-1 text-nowrap">We appreciate your business.</div>
        </section>

        <img class="printer-logo object-fit-contain flex-shrink-0" alt="Turnaround printer logo" src="${ASSET_ROOT}/turnaround-printer-logo_new.png" />

        <section class="turnaround-block">
          <div class="footer-heading mb-2 fw-bold text-uppercase lh-1 text-nowrap">TURNAROUND TIME (ESTIMATED)</div>
          <div class="footer-line mb-2 lh-sm"><strong class="fw-bold">Standard:</strong> ${esc(d.standardTurnaround)}</div>
          <div class="footer-line lh-sm"><strong class="fw-bold">Rush:</strong> ${esc(d.rushTurnaround)}</div>
        </section>

        <div class="connect-divider"></div>

        <section class="connect-block d-flex flex-column gap-2">
          <div class="connect-title fw-bold text-uppercase lh-1 text-nowrap mb-0">FOLLOW &amp; CONNECT</div>
          ${socialRow('makerworld', 'MakerWorld', d.businessMakerWorld)}
          ${socialRow('etsy', 'Etsy', displayWebsite(d.businessEtsy))}
          ${socialRow('website', 'Website', displayWebsite(d.businessWebsite))}
        </section>
      </div>
    </footer>
  </main>
  ${autoPrintScript}
</body>
</html>`;
}

function invoiceStyles() {
  return `<style>
    :root {
      --epata-blue: #17499b;
      --epata-text: #111111;
      --epata-page-w: 1024px;
      --epata-page-h: 1414px;
    }

    * { box-sizing: border-box; }

    html, body {
      margin: 0;
      background: #dfe5ef;
      color: var(--epata-text);
      font-family: Arial, Helvetica, sans-serif;
      -webkit-print-color-adjust: exact;
      print-color-adjust: exact;
    }

    .epata-page {
      width: var(--epata-page-w);
      min-height: var(--epata-page-h);
      height: auto;
      margin-top: 12px;
      margin-bottom: 36px;
      isolation: isolate;
      display: flex;
      flex-direction: column;
    }

    .epata-header {
      height: 286px;
      flex: 0 0 286px;
    }

    .epata-content-space {
      position: relative;
      flex: 1 0 auto;
      min-height: 1064px;
    }

    .epata-blue-line {
      height: 2px;
      background: var(--epata-blue);
    }

    .top-logo {
      left: 22px;
      top: 82px;
      width: 150px;
      height: 150px;
    }

    .top-title {
      left: 22px;
      top: 34px;
      font-family: Arial, Helvetica, sans-serif;
      font-size: 38px;
      font-weight: 900;
      letter-spacing: -1px;
      line-height: 1;
    }

    .contact-block {
      left: 200px;
      top: 92px;
    }

    .contact-row {
      font-size: 17px;
    }

    .contact-icon {
      width: 24px;
      height: 24px;
    }

    .review-block {
      display: flex;
      align-items: center;
      gap: 10px;
      margin-top: 6px;
      padding-top: 0;
      transform: translateY(-2px);
    }

    .review-qr {
      width: 52px;
      height: 52px;
      object-fit: contain;
      border: 1px solid #d6dbe5;
      background: #fff;
      padding: 2px;
      border-radius: 4px;
      flex: 0 0 auto;
    }

    .review-copy {
      min-width: 0;
      line-height: 1.15;
    }

    .review-title {
      font-size: 13px;
      font-weight: 800;
      color: #111;
      margin-bottom: 3px;
    }

    .review-help {
      font-size: 10.5px;
      color: #444;
      margin-bottom: 3px;
      max-width: 350px;
    }

    .review-link {
      display: inline-block;
      position: relative;
      z-index: 5;
      font-size: 10.5px;
      line-height: 1.15;
      color: var(--epata-blue);
      text-decoration: underline;
      word-break: break-all;
      pointer-events: auto;
    }

    .top-rule {
      left: 16px;
      top: 278px;
      width: 984px;
    }

    .center-watermark {
      left: 100px;
      top: 300px;
      width: 853px;
      height: 1078px;
      z-index: 2 !important;
      opacity: .98;
      filter: saturate(1.08) brightness(1.0);
      pointer-events: none;
    }

    .bottom-rule {
      width: 100%;
      flex: 0 0 auto;
    }

    .epata-footer {
      position: relative;
      z-index: 3;
      padding-left: 30px;
      padding-right: 30px;
      padding-bottom: 28px;
      margin-top: auto;
    }

    .footer-inner {
      min-height: 100px;
    }

    .thanks-block {
      width: 215px;
      flex: 0 0 215px;
    }

    .thanks {
      color: var(--epata-blue);
      font-size: 42px;
      line-height: .85;
      font-family: "Brush Script MT", "Segoe Script", "Lucida Handwriting", cursive;
      transform: rotate(-2deg);
    }

    .thanks-sub {
      font-size: 14px;
    }

    .printer-logo {
      width: 96px;
      height: 96px;
    }

    .turnaround-block {
      width: 260px;
      flex: 0 0 260px;
    }

    .footer-heading {
      font-size: 14px;
      font-weight: 900;
    }

    .footer-line {
      font-size: 14px;
      line-height: 1.2;
    }

    .connect-divider {
      width: 2px;
      min-height: 98px;
      background: var(--epata-blue);
      flex: 0 0 2px;
    }

    .connect-block {
      flex: 1 1 auto;
      min-width: 0;
    }

    .connect-title {
      font-size: 12px;
      font-weight: 900;
      margin-bottom: 0;
    }

    .social-row {
      font-size: 11px;
    }

    .social-icon {
      width: 20px;
      height: 20px;
    }

    strong {
      font-weight: 900;
    }

    .doc-heading {
      right: 30px;
      top: 34px;
      font-size: 38px;
      line-height: 1;
      font-weight: 900;
      color: var(--epata-blue);
      letter-spacing: -1px;
      z-index: 3;
    }

    .status-stamp {
      right: 30px;
      top: 150px;
      z-index: 4;
      border-radius: 8px;
      padding: 9px 22px 7px;
      min-width: 172px;
      text-align: center;
      font-size: 38px;
      line-height: 1;
      font-weight: 900;
      letter-spacing: 3px;
      transform: rotate(-8deg);
      background: rgba(255,255,255,.58);
    }

    .status-stamp-paid {
      color: #047857;
      border: 4px solid #047857;
    }

    .status-stamp-sent {
      right: 30px;
      top: 218px;
      color: #17499b;
      border: 3px solid #17499b;
      background: rgba(255,255,255,.64);
      font-size: 24px;
      min-width: 124px;
      padding: 6px 16px 5px;
      transform: rotate(0deg);
    }

    .status-stamp-draft {
      right: 30px;
      top: 218px;
      color: #475569;
      border: 3px solid #475569;
      background: rgba(255,255,255,.64);
      font-size: 24px;
      min-width: 124px;
      padding: 6px 16px 5px;
      transform: rotate(0deg);
    }

    .status-stamp-void {
      color: #b91c1c;
      border: 4px solid #b91c1c;
    }

    .doc-meta {
      right: 30px;
      top: 88px;
      width: 330px;
      font-size: 14px;
      z-index: 3;
    }

    .doc-meta-row {
      margin-bottom: 12px;
    }

    .doc-meta-label {
      color: #111;
    }

    .doc-meta-value {
      color: var(--epata-blue);
      font-weight: 900;
      text-align: right;
    }

    .estimate-content {
      position: relative;
      z-index: 3;
      padding: 28px 30px 28px;
    }

    .estimate-content * {
      position: relative;
      z-index: 3;
    }

    .estimate-panel {
      border: 1.8px solid var(--epata-blue);
      border-radius: 6px;
      background: rgba(255, 255, 255, .34);
      min-height: 150px;
      position: relative;
    }

    .panel-label {
      position: absolute;
      top: -1px;
      left: -1px;
      background: var(--epata-blue);
      color: #fff;
      font-size: 13px;
      font-weight: 900;
      line-height: 1;
      padding: 8px 13px;
      border-radius: 4px 0 4px 0;
      text-transform: uppercase;
    }

    .panel-body {
      padding: 36px 14px 14px;
      font-size: 14px;
    }

    .panel-body p {
      margin-bottom: 8px;
    }

    .detail-grid {
      display: grid;
      grid-template-columns: 130px 1fr;
      column-gap: 16px;
      row-gap: 9px;
      font-size: 13px;
      line-height: 1.25;
    }

    .summary-row {
      display: flex;
      justify-content: space-between;
      border-bottom: 1px solid #d6dfef;
      padding: 0 0 11px;
      margin-bottom: 11px;
      gap: 20px;
      font-size: 14px;
    }

    .summary-total-label {
      color: var(--epata-blue);
      font-weight: 900;
      line-height: 1;
      text-transform: uppercase;
    }

    .summary-total-value {
      color: var(--epata-blue);
      font-weight: 900;
      font-size: 28px;
      line-height: 1;
    }

    .breakdown-card {
      border: 1.8px solid var(--epata-blue);
      border-radius: 6px;
      background: rgba(255,255,255,.34);
      overflow: hidden;
    }

    .breakdown-title {
      background: var(--epata-blue);
      color: #fff;
      font-size: 14px;
      font-weight: 900;
      line-height: 1;
      padding: 9px 14px;
      text-transform: uppercase;
    }

    .estimate-table {
      margin: 0;
      font-size: 13px;
      border-color: #cfd9ea;
    }

    .estimate-table thead th {
      color: var(--epata-blue);
      font-weight: 900;
      text-transform: uppercase;
      background: rgba(255,255,255,.34);
      padding: 12px 12px;
      border-color: #cfd9ea;
    }

    .estimate-table tbody td {
      padding: 14px 12px;
      vertical-align: top;
      border-color: #cfd9ea;
      background: rgba(255,255,255,.34);
    }

    .estimate-table .num-col {
      width: 58px;
      text-align: center;
    }

    .estimate-table .qty-col,
    .estimate-table .rate-col,
    .estimate-table .amount-col {
      width: 95px;
      text-align: right;
      white-space: nowrap;
    }

    .small-panel {
      border: 1.8px solid var(--epata-blue);
      border-radius: 6px;
      background: rgba(255,255,255,.34);
      min-height: 235px;
      position: relative;
    }

    .small-panel .panel-body {
      font-size: 12px;
      padding-top: 36px;
    }

    .small-panel ul {
      padding-left: 16px;
      margin-bottom: 0;
    }

    .ai-use-disclosure {
      margin-top: 12px;
      padding-top: 9px;
      border-top: 1px solid #c8d3e5;
      color: #34445f;
      font-size: 10px;
      line-height: 1.35;
      font-style: italic;
    }

    .signature-line {
      border-bottom: 1.5px solid #111;
      height: 22px;
      flex: 1;
    }

    .row { display: flex; flex-wrap: wrap; margin-left: calc(-.5 * var(--bs-gutter-x, 1rem)); margin-right: calc(-.5 * var(--bs-gutter-x, 1rem)); margin-top: calc(-1 * var(--bs-gutter-y, 0)); }
    .row > * { flex-shrink: 0; width: 100%; max-width: 100%; padding-left: calc(var(--bs-gutter-x, 1rem) * .5); padding-right: calc(var(--bs-gutter-x, 1rem) * .5); margin-top: var(--bs-gutter-y, 0); }
    .g-3 { --bs-gutter-x: 1rem; --bs-gutter-y: 1rem; }
    .col-3 { flex: 0 0 auto; width: 25%; }
    .col-4 { flex: 0 0 auto; width: 33.33333333%; }
    .col-5 { flex: 0 0 auto; width: 41.66666667%; }
    .h-100 { height: 100% !important; }
    .table { width: 100%; border-collapse: collapse; }
    .table-bordered th, .table-bordered td { border: 1px solid #cfd9ea; }
    .position-relative { position: relative !important; }
    .position-absolute { position: absolute !important; }
    .d-flex { display: flex !important; }
    .flex-column { flex-direction: column !important; }
    .align-items-center { align-items: center !important; }
    .align-items-start { align-items: flex-start !important; }
    .align-items-end { align-items: flex-end !important; }
    .justify-content-between { justify-content: space-between !important; }
    .gap-2 { gap: .5rem !important; }
    .gap-3 { gap: 1rem !important; }
    .gap-4 { gap: 1.5rem !important; }
    .mx-auto { margin-left: auto !important; margin-right: auto !important; }
    .m-0 { margin: 0 !important; }
    .mb-0 { margin-bottom: 0 !important; }
    .mb-2 { margin-bottom: .5rem !important; }
    .mb-3 { margin-bottom: 1rem !important; }
    .mb-4 { margin-bottom: 1.5rem !important; }
    .mt-3 { margin-top: 1rem !important; }
    .mt-4 { margin-top: 1.5rem !important; }
    .mt-5 { margin-top: 3rem !important; }
    .mt-auto { margin-top: auto !important; }
    .pt-2 { padding-top: .5rem !important; }
    .lh-1 { line-height: 1 !important; }
    .lh-sm { line-height: 1.25 !important; }
    .text-nowrap { white-space: nowrap !important; }
    .text-uppercase { text-transform: uppercase !important; }
    .fw-bold { font-weight: 700 !important; }
    .flex-shrink-0 { flex-shrink: 0 !important; }
    .d-block { display: block !important; }
    .bg-white { background-color: #fff !important; }
    .overflow-hidden { overflow: hidden !important; }
    .shadow-lg { box-shadow: 0 18px 52px rgba(0,0,0,.18) !important; }
    .object-fit-contain { object-fit: contain !important; }
    .pe-none { pointer-events: none !important; }
    .z-0 { z-index: 0 !important; }
    .z-3 { z-index: 3 !important; }

    @media print {
      html, body {
        background: #fff;
      }

      .epata-page {
        margin: 0;
        box-shadow: none !important;
      }

      @page {
        size: auto;
        margin: 0;
      }
    }
  </style>`;
}

function buildRows(d) {
  const items = d.lineItems.filter(li => li.description || li.details || Number(li.quantity) || Number(li.rate));
  if (!items.length) {
    items.push({ description: 'Custom 3D print service', details: 'Details to be finalized', quantity: 1, rate: 0, amount: 0 });
  }

  return items.map((li, index) => `<tr>
    <td class="num-col">${index + 1}</td>
    <td>${escMultiline(li.description)}</td>
    <td>${escMultiline(li.details)}</td>
    <td class="qty-col">${esc(formatQty(li.quantity))}</td>
    <td class="rate-col">${money(li.rate)}</td>
    <td class="amount-col">${money(li.amount)}</td>
  </tr>`).join('');
}

function buildStatusStamp(status) {
  const normalized = String(status || 'Draft').trim();
  if (!normalized) return '';
  const statusKey = normalized.toLowerCase().replace(/[^a-z0-9]+/g, '-');
  // "Accepted" (estimate closed) gets the same green PAID styling.
  const styleKey = statusKey === 'accepted' ? 'paid' : statusKey;
  const knownClass = ['draft', 'sent', 'paid', 'void'].includes(styleKey) ? styleKey : 'draft';
  return `<div class="status-stamp status-stamp-${knownClass} position-absolute text-uppercase">${esc(normalized)}</div>`;
}

function actionPanelLabel(d) {
  if (d.docType === 'INVOICE') return 'Payment Status';
  // Estimate panel label reflects what's actually in the panel now.
  if (d.status === 'Accepted') return 'Accepted';
  if (d.status === 'Void')     return 'Voided';
  return 'Approval';
}

// Known default termsNotes templates — used to detect when the saved text is
// still the "out of the box" template for the OTHER doc type, so we can swap.
const ESTIMATE_DEFAULT_TERMS = `- This estimate is valid for 14 days from the date above.
- Final price may vary based on model changes or unexpected print issues.
- Payment is due before printing begins.
- Thank you for supporting EPATA 3D Prints!`;

const INVOICE_DEFAULT_TERMS = `- Payment is due by the due date shown above unless already paid.
- This invoice reflects the approved work, materials, and services listed.
- Paid invoices serve as a receipt for your records.
- Thank you for supporting EPATA 3D Prints!`;

const INVOICE_PAID_TERMS = `- Payment received in full — thank you!
- This invoice serves as your receipt for the work listed above.
- Please keep this document for your records.
- We appreciate your support of EPATA 3D Prints!`;

const ESTIMATE_ACCEPTED_TERMS = `- This estimate has been accepted.
- The price above is locked in barring scope changes after approval.
- Payment is due before printing begins.
- Thank you for choosing EPATA 3D Prints!`;

function normalizeData(input) {
  const d = { ...(input || {}) };
  d.docType = String(d.docType || 'ESTIMATE').toUpperCase();
  d.status = d.status || 'Draft';
  // Migrate legacy values so old saved data still renders correctly.
  if (d.docType === 'ESTIMATE' && d.status === 'Paid')     d.status = 'Accepted';
  if (d.docType === 'INVOICE'  && d.status === 'Accepted') d.status = 'Paid';
  d.lineItems = Array.isArray(d.lineItems) ? d.lineItems : [];
  d.subtotal = num(d.subtotal);
  d.discountAmount = num(d.discountAmount);
  d.rushAmount = num(d.rushAmount);
  d.taxAmount = num(d.taxAmount);
  d.total = num(d.total);
  d.amountPaid = num(d.amountPaid);
  d.balance = Number.isFinite(Number(d.balance)) ? num(d.balance) : Math.max(0, d.total - d.amountPaid);
  if (d.docType === 'INVOICE' && d.status === 'Paid') {
    d.amountPaid = d.total;
    d.balance = 0;
  }
  if (d.docType === 'ESTIMATE') {
    d.amountPaid = 0;
    d.balance = 0;
  }
  d.docRushPercent = num(d.docRushPercent);
  d.docTaxRate = num(d.docTaxRate);
  d.businessName = String(d.businessName || 'EPATA 3D PRINTS').toUpperCase();
  d.businessLocation = d.businessLocation || 'Based in New Jersey';
  d.businessEmail = d.businessEmail || 'epata.llc.co@gmail.com';
  d.businessPhone = d.businessPhone || '973 306 8628';
  d.businessWebsite = d.businessWebsite || 'erniephillipsportfolio.com';
  d.businessEtsy = d.businessEtsy || 'etsy.com/shop/EPATA3dPrints';
  d.businessMakerWorld = d.businessMakerWorld || 'makerworld.com/en/@epata.llc';
  d.standardTurnaround = d.standardTurnaround || 'Estimated timeline provided after design review and schedule confirmation';
  d.rushTurnaround = d.rushTurnaround || 'Expedited service available upon request, subject to current workload';
  d.pricingGuide = d.pricingGuide || `Print-Only Jobs
- $15 minimum, or $10 setup + $0.10/g + $2/hour
Basic Modeling
- $25/hour (1 hour minimum)
Revisions
- 1 small revision included
- Additional revisions: $15-$25 or hourly
Rush Fee
- Add 25%-50%
Material Notes
- ABS/ASA prints include a 20%-35% handling surcharge due to increased time and risk.`;
  // Pick the right default template for this doc type + status,
  // and swap saved text that still matches a known default of the WRONG type.
  const ideal = pickDefaultTerms(d.docType, d.status);
  const current = String(d.termsNotes || '').trim();
  const KNOWN_DEFAULTS = new Set(
    [ESTIMATE_DEFAULT_TERMS, INVOICE_DEFAULT_TERMS, INVOICE_PAID_TERMS, ESTIMATE_ACCEPTED_TERMS]
      .map(s => s.trim())
  );
  d.termsNotes = (!current || KNOWN_DEFAULTS.has(current)) ? ideal : d.termsNotes;
  return d;
}

function pickDefaultTerms(docType, status) {
  if (docType === 'INVOICE') {
    return status === 'Paid' ? INVOICE_PAID_TERMS : INVOICE_DEFAULT_TERMS;
  }
  return status === 'Accepted' ? ESTIMATE_ACCEPTED_TERMS : ESTIMATE_DEFAULT_TERMS;
}

function contactRow(type, value) {
  if (!value) return '';
  const icon = {
    location: `<path fill="#17499b" d="M12 2.2c-4.05 0-7.33 3.28-7.33 7.33 0 5.5 7.33 12.27 7.33 12.27s7.33-6.77 7.33-12.27c0-4.05-3.28-7.33-7.33-7.33zm0 10.1a2.77 2.77 0 1 1 0-5.54 2.77 2.77 0 0 1 0 5.54z"/>`,
    email: `<rect x="2.5" y="5.2" width="19" height="13.6" rx="1.2" fill="#17499b"/><path d="M3.4 6.3 12 13l8.6-6.7M3.4 17.7l6.2-5M20.6 17.7l-6.2-5" fill="none" stroke="#fff" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"/>`,
    phone: `<path fill="#17499b" d="M6.55 3.1c.68-.45 1.59-.3 2.1.34l2.07 2.6c.44.55.46 1.33.05 1.9l-1.15 1.6c1.06 2.02 2.78 3.75 4.82 4.82l1.6-1.15c.57-.41 1.35-.39 1.9.05l2.6 2.07c.64.51.79 1.42.34 2.1l-1.16 1.75c-.42.63-1.16.96-1.91.85-3.42-.5-6.72-2.18-9.5-4.96-2.78-2.78-4.46-6.08-4.96-9.5-.11-.75.22-1.49.85-1.91L6.55 3.1z"/>`,
  }[type];

  return `<div class="contact-row d-flex align-items-center gap-3 lh-1 text-nowrap">
    <svg class="contact-icon flex-shrink-0 d-block" viewBox="0 0 24 24" aria-hidden="true">${icon}</svg>
    <span>${esc(value)}</span>
  </div>`;
}

function metaRow(label, value, withMargin = true) {
  return `<div class="${withMargin ? 'doc-meta-row ' : ''}d-flex justify-content-between">
    <span class="doc-meta-label">${esc(label)}</span>
    <span class="doc-meta-value">${esc(value || '')}</span>
  </div>`;
}

function billTo(d) {
  const lines = [
    d.customerName || d.preparedFor,
    ...splitLines(d.customerAddress),
    d.customerPhone,
    d.customerEmail,
  ].filter(Boolean);

  if (!lines.length) return '';

  return lines.map((line, index) =>
    `<p class="${index === 0 ? 'fw-bold mb-3' : index === lines.length - 1 ? 'mb-0' : ''}">${esc(line)}</p>`
  ).join('');
}

function detailRow(label, value) {
  if (!value) return '';
  return `<div>${esc(label)}</div><div>${escMultiline(value)}</div>`;
}

function summaryRow(label, value) {
  return `<div class="summary-row"><span>${esc(label)}</span><span>${esc(value)}</span></div>`;
}

function structuredGuide(value) {
  const lines = splitLines(value);
  if (!lines.length) return '';

  const groups = [];
  let current = null;

  lines.forEach(line => {
    const isBullet = /^[-*•]\s+/.test(line);
    const text = line.replace(/^[-*•]\s+/, '');

    if (!isBullet) {
      current = { heading: text, bullets: [] };
      groups.push(current);
      return;
    }

    if (!current) {
      current = { heading: '', bullets: [] };
      groups.push(current);
    }
    current.bullets.push(text);
  });

  return groups.map(group => `
    ${group.heading ? `<div class="fw-bold">${esc(group.heading)}</div>` : ''}
    ${group.bullets.length ? `<ul class="mb-2">${group.bullets.map(item => `<li>${esc(item)}</li>`).join('')}</ul>` : ''}
  `).join('');
}

function bulletList(value, className = '') {
  const lines = splitLines(value);
  if (!lines.length) return '';
  return `<ul class="${className}">${lines.map(line => `<li>${esc(line.replace(/^[-*•]\s+/, ''))}</li>`).join('')}</ul>`;
}

function actionPanel(d, title) {
  if (d.docType === 'INVOICE') {
    return invoiceActionPanel(d);
  }
  return estimateActionPanel(d, title);
}

function invoiceActionPanel(d) {
  const headline = {
    Paid:  'Payment received. Thank you!',
    Sent:  'Payment is due for this invoice.',
    Draft: 'Draft invoice — not yet sent for payment.',
    Void:  'This invoice has been voided and is no longer payable.',
  }[d.status] || 'Payment is due for this invoice.';

  return `
    <p class="fw-bold mb-3">${headline}</p>
    <div class="summary-row"><span>Status</span><span>${esc(d.status)}</span></div>
    <div class="summary-row"><span>Invoice Total</span><span>${money(d.total)}</span></div>
    <div class="summary-row"><span>Amount Paid</span><span>${money(d.amountPaid)}</span></div>
    <div class="d-flex align-items-end justify-content-between mt-3">
      <div class="summary-total-label">Balance<br>Due</div>
      <div class="summary-total-value">${money(Math.max(0, d.balance))}</div>
    </div>
  `;
}

function estimateActionPanel(d, title) {
  // Accepted: confirmation block, no blank signature lines.
  if (d.status === 'Accepted') {
    return `
      <p class="fw-bold mb-3">This estimate has been accepted.</p>
      <p>Work will proceed under the scope and pricing approved above. An invoice will be issued upon completion.</p>
      <div class="summary-row"><span>Accepted on</span><span>${esc(prettyDate(d.docDate) || '—')}</span></div>
      <div class="summary-row"><span>Approved Total</span><span>${money(d.total)}</span></div>
    `;
  }

  // Void: cancellation block.
  if (d.status === 'Void') {
    return `
      <p class="fw-bold mb-3">This estimate has been voided.</p>
      <p>The pricing and scope above are no longer valid. Contact EPATA 3D Prints for a current quote.</p>
    `;
  }

  // Draft preview: keep the signature block but mark it as preview-only.
  // Sent (default): full approval signature block.
  const draftNote = d.status === 'Draft'
    ? '<p class="mb-3" style="color:#6b7280">Draft preview — for review before sending.</p>'
    : '';
  return `
    ${draftNote}
    <p>I approve this ${title.toLowerCase()} and authorize EPATA 3D Prints to begin work.</p>

    <div class="d-flex align-items-end gap-2 mt-5">
      <span>Signature:</span>
      <span class="signature-line"></span>
    </div>

    <div class="d-flex align-items-end gap-2 mt-4">
      <span>Name:</span>
      <span class="signature-line"></span>
    </div>

    <div class="d-flex align-items-end gap-2 mt-4">
      <span>Date:</span>
      <span class="signature-line"></span>
    </div>
  `;
}

function socialRow(kind, label, value) {
  if (!value) return '';

  const icons = {
    makerworld: `<rect x="1" y="1" width="18" height="18" rx="4" fill="#101010"/><path d="M5.1 14.7V5.3h2.2L10 8.4l2.7-3.1h2.2v9.4h-2.1V8.6l-2.8 3.2-2.8-3.2v6.1H5.1z" fill="none" stroke="#31d27c" stroke-width="1.15" stroke-linejoin="round"/>`,
    etsy: `<rect x="1" y="1" width="18" height="18" rx="3" fill="#f16921"/><text x="10" y="14.3" text-anchor="middle" font-family="Georgia, serif" font-size="14.5" font-weight="700" fill="#ffffff">E</text>`,
    website: `<circle cx="10" cy="10" r="9" fill="#17499b"/><circle cx="10" cy="10" r="6.2" fill="none" stroke="#ffffff" stroke-width="1.25"/><path d="M3.8 10h12.4M10 3.8c2.1 2.25 2.1 10.15 0 12.4M10 3.8c-2.1 2.25-2.1 10.15 0 12.4" fill="none" stroke="#ffffff" stroke-width="1.1" stroke-linecap="round"/>`,
  };

  return `<div class="social-row d-flex align-items-center gap-2 lh-1 text-nowrap">
    <svg class="social-icon flex-shrink-0 d-block" viewBox="0 0 20 20" aria-hidden="true">${icons[kind]}</svg>
    <span><strong>${esc(label)}</strong> — ${esc(value)}</span>
  </div>`;
}

function splitLines(value) {
  return String(value || '').split(/\r?\n/).map(v => v.trim()).filter(Boolean);
}

function esc(value) {
  return String(value ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;');
}

function escMultiline(value) {
  return esc(value).replace(/\r?\n/g, '<br>');
}

function prettyDate(value) {
  if (!value) return '';
  const date = new Date(`${value}T00:00:00`);
  return Number.isNaN(date.getTime())
    ? value
    : date.toLocaleDateString('en-US', { month: 'long', day: 'numeric', year: 'numeric' });
}

function formatQty(value) {
  const n = num(value);
  if (!n && String(value || '').trim()) return String(value);
  return Number.isInteger(n) ? String(n) : n.toFixed(2).replace(/0+$/, '').replace(/\.$/, '');
}

function displayWebsite(value) {
  return String(value || '').replace(/^https?:\/\//i, '').replace(/\/$/, '');
}

function num(value) {
  const n = Number(value ?? 0);
  return Number.isFinite(n) ? n : 0;
}

function formatTaxRate(value) {
  // Show up to 3 decimal places, trimming trailing zeros (e.g. 6.625, 6.6, 7)
  const n = num(value);
  return n.toFixed(3).replace(/\.?0+$/, '');
}

// Existing import kept for compatibility with older callers.
export function getPdfData(getFormDataFn, getLineItemsFn, config) {
  const formData = getFormDataFn();
  return {
    ...formData,
    ...config,
    brandColor: config?.brandColor || '#17468f',
  };
}
