using System.Text.RegularExpressions;
using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Services;

public sealed class ActionItemAutomationService(
    AppDbContext db,
    AiBusinessReviewService review,
    AiOperationsService operations)
{
    private const string AutomationKeyPrefix = "AUTOMATION KEY:";

    public async Task<ActionItemAutomationPreview> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var (candidates, reviewFindingCount, reconciliationFindingCount) = await BuildCandidatesAsync(cancellationToken);
        var openKeys = await LoadOpenAutomationKeysAsync(cancellationToken);
        var marked = candidates
            .Select(candidate => candidate with { AlreadyOpen = openKeys.Contains(candidate.Key) })
            .ToList();

        return new ActionItemAutomationPreview
        {
            Receipt = Receipt(),
            ReviewFindingCount = reviewFindingCount,
            ReconciliationFindingCount = reconciliationFindingCount,
            CandidateCount = marked.Count,
            AlreadyOpenCount = marked.Count(x => x.AlreadyOpen),
            ReadyToCreateCount = marked.Count(x => !x.AlreadyOpen),
            Candidates = marked
        };
    }

    public async Task<ActionItemAutomationSyncResult> SyncAsync(CancellationToken cancellationToken = default)
    {
        var preview = await PreviewAsync(cancellationToken);
        var created = preview.Candidates
            .Where(candidate => !candidate.AlreadyOpen)
            .Select(ToActionItem)
            .ToList();

        db.ActionItems.AddRange(created);
        await db.SaveChangesAsync(cancellationToken);

        return new ActionItemAutomationSyncResult
        {
            Receipt = Receipt(),
            CreatedCount = created.Count,
            SkippedExistingCount = preview.AlreadyOpenCount,
            Created = created
        };
    }

    private async Task<(List<ActionItemAutomationCandidate> Candidates, int ReviewCount, int ReconciliationCount)> BuildCandidatesAsync(
        CancellationToken cancellationToken)
    {
        var localReview = await review.BuildAsync();
        var reconciliation = await operations.BuildReconciliationAsync(cancellationToken);

        var reviewCandidates = localReview.Items
            .Where(item => !item.Area.Equals("Actions", StringComparison.OrdinalIgnoreCase))
            .Select(FromReview)
            .ToList();
        var reconciliationCandidates = reconciliation.Findings
            .Select(FromReconciliation)
            .ToList();

        return ([.. reviewCandidates, .. reconciliationCandidates], reviewCandidates.Count, reconciliationCandidates.Count);
    }

    private async Task<HashSet<string>> LoadOpenAutomationKeysAsync(CancellationToken cancellationToken)
    {
        var notes = await db.ActionItems.AsNoTracking()
            .Where(action => !action.IsArchived && action.Status != "Done")
            .Select(action => action.Notes)
            .ToListAsync(cancellationToken);

        return notes
            .Select(FindAutomationKey)
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static ActionItemAutomationCandidate FromReview(AiBusinessReviewItem item)
    {
        var key = $"review|{Slug(item.Route)}|{Slug(item.Title)}";
        return new(
            key,
            "Verified Local Rules Review",
            "LOCAL RULES",
            NormalizePriority(item.Priority),
            NormalizeArea(item.Area),
            Trim(item.Title, 180),
            item.Why,
            item.RecommendedAction,
            Trim(item.Evidence, 140),
            item.Route,
            false);
    }

    private static ActionItemAutomationCandidate FromReconciliation(AiReconciliationFinding finding)
    {
        var recordKey = string.Join("-", finding.Records
            .Select(record => $"{Slug(record.Entity)}-{record.Id}")
            .OrderBy(value => value));
        var key = $"reconciliation|{Slug(finding.Kind)}|{Slug(finding.Title)}|{recordKey}";
        var route = finding.Records.FirstOrDefault()?.Route ?? "aiOperations";
        var related = string.Join(" | ", finding.Records.Select(record => record.Label));

        return new(
            key,
            "Deterministic Duplicate & Reconciliation Check",
            "LOCAL RULES",
            NormalizePriority(finding.Severity),
            AreaFor(finding.Records),
            Trim(finding.Title, 180),
            finding.Why,
            finding.RecommendedAction,
            Trim(related, 140),
            route,
            false);
    }

    private static ActionItem ToActionItem(ActionItemAutomationCandidate candidate) => new()
    {
        Title = candidate.Title,
        Area = candidate.Area,
        Priority = candidate.Priority,
        Status = "Open",
        RelatedRecord = candidate.RelatedRecord,
        Notes = $"""
            AUTOMATION SOURCE: {candidate.Source}
            ASSISTANCE SOURCE: {candidate.AssistanceSource} (fixed, repeatable checks; not model-written)
            {AutomationKeyPrefix} {candidate.Key}
            OPEN AREA: {candidate.Route}

            WHY FLAGGED: {candidate.Why}
            NEXT STEP: {candidate.RecommendedAction}
            EVIDENCE WHEN CREATED: {candidate.RelatedRecord}

            Created only after an explicit "Sync Verified Findings to Actions" confirmation. Re-run the source scan for current evidence.
            """
    };

    private static AiOperationReceipt Receipt() => new()
    {
        Engine = "LOCAL RULES",
        UsedAi = false,
        Provider = "Verified business-review and reconciliation rules",
        Reads = ["Active ledger rows", "Review flags", "Calculated costs and balances", "Duplicate and reconciliation comparisons", "Open generated Action Items"],
        Writes = ["New Action Items only after explicit Sync Verified Findings confirmation"],
        UseWhen = "After a review or comparison finds missing proof, questionable calculations, overdue balances, pricing problems, duplicates, or mismatched records.",
        Safety = "No silent writes. Model-generated prose is never converted into tasks. Existing open generated tasks are not duplicated."
    };

    private static string AreaFor(List<AiRecordReference> records)
    {
        var areas = records
            .Select(record => record.Route switch
            {
                "sales" => "Sales",
                "receivables" => "AR",
                "invoiceRecords" => "Invoice",
                "products" => "Product",
                "expenses" => "Expense",
                "customerJobs" => "Operations",
                "parties" or "customers" => "Customer",
                _ => "Audit"
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return areas.Count == 1 ? areas[0] : "Audit";
    }

    private static string NormalizePriority(string value) => value is "High" or "Low" ? value : "Normal";

    private static string NormalizeArea(string value) => value switch
    {
        "Expenses" => "Expense",
        "Products" => "Product",
        _ => Trim(value, 80)
    };

    private static string FindAutomationKey(string? notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return string.Empty;
        var match = Regex.Match(notes, @"(?im)^AUTOMATION KEY:\s*(?<key>[^\r\n]+)");
        return match.Success ? match.Groups["key"].Value.Trim() : string.Empty;
    }

    private static string Slug(string? value) =>
        Regex.Replace(value ?? string.Empty, @"[^a-z0-9]+", "-", RegexOptions.IgnoreCase).Trim('-').ToLowerInvariant();

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = value.Trim();
        return cleaned.Length <= max ? cleaned : cleaned[..(max - 3)].TrimEnd() + "...";
    }
}
