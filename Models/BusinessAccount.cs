using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class BusinessAccount : AuditableEntity
{
    [Required, MaxLength(120)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(80)]
    public string AccountType { get; set; } = "Cash"; // Cash, Checking, Credit Card, Etsy, Gift Card, Other

    [MaxLength(120)]
    public string? Institution { get; set; }

    [MaxLength(20)]
    public string? Last4 { get; set; }

    public decimal? OpeningBalance { get; set; }
    public decimal? CurrentBalance { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}
