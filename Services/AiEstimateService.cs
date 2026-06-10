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

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private string InstructionsPath => Path.Combine(environment.ContentRootPath, "AiEstimateInstructions.json");
    private string BuilderPath => Path.Combine(environment.ContentRootPath, "wwwroot", "invoice-builder", "index.html");

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
        var hostedConfigured = configuration.GetValue<bool>("Ai:AllowHostedFallback")
            && !string.IsNullOrWhiteSpace(endpoint)
            && !string.IsNullOrWhiteSpace(model);
        return new
        {
            configured = localConnection is not null || hostedConfigured,
            provider = localConnection?.Provider ?? (hostedConfigured ? configuration["Ai:Provider"] ?? "OpenAI-compatible" : "Local rules fallback"),
            model = localConnection?.Model ?? model ?? string.Empty,
            localAi = localStatus,
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
                maxExtractedCharactersPerFile = AiSourceDocumentTextExtractor.MaxExtractedCharactersPerFile
            },
            visionRequiresConfiguredAi = true,
            safety = "Review-first: analysis opens an unsaved estimate draft and never saves or sends automatically. Local AI is preferred; hosted fallback is disabled unless explicitly enabled. URL fetching allows public HTTPS pages only."
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
        var connection = await ResolveProviderConnectionAsync(cancellationToken);
        if (connection is not null)
        {
            try
            {
                var aiResult = await CallConfiguredProviderAsync(prepared.Text, request.SourceName, prepared.Images, instructions, builderFields, products, connection.ChatEndpoint, connection.Model, connection.UseApiKey, cancellationToken);
                aiResult.Provider = connection.Provider;
                aiResult.UsedAi = true;
                aiResult.ExecutionReceipt.Provider = connection.Provider;
                aiResult.InstructionsPath = InstructionsPath;
                NormalizeResult(aiResult, request.SourceName, instructions);
                aiResult.Warnings.InsertRange(0, prepared.Warnings);
                return aiResult;
            }
            catch (Exception ex)
            {
                var fallback = BuildLocalDraft(prepared.Text, request.SourceName, prepared.Images, instructions, products);
                fallback.Warnings.InsertRange(0, prepared.Warnings);
                fallback.Warnings.Insert(0, $"Configured AI call failed, so local rules were used: {ex.Message}");
                return fallback;
            }
        }

        var local = BuildLocalDraft(prepared.Text, request.SourceName, prepared.Images, instructions, products);
        local.Warnings.InsertRange(0, prepared.Warnings);
        return local;
    }

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
        if (!string.IsNullOrWhiteSpace(request.SourceText)) parts.Add(request.SourceText.Trim());

        var pastedUrls = Regex.Matches(request.SourceText ?? string.Empty, @"https://[^\s<>""']+", RegexOptions.IgnoreCase)
            .Select(match => match.Value.TrimEnd('.', ',', ')', ']', '}'))
            .ToList();
        var allUrls = (request.SourceUrls ?? [])
            .Concat(pastedUrls)
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
            catch (Exception ex)
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
        string endpoint,
        string model,
        bool useApiKey,
        CancellationToken cancellationToken)
    {
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
            Create a separate line item for each distinct requested/listed product or service.
            Use supplied source-page metadata, uploaded documents, and uploaded pictures as source material. Pictures can identify likely products and features, but uncertain details must become questions or warnings.
            Use only the rules and prices in the editable instructions below.
            When the requested item matches the saved product catalog, treat its stored material, grams, print hours, rates, packaging cost, design minutes, and target price as the preferred pricing basis. Use target price as the minimum floor, not as an extra fee.
            Fill every applicable field in the HTML estimate builder contract below.
            Pay special attention to material, total grams for the quoted quantity, material cost per gram, print hours, machine rate, design time, setup, post-processing, difficulty, minimum, rush, discount, and tax.
            The application, not you, performs the final money calculation. Supply honest calculator inputs. Use 0 and add a question when a cost input cannot be supported.
            Do not make line-item rates disagree with the calculator inputs. When calculator inputs are present, the application will replace line items with a deterministic calculator breakdown.
            {schema}

            HTML ESTIMATE BUILDER FIELD IDS PRESENT:
            {JsonSerializer.Serialize(builderFields, JsonOptions)}

            SAVED PRODUCT / COST CATALOG:
            {JsonSerializer.Serialize(products, JsonOptions)}

            EDITABLE INSTRUCTIONS:
            {JsonSerializer.Serialize(instructions, JsonOptions)}
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
            max_tokens = 4096,
            response_format = new { type = useApiKey ? "json_object" : "text" },
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
        var keyVariable = configuration["Ai:ApiKeyEnvironmentVariable"] ?? "EPATA_AI_API_KEY";
        var apiKey = useApiKey ? Environment.GetEnvironmentVariable(keyVariable) : null;
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
        var content = envelope.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString();
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("AI provider returned no structured estimate content.");
        }

        var result = JsonSerializer.Deserialize<AiEstimateDraftResult>(NormalizeProviderJson(StripJsonFence(content)), JsonOptions)
            ?? throw new InvalidOperationException("AI provider response could not be parsed as an estimate draft.");
        result.ExecutionReceipt = BuildModelExecutionReceipt(envelope.RootElement, model);
        return result;
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
        var clean = Regex.Replace(source, @"\s+", " ").Trim();
        var email = Match(source, @"(?im)\b[\w.+-]+@[\w.-]+\.[a-z]{2,}\b");
        var phoneDigits = Regex.Replace(Match(source, @"(?<!\d)(?:\+?1[\s.-]?)?\(?\d{3}\)?[\s.-]?\d{3}[\s.-]?\d{4}(?!\d)") ?? string.Empty, @"\D", "");
        if (phoneDigits.Length == 11 && phoneDigits.StartsWith('1')) phoneDigits = phoneDigits[1..];
        var phone = phoneDigits.Length == 10 ? $"({phoneDigits[..3]}) {phoneDigits.Substring(3, 3)}-{phoneDigits[6..]}" : null;
        var customer = FirstMatch(source,
            @"(?im)^\s*(?:customer|name|from)\s*:\s*(?<value>[^\r\n<]{2,80})",
            @"(?im)^\s*(?<value>[A-Z][a-z]+(?:\s+[A-Z][a-z]+){1,3})\s*$");
        var subject = FirstMatch(source, @"(?im)^\s*subject\s*:\s*(?<value>[^\r\n]{3,120})");
        var sourcePageTitle = FirstMatch(source, @"(?im)^\s*(?:ETSY LISTING|SOURCE PAGE):\s*(?<value>[^\r\n]{3,180})");
        var savedProduct = products.FirstOrDefault(product =>
            (!string.IsNullOrWhiteSpace(product.Sku) && source.Contains(product.Sku, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(product.Name) && source.Contains(product.Name, StringComparison.OrdinalIgnoreCase)));
        var projectName = Trim(subject ?? sourcePageTitle ?? savedProduct?.Name ?? FirstMeaningfulLine(source), 100);
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
                ProjectDescription = Trim(clean, 500),
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
        if (!new[] { "PLA", "PETG", "ABS", "ASA", "TPU", "Nylon", "Resin" }.Any(x => Regex.IsMatch(source, $@"(?i)\b{Regex.Escape(x)}\b")))
            result.Questions.Add($"Confirm material. The draft currently uses the default: {instructions.Defaults.Material}.");
        if (lineItems.Any(x => x.Rate == instructions.Defaults.MinimumOrder))
            result.Warnings.Add($"One or more items use the ${instructions.Defaults.MinimumOrder:0.00} minimum and need pricing review.");
        if (images.Count > 0)
            result.Warnings.Add("Pictures were added as separate review items. Start a vision-capable Local AI model to identify pictured products and combine reference views automatically.");
        if (savedProduct is not null)
            result.Warnings.Add($"Saved product costing was applied from {savedProduct.Name}. Review stored grams, print time, rates, packaging, and target price before sending.");
        result.Warnings.Add("Local rules performed the extraction. Start Local AI and load a model for model-assisted interpretation.");
        NormalizeResult(result, sourceName, instructions);
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

    private void NormalizeResult(AiEstimateDraftResult result, string? sourceName, AiEstimateInstructions instructions)
    {
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
        var prefill = result.Prefill;
        prefill.DocType = "ESTIMATE";
        prefill.Status = "Draft";
        prefill.CustomerPhone = FormatUsPhone(prefill.CustomerPhone);
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
        prefill.LineItems ??= [];
        result.Questions ??= [];
        result.Warnings ??= [];
        foreach (var line in prefill.LineItems)
        {
            line.Quantity = Math.Max(0, line.Quantity);
            line.Rate = Math.Max(0, line.Rate);
        }

        var hasCalculatorInputs = prefill.CalcGrams > 0
            || prefill.CalcHours > 0
            || prefill.CalcDesignHours > 0
            || prefill.CalcSetupFee > 0
            || prefill.CalcPostFee > 0;
        if (hasCalculatorInputs)
        {
            result.Pricing = BuildCalculatorPricing(prefill);
            prefill.LineItems = BuildCalculatorLineItems(prefill, result.Pricing);
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
                ? $"PRICING BASIS: {prefill.CalcGrams:0.##}g at {prefill.CalcGramRate:C}/g; {prefill.CalcHours:0.##} machine hours at {prefill.CalcHourRate:C}/hr; {prefill.CalcDesignHours:0.##} design hours at {prefill.CalcDesignRate:C}/hr; setup {prefill.CalcSetupFee:C}; post-processing {prefill.CalcPostFee:C}; difficulty {prefill.CalcDifficulty:0.##}x; minimum {prefill.CalcMinimum:C}. App-calculated quote total: {result.Pricing.Total:C}."
                : $"PRICING BASIS: Item or keyword rates were used because calculator cost inputs were not available. App-calculated quote total: {result.Pricing.Total:C}.";
            var executionReceipt = result.UsedAi
                ? $"AI EXECUTION RECEIPT: {result.ExecutionReceipt.ExecutedAtUtc:u}; provider {result.ExecutionReceipt.Provider}; model {result.ExecutionReceipt.Model ?? "not supplied"}; response ID {result.ExecutionReceipt.ResponseId ?? "not supplied"}; tokens {result.ExecutionReceipt.TotalTokens?.ToString() ?? "not supplied"}."
                : $"LOCAL RULES RECEIPT: {result.ExecutionReceipt.ExecutedAtUtc:u}; fixed local extraction rules prepared this draft. No language model ran and token usage does not apply.";
            prefill.ProjectNotes = string.Join(Environment.NewLine + Environment.NewLine,
                new[] { prefill.ProjectNotes, provenance, executionReceipt, pricingBasis }.Where(x => !string.IsNullOrWhiteSpace(x)));
        }
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

    private static AiEstimatePricingSummary BuildLineItemPricing(AiEstimatePrefill prefill)
    {
        var lineSubtotal = prefill.LineItems.Sum(x => Math.Max(0, x.Quantity) * Math.Max(0, x.Rate));
        var rushAmount = lineSubtotal * (prefill.DocRushPercent / 100m);
        var taxable = Math.Max(0, lineSubtotal + rushAmount - prefill.DocDiscount);
        var tax = taxable * (prefill.DocTaxRate / 100m);
        return new AiEstimatePricingSummary
        {
            UsedCalculatorInputs = false,
            RequiresPricingReview = true,
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

    private static string? FirstMeaningfulLine(string source) => source
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault(line => !Regex.IsMatch(line, @"(?i)^(from|to|sent|subject|date)\s*:"));

    private static string? ExtractDimensions(string source) => Match(source,
        @"(?i)\b\d+(?:\.\d+)?\s*(?:mm|cm|in|inch|inches|"")\s*[x×]\s*\d+(?:\.\d+)?\s*(?:mm|cm|in|inch|inches|"")(?:\s*[x×]\s*\d+(?:\.\d+)?\s*(?:mm|cm|in|inch|inches|""))?");

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
