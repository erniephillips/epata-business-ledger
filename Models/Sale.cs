using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class Sale : AuditableEntity
{
    public DateTime? SaleDate { get; set; }

    [MaxLength(80)]
    public string Platform { get; set; } = "Direct"; // Etsy, Direct, MakerWorld, Other

    [MaxLength(80)]
    public string PaymentMethod { get; set; } = "Unknown / Review";

    [MaxLength(100)]
    public string SalesTaxHandling { get; set; } = "Unknown / Review";

    [MaxLength(80)]
    public string? OrderNumber { get; set; }

    [MaxLength(80)]
    public string? InvoiceNumber { get; set; }

    [MaxLength(160)]
    public string CustomerName { get; set; } = string.Empty;

    [MaxLength(220)]
    public string ProductName { get; set; } = string.Empty;

    [MaxLength(80)]
    public string? Sku { get; set; }

    [MaxLength(120)]
    public string? Variation { get; set; }

    [MaxLength(80)]
    public string? Color { get; set; }

    public decimal? Quantity { get; set; } = 1;
    public decimal? ItemSales { get; set; }
    public decimal? ShippingCharged { get; set; }
    public decimal? SalesTaxCollected { get; set; }
    public decimal? CustomerPaid { get; set; }
    public decimal? PlatformFees { get; set; }
    public decimal? ShippingLabelCost { get; set; }
    public decimal? Refunds { get; set; }
    public decimal? EstimatedCogs { get; set; }

    [MaxLength(80)]
    public string Status { get; set; } = "Paid"; // Draft, Paid, Fulfilled, Refunded, Needs Review

    [MaxLength(220)]
    public string? SourceProof { get; set; }

    [MaxLength(100)]
    public string? TrackingNumber { get; set; }

    public DateTime? ShipByDate { get; set; }

    public bool IncludeInDashboard { get; set; } = true;
    public bool NeedsReview { get; set; }
    public string? Notes { get; set; }
}
