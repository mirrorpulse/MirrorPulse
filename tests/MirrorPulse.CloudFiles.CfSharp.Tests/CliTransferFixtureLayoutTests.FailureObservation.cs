using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class CliTransferFixtureLayoutTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StoppedFailureObserverRetainsActualIntentDespiteMissingConflictRevisions(bool create)
    {
        var fixture = Create(); var paths = new MirrorPulseStoragePaths(Path.Combine(fixture.Root, "sync"), Path.Combine(fixture.Root, "data"));
        Directory.CreateDirectory(paths.SyncRootPath);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, CliTransferFixtureLayout.MarkerFileName), JsonSerializer.Serialize(fixture.Marker, JsonOptions));
            Guid item = Guid.NewGuid(); var route = fixture.Topology.Roots.Single(root => root.UniquenessKey == "left");
            DateTimeOffset observed = DateTimeOffset.UtcNow;
            MirrorPulseWorkerChangeCommand Command(long sequence, MirrorPulseWorkerChangeKind kind) => new(Guid.NewGuid(), sequence,
                route.InstanceId, route.UniquenessKey, kind, "cross-instance.bin", null, null, false, item, observed.AddSeconds(sequence));
            MirrorPulseWorkerChangeCommand[] window = [Command(1, create ? MirrorPulseWorkerChangeKind.Create : MirrorPulseWorkerChangeKind.ContentUpdate), Command(2, MirrorPulseWorkerChangeKind.ContentUpdate)];
            var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window, item, 2, new HashSet<Guid>())!;
            var binding = new MirrorPulseUploadBinding(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile);
            var content = new MirrorPulseCoalescingContent(3, Convert.ToHexString(SHA256.HashData(new byte[] { 2, 3, 5 })), binding);
            string? baseline = create ? null : "PRIVATE_ORIGINAL_REVISION";
            string before;
            Guid effect;
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.SaveAdapterTopologyAsync(fixture.Topology);
                var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window, baseline, content);
                effect = execution.Steps[0].OperationId;
                await catalog.PrepareJournalCoalescingMutationAsync(plan.PlanId, 0);
                await catalog.TransitionMutationAsync(effect, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
                await catalog.TransitionMutationAsync(effect, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Conflict);
                foreach (var command in window)
                    await catalog.SaveUploadConflictAsync(new(Guid.NewGuid(), route.InstanceId, command.OperationId.ToString("D"),
                        "Left/cross-instance.bin", MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, null, null, observed));
                before = JsonSerializer.Serialize(await catalog.ReadMutationAsync(effect));
            }
            var result = await RunFailureObserverAsync(fixture.Root);
            Assert.AreEqual(0, result.ExitCode, result.Error);
            var actual = JsonSerializer.Deserialize<CliTransferFailureObservation>(result.Output, JsonOptions)!;
            Assert.IsTrue(actual.ExclusiveCatalogOwner); Assert.IsFalse(actual.MoreConflictsAvailable);
            Assert.HasCount(2, actual.Conflicts); Assert.HasCount(1, actual.Plans);
            Assert.IsTrue(actual.Conflicts.All(conflict => !conflict.ExpectedRevisionPresent && !conflict.ActualRevisionPresent && conflict.PlanId == plan.PlanId));
            Assert.AreEqual(plan.PlanId, actual.Plans[0].PlanId); Assert.AreEqual(item, actual.Plans[0].ItemId);
            Assert.IsTrue(actual.Plans[0].ExecutionPresent);
            Assert.IsTrue(create ? actual.Plans[0].ExpectedRevisionPresent is false : actual.Plans[0].ExpectedRevisionPresent is true);
            Assert.HasCount(1, actual.Plans[0].Effects); var mutation = actual.Plans[0].Effects[0];
            Assert.AreEqual(effect, mutation.OperationId); Assert.AreEqual("Conflict", mutation.State); Assert.AreEqual("Started", mutation.ExecutionEvidence);
            Assert.IsTrue(create ? mutation.ExpectedRevisionPresent is false : mutation.ExpectedRevisionPresent is true);
            Assert.IsNotNull(mutation.AcceptedRevisionPresent);
            Assert.IsFalse(mutation.AcceptedRevisionPresent.Value);
            Assert.IsFalse(result.Output.Contains("PRIVATE_", StringComparison.Ordinal)); Assert.IsFalse(result.Output.Contains("cross-instance.bin", StringComparison.Ordinal));
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            Assert.AreEqual(before, JsonSerializer.Serialize(await reopened.ReadMutationAsync(effect)));
            Assert.HasCount(2, await reopened.ReadUploadConflictsAsync());
            Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
        }
        finally { Directory.Delete(CliTransferFixtureLayout.RequireFixtureDirectory(fixture.Root), true); }
    }

    [TestMethod]
    [DataRow("live-owner")]
    [DataRow("foreign-path")]
    [DataRow("foreign-operation")]
    public async Task FailureObserverRefusesLiveOwnershipOrForeignConflictWithoutStartingCloudFiles(string failure)
    {
        var fixture = Create(); var paths = new MirrorPulseStoragePaths(Path.Combine(fixture.Root, "sync"), Path.Combine(fixture.Root, "data"));
        Directory.CreateDirectory(paths.SyncRootPath); MirrorPulseProductCatalog? owner = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixture.Root, CliTransferFixtureLayout.MarkerFileName), JsonSerializer.Serialize(fixture.Marker, JsonOptions));
            owner = await MirrorPulseProductCatalog.OpenAsync(paths); await owner.SaveAdapterTopologyAsync(fixture.Topology);
            if (failure != "live-owner")
            {
                var route = fixture.Topology.Roots.Single(root => root.UniquenessKey == "left");
                await owner.SaveUploadConflictAsync(new(Guid.NewGuid(), route.InstanceId, failure == "foreign-operation" ? "PRIVATE_INVALID_OPERATION" : Guid.NewGuid().ToString("D"),
                    failure == "foreign-path" ? "Left/PRIVATE_FOREIGN_PATH" : "Left/cross-instance.bin", MirrorPulseConflictReason.StaleRemoteRevision,
                    MirrorPulseVersionComparison.Diverged, "PRIVATE_VERSION", null, DateTimeOffset.UtcNow));
                await owner.DisposeAsync(); owner = null;
            }
            var result = await RunFailureObserverAsync(fixture.Root);
            Assert.AreEqual(1, result.ExitCode); Assert.AreEqual(string.Empty, result.Output.Trim());
            Assert.IsFalse(result.Error.Contains("PRIVATE_", StringComparison.Ordinal));
            StringAssert.Contains(result.Error, failure == "live-owner" ? "stage=CatalogOwnership" : "stage=TransferFailureObservation");
            Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
        }
        finally
        {
            if (owner is not null) await owner.DisposeAsync();
            Directory.Delete(CliTransferFixtureLayout.RequireFixtureDirectory(fixture.Root), true);
        }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunFailureObserverAsync(string directory)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { typeof(ProbeMarker).Assembly.Location, "--observe-cli-transfer-failure", directory }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await output, await error);
    }
}
