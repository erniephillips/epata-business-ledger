// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Main Application
//  .NET 10 SPA Entry Point
// ═══════════════════════════════════════════════════════

import { api }                                    from './api.js?v=6';
import { el, toast, money, setVal, textVal,
         fmtDateTime, statusBadge, typeBadge,
         debounce, escapeHtml, todayStr }         from './utils.js?v=4';
import { initCalculator, calculate, getCalcState,
         restoreCalcState, pushToBuilder, buildDefaultCalculatorState,
         applyConfigDefaults, syncDifficultyButtons } from './calculator.js?v=5';
import { initBuilder, addLineItem, removeLineItem,
         getLineItems, updateTotals, captureState,
         restoreState, getFormData, newDocument,
         remapStatusForDocType } from './builder.js?v=12';
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
const aiAssistantState = {
  files: [],
  messages: [],
  draft: null,
  status: null,
  limits: null,
  lastFocus: null,
};

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
  document.querySelectorAll('[data-ai-assistant-open]').forEach(button =>
    button.addEventListener('click', () => openAiAssistantModal()));

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
  const calcFields = {
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
  };
  restoreCalcState(calcFields);
  Object.keys(calcFields).forEach(id => dispatchFieldEvents(id));

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
    setValAndNotify('docType', fields.docType, ['change']);
  }
  Object.entries(fields).forEach(([id, value]) => {
    if (id === 'docType' || id === 'docStatus') return;
    if (value !== null && value !== undefined && value !== '') setValAndNotify(id, value);
  });
  if (fields.docStatus) {
    const docType = fields.docType || textVal('docType') || 'ESTIMATE';
    setValAndNotify('docStatus', remapStatusForDocType(docType, fields.docStatus), ['change']);
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
  normalizeDocumentInputs();
  syncDifficultyButtons();
  updateTotals();
  showAssistanceDraftIndicator(prefill);
}

function setValAndNotify(id, value, eventNames = ['input', 'change']) {
  setVal(id, value);
  dispatchFieldEvents(id, eventNames);
}

function dispatchFieldEvents(id, eventNames = ['input', 'change']) {
  const node = el(id);
  if (!node) return;
  eventNames.forEach(name => node.dispatchEvent(new Event(name, { bubbles: true })));
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

// ── Local AI estimate assistant modal ─────────────────
function ensureAiAssistantModal() {
  let modal = el('aiAssistantModal');
  if (modal) return modal;

  document.body.insertAdjacentHTML('beforeend', `
    <div id="aiAssistantModal" class="ai-assistant-modal hidden" role="dialog" aria-modal="true" aria-labelledby="aiAssistantTitle">
      <div class="ai-assistant-scrim" data-ai-assistant-close></div>
      <section class="ai-assistant-panel" role="document">
        <header class="ai-assistant-header">
          <div>
            <span class="ai-assistant-kicker">Local AI estimate assistant</span>
            <h2 id="aiAssistantTitle">Build a draft from anything you have</h2>
            <p>Paste context, add files or images, chat for missing details, then fill a reviewable estimate or invoice draft.</p>
          </div>
          <div class="ai-assistant-header-actions">
            <button class="btn-ghost btn-sm" id="btnAiAssistantRefresh" type="button">Refresh LLM</button>
            <button class="btn-danger btn-sm" data-ai-assistant-close type="button" aria-label="Close AI intake modal">Close</button>
          </div>
        </header>

        <div class="ai-assistant-status">
          <span id="aiAssistantStatePill" class="ai-state-pill loading">Checking Local AI</span>
          <p id="aiAssistantStatusText">Opening the assistant and checking LM Studio...</p>
          <div id="aiAssistantStatusMeta" class="ai-assistant-meta"></div>
        </div>

        <div class="ai-assistant-grid">
          <section class="ai-assistant-source">
            <div class="ai-panel-title">
              <div>
                <h3>Context packet</h3>
                <p>Use this as the big working area. The final AI call sees this packet, uploaded readable docs, images, chat notes, app help, pricing rules, builder fields, and product costing.</p>
              </div>
              <button class="btn-ghost btn-sm" id="btnAiAssistantClear" type="button">Clear</button>
            </div>
            <label class="ai-field">
              <span>Paste request, messages, slicer notes, measurements, prices, or instructions</span>
              <textarea id="aiAssistantContext" class="ai-assistant-context" rows="16" placeholder="Paste the customer conversation, project notes, measurements, colors, material, deadlines, budget, slicer output, or anything else the estimate should consider..."></textarea>
            </label>
            <div class="ai-assistant-count-row">
              <span id="aiAssistantTextCount">0 characters</span>
              <span id="aiAssistantLimits">Loading limits...</span>
            </div>
            <label class="ai-field">
              <span>Product or source URLs, one per line</span>
              <textarea id="aiAssistantUrls" class="ai-url-source" rows="3" placeholder="https://www.etsy.com/listing/...&#10;https://supplier-or-reference-page.example/item"></textarea>
            </label>
            <div id="aiAssistantDropZone" class="ai-upload-drop" tabindex="0">
              <input id="aiAssistantFiles" class="hidden" type="file" multiple accept=".txt,.md,.eml,.csv,.json,.pdf,.docx,.png,.jpg,.jpeg,.webp,text/*,application/pdf,application/vnd.openxmlformats-officedocument.wordprocessingml.document,image/png,image/jpeg,image/webp">
              <label for="aiAssistantFiles">Add documents, images, PDFs, text, CSV, JSON, or email files</label>
              <small>Readable docs become text. Pictures are sent as references when the loaded local model can understand images.</small>
            </div>
            <div id="aiAssistantFileList" class="ai-file-list">No files selected.</div>
            <div class="ai-assistant-options">
              <label><span>Draft type</span><select id="aiAssistantDocType"><option value="ESTIMATE">Estimate</option><option value="INVOICE">Invoice</option></select></label>
              <label class="ai-check"><input id="aiAssistantIncludeCurrent" type="checkbox" checked> Include current calculator and form fields as context</label>
              <label class="ai-check"><input id="aiAssistantUseChat" type="checkbox" checked> Include this chat transcript in the final draft packet</label>
            </div>
          </section>

          <section class="ai-assistant-work">
            <div class="ai-panel-title">
              <div>
                <h3>Chat and draft</h3>
                <p>Ask the local model what is missing or how it reads the packet before generating fields.</p>
              </div>
            </div>
            <div id="aiAssistantChatLog" class="ai-chat-log" aria-live="polite"></div>
            <label class="ai-field">
              <span>Ask Local AI for clarification or next questions</span>
              <textarea id="aiAssistantQuestion" rows="3" placeholder="Example: What questions should I ask before quoting this?"></textarea>
            </label>
            <div class="ai-assistant-actions">
              <button class="btn-ghost" id="btnAiAssistantAsk" type="button">Ask Local AI</button>
              <button class="btn-ghost" id="btnAiAssistantMissing" type="button">Find missing details</button>
            </div>
            <div id="aiAssistantDraftPreview" class="ai-draft-preview">
              <div class="empty-state compact">
                <div class="empty-title">No draft generated yet.</div>
                <div class="empty-desc">When you are ready, generate and fill the builder. Nothing saves automatically.</div>
              </div>
            </div>
          </section>
        </div>

        <footer class="ai-assistant-footer">
          <span id="aiAssistantFooterNote">Local AI starts on open when possible. Review every generated field before saving.</span>
          <button class="btn btn-lg" id="btnAiAssistantGenerate" type="button">Generate + Fill Draft</button>
        </footer>
      </section>
    </div>`);

  modal = el('aiAssistantModal');
  modal.querySelectorAll('[data-ai-assistant-close]').forEach(node => node.addEventListener('click', closeAiAssistantModal));
  el('btnAiAssistantRefresh')?.addEventListener('click', () => refreshAiAssistantStatus(true));
  el('btnAiAssistantClear')?.addEventListener('click', clearAiAssistantModal);
  el('btnAiAssistantAsk')?.addEventListener('click', () => askAiAssistant());
  el('btnAiAssistantMissing')?.addEventListener('click', () => askAiAssistant('What details are missing before this can become a reliable estimate or invoice draft?'));
  el('btnAiAssistantGenerate')?.addEventListener('click', generateAiAssistantDraft);
  el('aiAssistantFiles')?.addEventListener('change', e => {
    addAiAssistantFiles(Array.from(e.target?.files || []));
    e.target.value = '';
  });
  el('aiAssistantContext')?.addEventListener('input', updateAiAssistantTextCount);
  el('aiAssistantQuestion')?.addEventListener('keydown', e => {
    if ((e.ctrlKey || e.metaKey) && e.key === 'Enter') {
      e.preventDefault();
      askAiAssistant();
    }
  });
  const dropZone = el('aiAssistantDropZone');
  if (dropZone) {
    dropZone.addEventListener('dragover', e => { e.preventDefault(); dropZone.classList.add('dragging'); });
    dropZone.addEventListener('dragleave', () => dropZone.classList.remove('dragging'));
    dropZone.addEventListener('drop', e => {
      e.preventDefault();
      dropZone.classList.remove('dragging');
      addAiAssistantFiles(Array.from(e.dataTransfer?.files || []));
    });
    dropZone.addEventListener('keydown', e => {
      if (e.key === 'Enter' || e.key === ' ') {
        e.preventDefault();
        el('aiAssistantFiles')?.click();
      }
    });
  }

  document.addEventListener('keydown', e => {
    if (!modal.classList.contains('hidden') && e.key === 'Escape') closeAiAssistantModal();
  });

  renderAiAssistantChat();
  renderAiAssistantFiles();
  return modal;
}

async function openAiAssistantModal() {
  const modal = ensureAiAssistantModal();
  aiAssistantState.lastFocus = document.activeElement;
  const docType = textVal('docType') || 'ESTIMATE';
  if (el('aiAssistantDocType')) el('aiAssistantDocType').value = docType === 'INVOICE' ? 'INVOICE' : 'ESTIMATE';
  updateAiAssistantTextCount();
  modal.classList.remove('hidden');
  document.body.classList.add('ai-assistant-open');
  el('aiAssistantContext')?.focus();
  await refreshAiAssistantStatus(true);
}

function closeAiAssistantModal() {
  const modal = el('aiAssistantModal');
  if (!modal) return;
  modal.classList.add('hidden');
  document.body.classList.remove('ai-assistant-open');
  aiAssistantState.lastFocus?.focus?.();
}

function clearAiAssistantModal() {
  if (!confirm('Clear the assistant context, files, chat, and draft preview?')) return;
  aiAssistantState.files = [];
  aiAssistantState.messages = [];
  aiAssistantState.draft = null;
  if (el('aiAssistantContext')) el('aiAssistantContext').value = '';
  if (el('aiAssistantUrls')) el('aiAssistantUrls').value = '';
  if (el('aiAssistantQuestion')) el('aiAssistantQuestion').value = '';
  renderAiAssistantFiles();
  renderAiAssistantChat();
  updateAiAssistantTextCount();
  renderAiAssistantDraftPreview(null);
}

async function refreshAiAssistantStatus(autoStart = false) {
  setAiAssistantStatus({ state: autoStart ? 'Starting Local AI...' : 'Checking Local AI', className: 'loading', message: 'Checking LM Studio and the selected local model...' });
  try {
    let status = await api.localAiStatus();
    if (autoStart && !status.modelReady) {
      setAiAssistantStatus({
        state: status.serverOnline ? 'Loading selected model...' : 'Starting LM Studio...',
        className: 'loading',
        message: status.message || 'Starting the local server and loading the selected model.'
      });
      const started = await api.startLocalAi();
      status = started.status || await api.localAiStatus();
      if (!started.success) {
        setAiAssistantStatus({
          state: 'Setup needs attention',
          className: 'warn',
          message: started.message || status.message || 'Local AI did not become ready.',
          status
        });
        return status;
      }
    }
    aiAssistantState.status = status;
    setAiAssistantStatus({
      state: status.modelReady ? 'Local AI ready' : status.serverOnline ? 'Server on' : 'Local AI off',
      className: status.modelReady ? 'ready' : status.serverOnline ? 'warn' : 'off',
      message: status.message || '',
      status
    });
    await refreshAiEstimateLimits();
    return status;
  } catch (e) {
    setAiAssistantStatus({
      state: 'Local AI unavailable',
      className: 'bad',
      message: friendlyAiAssistantError(e.message)
    });
    return null;
  }
}

function setAiAssistantStatus({ state, className, message, status = null }) {
  const pill = el('aiAssistantStatePill');
  const text = el('aiAssistantStatusText');
  const meta = el('aiAssistantStatusMeta');
  if (pill) {
    pill.className = `ai-state-pill ${className || ''}`;
    pill.textContent = state || 'Unknown';
  }
  if (text) text.textContent = message || '';
  if (meta) {
    const loaded = status?.loadedModels?.length ? status.loadedModels.join(', ') : 'None';
    meta.innerHTML = status ? [
      ['Endpoint', status.baseUrl],
      ['Model ID', status.modelIdentifier],
      ['Loaded', loaded],
      ['Context', status.contextLength ? `${Number(status.contextLength).toLocaleString()} tokens` : 'Not set']
    ].map(([label, value]) => `<span><b>${escapeHtml(label)}:</b> ${escapeHtml(value || 'Not set')}</span>`).join('') : '';
  }
}

async function refreshAiEstimateLimits() {
  if (aiAssistantState.limits) return aiAssistantState.limits;
  try {
    const status = await api.aiEstimateStatus();
    aiAssistantState.limits = status.limits || {};
    updateAiAssistantTextCount();
  } catch {
    aiAssistantState.limits = {};
  }
  return aiAssistantState.limits;
}

function addAiAssistantFiles(files) {
  const limits = aiAssistantState.limits || {};
  const maxFiles = Number(limits.maxFiles || 25);
  const existing = aiAssistantState.files;
  aiAssistantState.files = [...existing, ...files]
    .filter((file, index, list) => list.findIndex(candidate =>
      candidate.name === file.name && candidate.size === file.size && candidate.lastModified === file.lastModified) === index)
    .slice(0, maxFiles);
  if (existing.length + files.length > maxFiles) toast(`Only the first ${maxFiles} files were kept.`, 'info');
  renderAiAssistantFiles();
}

function removeAiAssistantFile(index) {
  aiAssistantState.files.splice(index, 1);
  renderAiAssistantFiles();
}

function renderAiAssistantFiles() {
  const list = el('aiAssistantFileList');
  if (!list) return;
  list.innerHTML = aiAssistantState.files.length
    ? aiAssistantState.files.map((file, index) => `
        <span class="ai-file-chip">
          <b>${escapeHtml(file.name)}</b>
          <small>${escapeHtml(formatBytes(file.size))}</small>
          <button type="button" aria-label="Remove ${escapeHtml(file.name)}" data-remove-ai-file="${index}">x</button>
        </span>`).join('')
    : 'No files selected.';
  list.querySelectorAll('[data-remove-ai-file]').forEach(button =>
    button.addEventListener('click', () => removeAiAssistantFile(Number(button.dataset.removeAiFile))));
}

function updateAiAssistantTextCount() {
  const source = el('aiAssistantContext')?.value || '';
  const count = source.length;
  const limits = aiAssistantState.limits || {};
  const maxText = Number(limits.maxCombinedTextCharacters || 500000);
  const maxFiles = Number(limits.maxFiles || 25);
  const maxFileMb = Number(limits.maxFileMegabytes || 20);
  const maxTotalMb = Number(limits.maxTotalUploadMegabytes || 75);
  const countNode = el('aiAssistantTextCount');
  if (countNode) {
    countNode.textContent = `${count.toLocaleString()} characters`;
    countNode.classList.toggle('bad', count > maxText);
  }
  const limitsNode = el('aiAssistantLimits');
  if (limitsNode) limitsNode.textContent = `${maxFiles} files, ${maxFileMb} MB each, ${maxTotalMb} MB total`;
}

async function askAiAssistant(forcedQuestion = '') {
  return runExclusiveToolAction('ai-assistant-chat', 'The assistant is already answering.', async () => {
    const questionNode = el('aiAssistantQuestion');
    const question = (forcedQuestion || questionNode?.value || '').trim();
    if (!question) {
      toast('Ask a question first.', 'info');
      questionNode?.focus();
      return null;
    }
    if (questionNode && !forcedQuestion) questionNode.value = '';
    aiAssistantState.messages.push({ role: 'user', content: question });
    renderAiAssistantChat(true);
    const button = el('btnAiAssistantAsk');
    const missingButton = el('btnAiAssistantMissing');
    setButtonsBusy([button, missingButton], true, 'Thinking...');
    try {
      const form = buildAiAssistantFormData({ question, includeChat: true });
      const answer = await api.aiEstimateChat(form);
      if (answer?.answer) aiAssistantState.messages.push({ role: 'assistant', content: answer.answer });
      if (answer?.warnings?.length) {
        aiAssistantState.messages.push({ role: 'assistant', content: `Warnings:\n- ${answer.warnings.join('\n- ')}` });
      }
      renderAiAssistantChat(true);
      return answer;
    } catch (e) {
      aiAssistantState.messages.push({ role: 'assistant', content: `Chat failed: ${friendlyAiAssistantError(e.message)}` });
      renderAiAssistantChat(true);
      toast(`AI chat failed: ${friendlyAiAssistantError(e.message)}`, 'error', 7000);
      return null;
    } finally {
      setButtonsBusy([button, missingButton], false);
    }
  });
}

async function generateAiAssistantDraft() {
  return runExclusiveToolAction('ai-assistant-draft', 'The assistant is already generating a draft.', async () => {
    const button = el('btnAiAssistantGenerate');
    setButtonsBusy([button], true, 'Generating...');
    try {
      const validation = validateAiAssistantSources();
      if (!validation.valid) throw new Error(validation.message);
      await ensureAiAssistantReady();
      const form = buildAiAssistantFormData({ includeChat: el('aiAssistantUseChat')?.checked });
      const result = await api.aiEstimateDraft(form);
      aiAssistantState.draft = result;
      renderAiAssistantDraftPreview(result);
      await fillAiAssistantDraft(result);
      return result;
    } catch (e) {
      renderAiAssistantDraftPreview({ error: friendlyAiAssistantError(e.message) });
      toast(`AI draft failed: ${friendlyAiAssistantError(e.message)}`, 'error', 8000);
      return null;
    } finally {
      setButtonsBusy([button], false);
    }
  });
}

async function ensureAiAssistantReady() {
  const status = aiAssistantState.status?.modelReady
    ? aiAssistantState.status
    : await refreshAiAssistantStatus(true);
  if (!status?.modelReady) {
    throw new Error(status?.message || 'Local AI is not ready. Select a model in Local AI Power and try again.');
  }
  return status;
}

function validateAiAssistantSources() {
  const limits = aiAssistantState.limits || {};
  const source = buildAiAssistantSourceText({ includeChat: el('aiAssistantUseChat')?.checked });
  const maxText = Number(limits.maxCombinedTextCharacters || 500000);
  const maxFileMb = Number(limits.maxFileMegabytes || 20);
  const maxTotalMb = Number(limits.maxTotalUploadMegabytes || 75);
  const tooLarge = aiAssistantState.files.find(file => file.size > maxFileMb * 1024 * 1024);
  const totalBytes = aiAssistantState.files.reduce((sum, file) => sum + file.size, 0);
  if (source.trim().length < 5 && aiAssistantState.files.length === 0) return { valid: false, message: 'Paste context or add at least one file/image.' };
  if (source.length > maxText) return { valid: false, message: `The context text is over the ${maxText.toLocaleString()} character limit.` };
  if (tooLarge) return { valid: false, message: `${tooLarge.name} is larger than the ${maxFileMb} MB per-file limit.` };
  if (totalBytes > maxTotalMb * 1024 * 1024) return { valid: false, message: `The selected files total more than ${maxTotalMb} MB.` };
  return { valid: true };
}

function buildAiAssistantFormData({ question = '', includeChat = false } = {}) {
  const form = new FormData();
  form.append('sourceText', buildAiAssistantSourceText({ includeChat }));
  form.append('sourceUrls', el('aiAssistantUrls')?.value || '');
  form.append('sourceName', aiAssistantState.files.length ? `Assistant modal (${aiAssistantState.files.length} files)` : 'Assistant modal context');
  form.append('question', question);
  form.append('currentDocumentContext', currentDocumentContextForAi());
  form.append('messagesJson', JSON.stringify(aiAssistantState.messages.slice(-10)));
  aiAssistantState.files.forEach(file => form.append('files', file, file.name));
  return form;
}

function buildAiAssistantSourceText({ includeChat = false } = {}) {
  const parts = [el('aiAssistantContext')?.value || ''];
  if (el('aiAssistantIncludeCurrent')?.checked) {
    parts.push(`CURRENT BUILDER STATE:\n${currentDocumentContextForAi()}`);
  }
  if (includeChat && aiAssistantState.messages.length) {
    parts.push(`LOCAL AI CLARIFICATION CHAT:\n${aiAssistantState.messages.map(message =>
      `${message.role === 'assistant' ? 'Assistant' : 'User'}: ${message.content}`).join('\n\n')}`);
  }
  return parts.filter(part => String(part || '').trim()).join('\n\n');
}

function currentDocumentContextForAi() {
  let formData = {};
  try { formData = getFormData(); } catch { formData = {}; }
  let calcState = {};
  try { calcState = getCalcState(); } catch { calcState = {}; }
  return JSON.stringify({
    activeRecordId,
    activeRecordType,
    activeRecordNumber,
    draftType: textVal('docType') || 'ESTIMATE',
    calculator: calcState,
    document: {
      docType: formData.docType,
      status: formData.status,
      docNumber: formData.docNumber,
      customerName: formData.customerName,
      customerPhone: formData.customerPhone,
      customerEmail: formData.customerEmail,
      customerAddress: formData.customerAddress,
      projectName: formData.projectName,
      material: formData.material,
      color: formData.color,
      infill: formData.infill,
      projectDescription: formData.projectDescription,
      projectNotes: formData.projectNotes,
      lineItems: (formData.lineItems || []).slice(0, 10),
    }
  }, null, 2);
}

function renderAiAssistantChat(scroll = false) {
  const log = el('aiAssistantChatLog');
  if (!log) return;
  log.innerHTML = aiAssistantState.messages.length
    ? aiAssistantState.messages.map(message => `
        <article class="ai-chat-message ${message.role === 'assistant' ? 'assistant' : 'user'}">
          <span>${message.role === 'assistant' ? 'Local AI' : 'You'}</span>
          <p>${escapeHtml(message.content)}</p>
        </article>`).join('')
    : `<div class="empty-state compact"><div class="empty-title">No chat yet.</div><div class="empty-desc">Ask for missing details, quote risks, or what the source packet seems to describe.</div></div>`;
  if (scroll) log.scrollTop = log.scrollHeight;
}

function renderAiAssistantDraftPreview(result) {
  const target = el('aiAssistantDraftPreview');
  if (!target) return;
  if (!result) {
    target.innerHTML = `<div class="empty-state compact"><div class="empty-title">No draft generated yet.</div><div class="empty-desc">When you are ready, generate and fill the builder. Nothing saves automatically.</div></div>`;
    return;
  }
  if (result.error) {
    target.innerHTML = `<div class="callout bad"><strong>Draft failed.</strong><p>${escapeHtml(result.error)}</p></div>`;
    return;
  }
  const prefill = result.prefill || {};
  const pricing = result.pricing || {};
  const fields = [
    ['Customer', prefill.customerName || 'Needs review'],
    ['Project', prefill.projectName || 'Needs review'],
    ['Material', [prefill.material, prefill.color, prefill.infill].filter(Boolean).join(' / ') || 'Needs review'],
    ['Calculator', `${prefill.calcGrams || 0}g, ${prefill.calcHours || 0} print hr, ${prefill.calcDesignHours || 0} design hr`],
    ['Total', money(pricing.total || 0)]
  ];
  target.innerHTML = `
    <div class="ai-draft-mini">
      <div class="ai-draft-mini-head">
        <strong>${escapeHtml(result.usedAi ? 'AI model draft filled' : 'Local rules draft filled')}</strong>
        <span class="badge ${result.usedAi ? 'badge-green' : 'badge-orange'}">${escapeHtml(result.provider || 'Structured draft')}</span>
      </div>
      <div class="ai-draft-mini-grid">${fields.map(([label, value]) => `<div><span>${escapeHtml(label)}</span><b>${escapeHtml(value)}</b></div>`).join('')}</div>
      ${(result.questions || []).length ? `<h4>Questions</h4><ul>${result.questions.map(x => `<li>${escapeHtml(x)}</li>`).join('')}</ul>` : ''}
      ${(result.warnings || []).length ? `<h4>Warnings</h4><ul>${result.warnings.map(x => `<li>${escapeHtml(x)}</li>`).join('')}</ul>` : ''}
    </div>`;
}

async function fillAiAssistantDraft(result) {
  const prefill = result?.prefill;
  if (!prefill) throw new Error('The server did not return mapped estimate fields.');
  const requestedType = el('aiAssistantDocType')?.value || textVal('docType') || prefill.docType || 'ESTIMATE';
  const docType = requestedType === 'INVOICE' ? 'INVOICE' : 'ESTIMATE';
  prefill.docType = docType;
  prefill.status = prefill.status || 'Draft';
  prefill.docStatus = prefill.docStatus || prefill.status;
  const nextNumber = !prefill.docNumber
    ? await api.nextNumber(docType).then(r => r.number).catch(() => '')
    : '';
  startCleanDocument(docType, prefill.docNumber || nextNumber);
  clearActiveRecordIdentity();
  applyDocumentPrefill(prefill);
  updateActiveBar();
  refreshInvoicePreview();
  setDbStatus(`Ready - AI-filled ${docType === 'INVOICE' ? 'invoice' : 'estimate'} draft ${textVal('docNumber') || ''}`.trim(), 'ready');
  showView('builder');
  closeAiAssistantModal();
  const warningText = (result.warnings || []).length ? ' Review warnings and questions before saving.' : '';
  toast(`AI filled an unsaved ${docType === 'INVOICE' ? 'invoice' : 'estimate'} draft.${warningText}`, 'success', 8000);
}

function setButtonsBusy(buttons, busy, busyText = 'Working...') {
  buttons.filter(Boolean).forEach(button => {
    if (busy) {
      button.dataset.originalText = button.textContent;
      button.textContent = busyText;
      button.disabled = true;
    } else {
      button.textContent = button.dataset.originalText || button.textContent;
      button.disabled = false;
      delete button.dataset.originalText;
    }
  });
}

function formatBytes(bytes) {
  const value = Number(bytes || 0);
  if (value >= 1024 * 1024) return `${(value / 1024 / 1024).toFixed(1)} MB`;
  if (value >= 1024) return `${(value / 1024).toFixed(1)} KB`;
  return `${value} B`;
}

function friendlyAiAssistantError(message) {
  const raw = String(message || '');
  const json = raw.match(/\{.*\}$/s)?.[0];
  if (json) {
    try {
      const parsed = JSON.parse(json);
      if (parsed.message) return parsed.message;
    } catch { }
  }
  return raw.replace(/^\d+\s+—\s+/, '').trim() || 'Unknown error';
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
