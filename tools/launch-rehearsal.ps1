param(
    [int]$BasePort = 5211
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$dataDir = Join-Path $root 'Data'
$publishDir = Join-Path $root 'obj\launch-rehearsal-publish'
$publishBuildDir = Join-Path $root 'obj\launch-rehearsal-publish-build'
$releaseDll = Join-Path $root 'obj\verify-build\EPATA.BusinessLedger.dll'
$publishedDll = Join-Path $publishDir 'EPATA.BusinessLedger.dll'
$productionDbPath = Join-Path $dataDir 'epata-business-ledger.db'
$localAppData = Join-Path $root '.appdata'
$localNuGetConfigDir = Join-Path $localAppData 'NuGet'

function Assert-Rehearsal($condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

function Remove-RehearsalPath([string]$path) {
    $resolvedRoot = [System.IO.Path]::GetFullPath($root)
    $fullPath = [System.IO.Path]::GetFullPath($path)
    Assert-Rehearsal ($fullPath.StartsWith($resolvedRoot, [System.StringComparison]::OrdinalIgnoreCase)) "Refusing to remove path outside workspace: $fullPath"
    if (Test-Path -LiteralPath $fullPath) {
        Remove-Item -LiteralPath $fullPath -Recurse -Force
    }
}

function Stop-PortOwner([int]$port) {
    $owners = @(Get-NetTCPConnection -LocalPort $port -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess -Unique)
    foreach ($owner in $owners) {
        if ($owner -gt 0) {
            Stop-Process -Id $owner -Force -ErrorAction SilentlyContinue
        }
    }
    Start-Sleep -Milliseconds 500
}

function Format-RehearsalArgument([string]$value) {
    if ($value -notmatch '[\s"]') {
        return $value
    }

    return '"' + ($value -replace '"', '\"') + '"'
}

function Wait-RehearsalHealth([string]$baseUrl, [string]$dbName, [string]$label, [string]$errPath) {
    $lastError = $null
    foreach ($attempt in 1..60) {
        try {
            $health = Invoke-RestMethod "$baseUrl/api/health"
            Assert-Rehearsal ($health.status -eq 'ok') "$label health did not report ok."
            Assert-Rehearsal ($health.database -like "*$dbName") "$label used the wrong database. Reported: $($health.database)"
            return $health
        } catch {
            $lastError = $_.Exception.Message
            Start-Sleep -Milliseconds 500
        }
    }

    $stderr = Get-Content -Raw $errPath -ErrorAction SilentlyContinue
    throw "$label did not become healthy. Last error: $lastError. stderr: $stderr"
}

function Start-RehearsalProcess(
    [string]$label,
    [string]$filePath,
    [string[]]$argumentList,
    [int]$port,
    [string]$dbName
) {
    Stop-PortOwner $port
    $safeLabel = $label -replace '[^A-Za-z0-9_-]', '-'
    $outPath = Join-Path $root "launch-$safeLabel-out.log"
    $errPath = Join-Path $root "launch-$safeLabel-err.log"
    Remove-Item -LiteralPath $outPath, $errPath -Force -ErrorAction SilentlyContinue

    $argumentText = if ($filePath.Equals('cmd.exe', [System.StringComparison]::OrdinalIgnoreCase) -and $argumentList.Count -ge 2 -and $argumentList[0] -eq '/c') {
        "/c $($argumentList[1])"
    } else {
        ($argumentList | ForEach-Object { Format-RehearsalArgument $_ }) -join ' '
    }
    $process = Start-Process -FilePath $filePath `
        -ArgumentList $argumentText `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outPath `
        -RedirectStandardError $errPath `
        -PassThru

    try {
        $baseUrl = "http://127.0.0.1:$port"
        $health = Wait-RehearsalHealth $baseUrl $dbName $label $errPath
        return [pscustomobject]@{
            Label = $label
            Port = $port
            Database = $health.database
            Result = 'pass'
        }
    } finally {
        Stop-PortOwner $port
        if ($process -and -not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
}

New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
New-Item -ItemType Directory -Force -Path $localNuGetConfigDir | Out-Null
$env:APPDATA = $localAppData
$productionBefore = if (Test-Path -LiteralPath $productionDbPath) {
    $item = Get-Item -LiteralPath $productionDbPath
    [pscustomobject]@{ Exists = $true; Length = $item.Length; LastWriteTimeUtc = $item.LastWriteTimeUtc }
} else {
    [pscustomobject]@{ Exists = $false; Length = 0; LastWriteTimeUtc = $null }
}

$dbNames = @(
    'launch-rehearsal-run-ps1.db',
    'launch-rehearsal-run-bat.db',
    'launch-rehearsal-release-dll.db',
    'launch-rehearsal-published-dll.db'
)
foreach ($dbName in $dbNames) {
    $dbPath = Join-Path $dataDir $dbName
    Remove-Item -LiteralPath $dbPath, "$dbPath-shm", "$dbPath-wal" -Force -ErrorAction SilentlyContinue
}

Remove-RehearsalPath $publishDir
Remove-RehearsalPath $publishBuildDir

dotnet build (Join-Path $root 'EPATA.BusinessLedger.csproj') -c Release -o (Join-Path $root 'obj\verify-build') --no-restore /p:UseAppHost=false
Assert-Rehearsal ($LASTEXITCODE -eq 0) 'Release DLL build failed during launch rehearsal.'
Assert-Rehearsal (Test-Path -LiteralPath $releaseDll) "Release DLL was not created at $releaseDll"

dotnet publish (Join-Path $root 'EPATA.BusinessLedger.csproj') -c Release -o $publishDir --no-restore --no-self-contained /p:UseAppHost=false /p:OutputPath="$publishBuildDir\"
Assert-Rehearsal ($LASTEXITCODE -eq 0) 'Published output build failed during launch rehearsal.'
Assert-Rehearsal (Test-Path -LiteralPath $publishedDll) "Published DLL was not created at $publishedDll"

$runPs1 = Join-Path $root 'run.ps1'
$runBat = Join-Path $root 'run.bat'
$results = @()

$results += Start-RehearsalProcess 'run.ps1' 'powershell' @(
    '-NoProfile',
    '-ExecutionPolicy',
    'Bypass',
    '-File',
    $runPs1,
    '-ExecutablePath',
    $publishedDll,
    '-NoBuild',
    '-Url',
    "http://127.0.0.1:$BasePort",
    '-ConnectionString',
    'Data Source=Data\launch-rehearsal-run-ps1.db',
    '-OpenBrowserOnStart:$false'
) $BasePort 'launch-rehearsal-run-ps1.db'

$previousNoPause = $env:EPATA_NO_PAUSE
$env:EPATA_NO_PAUSE = '1'
try {
    $batInnerCommand = "`"$runBat`" -ExecutablePath `"$publishedDll`" -NoBuild -Url `"http://127.0.0.1:$($BasePort + 1)`" -ConnectionString `"Data Source=Data\launch-rehearsal-run-bat.db`" -OpenBrowserOnStart:`$false"
    $batCommand = "`"$batInnerCommand`""
    $results += Start-RehearsalProcess 'run.bat' 'cmd.exe' @('/c', $batCommand) ($BasePort + 1) 'launch-rehearsal-run-bat.db'
} finally {
    $env:EPATA_NO_PAUSE = $previousNoPause
}

$results += Start-RehearsalProcess 'Release DLL' 'dotnet' @(
    $releaseDll,
    "App:Url=http://127.0.0.1:$($BasePort + 2)",
    'ConnectionStrings:DefaultConnection=Data Source=Data\launch-rehearsal-release-dll.db',
    'App:OpenBrowserOnStart=false'
) ($BasePort + 2) 'launch-rehearsal-release-dll.db'

$results += Start-RehearsalProcess 'published output' 'dotnet' @(
    $publishedDll,
    "App:Url=http://127.0.0.1:$($BasePort + 3)",
    'ConnectionStrings:DefaultConnection=Data Source=Data\launch-rehearsal-published-dll.db',
    'App:OpenBrowserOnStart=false'
) ($BasePort + 3) 'launch-rehearsal-published-dll.db'

$productionAfter = if (Test-Path -LiteralPath $productionDbPath) {
    $item = Get-Item -LiteralPath $productionDbPath
    [pscustomobject]@{ Exists = $true; Length = $item.Length; LastWriteTimeUtc = $item.LastWriteTimeUtc }
} else {
    [pscustomobject]@{ Exists = $false; Length = 0; LastWriteTimeUtc = $null }
}
Assert-Rehearsal ($productionBefore.Exists -eq $productionAfter.Exists) 'Launch rehearsal unexpectedly created or removed the production database.'
if ($productionBefore.Exists) {
    Assert-Rehearsal ($productionBefore.Length -eq $productionAfter.Length) 'Launch rehearsal changed the production database length.'
    Assert-Rehearsal ($productionBefore.LastWriteTimeUtc -eq $productionAfter.LastWriteTimeUtc) 'Launch rehearsal changed the production database timestamp.'
}

[pscustomobject]@{
    LaunchRehearsalRound87 = 'pass'
    RunPs1 = 'pass'
    RunBat = 'pass'
    ReleaseDll = 'pass'
    PublishedOutput = 'pass'
    ProductionDatabaseUntouched = 'pass'
    Results = $results
} | ConvertTo-Json -Depth 5
