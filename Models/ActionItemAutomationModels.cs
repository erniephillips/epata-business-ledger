namespace EPATA.BusinessLedger.Models;

public sealed record ActionItemAutomationCandidate(
    string Key,
    string Source,
    string AssistanceSource,
    string Priority,
    string Area,
    string Title,
    string Why,
    string RecommendedAction,
    string RelatedRecord,
    string Route,
    bool AlreadyOpen);

public sealed class ActionItemAutomationPreview
{
    public AiOperationReceipt Receipt { get; set; } = new();
    public int ReviewFindingCount { get; set; }
    public int ReconciliationFindingCount { get; set; }
    public int CandidateCount { get; set; }
    public int AlreadyOpenCount { get; set; }
    public int ReadyToCreateCount { get; set; }
    public List<ActionItemAutomationCandidate> Candidates { get; set; } = [];
}

public sealed class ActionItemAutomationSyncResult
{
    public AiOperationReceipt Receipt { get; set; } = new();
    public int CreatedCount { get; set; }
    public int SkippedExistingCount { get; set; }
    public List<ActionItem> Created { get; set; } = [];
}
