using MirrorPulse.App;

namespace MirrorPulse.Core.Tests;

[TestClass]
[DoNotParallelize]
public sealed class InstalledShellProbeStartupTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void StartupObservesTheOriginalDelegateAndDoesNotInventItsResult(bool fail)
    {
        WithProbeOutput(result =>
        {
            var originalFailure = new InvalidOperationException("Original startup failed.");
            int calls = 0;
            void OriginalStartup()
            {
                calls++;
                Assert.AreEqual("1:GeneratedStartupStarting", File.ReadAllText(result + ".progress"));
                Assert.IsFalse(File.Exists(result));
                if (fail) throw originalFailure;
            }
            if (fail)
            {
                var observed = Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulsePackagedShellProbeStartup.Run(OriginalStartup));
                Assert.AreSame(originalFailure, observed);
            }
            else MirrorPulsePackagedShellProbeStartup.Run(OriginalStartup);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(fail ? "1:GeneratedStartupStarting" : "1:GeneratedStartupReturned", File.ReadAllText(result + ".progress"));
            Assert.IsFalse(File.Exists(result));
        });
    }

    [TestMethod]
    public void StartupProgressFailureCannotReplaceTheGeneratedStartupOrItsOriginalFailure()
    {
        WithProbeOutput(result =>
        {
            Directory.CreateDirectory(result + ".progress");
            var original = new InvalidOperationException("Original startup failed.");
            var observed = Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulsePackagedShellProbeStartup.Run(() => throw original));
            Assert.AreSame(original, observed);
            Assert.IsTrue(Directory.Exists(result + ".progress"));
            Assert.IsFalse(File.Exists(result));
        });
    }

    [TestMethod]
    public void MissingProbeEnvironmentStillRunsTheOriginalStartupExactlyOnce()
    {
        string? previous = Environment.GetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT");
        try
        {
            Environment.SetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT", null);
            int calls = 0;
            MirrorPulsePackagedShellProbeStartup.Run(() => calls++);
            Assert.AreEqual(1, calls);
            Assert.ThrowsExactly<ArgumentNullException>(() => MirrorPulsePackagedShellProbeStartup.Run(null!));
        }
        finally { Environment.SetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT", previous); }
    }

    private static void WithProbeOutput(Action<string> verify)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-shell-startup-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string? previous = Environment.GetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT");
        try
        {
            string result = Path.Combine(directory, "result");
            Environment.SetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT", result);
            verify(result);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MIRRORPULSE_PACKAGED_SHELL_PROBE_RESULT", previous);
            Directory.Delete(directory, recursive: true);
        }
    }
}
