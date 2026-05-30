using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
    await DbSeeder.SeedPatchAsync(db);
    await SeedUnifiedInvoiceDocumentsFromLedgerAsync(db);
    await NormalizeInvoiceDocumentRowsAsync(db);
    await NormalizeLedgerMoneyRowsAsync(db);
    await RepairInvoiceProofCustomerMismatchesAsync(db);
    await RepairDirectPaidSalesMissingArAsync(db);
    await ArchiveOrphanedUnifiedLedgerRowsAsync(db);
}

app.UseDefaultFiles();
app.UseStaticFiles();

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

app.MapGet("/api/documents", async (AppDbContext db, string? q, string? type, string? status) =>
{
    var query = FilterInvoiceDocuments(db, q, type, status);
    return Results.Ok(await InvoiceDocumentSummaries(query).ToListAsync());
});

app.MapGet("/api/documents/stats", async (AppDbContext db) =>
{
    var all = await db.InvoiceDocuments.AsNoTracking()
        .Select(d => new { d.DocType, d.Status, d.Total, d.AmountPaid, d.Balance })
        .ToListAsync();
    var invoices = all.Where(d => d.DocType == "INVOICE").ToList();
    var openInvoices = invoices.Where(d => !IsClosedInvoiceDocumentStatus(d.Status)).ToList();
    var paidInvoices = invoices.Where(d => d.Status != "Void").ToList();

    return Results.Ok(new
    {
        totalEstimates = all.Count(d => d.DocType == "ESTIMATE"),
        totalInvoices = invoices.Count,
        totalRevenue = paidInvoices.Sum(d => d.AmountPaid),
        totalInvoiced = invoices.Where(d => d.Status != "Void").Sum(d => d.Total),
        paidRevenue = paidInvoices.Sum(d => d.AmountPaid),
        unpaidBalance = openInvoices.Sum(d => Math.Max(0, d.Balance)),
        draftCount = all.Count(d => d.Status == "Draft"),
        sentCount = all.Count(d => d.Status == "Sent"),
        paidCount = all.Count(d => d.Status == "Paid"),
        voidCount = all.Count(d => d.Status == "Void")
    });
});

app.MapGet("/api/documents/next-number", async (AppDbContext db, string type) =>
    Results.Ok(new { number = await NextInvoiceDocumentNumberAsync(db, type) }));

app.MapGet("/api/documents/latest", async (AppDbContext db) =>
{
    var doc = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
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

app.MapPost("/api/documents/{id:int}/duplicate", async (AppDbContext db, int id) =>
{
    var copy = await DuplicateInvoiceDocumentAsync(db, id, null);
    return copy is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(copy));
});

app.MapGet("/api/database/backup", async (AppDbContext db, IWebHostEnvironment env, IConfiguration configuration) =>
{
    var backup = await CreateDatabaseBackupAsync(db, env, configuration);
    return Results.File(backup.Bytes, "application/x-sqlite3", backup.FileName);
});

app.MapPost("/api/database/clear", () => Results.BadRequest(new { message = "Clear is disabled in the unified ledger. Delete individual documents or back up the database first." }));
app.MapPost("/api/database/import", () => Results.BadRequest(new { message = "Full database import is disabled in the unified ledger. Use Import Old App Once for invoice migration." }));

app.MapGet("/api/invoice-documents", async (AppDbContext db, string? q, string? type, string? status) =>
{
    return Results.Ok(await InvoiceDocumentSummaries(FilterInvoiceDocuments(db, q, type, status)).ToListAsync());
});

app.MapGet("/api/invoice-documents/stats", async (AppDbContext db) =>
{
    var all = await db.InvoiceDocuments.AsNoTracking()
        .Select(d => new { d.DocType, d.Status, d.Total, d.AmountPaid, d.Balance })
        .ToListAsync();
    var invoices = all.Where(d => d.DocType == "INVOICE").ToList();
    var openInvoices = invoices.Where(d => !IsClosedInvoiceDocumentStatus(d.Status)).ToList();
    var paidInvoices = invoices.Where(d => d.Status != "Void").ToList();

    return Results.Ok(new
    {
        totalEstimates = all.Count(d => d.DocType == "ESTIMATE"),
        totalInvoices = invoices.Count,
        totalRevenue = paidInvoices.Sum(d => d.AmountPaid),
        totalInvoiced = invoices.Where(d => d.Status != "Void").Sum(d => d.Total),
        paidRevenue = paidInvoices.Sum(d => d.AmountPaid),
        unpaidBalance = openInvoices.Sum(d => Math.Max(0, d.Balance)),
        draftCount = all.Count(d => d.Status == "Draft"),
        sentCount = all.Count(d => d.Status == "Sent"),
        paidCount = all.Count(d => d.Status == "Paid"),
        voidCount = all.Count(d => d.Status == "Void")
    });
});

app.MapGet("/api/invoice-documents/next-number", async (AppDbContext db, string type) =>
{
    return Results.Ok(new { number = await NextInvoiceDocumentNumberAsync(db, type) });
});

app.MapGet("/api/invoice-documents/latest", async (AppDbContext db) =>
{
    var doc = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
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
    var now = DateTimeOffset.UtcNow.ToString("O");
    var doc = new InvoiceDocument
    {
        CreatedAt = now,
        UpdatedAt = now,
        DocNumber = string.IsNullOrWhiteSpace(request.DocNumber) ? await NextInvoiceDocumentNumberAsync(db, request.DocType ?? "ESTIMATE") : request.DocNumber
    };
    ApplyInvoiceDocumentRequest(doc, request);
    db.InvoiceDocuments.Add(doc);
    await db.SaveChangesAsync();
    await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc);
    return Results.Ok(ToInvoiceDocumentDto(doc));
});

app.MapPut("/api/invoice-documents/{id:int}", async (AppDbContext db, int id, SaveInvoiceDocumentRequest request) =>
{
    var doc = await db.InvoiceDocuments.Include(d => d.LineItems).FirstOrDefaultAsync(d => d.Id == id);
    if (doc is null)
    {
        return Results.NotFound(new { message = $"Document {id} was not found." });
    }

    doc.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
    ApplyInvoiceDocumentRequest(doc, request);
    await db.SaveChangesAsync();
    await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc);
    return Results.Ok(ToInvoiceDocumentDto(doc));
});

app.MapDelete("/api/invoice-documents/{id:int}", async (AppDbContext db, int id) =>
{
    return await DeleteInvoiceDocumentAndSyncAsync(db, id);
});

app.MapPost("/api/invoice-documents/{id:int}/duplicate", async (AppDbContext db, int id) =>
{
    var source = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
        .FirstOrDefaultAsync(d => d.Id == id);
    if (source is null)
    {
        return Results.NotFound(new { message = $"Document {id} was not found." });
    }

    var now = DateTimeOffset.UtcNow.ToString("O");
    var copy = new InvoiceDocument
    {
        DocNumber = await NextInvoiceDocumentNumberAsync(db, source.DocType),
        DocType = source.DocType,
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
        ProjectNotes = source.ProjectNotes,
        PageSize = source.PageSize,
        DocDate = DateTime.Now.ToString("yyyy-MM-dd"),
        DueDate = DateTime.Now.AddDays(source.DocType == "INVOICE" ? 7 : 14).ToString("yyyy-MM-dd"),
        Subtotal = source.Subtotal,
        DiscountAmount = source.DiscountAmount,
        RushAmount = source.RushAmount,
        TaxAmount = source.TaxAmount,
        Total = source.Total,
        AmountPaid = 0,
        Balance = source.DocType == "INVOICE" ? source.Total : 0,
        PricingGuide = source.PricingGuide,
        TermsNotes = source.TermsNotes,
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
        Json = source.Json,
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

    db.InvoiceDocuments.Add(copy);
    await db.SaveChangesAsync();
    await SyncUnifiedInvoiceDocumentToLedgerAsync(db, copy);
    return Results.Ok(ToInvoiceDocumentDto(copy));
});

app.MapPost("/api/invoice-documents/{id:int}/convert-to-invoice", async (AppDbContext db, int id) =>
{
    var source = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
        .FirstOrDefaultAsync(d => d.Id == id);
    if (source is null)
    {
        return Results.NotFound(new { message = $"Document {id} was not found." });
    }

    var now = DateTimeOffset.UtcNow.ToString("O");
    var invoice = new InvoiceDocument
    {
        DocNumber = await NextInvoiceDocumentNumberAsync(db, "INVOICE"),
        DocType = "INVOICE",
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
        ProjectNotes = $"Converted from estimate {source.DocNumber}. {source.ProjectNotes}".Trim(),
        PageSize = source.PageSize,
        DocDate = DateTime.Now.ToString("yyyy-MM-dd"),
        DueDate = DateTime.Now.AddDays(7).ToString("yyyy-MM-dd"),
        Subtotal = source.Subtotal,
        DiscountAmount = source.DiscountAmount,
        RushAmount = source.RushAmount,
        TaxAmount = source.TaxAmount,
        Total = source.Total,
        AmountPaid = 0,
        Balance = source.Total,
        PricingGuide = source.PricingGuide,
        TermsNotes = "Payment due by the due date shown above.",
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
        Json = source.Json,
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

    db.InvoiceDocuments.Add(invoice);
    await db.SaveChangesAsync();
    await SyncUnifiedInvoiceDocumentToLedgerAsync(db, invoice);
    return Results.Ok(ToInvoiceDocumentDto(invoice));
});

app.MapPost("/api/invoice-documents/import-from-legacy", async (InvoiceAppImportService importer, AppDbContext db) =>
{
    var docs = await importer.ReadFullDocumentsAsync();
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
            Quantity = line.Quantity,
            Rate = line.Rate,
            Amount = line.Amount
        }));
        doc.UpdatedAt = old.UpdatedAt ?? DateTimeOffset.UtcNow.ToString("O");
    }

    await db.SaveChangesAsync();
    foreach (var doc in await db.InvoiceDocuments.Include(d => d.LineItems).ToListAsync())
    {
        await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc);
    }

    return Results.Ok(new { imported = docs.Count, created, updated });
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
    return Results.Ok(new { count = created.Count, documents = created });
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

app.MapPost("/api/system/backup", (IConfiguration configuration, IWebHostEnvironment env) =>
{
    var dbPath = GetDatabasePath(configuration, env);
    if (!System.IO.File.Exists(dbPath))
    {
        return Results.NotFound(new { message = "Database file was not found yet. Enter data first, then try backup again." });
    }

    var backupDir = Path.Combine(env.ContentRootPath, "Backups");
    Directory.CreateDirectory(backupDir);
    var backupPath = Path.Combine(backupDir, $"epata-business-ledger-{DateTime.Now:yyyyMMdd-HHmmss}.db");
    System.IO.File.Copy(dbPath, backupPath, overwrite: true);
    return Results.File(backupPath, "application/octet-stream", Path.GetFileName(backupPath));
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

    foreach (var doc in docs.Where(x => x.DocType.Equals("INVOICE", StringComparison.OrdinalIgnoreCase)))
    {
        var expectedTotal = doc.Subtotal - doc.DiscountAmount + doc.RushAmount + doc.TaxAmount;
        if (Math.Abs(expectedTotal - doc.Total) > 0.02m)
        {
            issues.Add(new("High", "Invoice document total mismatch", doc.DocNumber ?? $"Invoice document #{doc.Id}",
                $"Total {doc.Total:C} does not equal subtotal - discount + rush + tax {expectedTotal:C}."));
        }

        var expectedBalance = Math.Max(0, doc.Total - doc.AmountPaid);
        if (Math.Abs(expectedBalance - doc.Balance) > 0.02m)
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
        db.Set<TEntity>().Add(entity);
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

static IQueryable<T> ActiveRows<T>(IQueryable<T> query, bool includeArchived) where T : AuditableEntity
{
    return includeArchived ? query : query.Where(x => !x.IsArchived);
}

static IQueryable<InvoiceDocument> FilterInvoiceDocuments(AppDbContext db, string? q, string? type, string? status)
{
    var query = db.InvoiceDocuments.AsNoTracking().AsQueryable();
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
            d.UpdatedAt
        });
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
            : request.DocNumber
    };
    ApplyInvoiceDocumentRequest(doc, request);
    db.InvoiceDocuments.Add(doc);
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

    doc.UpdatedAt = DateTimeOffset.UtcNow.ToString("O");
    ApplyInvoiceDocumentRequest(doc, request);
    await db.SaveChangesAsync();
    await SyncUnifiedInvoiceDocumentToLedgerAsync(db, doc);
    return doc;
}

static async Task<InvoiceDocument?> DuplicateInvoiceDocumentAsync(AppDbContext db, int id, string? targetType)
{
    var source = await db.InvoiceDocuments.AsNoTracking()
        .Include(d => d.LineItems.OrderBy(li => li.SortOrder))
        .FirstOrDefaultAsync(d => d.Id == id);
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
        Json = source.Json,
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

    db.InvoiceDocuments.Add(copy);
    await db.SaveChangesAsync();
    await SyncUnifiedInvoiceDocumentToLedgerAsync(db, copy);
    return copy;
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

    db.InvoiceDocuments.Remove(doc);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = id, docNumber });
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
    await db.Database.CloseConnectionAsync();
    var dbPath = GetDatabasePath(configuration, env);
    if (!System.IO.File.Exists(dbPath))
    {
        return ([], "epata-business-ledger-missing.db");
    }

    var bytes = await System.IO.File.ReadAllBytesAsync(dbPath);
    return (bytes, $"epata-business-ledger-{DateTime.Now:yyyyMMdd-HHmmss}.db");
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

    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceDocuments_DocNumber\" ON \"InvoiceDocuments\" (\"DocNumber\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceDocuments_UpdatedAt\" ON \"InvoiceDocuments\" (\"UpdatedAt\");");
    await db.Database.ExecuteSqlRawAsync("CREATE INDEX IF NOT EXISTS \"IX_InvoiceLineItems_InvoiceDocumentId\" ON \"InvoiceLineItems\" (\"InvoiceDocumentId\");");

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
    };

    foreach (var sql in alterations)
    {
        try { await db.Database.ExecuteSqlRawAsync(sql); }
        catch { /* Column already exists — safe to ignore */ }
    }
}

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
            bill.NeedsReview = true;
            bill.Notes = AppendNote(bill.Notes, $"Review total: amount + sales tax equals {expectedTotal:C}, but Total is {(bill.Total ?? 0):C}.");
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
            expense.NeedsReview = true;
            expense.Notes = AppendNote(expense.Notes, $"Review total: amount + sales tax equals {expectedTotal:C}, but Total is {(expense.Total ?? 0):C}.");
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
    doc.Subtotal = request.Subtotal;
    doc.DiscountAmount = request.DiscountAmount;
    doc.RushAmount = request.RushAmount;
    doc.TaxAmount = request.TaxAmount;
    doc.Total = request.Total;
    doc.AmountPaid = request.AmountPaid;
    doc.Balance = request.Balance;
    if (doc.DocType.Equals("ESTIMATE", StringComparison.OrdinalIgnoreCase))
    {
        doc.AmountPaid = 0;
        doc.Balance = 0;
        if (doc.Status.Equals("Paid", StringComparison.OrdinalIgnoreCase))
        {
            doc.Status = "Accepted";
        }
    }
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
            Quantity = line.Quantity,
            Rate = line.Rate,
            Amount = line.Amount
        }));
    }
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

public sealed record TaxAuditIssue(string Severity, string Title, string Record, string Detail);
