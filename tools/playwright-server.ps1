param(
    [int]$Port = 5120
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dbPath = Join-Path $root 'Data\playwright-browser-tests.db'

foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal")) {
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}

$env:ASPNETCORE_ENVIRONMENT = 'Test'

dotnet run `
    -c Release `
    --no-restore `
    --no-launch-profile `
    --project (Join-Path $root 'EPATA.BusinessLedger.csproj') `
    -- `
    "App:Url=http://127.0.0.1:$Port" `
    "ConnectionStrings:DefaultConnection=Data Source=Data\playwright-browser-tests.db" `
    'App:OpenBrowserOnStart=false'
