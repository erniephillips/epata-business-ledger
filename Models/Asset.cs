using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class Asset : AuditableEntity
{
    [Required, MaxLength(180)]
    public string Name { get; set; } = string.Empty;

    public DateTime? PurchaseDate { get; set; }

    [MaxLength(160)]
    public string? VendorName { get; set; }

    [MaxLength(120)]
    public string Category { get; set; } = "Equipment";

    public decimal? Cost { get; set; }

    [MaxLength(120)]
    public string? SerialNumber { get; set; }

    public DateTime? WarrantyEndDate { get; set; }
    public decimal? BusinessUsePercent { get; set; } = 100;
    public DateTime? InServiceDate { get; set; }

    [MaxLength(80)]
    public string TaxTreatment { get; set; } = "Review"; // Section 179, De Minimis Expense, Depreciation, Review, Not Deductible

    public bool CountedExpenseThisYear { get; set; }
    public decimal? NotYetExpensed { get; set; }

    [MaxLength(220)]
    public string? SourceProof { get; set; }

    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
