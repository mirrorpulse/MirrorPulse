using System.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CfSharpRecreationGateTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IndependentSdkRecreationRequiresExplicitNativeEnvironmentBeforeBuildOrFixtureCreation(bool unset)
    {
        var start = new ProcessStartInfo("pwsh")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (unset) start.Environment.Remove("MIRRORPULSE_NATIVE_TEST"); else start.Environment["MIRRORPULSE_NATIVE_TEST"] = "0";
        foreach (string argument in new[] { "-NoProfile", "-File", Path.Combine(SftpProtocolFixture.FindRepositoryRoot(), "eng", "verify-cfsharp-recreation.ps1"),
            "-ExpectedArchitecture", "Arm64" }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        Assert.AreNotEqual(0, process.ExitCode);
        StringAssert.Contains(await output + await error, "explicitly enabled disposable environment");
    }
}
