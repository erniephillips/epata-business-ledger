@echo off
cd /d "%~dp0"
echo Starting EPATA Business Ledger in TEST mode...
echo Test DB: Data/epata-business-ledger-TEST.db
echo Port:    http://127.0.0.1:5063
echo.
echo This uses a separate database from production. Your real data is safe.
echo Close this window to stop the test instance.
echo.
set ASPNETCORE_ENVIRONMENT=Test
dotnet run --no-build -c Release --no-launch-profile --project "%~dp0EPATA.BusinessLedger.csproj"
pause
