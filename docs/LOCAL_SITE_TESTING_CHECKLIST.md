# EPATA Local Site Testing Checklist

This checklist is for the EPATA Accounts Tracker local-only site. It assumes the app runs on the owner's machine, uses local SQLite data, and does not need multi-user, public hosting, login, or internet-hardening tests. The highest-risk areas are document identity, invoice/estimate money math, ledger sync side effects, file import/export, and anything that can touch the live database.

## Progress

- Last updated: 2026-06-21, Round 115.
- Done: 446.
- Open: 0.
- Round 1 evidence: Release build to `obj\verify-build`, PowerShell 7 full acceptance suite, and `tools/local-smoke.ps1` against disposable SQLite databases.
- Round 2 evidence: repeated build and acceptance, acceptance DB cleanup, extended `tools/local-smoke.ps1` for restart persistence, duplicate import warning, blank/scanned import rejection, wrong-format import no-write, and imported paid invoice Sales/AR sync.
- Round 3 evidence: fixed generic CRUD `PUT` key handling, hardened CSV exports against formula-like text, expanded `tools/local-smoke.ps1` across 17 ledger CRUD routes, CSV exports, lookups, job timeline, app info, backup download, file boundary rejection, tax/review reads, and backup artifact cleanup.
- Round 4 evidence: in-app Browser bootstrap attempted but blocked by Windows sandbox, added disposable browser-test server helper, expanded invoice workflow smoke coverage for next-number gaps, save/update, unsafe type-change failure, convert-to-invoice, paid sync idempotency, invoice duplicate, archive/restore, void handling, and linked Sales/AR restore/hide behavior.
- Round 5 evidence: reproduced and fixed invoice renumbering orphan behavior in disposable smoke coverage, added repeated estimate conversion coverage, verified generated Sales/AR rows repoint from old invoice numbers, and reran smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 6 evidence: reproduced and fixed estimate sync overwriting a manual Customer Job with the same reference number, added accepted-estimate Customer Job behavior coverage, added estimate renumber Customer Job repoint coverage, verified estimates create no Sales/AR side effects, and reran smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 7 evidence: added explicit record-source metadata for builder document rows, fixed the smoke harness to enumerate REST array results correctly, verified Records list estimate/invoice/AR-only distinction, search by number/customer/project, type and status filters, Show Archived behavior, AR-only routing metadata, API alias agreement, and reran smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 8 evidence: extracted Records View sort/filter/page/footer/CSV behavior into testable helpers, added `tools/records-view-behavior.mjs`, verified sort by updated/created/number/total/paid/customer/project, page sizes 10/25/50/100, filtered/sorted CSV order with formula-guarded cells, footer totals for active invoice rows only, added Partial/Paid/Project UI options, and reran smoke, full acceptance, clean Release build, Node behavior test, and artifact cleanup checks.
- Round 9 evidence: extracted invoice-builder status/terms behavior and save-intent behavior into testable helpers, added `tools/invoice-builder-behavior.mjs`, added a conflicting in-flight save guard, verified Save as New leaves the original unchanged, verified opening A then B and saving B does not write A, verified estimate-to-paid-invoice creates a new invoice with Sales/AR only on the invoice, verified type-change toast text says original unchanged, and reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 10 evidence: added contact validation helper coverage for US phone formatting/completeness and optional email validation, verified customer/prepared-for/phone/email/address persistence, verified ordered multi-line line items reload in sort order, verified zero-quantity line item amount stays zero and is excluded from subtotal, and reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 11 evidence: extracted calculator pricing and calculator-to-builder transfer planning into pure helpers, verified calculator transfer of grams, hours, design hours, setup, post-processing, rates, difficulty, tax, rush, discount, product line details, and minimum-price adjustment line items, and reran behavior tests, invoice app module import, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 12 evidence: extracted active document session behavior into pure helpers, verified archiving the active record clears active document identity, verified failed saves preserve the active-bar text and never show saved state, wired the invoice app to that helper, and reran behavior tests, invoice app module import, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 13 evidence: extracted required-field and numeric normalization behavior into pure validation helpers, verified blank document date and customer name fail safely while blank document number remains allowed for server-generated numbering, verified malformed document number/phone/email fail safely, verified negative and impossible money/rate/tax/rush/line-item numbers clamp or clear according to local rules, wired the invoice app to the updated validation module, and reran behavior tests, invoice app module import, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 14 evidence: added targeted accounting smoke rows and exact before/after dashboard and tax-summary delta assertions for seller-collected tax, marketplace-collected tax, refunds, platform fees, shipping label cost, estimated COGS, operating expenses, COGS/material expenses, excluded non-deductible/memo-only/asset/unchecked expenses, non-deductible assets, and unpaid/partial/paid/void AR normalization; fixed void AR balance due to report zero; and reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 15 evidence: added multipart upload smoke coverage with a traversal-style filename, verified the saved file stays under `UploadedDocs`, verified the sanitized file can be read back through the audit-document file endpoint, cleaned up newly-created upload files only, and reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 16 evidence: made invoice PDF output honor the saved brand color, extracted testable calculator defaults for new documents, verified saved business/contact/social/brand/calculator settings through `/api/config`, verified settings persist after restart, verified configured defaults and PDF settings output in the behavior harness, bumped invoice-builder cache versions, and reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 17 evidence: extracted customer/vendor relationship directory aggregation into a reusable browser helper, verified customer rows include saved Party contacts and name-only activity from Sales, AR, PDF docs, Jobs, and Communications, verified vendor rows include saved Party contacts and name-only activity from Expenses, AP Bills, and Assets, verified paid/void AR/AP do not inflate open balances, wired the main shell to the helper, and reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 18 evidence: expanded invoice PDF renderer behavior coverage for customer-facing invoice and estimate output, verified doc type labels, document numbers, dates, prepared-for/customer details, project details, line items, rates, totals, public terms, turnaround text, estimate valid-until/approval wording, invoice due-date/payment-status wording, and verified `projectNotes`, internal notes, and import receipt text do not leak into customer PDFs; reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 19 evidence: tightened invoice/estimate document-number identity so archived records keep their numbers reserved, updated duplicate-number errors to explicitly document the archived-number rule, made restore return a clean 400 instead of a server error if a duplicate ever blocks restore, added disposable smoke coverage that archives a document, proves number reuse is rejected with the archived-record explanation, restores the original, and reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 20 evidence: completed the PDF import test matrix by adding oversized PDF upload coverage to the disposable smoke suite; verified invoice import, estimate import, duplicate-number warning and blocked save, scanned/blank OCR-needed message, wrong-format warning/no-write path, and per-file oversized PDF rejection; reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 21 evidence: added explicit disposable dashboard exclusion coverage for archived Sales, archived Expenses, archived AR rows, Void AR rows, and non-counted Expenses; verified they do not move dashboard KPIs or appear in gross receipts, known-cost, or open-invoice breakdowns; reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 22 evidence: added explicit Sales CSV archived-row coverage using a disposable archived row; verified default export excludes archived rows, `includeArchived=true` includes them, the expected Sales CSV header is present, and the archived row parses back through `ConvertFrom-Csv`; reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 23 planning evidence: expanded the checklist with the heavier PrintScore-style hardening passes so browser automation, visual QA, accessibility, launch/publish drills, stress data, backup/restore rehearsals, failure injection, local-safety scans, and release rehearsal work are tracked explicitly before this app is trusted with real records.
- Round 24 evidence: attempted the in-app browser route crawl but Windows sandboxing blocked the browser automation process, so the browser crawl remains open; completed the P0 no-real-database guard instead by documenting every automated test launcher database path in `docs/TEST_DATABASE_GUARD.md`, adding smoke assertions that the scripts use only disposable/test databases and disable auto-open where applicable, and rerunning behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 25 evidence: hardened and tested customer-facing HTML escaping by adding malicious user-text coverage to shared badge helpers and the invoice PDF renderer, fixed `typeBadge` to escape fallback labels, verified raw script/image/javascript-link markup is not emitted while escaped text is preserved, and reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 26 evidence: added local smoke secret-exposure coverage for static UI shells/scripts, invoice-builder shells/scripts, `/api/config`, `/api/app-info`, representative CSV exports, and an unavailable `/api/export/settings` route; seeded a fake API-key-shaped sentinel only into the disposable smoke settings table and verified it is not exposed through UI/config/app-info/export surfaces; reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 27 evidence: added required Payment Method tracking across receivables, bills, expenses, assets, tax obligations, customer jobs, Sales, and invoice documents; verified Etsy defaults to `Etsy Payments`, blank values normalize safely, invoice payment methods sync to Sales/AR, standalone invoice-builder payment method validation is required, and reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 28 evidence: replaced the main shell single-message toast with a stacked toast container, added `tools/toast-stack-behavior.mjs` to prove multiple save/duplicate/backend errors remain visible and user text is escaped, wired smoke and full acceptance static checks for the toast helper, and reran behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 29 evidence: added `tools/main-shell-toast-actions-behavior.mjs` to verify visible toast feedback exists for save, archive/delete, restore, upload/import, failure, and copy actions across the main shell and invoice-builder records modules; wired it into local smoke and added full-acceptance static checks before rerunning behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 30 evidence: added the shared `EpataModalLifecycle` helper for main shell and dashboard breakdown modals, verified modal open removes hidden state, focuses the first data-entry control, closes by wired close/backdrop/Escape handlers, and restores focus to the opener; wired `tools/modal-lifecycle-behavior.mjs` into local smoke and added full-acceptance static checks before rerunning behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 31 evidence: added the shared `EpataModalSaveState` helper so generic modal saves have an explicit success-versus-failure outcome, verified success closes and refreshes while failed/invalid saves keep the modal open, do not refresh, parse backend validation messages, and show a visible error toast; wired `tools/modal-save-state-behavior.mjs` into local smoke and added full-acceptance static checks before rerunning behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 32 evidence: added the shared `EpataEntityTableState` helper for generic ledger search/filter/page behavior, verified 1,200-row text/proof search, needs-review/open/paid filters, page clamping, sorted page windows, preserved row IDs for Edit/Archive actions after filtering, and stable global-search debounce wiring; wired `tools/entity-table-state-behavior.mjs` into local smoke and added full-acceptance static checks before rerunning behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 33 evidence: added the shared `EpataSidebarState` helper for sidebar collapse persistence, verified saved/default collapsed state, localStorage writes, desktop/mobile expanded-state logic, all four sidebar groups, all 35 sidebar page IDs, and static `showPage` coverage for explicit and config-backed routes; wired `tools/sidebar-navigation-behavior.mjs` into local smoke and added full-acceptance static checks before rerunning behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 34 evidence: added the shared `EpataNavigationHistory` helper for browser back/forward and in-app Back behavior, verified page history push/replace/skip/pop handling, route hash encoding/decoding, history stack clamping, helper script load order, and static app wiring; wired `tools/navigation-history-behavior.mjs` into local smoke and added full-acceptance static checks before rerunning behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 35 evidence: added the shared `EpataGlobalSearch` helper for global-search matching and result routing, verified customer-name, invoice-number, order-number, proof-reference, vendor, product/SKU, job-name, no-match, and special-character searches, and verified result cards open the matching row modal when an id exists or fall back to the page route; wired `tools/global-search-behavior.mjs` into local smoke and added full-acceptance static checks before rerunning behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 36 evidence: added the shared `EpataQuickAdd` helper for Quick Add card definitions, page targets, modal targets, and date-based presets; verified every Quick Add card opens the intended page or modal, Etsy/direct/AR/AP/expense/asset/communication/queue presets carry the expected status/payment/date/default values, overrides merge safely, card labels/attributes are escaped, and the helper is loaded before `app.js`; wired `tools/quick-add-behavior.mjs` into local smoke and added full-acceptance static checks before rerunning behavior tests, smoke, full acceptance, clean Release build, and artifact cleanup checks.
- Round 37 evidence: expanded Quick Add to expose create cards for Product / Costing, Action Item, Audit Doc / Proof Index, and Customer / Vendor Contact in addition to Sale, Expense, Job, AR, AP, Asset, Communication, and Queue cards; verified all required create configs are present, the new presets use safe review/default values, and smoke/full-acceptance still create the underlying generic endpoint rows only in disposable databases.
- Round 38 evidence: added disposable smoke coverage for saved Quick Add-shaped Sale, Expense, AR, AP Bill, Job, Product, Action Item, Audit Doc, and Customer/Vendor rows; verified each saved row appears in the correct ledger endpoint, and verified the expected dashboard deltas for gross receipts, customer paid, direct expenses, estimated net, open AR, open AP, dashboard breakdown labels, and dashboard action list.
- Round 39 evidence: reconciled generic ledger table filtering coverage with explicit smoke markers; verified the `Needs Review` filter returns only `needsReview` rows, verified text search matches proof/source-reference fields as well as visible row data, and added full-acceptance static gating for the helper's `needs-review` path.
- Round 40 evidence: extracted relationship/customer/vendor detail row-open targeting into the relationship helper, verified Sales, AR, Job, Audit Doc, AR-only record, invoice-record, and empty-row targets preserve the intended route/config and row id, and wired smoke/full-acceptance gates for the shared row-open path.
- Round 41 evidence: added disposable Sales dashboard toggle coverage; created a reportable Sale, verified exact dashboard deltas and gross-receipts breakdown inclusion, saved the same row with `includeInDashboard=false`, verified all affected dashboard KPIs returned to baseline and the breakdown entry disappeared, then restored the toggle and verified totals returned.
- Round 42 evidence: tightened generic table status filtering for Sales edge statuses; verified `Needs Review` status rows match the Needs Review filter even without the boolean flag, verified `Refunded` rows are treated as closed/paid-filter rows instead of open rows, verified the Sales status options expose both statuses, and verified the badge path still styles refunded rows as bad.
- Round 43 evidence: added a shared AR-to-invoice-builder prefill helper, wired the AR modal `Make PDF Invoice` action through it, preserved builder-generated invoice numbers while carrying the AR number as source reference text, translated AR subtotal/discount/rush/tax/payment/status into invoice-builder controls, and added Node/static smoke coverage for the full mapping.
- Round 44 evidence: tightened invoice-builder ledger sync so generated invoice rows are matched only by the `Unified invoice <number>` source stamp, not by loose invoice-number/customer matches; verified an external AR row with the same number/customer/proof link remains unchanged, a separate generated AR row is created, invoice records show the PDF document plus external AR row only, and stats count exactly the builder invoice plus external AR balance.
- Round 45 evidence: added persistent Bill tax categories and paid-deductible AP tax rules; verified paid operating and COGS/material Bills affect dashboard tax-prep deductions, known costs, estimated net, tax-summary buckets, and tax-audit/AI-review outputs, while a paid non-deductible Bill stays out of deductible outputs; reran disposable smoke, full acceptance, and Release build checks.
- Round 46 evidence: added explicit dashboard urgency metadata for open AP Bills, displayed the urgency badge in Open Payables, tightened AI Review so closed paid/void Bills are not flagged overdue, and verified overdue, due-today, due-soon, scheduled, no-due-date, paid, and void AP cases in disposable smoke; reran smoke, full acceptance, and Release build checks.
- Round 47 evidence: added paid purchase export coverage that verifies a counted Expense with payment method, payment account, receipt proof, tax bucket, business-use percent, and normalized total flows into tax-summary API totals, tax-summary CSV, Expenses CSV, and the tax-package `expenses.csv`; updated Tax Prep/package wording to include bills/AP and reran smoke, full acceptance, and Release build checks.
- Round 48 evidence: added product costing negative-value coverage for both create and update paths; verified grams, material cost per gram, print hours, machine rate, packaging cost, design minutes, target price, and estimated cost clamp to safe non-negative values while preserving review state; reran smoke, full acceptance, and Release build checks.
- Round 49 evidence: added an unsaved AI Product draft helper and static/behavior coverage; verified AI product imports open a prefilled Product modal as a new review row, reset unsafe persisted identity fields, preserve mapped product data, avoid mutating the AI result, and do not save automatically; reran disposable smoke, full acceptance, and Release build checks.
- Round 50 evidence: added explicit Product / Costing catalog search coverage for SKU, material, and color, made color visible in the Product table, and added disposable asset normalization checks for create/update cost, business-use percent, and not-yet-expensed clamps; reran behavior tests, disposable smoke, full acceptance, and Release build checks.
- Round 51 evidence: added shared Tax Prep state helpers and display sections for asset tax handling and MakerWorld reward income status; verified asset-tagged expenses stay out of ordinary deductions, only Section 179/De Minimis expensed assets affect asset deductions, MakerWorld gift-card dollars marked as income count once, and points-only rewards do not double-count income; reran behavior tests, disposable smoke, full acceptance, and Release build checks.
- Round 52 evidence: made Business Accounts show an explicit Active/Inactive status column with stable sorting, added behavior coverage for that UI wiring, and added disposable dashboard/tax-summary isolation checks proving large active and inactive account balances do not affect income, expense, AR/AP, deduction, or working-net totals; reran behavior tests, disposable smoke, full acceptance, and Release build checks.
- Round 53 evidence: added shared Job Workflow helpers for exact timeline row opening and printer-queue prefill from Customer Jobs; verified customer-detail job rows still use row-open targets, timeline Job/Printer Queue events carry exact row IDs, selected jobs open unsaved Queue prefill with job/customer/material/color/details, and reran behavior tests, disposable smoke, full acceptance, and Release build checks.
- Round 54 evidence: added shared Communication card planning helpers, tightened communication card wrapping for long subjects/summaries/references, verified Incoming/Outgoing/Internal communication rows save and display, due follow-ups appear in AI Review and action-item automation preview, long pasted summaries stay preserved and wrapped, and reran behavior tests, disposable smoke, full acceptance, and Release build checks.
- Round 55 evidence: added shared Relationship detail-section, contact-target, and section-pagination helpers; verified customer detail plans include Sales, AR, PDF docs, Jobs, Communications, and Proof rows, vendor detail plans include Expenses, Bills/AP, Assets, and Proof rows, Add Contact Details targets the correct Party create/edit path, relationship sections clamp/page rows and preserve underlying open targets, seeded matching disposable customer/vendor relationship rows, and reran behavior tests, disposable smoke, full acceptance, and Release build checks.
- Round 56 evidence: made duplicate relationship/contact names explicit in customer/vendor directories with a visible Duplicate contacts status and warning while preserving the normalized merge rule; verified duplicate customer/vendor contact cards merge predictably by trimmed, case-insensitive name, disposable duplicate Party rows save/reload safely, and reran behavior tests, disposable smoke, full acceptance, and Release build checks.
- Round 57 evidence: broadened deterministic AI Review printer-queue attention rules so active overdue jobs, failed attempts, explicit Needs Review rows, and Needs Attention statuses are flagged while completed/cancelled queue rows stay quiet; verified overdue and failed queue fixtures plus closed negative cases in disposable smoke and full acceptance, then reran Release build checks.
- Round 58 evidence: hardened Document Intake proof upload validation so missing-file multipart requests, zero-byte files, unsupported extensions, and files over the 20 MB proof limit return clear 400 responses before any file or Audit Doc row is written; verified rejected uploads leave the disposable database and UploadedDocs clean, valid text proof upload still succeeds, and reran disposable smoke, full acceptance, and Release build checks.
- Round 59 evidence: added proof-upload coverage for filenames containing spaces, parentheses, and apostrophes; verified the original Audit Doc filename is preserved, the stored file remains under UploadedDocs with the special characters intact, file contents read back through `/api/audit-documents/{id}/file`, and reran disposable smoke, full acceptance, and Release build checks.
- Round 60 evidence: verified Document Intake's uploaded-result Edit Audit Doc action is still wired to the Audit Doc modal and that the uploaded Audit Doc row can be edited and reloaded with updated document type, related record, review status, and notes; reran disposable smoke, full acceptance, and Release build checks.
- Round 61 evidence: tightened Document Intake local classification so customer-paid order receipts and customer invoices beat generic receipt language, asset/equipment proofs beat generic purchase receipts, and vendor bill language routes to Bill/AP; verified uploaded proof suggestions for Sale, Expense, Asset, Invoice, Estimate, Bill/AP, Shipping/Sale Cost, and ambiguous Review lanes in disposable smoke and full acceptance, then reran Release build checks.
- Round 62 evidence: made proof storage-boundary checks explicit for Document Intake uploads; verified classified batch uploads, traversal-style filenames, normal proof uploads, and special-character proof filenames all resolve to existing files under the local `UploadedDocs` directory while rejected uploads leave no files behind; reran disposable smoke and full acceptance checks. Release build was attempted but blocked because the visible host .NET SDK folder is incomplete.
- Round 63 evidence: added a copied-real-data safety rehearsal to disposable smoke and full acceptance: the app backup endpoint is called with the real POST method, the downloaded backup is verified as a SQLite database, copied to a separate test-only DB file under `Data/`, hash-compared against the backup, checked to avoid the production DB name, and cleaned up; documented the manual copied-real-data rule in `docs/TEST_DATABASE_GUARD.md`; reran disposable smoke and full acceptance checks.
- Round 64 evidence: moved backup-and-copy rehearsal into a true preflight immediately after `/api/health` confirms the disposable DB path, before admin/config, import, export, archive, restore, or clear-style checks run; added explicit preflight-completed assertions to smoke CRUD archive/restore paths, full-acceptance CRUD archive/restore paths, admin/clear checks, legacy import checks, and export/backup checks; documented the preflight order in `docs/TEST_DATABASE_GUARD.md`; reran disposable smoke and full acceptance checks.
- Round 65 evidence: added a disposable backup/restore drill to `tools/local-smoke.ps1`: after uploads, invoice records, and settings are seeded, the script downloads a SQLite backup, stops the app, removes only `Data/local-smoke.db` plus WAL/SHM files, restores the backup to that disposable DB path, restarts the app, and verifies document counts, Audit Doc counts, invoice data, uploaded proof file access, special filename metadata, and settings after restart; documented the restore drill in `docs/TEST_DATABASE_GUARD.md`; reran disposable smoke and full acceptance checks.
- Round 66 evidence: tightened invoice-builder type/status/save workflow coverage by extracting tested helper plans for doc-type changes, paid-invoice totals, and save requests; verified a loaded `EST-2026-0014` estimate switched to invoice clears the stale estimate number, remaps Accepted to Paid, auto-pays the full invoice total with zero balance, creates a new `INV-2026-0014` instead of updating the original estimate, appends the "original not overwritten" private note, and then treats the new invoice as an update on repeat save; bumped invoice-builder module cache versions and reran behavior, disposable smoke, and full acceptance checks.
- Round 67 evidence: tightened invoice-builder line-item controls by extracting and testing the editable line-item row template, verifying added rows expose editable description/details/quantity/rate fields, escape user-entered text, and wire the remove button through the app handler; routed Add Line Item and Remove Line Item button actions through preview-refresh wrappers so totals and live preview update after row changes; verified removing a row changes invoice totals/balance; bumped invoice-builder cache versions and reran behavior, disposable smoke, and full acceptance checks.
- Round 68 evidence: added live-preview refresh coverage for the invoice builder, verifying the builder update handler recalculates totals, refreshes the preview, and schedules autosave; input/change events debounce into `refreshInvoicePreview`; the preview iframe receives `renderInvoiceHtml` output from current form data and app config; and rendered preview HTML changes from stale customer/project/line-item values to updated values; reran behavior, disposable smoke, and full acceptance checks.
- Round 69 evidence: added Preview button coverage for the invoice builder, verifying the button requests preview mode, preview generation reads current form data, stays read-only instead of saving first, passes the preview flag into `generatePdf`, opens the named local preview window, writes the current invoice HTML, focuses the output, and does not trigger browser print mode; also fixed the disposable Round 46 bill urgency fixture to use run-date-relative overdue/today/soon/future dates so local smoke does not rot as the calendar moves; reran behavior, disposable smoke, and full acceptance checks.
- Round 70 evidence: added Download PDF coverage for the invoice builder, verifying the primary button requests printable output mode, download generation uses current form data, saves first when API-backed, opens the named local print window, writes the printable invoice HTML with current customer/project/line/balance values, focuses the output, and includes the delayed browser print script; reran behavior, disposable smoke, and full acceptance checks.
- Round 71 evidence: made AI Import PDF easier to reach by adding a Builder-header entry point next to Preview/Save/Download while preserving the Records-header entry point; added behavior coverage proving both buttons route to the hidden `invoicePdfImportFile` picker, that the picker accepts PDF files only, and that the selected file event maps into the existing PDF-draft import flow; reran behavior, disposable smoke, and full acceptance checks.
- Round 72 evidence: expanded AI Import PDF coverage for P1 import resilience: verified multi-page EPATA text maps document number, dates, customer contact fields, project fields, material/color/infill, discount/rush/tax, line description/details, pricing guide, and terms without creating records before user save; verified PDFs missing email/phone/address still map available document, customer, project, and line-item fields while leaving absent contact fields blank; verified private import metadata such as PDF import assistance/local-rules receipts does not leak into customer PDF output; verified oversized PDF upload returns the friendly 20 MB AI-intake limit message; reran behavior, disposable smoke, and full acceptance checks.
- Round 73 evidence: added shell/navigation/file-picker behavior coverage proving AI Import PDF and proof upload controls open their hidden file inputs and route selected files to the existing import/upload handlers; verified the embedded invoice tool fetches the standalone invoice shell, extracts the workspace, imports the current invoice app module, and initializes it inside the main ledger shell; verified Dashboard, Calculator, Builder, Records, Rate Card, and Settings tabs exist in both standalone and embedded invoice shells; verified switching between invoice pages preserves the mounted tool state through snapshots unless an intentional reset is requested; verified Ctrl+S and Ctrl+Shift+N shortcut wiring; reran the new behavior script, disposable smoke, and full acceptance checks.
- Round 74 evidence: extracted tested Product lookup mapping for the invoice builder, verified Product datalist options fill from lookup rows and a selected product can populate material, color, pricing inputs, and a target-price line item; verified active record bar text for new, estimate, and invoice records; verified failed save state keeps the active record identity and never reports saved; added PDF wrapping rules for long bill-to/address lines; verified internal notes, private project notes, and import receipts stay out of customer PDF output; reran invoice-builder behavior, disposable smoke, and full acceptance checks.
- Round 75 evidence: hardened invoice PDF layout/output behavior by applying the selected A4/Letter/Legal page size to renderer dimensions and print CSS, adding table-header repeat and row page-break protection, adding wrapping rules for long table text/terms/footer content, removing preview-only blank print-script whitespace so preview and printed output match except for the intentional print trigger, and verifying 25 line items, long description/details, long terms, all three page sizes, and preview/print parity through behavior, disposable smoke, and full acceptance checks.
- Round 76 evidence: added explicit AI Operations smoke markers and tightened full acceptance assertions for local-only workflows: verified paid marketplace order save rejects a missing Sale Date with a friendly 400, Product Import returns an unsaved Product draft plus listing copy, Job Planner action creation is idempotent, Slicer Reader extracts material, grams, print hours, plate count, and quantity, Listing Writer returns copy without modifying the Product row, and Ask Ledger returns the matching local row with route/id open-record data and read-only safety text.
- Round 77 evidence: added explicit AI Estimate Intake smoke markers for known Product/catalog matching, 300-piece bulk guitar-pick parsing and bulk-handling quantity, unsaved draft creation with unchanged Product/document counts, builder prefill wiring that opens the embedded estimate builder without saving, review preview coverage for every mapped field plus line items/questions/warnings/JSON, and a friendly 400 for source text over 500,000 characters.
- Round 78 evidence: added Tax Prep/Admin/export smoke markers: generated and displayed federal estimated, NJ estimated, NJ ST-50, annual, and custom tax obligations for a selected year; saved mileage-rate profile data; proved 2027 mileage changes business miles, deduction estimate, parking/tolls, working net, and review counts; verified tax-year-specific summary/export behavior and Tax Prep review UI wording; parsed representative CSV exports with expected Excel-friendly headers; and verified Admin/Data shows app/database info plus backup button wiring.
- Round 79 evidence: added Ledger Map/Workflow/Help smoke markers: verified Ledger Map renders job/invoice/AR/sale/proof/action lanes and explanations, local sample questions and Ask Ledger/open-record wiring exist, Workflow Guide uses current order-to-cash and expense labels, Help/Glossary links to relevant app areas, and the Ledger/Workflow/Help grids collapse through tablet/mobile CSS instead of requiring horizontal scrolling.
- Round 80 evidence: added AI Review/Local AI smoke markers: verified AI Review status reports deterministic local-only read-only behavior, model-backed/local AI UI copy clearly explains provider/local boundaries and no automatic writes, review cards expose empty-state and open-record routing, seeded review issues for underpriced products/unlinked proof/tax review appear with routes, and local smoke plus full acceptance passed against disposable databases.
- Round 81 evidence: added calculator/rate-card smoke markers and fixed shared currency rounding for half-cent display values; verified Rate Card copy-ready wording and default rate values match calculator defaults, verified calculator rounded totals render consistently into builder/PDF behavior output, and verified a saved invoice keeps its calculator fields after future config changes while API, Sales sync, AR sync, and CSV exports agree on the synced money values.
- Round 82 evidence: added server-side `/api/config` normalization and disposable smoke coverage for invalid settings; verified control characters are stripped, whitespace is trimmed, business email is normalized, invalid brand colors fall back safely, calculator rates/fees clamp or default to UI-safe limits, normalized settings survive reload, Round 16 baseline settings are restored afterward, and local smoke plus full acceptance passed.
- Round 83 evidence: hardened main shell and invoice records layout CSS for long names, notes, upload filenames, proof paths, AI file lists, mapped values, and record links using hard text wrapping while preserving compact action/numeric cells; added `LongContentLayoutRound83` smoke coverage and verified local smoke plus full acceptance passed.
- Round 84 evidence: added modal save in-flight guarding so Quick Add prefilled create modals ignore accidental double-click submissions while disabling the Save button during the request; added `ModalSaveDoubleClickRound84` and `QuickAddDoubleClickRound84` behavior/smoke markers and verified local smoke plus full acceptance passed.
- Round 85 evidence: added Local AI Start/Stop in-flight guarding so rapid clicks cannot overlap actions, kept both controls disabled while a request is running, and added safe smoke probes for missing LM Studio installs and already-off stop behavior without disturbing a running local AI server; `LocalAiStartStopRound85`, local smoke, and full acceptance passed.
- Round 86 evidence: widened the local EPATA PDF mapper for older invoice/estimate wording variants (`Invoice No`, short `Due`, `Client`, `Job Name`, `Job Description`, `Filament`, `Colour`, `Fill`, `Grand Total`, `Paid`, old `Line Items`, and plain `Notes`) while preserving existing multi-page import behavior; `PdfImportOldWordingRound86`, verify-build, local smoke, and full acceptance passed.
- Round 87 evidence: added test-safe launch parameters to `run.ps1`, argument forwarding/no-pause support to `run.bat`, and `tools/launch-rehearsal.ps1`; verified `run.ps1`, `run.bat`, the Release DLL, and isolated published output each start on local ports with disposable DBs reported by `/api/health`, browser auto-open disabled for rehearsal, and the production DB unchanged; launch rehearsal, local smoke, and full acceptance passed.
- Round 88 evidence: completed dependency/runtime health review in `docs/RUNTIME_HEALTH_REVIEW.md`, confirmed .NET SDK/runtime and publish output size, reproduced NuGet high-severity advisory `GHSA-2m69-gcr7-jv3q` for transitive `SQLitePCLRaw.lib.e_sqlite3` `2.1.11`, fixed it with a direct native package override to `3.50.3`, verified `dotnet list package --vulnerable --include-transitive` reports no vulnerable packages, and reran launch rehearsal, local smoke, and full acceptance.
- Round 89 evidence: added `tools/large-data-stress.ps1`, seeded a disposable DB through HTTP APIs with 250 Sales, 250 Expenses, 250 AR rows, 250 Customer Jobs, 500 invoice/estimate documents, and 50 Audit Document proof metadata rows, then verified dashboard, invoice records list, document search, invoice type/status filtering, sales list, lookup shape, and proof metadata list response times stayed within local thresholds; `LargeDataStressRound89` passed with dashboard 146ms, invoice records 60ms, document search 32ms, invoice filter 21ms, and proof metadata 11ms.
- Round 90 evidence: added DOCX support to proof uploads, extracted DOCX preview text from `word/document.xml`, added `tools/upload-performance.ps1`, and verified valid PDF, DOCX, and PNG uploads complete through `/api/documents/upload` in 122ms with Audit Doc metadata and PDF/DOCX previews preserved; upload performance, local smoke, and full acceptance passed.
- Round 91 evidence: added local SQLite busy/locked middleware returning a friendly 503 JSON retry message, switched local app logging to console/debug to avoid Windows EventLog access failures masking database errors, added `tools/sqlite-lock-smoke.ps1`, and verified a disposable DB write locked by a held SQLite transaction returns 503 then recovers after lock release; SQLite lock smoke, local smoke, and full acceptance passed.
- Round 92 evidence: added `tools/repeated-start-stop.ps1` and verified five repeated launches on the same local port against `Data/repeated-start-stop.db`, `/api/health` returned the expected DB each time, the first saved Sale persisted across restarts, each stop freed the port, and no orphan port owner remained.
- Round 93 evidence: consolidated import/export round-trip review from the current `tools/local-smoke.ps1` run: representative CSV exports parse with `ConvertFrom-Csv`, expected Excel-friendly headers are asserted, formula-like values are guarded, archived Sales are excluded by default and included with `includeArchived=true`, tax-package ZIP contents are read back and parsed, and downloaded app backups are verified as SQLite databases.
- Round 94 evidence: consolidated tax/export snapshot coverage from the current smoke suite: known seeded rows assert exact dashboard/tax-summary deltas for gross receipts, customer paid including tax memo, NJ sales tax, deductions, mileage, asset treatment, MakerWorld reward income, annual/custom/generated tax obligations, tax-year switching, and representative tax CSV/package exports.
- Round 95 evidence: consolidated security/local-safety scan coverage from current smoke/full-acceptance runs and `docs/TEST_DATABASE_GUARD.md`: static/API/export checks verify no secret-shaped values are exposed, file download paths reject arbitrary local paths, upload filenames cannot escape `UploadedDocs`, local/private URLs are blocked in AI intake, customer-facing HTML escapes user text, and destructive database clear/import routes return disabled messages.
- Round 96 evidence: added `tools/final-release-rehearsal.ps1` and verified clean, seeded medium, and copied-real-data database profiles all launch with `/api/health` reporting the intended disposable/copy DB, create/read Sales, Expenses, and paid invoice documents by API ID, exercise dashboard/lookups/tax/timeline/export/backup endpoints, and leave `Data\epata-business-ledger.db` plus its WAL/SHM files unchanged.
- Round 97 evidence: added `tools/structured-log-review.ps1` and `docs/STRUCTURED_LOG_REVIEW.md`, fixed startup schema patching so existing SQLite columns are checked with `PRAGMA table_info` before `ALTER TABLE`, reran build, local smoke, full acceptance, launch rehearsal, and final-release rehearsal, and verified structured log review reports 0 hard findings with only the known ASP.NET analyzer `AD0001` warning remaining.
- Round 98 evidence: strengthened the invoice-builder behavior harness with a live-preview render benchmark for 80 renders of a 24-line document while preserving the 150ms input/change debounce checks, wired `PreviewDebouncePerfRound98` into local smoke, and verified the node harness, local smoke, and full acceptance passed.
- Round 99 evidence: consolidated automated-test backlog coverage from `tools/local-smoke.ps1` and `tools/relationship-directory-behavior.mjs`: CSV export snapshots assert Excel-friendly headers, representative rows, archived inclusion, formula guards, tax-package ZIP contents, and API/export parity; tax summary snapshots assert known seeded deltas for gross receipts, customer paid, NJ sales tax buckets, deductions, mileage, assets, MakerWorld reward income, obligations, and tax-year switching; relationship-view tests cover customer/vendor aggregation, detail sections, contact-card targets, pagination, duplicate names, and open-record targets.
- Round 100 evidence: added `tests/EPATA.BusinessLedger.WebApplicationFactoryTests`, exposed the app `Program` for test-host startup, excluded `tests\**` from the app project, restored `Microsoft.AspNetCore.Mvc.Testing` 10.0.0, and ran the console WebApplicationFactory harness against `Data\webapplicationfactory-tests.db`; it covered core, tax, AI-status, operations-status, lookups, documents, generic CRUD route lists, exports, backup, and safety endpoint groups, verified unsafe estimate-to-invoice `PUT` returns 400 and preserves the original, and kept browser auto-open disabled. Also reran clean Release build, local smoke, and full acceptance.
- Round 101 evidence: extracted dashboard breakdown rendering and chart-state decisions into `wwwroot/js/dashboard-state.js`, wired the main shell to load and use it, added `tools/dashboard-state-behavior.mjs`, and verified populated KPI breakdown rows preserve route/id open targets, escape labels, show negative contributions, zero-row breakdowns display an explicit empty state, and line/donut chart states handle empty, all-zero, and nonzero data. Wired the markers into local smoke, then reran the dashboard behavior harness, disposable local smoke, full acceptance, and a clean Release build.
- Round 102 evidence: added `tools/rapid-submit-behavior.mjs`, added main-shell `runExclusiveAction` guards for ledger archive/review, document upload, legacy invoice import, and unified invoice editor save/duplicate/convert/archive, added standalone invoice-builder `runExclusiveToolAction` guards for PDF draft import and database import, added standalone records guards for duplicate/convert/archive/restore, and verified existing modal-save, invoice-save, AI Operations, marketplace-save, and Local AI in-flight protections. Wired `RapidSubmitRound102` into local smoke, then reran syntax checks, the rapid-submit behavior harness, disposable local smoke, full acceptance, and a clean Release build.
- Round 103 evidence: completed a source-level screen-reader label pass for the local shells by adding labels to the sidebar toggle, standalone invoice PDF/database hidden file inputs, generated Document Intake/proof file inputs, invoice line remove buttons, records duplicate/archive icon buttons, and first/last pager icon buttons; added `tools/accessibility-static-behavior.mjs`, wired `ScreenReaderLabelsRound103` into local smoke, updated older file-picker harnesses to allow accessible attributes without weakening PDF-only checks, and reran syntax checks, targeted Node behavior harnesses, disposable local smoke, full acceptance, and a clean Release build.
- Round 104 evidence: added `tools/offline-local-boundary-behavior.mjs` to verify normal browser sources do not hardcode remote fetch/beacon/websocket dependencies or hotlink required assets, app/browser auto-open is explicitly gated and disabled in test/rehearsal runners, data/backups/uploads resolve under local content-root folders, Local AI stays loopback-only, user-supplied AI URL intake blocks private/local hosts, and disposable runners use local `Data` paths; wired `OfflineLocalBoundaryRound104` into local smoke, then reran the boundary harness, disposable local smoke, full acceptance, and a clean Release build.
- Round 105 evidence: tightened vague empty-state copy in the printer queue and standalone invoice dashboard, added `tools/ui-empty-loading-behavior.mjs`, verified 26 main-shell empty states have readable title/detail copy, checked specific no-data states for dashboard charts, timelines, communications, relationship rows, Document Intake, AI Estimate, AI Operations, Global Search, invoice center, Tax Prep, and category summaries, and verified loading placeholders are replaced by ready or visible error states in the main shell and standalone builder. Wired `EmptyStatesRound105` and `LoadingStatesRound105` into local smoke, then reran syntax checks, the UI-state harness, disposable local smoke, full acceptance, and a clean Release build.
- Round 106 evidence: expanded `tests/EPATA.BusinessLedger.WebApplicationFactoryTests` with direct HTTP integration coverage for the UI-style estimate-to-invoice type-change path: unsafe direct `PUT` still returns 400 and preserves the estimate, the proper convert endpoint creates a new invoice id/number, the original estimate remains an accepted estimate with conversion notes, saving the converted invoice as paid creates Sales/AR rows only for the invoice number, and the estimate number never becomes Sales/AR income. Verified `TypeChangeCreateInvoiceRound106`, disposable local smoke, full acceptance, and a clean Release build.
- Round 107 evidence: strengthened `tools/sqlite-lock-smoke.ps1` to simulate a OneDrive-style SQLite write lock against `Data\sqlite-lock-smoke.db` under the current OneDrive workspace path; verified the locked write returns the friendly 503 retry message, the failed `ROUND91-DURING-LOCK` save creates zero partial/duplicate Sales rows, pre-lock and post-lock writes are retained exactly once, `PRAGMA integrity_check` returns `ok`, and the app recovers after lock release. Verified `SqliteOneDriveDelayRound107`, `NoDuplicateDuringLockRound107`, disposable local smoke, full acceptance, and a clean Release build.
- Round 108 evidence: attempted live headless Chrome/Edge screenshot and DOM capture against the disposable browser server, but nonblank browser rendering still produced no usable output, so live visual/browser rows remain open. Added `tools/responsive-layout-behavior.mjs` instead for source-level responsive hardening: verified main shell and invoice-builder tablet/mobile breakpoints, dashboard/grid collapse rules, full-width/mobile action rows, intentional horizontal table and invoice-preview scrolling, viewport-bounded modals, hard wrapping for long table/card text, and printer-board many-card behavior with capped completed cards. Wired `ResponsiveLayoutRound108`, `NoClipSourceRound108`, `DashboardResizeRound108`, and `PrinterBoardManyCardsRound108` into local smoke.
- Round 109 evidence: added a config-gated `App:ArtificialLatencyMs` middleware for test-only local latency rehearsal plus `tools/slow-machine-simulation.ps1`; verified the app starts against `Data\slow-machine-simulation.db` with `App:OpenBrowserOnStart=false`, `/api/app-info` and `/api/dashboard` include the artificial delay, dashboard data still returns, and the existing loading-state, debounced search/preview, and disabled duplicate-submit harnesses stay green. Wired `SlowMachineSimulationRound109` into local smoke and listed the script in `docs/TEST_DATABASE_GUARD.md`.
- Round 110 evidence: added a real modal Tab focus trap to `wwwroot/js/modal-lifecycle.js`, wired generic and dashboard modals to trap Tab in the main shell, added visible `:focus-visible` rings to the main and standalone invoice-builder shells, made the standalone invoice dashboard document-number action a real escaped button, labelled the standalone line-item remove button, and added `tools/keyboard-accessibility-behavior.mjs`. Verified modal focus cycling, Escape handling, keyboard-reachable modal/table/pager/sidebar/invoice controls, Ctrl+S/Ctrl+Shift+N shortcut coverage, and focus visibility through targeted behavior harnesses.
- Round 111 evidence: added Playwright browser-test scaffolding with `playwright.config.cjs`, `package.json`, disposable `tools/playwright-server.ps1`, `tests/playwright/sidebar-modals.spec.cjs`, and `tests/playwright/invoice-builder-workflow.spec.cjs`; guarded the Playwright server to use only `Data\playwright-browser-tests.db` with browser auto-open disabled; syntax-checked the new specs/config and reran offline boundary, local smoke, full acceptance, and clean Release build. Playwright specs were added but not executed in this environment because the Playwright package/browser runtime is not installed or available here.
- Round 112 evidence: added `tests/playwright/navigation-upload-responsive.spec.cjs` for file input/button wiring and responsive clipping checks across main shell and standalone invoice-builder viewports; added `tests/playwright/visual-regression.spec.cjs` plus `test:visual` and `test:visual:update` npm scripts for dashboard, document intake, tax prep, invoice builder, and invoice records screenshot baselines. Syntax-checked the new specs/package file and reran local smoke, full acceptance, and clean Release build. These browser specs were added but not executed here because the Playwright package/browser runtime is not installed or available in this session.
- Round 113 evidence: installed Playwright locally, added stable `tools/run-playwright.ps1` browser-test runner around the disposable `Data\playwright-browser-tests.db`, and executed the browser suites. Functional Playwright passed 8/8 for every main sidebar page, every standalone invoice-builder tab, modal focus trapping, upload button/file-input wiring, responsive no-clipping checks at 1440x900, 1280x720, and 390x844, and the full estimate-to-paid-invoice lifecycle including Sales/AR sync, archive, restore, CSV export, and nonblank live PDF preview. Visual Playwright regenerated and then compared baselines for dashboard, document intake, tax prep, invoice builder, and invoice records at 1440x900, 1280x720, and 390x844, passing 15/15. Local smoke also caught and verified a C# fix for date-only business fields rolling into the next day when default clock offsets crossed midnight.
- Round 114 evidence: added `tests/playwright/invoice-state-stress.spec.cjs` and hammered the standalone invoice builder plus Records page against disposable `Data\playwright-browser-tests.db`: full per-field estimate/invoice round-trip, saved estimate type-change to paid invoice, Records Create Invoice, old estimate reopen after conversion, delayed save while opening another record, and injected backend 500 save failure. Fixed mixed document/AR numeric ID collision in Records actions, stale save completion overwriting active identity, status restore ordering, invoice due-date label restore, builder tax-rate persistence, PDF/import status prefill ordering, and saved-state timing before Records refresh. Verified `npm run test:browser` 13/13, `npm run test:visual` 15/15, `tools\invoice-builder-behavior.mjs`, `tools\shell-navigation-file-picker-behavior.mjs`, Release build, `tools\local-smoke.ps1`, and `tools\full-acceptance.ps1`.
- Round 115 evidence: closed the final 16 open rows with `tests/playwright/final-checklist-closure.spec.cjs`, expanded `tests/playwright/visual-regression.spec.cjs`, and added `tools\onedrive-boundary-smoke.ps1`; verified uploaded existing EPATA invoice and estimate PDFs map through the real AI Import PDF UI as unsaved drafts before explicit save, live preview/download/print output for Letter/A4/Legal, PDF preview screenshots for invoice/estimate/paid/partial/long-address/long-terms/20-plus-line cases, sidebar/history/keyboard/table/responsive behavior, Document Intake upload edge cases, AI Operations local-rules workflows and open-record actions, dark/light screenshot passes, and OneDrive-hosted disposable DB backup/restart persistence without touching `Data\epata-business-ledger.db`. Verified `npm run test:browser` 26 passed / 14 intentional skips, `npm run test:visual` 70/70, `tools\onedrive-boundary-smoke.ps1`, `tools\invoice-builder-behavior.mjs`, `tools\shell-navigation-file-picker-behavior.mjs`, and `dotnet build EPATA.BusinessLedger.csproj -c Release --no-restore`.

## Test Priorities

- `P0`: Must pass before trusting the app with real records.
- `P1`: Should pass before a normal working session.
- `P2`: Useful polish, resilience, or exploratory coverage.

## Safe Local Test Setup

- [x] `P0` Run tests only against a disposable database unless explicitly doing read-only inspection of the real database.
- [x] `P0` Confirm `/api/health` reports the expected database path before creating, editing, or deleting records.
- [x] `P0` Keep the real DB out of automated destructive tests: do not point acceptance tests at `Data/epata-business-ledger.db`.
- [x] `P0` Verify the acceptance script uses `Data/full-acceptance.db`.
- [x] `P0` Before any manual test on copied real data, create a database backup from the app and copy the DB to a new file.
- [x] `P0` Verify backups are created before testing admin, import, export, archive, restore, or clear behavior.
- [x] `P1` Test with a fresh empty database.
- [x] `P1` Test with a seeded medium database: customers, products, jobs, estimates, invoices, sales, expenses, tax rows, files.
- [x] `P1` Test after app restart to catch persistence and startup migration issues.
- [x] `P1` Test while the Release exe is already running so build and launch behavior is understood.
- [x] `P2` Test with OneDrive sync paused and enabled, since local SQLite and generated files live under OneDrive.

## Baseline Automated Checks

- [x] `P0` `dotnet build EPATA.BusinessLedger.csproj -c Release -o obj\verify-build /p:UseAppHost=false` passes.
- [x] `P0` PowerShell 7 acceptance suite passes: `pwsh -NoProfile -ExecutionPolicy Bypass -File tools\full-acceptance.ps1`.
- [x] `P0` Acceptance suite cleans up disposable files after success and failure.
- [x] `P0` `/api/health` returns `status: ok`, the expected database path, server time, and mode.
- [x] `P0` Static shells load: `/`, `/index.html`, `/invoice-builder/`, `/invoice-builder/index.html`.
- [x] `P0` Core JS/CSS assets load with `200`.
- [x] `P1` Acceptance suite verifies every generic CRUD route at least once.
- [x] `P1` Acceptance suite verifies both invoice API aliases: `/api/documents` and `/api/invoice-documents`.
- [x] `P1` Acceptance suite verifies blocked destructive operations stay blocked: database clear and full import.
- [x] `P2` Add browser automation for page navigation, modal focus, file upload buttons, and responsive layout.

## Current Coverage Snapshot

Already covered by `tools/full-acceptance.ps1`:

- [x] `P0` Static shell and asset smoke checks.
- [x] `P0` Health, dashboard, tax audit, tax profile, tax summary, calendar, lookups, app info.
- [x] `P0` Generic CRUD create/read/update/archive/restore for each ledger entity.
- [x] `P0` AI estimate intake from text, image, PDF, DOCX, and blocked private URL.
- [x] `P0` AI operations: reconciliation, action sync idempotency, marketplace import/save, product import, job plan, slicer read, listing, ask ledger.
- [x] `P0` Invoice/estimate API workflows: next numbers, create estimate, failed type mutation returns 400, duplicate, convert estimate to invoice, create paid invoice, partial invoice, void invoice, delete/archive effects, stats, latest, API alias agreement.
- [x] `P0` New PDF import endpoint maps one invoice PDF and one estimate PDF into drafts.
- [x] `P0` Export and backup endpoints.

Still needing browser/manual coverage:

- [x] `P0` The exact invoice-builder UI workflow for switching type/status and saving.
- [x] `P0` Live PDF preview and downloaded/printed output visual inspection.
- [x] `P0` File picker behavior for AI Import PDF and proof uploads.
- [x] `P1` Sidebar navigation, modal focus, keyboard shortcuts, table paging/sorting/filtering, and responsive layout.
- [x] `P1` Visual checks for dashboard, invoice builder, records, document intake, tax prep, and AI screens.

## Global Local App Shell

- [x] `P0` App launches from `run.ps1`, `run.bat`, and published output.
- [x] `P0` App does not require internet access for normal local ledger work.
- [x] `P0` App does not auto-open or write to unexpected directories.
- [x] `P1` Sidebar renders all nav groups: Command, Books, Operations, Control.
- [x] `P1` Every sidebar item opens its intended view without console errors.
- [x] `P1` Browser back/forward works with page history.
- [x] `P1` Collapsed sidebar preference persists after reload.
- [x] `P1` Empty states are readable and specific for each page.
- [x] `P1` Loading states clear after API responses.
- [x] `P1` Toasts appear for save, delete/archive, restore, upload, failure, and copy actions.
- [x] `P1` Modals open, close by button, close by backdrop, and close with expected keyboard flow.
- [x] `P1` Modal save with invalid data does not close silently.
- [x] `P1` Search/filter inputs debounce or update without freezing on medium data.
- [x] `P1` Tables paginate, sort, and preserve row actions after filtering.
- [x] `P2` Layout works at desktop, laptop, tablet width, and mobile width.
- [x] `P2` No text overlaps, clipped buttons, or unusable tables on narrow screens.
- [x] `P2` Keyboard-only users can reach primary actions, table actions, and modal fields.

## Dashboard

- [x] `P0` Dashboard loads with an empty database.
- [x] `P0` Dashboard loads with mixed active and archived rows.
- [x] `P0` Gross receipts, net, sales tax memo, open AR, open AP, expense, and review counts match seeded data.
- [x] `P1` Dashboard excludes archived rows from active totals.
- [x] `P1` Dashboard excludes void invoices and non-counted expenses from active totals.
- [x] `P1` Dashboard breakdown modal opens for each KPI and lists the correct contributing records.
- [x] `P1` Breakdown modal handles zero-row results.
- [x] `P1` Charts render with nonzero data and with all-zero data.
- [x] `P2` Dashboard remains readable after resizing.

## Global Search

- [x] `P1` Search finds matches by customer name.
- [x] `P1` Search finds matches by invoice number, order number, proof reference, product, vendor, and job name.
- [x] `P1` Search result "Open" actions route to the right page or modal.
- [x] `P1` Search handles no matches.
- [x] `P2` Search handles special characters such as `#`, `/`, apostrophes, and email addresses.

## Quick Add

- [x] `P1` Each quick-add card opens the right modal with the expected presets.
- [x] `P1` Quick-add creates a Sale, Expense, Job, Product, Action Item, Audit Doc, AR row, and Customer/Vendor.
- [x] `P1` Saved quick-add rows appear in the correct ledger page and dashboard totals.
- [x] `P2` Quick-add does not duplicate accidental double-click submissions.

## Generic Ledger CRUD

Run create, read, update, archive, restore, filter, sort, and export checks for every generic route:

- [x] `P0` Parties / customers and vendors.
- [x] `P0` Sales / income.
- [x] `P0` Receivable invoices / AR ledger rows.
- [x] `P0` Bills / AP.
- [x] `P0` Expenses.
- [x] `P0` Customer jobs.
- [x] `P0` Audit documents.
- [x] `P1` Products and costing.
- [x] `P1` Assets / equipment.
- [x] `P1` MakerWorld rewards.
- [x] `P1` Business accounts.
- [x] `P1` Action items.
- [x] `P1` Customer communications.
- [x] `P1` Printer queue items.
- [x] `P1` Tax obligations.
- [x] `P1` Mileage logs.
- [x] `P1` App settings.

For each generic ledger page:

- [x] `P0` Required fields fail safely when blank.
- [x] `P0` Numeric fields normalize negative or impossible values according to business rules.
- [x] `P0` Date fields save and reload consistently.
- [x] `P0` Created and updated timestamps behave correctly.
- [x] `P0` Archive hides rows by default.
- [x] `P0` Restore makes rows visible again.
- [x] `P1` `Needs Review` filter finds relevant rows.
- [x] `P1` Text search finds visible columns and proof references.
- [x] `P1` CSV export contains the expected rows and headers.
- [x] `P1` Row opened from relationship/customer views edits the same underlying record.
- [x] `P2` Long notes, long filenames, and long customer names do not break layout.

## Sales / Income

- [x] `P0` `customerPaid` normalizes to item sales + shipping + sales tax when given inconsistent input.
- [x] `P0` Sales tax memo does not incorrectly count as net income.
- [x] `P0` Platform fees, shipping label cost, refunds, and COGS reduce net estimates.
- [x] `P0` Payment method maps to the correct tax export payment channel.
- [x] `P0` Marketplace-collected tax stays distinguishable from seller-collected tax.
- [x] `P1` Include-in-dashboard toggle controls dashboard totals.
- [x] `P1` Refunded and needs-review statuses display and filter correctly.
- [x] `P1` Tracking number and ship-by date persist.

## Receivables / AR Ledger

- [x] `P0` Invoice total, amount paid, and balance due normalize correctly.
- [x] `P0` Partial payment status shows remaining balance.
- [x] `P0` Paid status shows zero balance.
- [x] `P0` Void status excludes row from active open AR.
- [x] `P0` AR-only rows appear in invoice records as ledger rows with no PDF actions.
- [x] `P1` "Make PDF Invoice" path from AR/customer context pre-fills the invoice builder correctly.
- [x] `P1` AR row with external invoice link remains separate from builder PDF records.

## Bills / AP

- [x] `P0` Total normalizes to amount + sales tax.
- [x] `P0` Amount paid and balance due normalize across unpaid, partial, paid, and void.
- [x] `P1` Payment account persists.
- [x] `P1` Tax deductible and needs-review flags affect review/tax outputs as expected.
- [x] `P2` Due-date ordering flags urgent bills correctly.

## Expenses

- [x] `P0` Total normalizes to amount + sales tax.
- [x] `P0` Business-use percent clamps to valid range.
- [x] `P0` Non-deductible, memo-only, asset, and unchecked expenses are excluded from deductible totals.
- [x] `P0` COGS/material expenses are classified separately from operating expenses.
- [x] `P1` Receipt proof paths persist.
- [x] `P1` Paid purchase records flow into tax prep and exports correctly.

## Products / Costing

- [x] `P1` Product cost fields save and reload.
- [x] `P1` Negative grams, costs, hours, and target prices normalize safely.
- [x] `P1` Product names populate estimate/invoice builder product datalist.
- [x] `P1` Product import AI draft can open a prefilled Product modal without saving automatically.
- [x] `P2` Product catalog search handles SKU, material, and color.

## Assets / Equipment

- [x] `P1` Asset cost and business-use percent normalize.
- [x] `P1` In-service date, warranty end, tax treatment, counted expense, and source proof persist.
- [x] `P1` Asset rows show in tax prep as excluded or asset-specific, not ordinary expenses unless intended.

## MakerWorld Rewards

- [x] `P1` Points, gift card amount, code last 4, status, and income status persist.
- [x] `P1` Reward income status is visible in tax review.
- [x] `P2` Gift card and points values cannot accidentally double-count income.

## Business Accounts

- [x] `P1` Opening and current balance save and reload.
- [x] `P1` Active/inactive status is visible.
- [x] `P2` Balances do not get included in income/expense totals unless explicitly intended.

## Customer Jobs

- [x] `P0` Job status changes persist: lead, quoted, open, in progress, invoiced, completed, paid, cancelled.
- [x] `P0` Related order and invoice numbers link into job timeline.
- [x] `P1` Quote, invoice amount, amount paid, due date, and ship-by date persist.
- [x] `P1` Jobs can be opened from customer detail and timeline views.
- [x] `P1` Job data can feed printer queue prefill.

## Customer Communications

- [x] `P1` Incoming, outgoing, and internal communication rows save and display.
- [x] `P1` Follow-up date and status appear in review/automation output.
- [x] `P1` Communications link into job timeline by customer, job number, order number, or invoice number.
- [x] `P2` Long message summaries do not break cards.

## Printer Queue

- [x] `P0` Queue item create/update/archive/restore works.
- [x] `P0` Status columns render queued, printing, paused, failed, done, and cancelled correctly.
- [x] `P0` Starting/printing queue item can move linked customer job to in-progress.
- [x] `P0` Completing queue item can move linked customer job to completed.
- [x] `P1` Failure count, actual hours, scheduled start, started at, estimated finish, and completed at persist.
- [x] `P1` Printer queue items appear in job timeline.
- [x] `P1` AI review flags overdue or failed queue items.
- [x] `P2` Board layout remains usable with many cards.

## Customers, Vendors, and Relationship Views

- [x] `P0` Customer directory includes parties and name-only activity.
- [x] `P0` Vendor directory includes parties and vendor activity.
- [x] `P1` Customer detail links sales, AR, jobs, communications, invoice records, and proof docs.
- [x] `P1` Vendor detail links bills, expenses, assets, and proof docs.
- [x] `P1` Add Contact Details creates or updates the correct Party.
- [x] `P1` Relationship sections paginate and open the correct underlying row.
- [x] `P2` Duplicate names are handled predictably or clearly flagged.

## Document Intake / Audit Docs

- [x] `P0` Upload proof file creates an Audit Document only, not a Sale, Expense, Invoice, or AR row.
- [x] `P0` Suggested lane discloses local-rules engine and write boundary.
- [x] `P0` Uploaded file can be downloaded/viewed from `/api/audit-documents/{id}/file`.
- [x] `P1` Suggestions classify likely sale, expense, asset, invoice, estimate, bill, shipping, and review lanes.
- [x] `P1` Upload handles missing file, zero-byte file, unsupported extension, and oversized file.
- [x] `P1` Uploaded filenames with spaces, parentheses, and apostrophes work.
- [x] `P1` Opening "Edit Audit Doc" edits the created audit row.
- [x] `P2` File storage stays under the intended local upload directory.

## AI Estimate Intake

- [x] `P0` Text-only estimate draft produces line items and pricing review warnings.
- [x] `P0` Image upload creates review line item in local fallback mode.
- [x] `P0` PDF and DOCX source text is extracted into draft context.
- [x] `P0` Private/local URLs are blocked with an explicit warning.
- [x] `P1` Catalog/product matching works when a known product appears in the request.
- [x] `P1` Bulk quantity parsing works.
- [x] `P1` Draft can be pushed into invoice builder without saving automatically.
- [x] `P1` User can review and edit all draft fields before saving.
- [x] `P2` Very large text sources fail with a friendly size message.

## AI Operations

- [x] `P0` Reconciliation report runs locally and returns findings or clean state.
- [x] `P0` Action automation preview returns candidates without saving.
- [x] `P0` Sync findings creates action items once and is idempotent on repeat.
- [x] `P0` Marketplace order import parses paid Etsy/order text.
- [x] `P0` Marketplace order save creates Sale, optional Customer, optional completed Job, and linked proof docs.
- [x] `P0` Marketplace order save does not create AR or invoice documents for paid orders.
- [x] `P0` Duplicate marketplace order detection warns and avoids double-entry.
- [x] `P1` Marketplace order missing required date fails safely.
- [x] `P1` Product import returns a Product draft and listing copy without saving.
- [x] `P1` Job planner returns tasks and confirmed action creation is idempotent.
- [x] `P1` Slicer reader extracts material, grams, print hours, plates, quantity.
- [x] `P1` Listing writer creates title and description, but posts nothing automatically.
- [x] `P1` Ask Ledger returns relevant local rows with open-record actions.
- [x] `P2` Model-backed operations are clearly marked when they would send data to a provider.

## AI Review Center

- [x] `P0` `/api/ai/review/status` states whether data leaves the machine.
- [x] `P0` Review output identifies local-rules engine when running locally.
- [x] `P0` Review output is read-only unless user syncs action items.
- [x] `P1` Review flags due follow-ups, printer queue attention, orphaned ledger rows, invalid money, and tax review issues.
- [x] `P1` Open-record buttons route to the right record.
- [x] `P2` Review center handles empty ledger gracefully.

## Local AI Power

- [x] `P0` Local AI status page loads with service unavailable, available, running, and stopped states.
- [x] `P0` Settings save valid local model/provider settings.
- [x] `P0` Invalid local AI settings return 400 and do not corrupt saved settings.
- [x] `P1` Start and stop buttons behave safely when the local AI process is already running or not installed.
- [x] `P1` The page explains local/read-only boundaries accurately.

## Tax Prep

- [x] `P0` Tax profile loads and saves.
- [x] `P0` Tax calendar generation creates expected obligations for selected year.
- [x] `P0` Tax summary includes sales, expenses, COGS, mileage, sales tax memo, and estimated net.
- [x] `P0` Non-deductible and review-only rows are excluded where appropriate.
- [x] `P0` NJ sales tax export separates seller-collected from marketplace-collected tax.
- [x] `P1` Federal estimated tax, NJ sales tax, annual filing, and custom obligations display correctly.
- [x] `P1` Mileage logs affect mileage deduction calculations.
- [x] `P1` Tax year switch updates all panels and exports.
- [x] `P2` Tax prep has clear "needs review" indicators for incomplete rows.

## Import / Export

- [x] `P0` CSV export works for every entity.
- [x] `P0` Tax sales export works for all payment groups: all, noncash, digital, card, online, cash, other, unknown.
- [x] `P0` Tax summary export works.
- [x] `P0` NJ sales tax export works.
- [x] `P0` Tax package export returns a downloadable file.
- [x] `P0` Database backup endpoint returns a database file.
- [x] `P0` System backup endpoint returns a backup file.
- [x] `P0` Full database import stays disabled unless a deliberate restore workflow is built.
- [x] `P1` Exported CSVs open cleanly in Excel with expected headers.
- [x] `P1` Export includes archived rows only when requested.

## Admin / Config

- [x] `P0` Business config loads and saves: name, contact, brand color, calculator defaults.
- [x] `P0` Saving config updates invoice-builder defaults after reload.
- [x] `P0` Database clear remains disabled.
- [x] `P0` Database import remains disabled.
- [x] `P1` Admin page shows app/database information accurately.
- [x] `P1` Backup buttons work from the UI.
- [x] `P2` Invalid config values are rejected or normalized safely.

## Ledger Map, Workflow Guide, Help

- [x] `P1` Ledger Map renders each lane and explanation.
- [x] `P1` Ledger Map "ask ledger" examples run and open records correctly.
- [x] `P1` Workflow Guide explains normal order-to-cash and expense workflows without stale labels.
- [x] `P1` Help/Glossary opens and links to relevant app areas.
- [x] `P2` These pages are useful on mobile and do not require horizontal scrolling.

## Invoice / Estimate Creator: P0 Deep Checklist

### Shell and Navigation

- [x] `P0` Separate invoice-builder shell loads at `/invoice-builder/`.
- [x] `P0` Embedded invoice tool loads inside the main ledger shell.
- [x] `P0` Dashboard, Calculator, Builder, Records, Rate Card, and Settings tabs all open.
- [x] `P0` Switching between main app pages and embedded invoice tool preserves or intentionally resets state.
- [x] `P1` Keyboard shortcuts work: save and new estimate.
- [x] `P1` Active record bar always matches the record currently loaded.
- [x] `P1` Autosave indicator never claims saved after a failed save.

### New Document Identity

- [x] `P0` New Estimate assigns next `EST-YYYY-NNNN`.
- [x] `P0` New Invoice assigns next `INV-YYYY-NNNN`.
- [x] `P0` Next-number logic skips existing numbers and handles gaps.
- [x] `P0` Blank doc number on create gets a server-generated number.
- [x] `P0` Manual doc number with wrong prefix is rejected with 400.
- [x] `P0` Duplicate active doc number is rejected with 400.
- [x] `P0` Archived duplicate-number behavior is intentional and documented.
- [x] `P0` Opening record `A`, then opening record `B`, then saving cannot write to `A`.
- [x] `P0` Switching between records never swaps visible number/type with stored ID.
- [x] `P0` Type change on a saved estimate/invoice creates a new record or returns clear 400; it never mutates the original by ordinary save.

### Specific Regression: Estimate to Invoice + Paid

- [x] `P0` Open a saved estimate, change Type to Invoice, set Status Paid, enter Amount Paid, save.
- [x] `P0` Original estimate remains `ESTIMATE` with the original `EST-` number.
- [x] `P0` New invoice is created with a fresh `INV-` number.
- [x] `P0` UI toast clearly says a new invoice was created and original stayed unchanged.
- [x] `P0` No 500 appears.
- [x] `P0` No hidden duplicate error message is left behind under another toast.
- [x] `P0` Sales row and AR row sync only for the new invoice.
- [x] `P0` Reopen both records and confirm each has the right ID, type, number, status, and money.

### Status Rules

- [x] `P0` Estimate statuses are Draft, Sent, Accepted, Void.
- [x] `P0` Invoice statuses are Draft, Sent, Partial, Paid, Void.
- [x] `P0` Estimate amount paid is forced to zero or ignored.
- [x] `P0` Invoice amount paid can create Sent, Partial, Paid, and Void outcomes correctly.
- [x] `P0` Paid invoice balance is zero.
- [x] `P0` Partial invoice balance is total minus paid.
- [x] `P0` Void invoice amount paid is zero or excluded from active totals according to business rule.
- [x] `P0` Status dropdown updates immediately when Type changes.
- [x] `P1` Terms boilerplate changes only when it still matches the default for the previous type.

### Save Lifecycle

- [x] `P0` Save Draft creates a record when no active ID exists.
- [x] `P0` Save Draft updates the existing active record when type has not changed.
- [x] `P0` Save as New always creates a new record and leaves original unchanged.
- [x] `P0` Duplicate creates a new draft copy with a new number.
- [x] `P0` Duplicate from estimate stays estimate unless explicitly converting.
- [x] `P0` Duplicate from invoice stays invoice unless explicitly requested.
- [x] `P0` Archive hides record from normal Records list and active totals.
- [x] `P0` Restore returns archived record to Records list.
- [x] `P0` Delete/archive of active record clears active record identity.
- [x] `P0` Failed save does not update active record bar or show saved state.
- [x] `P0` Rapid double-click on save does not create duplicate records.
- [x] `P1` Save failure displays the backend message in a visible toast.
- [x] `P1` Latest document endpoint returns the most recently updated active record.

### Customer and Project Fields

- [x] `P0` Customer name, prepared-for, phone, email, and address save and reload.
- [x] `P0` Phone formats to `(555) 123-4567` and rejects incomplete values.
- [x] `P0` Email validation does not block blank optional email but flags malformed email.
- [x] `P1` Long addresses wrap in preview and PDF.
- [x] `P1` Product datalist fills from Products and can populate project/material/color where supported.
- [x] `P1` Internal notes never appear on customer PDF.

### Line Items

- [x] `P0` Add line item creates an editable row.
- [x] `P0` Remove line item updates totals and preview.
- [x] `P0` Quantity x rate calculates amount.
- [x] `P0` Negative quantity/rate is clamped or rejected.
- [x] `P0` Zero quantity/rate behavior is intentional.
- [x] `P0` Multi-line description/details save and reload.
- [x] `P0` Reordering or sort order persists if supported.
- [x] `P1` Twenty-plus line items paginate or flow in the PDF without overlap.
- [x] `P1` Very long description/details wrap without breaking PDF layout.

### Pricing and Money Rules

- [x] `P0` Subtotal equals sanitized line item total.
- [x] `P0` Discount cannot reduce taxable base below zero.
- [x] `P0` Rush percent creates the correct rush dollar amount.
- [x] `P0` Tax percent creates the correct tax dollar amount.
- [x] `P0` Total equals subtotal - discount + rush + tax.
- [x] `P0` Amount paid and balance normalize by type/status.
- [x] `P0` Calculator push-to-builder transfers grams, hours, design hours, setup, post-processing, rates, difficulty, tax, rush, discount, and line item.
- [x] `P0` Calculator minimum price is enforced.
- [x] `P1` Changing calculator settings changes future calculations, not old saved documents unexpectedly.
- [x] `P1` Rounding is consistent across builder summary, PDF preview, API response, Sales sync, AR sync, and exports.

### PDF Preview and Download

- [x] `P0` Live preview refreshes after field changes.
- [x] `P0` Preview button opens the current document output.
- [x] `P0` Download PDF creates printable output.
- [x] `P0` PDF includes correct doc type, doc number, dates, customer, project, line items, totals, terms, and turnaround.
- [x] `P0` PDF excludes internal notes and private import receipts.
- [x] `P0` Estimate PDF uses estimate wording and valid-until label.
- [x] `P0` Invoice PDF uses invoice wording and due-date/payment-status wording.
- [x] `P1` A4, Letter, and Legal page sizes render without clipped content.
- [x] `P1` Long terms and many line items do not overlap totals or footer.
- [x] `P1` Preview and downloaded output match.
- [x] `P2` Browser print dialog opens without script errors.

### Records View

- [x] `P0` Records list shows saved estimates, invoices, and AR-only rows distinctly.
- [x] `P0` Search by number, customer, and project works.
- [x] `P0` Type filter works.
- [x] `P0` Status filter works for estimate and invoice status sets.
- [x] `P0` Sort by updated, created, number, total, paid, customer, project works.
- [x] `P0` Pagination works at 10, 25, 50, and 100 rows.
- [x] `P0` Show Archived toggles archived rows.
- [x] `P0` AR-only rows open the AR ledger, not the builder.
- [x] `P0` Estimate row Create Invoice action creates linked invoice without overwriting estimate.
- [x] `P1` CSV export matches visible filtered/sorted records.
- [x] `P1` Footer totals match active invoice rows only.

### Convert Estimate to Invoice

- [x] `P0` Convert from Records creates an invoice with a new `INV-` number.
- [x] `P0` Source estimate remains intact.
- [x] `P0` Source estimate gets a conversion/link event.
- [x] `P0` Converted invoice starts with correct status and zero paid unless explicitly paid.
- [x] `P0` Converted invoice line items, totals, customer, project, and terms copy correctly.
- [x] `P0` Converting same estimate twice is either blocked or creates a clearly separate invoice with no hidden corruption.
- [x] `P1` Job/AR references from the estimate are updated or linked according to intended rules.

### Ledger Sync From Invoice Documents

- [x] `P0` Paid invoice creates or updates a Sale row.
- [x] `P0` Paid invoice creates or updates an AR row.
- [x] `P0` Partial invoice creates/updates AR and allocates paid amount correctly.
- [x] `P0` Draft estimate does not create Sale or AR.
- [x] `P0` Sent estimate does not create Sale or AR.
- [x] `P0` Accepted estimate can create/update Customer Job only if intended.
- [x] `P0` Void invoice hides or reverses synced Sale from dashboard.
- [x] `P0` Editing invoice number updates synced ledger references without orphaning old rows.
- [x] `P0` Archiving invoice document archives or hides synced rows according to intended audit rules.
- [x] `P1` Repeated saves are idempotent and do not create duplicate Sales/AR rows.

### AI Import PDF for Existing EPATA Format

- [x] `P0` AI Import PDF button opens file picker for PDF files.
- [x] `P0` Uploading an EPATA invoice PDF detects `INVOICE`.
- [x] `P0` Uploading an EPATA estimate PDF detects `ESTIMATE`.
- [x] `P0` Import maps doc number, dates, customer name, phone, email, address, prepared-for, project, material, color, infill, description, pricing guide, terms, tax rate, discount, rush, amount paid, status, and line items.
- [x] `P0` Import opens as an unsaved draft; it does not create or update a database record automatically.
- [x] `P0` Existing document number produces a duplicate warning before saving.
- [x] `P0` Saving an imported draft with existing doc number is blocked or requires choosing a new number.
- [x] `P0` Imported paid invoice saves as invoice and syncs to Sales/AR only after user saves.
- [x] `P0` Imported estimate saves as estimate and does not mark paid.
- [x] `P0` Scanned/image-only PDF returns a friendly OCR-needed message.
- [x] `P0` Wrong-format PDF returns a review warning and does not create bad records.
- [x] `P1` Multi-page PDF imports all visible text needed for fields.
- [x] `P1` PDF with missing email/phone/address still imports available fields.
- [x] `P1` PDF with extra notes does not put private import metadata on customer PDF.
- [x] `P1` Oversized PDF fails with a friendly size message.
- [x] `P2` Import handles old EPATA wording variations and minor spacing changes.

### Settings and Rate Card

- [x] `P0` Settings load and save business name, location, phone, email, website, social links, brand color, and calculator defaults.
- [x] `P0` Saved settings affect new documents and PDFs.
- [x] `P1` Rate Card copy buttons put the expected wording on clipboard.
- [x] `P1` Rate Card values match calculator defaults or clearly explain any difference.

## Security and Local Safety

- [x] `P0` No auth is required for local use, but app must bind only to intended local URL/port.
- [x] `P0` File upload paths cannot escape the intended local upload folder.
- [x] `P0` Download/file endpoints do not expose arbitrary local files.
- [x] `P0` Local/private URLs in AI intake are blocked.
- [x] `P0` Destructive database operations are disabled unless explicitly rebuilt with backup/restore flow.
- [x] `P1` No secrets or API keys are displayed in UI or exports.
- [x] `P1` HTML output escapes user-entered text to avoid script injection inside the local app.
- [x] `P1` CSV exports handle commas, quotes, newlines, and formula-like values safely.

## Performance and Reliability

- [x] `P1` App starts quickly with empty and medium databases.
- [x] `P1` Dashboard, records, and search stay responsive with at least 1,000 rows.
- [x] `P1` Invoice records sorting/filtering stays responsive with at least 500 invoices/estimates.
- [x] `P1` PDF preview refresh is debounced and does not lag badly while typing.
- [x] `P1` File upload of valid PDFs/DOCX/images completes within expected local time.
- [x] `P1` App handles SQLite locked-file scenarios gracefully with visible error.
- [x] `P2` OneDrive sync delays do not corrupt or duplicate records.

## Browser and Visual QA

- [x] `P1` Main site visual check at 1440x900.
- [x] `P1` Main site visual check at 1280x720.
- [x] `P1` Main site visual check at 390x844.
- [x] `P1` Invoice builder visual check at 1440x900.
- [x] `P1` Invoice builder visual check at 1280x720.
- [x] `P1` Invoice builder visual check at 390x844.
- [x] `P1` No primary button is clipped or hidden at common viewport sizes.
- [x] `P1` Tables remain usable on mobile or provide horizontal scroll intentionally.
- [x] `P1` Modals do not extend beyond viewport without scroll.
- [x] `P1` PDF preview frame renders nonblank.
- [x] `P2` Dark/light OS preference does not make text unreadable.

## PrintScore-Style Hardening Backlog

- [x] `P0` Run a browser route crawl across every sidebar page and invoice-builder tab, failing on console errors or failed network responses.
- [x] `P0` Run the full invoice lifecycle in browser automation: new estimate, save, reopen, convert, mark paid, confirm Sales/AR sync, archive, restore, export.
- [x] `P0` Run a browser failure-mode pass where saves return backend errors and the UI keeps the correct active record, visible toast, and unsaved state.
- [x] `P0` Run launch rehearsal from `run.ps1`, `run.bat`, Release DLL, and published output, verifying the health DB path before any write.
- [x] `P0` Run backup-and-copy rehearsal before any destructive manual/import/export/admin test against copied real data.
- [x] `P0` Run backup/restore drill using only copied/disposable database files and verify record counts, invoices, uploads, and settings after restart.
- [x] `P0` Run uploaded existing EPATA invoice and estimate PDFs through the AI-assisted import UI, review every mapped field, and save only after human confirmation.
- [x] `P0` Run no-real-database guard check for every automated script and document which database path each script uses.
- [x] `P1` Capture desktop/tablet/mobile screenshots for every major page and compare against accepted visual baselines.
- [x] `P1` Capture invoice PDF screenshots for invoice, estimate, paid invoice, partial invoice, long address, long terms, and 20-plus line-item cases.
- [x] `P1` Run accessibility keyboard pass: tab order, focus visibility, modal focus trap, Escape handling, and Enter/Space activation.
- [x] `P1` Run screen-reader label pass for primary buttons, icon-only buttons, filters, file inputs, invoice tabs, and modals.
- [x] `P1` Run browser history pass for dashboard, ledger pages, relationship pages, invoice records, and embedded invoice builder.
- [x] `P1` Run double-click/rapid-submit pass for create, save, archive, restore, upload, import, and AI action buttons.
- [x] `P1` Run offline/local-boundary pass: no external network dependency for normal ledger work, no accidental browser launch, no unexpected write folders.
- [x] `P1` Run large-data stress pass with at least 1,000 mixed ledger rows, 500 invoice/estimate records, and representative uploaded proof metadata.
- [x] `P1` Run repeated start/stop pass to catch orphan app processes, locked ports, stale DB connections, and restart persistence issues.
- [x] `P1` Run SQLite locked-file and OneDrive sync-delay scenarios with visible user-facing errors and no duplicate/corrupt records.
- [x] `P1` Run file-upload edge matrix through the real UI: missing file, zero-byte file, wrong extension, oversized file, duplicate filename, spaces, parentheses, apostrophes, and traversal-style names.
- [x] `P1` Run import/export round-trip review: CSV headers, Excel opening, formula guards, archived inclusion, tax package ZIP contents, and downloaded backup validity.
- [x] `P1` Run tax/export snapshot tests using known seeded data for gross receipts, customer paid, NJ sales tax, deductions, mileage, assets, and obligations.
- [x] `P1` Run AI Operations browser pass for marketplace order, product import, job planner, slicer reader, listing writer, Ask Ledger, and open-record actions.
- [x] `P1` Run security/local-safety scan for exposed secrets, arbitrary file access, path traversal, local/private URL blocks, and user-entered HTML escaping.
- [x] `P1` Run dependency/runtime health review, including known package advisories, framework version, publish output size, and startup warnings.
- [x] `P1` Run structured log/terminal review for build, smoke, acceptance, browser automation, launch scripts, and failed-save scenarios.
- [x] `P2` Run dark/light OS preference screenshots and check contrast, charts, badges, table stripes, and invoice PDF preview readability.
- [x] `P2` Run slow-machine simulation by adding artificial API latency and verifying loading states, debounced search, and disabled duplicate-submit buttons.
- [x] `P2` Run print workflow rehearsal from browser preview to PDF/download for Letter, A4, and Legal page sizes.
- [x] `P2` Run final release rehearsal from a clean disposable database, a seeded medium database, and a copied real-data database with read/write actions restricted to the copy.

## Suggested Automated Test Backlog

- [x] `P0` Add integration tests around saved estimate type-change to invoice/paid returning 400 and preserving original.
- [x] `P0` Add integration tests around UI-style "type changed means create new invoice" behavior if browser tests are added.
- [x] `P0` Add invoice document ledger sync idempotency tests.
- [x] `P0` Add PDF import tests for invoice, estimate, duplicate number, scanned/blank PDF, wrong-format PDF, and oversized PDF.
- [x] `P0` Add tests that invoice save failure never creates Sales/AR side effects.
- [x] `P1` Add WebApplicationFactory tests for every endpoint group with disposable SQLite.
- [x] `P1` Add Playwright smoke tests for sidebar navigation and all primary modals.
- [x] `P1` Add Playwright invoice-builder workflow tests: new estimate, save, open, convert, paid invoice, archive, restore.
- [x] `P1` Add export snapshot tests for CSV headers and representative rows.
- [x] `P1` Add tax summary snapshot tests using known seeded data.
- [x] `P1` Add relationship-view tests for customer/vendor details.
- [x] `P2` Add visual regression screenshots for dashboard, invoice builder, records, tax prep, and document intake.

## Manual Release Checklist

- [x] `P0` Build passes.
- [x] `P0` Acceptance suite passes under PowerShell 7.
- [x] `P0` Start app with disposable DB and verify `/api/health`.
- [x] `P0` Create estimate, save, reopen.
- [x] `P0` Convert estimate to invoice using Records action.
- [x] `P0` Create paid invoice and confirm Sales/AR sync.
- [x] `P0` Reproduce the old failure path: change saved estimate to invoice, mark paid, save; confirm no 500 and no original corruption.
- [x] `P0` Upload an existing EPATA invoice PDF and save only after review.
- [x] `P0` Upload an existing EPATA estimate PDF and save only after review.
- [x] `P0` Export database backup.
- [x] `P0` Export CSV/tax files.
- [x] `P1` Open dashboard, tax prep, AI review, document intake, printer queue, relationship pages, and admin.
- [x] `P1` Restart app and confirm created test records persist in disposable DB.
- [x] `P1` Remove disposable DB and temporary uploaded proof files.
