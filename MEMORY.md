# Project Memory

## Git Workflow
- Never treat unexpected deleted launcher or helper files as harmless background noise. Call them out immediately and restore them when the user needs them, without touching unrelated worktree changes.

## Verification
- Treat every interactive control as a state transition with an explicit async owner and persistence contract. Test isolated actions plus realistic A → B → Back/revisit/reload sequences, rapid repeat clicks, stale responses, and failures; verify that each piece of state persists or resets intentionally.
- During a stateful interaction audit, repair failures at the current browser/database checkpoint and continue forward from there. Do not recreate state zero after each fix; reserve the clean baseline-to-finish run for final verification.
- When Document Intake has a deterministic destination such as Invoice or Estimate, success means a populated destination record was created or linked and reopened successfully; an Audit Doc plus an ephemeral navigation prefill is not completion. Preserve the source document's own identity and regression-test a sanitized copy of the failing layout.
- Never report Windows launcher or pin setup complete from shortcut-file creation alone. Verify the visible Desktop, Start menu, and taskbar entries, then launch the actual shortcut and confirm the server becomes healthy, the browser opens, and the intended database is active.
- For custom controls built from native buttons, explicitly reset inherited global button layout and radius styles, then visually verify the open state, alignment, scrolling, and responsive overflow instead of checking text and selection behavior alone.
- Preserve actionable workflow results across in-app navigation and Back. Verify the complete result → related record → Back path, not only the initial render.
- When a UI reports a fetch/action failure, verify the exact user action endpoint from the browser path. A green status/readiness endpoint is not proof that the chat, draft, save, or upload action works.
- For Local AI UX bugs, verify perceived latency from the browser workflow. "Model ready" is not enough; the actual Ask/Generate action must return promptly or show a bounded fallback.
- For AI intake from pasted email, strip mailbox/browser UI chrome and business sender identity before draft generation. Customer-facing fields must describe the job, not Gmail labels, notification prompts, or EPATA sender addresses.
