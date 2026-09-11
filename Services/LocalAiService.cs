using System.Diagnostics;
using System.Text;
using System.Text.Json;
using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Services;

public sealed class LocalAiService(AppDbContext db, HttpClient httpClient)
{
    private static readonly SemaphoreSlim LifecycleGate = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private const string DefaultBaseUrl = "http://127.0.0.1:1234";
    private const string DefaultIdentifier = "epata-local";
    private const int DefaultContextLength = 8192;
    private const int DefaultIdleUnloadSeconds = 1800;
    private const int DefaultStructuredOutputTokens = 4096;
    private const int DefaultTextOutputTokens = 1024;

    public async Task<LocalAiSettings> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var values = await GetSettingValuesAsync(cancellationToken);
        return BuildSettings(values);
    }

    public async Task<LocalAiSettings> SaveSettingsAsync(SaveLocalAiSettingsRequest request, CancellationToken cancellationToken = default)
    {
        await LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var settings = new LocalAiSettings(
                NormalizeBaseUrl(request.BaseUrl),
                NormalizeModelPath(request.ModelPath),
                NormalizeIdentifier(request.ModelIdentifier),
                Math.Clamp(request.ContextLength <= 0 ? DefaultContextLength : request.ContextLength, 2048, 131072),
                Math.Clamp(request.IdleUnloadSeconds <= 0 ? DefaultIdleUnloadSeconds : request.IdleUnloadSeconds, 60, 86400));

            await UpsertAsync("LocalAi:BaseUrl", settings.BaseUrl, cancellationToken);
            await UpsertAsync("LocalAi:ModelPath", settings.ModelPath ?? string.Empty, cancellationToken);
            await UpsertAsync("LocalAi:ModelIdentifier", settings.ModelIdentifier, cancellationToken);
            await UpsertAsync("LocalAi:ContextLength", settings.ContextLength.ToString(), cancellationToken);
            await UpsertAsync("LocalAi:IdleUnloadSeconds", settings.IdleUnloadSeconds.ToString(), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return settings;
        }
        finally
        {
            LifecycleGate.Release();
        }
    }

    public async Task<LocalAiStatus> GetStatusAsync(bool includeAvailableModels = true, CancellationToken cancellationToken = default)
    {
        var values = await GetSettingValuesAsync(cancellationToken);
        var settings = BuildStatusSettings(values, out var configurationWarning, out var selectedModelPath);
        var status = new LocalAiStatus
        {
            BaseUrl = settings.BaseUrl,
            SelectedModelPath = selectedModelPath ?? settings.ModelPath,
            ModelIdentifier = settings.ModelIdentifier,
            ContextLength = settings.ContextLength,
            IdleUnloadSeconds = settings.IdleUnloadSeconds,
            LmsPath = FindLmsPath(),
            LmStudioPath = FindLmStudioPath()
        };
        status.LmsInstalled = status.LmsPath is not null && status.LmStudioPath is not null;
        if (includeAvailableModels) status.AvailableModels = FindAvailableModels();

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            using var response = await httpClient.GetAsync($"{settings.BaseUrl}/api/v1/models", timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                status.Message = $"LM Studio server returned {(int)response.StatusCode}.";
                return status;
            }

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            status.LoadedModels = document.RootElement.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Array
                ? models.EnumerateArray()
                    .Where(model => model.TryGetProperty("type", out var type) && type.GetString() == "llm")
                    .SelectMany(model => model.TryGetProperty("loaded_instances", out var instances) && instances.ValueKind == JsonValueKind.Array
                        ? instances.EnumerateArray().Select(instance => instance.TryGetProperty("id", out var id) ? id.GetString() : null)
                        : [])
                    .Where(id => !string.IsNullOrWhiteSpace(id))
                    .Cast<string>()
                    .ToList()
                : [];
            status.ServerOnline = true;
            status.ModelReady = !string.IsNullOrWhiteSpace(settings.ModelPath)
                && status.LoadedModels.Contains(settings.ModelIdentifier, StringComparer.OrdinalIgnoreCase);
            status.State = status.ModelReady ? "Model ready" : "Server on / selected model not loaded";
            status.Message = status.ModelReady
                ? $"Local AI is ready using {settings.ModelIdentifier}."
                : "LM Studio server is on. Select and load the app's model before using model-assisted features.";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException
            or JsonException
            or InvalidOperationException
            or IOException
            or TaskCanceledException)
        {
            status.State = "Off";
            status.Message = status.LmsInstalled
                ? "LM Studio is installed, but its local server is off."
                : "LM Studio or its lms command-line tool was not found.";
        }

        if (!string.IsNullOrWhiteSpace(configurationWarning))
        {
            status.ModelReady = false;
            status.State = "Setup needs attention";
            status.Message = configurationWarning;
        }

        return status;
    }

    public async Task<LocalAiActionResult> StartAsync(CancellationToken cancellationToken = default)
    {
        await LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var settings = await GetSettingsAsync(cancellationToken);
            var lmsPath = FindLmsPath();
            var studioPath = FindLmStudioPath();
            if (lmsPath is null || studioPath is null)
            {
                var missing = await GetStatusAsync(cancellationToken: cancellationToken);
                return new(false, "LM Studio and its lms command-line tool must be installed before the app can start local AI.", missing);
            }

            var initialStatus = await GetStatusAsync(false, cancellationToken);
            if (initialStatus.ModelReady)
            {
                return new(true, "Local AI server and selected model are already ready.", initialStatus);
            }

            if (!initialStatus.ServerOnline)
            {
                StartLmStudio(studioPath);
                await Task.Delay(TimeSpan.FromSeconds(4), cancellationToken);
                var port = new Uri(settings.BaseUrl).Port;
                var server = await RunProcessAsync(lmsPath, ["server", "start", "--port", port.ToString(), "--bind", "127.0.0.1"], TimeSpan.FromSeconds(75), cancellationToken);
                if (!server.Success)
                {
                    var failed = await GetStatusAsync(cancellationToken: cancellationToken);
                    return new(false, $"LM Studio opened, but the local server did not start. {server.Message}", failed);
                }
            }

            if (!string.IsNullOrWhiteSpace(settings.ModelPath))
            {
                var current = await GetStatusAsync(false, cancellationToken);
                if (!current.LoadedModels.Contains(settings.ModelIdentifier, StringComparer.OrdinalIgnoreCase))
                {
                    var modelKey = await ResolveModelKeyAsync(lmsPath, settings.ModelPath, cancellationToken);
                    if (string.IsNullOrWhiteSpace(modelKey))
                    {
                        var unresolved = await GetStatusAsync(cancellationToken: cancellationToken);
                        return new(false, "The selected GGUF file is present, but LM Studio did not report a matching language-model key.", unresolved);
                    }
                    var load = await RunProcessAsync(lmsPath,
                        ["load", modelKey, "--identifier", settings.ModelIdentifier, "--context-length", settings.ContextLength.ToString(), "--ttl", settings.IdleUnloadSeconds.ToString(), "--yes"],
                        TimeSpan.FromMinutes(5),
                        cancellationToken);
                    if (!load.Success)
                    {
                        var loadFailed = await GetStatusAsync(cancellationToken: cancellationToken);
                        return new(false, $"The server started, but the selected model did not load. {load.Message}", loadFailed);
                    }
                }
            }

            var status = await WaitForReadyStatusAsync(!string.IsNullOrWhiteSpace(settings.ModelPath), cancellationToken);
            var message = status.ModelReady
                ? "Local AI server and selected model are ready."
                : "Local AI server is on. Choose a downloaded model and click Start again to load it.";
            return new(status.ServerOnline, message, status);
        }
        finally
        {
            LifecycleGate.Release();
        }
    }

    public async Task<LocalAiActionResult> StopAsync(CancellationToken cancellationToken = default)
    {
        await LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var current = await GetStatusAsync(false, cancellationToken);
            if (!current.ServerOnline)
            {
                return new(true, "Local AI is already off.", await GetStatusAsync(cancellationToken: cancellationToken));
            }

            var lmsPath = FindLmsPath();
            if (lmsPath is null)
            {
                var missing = await GetStatusAsync(cancellationToken: cancellationToken);
                return new(false, "The lms command-line tool was not found.", missing);
            }

            await RunProcessAsync(lmsPath, ["unload", "--all"], TimeSpan.FromSeconds(30), cancellationToken);
            var stopped = await RunProcessAsync(lmsPath, ["server", "stop"], TimeSpan.FromSeconds(30), cancellationToken);
            await Task.Delay(700, cancellationToken);
            return new(stopped.Success, stopped.Success ? "Local AI server and loaded models were stopped." : stopped.Message,
                await GetStatusAsync(cancellationToken: cancellationToken));
        }
        finally
        {
            LifecycleGate.Release();
        }
    }

    public async Task<LocalAiConnection?> GetReadyConnectionAsync(CancellationToken cancellationToken = default)
    {
        var settings = await GetSettingsAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.ModelPath)) return null;
        var status = await GetStatusAsync(false, cancellationToken);
        if (!status.ModelReady) return null;
        return new LocalAiConnection($"{status.BaseUrl}/v1/chat/completions", settings.ModelIdentifier, $"LM Studio local / {settings.ModelIdentifier}");
    }

    public async Task<T> CompleteJsonAsync<T>(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var content = await SendChatAsync(systemPrompt, userPrompt, typeof(T), DefaultStructuredOutputTokens, cancellationToken);
        return DeserializeJson<T>(content);
    }

    public async Task<T> CompleteJsonWithImagesAsync<T>(
        string systemPrompt,
        string userPrompt,
        IReadOnlyCollection<AiEstimateImageInput> images,
        CancellationToken cancellationToken = default)
    {
        var content = new List<object> { new { type = "text", text = userPrompt } };
        content.AddRange(images.Take(10).Select(image => (object)new
        {
            type = "image_url",
            image_url = new { url = $"data:{image.ContentType};base64,{image.Base64Data}" }
        }));
        var response = await SendChatAsync(systemPrompt, content, typeof(T), DefaultStructuredOutputTokens, cancellationToken);
        return DeserializeJson<T>(response);
    }

    private static T DeserializeJson<T>(string content)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(JsonCandidate(content), JsonOptions)
                ?? throw new InvalidOperationException("Local AI returned empty structured JSON.");
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw new InvalidOperationException("Local AI did not return valid structured JSON. Try a stronger instruct model or use the local-rules result.", ex);
        }
    }

    public async Task<string> CompleteTextAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default,
        int maxTokens = DefaultTextOutputTokens)
        => await SendChatAsync(systemPrompt, userPrompt, null, maxTokens, cancellationToken);

    public async Task<string> CompleteTextWithImagesAsync(
        string systemPrompt,
        string userPrompt,
        IReadOnlyCollection<AiEstimateImageInput> images,
        CancellationToken cancellationToken = default,
        int maxTokens = DefaultTextOutputTokens)
    {
        var content = new List<object> { new { type = "text", text = userPrompt } };
        content.AddRange(images.Take(10).Select(image => (object)new
        {
            type = "image_url",
            image_url = new { url = $"data:{image.ContentType};base64,{image.Base64Data}" }
        }));
        return await SendChatAsync(systemPrompt, content, null, maxTokens, cancellationToken);
    }

    private async Task<string> SendChatAsync(string systemPrompt, object userContent, Type? responseType, int maxTokens, CancellationToken cancellationToken)
    {
        var connection = await GetReadyConnectionAsync(cancellationToken)
            ?? throw new InvalidOperationException("Local AI is off or no model is loaded. Open Local AI, select a model, and click Start.");
        var payload = new
        {
            model = connection.Model,
            temperature = 0.1,
            max_tokens = Math.Clamp(maxTokens, 64, DefaultStructuredOutputTokens),
            reasoning_effort = "none",
            response_format = BuildResponseFormat(responseType),
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userContent }
            }
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, connection.ChatEndpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
        };
        using var response = await httpClient.SendAsync(message, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Local AI returned {(int)response.StatusCode}: {Trim(responseText, 500)}");
        }

        using var envelope = JsonDocument.Parse(responseText);
        var responseMessage = envelope.RootElement.GetProperty("choices")[0].GetProperty("message");
        var content = responseMessage.TryGetProperty("content", out var contentElement) && contentElement.ValueKind == JsonValueKind.String
            ? contentElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(content)
            && responseMessage.TryGetProperty("reasoning_content", out var reasoningElement)
            && reasoningElement.ValueKind == JsonValueKind.String)
        {
            var reasoning = reasoningElement.GetString();
            content = responseType is null
                ? BuildVisibleFallbackFromReasoning(reasoning)
                : reasoning;
        }
        return string.IsNullOrWhiteSpace(content)
            ? throw new InvalidOperationException("Local AI returned no text.")
            : content.Trim();
    }

    private static string BuildVisibleFallbackFromReasoning(string? reasoning)
    {
        if (string.IsNullOrWhiteSpace(reasoning))
        {
            return string.Empty;
        }

        var normalizedReasoning = reasoning
            .Replace("\\r", "\r", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal);

        var usefulLines = normalizedReasoning
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("- ", StringComparison.Ordinal)
                || System.Text.RegularExpressions.Regex.IsMatch(line, @"^\d+[\.)]\s+"))
            .Where(line => !line.Contains("chain", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("think", StringComparison.OrdinalIgnoreCase)
                && !line.Contains("the user", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line, @"^\d+[\.)]\s+", "- "))
            .ToList();

        return usefulLines.Count > 0
            ? string.Join(Environment.NewLine, usefulLines)
            : "Local AI kept thinking and did not produce a final answer. Try again with a shorter question or a smaller context packet.";
    }

    private static object BuildResponseFormat(Type? responseType)
    {
        if (responseType is null) return new { type = "text" };
        return new
        {
            type = "json_schema",
            json_schema = new
            {
                name = "epata_structured_response",
                strict = false,
                schema = BuildJsonSchema(responseType, [])
            }
        };
    }

    private static object BuildJsonSchema(Type type, HashSet<Type> activeTypes)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type == typeof(char) || type == typeof(Guid)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(DateOnly) || type == typeof(TimeOnly))
        {
            return new { type = "string" };
        }
        if (type == typeof(bool)) return new { type = "boolean" };
        if (type.IsEnum) return new { type = "string", @enum = Enum.GetNames(type) };
        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long)
            || type == typeof(sbyte) || type == typeof(ushort) || type == typeof(uint) || type == typeof(ulong))
        {
            return new { type = "integer" };
        }
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
        {
            return new { type = "number" };
        }

        var enumerableType = type.IsArray
            ? typeof(IEnumerable<>).MakeGenericType(type.GetElementType()!)
            : type.GetInterfaces().Append(type)
                .FirstOrDefault(candidate => candidate.IsGenericType && candidate.GetGenericTypeDefinition() == typeof(IEnumerable<>));
        if (enumerableType is not null)
        {
            return new
            {
                type = "array",
                items = BuildJsonSchema(enumerableType.GetGenericArguments()[0], activeTypes)
            };
        }

        if (!activeTypes.Add(type)) return new { type = "object" };
        var properties = type.GetProperties()
            .Where(property => property.CanRead && property.GetIndexParameters().Length == 0)
            .ToDictionary(
                property => JsonNamingPolicy.CamelCase.ConvertName(property.Name),
                property => BuildJsonSchema(property.PropertyType, activeTypes));
        activeTypes.Remove(type);
        return new
        {
            type = "object",
            properties,
            additionalProperties = false
        };
    }

    private async Task<LocalAiStatus> WaitForReadyStatusAsync(bool requireModel, CancellationToken cancellationToken)
    {
        LocalAiStatus status = new();
        for (var attempt = 0; attempt < 20; attempt++)
        {
            status = await GetStatusAsync(false, cancellationToken);
            if (status.ServerOnline && (!requireModel || status.ModelReady)) return status;
            await Task.Delay(750, cancellationToken);
        }
        return await GetStatusAsync(cancellationToken: cancellationToken);
    }

    private static void StartLmStudio(string path)
    {
        Process.Start(new ProcessStartInfo(path)
        {
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static async Task<(bool Success, string Message, string Output)> RunProcessAsync(
        string fileName,
        IReadOnlyCollection<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(fileName)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return (false, $"Command timed out after {timeout.TotalSeconds:0} seconds.", string.Empty);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var output = string.Join(" ", new[] { await stdout, await stderr }.Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
        var cleaned = System.Text.RegularExpressions.Regex.Replace(output, @"\x1B\[[0-?]*[ -/]*[@-~]", string.Empty);
        return (process.ExitCode == 0, string.IsNullOrWhiteSpace(cleaned) ? $"Command exited with code {process.ExitCode}." : Trim(cleaned, 500), output);
    }

    private static async Task<string?> ResolveModelKeyAsync(string lmsPath, string modelPath, CancellationToken cancellationToken)
    {
        var modelsRoot = ModelsRoot();
        var relativePath = Path.GetRelativePath(modelsRoot, modelPath).Replace('\\', '/');
        var result = await RunProcessAsync(lmsPath, ["ls", "--llm", "--json"], TimeSpan.FromSeconds(45), cancellationToken);
        if (!result.Success || string.IsNullOrWhiteSpace(result.Output)) return null;
        try
        {
            using var document = JsonDocument.Parse(result.Output);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            foreach (var model in document.RootElement.EnumerateArray())
            {
                if (model.TryGetProperty("path", out var path)
                    && string.Equals(path.GetString(), relativePath, StringComparison.OrdinalIgnoreCase)
                    && model.TryGetProperty("modelKey", out var key))
                {
                    return key.GetString();
                }
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    private List<LocalAiModelOption> FindAvailableModels()
    {
        var root = ModelsRoot();
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*.gguf", SearchOption.AllDirectories)
            .Where(path => !Path.GetFileName(path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains("FLUX", StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var info = new FileInfo(path);
                return new LocalAiModelOption(
                    Path.GetFileNameWithoutExtension(path),
                    Path.GetRelativePath(root, path).Replace('\\', '/'),
                    path,
                    info.Length);
            })
            .OrderBy(model => model.Name)
            .ToList();
    }

    private static string? FindLmsPath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "bin", "lms.exe");
        return File.Exists(path) ? path : null;
    }

    private static string? FindLmStudioPath()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LM Studio", "LM Studio.exe");
        return File.Exists(path) ? path : null;
    }

    private static string ModelsRoot() => Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".lmstudio", "models"));

    private async Task<Dictionary<string, string?>> GetSettingValuesAsync(CancellationToken cancellationToken)
        => await db.AppSettings.AsNoTracking()
            .Where(x => x.Key.StartsWith("LocalAi:"))
            .ToDictionaryAsync(x => x.Key, x => x.Value, cancellationToken);

    private static LocalAiSettings BuildSettings(IReadOnlyDictionary<string, string?> values) =>
        new(
            NormalizeBaseUrl(values.GetValueOrDefault("LocalAi:BaseUrl")),
            NormalizeModelPath(values.GetValueOrDefault("LocalAi:ModelPath")),
            NormalizeIdentifier(values.GetValueOrDefault("LocalAi:ModelIdentifier")),
            ClampInt(values.GetValueOrDefault("LocalAi:ContextLength"), DefaultContextLength, 2048, 131072),
            ClampInt(values.GetValueOrDefault("LocalAi:IdleUnloadSeconds"), DefaultIdleUnloadSeconds, 60, 86400));

    private static LocalAiSettings BuildStatusSettings(
        IReadOnlyDictionary<string, string?> values,
        out string? configurationWarning,
        out string? selectedModelPath)
    {
        configurationWarning = null;
        selectedModelPath = null;

        var baseUrl = TryNormalizeSetting(
            () => NormalizeBaseUrl(values.GetValueOrDefault("LocalAi:BaseUrl")),
            DefaultBaseUrl,
            ref configurationWarning);

        var rawModelPath = values.GetValueOrDefault("LocalAi:ModelPath");
        var modelPath = TryNormalizeSetting(
            () => NormalizeModelPath(rawModelPath),
            null,
            ref configurationWarning);
        selectedModelPath = modelPath ?? CleanPathForDisplay(rawModelPath);

        return new(
            baseUrl,
            modelPath,
            TryNormalizeSetting(
                () => NormalizeIdentifier(values.GetValueOrDefault("LocalAi:ModelIdentifier")),
                DefaultIdentifier,
                ref configurationWarning),
            ClampInt(values.GetValueOrDefault("LocalAi:ContextLength"), DefaultContextLength, 2048, 131072),
            ClampInt(values.GetValueOrDefault("LocalAi:IdleUnloadSeconds"), DefaultIdleUnloadSeconds, 60, 86400));
    }

    private static T TryNormalizeSetting<T>(Func<T> normalize, T fallback, ref string? configurationWarning)
    {
        try
        {
            return normalize();
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or ArgumentException
            or IOException
            or NotSupportedException
            or UnauthorizedAccessException)
        {
            configurationWarning ??= ex.Message;
            return fallback;
        }
    }

    private static string NormalizeBaseUrl(string? value)
    {
        var cleaned = string.IsNullOrWhiteSpace(value) ? DefaultBaseUrl : value.Trim().TrimEnd('/');
        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp
            || !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("::1", StringComparison.OrdinalIgnoreCase))
            || uri.Port is < 1 or > 65535
            || !string.IsNullOrWhiteSpace(uri.UserInfo)
            || uri.AbsolutePath != "/")
        {
            throw new InvalidOperationException("Local AI server URL must be a loopback HTTP origin such as http://127.0.0.1:1234.");
        }
        return $"{uri.Scheme}://{uri.Authority}";
    }

    private static string? NormalizeModelPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var path = Path.GetFullPath(value.Trim());
        var root = ModelsRoot();
        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(path).Equals(".gguf", StringComparison.OrdinalIgnoreCase)
            || !File.Exists(path)
            || Path.GetFileName(path).StartsWith("mmproj", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Choose a downloaded GGUF model from the local LM Studio models folder.");
        }
        return path;
    }

    private static string? CleanPathForDisplay(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        try
        {
            return Path.GetFullPath(value.Trim());
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string NormalizeIdentifier(string? value)
    {
        var identifier = string.IsNullOrWhiteSpace(value) ? DefaultIdentifier : value.Trim();
        if (identifier.Length > 80 || identifier.Any(character => !(char.IsLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new InvalidOperationException("The local model identifier may contain only letters, numbers, dots, dashes, and underscores.");
        }
        return identifier;
    }

    private static int ClampInt(string? value, int fallback, int min, int max) =>
        int.TryParse(value, out var parsed) ? Math.Clamp(parsed, min, max) : fallback;

    private async Task UpsertAsync(string key, string value, CancellationToken cancellationToken)
    {
        var setting = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting is null)
        {
            db.AppSettings.Add(new AppSetting { Key = key, Value = value, Notes = "Local AI setting. Loopback-only; never stores an API token." });
        }
        else
        {
            setting.Value = value;
        }
    }

    private static string StripJsonFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline >= 0 && lastFence > firstNewline ? trimmed[(firstNewline + 1)..lastFence].Trim() : trimmed;
    }

    private static string JsonCandidate(string value)
    {
        var stripped = StripJsonFence(value);
        var first = stripped.IndexOf('{');
        var last = stripped.LastIndexOf('}');
        return first >= 0 && last > first ? stripped[first..(last + 1)] : stripped;
    }

    private static string Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return value.Length <= max ? value : value[..max].TrimEnd() + "...";
    }
}
