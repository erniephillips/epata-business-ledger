using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EPATA.BusinessLedger.Models;

public class Bill : AuditableEntity
{
    [MaxLength(160)]
    public string VendorName { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? BillNumber { get; set; }

    public DateTime? BillDate { get; set; }
    public DateTime? DueDate { get; set; }

    [MaxLength(120)]
    public string Category { get; set; } = "General Business";

    [MaxLength(260)]
    public string Description { get; set; } = string.Empty;

    public decimal? Amount { get; set; }
    public decimal? SalesTax { get; set; }
    public decimal? Total { get; set; }
    public decimal? AmountPaid { get; set; }
    public DateTime? PaymentDate { get; set; }

    [MaxLength(80)]
    public string Status { get; set; } = "Unpaid"; // Unpaid, Paid, Partial, Void

    [MaxLength(120)]
    public string? PaymentAccount { get; set; }

    [MaxLength(220)]
    public string? SourceProof { get; set; }

    [NotMapped]
    public decimal BalanceDue => Math.Max(0, (Total ?? 0) - (AmountPaid ?? 0));

    public bool TaxDeductible { get; set; } = true;
    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
