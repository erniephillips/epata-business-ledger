using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class MakerWorldReward : AuditableEntity
{
    public DateTime? RewardDate { get; set; }

    [MaxLength(80)]
    public string RewardType { get; set; } = "Points"; // Points, Gift Card, Redemption

    public int? PointsChange { get; set; }
    public decimal? GiftCardAmount { get; set; }

    [MaxLength(40)]
    public string? CodeLast4 { get; set; }

    [MaxLength(80)]
    public string Status { get; set; } = "Available"; // Available, Redeemed, Expired

    [MaxLength(220)]
    public string? SourceProof { get; set; }

    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
