param(
    [string]$ExecutablePath,
    [string]$Url,
    [string]$ConnectionString,
    [object]$OpenBrowserOnStart = $true,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
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

function Test-ExeFilesMatch {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FirstPath,

        [Parameter(Mandatory = $true)]
        [string]$SecondPath
    )

    if (-not (Test-Path -LiteralPath $FirstPath) -or -not (Test-Path -LiteralPath $SecondPath)) {
        return $false
    }

    $firstHash = (Get-FileHash -LiteralPath $FirstPath -Algorithm SHA256).Hash
    $secondHash = (Get-FileHash -LiteralPath $SecondPath -Algorithm SHA256).Hash
    return $firstHash.Equals($secondHash, [System.StringComparison]::OrdinalIgnoreCase)
}

$publishedExePath = Join-Path $PSScriptRoot "publish-win-x64\EPATA.BusinessLedger.exe"
$stagedExePath = Get-NewestStagedExePath
$usesDefaultExecutable = [string]::IsNullOrWhiteSpace($ExecutablePath)
$exePath = if ($usesDefaultExecutable) {
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
    Get-ChildItem -LiteralPath $PSScriptRoot -File |
        Where-Object { $_.Extension -in @('.cs', '.csproj', '.json') } |
        ForEach-Object { $_.FullName }
)
foreach ($sourceDirectoryName in @('Data', 'Models', 'Services', 'Properties')) {
    $sourceDirectory = Join-Path $PSScriptRoot $sourceDirectoryName
    if (Test-Path -LiteralPath $sourceDirectory) {
        $sourceFiles += Get-ChildItem -LiteralPath $sourceDirectory -Recurse -Filter "*.cs" -File |
            ForEach-Object { $_.FullName }
    }
}
$webRoot = Join-Path $PSScriptRoot "wwwroot"
if (Test-Path -LiteralPath $webRoot) {
    $sourceFiles += Get-ChildItem -LiteralPath $webRoot -Recurse -File |
        ForEach-Object { $_.FullName }
}

$needsPublish = -not (Test-Path -LiteralPath $exePath)
$newerStageDoesNotMatchPublished = $false
$publishedExeExists = Test-Path -LiteralPath $publishedExePath
$stagedExeExists = -not [string]::IsNullOrWhiteSpace($stagedExePath) -and (Test-Path -LiteralPath $stagedExePath)
if ($usesDefaultExecutable -and $publishedExeExists -and $stagedExeExists) {
    $stageIsNewer = (Get-Item -LiteralPath $stagedExePath).LastWriteTimeUtc -gt (Get-Item -LiteralPath $publishedExePath).LastWriteTimeUtc
    if ($stageIsNewer) {
        $newerStageDoesNotMatchPublished = -not (Test-ExeFilesMatch -FirstPath $publishedExePath -SecondPath $stagedExePath)
        if ($newerStageDoesNotMatchPublished) {
            $needsPublish = $true
        }
    }
}
if (-not $needsPublish -and -not $NoBuild) {
    $exeTime = (Get-Item -LiteralPath $exePath).LastWriteTimeUtc
    $needsPublish = $sourceFiles |
        Where-Object { Test-Path -LiteralPath $_ } |
        ForEach-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } |
        Where-Object { $_ -gt $exeTime } |
        Select-Object -First 1
}

if ($needsPublish) {
    if ($NoBuild) {
        if ($newerStageDoesNotMatchPublished) {
            throw "The canonical EPATA executable is older than and does not match the newest staged build. Publish again before launching with -NoBuild."
        }
        throw "The published EPATA executable is missing: $exePath"
    }

    Write-Output "Published exe is missing or older than the source. Building the one-time Windows exe first..."
    & (Join-Path $PSScriptRoot "publish-win-x64.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "The app executable could not be built (exit code $LASTEXITCODE). Run publish-win-x64.ps1 after dotnet restore access is available."
    }

    $stagedExePath = Get-NewestStagedExePath
    $publishedExeExists = Test-Path -LiteralPath $publishedExePath
    $stagedExeExists = -not [string]::IsNullOrWhiteSpace($stagedExePath) -and (Test-Path -LiteralPath $stagedExePath)
    $publishedMatchesStage = $publishedExeExists -and $stagedExeExists -and (Test-ExeFilesMatch -FirstPath $publishedExePath -SecondPath $stagedExePath)
    if (-not $publishedMatchesStage) {
        throw "The build completed, but the canonical EPATA executable does not match the newest staged build. Stop the running app and publish again."
    }
    $exePath = $publishedExePath
}

$productionDatabasePath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "Data\epata-business-ledger.db"))
$effectiveConnectionString = if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    "Data Source=$productionDatabasePath"
} else {
    $ConnectionString
}

$openBrowserValue = if ($OpenBrowserOnStart -is [bool]) {
    $OpenBrowserOnStart
} else {
    $text = [string]$OpenBrowserOnStart
    $text.Equals('true', [System.StringComparison]::OrdinalIgnoreCase) -or $text.Equals('1', [System.StringComparison]::OrdinalIgnoreCase)
}

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
        if ($openBrowserValue) {
            Start-Process -FilePath $effectiveUrl
        }
        Write-Output "EPATA is already running at $effectiveUrl."
        exit 0
    }
} catch {
    # No healthy app is listening at the target URL; launch below.
}

$workingDirectory = $PSScriptRoot
$launcherLogDirectory = Join-Path $PSScriptRoot "obj\launcher"
[System.IO.Directory]::CreateDirectory($launcherLogDirectory) | Out-Null
$stdoutLogPath = Join-Path $launcherLogDirectory "epata-stdout.log"
$stderrLogPath = Join-Path $launcherLogDirectory "epata-stderr.log"

# Start-Process joins ArgumentList values into one native command line. That split the
# SQLite connection string at spaces in this OneDrive path and made the app crash before
# Kestrel could listen. Inherited environment variables preserve each value exactly.
$env:ConnectionStrings__DefaultConnection = $effectiveConnectionString
$env:App__Url = $effectiveUrl
$env:App__OpenBrowserOnStart = "false"

if ([System.IO.Path]::GetExtension($exePath).Equals(".dll", [System.StringComparison]::OrdinalIgnoreCase)) {
    $process = Start-Process `
        -FilePath "dotnet" `
        -ArgumentList "`"$exePath`"" `
        -WorkingDirectory $workingDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutLogPath `
        -RedirectStandardError $stderrLogPath `
        -PassThru
} else {
    $process = Start-Process `
        -FilePath $exePath `
        -WorkingDirectory $workingDirectory `
        -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutLogPath `
        -RedirectStandardError $stderrLogPath `
        -PassThru
}

$health = $null
$startupDeadline = [DateTime]::UtcNow.AddSeconds(60)
do {
    if ($process.HasExited) {
        $errorDetail = @(
            if (Test-Path -LiteralPath $stderrLogPath) {
                Get-Content -LiteralPath $stderrLogPath -Tail 20 -ErrorAction SilentlyContinue
            }
            if (Test-Path -LiteralPath $stdoutLogPath) {
                Get-Content -LiteralPath $stdoutLogPath -Tail 20 -ErrorAction SilentlyContinue
            }
        ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $detailText = if ($errorDetail.Count -gt 0) { " $($errorDetail -join ' ')" } else { "" }
        throw "EPATA exited before its local site became ready (exit code $($process.ExitCode)).$detailText Logs: $stdoutLogPath and $stderrLogPath"
    }

    try {
        $health = Invoke-RestMethod $healthUrl -TimeoutSec 2
        if ($health.status -eq "ok") {
            break
        }
    } catch {
        $health = $null
    }

    Start-Sleep -Milliseconds 300
} while ([DateTime]::UtcNow -lt $startupDeadline)

if ($null -eq $health -or $health.status -ne "ok") {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    throw "EPATA did not become ready at $effectiveUrl within 60 seconds. Logs: $stdoutLogPath and $stderrLogPath"
}

if ($openBrowserValue) {
    Start-Process -FilePath $effectiveUrl
}

Write-Output "EPATA is running at $effectiveUrl."
