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
    await DbSeeder.SeedAsync(db, app.Configuration);
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

public sealed record ImportInvoiceRequest(string? BaseUrl);
