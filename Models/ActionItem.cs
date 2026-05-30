using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class ActionItem : AuditableEntity
{
    [Required, MaxLength(180)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(80)]
    public string Area { get; set; } = "General"; // Sales, Invoice, AP, Product, Tax, Audit

    [MaxLength(40)]
    public string Priority { get; set; } = "Normal"; // Low, Normal, High

    public DateTime? DueDate { get; set; }

    [MaxLength(80)]
    public string Status { get; set; } = "Open"; // Open, Waiting, Done

    [MaxLength(140)]
    public string? RelatedRecord { get; set; }

    public string? Notes { get; set; }
}
