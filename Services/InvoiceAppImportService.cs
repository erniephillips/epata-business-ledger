using System.Net;
using System.Text.Json;

namespace EPATA.BusinessLedger.Services;

public class InvoiceAppImportService(HttpClient httpClient, IConfiguration configuration)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<List<InvoiceAppDocument>> ReadDocumentsAsync(
        string? baseUrl = null,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot(baseUrl);
        using var response = await httpClient.GetAsync($"{root}/api/documents", cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        var docs = JsonSerializer.Deserialize<List<InvoiceAppDocument>>(body, JsonOptions);

        return docs ?? [];
    }

    public async Task<List<InvoiceAppFullDocument>> ReadFullDocumentsAsync(
        string? baseUrl = null,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot(baseUrl);
        var summaries = await ReadDocumentsAsync(root, cancellationToken);
        var full = new List<InvoiceAppFullDocument>();

        foreach (var summary in summaries)
        {
            try
            {
                using var response = await httpClient.GetAsync($"{root}/api/documents/{summary.Id}", cancellationToken);
                response.EnsureSuccessStatusCode();
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                var doc = JsonSerializer.Deserialize<InvoiceAppFullDocument>(body, JsonOptions);
                if (doc is not null)
                {
                    full.Add(doc);
                    continue;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Fall back to the list row if an older invoice app cannot return details.
            }

            full.Add(new InvoiceAppFullDocument(
                summary.Id,
                summary.DocNumber,
                summary.DocType,
                summary.Status,
                summary.CustomerName,
                null,
                null,
                null,
                null,
                summary.ProjectName,
                null,
                null,
                null,
                null,
                null,
                null,
                summary.DocDate,
                summary.DueDate,
                summary.Total,
                0,
                0,
                0,
                summary.Total,
                summary.AmountPaid,
                summary.Balance,
                null,
                null,
                null,
                null,
                0,
                0,
                0,
                0,
                0,
                0.05m,
                3,
                25,
                15,
                1,
                0,
                0,
                0,
                "[]",
                summary.CreatedAt,
                summary.UpdatedAt,
                []));
        }

        return full;
    }

    public async Task<object> TryReadInvoiceAppAsync(
        string? baseUrl = null,
        CancellationToken cancellationToken = default)
    {
        var root = ResolveRoot(baseUrl);
        var candidates = new[]
        {
            $"{root}/api/documents",
            $"{root}/api/documents/latest",
            $"{root}/api/invoices",
            $"{root}/api/estimates"
        };

        var attempts = new List<object>();
        foreach (var url in candidates)
        {
            try
            {
                using var response = await httpClient.GetAsync(url, cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                attempts.Add(new { url, ok = response.IsSuccessStatusCode, status = (int)response.StatusCode, preview = body.Length > 800 ? body[..800] : body });

                if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(body))
                {
                    JsonDocument? json = null;
                    try { json = JsonDocument.Parse(body); } catch { }
                    return new
                    {
                        ok = true,
                        message = "Invoice app responded. Review the preview before importing anything permanently.",
                        sourceUrl = url,
                        jsonKind = json?.RootElement.ValueKind.ToString(),
                        preview = body.Length > 4000 ? body[..4000] : body,
                        attempts
                    };
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                attempts.Add(new { url, ok = false, error = ex.Message });
            }
        }

        return new
        {
            ok = false,
            message = "Could not read the invoice app. Make sure http://localhost:5057/ is running, then try again. This app is still usable without import.",
            attempts
        };
    }

    private string ResolveRoot(string? baseUrl)
    {
        var value = (baseUrl ?? configuration["App:InvoiceAppUrl"] ?? "http://localhost:5057/").Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || !IsLoopbackHost(uri.Host))
        {
            throw new InvalidOperationException("The legacy invoice app URL must be a loopback HTTP URL such as http://localhost:5057.");
        }

        return value.TrimEnd('/');
    }

    private static bool IsLoopbackHost(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || IPAddress.TryParse(host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
}

public sealed record InvoiceAppDocument(
    int Id,
    string? DocNumber,
    string? DocType,
    string? Status,
    string? CustomerName,
    string? ProjectName,
    decimal Total,
    decimal AmountPaid,
    decimal Balance,
    string? DocDate,
    string? DueDate,
    string? CreatedAt,
    string? UpdatedAt);

public sealed record InvoiceAppFullDocument(
    int Id,
    string? DocNumber,
    string? DocType,
    string? Status,
    string? CustomerName,
    string? CustomerPhone,
    string? CustomerAddress,
    string? CustomerEmail,
    string? PreparedFor,
    string? ProjectName,
    string? Material,
    string? Color,
    string? Infill,
    string? ProjectDescription,
    string? ProjectNotes,
    string? PageSize,
    string? DocDate,
    string? DueDate,
    decimal Subtotal,
    decimal DiscountAmount,
    decimal RushAmount,
    decimal TaxAmount,
    decimal Total,
    decimal AmountPaid,
    decimal Balance,
    string? PricingGuide,
    string? TermsNotes,
    string? StandardTurnaround,
    string? RushTurnaround,
    decimal CalcGrams,
    decimal CalcHours,
    decimal CalcDesignHours,
    decimal CalcSetupFee,
    decimal CalcPostFee,
    decimal CalcGramRate,
    decimal CalcHourRate,
    decimal CalcDesignRate,
    decimal CalcMinimum,
    decimal CalcDifficulty,
    decimal CalcRush,
    decimal CalcDiscount,
    decimal CalcTaxRate,
    string? Json,
    string? CreatedAt,
    string? UpdatedAt,
    List<InvoiceAppLineItem>? LineItems);

public sealed record InvoiceAppLineItem(
    int Id,
    int SortOrder,
    string? Description,
    string? Details,
    decimal Quantity,
    decimal Rate,
    decimal Amount);
