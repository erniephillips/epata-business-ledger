using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace EPATA.BusinessLedger.WebApplicationFactoryTests;

public static class TestProgram
{
    public static async Task Main()
    {
        var root = FindWorkspaceRoot();
        var dbPath = Path.Combine(root, "Data", "webapplicationfactory-tests.db");
        foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            File.Delete(path);
        }

        await using var factory = new EpataFactory(root, dbPath);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await AssertEndpointGroupReadsAsync(client);
        await AssertGenericRouteListsAsync(client);
        await AssertCrudAndDocumentWorkflowsAsync(client);
        await AssertExportsAndSafetyAsync(client);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            WebApplicationFactoryRound100 = "pass",
            TypeChangeCreateInvoiceRound106 = "pass",
            Database = dbPath,
            EndpointGroups = "core,tax,ai-status,operations-status,lookups,documents,type-change-create-invoice,generic-crud,exports,backup,safety"
        }));
    }

static async Task AssertEndpointGroupReadsAsync(HttpClient client)
{
    var health = await GetJsonAsync(client, "/api/health");
    AssertEqual("ok", health.GetProperty("status").GetString(), "health status");
    AssertContains("webapplicationfactory-tests.db", health.GetProperty("database").GetString(), "health database");
    AssertDoesNotContain("epata-business-ledger.db", health.GetProperty("database").GetString(), "health database");

    foreach (var path in new[]
    {
        "/api/dashboard",
        "/api/tax-audit",
        "/api/tax-profile",
        "/api/tax-summary?year=2026",
        "/api/tax-calendar?year=2026",
        "/api/ai/review/status",
        "/api/ai/review",
        "/api/action-items/automation-preview",
        "/api/ai/operations/status",
        "/api/ai/operations/reconciliation",
        "/api/ai/local/status",
        "/api/job-timeline",
        "/api/lookups",
        "/api/config",
        "/api/documents",
        "/api/documents/stats",
        "/api/invoice-documents",
        "/api/invoice-documents/stats",
        "/api/app-info"
    })
    {
        using var response = await client.GetAsync(path);
        AssertStatus(HttpStatusCode.OK, response.StatusCode, path);
    }
}

static async Task AssertGenericRouteListsAsync(HttpClient client)
{
    foreach (var route in new[]
    {
        "parties",
        "sales",
        "customer-jobs",
        "customer-communications",
        "printer-queue-items",
        "receivable-invoices",
        "bills",
        "expenses",
        "products",
        "assets",
        "makerworld-rewards",
        "audit-documents",
        "business-accounts",
        "action-items",
        "tax-obligations",
        "mileage-logs",
        "settings"
    })
    {
        using var response = await client.GetAsync($"/api/{route}");
        AssertStatus(HttpStatusCode.OK, response.StatusCode, route);
    }
}

static async Task AssertCrudAndDocumentWorkflowsAsync(HttpClient client)
{
    var sale = await PostJsonAsync(client, "/api/sales", new
    {
        saleDate = "2026-06-20",
        platform = "Direct",
        paymentMethod = "Cash",
        salesTaxHandling = "Seller collected and remitted",
        orderNumber = "WAF-SALE-0001",
        customerName = "WAF Customer",
        productName = "WAF Widget",
        quantity = 1,
        itemSales = 25,
        shippingCharged = 0,
        salesTaxCollected = 0,
        customerPaid = 25,
        platformFees = 0,
        shippingLabelCost = 0,
        refunds = 0,
        estimatedCogs = 5,
        status = "Paid",
        includeInDashboard = true,
        needsReview = false,
        sourceProof = "waf-sale.pdf"
    });
    var saleId = sale.GetProperty("id").GetInt32();
    AssertTrue(saleId > 0, "sale id should be assigned");
    var readSale = await GetJsonAsync(client, $"/api/sales/{saleId}");
    AssertEqual("WAF-SALE-0001", readSale.GetProperty("orderNumber").GetString(), "sale readback");

    var estimate = await PostJsonAsync(client, "/api/documents", NewDocumentPayload("ESTIMATE", "Sent", "EST-2099-WAF-0001", 0));
    var estimateId = estimate.GetProperty("id").GetInt32();
    AssertTrue(estimateId > 0, "estimate id should be assigned");

    using var unsafeMutation = await client.PutAsJsonAsync($"/api/documents/{estimateId}", NewDocumentPayload("INVOICE", "Paid", "INV-2099-WAF-BAD", 30));
    AssertStatus(HttpStatusCode.BadRequest, unsafeMutation.StatusCode, "unsafe estimate-to-invoice mutation");

    var estimateAfter = await GetJsonAsync(client, $"/api/documents/{estimateId}");
    AssertEqual("ESTIMATE", estimateAfter.GetProperty("docType").GetString(), "unsafe mutation preserved document type");
    AssertEqual("EST-2099-WAF-0001", estimateAfter.GetProperty("docNumber").GetString(), "unsafe mutation preserved document number");

    var converted = await PostJsonAsync(client, $"/api/documents/{estimateId}/convert-to-invoice", new { });
    var convertedId = converted.GetProperty("id").GetInt32();
    var convertedNumber = converted.GetProperty("docNumber").GetString();
    AssertTrue(convertedId != estimateId, "estimate conversion should create a new invoice id");
    AssertEqual("INVOICE", converted.GetProperty("docType").GetString(), "converted document type");
    AssertContains("INV-", convertedNumber, "converted invoice number");
    AssertEqual("Draft", converted.GetProperty("status").GetString(), "converted invoice starts draft");

    var estimateAfterConvert = await GetJsonAsync(client, $"/api/documents/{estimateId}");
    AssertEqual("ESTIMATE", estimateAfterConvert.GetProperty("docType").GetString(), "conversion preserved original estimate type");
    AssertEqual("EST-2099-WAF-0001", estimateAfterConvert.GetProperty("docNumber").GetString(), "conversion preserved original estimate number");
    AssertEqual("Accepted", estimateAfterConvert.GetProperty("status").GetString(), "conversion marked original estimate accepted");
    AssertContains(convertedNumber!, estimateAfterConvert.GetProperty("projectNotes").GetString(), "conversion linked estimate notes to invoice");

    using var paidConversionResponse = await client.PutAsJsonAsync(
        $"/api/invoice-documents/{convertedId}",
        NewDocumentPayload("INVOICE", "Paid", convertedNumber!, 30));
    AssertStatus(HttpStatusCode.OK, paidConversionResponse.StatusCode, "pay converted invoice");
    var paidConversion = await ReadJsonAsync(paidConversionResponse);
    AssertEqual("INVOICE", paidConversion.GetProperty("docType").GetString(), "paid converted document type");
    AssertEqual("Paid", paidConversion.GetProperty("status").GetString(), "paid converted status");
    AssertEqual("30", paidConversion.GetProperty("amountPaid").GetDecimal().ToString(CultureInfo.InvariantCulture), "paid converted amount");

    var convertedReceivables = await GetJsonArrayAsync(client, "/api/receivable-invoices?includeArchived=true");
    AssertTrue(convertedReceivables.Any(x =>
        string.Equals(x.GetProperty("invoiceNumber").GetString(), convertedNumber, StringComparison.Ordinal)
        && string.Equals(x.GetProperty("sourceProof").GetString(), $"Unified invoice {convertedNumber}", StringComparison.Ordinal)),
        "paid converted invoice should have generated AR row");
    AssertTrue(!convertedReceivables.Any(x =>
        string.Equals(x.GetProperty("invoiceNumber").GetString(), "EST-2099-WAF-0001", StringComparison.Ordinal)),
        "original estimate number should not become an AR invoice");

    var convertedSales = await GetJsonArrayAsync(client, "/api/sales?includeArchived=true");
    AssertTrue(convertedSales.Any(x =>
        string.Equals(x.GetProperty("invoiceNumber").GetString(), convertedNumber, StringComparison.Ordinal)
        && string.Equals(x.GetProperty("sourceProof").GetString(), $"Unified invoice {convertedNumber}", StringComparison.Ordinal)
        && x.GetProperty("includeInDashboard").GetBoolean()),
        "paid converted invoice should have generated included Sale row");
    AssertTrue(!convertedSales.Any(x =>
        string.Equals(x.GetProperty("invoiceNumber").GetString(), "EST-2099-WAF-0001", StringComparison.Ordinal)),
        "original estimate number should not become a Sale invoice number");

    var invoice = await PostJsonAsync(client, "/api/invoice-documents", NewDocumentPayload("INVOICE", "Paid", "INV-2099-WAF-0001", 30));
    var invoiceId = invoice.GetProperty("id").GetInt32();
    var invoiceAlias = await GetJsonAsync(client, $"/api/documents/{invoiceId}");
    AssertEqual("INV-2099-WAF-0001", invoiceAlias.GetProperty("docNumber").GetString(), "document aliases agree");
}

static async Task AssertExportsAndSafetyAsync(HttpClient client)
{
    foreach (var path in new[]
    {
        "/api/export/sales?includeArchived=true",
        "/api/export/tax-summary?year=2026",
        "/api/export/nj-sales-tax?year=2026"
    })
    {
        using var response = await client.GetAsync(path);
        AssertStatus(HttpStatusCode.OK, response.StatusCode, path);
        var csv = await response.Content.ReadAsStringAsync();
        AssertTrue(csv.Contains(",", StringComparison.Ordinal), $"{path} should look like CSV");
    }

    using (var backup = await client.GetAsync("/api/database/backup"))
    {
        AssertStatus(HttpStatusCode.OK, backup.StatusCode, "database backup");
        var bytes = await backup.Content.ReadAsByteArrayAsync();
        var signature = System.Text.Encoding.ASCII.GetString(bytes, 0, 15);
        AssertEqual("SQLite format 3", signature, "backup signature");
    }

    using (var clear = await client.PostAsync("/api/database/clear", null))
    {
        AssertStatus(HttpStatusCode.BadRequest, clear.StatusCode, "database clear stays blocked");
    }

    using (var import = await client.PostAsync("/api/database/import", null))
    {
        AssertStatus(HttpStatusCode.BadRequest, import.StatusCode, "database import stays blocked");
    }
}

static async Task<JsonElement> GetJsonAsync(HttpClient client, string path)
{
    using var response = await client.GetAsync(path);
    AssertStatus(HttpStatusCode.OK, response.StatusCode, path);
    return await ReadJsonAsync(response);
}

static async Task<JsonElement> PostJsonAsync(HttpClient client, string path, object payload)
{
    using var response = await client.PostAsJsonAsync(path, payload);
    AssertTrue(
        response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Created,
        $"{path} expected OK/Created, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    return await ReadJsonAsync(response);
}

static async Task<JsonElement[]> GetJsonArrayAsync(HttpClient client, string path)
{
    var root = await GetJsonAsync(client, path);
    return root.EnumerateArray().Select(x => x.Clone()).ToArray();
}

static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
{
    await using var stream = await response.Content.ReadAsStreamAsync();
    using var document = await JsonDocument.ParseAsync(stream);
    return document.RootElement.Clone();
}

static object NewDocumentPayload(string docType, string status, string docNumber, decimal paid)
{
    var total = 30m;
    return new
    {
        docNumber,
        docType,
        status,
        customerName = "WAF Customer",
        customerPhone = "973-555-0100",
        customerAddress = "WAF Test Lane",
        customerEmail = "waf@example.test",
        preparedFor = "WAF Customer",
        projectName = "WAF Project",
        material = "PLA",
        color = "Black",
        infill = "20%",
        projectDescription = "WebApplicationFactory test document",
        projectNotes = "Internal WAF note",
        pageSize = "Letter",
        docDate = "2026-06-20",
        dueDate = "2026-07-04",
        subtotal = total,
        discountAmount = 0,
        rushAmount = 0,
        taxAmount = 0,
        total,
        amountPaid = paid,
        balance = Math.Max(0, total - paid),
        paymentMethod = "Cash",
        pricingGuide = "WAF pricing",
        termsNotes = "WAF terms",
        standardTurnaround = "5 days",
        rushTurnaround = "2 days",
        calcGrams = 10,
        calcHours = 1,
        calcDesignHours = 0,
        calcSetupFee = 0,
        calcPostFee = 0,
        calcGramRate = 0.05,
        calcHourRate = 3,
        calcDesignRate = 25,
        calcMinimum = 15,
        calcDifficulty = 1,
        calcRush = 0,
        calcDiscount = 0,
        calcTaxRate = 0,
        lineItems = new[]
        {
            new
            {
                sortOrder = 0,
                description = "WAF line",
                details = "WebApplicationFactory line item",
                quantity = 1,
                rate = total,
                amount = total
            }
        },
        json = "{}"
    };
}

static string FindWorkspaceRoot()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EPATA.BusinessLedger.csproj")))
    {
        directory = directory.Parent;
    }

    if (directory is null)
    {
        throw new InvalidOperationException("Could not find EPATA.BusinessLedger.csproj from " + AppContext.BaseDirectory);
    }

    return directory.FullName;
}

static void AssertStatus(HttpStatusCode expected, HttpStatusCode actual, string label)
{
    if (actual != expected)
    {
        throw new InvalidOperationException($"{label}: expected {(int)expected}, got {(int)actual}.");
    }
}

static void AssertTrue(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static void AssertEqual(string? expected, string? actual, string label)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}.");
    }
}

static void AssertContains(string expected, string? actual, string label)
{
    if (actual is null || !actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"{label}: expected value to contain {expected}, got {actual}.");
    }
}

static void AssertDoesNotContain(string unexpected, string? actual, string label)
{
    if (actual?.Contains(unexpected, StringComparison.OrdinalIgnoreCase) == true)
    {
        throw new InvalidOperationException($"{label}: value should not contain {unexpected}, got {actual}.");
    }
}
}

sealed class EpataFactory(string root, string dbPath) : WebApplicationFactory<global::Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseContentRoot(root);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Data Source=" + dbPath,
                ["App:OpenBrowserOnStart"] = "false",
                ["App:Url"] = "http://127.0.0.1:0",
                ["Ai:AllowHostedFallback"] = "false"
            });
        });
    }
}
