Set-Location $PSScriptRoot
$exePath = Join-Path $PSScriptRoot "publish-win-x64\EPATA.BusinessLedger.exe"
if (Test-Path $exePath) {
    & $exePath
} else {
    Write-Host "Published exe not found. Building one-time Windows exe first..."
    & (Join-Path $PSScriptRoot "publish-win-x64.ps1")
    & $exePath
}
