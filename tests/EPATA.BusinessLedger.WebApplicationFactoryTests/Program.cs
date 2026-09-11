using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using EPATA.BusinessLedger.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace EPATA.BusinessLedger.WebApplicationFactoryTests;

public static class TestProgram
{
    public static async Task Main()
    {
        var root = FindWorkspaceRoot();
        var requestedDbPath = Environment.GetEnvironmentVariable("EPATA_WAF_DB_PATH");
        var dbPath = string.IsNullOrWhiteSpace(requestedDbPath)
            ? Path.Combine(root, "Data", "webapplicationfactory-tests.db")
            : Path.GetFullPath(requestedDbPath);
        if (string.IsNullOrWhiteSpace(requestedDbPath))
        {
            foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                File.Delete(path);
            }
        }
        else if (File.Exists(dbPath) || File.Exists(dbPath + "-wal") || File.Exists(dbPath + "-shm"))
        {
            throw new InvalidOperationException($"EPATA_WAF_DB_PATH must name a new database so an existing checkpoint is never reset: {dbPath}");
        }

        await using var factory = new EpataFactory(root, dbPath);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        using var concurrentClient = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        await AssertEndpointGroupReadsAsync(client, dbPath);
        await AssertConfigAndAiStatusAsync(client);
        await AssertGenericRouteListsAsync(client);
        await AssertCrudAndDocumentWorkflowsAsync(client);
        await AssertAutomaticEtsyDocumentIntakeAsync(client, root);
        await AssertLegacyAuditDocumentFingerprintBackfillAsync(client, root);
        await AssertMissingAuditProofRepairAsync(client, root);
        await AssertAuditDocumentFingerprintRestoreSafetyAsync(client, root);
        await AssertMarketplaceRestoreCollisionSafetyAsync(client, concurrentClient);
        await AssertAutomaticInvoiceDocumentIntakeAsync(client, root);
        await AssertAutomaticInvoiceDocumentAccountingSafetyAsync(client, root);
        await AssertOrderLossAndTurboTaxWorkbookAsync(client);
        await AssertSafeRouteSmokeMatrixAsync(client);
        await AssertBackendMutationSafetyAsync(client, concurrentClient);
        await AssertExportsAndSafetyAsync(client, root);
        await AssertAiEstimateChatQuickRulesAsync(client);
        await AssertAiEstimateDraftCleansGmailPasteAsync(client);
        await AssertLocalAiTextCompletionUsesRequestedTokenBudgetAsync(root);

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            WebApplicationFactoryRound100 = "pass",
            TypeChangeCreateInvoiceRound106 = "pass",
            DevIdentityAndCloudAiStatus = "pass",
            AiEstimateChatQuickRules = "pass",
            AiEstimateDraftGmailCleanup = "pass",
            LocalAiTextTokenBudget = "pass",
            LocalAiCancellation = "pass",
            BackendMutationSafety = "pass",
            AutomaticEtsyDocumentIntake = "pass",
            AutomaticInvoiceDocumentIntake = "pass",
            AutomaticInvoiceDocumentAccountingSafety = "pass",
            Database = dbPath,
            EndpointGroups = "core,tax,ai-status,operations-status,lookups,documents,type-change-create-invoice,generic-crud,exports,backup,safety"
        }));
}

static async Task AssertLegacyAuditDocumentFingerprintBackfillAsync(HttpClient client, string root)
{
    const string fileName = "WAF-legacy-null-fingerprint-proof.pdf";
    const string relatedType = "Expense";
    const string relatedNumber = "WAF-LEGACY-PROOF-001";
    var pdf = CreateMinimalTextPdf("Legacy upload fingerprint fixture", "Amount $27.45");
    var uploadRoot = Path.Combine(root, "UploadedDocs");
    Directory.CreateDirectory(uploadRoot);
    var legacyPath = Path.Combine(uploadRoot, $"legacy-{Guid.NewGuid():N}-{fileName}");
    await File.WriteAllBytesAsync(legacyPath, pdf);

    try
    {
        var legacy = await PostJsonAsync(client, "/api/audit-documents", new
        {
            documentDate = "2026-09-10",
            documentType = "Receipt",
            relatedRecordType = relatedType,
            relatedRecordNumber = relatedNumber,
            fileName,
            filePathOrUrl = legacyPath,
            needsReview = true,
            notes = "Pre-fingerprint upload fixture"
        });
        var legacyId = legacy.GetProperty("id").GetInt32();
        AssertTrue(
            legacy.GetProperty("uploadFingerprint").ValueKind is JsonValueKind.Null,
            "legacy proof starts without an upload fingerprint");

        var matchingFilesBefore = Directory.EnumerateFiles(uploadRoot)
            .Count(path => Path.GetFileName(path).EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        var matchingRowsBefore = (await GetJsonArrayAsync(client, "/api/audit-documents?includeArchived=true"))
            .Count(document => string.Equals(document.GetProperty("fileName").GetString(), fileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(document.GetProperty("relatedRecordType").GetString(), relatedType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(document.GetProperty("relatedRecordNumber").GetString(), relatedNumber, StringComparison.OrdinalIgnoreCase));

        var exactRetry = await UploadDocumentAsync(client, fileName, pdf, relatedType, relatedNumber);
        AssertEqual("0", exactRetry.GetProperty("createdCount").GetInt32().ToString(CultureInfo.InvariantCulture), "legacy exact retry creates no Audit Doc");
        AssertEqual("1", exactRetry.GetProperty("duplicateUploadCount").GetInt32().ToString(CultureInfo.InvariantCulture), "legacy exact retry is identified");
        AssertEqual(
            legacyId.ToString(CultureInfo.InvariantCulture),
            exactRetry.GetProperty("documents")[0].GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture),
            "legacy exact retry returns the original Audit Doc");

        var claimed = await GetJsonAsync(client, $"/api/audit-documents/{legacyId}");
        AssertTrue(
            claimed.TryGetProperty("uploadFingerprint", out var fingerprint)
                && !string.IsNullOrWhiteSpace(fingerprint.GetString()),
            "legacy exact retry lazily claims a fingerprint on the canonical proof");
        var matchingRowsAfter = (await GetJsonArrayAsync(client, "/api/audit-documents?includeArchived=true"))
            .Count(document => string.Equals(document.GetProperty("fileName").GetString(), fileName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(document.GetProperty("relatedRecordType").GetString(), relatedType, StringComparison.OrdinalIgnoreCase)
                && string.Equals(document.GetProperty("relatedRecordNumber").GetString(), relatedNumber, StringComparison.OrdinalIgnoreCase));
        var matchingFilesAfter = Directory.EnumerateFiles(uploadRoot)
            .Count(path => Path.GetFileName(path).EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        AssertEqual(matchingRowsBefore.ToString(CultureInfo.InvariantCulture), matchingRowsAfter.ToString(CultureInfo.InvariantCulture), "legacy exact retry adds no database row");
        AssertEqual(matchingFilesBefore.ToString(CultureInfo.InvariantCulture), matchingFilesAfter.ToString(CultureInfo.InvariantCulture), "legacy exact retry retains no staged file");
    }
    finally
    {
        await DeleteUploadedTestProofsAsync(client, root, [fileName]);
    }
}

static async Task AssertMissingAuditProofRepairAsync(HttpClient client, string root)
{
    const string fileName = "WAF-missing-proof-reupload-repair.pdf";
    var pdf = CreateMinimalTextPdf("Missing proof repair fixture", "Amount $19.80");

    try
    {
        var firstUpload = await UploadDocumentAsync(client, fileName, pdf, "Expense");
        var original = firstUpload.GetProperty("documents")[0];
        var originalId = original.GetProperty("id").GetInt32();
        var missingPath = original.GetProperty("filePathOrUrl").GetString()!;
        File.Delete(missingPath);
        AssertTrue(!File.Exists(missingPath), "missing-proof fixture removes only its own freshly uploaded file");

        var exactRetry = await UploadDocumentAsync(client, fileName, pdf, "Expense");
        AssertEqual("0", exactRetry.GetProperty("createdCount").GetInt32().ToString(CultureInfo.InvariantCulture), "missing-proof exact retry creates no Audit Doc");
        AssertEqual("1", exactRetry.GetProperty("duplicateUploadCount").GetInt32().ToString(CultureInfo.InvariantCulture), "missing-proof exact retry is identified");
        AssertEqual(
            originalId.ToString(CultureInfo.InvariantCulture),
            exactRetry.GetProperty("documents")[0].GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture),
            "missing-proof exact retry repairs the original Audit Doc");

        var repaired = await GetJsonAsync(client, $"/api/audit-documents/{originalId}");
        var repairedPath = repaired.GetProperty("filePathOrUrl").GetString()!;
        AssertTrue(!string.Equals(missingPath, repairedPath, StringComparison.OrdinalIgnoreCase), "missing-proof retry stores a fresh proof path");
        AssertTrue(File.Exists(repairedPath), "missing-proof retry retains the replacement proof file");
        var matchingRows = (await GetJsonArrayAsync(client, "/api/audit-documents?includeArchived=true"))
            .Count(document => string.Equals(document.GetProperty("fileName").GetString(), fileName, StringComparison.OrdinalIgnoreCase));
        AssertEqual("1", matchingRows.ToString(CultureInfo.InvariantCulture), "missing-proof retry retains one Audit Doc row");
    }
    finally
    {
        await DeleteUploadedTestProofsAsync(client, root, [fileName]);
    }
}

static async Task AssertAuditDocumentFingerprintRestoreSafetyAsync(HttpClient client, string root)
{
    const string fileName = "WAF-proof-fingerprint-restore-safety.pdf";
    var pdf = CreateMinimalTextPdf("Proof restore safety fixture", "Amount $12.34");

    try
    {
        var firstUpload = await UploadDocumentAsync(client, fileName, pdf, "Expense");
        var archivedId = firstUpload.GetProperty("documents")[0].GetProperty("id").GetInt32();
        using (var archive = await client.DeleteAsync($"/api/audit-documents/{archivedId}"))
        {
            AssertStatus(HttpStatusCode.NoContent, archive.StatusCode, "archive original fingerprint proof");
        }

        var replacementUpload = await UploadDocumentAsync(client, fileName, pdf, "Expense");
        AssertEqual("1", replacementUpload.GetProperty("createdCount").GetInt32().ToString(CultureInfo.InvariantCulture), "re-upload after archive creates one active proof");
        var activeId = replacementUpload.GetProperty("documents")[0].GetProperty("id").GetInt32();
        AssertTrue(activeId != archivedId, "re-upload after archive has a new proof id");

        using (var blockedRestore = await client.PostAsJsonAsync($"/api/audit-documents/{archivedId}/restore", new { }))
        {
            AssertStatus(HttpStatusCode.Conflict, blockedRestore.StatusCode, "restore colliding archived fingerprint proof");
            var conflict = await ReadJsonAsync(blockedRestore);
            AssertEqual(activeId.ToString(CultureInfo.InvariantCulture), conflict.GetProperty("existingAuditDocumentId").GetInt32().ToString(CultureInfo.InvariantCulture), "restore conflict identifies active proof");
            AssertContains("same exact upload", conflict.GetProperty("message").GetString(), "restore fingerprint conflict message");
        }

        var collidingProofs = (await GetJsonArrayAsync(client, "/api/audit-documents?includeArchived=true"))
            .Where(document => string.Equals(document.GetProperty("fileName").GetString(), fileName, StringComparison.Ordinal))
            .ToArray();
        AssertEqual("1", collidingProofs.Count(document => !document.GetProperty("isArchived").GetBoolean()).ToString(CultureInfo.InvariantCulture), "failed restore leaves exactly one active proof");
        AssertTrue(collidingProofs.Single(document => document.GetProperty("id").GetInt32() == archivedId).GetProperty("isArchived").GetBoolean(), "failed restore keeps older proof archived");

        using (var archiveReplacement = await client.DeleteAsync($"/api/audit-documents/{activeId}"))
        {
            AssertStatus(HttpStatusCode.NoContent, archiveReplacement.StatusCode, "archive replacement fingerprint proof");
        }
        await PostJsonAsync(client, $"/api/audit-documents/{archivedId}/restore", new { });
        AssertTrue(!(await GetJsonAsync(client, $"/api/audit-documents/{archivedId}")).GetProperty("isArchived").GetBoolean(), "restore succeeds after active collision is archived");
    }
    finally
    {
        await DeleteUploadedTestProofsAsync(client, root, [fileName]);
    }
}

static async Task AssertMarketplaceRestoreCollisionSafetyAsync(HttpClient client, HttpClient concurrentClient)
{
    var firstSale = await PostJsonAsync(client, "/api/sales", new
    {
        saleDate = "2099-09-11",
        platform = "Etsy",
        orderNumber = "WAF-RESTORE-SALE-2099",
        customerName = "WAF Restore Customer A",
        productName = "Restore collision fixture A",
        quantity = 1,
        itemSales = 10,
        customerPaid = 10,
        status = "Paid",
        includeInDashboard = true
    });
    var secondSale = await PostJsonAsync(client, "/api/sales", new
    {
        saleDate = "2099-09-11",
        platform = "ET SY",
        orderNumber = "waf restore_sale #2099",
        customerName = "WAF Restore Customer B",
        productName = "Restore collision fixture B",
        quantity = 1,
        itemSales = 12,
        customerPaid = 12,
        status = "Paid",
        includeInDashboard = true
    });
    var firstSaleId = firstSale.GetProperty("id").GetInt32();
    var secondSaleId = secondSale.GetProperty("id").GetInt32();
    foreach (var saleId in new[] { firstSaleId, secondSaleId })
    {
        using var archive = await client.DeleteAsync($"/api/sales/{saleId}");
        AssertStatus(HttpStatusCode.NoContent, archive.StatusCode, "archive normalized marketplace Sale restore fixture");
    }

    var saleBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var saleRestores = new[]
    {
        RestoreAfterBarrierAsync(client, saleBarrier.Task, $"/api/sales/{firstSaleId}/restore"),
        RestoreAfterBarrierAsync(concurrentClient, saleBarrier.Task, $"/api/sales/{secondSaleId}/restore")
    };
    saleBarrier.SetResult(true);
    var saleResponses = await Task.WhenAll(saleRestores);
    try
    {
        AssertEqual("1", saleResponses.Count(response => response.StatusCode == HttpStatusCode.OK).ToString(CultureInfo.InvariantCulture), "simultaneous normalized Sale restore winner count");
        AssertEqual("1", saleResponses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ToString(CultureInfo.InvariantCulture), "simultaneous normalized Sale restore conflict count");

        var matchingSales = (await GetJsonArrayAsync(client, "/api/sales?includeArchived=true"))
            .Where(sale => sale.GetProperty("id").GetInt32() == firstSaleId || sale.GetProperty("id").GetInt32() == secondSaleId)
            .ToArray();
        var activeSale = matchingSales.Single(sale => !sale.GetProperty("isArchived").GetBoolean());
        var saleConflict = await ReadJsonAsync(saleResponses.Single(response => response.StatusCode == HttpStatusCode.Conflict));
        AssertEqual(activeSale.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture), saleConflict.GetProperty("existingSaleId").GetInt32().ToString(CultureInfo.InvariantCulture), "Sale restore conflict identifies active normalized duplicate");
        AssertContains("already uses", saleConflict.GetProperty("message").GetString(), "Sale restore collision message");
        AssertEqual("1", matchingSales.Count(sale => sale.GetProperty("isArchived").GetBoolean()).ToString(CultureInfo.InvariantCulture), "failed Sale restore remains archived");
    }
    finally
    {
        foreach (var response in saleResponses)
        {
            response.Dispose();
        }
    }

}

static async Task<HttpResponseMessage> RestoreAfterBarrierAsync(HttpClient client, Task barrier, string path)
{
    await barrier;
    return await client.PostAsJsonAsync(path, new { });
}

static async Task AssertConfigAndAiStatusAsync(HttpClient client)
{
    var config = await GetJsonAsync(client, "/api/config");
    AssertEqual("erniephillips26@gmail.com", config.GetProperty("businessEmail").GetString(), "default business email");

    using (var zeroRatesResponse = await client.PutAsJsonAsync("/api/config", InvoiceBuilderConfigPayload(config, 0, 0, 0, 0)))
    {
        AssertStatus(HttpStatusCode.OK, zeroRatesResponse.StatusCode, "save explicit zero calculator rates");
        var zeroRates = await ReadJsonAsync(zeroRatesResponse);
        AssertEqual("0", zeroRates.GetProperty("calcGramRate").GetDecimal().ToString(CultureInfo.InvariantCulture), "explicit zero gram rate");
        AssertEqual("0", zeroRates.GetProperty("calcHourRate").GetDecimal().ToString(CultureInfo.InvariantCulture), "explicit zero hour rate");
        AssertEqual("0", zeroRates.GetProperty("calcDesignRate").GetDecimal().ToString(CultureInfo.InvariantCulture), "explicit zero design rate");
        AssertEqual("0", zeroRates.GetProperty("calcMinimum").GetDecimal().ToString(CultureInfo.InvariantCulture), "explicit zero minimum");
    }
    var persistedZeroRates = await GetJsonAsync(client, "/api/config");
    AssertEqual("0", persistedZeroRates.GetProperty("calcGramRate").GetDecimal().ToString(CultureInfo.InvariantCulture), "persisted explicit zero gram rate");
    AssertEqual("0", persistedZeroRates.GetProperty("calcMinimum").GetDecimal().ToString(CultureInfo.InvariantCulture), "persisted explicit zero minimum");

    using (var restoreConfigResponse = await client.PutAsJsonAsync(
        "/api/config",
        InvoiceBuilderConfigPayload(
            config,
            config.GetProperty("calcGramRate").GetDecimal(),
            config.GetProperty("calcHourRate").GetDecimal(),
            config.GetProperty("calcDesignRate").GetDecimal(),
            config.GetProperty("calcMinimum").GetDecimal())))
    {
        AssertStatus(HttpStatusCode.OK, restoreConfigResponse.StatusCode, "restore calculator rates after zero round trip");
    }

    var status = await GetJsonAsync(client, "/api/ai/estimate/status");
    var cloud = status.GetProperty("cloudAi");
    AssertTrue(cloud.GetProperty("enabled").GetBoolean(), "cloud AI fallback should be enabled by configuration");
    AssertTrue(!cloud.GetProperty("configured").GetBoolean(), "cloud AI fallback should not be ready without the API key env var");
    AssertTrue(!cloud.GetProperty("apiKeyPresent").GetBoolean(), "cloud AI status should not claim a missing test API key is present");
    AssertEqual("EPATA_WAF_MISSING_API_KEY", cloud.GetProperty("apiKeyEnvironmentVariable").GetString(), "cloud AI API key env var");
    AssertEqual("erniephillips26@gmail.com", cloud.GetProperty("accountEmail").GetString(), "cloud AI account email");
    AssertContains("Provider-side billing", cloud.GetProperty("billing").GetString(), "cloud AI billing note");
    AssertContains("cloud fallback runs only when enabled", status.GetProperty("safety").GetString(), "cloud AI safety note");
}

static async Task AssertEndpointGroupReadsAsync(HttpClient client, string dbPath)
{
    var health = await GetJsonAsync(client, "/api/health");
    AssertEqual("ok", health.GetProperty("status").GetString(), "health status");
    AssertContains(Path.GetFileName(dbPath), health.GetProperty("database").GetString(), "health database");
    AssertDoesNotContain("epata-business-ledger.db", health.GetProperty("database").GetString(), "health database");

    foreach (var path in new[]
    {
        "/api/dashboard",
        "/api/tax-audit",
        "/api/tax-profile",
        "/api/tax-summary?year=2026",
            "/api/tax-calendar?year=2026",
            "/api/turbotax-setup?year=2026",
            "/api/turbotax-report?year=2026",
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
        "order-loss-incidents",
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

    var concurrentConversionRequests = Enumerable.Range(0, 4)
        .Select(index => client.PostAsJsonAsync(
            index % 2 == 0
                ? $"/api/documents/{estimateId}/convert-to-invoice"
                : $"/api/invoice-documents/{estimateId}/convert-to-invoice",
            new { }))
        .ToArray();
    var concurrentConversionResponses = await Task.WhenAll(concurrentConversionRequests);
    try
    {
        foreach (var response in concurrentConversionResponses)
        {
            AssertStatus(HttpStatusCode.OK, response.StatusCode, "concurrent repeated estimate conversion");
            var repeatedConversion = await ReadJsonAsync(response);
            AssertEqual(convertedId.ToString(CultureInfo.InvariantCulture), repeatedConversion.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture), "repeated conversion returns the original invoice");
            AssertEqual(convertedNumber, repeatedConversion.GetProperty("docNumber").GetString(), "repeated conversion preserves the invoice number");
            AssertEqual(estimateId.ToString(CultureInfo.InvariantCulture), repeatedConversion.GetProperty("sourceEstimateId").GetInt32().ToString(CultureInfo.InvariantCulture), "converted invoice keeps structural estimate link");
        }
    }
    finally
    {
        foreach (var response in concurrentConversionResponses)
        {
            response.Dispose();
        }
    }
    var conversionCopies = (await GetJsonArrayAsync(client, "/api/invoice-documents?includeArchived=true"))
        .Where(item => string.Equals(item.GetProperty("docNumber").GetString(), convertedNumber, StringComparison.Ordinal))
        .ToArray();
    AssertEqual("1", conversionCopies.Length.ToString(CultureInfo.InvariantCulture), "repeated conversion keeps one linked invoice");

    using (var convertInvoiceAgain = await client.PostAsJsonAsync($"/api/documents/{convertedId}/convert-to-invoice", new { }))
    {
        AssertStatus(HttpStatusCode.BadRequest, convertInvoiceAgain.StatusCode, "an invoice cannot be converted as though it were an estimate");
    }

    var estimateJobs = (await GetJsonArrayAsync(client, "/api/customer-jobs?includeArchived=true"))
        .Where(item => string.Equals(item.GetProperty("relatedInvoiceNumber").GetString(), "EST-2099-WAF-0001", StringComparison.Ordinal))
        .ToArray();
    AssertEqual("1", estimateJobs.Length.ToString(CultureInfo.InvariantCulture), "estimate conversion keeps one generated job");
    AssertEqual("Invoiced", estimateJobs[0].GetProperty("status").GetString(), "converted estimate job stays invoiced");
    var estimateJobId = estimateJobs[0].GetProperty("id").GetInt32();
    using (var directGeneratedJobArchive = await client.DeleteAsync($"/api/customer-jobs/{estimateJobId}"))
    {
        AssertStatus(HttpStatusCode.Conflict, directGeneratedJobArchive.StatusCode, "generated estimate job direct archive is blocked");
    }

    using (var archiveConvertedEstimate = await client.DeleteAsync($"/api/documents/{estimateId}"))
    {
        AssertStatus(HttpStatusCode.OK, archiveConvertedEstimate.StatusCode, "archive converted source estimate");
    }
    var jobWhileEstimateArchived = await GetJsonAsync(client, $"/api/customer-jobs/{estimateJobId}");
    AssertTrue(!jobWhileEstimateArchived.GetProperty("isArchived").GetBoolean(), "active converted invoice keeps generated job active");
    AssertEqual("Invoiced", jobWhileEstimateArchived.GetProperty("status").GetString(), "active converted invoice keeps generated job invoiced");
    await PostJsonAsync(client, $"/api/documents/{estimateId}/restore", new { });
    var jobAfterEstimateRestore = await GetJsonAsync(client, $"/api/customer-jobs/{estimateJobId}");
    AssertEqual("Invoiced", jobAfterEstimateRestore.GetProperty("status").GetString(), "restoring converted estimate does not revert job to quoted");

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

    var generatedReceivableId = convertedReceivables.Single(x =>
        string.Equals(x.GetProperty("invoiceNumber").GetString(), convertedNumber, StringComparison.Ordinal))
        .GetProperty("id").GetInt32();
    using (var directGeneratedReceivableArchive = await client.DeleteAsync($"/api/receivable-invoices/{generatedReceivableId}"))
    {
        AssertStatus(HttpStatusCode.Conflict, directGeneratedReceivableArchive.StatusCode, "generated receivable direct archive is blocked");
    }
    var generatedSaleId = convertedSales.Single(x =>
        string.Equals(x.GetProperty("invoiceNumber").GetString(), convertedNumber, StringComparison.Ordinal))
        .GetProperty("id").GetInt32();
    using (var directGeneratedSaleArchive = await client.DeleteAsync($"/api/sales/{generatedSaleId}"))
    {
        AssertStatus(HttpStatusCode.Conflict, directGeneratedSaleArchive.StatusCode, "generated invoice sale direct archive is blocked");
    }

    using (var archiveConvertedInvoice = await client.DeleteAsync($"/api/invoice-documents/{convertedId}"))
    {
        AssertStatus(HttpStatusCode.OK, archiveConvertedInvoice.StatusCode, "archive converted invoice through invoice alias");
    }
    using (var repeatArchiveConvertedInvoice = await client.DeleteAsync($"/api/invoice-documents/{convertedId}"))
    {
        AssertStatus(HttpStatusCode.OK, repeatArchiveConvertedInvoice.StatusCode, "repeated archive of converted invoice is idempotent");
    }
    var jobWhileConvertedInvoiceArchived = await GetJsonAsync(client, $"/api/customer-jobs/{estimateJobId}");
    AssertEqual("Quoted", jobWhileConvertedInvoiceArchived.GetProperty("status").GetString(), "archiving the only conversion returns the active estimate job to quoted");
    AssertTrue((await GetJsonAsync(client, $"/api/receivable-invoices/{generatedReceivableId}")).GetProperty("isArchived").GetBoolean(), "archiving converted invoice archives generated receivable");
    AssertTrue((await GetJsonAsync(client, $"/api/sales/{generatedSaleId}")).GetProperty("isArchived").GetBoolean(), "archiving converted invoice archives generated sale");

    await PostJsonAsync(client, $"/api/invoice-documents/{convertedId}/restore", new { });
    await PostJsonAsync(client, $"/api/invoice-documents/{convertedId}/restore", new { });
    var jobAfterConvertedInvoiceRestore = await GetJsonAsync(client, $"/api/customer-jobs/{estimateJobId}");
    AssertEqual("Invoiced", jobAfterConvertedInvoiceRestore.GetProperty("status").GetString(), "restoring conversion returns generated estimate job to invoiced");
    AssertTrue(!(await GetJsonAsync(client, $"/api/receivable-invoices/{generatedReceivableId}")).GetProperty("isArchived").GetBoolean(), "restoring converted invoice restores generated receivable");
    AssertTrue(!(await GetJsonAsync(client, $"/api/sales/{generatedSaleId}")).GetProperty("isArchived").GetBoolean(), "restoring converted invoice restores generated sale");

    var invoice = await PostJsonAsync(client, "/api/invoice-documents", NewDocumentPayload("INVOICE", "Paid", "INV-2099-WAF-0001", 30));
    var invoiceId = invoice.GetProperty("id").GetInt32();
    var invoiceAlias = await GetJsonAsync(client, $"/api/documents/{invoiceId}");
    AssertEqual("INV-2099-WAF-0001", invoiceAlias.GetProperty("docNumber").GetString(), "document aliases agree");

    using var staleSentResponse = await client.PutAsJsonAsync(
        $"/api/invoice-documents/{invoiceId}",
        NewDocumentPayload("INVOICE", "Sent", "INV-2099-WAF-0001", 30));
    AssertStatus(HttpStatusCode.OK, staleSentResponse.StatusCode, "sent invoice ignores stale paid amount");
    var staleSent = await ReadJsonAsync(staleSentResponse);
    AssertEqual("Sent", staleSent.GetProperty("status").GetString(), "sent invoice keeps sent status");
    AssertEqual("0", staleSent.GetProperty("amountPaid").GetDecimal().ToString(CultureInfo.InvariantCulture), "sent invoice resets amount paid");
    AssertEqual("30", staleSent.GetProperty("balance").GetDecimal().ToString(CultureInfo.InvariantCulture), "sent invoice restores full balance");

    var sentReceivables = await GetJsonArrayAsync(client, "/api/receivable-invoices?includeArchived=true");
    AssertTrue(sentReceivables.Any(x =>
        string.Equals(x.GetProperty("invoiceNumber").GetString(), "INV-2099-WAF-0001", StringComparison.Ordinal)
        && string.Equals(x.GetProperty("status").GetString(), "Sent", StringComparison.Ordinal)
        && x.GetProperty("amountPaid").GetDecimal() == 0m),
        "sent invoice should sync an unpaid AR row");

    var manualReceivable = await PostJsonAsync(client, "/api/receivable-invoices", ManualReceivablePayload(
        "WAF-MANUAL-AR-1",
        "manual-ar-original.pdf",
        "Paid",
        50));
    var manualReceivableId = manualReceivable.GetProperty("id").GetInt32();
    var generatedManualSales = (await GetJsonArrayAsync(client, "/api/sales?includeArchived=true"))
        .Where(item => item.TryGetProperty("sourceReceivableInvoiceId", out var sourceId)
            && sourceId.ValueKind == JsonValueKind.Number
            && sourceId.GetInt32() == manualReceivableId)
        .ToArray();
    AssertEqual("1", generatedManualSales.Length.ToString(CultureInfo.InvariantCulture), "paid manual receivable creates one structurally linked sale");
    var generatedManualSaleId = generatedManualSales[0].GetProperty("id").GetInt32();

    using (var renameManualReceivable = await client.PutAsJsonAsync(
        $"/api/receivable-invoices/{manualReceivableId}",
        ManualReceivablePayload("WAF-MANUAL-AR-RENAMED", "manual-ar-renamed.pdf", "Paid", 50)))
    {
        AssertStatus(HttpStatusCode.OK, renameManualReceivable.StatusCode, "rename paid manual receivable");
    }
    generatedManualSales = (await GetJsonArrayAsync(client, "/api/sales?includeArchived=true"))
        .Where(item => item.TryGetProperty("sourceReceivableInvoiceId", out var sourceId)
            && sourceId.ValueKind == JsonValueKind.Number
            && sourceId.GetInt32() == manualReceivableId)
        .ToArray();
    AssertEqual("1", generatedManualSales.Length.ToString(CultureInfo.InvariantCulture), "renaming manual receivable does not create a second sale");
    AssertEqual(generatedManualSaleId.ToString(CultureInfo.InvariantCulture), generatedManualSales[0].GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture), "renaming manual receivable preserves generated sale identity");
    AssertEqual("WAF-MANUAL-AR-RENAMED", generatedManualSales[0].GetProperty("invoiceNumber").GetString(), "renaming manual receivable rekeys generated sale");
    AssertEqual("manual-ar-renamed.pdf", generatedManualSales[0].GetProperty("sourceProof").GetString(), "renaming manual receivable repoints generated sale proof");

    using (var directManualSaleArchive = await client.DeleteAsync($"/api/sales/{generatedManualSaleId}"))
    {
        AssertStatus(HttpStatusCode.Conflict, directManualSaleArchive.StatusCode, "generated manual receivable sale direct archive is blocked");
    }
    using (var archiveManualReceivable = await client.DeleteAsync($"/api/receivable-invoices/{manualReceivableId}"))
    {
        AssertStatus(HttpStatusCode.NoContent, archiveManualReceivable.StatusCode, "archive manual receivable");
    }
    var saleAfterReceivableArchive = await GetJsonAsync(client, $"/api/sales/{generatedManualSaleId}");
    AssertTrue(saleAfterReceivableArchive.GetProperty("isArchived").GetBoolean(), "archiving manual receivable archives generated sale");

    await PostJsonAsync(client, $"/api/receivable-invoices/{manualReceivableId}/restore", new { });
    var saleAfterReceivableRestore = await GetJsonAsync(client, $"/api/sales/{generatedManualSaleId}");
    AssertTrue(!saleAfterReceivableRestore.GetProperty("isArchived").GetBoolean(), "restoring paid manual receivable restores generated sale");

    using (var markManualReceivableSent = await client.PutAsJsonAsync(
        $"/api/receivable-invoices/{manualReceivableId}",
        ManualReceivablePayload("WAF-MANUAL-AR-RENAMED", "manual-ar-renamed.pdf", "Sent", 50)))
    {
        AssertStatus(HttpStatusCode.OK, markManualReceivableSent.StatusCode, "mark manual receivable sent");
        var sentManualReceivable = await ReadJsonAsync(markManualReceivableSent);
        AssertEqual("0", sentManualReceivable.GetProperty("amountPaid").GetDecimal().ToString(CultureInfo.InvariantCulture), "sent manual receivable clears stale payment");
    }
    var saleAfterReceivableUnpaid = await GetJsonAsync(client, $"/api/sales/{generatedManualSaleId}");
    AssertTrue(saleAfterReceivableUnpaid.GetProperty("isArchived").GetBoolean(), "unpaying manual receivable archives generated sale");
    AssertTrue(!saleAfterReceivableUnpaid.GetProperty("includeInDashboard").GetBoolean(), "unpaying manual receivable removes generated sale from dashboard");

    const string legacyEstimateNumber = "EST-2099-WAF-LEGACY-LINK";
    const string legacyInvoiceNumber = "INV-2099-WAF-LEGACY-LINK";
    var legacyInvoice = await PostJsonAsync(
        client,
        "/api/invoice-documents",
        NewDocumentPayload("INVOICE", "Draft", legacyInvoiceNumber, 0, $"Converted from estimate {legacyEstimateNumber}."));
    var legacyEstimate = await PostJsonAsync(
        client,
        "/api/documents",
        NewDocumentPayload("ESTIMATE", "Accepted", legacyEstimateNumber, 0, $"Converted to invoice {legacyInvoiceNumber}."));
    var linkedLegacyConversion = await PostJsonAsync(
        client,
        $"/api/documents/{legacyEstimate.GetProperty("id").GetInt32()}/convert-to-invoice",
        new { });
    AssertEqual(
        legacyInvoice.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture),
        linkedLegacyConversion.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture),
        "legacy conversion markers are linked instead of creating another invoice");
    AssertEqual(
        legacyEstimate.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture),
        linkedLegacyConversion.GetProperty("sourceEstimateId").GetInt32().ToString(CultureInfo.InvariantCulture),
        "legacy conversion receives structural estimate link");
    var legacyEstimateJob = (await GetJsonArrayAsync(client, "/api/customer-jobs?includeArchived=true"))
        .Single(item => string.Equals(item.GetProperty("relatedInvoiceNumber").GetString(), legacyEstimateNumber, StringComparison.Ordinal));
    AssertEqual("Invoiced", legacyEstimateJob.GetProperty("status").GetString(), "linked legacy conversion advances generated estimate job");

    var duplicateThroughDocuments = await PostJsonAsync(client, $"/api/documents/{invoiceId}/duplicate", new { });
    var duplicateThroughInvoiceDocuments = await PostJsonAsync(client, $"/api/invoice-documents/{invoiceId}/duplicate", new { });
    AssertEqual("Draft", duplicateThroughDocuments.GetProperty("status").GetString(), "document alias duplicate starts draft");
    AssertEqual("Draft", duplicateThroughInvoiceDocuments.GetProperty("status").GetString(), "invoice-document alias duplicate starts draft");
    AssertTrue(
        duplicateThroughDocuments.GetProperty("id").GetInt32() != duplicateThroughInvoiceDocuments.GetProperty("id").GetInt32(),
        "explicit duplicate actions create separate records");

    var queueJob = await PostJsonAsync(client, "/api/customer-jobs", new
    {
        jobDate = "2026-06-22",
        customerName = "WAF Queue Customer",
        platform = "Direct",
        jobNumber = "WAF-QUEUE-JOB-1",
        jobName = "Queue state regression",
        jobType = "Print",
        status = "Open",
        paymentMethod = "Unknown / Review",
        sourceProof = "waf-user-created-queue-job",
        needsReview = false
    });
    var queueJobId = queueJob.GetProperty("id").GetInt32();
    var queueItem = await PostJsonAsync(client, "/api/printer-queue-items", new
    {
        queueDate = "2026-06-22",
        priority = "Normal",
        status = "Printing",
        printerName = "WAF Printer",
        customerJobId = queueJobId,
        customerName = "WAF Queue Customer",
        jobName = "Queue state regression",
        quantity = 1,
        plateCount = 1,
        progressPercent = 25,
        sourceProof = "waf-queue-state-proof",
        needsReview = false
    });
    var queueItemId = queueItem.GetProperty("id").GetInt32();
    AssertEqual("In Progress", (await GetJsonAsync(client, $"/api/customer-jobs/{queueJobId}")).GetProperty("status").GetString(), "printing queue item starts linked job");
    using (var archiveQueue = await client.DeleteAsync($"/api/printer-queue-items/{queueItemId}"))
    {
        AssertStatus(HttpStatusCode.NoContent, archiveQueue.StatusCode, "archive printer queue item");
    }
    using (var repeatArchiveQueue = await client.DeleteAsync($"/api/printer-queue-items/{queueItemId}"))
    {
        AssertStatus(HttpStatusCode.NoContent, repeatArchiveQueue.StatusCode, "repeated printer queue archive is idempotent");
    }
    AssertEqual("Open", (await GetJsonAsync(client, $"/api/customer-jobs/{queueJobId}")).GetProperty("status").GetString(), "archiving the only active queue item reopens linked job");
    await PostJsonAsync(client, $"/api/printer-queue-items/{queueItemId}/restore", new { });
    await PostJsonAsync(client, $"/api/printer-queue-items/{queueItemId}/restore", new { });
    AssertEqual("In Progress", (await GetJsonAsync(client, $"/api/customer-jobs/{queueJobId}")).GetProperty("status").GetString(), "restoring printing queue item restarts linked job");

    var movedQueueJob = await PostJsonAsync(client, "/api/customer-jobs", new
    {
        jobDate = "2026-06-23",
        customerName = "WAF Queue Customer Two",
        platform = "Direct",
        jobNumber = "WAF-QUEUE-JOB-2",
        jobName = "Queue reassignment regression",
        jobType = "Print",
        status = "Open",
        paymentMethod = "Unknown / Review",
        sourceProof = "waf-user-created-queue-job-two",
        needsReview = false
    });
    var movedQueueJobId = movedQueueJob.GetProperty("id").GetInt32();
    using (var moveQueueItem = await client.PutAsJsonAsync($"/api/printer-queue-items/{queueItemId}", new
    {
        queueDate = "2026-06-23",
        priority = "Normal",
        status = "Printing",
        printerName = "WAF Printer",
        customerJobId = movedQueueJobId,
        customerName = "WAF Queue Customer Two",
        jobName = "Queue reassignment regression",
        quantity = 1,
        plateCount = 1,
        progressPercent = 50,
        sourceProof = "waf-queue-state-proof",
        needsReview = false
    }))
    {
        AssertStatus(HttpStatusCode.OK, moveQueueItem.StatusCode, "move queue item to another job");
    }
    AssertEqual("Open", (await GetJsonAsync(client, $"/api/customer-jobs/{queueJobId}")).GetProperty("status").GetString(), "moving the only queue item reopens its old job");
    AssertEqual("In Progress", (await GetJsonAsync(client, $"/api/customer-jobs/{movedQueueJobId}")).GetProperty("status").GetString(), "moving a printing queue item starts its new job");
}

static async Task AssertAutomaticEtsyDocumentIntakeAsync(HttpClient client, string root)
{
    const string orderNumber = "990001001";
    const string refundedOrderNumber = "990001002";
    const string customerName = "Automation Test Customer";
    const string refundedCustomerName = "Refund Test Customer";
    var uploadedFileNames = new[]
    {
        "download-waf-2026-09-08.pdf",
        "download-waf-2026-09-08-copy.pdf",
        "download-waf-2026-09-09.pdf"
    };

    var paidOrderPdf = CreateMinimalTextPdf(
        "Etsy",
        "Ship to",
        customerName,
        "123 Example Street",
        "Testville, NJ 07001",
        "United States",
        "From",
        "EPATA Test Shop",
        "Sanitized Test Widget 2 x $12.00",
        "Color: Blue",
        "Size: Medium",
        "SKU: TEST-WIDGET-001",
        "Item total $24.00",
        "Shipping total $6.00",
        "Subtotal $30.00",
        "Tax $1.80",
        "Order total $31.80",
        $"Order #{orderNumber}",
        "Order date Sep 8, 2026",
        $"Buyer {customerName} (automation_test_user)",
        "Paid via Etsy Payments",
        "Scheduled to ship by Sep 10, 2026",
        "Tracking 9400111899223856928499");

    var refundedOrderPdf = CreateMinimalTextPdf(
        "Etsy",
        "Ship to",
        refundedCustomerName,
        "456 Sample Road",
        "Exampleton, NJ 07002",
        "United States",
        "From",
        "EPATA Test Shop",
        "Refunded Test Bracket 1 x $16.00",
        "Color: Red",
        "SKU: TEST-REFUND-001",
        "Item total $16.00",
        "Shipping total $4.00",
        "Subtotal $20.00",
        "Tax $1.40",
        "Refunded cost $21.40",
        "Order total $0.00",
        $"Order #{refundedOrderNumber}",
        "Order date Sep 9, 2026",
        $"Buyer {refundedCustomerName} (refund_test_user)",
        "Paid via Etsy Payments");

    try
    {
        var created = await UploadDocumentAsync(client, uploadedFileNames[0], paidOrderPdf);
        AssertEqual("1", created.GetProperty("marketplaceCreated").GetInt32().ToString(CultureInfo.InvariantCulture), "automatic Etsy import created count");
        AssertEqual("0", created.GetProperty("marketplaceLinked").GetInt32().ToString(CultureInfo.InvariantCulture), "automatic Etsy import linked count");
        var createdImport = created.GetProperty("marketplaceImports")[0];
        AssertTrue(createdImport.GetProperty("recognized").GetBoolean(), "generic PDF filename should be recognized from Etsy receipt content");
        AssertEqual("Created", createdImport.GetProperty("action").GetString(), "automatic Etsy import action");

        var createdSale = createdImport.GetProperty("sale");
        AssertEqual(orderNumber, createdSale.GetProperty("orderNumber").GetString(), "automatic Etsy Sale order number");
        AssertEqual("Etsy", createdSale.GetProperty("platform").GetString(), "automatic Etsy Sale platform");
        AssertEqual(customerName, createdSale.GetProperty("customerName").GetString(), "automatic Etsy Sale customer");
        AssertContains("Sanitized Test Widget", createdSale.GetProperty("productName").GetString(), "automatic Etsy Sale product");
        AssertEqual("TEST-WIDGET-001", createdSale.GetProperty("sku").GetString(), "automatic Etsy Sale SKU");
        AssertEqual("Blue", createdSale.GetProperty("color").GetString(), "automatic Etsy Sale color");
        AssertContains("Size: Medium", createdSale.GetProperty("variation").GetString(), "automatic Etsy Sale variation");
        AssertEqual("2", createdSale.GetProperty("quantity").GetDecimal().ToString(CultureInfo.InvariantCulture), "automatic Etsy Sale quantity");
        AssertEqual("24.00", createdSale.GetProperty("itemSales").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "automatic Etsy Sale item total");
        AssertEqual("6.00", createdSale.GetProperty("shippingCharged").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "automatic Etsy Sale shipping total");
        AssertEqual("1.80", createdSale.GetProperty("salesTaxCollected").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "automatic Etsy Sale tax");
        AssertEqual("31.80", createdSale.GetProperty("customerPaid").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "automatic Etsy Sale order total");
        AssertTrue(createdSale.GetProperty("needsReview").GetBoolean(), "automatic Etsy Sale should retain review for unknown fees and COGS");

        var createdDocument = created.GetProperty("documents")[0];
        AssertEqual("Etsy Order", createdDocument.GetProperty("documentType").GetString(), "content-aware Audit Doc classification");
        AssertEqual("Sale", createdDocument.GetProperty("relatedRecordType").GetString(), "automatic Etsy Audit Doc relation type");
        AssertEqual(orderNumber, createdDocument.GetProperty("relatedRecordNumber").GetString(), "automatic Etsy Audit Doc relation number");
        AssertTrue(!createdDocument.GetProperty("needsReview").GetBoolean(), "successfully linked Etsy proof should not need document review");

        var exactRetry = await UploadDocumentAsync(client, uploadedFileNames[0], paidOrderPdf);
        AssertEqual("0", exactRetry.GetProperty("createdCount").GetInt32().ToString(CultureInfo.InvariantCulture), "exact upload retry creates no Audit Doc");
        AssertEqual("1", exactRetry.GetProperty("duplicateUploadCount").GetInt32().ToString(CultureInfo.InvariantCulture), "exact upload retry is identified");
        AssertEqual(
            createdDocument.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture),
            exactRetry.GetProperty("documents")[0].GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture),
            "exact upload retry returns the original Audit Doc");

        var createdJob = createdImport.GetProperty("job");
        AssertEqual(orderNumber, createdJob.GetProperty("relatedOrderNumber").GetString(), "automatic Etsy Job relation");
        AssertEqual(customerName, createdJob.GetProperty("customerName").GetString(), "automatic Etsy Job customer");
        AssertEqual("Paid", createdJob.GetProperty("status").GetString(), "automatic Etsy Job status");
        var createdCustomer = createdImport.GetProperty("customer");
        AssertEqual(customerName, createdCustomer.GetProperty("name").GetString(), "automatic Etsy Customer name");
        AssertEqual("automation_test_user", createdCustomer.GetProperty("etsyUsername").GetString(), "automatic Etsy Customer username");
        AssertEqual("123 Example Street", createdCustomer.GetProperty("address1").GetString(), "automatic Etsy Customer address");

        var linked = await UploadDocumentAsync(client, uploadedFileNames[1], paidOrderPdf);
        AssertEqual("0", linked.GetProperty("marketplaceCreated").GetInt32().ToString(CultureInfo.InvariantCulture), "duplicate Etsy import created count");
        AssertEqual("1", linked.GetProperty("marketplaceLinked").GetInt32().ToString(CultureInfo.InvariantCulture), "duplicate Etsy import linked count");
        AssertEqual("LinkedExisting", linked.GetProperty("marketplaceImports")[0].GetProperty("action").GetString(), "duplicate Etsy import action");

        var paidOrderSales = (await GetJsonArrayAsync(client, "/api/sales?includeArchived=true"))
            .Where(x => string.Equals(x.GetProperty("orderNumber").GetString(), orderNumber, StringComparison.Ordinal))
            .ToArray();
        var paidOrderJobs = (await GetJsonArrayAsync(client, "/api/customer-jobs?includeArchived=true"))
            .Where(x => string.Equals(x.GetProperty("relatedOrderNumber").GetString(), orderNumber, StringComparison.Ordinal))
            .ToArray();
        var paidOrderCustomers = (await GetJsonArrayAsync(client, "/api/parties?includeArchived=true"))
            .Where(x => string.Equals(x.GetProperty("etsyUsername").GetString(), "automation_test_user", StringComparison.Ordinal))
            .ToArray();
        var paidOrderDocuments = (await GetJsonArrayAsync(client, "/api/audit-documents?includeArchived=true"))
            .Where(x => string.Equals(x.GetProperty("relatedRecordNumber").GetString(), orderNumber, StringComparison.Ordinal))
            .ToArray();
        AssertEqual("1", paidOrderSales.Length.ToString(CultureInfo.InvariantCulture), "same-order upload should not duplicate Sale");
        AssertEqual("1", paidOrderJobs.Length.ToString(CultureInfo.InvariantCulture), "same-order upload should not duplicate Job");
        AssertEqual("1", paidOrderCustomers.Length.ToString(CultureInfo.InvariantCulture), "same-order upload should not duplicate Customer");
        AssertEqual("2", paidOrderDocuments.Length.ToString(CultureInfo.InvariantCulture), "same-order upload should keep and link both proof documents");
        AssertTrue(paidOrderDocuments.All(x =>
            string.Equals(x.GetProperty("documentType").GetString(), "Etsy Order", StringComparison.Ordinal)
            && string.Equals(x.GetProperty("relatedRecordType").GetString(), "Sale", StringComparison.Ordinal)
            && !x.GetProperty("needsReview").GetBoolean()),
            "same-order proof documents should both be classified and linked");

        var refunded = await UploadDocumentAsync(client, uploadedFileNames[2], refundedOrderPdf);
        AssertEqual("1", refunded.GetProperty("marketplaceCreated").GetInt32().ToString(CultureInfo.InvariantCulture), "fully refunded Etsy import created count");
        var refundedImport = refunded.GetProperty("marketplaceImports")[0];
        AssertEqual("Created", refundedImport.GetProperty("action").GetString(), "fully refunded Etsy import action");
        var refundedSale = refundedImport.GetProperty("sale");
        AssertEqual(refundedOrderNumber, refundedSale.GetProperty("orderNumber").GetString(), "fully refunded Etsy Sale order number");
        AssertEqual("Refunded", refundedSale.GetProperty("status").GetString(), "fully refunded Etsy Sale status");
        AssertEqual("16.00", refundedSale.GetProperty("itemSales").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "fully refunded Etsy Sale item total");
        AssertEqual("4.00", refundedSale.GetProperty("shippingCharged").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "fully refunded Etsy Sale shipping total");
        AssertEqual("20.00", refundedSale.GetProperty("refunds").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "fully refunded Etsy Sale business refund");
        AssertEqual("0.00", refundedSale.GetProperty("salesTaxCollected").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "fully refunded Etsy Sale net tax");
        AssertEqual("0.00", refundedSale.GetProperty("customerPaid").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "fully refunded Etsy Sale net customer paid");
        var netGrossReceipts = refundedSale.GetProperty("itemSales").GetDecimal()
            + refundedSale.GetProperty("shippingCharged").GetDecimal()
            - refundedSale.GetProperty("refunds").GetDecimal()
            + refundedSale.GetProperty("salesTaxCollected").GetDecimal();
        AssertEqual("0.00", netGrossReceipts.ToString("0.00", CultureInfo.InvariantCulture), "fully refunded Etsy Sale net gross receipts");
        AssertEqual("Completed", refundedImport.GetProperty("job").GetProperty("status").GetString(), "fully refunded Etsy Job status");
        AssertEqual("0.00", refundedImport.GetProperty("job").GetProperty("amountPaid").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "fully refunded Etsy Job amount paid");

        var refundedDocuments = (await GetJsonArrayAsync(client, "/api/audit-documents?includeArchived=true"))
            .Where(x => string.Equals(x.GetProperty("relatedRecordNumber").GetString(), refundedOrderNumber, StringComparison.Ordinal))
            .ToArray();
        AssertEqual("1", refundedDocuments.Length.ToString(CultureInfo.InvariantCulture), "fully refunded Etsy proof count");
        AssertTrue(!refundedDocuments[0].GetProperty("needsReview").GetBoolean(), "fully refunded Etsy proof should be linked without document review");

        var paidSaleId = paidOrderSales.Single().GetProperty("id").GetInt32();
        using (var archivePaidSale = await client.DeleteAsync($"/api/sales/{paidSaleId}"))
        {
            AssertStatus(HttpStatusCode.NoContent, archivePaidSale.StatusCode, "archive Etsy Sale before reprocess regression");
        }
        using (var reprocess = await client.PostAsJsonAsync("/api/documents/reprocess-marketplace-orders", new { }))
        {
            AssertStatus(HttpStatusCode.OK, reprocess.StatusCode, "reprocess documents with archived Etsy Sale");
        }
        paidOrderSales = (await GetJsonArrayAsync(client, "/api/sales?includeArchived=true"))
            .Where(x => string.Equals(x.GetProperty("orderNumber").GetString(), orderNumber, StringComparison.Ordinal))
            .ToArray();
        AssertEqual("1", paidOrderSales.Length.ToString(CultureInfo.InvariantCulture), "reprocessing archived Etsy order creates no replacement Sale");
        AssertTrue(paidOrderSales[0].GetProperty("isArchived").GetBoolean(), "reprocessing leaves archived Etsy Sale archived");
        var paidProofAfterReprocess = await GetJsonAsync(client, $"/api/audit-documents/{createdDocument.GetProperty("id").GetInt32()}");
        AssertTrue(paidProofAfterReprocess.GetProperty("needsReview").GetBoolean(), "archived Etsy match is surfaced for review");
        AssertContains("matches archived Sale", paidProofAfterReprocess.GetProperty("notes").GetString(), "archived Etsy reprocess note");
    }
    finally
    {
        await DeleteUploadedTestProofsAsync(client, root, uploadedFileNames);
    }
}

static async Task AssertAutomaticInvoiceDocumentIntakeAsync(HttpClient client, string root)
{
    const string fileName = "EPATA_EST-2099-0014_Sample_Window_Parts.pdf";
    const string duplicateFileName = "EPATA_EST-2099-0014_Sample_Window_Parts-copy.pdf";
    var pdf = CreateMinimalTextPdf(
        "EPATA 3D PRINTS",
        "ESTIMATE",
        "Estimate # EST-2099-0014",
        "Date July 7, 2099",
        "Valid Until July 21, 2099",
        "Prepared For Sample Customer",
        "SENT",
        "BILL TO PROJECT DETAILS PRICING SUMMARY",
        "Sample Fabrication LLC",
        "Sample Customer",
        "(555) 010-1234",
        "customer@example.test",
        "Project Name: Sample Window Replacement Parts",
        "Description: Custom reverse-engineered replacement window parts based on customer-provided sample/photos.",
        "Material: Creality ASA",
        "Color: Black",
        "Infill: Functional settings TBD after prototype",
        "Subtotal $1,300.00",
        "Discount $0.00",
        "Rush Fee (0%) $0.00",
        "Tax (0%) $0.00",
        "ESTIMATED TOTAL $1,300.00",
        "ESTIMATE BREAKDOWN",
        "# DESCRIPTION CALCULATION / DETAILS QTY RATE AMOUNT",
        "1 Reverse engineering / CAD Measure and recreate geometry from sample/photos. 1 $325.00 $325.00",
        "2 Prototype set + fit adjustment One prototype set and one minor adjustment after test fit. 1 $150.00 $150.00",
        "3 Creality ASA material / consumables Flat material allowance plus brims and waste. 1 $125.00 $125.00",
        "4 Batch production time Estimated 50 machine hours. 50 $10.00 $500.00",
        "5 Cleanup / inspection / sorting Cleanup, inspection, sorting, and packaging for 72 pieces. 7 $25.00 $175.00",
        "6 Batch setup / handling buffer Slicing/layout and production management. 1 $25.00 $25.00",
        "PRICING GUIDE (FOR REFERENCE) TERMS & NOTES APPROVAL",
        "Print-Only Jobs - $15 minimum, or setup + material + machine time",
        "Basic Modeling - $25/hour (1 hour minimum)",
        "Material Notes",
        "This estimate is valid for 14 days from the date above.",
        "Final production begins only after prototype approval.",
        "Estimate sent - pending customer approval.",
        "TURNAROUND TIME (ESTIMATED)",
        "Standard: Estimated timeline provided after prototype approval",
        "Rush: Expedited service available upon request, subject to workload",
        "FOLLOW & CONNECT");

    try
    {
        var created = await UploadDocumentAsync(client, fileName, pdf, "Invoice");
        AssertEqual("1", created.GetProperty("invoiceDocumentCreated").GetInt32().ToString(CultureInfo.InvariantCulture), "automatic estimate/invoice intake created count");
        var intake = created.GetProperty("invoiceDocumentImports")[0];
        AssertEqual("Created", intake.GetProperty("action").GetString(), "automatic estimate/invoice intake action");
        var document = intake.GetProperty("invoiceDocument");
        AssertEqual("ESTIMATE", document.GetProperty("docType").GetString(), "PDF identity should override an incorrect Invoice routing selection");
        AssertEqual("EST-2099-0014", document.GetProperty("docNumber").GetString(), "mapped estimate number");
        AssertEqual("Sent", document.GetProperty("status").GetString(), "mapped source status");
        AssertEqual("Sample Fabrication LLC", document.GetProperty("customerName").GetString(), "mapped Bill To company");
        AssertEqual("Sample Customer", document.GetProperty("preparedFor").GetString(), "mapped Prepared For contact");
        AssertEqual("(555) 010-1234", document.GetProperty("customerPhone").GetString(), "mapped customer phone");
        AssertEqual("customer@example.test", document.GetProperty("customerEmail").GetString(), "mapped customer email");
        AssertTrue(document.GetProperty("customerAddress").ValueKind is JsonValueKind.Null, "company text must not be stored as a postal address");
        AssertEqual("Sample Window Replacement Parts", document.GetProperty("projectName").GetString(), "mapped project name");
        AssertEqual("Creality ASA", document.GetProperty("material").GetString(), "mapped material");
        AssertEqual("Black", document.GetProperty("color").GetString(), "mapped color");
        AssertEqual("Functional settings TBD after prototype", document.GetProperty("infill").GetString(), "infill must stop before pricing fields");
        AssertEqual("2099-07-07", document.GetProperty("docDate").GetString(), "mapped document date");
        AssertEqual("2099-07-21", document.GetProperty("dueDate").GetString(), "mapped valid-until date");
        AssertEqual("1300.00", document.GetProperty("subtotal").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "mapped subtotal");
        AssertEqual("1300.00", document.GetProperty("total").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "mapped total");
        AssertEqual("6", document.GetProperty("lineItems").GetArrayLength().ToString(CultureInfo.InvariantCulture), "all source line items should be mapped");
        AssertEqual("Reverse engineering / CAD", document.GetProperty("lineItems")[0].GetProperty("description").GetString(), "mapped first line description");
        AssertContains("Measure and recreate geometry", document.GetProperty("lineItems")[0].GetProperty("details").GetString(), "mapped first line calculation details");
        AssertEqual("Batch setup / handling buffer", document.GetProperty("lineItems")[5].GetProperty("description").GetString(), "mapped final line description");
        AssertContains("Slicing/layout", document.GetProperty("lineItems")[5].GetProperty("details").GetString(), "mapped final line calculation details");
        AssertEqual("7", document.GetProperty("lineItems")[4].GetProperty("quantity").GetDecimal().ToString(CultureInfo.InvariantCulture), "mapped cleanup quantity");
        AssertContains("Print-Only Jobs", document.GetProperty("pricingGuide").GetString(), "mapped pricing guide");
        AssertContains("prototype approval", document.GetProperty("termsNotes").GetString(), "mapped terms");
        AssertContains("prototype approval", document.GetProperty("standardTurnaround").GetString(), "mapped standard turnaround");

        var audit = intake.GetProperty("auditDocument");
        AssertEqual("Estimate", audit.GetProperty("documentType").GetString(), "Audit Doc corrected to source type");
        AssertEqual("Estimate", audit.GetProperty("relatedRecordType").GetString(), "Audit Doc linked type");
        AssertEqual("EST-2099-0014", audit.GetProperty("relatedRecordNumber").GetString(), "Audit Doc linked number");
        AssertTrue(!audit.GetProperty("needsReview").GetBoolean(), "successfully mapped proof should not remain unlinked");

        var duplicate = await UploadDocumentAsync(client, duplicateFileName, pdf, "Invoice");
        AssertEqual("0", duplicate.GetProperty("invoiceDocumentCreated").GetInt32().ToString(CultureInfo.InvariantCulture), "same estimate should not create another builder record");
        AssertEqual("1", duplicate.GetProperty("invoiceDocumentLinked").GetInt32().ToString(CultureInfo.InvariantCulture), "same estimate proof should link existing record");
        AssertEqual("LinkedExisting", duplicate.GetProperty("invoiceDocumentImports")[0].GetProperty("action").GetString(), "duplicate estimate intake action");

        var matches = (await GetJsonArrayAsync(client, "/api/invoice-documents?includeArchived=true"))
            .Where(item => string.Equals(item.GetProperty("docNumber").GetString(), "EST-2099-0014", StringComparison.Ordinal))
            .ToArray();
        AssertEqual("1", matches.Length.ToString(CultureInfo.InvariantCulture), "duplicate upload keeps one estimate record");
    }
    finally
    {
        await DeleteUploadedTestProofsAsync(client, root, [fileName, duplicateFileName]);
    }
}

static async Task AssertAutomaticInvoiceDocumentAccountingSafetyAsync(HttpClient client, string root)
{
    const string collisionNumber = "INV-2099-8015";
    const string collisionFileName = "EPATA_INV-2099-8015_Existing_Ledger_Collision.pdf";
    const string paidNumber = "INV-2099-8016";
    const string paidFileName = "EPATA_INV-2099-8016_Paid_Source.pdf";
    var collisionPdf = CreateMinimalTextPdf(
        "EPATA 3D PRINTS",
        "INVOICE",
        $"Invoice # {collisionNumber}",
        "Date August 1, 2099",
        "Due Date August 15, 2099",
        "Prepared For Existing Ledger Customer",
        "SENT",
        "BILL TO PROJECT DETAILS PRICING SUMMARY",
        "Existing Ledger Fabrication LLC",
        "Existing Ledger Customer",
        "existing-ledger@example.test",
        "Project Name: Existing Ledger Collision Fixture",
        "Description: This source must not create duplicate accounting rows.",
        "Material: ASA",
        "Color: Black",
        "Subtotal $88.00",
        "Tax (0%) $0.00",
        "INVOICE TOTAL $88.00",
        "INVOICE BREAKDOWN",
        "# DESCRIPTION CALCULATION / DETAILS QTY RATE AMOUNT",
        "1 Collision fixture service - Existing manually entered accounting row. 1 $88.00 $88.00",
        "TERMS & NOTES",
        "Payment due after delivery.");
    var paidPdf = CreateMinimalTextPdf(
        "EPATA 3D PRINTS",
        "INVOICE",
        $"Invoice # {paidNumber}",
        "Date August 2, 2099",
        "Due Date August 16, 2099",
        "Prepared For Paid Source Customer",
        "PAID",
        "BILL TO PROJECT DETAILS PRICING SUMMARY",
        "Paid Source Fabrication LLC",
        "Paid Source Customer",
        "paid-source@example.test",
        "Project Name: Paid Source Safety Fixture",
        "Description: Payment evidence from an uploaded historical PDF requires confirmation.",
        "Material: PETG",
        "Color: Blue",
        "Subtotal $240.00",
        "Tax (0%) $0.00",
        "Amount Paid $240.00",
        "INVOICE TOTAL $240.00",
        "INVOICE BREAKDOWN",
        "# DESCRIPTION CALCULATION / DETAILS QTY RATE AMOUNT",
        "1 Paid source fixture service - Populate the invoice without automatically recognizing cash. 1 $240.00 $240.00",
        "TERMS & NOTES",
        "Paid source status must be confirmed in the ledger.");

    try
    {
        await PostJsonAsync(client, "/api/receivable-invoices", new
        {
            invoiceNumber = collisionNumber,
            invoiceDate = "2099-08-01",
            dueDate = "2099-08-15",
            customerName = "Existing Ledger Fabrication LLC",
            projectName = "Existing Ledger Collision Fixture",
            status = "Sent",
            subtotal = 88,
            discount = 0,
            rushFee = 0,
            salesTax = 0,
            invoiceTotal = 88,
            amountPaid = 0,
            paymentMethod = "Unknown / Review",
            includeInCashReports = false,
            needsReview = false,
            sourceProof = "manual-existing-ledger-ar.pdf"
        });
        await PostJsonAsync(client, "/api/sales", new
        {
            saleDate = "2099-08-01",
            platform = "Direct",
            paymentMethod = "Cash",
            salesTaxHandling = "Seller collected and remitted",
            invoiceNumber = collisionNumber,
            customerName = "Existing Ledger Fabrication LLC",
            productName = "Existing Ledger Collision Fixture",
            quantity = 1,
            itemSales = 88,
            shippingCharged = 0,
            salesTaxCollected = 0,
            customerPaid = 88,
            status = "Paid",
            includeInDashboard = true,
            needsReview = false,
            sourceProof = "manual-existing-ledger-sale.pdf"
        });

        var collision = await UploadDocumentAsync(client, collisionFileName, collisionPdf, "Invoice");
        AssertEqual("0", collision.GetProperty("invoiceDocumentCreated").GetInt32().ToString(CultureInfo.InvariantCulture), "existing accounting collision must not create an InvoiceDocument");
        AssertEqual("1", collision.GetProperty("invoiceDocumentNeedsReview").GetInt32().ToString(CultureInfo.InvariantCulture), "existing accounting collision needs review count");
        var collisionImport = collision.GetProperty("invoiceDocumentImports")[0];
        AssertEqual("NeedsReview", collisionImport.GetProperty("action").GetString(), "existing accounting collision import action");
        AssertTrue(collisionImport.GetProperty("invoiceDocument").ValueKind is JsonValueKind.Null, "existing accounting collision must not return a newly created InvoiceDocument");
        var collisionAudit = collisionImport.GetProperty("auditDocument");
        AssertTrue(collisionAudit.GetProperty("needsReview").GetBoolean(), "existing accounting collision proof must remain in Needs Review");
        AssertEqual("Invoice", collisionAudit.GetProperty("relatedRecordType").GetString(), "existing accounting collision related type");
        AssertEqual(collisionNumber, collisionAudit.GetProperty("relatedRecordNumber").GetString(), "existing accounting collision related number");

        var collisionDocs = (await GetJsonArrayAsync(client, "/api/invoice-documents?includeArchived=true"))
            .Where(item => string.Equals(item.GetProperty("docNumber").GetString(), collisionNumber, StringComparison.Ordinal)
                && item.TryGetProperty("sourceKind", out var sourceKind)
                && string.Equals(sourceKind.GetString(), "document", StringComparison.Ordinal))
            .ToArray();
        var collisionReceivables = (await GetJsonArrayAsync(client, "/api/receivable-invoices?includeArchived=true"))
            .Where(item => string.Equals(item.GetProperty("invoiceNumber").GetString(), collisionNumber, StringComparison.Ordinal))
            .ToArray();
        var collisionSales = (await GetJsonArrayAsync(client, "/api/sales?includeArchived=true"))
            .Where(item => string.Equals(item.GetProperty("invoiceNumber").GetString(), collisionNumber, StringComparison.Ordinal))
            .ToArray();
        AssertEqual("0", collisionDocs.Length.ToString(CultureInfo.InvariantCulture), "existing accounting collision keeps builder record count unchanged");
        AssertEqual("1", collisionReceivables.Length.ToString(CultureInfo.InvariantCulture), "existing accounting collision keeps one AR row");
        AssertEqual("1", collisionSales.Length.ToString(CultureInfo.InvariantCulture), "existing accounting collision keeps one Sale row");

        var paid = await UploadDocumentAsync(client, paidFileName, paidPdf, "Invoice");
        AssertEqual("1", paid.GetProperty("invoiceDocumentCreated").GetInt32().ToString(CultureInfo.InvariantCulture), "paid source safety import created count");
        var paidImport = paid.GetProperty("invoiceDocumentImports")[0];
        AssertEqual("Created", paidImport.GetProperty("action").GetString(), "paid source safety import action");
        var paidDocument = paidImport.GetProperty("invoiceDocument");
        AssertEqual("INVOICE", paidDocument.GetProperty("docType").GetString(), "paid source safety document type");
        AssertEqual(paidNumber, paidDocument.GetProperty("docNumber").GetString(), "paid source safety document number");
        AssertEqual("Sent", paidDocument.GetProperty("status").GetString(), "paid source must require confirmation before recognizing payment");
        AssertEqual("0.00", paidDocument.GetProperty("amountPaid").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "paid source must not automatically recognize cash");
        AssertEqual("240.00", paidDocument.GetProperty("balance").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "paid source safe imported balance");
        AssertEqual("Paid Source Fabrication LLC", paidDocument.GetProperty("customerName").GetString(), "paid source mapped customer");
        AssertEqual("Paid Source Safety Fixture", paidDocument.GetProperty("projectName").GetString(), "paid source mapped project");
        AssertEqual("1", paidDocument.GetProperty("lineItems").GetArrayLength().ToString(CultureInfo.InvariantCulture), "paid source mapped line item count");
        AssertEqual("240.00", paidDocument.GetProperty("total").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "paid source mapped total");

        var paidReceivables = (await GetJsonArrayAsync(client, "/api/receivable-invoices?includeArchived=true"))
            .Where(item => string.Equals(item.GetProperty("invoiceNumber").GetString(), paidNumber, StringComparison.Ordinal))
            .ToArray();
        var paidSales = (await GetJsonArrayAsync(client, "/api/sales?includeArchived=true"))
            .Where(item => string.Equals(item.GetProperty("invoiceNumber").GetString(), paidNumber, StringComparison.Ordinal))
            .ToArray();
        AssertEqual("1", paidReceivables.Length.ToString(CultureInfo.InvariantCulture), "paid source creates one unpaid AR row");
        AssertEqual("Sent", paidReceivables[0].GetProperty("status").GetString(), "paid source AR status requires confirmation");
        AssertEqual("0.00", paidReceivables[0].GetProperty("amountPaid").GetDecimal().ToString("0.00", CultureInfo.InvariantCulture), "paid source AR does not recognize cash");
        AssertTrue(paidReceivables[0].GetProperty("needsReview").GetBoolean(), "paid source AR remains in Needs Review until payment is confirmed");
        AssertTrue(!paidReceivables[0].GetProperty("includeInCashReports").GetBoolean(), "paid source AR is excluded from cash reports");
        AssertEqual("0", paidSales.Length.ToString(CultureInfo.InvariantCulture), "paid source must not create a Sale before payment confirmation");
    }
    finally
    {
        await DeleteUploadedTestProofsAsync(client, root, [collisionFileName, paidFileName]);
    }
}

static async Task AssertSafeRouteSmokeMatrixAsync(HttpClient client)
{
    foreach (var path in new[]
    {
        "/api/documents/next-number?type=ESTIMATE",
        "/api/documents/next-number?type=INVOICE",
        "/api/invoice-documents/next-number?type=ESTIMATE",
        "/api/invoice-documents/next-number?type=INVOICE"
    })
    {
        var nextNumber = await GetJsonAsync(client, path);
        AssertTrue(!string.IsNullOrWhiteSpace(nextNumber.GetProperty("number").GetString()), $"{path} returns a document number");
    }

    var latestDocument = await GetJsonAsync(client, "/api/documents/latest");
    var latestInvoiceDocument = await GetJsonAsync(client, "/api/invoice-documents/latest");
    AssertEqual(
        latestDocument.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture),
        latestInvoiceDocument.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture),
        "latest document aliases agree");

    var calendarGenerationResponses = await Task.WhenAll(
        client.PostAsJsonAsync("/api/tax-calendar/generate?year=2098", new { }),
        client.PostAsJsonAsync("/api/tax-calendar/generate?year=2098", new { }));
    try
    {
        var generatedCounts = new List<int>();
        foreach (var response in calendarGenerationResponses)
        {
            AssertStatus(HttpStatusCode.OK, response.StatusCode, "concurrent tax calendar generation");
            generatedCounts.Add((await ReadJsonAsync(response)).GetArrayLength());
        }
        AssertTrue(generatedCounts.All(count => count > 0), "tax calendar generation creates obligations");
        AssertEqual(generatedCounts[0].ToString(CultureInfo.InvariantCulture), generatedCounts[1].ToString(CultureInfo.InvariantCulture), "concurrent tax calendar generation is idempotent");
    }
    finally
    {
        foreach (var response in calendarGenerationResponses)
        {
            response.Dispose();
        }
    }
    var generatedCalendar = await GetJsonArrayAsync(client, "/api/tax-calendar?year=2098");
    var distinctCalendarKeys = generatedCalendar
        .Select(item => $"{item.GetProperty("title").GetString()}|{item.GetProperty("period").GetString()}")
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();
    AssertEqual(generatedCalendar.Length.ToString(CultureInfo.InvariantCulture), distinctCalendarKeys.ToString(CultureInfo.InvariantCulture), "generated tax calendar has no duplicate obligations");

    var concurrentActionSyncResponses = await Task.WhenAll(
        client.PostAsJsonAsync("/api/action-items/sync-findings", new { }),
        client.PostAsJsonAsync("/api/action-items/sync-findings", new { }));
    try
    {
        var createdCounts = new List<int>();
        foreach (var response in concurrentActionSyncResponses)
        {
            AssertStatus(HttpStatusCode.OK, response.StatusCode, "concurrent finding sync");
            createdCounts.Add((await ReadJsonAsync(response)).GetProperty("createdCount").GetInt32());
        }
        AssertTrue(createdCounts.Count(count => count > 0) <= 1, "concurrent finding sync has only one creating request");
    }
    finally
    {
        foreach (var response in concurrentActionSyncResponses)
        {
            response.Dispose();
        }
    }
    var repeatedActionSync = await PostJsonAsync(client, "/api/action-items/sync-findings", new { });
    AssertEqual("0", repeatedActionSync.GetProperty("createdCount").GetInt32().ToString(CultureInfo.InvariantCulture), "repeated finding sync does not duplicate open actions");

    using (var invalidLocalAiSettings = await client.PutAsJsonAsync("/api/ai/local/settings", new
    {
        baseUrl = "http://example.com:1234",
        modelPath = (string?)null,
        modelIdentifier = "waf-local",
        contextLength = 8192,
        idleUnloadSeconds = 1800
    }))
    {
        AssertStatus(HttpStatusCode.BadRequest, invalidLocalAiSettings.StatusCode, "local AI settings reject non-loopback URL");
    }

    using (var invalidLegacyImport = await client.PostAsJsonAsync("/api/import/invoice-app", new { baseUrl = "http://example.com:5057" }))
    {
        AssertStatus(HttpStatusCode.BadRequest, invalidLegacyImport.StatusCode, "legacy import rejects non-loopback URL");
    }
    using (var missingAuditFile = await client.GetAsync("/api/audit-documents/2147483647/file"))
    {
        AssertStatus(HttpStatusCode.NotFound, missingAuditFile.StatusCode, "missing audit proof file");
    }
    using (var missingAuditImport = await client.PostAsJsonAsync(
        "/api/audit-documents/2147483647/import-invoice-document",
        new { targetType = "Invoice" }))
    {
        AssertStatus(HttpStatusCode.NotFound, missingAuditImport.StatusCode, "missing audit proof import");
    }
}

static async Task AssertExportsAndSafetyAsync(HttpClient client, string root)
{
    foreach (var path in new[]
    {
        "/api/export/sales?includeArchived=true",
        "/api/export/tax-sales?paymentGroup=all&year=2026",
        "/api/export/tax-summary?year=2026",
        "/api/export/schedule-c-lines?year=2026",
        "/api/export/nj-sales-tax?year=2026"
    })
    {
        using var response = await client.GetAsync(path);
        AssertStatus(HttpStatusCode.OK, response.StatusCode, path);
        var csv = await response.Content.ReadAsStringAsync();
        AssertTrue(csv.Contains(",", StringComparison.Ordinal), $"{path} should look like CSV");
    }

    using (var taxPackage = await client.GetAsync("/api/export/tax-package?year=2026"))
    {
        AssertStatus(HttpStatusCode.OK, taxPackage.StatusCode, "tax package export");
        var bytes = await taxPackage.Content.ReadAsByteArrayAsync();
        AssertTrue(bytes.Length > 4 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K', "tax package should be a ZIP archive");
    }

    await AssertBackupEndpointAsync(client, root, "/api/database/backup", usePost: false);
    await AssertBackupEndpointAsync(client, root, "/api/system/backup", usePost: true);

    using (var clear = await client.PostAsync("/api/database/clear", null))
    {
        AssertStatus(HttpStatusCode.BadRequest, clear.StatusCode, "database clear stays blocked");
    }

    using (var import = await client.PostAsync("/api/database/import", null))
    {
        AssertStatus(HttpStatusCode.BadRequest, import.StatusCode, "database import stays blocked");
    }
}

static async Task AssertBackendMutationSafetyAsync(HttpClient client, HttpClient concurrentClient)
{
    var party = await PostJsonAsync(client, "/api/parties", new
    {
        name = "WAF optimistic concurrency original",
        partyType = "Customer"
    });
    var partyId = party.GetProperty("id").GetInt32();
    var originalUpdatedAt = party.GetProperty("updatedAtUtc").GetDateTime();

    using (var firstUpdate = await PutJsonWithExpectedUpdatedAtAsync(
        client,
        $"/api/parties/{partyId}",
        new { name = "WAF optimistic concurrency winner", partyType = "Customer" },
        originalUpdatedAt))
    {
        AssertStatus(HttpStatusCode.OK, firstUpdate.StatusCode, "fresh generic PUT");
    }

    using (var staleUpdate = await PutJsonWithExpectedUpdatedAtAsync(
        client,
        $"/api/parties/{partyId}",
        new { name = "WAF stale overwrite", partyType = "Customer" },
        originalUpdatedAt))
    {
        AssertStatus(HttpStatusCode.Conflict, staleUpdate.StatusCode, "stale generic PUT");
        AssertContains("No fields were overwritten", await staleUpdate.Content.ReadAsStringAsync(), "stale generic PUT message");
    }
    AssertEqual(
        "WAF optimistic concurrency winner",
        (await GetJsonAsync(client, $"/api/parties/{partyId}")).GetProperty("name").GetString(),
        "stale generic PUT preserves newer record");

    var concurrentParty = await PostJsonAsync(client, "/api/parties", new
    {
        name = "WAF concurrent optimistic fixture",
        partyType = "Customer"
    });
    var concurrentPartyId = concurrentParty.GetProperty("id").GetInt32();
    var concurrentPartyUpdatedAt = concurrentParty.GetProperty("updatedAtUtc").GetDateTime();
    var genericBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var concurrentGenericWrites = new[]
    {
        PutJsonAfterBarrierAsync(client, genericBarrier.Task, $"/api/parties/{concurrentPartyId}", new { name = "WAF concurrent generic A", partyType = "Customer" }, concurrentPartyUpdatedAt),
        PutJsonAfterBarrierAsync(concurrentClient, genericBarrier.Task, $"/api/parties/{concurrentPartyId}", new { name = "WAF concurrent generic B", partyType = "Customer" }, concurrentPartyUpdatedAt)
    };
    genericBarrier.SetResult(true);
    var concurrentGenericResponses = await Task.WhenAll(concurrentGenericWrites);
    try
    {
        AssertEqual("1", concurrentGenericResponses.Count(response => response.StatusCode == HttpStatusCode.OK).ToString(CultureInfo.InvariantCulture), "two-client generic PUT has one winner");
        AssertEqual("1", concurrentGenericResponses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ToString(CultureInfo.InvariantCulture), "two-client generic PUT has one stale loser");
        var persistedName = (await GetJsonAsync(client, $"/api/parties/{concurrentPartyId}")).GetProperty("name").GetString();
        AssertTrue(persistedName is "WAF concurrent generic A" or "WAF concurrent generic B", "two-client generic PUT persists the winner");
    }
    finally
    {
        foreach (var response in concurrentGenericResponses) response.Dispose();
    }

    const string invoiceConcurrencyNumber = "EST-2099-9901";
    var invoiceConcurrencyPayload = NewDocumentPayload("ESTIMATE", "Draft", invoiceConcurrencyNumber, 0);
    var invoiceConcurrencyRecord = await PostJsonAsync(client, "/api/documents", invoiceConcurrencyPayload);
    var originalInvoiceUpdatedAt = invoiceConcurrencyRecord.GetProperty("updatedAt").GetString()!;
    var winningInvoicePayload = NewDocumentPayload("ESTIMATE", "Draft", invoiceConcurrencyNumber, 0, "WAF invoice concurrency winner");
    using (var freshInvoiceUpdate = await PutJsonWithExpectedUpdatedAtAsync(
        client,
        $"/api/documents/{invoiceConcurrencyRecord.GetProperty("id").GetInt32()}",
        winningInvoicePayload,
        originalInvoiceUpdatedAt))
    {
        AssertStatus(HttpStatusCode.OK, freshInvoiceUpdate.StatusCode, "fresh Invoice Record PUT");
    }
    using (var staleInvoiceUpdate = await PutJsonWithExpectedUpdatedAtAsync(
        client,
        $"/api/documents/{invoiceConcurrencyRecord.GetProperty("id").GetInt32()}",
        NewDocumentPayload("ESTIMATE", "Draft", invoiceConcurrencyNumber, 0, "WAF invoice stale overwrite"),
        originalInvoiceUpdatedAt))
    {
        AssertStatus(HttpStatusCode.Conflict, staleInvoiceUpdate.StatusCode, "stale Invoice Record PUT");
        AssertContains("No fields were overwritten", await staleInvoiceUpdate.Content.ReadAsStringAsync(), "stale Invoice Record PUT message");
    }
    AssertEqual(
        "WAF invoice concurrency winner",
        (await GetJsonAsync(client, $"/api/documents/{invoiceConcurrencyRecord.GetProperty("id").GetInt32()}")).GetProperty("projectNotes").GetString(),
        "stale Invoice Record PUT preserves newer record");

    const string concurrentInvoiceNumber = "EST-2099-9902";
    var concurrentInvoice = await PostJsonAsync(
        client,
        "/api/documents",
        NewDocumentPayload("ESTIMATE", "Draft", concurrentInvoiceNumber, 0));
    var concurrentInvoiceId = concurrentInvoice.GetProperty("id").GetInt32();
    var concurrentInvoiceUpdatedAt = concurrentInvoice.GetProperty("updatedAt").GetString()!;
    var invoiceBarrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    var concurrentInvoiceWrites = new[]
    {
        PutJsonAfterBarrierAsync(client, invoiceBarrier.Task, $"/api/documents/{concurrentInvoiceId}", NewDocumentPayload("ESTIMATE", "Draft", concurrentInvoiceNumber, 0, "WAF concurrent invoice A"), concurrentInvoiceUpdatedAt),
        PutJsonAfterBarrierAsync(concurrentClient, invoiceBarrier.Task, $"/api/documents/{concurrentInvoiceId}", NewDocumentPayload("ESTIMATE", "Draft", concurrentInvoiceNumber, 0, "WAF concurrent invoice B"), concurrentInvoiceUpdatedAt)
    };
    invoiceBarrier.SetResult(true);
    var concurrentInvoiceResponses = await Task.WhenAll(concurrentInvoiceWrites);
    try
    {
        AssertEqual("1", concurrentInvoiceResponses.Count(response => response.StatusCode == HttpStatusCode.OK).ToString(CultureInfo.InvariantCulture), "two-client Invoice Record PUT has one winner");
        AssertEqual("1", concurrentInvoiceResponses.Count(response => response.StatusCode == HttpStatusCode.Conflict).ToString(CultureInfo.InvariantCulture), "two-client Invoice Record PUT has one stale loser");
        var persistedNotes = (await GetJsonAsync(client, $"/api/documents/{concurrentInvoiceId}")).GetProperty("projectNotes").GetString();
        AssertTrue(persistedNotes is "WAF concurrent invoice A" or "WAF concurrent invoice B", "two-client Invoice Record PUT persists the winner");
    }
    finally
    {
        foreach (var response in concurrentInvoiceResponses) response.Dispose();
    }

    using (var malformedConcurrencyRequest = new HttpRequestMessage(HttpMethod.Put, $"/api/parties/{partyId}"))
    {
        malformedConcurrencyRequest.Headers.TryAddWithoutValidation("X-EPATA-Updated-At", "not-a-timestamp");
        malformedConcurrencyRequest.Content = JsonContent.Create(new
        {
            name = "WAF malformed concurrency overwrite",
            partyType = "Customer"
        });
        using var malformedConcurrencyResponse = await client.SendAsync(malformedConcurrencyRequest);
        AssertStatus(HttpStatusCode.BadRequest, malformedConcurrencyResponse.StatusCode, "malformed generic PUT concurrency timestamp");
    }
    AssertEqual(
        "WAF optimistic concurrency winner",
        (await GetJsonAsync(client, $"/api/parties/{partyId}")).GetProperty("name").GetString(),
        "malformed concurrency header preserves record");

    using (var invalid = await client.PostAsJsonAsync("/api/action-items", new
    {
        title = new string('X', 181),
        status = "Open"
    }))
    {
        AssertStatus(HttpStatusCode.BadRequest, invalid.StatusCode, "generic CRUD validates DataAnnotations");
    }

    using (var nullDefaultsResponse = await client.PostAsJsonAsync("/api/action-items", new
    {
        title = "WAF null normalization",
        area = (string?)null,
        priority = (string?)null,
        status = (string?)null
    }))
    {
        AssertStatus(HttpStatusCode.Created, nullDefaultsResponse.StatusCode, "generic CRUD normalizes nullable JSON for non-null model fields");
        var normalized = await ReadJsonAsync(nullDefaultsResponse);
        AssertEqual("General", normalized.GetProperty("area").GetString(), "generic CRUD null area default");
        AssertEqual("Normal", normalized.GetProperty("priority").GetString(), "generic CRUD null priority default");
        AssertEqual("Open", normalized.GetProperty("status").GetString(), "generic CRUD null status default");
    }

    using (var crossSiteRequest = new HttpRequestMessage(HttpMethod.Post, "/api/parties"))
    {
        crossSiteRequest.Headers.TryAddWithoutValidation("Origin", "https://cross-site.example");
        crossSiteRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "cross-site");
        crossSiteRequest.Content = JsonContent.Create(new { name = "WAF cross-site rejected", partyType = "Customer" });
        using var crossSiteResponse = await client.SendAsync(crossSiteRequest);
        AssertStatus(HttpStatusCode.Forbidden, crossSiteResponse.StatusCode, "cross-site API mutation");
    }

    var sameOrigin = $"{client.BaseAddress!.Scheme}://{client.BaseAddress.Authority}";
    using (var sameSiteRequest = new HttpRequestMessage(HttpMethod.Post, "/api/parties"))
    {
        sameSiteRequest.Headers.TryAddWithoutValidation("Origin", sameOrigin);
        sameSiteRequest.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
        sameSiteRequest.Content = JsonContent.Create(new { name = "WAF same-origin accepted", partyType = "Customer" });
        using var sameSiteResponse = await client.SendAsync(sameSiteRequest);
        AssertStatus(HttpStatusCode.Created, sameSiteResponse.StatusCode, "same-origin API mutation");
    }

    const string concurrentOrderNumber = "WAF-CONCURRENT-MARKETPLACE-2099";
    var concurrentSaves = await Task.WhenAll(
        SaveConfirmedMarketplaceOrderAsync(client, concurrentOrderNumber),
        SaveConfirmedMarketplaceOrderAsync(client, concurrentOrderNumber));
    try
    {
        AssertEqual("1", concurrentSaves.Count(response => response.StatusCode == HttpStatusCode.OK).ToString(CultureInfo.InvariantCulture), "concurrent marketplace save winner count");
        AssertEqual("1", concurrentSaves.Count(response => response.StatusCode == HttpStatusCode.BadRequest).ToString(CultureInfo.InvariantCulture), "concurrent marketplace duplicate rejection count");
    }
    finally
    {
        foreach (var response in concurrentSaves)
        {
            response.Dispose();
        }
    }

    var matchingSales = (await GetJsonArrayAsync(client, "/api/sales?includeArchived=true"))
        .Where(item => string.Equals(item.GetProperty("orderNumber").GetString(), concurrentOrderNumber, StringComparison.Ordinal))
        .ToArray();
    AssertEqual("1", matchingSales.Length.ToString(CultureInfo.InvariantCulture), "concurrent marketplace save persists one Sale");
    var marketplaceSaleId = matchingSales.Single().GetProperty("id").GetInt32();
    using (var archive = await client.DeleteAsync($"/api/sales/{marketplaceSaleId}"))
    {
        AssertStatus(HttpStatusCode.NoContent, archive.StatusCode, "archive marketplace concurrency fixture");
    }
    using (var archivedDuplicate = await SaveConfirmedMarketplaceOrderAsync(client, concurrentOrderNumber))
    {
        AssertStatus(HttpStatusCode.BadRequest, archivedDuplicate.StatusCode, "archived marketplace Sale still reserves order identity");
        AssertContains("already appears", await archivedDuplicate.Content.ReadAsStringAsync(), "archived marketplace duplicate message");
    }
    matchingSales = (await GetJsonArrayAsync(client, "/api/sales?includeArchived=true"))
        .Where(item => string.Equals(item.GetProperty("orderNumber").GetString(), concurrentOrderNumber, StringComparison.Ordinal))
        .ToArray();
    AssertEqual("1", matchingSales.Length.ToString(CultureInfo.InvariantCulture), "archived marketplace retry creates no replacement Sale");
}

static async Task<HttpResponseMessage> PutJsonWithExpectedUpdatedAtAsync(
    HttpClient client,
    string path,
    object payload,
    DateTime expectedUpdatedAt)
    => await PutJsonWithExpectedUpdatedAtAsync(
        client,
        path,
        payload,
        expectedUpdatedAt.ToString("O", CultureInfo.InvariantCulture));

static async Task<HttpResponseMessage> PutJsonWithExpectedUpdatedAtAsync(
    HttpClient client,
    string path,
    object payload,
    string expectedUpdatedAt)
{
    using var request = new HttpRequestMessage(HttpMethod.Put, path);
    request.Headers.TryAddWithoutValidation("X-EPATA-Updated-At", expectedUpdatedAt);
    request.Content = JsonContent.Create(payload);
    return await client.SendAsync(request);
}

static async Task<HttpResponseMessage> PutJsonAfterBarrierAsync(
    HttpClient client,
    Task barrier,
    string path,
    object payload,
    DateTime expectedUpdatedAt)
{
    await barrier;
    return await PutJsonWithExpectedUpdatedAtAsync(client, path, payload, expectedUpdatedAt);
}

static async Task<HttpResponseMessage> PutJsonAfterBarrierAsync(
    HttpClient client,
    Task barrier,
    string path,
    object payload,
    string expectedUpdatedAt)
{
    await barrier;
    return await PutJsonWithExpectedUpdatedAtAsync(client, path, payload, expectedUpdatedAt);
}

static async Task<HttpResponseMessage> SaveConfirmedMarketplaceOrderAsync(HttpClient client, string orderNumber)
{
    using var form = new MultipartFormDataContent();
    form.Add(new StringContent(JsonSerializer.Serialize(new
    {
        saleDate = "2099-09-10",
        platform = "Etsy",
        paymentMethod = "Etsy Payments",
        salesTaxHandling = "Marketplace collected/remitted - verify",
        orderNumber,
        customerName = "WAF Marketplace Concurrency Customer",
        productName = "WAF Marketplace Concurrency Product",
        quantity = 1,
        itemSales = 12.50m,
        shippingCharged = 0,
        salesTaxCollected = 0,
        customerPaid = 12.50m,
        platformFees = 0,
        shippingLabelCost = 0,
        refunds = 0,
        estimatedCogs = 0,
        status = "Paid",
        needsReview = true
    })), "saleJson");
    form.Add(new StringContent("false"), "createJob");
    form.Add(new StringContent("false"), "saveCustomerContact");
    form.Add(new StringContent(JsonSerializer.Serialize(new[] { orderNumber })), "detectedOrderNumbersJson");
    return await client.PostAsync("/api/ai/operations/marketplace-order-import/save", form);
}

static async Task AssertBackupEndpointAsync(HttpClient client, string root, string path, bool usePost)
{
    var backupDirectory = Path.Combine(root, "Backups");
    Directory.CreateDirectory(backupDirectory);
    var before = Directory.GetFiles(backupDirectory, "*.db")
        .Select(Path.GetFileName)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    using var response = usePost
        ? await client.PostAsync(path, null)
        : await client.GetAsync(path);
    AssertStatus(HttpStatusCode.OK, response.StatusCode, path);
    var bytes = await response.Content.ReadAsByteArrayAsync();
    var signature = Encoding.ASCII.GetString(bytes, 0, 15);
    AssertEqual("SQLite format 3", signature, $"{path} backup signature");

    var downloadName = response.Content.Headers.ContentDisposition?.FileNameStar
        ?? response.Content.Headers.ContentDisposition?.FileName;
    if (!string.IsNullOrWhiteSpace(downloadName))
    {
        var safeName = Path.GetFileName(downloadName.Trim('"'));
        var retainedPath = Path.Combine(backupDirectory, safeName);
        if (usePost)
        {
            AssertTrue(File.Exists(retainedPath), $"{path} should retain a server backup copy");
            File.Delete(retainedPath);
        }
        else
        {
            AssertTrue(!File.Exists(retainedPath), $"{path} GET should not retain a server backup copy");
            var after = Directory.GetFiles(backupDirectory, "*.db")
                .Select(Path.GetFileName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            AssertTrue(before.SetEquals(after), $"{path} GET should be side-effect free in the Backups folder");
        }
    }
}

static async Task AssertOrderLossAndTurboTaxWorkbookAsync(HttpClient client)
{
    var sale = await PostJsonAsync(client, "/api/sales", new
    {
        saleDate = "2026-07-10",
        platform = "Etsy",
        paymentMethod = "Etsy Payments",
        salesTaxHandling = "Marketplace Collected / Remitted",
        orderNumber = "WAF-LOSS-ORDER-1",
        customerName = "Loss Test Customer",
        productName = "Replacement Test Part",
        quantity = 1,
        itemSales = 40,
        shippingCharged = 5,
        salesTaxCollected = 3,
        customerPaid = 48,
        platformFees = 4,
        shippingLabelCost = 6,
        refunds = 0,
        estimatedCogs = 8,
        status = "Paid",
        includeInDashboard = true,
        needsReview = false,
        sourceProof = "waf-loss-order.pdf"
    });
    var saleId = sale.GetProperty("id").GetInt32();

    var incident = await PostJsonAsync(client, "/api/order-loss-incidents", new
    {
        incidentDate = "2026-07-15",
        saleId,
        platform = "Etsy",
        orderNumber = "WAF-LOSS-ORDER-1",
        customerName = "Loss Test Customer",
        productName = "Replacement Test Part",
        incidentType = "Damaged in transit",
        resolution = "Replacement / reship",
        status = "Resolved",
        customerRefund = 0,
        replacementCogs = 8,
        additionalShippingCost = 7,
        otherCost = 0,
        reimbursementReceived = 2,
        reimbursementSource = "USPS",
        claimNumber = "WAF-CLAIM-1",
        countInTaxReports = true,
        sourceProof = "waf-damage-proof.pdf",
        needsReview = false
    });
    AssertEqual("13", incident.GetProperty("netLoss").GetDecimal().ToString(CultureInfo.InvariantCulture), "order loss net loss after reimbursement");

    using (var profileResponse = await client.PutAsJsonAsync("/api/tax-profile", new
    {
        entityType = "Single-member LLC / Schedule C",
        state = "New Jersey",
        formationMonth = 1,
        njSalesTaxRegistration = "Registered",
        hasEmployees = "No",
        paysContractors = "No",
        usesVehicle = "No",
        homeOffice = "No",
        inventoryMethod = "Supplies / expense",
        businessMileageRate = 0,
        notes = "WAF tax profile"
    }))
    {
        AssertStatus(HttpStatusCode.OK, profileResponse.StatusCode, "save tax profile for TurboTax workbook");
    }

    using (var setupResponse = await client.PutAsJsonAsync("/api/turbotax-setup", new
    {
        taxYear = 2026,
        businessName = "EPATA 3D Prints",
        principalBusinessActivity = "3D printed products",
        principalBusinessCode = "339999",
        businessAddress = "WAF Test Address",
        accountingMethod = "Cash",
        materiallyParticipated = "Yes",
        startedOrAcquiredThisYear = "No",
        madeReportablePayments = "No",
        filedRequired1099s = "Not applicable / Review",
        atRiskStatus = "Not applicable - profit",
        cogsMethod = "Per-sale cost estimates",
        inventoryValuationMethod = "Cost",
        inventoryMethodChanged = "No",
        vehicleDeductionMethod = "No vehicle deduction",
        vehicleAvailableForPersonalUse = "No",
        anotherVehicleAvailable = "Yes",
        vehicleEvidence = "No",
        vehicleEvidenceWritten = "No",
        homeOfficeMethod = "Not claimed"
    }))
    {
        AssertStatus(HttpStatusCode.OK, setupResponse.StatusCode, "save TurboTax setup");
    }

    var report = await GetJsonAsync(client, "/api/turbotax-report?year=2026");
    AssertTrue(report.GetProperty("readyForDataEntry").GetBoolean(), "completed TurboTax setup should have no blockers");
    AssertTrue(report.GetProperty("orderLossIncidents").GetArrayLength() > 0, "TurboTax report should include order-loss incidents");
    AssertTrue(report.GetProperty("expenseDetails").EnumerateArray().Any(x =>
        string.Equals(x.GetProperty("scheduleCCategory").GetString(), "Shipping and postage", StringComparison.Ordinal)
        && x.GetProperty("deductibleAmount").GetDecimal() == 7m),
        "TurboTax report should itemize replacement shipping");

    using var printable = await client.GetAsync("/api/export/turbotax-workbook?year=2026");
    AssertStatus(HttpStatusCode.OK, printable.StatusCode, "printable TurboTax workbook");
    var html = await printable.Content.ReadAsStringAsync();
    AssertContains("WAF-LOSS-ORDER-1", html, "printable TurboTax workbook order detail");
    AssertContains("Schedule C line-by-line totals", html, "printable TurboTax workbook line table");
}

static async Task AssertAiEstimateChatQuickRulesAsync(HttpClient client)
{
    using var form = new MultipartFormDataContent();
    form.Add(new StringContent("Customer asks for a quote for a small custom 3D printed bracket. Material PLA. Need one piece."), "sourceText");
    form.Add(new StringContent("Assistant modal context"), "sourceName");
    form.Add(new StringContent("What details are missing before this can become a reliable estimate or invoice draft?"), "question");
    form.Add(new StringContent("{\"docType\":\"ESTIMATE\",\"status\":\"Draft\"}", Encoding.UTF8, "application/json"), "currentDocumentContext");

    var stopwatch = Stopwatch.StartNew();
    using var response = await client.PostAsync("/api/ai/estimate-chat/upload", form);
    stopwatch.Stop();

    AssertStatus(HttpStatusCode.OK, response.StatusCode, "AI estimate chat quick rules");
    var result = await ReadJsonAsync(response);
    AssertEqual("Local quick rules", result.GetProperty("provider").GetString(), "AI estimate chat quick provider");
    AssertTrue(!result.GetProperty("usedAi").GetBoolean(), "AI estimate chat quick path should not call the model");
    AssertTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"AI estimate chat quick path took {stopwatch.Elapsed.TotalSeconds:N2}s");
    AssertContains("Dimensions", result.GetProperty("answer").GetString(), "AI estimate chat quick answer");
    AssertDoesNotContain("Chat failed", result.GetProperty("answer").GetString(), "AI estimate chat quick answer");
}

static async Task AssertAiEstimateDraftCleansGmailPasteAsync(HttpClient client)
{
    const string gmailPaste = """
        Skip to content Using Gmail with screen readers Enable desktop notifications for Gmail. OK No thanks 10 of 17 Estimate for Pontoon Boat Tiller Head Fitting Inbox Ernest Phillips III <epata.llc.co@gmail.com> Attachments Wed, May 20, 10:58 PM to ryan Hi Ryan, Attached is the estimate for your custom 3D print request. Please review the price, project details, material, and notes. If everything looks good, reply with approval and I'll move forward. If anything needs to be adjusted, send me the change.
        """;

    using var form = new MultipartFormDataContent();
    form.Add(new StringContent(gmailPaste), "sourceText");
    form.Add(new StringContent("Gmail paste"), "sourceName");

    using var response = await client.PostAsync("/api/ai/estimate-draft/upload", form);

    AssertStatus(HttpStatusCode.OK, response.StatusCode, "AI estimate draft Gmail cleanup");
    var result = await ReadJsonAsync(response);
    var prefill = result.GetProperty("prefill");
    var projectName = prefill.GetProperty("projectName").GetString();
    var projectDescription = prefill.GetProperty("projectDescription").GetString();
    var customerName = prefill.GetProperty("customerName").GetString();
    var customerEmail = prefill.GetProperty("customerEmail").GetString();

    AssertContains("Pontoon Boat Tiller Head Fitting", projectName, "Gmail cleanup project name");
    AssertContains("pontoon boat tiller head fitting", projectDescription, "Gmail cleanup project description");
    AssertContains("Ryan", customerName, "Gmail cleanup customer name");
    AssertDoesNotContain("Skip to content", projectDescription, "Gmail cleanup project description");
    AssertDoesNotContain("Using Gmail", projectDescription, "Gmail cleanup project description");
    AssertDoesNotContain("desktop notifications", projectDescription, "Gmail cleanup project description");
    AssertDoesNotContain("epata.llc.co@gmail.com", customerEmail, "Gmail cleanup customer email");
    AssertDoesNotContain("epata.llc.co@gmail.com", projectDescription, "Gmail cleanup project description");
}

static async Task AssertLocalAiTextCompletionUsesRequestedTokenBudgetAsync(string root)
{
    var modelsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".lmstudio",
        "models");
    var testModelDirectory = Path.Combine(modelsRoot, "codex-token-budget-test");
    var modelPath = Path.Combine(testModelDirectory, "codex-token-budget-test.gguf");
    Directory.CreateDirectory(testModelDirectory);
    await File.WriteAllTextAsync(modelPath, "test");

    var dbPath = Path.Combine(root, "Data", "local-ai-token-budget-tests.db");
    foreach (var path in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
    {
        File.Delete(path);
    }

    try
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=" + dbPath)
            .Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();
        db.AppSettings.AddRange(
            new AppSetting { Key = "LocalAi:BaseUrl", Value = "http://127.0.0.1:1234" },
            new AppSetting { Key = "LocalAi:ModelPath", Value = modelPath },
            new AppSetting { Key = "LocalAi:ModelIdentifier", Value = "test-model" },
            new AppSetting { Key = "LocalAi:ContextLength", Value = "8192" });
        await db.SaveChangesAsync();

        var handler = new RecordingLocalAiHandler();
        var localAi = new LocalAiService(db, new HttpClient(handler));

        var answer = await localAi.CompleteTextAsync("system", "user", CancellationToken.None, 512);

        AssertEqual("ok", answer, "local AI text answer");
        AssertEqual("512", handler.LastChatMaxTokens?.ToString(CultureInfo.InvariantCulture), "local AI text token budget");

        handler.ReturnReasoningOnly = true;
        var visibleFallback = await localAi.CompleteTextAsync("system", "user", CancellationToken.None, 512);
        AssertContains("- Dimensions", visibleFallback, "local AI reasoning-only fallback");
        AssertDoesNotContain("Okay", visibleFallback, "local AI reasoning-only fallback");

        handler.BlockStatusRequests = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancellationPropagated = false;
        try
        {
            await localAi.GetStatusAsync(includeAvailableModels: false, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            cancellationPropagated = true;
        }
        AssertTrue(cancellationPropagated, "local AI status propagates caller cancellation instead of reporting a false Off state");
    }
    finally
    {
        try { Directory.Delete(testModelDirectory, recursive: true); } catch { }
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

static async Task<JsonElement> UploadDocumentAsync(
    HttpClient client,
    string fileName,
    byte[] bytes,
    string? relatedType = null,
    string? relatedNumber = null)
{
    using var form = new MultipartFormDataContent();
    using var file = new ByteArrayContent(bytes);
    file.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");
    form.Add(file, "files", fileName);
    if (!string.IsNullOrWhiteSpace(relatedType)) form.Add(new StringContent(relatedType), "relatedType");
    if (!string.IsNullOrWhiteSpace(relatedNumber)) form.Add(new StringContent(relatedNumber), "relatedNumber");
    using var response = await client.PostAsync("/api/documents/upload", form);
    AssertStatus(HttpStatusCode.OK, response.StatusCode, $"upload {fileName}");
    return await ReadJsonAsync(response);
}

static byte[] CreateMinimalTextPdf(params string[] lines)
{
    var content = new StringBuilder()
        .AppendLine("BT")
        .AppendLine("/F1 11 Tf")
        .AppendLine("50 760 Td")
        .AppendLine("14 TL");
    foreach (var line in lines)
    {
        content.Append('(')
            .Append(line.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("(", "\\(", StringComparison.Ordinal)
                .Replace(")", "\\)", StringComparison.Ordinal))
            .AppendLine(") Tj")
            .AppendLine("T*");
    }
    content.AppendLine("ET");

    var contentBytes = Encoding.ASCII.GetBytes(content.ToString());
    var objects = new[]
    {
        "<< /Type /Catalog /Pages 2 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
        "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        $"<< /Length {contentBytes.Length} >>\nstream\n{content}endstream"
    };

    using var pdf = new MemoryStream();
    static void WriteAscii(Stream stream, string value)
    {
        var bytes = Encoding.ASCII.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
    }

    WriteAscii(pdf, "%PDF-1.4\n%WAF\n");
    var offsets = new long[objects.Length + 1];
    for (var index = 0; index < objects.Length; index++)
    {
        offsets[index + 1] = pdf.Position;
        WriteAscii(pdf, $"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
    }

    var xrefOffset = pdf.Position;
    WriteAscii(pdf, $"xref\n0 {objects.Length + 1}\n");
    WriteAscii(pdf, "0000000000 65535 f \n");
    for (var index = 1; index < offsets.Length; index++)
    {
        WriteAscii(pdf, $"{offsets[index]:D10} 00000 n \n");
    }
    WriteAscii(pdf, $"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xrefOffset}\n%%EOF\n");
    return pdf.ToArray();
}

static async Task DeleteUploadedTestProofsAsync(HttpClient client, string root, IReadOnlyCollection<string> fileNames)
{
    try
    {
        var expectedNames = fileNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var uploadRoot = Path.GetFullPath(Path.Combine(root, "UploadedDocs"));
        var uploadRootWithSeparator = uploadRoot.EndsWith(Path.DirectorySeparatorChar)
            ? uploadRoot
            : uploadRoot + Path.DirectorySeparatorChar;
        var documents = await GetJsonArrayAsync(client, "/api/audit-documents?includeArchived=true");
        foreach (var document in documents.Where(x => expectedNames.Contains(x.GetProperty("fileName").GetString() ?? string.Empty)))
        {
            if (!document.TryGetProperty("filePathOrUrl", out var pathElement)
                || string.IsNullOrWhiteSpace(pathElement.GetString()))
            {
                continue;
            }

            var path = Path.GetFullPath(pathElement.GetString()!);
            if (path.StartsWith(uploadRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(path);
            }
        }
    }
    catch
    {
        // Cleanup must not hide the behavior assertion that failed.
    }
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

static object NewDocumentPayload(string docType, string status, string docNumber, decimal paid, string? projectNotes = null)
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
        projectNotes = projectNotes ?? "Internal WAF note",
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

static object ManualReceivablePayload(string invoiceNumber, string sourceProof, string status, decimal paid) => new
{
    invoiceNumber,
    invoiceDate = "2026-06-21",
    dueDate = "2026-07-05",
    customerName = "WAF Manual AR Customer",
    projectName = "WAF Manual AR Project",
    status,
    subtotal = 50,
    discount = 0,
    rushFee = 0,
    salesTax = 0,
    invoiceTotal = 50,
    amountPaid = paid,
    paymentMethod = "Cash",
    includeInCashReports = paid > 0,
    needsReview = false,
    sourceProof
};

static object InvoiceBuilderConfigPayload(
    JsonElement config,
    decimal gramRate,
    decimal hourRate,
    decimal designRate,
    decimal minimum) => new
{
    id = 1,
    businessName = config.GetProperty("businessName").GetString(),
    businessLocation = config.GetProperty("businessLocation").GetString(),
    businessEmail = config.GetProperty("businessEmail").GetString(),
    businessPhone = config.GetProperty("businessPhone").GetString(),
    businessWebsite = config.GetProperty("businessWebsite").GetString(),
    businessEtsy = config.GetProperty("businessEtsy").GetString(),
    businessInstagram = config.GetProperty("businessInstagram").GetString(),
    businessFacebook = config.GetProperty("businessFacebook").GetString(),
    brandColor = config.GetProperty("brandColor").GetString(),
    calcGramRate = gramRate,
    calcHourRate = hourRate,
    calcDesignRate = designRate,
    calcSetupFee = config.GetProperty("calcSetupFee").GetDecimal(),
    calcPostFee = config.GetProperty("calcPostFee").GetDecimal(),
    calcMinimum = minimum
};

static string FindWorkspaceRoot()
{
    foreach (var startPath in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
    {
        var directory = new DirectoryInfo(startPath);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "EPATA.BusinessLedger.csproj")))
        {
            directory = directory.Parent;
        }

        if (directory is not null)
        {
            return directory.FullName;
        }
    }

    throw new InvalidOperationException(
        $"Could not find EPATA.BusinessLedger.csproj from {Directory.GetCurrentDirectory()} or {AppContext.BaseDirectory}.");
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
                ["Ai:AllowHostedFallback"] = "true",
                ["Ai:Provider"] = "OpenAI",
                ["Ai:Endpoint"] = "https://api.openai.com/v1/chat/completions",
                ["Ai:Model"] = "gpt-5.5",
                ["Ai:ApiKeyEnvironmentVariable"] = "EPATA_WAF_MISSING_API_KEY",
                ["Ai:AccountEmail"] = "erniephillips26@gmail.com"
            });
        });
    }
}

sealed class RecordingLocalAiHandler : HttpMessageHandler
{
    public int? LastChatMaxTokens { get; private set; }
    public bool ReturnReasoningOnly { get; set; }
    public bool BlockStatusRequests { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method == HttpMethod.Get
            && request.RequestUri?.AbsolutePath.Equals("/api/v1/models", StringComparison.OrdinalIgnoreCase) == true)
        {
            if (BlockStatusRequests)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return JsonResponse("""
                {
                  "models": [
                    {
                      "type": "llm",
                      "loaded_instances": [
                        { "id": "test-model" }
                      ]
                    }
                  ]
                }
                """);
        }

        if (request.Method == HttpMethod.Post
            && request.RequestUri?.AbsolutePath.Equals("/v1/chat/completions", StringComparison.OrdinalIgnoreCase) == true)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var document = JsonDocument.Parse(body);
            LastChatMaxTokens = document.RootElement.GetProperty("max_tokens").GetInt32();
            if (ReturnReasoningOnly)
            {
                return JsonResponse("""
                    {
                      "choices": [
                        {
                          "message": {
                            "content": "",
                            "reasoning_content": "Okay, so I need to think through the missing details.\\n1. Dimensions and tolerances\\n2. Deadline\\n3. Color"
                          }
                        }
                      ]
                    }
                    """);
            }
            return JsonResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "ok"
                      }
                    }
                  ]
                }
                """);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("not found", Encoding.UTF8, "text/plain")
        };
    }

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
}
