namespace EPATA.BusinessLedger.Models;

public sealed class AiBusinessReviewResult
{
    public string Engine { get; set; } = "Local rules / ledger analysis";
    public bool UsedAi { get; set; }
    public DateTimeOffset GeneratedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public string Safety { get; set; } = "Read-only review. No records are created, edited, saved, or sent.";
    public string? ModelSummary { get; set; }
    public List<string> ModelRecommendedOrder { get; set; } = [];
    public List<string> ModelQuestions { get; set; } = [];
    public List<AiBusinessReviewItem> Items { get; set; } = [];
}

public sealed record AiBusinessReviewItem(
    string Priority,
    string Area,
    string Title,
    string Why,
    string RecommendedAction,
    string Route,
    string Engine,
    string Evidence);

public sealed class AiBusinessReviewNarrative
{
    public string Summary { get; set; } = string.Empty;
    public List<string> RecommendedOrder { get; set; } = [];
    public List<string> Questions { get; set; } = [];
}
