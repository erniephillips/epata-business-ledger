// Local copy of the renderer adapted from the project's invoice-builder/js/pdf.js
// This file is a reduced compatibility wrapper that imports utils.js from the local folder.
import { money } from './utils.js';

const ASSET_ROOT = './img';
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

// Minimal renderInvoiceHtml that reuses most of the original logic but points asset paths to local ./img
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
    ? `\n  <script>window.addEventListener('load',()=>setTimeout(()=>window.print(),250));<\/script>`
    : '';

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>${esc(d.docNumber || title)} - EPATA 3D Prints</title>
  ${invoiceStyles(d.brandColor, d.pageSize)}
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
  </main>${autoPrintScript}
</body>
</html>`;
}

// For brevity, reuse utility functions from the original file but keep them local below.
// (Include trimmed versions sufficient for rendering.)

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
  const styleKey = statusKey === 'accepted' ? 'paid' : statusKey;
  const knownClass = ['draft', 'sent', 'paid', 'void'].includes(styleKey) ? styleKey : 'draft';
  return `<div class="status-stamp status-stamp-${knownClass} position-absolute text-uppercase">${esc(normalized)}</div>`;
}

function invoiceStyles(brandColor, pageSize) {
  const safeBrandColor = normalizeBrandColor(brandColor);
  const page = pageMetrics(pageSize);
  return `<style> :root { --epata-blue: ${safeBrandColor}; --epata-text: #111111; --epata-page-w: ${page.width}px; --epata-page-h: ${page.height}px; } *{box-sizing:border-box} html,body{margin:0;background:#dfe5ef;color:var(--epata-text);font-family:Arial,Helvetica,sans-serif;-webkit-print-color-adjust:exact;print-color-adjust:exact} .epata-page{width:var(--epata-page-w);min-height:var(--epata-page-h);height:auto;margin-top:12px;margin-bottom:36px;isolation:isolate;display:flex;flex-direction:column} /* truncated styles for brevity */ </style>`;
}

// Minimal helpers (trimmed)
function splitLines(value){return String(value||'').split(/\r?\n/).map(v=>v.trim()).filter(Boolean);} 
function esc(value){return String(value??'').replaceAll('&','&amp;').replaceAll('<','&lt;').replaceAll('>','&gt;').replaceAll('"','&quot;').replaceAll("'","&#039;");}
function escMultiline(v){return esc(v).replace(/\r?\n/g,'<br>');}
function prettyDate(v){if(!v) return ''; const date=new Date(`${v}T00:00:00`); return Number.isNaN(date.getTime())?v:date.toLocaleDateString('en-US',{month:'long',day:'numeric',year:'numeric'});} 
function formatQty(value){const n=num(value); if(!n&&String(value||'').trim()) return String(value); return Number.isInteger(n)?String(n):n.toFixed(2).replace(/0+$/,'').replace(/\.$/, '');}
function num(value){const n=Number(value??0); return Number.isFinite(n)?n:0;} 
function normalizeBrandColor(value){const color=String(value||'').trim(); return /^#[0-9a-f]{6}$/i.test(color)?color:'#17499b';}
function formatTaxRate(v){const n=num(v); return n.toFixed(3).replace(/\.?0+$/,'');}

function contactRow(type,value){ if(!value) return ''; const icon={location:`<path fill="#17499b" d="M12 2.2c-4.05 0-7.33 3.28-7.33 7.33 0 5.5 7.33 12.27 7.33 12.27s7.33-6.77 7.33-12.27c0-4.05-3.28-7.33-7.33-7.33zm0 10.1a2.77 2.77 0 1 1 0-5.54 2.77 2.77 0 0 1 0 5.54z"/>`,email:`<rect x="2.5" y="5.2" width="19" height="13.6" rx="1.2" fill="#17499b"/><path d="M3.4 6.3 12 13l8.6-6.7M3.4 17.7l6.2-5M20.6 17.7l-6.2-5" fill="none" stroke="#fff" stroke-width="1.35" stroke-linecap="round" stroke-linejoin="round"/>`,phone:`<path fill="#17499b" d="M6.55 3.1c.68-.45 1.59-.3 2.1.34l2.07 2.6c.44.55.46 1.33.05 1.9l-1.15 1.6c1.06 2.02 2.78 3.75 4.82 4.82l1.6-1.15c.57-.41 1.35-.39 1.9.05l2.6 2.07c.64.51.79 1.42.34 2.1l-1.16 1.75c-.42.63-1.16.96-1.91.85-3.42-.5-6.72-2.18-9.5-4.96-2.78-2.78-4.46-6.08-4.96-9.5-.11-.75.22-1.49.85-1.91L6.55 3.1z"/>`}[type]; return `<div class="contact-row d-flex align-items-center gap-3 lh-1 text-nowrap"><svg class="contact-icon flex-shrink-0 d-block" viewBox="0 0 24 24" aria-hidden="true">${icon}</svg><span>${esc(value)}</span></div>`; }

function metaRow(label,value,withMargin=true){return `<div class="${withMargin?'doc-meta-row ':''}d-flex justify-content-between"><span class="doc-meta-label">${esc(label)}</span><span class="doc-meta-value">${esc(value||'')}</span></div>`;}

function billTo(d){const lines=[d.customerName||d.preparedFor,...splitLines(d.customerAddress),d.customerPhone,d.customerEmail].filter(Boolean); if(!lines.length) return ''; return lines.map((line,index)=>`<p class="${index===0?'fw-bold mb-3':index===lines.length-1?'mb-0':''}">${esc(line)}</p>`).join('');}
function detailRow(label,value){if(!value) return ''; return `<div>${esc(label)}</div><div>${escMultiline(value)}</div>`;} 
function summaryRow(label,value){return `<div class="summary-row"><span>${esc(label)}</span><span>${esc(value)}</span></div>`;} 
function structuredGuide(value){const lines=splitLines(value); if(!lines.length) return ''; const groups=[]; let current=null; lines.forEach(line=>{const isBullet=/^[-*•]\s+/.test(line); const text=line.replace(/^[-*•]\s+/,''); if(!isBullet){current={heading:text,bullets:[]}; groups.push(current); return;} if(!current){current={heading:'',bullets:[]}; groups.push(current);} current.bullets.push(text);}); return groups.map(group=>`${group.heading?`<div class="fw-bold">${esc(group.heading)}</div>`:''}${group.bullets.length?`<ul class="mb-2">${group.bullets.map(item=>`<li>${esc(item)}</li>`).join('')}</ul>`:''}`).join(''); }
function bulletList(value,className=''){const lines=splitLines(value); if(!lines.length) return ''; return `<ul class="${className}">${lines.map(line=>`<li>${esc(line.replace(/^[-*•]\s+/,''))}</li>`).join('')}</ul>`;} 
function actionPanelLabel(d){if(d.docType==='INVOICE') return 'Payment Status'; if(d.status==='Accepted') return 'Accepted'; if(d.status==='Void') return 'Voided'; return 'Approval';} 
function actionPanel(d,title){ if(d.docType==='INVOICE') return invoiceActionPanel(d); return estimateActionPanel(d,title); }
function invoiceActionPanel(d){const headline={Paid:'Payment received. Thank you!',Sent:'Payment is due for this invoice.',Draft:'Draft invoice — not yet sent for payment.',Void:'This invoice has been voided and is no longer payable.'}[d.status]||'Payment is due for this invoice.'; return `<p class="fw-bold mb-3">${headline}</p><div class="summary-row"><span>Status</span><span>${esc(d.status)}</span></div><div class="summary-row"><span>Invoice Total</span><span>${money(d.total)}</span></div><div class="summary-row"><span>Amount Paid</span><span>${money(d.amountPaid)}</span></div><div class="d-flex align-items-end justify-content-between mt-3"><div class="summary-total-label">Balance<br>Due</div><div class="summary-total-value">${money(Math.max(0,d.balance))}</div></div>`; }
function estimateActionPanel(d,title){ if(d.status==='Accepted'){return `<p class="fw-bold mb-3">This estimate has been accepted.</p><p>Work will proceed under the scope and pricing approved above. An invoice will be issued upon completion.</p><div class="summary-row"><span>Accepted on</span><span>${esc(prettyDate(d.docDate)||'—')}</span></div><div class="summary-row"><span>Approved Total</span><span>${money(d.total)}</span></div>`;} if(d.status==='Void'){return `<p class="fw-bold mb-3">This estimate has been voided.</p><p>The pricing and scope above are no longer valid. Contact EPATA 3D Prints for a current quote.</p>`;} const draftNote=d.status==='Draft'?'<p class="mb-3" style="color:#6b7280">Draft preview — for review before sending.</p>':''; return `${draftNote}<p>I approve this ${title.toLowerCase()} and authorize EPATA 3D Prints to begin work.</p><div class="d-flex align-items-end gap-2 mt-5"><span>Signature:</span><span class="signature-line"></span></div><div class="d-flex align-items-end gap-2 mt-4"><span>Name:</span><span class="signature-line"></span></div><div class="d-flex align-items-end gap-2 mt-4"><span>Date:</span><span class="signature-line"></span></div>`; }

function buildDefaultData(){return {docType:'ESTIMATE',pageSize:'A4',status:'Draft',lineItems:[],subtotal:0,discountAmount:0,rushAmount:0,taxAmount:0,total:0,amountPaid:0,balance:0,docRushPercent:0,docTaxRate:0,brandColor:'#17499b',businessName:'EPATA 3D PRINTS',businessLocation:'Based in New Jersey',businessEmail:'epata.llc.co@gmail.com',businessPhone:'973 306 8628',businessWebsite:'erniephillipsportfolio.com',businessEtsy:'etsy.com/shop/EPATA3dPrints',businessMakerWorld:'makerworld.com/en/@epata.llc',standardTurnaround:'Estimated timeline provided after design review and schedule confirmation',rushTurnaround:'Expedited service available upon request, subject to current workload'} }

function normalizeData(input){ const d={...buildDefaultData(),...(input||{})}; d.docType=String(d.docType||'ESTIMATE').toUpperCase(); d.pageSize=normalizePageSize(d.pageSize); d.status=d.status||'Draft'; if(d.docType==='ESTIMATE'&&d.status==='Paid') d.status='Accepted'; if(d.docType==='INVOICE'&&d.status==='Accepted') d.status='Paid'; d.lineItems=Array.isArray(d.lineItems)?d.lineItems:[]; d.subtotal=num(d.subtotal); d.discountAmount=num(d.discountAmount); d.rushAmount=num(d.rushAmount); d.taxAmount=num(d.taxAmount); d.total=num(d.total); d.amountPaid=num(d.amountPaid); d.balance=Number.isFinite(Number(d.balance))?num(d.balance):Math.max(0,d.total-d.amountPaid); if(d.docType==='INVOICE'&&d.status==='Paid'){d.amountPaid=d.total; d.balance=0} if(d.docType==='ESTIMATE'){d.amountPaid=0; d.balance=0} d.docRushPercent=num(d.docRushPercent); d.docTaxRate=num(d.docTaxRate); d.brandColor=normalizeBrandColor(d.brandColor); d.businessName=String(d.businessName||'EPATA 3D PRINTS').toUpperCase(); return d }

function pageMetrics(pageSize){ const normalized=normalizePageSize(pageSize); if(normalized==='LETTER') return {width:1024,height:1325,printSize:'letter portrait'}; if(normalized==='LEGAL') return {width:1024,height:1688,printSize:'legal portrait'}; return {width:1024,height:1414,printSize:'A4 portrait'} }

function normalizePageSize(pageSize){ const normalized=String(pageSize||'A4').trim().toUpperCase(); return ['A4','LETTER','LEGAL'].includes(normalized)?normalized:'A4' }

// export minimal API if needed
export { renderInvoiceHtml };

