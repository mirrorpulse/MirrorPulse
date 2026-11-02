# Explicit installed-verifier evidence only. Read known child process handles and
# allowlisted package executables; never enumerate processes, arbitrary modules,
# command lines, tokens, memory or user data. No artifact executable is launched here.
$script:InstalledProcessImagePolicyPath=$PSCommandPath
$script:InstalledProcessImagePolicySha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash
function Initialize-InstalledProcessImageReader {
    if ($null -ne ('MirrorPulse.InstalledProcessImageNative' -as [type])) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace MirrorPulse {
    public static class InstalledProcessImageNative {
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true, SetLastError=true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode, ExactSpelling=true)]
        public static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, StringBuilder name);
    }
}
'@
}

function Get-InstalledProcessImageBinding {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process,
        [Parameter(Mandatory)]$Prepared,
        [Parameter(Mandatory)][ValidateSet('App','Cli','Host','Fixture')][string]$Role,
        [Parameter(Mandatory)][string]$RelativePath,
        [AllowEmptyString()][Parameter(Mandatory)][string]$ExpectedPackageFullName,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$PreparedSha256)
    Initialize-InstalledProcessImageReader
    if ($Prepared.schemaVersion -ne 1 -or $Prepared.sourceSha -cnotmatch '^[0-9a-f]{40}$' -or
        $RelativePath -cnotmatch '^[A-Za-z0-9./-]+\.exe$' -or $RelativePath.Contains('..')) { throw 'Invalid original installed process binding request.' }
    $required = @{ App='MirrorPulse.App.exe';Cli='mp.exe';Host='host/MirrorPulse.Host.exe' }
    if ($Role -cne 'Fixture' -and ($required[$Role] -cne $RelativePath -or [string]::IsNullOrEmpty($ExpectedPackageFullName))) {
        throw 'Installed process evidence requires its exact App, CLI or Host executable and package identity.'
    }
    $selected = @($Prepared.inputs | Where-Object relativePath -CEQ $RelativePath)
    if ($selected.Count -ne 1 -or $selected[0].archivePath -cne ('inputs/'+$RelativePath) -or
        $selected[0].sha256 -cnotmatch '^[A-F0-9]{64}$' -or $selected[0].length -le 0 -or $selected[0].length -gt 128MB) {
        throw 'The executable must be one complete original installed input.'
    }
    $expected = Get-InstalledOriginalPath (Join-Path $Prepared.installLocation $RelativePath) $Prepared.installLocation
    if ($selected[0].path -cne $expected) { throw 'The original executable path changed its installed root.' }
    $Process.Refresh()
    if ($Process.HasExited) { throw 'The original installed process exited before image observation.' }
    $actualId = $Process.Id
    $startedAt = $Process.StartTime.ToUniversalTime()
    $current = [Diagnostics.Process]::GetCurrentProcess()
    try { $sameSession = $Process.SessionId -eq $current.SessionId } finally { $current.Dispose() }
    if (-not $sameSession) { throw 'The installed process belongs to another Windows session.' }
    $path = [Text.StringBuilder]::new(32768);[uint32]$length=32768
    if (-not [MirrorPulse.InstalledProcessImageNative]::QueryFullProcessImageNameW($Process.SafeHandle,0,$path,[ref]$length) -or
        $length -eq 0 -or $length -gt 32767 -or [IO.Path]::GetFullPath($path.ToString()) -ine $expected) {
        throw 'The actual executing process image differs from the selected installed executable.'
    }
    [uint32]$packageLength=0
    $packageCode=[MirrorPulse.InstalledProcessImageNative]::GetPackageFullName($Process.SafeHandle,[ref]$packageLength,$null)
    $packageMatched=$false
    if ($ExpectedPackageFullName.Length -gt 0) {
        if ($packageCode -ne 122 -or $packageLength -lt 2 -or $packageLength -gt 4096) { throw 'The actual process has no matching installed package identity.' }
        $packageName=[Text.StringBuilder]::new([int]$packageLength)
        $packageCode=[MirrorPulse.InstalledProcessImageNative]::GetPackageFullName($Process.SafeHandle,[ref]$packageLength,$packageName)
        if ($packageCode -ne 0 -or $packageName.ToString() -cne $ExpectedPackageFullName) { throw 'The actual process package identity differs from the installed verifier owner.' }
        $packageMatched=$true
    } elseif ($Role -cne 'Fixture' -or $packageCode -ne 15700) { throw 'Only an unpackaged ordinary test fixture may omit package identity.' }
    $hash=(Get-FileHash -LiteralPath $expected).Hash
    if ($hash -cne $selected[0].sha256 -or (Get-Item -LiteralPath $expected).Length -ne $selected[0].length) {
        throw 'The executing installed image differs from its original selected MSIX input.'
    }
    $Process.Refresh()
    if ($Process.HasExited -or $Process.Id -ne $actualId -or $Process.StartTime.ToUniversalTime() -ne $startedAt) {
        throw 'The original executing process did not remain alive through image authentication.'
    }
    [pscustomobject][ordered]@{ role=$Role;processId=$actualId;startedUtc=$startedAt.ToString('o');observedUtc=[DateTimeOffset]::UtcNow.ToString('o')
        relativePath=$RelativePath;imageSha256=$hash;imageLength=$selected[0].length;preparedSha256=$PreparedSha256
        sourceSha=$Prepared.sourceSha;sameSession=$true;observedWhileRunning=$true;nativeImagePathMatched=$true;packageIdentityMatched=$packageMatched
        packageIdentitySha256=if($packageMatched){[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($ExpectedPackageFullName)))}else{$null}
        actualManagedModuleLoadPathsEstablished=$false }
}

function Invoke-InstalledStatusWithImageBinding {
    param([Parameter(Mandatory)][string]$AliasPath,[Parameter(Mandatory)]$Prepared,
        [Parameter(Mandatory)][string]$ExpectedPackageFullName,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$PreparedSha256)
    # Compile the native reader before starting the original cold-Host CLI request.
    Initialize-InstalledProcessImageReader
    $start=[Diagnostics.ProcessStartInfo]::new($AliasPath);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $start.ArgumentList.Add('--json');$start.ArgumentList.Add('status')
    $process=[Diagnostics.Process]::Start($start)
    try {
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $binding=Get-InstalledProcessImageBinding -Process $process -Prepared $Prepared -Role Cli -RelativePath 'mp.exe' `
            -ExpectedPackageFullName $ExpectedPackageFullName -PreparedSha256 $PreparedSha256
        # The CLI retains its original internal 30s request limit. This bounds only
        # its external process cleanup, without retrying or changing its result.
        if (-not $process.WaitForExit(35000)) { throw 'The original installed status process did not exit after its request limit.' }
        $output=($stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()).Trim()
        return [pscustomobject]@{output=$output;exitCode=$process.ExitCode;imageBinding=$binding}
    } finally {
        if (-not $process.HasExited) { $process.Kill($true);[void]$process.WaitForExit(10000) }
        $process.Dispose()
    }
}

function Complete-InstalledProcessImageBindings {
    param([Parameter(Mandatory)][string]$Workspace,[Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)]$Prepared,[Parameter(Mandatory)][object[]]$Bindings,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$PreparedSha256,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$CollectedSha256,
        [Parameter(Mandatory)][string]$ExpectedPackageFullName,
        [Parameter(Mandatory)][bool]$VerifyShell)
    $output=Get-InstalledOriginalPath $OutputDirectory $Workspace;$path=Join-Path $output 'process-images.json'
    if ((Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath (Join-Path $output 'prepared.json')).Hash -cne $PreparedSha256 -or
        (Get-FileHash -LiteralPath (Join-Path $output 'collected.json')).Hash -cne $CollectedSha256) { throw 'Original installed input receipts or fresh process binding output changed.' }
    $roles=if($VerifyShell){@('App','Cli','Host')}else{@('Cli','Host')}
    if ($Bindings.Count -ne $roles.Count -or @(Compare-Object @($roles|Sort-Object) @($Bindings.role|Sort-Object) -CaseSensitive).Count -or
        @($Bindings.processId|Sort-Object -Unique).Count -ne $roles.Count) { throw 'Require every distinct original installed process, without missing or duplicate roles.' }
    $required=@{App='MirrorPulse.App.exe';Cli='mp.exe';Host='host/MirrorPulse.Host.exe'}
    $expectedIdentityHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($ExpectedPackageFullName)))
    foreach($binding in $Bindings) {
        $original=@($Prepared.inputs|Where-Object relativePath -CEQ $binding.relativePath)
        if ($binding.sourceSha -cne $Prepared.sourceSha -or $binding.preparedSha256 -cne $PreparedSha256 -or $original.Count -ne 1 -or
            $binding.imageSha256 -cne $original[0].sha256 -or $binding.imageLength -ne $original[0].length -or
            $binding.relativePath -cne $required[$binding.role] -or $binding.packageIdentitySha256 -cne $expectedIdentityHash -or
            $binding.processId -le 0 -or $binding.sameSession -ne $true -or $binding.observedWhileRunning -ne $true -or
            $binding.nativeImagePathMatched -ne $true -or $binding.packageIdentityMatched -ne $true -or $binding.actualManagedModuleLoadPathsEstablished -ne $false -or
            [DateTimeOffset]::Parse($binding.observedUtc) -lt [DateTimeOffset]::Parse($binding.startedUtc)) { throw 'An actual installed process binding differs from the original input or running package owner.' }
    }
    if ((Get-FileHash -LiteralPath $script:InstalledProcessImagePolicyPath).Hash -cne $script:InstalledProcessImagePolicySha256) { throw 'The original process-image verifier source changed during execution.' }
    $collector=Copy-InstalledRuntimeOriginal $script:InstalledProcessImagePolicyPath $Workspace `
        (Join-Path $output 'policy/installed-process-images.ps1') $Workspace $output
    [ordered]@{schemaVersion=1;sourceSha=$Prepared.sourceSha;runtime=$Prepared.runtime;preparedSha256=$PreparedSha256;collectedSha256=$CollectedSha256
        bindings=$Bindings;collector=$collector;actualApphostImagesAndPackageOwnersBound=$true;actualManagedModuleLoadPathsEstablished=$false
        fullP085Accepted=$false;fullS06Accepted=$false;completedUtc=[DateTimeOffset]::UtcNow.ToString('o')}|
        ConvertTo-Json -Depth 8|Set-Content -LiteralPath $path -Encoding utf8
    [pscustomobject]@{archivePath='process-images.json';sha256=(Get-FileHash -LiteralPath $path).Hash;processCount=$Bindings.Count}
}
