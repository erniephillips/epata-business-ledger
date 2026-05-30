// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Main Application
//  .NET 10 SPA Entry Point
// ═══════════════════════════════════════════════════════

import { api }                                    from './api.js';
import { el, toast, money, setVal, textVal,
         fmtDateTime, statusBadge, typeBadge,
         debounce, todayStr }                     from './utils.js';
import { initCalculator, calculate, getCalcState,
         restoreCalcState, pushToBuilder,
         applyConfigDefaults }                    from './calculator.js';
import { initBuilder, addLineItem, removeLineItem,
         getLineItems, updateTotals, captureState,
         restoreState, getFormData, newDocument } from './builder.js';
import { initRecords, refreshRecords, loadRecord,
         duplicateRecord, deleteRecord, exportCsv,
         getRecords }                             from './records.js';
import { generatePdf, renderInvoiceHtml }         from './pdf.js';

// ── State ─────────────────────────────────────────────
let activeRecordId  = null;
let apiReady        = false;
let appConfig       = {};
let autoSaveTimer   = null;
const AUTOSAVE_MS   = 30_000;

// ── Init ──────────────────────────────────────────────
export async function init(initialView = 'dashboard') {
  // Expose global handlers for inline onclick attributes
  window._builderUpdate  = () => { updateTotals(); refreshInvoicePreview(); scheduleAutoSave(); };
  window._removeLineItem = removeLineItem;
  window._loadRecord     = (id) => onLoadRecord(id);
  window._dupeRecord     = (id) => onDuplicateRecord(id);
  window._delRecord      = (id) => onDeleteRecord(id);
  window._copyText       = copyText;

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

    // Load latest or start fresh
    try {
      const latest = await api.latest();
      onDocumentLoaded(latest);
      activeRecordId = latest.id;
      setDbStatus(`Ready — loaded #${latest.id}`, 'ready');
    } catch {
      const num = await api.nextNumber('ESTIMATE').then(r => r.number).catch(() => '');
      newDocument('ESTIMATE', num);
      setDbStatus('Ready — new document', 'ready');
    }

    // Refresh dashboard stats
    await loadDashboardStats().catch(err => console.error('Dashboard stats error', err));
  }

  refreshInvoicePreview();
  showView(initialView);
}

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
          <td class="doc-number" style="cursor:pointer" onclick="window._loadRecord(${r.id})">${r.docNumber||'—'}</td>
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

  activeRecordId = doc.id;
  updateActiveBar();
  refreshInvoicePreview();
}

async function startNew(type = 'ESTIMATE') {
  const num = apiReady ? await api.nextNumber(type).then(r => r.number).catch(() => '') : '';
  newDocument(type, num);
  activeRecordId = null;
  updateActiveBar();
  refreshInvoicePreview();
  showView('builder');
}

async function saveRecord(forceNew = false) {
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
      toast(`Saved #${activeRecordId}`, 'success');
    } else {
      result = await api.create(body);
      activeRecordId = result.id;
      toast(`Created #${activeRecordId}`, 'success');
    }

    updateActiveBar();
    await refreshRecords();
    refreshInvoicePreview();
    setAutoSaveStatus('saved');
    apiReady = true;
    setDbStatus(`Ready — saved #${activeRecordId}`, 'ready');
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
}

// ── Auto-save ─────────────────────────────────────────
function scheduleAutoSave() {
  if (!activeRecordId || !apiReady) return;
  clearTimeout(autoSaveTimer);
  setAutoSaveStatus('saving');
  autoSaveTimer = setTimeout(async () => {
    try { await saveRecord(false); }
    catch { setAutoSaveStatus(''); }
  }, AUTOSAVE_MS);
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
    // Save first. If the database is broken, do not generate a PDF that never got tracked.
    if (apiReady) await saveRecord(false);

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
  const calc = calculate();
  pushToBuilder(calc, getLineItems, addLineItem);
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

async function onDeleteRecord(id) {
  try { await deleteRecord(id, activeRecordId, (newId) => { activeRecordId = newId; updateActiveBar(); }); }
  catch (e) { toast('Delete failed: ' + e.message, 'error'); }
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
}

async function saveSettings() {
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
    txt.textContent = `Editing #${activeRecordId} — Ctrl+S to save`;
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
