using System.Reflection.PortableExecutable;
using MirrorPulse.Worker.ProtocolFixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class InstalledStartupDiagnosticsTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task GuiApphostMissingFrameworkPreservesItsActualExitWithoutInventingShellCompletion()
    {
        await RunSharedShellOutputProbeAsync("native-host-error", """
            $fixture=Join-Path $Directory 'gui-fixture.exe'
            $start=New-InstalledShellProbeStartInfo -ExecutablePath $fixture -ResultPath $resultPath
            Initialize-InstalledShellProbeOutput -ResultPath $resultPath
            $child=[Diagnostics.Process]::Start($start)
            try {
                $observed=Wait-InstalledShellProbeResult -Process $child -ResultPath $resultPath -DeadlineUtc ([DateTime]::UtcNow.AddSeconds(20))
                if ($observed.processState -cne 'Exited' -or $observed.exitCode -ne -2147450730 -or
                    $observed.resultState -cne 'Invalid' -or $observed.stageState -cne 'Invalid' -or
                    $observed.resultBytesRead -ne 0 -or $observed.progressBytesRead -ne 0 -or
                    $null -ne $observed.failureHResult -or $null -ne $observed.stage) {
                    throw ('The GUI apphost must retain its actual FrameworkMissingFailure without reporting Shell completion: '+($observed|ConvertTo-Json -Compress))
                }
            } finally {
                if (-not $child.HasExited) {$child.Kill($true);$child.WaitForExit()}
                $child.Dispose()
            }
            """, PrepareMissingFrameworkGuiApphostAsync);
    }

    private static async Task PrepareMissingFrameworkGuiApphostAsync(string directory)
    {
        string assembly = typeof(ProtocolFixtureMarker).Assembly.Location;
        byte[] image = await File.ReadAllBytesAsync(Path.ChangeExtension(assembly, ".exe"));
        using (var reader = new PEReader(new MemoryStream(image, writable: false)))
        {
            Assert.AreEqual(Subsystem.WindowsCui, reader.PEHeaders.PEHeader!.Subsystem);
            // Only this private synthetic copy changes its PE subsystem; the
            // original apphost binding and all executable instructions stay intact.
            int subsystem = reader.PEHeaders.PEHeaderStartOffset + 68;
            image[subsystem] = (byte)Subsystem.WindowsGui;
            image[subsystem + 1] = 0;
        }
        using (var reader = new PEReader(new MemoryStream(image, writable: false)))
            Assert.AreEqual(Subsystem.WindowsGui, reader.PEHeaders.PEHeader!.Subsystem);
        await File.WriteAllBytesAsync(Path.Combine(directory, "gui-fixture.exe"), image);
        File.Copy(assembly, Path.Combine(directory, Path.GetFileName(assembly)));
        await File.WriteAllTextAsync(Path.Combine(directory, Path.GetFileNameWithoutExtension(assembly) + ".runtimeconfig.json"), """
            {"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"99.0.0"},"rollForward":"Disable"}}
            """);
    }
}
