using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class AppSetting : AuditableEntity
{
    [Required, MaxLength(120)]
    public string Key { get; set; } = string.Empty;

    [MaxLength(400)]
    public string? Value { get; set; }

    public string? Notes { get; set; }
}
