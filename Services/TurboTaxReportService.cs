using System.Globalization;
using System.Net;
using System.Text;
using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Services;

public sealed class TurboTaxReportService(AppDbContext db)
{
    public async Task<TaxYearSetup> GetSetupAsync(int year, CancellationToken cancellationToken = default)
    {
        var row = await db.TaxYearSetups.AsNoTracking()
            .FirstOrDefaultAsync(x => !x.IsArchived && x.TaxYear == year, cancellationToken);
        return row ?? new TaxYearSetup { TaxYear = year };
    }

    public async Task<TaxYearSetup> SaveSetupAsync(TaxYearSetup input, CancellationToken cancellationToken = default)
    {
        var year = input.TaxYear is >= 2000 and <= 2200 ? input.TaxYear : DateTime.Today.Year;
        var existing = await db.TaxYearSetups.FirstOrDefaultAsync(x => x.TaxYear == year, cancellationToken);
        input.Id = existing?.Id ?? 0;
        input.TaxYear = year;
        input.CreatedAtUtc = existing?.CreatedAtUtc ?? DateTime.UtcNow;
        input.UpdatedAtUtc = DateTime.UtcNow;
        input.IsArchived = false;
        NormalizeSetup(input);

        if (existing is null)
        {
            db.TaxYearSetups.Add(input);
            existing = input;
        }
        else
        {
            db.Entry(existing).CurrentValues.SetValues(input);
        }

        await db.SaveChangesAsync(cancellationToken);
        return existing;
    }

    public async Task<TurboTaxReport> BuildReportAsync(int year, CancellationToken cancellationToken = default)
    {
        var setup = await GetSetupAsync(year, cancellationToken);
        var sales = await db.Sales.AsNoTracking()
            .Where(x => !x.IsArchived && x.SaleDate.HasValue && x.SaleDate.Value.Year == year)
            .OrderBy(x => x.SaleDate)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var reportableSales = sales.Where(MoneyRules.IsReportableSale).ToList();
        var incidents = await db.OrderLossIncidents.AsNoTracking()
            .Where(x => !x.IsArchived && x.IncidentDate.HasValue && x.IncidentDate.Value.Year == year)
            .OrderBy(x => x.IncidentDate)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var countedIncidents = incidents.Where(x => x.CountInTaxReports).ToList();
        var expenses = await db.Expenses.AsNoTracking()
            .Where(x => !x.IsArchived && x.ExpenseDate.HasValue && x.ExpenseDate.Value.Year == year)
            .OrderBy(x => x.ExpenseDate)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var bills = await db.Bills.AsNoTracking()
            .Where(x => !x.IsArchived
                && ((x.PaymentDate.HasValue && x.PaymentDate.Value.Year == year)
                    || (!x.PaymentDate.HasValue && x.BillDate.HasValue && x.BillDate.Value.Year == year)))
            .OrderBy(x => x.PaymentDate ?? x.BillDate)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var assets = await db.Assets.AsNoTracking()
            .Where(x => !x.IsArchived
                && ((x.InServiceDate.HasValue && x.InServiceDate.Value.Year == year)
                    || (!x.InServiceDate.HasValue && x.PurchaseDate.HasValue && x.PurchaseDate.Value.Year == year)))
            .OrderBy(x => x.InServiceDate ?? x.PurchaseDate)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var mileage = await db.MileageLogs.AsNoTracking()
            .Where(x => !x.IsArchived && x.TripDate.HasValue && x.TripDate.Value.Year == year)
            .OrderBy(x => x.TripDate)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var rewards = await db.MakerWorldRewards.AsNoTracking()
            .Where(x => !x.IsArchived && x.RewardDate.HasValue && x.RewardDate.Value.Year == year)
            .OrderBy(x => x.RewardDate)
            .ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var settings = await db.AppSettings.AsNoTracking()
            .Where(x => x.Key.StartsWith("TaxProfile:"))
            .ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);

        var issues = new List<TaxReadinessIssue>();
        var incomeDetails = BuildIncomeDetails(reportableSales, countedIncidents, rewards, setup);
        var expenseDetails = new List<TaxExpenseDetailRow>();
        var lineAmounts = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var sale in reportableSales)
        {
            AddExpenseDetail(expenseDetails, lineAmounts, sale.SaleDate, "Sale", SaleReference(sale), sale.Platform,
                "Marketplace and payment processing fees", "10", "Commissions and fees", sale.PlatformFees ?? 0,
                sale.PlatformFees ?? 0, sale.SourceProof, sale.NeedsReview, sale.Notes);
            AddExpenseDetail(expenseDetails, lineAmounts, sale.SaleDate, "Sale", SaleReference(sale), sale.Platform,
                "Original outbound shipping label", "27b", "Shipping and postage", sale.ShippingLabelCost ?? 0,
                sale.ShippingLabelCost ?? 0, sale.SourceProof, sale.NeedsReview, sale.Notes);
        }

        foreach (var incident in incidents)
        {
            var count = incident.CountInTaxReports;
            AddExpenseDetail(expenseDetails, lineAmounts, incident.IncidentDate, "Order loss", LossReference(incident),
                incident.Platform, "Replacement item direct cost", "4", "Replacement COGS",
                incident.ReplacementCogs ?? 0, count && IsPerSaleCogs(setup) ? incident.ReplacementCogs ?? 0 : 0,
                incident.SourceProof, incident.NeedsReview || !count, incident.Notes);
            AddExpenseDetail(expenseDetails, lineAmounts, incident.IncidentDate, "Order loss", LossReference(incident),
                incident.Platform, "Replacement / reship postage", "27b", "Shipping and postage",
                incident.AdditionalShippingCost ?? 0, count ? incident.AdditionalShippingCost ?? 0 : 0,
                incident.SourceProof, incident.NeedsReview || !count, incident.Notes);
            AddExpenseDetail(expenseDetails, lineAmounts, incident.IncidentDate, "Order loss", LossReference(incident),
                incident.Platform, "Other damaged/lost-order cost", "27b", "Order loss and replacement costs",
                incident.OtherCost ?? 0, count ? incident.OtherCost ?? 0 : 0,
                incident.SourceProof, incident.NeedsReview || !count, incident.Notes);
        }

        foreach (var expense in expenses)
        {
            AddLedgerExpense(expenseDetails, lineAmounts, setup, issues, expense);
        }

        foreach (var bill in bills)
        {
            AddBillExpense(expenseDetails, lineAmounts, setup, issues, bill);
        }

        foreach (var asset in assets)
        {
            AddAssetExpense(expenseDetails, lineAmounts, issues, asset);
        }

        AddVehicleExpenses(expenseDetails, lineAmounts, setup, settings, mileage, expenses, issues, year);

        var line1 = incomeDetails.Sum(x => x.GrossReceipts);
        var line2 = incomeDetails.Sum(x => x.ReturnsAndAllowances);
        var line6 = incomeDetails.Sum(x => x.OtherIncome);
        var line4 = CalculateCogs(setup, reportableSales, countedIncidents, issues);
        lineAmounts["4"] = line4;

        AddSetupIssues(setup, settings, reportableSales, incidents, mileage, line1, issues);

        var part2Total = ScheduleCPart2Lines.Sum(line => lineAmounts.GetValueOrDefault(line));
        var line3 = line1 - line2;
        var line5 = line3 - line4;
        var line7 = line5 + line6;
        var line29 = line7 - part2Total;
        var line30 = Math.Max(0, setup.HomeOfficeDeduction);
        var line31 = line29 - line30;

        var scheduleLines = BuildScheduleLines(lineAmounts, setup, line1, line2, line3, line4, line5, line6, line7, part2Total, line29, line30, line31);
        var interview = BuildInterviewAnswers(setup, settings, mileage);
        var filingAssumption = settings.GetValueOrDefault("TaxProfile:EntityType") ?? "Not confirmed";
        if (!filingAssumption.Contains("Schedule C", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("BLOCKER", "Filing classification",
                $"The saved filing treatment is '{filingAssumption}'. This workbook is organized for Schedule C and may not be the correct return.",
                "Confirm the LLC's federal tax classification with the tax preparer before using the Schedule C totals."));
        }

        var orderedIssues = issues
            .DistinctBy(x => (x.Severity, x.Area, x.Message))
            .OrderBy(x => IssueOrder(x.Severity))
            .ThenBy(x => x.Area)
            .ToList();

        return new(
            year,
            filingAssumption,
            orderedIssues.All(x => !x.Severity.Equals("BLOCKER", StringComparison.OrdinalIgnoreCase)),
            line31,
            setup,
            interview,
            scheduleLines,
            incomeDetails,
            expenseDetails.OrderBy(x => x.Date).ThenBy(x => x.SourceType).ThenBy(x => x.Reference).ToList(),
            incidents,
            orderedIssues);
    }

    public async Task<string> BuildPrintableHtmlAsync(int year, CancellationToken cancellationToken = default)
    {
        var report = await BuildReportAsync(year, cancellationToken);
        var html = new StringBuilder();
        html.Append("""
            <!doctype html><html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1">
            <title>EPATA TurboTax & Schedule C Workbook</title>
            <style>
            :root{font-family:Arial,sans-serif;color:#172033;background:#fff}body{max-width:1100px;margin:0 auto;padding:32px;line-height:1.4}
            h1,h2{margin:.2rem 0 .7rem}h2{margin-top:2rem;border-bottom:2px solid #1f5f8b;padding-bottom:.35rem}p{margin:.45rem 0}
            .meta,.notice{padding:14px 16px;border:1px solid #b9c7d5;border-radius:8px;margin:14px 0}.notice{background:#f4f8fb}
            .bad{background:#fff0f0;border-color:#c84a4a}.good{background:#eef9f3;border-color:#3a8b62}
            table{width:100%;border-collapse:collapse;margin:10px 0 22px;font-size:12px}th,td{border:1px solid #cbd5df;padding:7px;text-align:left;vertical-align:top}
            th{background:#edf3f7}td.money{text-align:right;white-space:nowrap}.print{position:fixed;right:20px;top:20px;padding:10px 14px}
            @media print{body{max-width:none;padding:0}.print{display:none}h2{break-before:page}table{font-size:9px}tr{break-inside:avoid}.no-break{break-inside:avoid}}
            </style></head><body>
            <button class="print" onclick="window.print()">Print / Save PDF</button>
            """);
        html.Append($"<h1>EPATA {report.TaxYear} TurboTax &amp; Schedule C Workbook</h1>");
        html.Append($"<div class=\"notice {(report.ReadyForDataEntry ? "good" : "bad")}\"><strong>{(report.ReadyForDataEntry ? "READY FOR REVIEWED DATA ENTRY" : "NOT READY — CLEAR BLOCKERS FIRST")}</strong><p>This is an organized recordkeeping and tax-software entry workbook, not a filed return or tax advice. TurboTax screen wording can change; the line references follow the IRS 2026 draft Schedule C.</p></div>");
        html.Append($"<div class=\"meta\"><p><strong>Tax year:</strong> {report.TaxYear}</p><p><strong>Saved filing assumption:</strong> {H(report.FilingAssumption)}</p><p><strong>Working Schedule C net profit/(loss):</strong> {Money(report.ScheduleCNetProfitOrLoss)}</p></div>");

        AppendTable(html, "Readiness issues", ["Severity", "Area", "Issue", "Next action"], report.ReadinessIssues.Select(x => new[] { x.Severity, x.Area, x.Message, x.Action }));
        AppendTable(html, "TurboTax interview answers", ["Section", "Question", "Answer", "Status", "Source"], report.InterviewAnswers.Select(x => new[] { x.Section, x.Question, x.Answer, x.Status, x.Source }));
        AppendTable(html, "Schedule C line-by-line totals", ["Line", "Prompt / category", "Amount", "Status", "Source"], report.ScheduleCLines.Select(x => new[] { x.Line, x.Label, Money(x.Amount), x.Status, x.Source }));
        AppendTable(html, "Itemized income, refunds, and recoveries", ["Date", "Source", "Reference", "Description", "Gross receipts", "Returns / allowances", "Other income", "Proof", "Review"],
            report.IncomeDetails.Select(x => new[] { Date(x.Date), x.SourceType, x.Reference, x.Description, Money(x.GrossReceipts), Money(x.ReturnsAndAllowances), Money(x.OtherIncome), x.Proof ?? "", x.NeedsReview ? "YES" : "" }));
        AppendTable(html, "Itemized business costs", ["Date", "Source", "Reference", "Payee", "Description", "Schedule C", "Category", "Paid", "Deductible", "Proof", "Review"],
            report.ExpenseDetails.Select(x => new[] { Date(x.Date), x.SourceType, x.Reference, x.Payee, x.Description, x.ScheduleCLine, x.ScheduleCCategory, Money(x.PaidAmount), Money(x.DeductibleAmount), x.Proof ?? "", x.NeedsReview ? "YES" : "" }));
        AppendTable(html, "Damaged, lost, refunded, and replacement orders", ["Date", "Platform", "Order", "Customer", "Product", "Incident", "Resolution", "Refund", "Replacement COGS", "Extra shipping", "Other cost", "Recovery", "Net loss", "Counted", "Proof"],
            report.OrderLossIncidents.Select(x => new[] { Date(x.IncidentDate), x.Platform, x.OrderNumber ?? "", x.CustomerName ?? "", x.ProductName, x.IncidentType, x.Resolution, Money(x.CustomerRefund ?? 0), Money(x.ReplacementCogs ?? 0), Money(x.AdditionalShippingCost ?? 0), Money(x.OtherCost ?? 0), Money(x.ReimbursementReceived ?? 0), Money(x.NetLoss), x.CountInTaxReports ? "YES" : "MEMO ONLY", x.SourceProof ?? "" }));
        html.Append("<p>Keep the downloaded ZIP, original receipts/statements, shipment photos, customer messages, carrier claims, year-end inventory support, and the final filed return together.</p></body></html>");
        return html.ToString();
    }

    private static List<TaxIncomeDetailRow> BuildIncomeDetails(
        IReadOnlyList<Sale> sales,
        IReadOnlyList<OrderLossIncident> incidents,
        IReadOnlyList<MakerWorldReward> rewards,
        TaxYearSetup setup)
    {
        var rows = sales.Select(x => new TaxIncomeDetailRow(
            x.SaleDate,
            "Sale",
            SaleReference(x),
            $"{x.Platform}: {x.ProductName}",
            Math.Max(0, (x.ItemSales ?? 0) + (x.ShippingCharged ?? 0)),
            Math.Max(0, x.Refunds ?? 0),
            0,
            x.SourceProof,
            x.NeedsReview || string.IsNullOrWhiteSpace(x.SourceProof))).ToList();

        rows.AddRange(incidents.Select(x => new TaxIncomeDetailRow(
            x.IncidentDate,
            "Order loss",
            LossReference(x),
            $"{x.IncidentType}; {x.Resolution}",
            0,
            Math.Max(0, x.CustomerRefund ?? 0),
            Math.Max(0, x.ReimbursementReceived ?? 0),
            x.SourceProof,
            x.NeedsReview || string.IsNullOrWhiteSpace(x.SourceProof))));

        rows.AddRange(rewards.Where(MoneyRules.IsMakerWorldIncome).Select(x => new TaxIncomeDetailRow(
            x.RewardDate,
            "Reward income",
            $"MakerWorld reward #{x.Id}",
            x.RewardType,
            0,
            0,
            MoneyRules.MakerWorldIncomeAmount(x),
            x.SourceProof,
            x.NeedsReview || string.IsNullOrWhiteSpace(x.SourceProof))));

        if (setup.OtherBusinessIncome > 0)
        {
            rows.Add(new(setup.TaxYear == DateTime.Today.Year ? DateTime.Today : new DateTime(setup.TaxYear, 12, 31),
                "Year-end setup", "Other business income", "Other business income entered in TurboTax setup", 0, 0,
                setup.OtherBusinessIncome, null, true));
        }

        return rows.OrderBy(x => x.Date).ThenBy(x => x.SourceType).ThenBy(x => x.Reference).ToList();
    }

    private static void AddLedgerExpense(
        List<TaxExpenseDetailRow> details,
        Dictionary<string, decimal> lineAmounts,
        TaxYearSetup setup,
        List<TaxReadinessIssue> issues,
        Expense expense)
    {
        var paid = Math.Max(0, expense.Total ?? expense.Amount ?? 0);
        var counted = MoneyRules.IsTaxCountedExpense(expense);
        var amount = counted ? MoneyRules.TaxCountedExpenseAmount(expense) : 0;
        var mapping = MapTaxCategory(expense.TaxCategory, expense.Category, setup);
        if (mapping.Excluded) amount = 0;
        if (mapping.Line == "9" && !setup.VehicleDeductionMethod.Contains("Actual", StringComparison.OrdinalIgnoreCase)) amount = 0;
        if (mapping.Review)
        {
            issues.Add(new("BLOCKER", "Expense categories", $"Expense #{expense.Id} has an ambiguous or review-only tax category: '{expense.TaxCategory}'.", "Open Expenses and choose the exact TurboTax/Schedule C category."));
        }
        if (mapping.Line == "24b" && amount > 0) amount = decimal.Round(amount * .5m, 2);
        AddExpenseDetail(details, lineAmounts, expense.ExpenseDate, "Expense", $"Expense #{expense.Id}", expense.VendorName,
            expense.Description, mapping.Line, mapping.Category, paid, amount, expense.ReceiptProof,
            expense.NeedsReview || mapping.Review || !counted, expense.Notes);
    }

    private static void AddBillExpense(
        List<TaxExpenseDetailRow> details,
        Dictionary<string, decimal> lineAmounts,
        TaxYearSetup setup,
        List<TaxReadinessIssue> issues,
        Bill bill)
    {
        var paid = MoneyRules.PaidBillAmount(bill);
        var counted = MoneyRules.IsTaxCountedBill(bill);
        var mapping = MapTaxCategory(bill.TaxCategory, bill.Category, setup);
        var amount = counted && !mapping.Excluded ? paid : 0;
        if (mapping.Line == "9" && !setup.VehicleDeductionMethod.Contains("Actual", StringComparison.OrdinalIgnoreCase)) amount = 0;
        if (mapping.Review)
        {
            issues.Add(new("BLOCKER", "Bill categories", $"Bill #{bill.Id} has an ambiguous or review-only tax category: '{bill.TaxCategory}'.", "Open AP Bills and choose the exact TurboTax/Schedule C category."));
        }
        if (mapping.Line == "24b" && amount > 0) amount = decimal.Round(amount * .5m, 2);
        AddExpenseDetail(details, lineAmounts, bill.PaymentDate ?? bill.BillDate, "Paid bill", bill.BillNumber ?? $"Bill #{bill.Id}", bill.VendorName,
            bill.Description, mapping.Line, mapping.Category, paid, amount, bill.SourceProof,
            bill.NeedsReview || mapping.Review || !counted, bill.Notes);
    }

    private static void AddAssetExpense(
        List<TaxExpenseDetailRow> details,
        Dictionary<string, decimal> lineAmounts,
        List<TaxReadinessIssue> issues,
        Asset asset)
    {
        var paid = Math.Max(0, asset.Cost ?? 0);
        var businessAmount = paid * (Math.Clamp(asset.BusinessUsePercent ?? 100, 0, 100) / 100m);
        var treatment = asset.TaxTreatment ?? "Review";
        var line = "Review";
        var category = "Asset / depreciation review";
        var deductible = 0m;
        if (asset.CountedExpenseThisYear && treatment.Equals("Section 179", StringComparison.OrdinalIgnoreCase))
        {
            line = "13";
            category = "Section 179 expense";
            deductible = businessAmount;
        }
        else if (asset.CountedExpenseThisYear && treatment.Equals("De Minimis Expense", StringComparison.OrdinalIgnoreCase))
        {
            line = "27b";
            category = "De minimis safe harbor";
            deductible = businessAmount;
        }
        else if (treatment.Equals("Not Deductible", StringComparison.OrdinalIgnoreCase))
        {
            line = "Excluded";
            category = "Not deductible";
        }
        else
        {
            issues.Add(new("BLOCKER", "Assets and depreciation", $"Asset #{asset.Id} ({asset.Name}) needs a current-year depreciation/expensing decision.", "Confirm the asset treatment and deductible amount in TurboTax or with the preparer; do not guess from purchase price alone."));
        }

        AddExpenseDetail(details, lineAmounts, asset.InServiceDate ?? asset.PurchaseDate, "Asset", $"Asset #{asset.Id}", asset.VendorName ?? "",
            asset.Name, line, category, paid, deductible, asset.SourceProof, asset.NeedsReview || deductible == 0, asset.Notes);
    }

    private static void AddVehicleExpenses(
        List<TaxExpenseDetailRow> details,
        Dictionary<string, decimal> lineAmounts,
        TaxYearSetup setup,
        IReadOnlyDictionary<string, string?> settings,
        IReadOnlyList<MileageLog> mileage,
        IReadOnlyList<Expense> expenses,
        List<TaxReadinessIssue> issues,
        int year)
    {
        var miles = mileage.Sum(x => Math.Max(0, x.BusinessMiles ?? 0));
        var parkingTolls = mileage.Sum(x => Math.Max(0, x.ParkingAndTolls ?? 0));
        var standard = setup.VehicleDeductionMethod.Contains("Standard", StringComparison.OrdinalIgnoreCase);
        var actual = setup.VehicleDeductionMethod.Contains("Actual", StringComparison.OrdinalIgnoreCase);
        var rateFallback = decimal.TryParse(settings.GetValueOrDefault("TaxProfile:BusinessMileageRate"), NumberStyles.Number, CultureInfo.InvariantCulture, out var configuredRate)
            ? configuredRate
            : 0;
        var mileageDeduction = standard
            ? mileage.Sum(x => Math.Max(0, x.BusinessMiles ?? 0) * MileageRate(x.TripDate, year, rateFallback))
            : 0;

        if (miles > 0 && !standard && !actual && !setup.VehicleDeductionMethod.Contains("No vehicle", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("BLOCKER", "Vehicle deduction", $"{miles:N1} business miles are logged, but the vehicle deduction method is not confirmed.", "Choose Standard mileage, Actual expenses, or No vehicle deduction in the TurboTax setup."));
        }

        if (standard && expenses.Any(x => MoneyRules.IsTaxCountedExpense(x) && x.TaxCategory.Equals("Car and truck expenses", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new("BLOCKER", "Vehicle duplicate", "Standard mileage is selected, but counted car/truck expense rows also exist.", "Exclude actual vehicle operating costs from the tax report or change to the actual-expense method; parking and tolls may remain separate."));
        }

        AddExpenseDetail(details, lineAmounts, year == DateTime.Today.Year ? DateTime.Today : new DateTime(year, 12, 31), "Mileage log", $"{year} mileage",
            "", standard ? "Standard mileage deduction" : "Mileage log (support only)", "9", "Car and truck expenses", miles,
            decimal.Round(mileageDeduction, 2), mileage.FirstOrDefault()?.ProofReference, mileage.Any(x => x.NeedsReview),
            standard ? "2026 uses 72.5 cents through June 30 and 76 cents beginning July 1; other years use the saved rate." : "Mileage is not deducted unless Standard mileage is selected.");
        AddExpenseDetail(details, lineAmounts, year == DateTime.Today.Year ? DateTime.Today : new DateTime(year, 12, 31), "Mileage log", $"{year} parking/tolls",
            "", "Business parking and tolls", "9", "Car and truck expenses", parkingTolls, parkingTolls,
            mileage.FirstOrDefault()?.ProofReference, mileage.Any(x => x.NeedsReview), "Business parking and tolls are separately deductible; commuting parking is not included.");
    }

    private static decimal CalculateCogs(
        TaxYearSetup setup,
        IReadOnlyList<Sale> sales,
        IReadOnlyList<OrderLossIncident> incidents,
        List<TaxReadinessIssue> issues)
    {
        if (IsPerSaleCogs(setup))
        {
            return sales.Sum(x => Math.Max(0, x.EstimatedCogs ?? 0))
                + incidents.Sum(x => Math.Max(0, x.ReplacementCogs ?? 0));
        }

        if (IsInventoryCogs(setup))
        {
            return Math.Max(0,
                setup.BeginningInventory
                + setup.PurchasesLessPersonalUse
                + setup.CostOfLabor
                + setup.MaterialsAndSupplies
                + setup.OtherCogsCosts
                - setup.EndingInventory);
        }

        if (IsSuppliesMethod(setup))
        {
            return 0;
        }

        issues.Add(new("BLOCKER", "Cost of goods sold", "The COGS method is not confirmed, so per-sale estimates and paid material purchases cannot be safely combined.", "Choose one year-end COGS method in TurboTax setup to prevent double counting."));
        return 0;
    }

    private static List<ScheduleCLineRow> BuildScheduleLines(
        IReadOnlyDictionary<string, decimal> amounts,
        TaxYearSetup setup,
        decimal line1,
        decimal line2,
        decimal line3,
        decimal line4,
        decimal line5,
        decimal line6,
        decimal line7,
        decimal line28,
        decimal line29,
        decimal line30,
        decimal line31)
    {
        var rows = new List<ScheduleCLineRow>
        {
            Line("1", "Gross receipts or sales", line1, "Sales ledger before refunds"),
            Line("2", "Returns and allowances", line2, "Sale refunds plus counted order-loss refunds"),
            Line("3", "Net receipts (line 1 minus line 2)", line3, "Calculated"),
            Line("4", "Cost of goods sold", line4, setup.CogsMethod),
            Line("5", "Gross profit", line5, "Calculated"),
            Line("6", "Other business income", line6, "Rewards, recoveries, and year-end setup"),
            Line("7", "Gross income", line7, "Calculated")
        };

        foreach (var (line, label) in ScheduleCExpenseLabels)
        {
            rows.Add(Line(line, label, amounts.GetValueOrDefault(line), "Itemized business costs"));
        }

        rows.Add(Line("28", "Total expenses before home office", line28, "Calculated"));
        rows.Add(Line("29", "Tentative profit or (loss)", line29, "Calculated"));
        rows.Add(Line("30", "Business use of home", line30, setup.HomeOfficeMethod));
        rows.Add(Line("31", "Net profit or (loss)", line31, "Calculated"));
        rows.Add(Line("35", "Inventory at beginning of year", setup.BeginningInventory, "TurboTax year setup"));
        rows.Add(Line("36", "Purchases less items withdrawn for personal use", setup.PurchasesLessPersonalUse, "TurboTax year setup"));
        rows.Add(Line("37", "Cost of labor", setup.CostOfLabor, "TurboTax year setup"));
        rows.Add(Line("38", "Materials and supplies", setup.MaterialsAndSupplies, "TurboTax year setup"));
        rows.Add(Line("39", "Other COGS costs", setup.OtherCogsCosts, "TurboTax year setup"));
        rows.Add(Line("40", "Total of lines 35 through 39", setup.BeginningInventory + setup.PurchasesLessPersonalUse + setup.CostOfLabor + setup.MaterialsAndSupplies + setup.OtherCogsCosts, "Calculated"));
        rows.Add(Line("41", "Inventory at end of year", setup.EndingInventory, "TurboTax year setup"));
        rows.Add(Line("42", "Cost of goods sold", line4, setup.CogsMethod));
        return rows;
    }

    private static List<TurboTaxInterviewAnswerRow> BuildInterviewAnswers(
        TaxYearSetup setup,
        IReadOnlyDictionary<string, string?> settings,
        IReadOnlyList<MileageLog> mileage)
    {
        var rows = new List<TurboTaxInterviewAnswerRow>();
        void Add(string section, string question, string? answer, string source = "TurboTax year setup")
        {
            var value = string.IsNullOrWhiteSpace(answer) ? "NEEDS INPUT" : answer.Trim();
            var status = value.Contains("Review", StringComparison.OrdinalIgnoreCase) || value == "NEEDS INPUT" || value.StartsWith("Not confirmed", StringComparison.OrdinalIgnoreCase)
                ? "NEEDS INPUT"
                : "READY";
            rows.Add(new(section, question, value, status, source));
        }

        Add("Business profile", "Business / activity name", setup.BusinessName);
        Add("Business profile", "Principal business or profession", setup.PrincipalBusinessActivity);
        Add("Business profile", "Six-digit principal business code", setup.PrincipalBusinessCode);
        Add("Business profile", "Employer ID number (if the business has one)", setup.Ein ?? "Not entered");
        Add("Business profile", "Business address", setup.BusinessAddress);
        Add("Business profile", "Accounting method", setup.AccountingMethod);
        Add("Business profile", "Did you materially participate?", setup.MateriallyParticipated);
        Add("Business profile", $"Did you start or acquire the business in {setup.TaxYear}?", setup.StartedOrAcquiredThisYear);
        Add("Information returns", "Did you make payments that may require Forms 1099?", setup.MadeReportablePayments);
        Add("Information returns", "If yes, did or will you file the required Forms 1099?", setup.FiledRequired1099s);
        Add("Loss limitation", "If Schedule C has a loss, is all investment at risk?", setup.AtRiskStatus);
        Add("Income reconciliation", "1099-K total (do not add again to sales)", setup.Form1099KTotal.ToString("F2", CultureInfo.InvariantCulture));
        Add("Income reconciliation", "1099-NEC total (do not add again if already in sales)", setup.Form1099NecTotal.ToString("F2", CultureInfo.InvariantCulture));
        Add("Income reconciliation", "1099-MISC total (do not add again if already in sales)", setup.Form1099MiscTotal.ToString("F2", CultureInfo.InvariantCulture));
        Add("COGS / inventory", "Year-end COGS method", setup.CogsMethod);
        Add("COGS / inventory", "Closing inventory valuation method", setup.InventoryValuationMethod);
        Add("COGS / inventory", "Did the inventory method change?", setup.InventoryMethodChanged);
        Add("Vehicle", "Vehicle deduction method", setup.VehicleDeductionMethod);
        Add("Vehicle", "Date vehicle was placed in service", Date(setup.VehiclePlacedInService));
        Add("Vehicle", "Business miles", mileage.Sum(x => Math.Max(0, x.BusinessMiles ?? 0)).ToString("F1", CultureInfo.InvariantCulture), "Mileage log");
        Add("Vehicle", "Commuting miles", setup.CommutingMiles.ToString("F1", CultureInfo.InvariantCulture));
        Add("Vehicle", "Other personal miles", setup.OtherPersonalMiles.ToString("F1", CultureInfo.InvariantCulture));
        Add("Vehicle", "Was the vehicle available for personal use during off-duty hours?", setup.VehicleAvailableForPersonalUse);
        Add("Vehicle", "Was another vehicle available for personal use?", setup.AnotherVehicleAvailable);
        Add("Vehicle", "Do you have evidence supporting the deduction?", setup.VehicleEvidence);
        Add("Vehicle", "Is the vehicle evidence written?", setup.VehicleEvidenceWritten);
        Add("Home office", "Home-office method", setup.HomeOfficeMethod);
        Add("Home office", "Total home square feet", setup.HomeSquareFeet.ToString("F0", CultureInfo.InvariantCulture));
        Add("Home office", "Business-use square feet", setup.OfficeSquareFeet.ToString("F0", CultureInfo.InvariantCulture));
        Add("Tax profile", "Federal filing treatment", settings.GetValueOrDefault("TaxProfile:EntityType"), "Tax Setup profile");
        Add("Tax profile", "Materials / inventory treatment", settings.GetValueOrDefault("TaxProfile:InventoryMethod"), "Tax Setup profile");
        Add("Tax profile", "Employees?", settings.GetValueOrDefault("TaxProfile:HasEmployees"), "Tax Setup profile");
        Add("Tax profile", "Pays contractors?", settings.GetValueOrDefault("TaxProfile:PaysContractors"), "Tax Setup profile");
        return rows;
    }

    private static void AddSetupIssues(
        TaxYearSetup setup,
        IReadOnlyDictionary<string, string?> settings,
        IReadOnlyList<Sale> sales,
        IReadOnlyList<OrderLossIncident> incidents,
        IReadOnlyList<MileageLog> mileage,
        decimal line1,
        List<TaxReadinessIssue> issues)
    {
        void Required(string area, string value, string action)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Contains("Review", StringComparison.OrdinalIgnoreCase) || value.StartsWith("Not confirmed", StringComparison.OrdinalIgnoreCase))
                issues.Add(new("BLOCKER", area, $"A TurboTax interview answer is missing or unconfirmed: {area}.", action));
        }

        Required("Principal business code", setup.PrincipalBusinessCode, "Enter the six-digit business activity code after confirming it with the preparer or current IRS instructions.");
        Required("Business address", setup.BusinessAddress, "Enter the business address used on Schedule C.");
        Required("Material participation", setup.MateriallyParticipated, "Answer the Schedule C material-participation question.");
        Required("Business start question", setup.StartedOrAcquiredThisYear, $"Confirm whether the business started or was acquired in {setup.TaxYear}.");
        Required("1099 payment question", setup.MadeReportablePayments, "Confirm whether business payments require information returns.");

        var formTotal = setup.Form1099KTotal + setup.Form1099NecTotal + setup.Form1099MiscTotal;
        if (formTotal > 0 && Math.Abs(formTotal - line1) > 1)
        {
            issues.Add(new("REVIEW", "1099 reconciliation", $"Entered 1099 totals are {Money(formTotal)} while gross receipts before refunds are {Money(line1)}.", "Reconcile the difference without adding the same income twice; marketplace forms can use gross reporting conventions that differ from the ledger."));
        }

        foreach (var incident in incidents.Where(x => x.CountInTaxReports && (x.CustomerRefund ?? 0) > 0))
        {
            var sale = sales.FirstOrDefault(x => (incident.SaleId.HasValue && x.Id == incident.SaleId.Value)
                || (!string.IsNullOrWhiteSpace(incident.OrderNumber) && x.OrderNumber == incident.OrderNumber));
            if ((sale?.Refunds ?? 0) > 0)
            {
                issues.Add(new("BLOCKER", "Duplicate refund", $"{LossReference(incident)} has a refund on both the Sale and the order-loss record.", "Keep the refund in one place, or turn off Count in Tax Reports on the memo-only loss record."));
            }
        }

        if (incidents.Any(x => x.NeedsReview || string.IsNullOrWhiteSpace(x.SourceProof)))
        {
            issues.Add(new("REVIEW", "Order-loss proof", "One or more damaged/lost-order records need review or supporting proof.", "Attach the order, customer message, damage photo, replacement label, refund, and carrier claim/recovery where available."));
        }

        if (mileage.Any(x => x.NeedsReview || string.IsNullOrWhiteSpace(x.ProofReference)))
        {
            issues.Add(new("REVIEW", "Mileage support", "One or more mileage rows need review or a proof reference.", "Keep written trip date, destination, business purpose, miles, and supporting calendar/map records."));
        }

        if (settings.GetValueOrDefault("TaxProfile:InventoryMethod")?.Contains("Not confirmed", StringComparison.OrdinalIgnoreCase) != false)
        {
            issues.Add(new("BLOCKER", "Inventory treatment", "The general Tax Setup still has materials/inventory treatment unconfirmed.", "Confirm whether filament, hardware, packaging, and finished goods are treated as supplies, per-sale COGS, or inventory."));
        }
    }

    private static (string Line, string Category, bool Review, bool Excluded) MapTaxCategory(string? taxCategory, string? fallbackCategory, TaxYearSetup setup)
    {
        var category = (taxCategory ?? string.Empty).Trim();
        if (category.Equals("COGS / materials", StringComparison.OrdinalIgnoreCase) || category.Equals("COGS/Materials", StringComparison.OrdinalIgnoreCase))
        {
            if (IsSuppliesMethod(setup)) return ("22", "Supplies (materials method)", false, false);
            return ("COGS support", "Materials / COGS support — excluded from Part II", !IsInventoryCogs(setup) && !IsPerSaleCogs(setup), true);
        }

        return category.ToLowerInvariant() switch
        {
            "advertising" => ("8", "Advertising", false, false),
            "car and truck expenses" => ("9", "Car and truck expenses", false, false),
            "commissions and fees" => ("10", "Commissions and fees", false, false),
            "contract labor" => ("11", "Contract labor", false, false),
            "depletion" => ("12", "Depletion", false, false),
            "depreciation and section 179" => ("13", "Depreciation and section 179", false, false),
            "employee benefit programs" => ("14", "Employee benefit programs", false, false),
            "insurance" or "insurance (other than health)" => ("15", "Insurance (other than health)", false, false),
            "interest - mortgage" => ("16a", "Mortgage interest", false, false),
            "interest - vehicle loan" => ("16b", "Vehicle loan interest", false, false),
            "interest - other" => ("16c", "Other interest", false, false),
            "interest" => ("16c", "Other interest — confirm subtype", true, false),
            "legal and professional services" => ("17", "Legal and professional services", false, false),
            "office expense" => ("18", "Office expense", false, false),
            "pension and profit-sharing" or "pension and profit-sharing plans" => ("19", "Pension and profit-sharing plans", false, false),
            "rent or lease - vehicles/equipment" => ("20a", "Rent/lease vehicles, machinery, equipment", false, false),
            "rent or lease - other property" => ("20b", "Rent/lease other business property", false, false),
            "rent or lease" => ("20b", "Rent or lease — confirm subtype", true, false),
            "repairs and maintenance" => ("21", "Repairs and maintenance", false, false),
            "supplies" => ("22", "Supplies", false, false),
            "taxes and licenses" => ("23", "Taxes and licenses", false, false),
            "travel" => ("24a", "Travel", false, false),
            "deductible meals" => ("24b", "Deductible meals (50% default)", false, false),
            "utilities" => ("25", "Utilities", false, false),
            "wages" => ("26", "Wages", false, false),
            "energy efficient commercial buildings deduction" => ("27a", "Energy efficient commercial buildings deduction", true, false),
            "other business expense" => ("27b", string.IsNullOrWhiteSpace(fallbackCategory) ? "Other business expense" : fallbackCategory, false, false),
            "review" or "" => ("Review", "Needs tax category", true, true),
            _ => ("27b", category, true, false)
        };
    }

    private static void AddExpenseDetail(
        List<TaxExpenseDetailRow> details,
        Dictionary<string, decimal> lineAmounts,
        DateTime? date,
        string sourceType,
        string reference,
        string payee,
        string description,
        string line,
        string category,
        decimal paid,
        decimal deductible,
        string? proof,
        bool needsReview,
        string? notes)
    {
        paid = Math.Max(0, paid);
        deductible = Math.Max(0, deductible);
        if (paid == 0 && deductible == 0) return;
        details.Add(new(date, sourceType, reference, payee, description, line, category, paid, deductible, proof, needsReview, notes));
        if (ScheduleCPart2Lines.Contains(line))
        {
            lineAmounts[line] = lineAmounts.GetValueOrDefault(line) + deductible;
        }
    }

    private static void NormalizeSetup(TaxYearSetup setup)
    {
        setup.BusinessName = (setup.BusinessName ?? string.Empty).Trim();
        setup.PrincipalBusinessActivity = (setup.PrincipalBusinessActivity ?? string.Empty).Trim();
        setup.PrincipalBusinessCode = new string((setup.PrincipalBusinessCode ?? string.Empty).Where(char.IsDigit).Take(6).ToArray());
        setup.Ein = string.IsNullOrWhiteSpace(setup.Ein) ? null : setup.Ein.Trim();
        setup.BusinessAddress = (setup.BusinessAddress ?? string.Empty).Trim();
        setup.BeginningInventory = Math.Max(0, setup.BeginningInventory);
        setup.PurchasesLessPersonalUse = Math.Max(0, setup.PurchasesLessPersonalUse);
        setup.CostOfLabor = Math.Max(0, setup.CostOfLabor);
        setup.MaterialsAndSupplies = Math.Max(0, setup.MaterialsAndSupplies);
        setup.OtherCogsCosts = Math.Max(0, setup.OtherCogsCosts);
        setup.EndingInventory = Math.Max(0, setup.EndingInventory);
        setup.Form1099KTotal = Math.Max(0, setup.Form1099KTotal);
        setup.Form1099NecTotal = Math.Max(0, setup.Form1099NecTotal);
        setup.Form1099MiscTotal = Math.Max(0, setup.Form1099MiscTotal);
        setup.OtherBusinessIncome = Math.Max(0, setup.OtherBusinessIncome);
        setup.CommutingMiles = Math.Max(0, setup.CommutingMiles);
        setup.OtherPersonalMiles = Math.Max(0, setup.OtherPersonalMiles);
        setup.HomeSquareFeet = Math.Max(0, setup.HomeSquareFeet);
        setup.OfficeSquareFeet = Math.Min(setup.HomeSquareFeet, Math.Max(0, setup.OfficeSquareFeet));
        setup.HomeOfficeDeduction = Math.Max(0, setup.HomeOfficeDeduction);
    }

    private static decimal MileageRate(DateTime? tripDate, int year, decimal configuredRate)
    {
        if (year == 2026)
        {
            return tripDate is { Month: >= 7 } ? .76m : .725m;
        }
        return Math.Max(0, configuredRate);
    }

    private static bool IsPerSaleCogs(TaxYearSetup setup) => setup.CogsMethod.Contains("Per-sale", StringComparison.OrdinalIgnoreCase);
    private static bool IsInventoryCogs(TaxYearSetup setup) => setup.CogsMethod.Contains("inventory", StringComparison.OrdinalIgnoreCase);
    private static bool IsSuppliesMethod(TaxYearSetup setup) => setup.CogsMethod.Contains("Paid materials", StringComparison.OrdinalIgnoreCase);
    private static string SaleReference(Sale sale) => sale.OrderNumber ?? sale.InvoiceNumber ?? $"Sale #{sale.Id}";
    private static string LossReference(OrderLossIncident incident) => incident.OrderNumber ?? $"Order loss #{incident.Id}";
    private static ScheduleCLineRow Line(string line, string label, decimal amount, string source) => new(line, label, decimal.Round(amount, 2), "WORKING", source);
    private static string Date(DateTime? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
    private static string Money(decimal value) => value.ToString("C2", CultureInfo.GetCultureInfo("en-US"));
    private static string H(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static int IssueOrder(string severity) => severity.ToUpperInvariant() switch { "BLOCKER" => 0, "REVIEW" => 1, _ => 2 };

    private static void AppendTable(StringBuilder html, string title, IReadOnlyList<string> headers, IEnumerable<string[]> rows)
    {
        html.Append($"<h2>{H(title)}</h2><table><thead><tr>");
        foreach (var header in headers) html.Append($"<th>{H(header)}</th>");
        html.Append("</tr></thead><tbody>");
        var any = false;
        foreach (var row in rows)
        {
            any = true;
            html.Append("<tr>");
            foreach (var cell in row) html.Append($"<td>{H(cell)}</td>");
            html.Append("</tr>");
        }
        if (!any) html.Append($"<tr><td colspan=\"{headers.Count}\">No rows.</td></tr>");
        html.Append("</tbody></table>");
    }

    private static readonly string[] ScheduleCPart2Lines =
    ["8", "9", "10", "11", "12", "13", "14", "15", "16a", "16b", "16c", "17", "18", "19", "20a", "20b", "21", "22", "23", "24a", "24b", "25", "26", "27a", "27b"];

    private static readonly (string Line, string Label)[] ScheduleCExpenseLabels =
    [
        ("8", "Advertising"), ("9", "Car and truck expenses"), ("10", "Commissions and fees"),
        ("11", "Contract labor"), ("12", "Depletion"), ("13", "Depreciation and section 179"),
        ("14", "Employee benefit programs"), ("15", "Insurance (other than health)"),
        ("16a", "Mortgage interest"), ("16b", "Vehicle loan interest"), ("16c", "Other interest"),
        ("17", "Legal and professional services"), ("18", "Office expense"),
        ("19", "Pension and profit-sharing plans"), ("20a", "Rent/lease vehicles, machinery, equipment"),
        ("20b", "Rent/lease other business property"), ("21", "Repairs and maintenance"),
        ("22", "Supplies"), ("23", "Taxes and licenses"), ("24a", "Travel"),
        ("24b", "Deductible meals"), ("25", "Utilities"), ("26", "Wages"),
        ("27a", "Energy efficient commercial buildings deduction"), ("27b", "Other expenses")
    ];
}
