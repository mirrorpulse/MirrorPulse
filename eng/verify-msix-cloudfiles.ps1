[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [switch]$VerifyShell,
    [string]$EvidencePath
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot 'installed-startup-diagnostics.ps1')
. (Join-Path $PSScriptRoot 'installed-shell-observations.ps1')
. (Join-Path $PSScriptRoot 'installed-shell-process-boundary.ps1')
if (-not $IsWindows) {
    throw "MSIX Cloud Files verification requires Windows."
}
$os = Get-CimInstance -ClassName Win32_OperatingSystem
$version = [version]$os.Version
$osVersion = [version]::new($version.Major, $version.Minor, $version.Build, 0).ToString()
if ($os.ProductType -ne 1 -or [version]$osVersion -lt [version]"10.0.26100.0") {
    throw "Installed MSIX verification requires a supported Windows 11 desktop test user."
}

$project = Join-Path $PSScriptRoot "..\src\MirrorPulse.App\MirrorPulse.App.csproj"
$packageRoot = Join-Path ([IO.Path]::GetTempPath()) "MirrorPulse-msix-$([guid]::NewGuid().ToString('N'))"
$certificate = $null
$package = $null
$installed = $null
$uninstallFailed = $false
$aliasPath = $null
$packageName = "0B72358D-6DC9-479D-8C28-F0232B42A0B3"
$verificationPhase = 'Build'
$hostAttemptedAt = $null
$statusExitCode = $null
$shellAttemptedAt = $null
$probeProcess = $null
$shellOutputPrepared = $false
$shellEntryPointVerified = $false

if (Get-AppxPackage -Name $packageName | Select-Object -First 1) {
    throw "The verification user already has MirrorPulse installed; use a clean test user."
}

try {
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=AppPublisher" `
        -CertStoreLocation "Cert:\CurrentUser\My" -HashAlgorithm SHA256 -KeyLength 2048
    $pfxPath = Join-Path $packageRoot "MirrorPulse.TestSigning.pfx"
    $cerPath = Join-Path $packageRoot "MirrorPulse.TestSigning.cer"
    $passwordText = [guid]::NewGuid().ToString('N')
    $password = ConvertTo-SecureString -String $passwordText -AsPlainText -Force
    Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $password | Out-Null
    Export-Certificate -Cert $certificate -FilePath $cerPath | Out-Null
    Import-Certificate -FilePath $cerPath -CertStoreLocation "Cert:\LocalMachine\TrustedPeople" | Out-Null

    & dotnet build $project --configuration $Configuration --runtime $Runtime `
        -p:GenerateAppxPackageOnBuild=true `
        -p:AppxPackageSigningEnabled=true `
        -p:PackageCertificateThumbprint=$($certificate.Thumbprint) `
        -p:AppxPackageDir="$packageRoot\" `
        -p:MirrorPulseShellProbe=$($VerifyShell.IsPresent.ToString().ToLowerInvariant()) `
        --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "The packaged MirrorPulse.App build failed with exit code $LASTEXITCODE."
    }

    $package = Get-ChildItem -LiteralPath $packageRoot -Filter "*.msix" -File -Recurse |
        Select-Object -First 1
    if ($null -eq $package) {
        throw "The packaged build did not produce an MSIX file."
    }
    $packageSha256 = (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant()

    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
    try {
        $packageEntries = @($archive.Entries | ForEach-Object { $_.FullName.Replace('/', '\') })
        if ($packageEntries -notcontains "mp.exe") {
            throw "The MSIX payload does not contain the root-level mp.exe CLI alias target."
        }
        if ($packageEntries -notcontains "host\MirrorPulse.Host.exe") {
            throw "The MSIX payload does not contain host\MirrorPulse.Host.exe."
        }
    }
    finally {
        $archive.Dispose()
    }

    Add-AppxPackage -Path $package.FullName -ForceApplicationShutdown
    $installed = Get-AppxPackage -Name $packageName |
        Select-Object -First 1
    if ($null -eq $installed) {
        throw "The test MSIX package was not installed for the current user."
    }

    [xml]$manifest = Get-AppxPackageManifest -Package $installed.PackageFullName
    $namespace = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
    $namespace.AddNamespace("f", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $namespace.AddNamespace("desktop3", "http://schemas.microsoft.com/appx/manifest/desktop/windows10/3")
    $namespace.AddNamespace("uap3", "http://schemas.microsoft.com/appx/manifest/uap/windows10/3")
    $namespace.AddNamespace("desktop", "http://schemas.microsoft.com/appx/manifest/desktop/windows10")
    $namespace.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")
    $families = @($manifest.SelectNodes("/f:Package/f:Dependencies/f:TargetDeviceFamily", $namespace))
    if ($families.Count -ne 1 -or $families[0].Name -ne "Windows.Desktop" -or
        $families[0].MinVersion -ne "10.0.26100.0") {
        throw "The installed MSIX product minimum or device family is incorrect."
    }
    $cloudFiles = $manifest.SelectSingleNode(
        "/f:Package/f:Applications/f:Application/f:Extensions/desktop3:Extension[@Category='windows.cloudFiles']",
        $namespace)
    if ($null -eq $cloudFiles) {
        throw "The installed MSIX identity does not expose the windows.cloudFiles extension."
    }
    $adapterAssociation = $manifest.SelectSingleNode(
        "/f:Package/f:Applications/f:Application/f:Extensions/uap:Extension[@Category='windows.fileTypeAssociation']/uap:FileTypeAssociation[uap:SupportedFileTypes/uap:FileType='.mpadapter']",
        $namespace)
    if ($null -eq $adapterAssociation) {
        throw "The installed MSIX does not associate .mpadapter files."
    }
    $executionAlias = $manifest.SelectSingleNode(
        "/f:Package/f:Applications/f:Application/f:Extensions/uap3:Extension[@Category='windows.appExecutionAlias']/uap3:AppExecutionAlias/desktop:ExecutionAlias[@Alias='mp.exe']",
        $namespace)
    if ($null -eq $executionAlias) {
        throw "The installed MSIX does not expose the mp.exe app execution alias."
    }

    $mpCommand = Get-Command mp.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -eq $mpCommand) {
        throw "The installed MSIX did not register the mp.exe app execution alias."
    }
    $aliasPath = $mpCommand.Source
    $versionOutput = (& $mpCommand.Source --version 2>&1 | Out-String).Trim()
    if ($versionOutput -notmatch "MirrorPulse mp") {
        throw "The registered mp.exe alias did not return the CLI version: $versionOutput"
    }
    $verificationPhase = 'HostAutoStart'
    $hostAttemptedAt = [DateTimeOffset]::UtcNow
    $statusOutput = (& $mpCommand.Source --json status 2>&1 | Out-String).Trim()
    $statusExitCode = $LASTEXITCODE
    if ($statusExitCode -ne 0) {
        throw "The installed CLI did not auto-start its packaged Host: $statusOutput"
    }
    $status = $statusOutput | ConvertFrom-Json
    if ($status.kind -ne "result" -or $null -eq $status.data.instances) {
        throw "The packaged CLI status result is invalid."
    }
    & $mpCommand.Source --json host stop 2>$null | Out-Null
    $verificationPhase = 'Shell'

    if ($VerifyShell) {
        $resultPath = Join-Path $packageRoot "packaged-shell-probe.result"
        $appExe = Join-Path $installed.InstallLocation "MirrorPulse.App.exe"
        if (-not (Test-Path -LiteralPath $appExe)) {
            throw "The installed MirrorPulse app executable was not found."
        }

        $shellAttemptedAt = [DateTimeOffset]::UtcNow
        $entryPoint=Get-InstalledManagedEntryPoint -AssemblyPath (Join-Path $installed.InstallLocation 'MirrorPulse.App.dll')
        if ($entryPoint.type -cne 'MirrorPulse.App.MirrorPulsePackagedShellProbeEntryPoint' -or $entryPoint.method -cne 'Main') {
            throw 'The installed App assembly does not contain the requested Shell probe entry point.'
        }
        $shellEntryPointVerified = $true

        Initialize-InstalledShellProbeOutput -ResultPath $resultPath
        $shellOutputPrepared = $true
        $env:MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT = $resultPath
        try {
            $probeProcess = [Diagnostics.Process]::Start((New-InstalledShellProbeStartInfo -ExecutablePath $appExe -ResultPath $resultPath))
            $deadline = [DateTime]::UtcNow.AddMinutes(2)
            $probeObservation = Wait-InstalledShellProbeResult -Process $probeProcess -ResultPath $resultPath -DeadlineUtc $deadline
        }
        finally {
            Remove-Item Env:\MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT -ErrorAction SilentlyContinue
        }

        if ($probeObservation.resultState -cnotin @('Success','Failure')) {
            throw "The packaged App process did not report a Shell probe result."
        }

        $probeResult = (Get-Content -LiteralPath $resultPath -Raw).Trim()
        if ($probeResult -ne "success") {
            throw "The packaged App Shell probe failed: $probeResult"
        }

        Write-Output "Verified packaged Shell registration for $($installed.PackageFullName)."
    }

    Write-Output "Verified installed MSIX $($installed.PackageFullName), mp.exe alias, packaged Host auto-start, Cloud Files, and .mpadapter identities."
}
catch {
    if ($verificationPhase -eq 'HostAutoStart' -and $EvidencePath -and $null -ne $hostAttemptedAt) {
        try {
            $roots=[Collections.Generic.List[string]]::new()
            if ($env:MIRRORPULSE_DATA_ROOT) { $roots.Add([IO.Path]::GetFullPath($env:MIRRORPULSE_DATA_ROOT)) }
            $local=[Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
            $roots.Add((Join-Path $local 'MirrorPulse'))
            if ($null -ne $installed) {
                $packageData=Join-Path (Join-Path $local 'Packages') $installed.PackageFamilyName
                $roots.Add((Join-Path $packageData 'LocalCache/Local/MirrorPulse'))
                $roots.Add((Join-Path $packageData 'LocalState/MirrorPulse'))
                $processes=@(Get-InstalledHostExitObservation -ExpectedExecutable (Join-Path $installed.InstallLocation 'host/MirrorPulse.Host.exe'))
            } else { $processes=@() }
            $diagnostics=Get-InstalledHostStartupDiagnostics -DataRoots $roots.ToArray() -Since $hostAttemptedAt
            $directory=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))
            New-Item -ItemType Directory -Path $directory -Force|Out-Null
            [ordered]@{schemaVersion=1;sourceSha=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID;runtime=$Runtime;
                phase='HostAutoStart';verified=$false;hostAutoStart=$false;cliExitCode=$statusExitCode;
                attemptedAt=$hostAttemptedAt.ToString('O');processes=$processes;diagnostics=$diagnostics}|
                ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $directory 'installed-startup-failure.json') -Encoding utf8
            Write-Host "Installed startup diagnostics retained: $($diagnostics.records.Count) bounded Host records."
        } catch { Write-Host 'Installed startup diagnostics were unavailable; the original verification failure is retained.' }
    }
    if ($verificationPhase -eq 'Shell' -and $VerifyShell -and $EvidencePath -and $null -ne $shellAttemptedAt) {
        try {
            $observation=Get-InstalledShellProbeObservation -Process $probeProcess -ResultPath $resultPath
            $directory=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($EvidencePath))
            New-Item -ItemType Directory -Path $directory -Force|Out-Null
            [ordered]@{schemaVersion=1;sourceSha=$env:GITHUB_SHA;runId=$env:GITHUB_RUN_ID;runtime=$Runtime;
                phase='Shell';verified=$false;shellRegistration=$false;entryPointVerified=$shellEntryPointVerified;outputPrepared=$shellOutputPrepared;attemptedAt=$shellAttemptedAt.ToString('O');probe=$observation;
                processBoundary=(Get-InstalledShellProbeProcessBoundary -Process $probeProcess)}|
                ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $directory 'installed-shell-failure.json') -Encoding utf8
            Write-Host 'Installed Shell failure observations retained.'
        } catch { Write-Host 'Installed Shell observations were unavailable; the original verification failure is retained.' }
    }
    throw
}
finally {
    if ($null -ne $installed) {
        Remove-AppxPackage -Package $installed.PackageFullName -ErrorAction SilentlyContinue
        if (Get-AppxPackage -Name $packageName | Select-Object -First 1) {
            $uninstallFailed = $true
        }
        if ($null -ne $aliasPath -and (Test-Path -LiteralPath $aliasPath)) {
            $uninstallFailed = $true
        }
    }
    if ($null -ne $certificate) {
        Remove-Item -LiteralPath "Cert:\CurrentUser\My\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath "Cert:\LocalMachine\TrustedPeople\$($certificate.Thumbprint)" -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $packageRoot) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
    if ($uninstallFailed) {
        throw "The test MSIX package or mp.exe app execution alias remained registered after uninstall."
    }
}
if ($EvidencePath) {
    [ordered]@{
        schemaVersion=1;runtime=$Runtime;packageSha256=$packageSha256
        osVersion=$osVersion;osProductType=[int]$os.ProductType;minimumVersion="10.0.26100.0"
        installedIdentity=$true;cliAlias=$true;hostAutoStart=$true
        cloudFilesExtension=$true;adapterAssociation=$true;uninstalled=$true
        shellRegistration=$VerifyShell.IsPresent
        shellEntryPointVerified=$shellEntryPointVerified
    } | ConvertTo-Json | Set-Content -LiteralPath $EvidencePath -Encoding utf8
}
