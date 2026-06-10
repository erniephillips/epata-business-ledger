using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class MileageLog : AuditableEntity
{
    public DateTime? TripDate { get; set; }

    [MaxLength(120)]
    public string? Vehicle { get; set; }

    [MaxLength(180)]
    public string? StartLocation { get; set; }

    [MaxLength(180)]
    public string? EndLocation { get; set; }

    [Required, MaxLength(260)]
    public string BusinessPurpose { get; set; } = string.Empty;

    public decimal? BusinessMiles { get; set; }
    public decimal? ParkingAndTolls { get; set; }

    [MaxLength(220)]
    public string? ProofReference { get; set; }

    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
