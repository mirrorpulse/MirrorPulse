namespace MirrorPulse.Core.Tests;

public sealed partial class InstalledStartupDiagnosticsTests
{
    [TestMethod]
    [DoNotParallelize] // Cold PowerShell/native interop compilation shares the original 30-second process deadline.
    [DataRow("missing")]
    [DataRow("disposed")]
    [DataRow("running")]
    public async Task InstalledProbeProcessBoundaryRetainsOnlyActualClosedReadOnlyFacts(string scenario)
    {
        await RunSharedShellOutputProbeAsync(scenario, """
            . (Join-Path (Split-Path -Parent $Policy) 'installed-shell-process-boundary.ps1')
            $child=$null
            try {
                if ($Scenario -ceq 'disposed') {
                    $child=[Diagnostics.Process]::new();$child.Dispose()
                } elseif ($Scenario -ceq 'running') {
                    $child=[Diagnostics.Process]::GetCurrentProcess()
                }
                $observed=Get-InstalledShellProbeProcessBoundary -Process $child
                $expected=@('processState','packageIdentity','packageQueryCode','sameSession','moduleState','managedRuntime','winUiRuntime','appSdkBootstrap')
                if (@(Compare-Object $expected @($observed.psobject.Properties.Name) -CaseSensitive).Count) {
                    throw 'Only the closed, fixed process boundary fields may be retained.'
                }
                if ($Scenario -ceq 'running') {
                    if ($observed.processState -cne 'Running' -or $observed.sameSession -ne $true -or
                        $observed.moduleState -cne 'Observed' -or $observed.managedRuntime -ne $true -or
                        $observed.packageIdentity -cnotin @('Present','Absent') -or
                        ($observed.packageIdentity -ceq 'Present' -and $observed.packageQueryCode -ne 122) -or
                        ($observed.packageIdentity -ceq 'Absent' -and $observed.packageQueryCode -ne 15700)) {
                        throw ('The actual PowerShell process boundary was not observed: '+($observed|ConvertTo-Json -Compress))
                    }
                } elseif ($observed.processState -cne 'Unavailable' -or $observed.packageIdentity -cne 'Unavailable' -or
                    $null -ne $observed.packageQueryCode -or $null -ne $observed.sameSession -or
                    $observed.moduleState -cne 'Unavailable' -or $null -ne $observed.managedRuntime -or
                    $null -ne $observed.winUiRuntime -or $null -ne $observed.appSdkBootstrap) {
                    throw 'An unavailable process must not acquire fabricated facts.'
                }
                if (Test-Path -LiteralPath $resultPath) { throw 'Read-only process observation must not author a probe result.' }
                $observed|ConvertTo-Json -Compress
            } finally {if ($null -ne $child) {$child.Dispose()}}
            """);
    }
}
