# Structured Log Review

Last reviewed: 2026-06-20, Round 97.

## Logs Reviewed

- `build-round97.log`
- `local-smoke-round97.log`
- `full-acceptance-round97.log`
- `launch-rehearsal-round97.log`
- `final-release-rehearsal-round97.log`

## Result

- Build: passed.
- Local smoke: passed against disposable `Data\local-smoke.db`.
- Full acceptance: passed against disposable `Data\full-acceptance.db`.
- Launch rehearsal: passed for `run.ps1`, `run.bat`, Release DLL, and published output.
- Final release rehearsal: passed for clean, medium, and copied-real-data DB profiles; production DB unchanged.
- Failed-save scenarios: covered by `FailedSaveStatusRound74`, unsafe estimate-to-invoice `PUT` returning 400 instead of 500, and full-acceptance clean failure-path checks.

## Findings

- Fixed in Round 97: existing-database launches previously logged benign duplicate-column `ALTER TABLE` attempts as EF `fail:` entries. Startup now checks SQLite `PRAGMA table_info` before running one-time column migrations.
- Current residual warning: `build-round97.log` still contains ASP.NET analyzer `AD0001` warnings from `Microsoft.AspNetCore.Analyzers.RouteHandlers.RouteHandlerAnalyzer`. The build exits successfully with 0 errors, and launch/smoke/acceptance/release rehearsals pass.
- Browser automation remains blocked by local tooling/runtime limits recorded in the checklist; browser-specific rows remain open and are not counted as passed by this review.
