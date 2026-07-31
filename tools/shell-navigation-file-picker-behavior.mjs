import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';

const readProjectFile = path => readFile(new URL(`../${path}`, import.meta.url), 'utf8');

const [mainHtml, mainShell, invoiceHtml, invoiceApp, invoiceApi] = await Promise.all([
  readProjectFile('wwwroot/index.html'),
  readProjectFile('wwwroot/js/app.js'),
  readProjectFile('wwwroot/invoice-builder/index.html'),
  readProjectFile('wwwroot/invoice-builder/js/app.js'),
  readProjectFile('wwwroot/invoice-builder/js/api.js'),
]);

function includes(source, expected, label) {
  assert.ok(source.includes(expected), `${label} missing expected wiring: ${expected}`);
}

function matches(source, expected, label) {
  assert.match(source, expected, `${label} missing expected wiring: ${expected}`);
}

const invoiceViews = [
  ['dashboard', 'Dashboard'],
  ['calculator', 'Calculator'],
  ['builder', 'Builder'],
  ['records', 'Records'],
  ['ratecard', 'Rate Card'],
  ['settings', 'Settings'],
];

for (const [view, label] of invoiceViews) {
  includes(invoiceHtml, `data-view="${view}"`, `standalone invoice ${label} nav`);
  includes(invoiceHtml, `id="view-${view}"`, `standalone invoice ${label} view`);
}

for (const [view, label] of [
  ['dashboard', 'Dashboard'],
  ['calculator', 'Calculator'],
  ['builder', 'Builder + PDF'],
  ['records', 'Records'],
  ['ratecard', 'Rate Card'],
  ['settings', 'Settings'],
]) {
  includes(mainShell, `<button class="nav-item" data-view="${view}">${label}</button>`, `embedded invoice ${label} tab`);
}

includes(mainHtml, '<script src="/js/app.js?v=20260619-ar-prefill"></script>', 'main shell app script entry');
includes(mainShell, "fetch('/invoice-builder/index.html')", 'embedded invoice shell fetch');
includes(mainShell, "doc.querySelector('#main')", 'embedded invoice workspace extraction');
includes(mainShell, "await import('/invoice-builder/js/app.js?v=39')", 'embedded invoice app module import');
includes(mainShell, 'await module.init({ initialView, restoreSnapshot, newType, prefill });', 'embedded invoice app init');

includes(mainShell, "const invoicePages = ['invoiceCenter','estimates','invoices','pricingCalculator','invoiceRecords'];", 'invoice route group');
includes(mainShell, 'appState.invoiceToolSnapshot = window._invoiceToolSnapshot() || appState.invoiceToolSnapshot;', 'invoice snapshot capture');
includes(mainShell, 'appState.invoiceToolSnapshot = null;', 'invoice snapshot reset');
includes(mainShell, 'window._invoiceToolShowView(invoiceRouteView(page));', 'invoice tab switch without remount');
includes(mainShell, "else if (page === 'invoiceCenter') await renderMergedInvoiceTool(renderTarget, 'dashboard', '', appState.invoiceToolSnapshot);", 'invoice dashboard route');
includes(mainShell, "else if (page === 'estimates') await renderMergedInvoiceTool(renderTarget, 'builder', 'ESTIMATE', appState.invoiceToolSnapshot);", 'estimate route');
includes(mainShell, "else if (page === 'invoices') await renderMergedInvoiceTool(renderTarget, 'builder', 'INVOICE', appState.invoiceToolSnapshot);", 'invoice route');
includes(mainShell, "else if (page === 'pricingCalculator') await renderMergedInvoiceTool(renderTarget, 'calculator', '', appState.invoiceToolSnapshot);", 'calculator route');
includes(mainShell, "else if (page === 'invoiceRecords') await renderMergedInvoiceTool(renderTarget, 'records', '', appState.invoiceToolSnapshot);", 'invoice records route');
includes(invoiceApp, 'window._invoiceToolSnapshot = createDocumentSnapshot;', 'invoice snapshot export');
includes(invoiceApp, 'window._invoiceToolShowView = showView;', 'invoice tab switch export');
includes(invoiceApp, 'restoreDocumentSnapshot(options.restoreSnapshot);', 'invoice snapshot restore');

includes(invoiceHtml, 'id="btnImportPdfDraftBuilder"', 'builder AI Import PDF button');
includes(invoiceHtml, "document.getElementById('invoicePdfImportFile').click()", 'builder AI Import PDF picker click');
includes(invoiceHtml, 'id="btnImportPdfDraft">AI Import PDF</button>', 'records AI Import PDF button');
matches(invoiceHtml, /id="invoicePdfImportFile"[^>]*accept="\.pdf,application\/pdf"[^>]*style="display:none"/, 'AI Import PDF input');
includes(invoiceApp, "on('btnImportPdfDraft', () => el('invoicePdfImportFile')?.click());", 'records AI Import PDF picker click');
includes(invoiceApp, "on('invoicePdfImportFile', (e) => onImportPdfDraft(e));", 'AI Import PDF onchange import');

matches(invoiceHtml, /data-ai-assistant-open[\s\S]*AI Intake/, 'AI intake modal open buttons');
includes(invoiceApp, "document.querySelectorAll('[data-ai-assistant-open]')", 'AI intake modal button binding');
includes(invoiceApp, 'openAiAssistantModal', 'AI intake modal open function');
includes(invoiceApp, 'await refreshAiAssistantStatus(true);', 'AI intake modal local model start on open');
includes(invoiceApp, 'applyDocumentPrefill(prefill);', 'AI intake draft applies through builder prefill');
matches(invoiceApp, /function applyDocumentPrefill[\s\S]*restoreCalcState\(calcFields\);[\s\S]*const fields = \{/, 'AI prefill restores calculator before document fields');
includes(invoiceApi, "request('/api/ai/estimate-draft/upload'", 'AI intake draft upload API');
includes(invoiceApi, "request('/api/ai/estimate-chat/upload'", 'AI intake chat upload API');
includes(invoiceApi, "request('/api/ai/local/start'", 'AI intake local start API');

includes(mainShell, 'data-proof-upload="${field.name}"', 'row proof upload button');
includes(mainShell, 'id="proof_file_${field.name}" type="file"', 'row proof upload input');
includes(mainShell, 'button.onclick = () => fileInput.click();', 'row proof picker click');
includes(mainShell, 'fileInput.onchange = async () => {', 'row proof picker onchange');
includes(mainShell, 'await uploadProofForField(config, fieldName, fileInput.files[0]);', 'row proof upload handler');
matches(mainShell, /<input id="docFiles" type="file"[^>]*multiple/, 'document intake file input');
includes(mainShell, '<button class="primary-button" id="uploadDocsBtn">Upload and Index</button>', 'document intake upload button');
includes(mainShell, "qs('#uploadDocsBtn').onclick = uploadDocuments;", 'document intake upload binding');
includes(mainShell, "const files = qs('#docFiles').files;", 'document intake reads selected files');

includes(invoiceApp, "if (key === 's')", 'Ctrl+S save shortcut');
includes(invoiceApp, "if (el('view-settings')?.classList.contains('active')) await saveSettings();", 'Ctrl+S settings save path');
includes(invoiceApp, 'else await saveRecord(false);', 'Ctrl+S document save path');
includes(invoiceApp, "if (key === 'n' && e.shiftKey)", 'Ctrl+Shift+N shortcut');
includes(invoiceApp, "startNew('ESTIMATE');", 'Ctrl+Shift+N new estimate path');
matches(invoiceApp, /e\.preventDefault\(\);\s+e\.stopPropagation\(\);/, 'keyboard shortcut event suppression');

console.log(JSON.stringify({
  ShellNavigationFilePickerBehavior: 'pass',
  FilePickerRound73: 'pass',
  EmbeddedInvoiceShellRound73: 'pass',
  InvoiceTabsRound73: 'pass',
  InvoiceStateSwitchRound73: 'pass',
  InvoiceKeyboardShortcutsRound73: 'pass',
}));
