param(
    [int]$Port = 5221,
    [int]$RowsPerLedger = 250,
    [int]$DocumentCount = 500,
    [int]$ProofMetadataCount = 50
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$base = "http://127.0.0.1:$Port"
$dbPath = Join-Path $root 'Data\large-data-stress.db'
$dllPath = Join-Path $root 'obj\verify-build\EPATA.BusinessLedger.dll'
$outPath = Join-Path $root 'large-data-stress.out.log'
$errPath = Join-Path $root 'large-data-stress.err.log'
$server = $null

function Assert-Stress($condition, [string]$message) {
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

function Wait-StressHealth {
    $lastError = $null
    foreach ($attempt in 1..60) {
        try {
            $health = Invoke-RestMethod "$base/api/health"
            Assert-Stress ($health.database -like '*large-data-stress.db') "Stress app used wrong database: $($health.database)"
            return $health
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }

    $stderr = Get-Content -Raw $errPath -ErrorAction SilentlyContinue
    throw "Stress app did not become healthy. Last error: $lastError. stderr: $stderr"
}

function Invoke-StressJson([string]$method, [string]$path, $body) {
    return Invoke-RestMethod `
        -Method $method `
        -Uri "$base$path" `
        -ContentType 'application/json' `
        -Body ($body | ConvertTo-Json -Depth 20)
}

function Measure-StressEndpoint([string]$label, [string]$path, [int]$maxMs, [scriptblock]$assertion) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-RestMethod "$base$path"
    $watch.Stop()
    Assert-Stress ($watch.ElapsedMilliseconds -le $maxMs) "$label exceeded ${maxMs}ms: $($watch.ElapsedMilliseconds)ms"
    if ($assertion) {
        & $assertion $result
    }
    return [pscustomobject]@{
        Label = $label
        Path = $path
        Milliseconds = $watch.ElapsedMilliseconds
        LimitMilliseconds = $maxMs
    }
}

function New-StressDocumentPayload([int]$index) {
    $isInvoice = $index % 2 -eq 0
    $docType = if ($isInvoice) { 'INVOICE' } else { 'ESTIMATE' }
    $prefix = if ($isInvoice) { 'INV' } else { 'EST' }
    $number = '{0}-2026-{1:0000}' -f $prefix, (6000 + $index)
    $customer = 'StressCustomer-{0:0000}' -f $index
    $project = 'Stress Document Project {0:0000}' -f $index
    $rate = [decimal](20 + ($index % 40))
    return [ordered]@{
        docNumber = $number
        docType = $docType
        status = 'Draft'
        customerName = $customer
        customerPhone = '5551234567'
        customerAddress = "$index Stress Test Lane"
        customerEmail = "stress$index@example.test"
        preparedFor = $customer
        projectName = $project
        material = 'PLA'
        color = 'Black'
        infill = '20%'
        projectDescription = "Stress document description $index"
        projectNotes = "Stress private note $index"
        pageSize = 'Letter'
        docDate = '2026-06-20'
        dueDate = '2026-07-04'
        subtotal = 0
        discountAmount = 0
        rushAmount = 0
        taxAmount = 0
        total = 0
        amountPaid = 0
        balance = 0
        paymentMethod = 'Cash'
        pricingGuide = 'Stress pricing guide'
        termsNotes = 'Stress terms'
        standardTurnaround = '5 business days'
        rushTurnaround = '2 business days'
        calcGrams = 20
        calcHours = 1
        calcDesignHours = 0
        calcSetupFee = 0
        calcPostFee = 0
        calcGramRate = 0.05
        calcHourRate = 10
        calcDesignRate = 30
        calcMinimum = 0
        calcDifficulty = 1
        calcRush = 0
        calcDiscount = 0
        calcTaxRate = 0
        lineItems = @(@{
            sortOrder = 0
            description = $project
            details = 'Stress line item'
            quantity = 1
            rate = $rate
            amount = $rate
        })
        json = '{}'
    }
}

try {
    foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $outPath, $errPath)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }

    dotnet build (Join-Path $root 'EPATA.BusinessLedger.csproj') -c Release -o (Join-Path $root 'obj\verify-build') --no-restore /p:UseAppHost=false
    Assert-Stress ($LASTEXITCODE -eq 0) 'Stress verify build failed.'
    Assert-Stress (Test-Path -LiteralPath $dllPath) "Stress build output missing at $dllPath"

    Stop-PortOwner $Port
    $args = @(
        "`"$dllPath`"",
        "App:Url=$base",
        "`"ConnectionStrings:DefaultConnection=Data Source=Data\large-data-stress.db`"",
        'App:OpenBrowserOnStart=false'
    )
    $server = Start-Process -FilePath 'dotnet' `
        -ArgumentList $args `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outPath `
        -RedirectStandardError $errPath `
        -PassThru
    $health = Wait-StressHealth
    Assert-Stress ($health.status -eq 'ok') 'Stress health did not report ok.'

    $seedWatch = [System.Diagnostics.Stopwatch]::StartNew()
    foreach ($i in 1..$RowsPerLedger) {
        Invoke-StressJson 'POST' '/api/sales' ([ordered]@{
            saleDate = '2026-06-20'
            platform = if ($i % 2 -eq 0) { 'Direct' } else { 'Etsy' }
            paymentMethod = if ($i % 2 -eq 0) { 'Cash' } else { 'Etsy Payments' }
            salesTaxHandling = 'Seller collected and remitted'
            orderNumber = 'STRESS-SALE-{0:0000}' -f $i
            customerName = 'StressCustomer-{0:0000}' -f $i
            productName = 'Stress Widget'
            quantity = 1
            itemSales = 25
            shippingCharged = 5
            salesTaxCollected = 0
            customerPaid = 30
            platformFees = 0
            shippingLabelCost = 0
            refunds = 0
            estimatedCogs = 5
            status = 'Paid'
            includeInDashboard = $true
            needsReview = $false
            sourceProof = 'stress-sale.pdf'
            notes = 'Large data stress sale.'
        }) | Out-Null

        Invoke-StressJson 'POST' '/api/expenses' ([ordered]@{
            expenseDate = '2026-06-20'
            vendorName = 'Stress Vendor'
            category = 'Supplies'
            taxCategory = 'Operating Expense'
            description = 'Stress Expense {0:0000}' -f $i
            paymentMethod = 'Debit Card'
            amount = 12
            salesTax = 0
            total = 12
            taxBucket = 'Operating Expense'
            deductibleStatus = 'Yes'
            businessUsePercent = 100
            countedExpense = $true
            taxDeductible = $true
            needsReview = $false
            receiptProof = 'stress-expense.pdf'
            notes = 'Large data stress expense.'
        }) | Out-Null

        Invoke-StressJson 'POST' '/api/receivable-invoices' ([ordered]@{
            invoiceNumber = 'STRESS-AR-{0:0000}' -f $i
            invoiceDate = '2026-06-20'
            dueDate = '2026-07-20'
            customerName = 'StressCustomer-{0:0000}' -f $i
            projectName = 'Stress AR Project {0:0000}' -f $i
            status = if ($i % 3 -eq 0) { 'Paid' } else { 'Sent' }
            subtotal = 40
            discount = 0
            rushFee = 0
            taxRatePercent = 0
            salesTax = 0
            invoiceTotal = 40
            amountPaid = if ($i % 3 -eq 0) { 40 } else { 0 }
            paymentMethod = 'Check'
            sourceProof = 'stress-ar.pdf'
            includeInCashReports = $false
            needsReview = $false
            notes = 'Large data stress AR.'
        }) | Out-Null

        Invoke-StressJson 'POST' '/api/customer-jobs' ([ordered]@{
            jobDate = '2026-06-20'
            customerName = 'StressCustomer-{0:0000}' -f $i
            platform = 'Direct'
            jobNumber = 'STRESS-JOB-{0:0000}' -f $i
            jobName = 'Stress Job {0:0000}' -f $i
            jobType = 'Print'
            status = if ($i % 2 -eq 0) { 'Open' } else { 'Completed' }
            productName = 'Stress Widget'
            material = 'PLA'
            color = 'Black'
            description = 'Large data stress job.'
            quoteAmount = 40
            invoiceAmount = 40
            amountPaid = if ($i % 2 -eq 0) { 0 } else { 40 }
            paymentMethod = 'Cash'
            dueDate = '2026-07-20'
            sourceProof = 'stress-job.pdf'
            needsReview = $false
            notes = 'Large data stress job.'
        }) | Out-Null
    }

    foreach ($i in 1..$DocumentCount) {
        Invoke-StressJson 'POST' '/api/documents' (New-StressDocumentPayload $i) | Out-Null
    }
    foreach ($i in 1..$ProofMetadataCount) {
        Invoke-StressJson 'POST' '/api/audit-documents' ([ordered]@{
            documentDate = '2026-06-20'
            documentType = if ($i % 2 -eq 0) { 'Invoice' } else { 'Receipt' }
            relatedRecordType = if ($i % 2 -eq 0) { 'Invoice' } else { 'Sale' }
            relatedRecordNumber = if ($i % 2 -eq 0) { 'INV-STRESS-{0:0000}' -f $i } else { 'STRESS-SALE-{0:0000}' -f $i }
            fileName = 'stress-proof-{0:0000}.pdf' -f $i
            filePathOrUrl = 'UploadedDocs/stress-proof-{0:0000}.pdf' -f $i
            needsReview = $false
            notes = 'Representative uploaded proof metadata for large-data stress.'
        }) | Out-Null
    }
    $seedWatch.Stop()

    $timings = @()
    $timings += Measure-StressEndpoint 'Dashboard with 1,000 ledger rows' '/api/dashboard' 5000 {
        param($result)
        Assert-Stress ($result.kpis.grossReceipts -gt 0) 'Stress dashboard did not compute gross receipts.'
    }
    $timings += Measure-StressEndpoint 'Invoice records list with 500 documents' '/api/documents?includeArchived=true' 5000 {
        param($result)
        Assert-Stress (@($result).Count -ge $DocumentCount) "Stress documents list returned fewer than $DocumentCount rows."
    }
    $timings += Measure-StressEndpoint 'Invoice records search' '/api/documents?q=StressCustomer-0499&includeArchived=true' 2500 {
        param($result)
        Assert-Stress (@($result | Where-Object { $_.customerName -eq 'StressCustomer-0499' }).Count -ge 1) 'Stress document search did not find target customer.'
    }
    $timings += Measure-StressEndpoint 'Invoice records type/status filter' '/api/documents?type=INVOICE&status=Draft&includeArchived=true' 3000 {
        param($result)
        Assert-Stress (@($result).Count -ge [math]::Floor($DocumentCount / 2)) 'Stress invoice type/status filter returned too few invoices.'
    }
    $timings += Measure-StressEndpoint 'Sales records list' '/api/sales' 5000 {
        param($result)
        Assert-Stress (@($result).Count -ge $RowsPerLedger) "Stress sales list returned fewer than $RowsPerLedger rows."
    }
    $timings += Measure-StressEndpoint 'Lookup endpoint shape' '/api/lookups' 5000 {
        param($result)
        Assert-Stress ($null -ne $result.customers -and $null -ne $result.vendors -and $null -ne $result.products) 'Stress lookups response did not include expected collections.'
    }
    $timings += Measure-StressEndpoint 'Audit proof metadata list' '/api/audit-documents' 5000 {
        param($result)
        Assert-Stress (@($result).Count -ge $ProofMetadataCount) "Stress audit proof metadata list returned fewer than $ProofMetadataCount rows."
    }

    [pscustomobject]@{
        LargeDataStressRound89 = 'pass'
        Database = $health.database
        RowsPerLedger = $RowsPerLedger
        MixedLedgerRows = $RowsPerLedger * 4
        InvoiceEstimateDocuments = $DocumentCount
        ProofMetadataRows = $ProofMetadataCount
        SeedMilliseconds = $seedWatch.ElapsedMilliseconds
        Timings = $timings
    } | ConvertTo-Json -Depth 5
} finally {
    Stop-PortOwner $Port
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
}
