using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class AuditDocument : AuditableEntity
{
    public DateTime? DocumentDate { get; set; }

    [MaxLength(100)]
    public string DocumentType { get; set; } = "Receipt"; // Receipt, Invoice, Etsy Order, Tax, Bank, Other

    [MaxLength(100)]
    public string? RelatedRecordType { get; set; }

    [MaxLength(100)]
    public string? RelatedRecordNumber { get; set; }

    [MaxLength(220)]
    public string FileName { get; set; } = string.Empty;

    [MaxLength(400)]
    public string? FilePathOrUrl { get; set; }

    [MaxLength(64)]
    public string? UploadFingerprint { get; set; }

    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
