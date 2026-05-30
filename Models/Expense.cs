using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class Expense : AuditableEntity
{
    public DateTime? ExpenseDate { get; set; }

    [MaxLength(160)]
    public string VendorName { get; set; } = string.Empty;

    [MaxLength(120)]
    public string Category { get; set; } = "General Business";

    [MaxLength(260)]
    public string Description { get; set; } = string.Empty;

    [MaxLength(120)]
    public string? PaymentAccount { get; set; }

    public decimal? Amount { get; set; }
    public decimal? SalesTax { get; set; }
    public decimal? Total { get; set; }

    [MaxLength(220)]
    public string? ReceiptProof { get; set; }

    public bool TaxDeductible { get; set; } = true;
    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
