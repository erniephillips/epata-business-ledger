param(
    [string]$ExecutablePath,
    [string]$Url,
    [string]$ConnectionString,
    [object]$OpenBrowserOnStart = $true,
    [switch]$NoBuild
)

Set-Location $PSScriptRoot
$exePath = if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    Join-Path $PSScriptRoot "publish-win-x64\EPATA.BusinessLedger.exe"
} elseif ([System.IO.Path]::IsPathRooted($ExecutablePath)) {
    [System.IO.Path]::GetFullPath($ExecutablePath)
} else {
    [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot $ExecutablePath))
}
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

if ($needsPublish -and -not $NoBuild) {
    Write-Host "Published exe is missing or older than the source. Building the one-time Windows exe first..."
    & (Join-Path $PSScriptRoot "publish-win-x64.ps1")
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exePath)) {
        throw "The published exe could not be built. Open this folder in Codex or PowerShell and run publish-win-x64.ps1 after dotnet restore access is available."
    }
}

if ($needsPublish -and $NoBuild) {
    throw "The requested executable path does not exist: $exePath"
}

$appArgs = @()
if (-not [string]::IsNullOrWhiteSpace($Url)) {
    $appArgs += "App:Url=$Url"
}
if (-not [string]::IsNullOrWhiteSpace($ConnectionString)) {
    $appArgs += "ConnectionStrings:DefaultConnection=$ConnectionString"
}
$openBrowserValue = if ($OpenBrowserOnStart -is [bool]) {
    $OpenBrowserOnStart
} else {
    $text = [string]$OpenBrowserOnStart
    $text.Equals('true', [System.StringComparison]::OrdinalIgnoreCase) -or $text.Equals('1', [System.StringComparison]::OrdinalIgnoreCase)
}
$appArgs += "App:OpenBrowserOnStart=$openBrowserValue"

if ([System.IO.Path]::GetExtension($exePath).Equals(".dll", [System.StringComparison]::OrdinalIgnoreCase)) {
    & dotnet $exePath @appArgs
} else {
    & $exePath @appArgs
}
