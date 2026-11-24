[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$EvidencePath,
    [ValidateSet('X64', 'Arm64')][string]$ExpectedArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString(),
    [string]$DirectLocalPackagePath,
    [ValidatePattern('\A[0-9A-Fa-f]{64}\z')][string]$DirectLocalPackageSha256
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installed-process-images.ps1')
. (Join-Path $PSScriptRoot 'installed-startup-diagnostics.ps1')
$os = Get-CimInstance Win32_OperatingSystem
$architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
if ($os.ProductType -ne 1 -or [version]$os.Version -lt [version]'10.0.26100.0' -or
    $architecture -ine $ExpectedArchitecture -or $architecture -notin @('X64', 'Arm64')) {
    throw "Edition coexistence verification requires a clean Windows 11 $ExpectedArchitecture desktop test account."
}
$runtime = 'win-' + $architecture.ToLowerInvariant()
$localAdapterId = 'com.mirrorpulse.adapter.local'
if ([bool]$DirectLocalPackagePath -xor [bool]$DirectLocalPackageSha256) {
    throw 'A direct Local edition requires both the published package path and its SHA256.'
}
$directPackage = $null
$directManifest = $null
if ($DirectLocalPackagePath) {
    $directPackage = Get-Item -LiteralPath $DirectLocalPackagePath
    if ($directPackage.PSIsContainer -or $directPackage.Extension -ine '.mpadapter' -or
        (Get-FileHash -LiteralPath $directPackage.FullName).Hash -ine $DirectLocalPackageSha256) {
        throw 'The direct Local package does not match its published SHA256.'
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($directPackage.FullName)
    try {
        $entry = $archive.GetEntry('manifest.json')
        if (!$entry -or $entry.Length -gt 32KB) { throw 'The direct Local package has no bounded manifest.' }
        $reader = [IO.StreamReader]::new($entry.Open())
        try { $directManifest = $reader.ReadToEnd() | ConvertFrom-Json } finally { $reader.Dispose() }
    } finally { $archive.Dispose() }
    if ($directManifest.adapterId -cne $localAdapterId -or $directManifest.schemaVersion -ne 2 -or
        $directManifest.instancePolicy.maximumRootDefinitions -ne 1 -or @($directManifest.rootDefinitions).Count -ne 1 -or
        !$directManifest.presentation -or !$directManifest.entrypoints.$runtime) {
        throw 'The direct edition needs a published schema 2 Local package with one root, a signed form and a matching Worker.'
    }
}
$workspace = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$sourceSha = git -C $workspace rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sourceSha -cnotmatch '^[a-f0-9]{40}$' -or
    ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $sourceSha)) {
    throw 'Could not bind the packages to the selected source commit.'
}
$temp = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$scratch = Join-Path $temp ('mp-editions-' + [guid]::NewGuid().ToString('N').Substring(0, 12))
$appProject = Join-Path $workspace 'src/MirrorPulse.App/MirrorPulse.App.csproj'
$probeProject = Join-Path $workspace 'tests/MirrorPulse.CfSharp.CrashProbe/MirrorPulse.CfSharp.CrashProbe.csproj'
$probeDirectory = Join-Path $scratch 'probe'
$probeAssembly = Join-Path $probeDirectory 'MirrorPulse.CfSharp.CrashProbe.dll'
$solution = Join-Path $workspace 'MirrorPulse.sln'
$local = [Environment]::GetFolderPath('LocalApplicationData')
$user = [Environment]::GetFolderPath('UserProfile')
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try { $sid = $identity.User.Value } finally { $identity.Dispose() }
$standard = @{
    Prefix='MirrorPulse'; PackageName='0B72358D-6DC9-479D-8C28-F0232B42A0B3'; Alias='mp.exe'
    Name='MirrorPulse'; EnvironmentPrefix='MIRRORPULSE_'; Fixture='standard'
}
$custom = @{
    Prefix='MirrorPulse.sample-drive'; PackageName='MirrorPulse.SampleDrive'; Alias='sample-drive.exe'
    ProviderId='7e2494ee-f27a-44cd-a564-15dc2eee476e'; Name='Sample Drive'; EnvironmentPrefix='MIRRORPULSE_EDITION_SAMPLE_DRIVE_'; Fixture='custom'
}
$editions = @($standard, $custom)
$evidence = [ordered]@{ schemaVersion=1; sourceSha=$sourceSha; runtime=$runtime; phase='Preflight'; verified=$false; hosts=@() }
$hostAttempt = $null
$directInstallId = $null
$directInstanceId = $null
$directRootId = $null

function Invoke-ShellCheck($Edition, [switch]$Unregister) {
    $action = if ($Unregister) { 'unregister' } else { 'read' }
    $result = (& dotnet $probeAssembly --edition-shell-registration $Edition.Fixture $action | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $result -cnotin @('Present', 'Absent')) {
        throw 'Could not verify an edition Shell registration.'
    }
    $result
}

function Invoke-EditionCli($Edition, [string[]]$Arguments) {
    $result = & $Edition.AliasPath --json @Arguments
    if ($LASTEXITCODE -ne 0) { throw "The $($Edition.Name) CLI command failed: $($Arguments -join ' ')." }
    $response = $result | ConvertFrom-Json
    if ($response.kind -cne 'result') { throw 'The installed CLI did not return a result.' }
    $response.data
}

function Assert-EditionProcess($Edition, [int]$ProcessId, [string]$RelativePath) {
    $process = [Diagnostics.Process]::GetProcessById($ProcessId)
    try {
        [uint32]$nameLength = 0
        $result = [MirrorPulse.InstalledProcessImageNative]::GetPackageFullName($process.SafeHandle, [ref]$nameLength, $null)
        if ($result -ne 122 -or $nameLength -lt 2 -or $nameLength -gt 4096) { throw 'The process has no Windows package identity.' }
        $name = [Text.StringBuilder]::new([int]$nameLength)
        $result = [MirrorPulse.InstalledProcessImageNative]::GetPackageFullName($process.SafeHandle, [ref]$nameLength, $name)
        $image = [Text.StringBuilder]::new(32768)
        [uint32]$imageLength = $image.Capacity
        if ($result -ne 0 -or $name.ToString() -cne $Edition.Installed.PackageFullName -or
            -not [MirrorPulse.InstalledProcessImageNative]::QueryFullProcessImageNameW($process.SafeHandle, 0, $image, [ref]$imageLength) -or
            $image.ToString() -ine (Join-Path $Edition.Installed.InstallLocation $RelativePath)) {
            throw 'The process came from a different installed edition.'
        }
    } finally { $process.Dispose() }
}

function Get-EditionHost($Edition) {
    $hostStatus = Invoke-EditionCli $Edition @('--no-start', 'host', 'status')
    if ($hostStatus.state -cne 'Running' -or $hostStatus.processId -le 0 -or
        $hostStatus.controlPipeName -cne "$($Edition.Prefix)-control-v1-$sid") {
        throw 'The installed CLI is not connected to the expected running edition Host.'
    }
    Assert-EditionProcess $Edition $hostStatus.processId 'host\MirrorPulse.Host.exe'
    $hostStatus.processId
}

function Start-EditionWindow($Edition) {
    $processId = [MirrorPulse.EditionActivation]::Start($Edition.Installed.PackageFamilyName + '!App')
    Assert-EditionProcess $Edition $processId 'MirrorPulse.App.exe'
    $process = [Diagnostics.Process]::GetProcessById($processId)
    try {
        $deadline = [DateTime]::UtcNow.AddSeconds(20)
        do {
            $process.Refresh()
            if ($process.HasExited) { throw 'The activated edition exited before opening its window.' }
            if ($process.MainWindowHandle -ne [IntPtr]::Zero) { return $processId }
            Start-Sleep -Milliseconds 200
        } while ([DateTime]::UtcNow -lt $deadline)
        throw 'The activated edition did not open a window.'
    } finally { $process.Dispose() }
}

function Build-EditionPackage([string]$PackageDirectory, [string[]]$Overrides) {
    & dotnet build $appProject --configuration Release --runtime $runtime --no-restore `
        -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
        "-p:PackageCertificateThumbprint=$($certificate.Thumbprint)" "-p:AppxPackageDir=$PackageDirectory/" @Overrides | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'An edition MSIX build failed.' }
    $packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Directory | ForEach-Object {
        Get-ChildItem -LiteralPath $_.FullName -Filter '*.msix' -File
    })
    if ($packages.Count -ne 1) { throw 'Expected one product MSIX for an edition.' }
    $packages[0]
}

function Assert-DirectDeployment($Edition, [string]$InstallId, [string]$InstanceId, [string]$RootId, [string]$Source, [string]$Label) {
    $topology = Invoke-EditionCli $Edition @('--no-start', 'adapter', 'list')
    if (@($topology.installations).Count -ne 1 -or $topology.installations[0].installId -cne $InstallId -or
        @($topology.instances).Count -ne 1 -or $topology.instances[0].instanceId -cne $InstanceId -or
        $topology.instances[0].configuration.sourceDirectory -ine $Source -or @($topology.roots).Count -ne 1 -or
        $topology.roots[0].rootId -cne $RootId -or $topology.roots[0].label -cne $Label) {
        throw 'The signed direct Local deployment and root did not survive the lifecycle change.'
    }
}

$certificate = $null
$cleanupFailed = $false
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    & dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed for the standard edition and registration probe.' }
    & dotnet build $probeProject --configuration Release --no-restore --output $probeDirectory
    if ($LASTEXITCODE -ne 0) { throw 'The edition registration probe did not build.' }
    foreach ($edition in $editions) {
        $edition.AliasPath = Join-Path $local "Microsoft/WindowsApps/$($edition.Alias)"
        if ((Get-AppxPackage -Name $edition.PackageName) -or (Test-Path -LiteralPath $edition.AliasPath) -or
            (Test-Path -LiteralPath (Join-Path $user $edition.Prefix)) -or
            (Test-Path -LiteralPath (Join-Path $local $edition.Prefix)) -or (Invoke-ShellCheck $edition) -cne 'Absent' -or
            [Environment]::GetEnvironmentVariable($edition.EnvironmentPrefix + 'SYNC_ROOT') -or
            [Environment]::GetEnvironmentVariable($edition.EnvironmentPrefix + 'DATA_ROOT')) {
            throw 'Use a clean test account with the default paths for both editions.'
        }
    }

    Initialize-InstalledProcessImageReader
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

namespace MirrorPulse {
    [ComImport, Guid("2e941141-7f97-4756-ba1d-9decde894a3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IApplicationActivationManager {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string applicationId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, int options, out uint processId);
    }

    public static class EditionActivation {
        public static int Start(string applicationId) {
            var type = Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c"), true);
            var manager = (IApplicationActivationManager)Activator.CreateInstance(type);
            try {
                uint processId;
                manager.ActivateApplication(applicationId, null, 0, out processId);
                return checked((int)processId);
            } finally { Marshal.ReleaseComObject(manager); }
        }
    }
}
'@
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=AppPublisher' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -HashAlgorithm SHA256 -KeyLength 2048
    $cer = Join-Path $scratch 'signing.cer'
    Export-Certificate -Cert $certificate -FilePath $cer | Out-Null
    Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
    $profile = Join-Path $scratch 'edition.props'
    $directProfile = ''
    if ($directPackage) {
        $stagedPackage = Join-Path $scratch 'local.mpadapter'
        Copy-Item -LiteralPath $directPackage.FullName -Destination $stagedPackage
        if ((Get-FileHash -LiteralPath $stagedPackage).Hash -ine $DirectLocalPackageSha256) {
            throw 'The direct Local package changed before staging the MSIX.'
        }
        $signature = $directPackage.FullName + '.signature.json'
        if (Test-Path -LiteralPath $signature -PathType Leaf) {
            Copy-Item -LiteralPath $signature -Destination ($stagedPackage + '.signature.json')
        }
        $escapedPackage = [Security.SecurityElement]::Escape($stagedPackage)
        $directProfile = @"
  <PropertyGroup>
    <MirrorPulseEditionDirectAdapter>true</MirrorPulseEditionDirectAdapter>
  </PropertyGroup>
  <ItemGroup>
    <MirrorPulseEditionDefaultAdapter Include="$escapedPackage" AdapterId="$localAdapterId" />
  </ItemGroup>
"@
    }
    @"
<Project>
  <PropertyGroup>
    <MirrorPulseEditionId>sample-drive</MirrorPulseEditionId>
    <MirrorPulseEditionName>$($custom.Name)</MirrorPulseEditionName>
    <MirrorPulseEditionProviderId>$($custom.ProviderId)</MirrorPulseEditionProviderId>
    <MirrorPulseEditionCommand>sample-drive</MirrorPulseEditionCommand>
    <MirrorPulseEditionPackageName>$($custom.PackageName)</MirrorPulseEditionPackageName>
    <MirrorPulseEditionPublisher>CN=AppPublisher</MirrorPulseEditionPublisher>
    <MirrorPulseEditionPublisherName>MirrorPulse Team</MirrorPulseEditionPublisherName>
  </PropertyGroup>
$directProfile
</Project>
"@ | Set-Content -LiteralPath $profile -Encoding utf8
    $customBuild = @("-p:MirrorPulseEditionProfile=$profile", "-p:ArtifactsPath=$(Join-Path $scratch 'ed')")
    foreach ($edition in $editions) {
        $evidence.phase = "Build $($edition.Name)"
        $overrides = @()
        if ($edition -eq $custom) {
            $overrides = $customBuild
            & dotnet restore $solution --locked-mode @overrides
            if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed for the custom edition.' }
        }
        $package = Build-EditionPackage (Join-Path $scratch $edition.Prefix) $overrides
        $edition.Sha256 = (Get-FileHash -LiteralPath $package.FullName).Hash
        $evidence.phase = "Install $($edition.Name)"
        $edition.InstallAttempted = $true
        Add-AppxPackage -Path $package.FullName
        $edition.Installed = Get-AppxPackage -Name $edition.PackageName
        if (-not $edition.Installed -or $edition.Installed.Architecture.ToString() -ine $architecture -or
            -not (Test-Path -LiteralPath $edition.AliasPath)) {
            throw 'The edition did not register its CLI alias and expected package architecture.'
        }
        $version = & $edition.AliasPath --version
        $expectedBanner = "$($edition.Name) $([IO.Path]::GetFileNameWithoutExtension($edition.Alias)) "
        if ($LASTEXITCODE -ne 0 -or -not $version.StartsWith($expectedBanner, [StringComparison]::Ordinal)) {
            throw 'The installed CLI alias resolved to the wrong edition.'
        }
    }

    foreach ($edition in $editions) {
        $evidence.phase = 'Host startup ' + $edition.Name
        $hostAttempt = [DateTimeOffset]::UtcNow
        $null = Invoke-EditionCli $edition @('status')
        $edition.HostId = Get-EditionHost $edition
        $hostAttempt = $null
        $evidence.phase = 'Shell registration ' + $edition.Name
        if ((Invoke-ShellCheck $edition) -cne 'Present') { throw 'A running edition has no Shell root.' }
        $evidence.hosts += @{ identity=$edition.PackageName; packageBound=$true; shellRegistered=$true }
    }
    if ($standard.HostId -eq $custom.HostId) { throw 'Both CLI aliases route to the same Host.' }
    if ($directPackage) {
        $evidence.phase = 'Configure the signed direct Local bundle'
        $embedded = Join-Path $custom.Installed.InstallLocation "host/edition-adapters/adapter.$localAdapterId.mpadapter"
        if ((Get-FileHash -LiteralPath $embedded).Hash -ine $DirectLocalPackageSha256) {
            throw 'The installed MSIX does not contain the frozen Local package.'
        }
        $customTopology = Invoke-EditionCli $custom @('--no-start', 'adapter', 'list')
        $installations = @($customTopology.installations)
        if ($installations.Count -ne 1 -or $installations[0].adapterId -cne $localAdapterId -or
            $installations[0].version -cne $directManifest.version -or $installations[0].packageSha256 -ine $DirectLocalPackageSha256 -or
            !$installations[0].isSigned -or !$installations[0].form.presentation -or $installations[0].source -cne 'EditionBundle' -or
            @($customTopology.instances).Count -ne 0) {
            throw 'The custom Host did not import the exact signed Local default for first configuration.'
        }
        $directInstallId = $installations[0].installId
        $source = Join-Path $scratch 'direct-source'
        New-Item -ItemType Directory -Path $source | Out-Null
        $custom.WindowId = Start-EditionWindow $custom
        $directName = 'Direct Local'
        $directLabel = 'Direct Local folder'
        $uiProbe = Join-Path $PSScriptRoot 'verify-direct-edition-ui.ps1'
        & powershell.exe -NoLogo -NoProfile -NonInteractive -File $uiProbe -ProcessId $custom.WindowId `
            -SourcePath $source -RootKey ($directManifest.rootDefinitions[0].key) -RootLabel $directLabel -InstanceName $directName
        if ($LASTEXITCODE -ne 0) { throw 'The installed WinUI direct setup did not complete.' }
        $customTopology = Invoke-EditionCli $custom @('--no-start', 'adapter', 'list')
        $standardTopology = Invoke-EditionCli $standard @('--no-start', 'adapter', 'list')
        $directInstanceId = @($customTopology.instances)[0].instanceId
        if (!$directInstanceId -or @($customTopology.instances).Count -ne 1 -or @($customTopology.roots).Count -ne 1 -or
            $customTopology.instances[0].displayName -cne $directName -or $customTopology.instances[0].installId -cne $directInstallId -or
            $customTopology.roots[0].instanceId -cne $directInstanceId -or
            $customTopology.roots[0].uniquenessKey -cne $directManifest.rootDefinitions[0].key -or
            $customTopology.roots[0].label -cne $directLabel -or
            $standardTopology.installations.installId -contains $directInstallId -or
            $standardTopology.instances.instanceId -contains $directInstanceId) {
            throw 'The signed single-root deployment was not saved independently in the custom edition.'
        }
        $directRootId = $customTopology.roots[0].rootId
    }
    $evidence.phase = 'Independent configuration'
    $standardBefore = (Invoke-EditionCli $standard @('config')).locale
    $customBefore = (Invoke-EditionCli $custom @('config')).locale
    $updated = if ($customBefore -ceq 'zh-CN') { 'en-US' } else { 'zh-CN' }
    $null = Invoke-EditionCli $custom @('config', '--locale', $updated)
    if ((Invoke-EditionCli $custom @('config')).locale -cne $updated -or
        (Invoke-EditionCli $standard @('config')).locale -cne $standardBefore) {
        throw 'A configuration write did not stay within the selected edition.'
    }
    $evidence.configurationIsolated = $true
    $firstCustomHost = $custom.HostId
    $evidence.phase = 'Custom restart'
    $restarting = Invoke-EditionCli $custom @('host', 'restart')
    if ($restarting.processId -ne $firstCustomHost -or (Get-Process -Id $firstCustomHost -ErrorAction SilentlyContinue)) {
        throw 'The restart request did not drain the original custom Host.'
    }
    # A normal command starts the next Host after the old one drains.
    $null = Invoke-EditionCli $custom @('status')
    $custom.HostId = Get-EditionHost $custom
    if ($custom.HostId -eq $firstCustomHost -or (Get-EditionHost $standard) -ne $standard.HostId -or
        (Invoke-EditionCli $custom @('config')).locale -cne $updated -or (Invoke-ShellCheck $custom) -cne 'Present') {
        throw 'Restart did not preserve the custom configuration independently of the standard Host.'
    }
    $evidence.customRestartIsolated = $true
    if ($directPackage) {
        Assert-DirectDeployment $custom $directInstallId $directInstanceId $directRootId $source $directLabel
        $evidence.directLocal = @{ version=$directManifest.version; packageSha256=$DirectLocalPackageSha256.ToLowerInvariant(); winUiConfigured=$true; persisted=$true }
        $evidence.phase = 'Verify the registered Local root'
        $deadline = [DateTime]::UtcNow.AddSeconds(60)
        do {
            $rootView = @(Invoke-EditionCli $custom @('--no-start', 'root', 'list') |
                Where-Object { $_.root.rootId -ceq $directRootId })
            $deployment = @((Invoke-EditionCli $custom @('--no-start', 'status')).instances |
                Where-Object instanceId -CEQ $directInstanceId)
            $evidence.directLocal.readiness = @{
                rootState=$rootView.root.state; namespaceReady=$rootView.namespaceReady
                namespaceRecoveryCode=$rootView.namespaceRecoveryCode; enabled=$deployment.enabled
                workerConnected=$deployment.workerConnected; workerPhase=$deployment.phase; workerErrorCode=$deployment.lastErrorCode
            }
            if ($rootView.Count -eq 1 -and $rootView[0].namespaceReady -eq $true) { break }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'The signed Local root did not become ready with its Worker.' }
            Start-Sleep -Milliseconds 250
        } while ($true)
        & powershell.exe -NoLogo -NoProfile -NonInteractive -File $uiProbe -ProcessId $custom.WindowId -VerifyRegisteredRoot `
            -SourcePath $source -RootKey ($directManifest.rootDefinitions[0].key) -RootLabel $directLabel -InstanceName $directName
        if ($LASTEXITCODE -ne 0) { throw 'The installed WinUI form did not protect the registered root.' }
        Assert-DirectDeployment $custom $directInstallId $directInstanceId $directRootId $source $directLabel
        $evidence.directLocal.registeredRootProtected = $true

        $evidence.phase = 'Verify optional update discovery for official Local'
        $deadline = [DateTime]::UtcNow.AddSeconds(40)
        do {
            $deployment = @((Invoke-EditionCli $custom @('--no-start', 'status')).instances |
                Where-Object instanceId -CEQ $directInstanceId)
            if ($deployment.Count -eq 1 -and $deployment[0].workerConnected -eq $true) { break }
            if ([DateTime]::UtcNow -ge $deadline) { throw 'The signed Local Worker did not connect for update discovery.' }
            Start-Sleep -Milliseconds 250
        } while ($true)
        $update = Invoke-EditionCli $custom @('--no-start', 'instance', 'check-update', '--instance-id', $directInstanceId)
        if ($update.availability -cnotin @('Unsupported', 'Current') -or $update.currentVersion -cne $directManifest.version -or
            $null -ne $update.offerId -or $null -ne $update.availableVersion) {
            throw 'The optional update check did not return an accurate no-offer result for the official Local Worker.'
        }
        Assert-DirectDeployment $custom $directInstallId $directInstanceId $directRootId $source $directLabel
        $evidence.directLocal.optionalUpdate = @{ availability=$update.availability; currentVersion=$update.currentVersion }
    }

    $evidence.phase = 'Concurrent application activation'
    $standard.WindowId = Start-EditionWindow $standard
    if (!$custom.WindowId) { $custom.WindowId = Start-EditionWindow $custom }
    Assert-EditionProcess $custom $custom.WindowId 'MirrorPulse.App.exe'
    Assert-EditionProcess $standard $standard.WindowId 'MirrorPulse.App.exe'
    if ($standard.WindowId -eq $custom.WindowId) { throw 'Both application activations returned the same process.' }
    $evidence.concurrentApps = $true

    if ($directPackage) {
        $evidence.phase = 'Upgrade the running custom edition'
        $previousPackage = $custom.Installed
        $previousVersion = [version]$previousPackage.Version
        $nextVersion = [version]::new($previousVersion.Major, $previousVersion.Minor, ($previousVersion.Build + 1), 0)
        $upgrade = Build-EditionPackage (Join-Path $scratch 'upgrade') ($customBuild + "-p:MirrorPulseEditionPackageVersion=$nextVersion")
        $upgradeHash = (Get-FileHash -LiteralPath $upgrade.FullName).Hash
        $windowProcess = [Diagnostics.Process]::GetProcessById($custom.WindowId)
        try {
            if (!$windowProcess.CloseMainWindow() -or !$windowProcess.WaitForExit(20000)) {
                throw 'The original custom WinUI process did not close before upgrade.'
            }
        } finally { $windowProcess.Dispose() }
        $null = Invoke-EditionCli $custom @('--no-start', 'host', 'stop')
        if (Get-Process -Id $custom.HostId -ErrorAction SilentlyContinue) { throw 'The original custom Host did not stop before upgrade.' }

        Add-AppxPackage -Path $upgrade.FullName
        $custom.Installed = Get-AppxPackage -Name $custom.PackageName
        if (!$custom.Installed -or [version]$custom.Installed.Version -ne $nextVersion -or
            $custom.Installed.PackageFamilyName -cne $previousPackage.PackageFamilyName -or
            $custom.Installed.Architecture.ToString() -ine $architecture) { throw 'The same custom family did not upgrade to the new version.' }
        $null = Invoke-EditionCli $custom @('status')
        $custom.HostId = Get-EditionHost $custom
        $custom.WindowId = Start-EditionWindow $custom
        Assert-DirectDeployment $custom $directInstallId $directInstanceId $directRootId $source $directLabel
        if ((Invoke-EditionCli $custom @('config')).locale -cne $updated -or (Invoke-ShellCheck $custom) -cne 'Present' -or
            (Get-EditionHost $standard) -ne $standard.HostId -or (Invoke-ShellCheck $standard) -cne 'Present') {
            throw 'The edition upgrade failed to preserve independent configuration and Shell registrations.'
        }
        Assert-EditionProcess $standard $standard.WindowId 'MirrorPulse.App.exe'
        $evidence.directLocal.upgrade = @{ previousVersion=$previousVersion.ToString(); installedVersion=$nextVersion.ToString()
            packageSha256=$upgradeHash; retainedDeployment=$true; standardStillRunning=$true }
    }

    $evidence.phase = 'Custom edition uninstall'
    $null = Invoke-EditionCli $custom @('--no-start', 'host', 'stop')
    if ((Invoke-ShellCheck $custom -Unregister) -cne 'Absent') { throw 'Could not remove the test registration.' }
    Remove-AppxPackage -Package $custom.Installed.PackageFullName
    if ((Get-AppxPackage -Name $custom.PackageName) -or (Test-Path -LiteralPath $custom.AliasPath) -or
        (Get-EditionHost $standard) -ne $standard.HostId -or (Invoke-ShellCheck $standard) -cne 'Present') {
        throw 'Uninstall left the custom alias or disturbed the running standard edition.'
    }
    Assert-EditionProcess $standard $standard.WindowId 'MirrorPulse.App.exe'
    $evidence.standardStillRunningAfterCustomUninstall = $true
    $evidence.packages = @($editions | ForEach-Object { @{ identity=$_.PackageName; alias=$_.Alias; sha256=$_.Sha256 } })
    $evidence.phase = 'Complete'
    $evidence.verified = $true
} catch {
    if ($hostAttempt -and $edition.Installed) {
        try {
            $packageData = Join-Path (Join-Path $local 'Packages') $edition.Installed.PackageFamilyName
            $roots = @((Join-Path $local $edition.Prefix),
                (Join-Path $packageData "LocalCache/Local/$($edition.Prefix)"),
                (Join-Path $packageData "LocalState/$($edition.Prefix)"))
            $evidence.startupDiagnostics = Get-InstalledHostStartupDiagnostics -DataRoots $roots -Since $hostAttempt
            $evidence.processObservations = @(Get-InstalledHostExitObservation -ExpectedExecutable `
                (Join-Path $edition.Installed.InstallLocation 'host/MirrorPulse.Host.exe'))
        } catch { Write-Warning 'Bounded Host startup diagnostics could not be collected.' }
    }
    throw
} finally {
    foreach ($edition in $editions) {
        try {
            $installed = if ($edition.InstallAttempted) { Get-AppxPackage -Name $edition.PackageName -PackageTypeFilter Main }
            if ($installed) {
                if ($edition.HostId -and (Get-Process -Id $edition.HostId -ErrorAction SilentlyContinue)) {
                    $null = Invoke-EditionCli $edition @('--no-start', 'host', 'stop')
                }
                $null = Invoke-ShellCheck $edition -Unregister
                Remove-AppxPackage -Package $installed.PackageFullName
                if ((Get-AppxPackage -Name $edition.PackageName) -or (Test-Path -LiteralPath $edition.AliasPath)) {
                    throw 'The edition or its alias remained installed.'
                }
            }
        } catch { $cleanupFailed = $true; Write-Warning "Could not clean up $($edition.Name) verification state." }
    }
    if ($certificate) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
    }
    $evidence.cleanedUp = -not $cleanupFailed
    if ($cleanupFailed) { $evidence.verified = $false }
    $evidence | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
    if ($cleanupFailed) { throw 'The edition coexistence fixture was not fully uninstalled.' }
}
