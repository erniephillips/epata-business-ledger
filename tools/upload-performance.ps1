param(
    [int]$Port = 5231,
    [int]$MaxUploadMilliseconds = 5000
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$base = "http://127.0.0.1:$Port"
$dbPath = Join-Path $root 'Data\upload-performance.db'
$dllPath = Join-Path $root 'obj\verify-build\EPATA.BusinessLedger.dll'
$outPath = Join-Path $root 'upload-performance.out.log'
$errPath = Join-Path $root 'upload-performance.err.log'
$uploadedDocsDir = Join-Path $root 'UploadedDocs'
$server = $null
$uploadedDocsBefore = @()

function Assert-UploadPerf($condition, [string]$message) {
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

function Wait-UploadPerfHealth {
    $lastError = $null
    foreach ($attempt in 1..60) {
        try {
            $health = Invoke-RestMethod "$base/api/health"
            Assert-UploadPerf ($health.database -like '*upload-performance.db') "Upload performance app used wrong database: $($health.database)"
            return $health
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }

    $stderr = Get-Content -Raw $errPath -ErrorAction SilentlyContinue
    throw "Upload performance app did not become healthy. Last error: $lastError. stderr: $stderr"
}

function New-Round90DocxBytes {
    Add-Type -AssemblyName System.IO.Compression
    $memory = [System.IO.MemoryStream]::new()
    $archive = [System.IO.Compression.ZipArchive]::new($memory, [System.IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $contentTypes = $archive.CreateEntry('[Content_Types].xml')
        $writer = [System.IO.StreamWriter]::new($contentTypes.Open())
        $writer.Write('<?xml version="1.0" encoding="UTF-8"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>')
        $writer.Dispose()

        $rels = $archive.CreateEntry('_rels/.rels')
        $writer = [System.IO.StreamWriter]::new($rels.Open())
        $writer.Write('<?xml version="1.0" encoding="UTF-8"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>')
        $writer.Dispose()

        $doc = $archive.CreateEntry('word/document.xml')
        $writer = [System.IO.StreamWriter]::new($doc.Open())
        $writer.Write('<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Round90 DOCX proof upload preview</w:t></w:r></w:p></w:body></w:document>')
        $writer.Dispose()
    } finally {
        $archive.Dispose()
    }

    return $memory.ToArray()
}

try {
    foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $outPath, $errPath)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
    $uploadedDocsBefore = if (Test-Path -LiteralPath $uploadedDocsDir) {
        @(Get-ChildItem -LiteralPath $uploadedDocsDir -File -Recurse | ForEach-Object { $_.FullName })
    } else {
        @()
    }

    dotnet build (Join-Path $root 'EPATA.BusinessLedger.csproj') -c Release -o (Join-Path $root 'obj\verify-build') --no-restore /p:UseAppHost=false
    Assert-UploadPerf ($LASTEXITCODE -eq 0) 'Upload performance verify build failed.'
    Assert-UploadPerf (Test-Path -LiteralPath $dllPath) "Upload performance build output missing at $dllPath"

    Stop-PortOwner $Port
    $args = @(
        "`"$dllPath`"",
        "App:Url=$base",
        "`"ConnectionStrings:DefaultConnection=Data Source=Data\upload-performance.db`"",
        'App:OpenBrowserOnStart=false'
    )
    $server = Start-Process -FilePath 'dotnet' `
        -ArgumentList $args `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outPath `
        -RedirectStandardError $errPath `
        -PassThru
    $health = Wait-UploadPerfHealth
    Assert-UploadPerf ($health.status -eq 'ok') 'Upload performance health did not report ok.'

    $pdfBytes = [System.Text.Encoding]::ASCII.GetBytes("%PDF-1.4`n1 0 obj << /Type /Catalog >> endobj`n2 0 obj << /Length 52 >> stream`nBT /F1 12 Tf 72 720 Td (Round90 PDF proof upload preview) Tj ET`nendstream endobj`n%%EOF")
    $docxBytes = New-Round90DocxBytes
    $pngBytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==')

    $client = [System.Net.Http.HttpClient]::new()
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    try {
        foreach ($file in @(
            @{ Name = 'round90-proof.pdf'; ContentType = 'application/pdf'; Bytes = $pdfBytes },
            @{ Name = 'round90-proof.docx'; ContentType = 'application/vnd.openxmlformats-officedocument.wordprocessingml.document'; Bytes = $docxBytes },
            @{ Name = 'round90-proof.png'; ContentType = 'image/png'; Bytes = $pngBytes }
        )) {
            $content = [System.Net.Http.ByteArrayContent]::new([byte[]]$file.Bytes)
            $content.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse([string]$file.ContentType)
            $form.Add($content, 'files', [string]$file.Name)
        }
        $form.Add([System.Net.Http.StringContent]::new('Round90 Upload Performance'), 'relatedType')
        $form.Add([System.Net.Http.StringContent]::new('ROUND90-UPLOAD-PERF'), 'relatedNumber')

        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $response = $client.PostAsync("$base/api/documents/upload", $form).GetAwaiter().GetResult()
        $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        $watch.Stop()
        Assert-UploadPerf ($response.IsSuccessStatusCode) "Valid PDF/DOCX/PNG upload failed with $([int]$response.StatusCode): $body"
        Assert-UploadPerf ($watch.ElapsedMilliseconds -le $MaxUploadMilliseconds) "Valid PDF/DOCX/PNG upload exceeded ${MaxUploadMilliseconds}ms: $($watch.ElapsedMilliseconds)ms"

        $result = $body | ConvertFrom-Json
        Assert-UploadPerf ($result.count -eq 3) "Valid upload expected 3 documents, got $($result.count)."
        $documents = @($result.documents)
        Assert-UploadPerf (@($documents | Where-Object { $_.fileName -eq 'round90-proof.pdf' -and $_.notes -like '*Round90 PDF proof upload preview*' }).Count -eq 1) 'PDF proof upload did not preserve preview metadata.'
        Assert-UploadPerf (@($documents | Where-Object { $_.fileName -eq 'round90-proof.docx' -and $_.notes -like '*Round90 DOCX proof upload preview*' }).Count -eq 1) 'DOCX proof upload did not preserve preview metadata.'
        Assert-UploadPerf (@($documents | Where-Object { $_.fileName -eq 'round90-proof.png' -and $_.documentType -eq 'Other' }).Count -eq 1) 'PNG proof upload did not create expected metadata.'

        [pscustomobject]@{
            UploadPerformanceRound90 = 'pass'
            Database = $health.database
            UploadedCount = $result.count
            Milliseconds = $watch.ElapsedMilliseconds
            LimitMilliseconds = $MaxUploadMilliseconds
        } | ConvertTo-Json -Depth 4
    } finally {
        $form.Dispose()
        $client.Dispose()
    }
} finally {
    Stop-PortOwner $Port
    if ($server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }

    if (Test-Path -LiteralPath $uploadedDocsDir) {
        $beforeSet = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $uploadedDocsBefore) {
            [void]$beforeSet.Add($file)
        }
        Get-ChildItem -LiteralPath $uploadedDocsDir -File -Recurse |
            Where-Object { -not $beforeSet.Contains($_.FullName) } |
            Remove-Item -Force -ErrorAction SilentlyContinue
    }
}
