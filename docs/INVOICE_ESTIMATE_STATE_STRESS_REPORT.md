# Invoice / Estimate State Stress Report

Date: 2026-06-21
Round: 114
Database safety: all browser and smoke/acceptance checks used disposable databases: `Data\playwright-browser-tests.db`, `Data\local-smoke.db`, and `Data\full-acceptance.db`. The production ledger database was not used.

## Summary

Stress coverage added in `tests/playwright/invoice-state-stress.spec.cjs`:

- Save and reopen an estimate and an invoice with every builder field populated.
- Switch repeatedly between saved estimate and invoice records.
- Save a loaded estimate after changing Type to Invoice and Status to Paid.
- Convert an estimate to an invoice from Records, then reopen both old and new records.
- Delay one save, open another record while it is still pending, then save the second record.
- Inject a backend 500 on save and verify the active record, unsaved fields, and error toast stay visible.

## Problems Found And Fixed

| Area / field | Strict condition tested | Problem noticed | Fix status |
|---|---|---|---|
| Records Open identity | Builder document IDs collided with AR ledger row IDs in the mixed Records list. | Opening a document row with `id = 1` could find an AR row with `id = 1` first and return without loading the document, leaving the previous invoice visible. | Fixed: document actions now use document-only lookup and delegated `data-record-action` buttons. |
| Active record memory | A save was delayed, then another record was opened before the first save completed. | Slow save completion could overwrite the active bar/current identity after the user had moved to another record. | Fixed: document session version guards stop stale save completions from hijacking the visible record. |
| Save settled state | Immediately opened Records after a save. | UI could report `Ready - saved` before Records refresh finished, allowing clicks against rows while save cleanup was still settling. | Fixed: saved/ready state is set after Records refresh completes. |
| `docStatus` | Reopen estimate after viewing invoice, including converted estimates with `Accepted`. | Browser dropped `Accepted` because status was assigned while invoice options were still loaded, then remapped to `Draft`. | Fixed: restore now sets document type, rebuilds valid status options, then assigns/remaps status. |
| PDF/import `docStatus` | AI/PDF draft maps an estimate/invoice status into the builder. | Same ordering risk as record restore. | Fixed: import prefill now applies document type before status. |
| Due date label | Reopen invoice after estimate state. | Label could stay as `Valid Until` on an invoice. | Fixed: restore now updates the date label from the loaded document type. |
| `docTaxRate` | Save estimate with document tax rate, reopen it. | Tax field reopened as `0` because save payload used calculator tax instead of builder tax first. | Fixed: save now persists builder `docTaxRate` into `calcTaxRate`. |
| Line items | Change from a 2-line estimate form to a 1-line invoice form. | Extra visible row persists unless removed, which is expected UI behavior but needed exact testing. | Covered: stress setup now exercises the real remove-row button to prove no hidden line item persists after removal. |
| Backend 500 | Force document `PUT` to return 500. | Needed proof that failed save does not change identity or wipe edits. | Fixed/verified: active record and unsaved project field stay visible; persisted record remains unchanged; toast shows failure details. |

## Per-Field Result Matrix

| Field group | Result |
|---|---|
| Type / status / number | Estimate and invoice type/status/number round-trip correctly after restore-order and ID-collision fixes. Type-change save creates a new invoice and leaves the estimate intact. |
| Dates / page size | `docDate`, `dueDate`, and `pageSize` round-trip. Invoice/estimate date label now follows the loaded type. |
| Customer fields | `customerName`, `preparedFor`, phone formatting, lowercased email, and address round-trip. |
| Project fields | `projectName`, `material`, `color`, `infill`, public description, and private notes round-trip and do not jump records. |
| Line items | Description, details, quantity, rate, count, and order round-trip after add/remove operations. |
| Money fields | Discount, rush percent, tax rate, amount paid, total, and balance round-trip; paid invoices auto-balance to zero. |
| Payment method | Required payment method round-trips and paid invoice Sales sync keeps the selected method. |
| PDF notes | Pricing guide, terms, standard turnaround, and rush turnaround round-trip between estimate/invoice records. |
| Records conversion | Records Create Invoice opens the new invoice; the source estimate remains openable and becomes `Accepted` with conversion notes. |
| Failure/race state | Delayed saves and backend failures no longer swap visible IDs or wipe unsaved fields. |

## Verification Run

- `node --check wwwroot\invoice-builder\js\app.js`
- `node --check wwwroot\invoice-builder\js\builder.js`
- `node --check wwwroot\invoice-builder\js\records.js`
- `node --check tests\playwright\invoice-state-stress.spec.cjs`
- `node tools\invoice-builder-behavior.mjs`
- `node tools\shell-navigation-file-picker-behavior.mjs`
- `dotnet build EPATA.BusinessLedger.csproj -c Release --no-restore /p:UseAppHost=false`
- `dotnet build EPATA.BusinessLedger.csproj -c Release -o obj\verify-build --no-restore /p:UseAppHost=false`
- `pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\run-playwright.ps1 tests/playwright/invoice-state-stress.spec.cjs --project=chromium-desktop --reporter=line --timeout=120000`
- `npm run test:browser -- --project=chromium-desktop --reporter=line --timeout=120000`
- `npm run test:visual -- --project=chromium-desktop --reporter=line --timeout=120000`
- `pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\local-smoke.ps1`
- `pwsh -NoProfile -ExecutionPolicy Bypass -File .\tools\full-acceptance.ps1`
