param(
    [string]$ExecutablePath,
    [string]$Url,
    [string]$ConnectionString,
    [object]$OpenBrowserOnStart = $true,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$launcherPath = Join-Path $PSScriptRoot "launch-epata.ps1"
if (-not (Test-Path -LiteralPath $launcherPath)) {
    throw "The EPATA launcher could not be found: $launcherPath"
}

# Compatibility entry point for existing run.bat links and older shortcuts. Keep
# startup, health checking, argument quoting, and browser opening in one launcher.
& $launcherPath @PSBoundParameters
