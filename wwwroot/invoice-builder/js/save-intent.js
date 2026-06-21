export function getSaveIntent(forceNew, context = {}) {
  const activeRecordId = normalizeId(context.activeRecordId);
  const activeRecordType = normalizeType(context.activeRecordType);
  const requestedDocType = normalizeType(context.requestedDocType || context.docType || activeRecordType || 'ESTIMATE');
  const typeChanged = !forceNew && !!activeRecordId && !!activeRecordType && requestedDocType !== activeRecordType;
  const mode = forceNew
    ? 'create-new'
    : typeChanged
      ? 'type-change-create'
      : activeRecordId
        ? 'update-existing'
        : 'create';

  return {
    mode,
    forceNew: !!forceNew,
    typeChanged,
    activeRecordId,
    activeRecordType,
    requestedDocType,
  };
}

export function saveIntentKey(intent) {
  if (!intent) return '';
  return [
    intent.mode || '',
    intent.activeRecordId || '',
    intent.activeRecordType || '',
    intent.requestedDocType || '',
  ].join('|');
}

export function canReuseInFlightSave(inFlightIntent, requestedIntent) {
  return !!inFlightIntent && saveIntentKey(inFlightIntent) === saveIntentKey(requestedIntent);
}

export function appendPrivateNote(existing, note) {
  const current = String(existing || '').trim();
  return current && !current.toLowerCase().includes(String(note || '').toLowerCase())
    ? `${current}\n${note}`
    : current || note;
}

export function buildSaveRequestPlan(forceNew, context = {}) {
  const body = { ...(context.body || {}) };
  const intent = getSaveIntent(forceNew, {
    activeRecordId: context.activeRecordId,
    activeRecordType: context.activeRecordType,
    requestedDocType: context.requestedDocType || body.docType,
  });

  if (intent.forceNew || intent.typeChanged) {
    if (context.nextNumber) body.docNumber = context.nextNumber;
    if (intent.typeChanged) {
      const fromLabel = `${intent.activeRecordType} ${String(context.activeRecordNumber || '').trim() || `#${intent.activeRecordId}`}`;
      body.projectNotes = appendPrivateNote(
        body.projectNotes,
        `Created as a new ${body.docType} from ${fromLabel}. The original saved record was not overwritten.`,
      );
    }
  }

  const action = !intent.forceNew && intent.activeRecordId && !intent.typeChanged ? 'update' : 'create';
  return {
    action,
    body,
    intent,
    originalUnchanged: intent.typeChanged,
    updateId: action === 'update' ? intent.activeRecordId : null,
  };
}

function normalizeId(value) {
  const number = Number(value || 0);
  return Number.isFinite(number) && number > 0 ? number : null;
}

function normalizeType(value) {
  const text = String(value || '').trim().toUpperCase();
  return text === 'INVOICE' ? 'INVOICE' : text === 'ESTIMATE' ? 'ESTIMATE' : '';
}
