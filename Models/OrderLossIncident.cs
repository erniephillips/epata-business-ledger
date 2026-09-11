using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EPATA.BusinessLedger.Models;

public sealed class OrderLossIncident : AuditableEntity
{
    public DateTime? IncidentDate { get; set; }

    public int? SaleId { get; set; }

    [MaxLength(80)]
    public string Platform { get; set; } = "Other";

    [MaxLength(80)]
    public string? OrderNumber { get; set; }

    [MaxLength(160)]
    public string? CustomerName { get; set; }

    [MaxLength(220)]
    public string ProductName { get; set; } = string.Empty;

    [MaxLength(100)]
    public string IncidentType { get; set; } = "Damaged in transit";

    [MaxLength(100)]
    public string Resolution { get; set; } = "Replacement / reship";

    [MaxLength(80)]
    public string Status { get; set; } = "Resolved";

    public decimal? CustomerRefund { get; set; }
    public decimal? ReplacementCogs { get; set; }
    public decimal? AdditionalShippingCost { get; set; }
    public decimal? OtherCost { get; set; }
    public decimal? ReimbursementReceived { get; set; }

    [MaxLength(160)]
    public string? ReimbursementSource { get; set; }

    [MaxLength(100)]
    public string? ClaimNumber { get; set; }

    public bool CountInTaxReports { get; set; } = true;

    [MaxLength(220)]
    public string? SourceProof { get; set; }

    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }

    [NotMapped]
    public decimal NetLoss =>
        Math.Max(0, CustomerRefund ?? 0)
        + Math.Max(0, ReplacementCogs ?? 0)
        + Math.Max(0, AdditionalShippingCost ?? 0)
        + Math.Max(0, OtherCost ?? 0)
        - Math.Max(0, ReimbursementReceived ?? 0);
}
