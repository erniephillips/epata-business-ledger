// Local wrapper that imports the original renderer files copied into this folder.
// The invoice renderer depends on utils.js; ensure both files exist in PDF files/js.
export async function generatePdf(data, preview = false) {
  const mod = await import('./pdfRenderer.js');
  return mod.generatePdf(data, preview);
}
export { renderInvoiceHtml } from './pdfRenderer.js';

