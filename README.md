# EPATA Business Ledger

A local-first .NET 10 + SQLite web app for EPATA 3D Prints. It is meant to replace the tedious spreadsheet workflow for day-to-day tracking.

## What it does

- Dashboard with gross receipts, estimated net, open AR, open AP, sales tax memo, and cleanup items
- Customer Jobs CRUD
- Sales / Income CRUD
- Accounts Receivable / Invoice Register CRUD
- Accounts Payable / Bills CRUD
- Expenses CRUD
- Customers & Vendors CRUD
- Products & Costing CRUD
- Assets / Equipment CRUD
- MakerWorld Rewards CRUD
- Audit Docs / Proof Index CRUD
- Business Accounts CRUD
- Action Items CRUD
- Built-in Invoice Center and full estimate/invoice builder with calculator, records, live preview, and PDF print/save flow
- CSV export for each area
- SQLite database backup button
- One-time old invoice-app import helper for migrating existing estimate/invoice records
- Info hover bubbles throughout the app

## Seeded starting data

The app seeds your current known entries from the uploaded PDFs:

- Charles Eke paid direct invoice: `INV-2026-0001`
- Ryan Lavallee open invoice: tracked as `INV-2026-0002` because the uploaded PDF reused `INV-2026-0001`
- Etsy orders:
  - `4013795986` Crystal Koplar
  - `4035148787` Diletta Mittone
  - `4054847709` Jennifer Zappone
  - `4057061093` Melissa McElfish
  - `4057880893` saheed arije

The Etsy rows are marked `Needs Review` because the Etsy order PDFs show customer totals and tax, but not actual Etsy fees, actual label costs, packaging cost, or COGS.

## How to run normally

Use the one-click Windows build:

```powershell
.\publish-win-x64.ps1
```

Then run:

```text
publish-win-x64\EPATA.BusinessLedger.exe
```

This is a self-contained Windows executable, so it does not need the .NET runtime installed on the machine that runs it. Keep the generated `publish-win-x64` folder together because the app also serves its `wwwroot` files and reads `appsettings.json` from that folder.

You can also double-click `run.bat`. If the published exe exists, it runs it. If not, it builds the exe first.

## Developer run

1. Install the .NET 10 SDK.
2. Open a terminal in this folder.
3. Run:

```powershell
dotnet restore
dotnet run
```

The app runs at:

```text
http://127.0.0.1:5062
```

The old invoice app is no longer part of the daily workflow. Invoice Center is built into this app now. If the old app is running, the Import / Export page and Invoice Center can use it only as a one-time migration source.

## Where the data is stored

The SQLite database is created here:

```text
Data/epata-business-ledger.db
```

Backups download from the UI and are also copied into:

```text
Backups/
```

## Important workflow

Use this app like this:

- Create estimates and invoices in **Invoice Center**.
- Use **Open Full Screen** or the embedded builder for the original calculator, estimate page, invoice page, records page, and customer-facing PDF preview/generator.
- Saved estimates automatically update **Customer Jobs** with status Quoted.
- Saved invoices automatically update **AR Invoices**.
- Use **Convert to Invoice** when an estimate is approved.
- When a unified invoice is marked paid with an Amount Paid, the matching **Direct Sale** is created or updated automatically.
- Customer asks for something manually: create a **Customer Job**.
- You send a direct invoice manually: create an **AR Invoice**.
- Customer pays: update the AR Invoice to Paid and create/confirm a **Sale** row.
- Etsy order comes in: create a **Sale** and **Customer Job**.
- You buy filament/supplies: create an **Expense** if already paid.
- You owe a vendor but have not paid: create an **AP Bill**.
- You save PDFs/receipts/screenshots: use **Document Intake** or **Attach File** on a proof field. That creates an **Audit Doc** and stores the local file path.

## Developer notes

This uses a simple local-first architecture:

- ASP.NET Core Minimal APIs
- Entity Framework Core with SQLite
- Vanilla HTML/CSS/JavaScript frontend
- No login/auth because it is designed for localhost use only
- `EnsureCreated` instead of migrations to make first-run simple

If this grows into a hosted multi-user app, add authentication, real EF migrations, server-side authorization, and proper accounting/tax review workflows.
