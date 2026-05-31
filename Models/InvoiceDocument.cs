using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class InvoiceDocument
{
    public int Id { get; set; }

    [MaxLength(80)]
    public string? DocNumber { get; set; }

    [MaxLength(24)]
    public string DocType { get; set; } = "ESTIMATE";

    [MaxLength(40)]
    public string Status { get; set; } = "Draft";

    [MaxLength(160)]
    public string? CustomerName { get; set; }

    [MaxLength(80)]
    public string? CustomerPhone { get; set; }

    public string? CustomerAddress { get; set; }

    [MaxLength(160)]
    public string? CustomerEmail { get; set; }

    [MaxLength(160)]
    public string? PreparedFor { get; set; }

    [MaxLength(220)]
    public string? ProjectName { get; set; }

    [MaxLength(80)]
    public string? Material { get; set; }

    [MaxLength(80)]
    public string? Color { get; set; }

    [MaxLength(80)]
    public string? Infill { get; set; }

    public string? ProjectDescription { get; set; }
    public string? ProjectNotes { get; set; }

    [MaxLength(40)]
    public string? PageSize { get; set; }

    [MaxLength(24)]
    public string? DocDate { get; set; }

    [MaxLength(24)]
    public string? DueDate { get; set; }

    public decimal Subtotal { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal RushAmount { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
    public decimal AmountPaid { get; set; }
    public decimal Balance { get; set; }

    public string? PricingGuide { get; set; }
    public string? TermsNotes { get; set; }
    public string? StandardTurnaround { get; set; }
    public string? RushTurnaround { get; set; }

    public decimal CalcGrams { get; set; }
    public decimal CalcHours { get; set; }
    public decimal CalcDesignHours { get; set; }
    public decimal CalcSetupFee { get; set; }
    public decimal CalcPostFee { get; set; }
    public decimal CalcGramRate { get; set; } = 0.05m;
    public decimal CalcHourRate { get; set; } = 3m;
    public decimal CalcDesignRate { get; set; } = 25m;
    public decimal CalcMinimum { get; set; } = 15m;
    public decimal CalcDifficulty { get; set; } = 1m;
    public decimal CalcRush { get; set; }
    public decimal CalcDiscount { get; set; }
    public decimal CalcTaxRate { get; set; }

    public string Json { get; set; } = "{}";
    public bool IsArchived { get; set; }
    public string? ArchivedAt { get; set; }
    public string? ArchiveReason { get; set; }
    public string CreatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");
    public string UpdatedAt { get; set; } = DateTimeOffset.UtcNow.ToString("O");

    public List<InvoiceLineItem> LineItems { get; set; } = [];
}
