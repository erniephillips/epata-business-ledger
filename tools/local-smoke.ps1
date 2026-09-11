param(
    [int]$Port = 5113,
    [switch]$KeepFailureArtifacts
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$base = "http://127.0.0.1:$Port"
$dbPath = Join-Path $root 'Data\local-smoke.db'
$backupDownloadPath = Join-Path $root 'Data\local-smoke-app-backup-rehearsal.db'
$manualCopyPath = Join-Path $root 'Data\local-smoke-manual-copy-rehearsal.db'
$outPath = Join-Path $root 'local-smoke.out.log'
$errPath = Join-Path $root 'local-smoke.err.log'
$dllPath = Join-Path $root 'obj\verify-build\EPATA.BusinessLedger.dll'
$server = $null
$backupFilesBefore = @()
$uploadedDocsBefore = @()
$backupCopyPreflightCompleted = $false
$smokeSucceeded = $false

function Assert-Smoke($condition, $message) {
    if (-not $condition) {
        throw $message
    }
}

function Invoke-SmokeBackupCopyRehearsal([string]$label) {
    Remove-Item -LiteralPath $backupDownloadPath, $manualCopyPath -Force -ErrorAction SilentlyContinue

    $backup = Invoke-WebRequest -Method POST -Uri "$base/api/system/backup" -UseBasicParsing -OutFile $backupDownloadPath -PassThru
    Assert-Smoke ($backup.StatusCode -eq 200) "$label system backup returned $($backup.StatusCode)."
    Assert-Smoke (Test-Path -LiteralPath $backupDownloadPath) "$label backup download was not created."
    Assert-Smoke ((Get-Item -LiteralPath $backupDownloadPath).Length -gt 100) "$label backup file looked empty."
    $backupSignature = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($backupDownloadPath), 0, 15)
    Assert-Smoke ($backupSignature -eq 'SQLite format 3') "$label did not download a SQLite database. Signature: $backupSignature"
    $dataRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'Data'))
    $dataRootWithSep = if ($dataRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $dataRoot
    } else {
        $dataRoot + [System.IO.Path]::DirectorySeparatorChar
    }
    $manualCopyFullPath = [System.IO.Path]::GetFullPath($manualCopyPath)
    $productionDbPath = [System.IO.Path]::GetFullPath((Join-Path $root ('Data\' + 'epata-business-ledger' + '.db')))
    Assert-Smoke ($manualCopyFullPath.StartsWith($dataRootWithSep, [System.StringComparison]::OrdinalIgnoreCase)) "$label manual-copy target escaped Data: $manualCopyFullPath"
    Assert-Smoke (-not $manualCopyFullPath.Equals($productionDbPath, [System.StringComparison]::OrdinalIgnoreCase)) "$label manual-copy target would overwrite the production database."
    Copy-Item -LiteralPath $backupDownloadPath -Destination $manualCopyPath -Force
    Assert-Smoke (Test-Path -LiteralPath $manualCopyPath) "$label manual-copy file was not created."
    $backupHash = (Get-FileHash -LiteralPath $backupDownloadPath -Algorithm SHA256).Hash
    $copyHash = (Get-FileHash -LiteralPath $manualCopyPath -Algorithm SHA256).Hash
    Assert-Smoke ($copyHash -eq $backupHash) "$label manual-copy file does not match the app-created backup."
}

function Assert-SmokeNoSecretMaterial([string]$label, [string]$text) {
    $content = if ($null -eq $text) { '' } else { $text }
    $patterns = @(
        @{ Name = 'OpenAI-style key'; Pattern = 'sk-[A-Za-z0-9_-]{20,}' },
        @{ Name = 'generic secret assignment'; Pattern = '(?i)\b(api[_-]?key|secret|token|password)\b\s*[:=]\s*["''][^"'']{8,}' },
        @{ Name = 'authorization bearer header'; Pattern = '(?i)\bAuthorization\s*:\s*Bearer\s+[A-Za-z0-9._-]{12,}' },
        @{ Name = 'connection string'; Pattern = '(?i)\b(ConnectionStrings?:|Data Source=|Password=|User ID=|Uid=|Pwd=)' }
    )

    foreach ($pattern in $patterns) {
        Assert-Smoke ($content -notmatch $pattern.Pattern) "$label exposed $($pattern.Name)."
    }
}

function Assert-SmokeClose($actual, [decimal]$expected, [decimal]$tolerance, [string]$message) {
    $actualDecimal = Get-SmokeDecimalOrZero $actual
    if ([math]::Abs($actualDecimal - $expected) -gt $tolerance) {
        throw "$message Expected $expected, got $actualDecimal."
    }
}

function Get-Text($path) {
    return (Invoke-WebRequest -Uri "$base$path" -UseBasicParsing).Content
}

function Assert-SmokeCsvHeaders([string]$path, [string[]]$expectedHeaders) {
    $response = Invoke-WebRequest -Uri "$base$path" -UseBasicParsing
    Assert-Smoke ($response.StatusCode -eq 200) "CSV export $path returned $($response.StatusCode)."
    $content = [string]$response.Content
    Assert-Smoke (-not [string]::IsNullOrWhiteSpace($content)) "CSV export $path returned empty content."

    try {
        $rows = @($content | ConvertFrom-Csv)
    }
    catch {
        throw "CSV export $path could not be parsed by ConvertFrom-Csv: $($_.Exception.Message)"
    }

    if ($rows.Count -gt 0) {
        $actualHeaders = @($rows[0].PSObject.Properties.Name)
    } else {
        $firstLine = @($content -split "\r?\n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })[0]
        $actualHeaders = @($firstLine -split ',')
    }

    foreach ($header in $expectedHeaders) {
        Assert-Smoke ($actualHeaders -contains $header) "CSV export $path is missing expected header $header. Actual: $($actualHeaders -join ', ')"
    }
}

function Get-SmokeJsonArray([string]$path) {
    $value = Invoke-RestMethod "$base$path"
    if ($null -eq $value) {
        return
    }
    if ($value -is [array]) {
        foreach ($item in $value) {
            $item
        }
        return
    }
    $value
}

function Get-SmokeBytes([string]$path) {
    $client = [System.Net.Http.HttpClient]::new()
    try {
        $response = $client.GetAsync("$base$path").GetAwaiter().GetResult()
        Assert-Smoke ($response.IsSuccessStatusCode) "Expected successful binary response for $path, got $([int]$response.StatusCode)."
        return $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    }
    finally {
        $client.Dispose()
    }
}

function Get-SmokeZipEntryText([byte[]]$bytes, [string]$entryName) {
    $stream = [System.IO.MemoryStream]::new($bytes)
    try {
        $zip = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Read)
        try {
            $entry = $zip.GetEntry($entryName)
            Assert-Smoke ($null -ne $entry) "Tax package did not include $entryName."
            $reader = [System.IO.StreamReader]::new($entry.Open())
            try {
                return $reader.ReadToEnd()
            }
            finally {
                $reader.Dispose()
            }
        }
        finally {
            $zip.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

function Assert-SmokeUploadedDocUnderRoot($doc, [string]$label) {
    $uploadRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'UploadedDocs'))
    $uploadRootWithSep = if ($uploadRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $uploadRoot
    } else {
        $uploadRoot + [System.IO.Path]::DirectorySeparatorChar
    }
    $fullPath = [System.IO.Path]::GetFullPath([string]$doc.filePathOrUrl)
    Assert-Smoke ($fullPath.StartsWith($uploadRootWithSep, [System.StringComparison]::OrdinalIgnoreCase)) "$label escaped UploadedDocs: $fullPath"
    Assert-Smoke (Test-Path -LiteralPath $fullPath) "$label was not saved under UploadedDocs."
    return $fullPath
}

function Wait-SmokeHealth {
    $ready = $false
    $health = $null
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $health = Invoke-RestMethod "$base/api/health"
            $ready = $true
            break
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    Assert-Smoke $ready "Smoke app did not become ready on $base."
    return $health
}

function New-SavePayloadFromPrefill($prefill, [string]$paymentMethod = 'Unknown / Review') {
    $lineItems = @($prefill.lineItems | ForEach-Object {
        @{
            sortOrder = 0
            description = $_.description
            details = $_.details
            quantity = [decimal]$_.quantity
            rate = [decimal]$_.rate
            amount = [decimal]$_.quantity * [decimal]$_.rate
        }
    })

    return [ordered]@{
        docNumber = $prefill.docNumber
        docType = $prefill.docType
        status = $prefill.status
        customerName = $prefill.customerName
        customerPhone = $prefill.customerPhone
        customerAddress = $prefill.customerAddress
        customerEmail = $prefill.customerEmail
        preparedFor = $prefill.preparedFor
        projectName = $prefill.projectName
        material = $prefill.material
        color = $prefill.color
        infill = $prefill.infill
        projectDescription = $prefill.projectDescription
        projectNotes = $prefill.projectNotes
        pageSize = $prefill.pageSize
        docDate = $prefill.docDate
        dueDate = $prefill.dueDate
        subtotal = 0
        discountAmount = [decimal]$prefill.docDiscount
        rushAmount = 0
        taxAmount = 0
        total = 0
        amountPaid = [decimal]$prefill.amountPaid
        balance = 0
        paymentMethod = $paymentMethod
        pricingGuide = $prefill.pricingGuide
        termsNotes = $prefill.termsNotes
        standardTurnaround = $prefill.standardTurnaround
        rushTurnaround = $prefill.rushTurnaround
        calcGrams = [decimal]$prefill.calcGrams
        calcHours = [decimal]$prefill.calcHours
        calcDesignHours = [decimal]$prefill.calcDesignHours
        calcSetupFee = [decimal]$prefill.calcSetupFee
        calcPostFee = [decimal]$prefill.calcPostFee
        calcGramRate = [decimal]$prefill.calcGramRate
        calcHourRate = [decimal]$prefill.calcHourRate
        calcDesignRate = [decimal]$prefill.calcDesignRate
        calcMinimum = [decimal]$prefill.calcMinimum
        calcDifficulty = [decimal]$prefill.calcDifficulty
        calcRush = [decimal]$prefill.docRushPercent
        calcDiscount = [decimal]$prefill.docDiscount
        calcTaxRate = [decimal]$prefill.docTaxRate
        lineItems = $lineItems
        json = '{}'
    }
}

function Get-SmokeProp($object, [string]$name) {
    return $object.PSObject.Properties[$name].Value
}

function Get-SmokeDecimalOrZero($value) {
    if ($null -eq $value) {
        return [decimal]0
    }
    if ($value -is [array]) {
        if ($value.Count -eq 0) {
            return [decimal]0
        }
        return Get-SmokeDecimalOrZero $value[0]
    }
    if ($value -is [string] -and [string]::IsNullOrWhiteSpace($value)) {
        return [decimal]0
    }
    return [decimal]$value
}

function Copy-SmokePayload($payload) {
    $copy = [ordered]@{}
    foreach ($key in $payload.Keys) {
        $copy[$key] = $payload[$key]
    }
    return $copy
}

function Invoke-SmokeJson([string]$method, [string]$path, $body) {
    try {
        return Invoke-RestMethod `
            -Method $method `
            -Uri "$base$path" `
            -ContentType 'application/json' `
            -Body ($body | ConvertTo-Json -Depth 20)
    }
    catch {
        throw "$method $path failed: $($_.Exception.Message)"
    }
}

function Invoke-SmokeProofTextUpload($files) {
    $client = [System.Net.Http.HttpClient]::new()
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    try {
        foreach ($file in $files) {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes([string]$file.Text)
            $content = [System.Net.Http.ByteArrayContent]::new($bytes)
            $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/plain')
            $form.Add($content, 'files', [string]$file.FileName)
        }

        $response = $client.PostAsync("$base/api/documents/upload", $form).GetAwaiter().GetResult()
        $text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        Assert-Smoke ($response.IsSuccessStatusCode) "Proof text upload expected success, got $([int]$response.StatusCode): $text"
        return $text | ConvertFrom-Json
    }
    finally {
        $form.Dispose()
        $client.Dispose()
    }
}

function New-SmokeDocumentPayload(
    [string]$docNumber,
    [string]$docType,
    [string]$status,
    [string]$customer,
    [string]$project,
    [decimal]$rate,
    [decimal]$amountPaid = 0,
    [decimal]$taxRate = 10
) {
    return [ordered]@{
        docNumber = $docNumber
        docType = $docType
        status = $status
        customerName = $customer
        customerPhone = '5551234567'
        customerAddress = '4 Workflow Lane'
        customerEmail = 'round4@example.test'
        preparedFor = $customer
        projectName = $project
        material = 'PLA'
        color = 'Black'
        infill = '20%'
        projectDescription = 'Round 4 invoice workflow smoke'
        projectNotes = 'Round 4 disposable workflow coverage'
        pageSize = 'Letter'
        docDate = '2026-06-19'
        dueDate = '2026-06-26'
        subtotal = 0
        discountAmount = 0
        rushAmount = 0
        taxAmount = 0
        total = 0
        amountPaid = $amountPaid
        balance = 0
        paymentMethod = 'Cash'
        pricingGuide = 'Round 4 smoke'
        termsNotes = 'Round 4 smoke terms'
        standardTurnaround = '5 business days'
        rushTurnaround = '2 business days'
        calcGrams = 10
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
        calcTaxRate = $taxRate
        lineItems = @(@{
            sortOrder = 0
            description = $project
            details = 'Round 4 line item'
            quantity = 1
            rate = $rate
            amount = $rate
        })
        json = '{}'
    }
}

function Test-CrudRoundTrip([string]$route, $payload, [string]$verifyProperty, $expectedValue, [string]$updateProperty, $updatedValue, [string]$exportNeedle) {
    Assert-Smoke $backupCopyPreflightCompleted "Round 64 backup preflight did not run before $route CRUD/archive/restore checks."

    $created = Invoke-SmokeJson 'POST' "/api/$route" $payload
    Assert-Smoke ($created.id -gt 0) "$route create did not return an id."
    Assert-Smoke ((Get-SmokeProp $created $verifyProperty) -eq $expectedValue) "$route create did not preserve $verifyProperty."

    $loaded = Invoke-RestMethod "$base/api/$route/$($created.id)"
    Assert-Smoke ($loaded.id -eq $created.id) "$route load-by-id returned the wrong row."
    Assert-Smoke ((Get-SmokeProp $loaded $verifyProperty) -eq $expectedValue) "$route load-by-id did not preserve $verifyProperty."
    Assert-Smoke ($loaded.createdAtUtc) "$route did not set CreatedAtUtc."
    Assert-Smoke ($loaded.updatedAtUtc) "$route did not set UpdatedAtUtc."

    $updatePayload = Copy-SmokePayload $payload
    $updatePayload[$updateProperty] = $updatedValue
    $updated = Invoke-SmokeJson 'PUT' "/api/$route/$($created.id)" $updatePayload
    Assert-Smoke ((Get-SmokeProp $updated $updateProperty) -eq $updatedValue) "$route update did not preserve $updateProperty."
    Assert-Smoke ($updated.createdAtUtc -eq $loaded.createdAtUtc) "$route update changed CreatedAtUtc."
    Assert-Smoke ($updated.updatedAtUtc) "$route update did not return UpdatedAtUtc."

    $activeRows = @(Invoke-RestMethod "$base/api/$route")
    Assert-Smoke (@($activeRows | Where-Object { $_.id -eq $created.id }).Count -eq 1) "$route list did not show active row."

    $delete = Invoke-WebRequest -Method DELETE -Uri "$base/api/$route/$($created.id)" -SkipHttpErrorCheck
    Assert-Smoke ($delete.StatusCode -eq 204) "$route archive expected 204, got $($delete.StatusCode)."

    $hiddenRows = @(Invoke-RestMethod "$base/api/$route")
    Assert-Smoke (@($hiddenRows | Where-Object { $_.id -eq $created.id }).Count -eq 0) "$route archived row still appeared in default list."

    $archivedRows = @(Invoke-RestMethod "$base/api/${route}?includeArchived=true")
    Assert-Smoke (@($archivedRows | Where-Object { $_.id -eq $created.id }).Count -eq 1) "$route archived row did not appear with includeArchived=true."

    $restored = Invoke-RestMethod -Method POST -Uri "$base/api/$route/$($created.id)/restore"
    Assert-Smoke ($restored.isArchived -eq $false) "$route restore did not clear IsArchived."

    $restoredRows = @(Invoke-RestMethod "$base/api/$route")
    Assert-Smoke (@($restoredRows | Where-Object { $_.id -eq $created.id }).Count -eq 1) "$route restored row did not return to default list."

    if ($exportNeedle) {
        $export = Invoke-WebRequest -Uri "$base/api/export/${route}?includeArchived=true" -UseBasicParsing
        Assert-Smoke ($export.StatusCode -eq 200) "$route export returned $($export.StatusCode)."
        Assert-Smoke ($export.Headers.'Content-Disposition' -like "*epata-$route-*") "$route export did not set the expected download name."
        Assert-Smoke ($export.Content -like "*$exportNeedle*") "$route export did not contain the expected row value."
    }

    return $created.id
}

try {
    foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $backupDownloadPath, "$backupDownloadPath-shm", "$backupDownloadPath-wal", $manualCopyPath, "$manualCopyPath-shm", "$manualCopyPath-wal", $outPath, $errPath)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
    $backupDir = Join-Path $root 'Backups'
    $backupFilesBefore = if (Test-Path -LiteralPath $backupDir) {
        @(Get-ChildItem -LiteralPath $backupDir -Filter 'epata-business-ledger-*.db' -File | ForEach-Object { $_.FullName })
    } else {
        @()
    }
    $uploadedDocsDir = Join-Path $root 'UploadedDocs'
    $uploadedDocsBefore = if (Test-Path -LiteralPath $uploadedDocsDir) {
        @(Get-ChildItem -LiteralPath $uploadedDocsDir -File -Recurse | ForEach-Object { $_.FullName })
    } else {
        @()
    }

    if (-not (Test-Path -LiteralPath $dllPath)) {
        throw "Build output not found at $dllPath. Run dotnet build to obj\verify-build first."
    }

    $recordsBehaviorOutput = & node (Join-Path $root 'tools\records-view-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Records view behavior script failed: $($recordsBehaviorOutput -join "`n")"
    Assert-Smoke (($recordsBehaviorOutput -join "`n") -like '*"RecordsViewBehavior":"pass"*') 'Records view behavior script did not report pass.'
    $dashboardBehaviorOutput = & node (Join-Path $root 'tools\dashboard-state-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Dashboard state behavior script failed: $($dashboardBehaviorOutput -join "`n")"
    Assert-Smoke (($dashboardBehaviorOutput -join "`n") -like '*"DashboardStateBehavior":"pass"*') 'Dashboard state behavior script did not report pass.'
    Assert-Smoke (($dashboardBehaviorOutput -join "`n") -like '*"DashboardBreakdownRound101":"pass"*') 'Dashboard state behavior script did not report Round 101 breakdown coverage pass.'
    Assert-Smoke (($dashboardBehaviorOutput -join "`n") -like '*"DashboardZeroBreakdownRound101":"pass"*') 'Dashboard state behavior script did not report Round 101 zero-breakdown coverage pass.'
    Assert-Smoke (($dashboardBehaviorOutput -join "`n") -like '*"DashboardChartsRound101":"pass"*') 'Dashboard state behavior script did not report Round 101 chart coverage pass.'
    $builderBehaviorOutput = & node (Join-Path $root 'tools\invoice-builder-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Invoice builder behavior script failed: $($builderBehaviorOutput -join "`n")"
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"InvoiceBuilderBehavior":"pass"*') 'Invoice builder behavior script did not report pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"PdfRound18":"pass"*') 'Invoice builder behavior script did not report Round 18 PDF coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"HtmlEscapeRound25":"pass"*') 'Invoice builder behavior script did not report Round 25 HTML escaping coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"TypeStatusSaveWorkflowRound66":"pass"*') 'Invoice builder behavior script did not report Round 66 type/status/save workflow coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"LineItemAddRemoveRound67":"pass"*') 'Invoice builder behavior script did not report Round 67 line-item add/remove coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"LivePreviewRefreshRound68":"pass"*') 'Invoice builder behavior script did not report Round 68 live-preview refresh coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"PreviewDebouncePerfRound98":"pass"*') 'Invoice builder behavior script did not report Round 98 preview debounce/performance coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"PreviewButtonRound69":"pass"*') 'Invoice builder behavior script did not report Round 69 preview-button coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"DownloadPdfRound70":"pass"*') 'Invoice builder behavior script did not report Round 70 download-PDF coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"SaveToastDedupeUserReport":"pass"*') 'Invoice builder behavior script did not report save-toast dedupe coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"PdfImportFilePickerRound71":"pass"*') 'Invoice builder behavior script did not report Round 71 PDF import file-picker coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"PdfImportPrivateNotesRound72":"pass"*') 'Invoice builder behavior script did not report Round 72 PDF import private-notes coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"ActiveRecordBarRound74":"pass"*') 'Invoice builder behavior script did not report Round 74 active record bar coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"FailedSaveStatusRound74":"pass"*') 'Invoice builder behavior script did not report Round 74 failed-save status coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"ProductDatalistRound74":"pass"*') 'Invoice builder behavior script did not report Round 74 product datalist coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"LongAddressPdfRound74":"pass"*') 'Invoice builder behavior script did not report Round 74 long-address PDF coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"InternalNotesPdfRound74":"pass"*') 'Invoice builder behavior script did not report Round 74 internal-notes PDF coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"PdfManyItemsRound75":"pass"*') 'Invoice builder behavior script did not report Round 75 many-line-item PDF coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"PdfLongTextRound75":"pass"*') 'Invoice builder behavior script did not report Round 75 long-text PDF coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"PdfPageSizesRound75":"pass"*') 'Invoice builder behavior script did not report Round 75 page-size PDF coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"PdfLongTermsRound75":"pass"*') 'Invoice builder behavior script did not report Round 75 long-terms PDF coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"PdfPreviewPrintMatchRound75":"pass"*') 'Invoice builder behavior script did not report Round 75 preview/print match coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"CalculatorRoundingRound81":"pass"*') 'Invoice builder behavior script did not report Round 81 calculator rounding coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"RateCardCopyRound81":"pass"*') 'Invoice builder behavior script did not report Round 81 Rate Card copy coverage pass.'
    Assert-Smoke (($builderBehaviorOutput -join "`n") -like '*"RateCardDefaultsRound81":"pass"*') 'Invoice builder behavior script did not report Round 81 Rate Card defaults coverage pass.'
    $relationshipBehaviorOutput = & node (Join-Path $root 'tools\relationship-directory-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Relationship directory behavior script failed: $($relationshipBehaviorOutput -join "`n")"
    Assert-Smoke (($relationshipBehaviorOutput -join "`n") -like '*"RelationshipDirectoryBehavior":"pass"*') 'Relationship directory behavior script did not report pass.'
    Assert-Smoke (($relationshipBehaviorOutput -join "`n") -like '*"RelationshipOpenRound40":"pass"*') 'Relationship directory behavior script did not report Round 40 row-open coverage pass.'
    Assert-Smoke (($relationshipBehaviorOutput -join "`n") -like '*"RelationshipDetailRound55":"pass"*') 'Relationship directory behavior script did not report Round 55 detail section coverage pass.'
    Assert-Smoke (($relationshipBehaviorOutput -join "`n") -like '*"RelationshipContactRound55":"pass"*') 'Relationship directory behavior script did not report Round 55 contact coverage pass.'
    Assert-Smoke (($relationshipBehaviorOutput -join "`n") -like '*"RelationshipPaginationRound55":"pass"*') 'Relationship directory behavior script did not report Round 55 pagination coverage pass.'
    Assert-Smoke (($relationshipBehaviorOutput -join "`n") -like '*"RelationshipDuplicateNamesRound56":"pass"*') 'Relationship directory behavior script did not report Round 56 duplicate-name coverage pass.'
    $toastStackBehaviorOutput = & node (Join-Path $root 'tools\toast-stack-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Toast stack behavior script failed: $($toastStackBehaviorOutput -join "`n")"
    Assert-Smoke (($toastStackBehaviorOutput -join "`n") -like '*"ToastStackBehavior":"pass"*') 'Toast stack behavior script did not report pass.'
    $toastActionsBehaviorOutput = & node (Join-Path $root 'tools\main-shell-toast-actions-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Main shell toast actions behavior script failed: $($toastActionsBehaviorOutput -join "`n")"
    Assert-Smoke (($toastActionsBehaviorOutput -join "`n") -like '*"MainShellToastActionsBehavior":"pass"*') 'Main shell toast actions behavior script did not report pass.'
    $shellNavigationBehaviorOutput = & node (Join-Path $root 'tools\shell-navigation-file-picker-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Shell navigation/file picker behavior script failed: $($shellNavigationBehaviorOutput -join "`n")"
    Assert-Smoke (($shellNavigationBehaviorOutput -join "`n") -like '*"ShellNavigationFilePickerBehavior":"pass"*') 'Shell navigation/file picker behavior script did not report pass.'
    Assert-Smoke (($shellNavigationBehaviorOutput -join "`n") -like '*"FilePickerRound73":"pass"*') 'Shell navigation/file picker behavior script did not report Round 73 file-picker pass.'
    Assert-Smoke (($shellNavigationBehaviorOutput -join "`n") -like '*"EmbeddedInvoiceShellRound73":"pass"*') 'Shell navigation/file picker behavior script did not report Round 73 embedded invoice shell pass.'
    Assert-Smoke (($shellNavigationBehaviorOutput -join "`n") -like '*"InvoiceTabsRound73":"pass"*') 'Shell navigation/file picker behavior script did not report Round 73 invoice tabs pass.'
    Assert-Smoke (($shellNavigationBehaviorOutput -join "`n") -like '*"InvoiceStateSwitchRound73":"pass"*') 'Shell navigation/file picker behavior script did not report Round 73 invoice state switch pass.'
    Assert-Smoke (($shellNavigationBehaviorOutput -join "`n") -like '*"InvoiceKeyboardShortcutsRound73":"pass"*') 'Shell navigation/file picker behavior script did not report Round 73 invoice keyboard shortcut pass.'
    $modalLifecycleBehaviorOutput = & node (Join-Path $root 'tools\modal-lifecycle-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Modal lifecycle behavior script failed: $($modalLifecycleBehaviorOutput -join "`n")"
    Assert-Smoke (($modalLifecycleBehaviorOutput -join "`n") -like '*"ModalLifecycleBehavior":"pass"*') 'Modal lifecycle behavior script did not report pass.'
    $modalSaveStateBehaviorOutput = & node (Join-Path $root 'tools\modal-save-state-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Modal save-state behavior script failed: $($modalSaveStateBehaviorOutput -join "`n")"
    Assert-Smoke (($modalSaveStateBehaviorOutput -join "`n") -like '*"ModalSaveStateBehavior":"pass"*') 'Modal save-state behavior script did not report pass.'
    Assert-Smoke (($modalSaveStateBehaviorOutput -join "`n") -like '*"ModalSaveDoubleClickRound84":"pass"*') 'Modal save-state behavior script did not report Round 84 double-click guard coverage pass.'
    $rapidSubmitBehaviorOutput = & node (Join-Path $root 'tools\rapid-submit-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Rapid-submit behavior script failed: $($rapidSubmitBehaviorOutput -join "`n")"
    Assert-Smoke (($rapidSubmitBehaviorOutput -join "`n") -like '*"RapidSubmitBehavior":"pass"*') 'Rapid-submit behavior script did not report pass.'
    Assert-Smoke (($rapidSubmitBehaviorOutput -join "`n") -like '*"RapidSubmitRound102":"pass"*') 'Rapid-submit behavior script did not report Round 102 coverage pass.'
    $accessibilityStaticBehaviorOutput = & node (Join-Path $root 'tools\accessibility-static-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Accessibility static behavior script failed: $($accessibilityStaticBehaviorOutput -join "`n")"
    Assert-Smoke (($accessibilityStaticBehaviorOutput -join "`n") -like '*"AccessibilityStaticBehavior":"pass"*') 'Accessibility static behavior script did not report pass.'
    Assert-Smoke (($accessibilityStaticBehaviorOutput -join "`n") -like '*"ScreenReaderLabelsRound103":"pass"*') 'Accessibility static behavior script did not report Round 103 screen-reader label coverage pass.'
    $keyboardAccessibilityOutput = & node (Join-Path $root 'tools\keyboard-accessibility-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Keyboard accessibility behavior script failed: $($keyboardAccessibilityOutput -join "`n")"
    Assert-Smoke (($keyboardAccessibilityOutput -join "`n") -like '*"KeyboardAccessibilityBehavior":"pass"*') 'Keyboard accessibility behavior script did not report pass.'
    Assert-Smoke (($keyboardAccessibilityOutput -join "`n") -like '*"KeyboardReachabilityRound110":"pass"*') 'Keyboard accessibility behavior script did not report Round 110 keyboard reachability pass.'
    Assert-Smoke (($keyboardAccessibilityOutput -join "`n") -like '*"ModalFocusTrapRound110":"pass"*') 'Keyboard accessibility behavior script did not report Round 110 modal focus trap pass.'
    Assert-Smoke (($keyboardAccessibilityOutput -join "`n") -like '*"FocusVisibleRound110":"pass"*') 'Keyboard accessibility behavior script did not report Round 110 focus-visible pass.'
    Assert-Smoke (($keyboardAccessibilityOutput -join "`n") -like '*"InvoiceBuilderKeyboardRound110":"pass"*') 'Keyboard accessibility behavior script did not report Round 110 invoice-builder keyboard pass.'
    $offlineLocalBoundaryOutput = & node (Join-Path $root 'tools\offline-local-boundary-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Offline/local-boundary behavior script failed: $($offlineLocalBoundaryOutput -join "`n")"
    Assert-Smoke (($offlineLocalBoundaryOutput -join "`n") -like '*"OfflineLocalBoundaryBehavior":"pass"*') 'Offline/local-boundary behavior script did not report pass.'
    Assert-Smoke (($offlineLocalBoundaryOutput -join "`n") -like '*"OfflineLocalBoundaryRound104":"pass"*') 'Offline/local-boundary behavior script did not report Round 104 coverage pass.'
    $uiEmptyLoadingOutput = & node (Join-Path $root 'tools\ui-empty-loading-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "UI empty/loading behavior script failed: $($uiEmptyLoadingOutput -join "`n")"
    Assert-Smoke (($uiEmptyLoadingOutput -join "`n") -like '*"UiEmptyLoadingBehavior":"pass"*') 'UI empty/loading behavior script did not report pass.'
    Assert-Smoke (($uiEmptyLoadingOutput -join "`n") -like '*"EmptyStatesRound105":"pass"*') 'UI empty/loading behavior script did not report Round 105 empty-state coverage pass.'
    Assert-Smoke (($uiEmptyLoadingOutput -join "`n") -like '*"LoadingStatesRound105":"pass"*') 'UI empty/loading behavior script did not report Round 105 loading-state coverage pass.'
    $responsiveLayoutOutput = & node (Join-Path $root 'tools\responsive-layout-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Responsive layout behavior script failed: $($responsiveLayoutOutput -join "`n")"
    Assert-Smoke (($responsiveLayoutOutput -join "`n") -like '*"ResponsiveLayoutBehavior":"pass"*') 'Responsive layout behavior script did not report pass.'
    Assert-Smoke (($responsiveLayoutOutput -join "`n") -like '*"ResponsiveLayoutRound108":"pass"*') 'Responsive layout behavior script did not report Round 108 responsive coverage pass.'
    Assert-Smoke (($responsiveLayoutOutput -join "`n") -like '*"NoClipSourceRound108":"pass"*') 'Responsive layout behavior script did not report Round 108 no-clip source coverage pass.'
    Assert-Smoke (($responsiveLayoutOutput -join "`n") -like '*"DashboardResizeRound108":"pass"*') 'Responsive layout behavior script did not report Round 108 dashboard resize coverage pass.'
    Assert-Smoke (($responsiveLayoutOutput -join "`n") -like '*"PrinterBoardManyCardsRound108":"pass"*') 'Responsive layout behavior script did not report Round 108 printer board coverage pass.'
    $entityTableBehaviorOutput = & node (Join-Path $root 'tools\entity-table-state-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Entity table state behavior script failed: $($entityTableBehaviorOutput -join "`n")"
    Assert-Smoke (($entityTableBehaviorOutput -join "`n") -like '*"EntityTableStateBehavior":"pass"*') 'Entity table state behavior script did not report pass.'
    Assert-Smoke (($entityTableBehaviorOutput -join "`n") -like '*"NeedsReviewFilterRound39":"pass"*') 'Entity table behavior script did not report Round 39 needs-review filter coverage pass.'
    Assert-Smoke (($entityTableBehaviorOutput -join "`n") -like '*"TextProofSearchRound39":"pass"*') 'Entity table behavior script did not report Round 39 text/proof search coverage pass.'
    Assert-Smoke (($entityTableBehaviorOutput -join "`n") -like '*"SaleStatusRound42":"pass"*') 'Entity table behavior script did not report Round 42 sale status coverage pass.'
    Assert-Smoke (($entityTableBehaviorOutput -join "`n") -like '*"ProductCatalogSearchRound50":"pass"*') 'Entity table behavior script did not report Round 50 product catalog search coverage pass.'
    $sidebarNavigationBehaviorOutput = & node (Join-Path $root 'tools\sidebar-navigation-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Sidebar navigation behavior script failed: $($sidebarNavigationBehaviorOutput -join "`n")"
    Assert-Smoke (($sidebarNavigationBehaviorOutput -join "`n") -like '*"SidebarNavigationBehavior":"pass"*') 'Sidebar navigation behavior script did not report pass.'
    $navigationHistoryBehaviorOutput = & node (Join-Path $root 'tools\navigation-history-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Navigation history behavior script failed: $($navigationHistoryBehaviorOutput -join "`n")"
    Assert-Smoke (($navigationHistoryBehaviorOutput -join "`n") -like '*"NavigationHistoryBehavior":"pass"*') 'Navigation history behavior script did not report pass.'
    $globalSearchBehaviorOutput = & node (Join-Path $root 'tools\global-search-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Global search behavior script failed: $($globalSearchBehaviorOutput -join "`n")"
    Assert-Smoke (($globalSearchBehaviorOutput -join "`n") -like '*"GlobalSearchBehavior":"pass"*') 'Global search behavior script did not report pass.'
    $quickAddBehaviorOutput = & node (Join-Path $root 'tools\quick-add-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Quick Add behavior script failed: $($quickAddBehaviorOutput -join "`n")"
    Assert-Smoke (($quickAddBehaviorOutput -join "`n") -like '*"QuickAddBehavior":"pass"*') 'Quick Add behavior script did not report pass.'
    Assert-Smoke (($quickAddBehaviorOutput -join "`n") -like '*"QuickAddCreatesRound37":"pass"*') 'Quick Add behavior script did not report Round 37 create coverage pass.'
    Assert-Smoke (($quickAddBehaviorOutput -join "`n") -like '*"QuickAddDoubleClickRound84":"pass"*') 'Quick Add behavior script did not report Round 84 double-click guard coverage pass.'
    $invoicePrefillBehaviorOutput = & node (Join-Path $root 'tools\invoice-prefill-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Invoice prefill behavior script failed: $($invoicePrefillBehaviorOutput -join "`n")"
    Assert-Smoke (($invoicePrefillBehaviorOutput -join "`n") -like '*"InvoicePrefillBehavior":"pass"*') 'Invoice prefill behavior script did not report pass.'
    Assert-Smoke (($invoicePrefillBehaviorOutput -join "`n") -like '*"ArPdfPrefillRound43":"pass"*') 'Invoice prefill behavior script did not report Round 43 AR PDF prefill coverage pass.'
    $aiProductDraftBehaviorOutput = & node (Join-Path $root 'tools\ai-product-draft-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "AI Product draft behavior script failed: $($aiProductDraftBehaviorOutput -join "`n")"
    Assert-Smoke (($aiProductDraftBehaviorOutput -join "`n") -like '*"AiProductDraftBehavior":"pass"*') 'AI Product draft behavior script did not report pass.'
    Assert-Smoke (($aiProductDraftBehaviorOutput -join "`n") -like '*"ProductImportDraftRound49":"pass"*') 'AI Product draft behavior script did not report Round 49 coverage pass.'
    $taxPrepStateBehaviorOutput = & node (Join-Path $root 'tools\tax-prep-state-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Tax Prep state behavior script failed: $($taxPrepStateBehaviorOutput -join "`n")"
    Assert-Smoke (($taxPrepStateBehaviorOutput -join "`n") -like '*"TaxPrepStateBehavior":"pass"*') 'Tax Prep state behavior script did not report pass.'
    Assert-Smoke (($taxPrepStateBehaviorOutput -join "`n") -like '*"AssetTaxPrepRound51":"pass"*') 'Tax Prep state behavior script did not report Round 51 asset coverage pass.'
    Assert-Smoke (($taxPrepStateBehaviorOutput -join "`n") -like '*"RewardIncomeRound51":"pass"*') 'Tax Prep state behavior script did not report Round 51 reward income coverage pass.'
    Assert-Smoke (($taxPrepStateBehaviorOutput -join "`n") -like '*"RewardNoDoubleCountRound51":"pass"*') 'Tax Prep state behavior script did not report Round 51 reward no-double-count coverage pass.'
    $communicationWorkflowBehaviorOutput = & node (Join-Path $root 'tools\communication-workflow-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Communication Workflow behavior script failed: $($communicationWorkflowBehaviorOutput -join "`n")"
    Assert-Smoke (($communicationWorkflowBehaviorOutput -join "`n") -like '*"CommunicationWorkflowBehavior":"pass"*') 'Communication Workflow behavior script did not report pass.'
    Assert-Smoke (($communicationWorkflowBehaviorOutput -join "`n") -like '*"CommunicationDisplayRound54":"pass"*') 'Communication Workflow behavior script did not report Round 54 display coverage pass.'
    Assert-Smoke (($communicationWorkflowBehaviorOutput -join "`n") -like '*"CommunicationFollowUpRound54":"pass"*') 'Communication Workflow behavior script did not report Round 54 follow-up coverage pass.'
    Assert-Smoke (($communicationWorkflowBehaviorOutput -join "`n") -like '*"CommunicationLongSummaryRound54":"pass"*') 'Communication Workflow behavior script did not report Round 54 long-summary coverage pass.'
    $businessAccountBehaviorOutput = & node (Join-Path $root 'tools\business-account-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Business Account behavior script failed: $($businessAccountBehaviorOutput -join "`n")"
    Assert-Smoke (($businessAccountBehaviorOutput -join "`n") -like '*"BusinessAccountBehavior":"pass"*') 'Business Account behavior script did not report pass.'
    Assert-Smoke (($businessAccountBehaviorOutput -join "`n") -like '*"BusinessAccountStatusRound52":"pass"*') 'Business Account behavior script did not report Round 52 active-status coverage pass.'
    $jobWorkflowBehaviorOutput = & node (Join-Path $root 'tools\job-workflow-behavior.mjs') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Job Workflow behavior script failed: $($jobWorkflowBehaviorOutput -join "`n")"
    Assert-Smoke (($jobWorkflowBehaviorOutput -join "`n") -like '*"JobWorkflowBehavior":"pass"*') 'Job Workflow behavior script did not report pass.'
    Assert-Smoke (($jobWorkflowBehaviorOutput -join "`n") -like '*"JobTimelineOpenRound53":"pass"*') 'Job Workflow behavior script did not report Round 53 timeline open coverage pass.'
    Assert-Smoke (($jobWorkflowBehaviorOutput -join "`n") -like '*"JobQueuePrefillRound53":"pass"*') 'Job Workflow behavior script did not report Round 53 queue prefill coverage pass.'
    $slowMachineOutput = & pwsh -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\slow-machine-simulation.ps1') 2>&1
    Assert-Smoke ($LASTEXITCODE -eq 0) "Slow-machine simulation script failed: $($slowMachineOutput -join "`n")"
    Assert-Smoke (($slowMachineOutput -join "`n") -like '*"SlowMachineSimulationRound109":"pass"*') 'Slow-machine simulation did not report Round 109 coverage pass.'

    $dbGuardDoc = Join-Path $root 'docs\TEST_DATABASE_GUARD.md'
    Assert-Smoke (Test-Path -LiteralPath $dbGuardDoc) 'Test database guard document is missing.'
    $dbGuardText = Get-Content -LiteralPath $dbGuardDoc -Raw
    $scriptDatabaseGuards = @(
        @{ Path = 'tools\local-smoke.ps1'; Database = 'Data\local-smoke.db'; RequiresConnectionString = $true; RequiresNoAutoOpen = $true },
        @{ Path = 'tools\full-acceptance.ps1'; Database = 'Data\full-acceptance.db'; RequiresConnectionString = $true; RequiresNoAutoOpen = $true },
        @{ Path = 'tools\browser-test-server.ps1'; Database = 'Data\browser-round4.db'; RequiresConnectionString = $true; RequiresNoAutoOpen = $true },
        @{ Path = 'tools\playwright-server.ps1'; Database = 'Data\playwright-browser-tests.db'; RequiresConnectionString = $true; RequiresNoAutoOpen = $true },
        @{ Path = 'tools\slow-machine-simulation.ps1'; Database = 'Data\slow-machine-simulation.db'; RequiresConnectionString = $true; RequiresNoAutoOpen = $true },
        @{ Path = 'run-test.bat'; Database = 'Data/epata-business-ledger-TEST.db'; RequiresConnectionString = $false; RequiresNoAutoOpen = $false }
    )
    foreach ($guard in $scriptDatabaseGuards) {
        $scriptPath = Join-Path $root $guard.Path
        Assert-Smoke (Test-Path -LiteralPath $scriptPath) "Guarded script missing: $($guard.Path)."
        $scriptText = Get-Content -LiteralPath $scriptPath -Raw
        $dbPattern = [regex]::Escape($guard.Database).Replace('\\', '[\\/]')
        Assert-Smoke ($scriptText -match $dbPattern) "$($guard.Path) does not name its intended disposable/test database $($guard.Database)."
        Assert-Smoke ($scriptText -notmatch 'Data Source=Data[\\/]epata-business-ledger\.db') "$($guard.Path) points an automated test at the production ledger database."
        Assert-Smoke ($scriptText -notmatch 'Data[\\/]epata-business-ledger\.db') "$($guard.Path) references the production ledger database."
        Assert-Smoke ($dbGuardText -match [regex]::Escape($guard.Path).Replace('\\', '[\\/]')) "Test database guard document does not list $($guard.Path)."
        Assert-Smoke ($dbGuardText -match $dbPattern) "Test database guard document does not list $($guard.Database)."
        if ($guard.RequiresConnectionString) {
            Assert-Smoke ($scriptText -like '*ConnectionStrings:DefaultConnection*') "$($guard.Path) does not override the database connection string."
        }
        if ($guard.RequiresNoAutoOpen) {
            Assert-Smoke ($scriptText -like '*App:OpenBrowserOnStart=false*') "$($guard.Path) does not disable automatic browser launch."
        }
    }

    $args = @(
        "`"$dllPath`"",
        "App:Url=$base",
        "`"ConnectionStrings:DefaultConnection=Data Source=Data\local-smoke.db`"",
        'App:OpenBrowserOnStart=false'
    )

    $server = Start-Process -FilePath 'dotnet' `
        -ArgumentList $args `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outPath `
        -RedirectStandardError $errPath `
        -PassThru

    $health = Wait-SmokeHealth
    Assert-Smoke ($health.status -eq 'ok') 'Health endpoint did not report ok.'
    Assert-Smoke ($health.database -like '*local-smoke.db') "Smoke app is not using the disposable database. Reported: $($health.database)"
    Invoke-SmokeBackupCopyRehearsal 'Round 64 preflight backup-and-copy rehearsal'
    $backupCopyPreflightCompleted = $true

    foreach ($path in @(
        '/',
        '/index.html',
        '/js/relationship-directory.js?v=3',
        '/js/toast-stack.js?v=1',
        '/js/modal-lifecycle.js?v=1',
        '/js/modal-save-state.js?v=1',
        '/js/entity-table-state.js?v=2',
        '/js/sidebar-state.js?v=1',
        '/js/navigation-history.js?v=1',
        '/js/global-search.js?v=1',
        '/js/quick-add.js?v=1',
        '/js/invoice-prefill.js?v=1',
        '/js/tax-prep-state.js?v=1',
        '/js/communication-state.js?v=1',
        '/js/job-workflow-state.js?v=1',
        '/invoice-builder/',
        '/invoice-builder/index.html',
        '/js/app.js',
        '/css/site.css',
        '/invoice-builder/js/app.js',
        '/invoice-builder/js/calculator.js',
        '/invoice-builder/js/builder.js',
        '/invoice-builder/js/api.js',
        '/invoice-builder/js/records.js',
        '/invoice-builder/js/save-intent.js',
        '/invoice-builder/js/document-session.js',
        '/invoice-builder/js/validation.js',
        '/invoice-builder/css/app.css?v=20260609-ai-full-prefill'
    )) {
        $response = Invoke-WebRequest -Uri "$base$path" -UseBasicParsing
        Assert-Smoke ($response.StatusCode -eq 200) "$path returned $($response.StatusCode)."
        Assert-SmokeNoSecretMaterial "Static asset $path" $response.Content
    }

    $round80FreshReviewStatus = Invoke-RestMethod "$base/api/ai/review/status"
    Assert-Smoke ($round80FreshReviewStatus.usedAi -eq $false -and $round80FreshReviewStatus.sendsDataToAiProvider -eq $false) 'Round 80 AI Review status did not report deterministic local-only review.'
    Assert-Smoke ($round80FreshReviewStatus.safety -like '*Read-only*' -and $round80FreshReviewStatus.safety -like '*never changes*') 'Round 80 AI Review status did not explain its read-only boundary.'
    $round80FreshReview = Invoke-RestMethod "$base/api/ai/review"
    Assert-Smoke ($null -ne $round80FreshReview.items) 'Round 80 AI Review endpoint did not return a recommendations collection.'
    $round80AiOperationsStatus = Invoke-RestMethod "$base/api/ai/operations/status"
    Assert-Smoke ($round80AiOperationsStatus.safety -like '*review-first*' -or $round80AiOperationsStatus.safety -like '*Review-first*') 'Round 80 AI Operations status did not expose review-first safety text.'
    Assert-Smoke (@($round80AiOperationsStatus.features | Where-Object { $_.name -eq 'Ask the Ledger' -and $_.writes -like '*Read-only*' }).Count -eq 1) 'Round 80 AI Operations status did not mark Ask the Ledger as read-only.'
    $round80LocalAiStatus = Invoke-RestMethod "$base/api/ai/local/status"
    Assert-Smoke ($round80LocalAiStatus.localOnly -eq $true -and $round80LocalAiStatus.baseUrl -like 'http://127.0.0.1*') 'Round 80 Local AI status did not report loopback-only local behavior.'
    Assert-Smoke ($round80LocalAiStatus.safety -like '*Loopback-only*' -and $round80LocalAiStatus.safety -like '*explicit AI action*') 'Round 80 Local AI status did not explain the local/read-only boundary.'
    if (-not $round80LocalAiStatus.lmsInstalled) {
        $round85MissingStart = Invoke-WebRequest -Method POST -Uri "$base/api/ai/local/start" -ContentType 'application/json' -Body '{}' -UseBasicParsing -SkipHttpErrorCheck
        $round85MissingStartStatus = [int]$round85MissingStart.StatusCode
        $round85MissingStartContent = [string]$round85MissingStart.Content
        Assert-Smoke ($round85MissingStartStatus -eq 400) "Round 85 Local AI start without LM Studio should return 400, got $round85MissingStartStatus."
        Assert-Smoke ($round85MissingStartContent -like '*LM Studio*must be installed*') 'Round 85 Local AI missing-install start response did not explain the required install.'
    }
    if (-not $round80LocalAiStatus.serverOnline) {
        $round85StopAlreadyOff = Invoke-SmokeJson 'POST' '/api/ai/local/stop' ([ordered]@{})
        Assert-Smoke ($round85StopAlreadyOff.success -eq $true -and $round85StopAlreadyOff.message -like '*already off*') 'Round 85 Local AI stop did not safely no-op when the server was already off.'
    }

    $mainJs = Get-Text '/js/app.js'
    $siteCss = Get-Text '/css/site.css'
    $invoiceBuilderCss = Get-Text '/invoice-builder/css/app.css?v=20260609-ai-full-prefill'
    Assert-Smoke ($siteCss -match '(?s)\.table-wrap td\s*\{[^}]*overflow-wrap:\s*anywhere;[^}]*word-break:\s*break-word;') 'Main shell table cells are not hardened for long names, notes, and filenames.'
    Assert-Smoke ($siteCss -match '(?s)\.upload-next-card strong,\s*\.upload-next-card p,[^}]*\.proof-control input\s*\{[^}]*overflow-wrap:\s*anywhere;[^}]*word-break:\s*break-word;') 'Main shell document/AI/proof fields are not hardened for long upload filenames and mapped values.'
    Assert-Smoke ($invoiceBuilderCss -match '(?s)\.data-table td\s*\{[^}]*max-width:\s*320px;[^}]*overflow-wrap:\s*anywhere;[^}]*word-break:\s*break-word;') 'Invoice records table cells are not hardened for long customer/project values.'
    Assert-Smoke ($invoiceBuilderCss -match '(?s)\.record-link\s*\{[^}]*white-space:\s*normal;[^}]*overflow-wrap:\s*anywhere;[^}]*word-break:\s*break-word;') 'Invoice records links are not hardened for long document/customer text.'
    Assert-Smoke ($mainJs -like '*EpataRelationshipDirectory*') 'Main shell JS is not using the relationship directory helper.'
    Assert-Smoke ($mainJs -like '*customerDetailSectionPlan*' -and $mainJs -like '*vendorDetailSectionPlan*' -and $mainJs -like '*personContactTarget*' -and $mainJs -like '*relationshipSectionPage*') 'Main shell JS is not using Relationship detail/contact/pagination helpers.'
    $relationshipDirectoryJs = Get-Text '/js/relationship-directory.js?v=3'
    Assert-Smoke ($mainJs -like '*nameStatus*' -and $mainJs -like '*duplicateNameWarning*' -and $relationshipDirectoryJs -like '*Duplicate contacts*' -and $relationshipDirectoryJs -like '*trimmed, case-insensitive name*') 'Main shell JS is not exposing duplicate relationship-name status.'
    Assert-Smoke ($mainJs -like '*Edit Audit Doc*' -and $mainJs -like '*openModal(configs.auditDocs*') 'Document Intake is not wiring uploaded Audit Docs back to the Audit Doc edit modal.'
    foreach ($needle in @('Dashboard', 'Quick Add', 'Estimates', 'Invoices', 'Invoice Records', 'Document Intake', 'Tax Prep', 'Admin / Data')) {
        Assert-Smoke ($mainJs -like "*$needle*") "Main shell JS did not contain nav label $needle."
    }
    foreach ($needle in @('defaultPaymentMethod', 'Payment Method', 'Etsy Payments', 'Wire Transfer', 'Square', 'Stripe')) {
        Assert-Smoke ($mainJs -like "*$needle*") "Main shell JS is missing payment-method UI/default wiring for $needle."
    }
    $mainHtml = Get-Text '/index.html'
    Assert-Smoke ($mainHtml -like '*/js/relationship-directory.js?v=3*') 'Main shell is not loading the relationship directory helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/toast-stack.js?v=1*') 'Main shell is not loading the stacked toast helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/modal-lifecycle.js?v=1*') 'Main shell is not loading the modal lifecycle helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/modal-save-state.js?v=1*') 'Main shell is not loading the modal save-state helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/entity-table-state.js?v=2*') 'Main shell is not loading the entity table state helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/sidebar-state.js?v=1*') 'Main shell is not loading the sidebar state helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/navigation-history.js?v=1*') 'Main shell is not loading the navigation history helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/global-search.js?v=1*') 'Main shell is not loading the global search helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/quick-add.js?v=1*') 'Main shell is not loading the Quick Add helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/invoice-prefill.js?v=1*') 'Main shell is not loading the invoice prefill helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/tax-prep-state.js?v=1*') 'Main shell is not loading the Tax Prep state helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/communication-state.js?v=1*') 'Main shell is not loading the Communication state helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/job-workflow-state.js?v=1*') 'Main shell is not loading the Job Workflow state helper before app JS.'
    Assert-Smoke ($mainHtml -like '*/js/dashboard-state.js?v=1*') 'Main shell is not loading the Dashboard state helper before app JS.'
    Assert-Smoke ($mainHtml -like '*id="toast-container"*') 'Main shell does not expose a stacked toast container.'
    Assert-Smoke ($mainHtml -like '*/js/app.js?v=20260911-archive-recovery-2*') 'Main shell is not loading the current app JS version.'
    Assert-Smoke ($mainJs -like '*EpataToastStack*') 'Main shell JS is not using stacked toasts.'
    Assert-Smoke ($mainJs -like '*EpataModalLifecycle*') 'Main shell JS is not using modal lifecycle focus management.'
    Assert-Smoke ($mainJs -like '*EpataModalSaveState*') 'Main shell JS is not using modal save-state failure handling.'
    Assert-Smoke ($mainJs -like '*EpataEntityTableState*') 'Main shell JS is not using entity table state filtering/paging.'
    Assert-Smoke ($mainJs -like '*EpataDashboardState*') 'Main shell JS is not using dashboard state behavior.'
    Assert-Smoke ($mainJs -like '*EpataSidebarState*') 'Main shell JS is not using sidebar state persistence.'
    Assert-Smoke ($mainJs -like '*EpataNavigationHistory*') 'Main shell JS is not using navigation history behavior.'
    Assert-Smoke ($mainJs -like '*EpataGlobalSearch*') 'Main shell JS is not using global search behavior.'
    Assert-Smoke ($mainJs -like '*EpataQuickAdd*') 'Main shell JS is not using Quick Add behavior.'
    Assert-Smoke ($mainJs -like '*startReceivablePdfInvoice*' -and $mainJs -like '*EpataInvoicePrefill*') 'Main shell JS is not using AR-to-invoice prefill behavior.'
    Assert-Smoke ($mainJs -like '*EpataTaxPrepState*' -and $mainJs -like '*Asset Tax Handling*' -and $mainJs -like '*MakerWorld Reward Income Status*') 'Main shell JS is not using Tax Prep asset/reward display helpers.'
    Assert-Smoke ($mainJs -like '*EpataJobWorkflowState*' -and $mainJs -like '*openTimelineEvent*' -and $mainJs -like '*printerQueuePrefillFromJob*') 'Main shell JS is not using Job Workflow timeline/open/prefill helpers.'
    Assert-Smoke ($mainJs -like '*EpataCommunicationState*' -and $mainJs -like '*communicationCardPlan*' -and $mainJs -like '*communication-summary*') 'Main shell JS is not using Communication state/card helpers.'
    foreach ($needle in @('Federal & NJ Threshold Watch', 'Federal estimated payments', 'NJ estimated payments', 'Sales-Tax Handling', 'NJ ST-50', 'Filing Checklist', 'Review Before Filing', 'Needs Review', 'Missing Proof', 'Expense Review', 'Open Mileage Log')) {
        Assert-Smoke ($mainJs -like "*$needle*") "Round 78 Tax Prep UI is missing $needle."
    }
    Assert-Smoke ($mainJs -like '*function setTaxYear*' -and $mainJs -like '*showPage(''taxPrep'', { replace: true })*' -and $mainJs -like '*/api/export/tax-package?year=${taxYear}*' -and $mainJs -like '*/api/export/tax-summary?year=${taxYear}*' -and $mainJs -like '*/api/export/nj-sales-tax?year=${taxYear}*' -and $mainJs -like '*/api/export/tax-sales?year=${taxYear}&paymentGroup=all*') 'Round 78 Tax Prep year switch/export wiring is incomplete.'
    Assert-Smoke ($mainJs -like '*async function renderAdmin*' -and $mainJs -like '*api(''/api/app-info'')*' -and $mainJs -like '*dbPath*' -and $mainJs -like '*Backup DB Now*' -and $mainJs -like '*backupDb()*' -and $mainJs -like '*fetch(''/api/system/backup'', { method: ''POST'' })*') 'Round 78 Admin page is not wired to app/database info and system backup.'
    foreach ($needle in @('function renderLedgerMap', 'Ask The Ledger Map', 'ledgerQuestion(''job''', 'ledgerQuestion(''invoice''', 'ledgerQuestion(''ar''', 'ledgerQuestion(''sale''', 'ledgerQuestion(''proof''', 'ledgerQuestion(''action''', 'The Normal Direct Customer Flow', 'Why Not Just Invoices?', 'What The Screenshot Is Showing', 'Proof rule')) {
        Assert-Smoke ($mainJs -like "*$needle*") "Round 79 Ledger Map is missing $needle."
    }
    Assert-Smoke ($mainJs -like '*data-ledger-sample*' -and $mainJs -like '*answerLedgerQuestion*' -and $mainJs -like '*Can a job exist before an estimate?*' -and $mainJs -like '*Do Etsy orders need AR?*' -and $mainJs -like '*window.askLedgerMap*') 'Round 79 Ledger Map local ask/examples are not wired.'
    Assert-Smoke ($mainJs -like '*function renderWorkflowGuide*' -and $mainJs -like '*Estimate needed*' -and $mainJs -like '*Invoices -> New Invoice*' -and $mainJs -like '*Quick Add → Direct Paid Sale*' -and $mainJs -like '*Quick Add → Paid Expense*' -and $mainJs -like '*Quick Add → Bill / AP*' -and $mainJs -like '*Document Intake first*') 'Round 79 Workflow Guide does not explain order-to-cash and expense workflows with current labels.'
    Assert-Smoke ($mainJs -like '*function renderHelp*' -and $mainJs -like '*What Each Tab Is For*' -and $mainJs -like '*Help / Glossary*' -and $mainJs -like '*Open Ledger Map*' -and $mainJs -like '*Open Workflow Guide*' -and $mainJs -like '*showPage(''ledgerMap'')*' -and $mainJs -like '*showPage(''workflowGuide'')*') 'Round 79 Help/Glossary does not expose linked app-area guidance.'
    Assert-Smoke ($siteCss -like '*@media (max-width: 1120px)*' -and $siteCss -like '*.ledger-flow { grid-template-columns: 1fr; }*' -and $siteCss -like '*.flow-lane,*' -and $siteCss -like '*.ledger-question-grid, .ledger-screenshot-map, .ledger-mini-map*' -and $siteCss -like '*@media (max-width: 760px)*' -and $siteCss -like '*.nav-tour-chain { flex-direction: column; overflow-x: visible; }*') 'Round 79 Ledger/Workflow/Help pages are missing responsive mobile layout rules.'
    Assert-Smoke ($mainJs -like '*AI Review Center*' -and $mainJs -like '*No review recommendations.*' -and $mainJs -like '*showPage(''${escapeHtml(item.route)}'')*' -and $mainJs -like '*Open ${escapeHtml(item.area)}*') 'Round 80 AI Review UI does not expose empty-state and open-route behavior.'
    Assert-Smoke ($mainJs -like '*What Is AI Here?*' -and $mainJs -like '*Deterministic findings stay authoritative*' -and $mainJs -like '*Optional local-model explanation*' -and $mainJs -like '*Nothing leaves this computer*' -and $mainJs -like '*No automatic writes*') 'Round 80 AI Review UI does not clearly mark deterministic versus model-backed operations.'
    Assert-Smoke ($mainJs -like '*Local AI Power*' -and $mainJs -like '*Loopback-only*' -and $mainJs -like '*No hosted API token is required*' -and $mainJs -like '*What it cannot do*' -and $mainJs -like '*Local AI does not silently save records*') 'Round 80 Local AI UI does not explain local/read-only boundaries.'
    Assert-Smoke ($mainJs -like '*localAiActionInFlight: null*' -and $mainJs -like '*if (appState.localAiActionInFlight)*' -and $mainJs -like '*startButton.disabled = true*' -and $mainJs -like '*stopButton.disabled = true*' -and $mainJs -like '*Local AI ${appState.localAiActionInFlight} is already in progress.*') 'Round 85 Local AI UI does not guard rapid Start/Stop button submissions.'
    Assert-Smoke ($mainJs -like '*appState.invoiceToolPrefill = result.prefill*' -and $mainJs -like '*appState.invoiceToolSnapshot = null*' -and $mainJs -like '*showPage(''estimates'', { resetInvoiceTool: true })*') 'Round 77 AI Estimate draft is not wired to open the embedded estimate builder as an unsaved prefill.'
    foreach ($needle in @('Customer', 'Phone', 'Email', 'Address', 'Project', 'Material', 'Color', 'Infill', 'Valid until', 'Material used', 'Material rate', 'Print time', 'Machine rate', 'Design time', 'Design rate', 'Setup fee', 'Post-processing', 'Difficulty', 'Minimum', 'Rush', 'Discount', 'Tax rate')) {
        Assert-Smoke ($mainJs -like "*'$needle'*" -or $mainJs -like "*`"$needle`"*") "Round 77 AI Estimate review preview is missing field label $needle."
    }
    Assert-Smoke ($mainJs -like '*ai-line-preview*' -and $mainJs -like '*Questions*' -and $mainJs -like '*Warnings*' -and $mainJs -like '*View structured JSON*' -and $mainJs -like '*Nothing saves until you review and click Save*') 'Round 77 AI Estimate review surface does not expose editable draft context before saving.'

    $builderJs = Get-Text '/invoice-builder/js/builder.js'
    Assert-Smoke ($builderJs -like '*planDocTypeChange*' -and $builderJs -like '*shouldClearDocNumberForDocType*') 'Builder JS does not clear a mismatched doc-number prefix on type change.'
    Assert-Smoke ($builderJs -like '*INVOICE_STATUSES*Partial*') 'Builder JS does not expose Partial invoice status.'

    $invoiceAppJs = Get-Text '/invoice-builder/js/app.js'
    Assert-Smoke ($invoiceAppJs -match "from './save-intent\.js(?:\?[^']*)?'") 'Invoice app JS is not using the save-intent guard.'
    Assert-Smoke ($invoiceAppJs -match "from './calculator\.js(?:\?[^']*)?'") 'Invoice app JS is not using the calculator module.'
    Assert-Smoke ($invoiceAppJs -match "from './document-session\.js(?:\?[^']*)?'") 'Invoice app JS is not using the document session module.'
    Assert-Smoke ($invoiceAppJs -match "from './validation\.js(?:\?[^']*)?'") 'Invoice app JS is not using the validation module.'
    Assert-Smoke ($invoiceAppJs -like '*Finish the current save before starting a different save action*') 'Invoice app JS does not block conflicting in-flight save actions.'
    Assert-Smoke ($invoiceAppJs -like '*original unchanged*') 'Invoice app JS does not clearly tell the user when a type-change save creates a new record.'

    $calculatorJs = Get-Text '/invoice-builder/js/calculator.js'
    Assert-Smoke ($calculatorJs -like '*calculatePricingFromState*') 'Calculator JS does not expose pure pricing calculation coverage.'
    Assert-Smoke ($calculatorJs -like '*buildCalculatorPushPlan*') 'Calculator JS does not expose calculator-to-builder transfer coverage.'

    $documentSessionJs = Get-Text '/invoice-builder/js/document-session.js'
    Assert-Smoke ($documentSessionJs -like '*identityAfterArchivedRecord*') 'Document session JS does not expose archive identity coverage.'
    Assert-Smoke ($documentSessionJs -like '*buildSaveFailureUiState*') 'Document session JS does not expose failed-save UI coverage.'

    $validationJs = Get-Text '/invoice-builder/js/validation.js'
    Assert-Smoke ($validationJs -like '*validateDocumentState*') 'Validation JS does not expose pure required-field coverage.'
    Assert-Smoke ($validationJs -like '*normalizeNumberValue*') 'Validation JS does not expose pure numeric normalization coverage.'
    Assert-Smoke ($validationJs -like "*paymentMethod: 'Payment method'*") 'Validation JS does not require payment method.'

    $apiJs = Get-Text '/invoice-builder/js/api.js'
    Assert-Smoke ($apiJs -like '*/api/ai/invoice-document-draft/upload*') 'Invoice PDF import endpoint is not wired in API JS.'

    $invoiceHtml = Get-Text '/invoice-builder/index.html'
    Assert-Smoke ($invoiceHtml -match '<script type="module" src="\./js/app\.js(?:\?[^\"]*)?"></script>') 'Standalone invoice builder shell is not loading the app module.'
    Assert-Smoke ($invoiceHtml -like '*btnImportPdfDraft*') 'Invoice PDF import button is missing.'
    Assert-Smoke ($invoiceHtml -like '*btnImportPdfDraftBuilder*') 'Builder header Invoice PDF import button is missing.'
    Assert-Smoke ($invoiceHtml -like '*invoicePdfImportFile*') 'Invoice PDF file input is missing.'
    Assert-Smoke ($invoiceHtml -like '*id="paymentMethod" required*') 'Standalone invoice builder payment method field is not required.'
    Assert-Smoke ($invoiceHtml -like '*Wire Transfer*') 'Standalone invoice builder does not expose expanded payment methods.'
    Assert-Smoke ($invoiceHtml -like '*value="Partial">Partial*') 'Records status filter does not expose Partial invoices.'
    Assert-Smoke ($invoiceHtml -like '*value="paid">Sort: Paid*') 'Records sort dropdown does not expose Paid sort.'
    Assert-Smoke ($invoiceHtml -like '*value="project">Sort: Project*') 'Records sort dropdown does not expose Project sort.'

    $recordsJs = Get-Text '/invoice-builder/js/records.js'
    Assert-Smoke ($recordsJs -like '*buildRecordViewModel*') 'Records JS does not expose the behavior model used by local verification.'
    Assert-Smoke ($recordsJs -like '*recordsToCsv*') 'Records JS does not expose filtered/sorted CSV generation.'

    $round16ConfigBody = [ordered]@{
        businessName = 'Round 16 Fab Lab'
        businessLocation = 'Trenton, NJ'
        businessEmail = 'round16@example.test'
        businessPhone = '(973) 555-1616'
        businessWebsite = 'https://round16.example.test/'
        businessEtsy = 'https://etsy.com/shop/round16'
        businessInstagram = '@round16fab'
        businessFacebook = 'Round 16 Fabrication'
        brandColor = '#0f766e'
        calcGramRate = 0.13
        calcHourRate = 8.5
        calcDesignRate = 42
        calcSetupFee = 6
        calcPostFee = 4.5
        calcMinimum = 28
    }
    $round16SavedConfig = Invoke-RestMethod `
        -Method PUT `
        -Uri "$base/api/config" `
        -ContentType 'application/json' `
        -Body ($round16ConfigBody | ConvertTo-Json -Depth 5)
    Assert-Smoke ($round16SavedConfig.businessName -eq 'Round 16 Fab Lab') 'Saved config did not echo business name.'
    Assert-Smoke ([math]::Abs([decimal]$round16SavedConfig.calcGramRate - 0.13) -lt 0.0001) 'Saved config did not echo gram rate.'

    $round16LoadedConfig = Invoke-RestMethod "$base/api/config"
    Assert-Smoke ($round16LoadedConfig.businessName -eq 'Round 16 Fab Lab') 'Config reload did not preserve business name.'
    Assert-Smoke ($round16LoadedConfig.businessLocation -eq 'Trenton, NJ') 'Config reload did not preserve location.'
    Assert-Smoke ($round16LoadedConfig.businessEmail -eq 'round16@example.test') 'Config reload did not preserve email.'
    Assert-Smoke ($round16LoadedConfig.businessPhone -eq '(973) 555-1616') 'Config reload did not preserve phone.'
    Assert-Smoke ($round16LoadedConfig.businessWebsite -eq 'https://round16.example.test/') 'Config reload did not preserve website.'
    Assert-Smoke ($round16LoadedConfig.businessEtsy -eq 'https://etsy.com/shop/round16') 'Config reload did not preserve Etsy link.'
    Assert-Smoke ($round16LoadedConfig.businessInstagram -eq '@round16fab') 'Config reload did not preserve Instagram.'
    Assert-Smoke ($round16LoadedConfig.businessFacebook -eq 'Round 16 Fabrication') 'Config reload did not preserve Facebook.'
    Assert-Smoke ($round16LoadedConfig.brandColor -eq '#0f766e') 'Config reload did not preserve brand color.'
    Assert-Smoke ([math]::Abs([decimal]$round16LoadedConfig.calcGramRate - 0.13) -lt 0.0001) 'Config reload did not preserve gram rate.'
    Assert-Smoke ([math]::Abs([decimal]$round16LoadedConfig.calcHourRate - 8.5) -lt 0.0001) 'Config reload did not preserve hour rate.'
    Assert-Smoke ([math]::Abs([decimal]$round16LoadedConfig.calcDesignRate - 42) -lt 0.0001) 'Config reload did not preserve design rate.'
    Assert-Smoke ([math]::Abs([decimal]$round16LoadedConfig.calcSetupFee - 6) -lt 0.0001) 'Config reload did not preserve setup fee.'
    Assert-Smoke ([math]::Abs([decimal]$round16LoadedConfig.calcPostFee - 4.5) -lt 0.0001) 'Config reload did not preserve post-processing fee.'
    Assert-Smoke ([math]::Abs([decimal]$round16LoadedConfig.calcMinimum - 28) -lt 0.0001) 'Config reload did not preserve minimum charge.'

    $round81DocPayload = New-SmokeDocumentPayload 'INV-2026-8101' 'INVOICE' 'Paid' 'Round81 Calculator Customer' 'Round81 Calculator Rounding Project' 61.78 999 6.625
    $round81DocPayload.discountAmount = 4.33
    $round81DocPayload.rushAmount = 5.75
    $round81DocPayload.calcGrams = 33.3
    $round81DocPayload.calcHours = 2.75
    $round81DocPayload.calcDesignHours = 0.5
    $round81DocPayload.calcSetupFee = [decimal]$round16LoadedConfig.calcSetupFee
    $round81DocPayload.calcPostFee = [decimal]$round16LoadedConfig.calcPostFee
    $round81DocPayload.calcGramRate = [decimal]$round16LoadedConfig.calcGramRate
    $round81DocPayload.calcHourRate = [decimal]$round16LoadedConfig.calcHourRate
    $round81DocPayload.calcDesignRate = [decimal]$round16LoadedConfig.calcDesignRate
    $round81DocPayload.calcMinimum = [decimal]$round16LoadedConfig.calcMinimum
    $round81DocPayload.calcDifficulty = 1.2
    $round81DocPayload.calcRush = 10
    $round81DocPayload.calcDiscount = 4.33
    $round81DocPayload.lineItems[0].amount = 61.78
    $round81SavedDoc = Invoke-SmokeJson 'POST' '/api/documents' $round81DocPayload
    Assert-Smoke ($round81SavedDoc.docNumber -eq 'INV-2026-8101') 'Round 81 calculator settings fixture did not save with the expected document number.'
    Assert-SmokeClose $round81SavedDoc.subtotal 61.78 0.01 'Round 81 saved document subtotal did not match rounded builder summary.'
    Assert-SmokeClose $round81SavedDoc.discountAmount 4.33 0.01 'Round 81 saved document discount did not match rounded builder summary.'
    Assert-SmokeClose $round81SavedDoc.rushAmount 5.75 0.01 'Round 81 saved document rush did not match rounded builder summary.'
    Assert-SmokeClose $round81SavedDoc.amountPaid (Get-SmokeDecimalOrZero $round81SavedDoc.total) 0.01 'Round 81 paid document did not auto-clamp payment to rounded total.'

    $round81FutureConfigBody = [ordered]@{}
    foreach ($entry in $round16ConfigBody.GetEnumerator()) {
        $round81FutureConfigBody[$entry.Key] = $entry.Value
    }
    $round81FutureConfigBody.calcGramRate = 0.21
    $round81FutureConfigBody.calcHourRate = 11.25
    $round81FutureConfigBody.calcDesignRate = 48
    $round81FutureConfigBody.calcSetupFee = 9
    $round81FutureConfigBody.calcPostFee = 7
    $round81FutureConfigBody.calcMinimum = 35
    $round81FutureConfig = Invoke-RestMethod `
        -Method PUT `
        -Uri "$base/api/config" `
        -ContentType 'application/json' `
        -Body ($round81FutureConfigBody | ConvertTo-Json -Depth 5)
    Assert-SmokeClose $round81FutureConfig.calcGramRate 0.21 0.0001 'Round 81 future calculator gram-rate setting did not change.'
    Assert-SmokeClose $round81FutureConfig.calcMinimum 35 0.0001 'Round 81 future calculator minimum setting did not change.'

    $round81ReloadedDoc = Invoke-RestMethod "$base/api/documents/$($round81SavedDoc.id)"
    Assert-SmokeClose $round81ReloadedDoc.calcGramRate 0.13 0.0001 'Round 81 saved document calculator gram rate changed after settings update.'
    Assert-SmokeClose $round81ReloadedDoc.calcHourRate 8.5 0.0001 'Round 81 saved document calculator hour rate changed after settings update.'
    Assert-SmokeClose $round81ReloadedDoc.calcDesignRate 42 0.0001 'Round 81 saved document calculator design rate changed after settings update.'
    Assert-SmokeClose $round81ReloadedDoc.calcMinimum 28 0.0001 'Round 81 saved document calculator minimum changed after settings update.'
    Assert-SmokeClose $round81ReloadedDoc.total (Get-SmokeDecimalOrZero $round81SavedDoc.total) 0.01 'Round 81 saved document total changed after settings update.'

    $round81ArRows = @(Get-SmokeJsonArray '/api/receivable-invoices?includeArchived=true' | Where-Object { $_.invoiceNumber -eq 'INV-2026-8101' })
    Assert-Smoke ($round81ArRows.Count -eq 1) "Round 81 expected one synced AR row for INV-2026-8101, got $($round81ArRows.Count)."
    Assert-SmokeClose $round81ArRows[0].invoiceTotal (Get-SmokeDecimalOrZero $round81ReloadedDoc.total) 0.01 'Round 81 AR sync invoice total did not match document total.'
    Assert-SmokeClose $round81ArRows[0].amountPaid (Get-SmokeDecimalOrZero $round81ReloadedDoc.amountPaid) 0.01 'Round 81 AR sync amount paid did not match document amount paid.'
    Assert-SmokeClose $round81ArRows[0].balanceDue (Get-SmokeDecimalOrZero $round81ReloadedDoc.balance) 0.01 'Round 81 AR sync balance did not match document balance.'

    $round81SalesRows = @(Get-SmokeJsonArray '/api/sales?includeArchived=true' | Where-Object { $_.invoiceNumber -eq 'INV-2026-8101' })
    Assert-Smoke ($round81SalesRows.Count -eq 1) "Round 81 expected one synced Sales row for INV-2026-8101, got $($round81SalesRows.Count)."
    Assert-SmokeClose $round81SalesRows[0].customerPaid (Get-SmokeDecimalOrZero $round81ReloadedDoc.amountPaid) 0.01 'Round 81 Sales sync customer paid did not match document amount paid.'
    Assert-SmokeClose $round81SalesRows[0].salesTaxCollected (Get-SmokeDecimalOrZero $round81ReloadedDoc.taxAmount) 0.01 'Round 81 Sales sync tax did not match document tax.'

    $round81ReceivablesExportRows = @((Invoke-WebRequest -Uri "$base/api/export/receivable-invoices?includeArchived=true" -UseBasicParsing).Content | ConvertFrom-Csv)
    $round81ReceivablesExportRow = @($round81ReceivablesExportRows | Where-Object { $_.InvoiceNumber -eq 'INV-2026-8101' })[0]
    Assert-Smoke ($null -ne $round81ReceivablesExportRow) 'Round 81 receivables CSV export did not include the synced invoice.'
    Assert-SmokeClose $round81ReceivablesExportRow.InvoiceTotal (Get-SmokeDecimalOrZero $round81ReloadedDoc.total) 0.01 'Round 81 receivables CSV total did not match API total.'

    $round81SalesExportRows = @((Invoke-WebRequest -Uri "$base/api/export/sales?includeArchived=true" -UseBasicParsing).Content | ConvertFrom-Csv)
    $round81SalesExportRow = @($round81SalesExportRows | Where-Object { $_.InvoiceNumber -eq 'INV-2026-8101' })[0]
    Assert-Smoke ($null -ne $round81SalesExportRow) 'Round 81 Sales CSV export did not include the synced invoice sale.'
    Assert-SmokeClose $round81SalesExportRow.CustomerPaid (Get-SmokeDecimalOrZero $round81ReloadedDoc.amountPaid) 0.01 'Round 81 Sales CSV customer paid did not match API amount paid.'

    $round16RestoredConfig = Invoke-RestMethod `
        -Method PUT `
        -Uri "$base/api/config" `
        -ContentType 'application/json' `
        -Body ($round16ConfigBody | ConvertTo-Json -Depth 5)
    Assert-SmokeClose $round16RestoredConfig.calcGramRate 0.13 0.0001 'Round 81 did not restore the Round 16 calculator gram-rate setting.'
    Assert-SmokeClose $round16RestoredConfig.calcMinimum 28 0.0001 'Round 81 did not restore the Round 16 calculator minimum setting.'

    $round82InvalidConfigBody = [ordered]@{
        businessName = ('  Round 82' + [char]0 + ' Config Lab  ')
        businessLocation = ('  Test Location' + [char]7 + '  ')
        businessEmail = '  ROUND82@EXAMPLE.TEST  '
        businessPhone = ('  (973) 555-8282' + [char]1 + '  ')
        businessWebsite = "  https://round82.example.test/path  "
        businessEtsy = ('  https://etsy.com/shop/round82' + [char]2 + '  ')
        businessInstagram = '  @Round82  '
        businessFacebook = ('  Round 82 Page' + [char]3 + '  ')
        brandColor = 'url(javascript:alert(1))'
        calcGramRate = -5
        calcHourRate = 25000
        calcDesignRate = 99999
        calcSetupFee = -10
        calcPostFee = 2000000000
        calcMinimum = 0
    }
    $round82NormalizedConfig = Invoke-RestMethod `
        -Method PUT `
        -Uri "$base/api/config" `
        -ContentType 'application/json' `
        -Body ($round82InvalidConfigBody | ConvertTo-Json -Depth 5)
    Assert-Smoke ($round82NormalizedConfig.businessName -eq 'Round 82 Config Lab') 'Round 82 config business name was not stripped/trimmed safely.'
    Assert-Smoke ($round82NormalizedConfig.businessLocation -eq 'Test Location') 'Round 82 config location was not stripped/trimmed safely.'
    Assert-Smoke ($round82NormalizedConfig.businessEmail -eq 'round82@example.test') 'Round 82 config email was not normalized safely.'
    Assert-Smoke ($round82NormalizedConfig.businessPhone -eq '(973) 555-8282') 'Round 82 config phone was not stripped/trimmed safely.'
    Assert-Smoke ($round82NormalizedConfig.brandColor -eq '#17468f') 'Round 82 config invalid brand color did not fall back safely.'
    Assert-SmokeClose $round82NormalizedConfig.calcGramRate 0.05 0.0001 'Round 82 config negative gram rate did not fall back safely.'
    Assert-SmokeClose $round82NormalizedConfig.calcHourRate 10000 0.0001 'Round 82 config high hour rate did not clamp safely.'
    Assert-SmokeClose $round82NormalizedConfig.calcDesignRate 10000 0.0001 'Round 82 config high design rate did not clamp safely.'
    Assert-SmokeClose $round82NormalizedConfig.calcSetupFee 0 0.0001 'Round 82 config negative setup fee did not clamp safely.'
    Assert-SmokeClose $round82NormalizedConfig.calcPostFee 1000000000 0.0001 'Round 82 config high post fee did not clamp safely.'
    Assert-SmokeClose $round82NormalizedConfig.calcMinimum 15 0.0001 'Round 82 config zero minimum did not fall back safely.'

    $round82ReloadedConfig = Invoke-RestMethod "$base/api/config"
    Assert-Smoke ($round82ReloadedConfig.businessName -eq 'Round 82 Config Lab') 'Round 82 config reload did not preserve normalized business name.'
    Assert-Smoke ($round82ReloadedConfig.brandColor -eq '#17468f') 'Round 82 config reload did not preserve normalized brand color.'
    Assert-SmokeClose $round82ReloadedConfig.calcHourRate 10000 0.0001 'Round 82 config reload did not preserve clamped hour rate.'
    Assert-SmokeClose $round82ReloadedConfig.calcMinimum 15 0.0001 'Round 82 config reload did not preserve defaulted minimum.'

    $round82RestoredConfig = Invoke-RestMethod `
        -Method PUT `
        -Uri "$base/api/config" `
        -ContentType 'application/json' `
        -Body ($round16ConfigBody | ConvertTo-Json -Depth 5)
    Assert-SmokeClose $round82RestoredConfig.calcGramRate 0.13 0.0001 'Round 82 did not restore the Round 16 calculator gram-rate setting.'
    Assert-SmokeClose $round82RestoredConfig.calcMinimum 28 0.0001 'Round 82 did not restore the Round 16 calculator minimum setting.'

    $round26SecretSentinel = 'sk-round26-secret-value-that-must-not-export-123456'
    $round26SecretSetting = Invoke-SmokeJson 'POST' '/api/settings' ([ordered]@{
        key = 'Round26ApiKey'
        value = $round26SecretSentinel
        notes = 'Round 26 disposable secret exposure sentinel'
    })
    Assert-Smoke ($round26SecretSetting.value -eq $round26SecretSentinel) 'Round 26 secret sentinel was not saved to the disposable settings table.'

    $round26ConfigJson = Invoke-RestMethod "$base/api/config" | ConvertTo-Json -Depth 20
    Assert-Smoke ($round26ConfigJson -notlike "*$round26SecretSentinel*") 'Config API exposed the Round 26 secret sentinel.'
    Assert-SmokeNoSecretMaterial 'Config API' $round26ConfigJson

    foreach ($path in @('/', '/index.html', '/js/app.js', '/invoice-builder/', '/invoice-builder/index.html', '/invoice-builder/js/app.js', '/invoice-builder/js/utils.js')) {
        $content = Get-Text $path
        Assert-Smoke ($content -notlike "*$round26SecretSentinel*") "Static UI $path exposed the Round 26 secret sentinel."
        Assert-SmokeNoSecretMaterial "Static UI $path" $content
    }

    $payload = [ordered]@{
        docNumber = 'INV-2026-0001'
        docType = 'ESTIMATE'
        status = 'Draft'
        customerName = 'Smoke Customer'
        subtotal = 0
        discountAmount = 0
        rushAmount = 0
        taxAmount = 0
        total = 0
        amountPaid = 0
        lineItems = @(@{
            description = 'Smoke'
            quantity = 1
            rate = 10
        })
    }

    $wrongPrefix = Invoke-WebRequest `
        -Method POST `
        -Uri "$base/api/documents" `
        -ContentType 'application/json' `
        -Body ($payload | ConvertTo-Json -Depth 20) `
        -SkipHttpErrorCheck
    Assert-Smoke ($wrongPrefix.StatusCode -eq 400) "Wrong-prefix create expected 400, got $($wrongPrefix.StatusCode)."

    $payload.docNumber = 'EST-2026-0001'
    $created = Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/documents" `
        -ContentType 'application/json' `
        -Body ($payload | ConvertTo-Json -Depth 20)
    Assert-Smoke ($created.docNumber -eq 'EST-2026-0001') 'Created smoke estimate did not keep the expected number.'

    $duplicate = Invoke-WebRequest `
        -Method POST `
        -Uri "$base/api/documents" `
        -ContentType 'application/json' `
        -Body ($payload | ConvertTo-Json -Depth 20) `
        -SkipHttpErrorCheck
    Assert-Smoke ($duplicate.StatusCode -eq 400) "Duplicate create expected 400, got $($duplicate.StatusCode)."

    $latest = Invoke-RestMethod "$base/api/documents/latest"
    Assert-Smoke ($latest.docNumber -eq 'EST-2026-0001') 'Latest document endpoint returned the wrong smoke document.'

    $stats = Invoke-RestMethod "$base/api/documents/stats"
    Assert-Smoke ($stats.totalEstimates -ge 1) 'Document stats did not count the smoke estimate.'

    $archivedReservedNumber = 'EST-2025-0190'
    $archivedReserved = Invoke-SmokeJson 'POST' '/api/documents' (New-SmokeDocumentPayload $archivedReservedNumber 'ESTIMATE' 'Draft' 'Round19 Archived Number Customer' 'Round19 Archived Number Project' 19 0 0)
    Assert-Smoke ($archivedReserved.docNumber -eq $archivedReservedNumber) 'Archived-number reservation smoke did not save with the requested number.'
    $archiveReserved = Invoke-WebRequest -Method DELETE -Uri "$base/api/documents/$($archivedReserved.id)" -SkipHttpErrorCheck
    Assert-Smoke ($archiveReserved.StatusCode -eq 200) "Archiving number-reservation smoke document expected 200, got $($archiveReserved.StatusCode)."
    $archivedDuplicateAttempt = Invoke-WebRequest `
        -Method POST `
        -Uri "$base/api/documents" `
        -ContentType 'application/json' `
        -Body ((New-SmokeDocumentPayload $archivedReservedNumber 'ESTIMATE' 'Draft' 'Round19 Reuse Attempt Customer' 'Round19 Reuse Attempt Project' 29 0 0) | ConvertTo-Json -Depth 20) `
        -SkipHttpErrorCheck
    Assert-Smoke ($archivedDuplicateAttempt.StatusCode -eq 400) "Archived-number duplicate create expected 400, got $($archivedDuplicateAttempt.StatusCode)."
    Assert-Smoke ($archivedDuplicateAttempt.Content -like '*archived record*') 'Archived-number duplicate error did not explain the existing archived record.'
    Assert-Smoke ($archivedDuplicateAttempt.Content -like '*keep their numbers reserved*') 'Archived-number duplicate error did not document the reserved-number rule.'
    $restoredReserved = Invoke-RestMethod -Method POST -Uri "$base/api/documents/$($archivedReserved.id)/restore"
    Assert-Smoke ($restoredReserved.isArchived -eq $false) 'Restoring archived number-reservation smoke document did not clear IsArchived.'

    $beforeImportCount = @((Invoke-RestMethod "$base/api/documents")).Count
    $blankImport = Invoke-WebRequest `
        -Method POST `
        -Uri "$base/api/ai/invoice-document-draft/upload" `
        -Form @{ sourceName = 'blank scanned smoke import' } `
        -SkipHttpErrorCheck
    Assert-Smoke ($blankImport.StatusCode -eq 400) "Blank invoice PDF import expected 400, got $($blankImport.StatusCode)."
    Assert-Smoke ($blankImport.Content -like '*OCR*') 'Blank invoice PDF import did not explain OCR/readable PDF requirement.'

    $oversizedClient = [System.Net.Http.HttpClient]::new()
    $oversizedForm = [System.Net.Http.MultipartFormDataContent]::new()
    $oversizedBytes = [byte[]]::new((20 * 1024 * 1024) + 1)
    $oversizedContent = [System.Net.Http.ByteArrayContent]::new($oversizedBytes)
    $oversizedContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/pdf')
    $oversizedForm.Add([System.Net.Http.StringContent]::new('oversized-pdf-smoke.pdf'), 'sourceName')
    $oversizedForm.Add($oversizedContent, 'files', 'oversized-pdf-smoke.pdf')
    $oversizedResponse = $oversizedClient.PostAsync("$base/api/ai/invoice-document-draft/upload", $oversizedForm).GetAwaiter().GetResult()
    $oversizedText = $oversizedResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-Smoke ([int]$oversizedResponse.StatusCode -eq 400) "Oversized invoice PDF import expected 400, got $([int]$oversizedResponse.StatusCode)."
    Assert-Smoke ($oversizedText -like '*larger than the 20 MB per-file AI intake limit*') 'Oversized invoice PDF import did not explain the per-file limit.'
    $oversizedClient.Dispose()
    $oversizedForm.Dispose()
    $oversizedContent.Dispose()

    $wrongFormat = Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/ai/invoice-document-draft/upload" `
        -Form @{
            sourceName = 'wrong-format-smoke.txt'
            sourceText = 'This is a random note, not an EPATA invoice or estimate document.'
        }
    Assert-Smoke (@($wrongFormat.warnings | Where-Object { $_ -like '*No EPATA document number*' }).Count -ge 1) 'Wrong-format import did not warn about missing EPATA document number.'
    Assert-Smoke (@($wrongFormat.warnings | Where-Object { $_ -like '*No line-item table*' }).Count -ge 1) 'Wrong-format import did not warn about missing line items.'
    $afterWrongImportCount = @((Invoke-RestMethod "$base/api/documents")).Count
    Assert-Smoke ($afterWrongImportCount -eq $beforeImportCount) 'Wrong-format import created or changed document records.'

    $round72MultiPageImport = Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/ai/invoice-document-draft/upload" `
        -Form @{
            sourceName = 'round72-multipage-estimate.pdf'
            sourceText = 'ESTIMATE Estimate # EST-2026-7201 Date June 15, 2026 Valid Until June 29, 2026 Prepared For Round72 Multi Customer Bill To Round72 Multi Customer (973) 555-7201 multi72@example.test 72 Multi Page Lane Page 1 of 2 Project Details Project Name: Round72 Multi Page Project Description: Page one description Material: PLA Color: White Infill: 20% Pricing Summary Page 2 of 2 Subtotal $145.00 Discount -$5.00 Rush Fee (10%) $14.50 Tax (6.625%) $10.23 Estimated Total $164.73 ESTIMATE Breakdown # Description Calculation / Details Qty Rate Amount 1 Round72 Multi Line - Page two recovered line detail 2 $72.50 $145.00 Pricing Guide Round72 customer-facing pricing Terms & Notes Round72 customer-facing terms Approval Status Sent'
        }
    Assert-Smoke ($round72MultiPageImport.prefill.docType -eq 'ESTIMATE') 'Round 72 multi-page import did not detect ESTIMATE.'
    Assert-Smoke ($round72MultiPageImport.prefill.docNumber -eq 'EST-2026-7201') 'Round 72 multi-page import did not recover document number.'
    Assert-Smoke ($round72MultiPageImport.prefill.docDate -eq '2026-06-15') 'Round 72 multi-page import did not recover document date.'
    Assert-Smoke ($round72MultiPageImport.prefill.dueDate -eq '2026-06-29') 'Round 72 multi-page import did not recover valid-until date.'
    Assert-Smoke ($round72MultiPageImport.prefill.customerName -eq 'Round72 Multi Customer') 'Round 72 multi-page import did not recover customer name.'
    Assert-Smoke ($round72MultiPageImport.prefill.customerPhone -eq '(973) 555-7201') 'Round 72 multi-page import did not recover customer phone.'
    Assert-Smoke ($round72MultiPageImport.prefill.customerEmail -eq 'multi72@example.test') 'Round 72 multi-page import did not recover customer email.'
    Assert-Smoke ($round72MultiPageImport.prefill.customerAddress -like '*72 Multi Page Lane*') 'Round 72 multi-page import did not recover customer address.'
    Assert-Smoke ($round72MultiPageImport.prefill.projectName -eq 'Round72 Multi Page Project') "Round 72 multi-page import did not recover project name. Got '$($round72MultiPageImport.prefill.projectName)'."
    Assert-Smoke ($round72MultiPageImport.prefill.material -eq 'PLA') 'Round 72 multi-page import did not recover material.'
    Assert-Smoke ($round72MultiPageImport.prefill.color -eq 'White') 'Round 72 multi-page import did not recover color.'
    Assert-Smoke ($round72MultiPageImport.prefill.infill -eq '20%') 'Round 72 multi-page import did not recover infill.'
    Assert-Smoke ([math]::Abs([decimal]$round72MultiPageImport.prefill.docDiscount - 5) -lt 0.01) 'Round 72 multi-page import did not recover discount.'
    Assert-Smoke ([math]::Abs([decimal]$round72MultiPageImport.prefill.docRushPercent - 10) -lt 0.01) 'Round 72 multi-page import did not recover rush percent.'
    Assert-Smoke ([math]::Abs([decimal]$round72MultiPageImport.prefill.docTaxRate - 6.625) -lt 0.01) 'Round 72 multi-page import did not recover tax rate.'
    Assert-Smoke (@($round72MultiPageImport.prefill.lineItems).Count -eq 1) 'Round 72 multi-page import did not recover the line item.'
    Assert-Smoke (@($round72MultiPageImport.prefill.lineItems)[0].description -like '*Round72 Multi Line*') 'Round 72 multi-page import did not recover line description.'
    Assert-Smoke (@($round72MultiPageImport.prefill.lineItems)[0].details -like '*Page two recovered line detail*') 'Round 72 multi-page import did not recover line details from later page text.'
    Assert-Smoke ($round72MultiPageImport.prefill.pricingGuide -like '*Round72 customer-facing pricing*') 'Round 72 multi-page import did not recover pricing guide.'
    Assert-Smoke ($round72MultiPageImport.prefill.termsNotes -like '*Round72 customer-facing terms*') 'Round 72 multi-page import did not recover terms.'
    $afterRound72MultiPageCount = @((Invoke-RestMethod "$base/api/documents")).Count
    Assert-Smoke ($afterRound72MultiPageCount -eq $beforeImportCount) 'Round 72 multi-page import created or changed document records before user save.'

    $round72MissingContactImport = Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/ai/invoice-document-draft/upload" `
        -Form @{
            sourceName = 'round72-missing-contact-invoice.pdf'
            sourceText = 'INVOICE Invoice # INV-2026-7202 Date July 1, 2026 Due Date July 8, 2026 Prepared For Round72 Missing Contact Customer Bill To Round72 Missing Contact Customer Project Details Project Name: Round72 Missing Contact Project Description: Contact details intentionally absent Material: PETG Color: Black Infill: 25% Pricing Summary Subtotal $90.00 Discount -$0.00 Rush Fee (0%) $0.00 Tax (0%) $0.00 Invoice Total $90.00 Amount Paid $0.00 INVOICE Breakdown # Description Calculation / Details Qty Rate Amount 1 Round72 Missing Contact Line Available fields still map 1 $90.00 $90.00 Payment Status Status Sent'
        }
    Assert-Smoke ($round72MissingContactImport.prefill.docType -eq 'INVOICE') 'Round 72 missing-contact import did not detect INVOICE.'
    Assert-Smoke ($round72MissingContactImport.prefill.docNumber -eq 'INV-2026-7202') 'Round 72 missing-contact import did not recover document number.'
    Assert-Smoke ($round72MissingContactImport.prefill.customerName -eq 'Round72 Missing Contact Customer') 'Round 72 missing-contact import did not recover customer name.'
    Assert-Smoke ([string]::IsNullOrWhiteSpace([string]$round72MissingContactImport.prefill.customerEmail)) 'Round 72 missing-contact import should leave absent email blank.'
    Assert-Smoke ([string]::IsNullOrWhiteSpace([string]$round72MissingContactImport.prefill.customerPhone)) 'Round 72 missing-contact import should leave absent phone blank.'
    Assert-Smoke ([string]::IsNullOrWhiteSpace([string]$round72MissingContactImport.prefill.customerAddress)) 'Round 72 missing-contact import should leave absent address blank.'
    Assert-Smoke ($round72MissingContactImport.prefill.projectName -eq 'Round72 Missing Contact Project') 'Round 72 missing-contact import did not recover available project name.'
    Assert-Smoke (@($round72MissingContactImport.prefill.lineItems).Count -eq 1) 'Round 72 missing-contact import did not recover available line item.'
    $afterRound72MissingContactCount = @((Invoke-RestMethod "$base/api/documents")).Count
    Assert-Smoke ($afterRound72MissingContactCount -eq $beforeImportCount) 'Round 72 missing-contact import created or changed document records before user save.'

    $round86OldWordingImport = Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/ai/invoice-document-draft/upload" `
        -Form @{
            sourceName = 'round86-old-wording-invoice.pdf'
            sourceText = 'INVOICE Invoice No: INV-2026-8601 Date: 06/18/2026 Due: 06/25/2026 Client: Round86 Old Wording Customer (973) 555-8601 round86@example.test 86 Legacy Lane Project Details Job Name - Round86 Vintage Sign Job Description - Replacement shop sign with older wording Filament - PETG Colour - Navy Fill - 35% Pricing Summary Subtotal: $120.00 Less Discount: -$10.00 Rush (5%) $6.00 Sales Tax (6.625%) $7.69 Grand Total: $123.69 Paid: $50.00 Line Items # Description Details Qty Unit Price Total 1 Round86 Old Line - Legacy line details 2 $60.00 $120.00 Pricing Notes Round86 customer-facing pricing Notes Round86 customer-facing terms Status: Partial'
        }
    Assert-Smoke ($round86OldWordingImport.prefill.docType -eq 'INVOICE') 'Round 86 old-wording import did not detect INVOICE.'
    Assert-Smoke ($round86OldWordingImport.prefill.docNumber -eq 'INV-2026-8601') 'Round 86 old-wording import did not recover document number.'
    Assert-Smoke ($round86OldWordingImport.prefill.docDate -eq '2026-06-18') 'Round 86 old-wording import did not recover document date with colon punctuation.'
    Assert-Smoke ($round86OldWordingImport.prefill.dueDate -eq '2026-06-25') 'Round 86 old-wording import did not recover short Due date wording.'
    Assert-Smoke ($round86OldWordingImport.prefill.customerName -eq 'Round86 Old Wording Customer') "Round 86 old-wording import did not recover customer name from Client wording. Got '$($round86OldWordingImport.prefill.customerName)'."
    Assert-Smoke ($round86OldWordingImport.prefill.customerPhone -eq '(973) 555-8601') 'Round 86 old-wording import did not recover customer phone from Client wording.'
    Assert-Smoke ($round86OldWordingImport.prefill.customerEmail -eq 'round86@example.test') 'Round 86 old-wording import did not recover customer email from Client wording.'
    Assert-Smoke ($round86OldWordingImport.prefill.customerAddress -like '*86 Legacy Lane*') 'Round 86 old-wording import did not recover customer address from Client wording.'
    Assert-Smoke ($round86OldWordingImport.prefill.projectName -eq 'Round86 Vintage Sign') "Round 86 old-wording import did not recover Job Name as project name. Got '$($round86OldWordingImport.prefill.projectName)'."
    Assert-Smoke ($round86OldWordingImport.prefill.projectDescription -eq 'Replacement shop sign with older wording') 'Round 86 old-wording import did not recover Job Description.'
    Assert-Smoke ($round86OldWordingImport.prefill.material -eq 'PETG') 'Round 86 old-wording import did not recover Filament as material.'
    Assert-Smoke ($round86OldWordingImport.prefill.color -eq 'Navy') 'Round 86 old-wording import did not recover Colour as color.'
    Assert-Smoke ($round86OldWordingImport.prefill.infill -eq '35%') 'Round 86 old-wording import did not recover Fill as infill.'
    Assert-Smoke ([math]::Abs([decimal]$round86OldWordingImport.prefill.docDiscount - 10) -lt 0.01) 'Round 86 old-wording import did not recover Less Discount.'
    Assert-Smoke ([math]::Abs([decimal]$round86OldWordingImport.prefill.docRushPercent - 5) -lt 0.01) 'Round 86 old-wording import did not recover Rush percent without Fee wording.'
    Assert-Smoke ([math]::Abs([decimal]$round86OldWordingImport.prefill.docTaxRate - 6.625) -lt 0.01) 'Round 86 old-wording import did not recover Sales Tax rate.'
    Assert-Smoke ([math]::Abs([decimal]$round86OldWordingImport.prefill.amountPaid - 50) -lt 0.01) 'Round 86 old-wording import did not recover Paid as amount paid.'
    Assert-Smoke ($round86OldWordingImport.prefill.status -eq 'Partial') 'Round 86 old-wording import did not infer Partial from a partially paid invoice.'
    Assert-Smoke (@($round86OldWordingImport.prefill.lineItems).Count -eq 1) 'Round 86 old-wording import did not recover old Line Items table.'
    Assert-Smoke (@($round86OldWordingImport.prefill.lineItems)[0].description -eq 'Round86 Old Line') 'Round 86 old-wording import did not recover old line-item description.'
    Assert-Smoke (@($round86OldWordingImport.prefill.lineItems)[0].details -like '*Legacy line details*') 'Round 86 old-wording import did not recover old line-item details.'
    Assert-Smoke ([math]::Abs([decimal]$round86OldWordingImport.pricing.total - 123.69) -lt 0.01) "Round 86 old-wording import total expected 123.69, got $($round86OldWordingImport.pricing.total)."
    Assert-Smoke ($round86OldWordingImport.prefill.pricingGuide -like '*Round86 customer-facing pricing*') 'Round 86 old-wording import did not recover Pricing Notes.'
    Assert-Smoke ($round86OldWordingImport.prefill.termsNotes -like '*Round86 customer-facing terms*') "Round 86 old-wording import did not recover Notes as terms. Got '$($round86OldWordingImport.prefill.termsNotes)'."
    $afterRound86OldWordingCount = @((Invoke-RestMethod "$base/api/documents")).Count
    Assert-Smoke ($afterRound86OldWordingCount -eq $beforeImportCount) 'Round 86 old-wording import created or changed document records before user save.'

    $duplicateImport = Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/ai/invoice-document-draft/upload" `
        -Form @{
            sourceName = 'duplicate-estimate-smoke.pdf'
            sourceText = 'ESTIMATE Estimate # EST-2026-0001 Date June 10, 2026 Valid Until June 24, 2026 Prepared For Smoke Customer Bill To Smoke Customer smoke@example.test Project Details Project Name: Smoke Duplicate Description: Duplicate estimate import Material: PETG Color: Blue Infill: 20% Pricing Summary Subtotal $75.00 Discount -$0.00 Rush Fee (0%) $0.00 Tax (0%) $0.00 Estimated Total $75.00 ESTIMATE Breakdown # Description Calculation / Details Qty Rate Amount 1 Smoke duplicate Imported duplicate line 1 $75.00 $75.00 Approval Status Sent'
        }
    Assert-Smoke (@($duplicateImport.warnings | Where-Object { $_ -like '*already exists*' }).Count -ge 1) 'Duplicate-number PDF import did not warn before saving.'
    $duplicateImportSave = Invoke-WebRequest `
        -Method POST `
        -Uri "$base/api/documents" `
        -ContentType 'application/json' `
        -Body ((New-SavePayloadFromPrefill $duplicateImport.prefill) | ConvertTo-Json -Depth 20) `
        -SkipHttpErrorCheck
    Assert-Smoke ($duplicateImportSave.StatusCode -eq 400) "Saving duplicate-number imported draft expected 400, got $($duplicateImportSave.StatusCode)."

    $paidImport = Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/ai/invoice-document-draft/upload" `
        -Form @{
            sourceName = 'paid-invoice-smoke.pdf'
            sourceText = 'INVOICE Invoice # INV-2026-0200 Date June 12, 2026 Due Date June 19, 2026 Prepared For Smoke Paid Customer Bill To Smoke Paid Customer (973) 555-0200 paidsmoke@example.test 200 Smoke Ave Project Details Project Name: Imported Paid Smoke Description: Imported paid invoice Material: PLA Color: Red Infill: 20% Pricing Summary Subtotal $100.00 Discount -$0.00 Rush Fee (0%) $0.00 Tax (10%) $10.00 Invoice Total $110.00 Amount Paid $110.00 INVOICE Breakdown # Description Calculation / Details Qty Rate Amount 1 Imported smoke Smoke paid line 1 $100.00 $100.00 Payment Status Status Paid'
        }
    Assert-Smoke ($paidImport.prefill.docType -eq 'INVOICE') 'Paid invoice import did not detect INVOICE.'
    Assert-Smoke ($paidImport.prefill.status -eq 'Paid') 'Paid invoice import did not detect Paid status.'
    $paidPayload = New-SavePayloadFromPrefill $paidImport.prefill 'Zelle'
    $paidDocument = Invoke-RestMethod `
        -Method POST `
        -Uri "$base/api/documents" `
        -ContentType 'application/json' `
        -Body ($paidPayload | ConvertTo-Json -Depth 20)
    Assert-Smoke ($paidDocument.docType -eq 'INVOICE') 'Saved imported paid draft did not create an invoice.'
    Assert-Smoke ($paidDocument.status -eq 'Paid') 'Saved imported paid draft did not remain paid.'
    Assert-Smoke ([math]::Abs([decimal]$paidDocument.total - 110) -lt 0.01) "Saved imported paid draft total expected 110, got $($paidDocument.total)."
    Assert-Smoke ([math]::Abs([decimal]$paidDocument.amountPaid - 110) -lt 0.01) "Saved imported paid draft paid expected 110, got $($paidDocument.amountPaid)."
    $syncedSales = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq 'INV-2026-0200' })
    Assert-Smoke ($syncedSales.Count -eq 1) 'Saved imported paid invoice did not sync exactly one Sale row.'
    $syncedAr = @(Invoke-RestMethod "$base/api/receivable-invoices?includeArchived=true" | Where-Object { $_.invoiceNumber -eq 'INV-2026-0200' })
    Assert-Smoke ($syncedAr.Count -eq 1) 'Saved imported paid invoice did not sync exactly one AR row.'

    $statsBeforeRound44 = Invoke-RestMethod "$base/api/documents/stats"
    $round44Number = 'INV-2026-0440'
    $round44Customer = 'Round44 Shared Customer'
    $round44ExternalSource = 'External invoice PDF https://example.test/round44.pdf'
    $round44ExternalAr = Invoke-SmokeJson 'POST' '/api/receivable-invoices' ([ordered]@{
        invoiceNumber = $round44Number
        invoiceDate = '2026-06-19'
        dueDate = '2026-07-19'
        customerName = $round44Customer
        projectName = 'Round44 External AR'
        status = 'Sent'
        subtotal = 333
        discount = 0
        rushFee = 0
        taxRatePercent = 0
        salesTax = 0
        invoiceTotal = 333
        amountPaid = 0
        paymentMethod = 'Check'
        sourceProof = $round44ExternalSource
        includeInCashReports = $false
        needsReview = $false
        notes = 'Round 44 external AR must stay separate from builder PDFs.'
    })
    $round44Doc = Invoke-SmokeJson 'POST' '/api/documents' (New-SmokeDocumentPayload $round44Number 'INVOICE' 'Sent' $round44Customer 'Round44 Builder PDF' 110 0 0)
    $round44ArRows = @(Get-SmokeJsonArray '/api/receivable-invoices?includeArchived=true' | Where-Object { $_.invoiceNumber -eq $round44Number -and $_.customerName -eq $round44Customer })
    Assert-Smoke ($round44ArRows.Count -eq 2) "Round 44 expected one external AR row and one generated builder AR row, got $($round44ArRows.Count)."
    $round44ExternalReloaded = @($round44ArRows | Where-Object { $_.id -eq $round44ExternalAr.id })[0]
    Assert-Smoke ($null -ne $round44ExternalReloaded) 'Round 44 external AR row disappeared after saving a matching builder invoice.'
    Assert-Smoke ($round44ExternalReloaded.sourceProof -eq $round44ExternalSource) 'Round 44 builder invoice sync overwrote the external AR source proof.'
    Assert-Smoke ($round44ExternalReloaded.projectName -eq 'Round44 External AR') 'Round 44 builder invoice sync overwrote the external AR project name.'
    Assert-SmokeClose $round44ExternalReloaded.invoiceTotal 333 0.01 'Round 44 external AR invoice total changed after builder invoice save.'
    $round44GeneratedAr = @($round44ArRows | Where-Object { $_.sourceProof -eq "Unified invoice $round44Number" })
    Assert-Smoke ($round44GeneratedAr.Count -eq 1) 'Round 44 builder invoice did not create its own generated AR row.'
    Assert-Smoke ($round44GeneratedAr[0].id -ne $round44ExternalAr.id) 'Round 44 generated AR reused the external AR row id.'
    Assert-SmokeClose $round44GeneratedAr[0].invoiceTotal 110 0.01 'Round 44 generated AR did not match the builder invoice total.'
    $round44Records = @(Get-SmokeJsonArray "/api/documents?q=$round44Number&type=INVOICE")
    $round44DocumentRows = @($round44Records | Where-Object { $_.sourceKind -eq 'document' -and $_.sourceId -eq $round44Doc.id })
    $round44ExternalRecordRows = @($round44Records | Where-Object { $_.sourceKind -eq 'receivable' -and $_.sourceId -eq $round44ExternalAr.id })
    $round44GeneratedRecordRows = @($round44Records | Where-Object { $_.sourceKind -eq 'receivable' -and $_.sourceId -eq $round44GeneratedAr[0].id })
    Assert-Smoke ($round44DocumentRows.Count -eq 1) 'Round 44 invoice records did not show the builder PDF document row.'
    Assert-Smoke ($round44ExternalRecordRows.Count -eq 1) 'Round 44 invoice records did not show the external AR row separately.'
    Assert-Smoke ($round44GeneratedRecordRows.Count -eq 0) 'Round 44 invoice records exposed the generated AR row behind the builder PDF.'
    $statsAfterRound44 = Invoke-RestMethod "$base/api/documents/stats"
    Assert-Smoke (($statsAfterRound44.totalInvoices - $statsBeforeRound44.totalInvoices) -eq 2) 'Round 44 invoice stats did not count exactly the builder invoice plus external AR row.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $statsAfterRound44.unpaidBalance) - (Get-SmokeDecimalOrZero $statsBeforeRound44.unpaidBalance)) 443 0.01 'Round 44 unpaid invoice stats did not keep builder and external AR balances separate.'

    $year = [DateTime]::Now.Year
    $gapEstimate = Invoke-SmokeJson 'POST' '/api/documents' (New-SmokeDocumentPayload "EST-$year-0005" 'ESTIMATE' 'Draft' 'Round4 Gap Customer' 'Round4 Gap Estimate' 55 0 0)
    Assert-Smoke ($gapEstimate.docNumber -eq "EST-$year-0005") 'Explicit gap estimate did not save with the requested number.'
    $nextEstimate = Invoke-RestMethod "$base/api/documents/next-number?type=ESTIMATE"
    Assert-Smoke ($nextEstimate.number -eq "EST-$year-0006") "Next estimate number did not skip existing gap. Got $($nextEstimate.number)."

    $manualEstimateNumber = "EST-$year-0910"
    $manualEstimateJob = Invoke-SmokeJson 'POST' '/api/customer-jobs' ([ordered]@{
        jobDate = '2026-06-19'
        customerName = 'Round6 Manual Job Customer'
        platform = 'Direct'
        jobNumber = 'JOB-R6-MANUAL'
        relatedInvoiceNumber = $manualEstimateNumber
        jobName = 'Round6 Manual Job'
        jobType = 'Print'
        status = 'Open'
        productName = 'Manual Widget'
        material = 'PETG'
        color = 'White'
        description = 'Manual job must not be overwritten by estimate sync.'
        quoteAmount = 12
        invoiceAmount = 34
        amountPaid = 5
        dueDate = '2026-06-29'
        sourceProof = 'Manual Round6 job'
        needsReview = $false
        notes = 'Manual job collision guard.'
    })
    $manualCollisionEstimate = Invoke-SmokeJson 'POST' '/api/documents' (New-SmokeDocumentPayload $manualEstimateNumber 'ESTIMATE' 'Sent' 'Round6 Generated Estimate Customer' 'Round6 Generated Estimate' 64 999 0)
    Assert-Smoke ($manualCollisionEstimate.docNumber -eq $manualEstimateNumber) 'Manual-collision estimate did not save with the intended number.'
    Assert-Smoke ([math]::Abs([decimal]$manualCollisionEstimate.amountPaid) -lt 0.01) 'Manual-collision estimate did not force amount paid to zero.'
    $collisionJobs = @(Invoke-RestMethod "$base/api/customer-jobs?includeArchived=true" | Where-Object { $_.relatedInvoiceNumber -eq $manualEstimateNumber })
    $manualCollisionJobs = @($collisionJobs | Where-Object { $_.id -eq $manualEstimateJob.id })
    $generatedCollisionJobs = @($collisionJobs | Where-Object { $_.sourceProof -eq "Unified estimate $manualEstimateNumber" })
    Assert-Smoke ($manualCollisionJobs.Count -eq 1) 'Manual Customer Job disappeared during estimate sync.'
    Assert-Smoke ($manualCollisionJobs[0].sourceProof -eq 'Manual Round6 job') 'Estimate sync overwrote the manual Customer Job source proof.'
    Assert-Smoke ($manualCollisionJobs[0].jobName -eq 'Round6 Manual Job') 'Estimate sync overwrote the manual Customer Job name.'
    Assert-Smoke ($generatedCollisionJobs.Count -eq 1) 'Estimate sync did not create a separate generated Customer Job when a manual job shared the reference number.'
    Assert-Smoke ($generatedCollisionJobs[0].jobType -eq 'Estimate') 'Generated estimate Customer Job did not keep JobType Estimate.'
    Assert-Smoke ($generatedCollisionJobs[0].status -eq 'Quoted') 'Generated estimate Customer Job did not use quote-tracking status.'
    Assert-Smoke ([math]::Abs((Get-SmokeDecimalOrZero $generatedCollisionJobs[0].amountPaid)) -lt 0.01) 'Generated estimate Customer Job should not carry a positive amount paid.'
    Assert-Smoke ([math]::Abs((Get-SmokeDecimalOrZero $generatedCollisionJobs[0].invoiceAmount)) -lt 0.01) 'Generated estimate Customer Job should not carry a positive invoice amount.'
    $manualCollisionAr = @(Invoke-RestMethod "$base/api/receivable-invoices?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $manualEstimateNumber })
    $manualCollisionSales = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $manualEstimateNumber })
    Assert-Smoke ($manualCollisionAr.Count -eq 0) 'Estimate sync created an AR row for an estimate.'
    Assert-Smoke ($manualCollisionSales.Count -eq 0) 'Estimate sync created a Sale row for an estimate.'

    $acceptedEstimateNumber = "EST-$year-0911"
    $acceptedEstimate = Invoke-SmokeJson 'POST' '/api/documents' (New-SmokeDocumentPayload $acceptedEstimateNumber 'ESTIMATE' 'Accepted' 'Round6 Accepted Customer' 'Round6 Accepted Estimate' 84 999 0)
    Assert-Smoke ($acceptedEstimate.status -eq 'Accepted') 'Accepted estimate did not save with Accepted status.'
    Assert-Smoke ([math]::Abs([decimal]$acceptedEstimate.amountPaid) -lt 0.01) 'Accepted estimate did not force amount paid to zero.'
    $acceptedJobs = @(Invoke-RestMethod "$base/api/customer-jobs?includeArchived=true" | Where-Object { $_.sourceProof -eq "Unified estimate $acceptedEstimateNumber" })
    Assert-Smoke ($acceptedJobs.Count -eq 1) 'Accepted estimate did not create exactly one generated Customer Job.'
    Assert-Smoke ($acceptedJobs[0].relatedInvoiceNumber -eq $acceptedEstimateNumber) 'Accepted estimate Customer Job linked the wrong estimate number.'
    Assert-Smoke ($acceptedJobs[0].status -eq 'Quoted') 'Accepted estimate Customer Job should remain quote-tracking until converted.'
    Assert-Smoke ([math]::Abs((Get-SmokeDecimalOrZero $acceptedJobs[0].amountPaid)) -lt 0.01) 'Accepted estimate Customer Job should not carry a positive amount paid.'
    Assert-Smoke ([math]::Abs((Get-SmokeDecimalOrZero $acceptedJobs[0].invoiceAmount)) -lt 0.01) 'Accepted estimate Customer Job should not carry a positive invoice amount.'

    $acceptedEstimateRenumberedNumber = "EST-$year-0912"
    $acceptedEstimateRenumberPayload = New-SmokeDocumentPayload $acceptedEstimateRenumberedNumber 'ESTIMATE' 'Accepted' 'Round6 Accepted Customer' 'Round6 Accepted Estimate Renumbered' 94 999 0
    $acceptedEstimateRenumbered = Invoke-SmokeJson 'PUT' "/api/documents/$($acceptedEstimate.id)" $acceptedEstimateRenumberPayload
    Assert-Smoke ($acceptedEstimateRenumbered.id -eq $acceptedEstimate.id) 'Estimate renumber changed the document id.'
    Assert-Smoke ($acceptedEstimateRenumbered.docNumber -eq $acceptedEstimateRenumberedNumber) 'Estimate renumber did not persist the new estimate number.'
    $oldEstimateJobsAfterRenumber = @(Invoke-RestMethod "$base/api/customer-jobs?includeArchived=true" | Where-Object { $_.sourceProof -eq "Unified estimate $acceptedEstimateNumber" })
    $newEstimateJobsAfterRenumber = @(Invoke-RestMethod "$base/api/customer-jobs?includeArchived=true" | Where-Object { $_.sourceProof -eq "Unified estimate $acceptedEstimateRenumberedNumber" })
    Assert-Smoke ($oldEstimateJobsAfterRenumber.Count -eq 0) 'Estimate renumber left an orphan generated Customer Job on the old estimate number.'
    Assert-Smoke ($newEstimateJobsAfterRenumber.Count -eq 1) 'Estimate renumber did not keep exactly one generated Customer Job on the new estimate number.'
    Assert-Smoke ($newEstimateJobsAfterRenumber[0].relatedInvoiceNumber -eq $acceptedEstimateRenumberedNumber) 'Renumbered estimate Customer Job linked the wrong estimate number.'
    $acceptedEstimateAr = @(Invoke-RestMethod "$base/api/receivable-invoices?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $acceptedEstimateRenumberedNumber })
    $acceptedEstimateSales = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $acceptedEstimateRenumberedNumber })
    Assert-Smoke ($acceptedEstimateAr.Count -eq 0) 'Accepted estimate renumber created an AR row.'
    Assert-Smoke ($acceptedEstimateSales.Count -eq 0) 'Accepted estimate renumber created a Sale row.'

    $blankEstimatePayload = New-SmokeDocumentPayload '' 'ESTIMATE' 'Draft' 'Round4 Workflow Customer' 'Round4 Workflow Estimate' 123 0 10
    $round4Estimate = Invoke-SmokeJson 'POST' '/api/documents' $blankEstimatePayload
    Assert-Smoke ($round4Estimate.id -gt 0) 'Blank-number Save Draft did not create a document.'
    Assert-Smoke ($round4Estimate.docType -eq 'ESTIMATE') 'Blank-number Save Draft did not create an estimate.'
    Assert-Smoke ($round4Estimate.docNumber -like "EST-$year-*") "Blank-number estimate did not receive an EST number. Got $($round4Estimate.docNumber)."
    Assert-Smoke ([math]::Abs([decimal]$round4Estimate.amountPaid) -lt 0.01) 'Estimate Save Draft did not force amount paid to zero.'

    $round4EstimateUpdatePayload = New-SmokeDocumentPayload $round4Estimate.docNumber 'ESTIMATE' 'Sent' 'Round4 Workflow Customer Updated' 'Round4 Workflow Estimate Updated' 123 999 10
    $round4EstimateUpdated = Invoke-SmokeJson 'PUT' "/api/documents/$($round4Estimate.id)" $round4EstimateUpdatePayload
    Assert-Smoke ($round4EstimateUpdated.id -eq $round4Estimate.id) 'Save Draft update changed the active estimate id.'
    Assert-Smoke ($round4EstimateUpdated.docNumber -eq $round4Estimate.docNumber) 'Save Draft update changed the estimate number.'
    Assert-Smoke ($round4EstimateUpdated.status -eq 'Sent') 'Save Draft update did not persist same-type status.'
    Assert-Smoke ([math]::Abs([decimal]$round4EstimateUpdated.amountPaid) -lt 0.01) 'Same-type estimate update did not keep amount paid at zero.'

    $unsafeMutationPayload = New-SmokeDocumentPayload $round4Estimate.docNumber 'INVOICE' 'Paid' 'Round4 Workflow Customer Updated' 'Round4 Workflow Estimate Updated' 123 999 10
    $unsafeMutation = Invoke-WebRequest `
        -Method PUT `
        -Uri "$base/api/documents/$($round4Estimate.id)" `
        -ContentType 'application/json' `
        -Body ($unsafeMutationPayload | ConvertTo-Json -Depth 20) `
        -SkipHttpErrorCheck
    Assert-Smoke ($unsafeMutation.StatusCode -eq 400) "Unsafe estimate-to-invoice save expected 400, got $($unsafeMutation.StatusCode)."
    Assert-Smoke ($unsafeMutation.Content -like '*Cannot change saved ESTIMATE*') 'Unsafe estimate-to-invoice save did not return the clear type-change message.'
    $preservedEstimate = Invoke-RestMethod "$base/api/documents/$($round4Estimate.id)"
    Assert-Smoke ($preservedEstimate.docType -eq 'ESTIMATE') 'Unsafe mutation changed the original estimate type.'
    Assert-Smoke ($preservedEstimate.docNumber -eq $round4Estimate.docNumber) 'Unsafe mutation changed the original estimate number.'
    Assert-Smoke ([math]::Abs([decimal]$preservedEstimate.amountPaid) -lt 0.01) 'Unsafe mutation changed the original estimate paid amount.'
    $mutationSales = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $round4Estimate.docNumber })
    $mutationAr = @(Invoke-RestMethod "$base/api/receivable-invoices?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $round4Estimate.docNumber })
    Assert-Smoke ($mutationSales.Count -eq 0) 'Failed estimate-to-invoice save created a Sale side effect.'
    Assert-Smoke ($mutationAr.Count -eq 0) 'Failed estimate-to-invoice save created an AR side effect.'

    $convertedInvoice = Invoke-RestMethod -Method POST -Uri "$base/api/documents/$($round4Estimate.id)/convert-to-invoice"
    Assert-Smoke ($convertedInvoice.docType -eq 'INVOICE') 'Convert estimate action did not create an invoice.'
    Assert-Smoke ($convertedInvoice.docNumber -like "INV-$year-*") "Converted invoice did not receive an INV number. Got $($convertedInvoice.docNumber)."
    Assert-Smoke ($convertedInvoice.id -ne $round4Estimate.id) 'Convert estimate action reused the estimate id.'
    $estimateAfterConvert = Invoke-RestMethod "$base/api/documents/$($round4Estimate.id)"
    Assert-Smoke ($estimateAfterConvert.docType -eq 'ESTIMATE') 'Converted source estimate changed type.'
    Assert-Smoke ($estimateAfterConvert.docNumber -eq $round4Estimate.docNumber) 'Converted source estimate changed number.'
    Assert-Smoke ($estimateAfterConvert.status -eq 'Accepted') 'Converted source estimate was not marked Accepted.'

    $paidConvertedPayload = New-SmokeDocumentPayload $convertedInvoice.docNumber 'INVOICE' 'Paid' 'Round4 Workflow Customer Updated' 'Round4 Workflow Estimate Updated' 123 0 10
    $paidConverted = Invoke-SmokeJson 'PUT' "/api/documents/$($convertedInvoice.id)" $paidConvertedPayload
    Assert-Smoke ($paidConverted.status -eq 'Paid') 'Converted invoice did not save as Paid.'
    Assert-Smoke ([math]::Abs([decimal]$paidConverted.total - 135.30) -lt 0.01) "Converted paid invoice total expected 135.30, got $($paidConverted.total)."
    Assert-Smoke ([math]::Abs([decimal]$paidConverted.amountPaid - [decimal]$paidConverted.total) -lt 0.01) 'Paid converted invoice did not auto-fill amount paid.'
    Assert-Smoke ([math]::Abs([decimal]$paidConverted.balance) -lt 0.01) 'Paid converted invoice balance was not zero.'
    $paidConvertedAgain = Invoke-SmokeJson 'PUT' "/api/documents/$($convertedInvoice.id)" $paidConvertedPayload
    Assert-Smoke ($paidConvertedAgain.id -eq $convertedInvoice.id) 'Repeated paid invoice save changed the invoice id.'
    $round4Sales = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $convertedInvoice.docNumber })
    $round4Ar = @(Invoke-RestMethod "$base/api/receivable-invoices?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $convertedInvoice.docNumber })
    Assert-Smoke ($round4Sales.Count -eq 1) 'Repeated paid invoice save did not keep exactly one Sale row.'
    Assert-Smoke ($round4Ar.Count -eq 1) 'Repeated paid invoice save did not keep exactly one AR row.'

    $invoiceDuplicate = Invoke-RestMethod -Method POST -Uri "$base/api/documents/$($convertedInvoice.id)/duplicate"
    Assert-Smoke ($invoiceDuplicate.docType -eq 'INVOICE') 'Duplicate from invoice did not stay invoice.'
    Assert-Smoke ($invoiceDuplicate.docNumber -like "INV-$year-*") 'Duplicate invoice did not get an INV number.'
    Assert-Smoke ($invoiceDuplicate.docNumber -ne $convertedInvoice.docNumber) 'Duplicate invoice reused the source invoice number.'
    Assert-Smoke ($invoiceDuplicate.id -ne $convertedInvoice.id) 'Duplicate invoice reused the source invoice id.'

    $archiveInvoice = Invoke-WebRequest -Method DELETE -Uri "$base/api/documents/$($convertedInvoice.id)" -SkipHttpErrorCheck
    Assert-Smoke ($archiveInvoice.StatusCode -eq 200) "Invoice archive expected 200, got $($archiveInvoice.StatusCode)."
    $activeDocumentsAfterArchive = @(Invoke-RestMethod "$base/api/documents" | Where-Object { $_.id -eq $convertedInvoice.id })
    Assert-Smoke ($activeDocumentsAfterArchive.Count -eq 0) 'Archived invoice still appears in default invoice records list.'
    $archivedDocuments = @(Invoke-RestMethod "$base/api/documents?includeArchived=true" | Where-Object { $_.id -eq $convertedInvoice.id })
    Assert-Smoke ($archivedDocuments.Count -eq 1) 'Archived invoice does not appear with includeArchived=true.'
    $activeSalesAfterArchive = @(Invoke-RestMethod "$base/api/sales" | Where-Object { $_.invoiceNumber -eq $convertedInvoice.docNumber })
    $activeArAfterArchive = @(Invoke-RestMethod "$base/api/receivable-invoices" | Where-Object { $_.invoiceNumber -eq $convertedInvoice.docNumber })
    Assert-Smoke ($activeSalesAfterArchive.Count -eq 0) 'Archived invoice left an active Sale row.'
    Assert-Smoke ($activeArAfterArchive.Count -eq 0) 'Archived invoice left an active AR row.'

    $restoredInvoice = Invoke-RestMethod -Method POST -Uri "$base/api/documents/$($convertedInvoice.id)/restore"
    Assert-Smoke ($restoredInvoice.isArchived -eq $false) 'Restore invoice did not clear IsArchived.'
    $activeDocumentsAfterRestore = @(Invoke-RestMethod "$base/api/documents" | Where-Object { $_.id -eq $convertedInvoice.id })
    Assert-Smoke ($activeDocumentsAfterRestore.Count -eq 1) 'Restored invoice did not return to default invoice records list.'
    $activeSalesAfterRestore = @(Invoke-RestMethod "$base/api/sales" | Where-Object { $_.invoiceNumber -eq $convertedInvoice.docNumber })
    $activeArAfterRestore = @(Invoke-RestMethod "$base/api/receivable-invoices" | Where-Object { $_.invoiceNumber -eq $convertedInvoice.docNumber })
    Assert-Smoke ($activeSalesAfterRestore.Count -eq 1) 'Restored paid invoice did not restore the active Sale row.'
    Assert-Smoke ($activeArAfterRestore.Count -eq 1) 'Restored paid invoice did not restore the active AR row.'

    $voidPayload = New-SmokeDocumentPayload $convertedInvoice.docNumber 'INVOICE' 'Void' 'Round4 Workflow Customer Updated' 'Round4 Workflow Estimate Updated' 123 999 10
    $voidInvoice = Invoke-SmokeJson 'PUT' "/api/documents/$($convertedInvoice.id)" $voidPayload
    Assert-Smoke ($voidInvoice.status -eq 'Void') 'Void invoice update did not persist Void status.'
    Assert-Smoke ([math]::Abs([decimal]$voidInvoice.amountPaid) -lt 0.01) 'Void invoice did not force amount paid to zero.'
    $activeSalesAfterVoid = @(Invoke-RestMethod "$base/api/sales" | Where-Object { $_.invoiceNumber -eq $convertedInvoice.docNumber })
    $activeArAfterVoid = @(Invoke-RestMethod "$base/api/receivable-invoices" | Where-Object { $_.invoiceNumber -eq $convertedInvoice.docNumber })
    Assert-Smoke ($activeSalesAfterVoid.Count -eq 0) 'Void invoice left an active Sale row.'
    Assert-Smoke ($activeArAfterVoid.Count -eq 0) 'Void invoice left an active AR row.'

    $secondConvertResponse = Invoke-WebRequest `
        -Method POST `
        -Uri "$base/api/documents/$($round4Estimate.id)/convert-to-invoice" `
        -SkipHttpErrorCheck
    if ($secondConvertResponse.StatusCode -eq 200) {
        $secondConvertedInvoice = $secondConvertResponse.Content | ConvertFrom-Json
        Assert-Smoke ($secondConvertedInvoice.docType -eq 'INVOICE') 'Second estimate conversion did not return an invoice.'
        Assert-Smoke ($secondConvertedInvoice.id -ne $round4Estimate.id) 'Second estimate conversion reused the source estimate id.'
        Assert-Smoke ($secondConvertedInvoice.id -ne $convertedInvoice.id) 'Second estimate conversion reused the first converted invoice id.'
        Assert-Smoke ($secondConvertedInvoice.docNumber -ne $convertedInvoice.docNumber) 'Second estimate conversion reused the first converted invoice number.'
        $sourceAfterSecondConvert = Invoke-RestMethod "$base/api/documents/$($round4Estimate.id)"
        Assert-Smoke ($sourceAfterSecondConvert.docType -eq 'ESTIMATE') 'Second estimate conversion changed the source estimate type.'
        Assert-Smoke ($sourceAfterSecondConvert.docNumber -eq $round4Estimate.docNumber) 'Second estimate conversion changed the source estimate number.'
        $secondConvertSales = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $secondConvertedInvoice.docNumber })
        Assert-Smoke ($secondConvertSales.Count -eq 0) 'Second draft conversion unexpectedly created a Sale row.'
    }
    else {
        Assert-Smoke (($secondConvertResponse.StatusCode -eq 400 -or $secondConvertResponse.StatusCode -eq 409) -and $secondConvertResponse.Content -like '*convert*') "Second estimate conversion returned unexpected status $($secondConvertResponse.StatusCode)."
    }

    $renumberOriginal = "INV-$year-0900"
    $renumberNew = "INV-$year-0901"
    $renumberPayload = New-SmokeDocumentPayload $renumberOriginal 'INVOICE' 'Paid' 'Round5 Renumber Customer' 'Round5 Renumber Invoice' 77 0 10
    $renumberDoc = Invoke-SmokeJson 'POST' '/api/documents' $renumberPayload
    Assert-Smoke ($renumberDoc.docNumber -eq $renumberOriginal) 'Renumber smoke invoice did not save with the original number.'
    $renumberSalesBefore = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $renumberOriginal })
    $renumberArBefore = @(Invoke-RestMethod "$base/api/receivable-invoices?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $renumberOriginal })
    Assert-Smoke ($renumberSalesBefore.Count -eq 1) 'Renumber smoke setup did not create exactly one Sale row.'
    Assert-Smoke ($renumberArBefore.Count -eq 1) 'Renumber smoke setup did not create exactly one AR row.'

    $renumberUpdatePayload = New-SmokeDocumentPayload $renumberNew 'INVOICE' 'Paid' 'Round5 Renumber Customer' 'Round5 Renumber Invoice Updated' 88 0 10
    $renumberUpdated = Invoke-SmokeJson 'PUT' "/api/documents/$($renumberDoc.id)" $renumberUpdatePayload
    Assert-Smoke ($renumberUpdated.id -eq $renumberDoc.id) 'Renumber invoice update changed the document id.'
    Assert-Smoke ($renumberUpdated.docNumber -eq $renumberNew) 'Renumber invoice update did not persist the new invoice number.'
    $oldSalesAfterRenumber = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $renumberOriginal })
    $oldArAfterRenumber = @(Invoke-RestMethod "$base/api/receivable-invoices?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $renumberOriginal })
    $newSalesAfterRenumber = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $renumberNew })
    $newArAfterRenumber = @(Invoke-RestMethod "$base/api/receivable-invoices?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $renumberNew })
    Assert-Smoke ($oldSalesAfterRenumber.Count -eq 0) 'Renumbered invoice left an orphan Sale row on the old invoice number.'
    Assert-Smoke ($oldArAfterRenumber.Count -eq 0) 'Renumbered invoice left an orphan AR row on the old invoice number.'
    Assert-Smoke ($newSalesAfterRenumber.Count -eq 1) 'Renumbered invoice did not keep exactly one Sale row on the new invoice number.'
    Assert-Smoke ($newArAfterRenumber.Count -eq 1) 'Renumbered invoice did not keep exactly one AR row on the new invoice number.'

    $saveAsNewOriginalNumber = "EST-$year-0920"
    $saveAsNewCopyNumber = "EST-$year-0921"
    $saveAsNewOriginal = Invoke-SmokeJson 'POST' '/api/documents' (New-SmokeDocumentPayload $saveAsNewOriginalNumber 'ESTIMATE' 'Sent' 'Round9 Save New Customer' 'Round9 Save New Original' 31 0 0)
    $saveAsNewPayload = New-SmokeDocumentPayload $saveAsNewCopyNumber 'ESTIMATE' 'Draft' 'Round9 Save New Customer Copy' 'Round9 Save New Copy' 41 0 0
    $saveAsNewCopy = Invoke-SmokeJson 'POST' '/api/documents' $saveAsNewPayload
    $saveAsNewOriginalReloaded = Invoke-RestMethod "$base/api/documents/$($saveAsNewOriginal.id)"
    Assert-Smoke ($saveAsNewCopy.id -ne $saveAsNewOriginal.id) 'Save-as-new workflow reused the original estimate id.'
    Assert-Smoke ($saveAsNewCopy.docNumber -eq $saveAsNewCopyNumber) 'Save-as-new workflow did not use the new estimate number.'
    Assert-Smoke ($saveAsNewOriginalReloaded.docNumber -eq $saveAsNewOriginalNumber) 'Save-as-new workflow changed the original estimate number.'
    Assert-Smoke ($saveAsNewOriginalReloaded.projectName -eq 'Round9 Save New Original') 'Save-as-new workflow changed the original estimate project.'
    Assert-Smoke ($saveAsNewOriginalReloaded.status -eq 'Sent') 'Save-as-new workflow changed the original estimate status.'

    $switchRecordA = Invoke-SmokeJson 'POST' '/api/documents' (New-SmokeDocumentPayload "EST-$year-0922" 'ESTIMATE' 'Draft' 'Round9 Switch A Customer' 'Round9 Switch A Project' 22 0 0)
    $switchRecordB = Invoke-SmokeJson 'POST' '/api/documents' (New-SmokeDocumentPayload "INV-$year-0923" 'INVOICE' 'Sent' 'Round9 Switch B Customer' 'Round9 Switch B Project' 33 0 10)
    $loadedA = Invoke-RestMethod "$base/api/documents/$($switchRecordA.id)"
    $loadedB = Invoke-RestMethod "$base/api/documents/$($switchRecordB.id)"
    $switchBUpdatePayload = New-SmokeDocumentPayload $loadedB.docNumber 'INVOICE' 'Partial' 'Round9 Switch B Customer Updated' 'Round9 Switch B Project Updated' 44 10 10
    $switchBUpdated = Invoke-SmokeJson 'PUT' "/api/documents/$($loadedB.id)" $switchBUpdatePayload
    $switchAAfterBSave = Invoke-RestMethod "$base/api/documents/$($loadedA.id)"
    Assert-Smoke ($switchBUpdated.id -eq $loadedB.id) 'Switch A/B save changed the target record id.'
    Assert-Smoke ($switchBUpdated.docNumber -eq $loadedB.docNumber) 'Switch A/B save changed the target invoice number.'
    Assert-Smoke ($switchBUpdated.projectName -eq 'Round9 Switch B Project Updated') 'Switch A/B save did not update record B.'
    Assert-Smoke ($switchAAfterBSave.docNumber -eq $loadedA.docNumber) 'Switch A/B save changed record A number.'
    Assert-Smoke ($switchAAfterBSave.docType -eq 'ESTIMATE') 'Switch A/B save changed record A type.'
    Assert-Smoke ($switchAAfterBSave.projectName -eq 'Round9 Switch A Project') 'Switch A/B save wrote record B data into record A.'

    $typeChangeEstimateNumber = "EST-$year-0924"
    $typeChangeInvoiceNumber = "INV-$year-0924"
    $typeChangeEstimate = Invoke-SmokeJson 'POST' '/api/documents' (New-SmokeDocumentPayload $typeChangeEstimateNumber 'ESTIMATE' 'Accepted' 'Round9 Type Change Customer' 'Round9 Type Change Estimate' 52 0 0)
    $typeChangeInvoicePayload = New-SmokeDocumentPayload $typeChangeInvoiceNumber 'INVOICE' 'Paid' 'Round9 Type Change Customer' 'Round9 Type Change Invoice' 52 0 10
    $typeChangeInvoicePayload.projectNotes = "Created as a new INVOICE from ESTIMATE $typeChangeEstimateNumber. The original saved record was not overwritten."
    $typeChangeInvoice = Invoke-SmokeJson 'POST' '/api/documents' $typeChangeInvoicePayload
    $typeChangeEstimateReloaded = Invoke-RestMethod "$base/api/documents/$($typeChangeEstimate.id)"
    Assert-Smoke ($typeChangeInvoice.id -ne $typeChangeEstimate.id) 'Estimate-to-paid-invoice workflow reused the original estimate id.'
    Assert-Smoke ($typeChangeEstimateReloaded.docType -eq 'ESTIMATE') 'Estimate-to-paid-invoice workflow changed the original estimate type.'
    Assert-Smoke ($typeChangeEstimateReloaded.docNumber -eq $typeChangeEstimateNumber) 'Estimate-to-paid-invoice workflow changed the original estimate number.'
    Assert-Smoke ($typeChangeInvoice.docType -eq 'INVOICE') 'Estimate-to-paid-invoice workflow did not create an invoice.'
    Assert-Smoke ($typeChangeInvoice.docNumber -eq $typeChangeInvoiceNumber) 'Estimate-to-paid-invoice workflow did not use a fresh invoice number.'
    Assert-Smoke ($typeChangeInvoice.status -eq 'Paid') 'Estimate-to-paid-invoice workflow did not save the new invoice as Paid.'
    $typeChangeEstimateSales = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $typeChangeEstimateNumber })
    $typeChangeEstimateAr = @(Invoke-RestMethod "$base/api/receivable-invoices?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $typeChangeEstimateNumber })
    $typeChangeInvoiceSales = @(Invoke-RestMethod "$base/api/sales?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $typeChangeInvoiceNumber })
    $typeChangeInvoiceAr = @(Invoke-RestMethod "$base/api/receivable-invoices?includeArchived=true" | Where-Object { $_.invoiceNumber -eq $typeChangeInvoiceNumber })
    Assert-Smoke ($typeChangeEstimateSales.Count -eq 0) 'Estimate-to-paid-invoice workflow created a Sale row for the original estimate.'
    Assert-Smoke ($typeChangeEstimateAr.Count -eq 0) 'Estimate-to-paid-invoice workflow created an AR row for the original estimate.'
    Assert-Smoke ($typeChangeInvoiceSales.Count -eq 1) 'Estimate-to-paid-invoice workflow did not create exactly one Sale row for the new invoice.'
    Assert-Smoke ($typeChangeInvoiceAr.Count -eq 1) 'Estimate-to-paid-invoice workflow did not create exactly one AR row for the new invoice.'

    $fieldDocNumber = "EST-$year-0930"
    $fieldPayload = New-SmokeDocumentPayload $fieldDocNumber 'ESTIMATE' 'Sent' 'Round10 Customer Fields' 'Round10 Field Persistence' 10 0 0
    $fieldPayload.customerPhone = '(555) 123-4567'
    $fieldPayload.customerAddress = "123 Round 10 Way`nSuite B"
    $fieldPayload.customerEmail = 'round10@example.test'
    $fieldPayload.preparedFor = 'Round10 Prepared For'
    $fieldPayload.lineItems = @(
        @{
            sortOrder = 20
            description = "Second multiline line`nwith preserved description"
            details = "Second details`nwith preserved details"
            quantity = 0
            rate = 100
            amount = 0
        },
        @{
            sortOrder = 10
            description = "First multiline line`nwith preserved description"
            details = "First details`nwith preserved details"
            quantity = 2
            rate = 12.50
            amount = 25
        }
    )
    $fieldDoc = Invoke-SmokeJson 'POST' '/api/documents' $fieldPayload
    $fieldReloaded = Invoke-RestMethod "$base/api/documents/$($fieldDoc.id)"
    $fieldLines = @($fieldReloaded.lineItems)
    Assert-Smoke ($fieldReloaded.customerName -eq 'Round10 Customer Fields') 'Customer name did not save/reload.'
    Assert-Smoke ($fieldReloaded.preparedFor -eq 'Round10 Prepared For') 'Prepared-for did not save/reload.'
    Assert-Smoke ($fieldReloaded.customerPhone -eq '(555) 123-4567') 'Customer phone did not save/reload in normalized format.'
    Assert-Smoke ($fieldReloaded.customerEmail -eq 'round10@example.test') 'Customer email did not save/reload.'
    Assert-Smoke ($fieldReloaded.customerAddress -eq "123 Round 10 Way`nSuite B") 'Customer address did not preserve multiline content.'
    Assert-Smoke ($fieldLines.Count -eq 2) 'Expected two persisted line items for field/line smoke document.'
    Assert-Smoke ($fieldLines[0].sortOrder -eq 10 -and $fieldLines[0].description -eq "First multiline line`nwith preserved description") 'Line item sort order or first multiline description did not persist.'
    Assert-Smoke ($fieldLines[0].details -eq "First details`nwith preserved details") 'First multiline line item details did not persist.'
    Assert-Smoke ([math]::Abs([decimal]$fieldLines[0].amount - 25) -lt 0.01) 'First line item amount did not normalize from quantity x rate.'
    Assert-Smoke ($fieldLines[1].sortOrder -eq 20 -and [math]::Abs([decimal]$fieldLines[1].quantity) -lt 0.01) 'Zero-quantity line item did not keep its intended order or zero quantity.'
    Assert-Smoke ([math]::Abs([decimal]$fieldLines[1].amount) -lt 0.01) 'Zero-quantity line item should have a zero amount.'
    Assert-Smoke ([math]::Abs([decimal]$fieldReloaded.subtotal - 25) -lt 0.01) 'Document subtotal did not ignore the zero-quantity line item amount.'

    $crudIds = [ordered]@{}
    $crudIds.parties = Test-CrudRoundTrip 'parties' ([ordered]@{
        name = '=ROUND3-EXPORT-SAFE'
        partyType = 'Customer'
        email = 'round3@example.test'
        phone = '5551234567'
        address1 = '3 Smoke Test Way'
        city = 'Newark'
        state = 'NJ'
        postalCode = '07102'
        notes = "Round 3 party smoke, quote `"ok`"`nnew line"
    }) 'name' '=ROUND3-EXPORT-SAFE' 'name' '=ROUND3-FORMULA-SAFE' "'=ROUND3-FORMULA-SAFE"

    $partyExport = Invoke-WebRequest -Uri "$base/api/export/parties?includeArchived=true" -UseBasicParsing
    Assert-Smoke ($partyExport.Content -like '*''=ROUND3-FORMULA-SAFE*') 'Party export did not protect formula-like text.'
    Assert-Smoke ($partyExport.Content -like '*"Round 3 party smoke, quote ""ok""*') 'Party export did not escape comma/quote text.'
    Assert-Smoke ($partyExport.Content -like '*new line*') 'Party export did not preserve newline text.'

    $crudIds.products = Test-CrudRoundTrip 'products' ([ordered]@{
        name = 'Round3 Product'
        sku = 'R3-SKU'
        category = '3D Printed Product'
        material = 'PLA'
        color = 'Blue'
        grams = 42
        materialCostPerGram = 0.04
        printHours = 3.5
        machineRatePerHour = 5
        packagingCost = 1.25
        designMinutes = 30
        targetPrice = 45
        notes = 'Round 3 product smoke'
    }) 'name' 'Round3 Product' 'name' 'Round3 Product Updated' 'Round3 Product Updated'

    $round48NegativeProduct = Invoke-SmokeJson 'POST' '/api/products' ([ordered]@{
        name = 'Round48 Negative Costing Product'
        sku = 'R48-NEG'
        category = '3D Printed Product'
        material = 'PETG'
        color = 'Black'
        grams = -42
        materialCostPerGram = -0.04
        printHours = -3.5
        machineRatePerHour = -5
        packagingCost = -1.25
        designMinutes = -30
        targetPrice = -45
        needsReview = $false
        notes = 'Round 48 negative costing create clamp smoke'
    })
    Assert-SmokeClose $round48NegativeProduct.grams 0 0.01 'Round 48 negative product grams did not clamp to zero on create.'
    Assert-SmokeClose $round48NegativeProduct.materialCostPerGram 0 0.01 'Round 48 negative product material cost did not clamp to zero on create.'
    Assert-SmokeClose $round48NegativeProduct.printHours 0 0.01 'Round 48 negative product print hours did not clamp to zero on create.'
    Assert-SmokeClose $round48NegativeProduct.machineRatePerHour 0 0.01 'Round 48 negative product machine rate did not clamp to zero on create.'
    Assert-SmokeClose $round48NegativeProduct.packagingCost 0 0.01 'Round 48 negative product packaging cost did not clamp to zero on create.'
    Assert-SmokeClose $round48NegativeProduct.designMinutes 0 0.01 'Round 48 negative product design minutes did not clamp to zero on create.'
    Assert-SmokeClose $round48NegativeProduct.targetPrice 0 0.01 'Round 48 negative product target price did not clamp to zero on create.'
    Assert-SmokeClose $round48NegativeProduct.estimatedCost 0 0.01 'Round 48 negative product estimated cost should not be negative on create.'

    $round48UpdatedProduct = Invoke-SmokeJson 'PUT' "/api/products/$($round48NegativeProduct.id)" ([ordered]@{
        id = $round48NegativeProduct.id
        name = 'Round48 Negative Costing Product Updated'
        sku = 'R48-NEG'
        category = '3D Printed Product'
        material = 'PETG'
        color = 'Black'
        grams = -84
        materialCostPerGram = -0.08
        printHours = -7
        machineRatePerHour = -10
        packagingCost = -2.50
        designMinutes = -60
        targetPrice = -90
        needsReview = $true
        notes = 'Round 48 negative costing update clamp smoke'
    })
    Assert-SmokeClose $round48UpdatedProduct.grams 0 0.01 'Round 48 negative product grams did not clamp to zero on update.'
    Assert-SmokeClose $round48UpdatedProduct.materialCostPerGram 0 0.01 'Round 48 negative product material cost did not clamp to zero on update.'
    Assert-SmokeClose $round48UpdatedProduct.printHours 0 0.01 'Round 48 negative product print hours did not clamp to zero on update.'
    Assert-SmokeClose $round48UpdatedProduct.machineRatePerHour 0 0.01 'Round 48 negative product machine rate did not clamp to zero on update.'
    Assert-SmokeClose $round48UpdatedProduct.packagingCost 0 0.01 'Round 48 negative product packaging cost did not clamp to zero on update.'
    Assert-SmokeClose $round48UpdatedProduct.designMinutes 0 0.01 'Round 48 negative product design minutes did not clamp to zero on update.'
    Assert-SmokeClose $round48UpdatedProduct.targetPrice 0 0.01 'Round 48 negative product target price did not clamp to zero on update.'
    Assert-SmokeClose $round48UpdatedProduct.estimatedCost 0 0.01 'Round 48 negative product estimated cost should not be negative on update.'
    Assert-Smoke ($round48UpdatedProduct.needsReview -eq $true) 'Round 48 negative product update did not preserve needs-review flag.'

    $crudIds.sales = Test-CrudRoundTrip 'sales' ([ordered]@{
        saleDate = '2026-06-19'
        platform = 'Direct'
        paymentMethod = 'Cash'
        salesTaxHandling = 'Seller collected and remitted'
        orderNumber = 'R3-ORDER-001'
        invoiceNumber = 'INV-2026-R3SALE'
        customerName = 'Round3 Sale Customer'
        productName = 'Round3 Product Updated'
        sku = 'R3-SKU'
        variation = 'Large'
        color = 'Blue'
        quantity = 2
        itemSales = 80
        shippingCharged = 5
        salesTaxCollected = 5.95
        customerPaid = 90.95
        platformFees = 3.25
        shippingLabelCost = 4.10
        refunds = 0
        estimatedCogs = 14.50
        status = 'Paid'
        sourceProof = 'round3-sales-proof.csv'
        trackingNumber = '9400R3TRACK'
        shipByDate = '2026-06-26'
        includeInDashboard = $true
        notes = 'Round 3 sale smoke'
    }) 'customerName' 'Round3 Sale Customer' 'customerName' 'Round3 Sale Updated' 'Round3 Sale Updated'

    $crudIds.expenses = Test-CrudRoundTrip 'expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round3 Vendor'
        category = 'Filament'
        taxCategory = 'COGS/Materials'
        description = 'Round3 Expense'
        paymentMethod = 'Debit Card'
        paymentAccount = 'Cash'
        amount = 25
        salesTax = 1.66
        total = 26.66
        receiptProof = 'round3 receipt.pdf'
        taxBucket = 'COGS/Materials'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $true
        taxDeductible = $true
        notes = 'Round 3 expense smoke'
    }) 'description' 'Round3 Expense' 'description' 'Round3 Expense Updated' 'Round3 Expense Updated'

    $crudIds.receivables = Test-CrudRoundTrip 'receivable-invoices' ([ordered]@{
        invoiceNumber = 'INV-2026-R3AR'
        invoiceDate = '2026-06-19'
        dueDate = '2026-07-03'
        customerName = 'Round3 AR Customer'
        projectName = 'Round3 AR Project'
        status = 'Partial'
        subtotal = 120
        discount = 10
        rushFee = 5
        taxRatePercent = 6.625
        salesTax = 7.62
        invoiceTotal = 122.62
        amountPaid = 50
        paymentMethod = 'PayPal'
        sourceProof = 'Round3 AR proof'
        includeInCashReports = $true
        notes = 'Round 3 AR smoke'
    }) 'invoiceNumber' 'INV-2026-R3AR' 'projectName' 'Round3 AR Project Updated' 'Round3 AR Project Updated'

    $archiveDuplicate = Invoke-WebRequest -Method DELETE -Uri "$base/api/documents/$($invoiceDuplicate.id)" -SkipHttpErrorCheck
    Assert-Smoke ($archiveDuplicate.StatusCode -eq 200) "Archiving duplicate invoice for records view returned $($archiveDuplicate.StatusCode)."

    $recordsAll = @(Get-SmokeJsonArray '/api/documents?includeArchived=true')
    Assert-Smoke ((@($recordsAll | Where-Object { $_.sourceKind -eq 'document' -and $_.sourceId -eq $acceptedEstimateRenumbered.id -and $_.docType -eq 'ESTIMATE' -and $_.docNumber -eq $acceptedEstimateRenumberedNumber }).Count) -eq 1) 'Records list did not expose the saved estimate as a builder document row.'
    Assert-Smoke ((@($recordsAll | Where-Object { $_.sourceKind -eq 'document' -and $_.sourceId -eq $renumberUpdated.id -and $_.docType -eq 'INVOICE' -and $_.docNumber -eq $renumberNew }).Count) -eq 1) 'Records list did not expose the saved invoice as a builder document row.'
    Assert-Smoke ((@($recordsAll | Where-Object { $_.sourceKind -eq 'receivable' -and $_.sourceId -eq $crudIds.receivables -and $_.sourceLabel -eq 'AR only' -and $_.docNumber -eq 'INV-2026-R3AR' }).Count) -eq 1) 'Records list did not expose the AR-only row distinctly.'

    $activeRecordsAfterArchive = @(Get-SmokeJsonArray '/api/documents' | Where-Object { $_.sourceKind -eq 'document' -and $_.id -eq $invoiceDuplicate.id })
    $archivedRecordsAfterArchive = @(Get-SmokeJsonArray '/api/documents?includeArchived=true' | Where-Object { $_.sourceKind -eq 'document' -and $_.id -eq $invoiceDuplicate.id -and $_.isArchived })
    Assert-Smoke ($activeRecordsAfterArchive.Count -eq 0) 'Archived duplicate invoice still appeared in the normal Records list.'
    Assert-Smoke ($archivedRecordsAfterArchive.Count -eq 1) 'Show Archived did not return the archived duplicate invoice.'

    $numberSearch = @(Get-SmokeJsonArray "/api/documents?q=$([uri]::EscapeDataString($renumberNew))")
    $customerSearch = @(Get-SmokeJsonArray "/api/documents?q=$([uri]::EscapeDataString('Round5 Renumber Customer'))")
    $projectSearch = @(Get-SmokeJsonArray "/api/documents?q=$([uri]::EscapeDataString('Round3 AR Project Updated'))")
    Assert-Smoke ((@($numberSearch | Where-Object { $_.docNumber -eq $renumberNew }).Count) -eq 1) 'Records search by document number did not find the renumbered invoice.'
    Assert-Smoke ((@($customerSearch | Where-Object { $_.customerName -eq 'Round5 Renumber Customer' }).Count) -ge 1) 'Records search by customer did not find the saved invoice.'
    Assert-Smoke ((@($projectSearch | Where-Object { $_.sourceKind -eq 'receivable' -and $_.projectName -eq 'Round3 AR Project Updated' }).Count) -eq 1) 'Records search by AR-only project did not find the receivable row.'

    $estimateRecords = @(Get-SmokeJsonArray '/api/documents?type=ESTIMATE&includeArchived=true')
    $invoiceRecords = @(Get-SmokeJsonArray '/api/documents?type=INVOICE')
    Assert-Smoke ($estimateRecords.Count -gt 0 -and @($estimateRecords | Where-Object { $_.docType -ne 'ESTIMATE' }).Count -eq 0) 'Records type filter for estimates returned non-estimate rows.'
    Assert-Smoke ($invoiceRecords.Count -gt 0 -and @($invoiceRecords | Where-Object { $_.docType -ne 'INVOICE' }).Count -eq 0) 'Records type filter for invoices returned non-invoice rows.'

    $paidInvoiceRecords = @(Get-SmokeJsonArray '/api/documents?type=INVOICE&status=Paid')
    $acceptedEstimateRecords = @(Get-SmokeJsonArray '/api/documents?type=ESTIMATE&status=Accepted')
    Assert-Smoke ((@($paidInvoiceRecords | Where-Object { $_.docNumber -eq $renumberNew -and $_.status -eq 'Paid' }).Count) -eq 1) 'Records status filter did not find the paid invoice.'
    Assert-Smoke ((@($paidInvoiceRecords | Where-Object { $_.status -ne 'Paid' }).Count) -eq 0) 'Records paid status filter returned a non-paid row.'
    Assert-Smoke ((@($acceptedEstimateRecords | Where-Object { $_.docNumber -eq $acceptedEstimateRenumberedNumber -and $_.status -eq 'Accepted' }).Count) -eq 1) 'Records status filter did not find the accepted estimate.'
    Assert-Smoke ((@($acceptedEstimateRecords | Where-Object { $_.status -ne 'Accepted' }).Count) -eq 0) 'Records accepted status filter returned a non-accepted row.'

    $paidDocumentAliasRecords = @(Get-SmokeJsonArray '/api/invoice-documents?type=INVOICE&status=Paid')
    $paidDocumentNumbers = (@($paidInvoiceRecords | ForEach-Object { $_.docNumber }) | Sort-Object) -join '|'
    $paidAliasNumbers = (@($paidDocumentAliasRecords | ForEach-Object { $_.docNumber }) | Sort-Object) -join '|'
    Assert-Smoke ($paidDocumentNumbers -eq $paidAliasNumbers) 'Records API aliases returned different paid invoice filters.'

    $crudIds.bills = Test-CrudRoundTrip 'bills' ([ordered]@{
        vendorName = 'Round3 Bill Vendor'
        billNumber = 'R3-BILL-001'
        billDate = '2026-06-19'
        dueDate = '2026-07-19'
        category = 'Utilities'
        description = 'Round3 Bill'
        amount = 40
        salesTax = 0
        total = 40
        amountPaid = 10
        status = 'Partial'
        paymentMethod = 'ACH / Bank Transfer'
        paymentAccount = 'Checking'
        sourceProof = 'round3 bill proof'
        taxDeductible = $true
        notes = 'Round 3 bill smoke'
    }) 'description' 'Round3 Bill' 'description' 'Round3 Bill Updated' 'Round3 Bill Updated'

    $crudIds.jobs = Test-CrudRoundTrip 'customer-jobs' ([ordered]@{
        jobDate = '2026-06-19'
        customerName = 'Round3 Job Customer'
        platform = 'Direct'
        jobNumber = 'JOB-R3-001'
        relatedOrderNumber = 'R3-ORDER-001'
        relatedInvoiceNumber = 'INV-2026-R3SALE'
        jobName = 'Round3 Job'
        jobType = 'Print'
        status = 'In Progress'
        productName = 'Round3 Product Updated'
        material = 'PLA'
        color = 'Blue'
        description = 'Round 3 job smoke'
        quoteAmount = 120
        invoiceAmount = 122.62
        amountPaid = 50
        paymentMethod = 'Zelle'
        dueDate = '2026-07-03'
        shipByDate = '2026-06-26'
        sourceProof = 'round3 job proof'
        notes = 'Round 3 job smoke'
    }) 'jobName' 'Round3 Job' 'jobName' 'Round3 Job Updated' 'Round3 Job Updated'

    $crudIds.communications = Test-CrudRoundTrip 'customer-communications' ([ordered]@{
        occurredAt = '2026-06-19T10:00:00'
        customerName = 'Round3 Job Customer'
        direction = 'Incoming'
        channel = 'Email'
        subject = 'Round3 Communication'
        summary = 'Customer approved the Round 3 proof.'
        relatedJobNumber = 'JOB-R3-001'
        relatedOrderNumber = 'R3-ORDER-001'
        relatedInvoiceNumber = 'INV-2026-R3SALE'
        followUpDate = '2026-06-25'
        followUpStatus = 'Open'
        sourceProof = 'round3 communication proof'
        notes = 'Round 3 communication smoke'
    }) 'subject' 'Round3 Communication' 'subject' 'Round3 Communication Updated' 'Round3 Communication Updated'

    $round54LongSummaryWord = 'x' * 120
    $round54IncomingCommunication = Invoke-SmokeJson 'POST' '/api/customer-communications' ([ordered]@{
        occurredAt = '2026-06-19T08:00:00'
        customerName = 'Round54 Communication Customer'
        direction = 'Incoming'
        channel = 'Email'
        subject = 'Round54 Incoming Approval'
        summary = "Customer approved the proof and pasted a long requirement token $round54LongSummaryWord that must wrap in the communication card."
        relatedJobNumber = 'JOB-R54-COMM'
        relatedOrderNumber = 'ORDER-R54-COMM'
        relatedInvoiceNumber = 'INV-R54-COMM'
        followUpDate = '2026-06-18'
        followUpStatus = 'Open'
        sourceProof = 'round54-incoming-email.eml'
        notes = 'Round 54 incoming communication smoke'
    })
    $round54OutgoingCommunication = Invoke-SmokeJson 'POST' '/api/customer-communications' ([ordered]@{
        occurredAt = '2026-06-19T09:00:00'
        customerName = 'Round54 Communication Customer'
        direction = 'Outgoing'
        channel = 'Text Message'
        subject = 'Round54 Outgoing Update'
        summary = 'Sent production and pickup timing update.'
        relatedJobNumber = 'JOB-R54-COMM'
        relatedOrderNumber = 'ORDER-R54-COMM'
        relatedInvoiceNumber = 'INV-R54-COMM'
        followUpDate = '2026-06-21'
        followUpStatus = 'Done'
        sourceProof = 'round54-outgoing-text.png'
        notes = 'Round 54 outgoing communication smoke'
    })
    $round54InternalCommunication = Invoke-SmokeJson 'POST' '/api/customer-communications' ([ordered]@{
        occurredAt = '2026-06-19T10:00:00'
        customerName = 'Round54 Communication Customer'
        direction = 'Internal'
        channel = 'Other'
        subject = 'Round54 Internal Production Note'
        summary = 'Private note: verify magnet orientation before final assembly.'
        relatedJobNumber = 'JOB-R54-COMM'
        relatedOrderNumber = 'ORDER-R54-COMM'
        relatedInvoiceNumber = 'INV-R54-COMM'
        followUpStatus = 'None'
        sourceProof = 'round54-internal-note'
        notes = 'Round 54 internal communication smoke'
    })
    $round54CommunicationRows = @(Get-SmokeJsonArray '/api/customer-communications' | Where-Object { $_.customerName -eq 'Round54 Communication Customer' })
    $round54CommunicationDirections = @($round54CommunicationRows | ForEach-Object { $_.direction } | Sort-Object)
    $round54IncomingCount = @($round54CommunicationDirections | Where-Object { $_ -eq 'Incoming' }).Count
    $round54OutgoingCount = @($round54CommunicationDirections | Where-Object { $_ -eq 'Outgoing' }).Count
    $round54InternalCount = @($round54CommunicationDirections | Where-Object { $_ -eq 'Internal' }).Count
    Assert-Smoke ($round54CommunicationRows.Count -ge 3) "Round 54 expected at least three communication rows. Got: $($round54CommunicationDirections -join '|')"
    Assert-Smoke ($round54IncomingCount -ge 1) "Round 54 missing Incoming communication. Got: $($round54CommunicationDirections -join '|')"
    Assert-Smoke ($round54OutgoingCount -ge 1) "Round 54 missing Outgoing communication. Got: $($round54CommunicationDirections -join '|')"
    Assert-Smoke ($round54InternalCount -ge 1) "Round 54 missing Internal communication. Got: $($round54CommunicationDirections -join '|')"
    Assert-Smoke (($round54CommunicationRows | Where-Object { $_.summary -like "*$round54LongSummaryWord*" }).Count -eq 1) 'Round 54 long communication summary did not persist for display.'
    $round54IncomingSaved = $round54CommunicationRows | Where-Object { $_.subject -eq 'Round54 Incoming Approval' } | Select-Object -First 1
    $round54OutgoingSaved = $round54CommunicationRows | Where-Object { $_.subject -eq 'Round54 Outgoing Update' } | Select-Object -First 1
    $round54InternalSaved = $round54CommunicationRows | Where-Object { $_.subject -eq 'Round54 Internal Production Note' } | Select-Object -First 1
    $round54IncomingFollowUpDate = ([datetime]$round54IncomingSaved.followUpDate).Date
    Assert-Smoke ($round54IncomingSaved.followUpStatus -eq 'Open' -and $round54IncomingFollowUpDate -eq ([datetime]'2026-06-18').Date) "Round 54 incoming communication follow-up date/status did not persist. Got status '$($round54IncomingSaved.followUpStatus)' date '$($round54IncomingSaved.followUpDate)'."
    Assert-Smoke ($round54OutgoingSaved.followUpStatus -eq 'Done' -and $round54InternalSaved.followUpStatus -eq 'None') 'Round 54 closed/non-follow-up communication statuses did not persist.'

    $crudIds.queue = Test-CrudRoundTrip 'printer-queue-items' ([ordered]@{
        queueDate = '2026-06-19'
        priority = 'High'
        status = 'Queued'
        printerName = 'Round3 Printer'
        customerName = 'Round3 Job Customer'
        jobName = 'Round3 Job Updated'
        relatedOrderNumber = 'R3-ORDER-001'
        relatedInvoiceNumber = 'INV-2026-R3SALE'
        productName = 'Round3 Product Updated'
        material = 'PLA'
        color = 'Blue'
        quantity = 2
        plateCount = 1
        estimatedHours = 3.5
        actualHours = 1.25
        progressPercent = 40
        scheduledStart = '2026-06-20T09:00:00'
        startedAt = '2026-06-20T09:15:00'
        estimatedFinish = '2026-06-20T13:00:00'
        failureCount = 1
        sourceProof = 'round3 queue proof'
        needsReview = $true
        notes = 'Round 3 queue smoke'
    }) 'jobName' 'Round3 Job Updated' 'status' 'Printing' 'Printing'

    $round57OverdueQueue = Invoke-SmokeJson 'POST' '/api/printer-queue-items' ([ordered]@{
        queueDate = '2026-06-01'
        priority = 'High'
        status = 'Queued'
        printerName = 'Round57 Printer'
        customerName = 'Round57 Queue Customer'
        jobName = 'Round57 Overdue Queue Job'
        productName = 'Round57 Overdue Part'
        material = 'PLA'
        color = 'Black'
        quantity = 1
        plateCount = 1
        scheduledStart = '2000-01-02T09:00:00'
        estimatedFinish = '2000-01-02T12:00:00'
        failureCount = 0
        needsReview = $false
        notes = 'Round 57 overdue queue smoke'
    })
    $round57FailedQueue = Invoke-SmokeJson 'POST' '/api/printer-queue-items' ([ordered]@{
        queueDate = '2026-06-01'
        priority = 'Normal'
        status = 'Ready'
        printerName = 'Round57 Printer'
        customerName = 'Round57 Queue Customer'
        jobName = 'Round57 Failed Queue Job'
        productName = 'Round57 Failed Part'
        material = 'PETG'
        color = 'Blue'
        quantity = 1
        plateCount = 1
        scheduledStart = '2099-01-02T09:00:00'
        estimatedFinish = '2099-01-02T12:00:00'
        failureCount = 2
        needsReview = $false
        notes = 'Round 57 failed queue smoke'
    })
    $round57CompletedFailedQueue = Invoke-SmokeJson 'POST' '/api/printer-queue-items' ([ordered]@{
        queueDate = '2026-06-01'
        priority = 'Normal'
        status = 'Completed'
        printerName = 'Round57 Printer'
        customerName = 'Round57 Queue Customer'
        jobName = 'Round57 Completed Failed Queue Job'
        productName = 'Round57 Completed Failed Part'
        material = 'PLA'
        color = 'Gray'
        quantity = 1
        plateCount = 1
        scheduledStart = '2000-01-02T09:00:00'
        estimatedFinish = '2000-01-02T12:00:00'
        failureCount = 3
        needsReview = $true
        notes = 'Round 57 completed failed queue smoke'
    })
    $round57CancelledOverdueQueue = Invoke-SmokeJson 'POST' '/api/printer-queue-items' ([ordered]@{
        queueDate = '2026-06-01'
        priority = 'Low'
        status = 'Cancelled'
        printerName = 'Round57 Printer'
        customerName = 'Round57 Queue Customer'
        jobName = 'Round57 Cancelled Queue Job'
        productName = 'Round57 Cancelled Part'
        material = 'PLA'
        color = 'White'
        quantity = 1
        plateCount = 1
        scheduledStart = '2000-01-02T09:00:00'
        estimatedFinish = '2000-01-02T12:00:00'
        failureCount = 4
        needsReview = $false
        notes = 'Round 57 cancelled queue smoke'
    })
    Assert-Smoke ($round57OverdueQueue.id -gt 0 -and $round57FailedQueue.id -gt 0 -and $round57CompletedFailedQueue.id -gt 0 -and $round57CancelledOverdueQueue.id -gt 0) 'Round 57 printer queue attention fixtures did not save.'

    $crudIds.assets = Test-CrudRoundTrip 'assets' ([ordered]@{
        name = 'Round3 Asset'
        purchaseDate = '2026-06-19'
        vendorName = 'Round3 Asset Vendor'
        category = 'Equipment'
        cost = 599
        paymentMethod = 'Credit Card'
        serialNumber = 'R3ASSET001'
        warrantyEndDate = '2027-06-19'
        businessUsePercent = 100
        inServiceDate = '2026-06-20'
        taxTreatment = 'Section 179'
        countedExpenseThisYear = $true
        notYetExpensed = 0
        sourceProof = 'round3 asset proof'
        notes = 'Round 3 asset smoke'
    }) 'name' 'Round3 Asset' 'name' 'Round3 Asset Updated' 'Round3 Asset Updated'

    $round50Asset = Invoke-SmokeJson 'POST' '/api/assets' ([ordered]@{
        name = 'Round50 Normalized Asset'
        purchaseDate = '2026-06-19'
        vendorName = 'Round50 Asset Vendor'
        category = 'Equipment'
        cost = -499
        paymentMethod = 'Debit Card'
        serialNumber = 'R50ASSET001'
        businessUsePercent = 150
        inServiceDate = '2026-06-19'
        taxTreatment = 'Section 179'
        countedExpenseThisYear = $true
        notYetExpensed = -25
        sourceProof = 'round50 asset normalization proof'
        notes = 'Round 50 asset create normalization smoke'
    })
    Assert-SmokeClose $round50Asset.cost 0 0.01 'Round 50 asset cost did not clamp to zero on create.'
    Assert-SmokeClose $round50Asset.businessUsePercent 100 0.01 'Round 50 asset business-use percent did not clamp to 100 on create.'
    Assert-SmokeClose $round50Asset.notYetExpensed 0 0.01 'Round 50 asset not-yet-expensed did not clamp to zero on create.'

    $round50AssetUpdate = Invoke-SmokeJson 'PUT' "/api/assets/$($round50Asset.id)" ([ordered]@{
        id = $round50Asset.id
        name = 'Round50 Normalized Asset Updated'
        purchaseDate = '2026-06-19'
        vendorName = 'Round50 Asset Vendor'
        category = 'Equipment'
        cost = -100
        paymentMethod = 'Debit Card'
        serialNumber = 'R50ASSET001'
        businessUsePercent = -10
        inServiceDate = '2026-06-19'
        taxTreatment = 'Section 179'
        countedExpenseThisYear = $true
        notYetExpensed = -35
        sourceProof = 'round50 asset normalization proof'
        notes = 'Round 50 asset update normalization smoke'
    })
    Assert-SmokeClose $round50AssetUpdate.cost 0 0.01 'Round 50 asset cost did not clamp to zero on update.'
    Assert-SmokeClose $round50AssetUpdate.businessUsePercent 0 0.01 'Round 50 asset business-use percent did not clamp to zero on update.'
    Assert-SmokeClose $round50AssetUpdate.notYetExpensed 0 0.01 'Round 50 asset not-yet-expensed did not clamp to zero on update.'

    $crudIds.rewards = Test-CrudRoundTrip 'makerworld-rewards' ([ordered]@{
        rewardDate = '2026-06-19'
        rewardType = 'Gift Card'
        pointsChange = 500
        giftCardAmount = 40
        codeLast4 = 'R3GC'
        status = 'Available'
        incomeStatus = 'Review'
        sourceProof = 'round3 reward proof'
        notes = 'Round 3 reward smoke'
    }) 'codeLast4' 'R3GC' 'status' 'Redeemed' 'Redeemed'

    $crudIds.accounts = Test-CrudRoundTrip 'business-accounts' ([ordered]@{
        name = 'Round3 Account'
        accountType = 'Checking'
        institution = 'Round3 Bank'
        last4 = '3003'
        openingBalance = 100
        currentBalance = 125.50
        isActive = $true
        notes = 'Round 3 account smoke'
    }) 'name' 'Round3 Account' 'name' 'Round3 Account Updated' 'Round3 Account Updated'

    $dashboardBeforeRound52 = Invoke-RestMethod "$base/api/dashboard"
    $taxSummaryBeforeRound52 = Invoke-RestMethod "$base/api/tax-summary?year=2026"
    $round52ActiveAccount = Invoke-SmokeJson 'POST' '/api/business-accounts' ([ordered]@{
        name = 'Round52 Active Balance Account'
        accountType = 'Checking'
        institution = 'Round52 Bank'
        last4 = '5252'
        openingBalance = 999999
        currentBalance = 1234567.89
        isActive = $true
        notes = 'Round 52 active account balance isolation smoke'
    })
    $round52InactiveAccount = Invoke-SmokeJson 'POST' '/api/business-accounts' ([ordered]@{
        name = 'Round52 Inactive Balance Account'
        accountType = 'Credit Card'
        institution = 'Round52 Closed Card'
        last4 = '5253'
        openingBalance = -888888
        currentBalance = -777777.77
        isActive = $false
        notes = 'Round 52 inactive account balance isolation smoke'
    })
    Assert-Smoke ($round52ActiveAccount.isActive -eq $true) 'Round 52 active account did not preserve active status.'
    Assert-Smoke ($round52InactiveAccount.isActive -eq $false) 'Round 52 inactive account did not preserve inactive status.'
    $dashboardAfterRound52 = Invoke-RestMethod "$base/api/dashboard"
    $taxSummaryAfterRound52 = Invoke-RestMethod "$base/api/tax-summary?year=2026"
    foreach ($field in @('grossReceipts', 'customerPaid', 'taxPrepIncome', 'taxPrepDeductions', 'estimatedNet', 'openReceivables', 'openPayables')) {
        Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardAfterRound52.kpis.$field) - (Get-SmokeDecimalOrZero $dashboardBeforeRound52.kpis.$field)) 0.00 0.01 "Round 52 account balances changed dashboard KPI $field."
    }
    foreach ($field in @('grossReceipts', 'customerPaidIncludingTax', 'operatingExpenseDeductions', 'cogsMaterialExpenseDeductions', 'expensedAssets', 'makerWorldIncome', 'workingNetProfit')) {
        Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummaryAfterRound52.$field) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound52.$field)) 0.00 0.01 "Round 52 account balances changed tax summary field $field."
    }

    $crudIds.auditDocs = Test-CrudRoundTrip 'audit-documents' ([ordered]@{
        documentDate = '2026-06-19'
        documentType = 'Receipt'
        relatedRecordType = 'Sale'
        relatedRecordNumber = 'R3-ORDER-001'
        fileName = 'round3-audit.pdf'
        filePathOrUrl = 'UploadedDocs/round3-audit.pdf'
        needsReview = $true
        notes = 'Round 3 audit document smoke'
    }) 'fileName' 'round3-audit.pdf' 'fileName' 'round3-audit-updated.pdf' 'round3-audit-updated.pdf'

    $round55CustomerName = 'Round55 Relationship Customer'
    $round55VendorName = 'Round55 Relationship Vendor'
    $round55CustomerParty = Invoke-SmokeJson 'POST' '/api/parties' ([ordered]@{
        name = $round55CustomerName
        partyType = 'Customer'
        email = 'round55.customer@example.test'
        phone = '5555550055'
        defaultPlatform = 'Direct'
        notes = 'Round 55 customer contact create'
    })
    $round55CustomerParty.name = $round55CustomerName
    $round55CustomerParty.notes = 'Round 55 customer contact update'
    $round55CustomerPartyUpdated = Invoke-SmokeJson 'PUT' "/api/parties/$($round55CustomerParty.id)" $round55CustomerParty
    $round55VendorParty = Invoke-SmokeJson 'POST' '/api/parties' ([ordered]@{
        name = $round55VendorName
        partyType = 'Vendor'
        email = 'round55.vendor@example.test'
        phone = '5555550056'
        defaultPlatform = 'Vendor'
        notes = 'Round 55 vendor contact create'
    })
    $round55VendorParty.name = $round55VendorName
    $round55VendorParty.notes = 'Round 55 vendor contact update'
    $round55VendorPartyUpdated = Invoke-SmokeJson 'PUT' "/api/parties/$($round55VendorParty.id)" $round55VendorParty
    Assert-Smoke ($round55CustomerPartyUpdated.partyType -eq 'Customer' -and $round55CustomerPartyUpdated.notes -eq 'Round 55 customer contact update') 'Round 55 Add Contact Details customer create/update did not preserve the correct Party.'
    Assert-Smoke ($round55VendorPartyUpdated.partyType -eq 'Vendor' -and $round55VendorPartyUpdated.notes -eq 'Round 55 vendor contact update') 'Round 55 Add Contact Details vendor create/update did not preserve the correct Party.'

    $round55Sale = Invoke-SmokeJson 'POST' '/api/sales' ([ordered]@{
        saleDate = '2026-06-19'
        platform = 'Direct'
        paymentMethod = 'Cash'
        salesTaxHandling = 'No tax collected / not taxable'
        orderNumber = 'R55-REL-SALE'
        invoiceNumber = 'INV-2026-R55REL-SALE'
        customerName = $round55CustomerName
        productName = 'Round55 Relationship Product'
        quantity = 1
        itemSales = 25
        shippingCharged = 0
        salesTaxCollected = 0
        customerPaid = 25
        platformFees = 0
        shippingLabelCost = 0
        refunds = 0
        estimatedCogs = 5
        status = 'Paid'
        sourceProof = 'round55 relationship sale proof'
        includeInDashboard = $true
        notes = 'Round 55 relationship customer sale'
    })
    $round55Ar = Invoke-SmokeJson 'POST' '/api/receivable-invoices' ([ordered]@{
        invoiceNumber = 'INV-2026-R55REL-AR'
        invoiceDate = '2026-06-19'
        dueDate = '2026-06-26'
        customerName = $round55CustomerName
        projectName = 'Round55 Relationship AR'
        status = 'Sent'
        subtotal = 40
        discount = 0
        rushFee = 0
        salesTax = 0
        invoiceTotal = 40
        amountPaid = 0
        paymentMethod = 'Unknown / Review'
        sourceProof = 'round55 relationship ar proof'
        includeInCashReports = $false
        notes = 'Round 55 relationship customer ar'
    })
    $round55DocNumber = 'EST-2026-9550'
    $round55Doc = Invoke-SmokeJson 'POST' '/api/documents' (New-SmokeDocumentPayload $round55DocNumber 'ESTIMATE' 'Sent' $round55CustomerName 'Round55 Relationship PDF' 55 0 0)
    $round55Job = Invoke-SmokeJson 'POST' '/api/customer-jobs' ([ordered]@{
        jobDate = '2026-06-19'
        customerName = $round55CustomerName
        platform = 'Direct'
        jobNumber = 'JOB-R55-REL'
        relatedOrderNumber = 'R55-REL-SALE'
        relatedInvoiceNumber = $round55DocNumber
        jobName = 'Round55 Relationship Job'
        jobType = 'Print'
        status = 'Open'
        productName = 'Round55 Relationship Product'
        material = 'PLA'
        color = 'Green'
        description = 'Round 55 relationship customer job'
        quoteAmount = 55
        invoiceAmount = 0
        amountPaid = 0
        paymentMethod = 'Unknown / Review'
        sourceProof = 'round55 relationship job proof'
        notes = 'Round 55 relationship customer job'
    })
    $round55Communication = Invoke-SmokeJson 'POST' '/api/customer-communications' ([ordered]@{
        occurredAt = '2026-06-19T11:00:00'
        customerName = $round55CustomerName
        direction = 'Incoming'
        channel = 'Email'
        subject = 'Round55 Relationship Message'
        summary = 'Customer asked about the relationship detail view.'
        relatedJobNumber = 'JOB-R55-REL'
        relatedOrderNumber = 'R55-REL-SALE'
        relatedInvoiceNumber = $round55DocNumber
        followUpStatus = 'None'
        sourceProof = 'round55 relationship message proof'
        notes = 'Round 55 relationship customer communication'
    })
    $round55CustomerProof = Invoke-SmokeJson 'POST' '/api/audit-documents' ([ordered]@{
        documentDate = '2026-06-19'
        documentType = 'Proof'
        relatedRecordType = 'Customer'
        relatedRecordNumber = $round55CustomerName
        fileName = 'round55-customer-proof.pdf'
        filePathOrUrl = 'UploadedDocs/round55-customer-proof.pdf'
        notes = "Proof for $round55CustomerName"
    })
    $round55Expense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = $round55VendorName
        category = 'Supplies'
        taxCategory = 'Other business expense'
        description = 'Round55 Relationship Expense'
        paymentMethod = 'Credit Card'
        paymentAccount = 'Operations Card'
        amount = 20
        salesTax = 0
        total = 20
        receiptProof = 'round55 relationship expense proof'
        taxBucket = 'Operating Expense'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $true
        taxDeductible = $true
        notes = 'Round 55 relationship vendor expense'
    })
    $round55Bill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        billDate = '2026-06-19'
        dueDate = '2026-06-26'
        vendorName = $round55VendorName
        billNumber = 'BILL-R55-REL'
        category = 'Supplies'
        taxCategory = 'Other business expense'
        description = 'Round55 Relationship Bill'
        status = 'Open'
        amount = 30
        salesTax = 0
        total = 30
        amountPaid = 0
        paymentMethod = 'Check'
        paymentAccount = 'Checking'
        sourceProof = 'round55 relationship bill proof'
        taxDeductible = $true
        notes = 'Round 55 relationship vendor bill'
    })
    $round55Asset = Invoke-SmokeJson 'POST' '/api/assets' ([ordered]@{
        name = 'Round55 Relationship Asset'
        purchaseDate = '2026-06-19'
        vendorName = $round55VendorName
        category = 'Equipment'
        cost = 88
        paymentMethod = 'Debit Card'
        serialNumber = 'R55REL'
        businessUsePercent = 100
        inServiceDate = '2026-06-19'
        taxTreatment = 'Section 179'
        countedExpenseThisYear = $true
        notYetExpensed = 0
        sourceProof = 'round55 relationship asset proof'
        notes = 'Round 55 relationship vendor asset'
    })
    $round55VendorProof = Invoke-SmokeJson 'POST' '/api/audit-documents' ([ordered]@{
        documentDate = '2026-06-19'
        documentType = 'Proof'
        relatedRecordType = 'Vendor'
        relatedRecordNumber = $round55VendorName
        fileName = 'round55-vendor-proof.pdf'
        filePathOrUrl = 'UploadedDocs/round55-vendor-proof.pdf'
        notes = "Proof for $round55VendorName"
    })
    Assert-Smoke ($round55Sale.id -gt 0 -and $round55Ar.id -gt 0 -and $round55Doc.id -gt 0 -and $round55Job.id -gt 0 -and $round55Communication.id -gt 0 -and $round55CustomerProof.id -gt 0) 'Round 55 customer relationship detail fixture did not create every linked customer row.'
    Assert-Smoke ($round55Expense.id -gt 0 -and $round55Bill.id -gt 0 -and $round55Asset.id -gt 0 -and $round55VendorProof.id -gt 0) 'Round 55 vendor relationship detail fixture did not create every linked vendor row.'

    $round56DuplicateCustomerA = Invoke-SmokeJson 'POST' '/api/parties' ([ordered]@{
        name = 'Round56 Duplicate Customer'
        partyType = 'Customer'
        email = 'round56.customer.a@example.test'
        defaultPlatform = 'Direct'
        notes = 'Round 56 duplicate customer A'
    })
    $round56DuplicateCustomerB = Invoke-SmokeJson 'POST' '/api/parties' ([ordered]@{
        name = ' round56 duplicate customer '
        partyType = 'Customer'
        email = 'round56.customer.b@example.test'
        defaultPlatform = 'Direct'
        notes = 'Round 56 duplicate customer B'
    })
    $round56DuplicateVendorA = Invoke-SmokeJson 'POST' '/api/parties' ([ordered]@{
        name = 'Round56 Duplicate Vendor'
        partyType = 'Vendor'
        email = 'round56.vendor.a@example.test'
        defaultPlatform = 'Vendor'
        notes = 'Round 56 duplicate vendor A'
    })
    $round56DuplicateVendorB = Invoke-SmokeJson 'POST' '/api/parties' ([ordered]@{
        name = 'ROUND56 DUPLICATE VENDOR'
        partyType = 'Vendor'
        email = 'round56.vendor.b@example.test'
        defaultPlatform = 'Vendor'
        notes = 'Round 56 duplicate vendor B'
    })
    $round56Parties = @(Get-SmokeJsonArray '/api/parties' | Where-Object { $_.notes -like 'Round 56 duplicate*' })
    Assert-Smoke ($round56DuplicateCustomerA.id -gt 0 -and $round56DuplicateCustomerB.id -gt 0 -and $round56DuplicateVendorA.id -gt 0 -and $round56DuplicateVendorB.id -gt 0) 'Round 56 duplicate relationship-name fixture did not create every duplicate Party row.'
    Assert-Smoke ($round56Parties.Count -eq 4) 'Round 56 duplicate relationship-name Party rows did not reload from the disposable API.'

    $crudIds.actions = Test-CrudRoundTrip 'action-items' ([ordered]@{
        title = 'Round3 Action'
        area = 'Audit'
        priority = 'High'
        dueDate = '2026-06-25'
        status = 'Open'
        relatedRecord = 'R3-ORDER-001'
        notes = 'Round 3 action smoke'
    }) 'title' 'Round3 Action' 'title' 'Round3 Action Updated' 'Round3 Action Updated'

    $crudIds.taxObligations = Test-CrudRoundTrip 'tax-obligations' ([ordered]@{
        taxYear = 2026
        title = 'Round3 Tax Obligation'
        jurisdiction = 'NJ'
        obligationType = 'Sales Tax'
        formName = 'ST-50'
        period = 'Q2'
        dueDate = '2026-07-20'
        status = 'Review Applicability'
        estimatedAmount = 25
        amountPaid = 0
        paymentMethod = 'Check'
        confirmationNumber = 'R3TAX'
        proofReference = 'round3 tax proof'
        officialUrl = 'https://example.test/tax'
        appliesIf = 'Round 3 smoke'
        needsReview = $true
        notes = 'Round 3 tax obligation smoke'
    }) 'title' 'Round3 Tax Obligation' 'title' 'Round3 Tax Obligation Updated' 'Round3 Tax Obligation Updated'

    $crudIds.mileage = Test-CrudRoundTrip 'mileage-logs' ([ordered]@{
        tripDate = '2026-06-19'
        vehicle = 'Round3 Vehicle'
        startLocation = 'Shop'
        endLocation = 'Post Office'
        businessPurpose = 'Round3 Mileage'
        businessMiles = 12.5
        parkingAndTolls = 1.50
        proofReference = 'round3 mileage proof'
        needsReview = $false
        notes = 'Round 3 mileage smoke'
    }) 'businessPurpose' 'Round3 Mileage' 'businessPurpose' 'Round3 Mileage Updated' 'Round3 Mileage Updated'

    $crudIds.settings = Test-CrudRoundTrip 'settings' ([ordered]@{
        key = 'round3SmokeSetting'
        value = 'Round3 Setting'
        notes = 'Round 3 setting smoke'
    }) 'key' 'round3SmokeSetting' 'value' 'Round3 Setting Updated' $null

    $round3PaymentRows = [ordered]@{
        expenses = Invoke-RestMethod "$base/api/expenses/$($crudIds.expenses)"
        receivables = Invoke-RestMethod "$base/api/receivable-invoices/$($crudIds.receivables)"
        bills = Invoke-RestMethod "$base/api/bills/$($crudIds.bills)"
        jobs = Invoke-RestMethod "$base/api/customer-jobs/$($crudIds.jobs)"
        assets = Invoke-RestMethod "$base/api/assets/$($crudIds.assets)"
        taxObligations = Invoke-RestMethod "$base/api/tax-obligations/$($crudIds.taxObligations)"
    }
    Assert-Smoke ($round3PaymentRows.expenses.paymentMethod -eq 'Debit Card') 'Round 3 Expense did not preserve payment method.'
    Assert-Smoke ($round3PaymentRows.receivables.paymentMethod -eq 'PayPal') 'Round 3 Receivable did not preserve payment method.'
    Assert-Smoke ($round3PaymentRows.bills.paymentMethod -eq 'ACH / Bank Transfer') 'Round 3 Bill did not preserve payment method.'
    Assert-Smoke ($round3PaymentRows.jobs.paymentMethod -eq 'Zelle') 'Round 3 Customer Job did not preserve payment method.'
    Assert-Smoke ($round3PaymentRows.assets.paymentMethod -eq 'Credit Card') 'Round 3 Asset did not preserve payment method.'
    Assert-Smoke ($round3PaymentRows.taxObligations.paymentMethod -eq 'Check') 'Round 3 Tax Obligation did not preserve payment method.'

    $round78TaxProfile = Invoke-SmokeJson 'PUT' '/api/tax-profile' ([ordered]@{
        entityType = 'Single-member LLC / Schedule C'
        state = 'New Jersey'
        formationMonth = 6
        njSalesTaxRegistration = 'Registered'
        hasEmployees = 'No'
        paysContractors = 'No'
        usesVehicle = 'Yes'
        homeOffice = 'Not confirmed'
        inventoryMethod = 'Mixed / preparer review'
        businessMileageRate = 0.67
        notes = 'Round 78 tax profile smoke'
    })
    Assert-Smoke ($round78TaxProfile.businessMileageRate -eq 0.67) 'Round 78 Tax Profile did not save the mileage rate.'
    Assert-Smoke ($round78TaxProfile.formationMonth -eq 6) 'Round 78 Tax Profile did not save the NJ formation month.'

    Invoke-SmokeJson 'POST' '/api/tax-calendar/generate?year=2027' @{} | Out-Null
    $round78GeneratedObligations = @(Get-SmokeJsonArray '/api/tax-calendar?year=2027')
    Assert-Smoke (@($round78GeneratedObligations | Where-Object { $_.title -eq 'Federal estimated tax payment' -and $_.formName -eq '1040-ES' }).Count -ge 1) 'Round 78 tax calendar did not generate federal estimated tax obligations.'
    Assert-Smoke (@($round78GeneratedObligations | Where-Object { $_.title -eq 'NJ estimated Gross Income Tax payment' -and $_.formName -eq 'NJ-1040-ES' }).Count -ge 1) 'Round 78 tax calendar did not generate NJ estimated tax obligations.'
    Assert-Smoke (@($round78GeneratedObligations | Where-Object { $_.title -eq 'NJ sales and use tax return' -and $_.formName -eq 'ST-50' }).Count -ge 1) 'Round 78 tax calendar did not generate NJ sales-tax obligations.'
    $round78AnnualReports = @($round78GeneratedObligations | Where-Object { $_.formName -eq 'Annual Report' -and $_.period -eq 'Annual' })
    Assert-Smoke ($round78AnnualReports.Count -eq 1) 'Round 78 tax calendar did not generate exactly one NJ annual report row.'
    Assert-Smoke (([datetime]($round78AnnualReports[0]).dueDate).ToString('yyyy-MM') -eq '2027-06') 'Round 78 tax calendar did not place the NJ annual report in the configured formation month.'
    Assert-Smoke (@($round78GeneratedObligations | Where-Object { $_.formName -eq 'Schedule C / Form 1040' -and $_.period -eq 'Annual' }).Count -eq 1) 'Round 78 tax calendar did not generate the federal annual filing row.'

    $round78CustomObligation = Invoke-SmokeJson 'POST' '/api/tax-obligations' ([ordered]@{
        taxYear = 2027
        title = 'Round78 Custom City License Renewal'
        jurisdiction = 'Local'
        obligationType = 'Business License'
        formName = 'LOCAL-RENEW'
        period = 'Annual'
        dueDate = '2027-08-15'
        status = 'Review Applicability'
        estimatedAmount = 77
        amountPaid = 0
        paymentMethod = 'Check'
        proofReference = ''
        appliesIf = 'Round 78 custom local obligation smoke'
        needsReview = $true
        notes = 'Round 78 custom obligation'
    })
    $round78CalendarReload = @(Get-SmokeJsonArray '/api/tax-calendar?year=2027')
    Assert-Smoke (@($round78CalendarReload | Where-Object { $_.id -eq $round78CustomObligation.id -and $_.title -eq 'Round78 Custom City License Renewal' }).Count -eq 1) 'Round 78 tax calendar did not display the custom obligation.'

    $round78TaxSummaryBeforeMileage = Invoke-RestMethod "$base/api/tax-summary?year=2027"
    $round78Mileage = Invoke-SmokeJson 'POST' '/api/mileage-logs' ([ordered]@{
        tripDate = '2027-03-10'
        vehicle = 'Round78 Van'
        startLocation = 'Shop'
        endLocation = 'Customer site'
        businessPurpose = 'Round78 2027 delivery mileage'
        businessMiles = 100
        parkingAndTolls = 4
        proofReference = ''
        needsReview = $true
        notes = 'Round 78 mileage deduction smoke'
    })
    Assert-Smoke ($round78Mileage.id -gt 0) 'Round 78 mileage fixture was not created.'
    $round78TaxSummaryAfterMileage = Invoke-RestMethod "$base/api/tax-summary?year=2027"
    Assert-Smoke ($round78TaxSummaryAfterMileage.taxYear -eq 2027) 'Round 78 selected tax summary did not report 2027.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $round78TaxSummaryAfterMileage.businessMiles) - (Get-SmokeDecimalOrZero $round78TaxSummaryBeforeMileage.businessMiles)) 100.00 0.01 'Round 78 mileage business-mile delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $round78TaxSummaryAfterMileage.mileageDeductionEstimate) - (Get-SmokeDecimalOrZero $round78TaxSummaryBeforeMileage.mileageDeductionEstimate)) 67.00 0.01 'Round 78 mileage deduction delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $round78TaxSummaryAfterMileage.parkingAndTolls) - (Get-SmokeDecimalOrZero $round78TaxSummaryBeforeMileage.parkingAndTolls)) 4.00 0.01 'Round 78 parking/tolls delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $round78TaxSummaryAfterMileage.workingNetProfit) - (Get-SmokeDecimalOrZero $round78TaxSummaryBeforeMileage.workingNetProfit)) -71.00 0.01 'Round 78 mileage did not reduce working net by mileage deduction plus parking/tolls.'
    Assert-Smoke (($round78TaxSummaryAfterMileage.missingProofOrReviewCount - $round78TaxSummaryBeforeMileage.missingProofOrReviewCount) -ge 1) 'Round 78 tax summary did not surface the Needs Review mileage/obligation rows.'
    $round78TaxSummary2026 = Invoke-RestMethod "$base/api/tax-summary?year=2026"
    Assert-Smoke ($round78TaxSummary2026.taxYear -eq 2026 -and $round78TaxSummaryAfterMileage.taxYear -eq 2027) 'Round 78 tax year selection did not keep 2026 and 2027 summaries separate.'
    $round78TaxSummaryCsv = Invoke-WebRequest -Uri "$base/api/export/tax-summary?year=2027" -UseBasicParsing
    $round78TaxSummaryExportRow = @($round78TaxSummaryCsv.Content | ConvertFrom-Csv)[0]
    Assert-Smoke ($round78TaxSummaryExportRow.TaxYear -eq '2027') 'Round 78 tax summary export did not honor selected year 2027.'

    $blankPaymentExpense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round3 Blank Payment Vendor'
        category = 'Supplies'
        description = 'Round3 Blank Payment Expense'
        paymentMethod = ''
        amount = 1
        salesTax = 0
        countedExpense = $false
        taxDeductible = $false
    })
    Assert-Smoke ($blankPaymentExpense.paymentMethod -eq 'Unknown / Review') 'Blank Expense payment method did not normalize to Unknown / Review.'
    $archiveBlankPaymentExpense = Invoke-WebRequest -Method DELETE -Uri "$base/api/expenses/$($blankPaymentExpense.id)" -SkipHttpErrorCheck
    Assert-Smoke ($archiveBlankPaymentExpense.StatusCode -eq 204) "Archiving blank-payment Expense expected 204, got $($archiveBlankPaymentExpense.StatusCode)."

    $etsyDefaultJob = Invoke-SmokeJson 'POST' '/api/customer-jobs' ([ordered]@{
        jobDate = '2026-06-19'
        customerName = 'Round3 Etsy Payment Customer'
        platform = 'Etsy'
        jobName = 'Round3 Etsy Payment Default'
        status = 'Paid'
        invoiceAmount = 12
        amountPaid = 12
    })
    Assert-Smoke ($etsyDefaultJob.paymentMethod -eq 'Etsy Payments') 'Etsy Customer Job did not default payment method to Etsy Payments.'
    $archiveEtsyDefaultJob = Invoke-WebRequest -Method DELETE -Uri "$base/api/customer-jobs/$($etsyDefaultJob.id)" -SkipHttpErrorCheck
    Assert-Smoke ($archiveEtsyDefaultJob.StatusCode -eq 204) "Archiving Etsy default-payment Job expected 204, got $($archiveEtsyDefaultJob.StatusCode)."

    $dashboardBeforeRound38 = Invoke-RestMethod "$base/api/dashboard"
    $round38Sale = Invoke-SmokeJson 'POST' '/api/sales' ([ordered]@{
        saleDate = '2026-06-19'
        platform = 'Direct'
        paymentMethod = 'Unknown / Review'
        salesTaxHandling = 'Seller collected and remitted'
        orderNumber = 'R38-QA-SALE'
        customerName = 'Round38 Quick Add Customer'
        productName = 'Round38 Quick Add Widget'
        quantity = 1
        itemSales = 70
        shippingCharged = 5
        salesTaxCollected = 0
        customerPaid = 0
        platformFees = 0
        shippingLabelCost = 0
        refunds = 0
        estimatedCogs = 0
        status = 'Paid'
        includeInDashboard = $true
        needsReview = $false
        sourceProof = 'round38-quick-add-sale.pdf'
        notes = 'Round 38 Quick Add saved Sale proof.'
    })
    $round38Expense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round38 Quick Add Vendor'
        category = 'Supplies'
        taxCategory = 'Operating Expense'
        description = 'Round38 Quick Add Expense'
        paymentMethod = 'Debit Card'
        amount = 20
        salesTax = 2
        taxBucket = 'Operating Expense'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $true
        taxDeductible = $true
        needsReview = $false
        receiptProof = 'round38-quick-add-expense.pdf'
        notes = 'Round 38 Quick Add saved Expense proof.'
    })
    $round38Ar = Invoke-SmokeJson 'POST' '/api/receivable-invoices' ([ordered]@{
        invoiceNumber = 'INV-2026-R38-QA'
        invoiceDate = '2026-06-19'
        dueDate = '2026-07-19'
        customerName = 'Round38 Quick Add AR Customer'
        projectName = 'Round38 Quick Add AR'
        status = 'Sent'
        subtotal = 120
        discount = 0
        rushFee = 0
        salesTax = 0
        invoiceTotal = 0
        amountPaid = 20
        paymentMethod = 'Unknown / Review'
        needsReview = $false
        sourceProof = 'round38-quick-add-ar.pdf'
    })
    $round38Bill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round38 Quick Add Bill Vendor'
        billNumber = 'BILL-R38-QA'
        billDate = '2026-06-19'
        dueDate = '2026-07-19'
        category = 'Supplies'
        taxCategory = 'Operating Expense'
        description = 'Round38 Quick Add Bill'
        amount = 30
        salesTax = 10
        amountPaid = 10
        status = 'Unpaid'
        paymentMethod = 'Unknown / Review'
        taxDeductible = $true
        needsReview = $false
        sourceProof = 'round38-quick-add-bill.pdf'
    })
    $round38Job = Invoke-SmokeJson 'POST' '/api/customer-jobs' ([ordered]@{
        jobDate = '2026-06-19'
        customerName = 'Round38 Quick Add Job Customer'
        platform = 'Direct'
        jobNumber = 'JOB-R38-QA'
        jobName = 'Round38 Quick Add Job'
        jobType = 'Estimate'
        status = 'Quoted'
        productName = 'Round38 Quick Add Widget'
        quoteAmount = 55
        paymentMethod = 'Unknown / Review'
        needsReview = $false
        notes = 'Round 38 Quick Add saved Job proof.'
    })
    $round38Product = Invoke-SmokeJson 'POST' '/api/products' ([ordered]@{
        name = 'Round38 Quick Add Product'
        sku = 'R38-QA-PRODUCT'
        category = '3D Printed Product'
        material = 'PLA'
        color = 'Black'
        grams = 30
        materialCostPerGram = 0.04
        printHours = 2
        machineRatePerHour = 5
        packagingCost = 1
        designMinutes = 10
        targetPrice = 35
        needsReview = $true
        notes = 'Round 38 Quick Add saved Product proof.'
    })
    $round38Action = Invoke-SmokeJson 'POST' '/api/action-items' ([ordered]@{
        title = 'Round38 Quick Add Action'
        area = 'General'
        priority = 'Normal'
        dueDate = '2026-07-01'
        status = 'Open'
        relatedRecord = 'R38-QA-SALE'
        notes = 'Round 38 Quick Add saved Action proof.'
    })
    $round38AuditDoc = Invoke-SmokeJson 'POST' '/api/audit-documents' ([ordered]@{
        documentDate = '2026-06-19'
        documentType = 'Receipt'
        relatedRecordType = 'Sale'
        relatedRecordNumber = 'R38-QA-SALE'
        fileName = 'round38 quick add proof.pdf'
        filePathOrUrl = 'UploadedDocs/round38 quick add proof.pdf'
        needsReview = $true
        notes = 'Round 38 Quick Add saved Audit Doc proof.'
    })
    $round38Party = Invoke-SmokeJson 'POST' '/api/parties' ([ordered]@{
        name = 'Round38 Quick Add Contact'
        partyType = 'Both'
        email = 'round38-quick-add@example.test'
        phone = '555-380-0038'
        city = 'Newark'
        state = 'NJ'
        defaultPlatform = 'Direct'
        notes = 'Round 38 Quick Add saved Party proof.'
    })

    Assert-Smoke (@(Invoke-RestMethod "$base/api/sales" | Where-Object { $_.id -eq $round38Sale.id -and $_.orderNumber -eq 'R38-QA-SALE' }).Count -eq 1) 'Round 38 Quick Add Sale did not appear in Sales ledger.'
    Assert-Smoke (@(Invoke-RestMethod "$base/api/expenses" | Where-Object { $_.id -eq $round38Expense.id -and $_.description -eq 'Round38 Quick Add Expense' }).Count -eq 1) 'Round 38 Quick Add Expense did not appear in Expenses ledger.'
    Assert-Smoke (@(Invoke-RestMethod "$base/api/receivable-invoices" | Where-Object { $_.id -eq $round38Ar.id -and $_.invoiceNumber -eq 'INV-2026-R38-QA' }).Count -eq 1) 'Round 38 Quick Add AR row did not appear in AR ledger.'
    Assert-Smoke (@(Invoke-RestMethod "$base/api/bills" | Where-Object { $_.id -eq $round38Bill.id -and $_.billNumber -eq 'BILL-R38-QA' }).Count -eq 1) 'Round 38 Quick Add Bill did not appear in AP ledger.'
    Assert-Smoke (@(Invoke-RestMethod "$base/api/customer-jobs" | Where-Object { $_.id -eq $round38Job.id -and $_.jobNumber -eq 'JOB-R38-QA' }).Count -eq 1) 'Round 38 Quick Add Job did not appear in Customer Jobs.'
    Assert-Smoke (@(Invoke-RestMethod "$base/api/products" | Where-Object { $_.id -eq $round38Product.id -and $_.sku -eq 'R38-QA-PRODUCT' }).Count -eq 1) 'Round 38 Quick Add Product did not appear in Products / Costing.'
    Assert-Smoke (@(Invoke-RestMethod "$base/api/action-items" | Where-Object { $_.id -eq $round38Action.id -and $_.title -eq 'Round38 Quick Add Action' }).Count -eq 1) 'Round 38 Quick Add Action Item did not appear in Actions.'
    Assert-Smoke (@(Invoke-RestMethod "$base/api/audit-documents" | Where-Object { $_.id -eq $round38AuditDoc.id -and $_.fileName -eq 'round38 quick add proof.pdf' }).Count -eq 1) 'Round 38 Quick Add Audit Doc did not appear in Audit Docs.'
    Assert-Smoke (@(Invoke-RestMethod "$base/api/parties" | Where-Object { $_.id -eq $round38Party.id -and $_.name -eq 'Round38 Quick Add Contact' }).Count -eq 1) 'Round 38 Quick Add Customer/Vendor did not appear in People / Vendors.'

    $dashboardAfterRound38 = Invoke-RestMethod "$base/api/dashboard"
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardAfterRound38.kpis.grossReceipts) - (Get-SmokeDecimalOrZero $dashboardBeforeRound38.kpis.grossReceipts)) 95.00 0.01 'Round 38 Quick Add dashboard gross receipts delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardAfterRound38.kpis.customerPaid) - (Get-SmokeDecimalOrZero $dashboardBeforeRound38.kpis.customerPaid)) 95.00 0.01 'Round 38 Quick Add dashboard customer paid delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardAfterRound38.kpis.directExpenses) - (Get-SmokeDecimalOrZero $dashboardBeforeRound38.kpis.directExpenses)) 32.00 0.01 'Round 38 Quick Add dashboard direct expense and paid AP delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardAfterRound38.kpis.estimatedNet) - (Get-SmokeDecimalOrZero $dashboardBeforeRound38.kpis.estimatedNet)) 63.00 0.01 'Round 38 Quick Add dashboard estimated net delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardAfterRound38.kpis.openReceivables) - (Get-SmokeDecimalOrZero $dashboardBeforeRound38.kpis.openReceivables)) 100.00 0.01 'Round 38 Quick Add dashboard open AR delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardAfterRound38.kpis.openPayables) - (Get-SmokeDecimalOrZero $dashboardBeforeRound38.kpis.openPayables)) 30.00 0.01 'Round 38 Quick Add dashboard open AP delta.'
    $round38GrossLabels = @($dashboardAfterRound38.breakdowns.grossReceipts.items | ForEach-Object { $_.label })
    $round38KnownCostLabels = @($dashboardAfterRound38.breakdowns.knownCosts.items | ForEach-Object { $_.label })
    $round38OpenInvoices = @($dashboardAfterRound38.openInvoices | ForEach-Object { $_.invoiceNumber })
    $round38OpenBills = @($dashboardAfterRound38.openBills | ForEach-Object { "$($_.vendorName): $($_.description)" })
    $round38ActionTitles = @($dashboardAfterRound38.actions | ForEach-Object { $_.title })
    Assert-Smoke ($round38GrossLabels -contains 'Round38 Quick Add Customer: Round38 Quick Add Widget') 'Round 38 Quick Add Sale did not appear in dashboard gross receipts breakdown.'
    Assert-Smoke ($round38GrossLabels -contains 'Round38 Quick Add AR Customer: Round38 Quick Add AR') 'Round 38 Quick Add paid AR sync Sale did not appear in dashboard gross receipts breakdown.'
    Assert-Smoke ($round38KnownCostLabels -contains 'Round38 Quick Add Vendor: Round38 Quick Add Expense') 'Round 38 Quick Add Expense did not appear in dashboard known costs breakdown.'
    Assert-Smoke ($round38KnownCostLabels -contains 'Round38 Quick Add Bill Vendor: Round38 Quick Add Bill') 'Round 38 Quick Add paid AP Bill did not appear in dashboard known costs breakdown.'
    Assert-Smoke ($round38OpenInvoices -contains 'INV-2026-R38-QA') 'Round 38 Quick Add AR row did not appear in dashboard open invoices.'
    Assert-Smoke ($round38OpenBills -contains 'Round38 Quick Add Bill Vendor: Round38 Quick Add Bill') 'Round 38 Quick Add Bill did not appear in dashboard open bills.'
    Assert-Smoke ($round38ActionTitles -contains 'Round38 Quick Add Action') 'Round 38 Quick Add Action Item did not appear in dashboard actions.'

    $dashboardBeforeRound41 = Invoke-RestMethod "$base/api/dashboard"
    $round41SalePayload = [ordered]@{
        saleDate = '2026-06-19'
        platform = 'Direct'
        paymentMethod = 'Zelle'
        salesTaxHandling = 'Seller collected and remitted'
        orderNumber = 'R41-DASH-TOGGLE'
        invoiceNumber = ''
        customerName = 'Round41 Toggle Customer'
        productName = 'Round41 Toggle Widget'
        quantity = 1
        itemSales = 90
        shippingCharged = 10
        salesTaxCollected = 8
        customerPaid = 0
        platformFees = 4
        shippingLabelCost = 6
        refunds = 0
        estimatedCogs = 10
        status = 'Paid'
        includeInDashboard = $true
        needsReview = $false
        sourceProof = 'round41-dashboard-toggle.pdf'
        notes = 'Round 41 include-in-dashboard toggle coverage.'
    }
    $round41Sale = Invoke-SmokeJson 'POST' '/api/sales' $round41SalePayload
    Assert-Smoke ($round41Sale.includeInDashboard -eq $true) 'Round 41 Sale did not save with includeInDashboard=true.'
    Assert-SmokeClose $round41Sale.customerPaid 108.00 0.01 'Round 41 Sale customer paid normalization.'

    $dashboardRound41Included = Invoke-RestMethod "$base/api/dashboard"
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Included.kpis.grossReceipts) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.grossReceipts)) 100.00 0.01 'Round 41 include-in-dashboard gross receipts delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Included.kpis.customerPaid) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.customerPaid)) 108.00 0.01 'Round 41 include-in-dashboard customer paid delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Included.kpis.salesTaxMemo) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.salesTaxMemo)) 8.00 0.01 'Round 41 include-in-dashboard sales-tax memo delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Included.kpis.sellingCosts) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.sellingCosts)) 20.00 0.01 'Round 41 include-in-dashboard selling-cost delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Included.kpis.estimatedNet) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.estimatedNet)) 80.00 0.01 'Round 41 include-in-dashboard estimated-net delta.'
    $round41IncludedLabels = @($dashboardRound41Included.breakdowns.grossReceipts.items | ForEach-Object { $_.label })
    Assert-Smoke ($round41IncludedLabels -contains 'Round41 Toggle Customer: Round41 Toggle Widget') 'Round 41 included Sale did not appear in dashboard gross receipts breakdown.'

    $round41ExcludedPayload = Copy-SmokePayload $round41SalePayload
    $round41ExcludedPayload['includeInDashboard'] = $false
    $round41Excluded = Invoke-SmokeJson 'PUT' "/api/sales/$($round41Sale.id)" $round41ExcludedPayload
    Assert-Smoke ($round41Excluded.includeInDashboard -eq $false) 'Round 41 Sale did not save with includeInDashboard=false.'
    $dashboardRound41Excluded = Invoke-RestMethod "$base/api/dashboard"
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Excluded.kpis.grossReceipts) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.grossReceipts)) 0.00 0.01 'Round 41 excluded Sale changed gross receipts.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Excluded.kpis.customerPaid) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.customerPaid)) 0.00 0.01 'Round 41 excluded Sale changed customer paid.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Excluded.kpis.salesTaxMemo) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.salesTaxMemo)) 0.00 0.01 'Round 41 excluded Sale changed sales-tax memo.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Excluded.kpis.sellingCosts) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.sellingCosts)) 0.00 0.01 'Round 41 excluded Sale changed selling costs.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Excluded.kpis.estimatedNet) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.estimatedNet)) 0.00 0.01 'Round 41 excluded Sale changed estimated net.'
    $round41ExcludedLabels = @($dashboardRound41Excluded.breakdowns.grossReceipts.items | ForEach-Object { $_.label })
    Assert-Smoke (-not ($round41ExcludedLabels -contains 'Round41 Toggle Customer: Round41 Toggle Widget')) 'Round 41 excluded Sale appeared in dashboard gross receipts breakdown.'

    $round41RestoredPayload = Copy-SmokePayload $round41SalePayload
    $round41RestoredPayload['includeInDashboard'] = $true
    $round41Restored = Invoke-SmokeJson 'PUT' "/api/sales/$($round41Sale.id)" $round41RestoredPayload
    Assert-Smoke ($round41Restored.includeInDashboard -eq $true) 'Round 41 Sale did not save with includeInDashboard=true after toggle restore.'
    $dashboardRound41Restored = Invoke-RestMethod "$base/api/dashboard"
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Restored.kpis.grossReceipts) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.grossReceipts)) 100.00 0.01 'Round 41 restored Sale gross receipts delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Restored.kpis.customerPaid) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.customerPaid)) 108.00 0.01 'Round 41 restored Sale customer paid delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Restored.kpis.salesTaxMemo) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.salesTaxMemo)) 8.00 0.01 'Round 41 restored Sale sales-tax memo delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Restored.kpis.sellingCosts) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.sellingCosts)) 20.00 0.01 'Round 41 restored Sale selling-cost delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboardRound41Restored.kpis.estimatedNet) - (Get-SmokeDecimalOrZero $dashboardBeforeRound41.kpis.estimatedNet)) 80.00 0.01 'Round 41 restored Sale estimated-net delta.'

    $dashboardBeforeRound14 = Invoke-RestMethod "$base/api/dashboard"
    $taxSummaryBeforeRound14 = Invoke-RestMethod "$base/api/tax-summary?year=2026"
    $round46Today = (Get-Date).Date
    $round46BillDate = $round46Today.AddDays(-19).ToString('yyyy-MM-dd')
    $round46OverdueDueDate = $round46Today.AddDays(-1).ToString('yyyy-MM-dd')
    $round46TodayDueDate = $round46Today.ToString('yyyy-MM-dd')
    $round46SoonDueDate = $round46Today.AddDays(4).ToString('yyyy-MM-dd')
    $round46FutureDueDate = $round46Today.AddDays(43).ToString('yyyy-MM-dd')
    $round46ClosedDueDate = $round46Today.AddDays(-9).ToString('yyyy-MM-dd')
    $round46PaidPaymentDate = $round46Today.AddDays(-7).ToString('yyyy-MM-dd')

    $round46OverdueBill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round46 Overdue Bill Vendor'
        billNumber = 'BILL-R46-OVERDUE'
        billDate = $round46BillDate
        dueDate = $round46OverdueDueDate
        category = 'Utilities'
        taxCategory = 'Other business expense'
        description = 'Round46 Overdue Open Bill'
        amount = 10
        salesTax = 0
        status = 'Unpaid'
        paymentMethod = 'ACH / Bank Transfer'
        sourceProof = 'round46-overdue-bill.pdf'
        taxDeductible = $false
        needsReview = $false
    })
    $round46TodayBill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round46 Today Bill Vendor'
        billNumber = 'BILL-R46-TODAY'
        billDate = $round46BillDate
        dueDate = $round46TodayDueDate
        category = 'Utilities'
        taxCategory = 'Other business expense'
        description = 'Round46 Due Today Open Bill'
        amount = 11
        salesTax = 0
        status = 'Unpaid'
        paymentMethod = 'ACH / Bank Transfer'
        sourceProof = 'round46-today-bill.pdf'
        taxDeductible = $false
        needsReview = $false
    })
    $round46SoonBill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round46 Soon Bill Vendor'
        billNumber = 'BILL-R46-SOON'
        billDate = $round46BillDate
        dueDate = $round46SoonDueDate
        category = 'Utilities'
        taxCategory = 'Other business expense'
        description = 'Round46 Due Soon Open Bill'
        amount = 12
        salesTax = 0
        status = 'Unpaid'
        paymentMethod = 'ACH / Bank Transfer'
        sourceProof = 'round46-soon-bill.pdf'
        taxDeductible = $false
        needsReview = $false
    })
    $round46FutureBill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round46 Future Bill Vendor'
        billNumber = 'BILL-R46-FUTURE'
        billDate = $round46BillDate
        dueDate = $round46FutureDueDate
        category = 'Utilities'
        taxCategory = 'Other business expense'
        description = 'Round46 Future Open Bill'
        amount = 13
        salesTax = 0
        status = 'Unpaid'
        paymentMethod = 'ACH / Bank Transfer'
        sourceProof = 'round46-future-bill.pdf'
        taxDeductible = $false
        needsReview = $false
    })
    $round46NoDueBill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round46 No Due Bill Vendor'
        billNumber = 'BILL-R46-NODUE'
        billDate = $round46BillDate
        dueDate = $null
        category = 'Utilities'
        taxCategory = 'Other business expense'
        description = 'Round46 No Due Date Open Bill'
        amount = 14
        salesTax = 0
        status = 'Unpaid'
        paymentMethod = 'ACH / Bank Transfer'
        sourceProof = 'round46-no-due-bill.pdf'
        taxDeductible = $false
        needsReview = $false
    })
    $round46PaidBill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round46 Paid Bill Vendor'
        billNumber = 'BILL-R46-PAID'
        billDate = $round46BillDate
        dueDate = $round46ClosedDueDate
        category = 'Utilities'
        taxCategory = 'Other business expense'
        description = 'Round46 Paid Old Due Bill'
        amount = 15
        salesTax = 0
        amountPaid = 0
        paymentDate = $round46PaidPaymentDate
        status = 'Paid'
        paymentMethod = 'ACH / Bank Transfer'
        sourceProof = 'round46-paid-bill.pdf'
        taxDeductible = $false
        needsReview = $false
    })
    $round46VoidBill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round46 Void Bill Vendor'
        billNumber = 'BILL-R46-VOID'
        billDate = $round46BillDate
        dueDate = $round46ClosedDueDate
        category = 'Utilities'
        taxCategory = 'Other business expense'
        description = 'Round46 Void Old Due Bill'
        amount = 16
        salesTax = 0
        amountPaid = 0
        status = 'Void'
        paymentMethod = 'ACH / Bank Transfer'
        sourceProof = 'round46-void-bill.pdf'
        taxDeductible = $false
        needsReview = $false
    })
    Assert-SmokeClose $round46PaidBill.amountPaid 15 0.01 'Round 46 paid Bill did not normalize amount paid to total.'
    Assert-SmokeClose $round46PaidBill.balanceDue 0 0.01 'Round 46 paid Bill should not have a balance due.'
    Assert-Smoke ($round46VoidBill.status -eq 'Void') 'Round 46 void Bill status did not persist.'

    $round45OperatingBill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round45 Operating Bill Vendor'
        billNumber = 'BILL-R45-OPER'
        billDate = '2026-06-19'
        dueDate = '2026-07-19'
        category = 'Software'
        taxCategory = 'Other business expense'
        description = 'Round45 Paid Deductible Operating Bill'
        amount = 100
        salesTax = 10
        total = 1
        amountPaid = 55
        paymentDate = '2026-06-20'
        status = 'Partial'
        paymentMethod = 'Credit Card'
        paymentAccount = 'Business Card'
        sourceProof = 'round45-operating-bill.pdf'
        taxDeductible = $true
        needsReview = $true
        notes = 'Round 45 bill tax/review smoke.'
    })
    $round45CogsBill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round45 Materials Bill Vendor'
        billNumber = 'BILL-R45-COGS'
        billDate = '2026-06-19'
        dueDate = '2026-06-25'
        category = 'Filament / Material'
        taxCategory = 'COGS / materials'
        description = 'Round45 Paid Deductible Materials Bill'
        amount = 80
        salesTax = 0
        total = 1
        amountPaid = 0
        paymentDate = '2026-06-21'
        status = 'Paid'
        paymentMethod = 'Check'
        paymentAccount = 'Checking'
        sourceProof = 'round45-cogs-bill.pdf'
        taxDeductible = $true
        needsReview = $false
    })
    $round45NonDeductibleBill = Invoke-SmokeJson 'POST' '/api/bills' ([ordered]@{
        vendorName = 'Round45 Non Deductible Bill Vendor'
        billNumber = 'BILL-R45-NONDED'
        billDate = '2026-06-19'
        dueDate = '2026-06-25'
        category = 'Other'
        taxCategory = 'Other business expense'
        description = 'Round45 Paid Non Deductible Bill'
        amount = 999
        salesTax = 0
        total = 1
        amountPaid = 0
        paymentDate = '2026-06-22'
        status = 'Paid'
        paymentMethod = 'Debit Card'
        paymentAccount = 'Checking'
        sourceProof = ''
        taxDeductible = $false
        needsReview = $false
    })
    Assert-Smoke ($round45OperatingBill.taxCategory -eq 'Other business expense') 'Round 45 Bill tax category did not persist.'
    Assert-Smoke ($round45OperatingBill.needsReview -eq $true) 'Round 45 Bill needs-review flag did not persist.'
    Assert-SmokeClose $round45OperatingBill.total 110 0.01 'Round 45 operating bill total did not normalize.'
    Assert-SmokeClose $round45OperatingBill.amountPaid 55 0.01 'Round 45 operating bill partial payment did not persist.'
    Assert-SmokeClose $round45CogsBill.total 80 0.01 'Round 45 COGS bill total did not normalize.'
    Assert-SmokeClose $round45CogsBill.amountPaid 80 0.01 'Round 45 paid COGS bill amount paid did not normalize to total.'
    Assert-Smoke ($round45NonDeductibleBill.taxDeductible -eq $false) 'Round 45 non-deductible Bill flag did not persist.'

    $round21ArchivedSale = Invoke-SmokeJson 'POST' '/api/sales' ([ordered]@{
        saleDate = '2026-06-19'
        platform = 'Direct'
        paymentMethod = 'Cash'
        salesTaxHandling = 'Seller collected and remitted'
        orderNumber = 'R21-ARCHIVED-SALE'
        invoiceNumber = 'INV-2026-R21-ARCHIVED-SALE'
        customerName = 'Round21 Archived Customer'
        productName = 'Round21 Archived Widget'
        quantity = 1
        itemSales = 9999
        shippingCharged = 99
        salesTaxCollected = 999
        customerPaid = 0
        platformFees = 0
        shippingLabelCost = 0
        refunds = 0
        estimatedCogs = 0
        status = 'Paid'
        sourceProof = 'round21-archived-sale-proof.csv'
        includeInDashboard = $true
        notes = 'Round 21 archived dashboard exclusion smoke'
    })
    $archiveRound21Sale = Invoke-WebRequest -Method DELETE -Uri "$base/api/sales/$($round21ArchivedSale.id)" -SkipHttpErrorCheck
    Assert-Smoke ($archiveRound21Sale.StatusCode -eq 204) "Round 21 archived Sale expected 204, got $($archiveRound21Sale.StatusCode)."
    $round22DefaultSalesExport = Invoke-WebRequest -Uri "$base/api/export/sales" -UseBasicParsing
    $round22ArchivedSalesExport = Invoke-WebRequest -Uri "$base/api/export/sales?includeArchived=true" -UseBasicParsing
    Assert-Smoke ($round22DefaultSalesExport.Content -notlike '*R21-ARCHIVED-SALE*') 'Round 22 default Sales export included an archived row.'
    Assert-Smoke ($round22ArchivedSalesExport.Content -like '*R21-ARCHIVED-SALE*') 'Round 22 includeArchived Sales export did not include the archived row.'
    Assert-Smoke ($round22ArchivedSalesExport.Content -like 'SaleDate,Platform,PaymentMethod,SalesTaxHandling,OrderNumber,*') 'Round 22 Sales export header was missing expected leading columns.'
    $round22ParsedArchivedSalesExport = @($round22ArchivedSalesExport.Content | ConvertFrom-Csv)
    Assert-Smoke (@($round22ParsedArchivedSalesExport | Where-Object { $_.OrderNumber -eq 'R21-ARCHIVED-SALE' }).Count -eq 1) 'Round 22 Sales export could not be parsed back into the archived row.'

    $round21ArchivedExpense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round21 Archived Expense Vendor'
        category = 'Supplies'
        taxCategory = 'Operating Expense'
        description = 'Round21 Archived Counted Expense'
        amount = 888
        salesTax = 0
        taxBucket = 'Operating Expense'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $true
        taxDeductible = $true
        receiptProof = 'round21-archived-expense-receipt.pdf'
    })
    $archiveRound21Expense = Invoke-WebRequest -Method DELETE -Uri "$base/api/expenses/$($round21ArchivedExpense.id)" -SkipHttpErrorCheck
    Assert-Smoke ($archiveRound21Expense.StatusCode -eq 204) "Round 21 archived Expense expected 204, got $($archiveRound21Expense.StatusCode)."

    $round21ArchivedAr = Invoke-SmokeJson 'POST' '/api/receivable-invoices' ([ordered]@{
        invoiceNumber = 'INV-2026-R21-ARCHIVED'
        invoiceDate = '2026-06-19'
        dueDate = '2026-07-19'
        customerName = 'Round21 Archived AR Customer'
        projectName = 'Round21 Archived AR'
        status = 'Partial'
        subtotal = 777
        discount = 0
        rushFee = 0
        salesTax = 0
        invoiceTotal = 777
        amountPaid = 0
        sourceProof = 'round21-archived-ar'
    })
    $archiveRound21Ar = Invoke-WebRequest -Method DELETE -Uri "$base/api/receivable-invoices/$($round21ArchivedAr.id)" -SkipHttpErrorCheck
    Assert-Smoke ($archiveRound21Ar.StatusCode -eq 204) "Round 21 archived AR expected 204, got $($archiveRound21Ar.StatusCode)."

    $round21VoidAr = Invoke-SmokeJson 'POST' '/api/receivable-invoices' ([ordered]@{
        invoiceNumber = 'INV-2026-R21-VOID'
        invoiceDate = '2026-06-19'
        dueDate = '2026-07-19'
        customerName = 'Round21 Void AR Customer'
        projectName = 'Round21 Void AR'
        status = 'Void'
        subtotal = 666
        discount = 0
        rushFee = 0
        salesTax = 0
        invoiceTotal = 666
        amountPaid = 333
        sourceProof = 'round21-void-ar'
    })
    Assert-SmokeClose $round21VoidAr.amountPaid 0 0.01 'Round 21 void AR amount paid did not normalize to zero.'
    Assert-SmokeClose $round21VoidAr.balanceDue 0 0.01 'Round 21 void AR balance due did not normalize to zero.'
    Assert-Smoke ($round21VoidAr.includeInCashReports -eq $false) 'Round 21 void AR should not be included in cash reports.'

    $round21NonCountedExpense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round21 Non Counted Vendor'
        category = 'Supplies'
        taxCategory = 'Operating Expense'
        description = 'Round21 Non Counted Expense'
        amount = 555
        salesTax = 0
        taxBucket = 'Operating Expense'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $false
        taxDeductible = $true
        receiptProof = 'round21-non-counted-receipt.pdf'
    })
    Assert-Smoke ($round21NonCountedExpense.countedExpense -eq $false) 'Round 21 non-counted expense did not persist countedExpense=false.'

    $round14SellerSale = Invoke-SmokeJson 'POST' '/api/sales' ([ordered]@{
        saleDate = '2026-06-19'
        platform = 'Direct'
        paymentMethod = 'Cash'
        salesTaxHandling = 'Seller collected and remitted'
        orderNumber = 'R14-SELLER-001'
        invoiceNumber = 'INV-2026-R14-SELLER'
        customerName = 'Round14 Seller Tax Customer'
        productName = 'Round14 Tax Widget'
        quantity = 1
        itemSales = 100
        shippingCharged = 10
        salesTaxCollected = 6.30
        customerPaid = 0
        platformFees = 3
        shippingLabelCost = 4
        refunds = 15
        estimatedCogs = 20
        status = 'Paid'
        sourceProof = 'round14-seller-tax-proof.csv'
        includeInDashboard = $true
        notes = 'Round 14 seller-collected tax and net-income smoke'
    })
    Assert-SmokeClose $round14SellerSale.customerPaid 101.30 0.01 'Round 14 seller sale customer paid should normalize to gross receipts plus tax memo.'

    $round14MarketplaceSale = Invoke-SmokeJson 'POST' '/api/sales' ([ordered]@{
        saleDate = '2026-06-19'
        platform = 'Etsy'
        paymentMethod = 'Etsy Payments'
        salesTaxHandling = 'Marketplace collected/remitted - verify'
        orderNumber = 'R14-MKT-001'
        invoiceNumber = 'INV-2026-R14-MKT'
        customerName = 'Round14 Marketplace Tax Customer'
        productName = 'Round14 Marketplace Widget'
        quantity = 1
        itemSales = 50
        shippingCharged = 0
        salesTaxCollected = 3.31
        customerPaid = 0
        platformFees = 0
        shippingLabelCost = 0
        refunds = 0
        estimatedCogs = 0
        status = 'Paid'
        sourceProof = 'round14-marketplace-tax-proof.csv'
        includeInDashboard = $true
        notes = 'Round 14 marketplace-collected tax smoke'
    })
    Assert-SmokeClose $round14MarketplaceSale.customerPaid 53.31 0.01 'Round 14 marketplace sale customer paid should include marketplace tax memo for reconciliation.'

    $round14OperatingExpense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round14 Operating Vendor'
        category = 'Supplies'
        taxCategory = 'Operating Expense'
        description = 'Round14 Operating Deductible'
        amount = 100
        salesTax = 0
        taxBucket = 'Operating Expense'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $true
        taxDeductible = $true
        receiptProof = 'round14-operating-receipt.pdf'
    })
    $round14CogsExpense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round14 COGS Vendor'
        category = 'Filament'
        taxCategory = 'COGS/Materials'
        description = 'Round14 COGS Deductible'
        amount = 40
        salesTax = 0
        taxBucket = 'COGS/Materials'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $true
        taxDeductible = $true
        receiptProof = 'round14-cogs-receipt.pdf'
    })
    $round14NonDeductibleExpense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round14 Excluded Vendor'
        category = 'Personal'
        taxCategory = 'Operating Expense'
        description = 'Round14 Non Deductible'
        amount = 200
        salesTax = 0
        taxBucket = 'Operating Expense'
        deductibleStatus = 'No'
        businessUsePercent = 100
        countedExpense = $true
        taxDeductible = $false
        receiptProof = 'round14-nondeductible-receipt.pdf'
    })
    $round14MemoExpense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round14 Memo Vendor'
        category = 'Memo'
        taxCategory = 'Memo Only'
        description = 'Round14 Memo Only'
        amount = 300
        salesTax = 0
        taxBucket = 'Memo Only'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $true
        taxDeductible = $true
        receiptProof = 'round14-memo-receipt.pdf'
    })
    $round14AssetExpense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round14 Asset Expense Vendor'
        category = 'Equipment'
        taxCategory = 'Asset'
        description = 'Round14 Asset Expense'
        amount = 400
        salesTax = 0
        taxBucket = 'Asset'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $true
        taxDeductible = $true
        receiptProof = 'round14-asset-expense-receipt.pdf'
    })
    $round14UncheckedExpense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round14 Unchecked Vendor'
        category = 'Supplies'
        taxCategory = 'Operating Expense'
        description = 'Round14 Unchecked Expense'
        amount = 500
        salesTax = 0
        taxBucket = 'Operating Expense'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $false
        taxDeductible = $true
        receiptProof = 'round14-unchecked-receipt.pdf'
    })
    $round14NonDeductibleAsset = Invoke-SmokeJson 'POST' '/api/assets' ([ordered]@{
        name = 'Round14 Non Deductible Asset'
        purchaseDate = '2026-06-19'
        vendorName = 'Round14 Asset Vendor'
        category = 'Equipment'
        cost = 999
        businessUsePercent = 100
        inServiceDate = '2026-06-19'
        taxTreatment = 'Not Deductible'
        countedExpenseThisYear = $true
        sourceProof = 'round14-nondeductible-asset.pdf'
    })

    $round14UnpaidAr = Invoke-SmokeJson 'POST' '/api/receivable-invoices' ([ordered]@{
        invoiceNumber = 'INV-2026-R14-UNPAID'
        invoiceDate = '2026-06-19'
        customerName = 'Round14 AR Unpaid Customer'
        projectName = 'Round14 AR Unpaid'
        status = 'Sent'
        subtotal = 100
        discount = 0
        rushFee = 0
        salesTax = 0
        invoiceTotal = 100
        amountPaid = 0
        sourceProof = 'round14-ar-unpaid'
    })
    $round14PartialAr = Invoke-SmokeJson 'POST' '/api/receivable-invoices' ([ordered]@{
        invoiceNumber = 'INV-2026-R14-PARTIAL'
        invoiceDate = '2026-06-19'
        customerName = 'Round14 AR Partial Customer'
        projectName = 'Round14 AR Partial'
        status = 'Partial'
        subtotal = 120
        discount = 0
        rushFee = 0
        salesTax = 0
        invoiceTotal = 120
        amountPaid = 50
        sourceProof = 'round14-ar-partial'
    })
    $round14PaidAr = Invoke-SmokeJson 'POST' '/api/receivable-invoices' ([ordered]@{
        invoiceNumber = 'INV-2026-R14-PAID'
        invoiceDate = '2026-06-19'
        customerName = 'Round14 AR Paid Customer'
        projectName = 'Round14 AR Paid'
        status = 'Paid'
        subtotal = 80
        discount = 0
        rushFee = 0
        salesTax = 0
        invoiceTotal = 80
        amountPaid = 0
        sourceProof = 'round14-ar-paid'
    })
    $round14VoidAr = Invoke-SmokeJson 'POST' '/api/receivable-invoices' ([ordered]@{
        invoiceNumber = 'INV-2026-R14-VOID'
        invoiceDate = '2026-06-19'
        customerName = 'Round14 AR Void Customer'
        projectName = 'Round14 AR Void'
        status = 'Void'
        subtotal = 60
        discount = 0
        rushFee = 0
        salesTax = 0
        invoiceTotal = 60
        amountPaid = 30
        sourceProof = 'round14-ar-void'
    })

    Assert-SmokeClose $round14UnpaidAr.amountPaid 0 0.01 'Round 14 unpaid AR amount paid did not normalize to zero.'
    Assert-SmokeClose $round14UnpaidAr.balanceDue 100 0.01 'Round 14 unpaid AR balance due did not normalize to full total.'
    Assert-SmokeClose $round14PartialAr.amountPaid 50 0.01 'Round 14 partial AR amount paid did not remain partial.'
    Assert-SmokeClose $round14PartialAr.balanceDue 70 0.01 'Round 14 partial AR balance due did not normalize.'
    Assert-SmokeClose $round14PaidAr.amountPaid 80 0.01 'Round 14 paid AR amount paid did not normalize to invoice total.'
    Assert-SmokeClose $round14PaidAr.balanceDue 0 0.01 'Round 14 paid AR balance due did not normalize to zero.'
    Assert-SmokeClose $round14VoidAr.amountPaid 0 0.01 'Round 14 void AR amount paid did not normalize to zero.'
    Assert-SmokeClose $round14VoidAr.balanceDue 0 0.01 'Round 14 void AR balance due did not normalize to zero.'
    Assert-Smoke ($round14VoidAr.includeInCashReports -eq $false) 'Round 14 void AR should not be included in cash reports.'

    $lookups = Invoke-RestMethod "$base/api/lookups"
    Assert-Smoke (@($lookups.products | Where-Object { $_.name -eq 'Round3 Product Updated' }).Count -eq 1) 'Lookups did not include the smoke product for invoice-builder datalists.'
    Assert-Smoke (@($lookups.customers | Where-Object { $_ -eq '=ROUND3-FORMULA-SAFE' }).Count -eq 1) 'Lookups did not include the smoke customer.'

    $round77ProductCountBefore = @(Get-SmokeJsonArray '/api/products?includeArchived=true').Count
    $round77DocumentCountBefore = @(Get-SmokeJsonArray '/api/documents?includeArchived=true').Count
    $round77CatalogDraft = Invoke-SmokeJson 'POST' '/api/ai/estimate-draft' @{
        sourceName = 'round77-known-product-request.txt'
        sourceText = 'Please quote one Round3 Product Updated.'
    }
    Assert-Smoke ($round77CatalogDraft.prefill.material -eq 'PLA') 'Round 77 AI Estimate catalog matching did not apply the saved Product material.'
    Assert-Smoke ($round77CatalogDraft.prefill.color -eq 'Blue') 'Round 77 AI Estimate catalog matching did not apply the saved Product color.'
    Assert-SmokeClose $round77CatalogDraft.prefill.calcGrams 42 0.01 'Round 77 AI Estimate catalog matching saved Product grams.'
    Assert-SmokeClose $round77CatalogDraft.prefill.calcHours 3.5 0.01 'Round 77 AI Estimate catalog matching saved Product print hours.'
    Assert-Smoke (@($round77CatalogDraft.warnings | Where-Object { $_ -like '*Saved product costing was applied*' }).Count -eq 1) 'Round 77 AI Estimate catalog matching did not disclose saved Product costing use.'
    $round77ProductCountAfterCatalogDraft = @(Get-SmokeJsonArray '/api/products?includeArchived=true').Count
    $round77DocumentCountAfterCatalogDraft = @(Get-SmokeJsonArray '/api/documents?includeArchived=true').Count
    Assert-Smoke ($round77ProductCountAfterCatalogDraft -eq $round77ProductCountBefore) 'Round 77 AI Estimate catalog draft saved or changed a Product automatically.'
    Assert-Smoke ($round77DocumentCountAfterCatalogDraft -eq $round77DocumentCountBefore) 'Round 77 AI Estimate catalog draft created an invoice/estimate document automatically.'

    $round77BulkDraft = Invoke-SmokeJson 'POST' '/api/ai/estimate-draft' @{
        sourceName = 'round77-bulk-guitar-pick-request.txt'
        sourceText = 'Please quote 300 custom guitar picks, slightly oversized at 1.2mm, with a four color cartoon. The $200 design/proof phase is approved. Make a sample first. I was hoping to spend $700.'
    }
    Assert-Smoke ($round77BulkDraft.usedAi -eq $false) 'Round 77 bulk estimate should remain deterministic local fallback unless local AI is configured.'
    Assert-Smoke ($round77BulkDraft.prefill.projectName -like '*Guitar Pick*') 'Round 77 bulk estimate did not identify the actual guitar-pick job.'
    Assert-SmokeClose $round77BulkDraft.prefill.calcGrams 562.5 0.01 'Round 77 bulk estimate total material planning assumption.'
    Assert-SmokeClose $round77BulkDraft.prefill.calcHours 120 0.01 'Round 77 bulk estimate total machine-time planning assumption.'
    $round77BulkHandlingLine = @($round77BulkDraft.prefill.lineItems | Where-Object { $_.description -like '*Bulk handling*' })[0]
    Assert-Smoke ($null -ne $round77BulkHandlingLine) 'Round 77 bulk estimate did not include the configured bulk handling line.'
    Assert-SmokeClose $round77BulkHandlingLine.quantity 300 0.01 'Round 77 bulk estimate did not carry the parsed 300-piece quantity into the bulk handling line.'
    Assert-Smoke (@($round77BulkDraft.warnings | Where-Object { $_ -like '*300*guitar picks*' -or $_ -like '*stated*budget*' }).Count -ge 1) 'Round 77 bulk estimate did not disclose its quantity/budget planning assumptions.'

    $round77LargeSource = 'R' * 500001
    $round77LargeTextResponse = Invoke-WebRequest `
        -Method POST `
        -Uri "$base/api/ai/estimate-draft" `
        -ContentType 'application/json' `
        -Body (@{ sourceName = 'round77-too-large-source.txt'; sourceText = $round77LargeSource } | ConvertTo-Json -Depth 5) `
        -SkipHttpErrorCheck
    Assert-Smoke ($round77LargeTextResponse.StatusCode -eq 400) "Round 77 oversized AI Estimate source expected 400, got $($round77LargeTextResponse.StatusCode)."
    Assert-Smoke ($round77LargeTextResponse.Content -like '*combined extracted text is too large*' -and $round77LargeTextResponse.Content -like '*500,000*') 'Round 77 oversized AI Estimate source did not return the friendly size message.'

    $round76MarketplaceDraft = Invoke-RestMethod -Method Post -Uri "$base/api/ai/operations/marketplace-order-import" -Form @{
        sourceName = 'round76-etsy-order.txt'
        sourceText = 'Etsy Order # R76-ORDER-001 Order date Jun 12, 2026 Buyer Round76 Buyer (round76buyer) Item: Round76 Paid Widget Quantity: 1 Item total: $35.00 Shipping charged: $4.00 Sales tax: $2.58 Order total: $41.58'
        sourceUrls = ''
    }
    $round76MissingDateSale = $round76MarketplaceDraft.sale | ConvertTo-Json -Depth 12 | ConvertFrom-Json
    $round76MissingDateSale.saleDate = $null
    $round76MissingDateSave = Invoke-WebRequest -Method Post -Uri "$base/api/ai/operations/marketplace-order-import/save" -Form @{
        saleJson = ($round76MissingDateSale | ConvertTo-Json -Depth 12)
        jobJson = ($round76MarketplaceDraft.job | ConvertTo-Json -Depth 12)
        createJob = 'false'
        detectedOrderNumbersJson = (ConvertTo-Json -InputObject @($round76MarketplaceDraft.detectedOrderNumbers) -Compress)
    } -SkipHttpErrorCheck
    Assert-Smoke ($round76MissingDateSave.StatusCode -eq 400) 'Round 76 paid marketplace save accepted a missing Sale Date.'
    Assert-Smoke ($round76MissingDateSave.Content -like '*Sale Date is required*') 'Round 76 paid marketplace missing-date error was not friendly.'

    $round76ProductCountBefore = @(Get-SmokeJsonArray '/api/products?includeArchived=true').Count
    $round76ProductImport = Invoke-RestMethod -Method Post -Uri "$base/api/ai/operations/product-import" -Form @{
        sourceName = 'round76-product-source.txt'
        sourceText = "Round76 Imported Clamp`nBlack PETG replacement clamp for a local test fixture."
        sourceUrls = ''
    }
    Assert-Smoke ($round76ProductImport.product.name -like '*Round76 Imported Clamp*') 'Round 76 Product Import did not return the expected Product draft name.'
    Assert-Smoke ($round76ProductImport.product.material -eq 'PETG') 'Round 76 Product Import did not recover material.'
    Assert-Smoke ($round76ProductImport.product.color -eq 'black') 'Round 76 Product Import did not recover color.'
    Assert-Smoke ($round76ProductImport.product.needsReview -eq $true) 'Round 76 Product Import draft should require review.'
    Assert-Smoke (-not [string]::IsNullOrWhiteSpace([string]$round76ProductImport.listing.title)) 'Round 76 Product Import did not return listing title copy.'
    Assert-Smoke (-not [string]::IsNullOrWhiteSpace([string]$round76ProductImport.listing.description)) 'Round 76 Product Import did not return listing description copy.'
    $round76ProductCountAfter = @(Get-SmokeJsonArray '/api/products?includeArchived=true').Count
    Assert-Smoke ($round76ProductCountAfter -eq $round76ProductCountBefore) 'Round 76 Product Import saved a Product automatically.'

    $round76JobPlan = Invoke-SmokeJson 'POST' '/api/ai/operations/job-plan' @{
        sourceText = 'Round76 build 10 black PETG brackets, confirm requirements, slice, prototype, produce, QC, package, and invoice.'
    }
    Assert-Smoke (@($round76JobPlan.tasks).Count -ge 5) 'Round 76 Job Planner did not return a practical task list.'
    $round76JobPlanActions = Invoke-SmokeJson 'POST' '/api/ai/operations/job-plan/actions' @{
        jobName = $round76JobPlan.jobName
        tasks = $round76JobPlan.tasks
    }
    Assert-Smoke (@($round76JobPlanActions).Count -ge 5) 'Round 76 confirmed Job Planner did not create Action Items.'
    Assert-Smoke (@($round76JobPlanActions | Where-Object { $_.notes -like '*AUTOMATION KEY: job-plan*' }).Count -eq @($round76JobPlanActions).Count) 'Round 76 Job Planner Action Items did not preserve deduplication keys.'
    $round76JobPlanRepeat = Invoke-SmokeJson 'POST' '/api/ai/operations/job-plan/actions' @{
        jobName = $round76JobPlan.jobName
        tasks = $round76JobPlan.tasks
    }
    Assert-Smoke (@($round76JobPlanRepeat).Count -eq 0) 'Round 76 repeated Job Planner confirmation created duplicate open tasks.'

    $round76SlicerRead = Invoke-RestMethod -Method Post -Uri "$base/api/ai/operations/slicer-read" -Form @{
        sourceName = 'round76-slicer.txt'
        sourceText = 'Material: PETG black. Filament used 184.6g. Print time 8h 42m. Plates: 2. Quantity: 4.'
        sourceUrls = ''
    }
    Assert-Smoke ($round76SlicerRead.slicer.material -eq 'PETG') 'Round 76 Slicer Reader did not extract material.'
    Assert-Smoke ($round76SlicerRead.slicer.color -eq 'black') 'Round 76 Slicer Reader did not extract color.'
    Assert-SmokeClose $round76SlicerRead.slicer.grams 184.6 0.01 'Round 76 Slicer Reader grams.'
    Assert-SmokeClose $round76SlicerRead.slicer.printHours 8.7 0.01 'Round 76 Slicer Reader print hours.'
    Assert-Smoke ($round76SlicerRead.slicer.plateCount -eq 2) 'Round 76 Slicer Reader did not extract plate count.'
    Assert-Smoke ($round76SlicerRead.slicer.quantity -eq 4) 'Round 76 Slicer Reader did not extract quantity.'
    Assert-Smoke ($round76SlicerRead.productPrefill.needsReview -eq $true) 'Round 76 Slicer Reader Product prefill should require review.'

    $round76ListingProductBefore = Invoke-RestMethod "$base/api/products/$($crudIds.products)"
    $round76Listing = Invoke-SmokeJson 'POST' '/api/ai/operations/listing' @{
        productId = $crudIds.products
        platform = 'Etsy'
        extraInstructions = 'Round 76 local smoke only.'
    }
    Assert-Smoke (-not [string]::IsNullOrWhiteSpace([string]$round76Listing.listing.title)) 'Round 76 Listing Writer did not return a title.'
    Assert-Smoke (-not [string]::IsNullOrWhiteSpace([string]$round76Listing.listing.description)) 'Round 76 Listing Writer did not return a description.'
    $round76ListingProductAfter = Invoke-RestMethod "$base/api/products/$($crudIds.products)"
    Assert-Smoke ($round76ListingProductAfter.updatedAtUtc -eq $round76ListingProductBefore.updatedAtUtc) 'Round 76 Listing Writer modified the Product row.'

    $round76LedgerAnswer = Invoke-SmokeJson 'POST' '/api/ai/operations/ask-ledger' @{
        query = 'Round3 Product Updated'
    }
    $round76LedgerProductHits = @($round76LedgerAnswer.results | Where-Object { $_.route -eq 'products' -and $_.id -eq $crudIds.products })
    Assert-Smoke ($round76LedgerProductHits.Count -eq 1) 'Round 76 Ask Ledger did not return the matching Product.'
    Assert-Smoke (-not [string]::IsNullOrWhiteSpace([string]$round76LedgerProductHits[0].title)) 'Round 76 Ask Ledger matching result did not include an open-record title.'
    Assert-Smoke ($round76LedgerAnswer.receipt.safety -like '*never edits*') 'Round 76 Ask Ledger did not report its read-only boundary.'

    $jobTimeline = Invoke-RestMethod "$base/api/job-timeline?q=Round3"
    Assert-Smoke (@($jobTimeline.timelines).Count -gt 0) 'Job timeline did not return Round 3 job/communication/queue data.'
    $timelineEvents = @($jobTimeline.timelines | ForEach-Object { $_.events } | ForEach-Object { $_ })
    $round3JobEvents = @($timelineEvents | Where-Object { $_.kind -eq 'Job' -and $_.routePage -eq 'customerJobs' -and $_.recordId -eq $crudIds.jobs })
    $round3QueueEvents = @($timelineEvents | Where-Object { $_.kind -eq 'Printer Queue' -and $_.routePage -eq 'printerQueue' -and $_.recordId -eq $crudIds.queue })
    Assert-Smoke ($round3JobEvents.Count -eq 1) 'Round 53 job timeline did not expose the exact Customer Job row target.'
    Assert-Smoke ($round3JobEvents[0].title -eq 'JOB-R3-001') 'Round 53 job timeline Customer Job event title changed unexpectedly.'
    Assert-Smoke ($round3QueueEvents.Count -eq 1) 'Round 53 job timeline did not expose the exact Printer Queue row target.'
    Assert-Smoke ($round3QueueEvents[0].title -eq 'Round3 Job Updated') 'Round 53 job timeline Printer Queue event title changed unexpectedly.'

    $dashboard = Invoke-RestMethod "$base/api/dashboard"
    Assert-Smoke ($dashboard) 'Dashboard endpoint returned an empty response.'
    $taxAudit = Invoke-RestMethod "$base/api/tax-audit"
    Assert-Smoke ($taxAudit) 'Tax audit endpoint returned an empty response.'
    $taxSummary = Invoke-RestMethod "$base/api/tax-summary?year=2026"
    Assert-Smoke ($taxSummary) 'Tax summary endpoint returned an empty response.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboard.kpis.grossReceipts) - (Get-SmokeDecimalOrZero $dashboardBeforeRound14.kpis.grossReceipts)) 275.00 0.01 'Round 14 dashboard gross receipts delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboard.kpis.customerPaid) - (Get-SmokeDecimalOrZero $dashboardBeforeRound14.kpis.customerPaid)) 284.61 0.01 'Round 14 dashboard customer paid delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboard.kpis.salesTaxMemo) - (Get-SmokeDecimalOrZero $dashboardBeforeRound14.kpis.salesTaxMemo)) 9.61 0.01 'Round 14 dashboard sales-tax memo delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboard.kpis.sellingCosts) - (Get-SmokeDecimalOrZero $dashboardBeforeRound14.kpis.sellingCosts)) 27.00 0.01 'Round 14 dashboard selling-cost delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboard.kpis.directExpenses) - (Get-SmokeDecimalOrZero $dashboardBeforeRound14.kpis.directExpenses)) 275.00 0.01 'Round 14/Round 45 dashboard deductible expense and paid bill delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboard.kpis.taxPrepDeductions) - (Get-SmokeDecimalOrZero $dashboardBeforeRound14.kpis.taxPrepDeductions)) 275.00 0.01 'Round 45 paid deductible Bills did not affect dashboard tax-prep deductions.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboard.kpis.estimatedNet) - (Get-SmokeDecimalOrZero $dashboardBeforeRound14.kpis.estimatedNet)) -27.00 0.01 'Round 14/Round 45 dashboard estimated-net delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboard.kpis.openReceivables) - (Get-SmokeDecimalOrZero $dashboardBeforeRound14.kpis.openReceivables)) 170.00 0.01 'Round 21 dashboard open-receivables exclusion delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboard.kpis.openPayables) - (Get-SmokeDecimalOrZero $dashboardBeforeRound14.kpis.openPayables)) 115.00 0.01 'Round 45/Round 46 open Bills did not affect dashboard open-payables delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $dashboard.kpis.needsReviewCount) - (Get-SmokeDecimalOrZero $dashboardBeforeRound14.kpis.needsReviewCount)) 1.00 0.01 'Round 45 Bill needs-review flag did not affect dashboard review count.'

    $round21GrossLabels = @($dashboard.breakdowns.grossReceipts.items | ForEach-Object { $_.label })
    $round21KnownCostLabels = @($dashboard.breakdowns.knownCosts.items | ForEach-Object { $_.label })
    $round21OpenInvoiceNumbers = @($dashboard.openInvoices | ForEach-Object { $_.invoiceNumber })
    $round46OpenBills = @($dashboard.openBills | Where-Object { $_.vendorName -like 'Round46*' })
    $round46OpenBillVendors = @($round46OpenBills | ForEach-Object { $_.vendorName })
    $round46OpenBillUrgencies = @($round46OpenBills | ForEach-Object { $_.urgency })
    Assert-Smoke (-not ($round21GrossLabels -contains 'Round21 Archived Customer: Round21 Archived Widget')) 'Round 21 archived Sale appeared in dashboard gross receipts.'
    Assert-Smoke (-not ($round21KnownCostLabels -contains 'Round21 Archived Expense Vendor: Round21 Archived Counted Expense')) 'Round 21 archived Expense appeared in dashboard known costs.'
    Assert-Smoke (-not ($round21OpenInvoiceNumbers -contains 'INV-2026-R21-ARCHIVED')) 'Round 21 archived AR appeared in dashboard open invoices.'
    Assert-Smoke (-not ($round21OpenInvoiceNumbers -contains 'INV-2026-R21-VOID')) 'Round 21 void AR appeared in dashboard open invoices.'
    Assert-Smoke (-not ($round21KnownCostLabels -contains 'Round21 Non Counted Vendor: Round21 Non Counted Expense')) 'Round 21 non-counted Expense appeared in dashboard known costs.'
    Assert-Smoke ($round21KnownCostLabels -contains 'Round45 Operating Bill Vendor: Round45 Paid Deductible Operating Bill') 'Round 45 paid operating Bill did not appear in dashboard known costs.'
    Assert-Smoke ($round21KnownCostLabels -contains 'Round45 Materials Bill Vendor: Round45 Paid Deductible Materials Bill') 'Round 45 paid COGS Bill did not appear in dashboard known costs.'
    Assert-Smoke (-not ($round21KnownCostLabels -contains 'Round45 Non Deductible Bill Vendor: Round45 Paid Non Deductible Bill')) 'Round 45 non-deductible Bill appeared in dashboard known costs.'
    Assert-Smoke ($round46OpenBills.Count -eq 5) "Round 46 expected five open AP rows on dashboard, got $($round46OpenBills.Count)."
    Assert-Smoke (($round46OpenBillVendors -join '|') -eq 'Round46 Overdue Bill Vendor|Round46 Today Bill Vendor|Round46 Soon Bill Vendor|Round46 Future Bill Vendor|Round46 No Due Bill Vendor') 'Round 46 open Bills were not sorted by due-date urgency.'
    Assert-Smoke (($round46OpenBillUrgencies -join '|') -eq 'Overdue|Due today|Due soon|Scheduled|No due date') 'Round 46 open Bills did not expose the expected urgency buckets.'
    $round46OpenBillByVendor = @{}
    foreach ($bill in $round46OpenBills) { $round46OpenBillByVendor[$bill.vendorName] = $bill }
    Assert-Smoke ($round46OpenBillByVendor['Round46 Overdue Bill Vendor'].daysUntilDue -eq -1) 'Round 46 overdue Bill daysUntilDue was not -1.'
    Assert-Smoke ($round46OpenBillByVendor['Round46 Today Bill Vendor'].daysUntilDue -eq 0) 'Round 46 due-today Bill daysUntilDue was not 0.'
    Assert-Smoke ($round46OpenBillByVendor['Round46 Soon Bill Vendor'].daysUntilDue -eq 4) 'Round 46 due-soon Bill daysUntilDue was not 4.'
    Assert-Smoke ($round46OpenBillByVendor['Round46 No Due Bill Vendor'].daysUntilDue -eq $null) 'Round 46 no-due Bill should not have daysUntilDue.'
    Assert-Smoke (-not ($round46OpenBillVendors -contains 'Round46 Paid Bill Vendor')) 'Round 46 paid Bill appeared in dashboard open Bills.'
    Assert-Smoke (-not ($round46OpenBillVendors -contains 'Round46 Void Bill Vendor')) 'Round 46 void Bill appeared in dashboard open Bills.'

    $round45ReviewLabels = @($dashboard.breakdowns.needsReview.items | ForEach-Object { $_.label })
    Assert-Smoke ($round45ReviewLabels -contains 'Round45 Operating Bill Vendor: Round45 Paid Deductible Operating Bill') 'Round 45 Bill did not appear in dashboard needs-review breakdown.'

    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.grossReceipts) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.grossReceipts)) 275.00 0.01 'Round 14 tax summary gross receipts delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.customerPaidIncludingTax) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.customerPaidIncludingTax)) 284.61 0.01 'Round 14 tax summary customer-paid delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.salesTaxMemo) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.salesTaxMemo)) 9.61 0.01 'Round 14 tax summary sales tax memo delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.sellerCollectedSalesTax) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.sellerCollectedSalesTax)) 6.30 0.01 'Round 14 seller-collected tax delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.marketplaceSalesTaxMemo) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.marketplaceSalesTaxMemo)) 3.31 0.01 'Round 14 marketplace-collected tax delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.platformAndShippingCosts) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.platformAndShippingCosts)) 7.00 0.01 'Round 14 platform/shipping cost delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.estimatedCogs) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.estimatedCogs)) 20.00 0.01 'Round 14 estimated COGS delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.operatingExpenseDeductions) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.operatingExpenseDeductions)) 155.00 0.01 'Round 14/Round 45 operating expense and paid bill deduction delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.cogsMaterialExpenseDeductions) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.cogsMaterialExpenseDeductions)) 120.00 0.01 'Round 14/Round 45 COGS/material expense and paid bill deduction delta.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.expensedAssets) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.expensedAssets)) 0.00 0.01 'Round 14 non-deductible asset should not affect expensed assets.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummary.workingNetProfit) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound14.workingNetProfit)) -27.00 0.01 'Round 14/Round 45 tax summary working net delta.'
    Assert-Smoke (($taxSummary.missingProofOrReviewCount - $taxSummaryBeforeRound14.missingProofOrReviewCount) -eq 1) 'Round 45 paid deductible Bill needs-review flag did not affect tax summary review count.'
    Assert-Smoke (@($taxAudit.issues | Where-Object { $_.title -eq 'AP bill needs tax classification' -and $_.record -eq 'BILL-R45-OPER' }).Count -eq 1) 'Round 45 Bill needs-review flag did not appear in tax audit.'
    Assert-Smoke (@($taxAudit.issues | Where-Object { $_.record -eq 'BILL-R45-NONDED' }).Count -eq 0) 'Round 45 non-deductible Bill incorrectly appeared in tax audit.'

    $round14OpenInvoiceNumbers = @($dashboard.openInvoices | ForEach-Object { $_.invoiceNumber })
    Assert-Smoke ($round14OpenInvoiceNumbers -contains 'INV-2026-R14-UNPAID') 'Round 14 unpaid AR should appear in open receivables.'
    Assert-Smoke ($round14OpenInvoiceNumbers -contains 'INV-2026-R14-PARTIAL') 'Round 14 partial AR should appear in open receivables.'
    Assert-Smoke (-not ($round14OpenInvoiceNumbers -contains 'INV-2026-R14-PAID')) 'Round 14 paid AR should not appear in open receivables.'
    Assert-Smoke (-not ($round14OpenInvoiceNumbers -contains 'INV-2026-R14-VOID')) 'Round 14 void AR should not appear in open receivables.'

    $njRound14TaxCsv = Invoke-WebRequest -Uri "$base/api/export/nj-sales-tax?year=2026" -UseBasicParsing
    Assert-Smoke ($njRound14TaxCsv.Content -like '*Seller Collected*R14-SELLER-001*') 'Round 14 seller-collected sale was not distinguishable in NJ sales-tax export.'
    Assert-Smoke ($njRound14TaxCsv.Content -like '*Marketplace Collected / Remitted*R14-MKT-001*') 'Round 14 marketplace-collected sale was not distinguishable in NJ sales-tax export.'

    $round80UnderpricedProduct = Invoke-SmokeJson 'POST' '/api/products' ([ordered]@{
        name = 'Round80 Underpriced Review Product'
        sku = 'R80-UNDER'
        category = '3D Printed Product'
        material = 'PETG'
        color = 'Blue'
        grams = 250
        materialCostPerGram = 0.08
        printHours = 8
        machineRatePerHour = 7
        packagingCost = 5
        targetPrice = 10
        notes = 'Round 80 AI Review underpriced product fixture'
    })
    Assert-Smoke ($round80UnderpricedProduct.id -gt 0) 'Round 80 underpriced product fixture was not created.'
    $round80UnlinkedProof = Invoke-SmokeJson 'POST' '/api/audit-documents' ([ordered]@{
        documentDate = '2026-06-19'
        documentType = 'Receipt'
        relatedRecordType = ''
        relatedRecordNumber = ''
        fileName = 'round80-unlinked-proof.pdf'
        filePathOrUrl = ''
        needsReview = $true
        notes = 'Round 80 AI Review unlinked proof fixture'
    })
    Assert-Smoke ($round80UnlinkedProof.id -gt 0) 'Round 80 unlinked proof fixture was not created.'

    $taxCalendar = Invoke-RestMethod "$base/api/tax-calendar?year=2026"
    Assert-Smoke ($taxCalendar) 'Tax calendar endpoint returned an empty response.'
    $review = Invoke-RestMethod "$base/api/ai/review"
    Assert-Smoke ($review) 'AI review endpoint returned an empty response.'
    Assert-Smoke (@($review.items | Where-Object { $_.title -eq 'Bills need tax or deductibility review' -and $_.evidence -like '*BILL-R45-OPER*' }).Count -eq 1) 'Round 45 Bill needs-review flag did not appear in AI review.'
    Assert-Smoke (@($review.items | Where-Object { $_.title -eq 'Products are priced below entered cost' -and $_.route -eq 'products' -and $_.evidence -like '*Round80 Underpriced Review Product*' }).Count -eq 1) 'Round 80 underpriced Product did not create a routed AI Review pricing finding.'
    Assert-Smoke (@($review.items | Where-Object { $_.title -eq 'Uploaded proof needs linking or review' -and $_.route -eq 'auditDocs' -and $_.evidence -like '*round80-unlinked-proof.pdf*' }).Count -eq 1) 'Round 80 unlinked proof did not create a routed AI Review proof-linking finding.'
    Assert-Smoke (@($review.items | Where-Object { $_.route -eq 'taxObligations' -and $_.title -eq 'Tax filings, payments, or annual fees need review' }).Count -eq 1) 'Round 80 Tax review obligations did not create a routed AI Review finding.'
    $round54CommunicationReview = @($review.items | Where-Object { $_.title -eq 'Customer communication follow-ups are due' -and $_.route -eq 'communications' })
    Assert-Smoke ($round54CommunicationReview.Count -ge 1) 'Round 54 communication follow-up did not create an AI review finding.'
    Assert-Smoke ((($round54CommunicationReview | ForEach-Object { $_.evidence }) -join ' | ') -like '*Round54 Communication Customer: Round54 Incoming Approval*') 'Round 54 communication follow-up evidence was not present in AI review.'
    $round46OverdueReview = @($review.items | Where-Object { $_.title -eq 'Vendor bills are overdue' })
    Assert-Smoke ($round46OverdueReview.Count -ge 1) 'Round 46 overdue Bill did not create an AI review finding.'
    $round46OverdueEvidence = ($round46OverdueReview | ForEach-Object { $_.evidence }) -join ' | '
    Assert-Smoke ($round46OverdueEvidence -like '*BILL-R46-OVERDUE*') 'Round 46 overdue Bill was not included in AI review evidence.'
    Assert-Smoke ($round46OverdueEvidence -notlike '*BILL-R46-TODAY*') 'Round 46 due-today Bill was incorrectly flagged as overdue.'
    Assert-Smoke ($round46OverdueEvidence -notlike '*BILL-R46-PAID*') 'Round 46 paid Bill was incorrectly flagged as overdue.'
    Assert-Smoke ($round46OverdueEvidence -notlike '*BILL-R46-VOID*') 'Round 46 void Bill was incorrectly flagged as overdue.'
    $round57QueueReview = @($review.items | Where-Object { $_.title -eq 'Printer queue items need attention' -and $_.route -eq 'printerQueue' })
    Assert-Smoke ($round57QueueReview.Count -ge 1) 'Round 57 printer queue attention did not create an AI review finding.'
    $round57QueueEvidence = ($round57QueueReview | ForEach-Object { $_.evidence }) -join ' | '
    Assert-Smoke ($round57QueueEvidence -like '*Round57 Overdue Queue Job*' -and $round57QueueEvidence -like '*overdue*') 'Round 57 overdue printer queue item was not included in AI review evidence.'
    Assert-Smoke ($round57QueueEvidence -like '*Round57 Failed Queue Job*' -and $round57QueueEvidence -like '*2 failed attempts*') 'Round 57 failed printer queue item was not included in AI review evidence.'
    Assert-Smoke ($round57QueueEvidence -notlike '*Round57 Completed Failed Queue Job*') 'Round 57 completed printer queue item was incorrectly flagged in AI review.'
    Assert-Smoke ($round57QueueEvidence -notlike '*Round57 Cancelled Queue Job*') 'Round 57 cancelled printer queue item was incorrectly flagged in AI review.'

    $taxSummaryBeforeRound47 = Invoke-RestMethod "$base/api/tax-summary?year=2026"
    $round47PaidPurchase = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round47 Paid Purchase Vendor'
        category = 'Software'
        taxCategory = 'Other business expense'
        description = 'Round47 Paid Purchase Export Proof'
        paymentMethod = 'Credit Card'
        paymentAccount = 'Operations Card'
        amount = 123
        salesTax = 7
        total = 1
        receiptProof = 'round47-paid-purchase-receipt.pdf'
        taxBucket = 'Operating Expense'
        deductibleStatus = 'Yes'
        businessUsePercent = 80
        countedExpense = $true
        taxDeductible = $true
        needsReview = $false
        notes = 'Round 47 paid purchase tax/export smoke.'
    })
    Assert-SmokeClose $round47PaidPurchase.total 130 0.01 'Round 47 paid purchase total did not normalize.'
    Assert-Smoke ($round47PaidPurchase.paymentMethod -eq 'Credit Card') 'Round 47 paid purchase payment method did not persist.'
    Assert-Smoke ($round47PaidPurchase.paymentAccount -eq 'Operations Card') 'Round 47 paid purchase payment account did not persist.'
    Assert-Smoke ($round47PaidPurchase.receiptProof -eq 'round47-paid-purchase-receipt.pdf') 'Round 47 paid purchase receipt proof did not persist.'

    $taxSummaryAfterRound47 = Invoke-RestMethod "$base/api/tax-summary?year=2026"
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummaryAfterRound47.operatingExpenseDeductions) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound47.operatingExpenseDeductions)) 104.00 0.01 'Round 47 paid purchase did not affect operating tax deductions with business-use percent.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummaryAfterRound47.workingNetProfit) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound47.workingNetProfit)) -104.00 0.01 'Round 47 paid purchase did not affect tax-summary working net.'
    Assert-Smoke (($taxSummaryAfterRound47.missingProofOrReviewCount - $taxSummaryBeforeRound47.missingProofOrReviewCount) -eq 0) 'Round 47 paid purchase with proof should not add a proof/review gap.'

    $round47ExpensesExport = Invoke-WebRequest -Uri "$base/api/export/expenses?includeArchived=true" -UseBasicParsing
    Assert-Smoke ($round47ExpensesExport.Content -like '*Round47 Paid Purchase Export Proof*') 'Round 47 paid purchase was missing from Expenses CSV export.'
    $round47ExportRows = @($round47ExpensesExport.Content | ConvertFrom-Csv | Where-Object { $_.Description -eq 'Round47 Paid Purchase Export Proof' })
    Assert-Smoke ($round47ExportRows.Count -eq 1) "Round 47 expected one paid purchase row in Expenses CSV, got $($round47ExportRows.Count)."
    $round47ExportRow = $round47ExportRows[0]
    Assert-Smoke ($round47ExportRow.PaymentMethod -eq 'Credit Card') 'Round 47 Expenses CSV lost payment method.'
    Assert-Smoke ($round47ExportRow.PaymentAccount -eq 'Operations Card') 'Round 47 Expenses CSV lost payment account.'
    Assert-Smoke ($round47ExportRow.ReceiptProof -eq 'round47-paid-purchase-receipt.pdf') 'Round 47 Expenses CSV lost receipt proof.'
    Assert-Smoke ($round47ExportRow.TaxBucket -eq 'Operating Expense') 'Round 47 Expenses CSV lost tax bucket.'
    Assert-SmokeClose $round47ExportRow.Total 130.00 0.01 'Round 47 Expenses CSV lost normalized total.'
    Assert-SmokeClose $round47ExportRow.BusinessUsePercent 80.00 0.01 'Round 47 Expenses CSV lost business-use percent.'

    $round47TaxSummaryCsv = Invoke-WebRequest -Uri "$base/api/export/tax-summary?year=2026" -UseBasicParsing
    $round47TaxSummaryExportRow = @($round47TaxSummaryCsv.Content | ConvertFrom-Csv)[0]
    Assert-SmokeClose $round47TaxSummaryExportRow.OperatingExpenseDeductions (Get-SmokeDecimalOrZero $taxSummaryAfterRound47.operatingExpenseDeductions) 0.01 'Round 47 Tax Summary CSV did not match API operating deductions.'
    Assert-SmokeClose $round47TaxSummaryExportRow.WorkingNetProfit (Get-SmokeDecimalOrZero $taxSummaryAfterRound47.workingNetProfit) 0.01 'Round 47 Tax Summary CSV did not match API working net.'

    $taxSummaryBeforeRound51 = $taxSummaryAfterRound47
    $round51ExpensedAsset = Invoke-SmokeJson 'POST' '/api/assets' ([ordered]@{
        name = 'Round51 Section179 Printer'
        purchaseDate = '2026-06-19'
        vendorName = 'Round51 Asset Vendor'
        category = 'Equipment'
        cost = 1000
        paymentMethod = 'Credit Card'
        serialNumber = 'R51-179'
        businessUsePercent = 50
        inServiceDate = '2026-06-19'
        taxTreatment = 'Section 179'
        countedExpenseThisYear = $true
        notYetExpensed = 0
        sourceProof = 'round51-section179-printer.pdf'
        needsReview = $false
        notes = 'Round 51 asset-specific deduction smoke'
    })
    $round51DepreciationAsset = Invoke-SmokeJson 'POST' '/api/assets' ([ordered]@{
        name = 'Round51 Depreciation Printer'
        purchaseDate = '2026-06-19'
        vendorName = 'Round51 Asset Vendor'
        category = 'Equipment'
        cost = 900
        paymentMethod = 'Credit Card'
        serialNumber = 'R51-DEP'
        businessUsePercent = 100
        inServiceDate = '2026-06-19'
        taxTreatment = 'Depreciation'
        countedExpenseThisYear = $true
        notYetExpensed = 900
        sourceProof = 'round51-depreciation-printer.pdf'
        needsReview = $false
        notes = 'Round 51 depreciation asset review smoke'
    })
    $round51NonDeductibleAsset = Invoke-SmokeJson 'POST' '/api/assets' ([ordered]@{
        name = 'Round51 Personal Tool'
        purchaseDate = '2026-06-19'
        vendorName = 'Round51 Asset Vendor'
        category = 'Equipment'
        cost = 250
        paymentMethod = 'Cash'
        serialNumber = 'R51-ND'
        businessUsePercent = 100
        inServiceDate = '2026-06-19'
        taxTreatment = 'Not Deductible'
        countedExpenseThisYear = $true
        notYetExpensed = 250
        sourceProof = 'round51-personal-tool.pdf'
        needsReview = $false
        notes = 'Round 51 excluded asset smoke'
    })
    $round51AssetExpense = Invoke-SmokeJson 'POST' '/api/expenses' ([ordered]@{
        expenseDate = '2026-06-19'
        vendorName = 'Round51 Asset Expense Vendor'
        category = 'Equipment'
        taxCategory = 'Asset'
        description = 'Round51 Asset-tagged Expense'
        paymentMethod = 'Debit Card'
        paymentAccount = 'Operations Card'
        amount = 75
        salesTax = 0
        total = 75
        receiptProof = 'round51-asset-expense.pdf'
        taxBucket = 'Asset'
        deductibleStatus = 'Yes'
        businessUsePercent = 100
        countedExpense = $true
        taxDeductible = $true
        needsReview = $false
        notes = 'Round 51 asset-tagged expense excluded from ordinary deductions smoke'
    })
    $round51GiftCardIncome = Invoke-SmokeJson 'POST' '/api/makerworld-rewards' ([ordered]@{
        rewardDate = '2026-06-19'
        rewardType = 'Gift Card'
        pointsChange = 500
        giftCardAmount = 40
        codeLast4 = 'R51I'
        status = 'Available'
        incomeStatus = 'Yes - Count as income'
        sourceProof = 'round51-makerworld-income.pdf'
        needsReview = $false
        notes = 'Round 51 gift card income smoke'
    })
    $round51PointsOnlyIncome = Invoke-SmokeJson 'POST' '/api/makerworld-rewards' ([ordered]@{
        rewardDate = '2026-06-19'
        rewardType = 'Points'
        pointsChange = 1200
        giftCardAmount = 0
        codeLast4 = 'R51P'
        status = 'Available'
        incomeStatus = 'Yes - Count as income'
        sourceProof = 'round51-makerworld-points.pdf'
        needsReview = $false
        notes = 'Round 51 points-only no-dollar-income smoke'
    })
    $round51ReviewReward = Invoke-SmokeJson 'POST' '/api/makerworld-rewards' ([ordered]@{
        rewardDate = '2026-06-19'
        rewardType = 'Gift Card'
        pointsChange = 300
        giftCardAmount = 25
        codeLast4 = 'R51R'
        status = 'Available'
        incomeStatus = 'Review'
        sourceProof = 'round51-makerworld-review.pdf'
        needsReview = $false
        notes = 'Round 51 reward review smoke'
    })
    $round51NoIncomeReward = Invoke-SmokeJson 'POST' '/api/makerworld-rewards' ([ordered]@{
        rewardDate = '2026-06-19'
        rewardType = 'Gift Card'
        pointsChange = 200
        giftCardAmount = 10
        codeLast4 = 'R51N'
        status = 'Available'
        incomeStatus = 'No'
        sourceProof = 'round51-makerworld-no-income.pdf'
        needsReview = $false
        notes = 'Round 51 no-income reward smoke'
    })
    Assert-Smoke ($round51ExpensedAsset.id -gt 0 -and $round51DepreciationAsset.id -gt 0 -and $round51NonDeductibleAsset.id -gt 0 -and $round51AssetExpense.id -gt 0) 'Round 51 asset rows were not created.'
    Assert-Smoke ($round51GiftCardIncome.id -gt 0 -and $round51PointsOnlyIncome.id -gt 0 -and $round51ReviewReward.id -gt 0 -and $round51NoIncomeReward.id -gt 0) 'Round 51 MakerWorld reward rows were not created.'

    $taxSummaryAfterRound51 = Invoke-RestMethod "$base/api/tax-summary?year=2026"
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummaryAfterRound51.expensedAssets) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound51.expensedAssets)) 500.00 0.01 'Round 51 asset deduction did not use only asset-specific expensed treatment.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummaryAfterRound51.operatingExpenseDeductions) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound51.operatingExpenseDeductions)) 0.00 0.01 'Round 51 asset-tagged expense incorrectly affected ordinary operating deductions.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummaryAfterRound51.cogsMaterialExpenseDeductions) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound51.cogsMaterialExpenseDeductions)) 0.00 0.01 'Round 51 asset-tagged expense incorrectly affected COGS/material deductions.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummaryAfterRound51.makerWorldIncome) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound51.makerWorldIncome)) 40.00 0.01 'Round 51 MakerWorld income should count only gift-card dollar value marked as income.'
    Assert-SmokeClose ((Get-SmokeDecimalOrZero $taxSummaryAfterRound51.workingNetProfit) - (Get-SmokeDecimalOrZero $taxSummaryBeforeRound51.workingNetProfit)) -460.00 0.01 'Round 51 working net should reflect $40 reward income minus $500 asset deduction only.'

    $round47TaxPackage = Get-SmokeBytes '/api/export/tax-package?year=2026'
    $round47TaxPackageExpenses = Get-SmokeZipEntryText $round47TaxPackage 'expenses.csv'
    $round47TaxPackageRows = @($round47TaxPackageExpenses | ConvertFrom-Csv | Where-Object { $_.Description -eq 'Round47 Paid Purchase Export Proof' })
    Assert-Smoke ($round47TaxPackageRows.Count -eq 1) "Round 47 expected one paid purchase row in tax package expenses.csv, got $($round47TaxPackageRows.Count)."
    Assert-Smoke ($round47TaxPackageRows[0].PaymentMethod -eq 'Credit Card') 'Round 47 tax package expenses.csv lost payment method.'
    Assert-Smoke ($round47TaxPackageRows[0].PaymentAccount -eq 'Operations Card') 'Round 47 tax package expenses.csv lost payment account.'
    Assert-Smoke ($round47TaxPackageRows[0].ReceiptProof -eq 'round47-paid-purchase-receipt.pdf') 'Round 47 tax package expenses.csv lost receipt proof.'
    $round51TaxPackageAssets = Get-SmokeZipEntryText $round47TaxPackage 'assets.csv'
    $round51TaxPackageRewards = Get-SmokeZipEntryText $round47TaxPackage 'makerworld-rewards.csv'
    Assert-Smoke ($round51TaxPackageAssets -like '*Round51 Section179 Printer*' -and $round51TaxPackageAssets -like '*Round51 Depreciation Printer*' -and $round51TaxPackageAssets -like '*Round51 Personal Tool*') 'Round 51 tax package assets.csv did not include asset-specific rows.'
    Assert-Smoke ($round51TaxPackageRewards -like '*R51I*' -and $round51TaxPackageRewards -like '*R51P*' -and $round51TaxPackageRewards -like '*R51R*' -and $round51TaxPackageRewards -like '*Yes - Count as income*' -and $round51TaxPackageRewards -like '*Review*') 'Round 51 tax package makerworld-rewards.csv did not include reward income status rows.'

    $automationPreview = Invoke-RestMethod "$base/api/action-items/automation-preview"
    Assert-Smoke ($automationPreview) 'Action item automation preview returned an empty response.'
    $round54CommunicationAutomation = @($automationPreview.candidates | Where-Object { $_.title -eq 'Customer communication follow-ups are due' -and $_.route -eq 'communications' })
    Assert-Smoke ($round54CommunicationAutomation.Count -eq 1) 'Round 54 communication follow-up did not appear in action-item automation preview.'
    Assert-Smoke ($round54CommunicationAutomation[0].relatedRecord -like '*Round54 Communication Customer*') 'Round 54 communication automation candidate did not carry communication evidence.'

    $appInfo = Invoke-RestMethod "$base/api/app-info"
    Assert-Smoke ($appInfo.dbPath -like '*local-smoke.db') "App info did not report the disposable database. Reported: $($appInfo.dbPath)"
    Assert-Smoke (-not [string]::IsNullOrWhiteSpace([string]$appInfo.environment)) 'Round 78 App info did not report environment.'
    $appInfoJson = $appInfo | ConvertTo-Json -Depth 20
    Assert-Smoke ($appInfoJson -notlike "*$round26SecretSentinel*") 'App info API exposed the Round 26 secret sentinel.'
    Assert-SmokeNoSecretMaterial 'App info API' $appInfoJson

    foreach ($exportPath in @(
        '/api/export/parties?includeArchived=true',
        '/api/export/sales?includeArchived=true',
        '/api/export/receivable-invoices?includeArchived=true',
        '/api/export/expenses?includeArchived=true',
        '/api/export/audit-documents?includeArchived=true',
        '/api/export/business-accounts?includeArchived=true',
        '/api/export/tax-sales?paymentGroup=all&year=2026',
        '/api/export/tax-summary?year=2026',
        '/api/export/nj-sales-tax?year=2026'
    )) {
        $exportResponse = Invoke-WebRequest -Uri "$base$exportPath" -UseBasicParsing
        Assert-Smoke ($exportResponse.StatusCode -eq 200) "Round 26 export $exportPath returned $($exportResponse.StatusCode)."
        Assert-Smoke ($exportResponse.Content -notlike "*$round26SecretSentinel*") "Round 26 export $exportPath exposed the secret sentinel."
        Assert-SmokeNoSecretMaterial "Round 26 export $exportPath" $exportResponse.Content
    }
    Assert-SmokeCsvHeaders '/api/export/sales?includeArchived=true' @('Id', 'SaleDate', 'Platform', 'PaymentMethod', 'SalesTaxHandling', 'CustomerName', 'CustomerPaid', 'NeedsReview')
    Assert-SmokeCsvHeaders '/api/export/expenses?includeArchived=true' @('Id', 'ExpenseDate', 'VendorName', 'PaymentMethod', 'TaxBucket', 'BusinessUsePercent', 'NeedsReview')
    Assert-SmokeCsvHeaders '/api/export/receivable-invoices?includeArchived=true' @('Id', 'InvoiceNumber', 'InvoiceDate', 'CustomerName', 'Status', 'PaymentMethod', 'InvoiceTotal', 'AmountPaid')
    Assert-SmokeCsvHeaders '/api/export/tax-obligations?includeArchived=true' @('Id', 'TaxYear', 'Title', 'Jurisdiction', 'ObligationType', 'PaymentMethod', 'NeedsReview')
    Assert-SmokeCsvHeaders '/api/export/mileage-logs?includeArchived=true' @('Id', 'TripDate', 'BusinessPurpose', 'BusinessMiles', 'ParkingAndTolls', 'NeedsReview')
    Assert-SmokeCsvHeaders '/api/export/tax-summary?year=2027' @('TaxYear', 'BusinessMiles', 'MileageRate', 'MileageDeductionEstimate', 'MissingProofOrReviewCount')

    $settingsExport = Invoke-WebRequest -Uri "$base/api/export/settings" -UseBasicParsing -SkipHttpErrorCheck
    Assert-Smoke ($settingsExport.StatusCode -eq 404) "Settings export should not exist; got $($settingsExport.StatusCode)."
    Assert-Smoke ($settingsExport.Content -notlike "*$round26SecretSentinel*") 'Settings export response exposed the Round 26 secret sentinel.'
    Assert-SmokeNoSecretMaterial 'Settings export response' $settingsExport.Content

    Assert-Smoke $backupCopyPreflightCompleted 'Round 64 backup preflight did not run before export/file-boundary checks.'

    $outsideDoc = Invoke-SmokeJson 'POST' '/api/audit-documents' ([ordered]@{
        documentDate = '2026-06-19'
        documentType = 'Other'
        fileName = 'outside-file.txt'
        filePathOrUrl = 'C:\Windows\win.ini'
        notes = 'Round 3 outside file boundary smoke'
    })
    $outsideFile = Invoke-WebRequest -Uri "$base/api/audit-documents/$($outsideDoc.id)/file" -UseBasicParsing -SkipHttpErrorCheck
    Assert-Smoke ($outsideFile.StatusCode -eq 400) "Outside audit file expected 400, got $($outsideFile.StatusCode)."

    $round61ClassificationUpload = Invoke-SmokeProofTextUpload @(
        [pscustomobject]@{ FileName = 'round61 etsy sale order.txt'; Text = 'Etsy order receipt. Order #R61-SALE-001. Buyer paid. Customer paid. Order total $42.50. Tracking 9400111899000000000001.' },
        [pscustomobject]@{ FileName = 'round61 supply receipt.txt'; Text = 'Receipt for paid purchase from Office Depot. Subtotal $18.25 total $18.25 charged to business card.' },
        [pscustomobject]@{ FileName = 'round61 bambu printer asset.txt'; Text = 'Bambu P1S printer equipment purchase receipt. Serial BP1S-R61. Total $699.00. Durable business property.' },
        [pscustomobject]@{ FileName = 'round61 customer invoice.txt'; Text = 'Customer invoice INV-2026-R61. Amount due $120.00. Payment due on receipt for custom display stand.' },
        [pscustomobject]@{ FileName = 'round61 customer estimate.txt'; Text = 'Estimate EST-2026-R61 quote proposal valid until 2026-07-01. Total $250.00 for custom bracket.' },
        [pscustomobject]@{ FileName = 'round61 vendor bill.txt'; Text = 'Vendor bill from Filament Supplier. Payment terms net 30. Unpaid supplier statement. Amount due $88.00.' },
        [pscustomobject]@{ FileName = 'round61 usps shipping label.txt'; Text = 'USPS shipping label postage. Tracking 9400111899000000000002. Ship by tomorrow. Total $8.40.' },
        [pscustomobject]@{ FileName = 'round61 ambiguous note.txt'; Text = 'Studio note: remember to review the loose document and decide where it belongs later.' }
    )
    Assert-Smoke ($round61ClassificationUpload.count -eq 8) 'Round 61 classification upload did not create all Audit Docs.'
    foreach ($doc in @($round61ClassificationUpload.documents)) {
        $null = Assert-SmokeUploadedDocUnderRoot $doc "Round 62 classified upload $($doc.fileName)"
    }
    $round61SuggestionsByFile = @{}
    foreach ($suggestion in @($round61ClassificationUpload.suggestions)) {
        $round61SuggestionsByFile[$suggestion.fileName] = $suggestion
    }
    Assert-Smoke ($round61SuggestionsByFile['round61 etsy sale order.txt'].lane -eq 'Sale' -and $round61SuggestionsByFile['round61 etsy sale order.txt'].suggestedRoute -eq 'sales' -and $round61SuggestionsByFile['round61 etsy sale order.txt'].suggestedKind -eq 'etsy') 'Round 61 sale proof did not classify as an Etsy Sale suggestion.'
    Assert-Smoke ($round61SuggestionsByFile['round61 supply receipt.txt'].lane -like 'Expense*' -and $round61SuggestionsByFile['round61 supply receipt.txt'].suggestedConfig -eq 'expenses') 'Round 61 receipt proof did not classify as an Expense suggestion.'
    Assert-Smoke ($round61SuggestionsByFile['round61 bambu printer asset.txt'].lane -eq 'Asset' -and $round61SuggestionsByFile['round61 bambu printer asset.txt'].suggestedConfig -eq 'assets' -and $round61SuggestionsByFile['round61 bambu printer asset.txt'].suggestedKind -eq 'assetPurchase') 'Round 61 equipment proof did not classify as an Asset suggestion.'
    Assert-Smoke ($round61SuggestionsByFile['round61 customer invoice.txt'].lane -eq 'Invoice' -and $round61SuggestionsByFile['round61 customer invoice.txt'].suggestedRoute -eq 'invoices') 'Round 61 invoice proof did not classify as an Invoice suggestion.'
    Assert-Smoke ($round61SuggestionsByFile['round61 customer estimate.txt'].lane -eq 'Estimate' -and $round61SuggestionsByFile['round61 customer estimate.txt'].suggestedRoute -eq 'estimates') 'Round 61 estimate proof did not classify as an Estimate suggestion.'
    Assert-Smoke ($round61SuggestionsByFile['round61 vendor bill.txt'].lane -eq 'Bill / AP' -and $round61SuggestionsByFile['round61 vendor bill.txt'].suggestedConfig -eq 'bills') 'Round 61 vendor bill proof did not classify as a Bill/AP suggestion.'
    Assert-Smoke ($round61SuggestionsByFile['round61 usps shipping label.txt'].lane -eq 'Shipping / Sale Cost' -and $round61SuggestionsByFile['round61 usps shipping label.txt'].suggestedConfig -eq 'sales') 'Round 61 shipping proof did not classify as a Shipping/Sale Cost suggestion.'
    Assert-Smoke ($round61SuggestionsByFile['round61 ambiguous note.txt'].lane -eq 'Review' -and $round61SuggestionsByFile['round61 ambiguous note.txt'].suggestedRoute -eq 'documentIntake') 'Round 61 ambiguous proof did not classify as a human Review suggestion.'

    $round58DocsBefore = @(Get-SmokeJsonArray '/api/audit-documents').Count
    $round58Client = [System.Net.Http.HttpClient]::new()
    $round58MissingForm = [System.Net.Http.MultipartFormDataContent]::new()
    $round58MissingForm.Add([System.Net.Http.StringContent]::new('Round 58 missing file edge'), 'relatedType')
    $round58MissingResponse = $round58Client.PostAsync("$base/api/documents/upload", $round58MissingForm).GetAwaiter().GetResult()
    $round58MissingText = $round58MissingResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-Smoke ([int]$round58MissingResponse.StatusCode -eq 400) "Round 58 missing proof upload expected 400, got $([int]$round58MissingResponse.StatusCode)."
    Assert-Smoke ($round58MissingText -like '*Choose at least one file*') 'Round 58 missing proof upload did not explain that a file is required.'
    $round58MissingForm.Dispose()

    $round58ZeroForm = [System.Net.Http.MultipartFormDataContent]::new()
    $round58ZeroContent = [System.Net.Http.ByteArrayContent]::new([byte[]]::new(0))
    $round58ZeroContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/plain')
    $round58ZeroForm.Add($round58ZeroContent, 'files', 'round58-empty.txt')
    $round58ZeroResponse = $round58Client.PostAsync("$base/api/documents/upload", $round58ZeroForm).GetAwaiter().GetResult()
    $round58ZeroText = $round58ZeroResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-Smoke ([int]$round58ZeroResponse.StatusCode -eq 400) "Round 58 zero-byte proof upload expected 400, got $([int]$round58ZeroResponse.StatusCode)."
    Assert-Smoke ($round58ZeroText -like '*round58-empty.txt*empty*') 'Round 58 zero-byte proof upload did not explain the empty file.'
    $round58ZeroForm.Dispose()
    $round58ZeroContent.Dispose()

    $round58UnsupportedForm = [System.Net.Http.MultipartFormDataContent]::new()
    $round58UnsupportedContent = [System.Net.Http.ByteArrayContent]::new([System.Text.Encoding]::UTF8.GetBytes('Round 58 unsupported proof upload'))
    $round58UnsupportedContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/octet-stream')
    $round58UnsupportedForm.Add($round58UnsupportedContent, 'files', 'round58-unsupported.exe')
    $round58UnsupportedResponse = $round58Client.PostAsync("$base/api/documents/upload", $round58UnsupportedForm).GetAwaiter().GetResult()
    $round58UnsupportedText = $round58UnsupportedResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-Smoke ([int]$round58UnsupportedResponse.StatusCode -eq 400) "Round 58 unsupported proof upload expected 400, got $([int]$round58UnsupportedResponse.StatusCode)."
    Assert-Smoke ($round58UnsupportedText -like '*round58-unsupported.exe*not a supported proof upload type*') 'Round 58 unsupported proof upload did not explain the supported file types.'
    $round58UnsupportedForm.Dispose()
    $round58UnsupportedContent.Dispose()

    $round58OversizedForm = [System.Net.Http.MultipartFormDataContent]::new()
    $round58OversizedBytes = [byte[]]::new((20 * 1024 * 1024) + 1)
    $round58OversizedContent = [System.Net.Http.ByteArrayContent]::new($round58OversizedBytes)
    $round58OversizedContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('application/pdf')
    $round58OversizedForm.Add($round58OversizedContent, 'files', 'round58-oversized.pdf')
    $round58OversizedResponse = $round58Client.PostAsync("$base/api/documents/upload", $round58OversizedForm).GetAwaiter().GetResult()
    $round58OversizedText = $round58OversizedResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-Smoke ([int]$round58OversizedResponse.StatusCode -eq 400) "Round 58 oversized proof upload expected 400, got $([int]$round58OversizedResponse.StatusCode)."
    Assert-Smoke ($round58OversizedText -like '*round58-oversized.pdf*20 MB per-file proof upload limit*') 'Round 58 oversized proof upload did not explain the proof upload limit.'
    $round58OversizedForm.Dispose()
    $round58OversizedContent.Dispose()
    $round58Client.Dispose()

    $round58DocsAfter = @(Get-SmokeJsonArray '/api/audit-documents').Count
    Assert-Smoke ($round58DocsAfter -eq $round58DocsBefore) 'Round 58 rejected proof uploads created Audit Document rows.'
    $round58UploadRoot = Join-Path $root 'UploadedDocs'
    $round58RejectedFiles = if (Test-Path -LiteralPath $round58UploadRoot) {
        @(Get-ChildItem -LiteralPath $round58UploadRoot -Filter '*round58-*' -ErrorAction SilentlyContinue)
    } else {
        @()
    }
    Assert-Smoke ($round58RejectedFiles.Count -eq 0) "Round 58 rejected proof uploads wrote files: $($round58RejectedFiles.Name -join ', ')"

    $uploadClient = [System.Net.Http.HttpClient]::new()
    $uploadForm = [System.Net.Http.MultipartFormDataContent]::new()
    $uploadBytes = [System.Text.Encoding]::UTF8.GetBytes('Round 15 upload path traversal smoke')
    $uploadContent = [System.Net.Http.ByteArrayContent]::new($uploadBytes)
    $uploadContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/plain')
    $uploadForm.Add($uploadContent, 'files', '..\..\round15-escape.txt')
    $uploadResponse = $uploadClient.PostAsync("$base/api/documents/upload", $uploadForm).GetAwaiter().GetResult()
    $uploadJsonText = $uploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-Smoke ($uploadResponse.IsSuccessStatusCode) "Traversal filename upload expected success with safe storage, got $([int]$uploadResponse.StatusCode): $uploadJsonText"
    $uploadResult = $uploadJsonText | ConvertFrom-Json
    Assert-Smoke ($uploadResult.count -eq 1) 'Traversal filename upload did not create exactly one audit document.'
    $uploadedDoc = @($uploadResult.documents)[0]
    $uploadedFullPath = Assert-SmokeUploadedDocUnderRoot $uploadedDoc 'Round 62 traversal upload'
    $expectedUploadRoot = [System.IO.Path]::GetFullPath((Join-Path $root 'UploadedDocs'))
    $expectedUploadRootWithSep = if ($expectedUploadRoot.EndsWith([System.IO.Path]::DirectorySeparatorChar)) {
        $expectedUploadRoot
    } else {
        $expectedUploadRoot + [System.IO.Path]::DirectorySeparatorChar
    }
    Assert-Smoke ($uploadedFullPath.StartsWith($expectedUploadRootWithSep, [System.StringComparison]::OrdinalIgnoreCase)) "Traversal filename escaped UploadedDocs: $uploadedFullPath"
    Assert-Smoke ((Split-Path -Leaf $uploadedFullPath) -like '*round15-escape.txt') 'Traversal filename was not reduced to a safe file name.'
    Assert-Smoke (-not ((Split-Path -Leaf $uploadedFullPath) -match '\.\.')) 'Stored traversal filename still contains path traversal segments.'
    Assert-Smoke (Test-Path -LiteralPath $uploadedFullPath) 'Uploaded traversal smoke file was not saved.'
    $uploadedFile = Invoke-WebRequest -Uri "$base/api/audit-documents/$($uploadedDoc.id)/file" -UseBasicParsing
    Assert-Smoke ($uploadedFile.StatusCode -eq 200) "Uploaded traversal smoke file could not be read back safely; got $($uploadedFile.StatusCode)."
    Assert-Smoke ($uploadedFile.Content -like '*Round 15 upload path traversal smoke*') 'Uploaded traversal smoke file content did not round-trip.'

    $round60AuditEditPayload = [ordered]@{}
    foreach ($property in $uploadedDoc.PSObject.Properties) {
        $round60AuditEditPayload[$property.Name] = $property.Value
    }
    $round60AuditEditPayload['documentType'] = 'Invoice'
    $round60AuditEditPayload['relatedRecordType'] = 'Invoice'
    $round60AuditEditPayload['relatedRecordNumber'] = 'INV-R60-AUDIT'
    $round60AuditEditPayload['needsReview'] = $false
    $round60AuditEditPayload['notes'] = 'Round 60 edited from the Document Intake Edit Audit Doc flow.'
    $round60EditedAuditDoc = Invoke-SmokeJson 'PUT' "/api/audit-documents/$($uploadedDoc.id)" $round60AuditEditPayload
    Assert-Smoke ($round60EditedAuditDoc.documentType -eq 'Invoice') 'Round 60 Edit Audit Doc flow did not persist document type.'
    Assert-Smoke ($round60EditedAuditDoc.relatedRecordType -eq 'Invoice' -and $round60EditedAuditDoc.relatedRecordNumber -eq 'INV-R60-AUDIT') 'Round 60 Edit Audit Doc flow did not persist related record fields.'
    Assert-Smoke ($round60EditedAuditDoc.needsReview -eq $false) 'Round 60 Edit Audit Doc flow did not clear Needs Review.'
    $round60ReloadedAuditDoc = Invoke-RestMethod "$base/api/audit-documents/$($uploadedDoc.id)"
    Assert-Smoke ($round60ReloadedAuditDoc.notes -eq 'Round 60 edited from the Document Intake Edit Audit Doc flow.') 'Round 60 edited Audit Doc notes did not reload from the disposable API.'

    $round59SpecialFileName = "round59 Bob's order (final proof).txt"
    $round59SpecialForm = [System.Net.Http.MultipartFormDataContent]::new()
    $round59SpecialBytes = [System.Text.Encoding]::UTF8.GetBytes('Round 59 special proof filename smoke')
    $round59SpecialContent = [System.Net.Http.ByteArrayContent]::new($round59SpecialBytes)
    $round59SpecialContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('text/plain')
    $round59SpecialForm.Add($round59SpecialContent, 'files', $round59SpecialFileName)
    $round59SpecialResponse = $uploadClient.PostAsync("$base/api/documents/upload", $round59SpecialForm).GetAwaiter().GetResult()
    $round59SpecialJsonText = $round59SpecialResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    Assert-Smoke ($round59SpecialResponse.IsSuccessStatusCode) "Round 59 special proof filename upload expected success, got $([int]$round59SpecialResponse.StatusCode): $round59SpecialJsonText"
    $round59SpecialResult = $round59SpecialJsonText | ConvertFrom-Json
    Assert-Smoke ($round59SpecialResult.count -eq 1) 'Round 59 special proof filename upload did not create exactly one Audit Document.'
    $round59SpecialDoc = @($round59SpecialResult.documents)[0]
    Assert-Smoke ($round59SpecialDoc.fileName -eq $round59SpecialFileName) 'Round 59 special proof filename was not preserved on the Audit Document.'
    $round59SpecialFullPath = Assert-SmokeUploadedDocUnderRoot $round59SpecialDoc 'Round 62 special proof upload'
    $round59SpecialLeaf = Split-Path -Leaf $round59SpecialFullPath
    Assert-Smoke ($round59SpecialLeaf -like "*$round59SpecialFileName") "Round 59 stored filename did not retain spaces, parentheses, and apostrophe. Got: $round59SpecialLeaf"
    Assert-Smoke (Test-Path -LiteralPath $round59SpecialFullPath) 'Round 59 special proof file was not saved.'
    $round59SpecialFile = Invoke-WebRequest -Uri "$base/api/audit-documents/$($round59SpecialDoc.id)/file" -UseBasicParsing
    Assert-Smoke ($round59SpecialFile.StatusCode -eq 200) "Round 59 special proof file could not be read back; got $($round59SpecialFile.StatusCode)."
    Assert-Smoke ($round59SpecialFile.Content -like '*Round 59 special proof filename smoke*') 'Round 59 special proof file content did not round-trip.'
    $round59SpecialForm.Dispose()
    $round59SpecialContent.Dispose()

    $uploadClient.Dispose()
    $uploadForm.Dispose()
    $uploadContent.Dispose()

    $restoreDocumentCount = @(Get-SmokeJsonArray '/api/documents?includeArchived=true').Count
    $restoreAuditDocCount = @(Get-SmokeJsonArray '/api/audit-documents?includeArchived=true').Count
    $restoreConfigBefore = Invoke-RestMethod "$base/api/config"
    Assert-Smoke ($restoreDocumentCount -gt 0) 'Round 65 restore drill did not have document records to verify.'
    Assert-Smoke ($restoreAuditDocCount -ge 2) 'Round 65 restore drill did not have uploaded Audit Docs to verify.'
    $restoreBackup = Invoke-WebRequest -Method POST -Uri "$base/api/system/backup" -UseBasicParsing -OutFile $backupDownloadPath -PassThru
    Assert-Smoke ($restoreBackup.StatusCode -eq 200) "Round 65 restore-drill backup returned $($restoreBackup.StatusCode)."
    Assert-Smoke (Test-Path -LiteralPath $backupDownloadPath) 'Round 65 restore-drill backup download was not created.'
    $restoreBackupSignature = [System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($backupDownloadPath), 0, 15)
    Assert-Smoke ($restoreBackupSignature -eq 'SQLite format 3') "Round 65 restore-drill backup was not a SQLite database. Signature: $restoreBackupSignature"

    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
        $server = $null
        Start-Sleep -Milliseconds 750
    }
    foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal")) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
    Copy-Item -LiteralPath $backupDownloadPath -Destination $dbPath -Force
    Assert-Smoke (Test-Path -LiteralPath $dbPath) 'Round 65 restore drill did not copy the backup to the disposable database path.'
    $restoreBackupHash = (Get-FileHash -LiteralPath $backupDownloadPath -Algorithm SHA256).Hash
    $restoredDbHash = (Get-FileHash -LiteralPath $dbPath -Algorithm SHA256).Hash
    Assert-Smoke ($restoredDbHash -eq $restoreBackupHash) 'Round 65 restored disposable database does not match the backup file.'

    $server = Start-Process -FilePath 'dotnet' `
        -ArgumentList $args `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outPath `
        -RedirectStandardError $errPath `
        -PassThru
    $restartHealth = Wait-SmokeHealth
    Assert-Smoke ($restartHealth.database -like '*local-smoke.db') "Restarted smoke app is not using the disposable database. Reported: $($restartHealth.database)"
    $persistedDocument = Invoke-RestMethod "$base/api/documents/$($paidDocument.id)"
    Assert-Smoke ($persistedDocument.docNumber -eq 'INV-2026-0200') 'Saved smoke invoice did not persist across restart.'
    $persistedConfig = Invoke-RestMethod "$base/api/config"
    Assert-Smoke ($persistedConfig.businessName -eq 'Round 16 Fab Lab') 'Saved settings did not persist across restart.'
    Assert-Smoke ([math]::Abs([decimal]$persistedConfig.calcMinimum - 28) -lt 0.0001) 'Saved calculator defaults did not persist across restart.'
    Assert-Smoke ($persistedConfig.businessName -eq $restoreConfigBefore.businessName) 'Round 65 restored settings do not match the backed-up settings.'
    $restoredDocumentCount = @(Get-SmokeJsonArray '/api/documents?includeArchived=true').Count
    $restoredAuditDocCount = @(Get-SmokeJsonArray '/api/audit-documents?includeArchived=true').Count
    Assert-Smoke ($restoredDocumentCount -eq $restoreDocumentCount) "Round 65 restored document count changed. Expected $restoreDocumentCount, got $restoredDocumentCount."
    Assert-Smoke ($restoredAuditDocCount -eq $restoreAuditDocCount) "Round 65 restored Audit Doc count changed. Expected $restoreAuditDocCount, got $restoredAuditDocCount."
    $restoredUploadedDoc = Invoke-RestMethod "$base/api/audit-documents/$($uploadedDoc.id)"
    Assert-Smoke ($restoredUploadedDoc.fileName -eq $uploadedDoc.fileName) 'Round 65 restored upload metadata did not match the backed-up traversal upload.'
    $restoredUploadedFile = Invoke-WebRequest -Uri "$base/api/audit-documents/$($uploadedDoc.id)/file" -UseBasicParsing
    Assert-Smoke ($restoredUploadedFile.Content -like '*Round 15 upload path traversal smoke*') 'Round 65 restored upload file content did not read back after restore.'
    $restoredSpecialDoc = Invoke-RestMethod "$base/api/audit-documents/$($round59SpecialDoc.id)"
    Assert-Smoke ($restoredSpecialDoc.fileName -eq $round59SpecialFileName) 'Round 65 restored special-filename upload metadata did not match.'

    $smokeSucceeded = $true
    [pscustomobject]@{
        Health = $health.status
        Database = $health.database
        StaticAssets = 'pass'
        MainShellNav = 'pass'
        InvoiceBuilderControls = 'pass'
        WrongPrefixRejected = $wrongPrefix.StatusCode
        DuplicateRejected = $duplicate.StatusCode
        Latest = $latest.docNumber
        StatsEstimates = $stats.totalEstimates
        BlankImportRejected = $blankImport.StatusCode
        WrongFormatNoWrite = 'pass'
        DuplicateImportWarned = 'pass'
        ImportedPaidInvoiceSync = 'pass'
        InvoiceWorkflowRound4 = 'pass'
        InvoiceWorkflowRound5 = 'pass'
        InvoiceWorkflowRound6 = 'pass'
        InvoiceWorkflowRound9 = 'pass'
        TypeStatusSaveWorkflowRound66 = 'pass'
        LineItemAddRemoveRound67 = 'pass'
        LivePreviewRefreshRound68 = 'pass'
        PreviewButtonRound69 = 'pass'
        DownloadPdfRound70 = 'pass'
        SaveToastDedupeUserReport = 'pass'
        PdfImportFilePickerRound71 = 'pass'
        PdfImportMultiPageRound72 = 'pass'
        PdfImportMissingContactRound72 = 'pass'
        PdfImportPrivateNotesRound72 = 'pass'
        PdfImportOversizeRound72 = 'pass'
        PdfImportOldWordingRound86 = 'pass'
        ActiveRecordBarRound74 = 'pass'
        FailedSaveStatusRound74 = 'pass'
        ProductDatalistRound74 = 'pass'
        LongAddressPdfRound74 = 'pass'
        InternalNotesPdfRound74 = 'pass'
        PdfManyItemsRound75 = 'pass'
        PdfLongTextRound75 = 'pass'
        PdfPageSizesRound75 = 'pass'
        PdfLongTermsRound75 = 'pass'
        PdfPreviewPrintMatchRound75 = 'pass'
        CalculatorSettingsFutureRound81 = 'pass'
        CalculatorRoundingRound81 = 'pass'
        RateCardCopyRound81 = 'pass'
        RateCardDefaultsRound81 = 'pass'
        InvalidConfigRound82 = 'pass'
        LongContentLayoutRound83 = 'pass'
        InvoiceFieldsRound10 = 'pass'
        CalculatorRound11 = 'pass'
        DocumentSessionRound12 = 'pass'
        ValidationRound13 = 'pass'
        AccountingRound14 = 'pass'
        PaymentMethodRound27 = 'pass'
        DashboardExclusionsRound21 = 'pass'
        CsvArchiveRound22 = 'pass'
        TestDatabaseGuardRound24 = 'pass'
        HtmlEscapeRound25 = 'pass'
        SecretExposureRound26 = 'pass'
        DocumentIntakeClassificationRound61 = 'pass'
        ProofStorageBoundaryRound62 = 'pass'
        ProofUploadEdgesRound58 = 'pass'
        ProofSpecialFilenamesRound59 = 'pass'
        AuditDocEditRound60 = 'pass'
        ToastStackRound28 = 'pass'
        ToastActionsRound29 = 'pass'
        FilePickerRound73 = 'pass'
        EmbeddedInvoiceShellRound73 = 'pass'
        InvoiceTabsRound73 = 'pass'
        InvoiceStateSwitchRound73 = 'pass'
        InvoiceKeyboardShortcutsRound73 = 'pass'
        ModalLifecycleRound30 = 'pass'
        ModalFailedSaveRound31 = 'pass'
        RapidSubmitRound102 = 'pass'
        ScreenReaderLabelsRound103 = 'pass'
        KeyboardReachabilityRound110 = 'pass'
        ModalFocusTrapRound110 = 'pass'
        FocusVisibleRound110 = 'pass'
        InvoiceBuilderKeyboardRound110 = 'pass'
        OfflineLocalBoundaryRound104 = 'pass'
        EmptyStatesRound105 = 'pass'
        LoadingStatesRound105 = 'pass'
        ResponsiveLayoutRound108 = 'pass'
        NoClipSourceRound108 = 'pass'
        DashboardResizeRound108 = 'pass'
        PrinterBoardManyCardsRound108 = 'pass'
        EntitySearchRound32 = 'pass'
        EntityTableActionsRound32 = 'pass'
        NeedsReviewFilterRound39 = 'pass'
        TextProofSearchRound39 = 'pass'
        BillTaxReviewRound45 = 'pass'
        BillDueUrgencyRound46 = 'pass'
        PrinterQueueAiReviewRound57 = 'pass'
        PaidPurchaseExportRound47 = 'pass'
        ProductNegativeClampRound48 = 'pass'
        ProductImportDraftRound49 = 'pass'
        MarketplaceMissingDateRound76 = 'pass'
        ProductImportUnsavedRound76 = 'pass'
        JobPlannerIdempotentRound76 = 'pass'
        SlicerReaderRound76 = 'pass'
        ListingWriterRound76 = 'pass'
        AskLedgerOpenActionsRound76 = 'pass'
        AiEstimateKnownProductRound77 = 'pass'
        AiEstimateBulkQuantityRound77 = 'pass'
        AiEstimateBuilderPushRound77 = 'pass'
        AiEstimateReviewFieldsRound77 = 'pass'
        AiEstimateLargeTextRound77 = 'pass'
        TaxObligationsRound78 = 'pass'
        MileageTaxDeductionRound78 = 'pass'
        TaxYearSwitchRound78 = 'pass'
        TaxNeedsReviewRound78 = 'pass'
        CsvExcelHeadersRound78 = 'pass'
        AdminInfoRound78 = 'pass'
        AdminBackupUiRound78 = 'pass'
        LedgerMapLanesRound79 = 'pass'
        LedgerMapAskRound79 = 'pass'
        WorkflowGuideRound79 = 'pass'
        HelpGlossaryRound79 = 'pass'
        LedgerHelpMobileRound79 = 'pass'
        ModelBackedClearRound80 = 'pass'
        AiReviewFindingsRound80 = 'pass'
        AiReviewOpenRoutesRound80 = 'pass'
        AiReviewEmptyRound80 = 'pass'
        LocalAiBoundariesRound80 = 'pass'
        LocalAiStartStopRound85 = 'pass'
        ProductCatalogSearchRound50 = 'pass'
        AssetNormalizationRound50 = 'pass'
        AssetTaxPrepRound51 = 'pass'
        RewardIncomeRound51 = 'pass'
        RewardNoDoubleCountRound51 = 'pass'
        CommunicationDisplayRound54 = 'pass'
        CommunicationFollowUpRound54 = 'pass'
        CommunicationLongSummaryRound54 = 'pass'
        BusinessAccountStatusRound52 = 'pass'
        BusinessAccountBalanceIsolationRound52 = 'pass'
        JobTimelineOpenRound53 = 'pass'
        JobQueuePrefillRound53 = 'pass'
        SlowMachineSimulationRound109 = 'pass'
        SaleStatusRound42 = 'pass'
        SidebarNavRound33 = 'pass'
        SidebarPreferenceRound33 = 'pass'
        BrowserHistoryRound34 = 'pass'
        GlobalSearchRound35 = 'pass'
        QuickAddRound36 = 'pass'
        QuickAddCreatesRound37 = 'pass'
        QuickAddSavedRound38 = 'pass'
        QuickAddDoubleClickRound84 = 'pass'
        ArPdfPrefillRound43 = 'pass'
        ExternalArSeparationRound44 = 'pass'
        IncludeInDashboardRound41 = 'pass'
        RelationshipsRound17 = 'pass'
        RelationshipOpenRound40 = 'pass'
        RelationshipDetailRound55 = 'pass'
        RelationshipContactRound55 = 'pass'
        RelationshipPaginationRound55 = 'pass'
        RelationshipDuplicateNamesRound56 = 'pass'
        PdfRound18 = 'pass'
        ArchivedDuplicateRound19 = 'pass'
        PdfImportRound20 = 'pass'
        RecordsViewRound7 = 'pass'
        RecordsViewRound8 = 'pass'
        SiteCrudRoutes = $crudIds.Count
        CsvExports = 'pass'
        AppInfo = 'pass'
        SystemBackup = 'pass'
        DashboardBreakdownRound101 = 'pass'
        DashboardZeroBreakdownRound101 = 'pass'
        DashboardChartsRound101 = 'pass'
        BackupCopyRehearsalRound63 = 'pass'
        BackupPreflightRound64 = 'pass'
        BackupRestoreDrillRound65 = 'pass'
        FileBoundaryRejected = $outsideFile.StatusCode
        UploadPathRound15 = 'pass'
        SettingsRound16 = 'pass'
        TaxAndReviewReads = 'pass'
        Lookups = 'pass'
        JobTimeline = 'pass'
        RestartPersistence = 'pass'
    } | ConvertTo-Json -Compress
}
finally {
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force
    }
    $backupDir = Join-Path $root 'Backups'
    if (Test-Path -LiteralPath $backupDir) {
        $before = @{}
        foreach ($path in $backupFilesBefore) {
            $before[$path] = $true
        }
        Get-ChildItem -LiteralPath $backupDir -Filter 'epata-business-ledger-*.db' -File -ErrorAction SilentlyContinue |
            Where-Object { -not $before.ContainsKey($_.FullName) } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
    $uploadedDocsDir = Join-Path $root 'UploadedDocs'
    if (Test-Path -LiteralPath $uploadedDocsDir) {
        $beforeUploads = @{}
        foreach ($path in $uploadedDocsBefore) {
            $beforeUploads[$path] = $true
        }
        Get-ChildItem -LiteralPath $uploadedDocsDir -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { -not $beforeUploads.ContainsKey($_.FullName) } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Milliseconds 500
    if ($smokeSucceeded -or -not $KeepFailureArtifacts) {
        foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $backupDownloadPath, "$backupDownloadPath-shm", "$backupDownloadPath-wal", $manualCopyPath, "$manualCopyPath-shm", "$manualCopyPath-wal", $outPath, $errPath)) {
            Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
        }
    }
}
