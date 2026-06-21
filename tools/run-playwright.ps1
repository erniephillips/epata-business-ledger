param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PlaywrightArgs
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$serverScript = Join-Path $PSScriptRoot 'playwright-server.ps1'
$Port = if ($env:EPATA_PLAYWRIGHT_PORT) { [int]$env:EPATA_PLAYWRIGHT_PORT } else { 5120 }
$objDir = Join-Path $root 'obj'
New-Item -ItemType Directory -Path $objDir -Force | Out-Null
$serverOutLog = Join-Path $root 'obj\playwright-server.out.log'
$serverErrLog = Join-Path $root 'obj\playwright-server.err.log'

if ($PlaywrightArgs.Count -gt 0 -and $PlaywrightArgs[0] -eq '--') {
    $PlaywrightArgs = @($PlaywrightArgs | Select-Object -Skip 1)
}

$existing = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue | Select-Object -First 1
if ($existing) {
    throw "Port $Port is already in use by process $($existing.OwningProcess)."
}

$serverArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$serverScript`" -Port $Port"

$server = Start-Process `
    -FilePath 'pwsh' `
    -ArgumentList $serverArgs `
    -WorkingDirectory $root `
    -WindowStyle Hidden `
    -RedirectStandardOutput $serverOutLog `
    -RedirectStandardError $serverErrLog `
    -PassThru

$exitCode = 1

try {
    $ready = $false
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1
        try {
            Invoke-RestMethod "http://127.0.0.1:$Port/api/health" -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        } catch { }
    }

    if (-not $ready) {
        Write-Host "Server stdout log: $serverOutLog"
        Write-Host "Server stderr log: $serverErrLog"
        throw "Playwright test server did not become healthy. PID $($server.Id)."
    }

    $env:EPATA_PLAYWRIGHT_PORT = [string]$Port
    $env:EPATA_PLAYWRIGHT_BASE_URL = "http://127.0.0.1:$Port"
    $env:EPATA_PLAYWRIGHT_SKIP_WEBSERVER = '1'

    Push-Location $root
    try {
        & npx playwright test @PlaywrightArgs
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
} finally {
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

exit $exitCode
