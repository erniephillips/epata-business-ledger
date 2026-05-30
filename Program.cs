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
    var connectionString = builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=Data/epata-business-ledger.db";
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

Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "Data"));
Directory.CreateDirectory(Path.Combine(app.Environment.ContentRootPath, "Backups"));

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    await db.Database.EnsureCreatedAsync();
    await EnsureUnifiedInvoiceTablesAsync(db);
    await DbSeeder.SeedAsync(db, app.Configuration);
    await DbSeeder.SeedPatchAsync(db);
    await SeedUnifiedInvoiceDocumentsFromLedgerAsync(db);
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

app.MapGet("/api/lookups", async (AppDbContext db) =>
{
    var customers = await db.Parties.Where(x => !x.IsArchived && (x.PartyType == "Customer" || x.PartyType == "Both")).OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
    var vendors = await db.Parties.Where(x => !x.IsArchived && (x.PartyType == "Vendor" || x.PartyType == "Both")).OrderBy(x => x.Name).Select(x => x.Name).ToListAsync();
    var products = await db.Products.Where(x => !x.IsArchived).OrderBy(x => x.Name).Select(x => new { x.Name, x.Sku, x.TargetPrice }).ToListAsync();
    return Results.Ok(new { customers, vendors, products });
});

app.MapPost("/api/import/invoice-app", async (InvoiceAppImportService importer, ImportInvoiceRequest request) =>
{
    var result = await importer.TryReadInvoiceAppAsync(request.BaseUrl);
    return Results.Ok(result);
});

app.MapGet("/api/health", (IWebHostEnvironment env) => Results.Ok(new
{
    status = "ok",
    database = Path.Combine(env.ContentRootPath, "Data", "epata-business-ledger.db"),
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

    return Results.Ok(new
    {
        totalEstimates = all.Count(d => d.DocType == "ESTIMATE"),
        totalInvoices = all.Count(d => d.DocType == "INVOICE"),
        totalRevenue = all.Where(d => d.DocType == "INVOICE").Sum(d => d.Total),
        paidRevenue = all.Where(d => d.DocType == "INVOICE" && d.Status == "Paid").Sum(d => d.Total),
        unpaidBalance = all.Where(d => d.DocType == "INVOICE" && d.Status != "Paid" && d.Status != "Void").Sum(d => d.Balance),
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
    return Results.Ok(new { id = doc.Id });
});

app.MapPut("/api/documents/{id:int}", async (AppDbContext db, int id, SaveInvoiceDocumentRequest request) =>
{
    var doc = await UpdateInvoiceDocumentAsync(db, id, request);
    return doc is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(new { id = doc.Id });
});

app.MapDelete("/api/documents/{id:int}", async (AppDbContext db, int id) =>
{
    var doc = await db.InvoiceDocuments.FindAsync(id);
    if (doc is null)
    {
        return Results.NotFound(new { message = $"Document {id} was not found." });
    }

    db.InvoiceDocuments.Remove(doc);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = id });
});

app.MapPost("/api/documents/{id:int}/duplicate", async (AppDbContext db, int id) =>
{
    var copy = await DuplicateInvoiceDocumentAsync(db, id, null);
    return copy is null ? Results.NotFound(new { message = $"Document {id} was not found." }) : Results.Ok(ToInvoiceDocumentDto(copy));
});

app.MapGet("/api/database/backup", async (AppDbContext db, IWebHostEnvironment env) =>
{
    var backup = await CreateDatabaseBackupAsync(db, env);
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

    return Results.Ok(new
    {
        totalEstimates = all.Count(d => d.DocType == "ESTIMATE"),
        totalInvoices = all.Count(d => d.DocType == "INVOICE"),
        totalRevenue = all.Where(d => d.DocType == "INVOICE").Sum(d => d.Total),
        paidRevenue = all.Where(d => d.DocType == "INVOICE" && d.Status == "Paid").Sum(d => d.Total),
        unpaidBalance = all.Where(d => d.DocType == "INVOICE" && d.Status != "Paid" && d.Status != "Void").Sum(d => d.Balance),
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
    var doc = await db.InvoiceDocuments.FindAsync(id);
    if (doc is null)
    {
        return Results.NotFound(new { message = $"Document {id} was not found." });
    }

    db.InvoiceDocuments.Remove(doc);
    await db.SaveChangesAsync();
    return Results.Ok(new { deleted = id });
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
        Balance = source.Total,
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

app.MapGet("/api/export/{entity}", async (string entity, AppDbContext db) =>
{
    var fileName = $"epata-{entity}-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
    return entity.ToLowerInvariant() switch
    {
        "parties" or "customers" => Csv(fileName, CsvExportService.ToCsv(await db.Parties.AsNoTracking().ToListAsync())),
        "sales" => Csv(fileName, CsvExportService.ToCsv(await db.Sales.AsNoTracking().ToListAsync())),
        "customer-jobs" or "jobs" => Csv(fileName, CsvExportService.ToCsv(await db.CustomerJobs.AsNoTracking().ToListAsync())),
        "receivable-invoices" or "invoices" => Csv(fileName, CsvExportService.ToCsv(await db.ReceivableInvoices.AsNoTracking().ToListAsync())),
        "bills" => Csv(fileName, CsvExportService.ToCsv(await db.Bills.AsNoTracking().ToListAsync())),
        "expenses" => Csv(fileName, CsvExportService.ToCsv(await db.Expenses.AsNoTracking().ToListAsync())),
        "products" => Csv(fileName, CsvExportService.ToCsv(await db.Products.AsNoTracking().ToListAsync())),
        "assets" => Csv(fileName, CsvExportService.ToCsv(await db.Assets.AsNoTracking().ToListAsync())),
        "makerworld-rewards" => Csv(fileName, CsvExportService.ToCsv(await db.MakerWorldRewards.AsNoTracking().ToListAsync())),
        "audit-documents" => Csv(fileName, CsvExportService.ToCsv(await db.AuditDocuments.AsNoTracking().ToListAsync())),
        "business-accounts" => Csv(fileName, CsvExportService.ToCsv(await db.BusinessAccounts.AsNoTracking().ToListAsync())),
        "action-items" => Csv(fileName, CsvExportService.ToCsv(await db.ActionItems.AsNoTracking().ToListAsync())),
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
        dbPath = config.GetConnectionString("DefaultConnection")
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
        Balance = source.Total,
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

static async Task<(byte[] Bytes, string FileName)> CreateDatabaseBackupAsync(AppDbContext db, IWebHostEnvironment env)
{
    await db.Database.CloseConnectionAsync();
    var dbPath = Path.Combine(env.ContentRootPath, "Data", "epata-business-ledger.db");
    if (!System.IO.File.Exists(dbPath))
    {
        return ([], "epata-business-ledger-missing.db");
    }

    var bytes = await System.IO.File.ReadAllBytesAsync(dbPath);
    return (bytes, $"epata-business-ledger-{DateTime.Now:yyyyMMdd-HHmmss}.db");
}

static string GetDatabasePath(IConfiguration configuration, IWebHostEnvironment env)
{
    var connectionString = configuration.GetConnectionString("DefaultConnection") ?? "Data Source=Data/epata-business-ledger.db";
    var builder = new SqliteConnectionStringBuilder(connectionString);
    var dataSource = builder.DataSource;
    return Path.IsPathRooted(dataSource) ? dataSource : Path.Combine(env.ContentRootPath, dataSource);
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
        job.AmountPaid = doc.AmountPaid;
        job.InvoiceAmount = null;
        job.NeedsReview = false;
    }
    else if (doc.DocType == "INVOICE")
    {
        var invoice = await db.ReceivableInvoices.FirstOrDefaultAsync(x => x.InvoiceNumber == doc.DocNumber);
        if (invoice is null)
        {
            invoice = new ReceivableInvoice
            {
                InvoiceNumber = doc.DocNumber,
                SourceProof = $"Unified invoice {doc.DocNumber}",
                Notes = "Created from the unified estimate/invoice workspace."
            };
            db.ReceivableInvoices.Add(invoice);
        }

        invoice.InvoiceDate = ParseDate(doc.DocDate) ?? invoice.InvoiceDate;
        invoice.DueDate = ParseDate(doc.DueDate) ?? invoice.DueDate;
        invoice.CustomerName = doc.CustomerName ?? invoice.CustomerName;
        invoice.ProjectName = doc.ProjectName ?? invoice.ProjectName;
        invoice.Status = NormalizeInvoiceStatus(doc.Status, doc.Total, doc.AmountPaid);
        invoice.Subtotal = doc.Subtotal;
        invoice.Discount = doc.DiscountAmount;
        invoice.RushFee = doc.RushAmount;
        invoice.TaxRatePercent = doc.CalcTaxRate;
        invoice.SalesTax = doc.TaxAmount;
        invoice.InvoiceTotal = doc.Total;
        invoice.AmountPaid = doc.AmountPaid;
        invoice.IncludeInCashReports = doc.AmountPaid > 0;
        invoice.NeedsReview = doc.AmountPaid < doc.Total && !invoice.Status.Equals("Void", StringComparison.OrdinalIgnoreCase);

        await SyncPaidInvoiceToSaleAsync(db, doc);
    }

    await db.SaveChangesAsync();
}

static async Task SyncPaidInvoiceToSaleAsync(AppDbContext db, InvoiceDocument doc)
{
    if (doc.AmountPaid <= 0 || doc.Status.Equals("Void", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    var sale = await db.Sales.FirstOrDefaultAsync(x => x.InvoiceNumber == doc.DocNumber && x.Platform == "Direct");
    if (sale is null)
    {
        sale = new Sale
        {
            Platform = "Direct",
            InvoiceNumber = doc.DocNumber,
            SourceProof = $"Unified invoice {doc.DocNumber}",
            IncludeInDashboard = true,
            Notes = "Created automatically from a paid unified invoice."
        };
        db.Sales.Add(sale);
    }

    sale.SaleDate = ParseDate(doc.DocDate) ?? sale.SaleDate ?? DateTime.Today;
    sale.CustomerName = doc.CustomerName ?? sale.CustomerName;
    sale.ProductName = doc.ProjectName ?? sale.ProductName;
    sale.Color = doc.Color ?? sale.Color;
    sale.Quantity = 1;
    sale.ItemSales = doc.Subtotal;
    sale.ShippingCharged = 0;
    sale.SalesTaxCollected = doc.TaxAmount;
    sale.CustomerPaid = doc.AmountPaid;
    sale.Status = doc.AmountPaid >= doc.Total ? "Paid" : "Paid";
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
