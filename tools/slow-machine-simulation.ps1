param(
    [int]$Port = 5116,
    [int]$LatencyMs = 350
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$base = "http://127.0.0.1:$Port"
$dbPath = Join-Path $root 'Data\slow-machine-simulation.db'
$outPath = Join-Path $root 'slow-machine-simulation.out.log'
$errPath = Join-Path $root 'slow-machine-simulation.err.log'
$pidPath = Join-Path $root 'slow-machine-simulation.pid'
$dllPath = Join-Path $root 'obj\verify-build\EPATA.BusinessLedger.dll'

function Stop-SlowMachineServer {
    if (Test-Path -LiteralPath $pidPath) {
        $processId = Get-Content -LiteralPath $pidPath -Raw
        if ($processId -match '^\d+$') {
            Stop-Process -Id ([int]$processId) -Force -ErrorAction SilentlyContinue
        }
    }
}

function Remove-SlowMachineArtifacts {
    foreach ($path in @($dbPath, "$dbPath-shm", "$dbPath-wal", $outPath, $errPath, $pidPath)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
}

function Invoke-TimedRestMethod {
    param([string]$Uri)

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $body = Invoke-RestMethod $Uri
    $sw.Stop()

    [pscustomobject]@{
        Body = $body
        ElapsedMs = [math]::Round($sw.Elapsed.TotalMilliseconds, 1)
    }
}

function Assert-SlowMachine {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

Stop-SlowMachineServer
Remove-SlowMachineArtifacts

if (-not (Test-Path -LiteralPath $dllPath)) {
    throw "Build output not found at $dllPath. Run dotnet build to obj\verify-build first."
}

$arguments = @(
    "`"$dllPath`"",
    "App:Url=$base",
    "`"ConnectionStrings:DefaultConnection=Data Source=Data\slow-machine-simulation.db`"",
    'App:OpenBrowserOnStart=false',
    "App:ArtificialLatencyMs=$LatencyMs"
)

$server = $null
$result = $null

try {
    $server = Start-Process -FilePath 'dotnet' `
        -ArgumentList $arguments `
        -WorkingDirectory $root `
        -WindowStyle Hidden `
        -RedirectStandardOutput $outPath `
        -RedirectStandardError $errPath `
        -PassThru

    Set-Content -LiteralPath $pidPath -Value $server.Id -NoNewline

    $health = $null
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $health = Invoke-RestMethod "$base/api/health"
            break
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }

    Assert-SlowMachine ($null -ne $health) "Slow-machine server did not become ready at $base."
    Assert-SlowMachine ($health.database -like '*slow-machine-simulation.db') "Slow-machine server is not using the disposable database. Reported: $($health.database)"

    $minimumExpectedMs = [math]::Max(150, [math]::Floor($LatencyMs * 0.55))
    $appInfo = Invoke-TimedRestMethod "$base/api/app-info"
    $dashboard = Invoke-TimedRestMethod "$base/api/dashboard"

    Assert-SlowMachine ($appInfo.ElapsedMs -ge $minimumExpectedMs) "App info response did not include the expected artificial latency. Got $($appInfo.ElapsedMs)ms."
    Assert-SlowMachine ($dashboard.ElapsedMs -ge $minimumExpectedMs) "Dashboard response did not include the expected artificial latency. Got $($dashboard.ElapsedMs)ms."
    Assert-SlowMachine ($dashboard.Body.kpis -ne $null) 'Dashboard did not return KPI data under artificial latency.'

    $uiEmptyLoadingOutput = & node (Join-Path $root 'tools\ui-empty-loading-behavior.mjs') 2>&1
    Assert-SlowMachine ($LASTEXITCODE -eq 0) "UI empty/loading behavior script failed: $($uiEmptyLoadingOutput -join "`n")"
    Assert-SlowMachine (($uiEmptyLoadingOutput -join "`n") -like '*"LoadingStatesRound105":"pass"*') 'Loading-state coverage marker is missing.'

    $entityTableOutput = & node (Join-Path $root 'tools\entity-table-state-behavior.mjs') 2>&1
    Assert-SlowMachine ($LASTEXITCODE -eq 0) "Entity table behavior script failed: $($entityTableOutput -join "`n")"
    Assert-SlowMachine (($entityTableOutput -join "`n") -like '*globalSearchTimer = setTimeout*' -or ($entityTableOutput -join "`n") -like '*"EntityTableStateBehavior":"pass"*') 'Debounced search coverage marker is missing.'

    $invoiceBuilderOutput = & node (Join-Path $root 'tools\invoice-builder-behavior.mjs') 2>&1
    Assert-SlowMachine ($LASTEXITCODE -eq 0) "Invoice builder behavior script failed: $($invoiceBuilderOutput -join "`n")"
    Assert-SlowMachine (($invoiceBuilderOutput -join "`n") -like '*"PreviewDebouncePerfRound98":"pass"*') 'Invoice preview debounce/performance marker is missing.'

    $rapidSubmitOutput = & node (Join-Path $root 'tools\rapid-submit-behavior.mjs') 2>&1
    Assert-SlowMachine ($LASTEXITCODE -eq 0) "Rapid-submit behavior script failed: $($rapidSubmitOutput -join "`n")"
    Assert-SlowMachine (($rapidSubmitOutput -join "`n") -like '*"RapidSubmitRound102":"pass"*') 'Disabled duplicate-submit coverage marker is missing.'

    $result = [pscustomobject]@{
        SlowMachineSimulationRound109 = 'pass'
        Database = $dbPath
        ArtificialLatencyMs = $LatencyMs
        MinimumExpectedMs = $minimumExpectedMs
        AppInfoElapsedMs = $appInfo.ElapsedMs
        DashboardElapsedMs = $dashboard.ElapsedMs
        LoadingStatesRound105 = 'pass'
        DebouncedSearchRound109 = 'pass'
        PreviewDebouncePerfRound98 = 'pass'
        RapidSubmitRound102 = 'pass'
    }
}
finally {
    Stop-SlowMachineServer
    Remove-SlowMachineArtifacts
}

if ($null -ne $result) {
    $result | ConvertTo-Json -Compress
}
