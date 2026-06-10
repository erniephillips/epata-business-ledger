namespace EPATA.BusinessLedger.Models;

public sealed record LocalAiSettings(
    string BaseUrl,
    string? ModelPath,
    string ModelIdentifier,
    int ContextLength,
    int IdleUnloadSeconds);

public sealed record SaveLocalAiSettingsRequest(
    string? BaseUrl,
    string? ModelPath,
    string? ModelIdentifier,
    int ContextLength,
    int IdleUnloadSeconds);

public sealed record LocalAiModelOption(
    string Name,
    string RelativePath,
    string FullPath,
    long Bytes);

public sealed class LocalAiStatus
{
    public bool LocalOnly { get; set; } = true;
    public bool LmsInstalled { get; set; }
    public string? LmsPath { get; set; }
    public string? LmStudioPath { get; set; }
    public bool ServerOnline { get; set; }
    public bool ModelReady { get; set; }
    public string State { get; set; } = "Off";
    public string BaseUrl { get; set; } = "http://127.0.0.1:1234";
    public string? SelectedModelPath { get; set; }
    public string ModelIdentifier { get; set; } = "epata-local";
    public int ContextLength { get; set; } = 8192;
    public int IdleUnloadSeconds { get; set; } = 1800;
    public List<string> LoadedModels { get; set; } = [];
    public List<LocalAiModelOption> AvailableModels { get; set; } = [];
    public string? Message { get; set; }
    public string Safety { get; set; } = "Loopback-only. Local AI data stays on this computer and is sent only after an explicit AI action.";
}

public sealed record LocalAiActionResult(bool Success, string Message, LocalAiStatus Status);

public sealed record LocalAiConnection(string ChatEndpoint, string Model, string Provider);
