# AI, Local Rules, and Automation Guide

This app uses three different kinds of assistance. The UI labels them so you can tell what touched a result and why.

## Indicator key

Every assisted result displays a colored source marker:

- **AI MODEL** means a loaded LM Studio language model interpreted selected content.
- **LOCAL RULES** means fixed, repeatable code and patterns produced the result. This is the automatic fallback whenever Local AI is off, unavailable, or an estimate-model call fails.
- **AUTOMATION** means a fixed workflow synchronized or created records after an explicit action such as saving or uploading.

The source marker describes the result actually returned, not merely the engine that was available when the screen opened.

Every AI Estimate Intake result also includes an **Execution Receipt**. A completed model run records the provider, model name, response ID when supplied by the provider, UTC execution time, and token usage when supplied. A Local Rules receipt explicitly says that no language model ran. The receipt is also copied into the estimate's Project Notes so it remains visible after the draft is filled and saved.

## Local AI model

The preferred model connection is LM Studio running on this computer.

- Open **Local AI Power** to choose a downloaded GGUF model, save settings, and start or stop the LM Studio server.
- The header always shows **Local AI Off**, **Local AI On**, or **Local AI Ready**.
- The app accepts only loopback HTTP origins such as `http://127.0.0.1:1234`. It will not connect the local-AI controls to another computer or a public AI endpoint.
- Start launches only the installed LM Studio application and its known `lms.exe` command. It cannot run arbitrary commands.
- Stop unloads models and stops the LM Studio local server.
- The default idle timeout unloads the selected model after 30 minutes without use.
- No API token is needed for LM Studio.
- Hosted-provider fallback is optional and runs only when `Ai:AllowHostedFallback` is `true`, a hosted endpoint/model is configured, and the configured API-key environment variable is present. Provider billing is controlled by the API account, not by this local ledger.

## AI model

An AI model interprets unstructured content. Data is sent only when an AI endpoint and model are configured.

### AI Estimate Intake

- **Use it when:** a customer request is spread across text messages, an email chain, public product/source URLs, PDF or DOCX documents, pictures, or pasted notes.
- **Reads:** only the sources you add to AI Estimate Intake, `AiEstimateInstructions.json`, the current field IDs in `wwwroot/invoice-builder/index.html`, and active Products / Costing rows.
- **Does:** returns the complete HTML builder field contract, calculator inputs, customer/project fields, terms, turnaround, and estimate line items.
- **Saved-product pricing:** when a request matches a saved product name or SKU, its material, color, grams, material rate, print hours, machine rate, packaging cost, design time, and target-price floor become the preferred cost basis.
- **Money safety:** the model proposes cost inputs such as grams and print time. The app deterministically recomputes material, machine, design, setup, post-processing, difficulty, minimum, rush, discount, tax, and total before filling the builder.
- **Granular quotes:** complex jobs retain customer-facing line items for phases such as artwork, prototype, production setup, production run, and cleanup. Calculator grams, hours, and rates remain visible as the underlying cost basis.
- **Missing information:** a missing customer name never blocks estimating. When slicer data is unavailable, the model creates a review-first planning estimate from clearly disclosed assumptions instead of automatically collapsing the job to the minimum.
- **Context safety:** model output is capped so a verbose response cannot consume the loaded model's entire context window. If the model still fails or returns invalid structured data, the result is visibly replaced by Local Rules and includes the failure warning.
- **Structured-answer mode:** estimate generation requests concise structured output with extended reasoning disabled so the local model spends its context on the usable estimate instead of exhausting it before returning JSON.
- **Writes:** nothing automatically. It opens an unsaved estimate draft after you choose to continue.
- **Saved provenance:** the estimate Project Notes say which engine prepared the draft, the exact pricing basis, the app-calculated total, and that review is required.
- **Required review:** customer details, quantities, prices, taxes, terms, and pictured items.

### AI Operations

Open **AI Operations** for the product, production, cleanup, writing, and ledger-question tools below. Every result displays a receipt with:

- the actual engine used: **AI MODEL** or **LOCAL RULES**
- what the tool read
- what the tool can write
- when to use it
- its safety boundary

#### Duplicate & Reconciliation Check

- **Runs first by design:** the default report uses deterministic Local Rules, not a language model.
- **Reads:** active Sales, Expenses, Products, Parties, Customer Jobs, AR invoices, and estimate/invoice documents.
- **Finds:** repeated order/invoice/SKU identifiers, suspicious same-date/same-amount records, duplicate contacts/products, and disagreements between Invoice documents, AR, and paid Sales.
- **Optional AI:** **Explain Findings with Local AI** sends only the deterministic findings to LM Studio for a plain-language cleanup order.
- **Writes:** nothing. It never merges, archives, deletes, or edits records.
- **Optional task sync:** **Sync Verified Findings to Actions** creates only missing Action Items from the deterministic findings after a separate confirmation. The AI explanation is never converted into tasks.

#### Paid Etsy / Marketplace Order Import

- **Use it when:** an Etsy or other marketplace order is already paid and complete.
- **Reads:** only the order PDF, screenshot, document, or pasted order/payment-statement text you explicitly add.
- **Does:** prepares a reviewable paid Sale, reusable customer business card, plus an optional completed/paid Customer Job and checks marketplace + order number for duplicates.
- **Customer contact:** extracts the ship-to name, email, phone when supplied, address, country, and marketplace username. Confirmed save creates a customer contact or fills only missing fields on a matching saved contact.
- **One order at a time:** if an upload contains more than one order number, saving is blocked so dates, customers, amounts, and proof cannot be mixed together.
- **Required date:** the marketplace order date becomes the Sale Date and completed Job date. Saving is blocked until a Sale Date is present.
- **Explicit save:** **Save Paid Sale + Link Proof** creates the Sale, customer contact, optional Job, and linked Audit Docs together.
- **Writes:** nothing until explicit confirmation. It never creates an estimate, invoice, or AR row.
- **Important:** missing marketplace fees, shipping-label cost, and COGS remain review warnings instead of being invented.

#### Product Importer

- **Use it when:** you already have a MakerWorld, Etsy, or other public product page, product documents, pasted notes, or pictures.
- **Reads:** only the public HTTPS URLs, text, documents, and pictures you explicitly add.
- **Does:** extracts a reviewable Product / Costing draft and listing-copy preview, checks for possible existing Products, and preserves source references in notes.
- **Writes:** nothing automatically. Click **Open Unsaved Product Draft**, review it, then click Save in Products / Costing.
- **Important:** external pages do not know your internal grams, print hours, packaging, design time, or target price. Unknown costs remain review items.

#### Job Planner

- **Use it when:** a quote is accepted or before production starts.
- **Reads:** a selected Customer Job and/or pasted job description.
- **Does:** drafts requirement, design, slicing, prototype, approval, production, QC, packaging/delivery, invoice, and payment-follow-up tasks.
- **Writes:** only after you explicitly click and confirm **Create These Action Items**.
- **Duplicate protection:** confirming the same plan again skips matching open generated tasks.

#### Slicer Report & Screenshot Reader

- **Use it when:** you have slicer text, a report, PDF/DOCX, or screenshot.
- **Reads:** only slicer sources you explicitly add.
- **Does:** drafts material, color, grams, print time, filament length, plate count, quantity, material cost, and a Product / Costing prefill.
- **Writes:** nothing automatically. Verify whether values are total or per-unit before saving.
- **Picture limitation:** screenshots need a vision-capable loaded model. Local Rules can parse text but cannot understand screenshot pixels.

#### Product Listing Writer

- **Use it when:** creating or refreshing MakerWorld, Etsy, website, or general listing copy.
- **Reads:** a selected Product / Costing row and optional instructions.
- **Does:** drafts title, description, highlights, tags, FAQ, and personalization instructions.
- **Writes:** nothing and never posts to a marketplace.

#### Ask the Ledger

- **Use it when:** you want a plain-language answer about records already entered in the app.
- **Reads:** only active local rows selected by deterministic search for your question.
- **Does:** returns an answer plus links to the exact matching records.
- **Writes:** nothing. It is read-only.

## Local rules

Local rules stay on this computer and use deterministic patterns. They are not a language model.

### AI Estimate Intake fallback

- Used when no AI endpoint/model is configured or a configured call fails.
- Extracts obvious names, phone numbers, materials, quantities, prices, public webpage metadata, readable PDF/DOCX text, and file names.
- Pictures become clearly marked review items because local rules cannot understand image content.
- Scanned/image-only PDFs need OCR or a configured vision-capable AI model; readable embedded PDF text is extracted locally.

### AI Estimate Intake limits

- Up to 25 uploaded files, 20 MB per file, and 75 MB total per analysis.
- Up to 20 public HTTPS product/source URLs. Local and private-network URLs are blocked.
- Only URLs deliberately entered in the Source URLs field are fetched. Links embedded inside uploaded email chains, DOCX files, or PDFs remain source evidence and are not followed automatically.
- Combined pasted and extracted text is limited to 500,000 characters; each document contributes up to 150,000 extracted characters.
- These limits protect memory and AI model context. The intake screen displays them before analysis.

### AI Review Center

- **Use it when:** starting bookkeeping cleanup, customer follow-up, pricing review, or tax preparation.
- **Reads:** active ledger rows, review flags, proof links, payment status, and product costing.
- **Does:** prioritizes possible cleanup work and explains why each item was flagged.
- **Writes:** nothing. It is read-only and routes you to the relevant ledger.
- **Optional model action:** clicking **Explain with Local AI** sends only the already-generated deterministic findings to the loaded LM Studio model for a summary, recommended order, and follow-up questions.
- **Model safety:** the deterministic findings remain authoritative; the model cannot modify records or determine tax treatment.
- **Optional task sync:** **Sync Verified Findings to Actions** groups the deterministic findings into deduplicated follow-up tasks after a separate confirmation. It skips the recursive "open Action Items" finding.

### Action Control Center

- **Use it when:** you want one place to see why a task exists and turn verified problems into follow-up work.
- **Sources:** manual entries, explicitly confirmed Job Planner tasks, and explicitly synced Review Center/reconciliation findings.
- **Preview:** shows grouped findings, calculated-cost/balance checks, duplicate comparisons, and whether a matching generated task is already open.
- **Labels:** generated task notes preserve the automation source, assistance source, stable automation key, reason, next step, and evidence at creation.
- **Safety:** scans remain read-only. The local AI model's prose never becomes a task, and repeated syncs do not duplicate open generated tasks.

### Document Intake suggestions

- **Use it when:** a proof file arrives before you know where it belongs.
- **Reads:** the uploaded file name, the related-area fields you entered, and extracted text preview when available.
- **Does:** suggests the likely ledger and prefill values.
- **Writes:** only the Audit Doc created by the upload. It does not post the suggested Sale, Expense, Bill, or other record.

### Tax Prep calculation audit

- Checks entered totals and relationships with deterministic accounting consistency rules.
- It is not AI and is not tax advice.

## Automation

Automation runs after an explicit save or payment action.

- Saving estimates/invoices synchronizes their related Customer Job or AR record.
- Recording invoice payment can create or update the matching Direct Sale.
- Uploading proof creates an Audit Doc.
- Confirming a Job Planner or verified-finding sync can create deduplicated Action Items.
- Automation does not mean AI; it follows fixed application rules.

## Editable AI pricing instructions

`AiEstimateInstructions.json` is reread for each estimate analysis. Edit it to change default material, tax rate, minimum order, fees, and keyword-based prices.

`AiOperationsInstructions.json` is reread for AI Operations. Edit it to change product-import rules, default costing assumptions, job-plan stages, brand name, and listing-writing rules.

## Safety rule

AI and local-rule recommendations prepare drafts and review lists. They do not silently save, send, post, file, or delete ledger records. Local AI runs only after you start it, and model-assisted Review Center analysis runs only after you explicitly click it. Action Items from Job Planner or verified findings require a separate explicit confirmation.
