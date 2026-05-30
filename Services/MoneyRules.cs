using EPATA.BusinessLedger.Models;

namespace EPATA.BusinessLedger.Services;

public static class MoneyRules
{
    private static readonly HashSet<string> ExcludedSaleStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Draft",
        "Void",
        "Cancelled",
        "Canceled"
    };

    public static bool IsReportableSale(Sale sale)
    {
        return !sale.IsArchived && sale.IncludeInDashboard && !ExcludedSaleStatuses.Contains(sale.Status ?? string.Empty);
    }

    public static decimal SaleGrossReceipts(Sale sale)
    {
        return (sale.ItemSales ?? 0) + (sale.ShippingCharged ?? 0) - (sale.Refunds ?? 0);
    }

    public static decimal SaleCustomerPaid(Sale sale) => sale.CustomerPaid ?? 0;

    public static decimal SaleSalesTaxMemo(Sale sale) => sale.SalesTaxCollected ?? 0;

    public static decimal SaleKnownCosts(Sale sale)
    {
        return (sale.PlatformFees ?? 0) + (sale.ShippingLabelCost ?? 0) + (sale.EstimatedCogs ?? 0);
    }

    public static bool IsTaxCountedExpense(Expense expense)
    {
        if (expense.IsArchived || !expense.CountedExpense || !expense.TaxDeductible)
        {
            return false;
        }

        if (!expense.DeductibleStatus.Equals("Yes", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return expense.TaxBucket.Equals("Operating Expense", StringComparison.OrdinalIgnoreCase)
            || expense.TaxBucket.Equals("COGS/Materials", StringComparison.OrdinalIgnoreCase);
    }

    public static decimal TaxCountedExpenseAmount(Expense expense)
    {
        if (!IsTaxCountedExpense(expense))
        {
            return 0;
        }

        var businessUse = Math.Clamp(expense.BusinessUsePercent ?? 100, 0, 100) / 100m;
        return (expense.Total ?? expense.Amount ?? 0) * businessUse;
    }

    public static bool IsFullyExpensedAsset(Asset asset)
    {
        if (asset.IsArchived || !asset.CountedExpenseThisYear)
        {
            return false;
        }

        return asset.TaxTreatment.Equals("Section 179", StringComparison.OrdinalIgnoreCase)
            || asset.TaxTreatment.Equals("De Minimis Expense", StringComparison.OrdinalIgnoreCase);
    }

    public static decimal FullyExpensedAssetAmount(Asset asset)
    {
        if (!IsFullyExpensedAsset(asset))
        {
            return 0;
        }

        var businessUse = Math.Clamp(asset.BusinessUsePercent ?? 100, 0, 100) / 100m;
        return (asset.Cost ?? 0) * businessUse;
    }

    public static bool IsMakerWorldIncome(MakerWorldReward reward)
    {
        return !reward.IsArchived && reward.IncomeStatus.Equals("Yes - Count as income", StringComparison.OrdinalIgnoreCase);
    }

    public static decimal MakerWorldIncomeAmount(MakerWorldReward reward)
    {
        return IsMakerWorldIncome(reward) ? reward.GiftCardAmount ?? 0 : 0;
    }

    public static decimal InvoiceTaxableSalesBase(decimal subtotal, decimal discount, decimal rush)
    {
        return Math.Max(0, subtotal - discount + rush);
    }

    public static (decimal SalesBase, decimal TaxMemo) AllocateInvoicePayment(decimal subtotal, decimal discount, decimal rush, decimal tax, decimal total, decimal amountPaid)
    {
        var paid = Math.Max(0, amountPaid);
        if (paid == 0)
        {
            return (0, 0);
        }

        var salesBase = InvoiceTaxableSalesBase(subtotal, discount, rush);
        if (total <= 0)
        {
            return (salesBase, tax);
        }

        var paidRatio = Math.Min(1, paid / total);
        return (decimal.Round(salesBase * paidRatio, 2), decimal.Round(tax * paidRatio, 2));
    }
}
