[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [switch]$Regression,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$EvidencePath
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$runRoot = Join-Path ([IO.Path]::GetTempPath()) "MirrorPulse-cli-integration-$([guid]::NewGuid().ToString('N'))"
$cliRoot = Join-Path $runRoot "cli"
$hostRoot = Join-Path $runRoot "host"
$syncRoot = Join-Path $runRoot "sync"
$dataRoot = Join-Path $runRoot "data"
$sourceRoot = Join-Path $runRoot "source"
$hostProcess = $null
$fixtureBytes = [Text.Encoding]::ASCII.GetBytes("0123456789ABCDEF-local-fixture")

function Invoke-MirrorPulseCli {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)

    Write-Host "CLI command: $($Arguments -join ' ')"
    $text = (& $script:cliExecutable @Arguments 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) {
        throw "CLI command failed ($LASTEXITCODE): $($Arguments -join ' ')`n$text"
    }

    if ([string]::IsNullOrWhiteSpace($text)) {
        throw "CLI command returned no output: $($Arguments -join ' ')"
    }

    return $text | ConvertFrom-Json
}

function Wait-MirrorPulseHostStopped {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        & $script:cliExecutable --json --no-start status 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { return }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)

    throw "The Host did not stop after the CLI lifecycle command."
}

function Write-MirrorPulseUploadDiagnostics {
    $logPath = Join-Path $dataRoot 'logs\mirrorpulse.log'
    if (-not (Test-Path -LiteralPath $logPath)) { return }
    Write-Host 'First safe diagnostic for each upload failure boundary:'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $printed = 0
    foreach ($line in Get-Content -LiteralPath $logPath) {
        $entry = $line | ConvertFrom-Json
        if ($entry.Category -ne 'CloudFiles.Upload') { continue }
        $key = "$($entry.Code):$($entry.Fields.operationId):$($entry.Fields.acknowledgementPhase)"
        if ($seen.Add($key)) {
            Write-Host $line
            if (++$printed -ge 32) { break }
        }
    }
}

try {
    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) {
        throw "The Adapter package was not found: $PackagePath"
    }

    New-Item -ItemType Directory -Path $cliRoot, $hostRoot, $syncRoot, $dataRoot, $sourceRoot -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $sourceRoot "nested") -Force | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $sourceRoot "nested\fixture.txt"), $fixtureBytes)

    $cliProject = Join-Path $repositoryRoot "src\MirrorPulse.Cli\MirrorPulse.Cli.csproj"
    $hostProject = Join-Path $repositoryRoot "src\MirrorPulse.Host\MirrorPulse.Host.csproj"
    & dotnet publish $cliProject --configuration $Configuration --runtime $Runtime --self-contained true --no-restore --output $cliRoot
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed." }
    & dotnet publish $hostProject --configuration $Configuration --runtime $Runtime --self-contained true --no-restore --output $hostRoot
    if ($LASTEXITCODE -ne 0) { throw "Host publish failed." }

    $script:cliExecutable = Join-Path $cliRoot "mp.exe"
    $hostExecutable = Join-Path $hostRoot "MirrorPulse.Host.exe"
    if (-not (Test-Path -LiteralPath $script:cliExecutable) -or
        -not (Test-Path -LiteralPath $hostExecutable)) {
        throw "The published CLI or Host executable is missing."
    }

    $env:MIRRORPULSE_DATA_ROOT = $dataRoot
    $env:MIRRORPULSE_SYNC_ROOT = $syncRoot
    $env:MIRRORPULSE_HOST_PATH = $hostExecutable
    $env:MIRRORPULSE_DEVELOPER_MODE = "1"

    $version = Invoke-MirrorPulseCli @("--json", "--version")
    if ($version.kind -ne "version") { throw "The CLI version envelope is invalid." }

    try {
        $hostStatus = Invoke-MirrorPulseCli @("--json", "--developer-mode", "host", "start")
    }
    catch {
        # Read safe diagnostics from the original attempt without changing a partially
        # registered root by launching another Host.
        $startupLog = Join-Path $dataRoot 'logs\mirrorpulse.log'
        if (Test-Path -LiteralPath $startupLog) {
            Write-Host 'Original isolated Host startup diagnostic:'
            Get-Content -LiteralPath $startupLog -Tail 8 | ForEach-Object { Write-Host $_ }
        }
        throw
    }
    if ($hostStatus.kind -ne "result" -or $hostStatus.data.state -notin @("Running", "Degraded")) {
        throw "The Host did not reach a running state through the CLI."
    }

    $install = Invoke-MirrorPulseCli @("--json", "--developer-mode", "adapter", "install", "--package", (Resolve-Path $PackagePath).Path)
    $installId = $install.data.installedAdapterId
    if ([string]::IsNullOrWhiteSpace($installId)) { throw "The CLI install response did not contain an installation ID." }

    $instance = Invoke-MirrorPulseCli @("--json", "--developer-mode", "instance", "create",
        "--install-id", $installId, "--name", "CLI fixture",
        "--config", "sourceDirectory=$sourceRoot")
    $instanceId = $instance.data.createdInstanceId
    if ([string]::IsNullOrWhiteSpace($instanceId)) { throw "The CLI create response did not contain an instance ID." }

    # A new instance is admitted when the Host is composed on the next start.
    $restart = Invoke-MirrorPulseCli @("--json", "--developer-mode", "host", "restart")
    if ($restart.kind -ne "result") { throw "The CLI Host restart response is invalid." }
    Wait-MirrorPulseHostStopped

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 500
        $status = Invoke-MirrorPulseCli @("--json", "--developer-mode", "status")
        $entry = @($status.data.instances) | Where-Object { $_.instanceId -eq $instanceId } | Select-Object -First 1
    } while (($null -eq $entry -or $entry.phase -ne "Connected") -and [DateTime]::UtcNow -lt $deadline)
    if ($null -eq $entry -or $entry.phase -ne "Connected") {
        throw "The CLI status response did not show a connected Local Worker: $($entry | ConvertTo-Json -Compress)"
    }

    $topology = Invoke-MirrorPulseCli @("--json", "--developer-mode", "adapter", "list")
    $rootDirectory = @($topology.data.roots) |
        Where-Object { $_.instanceId -eq $instanceId } |
        Select-Object -First 1 -ExpandProperty directoryName
    if ([string]::IsNullOrWhiteSpace($rootDirectory)) {
        throw "The Adapter instance did not register a first-level root."
    }
    $mappedRoot = Join-Path $syncRoot $rootDirectory

    $refresh = Invoke-MirrorPulseCli @("--json", "--developer-mode", "sync", "refresh")
    if ($refresh.kind -ne "result") { throw "The CLI refresh response is invalid." }

    $mappedFixture = Join-Path $mappedRoot "nested\fixture.txt"
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path -LiteralPath $mappedFixture) -and [DateTime]::UtcNow -lt $deadline) {
        Start-Sleep -Milliseconds 500
        Invoke-MirrorPulseCli @("--json", "--developer-mode", "sync", "refresh") | Out-Null
    }
    if (-not (Test-Path -LiteralPath $mappedFixture)) {
        throw "The signed Local Adapter file was not listed through the Cloud Files root."
    }

    $range = [byte[]]::new(5)
    $stream = [IO.File]::Open($mappedFixture, [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    try {
        [void]$stream.Seek(7, [IO.SeekOrigin]::Begin)
        $count = $stream.Read($range, 0, $range.Length)
    }
    finally {
        $stream.Dispose()
    }
    if ($count -ne 5 -or [Text.Encoding]::ASCII.GetString($range) -ne "789AB") {
        throw "The Cloud Files range read returned the wrong bytes."
    }

    if ($Regression) {
        $roundTrip = Join-Path $sourceRoot "cli-roundtrip.txt"
        Set-Content -LiteralPath $roundTrip -Value "remote-before-local" -NoNewline
        Invoke-MirrorPulseCli @("--json", "--developer-mode", "sync", "refresh") | Out-Null
        $remoteFile = Join-Path $mappedRoot "cli-roundtrip.txt"
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        while (-not (Test-Path -LiteralPath $remoteFile) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 500
            Invoke-MirrorPulseCli @("--json", "--developer-mode", "sync", "refresh") | Out-Null
        }
        if (-not (Test-Path -LiteralPath $remoteFile)) {
            throw "The remote Local Adapter change did not reach the Cloud Files root."
        }

        if ([IO.File]::ReadAllText($remoteFile) -ne "remote-before-local") {
            throw "The remote batch did not hydrate the expected content."
        }
        $cursorStatus = Invoke-MirrorPulseCli @("--json", "--developer-mode", "status")
        $cursorBeforeRestart = @($cursorStatus.data.instances) |
            Where-Object { $_.instanceId -eq $instanceId } |
            Select-Object -First 1 -ExpandProperty cursorFingerprint
        if ([string]::IsNullOrWhiteSpace($cursorBeforeRestart)) {
            throw "The remote batch did not persist an Adapter cursor."
        }

        # Preserve the local change in CfSharp's journal while the instance is offline.
        Invoke-MirrorPulseCli @("--json", "--developer-mode", "instance", "disable",
            "--instance-id", $instanceId) | Out-Null
        Invoke-MirrorPulseCli @("--json", "--developer-mode", "host", "restart") | Out-Null
        Wait-MirrorPulseHostStopped
        try {
            $offlineStatus = Invoke-MirrorPulseCli @("--json", "--developer-mode", "status")
        }
        catch {
            Write-Host "Disabled Host startup diagnostics:"
            $hostOutput = (& $hostExecutable --run-once 2>&1 | Out-String).Trim()
            Write-Host $hostOutput
            throw
        }
        $offlineInstance = @($offlineStatus.data.instances) |
            Where-Object { $_.instanceId -eq $instanceId } | Select-Object -First 1
        if ($null -eq $offlineInstance -or $offlineInstance.phase -ne "Offline") {
            throw "The disabled Adapter instance did not remain offline after the Host restarted."
        }
        Start-Sleep -Seconds 1
        $baselineStatus = Invoke-MirrorPulseCli @("--json", "--developer-mode", "status")
        $pendingBeforeWrite = $baselineStatus.data.pendingUploads
        $queuedFile = Join-Path $mappedRoot "queued-upload.txt"
        [IO.File]::WriteAllText($queuedFile, "queued-local-upload")
        $deadline = [DateTime]::UtcNow.AddSeconds(15)
        do {
            Start-Sleep -Milliseconds 250
            $queuedStatus = Invoke-MirrorPulseCli @("--json", "--developer-mode", "status")
        } while ($queuedStatus.data.pendingUploads -le $pendingBeforeWrite -and [DateTime]::UtcNow -lt $deadline)
        if ($queuedStatus.data.pendingUploads -le $pendingBeforeWrite) {
            throw "The offline local edit was not retained in the upload journal."
        }
        Write-Host "Offline journal grew from $pendingBeforeWrite to $($queuedStatus.data.pendingUploads) pending upload(s)."
        Invoke-MirrorPulseCli @("--json", "--developer-mode", "instance", "enable",
            "--instance-id", $instanceId) | Out-Null
        Invoke-MirrorPulseCli @("--json", "--developer-mode", "host", "restart") | Out-Null
        Wait-MirrorPulseHostStopped
        $sourceUpload = Join-Path $sourceRoot "queued-upload.txt"
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        while (-not (Test-Path -LiteralPath $sourceUpload) -and [DateTime]::UtcNow -lt $deadline) {
            Start-Sleep -Milliseconds 500
            Invoke-MirrorPulseCli @("--json", "--developer-mode", "status") | Out-Null
        }
        if (-not (Test-Path -LiteralPath $sourceUpload) -or
            [IO.File]::ReadAllText($sourceUpload) -ne "queued-local-upload") {
            $failedStatus = Invoke-MirrorPulseCli @("--json", "--developer-mode", "status")
            $failedConflicts = Invoke-MirrorPulseCli @("--json", "--developer-mode", "conflict", "list")
            $failedInstance = @($failedStatus.data.instances) |
                Where-Object { $_.instanceId -eq $instanceId } | Select-Object -First 1
            $sourceNames = (Get-ChildItem -LiteralPath $sourceRoot -File | Select-Object -ExpandProperty Name) -join ","
            Write-MirrorPulseUploadDiagnostics
            throw "The persisted upload was not delivered. Pending=$($failedStatus.data.pendingUploads); " +
                "UploadConflicts=$($failedStatus.data.pendingUploadConflicts); " +
                "Phase=$($failedInstance.phase); Error=$($failedInstance.lastErrorCode); " +
                "Conflicts=$(@($failedConflicts.data.items).Count); SourceFiles=$sourceNames."
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            $drainedStatus = Invoke-MirrorPulseCli @("--json", "--developer-mode", "status")
            if ($drainedStatus.data.pendingUploads -eq 0) { break }
            Start-Sleep -Milliseconds 500
        } while ([DateTime]::UtcNow -lt $deadline)
        if ($drainedStatus.data.pendingUploads -ne 0) {
            $instanceStatus = @($drainedStatus.data.instances) | Where-Object instanceId -eq $instanceId | Select-Object -First 1
            Write-MirrorPulseUploadDiagnostics
            Write-Host 'Blocked local operation reasons:'
            @($drainedStatus.data.blockedLocalOperations) | Group-Object reason |
                ForEach-Object { Write-Host "$($_.Name): $($_.Count)" }
            $conflictStatus = Invoke-MirrorPulseCli @('--json', '--developer-mode', 'conflict', 'list')
            Write-Host 'Conflict reasons:'
            @($conflictStatus.data.items) | Group-Object reason |
                ForEach-Object { Write-Host "$($_.Name): $($_.Count)" }
            foreach ($conflictItem in @($conflictStatus.data.items)) {
                $relative = $conflictItem.relativePath.Replace('\', '/')
                $role = if ($relative.EndsWith('/nested/fixture.txt')) { 'HydratedRangeFixture' }
                    elseif ($relative.EndsWith('/cli-roundtrip.txt')) { 'HydratedRoundTripFixture' }
                    elseif ($relative.EndsWith('/queued-upload.txt')) { 'OfflineUploadFixture' }
                    else { 'OtherFixture' }
                Write-Host "Fixture conflict: role=$role; reason=$($conflictItem.reason); source=$($conflictItem.source); localRevisionPresent=$(-not [string]::IsNullOrEmpty($conflictItem.localRevision)); remoteRevisionPresent=$(-not [string]::IsNullOrEmpty($conflictItem.remoteRevision))."
            }
            throw "The upload journal did not drain. Pending=$($drainedStatus.data.pendingUploads); " +
                "UploadConflicts=$($drainedStatus.data.pendingUploadConflicts); " +
                "Phase=$($instanceStatus.phase); Error=$($instanceStatus.lastErrorCode)."
        }

        # Both sides change while the Host is stopped. The conflict must survive another restart.
        Invoke-MirrorPulseCli @("--json", "--developer-mode", "host", "stop") | Out-Null
        Wait-MirrorPulseHostStopped
        [IO.File]::WriteAllText($remoteFile, "local-conflict")
        [IO.File]::WriteAllText($roundTrip, "remote-conflict")
        Invoke-MirrorPulseCli @("--json", "--developer-mode", "host", "start") | Out-Null
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        do {
            Start-Sleep -Milliseconds 500
            Invoke-MirrorPulseCli @("--json", "--developer-mode", "sync", "refresh") | Out-Null
            $conflicts = Invoke-MirrorPulseCli @("--json", "--developer-mode", "conflict", "list")
            $conflict = @($conflicts.data.items) |
                Where-Object { $_.relativePath -like "*cli-roundtrip.txt" } | Select-Object -First 1
        } while ($null -eq $conflict -and [DateTime]::UtcNow -lt $deadline)
        if ($null -eq $conflict) { throw "The local/remote race did not create a CLI-visible conflict." }
        $conflictId = $conflict.conflictId
    }

    $restart = Invoke-MirrorPulseCli @("--json", "--developer-mode", "host", "restart")
    if ($restart.kind -ne "result") { throw "The CLI Host restart response is invalid." }
    Wait-MirrorPulseHostStopped
    $listed = Invoke-MirrorPulseCli @("--json", "--developer-mode", "instance", "list")
    if (-not (@($listed.data.instances) | Where-Object { $_.instanceId -eq $instanceId })) {
        throw "The instance was not retained across a CLI Host restart."
    }
    if ($Regression) {
        $persisted = Invoke-MirrorPulseCli @("--json", "--developer-mode", "conflict", "list")
        if (-not (@($persisted.data.items) | Where-Object { $_.conflictId -eq $conflictId })) {
            throw "The conflict was not retained across a CLI Host restart."
        }
        $persistedStatus = Invoke-MirrorPulseCli @("--json", "--developer-mode", "status")
        $cursorAfterRestart = @($persistedStatus.data.instances) |
            Where-Object { $_.instanceId -eq $instanceId } |
            Select-Object -First 1 -ExpandProperty cursorFingerprint
        if ([string]::IsNullOrWhiteSpace($cursorAfterRestart)) {
            throw "The Adapter cursor was not retained across a CLI Host restart."
        }
    }

    Invoke-MirrorPulseCli @("--json", "--developer-mode", "host", "stop") | Out-Null
    Write-Output "Verified CLI -> Host -> Worker integration for $Runtime ($instanceId)."
}
finally {
    if ($null -ne $script:cliExecutable -and (Test-Path -LiteralPath $script:cliExecutable)) {
        try {
            & $script:cliExecutable --json --developer-mode host stop 2>$null | Out-Null
        } catch {
        }
    }
    if ($null -ne $hostProcess -and -not $hostProcess.HasExited) {
        $hostProcess.Kill($true)
    }
    Remove-Item Env:\MIRRORPULSE_DATA_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:\MIRRORPULSE_SYNC_ROOT -ErrorAction SilentlyContinue
    Remove-Item Env:\MIRRORPULSE_HOST_PATH -ErrorAction SilentlyContinue
    Remove-Item Env:\MIRRORPULSE_DEVELOPER_MODE -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $runRoot) {
        Remove-Item -LiteralPath $runRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if ($EvidencePath) {
    [ordered]@{
        schemaVersion=1;runtime=$Runtime;regression=$Regression.IsPresent;rangeRead=$true
        offlineQueueRetained=$Regression.IsPresent;uploadJournalDrained=$Regression.IsPresent
        conflictPersisted=$Regression.IsPresent;cursorPersisted=$Regression.IsPresent
    } | ConvertTo-Json | Set-Content -LiteralPath $EvidencePath -Encoding utf8
}
