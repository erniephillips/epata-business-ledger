$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$publishDir = Join-Path $PSScriptRoot "publish-win-x64"
$exePath = Join-Path $publishDir "EPATA.BusinessLedger.exe"
$stageRoot = Join-Path $PSScriptRoot "obj\publish-win-x64-stage"
$transactionId = "{0}-{1}" -f (Get-Date -Format "yyyyMMdd-HHmmssfff"), $PID
$stageDir = Join-Path $stageRoot $transactionId
$replacementDir = Join-Path $stageRoot "replacement-$transactionId"
$backupDir = Join-Path $stageRoot "previous-$transactionId"
$failedDir = Join-Path $stageRoot "failed-$transactionId"
$markerPath = Join-Path $stageRoot "latest-stage.txt"
$markerTempPath = Join-Path $stageRoot "latest-stage-$transactionId.tmp"
$publishLockPath = Join-Path $stageRoot "publish.lock"
$localAppData = Join-Path $PSScriptRoot ".appdata"
$localNuGetConfigDir = Join-Path $localAppData "NuGet"
$localNuGetConfigPath = Join-Path $localNuGetConfigDir "NuGet.Config"
$globalPackageSource = Join-Path $env:USERPROFILE ".nuget\packages"
$protectedRuntimeDirectories = @('Data', 'Backups', 'UploadedDocs')

function Get-PublishDirectoryManifest {
  param(
    [Parameter(Mandatory = $true)]
    [string]$DirectoryPath,

    [string[]]$ExcludedTopLevelDirectories = @()
  )

  $rootPath = [System.IO.Path]::GetFullPath($DirectoryPath).TrimEnd([char[]]@(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar))
  $prefix = $rootPath + [System.IO.Path]::DirectorySeparatorChar
  @(
    Get-ChildItem -LiteralPath $rootPath -Recurse -File -Force |
      ForEach-Object {
        $relativePath = $_.FullName.Substring($prefix.Length).Replace('\', '/')
        $topLevelDirectory = ($relativePath -split '/', 2)[0]
        if (-not ($ExcludedTopLevelDirectories -contains $topLevelDirectory)) {
          $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
          "$relativePath`t$($_.Length)`t$hash"
        }
      } |
      Sort-Object
  )
}

function Assert-PublishTreesMatch {
  param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedPath,

    [Parameter(Mandatory = $true)]
    [string]$ActualPath,

    [string[]]$ActualExcludedTopLevelDirectories = @()
  )

  $expectedManifest = @(Get-PublishDirectoryManifest -DirectoryPath $ExpectedPath)
  $actualManifest = @(Get-PublishDirectoryManifest -DirectoryPath $ActualPath -ExcludedTopLevelDirectories $ActualExcludedTopLevelDirectories)
  $difference = Compare-Object -ReferenceObject $expectedManifest -DifferenceObject $actualManifest -SyncWindow 0 | Select-Object -First 1
  if ($null -ne $difference) {
    throw "The publish trees do not match. First difference: $($difference.InputObject) ($($difference.SideIndicator))"
  }
}

function Test-ProtectedRuntimeDirectory {
  param(
    [Parameter(Mandatory = $true)]
    [System.IO.FileSystemInfo]$Item
  )

  return $Item.PSIsContainer -and ($protectedRuntimeDirectories -contains $Item.Name)
}

function Get-BuildOwnedPublishEntries {
  param(
    [Parameter(Mandatory = $true)]
    [string]$DirectoryPath
  )

  if (-not (Test-Path -LiteralPath $DirectoryPath -PathType Container)) {
    return @()
  }

  @(
    Get-ChildItem -LiteralPath $DirectoryPath -Force |
      Where-Object { -not (Test-ProtectedRuntimeDirectory -Item $_) } |
      Sort-Object `
        @{ Expression = { if ($_.Name.Equals('EPATA.BusinessLedger.exe', [System.StringComparison]::OrdinalIgnoreCase)) { 0 } else { 1 } } },
        @{ Expression = { $_.Name } }
  )
}

New-Item -ItemType Directory -Force -Path $stageRoot | Out-Null
$publishLock = $null
try {
  try {
    $publishLock = [System.IO.File]::Open(
      $publishLockPath,
      [System.IO.FileMode]::OpenOrCreate,
      [System.IO.FileAccess]::ReadWrite,
      [System.IO.FileShare]::None)
  } catch {
    throw "Another EPATA publish appears to be running. Wait for it to finish, then try again. $($_.Exception.Message)"
  }

  New-Item -ItemType Directory -Force -Path $localNuGetConfigDir | Out-Null
  $env:APPDATA = $localAppData

  @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="global-packages" value="$globalPackageSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@ | Set-Content -LiteralPath $localNuGetConfigPath -Encoding UTF8

  New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

  dotnet publish "EPATA.BusinessLedger.csproj" `
    -c Release `
    -r win-x64 `
    --ignore-failed-sources `
    --configfile $localNuGetConfigPath `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $stageDir

  if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
  }

  # These are development/test artifacts, not deployable application content. Only
  # remove them from the disposable stage; the canonical runtime-data directories
  # are protected separately and never enter this cleanup path.
  foreach ($nonDeployRelativePath in @(
    'appsettings.Test.json',
    'publish-win-x64',
    'test-results',
    'test-results-root-debug'
  )) {
    $nonDeployPath = [System.IO.Path]::GetFullPath((Join-Path $stageDir $nonDeployRelativePath))
    $stagePrefix = [System.IO.Path]::GetFullPath($stageDir).TrimEnd([char[]]@(
      [System.IO.Path]::DirectorySeparatorChar,
      [System.IO.Path]::AltDirectorySeparatorChar)) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $nonDeployPath.StartsWith($stagePrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
      throw "Refusing to clean an unexpected staged path: $nonDeployPath"
    }
    if (Test-Path -LiteralPath $nonDeployPath) {
      Remove-Item -LiteralPath $nonDeployPath -Recurse -Force
    }
  }

  foreach ($requiredPath in @(
    'EPATA.BusinessLedger.exe',
    'appsettings.json',
    'wwwroot\index.html'
  )) {
    $fullRequiredPath = Join-Path $stageDir $requiredPath
    if (-not (Test-Path -LiteralPath $fullRequiredPath -PathType Leaf)) {
      throw "Publish completed but a required staged file is missing: $fullRequiredPath"
    }
  }

  foreach ($runtimeDirectoryName in $protectedRuntimeDirectories) {
    $unexpectedRuntimePath = Join-Path $stageDir $runtimeDirectoryName
    if (Test-Path -LiteralPath $unexpectedRuntimePath) {
      throw "The staged build unexpectedly contains the protected runtime path '$runtimeDirectoryName'. Refusing to overwrite live data."
    }
  }

  New-Item -ItemType Directory -Force -Path $replacementDir | Out-Null
  Get-ChildItem -LiteralPath $stageDir -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName -Destination $replacementDir -Recurse -Force
  }
  Assert-PublishTreesMatch -ExpectedPath $stageDir -ActualPath $replacementDir

  New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
  if (-not (Test-Path -LiteralPath $publishDir -PathType Container)) {
    New-Item -ItemType Directory -Force -Path $publishDir | Out-Null
  }

  $movedPreviousEntries = [System.Collections.Generic.List[string]]::new()
  $installedEntries = [System.Collections.Generic.List[string]]::new()
  try {
    foreach ($entry in @(Get-BuildOwnedPublishEntries -DirectoryPath $publishDir)) {
      Move-Item -LiteralPath $entry.FullName -Destination (Join-Path $backupDir $entry.Name)
      $movedPreviousEntries.Add($entry.Name)
    }

    foreach ($entry in @(Get-ChildItem -LiteralPath $replacementDir -Force)) {
      Move-Item -LiteralPath $entry.FullName -Destination (Join-Path $publishDir $entry.Name)
      $installedEntries.Add($entry.Name)
    }

    Assert-PublishTreesMatch `
      -ExpectedPath $stageDir `
      -ActualPath $publishDir `
      -ActualExcludedTopLevelDirectories $protectedRuntimeDirectories
  } catch {
    $installError = $_.Exception.Message
    $rollbackErrors = [System.Collections.Generic.List[string]]::new()
    New-Item -ItemType Directory -Force -Path $failedDir | Out-Null

    foreach ($entryName in $installedEntries) {
      $installedPath = Join-Path $publishDir $entryName
      if (Test-Path -LiteralPath $installedPath) {
        try {
          Move-Item -LiteralPath $installedPath -Destination (Join-Path $failedDir $entryName)
        } catch {
          $rollbackErrors.Add("Could not quarantine '$installedPath': $($_.Exception.Message)")
        }
      }
    }
    foreach ($entryName in $movedPreviousEntries) {
      $previousPath = Join-Path $backupDir $entryName
      if (Test-Path -LiteralPath $previousPath) {
        try {
          Move-Item -LiteralPath $previousPath -Destination (Join-Path $publishDir $entryName)
        } catch {
          $rollbackErrors.Add("Could not restore '$previousPath': $($_.Exception.Message)")
        }
      }
    }

    $rollbackDetail = if ($rollbackErrors.Count -gt 0) {
      " Rollback issues: $($rollbackErrors -join ' ')"
    } else {
      " The previous publish tree was restored."
    }
    throw "The staged build was valid, but its rollback-safe installation failed. Live Data, Backups, and UploadedDocs were not moved. $installError$rollbackDetail"
  }

  if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
    throw "The staged build was installed, but the canonical executable is missing: $exePath"
  }

  Set-Content -LiteralPath $markerTempPath -Value ([System.IO.Path]::GetFullPath($stageDir)) -Encoding UTF8
  Move-Item -LiteralPath $markerTempPath -Destination $markerPath -Force

  foreach ($cleanupPath in @($backupDir, $replacementDir)) {
    if (Test-Path -LiteralPath $cleanupPath) {
      try {
        Remove-Item -LiteralPath $cleanupPath -Recurse -Force
      } catch {
        Write-Warning "The publish succeeded, but an ignored rollback directory could not be removed: $cleanupPath. $($_.Exception.Message)"
      }
    }
  }

  Write-Host "Published to $publishDir"
  Write-Host "Run $exePath"
} finally {
  if ($null -ne $publishLock) {
    $publishLock.Dispose()
  }
  if (Test-Path -LiteralPath $markerTempPath) {
    Remove-Item -LiteralPath $markerTempPath -Force -ErrorAction SilentlyContinue
  }
}
