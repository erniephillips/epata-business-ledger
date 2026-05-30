using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class Party : AuditableEntity
{
    [Required, MaxLength(160)]
    public string Name { get; set; } = string.Empty;

    [MaxLength(40)]
    public string PartyType { get; set; } = "Customer"; // Customer, Vendor, Both

    [MaxLength(160)]
    public string? Email { get; set; }

    [MaxLength(40)]
    public string? Phone { get; set; }

    [MaxLength(180)]
    public string? Address1 { get; set; }

    [MaxLength(180)]
    public string? Address2 { get; set; }

    [MaxLength(80)]
    public string? City { get; set; }

    [MaxLength(40)]
    public string? State { get; set; }

    [MaxLength(20)]
    public string? PostalCode { get; set; }

    [MaxLength(80)]
    public string? Country { get; set; } = "United States";

    [MaxLength(100)]
    public string? EtsyUsername { get; set; }

    [MaxLength(120)]
    public string? DefaultPlatform { get; set; }

    public string? Notes { get; set; }
}
