using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class InvoiceDocumentEvent
{
    public int Id { get; set; }

    public int InvoiceDocumentId { get; set; }

    [MaxLength(80)]
    public string? DocNumber { get; set; }

    [MaxLength(24)]
    public string DocType { get; set; } = "ESTIMATE";

    [MaxLength(60)]
    public string EventType { get; set; } = "Updated";

    [MaxLength(40)]
    public string? FromStatus { get; set; }

    [MaxLength(40)]
    public string? ToStatus { get; set; }

    [MaxLength(220)]
    public string Summary { get; set; } = string.Empty;

    public string? Detail { get; set; }

    public decimal? Amount { get; set; }

    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
}
