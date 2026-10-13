using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
[TestClass]
[DoNotParallelize]
public sealed class DurabilityFaultFixtureTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (DurabilityBoundary boundary in Enum.GetValues<DurabilityBoundary>())
            foreach (FaultTiming timing in Enum.GetValues<FaultTiming>())
                foreach (FaultMode mode in Enum.GetValues<FaultMode>())
                    yield return [boundary, timing, mode];
    }

    [TestMethod]
    [DynamicData(nameof(Cases))]
    [TestCategory("DurabilityFaultFixture")]
    public async Task InjectedFailureLeavesTheExpectedDurableBoundary(
        DurabilityBoundary boundary, FaultTiming timing, FaultMode mode)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-fault-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".mp-fault-fixture"), "test-only");
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        Exception? primaryFailure = null;
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[] { typeof(ProbeMarker).Assembly.Location, root, boundary.ToString(), timing.ToString(), mode.ToString() })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("The fault probe did not start.");
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // Drain the owned process after termination as well. Cancelling these readers at
            // the deadline discards the very diagnostics needed to explain its timeout.
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            bool timedOut = false;
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { timedOut = true; }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync();
                }
            }

            string diagnostics = $"{await output}\n{await error}";
            Assert.IsFalse(timedOut, $"The owned {boundary}/{timing}/{mode} probe exceeded its original 30-second deadline.\n{diagnostics}");
            Assert.AreEqual(mode == FaultMode.Exit ? DurabilityFaultProbe.ExitFaultCode : DurabilityFaultProbe.ExceptionFaultCode,
                process.ExitCode, diagnostics);
            string expected = timing == FaultTiming.Before ? "before" : "after";
            switch (boundary)
            {
                case DurabilityBoundary.RescanCheckpoint:
                    await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                    {
                        MirrorPulseFullRescanCheckpoint checkpoint = (await catalog.ReadFullRescanAsync())!;
                        Assert.IsNotNull(checkpoint);
                        Assert.AreEqual(timing == FaultTiming.Before ? MirrorPulseFullRescanPhase.Requested : MirrorPulseFullRescanPhase.Running, checkpoint.Phase);
                        Assert.AreEqual(checkpoint.Generation, (await catalog.BeginFullRescanAsync()).Generation);
                    }
                    break;
                case DurabilityBoundary.ConflictCopy:
                    MirrorPulseConflictRecord copyConflict = DurabilityFaultProbe.CopyConflict();
                    string copyPath = Path.Combine(MirrorPulseConflictDirectory.GetPath(paths, copyConflict.InstanceId), "local",
                        MirrorPulseConflictFileName.Create(copyConflict));
                    Assert.AreEqual("original side", await File.ReadAllTextAsync(Path.Combine(paths.SyncRootPath, "note.txt")));
                    Assert.AreEqual(timing == FaultTiming.After, File.Exists(copyPath));
                    Assert.AreEqual(timing == FaultTiming.After, File.Exists(copyPath + ".manifest.json"));
                    if (timing == FaultTiming.After)
                    {
                        string reopened = await new MirrorPulseConflictCopyStore(paths).PreserveAsync(copyConflict,
                            MirrorPulseConflictPreservedSide.Local, _ => throw new InvalidOperationException("A committed copy must not reopen its source."));
                        Assert.AreEqual(copyPath, reopened);
                        Assert.AreEqual("original side", await File.ReadAllTextAsync(reopened));
                    }
                    else
                    {
                        Assert.IsTrue(File.Exists(copyPath + ".pending.json"));
                        var recovered = new MirrorPulseConflictCopyStore(paths);
                        string replay = await recovered.PreserveAsync(copyConflict, MirrorPulseConflictPreservedSide.Local,
                            token => mode == FaultMode.Exit
                                ? throw new InvalidOperationException("Flushed staging survives abrupt exit; do not reopen the source.")
                                : recovered.OpenLocalAsync(copyConflict, token));
                        Assert.AreEqual(copyPath, replay);
                        Assert.AreEqual("original side", await File.ReadAllTextAsync(replay));
                        Assert.IsTrue(File.Exists(replay + ".manifest.json"));
                    }
                    break;
                case DurabilityBoundary.RemoteWrite:
                    Assert.AreEqual(expected, await File.ReadAllTextAsync(Path.Combine(root, "remote", "document.txt")));
                    Assert.HasCount(1, Directory.GetFiles(Path.Combine(root, "remote")));
                    break;
                case DurabilityBoundary.JournalAcknowledgement:
                    await using (ICloudStateStore store = await MirrorPulseCfSharpStateStoreFactory.Create(paths)
                        .OpenAsync(new CloudStateStoreContext(paths.SyncRootPath)))
                    {
                        await using ICloudStateTransaction read = await store.BeginTransactionAsync();
                        CloudOperationJournalEntry? pending = await read.Operations.GetAsync(DurabilityFaultProbe.OperationId);
                        Assert.AreEqual(timing == FaultTiming.Before, pending is not null);
                        await read.RollbackAsync();
                    }
                    break;
                case DurabilityBoundary.SnapshotSave:
                    var snapshots = new MirrorPulseFileRemotePollSnapshotStore(paths.DataRootPath);
                    IReadOnlyDictionary<string, MirrorPulseRemoteSnapshotEntry>? snapshot = await snapshots.LoadAsync(DurabilityFaultProbe.Instance);
                    Assert.IsNotNull(snapshot);
                    Assert.AreEqual(expected, snapshot["document"].RemoteRevision);
                    Assert.HasCount(1, Directory.GetFiles(Path.Combine(paths.DataRootPath, "remote-poll")));
                    break;
                case DurabilityBoundary.CatalogSave:
                    await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                    {
                        MirrorPulseUserCommandRecord? command = await catalog.ReadUserCommandAsync(DurabilityFaultProbe.OperationId);
                        Assert.IsNotNull(command);
                        Assert.AreEqual(expected, command.State);
                    }
                    break;
                case DurabilityBoundary.PendingRemoteBatch:
                    await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                    {
                        var pendingStore = new MirrorPulseCatalogRemotePollPendingStore(catalog);
                        MirrorPulsePendingRemotePoll? pending = await pendingStore.LoadAsync(DurabilityFaultProbe.Instance, CancellationToken.None);
                        Assert.AreEqual(timing == FaultTiming.After, pending is not null);
                        if (pending is not null)
                        {
                            Assert.AreEqual(DurabilityFaultProbe.Instance + "/fixture", pending.BatchId);
                            Assert.AreEqual("before", pending.Previous["document"].RemoteRevision);
                            Assert.AreEqual("after", pending.Candidate["document"].RemoteRevision);
                            CollectionAssert.AreEqual(new byte[32], pending.Fingerprint);
                        }
                    }
                    break;
            }
        }
        catch (Exception failure) { primaryFailure = failure; }
        try { await DeleteOwnedFixtureAsync(root); }
        catch (Exception cleanupFailure) when (primaryFailure is not null)
        {
            throw new AggregateException("The probe failed and its owned fixture could not be removed.", primaryFailure, cleanupFailure);
        }
        if (primaryFailure is not null) ExceptionDispatchInfo.Capture(primaryFailure).Throw();
    }

    private static async Task DeleteOwnedFixtureAsync(string root)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        string expectedParent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-fault-tests"));
        if (!string.Equals(directory.Parent?.FullName, expectedParent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(directory.Name, "N", out _) || directory.LinkTarget is not null ||
            !File.Exists(Path.Combine(directory.FullName, ".mp-fault-fixture")))
            throw new InvalidOperationException("Cleanup requires the exact owned temporary fault fixture.");
        // Windows may briefly retain mapped SQLite handles after abrupt process exit.
        // This bound affects cleanup only; every fault assertion and deadline stays intact.
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            try { directory.Delete(recursive: true); return; }
            catch (IOException) when (elapsed.Elapsed < TimeSpan.FromSeconds(2)) { await Task.Delay(100); }
        }
    }
}
