using System.Text.Json;

namespace EPATA.BusinessLedger.Services;

public class InvoiceAppImportService(HttpClient httpClient, IConfiguration configuration)
{
    public async Task<object> TryReadInvoiceAppAsync(string? baseUrl = null)
    {
        var root = (baseUrl ?? configuration["App:InvoiceAppUrl"] ?? "http://localhost:5057/").TrimEnd('/');
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
                using var response = await httpClient.GetAsync(url);
                var body = await response.Content.ReadAsStringAsync();
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
            catch (Exception ex)
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
}
