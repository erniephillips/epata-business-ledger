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

    [MaxLength(220)]
    public string? SourceProof { get; set; }

    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
