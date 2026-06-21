(function (global) {
  function extractErrorMessage(error) {
    const raw = String(error?.message || error || '').trim();
    if (!raw) return 'Unknown save error.';

    try {
      const parsed = JSON.parse(raw);
      if (parsed?.message) return String(parsed.message);
      if (parsed?.title) return String(parsed.title);
    } catch {
    }

    return raw;
  }

  function modalSaveOutcome(result = {}) {
    if (result.ok || result.success) {
      return {
        closeModal: true,
        refreshPage: true,
        toastMessage: result.successMessage || 'Saved.',
        toastType: 'success',
      };
    }

    return {
      closeModal: false,
      refreshPage: false,
      toastMessage: `Save failed: ${extractErrorMessage(result.error)}`,
      toastType: 'error',
    };
  }

  function createModalSaveGuard() {
    let inFlight = false;
    return {
      tryStart() {
        if (inFlight) return false;
        inFlight = true;
        return true;
      },
      finish() {
        inFlight = false;
      },
      reset() {
        inFlight = false;
      },
      isInFlight() {
        return inFlight;
      },
    };
  }

  global.EpataModalSaveState = {
    createModalSaveGuard,
    extractErrorMessage,
    modalSaveOutcome,
  };
})(globalThis);
