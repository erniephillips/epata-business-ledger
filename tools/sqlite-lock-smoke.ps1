param(
    [int]$Port = 5241
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$base = "http://127.0.0.1:$Port"
$dbPath = Join-Path $root 'Data\sqlite-lock-smoke.db'
$dllPath = Join-Path $root 'obj\verify-build\EPATA.BusinessLedger.dll'
$outPath = Join-Path $root 'sqlite-lock-smoke.out.log'
$errPath = Join-Path $root 'sqlite-lock-smoke.err.log'
$server = $null
$lockConnection = $null
$lockTransaction = $null

function Assert-LockSmoke($condition, [string]$message) {
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

function Wait-LockSmokeHealth {
    $lastError = $null
    foreach ($attempt in 1..60) {
        try {
            $health = Invoke-RestMethod "$base/api/health"
            Assert-LockSmoke ($health.database -like '*sqlite-lock-smoke.db') "Lock smoke app used wrong database: $($health.database)"
            return $health
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }

    $stderr = Get-Content -Raw $errPath -ErrorAction SilentlyContinue
    throw "Lock smoke app did not become healthy. Last error: $lastError. stderr: $stderr"
}

function New-LockSmokeSale([string]$orderNumber) {
    return [ordered]@{
        saleDate = '2026-06-20'
        platform = 'Direct'
        paymentMethod = 'Cash'
        salesTaxHandling = 'Seller collected and remitted'
        orderNumber = $orderNumber
        customerName = 'Round91 Lock Customer'
        productName = 'Round91 Lock Widget'
        quantity = 1
        itemSales = 10
        shippingCharged = 0
        salesTaxCollected = 0
        customerPaid = 10
        platformFees = 0
        shippingLabelCost = 0
        refunds = 0
        estimatedCogs = 0
        status = 'Paid'
        includeInDashboard = $true
        needsReview = $false
        sourceProof = 'round91-lock.pdf'
        notes = 'SQLite locked-file smoke.'
    }
}

try {
    foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $outPath, $errPath)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }

    dotnet build (Join-Path $root 'EPATA.BusinessLedger.csproj') -c Release -o (Join-Path $root 'obj\verify-build') --no-restore /p:UseAppHost=false
    Assert-LockSmoke ($LASTEXITCODE -eq 0) 'SQLite lock smoke verify build failed.'
    Assert-LockSmoke (Test-Path -LiteralPath $dllPath) "SQLite lock smoke build output missing at $dllPath"

    Stop-PortOwner $Port
    $args = @(
        "`"$dllPath`"",
        "App:Url=$base",
        "`"ConnectionStrings:DefaultConnection=Data Source=Data\sqlite-lock-smoke.db;Default Timeout=1`"",
        'App:OpenBrowserOnStart=false'
    )
    $server = Start-Process -FilePath 'dotnet' `
        -ArgumentList $args `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outPath `
        -RedirectStandardError $errPath `
        -PassThru
    $health = Wait-LockSmokeHealth
    Assert-LockSmoke ($health.status -eq 'ok') 'SQLite lock smoke health did not report ok.'

    Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/sales" `
        -ContentType 'application/json' `
        -Body ((New-LockSmokeSale 'ROUND91-BEFORE-LOCK') | ConvertTo-Json -Depth 20) | Out-Null

    $nativeSqlitePath = Join-Path $root 'obj\verify-build\runtimes\win-x64\native'
    $env:PATH = "$nativeSqlitePath;$env:PATH"
    Add-Type -Path (Join-Path $root 'obj\verify-build\SQLitePCLRaw.core.dll')
    Add-Type -Path (Join-Path $root 'obj\verify-build\SQLitePCLRaw.provider.e_sqlite3.dll')
    Add-Type -Path (Join-Path $root 'obj\verify-build\SQLitePCLRaw.batteries_v2.dll')
    Add-Type -Path (Join-Path $root 'obj\verify-build\Microsoft.Data.Sqlite.dll')
    [SQLitePCL.Batteries_V2]::Init()
    $lockConnection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$dbPath;Default Timeout=1")
    $lockConnection.Open()
    $lockTransaction = $lockConnection.BeginTransaction()
    $lockCommand = $lockConnection.CreateCommand()
    $lockCommand.Transaction = $lockTransaction
    $lockCommand.CommandText = 'CREATE TABLE IF NOT EXISTS LockSmokeHold (Id INTEGER PRIMARY KEY); INSERT INTO LockSmokeHold DEFAULT VALUES;'
    [void]$lockCommand.ExecuteNonQuery()

    $lockedResponse = Invoke-WebRequest `
        -Method POST `
        -Uri "$base/api/sales" `
        -ContentType 'application/json' `
        -Body ((New-LockSmokeSale 'ROUND91-DURING-LOCK') | ConvertTo-Json -Depth 20) `
        -SkipHttpErrorCheck

    Assert-LockSmoke ($lockedResponse.StatusCode -eq 503) "SQLite locked-file save expected 503, got $($lockedResponse.StatusCode): $($lockedResponse.Content)"
    Assert-LockSmoke ($lockedResponse.Content -like '*SQLite database is busy or locked*') 'SQLite locked-file response did not include friendly retry guidance.'

    $lockTransaction.Rollback()
    $lockTransaction.Dispose()
    $lockTransaction = $null
    $lockConnection.Dispose()
    $lockConnection = $null

    $afterLock = Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/sales" `
        -ContentType 'application/json' `
        -Body ((New-LockSmokeSale 'ROUND91-AFTER-LOCK') | ConvertTo-Json -Depth 20)
    Assert-LockSmoke ($afterLock.id -gt 0) 'SQLite lock smoke did not recover after lock release.'

    $salesAfterRecovery = @(Invoke-RestMethod "$base/api/sales?includeArchived=true")
    Assert-LockSmoke (@($salesAfterRecovery | Where-Object { $_.orderNumber -eq 'ROUND91-BEFORE-LOCK' }).Count -eq 1) 'SQLite lock smoke lost the pre-lock sale.'
    Assert-LockSmoke (@($salesAfterRecovery | Where-Object { $_.orderNumber -eq 'ROUND91-DURING-LOCK' }).Count -eq 0) 'SQLite lock smoke created a duplicate/partial row for the failed locked write.'
    Assert-LockSmoke (@($salesAfterRecovery | Where-Object { $_.orderNumber -eq 'ROUND91-AFTER-LOCK' }).Count -eq 1) 'SQLite lock smoke did not persist exactly one recovery sale.'

    $integrityConnection = [Microsoft.Data.Sqlite.SqliteConnection]::new("Data Source=$dbPath;Mode=ReadOnly")
    try {
        $integrityConnection.Open()
        $integrityCommand = $integrityConnection.CreateCommand()
        $integrityCommand.CommandText = 'PRAGMA integrity_check;'
        $integrityResult = [string]$integrityCommand.ExecuteScalar()
        Assert-LockSmoke ($integrityResult -eq 'ok') "SQLite integrity_check failed after lock recovery: $integrityResult"
    } finally {
        $integrityConnection.Dispose()
    }

    [pscustomobject]@{
        SqliteLockRound91 = 'pass'
        SqliteOneDriveDelayRound107 = 'pass'
        NoDuplicateDuringLockRound107 = 'pass'
        Database = $health.database
        UnderOneDrivePath = ($dbPath -like '*OneDrive*')
        LockedStatus = $lockedResponse.StatusCode
        RecoverySaleId = $afterLock.id
        SalesAfterRecovery = $salesAfterRecovery.Count
    } | ConvertTo-Json -Depth 4
} finally {
    if ($lockTransaction) {
        $lockTransaction.Dispose()
    }
    if ($lockConnection) {
        $lockConnection.Dispose()
    }
    Stop-PortOwner $Port
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
