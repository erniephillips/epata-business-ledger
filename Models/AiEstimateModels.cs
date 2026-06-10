namespace EPATA.BusinessLedger.Models;

public sealed record AiEstimateDraftRequest(
    string? SourceText,
    string? SourceName,
    List<string>? SourceUrls = null,
    List<AiEstimateImageInput>? Images = null,
    List<string>? SourceWarnings = null);

public sealed record AiEstimateImageInput(string FileName, string ContentType, string Base64Data);

public sealed class AiEstimateDraftResult
{
    public string Provider { get; set; } = "Local rules";
    public bool UsedAi { get; set; }
    public AiExecutionReceipt ExecutionReceipt { get; set; } = new();
    public string SourceName { get; set; } = "Pasted text";
    public string InstructionsPath { get; set; } = string.Empty;
    public AiEstimatePrefill Prefill { get; set; } = new();
    public AiEstimatePricingSummary Pricing { get; set; } = new();
    public List<string> Questions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class AiExecutionReceipt
{
    public string Engine { get; set; } = "LOCAL RULES";
    public bool UsedAi { get; set; }
    public string Provider { get; set; } = "Local rules";
    public string? Model { get; set; }
    public string? ResponseId { get; set; }
    public DateTimeOffset ExecutedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
}

public sealed class AiEstimatePrefill
{
    public string DocType { get; set; } = "ESTIMATE";
    public string Status { get; set; } = "Draft";
    public string? CustomerName { get; set; }
    public string? CustomerPhone { get; set; }
    public string? CustomerEmail { get; set; }
    public string? CustomerAddress { get; set; }
    public string? PreparedFor { get; set; }
    public string? ProjectName { get; set; }
    public string? Material { get; set; }
    public string? Color { get; set; }
    public string? Infill { get; set; }
    public string? ProjectDescription { get; set; }
    public string? ProjectNotes { get; set; }
    public string? PageSize { get; set; }
    public string? DocDate { get; set; }
    public string? DueDate { get; set; }
    public string? PaymentMethod { get; set; }
    public string? PricingGuide { get; set; }
    public string? TermsNotes { get; set; }
    public string? StandardTurnaround { get; set; }
    public string? RushTurnaround { get; set; }
    public decimal DocTaxRate { get; set; }
    public decimal DocRushPercent { get; set; }
    public decimal DocDiscount { get; set; }
    public decimal CalcGrams { get; set; }
    public decimal CalcHours { get; set; }
    public decimal CalcDesignHours { get; set; }
    public decimal CalcSetupFee { get; set; }
    public decimal CalcPostFee { get; set; }
    public decimal CalcGramRate { get; set; }
    public decimal CalcHourRate { get; set; }
    public decimal CalcDesignRate { get; set; }
    public decimal CalcMinimum { get; set; }
    public decimal CalcDifficulty { get; set; }
    public decimal CalcRush { get; set; }
    public decimal CalcDiscount { get; set; }
    public decimal CalcTaxRate { get; set; }
    public string AssistanceSource { get; set; } = "LOCAL RULES";
    public string AssistanceProvider { get; set; } = "Local rules";
    public List<AiEstimateLineItem> LineItems { get; set; } = [];
}

public sealed class AiEstimateLineItem
{
    public string Description { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal Rate { get; set; }
}

public sealed class AiEstimatePricingSummary
{
    public bool UsedCalculatorInputs { get; set; }
    public bool RequiresPricingReview { get; set; } = true;
    public decimal Setup { get; set; }
    public decimal Material { get; set; }
    public decimal Machine { get; set; }
    public decimal Design { get; set; }
    public decimal PostProcessing { get; set; }
    public decimal DifficultyFee { get; set; }
    public decimal MinimumAdjustment { get; set; }
    public decimal LineSubtotal { get; set; }
    public decimal RushAmount { get; set; }
    public decimal Discount { get; set; }
    public decimal TaxableSubtotal { get; set; }
    public decimal TaxAmount { get; set; }
    public decimal Total { get; set; }
}
