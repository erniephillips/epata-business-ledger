param(
    [string]$ExecutablePath,
    [string]$Url,
    [string]$ConnectionString,
    [object]$OpenBrowserOnStart = $true,
    [switch]$NoBuild
)

Set-Location $PSScriptRoot

function Get-NewestStagedExePath {
    $stageRoot = Join-Path $PSScriptRoot "obj\publish-win-x64-stage"
    $legacyStageExePath = Join-Path $stageRoot "EPATA.BusinessLedger.exe"
    $candidates = @()
    if (Test-Path -LiteralPath $legacyStageExePath) {
        $candidates += Get-Item -LiteralPath $legacyStageExePath
    }
    if (Test-Path -LiteralPath $stageRoot) {
        $candidates += Get-ChildItem -LiteralPath $stageRoot -Recurse -Filter "EPATA.BusinessLedger.exe" -File -ErrorAction SilentlyContinue
    }
    $candidates |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1 |
        ForEach-Object { $_.FullName }
}

$publishedExePath = Join-Path $PSScriptRoot "publish-win-x64\EPATA.BusinessLedger.exe"
$stagedExePath = Get-NewestStagedExePath
$exePath = if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    if (Test-Path -LiteralPath $publishedExePath) {
        $publishedExePath
    } elseif (-not [string]::IsNullOrWhiteSpace($stagedExePath) -and (Test-Path -LiteralPath $stagedExePath)) {
        $stagedExePath
    } else {
        $publishedExePath
    }
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
    "wwwroot\css\site.css",
    "wwwroot\invoice-builder\index.html",
    "wwwroot\invoice-builder\css\app.css"
) | ForEach-Object { Join-Path $PSScriptRoot $_ }
$invoiceBuilderJsPath = Join-Path $PSScriptRoot "wwwroot\invoice-builder\js"
if (Test-Path -LiteralPath $invoiceBuilderJsPath) {
    $sourceFiles += Get-ChildItem -Path $invoiceBuilderJsPath -Filter "*.js" -File |
        ForEach-Object { $_.FullName }
}

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
    Write-Output "Published exe is missing or older than the source. Building the one-time Windows exe first..."
    & (Join-Path $PSScriptRoot "publish-win-x64.ps1")
    $stagedExePath = Get-NewestStagedExePath
    if (Test-Path -LiteralPath $publishedExePath) {
        $exePath = $publishedExePath
    } elseif (-not [string]::IsNullOrWhiteSpace($stagedExePath) -and (Test-Path -LiteralPath $stagedExePath)) {
        $exePath = $stagedExePath
    }
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path $exePath)) {
        throw "The app executable could not be built. Run publish-win-x64.ps1 after dotnet restore access is available."
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

$effectiveUrl = if (-not [string]::IsNullOrWhiteSpace($Url)) {
    $Url
} else {
    try {
        $settings = Get-Content -Raw (Join-Path $PSScriptRoot "appsettings.json") | ConvertFrom-Json
        [string]$settings.App.Url
    } catch {
        "http://127.0.0.1:5062"
    }
}
if ([string]::IsNullOrWhiteSpace($effectiveUrl)) {
    $effectiveUrl = "http://127.0.0.1:5062"
}

try {
    $healthUrl = $effectiveUrl.TrimEnd('/') + "/api/health"
    $health = Invoke-RestMethod $healthUrl -TimeoutSec 2
    if ($health.status -eq "ok") {
        Write-Output "EPATA is already running at $effectiveUrl."
        exit 0
    }
} catch {
    # No healthy app is listening at the target URL; launch below.
}

if ([System.IO.Path]::GetExtension($exePath).Equals(".dll", [System.StringComparison]::OrdinalIgnoreCase)) {
    & dotnet $exePath @appArgs
} else {
    & $exePath @appArgs
}
