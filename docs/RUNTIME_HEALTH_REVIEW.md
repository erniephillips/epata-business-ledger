# Runtime Health Review

Last reviewed: 2026-06-20, Round 88.

## Runtime

- Target framework: `net10.0`.
- .NET SDK: `10.0.301`.
- .NET host/runtime: `10.0.9`, `win-x64`.
- Installed ASP.NET Core runtime used by the app: `Microsoft.AspNetCore.App 10.0.9`.

## Dependency Advisory Result

- Command: `dotnet list EPATA.BusinessLedger.csproj package --vulnerable --include-transitive --no-restore`.
- Result after Round 88: no vulnerable packages reported by NuGet advisory sources.
- Fixed finding: `SQLitePCLRaw.lib.e_sqlite3` resolved transitively to `2.1.11` and NuGet reported high-severity advisory `GHSA-2m69-gcr7-jv3q`.
- Fix applied: direct native package override to `SQLitePCLRaw.lib.e_sqlite3` `3.50.3`.
- Rationale: `3.50.3` is the native SQLite asset package only, so it removes the vulnerable native library without forcing a major managed SQLitePCLRaw API upgrade.

## Package Graph

Top-level packages after Round 88:

- `Microsoft.Data.Sqlite` `10.0.0`.
- `Microsoft.EntityFrameworkCore.Sqlite` `10.0.0`.
- `PdfPig` `0.1.14`.
- `SQLitePCLRaw.lib.e_sqlite3` `3.50.3`.

SQLitePCLRaw managed transitive packages remain at `2.1.11`; the vulnerable native `lib.e_sqlite3` package is overridden to `3.50.3`.

## Publish Output

- Launch rehearsal publish folder: `obj\launch-rehearsal-publish`.
- Published output size during review: 148 files, 48,833,318 bytes.
- Rehearsal launched published output with `App:OpenBrowserOnStart=false` and disposable DB `Data\launch-rehearsal-published-dll.db`.

## Startup And Build Notes

- `tools\launch-rehearsal.ps1` verified `run.ps1`, `run.bat`, Release DLL, and published output each report the expected disposable DB path from `/api/health`.
- Launch rehearsal stderr logs were empty after successful command quoting fixes.
- Build still emits `AD0001` from `Microsoft.AspNetCore.Analyzers.RouteHandlers.RouteHandlerAnalyzer`; this is an analyzer exception, not an app runtime failure. Keep watching it on future SDK updates.
