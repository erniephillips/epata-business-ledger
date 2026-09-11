using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using EPATA.BusinessLedger.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var webRootPath = new[]
    {
        Path.Combine(builder.Environment.ContentRootPath, "wwwroot"),
        Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
        Path.Combine(AppContext.BaseDirectory, "wwwroot")
    }
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .FirstOrDefault(Directory.Exists)
    ?? throw new DirectoryNotFoundException(
        "The application cannot find its wwwroot folder. Keep the published wwwroot folder beside EPATA.BusinessLedger.exe.");

builder.Environment.WebRootPath = webRootPath;
var webRootFileProvider = new PhysicalFileProvider(webRootPath);
builder.Environment.WebRootFileProvider = webRootFileProvider;

var appUrl = builder.Configuration["App:Url"] ?? "http://127.0.0.1:5062";
builder.WebHost.UseUrls(appUrl);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = ResolveConnectionString(builder.Configuration, builder.Environment.ContentRootPath);
    options.UseSqlite(connectionString);
});

builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<AiBusinessReviewService>();
builder.Services.AddScoped<ActionItemAutomationService>();
builder.Services.AddScoped<TaxPlanningService>();
builder.Services.AddScoped<TurboTaxReportService>();
builder.Services.AddScoped<AiSourceDocumentTextExtractor>();
builder.Services.AddScoped<InvoiceDocumentPdfDraftService>();
builder.Services.AddHttpClient<InvoiceAppImportService>();
builder.Services.AddHttpClient<LocalAiService>(client => client.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddHttpClient<AiOperationsService>(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(10);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 EPATA-Business-Ledger/1.0");
    })
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<AiEstimateService>(client => client.Timeout = TimeSpan.FromMinutes(10))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.WriteIndented = true;
});

var app = builder.Build();

Directory.CreateDirectory(GetSqliteDataDirectory(app.Configuration, app.Environment.ContentRootPath));
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "Backups"));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await EnsureUnifiedInvoiceTablesAsync(db);
    await DbSeeder.SeedAsync(db, app.Configuration);
    // Do not run data "repair" or invoice reconciliation on startup.
    // Invoice/estimate documents are user-owned records; background startup writes
    // can silently change type/status/amounts and make the records view untrustworthy.
}

app.UseDefaultFiles(new DefaultFilesOptions
{
    FileProvider = webRootFileProvider
});
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = webRootFileProvider,
    OnPrepareResponse = context =>
    {
        var path = context.File.PhysicalPath ?? string.Empty;
        if (path.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
        {
            context.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            context.Context.Response.Headers.Pragma = "no-cache";
            context.Context.Response.Headers.Expires = "0";
        }
    }
});

if (int.TryParse(app.Configuration["App:ArtificialLatencyMs"], out var artificialLatencyMs) && artificialLatencyMs > 0)
{
    var boundedLatencyMs = Math.Min(artificialLatencyMs, 5_000);
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            await Task.Delay(boundedLatencyMs, context.RequestAborted);
        }

        await next();
    });
}

app.Use(async (context, next) =>
{
    if (IsCrossSiteApiMutation(context.Request))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "This local ledger rejected a cross-site change. Use the EPATA app window that is connected to this local server."
        });
        return;
    }

    try
    {
        await next();
    }
    catch (Exception ex) when (IsSqliteBusyOrLocked(ex) && !context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "The local SQLite database is busy or locked. Close other EPATA windows, wait for OneDrive sync to finish, then try again."
        });
    }
    catch (DbUpdateConcurrencyException) when (!context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "This record changed while your save was in progress. Reload it, review the newer values, and try again. No fields were overwritten."
        });
    }
    catch (Exception ex) when (IsSqliteConstraintViolation(ex) && !context.Response.HasStarted)
    {
        context.Response.StatusCode = StatusCodes.Status409Conflict;
        await context.Response.WriteAsJsonAsync(new
        {
            message = "That change conflicts with an existing ledger record. Refresh the page and review the existing record before trying again."
        });
    }
});

MapCrud<Party>(app, "parties");
MapCrud<Sale>(app, "sales");
MapCrud<CustomerJob>(app, "customer-jobs");
MapCrud<CustomerCommunication>(app, "customer-communications");
MapCrud<PrinterQueueItem>(app, "printer-queue-items");
MapCrud<ReceivableInvoice>(app, "receivable-invoices");
MapCrud<Bill>(app, "bills");
MapCrud<Expense>(app, "expenses");
MapCrud<OrderLossIncident>(app, "order-loss-incidents");
MapCrud<Product>(app, "products");
MapCrud<Asset>(app, "assets");
MapCrud<MakerWorldReward>(app, "makerworld-rewards");
MapCrud<AuditDocument>(app, "audit-documents");
MapCrud<BusinessAccount>(app, "business-accounts");
MapCrud<ActionItem>(app, "action-items");
MapCrud<TaxObligation>(app, "tax-obligations");
MapCrud<MileageLog>(app, "mileage-logs");
MapCrud<AppSetting>(app, "settings");

app.MapGet("/api/dashboard", async (DashboardService dashboard) => Results.Ok(await dashboard.BuildAsync()));

app.MapGet("/api/tax-audit", async (AppDbContext db) => Results.Ok(await BuildTaxAuditAsync(db)));

app.MapGet("/api/tax-profile", async (TaxPlanningService tax) => Results.Ok(await tax.GetProfileAsync()));

app.MapPut("/api/tax-profile", async (TaxPlanningService tax, TaxProfile profile) => Results.Ok(await tax.SaveProfileAsync(profile)));

app.MapGet("/api/tax-summary", async (TaxPlanningService tax, int? year) =>
    Results.Ok(await tax.BuildSummaryAsync(year ?? DateTime.Today.Year)));

app.MapGet("/api/turbotax-setup", async (TurboTaxReportService report, int? year, CancellationToken cancellationToken) =>
    Results.Ok(await report.GetSetupAsync(year ?? DateTime.Today.Year, cancellationToken)));

app.MapPut("/api/turbotax-setup", async (TurboTaxReportService report, TaxYearSetup setup, CancellationToken cancellationToken) =>
    Results.Ok(await report.SaveSetupAsync(setup, cancellationToken)));

app.MapGet("/api/turbotax-report", async (TurboTaxReportService report, int? year, CancellationToken cancellationToken) =>
    Results.Ok(await report.BuildReportAsync(year ?? DateTime.Today.Year, cancellationToken)));

app.MapGet("/api/tax-calendar", async (AppDbContext db, int? year) =>
    Results.Ok(await db.TaxObligations.AsNoTracking()
        .Where(x => !x.IsArchived && x.TaxYear == (year ?? DateTime.Today.Year))
        .OrderBy(x => x.DueDate == null)
        .ThenBy(x => x.DueDate)
        .ThenBy(x => x.Title)
        .ToListAsync()));

app.MapPost("/api/tax-calendar/generate", async (TaxPlanningService tax, int? year, CancellationToken cancellationToken) =>
    Results.Ok(await tax.GenerateObligationsAsync(year ?? DateTime.Today.Year, cancellationToken)));

app.MapGet("/api/ai/review/status", (AiBusinessReviewService review) => Results.Ok(review.Status()));

app.MapGet("/api/ai/review", async (AiBusinessReviewService review) => Results.Ok(await review.BuildAsync()));

app.MapGet("/api/action-items/automation-preview", async (ActionItemAutomationService actions, CancellationToken cancellationToken) =>
    Results.Ok(await actions.PreviewAsync(cancellationToken)));

app.MapPost("/api/action-items/sync-findings", async (ActionItemAutomationService actions, CancellationToken cancellationToken) =>
    Results.Ok(await actions.SyncAsync(cancellationToken)));

app.MapPost("/api/ai/review/model", async Task<IResult> (AiBusinessReviewService review, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await review.BuildModelAssistedAsync(cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/ai/operations/status", async (AiOperationsService operations, CancellationToken cancellationToken) =>
    Results.Ok(await operations.StatusAsync(cancellationToken)));

app.MapGet("/api/ai/operations/reconciliation", async (AiOperationsService operations, CancellationToken cancellationToken) =>
    Results.Ok(await operations.BuildReconciliationAsync(cancellationToken)));

app.MapPost("/api/ai/operations/reconciliation/model", async Task<IResult> (AiOperationsService operations, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await operations.ExplainReconciliationAsync(cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/operations/marketplace-order-import", async Task<IResult> (
    HttpRequest request,
    AiOperationsService operations,
    AiSourceDocumentTextExtractor extractor,
    CancellationToken cancellationToken) =>
{
    try
    {
        var packet = await ReadAiOperationSourcePacketAsync(request, extractor, cancellationToken);
        return Results.Ok(await operations.BuildMarketplaceOrderDraftAsync(packet, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/operations/marketplace-order-import/save", async Task<IResult> (
    HttpRequest request,
    AiOperationsService operations,
    AppDbContext db,
    IWebHostEnvironment env,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Marketplace order save must be sent as multipart/form-data." });
    }
    var storedPaths = new List<string>();
    var persisted = false;
    try
    {
        var form = await request.ReadFormAsync(cancellationToken);
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
        var sale = JsonSerializer.Deserialize<Sale>(form["saleJson"].FirstOrDefault() ?? string.Empty, serializerOptions)
            ?? throw new InvalidOperationException("The reviewed Sale draft is missing or invalid.");
        var jobText = form["jobJson"].FirstOrDefault();
        var job = string.IsNullOrWhiteSpace(jobText) ? null : JsonSerializer.Deserialize<CustomerJob>(jobText, serializerOptions);
        var customerText = form["customerJson"].FirstOrDefault();
        var customer = string.IsNullOrWhiteSpace(customerText) ? null : JsonSerializer.Deserialize<Party>(customerText, serializerOptions);
        var createJob = bool.TryParse(form["createJob"].FirstOrDefault(), out var parsedCreateJob) && parsedCreateJob;
        var saveCustomerContact = !bool.TryParse(form["saveCustomerContact"].FirstOrDefault(), out var parsedSaveCustomerContact) || parsedSaveCustomerContact;
        var detectedOrderNumbers = JsonSerializer.Deserialize<List<string>>(form["detectedOrderNumbersJson"].FirstOrDefault() ?? "[]", serializerOptions) ?? [];
        var files = form.Files.Take(AiEstimateService.MaxUploadFiles).ToList();
        if (files.Sum(file => file.Length) > AiEstimateService.MaxTotalUploadBytes)
        {
            throw new InvalidOperationException($"The selected files total more than {AiEstimateService.MaxTotalUploadBytes / 1024 / 1024} MB.");
        }
        var uploadDir = Path.Combine(env.ContentRootPath, "UploadedDocs");
        Directory.CreateDirectory(uploadDir);
        var proofs = new List<AiMarketplaceProof>();
        foreach (var file in files)
        {
            if (file.Length <= 0) continue;
            if (file.Length > AiEstimateService.MaxUploadFileBytes)
            {
                throw new InvalidOperationException($"{file.FileName} is larger than the {AiEstimateService.MaxUploadFileBytes / 1024 / 1024} MB per-file limit.");
            }
            var storedName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}-{MakeSafeFileName(file.FileName)}";
            var storedPath = Path.Combine(uploadDir, storedName);
            await using (var stream = System.IO.File.Create(storedPath))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }
            storedPaths.Add(storedPath);
            proofs.Add(new AiMarketplaceProof(file.FileName, storedPath));
        }
        var result = await operations.SaveMarketplaceOrderAsync(new AiMarketplaceOrderSaveRequest(
            sale,
            job,
            customer,
            createJob,
            saveCustomerContact,
            proofs,
            detectedOrderNumbers), cancellationToken);
        persisted = true;
        return Results.Ok(result);
    }
    catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    finally
    {
        if (!persisted)
        {
            foreach (var path in storedPaths)
            {
                await DeleteProofFileIfUnreferencedAsync(db, path);
            }
        }
    }
});

app.MapPost("/api/ai/operations/product-import", async Task<IResult> (
    HttpRequest request,
    AiOperationsService operations,
    AiSourceDocumentTextExtractor extractor,
    CancellationToken cancellationToken) =>
{
    try
    {
        var packet = await ReadAiOperationSourcePacketAsync(request, extractor, cancellationToken);
        return Results.Ok(await operations.BuildProductDraftAsync(packet, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/operations/job-plan", async Task<IResult> (
    AiOperationsService operations,
    AiJobPlanRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await operations.BuildJobPlanAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/operations/job-plan/actions", async Task<IResult> (
    AiOperationsService operations,
    AiJobPlanSaveRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await operations.CreateJobPlanActionsAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/operations/slicer-read", async Task<IResult> (
    HttpRequest request,
    AiOperationsService operations,
    AiSourceDocumentTextExtractor extractor,
    CancellationToken cancellationToken) =>
{
    try
    {
        var packet = await ReadAiOperationSourcePacketAsync(request, extractor, cancellationToken);
        return Results.Ok(await operations.ReadSlicerAsync(packet, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/operations/listing", async Task<IResult> (
    AiOperationsService operations,
    AiListingRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await operations.BuildListingAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/operations/ask-ledger", async Task<IResult> (
    AiOperationsService operations,
    AiLedgerQuestionRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await operations.AskLedgerAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/ai/local/status", async (LocalAiService localAi, CancellationToken cancellationToken) =>
    Results.Ok(await localAi.GetStatusAsync(cancellationToken: cancellationToken)));

app.MapPut("/api/ai/local/settings", async Task<IResult> (
    LocalAiService localAi,
    SaveLocalAiSettingsRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await localAi.SaveSettingsAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/local/start", async Task<IResult> (LocalAiService localAi, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await localAi.StartAsync(cancellationToken);
        return result.Success ? Results.Ok(result) : Results.BadRequest(result);
    }
    catch (Exception ex) when (ex is InvalidOperationException or IOException)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/local/stop", async Task<IResult> (LocalAiService localAi, CancellationToken cancellationToken) =>
{
    var result = await localAi.StopAsync(cancellationToken);
    return result.Success ? Results.Ok(result) : Results.BadRequest(result);
});

app.MapGet("/api/job-timeline", async (AppDbContext db, string? q, string? customer) =>
    Results.Ok(await BuildJobTimelineAsync(db, q, customer)));

app.MapGet("/api/lookups", async (AppDbContext db) =>
{
    var customers = await db.Parties.Where(x => !x.IsArchived && (x.PartyType == "Customer" || x.PartyType == "Both")).OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
    var vendors = await db.Parties.Where(x => !x.IsArchived && (x.PartyType == "Vendor" || x.PartyType == "Both")).OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
    var products = await db.Products.Where(x => !x.IsArchived).OrderBy(x => x.Name).Select(x => new
    {
        x.Name,
        x.Sku,
        x.Category,
        x.Material,
        x.Color,
        x.Grams,
        x.PrintHours,
        x.MaterialCostPerGram,
        x.MachineRatePerHour,
        x.PackagingCost,
        x.DesignMinutes,
        x.TargetPrice
    }).ToListAsync();
    return Results.Ok(new { customers, vendors, products });
});

app.MapPost("/api/import/invoice-app", async Task<IResult> (
    InvoiceAppImportService importer,
    ImportInvoiceRequest request,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await importer.TryReadInvoiceAppAsync(request.BaseUrl, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapGet("/api/health", (IWebHostEnvironment env, IConfiguration config) => Results.Ok(new
{
    status = "ok",
    database = GetDatabasePath(config, env),
    serverTimeUtc = DateTimeOffset.UtcNow,
    mode = "unified-ledger"
}));

app.MapGet("/api/config", async (AppDbContext db) =>
{
    var settings = await db.AppSettings.AsNoTracking().ToDictionaryAsync(x => x.Key, x => x.Value);
    return Results.Ok(new
    {
        id = 1,
        businessName = settings.GetValueOrDefault("BusinessName", "EPATA 3D PRINTS"),
        businessLocation = settings.GetValueOrDefault("BusinessLocation", "Based in NJ"),
        businessEmail = settings.GetValueOrDefault("BusinessEmail", "erniephillips26@gmail.com"),
        businessPhone = settings.GetValueOrDefault("BusinessPhone", "(973) 306-8628"),
        businessWebsite = settings.GetValueOrDefault("BusinessWebsite", "https://erniephillipsportfolio.com/"),
        businessEtsy = settings.GetValueOrDefault("BusinessEtsy", "https://www.etsy.com/shop/epata3dprints"),
        businessInstagram = settings.GetValueOrDefault("BusinessInstagram", "@epata3dprints"),
        businessFacebook = settings.GetValueOrDefault("BusinessFacebook", "EPATA 3D Prints"),
        brandColor = settings.GetValueOrDefault("BrandColor", "#17468f"),
        calcGramRate = decimal.TryParse(settings.GetValueOrDefault("CalcGramRate"), out var gramRate) ? gramRate : 0.05m,
        calcHourRate = decimal.TryParse(settings.GetValueOrDefault("CalcHourRate"), out var hourRate) ? hourRate : 3m,
        calcDesignRate = decimal.TryParse(settings.GetValueOrDefault("CalcDesignRate"), out var designRate) ? designRate : 25m,
        calcSetupFee = decimal.TryParse(settings.GetValueOrDefault("CalcSetupFee"), out var setupFee) ? setupFee : 0m,
        calcPostFee = decimal.TryParse(settings.GetValueOrDefault("CalcPostFee"), out var postFee) ? postFee : 0m,
        calcMinimum = decimal.TryParse(settings.GetValueOrDefault("CalcMinimum"), out var minimum) ? minimum : 15m
    });
});

app.MapPut("/api/config", async (AppDbContext db, InvoiceBuilderConfigRequest request) =>
{
    var normalized = NormalizeInvoiceBuilderConfigRequest(request);
    await UpsertSettingAsync(db, "BusinessName", normalized.BusinessName);
    await UpsertSettingAsync(db, "BusinessLocation", normalized.BusinessLocation);
    await UpsertSettingAsync(db, "BusinessEmail", normalized.BusinessEmail);
    await UpsertSettingAsync(db, "BusinessPhone", normalized.BusinessPhone);
    await UpsertSettingAsync(db, "BusinessWebsite", normalized.BusinessWebsite);
    await UpsertSettingAsync(db, "BusinessEtsy", normalized.BusinessEtsy);
    await UpsertSettingAsync(db, "BusinessInstagram", normalized.BusinessInstagram);
    await UpsertSettingAsync(db, "BusinessFacebook", normalized.BusinessFacebook);
    await UpsertSettingAsync(db, "BrandColor", normalized.BrandColor);
    await UpsertSettingAsync(db, "CalcGramRate", normalized.CalcGramRate.ToString(CultureInfo.InvariantCulture));
    await UpsertSettingAsync(db, "CalcHourRate", normalized.CalcHourRate.ToString(CultureInfo.InvariantCulture));
    await UpsertSettingAsync(db, "CalcDesignRate", normalized.CalcDesignRate.ToString(CultureInfo.InvariantCulture));
    await UpsertSettingAsync(db, "CalcSetupFee", normalized.CalcSetupFee.ToString(CultureInfo.InvariantCulture));
    await UpsertSettingAsync(db, "CalcPostFee", normalized.CalcPostFee.ToString(CultureInfo.InvariantCulture));
    await UpsertSettingAsync(db, "CalcMinimum", normalized.CalcMinimum.ToString(CultureInfo.InvariantCulture));
    await db.SaveChangesAsync();
    return Results.Ok(normalized);
});

app.MapGet("/api/documents", async (AppDbContext db, string? q, string? type, string? status, bool includeArchived = false) =>
{
    return Results.Ok(await UnifiedInvoiceRecordSummariesAsync(db, q, type, status, includeArchived));
});

app.MapGet("/api/documents/stats", async (AppDbContext db) =>
{
    return Results.Ok(await UnifiedInvoiceStatsAsync(db));
});

app.MapGet("/api/documents/next-number", async (AppDbContext db, string type) =>
    Results.Ok(new { number = await NextInvoiceDocumentNumberAsync(db, type) }));

app.MapGet("/api/documents/latest", async (AppDbContext db) =>
{
    var doc = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
        .Where(d => !d.IsArchived)
        .OrderByDescending(d => d.UpdatedAt)
        .ThenByDescending(d => d.Id)
        .FirstOrDefaultAsync();

    return doc is null ? Results.NotFound(new { message = "No saved estimates or invoices yet." }) : Results.Ok(ToInvoiceDocumentDto(doc));
});

app.MapGet("/api/documents/{id:int}", async (AppDbContext db, int id) =>
{
    var doc = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
        .FirstOrDefaultAsync(d => d.Id == id);
    return doc is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(doc));
});

app.MapPost("/api/documents", async (AppDbContext db, SaveInvoiceDocumentRequest request, CancellationToken cancellationToken) =>
{
    return await CreateInvoiceDocumentResultAsync(db, request, cancellationToken);
});

app.MapPut("/api/documents/{id:int}", async (
    AppDbContext db,
    int id,
    SaveInvoiceDocumentRequest request,
    HttpRequest httpRequest,
    CancellationToken cancellationToken) =>
{
    return await UpdateInvoiceDocumentResultAsync(db, id, request, httpRequest, cancellationToken);
});

app.MapDelete("/api/documents/{id:int}", async (AppDbContext db, int id, CancellationToken cancellationToken) =>
{
    return await DeleteInvoiceDocumentAndSyncAsync(db, id, cancellationToken);
});

app.MapPost("/api/documents/{id:int}/restore", async (AppDbContext db, int id, CancellationToken cancellationToken) =>
{
    return await RestoreInvoiceDocumentAndSyncAsync(db, id, cancellationToken);
});

app.MapPost("/api/documents/{id:int}/duplicate", async (AppDbContext db, int id) =>
{
    var copy = await DuplicateInvoiceDocumentAsync(db, id, null);
    return copy is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(copy));
});

app.MapPost("/api/documents/{id:int}/convert-to-invoice", async (AppDbContext db, int id) =>
{
    return await ConvertInvoiceDocumentResultAsync(db, id);
});

app.MapGet("/api/database/backup", async (AppDbContext db, IWebHostEnvironment env, IConfiguration configuration) =>
{
    var backup = await CreateDatabaseBackupAsync(db, env, configuration, retainServerCopy: false);
    return Results.File(backup.Bytes, "application/x-sqlite3", backup.FileName);
});

app.MapPost("/api/database/clear", () => Results.BadRequest(new { message = "Clear is disabled in the unified ledger. Delete individual documents or back up the database first." }));
app.MapPost("/api/database/import", () => Results.BadRequest(new { message = "Full database import is disabled in the unified ledger. Use Import Old App Once for invoice migration." }));

app.MapGet("/api/invoice-documents", async (AppDbContext db, string? q, string? type, string? status, bool includeArchived = false) =>
{
    return Results.Ok(await UnifiedInvoiceRecordSummariesAsync(db, q, type, status, includeArchived));
});

app.MapGet("/api/invoice-documents/stats", async (AppDbContext db) =>
{
    return Results.Ok(await UnifiedInvoiceStatsAsync(db));
});

app.MapGet("/api/invoice-documents/next-number", async (AppDbContext db, string type) =>
{
    return Results.Ok(new { number = await NextInvoiceDocumentNumberAsync(db, type) });
});

app.MapGet("/api/invoice-documents/latest", async (AppDbContext db) =>
{
    var doc = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
        .Where(d => !d.IsArchived)
        .OrderByDescending(d => d.UpdatedAt)
        .ThenByDescending(d => d.Id)
        .FirstOrDefaultAsync();

    return doc is null ? Results.NotFound(new { message = "No saved estimates or invoices yet." }) : Results.Ok(ToInvoiceDocumentDto(doc));
});

app.MapGet("/api/invoice-documents/{id:int}", async (AppDbContext db, int id) =>
{
    var doc = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
        .FirstOrDefaultAsync(d => d.Id == id);

    return doc is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(doc));
});

app.MapPost("/api/invoice-documents", async (AppDbContext db, SaveInvoiceDocumentRequest request, CancellationToken cancellationToken) =>
{
    return await CreateInvoiceDocumentResultAsync(db, request, cancellationToken);
});

app.MapPut("/api/invoice-documents/{id:int}", async (
    AppDbContext db,
    int id,
    SaveInvoiceDocumentRequest request,
    HttpRequest httpRequest,
    CancellationToken cancellationToken) =>
{
    return await UpdateInvoiceDocumentResultAsync(db, id, request, httpRequest, cancellationToken);
});

app.MapDelete("/api/invoice-documents/{id:int}", async (AppDbContext db, int id, CancellationToken cancellationToken) =>
{
    return await DeleteInvoiceDocumentAndSyncAsync(db, id, cancellationToken);
});

app.MapPost("/api/invoice-documents/{id:int}/restore", async (AppDbContext db, int id, CancellationToken cancellationToken) =>
{
    return await RestoreInvoiceDocumentAndSyncAsync(db, id, cancellationToken);
});

app.MapPost("/api/invoice-documents/{id:int}/duplicate", async (AppDbContext db, int id) =>
{
    var copy = await DuplicateInvoiceDocumentAsync(db, id, null);
    return copy is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(copy));
});

app.MapPost("/api/invoice-documents/{id:int}/convert-to-invoice", async (AppDbContext db, int id) =>
{
    return await ConvertInvoiceDocumentResultAsync(db, id);
});

app.MapPost("/api/invoice-documents/import-from-legacy", async Task<IResult> (
    InvoiceAppImportService importer,
    AppDbContext db,
    CancellationToken cancellationToken) =>
{
    List<InvoiceAppFullDocument> docs;
    try
    {
        docs = await importer.ReadFullDocumentsAsync(cancellationToken: cancellationToken);
    }
    catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
    {
        return Results.Ok(new
        {
            success = false,
            imported = 0,
            created = 0,
            updated = 0,
            message = $"Could not reach the old invoice app at http://localhost:5057. Open it first if you still need a one-time legacy import. {ex.Message}"
        });
    }

    await InvoiceDocumentMutationLocks.Gate.WaitAsync(cancellationToken);
    try
    {
        var created = 0;
        var updated = 0;
        var skippedArchived = 0;
        var importCandidates = docs
            .Where(document => !string.IsNullOrWhiteSpace(document.DocNumber))
            .GroupBy(document => document.DocNumber!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(document => ParseDateTime(document.UpdatedAt) ?? DateTime.MinValue)
                .ThenBy(document => document.Id)
                .Last())
            .ToList();
        var touchedDocuments = new List<InvoiceDocument>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        try
        {
            foreach (var old in importCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var documentNumber = old.DocNumber!.Trim().ToUpperInvariant();
                var doc = await db.InvoiceDocuments
                    .Include(d => d.LineItems)
                    .OrderBy(d => d.IsArchived)
                    .FirstOrDefaultAsync(
                        d => d.DocNumber != null && d.DocNumber.ToUpper() == documentNumber,
                        cancellationToken);
                if (doc is null)
                {
                    doc = new InvoiceDocument { DocNumber = documentNumber, CreatedAt = old.CreatedAt ?? DateTimeOffset.UtcNow.ToString("O") };
                    db.InvoiceDocuments.Add(doc);
                    created++;
                }
                else if (doc.IsArchived)
                {
                    skippedArchived++;
                    continue;
                }
                else
                {
                    updated++;
                }

                doc.DocType = (old.DocType ?? "ESTIMATE").ToUpperInvariant();
                doc.Status = old.Status ?? "Draft";
                doc.CustomerName = old.CustomerName;
                doc.CustomerPhone = old.CustomerPhone;
                doc.CustomerAddress = old.CustomerAddress;
                doc.CustomerEmail = old.CustomerEmail;
                doc.PreparedFor = old.PreparedFor;
                doc.ProjectName = old.ProjectName;
                doc.Material = old.Material;
                doc.Color = old.Color;
                doc.Infill = old.Infill;
                doc.ProjectDescription = old.ProjectDescription;
                doc.ProjectNotes = old.ProjectNotes;
                doc.PageSize = old.PageSize;
                doc.Total = old.Total;
                doc.Subtotal = old.Subtotal != 0 ? old.Subtotal : old.Total;
                doc.DiscountAmount = old.DiscountAmount;
                doc.RushAmount = old.RushAmount;
                doc.TaxAmount = old.TaxAmount;
                doc.AmountPaid = old.AmountPaid;
                doc.Balance = old.Balance;
                doc.DocDate = old.DocDate;
                doc.DueDate = old.DueDate;
                doc.PricingGuide = old.PricingGuide;
                doc.TermsNotes = old.TermsNotes;
                doc.StandardTurnaround = old.StandardTurnaround;
                doc.RushTurnaround = old.RushTurnaround;
                doc.CalcGrams = old.CalcGrams;
                doc.CalcHours = old.CalcHours;
                doc.CalcDesignHours = old.CalcDesignHours;
                doc.CalcSetupFee = old.CalcSetupFee;
                doc.CalcPostFee = old.CalcPostFee;
                doc.CalcGramRate = old.CalcGramRate == 0 ? 0.05m : old.CalcGramRate;
                doc.CalcHourRate = old.CalcHourRate == 0 ? 3m : old.CalcHourRate;
                doc.CalcDesignRate = old.CalcDesignRate == 0 ? 25m : old.CalcDesignRate;
                doc.CalcMinimum = old.CalcMinimum == 0 ? 15m : old.CalcMinimum;
                doc.CalcDifficulty = old.CalcDifficulty == 0 ? 1m : old.CalcDifficulty;
                doc.CalcRush = old.CalcRush;
                doc.CalcDiscount = old.CalcDiscount;
                doc.CalcTaxRate = old.CalcTaxRate;
                doc.Json = old.Json ?? "{}";
                doc.LineItems.Clear();
                doc.LineItems.AddRange((old.LineItems ?? []).Select((line, index) => new InvoiceLineItem
                {
                    SortOrder = line.SortOrder > 0 ? line.SortOrder : index + 1,
                    Description = line.Description,
                    Details = line.Details,
                    Quantity = Math.Max(0, line.Quantity),
                    Rate = Math.Max(0, line.Rate),
                    Amount = Math.Max(0, line.Quantity) * Math.Max(0, line.Rate)
                }));
                NormalizeExistingInvoiceDocumentMoney(doc);
                doc.UpdatedAt = old.UpdatedAt ?? DateTimeOffset.UtcNow.ToString("O");
                touchedDocuments.Add(doc);
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var doc in touchedDocuments.DistinctBy(document => document.Id))
            {
                await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc, cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new
            {
                success = true,
                imported = created + updated,
                sourceDocuments = docs.Count,
                skippedDuplicateSourceDocuments = docs.Count - importCandidates.Count,
                skippedArchivedDocuments = skippedArchived,
                created,
                updated
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return Results.Conflict(new
            {
                success = false,
                imported = 0,
                created = 0,
                updated = 0,
                message = $"The legacy import was rolled back completely; no partial documents or accounting rows were saved. {ex.Message}"
            });
        }
    }
    finally
    {
        InvoiceDocumentMutationLocks.Gate.Release();
    }
});

app.MapPost("/api/documents/upload", async (
    HttpRequest request,
    AppDbContext db,
    IWebHostEnvironment env,
    AiSourceDocumentTextExtractor extractor,
    InvoiceDocumentPdfDraftService invoiceDrafts,
    AiOperationsService operations,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Upload must be sent as multipart/form-data." });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    if (form.Files.Count == 0)
    {
        return Results.BadRequest(new { message = "Choose at least one file to upload." });
    }

    const int maxUploadFiles = 20;
    const long maxTotalUploadBytes = 100L * 1024 * 1024;
    if (form.Files.Count > maxUploadFiles)
    {
        return Results.BadRequest(new { message = $"Upload no more than {maxUploadFiles} proof files at a time." });
    }
    if (form.Files.Sum(file => file.Length) > maxTotalUploadBytes)
    {
        return Results.BadRequest(new { message = "The combined proof upload is larger than the 100 MB batch limit." });
    }

    foreach (var file in form.Files)
    {
        if (file.Length <= 0)
        {
            return Results.BadRequest(new { message = $"{file.FileName} is empty. Choose a non-empty proof file." });
        }

        if (file.Length > AiEstimateService.MaxUploadFileBytes)
        {
            return Results.BadRequest(new { message = $"{file.FileName} is larger than the {AiEstimateService.MaxUploadFileBytes / 1024 / 1024} MB per-file proof upload limit." });
        }

        if (!IsSupportedProofUploadFile(file.FileName))
        {
            return Results.BadRequest(new { message = $"{file.FileName} is not a supported proof upload type. Use PDF, PNG, JPG, WEBP, TXT, CSV, JSON, or Markdown." });
        }
    }

    var uploadDir = Path.Combine(env.ContentRootPath, "UploadedDocs");
    Directory.CreateDirectory(uploadDir);

    var created = new List<AuditDocument>();
    var documents = new List<AuditDocument>();
    var duplicateUploads = new List<object>();
    var marketplaceImports = new List<AiMarketplaceOrderAutoImportResult>();
    var invoiceDocumentImports = new List<InvoiceDocumentIntakeResult>();
    var requestedRelatedType = form["relatedType"].FirstOrDefault();
    var requestedRelatedNumber = form["relatedNumber"].FirstOrDefault();
    foreach (var file in form.Files)
    {
        var safeName = MakeSafeFileName(file.FileName);
        var storedName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}-{safeName}";
        var storedPath = Path.Combine(uploadDir, storedName);
        var retainedFile = false;
        SemaphoreSlim? uploadGate = null;
        var uploadGateHeld = false;
        try
        {
            await using (var stream = System.IO.File.Create(storedPath))
            {
                await file.CopyToAsync(stream, cancellationToken);
            }

            var contentHash = await ComputeFileSha256Async(storedPath, cancellationToken);
            var uploadFingerprint = BuildUploadFingerprint(
                contentHash,
                file.FileName,
                requestedRelatedType,
                requestedRelatedNumber);
            uploadGate = UploadMutationLocks.For(uploadFingerprint);
            await uploadGate.WaitAsync(cancellationToken);
            uploadGateHeld = true;

            var duplicate = await db.AuditDocuments.AsNoTracking().FirstOrDefaultAsync(
                document => !document.IsArchived && document.UploadFingerprint == uploadFingerprint,
                cancellationToken);
            if (duplicate is null)
            {
                duplicate = await FindAndClaimLegacyDuplicateUploadAsync(
                    db,
                    env,
                    contentHash,
                    uploadFingerprint,
                    file.FileName,
                    requestedRelatedType,
                    requestedRelatedNumber,
                    cancellationToken);
            }
            if (duplicate is not null)
            {
                if (!TryGetExistingAllowedProofFilePath(env.ContentRootPath, duplicate.FilePathOrUrl, out _))
                {
                    var trackedDuplicate = await db.AuditDocuments.FirstAsync(
                        document => document.Id == duplicate.Id,
                        cancellationToken);
                    trackedDuplicate.FilePathOrUrl = storedPath;
                    trackedDuplicate.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    retainedFile = true;
                    duplicate = trackedDuplicate;
                }
                documents.Add(duplicate);
                duplicateUploads.Add(new
                {
                    auditDocumentId = duplicate.Id,
                    duplicate.FileName,
                    duplicate.RelatedRecordType,
                    duplicate.RelatedRecordNumber,
                    message = "This exact file upload was already saved. The existing proof was returned and no duplicate record or file was created."
                });
                continue;
            }

            AiSourceDocumentExtraction extraction;
            try
            {
                extraction = await extractor.ExtractAsync(storedPath, file.ContentType, cancellationToken);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or System.Xml.XmlException)
            {
                extraction = new AiSourceDocumentExtraction(string.Empty, $"Readable text could not be extracted from {file.FileName}: {ex.Message}");
            }
            var preview = TrimPreview(extraction.Text);
            var doc = new AuditDocument
            {
                DocumentDate = DateTime.Today,
                DocumentType = GuessDocumentType(file.FileName, extraction.Text),
                FileName = file.FileName,
                FilePathOrUrl = storedPath,
                UploadFingerprint = uploadFingerprint,
                RelatedRecordType = requestedRelatedType,
                RelatedRecordNumber = requestedRelatedNumber,
                NeedsReview = true,
                Notes = string.IsNullOrWhiteSpace(preview)
                    ? AppendNote("Uploaded document. Review and connect it to the matching sale, invoice, bill, expense, or job.", extraction.Warning ?? string.Empty)
                    : AppendNote($"Uploaded document. Extracted preview:\n{preview}", extraction.Warning ?? string.Empty)
            };

            db.AuditDocuments.Add(doc);
            await db.SaveChangesAsync(cancellationToken);
            retainedFile = true;
            created.Add(doc);
            documents.Add(doc);

            var selectedArea = doc.RelatedRecordType?.Trim();
            if (IsInvoiceDocumentRelatedArea(selectedArea))
            {
                try
                {
                    invoiceDocumentImports.Add(await ImportInvoiceDocumentFromAuditAsync(
                        db,
                        invoiceDrafts,
                        doc,
                        extraction,
                        storedPath,
                        selectedArea!,
                        cancellationToken));
                }
                catch (Exception ex) when (ex is InvalidOperationException
                    or IOException
                    or UnauthorizedAccessException
                    or ArgumentException
                    or NotSupportedException)
                {
                    doc = await ReloadAuditDocumentAfterFailedMutationAsync(db, doc.Id, cancellationToken);
                    ReplaceAuditDocument(created, doc);
                    ReplaceAuditDocument(documents, doc);
                    doc.NeedsReview = true;
                    doc.Notes = AppendNote(doc.Notes, $"Automatic {selectedArea} import could not finish: {ex.Message}");
                    doc.UpdatedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(cancellationToken);
                    invoiceDocumentImports.Add(new InvoiceDocumentIntakeResult(
                        "NeedsReview",
                        null,
                        doc,
                        $"The proof is indexed, but the {selectedArea?.ToLowerInvariant()} record could not be created: {ex.Message}",
                        [ex.Message],
                        null));
                }
            }
            else if (string.IsNullOrWhiteSpace(selectedArea)
                || selectedArea.Equals("Sale", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    marketplaceImports.Add(await operations.AutoImportMarketplaceOrderAsync(
                        new AiOperationSourcePacket(
                            extraction.Text,
                            [],
                            [],
                            string.IsNullOrWhiteSpace(extraction.Warning) ? [] : [extraction.Warning],
                            file.FileName),
                        doc,
                        cancellationToken));
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException or HttpRequestException or IOException)
                {
                    doc = await ReloadAuditDocumentAfterFailedMutationAsync(db, doc.Id, cancellationToken);
                    ReplaceAuditDocument(created, doc);
                    ReplaceAuditDocument(documents, doc);
                    doc.NeedsReview = true;
                    doc.Notes = AppendNote(doc.Notes, $"Automatic Etsy import could not finish: {ex.Message}");
                    await db.SaveChangesAsync(cancellationToken);
                    marketplaceImports.Add(new AiMarketplaceOrderAutoImportResult
                    {
                        Recognized = doc.DocumentType.Equals("Etsy Order", StringComparison.OrdinalIgnoreCase),
                        Action = "NeedsReview",
                        AuditDocument = doc,
                        Message = $"The file was safely indexed, but automatic Etsy import could not finish: {ex.Message}",
                        Warnings = [ex.Message]
                    });
                }
            }
        }
        finally
        {
            if (uploadGateHeld)
            {
                uploadGate!.Release();
            }
            if (!retainedFile)
            {
                await DeleteProofFileIfUnreferencedAsync(db, storedPath);
            }
        }
    }

    await db.SaveChangesAsync(cancellationToken);
    return Results.Ok(new
    {
        count = documents.Count,
        createdCount = created.Count,
        duplicateUploadCount = duplicateUploads.Count,
        documents,
        duplicateUploads,
        suggestions = documents.Select(BuildInboxSuggestion).ToList(),
        marketplaceImports,
        invoiceDocumentImports = invoiceDocumentImports.Select(ToInvoiceDocumentIntakeDto).ToList(),
        marketplaceCreated = marketplaceImports.Count(x => x.Action == "Created"),
        marketplaceLinked = marketplaceImports.Count(x => x.Action == "LinkedExisting"),
        marketplaceNeedsReview = marketplaceImports.Count(x => x.Action == "NeedsReview"),
        invoiceDocumentCreated = invoiceDocumentImports.Count(x => x.Action == "Created"),
        invoiceDocumentLinked = invoiceDocumentImports.Count(x => x.Action == "LinkedExisting"),
        invoiceDocumentNeedsReview = invoiceDocumentImports.Count(x => x.Action == "NeedsReview")
    });
});

app.MapPost("/api/audit-documents/{id:int}/import-invoice-document", async Task<IResult> (
    int id,
    InvoiceDocumentImportRequest request,
    AppDbContext db,
    IWebHostEnvironment env,
    AiSourceDocumentTextExtractor extractor,
    InvoiceDocumentPdfDraftService invoiceDrafts,
    CancellationToken cancellationToken) =>
{
    var auditDocument = await db.AuditDocuments.FirstOrDefaultAsync(
        document => document.Id == id && !document.IsArchived,
        cancellationToken);
    if (auditDocument is null)
    {
        return Results.NotFound(new { message = $"Audit document {id} was not found." });
    }

    var targetType = string.IsNullOrWhiteSpace(request.TargetType)
        ? auditDocument.RelatedRecordType
        : request.TargetType;
    if (!IsInvoiceDocumentRelatedArea(targetType))
    {
        return Results.BadRequest(new { message = "Choose Invoice or Estimate as the target document type." });
    }

    if (string.IsNullOrWhiteSpace(auditDocument.FilePathOrUrl))
    {
        return Results.BadRequest(new { message = "This Audit Doc does not have a saved proof file." });
    }

    var fullPath = Path.GetFullPath(auditDocument.FilePathOrUrl);
    if (!IsAllowedProofFilePath(env.ContentRootPath, fullPath) || !System.IO.File.Exists(fullPath))
    {
        return Results.BadRequest(new { message = "The saved proof file is missing or outside an allowed EPATA business folder." });
    }

    try
    {
        var extraction = await extractor.ExtractAsync(fullPath, ContentTypeForFile(fullPath), cancellationToken);
        var result = await ImportInvoiceDocumentFromAuditAsync(
            db,
            invoiceDrafts,
            auditDocument,
            extraction,
            fullPath,
            targetType!,
            cancellationToken);
        return Results.Ok(ToInvoiceDocumentIntakeDto(result));
    }
    catch (Exception ex) when (ex is InvalidOperationException
        or IOException
        or UnauthorizedAccessException
        or ArgumentException
        or NotSupportedException)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/documents/reprocess-marketplace-orders", async (
    AppDbContext db,
    IWebHostEnvironment env,
    AiSourceDocumentTextExtractor extractor,
    AiOperationsService operations,
    CancellationToken cancellationToken) =>
{
    var candidates = await db.AuditDocuments
        .Where(x => !x.IsArchived
            && x.FilePathOrUrl != null
            && (x.NeedsReview
                || x.RelatedRecordType != "Sale"
                || x.DocumentType != "Etsy Order"
                || (x.RelatedRecordNumber != null
                    && !db.Sales.Any(sale => !sale.IsArchived
                        && sale.OrderNumber == x.RelatedRecordNumber))))
        .OrderBy(x => x.Id)
        .ToListAsync(cancellationToken);
    var results = new List<AiMarketplaceOrderAutoImportResult>();
    var errors = new List<object>();

    foreach (var candidate in candidates)
    {
        var doc = await db.AuditDocuments.FirstOrDefaultAsync(
            auditDocument => auditDocument.Id == candidate.Id && !auditDocument.IsArchived,
            cancellationToken);
        if (doc is null)
        {
            continue;
        }
        var wasEtsyCandidate = doc.DocumentType.Equals("Etsy Order", StringComparison.OrdinalIgnoreCase)
            || (doc.Notes?.Contains("Etsy", StringComparison.OrdinalIgnoreCase) ?? false);
        try
        {
            var fullPath = Path.GetFullPath(doc.FilePathOrUrl!);
            if (!IsAllowedProofFilePath(env.ContentRootPath, fullPath) || !System.IO.File.Exists(fullPath))
            {
                errors.Add(new { auditDocumentId = doc.Id, doc.FileName, message = "The saved proof file is missing or outside an allowed EPATA business folder." });
                continue;
            }

            var extraction = await extractor.ExtractAsync(fullPath, ContentTypeForFile(fullPath), cancellationToken);
            var preview = TrimPreview(extraction.Text);
            var replaceGeneratedPreview = doc.Notes?.StartsWith(
                "Uploaded document. Extracted preview:",
                StringComparison.OrdinalIgnoreCase) == true;
            var result = await operations.AutoImportMarketplaceOrderAsync(
                new AiOperationSourcePacket(
                    extraction.Text,
                    [],
                    [],
                    string.IsNullOrWhiteSpace(extraction.Warning) ? [] : [extraction.Warning],
                    doc.FileName),
                doc,
                cancellationToken);
            results.Add(result);
            if (result.Recognized && !string.IsNullOrWhiteSpace(preview))
            {
                var cleanPreviewNote = $"Uploaded document. Extracted preview:\n{preview}";
                doc.Notes = replaceGeneratedPreview
                    ? AppendNote(cleanPreviewNote, result.Message)
                    : AppendNote(doc.Notes, $"Reprocessed readable preview:\n{preview}");
                doc.DocumentType = "Etsy Order";
                doc.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException
            or JsonException
            or HttpRequestException
            or IOException
            or InvalidDataException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException
            or System.Security.SecurityException)
        {
            var failedDocumentId = doc.Id;
            db.ChangeTracker.Clear();
            doc = await db.AuditDocuments.FirstAsync(
                auditDocument => auditDocument.Id == failedDocumentId,
                cancellationToken);
            if (wasEtsyCandidate)
            {
                doc.NeedsReview = true;
                doc.Notes = AppendNote(doc.Notes, $"Automatic Etsy reprocessing could not finish: {ex.Message}");
                doc.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
            }
            errors.Add(new { auditDocumentId = doc.Id, doc.FileName, message = ex.Message });
        }
    }

    return Results.Ok(new
    {
        scanned = candidates.Count,
        recognized = results.Count(x => x.Recognized),
        created = results.Count(x => x.Action == "Created"),
        linkedExisting = results.Count(x => x.Action == "LinkedExisting"),
        needsReview = results.Count(x => x.Action == "NeedsReview"),
        indexedOnly = results.Count(x => x.Action == "IndexedOnly"),
        results,
        errors
    });
});

app.MapGet("/api/audit-documents/{id:int}/file", async Task<IResult> (int id, AppDbContext db, IWebHostEnvironment env) =>
{
    var doc = await db.AuditDocuments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && !x.IsArchived);
    if (doc is null)
    {
        return Results.NotFound(new { message = "Audit document not found." });
    }

    if (string.IsNullOrWhiteSpace(doc.FilePathOrUrl))
    {
        return Results.NotFound(new { message = "This Audit Doc does not have a saved file path." });
    }

    var fullPath = Path.GetFullPath(doc.FilePathOrUrl);
    if (!IsAllowedProofFilePath(env.ContentRootPath, fullPath))
    {
        return Results.BadRequest(new { message = "This file path is outside the EPATA business folders the app is allowed to open." });
    }

    if (!System.IO.File.Exists(fullPath))
    {
        return Results.NotFound(new { message = "The saved file is missing from UploadedDocs." });
    }

    return Results.File(System.IO.File.OpenRead(fullPath), ContentTypeForFile(fullPath), fileDownloadName: null, enableRangeProcessing: true);
});

app.MapGet("/api/export/{entity}", async (string entity, AppDbContext db, bool includeArchived = false) =>
{
    var fileName = $"epata-{entity}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
    return entity.ToLowerInvariant() switch
    {
        "parties" or "customers" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Parties.AsNoTracking(), includeArchived).ToListAsync())),
        "sales" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Sales.AsNoTracking(), includeArchived).ToListAsync())),
        "customer-jobs" or "jobs" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.CustomerJobs.AsNoTracking(), includeArchived).ToListAsync())),
        "customer-communications" or "communications" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.CustomerCommunications.AsNoTracking(), includeArchived).ToListAsync())),
        "printer-queue-items" or "printer-queue" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.PrinterQueueItems.AsNoTracking(), includeArchived).ToListAsync())),
        "receivable-invoices" or "invoices" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.ReceivableInvoices.AsNoTracking(), includeArchived).ToListAsync())),
        "bills" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Bills.AsNoTracking(), includeArchived).ToListAsync())),
        "expenses" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Expenses.AsNoTracking(), includeArchived).ToListAsync())),
        "order-loss-incidents" or "order-losses" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.OrderLossIncidents.AsNoTracking(), includeArchived).ToListAsync())),
        "products" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Products.AsNoTracking(), includeArchived).ToListAsync())),
        "assets" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Assets.AsNoTracking(), includeArchived).ToListAsync())),
        "makerworld-rewards" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.MakerWorldRewards.AsNoTracking(), includeArchived).ToListAsync())),
        "audit-documents" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.AuditDocuments.AsNoTracking(), includeArchived).ToListAsync())),
        "business-accounts" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.BusinessAccounts.AsNoTracking(), includeArchived).ToListAsync())),
        "action-items" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.ActionItems.AsNoTracking(), includeArchived).ToListAsync())),
        "tax-obligations" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.TaxObligations.AsNoTracking(), includeArchived).ToListAsync())),
        "mileage-logs" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.MileageLogs.AsNoTracking(), includeArchived).ToListAsync())),
        _ => Results.NotFound(new { message = "Unknown export entity." })
    };
});

app.MapGet("/api/export/tax-sales", async (AppDbContext db, string? paymentGroup = "all", int? year = null) =>
{
    var sales = await db.Sales.AsNoTracking()
        .Where(x => !x.IsArchived && x.IncludeInDashboard && (!year.HasValue || (x.SaleDate.HasValue && x.SaleDate.Value.Year == year.Value)))
        .OrderBy(x => x.SaleDate)
        .ThenBy(x => x.Id)
        .ToListAsync();
    var rows = sales
        .Select(x => new TaxSaleExportRow(
            x.Id,
            x.SaleDate,
            x.Platform,
            x.PaymentMethod,
            PaymentChannelFor(x),
            x.SalesTaxHandling,
            x.OrderNumber,
            x.InvoiceNumber,
            x.CustomerName,
            x.ProductName,
            x.ItemSales,
            x.ShippingCharged,
            x.SalesTaxCollected,
            x.CustomerPaid,
            x.PlatformFees,
            x.ShippingLabelCost,
            x.Refunds,
            x.EstimatedCogs,
            x.Status,
            x.SourceProof,
            x.NeedsReview,
            x.Notes))
        .Where(x => PaymentChannelMatches(x.PaymentGroup, paymentGroup))
        .ToList();
    var group = string.IsNullOrWhiteSpace(paymentGroup) ? "all" : Regex.Replace(paymentGroup.ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
    return Csv($"epata-tax-sales-{year?.ToString() ?? "all-years"}-{group}-{DateTime.Now:yyyyMMdd-HHmmss}.csv", CsvExportService.ToCsv(rows));
});

app.MapGet("/api/export/tax-summary", async (TaxPlanningService tax, int? year) =>
{
    var resolvedYear = year ?? DateTime.Today.Year;
    return Csv($"epata-tax-summary-{resolvedYear}-{DateTime.Now:yyyyMMdd-HHmmss}.csv", CsvExportService.ToCsv(new[] { await tax.BuildSummaryAsync(resolvedYear) }));
});

app.MapGet("/api/export/schedule-c-lines", async (TurboTaxReportService report, int? year, CancellationToken cancellationToken) =>
{
    var resolvedYear = year ?? DateTime.Today.Year;
    var workbook = await report.BuildReportAsync(resolvedYear, cancellationToken);
    return Csv($"epata-schedule-c-lines-{resolvedYear}-{DateTime.Now:yyyyMMdd-HHmmss}.csv", CsvExportService.ToCsv(workbook.ScheduleCLines));
});

app.MapGet("/api/export/turbotax-workbook", async (TurboTaxReportService report, int? year, CancellationToken cancellationToken) =>
{
    var resolvedYear = year ?? DateTime.Today.Year;
    var html = await report.BuildPrintableHtmlAsync(resolvedYear, cancellationToken);
    return Results.Content(html, "text/html; charset=utf-8", Encoding.UTF8);
});

app.MapGet("/api/export/nj-sales-tax", async (TaxPlanningService tax, int? year, int? quarter) =>
{
    var resolvedYear = year ?? DateTime.Today.Year;
    var quarterName = quarter is >= 1 and <= 4 ? $"-q{quarter}" : "-all-quarters";
    return Csv($"epata-nj-sales-tax-review-{resolvedYear}{quarterName}-{DateTime.Now:yyyyMMdd-HHmmss}.csv", CsvExportService.ToCsv(await tax.BuildNjSalesTaxReviewAsync(resolvedYear, quarter)));
});

app.MapGet("/api/export/tax-package", async (TaxPlanningService tax, int? year) =>
{
    var resolvedYear = year ?? DateTime.Today.Year;
    return Results.File(await tax.BuildTaxPackageAsync(resolvedYear), "application/zip", $"epata-tax-package-{resolvedYear}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
});

app.MapGet("/api/ai/estimate/status", async (AiEstimateService ai, CancellationToken cancellationToken) =>
    Results.Ok(await ai.StatusAsync(cancellationToken)));

app.MapPost("/api/ai/estimate-draft", async Task<IResult> (AiEstimateService ai, AiEstimateDraftRequest request, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await ai.CreateDraftAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/estimate-draft/upload", async Task<IResult> (
    HttpRequest request,
    AiEstimateService ai,
    AiSourceDocumentTextExtractor extractor,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "AI estimate uploads must be sent as multipart/form-data." });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var sourceText = form["sourceText"].FirstOrDefault() ?? string.Empty;
    var sourceName = form["sourceName"].FirstOrDefault() ?? "Mixed sources";
    var sourceUrls = form["sourceUrls"]
        .SelectMany(value => value?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(AiEstimateService.MaxSourceUrls)
        .ToList();
    var images = new List<AiEstimateImageInput>();
    var warnings = new List<string>();
    var files = form.Files.Take(AiEstimateService.MaxUploadFiles).ToList();
    if (form.Files.Count > AiEstimateService.MaxUploadFiles)
    {
        warnings.Add($"Only the first {AiEstimateService.MaxUploadFiles} files were read.");
    }
    var totalBytes = files.Sum(file => file.Length);
    if (totalBytes > AiEstimateService.MaxTotalUploadBytes)
    {
        return Results.BadRequest(new { message = $"The selected files total more than {AiEstimateService.MaxTotalUploadBytes / 1024 / 1024} MB. Remove or split some files and try again." });
    }

    foreach (var file in files)
    {
        if (file.Length <= 0) continue;
        if (file.Length > AiEstimateService.MaxUploadFileBytes)
        {
            return Results.BadRequest(new { message = $"{file.FileName} is larger than the {AiEstimateService.MaxUploadFileBytes / 1024 / 1024} MB per-file AI intake limit." });
        }

        if (file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            await using var memory = new MemoryStream();
            await file.CopyToAsync(memory, cancellationToken);
            images.Add(new AiEstimateImageInput(file.FileName, file.ContentType, Convert.ToBase64String(memory.ToArray())));
            continue;
        }

        try
        {
            var extraction = await extractor.ExtractAsync(file, cancellationToken);
            if (!string.IsNullOrWhiteSpace(extraction.Text))
            {
                sourceText = string.Join("\n\n", new[] { sourceText, $"SOURCE FILE: {file.FileName}\n{extraction.Text}" }.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            if (!string.IsNullOrWhiteSpace(extraction.Warning)) warnings.Add(extraction.Warning);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            warnings.Add($"{file.FileName} could not be read as a valid {Path.GetExtension(file.FileName).TrimStart('.').ToUpperInvariant()} document.");
        }
    }

    try
    {
        return Results.Ok(await ai.CreateDraftAsync(new AiEstimateDraftRequest(sourceText, sourceName, sourceUrls, images, warnings), cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/estimate-chat/upload", async Task<IResult> (
    HttpRequest request,
    AiEstimateService ai,
    AiSourceDocumentTextExtractor extractor,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "AI estimate chat must be sent as multipart/form-data." });
    }

    var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };
    var form = await request.ReadFormAsync(cancellationToken);
    var sourceText = form["sourceText"].FirstOrDefault() ?? string.Empty;
    var sourceName = form["sourceName"].FirstOrDefault() ?? "Assistant modal sources";
    var sourceUrls = form["sourceUrls"]
        .SelectMany(value => value?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(AiEstimateService.MaxSourceUrls)
        .ToList();
    var question = form["question"].FirstOrDefault() ?? string.Empty;
    var currentDocumentContext = form["currentDocumentContext"].FirstOrDefault() ?? string.Empty;
    var messagesJson = form["messagesJson"].FirstOrDefault();
    List<AiEstimateChatTurn> messages = string.IsNullOrWhiteSpace(messagesJson)
        ? []
        : JsonSerializer.Deserialize<List<AiEstimateChatTurn>>(messagesJson, serializerOptions) ?? [];
    var images = new List<AiEstimateImageInput>();
    var warnings = new List<string>();
    var files = form.Files.Take(AiEstimateService.MaxUploadFiles).ToList();
    if (form.Files.Count > AiEstimateService.MaxUploadFiles)
    {
        warnings.Add($"Only the first {AiEstimateService.MaxUploadFiles} files were read.");
    }

    var totalBytes = files.Sum(file => file.Length);
    if (totalBytes > AiEstimateService.MaxTotalUploadBytes)
    {
        return Results.BadRequest(new { message = $"The selected files total more than {AiEstimateService.MaxTotalUploadBytes / 1024 / 1024} MB. Remove or split some files and try again." });
    }

    foreach (var file in files)
    {
        if (file.Length <= 0) continue;
        if (file.Length > AiEstimateService.MaxUploadFileBytes)
        {
            return Results.BadRequest(new { message = $"{file.FileName} is larger than the {AiEstimateService.MaxUploadFileBytes / 1024 / 1024} MB per-file AI chat limit." });
        }

        if (file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            await using var memory = new MemoryStream();
            await file.CopyToAsync(memory, cancellationToken);
            images.Add(new AiEstimateImageInput(file.FileName, file.ContentType, Convert.ToBase64String(memory.ToArray())));
            continue;
        }

        try
        {
            var extraction = await extractor.ExtractAsync(file, cancellationToken);
            if (!string.IsNullOrWhiteSpace(extraction.Text))
            {
                sourceText = string.Join("\n\n", new[] { sourceText, $"SOURCE FILE: {file.FileName}\n{extraction.Text}" }.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            if (!string.IsNullOrWhiteSpace(extraction.Warning)) warnings.Add(extraction.Warning);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            warnings.Add($"{file.FileName} could not be read as a valid {Path.GetExtension(file.FileName).TrimStart('.').ToUpperInvariant()} document.");
        }
    }

    try
    {
        return Results.Ok(await ai.ChatAsync(new AiEstimateChatRequest(
            sourceText,
            sourceName,
            sourceUrls,
            images,
            warnings,
            messages,
            question,
            currentDocumentContext), cancellationToken));
    }
    catch (Exception ex) when (ex is InvalidOperationException or JsonException)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/ai/invoice-document-draft/upload", async Task<IResult> (
    HttpRequest request,
    InvoiceDocumentPdfDraftService draftService,
    AiSourceDocumentTextExtractor extractor,
    CancellationToken cancellationToken) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Invoice PDF import must be sent as multipart/form-data." });
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var sourceText = form["sourceText"].FirstOrDefault() ?? string.Empty;
    var sourceName = form["sourceName"].FirstOrDefault() ?? "Uploaded invoice or estimate PDF";
    var warnings = new List<string>();
    var files = form.Files.Take(AiEstimateService.MaxUploadFiles).ToList();
    if (form.Files.Count > AiEstimateService.MaxUploadFiles)
    {
        warnings.Add($"Only the first {AiEstimateService.MaxUploadFiles} files were read.");
    }

    var totalBytes = files.Sum(file => file.Length);
    if (totalBytes > AiEstimateService.MaxTotalUploadBytes)
    {
        return Results.BadRequest(new { message = $"The selected files total more than {AiEstimateService.MaxTotalUploadBytes / 1024 / 1024} MB. Remove or split some files and try again." });
    }

    foreach (var file in files)
    {
        if (file.Length <= 0) continue;
        if (file.Length > AiEstimateService.MaxUploadFileBytes)
        {
            return Results.BadRequest(new { message = $"{file.FileName} is larger than the {AiEstimateService.MaxUploadFileBytes / 1024 / 1024} MB per-file AI intake limit." });
        }

        try
        {
            var extraction = await extractor.ExtractAsync(file, cancellationToken);
            if (!string.IsNullOrWhiteSpace(extraction.Text))
            {
                sourceText = string.Join("\n\n", new[] { sourceText, $"SOURCE FILE: {file.FileName}\n{extraction.Text}" }.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            if (!string.IsNullOrWhiteSpace(extraction.Warning)) warnings.Add(extraction.Warning);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            warnings.Add($"{file.FileName} could not be read as a valid {Path.GetExtension(file.FileName).TrimStart('.').ToUpperInvariant()} document.");
        }
    }

    try
    {
        return Results.Ok(await draftService.CreateDraftAsync(sourceText, sourceName, warnings, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
});

app.MapPost("/api/system/backup", async (AppDbContext db, IConfiguration configuration, IWebHostEnvironment env) =>
{
    var backup = await CreateDatabaseBackupAsync(db, env, configuration, retainServerCopy: true);
    return Results.File(backup.Bytes, "application/octet-stream", backup.FileName);
});

app.MapGet("/api/app-info", (IWebHostEnvironment env, IConfiguration config) =>
    Results.Ok(new
    {
        environment = env.EnvironmentName,
        isTest = env.EnvironmentName.Equals("Test", StringComparison.OrdinalIgnoreCase),
        dbPath = new SqliteConnectionStringBuilder(ResolveConnectionString(config, env.ContentRootPath)).DataSource
    }));

app.MapGet("/invoice-builder/", () => Results.Redirect("/invoice-builder/index.html"));

app.MapFallbackToFile("index.html", new StaticFileOptions
{
    FileProvider = webRootFileProvider
});

var openBrowser = bool.TryParse(app.Configuration["App:OpenBrowserOnStart"], out var shouldOpen) && shouldOpen;
if (openBrowser)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        OpenBrowser(appUrl);
    });
}

WindowsTrayApplication? trayApplication = null;
var showTrayIcon = OperatingSystem.IsWindows()
    && !app.Environment.IsEnvironment("Test")
    && (!bool.TryParse(app.Configuration["App:ShowTrayIcon"], out var shouldShowTrayIcon) || shouldShowTrayIcon);
if (showTrayIcon)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            trayApplication = WindowsTrayApplication.Start(appUrl, app.Lifetime.StopApplication);
        }
        catch (Exception ex)
        {
            app.Logger.LogWarning(ex, "The Windows notification area icon could not be started.");
        }
    });
}

try
{
    await app.RunAsync();
}
finally
{
    trayApplication?.Dispose();
}

static void OpenBrowser(string appUrl)
{
    try
    {
        Process.Start(new ProcessStartInfo(appUrl) { UseShellExecute = true });
    }
    catch
    {
        // Browser auto-open is a convenience only; app still runs without it.
    }
}

static async Task<object> BuildJobTimelineAsync(AppDbContext db, string? q, string? customer)
{
    var groups = new Dictionary<string, TimelineGroupDto>(StringComparer.OrdinalIgnoreCase);
    var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var query = (q ?? string.Empty).Trim();
    var customerFilter = (customer ?? string.Empty).Trim();

    var sales = await db.Sales.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var jobs = await db.CustomerJobs.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var communications = await db.CustomerCommunications.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var printerQueue = await db.PrinterQueueItems.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var receivables = await db.ReceivableInvoices.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var expenses = await db.Expenses.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var docs = await db.InvoiceDocuments.AsNoTracking().ToListAsync();
    var activeDocNumbers = docs
        .Where(d => !d.IsArchived && !d.Status.Equals("Void", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(d.DocNumber))
        .Select(d => d.DocNumber!)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var auditDocs = await db.AuditDocuments.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var actions = await db.ActionItems.AsNoTracking().Where(x => !x.IsArchived && x.Status != "Done").ToListAsync();
    var documentEvents = await db.InvoiceDocumentEvents.AsNoTracking().ToListAsync();
    var docsWithEvents = documentEvents.Select(e => e.InvoiceDocumentId).ToHashSet();

    foreach (var doc in docs)
    {
        if (doc.IsArchived || doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        var date = TimelineDocumentMoment(ParseDate(doc.DocDate), doc.UpdatedAt, doc.CreatedAt, doc.Id);
        var group = GetTimelineGroup(groups, aliases, doc.CustomerName, doc.DocNumber, null, doc.ProjectName, doc.DocType);
        group.FlowScore = Math.Max(group.FlowScore, 3);
        group.WorkflowType = doc.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase) ? "Estimate Flow" : "Invoice Flow";
        group.Status = PickBestStatus(group.Status, doc.Status);
        if (doc.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase)) AddTimelineEstimate(group, doc.DocNumber, doc.Total);
        if (doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase))
        {
            AddTimelineInvoiced(group, doc.DocNumber, doc.Total);
            AddTimelinePaid(group, doc.DocNumber, doc.AmountPaid);
        }
        if (!docsWithEvents.Contains(doc.Id))
        {
            group.Events.Add(new TimelineEventDto(date, doc.DocType, doc.Status, doc.DocNumber ?? $"Document #{doc.Id}",
                doc.ProjectName ?? doc.PreparedFor ?? doc.CustomerName ?? "Document", doc.Total, "invoiceRecords", doc.Id, doc.DocNumber, null, false));
        }
        if (doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) && doc.Balance > 0 && !IsClosedInvoiceDocumentStatus(doc.Status))
        {
            AddMissing(group, $"Open balance: {doc.Balance:C}");
        }
        if (string.IsNullOrWhiteSpace(doc.CustomerName)) AddMissing(group, "Missing customer name");
    }

    foreach (var docEvent in documentEvents)
    {
        var group = GetTimelineGroup(groups, aliases, null, docEvent.DocNumber, null, docEvent.Summary, docEvent.DocType);
        group.WorkflowType = docEvent.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase) ? "Estimate Flow" : "Invoice Flow";
        group.Events.Add(new TimelineEventDto(ParseTimelineDate(docEvent.CreatedAt), docEvent.DocType, docEvent.ToStatus ?? docEvent.EventType, docEvent.Summary,
            docEvent.Detail ?? docEvent.EventType, docEvent.Amount, "invoiceRecords", docEvent.InvoiceDocumentId, docEvent.DocNumber, null, false));
    }

    foreach (var invoice in receivables)
    {
        if (IsOrphanedUnifiedInvoiceLedgerRow(invoice.SourceProof, invoice.InvoiceNumber, activeDocNumbers))
        {
            continue;
        }

        var group = GetTimelineGroup(groups, aliases, invoice.CustomerName, invoice.InvoiceNumber, null, invoice.ProjectName, "Invoice");
        group.FlowScore = Math.Max(group.FlowScore, 3);
        group.WorkflowType = "Invoice Flow";
        group.Status = PickBestStatus(group.Status, invoice.Status);
        AddTimelineInvoiced(group, invoice.InvoiceNumber, invoice.InvoiceTotal ?? 0);
        AddTimelinePaid(group, invoice.InvoiceNumber, invoice.AmountPaid ?? 0);
        group.Events.Add(new TimelineEventDto(TimelineMoment(invoice.InvoiceDate, invoice.UpdatedAtUtc, invoice.CreatedAtUtc, invoice.Id), "AR Invoice", invoice.Status, invoice.InvoiceNumber,
            invoice.ProjectName ?? invoice.CustomerName, invoice.InvoiceTotal, "receivables", invoice.Id, invoice.InvoiceNumber, invoice.SourceProof, invoice.NeedsReview));
        if (invoice.BalanceDue > 0 && !invoice.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase)) AddMissing(group, $"AR still open: {invoice.BalanceDue:C}");
        if (string.IsNullOrWhiteSpace(invoice.SourceProof)) AddMissing(group, "AR invoice needs source proof");
    }

    foreach (var sale in sales)
    {
        if (IsOrphanedUnifiedInvoiceLedgerRow(sale.SourceProof, sale.InvoiceNumber, activeDocNumbers))
        {
            continue;
        }

        var group = GetTimelineGroup(groups, aliases, sale.CustomerName, sale.InvoiceNumber, sale.OrderNumber, sale.ProductName, "Sale");
        group.FlowScore = Math.Max(group.FlowScore, 4);
        group.WorkflowType = "Paid Sale Flow";
        group.Status = PickBestStatus(group.Status, sale.Status);
        var gross = sale.CustomerPaid ?? ((sale.ItemSales ?? 0) + (sale.ShippingCharged ?? 0) + (sale.SalesTaxCollected ?? 0));
        group.GrossReceipts += gross;
        AddTimelinePaid(group, sale.InvoiceNumber ?? sale.OrderNumber, gross);
        group.TaxMemo += sale.SalesTaxCollected ?? 0;
        group.SellingCosts += (sale.PlatformFees ?? 0) + (sale.ShippingLabelCost ?? 0) + (sale.Refunds ?? 0);
        group.EstimatedCogs += sale.EstimatedCogs ?? 0;
        group.Events.Add(new TimelineEventDto(TimelineMoment(sale.SaleDate, sale.UpdatedAtUtc, sale.CreatedAtUtc, sale.Id), "Sale", sale.Status, sale.OrderNumber ?? sale.InvoiceNumber ?? $"Sale #{sale.Id}",
            sale.ProductName, gross, "sales", sale.Id, sale.OrderNumber ?? sale.InvoiceNumber, sale.SourceProof, sale.NeedsReview));
        if (string.IsNullOrWhiteSpace(sale.SourceProof)) AddMissing(group, "Sale needs source proof");
        if ((sale.EstimatedCogs ?? 0) <= 0) AddMissing(group, "Sale missing estimated COGS");
        if (sale.Platform.Equals("Etsy", StringComparison.OrdinalIgnoreCase) && (sale.PlatformFees ?? 0) <= 0) AddMissing(group, "Etsy sale missing platform fees");
        if (sale.Platform.Equals("Etsy", StringComparison.OrdinalIgnoreCase) && (sale.ShippingCharged ?? 0) > 0 && (sale.ShippingLabelCost ?? 0) <= 0) AddMissing(group, "Etsy sale missing shipping label cost");
    }

    foreach (var job in jobs)
    {
        if (IsOrphanedUnifiedEstimateJob(job.SourceProof, job.RelatedInvoiceNumber, activeDocNumbers))
        {
            continue;
        }

        var group = GetTimelineGroup(groups, aliases, job.CustomerName, job.RelatedInvoiceNumber, job.RelatedOrderNumber, job.JobName, "Job");
        group.FlowScore = Math.Max(group.FlowScore, 3);
        group.WorkflowType = "Job Flow";
        group.Status = PickBestStatus(group.Status, job.Status);
        var isUnifiedEstimateJob = !string.IsNullOrWhiteSpace(job.SourceProof)
            && job.SourceProof.StartsWith("Unified estimate ", StringComparison.OrdinalIgnoreCase);
        AddTimelineEstimate(group, job.RelatedInvoiceNumber ?? job.JobNumber ?? job.JobName, job.QuoteAmount ?? 0);
        if (!isUnifiedEstimateJob)
        {
            AddTimelineInvoiced(group, job.RelatedInvoiceNumber ?? job.JobNumber ?? job.JobName, job.InvoiceAmount ?? 0);
            AddTimelinePaid(group, job.RelatedInvoiceNumber ?? job.JobNumber ?? job.JobName, job.AmountPaid ?? 0);
        }
        group.Events.Add(new TimelineEventDto(TimelineMoment(job.JobDate, job.UpdatedAtUtc, job.CreatedAtUtc, job.Id), "Job", job.Status, job.JobNumber ?? job.JobName ?? $"Job #{job.Id}",
            job.Description ?? job.ProductName ?? job.JobName ?? "Customer job", job.InvoiceAmount ?? job.QuoteAmount, "customerJobs", job.Id, job.JobNumber ?? job.RelatedOrderNumber ?? job.RelatedInvoiceNumber, job.SourceProof, job.NeedsReview));
        if (string.IsNullOrWhiteSpace(job.SourceProof)) AddMissing(group, "Job needs proof or customer message");
        if (job.Status is "Lead" or "Quoted" or "Open" or "In Progress") AddMissing(group, $"Job still {job.Status}");
    }

    foreach (var communication in communications)
    {
        var group = GetTimelineGroup(
            groups,
            aliases,
            communication.CustomerName,
            communication.RelatedInvoiceNumber,
            communication.RelatedOrderNumber,
            communication.Subject ?? communication.RelatedJobNumber ?? communication.CustomerName,
            "Communication");
        group.FlowScore = Math.Max(group.FlowScore, 2);
        group.WorkflowType = string.IsNullOrWhiteSpace(group.WorkflowType) ? "Customer Conversation" : group.WorkflowType;
        group.Events.Add(new TimelineEventDto(
            TimelineMoment(communication.OccurredAt, communication.UpdatedAtUtc, communication.CreatedAtUtc, communication.Id),
            "Communication",
            $"{communication.Direction} · {communication.Channel}",
            communication.Subject ?? $"{communication.Channel} with {communication.CustomerName}",
            communication.Summary,
            null,
            "communications",
            communication.Id,
            FirstFilled(communication.RelatedInvoiceNumber, communication.RelatedOrderNumber, communication.RelatedJobNumber),
            communication.SourceProof,
            communication.NeedsReview));
        if (communication.FollowUpStatus.Equals("Open", StringComparison.OrdinalIgnoreCase))
        {
            AddMissing(group, communication.FollowUpDate.HasValue
                ? $"Communication follow-up due {communication.FollowUpDate:yyyy-MM-dd}"
                : "Communication follow-up is open");
        }
    }

    foreach (var queueItem in printerQueue)
    {
        var group = GetTimelineGroup(
            groups,
            aliases,
            queueItem.CustomerName,
            queueItem.RelatedInvoiceNumber,
            queueItem.RelatedOrderNumber,
            queueItem.JobName,
            "Printer Queue");
        group.FlowScore = Math.Max(group.FlowScore, 3);
        group.WorkflowType = string.IsNullOrWhiteSpace(group.WorkflowType) ? "Production Flow" : group.WorkflowType;
        var timing = queueItem.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)
            ? queueItem.CompletedAt
            : queueItem.Status.Equals("Printing", StringComparison.OrdinalIgnoreCase)
                ? queueItem.StartedAt
                : queueItem.ScheduledStart ?? queueItem.QueueDate;
        group.Events.Add(new TimelineEventDto(
            TimelineMoment(timing, queueItem.UpdatedAtUtc, queueItem.CreatedAtUtc, queueItem.Id),
            "Printer Queue",
            queueItem.Status,
            queueItem.JobName,
            $"{queueItem.PrinterName ?? "Unassigned printer"} · {queueItem.Material ?? "Material TBD"} {queueItem.Color ?? string.Empty} · {queueItem.ProgressPercent:0}% complete",
            null,
            "printerQueue",
            queueItem.Id,
            FirstFilled(queueItem.RelatedInvoiceNumber, queueItem.RelatedOrderNumber),
            queueItem.SourceProof,
            queueItem.NeedsReview || queueItem.Status.Equals("Needs Attention", StringComparison.OrdinalIgnoreCase)));
        if (queueItem.Status is "Queued" or "Ready" or "Printing" or "Paused" or "Needs Attention")
        {
            AddMissing(group, $"Production is {queueItem.Status}");
        }
    }

    foreach (var expense in expenses)
    {
        var reference = FirstFilled(expense.Description, expense.ReceiptProof, $"Expense #{expense.Id}");
        var group = GetTimelineGroup(groups, aliases, expense.VendorName, reference, null, expense.Description, "Expense");
        group.FlowScore = Math.Max(group.FlowScore, 2);
        group.WorkflowType = "Money Spent Flow";
        group.SellingCosts += expense.Category.Contains("Shipping", StringComparison.OrdinalIgnoreCase) || expense.Category.Contains("Marketing", StringComparison.OrdinalIgnoreCase)
            ? expense.Total ?? expense.Amount ?? 0
            : 0;
        group.Events.Add(new TimelineEventDto(TimelineMoment(expense.ExpenseDate, expense.UpdatedAtUtc, expense.CreatedAtUtc, expense.Id), "Expense", expense.DeductibleStatus, expense.Description,
            $"{expense.VendorName} · {expense.Category}", expense.Total ?? expense.Amount, "expenses", expense.Id, expense.Description, expense.ReceiptProof, expense.NeedsReview));
        if (string.IsNullOrWhiteSpace(expense.ReceiptProof)) AddMissing(group, "Expense needs receipt proof");
        if (expense.NeedsReview || expense.DeductibleStatus == "Review" || expense.TaxBucket == "Review") AddMissing(group, "Expense tax/category needs review");
    }

    foreach (var proof in auditDocs)
    {
        var group = GetTimelineGroup(groups, aliases, null, proof.RelatedRecordNumber, proof.RelatedRecordNumber, proof.FileName, "Proof");
        group.WorkflowType = string.IsNullOrWhiteSpace(group.WorkflowType) ? "Proof Inbox" : group.WorkflowType;
        group.Events.Add(new TimelineEventDto(TimelineMoment(proof.DocumentDate, proof.UpdatedAtUtc, proof.CreatedAtUtc, proof.Id), "Proof", proof.NeedsReview ? "Needs Review" : "Indexed", proof.FileName,
            proof.RelatedRecordType ?? "Audit document", null, "auditDocs", proof.Id, proof.RelatedRecordNumber, proof.FilePathOrUrl, proof.NeedsReview));
        if (proof.NeedsReview) AddMissing(group, $"Review proof: {proof.FileName}");
    }

    foreach (var action in actions)
    {
        var group = GetTimelineGroup(groups, aliases, null, action.RelatedRecord, action.RelatedRecord, action.Title, "Action");
        group.WorkflowType = string.IsNullOrWhiteSpace(group.WorkflowType) ? "Action Queue" : group.WorkflowType;
        var actionTimeLabel = action.DueDate.HasValue ? null : "cleanup";
        var actionExactTimeLabel = action.DueDate.HasValue ? null : "no due date";
        group.Events.Add(new TimelineEventDto(TimelineMoment(action.DueDate, action.UpdatedAtUtc, action.CreatedAtUtc, action.Id), "Action", action.Priority, action.Title,
            action.Notes ?? action.Area, null, "actions", action.Id, action.RelatedRecord, null, true, actionTimeLabel, actionExactTimeLabel));
        AddMissing(group, $"Cleanup task: {action.Title}");
    }

    var timelines = groups.Values
        .Select(FinalizeTimeline)
        .Where(g => MatchesTimeline(g, query, customerFilter))
        .OrderByDescending(g => g.LastActivity)
        .ThenByDescending(g => g.FlowScore)
        .ThenBy(g => g.Title)
        .Take(80)
        .ToList();

    var customers = groups.Values
        .Select(g => g.CustomerName)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(s => s)
        .ToList();

    return new
    {
        summary = new
        {
            count = timelines.Count,
            openIssues = timelines.Sum(g => g.Missing.Count),
            estimatedProfit = timelines.Sum(g => g.EstimatedProfit),
            openActions = timelines.Sum(g => g.Events.Count(e => e.Kind == "Action"))
        },
        customers,
        timelines
    };
}

static TimelineGroupDto FinalizeTimeline(TimelineGroupDto group)
{
    group.Events = group.Events.OrderByDescending(e => e.Date).ThenBy(e => e.Kind).ToList();
    group.LastActivity = group.Events.FirstOrDefault()?.Date ?? DateTime.MinValue;
    group.EstimatedProfit = group.GrossReceipts - group.TaxMemo - group.SellingCosts - group.EstimatedCogs;
    group.Subtitle = string.Join(" / ", group.Events.Select(e => e.Reference).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct().Take(4));
    group.Status = TimelineFlowStatus(group);
    if (group.Invoiced > 0 || group.Paid > 0)
    {
        group.Missing.RemoveAll(m => m.StartsWith("Job still Quoted", StringComparison.OrdinalIgnoreCase));
    }
    group.Missing = group.Missing.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s).ToList();
    return group;
}

static string TimelineFlowStatus(TimelineGroupDto group)
{
    if (group.Events.Any(e => e.Status.Equals("Void", StringComparison.OrdinalIgnoreCase) || e.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase))) return "Void";
    if (group.Paid > 0 && (group.Invoiced <= 0 || group.Paid >= group.Invoiced - 0.01m)) return "Paid";
    if (group.Paid > 0) return "Partial";

    var invoiceStatus = group.Events
        .Where(e => e.Kind is "INVOICE" or "AR Invoice")
        .Select(e => e.Status)
        .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
    if (!string.IsNullOrWhiteSpace(invoiceStatus)) return invoiceStatus;

    var estimateStatus = group.Events
        .Where(e => e.Kind == "ESTIMATE")
        .Select(e => e.Status)
        .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
    if (!string.IsNullOrWhiteSpace(estimateStatus)) return estimateStatus;

    var jobStatus = group.Events
        .Where(e => e.Kind == "Job")
        .Select(e => e.Status)
        .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
    return string.IsNullOrWhiteSpace(jobStatus) ? group.Status : jobStatus;
}

static bool MatchesTimeline(TimelineGroupDto group, string query, string customer)
{
    if (!string.IsNullOrWhiteSpace(customer) && !group.CustomerName.Contains(customer, StringComparison.OrdinalIgnoreCase)) return false;
    if (string.IsNullOrWhiteSpace(query)) return true;
    return JsonSerializer.Serialize(group).Contains(query, StringComparison.OrdinalIgnoreCase);
}

static bool IsOrphanedUnifiedInvoiceLedgerRow(string? sourceProof, string? invoiceNumber, HashSet<string> activeDocNumbers)
{
    return !string.IsNullOrWhiteSpace(sourceProof)
        && sourceProof.StartsWith("Unified invoice ", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(invoiceNumber)
        && !activeDocNumbers.Contains(invoiceNumber);
}

static bool IsOrphanedUnifiedEstimateJob(string? sourceProof, string? relatedNumber, HashSet<string> activeDocNumbers)
{
    return !string.IsNullOrWhiteSpace(sourceProof)
        && sourceProof.StartsWith("Unified estimate ", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(relatedNumber)
        && !activeDocNumbers.Contains(relatedNumber);
}

static TimelineGroupDto GetTimelineGroup(Dictionary<string, TimelineGroupDto> groups, Dictionary<string, string> aliases, string? customer, string? invoice, string? order, string? name, string fallback)
{
    var keys = BuildTimelineKeys(customer, invoice, order, name, fallback);
    foreach (var candidate in keys)
    {
        if (aliases.TryGetValue(candidate, out var targetKey) && groups.TryGetValue(targetKey, out var aliasedGroup))
        {
            AddTimelineAliases(aliases, keys, aliasedGroup.Key);
            return aliasedGroup;
        }
        if (groups.TryGetValue(candidate, out var directGroup))
        {
            AddTimelineAliases(aliases, keys, directGroup.Key);
            return directGroup;
        }
    }

    var cleanCustomer = FirstFilled(customer);
    var cleanName = FirstFilled(name);
    if (!string.IsNullOrWhiteSpace(cleanCustomer) && IsMeaningfulTimelineName(cleanName, FirstFilled(invoice, order)))
    {
        var nearMatch = groups.Values.FirstOrDefault(g =>
            !string.IsNullOrWhiteSpace(g.CustomerName)
            && string.Equals(g.CustomerName, cleanCustomer, StringComparison.OrdinalIgnoreCase)
            && TimelineNamesAreNearMatch(g.Title, cleanName!));
        if (nearMatch is not null)
        {
            AddTimelineAliases(aliases, keys, nearMatch.Key);
            return nearMatch;
        }
    }

    var reference = FirstFilled(invoice, order);
    var key = keys[0];
    if (!groups.TryGetValue(key, out var group))
    {
        group = new TimelineGroupDto
        {
            Key = key,
            Title = FirstFilled(name, customer, reference, fallback) ?? fallback,
            CustomerName = customer ?? string.Empty,
            Status = "Open"
        };
        groups[key] = group;
    }
    AddTimelineAliases(aliases, keys, group.Key);
    if (string.IsNullOrWhiteSpace(group.CustomerName) && !string.IsNullOrWhiteSpace(customer)) group.CustomerName = customer;
    if (!string.IsNullOrWhiteSpace(name) && (group.Title == fallback || group.Title == group.CustomerName)) group.Title = name;
    return group;
}

static bool TimelineNamesAreNearMatch(string? left, string right)
{
    var a = TimelineNameTokens(left);
    var b = TimelineNameTokens(right);
    if (a.Count == 0 || b.Count == 0)
    {
        return false;
    }

    var overlap = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
    var smaller = Math.Min(a.Count, b.Count);
    var larger = Math.Max(a.Count, b.Count);
    return overlap >= 2 && overlap / (decimal)smaller >= 0.72m && overlap / (decimal)larger >= 0.45m;
}

static HashSet<string> TimelineNameTokens(string? value)
{
    var stop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "for", "of", "the", "to", "with", "custom", "set", "new", "invoice", "estimate"
    };
    return Regex.Matches(value ?? string.Empty, @"[a-z0-9]{2,}", RegexOptions.IgnoreCase)
        .Select(m => m.Value.ToLowerInvariant())
        .Where(token => !stop.Contains(token))
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
}

static List<string> BuildTimelineKeys(string? customer, string? invoice, string? order, string? name, string fallback)
{
    var keys = new List<string>();
    var cleanCustomer = FirstFilled(customer);
    var cleanName = FirstFilled(name);
    var reference = FirstFilled(invoice, order);

    if (!string.IsNullOrWhiteSpace(cleanCustomer) && IsMeaningfulTimelineName(cleanName, reference))
    {
        keys.Add(NormalizeTimelineKey($"work:{cleanCustomer}:{cleanName}"));
    }
    if (!string.IsNullOrWhiteSpace(reference))
    {
        keys.Add(NormalizeTimelineKey($"ref:{reference}"));
    }
    if (!string.IsNullOrWhiteSpace(cleanCustomer))
    {
        keys.Add(NormalizeTimelineKey($"customer:{cleanCustomer}:{cleanName ?? fallback}"));
    }
    if (string.IsNullOrWhiteSpace(cleanCustomer))
    {
        keys.Add(NormalizeTimelineKey(cleanName ?? reference ?? fallback));
    }
    return keys.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}

static bool IsMeaningfulTimelineName(string? name, string? reference)
{
    if (string.IsNullOrWhiteSpace(name)) return false;
    var value = name.Trim();
    if (!string.IsNullOrWhiteSpace(reference) && value.Equals(reference.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
    if (Regex.IsMatch(value, @"^(EST|INV)-\d{4}-\d+", RegexOptions.IgnoreCase)) return false;
    return value != "—" && value != "-";
}

static void AddTimelineAliases(Dictionary<string, string> aliases, IEnumerable<string> keys, string targetKey)
{
    foreach (var key in keys)
    {
        if (!aliases.ContainsKey(key))
        {
            aliases[key] = targetKey;
        }
    }
}

static string NormalizeTimelineKey(string? value)
{
    var text = (value ?? "Unsorted").Trim().ToLowerInvariant();
    return Regex.Replace(text, @"\s+", " ");
}

static string? FirstFilled(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

static string PickBestStatus(string? current, string? incoming)
{
    if (string.IsNullOrWhiteSpace(incoming)) return current ?? "Open";
    if (incoming.Contains("Review", StringComparison.OrdinalIgnoreCase) || incoming.Contains("Overdue", StringComparison.OrdinalIgnoreCase)) return incoming;
    if (string.IsNullOrWhiteSpace(current) || current == "Open" || current == "Draft") return incoming;
    if (incoming is "In Progress" or "Sent" or "Partial" or "Open") return incoming;
    return current;
}

static void AddMissing(TimelineGroupDto group, string message)
{
    if (!string.IsNullOrWhiteSpace(message)) group.Missing.Add(message);
}

static void AddTimelineEstimate(TimelineGroupDto group, string? reference, decimal amount)
{
    if (amount <= 0) return;
    if (group.CountedEstimateRefs.Add(TimelineMoneyKey(reference, amount))) group.EstimatedValue += amount;
}

static void AddTimelineInvoiced(TimelineGroupDto group, string? reference, decimal amount)
{
    if (amount <= 0) return;
    if (group.CountedInvoiceRefs.Add(TimelineMoneyKey(reference, amount))) group.Invoiced += amount;
}

static void AddTimelinePaid(TimelineGroupDto group, string? reference, decimal amount)
{
    if (amount <= 0) return;
    if (group.CountedPaidRefs.Add(TimelineMoneyKey(reference, amount))) group.Paid += amount;
}

static string TimelineMoneyKey(string? reference, decimal amount)
{
    return string.IsNullOrWhiteSpace(reference)
        ? $"amount:{amount:0.00}"
        : NormalizeTimelineKey(reference);
}

static DateTime TimelineMoment(DateTime? businessDate, DateTime updatedAtUtc, DateTime createdAtUtc, int id)
{
    var clock = updatedAtUtc == default ? createdAtUtc : updatedAtUtc;
    if (businessDate is null)
    {
        return clock == default ? DateTime.Today.AddMinutes(id % 1440) : ToLocalClock(clock);
    }

    if (businessDate.Value.TimeOfDay != TimeSpan.Zero)
    {
        return businessDate.Value;
    }

    var localClock = ToLocalClock(clock);
    if (localClock != default)
    {
        return businessDate.Value.Date.Add(localClock.TimeOfDay);
    }

    return businessDate.Value.Date.AddHours(9).AddMinutes(id % 360);
}

static DateTime TimelineDocumentMoment(DateTime? businessDate, string? updatedAt, string? createdAt, int id)
{
    var clock = ParseDateTime(updatedAt) ?? ParseDateTime(createdAt);
    if (businessDate is null)
    {
        return clock is null ? DateTime.Today.AddMinutes(id % 1440) : ToLocalClock(clock.Value);
    }

    if (businessDate.Value.TimeOfDay != TimeSpan.Zero)
    {
        return businessDate.Value;
    }

    if (clock is not null)
    {
        return businessDate.Value.Date.Add(ToLocalClock(clock.Value).TimeOfDay);
    }

    return businessDate.Value.Date.AddHours(9).AddMinutes(id % 360);
}

static DateTime? ParseDateTime(string? value)
{
    return DateTime.TryParse(value, out var parsed) ? parsed : null;
}

static DateTime ToLocalClock(DateTime clock)
{
    if (clock == default) return clock;
    return clock.Kind == DateTimeKind.Local
        ? clock
        : DateTime.SpecifyKind(clock, DateTimeKind.Utc).ToLocalTime();
}

static bool IsInvoiceDocumentRelatedArea(string? value)
{
    return value?.Equals("Invoice", StringComparison.OrdinalIgnoreCase) == true
        || value?.Equals("Estimate", StringComparison.OrdinalIgnoreCase) == true;
}

static async Task<InvoiceDocumentIntakeResult> ImportInvoiceDocumentFromAuditAsync(
    AppDbContext db,
    InvoiceDocumentPdfDraftService draftService,
    AuditDocument auditDocument,
    AiSourceDocumentExtraction extraction,
    string sourcePath,
    string requestedType,
    CancellationToken cancellationToken)
{
    if (string.IsNullOrWhiteSpace(extraction.Text))
    {
        throw new InvalidOperationException(extraction.Warning
            ?? "No readable text was found. Scanned/image-only documents need OCR before their fields can be mapped.");
    }

    var sourceHash = await ComputeFileSha256Async(sourcePath, cancellationToken);
    IReadOnlyCollection<string> sourceWarnings = string.IsNullOrWhiteSpace(extraction.Warning)
        ? []
        : [extraction.Warning];
    var draft = await draftService.CreateDraftAsync(
        extraction.Text,
        auditDocument.FileName,
        sourceWarnings,
        cancellationToken,
        requestedType);
    var prefill = draft.Prefill;
    var documentType = prefill.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? "INVOICE" : "ESTIMATE";
    var sourceDocumentNumber = NormalizeImportedDocumentNumber(prefill.DocNumber);
    var submittedDocumentNumber = NormalizeImportedDocumentNumber(auditDocument.RelatedRecordNumber);
    // The proof is the source of truth. A typed related number may help only when
    // the PDF does not carry its own unambiguous EST/INV identity.
    var documentNumber = DocumentNumberMatchesType(sourceDocumentNumber, documentType)
        ? sourceDocumentNumber
        : DocumentNumberMatchesType(submittedDocumentNumber, documentType)
            ? submittedDocumentNumber
            : null;

    await InvoiceDocumentMutationLocks.Gate.WaitAsync(cancellationToken);
    try
    {
        InvoiceDocument? existing = null;
        if (!string.IsNullOrWhiteSpace(documentNumber))
        {
            var documentIdentity = InvoiceDocumentIdentityKey(documentNumber)!;
            existing = await db.InvoiceDocuments
                .Include(document => document.LineItems.OrderBy(line => line.SortOrder))
                .FirstOrDefaultAsync(
                    document => !document.IsArchived
                        && document.DocNumber != null
                        && document.DocNumber.Trim().ToUpper() == documentIdentity,
                    cancellationToken);
        }

        if (existing is null)
        {
            var hashMarker = $"\"sourceSha256\":\"{sourceHash}\"";
            existing = await db.InvoiceDocuments
                .Include(document => document.LineItems.OrderBy(line => line.SortOrder))
                .FirstOrDefaultAsync(
                    document => !document.IsArchived
                        && document.DocType == documentType
                        && document.Json.Contains(hashMarker),
                    cancellationToken);
        }

        if (existing is not null)
        {
            LinkAuditDocumentToInvoiceDocument(auditDocument, existing, draft.Warnings, "Linked existing populated record");
            await db.SaveChangesAsync(cancellationToken);
            return new InvoiceDocumentIntakeResult(
                "LinkedExisting",
                existing,
                auditDocument,
                $"Linked the proof to existing {FriendlyInvoiceDocumentType(existing.DocType)} {existing.DocNumber}; no duplicate record was created.",
                draft.Warnings,
                sourceDocumentNumber);
        }

        if (documentType == "INVOICE" && !string.IsNullOrWhiteSpace(documentNumber))
        {
            var documentIdentity = InvoiceDocumentIdentityKey(documentNumber)!;
            var matchingReceivable = await db.ReceivableInvoices.AsNoTracking().AnyAsync(
                invoice => !invoice.IsArchived
                    && invoice.InvoiceNumber.Trim().ToUpper() == documentIdentity,
                cancellationToken);
            var matchingSale = await db.Sales.AsNoTracking().AnyAsync(
                sale => !sale.IsArchived
                    && sale.IncludeInDashboard
                    && sale.Platform.ToUpper() == "DIRECT"
                    && sale.InvoiceNumber != null
                    && sale.InvoiceNumber.Trim().ToUpper() == documentIdentity,
                cancellationToken);
            if (matchingReceivable || matchingSale)
            {
                var conflictSources = string.Join(" and ", new[]
                {
                matchingReceivable ? "an existing receivable" : null,
                matchingSale ? "an existing direct sale" : null
            }.Where(value => value is not null));
                var warning = $"{documentNumber} already has {conflictSources} outside the estimate/invoice builder. Automatic import stopped so it cannot duplicate accounting. Reconcile that ledger row before importing this PDF as a builder invoice.";
                var warnings = draft.Warnings.Append(warning).ToList();
                auditDocument.DocumentDate = DateTime.TryParseExact(
                    prefill.DocDate,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var sourceDate)
                    ? sourceDate
                    : auditDocument.DocumentDate;
                auditDocument.DocumentType = "Invoice";
                auditDocument.RelatedRecordType = "Invoice";
                auditDocument.RelatedRecordNumber = documentNumber;
                auditDocument.NeedsReview = true;
                auditDocument.Notes = AppendNote(auditDocument.Notes, warning);
                auditDocument.UpdatedAtUtc = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                return new InvoiceDocumentIntakeResult(
                    "NeedsReview",
                    null,
                    auditDocument,
                    warning,
                    warnings,
                    sourceDocumentNumber);
            }
        }

        var projectNotes = (prefill.ProjectNotes ?? string.Empty).Replace(
            "Fixed local text-mapping rules prepared this draft. No database record was created by the import.",
            "Fixed local text-mapping rules created this record from Document Intake.",
            StringComparison.Ordinal);
        projectNotes = AppendNote(
            projectNotes,
            $"SOURCE PROOF: Audit Doc #{auditDocument.Id}, {auditDocument.FileName}.");
        if (!string.IsNullOrWhiteSpace(sourceDocumentNumber)
            && !string.Equals(sourceDocumentNumber, documentNumber, StringComparison.OrdinalIgnoreCase))
        {
            projectNotes = AppendNote(projectNotes, $"SOURCE PDF NUMBER: {sourceDocumentNumber}.");
        }

        var savedStatus = prefill.Status;
        var savedAmountPaid = prefill.AmountPaid;
        var importWarnings = draft.Warnings.ToList();
        if (documentType == "INVOICE"
            && (prefill.AmountPaid > 0
                || prefill.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase)
                || prefill.Status.Equals("Partial", StringComparison.OrdinalIgnoreCase)))
        {
            savedStatus = "Sent";
            savedAmountPaid = 0;
            var paymentWarning = $"The source PDF reports status {prefill.Status} and amount paid {prefill.AmountPaid.ToString("C", CultureInfo.GetCultureInfo("en-US"))}. The imported invoice was saved as unpaid Sent so document intake cannot create duplicate cash income. Confirm payment from the ledger record before marking it paid.";
            projectNotes = AppendNote(projectNotes, $"SOURCE PDF PAYMENT: Status {prefill.Status}; amount paid {prefill.AmountPaid.ToString("0.00", CultureInfo.InvariantCulture)}. {paymentWarning}");
            importWarnings.Add(paymentWarning);
        }

        var json = JsonSerializer.Serialize(new
        {
            version = 3,
            savedAt = DateTimeOffset.UtcNow,
            documentIntake = new
            {
                auditDocumentId = auditDocument.Id,
                sourceFileName = auditDocument.FileName,
                sourceSha256 = sourceHash,
                sourceDocumentNumber,
                requestedType,
                detectedType = documentType,
                sourceStatus = prefill.Status,
                sourceAmountPaid = prefill.AmountPaid,
                savedStatus,
                savedAmountPaid,
                mapper = draft.Provider
            }
        });
        var lineItems = prefill.LineItems.Select((line, index) => new InvoiceLineItemRequest(
            null,
            index + 1,
            line.Description,
            line.Details,
            line.Quantity,
            line.Rate,
            line.Quantity * line.Rate)).ToList();
        var request = new SaveInvoiceDocumentRequest(
            DocNumber: documentNumber,
            DocType: documentType,
            Status: savedStatus,
            CustomerName: prefill.CustomerName,
            CustomerPhone: prefill.CustomerPhone,
            CustomerAddress: prefill.CustomerAddress,
            CustomerEmail: prefill.CustomerEmail,
            PreparedFor: prefill.PreparedFor,
            ProjectName: prefill.ProjectName,
            Material: prefill.Material,
            Color: prefill.Color,
            Infill: prefill.Infill,
            ProjectDescription: prefill.ProjectDescription,
            ProjectNotes: projectNotes,
            PageSize: prefill.PageSize,
            DocDate: prefill.DocDate,
            DueDate: prefill.DueDate,
            Subtotal: draft.Pricing.LineSubtotal,
            DiscountAmount: draft.Pricing.Discount,
            RushAmount: draft.Pricing.RushAmount,
            TaxAmount: draft.Pricing.TaxAmount,
            Total: draft.Pricing.Total,
            AmountPaid: savedAmountPaid,
            Balance: Math.Max(0, draft.Pricing.Total - savedAmountPaid),
            PaymentMethod: prefill.PaymentMethod,
            PricingGuide: prefill.PricingGuide,
            TermsNotes: prefill.TermsNotes,
            StandardTurnaround: prefill.StandardTurnaround,
            RushTurnaround: prefill.RushTurnaround,
            CalcGrams: prefill.CalcGrams,
            CalcHours: prefill.CalcHours,
            CalcDesignHours: prefill.CalcDesignHours,
            CalcSetupFee: prefill.CalcSetupFee,
            CalcPostFee: prefill.CalcPostFee,
            CalcGramRate: prefill.CalcGramRate,
            CalcHourRate: prefill.CalcHourRate,
            CalcDesignRate: prefill.CalcDesignRate,
            CalcMinimum: prefill.CalcMinimum,
            CalcDifficulty: prefill.CalcDifficulty,
            CalcRush: prefill.DocRushPercent,
            CalcDiscount: prefill.DocDiscount,
            CalcTaxRate: prefill.DocTaxRate,
            LineItems: lineItems,
            Json: json);

        var created = await CreateInvoiceDocumentUnderLockAsync(
            db,
            request,
            cancellationToken,
            document => LinkAuditDocumentToInvoiceDocument(
                auditDocument,
                document,
                importWarnings,
                "Created and linked populated record"));
        return new InvoiceDocumentIntakeResult(
            "Created",
            created,
            auditDocument,
            $"Created populated {FriendlyInvoiceDocumentType(created.DocType)} {created.DocNumber} and linked the proof.",
            importWarnings,
            sourceDocumentNumber);
    }
    finally
    {
        InvoiceDocumentMutationLocks.Gate.Release();
    }
}

static void LinkAuditDocumentToInvoiceDocument(
    AuditDocument auditDocument,
    InvoiceDocument invoiceDocument,
    IReadOnlyCollection<string> warnings,
    string action)
{
    var type = FriendlyInvoiceDocumentType(invoiceDocument.DocType);
    auditDocument.DocumentDate = DateTime.TryParseExact(
        invoiceDocument.DocDate,
        "yyyy-MM-dd",
        CultureInfo.InvariantCulture,
        DateTimeStyles.None,
        out var documentDate)
        ? documentDate
        : auditDocument.DocumentDate;
    auditDocument.DocumentType = type;
    auditDocument.RelatedRecordType = type;
    auditDocument.RelatedRecordNumber = invoiceDocument.DocNumber;
    auditDocument.NeedsReview = false;
    auditDocument.Notes = AppendNote(
        auditDocument.Notes,
        $"{action}: {type} {invoiceDocument.DocNumber}. {warnings.Count} parser warning(s) retained on the upload result.");
    auditDocument.UpdatedAtUtc = DateTime.UtcNow;
}

static object ToInvoiceDocumentIntakeDto(InvoiceDocumentIntakeResult result) => new
{
    result.Action,
    invoiceDocument = result.InvoiceDocument is null ? null : ToInvoiceDocumentDto(result.InvoiceDocument),
    result.AuditDocument,
    result.Message,
    result.Warnings,
    result.SourceDocumentNumber
};

static string FriendlyInvoiceDocumentType(string? documentType)
{
    return documentType?.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) == true ? "Invoice" : "Estimate";
}

static bool DocumentNumberMatchesType(string? documentNumber, string documentType)
{
    var prefix = documentType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? "INV-" : "EST-";
    return documentNumber?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;
}

static string? NormalizeImportedDocumentNumber(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var normalized = Regex.Replace(value.Trim().ToUpperInvariant(), @"\s+", "-");
    return Regex.IsMatch(normalized, @"^(?:INV|EST)-\d{4}-\d{4}$") ? normalized : null;
}

static async Task<string> ComputeFileSha256Async(string path, CancellationToken cancellationToken)
{
    await using var stream = System.IO.File.OpenRead(path);
    return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
}

static string BuildUploadFingerprint(
    string contentHash,
    string fileName,
    string? relatedType,
    string? relatedNumber)
{
    var identity = string.Join('\n',
        contentHash.Trim().ToUpperInvariant(),
        NormalizeUploadFileName(fileName),
        NormalizeUploadIdentityPart(relatedType),
        NormalizeUploadIdentityPart(relatedNumber));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
}

static async Task<AuditDocument?> FindAndClaimLegacyDuplicateUploadAsync(
    AppDbContext db,
    IWebHostEnvironment env,
    string contentHash,
    string uploadFingerprint,
    string fileName,
    string? relatedType,
    string? relatedNumber,
    CancellationToken cancellationToken)
{
    var normalizedFileName = NormalizeUploadFileName(fileName);
    var normalizedRelatedType = NormalizeUploadIdentityPart(relatedType);
    var normalizedRelatedNumber = NormalizeUploadIdentityPart(relatedNumber);
    var candidates = await db.AuditDocuments
        .Where(document => !document.IsArchived
            && (document.UploadFingerprint == null || document.UploadFingerprint == string.Empty)
            && document.FilePathOrUrl != null
            && document.FilePathOrUrl != string.Empty)
        .OrderBy(document => document.Id)
        .ToListAsync(cancellationToken);

    foreach (var candidate in candidates)
    {
        if (!NormalizeUploadFileName(candidate.FileName).Equals(normalizedFileName, StringComparison.Ordinal)
            || !NormalizeUploadIdentityPart(candidate.RelatedRecordType).Equals(normalizedRelatedType, StringComparison.Ordinal)
            || !NormalizeUploadIdentityPart(candidate.RelatedRecordNumber).Equals(normalizedRelatedNumber, StringComparison.Ordinal))
        {
            continue;
        }

        try
        {
            if (!TryGetExistingAllowedProofFilePath(
                    env.ContentRootPath,
                    candidate.FilePathOrUrl,
                    out var candidatePath))
            {
                continue;
            }

            var candidateHash = await ComputeFileSha256Async(candidatePath, cancellationToken);
            if (!candidateHash.Equals(contentHash, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            candidate.UploadFingerprint = uploadFingerprint;
            candidate.UpdatedAtUtc = DateTime.UtcNow;
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return candidate;
            }
            catch (DbUpdateException ex) when (IsSqliteConstraintViolation(ex))
            {
                db.ChangeTracker.Clear();
                var concurrentlyClaimed = await db.AuditDocuments.AsNoTracking().FirstOrDefaultAsync(
                    document => !document.IsArchived && document.UploadFingerprint == uploadFingerprint,
                    cancellationToken);
                if (concurrentlyClaimed is not null)
                {
                    return concurrentlyClaimed;
                }

                throw;
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or ArgumentException
            or NotSupportedException)
        {
            // A stale or inaccessible legacy path cannot prove identity. Leave that
            // row untouched and keep checking other matching legacy candidates.
        }
    }

    return null;
}

static string NormalizeUploadFileName(string fileName) =>
    MakeSafeFileName(fileName).Trim().ToUpperInvariant();

static string NormalizeUploadIdentityPart(string? value) =>
    (value ?? string.Empty).Trim().ToUpperInvariant();

static bool TryGetExistingAllowedProofFilePath(
    string contentRootPath,
    string? savedPath,
    out string fullPath)
{
    fullPath = string.Empty;
    if (string.IsNullOrWhiteSpace(savedPath))
    {
        return false;
    }

    try
    {
        fullPath = Path.IsPathFullyQualified(savedPath)
            ? Path.GetFullPath(savedPath)
            : Path.GetFullPath(Path.Combine(contentRootPath, savedPath));
        return IsAllowedProofFilePath(contentRootPath, fullPath)
            && System.IO.File.Exists(fullPath);
    }
    catch (Exception ex) when (ex is IOException
        or UnauthorizedAccessException
        or ArgumentException
        or NotSupportedException)
    {
        fullPath = string.Empty;
        return false;
    }
}

static async Task<AuditDocument> ReloadAuditDocumentAfterFailedMutationAsync(
    AppDbContext db,
    int auditDocumentId,
    CancellationToken cancellationToken)
{
    db.ChangeTracker.Clear();
    return await db.AuditDocuments.FirstAsync(
        document => document.Id == auditDocumentId,
        cancellationToken);
}

static void ReplaceAuditDocument(List<AuditDocument> documents, AuditDocument replacement)
{
    var index = documents.FindIndex(document => document.Id == replacement.Id);
    if (index >= 0)
    {
        documents[index] = replacement;
    }
}

static void TryDeleteFile(string path)
{
    try
    {
        if (System.IO.File.Exists(path))
        {
            System.IO.File.Delete(path);
        }
    }
    catch
    {
        // Best-effort cleanup. A retained database row is never pointed at a file
        // that this method is asked to remove.
    }
}

static async Task DeleteProofFileIfUnreferencedAsync(AppDbContext db, string path)
{
    try
    {
        // A cancelled SaveChangesAsync can race with the SQLite commit. Confirm the
        // row is absent before removing its file so a committed proof never becomes
        // a broken database link. If the check itself fails, preserving an orphaned
        // file is safer than deleting evidence that may already be referenced.
        var referenced = await db.AuditDocuments.AsNoTracking().AnyAsync(
            document => document.FilePathOrUrl == path,
            CancellationToken.None);
        if (!referenced)
        {
            TryDeleteFile(path);
        }
    }
    catch
    {
        // Keep the staged file when database state cannot be established safely.
    }
}

static object BuildInboxSuggestion(AuditDocument doc)
{
    var raw = $"{doc.FileName}\n{doc.DocumentType}\n{doc.RelatedRecordType}\n{doc.RelatedRecordNumber}\n{doc.Notes}";
    var text = raw.ToLowerInvariant();
    var proof = doc.FilePathOrUrl ?? doc.FileName;
    var amount = TryFindMoney(text);
    var date = TryFindDocumentDate(raw);
    var orderNumber = FirstFilled(doc.RelatedRecordNumber, TryFindOrderNumber(raw));
    var invoiceNumber = TryFindInvoiceNumber(raw);
    var trackingNumber = TryFindTrackingNumber(raw);
    var vendor = GuessVendor(text, doc.FileName);
    var product = GuessProductName(raw, doc.FileName);

    var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["sale"] = 0,
        ["expense"] = 0,
        ["asset"] = 0,
        ["invoice"] = 0,
        ["estimate"] = 0,
        ["shipping"] = 0,
        ["bill"] = 0
    };
    var reasons = new List<string>();

    ScoreWhen(scores, reasons, "sale", 45, doc.RelatedRecordType?.Equals("Sale", StringComparison.OrdinalIgnoreCase) == true, "You selected Related Area = Sale.");
    ScoreWhen(scores, reasons, "expense", 45, doc.RelatedRecordType?.Equals("Expense", StringComparison.OrdinalIgnoreCase) == true, "You selected Related Area = Expense.");
    ScoreWhen(scores, reasons, "asset", 55, doc.RelatedRecordType?.Equals("Asset", StringComparison.OrdinalIgnoreCase) == true, "You selected Related Area = Asset.");
    ScoreWhen(scores, reasons, "estimate", 55, doc.RelatedRecordType?.Equals("Estimate", StringComparison.OrdinalIgnoreCase) == true, "You selected Related Area = Estimate.");
    ScoreWhen(scores, reasons, "invoice", 45, doc.RelatedRecordType?.Equals("Invoice", StringComparison.OrdinalIgnoreCase) == true, "You selected Related Area = Invoice.");
    ScoreWhen(scores, reasons, "bill", 45, doc.RelatedRecordType?.Equals("Bill", StringComparison.OrdinalIgnoreCase) == true, "You selected Related Area = Bill.");
    ScoreWhen(scores, reasons, "sale", 55, HasAny(text, "etsy order", "payment for order", "customer paid", "sale", "buyer paid", "order total"), "Looks like customer money or an order.");
    ScoreWhen(scores, reasons, "expense", 35, HasAny(text, "receipt", "purchase", "paid to", "charged", "expense", "invoice paid"), "Looks like a paid purchase/receipt.");
    ScoreWhen(scores, reasons, "expense", 40, HasAny(text, "etsy ads", "advertising", "marketing", "click-through", "listing fee", "processing fee", "transaction fee"), "Looks like marketplace fees or advertising.");
    ScoreWhen(scores, reasons, "shipping", 45, HasAny(text, "shipping label", "usps", "postage", "tracking", "ship by"), "Looks like shipping/postage.");
    ScoreWhen(scores, reasons, "invoice", 55, HasAny(text, "invoice", "amount due", "balance due", "payment due"), "Looks like an invoice.");
    ScoreWhen(scores, reasons, "estimate", 35, HasAny(text, "estimate", "quote", "proposal", "valid until"), "Looks like an estimate/quote.");
    ScoreWhen(scores, reasons, "asset", 60, HasAny(text, "printer", "ams", "bambu", "x1c", "p1s", "a1 mini", "equipment", "serial"), "Looks like equipment or durable business property.");
    ScoreWhen(scores, reasons, "bill", 50, HasAny(text, "vendor bill", "supplier bill", "bill from", "payment terms", "net 30", "supplier statement"), "Looks like a vendor bill or statement.");
    ScoreWhen(scores, reasons, "bill", 30, HasAny(text, "due date", "net 30", "amount due", "unpaid", "statement"), "Looks like money owed to a vendor.");
    ScoreWhen(scores, reasons, "sale", 15, !string.IsNullOrWhiteSpace(orderNumber) && !HasAny(text, "receipt", "purchase"), $"Found order/reference number {orderNumber}.");
    ScoreWhen(scores, reasons, "expense", 10, amount.HasValue && HasAny(text, "tax", "subtotal", "total"), "Found money fields that often appear on receipts.");

    var winner = scores.OrderByDescending(kv => kv.Value).First();
    var lane = winner.Value < 25 ? "Review" : winner.Key;
    var confidence = winner.Value >= 75 ? "High" : winner.Value >= 45 ? "Medium" : "Low";
    var result = BuildSuggestionForLane(lane, text, proof, amount, date, orderNumber, invoiceNumber, trackingNumber, vendor, product);
    var nextSteps = BuildSuggestionNextSteps(lane);

    return new
    {
        auditDocumentId = doc.Id,
        doc.FileName,
        lane = result.Lane,
        title = result.Title,
        detail = result.Detail,
        suggestedRoute = result.Route,
        suggestedConfig = result.Config,
        suggestedKind = result.Kind,
        suggestedAmount = amount,
        relatedRecordType = doc.RelatedRecordType,
        relatedRecordNumber = doc.RelatedRecordNumber,
        proofReference = proof,
        engine = "Local rules",
        usedAi = false,
        touches = "Uploaded filename, selected related area/reference, and locally extracted text patterns.",
        doesNot = "Does not create or save the suggested ledger record.",
        confidence,
        reasons = reasons.Distinct().Take(5).ToList(),
        extracted = new
        {
            amount,
            date,
            orderNumber,
            invoiceNumber,
            trackingNumber,
            vendor,
            product
        },
        suggestedPrefill = result.Prefill,
        nextSteps
    };
}

static InboxSuggestionLane BuildSuggestionForLane(string lane, string text, string proof, decimal? amount, string? date, string? orderNumber, string? invoiceNumber, string? trackingNumber, string? vendor, string? product)
{
    var today = DateTime.Today.ToString("yyyy-MM-dd");
    return lane switch
    {
        "sale" => new InboxSuggestionLane(
            "Sale",
            text.Contains("etsy") ? "Likely Etsy sale" : "Likely paid sale",
            "Create a Sale row, confirm money received, then review fees, shipping label cost, sales-tax memo, and COGS before tax export.",
            "sales",
            "sales",
            text.Contains("etsy") ? "etsy" : "directPaid",
            new Dictionary<string, object?>
            {
                ["saleDate"] = date ?? today,
                ["platform"] = text.Contains("etsy") ? "Etsy" : "Direct",
                ["status"] = "Paid",
                ["orderNumber"] = orderNumber,
                ["invoiceNumber"] = invoiceNumber,
                ["productName"] = product,
                ["customerPaid"] = amount,
                ["itemSales"] = amount,
                ["trackingNumber"] = trackingNumber,
                ["sourceProof"] = proof,
                ["includeInDashboard"] = true,
                ["needsReview"] = true,
                ["notes"] = "Created from Ledger Assistant suggestion. Verify fees, shipping label cost, sales tax memo, and estimated COGS."
            }),
        "expense" => new InboxSuggestionLane(
            text.Contains("etsy ads") || text.Contains("advertising") || text.Contains("marketing") ? "Expense: Marketing" : "Expense",
            text.Contains("etsy ads") ? "Likely Etsy ads expense" : "Likely paid expense",
            "Create an Expense row, confirm vendor/category/tax bucket, and keep Needs Review until deductible treatment is checked.",
            "expenses",
            "expenses",
            "expense",
            new Dictionary<string, object?>
            {
                ["expenseDate"] = date ?? today,
                ["vendorName"] = vendor,
                ["category"] = text.Contains("ads") || text.Contains("marketing") || text.Contains("advertising") ? "Advertising" : GuessExpenseCategory(text),
                ["description"] = product ?? "Uploaded receipt/proof",
                ["amount"] = amount,
                ["total"] = amount,
                ["taxBucket"] = text.Contains("filament") || text.Contains("material") ? "COGS/Materials" : "Operating Expense",
                ["deductibleStatus"] = "Review",
                ["businessUsePercent"] = 100,
                ["countedExpense"] = true,
                ["receiptProof"] = proof,
                ["needsReview"] = true,
                ["notes"] = "Created from Ledger Assistant suggestion. Verify category, sales tax, and deductible treatment before filing."
            }),
        "asset" => new InboxSuggestionLane(
            "Asset",
            "Likely equipment/asset purchase",
            "Create an Asset row instead of a normal expense if this is a printer, AMS, computer, durable tool, or high-value equipment.",
            "assets",
            "assets",
            "assetPurchase",
            new Dictionary<string, object?>
            {
                ["purchaseDate"] = date ?? today,
                ["inServiceDate"] = date ?? today,
                ["vendorName"] = vendor,
                ["name"] = product ?? "Equipment purchase",
                ["category"] = "Equipment",
                ["cost"] = amount,
                ["businessUsePercent"] = 100,
                ["taxTreatment"] = "Review",
                ["countedExpenseThisYear"] = false,
                ["sourceProof"] = proof,
                ["needsReview"] = true,
                ["notes"] = "Created from Ledger Assistant suggestion. Review Section 179, de minimis, or depreciation treatment before filing."
            }),
        "invoice" => new InboxSuggestionLane(
            "Invoice",
            "Likely invoice proof",
            "Create a draft Invoice PDF from the extracted fields, review it, then save only if it looks right. Use AR after it is sent or if money is owed.",
            "invoices",
            null,
            null,
            new Dictionary<string, object?>
            {
                ["docType"] = "INVOICE",
                ["docNumber"] = invoiceNumber ?? orderNumber,
                ["docDate"] = date ?? today,
                ["dueDate"] = date ?? today,
                ["projectName"] = product,
                ["docStatus"] = amount.HasValue && text.Contains("paid") ? "Paid" : "Draft",
                ["amountPaid"] = text.Contains("paid") ? amount : 0,
                ["projectNotes"] = $"Draft built from uploaded proof. Verify every copied field before saving. Proof: {proof}",
                ["sourceProof"] = proof,
                ["lineItems"] = amount.HasValue
                    ? new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["description"] = product ?? "Uploaded invoice item",
                            ["details"] = "Copied from uploaded proof. Review description, quantity, rate, and total.",
                            ["quantity"] = 1,
                            ["rate"] = amount,
                            ["amount"] = amount
                        }
                    }
                    : Array.Empty<Dictionary<string, object?>>()
            }),
        "estimate" => new InboxSuggestionLane(
            "Estimate",
            "Likely estimate/quote proof",
            "Create a draft Estimate from the extracted fields, review it, then save only if you want this old quote tracked in the app.",
            "estimates",
            null,
            null,
            new Dictionary<string, object?>
            {
                ["docType"] = "ESTIMATE",
                ["docNumber"] = invoiceNumber ?? orderNumber,
                ["docDate"] = date ?? today,
                ["dueDate"] = DateTime.TryParse(date, out var estimateDate) ? estimateDate.AddDays(14).ToString("yyyy-MM-dd") : DateTime.Today.AddDays(14).ToString("yyyy-MM-dd"),
                ["docStatus"] = "Draft",
                ["projectName"] = product,
                ["projectNotes"] = $"Draft built from uploaded proof. Verify every copied field before saving. Proof: {proof}",
                ["sourceProof"] = proof,
                ["lineItems"] = amount.HasValue
                    ? new[]
                    {
                        new Dictionary<string, object?>
                        {
                            ["description"] = product ?? "Uploaded estimate item",
                            ["details"] = "Copied from uploaded proof. Review description, quantity, rate, and total.",
                            ["quantity"] = 1,
                            ["rate"] = amount,
                            ["amount"] = amount
                        }
                    }
                    : Array.Empty<Dictionary<string, object?>>()
            }),
        "shipping" => new InboxSuggestionLane(
            "Shipping / Sale Cost",
            "Likely shipping label or postage",
            "If this belongs to an order, add it to that Sale's Shipping Label Cost. If it is general postage, record it as an Expense.",
            "sales",
            "sales",
            text.Contains("etsy") ? "etsy" : "directPaid",
            new Dictionary<string, object?>
            {
                ["saleDate"] = date ?? today,
                ["platform"] = text.Contains("etsy") ? "Etsy" : "Direct",
                ["orderNumber"] = orderNumber,
                ["shippingLabelCost"] = amount,
                ["trackingNumber"] = trackingNumber,
                ["sourceProof"] = proof,
                ["needsReview"] = true,
                ["notes"] = "Shipping proof imported by Ledger Assistant. Link this to the matching sale/order."
            }),
        "bill" => new InboxSuggestionLane(
            "Bill / AP",
            "Likely unpaid vendor bill",
            "Create a Bill/AP row if this proves money owed but not yet paid.",
            "bills",
            "bills",
            "bill",
            new Dictionary<string, object?>
            {
                ["billDate"] = date ?? today,
                ["vendorName"] = vendor,
                ["description"] = product ?? "Uploaded vendor bill",
                ["total"] = amount,
                ["amount"] = amount,
                ["status"] = "Unpaid",
                ["sourceProof"] = proof,
                ["needsReview"] = true
            }),
        _ => new InboxSuggestionLane(
            "Review",
            "Proof indexed, needs a human choice",
            "The file is stored as an Audit Doc. Choose whether it proves a Sale, Expense, Invoice, Bill, Customer Job, Product, Asset, or business document.",
            "documentIntake",
            null,
            null,
            new Dictionary<string, object?>())
    };
}

static List<string> BuildSuggestionNextSteps(string lane)
{
    return lane switch
    {
        "sale" => ["Confirm customer paid amount.", "Enter Etsy/payment fees and label cost if applicable.", "Enter estimated COGS before tax export."],
        "expense" => ["Confirm vendor/category.", "Choose COGS, operating expense, asset, or memo-only tax bucket.", "Keep proof attached."],
        "asset" => ["Confirm cost and in-service date.", "Review tax treatment before filing.", "Keep receipt and serial/warranty details."],
        "shipping" => ["Match to the sale/order.", "Enter label cost on the sale.", "Do not count customer shipping charged as label cost."],
        "invoice" => ["Confirm sent/paid status.", "Match or create AR.", "Attach proof and mark paid only when money arrived."],
        _ => ["Open the Audit Doc.", "Decide what business record it proves.", "Create or attach the related record manually."]
    };
}

static void ScoreWhen(Dictionary<string, int> scores, List<string> reasons, string key, int points, bool condition, string reason)
{
    if (!condition) return;
    scores[key] = scores.GetValueOrDefault(key) + points;
    reasons.Add(reason);
}

static bool HasAny(string text, params string[] needles)
{
    return needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
}

static string? TryFindDocumentDate(string text)
{
    var labeled = Regex.Match(text, @"\b(?:date|paid on|order date|invoice date|purchase date)\s*[:#-]?\s*(\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}-\d{1,2}-\d{1,2}|[A-Z][a-z]{2,8}\s+\d{1,2},\s+\d{4})", RegexOptions.IgnoreCase);
    if (labeled.Success && DateTime.TryParse(labeled.Groups[1].Value, out var labeledDate))
    {
        return labeledDate.ToString("yyyy-MM-dd");
    }

    var any = Regex.Match(text, @"\b(\d{1,2}[/-]\d{1,2}[/-]\d{2,4}|\d{4}-\d{1,2}-\d{1,2})\b");
    return any.Success && DateTime.TryParse(any.Groups[1].Value, out var date) ? date.ToString("yyyy-MM-dd") : null;
}

static string? TryFindOrderNumber(string text)
{
    var match = Regex.Match(text, @"\b(?:order|order\s*#|order\s*number|etsy\s*order)\s*[:#-]?\s*([A-Z0-9-]{5,})", RegexOptions.IgnoreCase);
    return match.Success ? match.Groups[1].Value.Trim() : null;
}

static string? TryFindInvoiceNumber(string text)
{
    var match = Regex.Match(text, @"\b(?:invoice|inv|invoice\s*#|invoice\s*number)\s*[:#-]?\s*((?:INV|EST)?-?\d{4}-?\d{3,}|[A-Z0-9-]{5,})", RegexOptions.IgnoreCase);
    return match.Success ? match.Groups[1].Value.Trim() : null;
}

static string? TryFindTrackingNumber(string text)
{
    var labeled = Regex.Match(text, @"\b(?:tracking|tracking\s*#|label)\s*[:#-]?\s*([A-Z0-9]{10,34})", RegexOptions.IgnoreCase);
    if (labeled.Success) return labeled.Groups[1].Value.Trim();

    var usps = Regex.Match(text, @"\b(9[2345]\d{20,24})\b");
    return usps.Success ? usps.Groups[1].Value.Trim() : null;
}

static string? GuessVendor(string text, string fileName)
{
    var vendors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["etsy"] = "Etsy",
        ["amazon"] = "Amazon",
        ["bambu"] = "Bambu Lab",
        ["micro center"] = "Micro Center",
        ["usps"] = "USPS",
        ["paypal"] = "PayPal",
        ["stripe"] = "Stripe",
        ["makerworld"] = "MakerWorld",
        ["home depot"] = "Home Depot",
        ["lowes"] = "Lowe's"
    };
    foreach (var vendor in vendors)
    {
        if (text.Contains(vendor.Key, StringComparison.OrdinalIgnoreCase) || fileName.Contains(vendor.Key, StringComparison.OrdinalIgnoreCase))
        {
            return vendor.Value;
        }
    }

    var soldBy = Regex.Match(text, @"\b(?:sold by|vendor|merchant|from)\s*[:#-]?\s*([A-Za-z0-9 &.'-]{3,60})", RegexOptions.IgnoreCase);
    return soldBy.Success ? soldBy.Groups[1].Value.Trim() : null;
}

static string? GuessProductName(string raw, string fileName)
{
    var labeled = Regex.Match(raw, @"\b(?:item|product|description|title|for)\s*[:#-]?\s*([A-Za-z0-9 &.'()/+-]{5,100})", RegexOptions.IgnoreCase);
    if (labeled.Success)
    {
        return labeled.Groups[1].Value.Trim();
    }

    var name = Path.GetFileNameWithoutExtension(fileName)
        .Replace("_", " ")
        .Replace("-", " ")
        .Trim();
    name = Regex.Replace(name, @"^\d{8,}[\s-]*[a-f0-9]{4,}[\s-]*", string.Empty, RegexOptions.IgnoreCase);
    return string.IsNullOrWhiteSpace(name) ? null : name;
}

static string GuessExpenseCategory(string text)
{
    if (HasAny(text, "filament", "pla", "petg", "abs", "asa", "resin", "material")) return "Filament / Material";
    if (HasAny(text, "shipping", "usps", "postage", "label")) return "Shipping / Postage";
    if (HasAny(text, "box", "packaging", "bubble", "tape", "mailer")) return "Packaging";
    if (HasAny(text, "fee", "processing", "transaction", "listing")) return "Marketplace Fees";
    if (HasAny(text, "software", "subscription", "saas")) return "Software";
    if (HasAny(text, "tool", "nozzle", "caliper", "glue", "knife")) return "Tools";
    if (HasAny(text, "printer", "ams", "equipment")) return "Equipment";
    if (HasAny(text, "ads", "advertising", "marketing")) return "Advertising";
    return "General Business";
}

static decimal? TryFindMoney(string text)
{
    var explicitDollar = Regex.Match(text, @"(?<![\w-])\$\s*([0-9]{1,5}(?:,[0-9]{3})*(?:\.[0-9]{2})?|[0-9]{1,5}\.[0-9]{2})(?![\w-])");
    if (explicitDollar.Success && TryParseMoneyCandidate(explicitDollar.Groups[1].Value, out var dollarValue))
    {
        return dollarValue;
    }

    var labeledAmount = Regex.Match(text, @"\b(?:total|amount|paid|charge|cost|price|fee|tax|balance|subtotal)\s*[:=]?\s*([0-9]{1,5}(?:,[0-9]{3})*\.[0-9]{2}|[0-9]{1,5}\.[0-9]{2})\b");
    return labeledAmount.Success && TryParseMoneyCandidate(labeledAmount.Groups[1].Value, out var labeledValue)
        ? labeledValue
        : null;
}

static bool TryParseMoneyCandidate(string raw, out decimal value)
{
    return decimal.TryParse(raw.Replace(",", string.Empty), out value)
        && value > 0
        && value < 100000;
}

static async Task<object> BuildTaxAuditAsync(AppDbContext db)
{
    var issues = new List<TaxAuditIssue>();
    var sales = await db.Sales.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var invoices = await db.ReceivableInvoices.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var jobs = await db.CustomerJobs.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var docs = await db.InvoiceDocuments.AsNoTracking().ToListAsync();
    var expenses = await db.Expenses.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var assets = await db.Assets.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var rewards = await db.MakerWorldRewards.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var bills = await db.Bills.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();

    if (expenses.Count == 0)
    {
        issues.Add(new("High", "No expenses entered", "Expenses",
            "Tax Prep cannot be complete until business purchases from receipts are entered and classified."));
    }

    foreach (var sale in sales.Where(MoneyRules.IsReportableSale))
    {
        var expectedPaid = MoneyRules.SaleGrossReceipts(sale) + MoneyRules.SaleSalesTaxMemo(sale);
        var actualPaid = MoneyRules.SaleCustomerPaid(sale);
        if (actualPaid > 0 && Math.Abs(expectedPaid - actualPaid) > 0.02m)
        {
            issues.Add(new("High", "Sale paid-total mismatch", sale.InvoiceNumber ?? sale.OrderNumber ?? $"Sale #{sale.Id}",
                $"CustomerPaid {actualPaid:C} does not equal item + shipping - refunds + sales tax {expectedPaid:C}."));
        }
    }

    foreach (var invoice in invoices)
    {
        var expectedTotal = (invoice.Subtotal ?? 0) - (invoice.Discount ?? 0) + (invoice.RushFee ?? 0) + (invoice.SalesTax ?? 0);
        if ((invoice.InvoiceTotal ?? 0) > 0 && Math.Abs(expectedTotal - (invoice.InvoiceTotal ?? 0)) > 0.02m)
        {
            issues.Add(new("High", "AR invoice total mismatch", invoice.InvoiceNumber,
                $"InvoiceTotal {(invoice.InvoiceTotal ?? 0):C} does not equal subtotal - discount + rush + tax {expectedTotal:C}."));
        }

        var sale = sales.FirstOrDefault(x =>
            x.Platform.Equals("Direct", StringComparison.OrdinalIgnoreCase)
            && x.InvoiceNumber == invoice.InvoiceNumber
            && x.IncludeInDashboard
            && (string.Equals(x.CustomerName, invoice.CustomerName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(x.SourceProof, $"Unified invoice {invoice.InvoiceNumber}", StringComparison.OrdinalIgnoreCase)));
        var paid = invoice.AmountPaid ?? 0;
        if (paid <= 0 && sale is not null && MoneyRules.IsReportableSale(sale))
        {
            issues.Add(new("Critical", "Unpaid invoice has reportable sale", invoice.InvoiceNumber,
                "This invoice shows no payment, but a matching Direct Sale is still included in dashboard/tax totals."));
        }
        else if (paid > 0 && sale is null && !invoice.Status.Equals("Void", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("High", "Paid invoice missing sale", invoice.InvoiceNumber,
                "This invoice has AmountPaid, but no matching included Direct Sale was found."));
        }

        if (!string.IsNullOrWhiteSpace(invoice.SourceProof))
        {
            var proofSale = sales.FirstOrDefault(x =>
                !string.IsNullOrWhiteSpace(x.SourceProof)
                && string.Equals(x.SourceProof, invoice.SourceProof, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(x.CustomerName, invoice.CustomerName, StringComparison.OrdinalIgnoreCase));
            if (proofSale is not null)
            {
                issues.Add(new("High", "Invoice proof belongs to another customer", invoice.InvoiceNumber,
                    $"Invoice proof matches sale customer {proofSale.CustomerName}, but this AR row is for {invoice.CustomerName}."));
            }
        }
    }

    foreach (var directSale in sales.Where(x =>
        MoneyRules.IsReportableSale(x)
        && x.Platform.Equals("Direct", StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(x.InvoiceNumber)
        && (x.CustomerPaid ?? 0) > 0))
    {
        var matchingAr = invoices.Any(x =>
            x.InvoiceNumber == directSale.InvoiceNumber
            && string.Equals(x.CustomerName, directSale.CustomerName, StringComparison.OrdinalIgnoreCase));
        if (!matchingAr)
        {
            issues.Add(new("High", "Direct paid sale missing AR history", directSale.InvoiceNumber!,
                "The sale counts as income, but no same-customer AR invoice row exists for invoice history/proof review."));
        }
    }

    foreach (var group in sales.Where(x => x.Platform.Equals("Direct", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.InvoiceNumber))
        .GroupBy(x => x.InvoiceNumber!)
        .Where(g => g.Select(x => x.CustomerName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
    {
        issues.Add(new("High", "Duplicate direct invoice number across customers", group.Key,
            "Invoice numbers should be unique for direct sales/invoices. Duplicate numbers can cause income matching errors."));
    }

    foreach (var group in invoices.Where(x => !string.IsNullOrWhiteSpace(x.InvoiceNumber))
        .GroupBy(x => x.InvoiceNumber)
        .Where(g => g.Select(x => x.CustomerName).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
    {
        issues.Add(new("High", "Duplicate AR invoice number across customers", group.Key,
            "AR invoice numbers should be unique. Use Original PDF Invoice # for duplicate/wrong source PDFs."));
    }

    var docNumbers = docs.Select(x => x.DocNumber).Where(x => !string.IsNullOrWhiteSpace(x)).ToHashSet(StringComparer.OrdinalIgnoreCase);
    foreach (var invoice in invoices.Where(x => (x.SourceProof ?? string.Empty).StartsWith("Unified invoice ", StringComparison.OrdinalIgnoreCase) && !docNumbers.Contains(x.InvoiceNumber)))
    {
        issues.Add(new("High", "Orphaned AR invoice", invoice.InvoiceNumber,
            "This AR row says it came from the unified invoice builder, but the source document no longer exists."));
    }

    foreach (var sale in sales.Where(x => (x.SourceProof ?? string.Empty).StartsWith("Unified invoice ", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.InvoiceNumber) && !docNumbers.Contains(x.InvoiceNumber)))
    {
        issues.Add(new("High", "Orphaned direct sale", sale.InvoiceNumber!,
            "This Sale row says it came from the unified invoice builder, but the source document no longer exists."));
    }

    foreach (var job in jobs.Where(x => (x.SourceProof ?? string.Empty).StartsWith("Unified estimate ", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(x.RelatedInvoiceNumber) && !docNumbers.Contains(x.RelatedInvoiceNumber)))
    {
        issues.Add(new("High", "Orphaned customer job", job.RelatedInvoiceNumber!,
            "This Customer Job says it came from the unified estimate builder, but the source document no longer exists."));
    }

    var docLineItems = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems)
        .ToListAsync();
    foreach (var doc in docLineItems)
    {
        var lineSubtotal = doc.LineItems.Sum(x => Math.Max(0, x.Quantity) * Math.Max(0, x.Rate));
        if (doc.LineItems.Count > 0 && Math.Abs(lineSubtotal - doc.Subtotal) > 0.02m)
        {
            issues.Add(new("High", "Document subtotal does not match line items", doc.DocNumber ?? $"Document #{doc.Id}",
                $"Subtotal {doc.Subtotal:C} does not equal line item total {lineSubtotal:C}."));
        }

        var expectedTotal = doc.Subtotal - doc.DiscountAmount + doc.RushAmount + doc.TaxAmount;
        if (Math.Abs(expectedTotal - doc.Total) > 0.02m)
        {
            issues.Add(new("High", "Invoice document total mismatch", doc.DocNumber ?? $"Invoice document #{doc.Id}",
                $"Total {doc.Total:C} does not equal subtotal - discount + rush + tax {expectedTotal:C}."));
        }

        var expectedBalance = Math.Max(0, doc.Total - doc.AmountPaid);
        if (doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) && Math.Abs(expectedBalance - doc.Balance) > 0.02m)
        {
            issues.Add(new("Medium", "Invoice document balance mismatch", doc.DocNumber ?? $"Invoice document #{doc.Id}",
                $"Balance {doc.Balance:C} does not equal total - amount paid {expectedBalance:C}."));
        }
    }

    foreach (var estimate in docs.Where(x => x.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase) && x.AmountPaid > 0))
    {
        issues.Add(new("Medium", "Estimate has Amount Paid", estimate.DocNumber ?? $"Estimate #{estimate.Id}",
            "Accepted estimates are not cash receipts. Create/save a paid invoice when money is actually received."));
    }

    foreach (var job in jobs.Where(x =>
        !string.IsNullOrWhiteSpace(x.RelatedInvoiceNumber)
        && x.RelatedInvoiceNumber.StartsWith("EST-", StringComparison.OrdinalIgnoreCase)
        && (x.AmountPaid ?? 0) > 0))
    {
        issues.Add(new("High", "Estimate job shows amount paid", job.RelatedInvoiceNumber!,
            "Customer Jobs linked to estimates should use Quote Amount only. Amount Paid belongs on a paid invoice or Sale row."));
    }

    foreach (var expense in expenses)
    {
        var expectedExpenseTotal = (expense.Amount ?? 0) + (expense.SalesTax ?? 0);
        if ((expense.Total ?? 0) > 0 && Math.Abs(expectedExpenseTotal - (expense.Total ?? 0)) > 0.02m)
        {
            issues.Add(new("High", "Expense total mismatch", expense.Description.Length > 0 ? expense.Description : $"Expense #{expense.Id}",
                $"Total {(expense.Total ?? 0):C} does not equal amount + sales tax {expectedExpenseTotal:C}."));
        }

        if (expense.NeedsReview || expense.DeductibleStatus.Equals("Review", StringComparison.OrdinalIgnoreCase) || expense.TaxBucket.Equals("Review", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("Medium", "Expense needs tax classification", expense.Description.Length > 0 ? expense.Description : $"Expense #{expense.Id}",
                "This expense is not counted as a deductible expense until tax bucket and deductibility are set intentionally."));
        }
    }

    foreach (var bill in bills)
    {
        var billLabel = !string.IsNullOrWhiteSpace(bill.BillNumber)
            ? bill.BillNumber
            : !string.IsNullOrWhiteSpace(bill.Description) ? bill.Description : $"Bill #{bill.Id}";
        var expectedBillTotal = (bill.Amount ?? 0) + (bill.SalesTax ?? 0);
        if ((bill.Total ?? 0) > 0 && Math.Abs(expectedBillTotal - (bill.Total ?? 0)) > 0.02m)
        {
            issues.Add(new("High", "AP bill total mismatch", billLabel,
                $"Total {(bill.Total ?? 0):C} does not equal amount + sales tax {expectedBillTotal:C}."));
        }

        var paid = bill.AmountPaid ?? 0;
        var total = bill.Total ?? bill.Amount ?? 0;
        if (bill.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase) && paid + 0.02m < total)
        {
            issues.Add(new("High", "Paid bill has unpaid balance", billLabel,
                $"Status is Paid, but AmountPaid {paid:C} is less than Total {total:C}."));
        }
        else if ((bill.Status.Equals("Unpaid", StringComparison.OrdinalIgnoreCase) || bill.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase)) && paid > 0.02m)
        {
            issues.Add(new("Medium", "Unpaid bill has payment amount", billLabel,
                $"Status is {bill.Status}, but AmountPaid is {paid:C}."));
        }

        if (bill.NeedsReview
            || (bill.TaxDeductible
                && MoneyRules.PaidBillAmount(bill) > 0
                && MoneyRules.BillDeductionBucket(bill).Equals("Review", StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new("Medium", "AP bill needs tax classification", billLabel,
                "Confirm the bill's tax category, deductibility, paid amount, and source proof before tax handoff."));
        }

        if (bill.TaxDeductible
            && MoneyRules.PaidBillAmount(bill) > 0
            && string.IsNullOrWhiteSpace(bill.SourceProof))
        {
            issues.Add(new("Medium", "Paid deductible bill missing proof", billLabel,
                "Paid deductible AP should link to the vendor invoice, receipt, statement, or payment proof."));
        }
    }

    foreach (var asset in assets)
    {
        if (asset.NeedsReview || asset.TaxTreatment.Equals("Review", StringComparison.OrdinalIgnoreCase) || asset.TaxTreatment.Equals("Depreciation", StringComparison.OrdinalIgnoreCase))
        {
            issues.Add(new("Medium", "Asset needs tax treatment review", asset.Name,
                "Assets are only counted automatically when marked Section 179 or De Minimis Expense and Expensed This Year."));
        }
    }

    foreach (var reward in rewards.Where(x => x.IncomeStatus.Equals("Review", StringComparison.OrdinalIgnoreCase)))
    {
        issues.Add(new("Medium", "MakerWorld reward income review", reward.SourceProof ?? $"Reward #{reward.Id}",
            "Set Income Status before filing so redeemed rewards are not missed or double-counted."));
    }

    return new
    {
        summary = new
        {
            reportableGrossReceipts = sales.Where(MoneyRules.IsReportableSale).Sum(MoneyRules.SaleGrossReceipts),
            customerPaidIncludingTaxMemo = sales.Where(MoneyRules.IsReportableSale).Sum(MoneyRules.SaleCustomerPaid),
            salesTaxMemo = sales.Where(MoneyRules.IsReportableSale).Sum(MoneyRules.SaleSalesTaxMemo),
            deductibleExpenses = expenses.Sum(MoneyRules.TaxCountedExpenseAmount) + bills.Sum(MoneyRules.TaxCountedBillAmount),
            expensedAssets = assets.Sum(MoneyRules.FullyExpensedAssetAmount),
            makerWorldIncome = rewards.Sum(MoneyRules.MakerWorldIncomeAmount),
            criticalIssues = issues.Count(x => x.Severity == "Critical"),
            highIssues = issues.Count(x => x.Severity == "High"),
            mediumIssues = issues.Count(x => x.Severity == "Medium")
        },
        issues
    };
}

static string ResolveConnectionString(IConfiguration configuration, string contentRootPath)
{
    var configured = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=Data/epata-business-ledger.db";
    var builder = new SqliteConnectionStringBuilder(configured);
    var dataSource = builder.DataSource;
    if (string.IsNullOrWhiteSpace(dataSource) || Path.IsPathRooted(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
    {
        return builder.ConnectionString;
    }

    var rootCandidate = Path.GetFullPath(Path.Combine(contentRootPath, dataSource));
    var parentCandidate = Path.GetFullPath(Path.Combine(contentRootPath, "..", dataSource));
    if (Path.GetFileName(contentRootPath).Equals("publish-win-x64", StringComparison.OrdinalIgnoreCase)
        && (File.Exists(parentCandidate) || Directory.Exists(Path.GetDirectoryName(parentCandidate))))
    {
        builder.DataSource = parentCandidate;
    }
    else
    {
        builder.DataSource = rootCandidate;
    }

    return builder.ConnectionString;
}

static string GetSqliteDataDirectory(IConfiguration configuration, string contentRootPath)
{
    var connectionString = ResolveConnectionString(configuration, contentRootPath);
    var builder = new SqliteConnectionStringBuilder(connectionString);
    var dataSource = builder.DataSource;
    if (string.IsNullOrWhiteSpace(dataSource) || dataSource.Equals(":memory:", StringComparison.OrdinalIgnoreCase))
    {
        return Path.Combine(contentRootPath, "Data");
    }

    return Path.GetDirectoryName(Path.GetFullPath(dataSource)) ?? Path.Combine(contentRootPath, "Data");
}

static async Task<AiOperationSourcePacket> ReadAiOperationSourcePacketAsync(
    HttpRequest request,
    AiSourceDocumentTextExtractor extractor,
    CancellationToken cancellationToken)
{
    if (!request.HasFormContentType)
    {
        throw new InvalidOperationException("AI source uploads must be sent as multipart/form-data.");
    }

    var form = await request.ReadFormAsync(cancellationToken);
    var sourceText = form["sourceText"].FirstOrDefault() ?? string.Empty;
    var sourceName = form["sourceName"].FirstOrDefault() ?? "Mixed sources";
    var urls = form["sourceUrls"]
        .SelectMany(value => value?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(AiEstimateService.MaxSourceUrls)
        .ToList();
    var images = new List<AiEstimateImageInput>();
    var warnings = new List<string>();
    var files = form.Files.Take(AiEstimateService.MaxUploadFiles).ToList();
    if (form.Files.Count > AiEstimateService.MaxUploadFiles)
    {
        warnings.Add($"Only the first {AiEstimateService.MaxUploadFiles} files were read.");
    }
    if (files.Sum(file => file.Length) > AiEstimateService.MaxTotalUploadBytes)
    {
        throw new InvalidOperationException($"The selected files total more than {AiEstimateService.MaxTotalUploadBytes / 1024 / 1024} MB.");
    }

    foreach (var file in files)
    {
        if (file.Length <= 0) continue;
        if (file.Length > AiEstimateService.MaxUploadFileBytes)
        {
            throw new InvalidOperationException($"{file.FileName} is larger than the {AiEstimateService.MaxUploadFileBytes / 1024 / 1024} MB per-file limit.");
        }

        if (file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            await using var memory = new MemoryStream();
            await file.CopyToAsync(memory, cancellationToken);
            images.Add(new AiEstimateImageInput(file.FileName, file.ContentType, Convert.ToBase64String(memory.ToArray())));
            continue;
        }

        try
        {
            var extraction = await extractor.ExtractAsync(file, cancellationToken);
            if (!string.IsNullOrWhiteSpace(extraction.Text))
            {
                sourceText = string.Join("\n\n", new[] { sourceText, $"SOURCE FILE: {file.FileName}\n{extraction.Text}" }.Where(x => !string.IsNullOrWhiteSpace(x)));
            }
            if (!string.IsNullOrWhiteSpace(extraction.Warning)) warnings.Add(extraction.Warning);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Xml.XmlException)
        {
            warnings.Add($"{file.FileName} could not be read as a valid {Path.GetExtension(file.FileName).TrimStart('.').ToUpperInvariant()} document.");
        }
    }

    if (sourceText.Length > AiEstimateService.MaxCombinedTextCharacters)
    {
        throw new InvalidOperationException($"The combined extracted text is too large. Keep it under {AiEstimateService.MaxCombinedTextCharacters:N0} characters.");
    }
    return new AiOperationSourcePacket(sourceText, urls, images, warnings, sourceName);
}

static TEntity? DeserializeCrudPayload<TEntity>(
    JsonElement payload,
    JsonSerializerOptions serializerOptions) where TEntity : AuditableEntity
{
    try
    {
        return payload.Deserialize<TEntity>(serializerOptions);
    }
    catch (JsonException)
    {
        return null;
    }
}

static void MapCrud<TEntity>(WebApplication app, string route) where TEntity : AuditableEntity
{
    var group = app.MapGroup($"/api/{route}");

    group.MapGet("", async (AppDbContext db, bool? includeArchived) =>
    {
        IQueryable<TEntity> query = db.Set<TEntity>().AsNoTracking();
        if (includeArchived != true)
        {
            query = query.Where(x => !x.IsArchived);
        }

        return Results.Ok(await query.OrderByDescending(x => x.UpdatedAtUtc).ThenByDescending(x => x.Id).ToListAsync());
    });

    group.MapGet("/{id:int}", async (int id, AppDbContext db) =>
    {
        var entity = await db.Set<TEntity>().FindAsync(id);
        return entity is null ? Results.NotFound() : Results.Ok(entity);
    });

    group.MapPost("", async Task<IResult> (
        JsonElement payload,
        AppDbContext db,
        Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        CancellationToken cancellationToken) =>
    {
        var entity = DeserializeCrudPayload<TEntity>(payload, jsonOptions.Value.SerializerOptions);
        if (entity is null)
        {
            return Results.BadRequest(new { message = $"The request body must contain a valid {typeof(TEntity).Name} JSON object." });
        }
        if (GeneratedRecordOwnershipMessage(entity) is { } ownershipMessage)
        {
            return Results.Conflict(new { message = ownershipMessage });
        }

        entity.Id = 0;
        if (entity is AuditDocument auditDocument)
        {
            auditDocument.UploadFingerprint = null;
        }
        entity.CreatedAtUtc = DateTime.UtcNow;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        if (entity is AppSetting)
        {
            entity.IsArchived = false;
        }
        NormalizeCrudEntity(entity);
        if (ValidateCrudEntity(entity) is { } validationProblem)
        {
            return validationProblem;
        }

        var mutationGate = CrudIdentityMutationGate(entity);
        if (mutationGate is not null)
        {
            await mutationGate.WaitAsync(cancellationToken);
        }
        try
        {
            if (!entity.IsArchived
                && await FindActiveCrudIdentityConflictAsync(db, entity, cancellationToken) is { } identityConflict)
            {
                return identityConflict;
            }

            db.Set<TEntity>().Add(entity);
            await ApplyCrudSideEffectsAsync(db, entity);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Created($"/api/{route}/{entity.Id}", entity);
        }
        finally
        {
            mutationGate?.Release();
        }
    });

    group.MapPut("/{id:int}", async Task<IResult> (
        int id,
        JsonElement payload,
        AppDbContext db,
        HttpRequest request,
        Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> jsonOptions,
        CancellationToken cancellationToken) =>
    {
        var input = DeserializeCrudPayload<TEntity>(payload, jsonOptions.Value.SerializerOptions);
        if (input is null)
        {
            return Results.BadRequest(new { message = $"The request body must contain a valid {typeof(TEntity).Name} JSON object." });
        }
        var existing = await db.Set<TEntity>().FindAsync(id);
        if (existing is null)
        {
            return Results.NotFound();
        }

        if (!TryReadExpectedUpdatedAt(request, out var expectedUpdatedAt))
        {
            return Results.BadRequest(new
            {
                message = "X-EPATA-Updated-At must be an ISO-8601 timestamp returned by this API. Refresh the record and try again."
            });
        }
        if (expectedUpdatedAt.HasValue
            && existing.UpdatedAtUtc != expectedUpdatedAt.Value)
        {
            return Results.Conflict(new
            {
                message = "This record changed after the page loaded it. Refresh the record, review the newer values, and try again. No fields were overwritten.",
                currentUpdatedAtUtc = existing.UpdatedAtUtc
            });
        }

        if (GeneratedRecordOwnershipMessage(existing) is { } existingOwnershipMessage)
        {
            return Results.Conflict(new { message = existingOwnershipMessage });
        }
        if (GeneratedRecordOwnershipMessage(input) is { } inputOwnershipMessage)
        {
            return Results.Conflict(new { message = inputOwnershipMessage });
        }

        var previousReceivableIdentity = ReceivableInvoiceSyncIdentity.Capture(existing);
        var previousQueueJobId = existing is PrinterQueueItem previousQueue ? previousQueue.CustomerJobId : null;
        var uploadFingerprint = existing is AuditDocument existingAuditDocument
            ? existingAuditDocument.UploadFingerprint
            : null;
        input.Id = id;
        input.CreatedAtUtc = existing.CreatedAtUtc;
        input.IsArchived = existing is AppSetting ? false : existing.IsArchived;
        NormalizeCrudEntity(input);
        if (ValidateCrudEntity(input) is { } validationProblem)
        {
            return validationProblem;
        }

        var mutationGate = CrudIdentityMutationGate(input);
        if (mutationGate is not null)
        {
            await mutationGate.WaitAsync(cancellationToken);
        }
        try
        {
            if (!input.IsArchived
                && await FindActiveCrudIdentityConflictAsync(db, input, cancellationToken) is { } identityConflict)
            {
                return identityConflict;
            }

            var created = existing.CreatedAtUtc;
            db.Entry(existing).CurrentValues.SetValues(input);
            existing.Id = id;
            existing.CreatedAtUtc = created;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            if (existing is AuditDocument auditDocument)
            {
                auditDocument.UploadFingerprint = uploadFingerprint;
            }
            NormalizeCrudEntity(existing);
            await ApplyCrudSideEffectsAsync(db, existing, previousReceivableIdentity, previousQueueJobId);
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(existing);
        }
        finally
        {
            mutationGate?.Release();
        }
    });

    group.MapDelete("/{id:int}", async Task<IResult> (int id, AppDbContext db) =>
    {
        var existing = await db.Set<TEntity>().FindAsync(id);
        if (existing is null)
        {
            return Results.NotFound();
        }

        if (GeneratedRecordOwnershipMessage(existing) is { } ownershipMessage)
        {
            return Results.Conflict(new { message = ownershipMessage });
        }
        if (existing is AppSetting)
        {
            return Results.Conflict(new
            {
                message = "Settings are live configuration and cannot be archived. Update the setting value through its configuration screen instead."
            });
        }
        if (existing.IsArchived)
        {
            return Results.NoContent();
        }

        var previousReceivableIdentity = ReceivableInvoiceSyncIdentity.Capture(existing);
        var previousQueueJobId = existing is PrinterQueueItem previousQueue ? previousQueue.CustomerJobId : null;
        existing.IsArchived = true;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await ApplyCrudSideEffectsAsync(db, existing, previousReceivableIdentity, previousQueueJobId);
        await db.SaveChangesAsync();
        return Results.NoContent();
    });

    group.MapPost("/{id:int}/restore", async Task<IResult> (
        int id,
        AppDbContext db,
        CancellationToken cancellationToken) =>
    {
        var existing = await db.Set<TEntity>().FirstOrDefaultAsync(
            entity => entity.Id == id,
            cancellationToken);
        if (existing is null)
        {
            return Results.NotFound();
        }

        if (GeneratedRecordOwnershipMessage(existing) is { } ownershipMessage)
        {
            return Results.Conflict(new { message = ownershipMessage });
        }
        if (existing is AppSetting)
        {
            return Results.Conflict(new
            {
                message = "Settings are live configuration and do not support archive or restore. Update the setting value through its configuration screen instead."
            });
        }
        if (!existing.IsArchived)
        {
            return Results.Ok(existing);
        }

        SemaphoreSlim? restoreGate = null;
        var restoreGateHeld = false;
        try
        {
            if (existing is AuditDocument { UploadFingerprint: { Length: > 0 } fingerprint })
            {
                restoreGate = UploadMutationLocks.For(fingerprint);
                await restoreGate.WaitAsync(cancellationToken);
                restoreGateHeld = true;
                var activeDuplicate = await db.AuditDocuments.AsNoTracking().FirstOrDefaultAsync(
                    document => document.Id != existing.Id
                        && !document.IsArchived
                        && document.UploadFingerprint == fingerprint,
                    cancellationToken);
                if (activeDuplicate is not null)
                {
                    return Results.Conflict(new
                    {
                        message = $"This archived proof is the same exact upload as active proof #{activeDuplicate.Id}. Archive the active proof first if you want to restore this older record.",
                        existingAuditDocumentId = activeDuplicate.Id
                    });
                }
            }
            else if (CrudIdentityMutationGate(existing) is { } identityGate)
            {
                restoreGate = identityGate;
                await restoreGate.WaitAsync(cancellationToken);
                restoreGateHeld = true;
                if (await FindActiveCrudIdentityConflictAsync(db, existing, cancellationToken) is { } identityConflict)
                {
                    return identityConflict;
                }
            }

            var previousReceivableIdentity = ReceivableInvoiceSyncIdentity.Capture(existing);
            var previousQueueJobId = existing is PrinterQueueItem previousQueue ? previousQueue.CustomerJobId : null;
            existing.IsArchived = false;
            existing.UpdatedAtUtc = DateTime.UtcNow;
            await ApplyCrudSideEffectsAsync(db, existing, previousReceivableIdentity, previousQueueJobId);
            if (existing is CustomerJob customerJob)
            {
                await RecomputeCustomerJobFromPrinterQueueAsync(db, customerJob, cancellationToken);
            }
            await db.SaveChangesAsync(cancellationToken);
            return Results.Ok(existing);
        }
        finally
        {
            if (restoreGateHeld)
            {
                restoreGate!.Release();
            }
        }
    });
}

static SemaphoreSlim? CrudIdentityMutationGate(AuditableEntity entity)
{
    return entity switch
    {
        Sale sale when MarketplaceMutationLocks.IdentityKey(sale.Platform, sale.OrderNumber) is { } identityKey =>
            MarketplaceMutationLocks.For(identityKey),
        ReceivableInvoice invoice when ReceivableInvoiceIdentity.IdentityKey(invoice.InvoiceNumber) is not null =>
            InvoiceDocumentMutationLocks.Gate,
        TaxObligation => TaxObligationMutationLocks.Gate,
        _ => null
    };
}

static async Task<IResult?> FindActiveCrudIdentityConflictAsync(
    AppDbContext db,
    AuditableEntity entity,
    CancellationToken cancellationToken)
{
    if (entity is Sale sale
        && MarketplaceMutationLocks.IdentityKey(sale.Platform, sale.OrderNumber) is { } marketplaceIdentityKey)
    {
        var activeDuplicateId = await FindActiveMarketplaceRestoreCollisionIdAsync(
            db,
            sale,
            marketplaceIdentityKey,
            cancellationToken);
        if (activeDuplicateId.HasValue)
        {
            return Results.Conflict(new
            {
                message = $"Sale #{activeDuplicateId.Value} already uses {sale.Platform} order {sale.OrderNumber}. Archive the active Sale before reusing this marketplace order identity.",
                existingSaleId = activeDuplicateId.Value
            });
        }
    }

    if (entity is ReceivableInvoice invoice
        && ReceivableInvoiceIdentity.IdentityKey(invoice.InvoiceNumber) is { } receivableIdentityKey)
    {
        var candidates = await db.ReceivableInvoices.AsNoTracking()
            .Where(candidate => candidate.Id != invoice.Id && !candidate.IsArchived)
            .Select(candidate => new { candidate.Id, candidate.InvoiceNumber })
            .ToListAsync(cancellationToken);
        var activeDuplicateId = candidates.FirstOrDefault(candidate =>
            ReceivableInvoiceIdentity.IdentityKey(candidate.InvoiceNumber) == receivableIdentityKey)?.Id;
        if (activeDuplicateId.HasValue)
        {
            return Results.Conflict(new
            {
                message = $"Receivable invoice #{activeDuplicateId.Value} already uses invoice number {invoice.InvoiceNumber}. Archive the active receivable before reusing this invoice identity.",
                existingReceivableInvoiceId = activeDuplicateId.Value
            });
        }
    }

    if (entity is TaxObligation obligation)
    {
        var obligationIdentityKey = TaxObligationMutationLocks.IdentityKey(
            obligation.TaxYear,
            obligation.Title,
            obligation.Period);
        var candidates = await db.TaxObligations.AsNoTracking()
            .Where(candidate => candidate.Id != obligation.Id
                && !candidate.IsArchived
                && candidate.TaxYear == obligation.TaxYear)
            .Select(candidate => new { candidate.Id, candidate.TaxYear, candidate.Title, candidate.Period })
            .ToListAsync(cancellationToken);
        var activeDuplicateId = candidates.FirstOrDefault(candidate =>
            TaxObligationMutationLocks.IdentityKey(candidate.TaxYear, candidate.Title, candidate.Period) == obligationIdentityKey)?.Id;
        if (activeDuplicateId.HasValue)
        {
            return Results.Conflict(new
            {
                message = $"Tax obligation #{activeDuplicateId.Value} already uses {obligation.TaxYear} / {obligation.Title} / {obligation.Period}. Keep the regenerated active obligation and leave this older record archived.",
                existingTaxObligationId = activeDuplicateId.Value
            });
        }
    }

    return null;
}

static async Task<int?> FindActiveMarketplaceRestoreCollisionIdAsync(
    AppDbContext db,
    AuditableEntity entity,
    string marketplaceIdentityKey,
    CancellationToken cancellationToken)
{
    if (entity is Sale sale)
    {
        var candidates = await db.Sales.AsNoTracking()
            .Where(candidate => candidate.Id != sale.Id && !candidate.IsArchived)
            .Select(candidate => new { candidate.Id, candidate.Platform, candidate.OrderNumber })
            .ToListAsync(cancellationToken);
        return candidates.FirstOrDefault(candidate =>
            MarketplaceMutationLocks.IdentityKey(candidate.Platform, candidate.OrderNumber) == marketplaceIdentityKey)?.Id;
    }

    return null;
}

static bool TryReadExpectedUpdatedAt(HttpRequest request, out DateTime? expectedUpdatedAt)
{
    expectedUpdatedAt = null;
    var value = request.Headers["X-EPATA-Updated-At"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(value))
    {
        return true;
    }

    if (!DateTime.TryParse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.RoundtripKind | DateTimeStyles.AllowWhiteSpaces,
        out var parsed))
    {
        return false;
    }

    expectedUpdatedAt = parsed;
    return true;
}

static bool TryReadExpectedInvoiceDocumentUpdatedAt(HttpRequest request, out string? expectedUpdatedAt)
{
    expectedUpdatedAt = request.Headers["X-EPATA-Updated-At"].FirstOrDefault()?.Trim();
    return string.IsNullOrWhiteSpace(expectedUpdatedAt)
        || DateTimeOffset.TryParse(
            expectedUpdatedAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out _);
}

static bool InvoiceDocumentUpdatedAtMatches(string currentUpdatedAt, string? expectedUpdatedAt)
{
    if (string.IsNullOrWhiteSpace(expectedUpdatedAt)
        || string.Equals(currentUpdatedAt, expectedUpdatedAt, StringComparison.Ordinal))
    {
        return true;
    }

    return DateTimeOffset.TryParse(
            currentUpdatedAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var current)
        && DateTimeOffset.TryParse(
            expectedUpdatedAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces,
            out var expected)
        && current.Equals(expected);
}

static IResult? ValidateCrudEntity(AuditableEntity entity)
{
    var validationResults = new List<ValidationResult>();
    if (Validator.TryValidateObject(entity, new ValidationContext(entity), validationResults, validateAllProperties: true))
    {
        return null;
    }

    var errors = validationResults
        .SelectMany(result => result.MemberNames.DefaultIfEmpty(string.Empty).Select(member => new
        {
            Member = string.IsNullOrWhiteSpace(member) ? "record" : char.ToLowerInvariant(member[0]) + member[1..],
            Message = result.ErrorMessage ?? "The supplied value is invalid."
        }))
        .GroupBy(item => item.Member, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(
            group => group.Key,
            group => group.Select(item => item.Message).Distinct(StringComparer.Ordinal).ToArray(),
            StringComparer.OrdinalIgnoreCase);
    return Results.ValidationProblem(errors, statusCode: StatusCodes.Status400BadRequest);
}

static IResult Csv(string fileName, string csv)
{
    return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
}

static string? GeneratedRecordOwnershipMessage(AuditableEntity entity)
{
    if (entity is ReceivableInvoice invoice
        && HasGeneratedSource(invoice.SourceProof, "Unified invoice "))
    {
        return "This receivable is generated from an Invoice Record. Edit, archive, or restore the source Invoice Record instead.";
    }

    if (entity is CustomerJob job
        && HasGeneratedSource(job.SourceProof, "Unified estimate "))
    {
        return "This customer job is generated from an Estimate Record. Edit, archive, or restore the source Estimate Record instead.";
    }

    if (entity is Sale sale)
    {
        if (HasGeneratedSource(sale.SourceProof, "Unified invoice "))
        {
            return "This sale is generated from an Invoice Record. Edit, archive, or restore the source Invoice Record instead.";
        }
        if (sale.SourceReceivableInvoiceId.HasValue
            || sale.Notes?.Contains("automatically", StringComparison.OrdinalIgnoreCase) == true
                && sale.Notes.Contains("receivable invoice", StringComparison.OrdinalIgnoreCase))
        {
            return "This sale is generated from a receivable invoice. Edit, archive, or restore the source receivable instead.";
        }
    }

    return null;
}

static bool HasGeneratedSource(string? sourceProof, string prefix) =>
    sourceProof?.TrimStart().StartsWith(prefix, StringComparison.OrdinalIgnoreCase) == true;

static string PaymentChannelFor(Sale sale)
{
    var method = (sale.PaymentMethod ?? string.Empty).Trim().ToLowerInvariant();
    var platform = (sale.Platform ?? string.Empty).Trim().ToLowerInvariant();
    if (method == "cash") return "Cash";
    if (new[] { "zelle", "venmo", "cash app", "paypal", "ach", "bank transfer", "wire" }.Any(method.Contains))
        return "Digital Transfer";
    if (new[] { "credit card", "debit card", "card", "square", "stripe" }.Any(method.Contains))
        return "Credit / Debit Card";
    if (new[] { "etsy", "makerworld", "online marketplace", "shopify", "ebay", "amazon", "website", "online" }.Any(method.Contains)
        || new[] { "etsy", "makerworld", "shopify", "ebay", "amazon", "online" }.Any(platform.Contains))
        return "Online Marketplace";
    if (!string.IsNullOrWhiteSpace(method) && method is not "unknown" and not "unknown / review")
        return "Other Non-Cash";
    return "Unknown / Review";
}

static bool PaymentChannelMatches(string paymentChannel, string? requested)
{
    return (requested ?? "all").Trim().ToLowerInvariant() switch
    {
        "" or "all" => true,
        "noncash" or "non-cash" => paymentChannel is not "Cash" and not "Unknown / Review",
        "digital" or "transfer" or "digital-transfer" => paymentChannel == "Digital Transfer",
        "card" or "credit-card" or "credit-debit-card" => paymentChannel == "Credit / Debit Card",
        "online" or "marketplace" or "online-marketplace" => paymentChannel == "Online Marketplace",
        "cash" => paymentChannel == "Cash",
        "unknown" or "review" => paymentChannel == "Unknown / Review",
        "other" or "other-noncash" or "other-non-cash" => paymentChannel == "Other Non-Cash",
        _ => true
    };
}

static string NormalizePaymentMethod(string? paymentMethod, string? platform = null)
{
    var method = (paymentMethod ?? string.Empty).Trim();
    var normalizedPlatform = (platform ?? string.Empty).Trim();
    if (string.IsNullOrWhiteSpace(method)
        || method.Equals("Unknown / Review", StringComparison.OrdinalIgnoreCase))
    {
        if (normalizedPlatform.Equals("Etsy", StringComparison.OrdinalIgnoreCase))
        {
            return "Etsy Payments";
        }

        if (normalizedPlatform.Contains("MakerWorld", StringComparison.OrdinalIgnoreCase))
        {
            return "MakerWorld";
        }

        return "Unknown / Review";
    }

    return method.Length > 80 ? method[..80] : method;
}

static void NormalizeCrudEntity<TEntity>(TEntity entity) where TEntity : AuditableEntity
{
    ApplyDefaultClockToBusinessDates(entity);

    switch (entity)
    {
        case Party party:
            party.Name ??= string.Empty;
            party.PartyType = string.IsNullOrWhiteSpace(party.PartyType) ? "Customer" : party.PartyType;
            break;
        case Bill bill:
            bill.VendorName ??= string.Empty;
            bill.Category = string.IsNullOrWhiteSpace(bill.Category) ? "General Business" : bill.Category;
            bill.Description ??= string.Empty;
            bill.Status = string.IsNullOrWhiteSpace(bill.Status) ? "Unpaid" : bill.Status;
            bill.Amount = ClampMoney(bill.Amount);
            bill.SalesTax = ClampMoney(bill.SalesTax);
            bill.Total = (bill.Amount ?? 0) + (bill.SalesTax ?? 0);
            bill.AmountPaid = ClampMoney(bill.AmountPaid);
            bill.PaymentMethod = NormalizePaymentMethod(bill.PaymentMethod);
            if (string.IsNullOrWhiteSpace(bill.TaxCategory)) bill.TaxCategory = "Other business expense";
            if (string.Equals(bill.Status, "Paid", StringComparison.OrdinalIgnoreCase) && (bill.AmountPaid ?? 0) <= 0 && (bill.Total ?? 0) > 0)
            {
                bill.AmountPaid = bill.Total;
            }
            break;
        case Expense expense:
            expense.VendorName ??= string.Empty;
            expense.Category = string.IsNullOrWhiteSpace(expense.Category) ? "General Business" : expense.Category;
            expense.Description ??= string.Empty;
            expense.TaxBucket = string.IsNullOrWhiteSpace(expense.TaxBucket) ? "Operating Expense" : expense.TaxBucket;
            expense.DeductibleStatus = string.IsNullOrWhiteSpace(expense.DeductibleStatus) ? "Yes" : expense.DeductibleStatus;
            expense.Amount = ClampMoney(expense.Amount);
            expense.SalesTax = ClampMoney(expense.SalesTax);
            expense.Total = (expense.Amount ?? 0) + (expense.SalesTax ?? 0);
            expense.BusinessUsePercent = ClampPercent(expense.BusinessUsePercent);
            expense.PaymentMethod = NormalizePaymentMethod(expense.PaymentMethod);
            if (string.IsNullOrWhiteSpace(expense.TaxCategory)) expense.TaxCategory = "Other business expense";
            break;
        case OrderLossIncident incident:
            incident.ProductName ??= string.Empty;
            incident.CustomerRefund = ClampMoney(incident.CustomerRefund);
            incident.ReplacementCogs = ClampMoney(incident.ReplacementCogs);
            incident.AdditionalShippingCost = ClampMoney(incident.AdditionalShippingCost);
            incident.OtherCost = ClampMoney(incident.OtherCost);
            incident.ReimbursementReceived = ClampMoney(incident.ReimbursementReceived);
            if (string.IsNullOrWhiteSpace(incident.Platform)) incident.Platform = "Other";
            if (string.IsNullOrWhiteSpace(incident.IncidentType)) incident.IncidentType = "Damaged in transit";
            if (string.IsNullOrWhiteSpace(incident.Resolution)) incident.Resolution = "Replacement / reship";
            if (string.IsNullOrWhiteSpace(incident.Status)) incident.Status = "Resolved";
            break;
        case ReceivableInvoice invoice:
            invoice.InvoiceNumber = (invoice.InvoiceNumber ?? string.Empty).Trim();
            invoice.CustomerName ??= string.Empty;
            invoice.Status = string.IsNullOrWhiteSpace(invoice.Status) ? "Draft" : invoice.Status;
            invoice.Subtotal = ClampMoney(invoice.Subtotal);
            invoice.Discount = ClampMoney(invoice.Discount);
            invoice.RushFee = ClampMoney(invoice.RushFee);
            invoice.SalesTax = ClampMoney(invoice.SalesTax);
            invoice.InvoiceTotal = Math.Max(0, (invoice.Subtotal ?? 0) - (invoice.Discount ?? 0) + (invoice.RushFee ?? 0) + (invoice.SalesTax ?? 0));
            invoice.AmountPaid = Math.Min(invoice.InvoiceTotal ?? 0, ClampMoney(invoice.AmountPaid) ?? 0);
            if (string.Equals(invoice.Status, "Paid", StringComparison.OrdinalIgnoreCase) && invoice.AmountPaid <= 0 && invoice.InvoiceTotal > 0)
            {
                invoice.AmountPaid = invoice.InvoiceTotal;
            }
            else if (string.Equals(invoice.Status, "Draft", StringComparison.OrdinalIgnoreCase)
                || string.Equals(invoice.Status, "Sent", StringComparison.OrdinalIgnoreCase)
                || string.Equals(invoice.Status, "Void", StringComparison.OrdinalIgnoreCase))
            {
                invoice.AmountPaid = 0;
            }
            invoice.PaymentMethod = NormalizePaymentMethod(invoice.PaymentMethod);
            invoice.IncludeInCashReports = !invoice.IsArchived
                && invoice.AmountPaid > 0
                && !string.Equals(invoice.Status, "Void", StringComparison.OrdinalIgnoreCase);
            if (string.Equals(invoice.Status, "Void", StringComparison.OrdinalIgnoreCase))
            {
                invoice.AmountPaid = 0;
                invoice.IncludeInCashReports = false;
            }
            break;
        case Sale sale:
            sale.CustomerName ??= string.Empty;
            sale.ProductName ??= string.Empty;
            sale.Status = string.IsNullOrWhiteSpace(sale.Status) ? "Paid" : sale.Status;
            sale.Platform = string.IsNullOrWhiteSpace(sale.Platform) ? "Direct" : sale.Platform.Trim();
            sale.OrderNumber = string.IsNullOrWhiteSpace(sale.OrderNumber) ? null : sale.OrderNumber.Trim();
            sale.Quantity = ClampMoney(sale.Quantity);
            sale.ItemSales = ClampMoney(sale.ItemSales);
            sale.ShippingCharged = ClampMoney(sale.ShippingCharged);
            sale.SalesTaxCollected = ClampMoney(sale.SalesTaxCollected);
            sale.PlatformFees = ClampMoney(sale.PlatformFees);
            sale.ShippingLabelCost = ClampMoney(sale.ShippingLabelCost);
            sale.Refunds = ClampMoney(sale.Refunds);
            sale.EstimatedCogs = ClampMoney(sale.EstimatedCogs);
            sale.CustomerPaid = ClampMoney(sale.CustomerPaid);
            var expectedCustomerPaid = MoneyRules.SaleGrossReceipts(sale) + MoneyRules.SaleSalesTaxMemo(sale);
            if (expectedCustomerPaid > 0 && !string.Equals(sale.Status, "Draft", StringComparison.OrdinalIgnoreCase))
            {
                sale.CustomerPaid = expectedCustomerPaid;
            }
            if (string.Equals(sale.Status, "Draft", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sale.Status, "Void", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sale.Status, "Cancelled", StringComparison.OrdinalIgnoreCase)
                || string.Equals(sale.Status, "Canceled", StringComparison.OrdinalIgnoreCase))
            {
                sale.IncludeInDashboard = false;
            }
            sale.PaymentMethod = NormalizePaymentMethod(sale.PaymentMethod, sale.Platform);
            if (string.IsNullOrWhiteSpace(sale.SalesTaxHandling))
            {
                sale.SalesTaxHandling = "Unknown / Review";
            }
            break;
        case TaxObligation obligation:
            obligation.Title = (obligation.Title ?? string.Empty).Trim();
            obligation.Period = string.IsNullOrWhiteSpace(obligation.Period) ? null : obligation.Period.Trim();
            obligation.Jurisdiction = string.IsNullOrWhiteSpace(obligation.Jurisdiction) ? "Federal" : obligation.Jurisdiction;
            obligation.ObligationType = string.IsNullOrWhiteSpace(obligation.ObligationType) ? "Other" : obligation.ObligationType;
            obligation.Status = string.IsNullOrWhiteSpace(obligation.Status) ? "Review Applicability" : obligation.Status;
            obligation.EstimatedAmount = ClampMoney(obligation.EstimatedAmount);
            obligation.AmountPaid = ClampMoney(obligation.AmountPaid);
            obligation.PaymentMethod = NormalizePaymentMethod(obligation.PaymentMethod);
            obligation.TaxYear = obligation.TaxYear is >= 2000 and <= 2200 ? obligation.TaxYear : DateTime.Today.Year;
            if (string.Equals(obligation.Status, "Filed / Paid", StringComparison.OrdinalIgnoreCase)
                || string.Equals(obligation.Status, "Not Required", StringComparison.OrdinalIgnoreCase))
            {
                obligation.NeedsReview = false;
            }
            break;
        case MileageLog mileage:
            mileage.BusinessPurpose ??= string.Empty;
            mileage.BusinessMiles = ClampMoney(mileage.BusinessMiles);
            mileage.ParkingAndTolls = ClampMoney(mileage.ParkingAndTolls);
            break;
        case CustomerJob job:
            job.CustomerName ??= string.Empty;
            job.Platform = string.IsNullOrWhiteSpace(job.Platform) ? "Direct" : job.Platform;
            job.JobName ??= string.Empty;
            job.JobType = string.IsNullOrWhiteSpace(job.JobType) ? "Print" : job.JobType;
            job.Status = string.IsNullOrWhiteSpace(job.Status) ? "Open" : job.Status;
            job.QuoteAmount = ClampMoney(job.QuoteAmount);
            job.InvoiceAmount = ClampMoney(job.InvoiceAmount);
            job.AmountPaid = ClampMoney(job.AmountPaid);
            job.PaymentMethod = NormalizePaymentMethod(job.PaymentMethod, job.Platform);
            if (!string.IsNullOrWhiteSpace(job.RelatedInvoiceNumber)
                && job.RelatedInvoiceNumber.StartsWith("EST-", StringComparison.OrdinalIgnoreCase))
            {
                job.JobType = "Estimate";
                job.InvoiceAmount = null;
                job.AmountPaid = null;
                if (string.Equals(job.Status, "Paid", StringComparison.OrdinalIgnoreCase))
                {
                    job.Status = "Quoted";
                }
            }
            break;
        case CustomerCommunication communication:
            communication.CustomerName ??= string.Empty;
            communication.Direction = string.IsNullOrWhiteSpace(communication.Direction) ? "Outgoing" : communication.Direction;
            communication.Channel = string.IsNullOrWhiteSpace(communication.Channel) ? "Email" : communication.Channel;
            communication.Summary ??= string.Empty;
            communication.FollowUpStatus = string.IsNullOrWhiteSpace(communication.FollowUpStatus) ? "None" : communication.FollowUpStatus;
            communication.OccurredAt ??= DateTime.Now;
            if (!string.Equals(communication.FollowUpStatus, "Open", StringComparison.OrdinalIgnoreCase))
            {
                communication.FollowUpDate = string.Equals(communication.FollowUpStatus, "Done", StringComparison.OrdinalIgnoreCase)
                    ? communication.FollowUpDate
                    : null;
            }
            break;
        case PrinterQueueItem queue:
            queue.Priority = string.IsNullOrWhiteSpace(queue.Priority) ? "Normal" : queue.Priority;
            queue.JobName ??= string.Empty;
            queue.Status = string.IsNullOrWhiteSpace(queue.Status) ? "Queued" : queue.Status;
            queue.Quantity = Math.Max(1, queue.Quantity);
            queue.PlateCount = Math.Max(1, queue.PlateCount);
            queue.FailureCount = Math.Max(0, queue.FailureCount);
            queue.EstimatedHours = ClampMoney(queue.EstimatedHours);
            queue.ActualHours = ClampMoney(queue.ActualHours);
            queue.ProgressPercent = Math.Clamp(queue.ProgressPercent, 0, 100);
            if (string.Equals(queue.Status, "Printing", StringComparison.OrdinalIgnoreCase))
            {
                queue.StartedAt ??= DateTime.Now;
                queue.CompletedAt = null;
                queue.ProgressPercent = Math.Clamp(queue.ProgressPercent, 1, 99);
            }
            else if (string.Equals(queue.Status, "Completed", StringComparison.OrdinalIgnoreCase))
            {
                queue.CompletedAt ??= DateTime.Now;
                queue.ProgressPercent = 100;
                queue.NeedsReview = false;
            }
            else
            {
                queue.CompletedAt = null;
                queue.ProgressPercent = Math.Min(queue.ProgressPercent, 99);
                if (string.Equals(queue.Status, "Queued", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(queue.Status, "Ready", StringComparison.OrdinalIgnoreCase))
                {
                    queue.StartedAt = null;
                    queue.ProgressPercent = 0;
                }
            }
            break;
        case Asset asset:
            asset.Name ??= string.Empty;
            asset.Category = string.IsNullOrWhiteSpace(asset.Category) ? "Equipment" : asset.Category;
            asset.TaxTreatment = string.IsNullOrWhiteSpace(asset.TaxTreatment) ? "Review" : asset.TaxTreatment;
            asset.Cost = ClampMoney(asset.Cost);
            asset.BusinessUsePercent = ClampPercent(asset.BusinessUsePercent);
            asset.NotYetExpensed = ClampMoney(asset.NotYetExpensed);
            asset.PaymentMethod = NormalizePaymentMethod(asset.PaymentMethod);
            break;
        case MakerWorldReward reward:
            reward.RewardType = string.IsNullOrWhiteSpace(reward.RewardType) ? "Points" : reward.RewardType;
            reward.Status = string.IsNullOrWhiteSpace(reward.Status) ? "Available" : reward.Status;
            reward.IncomeStatus = string.IsNullOrWhiteSpace(reward.IncomeStatus) ? "Review" : reward.IncomeStatus;
            reward.GiftCardAmount = ClampMoney(reward.GiftCardAmount);
            reward.PointsChange ??= 0;
            break;
        case Product product:
            product.Name ??= string.Empty;
            product.Category = string.IsNullOrWhiteSpace(product.Category) ? "3D Printed Product" : product.Category;
            product.TargetPrice = ClampMoney(product.TargetPrice);
            product.Grams = ClampMoney(product.Grams);
            product.MaterialCostPerGram = ClampMoney(product.MaterialCostPerGram);
            product.PrintHours = ClampMoney(product.PrintHours);
            product.MachineRatePerHour = ClampMoney(product.MachineRatePerHour);
            product.PackagingCost = ClampMoney(product.PackagingCost);
            product.DesignMinutes = ClampMoney(product.DesignMinutes);
            break;
        case AuditDocument document:
            document.DocumentType = string.IsNullOrWhiteSpace(document.DocumentType) ? "Receipt" : document.DocumentType;
            document.FileName ??= string.Empty;
            break;
        case BusinessAccount account:
            account.Name ??= string.Empty;
            account.AccountType = string.IsNullOrWhiteSpace(account.AccountType) ? "Cash" : account.AccountType;
            break;
        case ActionItem action:
            action.Title ??= string.Empty;
            action.Area = string.IsNullOrWhiteSpace(action.Area) ? "General" : action.Area;
            action.Priority = string.IsNullOrWhiteSpace(action.Priority) ? "Normal" : action.Priority;
            action.Status = string.IsNullOrWhiteSpace(action.Status) ? "Open" : action.Status;
            break;
        case AppSetting setting:
            setting.Key = (setting.Key ?? string.Empty).Trim();
            break;
    }
}

static void ApplyDefaultClockToBusinessDates(AuditableEntity entity)
{
    var clock = entity.UpdatedAtUtc == default ? DateTime.UtcNow : entity.UpdatedAtUtc;
    switch (entity)
    {
        case Sale sale:
            sale.SaleDate = AddClockIfMidnight(sale.SaleDate, clock, 0);
            sale.ShipByDate = AddClockIfMidnight(sale.ShipByDate, clock, 30);
            break;
        case CustomerJob job:
            job.JobDate = AddClockIfMidnight(job.JobDate, clock, 5);
            job.DueDate = AddClockIfMidnight(job.DueDate, clock, 10);
            job.ShipByDate = AddClockIfMidnight(job.ShipByDate, clock, 15);
            break;
        case CustomerCommunication communication:
            communication.OccurredAt = AddClockIfMidnight(communication.OccurredAt, clock, 0);
            communication.FollowUpDate = AddClockIfMidnight(communication.FollowUpDate, clock, 10);
            break;
        case PrinterQueueItem queue:
            queue.QueueDate = AddClockIfMidnight(queue.QueueDate, clock, 0);
            queue.ScheduledStart = AddClockIfMidnight(queue.ScheduledStart, clock, 5);
            queue.StartedAt = AddClockIfMidnight(queue.StartedAt, clock, 10);
            queue.EstimatedFinish = AddClockIfMidnight(queue.EstimatedFinish, clock, 15);
            queue.CompletedAt = AddClockIfMidnight(queue.CompletedAt, clock, 20);
            break;
        case ReceivableInvoice invoice:
            invoice.InvoiceDate = AddClockIfMidnight(invoice.InvoiceDate, clock, 0);
            invoice.DueDate = AddClockIfMidnight(invoice.DueDate, clock, 10);
            break;
        case Bill bill:
            bill.BillDate = AddClockIfMidnight(bill.BillDate, clock, 0);
            bill.DueDate = AddClockIfMidnight(bill.DueDate, clock, 10);
            bill.PaymentDate = AddClockIfMidnight(bill.PaymentDate, clock, 20);
            break;
        case Expense expense:
            expense.ExpenseDate = AddClockIfMidnight(expense.ExpenseDate, clock, 0);
            break;
        case Asset asset:
            asset.PurchaseDate = AddClockIfMidnight(asset.PurchaseDate, clock, 0);
            asset.InServiceDate = AddClockIfMidnight(asset.InServiceDate, clock, 10);
            asset.WarrantyEndDate = AddClockIfMidnight(asset.WarrantyEndDate, clock, 20);
            break;
        case MakerWorldReward reward:
            reward.RewardDate = AddClockIfMidnight(reward.RewardDate, clock, 0);
            break;
        case AuditDocument doc:
            doc.DocumentDate = AddClockIfMidnight(doc.DocumentDate, clock, 0);
            break;
        case ActionItem action:
            action.DueDate = AddClockIfMidnight(action.DueDate, clock, 0);
            break;
    }
}

static DateTime? AddClockIfMidnight(DateTime? value, DateTime clock, int minuteOffset)
{
    if (value is null || value.Value.TimeOfDay != TimeSpan.Zero)
    {
        return value;
    }

    var localClock = clock.Kind == DateTimeKind.Utc ? clock.ToLocalTime() : clock;
    var timeOfDay = localClock.TimeOfDay.Add(TimeSpan.FromMinutes(minuteOffset));
    if (timeOfDay >= TimeSpan.FromDays(1))
    {
        timeOfDay = TimeSpan.FromDays(1).Subtract(TimeSpan.FromSeconds(1));
    }

    return value.Value.Date
        .Add(timeOfDay);
}

static async Task ApplyCrudSideEffectsAsync<TEntity>(
    AppDbContext db,
    TEntity entity,
    ReceivableInvoiceSyncIdentity? previousReceivableIdentity = null,
    int? previousQueueJobId = null) where TEntity : AuditableEntity
{
    if (entity is ReceivableInvoice invoice)
    {
        await SyncManualReceivableInvoiceToSaleAsync(db, invoice, previousReceivableIdentity);
    }
    if (entity is PrinterQueueItem queue)
    {
        if (previousQueueJobId.HasValue && previousQueueJobId != queue.CustomerJobId)
        {
            await SyncCustomerJobFromPrinterQueueAsync(db, previousQueueJobId.Value, queue, includeChangedQueue: false);
        }
        if (queue.CustomerJobId.HasValue)
        {
            await SyncCustomerJobFromPrinterQueueAsync(db, queue.CustomerJobId.Value, queue, includeChangedQueue: true);
        }
    }
}

static async Task SyncCustomerJobFromPrinterQueueAsync(
    AppDbContext db,
    int jobId,
    PrinterQueueItem changedQueue,
    bool includeChangedQueue)
{
    var job = await db.CustomerJobs.FirstOrDefaultAsync(x => x.Id == jobId && !x.IsArchived);
    if (job is null || StatusEquals(job.Status, "Paid") || StatusEquals(job.Status, "Cancelled"))
    {
        return;
    }

    var persisted = await db.PrinterQueueItems
        .Where(x => x.CustomerJobId == jobId && x.Id != changedQueue.Id && !x.IsArchived)
        .ToListAsync();
    var active = persisted.ToList();
    if (includeChangedQueue && !changedQueue.IsArchived)
    {
        active.Add(changedQueue);
    }

    ApplyCustomerJobQueueStatus(job, active);
}

static async Task RecomputeCustomerJobFromPrinterQueueAsync(
    AppDbContext db,
    CustomerJob job,
    CancellationToken cancellationToken)
{
    if (StatusEquals(job.Status, "Paid") || StatusEquals(job.Status, "Cancelled"))
    {
        return;
    }

    var active = await db.PrinterQueueItems.AsNoTracking()
        .Where(item => item.CustomerJobId == job.Id && !item.IsArchived)
        .ToListAsync(cancellationToken);
    ApplyCustomerJobQueueStatus(job, active);
}

static void ApplyCustomerJobQueueStatus(CustomerJob job, IReadOnlyCollection<PrinterQueueItem> active)
{
    var anyActivelyWorked = active.Any(x => QueueStatusIs(x, "Printing", "Paused", "Needs Attention"));
    var anyCompleted = active.Any(x => QueueStatusIs(x, "Completed"));
    var anyWaiting = active.Any(x => QueueStatusIs(x, "Queued", "Ready"));
    var allTerminal = active.Count > 0 && active.All(x => QueueStatusIs(x, "Completed", "Cancelled"));

    if (anyActivelyWorked || (anyCompleted && anyWaiting))
    {
        job.Status = "In Progress";
    }
    else if (allTerminal && anyCompleted)
    {
        job.Status = "Completed";
    }
    else
    {
        job.Status = "Open";
    }
}

static bool QueueStatusIs(PrinterQueueItem queue, params string[] statuses) =>
    statuses.Any(status => string.Equals(queue.Status, status, StringComparison.OrdinalIgnoreCase));

static decimal? ClampMoney(decimal? value)
{
    return value is null ? null : Math.Max(0, value.Value);
}

static decimal? ClampPercent(decimal? value)
{
    return value is null ? null : Math.Clamp(value.Value, 0, 100);
}

static IQueryable<T> ActiveRows<T>(IQueryable<T> query, bool includeArchived) where T : AuditableEntity
{
    return includeArchived ? query : query.Where(x => !x.IsArchived);
}

static IQueryable<InvoiceDocument> FilterInvoiceDocuments(AppDbContext db, string? q, string? type, string? status, bool includeArchived = false)
{
    var query = db.InvoiceDocuments.AsNoTracking().AsQueryable();
    if (!includeArchived)
    {
        query = query.Where(d => !d.IsArchived);
    }

    if (!string.IsNullOrWhiteSpace(type))
    {
        query = query.Where(d => d.DocType == type.ToUpperInvariant());
    }

    if (!string.IsNullOrWhiteSpace(status))
    {
        query = query.Where(d => d.Status == status);
    }

    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = q.ToLowerInvariant();
        query = query.Where(d =>
            (d.DocNumber != null && d.DocNumber.ToLower().Contains(term)) ||
            (d.CustomerName != null && d.CustomerName.ToLower().Contains(term)) ||
            (d.ProjectName != null && d.ProjectName.ToLower().Contains(term)));
    }

    return query;
}

static IQueryable<object> InvoiceDocumentSummaries(IQueryable<InvoiceDocument> query)
{
    return query
        .OrderByDescending(d => d.UpdatedAt)
        .ThenByDescending(d => d.Id)
        .Select(d => new
        {
            d.Id,
            d.DocNumber,
            d.DocType,
            d.Status,
            d.CustomerName,
            d.ProjectName,
            d.Total,
            d.AmountPaid,
            d.Balance,
            d.DocDate,
            d.DueDate,
            d.CreatedAt,
            d.UpdatedAt,
            d.IsArchived,
            d.ArchivedAt,
            d.ArchiveReason,
            SourceKind = "document",
            SourceId = d.Id,
            SourceLabel = "Builder document"
        });
}

static async Task<List<object>> UnifiedInvoiceRecordSummariesAsync(AppDbContext db, string? q, string? type, string? status, bool includeArchived = false)
{
    var docRows = await InvoiceDocumentSummaries(FilterInvoiceDocuments(db, q, type, status, includeArchived)).ToListAsync();
    var invoiceDocNumbers = await db.InvoiceDocuments.AsNoTracking()
        .Where(d => d.DocType == "INVOICE" && d.DocNumber != null)
        .Select(d => d.DocNumber)
        .ToListAsync();

    var includeInvoices = string.IsNullOrWhiteSpace(type) || type.Equals("INVOICE", StringComparison.OrdinalIgnoreCase);
    IQueryable<ReceivableInvoice> arQuery = db.ReceivableInvoices.AsNoTracking();
    if (!includeArchived)
    {
        arQuery = arQuery.Where(x => !x.IsArchived);
    }
    if (!includeInvoices)
    {
        arQuery = arQuery.Where(_ => false);
    }
    if (!string.IsNullOrWhiteSpace(status))
    {
        arQuery = arQuery.Where(x => x.Status == status);
    }
    if (!string.IsNullOrWhiteSpace(q))
    {
        var term = q.ToLowerInvariant();
        arQuery = arQuery.Where(x =>
            (x.InvoiceNumber != null && x.InvoiceNumber.ToLower().Contains(term)) ||
            (x.CustomerName != null && x.CustomerName.ToLower().Contains(term)) ||
            (x.ProjectName != null && x.ProjectName.ToLower().Contains(term)));
    }

    var arRows = await arQuery
        .OrderByDescending(x => x.UpdatedAtUtc)
        .ThenByDescending(x => x.Id)
        .ToListAsync();

    var arOnlyRows = arRows
        .Where(x => !IsGeneratedReceivableForActiveInvoiceDocument(x, invoiceDocNumbers))
        .Select(x => new
        {
            x.Id,
            DocNumber = x.InvoiceNumber,
            DocType = "INVOICE",
            x.Status,
            x.CustomerName,
            x.ProjectName,
            Total = x.InvoiceTotal ?? 0,
            AmountPaid = x.AmountPaid ?? 0,
            Balance = Math.Max(0, (x.InvoiceTotal ?? 0) - (x.AmountPaid ?? 0)),
            DocDate = x.InvoiceDate.HasValue ? x.InvoiceDate.Value.ToString("yyyy-MM-dd") : null,
            DueDate = x.DueDate.HasValue ? x.DueDate.Value.ToString("yyyy-MM-dd") : null,
            CreatedAt = x.CreatedAtUtc.ToString("O"),
            UpdatedAt = x.UpdatedAtUtc.ToString("O"),
            x.IsArchived,
            SourceKind = "receivable",
            SourceId = x.Id,
            SourceLabel = "AR only"
        })
        .Cast<object>()
        .ToList();

    return docRows
        .Concat(arOnlyRows)
        .OrderByDescending(r => GetRecordUpdatedAt(r))
        .ToList();
}

static async Task<object> UnifiedInvoiceStatsAsync(AppDbContext db)
{
    var docs = await db.InvoiceDocuments.AsNoTracking()
        .Where(d => !d.IsArchived)
        .Select(d => new
        {
            d.DocNumber,
            d.DocType,
            d.Status,
            d.CustomerName,
            d.Total,
            d.AmountPaid,
            d.Balance
        })
        .ToListAsync();

    var activeInvoiceDocNumbers = docs
        .Where(d => d.DocType == "INVOICE"
            && !StatusEquals(d.Status, "Void")
            && !string.IsNullOrWhiteSpace(d.DocNumber))
        .Select(d => d.DocNumber)
        .ToList();

    var arOnly = (await db.ReceivableInvoices.AsNoTracking()
        .Where(x => !x.IsArchived)
        .ToListAsync())
        .Where(x => !IsGeneratedReceivableForActiveInvoiceDocument(x, activeInvoiceDocNumbers))
        .Select(x => new
        {
            DocType = "INVOICE",
            Status = x.Status,
            Total = x.InvoiceTotal ?? 0,
            AmountPaid = x.AmountPaid ?? 0,
            Balance = Math.Max(0, (x.InvoiceTotal ?? 0) - (x.AmountPaid ?? 0))
        })
        .ToList();

    var all = docs
        .Select(d => new { d.DocType, d.Status, d.Total, d.AmountPaid, d.Balance })
        .Concat(arOnly)
        .ToList();
    var invoices = all.Where(d => d.DocType == "INVOICE").ToList();
    var openInvoices = invoices.Where(d => !IsClosedInvoiceDocumentStatus(d.Status)).ToList();
    var activeInvoices = invoices.Where(d => !StatusEquals(d.Status, "Draft") && !StatusEquals(d.Status, "Void")).ToList();

    return new
    {
        totalEstimates = all.Count(d => d.DocType == "ESTIMATE"),
        totalInvoices = invoices.Count,
        totalRevenue = activeInvoices.Sum(d => d.AmountPaid),
        totalInvoiced = activeInvoices.Sum(d => d.Total),
        paidRevenue = activeInvoices.Sum(d => d.AmountPaid),
        unpaidBalance = openInvoices.Sum(d => Math.Max(0, d.Balance)),
        draftCount = all.Count(d => StatusEquals(d.Status, "Draft")),
        sentCount = all.Count(d => StatusEquals(d.Status, "Sent")),
        paidCount = all.Count(d => StatusEquals(d.Status, "Paid")),
        voidCount = all.Count(d => StatusEquals(d.Status, "Void"))
    };
}

static DateTime GetRecordUpdatedAt(object row)
{
    var prop = row.GetType().GetProperty("UpdatedAt");
    var value = prop?.GetValue(row)?.ToString();
    return DateTime.TryParse(value, out var parsed) ? parsed : DateTime.MinValue;
}

static bool StatusEquals(string? actual, string expected)
{
    return string.Equals(actual ?? string.Empty, expected, StringComparison.OrdinalIgnoreCase);
}

static bool IsGeneratedReceivableForActiveInvoiceDocument(ReceivableInvoice invoice, IEnumerable<string?> activeInvoiceDocumentNumbers)
{
    if (string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
    {
        return false;
    }

    var hasActiveDocument = activeInvoiceDocumentNumbers.Any(docNumber =>
        string.Equals(docNumber, invoice.InvoiceNumber, StringComparison.OrdinalIgnoreCase));
    return hasActiveDocument
        && string.Equals(invoice.SourceProof?.Trim(), $"Unified invoice {invoice.InvoiceNumber}", StringComparison.OrdinalIgnoreCase);
}

static async Task<InvoiceDocument> CreateInvoiceDocumentAsync(
    AppDbContext db,
    SaveInvoiceDocumentRequest request,
    CancellationToken cancellationToken = default)
{
    await InvoiceDocumentMutationLocks.Gate.WaitAsync(cancellationToken);
    try
    {
        return await CreateInvoiceDocumentUnderLockAsync(db, request, cancellationToken);
    }
    finally
    {
        InvoiceDocumentMutationLocks.Gate.Release();
    }
}

static async Task<InvoiceDocument> CreateInvoiceDocumentUnderLockAsync(
    AppDbContext db,
    SaveInvoiceDocumentRequest request,
    CancellationToken cancellationToken,
    Action<InvoiceDocument>? beforeCommit = null)
{
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    try
    {
        var now = DateTimeOffset.UtcNow.ToString("O");
        var doc = new InvoiceDocument
        {
            CreatedAt = now,
            UpdatedAt = now,
            DocNumber = string.IsNullOrWhiteSpace(request.DocNumber)
                ? await NextInvoiceDocumentNumberAsync(db, request.DocType ?? "ESTIMATE")
                : CanonicalizeNewInvoiceDocumentNumber(request.DocNumber)
        };
        ApplyInvoiceDocumentRequest(doc, request);
        await ValidateInvoiceDocumentIdentityAsync(db, doc, cancellationToken);
        db.InvoiceDocuments.Add(doc);
        await db.SaveChangesAsync(cancellationToken);
        AddInvoiceDocumentEvent(db, doc, "Created", null, doc.Status, $"{doc.DocType} created as {doc.Status}", $"{doc.DocNumber} was created.", doc.Total);
        await db.SaveChangesAsync(cancellationToken);
        await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc, cancellationToken);
        if (beforeCommit is not null)
        {
            beforeCommit(doc);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        return doc;
    }
    catch
    {
        await transaction.RollbackAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        throw;
    }
}

static async Task<IResult> CreateInvoiceDocumentResultAsync(
    AppDbContext db,
    SaveInvoiceDocumentRequest request,
    CancellationToken cancellationToken)
{
    try
    {
        var doc = await CreateInvoiceDocumentAsync(db, request, cancellationToken);
        return Results.Ok(ToInvoiceDocumentDto(doc));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
}

static async Task<InvoiceDocument?> UpdateInvoiceDocumentAsync(
    AppDbContext db,
    int id,
    SaveInvoiceDocumentRequest request,
    string? expectedUpdatedAt,
    CancellationToken cancellationToken)
{
    await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
    var doc = await db.InvoiceDocuments
        .Include(d => d.LineItems)
        .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
    if (doc is null)
    {
        await transaction.RollbackAsync(CancellationToken.None);
        return null;
    }
    try
    {
        if (doc.IsArchived)
        {
            throw new InvalidOperationException($"Archived document {doc.DocNumber} must be restored before it can be edited.");
        }
        if (!InvoiceDocumentUpdatedAtMatches(doc.UpdatedAt, expectedUpdatedAt))
        {
            throw new StaleInvoiceDocumentException(doc.UpdatedAt);
        }

        var before = CaptureInvoiceDocumentAuditState(doc);
        doc.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        var requestedType = (request.DocType ?? doc.DocType ?? "ESTIMATE").ToUpperInvariant();
        if (!string.Equals(doc.DocType, requestedType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Cannot change saved {doc.DocType} {doc.DocNumber} into a {requestedType} by saving. Use Create Invoice from the estimate row so the original estimate stays intact.");
        }

        ApplyInvoiceDocumentRequest(doc, request);
        await ValidateInvoiceDocumentIdentityAsync(db, doc, cancellationToken);
        await RepointUnifiedDocumentLedgerLinksAsync(db, doc, before, cancellationToken);
        AddInvoiceDocumentChangeEvents(db, doc, before);
        await db.SaveChangesAsync(cancellationToken);
        await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return doc;
    }
    catch
    {
        await transaction.RollbackAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        throw;
    }
}

static async Task<IResult> UpdateInvoiceDocumentResultAsync(
    AppDbContext db,
    int id,
    SaveInvoiceDocumentRequest request,
    HttpRequest httpRequest,
    CancellationToken cancellationToken)
{
    if (!TryReadExpectedInvoiceDocumentUpdatedAt(httpRequest, out var expectedUpdatedAt))
    {
        return Results.BadRequest(new
        {
            message = "X-EPATA-Updated-At must be the ISO-8601 timestamp returned with this Invoice Record. Reload the record and try again."
        });
    }

    await InvoiceDocumentMutationLocks.Gate.WaitAsync(cancellationToken);
    try
    {
        var doc = await UpdateInvoiceDocumentAsync(db, id, request, expectedUpdatedAt, cancellationToken);
        return doc is null
            ? Results.NotFound(new { message = $"Document {id} was not found." })
            : Results.Ok(ToInvoiceDocumentDto(doc));
    }
    catch (StaleInvoiceDocumentException ex)
    {
        return Results.Conflict(new
        {
            message = "This Invoice Record changed after you opened it. Reload it, review the newer values, and save again. No fields were overwritten.",
            currentUpdatedAt = ex.CurrentUpdatedAt
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    finally
    {
        InvoiceDocumentMutationLocks.Gate.Release();
    }
}

static async Task<InvoiceDocument?> DuplicateInvoiceDocumentAsync(AppDbContext db, int id, string? targetType)
{
    await InvoiceDocumentMutationLocks.Gate.WaitAsync();
    try
    {
        var source = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
        .FirstOrDefaultAsync(d => d.Id == id && !d.IsArchived);
        if (source is null)
        {
            return null;
        }

        var isConversion = string.Equals(targetType, "INVOICE", StringComparison.OrdinalIgnoreCase);
        if (isConversion && !source.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Create Invoice is available only for an estimate. Duplicate an invoice with Save as New instead.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            if (isConversion)
            {
                var existingConversion = await FindConvertedInvoiceAsync(db, source.Id);
                if (existingConversion is not null)
                {
                    await transaction.CommitAsync();
                    return existingConversion;
                }
            }

            var docType = targetType ?? source.DocType;
            var now = DateTimeOffset.UtcNow.ToString("O");
            var copy = new InvoiceDocument
            {
                SourceEstimateId = isConversion ? source.Id : null,
                DocNumber = await NextInvoiceDocumentNumberAsync(db, docType),
                DocType = docType,
                Status = "Draft",
                CustomerName = source.CustomerName,
                CustomerPhone = source.CustomerPhone,
                CustomerAddress = source.CustomerAddress,
                CustomerEmail = source.CustomerEmail,
                PreparedFor = source.PreparedFor,
                ProjectName = source.ProjectName,
                Material = source.Material,
                Color = source.Color,
                Infill = source.Infill,
                ProjectDescription = source.ProjectDescription,
                ProjectNotes = targetType == "INVOICE" ? $"Converted from estimate {source.DocNumber}. {source.ProjectNotes}".Trim() : source.ProjectNotes,
                PageSize = source.PageSize,
                DocDate = DateTime.Now.ToString("yyyy-MM-dd"),
                DueDate = DateTime.Now.AddDays(docType == "INVOICE" ? 7 : 14).ToString("yyyy-MM-dd"),
                Subtotal = source.Subtotal,
                DiscountAmount = source.DiscountAmount,
                RushAmount = source.RushAmount,
                TaxAmount = source.TaxAmount,
                Total = source.Total,
                AmountPaid = 0,
                Balance = docType == "INVOICE" ? source.Total : 0,
                PaymentMethod = source.PaymentMethod,
                PricingGuide = source.PricingGuide,
                TermsNotes = docType == "INVOICE" ? "Payment due by the due date shown above." : source.TermsNotes,
                StandardTurnaround = source.StandardTurnaround,
                RushTurnaround = source.RushTurnaround,
                CalcGrams = source.CalcGrams,
                CalcHours = source.CalcHours,
                CalcDesignHours = source.CalcDesignHours,
                CalcSetupFee = source.CalcSetupFee,
                CalcPostFee = source.CalcPostFee,
                CalcGramRate = source.CalcGramRate,
                CalcHourRate = source.CalcHourRate,
                CalcDesignRate = source.CalcDesignRate,
                CalcMinimum = source.CalcMinimum,
                CalcDifficulty = source.CalcDifficulty,
                CalcRush = source.CalcRush,
                CalcDiscount = source.CalcDiscount,
                CalcTaxRate = source.CalcTaxRate,
                Json = BuildConvertedInvoiceJson(source, docType),
                CreatedAt = now,
                UpdatedAt = now,
                LineItems = source.LineItems.Select(li => new InvoiceLineItem
                {
                    SortOrder = li.SortOrder,
                    Description = li.Description,
                    Details = li.Details,
                    Quantity = li.Quantity,
                    Rate = li.Rate,
                    Amount = li.Amount
                }).ToList()
            };

            NormalizeExistingInvoiceDocumentMoney(copy);
            await ValidateInvoiceDocumentIdentityAsync(db, copy);
            db.InvoiceDocuments.Add(copy);
            await db.SaveChangesAsync();
            AddInvoiceDocumentEvent(db, copy, targetType == "INVOICE" ? "Converted" : "Duplicated", source.Status, copy.Status,
                targetType == "INVOICE" ? $"Invoice created from {source.DocNumber}" : $"Document duplicated from {source.DocNumber}",
                $"{copy.DocNumber} was created from {source.DocNumber}.", copy.Total);
            await db.SaveChangesAsync();
            await SyncUnifiedInvoiceDocumentToLedgerAsync(db, copy);
            if (isConversion)
            {
                await MarkEstimateConvertedAsync(db, source.Id, copy.DocNumber);
            }
            await transaction.CommitAsync();
            return copy;
        }
        catch
        {
            await transaction.RollbackAsync();
            db.ChangeTracker.Clear();
            throw;
        }
    }
    finally
    {
        InvoiceDocumentMutationLocks.Gate.Release();
    }
}

static async Task<InvoiceDocument?> FindConvertedInvoiceAsync(AppDbContext db, int estimateId)
{
    var linked = await db.InvoiceDocuments
        .Include(document => document.LineItems.OrderBy(line => line.SortOrder))
        .FirstOrDefaultAsync(document => document.SourceEstimateId == estimateId);
    if (linked is not null)
    {
        return linked;
    }

    var estimate = await db.InvoiceDocuments.AsNoTracking()
        .FirstOrDefaultAsync(document => document.Id == estimateId && document.DocType == "ESTIMATE");
    if (estimate is null || string.IsNullOrWhiteSpace(estimate.DocNumber))
    {
        return null;
    }

    var referencedInvoiceNumbers = Regex.Matches(
            estimate.ProjectNotes ?? string.Empty,
            @"Converted to invoice (?<number>INV-[A-Za-z0-9-]+)",
            RegexOptions.IgnoreCase)
        .Select(match => match.Groups["number"].Value)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);
    var legacyMarker = $"Converted from estimate {estimate.DocNumber}";
    var unlinkedInvoices = await db.InvoiceDocuments
        .Include(document => document.LineItems.OrderBy(line => line.SortOrder))
        .Where(document => document.DocType == "INVOICE" && document.SourceEstimateId == null)
        .OrderBy(document => document.Id)
        .ToListAsync();
    var legacy = unlinkedInvoices.FirstOrDefault(document =>
        !string.IsNullOrWhiteSpace(document.DocNumber)
        && (referencedInvoiceNumbers.Contains(document.DocNumber)
            || document.ProjectNotes?.Contains(legacyMarker, StringComparison.OrdinalIgnoreCase) == true));
    if (legacy is null)
    {
        return null;
    }

    legacy.SourceEstimateId = estimateId;
    await db.SaveChangesAsync();
    await MarkEstimateConvertedAsync(db, estimateId, legacy.DocNumber);
    return legacy;
}

static async Task<IResult> ConvertInvoiceDocumentResultAsync(AppDbContext db, int id)
{
    var gate = InvoiceDocumentConversionLocks.For(id);
    await gate.WaitAsync();
    try
    {
        var invoice = await DuplicateInvoiceDocumentAsync(db, id, "INVOICE");
        return invoice is null
            ? Results.NotFound(new { message = $"Document {id} was not found." })
            : Results.Ok(ToInvoiceDocumentDto(invoice));
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { message = ex.Message });
    }
    catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
    {
        db.ChangeTracker.Clear();
        var existing = await FindConvertedInvoiceAsync(db, id);
        if (existing is not null)
        {
            return Results.Ok(ToInvoiceDocumentDto(existing));
        }

        throw;
    }
    finally
    {
        gate.Release();
    }
}

static string BuildConvertedInvoiceJson(InvoiceDocument source, string docType)
{
    try
    {
        using var parsed = JsonDocument.Parse(string.IsNullOrWhiteSpace(source.Json) ? "{}" : source.Json);
        var map = JsonSerializer.Deserialize<Dictionary<string, object?>>(parsed.RootElement.GetRawText()) ?? [];
        map["docType"] = docType;
        map["status"] = "Draft";
        map["docNumber"] = null;
        map["amountPaid"] = 0;
        map["balance"] = docType == "INVOICE" ? source.Total : 0;
        map["lineItems"] = source.LineItems.OrderBy(li => li.SortOrder).Select(li => new
        {
            description = li.Description,
            details = li.Details,
            quantity = li.Quantity,
            rate = li.Rate,
            amount = li.Amount
        }).ToList();
        return JsonSerializer.Serialize(map);
    }
    catch
    {
        return "{}";
    }
}

static async Task MarkEstimateConvertedAsync(AppDbContext db, int estimateId, string? invoiceNumber)
{
    var estimate = await db.InvoiceDocuments.FirstOrDefaultAsync(d => d.Id == estimateId && d.DocType == "ESTIMATE");
    if (estimate is null)
    {
        return;
    }

    var beforeStatus = estimate.Status;
    estimate.Status = estimate.Status.Equals("Void", StringComparison.OrdinalIgnoreCase) ? estimate.Status : "Accepted";
    estimate.ProjectNotes = AppendNote(estimate.ProjectNotes, $"Converted to invoice {invoiceNumber}.");
    estimate.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
    AddInvoiceDocumentEvent(db, estimate, "Converted", beforeStatus, estimate.Status, $"Estimate converted to invoice {invoiceNumber}",
        $"{estimate.DocNumber} remained an estimate and was linked to invoice {invoiceNumber}.", estimate.Total);

    var sourceProof = $"Unified estimate {estimate.DocNumber}";
    var estimateIdentity = InvoiceDocumentIdentityKey(estimate.DocNumber)!;
    var sourceIdentity = sourceProof.Trim().ToUpperInvariant();
    var job = await db.CustomerJobs.FirstOrDefaultAsync(x =>
        !x.IsArchived
        && x.RelatedInvoiceNumber != null
        && x.RelatedInvoiceNumber.Trim().ToUpper() == estimateIdentity
        && x.SourceProof != null
        && x.SourceProof.Trim().ToUpper() == sourceIdentity);
    if (job is not null)
    {
        job.Status = "Invoiced";
        job.InvoiceAmount = null;
        job.Notes = AppendNote(job.Notes, $"Converted to invoice {invoiceNumber}.");
        job.UpdatedAtUtc = DateTime.UtcNow;
    }

    await db.SaveChangesAsync();
}

static async Task SyncSourceEstimateJobAfterConversionStateChangeAsync(
    AppDbContext db,
    int estimateId,
    int changedInvoiceId,
    bool changedInvoiceIsActive)
{
    var estimate = await db.InvoiceDocuments.AsNoTracking()
        .FirstOrDefaultAsync(document => document.Id == estimateId && document.DocType == "ESTIMATE");
    if (estimate is null || string.IsNullOrWhiteSpace(estimate.DocNumber))
    {
        return;
    }

    var sourceProof = $"Unified estimate {estimate.DocNumber}";
    var estimateIdentity = InvoiceDocumentIdentityKey(estimate.DocNumber)!;
    var sourceIdentity = sourceProof.Trim().ToUpperInvariant();
    var job = await db.CustomerJobs.FirstOrDefaultAsync(candidate =>
        candidate.RelatedInvoiceNumber != null
        && candidate.RelatedInvoiceNumber.Trim().ToUpper() == estimateIdentity
        && candidate.SourceProof != null
        && candidate.SourceProof.Trim().ToUpper() == sourceIdentity);
    if (job is null)
    {
        return;
    }

    var hasActiveConversion = changedInvoiceIsActive
        || await db.InvoiceDocuments.AsNoTracking().AnyAsync(document =>
            document.SourceEstimateId == estimateId
            && document.Id != changedInvoiceId
            && !document.IsArchived);

    job.IsArchived = estimate.IsArchived && !hasActiveConversion;
    job.Status = hasActiveConversion
        ? "Invoiced"
        : estimate.Status.Equals("Void", StringComparison.OrdinalIgnoreCase)
            ? "Cancelled"
            : "Quoted";
    job.UpdatedAtUtc = DateTime.UtcNow;
}

static async Task ValidateInvoiceDocumentIdentityAsync(
    AppDbContext db,
    InvoiceDocument doc,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(doc.DocNumber))
    {
        return;
    }

    var documentIdentity = InvoiceDocumentIdentityKey(doc.DocNumber)!;

    var expectedPrefix = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? "INV-" : "EST-";
    if (!doc.DocNumber.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"{doc.DocType} documents must use a {expectedPrefix} number. Use Save as New or create a new {doc.DocType.ToLowerInvariant()} instead of changing the type on an existing record.");
    }

    var duplicate = await db.InvoiceDocuments.AsNoTracking()
        .Where(d => d.Id != doc.Id
            && d.DocNumber != null
            && d.DocNumber.Trim().ToUpper() == documentIdentity)
        .Select(d => new { d.Id, d.DocType, d.CustomerName, d.ProjectName, d.IsArchived })
        .FirstOrDefaultAsync(cancellationToken);
    if (duplicate is not null)
    {
        var archiveState = duplicate.IsArchived ? "archived " : string.Empty;
        throw new InvalidOperationException($"Document number {doc.DocNumber} is already reserved by {archiveState}record #{duplicate.Id} ({duplicate.DocType}, {duplicate.CustomerName}, {duplicate.ProjectName}). Archived estimates/invoices keep their numbers reserved for audit clarity.");
    }
}

static string CanonicalizeNewInvoiceDocumentNumber(string documentNumber) =>
    documentNumber.Trim().ToUpperInvariant();

static string? InvoiceDocumentIdentityKey(string? documentNumber) =>
    string.IsNullOrWhiteSpace(documentNumber)
        ? null
        : documentNumber.Trim().ToUpperInvariant();

static bool InvoiceDocumentIdentityEquals(string? left, string? right) =>
    string.Equals(
        InvoiceDocumentIdentityKey(left),
        InvoiceDocumentIdentityKey(right),
        StringComparison.Ordinal);

static async Task<IResult> DeleteInvoiceDocumentAndSyncAsync(
    AppDbContext db,
    int id,
    CancellationToken cancellationToken)
{
    await InvoiceDocumentMutationLocks.Gate.WaitAsync(cancellationToken);
    try
    {
        var doc = await db.InvoiceDocuments.FirstOrDefaultAsync(
            document => document.Id == id,
            cancellationToken);
        if (doc is null)
        {
            return Results.NotFound(new { message = $"Document {id} was not found." });
        }
        if (doc.IsArchived)
        {
            return Results.Ok(new { archived = id, docNumber = doc.DocNumber ?? string.Empty, alreadyArchived = true });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var docNumber = doc.DocNumber ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(docNumber))
            {
                var documentIdentity = InvoiceDocumentIdentityKey(docNumber)!;
                if (doc.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase))
                {
                    var sourceProof = $"Unified estimate {docNumber}";
                    var sourceIdentity = sourceProof.Trim().ToUpperInvariant();
                    var hasActiveConvertedInvoice = await db.InvoiceDocuments.AsNoTracking()
                        .AnyAsync(x => x.SourceEstimateId == doc.Id && !x.IsArchived, cancellationToken);
                    var jobs = await db.CustomerJobs
                        .Where(x => !x.IsArchived
                            && x.RelatedInvoiceNumber != null
                            && x.RelatedInvoiceNumber.Trim().ToUpper() == documentIdentity
                            && x.SourceProof != null
                            && x.SourceProof.Trim().ToUpper() == sourceIdentity)
                        .ToListAsync(cancellationToken);
                    foreach (var job in jobs)
                    {
                        job.IsArchived = !hasActiveConvertedInvoice;
                        if (hasActiveConvertedInvoice)
                        {
                            job.Status = "Invoiced";
                        }
                        job.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }
                else if (doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase))
                {
                    var sourceProof = $"Unified invoice {docNumber}";
                    var sourceIdentity = sourceProof.Trim().ToUpperInvariant();
                    var invoice = await db.ReceivableInvoices.FirstOrDefaultAsync(
                        x => !x.IsArchived
                            && x.InvoiceNumber.Trim().ToUpper() == documentIdentity
                            && x.SourceProof != null
                            && x.SourceProof.Trim().ToUpper() == sourceIdentity,
                        cancellationToken);
                    if (invoice is not null)
                    {
                        invoice.IsArchived = true;
                        invoice.UpdatedAtUtc = DateTime.UtcNow;
                    }

                    var sales = await db.Sales
                        .Where(x => !x.IsArchived
                            && x.Platform.ToUpper() == "DIRECT"
                            && x.InvoiceNumber != null
                            && x.InvoiceNumber.Trim().ToUpper() == documentIdentity
                            && x.SourceProof != null
                            && x.SourceProof.Trim().ToUpper() == sourceIdentity)
                        .ToListAsync(cancellationToken);
                    foreach (var sale in sales)
                    {
                        sale.IncludeInDashboard = false;
                        sale.IsArchived = true;
                        sale.UpdatedAtUtc = DateTime.UtcNow;
                    }
                }
            }

            doc.IsArchived = true;
            doc.ArchivedAt = DateTimeOffset.UtcNow.ToString("O");
            doc.ArchiveReason = "Archived from the invoice records page. Linked generated ledger rows were archived too; the original document and line items remain in the database.";
            doc.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            if (doc.SourceEstimateId.HasValue)
            {
                await SyncSourceEstimateJobAfterConversionStateChangeAsync(db, doc.SourceEstimateId.Value, doc.Id, false);
            }
            AddInvoiceDocumentEvent(db, doc, "Archived", doc.Status, doc.Status, $"{doc.DocType} archived", $"{doc.DocNumber} was archived, not deleted.", doc.Total);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(new { archived = id, docNumber });
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }
    finally
    {
        InvoiceDocumentMutationLocks.Gate.Release();
    }
}

static async Task<IResult> RestoreInvoiceDocumentAndSyncAsync(
    AppDbContext db,
    int id,
    CancellationToken cancellationToken)
{
    await InvoiceDocumentMutationLocks.Gate.WaitAsync(cancellationToken);
    try
    {
        var doc = await db.InvoiceDocuments
            .Include(d => d.LineItems)
            .FirstOrDefaultAsync(d => d.Id == id, cancellationToken);
        if (doc is null)
        {
            return Results.NotFound(new { message = $"Document {id} was not found." });
        }
        if (!doc.IsArchived)
        {
            return Results.Ok(ToInvoiceDocumentDto(doc));
        }

        try
        {
            await ValidateInvoiceDocumentIdentityAsync(db, doc, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            doc.IsArchived = false;
            doc.ArchivedAt = null;
            doc.ArchiveReason = null;
            doc.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            if (doc.SourceEstimateId.HasValue)
            {
                await SyncSourceEstimateJobAfterConversionStateChangeAsync(db, doc.SourceEstimateId.Value, doc.Id, true);
            }
            AddInvoiceDocumentEvent(db, doc, "Restored", doc.Status, doc.Status, $"{doc.DocType} restored", $"{doc.DocNumber} was restored from archive.", doc.Total);
            await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Results.Ok(ToInvoiceDocumentDto(doc));
        }
        catch (InvalidOperationException ex)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return Results.Conflict(new { message = ex.Message });
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            throw;
        }
    }
    finally
    {
        InvoiceDocumentMutationLocks.Gate.Release();
    }
}

static async Task UpsertSettingAsync(AppDbContext db, string key, string? value)
{
    var setting = await db.AppSettings.FirstOrDefaultAsync(x => x.Key == key);
    if (setting is null)
    {
        db.AppSettings.Add(new AppSetting { Key = key, Value = value ?? string.Empty, Notes = "Invoice builder setting." });
    }
    else
    {
        setting.Value = value ?? string.Empty;
        setting.IsArchived = false;
    }
}

static InvoiceBuilderConfigRequest NormalizeInvoiceBuilderConfigRequest(InvoiceBuilderConfigRequest request)
{
    return request with
    {
        Id = 1,
        BusinessName = NormalizeConfigText(request.BusinessName, 160),
        BusinessLocation = NormalizeConfigText(request.BusinessLocation, 160),
        BusinessEmail = NormalizeConfigText(request.BusinessEmail, 160).ToLowerInvariant(),
        BusinessPhone = NormalizeConfigText(request.BusinessPhone, 160),
        BusinessWebsite = NormalizeConfigText(request.BusinessWebsite, 500),
        BusinessEtsy = NormalizeConfigText(request.BusinessEtsy, 300),
        BusinessInstagram = NormalizeConfigText(request.BusinessInstagram, 160),
        BusinessFacebook = NormalizeConfigText(request.BusinessFacebook, 300),
        BrandColor = NormalizeConfigBrandColor(request.BrandColor),
        CalcGramRate = Math.Clamp(request.CalcGramRate, 0m, 1_000m),
        CalcHourRate = Math.Clamp(request.CalcHourRate, 0m, 10_000m),
        CalcDesignRate = Math.Clamp(request.CalcDesignRate, 0m, 10_000m),
        CalcSetupFee = Math.Clamp(request.CalcSetupFee, 0m, 1_000_000_000m),
        CalcPostFee = Math.Clamp(request.CalcPostFee, 0m, 1_000_000_000m),
        CalcMinimum = Math.Clamp(request.CalcMinimum, 0m, 1_000_000_000m)
    };
}

static string NormalizeConfigText(string? value, int maxLength)
{
    var clean = Regex.Replace(value ?? string.Empty, @"[\u0000-\u0008\u000B\u000C\u000E-\u001F\u007F]", string.Empty).Trim();
    return clean.Length > maxLength ? clean[..maxLength] : clean;
}

static string NormalizeConfigBrandColor(string? value)
{
    var clean = NormalizeConfigText(value, 32);
    return Regex.IsMatch(clean, "^#[0-9a-fA-F]{6}$") ? clean.ToLowerInvariant() : "#17468f";
}

static async Task<(byte[] Bytes, string FileName)> CreateDatabaseBackupAsync(
    AppDbContext db,
    IWebHostEnvironment env,
    IConfiguration configuration,
    bool retainServerCopy)
{
    var dbPath = GetDatabasePath(configuration, env);
    if (!System.IO.File.Exists(dbPath))
    {
        return ([], "epata-business-ledger-missing.db");
    }

    var fileName = $"epata-business-ledger-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}.db";
    var backupDir = retainServerCopy
        ? Path.Combine(env.ContentRootPath, "Backups")
        : Path.Combine(Path.GetTempPath(), "EPATA-BusinessLedger-Backups");
    Directory.CreateDirectory(backupDir);
    var backupPath = Path.Combine(backupDir, fileName);
    var sourceConnectionString = new SqliteConnectionStringBuilder(ResolveConnectionString(configuration, env.ContentRootPath))
    {
        Pooling = false
    }.ToString();
    var destinationConnectionString = new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString();

    try
    {
        await using (var source = new SqliteConnection(sourceConnectionString))
        await using (var destination = new SqliteConnection(destinationConnectionString))
        {
            await source.OpenAsync();
            await destination.OpenAsync();
            source.BackupDatabase(destination);
        }

        SqliteConnection.ClearAllPools();
        var bytes = await System.IO.File.ReadAllBytesAsync(backupPath);
        return (bytes, fileName);
    }
    finally
    {
        if (!retainServerCopy)
        {
            TryDeleteFile(backupPath);
            try
            {
                if (Directory.Exists(backupDir) && !Directory.EnumerateFileSystemEntries(backupDir).Any())
                {
                    Directory.Delete(backupDir);
                }
            }
            catch
            {
                // Another backup may be using the shared temporary directory.
            }
        }
    }
}

static string GetDatabasePath(IConfiguration configuration, IWebHostEnvironment env)
{
    var connectionString = ResolveConnectionString(configuration, env.ContentRootPath);
    var builder = new SqliteConnectionStringBuilder(connectionString);
    var dataSource = builder.DataSource;
    return Path.IsPathRooted(dataSource) ? dataSource : Path.GetFullPath(Path.Combine(env.ContentRootPath, dataSource));
}

static string MakeSafeFileName(string fileName)
{
    var name = Path.GetFileName(fileName);
    foreach (var c in Path.GetInvalidFileNameChars())
    {
        name = name.Replace(c, '-');
    }

    return string.IsNullOrWhiteSpace(name) ? "uploaded-document" : name;
}

static string ContentTypeForFile(string filePath)
{
    return Path.GetExtension(filePath).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        ".svg" => "image/svg+xml",
        ".txt" => "text/plain; charset=utf-8",
        ".csv" => "text/csv; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".md" => "text/markdown; charset=utf-8",
        _ => "application/octet-stream"
    };
}

static bool IsSupportedProofUploadFile(string? fileName)
{
    var extension = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
    return extension is ".pdf" or ".docx" or ".png" or ".jpg" or ".jpeg" or ".webp" or ".txt" or ".csv" or ".json" or ".md";
}

static bool IsCrossSiteApiMutation(HttpRequest request)
{
    if (!request.Path.StartsWithSegments("/api")
        || HttpMethods.IsGet(request.Method)
        || HttpMethods.IsHead(request.Method)
        || HttpMethods.IsOptions(request.Method))
    {
        return false;
    }

    var fetchSite = request.Headers["Sec-Fetch-Site"].FirstOrDefault();
    if (fetchSite?.Equals("cross-site", StringComparison.OrdinalIgnoreCase) == true)
    {
        return true;
    }

    var originValue = request.Headers.Origin.FirstOrDefault();
    if (string.IsNullOrWhiteSpace(originValue))
    {
        // Local scripts and integration tools do not send Origin. Browser cross-site
        // requests do, and modern browsers also send Sec-Fetch-Site above.
        return false;
    }

    if (!Uri.TryCreate(originValue, UriKind.Absolute, out var origin)
        || !origin.Scheme.Equals(request.Scheme, StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    var requestPort = request.Host.Port ?? (request.IsHttps ? 443 : 80);
    return !origin.Host.Equals(request.Host.Host, StringComparison.OrdinalIgnoreCase)
        || origin.Port != requestPort;
}

static bool IsSqliteBusyOrLocked(Exception exception)
{
    for (var current = exception; current is not null; current = current.InnerException)
    {
        if (current is SqliteException sqlite
            && (sqlite.SqliteErrorCode is 5 or 6
                || sqlite.SqliteExtendedErrorCode is 5 or 6
                || current.Message.Contains("locked", StringComparison.OrdinalIgnoreCase)
                || current.Message.Contains("busy", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (current.Message.Contains("database is locked", StringComparison.OrdinalIgnoreCase)
            || current.Message.Contains("database is busy", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
    }

    return false;
}

static bool IsSqliteConstraintViolation(Exception exception)
{
    for (var current = exception; current is not null; current = current.InnerException)
    {
        if (current is SqliteException sqlite && sqlite.SqliteErrorCode == 19)
        {
            return true;
        }
    }

    return false;
}

static bool IsAllowedProofFilePath(string contentRootPath, string fullPath)
{
    if (!Path.IsPathFullyQualified(fullPath)) return false;

    var roots = new List<string>
    {
        Path.GetFullPath(Path.Combine(contentRootPath, "UploadedDocs"))
    };

    var current = new DirectoryInfo(contentRootPath);
    while (current is not null)
    {
        if (current.Name.Equals("__EPATA 3D Print Business Folder", StringComparison.OrdinalIgnoreCase))
        {
            var financeProofRoot = Path.Combine(current.FullName, "03_Finances");
            if (Directory.Exists(financeProofRoot))
            {
                roots.Add(Path.GetFullPath(financeProofRoot));
            }
            break;
        }

        current = current.Parent;
    }

    return roots.Distinct(StringComparer.OrdinalIgnoreCase).Any(root =>
    {
        var normalized = Path.GetFullPath(root);
        var rootWithSeparator = normalized.EndsWith(Path.DirectorySeparatorChar)
            ? normalized
            : normalized + Path.DirectorySeparatorChar;
        return fullPath.Equals(normalized, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    });
}

static string AppendNote(string? existing, string note)
{
    if (string.IsNullOrWhiteSpace(existing))
    {
        return note;
    }

    return existing.Contains(note, StringComparison.OrdinalIgnoreCase)
        ? existing
        : $"{existing.Trim()}\n{note}";
}

static string GuessDocumentType(string fileName, string? extractedText = null)
{
    var lower = fileName.ToLowerInvariant();
    var text = extractedText ?? string.Empty;
    if (text.Contains("etsy", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(text, @"(?i)\border\s*(?:number|no\.?|#)?\s*[:#]?\s*\d{7,}")
        && (text.Contains("Etsy Payments", StringComparison.OrdinalIgnoreCase)
            || text.Contains("Order total", StringComparison.OrdinalIgnoreCase))) return "Etsy Order";
    if (lower.Contains("etsy") || lower.Contains("order")) return "Etsy Order";
    if (lower.Contains("invoice") || lower.Contains("inv-")) return "Invoice";
    if (lower.Contains("receipt")) return "Receipt";
    if (lower.Contains("tax")) return "Tax";
    if (lower.Contains("bank") || lower.Contains("statement")) return "Bank";
    return "Other";
}

static string TrimPreview(string text)
{
    var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    return cleaned.Length <= 1800 ? cleaned : cleaned[..1800] + "...";
}

static DateTime? ParseDate(string? value)
{
    return DateTime.TryParse(value, out var parsed) ? parsed.Date : null;
}

static DateTime ParseTimelineDate(string? value)
{
    return DateTime.TryParse(value, out var parsed) ? parsed : DateTime.Now;
}

static InvoiceDocumentAuditState CaptureInvoiceDocumentAuditState(InvoiceDocument doc)
{
    return new InvoiceDocumentAuditState(
        doc.DocType,
        doc.DocNumber,
        doc.Status,
        doc.CustomerName,
        doc.ProjectName,
        doc.Total,
        doc.AmountPaid,
        doc.Balance,
        string.Join("|", doc.LineItems
            .OrderBy(li => li.SortOrder)
            .Select(li => $"{li.Description}~{li.Details}~{li.Quantity}~{li.Rate}~{li.Amount}")));
}

static void AddInvoiceDocumentChangeEvents(AppDbContext db, InvoiceDocument doc, InvoiceDocumentAuditState before)
{
    var added = false;
    if (!string.Equals(before.Status, doc.Status, StringComparison.OrdinalIgnoreCase))
    {
        AddInvoiceDocumentEvent(db, doc, "StatusChanged", before.Status, doc.Status,
            $"{doc.DocType} status changed: {before.Status} -> {doc.Status}",
            $"{doc.DocNumber} moved from {before.Status} to {doc.Status}.", doc.Total);
        added = true;
    }

    if (before.Total != doc.Total || before.AmountPaid != doc.AmountPaid || before.Balance != doc.Balance)
    {
        AddInvoiceDocumentEvent(db, doc, "MoneyChanged", doc.Status, doc.Status,
            $"{doc.DocType} money changed",
            $"Total {before.Total:C} -> {doc.Total:C}; paid {before.AmountPaid:C} -> {doc.AmountPaid:C}; balance {before.Balance:C} -> {doc.Balance:C}.", doc.Total);
        added = true;
    }

    if (!string.Equals(before.CustomerName ?? string.Empty, doc.CustomerName ?? string.Empty, StringComparison.Ordinal)
        || !string.Equals(before.ProjectName ?? string.Empty, doc.ProjectName ?? string.Empty, StringComparison.Ordinal))
    {
        AddInvoiceDocumentEvent(db, doc, "IdentityChanged", doc.Status, doc.Status,
            $"{doc.DocType} customer/project changed",
            $"Customer '{before.CustomerName}' -> '{doc.CustomerName}'; project '{before.ProjectName}' -> '{doc.ProjectName}'.", doc.Total);
        added = true;
    }

    var lineSignature = string.Join("|", doc.LineItems
        .OrderBy(li => li.SortOrder)
        .Select(li => $"{li.Description}~{li.Details}~{li.Quantity}~{li.Rate}~{li.Amount}"));
    if (!string.Equals(before.LineSignature, lineSignature, StringComparison.Ordinal))
    {
        AddInvoiceDocumentEvent(db, doc, "LineItemsChanged", doc.Status, doc.Status,
            $"{doc.DocType} line items changed",
            $"{doc.DocNumber} line items were edited.", doc.Total);
        added = true;
    }

    if (!added)
    {
        AddInvoiceDocumentEvent(db, doc, "Saved", doc.Status, doc.Status,
            $"{doc.DocType} saved",
            $"{doc.DocNumber} was saved without a tracked status, money, customer, project, or line-item change.", doc.Total);
    }
}

static async Task RepointUnifiedDocumentLedgerLinksAsync(
    AppDbContext db,
    InvoiceDocument doc,
    InvoiceDocumentAuditState before,
    CancellationToken cancellationToken = default)
{
    var oldNumber = before.DocNumber?.Trim();
    var newNumber = doc.DocNumber?.Trim();
    if (string.IsNullOrWhiteSpace(oldNumber)
        || string.IsNullOrWhiteSpace(newNumber)
        || string.Equals(oldNumber, newNumber, StringComparison.OrdinalIgnoreCase)
        || !string.Equals(before.DocType, doc.DocType, StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var now = DateTime.UtcNow;
    if (doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase))
    {
        var oldSource = $"Unified invoice {oldNumber}";
        var newSource = $"Unified invoice {newNumber}";
        var oldSourceIdentity = oldSource.ToUpperInvariant();

        var invoices = await db.ReceivableInvoices
            .Where(x => x.SourceProof != null && x.SourceProof.Trim().ToUpper() == oldSourceIdentity)
            .ToListAsync(cancellationToken);
        foreach (var invoice in invoices)
        {
            invoice.OriginalInvoiceNumber ??= oldNumber;
            invoice.InvoiceNumber = newNumber;
            invoice.SourceProof = newSource;
            invoice.UpdatedAtUtc = now;
        }

        var sales = await db.Sales
            .Where(x => x.Platform.ToUpper() == "DIRECT"
                && x.SourceProof != null
                && x.SourceProof.Trim().ToUpper() == oldSourceIdentity)
            .ToListAsync(cancellationToken);
        foreach (var sale in sales)
        {
            sale.InvoiceNumber = newNumber;
            sale.SourceProof = newSource;
            sale.UpdatedAtUtc = now;
        }
    }
    else if (doc.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase))
    {
        var oldSource = $"Unified estimate {oldNumber}";
        var newSource = $"Unified estimate {newNumber}";
        var oldSourceIdentity = oldSource.ToUpperInvariant();

        var jobs = await db.CustomerJobs
            .Where(x => x.SourceProof != null && x.SourceProof.Trim().ToUpper() == oldSourceIdentity)
            .ToListAsync(cancellationToken);
        foreach (var job in jobs)
        {
            job.RelatedInvoiceNumber = newNumber;
            job.SourceProof = newSource;
            job.UpdatedAtUtc = now;
        }
    }
}

static void AddInvoiceDocumentEvent(AppDbContext db, InvoiceDocument doc, string eventType, string? fromStatus, string? toStatus, string summary, string detail, decimal? amount)
{
    db.InvoiceDocumentEvents.Add(new InvoiceDocumentEvent
    {
        InvoiceDocumentId = doc.Id,
        DocNumber = doc.DocNumber,
        DocType = doc.DocType,
        EventType = eventType,
        FromStatus = fromStatus,
        ToStatus = toStatus,
        Summary = summary,
        Detail = detail,
        Amount = amount,
        CreatedAt = DateTimeOffset.UtcNow.ToString("O")
    });
}

static string NormalizeInvoiceStatus(string status, decimal total, decimal paid)
{
    if (status.Equals("Void", StringComparison.OrdinalIgnoreCase)) return "Void";
    if (paid >= total && total > 0) return "Paid";
    if (paid > 0) return "Partial";
    if (status.Equals("Draft", StringComparison.OrdinalIgnoreCase)) return "Draft";
    return "Sent";
}

static bool IsClosedInvoiceDocumentStatus(string? status)
{
    return status is null
        || status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Paid", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Void", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase);
}

static async Task EnsureUnifiedInvoiceTablesAsync(AppDbContext db)
{
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "InvoiceDocuments" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_InvoiceDocuments" PRIMARY KEY AUTOINCREMENT,
            "SourceEstimateId" INTEGER NULL,
            "DocNumber" TEXT NULL,
            "DocType" TEXT NOT NULL DEFAULT 'ESTIMATE',
            "Status" TEXT NOT NULL DEFAULT 'Draft',
            "CustomerName" TEXT NULL,
            "CustomerPhone" TEXT NULL,
            "CustomerAddress" TEXT NULL,
            "CustomerEmail" TEXT NULL,
            "PreparedFor" TEXT NULL,
            "ProjectName" TEXT NULL,
            "Material" TEXT NULL,
            "Color" TEXT NULL,
            "Infill" TEXT NULL,
            "ProjectDescription" TEXT NULL,
            "ProjectNotes" TEXT NULL,
            "PageSize" TEXT NULL,
            "DocDate" TEXT NULL,
            "DueDate" TEXT NULL,
            "Subtotal" TEXT NOT NULL DEFAULT '0.0',
            "DiscountAmount" TEXT NOT NULL DEFAULT '0.0',
            "RushAmount" TEXT NOT NULL DEFAULT '0.0',
            "TaxAmount" TEXT NOT NULL DEFAULT '0.0',
            "Total" TEXT NOT NULL DEFAULT '0.0',
            "AmountPaid" TEXT NOT NULL DEFAULT '0.0',
            "Balance" TEXT NOT NULL DEFAULT '0.0',
            "PaymentMethod" TEXT NOT NULL DEFAULT 'Unknown / Review',
            "PricingGuide" TEXT NULL,
            "TermsNotes" TEXT NULL,
            "StandardTurnaround" TEXT NULL,
            "RushTurnaround" TEXT NULL,
            "CalcGrams" TEXT NOT NULL DEFAULT '0.0',
            "CalcHours" TEXT NOT NULL DEFAULT '0.0',
            "CalcDesignHours" TEXT NOT NULL DEFAULT '0.0',
            "CalcSetupFee" TEXT NOT NULL DEFAULT '0.0',
            "CalcPostFee" TEXT NOT NULL DEFAULT '0.0',
            "CalcGramRate" TEXT NOT NULL DEFAULT '0.05',
            "CalcHourRate" TEXT NOT NULL DEFAULT '3.0',
            "CalcDesignRate" TEXT NOT NULL DEFAULT '25.0',
            "CalcMinimum" TEXT NOT NULL DEFAULT '15.0',
            "CalcDifficulty" TEXT NOT NULL DEFAULT '1.0',
            "CalcRush" TEXT NOT NULL DEFAULT '0.0',
            "CalcDiscount" TEXT NOT NULL DEFAULT '0.0',
            "CalcTaxRate" TEXT NOT NULL DEFAULT '0.0',
            "Json" TEXT NOT NULL DEFAULT '{{}}',
            "IsArchived" INTEGER NOT NULL DEFAULT 0,
            "ArchivedAt" TEXT NULL,
            "ArchiveReason" TEXT NULL,
            "CreatedAt" TEXT NOT NULL,
            "UpdatedAt" TEXT NOT NULL
        );
        """);

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "InvoiceLineItems" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_InvoiceLineItems" PRIMARY KEY AUTOINCREMENT,
            "InvoiceDocumentId" INTEGER NOT NULL,
            "SortOrder" INTEGER NOT NULL,
            "Description" TEXT NULL,
            "Details" TEXT NULL,
            "Quantity" TEXT NOT NULL DEFAULT '1.0',
            "Rate" TEXT NOT NULL DEFAULT '0.0',
            "Amount" TEXT NOT NULL DEFAULT '0.0',
            CONSTRAINT "FK_InvoiceLineItems_InvoiceDocuments_InvoiceDocumentId"
                FOREIGN KEY ("InvoiceDocumentId") REFERENCES "InvoiceDocuments" ("Id") ON DELETE CASCADE
        );
        """);

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "InvoiceDocumentEvents" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_InvoiceDocumentEvents" PRIMARY KEY AUTOINCREMENT,
            "InvoiceDocumentId" INTEGER NOT NULL,
            "DocNumber" TEXT NULL,
            "DocType" TEXT NOT NULL DEFAULT 'ESTIMATE',
            "EventType" TEXT NOT NULL DEFAULT 'Updated',
            "FromStatus" TEXT NULL,
            "ToStatus" TEXT NULL,
            "Summary" TEXT NOT NULL,
            "Detail" TEXT NULL,
            "Amount" TEXT NULL,
            "CreatedAt" TEXT NOT NULL
        );
        """);

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "TaxObligations" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_TaxObligations" PRIMARY KEY AUTOINCREMENT,
            "TaxYear" INTEGER NOT NULL,
            "Title" TEXT NOT NULL,
            "Jurisdiction" TEXT NOT NULL DEFAULT 'Federal',
            "ObligationType" TEXT NOT NULL DEFAULT 'Other',
            "FormName" TEXT NULL,
            "Period" TEXT NULL,
            "DueDate" TEXT NULL,
            "Status" TEXT NOT NULL DEFAULT 'Review Applicability',
            "EstimatedAmount" TEXT NULL,
            "AmountPaid" TEXT NULL,
            "PaidOrFiledDate" TEXT NULL,
            "ConfirmationNumber" TEXT NULL,
            "ProofReference" TEXT NULL,
            "OfficialUrl" TEXT NULL,
            "AppliesIf" TEXT NULL,
            "NeedsReview" INTEGER NOT NULL DEFAULT 1,
            "Notes" TEXT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL,
            "IsArchived" INTEGER NOT NULL DEFAULT 0
        );
        """);

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "MileageLogs" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_MileageLogs" PRIMARY KEY AUTOINCREMENT,
            "TripDate" TEXT NULL,
            "Vehicle" TEXT NULL,
            "StartLocation" TEXT NULL,
            "EndLocation" TEXT NULL,
            "BusinessPurpose" TEXT NOT NULL,
            "BusinessMiles" TEXT NULL,
            "ParkingAndTolls" TEXT NULL,
            "ProofReference" TEXT NULL,
            "NeedsReview" INTEGER NOT NULL DEFAULT 0,
            "Notes" TEXT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL,
            "IsArchived" INTEGER NOT NULL DEFAULT 0
        );
        """);

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "CustomerCommunications" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_CustomerCommunications" PRIMARY KEY AUTOINCREMENT,
            "OccurredAt" TEXT NULL,
            "CustomerName" TEXT NOT NULL,
            "Direction" TEXT NOT NULL DEFAULT 'Outgoing',
            "Channel" TEXT NOT NULL DEFAULT 'Email',
            "Subject" TEXT NULL,
            "Summary" TEXT NOT NULL,
            "CustomerJobId" INTEGER NULL,
            "RelatedJobNumber" TEXT NULL,
            "RelatedOrderNumber" TEXT NULL,
            "RelatedInvoiceNumber" TEXT NULL,
            "FollowUpDate" TEXT NULL,
            "FollowUpStatus" TEXT NOT NULL DEFAULT 'None',
            "SourceProof" TEXT NULL,
            "NeedsReview" INTEGER NOT NULL DEFAULT 0,
            "Notes" TEXT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL,
            "IsArchived" INTEGER NOT NULL DEFAULT 0
        );
        """);

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "PrinterQueueItems" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_PrinterQueueItems" PRIMARY KEY AUTOINCREMENT,
            "QueueDate" TEXT NULL,
            "Priority" TEXT NOT NULL DEFAULT 'Normal',
            "Status" TEXT NOT NULL DEFAULT 'Queued',
            "PrinterName" TEXT NULL,
            "CustomerJobId" INTEGER NULL,
            "CustomerName" TEXT NULL,
            "JobName" TEXT NOT NULL,
            "RelatedOrderNumber" TEXT NULL,
            "RelatedInvoiceNumber" TEXT NULL,
            "ProductName" TEXT NULL,
            "Material" TEXT NULL,
            "Color" TEXT NULL,
            "Quantity" INTEGER NOT NULL DEFAULT 1,
            "PlateCount" INTEGER NOT NULL DEFAULT 1,
            "EstimatedHours" TEXT NULL,
            "ActualHours" TEXT NULL,
            "ProgressPercent" TEXT NOT NULL DEFAULT '0.0',
            "ScheduledStart" TEXT NULL,
            "StartedAt" TEXT NULL,
            "EstimatedFinish" TEXT NULL,
            "CompletedAt" TEXT NULL,
            "FailureCount" INTEGER NOT NULL DEFAULT 0,
            "SourceProof" TEXT NULL,
            "NeedsReview" INTEGER NOT NULL DEFAULT 0,
            "Notes" TEXT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL,
            "IsArchived" INTEGER NOT NULL DEFAULT 0
        );
        """);

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "OrderLossIncidents" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_OrderLossIncidents" PRIMARY KEY AUTOINCREMENT,
            "IncidentDate" TEXT NULL,
            "SaleId" INTEGER NULL,
            "Platform" TEXT NOT NULL DEFAULT 'Other',
            "OrderNumber" TEXT NULL,
            "CustomerName" TEXT NULL,
            "ProductName" TEXT NOT NULL DEFAULT '',
            "IncidentType" TEXT NOT NULL DEFAULT 'Damaged in transit',
            "Resolution" TEXT NOT NULL DEFAULT 'Replacement / reship',
            "Status" TEXT NOT NULL DEFAULT 'Resolved',
            "CustomerRefund" TEXT NULL,
            "ReplacementCogs" TEXT NULL,
            "AdditionalShippingCost" TEXT NULL,
            "OtherCost" TEXT NULL,
            "ReimbursementReceived" TEXT NULL,
            "ReimbursementSource" TEXT NULL,
            "ClaimNumber" TEXT NULL,
            "CountInTaxReports" INTEGER NOT NULL DEFAULT 1,
            "SourceProof" TEXT NULL,
            "NeedsReview" INTEGER NOT NULL DEFAULT 0,
            "Notes" TEXT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL,
            "IsArchived" INTEGER NOT NULL DEFAULT 0
        );
        """);

    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS "TaxYearSetups" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_TaxYearSetups" PRIMARY KEY AUTOINCREMENT,
            "TaxYear" INTEGER NOT NULL,
            "BusinessName" TEXT NOT NULL DEFAULT 'EPATA 3D Prints',
            "PrincipalBusinessActivity" TEXT NOT NULL DEFAULT '3D printed products and custom fabrication',
            "PrincipalBusinessCode" TEXT NOT NULL DEFAULT '',
            "Ein" TEXT NULL,
            "BusinessAddress" TEXT NOT NULL DEFAULT '',
            "AccountingMethod" TEXT NOT NULL DEFAULT 'Cash',
            "MateriallyParticipated" TEXT NOT NULL DEFAULT 'Review',
            "StartedOrAcquiredThisYear" TEXT NOT NULL DEFAULT 'Review',
            "MadeReportablePayments" TEXT NOT NULL DEFAULT 'Review',
            "FiledRequired1099s" TEXT NOT NULL DEFAULT 'Not applicable / Review',
            "AtRiskStatus" TEXT NOT NULL DEFAULT 'Review if the business has a loss',
            "CogsMethod" TEXT NOT NULL DEFAULT 'Not confirmed',
            "InventoryValuationMethod" TEXT NOT NULL DEFAULT 'Cost',
            "InventoryMethodChanged" TEXT NOT NULL DEFAULT 'No / Review',
            "BeginningInventory" TEXT NOT NULL DEFAULT '0',
            "PurchasesLessPersonalUse" TEXT NOT NULL DEFAULT '0',
            "CostOfLabor" TEXT NOT NULL DEFAULT '0',
            "MaterialsAndSupplies" TEXT NOT NULL DEFAULT '0',
            "OtherCogsCosts" TEXT NOT NULL DEFAULT '0',
            "EndingInventory" TEXT NOT NULL DEFAULT '0',
            "Form1099KTotal" TEXT NOT NULL DEFAULT '0',
            "Form1099NecTotal" TEXT NOT NULL DEFAULT '0',
            "Form1099MiscTotal" TEXT NOT NULL DEFAULT '0',
            "OtherBusinessIncome" TEXT NOT NULL DEFAULT '0',
            "VehicleDeductionMethod" TEXT NOT NULL DEFAULT 'Not confirmed',
            "VehiclePlacedInService" TEXT NULL,
            "CommutingMiles" TEXT NOT NULL DEFAULT '0',
            "OtherPersonalMiles" TEXT NOT NULL DEFAULT '0',
            "VehicleAvailableForPersonalUse" TEXT NOT NULL DEFAULT 'Review',
            "AnotherVehicleAvailable" TEXT NOT NULL DEFAULT 'Review',
            "VehicleEvidence" TEXT NOT NULL DEFAULT 'Review',
            "VehicleEvidenceWritten" TEXT NOT NULL DEFAULT 'Review',
            "HomeOfficeMethod" TEXT NOT NULL DEFAULT 'Not claimed / Review',
            "HomeSquareFeet" TEXT NOT NULL DEFAULT '0',
            "OfficeSquareFeet" TEXT NOT NULL DEFAULT '0',
            "HomeOfficeDeduction" TEXT NOT NULL DEFAULT '0',
            "Notes" TEXT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL,
            "IsArchived" INTEGER NOT NULL DEFAULT 0
        );
        """);

    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceDocuments_DocNumber\" ON \"InvoiceDocuments\" (\"DocNumber\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceDocuments_UpdatedAt\" ON \"InvoiceDocuments\" (\"UpdatedAt\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceLineItems_InvoiceDocumentId\" ON \"InvoiceLineItems\" (\"InvoiceDocumentId\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceDocumentEvents_InvoiceDocumentId\" ON \"InvoiceDocumentEvents\" (\"InvoiceDocumentId\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceDocumentEvents_CreatedAt\" ON \"InvoiceDocumentEvents\" (\"CreatedAt\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_TaxObligations_DueDate\" ON \"TaxObligations\" (\"DueDate\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_TaxObligations_TaxYear_Title_Period\" ON \"TaxObligations\" (\"TaxYear\", \"Title\", \"Period\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_MileageLogs_TripDate\" ON \"MileageLogs\" (\"TripDate\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_CustomerCommunications_CustomerName\" ON \"CustomerCommunications\" (\"CustomerName\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_CustomerCommunications_OccurredAt\" ON \"CustomerCommunications\" (\"OccurredAt\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_PrinterQueueItems_Status\" ON \"PrinterQueueItems\" (\"Status\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_PrinterQueueItems_PrinterName\" ON \"PrinterQueueItems\" (\"PrinterName\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_PrinterQueueItems_CustomerJobId\" ON \"PrinterQueueItems\" (\"CustomerJobId\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_OrderLossIncidents_IncidentDate\" ON \"OrderLossIncidents\" (\"IncidentDate\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_OrderLossIncidents_OrderNumber\" ON \"OrderLossIncidents\" (\"OrderNumber\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_OrderLossIncidents_SaleId\" ON \"OrderLossIncidents\" (\"SaleId\");");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_TaxYearSetups_TaxYear\" ON \"TaxYearSetups\" (\"TaxYear\");");

    // ── Schema migrations for new fields (safe on existing DBs) ──────────────
    var alterations = new (string Table, string Column, string Sql)[]
    {
        // Expenses
        ("Expenses", "TaxBucket", "ALTER TABLE \"Expenses\" ADD COLUMN \"TaxBucket\" TEXT NOT NULL DEFAULT 'Operating Expense'"),
        ("Expenses", "DeductibleStatus", "ALTER TABLE \"Expenses\" ADD COLUMN \"DeductibleStatus\" TEXT NOT NULL DEFAULT 'Yes'"),
        ("Expenses", "BusinessUsePercent", "ALTER TABLE \"Expenses\" ADD COLUMN \"BusinessUsePercent\" TEXT NULL"),
        ("Expenses", "CountedExpense", "ALTER TABLE \"Expenses\" ADD COLUMN \"CountedExpense\" INTEGER NOT NULL DEFAULT 1"),
        ("Expenses", "TaxCategory", "ALTER TABLE \"Expenses\" ADD COLUMN \"TaxCategory\" TEXT NOT NULL DEFAULT 'Other business expense'"),
        ("Expenses", "PaymentMethod", "ALTER TABLE \"Expenses\" ADD COLUMN \"PaymentMethod\" TEXT NOT NULL DEFAULT 'Unknown / Review'"),
        // Assets
        ("Assets", "InServiceDate", "ALTER TABLE \"Assets\" ADD COLUMN \"InServiceDate\" TEXT NULL"),
        ("Assets", "TaxTreatment", "ALTER TABLE \"Assets\" ADD COLUMN \"TaxTreatment\" TEXT NOT NULL DEFAULT 'Review'"),
        ("Assets", "CountedExpenseThisYear", "ALTER TABLE \"Assets\" ADD COLUMN \"CountedExpenseThisYear\" INTEGER NOT NULL DEFAULT 0"),
        ("Assets", "NotYetExpensed", "ALTER TABLE \"Assets\" ADD COLUMN \"NotYetExpensed\" TEXT NULL"),
        ("Assets", "PaymentMethod", "ALTER TABLE \"Assets\" ADD COLUMN \"PaymentMethod\" TEXT NOT NULL DEFAULT 'Unknown / Review'"),
        // Customer work and payment trackers
        ("CustomerJobs", "PaymentMethod", "ALTER TABLE \"CustomerJobs\" ADD COLUMN \"PaymentMethod\" TEXT NOT NULL DEFAULT 'Unknown / Review'"),
        ("ReceivableInvoices", "PaymentMethod", "ALTER TABLE \"ReceivableInvoices\" ADD COLUMN \"PaymentMethod\" TEXT NOT NULL DEFAULT 'Unknown / Review'"),
        ("Bills", "PaymentMethod", "ALTER TABLE \"Bills\" ADD COLUMN \"PaymentMethod\" TEXT NOT NULL DEFAULT 'Unknown / Review'"),
        ("Bills", "TaxCategory", "ALTER TABLE \"Bills\" ADD COLUMN \"TaxCategory\" TEXT NOT NULL DEFAULT 'Other business expense'"),
        ("TaxObligations", "PaymentMethod", "ALTER TABLE \"TaxObligations\" ADD COLUMN \"PaymentMethod\" TEXT NOT NULL DEFAULT 'Unknown / Review'"),
        // MakerWorld
        ("MakerWorldRewards", "IncomeStatus", "ALTER TABLE \"MakerWorldRewards\" ADD COLUMN \"IncomeStatus\" TEXT NOT NULL DEFAULT 'Review'"),
        // Payment-channel tax exports
        ("Sales", "SourceReceivableInvoiceId", "ALTER TABLE \"Sales\" ADD COLUMN \"SourceReceivableInvoiceId\" INTEGER NULL"),
        ("Sales", "PaymentMethod", "ALTER TABLE \"Sales\" ADD COLUMN \"PaymentMethod\" TEXT NOT NULL DEFAULT 'Unknown / Review'"),
        ("Sales", "SalesTaxHandling", "ALTER TABLE \"Sales\" ADD COLUMN \"SalesTaxHandling\" TEXT NOT NULL DEFAULT 'Unknown / Review'"),
        ("AuditDocuments", "UploadFingerprint", "ALTER TABLE \"AuditDocuments\" ADD COLUMN \"UploadFingerprint\" TEXT NULL"),
        ("InvoiceDocuments", "PaymentMethod", "ALTER TABLE \"InvoiceDocuments\" ADD COLUMN \"PaymentMethod\" TEXT NOT NULL DEFAULT 'Unknown / Review'"),
        // Unified invoice/estimate archive safety
        ("InvoiceDocuments", "SourceEstimateId", "ALTER TABLE \"InvoiceDocuments\" ADD COLUMN \"SourceEstimateId\" INTEGER NULL"),
        ("InvoiceDocuments", "IsArchived", "ALTER TABLE \"InvoiceDocuments\" ADD COLUMN \"IsArchived\" INTEGER NOT NULL DEFAULT 0"),
        ("InvoiceDocuments", "ArchivedAt", "ALTER TABLE \"InvoiceDocuments\" ADD COLUMN \"ArchivedAt\" TEXT NULL"),
        ("InvoiceDocuments", "ArchiveReason", "ALTER TABLE \"InvoiceDocuments\" ADD COLUMN \"ArchiveReason\" TEXT NULL"),
    };

    foreach (var alteration in alterations)
    {
        await AddSqliteColumnIfMissingAsync(db, alteration.Table, alteration.Column, alteration.Sql);
    }

    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_InvoiceDocuments_SourceEstimateId\" ON \"InvoiceDocuments\" (\"SourceEstimateId\") WHERE \"SourceEstimateId\" IS NOT NULL;");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Sales_SourceReceivableInvoiceId\" ON \"Sales\" (\"SourceReceivableInvoiceId\") WHERE \"SourceReceivableInvoiceId\" IS NOT NULL;");
    await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_AuditDocuments_UploadFingerprint\" ON \"AuditDocuments\" (\"UploadFingerprint\") WHERE \"UploadFingerprint\" IS NOT NULL AND \"IsArchived\" = 0;");
}

static async Task AddSqliteColumnIfMissingAsync(AppDbContext db, string tableName, string columnName, string sql)
{
    if (await SqliteColumnExistsAsync(db, tableName, columnName))
    {
        return;
    }

    await db.Database.ExecuteSqlRawAsync(sql);
}

static async Task<bool> SqliteColumnExistsAsync(AppDbContext db, string tableName, string columnName)
{
    var connection = db.Database.GetDbConnection();
    var closeAfter = connection.State != System.Data.ConnectionState.Open;
    if (closeAfter)
    {
        await connection.OpenAsync();
    }

    try
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info({QuoteSqliteIdentifier(tableName)})";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            if (reader.FieldCount > 1 && string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
    finally
    {
        if (closeAfter)
        {
            await connection.CloseAsync();
        }
    }
}

static string QuoteSqliteIdentifier(string identifier) => "\"" + identifier.Replace("\"", "\"\"") + "\"";

#pragma warning disable CS8321 // Quarantined one-time repair helpers; never run automatically on startup.
static async Task SeedUnifiedInvoiceDocumentsFromLedgerAsync(AppDbContext db)
{
    if (await db.InvoiceDocuments.AnyAsync())
    {
        return;
    }

    var existingInvoices = await db.ReceivableInvoices.AsNoTracking()
        .Where(x => !x.IsArchived && !string.IsNullOrWhiteSpace(x.InvoiceNumber))
        .ToListAsync();
    foreach (var invoice in existingInvoices)
    {
        db.InvoiceDocuments.Add(new InvoiceDocument
        {
            DocNumber = invoice.InvoiceNumber,
            DocType = "INVOICE",
            Status = invoice.Status,
            CustomerName = invoice.CustomerName,
            ProjectName = invoice.ProjectName,
            DocDate = invoice.InvoiceDate?.ToString("yyyy-MM-dd"),
            DueDate = invoice.DueDate?.ToString("yyyy-MM-dd"),
            Subtotal = invoice.Subtotal ?? invoice.InvoiceTotal ?? 0,
            DiscountAmount = invoice.Discount ?? 0,
            RushAmount = invoice.RushFee ?? 0,
            TaxAmount = invoice.SalesTax ?? 0,
            Total = invoice.InvoiceTotal ?? 0,
            AmountPaid = invoice.AmountPaid ?? 0,
            Balance = Math.Max(0, (invoice.InvoiceTotal ?? 0) - (invoice.AmountPaid ?? 0)),
            CalcTaxRate = invoice.TaxRatePercent ?? 0,
            TermsNotes = "Imported from the existing AR invoice register during unified-app setup.",
            CreatedAt = invoice.CreatedAtUtc.ToString("O"),
            UpdatedAt = invoice.UpdatedAtUtc.ToString("O"),
            LineItems =
            [
                new InvoiceLineItem
                {
                    SortOrder = 1,
                    Description = invoice.ProjectName ?? "Invoice",
                    Quantity = 1,
                    Rate = invoice.Subtotal ?? invoice.InvoiceTotal ?? 0,
                    Amount = invoice.Subtotal ?? invoice.InvoiceTotal ?? 0
                }
            ]
        });
    }

    await db.SaveChangesAsync();
}

static async Task NormalizeInvoiceDocumentRowsAsync(AppDbContext db)
{
    var changed = false;
    var allDocs = await db.InvoiceDocuments.Include(d => d.LineItems).ToListAsync();
    foreach (var doc in allDocs)
    {
        var subtotal = doc.LineItems.Count > 0 ? doc.LineItems.Sum(x => Math.Max(0, x.Quantity) * Math.Max(0, x.Rate)) : doc.Subtotal;
        var discount = Math.Max(0, doc.DiscountAmount);
        var rush = Math.Max(0, doc.RushAmount);
        var taxRate = Math.Clamp(doc.CalcTaxRate, 0, 30);
        var taxable = MoneyRules.InvoiceTaxableSalesBase(subtotal, discount, rush);
        var tax = taxRate > 0 ? taxable * taxRate / 100m : Math.Max(0, doc.TaxAmount);
        var total = taxable + tax;
        var paid = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) && !doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase)
            ? Math.Min(Math.Max(0, doc.AmountPaid), total)
            : 0;
        var balance = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) && !doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase)
            ? Math.Max(0, total - paid)
            : 0;

        if (Math.Abs(doc.Subtotal - subtotal) > 0.02m
            || Math.Abs(doc.DiscountAmount - discount) > 0.02m
            || Math.Abs(doc.RushAmount - rush) > 0.02m
            || Math.Abs(doc.TaxAmount - tax) > 0.02m
            || Math.Abs(doc.Total - total) > 0.02m
            || Math.Abs(doc.AmountPaid - paid) > 0.02m
            || Math.Abs(doc.Balance - balance) > 0.02m)
        {
            doc.Subtotal = subtotal;
            doc.DiscountAmount = discount;
            doc.RushAmount = rush;
            doc.TaxAmount = tax;
            doc.Total = total;
            doc.AmountPaid = paid;
            doc.Balance = balance;
            doc.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
            changed = true;
        }
    }

    var estimatesWithPayment = await db.InvoiceDocuments
        .Where(d => d.DocType == "ESTIMATE" && (d.AmountPaid != 0 || d.Balance != 0 || d.Status == "Paid"))
        .ToListAsync();

    foreach (var estimate in estimatesWithPayment)
    {
        estimate.AmountPaid = 0;
        estimate.Balance = 0;
        if (estimate.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase))
        {
            estimate.Status = "Accepted";
        }

        estimate.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
        changed = true;
    }

    if (changed)
    {
        await db.SaveChangesAsync();
    }
}

static async Task NormalizeLedgerMoneyRowsAsync(AppDbContext db)
{
    var changed = false;
    var estimateJobs = await db.CustomerJobs
        .Where(x => !x.IsArchived
            && x.RelatedInvoiceNumber != null
            && x.RelatedInvoiceNumber.StartsWith("EST-")
            && (x.AmountPaid != null || x.InvoiceAmount != null))
        .ToListAsync();

    foreach (var job in estimateJobs)
    {
        job.AmountPaid = null;
        job.InvoiceAmount = null;
        if (!job.JobType.Equals("Estimate", StringComparison.OrdinalIgnoreCase))
        {
            job.JobType = "Estimate";
        }

        if (job.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase))
        {
            job.Status = "Quoted";
        }

        job.Notes = AppendNote(job.Notes, "Normalized during startup: estimate-linked jobs use Quote Amount only; Amount Paid belongs on a paid invoice or Sale.");
        job.UpdatedAtUtc = DateTime.UtcNow;
        changed = true;
    }

    var bills = await db.Bills
        .Where(x => !x.IsArchived && x.Total != null)
        .ToListAsync();
    foreach (var bill in bills)
    {
        var expectedTotal = (bill.Amount ?? 0) + (bill.SalesTax ?? 0);
        if (Math.Abs(expectedTotal - (bill.Total ?? 0)) > 0.02m)
        {
            bill.Notes = AppendNote(bill.Notes, $"Startup corrected Total from {(bill.Total ?? 0):C} to {expectedTotal:C} because amount + sales tax is the canonical bill total.");
            bill.Total = expectedTotal;
            bill.UpdatedAtUtc = DateTime.UtcNow;
            changed = true;
        }
    }

    var expenses = await db.Expenses
        .Where(x => !x.IsArchived && x.Total != null)
        .ToListAsync();
    foreach (var expense in expenses)
    {
        var expectedTotal = (expense.Amount ?? 0) + (expense.SalesTax ?? 0);
        if (Math.Abs(expectedTotal - (expense.Total ?? 0)) > 0.02m)
        {
            expense.Notes = AppendNote(expense.Notes, $"Startup corrected Total from {(expense.Total ?? 0):C} to {expectedTotal:C} because amount + sales tax is the canonical expense total.");
            expense.Total = expectedTotal;
            expense.UpdatedAtUtc = DateTime.UtcNow;
            changed = true;
        }
    }

    var voidInvoices = await db.ReceivableInvoices
        .Where(x => !x.IsArchived && x.Status == "Void")
        .ToListAsync();
    foreach (var invoice in voidInvoices)
    {
        invoice.IsArchived = true;
        invoice.Notes = AppendNote(invoice.Notes, "Archived from active AR during startup because void invoices are retained in Invoice Documents, not active receivables.");
        invoice.UpdatedAtUtc = DateTime.UtcNow;
        changed = true;
    }

    if (changed)
    {
        await db.SaveChangesAsync();
    }
}

static async Task ReconcileUnifiedDocumentsToLedgerAsync(AppDbContext db)
{
    var docs = await db.InvoiceDocuments
        .Include(d => d.LineItems)
        .Where(d => d.DocNumber != null && d.DocNumber != "")
        .ToListAsync();

    foreach (var doc in docs)
    {
        await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc);
    }
}

static async Task RepairDirectPaidSalesMissingArAsync(AppDbContext db)
{
    var changed = false;
    var sales = await db.Sales
        .Where(x => !x.IsArchived
            && x.Platform.ToUpper() == "DIRECT"
            && x.IncludeInDashboard
            && x.InvoiceNumber != null
            && x.CustomerPaid != null
            && x.CustomerPaid > 0)
        .ToListAsync();

    foreach (var sale in sales)
    {
        var invoiceIdentity = ReceivableInvoiceIdentity.IdentityKey(sale.InvoiceNumber)!;
        var exists = await db.ReceivableInvoices.AnyAsync(x =>
            !x.IsArchived
            && x.InvoiceNumber.Trim().ToUpper() == invoiceIdentity);

        if (exists)
        {
            continue;
        }

        var subtotal = MoneyRules.SaleGrossReceipts(sale);
        var tax = MoneyRules.SaleSalesTaxMemo(sale);
        var total = MoneyRules.SaleCustomerPaid(sale) > 0 ? MoneyRules.SaleCustomerPaid(sale) : subtotal + tax;
        db.ReceivableInvoices.Add(new ReceivableInvoice
        {
            InvoiceNumber = sale.InvoiceNumber!,
            InvoiceDate = sale.SaleDate,
            DueDate = sale.SaleDate,
            CustomerName = sale.CustomerName,
            ProjectName = sale.ProductName,
            Status = "Paid",
            Subtotal = subtotal,
            Discount = 0,
            RushFee = 0,
            SalesTax = tax,
            InvoiceTotal = total,
            AmountPaid = total,
            IncludeInCashReports = true,
            SourceProof = sale.SourceProof,
            Notes = "Repaired from a paid Direct Sale because no matching AR row existed for this customer/invoice."
        });
        changed = true;
    }

    if (changed)
    {
        await db.SaveChangesAsync();
    }
}

static async Task RepairInvoiceProofCustomerMismatchesAsync(AppDbContext db)
{
    var changed = false;
    var invoices = await db.ReceivableInvoices
        .Where(x => !x.IsArchived && x.SourceProof != null && x.SourceProof != "")
        .ToListAsync();

    foreach (var invoice in invoices)
    {
        var saleForProof = await db.Sales.FirstOrDefaultAsync(x =>
            !x.IsArchived
            && x.SourceProof == invoice.SourceProof
            && x.CustomerName != invoice.CustomerName);

        if (saleForProof is null)
        {
            continue;
        }

        invoice.SourceProof = $"Unified invoice {invoice.InvoiceNumber}";
        invoice.NeedsReview = true;
        invoice.Notes = AppendNote(invoice.Notes,
            $"Startup repair: old SourceProof matched {saleForProof.CustomerName}, so this AR proof was reset to the unified invoice reference. Reattach the correct PDF if needed.");
        invoice.UpdatedAtUtc = DateTime.UtcNow;
        changed = true;
    }

    if (changed)
    {
        await db.SaveChangesAsync();
    }
}

static async Task ArchiveOrphanedUnifiedLedgerRowsAsync(AppDbContext db)
{
    var docNumbers = await db.InvoiceDocuments
        .Where(d => d.DocNumber != null && d.DocNumber != "")
        .Select(d => d.DocNumber!)
        .ToListAsync();
    var existingDocs = docNumbers.ToHashSet(StringComparer.OrdinalIgnoreCase);
    var changed = false;

    var orphanInvoices = await db.ReceivableInvoices
        .Where(x => !x.IsArchived && x.SourceProof != null && x.SourceProof.StartsWith("Unified invoice "))
        .ToListAsync();
    foreach (var invoice in orphanInvoices.Where(x => !existingDocs.Contains(x.InvoiceNumber)))
    {
        invoice.IsArchived = true;
        invoice.UpdatedAtUtc = DateTime.UtcNow;
        changed = true;
    }

    var orphanSales = await db.Sales
        .Where(x => !x.IsArchived && x.SourceProof != null && x.SourceProof.StartsWith("Unified invoice ") && x.InvoiceNumber != null)
        .ToListAsync();
    foreach (var sale in orphanSales.Where(x => !existingDocs.Contains(x.InvoiceNumber!)))
    {
        sale.IncludeInDashboard = false;
        sale.IsArchived = true;
        sale.UpdatedAtUtc = DateTime.UtcNow;
        changed = true;
    }

    var orphanJobs = await db.CustomerJobs
        .Where(x => !x.IsArchived && x.SourceProof != null && x.SourceProof.StartsWith("Unified estimate ") && x.RelatedInvoiceNumber != null)
        .ToListAsync();
    foreach (var job in orphanJobs.Where(x => !existingDocs.Contains(x.RelatedInvoiceNumber!)))
    {
        job.IsArchived = true;
        job.UpdatedAtUtc = DateTime.UtcNow;
        changed = true;
    }

    if (changed)
    {
        await db.SaveChangesAsync();
    }
}
#pragma warning restore CS8321

static async Task<string> NextInvoiceDocumentNumberAsync(AppDbContext db, string? type)
{
    var prefix = string.Equals(type, "INVOICE", StringComparison.OrdinalIgnoreCase) ? "INV" : "EST";
    var year = DateTime.Now.Year;
    var current = await db.InvoiceDocuments.AsNoTracking()
        .Where(d => d.DocNumber != null && d.DocNumber.StartsWith($"{prefix}-{year}-"))
        .Select(d => d.DocNumber!)
        .ToListAsync();
    if (prefix == "INV")
    {
        current.AddRange(await db.ReceivableInvoices.AsNoTracking()
            .Where(d => d.InvoiceNumber.StartsWith($"{prefix}-{year}-"))
            .Select(d => d.InvoiceNumber)
            .ToListAsync());
    }
    else
    {
        current.AddRange(await db.CustomerJobs.AsNoTracking()
            .Where(d => d.RelatedInvoiceNumber != null && d.RelatedInvoiceNumber.StartsWith($"{prefix}-{year}-"))
            .Select(d => d.RelatedInvoiceNumber!)
            .ToListAsync());
    }

    var next = 1;
    foreach (var value in current)
    {
        var parts = value.Split('-');
        if (parts.Length == 3 && int.TryParse(parts[2], out var seq))
        {
            next = Math.Max(next, seq + 1);
        }
    }

    return $"{prefix}-{year}-{next:D4}";
}

static void ApplyInvoiceDocumentRequest(InvoiceDocument doc, SaveInvoiceDocumentRequest request)
{
    if (!string.IsNullOrWhiteSpace(request.DocNumber))
    {
        doc.DocNumber = InvoiceDocumentIdentityEquals(doc.DocNumber, request.DocNumber)
            ? doc.DocNumber?.Trim()
            : CanonicalizeNewInvoiceDocumentNumber(request.DocNumber);
    }
    doc.DocType = (request.DocType ?? "ESTIMATE").ToUpperInvariant();
    doc.Status = request.Status ?? "Draft";
    doc.CustomerName = request.CustomerName;
    doc.CustomerPhone = request.CustomerPhone;
    doc.CustomerAddress = request.CustomerAddress;
    doc.CustomerEmail = request.CustomerEmail;
    doc.PreparedFor = request.PreparedFor;
    doc.ProjectName = request.ProjectName;
    doc.Material = request.Material;
    doc.Color = request.Color;
    doc.Infill = request.Infill;
    doc.ProjectDescription = request.ProjectDescription;
    doc.ProjectNotes = request.ProjectNotes;
    doc.PageSize = request.PageSize;
    doc.DocDate = request.DocDate;
    doc.DueDate = request.DueDate;
    doc.PaymentMethod = NormalizePaymentMethod(request.PaymentMethod);
    doc.PricingGuide = request.PricingGuide;
    doc.TermsNotes = request.TermsNotes;
    doc.StandardTurnaround = request.StandardTurnaround;
    doc.RushTurnaround = request.RushTurnaround;
    doc.CalcGrams = request.CalcGrams;
    doc.CalcHours = request.CalcHours;
    doc.CalcDesignHours = request.CalcDesignHours;
    doc.CalcSetupFee = request.CalcSetupFee;
    doc.CalcPostFee = request.CalcPostFee;
    doc.CalcGramRate = request.CalcGramRate == 0 ? 0.05m : request.CalcGramRate;
    doc.CalcHourRate = request.CalcHourRate == 0 ? 3m : request.CalcHourRate;
    doc.CalcDesignRate = request.CalcDesignRate == 0 ? 25m : request.CalcDesignRate;
    doc.CalcMinimum = request.CalcMinimum == 0 ? 15m : request.CalcMinimum;
    doc.CalcDifficulty = request.CalcDifficulty == 0 ? 1m : request.CalcDifficulty;
    doc.CalcRush = request.CalcRush;
    doc.CalcDiscount = request.CalcDiscount;
    doc.CalcTaxRate = request.CalcTaxRate;
    doc.Json = request.Json ?? "{}";

    doc.LineItems.Clear();
    if (request.LineItems is { Count: > 0 })
    {
        doc.LineItems.AddRange(request.LineItems.Select((line, index) => new InvoiceLineItem
        {
            SortOrder = line.SortOrder > 0 ? line.SortOrder : index + 1,
            Description = line.Description,
            Details = line.Details,
            Quantity = Math.Max(0, line.Quantity),
            Rate = Math.Max(0, line.Rate),
            Amount = Math.Max(0, line.Quantity) * Math.Max(0, line.Rate)
        }));
    }

    NormalizeInvoiceDocumentMoney(doc, request);
}

static void NormalizeInvoiceDocumentMoney(InvoiceDocument doc, SaveInvoiceDocumentRequest request)
{
    var lineSubtotal = doc.LineItems.Count > 0
        ? doc.LineItems.Sum(x => x.Quantity * x.Rate)
        : request.Subtotal;
    var discount = Math.Max(0, request.DiscountAmount);
    var rush = Math.Max(0, request.RushAmount);
    var taxable = MoneyRules.InvoiceTaxableSalesBase(lineSubtotal, discount, rush);
    var taxRate = Math.Clamp(request.CalcTaxRate, 0, 30);
    var tax = taxRate > 0 ? taxable * taxRate / 100m : Math.Max(0, request.TaxAmount);
    var total = taxable + tax;
    var paid = Math.Max(0, request.AmountPaid);

    if (doc.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase))
    {
        paid = 0;
        if (doc.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase))
        {
            doc.Status = "Accepted";
        }
    }
    else if (doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase)
        || doc.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
        || doc.Status.Equals("Sent", StringComparison.OrdinalIgnoreCase))
    {
        paid = 0;
    }
    else if (doc.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase) && total > 0)
    {
        paid = total;
    }

    doc.Subtotal = lineSubtotal;
    doc.DiscountAmount = discount;
    doc.RushAmount = rush;
    doc.TaxAmount = tax;
    doc.Total = total;
    doc.AmountPaid = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? Math.Min(paid, total) : 0;
    doc.Balance = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) && !doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase)
        ? Math.Max(0, total - doc.AmountPaid)
        : 0;
}

static void NormalizeExistingInvoiceDocumentMoney(InvoiceDocument doc)
{
    var subtotal = doc.LineItems.Count > 0
        ? doc.LineItems.Sum(x => Math.Max(0, x.Quantity) * Math.Max(0, x.Rate))
        : Math.Max(0, doc.Subtotal);
    var discount = Math.Max(0, doc.DiscountAmount);
    var rush = Math.Max(0, doc.RushAmount);
    var taxRate = Math.Clamp(doc.CalcTaxRate, 0, 30);
    var taxable = MoneyRules.InvoiceTaxableSalesBase(subtotal, discount, rush);
    var tax = taxRate > 0 ? taxable * taxRate / 100m : Math.Max(0, doc.TaxAmount);
    var total = taxable + tax;
    var paid = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase)
        ? Math.Min(Math.Max(0, doc.AmountPaid), total)
        : 0;

    if (doc.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase) && doc.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase))
    {
        doc.Status = "Accepted";
    }
    else if (doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase)
        && (doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase)
            || doc.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
            || doc.Status.Equals("Sent", StringComparison.OrdinalIgnoreCase)))
    {
        paid = 0;
    }
    else if (doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) && doc.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase) && total > 0)
    {
        paid = total;
    }

    doc.Subtotal = subtotal;
    doc.DiscountAmount = discount;
    doc.RushAmount = rush;
    doc.TaxAmount = tax;
    doc.Total = total;
    doc.AmountPaid = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? paid : 0;
    doc.Balance = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) && !doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase)
        ? Math.Max(0, total - paid)
        : 0;
}

static async Task SyncUnifiedInvoiceDocumentToLedgerAsync(
    AppDbContext db,
    InvoiceDocument doc,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(doc.DocNumber))
    {
        return;
    }

    if (doc.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase))
    {
        var unifiedSource = $"Unified estimate {doc.DocNumber}";
        var unifiedSourceIdentity = unifiedSource.ToUpperInvariant();
        var hasConvertedInvoice = await db.InvoiceDocuments.AsNoTracking()
            .AnyAsync(x => x.SourceEstimateId == doc.Id && !x.IsArchived, cancellationToken);
        var job = await db.CustomerJobs.FirstOrDefaultAsync(
            x => x.SourceProof != null && x.SourceProof.Trim().ToUpper() == unifiedSourceIdentity,
            cancellationToken);
        if (job is null)
        {
            if (doc.IsArchived && !hasConvertedInvoice)
            {
                return;
            }

            job = new CustomerJob
            {
                Platform = "Direct",
                JobType = "Estimate",
                RelatedInvoiceNumber = doc.DocNumber,
                SourceProof = unifiedSource,
                Notes = "Created from the unified estimate/invoice workspace. Estimate only; not income and not AR."
            };
            db.CustomerJobs.Add(job);
        }

        job.Platform = "Direct";
        job.JobType = "Estimate";
        job.RelatedInvoiceNumber = doc.DocNumber;
        job.SourceProof = unifiedSource;
        job.JobDate = ParseDate(doc.DocDate) ?? job.JobDate;
        job.DueDate = ParseDate(doc.DueDate) ?? job.DueDate;
        job.CustomerName = doc.CustomerName ?? job.CustomerName;
        job.JobName = doc.ProjectName ?? job.JobName;
        job.Material = doc.Material ?? job.Material;
        job.Color = doc.Color ?? job.Color;
        job.Description = doc.ProjectDescription ?? job.Description;
        job.Status = doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase)
            ? "Cancelled"
            : hasConvertedInvoice
                ? "Invoiced"
                : "Quoted";
        job.QuoteAmount = doc.Total;
        job.AmountPaid = null;
        job.InvoiceAmount = null;
        job.NeedsReview = false;
        job.IsArchived = doc.IsArchived && !hasConvertedInvoice;
        job.UpdatedAtUtc = DateTime.UtcNow;
    }
    else if (doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase))
    {
        var unifiedSource = $"Unified invoice {doc.DocNumber}";
        var unifiedSourceIdentity = unifiedSource.ToUpperInvariant();
        var documentIdentity = InvoiceDocumentIdentityKey(doc.DocNumber)!;
        var invoice = await db.ReceivableInvoices.FirstOrDefaultAsync(
            x => x.SourceProof != null && x.SourceProof.Trim().ToUpper() == unifiedSourceIdentity,
            cancellationToken);
        var activeIdentityRows = await db.ReceivableInvoices.AsNoTracking()
            .Where(candidate => !candidate.IsArchived
                && candidate.InvoiceNumber.Trim().ToUpper() == documentIdentity)
            .Select(candidate => candidate.Id)
            .ToListAsync(cancellationToken);
        var activeCollisionId = activeIdentityRows.FirstOrDefault(candidateId => candidateId != invoice?.Id);
        if (!doc.IsArchived && activeCollisionId > 0)
        {
            throw new InvalidOperationException(
                $"Invoice number {doc.DocNumber} already belongs to active receivable #{activeCollisionId}. Archive or reconcile that receivable before saving this Invoice Record.");
        }

        if (invoice is null)
        {
            if (doc.IsArchived)
            {
                await SyncInvoicePaymentToSaleAsync(db, doc, cancellationToken);
                await db.SaveChangesAsync(cancellationToken);
                return;
            }

            invoice = new ReceivableInvoice
            {
                InvoiceNumber = doc.DocNumber,
                SourceProof = unifiedSource,
                Notes = "Created from the unified estimate/invoice workspace."
            };
            db.ReceivableInvoices.Add(invoice);
        }

        invoice.InvoiceDate = ParseDate(doc.DocDate) ?? invoice.InvoiceDate;
        invoice.DueDate = ParseDate(doc.DueDate) ?? invoice.DueDate;
        invoice.CustomerName = doc.CustomerName ?? invoice.CustomerName;
        invoice.ProjectName = doc.ProjectName ?? invoice.ProjectName;
        invoice.Status = NormalizeInvoiceStatus(doc.Status, doc.Total, doc.AmountPaid);
        invoice.IsArchived = doc.IsArchived || invoice.Status.Equals("Void", StringComparison.OrdinalIgnoreCase);
        invoice.Subtotal = doc.Subtotal;
        invoice.Discount = doc.DiscountAmount;
        invoice.RushFee = doc.RushAmount;
        invoice.TaxRatePercent = doc.CalcTaxRate;
        invoice.SalesTax = doc.TaxAmount;
        invoice.InvoiceTotal = doc.Total;
        invoice.AmountPaid = doc.AmountPaid;
        invoice.PaymentMethod = NormalizePaymentMethod(doc.PaymentMethod);
        invoice.IncludeInCashReports = !invoice.IsArchived && doc.AmountPaid > 0;
        invoice.NeedsReview = doc.AmountPaid < doc.Total && !invoice.Status.Equals("Void", StringComparison.OrdinalIgnoreCase);

        await SyncInvoicePaymentToSaleAsync(db, doc, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return;
    }

    await db.SaveChangesAsync(cancellationToken);
}

static async Task SyncInvoicePaymentToSaleAsync(
    AppDbContext db,
    InvoiceDocument doc,
    CancellationToken cancellationToken = default)
{
    var unifiedSource = $"Unified invoice {doc.DocNumber}";
    var documentIdentity = InvoiceDocumentIdentityKey(doc.DocNumber)!;
    var unifiedSourceIdentity = unifiedSource.ToUpperInvariant();
    var sale = await db.Sales.FirstOrDefaultAsync(x =>
        x.InvoiceNumber != null
        && x.InvoiceNumber.Trim().ToUpper() == documentIdentity
        && x.Platform.ToUpper() == "DIRECT"
        && x.SourceProof != null
        && x.SourceProof.Trim().ToUpper() == unifiedSourceIdentity,
        cancellationToken);

    if (doc.IsArchived || doc.AmountPaid <= 0 || doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase))
    {
        if (sale is not null && string.Equals(sale.SourceProof, unifiedSource, StringComparison.OrdinalIgnoreCase))
        {
            sale.IncludeInDashboard = false;
            sale.IsArchived = true;
            sale.Status = "Draft";
            sale.NeedsReview = false;
            sale.Notes = "Automatically hidden because the unified invoice is archived, unpaid, or void.";
            sale.UpdatedAtUtc = DateTime.UtcNow;
        }

        return;
    }

    if (sale is null)
    {
        sale = new Sale
        {
            Platform = "Direct",
            InvoiceNumber = doc.DocNumber,
            SourceProof = unifiedSource,
            IncludeInDashboard = true,
            Notes = "Created automatically from a paid unified invoice."
        };
        db.Sales.Add(sale);
    }

    var allocated = MoneyRules.AllocateInvoicePayment(doc.Subtotal, doc.DiscountAmount, doc.RushAmount, doc.TaxAmount, doc.Total, doc.AmountPaid);
    sale.SaleDate = ParseDate(doc.DocDate) ?? sale.SaleDate ?? DateTime.Today;
    sale.CustomerName = doc.CustomerName ?? sale.CustomerName;
    sale.ProductName = doc.ProjectName ?? sale.ProductName;
    sale.PaymentMethod = NormalizePaymentMethod(doc.PaymentMethod);
    sale.Color = doc.Color ?? sale.Color;
    sale.Quantity = 1;
    sale.ItemSales = allocated.SalesBase;
    sale.ShippingCharged = 0;
    sale.SalesTaxCollected = allocated.TaxMemo;
    sale.CustomerPaid = doc.AmountPaid;
    sale.Status = doc.AmountPaid >= doc.Total ? "Paid" : "Partial";
    sale.IncludeInDashboard = true;
    sale.IsArchived = false;
    sale.NeedsReview = false;
    sale.UpdatedAtUtc = DateTime.UtcNow;
}

static async Task SyncManualReceivableInvoiceToSaleAsync(
    AppDbContext db,
    ReceivableInvoice invoice,
    ReceivableInvoiceSyncIdentity? previousIdentity = null)
{
    if (string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
    {
        return;
    }

    var sourceProof = ReceivableInvoiceSyncIdentity.EffectiveSourceProof(invoice.InvoiceNumber, invoice.SourceProof);
    Sale? sale = null;
    if (invoice.Id > 0)
    {
        sale = await db.Sales.FirstOrDefaultAsync(x => x.SourceReceivableInvoiceId == invoice.Id);
    }

    if (sale is null)
    {
        var previousNumber = previousIdentity?.InvoiceNumber;
        var previousSourceProof = previousIdentity?.SourceProofValue;
        var candidates = await db.Sales
            .Where(x => x.SourceReceivableInvoiceId == null
                && x.Platform.ToUpper() == "DIRECT")
            .ToListAsync();
        sale = candidates.FirstOrDefault(candidate => IsGeneratedManualReceivableSale(
            candidate,
            invoice.InvoiceNumber,
            sourceProof,
            previousNumber,
            previousSourceProof));
    }

    var paid = invoice.AmountPaid ?? 0;
    var total = invoice.InvoiceTotal ?? 0;

    if (sale is not null)
    {
        sale.SourceReceivableInvoice = invoice;
        sale.SourceReceivableInvoiceId = invoice.Id > 0 ? invoice.Id : null;
        sale.InvoiceNumber = invoice.InvoiceNumber;
        sale.SourceProof = sourceProof;
    }

    if (invoice.IsArchived || paid <= 0 || invoice.Status.Equals("Void", StringComparison.OrdinalIgnoreCase))
    {
        if (sale is not null)
        {
            sale.IncludeInDashboard = false;
            sale.IsArchived = true;
            sale.Status = "Draft";
            sale.NeedsReview = false;
            sale.Notes = invoice.IsArchived
                ? "Automatically hidden because the source receivable invoice is archived."
                : "Automatically hidden because the receivable invoice is unpaid or void.";
            sale.UpdatedAtUtc = DateTime.UtcNow;
        }

        return;
    }

    if (sale is null)
    {
        sale = new Sale
        {
            Platform = "Direct",
            InvoiceNumber = invoice.InvoiceNumber,
            SourceProof = sourceProof,
            SourceReceivableInvoice = invoice,
            IncludeInDashboard = true,
            Notes = "Created automatically from a paid receivable invoice."
        };
        db.Sales.Add(sale);
    }

    sale.SourceReceivableInvoice = invoice;
    sale.SourceReceivableInvoiceId = invoice.Id > 0 ? invoice.Id : null;
    sale.InvoiceNumber = invoice.InvoiceNumber;
    sale.SourceProof = sourceProof;
    var allocated = MoneyRules.AllocateInvoicePayment(
        invoice.Subtotal ?? 0,
        invoice.Discount ?? 0,
        invoice.RushFee ?? 0,
        invoice.SalesTax ?? 0,
        total,
        paid);
    sale.SaleDate = invoice.InvoiceDate ?? sale.SaleDate ?? DateTime.Today;
    sale.CustomerName = invoice.CustomerName;
    sale.ProductName = invoice.ProjectName ?? "Receivable invoice";
    sale.PaymentMethod = NormalizePaymentMethod(invoice.PaymentMethod);
    sale.Quantity = 1;
    sale.ItemSales = allocated.SalesBase;
    sale.ShippingCharged = 0;
    sale.SalesTaxCollected = allocated.TaxMemo;
    sale.CustomerPaid = paid;
    sale.Status = paid >= total ? "Paid" : "Partial";
    sale.IncludeInDashboard = true;
    sale.IsArchived = false;
    sale.NeedsReview = false;
    sale.Notes = "Created automatically from a paid receivable invoice.";
    sale.UpdatedAtUtc = DateTime.UtcNow;
}

static bool IsGeneratedManualReceivableSale(
    Sale sale,
    string currentNumber,
    string currentSourceProof,
    string? previousNumber,
    string? previousSourceProof)
{
    if (sale.Notes?.Contains("receivable invoice", StringComparison.OrdinalIgnoreCase) == true
        && (sale.Notes.Contains("automatically", StringComparison.OrdinalIgnoreCase)
            || sale.Notes.Contains("Automatically", StringComparison.OrdinalIgnoreCase)))
    {
        return true;
    }

    return string.Equals(sale.InvoiceNumber, currentNumber, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sale.SourceProof, currentSourceProof, StringComparison.OrdinalIgnoreCase)
        || !string.IsNullOrWhiteSpace(previousNumber)
            && !string.IsNullOrWhiteSpace(previousSourceProof)
            && string.Equals(sale.InvoiceNumber, previousNumber, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sale.SourceProof, previousSourceProof, StringComparison.OrdinalIgnoreCase);
}

static object ToInvoiceDocumentDto(InvoiceDocument doc) => new
{
    doc.Id,
    doc.SourceEstimateId,
    doc.DocNumber,
    doc.DocType,
    doc.Status,
    doc.CustomerName,
    doc.CustomerPhone,
    doc.CustomerAddress,
    doc.CustomerEmail,
    doc.PreparedFor,
    doc.ProjectName,
    doc.Material,
    doc.Color,
    doc.Infill,
    doc.ProjectDescription,
    doc.ProjectNotes,
    doc.PageSize,
    doc.DocDate,
    doc.DueDate,
    doc.Subtotal,
    doc.DiscountAmount,
    doc.RushAmount,
    doc.TaxAmount,
    doc.Total,
    doc.AmountPaid,
    doc.Balance,
    doc.PaymentMethod,
    doc.PricingGuide,
    doc.TermsNotes,
    doc.StandardTurnaround,
    doc.RushTurnaround,
    doc.CalcGrams,
    doc.CalcHours,
    doc.CalcDesignHours,
    doc.CalcSetupFee,
    doc.CalcPostFee,
    doc.CalcGramRate,
    doc.CalcHourRate,
    doc.CalcDesignRate,
    doc.CalcMinimum,
    doc.CalcDifficulty,
    doc.CalcRush,
    doc.CalcDiscount,
    doc.CalcTaxRate,
    doc.Json,
    doc.IsArchived,
    doc.ArchivedAt,
    doc.ArchiveReason,
    doc.CreatedAt,
    doc.UpdatedAt,
    lineItems = doc.LineItems.OrderBy(li => li.SortOrder).Select(li => new
    {
        li.Id,
        li.SortOrder,
        li.Description,
        li.Details,
        li.Quantity,
        li.Rate,
        li.Amount
    })
};

public sealed record InvoiceDocumentImportRequest(string? TargetType);

public sealed record InvoiceDocumentIntakeResult(
    string Action,
    InvoiceDocument? InvoiceDocument,
    AuditDocument AuditDocument,
    string Message,
    List<string> Warnings,
    string? SourceDocumentNumber);

public sealed record ImportInvoiceRequest(string? BaseUrl);

public sealed record InvoiceBuilderConfigRequest(
    int Id,
    string? BusinessName,
    string? BusinessLocation,
    string? BusinessEmail,
    string? BusinessPhone,
    string? BusinessWebsite,
    string? BusinessEtsy,
    string? BusinessInstagram,
    string? BusinessFacebook,
    string? BrandColor,
    decimal CalcGramRate,
    decimal CalcHourRate,
    decimal CalcDesignRate,
    decimal CalcSetupFee,
    decimal CalcPostFee,
    decimal CalcMinimum);

public sealed record SaveInvoiceDocumentRequest(
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
    string? PaymentMethod,
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
    List<InvoiceLineItemRequest>? LineItems,
    string? Json);

public sealed record InvoiceLineItemRequest(
    int? Id,
    int SortOrder,
    string? Description,
    string? Details,
    decimal Quantity,
    decimal Rate,
    decimal Amount);

public sealed class TimelineGroupDto
{
    public string Key { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public string Status { get; set; } = "Open";
    public string WorkflowType { get; set; } = string.Empty;
    public int FlowScore { get; set; }
    public DateTime LastActivity { get; set; }
    public decimal EstimatedValue { get; set; }
    public decimal Invoiced { get; set; }
    public decimal Paid { get; set; }
    public decimal GrossReceipts { get; set; }
    public decimal TaxMemo { get; set; }
    public decimal SellingCosts { get; set; }
    public decimal EstimatedCogs { get; set; }
    public decimal EstimatedProfit { get; set; }
    public List<string> Missing { get; set; } = [];
    public List<TimelineEventDto> Events { get; set; } = [];
    [JsonIgnore] public HashSet<string> CountedEstimateRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public HashSet<string> CountedInvoiceRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
    [JsonIgnore] public HashSet<string> CountedPaidRefs { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record TimelineEventDto(
    DateTime Date,
    string Kind,
    string Status,
    string Title,
    string Detail,
    decimal? Amount,
    string RoutePage,
    int RecordId,
    string? Reference,
    string? Proof,
    bool NeedsReview,
    string? TimeLabel = null,
    string? ExactTimeLabel = null);

public sealed record InboxSuggestionLane(
    string Lane,
    string Title,
    string Detail,
    string Route,
    string? Config,
    string? Kind,
    Dictionary<string, object?> Prefill);

public sealed record InvoiceDocumentAuditState(
    string DocType,
    string? DocNumber,
    string Status,
    string? CustomerName,
    string? ProjectName,
    decimal Total,
    decimal AmountPaid,
    decimal Balance,
    string LineSignature);

public sealed class StaleInvoiceDocumentException(string currentUpdatedAt)
    : InvalidOperationException("The Invoice Record changed after it was opened.")
{
    public string CurrentUpdatedAt { get; } = currentUpdatedAt;
}

public sealed record ReceivableInvoiceSyncIdentity(string InvoiceNumber, string SourceProofValue)
{
    public static ReceivableInvoiceSyncIdentity? Capture(AuditableEntity entity) => entity is ReceivableInvoice invoice
        ? new(invoice.InvoiceNumber, EffectiveSourceProof(invoice.InvoiceNumber, invoice.SourceProof))
        : null;

    public static string EffectiveSourceProof(string invoiceNumber, string? sourceProof) =>
        string.IsNullOrWhiteSpace(sourceProof) ? $"Receivable invoice {invoiceNumber}" : sourceProof;
}

public sealed record TaxAuditIssue(string Severity, string Title, string Record, string Detail);

public sealed record TaxSaleExportRow(
    int Id,
    DateTime? SaleDate,
    string Platform,
    string PaymentMethod,
    string PaymentGroup,
    string SalesTaxHandling,
    string? OrderNumber,
    string? InvoiceNumber,
    string CustomerName,
    string ProductName,
    decimal? ItemSales,
    decimal? ShippingCharged,
    decimal? SalesTaxCollected,
    decimal? CustomerPaid,
    decimal? PlatformFees,
    decimal? ShippingLabelCost,
    decimal? Refunds,
    decimal? EstimatedCogs,
    string Status,
    string? SourceProof,
    bool NeedsReview,
    string? Notes);

public static class InvoiceDocumentMutationLocks
{
    public static SemaphoreSlim Gate { get; } = new(1, 1);
}

public static class InvoiceDocumentConversionLocks
{
    private static readonly ConcurrentDictionary<int, SemaphoreSlim> Gates = new();

    public static SemaphoreSlim For(int estimateId) => Gates.GetOrAdd(estimateId, static _ => new SemaphoreSlim(1, 1));
}

public static class UploadMutationLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public static SemaphoreSlim For(string uploadFingerprint) =>
        Gates.GetOrAdd(uploadFingerprint, static _ => new SemaphoreSlim(1, 1));
}

public static class MarketplaceMutationLocks
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.Ordinal);

    public static string? IdentityKey(string? platform, string? orderNumber)
    {
        var platformKey = NormalizeIdentityPart(platform);
        var orderKey = NormalizeIdentityPart(orderNumber);
        return string.IsNullOrWhiteSpace(platformKey) || string.IsNullOrWhiteSpace(orderKey)
            ? null
            : $"{platformKey}|{orderKey}";
    }

    public static SemaphoreSlim For(string marketplaceIdentityKey) =>
        Gates.GetOrAdd(marketplaceIdentityKey, static _ => new SemaphoreSlim(1, 1));

    private static string NormalizeIdentityPart(string? value) =>
        Regex.Replace(value ?? string.Empty, @"[^a-z0-9]+", string.Empty, RegexOptions.IgnoreCase).ToLowerInvariant();
}

public static class ReceivableInvoiceIdentity
{
    public static string? IdentityKey(string? invoiceNumber) =>
        string.IsNullOrWhiteSpace(invoiceNumber)
            ? null
            : invoiceNumber.Trim().ToUpperInvariant();
}

public partial class Program;
