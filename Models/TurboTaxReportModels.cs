namespace EPATA.BusinessLedger.Models;

public sealed record TurboTaxInterviewAnswerRow(
    string Section,
    string Question,
    string Answer,
    string Status,
    string Source);

public sealed record ScheduleCLineRow(
    string Line,
    string Label,
    decimal Amount,
    string Status,
    string Source);

public sealed record TaxIncomeDetailRow(
    DateTime? Date,
    string SourceType,
    string Reference,
    string Description,
    decimal GrossReceipts,
    decimal ReturnsAndAllowances,
    decimal OtherIncome,
    string? Proof,
    bool NeedsReview);

public sealed record TaxExpenseDetailRow(
    DateTime? Date,
    string SourceType,
    string Reference,
    string Payee,
    string Description,
    string ScheduleCLine,
    string ScheduleCCategory,
    decimal PaidAmount,
    decimal DeductibleAmount,
    string? Proof,
    bool NeedsReview,
    string? Notes);

public sealed record TaxReadinessIssue(
    string Severity,
    string Area,
    string Message,
    string Action);

public sealed record TurboTaxReport(
    int TaxYear,
    string FilingAssumption,
    bool ReadyForDataEntry,
    decimal ScheduleCNetProfitOrLoss,
    TaxYearSetup Setup,
    IReadOnlyList<TurboTaxInterviewAnswerRow> InterviewAnswers,
    IReadOnlyList<ScheduleCLineRow> ScheduleCLines,
    IReadOnlyList<TaxIncomeDetailRow> IncomeDetails,
    IReadOnlyList<TaxExpenseDetailRow> ExpenseDetails,
    IReadOnlyList<OrderLossIncident> OrderLossIncidents,
    IReadOnlyList<TaxReadinessIssue> ReadinessIssues);
