$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
$publishedExe = Join-Path $PSScriptRoot "publish-win-x64\EPATA.BusinessLedger.exe"
if (-not (Test-Path -LiteralPath $publishedExe)) {
    Write-Host "Published exe not found. Creating it first..."
    & (Join-Path $PSScriptRoot "publish-win-x64.ps1")
}
if (-not (Test-Path -LiteralPath $publishedExe)) {
    throw "The published EPATA executable could not be found: $publishedExe"
}

$launcher = Join-Path $PSScriptRoot "launch-epata.ps1"
if (-not (Test-Path -LiteralPath $launcher)) {
    throw "The EPATA launcher could not be found: $launcher"
}

$powershell = (Get-Command powershell.exe -ErrorAction Stop).Source
$wsh = New-Object -ComObject WScript.Shell

function Resolve-KnownFolder {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$FallbackPath
    )

    $path = [Environment]::GetFolderPath($Name)
    if ([string]::IsNullOrWhiteSpace($path)) {
        $path = $FallbackPath
    }
    if ([string]::IsNullOrWhiteSpace($path)) {
        throw "Windows did not return a usable $Name folder."
    }
    [System.IO.Directory]::CreateDirectory($path) | Out-Null
    $path
}

function Set-EpataShortcut {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [bool]$OpenBrowser
    )

    $shortcut = $wsh.CreateShortcut($Path)
    $openBrowserText = $OpenBrowser.ToString().ToLowerInvariant()
    $shortcut.TargetPath = $powershell
    $shortcut.Arguments = "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$launcher`" -OpenBrowserOnStart $openBrowserText"
    $shortcut.WorkingDirectory = $PSScriptRoot
    $shortcut.IconLocation = "$publishedExe,0"
    $shortcut.Description = "Open the local EPATA Business Ledger"
    $shortcut.Save()

    $savedShortcut = $wsh.CreateShortcut($Path)
    if ($savedShortcut.TargetPath -ne $powershell -or
        $savedShortcut.WorkingDirectory -ne $PSScriptRoot -or
        $savedShortcut.Arguments -notlike "*$launcher*") {
        throw "Shortcut verification failed: $Path"
    }
    Write-Host "Shortcut created: $Path"
}

$shortcutName = "EPATA Business Ledger.lnk"
$oneDriveDesktop = if ([string]::IsNullOrWhiteSpace($env:OneDrive)) { $null } else { Join-Path $env:OneDrive "Desktop" }
$desktopFallback = if (-not [string]::IsNullOrWhiteSpace($oneDriveDesktop) -and (Test-Path -LiteralPath $oneDriveDesktop)) {
    $oneDriveDesktop
} else {
    Join-Path $env:USERPROFILE "Desktop"
}
$programsFallback = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$startupFallback = Join-Path $programsFallback "Startup"

$desktopFolder = Resolve-KnownFolder -Name "Desktop" -FallbackPath $desktopFallback
$programsFolder = Resolve-KnownFolder -Name "Programs" -FallbackPath $programsFallback
$startupFolder = Resolve-KnownFolder -Name "Startup" -FallbackPath $startupFallback

$desktopShortcut = Join-Path $desktopFolder $shortcutName
$startMenuShortcut = Join-Path $programsFolder $shortcutName
$startupShortcut = Join-Path $startupFolder $shortcutName

Set-EpataShortcut -Path $desktopShortcut -OpenBrowser $true
Set-EpataShortcut -Path $startMenuShortcut -OpenBrowser $true
Set-EpataShortcut -Path $startupShortcut -OpenBrowser $false

$oldShortcut = Join-Path $desktopFolder "EPATA Business Account Tracker.lnk"
if (Test-Path -LiteralPath $oldShortcut) {
    Remove-Item -LiteralPath $oldShortcut -Force
    Write-Host "Removed obsolete shortcut: $oldShortcut"
}
