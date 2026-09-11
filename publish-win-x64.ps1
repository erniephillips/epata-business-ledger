$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$publishDir = Join-Path $PSScriptRoot "publish-win-x64"
$exePath = Join-Path $publishDir "EPATA.BusinessLedger.exe"
$stageRoot = Join-Path $PSScriptRoot "obj\publish-win-x64-stage"
$stageDir = Join-Path $stageRoot (Get-Date -Format "yyyyMMdd-HHmmss")
$nestedPublishDir = Join-Path $publishDir "publish-win-x64"
$localAppData = Join-Path $PSScriptRoot ".appdata"
$localNuGetConfigDir = Join-Path $localAppData "NuGet"
$localNuGetConfigPath = Join-Path $localNuGetConfigDir "NuGet.Config"
$globalPackageSource = Join-Path $env:USERPROFILE ".nuget\packages"
New-Item -ItemType Directory -Force -Path $localNuGetConfigDir | Out-Null
$env:APPDATA = $localAppData

@"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="global-packages" value="$globalPackageSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $localNuGetConfigPath -Encoding UTF8

if (Test-Path -LiteralPath $nestedPublishDir) {
  $resolvedNested = (Resolve-Path -LiteralPath $nestedPublishDir).Path
  if (-not $resolvedNested.StartsWith($publishDir, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to remove unexpected path: $resolvedNested"
  }
  Remove-Item -LiteralPath $nestedPublishDir -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null
New-Item -ItemType Directory -Force -Path $publishDir | Out-Null

dotnet publish "EPATA.BusinessLedger.csproj" `
  -c Release `
  -r win-x64 `
  --ignore-failed-sources `
  --configfile $localNuGetConfigPath `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $stageDir

if ($LASTEXITCODE -ne 0) {
  Write-Warning "dotnet publish failed with exit code $LASTEXITCODE."
  exit $LASTEXITCODE
}

$stageExePath = Join-Path $stageDir "EPATA.BusinessLedger.exe"
if (-not (Test-Path -LiteralPath $stageExePath)) {
  throw "Publish completed but no executable was found in the staging folder: $stageExePath"
}

Set-Content -LiteralPath (Join-Path $stageRoot "latest-stage.txt") -Value $stageDir -Encoding UTF8

try {
  Copy-Item -Path (Join-Path $stageDir "*") -Destination $publishDir -Recurse -Force
} catch {
  throw "Published build succeeded, but Windows/OneDrive blocked copying it into $publishDir. Stop the running EPATA app and publish again. The staged executable remains at $stageExePath. $($_.Exception.Message)"
}

if (-not (Test-Path -LiteralPath $exePath)) {
  throw "The staged build was created, but the canonical executable is missing: $exePath"
}

$stageHash = (Get-FileHash -LiteralPath $stageExePath -Algorithm SHA256).Hash
$publishedHash = (Get-FileHash -LiteralPath $exePath -Algorithm SHA256).Hash
if (-not $stageHash.Equals($publishedHash, [System.StringComparison]::OrdinalIgnoreCase)) {
  throw "The canonical executable does not match the staged build. Stop the running EPATA app and publish again. Staged: $stageExePath. Canonical: $exePath."
}

Write-Host "Published to $publishDir"
Write-Host "Run $exePath"
