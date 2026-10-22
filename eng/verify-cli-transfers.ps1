[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-arm64',
    [Parameter(Mandatory)][string]$PackagePath,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [Parameter(Mandatory)][string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
if ($env:MIRRORPULSE_NATIVE_TEST -cne '1') { throw 'The CLI transfer fixture requires an explicitly enabled disposable native test environment.' }
$repository = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceSha = (& git -C $repository rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceSha -cnotmatch '\A[0-9a-f]{40}\z' -or ($env:GITHUB_SHA -and $sourceSha -cne $env:GITHUB_SHA)) { throw 'The actual CLI transfer source commit could not be bound.' }
. (Join-Path $PSScriptRoot 'cli-integration-failure-policy.ps1')
. (Join-Path $PSScriptRoot 'cli-transfer-evidence-policy.ps1')
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('MirrorPulse-cli-transfer-' + [guid]::NewGuid().ToString('N'))
$sync = Join-Path $fixture 'sync'; $data = Join-Path $fixture 'data'
$evidenceFile = [IO.Path]::GetFullPath($EvidencePath)
$originals = Join-Path ([IO.Path]::GetDirectoryName($evidenceFile)) 'cli-transfers.originals'
New-Item -ItemType Directory -Path $originals -Force | Out-Null
$savedEnvironment = @{}
foreach ($name in @('MIRRORPULSE_DATA_ROOT', 'MIRRORPULSE_SYNC_ROOT', 'MIRRORPULSE_HOST_PATH', 'MIRRORPULSE_DEVELOPER_MODE')) {
    $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$script:transferCli = $null; $script:transferProbe = $null; $script:ownedTransferHost = $false
$primaryFailure = $null; $phase = 'Setup'; $report = $null
$cases = [Collections.Generic.List[object]]::new()

function Invoke-TransferCli {
    param([Parameter(Mandatory)][AllowEmptyString()][string[]]$Arguments)
    $invocation = @('--developer-mode') + $Arguments
    $output = (& $script:transferCli @invocation 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($output)) {
        throw "The synthetic transfer CLI failed: $($Arguments -join ' '): $output"
    }
    $result = $output | ConvertFrom-Json
    if ($result.kind -cne 'result') { throw 'The transfer CLI returned an unexpected envelope.' }
    return $result
}

function Wait-TransferHostStopped {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    do {
        & $script:transferCli --json --developer-mode --no-start host status 2>$null | Out-Null
        if ($LASTEXITCODE -ne 0) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The synthetic transfer Host did not stop within its lifecycle budget.'
}

function Invoke-TransferProbe {
    param([Parameter(Mandatory)][ValidateSet('audit', 'cleanup')][string]$Mode)
    $start = [Diagnostics.ProcessStartInfo]::new('dotnet')
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    foreach ($argument in @($script:transferProbe, "--$Mode-cli-transfer-fixture", $fixture)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $output = $process.StandardOutput.ReadToEndAsync(); $errorOutput = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { throw "The stopped transfer $Mode oracle timed out." }
        if ($process.ExitCode -ne 0) { throw "The stopped transfer $Mode oracle rejected its fixture: $($errorOutput.GetAwaiter().GetResult())" }
        return ($output.GetAwaiter().GetResult() | ConvertFrom-Json)
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}

function Read-TransferHash {
    param([Parameter(Mandatory)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Wait-TransferHash {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][string]$Expected)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        if ((Test-Path -LiteralPath $Path -PathType Leaf) -and (Read-TransferHash $Path) -ieq $Expected) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw 'The actual transfer or later user write did not produce its expected complete source bytes.'
}

function Assert-TransferRootsReady {
    $result = Invoke-TransferCli @('--json', '--no-start', 'root', 'list')
    foreach ($route in $script:transferRoutes) {
        $actual = @($result.data | Where-Object { $_.root.rootId -ceq $route.rootId })
        if ($actual.Count -ne 1 -or $actual[0].namespaceReady -ne $true -or $actual[0].syncState -cne 'Active' -or
            $null -ne $actual[0].namespaceRecoveryOperationId -or $null -ne $actual[0].namespaceRecoveryCode) {
            throw 'The current Host did not freshly admit every original transfer root.'
        }
    }
}

function Write-TransferOriginal {
    param([Parameter(Mandatory)][string]$Name, [Parameter(Mandatory)][object]$Value)
    $Value | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $originals ($Name + '.json')) -Encoding utf8
}

try {
    if (-not (Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw 'The exact signed Local package is missing.' }
    New-Item -ItemType Directory -Path $sync, $data -Force | Out-Null
    $user = [Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $access = Get-Acl -LiteralPath $sync; $access.SetOwner($user.User); Set-Acl -LiteralPath $sync -AclObject $access
        if ((Get-Acl -LiteralPath $sync).GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $user.User.Value) {
            throw 'The fresh synthetic anchor did not retain its current-user owner.'
        }
    } finally { $user.Dispose() }
    foreach ($key in @('left', 'right', 'target')) { New-Item -ItemType Directory -Path (Join-Path $fixture "source/$key") | Out-Null }
    $payloads = @{}
    foreach ($entry in @(@{name='same-instance.bin';length=32769}, @{name='cross-instance.bin';length=2097409})) {
        $bytes = [byte[]]::new($entry.length); [Random]::new($entry.length).NextBytes($bytes)
        $payloads[$entry.name] = $bytes
        [IO.File]::WriteAllBytes((Join-Path $fixture ('source/left/' + $entry.name)), $bytes)
    }
    foreach ($publish in @(@{project='src/MirrorPulse.Cli/MirrorPulse.Cli.csproj';directory='cli';native=$true},
        @{project='src/MirrorPulse.Host/MirrorPulse.Host.csproj';directory='host';native=$true},
        @{project='tests/MirrorPulse.CfSharp.CrashProbe/MirrorPulse.CfSharp.CrashProbe.csproj';directory='probe';native=$false})) {
        $publishArgs = @('publish', (Join-Path $repository $publish.project), '-c', $Configuration, '--no-restore', '--output', (Join-Path $fixture $publish.directory))
        if ($publish.native) { $publishArgs += @('--runtime', $Runtime, '--self-contained', 'true') } else { $publishArgs += @('--self-contained', 'false') }
        & dotnet @publishArgs
        if ($LASTEXITCODE -ne 0) { throw 'The CLI transfer fixture publish failed.' }
    }
    $script:transferCli = Join-Path $fixture 'cli/mp.exe'
    $script:transferProbe = Join-Path $fixture 'probe/MirrorPulse.CfSharp.CrashProbe.dll'
    $env:MIRRORPULSE_DATA_ROOT = $data; $env:MIRRORPULSE_SYNC_ROOT = $sync
    $env:MIRRORPULSE_HOST_PATH = Join-Path $fixture 'host/MirrorPulse.Host.exe'; $env:MIRRORPULSE_DEVELOPER_MODE = '1'
    $phase = 'HostStartup'
    Invoke-TransferCli @('--json', 'host', 'start') | Out-Null
    $script:ownedTransferHost = $true
    $phase = 'SignedTopology'
    $installation = Invoke-TransferCli @('--json', 'adapter', 'install', '--package', (Resolve-Path $PackagePath).Path)
    $installId = $installation.data.installedAdapterId
    $sourceInstance = Invoke-TransferCli @('--json', 'instance', 'create', '--install-id', $installId, '--name', 'Transfer source',
        '--config', ('root.left.sourceDirectory=' + [IO.Path]::Combine($fixture, 'source', 'left')),
        '--config', ('root.right.sourceDirectory=' + [IO.Path]::Combine($fixture, 'source', 'right')))
    $targetInstance = Invoke-TransferCli @('--json', 'instance', 'create', '--install-id', $installId, '--name', 'Transfer target',
        '--config', ('root.target.sourceDirectory=' + [IO.Path]::Combine($fixture, 'source', 'target')))
    $topology = (Invoke-TransferCli @('--json', '--no-start', 'adapter', 'list')).data
    $installed = @($topology.installations | Where-Object installId -ceq $installId)
    if ($installed.Count -ne 1 -or $installed[0].adapterId -cne 'com.mirrorpulse.adapter.local' -or $installed[0].isSigned -ne $true -or
        @($topology.instances).Count -ne 2 -or @($topology.roots).Count -ne 3) { throw 'The CLI fixture did not retain the exact signed two-instance three-root topology.' }
    $script:transferRoutes = @($topology.roots | ForEach-Object {
        [ordered]@{instanceId=$_.instanceId;rootId=$_.rootId;rootKey=$_.uniquenessKey;directoryName=$_.directoryName}
    })
    $operations = @([guid]::NewGuid().ToString('D'), [guid]::NewGuid().ToString('D'))
    [ordered]@{schemaVersion=1;routes=$script:transferRoutes;transferOperationIds=$operations} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $fixture '.mp-cli-transfer-fixture.json') -Encoding utf8
    Invoke-TransferCli @('--json', 'host', 'restart') | Out-Null; Wait-TransferHostStopped
    Invoke-TransferCli @('--json', 'host', 'start') | Out-Null
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(30)
    do {
        $status = (Invoke-TransferCli @('--json', '--no-start', 'status')).data
        $connected = @($status.instances | Where-Object { $_.instanceId -in @($sourceInstance.data.createdInstanceId, $targetInstance.data.createdInstanceId) -and $_.phase -ceq 'Connected' })
        if ($connected.Count -eq 2) { break }
        Start-Sleep -Milliseconds 100
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    if ($connected.Count -ne 2) { throw 'Both actual signed Local Workers must connect.' }
    Assert-TransferRootsReady
    $workerIds = [Collections.Generic.List[int]]::new()
    $installPrefix = [IO.Path]::GetFullPath($installed[0].installationDirectory).TrimEnd('\') + '\'
    foreach ($process in Get-Process) {
        try {
            $executable = $process.MainModule.FileName
            if ([string]::IsNullOrWhiteSpace($executable)) { continue }
            if (-not $executable.StartsWith($installPrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $modules = @($process.Modules | Where-Object ModuleName -ieq 'coreclr.dll')
            if ($modules.Count -ne 1 -or $modules[0].FileName -ine (Join-Path ([IO.Path]::GetDirectoryName($executable)) 'coreclr.dll')) {
                throw 'The actual signed Worker did not load its private payload runtime.'
            }
            $workerIds.Add($process.Id)
        } catch [ComponentModel.Win32Exception] { } catch [InvalidOperationException] { }
        finally { $process.Dispose() }
    }
    if ($workerIds.Count -ne 2) { throw 'Exactly two actual signed Worker processes must run from the verified installation.' }
    $beforeRestart = (Invoke-TransferCli @('--json', '--no-start', 'host', 'status')).data.processId
    $left = @($script:transferRoutes | Where-Object rootKey -ceq 'left')[0]
    $phase = 'TransferCommands'
    for ($index = 0; $index -lt 2; $index++) {
        $same = $index -eq 0; $scope = if ($same) { 'SameInstance' } else { 'CrossInstance' }
        $sourceName = if ($same) { 'same-instance.bin' } else { 'cross-instance.bin' }
        $targetName = if ($same) { 'same-instance-moved.bin' } else { 'cross-instance-moved.bin' }
        $targetKey = if ($same) { 'right' } else { 'target' }
        $target = @($script:transferRoutes | Where-Object rootKey -ceq $targetKey)[0]
        Get-ChildItem -LiteralPath (Join-Path $sync $left.directoryName) | Out-Null
        $sourceLocal = Join-Path $sync ($left.directoryName + '/' + $sourceName)
        $sourceRemote = Join-Path $fixture ('source/left/' + $sourceName)
        $sourceHash = Read-TransferHash $sourceRemote
        Wait-TransferHash $sourceLocal $sourceHash
        $member = (Invoke-TransferCli @('--json', '--no-start', 'namespace', 'member', 'inspect', ($left.directoryName + '/' + $sourceName))).data
        $parent = (Invoke-TransferCli @('--json', '--no-start', 'namespace', 'member', 'inspect', $target.directoryName, '--directory')).data
        $moved = (Invoke-TransferCli @('--json', '--no-start', 'namespace', 'transfer', 'move', $operations[$index], $member.memberId, $parent.memberId, $targetName)).data
        $targetLocal = Join-Path $sync ($target.directoryName + '/' + $targetName)
        $targetRemote = Join-Path $fixture ('source/' + $targetKey + '/' + $targetName)
        Wait-TransferHash $targetRemote $sourceHash; Wait-TransferHash $targetLocal $sourceHash
        $published = (Invoke-TransferCli @('--json', '--no-start', 'namespace', 'member', 'inspect', ($target.directoryName + '/' + $targetName))).data
        Assert-TransferRootsReady
        $case = [ordered]@{scope=$scope;operationId=$operations[$index];status=$moved;sourceItemId=$member.nativeItemId;targetItemId=$published.nativeItemId
            sourceSha256=$sourceHash;targetLocalSha256=(Read-TransferHash $targetLocal);targetRemoteSha256=(Read-TransferHash $targetRemote)
            sourceLocalMissing=(-not (Test-Path -LiteralPath $sourceLocal));sourceRemoteMissing=(-not (Test-Path -LiteralPath $sourceRemote));freshRootsAfterMove=$true}
        $laterBytes = [Text.Encoding]::UTF8.GetBytes('later target edit ' + $scope)
        [IO.File]::WriteAllBytes($targetLocal, $laterBytes); $case.laterTargetSha256 = Read-TransferHash $targetLocal
        Wait-TransferHash $targetRemote $case.laterTargetSha256
        [IO.File]::WriteAllBytes($sourceRemote, [Text.Encoding]::UTF8.GetBytes('later remote source ' + $scope))
        $case.laterSourceSha256 = Read-TransferHash $sourceRemote
        Invoke-TransferCli @('--json', '--no-start', 'sync', 'refresh') | Out-Null
        Wait-TransferHash $sourceLocal $case.laterSourceSha256
        $newSource = (Invoke-TransferCli @('--json', '--no-start', 'namespace', 'member', 'inspect', ($left.directoryName + '/' + $sourceName))).data
        $case.laterSourceMemberId = $newSource.memberId; $case.laterSourceItemId = $newSource.nativeItemId
        $case.laterSourcePublicIdentityReused = $newSource.nativeItemId -ceq $member.nativeItemId
        Assert-TransferRootsReady
        $case.resumedStatus = (Invoke-TransferCli @('--json', '--no-start', 'namespace', 'transfer', 'resume', $operations[$index])).data
        $case.resumedSourceSha256 = Read-TransferHash $sourceRemote; $case.laterRemoteSourcePreserved = $case.laterSourceSha256 -ieq $case.resumedSourceSha256
        $cases.Add($case); Write-TransferOriginal -Name ('case-' + $scope) -Value $case
    }
    $phase = 'RestartAndHistoricalReplay'
    Invoke-TransferCli @('--json', 'host', 'restart') | Out-Null; Wait-TransferHostStopped
    Invoke-TransferCli @('--json', 'host', 'start') | Out-Null
    $afterRestart = (Invoke-TransferCli @('--json', '--no-start', 'host', 'status')).data.processId
    Assert-TransferRootsReady
    foreach ($case in $cases) {
        $case.freshRootsBeforeReplay = $true
        $case.restartedStatus = (Invoke-TransferCli @('--json', '--no-start', 'namespace', 'transfer', 'resume', $case.operationId)).data
        $case.restartedTargetSha256 = Read-TransferHash (Join-Path $sync $case.status.targetRelativePath)
        $sourceName = if ($case.scope -ceq 'SameInstance') { 'same-instance.bin' } else { 'cross-instance.bin' }
        $case.resumedSourceSha256 = Read-TransferHash (Join-Path $fixture ('source/left/' + $sourceName))
        $case.laterRemoteSourcePreserved = $case.laterSourceSha256 -ieq $case.resumedSourceSha256
    }
    Invoke-TransferCli @('--json', '--no-start', 'host', 'stop') | Out-Null; Wait-TransferHostStopped
    $phase = 'StoppedHostReceipts'
    $audit = Invoke-TransferProbe -Mode audit
    $report = [ordered]@{schemaVersion=1;sourceSha=$sourceSha;runId=$env:GITHUB_RUN_ID;runtime=$Runtime
        signedInstallation=$true;packageSha256=$installed[0].packageSha256;workerProcessIds=$workerIds.ToArray();privateWorkerRuntime=$true
        roots=$script:transferRoutes;cases=$cases.ToArray();audit=$audit;hostProcessBeforeRestart=$beforeRestart;hostProcessAfterRestart=$afterRestart}
    Assert-CliTransferObservations -Evidence ([pscustomobject]$report) -BeforeCleanup
    Write-TransferOriginal -Name 'completed-execution' -Value $report
} catch {
    $primaryFailure = $_
    try {
        Write-TransferOriginal -Name 'execution-failure' -Value ([ordered]@{phase=$phase;sourceSha=$sourceSha;runId=$env:GITHUB_RUN_ID
            type=$_.Exception.GetType().Name;hresult=$_.Exception.HResult.ToString('X8');message=$_.Exception.Message.Replace($fixture, '<fixture>');completedCases=$cases.ToArray()})
    } catch { Write-Host 'The original transfer failure observer was unavailable.' }
    throw $primaryFailure
} finally {
    try {
        Invoke-CliFixtureTeardown -PrimaryFailure $primaryFailure -RecordFailure {
            param($failure)
            Write-TransferOriginal -Name 'cleanup-failure' -Value ([ordered]@{sourceSha=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID
                type=$failure.Exception.GetType().Name;hresult=$failure.Exception.HResult.ToString('X8');message=$failure.Exception.Message.Replace($fixture, '<fixture>')})
        } -Cleanup {
            $ownedHostAlive = $false
            foreach ($candidate in Get-Process -Name 'MirrorPulse.Host' -ErrorAction SilentlyContinue) {
                try { if ($candidate.MainModule.FileName -ieq (Join-Path $fixture 'host/MirrorPulse.Host.exe')) { $ownedHostAlive = $true } }
                catch [ComponentModel.Win32Exception] { } catch [InvalidOperationException] { }
                finally { $candidate.Dispose() }
            }
            if ($script:ownedTransferHost -or $ownedHostAlive) {
                & $script:transferCli --json --developer-mode --no-start host stop 2>$null | Out-Null
                Wait-TransferHostStopped
            }
            if (Test-Path -LiteralPath $fixture) {
                $resolved = [IO.Path]::GetFullPath($fixture)
                if ([IO.Path]::GetDirectoryName($resolved) -ine [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetTempPath()) -or
                    [IO.Path]::GetFileName($resolved) -cnotmatch '\AMirrorPulse-cli-transfer-[0-9a-f]{32}\z' -or
                    (Get-Item -LiteralPath $resolved -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) { throw 'The transfer cleanup target is outside its exact synthetic fixture.' }
                if (-not (Test-Path -LiteralPath (Join-Path $resolved '.mp-cli-transfer-fixture.json'))) { throw 'An incomplete unauthenticated transfer fixture is retained.' }
                $cleanup = Invoke-TransferProbe -Mode cleanup
                if ($cleanup.exclusiveCatalogOwner -ne $true -or $cleanup.protectedNamespace -ne $true -or
                    $cleanup.validatedObjects -lt 4 -or $cleanup.restoredObjects -ne $cleanup.validatedObjects -or
                    $cleanup.cloudRootUnregistered -ne $true) { throw 'The authenticated transfer fixture cleanup did not finish.' }
                Remove-Item -LiteralPath $resolved -Recurse -Force
                if (Test-Path -LiteralPath $resolved) { throw 'The authenticated transfer fixture was not removed.' }
                if ($null -ne $report) { $report.cleanup = $cleanup; $report.fixtureRemoved = $true }
            }
        }
    } finally {
        foreach ($name in $savedEnvironment.Keys) { [Environment]::SetEnvironmentVariable($name, $savedEnvironment[$name], 'Process') }
    }
}
Assert-CliTransferObservations -Evidence ([pscustomobject]$report)
$report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $evidenceFile -Encoding utf8
Write-Output "Verified real signed Local CLI same-instance and cross-instance transfers for $Runtime."
