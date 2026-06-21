// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Main Application
//  .NET 10 SPA Entry Point
// ═══════════════════════════════════════════════════════

import { api }                                    from './api.js?v=5';
import { el, toast, money, setVal, textVal,
         fmtDateTime, statusBadge, typeBadge,
         debounce, escapeHtml, todayStr }         from './utils.js?v=4';
import { initCalculator, calculate, getCalcState,
         restoreCalcState, pushToBuilder, buildDefaultCalculatorState,
         applyConfigDefaults, syncDifficultyButtons } from './calculator.js?v=5';
import { initBuilder, addLineItem, removeLineItem,
         getLineItems, updateTotals, captureState,
         restoreState, getFormData, newDocument,
         remapStatusForDocType } from './builder.js?v=11';
import { initRecords, refreshRecords, loadRecord,
         duplicateRecord, deleteRecord, exportCsv,
         getRecords, convertEstimateToInvoice,
         restoreRecord }                          from './records.js?v=12';
import { buildSaveRequestPlan, canReuseInFlightSave, getSaveIntent } from './save-intent.js?v=2';
import { activeRecordBarText, buildSaveFailureUiState,
         emptyActiveRecordIdentity, identityAfterArchivedRecord,
         identityFromDocument, normalizeActiveRecordIdentity } from './document-session.js?v=1';
import { generatePdf, renderInvoiceHtml }         from './pdf.js?v=5';
import { buildProductOptionsHtml, buildSelectedProductPatch,
         findProductByName }                      from './product-lookups.js?v=1';
import { initInputValidation, normalizeDocumentInputs, normalizeSettingsInputs,
         validateCalculatorInputs, validateDocumentInputs,
         validateSettingsInputs } from './validation.js?v=4';

// ── State ─────────────────────────────────────────────
let activeRecordId  = null;
let activeRecordType = null;
let activeRecordNumber = null;
let apiReady        = false;
let appConfig       = {};
let autoSaveTimer   = null;
let saveInFlight    = null;
let saveInFlightIntent = null;
let documentSessionVersion = 0;
let productLookups  = [];
const actionInFlight = new Set();
const AUTOSAVE_MS   = 30_000;

async function runExclusiveToolAction(key, busyMessage, action) {
  const actionKey = String(key || 'tool-action');
  if (actionInFlight.has(actionKey)) {
    toast(busyMessage || 'Action already in progress.', 'info');
    return null;
  }

  actionInFlight.add(actionKey);
  try {
    return await action();
  } finally {
    actionInFlight.delete(actionKey);
  }
}

// ── Init ──────────────────────────────────────────────
export async function init(initialView = 'dashboard') {
  const options = typeof initialView === 'object' && initialView !== null ? initialView : {};
  if (typeof initialView === 'object' && initialView !== null) initialView = options.initialView || 'dashboard';

  // Expose global handlers for inline onclick attributes
  window._builderUpdate  = () => { updateTotals(); refreshInvoicePreview(); scheduleAutoSave(); };
  window._removeLineItem = removeLineItemAndRefresh;
  window._loadRecord     = (id) => onLoadRecord(id);
  window._dupeRecord     = (id) => onDuplicateRecord(id);
  window._convertEstimate = (id) => onConvertEstimate(id);
  window._delRecord      = (id) => onDeleteRecord(id);
  window._restoreRecord  = (id) => onRestoreRecord(id);
  window._copyText       = copyText;
  window._invoiceToolSnapshot = createDocumentSnapshot;
  window._invoiceToolShowView = showView;
  window._invoiceToolIsSaving = () => !!saveInFlight;

  // Override the shim — expose addLineItem globally for onclick handlers
  window.addLineItem     = addLineItemAndRefresh;
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
  on('btnImportPdfDraft', () => el('invoicePdfImportFile')?.click());
  on('invoicePdfImportFile', (e) => onImportPdfDraft(e));
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
      clearActiveRecordIdentity();
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
          <td class="doc-number"><button class="record-link" type="button" onclick="${r.sourceKind === 'receivable' ? `window.openLedgerEntityRecord && window.openLedgerEntityRecord('receivables', ${r.sourceId || r.id})` : `window._loadRecord(${r.id})`}">${escapeHtml(r.docNumber || '—')}</button></td>
          <td>${typeBadge(r.docType)}</td>
          <td>${statusBadge(r.status||'Draft')}</td>
          <td>${r.customerName||'—'}</td>
          <td class="num">${money(r.total)}</td>
          <td class="muted">${fmtDateTime(r.updatedAt)}</td>
        </tr>`).join('')
        : `<tr><td colspan="6"><div class="empty-state" style="padding:30px"><div class="empty-icon">📄</div><div class="empty-title">No records yet</div><div class="empty-desc">Saved estimates and invoices will appear here after you create them.</div></div></td></tr>`;
    }
  } catch (e) {
    console.error('Stats error', e);
  }
}

// ── Document lifecycle ────────────────────────────────
function onDocumentLoaded(doc) {
  markDocumentSessionChanged();
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

  setActiveRecordIdentity(doc);
  updateActiveBar();
  refreshInvoicePreview();
}

async function startNew(type = 'ESTIMATE') {
  const num = apiReady ? await api.nextNumber(type).then(r => r.number).catch(() => '') : '';
  startCleanDocument(type, num);
  clearActiveRecordIdentity();
  updateActiveBar();
  refreshInvoicePreview();
  showView('builder');
}

function startCleanDocument(type = 'ESTIMATE', number = '') {
  markDocumentSessionChanged();
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
    paymentMethod: prefill.paymentMethod || 'Unknown / Review',
    pricingGuide: prefill.pricingGuide || '',
    termsNotes: prefill.termsNotes || '',
    standardTurnaround: prefill.standardTurnaround || '',
    rushTurnaround: prefill.rushTurnaround || ''
  };
  if (fields.docType) {
    setVal('docType', fields.docType);
    el('docType')?.dispatchEvent(new Event('change'));
  }
  Object.entries(fields).forEach(([id, value]) => {
    if (id === 'docType' || id === 'docStatus') return;
    if (value !== null && value !== undefined && value !== '') setVal(id, value);
  });
  if (fields.docStatus) {
    const docType = fields.docType || textVal('docType') || 'ESTIMATE';
    setVal('docStatus', remapStatusForDocType(docType, fields.docStatus));
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
      activeRecordType,
      activeRecordNumber,
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
  markDocumentSessionChanged();
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
  applyActiveRecordIdentity({
    activeRecordId: snapshot.activeRecordId || null,
    activeRecordType: snapshot.activeRecordType || doc.docType || null,
    activeRecordNumber: snapshot.activeRecordNumber || doc.docNumber || null,
  });
  updateActiveBar();
  refreshInvoicePreview();
  return true;
}

function getActiveRecordIdentity() {
  return normalizeActiveRecordIdentity({ activeRecordId, activeRecordType, activeRecordNumber });
}

function applyActiveRecordIdentity(identity) {
  const normalized = normalizeActiveRecordIdentity(identity);
  activeRecordId = normalized.activeRecordId;
  activeRecordType = normalized.activeRecordType;
  activeRecordNumber = normalized.activeRecordNumber;
}

function setActiveRecordIdentity(doc) {
  applyActiveRecordIdentity(identityFromDocument(doc));
}

function clearActiveRecordIdentity() {
  applyActiveRecordIdentity(emptyActiveRecordIdentity());
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
    const requestedIntent = getSaveIntent(forceNew, {
      activeRecordId,
      activeRecordType,
      requestedDocType: textVal('docType') || 'ESTIMATE',
    });
    if (canReuseInFlightSave(saveInFlightIntent, requestedIntent)) {
      toast(`Still saving ${textVal('docNumber') || 'document'}...`, 'info', 1600, { key: 'invoice-save-status' });
      return saveInFlight;
    }
    toast('Finish the current save before starting a different save action.', 'error', 3200);
    return null;
  }

  saveInFlightIntent = getSaveIntent(forceNew, {
    activeRecordId,
    activeRecordType,
    requestedDocType: textVal('docType') || 'ESTIMATE',
  });
  saveInFlight = doSaveRecord(forceNew, documentSessionVersion);
  try {
    return await saveInFlight;
  } finally {
    saveInFlight = null;
    saveInFlightIntent = null;
  }
}

async function doSaveRecord(forceNew = false, saveSessionVersion = documentSessionVersion) {
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
      calcTaxRate:     parseFloat(formData.docTaxRate)   || parseFloat(calcState.taxRate) || 0,
      json: JSON.stringify(legacy),
    };

    const preliminaryPlan = buildSaveRequestPlan(forceNew, {
      activeRecordId,
      activeRecordType,
      activeRecordNumber,
      requestedDocType: body.docType,
      body,
    });
    const needsNewNumber = preliminaryPlan.intent.forceNew || preliminaryPlan.intent.typeChanged;
    const nextNumber = needsNewNumber ? await api.nextNumber(body.docType).then(r => r.number).catch(() => '') : '';
    const savePlan = buildSaveRequestPlan(forceNew, {
      activeRecordId,
      activeRecordType,
      activeRecordNumber,
      requestedDocType: body.docType,
      body,
      nextNumber,
    });
    const saveBody = savePlan.body;

    let result;
    if (savePlan.action === 'update') {
      result = await api.update(savePlan.updateId, saveBody);
      toast(`Saved ${result.docNumber || saveBody.docNumber || 'document'}`, 'success', 3000, { key: 'invoice-save-status' });
    } else {
      result = await api.create(saveBody);
      toast(savePlan.intent.typeChanged
        ? `Created new ${result.docType === 'INVOICE' ? 'invoice' : 'estimate'} ${result.docNumber}; original unchanged`
        : `Created ${result.docNumber || saveBody.docNumber || 'document'}`, 'success', 3000, { key: 'invoice-save-status' });
    }

    const stillActiveSession = saveSessionVersion === documentSessionVersion;
    if (stillActiveSession) {
      if (result?.docNumber) setVal('docNumber', result.docNumber);
      if (result?.docType) setVal('docType', result.docType);
      if (result) setActiveRecordIdentity(result);
      updateActiveBar();
      refreshInvoicePreview();
    } else {
      toast(`Saved ${result.docNumber || saveBody.docNumber || 'document'} in the background; current record unchanged`, 'success', 3000);
    }
    await refreshRecords();
    if (stillActiveSession) {
      setAutoSaveStatus('saved');
      setDbStatus(`Ready — saved ${result.docNumber || saveBody.docNumber || 'document'}`, 'ready');
    } else {
      setDbStatus('Ready', 'ready');
    }
    apiReady = true;
    return result;
  } catch (err) {
    const failureState = buildSaveFailureUiState({
      identity: getActiveRecordIdentity(),
      activeBarText: el('activeRecordText')?.textContent || '',
    });
    const stillActiveSession = saveSessionVersion === documentSessionVersion;
    toast('Save failed: ' + err.message, 'error', 3000, { key: 'invoice-save-status' });
    apiReady = false;
    if (stillActiveSession) {
      setDbStatus(failureState.dbStatusText, failureState.dbStatusState);
      setAutoSaveStatus(failureState.autoSaveStatus);
    } else {
      setDbStatus('Server needs attention; current record unchanged', 'error');
    }
    throw err;
  }
}

function markDocumentSessionChanged() {
  documentSessionVersion += 1;
  return documentSessionVersion;
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

function addLineItemAndRefresh(item = {}) {
  addLineItem(item);
  updateTotals();
  refreshInvoicePreview();
  scheduleAutoSave();
}

function removeLineItemAndRefresh(btn) {
  removeLineItem(btn);
  updateTotals();
  refreshInvoicePreview();
  scheduleAutoSave();
}

async function loadProductLookups() {
  const res = await fetch('/api/lookups');
  if (!res.ok) return;
  const data = await res.json();
  productLookups = data.products || [];
  const list = el('productOptions');
  if (list) {
    list.innerHTML = buildProductOptionsHtml(productLookups);
  }
}

function applySelectedProduct() {
  const selected = findProductByName(productLookups, textVal('projectName'));
  if (!selected) return;
  const hasMeaningfulLine = Array.from(document.querySelectorAll('#lineItemsBody tr')).some(row =>
    row.querySelector('.item-desc')?.value?.trim() || Number(row.querySelector('.item-rate')?.value || 0) > 0);
  const patch = buildSelectedProductPatch(selected, {
    postFee: textVal('postFee'),
    minimum: textVal('minimum'),
    hasMeaningfulLine,
  });
  Object.entries(patch.fields).forEach(([field, value]) => setVal(field, value));
  calculate();
  if (patch.lineItem) {
    const rows = document.querySelectorAll('#lineItemsBody tr');
    rows.forEach(row => row.remove());
    addLineItem(patch.lineItem);
  }
  updateTotals();
  refreshInvoicePreview();
}

function defaultCalcState() {
  return buildDefaultCalculatorState(appConfig);
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
  const selectedProduct = findProductByName(productLookups, textVal('projectName'));
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
    await loadRecord(id, (newId) => {
      applyActiveRecordIdentity({ ...getActiveRecordIdentity(), activeRecordId: newId });
    });
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
    const doc = await convertEstimateToInvoice(id);
    if (!doc?.id || doc.docType !== 'INVOICE') {
      throw new Error('The server did not return the new invoice record.');
    }
    onDocumentLoaded(doc);
    showView('builder');
  } catch (e) {
    toast('Invoice creation failed: ' + e.message, 'error');
  }
}

async function onDeleteRecord(id) {
  try {
    await deleteRecord(id, activeRecordId, (newId) => {
      const nextIdentity = newId
        ? { ...getActiveRecordIdentity(), activeRecordId: newId }
        : identityAfterArchivedRecord(getActiveRecordIdentity(), id);
      applyActiveRecordIdentity(nextIdentity);
      updateActiveBar();
    });
  }
  catch (e) { toast('Archive failed: ' + e.message, 'error'); }
}

async function onRestoreRecord(id) {
  try { await restoreRecord(id); }
  catch (e) { toast('Restore failed: ' + e.message, 'error'); }
}

async function onImportPdfDraft(event) {
  const input = event?.target;
  const file = input?.files?.[0];
  if (input) input.value = '';
  if (!file) return;

  return runExclusiveToolAction('pdf-draft-import', 'PDF import already in progress.', async () => {
  try {
    if (!await ensureApiReady()) {
      throw new Error('The invoice API is not responding. Reopen the rebuilt app and try again.');
    }

    setAutoSaveStatus('saving');
    const result = await api.importPdfDraft(file);
    const prefill = result?.prefill;
    if (!prefill) throw new Error('The server did not return mapped invoice fields.');

    const nextNumber = !prefill.docNumber
      ? await api.nextNumber(prefill.docType || 'ESTIMATE').then(r => r.number).catch(() => '')
      : '';
    startCleanDocument(prefill.docType || 'ESTIMATE', prefill.docNumber || nextNumber);
    clearActiveRecordIdentity();
    applyDocumentPrefill(prefill);
    setAutoSaveStatus('');
    const warningText = (result.warnings || []).length ? ` Review warnings before saving.` : '';
    toast(`Mapped ${prefill.docType === 'INVOICE' ? 'invoice' : 'estimate'} PDF into an unsaved draft.${warningText}`, 'success', 7000);
    setDbStatus(`Ready — imported PDF draft ${textVal('docNumber') || ''}`.trim(), 'ready');
    showView('builder');
  } catch (e) {
    setAutoSaveStatus('');
    toast('PDF import failed: ' + e.message, 'error', 7000);
  }
  });
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
  return runExclusiveToolAction('database-import', 'Database import already in progress.', async () => {
    if (!confirm('Importing will replace the current database. Backup first? Continue?')) return;
    try {
      await api.importDb(file);
      await refreshRecords();
      toast('Database imported successfully', 'success');
    } catch (err) {
      toast('Import failed: ' + err.message, 'error');
    }
  });
}

// ── UI helpers ────────────────────────────────────────
function updateActiveBar() {
  const bar = el('activeRecordBar');
  const txt = el('activeRecordText');
  if (!bar || !txt) return;
  bar.classList.remove('hidden');
  txt.textContent = activeRecordBarText(getActiveRecordIdentity(), textVal('docNumber'));
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
