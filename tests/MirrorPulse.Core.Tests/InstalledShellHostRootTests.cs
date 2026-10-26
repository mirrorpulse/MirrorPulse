using MirrorPulse.App;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class InstalledShellHostRootTests
{
    [TestMethod]
    [DataRow("missing")]
    [DataRow("empty")]
    [DataRow("relative")]
    [DataRow("volume")]
    public void ExpectedHostRootMustAlreadyExistAndCannotBeInferredOrCreated(string scenario)
    {
        string missing = Path.Combine(Path.GetTempPath(), "MirrorPulse-shell-root-tests", Guid.NewGuid().ToString("N"));
        string? input = scenario switch
        {
            "missing" => missing,
            "empty" => null,
            "relative" => "MirrorPulse",
            "volume" => Path.GetPathRoot(missing),
            _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
        };
        Assert.Throws<Exception>(() => MirrorPulseShellProbeHostRoot.RequireExpectedRoot(input));
        Assert.IsFalse(Directory.Exists(missing));
    }

    [TestMethod]
    public void ExistingRegistrationMustMatchTheExpectedHostPathAndProviderWithoutTakingOwnership()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-shell-root-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string sentinel = Path.Combine(directory, "owned-by-host.txt");
        File.WriteAllText(sentinel, "host data");
        try
        {
            string root = MirrorPulseShellProbeHostRoot.RequireExpectedRoot(directory + Path.DirectorySeparatorChar);
            Guid provider = Guid.NewGuid();
            MirrorPulseShellProbeHostRoot.RequireRegistration(root, provider, directory.ToUpperInvariant(), provider);
            Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseShellProbeHostRoot.RequireRegistration(root, provider, directory + "-foreign", provider));
            Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseShellProbeHostRoot.RequireRegistration(root, provider, directory, Guid.NewGuid()));
            Assert.AreEqual("host data", File.ReadAllText(sentinel));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task RestorationIsAwaitedAndBothOriginalFailuresArePreserved(bool verificationFails, bool restorationFails)
    {
        var verification = new InvalidDataException("Original verification failure.");
        var restoration = new IOException("Original restoration failure.");
        var restoring = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int verifyCalls = 0, restoreCalls = 0;
        Task operation = MirrorPulseShellProbeHostRoot.VerifyAndRestoreAsync(() =>
        {
            verifyCalls++;
            if (verificationFails) throw verification;
            return Task.CompletedTask;
        }, async () =>
        {
            restoreCalls++;
            restoring.SetResult();
            await release.Task;
            if (restorationFails) throw restoration;
        });
        await restoring.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsFalse(operation.IsCompleted, "Success or the original failure must wait for restoration.");
        release.SetResult();
        if (verificationFails && restorationFails)
        {
            AggregateException failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => operation);
            CollectionAssert.AreEqual(new Exception[] { verification, restoration }, failure.InnerExceptions.ToArray());
        }
        else if (verificationFails)
            Assert.AreSame(verification, await Assert.ThrowsExactlyAsync<InvalidDataException>(() => operation));
        else if (restorationFails)
            Assert.AreSame(restoration, await Assert.ThrowsExactlyAsync<IOException>(() => operation));
        else await operation;
        Assert.AreEqual(1, verifyCalls);
        Assert.AreEqual(1, restoreCalls);
    }
}
