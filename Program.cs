using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using EPATA.BusinessLedger.Data;
using EPATA.BusinessLedger.Models;
using EPATA.BusinessLedger.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

var appUrl = builder.Configuration["App:Url"] ?? "http://127.0.0.1:5062";
builder.WebHost.UseUrls(appUrl);

builder.Services.AddDbContext<AppDbContext>(options =>
{
    var connectionString = ResolveConnectionString(builder.Configuration, builder.Environment.ContentRootPath);
    options.UseSqlite(connectionString);
});

builder.Services.AddScoped<DashboardService>();
builder.Services.AddHttpClient<InvoiceAppImportService>();
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

app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
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

MapCrud<Party>(app, "parties");
MapCrud<Sale>(app, "sales");
MapCrud<CustomerJob>(app, "customer-jobs");
MapCrud<ReceivableInvoice>(app, "receivable-invoices");
MapCrud<Bill>(app, "bills");
MapCrud<Expense>(app, "expenses");
MapCrud<Product>(app, "products");
MapCrud<Asset>(app, "assets");
MapCrud<MakerWorldReward>(app, "makerworld-rewards");
MapCrud<AuditDocument>(app, "audit-documents");
MapCrud<BusinessAccount>(app, "business-accounts");
MapCrud<ActionItem>(app, "action-items");
MapCrud<AppSetting>(app, "settings");

app.MapGet("/api/dashboard", async (DashboardService dashboard) => Results.Ok(await dashboard.BuildAsync()));

app.MapGet("/api/tax-audit", async (AppDbContext db) => Results.Ok(await BuildTaxAuditAsync(db)));

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

app.MapPost("/api/import/invoice-app", async (InvoiceAppImportService importer, ImportInvoiceRequest request) =>
{
    var result = await importer.TryReadInvoiceAppAsync(request.BaseUrl);
    return Results.Ok(result);
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
        businessEmail = settings.GetValueOrDefault("BusinessEmail", "epata.llc.co@gmail.com"),
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
    await UpsertSettingAsync(db, "BusinessName", request.BusinessName);
    await UpsertSettingAsync(db, "BusinessLocation", request.BusinessLocation);
    await UpsertSettingAsync(db, "BusinessEmail", request.BusinessEmail);
    await UpsertSettingAsync(db, "BusinessPhone", request.BusinessPhone);
    await UpsertSettingAsync(db, "BusinessWebsite", request.BusinessWebsite);
    await UpsertSettingAsync(db, "BusinessEtsy", request.BusinessEtsy);
    await UpsertSettingAsync(db, "BusinessInstagram", request.BusinessInstagram);
    await UpsertSettingAsync(db, "BusinessFacebook", request.BusinessFacebook);
    await UpsertSettingAsync(db, "BrandColor", request.BrandColor);
    await UpsertSettingAsync(db, "CalcGramRate", request.CalcGramRate.ToString());
    await UpsertSettingAsync(db, "CalcHourRate", request.CalcHourRate.ToString());
    await UpsertSettingAsync(db, "CalcDesignRate", request.CalcDesignRate.ToString());
    await UpsertSettingAsync(db, "CalcSetupFee", request.CalcSetupFee.ToString());
    await UpsertSettingAsync(db, "CalcPostFee", request.CalcPostFee.ToString());
    await UpsertSettingAsync(db, "CalcMinimum", request.CalcMinimum.ToString());
    await db.SaveChangesAsync();
    return Results.Ok(request with { Id = 1 });
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

app.MapPost("/api/documents", async (AppDbContext db, SaveInvoiceDocumentRequest request) =>
{
    var doc = await CreateInvoiceDocumentAsync(db, request);
    return Results.Ok(ToInvoiceDocumentDto(doc));
});

app.MapPut("/api/documents/{id:int}", async (AppDbContext db, int id, SaveInvoiceDocumentRequest request) =>
{
    var doc = await UpdateInvoiceDocumentAsync(db, id, request);
    return doc is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(doc));
});

app.MapDelete("/api/documents/{id:int}", async (AppDbContext db, int id) =>
{
    return await DeleteInvoiceDocumentAndSyncAsync(db, id);
});

app.MapPost("/api/documents/{id:int}/restore", async (AppDbContext db, int id) =>
{
    return await RestoreInvoiceDocumentAndSyncAsync(db, id);
});

app.MapPost("/api/documents/{id:int}/duplicate", async (AppDbContext db, int id) =>
{
    var copy = await DuplicateInvoiceDocumentAsync(db, id, null);
    return copy is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(copy));
});

app.MapPost("/api/documents/{id:int}/convert-to-invoice", async (AppDbContext db, int id) =>
{
    var invoice = await DuplicateInvoiceDocumentAsync(db, id, "INVOICE");
    return invoice is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(invoice));
});

app.MapGet("/api/database/backup", async (AppDbContext db, IWebHostEnvironment env, IConfiguration configuration) =>
{
    var backup = await CreateDatabaseBackupAsync(db, env, configuration);
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

app.MapPost("/api/invoice-documents", async (AppDbContext db, SaveInvoiceDocumentRequest request) =>
{
    var doc = await CreateInvoiceDocumentAsync(db, request);
    return Results.Ok(ToInvoiceDocumentDto(doc));
});

app.MapPut("/api/invoice-documents/{id:int}", async (AppDbContext db, int id, SaveInvoiceDocumentRequest request) =>
{
    var doc = await UpdateInvoiceDocumentAsync(db, id, request);
    return doc is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(doc));
});

app.MapDelete("/api/invoice-documents/{id:int}", async (AppDbContext db, int id) =>
{
    return await DeleteInvoiceDocumentAndSyncAsync(db, id);
});

app.MapPost("/api/invoice-documents/{id:int}/restore", async (AppDbContext db, int id) =>
{
    return await RestoreInvoiceDocumentAndSyncAsync(db, id);
});

app.MapPost("/api/invoice-documents/{id:int}/duplicate", async (AppDbContext db, int id) =>
{
    var copy = await DuplicateInvoiceDocumentAsync(db, id, null);
    return copy is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(copy));
});

app.MapPost("/api/invoice-documents/{id:int}/convert-to-invoice", async (AppDbContext db, int id) =>
{
    var invoice = await DuplicateInvoiceDocumentAsync(db, id, "INVOICE");
    return invoice is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(invoice));
});

app.MapPost("/api/invoice-documents/import-from-legacy", async (InvoiceAppImportService importer, AppDbContext db) =>
{
    List<InvoiceAppFullDocument> docs;
    try
    {
        docs = await importer.ReadFullDocumentsAsync();
    }
    catch (HttpRequestException ex)
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

    var created = 0;
    var updated = 0;

    foreach (var old in docs.Where(d => !string.IsNullOrWhiteSpace(d.DocNumber)))
    {
        var doc = await db.InvoiceDocuments
            .Include(d => d.LineItems)
            .FirstOrDefaultAsync(d => d.DocNumber == old.DocNumber);
        if (doc is null)
        {
            doc = new InvoiceDocument { DocNumber = old.DocNumber, CreatedAt = old.CreatedAt ?? DateTimeOffset.UtcNow.ToString("O") };
            db.InvoiceDocuments.Add(doc);
            created++;
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
    }

    await db.SaveChangesAsync();
    foreach (var doc in await db.InvoiceDocuments.Include(d => d.LineItems).ToListAsync())
    {
        await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc);
    }

    return Results.Ok(new { success = true, imported = docs.Count, created, updated });
});

app.MapPost("/api/documents/upload", async (HttpRequest request, AppDbContext db, IWebHostEnvironment env) =>
{
    if (!request.HasFormContentType)
    {
        return Results.BadRequest(new { message = "Upload must be sent as multipart/form-data." });
    }

    var form = await request.ReadFormAsync();
    if (form.Files.Count == 0)
    {
        return Results.BadRequest(new { message = "Choose at least one file to upload." });
    }

    var uploadDir = Path.Combine(env.ContentRootPath, "UploadedDocs");
    Directory.CreateDirectory(uploadDir);

    var created = new List<AuditDocument>();
    foreach (var file in form.Files)
    {
        if (file.Length == 0)
        {
            continue;
        }

        var safeName = MakeSafeFileName(file.FileName);
        var storedName = $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}-{safeName}";
        var storedPath = Path.Combine(uploadDir, storedName);
        await using (var stream = System.IO.File.Create(storedPath))
        {
            await file.CopyToAsync(stream);
        }

        var preview = await TryExtractDocumentPreviewAsync(storedPath, file.ContentType);
        var doc = new AuditDocument
        {
            DocumentDate = DateTime.Today,
            DocumentType = GuessDocumentType(file.FileName),
            FileName = file.FileName,
            FilePathOrUrl = storedPath,
            RelatedRecordType = form["relatedType"].FirstOrDefault(),
            RelatedRecordNumber = form["relatedNumber"].FirstOrDefault(),
            NeedsReview = true,
            Notes = string.IsNullOrWhiteSpace(preview)
                ? "Uploaded document. Review and connect it to the matching sale, invoice, bill, expense, or job."
                : $"Uploaded document. Extracted preview:\n{preview}"
        };

        db.AuditDocuments.Add(doc);
        created.Add(doc);
    }

    await db.SaveChangesAsync();
    return Results.Ok(new
    {
        count = created.Count,
        documents = created,
        suggestions = created.Select(BuildInboxSuggestion).ToList()
    });
});

app.MapGet("/api/export/{entity}", async (string entity, AppDbContext db, bool includeArchived = false) =>
{
    var fileName = $"epata-{entity}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
    return entity.ToLowerInvariant() switch
    {
        "parties" or "customers" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Parties.AsNoTracking(), includeArchived).ToListAsync())),
        "sales" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Sales.AsNoTracking(), includeArchived).ToListAsync())),
        "customer-jobs" or "jobs" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.CustomerJobs.AsNoTracking(), includeArchived).ToListAsync())),
        "receivable-invoices" or "invoices" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.ReceivableInvoices.AsNoTracking(), includeArchived).ToListAsync())),
        "bills" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Bills.AsNoTracking(), includeArchived).ToListAsync())),
        "expenses" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Expenses.AsNoTracking(), includeArchived).ToListAsync())),
        "products" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Products.AsNoTracking(), includeArchived).ToListAsync())),
        "assets" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.Assets.AsNoTracking(), includeArchived).ToListAsync())),
        "makerworld-rewards" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.MakerWorldRewards.AsNoTracking(), includeArchived).ToListAsync())),
        "audit-documents" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.AuditDocuments.AsNoTracking(), includeArchived).ToListAsync())),
        "business-accounts" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.BusinessAccounts.AsNoTracking(), includeArchived).ToListAsync())),
        "action-items" => Csv(fileName, CsvExportService.ToCsv(await ActiveRows(db.ActionItems.AsNoTracking(), includeArchived).ToListAsync())),
        _ => Results.NotFound(new { message = "Unknown export entity." })
    };
});

app.MapPost("/api/system/backup", async (AppDbContext db, IConfiguration configuration, IWebHostEnvironment env) =>
{
    var backup = await CreateDatabaseBackupAsync(db, env, configuration);
    return Results.File(backup.Bytes, "application/octet-stream", backup.FileName);
});

app.MapGet("/api/app-info", (IWebHostEnvironment env, IConfiguration config) =>
    Results.Ok(new {
        environment = env.EnvironmentName,
        isTest = env.EnvironmentName.Equals("Test", StringComparison.OrdinalIgnoreCase),
        dbPath = new SqliteConnectionStringBuilder(ResolveConnectionString(config, env.ContentRootPath)).DataSource
    }));

app.MapGet("/invoice-builder/", () => Results.Redirect("/invoice-builder/index.html"));

app.MapFallbackToFile("index.html");

var openBrowser = bool.TryParse(app.Configuration["App:OpenBrowserOnStart"], out var shouldOpen) && shouldOpen;
if (openBrowser)
{
    app.Lifetime.ApplicationStarted.Register(() =>
    {
        try
        {
            Process.Start(new ProcessStartInfo(appUrl) { UseShellExecute = true });
        }
        catch
        {
            // Browser auto-open is a convenience only; app still runs without it.
        }
    });
}

await app.RunAsync();

static async Task<object> BuildJobTimelineAsync(AppDbContext db, string? q, string? customer)
{
    var groups = new Dictionary<string, TimelineGroupDto>(StringComparer.OrdinalIgnoreCase);
    var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var query = (q ?? string.Empty).Trim();
    var customerFilter = (customer ?? string.Empty).Trim();

    var sales = await db.Sales.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
    var jobs = await db.CustomerJobs.AsNoTracking().Where(x => !x.IsArchived).ToListAsync();
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
    ScoreWhen(scores, reasons, "invoice", 45, doc.RelatedRecordType?.Equals("Invoice", StringComparison.OrdinalIgnoreCase) == true, "You selected Related Area = Invoice.");
    ScoreWhen(scores, reasons, "bill", 45, doc.RelatedRecordType?.Equals("Bill", StringComparison.OrdinalIgnoreCase) == true, "You selected Related Area = Bill.");
    ScoreWhen(scores, reasons, "sale", 35, HasAny(text, "etsy order", "payment for order", "customer paid", "sale", "buyer paid", "order total"), "Looks like customer money or an order.");
    ScoreWhen(scores, reasons, "expense", 35, HasAny(text, "receipt", "purchase", "paid to", "charged", "expense", "invoice paid"), "Looks like a paid purchase/receipt.");
    ScoreWhen(scores, reasons, "expense", 40, HasAny(text, "etsy ads", "advertising", "marketing", "click-through", "listing fee", "processing fee", "transaction fee"), "Looks like marketplace fees or advertising.");
    ScoreWhen(scores, reasons, "shipping", 45, HasAny(text, "shipping label", "usps", "postage", "tracking", "ship by"), "Looks like shipping/postage.");
    ScoreWhen(scores, reasons, "invoice", 35, HasAny(text, "invoice", "amount due", "balance due", "payment due"), "Looks like an invoice.");
    ScoreWhen(scores, reasons, "estimate", 35, HasAny(text, "estimate", "quote", "proposal", "valid until"), "Looks like an estimate/quote.");
    ScoreWhen(scores, reasons, "asset", 40, HasAny(text, "printer", "ams", "bambu", "x1c", "p1s", "a1 mini", "equipment", "serial"), "Looks like equipment or durable business property.");
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
            "Open or create the invoice/AR row, confirm whether it is sent, partial, paid, or still open, then attach proof.",
            "receivables",
            "receivables",
            "invoice",
            new Dictionary<string, object?>
            {
                ["invoiceDate"] = date ?? today,
                ["invoiceNumber"] = invoiceNumber ?? orderNumber,
                ["projectName"] = product,
                ["status"] = amount.HasValue && text.Contains("paid") ? "Paid" : "Sent",
                ["invoiceTotal"] = amount,
                ["amountPaid"] = text.Contains("paid") ? amount : 0,
                ["sourceProof"] = proof,
                ["needsReview"] = true
            }),
        "estimate" => new InboxSuggestionLane(
            "Estimate",
            "Likely estimate/quote proof",
            "Use the estimate builder or create a customer job if this quote was created outside the app.",
            "estimates",
            null,
            null,
            new Dictionary<string, object?>()),
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
            deductibleExpenses = expenses.Sum(MoneyRules.TaxCountedExpenseAmount),
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

    group.MapPost("", async (TEntity entity, AppDbContext db) =>
    {
        entity.Id = 0;
        entity.CreatedAtUtc = DateTime.UtcNow;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        NormalizeCrudEntity(entity);
        db.Set<TEntity>().Add(entity);
        await ApplyCrudSideEffectsAsync(db, entity);
        await db.SaveChangesAsync();
        return Results.Created($"/api/{route}/{entity.Id}", entity);
    });

    group.MapPut("/{id:int}", async (int id, TEntity input, AppDbContext db) =>
    {
        var existing = await db.Set<TEntity>().FindAsync(id);
        if (existing is null)
        {
            return Results.NotFound();
        }

        var created = existing.CreatedAtUtc;
        db.Entry(existing).CurrentValues.SetValues(input);
        existing.Id = id;
        existing.CreatedAtUtc = created;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        NormalizeCrudEntity(existing);
        await ApplyCrudSideEffectsAsync(db, existing);
        await db.SaveChangesAsync();
        return Results.Ok(existing);
    });

    group.MapDelete("/{id:int}", async (int id, AppDbContext db) =>
    {
        var existing = await db.Set<TEntity>().FindAsync(id);
        if (existing is null)
        {
            return Results.NotFound();
        }

        existing.IsArchived = true;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.NoContent();
    });

    group.MapPost("/{id:int}/restore", async (int id, AppDbContext db) =>
    {
        var existing = await db.Set<TEntity>().FindAsync(id);
        if (existing is null)
        {
            return Results.NotFound();
        }

        existing.IsArchived = false;
        existing.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return Results.Ok(existing);
    });
}

static IResult Csv(string fileName, string csv)
{
    return Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", fileName);
}

static void NormalizeCrudEntity<TEntity>(TEntity entity) where TEntity : AuditableEntity
{
    ApplyDefaultClockToBusinessDates(entity);

    switch (entity)
    {
        case Bill bill:
            bill.Amount = ClampMoney(bill.Amount);
            bill.SalesTax = ClampMoney(bill.SalesTax);
            bill.Total = (bill.Amount ?? 0) + (bill.SalesTax ?? 0);
            bill.AmountPaid = ClampMoney(bill.AmountPaid);
            if (bill.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase) && (bill.AmountPaid ?? 0) <= 0 && (bill.Total ?? 0) > 0)
            {
                bill.AmountPaid = bill.Total;
            }
            break;
        case Expense expense:
            expense.Amount = ClampMoney(expense.Amount);
            expense.SalesTax = ClampMoney(expense.SalesTax);
            expense.Total = (expense.Amount ?? 0) + (expense.SalesTax ?? 0);
            expense.BusinessUsePercent = ClampPercent(expense.BusinessUsePercent);
            break;
        case ReceivableInvoice invoice:
            invoice.Subtotal = ClampMoney(invoice.Subtotal);
            invoice.Discount = ClampMoney(invoice.Discount);
            invoice.RushFee = ClampMoney(invoice.RushFee);
            invoice.SalesTax = ClampMoney(invoice.SalesTax);
            invoice.InvoiceTotal = Math.Max(0, (invoice.Subtotal ?? 0) - (invoice.Discount ?? 0) + (invoice.RushFee ?? 0) + (invoice.SalesTax ?? 0));
            invoice.AmountPaid = Math.Min(invoice.InvoiceTotal ?? 0, ClampMoney(invoice.AmountPaid) ?? 0);
            if (invoice.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase) && invoice.AmountPaid <= 0 && invoice.InvoiceTotal > 0)
            {
                invoice.AmountPaid = invoice.InvoiceTotal;
            }
            invoice.IncludeInCashReports = invoice.AmountPaid > 0 && !invoice.Status.Equals("Void", StringComparison.OrdinalIgnoreCase);
            if (invoice.Status.Equals("Void", StringComparison.OrdinalIgnoreCase))
            {
                invoice.AmountPaid = 0;
                invoice.IncludeInCashReports = false;
            }
            break;
        case Sale sale:
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
            if (expectedCustomerPaid > 0 && !sale.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase))
            {
                sale.CustomerPaid = expectedCustomerPaid;
            }
            if (sale.Status.Equals("Draft", StringComparison.OrdinalIgnoreCase)
                || sale.Status.Equals("Void", StringComparison.OrdinalIgnoreCase)
                || sale.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)
                || sale.Status.Equals("Canceled", StringComparison.OrdinalIgnoreCase))
            {
                sale.IncludeInDashboard = false;
            }
            break;
        case CustomerJob job:
            job.QuoteAmount = ClampMoney(job.QuoteAmount);
            job.InvoiceAmount = ClampMoney(job.InvoiceAmount);
            job.AmountPaid = ClampMoney(job.AmountPaid);
            if (!string.IsNullOrWhiteSpace(job.RelatedInvoiceNumber)
                && job.RelatedInvoiceNumber.StartsWith("EST-", StringComparison.OrdinalIgnoreCase))
            {
                job.JobType = "Estimate";
                job.InvoiceAmount = null;
                job.AmountPaid = null;
                if (job.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase))
                {
                    job.Status = "Quoted";
                }
            }
            break;
        case Asset asset:
            asset.Cost = ClampMoney(asset.Cost);
            asset.BusinessUsePercent = ClampPercent(asset.BusinessUsePercent);
            asset.NotYetExpensed = ClampMoney(asset.NotYetExpensed);
            break;
        case MakerWorldReward reward:
            reward.GiftCardAmount = ClampMoney(reward.GiftCardAmount);
            reward.PointsChange ??= 0;
            break;
        case Product product:
            product.TargetPrice = ClampMoney(product.TargetPrice);
            product.Grams = ClampMoney(product.Grams);
            product.MaterialCostPerGram = ClampMoney(product.MaterialCostPerGram);
            product.PrintHours = ClampMoney(product.PrintHours);
            product.MachineRatePerHour = ClampMoney(product.MachineRatePerHour);
            product.PackagingCost = ClampMoney(product.PackagingCost);
            product.DesignMinutes = ClampMoney(product.DesignMinutes);
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
    return value.Value.Date
        .Add(localClock.TimeOfDay)
        .AddMinutes(minuteOffset);
}

static async Task ApplyCrudSideEffectsAsync<TEntity>(AppDbContext db, TEntity entity) where TEntity : AuditableEntity
{
    if (entity is ReceivableInvoice invoice)
    {
        await SyncManualReceivableInvoiceToSaleAsync(db, invoice);
    }
}

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
            d.ArchiveReason
        });
}

static async Task<List<object>> UnifiedInvoiceRecordSummariesAsync(AppDbContext db, string? q, string? type, string? status, bool includeArchived = false)
{
    var docRows = await InvoiceDocumentSummaries(FilterInvoiceDocuments(db, q, type, status, includeArchived)).ToListAsync();
    var invoiceDocKeys = await db.InvoiceDocuments.AsNoTracking()
        .Where(d => !d.IsArchived && d.DocType == "INVOICE" && d.DocNumber != null && d.Status != "Void")
        .Select(d => new { d.DocNumber, d.CustomerName })
        .ToListAsync();

    var includeInvoices = string.IsNullOrWhiteSpace(type) || type.Equals("INVOICE", StringComparison.OrdinalIgnoreCase);
    var arQuery = db.ReceivableInvoices.AsNoTracking().Where(x => !x.IsArchived);
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

    var activeArRows = await arQuery
        .OrderByDescending(x => x.UpdatedAtUtc)
        .ThenByDescending(x => x.Id)
        .ToListAsync();

    var arOnlyRows = activeArRows
        .Where(x => !invoiceDocKeys.Any(d =>
            string.Equals(d.DocNumber, x.InvoiceNumber, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.CustomerName ?? string.Empty, x.CustomerName ?? string.Empty, StringComparison.OrdinalIgnoreCase)))
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

    var invoiceDocKeys = docs
        .Where(d => d.DocType == "INVOICE"
            && !StatusEquals(d.Status, "Void")
            && !string.IsNullOrWhiteSpace(d.DocNumber))
        .Select(d => new { d.DocNumber, d.CustomerName })
        .ToList();

    var arOnly = (await db.ReceivableInvoices.AsNoTracking()
        .Where(x => !x.IsArchived)
        .ToListAsync())
        .Where(x => !invoiceDocKeys.Any(d =>
            string.Equals(d.DocNumber, x.InvoiceNumber, StringComparison.OrdinalIgnoreCase)
            && string.Equals(d.CustomerName ?? string.Empty, x.CustomerName ?? string.Empty, StringComparison.OrdinalIgnoreCase)))
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

static async Task<InvoiceDocument> CreateInvoiceDocumentAsync(AppDbContext db, SaveInvoiceDocumentRequest request)
{
    var now = DateTimeOffset.UtcNow.ToString("O");
    var doc = new InvoiceDocument
    {
        CreatedAt = now,
        UpdatedAt = now,
        DocNumber = string.IsNullOrWhiteSpace(request.DocNumber)
            ? await NextInvoiceDocumentNumberAsync(db, request.DocType ?? "ESTIMATE")
            : request.DocNumber.Trim()
    };
    ApplyInvoiceDocumentRequest(doc, request);
    await ValidateInvoiceDocumentIdentityAsync(db, doc);
    db.InvoiceDocuments.Add(doc);
    await db.SaveChangesAsync();
    AddInvoiceDocumentEvent(db, doc, "Created", null, doc.Status, $"{doc.DocType} created as {doc.Status}", $"{doc.DocNumber} was created.", doc.Total);
    await db.SaveChangesAsync();
    await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc);
    return doc;
}

static async Task<InvoiceDocument?> UpdateInvoiceDocumentAsync(AppDbContext db, int id, SaveInvoiceDocumentRequest request)
{
    var doc = await db.InvoiceDocuments.Include(d => d.LineItems).FirstOrDefaultAsync(d => d.Id == id);
    if (doc is null)
    {
        return null;
    }
    if (doc.IsArchived)
    {
        throw new InvalidOperationException($"Archived document {doc.DocNumber} must be restored before it can be edited.");
    }

    var before = CaptureInvoiceDocumentAuditState(doc);
    doc.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
    var requestedType = (request.DocType ?? doc.DocType ?? "ESTIMATE").ToUpperInvariant();
    if (!string.Equals(doc.DocType, requestedType, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"Cannot change saved {doc.DocType} {doc.DocNumber} into a {requestedType} by saving. Use Create Invoice from the estimate row so the original estimate stays intact.");
    }

    ApplyInvoiceDocumentRequest(doc, request);
    await ValidateInvoiceDocumentIdentityAsync(db, doc);
    AddInvoiceDocumentChangeEvents(db, doc, before);
    await db.SaveChangesAsync();
    await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc);
    return doc;
}

static async Task<InvoiceDocument?> DuplicateInvoiceDocumentAsync(AppDbContext db, int id, string? targetType)
{
    var source = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
        .FirstOrDefaultAsync(d => d.Id == id && !d.IsArchived);
    if (source is null)
    {
        return null;
    }

    var docType = targetType ?? source.DocType;
    var now = DateTimeOffset.UtcNow.ToString("O");
    var copy = new InvoiceDocument
    {
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
    db.InvoiceDocuments.Add(copy);
    await db.SaveChangesAsync();
    AddInvoiceDocumentEvent(db, copy, targetType == "INVOICE" ? "Converted" : "Duplicated", source.Status, copy.Status,
        targetType == "INVOICE" ? $"Invoice created from {source.DocNumber}" : $"Document duplicated from {source.DocNumber}",
        $"{copy.DocNumber} was created from {source.DocNumber}.", copy.Total);
    await db.SaveChangesAsync();
    await SyncUnifiedInvoiceDocumentToLedgerAsync(db, copy);
    if (targetType == "INVOICE")
    {
        await MarkEstimateConvertedAsync(db, source.Id, copy.DocNumber);
    }
    return copy;
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
    var job = await db.CustomerJobs.FirstOrDefaultAsync(x =>
        !x.IsArchived
        && x.RelatedInvoiceNumber == estimate.DocNumber
        && x.SourceProof == sourceProof);
    if (job is not null)
    {
        job.Status = "Invoiced";
        job.InvoiceAmount = null;
        job.Notes = AppendNote(job.Notes, $"Converted to invoice {invoiceNumber}.");
        job.UpdatedAtUtc = DateTime.UtcNow;
    }

    await db.SaveChangesAsync();
}

static async Task ValidateInvoiceDocumentIdentityAsync(AppDbContext db, InvoiceDocument doc)
{
    if (string.IsNullOrWhiteSpace(doc.DocNumber))
    {
        return;
    }

    var expectedPrefix = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? "INV-" : "EST-";
    if (!doc.DocNumber.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException($"{doc.DocType} documents must use a {expectedPrefix} number. Use Save as New or create a new {doc.DocType.ToLowerInvariant()} instead of changing the type on an existing record.");
    }

    var duplicate = await db.InvoiceDocuments.AsNoTracking()
        .Where(d => !d.IsArchived && d.Id != doc.Id && d.DocNumber == doc.DocNumber)
        .Select(d => new { d.Id, d.DocType, d.CustomerName, d.ProjectName })
        .FirstOrDefaultAsync();
    if (duplicate is not null)
    {
        throw new InvalidOperationException($"Document number {doc.DocNumber} is already used by record #{duplicate.Id} ({duplicate.DocType}, {duplicate.CustomerName}, {duplicate.ProjectName}).");
    }
}

static async Task<IResult> DeleteInvoiceDocumentAndSyncAsync(AppDbContext db, int id)
{
    var doc = await db.InvoiceDocuments.FindAsync(id);
    if (doc is null)
    {
        return Results.NotFound(new { message = $"Document {id} was not found." });
    }

    var docNumber = doc.DocNumber ?? string.Empty;
    if (!string.IsNullOrWhiteSpace(docNumber))
    {
        if (doc.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase))
        {
            var sourceProof = $"Unified estimate {docNumber}";
            var jobs = await db.CustomerJobs
                .Where(x => !x.IsArchived && x.RelatedInvoiceNumber == docNumber && x.SourceProof == sourceProof)
                .ToListAsync();
            foreach (var job in jobs)
            {
                job.IsArchived = true;
                job.UpdatedAtUtc = DateTime.UtcNow;
            }
        }
        else if (doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase))
        {
            var sourceProof = $"Unified invoice {docNumber}";
            var invoice = await db.ReceivableInvoices.FirstOrDefaultAsync(x => !x.IsArchived && x.InvoiceNumber == docNumber && x.SourceProof == sourceProof);
            if (invoice is not null)
            {
                invoice.IsArchived = true;
                invoice.UpdatedAtUtc = DateTime.UtcNow;
            }

            var sales = await db.Sales
                .Where(x => !x.IsArchived && x.Platform == "Direct" && x.InvoiceNumber == docNumber && x.SourceProof == sourceProof)
                .ToListAsync();
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
    AddInvoiceDocumentEvent(db, doc, "Archived", doc.Status, doc.Status, $"{doc.DocType} archived", $"{doc.DocNumber} was archived, not deleted.", doc.Total);
    await db.SaveChangesAsync();
    return Results.Ok(new { archived = id, docNumber });
}

static async Task<IResult> RestoreInvoiceDocumentAndSyncAsync(AppDbContext db, int id)
{
    var doc = await db.InvoiceDocuments.Include(d => d.LineItems).FirstOrDefaultAsync(d => d.Id == id);
    if (doc is null)
    {
        return Results.NotFound(new { message = $"Document {id} was not found." });
    }

    doc.IsArchived = false;
    doc.ArchivedAt = null;
    doc.ArchiveReason = null;
    doc.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
    await ValidateInvoiceDocumentIdentityAsync(db, doc);
    AddInvoiceDocumentEvent(db, doc, "Restored", doc.Status, doc.Status, $"{doc.DocType} restored", $"{doc.DocNumber} was restored from archive.", doc.Total);
    await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc);
    await db.SaveChangesAsync();
    return Results.Ok(ToInvoiceDocumentDto(doc));
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
    }
}

static async Task<(byte[] Bytes, string FileName)> CreateDatabaseBackupAsync(AppDbContext db, IWebHostEnvironment env, IConfiguration configuration)
{
    var dbPath = GetDatabasePath(configuration, env);
    if (!System.IO.File.Exists(dbPath))
    {
        return ([], "epata-business-ledger-missing.db");
    }

    var fileName = $"epata-business-ledger-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid().ToString("N")[..8]}.db";
    var backupDir = Path.Combine(env.ContentRootPath, "Backups");
    Directory.CreateDirectory(backupDir);
    var backupPath = Path.Combine(backupDir, fileName);
    var sourceConnectionString = new SqliteConnectionStringBuilder(ResolveConnectionString(configuration, env.ContentRootPath))
    {
        Pooling = false
    }.ToString();
    var destinationConnectionString = new SqliteConnectionStringBuilder { DataSource = backupPath, Pooling = false }.ToString();

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

static string GuessDocumentType(string fileName)
{
    var lower = fileName.ToLowerInvariant();
    if (lower.Contains("etsy") || lower.Contains("order")) return "Etsy Order";
    if (lower.Contains("invoice") || lower.Contains("inv-")) return "Invoice";
    if (lower.Contains("receipt")) return "Receipt";
    if (lower.Contains("tax")) return "Tax";
    if (lower.Contains("bank") || lower.Contains("statement")) return "Bank";
    return "Other";
}

static async Task<string> TryExtractDocumentPreviewAsync(string path, string? contentType)
{
    try
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (contentType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true ||
            extension is ".txt" or ".csv" or ".json" or ".md")
        {
            var text = await System.IO.File.ReadAllTextAsync(path);
            return TrimPreview(text);
        }

        if (extension == ".pdf")
        {
            var bytes = await System.IO.File.ReadAllBytesAsync(path);
            var raw = Encoding.Latin1.GetString(bytes);
            var matches = System.Text.RegularExpressions.Regex.Matches(raw, @"\((?<text>(?:\\.|[^\\)]){3,})\)");
            var pieces = matches
                .Select(m => m.Groups["text"].Value.Replace("\\(", "(").Replace("\\)", ")").Replace("\\n", " "))
                .Where(s => s.Any(char.IsLetterOrDigit))
                .Take(80);
            return TrimPreview(string.Join(" ", pieces));
        }
    }
    catch
    {
        return string.Empty;
    }

    return string.Empty;
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

    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceDocuments_DocNumber\" ON \"InvoiceDocuments\" (\"DocNumber\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceDocuments_UpdatedAt\" ON \"InvoiceDocuments\" (\"UpdatedAt\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceLineItems_InvoiceDocumentId\" ON \"InvoiceLineItems\" (\"InvoiceDocumentId\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceDocumentEvents_InvoiceDocumentId\" ON \"InvoiceDocumentEvents\" (\"InvoiceDocumentId\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceDocumentEvents_CreatedAt\" ON \"InvoiceDocumentEvents\" (\"CreatedAt\");");

    // ── Schema migrations for new fields (safe on existing DBs) ──────────────
    var alterations = new[]
    {
        // Expenses
        "ALTER TABLE \"Expenses\" ADD COLUMN \"TaxBucket\" TEXT NOT NULL DEFAULT 'Operating Expense'",
        "ALTER TABLE \"Expenses\" ADD COLUMN \"DeductibleStatus\" TEXT NOT NULL DEFAULT 'Yes'",
        "ALTER TABLE \"Expenses\" ADD COLUMN \"BusinessUsePercent\" TEXT NULL",
        "ALTER TABLE \"Expenses\" ADD COLUMN \"CountedExpense\" INTEGER NOT NULL DEFAULT 1",
        // Assets
        "ALTER TABLE \"Assets\" ADD COLUMN \"InServiceDate\" TEXT NULL",
        "ALTER TABLE \"Assets\" ADD COLUMN \"TaxTreatment\" TEXT NOT NULL DEFAULT 'Review'",
        "ALTER TABLE \"Assets\" ADD COLUMN \"CountedExpenseThisYear\" INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE \"Assets\" ADD COLUMN \"NotYetExpensed\" TEXT NULL",
        // MakerWorld
        "ALTER TABLE \"MakerWorldRewards\" ADD COLUMN \"IncomeStatus\" TEXT NOT NULL DEFAULT 'Review'",
        // Unified invoice/estimate archive safety
        "ALTER TABLE \"InvoiceDocuments\" ADD COLUMN \"IsArchived\" INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE \"InvoiceDocuments\" ADD COLUMN \"ArchivedAt\" TEXT NULL",
        "ALTER TABLE \"InvoiceDocuments\" ADD COLUMN \"ArchiveReason\" TEXT NULL",
    };

    foreach (var sql in alterations)
    {
        try { await db.Database.ExecuteSqlRawAsync(sql); }
        catch { /* Column already exists — safe to ignore */ }
    }
}

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
        var paid = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? Math.Min(Math.Max(0, doc.AmountPaid), total) : 0;
        var balance = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? Math.Max(0, total - paid) : 0;

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
            && x.Platform == "Direct"
            && x.IncludeInDashboard
            && x.InvoiceNumber != null
            && x.CustomerPaid != null
            && x.CustomerPaid > 0)
        .ToListAsync();

    foreach (var sale in sales)
    {
        var exists = await db.ReceivableInvoices.AnyAsync(x =>
            !x.IsArchived
            && x.InvoiceNumber == sale.InvoiceNumber
            && x.CustomerName == sale.CustomerName);

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
    doc.DocNumber = request.DocNumber ?? doc.DocNumber;
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
    else if (doc.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase) && paid <= 0 && total > 0)
    {
        paid = total;
    }

    doc.Subtotal = lineSubtotal;
    doc.DiscountAmount = discount;
    doc.RushAmount = rush;
    doc.TaxAmount = tax;
    doc.Total = total;
    doc.AmountPaid = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? Math.Min(paid, total) : 0;
    doc.Balance = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase)
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
    else if (doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) && doc.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase) && paid <= 0 && total > 0)
    {
        paid = total;
    }

    doc.Subtotal = subtotal;
    doc.DiscountAmount = discount;
    doc.RushAmount = rush;
    doc.TaxAmount = tax;
    doc.Total = total;
    doc.AmountPaid = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? paid : 0;
    doc.Balance = doc.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase) ? Math.Max(0, total - paid) : 0;
}

static async Task SyncUnifiedInvoiceDocumentToLedgerAsync(AppDbContext db, InvoiceDocument doc)
{
    if (string.IsNullOrWhiteSpace(doc.DocNumber))
    {
        return;
    }

    if (doc.DocType == "ESTIMATE")
    {
        var job = await db.CustomerJobs.FirstOrDefaultAsync(x => x.RelatedInvoiceNumber == doc.DocNumber);
        if (job is null)
        {
            job = new CustomerJob
            {
                Platform = "Direct",
                JobType = "Estimate",
                RelatedInvoiceNumber = doc.DocNumber,
                SourceProof = $"Unified estimate {doc.DocNumber}",
                Notes = "Created from the unified estimate/invoice workspace. Estimate only; not income and not AR."
            };
            db.CustomerJobs.Add(job);
        }

        job.JobDate = ParseDate(doc.DocDate) ?? job.JobDate;
        job.DueDate = ParseDate(doc.DueDate) ?? job.DueDate;
        job.CustomerName = doc.CustomerName ?? job.CustomerName;
        job.JobName = doc.ProjectName ?? job.JobName;
        job.Material = doc.Material ?? job.Material;
        job.Color = doc.Color ?? job.Color;
        job.Description = doc.ProjectDescription ?? job.Description;
        job.Status = doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase) ? "Cancelled" : "Quoted";
        job.QuoteAmount = doc.Total;
        job.AmountPaid = null;
        job.InvoiceAmount = null;
        job.NeedsReview = false;
    }
    else if (doc.DocType == "INVOICE")
    {
        var unifiedSource = $"Unified invoice {doc.DocNumber}";
        var invoice = await db.ReceivableInvoices.FirstOrDefaultAsync(x => x.SourceProof == unifiedSource);
        invoice ??= await db.ReceivableInvoices.FirstOrDefaultAsync(x =>
            x.InvoiceNumber == doc.DocNumber
            && x.CustomerName == (doc.CustomerName ?? string.Empty));

        if (invoice is null)
        {
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
        invoice.IsArchived = invoice.Status.Equals("Void", StringComparison.OrdinalIgnoreCase);
        invoice.Subtotal = doc.Subtotal;
        invoice.Discount = doc.DiscountAmount;
        invoice.RushFee = doc.RushAmount;
        invoice.TaxRatePercent = doc.CalcTaxRate;
        invoice.SalesTax = doc.TaxAmount;
        invoice.InvoiceTotal = doc.Total;
        invoice.AmountPaid = doc.AmountPaid;
        invoice.IncludeInCashReports = doc.AmountPaid > 0;
        invoice.NeedsReview = doc.AmountPaid < doc.Total && !invoice.Status.Equals("Void", StringComparison.OrdinalIgnoreCase);

        await SyncInvoicePaymentToSaleAsync(db, doc);
    }

    await db.SaveChangesAsync();
}

static async Task SyncInvoicePaymentToSaleAsync(AppDbContext db, InvoiceDocument doc)
{
    var unifiedSource = $"Unified invoice {doc.DocNumber}";
    var sale = await db.Sales.FirstOrDefaultAsync(x =>
        x.InvoiceNumber == doc.DocNumber
        && x.Platform == "Direct"
        && (x.SourceProof == unifiedSource || x.CustomerName == doc.CustomerName));

    if (doc.AmountPaid <= 0 || doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase))
    {
        if (sale is not null && string.Equals(sale.SourceProof, unifiedSource, StringComparison.OrdinalIgnoreCase))
        {
            sale.IncludeInDashboard = false;
            sale.Status = "Draft";
            sale.NeedsReview = false;
            sale.Notes = "Automatically hidden because the unified invoice is unpaid or void.";
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
    sale.Color = doc.Color ?? sale.Color;
    sale.Quantity = 1;
    sale.ItemSales = allocated.SalesBase;
    sale.ShippingCharged = 0;
    sale.SalesTaxCollected = allocated.TaxMemo;
    sale.CustomerPaid = doc.AmountPaid;
    sale.Status = doc.AmountPaid >= doc.Total ? "Paid" : "Partial";
    sale.IncludeInDashboard = true;
    sale.NeedsReview = false;
}

static async Task SyncManualReceivableInvoiceToSaleAsync(AppDbContext db, ReceivableInvoice invoice)
{
    if (string.IsNullOrWhiteSpace(invoice.InvoiceNumber))
    {
        return;
    }

    var sourceProof = string.IsNullOrWhiteSpace(invoice.SourceProof)
        ? $"Receivable invoice {invoice.InvoiceNumber}"
        : invoice.SourceProof;
    var sale = await db.Sales.FirstOrDefaultAsync(x =>
        x.Platform == "Direct"
        && x.InvoiceNumber == invoice.InvoiceNumber
        && (x.SourceProof == sourceProof || x.CustomerName == invoice.CustomerName));
    var paid = invoice.AmountPaid ?? 0;
    var total = invoice.InvoiceTotal ?? 0;

    if (paid <= 0 || invoice.Status.Equals("Void", StringComparison.OrdinalIgnoreCase))
    {
        if (sale is not null && string.Equals(sale.SourceProof, sourceProof, StringComparison.OrdinalIgnoreCase))
        {
            sale.IncludeInDashboard = false;
            sale.Status = "Draft";
            sale.NeedsReview = false;
            sale.Notes = "Automatically hidden because the receivable invoice is unpaid or void.";
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
            IncludeInDashboard = true,
            Notes = "Created automatically from a paid receivable invoice."
        };
        db.Sales.Add(sale);
    }

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
    sale.Quantity = 1;
    sale.ItemSales = allocated.SalesBase;
    sale.ShippingCharged = 0;
    sale.SalesTaxCollected = allocated.TaxMemo;
    sale.CustomerPaid = paid;
    sale.Status = paid >= total ? "Paid" : "Partial";
    sale.IncludeInDashboard = true;
    sale.NeedsReview = false;
}

static object ToInvoiceDocumentDto(InvoiceDocument doc) => new
{
    doc.Id,
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
    string Status,
    string? CustomerName,
    string? ProjectName,
    decimal Total,
    decimal AmountPaid,
    decimal Balance,
    string LineSignature);

public sealed record TaxAuditIssue(string Severity, string Title, string Record, string Detail);
