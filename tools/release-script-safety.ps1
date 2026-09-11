$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$fixtureRoot = Join-Path $root 'obj\release-script-safety'
$fixtureLauncher = Join-Path $fixtureRoot 'launch-epata.ps1'
$fixturePublisher = Join-Path $fixtureRoot 'publish-win-x64.ps1'
$fixturePublishDir = Join-Path $fixtureRoot 'publish-win-x64'
$fixtureStageRoot = Join-Path $fixtureRoot 'obj\publish-win-x64-stage'
$expectedDatabasePath = Join-Path $fixtureRoot 'Data\expected.db'
$originalLocation = Get-Location
$originalAppData = $env:APPDATA

function Assert-SafetyTest($condition, [string]$message) {
    if (-not $condition) {
        throw $message
    }
}

function Remove-SafetyFixture {
    if (-not (Test-Path -LiteralPath $fixtureRoot)) {
        return
    }

    $normalizedRoot = [System.IO.Path]::GetFullPath($root).TrimEnd([char[]]@(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)) + [System.IO.Path]::DirectorySeparatorChar
    $normalizedFixture = [System.IO.Path]::GetFullPath($fixtureRoot)
    Assert-SafetyTest ($normalizedFixture.StartsWith($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase)) "Refusing to remove fixture outside the workspace: $normalizedFixture"
    Assert-SafetyTest ((Split-Path -Leaf $normalizedFixture) -eq 'release-script-safety') "Refusing to remove an unexpected fixture path: $normalizedFixture"
    Remove-Item -LiteralPath $normalizedFixture -Recurse -Force
}

function Assert-PowerShellParses([string]$path) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile(
        $path,
        [ref]$tokens,
        [ref]$errors)
    Assert-SafetyTest ($errors.Count -eq 0) "$path has PowerShell parse errors: $($errors.Message -join '; ')"
}

try {
    Assert-PowerShellParses (Join-Path $root 'launch-epata.ps1')
    Assert-PowerShellParses (Join-Path $root 'publish-win-x64.ps1')

    Remove-SafetyFixture
    New-Item -ItemType Directory -Force -Path $fixtureRoot | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'launch-epata.ps1') -Destination $fixtureLauncher
    Copy-Item -LiteralPath (Join-Path $root 'publish-win-x64.ps1') -Destination $fixturePublisher
    Set-Content -LiteralPath (Join-Path $fixtureRoot 'EPATA.BusinessLedger.csproj') -Value '<Project Sdk="Microsoft.NET.Sdk.Web" />' -Encoding UTF8
    New-Item -ItemType Directory -Force -Path $fixtureStageRoot | Out-Null
    Set-Content -LiteralPath (Join-Path $fixtureStageRoot 'latest-stage.txt') -Value (Join-Path $fixtureStageRoot 'obsolete-stage') -Encoding UTF8

    foreach ($path in @(
        (Join-Path $fixturePublishDir 'Data'),
        (Join-Path $fixturePublishDir 'Backups'),
        (Join-Path $fixturePublishDir 'UploadedDocs'),
        (Join-Path $fixturePublishDir 'stale-assets')
    )) {
        New-Item -ItemType Directory -Force -Path $path | Out-Null
    }
    Set-Content -LiteralPath (Join-Path $fixturePublishDir 'EPATA.BusinessLedger.exe') -Value 'old executable' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fixturePublishDir 'stale.txt') -Value 'remove me' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fixturePublishDir 'stale-assets\old.js') -Value 'remove me too' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fixturePublishDir 'Data\runtime.db') -Value 'live database sentinel' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fixturePublishDir 'Backups\runtime-backup.db') -Value 'live backup sentinel' -Encoding UTF8
    Set-Content -LiteralPath (Join-Path $fixturePublishDir 'UploadedDocs\runtime-proof.pdf') -Value 'live proof sentinel' -Encoding UTF8

    function dotnet {
        $arguments = @($args)
        $outputIndex = [Array]::IndexOf($arguments, '-o')
        Assert-SafetyTest ($outputIndex -ge 0 -and $outputIndex + 1 -lt $arguments.Count) 'Fake dotnet did not receive an output directory.'
        $outputDirectory = [string]$arguments[$outputIndex + 1]
        New-Item -ItemType Directory -Force -Path (Join-Path $outputDirectory 'wwwroot') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $outputDirectory 'publish-win-x64') | Out-Null
        New-Item -ItemType Directory -Force -Path (Join-Path $outputDirectory 'test-results') | Out-Null
        Set-Content -LiteralPath (Join-Path $outputDirectory 'EPATA.BusinessLedger.exe') -Value 'new executable' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $outputDirectory 'appsettings.json') -Value '{"App":{"Url":"http://127.0.0.1:59999"}}' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $outputDirectory 'appsettings.Test.json') -Value '{"TestOnly":true}' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $outputDirectory 'wwwroot\index.html') -Value '<!doctype html><title>EPATA</title>' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $outputDirectory 'wwwroot\app.js') -Value 'console.log("new");' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $outputDirectory 'publish-win-x64\nested.txt') -Value 'must not recursively deploy' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $outputDirectory 'test-results\result.txt') -Value 'must not deploy' -Encoding UTF8
        $global:LASTEXITCODE = 0
    }

    try {
        & $fixturePublisher
    } finally {
        Set-Location $originalLocation
        Remove-Item Function:\dotnet -ErrorAction SilentlyContinue
    }

    Assert-SafetyTest (-not (Test-Path -LiteralPath (Join-Path $fixturePublishDir 'stale.txt'))) 'Publisher left a stale root file behind.'
    Assert-SafetyTest (-not (Test-Path -LiteralPath (Join-Path $fixturePublishDir 'stale-assets'))) 'Publisher left a stale build directory behind.'
    Assert-SafetyTest (-not (Test-Path -LiteralPath (Join-Path $fixturePublishDir 'appsettings.Test.json'))) 'Publisher deployed test-only configuration.'
    Assert-SafetyTest (-not (Test-Path -LiteralPath (Join-Path $fixturePublishDir 'publish-win-x64'))) 'Publisher recursively nested the canonical publish directory.'
    Assert-SafetyTest (-not (Test-Path -LiteralPath (Join-Path $fixturePublishDir 'test-results'))) 'Publisher deployed test output.'
    Assert-SafetyTest ((Get-Content -LiteralPath (Join-Path $fixturePublishDir 'EPATA.BusinessLedger.exe') -Raw).Trim() -eq 'new executable') 'Publisher did not install the staged executable.'
    Assert-SafetyTest ((Get-Content -LiteralPath (Join-Path $fixturePublishDir 'Data\runtime.db') -Raw).Trim() -eq 'live database sentinel') 'Publisher changed the protected runtime database.'
    Assert-SafetyTest ((Get-Content -LiteralPath (Join-Path $fixturePublishDir 'Backups\runtime-backup.db') -Raw).Trim() -eq 'live backup sentinel') 'Publisher changed the protected runtime backup.'
    Assert-SafetyTest ((Get-Content -LiteralPath (Join-Path $fixturePublishDir 'UploadedDocs\runtime-proof.pdf') -Raw).Trim() -eq 'live proof sentinel') 'Publisher changed the protected uploaded proof.'

    $markerPath = Join-Path $fixtureStageRoot 'latest-stage.txt'
    Assert-SafetyTest (Test-Path -LiteralPath $markerPath -PathType Leaf) 'Publisher did not create latest-stage.txt.'
    $markedStage = [System.IO.Path]::GetFullPath((Get-Content -LiteralPath $markerPath -Raw).Trim())
    Assert-SafetyTest ((Split-Path -Parent $markedStage).Equals([System.IO.Path]::GetFullPath($fixtureStageRoot), [System.StringComparison]::OrdinalIgnoreCase)) 'Publisher marker did not name a direct child of the stage root.'
    Assert-SafetyTest (Test-Path -LiteralPath (Join-Path $markedStage 'EPATA.BusinessLedger.exe') -PathType Leaf) 'Publisher marker named a stage without an executable.'

    # A locked/running executable must fail before the prior publish tree or any
    # runtime-data directory is changed, and the successful marker must stay put.
    $markerBeforeLockedPublish = (Get-Content -LiteralPath $markerPath -Raw).Trim()
    $lockedExecutable = [System.IO.File]::Open(
        (Join-Path $fixturePublishDir 'EPATA.BusinessLedger.exe'),
        [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read,
        [System.IO.FileShare]::None)
    function dotnet {
        $arguments = @($args)
        $outputIndex = [Array]::IndexOf($arguments, '-o')
        Assert-SafetyTest ($outputIndex -ge 0 -and $outputIndex + 1 -lt $arguments.Count) 'Fake dotnet did not receive an output directory.'
        $outputDirectory = [string]$arguments[$outputIndex + 1]
        New-Item -ItemType Directory -Force -Path (Join-Path $outputDirectory 'wwwroot') | Out-Null
        Set-Content -LiteralPath (Join-Path $outputDirectory 'EPATA.BusinessLedger.exe') -Value 'locked-update executable' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $outputDirectory 'appsettings.json') -Value '{"App":{"Url":"http://127.0.0.1:59999"}}' -Encoding UTF8
        Set-Content -LiteralPath (Join-Path $outputDirectory 'wwwroot\index.html') -Value '<!doctype html><title>Locked update</title>' -Encoding UTF8
        $global:LASTEXITCODE = 0
    }
    $lockedPublishError = $null
    try {
        & $fixturePublisher
    } catch {
        $lockedPublishError = $_.Exception.Message
    } finally {
        $lockedExecutable.Dispose()
        Set-Location $originalLocation
        Remove-Item Function:\dotnet -ErrorAction SilentlyContinue
    }
    Assert-SafetyTest ($lockedPublishError -like '*rollback-safe installation failed*') "Publisher did not safely reject a locked executable: $lockedPublishError"
    Assert-SafetyTest ((Get-Content -LiteralPath (Join-Path $fixturePublishDir 'EPATA.BusinessLedger.exe') -Raw).Trim() -eq 'new executable') 'Locked publish attempt changed the prior executable.'
    Assert-SafetyTest (((Get-Content -LiteralPath $markerPath -Raw).Trim()) -eq $markerBeforeLockedPublish) 'Locked publish attempt replaced the last successful stage marker.'
    Assert-SafetyTest ((Get-Content -LiteralPath (Join-Path $fixturePublishDir 'Data\runtime.db') -Raw).Trim() -eq 'live database sentinel') 'Locked publish attempt changed the runtime database.'

    # A newer unmarked stage must not override the publisher's explicit marker.
    $unmarkedStage = Join-Path $fixtureStageRoot '99991231-235959999-9999'
    New-Item -ItemType Directory -Force -Path $unmarkedStage | Out-Null
    Set-Content -LiteralPath (Join-Path $unmarkedStage 'EPATA.BusinessLedger.exe') -Value 'unmarked executable' -Encoding UTF8
    (Get-Item -LiteralPath (Join-Path $unmarkedStage 'EPATA.BusinessLedger.exe')).LastWriteTimeUtc = [DateTime]::UtcNow.AddYears(10)

    $global:ReleaseSafetyStartProcessCalls = 0
    function Invoke-RestMethod {
        [pscustomobject]@{
            status = 'ok'
            mode = 'unified-ledger'
            database = Join-Path $fixtureRoot 'Data\not-expected.db'
        }
    }
    function Start-Process {
        $global:ReleaseSafetyStartProcessCalls++
        throw 'Start-Process must not run for a mismatched preflight health response.'
    }

    $preflightError = $null
    try {
        & $fixtureLauncher `
            -NoBuild `
            -Url 'http://127.0.0.1:59997' `
            -ConnectionString 'Data Source=Data\expected.db' `
            -OpenBrowserOnStart:$false
    } catch {
        $preflightError = $_.Exception.Message
    } finally {
        Set-Location $originalLocation
        Remove-Item Function:\Invoke-RestMethod, Function:\Start-Process -ErrorAction SilentlyContinue
    }
    Assert-SafetyTest ($preflightError -like '*not the expected EPATA production ledger*') "Launcher did not reject an exact-path preflight mismatch: $preflightError"
    Assert-SafetyTest ($global:ReleaseSafetyStartProcessCalls -eq 0) 'Launcher attempted to start a child despite a mismatched preflight health response.'

    $global:ReleaseSafetyHealthCalls = 0
    $global:ReleaseSafetyStartProcessCalls = 0
    $global:ReleaseSafetyStopProcessCalls = 0
    function Invoke-RestMethod {
        $global:ReleaseSafetyHealthCalls++
        if ($global:ReleaseSafetyHealthCalls -eq 1) {
            throw 'No service during preflight.'
        }
        [pscustomobject]@{
            status = 'ok'
            mode = 'other-ledger'
            database = $expectedDatabasePath
        }
    }
    function Start-Process {
        $global:ReleaseSafetyStartProcessCalls++
        [pscustomobject]@{ HasExited = $false; Id = 4242 }
    }
    function Stop-Process {
        $global:ReleaseSafetyStopProcessCalls++
    }

    $postStartError = $null
    try {
        & $fixtureLauncher `
            -NoBuild `
            -Url 'http://127.0.0.1:59996' `
            -ConnectionString 'Data Source=Data\expected.db' `
            -OpenBrowserOnStart:$false
    } catch {
        $postStartError = $_.Exception.Message
    } finally {
        Set-Location $originalLocation
        Remove-Item Function:\Invoke-RestMethod, Function:\Start-Process, Function:\Stop-Process -ErrorAction SilentlyContinue
    }
    Assert-SafetyTest ($postStartError -like '*different service answered the post-start health check*') "Launcher accepted an unrelated post-start service: $postStartError"
    Assert-SafetyTest ($global:ReleaseSafetyStartProcessCalls -eq 1) 'Launcher did not start exactly one child in the post-start race test.'
    Assert-SafetyTest ($global:ReleaseSafetyStopProcessCalls -eq 1) 'Launcher did not stop its child after an unrelated service answered post-start health.'

    $validHarness = Join-Path $fixtureRoot 'valid-health-harness.ps1'
    @'
param(
    [string]$Launcher,
    [string]$FixtureRoot
)
function Invoke-RestMethod {
    [pscustomobject]@{
        status = 'ok'
        mode = 'unified-ledger'
        database = Join-Path $FixtureRoot 'Data\.\expected.db'
    }
}
function Start-Process {
    throw 'The valid-health harness must not start a child or browser.'
}
& $Launcher `
    -NoBuild `
    -Url 'http://127.0.0.1:59995' `
    -ConnectionString 'Data Source=Data\expected.db' `
    -OpenBrowserOnStart:$false
'@ | Set-Content -LiteralPath $validHarness -Encoding UTF8

    $powerShellEngine = (Get-Process -Id $PID).Path
    $validOutput = @(& $powerShellEngine -NoProfile -ExecutionPolicy Bypass -File $validHarness -Launcher $fixtureLauncher -FixtureRoot $fixtureRoot 2>&1)
    $validExitCode = $LASTEXITCODE
    Assert-SafetyTest ($validExitCode -eq 0) "Launcher rejected a normalized exact health identity: $($validOutput -join ' ')"
    Assert-SafetyTest (($validOutput -join ' ') -like '*already running*') 'Launcher did not recognize the valid health response.'

    Set-Content -LiteralPath $markerPath -Value $fixtureRoot -Encoding UTF8
    $invalidMarkerError = $null
    try {
        & $fixtureLauncher `
            -NoBuild `
            -Url 'http://127.0.0.1:59994' `
            -ConnectionString 'Data Source=Data\expected.db' `
            -OpenBrowserOnStart:$false
    } catch {
        $invalidMarkerError = $_.Exception.Message
    } finally {
        Set-Location $originalLocation
    }
    Assert-SafetyTest ($invalidMarkerError -like '*outside its staging root*') "Launcher did not reject a marker outside the stage root: $invalidMarkerError"

    [pscustomobject]@{
        ReleaseScriptSafety = 'pass'
        PublisherRemovedStaleBuildFiles = 'pass'
        PublisherPreservedRuntimeData = 'pass'
        LockedExecutablePreservedPriorPublish = 'pass'
        LatestStageMarkerValidated = 'pass'
        UnmarkedNewerStageIgnored = 'pass'
        ExactDatabaseHealthRequired = 'pass'
        UnifiedLedgerModeRequired = 'pass'
        PostStartUnrelatedServiceRejected = 'pass'
    } | ConvertTo-Json -Depth 3
} finally {
    Set-Location $originalLocation
    $env:APPDATA = $originalAppData
    Remove-Item Function:\dotnet, Function:\Invoke-RestMethod, Function:\Start-Process, Function:\Stop-Process -ErrorAction SilentlyContinue
    Remove-Variable ReleaseSafetyHealthCalls, ReleaseSafetyStartProcessCalls, ReleaseSafetyStopProcessCalls -Scope Global -ErrorAction SilentlyContinue
    Remove-SafetyFixture
}
