// ═══════════════════════════════════════════════════════
//  EPATA Invoice Tool — Active Document Session
// ═══════════════════════════════════════════════════════

export function emptyActiveRecordIdentity() {
  return {
    activeRecordId: null,
    activeRecordType: null,
    activeRecordNumber: null,
    activeRecordUpdatedAt: null,
  };
}

export function normalizeActiveRecordIdentity(identity = {}) {
  return {
    activeRecordId: identity.activeRecordId || null,
    activeRecordType: identity.activeRecordType || null,
    activeRecordNumber: identity.activeRecordNumber || null,
    activeRecordUpdatedAt: identity.activeRecordUpdatedAt || null,
  };
}

export function identityFromDocument(doc = null) {
  if (!doc) return emptyActiveRecordIdentity();
  return normalizeActiveRecordIdentity({
    activeRecordId: doc.id || null,
    activeRecordType: doc.docType || null,
    activeRecordNumber: doc.docNumber || null,
    activeRecordUpdatedAt: doc.updatedAt || null,
  });
}

export function identityAfterArchivedRecord(identity = {}, archivedId = null) {
  const current = normalizeActiveRecordIdentity(identity);
  if (current.activeRecordId && Number(current.activeRecordId) === Number(archivedId)) {
    return emptyActiveRecordIdentity();
  }
  return current;
}

export function activeRecordBarText(identity = {}, visibleDocNumber = '') {
  const current = normalizeActiveRecordIdentity(identity);
  if (!current.activeRecordId) return 'New document — Ctrl+S to save';
  return `Editing ${visibleDocNumber || current.activeRecordNumber || `#${current.activeRecordId}`} — Ctrl+S to save`;
}

export function buildSaveFailureUiState({ identity = {}, activeBarText = '' } = {}) {
  return {
    identity: normalizeActiveRecordIdentity(identity),
    activeBarText: String(activeBarText || ''),
    autoSaveStatus: '',
    dbStatusText: 'Save failed',
    dbStatusState: 'error',
    shouldUpdateActiveBar: false,
    shouldShowSavedState: false,
  };
}
