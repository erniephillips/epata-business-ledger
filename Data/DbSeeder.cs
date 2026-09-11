using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(
        AppDbContext db,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        _ = configuration;

        if (await db.AppSettings.AnyAsync(
                setting => setting.Key == "SeedVersion",
                cancellationToken))
        {
            return;
        }

        // A fresh installation starts with structure only. Customer, order, invoice,
        // asset, and reward data must come from that installation's own database or
        // explicit imports; real business records never belong in source-controlled seed data.
        if (!await db.BusinessAccounts.AnyAsync(cancellationToken))
        {
            db.BusinessAccounts.AddRange(
                new BusinessAccount
                {
                    Name = "Marketplace Clearing",
                    AccountType = "Online Marketplace",
                    CurrentBalance = 0,
                    Notes = "Use for marketplace payouts and fees after reconciling statements."
                },
                new BusinessAccount
                {
                    Name = "Cash / Direct Payments",
                    AccountType = "Cash",
                    CurrentBalance = 0,
                    Notes = "Use for direct customer payments."
                },
                new BusinessAccount
                {
                    Name = "Business Rewards",
                    AccountType = "Gift Card",
                    CurrentBalance = 0,
                    Notes = "Use for business rewards, credits, and gift cards."
                });
        }

        if (!await db.AppSettings.AnyAsync(
                setting => setting.Key == "LegacyInvoiceImport",
                cancellationToken))
        {
            db.AppSettings.Add(new AppSetting
            {
                Key = "LegacyInvoiceImport",
                Value = "Optional",
                Notes = "Use Invoice Center for new work. Legacy import is only for an explicit one-time migration."
            });
        }

        db.AppSettings.Add(new AppSetting
        {
            Key = "SeedVersion",
            Value = "2026-09-11-privacy-safe",
            Notes = "Initialized a privacy-safe empty ledger."
        });

        await db.SaveChangesAsync(cancellationToken);
    }

    public static async Task SeedPatchAsync(
        AppDbContext db,
        CancellationToken cancellationToken = default)
    {
        const string patchVersion = "2026-09-11-privacy-safe";
        if (await db.AppSettings.AnyAsync(
                setting => setting.Key == "SeedPatch" && setting.Value == patchVersion,
                cancellationToken))
        {
            return;
        }

        db.AppSettings.Add(new AppSetting
        {
            Key = "SeedPatch",
            Value = patchVersion,
            Notes = "Source-controlled seed data contains no customer or transaction records."
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
