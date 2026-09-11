namespace EPATA.BusinessLedger.Models;

public sealed class AiOperationReceipt
{
    public string Engine { get; set; } = "LOCAL RULES";
    public bool UsedAi { get; set; }
    public string Provider { get; set; } = "Local rules";
    public DateTimeOffset ExecutedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public List<string> Reads { get; set; } = [];
    public List<string> Writes { get; set; } = [];
    public string UseWhen { get; set; } = string.Empty;
    public string Safety { get; set; } = "Review-first. Nothing is saved automatically.";
}

public sealed record AiRecordReference(string Entity, int Id, string Label, string Route);

public sealed record AiReconciliationFinding(
    string Severity,
    string Kind,
    string Title,
    string Why,
    string RecommendedAction,
    List<AiRecordReference> Records);

public sealed class AiReconciliationReport
{
    public AiOperationReceipt Receipt { get; set; } = new();
    public int ExactDuplicateGroups { get; set; }
    public int PossibleDuplicateGroups { get; set; }
    public int ReconciliationIssues { get; set; }
    public List<AiReconciliationFinding> Findings { get; set; } = [];
    public string? ModelSummary { get; set; }
    public List<string> ModelRecommendedOrder { get; set; } = [];
    public List<string> ModelQuestions { get; set; } = [];
}

public sealed class AiReconciliationNarrative
{
    public string Summary { get; set; } = string.Empty;
    public List<string> RecommendedOrder { get; set; } = [];
    public List<string> Questions { get; set; } = [];
}

public sealed record AiOperationSourcePacket(
    string SourceText,
    List<string> SourceUrls,
    List<AiEstimateImageInput> Images,
    List<string> Warnings,
    string SourceName);

public sealed class AiProductImportResult
{
    public AiOperationReceipt Receipt { get; set; } = new();
    public Product Product { get; set; } = new() { NeedsReview = true };
    public AiListingDraft Listing { get; set; } = new();
    public List<AiRecordReference> PossibleDuplicates { get; set; } = [];
    public List<string> Questions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class AiProductImportModelDraft
{
    public Product Product { get; set; } = new() { NeedsReview = true };
    public AiListingDraft Listing { get; set; } = new();
    public List<string> Questions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class AiMarketplaceOrderImportResult
{
    public AiOperationReceipt Receipt { get; set; } = new();
    public Sale Sale { get; set; } = new() { Platform = "Etsy", PaymentMethod = "Etsy Payments", Status = "Paid", NeedsReview = true };
    public CustomerJob Job { get; set; } = new() { Platform = "Etsy", JobType = "Print", Status = "Completed", NeedsReview = true };
    public Party Customer { get; set; } = new() { PartyType = "Customer", DefaultPlatform = "Etsy" };
    public bool SuggestedCreateJob { get; set; } = true;
    public List<string> DetectedOrderNumbers { get; set; } = [];
    public List<AiRecordReference> PossibleDuplicateSales { get; set; } = [];
    public List<AiRecordReference> PossibleDuplicateJobs { get; set; } = [];
    public List<AiRecordReference> PossibleCustomerMatches { get; set; } = [];
    public List<string> Questions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class AiMarketplaceOrderModelDraft
{
    public AiMarketplaceSaleDraft Sale { get; set; } = new();
    public AiMarketplaceJobDraft Job { get; set; } = new();
    public AiMarketplaceCustomerDraft Customer { get; set; } = new();
    public List<string> Questions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class AiMarketplaceSaleDraft
{
    public string SaleDate { get; set; } = string.Empty;
    public string Platform { get; set; } = "Etsy";
    public string PaymentMethod { get; set; } = "Etsy Payments";
    public string SalesTaxHandling { get; set; } = "Marketplace collected/remitted - verify";
    public string OrderNumber { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string ProductName { get; set; } = string.Empty;
    public string Sku { get; set; } = string.Empty;
    public string Variation { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public decimal Quantity { get; set; } = 1;
    public decimal ItemSales { get; set; }
    public decimal ShippingCharged { get; set; }
    public decimal SalesTaxCollected { get; set; }
    public decimal CustomerPaid { get; set; }
    public decimal PlatformFees { get; set; }
    public decimal ShippingLabelCost { get; set; }
    public decimal Refunds { get; set; }
    public decimal EstimatedCogs { get; set; }
    public string TrackingNumber { get; set; } = string.Empty;
    public string ShipByDate { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class AiMarketplaceJobDraft
{
    public string Material { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ShipByDate { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed class AiMarketplaceCustomerDraft
{
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Address1 { get; set; } = string.Empty;
    public string Address2 { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string PostalCode { get; set; } = string.Empty;
    public string Country { get; set; } = string.Empty;
    public string EtsyUsername { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
}

public sealed record AiMarketplaceOrderSaveRequest(
    Sale Sale,
    CustomerJob? Job,
    Party? Customer,
    bool CreateJob,
    bool SaveCustomerContact,
    List<AiMarketplaceProof> Proofs,
    List<string> DetectedOrderNumbers);
public sealed record AiMarketplaceProof(string FileName, string StoredPath);

public sealed class AiMarketplaceOrderSaveResult
{
    public AiOperationReceipt Receipt { get; set; } = new();
    public Sale Sale { get; set; } = new();
    public CustomerJob? Job { get; set; }
    public Party? Customer { get; set; }
    public string CustomerSaveAction { get; set; } = string.Empty;
    public List<AuditDocument> AuditDocuments { get; set; } = [];
}

public sealed class AiMarketplaceOrderAutoImportResult
{
    public bool Recognized { get; set; }
    public string Action { get; set; } = "IndexedOnly";
    public string Message { get; set; } = string.Empty;
    public bool UsedAi { get; set; }
    public Sale? Sale { get; set; }
    public CustomerJob? Job { get; set; }
    public Party? Customer { get; set; }
    public AuditDocument? AuditDocument { get; set; }
    public List<string> Warnings { get; set; } = [];
}

public sealed class AiListingDraft
{
    public string Platform { get; set; } = "General";
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<string> Highlights { get; set; } = [];
    public List<string> Tags { get; set; } = [];
    public List<string> Faq { get; set; } = [];
    public string PersonalizationInstructions { get; set; } = string.Empty;
}

public sealed record AiJobPlanRequest(int? JobId, string? SourceText);

public sealed class AiJobPlanResult
{
    public AiOperationReceipt Receipt { get; set; } = new();
    public string JobName { get; set; } = string.Empty;
    public string Goal { get; set; } = string.Empty;
    public List<AiJobPlanTask> Tasks { get; set; } = [];
    public List<string> Questions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class AiJobPlanTask
{
    public string Title { get; set; } = string.Empty;
    public string Area { get; set; } = "General";
    public string Priority { get; set; } = "Normal";
    public string Status { get; set; } = "Open";
    public string? DueDate { get; set; }
    public string? RelatedRecord { get; set; }
    public string? Notes { get; set; }
}

public sealed record AiJobPlanSaveRequest(string JobName, List<AiJobPlanTask> Tasks);

public sealed class AiSlicerReadResult
{
    public AiOperationReceipt Receipt { get; set; } = new();
    public AiSlicerDraft Slicer { get; set; } = new();
    public Product ProductPrefill { get; set; } = new() { NeedsReview = true };
    public List<string> Questions { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class AiSlicerDraft
{
    public string Material { get; set; } = string.Empty;
    public string Color { get; set; } = string.Empty;
    public decimal Grams { get; set; }
    public decimal PrintHours { get; set; }
    public decimal FilamentLengthMeters { get; set; }
    public int PlateCount { get; set; }
    public int Quantity { get; set; } = 1;
    public decimal EstimatedMaterialCost { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public sealed record AiListingRequest(int? ProductId, Product? Product, string? Platform, string? ExtraInstructions);

public sealed class AiListingResult
{
    public AiOperationReceipt Receipt { get; set; } = new();
    public AiListingDraft Listing { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
}

public sealed record AiLedgerQuestionRequest(string Query);

public sealed class AiLedgerAnswer
{
    public AiOperationReceipt Receipt { get; set; } = new();
    public string Query { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public List<AiLedgerSearchHit> Results { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed record AiLedgerSearchHit(string Area, string Route, int Id, string Title, string Detail, string Evidence);

public sealed class AiOperationsInstructions
{
    public string BrandName { get; set; } = "EPATA 3D Prints";
    public string DefaultProductCategory { get; set; } = "3D Printed Product";
    public decimal DefaultMaterialCostPerGram { get; set; } = 0.05m;
    public decimal DefaultMachineRatePerHour { get; set; } = 3m;
    public decimal DefaultPackagingCost { get; set; } = 1m;
    public List<string> ProductImportRules { get; set; } = [];
    public List<string> JobPlanStages { get; set; } = [];
    public List<string> ListingRules { get; set; } = [];
}
