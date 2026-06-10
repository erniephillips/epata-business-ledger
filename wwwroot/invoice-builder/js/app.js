// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Main Application
//  .NET 10 SPA Entry Point
// ═══════════════════════════════════════════════════════

import { api }                                    from './api.js?v=4';
import { el, toast, money, setVal, textVal,
         fmtDateTime, statusBadge, typeBadge,
         debounce, todayStr }                     from './utils.js?v=2';
import { initCalculator, calculate, getCalcState,
         restoreCalcState, pushToBuilder,
         applyConfigDefaults, syncDifficultyButtons } from './calculator.js?v=3';
import { initBuilder, addLineItem, removeLineItem,
         getLineItems, updateTotals, captureState,
         restoreState, getFormData, newDocument } from './builder.js?v=5';
import { initRecords, refreshRecords, loadRecord,
         duplicateRecord, deleteRecord, exportCsv,
         getRecords, convertEstimateToInvoice,
         restoreRecord }                          from './records.js?v=10';
import { generatePdf, renderInvoiceHtml }         from './pdf.js?v=3';
import { initInputValidation, normalizeDocumentInputs, normalizeSettingsInputs,
         validateCalculatorInputs, validateDocumentInputs,
         validateSettingsInputs } from './validation.js?v=2';

// ── State ─────────────────────────────────────────────
let activeRecordId  = null;
let apiReady        = false;
let appConfig       = {};
let autoSaveTimer   = null;
let saveInFlight    = null;
let productLookups  = [];
const AUTOSAVE_MS   = 30_000;

// ── Init ──────────────────────────────────────────────
export async function init(initialView = 'dashboard') {
  const options = typeof initialView === 'object' && initialView !== null ? initialView : {};
  if (typeof initialView === 'object' && initialView !== null) initialView = options.initialView || 'dashboard';

  // Expose global handlers for inline onclick attributes
  window._builderUpdate  = () => { updateTotals(); refreshInvoicePreview(); scheduleAutoSave(); };
  window._removeLineItem = removeLineItem;
  window._loadRecord     = (id) => onLoadRecord(id);
  window._dupeRecord     = (id) => onDuplicateRecord(id);
  window._convertEstimate = (id) => onConvertEstimate(id);
  window._delRecord      = (id) => onDeleteRecord(id);
  window._restoreRecord  = (id) => onRestoreRecord(id);
  window._copyText       = copyText;
  window._invoiceToolSnapshot = createDocumentSnapshot;
  window._invoiceToolShowView = showView;

  // Override the shim — expose addLineItem globally for onclick handlers
  window.addLineItem     = addLineItem;
  window.el              = el;

  // Nav
  document.querySelectorAll('.nav-item[data-view]').forEach(btn =>
    btn.addEventListener('click', () => showView(btn.dataset.view)));

  // Buttons
  on('btnNewEstimate',  () => startNew('ESTIMATE'));
  on('btnNewInvoice',   () => startNew('INVOICE'));
  on('btnSaveDraft',    () => saveRecord(false));
  on('btnSaveNew',      () => saveRecord(true));
  on('btnDownloadPdf',  () => onGeneratePdf(false));
  on('btnPreviewPdf',   () => onGeneratePdf(true));
  on('btnPushToBuilder',     () => onPushToBuilder());
  on('btnPushToBuilderCard', () => onPushToBuilder());
  on('btnExportDb',     () => { window.location.href = api.backupUrl(); });
  on('btnImportDb',     () => el('importDbFile')?.click());
  on('btnExportCsv',    () => exportCsv());
  on('importDbFile',    (e) => onImportDb(e));
  on('btnSaveSettings', () => saveSettings());

  // Records init
  initRecords({ onLoad: onDocumentLoaded, onNew: startNew });

  // Builder & calculator init
  initBuilder();
  initCalculator();
  initInputValidation();
  wireLiveCalculationUpdates();
  const builderView = el('view-builder');
  builderView?.addEventListener('input', debounce(refreshInvoicePreview, 150));
  builderView?.addEventListener('change', debounce(refreshInvoicePreview, 150));

  // Keyboard shortcuts
  document.addEventListener('keydown', async (e) => {
    if (!(e.ctrlKey || e.metaKey) || e.altKey) return;

    const key = e.key.toLowerCase();

    if (key === 's') {
      e.preventDefault();
      e.stopPropagation();

      try {
        if (el('view-settings')?.classList.contains('active')) await saveSettings();
        else await saveRecord(false);
      } catch {
        // saveRecord/saveSettings already show the error toast
      }
    }

    if (key === 'n' && e.shiftKey) {
      e.preventDefault();
      e.stopPropagation();
      startNew('ESTIMATE');
    }
  });

  // Connect to server. Only health decides whether saving is available;
  // config/latest/dashboard failures should not make Ctrl+S think the app is offline.
  setDbStatus('Connecting…', 'loading');
  try {
    await api.health();
    apiReady = true;
    setDbStatus('Ready', 'ready');
  } catch (err) {
    apiReady = false;
    setDbStatus('Server offline', 'error');
    toast('Cannot connect to server. Is the app running?', 'error', 6000);
    console.error(err);
  }

  if (apiReady) {
    // Load config
    try {
      appConfig = await api.getConfig();
      applyConfigToSettings(appConfig);
      applyConfigDefaults(appConfig);
      await loadProductLookups();
    } catch (err) {
      toast('Settings could not load. Saving still works.', 'error', 5000);
      console.error(err);
    }

    // Load records
    try {
      await refreshRecords();
    } catch (err) {
      toast('Records could not load. Saving still works.', 'error', 5000);
      console.error(err);
    }

    if (options.restoreSnapshot) {
      restoreDocumentSnapshot(options.restoreSnapshot);
      setDbStatus(`Ready — ${activeRecordId ? 'editing' : 'draft'} ${textVal('docNumber') || 'document'}`, 'ready');
    } else {
      const startType = options.newType || 'ESTIMATE';
      const num = await api.nextNumber(startType).then(r => r.number).catch(() => '');
      activeRecordId = null;
      startCleanDocument(startType, num);
      applyDocumentPrefill(options.prefill);
      setDbStatus(`Ready — new ${startType === 'INVOICE' ? 'invoice' : 'estimate'}`, 'ready');
    }

    // Refresh dashboard stats
    await loadDashboardStats().catch(err => console.error('Dashboard stats error', err));
  }

  refreshInvoicePreview();
  showView(initialView);
}

// ── Difficulty card global (called from inline onclick in index.html) ──
window.diffCardClick = function(btn) {
  document.querySelectorAll('#difficultyGrid .diff-card').forEach(b => b.classList.remove('active'));
  btn.classList.add('active');
  const hidden = el('difficulty');
  if (hidden) { hidden.value = btn.dataset.val; hidden.dispatchEvent(new Event('change')); }
};

// ── View routing ──────────────────────────────────────
export function showView(name) {
  document.querySelectorAll('.view').forEach(v => v.classList.remove('active'));
  document.querySelectorAll('.nav-item[data-view]').forEach(b => b.classList.remove('active'));
  el(`view-${name}`)?.classList.add('active');
  document.querySelector(`.nav-item[data-view="${name}"]`)?.classList.add('active');
  if (name === 'dashboard') loadDashboardStats();
  if (name === 'records')   refreshRecords();
  if (name === 'builder')   refreshInvoicePreview();
  window.scrollTo({ top: 0, behavior: 'smooth' });
}

// ── Dashboard ─────────────────────────────────────────
async function loadDashboardStats() {
  try {
    const s = await api.stats();
    setText('statEstimates', s.totalEstimates);
    setText('statInvoices',  s.totalInvoices);
    setText('statRevenue',   money(s.totalRevenue));
    setText('statUnpaid',    money(s.unpaidBalance));
    setText('statDraft',     s.draftCount);
    setText('statSent',      s.sentCount);
    setText('statPaid',      s.paidCount);

    // Recent records table
    const rows = getRecords().slice(0, 8);
    const tbody = el('dashRecentBody');
    if (tbody) {
      tbody.innerHTML = rows.length ? rows.map(r => `
        <tr>
          <td class="doc-number" style="cursor:pointer" onclick="${r.sourceKind === 'receivable' ? `window.openLedgerEntityRecord && window.openLedgerEntityRecord('receivables', ${r.sourceId || r.id})` : `window._loadRecord(${r.id})`}">${r.docNumber||'—'}</td>
          <td>${typeBadge(r.docType)}</td>
          <td>${statusBadge(r.status||'Draft')}</td>
          <td>${r.customerName||'—'}</td>
          <td class="num">${money(r.total)}</td>
          <td class="muted">${fmtDateTime(r.updatedAt)}</td>
        </tr>`).join('')
        : `<tr><td colspan="6"><div class="empty-state" style="padding:30px"><div class="empty-icon">📄</div><div class="empty-title">No records yet</div></div></td></tr>`;
    }
  } catch (e) {
    console.error('Stats error', e);
  }
}

// ── Document lifecycle ────────────────────────────────
function onDocumentLoaded(doc) {
  cancelPendingAutoSave();
  // Restore form fields
  restoreState({
    formValues: {
      docType:  doc.docType,
      docStatus: doc.status,
      docNumber: doc.docNumber,
      docDate:   doc.docDate,
      dueDate:   doc.dueDate,
      preparedFor:      doc.preparedFor,
      customerName:     doc.customerName,
      customerPhone:    doc.customerPhone,
      customerAddress:  doc.customerAddress,
      customerEmail:    doc.customerEmail,
      projectName:      doc.projectName,
      material:         doc.material,
      color:            doc.color,
      infill:           doc.infill,
      projectDescription: doc.projectDescription,
      projectNotes:     doc.projectNotes,
      pageSize:         doc.pageSize,
      docDiscount:      doc.discountAmount ?? 0,
      docRushPercent:   doc.rushAmount && doc.subtotal ? Math.round((doc.rushAmount / doc.subtotal) * 100) : 0,
      docTaxRate:       doc.calcTaxRate ?? 0,
      amountPaid:       doc.amountPaid ?? 0,
      paymentMethod:    doc.paymentMethod || 'Unknown / Review',
      pricingGuide:     doc.pricingGuide,
      termsNotes:       doc.termsNotes,
      standardTurnaround: doc.standardTurnaround,
      rushTurnaround:   doc.rushTurnaround,
    },
    lineItems: (doc.lineItems || []).map(li => ({
      desc: li.description, details: li.details,
      qty: li.quantity, rate: li.rate,
    })),
  });

  // Restore calculator
  restoreCalcState({
    grams: doc.calcGrams, hours: doc.calcHours,
    designHours: doc.calcDesignHours, setupFee: doc.calcSetupFee,
    postFee: doc.calcPostFee, gramRate: doc.calcGramRate,
    hourRate: doc.calcHourRate, designRate: doc.calcDesignRate,
    minimum: doc.calcMinimum, difficulty: doc.calcDifficulty,
    rush: doc.calcRush, discount: doc.calcDiscount,
    taxRate: doc.calcTaxRate,
  });
  normalizeDocumentInputs();
  syncDifficultyButtons();

  activeRecordId = doc.id;
  updateActiveBar();
  refreshInvoicePreview();
}

async function startNew(type = 'ESTIMATE') {
  const num = apiReady ? await api.nextNumber(type).then(r => r.number).catch(() => '') : '';
  startCleanDocument(type, num);
  activeRecordId = null;
  updateActiveBar();
  refreshInvoicePreview();
  showView('builder');
}

function startCleanDocument(type = 'ESTIMATE', number = '') {
  cancelPendingAutoSave();
  newDocument(type, number);
  restoreCalcState(defaultCalcState());
}

function applyDocumentPrefill(prefill = null) {
  if (!prefill) return;
  const fields = {
    docType: prefill.docType || '',
    docNumber: prefill.docNumber || '',
    docDate: prefill.docDate || '',
    dueDate: prefill.dueDate || '',
    docStatus: prefill.docStatus || prefill.status || '',
    preparedFor: prefill.preparedFor || prefill.customerName || '',
    customerName: prefill.customerName || '',
    customerPhone: prefill.customerPhone || '',
    customerAddress: prefill.customerAddress || '',
    customerEmail: prefill.customerEmail || '',
    projectName: prefill.projectName || '',
    material: prefill.material || '',
    color: prefill.color || '',
    infill: prefill.infill || '',
    projectDescription: prefill.projectDescription || '',
    projectNotes: prefill.projectNotes || '',
    pageSize: prefill.pageSize || '',
    docDiscount: prefill.docDiscount ?? prefill.discountAmount ?? '',
    docRushPercent: prefill.docRushPercent ?? '',
    docTaxRate: prefill.docTaxRate ?? prefill.calcTaxRate ?? '',
    amountPaid: prefill.amountPaid ?? '',
    paymentMethod: prefill.paymentMethod || '',
    pricingGuide: prefill.pricingGuide || '',
    termsNotes: prefill.termsNotes || '',
    standardTurnaround: prefill.standardTurnaround || '',
    rushTurnaround: prefill.rushTurnaround || ''
  };
  Object.entries(fields).forEach(([id, value]) => {
    if (value !== null && value !== undefined && value !== '') setVal(id, value);
  });
  if (prefill.docType) {
    const docType = el('docType');
    docType?.dispatchEvent(new Event('change'));
  }
  if (Array.isArray(prefill.lineItems) && prefill.lineItems.length) {
    const tbody = el('lineItemsBody');
    if (tbody) tbody.innerHTML = '';
    prefill.lineItems.forEach(item => addLineItem({
      description: item.description || item.desc || '',
      details: item.details || '',
      qty: item.quantity ?? item.qty ?? 1,
      rate: item.rate ?? item.amount ?? 0,
      source: 'assistant'
    }));
  }
  restoreCalcState({
    grams: prefill.calcGrams ?? 0,
    hours: prefill.calcHours ?? 0,
    designHours: prefill.calcDesignHours ?? 0,
    setupFee: prefill.calcSetupFee ?? 0,
    postFee: prefill.calcPostFee ?? 0,
    gramRate: prefill.calcGramRate ?? appConfig.calcGramRate ?? 0.05,
    hourRate: prefill.calcHourRate ?? appConfig.calcHourRate ?? 3,
    designRate: prefill.calcDesignRate ?? appConfig.calcDesignRate ?? 25,
    minimum: prefill.calcMinimum ?? appConfig.calcMinimum ?? 15,
    difficulty: prefill.calcDifficulty ?? 1,
    rush: prefill.calcRush ?? prefill.docRushPercent ?? 0,
    discount: prefill.calcDiscount ?? prefill.docDiscount ?? 0,
    taxRate: prefill.calcTaxRate ?? prefill.docTaxRate ?? 0,
  });
  normalizeDocumentInputs();
  syncDifficultyButtons();
  updateTotals();
  showAssistanceDraftIndicator(prefill);
}

function showAssistanceDraftIndicator(prefill) {
  const indicator = el('autosaveIndicator');
  if (!indicator || !prefill?.assistanceSource) return;
  const ai = String(prefill.assistanceSource).toUpperCase().includes('AI MODEL');
  indicator.className = `autosave-indicator assistant-draft ${ai ? 'assistant-ai' : 'assistant-rules'}`;
  indicator.innerHTML = `<span class="autosave-dot"></span>${ai ? 'AI MODEL DRAFT' : 'LOCAL RULES DRAFT'} · review before saving`;
}

function createDocumentSnapshot() {
  if (!document.getElementById('main') || !el('docType')) return null;
  try {
    return {
      activeRecordId,
      formData: getFormData(),
      calcState: getCalcState(),
      legacy: captureState(),
    };
  } catch {
    return null;
  }
}

function restoreDocumentSnapshot(snapshot) {
  if (!snapshot) return false;
  cancelPendingAutoSave();
  const doc = snapshot.formData || {};
  restoreState({
    formValues: {
      docType: doc.docType,
      docStatus: doc.status,
      docNumber: doc.docNumber,
      docDate: doc.docDate,
      dueDate: doc.dueDate,
      preparedFor: doc.preparedFor,
      customerName: doc.customerName,
      customerPhone: doc.customerPhone,
      customerAddress: doc.customerAddress,
      customerEmail: doc.customerEmail,
      projectName: doc.projectName,
      material: doc.material,
      color: doc.color,
      infill: doc.infill,
      projectDescription: doc.projectDescription,
      projectNotes: doc.projectNotes,
      pageSize: doc.pageSize,
      docDiscount: doc.discountAmount ?? 0,
      docRushPercent: doc.subtotal ? Math.round(((doc.rushAmount || 0) / doc.subtotal) * 100) : 0,
      docTaxRate: doc.calcTaxRate ?? 0,
      amountPaid: doc.amountPaid ?? 0,
      paymentMethod: doc.paymentMethod || 'Unknown / Review',
      pricingGuide: doc.pricingGuide,
      termsNotes: doc.termsNotes,
      standardTurnaround: doc.standardTurnaround,
      rushTurnaround: doc.rushTurnaround,
    },
    lineItems: (doc.lineItems || []).map(li => ({
      desc: li.description,
      details: li.details,
      qty: li.quantity,
      rate: li.rate,
    })),
  });
  restoreCalcState(snapshot.calcState || {});
  normalizeDocumentInputs();
  syncDifficultyButtons();
  activeRecordId = snapshot.activeRecordId || null;
  updateActiveBar();
  refreshInvoicePreview();
  return true;
}

async function saveRecord(forceNew = false) {
  const validation = validateDocumentInputs();
  if (!validation.valid) {
    showView(validation.view);
    validation.element?.focus();
    validation.element?.reportValidity();
    toast(validation.message, 'error', 5000);
    return null;
  }

  if (saveInFlight) {
    toast(`Still saving ${textVal('docNumber') || 'document'}...`, 'info', 1600);
    return saveInFlight;
  }

  saveInFlight = doSaveRecord(forceNew);
  try {
    return await saveInFlight;
  } finally {
    saveInFlight = null;
  }
}

async function doSaveRecord(forceNew = false) {
  setAutoSaveStatus('saving');
  try {
    if (!await ensureApiReady()) {
      throw new Error('The invoice API is not responding. Close any old EPATA.BusinessLedger process and reopen the rebuilt app.');
    }
    const formData = getFormData();
    const calcState = getCalcState();
    const legacy = captureState();

    const body = {
      ...formData,
      calcGrams:       parseFloat(calcState.grams)       || 0,
      calcHours:       parseFloat(calcState.hours)       || 0,
      calcDesignHours: parseFloat(calcState.designHours) || 0,
      calcSetupFee:    parseFloat(calcState.setupFee)    || 0,
      calcPostFee:     parseFloat(calcState.postFee)     || 0,
      calcGramRate:    parseFloat(calcState.gramRate)    || 0.05,
      calcHourRate:    parseFloat(calcState.hourRate)    || 3,
      calcDesignRate:  parseFloat(calcState.designRate)  || 25,
      calcMinimum:     parseFloat(calcState.minimum)     || 15,
      calcDifficulty:  parseFloat(calcState.difficulty)  || 1,
      calcRush:        parseFloat(calcState.rush)        || 0,
      calcDiscount:    parseFloat(calcState.discount)    || 0,
      calcTaxRate:     parseFloat(calcState.taxRate)     || 0,
      json: JSON.stringify(legacy),
    };

    let result;
    if (!forceNew && activeRecordId) {
      result = await api.update(activeRecordId, body);
      toast(`Saved ${result.docNumber || body.docNumber || 'document'}`, 'success');
    } else {
      result = await api.create(body);
      activeRecordId = result.id;
      toast(`Created ${result.docNumber || body.docNumber || 'document'}`, 'success');
    }
    if (result?.docNumber) setVal('docNumber', result.docNumber);

    updateActiveBar();
    await refreshRecords();
    refreshInvoicePreview();
    setAutoSaveStatus('saved');
    apiReady = true;
    setDbStatus(`Ready — saved ${result.docNumber || body.docNumber || 'document'}`, 'ready');
    return result;
  } catch (err) {
    toast('Save failed: ' + err.message, 'error');
    apiReady = false;
    setDbStatus('Save failed', 'error');
    setAutoSaveStatus('');
    throw err;
  }
}

async function ensureApiReady() {
  if (apiReady) return true;
  try {
    await api.health();
    apiReady = true;
    setDbStatus('Ready', 'ready');
    return true;
  } catch (err) {
    apiReady = false;
    setDbStatus('Server offline', 'error');
    console.error(err);
    return false;
  }
}


function wireLiveCalculationUpdates() {
  const builderView = el('view-builder');
  if (builderView) {
    const handler = (e) => {
      if (!e.target?.matches?.('input, select, textarea')) return;
      updateTotals();
      scheduleAutoSave();
    };
    builderView.addEventListener('input', handler);
    builderView.addEventListener('change', handler);
  }

  document.addEventListener('epata:totals-updated', () => {
    // Keeps summary values, database payloads, and generated PDFs using the same live totals.
  });

  el('projectName')?.addEventListener('change', applySelectedProduct);
}

async function loadProductLookups() {
  const res = await fetch('/api/lookups');
  if (!res.ok) return;
  const data = await res.json();
  productLookups = data.products || [];
  const list = el('productOptions');
  if (list) {
    list.innerHTML = productLookups.map(p => `<option value="${escapeHtml(p.name || '')}">${escapeHtml([p.sku, p.targetPrice ? money(p.targetPrice) : ''].filter(Boolean).join(' · '))}</option>`).join('');
  }
}

function applySelectedProduct() {
  const selected = productLookups.find(p => (p.name || '').toLowerCase() === textVal('projectName').toLowerCase());
  if (!selected) return;
  if (selected.material) setVal('material', selected.material);
  if (selected.color) setVal('color', selected.color);
  if (selected.grams != null) setVal('grams', selected.grams);
  if (selected.printHours != null) setVal('hours', selected.printHours);
  if (selected.materialCostPerGram != null) setVal('gramRate', selected.materialCostPerGram);
  if (selected.machineRatePerHour != null) setVal('hourRate', selected.machineRatePerHour);
  if (selected.designMinutes != null) setVal('designHours', Number(selected.designMinutes || 0) / 60);
  if (selected.packagingCost != null && !Number(textVal('postFee') || 0)) setVal('postFee', selected.packagingCost);
  if (selected.targetPrice != null && !Number(textVal('minimum') || 0)) setVal('minimum', selected.targetPrice);
  calculate();
  const hasMeaningfulLine = Array.from(document.querySelectorAll('#lineItemsBody tr')).some(row =>
    row.querySelector('.item-desc')?.value?.trim() || Number(row.querySelector('.item-rate')?.value || 0) > 0);
  if (selected.targetPrice && !hasMeaningfulLine) {
    const rows = document.querySelectorAll('#lineItemsBody tr');
    rows.forEach(row => row.remove());
    addLineItem({ description: selected.name, details: [selected.sku, selected.category, selected.material].filter(Boolean).join(' · '), qty: 1, rate: selected.targetPrice });
  }
  updateTotals();
  refreshInvoicePreview();
}

function defaultCalcState() {
  return {
    grams: 0,
    hours: 0,
    designHours: 0,
    setupFee: appConfig.calcSetupFee ?? 0,
    postFee: appConfig.calcPostFee ?? 0,
    gramRate: appConfig.calcGramRate ?? 0.05,
    hourRate: appConfig.calcHourRate ?? 3,
    designRate: appConfig.calcDesignRate ?? 25,
    minimum: appConfig.calcMinimum ?? 15,
    difficulty: 1,
    rush: 0,
    discount: 0,
    taxRate: 0,
  };
}

// ── Auto-save ─────────────────────────────────────────
function scheduleAutoSave() {
  // Intentionally disabled. Estimates/invoices should only change on explicit Save,
  // Save as New, Download PDF, or another direct user action.
  cancelPendingAutoSave();
}

function cancelPendingAutoSave() {
  clearTimeout(autoSaveTimer);
  autoSaveTimer = null;
  setAutoSaveStatus('');
}

function setAutoSaveStatus(state) {
  const ind = el('autosaveIndicator');
  if (!ind) return;
  ind.className = `autosave-indicator ${state}`;
  ind.innerHTML = `<span class="autosave-dot"></span>${
    state === 'saving' ? 'Saving…' : state === 'saved' ? 'Saved' : ''}`;
  if (state === 'saved') setTimeout(() => { ind.innerHTML = ''; ind.className = 'autosave-indicator'; }, 2500);
}

// ── PDF ───────────────────────────────────────────────
async function onGeneratePdf(preview = false) {
  try {
    // Preview is read-only. Download saves first so the exported PDF is tracked.
    if (!preview && apiReady && !await saveRecord(false)) return;

    const formData = getFormData();
    const data = { ...formData, ...appConfig, brandColor: appConfig.brandColor || '#17468f' };
    refreshInvoicePreview();
    await generatePdf(data, preview);
  } catch (e) {
    toast('PDF error: ' + e.message, 'error');
  }
}

// ── Calculator → Builder push ─────────────────────────
function onPushToBuilder() {
  const validation = validateCalculatorInputs();
  if (!validation.valid) {
    showView('calculator');
    validation.element?.focus();
    validation.element?.reportValidity();
    toast(validation.message, 'error', 5000);
    return;
  }

  const calc = calculate();
  const selectedProduct = productLookups.find(p => (p.name || '').toLowerCase() === textVal('projectName').toLowerCase());
  pushToBuilder(calc, getLineItems, addLineItem, {
    productName: textVal('projectName'),
    productDetails: selectedProduct
      ? [selectedProduct.sku, selectedProduct.category, selectedProduct.material, selectedProduct.color].filter(Boolean).join(' · ')
      : ''
  });
  updateTotals();
  refreshInvoicePreview();
  scheduleAutoSave();
  toast('Calculator values pushed to builder', 'success');
  showView('builder');
}

function refreshInvoicePreview() {
  const frame = el('invoicePreviewFrame');
  if (!frame) return;
  const data = { ...getFormData(), ...appConfig, brandColor: appConfig.brandColor || '#17468f' };
  frame.srcdoc = renderInvoiceHtml(data);
  frame.onload = () => resizePreviewFrame(frame);
}

function resizePreviewFrame(frame) {
  try {
    const doc = frame.contentDocument || frame.contentWindow?.document;
    const height = Math.max(1414, doc?.documentElement?.scrollHeight || 0, doc?.body?.scrollHeight || 0);
    frame.style.height = `${height}px`;
  } catch {
    frame.style.height = '1414px';
  }
}

// ── Records ───────────────────────────────────────────
async function onLoadRecord(id) {
  try {
    await loadRecord(id, (newId) => { activeRecordId = newId; });
    updateActiveBar();
    showView('builder');
  } catch (e) {
    toast('Could not load record: ' + e.message, 'error');
  }
}

async function onDuplicateRecord(id) {
  try { await duplicateRecord(id); } catch (e) { toast('Duplicate failed: ' + e.message, 'error'); }
}

async function onConvertEstimate(id) {
  try {
    await convertEstimateToInvoice(id, (doc) => {
      if (!doc?.id || doc.docType !== 'INVOICE') {
        throw new Error('The server did not return the new invoice record.');
      }
      onLoadRecord(doc.id);
    });
  } catch (e) {
    toast('Invoice creation failed: ' + e.message, 'error');
  }
}

async function onDeleteRecord(id) {
  try { await deleteRecord(id, activeRecordId, (newId) => { activeRecordId = newId; updateActiveBar(); }); }
  catch (e) { toast('Archive failed: ' + e.message, 'error'); }
}

async function onRestoreRecord(id) {
  try { await restoreRecord(id); }
  catch (e) { toast('Restore failed: ' + e.message, 'error'); }
}

// ── Settings ──────────────────────────────────────────
function applyConfigToSettings(cfg) {
  if (!cfg) return;
  const map = {
    businessName: cfg.businessName, businessLocation: cfg.businessLocation,
    businessEmail: cfg.businessEmail, businessPhone: cfg.businessPhone,
    businessWebsite: cfg.businessWebsite, businessEtsy: cfg.businessEtsy,
    businessInstagram: cfg.businessInstagram, businessFacebook: cfg.businessFacebook,
    brandColor: cfg.brandColor,
    sCalcGramRate: cfg.calcGramRate, sCalcHourRate: cfg.calcHourRate,
    sCalcDesignRate: cfg.calcDesignRate, sCalcSetupFee: cfg.calcSetupFee,
    sCalcPostFee: cfg.calcPostFee, sCalcMinimum: cfg.calcMinimum,
  };
  Object.entries(map).forEach(([id, v]) => { if (el(id) && v != null) el(id).value = v; });
  normalizeSettingsInputs();
}

async function saveSettings() {
  const validation = validateSettingsInputs();
  if (!validation.valid) {
    showView('settings');
    validation.element?.focus();
    validation.element?.reportValidity();
    toast(validation.message, 'error', 5000);
    return null;
  }

  const body = {
    businessName: textVal('businessName'), businessLocation: textVal('businessLocation'),
    businessEmail: textVal('businessEmail'), businessPhone: textVal('businessPhone'),
    businessWebsite: textVal('businessWebsite'), businessEtsy: textVal('businessEtsy'),
    businessInstagram: textVal('businessInstagram'), businessFacebook: textVal('businessFacebook'),
    brandColor: textVal('brandColor') || '#17468f',
    calcGramRate:   parseFloat(el('sCalcGramRate')?.value)   || 0.05,
    calcHourRate:   parseFloat(el('sCalcHourRate')?.value)   || 3,
    calcDesignRate: parseFloat(el('sCalcDesignRate')?.value) || 25,
    calcSetupFee:   parseFloat(el('sCalcSetupFee')?.value)   || 0,
    calcPostFee:    parseFloat(el('sCalcPostFee')?.value)    || 0,
    calcMinimum:    parseFloat(el('sCalcMinimum')?.value)    || 15,
  };
  try {
    appConfig = await api.saveConfig(body);
    toast('Settings saved', 'success');
  } catch (e) {
    toast('Settings save failed: ' + e.message, 'error');
  }
}

// ── Database import ───────────────────────────────────
async function onImportDb(e) {
  const file = e.target.files?.[0];
  e.target.value = '';
  if (!file) return;
  if (!confirm('Importing will replace the current database. Backup first? Continue?')) return;
  try {
    await api.importDb(file);
    await refreshRecords();
    toast('Database imported successfully', 'success');
  } catch (err) {
    toast('Import failed: ' + err.message, 'error');
  }
}

// ── UI helpers ────────────────────────────────────────
function updateActiveBar() {
  const bar = el('activeRecordBar');
  const txt = el('activeRecordText');
  if (!bar || !txt) return;
  if (activeRecordId) {
    bar.classList.remove('hidden');
    txt.textContent = `Editing ${textVal('docNumber') || `#${activeRecordId}`} — Ctrl+S to save`;
  } else {
    bar.classList.remove('hidden');
    txt.textContent = 'New document — Ctrl+S to save';
  }
}

function setDbStatus(msg, state = '') {
  const dot = el('dbDot');
  const txt = el('db-status-text');
  if (dot) dot.className = `db-dot ${state}`;
  if (txt) txt.textContent = msg;
}

function on(id, fn) {
  const e = el(id);
  if (!e) return;
  if (e.type === 'file') e.addEventListener('change', fn);
  else e.addEventListener('click', fn);
}

function setText(id, v) { const e = el(id); if (e) e.textContent = v ?? ''; }

function copyText(text) {
  navigator.clipboard?.writeText(text)
    .then(() => toast('Copied to clipboard!', 'success', 2000))
    .catch(() => toast('Copy failed', 'error'));
}

// ── Boot ──────────────────────────────────────────────
document.addEventListener('DOMContentLoaded', () => {
  if (document.getElementById('sidebar') && document.getElementById('main')) init();
});
