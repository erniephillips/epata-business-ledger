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
        var actions = await db.ActionItems.Where(x => !x.IsArchived && x.Status != "Done")
            .OrderByDescending(x => x.Priority == "High")
            .ThenBy(x => x.DueDate)
            .Take(8)
            .ToListAsync();

        var paidSales = sales.Where(x => !string.Equals(x.Status, "Draft", StringComparison.OrdinalIgnoreCase));
        var grossReceipts = paidSales.Sum(x => (x.ItemSales ?? 0) + (x.ShippingCharged ?? 0) - (x.Refunds ?? 0));
        var taxMemo = paidSales.Sum(x => x.SalesTaxCollected ?? 0);
        var customerPaid = paidSales.Sum(x => x.CustomerPaid ?? 0);
        var sellingCosts = paidSales.Sum(x => (x.PlatformFees ?? 0) + (x.ShippingLabelCost ?? 0) + (x.EstimatedCogs ?? 0));
        var directExpenses = expenses.Sum(x => x.Total ?? x.Amount ?? 0);
        var estimatedNet = grossReceipts - sellingCosts - directExpenses;
        var openReceivables = invoices.Sum(x => Math.Max(0, (x.InvoiceTotal ?? 0) - (x.AmountPaid ?? 0)));
        var openPayables = bills.Sum(x => Math.Max(0, (x.Total ?? 0) - (x.AmountPaid ?? 0)));
        var needsReviewCount = sales.Count(x => x.NeedsReview) + invoices.Count(x => x.NeedsReview) + bills.Count(x => x.NeedsReview) + expenses.Count(x => x.NeedsReview);

        var monthly = sales
            .Where(x => x.SaleDate.HasValue)
            .GroupBy(x => new { x.SaleDate!.Value.Year, x.SaleDate!.Value.Month })
            .Select(g => new
            {
                month = new DateTime(g.Key.Year, g.Key.Month, 1).ToString("yyyy-MM"),
                grossReceipts = g.Sum(x => (x.ItemSales ?? 0) + (x.ShippingCharged ?? 0) - (x.Refunds ?? 0)),
                salesTaxMemo = g.Sum(x => x.SalesTaxCollected ?? 0),
                estimatedCosts = g.Sum(x => (x.PlatformFees ?? 0) + (x.ShippingLabelCost ?? 0) + (x.EstimatedCogs ?? 0)),
                estimatedNet = g.Sum(x => (x.ItemSales ?? 0) + (x.ShippingCharged ?? 0) - (x.Refunds ?? 0) - (x.PlatformFees ?? 0) - (x.ShippingLabelCost ?? 0) - (x.EstimatedCogs ?? 0)),
                orders = g.Count()
            })
            .OrderBy(x => x.month)
            .ToList();

        var openInvoices = invoices
            .Where(x => Math.Max(0, (x.InvoiceTotal ?? 0) - (x.AmountPaid ?? 0)) > 0)
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
            .Where(x => Math.Max(0, (x.Total ?? 0) - (x.AmountPaid ?? 0)) > 0)
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
}
