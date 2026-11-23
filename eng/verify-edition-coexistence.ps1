[CmdletBinding()]
param([Parameter(Mandatory)][string]$EvidencePath)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'installed-process-images.ps1')
$os = Get-CimInstance Win32_OperatingSystem
if ($os.ProductType -ne 1 -or [version]$os.Version -lt [version]'10.0.26100.0' -or
    [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'Arm64') {
    throw 'Edition coexistence verification requires a clean Windows 11 ARM64 desktop test account.'
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
$evidence = [ordered]@{ schemaVersion=1; sourceSha=$sourceSha; runtime='win-arm64'; phase='Preflight'; verified=$false }

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
        $packageDirectory = Join-Path $scratch $edition.Prefix
        & dotnet build $appProject --configuration Release --runtime win-arm64 --no-restore `
            -p:GenerateAppxPackageOnBuild=true -p:AppxPackageSigningEnabled=true `
            "-p:PackageCertificateThumbprint=$($certificate.Thumbprint)" "-p:AppxPackageDir=$packageDirectory/" @overrides
        if ($LASTEXITCODE -ne 0) { throw 'An edition MSIX build failed.' }
        $packages = @(Get-ChildItem -LiteralPath $packageDirectory -Directory | ForEach-Object {
            Get-ChildItem -LiteralPath $_.FullName -Filter '*.msix' -File
        })
        if ($packages.Count -ne 1) { throw 'Expected one product MSIX for an edition.' }
        $edition.Sha256 = (Get-FileHash -LiteralPath $packages[0].FullName).Hash
        $evidence.phase = "Install $($edition.Name)"
        $edition.InstallAttempted = $true
        Add-AppxPackage -Path $packages[0].FullName
        $edition.Installed = Get-AppxPackage -Name $edition.PackageName
        if (-not $edition.Installed -or -not (Test-Path -LiteralPath $edition.AliasPath)) {
            throw 'The installed edition or its CLI alias was not registered.'
        }
        $version = & $edition.AliasPath --version
        $expectedBanner = "$($edition.Name) $([IO.Path]::GetFileNameWithoutExtension($edition.Alias)) "
        if ($LASTEXITCODE -ne 0 -or -not $version.StartsWith($expectedBanner, [StringComparison]::Ordinal)) {
            throw 'The installed CLI alias resolved to the wrong edition.'
        }
    }

    $evidence.phase = 'Concurrent Hosts and Shell roots'
    foreach ($edition in $editions) {
        $null = Invoke-EditionCli $edition @('status')
        $edition.HostId = Get-EditionHost $edition
        if ((Invoke-ShellCheck $edition) -cne 'Present') { throw 'A running edition has no Shell root.' }
    }
    if ($standard.HostId -eq $custom.HostId) { throw 'Both CLI aliases route to the same Host.' }
    $standardBefore = (Invoke-EditionCli $standard @('config')).locale
    $customBefore = (Invoke-EditionCli $custom @('config')).locale
    $updated = if ($customBefore -ceq 'zh-CN') { 'en-US' } else { 'zh-CN' }
    $null = Invoke-EditionCli $custom @('config', '--locale', $updated)
    if ((Invoke-EditionCli $custom @('config')).locale -cne $updated -or
        (Invoke-EditionCli $standard @('config')).locale -cne $standardBefore) {
        throw 'A configuration write did not stay within the selected edition.'
    }
    $firstCustomHost = $custom.HostId
    $null = Invoke-EditionCli $custom @('host', 'restart')
    $custom.HostId = Get-EditionHost $custom
    if ($custom.HostId -eq $firstCustomHost -or (Get-EditionHost $standard) -ne $standard.HostId -or
        (Invoke-EditionCli $custom @('config')).locale -cne $updated -or (Invoke-ShellCheck $custom) -cne 'Present') {
        throw 'Restart did not preserve the custom configuration independently of the standard Host.'
    }
    $evidence.configurationIsolated = $true

    $evidence.phase = 'Concurrent application activation'
    $standard.WindowId = Start-EditionWindow $standard
    $custom.WindowId = Start-EditionWindow $custom
    Assert-EditionProcess $standard $standard.WindowId 'MirrorPulse.App.exe'
    if ($standard.WindowId -eq $custom.WindowId) { throw 'Both application activations returned the same process.' }
    $evidence.concurrentApps = $true

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
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
    if ($cleanupFailed) { throw 'The edition coexistence fixture was not fully uninstalled.' }
}
