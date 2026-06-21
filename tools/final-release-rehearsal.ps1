param(
    [int]$BasePort = 5261
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dataDir = Join-Path $root 'Data'
$dllPath = Join-Path $root 'obj\verify-build\EPATA.BusinessLedger.dll'
$projectPath = Join-Path $root 'EPATA.BusinessLedger.csproj'
$productionDbPath = Join-Path $dataDir 'epata-business-ledger.db'
$releaseDbNames = @(
    'final-release-clean.db',
    'final-release-medium.db',
    'final-release-real-copy.db'
)

function Assert-Release($condition, [string]$message) {
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

function Remove-DisposableDatabase([string]$dbName) {
    Assert-Release ($dbName -like 'final-release-*.db') "Refusing to remove non-release database $dbName."
    foreach ($suffix in @('', '-wal', '-shm')) {
        $path = Join-Path $dataDir "$dbName$suffix"
        $fullPath = [System.IO.Path]::GetFullPath($path)
        $dataRoot = [System.IO.Path]::GetFullPath($dataDir)
        Assert-Release ($fullPath.StartsWith($dataRoot, [System.StringComparison]::OrdinalIgnoreCase)) "Refusing to remove path outside Data: $fullPath"
        Remove-Item -LiteralPath $fullPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-DatabaseFileState {
    $paths = @($productionDbPath, "$productionDbPath-wal", "$productionDbPath-shm")
    foreach ($path in $paths) {
        if (Test-Path -LiteralPath $path) {
            $item = Get-Item -LiteralPath $path
            $hash = $null
            try {
                $hash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
            } catch {
                $hash = $null
            }

            [pscustomobject]@{
                Path = [System.IO.Path]::GetFullPath($path)
                Exists = $true
                Length = $item.Length
                LastWriteTimeUtc = $item.LastWriteTimeUtc
                Hash = $hash
            }
        } else {
            [pscustomobject]@{
                Path = [System.IO.Path]::GetFullPath($path)
                Exists = $false
                Length = 0
                LastWriteTimeUtc = $null
                Hash = $null
            }
        }
    }
}

function Assert-ProductionUnchanged($before, $after) {
    for ($i = 0; $i -lt $before.Count; $i++) {
        $left = $before[$i]
        $right = $after[$i]
        Assert-Release ($left.Path -eq $right.Path) "Production database state order changed."
        Assert-Release ($left.Exists -eq $right.Exists) "Production database file existence changed: $($left.Path)"
        Assert-Release ($left.Length -eq $right.Length) "Production database file length changed: $($left.Path)"
        Assert-Release ($left.LastWriteTimeUtc -eq $right.LastWriteTimeUtc) "Production database timestamp changed: $($left.Path)"
        if ($left.Hash -and $right.Hash) {
            Assert-Release ($left.Hash -eq $right.Hash) "Production database hash changed: $($left.Path)"
        }
    }
}

function Copy-ProductionDatabaseToReleaseCopy {
    Assert-Release (Test-Path -LiteralPath $productionDbPath) "Production database was not found at $productionDbPath."
    $targetDbName = 'final-release-real-copy.db'
    Remove-DisposableDatabase $targetDbName

    foreach ($suffix in @('', '-wal', '-shm')) {
        $source = "$productionDbPath$suffix"
        if (Test-Path -LiteralPath $source) {
            $target = Join-Path $dataDir "$targetDbName$suffix"
            Copy-Item -LiteralPath $source -Destination $target -Force
        }
    }

    $copyPath = Join-Path $dataDir $targetDbName
    Assert-Release (Test-Path -LiteralPath $copyPath) "Copied real-data database was not created."
    $signature = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($copyPath), 0, 15)
    Assert-Release ($signature -eq 'SQLite format 3') "Copied real-data database is not a SQLite database."
}

function Wait-Health([string]$baseUrl, [string]$dbName, [string]$label, [string]$errPath) {
    $lastError = $null
    foreach ($attempt in 1..60) {
        try {
            $health = Invoke-RestMethod "$baseUrl/api/health"
            Assert-Release ($health.status -eq 'ok') "$label health did not report ok."
            Assert-Release ($health.database -like "*$dbName") "$label used the wrong database: $($health.database)"
            Assert-Release ($health.database -notlike '*epata-business-ledger.db') "$label targeted the production database."
            return $health
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }

    $stderr = Get-Content -Raw $errPath -ErrorAction SilentlyContinue
    throw "$label did not become healthy. Last error: $lastError. stderr: $stderr"
}

function Start-ReleaseApp([string]$label, [string]$dbName, [int]$port) {
    Stop-PortOwner $port
    $safeLabel = $label -replace '[^A-Za-z0-9_-]', '-'
    $outPath = Join-Path $root "final-release-$safeLabel.out.log"
    $errPath = Join-Path $root "final-release-$safeLabel.err.log"
    Remove-Item -LiteralPath $outPath, $errPath -Force -ErrorAction SilentlyContinue

    $baseUrl = "http://127.0.0.1:$port"
    $args = @(
        "`"$dllPath`"",
        "App:Url=$baseUrl",
        "`"ConnectionStrings:DefaultConnection=Data Source=Data\$dbName`"",
        'App:OpenBrowserOnStart=false'
    )
    $process = Start-Process -FilePath 'dotnet' `
        -ArgumentList $args `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outPath `
        -RedirectStandardError $errPath `
        -PassThru

    $health = Wait-Health $baseUrl $dbName $label $errPath
    [pscustomobject]@{
        Process = $process
        BaseUrl = $baseUrl
        Health = $health
        OutPath = $outPath
        ErrPath = $errPath
    }
}

function Stop-ReleaseApp($started, [int]$port) {
    Stop-PortOwner $port
    if ($started -and $started.Process -and -not $started.Process.HasExited) {
        Stop-Process -Id $started.Process.Id -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-Json([string]$method, [string]$baseUrl, [string]$path, $body = $null) {
    $uri = "$baseUrl$path"
    if ($null -eq $body) {
        return Invoke-RestMethod -Method $method -Uri $uri
    }

    return Invoke-RestMethod -Method $method -Uri $uri -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 20)
}

function Invoke-Raw([string]$method, [string]$baseUrl, [string]$path) {
    Invoke-WebRequest -Method $method -Uri "$baseUrl$path" -UseBasicParsing -SkipHttpErrorCheck
}

function New-ReleaseSale([string]$label, [int]$index) {
    [ordered]@{
        saleDate = '2026-06-20'
        platform = 'Direct'
        paymentMethod = 'Zelle'
        salesTaxHandling = 'Seller collected and remitted'
        orderNumber = "FINAL-$label-SALE-{0:0000}" -f $index
        customerName = "Final Release $label Customer"
        productName = "Final Release $label Widget"
        quantity = 1
        itemSales = 20 + $index
        shippingCharged = 0
        salesTaxCollected = 0
        customerPaid = 20 + $index
        platformFees = 0
        shippingLabelCost = 0
        refunds = 0
        estimatedCogs = 5
        status = 'Paid'
        includeInDashboard = $true
        needsReview = $false
        sourceProof = "final-release-$label.txt"
        notes = 'Final release rehearsal sentinel row.'
    }
}

function New-ReleaseExpense([string]$label, [int]$index) {
    [ordered]@{
        expenseDate = '2026-06-20'
        vendorName = "Final Release $label Vendor"
        category = 'Supplies'
        taxCategory = 'Materials and supplies'
        description = "Final Release $label Expense $index"
        paymentMethod = 'Debit Card'
        amount = 3 + $index
        salesTax = 0
        total = 3 + $index
        taxDeductible = $true
        countedExpense = $true
        deductibleStatus = 'Count'
        taxBucket = 'COGS / Materials'
        businessUsePercent = 100
        needsReview = $false
        receiptProof = "final-release-$label-expense.txt"
        notes = 'Final release rehearsal expense.'
    }
}

function New-ReleaseDocument([string]$label, [int]$index) {
    $total = 40 + $index
    [ordered]@{
        docNumber = "INV-2099-$label-{0:0000}" -f $index
        docType = 'INVOICE'
        status = 'Paid'
        customerName = "Final Release $label Customer"
        customerPhone = '973-555-2000'
        customerAddress = 'Final Release Lane'
        customerEmail = "final-release-$label@example.test"
        preparedFor = "Final Release $label Customer"
        projectName = "Final Release $label Invoice"
        material = 'PLA'
        color = 'Black'
        infill = '20%'
        projectDescription = 'Final release rehearsal invoice.'
        projectNotes = 'Created against a disposable or copied DB only.'
        pageSize = 'Letter'
        docDate = '2026-06-20'
        dueDate = '2026-07-04'
        subtotal = $total
        discountAmount = 0
        rushAmount = 0
        taxAmount = 0
        total = $total
        amountPaid = $total
        balance = 0
        paymentMethod = 'Zelle'
        pricingGuide = 'Final release rehearsal'
        termsNotes = 'Rehearsal terms'
        standardTurnaround = '5 days'
        rushTurnaround = '2 days'
        calcGrams = 10
        calcHours = 1
        calcDesignHours = 0
        calcSetupFee = 0
        calcPostFee = 0
        calcGramRate = 0.05
        calcHourRate = 3
        calcDesignRate = 25
        calcMinimum = 15
        calcDifficulty = 1
        calcRush = 0
        calcDiscount = 0
        calcTaxRate = 0
        lineItems = @(
            @{
                sortOrder = 0
                description = 'Final release print'
                details = 'Sentinel invoice line item'
                quantity = 1
                rate = $total
                amount = $total
            }
        )
        json = '{}'
    }
}

function Assert-DownloadLooksValid([string]$label, $response, [int]$minimumBytes) {
    Assert-Release ($response.StatusCode -eq 200) "$label returned HTTP $($response.StatusCode)."
    Assert-Release ($response.RawContentLength -ge $minimumBytes) "$label looked too small: $($response.RawContentLength) bytes."
}

function Invoke-ReleaseProfile([string]$label, [string]$dbName, [int]$port, [int]$extraRows) {
    $started = Start-ReleaseApp $label $dbName $port
    try {
        $baseUrl = $started.BaseUrl
        $dashboardBefore = Invoke-Json GET $baseUrl '/api/dashboard'
        $lookups = Invoke-Json GET $baseUrl '/api/lookups'
        Assert-Release ($null -ne $dashboardBefore) "$label dashboard did not return JSON."
        Assert-Release ($null -ne $lookups) "$label lookups did not return JSON."

        $createdSales = @()
        $createdExpenses = @()
        for ($i = 1; $i -le [Math]::Max(1, $extraRows); $i++) {
            $createdSales += Invoke-Json POST $baseUrl '/api/sales' (New-ReleaseSale $label $i)
            if ($i -le [Math]::Max(1, [Math]::Min($extraRows, 12))) {
                $createdExpenses += Invoke-Json POST $baseUrl '/api/expenses' (New-ReleaseExpense $label $i)
            }
        }

        $createdDoc = Invoke-Json POST $baseUrl '/api/documents' (New-ReleaseDocument $label 1)
        Assert-Release ($createdDoc.docNumber -like "INV-2099-$label-*") "$label invoice sentinel was not saved."

        $salesList = Invoke-Json GET $baseUrl '/api/sales'
        $expenseList = Invoke-Json GET $baseUrl '/api/expenses'
        $documentList = Invoke-Json GET $baseUrl '/api/documents?includeArchived=true'
        Assert-Release ($null -ne $salesList) "$label sales list endpoint did not return JSON."
        Assert-Release ($null -ne $expenseList) "$label expenses list endpoint did not return JSON."
        Assert-Release ($null -ne $documentList) "$label documents list endpoint did not return JSON."

        foreach ($createdSale in $createdSales) {
            $readSale = Invoke-Json GET $baseUrl "/api/sales/$($createdSale.id)"
            Assert-Release ($readSale.orderNumber -eq $createdSale.orderNumber) "$label sale $($createdSale.id) readback changed order number."
            Assert-Release ($readSale.paymentMethod -eq 'Zelle') "$label sale $($createdSale.id) readback changed payment method."
        }

        foreach ($createdExpense in $createdExpenses) {
            $readExpense = Invoke-Json GET $baseUrl "/api/expenses/$($createdExpense.id)"
            Assert-Release ($readExpense.description -eq $createdExpense.description) "$label expense $($createdExpense.id) readback changed description."
            Assert-Release ($readExpense.paymentMethod -eq 'Debit Card') "$label expense $($createdExpense.id) readback changed payment method."
        }

        $readDoc = Invoke-Json GET $baseUrl "/api/documents/$($createdDoc.id)"
        Assert-Release ($readDoc.docNumber -eq $createdDoc.docNumber) "$label invoice document readback changed document number."
        Assert-Release ($readDoc.paymentMethod -eq 'Zelle') "$label invoice document readback changed payment method."

        $taxSummary = Invoke-Json GET $baseUrl '/api/tax-summary?year=2026'
        $jobTimeline = @(Invoke-Json GET $baseUrl "/api/job-timeline?q=Final%20Release%20$label")
        Assert-Release ($null -ne $taxSummary) "$label tax summary did not return JSON."
        Assert-Release ($jobTimeline.Count -gt 0) "$label job timeline did not include sentinel rows."

        Assert-DownloadLooksValid "$label sales export" (Invoke-Raw GET $baseUrl '/api/export/sales?includeArchived=true') 100
        Assert-DownloadLooksValid "$label database backup" (Invoke-Raw POST $baseUrl '/api/system/backup') 100

        [pscustomobject]@{
            Label = $label
            Database = $started.Health.database
            SalesCreated = $createdSales.Count
            ExpensesCreated = $createdExpenses.Count
            InvoiceDocument = $createdDoc.docNumber
            Result = 'pass'
        }
    } finally {
        Stop-ReleaseApp $started $port
    }
}

New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
foreach ($dbName in $releaseDbNames) {
    if ($dbName -ne 'final-release-real-copy.db') {
        Remove-DisposableDatabase $dbName
    }
}

$productionBefore = @(Get-DatabaseFileState)
Copy-ProductionDatabaseToReleaseCopy

dotnet build $projectPath -c Release -o (Join-Path $root 'obj\verify-build') --no-restore /p:UseAppHost=false
Assert-Release ($LASTEXITCODE -eq 0) 'Final release rehearsal build failed.'
Assert-Release (Test-Path -LiteralPath $dllPath) "Build output missing at $dllPath"

$results = @()
$results += Invoke-ReleaseProfile 'clean' 'final-release-clean.db' $BasePort 1
$results += Invoke-ReleaseProfile 'medium' 'final-release-medium.db' ($BasePort + 1) 24
$results += Invoke-ReleaseProfile 'realcopy' 'final-release-real-copy.db' ($BasePort + 2) 1

$productionAfter = @(Get-DatabaseFileState)
Assert-ProductionUnchanged $productionBefore $productionAfter

[pscustomobject]@{
    FinalReleaseRehearsalRound96 = 'pass'
    ProductionDatabaseUntouched = 'pass'
    Profiles = $results
} | ConvertTo-Json -Depth 6
