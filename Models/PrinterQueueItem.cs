using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class PrinterQueueItem : AuditableEntity
{
    public DateTime? QueueDate { get; set; }

    [MaxLength(40)]
    public string Priority { get; set; } = "Normal";

    [MaxLength(60)]
    public string Status { get; set; } = "Queued"; // Queued, Ready, Printing, Paused, Needs Attention, Completed, Cancelled

    [MaxLength(120)]
    public string? PrinterName { get; set; }

    public int? CustomerJobId { get; set; }

    [MaxLength(160)]
    public string? CustomerName { get; set; }

    [MaxLength(220)]
    public string JobName { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? RelatedOrderNumber { get; set; }

    [MaxLength(80)]
    public string? RelatedInvoiceNumber { get; set; }

    [MaxLength(220)]
    public string? ProductName { get; set; }

    [MaxLength(80)]
    public string? Material { get; set; }

    [MaxLength(80)]
    public string? Color { get; set; }

    public int Quantity { get; set; } = 1;
    public int PlateCount { get; set; } = 1;
    public decimal? EstimatedHours { get; set; }
    public decimal? ActualHours { get; set; }
    public decimal ProgressPercent { get; set; }
    public DateTime? ScheduledStart { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? EstimatedFinish { get; set; }
    public DateTime? CompletedAt { get; set; }
    public int FailureCount { get; set; }

    [MaxLength(220)]
    public string? SourceProof { get; set; }

    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
