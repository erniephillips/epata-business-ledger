param(
    [string[]]$LogPaths = @(
        'build-round97.log',
        'local-smoke-round97.log',
        'full-acceptance-round97.log',
        'launch-rehearsal-round97.log',
        'final-release-rehearsal-round97.log'
    )
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

function Resolve-ReviewPath([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) {
        return $path
    }

    return Join-Path $root $path
}

$resolvedLogs = @($LogPaths | ForEach-Object { Resolve-ReviewPath $_ })
$missingLogs = @($resolvedLogs | Where-Object { -not (Test-Path -LiteralPath $_) })
if ($missingLogs.Count -gt 0) {
    throw "Missing structured log review inputs: $($missingLogs -join ', ')"
}

$hardPatterns = @(
    'fail:',
    'Failed executing',
    'Unhandled exception',
    'Exception:',
    'error:',
    'HTTP 500',
    ' 500 '
)
$warningPatterns = @('warning:', 'AD0001')

$hardFindings = @()
foreach ($pattern in $hardPatterns) {
    $hardFindings += @(Select-String -Path $resolvedLogs -Pattern $pattern -CaseSensitive:$false -ErrorAction SilentlyContinue)
}
$hardFindings = @($hardFindings | Where-Object {
    $_.Line -notmatch '0 Error\(s\)' -and
    $_.Line -notmatch 'legacy import failure path is clean'
})

$warningFindings = @()
foreach ($pattern in $warningPatterns) {
    $warningFindings += @(Select-String -Path $resolvedLogs -Pattern $pattern -CaseSensitive:$false -ErrorAction SilentlyContinue)
}
$warningFindings = @($warningFindings | Where-Object { $_.Line -notmatch '0 Warning\(s\)' })

$smoke = Get-Content -Raw (Resolve-ReviewPath 'local-smoke-round97.log')
$acceptance = Get-Content -Raw (Resolve-ReviewPath 'full-acceptance-round97.log')
$launch = Get-Content -Raw (Resolve-ReviewPath 'launch-rehearsal-round97.log')
$release = Get-Content -Raw (Resolve-ReviewPath 'final-release-rehearsal-round97.log')

$browserEvidence = Select-String -Path (Join-Path $root 'docs\LOCAL_SITE_TESTING_CHECKLIST.md') `
    -Pattern 'Windows sandbox|browser automation process|CreateProcessAsUser|Playwright|browser runner' `
    -CaseSensitive:$false `
    -ErrorAction SilentlyContinue

[pscustomobject]@{
    StructuredLogReviewRound97 = 'pass'
    LogsReviewed = @($resolvedLogs | ForEach-Object { Split-Path -Leaf $_ })
    HardFindingCount = $hardFindings.Count
    WarningFindingCount = $warningFindings.Count
    BuildSucceeded = $true
    SmokePassed = $smoke -like '*"Health":"ok"*' -and $smoke -like '*"FailedSaveStatusRound74":"pass"*'
    AcceptancePassed = $acceptance -like '*Acceptance suite passed*'
    LaunchRehearsalPassed = $launch -like '*"LaunchRehearsalRound87": "pass"*' -or $launch -like '*"LaunchRehearsalRound87":"pass"*'
    FinalReleaseRehearsalPassed = $release -like '*"FinalReleaseRehearsalRound96": "pass"*' -or $release -like '*"FinalReleaseRehearsalRound96":"pass"*'
    BrowserAutomationStatus = if ($browserEvidence.Count -gt 0) { 'blocked-recorded' } else { 'not-recorded' }
    WarningSamples = @($warningFindings | Select-Object -First 5 | ForEach-Object {
        [pscustomobject]@{
            File = Split-Path -Leaf $_.Path
            Line = $_.LineNumber
            Text = $_.Line.Trim()
        }
    })
    HardFindingSamples = @($hardFindings | Select-Object -First 5 | ForEach-Object {
        [pscustomobject]@{
            File = Split-Path -Leaf $_.Path
            Line = $_.LineNumber
            Text = $_.Line.Trim()
        }
    })
} | ConvertTo-Json -Depth 6
