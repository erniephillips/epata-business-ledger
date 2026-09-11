using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<CustomerJob> CustomerJobs => Set<CustomerJob>();
    public DbSet<CustomerCommunication> CustomerCommunications => Set<CustomerCommunication>();
    public DbSet<PrinterQueueItem> PrinterQueueItems => Set<PrinterQueueItem>();
    public DbSet<ReceivableInvoice> ReceivableInvoices => Set<ReceivableInvoice>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<OrderLossIncident> OrderLossIncidents => Set<OrderLossIncident>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<MakerWorldReward> MakerWorldRewards => Set<MakerWorldReward>();
    public DbSet<AuditDocument> AuditDocuments => Set<AuditDocument>();
    public DbSet<BusinessAccount> BusinessAccounts => Set<BusinessAccount>();
    public DbSet<ActionItem> ActionItems => Set<ActionItem>();
    public DbSet<TaxObligation> TaxObligations => Set<TaxObligation>();
    public DbSet<MileageLog> MileageLogs => Set<MileageLog>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<TaxYearSetup> TaxYearSetups => Set<TaxYearSetup>();
    public DbSet<InvoiceDocument> InvoiceDocuments => Set<InvoiceDocument>();
    public DbSet<InvoiceLineItem> InvoiceLineItems => Set<InvoiceLineItem>();
    public DbSet<InvoiceDocumentEvent> InvoiceDocumentEvents => Set<InvoiceDocumentEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Party>().HasIndex(x => x.Name);
        modelBuilder.Entity<Sale>().HasIndex(x => x.SaleDate);
        modelBuilder.Entity<Sale>().HasIndex(x => x.OrderNumber);
        modelBuilder.Entity<Sale>()
            .HasIndex(x => x.SourceReceivableInvoiceId)
            .IsUnique()
            .HasFilter("\"SourceReceivableInvoiceId\" IS NOT NULL");
        modelBuilder.Entity<Sale>()
            .HasOne(x => x.SourceReceivableInvoice)
            .WithMany()
            .HasForeignKey(x => x.SourceReceivableInvoiceId)
            .OnDelete(DeleteBehavior.Restrict);
        modelBuilder.Entity<CustomerJob>().HasIndex(x => x.CustomerName);
        modelBuilder.Entity<CustomerJob>().HasIndex(x => x.RelatedInvoiceNumber);
        modelBuilder.Entity<CustomerCommunication>().HasIndex(x => x.CustomerName);
        modelBuilder.Entity<CustomerCommunication>().HasIndex(x => x.OccurredAt);
        modelBuilder.Entity<PrinterQueueItem>().HasIndex(x => x.Status);
        modelBuilder.Entity<PrinterQueueItem>().HasIndex(x => x.PrinterName);
        modelBuilder.Entity<PrinterQueueItem>().HasIndex(x => x.CustomerJobId);
        modelBuilder.Entity<ReceivableInvoice>().HasIndex(x => x.InvoiceNumber).IsUnique(false);
        modelBuilder.Entity<Bill>().HasIndex(x => x.DueDate);
        modelBuilder.Entity<Expense>().HasIndex(x => x.ExpenseDate);
        modelBuilder.Entity<OrderLossIncident>().HasIndex(x => x.IncidentDate);
        modelBuilder.Entity<OrderLossIncident>().HasIndex(x => x.OrderNumber);
        modelBuilder.Entity<OrderLossIncident>().HasIndex(x => x.SaleId);
        modelBuilder.Entity<Product>().HasIndex(x => x.Sku).IsUnique(false);
        modelBuilder.Entity<AuditDocument>().HasIndex(x => x.RelatedRecordNumber);
        modelBuilder.Entity<AuditDocument>()
            .HasIndex(x => x.UploadFingerprint)
            .IsUnique()
            .HasFilter("\"UploadFingerprint\" IS NOT NULL AND \"IsArchived\" = 0");
        modelBuilder.Entity<TaxObligation>().HasIndex(x => x.DueDate);
        modelBuilder.Entity<TaxObligation>().HasIndex(x => new { x.TaxYear, x.Title, x.Period });
        modelBuilder.Entity<TaxYearSetup>().HasIndex(x => x.TaxYear).IsUnique();
        modelBuilder.Entity<MileageLog>().HasIndex(x => x.TripDate);
        modelBuilder.Entity<AppSetting>().HasIndex(x => x.Key).IsUnique();
        modelBuilder.Entity<InvoiceDocument>().HasIndex(x => x.DocNumber).IsUnique(false);
        modelBuilder.Entity<InvoiceDocument>()
            .HasIndex(x => x.SourceEstimateId)
            .IsUnique()
            .HasFilter("\"SourceEstimateId\" IS NOT NULL");
        modelBuilder.Entity<InvoiceDocument>().HasIndex(x => x.UpdatedAt);
        modelBuilder.Entity<InvoiceDocumentEvent>().HasIndex(x => x.InvoiceDocumentId);
        modelBuilder.Entity<InvoiceDocumentEvent>().HasIndex(x => x.CreatedAt);
        modelBuilder.Entity<InvoiceLineItem>()
            .HasOne(x => x.InvoiceDocument)
            .WithMany(x => x.LineItems)
            .HasForeignKey(x => x.InvoiceDocumentId)
            .OnDelete(DeleteBehavior.Cascade);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes()
                     .Where(entityType => typeof(AuditableEntity).IsAssignableFrom(entityType.ClrType)))
        {
            modelBuilder.Entity(entityType.ClrType)
                .Property(nameof(AuditableEntity.UpdatedAtUtc))
                .IsConcurrencyToken();
        }
        modelBuilder.Entity<InvoiceDocument>()
            .Property(document => document.UpdatedAt)
            .IsConcurrencyToken();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return base.SaveChangesAsync(cancellationToken);
    }

    public override int SaveChanges()
    {
        StampAuditFields();
        return base.SaveChanges();
    }

    private void StampAuditFields()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.UpdatedAtUtc = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
            }
        }
    }
}
