# Failure-only, read-only observations. Never retain process paths, package names,
# arbitrary module names, command lines, window text, tokens or process memory.
function Get-InstalledShellProbeProcessBoundary {
    param([AllowNull()][System.Diagnostics.Process]$Process)
    $result=[ordered]@{processState='Unavailable';packageIdentity='Unavailable';packageQueryCode=$null;
        sameSession=$null;moduleState='Unavailable';managedRuntime=$null;winUiRuntime=$null;appSdkBootstrap=$null}
    if ($null -eq $Process) { return [pscustomobject]$result }
    try {
        $Process.Refresh()
        $hasExited=$Process.HasExited
        if ($hasExited -isnot [bool]) { return [pscustomobject]$result }
        if ($hasExited) { $result.processState='Exited';return [pscustomobject]$result }
        $result.processState='Running'
    } catch { return [pscustomobject]$result }
    try {
        $current=[Diagnostics.Process]::GetCurrentProcess()
        try {
            $session=$Process.SessionId;$currentSession=$current.SessionId
            if ($session -is [int] -and $currentSession -is [int]) { $result.sameSession=$session -eq $currentSession }
        } finally { $current.Dispose() }
    } catch { }
    try {
        if ($null -eq ('MirrorPulse.InstalledProbeNative' -as [type])) {
            Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace MirrorPulse {
    public static class InstalledProbeNative {
        [DllImport("kernel32.dll", ExactSpelling=true)]
        public static extern int GetPackageFullName(SafeProcessHandle process, ref uint length, IntPtr name);
    }
}
'@
        }
        [uint32]$length=0
        $code=[MirrorPulse.InstalledProbeNative]::GetPackageFullName($Process.SafeHandle,[ref]$length,[IntPtr]::Zero)
        $result.packageQueryCode=$code
        # The documented size-only query distinguishes identity without reading its name.
        if ($code -eq 122 -and $length -gt 0) { $result.packageIdentity='Present' }
        elseif ($code -eq 15700) { $result.packageIdentity='Absent' }
    } catch { }
    try {
        $managed=$false;$xaml=$false;$bootstrap=$false
        $modules=$Process.Modules
        if ($null -eq $modules) { throw 'Process modules are unavailable.' }
        foreach ($module in $modules) {
            switch -Exact ($module.ModuleName.ToLowerInvariant()) {
                'coreclr.dll' { $managed=$true }
                'microsoft.ui.xaml.dll' { $xaml=$true }
                'microsoft.windowsappruntime.bootstrap.dll' { $bootstrap=$true }
            }
        }
        $result.moduleState='Observed';$result.managedRuntime=$managed
        $result.winUiRuntime=$xaml;$result.appSdkBootstrap=$bootstrap
    } catch { }
    [pscustomobject]$result
}
