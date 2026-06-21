using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Services;

public sealed class AiBusinessReviewService(AppDbContext db, LocalAiService localAi)
{
    private const string Engine = "Local rules / ledger analysis";

    public object Status() => new
    {
        engine = Engine,
        usedAi = false,
        sendsDataToAiProvider = false,
        reads = new[] { "Active ledger rows", "Review flags", "Payment status", "Proof references", "Entered product costs and prices" },
        writes = Array.Empty<string>(),
        safety = "Read-only review. It prioritizes possible cleanup work but never changes, saves, posts, or sends records.",
        useWhen = "Use before bookkeeping cleanup, customer follow-up, pricing review, or tax preparation."
    };

    public async Task<AiBusinessReviewResult> BuildAsync()
    {
        var today = DateTime.Today;
        var items = new List<AiBusinessReviewItem>();
        var sales = await db.Sales.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
        var expenses = await db.Expenses.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
        var invoices = await db.ReceivableInvoices.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
        var bills = await db.Bills.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
        var products = await db.Products.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
        var auditDocs = await db.AuditDocuments.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
        var actions = await db.ActionItems.AsNoTracking().Where(x => !x.IsArchived && !x.Status.Equals("Done")).ToListAsync();
        var taxObligations = await db.TaxObligations.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
        var mileage = await db.MileageLogs.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
        var communications = await db.CustomerCommunications.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
        var printerQueue = await db.PrinterQueueItems.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();

        AddGroup(
            items,
            sales.Where(x => x.PaymentMethod.Contains("Unknown", StringComparison.OrdinalIgnoreCase)),
            "High",
            "Sales",
            "Sales need payment methods",
            "Payment method controls payment-channel tax exports and helps reconcile deposits.",
            "Enter Cash, Zelle, Venmo, card, marketplace, or the actual method.",
            "sales",
            x => x.InvoiceNumber ?? x.OrderNumber ?? $"Sale #{x.Id}");

        AddGroup(
            items,
            sales.Where(x => x.SalesTaxHandling.Contains("Unknown", StringComparison.OrdinalIgnoreCase) || x.SalesTaxHandling.Contains("Review", StringComparison.OrdinalIgnoreCase)),
            "High",
            "Tax",
            "Sales-tax handling needs confirmation",
            "These sales do not say whether a marketplace or EPATA collected/remitted sales tax.",
            "Mark each sale as marketplace-remitted, seller-collected, exempt/not taxable, or no-tax/review.",
            "sales",
            x => x.InvoiceNumber ?? x.OrderNumber ?? $"Sale #{x.Id}");

        AddGroup(
            items,
            sales.Where(x => string.IsNullOrWhiteSpace(x.SourceProof) || x.NeedsReview),
            "High",
            "Sales",
            "Sales need proof or review",
            "These sales are missing a proof reference or are explicitly marked for review.",
            "Attach the order, invoice, payment screenshot, or other proof and clear the review flag.",
            "sales",
            x => x.InvoiceNumber ?? x.OrderNumber ?? $"Sale #{x.Id}");

        AddGroup(
            items,
            sales.Where(x => (x.PlatformFees ?? 0) == 0 && (x.EstimatedCogs ?? 0) == 0 && (x.ShippingLabelCost ?? 0) == 0),
            "Normal",
            "Sales",
            "Sales may be missing costs",
            "No platform fees, shipping-label cost, or estimated COGS were entered.",
            "Confirm whether the sale truly had no costs or enter the missing amounts.",
            "sales",
            x => x.InvoiceNumber ?? x.OrderNumber ?? $"Sale #{x.Id}");

        AddGroup(
            items,
            expenses.Where(x => x.NeedsReview || x.DeductibleStatus.Equals("Review", StringComparison.OrdinalIgnoreCase) || x.TaxBucket.Equals("Review", StringComparison.OrdinalIgnoreCase)),
            "High",
            "Expenses",
            "Expenses need tax classification review",
            "These rows are marked for review or still use a review-only tax classification.",
            "Confirm category, tax bucket, deductibility, and business-use percentage.",
            "expenses",
            x => $"{x.VendorName}: {x.Description}");

        AddGroup(
            items,
            expenses.Where(x => string.IsNullOrWhiteSpace(x.ReceiptProof)),
            "Normal",
            "Expenses",
            "Expenses are missing receipt proof",
            "A paid expense without a receipt or proof reference is harder to verify later.",
            "Attach the receipt, invoice, statement, or payment proof.",
            "expenses",
            x => $"{x.VendorName}: {x.Description}");

        AddGroup(
            items,
            invoices.Where(x => x.BalanceDue > 0 && x.DueDate.HasValue && x.DueDate.Value.Date < today),
            "High",
            "AR",
            "Customer invoices are overdue",
            "These invoice balances have passed their due dates.",
            "Follow up with the customer, record payment, or update the status and notes.",
            "receivables",
            x => $"{x.InvoiceNumber}: {x.CustomerName}");

        AddGroup(
            items,
            invoices.Where(x => x.BalanceDue > 0 && (!x.DueDate.HasValue || x.DueDate.Value.Date >= today)),
            "Normal",
            "AR",
            "Customer balances are still open",
            "These invoices still have money due.",
            "Confirm the due date and plan the next customer follow-up.",
            "receivables",
            x => $"{x.InvoiceNumber}: {x.CustomerName}");

        AddGroup(
            items,
            bills.Where(x => IsOpenBill(x) && x.BalanceDue > 0 && x.DueDate.HasValue && x.DueDate.Value.Date < today),
            "High",
            "AP",
            "Vendor bills are overdue",
            "These vendor balances have passed their due dates.",
            "Pay, dispute, or update the bill status and notes.",
            "bills",
            x => $"{x.VendorName}: {x.BillNumber ?? x.Description}");

        AddGroup(
            items,
            bills.Where(x => x.NeedsReview
                || (x.TaxDeductible
                    && MoneyRules.PaidBillAmount(x) > 0
                    && MoneyRules.BillDeductionBucket(x).Equals("Review", StringComparison.OrdinalIgnoreCase))),
            "Normal",
            "AP",
            "Bills need tax or deductibility review",
            "These vendor bills are marked for review or have paid deductible amounts without a confirmed tax category.",
            "Confirm source proof, tax category, deductibility, and paid amount before tax handoff.",
            "bills",
            x => $"{x.VendorName}: {x.BillNumber ?? x.Description}");

        AddGroup(
            items,
            products.Where(x => x.TargetPrice.HasValue && x.TargetPrice.Value > 0 && x.TargetPrice.Value < x.EstimatedCost),
            "High",
            "Products",
            "Products are priced below entered cost",
            "The target price is lower than the material, machine-time, and packaging cost currently entered.",
            "Review costing assumptions or raise the target price.",
            "products",
            x => $"{x.Name}: target {x.TargetPrice:C}, cost {x.EstimatedCost:C}");

        AddGroup(
            items,
            products.Where(x => x.NeedsReview || !x.TargetPrice.HasValue || x.TargetPrice <= 0),
            "Normal",
            "Products",
            "Products need pricing review",
            "These products are marked for review or do not have a usable target price.",
            "Complete costing and confirm a target price before using them in estimates.",
            "products",
            x => x.Sku is null ? x.Name : $"{x.Name} ({x.Sku})");

        AddGroup(
            items,
            auditDocs.Where(x => x.NeedsReview || string.IsNullOrWhiteSpace(x.RelatedRecordType) || string.IsNullOrWhiteSpace(x.RelatedRecordNumber)),
            "Normal",
            "Proof",
            "Uploaded proof needs linking or review",
            "These files are marked for review or are not fully linked to a ledger record.",
            "Open Audit Docs and connect each file to the record it proves.",
            "auditDocs",
            x => x.FileName);

        AddGroup(
            items,
            actions.Where(x => x.Priority.Equals("High", StringComparison.OrdinalIgnoreCase)),
            "High",
            "Actions",
            "High-priority action items are open",
            "These are existing tasks you marked as high priority.",
            "Resolve the task or update its status and due date.",
            "actions",
            x => x.RelatedRecord is null ? x.Title : $"{x.Title}: {x.RelatedRecord}");

        AddGroup(
            items,
            taxObligations.Where(x => x.NeedsReview && !x.Status.Equals("Filed / Paid", StringComparison.OrdinalIgnoreCase) && !x.Status.Equals("Not Required", StringComparison.OrdinalIgnoreCase)),
            "High",
            "Tax",
            "Tax filings, payments, or annual fees need review",
            "These obligations still need applicability, filing/payment, or proof confirmed.",
            "Open Tax Obligations and record status, amount, confirmation number, and proof.",
            "taxObligations",
            x => x.DueDate.HasValue ? $"{x.Title} {x.Period}: due {x.DueDate:yyyy-MM-dd}" : $"{x.Title} {x.Period}: due date not confirmed");

        AddGroup(
            items,
            mileage.Where(x => x.NeedsReview || !x.TripDate.HasValue || (x.BusinessMiles ?? 0) <= 0),
            "Normal",
            "Tax",
            "Mileage log entries need review",
            "These trips are missing a date, miles, or are explicitly marked for review.",
            "Complete the business purpose, miles, and support while details are available.",
            "mileage",
            x => $"{x.TripDate:yyyy-MM-dd}: {x.BusinessPurpose}");

        AddGroup(
            items,
            communications.Where(x => x.FollowUpStatus.Equals("Open", StringComparison.OrdinalIgnoreCase)
                && (!x.FollowUpDate.HasValue || x.FollowUpDate.Value.Date <= today)),
            "High",
            "Customer",
            "Customer communication follow-ups are due",
            "These conversations still need a response or check-in, and the follow-up date is missing or due.",
            "Reply, record the outcome, then mark the communication follow-up Done.",
            "communications",
            x => $"{x.CustomerName}: {x.Subject ?? x.Summary}");

        AddGroup(
            items,
            printerQueue.Where(x => PrinterQueueNeedsAttention(x, today)),
            "High",
            "Operations",
            "Printer queue items need attention",
            "These production items are overdue, failed, explicitly flagged, or have a problem status.",
            "Review the printer, slicer settings, material, failure notes, and next production step.",
            "printerQueue",
            x => PrinterQueueEvidence(x, today));

        var priorityOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["High"] = 0,
            ["Normal"] = 1,
            ["Low"] = 2
        };

        return new AiBusinessReviewResult
        {
            Items = items
                .OrderBy(x => priorityOrder.GetValueOrDefault(x.Priority, 9))
                .ThenBy(x => x.Area)
                .ThenBy(x => x.Title)
                .Take(60)
                .ToList()
        };
    }

    public async Task<AiBusinessReviewResult> BuildModelAssistedAsync(CancellationToken cancellationToken = default)
    {
        var localReview = await BuildAsync();
        const string systemPrompt = """
            You are a local, read-only bookkeeping review assistant for a small 3D-printing business.
            The deterministic review findings supplied by the app are the authority.
            Do not invent transactions, tax conclusions, deadlines, or filing requirements.
            Return JSON with exactly: {"summary":"","recommendedOrder":[],"questions":[]}.
            Keep the summary concise, order the existing cleanup themes by practical urgency, and ask only useful follow-up questions.
            """;
        var suppliedFindings = System.Text.Json.JsonSerializer.Serialize(localReview.Items);
        AiBusinessReviewNarrative narrative;
        try
        {
            narrative = await localAi.CompleteJsonAsync<AiBusinessReviewNarrative>(systemPrompt, suppliedFindings, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            var plainSummary = await localAi.CompleteTextAsync(
                systemPrompt + "\nIf structured JSON is difficult, return one concise plain-text explanation instead.",
                suppliedFindings,
                cancellationToken);
            narrative = new AiBusinessReviewNarrative { Summary = plainSummary };
        }

        localReview.Engine = "LM Studio local model + local rules";
        localReview.UsedAi = true;
        localReview.Safety = "Read-only local-model explanation of deterministic review findings. No records are changed, saved, posted, or sent outside this computer.";
        localReview.ModelSummary = narrative.Summary;
        localReview.ModelRecommendedOrder = narrative.RecommendedOrder ?? [];
        localReview.ModelQuestions = narrative.Questions ?? [];
        return localReview;
    }

    private static void AddGroup<T>(
        ICollection<AiBusinessReviewItem> items,
        IEnumerable<T> matches,
        string priority,
        string area,
        string title,
        string why,
        string action,
        string route,
        Func<T, string> evidence)
    {
        var rows = matches.Take(8).Select(evidence).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        if (rows.Count == 0) return;

        items.Add(new(
            priority,
            area,
            title,
            why,
            action,
            route,
            Engine,
            string.Join(" | ", rows)));
    }

    private static bool IsOpenBill(Bill bill)
    {
        return bill.Status is not null
            && !bill.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
            && !bill.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase)
            && !bill.Status.Equals("Void", StringComparison.OrdinalIgnoreCase)
            && !bill.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            && !bill.Status.Equals("Canceled", StringComparison.OrdinalIgnoreCase);
    }

    private static bool PrinterQueueNeedsAttention(PrinterQueueItem queue, DateTime today)
    {
        if (IsClosedPrinterQueueStatus(queue.Status))
        {
            return false;
        }

        return queue.NeedsReview
            || (queue.Status?.Equals("Needs Attention", StringComparison.OrdinalIgnoreCase) ?? false)
            || (queue.Status?.Contains("Fail", StringComparison.OrdinalIgnoreCase) ?? false)
            || queue.FailureCount > 0
            || PrinterQueueIsOverdue(queue, today);
    }

    private static bool PrinterQueueIsOverdue(PrinterQueueItem queue, DateTime today)
    {
        if (queue.EstimatedFinish.HasValue && queue.EstimatedFinish.Value.Date < today)
        {
            return true;
        }

        return queue.ScheduledStart.HasValue
            && queue.ScheduledStart.Value.Date < today
            && !queue.StartedAt.HasValue;
    }

    private static bool IsClosedPrinterQueueStatus(string? status)
    {
        return status is not null
            && (status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Done", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase));
    }

    private static string PrinterQueueEvidence(PrinterQueueItem queue, DateTime today)
    {
        var status = queue.Status ?? string.Empty;
        var reasons = new List<string>();
        if (queue.NeedsReview)
        {
            reasons.Add("needs review");
        }

        if (status.Equals("Needs Attention", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("needs attention");
        }
        else if (status.Contains("Fail", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add(status);
        }

        if (queue.FailureCount > 0)
        {
            reasons.Add(queue.FailureCount == 1 ? "1 failed attempt" : $"{queue.FailureCount} failed attempts");
        }

        if (PrinterQueueIsOverdue(queue, today))
        {
            reasons.Add("overdue");
        }

        var reasonText = reasons.Count > 0 ? $" ({string.Join(", ", reasons)})" : string.Empty;
        return $"{queue.PrinterName ?? "Unassigned printer"}: {queue.JobName}{reasonText}";
    }
}
