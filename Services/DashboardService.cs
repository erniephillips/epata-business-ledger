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
            actions
        };
    }

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
