using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Party> Parties => Set<Party>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<CustomerJob> CustomerJobs => Set<CustomerJob>();
    public DbSet<ReceivableInvoice> ReceivableInvoices => Set<ReceivableInvoice>();
    public DbSet<Bill> Bills => Set<Bill>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<MakerWorldReward> MakerWorldRewards => Set<MakerWorldReward>();
    public DbSet<AuditDocument> AuditDocuments => Set<AuditDocument>();
    public DbSet<BusinessAccount> BusinessAccounts => Set<BusinessAccount>();
    public DbSet<ActionItem> ActionItems => Set<ActionItem>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Party>().HasIndex(x => x.Name);
        modelBuilder.Entity<Sale>().HasIndex(x => x.SaleDate);
        modelBuilder.Entity<Sale>().HasIndex(x => x.OrderNumber);
        modelBuilder.Entity<CustomerJob>().HasIndex(x => x.CustomerName);
        modelBuilder.Entity<CustomerJob>().HasIndex(x => x.RelatedInvoiceNumber);
        modelBuilder.Entity<ReceivableInvoice>().HasIndex(x => x.InvoiceNumber).IsUnique(false);
        modelBuilder.Entity<Bill>().HasIndex(x => x.DueDate);
        modelBuilder.Entity<Expense>().HasIndex(x => x.ExpenseDate);
        modelBuilder.Entity<Product>().HasIndex(x => x.Sku).IsUnique(false);
        modelBuilder.Entity<AuditDocument>().HasIndex(x => x.RelatedRecordNumber);
        modelBuilder.Entity<AppSetting>().HasIndex(x => x.Key).IsUnique();
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
