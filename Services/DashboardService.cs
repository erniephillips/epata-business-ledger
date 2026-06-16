using EPATA.BusinessLedger.Data;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Services;

public class DashboardService(AppDbContext db)
{
    public async Task<object> BuildAsync()
    {
        var sales = await db.Sales.Where(x => !x.IsArchived && x.IncludeInDashboard).ToListAsync();
        var invoices = await db.ReceivableInvoices.Where(x => !x.IsArchived).ToListAsync();
        var bills = await db.Bills.Where(x => !x.IsArchived).ToListAsync();
        var expenses = await db.Expenses.Where(x => !x.IsArchived).ToListAsync();
        var assets = await db.Assets.Where(x => !x.IsArchived).ToListAsync();
        var rewards = await db.MakerWorldRewards.Where(x => !x.IsArchived).ToListAsync();
        var actions = await db.ActionItems.Where(x => !x.IsArchived && x.Status != "Done")
            .OrderByDescending(x => x.Priority == "High")
            .ThenBy(x => x.DueDate)
            .Take(8)
            .ToListAsync();

        var reportableSales = sales.Where(MoneyRules.IsReportableSale).ToList();
        var grossReceipts = reportableSales.Sum(MoneyRules.SaleGrossReceipts);
        var taxMemo = reportableSales.Sum(MoneyRules.SaleSalesTaxMemo);
        var customerPaid = reportableSales.Sum(MoneyRules.SaleCustomerPaid);
        var sellingCosts = reportableSales.Sum(MoneyRules.SaleKnownCosts);
        var directExpenses = expenses.Sum(MoneyRules.TaxCountedExpenseAmount);
        var expensedAssets = assets.Sum(MoneyRules.FullyExpensedAssetAmount);
        var makerWorldIncome = rewards.Sum(MoneyRules.MakerWorldIncomeAmount);
        var estimatedNet = grossReceipts - sellingCosts - directExpenses;
        var openReceivables = invoices.Where(x => !IsClosedOrDraft(x.Status)).Sum(x => Math.Max(0, (x.InvoiceTotal ?? 0) - (x.AmountPaid ?? 0)));
        var openPayables = bills.Where(x => !IsClosedOrDraft(x.Status)).Sum(x => Math.Max(0, (x.Total ?? 0) - (x.AmountPaid ?? 0)));
        var needsReviewCount = sales.Count(x => x.NeedsReview) + invoices.Count(x => x.NeedsReview) + bills.Count(x => x.NeedsReview) + expenses.Count(x => x.NeedsReview) + assets.Count(x => x.NeedsReview) + rewards.Count(x => x.NeedsReview);
        var breakdowns = new Dictionary<string, DashboardBreakdown>
        {
            ["grossReceipts"] = Breakdown(
                "Gross Receipts",
                "Item sales + shipping charged - refunds for reportable Sales included on the dashboard.",
                true,
                reportableSales.Select(x => Item("Sale", "sales", x.Id, x.SaleDate, $"{x.CustomerName}: {x.ProductName}", $"Order {x.OrderNumber ?? "none"}; {x.Platform}; {x.Status}", MoneyRules.SaleGrossReceipts(x)))),
            ["customerPaid"] = Breakdown(
                "Customer Paid",
                "Full customer-paid amounts, including sales-tax memo amounts, for reportable Sales.",
                true,
                reportableSales.Select(x => Item("Sale", "sales", x.Id, x.SaleDate, $"{x.CustomerName}: {x.ProductName}", $"Order {x.OrderNumber ?? "none"}; {x.Platform}; {x.Status}", MoneyRules.SaleCustomerPaid(x)))),
            ["salesTaxMemo"] = Breakdown(
                "Sales Tax Memo",
                "Sales tax shown on reportable Sales. Marketplace-collected tax remains a memo amount for review.",
                true,
                reportableSales.Where(x => MoneyRules.SaleSalesTaxMemo(x) != 0)
                    .Select(x => Item("Sale", "sales", x.Id, x.SaleDate, $"{x.CustomerName}: {x.ProductName}", $"Order {x.OrderNumber ?? "none"}; {x.SalesTaxHandling}", MoneyRules.SaleSalesTaxMemo(x)))),
            ["knownCosts"] = Breakdown(
                "Known Costs",
                "Entered Sale costs plus counted deductible operating/COGS Expenses.",
                true,
                reportableSales.Where(x => MoneyRules.SaleKnownCosts(x) != 0)
                    .Select(x => Item("Sale costs", "sales", x.Id, x.SaleDate, $"{x.CustomerName}: {x.ProductName}", $"Fees {(x.PlatformFees ?? 0):C}; label {(x.ShippingLabelCost ?? 0):C}; COGS {(x.EstimatedCogs ?? 0):C}", MoneyRules.SaleKnownCosts(x)))
                    .Concat(expenses.Where(x => MoneyRules.TaxCountedExpenseAmount(x) != 0)
                        .Select(x => Item("Expense", "expenses", x.Id, x.ExpenseDate, $"{x.VendorName}: {x.Description}", $"{x.TaxBucket}; {x.BusinessUsePercent ?? 100}% business use", MoneyRules.TaxCountedExpenseAmount(x))))),
            ["estimatedNet"] = Breakdown(
                "Estimated Net",
                "Gross receipts - entered Sale costs - counted deductible operating/COGS Expenses.",
                true,
                reportableSales.Select(x => Item("Sale revenue", "sales", x.Id, x.SaleDate, $"{x.CustomerName}: {x.ProductName}", $"Gross receipts from order {x.OrderNumber ?? "none"}", MoneyRules.SaleGrossReceipts(x)))
                    .Concat(reportableSales.Where(x => MoneyRules.SaleKnownCosts(x) != 0)
                        .Select(x => Item("Sale costs", "sales", x.Id, x.SaleDate, $"{x.CustomerName}: {x.ProductName}", $"Fees {(x.PlatformFees ?? 0):C}; label {(x.ShippingLabelCost ?? 0):C}; COGS {(x.EstimatedCogs ?? 0):C}", -MoneyRules.SaleKnownCosts(x))))
                    .Concat(expenses.Where(x => MoneyRules.TaxCountedExpenseAmount(x) != 0)
                        .Select(x => Item("Expense", "expenses", x.Id, x.ExpenseDate, $"{x.VendorName}: {x.Description}", $"{x.TaxBucket}; {x.BusinessUsePercent ?? 100}% business use", -MoneyRules.TaxCountedExpenseAmount(x))))),
            ["openReceivables"] = Breakdown(
                "Open AR",
                "Invoice total - amount paid for non-draft, non-closed AR invoices.",
                true,
                invoices.Where(x => !IsClosedOrDraft(x.Status) && x.BalanceDue > 0)
                    .Select(x => Item("AR Invoice", "receivables", x.Id, x.DueDate ?? x.InvoiceDate, $"{x.InvoiceNumber}: {x.CustomerName}", $"{x.ProjectName}; {x.Status}; due {x.DueDate:yyyy-MM-dd}", x.BalanceDue))),
            ["openPayables"] = Breakdown(
                "Open AP",
                "Bill total - amount paid for non-draft, non-closed Bills.",
                true,
                bills.Where(x => !IsClosedOrDraft(x.Status) && x.BalanceDue > 0)
                    .Select(x => Item("Bill", "bills", x.Id, x.DueDate ?? x.BillDate, $"{x.VendorName}: {x.Description}", $"{x.Category}; {x.Status}; due {x.DueDate:yyyy-MM-dd}", x.BalanceDue))),
            ["needsReview"] = Breakdown(
                "Needs Review",
                "Active dashboard-related rows currently marked Needs Review.",
                false,
                sales.Where(x => x.NeedsReview).Select(x => Item("Sale", "sales", x.Id, x.SaleDate, $"{x.CustomerName}: {x.ProductName}", $"Order {x.OrderNumber ?? "none"}; {x.Platform}; {x.Status}", 1))
                    .Concat(invoices.Where(x => x.NeedsReview).Select(x => Item("AR Invoice", "receivables", x.Id, x.DueDate ?? x.InvoiceDate, $"{x.InvoiceNumber}: {x.CustomerName}", $"{x.ProjectName}; {x.Status}", 1)))
                    .Concat(bills.Where(x => x.NeedsReview).Select(x => Item("Bill", "bills", x.Id, x.DueDate ?? x.BillDate, $"{x.VendorName}: {x.Description}", $"{x.Category}; {x.Status}", 1)))
                    .Concat(expenses.Where(x => x.NeedsReview).Select(x => Item("Expense", "expenses", x.Id, x.ExpenseDate, $"{x.VendorName}: {x.Description}", $"{x.Category}; {x.TaxBucket}", 1)))
                    .Concat(assets.Where(x => x.NeedsReview).Select(x => Item("Asset", "assets", x.Id, x.PurchaseDate, x.Name, $"{x.VendorName}; {x.TaxTreatment}", 1)))
                    .Concat(rewards.Where(x => x.NeedsReview).Select(x => Item("MakerWorld Reward", "makerworld", x.Id, x.RewardDate, x.RewardType, $"{x.Status}; {x.IncomeStatus}", 1))))
        };

        var monthly = reportableSales
            .Where(x => x.SaleDate.HasValue)
            .GroupBy(x => new { x.SaleDate!.Value.Year, x.SaleDate!.Value.Month })
            .Select(g => new
            {
                month = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("yyyy-MM"),
                grossReceipts = g.Sum(MoneyRules.SaleGrossReceipts),
                salesTaxMemo = g.Sum(MoneyRules.SaleSalesTaxMemo),
                customerPaid = g.Sum(MoneyRules.SaleCustomerPaid),
                estimatedCosts = g.Sum(MoneyRules.SaleKnownCosts),
                estimatedNet = g.Sum(x => MoneyRules.SaleGrossReceipts(x) - MoneyRules.SaleKnownCosts(x)),
                orders = g.Count()
            })
            .OrderBy(x => x.month)
            .ToList();

        var openInvoices = invoices
            .Where(x => !IsClosedOrDraft(x.Status) && Math.Max(0, (x.InvoiceTotal ?? 0) - (x.AmountPaid ?? 0)) > 0)
            .OrderBy(x => x.DueDate ?? DateTime.MaxValue)
            .Select(x => new
            {
                x.InvoiceNumber,
                x.CustomerName,
                x.ProjectName,
                x.Status,
                dueDate = x.DueDate,
                balanceDue = Math.Max(0, (x.InvoiceTotal ?? 0) - (x.AmountPaid ?? 0)),
                x.NeedsReview
            })
            .ToList();

        var openBills = bills
            .Where(x => !IsClosedOrDraft(x.Status) && Math.Max(0, (x.Total ?? 0) - (x.AmountPaid ?? 0)) > 0)
            .OrderBy(x => x.DueDate ?? DateTime.MaxValue)
            .Select(x => new
            {
                x.VendorName,
                x.Description,
                x.Category,
                dueDate = x.DueDate,
                balanceDue = Math.Max(0, (x.Total ?? 0) - (x.AmountPaid ?? 0)),
                x.Status,
                x.NeedsReview
            })
            .ToList();

        return new
        {
            kpis = new
            {
                grossReceipts,
                customerPaid,
                salesTaxMemo = taxMemo,
                sellingCosts,
                directExpenses,
                expensedAssets,
                makerWorldIncome,
                taxPrepIncome = grossReceipts + makerWorldIncome,
                taxPrepDeductions = directExpenses + expensedAssets,
                estimatedNet,
                openReceivables,
                openPayables,
                needsReviewCount
            },
            monthly,
            openInvoices,
            openBills,
            actions,
            breakdowns
        };
    }

    private static DashboardBreakdown Breakdown(string title, string formula, bool money, IEnumerable<DashboardBreakdownItem> items)
    {
        var rows = items.OrderByDescending(x => x.Date).ThenBy(x => x.Label).ToList();
        return new DashboardBreakdown(title, formula, money, rows.Sum(x => x.Amount), rows);
    }

    private static DashboardBreakdownItem Item(string sourceType, string route, int id, DateTime? date, string label, string detail, decimal amount) =>
        new(sourceType, route, id, date, label, detail, amount);

    private static bool IsClosedOrDraft(string? status)
    {
        return status is null
            || status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Paid", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Void", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
            || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record DashboardBreakdown(string Title, string Formula, bool Money, decimal Total, List<DashboardBreakdownItem> Items);
public sealed record DashboardBreakdownItem(string SourceType, string Route, int Id, DateTime? Date, string Label, string Detail, decimal Amount);
