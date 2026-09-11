param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$PlaywrightArgs
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
$serverScript = Join-Path $PSScriptRoot 'playwright-server.ps1'
$Port = if ($env:EPATA_PLAYWRIGHT_PORT) { [int]$env:EPATA_PLAYWRIGHT_PORT } else { 5120 }
$objDir = Join-Path $root 'obj'
New-Item -ItemType Directory -Path $objDir -Force | Out-Null
$serverOutLog = Join-Path $root 'obj\playwright-server.out.log'
$serverErrLog = Join-Path $root 'obj\playwright-server.err.log'

function Get-ListeningProcessIdsForPort {
    param([Parameter(Mandatory = $true)][int]$LocalPort)

    @(
        netstat.exe -ano -p tcp |
            ForEach-Object {
                $fields = $_.Trim() -split '\s+'
                if ($fields.Count -ge 5 -and
                    $fields[0] -eq 'TCP' -and
                    $fields[1].EndsWith(":$LocalPort", [System.StringComparison]::OrdinalIgnoreCase) -and
                    $fields[3] -eq 'LISTENING') {
                    [int]$fields[4]
                }
            } |
            Select-Object -Unique
    )
}

if ($PlaywrightArgs.Count -gt 0 -and $PlaywrightArgs[0] -eq '--') {
    $PlaywrightArgs = @($PlaywrightArgs | Select-Object -Skip 1)
}

$existingProcessId = Get-ListeningProcessIdsForPort -LocalPort $Port | Select-Object -First 1
if ($existingProcessId) {
    throw "Port $Port is already in use by process $existingProcessId."
}

$serverArgs = "-NoProfile -ExecutionPolicy Bypass -File `"$serverScript`" -Port $Port"

$server = Start-Process `
    -FilePath 'pwsh' `
    -ArgumentList $serverArgs `
    -WorkingDirectory $root `
    -WindowStyle Hidden `
    -RedirectStandardOutput $serverOutLog `
    -RedirectStandardError $serverErrLog `
    -PassThru

$exitCode = 1

try {
    $ready = $false
    for ($i = 0; $i -lt 45; $i++) {
        Start-Sleep -Seconds 1
        try {
            Invoke-RestMethod "http://127.0.0.1:$Port/api/health" -TimeoutSec 2 | Out-Null
            $ready = $true
            break
        } catch { }
    }

    if (-not $ready) {
        Write-Host "Server stdout log: $serverOutLog"
        Write-Host "Server stderr log: $serverErrLog"
        throw "Playwright test server did not become healthy. PID $($server.Id)."
    }

    $env:EPATA_PLAYWRIGHT_PORT = [string]$Port
    $env:EPATA_PLAYWRIGHT_BASE_URL = "http://127.0.0.1:$Port"
    $env:EPATA_PLAYWRIGHT_SKIP_WEBSERVER = '1'
    if (-not $env:EPATA_PLAYWRIGHT_EXECUTABLE_PATH) {
        $installedChrome = 'C:\Program Files\Google\Chrome\Application\chrome.exe'
        if (Test-Path -LiteralPath $installedChrome) {
            $env:EPATA_PLAYWRIGHT_EXECUTABLE_PATH = $installedChrome
        }
    }

    Push-Location $root
    try {
        $npx = Get-Command npx -ErrorAction SilentlyContinue
        if ($npx) {
            & $npx.Source playwright test @PlaywrightArgs
        } else {
            $nodeExecutable = $env:EPATA_NODE_EXECUTABLE
            if (-not $nodeExecutable) {
                $nodeCommand = Get-Command node -ErrorAction SilentlyContinue
                if ($nodeCommand) { $nodeExecutable = $nodeCommand.Source }
            }
            if (-not $nodeExecutable) {
                $bundledNode = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe'
                if (Test-Path -LiteralPath $bundledNode) { $nodeExecutable = $bundledNode }
            }
            if (-not $nodeExecutable -or -not (Test-Path -LiteralPath $nodeExecutable)) {
                throw 'Node.js was not found. Set EPATA_NODE_EXECUTABLE to node.exe and try again.'
            }
            $playwrightCli = Join-Path $root 'node_modules\@playwright\test\cli.js'
            if (-not (Test-Path -LiteralPath $playwrightCli)) {
                throw 'Playwright is not installed. Run npm install before the browser tests.'
            }
            & $nodeExecutable $playwrightCli test @PlaywrightArgs
        }
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }
} finally {
    $listenerProcessIds = @(Get-ListeningProcessIdsForPort -LocalPort $Port)
    foreach ($listenerProcessId in $listenerProcessIds) {
        $listenerProcess = Get-Process -Id $listenerProcessId -ErrorAction SilentlyContinue
        $listenerPath = if ($listenerProcess) { $listenerProcess.Path } else { $null }
        if ($listenerProcess -and
            $listenerProcess.ProcessName -eq 'EPATA.BusinessLedger' -and
            -not [string]::IsNullOrWhiteSpace($listenerPath) -and
            $listenerPath.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
            Stop-Process -Id $listenerProcessId -Force -ErrorAction SilentlyContinue
        }
    }
    Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}

exit $exitCode
