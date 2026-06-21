param(
    [int]$Port = 5242
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$base = "http://127.0.0.1:$Port"
$dbPath = Join-Path $root 'Data\onedrive-boundary-smoke.db'
$objDir = Join-Path $root 'obj'
$outPath = Join-Path $objDir 'onedrive-boundary-smoke.out.log'
$errPath = Join-Path $objDir 'onedrive-boundary-smoke.err.log'
$backupPath = Join-Path $objDir 'onedrive-boundary-smoke-backup.db'
$server = $null

function Assert-OneDriveSmoke($condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

function Stop-PortOwner([int]$port) {
    $owners = @(Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    foreach ($owner in $owners) {
        if ($owner -gt 0) {
            Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue
        }
    }
}

function Start-OneDriveSmokeServer {
    Stop-PortOwner $Port
    $env:ASPNETCORE_ENVIRONMENT = 'Test'
    $projectPath = Join-Path $root 'EPATA.BusinessLedger.csproj'
    $args = @(
        'run',
        '-c',
        'Release',
        '--no-restore',
        '--no-launch-profile',
        '--project',
        "`"$projectPath`"",
        '--',
        "App:Url=$base",
        '"ConnectionStrings:DefaultConnection=Data Source=Data\onedrive-boundary-smoke.db"',
        'App:OpenBrowserOnStart=false'
    )
    return Start-Process -FilePath 'dotnet' `
        -ArgumentList $args `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outPath `
        -RedirectStandardError $errPath `
        -PassThru
}

function Wait-OneDriveSmokeHealth {
    $lastError = $null
    foreach ($attempt in 1..60) {
        try {
            $health = Invoke-RestMethod "$base/api/health"
            Assert-OneDriveSmoke ($health.status -eq 'ok') 'OneDrive boundary app health did not report ok.'
            Assert-OneDriveSmoke ($health.database -like '*onedrive-boundary-smoke.db') "OneDrive boundary app used wrong database: $($health.database)"
            return $health
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }

    $stderr = Get-Content -Raw $errPath -ErrorAction SilentlyContinue
    throw "OneDrive boundary smoke app did not become healthy. Last error: $lastError. stderr: $stderr"
}

function New-OneDriveSmokeSale {
    return [ordered]@{
        saleDate = '2026-06-21'
        platform = 'Direct'
        paymentMethod = 'Cash'
        salesTaxHandling = 'Seller collected and remitted'
        orderNumber = 'ONEDRIVE-BOUNDARY-ROUND115'
        customerName = 'OneDrive Boundary Customer'
        productName = 'OneDrive Boundary Widget'
        quantity = 1
        itemSales = 12
        shippingCharged = 0
        salesTaxCollected = 0
        customerPaid = 12
        platformFees = 0
        shippingLabelCost = 0
        refunds = 0
        estimatedCogs = 0
        status = 'Paid'
        includeInDashboard = $true
        needsReview = $false
        sourceProof = 'onedrive-boundary-proof.txt'
        notes = 'Disposable OneDrive-hosted boundary smoke.'
    }
}

try {
    New-Item -ItemType Directory -Path $objDir -Force | Out-Null
    $rootFull = [System.IO.Path]::GetFullPath($root)
    $dbFull = [System.IO.Path]::GetFullPath($dbPath)
    Assert-OneDriveSmoke ($rootFull -like '*OneDrive*') "Workspace is not under OneDrive, so this cannot rehearse the OneDrive-hosted local path. Root: $rootFull"
    Assert-OneDriveSmoke ($dbFull.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) "Disposable DB escaped workspace: $dbFull"
    Assert-OneDriveSmoke ($dbFull -notlike '*epata-business-ledger.db*') "Refusing to run against production database: $dbFull"

    foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $outPath, $errPath, $backupPath)) {
        $full = [System.IO.Path]::GetFullPath($path)
        if ($full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $full)) {
            Remove-Item -LiteralPath $full -Force
        }
    }

    $server = Start-OneDriveSmokeServer
    $health = Wait-OneDriveSmokeHealth
    $appInfo = Invoke-RestMethod "$base/api/app-info"
    Assert-OneDriveSmoke ($appInfo.dbPath -like '*onedrive-boundary-smoke.db') "App-info reported wrong DB path: $($appInfo.dbPath)"

    $created = Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/sales" `
        -ContentType 'application/json' `
        -Body ((New-OneDriveSmokeSale) | ConvertTo-Json -Depth 20)
    Assert-OneDriveSmoke ($created.id -gt 0) 'OneDrive boundary sale did not save.'

    Invoke-WebRequest `
        -Method POST `
        -Uri "$base/api/system/backup" `
        -UseBasicParsing `
        -OutFile $backupPath | Out-Null
    $signature = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($backupPath), 0, 15)
    Assert-OneDriveSmoke ($signature -eq 'SQLite format 3') "OneDrive boundary backup was not SQLite. Signature: $signature"

    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    $server = $null
    Start-Sleep -Milliseconds 500

    $server = Start-OneDriveSmokeServer
    [void](Wait-OneDriveSmokeHealth)
    $afterRestart = @(Invoke-RestMethod "$base/api/sales?includeArchived=true")
    Assert-OneDriveSmoke (@($afterRestart | Where-Object { $_.orderNumber -eq 'ONEDRIVE-BOUNDARY-ROUND115' }).Count -eq 1) 'OneDrive boundary restart did not preserve exactly one saved sale.'

    [pscustomobject]@{
        OneDriveBoundaryRound115 = 'pass'
        WorkspaceUnderOneDrive = $true
        DisposableDatabase = $dbFull
        AppReportedDatabase = $appInfo.dbPath
        BackupSignature = $signature
        RestartRoundTrip = 'pass'
        ProductionDatabaseTouched = $false
        PauseToggleNote = 'OS-level OneDrive pause/resume is not automated; this smoke verifies the app on the OneDrive-hosted path with disposable DB restart/backup behavior.'
        ExistingLockDelayCoverage = 'tools/sqlite-lock-smoke.ps1 covers OneDrive-style SQLite busy/locked delay recovery.'
        HealthDatabase = $health.database
        SaleId = $created.id
    } | ConvertTo-Json -Depth 4
} finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
    Stop-PortOwner $Port
}
