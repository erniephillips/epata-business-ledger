Set-Location $PSScriptRoot
$exePath = Join-Path $PSScriptRoot "publish-win-x64\EPATA.BusinessLedger.exe"
$sourceFiles = @(
    "Program.cs",
    "EPATA.BusinessLedger.csproj",
    "Data\AppDbContext.cs",
    "Models\InvoiceDocument.cs",
    "Models\InvoiceLineItem.cs",
    "wwwroot\index.html",
    "wwwroot\js\app.js",
    "wwwroot\css\site.css"
) | ForEach-Object { Join-Path $PSScriptRoot $_ }

$needsPublish = -not (Test-Path $exePath)
if (-not $needsPublish) {
    $exeTime = (Get-Item $exePath).LastWriteTimeUtc
    $needsPublish = $sourceFiles |
        Where-Object { Test-Path $_ } |
        ForEach-Object { (Get-Item $_).LastWriteTimeUtc } |
        Where-Object { $_ -gt $exeTime } |
        Select-Object -First 1
}

if ($needsPublish) {
    Write-Host "Published exe is missing or older than the source. Building the one-time Windows exe first..."
    & (Join-Path $PSScriptRoot "publish-win-x64.ps1")
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exePath)) {
        throw "The published exe could not be built. Open this folder in Codex or PowerShell and run publish-win-x64.ps1 after dotnet restore access is available."
    }
}

& $exePath
