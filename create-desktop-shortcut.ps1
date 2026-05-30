Set-Location $PSScriptRoot
$publishedExe = Join-Path $PSScriptRoot "publish-win-x64\EPATA.BusinessLedger.exe"
if (-not (Test-Path $publishedExe)) {
    Write-Host "Published exe not found. Creating it first..."
    & (Join-Path $PSScriptRoot "publish-win-x64.ps1")
}
$target = $publishedExe
$shortcutPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "EPATA Business Ledger.lnk"
$wsh = New-Object -ComObject WScript.Shell
$shortcut = $wsh.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $target
$shortcut.WorkingDirectory = Split-Path $target
$shortcut.IconLocation = $target
$shortcut.Save()
Write-Host "Shortcut created: $shortcutPath"
