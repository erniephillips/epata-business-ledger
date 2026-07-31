# Project Memory

## Git Workflow
- Never treat unexpected deleted launcher or helper files as harmless background noise. Call them out immediately and restore them when the user needs them, without touching unrelated worktree changes.

## Verification
- When a UI reports a fetch/action failure, verify the exact user action endpoint from the browser path. A green status/readiness endpoint is not proof that the chat, draft, save, or upload action works.
- For Local AI UX bugs, verify perceived latency from the browser workflow. "Model ready" is not enough; the actual Ask/Generate action must return promptly or show a bounded fallback.
- For AI intake from pasted email, strip mailbox/browser UI chrome and business sender identity before draft generation. Customer-facing fields must describe the job, not Gmail labels, notification prompts, or EPATA sender addresses.
