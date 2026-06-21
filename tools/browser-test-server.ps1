param(
    [ValidateSet('Start', 'Stop', 'Clean', 'Status')]
    [string]$Action = 'Start',
    [int]$Port = 5115
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$base = "http://127.0.0.1:$Port"
$dbPath = Join-Path $root 'Data\browser-round4.db'
$outPath = Join-Path $root 'browser-round4.out.log'
$errPath = Join-Path $root 'browser-round4.err.log'
$pidPath = Join-Path $root 'browser-round4.pid'
$dllPath = Join-Path $root 'obj\verify-build\EPATA.BusinessLedger.dll'

function Stop-BrowserRoundServer {
    if (Test-Path -LiteralPath $pidPath) {
        $processId = Get-Content -LiteralPath $pidPath -Raw
        if ($processId -match '^\d+$') {
            Stop-Process -Id ([int]$processId) -Force -ErrorAction SilentlyContinue
        }
    }
}

function Remove-BrowserRoundArtifacts {
    foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $outPath, $errPath, $pidPath)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

if ($Action -eq 'Stop') {
    Stop-BrowserRoundServer
    Remove-Item -LiteralPath $pidPath -Force -ErrorAction SilentlyContinue
    return
}

if ($Action -eq 'Clean') {
    Stop-BrowserRoundServer
    Remove-BrowserRoundArtifacts
    return
}

if ($Action -eq 'Status') {
    try {
        Invoke-RestMethod "$base/api/health" | ConvertTo-Json -Compress
    }
    catch {
        [pscustomobject]@{ status = 'offline'; url = $base; message = $_.Exception.Message } | ConvertTo-Json -Compress
    }
    return
}

Stop-BrowserRoundServer
Remove-BrowserRoundArtifacts

if (-not (Test-Path -LiteralPath $dllPath)) {
    throw "Build output not found at $dllPath. Run dotnet build to obj\verify-build first."
}

$arguments = @(
    "`"$dllPath`"",
    "App:Url=$base",
    "`"ConnectionStrings:DefaultConnection=Data Source=Data\browser-round4.db`"",
    'App:OpenBrowserOnStart=false'
)

$server = Start-Process -FilePath 'dotnet' `
    -ArgumentList $arguments `
    -WorkingDirectory $root `
    -WindowStyle Hidden `
    -RedirectStandardOutput $outPath `
    -RedirectStandardError $errPath `
    -PassThru

Set-Content -LiteralPath $pidPath -Value $server.Id -NoNewline

$health = $null
for ($i = 0; $i -lt 40; $i++) {
    try {
        $health = Invoke-RestMethod "$base/api/health"
        break
    }
    catch {
        Start-Sleep -Milliseconds 500
    }
}

if ($null -eq $health) {
    Stop-BrowserRoundServer
    throw "Browser test server did not become ready at $base."
}

if ($health.database -notlike '*browser-round4.db') {
    Stop-BrowserRoundServer
    throw "Browser test server is not using the disposable database. Reported: $($health.database)"
}

[pscustomobject]@{
    pid = $server.Id
    url = $base
    status = $health.status
    database = $health.database
} | ConvertTo-Json -Compress
