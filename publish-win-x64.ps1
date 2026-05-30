Set-Location $PSScriptRoot
$publishDir = Join-Path $PSScriptRoot "publish-win-x64"
$exePath = Join-Path $publishDir "EPATA.BusinessLedger.exe"

dotnet publish "EPATA.BusinessLedger.csproj" `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None `
  -p:DebugSymbols=false `
  -o $publishDir

Write-Host "Published to $publishDir"
Write-Host "Run $exePath"
