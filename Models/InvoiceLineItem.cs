using System.ComponentModel.DataAnnotations;

namespace EPATA.BusinessLedger.Models;

public class InvoiceLineItem
{
    public int Id { get; set; }
    public int InvoiceDocumentId { get; set; }
    public int SortOrder { get; set; }

    [MaxLength(240)]
    public string? Description { get; set; }

    public string? Details { get; set; }
    public decimal Quantity { get; set; } = 1;
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }

    public InvoiceDocument? InvoiceDocument { get; set; }
}
