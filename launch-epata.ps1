param(
    [string]$ExecutablePath,
    [string]$Url,
    [string]$ConnectionString,
    [object]$OpenBrowserOnStart = $true,
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

function Get-LatestStagedDirectory {
    $stageRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "obj\publish-win-x64-stage"))
    $markerPath = Join-Path $stageRoot "latest-stage.txt"
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        return $null
    }

    try {
        $markedDirectory = (Get-Content -LiteralPath $markerPath -Raw -ErrorAction Stop).Trim()
    } catch {
        throw "The latest publish-stage marker could not be read: $markerPath. $($_.Exception.Message)"
    }

    if ([string]::IsNullOrWhiteSpace($markedDirectory) -or -not [System.IO.Path]::IsPathRooted($markedDirectory)) {
        throw "The latest publish-stage marker must contain one absolute staging directory: $markerPath"
    }

    $stageDirectory = [System.IO.Path]::GetFullPath($markedDirectory)
    $stageParent = [System.IO.Path]::GetFullPath([System.IO.Path]::GetDirectoryName($stageDirectory))
    if (-not $stageParent.Equals($stageRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The latest publish-stage marker points outside its staging root: $stageDirectory"
    }
    if (-not (Test-Path -LiteralPath $stageDirectory -PathType Container)) {
        throw "The latest publish-stage marker points to a missing directory: $stageDirectory"
    }

    $stageDirectory
}

function Test-ExeFilesMatch {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FirstPath,

        [Parameter(Mandatory = $true)]
        [string]$SecondPath
    )

    if (-not (Test-Path -LiteralPath $FirstPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $SecondPath -PathType Leaf)) {
        return $false
    }

    $firstHash = (Get-FileHash -LiteralPath $FirstPath -Algorithm SHA256).Hash
    $secondHash = (Get-FileHash -LiteralPath $SecondPath -Algorithm SHA256).Hash
    return $firstHash.Equals($secondHash, [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-ExpectedDatabasePath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ConnectionString,

        [Parameter(Mandatory = $true)]
        [string]$ContentRootPath
    )

    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    try {
        $builder.set_ConnectionString($ConnectionString)
    } catch {
        throw "The SQLite connection string is invalid. $($_.Exception.Message)"
    }

    $dataSource = $null
    foreach ($key in @('Data Source', 'DataSource', 'Filename')) {
        if ($builder.ContainsKey($key)) {
            $dataSource = [string]$builder[$key]
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($dataSource) -or $dataSource.Equals(':memory:', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The SQLite connection string must identify a file-backed Data Source."
    }

    if ([System.IO.Path]::IsPathRooted($dataSource)) {
        return [System.IO.Path]::GetFullPath($dataSource)
    }
    return [System.IO.Path]::GetFullPath((Join-Path $ContentRootPath $dataSource))
}

function Test-ExpectedHealth {
    param(
        $Health,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedDatabasePath
    )

    if ($null -eq $Health -or
        -not ([string]$Health.status).Equals('ok', [System.StringComparison]::OrdinalIgnoreCase) -or
        -not ([string]$Health.mode).Equals('unified-ledger', [System.StringComparison]::Ordinal)) {
        return $false
    }

    try {
        $reportedDatabasePath = [System.IO.Path]::GetFullPath([string]$Health.database)
        $normalizedExpectedPath = [System.IO.Path]::GetFullPath($ExpectedDatabasePath)
        return $reportedDatabasePath.Equals($normalizedExpectedPath, [System.StringComparison]::OrdinalIgnoreCase)
    } catch {
        return $false
    }
}

function Format-HealthIdentity {
    param($Health)

    if ($null -eq $Health) {
        return 'no health response'
    }
    return "status='$([string]$Health.status)', mode='$([string]$Health.mode)', database='$([string]$Health.database)'"
}

$publishedExePath = Join-Path $PSScriptRoot "publish-win-x64\EPATA.BusinessLedger.exe"
$usesDefaultExecutable = [string]::IsNullOrWhiteSpace($ExecutablePath)
$latestStageDirectory = if ($usesDefaultExecutable) { Get-LatestStagedDirectory } else { $null }
$stagedExePath = if ([string]::IsNullOrWhiteSpace($latestStageDirectory)) {
    $null
} else {
    $candidate = Join-Path $latestStageDirectory "EPATA.BusinessLedger.exe"
    if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {
        throw "The latest publish stage is missing EPATA.BusinessLedger.exe: $latestStageDirectory"
    }
    $candidate
}
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
$publishedDoesNotMatchLatestStage = $false
$publishedExeExists = Test-Path -LiteralPath $publishedExePath
$stagedExeExists = -not [string]::IsNullOrWhiteSpace($stagedExePath) -and (Test-Path -LiteralPath $stagedExePath)
if ($usesDefaultExecutable -and $publishedExeExists -and $stagedExeExists) {
    $publishedDoesNotMatchLatestStage = -not (Test-ExeFilesMatch -FirstPath $publishedExePath -SecondPath $stagedExePath)
    if ($publishedDoesNotMatchLatestStage) {
        $needsPublish = $true
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
        if ($publishedDoesNotMatchLatestStage) {
            throw "The canonical EPATA publish tree does not match the latest validated stage. Publish again before launching with -NoBuild."
        }
        throw "The published EPATA executable is missing: $exePath"
    }

    Write-Output "Published exe is missing or older than the source. Building the one-time Windows exe first..."
    & (Join-Path $PSScriptRoot "publish-win-x64.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "The app executable could not be built (exit code $LASTEXITCODE). Run publish-win-x64.ps1 after dotnet restore access is available."
    }

    $latestStageDirectory = Get-LatestStagedDirectory
    $stagedExePath = Join-Path $latestStageDirectory "EPATA.BusinessLedger.exe"
    $publishedExeExists = Test-Path -LiteralPath $publishedExePath
    $stagedExeExists = -not [string]::IsNullOrWhiteSpace($stagedExePath) -and (Test-Path -LiteralPath $stagedExePath)
    $publishedMatchesStage = $publishedExeExists -and $stagedExeExists -and (Test-ExeFilesMatch -FirstPath $publishedExePath -SecondPath $stagedExePath)
    if (-not $publishedMatchesStage) {
        throw "The build completed, but the canonical EPATA publish tree does not match the latest validated stage. Stop the running app and publish again."
    }
    $exePath = $publishedExePath
}

$productionDatabasePath = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot "Data\epata-business-ledger.db"))
$effectiveConnectionString = if ([string]::IsNullOrWhiteSpace($ConnectionString)) {
    "Data Source=$productionDatabasePath"
} else {
    $ConnectionString
}
$workingDirectory = $PSScriptRoot
$expectedDatabasePath = Get-ExpectedDatabasePath -ConnectionString $effectiveConnectionString -ContentRootPath $workingDirectory

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

$healthUrl = $effectiveUrl.TrimEnd('/') + "/api/health"
$health = $null
try {
    $health = Invoke-RestMethod $healthUrl -TimeoutSec 2
} catch {
    # No service is listening at the target URL; launch below.
}
if ($null -ne $health) {
    if (-not (Test-ExpectedHealth -Health $health -ExpectedDatabasePath $expectedDatabasePath)) {
        $identity = Format-HealthIdentity -Health $health
        throw "A service is already responding at $effectiveUrl, but it is not the expected EPATA production ledger ($identity). Expected mode='unified-ledger' and database='$expectedDatabasePath'. Refusing to start or open it."
    }
    if ($openBrowserValue) {
        Start-Process -FilePath $effectiveUrl
    }
    Write-Output "EPATA is already running at $effectiveUrl with database $expectedDatabasePath."
    exit 0
}

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

    $candidateHealth = $null
    try {
        $candidateHealth = Invoke-RestMethod $healthUrl -TimeoutSec 2
    } catch {
        # The child has not opened the health endpoint yet.
    }
    if ($null -ne $candidateHealth) {
        if (Test-ExpectedHealth -Health $candidateHealth -ExpectedDatabasePath $expectedDatabasePath) {
            $health = $candidateHealth
            break
        }

        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        $identity = Format-HealthIdentity -Health $candidateHealth
        throw "A different service answered the post-start health check at $effectiveUrl ($identity). Expected mode='unified-ledger' and database='$expectedDatabasePath'. The newly started process was stopped without opening a browser."
    }

    Start-Sleep -Milliseconds 300
} while ([DateTime]::UtcNow -lt $startupDeadline)

if (-not (Test-ExpectedHealth -Health $health -ExpectedDatabasePath $expectedDatabasePath)) {
    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }
    throw "EPATA did not become ready at $effectiveUrl within 60 seconds. Logs: $stdoutLogPath and $stderrLogPath"
}

if ($openBrowserValue) {
    Start-Process -FilePath $effectiveUrl
}

Write-Output "EPATA is running at $effectiveUrl."
