using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EPATA.BusinessLedger.Models;

public class ReceivableInvoice : AuditableEntity
{
    [Required, MaxLength(80)]
    public string InvoiceNumber { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? OriginalInvoiceNumber { get; set; }

    public DateTime? InvoiceDate { get; set; }
    public DateTime? DueDate { get; set; }

    [MaxLength(160)]
    public string CustomerName { get; set; } = string.Empty;

    [MaxLength(220)]
    public string? ProjectName { get; set; }

    [MaxLength(80)]
    public string Status { get; set; } = "Draft"; // Draft, Sent, Paid, Partial, Void, Overdue

    public decimal? Subtotal { get; set; }
    public decimal? Discount { get; set; }
    public decimal? RushFee { get; set; }
    public decimal? TaxRatePercent { get; set; }
    public decimal? SalesTax { get; set; }
    public decimal? InvoiceTotal { get; set; }
    public decimal? AmountPaid { get; set; }

    [NotMapped]
    public decimal BalanceDue => Math.Max(0, (InvoiceTotal ?? 0) - (AmountPaid ?? 0));

    [MaxLength(220)]
    public string? SourceProof { get; set; }

    [MaxLength(260)]
    public string? ExternalInvoiceAppUrl { get; set; }

    public bool IncludeInCashReports { get; set; }
    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
