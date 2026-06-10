using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class Expense : AuditableEntity
{
    public DateTime? ExpenseDate { get; set; }

    [MaxLength(160)]
    public string VendorName { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Category { get; set; } = "General Business";

    [MaxLength(120)]
    public string TaxCategory { get; set; } = "Other business expense";

    [MaxLength(260)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? PaymentAccount { get; set; }

    public decimal? Amount { get; set; }
    public decimal? SalesTax { get; set; }
    public decimal? Total { get; set; }

    [MaxLength(220)]
    public string? ReceiptProof { get; set; }

    // Tax & deductibility tracking
    [MaxLength(80)]
    public string TaxBucket { get; set; } = "Operating Expense"; // Operating Expense, COGS/Materials, Asset, Memo Only, Review

    [MaxLength(40)]
    public string DeductibleStatus { get; set; } = "Yes"; // Yes, No, Review

    public decimal? BusinessUsePercent { get; set; } = 100;
    public bool CountedExpense { get; set; } = true;

    public bool TaxDeductible { get; set; } = true;
    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
