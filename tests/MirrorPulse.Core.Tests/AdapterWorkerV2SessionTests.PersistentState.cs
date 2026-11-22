using System.Diagnostics;
using System.Text.Json;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Security;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class AdapterWorkerV2SessionTests
{
    [TestMethod]
    public async Task WorkerDurableStateIsInstanceScopedAndSurvivesCatalogReopenAndCacheRemoval()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var first = CreateFixtureTopology(Path.Combine(directory, "first"), InstanceId.New());
        var second = CreateFixtureTopology(Path.Combine(directory, "second"), InstanceId.New());
        var disabled = CreateFixtureTopology(Path.Combine(directory, "disabled"), InstanceId.New());
        var inactive = disabled.Instances.Single();
        inactive = new(inactive.AdapterId, inactive.InstallId, inactive.InstanceId, "Disabled", inactive.Configuration,
            inactive.CredentialReferences, inactive.FileCacheDirectory, inactive.TransferCacheDirectory, false,
            AdapterLifecycleState.Disabled, null, inactive.CreatedAt);
        RootRegistration OneRoot(MirrorPulseAdapterTopology source, string label, bool enabled = true)
        {
            var root = source.Roots[0];
            return new(root.AdapterId, root.InstanceId, root.RootId, root.UniquenessKey, label, label, root.CustomEntry,
                enabled ? RootRegistrationState.Active : RootRegistrationState.Disabled, root.RegisteredAt, root.IdentityScope);
        }
        var topology = new MirrorPulseAdapterTopology([.. first.Installations, .. second.Installations, .. disabled.Installations],
            [first.Instances.Single(), second.Instances.Single(), inactive],
            [OneRoot(first, "FirstRoot"), OneRoot(second, "SecondRoot"), OneRoot(disabled, "DisabledRoot", enabled: false)]);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        CancellationToken token = deadline.Token;
        var originals = new Dictionary<InstanceId, byte[]>();
        try
        {
            for (int owner = 0; owner < 2; owner++)
            {
                foreach (var instance in topology.Instances.Where(value => value.Enabled))
                {
                    Directory.CreateDirectory(instance.TransferCacheDirectory);
                    Directory.CreateDirectory(instance.FileCacheDirectory);
                    await File.WriteAllTextAsync(Path.Combine(instance.FileCacheDirectory, "ephemeral.txt"), "discardable cache", token);
                    await File.WriteAllTextAsync(Path.Combine(instance.TransferCacheDirectory, ".mp-startup-fixture-mode"), "DurableStateProbe", token);
                }
                await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token))
                {
                    await catalog.SaveAdapterTopologyAsync(topology, token);
                    await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
                    await supervisor.StartAsync(topology);
                    foreach (var instance in topology.Instances.Where(value => value.Enabled))
                    {
                        while (!supervisor.IsConnected(instance.InstanceId)) await Task.Delay(25, token);
                        Assert.AreEqual("left/revision", await supervisor.StatAsync(new(instance.InstanceId, "same.txt", "left"), token));
                        string state = Path.Combine(paths.DataRootPath, "state", "workers", instance.InstanceId.Value.ToString("N"));
                        byte[] receipt = await File.ReadAllBytesAsync(Path.Combine(state, ".mp-worker-state-fixture"), token);
                        using var retained = JsonDocument.Parse(receipt);
                        Assert.AreEqual(instance.InstanceId.Value, retained.RootElement.GetProperty("instanceId").GetGuid());
                        Assert.IsFalse(SourceDirectoryPathNormalizer.IsWithin(instance.TransferCacheDirectory, state));
                        Assert.IsFalse(SourceDirectoryPathNormalizer.IsWithin(instance.FileCacheDirectory, state));
                        if (owner == 0) originals.Add(instance.InstanceId, receipt);
                        else CollectionAssert.AreEqual(originals[instance.InstanceId], receipt);
                    }
                    Assert.IsFalse(supervisor.IsConnected(inactive.InstanceId));
                    Assert.IsFalse(Directory.Exists(Path.Combine(paths.DataRootPath, "state", "workers", inactive.InstanceId.Value.ToString("N"))));
                }
                // All Worker/Host owners have stopped before removing these known
                // per-test caches. Persisted state must survive both removals.
                foreach (var instance in topology.Instances.Where(value => value.Enabled))
                    foreach (string cache in new[] { instance.TransferCacheDirectory, instance.FileCacheDirectory })
                    {
                        Assert.IsTrue(SourceDirectoryPathNormalizer.IsWithin(directory, cache));
                        if (Directory.Exists(cache)) Directory.Delete(cache, recursive: true);
                    }
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WorkerStartupRejectsCacheOverlapWithDurableStateBeforeLaunching(bool cacheInsideState)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var topology = CreateFixtureTopology(directory, InstanceId.New());
        var instance = topology.Instances.Single();
        string cache = cacheInsideState ? Path.Combine(paths.DataRootPath, "state", "workers", "cache") : paths.DataRootPath;
        instance = new(instance.AdapterId, instance.InstallId, instance.InstanceId, instance.DisplayName, instance.Configuration,
            instance.CredentialReferences, instance.FileCacheDirectory, cache, instance.Enabled, instance.LifecycleState, null, instance.CreatedAt);
        topology = topology with { Instances = [instance] };
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
            var refused = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => supervisor.StartAsync(topology));
            Assert.AreEqual("WorkerPersistentStateCacheOverlap", refused.Message);
            Assert.IsFalse(Directory.Exists(Path.Combine(paths.DataRootPath, "state", "workers")));
            Assert.IsFalse(supervisor.IsConnected(instance.InstanceId));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WorkerStateGrantRefusesAFileOrRedirectedAncestorBeforeStartingAProcess(bool junction)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var topology = CreateFixtureTopology(directory, InstanceId.New());
        var instance = topology.Instances.Single();
        string stateRoot = Path.Combine(paths.DataRootPath, "state", "workers");
        string blocked = junction ? stateRoot : Path.Combine(stateRoot, instance.InstanceId.Value.ToString("N"));
        string outside = Path.Combine(directory, "external-target");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        CancellationToken token = deadline.Token;
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token);
            Directory.CreateDirectory(outside);
            await File.WriteAllTextAsync(Path.Combine(outside, "sentinel.txt"), "independent bytes", token);
            if (junction)
            {
                var start = new ProcessStartInfo("pwsh")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-CommandWithArgs",
                    "New-Item -ItemType Junction -Path $args[0] -Target $args[1] -ErrorAction Stop | Out-Null", blocked, outside })
                    start.ArgumentList.Add(argument);
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Fixture junction process did not start.");
                try
                {
                    var output = process.StandardOutput.ReadToEndAsync(token);
                    var error = process.StandardError.ReadToEndAsync(token);
                    await process.WaitForExitAsync(token);
                    await output; await error;
                    Assert.AreEqual(0, process.ExitCode, "The owned fixture junction must be created without an elevation or test skip.");
                }
                finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); process.WaitForExit(); } }
            }
            else
            {
                Directory.CreateDirectory(stateRoot);
                await File.WriteAllTextAsync(blocked, "existing original file", token);
            }
            await catalog.SaveAdapterTopologyAsync(topology, token);
            await using var supervisor = new AdapterInstanceProcessSupervisor(catalog, new WindowsCredentialManagerStore());
            await supervisor.StartAsync(topology);
            while ((await catalog.ReadInstanceRuntimeStateAsync(instance.InstanceId, token))?.Phase != "Worker failed")
                await Task.Delay(25, token);
            Assert.IsFalse(supervisor.IsConnected(instance.InstanceId));
            Assert.IsFalse(Directory.Exists(instance.TransferCacheDirectory));
            Assert.AreEqual("independent bytes", await File.ReadAllTextAsync(Path.Combine(outside, "sentinel.txt"), token));
            Assert.HasCount(1, Directory.GetFileSystemEntries(outside));
            if (!junction) Assert.AreEqual("existing original file", await File.ReadAllTextAsync(blocked, token));
        }
        finally
        {
            if (junction && Directory.Exists(blocked)) Directory.Delete(blocked);
            Directory.Delete(directory, recursive: true);
        }
    }
}
