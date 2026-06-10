namespace EPATA.BusinessLedger.Models;

public sealed record TaxProfile(
    string EntityType,
    string State,
    int? FormationMonth,
    string NjSalesTaxRegistration,
    string HasEmployees,
    string PaysContractors,
    string UsesVehicle,
    string HomeOffice,
    string InventoryMethod,
    decimal? BusinessMileageRate,
    string? Notes);

public sealed record TaxSummaryRow(
    int TaxYear,
    decimal GrossReceipts,
    decimal CustomerPaidIncludingTax,
    decimal SalesTaxMemo,
    decimal SellerCollectedSalesTax,
    decimal MarketplaceSalesTaxMemo,
    int SalesTaxHandlingNeedsReview,
    decimal PlatformAndShippingCosts,
    decimal EstimatedCogs,
    decimal OperatingExpenseDeductions,
    decimal CogsMaterialExpenseDeductions,
    decimal ExpensedAssets,
    decimal MakerWorldIncome,
    decimal BusinessMiles,
    decimal MileageRate,
    decimal MileageDeductionEstimate,
    decimal ParkingAndTolls,
    decimal WorkingNetProfit,
    bool ScheduleSeThresholdReached,
    int MissingProofOrReviewCount);

public sealed record NjSalesTaxReviewRow(
    int Id,
    DateTime? SaleDate,
    int Quarter,
    string Platform,
    string SalesTaxHandling,
    string ReviewBucket,
    string? OrderNumber,
    string? InvoiceNumber,
    string CustomerName,
    decimal GrossReceipts,
    decimal SalesTaxMemo,
    string? SourceProof,
    bool NeedsReview,
    string? Notes);
