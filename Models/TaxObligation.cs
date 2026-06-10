using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class TaxObligation : AuditableEntity
{
    public int TaxYear { get; set; } = DateTime.Today.Year;

    [Required, MaxLength(180)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(80)]
    public string Jurisdiction { get; set; } = "Federal";

    [MaxLength(100)]
    public string ObligationType { get; set; } = "Other";

    [MaxLength(80)]
    public string? FormName { get; set; }

    [MaxLength(80)]
    public string? Period { get; set; }

    public DateTime? DueDate { get; set; }

    [MaxLength(40)]
    public string Status { get; set; } = "Review Applicability";

    public decimal? EstimatedAmount { get; set; }
    public decimal? AmountPaid { get; set; }
    public DateTime? PaidOrFiledDate { get; set; }

    [MaxLength(160)]
    public string? ConfirmationNumber { get; set; }

    [MaxLength(220)]
    public string? ProofReference { get; set; }

    [MaxLength(400)]
    public string? OfficialUrl { get; set; }

    public string? AppliesIf { get; set; }
    public bool NeedsReview { get; set; } = true;
    public string? Notes { get; set; }
}
