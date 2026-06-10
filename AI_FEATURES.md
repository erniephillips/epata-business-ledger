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
- Hosted-provider fallback is disabled by default. It runs only if `Ai:AllowHostedFallback` is deliberately changed to `true` and a hosted endpoint/model is configured.

## AI model

An AI model interprets unstructured content. Data is sent only when an AI endpoint and model are configured.

### AI Estimate Intake

- **Use it when:** a customer request is spread across text messages, an email chain, public product/source URLs, PDF or DOCX documents, pictures, or pasted notes.
- **Reads:** only the sources you add to AI Estimate Intake, `AiEstimateInstructions.json`, the current field IDs in `wwwroot/invoice-builder/index.html`, and active Products / Costing rows.
- **Does:** returns the complete HTML builder field contract, calculator inputs, customer/project fields, terms, turnaround, and estimate line items.
- **Saved-product pricing:** when a request matches a saved product name or SKU, its material, color, grams, material rate, print hours, machine rate, packaging cost, design time, and target-price floor become the preferred cost basis.
- **Money safety:** the model proposes cost inputs such as grams and print time. The app deterministically recomputes material, machine, design, setup, post-processing, difficulty, minimum, rush, discount, tax, and total before filling the builder.
- **Context safety:** model output is capped so a verbose response cannot consume the loaded model's entire context window. If the model still fails or returns invalid structured data, the result is visibly replaced by Local Rules and includes the failure warning.
- **Writes:** nothing automatically. It opens an unsaved estimate draft after you choose to continue.
- **Saved provenance:** the estimate Project Notes say which engine prepared the draft, the exact pricing basis, the app-calculated total, and that review is required.
- **Required review:** customer details, quantities, prices, taxes, terms, and pictured items.

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
- Combined pasted and extracted text is limited to 500,000 characters; each document contributes up to 150,000 extracted characters.
- These limits protect memory and AI model context. The intake screen displays them before analysis.

### AI Review Center

- **Use it when:** starting bookkeeping cleanup, customer follow-up, pricing review, or tax preparation.
- **Reads:** active ledger rows, review flags, proof links, payment status, and product costing.
- **Does:** prioritizes possible cleanup work and explains why each item was flagged.
- **Writes:** nothing. It is read-only and routes you to the relevant ledger.
- **Optional model action:** clicking **Explain with Local AI** sends only the already-generated deterministic findings to the loaded LM Studio model for a summary, recommended order, and follow-up questions.
- **Model safety:** the deterministic findings remain authoritative; the model cannot modify records or determine tax treatment.

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
- Automation does not mean AI; it follows fixed application rules.

## Editable AI pricing instructions

`AiEstimateInstructions.json` is reread for each estimate analysis. Edit it to change default material, tax rate, minimum order, fees, and keyword-based prices.

## Safety rule

AI and local-rule recommendations prepare drafts and review lists. They do not silently save, send, post, file, or delete ledger records. Local AI runs only after you start it, and model-assisted Review Center analysis runs only after you explicitly click it.
