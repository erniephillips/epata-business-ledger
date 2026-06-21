# Test Database Guard

Automated tests and rehearsals must not write to the real ledger database. Use this map before running scripts that create, update, archive, restore, import, export, upload, or back up records.

| Script | Intended database | Notes |
| --- | --- | --- |
| `tools/local-smoke.ps1` | `Data/local-smoke.db` | Full local smoke suite. Starts the app with `App:OpenBrowserOnStart=false` and cleans the disposable database, logs, and new backup/upload artifacts. |
| `tools/full-acceptance.ps1` | `Data/full-acceptance.db` | Full acceptance suite. Starts the app with `App:OpenBrowserOnStart=false` and cleans the disposable database, generated source files, logs, and new backup/upload artifacts. |
| `tools/browser-test-server.ps1` | `Data/browser-round4.db` | Disposable browser-test server. Starts the app with `App:OpenBrowserOnStart=false`; use `-Action Clean` after browser work. |
| `tools/playwright-server.ps1` | `Data/playwright-browser-tests.db` | Playwright browser automation server. Starts the app with `App:OpenBrowserOnStart=false`; Playwright owns the server process and uses the disposable database for browser specs. |
| `tools/slow-machine-simulation.ps1` | `Data/slow-machine-simulation.db` | Slow local-machine rehearsal. Starts the app with `App:OpenBrowserOnStart=false`, enables only test-configured `App:ArtificialLatencyMs`, verifies `/api/health` reports the disposable DB, and cleans the DB/log artifacts. |
| `tools/launch-rehearsal.ps1` | `Data/launch-rehearsal-run-ps1.db`, `Data/launch-rehearsal-run-bat.db`, `Data/launch-rehearsal-release-dll.db`, `Data/launch-rehearsal-published-dll.db` | Launch rehearsal for `run.ps1`, `run.bat`, Release DLL, and published output. Passes `App:OpenBrowserOnStart=false`, verifies `/api/health` reports the disposable DB before any write, and asserts the production DB is unchanged. |
| `run-test.bat` | `Data/epata-business-ledger-TEST.db` | Manual test-mode launcher using `ASPNETCORE_ENVIRONMENT=Test`. This is separate from the production ledger database. |

The production/default database (`Data/epata-business-ledger.db`) is allowed for normal app use only. Do not point automated destructive tests at it.

## Copied Real Data Manual Rehearsal

Before any manual test that could create, edit, archive, restore, import, export, upload, back up, or otherwise mutate copied real data:

1. Create a database backup from the app first.
2. Copy that backup database to a new file under `Data/` with a test-only name.
3. Launch the app with `ConnectionStrings:DefaultConnection` pointed at the copied file.
4. Confirm `/api/health` reports the copied database path before making any write.
5. Keep `Data/epata-business-ledger.db` closed to destructive or exploratory testing.

`tools/local-smoke.ps1` and `tools/full-acceptance.ps1` rehearse this rule with disposable databases by copying an app-created backup to `Data/local-smoke-manual-copy-rehearsal.db` or `Data/full-acceptance-manual-copy-rehearsal.db`, verifying the copy hash matches the backup, and deleting the rehearsal copy during cleanup.

The rehearsal runs as a preflight immediately after `/api/health` confirms the disposable database path and before admin, import, export, archive, restore, or clear-style checks run.

`tools/local-smoke.ps1` also runs a disposable restore drill: it creates an app backup, stops the app, removes only the disposable `Data/local-smoke.db` files, restores the downloaded backup to that same disposable path, restarts the app, and verifies record counts, invoice data, uploaded proof file access, and settings.
