param(
    [int]$Port = 5111
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
Add-Type -AssemblyName System.Net.Http

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dbPath = Join-Path $root 'Data\full-acceptance.db'
$backupDownloadPath = Join-Path $root 'Data\full-acceptance-app-backup-rehearsal.db'
$manualCopyPath = Join-Path $root 'Data\full-acceptance-manual-copy-rehearsal.db'
$base = "http://127.0.0.1:$Port"
$project = Join-Path $root 'EPATA.BusinessLedger.csproj'
$builtDll = @(
    Join-Path $root 'bin\Release\net10.0-windows\EPATA.BusinessLedger.dll'
    Join-Path $root 'bin\Release\net10.0\EPATA.BusinessLedger.dll'
    Join-Path $root 'obj\verify-build\EPATA.BusinessLedger.dll'
) | Where-Object { Test-Path -LiteralPath $_ } | Sort-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } -Descending | Select-Object -First 1
$uploadProbe = Join-Path $root 'Data\acceptance-upload-proof.txt'
$aiDocxProbe = Join-Path $root 'Data\acceptance-ai-source.docx'
$aiPdfProbe = Join-Path $root 'Data\acceptance-ai-source.pdf'
$aiInvoicePdfProbe = Join-Path $root 'Data\acceptance-invoice-document.pdf'
$aiEstimatePdfProbe = Join-Path $root 'Data\acceptance-estimate-document.pdf'
$serverJob = $null
$backupDir = Join-Path $root 'Backups'
$backupFilesBefore = @()
$backupCopyPreflightCompleted = $false

function Write-Step($message) {
    Write-Host "[acceptance] $message"
}

function Assert-True($condition, $message) {
    if (-not $condition) {
        throw $message
    }
}

function Invoke-BackupCopyRehearsal([string]$label) {
    Remove-Item -LiteralPath $backupDownloadPath, $manualCopyPath -Force -ErrorAction SilentlyContinue

    $downloadedBackup = Invoke-WebRequest -Method POST -Uri "$base/api/system/backup" -UseBasicParsing -SkipHttpErrorCheck -OutFile $backupDownloadPath -PassThru
    Assert-True ($downloadedBackup.StatusCode -eq 200) "$label download failed."
    Assert-True (Test-Path -LiteralPath $backupDownloadPath) "$label download was not created."
    Assert-True ((Get-Item -LiteralPath $backupDownloadPath).Length -gt 100) "$label file looked empty."
    $downloadedBackupSignature = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($backupDownloadPath), 0, 15)
    Assert-True ($downloadedBackupSignature -eq 'SQLite format 3') "$label did not download a SQLite database. Signature: $downloadedBackupSignature"
    $dataRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'Data'))
    $dataRootWithSep = if ($dataRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $dataRoot
    } else {
        $dataRoot + [System.IO.Path]::DirectorySeparatorChar
    }
    $manualCopyFullPath = [System.IO.Path]::GetFullPath($manualCopyPath)
    $productionDbPath = [System.IO.Path]::GetFullPath((Join-Path $root ('Data\' + 'epata-business-ledger' + '.db')))
    Assert-True ($manualCopyFullPath.StartsWith($dataRootWithSep, [System.StringComparison]::OrdinalIgnoreCase)) "$label target escaped Data: $manualCopyFullPath"
    Assert-True (-not $manualCopyFullPath.Equals($productionDbPath, [System.StringComparison]::OrdinalIgnoreCase)) "$label target would overwrite the production database."
    Copy-Item -LiteralPath $backupDownloadPath -Destination $manualCopyPath -Force
    Assert-True (Test-Path -LiteralPath $manualCopyPath) "$label copy was not created."
    $backupHash = (Get-FileHash -LiteralPath $backupDownloadPath -Algorithm SHA256).Hash
    $copyHash = (Get-FileHash -LiteralPath $manualCopyPath -Algorithm SHA256).Hash
    Assert-True ($copyHash -eq $backupHash) "$label copy does not match the app-created backup."
}

function Assert-Close([decimal]$actual, [decimal]$expected, [decimal]$tolerance, $message) {
    if ([math]::Abs([decimal]$actual - [decimal]$expected) -gt $tolerance) {
        throw "$message Expected $expected, got $actual."
    }
}

function Assert-UploadedDocUnderRoot($doc, [string]$label) {
    $uploadRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'UploadedDocs'))
    $uploadRootWithSep = if ($uploadRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $uploadRoot
    } else {
        $uploadRoot + [System.IO.Path]::DirectorySeparatorChar
    }
    $fullPath = [System.IO.Path]::GetFullPath([string]$doc.filePathOrUrl)
    Assert-True ($fullPath.StartsWith($uploadRootWithSep, [System.StringComparison]::OrdinalIgnoreCase)) "$label escaped UploadedDocs: $fullPath"
    Assert-True (Test-Path -LiteralPath $fullPath) "$label was not saved under UploadedDocs."
    return $fullPath
}

function Invoke-Json($method, $path, $body = $null) {
    $uri = "$base$path"
    if ($null -eq $body) {
        return Invoke-RestMethod -Method $method -Uri $uri
    }

    return Invoke-RestMethod -Method $method -Uri $uri -ContentType 'application/json' -Body ($body | ConvertTo-Json -Depth 20)
}

function Invoke-Raw($method, $path) {
    $uri = "$base$path"
    return Invoke-WebRequest -Method $method -Uri $uri -UseBasicParsing -SkipHttpErrorCheck
}

function New-DocPayload($type, $status, $number, $paid, $customer) {
    @{
        docNumber = $number
        docType = $type
        status = $status
        customerName = $customer
        customerPhone = '973-555-1000'
        customerAddress = 'Acceptance Test Lane'
        customerEmail = "$($customer.Replace(' ', '.').ToLowerInvariant())@example.test"
        preparedFor = $customer
        projectName = "Acceptance $type"
        material = 'PLA'
        color = 'Black'
        infill = '20%'
        projectDescription = 'Acceptance workflow proof'
        projectNotes = 'Generated by full-acceptance.ps1'
        pageSize = 'A4'
        docDate = '2026-05-30'
        dueDate = '2026-06-13'
        subtotal = 999
        discountAmount = -25
        rushAmount = -4
        taxAmount = -8
        total = 1
        amountPaid = $paid
        balance = 12345
        paymentMethod = 'Zelle'
        pricingGuide = 'Acceptance'
        termsNotes = 'Acceptance terms'
        standardTurnaround = '5 days'
        rushTurnaround = '2 days'
        calcGrams = 10
        calcHours = 1
        calcDesignHours = 0.5
        calcSetupFee = 2
        calcPostFee = 3
        calcGramRate = 0.05
        calcHourRate = 3
        calcDesignRate = 25
        calcMinimum = 15
        calcDifficulty = 1
        calcRush = 0
        calcDiscount = 0
        calcTaxRate = 10
        lineItems = @(
            @{
                sortOrder = 0
                description = 'Widget print'
                details = 'Line item canonicalization'
                quantity = 2
                rate = 50
                amount = 999
            },
            @{
                sortOrder = 1
                description = 'Negative ignored'
                details = 'Should not reduce subtotal'
                quantity = -3
                rate = 99
                amount = -297
            }
        )
        json = '{}'
    }
}

function Assert-DocMoney($doc, $expectedPaid, $message) {
    Assert-Close ([decimal]$doc.subtotal) 100 0.01 "$message subtotal"
    Assert-Close ([decimal]$doc.discountAmount) 0 0.01 "$message discount"
    Assert-Close ([decimal]$doc.rushAmount) 0 0.01 "$message rush"
    Assert-Close ([decimal]$doc.taxAmount) 10 0.01 "$message tax"
    Assert-Close ([decimal]$doc.total) 110 0.01 "$message total"
    Assert-Close ([decimal]$doc.amountPaid) ([decimal]$expectedPaid) 0.01 "$message paid"
    Assert-Close ([decimal]$doc.balance) ([decimal](110 - $expectedPaid)) 0.01 "$message balance"
}

function Assert-EstimateMoney($doc, $message) {
    Assert-Close ([decimal]$doc.subtotal) 100 0.01 "$message subtotal"
    Assert-Close ([decimal]$doc.taxAmount) 10 0.01 "$message tax"
    Assert-Close ([decimal]$doc.total) 110 0.01 "$message total"
    Assert-Close ([decimal]$doc.amountPaid) 0 0.01 "$message estimate paid"
    Assert-Close ([decimal]$doc.balance) 0 0.01 "$message estimate balance"
}

function Assert-CrudRoundTrip($route, $payload, $updateField, $updatedValue) {
    Assert-True $backupCopyPreflightCompleted "Round 64 backup preflight did not run before $route CRUD/archive/restore checks."

    $created = Invoke-Json POST "/api/$route" $payload
    Assert-True ($created.id -gt 0) "$route create did not return an id."
    $fetched = Invoke-Json GET "/api/$route/$($created.id)"
    Assert-True ($fetched.id -eq $created.id) "$route fetch by id returned the wrong record."

    $updatedPayload = @{}
    foreach ($p in $created.PSObject.Properties) {
        $updatedPayload[$p.Name] = $p.Value
    }
    $updatedPayload[$updateField] = $updatedValue
    $updated = Invoke-Json PUT "/api/$route/$($created.id)" $updatedPayload
    Assert-True ($updated.$updateField -eq $updatedValue) "$route update did not persist $updateField."

    Invoke-Json DELETE "/api/$route/$($created.id)" | Out-Null
    $archived = Invoke-Json GET "/api/$route/$($created.id)"
    Assert-True ($archived.isArchived -eq $true) "$route delete did not archive the record."

    Invoke-Json POST "/api/$route/$($created.id)/restore" | Out-Null
    $restored = Invoke-Json GET "/api/$route/$($created.id)"
    Assert-True ($restored.isArchived -eq $false) "$route restore did not unarchive the record."
    return $restored
}

function Upload-ProofFile {
    Set-Content -LiteralPath $uploadProbe -Value 'Acceptance proof upload content' -NoNewline
    $client = [System.Net.Http.HttpClient]::new()
    $content = [System.Net.Http.MultipartFormDataContent]::new()
    $bytes = [System.IO.File]::ReadAllBytes($uploadProbe)
    $fileContent = [System.Net.Http.ByteArrayContent]::new($bytes)
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/plain')
    $content.Add($fileContent, 'file', 'acceptance-upload-proof.txt')
    $response = $client.PostAsync("$base/api/documents/upload", $content).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "Document upload failed with $($response.StatusCode): $text"
    }
    return $text | ConvertFrom-Json
}

function Upload-ProofTextFiles($files) {
    $client = [System.Net.Http.HttpClient]::new()
    $content = [System.Net.Http.MultipartFormDataContent]::new()
    try {
        foreach ($file in $files) {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes([string]$file.Text)
            $fileContent = [System.Net.Http.ByteArrayContent]::new($bytes)
            $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/plain')
            $content.Add($fileContent, 'files', [string]$file.FileName)
        }

        $response = $client.PostAsync("$base/api/documents/upload", $content).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $response.IsSuccessStatusCode) {
            throw "Document text upload failed with $($response.StatusCode): $text"
        }
        return $text | ConvertFrom-Json
    }
    finally {
        $content.Dispose()
        $client.Dispose()
    }
}

function Invoke-AiEstimateUpload {
    $client = [System.Net.Http.HttpClient]::new()
    $content = [System.Net.Http.MultipartFormDataContent]::new()
    $content.Add([System.Net.Http.StringContent]::new("- 2x Custom keychains - `$18 each`n- 1x Replacement part - `$27"), 'sourceText')
    $content.Add([System.Net.Http.StringContent]::new('acceptance mixed sources'), 'sourceName')
    $imageContent = [System.Net.Http.ByteArrayContent]::new([byte[]](1, 2, 3, 4))
    $imageContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('image/png')
    $content.Add($imageContent, 'files', 'reference-view.png')
    $response = $client.PostAsync("$base/api/ai/estimate-draft/upload", $content).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "AI estimate upload failed with $($response.StatusCode): $text"
    }
    return $text | ConvertFrom-Json
}

function New-AcceptanceDocx {
    param([string]$Path)
    Add-Type -AssemblyName System.IO.Compression
    $stream = [System.IO.File]::Open($Path, [System.IO.FileMode]::Create)
    try {
        $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            $entry = $archive.CreateEntry('word/document.xml')
            $writer = [System.IO.StreamWriter]::new($entry.Open())
            try {
                $writer.Write('<?xml version="1.0" encoding="UTF-8" standalone="yes"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Customer: Acceptance DOCX Customer</w:t></w:r></w:p><w:p><w:r><w:t>- 2x DOCX bracket - $31</w:t></w:r></w:p></w:body></w:document>')
            } finally {
                $writer.Dispose()
            }
        } finally {
            $archive.Dispose()
        }
    } finally {
        $stream.Dispose()
    }
}

function Invoke-AiEstimateDocumentUpload {
    New-AcceptanceDocx -Path $aiDocxProbe
    [System.IO.File]::WriteAllText($aiPdfProbe, '%PDF-1.4 BT (PDF custom sign request) Tj ET %%EOF', [System.Text.Encoding]::Latin1)
    $client = [System.Net.Http.HttpClient]::new()
    $content = [System.Net.Http.MultipartFormDataContent]::new()
    $content.Add([System.Net.Http.StringContent]::new('acceptance PDF and DOCX sources'), 'sourceName')
    foreach ($spec in @(
        @{ Path = $aiDocxProbe; Type = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'; Name = 'customer-request.docx' },
        @{ Path = $aiPdfProbe; Type = 'application/pdf'; Name = 'customer-reference.pdf' }
    )) {
        $fileContent = [System.Net.Http.ByteArrayContent]::new([System.IO.File]::ReadAllBytes($spec.Path))
        $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse($spec.Type)
        $content.Add($fileContent, 'files', $spec.Name)
    }
    $response = $client.PostAsync("$base/api/ai/estimate-draft/upload", $content).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "AI document estimate upload failed with $($response.StatusCode): $text"
    }
    return $text | ConvertFrom-Json
}

function Write-ProbePdfText {
    param(
        [string]$Path,
        [string]$Text
    )

    $escaped = $Text.Replace('\', '\\').Replace('(', '\(').Replace(')', '\)')
    [System.IO.File]::WriteAllText($Path, "%PDF-1.4 BT ($escaped) Tj ET %%EOF", [System.Text.Encoding]::Latin1)
}

function Invoke-AiInvoiceDocumentDraftUpload {
    Write-ProbePdfText -Path $aiInvoicePdfProbe -Text 'INVOICE Invoice # INV-2026-0099 Date June 12, 2026 Due Date June 19, 2026 Prepared For Acceptance PDF Customer Bill To Acceptance PDF Customer 973-555-0199 pdfcustomer@example.test 123 Import Ave Testville NJ 07001 Project Details Project Name: Imported Console Cover Description: Replacement console part Material: ABS Color: Black Infill: 40% Pricing Summary Subtotal $100.00 Discount -$5.00 Rush Fee (0%) $0.00 Tax (10%) $9.50 Balance Due $0.00 INVOICE Breakdown # Description Calculation / Details Qty Rate Amount 1 Console cover ABS print and finishing 1 $100.00 $100.00 Pricing Guide Print-Only Jobs - $15 minimum Terms & Notes - Payment is due by the due date shown above. Payment Status Status Paid Invoice Total $104.50 Amount Paid $104.50 Balance Due $0.00'
    $client = [System.Net.Http.HttpClient]::new()
    $content = [System.Net.Http.MultipartFormDataContent]::new()
    $content.Add([System.Net.Http.StringContent]::new('acceptance invoice PDF'), 'sourceName')
    $fileContent = [System.Net.Http.ByteArrayContent]::new([System.IO.File]::ReadAllBytes($aiInvoicePdfProbe))
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/pdf')
    $content.Add($fileContent, 'files', 'accepted-invoice.pdf')
    $response = $client.PostAsync("$base/api/ai/invoice-document-draft/upload", $content).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "AI invoice PDF draft upload failed with $($response.StatusCode): $text"
    }
    return $text | ConvertFrom-Json
}

function Invoke-AiEstimateDocumentDraftUpload {
    Write-ProbePdfText -Path $aiEstimatePdfProbe -Text 'ESTIMATE Estimate # EST-2026-0042 Date June 10, 2026 Valid Until June 24, 2026 Prepared For Acceptance Estimate Customer Bill To Acceptance Estimate Customer estimatecustomer@example.test Project Details Project Name: Imported Estimate Bracket Description: Quote for bracket replacement Material: PETG Color: Blue Infill: 20% Pricing Summary Subtotal $75.00 Discount -$0.00 Rush Fee (0%) $0.00 Tax (0%) $0.00 Estimated Total $75.00 ESTIMATE Breakdown # Description Calculation / Details Qty Rate Amount 1 Bracket replacement Printed PETG bracket 1 $75.00 $75.00 Terms & Notes - This estimate is valid for 14 days from the date above. Approval Status Sent Approved Total $75.00'
    $client = [System.Net.Http.HttpClient]::new()
    $content = [System.Net.Http.MultipartFormDataContent]::new()
    $content.Add([System.Net.Http.StringContent]::new('acceptance estimate PDF'), 'sourceName')
    $fileContent = [System.Net.Http.ByteArrayContent]::new([System.IO.File]::ReadAllBytes($aiEstimatePdfProbe))
    $fileContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/pdf')
    $content.Add($fileContent, 'files', 'accepted-estimate.pdf')
    $response = $client.PostAsync("$base/api/ai/invoice-document-draft/upload", $content).GetAwaiter().GetResult()
    $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) {
        throw "AI estimate PDF draft upload failed with $($response.StatusCode): $text"
    }
    return $text | ConvertFrom-Json
}

try {
    Write-Step 'Preparing disposable database'
    $backupFilesBefore = if (Test-Path -LiteralPath $backupDir) {
        @(Get-ChildItem -LiteralPath $backupDir -Filter 'epata-business-ledger-*.db' -File | ForEach-Object { $_.FullName })
    } else {
        @()
    }

    foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $backupDownloadPath, "$backupDownloadPath-shm", "$backupDownloadPath-wal", $manualCopyPath, "$manualCopyPath-shm", "$manualCopyPath-wal", $uploadProbe, $aiDocxProbe, $aiPdfProbe, $aiInvoicePdfProbe, $aiEstimatePdfProbe)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }

    Write-Step "Starting app on $base"
    $serverJob = Start-Job -ScriptBlock {
        param($root, $project, $builtDll, $port, $dbPath)
        Set-Location $root
        if (Test-Path -LiteralPath $builtDll) {
            dotnet $builtDll "App:Url=http://127.0.0.1:$port" "ConnectionStrings:DefaultConnection=Data Source=$dbPath" "App:OpenBrowserOnStart=false" "Ai:AllowHostedFallback=false"
        } else {
            dotnet run --no-build -c Release --no-launch-profile --project $project -- "App:Url=http://127.0.0.1:$port" "ConnectionStrings:DefaultConnection=Data Source=$dbPath" "App:OpenBrowserOnStart=false" "Ai:AllowHostedFallback=false"
        }
    } -ArgumentList $root, $project, $builtDll, $Port, $dbPath

    $ready = $false
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Milliseconds 750
        try {
            $health = Invoke-Json GET '/api/health'
            if ($health.status -eq 'ok') {
                $ready = $true
                break
            }
        } catch {
        }
    }
    if (-not $ready) {
        $jobOutput = Receive-Job -Job $serverJob -Keep
        throw "App did not become ready. Job output: $jobOutput"
    }
    Assert-True ($health.database -like '*full-acceptance.db') "Acceptance app is not using the disposable database. Reported: $($health.database)"
    Invoke-BackupCopyRehearsal 'Round 64 preflight backup-and-copy rehearsal'
    $backupCopyPreflightCompleted = $true

    Write-Step 'Checking static shells and core endpoints'
    foreach ($path in @('/', '/index.html', '/css/site.css', '/js/app.js', '/js/relationship-directory.js?v=3', '/js/toast-stack.js?v=1', '/js/modal-lifecycle.js?v=1', '/js/modal-save-state.js?v=1', '/js/entity-table-state.js?v=2', '/js/sidebar-state.js?v=1', '/js/navigation-history.js?v=1', '/js/global-search.js?v=1', '/js/quick-add.js?v=1', '/js/invoice-prefill.js?v=1', '/js/tax-prep-state.js?v=1', '/js/communication-state.js?v=1', '/js/job-workflow-state.js?v=1', '/invoice-builder/', '/invoice-builder/index.html', '/invoice-builder/css/app.css', '/invoice-builder/js/app.js', '/invoice-builder/js/builder.js', '/invoice-builder/js/records.js', '/invoice-builder/js/pdf.js')) {
        $response = Invoke-Raw GET $path
        Assert-True ($response.StatusCode -eq 200) "Static asset $path failed."
    }
    $appJs = (Invoke-Raw GET '/js/app.js').Content
    $relationshipDirectoryJs = (Invoke-Raw GET '/js/relationship-directory.js?v=3').Content
    $modalLifecycleJs = (Invoke-Raw GET '/js/modal-lifecycle.js?v=1').Content
    $modalSaveStateJs = (Invoke-Raw GET '/js/modal-save-state.js?v=1').Content
    $entityTableStateJs = (Invoke-Raw GET '/js/entity-table-state.js?v=2').Content
    $sidebarStateJs = (Invoke-Raw GET '/js/sidebar-state.js?v=1').Content
    $navigationHistoryJs = (Invoke-Raw GET '/js/navigation-history.js?v=1').Content
    $globalSearchJs = (Invoke-Raw GET '/js/global-search.js?v=1').Content
    $quickAddJs = (Invoke-Raw GET '/js/quick-add.js?v=1').Content
    $invoicePrefillJs = (Invoke-Raw GET '/js/invoice-prefill.js?v=1').Content
    $aiProductDraftJs = (Invoke-Raw GET '/js/ai-product-draft.js?v=1').Content
    $taxPrepStateJs = (Invoke-Raw GET '/js/tax-prep-state.js?v=1').Content
    $communicationStateJs = (Invoke-Raw GET '/js/communication-state.js?v=1').Content
    $jobWorkflowStateJs = (Invoke-Raw GET '/js/job-workflow-state.js?v=1').Content
    Assert-True ($appJs -match 'function assistanceIndicator') 'Assistance source indicator is missing from the app shell.'
    Assert-True ($appJs -match 'Automatic fallback while Local AI is unavailable') 'Local AI fallback indicator is missing.'
    Assert-True ($appJs -match 'defaultPaymentMethod' -and $appJs -match 'Etsy Payments' -and $appJs -match 'Wire Transfer') 'Payment method defaults/options are missing from the app shell.'
    Assert-True ($appJs -match 'relationshipOpenButton' -and $relationshipDirectoryJs -match 'relationshipLinkedRowTarget') 'Relationship detail row open routing is not using the shared helper.'
    Assert-True ($appJs -match 'customerDetailSectionPlan' -and $appJs -match 'vendorDetailSectionPlan' -and $appJs -match 'personContactTarget' -and $appJs -match 'relationshipSectionPage' -and $relationshipDirectoryJs -match 'customerDetailSectionPlan' -and $relationshipDirectoryJs -match 'vendorDetailSectionPlan' -and $relationshipDirectoryJs -match 'personContactTarget' -and $relationshipDirectoryJs -match 'relationshipSectionPage') 'Relationship detail/contact/pagination helpers are not wired into the main shell.'
Assert-True ($appJs -match 'nameStatus' -and $appJs -match 'duplicateNameWarning' -and $relationshipDirectoryJs -match 'Duplicate contacts' -and $relationshipDirectoryJs -match 'duplicateNameWarning' -and $relationshipDirectoryJs -match 'trimmed, case-insensitive name') 'Relationship duplicate-name status is not visible in the main shell.'
    Assert-True ($appJs -match 'Edit Audit Doc' -and $appJs -match 'openModal\(configs\.auditDocs') 'Document Intake is not wiring uploaded Audit Docs back to the Audit Doc edit modal.'
    Assert-True ($appJs -match 'EpataToastStack') 'Main shell is not using stacked toasts for visible save/error messages.'
    Assert-True ($appJs -match 'EpataModalLifecycle' -and $modalLifecycleJs -match 'openModalSurface' -and $modalLifecycleJs -match 'closeModalSurface') 'Main shell is not using modal lifecycle focus management.'
    Assert-True ($appJs -match 'EpataModalSaveState' -and $modalSaveStateJs -match 'closeModal: false' -and $modalSaveStateJs -match 'refreshPage: false' -and $modalSaveStateJs -match 'Save failed:') 'Main shell is not using explicit failed-save modal handling.'
    Assert-True ($appJs -match 'EpataEntityTableState' -and $entityTableStateJs -match 'buildEntityTableModel' -and $entityTableStateJs -match 'filterRows' -and $entityTableStateJs -match 'clampPage' -and $entityTableStateJs -match 'needs-review' -and $entityTableStateJs -match 'refunded' -and $appJs -match 'Refunded' -and $appJs -match 'Needs Review') 'Main shell is not using entity table search/filter/page helpers.'
    Assert-True ($appJs -match "columns:\s*\['name','sku','material','color','grams'") 'Product catalog table does not expose color with SKU and material.'
    Assert-True ($appJs -match "columns:\s*\['name','accountType','institution','last4','activeStatus','openingBalance','currentBalance'\]" -and $appJs -match "row\.isActive === false \? 'Inactive' : 'Active'") 'Business Accounts table does not expose explicit active/inactive status.'
    Assert-True ($appJs -match 'EpataSidebarState' -and $sidebarStateJs -match 'resolveSidebarCollapsed' -and $sidebarStateJs -match 'persistSidebarCollapsed' -and $sidebarStateJs -match 'sidebarExpandedState') 'Main shell is not using sidebar state persistence helpers.'
    Assert-True ($appJs -match 'EpataNavigationHistory' -and $navigationHistoryJs -match 'nextPageHistory' -and $navigationHistoryJs -match 'browserHistoryMode' -and $navigationHistoryJs -match 'pageFromStateOrHash') 'Main shell is not using navigation history helpers.'
    Assert-True ($appJs -match 'EpataGlobalSearch' -and $globalSearchJs -match 'searchRowsByConfig' -and $globalSearchJs -match 'resultOpenCall' -and $globalSearchJs -match 'rowMatchesQuery') 'Main shell is not using global search helpers.'
    Assert-True ($appJs -match 'EpataQuickAdd' -and $quickAddJs -match 'quickAddCards' -and $quickAddJs -match 'quickAddPreset' -and $quickAddJs -match 'quickAddTarget' -and $quickAddJs -match 'productCosting' -and $quickAddJs -match 'actionItem' -and $quickAddJs -match 'auditDoc' -and $quickAddJs -match 'partyContact') 'Main shell is not using complete Quick Add helpers.'
    Assert-True ($appJs -match 'startReceivablePdfInvoice' -and $appJs -match 'EpataInvoicePrefill' -and $invoicePrefillJs -match 'invoicePrefillFromReceivable' -and $invoicePrefillJs -match 'sourceReceivableInvoiceNumber') 'Main shell is not using AR-to-invoice PDF prefill helpers.'
    Assert-True ($appJs -match 'EpataAiProductDraft' -and $aiProductDraftJs -match 'openProductDraftPlan' -and $aiProductDraftJs -match 'shouldSaveAutomatically: false') 'Main shell is not using unsaved AI Product draft helpers.'
    Assert-True ($appJs -match 'EpataTaxPrepState' -and $taxPrepStateJs -match 'taxPrepAssetHandlingRows' -and $taxPrepStateJs -match 'taxPrepRewardIncomeRows' -and $appJs -match 'Asset Tax Handling' -and $appJs -match 'MakerWorld Reward Income Status') 'Main shell is not using Tax Prep asset/reward display helpers.'
    Assert-True ($appJs -match 'EpataCommunicationState' -and $communicationStateJs -match 'communicationCardPlan' -and $appJs -match 'long-summary') 'Main shell is not using Communication card state helpers.'
    Assert-True ($appJs -match 'EpataJobWorkflowState' -and $jobWorkflowStateJs -match 'timelineEventTarget' -and $jobWorkflowStateJs -match 'printerQueuePrefillFromJob' -and $appJs -match 'openTimelineEvent') 'Main shell is not using Job Workflow timeline/open/prefill helpers.'
    $invoiceAppJs = (Invoke-Raw GET '/invoice-builder/js/app.js').Content
    $invoiceRecordsJs = (Invoke-Raw GET '/invoice-builder/js/records.js').Content
    Assert-True ($appJs -match 'modalSaveOutcome\(\{ ok: true \}\)' -and $modalSaveStateJs -like "*'Saved.'*" -and $modalSaveStateJs -like '*Save failed:*' -and $invoiceAppJs -like '*Save failed:*') 'Save action toast feedback is missing from a browser module.'
    Assert-True ($appJs -match "toast\('Archived\.',\s*'success'\)" -and $invoiceRecordsJs -match '\$\{label\} archived' -and $invoiceAppJs -like '*Archive failed:*') 'Archive/delete action toast feedback is missing from a browser module.'
    Assert-True ($appJs -match "toast\('Restored\.',\s*'success'\)" -and $invoiceRecordsJs -match '\$\{label\} restored' -and $invoiceAppJs -like '*Restore failed:*') 'Restore action toast feedback is missing from a browser module.'
    Assert-True ($appJs -like '*Proof file attached and indexed.*' -and $appJs -like '*Upload failed:*' -and $invoiceAppJs -like '*PDF import failed:*') 'Upload action toast feedback is missing from a browser module.'
    Assert-True ($appJs -like '*Listing text copied.*' -and $appJs -like '*Calculator line copied.*' -and $invoiceAppJs -like '*Copied to clipboard!*') 'Copy action toast feedback is missing from a browser module.'
    $mainHtml = (Invoke-Raw GET '/index.html').Content
    $builderHtml = (Invoke-Raw GET '/invoice-builder/index.html').Content
    Assert-True ($mainHtml -match 'relationship-directory.js') 'Main shell is not loading the relationship directory helper.'
    Assert-True ($mainHtml -match 'toast-container' -and $mainHtml -match 'toast-stack.js') 'Main shell is not loading the stacked toast container/helper.'
    Assert-True ($mainHtml -match 'modal-lifecycle.js') 'Main shell is not loading the modal lifecycle helper.'
    Assert-True ($mainHtml -match 'modal-save-state.js') 'Main shell is not loading the modal save-state helper.'
    Assert-True ($mainHtml -match 'entity-table-state.js') 'Main shell is not loading the entity table state helper.'
    Assert-True ($mainHtml -match 'sidebar-state.js') 'Main shell is not loading the sidebar state helper.'
    Assert-True ($mainHtml -match 'navigation-history.js') 'Main shell is not loading the navigation history helper.'
    Assert-True ($mainHtml -match 'global-search.js') 'Main shell is not loading the global search helper.'
    Assert-True ($mainHtml -match 'quick-add.js') 'Main shell is not loading the Quick Add helper.'
    Assert-True ($mainHtml -match 'invoice-prefill.js') 'Main shell is not loading the invoice prefill helper.'
    Assert-True ($mainHtml -match 'tax-prep-state.js') 'Main shell is not loading the Tax Prep state helper.'
    Assert-True ($mainHtml -match 'communication-state.js') 'Main shell is not loading the Communication state helper.'
    Assert-True ($mainHtml -match 'job-workflow-state.js') 'Main shell is not loading the Job Workflow state helper.'
    Assert-True ($mainHtml -match 'dashboard-state.js') 'Main shell is not loading the Dashboard state helper.'
    Assert-True ($mainHtml -match 'id="sidebarToggle"[^>]*aria-controls="primarySidebar"[^>]*aria-expanded="true"[^>]*aria-label="Collapse menu"') 'Sidebar toggle screen-reader label is missing.'
    Assert-True ($mainHtml -match 'id="sidebarScrim"[^>]*aria-label="Close menu"') 'Sidebar scrim screen-reader label is missing.'
    Assert-True ($mainHtml -match 'id="modal"[^>]*role="dialog"[^>]*aria-modal="true"[^>]*aria-labelledby=' -and $mainHtml -match 'id="breakdownModal"[^>]*role="dialog"[^>]*aria-modal="true"[^>]*aria-labelledby=') 'Primary modal dialog semantics are missing.'
    Assert-True ($builderHtml -match 'id="invoicePdfImportFile"[^>]*aria-label="Import invoice or estimate PDF"') 'Invoice-builder PDF import file input is missing its screen-reader label.'
    Assert-True ($builderHtml -notmatch 'id="importDbFile"' -and $builderHtml -match 'disabled[^>]*aria-disabled="true"[^>]*title="[^"]*Database restore is disabled[^"]*"[^>]*>Database restore unavailable</button>' -and $builderHtml -match 'id="btnExportDb"[^>]*>[^<]*DB Backup</button>') 'Invoice-builder database recovery controls must provide a real backup download and an explained, disabled live-restore control.'
    Assert-True ($appJs -match 'aria-label="Choose proof files to upload"' -and $appJs -match 'aria-label="Attach proof file for \$\{escapeAttr\(field\.label \|\| field\.name\)\}"') 'Document/proof upload file inputs are missing screen-reader labels.'
    Assert-True ($appJs -match 'aria-label="Remove line item"' -and $invoiceRecordsJs -match 'aria-label="Duplicate \$\{escapeHtml\(r\.docNumber \|\|' -and $invoiceRecordsJs -match 'aria-label="Archive \$\{escapeHtml\(r\.docNumber \|\|') 'Icon-only invoice action buttons are missing screen-reader labels.'
    Assert-True ($appJs -match 'id="pgFirst" aria-label="First page"' -and $appJs -match 'id="pgLast" aria-label="Last page"' -and $invoiceRecordsJs -match 'id="recPageFirst" aria-label="First records page"' -and $invoiceRecordsJs -match 'id="recPageLast" aria-label="Last records page"') 'Pager icon buttons are missing screen-reader labels.'
    Invoke-Json GET '/api/dashboard' | Out-Null
    Invoke-Json GET '/api/tax-audit' | Out-Null
    $taxProfile = Invoke-Json GET '/api/tax-profile'
    Assert-True ($taxProfile.state -eq 'New Jersey') 'Tax profile did not default to New Jersey.'
    $taxProfile.entityType = 'Single-member LLC / Schedule C'
    $taxProfile.formationMonth = 5
    $taxProfile.njSalesTaxRegistration = 'Registered'
    $taxProfile.usesVehicle = 'Yes'
    $taxProfile.businessMileageRate = 0.70
    $savedTaxProfile = Invoke-Json PUT '/api/tax-profile' $taxProfile
    Assert-True ($savedTaxProfile.formationMonth -eq 5) 'Tax profile formation month did not save.'
    $taxCalendar = Invoke-Json POST '/api/tax-calendar/generate?year=2026'
    Assert-True (@($taxCalendar).Count -ge 10) 'Tax calendar generation did not create the expected obligation set.'
    Assert-True (@($taxCalendar | Where-Object { $_.title -like '*annual report*' -and ([datetime]$_.dueDate).Month -eq 5 }).Count -eq 1) 'NJ annual report did not use the configured formation month.'
    $taxSummary = Invoke-Json GET '/api/tax-summary?year=2026'
    Assert-True ($taxSummary.taxYear -eq 2026) 'Tax summary returned the wrong year.'
    Invoke-Json GET '/api/lookups' | Out-Null
    Invoke-Json GET '/api/app-info' | Out-Null
    $localAiStatus = Invoke-Json GET '/api/ai/local/status'
    Assert-True ($localAiStatus.localOnly -eq $true) 'Local AI status did not enforce its local-only boundary.'
    Assert-True ($localAiStatus.baseUrl -eq 'http://127.0.0.1:1234') 'Local AI status did not use the safe default loopback URL.'
    $savedLocalAiSettings = Invoke-Json PUT '/api/ai/local/settings' @{
        baseUrl = 'http://localhost:1234'
        modelPath = $null
        modelIdentifier = 'acceptance-local'
        contextLength = 4096
        idleUnloadSeconds = 600
    }
    Assert-True ($savedLocalAiSettings.baseUrl -eq 'http://localhost:1234') 'Local AI settings did not save a loopback URL.'
    Assert-True ($savedLocalAiSettings.modelIdentifier -eq 'acceptance-local') 'Local AI settings did not save the model identifier.'
    $invalidLocalAiSettings = Invoke-WebRequest -Method PUT -Uri "$base/api/ai/local/settings" -ContentType 'application/json' -Body (@{
        baseUrl = 'https://example.com'
        modelPath = $null
        modelIdentifier = 'unsafe'
        contextLength = 4096
        idleUnloadSeconds = 600
    } | ConvertTo-Json) -SkipHttpErrorCheck
    Assert-True ($invalidLocalAiSettings.StatusCode -eq 400) 'Local AI settings accepted a non-loopback URL.'
    $catalogProduct = Invoke-Json POST '/api/products' @{
        name = 'Acceptance Catalog Widget'
        sku = 'AI-CATALOG-001'
        category = '3D Printed Product'
        material = 'PETG'
        color = 'Blue'
        grams = 100
        materialCostPerGram = 0.04
        printHours = 2
        machineRatePerHour = 3
        packagingCost = 2
        designMinutes = 30
        targetPrice = 40
        notes = 'Saved product costing acceptance test'
    }
    $aiStatus = Invoke-Json GET '/api/ai/estimate/status'
    Assert-True ($aiStatus.instructionsPath -like '*AiEstimateInstructions.json') 'AI estimate status did not report the editable instructions path.'
    Assert-True (@($aiStatus.supportedUploads) -contains '.png') 'AI estimate status did not report picture upload support.'
    Assert-True (@($aiStatus.supportedUploads) -contains '.pdf') 'AI estimate status did not report PDF upload support.'
    Assert-True (@($aiStatus.supportedUploads) -contains '.docx') 'AI estimate status did not report DOCX upload support.'
    Assert-True ($aiStatus.limits.maxCombinedTextCharacters -eq 500000) 'AI estimate status did not report the expanded text limit.'
    Assert-True ($aiStatus.builderPath -like '*invoice-builder*index.html') 'AI estimate status did not report the ingested HTML builder path.'
    Assert-True (@($aiStatus.builderFields) -contains 'grams') 'AI estimate status did not ingest calculator fields from the HTML builder.'
    Assert-True (@($aiStatus.builderFields) -contains 'termsNotes') 'AI estimate status did not ingest terms fields from the HTML builder.'
    Assert-True ($aiStatus.productCatalogCount -ge 1) 'AI estimate status did not report the saved product/cost catalog.'
    Assert-True ($aiStatus.cloudAi.enabled -eq $false) 'Full acceptance must keep hosted AI disabled so extraction assertions remain deterministic.'
    $aiDraft = Invoke-Json POST '/api/ai/estimate-draft' @{
        sourceName = 'acceptance-email.txt'
        sourceText = "From: Jane Customer`nSubject: Replacement bracket`nPlease make 3 black PETG replacement parts, 40mm x 20mm x 10mm. My phone is 973-555-0123."
    }
    Assert-True ($aiDraft.prefill.docType -eq 'ESTIMATE') 'AI estimate intake did not return an estimate draft.'
    Assert-True (@($aiDraft.prefill.lineItems).Count -gt 0) 'AI estimate intake did not return a line item.'
    Assert-True ($aiDraft.prefill.customerPhone -eq '(973) 555-0123') 'AI estimate intake did not normalize the customer phone.'
    Assert-True ($aiDraft.prefill.projectNotes -like '*ASSISTANCE:*') 'AI estimate intake did not add durable assistance provenance to Project Notes.'
    Assert-True ($aiDraft.prefill.termsNotes.Length -gt 20) 'AI estimate intake did not fill estimate terms.'
    Assert-True ($aiDraft.prefill.standardTurnaround.Length -gt 10) 'AI estimate intake did not fill standard turnaround.'
    Assert-True ($aiDraft.prefill.calcGramRate -eq 0.05) 'AI estimate intake did not fill the calculator material rate.'
    Assert-True ($aiDraft.prefill.assistanceSource -eq 'LOCAL RULES') 'AI estimate fallback did not identify its builder assistance source.'
    Assert-True ($aiDraft.executionReceipt.engine -eq 'LOCAL RULES') 'AI estimate fallback did not return a Local Rules execution receipt.'
    Assert-True ($aiDraft.executionReceipt.usedAi -eq $false) 'AI estimate fallback execution receipt incorrectly claimed model AI use.'
    Assert-True ($aiDraft.executionReceipt.totalTokens -eq $null) 'AI estimate fallback execution receipt incorrectly reported model tokens.'
    Assert-True ($aiDraft.prefill.projectNotes -like '*LOCAL RULES RECEIPT:*') 'AI estimate fallback did not preserve its execution receipt in Project Notes.'
    $bulkPickDraft = Invoke-Json POST '/api/ai/estimate-draft' @{
        sourceName = 'bulk-guitar-pick-request.txt'
        sourceText = 'Please quote 300 custom guitar picks, slightly oversized at 1.2mm, with a four color cartoon. The $200 design/proof phase is approved. Make a sample first. I was hoping to spend $700.'
    }
    Assert-True ($bulkPickDraft.usedAi -eq $false) 'Bulk-pick local fallback incorrectly claimed model AI use.'
    Assert-Close ([decimal]$bulkPickDraft.prefill.calcGrams) 562.5 0.01 'Bulk-pick editable material planning assumption'
    Assert-Close ([decimal]$bulkPickDraft.prefill.calcHours) 120 0.01 'Bulk-pick editable machine-time planning assumption'
    Assert-True (@($bulkPickDraft.prefill.lineItems | Where-Object { $_.description -like '*Bulk handling*' }).Count -eq 1) 'Bulk-pick planning did not include configured bulk handling.'
    Assert-True ($bulkPickDraft.prefill.projectName -like '*Guitar Pick*') 'Bulk-pick planning did not identify the actual job.'
    Assert-True (@($bulkPickDraft.warnings | Where-Object { $_ -like '*stated*budget*' }).Count -eq 1) 'Bulk-pick planning did not compare the draft with the stated budget.'
    $aiPricedDraft = Invoke-Json POST '/api/ai/estimate-draft' @{
        sourceName = 'acceptance-priced-request.txt'
        sourceText = "Customer: Price Check Customer`nSubject: PETG enclosure`nUse black PETG. Material: 120 grams. Print time: 6 hours. Design time: 1 hour. Setup fee: `$5. Post-processing: `$4. Material rate: `$0.05 per gram. Machine rate: `$3. Design rate: `$25. Minimum: `$15. Rush: 25%. Discount: `$2. Tax: 6.625%."
    }
    Assert-True ($aiPricedDraft.pricing.usedCalculatorInputs -eq $true) 'AI estimate intake did not use supplied calculator inputs.'
    Assert-Close ([decimal]$aiPricedDraft.pricing.material) 6 0.01 'AI estimate material calculation'
    Assert-Close ([decimal]$aiPricedDraft.pricing.machine) 18 0.01 'AI estimate machine-time calculation'
    Assert-Close ([decimal]$aiPricedDraft.pricing.design) 25 0.01 'AI estimate design calculation'
    Assert-Close ([decimal]$aiPricedDraft.pricing.lineSubtotal) 58 0.01 'AI estimate deterministic line subtotal'
    Assert-Close ([decimal]$aiPricedDraft.pricing.total) 75.17 0.01 'AI estimate deterministic quote total'
    Assert-True (@($aiPricedDraft.prefill.lineItems | Where-Object { $_.description -eq 'Material usage' }).Count -eq 1) 'AI estimate did not build the material-cost line item.'
    Assert-True ($aiPricedDraft.prefill.projectNotes -like '*PRICING BASIS:*') 'AI estimate did not add durable pricing provenance.'
    $aiCatalogDraft = Invoke-Json POST '/api/ai/estimate-draft' @{
        sourceName = 'saved-product-request.txt'
        sourceText = 'Please quote one Acceptance Catalog Widget.'
    }
    Assert-True ($aiCatalogDraft.prefill.material -eq 'PETG') 'AI estimate did not use the saved product material.'
    Assert-Close ([decimal]$aiCatalogDraft.prefill.calcGrams) 100 0.01 'AI estimate saved product grams'
    Assert-Close ([decimal]$aiCatalogDraft.pricing.total) 40 0.01 'AI estimate saved product target-price floor'
    Assert-True (@($aiCatalogDraft.warnings | Where-Object { $_ -like '*Saved product costing was applied*' }).Count -eq 1) 'AI estimate did not disclose saved product costing use.'
    Write-Step 'Checking AI Operations workflows'
    $duplicateProduct = Invoke-Json POST '/api/products' @{
        name = 'Acceptance Catalog Widget Duplicate'
        sku = 'AI-CATALOG-001'
        category = 'Acceptance Product'
        material = 'PETG'
        needsReview = $true
    }
    $reconciliation = Invoke-Json GET '/api/ai/operations/reconciliation'
    Assert-True ($reconciliation.receipt.usedAi -eq $false) 'Duplicate checker must remain deterministic by default.'
    Assert-True (@($reconciliation.findings | Where-Object { $_.kind -eq 'Exact duplicate' -and $_.title -like '*SKU*' }).Count -ge 1) 'Duplicate checker did not find the deliberately duplicated Product SKU.'
    $actionPreview = Invoke-Json GET '/api/action-items/automation-preview'
    Assert-True ($actionPreview.receipt.usedAi -eq $false) 'Action sync preview incorrectly claimed model AI use.'
    Assert-True ($actionPreview.readyToCreateCount -gt 0) 'Action sync preview did not identify any verified findings ready to create.'
    Assert-True (@($actionPreview.candidates | Where-Object { $_.source -like '*Reconciliation*' }).Count -ge 1) 'Action sync preview did not include reconciliation findings.'
    Assert-True (@($actionPreview.candidates | Where-Object { $_.area -eq 'Actions' }).Count -eq 0) 'Action sync preview included the recursive open-actions finding.'
    $actionSync = Invoke-Json POST '/api/action-items/sync-findings' @{}
    Assert-True ($actionSync.createdCount -gt 0) 'Action finding sync did not create any Action Items.'
    Assert-True (@($actionSync.created | Where-Object { $_.notes -like '*AUTOMATION KEY:*' }).Count -eq $actionSync.createdCount) 'Generated Action Items did not preserve stable automation keys.'
    $actionSyncRepeat = Invoke-Json POST '/api/action-items/sync-findings' @{}
    Assert-True ($actionSyncRepeat.createdCount -eq 0) 'Repeating action finding sync created duplicate open tasks.'
    Assert-True ($actionSyncRepeat.skippedExistingCount -ge $actionSync.createdCount) 'Repeated action finding sync did not report existing open generated tasks.'

    $marketplaceDraft = Invoke-RestMethod -Method Post -Uri "$base/api/ai/operations/marketplace-order-import" -Form @{
        sourceName = 'acceptance-etsy-order.txt'
        sourceText = @'
Etsy Order # 4999000111
Order date: June 10, 2026
Ship to
Acceptance Etsy Buyer
123 Acceptance Test Lane
TESTVILLE, NJ 07001
United States
Scheduled to ship by June 12, 2026
Buyer Acceptance Etsy Buyer (acceptance.etsy@example.test)
Item: Acceptance PETG Widget Set
Quantity: 2
Item total: $40.00
Shipping: $6.00
Sales tax: $3.00
Order total: $49.00
Tracking number: 9400111899000000000000
'@
        sourceUrls = ''
    }
    Assert-True ($marketplaceDraft.sale.platform -eq 'Etsy') 'Paid marketplace importer did not identify Etsy.'
    Assert-True ($marketplaceDraft.sale.paymentMethod -eq 'Etsy Payments') 'Paid marketplace importer did not default Etsy Sale payment method.'
    Assert-True ($marketplaceDraft.job.paymentMethod -eq 'Etsy Payments') 'Paid marketplace importer did not default Etsy Job payment method.'
    Assert-True ($marketplaceDraft.sale.orderNumber -eq '4999000111') 'Paid marketplace importer did not extract the order number.'
    Assert-True (([datetime]$marketplaceDraft.sale.saleDate).ToString('yyyy-MM-dd') -eq '2026-06-10') 'Paid marketplace importer did not extract the Sale Date.'
    Assert-True (@($marketplaceDraft.detectedOrderNumbers).Count -eq 1) 'Paid marketplace importer did not report the detected order number.'
    Assert-True ($marketplaceDraft.customer.name -eq 'Acceptance Etsy Buyer') 'Paid marketplace importer did not extract the customer contact name.'
    Assert-True ($marketplaceDraft.customer.email -eq 'acceptance.etsy@example.test') 'Paid marketplace importer did not extract the customer email.'
    Assert-True ($marketplaceDraft.customer.address1 -eq '123 Acceptance Test Lane') 'Paid marketplace importer did not extract the customer street address.'
    Assert-True ($marketplaceDraft.customer.city -eq 'Testville') 'Paid marketplace importer did not extract the customer city.'
    Assert-True ($marketplaceDraft.customer.state -eq 'NJ') 'Paid marketplace importer did not extract the customer state.'
    Assert-True ($marketplaceDraft.customer.postalCode -eq '07001') 'Paid marketplace importer did not extract the customer ZIP.'
    Assert-Close ([decimal]$marketplaceDraft.sale.customerPaid) 49 0.01 'Paid marketplace importer customer paid'
    Assert-True ($marketplaceDraft.possibleDuplicateSales.Count -eq 0) 'New marketplace order was incorrectly marked duplicate.'
    $interleavedMarketplaceDraft = Invoke-RestMethod -Method Post -Uri "$base/api/ai/operations/marketplace-order-import" -Form @{
        sourceName = 'acceptance-interleaved-etsy-pdf-text.txt'
        sourceText = 'Etsy Ship to 1 item Acceptance Column Buyer 987 Column Test Rd Product title 1 x $10.00 COLUMNTOWN, NJ 07002 More product text United States Scheduled to ship by Jun 12, 2026 From EPATA 12 Seller Ave SELLERTOWN, NJ 07003 United States Order #4999000333 Order date Jun 10, 2026 Buyer Acceptance Column Buyer (columnbuyer)'
        sourceUrls = ''
    }
    Assert-True ($interleavedMarketplaceDraft.customer.name -eq 'Acceptance Column Buyer') 'Paid marketplace importer did not extract the ship-to name from interleaved Etsy PDF text.'
    Assert-True ($interleavedMarketplaceDraft.customer.address1 -eq '987 Column Test Rd') 'Paid marketplace importer did not extract the street from interleaved Etsy PDF text.'
    Assert-True ($interleavedMarketplaceDraft.customer.city -eq 'Columntown') 'Paid marketplace importer did not extract the city from interleaved Etsy PDF text.'
    Assert-True ($interleavedMarketplaceDraft.customer.etsyUsername -eq 'columnbuyer') 'Paid marketplace importer did not extract the Etsy username from interleaved Etsy PDF text.'
    $missingDateSale = $marketplaceDraft.sale | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $missingDateSale.saleDate = $null
    $missingDateSave = Invoke-WebRequest -Method Post -Uri "$base/api/ai/operations/marketplace-order-import/save" -Form @{
        saleJson = ($missingDateSale | ConvertTo-Json -Depth 12)
        jobJson = ($marketplaceDraft.job | ConvertTo-Json -Depth 12)
        createJob = 'false'
        detectedOrderNumbersJson = (ConvertTo-Json -InputObject @($marketplaceDraft.detectedOrderNumbers) -Compress)
    } -SkipHttpErrorCheck
    Assert-True ($missingDateSave.StatusCode -eq 400) 'Paid marketplace save accepted a missing Sale Date.'

    $mixedMarketplaceDraft = Invoke-RestMethod -Method Post -Uri "$base/api/ai/operations/marketplace-order-import" -Form @{
        sourceName = 'acceptance-mixed-etsy-orders.txt'
        sourceText = @'
Etsy Order # 4999000111
Order date Jun 7, 2026
Order total: $49.00

Etsy Order # 4999000222
Order date Jun 10, 2026
Order total: $61.87
'@
        sourceUrls = ''
    }
    Assert-True (@($mixedMarketplaceDraft.detectedOrderNumbers).Count -eq 2) 'Paid marketplace importer did not detect multiple order numbers in one batch.'
    $mixedMarketplaceSave = Invoke-WebRequest -Method Post -Uri "$base/api/ai/operations/marketplace-order-import/save" -Form @{
        saleJson = ($marketplaceDraft.sale | ConvertTo-Json -Depth 12)
        jobJson = ($marketplaceDraft.job | ConvertTo-Json -Depth 12)
        createJob = 'false'
        detectedOrderNumbersJson = (ConvertTo-Json -InputObject @($mixedMarketplaceDraft.detectedOrderNumbers) -Compress)
    } -SkipHttpErrorCheck
    Assert-True ($mixedMarketplaceSave.StatusCode -eq 400) 'Paid marketplace save accepted a batch containing multiple orders.'

    $arCountBeforeMarketplaceSave = @((Invoke-Json GET '/api/receivable-invoices')).Count
    $invoiceDocCountBeforeMarketplaceSave = @((Invoke-Json GET '/api/invoice-documents')).Count
    Set-Content -LiteralPath $uploadProbe -Value 'Acceptance marketplace order proof' -NoNewline
    $marketplaceSave = Invoke-RestMethod -Method Post -Uri "$base/api/ai/operations/marketplace-order-import/save" -Form @{
        saleJson = ($marketplaceDraft.sale | ConvertTo-Json -Depth 12)
        jobJson = ($marketplaceDraft.job | ConvertTo-Json -Depth 12)
        customerJson = ($marketplaceDraft.customer | ConvertTo-Json -Depth 12)
        createJob = 'true'
        saveCustomerContact = 'true'
        detectedOrderNumbersJson = (ConvertTo-Json -InputObject @($marketplaceDraft.detectedOrderNumbers) -Compress)
        files = Get-Item -LiteralPath $uploadProbe
    }
    Assert-True ($marketplaceSave.sale.id -gt 0) 'Paid marketplace save did not create a Sale.'
    Assert-True ($marketplaceSave.job.id -gt 0) 'Paid marketplace save did not create the optional completed Job.'
    Assert-True ($marketplaceSave.sale.paymentMethod -eq 'Etsy Payments') 'Paid marketplace save did not preserve Sale payment method.'
    Assert-True ($marketplaceSave.job.paymentMethod -eq 'Etsy Payments') 'Paid marketplace save did not preserve Job payment method.'
    Assert-True ($marketplaceSave.customer.id -gt 0) 'Paid marketplace save did not create the customer business card.'
    Assert-True ($marketplaceSave.customer.address1 -eq '123 Acceptance Test Lane') 'Saved customer business card lost the shipping address.'
    Assert-True (@($marketplaceSave.auditDocuments).Count -eq 1) 'Paid marketplace save did not link uploaded proof.'
    Assert-True (@((Invoke-Json GET '/api/receivable-invoices')).Count -eq $arCountBeforeMarketplaceSave) 'Paid marketplace save incorrectly created AR.'
    Assert-True (@((Invoke-Json GET '/api/invoice-documents')).Count -eq $invoiceDocCountBeforeMarketplaceSave) 'Paid marketplace save incorrectly created an estimate/invoice document.'
    $marketplaceDuplicate = Invoke-RestMethod -Method Post -Uri "$base/api/ai/operations/marketplace-order-import" -Form @{
        sourceName = 'acceptance-etsy-duplicate.txt'
        sourceText = 'Etsy Order # 4999000111 Item: Acceptance PETG Widget Set Order total: $49.00'
        sourceUrls = ''
    }
    Assert-True (@($marketplaceDuplicate.possibleDuplicateSales).Count -eq 1) 'Paid marketplace importer did not detect an existing Etsy order.'
    Invoke-Json DELETE "/api/sales/$($marketplaceSave.sale.id)" | Out-Null
    Invoke-Json DELETE "/api/customer-jobs/$($marketplaceSave.job.id)" | Out-Null
    Invoke-Json DELETE "/api/parties/$($marketplaceSave.customer.id)" | Out-Null
    Invoke-Json DELETE "/api/audit-documents/$($marketplaceSave.auditDocuments[0].id)" | Out-Null

    $productCountBeforeAiImport = @((Invoke-Json GET '/api/products?includeArchived=true')).Count
    $productImport = Invoke-RestMethod -Method Post -Uri "$base/api/ai/operations/product-import" -Form @{
        sourceName = 'acceptance-product-source.txt'
        sourceText = 'Acceptance Imported Bracket. Black PETG replacement bracket.'
        sourceUrls = ''
    }
    Assert-True ($productImport.product.name -like '*Acceptance Imported Bracket*') 'Product importer did not build a Product draft from pasted source text.'
    Assert-True ($productImport.product.material -eq 'PETG') 'Product importer did not extract material.'
    Assert-True ($productImport.product.needsReview -eq $true) 'Product importer draft must require review.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($productImport.listing.title)) 'Product importer did not return listing title copy.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($productImport.listing.description)) 'Product importer did not return listing description copy.'
    Assert-True (@($productImport.receipt.writes | Where-Object { $_ -like '*Unsaved Product*' }).Count -ge 1) 'Product importer receipt did not state that the Product draft is unsaved.'
    Assert-True (@((Invoke-Json GET '/api/products?includeArchived=true')).Count -eq $productCountBeforeAiImport) 'Product importer saved a Product automatically.'

    $jobPlan = Invoke-Json POST '/api/ai/operations/job-plan' @{ sourceText = 'Build 10 black PETG brackets, get prototype approval, then package and invoice.' }
    Assert-True (@($jobPlan.tasks).Count -ge 5) 'Job planner did not return a practical multi-step plan.'
    $jobPlanActions = Invoke-Json POST '/api/ai/operations/job-plan/actions' @{ jobName = $jobPlan.jobName; tasks = $jobPlan.tasks }
    Assert-True (@($jobPlanActions).Count -ge 5) 'Confirmed Job Planner did not create its Action Items.'
    Assert-True (@($jobPlanActions | Where-Object { $_.notes -like '*AUTOMATION KEY: job-plan*' }).Count -eq @($jobPlanActions).Count) 'Job Planner Action Items did not preserve deduplication keys.'
    $jobPlanActionsRepeat = Invoke-Json POST '/api/ai/operations/job-plan/actions' @{ jobName = $jobPlan.jobName; tasks = $jobPlan.tasks }
    Assert-True (@($jobPlanActionsRepeat).Count -eq 0) 'Repeating a confirmed Job Planner plan created duplicate open tasks.'

    $slicerRead = Invoke-RestMethod -Method Post -Uri "$base/api/ai/operations/slicer-read" -Form @{
        sourceName = 'acceptance-slicer.txt'
        sourceText = 'Material: PETG. Filament used 184.6g. Print time 8h 42m. Plates: 2. Quantity: 4.'
        sourceUrls = ''
    }
    Assert-True ($slicerRead.slicer.material -eq 'PETG') 'Slicer reader material'
    Assert-Close ([decimal]$slicerRead.slicer.grams) 184.6 0.01 'Slicer reader grams'
    Assert-Close ([decimal]$slicerRead.slicer.printHours) 8.7 0.01 'Slicer reader hours'
    Assert-True ($slicerRead.slicer.plateCount -eq 2) 'Slicer reader plate count'
    Assert-True ($slicerRead.slicer.quantity -eq 4) 'Slicer reader quantity'

    $listingProductBefore = Invoke-Json GET "/api/products/$($catalogProduct.id)"
    $listing = Invoke-Json POST '/api/ai/operations/listing' @{ productId = $catalogProduct.id; platform = 'MakerWorld'; extraInstructions = 'Keep it concise.' }
    Assert-True (-not [string]::IsNullOrWhiteSpace($listing.listing.title)) 'Listing writer did not return a title.'
    Assert-True (-not [string]::IsNullOrWhiteSpace($listing.listing.description)) 'Listing writer did not return a description.'
    $listingProductAfter = Invoke-Json GET "/api/products/$($catalogProduct.id)"
    Assert-True ($listingProductAfter.updatedAtUtc -eq $listingProductBefore.updatedAtUtc) 'Listing writer modified the Product row.'
    Assert-True (@($listing.receipt.writes | Where-Object { $_ -like '*preview only*' }).Count -ge 1) 'Listing writer receipt did not state preview-only output.'

    $ledgerAnswer = Invoke-Json POST '/api/ai/operations/ask-ledger' @{ query = 'Acceptance Catalog Widget' }
    Assert-True (@($ledgerAnswer.results).Count -ge 1) 'Ask the Ledger did not return the matching Product.'
    Assert-True (@($ledgerAnswer.results | Where-Object { $_.route -eq 'products' -and $_.id -eq $catalogProduct.id }).Count -eq 1) 'Ask the Ledger did not return a Product route/id open action.'
    Assert-True ($ledgerAnswer.receipt.safety -like '*never edits*') 'Ask the Ledger receipt did not state its read-only boundary.'
    Invoke-Json DELETE "/api/products/$($duplicateProduct.id)" | Out-Null
    Invoke-Json DELETE "/api/products/$($catalogProduct.id)" | Out-Null
    $aiMixedDraft = Invoke-AiEstimateUpload
    Assert-True (@($aiMixedDraft.prefill.lineItems).Count -ge 3) 'Mixed-source AI estimate intake did not create separate pasted-text and picture items.'
    Assert-True (@($aiMixedDraft.prefill.lineItems | Where-Object { $_.description -like 'Item from picture:*' }).Count -eq 1) 'Picture upload did not create a review line item in local fallback mode.'
    $aiDocumentDraft = Invoke-AiEstimateDocumentUpload
    Assert-True ($aiDocumentDraft.usedAi -eq $false -and $aiDocumentDraft.executionReceipt.engine -eq 'LOCAL RULES') 'Document extraction acceptance unexpectedly used an AI provider.'
    Assert-True ($aiDocumentDraft.prefill.customerName -eq 'Acceptance DOCX Customer') 'DOCX customer identity was not mapped into the AI estimate draft.'
    Assert-True (@($aiDocumentDraft.prefill.lineItems | Where-Object { $_.description -eq 'DOCX bracket' -and $_.quantity -eq 2 -and $_.rate -eq 31 }).Count -eq 1) 'DOCX quantity, item, and rate were not mapped into one line item.'
    Assert-True ($aiDocumentDraft.prefill.projectDescription -like '*PDF custom sign request*') 'PDF source text was not extracted into the AI estimate draft.'
    Assert-True ($aiDocumentDraft.prefill.projectName -notlike '*SOURCE FILE*' -and $aiDocumentDraft.prefill.projectDescription -notlike '*SOURCE FILE*') 'Upload envelope metadata leaked into customer-facing project fields.'
    $aiInvoiceDocumentDraft = Invoke-AiInvoiceDocumentDraftUpload
    Assert-True ($aiInvoiceDocumentDraft.prefill.docType -eq 'INVOICE') 'Invoice PDF import did not detect invoice type.'
    Assert-True ($aiInvoiceDocumentDraft.prefill.docNumber -eq 'INV-2026-0099') 'Invoice PDF import did not recover invoice number.'
    Assert-True ($aiInvoiceDocumentDraft.prefill.status -eq 'Paid') 'Invoice PDF import did not recover paid status.'
    Assert-True ($aiInvoiceDocumentDraft.prefill.customerPhone -eq '(973) 555-0199') 'Invoice PDF import did not normalize customer phone.'
    Assert-True ($aiInvoiceDocumentDraft.prefill.customerEmail -eq 'pdfcustomer@example.test') 'Invoice PDF import did not recover customer email.'
    Assert-True ($aiInvoiceDocumentDraft.prefill.projectName -eq 'Imported Console Cover') 'Invoice PDF import did not recover project name.'
    Assert-Close ([decimal]$aiInvoiceDocumentDraft.prefill.amountPaid) 104.50 0.01 'Invoice PDF import amount paid'
    Assert-Close ([decimal]$aiInvoiceDocumentDraft.prefill.docTaxRate) 10 0.01 'Invoice PDF import tax rate'
    Assert-True (@($aiInvoiceDocumentDraft.prefill.lineItems).Count -eq 1) 'Invoice PDF import did not recover one line item.'
    Assert-Close ([decimal]$aiInvoiceDocumentDraft.prefill.lineItems[0].rate) 100 0.01 'Invoice PDF import line item rate'
    Assert-True ($aiInvoiceDocumentDraft.executionReceipt.usedAi -eq $false) 'Invoice PDF local mapper incorrectly claimed model AI use.'
    $aiEstimateDocumentDraft = Invoke-AiEstimateDocumentDraftUpload
    Assert-True ($aiEstimateDocumentDraft.prefill.docType -eq 'ESTIMATE') 'Estimate PDF import did not detect estimate type.'
    Assert-True ($aiEstimateDocumentDraft.prefill.docNumber -eq 'EST-2026-0042') 'Estimate PDF import did not recover estimate number.'
    Assert-True ($aiEstimateDocumentDraft.prefill.projectName -eq 'Imported Estimate Bracket') 'Estimate PDF import did not recover project name.'
    Assert-Close ([decimal]$aiEstimateDocumentDraft.prefill.amountPaid) 0 0.01 'Estimate PDF import should not mark paid.'
    Assert-True (@($aiEstimateDocumentDraft.prefill.lineItems).Count -eq 1) 'Estimate PDF import did not recover one line item.'
    $aiBlockedUrlDraft = Invoke-Json POST '/api/ai/estimate-draft' @{
        sourceName = 'blocked URL safety test'
        sourceText = 'One custom bracket'
        sourceUrls = @('https://localhost/private-product')
    }
    Assert-True (@($aiBlockedUrlDraft.warnings | Where-Object { $_ -like '*public HTTPS pages only*' }).Count -eq 1) 'AI estimate intake did not disclose that a private/local URL was blocked.'
    $aiReviewStatus = Invoke-Json GET '/api/ai/review/status'
    Assert-True ($aiReviewStatus.sendsDataToAiProvider -eq $false) 'AI review status did not disclose that local review stays local.'
    Assert-True ($aiReviewStatus.safety -like '*Read-only*') 'AI review status did not disclose its read-only safety boundary.'
    $aiReview = Invoke-Json GET '/api/ai/review'
    Assert-True ($aiReview.engine -like '*Local rules*') 'AI review did not identify its engine.'
    Assert-True ($aiReview.safety -like '*Read-only*') 'AI review did not identify its safety boundary.'

    Write-Step 'Checking admin/config endpoints'
    Assert-True $backupCopyPreflightCompleted 'Round 64 backup preflight did not run before admin/clear checks.'
    $config = Invoke-Json GET '/api/config'
    $config.businessName = 'EPATA Acceptance Ledger'
    $config.calcMinimum = 18
    $savedConfig = Invoke-Json PUT '/api/config' $config
    Assert-True ($savedConfig.businessName -eq 'EPATA Acceptance Ledger') 'Config save did not persist businessName.'
    $clearFailure = Invoke-Raw POST '/api/database/clear'
    Assert-True ($clearFailure.StatusCode -eq 400) 'Database clear should be blocked.'

    Write-Step 'Checking generic ledger CRUD routes'
    $party = Assert-CrudRoundTrip 'parties' @{ name='Acceptance Customer'; partyType='Both'; email='customer@example.test'; phone='973-555-0101'; city='Testville'; state='NJ'; country='United States'; notes='acceptance' } 'notes' 'updated party'
    Assert-CrudRoundTrip 'products' @{ name='Acceptance Product'; sku='ACC-001'; category='3D Printed Product'; material='PLA'; color='Black'; grams=-10; materialCostPerGram=-0.1; printHours=2; machineRatePerHour=3; packagingCost=-1; designMinutes=20; targetPrice=-5; notes='acceptance' } 'notes' 'updated product' | Out-Null
    $job = Assert-CrudRoundTrip 'customer-jobs' @{ jobDate='2026-05-30'; customerName='Acceptance Customer'; platform='Direct'; jobNumber='JOB-ACC-001'; relatedInvoiceNumber='INV-ACC-001'; jobName='Acceptance Job'; jobType='Print'; status='Open'; productName='Acceptance Product'; material='PLA'; color='Black'; quoteAmount=-25; invoiceAmount=-40; amountPaid=-10; paymentMethod='Zelle'; notes='acceptance' } 'status' 'Completed'
    Assert-True ($job.paymentMethod -eq 'Zelle') 'Customer Job CRUD payment method was not preserved.'
    $sale = Assert-CrudRoundTrip 'sales' @{ saleDate='2026-05-30'; platform='Direct'; paymentMethod='Credit Card'; salesTaxHandling='Seller Collected'; orderNumber='ORD-ACC-001'; invoiceNumber=''; customerName='Acceptance Customer'; productName='Acceptance Product'; quantity=-1; itemSales=80; shippingCharged=5; salesTaxCollected=6.8; customerPaid=999; platformFees=-3; shippingLabelCost=-2; refunds=-4; estimatedCogs=-5; status='Paid'; includeInDashboard=$true; notes='acceptance' } 'notes' 'updated sale'
    Assert-Close ([decimal]$sale.customerPaid) 91.8 0.01 'Sale CRUD customer paid should be normalized.'
    $ar = Assert-CrudRoundTrip 'receivable-invoices' @{ invoiceNumber='AR-ACC-001'; invoiceDate='2026-05-30'; dueDate='2026-06-15'; customerName='Acceptance Customer'; projectName='AR Project'; status='Partial'; subtotal=100; discount=-12; rushFee=-4; taxRatePercent=10; salesTax=-7; invoiceTotal=1; amountPaid=-2; paymentMethod='PayPal'; includeInCashReports=$true; notes='acceptance' } 'status' 'Paid'
    Assert-True ($ar.paymentMethod -eq 'PayPal') 'Receivable CRUD payment method was not preserved.'
    $bill = Assert-CrudRoundTrip 'bills' @{ vendorName='Acceptance Vendor'; billNumber='BILL-ACC-001'; billDate='2026-05-30'; dueDate='2026-06-30'; category='Supplies'; description='Acceptance bill'; amount=100; salesTax=7; total=1; amountPaid=-8; status='Partial'; paymentMethod='ACH / Bank Transfer'; paymentAccount='Checking'; taxDeductible=$true; notes='acceptance' } 'status' 'Paid'
    Assert-True ($bill.paymentMethod -eq 'ACH / Bank Transfer') 'Bill CRUD payment method was not preserved.'
    Assert-Close ([decimal]$bill.total) 107 0.01 'Bill total should normalize amount plus tax.'
    $expense = Assert-CrudRoundTrip 'expenses' @{ expenseDate='2026-05-30'; vendorName='Acceptance Vendor'; category='Supplies'; taxCategory='COGS / materials'; description='Acceptance expense'; paymentMethod='Debit Card'; paymentAccount='Checking'; amount=20; salesTax=2; total=1; receiptProof='proof'; taxBucket='COGS/Materials'; deductibleStatus='Yes'; businessUsePercent=150; countedExpense=$true; taxDeductible=$true; notes='acceptance' } 'notes' 'updated expense'
    Assert-True ($expense.paymentMethod -eq 'Debit Card') 'Expense CRUD payment method was not preserved.'
    Assert-Close ([decimal]$expense.total) 22 0.01 'Expense total should normalize amount plus tax.'
    Assert-Close ([decimal]$expense.businessUsePercent) 100 0.01 'Expense business use should clamp at 100.'
    $asset = Assert-CrudRoundTrip 'assets' @{ name='Acceptance Printer'; purchaseDate='2026-05-30'; vendorName='Bambu'; category='Equipment'; cost=-200; paymentMethod='Credit Card'; serialNumber='ACC123'; businessUsePercent=150; inServiceDate='2026-05-30'; taxTreatment='Section 179'; countedExpenseThisYear=$true; notes='acceptance' } 'notes' 'updated asset'
    Assert-True ($asset.paymentMethod -eq 'Credit Card') 'Asset CRUD payment method was not preserved.'
    Assert-Close ([decimal]$asset.cost) 0 0.01 'Asset cost should clamp at zero.'
    Assert-Close ([decimal]$asset.businessUsePercent) 100 0.01 'Asset business use should clamp at 100.'
    Assert-CrudRoundTrip 'makerworld-rewards' @{ rewardDate='2026-05-30'; rewardType='Gift Card'; pointsChange=-100; giftCardAmount=-50; codeLast4='1234'; status='Available'; incomeStatus='Yes - Count as income'; notes='acceptance' } 'notes' 'updated makerworld' | Out-Null
    Assert-CrudRoundTrip 'audit-documents' @{ documentDate='2026-05-30'; documentType='Receipt'; relatedRecordType='Expense'; relatedRecordNumber='EXP-ACC'; fileName='acceptance.pdf'; filePathOrUrl='C:\acceptance.pdf'; notes='acceptance' } 'notes' 'updated audit doc' | Out-Null
    Assert-CrudRoundTrip 'business-accounts' @{ name='Acceptance Checking'; accountType='Checking'; institution='Acceptance Bank'; last4='1111'; openingBalance=-10; currentBalance=-5; isActive=$true; notes='acceptance' } 'notes' 'updated account' | Out-Null
    Assert-CrudRoundTrip 'action-items' @{ title='Acceptance action'; area='Tax'; priority='High'; dueDate='2026-06-01'; status='Open'; relatedRecord='ACC'; notes='acceptance' } 'status' 'Done' | Out-Null
    $taxObligation = Assert-CrudRoundTrip 'tax-obligations' @{ taxYear=2026; title='Acceptance tax obligation'; jurisdiction='Federal'; obligationType='Estimated Income Tax'; formName='1040-ES'; period='Q2'; dueDate='2026-06-15'; status='Review Applicability'; estimatedAmount=-20; amountPaid=-10; paymentMethod='Check'; appliesIf='Acceptance'; needsReview=$true; notes='acceptance' } 'status' 'Filed / Paid'
    Assert-True ($taxObligation.paymentMethod -eq 'Check') 'Tax Obligation CRUD payment method was not preserved.'
    Assert-CrudRoundTrip 'mileage-logs' @{ tripDate='2026-05-30'; vehicle='Acceptance Vehicle'; startLocation='Home'; endLocation='Post Office'; businessPurpose='Ship customer order'; businessMiles=-12; parkingAndTolls=-2; proofReference='calendar'; notes='acceptance' } 'notes' 'updated mileage' | Out-Null
    $setting = Invoke-Json POST '/api/settings' @{ key='AcceptanceSetting'; value='One'; notes='acceptance' }
    $setting.value = 'Two'
    $setting.isArchived = $true
    $updatedSetting = Invoke-Json PUT "/api/settings/$($setting.id)" $setting
    Assert-True ($updatedSetting.value -eq 'Two' -and $updatedSetting.isArchived -eq $false) 'Setting update did not persist or allowed archive-state injection.'
    $settingArchive = Invoke-Raw DELETE "/api/settings/$($setting.id)"
    Assert-True ($settingArchive.StatusCode -eq 409 -and $settingArchive.Content -like '*cannot be archived*') 'Settings archive safety contract was not enforced.'
    $settingRestore = Invoke-Raw POST "/api/settings/$($setting.id)/restore"
    Assert-True ($settingRestore.StatusCode -eq 409 -and $settingRestore.Content -like '*do not support archive or restore*') 'Settings restore safety contract was not enforced.'

    Write-Step 'Checking customer communication timeline and printer queue'
    $communication = Assert-CrudRoundTrip 'customer-communications' @{
        occurredAt='2026-06-10T09:30:00'
        customerName='Acceptance Customer'
        direction='Incoming'
        channel='Email'
        subject='Acceptance Queue Approval'
        summary='Customer approved the black PETG production run.'
        relatedJobNumber='JOB-QUEUE-ACC'
        relatedInvoiceNumber='INV-QUEUE-ACC'
        followUpDate='2026-06-11'
        followUpStatus='Open'
        sourceProof='acceptance-email.eml'
        notes='acceptance'
    } 'notes' 'updated communication'
    Assert-True ($communication.followUpStatus -eq 'Open') 'Communication follow-up status was not preserved.'

    $queueJob = Invoke-Json POST '/api/customer-jobs' @{
        jobDate='2026-06-10'
        customerName='Acceptance Customer'
        platform='Direct'
        jobNumber='JOB-QUEUE-ACC'
        relatedInvoiceNumber='INV-QUEUE-ACC'
        jobName='Acceptance Queue Job'
        jobType='Print'
        status='Open'
        productName='Acceptance Queue Part'
        material='PETG'
        color='Black'
    }
    $queueItem = Invoke-Json POST '/api/printer-queue-items' @{
        queueDate='2026-06-10'
        priority='High'
        status='Printing'
        printerName='Acceptance P1S'
        customerJobId=$queueJob.id
        customerName='Acceptance Customer'
        jobName='Acceptance Queue Job'
        relatedInvoiceNumber='INV-QUEUE-ACC'
        productName='Acceptance Queue Part'
        material='PETG'
        color='Black'
        quantity=0
        plateCount=0
        estimatedHours=-2
        progressPercent=-5
    }
    Assert-True ($queueItem.quantity -eq 1 -and $queueItem.plateCount -eq 1) 'Printer queue did not normalize quantity and plate count.'
    Assert-True ($queueItem.progressPercent -ge 1 -and $queueItem.startedAt) 'Printing queue item did not set progress/start time.'
    $queueJobPrinting = Invoke-Json GET "/api/customer-jobs/$($queueJob.id)"
    Assert-True ($queueJobPrinting.status -eq 'In Progress') 'Printing queue item did not update its linked Customer Job.'

    $queueItem.status = 'Completed'
    $queueItem.actualHours = 1.5
    $queueCompleted = Invoke-Json PUT "/api/printer-queue-items/$($queueItem.id)" $queueItem
    Assert-True ($queueCompleted.progressPercent -eq 100 -and $queueCompleted.completedAt) 'Completed queue item did not set 100% and completion time.'
    $queueJobCompleted = Invoke-Json GET "/api/customer-jobs/$($queueJob.id)"
    Assert-True ($queueJobCompleted.status -eq 'Completed') 'Completed queue item did not update its linked Customer Job.'

    $queueCompleted.status = 'Needs Attention'
    $queueCompleted.needsReview = $true
    $queueAttention = Invoke-Json PUT "/api/printer-queue-items/$($queueItem.id)" $queueCompleted
    Assert-True ($queueAttention.status -eq 'Needs Attention') 'Printer queue attention status did not persist.'
    $queueOverdue = Invoke-Json POST '/api/printer-queue-items' @{
        queueDate='2026-06-10'
        priority='High'
        status='Queued'
        printerName='Acceptance P1S'
        customerName='Acceptance Customer'
        jobName='Acceptance Overdue Queue Job'
        productName='Acceptance Overdue Part'
        material='PLA'
        color='Black'
        quantity=1
        plateCount=1
        scheduledStart='2000-01-02T09:00:00'
        estimatedFinish='2000-01-02T12:00:00'
        failureCount=0
        needsReview=$false
    }
    $queueFailed = Invoke-Json POST '/api/printer-queue-items' @{
        queueDate='2026-06-10'
        priority='Normal'
        status='Ready'
        printerName='Acceptance A1'
        customerName='Acceptance Customer'
        jobName='Acceptance Failed Queue Job'
        productName='Acceptance Failed Part'
        material='PETG'
        color='Blue'
        quantity=1
        plateCount=1
        scheduledStart='2099-01-02T09:00:00'
        estimatedFinish='2099-01-02T12:00:00'
        failureCount=2
        needsReview=$false
    }
    $queueCompletedFailed = Invoke-Json POST '/api/printer-queue-items' @{
        queueDate='2026-06-10'
        priority='Normal'
        status='Completed'
        printerName='Acceptance A1'
        customerName='Acceptance Customer'
        jobName='Acceptance Completed Failed Queue Job'
        productName='Acceptance Completed Failed Part'
        material='PETG'
        color='Gray'
        quantity=1
        plateCount=1
        scheduledStart='2000-01-02T09:00:00'
        estimatedFinish='2000-01-02T12:00:00'
        failureCount=3
        needsReview=$true
    }
    Assert-True ($queueOverdue.id -gt 0 -and $queueFailed.id -gt 0 -and $queueCompletedFailed.id -gt 0) 'Printer queue AI review fixtures did not save.'
    $operationsReview = Invoke-Json GET '/api/ai/review'
    Assert-True (@($operationsReview.items | Where-Object { $_.title -eq 'Customer communication follow-ups are due' }).Count -eq 1) 'AI review did not flag due customer communication follow-ups.'
    $queueReview = @($operationsReview.items | Where-Object { $_.title -eq 'Printer queue items need attention' })
    Assert-True ($queueReview.Count -eq 1) 'AI review did not flag printer queue items needing attention.'
    $queueReviewEvidence = ($queueReview | ForEach-Object { $_.evidence }) -join ' | '
    Assert-True ($queueReviewEvidence -like '*Acceptance Overdue Queue Job*' -and $queueReviewEvidence -like '*overdue*') 'AI review did not flag overdue printer queue items.'
    Assert-True ($queueReviewEvidence -like '*Acceptance Failed Queue Job*' -and $queueReviewEvidence -like '*2 failed attempts*') 'AI review did not flag failed printer queue items.'
    Assert-True ($queueReviewEvidence -notlike '*Acceptance Completed Failed Queue Job*') 'AI review incorrectly flagged a completed printer queue item.'
    $operationsTimeline = Invoke-Json GET '/api/job-timeline?q=Acceptance%20Queue'
    Assert-True (@($operationsTimeline.timelines.events | Where-Object { $_.kind -eq 'Communication' }).Count -ge 1) 'Job Timeline did not include customer communication events.'
    Assert-True (@($operationsTimeline.timelines.events | Where-Object { $_.kind -eq 'Printer Queue' }).Count -ge 1) 'Job Timeline did not include printer queue events.'

    Write-Step 'Checking proof/document intake upload'
    $classificationUpload = Upload-ProofTextFiles @(
        [pscustomobject]@{ FileName = 'acceptance-proof-sale.txt'; Text = 'Etsy order receipt. Order #ACC-SALE-001. Buyer paid. Customer paid. Order total $42.50. Tracking 9400111899000000000101.' },
        [pscustomobject]@{ FileName = 'acceptance-proof-expense.txt'; Text = 'Receipt for paid purchase from Office Depot. Subtotal $18.25 total $18.25 charged to business card.' },
        [pscustomobject]@{ FileName = 'acceptance-proof-asset.txt'; Text = 'Bambu P1S printer equipment purchase receipt. Serial BP1S-ACC. Total $699.00. Durable business property.' },
        [pscustomobject]@{ FileName = 'acceptance-proof-invoice.txt'; Text = 'Customer invoice INV-2026-ACC-PROOF. Amount due $120.00. Payment due on receipt for custom display stand.' },
        [pscustomobject]@{ FileName = 'acceptance-proof-estimate.txt'; Text = 'Estimate EST-2026-ACC-PROOF quote proposal valid until 2026-07-01. Total $250.00 for custom bracket.' },
        [pscustomobject]@{ FileName = 'acceptance-proof-bill.txt'; Text = 'Vendor bill from Filament Supplier. Payment terms net 30. Unpaid supplier statement. Amount due $88.00.' },
        [pscustomobject]@{ FileName = 'acceptance-proof-shipping.txt'; Text = 'USPS shipping label postage. Tracking 9400111899000000000102. Ship by tomorrow. Total $8.40.' },
        [pscustomobject]@{ FileName = 'acceptance-proof-review.txt'; Text = 'Studio note: remember to review the loose document and decide where it belongs later.' }
    )
    Assert-True ($classificationUpload.count -eq 8) 'Document intake classification upload did not create all Audit Docs.'
    foreach ($doc in @($classificationUpload.documents)) {
        $null = Assert-UploadedDocUnderRoot $doc "Classified proof upload $($doc.fileName)"
    }
    $suggestionsByFile = @{}
    foreach ($suggestion in @($classificationUpload.suggestions)) {
        $suggestionsByFile[$suggestion.fileName] = $suggestion
    }
    Assert-True ($suggestionsByFile['acceptance-proof-sale.txt'].lane -eq 'Sale' -and $suggestionsByFile['acceptance-proof-sale.txt'].suggestedRoute -eq 'sales' -and $suggestionsByFile['acceptance-proof-sale.txt'].suggestedKind -eq 'etsy') 'Document intake did not classify sale proof correctly.'
    Assert-True ($suggestionsByFile['acceptance-proof-expense.txt'].lane -like 'Expense*' -and $suggestionsByFile['acceptance-proof-expense.txt'].suggestedConfig -eq 'expenses') 'Document intake did not classify expense proof correctly.'
    Assert-True ($suggestionsByFile['acceptance-proof-asset.txt'].lane -eq 'Asset' -and $suggestionsByFile['acceptance-proof-asset.txt'].suggestedConfig -eq 'assets') 'Document intake did not classify asset proof correctly.'
    Assert-True ($suggestionsByFile['acceptance-proof-invoice.txt'].lane -eq 'Invoice' -and $suggestionsByFile['acceptance-proof-invoice.txt'].suggestedRoute -eq 'invoices') 'Document intake did not classify invoice proof correctly.'
    Assert-True ($suggestionsByFile['acceptance-proof-estimate.txt'].lane -eq 'Estimate' -and $suggestionsByFile['acceptance-proof-estimate.txt'].suggestedRoute -eq 'estimates') 'Document intake did not classify estimate proof correctly.'
    Assert-True ($suggestionsByFile['acceptance-proof-bill.txt'].lane -eq 'Bill / AP' -and $suggestionsByFile['acceptance-proof-bill.txt'].suggestedConfig -eq 'bills') 'Document intake did not classify bill proof correctly.'
    Assert-True ($suggestionsByFile['acceptance-proof-shipping.txt'].lane -eq 'Shipping / Sale Cost' -and $suggestionsByFile['acceptance-proof-shipping.txt'].suggestedConfig -eq 'sales') 'Document intake did not classify shipping proof correctly.'
    Assert-True ($suggestionsByFile['acceptance-proof-review.txt'].lane -eq 'Review' -and $suggestionsByFile['acceptance-proof-review.txt'].suggestedRoute -eq 'documentIntake') 'Document intake did not classify ambiguous proof as Review.'

    $uploadEdgeDocsBefore = @(Invoke-Json GET '/api/audit-documents?includeArchived=true').Count
    $uploadEdgeClient = [System.Net.Http.HttpClient]::new()

    $missingUploadForm = [System.Net.Http.MultipartFormDataContent]::new()
    $missingUploadForm.Add([System.Net.Http.StringContent]::new('Acceptance missing file edge'), 'relatedType')
    $missingUploadResponse = $uploadEdgeClient.PostAsync("$base/api/documents/upload", $missingUploadForm).GetAwaiter().GetResult()
    $missingUploadText = $missingUploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-True ([int]$missingUploadResponse.StatusCode -eq 400) "Missing proof upload expected 400, got $([int]$missingUploadResponse.StatusCode)."
    Assert-True ($missingUploadText -like '*Choose at least one file*') 'Missing proof upload did not explain that a file is required.'
    $missingUploadForm.Dispose()

    $zeroUploadForm = [System.Net.Http.MultipartFormDataContent]::new()
    $zeroUploadContent = [System.Net.Http.ByteArrayContent]::new([byte[]]::new(0))
    $zeroUploadContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/plain')
    $zeroUploadForm.Add($zeroUploadContent, 'files', 'acceptance-upload-empty.txt')
    $zeroUploadResponse = $uploadEdgeClient.PostAsync("$base/api/documents/upload", $zeroUploadForm).GetAwaiter().GetResult()
    $zeroUploadText = $zeroUploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-True ([int]$zeroUploadResponse.StatusCode -eq 400) "Zero-byte proof upload expected 400, got $([int]$zeroUploadResponse.StatusCode)."
    Assert-True ($zeroUploadText -like '*acceptance-upload-empty.txt*empty*') 'Zero-byte proof upload did not explain the empty file.'
    $zeroUploadForm.Dispose()
    $zeroUploadContent.Dispose()

    $unsupportedUploadForm = [System.Net.Http.MultipartFormDataContent]::new()
    $unsupportedUploadContent = [System.Net.Http.ByteArrayContent]::new([System.Text.Encoding]::UTF8.GetBytes('Acceptance unsupported proof upload'))
    $unsupportedUploadContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/octet-stream')
    $unsupportedUploadForm.Add($unsupportedUploadContent, 'files', 'acceptance-upload-unsupported.exe')
    $unsupportedUploadResponse = $uploadEdgeClient.PostAsync("$base/api/documents/upload", $unsupportedUploadForm).GetAwaiter().GetResult()
    $unsupportedUploadText = $unsupportedUploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-True ([int]$unsupportedUploadResponse.StatusCode -eq 400) "Unsupported proof upload expected 400, got $([int]$unsupportedUploadResponse.StatusCode)."
    Assert-True ($unsupportedUploadText -like '*acceptance-upload-unsupported.exe*not a supported proof upload type*') 'Unsupported proof upload did not explain supported file types.'
    $unsupportedUploadForm.Dispose()
    $unsupportedUploadContent.Dispose()

    $oversizedUploadForm = [System.Net.Http.MultipartFormDataContent]::new()
    $oversizedUploadBytes = [byte[]]::new((20 * 1024 * 1024) + 1)
    $oversizedUploadContent = [System.Net.Http.ByteArrayContent]::new($oversizedUploadBytes)
    $oversizedUploadContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/pdf')
    $oversizedUploadForm.Add($oversizedUploadContent, 'files', 'acceptance-upload-oversized.pdf')
    $oversizedUploadResponse = $uploadEdgeClient.PostAsync("$base/api/documents/upload", $oversizedUploadForm).GetAwaiter().GetResult()
    $oversizedUploadText = $oversizedUploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-True ([int]$oversizedUploadResponse.StatusCode -eq 400) "Oversized proof upload expected 400, got $([int]$oversizedUploadResponse.StatusCode)."
    Assert-True ($oversizedUploadText -like '*acceptance-upload-oversized.pdf*20 MB per-file proof upload limit*') 'Oversized proof upload did not explain the proof upload limit.'
    $oversizedUploadForm.Dispose()
    $oversizedUploadContent.Dispose()
    $uploadEdgeClient.Dispose()

    $uploadEdgeDocsAfter = @(Invoke-Json GET '/api/audit-documents?includeArchived=true').Count
    Assert-True ($uploadEdgeDocsAfter -eq $uploadEdgeDocsBefore) 'Rejected proof uploads created Audit Document rows.'
    $uploadEdgeFiles = @(Get-ChildItem -LiteralPath (Join-Path $root 'UploadedDocs') -Filter 'acceptance-upload-*' -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'acceptance-upload-proof.txt' })
    Assert-True ($uploadEdgeFiles.Count -eq 0) "Rejected proof uploads wrote files: $($uploadEdgeFiles.Name -join ', ')"

    $upload = Upload-ProofFile
    Assert-True ($upload.count -eq 1) 'Document upload did not create one audit document.'
    Assert-True ($upload.documents[0].id -gt 0) 'Document upload did not return an audit document id.'
    Assert-True ($upload.documents[0].fileName -eq 'acceptance-upload-proof.txt') 'Document upload returned the wrong filename.'
    $null = Assert-UploadedDocUnderRoot $upload.documents[0] 'Normal proof upload'
    Assert-True ($upload.suggestions[0].engine -eq 'Local rules') 'Document suggestion did not identify its local-rules engine.'
    Assert-True ($upload.suggestions[0].usedAi -eq $false) 'Document suggestion incorrectly claimed model AI use.'
    Assert-True ($upload.suggestions[0].doesNot -like '*Does not create or save*') 'Document suggestion did not disclose its write boundary.'
    $uploadAuditEditPayload = @{}
    foreach ($property in $upload.documents[0].PSObject.Properties) {
        $uploadAuditEditPayload[$property.Name] = $property.Value
    }
    $uploadAuditEditPayload['documentType'] = 'Invoice'
    $uploadAuditEditPayload['relatedRecordType'] = 'Invoice'
    $uploadAuditEditPayload['relatedRecordNumber'] = 'INV-ACCEPT-UPLOAD'
    $uploadAuditEditPayload['needsReview'] = $false
    $uploadAuditEditPayload['notes'] = 'Acceptance edited from the Document Intake Edit Audit Doc flow.'
    $uploadAuditEdit = Invoke-Json PUT "/api/audit-documents/$($upload.documents[0].id)" $uploadAuditEditPayload
    Assert-True ($uploadAuditEdit.documentType -eq 'Invoice') 'Edit Audit Doc did not persist document type.'
    Assert-True ($uploadAuditEdit.relatedRecordType -eq 'Invoice' -and $uploadAuditEdit.relatedRecordNumber -eq 'INV-ACCEPT-UPLOAD') 'Edit Audit Doc did not persist related record fields.'
    Assert-True ($uploadAuditEdit.needsReview -eq $false) 'Edit Audit Doc did not clear Needs Review.'
    $uploadAuditReloaded = Invoke-Json GET "/api/audit-documents/$($upload.documents[0].id)"
    Assert-True ($uploadAuditReloaded.notes -eq 'Acceptance edited from the Document Intake Edit Audit Doc flow.') 'Edited Audit Doc did not reload saved notes.'

    $specialUploadName = "acceptance Bob's order (final proof).txt"
    $specialUploadClient = [System.Net.Http.HttpClient]::new()
    $specialUploadForm = [System.Net.Http.MultipartFormDataContent]::new()
    $specialUploadContent = [System.Net.Http.ByteArrayContent]::new([System.Text.Encoding]::UTF8.GetBytes('Acceptance special proof filename content'))
    $specialUploadContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/plain')
    $specialUploadForm.Add($specialUploadContent, 'files', $specialUploadName)
    $specialUploadResponse = $specialUploadClient.PostAsync("$base/api/documents/upload", $specialUploadForm).GetAwaiter().GetResult()
    $specialUploadText = $specialUploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-True ($specialUploadResponse.IsSuccessStatusCode) "Special proof filename upload failed with $([int]$specialUploadResponse.StatusCode): $specialUploadText"
    $specialUpload = $specialUploadText | ConvertFrom-Json
    Assert-True ($specialUpload.count -eq 1) 'Special proof filename upload did not create one audit document.'
    Assert-True ($specialUpload.documents[0].fileName -eq $specialUploadName) 'Special proof filename was not preserved on the Audit Document.'
    $specialUploadPath = Assert-UploadedDocUnderRoot $specialUpload.documents[0] 'Special proof upload'
    Assert-True ((Split-Path -Leaf $specialUploadPath) -like "*$specialUploadName") 'Special proof stored filename did not retain spaces, parentheses, and apostrophe.'
    $specialUploadFile = Invoke-Raw GET "/api/audit-documents/$($specialUpload.documents[0].id)/file"
    Assert-True ($specialUploadFile.StatusCode -eq 200) 'Special proof filename could not be read back through the Audit Doc file endpoint.'
    Assert-True ($specialUploadFile.Content -like '*Acceptance special proof filename content*') 'Special proof filename content did not round-trip.'
    $specialUploadClient.Dispose()
    $specialUploadForm.Dispose()
    $specialUploadContent.Dispose()

    Write-Step 'Checking estimate and invoice builder workflows'
    $nextEstimate = Invoke-Json GET '/api/documents/next-number?type=ESTIMATE'
    Assert-True ($nextEstimate.number -like 'EST-*') 'Next estimate number should be EST-*.' 
    $estimate = Invoke-Json POST '/api/documents' (New-DocPayload 'ESTIMATE' 'Sent' $nextEstimate.number 500 'Acceptance Estimate Customer')
    Assert-EstimateMoney $estimate 'Estimate create'
    $mutatedEstimatePayload = New-DocPayload 'INVOICE' 'Paid' $estimate.docNumber.Replace('EST-', 'INV-') 110 'Acceptance Estimate Customer'
    $mutatedEstimateSave = Invoke-WebRequest -Method PUT -Uri "$base/api/documents/$($estimate.id)" -ContentType 'application/json' -Body ($mutatedEstimatePayload | ConvertTo-Json -Depth 20) -UseBasicParsing -SkipHttpErrorCheck
    Assert-True ($mutatedEstimateSave.StatusCode -eq 400) 'Changing a saved estimate into an invoice by PUT should return 400, not 500.'
    $estimateAfterFailedTypeChange = Invoke-Json GET "/api/documents/$($estimate.id)"
    Assert-True ($estimateAfterFailedTypeChange.docType -eq 'ESTIMATE') 'Failed type-change save changed the original estimate type.'
    Assert-True ($estimateAfterFailedTypeChange.docNumber -eq $estimate.docNumber) 'Failed type-change save changed the original estimate number.'

    $duplicate = Invoke-Json POST "/api/documents/$($estimate.id)/duplicate"
    Assert-True ($duplicate.id -ne $estimate.id) 'Document duplicate reused source id.'
    Assert-True ($duplicate.docType -eq 'ESTIMATE') 'Estimate duplicate changed doc type.'
    Assert-EstimateMoney $duplicate 'Estimate duplicate'

    $invoiceFromEstimate = Invoke-Json POST "/api/invoice-documents/$($estimate.id)/convert-to-invoice"
    Assert-True ($invoiceFromEstimate.docType -eq 'INVOICE') 'Convert to invoice did not change doc type.'
    Assert-True ($invoiceFromEstimate.docNumber -like 'INV-*') 'Convert to invoice did not assign INV number.'
    Assert-DocMoney $invoiceFromEstimate 0 'Converted invoice'

    $nextInvoice = Invoke-Json GET '/api/invoice-documents/next-number?type=INVOICE'
    Assert-True ($nextInvoice.number -like 'INV-*') 'Next invoice number should be INV-*.' 
    $paidInvoice = Invoke-Json POST '/api/invoice-documents' (New-DocPayload 'INVOICE' 'Paid' $nextInvoice.number 110 'Acceptance Paid Customer')
    Assert-DocMoney $paidInvoice 110 'Paid invoice create'

    $sales = Invoke-Json GET '/api/sales?includeArchived=true'
    $paidSale = @($sales | Where-Object { $_.invoiceNumber -eq $paidInvoice.docNumber })[0]
    Assert-True ($null -ne $paidSale) 'Paid invoice did not sync to Sales.'
    Assert-Close ([decimal]$paidSale.itemSales) 100 0.01 'Paid invoice sale item sales allocation'
    Assert-Close ([decimal]$paidSale.salesTaxCollected) 10 0.01 'Paid invoice sale tax allocation'
    Assert-Close ([decimal]$paidSale.customerPaid) 110 0.01 'Paid invoice sale customer paid'
    Assert-True ($paidSale.paymentMethod -eq 'Zelle') 'Paid invoice payment method did not sync to Sales.'

    $arRows = Invoke-Json GET '/api/receivable-invoices?includeArchived=true'
    $paidAr = @($arRows | Where-Object { $_.invoiceNumber -eq $paidInvoice.docNumber })[0]
    Assert-True ($null -ne $paidAr) 'Paid invoice did not sync to AR.'
    Assert-Close ([decimal]$paidAr.invoiceTotal) 110 0.01 'Paid invoice AR total'
    Assert-Close ([decimal]$paidAr.amountPaid) 110 0.01 'Paid invoice AR paid'
    Assert-True ($paidAr.paymentMethod -eq 'Zelle') 'Paid invoice payment method did not sync to AR.'

    $partialPayload = New-DocPayload 'INVOICE' 'Partial' 'INV-ACC-PARTIAL' 55 'Acceptance Partial Customer'
    $partialInvoice = Invoke-Json POST '/api/documents' $partialPayload
    Assert-DocMoney $partialInvoice 55 'Partial invoice create'
    $sales = Invoke-Json GET '/api/sales?includeArchived=true'
    $partialSale = @($sales | Where-Object { $_.invoiceNumber -eq $partialInvoice.docNumber })[0]
    Assert-Close ([decimal]$partialSale.itemSales) 50 0.01 'Partial invoice sale item sales allocation'
    Assert-Close ([decimal]$partialSale.salesTaxCollected) 5 0.01 'Partial invoice sale tax allocation'
    Assert-Close ([decimal]$partialSale.customerPaid) 55 0.01 'Partial invoice sale paid'

    $partialPayload.status = 'Void'
    $partialPayload.amountPaid = 0
    $voidInvoice = Invoke-Json PUT "/api/documents/$($partialInvoice.id)" $partialPayload
    Assert-Close ([decimal]$voidInvoice.amountPaid) 0 0.01 'Void invoice paid should be zero.'
    $sales = Invoke-Json GET '/api/sales?includeArchived=true'
    $voidSale = @($sales | Where-Object { $_.invoiceNumber -eq $partialInvoice.docNumber })[0]
    Assert-True ($voidSale.includeInDashboard -eq $false) 'Voiding invoice should hide synced Sale from dashboard.'

    Invoke-Json DELETE "/api/documents/$($duplicate.id)" | Out-Null
    $arRows = Invoke-Json GET '/api/receivable-invoices?includeArchived=true'
    Assert-True (-not (@($arRows | Where-Object { $_.invoiceNumber -eq $duplicate.docNumber -and -not $_.isArchived }).Count)) 'Deleted duplicate left active AR rows.'

    Invoke-Json GET '/api/documents/latest' | Out-Null
    $stats = Invoke-Json GET '/api/documents/stats'
    Assert-True ($stats.totalInvoices -ge 3) 'Document stats did not count created invoices.'
    Assert-True ($stats.totalInvoiced -ge 110) 'Document stats did not include active invoice totals.'

    Write-Step 'Checking legacy import failure path is clean'
    Assert-True $backupCopyPreflightCompleted 'Round 64 backup preflight did not run before import checks.'
    $legacy = Invoke-Json POST '/api/invoice-documents/import-from-legacy'
    Assert-True ($legacy.success -eq $false) 'Legacy import without old app should fail gracefully, not claim success.'

    Write-Step 'Checking exports and backups'
    Assert-True $backupCopyPreflightCompleted 'Round 64 backup preflight did not run before export/backup checks.'
    foreach ($entity in @('parties', 'sales', 'customer-jobs', 'customer-communications', 'printer-queue-items', 'receivable-invoices', 'bills', 'expenses', 'products', 'assets', 'makerworld-rewards', 'audit-documents', 'business-accounts', 'action-items', 'tax-obligations', 'mileage-logs')) {
        $csv = Invoke-Raw GET "/api/export/$entity"
        Assert-True ($csv.StatusCode -eq 200) "CSV export failed for $entity."
        Assert-True ($csv.Content.Length -gt 10) "CSV export for $entity looked empty."
    }
    foreach ($group in @('all', 'noncash', 'digital', 'card', 'online', 'cash', 'other', 'unknown')) {
        $csv = Invoke-Raw GET "/api/export/tax-sales?paymentGroup=$group"
        Assert-True ($csv.StatusCode -eq 200) "Tax sales export failed for payment group $group."
        Assert-True ($csv.Content -like '*PaymentGroup*') "Tax sales export for $group did not include PaymentGroup."
    }
    $digitalTaxCsv = Invoke-Raw GET '/api/export/tax-sales?paymentGroup=digital'
    Assert-True ($digitalTaxCsv.Content -like '*Zelle*') 'Digital-transfer tax export did not include the paid Zelle invoice.'
    $cardTaxCsv = Invoke-Raw GET '/api/export/tax-sales?paymentGroup=card'
    Assert-True ($cardTaxCsv.Content -like '*Credit Card*') 'Card tax export did not include the credit-card sale.'
    $njSalesTaxCsv = Invoke-Raw GET '/api/export/nj-sales-tax?year=2026'
    Assert-True ($njSalesTaxCsv.StatusCode -eq 200) 'NJ sales-tax review export failed.'
    Assert-True ($njSalesTaxCsv.Content -like '*Seller Collected*') 'NJ sales-tax review export did not include seller-collected handling.'
    $taxSummaryCsv = Invoke-Raw GET '/api/export/tax-summary?year=2026'
    Assert-True ($taxSummaryCsv.Content -like '*WorkingNetProfit*') 'Tax summary export did not include working net profit.'
    $taxPackage = Invoke-Raw GET '/api/export/tax-package?year=2026'
    Assert-True ($taxPackage.StatusCode -eq 200) 'Tax package ZIP export failed.'
    Assert-True ($taxPackage.Content.Length -gt 500) 'Tax package ZIP looked empty.'
    $backup = Invoke-Raw GET '/api/database/backup'
    Assert-True ($backup.StatusCode -eq 200) 'Database backup endpoint failed.'
    $systemBackup = Invoke-Raw POST '/api/system/backup'
    Assert-True ($systemBackup.StatusCode -eq 200) 'System backup endpoint failed.'
    Assert-True ($systemBackup.Content.Length -gt 100) 'System backup response looked empty.'
    Assert-True $backupCopyPreflightCompleted 'Round 64 backup preflight did not run before export/backup checks.'

    Write-Step 'Checking dashboard and tax calculation outputs'
    $dashboard = Invoke-Json GET '/api/dashboard'
    Assert-True ($dashboard.kpis.grossReceipts -gt 0) 'Dashboard gross receipts should reflect acceptance sales.'
    Assert-True ($dashboard.kpis.estimatedNet -ne $null) 'Dashboard estimatedNet missing.'
    foreach ($key in @('grossReceipts','estimatedNet','openReceivables','openPayables','customerPaid','salesTaxMemo','knownCosts','needsReview')) {
        Assert-True ($null -ne $dashboard.breakdowns.$key) "Dashboard breakdown $key is missing."
    }
    Assert-Close ([decimal]$dashboard.breakdowns.grossReceipts.total) ([decimal]$dashboard.kpis.grossReceipts) 0.01 'Dashboard gross receipts breakdown total'
    Assert-Close ([decimal]$dashboard.breakdowns.estimatedNet.total) ([decimal]$dashboard.kpis.estimatedNet) 0.01 'Dashboard estimated net breakdown total'
    Assert-Close ([decimal]$dashboard.breakdowns.openReceivables.total) ([decimal]$dashboard.kpis.openReceivables) 0.01 'Dashboard open AR breakdown total'
    Assert-Close ([decimal]$dashboard.breakdowns.openPayables.total) ([decimal]$dashboard.kpis.openPayables) 0.01 'Dashboard open AP breakdown total'
    Assert-Close ([decimal]$dashboard.breakdowns.customerPaid.total) ([decimal]$dashboard.kpis.customerPaid) 0.01 'Dashboard customer paid breakdown total'
    Assert-Close ([decimal]$dashboard.breakdowns.salesTaxMemo.total) ([decimal]$dashboard.kpis.salesTaxMemo) 0.01 'Dashboard sales-tax memo breakdown total'
    Assert-Close ([decimal]$dashboard.breakdowns.knownCosts.total) ([decimal]$dashboard.kpis.sellingCosts + [decimal]$dashboard.kpis.directExpenses) 0.01 'Dashboard known-costs breakdown total'
    Assert-True ([decimal]$dashboard.breakdowns.needsReview.total -eq [decimal]$dashboard.kpis.needsReviewCount) 'Dashboard Needs Review breakdown total does not match KPI.'
    $taxAudit = Invoke-Json GET '/api/tax-audit'
    Assert-True ($null -ne $taxAudit.summary) 'Tax audit summary missing.'
    $highTaxIssues = @($taxAudit.issues | Where-Object { $_.severity -eq 'High' })
    if ($highTaxIssues.Count -gt 0) {
        Write-Host '[acceptance] High tax issues:'
        $highTaxIssues | ConvertTo-Json -Depth 5 | Write-Host
    }
    Assert-True ($highTaxIssues.Count -eq 0) 'Tax audit has high severity issues in disposable acceptance DB.'

    Write-Step 'Checking API aliases agree'
    $docsA = Invoke-Json GET '/api/documents'
    $docsB = Invoke-Json GET '/api/invoice-documents'
    Assert-True (@($docsA).Count -eq @($docsB).Count) 'Documents and invoice-documents aliases returned different counts.'

    Write-Step 'Acceptance suite passed'
} catch {
    if ($serverJob) {
        $jobOutput = Receive-Job -Job $serverJob -Keep -ErrorAction SilentlyContinue
        if ($jobOutput) {
            Write-Host '[acceptance] Server output before failure:'
            Write-Host $jobOutput
        }
    }
    throw
} finally {
    if ($serverJob) {
        Stop-Job -Job $serverJob -ErrorAction SilentlyContinue | Out-Null
        Receive-Job -Job $serverJob -ErrorAction SilentlyContinue | Out-Null
        Remove-Job -Job $serverJob -Force -ErrorAction SilentlyContinue | Out-Null
    }

    foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $backupDownloadPath, "$backupDownloadPath-shm", "$backupDownloadPath-wal", $manualCopyPath, "$manualCopyPath-shm", "$manualCopyPath-wal", $uploadProbe, $aiDocxProbe, $aiPdfProbe, $aiInvoicePdfProbe, $aiEstimatePdfProbe)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
    Get-ChildItem -LiteralPath (Join-Path $root 'UploadedDocs') -Filter '*acceptance*proof*.txt' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $backupDir) {
        $before = @{}
        foreach ($path in $backupFilesBefore) {
            $before[$path] = $true
        }
        Get-ChildItem -LiteralPath $backupDir -Filter 'epata-business-ledger-*.db' -File -ErrorAction SilentlyContinue |
            Where-Object { -not $before.ContainsKey($_.FullName) } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
