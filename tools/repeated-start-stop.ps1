param(
    [int]$Port = 5251,
    [int]$Cycles = 5
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$base = "http://127.0.0.1:$Port"
$dbPath = Join-Path $root 'Data\repeated-start-stop.db'
$dllPath = Join-Path $root 'obj\verify-build\EPATA.BusinessLedger.dll'
$outPath = Join-Path $root 'repeated-start-stop.out.log'
$errPath = Join-Path $root 'repeated-start-stop.err.log'

function Assert-Restart($condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

function Get-PortOwners([int]$port) {
    @(Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique |
        Where-Object { $_ -gt 0 })
}

function Stop-PortOwner([int]$port) {
    foreach ($owner in Get-PortOwners $port) {
        Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
}

function Wait-Health([string]$label) {
    $lastError = $null
    foreach ($attempt in 1..60) {
        try {
            $health = Invoke-RestMethod "$base/api/health"
            Assert-Restart ($health.status -eq 'ok') "$label health did not report ok."
            Assert-Restart ($health.database -like '*repeated-start-stop.db') "$label used wrong database: $($health.database)"
            return $health
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }

    $stderr = Get-Content -Raw $errPath -ErrorAction SilentlyContinue
    throw "$label did not become healthy. Last error: $lastError. stderr: $stderr"
}

function New-RestartSale([int]$cycle) {
    [ordered]@{
        saleDate = '2026-06-20'
        platform = 'Direct'
        paymentMethod = 'Cash'
        salesTaxHandling = 'Seller collected and remitted'
        orderNumber = 'ROUND92-RESTART-{0:0000}' -f $cycle
        customerName = 'Round92 Restart Customer'
        productName = 'Round92 Restart Widget'
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
        sourceProof = 'round92-restart.pdf'
        notes = 'Repeated start/stop persistence smoke.'
    }
}

foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $outPath, $errPath)) {
    Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
}

dotnet build (Join-Path $root 'EPATA.BusinessLedger.csproj') -c Release -o (Join-Path $root 'obj\verify-build') --no-restore /p:UseAppHost=false
Assert-Restart ($LASTEXITCODE -eq 0) 'Repeated start/stop verify build failed.'
Assert-Restart (Test-Path -LiteralPath $dllPath) "Repeated start/stop build output missing at $dllPath"

$results = @()
Stop-PortOwner $Port

for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
    Remove-Item -LiteralPath $outPath, $errPath -Force -ErrorAction SilentlyContinue
    $args = @(
        "`"$dllPath`"",
        "App:Url=$base",
        "`"ConnectionStrings:DefaultConnection=Data Source=Data\repeated-start-stop.db`"",
        'App:OpenBrowserOnStart=false'
    )
    $process = Start-Process -FilePath 'dotnet' `
        -ArgumentList $args `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outPath `
        -RedirectStandardError $errPath `
        -PassThru
    try {
        $health = Wait-Health "Cycle $cycle"
        if ($cycle -eq 1) {
            Invoke-RestMethod `
                -Method POST `
                -Uri "$base/api/sales" `
                -ContentType 'application/json' `
                -Body ((New-RestartSale $cycle) | ConvertTo-Json -Depth 20) | Out-Null
        } else {
            $sales = @(Invoke-RestMethod "$base/api/sales")
            Assert-Restart (@($sales | Where-Object { $_.orderNumber -eq 'ROUND92-RESTART-0001' }).Count -eq 1) "Cycle $cycle did not preserve the first sale after restart."
        }

        $results += [pscustomobject]@{
            Cycle = $cycle
            ProcessId = $process.Id
            Database = $health.database
            Result = 'started'
        }
    } finally {
        Stop-PortOwner $Port
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }

    Assert-Restart ((Get-PortOwners $Port).Count -eq 0) "Cycle $cycle left port $Port owned by a process."
}

$finalOwners = Get-PortOwners $Port
Assert-Restart ($finalOwners.Count -eq 0) "Repeated start/stop left orphan owners on port ${Port}: $($finalOwners -join ', ')"

[pscustomobject]@{
    RepeatedStartStopRound92 = 'pass'
    Port = $Port
    Cycles = $Cycles
    Database = $dbPath
    Results = $results
} | ConvertTo-Json -Depth 5
