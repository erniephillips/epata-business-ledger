using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class CustomerCommunication : AuditableEntity
{
    public DateTime? OccurredAt { get; set; }

    [Required, MaxLength(160)]
    public string CustomerName { get; set; } = string.Empty;

    [MaxLength(40)]
    public string Direction { get; set; } = "Outgoing"; // Incoming, Outgoing, Internal

    [MaxLength(60)]
    public string Channel { get; set; } = "Email"; // Email, Text, Phone, Etsy, In Person, Other

    [MaxLength(220)]
    public string? Subject { get; set; }

    [Required]
    public string Summary { get; set; } = string.Empty;

    public int? CustomerJobId { get; set; }

    [MaxLength(80)]
    public string? RelatedJobNumber { get; set; }

    [MaxLength(80)]
    public string? RelatedOrderNumber { get; set; }

    [MaxLength(80)]
    public string? RelatedInvoiceNumber { get; set; }

    public DateTime? FollowUpDate { get; set; }

    [MaxLength(40)]
    public string FollowUpStatus { get; set; } = "None"; // None, Open, Done

    [MaxLength(220)]
    public string? SourceProof { get; set; }

    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
