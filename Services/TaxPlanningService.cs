using System.IO.Compression;
using System.Text;
using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Services;

public sealed class TaxPlanningService(AppDbContext db)
{
    private const string FederalEstimatedUrl = "https://www.irs.gov/businesses/small-businesses-self-employed/estimated-taxes";
    private const string FederalSelfEmployedUrl = "https://www.irs.gov/businesses/small-businesses-self-employed/self-employed-individuals-tax-center";
    private const string NjEstimatedUrl = "https://www.nj.gov/treasury/taxation/njit20.shtml";
    private const string NjSalesTaxUrl = "https://www.nj.gov/treasury/taxation/su_12.shtml";
    private const string NjAnnualReportUrl = "https://www.njportal.com/DOR/annualreports/";

    public async Task<TaxProfile> GetProfileAsync()
    {
        var settings = await db.AppSettings.AsNoTracking()
            .Where(x => x.Key.StartsWith("TaxProfile:"))
            .ToDictionaryAsync(x => x.Key, x => x.Value);

        return new(
            settings.GetValueOrDefault("TaxProfile:EntityType") ?? "Not confirmed",
            settings.GetValueOrDefault("TaxProfile:State") ?? "New Jersey",
            int.TryParse(settings.GetValueOrDefault("TaxProfile:FormationMonth"), out var month) && month is >= 1 and <= 12 ? month : null,
            settings.GetValueOrDefault("TaxProfile:NjSalesTaxRegistration") ?? "Not confirmed",
            settings.GetValueOrDefault("TaxProfile:HasEmployees") ?? "Not confirmed",
            settings.GetValueOrDefault("TaxProfile:PaysContractors") ?? "Not confirmed",
            settings.GetValueOrDefault("TaxProfile:UsesVehicle") ?? "Not confirmed",
            settings.GetValueOrDefault("TaxProfile:HomeOffice") ?? "Not confirmed",
            settings.GetValueOrDefault("TaxProfile:InventoryMethod") ?? "Not confirmed",
            decimal.TryParse(settings.GetValueOrDefault("TaxProfile:BusinessMileageRate"), out var rate) && rate > 0 ? rate : null,
            settings.GetValueOrDefault("TaxProfile:Notes"));
    }

    public async Task<TaxProfile> SaveProfileAsync(TaxProfile profile)
    {
        await UpsertAsync("TaxProfile:EntityType", profile.EntityType);
        await UpsertAsync("TaxProfile:State", profile.State);
        await UpsertAsync("TaxProfile:FormationMonth", profile.FormationMonth?.ToString());
        await UpsertAsync("TaxProfile:NjSalesTaxRegistration", profile.NjSalesTaxRegistration);
        await UpsertAsync("TaxProfile:HasEmployees", profile.HasEmployees);
        await UpsertAsync("TaxProfile:PaysContractors", profile.PaysContractors);
        await UpsertAsync("TaxProfile:UsesVehicle", profile.UsesVehicle);
        await UpsertAsync("TaxProfile:HomeOffice", profile.HomeOffice);
        await UpsertAsync("TaxProfile:InventoryMethod", profile.InventoryMethod);
        await UpsertAsync("TaxProfile:BusinessMileageRate", profile.BusinessMileageRate?.ToString());
        await UpsertAsync("TaxProfile:Notes", profile.Notes);
        await db.SaveChangesAsync();
        return await GetProfileAsync();
    }

    public async Task<TaxSummaryRow> BuildSummaryAsync(int year)
    {
        var profile = await GetProfileAsync();
        var sales = await db.Sales.AsNoTracking().Where(x => !x.IsArchived && x.SaleDate.HasValue && x.SaleDate.Value.Year == year).ToListAsync();
        var reportableSales = sales.Where(MoneyRules.IsReportableSale).ToList();
        var expenses = await db.Expenses.AsNoTracking().Where(x => !x.IsArchived && x.ExpenseDate.HasValue && x.ExpenseDate.Value.Year == year).ToListAsync();
        var assets = await db.Assets.AsNoTracking().Where(x => !x.IsArchived && ((x.InServiceDate.HasValue && x.InServiceDate.Value.Year == year) || (!x.InServiceDate.HasValue && x.PurchaseDate.HasValue && x.PurchaseDate.Value.Year == year))).ToListAsync();
        var rewards = await db.MakerWorldRewards.AsNoTracking().Where(x => !x.IsArchived && x.RewardDate.HasValue && x.RewardDate.Value.Year == year).ToListAsync();
        var mileage = await db.MileageLogs.AsNoTracking().Where(x => !x.IsArchived && x.TripDate.HasValue && x.TripDate.Value.Year == year).ToListAsync();
        var obligations = await db.TaxObligations.AsNoTracking().Where(x => !x.IsArchived && x.TaxYear == year).ToListAsync();

        var gross = reportableSales.Sum(MoneyRules.SaleGrossReceipts);
        var marketplaceTax = reportableSales.Where(x => SalesTaxReviewBucket(x).StartsWith("Marketplace", StringComparison.OrdinalIgnoreCase)).Sum(MoneyRules.SaleSalesTaxMemo);
        var sellerTax = reportableSales.Where(x => SalesTaxReviewBucket(x) == "Seller Collected").Sum(MoneyRules.SaleSalesTaxMemo);
        var unknownTaxRows = reportableSales.Count(x => SalesTaxReviewBucket(x).Contains("Review", StringComparison.OrdinalIgnoreCase));
        var operating = expenses.Where(x => MoneyRules.IsTaxCountedExpense(x) && x.TaxBucket.Equals("Operating Expense", StringComparison.OrdinalIgnoreCase)).Sum(MoneyRules.TaxCountedExpenseAmount);
        var materials = expenses.Where(x => MoneyRules.IsTaxCountedExpense(x) && x.TaxBucket.Equals("COGS/Materials", StringComparison.OrdinalIgnoreCase)).Sum(MoneyRules.TaxCountedExpenseAmount);
        var expensedAssets = assets.Sum(MoneyRules.FullyExpensedAssetAmount);
        var rewardIncome = rewards.Sum(MoneyRules.MakerWorldIncomeAmount);
        var miles = mileage.Sum(x => x.BusinessMiles ?? 0);
        var mileageRate = profile.BusinessMileageRate ?? 0;
        var mileageDeduction = miles * mileageRate;
        var parkingTolls = mileage.Sum(x => x.ParkingAndTolls ?? 0);
        var platformCosts = reportableSales.Sum(x => (x.PlatformFees ?? 0) + (x.ShippingLabelCost ?? 0));
        var estimatedCogs = reportableSales.Sum(x => x.EstimatedCogs ?? 0);
        var workingProfit = gross + rewardIncome - platformCosts - estimatedCogs - operating - materials - expensedAssets - mileageDeduction - parkingTolls;
        var missingProof = reportableSales.Count(x => x.NeedsReview || string.IsNullOrWhiteSpace(x.SourceProof))
            + expenses.Count(x => x.NeedsReview || string.IsNullOrWhiteSpace(x.ReceiptProof))
            + assets.Count(x => x.NeedsReview || string.IsNullOrWhiteSpace(x.SourceProof))
            + mileage.Count(x => x.NeedsReview)
            + obligations.Count(x => x.NeedsReview && !x.Status.Equals("Filed / Paid", StringComparison.OrdinalIgnoreCase) && !x.Status.Equals("Not Required", StringComparison.OrdinalIgnoreCase));

        return new(
            year,
            gross,
            reportableSales.Sum(MoneyRules.SaleCustomerPaid),
            reportableSales.Sum(MoneyRules.SaleSalesTaxMemo),
            sellerTax,
            marketplaceTax,
            unknownTaxRows,
            platformCosts,
            estimatedCogs,
            operating,
            materials,
            expensedAssets,
            rewardIncome,
            miles,
            mileageRate,
            mileageDeduction,
            parkingTolls,
            workingProfit,
            workingProfit >= 400,
            missingProof);
    }

    public async Task<List<TaxObligation>> GenerateObligationsAsync(int year)
    {
        var profile = await GetProfileAsync();
        var templates = ObligationTemplates(year, profile);
        var existing = await db.TaxObligations.Where(x => !x.IsArchived && x.TaxYear == year).ToListAsync();
        foreach (var template in templates)
        {
            if (existing.Any(x => x.Title == template.Title && x.Period == template.Period)) continue;
            db.TaxObligations.Add(template);
        }

        await db.SaveChangesAsync();
        return await db.TaxObligations.AsNoTracking()
            .Where(x => !x.IsArchived && x.TaxYear == year)
            .OrderBy(x => x.DueDate == null)
            .ThenBy(x => x.DueDate)
            .ThenBy(x => x.Title)
            .ToListAsync();
    }

    public async Task<List<NjSalesTaxReviewRow>> BuildNjSalesTaxReviewAsync(int year, int? quarter = null)
    {
        var sales = await db.Sales.AsNoTracking()
            .Where(x => !x.IsArchived && x.IncludeInDashboard && x.SaleDate.HasValue && x.SaleDate.Value.Year == year)
            .OrderBy(x => x.SaleDate)
            .ThenBy(x => x.Id)
            .ToListAsync();

        return sales
            .Select(x => new NjSalesTaxReviewRow(
                x.Id,
                x.SaleDate,
                ((x.SaleDate!.Value.Month - 1) / 3) + 1,
                x.Platform,
                x.SalesTaxHandling,
                SalesTaxReviewBucket(x),
                x.OrderNumber,
                x.InvoiceNumber,
                x.CustomerName,
                MoneyRules.SaleGrossReceipts(x),
                MoneyRules.SaleSalesTaxMemo(x),
                x.SourceProof,
                x.NeedsReview,
                x.Notes))
            .Where(x => !quarter.HasValue || x.Quarter == quarter.Value)
            .ToList();
    }

    public async Task<byte[]> BuildTaxPackageAsync(int year)
    {
        var summary = await BuildSummaryAsync(year);
        var obligations = await db.TaxObligations.AsNoTracking().Where(x => !x.IsArchived && x.TaxYear == year).OrderBy(x => x.DueDate).ToListAsync();
        var sales = await db.Sales.AsNoTracking().Where(x => !x.IsArchived && x.SaleDate.HasValue && x.SaleDate.Value.Year == year).OrderBy(x => x.SaleDate).ToListAsync();
        var expenses = await db.Expenses.AsNoTracking().Where(x => !x.IsArchived && x.ExpenseDate.HasValue && x.ExpenseDate.Value.Year == year).OrderBy(x => x.ExpenseDate).ToListAsync();
        var assets = await db.Assets.AsNoTracking().Where(x => !x.IsArchived).OrderBy(x => x.PurchaseDate).ToListAsync();
        var mileage = await db.MileageLogs.AsNoTracking().Where(x => !x.IsArchived && x.TripDate.HasValue && x.TripDate.Value.Year == year).OrderBy(x => x.TripDate).ToListAsync();
        var rewards = await db.MakerWorldRewards.AsNoTracking().Where(x => !x.IsArchived && x.RewardDate.HasValue && x.RewardDate.Value.Year == year).OrderBy(x => x.RewardDate).ToListAsync();
        var docs = await db.AuditDocuments.AsNoTracking().Where(x => !x.IsArchived).OrderBy(x => x.DocumentDate).ToListAsync();
        var njSalesTax = await BuildNjSalesTaxReviewAsync(year);

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            AddText(zip, "README.txt", TaxPackageReadme(year));
            AddText(zip, "tax-summary.csv", CsvExportService.ToCsv(new[] { summary }));
            AddText(zip, "tax-obligations.csv", CsvExportService.ToCsv(obligations));
            AddText(zip, "sales.csv", CsvExportService.ToCsv(sales));
            AddText(zip, "nj-sales-tax-review.csv", CsvExportService.ToCsv(njSalesTax));
            AddText(zip, "expenses.csv", CsvExportService.ToCsv(expenses));
            AddText(zip, "assets.csv", CsvExportService.ToCsv(assets));
            AddText(zip, "mileage.csv", CsvExportService.ToCsv(mileage));
            AddText(zip, "makerworld-rewards.csv", CsvExportService.ToCsv(rewards));
            AddText(zip, "proof-index.csv", CsvExportService.ToCsv(docs));
        }

        return stream.ToArray();
    }

    private async Task UpsertAsync(string key, string? value)
    {
        var row = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key);
        if (row is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value, Notes = "Tax Setup profile value." });
        }
        else
        {
            row.Value = value;
        }
    }

    private static string SalesTaxReviewBucket(Sale sale)
    {
        var handling = sale.SalesTaxHandling ?? string.Empty;
        if (handling.Contains("Marketplace", StringComparison.OrdinalIgnoreCase)) return "Marketplace Collected / Remitted";
        if (handling.Contains("Seller Collected", StringComparison.OrdinalIgnoreCase)) return "Seller Collected";
        if (handling.Contains("Exempt", StringComparison.OrdinalIgnoreCase) || handling.Contains("Not Taxable", StringComparison.OrdinalIgnoreCase)) return "Exempt / Not Taxable";
        if (sale.Platform.Contains("Etsy", StringComparison.OrdinalIgnoreCase) || sale.Platform.Contains("Marketplace", StringComparison.OrdinalIgnoreCase))
            return "Marketplace - Confirm / Review";
        return "Seller / Taxability Review";
    }

    private static List<TaxObligation> ObligationTemplates(int year, TaxProfile profile)
    {
        var rows = new List<TaxObligation>();
        void Add(string title, string jurisdiction, string type, string form, string period, DateTime? due, string appliesIf, string url, string? notes = null)
        {
            rows.Add(new TaxObligation
            {
                TaxYear = year,
                Title = title,
                Jurisdiction = jurisdiction,
                ObligationType = type,
                FormName = form,
                Period = period,
                DueDate = due,
                Status = "Review Applicability",
                AppliesIf = appliesIf,
                OfficialUrl = url,
                Notes = notes,
                NeedsReview = true
            });
        }

        Add("Federal estimated tax payment", "Federal", "Estimated Income Tax", "1040-ES", "Q1", new DateTime(year, 4, 15), "Generally applies if you expect to owe at least $1,000 after withholding and refundable credits.", FederalEstimatedUrl);
        Add("Federal estimated tax payment", "Federal", "Estimated Income Tax", "1040-ES", "Q2", new DateTime(year, 6, 15), "Generally applies if you expect to owe at least $1,000 after withholding and refundable credits.", FederalEstimatedUrl);
        Add("Federal estimated tax payment", "Federal", "Estimated Income Tax", "1040-ES", "Q3", new DateTime(year, 9, 15), "Generally applies if you expect to owe at least $1,000 after withholding and refundable credits.", FederalEstimatedUrl);
        Add("Federal estimated tax payment", "Federal", "Estimated Income Tax", "1040-ES", "Q4", new DateTime(year + 1, 1, 15), "Generally applies if you expect to owe at least $1,000 after withholding and refundable credits.", FederalEstimatedUrl);

        Add("NJ estimated Gross Income Tax payment", "New Jersey", "Estimated Income Tax", "NJ-1040-ES", "Q1", new DateTime(year, 4, 15), "Generally applies if estimated NJ tax is more than $400 beyond withholding and credits.", NjEstimatedUrl);
        Add("NJ estimated Gross Income Tax payment", "New Jersey", "Estimated Income Tax", "NJ-1040-ES", "Q2", new DateTime(year, 6, 15), "Generally applies if estimated NJ tax is more than $400 beyond withholding and credits.", NjEstimatedUrl);
        Add("NJ estimated Gross Income Tax payment", "New Jersey", "Estimated Income Tax", "NJ-1040-ES", "Q3", new DateTime(year, 9, 15), "Generally applies if estimated NJ tax is more than $400 beyond withholding and credits.", NjEstimatedUrl);
        Add("NJ estimated Gross Income Tax payment", "New Jersey", "Estimated Income Tax", "NJ-1040-ES", "Q4", new DateTime(year + 1, 1, 15), "Generally applies if estimated NJ tax is more than $400 beyond withholding and credits.", NjEstimatedUrl);

        var salesTaxStatus = profile.NjSalesTaxRegistration.Equals("Not registered", StringComparison.OrdinalIgnoreCase) ? "Not Required" : "Review Applicability";
        foreach (var (period, due) in new[]
        {
            ("Q1", new DateTime(year, 4, 20)),
            ("Q2", new DateTime(year, 7, 20)),
            ("Q3", new DateTime(year, 10, 20)),
            ("Q4", new DateTime(year + 1, 1, 20))
        })
        {
            Add("NJ sales and use tax return", "New Jersey", "Sales Tax", "ST-50", period, due, "Required quarterly if registered to collect NJ sales tax, including zero-sales quarters.", NjSalesTaxUrl, "Marketplace sales and direct sales may have different handling. Review the NJ sales-tax export.");
            rows[^1].Status = salesTaxStatus;
        }

        DateTime? annualReportDue = profile.FormationMonth is >= 1 and <= 12
            ? new DateTime(year, profile.FormationMonth.Value, DateTime.DaysInMonth(year, profile.FormationMonth.Value))
            : null;
        Add("NJ LLC annual report and $75 filing fee", "New Jersey", "Annual Report / Fee", "Annual Report", "Annual", annualReportDue, "Applies to NJ business entities. Due in the formation/authorization month.", NjAnnualReportUrl, "File directly through the official NJ portal, not a private mailer.");
        Add($"{year} federal individual/business income-tax return", "Federal", "Annual Income Tax", "Schedule C / Form 1040", "Annual", new DateTime(year + 1, 4, 15), "Confirm filing forms based on your LLC's federal tax classification.", FederalSelfEmployedUrl);
        Add($"{year} NJ Gross Income Tax return", "New Jersey", "Annual Income Tax", "NJ-1040", "Annual", new DateTime(year + 1, 4, 15), "Confirm filing requirement and forms based on your NJ and federal tax situation.", NjEstimatedUrl);
        Add("Employee and contractor information-return review", "Federal / New Jersey", "Payroll / Information Returns", "W-2 / 1099 review", "Annual", new DateTime(year + 1, 1, 31), "Applies if you have employees or make reportable payments to contractors.", "https://www.irs.gov/businesses/small-businesses-self-employed/reporting-payments-to-independent-contractors");
        return rows;
    }

    private static void AddText(ZipArchive zip, string fileName, string content)
    {
        var entry = zip.CreateEntry(fileName, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(true));
        writer.Write(content);
    }

    private static string TaxPackageReadme(int year) => $"""
        EPATA TAX PACKAGE - {year}

        This package is a recordkeeping and accountant-handoff aid. It is not a completed tax return and is not tax advice.

        Start with tax-summary.csv and tax-obligations.csv.
        Review every row marked NeedsReview or Review Applicability.
        Confirm NJ sales-tax treatment in nj-sales-tax-review.csv.
        Gross income must be tracked whether or not a 1099 or other tax form is received.
        Keep source receipts, statements, invoices, mileage support, filings, and payment confirmations.

        Static guidance file in the app folder: TAX_GUIDE.md
        Generated: {DateTimeOffset.Now:O}
        """;
}
