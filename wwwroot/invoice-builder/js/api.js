// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — API Client
// ═══════════════════════════════════════════════════════

const BASE = '';  // Same-origin; no CORS needed

async function request(url, opts = {}) {
  const res = await fetch(BASE + url, opts);
  const contentType = res.headers.get('content-type') || '';
  if (!res.ok) {
    const txt = await res.text().catch(() => res.statusText);
    throw new Error(`${res.status} — ${txt}`);
  }
  if (res.status === 204) return null;
  if (!contentType.includes('application/json')) {
    throw new Error('The invoice API did not answer. Close any old EPATA.BusinessLedger process and reopen the rebuilt app.');
  }
  return res.json();
}

// ── Documents ─────────────────────────────────────────
export const api = {
  health:      ()              => request('/api/health'),
  stats:       ()              => request('/api/documents/stats'),
  list:        (params = {})   => request('/api/documents?' + new URLSearchParams(params)),
  latest:      ()              => request('/api/documents/latest'),
  get:         (id)            => request(`/api/documents/${id}`),
  nextNumber:  (type)          => request(`/api/documents/next-number?type=${type}`),
  create:      (body)          => request('/api/documents',      { method: 'POST', ...json(body) }),
  update:      (id, body)      => request(`/api/documents/${id}`, { method: 'PUT',  ...json(body) }),
  delete:      (id)            => request(`/api/documents/${id}`, { method: 'DELETE' }),
  restore:     (id)            => request(`/api/documents/${id}/restore`, { method: 'POST' }),
  duplicate:   (id)            => request(`/api/documents/${id}/duplicate`, { method: 'POST' }),
  convertToInvoice: (id)       => request(`/api/documents/${id}/convert-to-invoice`, { method: 'POST' }),

  // Settings
  getConfig:   ()              => request('/api/config'),
  saveConfig:  (body)          => request('/api/config', { method: 'PUT', ...json(body) }),

  // Database
  backupUrl:   ()              => '/api/database/backup',
  clearDb:     ()              => request('/api/database/clear', { method: 'POST' }),
  importDb:    (file)          => {
    const fd = new FormData();
    fd.append('file', file);
    return request('/api/database/import', { method: 'POST', body: fd });
  },
};

function json(body) {
  return {
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  };
}
