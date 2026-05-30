using EPATA.BusinessLedger.Models;
using Microsoft.EntityFrameworkCore;

namespace EPATA.BusinessLedger.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(AppDbContext db, IConfiguration configuration)
    {
        if (await db.AppSettings.AnyAsync(x => x.Key == "SeedVersion"))
        {
            return;
        }

        var invoiceAppUrl = configuration["App:InvoiceAppUrl"] ?? "http://localhost:5057/";

        db.BusinessAccounts.AddRange(
            new BusinessAccount { Name = "Etsy Payments", AccountType = "Etsy", Institution = "Etsy", CurrentBalance = 0, Notes = "Use this for Etsy payouts and fees once you start reconciling monthly statements." },
            new BusinessAccount { Name = "Cash / Direct Payments", AccountType = "Cash", CurrentBalance = 0, Notes = "Use this for local/direct jobs paid by cash, Venmo, Zelle, check, etc." },
            new BusinessAccount { Name = "MakerWorld Gift Cards", AccountType = "Gift Card", Institution = "Bambu Lab / MakerWorld", CurrentBalance = 0, Notes = "Track gift cards or rewards used to buy supplies." }
        );

        db.Parties.AddRange(
            new Party { Name = "Charles Eke", PartyType = "Customer", City = "Landing", State = "NJ", DefaultPlatform = "Direct", Notes = "Console Cover direct invoice customer." },
            new Party { Name = "Ryan Lavallee", PartyType = "Customer", Email = "ryan@lavallees.net", DefaultPlatform = "Direct", Notes = "Pontoon boat tiller head fitting invoice customer." },
            new Party { Name = "Crystal Koplar", PartyType = "Customer", Address1 = "17 Sucker Brook Rd", City = "Goshen", State = "CT", PostalCode = "06756", EtsyUsername = "crystalkoplar", DefaultPlatform = "Etsy" },
            new Party { Name = "Diletta Mittone", PartyType = "Customer", Address1 = "13417 Northwest 5th Court", City = "Plantation", State = "FL", PostalCode = "33325", EtsyUsername = "ibdp0ky38db4kvoe", DefaultPlatform = "Etsy" },
            new Party { Name = "Jennifer Zappone", PartyType = "Customer", Address1 = "314 Flamingo Drive", City = "Lakeland", State = "FL", PostalCode = "33803", EtsyUsername = "ch6tlz2azl15dxbs", DefaultPlatform = "Etsy" },
            new Party { Name = "Melissa McElfish", PartyType = "Customer", Address1 = "9524 Simonsville Rd", City = "Midlothian", State = "VA", PostalCode = "23112", EtsyUsername = "msegrl104", DefaultPlatform = "Etsy" },
            new Party { Name = "saheed arije", PartyType = "Customer", Address1 = "655 Albin Ave", City = "West Babylon", State = "NY", PostalCode = "11704-7401", EtsyUsername = "02mfkhcmxs9396hl", DefaultPlatform = "Etsy" },
            new Party { Name = "Bambu Lab", PartyType = "Vendor", DefaultPlatform = "Vendor", Notes = "Printer, parts, filament, gift-card purchases." },
            new Party { Name = "USPS", PartyType = "Vendor", DefaultPlatform = "Vendor", Notes = "Shipping labels and postage." },
            new Party { Name = "Etsy", PartyType = "Vendor", DefaultPlatform = "Etsy", Notes = "Marketplace fees, labels, ads, and tax memo source." }
        );

        db.Products.AddRange(
            new Product { Name = "Stove Knob Guard - Samsung Front Control Range Protector", Sku = "FKG-SET4", Category = "3D Printed Product", Material = "PETG/ABS", Color = "Black/White", TargetPrice = 40, NeedsReview = true, Notes = "Seeded from Etsy orders. Add actual grams, print time, packaging, and label assumptions." },
            new Product { Name = "Stove Knob Guard - Set of 5", Sku = "FKG-SET5", Category = "3D Printed Product", Material = "PETG/ABS", Color = "Black", TargetPrice = 50, NeedsReview = true, Notes = "Set of 5 variant from Melissa order." },
            new Product { Name = "Console Cover", Category = "Custom Print", Material = "ABS", Color = "Black", Grams = 250, MaterialCostPerGram = 0.05m, PrintHours = 3, MachineRatePerHour = 15, TargetPrice = 80.04m, Notes = "Charles Eke direct job." },
            new Product { Name = "Pontoon Boat Tiller Head Fitting", Category = "Custom Print", Material = "ABS", Color = "Black", Grams = 250, MaterialCostPerGram = 0.05m, PrintHours = 4, MachineRatePerHour = 15, TargetPrice = 118.35m, Notes = "Ryan Lavallee direct job; unpaid as seeded." }
        );

        db.Sales.AddRange(
            new Sale { SaleDate = new DateTime(2026, 3, 28), Platform = "Etsy", OrderNumber = "4013795986", CustomerName = "Crystal Koplar", ProductName = "Stove Knob Guard for Samsung Front Control Range", Sku = "FKG-SET4", Variation = "Set of 4", Color = "", Quantity = 1, ItemSales = 40, ShippingCharged = 5.79m, SalesTaxCollected = 2.91m, CustomerPaid = 48.70m, Status = "Paid", SourceProof = "download-2026-03-30.pdf", TrackingNumber = "9400109206094596830814", ShipByDate = new DateTime(2026, 3, 30), NeedsReview = true, Notes = "Actual Etsy fees, label cost, packaging, and COGS still need to be entered." },
            new Sale { SaleDate = new DateTime(2026, 4, 17), Platform = "Etsy", OrderNumber = "4035148787", CustomerName = "Diletta Mittone", ProductName = "3D Printed Stove Knob Guard - Samsung Front Control Range Protector", Sku = "FKG-SET4", Variation = "Set of 4", Color = "Black", Quantity = 1, ItemSales = 40, ShippingCharged = 6.09m, SalesTaxCollected = 3.23m, CustomerPaid = 49.32m, Status = "Paid", SourceProof = "download-2026-04-18.pdf", TrackingNumber = "9400109206094614171691", ShipByDate = new DateTime(2026, 4, 20), NeedsReview = true, Notes = "Actual Etsy fees, label cost, packaging, and COGS still need to be entered." },
            new Sale { SaleDate = new DateTime(2026, 5, 6), Platform = "Etsy", OrderNumber = "4054847709", CustomerName = "Jennifer Zappone", ProductName = "3D Printed Stove Knob Guard - Samsung Front Control Range Protector", Sku = "FKG-SET4", Variation = "Set of 4", Color = "Black", Quantity = 1, ItemSales = 40, ShippingCharged = 6.37m, SalesTaxCollected = 3.25m, CustomerPaid = 49.62m, Status = "Paid", SourceProof = "download-2026-05-06.pdf", TrackingNumber = "9400109206094026982304", ShipByDate = new DateTime(2026, 5, 8), NeedsReview = true, Notes = "Actual Etsy fees, label cost, packaging, and COGS still need to be entered." },
            new Sale { SaleDate = new DateTime(2026, 5, 8), Platform = "Etsy", OrderNumber = "4057061093", CustomerName = "Melissa McElfish", ProductName = "3D Printed Stove Knob Guard - Samsung Front Control Range Protector", Sku = "FKG-SET5", Variation = "Set of 5", Color = "Black", Quantity = 1, ItemSales = 50, ShippingCharged = 6.17m, SalesTaxCollected = 3.00m, CustomerPaid = 59.17m, Status = "Paid", SourceProof = "download-2026-05-08.pdf", ShipByDate = new DateTime(2026, 5, 11), NeedsReview = true, Notes = "Actual Etsy fees, label cost, packaging, and COGS still need to be entered." },
            new Sale { SaleDate = new DateTime(2026, 5, 9), Platform = "Etsy", OrderNumber = "4057880893", CustomerName = "saheed arije", ProductName = "3D Printed Stove Knob Guard - Samsung Front Control Range Protector", Sku = "FKG-SET4", Variation = "Set of 4", Color = "White", Quantity = 1, ItemSales = 40, ShippingCharged = 6.10m, SalesTaxCollected = 4.03m, CustomerPaid = 50.13m, Status = "Paid", SourceProof = "download-2026-05-09.pdf", TrackingNumber = "9400109206094636742503", ShipByDate = new DateTime(2026, 5, 11), NeedsReview = true, Notes = "Actual Etsy fees, label cost, packaging, and COGS still need to be entered." },
            new Sale { SaleDate = new DateTime(2026, 5, 9), Platform = "Direct", InvoiceNumber = "INV-2026-0001", CustomerName = "Charles Eke", ProductName = "Console Cover", Sku = "CUSTOM-CONSOLE-COVER", Variation = "Custom ABS print", Color = "Black", Quantity = 1, ItemSales = 80.04m, ShippingCharged = 0, SalesTaxCollected = 0, CustomerPaid = 80.04m, EstimatedCogs = 12.50m, Status = "Paid", SourceProof = "INV-2026-0001 - EPATA 3D Prints(1).pdf", NeedsReview = false, Notes = "Paid direct invoice." }
        );

        db.CustomerJobs.AddRange(
            new CustomerJob { JobDate = new DateTime(2026, 3, 28), CustomerName = "Crystal Koplar", Platform = "Etsy", RelatedOrderNumber = "4013795986", JobName = "Samsung stove knob guard order", JobType = "Print", Status = "Paid", ProductName = "Stove Knob Guard", Material = "PETG/ABS", QuoteAmount = 48.70m, InvoiceAmount = 48.70m, AmountPaid = 48.70m, ShipByDate = new DateTime(2026, 3, 30), SourceProof = "download-2026-03-30.pdf", NeedsReview = true, Notes = "Financial costs still need Etsy fees/label/COGS." },
            new CustomerJob { JobDate = new DateTime(2026, 4, 17), CustomerName = "Diletta Mittone", Platform = "Etsy", RelatedOrderNumber = "4035148787", JobName = "Samsung stove knob guard order", JobType = "Print", Status = "Paid", ProductName = "Stove Knob Guard", Material = "PETG/ABS", Color = "Black", QuoteAmount = 49.32m, InvoiceAmount = 49.32m, AmountPaid = 49.32m, ShipByDate = new DateTime(2026, 4, 20), SourceProof = "download-2026-04-18.pdf", NeedsReview = true, Notes = "Financial costs still need Etsy fees/label/COGS." },
            new CustomerJob { JobDate = new DateTime(2026, 5, 6), CustomerName = "Jennifer Zappone", Platform = "Etsy", RelatedOrderNumber = "4054847709", JobName = "Samsung stove knob guard order", JobType = "Print", Status = "Paid", ProductName = "Stove Knob Guard", Material = "PETG/ABS", Color = "Black", QuoteAmount = 49.62m, InvoiceAmount = 49.62m, AmountPaid = 49.62m, ShipByDate = new DateTime(2026, 5, 8), SourceProof = "download-2026-05-06.pdf", NeedsReview = true, Notes = "Financial costs still need Etsy fees/label/COGS." },
            new CustomerJob { JobDate = new DateTime(2026, 5, 8), CustomerName = "Melissa McElfish", Platform = "Etsy", RelatedOrderNumber = "4057061093", JobName = "Samsung stove knob guard set of 5 order", JobType = "Print", Status = "Paid", ProductName = "Stove Knob Guard - Set of 5", Material = "PETG/ABS", Color = "Black", QuoteAmount = 59.17m, InvoiceAmount = 59.17m, AmountPaid = 59.17m, ShipByDate = new DateTime(2026, 5, 11), SourceProof = "download-2026-05-08.pdf", NeedsReview = true, Notes = "Financial costs still need Etsy fees/label/COGS." },
            new CustomerJob { JobDate = new DateTime(2026, 5, 9), CustomerName = "saheed arije", Platform = "Etsy", RelatedOrderNumber = "4057880893", JobName = "Samsung stove knob guard white order", JobType = "Print", Status = "Paid", ProductName = "Stove Knob Guard", Material = "PETG/ABS", Color = "White", QuoteAmount = 50.13m, InvoiceAmount = 50.13m, AmountPaid = 50.13m, ShipByDate = new DateTime(2026, 5, 11), SourceProof = "download-2026-05-09.pdf", NeedsReview = true, Notes = "Financial costs still need Etsy fees/label/COGS." },
            new CustomerJob { JobDate = new DateTime(2026, 5, 9), CustomerName = "Charles Eke", Platform = "Direct", RelatedInvoiceNumber = "INV-2026-0001", JobName = "Console Cover", JobType = "Print", Status = "Paid", ProductName = "Console Cover", Material = "ABS", Color = "Black", Description = "Customer requested a console cover with cutouts for joystick knobs.", QuoteAmount = 80.04m, InvoiceAmount = 80.04m, AmountPaid = 80.04m, SourceProof = "INV-2026-0001 - EPATA 3D Prints(1).pdf" },
            new CustomerJob { JobDate = new DateTime(2026, 5, 21), CustomerName = "Ryan Lavallee", Platform = "Direct", RelatedInvoiceNumber = "INV-2026-0002", JobName = "Pontoon Boat Tiller Head Fitting", JobType = "Print + Design", Status = "Invoiced", ProductName = "Pontoon Boat Tiller Head Fitting", Material = "ABS", Color = "Black", Description = "Custom 3D printed replacement tiller head fitting for pontoon boat use.", QuoteAmount = 118.35m, InvoiceAmount = 118.35m, AmountPaid = 0, DueDate = new DateTime(2026, 6, 4), SourceProof = "INV-2026-0001 - EPATA 3D Prints(2).pdf", NeedsReview = true, Notes = "Open AR; invoice number corrected in tracker to avoid duplicate INV-2026-0001." }
        );

        db.ReceivableInvoices.AddRange(
            new ReceivableInvoice { InvoiceNumber = "INV-2026-0001", OriginalInvoiceNumber = "INV-2026-0001", InvoiceDate = new DateTime(2026, 5, 9), DueDate = new DateTime(2026, 4, 28), CustomerName = "Charles Eke", ProjectName = "Console Cover", Status = "Paid", Subtotal = 80.04m, Discount = 0, RushFee = 0, TaxRatePercent = 0, SalesTax = 0, InvoiceTotal = 80.04m, AmountPaid = 80.04m, SourceProof = "INV-2026-0001 - EPATA 3D Prints(1).pdf", ExternalInvoiceAppUrl = invoiceAppUrl, IncludeInCashReports = true, Notes = "Seeded from uploaded paid PDF." },
            new ReceivableInvoice { InvoiceNumber = "INV-2026-0002", OriginalInvoiceNumber = "INV-2026-0001", InvoiceDate = new DateTime(2026, 5, 21), DueDate = new DateTime(2026, 6, 4), CustomerName = "Ryan Lavallee", ProjectName = "Pontoon Boat Tiller Head Fitting", Status = "Sent", Subtotal = 111, Discount = 0, RushFee = 0, TaxRatePercent = 6.625m, SalesTax = 7.35m, InvoiceTotal = 118.35m, AmountPaid = 0, SourceProof = "INV-2026-0001 - EPATA 3D Prints(2).pdf", ExternalInvoiceAppUrl = invoiceAppUrl, IncludeInCashReports = false, NeedsReview = true, Notes = "PDF reused INV-2026-0001. Tracker uses INV-2026-0002 to avoid duplicate invoice numbers. Do not count as income until paid." }
        );

        db.AuditDocuments.AddRange(
            new AuditDocument { DocumentDate = new DateTime(2026, 5, 9), DocumentType = "Invoice", RelatedRecordType = "ReceivableInvoice", RelatedRecordNumber = "INV-2026-0001", FileName = "INV-2026-0001 - EPATA 3D Prints(1).pdf", Notes = "Charles Eke paid invoice." },
            new AuditDocument { DocumentDate = new DateTime(2026, 5, 21), DocumentType = "Invoice", RelatedRecordType = "ReceivableInvoice", RelatedRecordNumber = "INV-2026-0002", FileName = "INV-2026-0001 - EPATA 3D Prints(2).pdf", NeedsReview = true, Notes = "Ryan Lavallee unpaid invoice; original PDF number duplicated." },
            new AuditDocument { DocumentDate = new DateTime(2026, 3, 28), DocumentType = "Etsy Order", RelatedRecordType = "Sale", RelatedRecordNumber = "4013795986", FileName = "download-2026-03-30.pdf" },
            new AuditDocument { DocumentDate = new DateTime(2026, 4, 17), DocumentType = "Etsy Order", RelatedRecordType = "Sale", RelatedRecordNumber = "4035148787", FileName = "download-2026-04-18.pdf" },
            new AuditDocument { DocumentDate = new DateTime(2026, 5, 6), DocumentType = "Etsy Order", RelatedRecordType = "Sale", RelatedRecordNumber = "4054847709", FileName = "download-2026-05-06.pdf" },
            new AuditDocument { DocumentDate = new DateTime(2026, 5, 8), DocumentType = "Etsy Order", RelatedRecordType = "Sale", RelatedRecordNumber = "4057061093", FileName = "download-2026-05-08.pdf" },
            new AuditDocument { DocumentDate = new DateTime(2026, 5, 9), DocumentType = "Etsy Order", RelatedRecordType = "Sale", RelatedRecordNumber = "4057880893", FileName = "download-2026-05-09.pdf" }
        );

        db.ActionItems.AddRange(
            new ActionItem { Title = "Enter actual Etsy platform fees for seeded orders", Area = "Sales", Priority = "High", Status = "Open", RelatedRecord = "Etsy seeded orders", Notes = "The Etsy order PDFs show customer totals and tax, but not marketplace fees. Add fees from Etsy payment statements for accurate profit." },
            new ActionItem { Title = "Enter actual shipping label costs and packaging costs", Area = "Sales", Priority = "High", Status = "Open", RelatedRecord = "Etsy seeded orders", Notes = "Customer shipping charged is not the same thing as your actual postage/label cost." },
            new ActionItem { Title = "Confirm Ryan invoice number was corrected", Area = "Invoice", Priority = "High", Status = "Open", RelatedRecord = "INV-2026-0002", Notes = "Uploaded Ryan PDF displayed INV-2026-0001. This app stores it as INV-2026-0002 to avoid duplicate invoice numbers." },
            new ActionItem { Title = "Add starting supplies and filament inventory expenses", Area = "AP", Priority = "Normal", Status = "Open", Notes = "Use Expenses for already-paid purchases and Bills for unpaid vendor invoices." }
        );

        db.AppSettings.AddRange(
            new AppSetting { Key = "SeedVersion", Value = "2026-05-30-001", Notes = "Initial seed based on uploaded EPATA PDFs and tracker structure." },
            new AppSetting { Key = "NextInvoiceNumber", Value = "INV-2026-0003", Notes = "Charles is 0001. Ryan is tracked as 0002 because the uploaded Ryan PDF reused 0001." },
            new AppSetting { Key = "InvoiceAppUrl", Value = invoiceAppUrl, Notes = "Existing local invoice app link." }
        );

        await db.SaveChangesAsync();
    }
}
