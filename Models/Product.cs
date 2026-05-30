using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace EPATA.BusinessLedger.Models;

public class Product : AuditableEntity
{
    [Required, MaxLength(180)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? Sku { get; set; }

    [MaxLength(100)]
    public string Category { get; set; } = "3D Printed Product";

    [MaxLength(80)]
    public string? Material { get; set; }

    [MaxLength(80)]
    public string? Color { get; set; }

    public decimal? Grams { get; set; }
    public decimal? MaterialCostPerGram { get; set; }
    public decimal? PrintHours { get; set; }
    public decimal? MachineRatePerHour { get; set; }
    public decimal? PackagingCost { get; set; }
    public decimal? DesignMinutes { get; set; }
    public decimal? TargetPrice { get; set; }

    [NotMapped]
    public decimal EstimatedCost => ((Grams ?? 0) * (MaterialCostPerGram ?? 0)) + ((PrintHours ?? 0) * (MachineRatePerHour ?? 0)) + (PackagingCost ?? 0);

    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
