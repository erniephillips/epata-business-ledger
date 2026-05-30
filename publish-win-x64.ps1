$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$publishDir = Join-Path $PSScriptRoot "publish-win-x64"
$exePath = Join-Path $publishDir "EPATA.BusinessLedger.exe"
$nestedPublishDir = Join-Path $publishDir "publish-win-x64"
$localAppData = Join-Path $PSScriptRoot ".appdata"
$localNuGetConfigDir = Join-Path $localAppData "NuGet"
$globalPackageSource = Join-Path $env:USERPROFILE ".nuget\packages"
New-Item -ItemType Directory -Force -Path $localNuGetConfigDir | Out-Null
$env:APPDATA = $localAppData

if (Test-Path -LiteralPath $nestedPublishDir) {
  $resolvedNested = (Resolve-Path -LiteralPath $nestedPublishDir).Path
  if (-not $resolvedNested.StartsWith($publishDir, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove unexpected path: $resolvedNested"
  }
  Remove-Item -LiteralPath $nestedPublishDir -Recurse -Force
}

dotnet publish "EPATA.BusinessLedger.csproj" `
  -c Release `
  -r win-x64 `
  --ignore-failed-sources `
  --source $globalPackageSource `
  --source "https://api.nuget.org/v3/index.json" `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $publishDir

if ($LASTEXITCODE -ne 0) {
  throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Write-Host "Published to $publishDir"
Write-Host "Run $exePath"
