// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Utilities
// ═══════════════════════════════════════════════════════

// ── DOM helpers ───────────────────────────────────────
export const el = (id) => document.getElementById(id);

export function val(id) {
  const n = parseFloat(el(id)?.value);
  return isNaN(n) ? 0 : n;
}

export function textVal(id) {
  return el(id)?.value?.trim() ?? '';
}

export function setVal(id, v) {
  const e = el(id);
  if (e) e.value = v ?? '';
}

// ── Formatting ────────────────────────────────────────
export function money(n) {
  const num = typeof n === 'string' ? parseFloat(n) : n;
  return isNaN(num) ? '$0.00' : '$' + num.toFixed(2).replace(/\B(?=(\d{3})+(?!\d))/g, ',');
}

export function plainMoney(n) {
  const num = typeof n === 'string' ? parseFloat(n) : n;
  return isNaN(num) ? '0.00' : num.toFixed(2);
}

export function escapeHtml(v) {
  return String(v ?? '')
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#039;');
}

// ── Dates ─────────────────────────────────────────────
export function todayStr(offsetDays = 0) {
  const d = new Date();
  d.setDate(d.getDate() + Number(offsetDays || 0));
  return d.toISOString().slice(0, 10);
}

export function fmtDate(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return isNaN(d.getTime()) ? iso : d.toLocaleDateString('en-US', { month: 'short', day: 'numeric', year: 'numeric' });
}

export function fmtDateTime(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  return isNaN(d.getTime()) ? iso : d.toLocaleString('en-US', { month: 'short', day: 'numeric', year: 'numeric', hour: 'numeric', minute: '2-digit' });
}

// ── Toast notifications ───────────────────────────────
export function toast(msg, type = 'info', duration = 3000) {
  const container = el('toast-container');
  if (!container) return;
  const t = document.createElement('div');
  t.className = `toast ${type}`;
  const icon = type === 'success' ? '✓' : type === 'error' ? '✕' : 'ℹ';
  t.innerHTML = `<span>${icon}</span><span>${escapeHtml(msg)}</span>`;
  container.appendChild(t);
  setTimeout(() => { t.style.opacity = '0'; t.style.transition = 'opacity 0.3s'; setTimeout(() => t.remove(), 300); }, duration);
}

// ── Status badge HTML ─────────────────────────────────
export function statusBadge(status) {
  const map = {
    Draft:    'status-draft',
    Sent:     'status-sent',
    Paid:     'status-paid',
    Accepted: 'status-paid',   // shares the green "closed" styling
    Void:     'status-void',
  };
  return `<span class="badge ${map[status] ?? 'badge-gray'}">${escapeHtml(status ?? 'Draft')}</span>`;
}

export function typeBadge(type) {
  return type === 'INVOICE'
    ? `<span class="badge badge-purple">${type}</span>`
    : `<span class="badge badge-blue">${type ?? 'ESTIMATE'}</span>`;
}

// ── Logo (base64 embedded for PDF use) ───────────────
// Populated from /img/logo.png at runtime
let _logoDataUri = null;

export async function getLogoDataUri() {
  if (_logoDataUri) return _logoDataUri;
  try {
    const res = await fetch('/img/logo.png');
    const blob = await res.blob();
    _logoDataUri = await new Promise((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result);
      reader.onerror = reject;
      reader.readAsDataURL(blob);
    });
  } catch {
    _logoDataUri = '';
  }
  return _logoDataUri;
}

// ── Keyboard shortcuts ────────────────────────────────
export function registerShortcut(key, mods, fn) {
  document.addEventListener('keydown', (e) => {
    const ctrl  = mods.includes('ctrl')  ? (e.ctrlKey  || e.metaKey) : true;
    const shift = mods.includes('shift') ? e.shiftKey : true;
    if (e.key.toLowerCase() === key.toLowerCase() && ctrl && shift) {
      e.preventDefault();
      fn();
    }
  });
}

// ── Debounce ──────────────────────────────────────────
export function debounce(fn, ms) {
  let timer;
  return (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), ms); };
}
