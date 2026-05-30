using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class CustomerJob : AuditableEntity
{
    public DateTime? JobDate { get; set; }

    [MaxLength(160)]
    public string CustomerName { get; set; } = string.Empty;

    [MaxLength(80)]
    public string Platform { get; set; } = "Direct";

    [MaxLength(80)]
    public string? JobNumber { get; set; }

    [MaxLength(80)]
    public string? RelatedOrderNumber { get; set; }

    [MaxLength(80)]
    public string? RelatedInvoiceNumber { get; set; }

    [MaxLength(220)]
    public string JobName { get; set; } = string.Empty;

    [MaxLength(80)]
    public string JobType { get; set; } = "Print"; // Print, Design, Repair, Estimate, Other

    [MaxLength(80)]
    public string Status { get; set; } = "Open"; // Lead, Quoted, Open, In Progress, Completed, Paid, Cancelled

    [MaxLength(220)]
    public string? ProductName { get; set; }

    [MaxLength(80)]
    public string? Material { get; set; }

    [MaxLength(80)]
    public string? Color { get; set; }

    public string? Description { get; set; }
    public decimal? QuoteAmount { get; set; }
    public decimal? InvoiceAmount { get; set; }
    public decimal? AmountPaid { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime? ShipByDate { get; set; }

    [MaxLength(220)]
    public string? SourceProof { get; set; }

    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
