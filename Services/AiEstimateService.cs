using System.Net.Http.Headers;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Services;

public sealed class AiEstimateService(HttpClient httpClient, IConfiguration configuration, IWebHostEnvironment environment, LocalAiService localAi, AppDbContext db)
{
    public const int MaxCombinedTextCharacters = 500_000;
    public const int MaxUploadFiles = 25;
    public const int MaxUploadFileBytes = 20 * 1024 * 1024;
    public const int MaxTotalUploadBytes = 75 * 1024 * 1024;
    public const int MaxSourceUrls = 20;
    public const int MaxImages = 10;
    public const int MaxModelSourceCharacters = 32_000;
    private const int MaxModelOutputTokens = 4_096;
    private const int MaxClarificationChatOutputTokens = 512;
    private static readonly TimeSpan DraftModelTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ClarificationModelTimeout = TimeSpan.FromSeconds(25);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private string InstructionsPath => Path.Combine(environment.ContentRootPath, "AiEstimateInstructions.json");
    private string BuilderPath => Path.Combine(environment.ContentRootPath, "wwwroot", "invoice-builder", "index.html");
    private string AiFeaturesPath => Path.Combine(environment.ContentRootPath, "AI_FEATURES.md");

    private static readonly string[] BuilderFieldIds =
    [
        "docType", "pageSize", "docNumber", "docDate", "dueDate", "docStatus",
        "preparedFor", "customerName", "customerPhone", "customerAddress", "customerEmail",
        "projectName", "material", "color", "infill", "projectDescription", "projectNotes",
        "docDiscount", "docRushPercent", "docTaxRate", "amountPaid", "paymentMethod",
        "pricingGuide", "termsNotes", "standardTurnaround", "rushTurnaround",
        "grams", "gramRate", "hours", "hourRate", "designHours", "designRate",
        "setupFee", "postFee", "difficulty", "rush", "discount", "taxRate", "minimum"
    ];

    public async Task<object> StatusAsync(CancellationToken cancellationToken = default)
    {
        var localStatus = await localAi.GetStatusAsync(false, cancellationToken);
        var localConnection = await localAi.GetReadyConnectionAsync(cancellationToken);
        var endpoint = configuration["Ai:Endpoint"];
        var model = configuration["Ai:Model"];
        var keyVariable = GetConfiguredAiApiKeyEnvironmentVariable();
        var apiKeyPresent = !string.IsNullOrWhiteSpace(GetConfiguredAiApiKey());
        var hostedEnabled = configuration.GetValue<bool>("Ai:AllowHostedFallback");
        var hostedConfigured = configuration.GetValue<bool>("Ai:AllowHostedFallback")
            && !string.IsNullOrWhiteSpace(endpoint)
            && !string.IsNullOrWhiteSpace(model)
            && apiKeyPresent;
        return new
        {
            configured = localConnection is not null || hostedConfigured,
            provider = localConnection?.Provider ?? (hostedConfigured ? configuration["Ai:Provider"] ?? "OpenAI-compatible" : "Local rules fallback"),
            model = localConnection?.Model ?? model ?? string.Empty,
            localAi = localStatus,
            cloudAi = new
            {
                enabled = hostedEnabled,
                configured = hostedConfigured,
                provider = configuration["Ai:Provider"] ?? "OpenAI-compatible",
                endpoint = endpoint ?? string.Empty,
                model = model ?? string.Empty,
                apiKeyEnvironmentVariable = keyVariable,
                apiKeyPresent,
                accountEmail = configuration["Ai:AccountEmail"] ?? string.Empty,
                billing = "Provider-side billing is controlled in the API account. If the provider reports billing disabled, enable billing for this account or use Local AI/local rules."
            },
            instructionsPath = InstructionsPath,
            builderPath = BuilderPath,
            builderFields = await LoadBuilderFieldsAsync(cancellationToken),
            productCatalogCount = await db.Products.AsNoTracking().CountAsync(x => !x.IsArchived, cancellationToken),
            supportedUploads = AiSourceDocumentTextExtractor.SupportedExtensions.Concat([".png", ".jpg", ".jpeg", ".webp"]).ToArray(),
            supportedSources = new[] { "Pasted text and email chains", "Public HTTPS product/source URLs", "Product/reference pictures", "PDF, DOCX, text, email, CSV, and JSON files" },
            limits = new
            {
                maxFiles = MaxUploadFiles,
                maxFileMegabytes = MaxUploadFileBytes / 1024 / 1024,
                maxTotalUploadMegabytes = MaxTotalUploadBytes / 1024 / 1024,
                maxSourceUrls = MaxSourceUrls,
                maxCombinedTextCharacters = MaxCombinedTextCharacters,
                maxModelSourceCharacters = MaxModelSourceCharacters,
                maxExtractedCharactersPerFile = AiSourceDocumentTextExtractor.MaxExtractedCharactersPerFile
            },
            visionRequiresConfiguredAi = true,
            safety = "Review-first: analysis opens an unsaved estimate draft and never saves or sends automatically. Local AI is preferred; cloud fallback runs only when enabled, configured, and the API-key environment variable is present. URL fetching allows public HTTPS pages only."
        };
    }

    public async Task<AiEstimateDraftResult> CreateDraftAsync(AiEstimateDraftRequest request, CancellationToken cancellationToken)
    {
        var prepared = await PrepareSourcesAsync(request, cancellationToken);
        if (prepared.Text.Length < 5 && prepared.Images.Count == 0)
        {
            throw new InvalidOperationException("Paste text, add a public HTTPS source URL, or upload a supported document/image before analyzing.");
        }

        if (prepared.Text.Length > MaxCombinedTextCharacters)
        {
            throw new InvalidOperationException($"The combined extracted text is too large. Keep it under {MaxCombinedTextCharacters:N0} characters.");
        }

        var instructions = await LoadInstructionsAsync(cancellationToken);
        var builderFields = await LoadBuilderFieldsAsync(cancellationToken);
        var products = await LoadProductCatalogAsync(cancellationToken);
        var appGuidance = await LoadEstimateHelpGuidanceAsync(cancellationToken);
        var useLocalOnlyForThinGmailWrapper = ShouldUseLocalDraftWithoutModel(prepared);
        var connection = useLocalOnlyForThinGmailWrapper ? null : await ResolveProviderConnectionAsync(cancellationToken);
        if (connection is not null)
        {
            try
            {
                var modelSource = BuildModelSource(prepared.Text);
                using var modelTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                modelTimeout.CancelAfter(DraftModelTimeout);
                var aiResult = await CallConfiguredProviderAsync(modelSource.Text, request.SourceName, prepared.Images, instructions, builderFields, products, appGuidance, connection.ChatEndpoint, connection.Model, connection.UseApiKey, modelTimeout.Token);
                aiResult.Provider = connection.Provider;
                aiResult.UsedAi = true;
                aiResult.ExecutionReceipt.Provider = connection.Provider;
                aiResult.ExecutionReceipt.SourceCharacters = prepared.Text.Length;
                aiResult.ExecutionReceipt.ModelInputCharacters = modelSource.Text.Length;
                aiResult.ExecutionReceipt.SourceWasCondensed = modelSource.WasCondensed;
                aiResult.InstructionsPath = InstructionsPath;
                ApplySourcePlanningAssumptions(aiResult, prepared.Text, instructions);
                NormalizeResult(aiResult, request.SourceName, instructions, prepared.Text);
                AddSourceBudgetWarning(aiResult, prepared.Text);
                aiResult.Warnings.InsertRange(0, prepared.Warnings);
                if (modelSource.WasCondensed)
                {
                    aiResult.Warnings.Insert(0, $"The source packet contained {prepared.Text.Length:N0} characters. The app selected the most quote-relevant {modelSource.Text.Length:N0} characters for the model to stay within its context window.");
                }
                return aiResult;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                var fallback = BuildLocalDraft(prepared.Text, request.SourceName, prepared.Images, instructions, products);
                fallback.ExecutionReceipt.SourceCharacters = prepared.Text.Length;
                fallback.ExecutionReceipt.ModelInputCharacters = 0;
                ApplySourcePlanningAssumptions(fallback, prepared.Text, instructions);
                NormalizeResult(fallback, request.SourceName, instructions, prepared.Text);
                AddSourceBudgetWarning(fallback, prepared.Text);
                fallback.Warnings.InsertRange(0, prepared.Warnings);
                var reason = ex is OperationCanceledException
                    ? $"Configured AI call exceeded {DraftModelTimeout.TotalSeconds:0} seconds, so local rules were used."
                    : $"Configured AI call failed, so local rules were used: {ex.Message}";
                fallback.Warnings.Insert(0, reason);
                return fallback;
            }
        }

        var local = BuildLocalDraft(prepared.Text, request.SourceName, prepared.Images, instructions, products);
        local.ExecutionReceipt.SourceCharacters = prepared.Text.Length;
        local.ExecutionReceipt.ModelInputCharacters = 0;
        ApplySourcePlanningAssumptions(local, prepared.Text, instructions);
        NormalizeResult(local, request.SourceName, instructions, prepared.Text);
        AddSourceBudgetWarning(local, prepared.Text);
        local.Warnings.InsertRange(0, prepared.Warnings);
        if (useLocalOnlyForThinGmailWrapper)
        {
            local.Warnings.Insert(0, "The pasted Gmail content looked like an outgoing estimate wrapper, so local cleanup rules drafted from the subject instead of waiting on the model.");
        }
        return local;
    }

    public async Task<AiEstimateChatResult> ChatAsync(AiEstimateChatRequest request, CancellationToken cancellationToken)
    {
        if (TryBuildQuickClarificationResult(request) is { } quickResult)
        {
            return quickResult;
        }

        var prepared = await PrepareSourcesAsync(
            new AiEstimateDraftRequest(request.SourceText, request.SourceName, request.SourceUrls, request.Images, request.SourceWarnings),
            cancellationToken);
        if (prepared.Text.Length < 5 && prepared.Images.Count == 0)
        {
            throw new InvalidOperationException("Add source context or upload at least one file/image before chatting with Local AI.");
        }

        var connection = await localAi.GetReadyConnectionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Local AI is off or no model is loaded. The assistant modal can start LM Studio, but a GGUF model must be selected and ready before chat.");
        var instructions = await LoadInstructionsAsync(cancellationToken);
        var builderFields = await LoadBuilderFieldsAsync(cancellationToken);
        var products = await LoadProductCatalogAsync(cancellationToken);
        var modelSource = BuildModelSource(prepared.Text);
        var relevantProducts = products
            .Where(product => (!string.IsNullOrWhiteSpace(product.Name) && modelSource.Text.Contains(product.Name, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(product.Sku) && modelSource.Text.Contains(product.Sku, StringComparison.OrdinalIgnoreCase)))
            .Take(25)
            .ToList();
        var recentMessages = (request.Messages ?? [])
            .Where(message => !string.IsNullOrWhiteSpace(message.Content))
            .TakeLast(10)
            .Select(message => $"{NormalizeChatRole(message.Role)}: {Trim(message.Content, 1_200)}")
            .ToList();

        var systemPrompt = $"""
            You are the EPATA local estimate intake assistant.
            You are running only to help gather and clarify context before the app builds an estimate or invoice draft.
            Use only the source packet, current document snapshot, recent chat, editable estimate instructions, HTML builder field contract, and saved product/cost catalog provided below.
            Do not invent proof, certifications, customer approvals, tax advice, or exact slicer data.
            When details are missing, ask focused follow-up questions and state safe planning assumptions.
            Reply in at most 90 words. Use at most 5 bullets. Start with the final answer immediately; do not write analysis.
            If the user asks whether the draft is ready, answer with Ready / Not ready plus the few missing items that matter most.
            Never save, send, post, or claim that you changed the ledger.

            HTML BUILDER FIELD IDS PRESENT:
            {JsonSerializer.Serialize(builderFields, JsonOptions)}

            SAVED PRODUCT / COST CATALOG:
            {JsonSerializer.Serialize(relevantProducts, JsonOptions)}

            EDITABLE ESTIMATE INSTRUCTIONS:
            {JsonSerializer.Serialize(instructions, JsonOptions)}
            """;
        var userPrompt = $"""
            Source: {request.SourceName ?? "Estimate assistant packet"}

            CURRENT DOCUMENT SNAPSHOT:
            {Trim(request.CurrentDocumentContext, 4_000)}

            SOURCE PACKET:
            {modelSource.Text}

            RECENT CHAT:
            {string.Join("\n", recentMessages)}

            USER QUESTION:
            {Trim(request.Question, 2_000)}
            """;

        string answer;
        using (var modelTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            modelTimeout.CancelAfter(ClarificationModelTimeout);
            try
            {
                answer = prepared.Images.Count > 0
                    ? await localAi.CompleteTextWithImagesAsync(systemPrompt, userPrompt, prepared.Images, modelTimeout.Token, MaxClarificationChatOutputTokens)
                    : await localAi.CompleteTextAsync(systemPrompt, userPrompt, modelTimeout.Token, MaxClarificationChatOutputTokens);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return BuildQuickClarificationTimeoutResult(request, prepared, modelSource, connection);
            }
        }

        return new AiEstimateChatResult
        {
            Provider = connection.Provider,
            UsedAi = true,
            SourceName = request.SourceName ?? "Estimate assistant packet",
            Answer = Trim(answer, 4_000),
            ExecutionReceipt = new AiExecutionReceipt
            {
                Engine = "AI MODEL",
                UsedAi = true,
                Provider = connection.Provider,
                Model = connection.Model,
                ExecutedAtUtc = DateTimeOffset.UtcNow,
                SourceCharacters = prepared.Text.Length,
                ModelInputCharacters = modelSource.Text.Length,
                SourceWasCondensed = modelSource.WasCondensed
            },
            Warnings = prepared.Warnings
        };
    }

    private static AiEstimateChatResult? TryBuildQuickClarificationResult(AiEstimateChatRequest request)
    {
        var question = request.Question ?? string.Empty;
        if (string.IsNullOrWhiteSpace(question))
        {
            return null;
        }

        if (IsQuickPurposeQuestion(question))
        {
            return BuildQuickClarificationResult(
                request,
                "I help turn customer messages, files, URLs, and current builder fields into a reviewable estimate or invoice draft. I can spot missing quote details and planning assumptions. I never save, send, or change the ledger automatically.");
        }

        if (IsQuickMissingDetailsQuestion(question))
        {
            return BuildQuickClarificationResult(request, BuildMissingDetailsAnswer(MergeQuickClarificationContext(request.SourceText, request.CurrentDocumentContext)));
        }

        return null;
    }

    private static AiEstimateChatResult BuildQuickClarificationTimeoutResult(
        AiEstimateChatRequest request,
        PreparedAiSources prepared,
        PreparedModelSource modelSource,
        LocalAiConnection connection)
    {
        var answer = $"""
            The loaded local model is still taking too long, so I stopped waiting after {ClarificationModelTimeout.TotalSeconds:0} seconds.

            {BuildMissingDetailsAnswer(MergeQuickClarificationContext(prepared.Text, request.CurrentDocumentContext))}
            """;

        return BuildQuickClarificationResult(
            request,
            answer,
            prepared.Text.Length,
            modelSource.Text.Length,
            modelSource.WasCondensed,
            connection.Model,
            [
                ..prepared.Warnings,
                "The loaded model exceeded the quick chat timeout. Switch LM Studio to a smaller non-reasoning model for faster custom answers."
            ]);
    }

    private static AiEstimateChatResult BuildQuickClarificationResult(
        AiEstimateChatRequest request,
        string answer,
        int? sourceCharacters = null,
        int modelInputCharacters = 0,
        bool sourceWasCondensed = false,
        string? model = null,
        List<string>? warnings = null)
    {
        var sourceContext = MergeQuickClarificationContext(request.SourceText, request.CurrentDocumentContext);
        return new AiEstimateChatResult
        {
            Provider = "Local quick rules",
            UsedAi = false,
            SourceName = request.SourceName ?? "Estimate assistant packet",
            Answer = Trim(answer, 4_000),
            ExecutionReceipt = new AiExecutionReceipt
            {
                Engine = "LOCAL QUICK RULES",
                UsedAi = false,
                Provider = "Local quick rules",
                Model = model,
                ExecutedAtUtc = DateTimeOffset.UtcNow,
                SourceCharacters = sourceCharacters ?? sourceContext.Length,
                ModelInputCharacters = modelInputCharacters,
                SourceWasCondensed = sourceWasCondensed
            },
            Warnings = warnings ?? []
        };
    }

    private static bool IsQuickPurposeQuestion(string question) => Regex.IsMatch(
        question,
        @"(?i)\b(?:purpose|what\s+(?:is|are)\s+you|what\s+do\s+you\s+do|what\s+can\s+you\s+do|why\s+are\s+you\s+here)\b",
        RegexOptions.CultureInvariant);

    private static bool IsQuickMissingDetailsQuestion(string question) => Regex.IsMatch(
        question,
        @"(?i)\b(?:missing|what\s+details|details\s+do\s+i\s+need|what\s+questions|questions?\s+should|ask\s+before|ready|reliable|clarif(?:y|ication)|need\s+before|next\s+questions?)\b",
        RegexOptions.CultureInvariant);

    private static string BuildMissingDetailsAnswer(string source)
    {
        var missing = new List<string>();
        var found = new List<string>();

        AddMissingOrFound(missing, found, HasDimensionsOrReference(source), "Dimensions, CAD/STL/reference file, or measured sketch", "dimensions/reference");
        AddMissingOrFound(missing, found, HasQuantity(source), "Quantity", "quantity");
        AddMissingOrFound(missing, found, HasMaterial(source), "Material", "material");
        AddMissingOrFound(missing, found, HasColorOrFinish(source), "Color and finish", "color/finish");
        AddMissingOrFound(missing, found, HasUseOrTolerance(source), "Use case, load/strength, fit, and tolerance needs", "use/tolerance");
        AddMissingOrFound(missing, found, HasDeadline(source), "Deadline or turnaround", "deadline");

        if (!HasCustomerContact(source))
        {
            missing.Add("Customer name/contact for an invoice-ready draft");
        }

        if (missing.Count == 0)
        {
            return "Ready enough for a reviewable draft. Still verify measurements, price, tax, turnaround, payment, and customer approval before saving or sending.";
        }

        var heading = missing.Count <= 2
            ? "Ready enough for a rough draft, but confirm:"
            : "Not ready for a reliable estimate yet. Missing:";
        var foundText = found.Count > 0
            ? $"\n\nAlready found: {string.Join(", ", found.Take(5))}."
            : string.Empty;

        return $"""
            {heading}
            {string.Join("\n", missing.Take(6).Select(item => "- " + item))}
            {foundText}

            For an invoice draft, also confirm the customer approved the quote and how they will pay.
            """;
    }

    private static void AddMissingOrFound(List<string> missing, List<string> found, bool hasValue, string missingLabel, string foundLabel)
    {
        if (hasValue)
        {
            found.Add(foundLabel);
        }
        else
        {
            missing.Add(missingLabel);
        }
    }

    private static string MergeQuickClarificationContext(params string?[] parts)
    {
        var merged = string.Join("\n\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim()));
        return Regex.Replace(merged, @"[^\S\r\n]+", " ").Trim();
    }

    private static bool HasDimensionsOrReference(string source) => Regex.IsMatch(
        source,
        @"(?i)(?:\b\d+(?:\.\d+)?\s*(?:mm|cm|in|inch|inches|"")\b|(?:stl|step|3mf|obj|cad|drawing|sketch|reference\s+file|dimensions?|measurements?))",
        RegexOptions.CultureInvariant);

    private static bool HasQuantity(string source) => Regex.IsMatch(
        source,
        @"(?i)\b(?:(?:\d+|one|two|three|four|five|six|seven|eight|nine|ten)\s*(?:x|pcs?|pieces?|copies?|units?|prints?)|quantity\s*[:=]\s*\d+)\b",
        RegexOptions.CultureInvariant);

    private static bool HasMaterial(string source) => Regex.IsMatch(
        source,
        @"(?i)\b(?:PLA|PETG|ABS|ASA|TPU|nylon|resin|carbon\s*fiber|wood\s*fill|silk\s*pla|matte\s*pla)\b",
        RegexOptions.CultureInvariant);

    private static bool HasColorOrFinish(string source) => Regex.IsMatch(
        source,
        @"(?i)\b(?:black|white|red|blue|green|yellow|orange|purple|pink|gray|grey|silver|gold|clear|transparent|matte|glossy|silk|sanded|painted|finish(?:ed)?|colour|color)\b",
        RegexOptions.CultureInvariant);

    private static bool HasUseOrTolerance(string source) => Regex.IsMatch(
        source,
        @"(?i)\b(?:load|strength|tolerance|fit|press\s*fit|snap\s*fit|clearance|mount|mounted|screw|bolt|hole|functional|prototype|outdoor|heat|food|use\s+case)\b",
        RegexOptions.CultureInvariant);

    private static bool HasDeadline(string source) => Regex.IsMatch(
        source,
        @"(?i)\b(?:deadline|due|turnaround|rush|ship|deliver|pickup|pick\s*up|today|tomorrow|this\s+week|next\s+week|by\s+\w+|\b\d{1,2}/\d{1,2}\b)\b",
        RegexOptions.CultureInvariant);

    private static bool HasCustomerContact(string source) => Regex.IsMatch(
        source,
        @"(?im)^(?:to|from|hey|hi|hello)\s+[A-Z][a-z]+|\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b|(?<!\d)(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}(?!\d)",
        RegexOptions.CultureInvariant);

    private async Task<ResolvedAiConnection?> ResolveProviderConnectionAsync(CancellationToken cancellationToken)
    {
        var localConnection = await localAi.GetReadyConnectionAsync(cancellationToken);
        if (localConnection is not null)
        {
            return new ResolvedAiConnection(localConnection.ChatEndpoint, localConnection.Model, localConnection.Provider, false);
        }

        var endpoint = configuration["Ai:Endpoint"];
        var model = configuration["Ai:Model"];
        return !configuration.GetValue<bool>("Ai:AllowHostedFallback")
            || string.IsNullOrWhiteSpace(endpoint)
            || string.IsNullOrWhiteSpace(model)
            || string.IsNullOrWhiteSpace(GetConfiguredAiApiKey())
            ? null
            : new ResolvedAiConnection(endpoint, model, $"{configuration["Ai:Provider"] ?? "AI"} / {model}", true);
    }

    private async Task<AiEstimateInstructions> LoadInstructionsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(InstructionsPath))
        {
            throw new FileNotFoundException("AI estimate instructions file was not found.", InstructionsPath);
        }

        await using var stream = File.OpenRead(InstructionsPath);
        return await JsonSerializer.DeserializeAsync<AiEstimateInstructions>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("AI estimate instructions file is empty or invalid.");
    }

    private async Task<string[]> LoadBuilderFieldsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(BuilderPath)) return [];
        var html = await File.ReadAllTextAsync(BuilderPath, cancellationToken);
        return BuilderFieldIds
            .Where(id => Regex.IsMatch(html, $@"\bid\s*=\s*[""']{Regex.Escape(id)}[""']", RegexOptions.IgnoreCase))
            .ToArray();
    }

    private async Task<string> LoadEstimateHelpGuidanceAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AiFeaturesPath)) return "AI_FEATURES.md was not found. Use editable estimate instructions and builder fields as the source of truth.";
        var text = await File.ReadAllTextAsync(AiFeaturesPath, cancellationToken);
        var headings = new[]
        {
            "## Local AI model",
            "### AI Estimate Intake",
            "### AI Estimate Intake fallback",
            "### AI Estimate Intake limits",
            "## Editable AI pricing instructions",
            "## What never happens automatically"
        };
        var excerpts = headings
            .Select(heading => MarkdownSection(text, heading))
            .Where(section => !string.IsNullOrWhiteSpace(section))
            .ToList();
        var guidance = excerpts.Count > 0 ? string.Join("\n\n", excerpts) : text;
        return Trim(guidance, 6_000);
    }

    private async Task<List<AiProductPricingContext>> LoadProductCatalogAsync(CancellationToken cancellationToken) => await db.Products
        .AsNoTracking()
        .Where(x => !x.IsArchived)
        .OrderBy(x => x.Name)
        .Take(250)
        .Select(x => new AiProductPricingContext(
            x.Name,
            x.Sku,
            x.Category,
            x.Material,
            x.Color,
            x.Grams,
            x.MaterialCostPerGram,
            x.PrintHours,
            x.MachineRatePerHour,
            x.PackagingCost,
            x.DesignMinutes,
            x.TargetPrice,
            x.Notes))
        .ToListAsync(cancellationToken);

    private async Task<PreparedAiSources> PrepareSourcesAsync(AiEstimateDraftRequest request, CancellationToken cancellationToken)
    {
        var parts = new List<string>();
        var warnings = new List<string>(request.SourceWarnings ?? []);
        if (!string.IsNullOrWhiteSpace(request.SourceText))
        {
            var cleanedSourceText = CleanPastedSourceText(request.SourceText);
            if (ContainsGmailChrome(request.SourceText) && !string.Equals(cleanedSourceText, request.SourceText.Trim(), StringComparison.Ordinal))
            {
                warnings.Add("Removed Gmail interface text from the pasted source before drafting.");
            }
            parts.Add(cleanedSourceText);
        }

        var allUrls = (request.SourceUrls ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (allUrls.Count > MaxSourceUrls)
        {
            warnings.Add($"Only the first {MaxSourceUrls} distinct URLs were read.");
        }

        var urls = allUrls.Take(MaxSourceUrls);
        foreach (var rawUrl in urls)
        {
            if (!Uri.TryCreate(rawUrl.Trim(), UriKind.Absolute, out var uri)
                || !await IsSafePublicHttpsUriAsync(uri, cancellationToken))
            {
                warnings.Add($"Skipped URL: {rawUrl}. AI intake fetches public HTTPS pages only; local, private-network, and unsafe addresses are blocked.");
                continue;
            }

            try
            {
                parts.Add(await FetchSourcePageTextAsync(uri, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                warnings.Add($"Could not read source page {uri}: {Trim(ex.Message, 180)}");
                parts.Add($"""
                    SOURCE PAGE: Source page needs review
                    SOURCE URL: {uri}
                    SOURCE DESCRIPTION: Page metadata could not be fetched.
                    SOURCE PRICE: Needs review
                    """);
            }
        }

        var images = new List<AiEstimateImageInput>();
        foreach (var image in (request.Images ?? []).Take(MaxImages))
        {
            if (!image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrWhiteSpace(image.Base64Data)
                || image.Base64Data.Length > (MaxUploadFileBytes * 4 / 3) + 16)
            {
                warnings.Add($"Skipped unsupported or oversized image: {image.FileName}");
                continue;
            }
            images.Add(image);
        }

        if (images.Count > 0)
        {
            parts.Add("UPLOADED REFERENCE PICTURES:\n" + string.Join('\n', images.Select(x => $"- {x.FileName}")));
        }

        return new PreparedAiSources(string.Join("\n\n", parts), images, warnings);
    }

    private static PreparedModelSource BuildModelSource(string source)
    {
        var cleaned = Regex.Replace(source, @"[^\S\r\n]+", " ").Trim();
        if (cleaned.Length <= MaxModelSourceCharacters)
        {
            return new PreparedModelSource(cleaned, false);
        }

        var relevantPattern = new Regex(
            @"(?i)(?:\$\s*\d|\b\d+\s*(?:x|×|pieces?|pcs?|units?|copies|grams?|g\b|hours?|hrs?|mm|cm|inches?|colors?|colours?|days?|weeks?)\b|quote|estimate|budget|price|cost|design|artwork|logo|proof|prototype|sample|print|production|quantity|material|filament|pla|petg|abs|asa|tpu|resin|color|colour|deadline|turnaround|deliver|size|thick|dimension|cleanup|sand|packag|approved|approve|agreed)",
            RegexOptions.Compiled);
        var chunks = Regex.Split(cleaned, @"(?<=[.!?])\s+|\r?\n+")
            .Select(chunk => chunk.Trim())
            .Where(chunk => chunk.Length >= 3)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var selected = new List<string>
        {
            "SOURCE PACKET WAS CONDENSED FOR MODEL CONTEXT. Use these quote-relevant excerpts and disclose assumptions."
        };
        selected.AddRange(chunks.Where(chunk => relevantPattern.IsMatch(chunk)));
        selected.AddRange(chunks.Take(12));
        selected.AddRange(chunks.TakeLast(12));

        var builder = new StringBuilder(MaxModelSourceCharacters);
        foreach (var chunk in selected.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (builder.Length + chunk.Length + 2 > MaxModelSourceCharacters) continue;
            builder.AppendLine(chunk).AppendLine();
        }
        return new PreparedModelSource(builder.ToString().Trim(), true);
    }

    private async Task<string> FetchSourcePageTextAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        var uri = initialUri;
        for (var redirect = 0; redirect <= 4; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 EPATA-Business-Ledger/1.0");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var nextUri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (!await IsSafePublicHttpsUriAsync(nextUri, cancellationToken))
                {
                    throw new InvalidOperationException("The page redirected to a blocked or non-public address.");
                }
                uri = nextUri;
                continue;
            }
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"The page returned {(int)response.StatusCode}.");
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) && !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The URL returned {mediaType}, not a readable web page.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);
            var buffer = new char[2_000_000];
            var read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
            var html = new string(buffer, 0, read);
            var isEtsy = uri.Host.Equals("etsy.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".etsy.com", StringComparison.OrdinalIgnoreCase);
            var title = HtmlMeta(html, "og:title") ?? HtmlTitle(html) ?? uri.Host;
            var description = HtmlMeta(html, "og:description") ?? HtmlMeta(html, "description") ?? string.Empty;
            var price = HtmlMeta(html, "product:price:amount") ?? FirstMatch(html, @"""price""\s*:\s*""?(?<value>\d+(?:\.\d{1,2})?)");
            var label = isEtsy ? "ETSY LISTING" : "SOURCE PAGE";
            return $"""
                {label}: {Trim(title, 180)}
                SOURCE URL: {uri}
                SOURCE DESCRIPTION: {Trim(description, 1_500)}
                SOURCE PRICE: {(string.IsNullOrWhiteSpace(price) ? "Needs review" : "$" + price)}
                """;
        }

        throw new InvalidOperationException("The page redirected too many times.");
    }

    private static async Task<bool> IsSafePublicHttpsUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.Scheme != Uri.UriSchemeHttps
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrWhiteSpace(uri.UserInfo)
            || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(uri.DnsSafeHost, cancellationToken);
            return addresses.Length > 0 && addresses.All(IsPublicAddress);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsPublicAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Any)
            || address.Equals(IPAddress.IPv6Any)
            || address.Equals(IPAddress.None)
            || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] != 10
                && bytes[0] != 127
                && !(bytes[0] == 169 && bytes[1] == 254)
                && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
                && !(bytes[0] == 192 && bytes[1] == 168)
                && !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127)
                && !(bytes[0] == 0)
                && !(bytes[0] >= 224);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return !address.IsIPv6LinkLocal
                && !address.IsIPv6Multicast
                && !address.IsIPv6SiteLocal
                && (bytes[0] & 0xFE) != 0xFC;
        }

        return false;
    }

    private static string? HtmlMeta(string html, string property)
    {
        var escaped = Regex.Escape(property);
        var patterns = new[]
        {
            $@"(?is)<meta[^>]+(?:property|name)\s*=\s*[""']{escaped}[""'][^>]+content\s*=\s*[""'](?<value>.*?)[""']",
            $@"(?is)<meta[^>]+content\s*=\s*[""'](?<value>.*?)[""'][^>]+(?:property|name)\s*=\s*[""']{escaped}[""']"
        };
        return patterns.Select(pattern => FirstMatch(html, pattern)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) is { } value
            ? WebUtility.HtmlDecode(value).Trim()
            : null;
    }

    private static string? HtmlTitle(string html)
    {
        var title = FirstMatch(html, @"(?is)<title[^>]*>(?<value>.*?)</title>");
        return string.IsNullOrWhiteSpace(title) ? null : WebUtility.HtmlDecode(title).Trim();
    }

    private async Task<AiEstimateDraftResult> CallConfiguredProviderAsync(
        string source,
        string? sourceName,
        List<AiEstimateImageInput> images,
        AiEstimateInstructions instructions,
        string[] builderFields,
        List<AiProductPricingContext> products,
        string appGuidance,
        string endpoint,
        string model,
        bool useApiKey,
        CancellationToken cancellationToken)
    {
        var relevantProducts = products
            .Where(product => (!string.IsNullOrWhiteSpace(product.Name) && source.Contains(product.Name, StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(product.Sku) && source.Contains(product.Sku, StringComparison.OrdinalIgnoreCase)))
            .Take(25)
            .ToList();
        var schema = """
            Return JSON with this exact top-level structure:
            {
              "prefill": {
                "docType": "ESTIMATE",
                "status": "Draft",
                "customerName": null,
                "customerPhone": null,
                "customerEmail": null,
                "customerAddress": null,
                "preparedFor": null,
                "projectName": null,
                "material": null,
                "color": null,
                "infill": null,
                "projectDescription": null,
                "projectNotes": null,
                "pageSize": "LETTER",
                "docDate": null,
                "dueDate": null,
                "paymentMethod": "Unknown / Review",
                "pricingGuide": null,
                "termsNotes": null,
                "standardTurnaround": null,
                "rushTurnaround": null,
                "docTaxRate": 0,
                "docRushPercent": 0,
                "docDiscount": 0,
                "calcGrams": 0,
                "calcHours": 0,
                "calcDesignHours": 0,
                "calcSetupFee": 0,
                "calcPostFee": 0,
                "calcGramRate": 0.05,
                "calcHourRate": 3,
                "calcDesignRate": 25,
                "calcMinimum": 15,
                "calcDifficulty": 1,
                "calcRush": 0,
                "calcDiscount": 0,
                "calcTaxRate": 0,
                "lineItems": [
                  { "description": "", "details": "", "quantity": 1, "rate": 0 }
                ]
              },
              "questions": [],
              "warnings": []
            }
            """;
        var systemPrompt = $"""
            You prepare review-first estimate drafts for EPATA LLC.
            Never claim the estimate is final. Never invent missing customer requirements.
            A customer name is optional and never blocks preparing a useful estimate.
            Your primary task is to identify the actual job, make disclosed planning assumptions, and return a granular draft quote. Do not collapse a detailed job to the minimum merely because slicer output or a customer name is missing.
            Create a separate line item for each distinct requested/listed product or service.
            Break complex jobs into customer-facing phases such as design/artwork, prototype/sample, production setup and color changes, production run, cleanup/post-processing, and packaging when those phases apply.
            Treat explicitly discussed or accepted amounts as separate line items. Use line-item rates as the actual customer-facing draft quote. Calculator inputs are the underlying cost basis and may be lower than the quoted line-item total.
            Every line-item amount must equal quantity multiplied by rate. Use quantity 1 for one-time phase fees. Use the actual number of grams with the per-gram rate for material rows, and actual machine/design hours with hourly rates for time rows.
            Fill calcGrams, calcHours, calcDesignHours, calcSetupFee, calcPostFee, and all calculator rates for the entire quoted quantity. Do not leave them at 0 when a defensible planning assumption is possible.
            Do not double-charge an accepted fixed design/proof price with a second hourly design line item. Keep design hours in calculator inputs as cost-basis evidence, but quote the accepted fixed design amount once.
            For applicable bulk production, include the editable prototypeSampleFee, multicolorSetupFee, bulkHandlingPerUnit, and postProcessingHourly rates from the instructions. Raw filament and machine cost alone are not a complete customer quote.
            Do not bundle a prototype/sample fee into a setup line when the prototype/sample is already a separate line item. Each fee must appear exactly once.
            When the sources describe roughly 300 slightly oversized 1.2 mm guitar-pick-like pieces, use at least 300 total grams before adding a disclosed multicolor waste allowance.
            Use supplied source-page metadata, uploaded documents, and uploaded pictures as source material. Pictures can identify likely products and features, but uncertain details must become questions or warnings.
            The source packet may contain Gmail interface chrome, screen-reader text, inbox counters, labels, attachment labels, desktop notification prompts, or sent-message boilerplate. Ignore that UI text completely.
            Never use EPATA LLC, Ernest Phillips, epata.llc.co@gmail.com, or other EPATA sender identity as the customer name or customer email.
            If the source is an outgoing EPATA estimate/invoice email, infer the customer from the To line or greeting when clear, but leave customerEmail blank unless a customer email address is visible.
            ProjectDescription must describe the requested physical object/service and work to be quoted. It must not describe Gmail, an inbox, an attachment, screen readers, desktop notifications, or the fact that an estimate was attached.
            Use only the rules and prices in the editable instructions below.
            When the requested item matches the saved product catalog, treat its stored material, grams, print hours, rates, packaging cost, design minutes, and target price as the preferred pricing basis. Use target price as the minimum floor, not as an extra fee.
            Fill every applicable field in the HTML estimate builder contract below.
            Pay special attention to material, total grams for the quoted quantity, material cost per gram, print hours, machine rate, design time, setup, post-processing, difficulty, minimum, rush, discount, and tax.
            The application, not you, performs the final money calculation. Supply honest calculator inputs.
            When exact slicer data is missing, estimate reasonable total grams, machine hours, design hours, setup, and post-processing from the stated quantity, dimensions/thickness, material, color count, complexity, and production method. Clearly disclose each planning assumption in line-item details, project notes, warnings, or questions.
            Use 0 only when no defensible planning assumption can be made. Missing customer identity is never a reason to use 0 or the minimum.
            Keep projectDescription under 500 characters, projectNotes under 900 characters, each line-item description under 100 characters, each line-item details field under 300 characters, and each question/warning under 220 characters.
            Do not repeat or summarize the full conversation. Return at most 10 useful line items, 8 questions, and 8 warnings.
            {schema}

            HTML ESTIMATE BUILDER FIELD IDS PRESENT:
            {JsonSerializer.Serialize(builderFields, JsonOptions)}

            SAVED PRODUCT / COST CATALOG:
            {JsonSerializer.Serialize(relevantProducts, JsonOptions)}

            EDITABLE INSTRUCTIONS:
            {JsonSerializer.Serialize(instructions, JsonOptions)}

            APP HELP / ABOUT GUIDANCE:
            {appGuidance}
            """;
        var userContent = new List<object>
        {
            new { type = "text", text = $"Source: {sourceName ?? "Mixed sources"}\n\n{source}" }
        };
        userContent.AddRange(images.Select(image => (object)new
        {
            type = "image_url",
            image_url = new { url = $"data:{image.ContentType};base64,{image.Base64Data}" }
        }));
        var payload = new
        {
            model,
            temperature = 0.1,
            max_tokens = MaxModelOutputTokens,
            reasoning_effort = "none",
            response_format = BuildEstimateResponseFormat(useApiKey),
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        var apiKey = useApiKey ? GetConfiguredAiApiKey() : null;
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        using var response = await httpClient.SendAsync(message, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"AI provider returned {(int)response.StatusCode}: {Trim(responseText, 500)}");
        }

        using var envelope = JsonDocument.Parse(responseText);
        var responseMessage = envelope.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message");
        var content = responseMessage.GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(content)
            && responseMessage.TryGetProperty("reasoning_content", out var reasoningContent)
            && reasoningContent.ValueKind == JsonValueKind.String)
        {
            content = reasoningContent.GetString();
        }
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("AI provider returned no structured estimate content.");
        }

        var result = JsonSerializer.Deserialize<AiEstimateDraftResult>(NormalizeProviderJson(StripJsonFence(content)), JsonOptions)
            ?? throw new InvalidOperationException("AI provider response could not be parsed as an estimate draft.");
        result.ExecutionReceipt = BuildModelExecutionReceipt(envelope.RootElement, model);
        return result;
    }

    private string GetConfiguredAiApiKeyEnvironmentVariable()
    {
        var configured = configuration["Ai:ApiKeyEnvironmentVariable"];
        return string.IsNullOrWhiteSpace(configured) ? "OPENAI_API_KEY" : configured;
    }

    private string? GetConfiguredAiApiKey()
    {
        var primary = GetConfiguredAiApiKeyEnvironmentVariable();
        foreach (var variable in new[] { primary, "OPENAI_API_KEY", "EPATA_AI_API_KEY" }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var value = Environment.GetEnvironmentVariable(variable);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static object BuildEstimateResponseFormat(bool useApiKey)
    {
        if (useApiKey) return new { type = "json_object" };
        var text = new { type = "string" };
        var shortText = new { type = "string", maxLength = 300 };
        var number = new { type = "number" };
        var lineItem = new
        {
            type = "object",
            properties = new
            {
                description = new { type = "string", maxLength = 100 },
                details = shortText,
                quantity = number,
                rate = number
            },
            required = new[] { "description", "details", "quantity", "rate" },
            additionalProperties = false
        };
        var prefill = new
        {
            type = "object",
            properties = new
            {
                customerName = text,
                customerPhone = text,
                customerEmail = text,
                customerAddress = text,
                projectName = text,
                material = text,
                color = text,
                infill = text,
                projectDescription = new { type = "string", maxLength = 500 },
                projectNotes = new { type = "string", maxLength = 900 },
                calcGrams = number,
                calcHours = number,
                calcDesignHours = number,
                calcSetupFee = number,
                calcPostFee = number,
                calcGramRate = number,
                calcHourRate = number,
                calcDesignRate = number,
                calcMinimum = number,
                calcDifficulty = number,
                calcRush = number,
                calcDiscount = number,
                calcTaxRate = number,
                lineItems = new { type = "array", items = lineItem, minItems = 1, maxItems = 10 }
            },
            required = new[]
            {
                "customerName", "customerPhone", "customerEmail", "customerAddress",
                "projectName", "material", "color", "infill", "projectDescription", "projectNotes",
                "calcGrams", "calcHours", "calcDesignHours", "calcSetupFee", "calcPostFee",
                "calcGramRate", "calcHourRate", "calcDesignRate", "calcMinimum", "calcDifficulty",
                "calcRush", "calcDiscount", "calcTaxRate", "lineItems"
            },
            additionalProperties = true
        };
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "epata_estimate_draft",
                strict = false,
                schema = new
                {
                    type = "object",
                    properties = new
                    {
                        prefill,
                        questions = new { type = "array", items = shortText, maxItems = 8 },
                        warnings = new { type = "array", items = shortText, maxItems = 8 }
                    },
                    required = new[] { "prefill", "questions", "warnings" },
                    additionalProperties = false
                }
            }
        };
    }

    private static AiExecutionReceipt BuildModelExecutionReceipt(JsonElement envelope, string requestedModel)
    {
        var receipt = new AiExecutionReceipt
        {
            Engine = "AI MODEL",
            UsedAi = true,
            Model = ReadString(envelope, "model") ?? requestedModel,
            ResponseId = ReadString(envelope, "id"),
            ExecutedAtUtc = DateTimeOffset.UtcNow
        };
        if (envelope.TryGetProperty("usage", out var usage))
        {
            receipt.PromptTokens = ReadInt(usage, "prompt_tokens");
            receipt.CompletionTokens = ReadInt(usage, "completion_tokens");
            receipt.TotalTokens = ReadInt(usage, "total_tokens");
        }
        return receipt;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static int? ReadInt(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value)) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)) return number;
        return value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number) ? number : null;
    }

    private static string NormalizeProviderJson(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException("AI provider response was not a JSON object.");
        NormalizeStringArray(root, "questions");
        NormalizeStringArray(root, "warnings");
        return root.ToJsonString();
    }

    private static void NormalizeStringArray(JsonObject root, string property)
    {
        if (root[property] is not JsonArray source) return;
        var normalized = new JsonArray();
        foreach (var node in source)
        {
            if (node is null) continue;
            if (node is JsonObject obj)
            {
                var text = FirstNodeText(obj, "question", "warning", "text", "message", "detail");
                if (!string.IsNullOrWhiteSpace(text)) normalized.Add(text);
                continue;
            }
            var value = node.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(value)) normalized.Add(value);
        }
        root[property] = normalized;
    }

    private static string? FirstNodeText(JsonObject obj, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = obj[key]?.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }
        return null;
    }

    private AiEstimateDraftResult BuildLocalDraft(string source, string? sourceName, List<AiEstimateImageInput> images, AiEstimateInstructions instructions, List<AiProductPricingContext> products)
    {
        source = CleanPastedSourceText(source);
        var clean = Regex.Replace(source, @"\s+", " ").Trim();
        var email = FirstCustomerEmail(source);
        var phoneDigits = Regex.Replace(Match(source, @"(?<!\d)(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}(?!\d)") ?? string.Empty, @"\D", "");
        if (phoneDigits.Length == 11 && phoneDigits.StartsWith('1')) phoneDigits = phoneDigits[1..];
        var phone = phoneDigits.Length == 10 ? $"({phoneDigits[..3]}) {phoneDigits.Substring(3, 3)}-{phoneDigits[6..]}" : null;
        var customer = ExtractCustomerName(source);
        var subject = NormalizeProjectTitle(ExtractProjectTitle(source) ?? FirstMatch(source, @"(?im)^\s*subject\s*:\s*(?<value>[^\r\n]{3,120})"));
        var sourcePageTitle = FirstMatch(source, @"(?im)^\s*(?:ETSY LISTING|SOURCE PAGE):\s*(?<value>[^\r\n]{3,180})");
        var savedProduct = products.FirstOrDefault(product =>
            (!string.IsNullOrWhiteSpace(product.Sku) && source.Contains(product.Sku, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(product.Name) && source.Contains(product.Name, StringComparison.OrdinalIgnoreCase)));
        var projectName = Trim(subject ?? sourcePageTitle ?? savedProduct?.Name ?? ProjectNameFromSource(source), 100);
        var material = new[] { "PLA", "PETG", "ABS", "ASA", "TPU", "Nylon", "Resin" }
            .FirstOrDefault(x => Regex.IsMatch(source, $@"(?i)\b{Regex.Escape(x)}\b"))
            ?? savedProduct?.Material
            ?? instructions.Defaults.Material;
        var color = FirstMatch(source, @"(?i)\b(?:color|colour)\s*(?:is|:|=)?\s*(?<value>[a-z][a-z -]{2,30})")
            ?? savedProduct?.Color;
        var infill = Match(source, @"(?i)\b\d{1,3}\s*%\s*infill\b")?.Replace("infill", string.Empty, StringComparison.OrdinalIgnoreCase).Trim()
            ?? instructions.Defaults.Infill;
        var details = string.Join(", ", new[]
        {
            material,
            color,
            infill,
            ExtractDimensions(source),
            savedProduct?.Sku
        }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var lineItems = BuildLocalLineItems(source, images, instructions, projectName, details);
        var calcGrams = DecimalMatch(source,
            @"(?i)\b(?:material|filament|weight|grams?)\s*(?:used|usage|estimate|total|:|=)?\s*(?<value>\d+(?:\.\d+)?)\s*(?:g|grams?)\b",
            @"(?i)\b(?<value>\d+(?:\.\d+)?)\s*(?:g|grams?)\s+(?:of\s+)?(?:material|filament)\b",
            @"(?i)\b(?<value>\d+(?:\.\d+)?)\s*(?:g|grams?)\b");
        var calcHours = DecimalMatch(source,
            @"(?i)\b(?:print|machine)\s*(?:time|hours?)?\s*(?:is|:|=)?\s*(?<value>\d+(?:\.\d+)?)\s*(?:h|hr|hrs|hours?)\b",
            @"(?i)\b(?<value>\d+(?:\.\d+)?)\s*(?:h|hr|hrs|hours?)\s+(?:of\s+)?(?:print|machine)\s*time\b");
        var calcDesignHours = DecimalMatch(source,
            @"(?i)\b(?:design|cad|modeling|modelling)\s*(?:time|hours?)?\s*(?:is|:|=)?\s*(?<value>\d+(?:\.\d+)?)\s*(?:h|hr|hrs|hours?)\b",
            @"(?i)\b(?<value>\d+(?:\.\d+)?)\s*(?:h|hr|hrs|hours?)\s+(?:of\s+)?(?:design|cad|modeling|modelling)\b");
        var calcSetup = DecimalMatch(source, @"(?i)\bsetup\s*(?:fee|cost)?\s*(?:is|:|=)?\s*\$?\s*(?<value>\d+(?:\.\d+)?)");
        var calcPost = DecimalMatch(source, @"(?i)\bpost[- ]?processing\s*(?:fee|cost)?\s*(?:is|:|=)?\s*\$?\s*(?<value>\d+(?:\.\d+)?)");
        var calcGramRate = DecimalMatch(source, @"(?i)\$?\s*(?<value>\d+(?:\.\d+)?)\s*(?:/|per)\s*(?:g|gram)\b");
        var calcHourRate = DecimalMatch(source, @"(?i)(?:machine|print)\s*rate\s*(?:is|:|=)?\s*\$?\s*(?<value>\d+(?:\.\d+)?)");
        var calcDesignRate = DecimalMatch(source, @"(?i)(?:design|cad|modeling|modelling)\s*rate\s*(?:is|:|=)?\s*\$?\s*(?<value>\d+(?:\.\d+)?)");
        var calcMinimum = DecimalMatch(source, @"(?i)\bminimum(?:\s+(?:charge|order|price))?\s*(?:is|:|=)?\s*\$?\s*(?<value>\d+(?:\.\d+)?)");
        var calcRush = DecimalMatch(source, @"(?i)\brush\s*(?:fee|surcharge|percent|%)?\s*(?:is|:|=)?\s*(?<value>\d+(?:\.\d+)?)\s*%");
        var calcDiscount = DecimalMatch(source, @"(?i)\bdiscount\s*(?:is|:|=)?\s*\$?\s*(?<value>\d+(?:\.\d+)?)");
        var calcTax = DecimalMatch(source, @"(?i)\b(?:sales\s+)?tax\s*(?:rate|is|:|=)?\s*(?<value>\d+(?:\.\d+)?)\s*%");
        calcGrams = calcGrams > 0 ? calcGrams : savedProduct?.Grams ?? 0;
        calcHours = calcHours > 0 ? calcHours : savedProduct?.PrintHours ?? 0;
        calcDesignHours = calcDesignHours > 0 ? calcDesignHours : (savedProduct?.DesignMinutes ?? 0) / 60m;
        calcPost = calcPost > 0 ? calcPost : savedProduct?.PackagingCost ?? 0;

        var result = new AiEstimateDraftResult
        {
            Provider = "Local rules",
            UsedAi = false,
            SourceName = sourceName ?? "Pasted text",
            InstructionsPath = InstructionsPath,
            Prefill = new AiEstimatePrefill
            {
                CustomerName = customer,
                CustomerPhone = phone,
                CustomerEmail = email,
                PreparedFor = customer,
                ProjectName = projectName,
                Material = material,
                Color = color,
                Infill = infill,
                ProjectDescription = BuildProjectDescription(source, projectName),
                ProjectNotes = $"Drafted from {sourceName ?? "pasted customer text"}. Review every field and price before saving.",
                PageSize = instructions.Defaults.PageSize,
                DocDate = DateTime.Today.ToString("yyyy-MM-dd"),
                DueDate = DateTime.Today.AddDays(instructions.Defaults.EstimateValidDays).ToString("yyyy-MM-dd"),
                PaymentMethod = instructions.Defaults.PaymentMethod,
                PricingGuide = instructions.Defaults.PricingGuide,
                TermsNotes = instructions.Defaults.TermsNotes,
                StandardTurnaround = instructions.Defaults.StandardTurnaround,
                RushTurnaround = instructions.Defaults.RushTurnaround,
                DocTaxRate = instructions.Defaults.TaxRatePercent,
                CalcGrams = calcGrams,
                CalcHours = calcHours,
                CalcDesignHours = calcDesignHours,
                CalcSetupFee = calcSetup,
                CalcPostFee = calcPost,
                CalcGramRate = calcGramRate > 0 ? calcGramRate : savedProduct?.MaterialCostPerGram is > 0 ? savedProduct.MaterialCostPerGram.Value : instructions.Rates.GetValueOrDefault("materialPerGram", 0.05m),
                CalcHourRate = calcHourRate > 0 ? calcHourRate : savedProduct?.MachineRatePerHour is > 0 ? savedProduct.MachineRatePerHour.Value : instructions.Rates.GetValueOrDefault("machineHourly", 3m),
                CalcDesignRate = calcDesignRate > 0 ? calcDesignRate : instructions.Rates.GetValueOrDefault("designHourly", 25m),
                CalcMinimum = calcMinimum > 0 ? calcMinimum : Math.Max(instructions.Defaults.MinimumOrder, savedProduct?.TargetPrice ?? 0),
                CalcDifficulty = DifficultyForMaterial(material),
                CalcRush = calcRush,
                CalcDiscount = calcDiscount,
                CalcTaxRate = calcTax > 0 ? calcTax : instructions.Defaults.TaxRatePercent,
                LineItems = lineItems
            }
        };

        if (string.IsNullOrWhiteSpace(customer)) result.Questions.Add("What is the customer's name?");
        if (string.IsNullOrWhiteSpace(ExtractDimensions(source))) result.Questions.Add("What are the required dimensions or fit requirements?");
        var sourceHasMaterial = new[] { "PLA", "PETG", "ABS", "ASA", "TPU", "Nylon", "Resin" }
            .Any(x => Regex.IsMatch(source, $@"(?i)\b{Regex.Escape(x)}\b"));
        if (!sourceHasMaterial)
        {
            var materialBasis = savedProduct?.Material is { Length: > 0 }
                ? "saved product material"
                : "default material";
            result.Questions.Add($"Confirm material. The draft currently uses the {materialBasis}: {material}.");
        }
        if (lineItems.Any(x => x.Rate == instructions.Defaults.MinimumOrder))
            result.Warnings.Add($"One or more items use the ${instructions.Defaults.MinimumOrder:0.00} minimum and need pricing review.");
        if (images.Count > 0)
            result.Warnings.Add("Pictures were added as separate review items. Start a vision-capable Local AI model to identify pictured products and combine reference views automatically.");
        if (savedProduct is not null)
            result.Warnings.Add($"Saved product costing was applied from {savedProduct.Name}. Review stored grams, print time, rates, packaging, and target price before sending.");
        result.Warnings.Add("Local rules performed the extraction. Start Local AI and load a model for model-assisted interpretation.");
        NormalizeResult(result, sourceName, instructions, source);
        return result;
    }

    private static List<AiEstimateLineItem> BuildLocalLineItems(
        string source,
        List<AiEstimateImageInput> images,
        AiEstimateInstructions instructions,
        string? projectName,
        string sharedDetails)
    {
        var items = new List<AiEstimateLineItem>();
        var sourcePages = Regex.Matches(source, @"(?ims)^(?:ETSY LISTING|SOURCE PAGE):\s*(?<title>[^\r\n]+).*?^SOURCE PRICE:\s*(?<price>[^\r\n]+)")
            .Cast<Match>()
            .ToList();
        foreach (var page in sourcePages)
        {
            var url = FirstMatch(page.Value, @"(?im)^SOURCE URL:\s*(?<value>[^\r\n]+)");
            var description = FirstMatch(page.Value, @"(?im)^SOURCE DESCRIPTION:\s*(?<value>[^\r\n]+)");
            items.Add(CreateLocalLineItem(
                page.Value,
                instructions,
                string.Join(". ", new[] { sharedDetails, description, url }.Where(x => !string.IsNullOrWhiteSpace(x))),
                page.Groups["title"].Value));
        }

        var listedLines = source
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => Regex.Match(line, @"^(?:[-*•]\s+|\d+[.)]\s+)(?<value>.+)$"))
            .Where(match => match.Success)
            .Select(match => match.Groups["value"].Value.Trim())
            .Where(line => line.Length >= 3
                && !line.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                && !line.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                && !line.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                && !line.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
                && !line.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(25)
            .ToList();
        foreach (var line in listedLines)
        {
            items.Add(CreateLocalLineItem(line, instructions, sharedDetails));
        }

        foreach (var image in images)
        {
            var imageName = Regex.Replace(Path.GetFileNameWithoutExtension(image.FileName), @"[_-]+", " ").Trim();
            items.Add(new AiEstimateLineItem
            {
                Description = string.IsNullOrWhiteSpace(imageName) ? "Item from uploaded picture" : $"Item from picture: {Trim(imageName, 100)}",
                Details = string.Join(". ", new[] { sharedDetails, $"Reference picture: {image.FileName}. Confirm whether this is a separate item or another view of an existing item." }.Where(x => !string.IsNullOrWhiteSpace(x))),
                Quantity = 1,
                Rate = instructions.Defaults.MinimumOrder
            });
        }

        if (items.Count == 0)
        {
            items.Add(CreateLocalLineItem(projectName ?? FirstMeaningfulLine(source) ?? "Custom 3D print / design service", instructions, sharedDetails));
        }
        return items;
    }

    private static AiEstimateLineItem CreateLocalLineItem(string source, AiEstimateInstructions instructions, string details, string? descriptionOverride = null)
    {
        var quantityText = FirstMatch(source,
            @"(?i)^\s*(?<value>\d{1,4})\s*(?:x|×)\b",
            @"(?i)\b(?:qty|quantity|need|want|make|print)\s*(?:of|:|=)?\s*(?<value>\d{1,4})\b");
        var quantity = decimal.TryParse(quantityText, out var parsedQuantity) ? Math.Clamp(parsedQuantity, 1, 1000) : 1;
        var explicitMoney = Match(source, @"(?<!\w)\$\s*\d+(?:\.\d{1,2})?");
        var explicitRate = decimal.TryParse(Regex.Match(explicitMoney ?? string.Empty, @"\d+(?:\.\d{1,2})?").Value, out var parsedMoney)
            ? parsedMoney
            : 0;
        var keyword = instructions.KeywordPrices.FirstOrDefault(rule => rule.Keywords.Any(k => source.Contains(k, StringComparison.OrdinalIgnoreCase)));
        var cleaned = Regex.Replace(source, @"(?i)^\s*\d{1,4}\s*(?:x|×)?\s*", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s*[-–:]?\s*\$\s*\d+(?:\.\d{1,2})?(?:\s*(?:each|ea))?\s*$", string.Empty).Trim();
        var description = !string.IsNullOrWhiteSpace(descriptionOverride)
            ? Trim(descriptionOverride, 140)
            : string.IsNullOrWhiteSpace(cleaned)
            ? keyword?.Description ?? "Custom 3D print / design service"
            : Trim(cleaned, 140);
        return new AiEstimateLineItem
        {
            Description = description,
            Details = string.Join(". ", new[] { details, keyword?.Details }.Where(x => !string.IsNullOrWhiteSpace(x))),
            Quantity = quantity,
            Rate = explicitRate > 0 ? explicitRate : keyword?.Rate > 0 ? keyword.Rate : instructions.Defaults.MinimumOrder
        };
    }

    private void NormalizeResult(AiEstimateDraftResult result, string? sourceName, AiEstimateInstructions instructions, string? source = null)
    {
        source = string.IsNullOrWhiteSpace(source) ? null : CleanPastedSourceText(source);
        result.SourceName = sourceName ?? result.SourceName ?? "Pasted text";
        result.ExecutionReceipt ??= new AiExecutionReceipt();
        result.ExecutionReceipt.Engine = result.UsedAi ? "AI MODEL" : "LOCAL RULES";
        result.ExecutionReceipt.UsedAi = result.UsedAi;
        result.ExecutionReceipt.Provider = result.Provider;
        if (!result.UsedAi)
        {
            result.ExecutionReceipt.Model = null;
            result.ExecutionReceipt.ResponseId = null;
            result.ExecutionReceipt.PromptTokens = null;
            result.ExecutionReceipt.CompletionTokens = null;
            result.ExecutionReceipt.TotalTokens = null;
        }
        result.Prefill ??= new AiEstimatePrefill();
        result.Questions ??= [];
        result.Warnings ??= [];
        var prefill = result.Prefill;
        prefill.DocType = "ESTIMATE";
        prefill.Status = "Draft";
        prefill.CustomerPhone = FormatUsPhone(prefill.CustomerPhone);
        prefill.CustomerEmail = NormalizeCustomerEmail(prefill.CustomerEmail);
        prefill.LineItems ??= [];
        RepairCustomerIdentity(prefill, source);
        RepairProjectText(prefill, source, result.Warnings);
        prefill.PreparedFor = string.IsNullOrWhiteSpace(prefill.PreparedFor) ? prefill.CustomerName : prefill.PreparedFor;
        prefill.Material = string.IsNullOrWhiteSpace(prefill.Material) ? instructions.Defaults.Material : prefill.Material;
        prefill.Infill = string.IsNullOrWhiteSpace(prefill.Infill) ? instructions.Defaults.Infill : prefill.Infill;
        prefill.PageSize = string.IsNullOrWhiteSpace(prefill.PageSize) ? instructions.Defaults.PageSize : prefill.PageSize;
        prefill.DocDate = string.IsNullOrWhiteSpace(prefill.DocDate) ? DateTime.Today.ToString("yyyy-MM-dd") : prefill.DocDate;
        prefill.DueDate = string.IsNullOrWhiteSpace(prefill.DueDate)
            ? DateTime.Today.AddDays(instructions.Defaults.EstimateValidDays).ToString("yyyy-MM-dd")
            : prefill.DueDate;
        prefill.PaymentMethod = string.IsNullOrWhiteSpace(prefill.PaymentMethod) ? instructions.Defaults.PaymentMethod : prefill.PaymentMethod;
        prefill.PricingGuide = string.IsNullOrWhiteSpace(prefill.PricingGuide) ? instructions.Defaults.PricingGuide : prefill.PricingGuide;
        prefill.TermsNotes = string.IsNullOrWhiteSpace(prefill.TermsNotes) ? instructions.Defaults.TermsNotes : prefill.TermsNotes;
        prefill.StandardTurnaround = string.IsNullOrWhiteSpace(prefill.StandardTurnaround) ? instructions.Defaults.StandardTurnaround : prefill.StandardTurnaround;
        prefill.RushTurnaround = string.IsNullOrWhiteSpace(prefill.RushTurnaround) ? instructions.Defaults.RushTurnaround : prefill.RushTurnaround;
        prefill.DocTaxRate = Math.Clamp(prefill.DocTaxRate, 0, 30);
        prefill.DocRushPercent = Math.Clamp(prefill.DocRushPercent, 0, 200);
        prefill.DocDiscount = Math.Max(0, prefill.DocDiscount);
        prefill.CalcGrams = Math.Max(0, prefill.CalcGrams);
        prefill.CalcHours = Math.Max(0, prefill.CalcHours);
        prefill.CalcDesignHours = Math.Max(0, prefill.CalcDesignHours);
        prefill.CalcSetupFee = Math.Max(0, prefill.CalcSetupFee);
        prefill.CalcPostFee = Math.Max(0, prefill.CalcPostFee);
        prefill.CalcGramRate = prefill.CalcGramRate > 0 ? prefill.CalcGramRate : instructions.Rates.GetValueOrDefault("materialPerGram", 0.05m);
        prefill.CalcHourRate = prefill.CalcHourRate > 0 ? prefill.CalcHourRate : instructions.Rates.GetValueOrDefault("machineHourly", 3m);
        prefill.CalcDesignRate = prefill.CalcDesignRate > 0 ? prefill.CalcDesignRate : instructions.Rates.GetValueOrDefault("designHourly", 25m);
        prefill.CalcMinimum = prefill.CalcMinimum > 0 ? prefill.CalcMinimum : instructions.Defaults.MinimumOrder;
        prefill.CalcDifficulty = prefill.CalcDifficulty > 0 ? Math.Clamp(prefill.CalcDifficulty, 1, 5) : DifficultyForMaterial(prefill.Material);
        prefill.CalcRush = Math.Clamp(Math.Max(prefill.CalcRush, prefill.DocRushPercent), 0, 200);
        prefill.CalcDiscount = Math.Max(prefill.CalcDiscount, prefill.DocDiscount);
        prefill.CalcTaxRate = Math.Clamp(Math.Max(prefill.CalcTaxRate, prefill.DocTaxRate), 0, 30);
        prefill.DocRushPercent = prefill.CalcRush;
        prefill.DocDiscount = prefill.CalcDiscount;
        prefill.DocTaxRate = prefill.CalcTaxRate;
        prefill.AssistanceSource = result.UsedAi ? "AI MODEL" : "LOCAL RULES";
        prefill.AssistanceProvider = result.Provider;
        foreach (var line in prefill.LineItems)
        {
            line.Quantity = Math.Max(0, line.Quantity);
            line.Rate = Math.Max(0, line.Rate);
        }
        var preservesGranularQuote = result.UsedAi || result.UsedSourcePlanning;
        if (preservesGranularQuote && NormalizeGranularPhaseRates(prefill, instructions))
        {
            result.Warnings.Add("The app applied editable prototype, multicolor setup, and bulk-handling rates exactly once and removed any hidden difficulty multiplier.");
        }
        if (preservesGranularQuote && ReconcileCalculatorWithGranularLineItems(prefill))
        {
            result.Warnings.Add("The app reconciled calculator grams, hours, setup, design, and post-processing values to the granular AI line items before calculating the quote.");
        }

        var hasCalculatorInputs = prefill.CalcGrams > 0
            || prefill.CalcHours > 0
            || prefill.CalcDesignHours > 0
            || prefill.CalcSetupFee > 0
            || prefill.CalcPostFee > 0;
        if (hasCalculatorInputs)
        {
            var calculatorPricing = BuildCalculatorPricing(prefill);
            var granularQuoteTotal = prefill.LineItems.Sum(x => Math.Max(0, x.Quantity) * Math.Max(0, x.Rate));
            var hasGranularAiQuote = preservesGranularQuote
                && prefill.LineItems.Count >= 2
                && prefill.LineItems.Count(x => x.Rate > 0) >= 2
                && granularQuoteTotal > 0;
            if (hasGranularAiQuote)
            {
                result.Pricing = BuildLineItemPricing(prefill, calculatorPricing);
                result.Warnings.Add("Granular AI line items drive the draft quote total. Calculator grams, hours, and rates are retained as the underlying cost basis for review.");
                if (result.Pricing.LineSubtotal < calculatorPricing.LineSubtotal)
                {
                    result.Warnings.Add($"The granular quote subtotal {result.Pricing.LineSubtotal:C} is below its calculator cost basis {calculatorPricing.LineSubtotal:C}. Increase or correct the line items before sending.");
                }
            }
            else
            {
                result.Pricing = calculatorPricing;
                prefill.LineItems = BuildCalculatorLineItems(prefill, result.Pricing);
            }
        }
        else
        {
            result.Pricing = BuildLineItemPricing(prefill);
            result.Warnings.Add("No supported grams, print time, design time, setup, or post-processing amount was supplied. The draft uses item/keyword pricing and requires a pricing review.");
        }

        if (prefill.LineItems.Count == 0)
        {
            prefill.LineItems.Add(new AiEstimateLineItem
            {
                Description = prefill.ProjectName ?? "Custom 3D print / design service",
                Quantity = 1,
                Rate = instructions.Defaults.MinimumOrder
            });
            result.Warnings.Add("No priced line items were returned, so the editable minimum-order amount was inserted for review.");
            result.Pricing = BuildLineItemPricing(prefill);
        }

        var assistanceLabel = result.UsedAi ? "AI ASSISTANCE" : "LOCAL RULES ASSISTANCE";
        if (!(prefill.ProjectNotes ?? string.Empty).Contains("ASSISTANCE:", StringComparison.OrdinalIgnoreCase))
        {
            var provenance = $"{assistanceLabel}: Draft prepared by {result.Provider} from {result.SourceName}. Assistance populated fields and line items; review customer details, quantities, prices, taxes, and terms before saving.";
            var pricingBasis = result.Pricing.UsedCalculatorInputs
                ? $"PRICING BASIS: {result.Pricing.PricingMode}. {prefill.CalcGrams:0.##}g at {prefill.CalcGramRate:C}/g; {prefill.CalcHours:0.##} machine hours at {prefill.CalcHourRate:C}/hr; {prefill.CalcDesignHours:0.##} design hours at {prefill.CalcDesignRate:C}/hr; setup {prefill.CalcSetupFee:C}; post-processing {prefill.CalcPostFee:C}; difficulty {prefill.CalcDifficulty:0.##}x; minimum {prefill.CalcMinimum:C}. App-calculated quote total: {result.Pricing.Total:C}."
                : $"PRICING BASIS: Item or keyword rates were used because calculator cost inputs were not available. App-calculated quote total: {result.Pricing.Total:C}.";
            var executionReceipt = result.UsedAi
                ? $"AI EXECUTION RECEIPT: {result.ExecutionReceipt.ExecutedAtUtc:u}; provider {result.ExecutionReceipt.Provider}; model {result.ExecutionReceipt.Model ?? "not supplied"}; response ID {result.ExecutionReceipt.ResponseId ?? "not supplied"}; tokens {result.ExecutionReceipt.TotalTokens?.ToString() ?? "not supplied"}."
                : $"LOCAL RULES RECEIPT: {result.ExecutionReceipt.ExecutedAtUtc:u}; fixed local extraction rules prepared this draft. No language model ran and token usage does not apply.";
            prefill.ProjectNotes = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { prefill.ProjectNotes, provenance, executionReceipt, pricingBasis }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
    }

    private static bool NormalizeGranularPhaseRates(AiEstimatePrefill prefill, AiEstimateInstructions instructions)
    {
        if (prefill.LineItems.Count < 2) return false;
        var changed = false;
        var prototype = FindLine(prefill.LineItems, "prototype", "sample print");
        var setup = FindLine(prefill.LineItems, "multicolor setup", "production setup", "color change", "colour change");
        var bulkHandling = FindLine(prefill.LineItems, "bulk handling");
        var postProcessing = FindLine(prefill.LineItems, "post-processing", "post processing", "cleanup", "finishing");

        var prototypeRate = instructions.Rates.GetValueOrDefault("prototypeSampleFee");
        if (prototype is not null && prototypeRate > 0)
        {
            changed |= SetIfDifferent(prototype.Quantity, 1, value => prototype.Quantity = value);
            changed |= SetIfDifferent(prototype.Rate, prototypeRate, value => prototype.Rate = value);
            prototype.Details = AppendDetailOnce(prototype.Details, "Uses editable prototypeSampleFee.");
        }

        var multicolorSetupRate = instructions.Rates.GetValueOrDefault("multicolorSetupFee");
        if (setup is not null && multicolorSetupRate > 0)
        {
            changed |= SetIfDifferent(setup.Quantity, 1, value => setup.Quantity = value);
            changed |= SetIfDifferent(setup.Rate, multicolorSetupRate, value => setup.Rate = value);
            setup.Details = "Multicolor production setup, file slicing, plate layout, and color-change preparation. Uses editable multicolorSetupFee.";
        }

        var bulkHandlingRate = instructions.Rates.GetValueOrDefault("bulkHandlingPerUnit");
        if (bulkHandling is not null && bulkHandlingRate > 0)
        {
            changed |= SetIfDifferent(bulkHandling.Rate, bulkHandlingRate, value => bulkHandling.Rate = value);
            bulkHandling.Details = AppendDetailOnce(bulkHandling.Details, "Uses editable bulkHandlingPerUnit.");
        }

        var postProcessingRate = instructions.Rates.GetValueOrDefault("postProcessingHourly");
        if (postProcessing is not null && postProcessingRate > 0)
        {
            changed |= SetIfDifferent(postProcessing.Quantity, 1, value => postProcessing.Quantity = value);
            changed |= SetIfDifferent(postProcessing.Rate, postProcessingRate, value => postProcessing.Rate = value);
            postProcessing.Details = AppendDetailOnce(postProcessing.Details, "Uses editable postProcessingHourly.");
        }

        if (!prefill.LineItems.Any(line => line.Description.Contains("difficulty", StringComparison.OrdinalIgnoreCase)))
        {
            changed |= SetIfDifferent(prefill.CalcDifficulty, 1, value => prefill.CalcDifficulty = value);
        }
        return changed;
    }

    private static string AppendDetailOnce(string? details, string addition)
    {
        if ((details ?? string.Empty).Contains(addition, StringComparison.OrdinalIgnoreCase)) return Trim(details, 300);
        return Trim(string.Join(" ", new[] { details, addition }.Where(value => !string.IsNullOrWhiteSpace(value))), 300);
    }

    private static bool ReconcileCalculatorWithGranularLineItems(AiEstimatePrefill prefill)
    {
        if (prefill.LineItems.Count < 2) return false;
        var changed = false;
        var material = FindLine(prefill.LineItems, "material");
        if (material is { Quantity: > 0, Rate: > 0 })
        {
            changed |= SetIfDifferent(prefill.CalcGrams, material.Quantity, value => prefill.CalcGrams = value);
            changed |= SetIfDifferent(prefill.CalcGramRate, material.Rate, value => prefill.CalcGramRate = value);
        }

        var machine = FindLine(prefill.LineItems, "machine time", "print time");
        if (machine is { Quantity: > 0, Rate: > 0 })
        {
            changed |= SetIfDifferent(prefill.CalcHours, machine.Quantity, value => prefill.CalcHours = value);
            changed |= SetIfDifferent(prefill.CalcHourRate, machine.Rate, value => prefill.CalcHourRate = value);
        }

        var design = FindLine(prefill.LineItems, "design", "artwork", "modeling", "modelling");
        if (design is { Quantity: > 0, Rate: > 0 } && prefill.CalcDesignRate > 0)
        {
            var designAmount = design.Quantity * design.Rate;
            changed |= SetIfDifferent(prefill.CalcDesignHours, designAmount / prefill.CalcDesignRate, value => prefill.CalcDesignHours = value);
        }

        var setup = FindLine(prefill.LineItems, "setup");
        if (setup is { Quantity: > 0, Rate: > 0 })
        {
            changed |= SetIfDifferent(prefill.CalcSetupFee, setup.Quantity * setup.Rate, value => prefill.CalcSetupFee = value);
        }

        var post = FindLine(prefill.LineItems, "post-processing", "post processing", "cleanup", "finishing");
        if (post is { Quantity: > 0, Rate: > 0 })
        {
            changed |= SetIfDifferent(prefill.CalcPostFee, post.Quantity * post.Rate, value => prefill.CalcPostFee = value);
        }
        return changed;
    }

    private static void ApplySourcePlanningAssumptions(AiEstimateDraftResult result, string source, AiEstimateInstructions instructions)
    {
        if (!Regex.IsMatch(source, @"(?i)\bguitar\s+pic(?:k)?s?\b")) return;
        var quantity = ExtractBulkQuantity(source);
        if (quantity < 25) return;

        result.UsedSourcePlanning = true;
        result.Prefill ??= new AiEstimatePrefill();
        result.Prefill.LineItems ??= [];
        var prefill = result.Prefill;
        prefill.LineItems.RemoveAll(line => line.Rate == instructions.Defaults.MinimumOrder
            && (line.Description.Contains("SOURCE FILE", StringComparison.OrdinalIgnoreCase)
                || line.Description.Equals("Gmail", StringComparison.OrdinalIgnoreCase)
                || line.Description.Contains("Custom 3D print / design service", StringComparison.OrdinalIgnoreCase)));
        if (string.IsNullOrWhiteSpace(prefill.ProjectName)
            || prefill.ProjectName.Contains("SOURCE FILE", StringComparison.OrdinalIgnoreCase)
            || prefill.ProjectName.Equals("Gmail", StringComparison.OrdinalIgnoreCase))
        {
            prefill.ProjectName = $"Custom Guitar Pick Design & Production ({quantity:0} pcs)";
        }
        if (string.IsNullOrWhiteSpace(prefill.ProjectDescription)
            || prefill.ProjectDescription.Contains("SOURCE FILE:", StringComparison.OrdinalIgnoreCase))
        {
            prefill.ProjectDescription = $"Planning estimate for design, prototype approval, and production of {quantity:0} custom promotional guitar picks.";
        }
        var gramsPerUnit = instructions.Rates.GetValueOrDefault("flatPromoPieceGramsPerUnit", 1.5m);
        var wastePercent = instructions.Rates.GetValueOrDefault("multicolorWastePercent", 25m);
        var hoursPer100 = instructions.Rates.GetValueOrDefault("flatPromoPieceMachineHoursPer100", 40m);
        var totalGrams = Money(quantity * gramsPerUnit * (1 + wastePercent / 100m));
        var totalHours = Money(quantity / 100m * hoursPer100);
        var gramRate = instructions.Rates.GetValueOrDefault("materialPerGram", 0.05m);
        var hourRate = instructions.Rates.GetValueOrDefault("machineHourly", 3m);

        prefill.CalcGrams = totalGrams;
        prefill.CalcHours = totalHours;
        prefill.CalcGramRate = gramRate;
        prefill.CalcHourRate = hourRate;
        UpsertPlanningLine(prefill.LineItems, ["material"], "Production material",
            $"{quantity:0} flat promotional picks at {gramsPerUnit:0.##}g each plus {wastePercent:0.##}% multicolor waste allowance.", totalGrams, gramRate);
        UpsertPlanningLine(prefill.LineItems, ["machine time", "print time"], "Production machine time",
            $"{hoursPer100:0.##} editable machine hours per 100 pieces for {quantity:0} pieces.", totalHours, hourRate);

        var designFee = ExtractExplicitDesignFee(source);
        if (designFee > 0)
        {
            UpsertPlanningLine(prefill.LineItems, ["design", "artwork", "logo", "proof"], "Logo/artwork design & proof",
                "Fixed design/proof amount explicitly discussed in the source packet.", 1, designFee);
            var designRate = instructions.Rates.GetValueOrDefault("designHourly", 25m);
            if (designRate > 0)
            {
                prefill.CalcDesignRate = designRate;
                prefill.CalcDesignHours = Money(designFee / designRate);
            }
        }

        var prototypeRate = instructions.Rates.GetValueOrDefault("prototypeSampleFee");
        if (prototypeRate > 0 && source.Contains("sample", StringComparison.OrdinalIgnoreCase))
        {
            UpsertPlanningLine(prefill.LineItems, ["prototype", "sample print"], "Prototype sample print",
                "One sample for design, color, and printability approval before the bulk run.", 1, prototypeRate);
        }

        var setupRate = instructions.Rates.GetValueOrDefault("multicolorSetupFee");
        if (setupRate > 0 && Regex.IsMatch(source, @"(?i)\b(?:4|four|5|five|multi)[ -]?colou?rs?\b"))
        {
            prefill.CalcSetupFee = setupRate;
            UpsertPlanningLine(prefill.LineItems, ["production setup", "multicolor setup", "color change", "colour change"], "Multicolor production setup",
                "File slicing, plate layout, calibration, and multicolor setup using editable multicolorSetupFee.", 1, setupRate);
        }

        var bulkRate = instructions.Rates.GetValueOrDefault("bulkHandlingPerUnit");
        if (bulkRate > 0)
        {
            UpsertPlanningLine(prefill.LineItems, ["bulk handling"], "Bulk handling / production management",
                "Per-unit production management and handling using editable bulkHandlingPerUnit.", quantity, bulkRate);
        }
        result.Warnings.Add($"The app applied editable flat-promotional-part planning assumptions for {quantity:0} guitar picks: {totalGrams:0.##}g and {totalHours:0.##} machine hours. Update AiEstimateInstructions.json as real slicer data becomes available.");
    }

    private static decimal ExtractBulkQuantity(string source)
    {
        return Regex.Matches(source, @"(?i)\b(?<value>\d{2,5})\b(?:\s+[\w/-]+){0,4}\s*(?:[- ]?\s*(?:pieces?|pcs?|units?|copies|picks?|pics?))\b")
            .Select(match => decimal.TryParse(match.Groups["value"].Value, out var value) ? value : 0)
            .Where(value => value is >= 25 and <= 10_000)
            .DefaultIfEmpty(0)
            .Max();
    }

    private static decimal ExtractExplicitDesignFee(string source)
    {
        var value = FirstMatch(source,
            @"(?i)\$\s*(?<value>\d+(?:\.\d{1,2})?)\s*(?:for\s+the\s+)?(?:logo|design|artwork|proof)",
            @"(?i)(?:logo|design|artwork|proof)[^$\r\n]{0,50}\$\s*(?<value>\d+(?:\.\d{1,2})?)");
        return decimal.TryParse(value, out var amount) ? amount : 0;
    }

    private static void UpsertPlanningLine(List<AiEstimateLineItem> lines, string[] matchTerms, string description, string details, decimal quantity, decimal rate)
    {
        var line = FindLine(lines, matchTerms);
        if (line is null)
        {
            line = new AiEstimateLineItem();
            lines.Add(line);
        }
        line.Description = description;
        line.Details = details;
        line.Quantity = quantity;
        line.Rate = rate;
    }

    private static AiEstimateLineItem? FindLine(IEnumerable<AiEstimateLineItem> lines, params string[] terms) => lines
        .FirstOrDefault(line => terms.Any(term => line.Description.Contains(term, StringComparison.OrdinalIgnoreCase)));

    private static bool SetIfDifferent(decimal current, decimal value, Action<decimal> setter)
    {
        value = Math.Max(0, value);
        if (Math.Abs(current - value) < 0.001m) return false;
        setter(value);
        return true;
    }

    private static void AddSourceBudgetWarning(AiEstimateDraftResult result, string source)
    {
        var budget = DecimalMatch(source,
            @"(?i)\b(?:budget|hoping\s+to\s+spend|want(?:ed)?\s+to\s+spend|spend|up\s+to|max(?:imum)?)\D{0,30}\$?\s*(?<value>\d{2,6}(?:\.\d{1,2})?)");
        if (budget <= 0) return;
        var difference = result.Pricing.Total - budget;
        result.Warnings.Add(difference > 0
            ? $"The draft total {result.Pricing.Total:C} is {difference:C} above the customer's stated {budget:C} budget. Review scope, production assumptions, or options before sending."
            : $"The draft total {result.Pricing.Total:C} is within the customer's stated {budget:C} budget by {Math.Abs(difference):C}. Review assumptions before sending.");
    }

    private static AiEstimatePricingSummary BuildCalculatorPricing(AiEstimatePrefill prefill)
    {
        var setup = prefill.CalcSetupFee;
        var material = prefill.CalcGrams * prefill.CalcGramRate;
        var machine = prefill.CalcHours * prefill.CalcHourRate;
        var design = prefill.CalcDesignHours * prefill.CalcDesignRate;
        var post = prefill.CalcPostFee;
        var baseSubtotal = setup + material + machine + design + post;
        var difficultyFee = baseSubtotal * Math.Max(0, prefill.CalcDifficulty - 1);
        var beforeMinimum = baseSubtotal + difficultyFee;
        var rushFactor = 1 + (prefill.CalcRush / 100m);
        var minimumAdjustment = Math.Max(0, ((prefill.CalcMinimum + prefill.CalcDiscount) / rushFactor) - beforeMinimum);
        var lineSubtotal = beforeMinimum + minimumAdjustment;
        var rushAmount = lineSubtotal * (prefill.CalcRush / 100m);
        var taxable = Math.Max(prefill.CalcMinimum, lineSubtotal + rushAmount - prefill.CalcDiscount);
        var tax = taxable * (prefill.CalcTaxRate / 100m);
        return new AiEstimatePricingSummary
        {
            PricingMode = "Calculator-generated quote",
            UsedCalculatorInputs = true,
            RequiresPricingReview = true,
            Setup = Money(setup),
            Material = Money(material),
            Machine = Money(machine),
            Design = Money(design),
            PostProcessing = Money(post),
            DifficultyFee = Money(difficultyFee),
            MinimumAdjustment = Money(minimumAdjustment),
            LineSubtotal = Money(lineSubtotal),
            RushAmount = Money(rushAmount),
            Discount = Money(prefill.CalcDiscount),
            TaxableSubtotal = Money(taxable),
            TaxAmount = Money(tax),
            Total = Money(taxable + tax)
        };
    }

    private static List<AiEstimateLineItem> BuildCalculatorLineItems(AiEstimatePrefill prefill, AiEstimatePricingSummary pricing)
    {
        var details = string.Join(" · ", new[] { prefill.ProjectName, prefill.Material, prefill.Color, prefill.Infill }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var items = new List<AiEstimateLineItem>();
        AddCalculatorItem(items, prefill.CalcSetupFee > 0, prefill.ProjectName ?? "Print setup / file preparation", details, 1, prefill.CalcSetupFee);
        AddCalculatorItem(items, prefill.CalcGrams > 0, "Material usage", $"{prefill.CalcGrams:0.##} grams × {prefill.CalcGramRate:C}/g · {prefill.Material}", prefill.CalcGrams, prefill.CalcGramRate);
        AddCalculatorItem(items, prefill.CalcHours > 0, "Machine print time", $"{prefill.CalcHours:0.##} print hours × {prefill.CalcHourRate:C}/hr", prefill.CalcHours, prefill.CalcHourRate);
        AddCalculatorItem(items, prefill.CalcDesignHours > 0, "Design / modeling time", $"{prefill.CalcDesignHours:0.##} design hours × {prefill.CalcDesignRate:C}/hr", prefill.CalcDesignHours, prefill.CalcDesignRate);
        AddCalculatorItem(items, prefill.CalcPostFee > 0, "Post-processing / handling", "Cleanup, support removal, packaging, or special handling.", 1, prefill.CalcPostFee);
        AddCalculatorItem(items, pricing.DifficultyFee > 0, "Material / difficulty surcharge", $"Difficulty multiplier: {prefill.CalcDifficulty:0.##}×", 1, pricing.DifficultyFee);
        AddCalculatorItem(items, pricing.MinimumAdjustment > 0, "Minimum charge adjustment", $"Minimum quote floor: {prefill.CalcMinimum:C}.", 1, pricing.MinimumAdjustment);
        return items;
    }

    private static void AddCalculatorItem(List<AiEstimateLineItem> items, bool include, string description, string details, decimal quantity, decimal rate)
    {
        if (!include) return;
        items.Add(new AiEstimateLineItem
        {
            Description = Trim(description, 140),
            Details = details,
            Quantity = Math.Max(0, quantity),
            Rate = Money(Math.Max(0, rate))
        });
    }

    private static AiEstimatePricingSummary BuildLineItemPricing(AiEstimatePrefill prefill, AiEstimatePricingSummary? calculatorCostBasis = null)
    {
        var lineSubtotal = prefill.LineItems.Sum(x => Math.Max(0, x.Quantity) * Math.Max(0, x.Rate));
        var rushAmount = lineSubtotal * (prefill.DocRushPercent / 100m);
        var taxable = Math.Max(prefill.CalcMinimum, lineSubtotal + rushAmount - prefill.DocDiscount);
        var tax = taxable * (prefill.DocTaxRate / 100m);
        return new AiEstimatePricingSummary
        {
            PricingMode = calculatorCostBasis is null ? "Granular quote line items" : "Granular quote line items with calculator cost basis",
            UsedCalculatorInputs = calculatorCostBasis is not null,
            RequiresPricingReview = true,
            Setup = calculatorCostBasis?.Setup ?? 0,
            Material = calculatorCostBasis?.Material ?? 0,
            Machine = calculatorCostBasis?.Machine ?? 0,
            Design = calculatorCostBasis?.Design ?? 0,
            PostProcessing = calculatorCostBasis?.PostProcessing ?? 0,
            DifficultyFee = calculatorCostBasis?.DifficultyFee ?? 0,
            MinimumAdjustment = calculatorCostBasis?.MinimumAdjustment ?? 0,
            LineSubtotal = Money(lineSubtotal),
            RushAmount = Money(rushAmount),
            Discount = Money(prefill.DocDiscount),
            TaxableSubtotal = Money(taxable),
            TaxAmount = Money(tax),
            Total = Money(taxable + tax)
        };
    }

    private static decimal DifficultyForMaterial(string? material) => (material ?? string.Empty).ToUpperInvariant() switch
    {
        "ABS" or "ASA" => 1.2m,
        "NYLON" or "TPU" or "FLEX" => 1.35m,
        "RESIN" => 1.5m,
        _ => 1m
    };

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static string? FormatUsPhone(string? value)
    {
        var digits = Regex.Replace(value ?? string.Empty, @"\D", "");
        if (digits.Length == 11 && digits.StartsWith('1')) digits = digits[1..];
        return digits.Length == 10 ? $"({digits[..3]}) {digits.Substring(3, 3)}-{digits[6..]}" : value;
    }

    private static string? FirstMatch(string input, params string[] patterns)
    {
        foreach (var pattern in patterns)
        {
            var match = Regex.Match(input, pattern);
            if (match.Success) return match.Groups["value"].Value.Trim();
        }
        return null;
    }

    private static string? Match(string input, string pattern)
    {
        var match = Regex.Match(input, pattern);
        return match.Success ? match.Value.Trim() : null;
    }

    private static decimal DecimalMatch(string input, params string[] patterns)
    {
        var value = FirstMatch(input, patterns);
        return decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? Math.Max(0, parsed)
            : 0;
    }

    private static string CleanPastedSourceText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var cleaned = value.Replace('\u00a0', ' ').Trim();
        cleaned = Regex.Replace(cleaned, @"(?i)\bSkip to content\b", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\bUsing Gmail with screen readers\b", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\bEnable desktop notifications for Gmail\.?\b", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\bOK\s+No thanks\b", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\b\d+\s+of\s+\d+\b", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\b(?:Inbox|Attachments?)\b", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\b[\w.+-]*(?:epata|ernest|ernie)[\w.+-]*@[\w.-]+\.[a-z]{2,}\b", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\bAttached is the estimate for your custom 3D print request\.?", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\bPlease review the price, project details, material, and notes\.?", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\bIf everything looks good, reply with approval and I(?:'|’)ll move forward\.?", " ");
        cleaned = Regex.Replace(cleaned, @"(?i)\bIf anything needs to be adjusted, send me the chang(?:e|es)?\.?", " ");
        cleaned = Regex.Replace(cleaned, @"[^\S\r\n]+", " ");
        cleaned = Regex.Replace(cleaned, @"(?:\r?\n\s*){3,}", Environment.NewLine + Environment.NewLine);
        return cleaned.Trim();
    }

    private static bool ContainsGmailChrome(string? value) => !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(value, @"(?i)(Skip to content|Using Gmail with screen readers|Enable desktop notifications for Gmail|OK\s+No thanks|\b\d+\s+of\s+\d+\b)");

    private static bool ShouldUseLocalDraftWithoutModel(PreparedAiSources prepared)
    {
        var cameFromGmailPaste = prepared.Warnings.Any(warning => warning.Contains("Gmail interface text", StringComparison.OrdinalIgnoreCase));
        if (!cameFromGmailPaste || string.IsNullOrWhiteSpace(ExtractProjectTitle(prepared.Text))) return false;
        if (prepared.Text.Contains("EPATA LLC", StringComparison.OrdinalIgnoreCase)) return true;

        return !Regex.IsMatch(
            prepared.Text,
            @"(?i)\b(?:PLA|PETG|ABS|ASA|TPU|nylon|resin|material|dimensions?|measurements?|mm|cm|inches?|grams?|hours?|qty|quantity|deadline|budget|color|colour|STL|STEP|CAD|\$\s*\d)",
            RegexOptions.CultureInvariant);
    }

    private static string? FirstCustomerEmail(string source) => Regex.Matches(source, @"(?im)\b[\w.+-]+@[\w.-]+\.[a-z]{2,}\b")
        .Select(match => match.Value.Trim())
        .FirstOrDefault(email => !IsBusinessEmail(email));

    private static string? NormalizeCustomerEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        var match = Regex.Match(email, @"(?im)\b[\w.+-]+@[\w.-]+\.[a-z]{2,}\b");
        if (!match.Success) return null;
        var normalized = match.Value.Trim();
        return IsBusinessEmail(normalized) ? null : normalized;
    }

    private static bool IsBusinessEmail(string email) => email.Contains("epata", StringComparison.OrdinalIgnoreCase)
        || email.Contains("ernest", StringComparison.OrdinalIgnoreCase)
        || email.Contains("ernie", StringComparison.OrdinalIgnoreCase);

    private static string? ExtractCustomerName(string source)
    {
        var value = FirstMatch(source,
            @"(?im)^\s*(?:customer|name)\s*:\s*(?<value>[^\r\n<]{2,80})",
            @"(?i)\bto\s+(?<value>[A-Za-z][A-Za-z.'-]{1,30})\b",
            @"(?i)\b(?:hi|hey|hello)\s+(?<value>[A-Za-z][A-Za-z.'-]{1,30})\b",
            @"(?im)^\s*(?<value>[A-Z][a-z]+(?:\s+[A-Z][a-z]+){1,3})\s*$");
        if (string.IsNullOrWhiteSpace(value) || IsBusinessName(value) || IsGmailJunkText(value)) return null;
        return ToDisplayName(value);
    }

    private static string? ExtractProjectTitle(string source) => FirstMatch(source,
        @"(?is)\bEstimate\s+for\s+(?<value>.*?)(?=\s+(?:Inbox|EPATA LLC|Ernest\s+Phillips|Attachments?|Mon|Tue|Wed|Thu|Fri|Sat|Sun)\b|$)",
        @"(?is)\bQuote\s+for\s+(?<value>.*?)(?=\s+(?:Inbox|EPATA LLC|Ernest\s+Phillips|Attachments?|Mon|Tue|Wed|Thu|Fri|Sat|Sun)\b|$)",
        @"(?im)^\s*(?:subject|project)\s*:\s*(?<value>[^\r\n]{3,140})");

    private static string? NormalizeProjectTitle(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var cleaned = CleanTextFragment(value);
        cleaned = Regex.Replace(cleaned, @"(?i)^(?:re|fw|fwd)\s*:\s*", string.Empty);
        cleaned = Regex.Replace(cleaned, @"(?i)^(?:estimate|invoice|quote)\s+for\s+", string.Empty);
        cleaned = Regex.Replace(cleaned, @"(?i)\s+(?:Inbox|Attachments?|EPATA LLC|Ernest\s+Phillips|Mon|Tue|Wed|Thu|Fri|Sat|Sun)\b.*$", string.Empty);
        cleaned = CleanTextFragment(cleaned);
        if (string.IsNullOrWhiteSpace(cleaned) || IsGmailJunkText(cleaned)) return null;
        return cleaned.Length <= 100 ? ToTitleIfNeeded(cleaned) : Trim(ToTitleIfNeeded(cleaned), 100);
    }

    private static string BuildProjectDescription(string source, string? projectName)
    {
        var title = NormalizeProjectTitle(ExtractProjectTitle(source));
        if (!string.IsNullOrWhiteSpace(title))
        {
            return Trim($"Custom 3D print estimate for {title.ToLowerInvariant()}.", 500);
        }

        var sourceSummary = BuildSourceDescription(source);
        if (!string.IsNullOrWhiteSpace(sourceSummary)) return Trim(sourceSummary, 500);

        var fallbackTitle = NormalizeProjectTitle(projectName);
        return !string.IsNullOrWhiteSpace(fallbackTitle)
            ? Trim($"Custom 3D print estimate for {fallbackTitle.ToLowerInvariant()}.", 500)
            : "Custom 3D print / design service.";
    }

    private static void RepairCustomerIdentity(AiEstimatePrefill prefill, string? source)
    {
        if (IsBusinessName(prefill.CustomerName)) prefill.CustomerName = null;
        if (IsBusinessName(prefill.PreparedFor)) prefill.PreparedFor = null;
        if (string.IsNullOrWhiteSpace(source)) return;

        var customer = ExtractCustomerName(source);
        if (!string.IsNullOrWhiteSpace(customer))
        {
            if (string.IsNullOrWhiteSpace(prefill.CustomerName)) prefill.CustomerName = customer;
            if (string.IsNullOrWhiteSpace(prefill.PreparedFor)) prefill.PreparedFor = customer;
        }

        prefill.CustomerEmail ??= FirstCustomerEmail(source);
    }

    private static void RepairProjectText(AiEstimatePrefill prefill, string? source, List<string> warnings)
    {
        var repaired = false;
        var derivedTitle = string.IsNullOrWhiteSpace(source) ? null : NormalizeProjectTitle(ExtractProjectTitle(source));
        if (IsGmailJunkText(prefill.ProjectName) && !string.IsNullOrWhiteSpace(derivedTitle))
        {
            prefill.ProjectName = derivedTitle;
            repaired = true;
        }

        if (string.IsNullOrWhiteSpace(prefill.ProjectName) && !string.IsNullOrWhiteSpace(derivedTitle))
        {
            prefill.ProjectName = derivedTitle;
        }

        if (IsGmailJunkText(prefill.ProjectDescription) || string.IsNullOrWhiteSpace(prefill.ProjectDescription))
        {
            prefill.ProjectDescription = BuildProjectDescription(source ?? string.Empty, prefill.ProjectName);
            repaired = true;
        }

        foreach (var line in prefill.LineItems ?? [])
        {
            if (IsGmailJunkText(line.Description))
            {
                line.Description = prefill.ProjectName ?? "Custom 3D print / design service";
                repaired = true;
            }
            if (IsGmailJunkText(line.Details))
            {
                line.Details = string.Empty;
                repaired = true;
            }
        }

        if (repaired)
        {
            warnings.Add("Removed Gmail interface/sent-message boilerplate from customer-facing draft fields.");
        }
    }

    private static bool IsBusinessName(string? value) => !string.IsNullOrWhiteSpace(value)
        && (value.Contains("EPATA", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Ernest Phillips", StringComparison.OrdinalIgnoreCase)
            || value.Contains("Ernie Phillips", StringComparison.OrdinalIgnoreCase));

    private static bool IsGmailJunkText(string? value) => !string.IsNullOrWhiteSpace(value)
        && Regex.IsMatch(value, @"(?i)(Skip to content|Using Gmail|screen readers|desktop notifications|OK\s+No thanks|\b\d+\s+of\s+\d+\b|\bGmail\b|\bInbox\b|[\w.+-]*(?:epata|ernest|ernie)[\w.+-]*@[\w.-]+\.[a-z]{2,}|Attached is the estimate|Please review the price|If everything looks good|If anything needs to be adjusted)");

    private static string CleanTextFragment(string value)
    {
        var cleaned = CleanPastedSourceText(value);
        cleaned = Regex.Replace(cleaned, @"(?i)\b(?:Wed|Mon|Tue|Thu|Fri|Sat|Sun),?\s+[A-Z][a-z]{2,8}\s+\d{1,2},?\s+\d{1,2}:\d{2}\s*(?:AM|PM)\b.*$", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(" \t\r\n-–:.".ToCharArray());
        return cleaned;
    }

    private static string ToDisplayName(string value)
    {
        var cleaned = Regex.Replace(value, @"[^\p{L}\p{M} .'-]+", string.Empty).Trim();
        return ToTitleIfNeeded(cleaned);
    }

    private static string ToTitleIfNeeded(string value) => value.Any(char.IsLower) && value.Any(char.IsUpper)
        ? value.Trim()
        : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant()).Trim();

    private static string? ProjectNameFromSource(string source)
    {
        var line = SourceDescriptionGroups(source).SelectMany(group => group).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line)) return null;
        return NormalizeProjectTitle(line);
    }

    private static string? FirstMeaningfulLine(string source) => MeaningfulSourceLines(source).FirstOrDefault();

    private static IEnumerable<string> MeaningfulSourceLines(string source) =>
        Regex.Split(CleanPastedSourceText(source), @"(?<=[.!?])\s+|\r?\n+")
            .Select(CleanTextFragment)
            .Where(line => line.Length >= 3
                && !Regex.IsMatch(line, @"(?i)^(?:SOURCE FILE|UPLOADED REFERENCE PICTURES)\b")
                && !Regex.IsMatch(line, @"(?i)^(?:from|to|sent|subject|date)\s*:")
                && !IsGmailJunkText(line))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    private static string? BuildSourceDescription(string source)
    {
        var groups = SourceDescriptionGroups(source);
        if (groups.Count == 0) return null;

        var selected = groups.Select(group => group[0]).ToList();
        var maximumLines = Math.Max(groups.Count, 6);
        for (var offset = 1; selected.Count < maximumLines; offset++)
        {
            var added = false;
            foreach (var group in groups)
            {
                if (offset >= group.Count) continue;
                selected.Add(group[offset]);
                added = true;
                if (selected.Count >= maximumLines) break;
            }
            if (!added) break;
        }

        var perLineLimit = Math.Max(36, 470 / selected.Count);
        return Trim(string.Join(" ", selected.Select(line => WithSentenceEnding(Trim(line, perLineLimit)))), 500);
    }

    private static List<List<string>> SourceDescriptionGroups(string source) =>
        Regex.Split(
                CleanPastedSourceText(source),
                @"(?im)^\s*SOURCE FILE\s*:[^\r\n]*(?:\r?\n|$)")
            .Select(block => MeaningfulSourceLines(block)
                .Select(NormalizeWorkDescriptionLine)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => line!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList())
            .Where(group => group.Count > 0)
            .ToList();

    private static string? NormalizeWorkDescriptionLine(string line)
    {
        if (Regex.IsMatch(
                line,
                @"(?i)^(?:customer(?:\s+name)?|name|prepared\s+for|bill\s+to|ship\s+to|email|e-mail|phone|telephone|address|from|to|sent|date)\s*:"))
        {
            return null;
        }
        if (Regex.IsMatch(line, @"(?i)^https?://\S+$|\.(?:pdf|docx?|txt|eml|csv|json)$")) return null;

        var cleaned = Regex.Replace(
            line,
            @"(?i)^(?:project\s+description|description|work\s+requested|request|item|product)\s*:\s*",
            string.Empty);
        cleaned = Regex.Replace(cleaned, @"(?i)^\s*\d{1,4}\s*(?:x|×)\s+", string.Empty);
        cleaned = Regex.Replace(cleaned, @"\s*[-–:]?\s*\$\s*\d+(?:\.\d{1,2})?(?:\s*(?:each|ea))?\s*$", string.Empty);
        cleaned = CleanTextFragment(cleaned);
        return cleaned.Length >= 3 && !IsGmailJunkText(cleaned) ? cleaned : null;
    }

    private static string WithSentenceEnding(string value) => value.EndsWith('.') || value.EndsWith('!') || value.EndsWith('?')
        ? value
        : value + ".";

    private static string? ExtractDimensions(string source) => Match(source,
        @"(?i)\b\d+(?:\.\d+)?\s*(?:mm|cm|in|inch|inches|"")\s*[x×]\s*\d+(?:\.\d+)?\s*(?:mm|cm|in|inch|inches|"")(?:\s*[x×]\s*\d+(?:\.\d+)?\s*(?:mm|cm|in|inch|inches|""))?");

    private static string MarkdownSection(string text, string heading)
    {
        var start = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;
        var next = text.IndexOf("\n##", start + heading.Length, StringComparison.OrdinalIgnoreCase);
        var section = next > start ? text[start..next] : text[start..];
        return Trim(section.Trim(), 1_800);
    }

    private static string NormalizeChatRole(string? role)
    {
        var clean = (role ?? string.Empty).Trim().ToLowerInvariant();
        return clean is "assistant" or "ai" or "model" ? "Assistant" : "User";
    }

    private static string StripJsonFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline ? trimmed[(firstNewline + 1)..lastFence].Trim() : trimmed;
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Length <= max ? value : value[..max].TrimEnd() + "...";
    }
}

public sealed class AiEstimateInstructions
{
    public int Version { get; set; }
    public string BusinessName { get; set; } = "EPATA LLC";
    public string Currency { get; set; } = "USD";
    public AiEstimateDefaults Defaults { get; set; } = new();
    public Dictionary<string, decimal> Rates { get; set; } = [];
    public List<AiKeywordPrice> KeywordPrices { get; set; } = [];
    public List<string> PricingRules { get; set; } = [];
    public List<string> AiInstructions { get; set; } = [];
}

public sealed class AiEstimateDefaults
{
    public decimal MinimumOrder { get; set; } = 15;
    public decimal TaxRatePercent { get; set; }
    public string Material { get; set; } = "PLA";
    public string Infill { get; set; } = "20%";
    public int EstimateValidDays { get; set; } = 14;
    public string PageSize { get; set; } = "LETTER";
    public string PaymentMethod { get; set; } = "Unknown / Review";
    public string PricingGuide { get; set; } = "Use the calculator cost basis and review every quoted amount before sending.";
    public string TermsNotes { get; set; } = "This estimate is valid for 14 days. Final price may vary if requirements change. Payment is due before printing begins.";
    public string StandardTurnaround { get; set; } = "Estimated timeline provided after design review and schedule confirmation";
    public string RushTurnaround { get; set; } = "Expedited service available upon request, subject to current workload";
}

public sealed class AiKeywordPrice
{
    public List<string> Keywords { get; set; } = [];
    public string Description { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public decimal Rate { get; set; }
}

sealed record PreparedAiSources(string Text, List<AiEstimateImageInput> Images, List<string> Warnings);
sealed record PreparedModelSource(string Text, bool WasCondensed);
sealed record ResolvedAiConnection(string ChatEndpoint, string Model, string Provider, bool UseApiKey);
sealed record AiProductPricingContext(
    string Name,
    string? Sku,
    string Category,
    string? Material,
    string? Color,
    decimal? Grams,
    decimal? MaterialCostPerGram,
    decimal? PrintHours,
    decimal? MachineRatePerHour,
    decimal? PackagingCost,
    decimal? DesignMinutes,
    decimal? TargetPrice,
    string? Notes);
