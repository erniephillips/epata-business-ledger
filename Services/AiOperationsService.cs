using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.RegularExpressions;
using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Services;

public sealed class AiOperationsService(
    AppDbContext db,
    LocalAiService localAi,
    HttpClient httpClient,
    IWebHostEnvironment environment)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private string InstructionsPath => Path.Combine(environment.ContentRootPath, "AiOperationsInstructions.json");

    public async Task<object> StatusAsync(CancellationToken cancellationToken = default)
    {
        var status = await localAi.GetStatusAsync(false, cancellationToken);
        return new
        {
            localAiReady = status.ModelReady,
            provider = status.ModelReady ? $"LM Studio local / {status.ModelIdentifier}" : "Local rules fallback",
            instructionsPath = InstructionsPath,
            features = new[]
            {
                Feature("Duplicate & Reconciliation Check", "Active ledger records and invoice documents", "Read-only findings; never merges, archives, or deletes.", "Before cleanup, imports, tax prep, or when totals look wrong."),
                Feature("Paid Marketplace Order Import", "Etsy or marketplace order PDFs, screenshots, documents, and pasted order text", "Reviewable paid Sale and optional completed Customer Job; proof is saved only after explicit confirmation.", "When an Etsy or other marketplace order is already paid and complete."),
                Feature("Product Importer", "Public product URLs, pasted notes, documents, and pictures you add", "Reviewable Product draft only.", "To turn MakerWorld, Etsy, or any public product page into a Product / Costing draft."),
                Feature("Job Planner", "A selected Customer Job or pasted job description", "Reviewable task plan; Action Items only after an explicit Create button.", "After a quote is accepted or before production starts."),
                Feature("Slicer Reader", "Slicer text, reports, screenshots, and files you add", "Reviewable slicer values and Product draft only.", "To fill grams, print time, material, plates, and cost assumptions."),
                Feature("Listing Writer", "A selected Product / Costing row", "Listing copy preview only.", "When creating or refreshing MakerWorld, Etsy, or general product copy."),
                Feature("Ask the Ledger", "Active local ledger rows matching your question", "Read-only answer and links.", "When you want plain-language answers about records already in the app.")
            },
            safety = "Every feature is review-first. Local AI runs only after an explicit action; no feature silently saves, merges, archives, deletes, sends, or posts records."
        };
    }

    public async Task<AiReconciliationReport> BuildReconciliationAsync(CancellationToken cancellationToken = default)
    {
        var report = new AiReconciliationReport
        {
            Receipt = Receipt(
                false,
                "Local rules / deterministic reconciliation",
                ["Active Sales, Expenses, Products, Parties, Customer Jobs, AR invoices, and estimate/invoice documents"],
                [],
                "Before imports, bookkeeping cleanup, tax prep, or whenever totals and record counts look suspicious.",
                "Read-only. Findings never merge, archive, delete, or edit records.")
        };

        var sales = await db.Sales.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var expenses = await db.Expenses.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var products = await db.Products.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var parties = await db.Parties.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var jobs = await db.CustomerJobs.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var ar = await db.ReceivableInvoices.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var docs = await db.InvoiceDocuments.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken);

        AddDuplicateGroups(report, sales.Where(x => !string.IsNullOrWhiteSpace(x.OrderNumber)).GroupBy(x => Key(x.Platform, x.OrderNumber)),
            "Exact duplicate", "Sales share the same platform and order number", "An order should normally appear once in Sales.", "Compare proof and archive only the duplicate after confirming it.",
            x => Ref("Sale", x.Id, $"{x.Platform} order {x.OrderNumber}: {x.CustomerName}", "sales"));
        AddDuplicateGroups(report, sales.Where(x => !string.IsNullOrWhiteSpace(x.InvoiceNumber)).GroupBy(x => Key(x.InvoiceNumber)),
            "Exact duplicate", "Sales share the same invoice number", "Multiple Sales rows tied to one direct invoice can double-count income.", "Compare amounts and payment proof before archiving anything.",
            x => Ref("Sale", x.Id, $"{x.InvoiceNumber}: {x.CustomerName} {MoneyRules.SaleGrossReceipts(x):C}", "sales"));
        AddDuplicateGroups(report, ar.Where(x => !string.IsNullOrWhiteSpace(x.InvoiceNumber)).GroupBy(x => Key(x.InvoiceNumber)),
            "Exact duplicate", "AR rows share the same invoice number", "Duplicate receivable rows can overstate what customers owe.", "Confirm the valid AR row and archive only the duplicate.",
            x => Ref("AR Invoice", x.Id, $"{x.InvoiceNumber}: {x.CustomerName} {x.InvoiceTotal:C}", "receivables"));
        AddDuplicateGroups(report, products.Where(x => !string.IsNullOrWhiteSpace(x.Sku)).GroupBy(x => Key(x.Sku)),
            "Exact duplicate", "Products share the same SKU", "One SKU should normally identify one reusable costing record.", "Compare costing assumptions and consolidate manually.",
            x => Ref("Product", x.Id, $"{x.Name} ({x.Sku})", "products"));
        AddDuplicateGroups(report, jobs.Where(x => !string.IsNullOrWhiteSpace(x.RelatedOrderNumber)).GroupBy(x => Key(x.Platform, x.RelatedOrderNumber)),
            "Exact duplicate", "Customer Jobs share the same order number", "The same marketplace order may be represented by multiple jobs.", "Confirm whether these are separate production jobs or duplicates.",
            x => Ref("Customer Job", x.Id, $"{x.RelatedOrderNumber}: {x.JobName}", "customerJobs"));

        AddDuplicateGroups(report, expenses.GroupBy(x => Key(DateKey(x.ExpenseDate), x.VendorName, x.Description, MoneyKey(x.Total))),
            "Possible duplicate", "Expenses have the same date, vendor, description, and total", "Repeated expense imports or receipt entry can double-count deductions.", "Compare receipt proof and payment account before archiving anything.",
            x => Ref("Expense", x.Id, $"{DateKey(x.ExpenseDate)} {x.VendorName}: {x.Description} {x.Total:C}", "expenses"));
        AddDuplicateGroups(report, products.GroupBy(x => Key(x.Name)),
            "Possible duplicate", "Products have the same normalized name", "Separate costing rows with the same name can cause inconsistent estimate pricing.", "Compare SKU, material, variation, and costing before consolidating.",
            x => Ref("Product", x.Id, $"{x.Name} ({x.Sku ?? "no SKU"})", "products"));
        AddDuplicateGroups(report, parties.GroupBy(x => Key(x.Name, x.Email, Digits(x.Phone))),
            "Possible duplicate", "People/vendors share the same name and contact details", "Duplicate party rows split history across multiple records.", "Compare linked records and consolidate manually.",
            x => Ref("Party", x.Id, $"{x.Name}: {x.Email ?? x.Phone ?? "no contact"}", "customers"));
        AddDuplicateGroups(report, sales.GroupBy(x => Key(DateKey(x.SaleDate), x.CustomerName, x.ProductName, MoneyKey(x.CustomerPaid))),
            "Possible duplicate", "Sales have the same date, customer, product, and paid amount", "This pattern can indicate the same sale was entered twice.", "Compare order/invoice numbers and proof before archiving anything.",
            x => Ref("Sale", x.Id, $"{DateKey(x.SaleDate)} {x.CustomerName}: {x.ProductName} {x.CustomerPaid:C}", "sales"));

        foreach (var invoice in ar.Where(x => !string.IsNullOrWhiteSpace(x.InvoiceNumber)))
        {
            var document = docs.FirstOrDefault(x => x.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase)
                && Same(x.DocNumber, invoice.InvoiceNumber));
            if (document is null) continue;
            var mismatches = new List<string>();
            if (!Same(document.CustomerName, invoice.CustomerName)) mismatches.Add("customer name");
            if (Math.Abs(document.Total - (invoice.InvoiceTotal ?? 0)) > 0.01m) mismatches.Add("invoice total");
            if (Math.Abs(document.AmountPaid - (invoice.AmountPaid ?? 0)) > 0.01m) mismatches.Add("amount paid");
            if (mismatches.Count == 0) continue;
            report.Findings.Add(new(
                "High",
                "Reconciliation",
                $"Invoice document and AR disagree: {invoice.InvoiceNumber}",
                $"The linked records disagree on {string.Join(", ", mismatches)}.",
                "Open both records, compare the customer-facing invoice and payment proof, then correct the wrong ledger value.",
                [
                    Ref("AR Invoice", invoice.Id, $"{invoice.InvoiceNumber}: {invoice.CustomerName} {invoice.InvoiceTotal:C}", "receivables"),
                    Ref("Invoice Document", document.Id, $"{document.DocNumber}: {document.CustomerName} {document.Total:C}", "invoiceRecords")
                ]));
        }

        foreach (var sale in sales.Where(x => !string.IsNullOrWhiteSpace(x.InvoiceNumber)))
        {
            var invoice = ar.FirstOrDefault(x => Same(x.InvoiceNumber, sale.InvoiceNumber));
            if (invoice is null) continue;
            var salePaid = MoneyRules.SaleGrossReceipts(sale);
            var invoicePaid = invoice.AmountPaid ?? 0;
            if (Math.Abs(salePaid - invoicePaid) <= 0.01m && Same(sale.CustomerName, invoice.CustomerName)) continue;
            report.Findings.Add(new(
                "High",
                "Reconciliation",
                $"Paid Sale and AR disagree: {sale.InvoiceNumber}",
                $"Sale gross receipts are {salePaid:C}; AR amount paid is {invoicePaid:C}. Customer names may also differ.",
                "Compare payment proof and correct the wrong record so revenue and open AR are not misstated.",
                [
                    Ref("Sale", sale.Id, $"{sale.InvoiceNumber}: {sale.CustomerName} {salePaid:C}", "sales"),
                    Ref("AR Invoice", invoice.Id, $"{invoice.InvoiceNumber}: {invoice.CustomerName} paid {invoicePaid:C}", "receivables")
                ]));
        }

        report.Findings = report.Findings
            .OrderBy(x => x.Severity == "High" ? 0 : x.Severity == "Normal" ? 1 : 2)
            .ThenBy(x => x.Title)
            .Take(100)
            .ToList();
        report.ExactDuplicateGroups = report.Findings.Count(x => x.Kind == "Exact duplicate");
        report.PossibleDuplicateGroups = report.Findings.Count(x => x.Kind == "Possible duplicate");
        report.ReconciliationIssues = report.Findings.Count(x => x.Kind == "Reconciliation");
        return report;
    }

    public async Task<AiReconciliationReport> ExplainReconciliationAsync(CancellationToken cancellationToken = default)
    {
        var report = await BuildReconciliationAsync(cancellationToken);
        var narrative = await localAi.CompleteJsonAsync<AiReconciliationNarrative>(
            """
            You explain a deterministic duplicate and reconciliation report for a small business.
            The supplied findings are authoritative. Do not invent records and never recommend deleting or merging without comparing proof.
            Return JSON: {"summary":"","recommendedOrder":[],"questions":[]}.
            """,
            JsonSerializer.Serialize(report.Findings, JsonOptions),
            cancellationToken);
        var connection = await localAi.GetReadyConnectionAsync(cancellationToken);
        report.Receipt = Receipt(true, connection?.Provider ?? "LM Studio local model",
            ["Deterministic duplicate and reconciliation findings"],
            [],
            "After the local duplicate check, when you want a plain-language cleanup order.",
            "Read-only explanation. The model cannot merge, archive, delete, or edit records.");
        report.ModelSummary = narrative.Summary;
        report.ModelRecommendedOrder = narrative.RecommendedOrder ?? [];
        report.ModelQuestions = narrative.Questions ?? [];
        return report;
    }

    public async Task<AiMarketplaceOrderImportResult> BuildMarketplaceOrderDraftAsync(AiOperationSourcePacket packet, CancellationToken cancellationToken = default)
    {
        var source = string.Join("\n\n", new[]
        {
            packet.SourceText,
            string.Join("\n", packet.Images.Select(x => $"UPLOADED ORDER IMAGE: {x.FileName}"))
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (source.Length < 3 && packet.Images.Count == 0)
        {
            throw new InvalidOperationException("Paste marketplace order text or upload an Etsy/marketplace order document or screenshot.");
        }

        var result = BuildLocalMarketplaceOrder(source, packet);
        result.DetectedOrderNumbers = DetectMarketplaceOrderNumbers(source);
        if (result.DetectedOrderNumbers.Count > 1)
        {
            result.Warnings.Add($"Multiple marketplace orders were detected in this batch ({string.Join(", ", result.DetectedOrderNumbers)}). Analyze and save one order at a time.");
        }
        var connection = await localAi.GetReadyConnectionAsync(cancellationToken);
        if (connection is not null)
        {
            try
            {
                const string prompt = """
                    Extract one already-paid marketplace order into a reviewable Sale and completed Customer Job draft.
                    This is usually Etsy, but it may be another marketplace. Do not create an estimate, invoice, or AR record.
                    Use only supplied facts. Do not invent fees, shipping-label cost, COGS, tracking, SKU, dates, tax, or customer information.
                    Keep unknown numeric values at 0 and add a question or warning.
                    ItemSales excludes shipping and sales tax. CustomerPaid is the total the customer paid including shipping and tax.
                    Platform fees and shipping label cost come from seller/payment statements and may be absent from customer order receipts.
                    Capture the ship-to contact as Customer. Capture email, phone, address, country, and marketplace username only when explicitly supplied.
                    Use an empty string for unknown dates and text fields. Dates must use YYYY-MM-DD.
                    Return JSON:
                    {"sale":{"saleDate":"","platform":"Etsy","paymentMethod":"Etsy Payments","salesTaxHandling":"Marketplace collected/remitted - verify","orderNumber":"","customerName":"","productName":"","sku":"","variation":"","color":"","quantity":1,"itemSales":0,"shippingCharged":0,"salesTaxCollected":0,"customerPaid":0,"platformFees":0,"shippingLabelCost":0,"refunds":0,"estimatedCogs":0,"trackingNumber":"","shipByDate":"","notes":""},"job":{"material":"","color":"","description":"","shipByDate":"","notes":""},"customer":{"name":"","email":"","phone":"","address1":"","address2":"","city":"","state":"","postalCode":"","country":"","etsyUsername":"","notes":""},"questions":[],"warnings":[]}.
                    """;
                var model = packet.Images.Count > 0
                    ? await localAi.CompleteJsonWithImagesAsync<AiMarketplaceOrderModelDraft>(prompt, Trim(source, 32_000), packet.Images, cancellationToken)
                    : await localAi.CompleteJsonAsync<AiMarketplaceOrderModelDraft>(prompt, Trim(source, 32_000), cancellationToken);
                result.Sale = MergeMarketplaceSale(MarketplaceSaleFromModel(model.Sale, packet), result.Sale);
                result.Job = MarketplaceJobFromModel(result.Sale, model.Job);
                result.Customer = MergeMarketplaceCustomer(MarketplaceCustomerFromModel(model.Customer, result.Sale.Platform), result.Customer);
                result.Questions = model.Questions ?? [];
                result.Warnings.AddRange(model.Warnings ?? []);
                result.Receipt = Receipt(true, connection.Provider,
                    ["Only the marketplace order text, documents, and screenshots added here"],
                    ["Reviewable paid Sale, customer contact, and optional completed Customer Job; uploaded proof only after explicit Save"],
                    "When an Etsy or marketplace order is already paid and should be recorded without an estimate, invoice, or AR row.",
                    "Review-first. Duplicate order numbers block saving. It never creates an estimate, invoice, or AR record.");
            }
            catch (Exception ex)
            {
                result.Warnings.Insert(0, $"Local AI could not structure the marketplace order, so local extraction rules were used: {ex.Message}");
            }
        }

        NormalizeMarketplaceOrder(result, packet);
        result.PossibleDuplicateSales = await FindMarketplaceSaleDuplicatesAsync(result.Sale, cancellationToken);
        result.PossibleDuplicateJobs = await FindMarketplaceJobDuplicatesAsync(result.Sale, cancellationToken);
        result.PossibleCustomerMatches = await FindMarketplaceCustomerMatchesAsync(result.Customer, cancellationToken);
        result.SuggestedCreateJob = result.PossibleDuplicateJobs.Count == 0;
        if (result.PossibleDuplicateSales.Count > 0)
        {
            result.Warnings.Insert(0, "This marketplace order may already exist in Sales. Saving is blocked until the existing order is reviewed.");
        }
        if (result.PossibleDuplicateJobs.Count > 0)
        {
            result.Warnings.Add("A matching Customer Job already exists, so creating another Job is not recommended.");
        }
        if (result.PossibleCustomerMatches.Count > 0)
        {
            result.Warnings.Add("A matching saved customer contact was found. Confirmed save fills only its missing contact fields.");
        }
        return result;
    }

    public async Task<AiMarketplaceOrderSaveResult> SaveMarketplaceOrderAsync(AiMarketplaceOrderSaveRequest request, CancellationToken cancellationToken = default)
    {
        var detectedOrders = (request.DetectedOrderNumbers ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (detectedOrders.Count > 1)
        {
            throw new InvalidOperationException($"This upload contains multiple marketplace orders ({string.Join(", ", detectedOrders)}). Analyze and save each order separately.");
        }
        var result = new AiMarketplaceOrderImportResult
        {
            Sale = request.Sale,
            Job = request.Job ?? new CustomerJob(),
            Customer = request.Customer ?? new Party { Name = request.Sale.CustomerName, PartyType = "Customer", DefaultPlatform = request.Sale.Platform }
        };
        NormalizeMarketplaceOrder(result, new AiOperationSourcePacket(string.Empty, [], [], [], "Confirmed marketplace import"));
        if (result.Sale.SaleDate is null)
        {
            throw new InvalidOperationException("Sale Date is required before saving a paid marketplace order. Use the order date shown on the marketplace receipt.");
        }
        var duplicates = await FindMarketplaceSaleDuplicatesAsync(result.Sale, cancellationToken);
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException($"Marketplace order {result.Sale.OrderNumber ?? "(no order number)"} already appears in Sales. Open the existing Sale instead of importing a duplicate.");
        }

        var now = DateTime.UtcNow;
        var proofPath = request.Proofs.FirstOrDefault()?.StoredPath;
        result.Sale.Id = 0;
        result.Sale.CreatedAtUtc = now;
        result.Sale.UpdatedAtUtc = now;
        result.Sale.SourceProof = string.IsNullOrWhiteSpace(proofPath) ? result.Sale.SourceProof : proofPath;
        result.Sale.Notes = $"AI/LOCAL RULES MARKETPLACE IMPORT: Saved only after explicit review and confirmation.\n{result.Sale.Notes}".Trim();
        db.Sales.Add(result.Sale);

        CustomerJob? job = null;
        if (request.CreateJob && request.Job is not null && (await FindMarketplaceJobDuplicatesAsync(result.Sale, cancellationToken)).Count == 0)
        {
            job = result.Job;
            job.Id = 0;
            job.CreatedAtUtc = now;
            job.UpdatedAtUtc = now;
            job.SourceProof = string.IsNullOrWhiteSpace(proofPath) ? job.SourceProof : proofPath;
            job.Notes = $"AI/LOCAL RULES MARKETPLACE IMPORT: Completed Job created only after explicit review and confirmation.\n{job.Notes}".Trim();
            db.CustomerJobs.Add(job);
        }

        Party? customer = null;
        var customerSaveAction = string.Empty;
        if (request.SaveCustomerContact && !string.IsNullOrWhiteSpace(result.Customer.Name))
        {
            (customer, customerSaveAction) = await UpsertMarketplaceCustomerAsync(result.Customer, result.Sale, cancellationToken);
        }

        var docs = request.Proofs.Select(proof => new AuditDocument
        {
            DocumentDate = result.Sale.SaleDate ?? DateTime.Today,
            DocumentType = result.Sale.Platform.Equals("Etsy", StringComparison.OrdinalIgnoreCase) ? "Etsy Order" : "Receipt",
            RelatedRecordType = "Sale",
            RelatedRecordNumber = result.Sale.OrderNumber,
            FileName = proof.FileName,
            FilePathOrUrl = proof.StoredPath,
            NeedsReview = false,
            Notes = "Uploaded and linked during confirmed Paid Marketplace Order Import."
        }).ToList();
        db.AuditDocuments.AddRange(docs);
        await db.SaveChangesAsync(cancellationToken);
        return new AiMarketplaceOrderSaveResult
        {
            Sale = result.Sale,
            Job = job,
            Customer = customer,
            CustomerSaveAction = customerSaveAction,
            AuditDocuments = docs,
            Receipt = Receipt(false, "Explicit confirmed automation",
                ["Reviewed marketplace Sale draft, customer contact draft, optional Job draft, and uploaded proof files"],
                ["One paid Sale", customer is null ? "No customer contact change" : $"{customerSaveAction} customer contact", job is null ? "No Customer Job" : "One completed Customer Job", $"{docs.Count} linked Audit Doc(s)"],
                "After reviewing a paid marketplace order import.",
                "Explicit save only. Duplicate marketplace order numbers are blocked; no estimate, invoice, or AR row is created.")
        };
    }

    public async Task<AiProductImportResult> BuildProductDraftAsync(AiOperationSourcePacket packet, CancellationToken cancellationToken = default)
    {
        var instructions = await LoadInstructionsAsync(cancellationToken);
        var source = await BuildSourcePacketAsync(packet, cancellationToken);
        if (source.Text.Length < 3 && packet.Images.Count == 0)
        {
            throw new InvalidOperationException("Add a public product URL, pasted notes, a document, or a picture before building a Product draft.");
        }
        var localProduct = BuildLocalProductDraft(source.Text, packet, instructions);
        var result = new AiProductImportResult
        {
            Receipt = Receipt(false, "Local rules / source-page extraction",
                ["Only the public URLs, pasted notes, documents, and picture file names added to Product Import"],
                ["Unsaved Product / Costing draft and listing preview"],
                "To turn MakerWorld, Etsy, or any public product/source page into a reusable Product draft.",
                "Review-first. Local rules cannot understand image contents and never save a Product automatically."),
            Product = localProduct,
            Listing = BuildLocalListing(localProduct, "General", instructions),
            Warnings = [..packet.Warnings, ..source.Warnings]
        };
        var connection = await localAi.GetReadyConnectionAsync(cancellationToken);
        if (connection is not null)
        {
            try
            {
                var prompt = $$"""
                    Prepare a reviewable Product / Costing draft and listing draft from the supplied public product pages, documents, notes, and images.
                    This may be a MakerWorld page, Etsy page, or any public product site.
                    Never invent internal grams, print hours, costs, packaging, design time, or target price. Use 0 for unknown numeric values and ask a question.
                    Return JSON: {"product":{"name":"","sku":"","category":"","material":"","color":"","grams":0,"materialCostPerGram":0,"printHours":0,"machineRatePerHour":0,"packagingCost":0,"designMinutes":0,"targetPrice":0,"needsReview":true,"notes":""},"listing":{"platform":"General","title":"","description":"","highlights":[],"tags":[],"faq":[],"personalizationInstructions":""},"questions":[],"warnings":[]}.
                    Editable instructions: {{JsonSerializer.Serialize(instructions, JsonOptions)}}
                    """;
                var model = packet.Images.Count > 0
                    ? await localAi.CompleteJsonWithImagesAsync<AiProductImportModelDraft>(prompt, source.Text, packet.Images, cancellationToken)
                    : await localAi.CompleteJsonAsync<AiProductImportModelDraft>(prompt, source.Text, cancellationToken);
                result.Product = model.Product ?? result.Product;
                result.Listing = model.Listing ?? result.Listing;
                result.Questions = model.Questions ?? [];
                result.Warnings.AddRange(model.Warnings ?? []);
                result.Receipt = Receipt(true, connection.Provider,
                    ["Only the public URLs, pasted notes, documents, and pictures added to Product Import", "Editable AiOperationsInstructions.json"],
                    ["Unsaved Product / Costing draft and listing preview"],
                    "To turn MakerWorld, Etsy, or any public product/source page into a reusable Product draft.",
                    "Review-first. It never saves a Product automatically and unknown internal costs remain marked for review.");
            }
            catch (Exception ex)
            {
                result.Warnings.Insert(0, $"Local AI could not prepare the Product draft, so local extraction rules were used: {ex.Message}");
            }
        }

        NormalizeProduct(result.Product, instructions, packet);
        result.Listing = NormalizeListing(result.Listing, result.Product, "General", instructions);
        result.PossibleDuplicates = await FindProductDuplicatesAsync(result.Product, cancellationToken);
        if (result.PossibleDuplicates.Count > 0)
        {
            result.Warnings.Add("Possible existing Products were found. Compare them before saving this draft so the catalog is not duplicated.");
        }
        return result;
    }

    public async Task<AiJobPlanResult> BuildJobPlanAsync(AiJobPlanRequest request, CancellationToken cancellationToken = default)
    {
        var instructions = await LoadInstructionsAsync(cancellationToken);
        CustomerJob? job = null;
        if (request.JobId is > 0)
        {
            job = await db.CustomerJobs.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.JobId && !x.IsArchived, cancellationToken);
        }
        var source = string.Join("\n", new[]
        {
            job is null ? null : $"JOB: {job.JobName}\nCUSTOMER: {job.CustomerName}\nSTATUS: {job.Status}\nPRODUCT: {job.ProductName}\nMATERIAL: {job.Material}\nDUE: {job.DueDate:yyyy-MM-dd}\nSHIP BY: {job.ShipByDate:yyyy-MM-dd}\nDESCRIPTION: {job.Description}\nNOTES: {job.Notes}",
            request.SourceText
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (source.Length < 3) throw new InvalidOperationException("Choose a Customer Job or paste a job description.");

        var result = BuildLocalJobPlan(job, source, instructions);
        var connection = await localAi.GetReadyConnectionAsync(cancellationToken);
        if (connection is not null)
        {
            try
            {
                var model = await localAi.CompleteJsonAsync<AiJobPlanResult>(
                    $$"""
                    Build a practical production job plan for a small 3D-print business.
                    Use only supplied facts. Include requirement confirmation, design, slicer/cost check, prototype/approval when appropriate, production, QC, packaging/delivery, and invoice/payment follow-up.
                    Return JSON: {"jobName":"","goal":"","tasks":[{"title":"","area":"General","priority":"Normal","status":"Open","dueDate":null,"relatedRecord":"","notes":""}],"questions":[],"warnings":[]}.
                    Stages to consider: {{JsonSerializer.Serialize(instructions.JobPlanStages, JsonOptions)}}
                    """,
                    Trim(source, 24_000),
                    cancellationToken);
                result.JobName = model.JobName;
                result.Goal = model.Goal;
                result.Tasks = model.Tasks ?? [];
                result.Questions = model.Questions ?? [];
                result.Warnings = model.Warnings ?? [];
                result.Receipt = Receipt(true, connection.Provider,
                    ["Selected Customer Job and/or pasted job description", "Editable job-plan stages in AiOperationsInstructions.json"],
                    ["Unsaved task plan", "Action Items only after you explicitly click Create Action Items"],
                    "After a quote is accepted or before production starts.",
                    "Review-first. The model cannot start production, contact customers, or create tasks until you explicitly request it.");
            }
            catch (Exception ex)
            {
                result.Warnings.Insert(0, $"Local AI could not build the job plan, so the editable standard stage plan was used: {ex.Message}");
            }
        }
        result.Tasks = NormalizeTasks(result.Tasks, job?.JobName ?? result.JobName);
        return result;
    }

    public async Task<List<ActionItem>> CreateJobPlanActionsAsync(AiJobPlanSaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Tasks is null || request.Tasks.Count == 0) throw new InvalidOperationException("The job plan does not contain any tasks.");
        var existingKeys = (await db.ActionItems.AsNoTracking()
                .Where(action => !action.IsArchived && action.Status != "Done")
                .Select(action => action.Notes)
                .ToListAsync(cancellationToken))
            .Select(AutomationKeyFromNotes)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var created = new List<ActionItem>();
        foreach (var task in request.Tasks.Take(30))
        {
            var automationKey = $"job-plan|{Key(request.JobName, task.Title)}";
            if (!existingKeys.Add(automationKey)) continue;

            created.Add(new ActionItem
            {
                Title = Trim(task.Title, 180),
                Area = Trim(string.IsNullOrWhiteSpace(task.Area) ? "General" : task.Area, 80),
                Priority = task.Priority is "High" or "Low" ? task.Priority : "Normal",
                DueDate = DateTime.TryParse(task.DueDate, out var due) ? due : null,
                Status = "Open",
                RelatedRecord = Trim(task.RelatedRecord ?? request.JobName, 140),
                Notes = $"""
                    AUTOMATION SOURCE: AI Job Planner
                    AUTOMATION KEY: {automationKey}
                    AUTOMATION: Created only after explicit confirmation from the AI Job Planner.

                    {task.Notes}
                    """.Trim()
            });
        }
        db.ActionItems.AddRange(created);
        await db.SaveChangesAsync(cancellationToken);
        return created;
    }

    public async Task<AiSlicerReadResult> ReadSlicerAsync(AiOperationSourcePacket packet, CancellationToken cancellationToken = default)
    {
        var instructions = await LoadInstructionsAsync(cancellationToken);
        var source = string.Join("\n\n", new[]
        {
            packet.SourceText,
            string.Join("\n", packet.Images.Select(x => $"IMAGE FILE: {x.FileName}"))
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (source.Length < 3 && packet.Images.Count == 0) throw new InvalidOperationException("Paste slicer text or upload a slicer report/screenshot.");
        var result = BuildLocalSlicer(source, packet, instructions);
        var connection = await localAi.GetReadyConnectionAsync(cancellationToken);
        if (connection is not null)
        {
            try
            {
                const string prompt = """
                    Extract a reviewable 3D-printer slicer summary from supplied text, reports, and screenshots.
                    Do not invent values. Return JSON:
                    {"slicer":{"material":"","color":"","grams":0,"printHours":0,"filamentLengthMeters":0,"plateCount":0,"quantity":1,"estimatedMaterialCost":0,"notes":""},"productPrefill":{"name":"","sku":"","category":"3D Printed Product","material":"","color":"","grams":0,"materialCostPerGram":0,"printHours":0,"machineRatePerHour":0,"packagingCost":0,"designMinutes":0,"targetPrice":0,"needsReview":true,"notes":""},"questions":[],"warnings":[]}.
                    """;
                var model = packet.Images.Count > 0
                    ? await localAi.CompleteJsonWithImagesAsync<AiSlicerReadResult>(prompt, Trim(source, 24_000), packet.Images, cancellationToken)
                    : await localAi.CompleteJsonAsync<AiSlicerReadResult>(prompt, Trim(source, 24_000), cancellationToken);
                result.Slicer = model.Slicer ?? result.Slicer;
                result.ProductPrefill = model.ProductPrefill ?? result.ProductPrefill;
                result.Questions = model.Questions ?? [];
                result.Warnings.AddRange(model.Warnings ?? []);
                result.Receipt = Receipt(true, connection.Provider,
                    ["Only slicer text, reports, screenshots, and files added here"],
                    ["Unsaved slicer summary and Product / Costing draft"],
                    "To fill grams, print time, material, plates, and costing assumptions from slicer output.",
                    "Review-first. It never saves costing automatically and values must be checked against the slicer.");
            }
            catch (Exception ex)
            {
                result.Warnings.Insert(0, $"Local AI could not read the slicer packet, so local extraction rules were used: {ex.Message}");
            }
        }
        NormalizeSlicer(result, instructions);
        return result;
    }

    public async Task<AiListingResult> BuildListingAsync(AiListingRequest request, CancellationToken cancellationToken = default)
    {
        var instructions = await LoadInstructionsAsync(cancellationToken);
        var product = request.Product;
        if (request.ProductId is > 0)
        {
            product = await db.Products.AsNoTracking().FirstOrDefaultAsync(x => x.Id == request.ProductId && !x.IsArchived, cancellationToken);
        }
        if (product is null || string.IsNullOrWhiteSpace(product.Name)) throw new InvalidOperationException("Choose a Product / Costing row first.");
        var platform = string.IsNullOrWhiteSpace(request.Platform) ? "General" : request.Platform.Trim();
        var result = new AiListingResult
        {
            Listing = BuildLocalListing(product, platform, instructions),
            Receipt = Receipt(false, "Local rules / listing template",
                ["Selected Product / Costing row"],
                ["Listing copy preview only"],
                "When creating or refreshing MakerWorld, Etsy, or general product copy.",
                "Review-first. It does not post to any marketplace or modify the Product.")
        };
        var connection = await localAi.GetReadyConnectionAsync(cancellationToken);
        if (connection is not null)
        {
            try
            {
                result.Listing = await localAi.CompleteJsonAsync<AiListingDraft>(
                    $$"""
                    Write accurate product-listing copy for {{platform}}. Use only supplied product facts.
                    Do not invent compatibility, safety, dimensions, certifications, included items, or performance claims.
                    Return JSON: {"platform":"{{platform}}","title":"","description":"","highlights":[],"tags":[],"faq":[],"personalizationInstructions":""}.
                    Rules: {{JsonSerializer.Serialize(instructions.ListingRules, JsonOptions)}}
                    """,
                    JsonSerializer.Serialize(new { product, request.ExtraInstructions }, JsonOptions),
                    cancellationToken);
                result.Receipt = Receipt(true, connection.Provider,
                    ["Selected Product / Costing row", "Optional listing instructions"],
                    ["Listing copy preview only"],
                    "When creating or refreshing MakerWorld, Etsy, or general product copy.",
                    "Review-first. It does not post to any marketplace or modify the Product.");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Local AI could not write the listing, so the local listing template was used: {ex.Message}");
            }
        }
        result.Listing = NormalizeListing(result.Listing, product, platform, instructions);
        return result;
    }

    public async Task<AiLedgerAnswer> AskLedgerAsync(AiLedgerQuestionRequest request, CancellationToken cancellationToken = default)
    {
        var query = (request.Query ?? string.Empty).Trim();
        if (query.Length < 2) throw new InvalidOperationException("Ask a specific question about the ledger.");
        var results = await SearchLedgerAsync(query, cancellationToken);
        var answer = new AiLedgerAnswer
        {
            Query = query,
            Results = results,
            Answer = results.Count == 0
                ? "No active ledger records matched that question."
                : $"Found {results.Count} relevant active ledger record{(results.Count == 1 ? string.Empty : "s")}. Review the linked records below.",
            Receipt = Receipt(false, "Local rules / ledger search",
                ["Active local ledger rows matching the question"],
                [],
                "When you want a plain-language answer about records already entered in the app.",
                "Read-only. Search never edits, saves, sends, archives, or deletes records.")
        };
        var connection = await localAi.GetReadyConnectionAsync(cancellationToken);
        if (connection is not null && results.Count > 0)
        {
            try
            {
                answer.Answer = await localAi.CompleteTextAsync(
                    """
                    Answer the user's ledger question using only the supplied matching records.
                    Be concise, mention uncertainty, and do not invent totals or records. Suggest opening linked records when details need confirmation.
                    """,
                    JsonSerializer.Serialize(new { query, results }, JsonOptions),
                    cancellationToken);
                answer.Receipt = Receipt(true, connection.Provider,
                    ["Only active local ledger rows selected by deterministic search for this question"],
                    [],
                    "When you want a plain-language answer about records already entered in the app.",
                    "Read-only local-model answer. It never edits, saves, sends, archives, or deletes records.");
            }
            catch (Exception ex)
            {
                answer.Warnings.Add($"Local AI could not explain the search results, so the matching-record list remains available: {ex.Message}");
            }
        }
        return answer;
    }

    private async Task<List<AiLedgerSearchHit>> SearchLedgerAsync(string query, CancellationToken cancellationToken)
    {
        var terms = Regex.Matches(query.ToLowerInvariant(), @"[a-z0-9$.-]{2,}")
            .Select(x => x.Value)
            .Where(x => x is not "the" and not "and" and not "for" and not "with" and not "show" and not "find")
            .Distinct()
            .Take(8)
            .ToList();
        bool Match(params object?[] values)
        {
            var text = string.Join(" ", values.Where(x => x is not null)).ToLowerInvariant();
            return terms.Count == 0 || terms.All(text.Contains) || terms.Count(term => text.Contains(term)) >= Math.Min(2, terms.Count);
        }

        var hits = new List<AiLedgerSearchHit>();
        hits.AddRange((await db.Sales.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken))
            .Where(x => Match(x.CustomerName, x.ProductName, x.OrderNumber, x.InvoiceNumber, x.Platform, x.Status, x.Notes, x.CustomerPaid))
            .Select(x => new AiLedgerSearchHit("Sales", "sales", x.Id, $"{x.CustomerName}: {x.ProductName}", $"{x.Platform} {x.Status} {MoneyRules.SaleGrossReceipts(x):C}", $"Order {x.OrderNumber}; invoice {x.InvoiceNumber}; paid {x.CustomerPaid:C}")));
        hits.AddRange((await db.Expenses.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken))
            .Where(x => Match(x.VendorName, x.Description, x.Category, x.TaxBucket, x.Notes, x.Total))
            .Select(x => new AiLedgerSearchHit("Expenses", "expenses", x.Id, $"{x.VendorName}: {x.Description}", $"{x.Category} {x.Total:C}", $"Tax bucket {x.TaxBucket}; proof {x.ReceiptProof}")));
        hits.AddRange((await db.CustomerJobs.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken))
            .Where(x => Match(x.CustomerName, x.JobName, x.ProductName, x.Status, x.JobNumber, x.RelatedOrderNumber, x.RelatedInvoiceNumber, x.Description, x.Notes))
            .Select(x => new AiLedgerSearchHit("Customer Jobs", "customerJobs", x.Id, $"{x.CustomerName}: {x.JobName}", $"{x.Status}; {x.ProductName}", $"Order {x.RelatedOrderNumber}; invoice {x.RelatedInvoiceNumber}; due {x.DueDate:yyyy-MM-dd}")));
        hits.AddRange((await db.Products.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken))
            .Where(x => Match(x.Name, x.Sku, x.Category, x.Material, x.Color, x.Notes, x.TargetPrice))
            .Select(x => new AiLedgerSearchHit("Products", "products", x.Id, x.Name, $"{x.Material} {x.Color} target {x.TargetPrice:C}", $"SKU {x.Sku}; estimated cost {x.EstimatedCost:C}")));
        hits.AddRange((await db.ReceivableInvoices.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken))
            .Where(x => Match(x.CustomerName, x.ProjectName, x.InvoiceNumber, x.Status, x.Notes, x.InvoiceTotal, x.AmountPaid))
            .Select(x => new AiLedgerSearchHit("AR", "receivables", x.Id, $"{x.InvoiceNumber}: {x.CustomerName}", $"{x.Status}; balance {x.BalanceDue:C}", $"Total {x.InvoiceTotal:C}; paid {x.AmountPaid:C}")));
        return hits.Take(40).ToList();
    }

    private async Task<(string Text, List<string> Warnings)> BuildSourcePacketAsync(AiOperationSourcePacket packet, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        var warnings = new List<string>();
        if (!string.IsNullOrWhiteSpace(packet.SourceText)) parts.Add(packet.SourceText.Trim());
        foreach (var url in packet.SourceUrls.Distinct(StringComparer.OrdinalIgnoreCase).Take(10))
        {
            try
            {
                parts.Add(await FetchPublicPageAsync(url, cancellationToken));
            }
            catch (Exception ex)
            {
                warnings.Add($"{url} could not be read: {ex.Message}");
                parts.Add($"SOURCE URL (page could not be fetched; review manually): {url}");
            }
        }
        if (packet.Images.Count > 0)
        {
            parts.Add("UPLOADED PICTURES:\n" + string.Join("\n", packet.Images.Select(x => x.FileName)));
        }
        return (Trim(string.Join("\n\n", parts), 32_000), warnings);
    }

    private async Task<string> FetchPublicPageAsync(string value, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Only public HTTPS URLs are allowed.");
        await EnsurePublicHostAsync(uri, cancellationToken);
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is not null)
        {
            var redirected = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
            await EnsurePublicHostAsync(redirected, cancellationToken);
            using var redirectedResponse = await httpClient.GetAsync(redirected, cancellationToken);
            redirectedResponse.EnsureSuccessStatusCode();
            return PageText(redirected, await redirectedResponse.Content.ReadAsStringAsync(cancellationToken));
        }
        response.EnsureSuccessStatusCode();
        return PageText(uri, await response.Content.ReadAsStringAsync(cancellationToken));
    }

    private static async Task EnsurePublicHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrWhiteSpace(uri.UserInfo)) throw new InvalidOperationException("Only public HTTPS URLs are allowed.");
        var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
        if (addresses.Length == 0 || addresses.Any(IsPrivateAddress)) throw new InvalidOperationException("Private or local-network URLs are blocked.");
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.Equals(IPAddress.IPv6Loopback);
        }
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || bytes[0] == 127
            || bytes[0] == 0
            || (bytes[0] == 169 && bytes[1] == 254)
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static string PageText(Uri uri, string html)
    {
        string Meta(string name) => WebUtility.HtmlDecode(Regex.Match(html,
            $@"(?is)<meta[^>]+(?:name|property)=[""']{Regex.Escape(name)}[""'][^>]+content=[""'](?<value>[^""']+)",
            RegexOptions.IgnoreCase).Groups["value"].Value);
        var title = WebUtility.HtmlDecode(Regex.Match(html, @"(?is)<title[^>]*>(?<value>.*?)</title>").Groups["value"].Value);
        var text = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(html, @"(?is)<script.*?</script>|<style.*?</style>", " "), @"(?is)<[^>]+>", " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return Trim($"SOURCE URL: {uri}\nTITLE: {Meta("og:title")} {title}\nDESCRIPTION: {Meta("og:description")} {Meta("description")}\nPAGE TEXT: {text}", 20_000);
    }

    private static Party BuildLocalMarketplaceCustomer(string source, string platform, AiOperationSourcePacket packet)
    {
        var contact = new Party
        {
            PartyType = "Customer",
            DefaultPlatform = platform,
            Country = "United States",
            Notes = $"Marketplace contact draft extracted from {packet.SourceName}. Review before confirmed save."
        };

        var shipSegment = Regex.Match(source, @"(?is)\bShip\s+to\s+(?<value>.*?)(?=\s+From\b)").Groups["value"].Value;
        if (!string.IsNullOrWhiteSpace(shipSegment))
        {
            var recipient = Regex.Match(shipSegment, @"(?is)^(?:\d+\s+items?\s+)?(?<value>[A-Z][A-Z .'-]{1,158}?)(?=\s+\d+\s+)");
            contact.Name = recipient.Success ? recipient.Groups["value"].Value.Trim() : string.Empty;
            var addressSource = recipient.Success ? shipSegment[recipient.Length..] : shipSegment;
            contact.Address1 = First(addressSource,
                @"(?is)\b(?<value>\d{1,6}\s+[\w .#'/-]{1,160}?\b(?:St|Street|Rd|Road|Ave|Avenue|Dr|Drive|Ln|Lane|Ct|Court|Blvd|Boulevard|Way|Pl|Place|Pkwy|Parkway|Ter|Terrace|Cir|Circle))\b");
            var location = Regex.Match(shipSegment, @"(?i)\b(?<city>[A-Z][A-Z .'-]{1,78}),\s*(?<state>[A-Z]{2})\s+(?<postal>\d{5}(?:-\d{4})?)\b");
            if (location.Success)
            {
                contact.City = ToTitleCase(location.Groups["city"].Value);
                contact.State = location.Groups["state"].Value.ToUpperInvariant();
                contact.PostalCode = location.Groups["postal"].Value.Trim();
            }
            if (shipSegment.Contains("United States", StringComparison.OrdinalIgnoreCase)) contact.Country = "United States";
            else if (shipSegment.Contains("Canada", StringComparison.OrdinalIgnoreCase)) contact.Country = "Canada";
        }

        if (string.IsNullOrWhiteSpace(contact.Address1))
        {
            var shipTo = Regex.Match(source,
                @"(?is)\bShip\s+to\s+(?<name>[A-Z][A-Z .'-]{1,158}?)\s+(?<address1>\d[\w .#'/-]{2,180}?)\s+(?<city>[A-Z][A-Z .'-]{1,78}),\s*(?<state>[A-Z]{2})\s+(?<postal>\d{5}(?:-\d{4})?)\s+(?<country>United States|Canada)(?=\s+(?:Scheduled\s+to\s+ship\s+by|From|Order\b))");
            if (shipTo.Success)
            {
                contact.Name = shipTo.Groups["name"].Value.Trim();
                contact.Address1 = shipTo.Groups["address1"].Value.Trim();
                contact.City = ToTitleCase(shipTo.Groups["city"].Value);
                contact.State = shipTo.Groups["state"].Value.ToUpperInvariant();
                contact.PostalCode = shipTo.Groups["postal"].Value.Trim();
                contact.Country = shipTo.Groups["country"].Value.Trim();
            }
        }

        var buyer = Regex.Match(source, @"(?is)\bBuyer\s+(?<name>[^()\r\n]{2,160}?)\s*\((?<contact>[^)\r\n]{2,160})\)");
        if (buyer.Success)
        {
            if (string.IsNullOrWhiteSpace(contact.Name)) contact.Name = buyer.Groups["name"].Value.Trim();
            var buyerContact = buyer.Groups["contact"].Value.Trim();
            if (buyerContact.Contains('@')) contact.Email = buyerContact;
            else contact.EtsyUsername = buyerContact;
        }

        contact.Email ??= First(source, @"(?i)\b(?<value>[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,})\b");
        contact.Phone ??= First(source, @"(?im)\b(?:phone|telephone|tel)\s*[:\-]\s*(?<value>(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]\d{3}[\s.-]\d{4})");
        return contact;
    }

    private static AiMarketplaceOrderImportResult BuildLocalMarketplaceOrder(string source, AiOperationSourcePacket packet)
    {
        var platform = source.Contains("etsy", StringComparison.OrdinalIgnoreCase) ? "Etsy" : "Other";
        var contact = BuildLocalMarketplaceCustomer(source, platform, packet);
        var orderNumber = First(source,
            @"(?im)\border\s*(?:number|no\.?|#)\s*[:#]?\s*(?<value>[A-Z0-9-]{5,})",
            @"(?im)\border\s+ID\s*[:#]?\s*(?<value>[A-Z0-9-]{5,})");
        var customer = !string.IsNullOrWhiteSpace(contact.Name) ? contact.Name : First(source,
            @"(?im)^(?:buyer|customer|ship\s*to|sold\s*to)\s*[:\-]\s*(?<value>[^\r\n,]{2,160})",
            @"(?im)^name\s*[:\-]\s*(?<value>[^\r\n,]{2,160})");
        var product = First(source,
            @"(?im)^(?:item|product|listing)\s*(?:name|title)?\s*[:\-]\s*(?<value>[^\r\n]{3,220})",
            @"(?im)^description\s*[:\-]\s*(?<value>[^\r\n]{3,220})");
        var itemSales = Decimal(source,
            @"(?im)\bitem\s*(?:total|subtotal|sales?)\s*[:$ ]+\$?\s*(?<value>\d+(?:\.\d{1,2})?)",
            @"(?im)\bsubtotal\s*[:$ ]+\$?\s*(?<value>\d+(?:\.\d{1,2})?)");
        var shipping = Decimal(source, @"(?im)\bshipping(?:\s+charged)?\s*[:$ ]+\$?\s*(?<value>\d+(?:\.\d{1,2})?)");
        var tax = Decimal(source, @"(?im)\b(?:sales\s+)?tax\s*[:$ ]+\$?\s*(?<value>\d+(?:\.\d{1,2})?)");
        var paid = Decimal(source,
            @"(?im)\b(?:order\s+total|customer\s+paid|total\s+paid|grand\s+total)\s*[:$ ]+\$?\s*(?<value>\d+(?:\.\d{1,2})?)",
            @"(?im)^total\s*[:$ ]+\$?\s*(?<value>\d+(?:\.\d{1,2})?)");
        var fees = Decimal(source, @"(?im)\b(?:platform|etsy|transaction|processing)\s+fees?\s*[:$ ]+\$?\s*(?<value>\d+(?:\.\d{1,2})?)");
        var label = Decimal(source, @"(?im)\b(?:shipping\s+label|postage|label\s+cost)\s*[:$ ]+\$?\s*(?<value>\d+(?:\.\d{1,2})?)");
        var quantity = Math.Max(1, Decimal(source, @"(?im)\b(?:quantity|qty)\s*[:x ]+\s*(?<value>\d+(?:\.\d+)?)"));
        var tracking = First(source, @"(?im)\btracking(?:\s+number|#)?\s*[:#]?\s*(?<value>[A-Z0-9-]{8,})");
        var date = Date(source,
            @"(?im)\b(?:order\s+date|ordered|sale\s+date|purchased)\s*(?:[:\-]\s*)?(?<value>(?:Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|Jun(?:e)?|Jul(?:y)?|Aug(?:ust)?|Sep(?:tember)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\s+\d{1,2},\s+\d{4}|\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}-\d{2}-\d{2})");
        var sale = new Sale
        {
            SaleDate = date,
            Platform = platform,
            PaymentMethod = platform == "Etsy" ? "Etsy Payments" : "Online Marketplace",
            SalesTaxHandling = "Marketplace collected/remitted - verify",
            OrderNumber = orderNumber,
            CustomerName = customer ?? string.Empty,
            ProductName = product ?? "Marketplace order item - needs review",
            Quantity = quantity,
            ItemSales = itemSales,
            ShippingCharged = shipping,
            SalesTaxCollected = tax,
            CustomerPaid = paid,
            PlatformFees = fees,
            ShippingLabelCost = label,
            Status = "Paid",
            SourceProof = packet.SourceName,
            TrackingNumber = tracking,
            IncludeInDashboard = true,
            NeedsReview = true,
            Notes = "LOCAL RULES marketplace-order draft. Verify every amount and add missing marketplace fees, shipping label cost, and COGS."
        };
        return new AiMarketplaceOrderImportResult
        {
            Sale = sale,
            Job = MarketplaceJobFromSale(sale),
            Customer = contact,
            Warnings = [..packet.Warnings],
            Receipt = Receipt(false, "Local rules / marketplace order extraction",
                ["Only marketplace order text, readable documents, and screenshot file names added here"],
                ["Reviewable paid Sale, customer contact, and optional completed Customer Job; proof only after explicit Save"],
                "When an Etsy or marketplace order is already paid and should not become an estimate, invoice, or AR row.",
                "Review-first. Local rules cannot understand screenshot pixels. Duplicate order numbers block saving.")
        };
    }

    private static CustomerJob MarketplaceJobFromSale(Sale sale) => new()
    {
        JobDate = sale.SaleDate,
        CustomerName = sale.CustomerName,
        Platform = sale.Platform,
        RelatedOrderNumber = sale.OrderNumber,
        JobName = sale.ProductName,
        JobType = "Print",
        Status = "Paid",
        ProductName = sale.ProductName,
        Color = sale.Color,
        Description = $"Completed paid marketplace order {sale.OrderNumber}.",
        InvoiceAmount = sale.CustomerPaid,
        AmountPaid = sale.CustomerPaid,
        ShipByDate = sale.ShipByDate,
        SourceProof = sale.SourceProof,
        NeedsReview = sale.NeedsReview,
        Notes = "Marketplace order job draft. No estimate, invoice, or AR record is required."
    };

    private static Sale MarketplaceSaleFromModel(AiMarketplaceSaleDraft model, AiOperationSourcePacket packet) => new()
    {
        SaleDate = ParseDate(model.SaleDate),
        Platform = model.Platform,
        PaymentMethod = model.PaymentMethod,
        SalesTaxHandling = model.SalesTaxHandling,
        OrderNumber = model.OrderNumber,
        CustomerName = model.CustomerName,
        ProductName = model.ProductName,
        Sku = model.Sku,
        Variation = model.Variation,
        Color = model.Color,
        Quantity = model.Quantity,
        ItemSales = model.ItemSales,
        ShippingCharged = model.ShippingCharged,
        SalesTaxCollected = model.SalesTaxCollected,
        CustomerPaid = model.CustomerPaid,
        PlatformFees = model.PlatformFees,
        ShippingLabelCost = model.ShippingLabelCost,
        Refunds = model.Refunds,
        EstimatedCogs = model.EstimatedCogs,
        Status = "Paid",
        SourceProof = packet.SourceName,
        TrackingNumber = model.TrackingNumber,
        ShipByDate = ParseDate(model.ShipByDate),
        IncludeInDashboard = true,
        NeedsReview = true,
        Notes = model.Notes
    };

    private static Sale MergeMarketplaceSale(Sale model, Sale local)
    {
        model.SaleDate = local.SaleDate ?? model.SaleDate;
        model.OrderNumber = string.IsNullOrWhiteSpace(local.OrderNumber) ? model.OrderNumber : local.OrderNumber;
        model.CustomerName = string.IsNullOrWhiteSpace(local.CustomerName) ? model.CustomerName : local.CustomerName;
        model.ProductName = string.IsNullOrWhiteSpace(model.ProductName) ? local.ProductName : model.ProductName;
        model.TrackingNumber = string.IsNullOrWhiteSpace(local.TrackingNumber) ? model.TrackingNumber : local.TrackingNumber;
        model.ShipByDate ??= local.ShipByDate;
        model.Quantity = model.Quantity > 0 ? model.Quantity : local.Quantity;
        model.ItemSales = model.ItemSales > 0 ? model.ItemSales : local.ItemSales;
        model.ShippingCharged = model.ShippingCharged > 0 ? model.ShippingCharged : local.ShippingCharged;
        model.SalesTaxCollected = model.SalesTaxCollected > 0 ? model.SalesTaxCollected : local.SalesTaxCollected;
        model.CustomerPaid = model.CustomerPaid > 0 ? model.CustomerPaid : local.CustomerPaid;
        model.PlatformFees = model.PlatformFees > 0 ? model.PlatformFees : local.PlatformFees;
        model.ShippingLabelCost = model.ShippingLabelCost > 0 ? model.ShippingLabelCost : local.ShippingLabelCost;
        return model;
    }

    private static Party MarketplaceCustomerFromModel(AiMarketplaceCustomerDraft model, string platform) => new()
    {
        Name = model.Name,
        PartyType = "Customer",
        Email = model.Email,
        Phone = model.Phone,
        Address1 = model.Address1,
        Address2 = model.Address2,
        City = model.City,
        State = model.State,
        PostalCode = model.PostalCode,
        Country = model.Country,
        EtsyUsername = model.EtsyUsername,
        DefaultPlatform = platform,
        Notes = model.Notes
    };

    private static Party MergeMarketplaceCustomer(Party model, Party local)
    {
        model.Name = string.IsNullOrWhiteSpace(local.Name) ? model.Name : local.Name;
        model.Email = string.IsNullOrWhiteSpace(local.Email) ? model.Email : local.Email;
        model.Phone = string.IsNullOrWhiteSpace(local.Phone) ? model.Phone : local.Phone;
        model.Address1 = string.IsNullOrWhiteSpace(local.Address1) ? model.Address1 : local.Address1;
        model.Address2 = string.IsNullOrWhiteSpace(local.Address2) ? model.Address2 : local.Address2;
        model.City = string.IsNullOrWhiteSpace(local.City) ? model.City : local.City;
        model.State = string.IsNullOrWhiteSpace(local.State) ? model.State : local.State;
        model.PostalCode = string.IsNullOrWhiteSpace(local.PostalCode) ? model.PostalCode : local.PostalCode;
        model.Country = string.IsNullOrWhiteSpace(local.Country) ? model.Country : local.Country;
        model.EtsyUsername = string.IsNullOrWhiteSpace(local.EtsyUsername) ? model.EtsyUsername : local.EtsyUsername;
        model.DefaultPlatform = string.IsNullOrWhiteSpace(local.DefaultPlatform) ? model.DefaultPlatform : local.DefaultPlatform;
        return model;
    }

    private static CustomerJob MarketplaceJobFromModel(Sale sale, AiMarketplaceJobDraft model)
    {
        var job = MarketplaceJobFromSale(sale);
        job.Material = model.Material;
        job.Color = string.IsNullOrWhiteSpace(model.Color) ? sale.Color : model.Color;
        job.Description = string.IsNullOrWhiteSpace(model.Description) ? job.Description : model.Description;
        job.ShipByDate = ParseDate(model.ShipByDate) ?? sale.ShipByDate;
        job.Notes = model.Notes;
        return job;
    }

    private static void NormalizeMarketplaceOrder(AiMarketplaceOrderImportResult result, AiOperationSourcePacket packet)
    {
        result.Sale ??= new Sale();
        var sale = result.Sale;
        sale.Id = 0;
        sale.IsArchived = false;
        sale.Platform = Trim(string.IsNullOrWhiteSpace(sale.Platform) ? "Etsy" : sale.Platform, 80);
        sale.PaymentMethod = Trim(string.IsNullOrWhiteSpace(sale.PaymentMethod)
            ? (sale.Platform.Equals("Etsy", StringComparison.OrdinalIgnoreCase) ? "Etsy Payments" : "Online Marketplace")
            : sale.PaymentMethod, 80);
        sale.SalesTaxHandling = Trim(string.IsNullOrWhiteSpace(sale.SalesTaxHandling) ? "Marketplace collected/remitted - verify" : sale.SalesTaxHandling, 100);
        sale.OrderNumber = TrimNullable(sale.OrderNumber, 80);
        sale.InvoiceNumber = null;
        sale.CustomerName = Trim(sale.CustomerName, 160);
        sale.ProductName = Trim(string.IsNullOrWhiteSpace(sale.ProductName) ? "Marketplace order item - needs review" : sale.ProductName, 220);
        sale.Sku = TrimNullable(sale.Sku, 80);
        sale.Variation = TrimNullable(sale.Variation, 120);
        sale.Color = TrimNullable(sale.Color, 80);
        sale.Quantity = Positive(sale.Quantity) ?? 1;
        sale.ItemSales = NonNegative(sale.ItemSales);
        sale.ShippingCharged = NonNegative(sale.ShippingCharged);
        sale.SalesTaxCollected = NonNegative(sale.SalesTaxCollected);
        sale.CustomerPaid = NonNegative(sale.CustomerPaid);
        sale.PlatformFees = NonNegative(sale.PlatformFees);
        sale.ShippingLabelCost = NonNegative(sale.ShippingLabelCost);
        sale.Refunds = NonNegative(sale.Refunds);
        sale.EstimatedCogs = NonNegative(sale.EstimatedCogs);
        if ((sale.ItemSales ?? 0) <= 0 && (sale.CustomerPaid ?? 0) > 0)
        {
            sale.ItemSales = Math.Max(0, (sale.CustomerPaid ?? 0) - (sale.ShippingCharged ?? 0) - (sale.SalesTaxCollected ?? 0));
        }
        if ((sale.CustomerPaid ?? 0) <= 0)
        {
            sale.CustomerPaid = Math.Max(0, (sale.ItemSales ?? 0) + (sale.ShippingCharged ?? 0) + (sale.SalesTaxCollected ?? 0) - (sale.Refunds ?? 0));
        }
        sale.Status = "Paid";
        sale.SourceProof = TrimNullable(string.IsNullOrWhiteSpace(sale.SourceProof) ? packet.SourceName : sale.SourceProof, 220);
        sale.TrackingNumber = TrimNullable(sale.TrackingNumber, 100);
        sale.IncludeInDashboard = true;
        sale.NeedsReview = true;
        sale.Notes = Trim(sale.Notes, 4_000);

        result.Job ??= MarketplaceJobFromSale(sale);
        var job = result.Job;
        job.Id = 0;
        job.IsArchived = false;
        job.JobDate ??= sale.SaleDate;
        job.CustomerName = Trim(string.IsNullOrWhiteSpace(job.CustomerName) ? sale.CustomerName : job.CustomerName, 160);
        job.Platform = sale.Platform;
        job.RelatedOrderNumber = sale.OrderNumber;
        job.RelatedInvoiceNumber = null;
        job.JobName = Trim(string.IsNullOrWhiteSpace(job.JobName) ? sale.ProductName : job.JobName, 220);
        job.JobType = Trim(string.IsNullOrWhiteSpace(job.JobType) ? "Print" : job.JobType, 80);
        job.Status = "Paid";
        job.ProductName = TrimNullable(string.IsNullOrWhiteSpace(job.ProductName) ? sale.ProductName : job.ProductName, 220);
        job.Color = TrimNullable(string.IsNullOrWhiteSpace(job.Color) ? sale.Color : job.Color, 80);
        job.InvoiceAmount = sale.CustomerPaid;
        job.AmountPaid = sale.CustomerPaid;
        job.ShipByDate ??= sale.ShipByDate;
        job.SourceProof = sale.SourceProof;
        job.NeedsReview = true;
        job.Notes = Trim(job.Notes, 4_000);

        result.Customer ??= new Party();
        var customer = result.Customer;
        customer.Id = 0;
        customer.IsArchived = false;
        customer.Name = Trim(string.IsNullOrWhiteSpace(customer.Name) ? sale.CustomerName : customer.Name, 160);
        customer.PartyType = "Customer";
        customer.Email = TrimNullable(customer.Email, 160)?.ToLowerInvariant();
        customer.Phone = TrimNullable(customer.Phone, 40);
        customer.Address1 = TrimNullable(customer.Address1, 180);
        customer.Address2 = TrimNullable(customer.Address2, 180);
        customer.City = string.IsNullOrWhiteSpace(customer.City) ? null : Trim(ToTitleCase(customer.City), 80);
        customer.State = TrimNullable(customer.State, 40)?.ToUpperInvariant();
        customer.PostalCode = TrimNullable(customer.PostalCode, 20);
        customer.Country = TrimNullable(string.IsNullOrWhiteSpace(customer.Country) ? "United States" : customer.Country, 80);
        customer.EtsyUsername = TrimNullable(customer.EtsyUsername, 100);
        customer.DefaultPlatform = TrimNullable(string.IsNullOrWhiteSpace(customer.DefaultPlatform) ? sale.Platform : customer.DefaultPlatform, 120);
        customer.Notes = TrimNullable(customer.Notes, 4_000);
        if (!string.IsNullOrWhiteSpace(customer.Name))
        {
            sale.CustomerName = customer.Name;
            job.CustomerName = customer.Name;
        }

        result.Questions ??= [];
        result.Warnings ??= [];
        if (string.IsNullOrWhiteSpace(sale.OrderNumber)) result.Questions.Add("What is the marketplace order number? It is required for reliable duplicate prevention.");
        if (sale.SaleDate is null) result.Questions.Add("What is the marketplace order date? Sale Date is required before saving.");
        if (string.IsNullOrWhiteSpace(sale.CustomerName)) result.Questions.Add("What customer or buyer name appears on the order?");
        if (string.IsNullOrWhiteSpace(customer.Address1)) result.Questions.Add("What shipping address appears on the marketplace order?");
        if (string.IsNullOrWhiteSpace(customer.Email) && string.IsNullOrWhiteSpace(customer.EtsyUsername)) result.Questions.Add("What email or marketplace username identifies this customer?");
        if (string.IsNullOrWhiteSpace(customer.Phone)) result.Warnings.Add("No customer phone number was supplied by the marketplace order.");
        if ((sale.PlatformFees ?? 0) <= 0) result.Warnings.Add("Marketplace fees were not found. Add them from the Etsy/payment statement for accurate profit.");
        if ((sale.EstimatedCogs ?? 0) <= 0) result.Warnings.Add("Estimated COGS was not found. Add material, packaging, and production cost.");
    }

    private static Product BuildLocalProductDraft(string source, AiOperationSourcePacket packet, AiOperationsInstructions instructions)
    {
        var title = First(source, @"(?im)^TITLE:\s*(?<value>[^\r\n]+)", @"(?im)^SOURCE FILE:\s*(?<value>[^\r\n]+)", @"(?m)^(?<value>[^\r\n]{4,180})");
        title = Regex.Replace(title ?? "Imported product draft", @"\.(?:pdf|docx|txt|jpg|jpeg|png|webp)$", string.Empty, RegexOptions.IgnoreCase).Trim();
        var material = First(source, @"(?i)\b(?<value>PLA|PETG|ABS|ASA|TPU|resin|nylon|PC|polycarbonate)\b");
        var color = First(source, @"(?i)\b(?<value>black|white|red|blue|green|yellow|orange|purple|gray|grey|silver|gold|multicolor|multi-color)\b");
        return new Product
        {
            Name = Trim(title, 180),
            Category = instructions.DefaultProductCategory,
            Material = material,
            Color = color,
            MaterialCostPerGram = instructions.DefaultMaterialCostPerGram,
            MachineRatePerHour = instructions.DefaultMachineRatePerHour,
            PackagingCost = instructions.DefaultPackagingCost,
            NeedsReview = true,
            Notes = $"AI/LOCAL RULES PRODUCT IMPORT DRAFT. Sources: {packet.SourceName}; {string.Join(", ", packet.SourceUrls)}. Fill actual grams, print hours, packaging, design time, and target price before using for estimates."
        };
    }

    private static AiJobPlanResult BuildLocalJobPlan(CustomerJob? job, string source, AiOperationsInstructions instructions)
    {
        var name = job?.JobName ?? First(source, @"(?im)^(?:job|project|subject)\s*:\s*(?<value>[^\r\n]+)") ?? "New production job";
        var due = job?.ShipByDate ?? job?.DueDate;
        var related = job?.JobNumber ?? job?.RelatedOrderNumber ?? job?.RelatedInvoiceNumber ?? name;
        var tasks = instructions.JobPlanStages.Select((stage, index) => new AiJobPlanTask
        {
            Title = stage,
            Area = index >= instructions.JobPlanStages.Count - 1 ? "Invoice" : "General",
            Priority = index < 3 ? "High" : "Normal",
            Status = "Open",
            DueDate = due?.Date.ToString("yyyy-MM-dd"),
            RelatedRecord = related,
            Notes = "LOCAL RULES job-plan stage. Review owner, order, due date, and whether this stage applies."
        }).ToList();
        return new AiJobPlanResult
        {
            JobName = name,
            Goal = $"Complete {name} with requirements, pricing, approval, production, quality control, and payment tracked.",
            Tasks = tasks,
            Receipt = Receipt(false, "Local rules / editable job-plan stages",
                ["Selected Customer Job and/or pasted job description", "Editable job-plan stages in AiOperationsInstructions.json"],
                ["Unsaved task plan", "Action Items only after you explicitly click Create Action Items"],
                "After a quote is accepted or before production starts.",
                "Review-first. No Action Items are created until you explicitly request it.")
        };
    }

    private static AiSlicerReadResult BuildLocalSlicer(string source, AiOperationSourcePacket packet, AiOperationsInstructions instructions)
    {
        var grams = Decimal(source, @"(?i)(?:filament|material|weight|used)\D{0,30}(?<value>\d+(?:\.\d+)?)\s*g\b", @"(?i)(?<value>\d+(?:\.\d+)?)\s*g(?:rams?)?\b");
        var hours = Decimal(source, @"(?i)(?:print\s*time|time)\D{0,20}(?<hours>\d+)\s*h(?:ours?)?(?:\s*(?<minutes>\d+)\s*m)?");
        var minutes = Decimal(source, @"(?i)(?:print\s*time|time)\D{0,30}(?<value>\d+(?:\.\d+)?)\s*m(?:in(?:utes?)?)?\b");
        if (hours <= 0 && minutes > 0) hours = minutes / 60m;
        var material = First(source, @"(?i)\b(?<value>PLA|PETG|ABS|ASA|TPU|resin|nylon|PC|polycarbonate)\b") ?? string.Empty;
        var color = First(source, @"(?i)\b(?<value>black|white|red|blue|green|yellow|orange|purple|gray|grey|silver|gold|multicolor|multi-color)\b") ?? string.Empty;
        var plates = (int)Decimal(source, @"(?i)(?:plates?|build\s+plates?)\D{0,15}(?<value>\d+)");
        var qty = (int)Math.Max(1, Decimal(source, @"(?i)(?:quantity|copies|instances|objects?)\D{0,15}(?<value>\d+)"));
        var slicer = new AiSlicerDraft
        {
            Material = material,
            Color = color,
            Grams = grams,
            PrintHours = hours,
            PlateCount = plates,
            Quantity = qty,
            EstimatedMaterialCost = grams * instructions.DefaultMaterialCostPerGram,
            Notes = "Values extracted with local patterns. Verify against the slicer before saving."
        };
        return new AiSlicerReadResult
        {
            Slicer = slicer,
            ProductPrefill = new Product
            {
                Name = packet.SourceName,
                Category = instructions.DefaultProductCategory,
                Material = material,
                Color = color,
                Grams = grams,
                PrintHours = hours,
                MaterialCostPerGram = instructions.DefaultMaterialCostPerGram,
                MachineRatePerHour = instructions.DefaultMachineRatePerHour,
                PackagingCost = instructions.DefaultPackagingCost,
                NeedsReview = true,
                Notes = "Slicer Reader draft. Verify quantity, per-unit versus total values, and actual packaging/target price."
            },
            Warnings = [..packet.Warnings],
            Receipt = Receipt(false, "Local rules / slicer text extraction",
                ["Only slicer text, reports, screenshot file names, and files added here"],
                ["Unsaved slicer summary and Product / Costing draft"],
                "To fill grams, print time, material, plates, and costing assumptions from slicer output.",
                "Review-first. Local rules cannot understand screenshot pixels and never save costing automatically.")
        };
    }

    private static AiListingDraft BuildLocalListing(Product product, string platform, AiOperationsInstructions instructions)
    {
        var material = string.IsNullOrWhiteSpace(product.Material) ? "3D printed material" : product.Material;
        var color = string.IsNullOrWhiteSpace(product.Color) ? "available color options" : product.Color;
        return new AiListingDraft
        {
            Platform = platform,
            Title = Trim(product.Name, 140),
            Description = $"{product.Name} by {instructions.BrandName}. Made in {material} with {color}. Review dimensions, included quantity, compatibility, personalization, and production time before publishing.",
            Highlights = [$"Material: {material}", $"Color: {color}", "Made to order; review details before purchase"],
            Tags = Regex.Matches($"{product.Name} {product.Category} {material}", @"[A-Za-z0-9]+")
                .Select(x => x.Value.ToLowerInvariant()).Distinct().Take(13).ToList(),
            Faq = ["What dimensions and quantity are included?", "What color and material options are available?", "What is the current production time?"],
            PersonalizationInstructions = "Enter any requested text, color, dimensions, or fit details. Confirm details before production."
        };
    }

    private static AiListingDraft NormalizeListing(AiListingDraft? listing, Product product, string platform, AiOperationsInstructions instructions)
    {
        listing ??= BuildLocalListing(product, platform, instructions);
        listing.Platform = Trim(string.IsNullOrWhiteSpace(listing.Platform) ? platform : listing.Platform, 60);
        listing.Title = Trim(string.IsNullOrWhiteSpace(listing.Title) ? product.Name : listing.Title, 180);
        listing.Description = Trim(listing.Description, 4_000);
        listing.Highlights = (listing.Highlights ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Trim(x, 240)).Distinct().Take(10).ToList();
        listing.Tags = (listing.Tags ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Trim(x, 40)).Distinct(StringComparer.OrdinalIgnoreCase).Take(13).ToList();
        listing.Faq = (listing.Faq ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Trim(x, 300)).Distinct().Take(8).ToList();
        listing.PersonalizationInstructions = Trim(listing.PersonalizationInstructions, 800);
        return listing;
    }

    private static void NormalizeProduct(Product product, AiOperationsInstructions instructions, AiOperationSourcePacket packet)
    {
        product.Id = 0;
        product.IsArchived = false;
        product.Name = Trim(string.IsNullOrWhiteSpace(product.Name) ? packet.SourceName : product.Name, 180);
        product.Sku = TrimNullable(product.Sku, 80);
        product.Category = Trim(string.IsNullOrWhiteSpace(product.Category) ? instructions.DefaultProductCategory : product.Category, 100);
        product.Material = TrimNullable(product.Material, 80);
        product.Color = TrimNullable(product.Color, 80);
        product.Grams = Positive(product.Grams);
        product.MaterialCostPerGram = Positive(product.MaterialCostPerGram) ?? instructions.DefaultMaterialCostPerGram;
        product.PrintHours = Positive(product.PrintHours);
        product.MachineRatePerHour = Positive(product.MachineRatePerHour) ?? instructions.DefaultMachineRatePerHour;
        product.PackagingCost = Positive(product.PackagingCost) ?? instructions.DefaultPackagingCost;
        product.DesignMinutes = Positive(product.DesignMinutes);
        product.TargetPrice = Positive(product.TargetPrice);
        product.NeedsReview = true;
        product.Notes = Trim(product.Notes, 4_000);
    }

    private static void NormalizeSlicer(AiSlicerReadResult result, AiOperationsInstructions instructions)
    {
        result.Slicer ??= new AiSlicerDraft();
        result.ProductPrefill ??= new Product();
        result.Slicer.Grams = Math.Max(0, result.Slicer.Grams);
        result.Slicer.PrintHours = Math.Max(0, result.Slicer.PrintHours);
        result.Slicer.FilamentLengthMeters = Math.Max(0, result.Slicer.FilamentLengthMeters);
        result.Slicer.PlateCount = Math.Max(0, result.Slicer.PlateCount);
        result.Slicer.Quantity = Math.Max(1, result.Slicer.Quantity);
        result.Slicer.EstimatedMaterialCost = result.Slicer.Grams * instructions.DefaultMaterialCostPerGram;
        result.ProductPrefill.Id = 0;
        result.ProductPrefill.IsArchived = false;
        result.ProductPrefill.Category = string.IsNullOrWhiteSpace(result.ProductPrefill.Category) ? instructions.DefaultProductCategory : result.ProductPrefill.Category;
        result.ProductPrefill.Material = string.IsNullOrWhiteSpace(result.ProductPrefill.Material) ? result.Slicer.Material : result.ProductPrefill.Material;
        result.ProductPrefill.Color = string.IsNullOrWhiteSpace(result.ProductPrefill.Color) ? result.Slicer.Color : result.ProductPrefill.Color;
        result.ProductPrefill.Grams = result.Slicer.Grams;
        result.ProductPrefill.PrintHours = result.Slicer.PrintHours;
        result.ProductPrefill.MaterialCostPerGram = instructions.DefaultMaterialCostPerGram;
        result.ProductPrefill.MachineRatePerHour = instructions.DefaultMachineRatePerHour;
        result.ProductPrefill.NeedsReview = true;
    }

    private async Task<List<AiRecordReference>> FindProductDuplicatesAsync(Product draft, CancellationToken cancellationToken)
    {
        var name = Key(draft.Name);
        var sku = Key(draft.Sku);
        return (await db.Products.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken))
            .Where(x => (!string.IsNullOrWhiteSpace(sku) && Key(x.Sku) == sku) || Key(x.Name) == name)
            .Take(10)
            .Select(x => Ref("Product", x.Id, $"{x.Name} ({x.Sku ?? "no SKU"})", "products"))
            .ToList();
    }

    private async Task<List<AiRecordReference>> FindMarketplaceSaleDuplicatesAsync(Sale draft, CancellationToken cancellationToken)
    {
        var order = Key(draft.OrderNumber);
        if (string.IsNullOrWhiteSpace(order)) return [];
        return (await db.Sales.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken))
            .Where(x => Key(x.Platform) == Key(draft.Platform) && Key(x.OrderNumber) == order)
            .Take(10)
            .Select(x => Ref("Sale", x.Id, $"{x.Platform} order {x.OrderNumber}: {x.CustomerName} {x.CustomerPaid:C}", "sales"))
            .ToList();
    }

    private async Task<List<AiRecordReference>> FindMarketplaceJobDuplicatesAsync(Sale draft, CancellationToken cancellationToken)
    {
        var order = Key(draft.OrderNumber);
        if (string.IsNullOrWhiteSpace(order)) return [];
        return (await db.CustomerJobs.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken))
            .Where(x => Key(x.Platform) == Key(draft.Platform) && Key(x.RelatedOrderNumber) == order)
            .Take(10)
            .Select(x => Ref("Customer Job", x.Id, $"{x.Platform} order {x.RelatedOrderNumber}: {x.JobName}", "customerJobs"))
            .ToList();
    }

    private async Task<List<AiRecordReference>> FindMarketplaceCustomerMatchesAsync(Party draft, CancellationToken cancellationToken)
    {
        var name = Key(draft.Name);
        var email = Key(draft.Email);
        var username = Key(draft.EtsyUsername);
        return (await db.Parties.AsNoTracking().Where(x => !x.IsArchived).ToListAsync(cancellationToken))
            .Where(x => (!string.IsNullOrWhiteSpace(username) && Key(x.EtsyUsername) == username)
                || (!string.IsNullOrWhiteSpace(email) && Key(x.Email) == email)
                || (!string.IsNullOrWhiteSpace(name) && Key(x.Name) == name))
            .Take(10)
            .Select(x => Ref("Customer", x.Id, $"{x.Name}: {x.City}, {x.State}", "parties"))
            .ToList();
    }

    private async Task<(Party Customer, string Action)> UpsertMarketplaceCustomerAsync(Party draft, Sale sale, CancellationToken cancellationToken)
    {
        var name = Key(draft.Name);
        var email = Key(draft.Email);
        var username = Key(draft.EtsyUsername);
        var parties = await db.Parties.Where(x => !x.IsArchived).ToListAsync(cancellationToken);
        var customer = parties.FirstOrDefault(x => !string.IsNullOrWhiteSpace(username) && Key(x.EtsyUsername) == username)
            ?? parties.FirstOrDefault(x => !string.IsNullOrWhiteSpace(email) && Key(x.Email) == email)
            ?? parties.FirstOrDefault(x => !string.IsNullOrWhiteSpace(name) && Key(x.Name) == name);
        var action = customer is null ? "Created" : "Updated";

        if (customer is null)
        {
            customer = new Party
            {
                Name = draft.Name,
                PartyType = "Customer",
                Email = draft.Email,
                Phone = draft.Phone,
                Address1 = draft.Address1,
                Address2 = draft.Address2,
                City = draft.City,
                State = draft.State,
                PostalCode = draft.PostalCode,
                Country = draft.Country,
                EtsyUsername = draft.EtsyUsername,
                DefaultPlatform = draft.DefaultPlatform,
                Notes = draft.Notes
            };
            db.Parties.Add(customer);
        }
        else
        {
            customer.PartyType = customer.PartyType.Equals("Vendor", StringComparison.OrdinalIgnoreCase) ? "Both" : "Customer";
            customer.Email = PreferExisting(customer.Email, draft.Email);
            customer.Phone = PreferExisting(customer.Phone, draft.Phone);
            customer.Address1 = PreferExisting(customer.Address1, draft.Address1);
            customer.Address2 = PreferExisting(customer.Address2, draft.Address2);
            customer.City = PreferExisting(customer.City, draft.City);
            customer.State = PreferExisting(customer.State, draft.State);
            customer.PostalCode = PreferExisting(customer.PostalCode, draft.PostalCode);
            customer.Country = PreferExisting(customer.Country, draft.Country);
            customer.EtsyUsername = PreferExisting(customer.EtsyUsername, draft.EtsyUsername);
            customer.DefaultPlatform = PreferExisting(customer.DefaultPlatform, draft.DefaultPlatform);
        }

        var sourceNote = $"Marketplace customer contact confirmed from {sale.Platform} order {sale.OrderNumber}.";
        if (!ContainsLine(customer.Notes, sourceNote))
        {
            customer.Notes = Trim(string.Join("\n", new[] { customer.Notes, sourceNote }.Where(x => !string.IsNullOrWhiteSpace(x))), 4_000);
        }
        return (customer, action);
    }

    private async Task<AiOperationsInstructions> LoadInstructionsAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(InstructionsPath);
        return await JsonSerializer.DeserializeAsync<AiOperationsInstructions>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("AiOperationsInstructions.json is empty or invalid.");
    }

    private static List<AiJobPlanTask> NormalizeTasks(List<AiJobPlanTask>? tasks, string related)
    {
        return (tasks ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Title)).Take(30).Select(x =>
        {
            x.Title = Trim(x.Title, 180);
            x.Area = Trim(string.IsNullOrWhiteSpace(x.Area) ? "General" : x.Area, 80);
            x.Priority = x.Priority is "High" or "Low" ? x.Priority : "Normal";
            x.Status = "Open";
            x.RelatedRecord = Trim(string.IsNullOrWhiteSpace(x.RelatedRecord) ? related : x.RelatedRecord, 140);
            x.Notes = Trim(x.Notes, 1_000);
            return x;
        }).ToList();
    }

    private static void AddDuplicateGroups<T>(
        AiReconciliationReport report,
        IEnumerable<IGrouping<string, T>> groups,
        string kind,
        string title,
        string why,
        string action,
        Func<T, AiRecordReference> reference)
    {
        foreach (var group in groups.Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Count() > 1).Take(25))
        {
            report.Findings.Add(new(
                kind == "Exact duplicate" ? "High" : "Normal",
                kind,
                title,
                why,
                action,
                group.Take(10).Select(reference).ToList()));
        }
    }

    private static AiOperationReceipt Receipt(bool usedAi, string provider, List<string> reads, List<string> writes, string useWhen, string safety) => new()
    {
        Engine = usedAi ? "AI MODEL" : "LOCAL RULES",
        UsedAi = usedAi,
        Provider = provider,
        Reads = reads,
        Writes = writes,
        UseWhen = useWhen,
        Safety = safety
    };

    private static object Feature(string name, string reads, string writes, string useWhen) => new { name, reads, writes, useWhen };
    private static AiRecordReference Ref(string entity, int id, string label, string route) => new(entity, id, label, route);
    private static bool Same(string? left, string? right) => Key(left) == Key(right);
    private static string DateKey(DateTime? value) => value?.ToString("yyyy-MM-dd") ?? string.Empty;
    private static string MoneyKey(decimal? value) => (value ?? 0).ToString("0.00");
    private static string Digits(string? value) => Regex.Replace(value ?? string.Empty, @"\D", string.Empty);
    private static string Key(params string?[] values)
    {
        var normalized = values.Select(value => Regex.Replace(value ?? string.Empty, @"[^a-z0-9]+", string.Empty, RegexOptions.IgnoreCase).ToLowerInvariant()).ToList();
        return normalized.All(string.IsNullOrWhiteSpace) ? string.Empty : string.Join("|", normalized);
    }
    private static decimal? Positive(decimal? value) => value is > 0 ? value : null;
    private static decimal? NonNegative(decimal? value) => value is null ? null : Math.Max(0, value.Value);

    private static string? First(string source, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(source, pattern);
            if (match.Success) return match.Groups["value"].Value.Trim();
        }
        return null;
    }

    private static decimal Decimal(string source, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(source, pattern);
            if (!match.Success) continue;
            if (match.Groups["hours"].Success && decimal.TryParse(match.Groups["hours"].Value, out var hours))
            {
                var minutes = decimal.TryParse(match.Groups["minutes"].Value, out var parsedMinutes) ? parsedMinutes : 0;
                return hours + minutes / 60m;
            }
            if (decimal.TryParse(match.Groups["value"].Value, out var value)) return value;
        }
        return 0;
    }

    private static DateTime? Date(string source, params string[] patterns)
    {
        var value = First(source, patterns);
        return DateTime.TryParse(value, out var parsed) ? parsed : null;
    }

    private static DateTime? ParseDate(string? value) => DateTime.TryParse(value, out var parsed) ? parsed : null;

    private static List<string> DetectMarketplaceOrderNumbers(string source) => Regex.Matches(source,
            @"(?i)\border\s*(?:number|no\.?|#)?\s*[:#]?\s*(?<value>\d{7,})")
        .Select(match => match.Groups["value"].Value)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(20)
        .ToList();

    private static string? PreferExisting(string? existing, string? proposed) =>
        string.IsNullOrWhiteSpace(existing) ? TrimNullable(proposed, 4_000) : existing;

    private static bool ContainsLine(string? value, string line) =>
        (value ?? string.Empty).Contains(line, StringComparison.OrdinalIgnoreCase);

    private static string AutomationKeyFromNotes(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return string.Empty;
        var match = Regex.Match(notes, @"(?im)^AUTOMATION KEY:\s*(?<key>[^\r\n]+)");
        return match.Success ? match.Groups["key"].Value.Trim() : string.Empty;
    }

    private static string ToTitleCase(string value) =>
        System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Trim().ToLowerInvariant());

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = value.Trim();
        if (cleaned.Length <= max) return cleaned;
        if (max <= 3) return cleaned[..max];
        return cleaned[..(max - 3)].TrimEnd() + "...";
    }

    private static string? TrimNullable(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : Trim(value, max);
}
