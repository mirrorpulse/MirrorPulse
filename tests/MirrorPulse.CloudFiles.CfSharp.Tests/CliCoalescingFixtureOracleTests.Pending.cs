using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed partial class PendingOracleBoundaryTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [TestMethod]
    public async Task IndependentProcessReadsEveryActualPendingOriginalWithoutChangingItsExecution()
    {
        await using var fixture = await Fixture.CreateAsync();
        string before;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            before = JsonSerializer.Serialize(await catalog.ReadMutationAsync(fixture.EffectId));
        var result = await RunAsync(fixture.Root, completed: false);
        Assert.AreEqual(0, result.ExitCode, result.Error);
        var audit = JsonSerializer.Deserialize<CliCoalescingFixtureAudit>(result.Output, JsonOptions)!;
        Assert.IsTrue(audit.ExclusiveCatalogOwner); Assert.AreEqual("UpdateFile", audit.RawEffect);
        Assert.AreEqual(fixture.PlanId, audit.PlanId); Assert.AreEqual(fixture.EffectId, audit.RejectedEffectId);
        CollectionAssert.AreEqual(fixture.OriginalIds, audit.OriginalOperationIds.ToArray());
        Assert.AreEqual(2, audit.OriginalCount); Assert.AreEqual(2, audit.PendingOriginalCount); Assert.AreEqual(0, audit.AcknowledgedOriginalCount);
        Assert.AreEqual("Conflict", audit.OriginalEffectState); Assert.IsTrue(audit.OriginalExpectedRevisionPresent);
        Assert.IsNull(audit.CommandId); Assert.IsNull(audit.AuthorizedEffectId); Assert.IsFalse(audit.ActualNativeConfirmation);
        Assert.IsFalse(result.Output.Contains("PRIVATE_", StringComparison.Ordinal));
        Assert.IsFalse(result.Output.Contains(fixture.Root, StringComparison.Ordinal));
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(0, StringComparer.Ordinal.Compare(before, JsonSerializer.Serialize(await reopened.ReadMutationAsync(fixture.EffectId))));
        Assert.HasCount(2, await reopened.ReadUploadConflictsAsync());
        Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
    }

    [TestMethod]
    [DataRow("live-owner")]
    [DataRow("foreign-marker")]
    [DataRow("foreign-route")]
    [DataRow("foreign-effect")]
    [DataRow("missing-original")]
    [DataRow("foreign-conflict")]
    [DataRow("missing-sdk")]
    [DataRow("completed-with-pending-originals")]
    [DataRow("unapproved-directory")]
    public async Task IndependentProcessRefusesIncompleteOrForeignEvidenceBeforeClaimingAcknowledgment(string change)
    {
        await using var fixture = await Fixture.CreateAsync(change);
        MirrorPulseProductCatalog? owner = null;
        try
        {
            if (change == "live-owner") owner = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            string root = fixture.Root;
            if (change == "unapproved-directory") root = Directory.CreateDirectory(Path.Combine(root, "subdirectory")).FullName;
            var result = await RunAsync(root, completed: change == "completed-with-pending-originals");
            Assert.AreNotEqual(0, result.ExitCode); Assert.AreEqual(string.Empty, result.Output.Trim());
            Assert.IsFalse(result.Error.Contains("PRIVATE_", StringComparison.Ordinal));
            Assert.IsFalse(result.Error.Contains(fixture.Root, StringComparison.Ordinal));
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.Root, "inspection")));
            if (change == "missing-sdk") Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        }
        finally { if (owner is not null) await owner.DisposeAsync(); }
    }

    private static async Task<(int ExitCode, string Output, string Error)> RunAsync(string directory, bool completed)
    {
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in new[] { typeof(CliCoalescingFixtureProbe).Assembly.Location, completed ? "--audit-cli-coalescing-completed" : "--audit-cli-coalescing-pending", directory })
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        return (process.ExitCode, await output, await error);
    }

    private sealed partial class Fixture(string root, MirrorPulseStoragePaths paths, Guid planId, Guid effectId, Guid[] originalIds) : IAsyncDisposable
    {
        internal string Root { get; } = root;
        internal MirrorPulseStoragePaths Paths { get; } = paths;
        internal Guid PlanId { get; } = planId;
        internal Guid EffectId { get; } = effectId;
        internal Guid[] OriginalIds { get; } = originalIds;

        internal static async Task<Fixture> CreateAsync(string? change = null)
        {
            string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-cli-integration-" + Guid.NewGuid().ToString("N"));
            var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
            Directory.CreateDirectory(paths.SyncRootPath);
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var manifest = new AdapterManifest(1, AdapterId.Parse("example.coalescing-fixture"), "Coalescing fixture", "1.0.0", new(2, 2),
                new Dictionary<string, string> { ["win-x64"] = "worker.exe", ["win-arm64"] = "worker.exe" }, new(null), new(null, null),
                new(true, false, true, true), ["en-US"], "1.0.0", [new("files", "Files", "Files", false)]);
            var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(root, "installed"), new(new string('a', 64)),
                AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            await catalog.AddInstallationAsync(installation);
            var instance = await catalog.CreateInstanceAsync(installation.InstallId, "Coalescing fixture", new Dictionary<string, string>(), [],
                Path.Combine(root, "cache", "files"), Path.Combine(root, "cache", "transfers"));
            var route = (await catalog.ReadAdapterTopologyAsync()).Roots.Single();
            var marker = new
            {
                schemaVersion = change == "foreign-marker" ? 2 : 1,
                instanceId = instance.InstanceId.ToString(),
                rootId = change == "foreign-route" ? Guid.NewGuid().ToString("D") : route.RootId.ToString(),
                rootKey = route.UniquenessKey,
                directoryName = route.DirectoryName
            };
            await File.WriteAllTextAsync(Path.Combine(root, ".mp-cli-fixture.json"), JsonSerializer.Serialize(marker, JsonOptions));
            Guid item = Guid.NewGuid(); DateTimeOffset now = DateTimeOffset.UtcNow;
            MirrorPulseWorkerChangeCommand Command(long sequence, MirrorPulseWorkerChangeKind kind) => new(Guid.NewGuid(), sequence,
                route.InstanceId, route.UniquenessKey, kind, "cli-roundtrip.txt", null, null, false, item, now.AddSeconds(sequence));
            MirrorPulseWorkerChangeCommand[] window = [Command(1, change == "foreign-effect" ? MirrorPulseWorkerChangeKind.Create : MirrorPulseWorkerChangeKind.ContentUpdate), Command(2, MirrorPulseWorkerChangeKind.ContentUpdate)];
            var plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window, item, 2, new HashSet<Guid>())!;
            var content = new MirrorPulseCoalescingContent(3, Convert.ToHexString(SHA256.HashData(new byte[] { 3, 5, 7 })),
                new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile));
            var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(plan, window, change == "foreign-effect" ? null : "PRIVATE_ORIGINAL_REVISION", content);
            Guid effect = execution.Steps[0].OperationId;
            await catalog.PrepareJournalCoalescingMutationAsync(plan.PlanId, 0);
            await catalog.TransitionMutationAsync(effect, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            await catalog.TransitionMutationAsync(effect, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Conflict);
            foreach (var original in window)
                await catalog.SaveUploadConflictAsync(new(Guid.NewGuid(), route.InstanceId, original.OperationId.ToString("D"),
                    route.DirectoryName + (change == "foreign-conflict" ? "/PRIVATE_FOREIGN_PATH" : "/cli-roundtrip.txt"),
                    MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, "PRIVATE_ORIGINAL_REVISION", "PRIVATE_SELECTED_REVISION", now));
            if (change != "missing-sdk")
            {
                await using var store = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new(paths.SyncRootPath));
                await using var transaction = await store.BeginTransactionAsync();
                await transaction.Items.UpsertAsync(new(item, "PRIVATE_REMOTE_ID", route.DirectoryName + "/cli-roundtrip.txt", CloudItemKind.File, "PRIVATE_ORIGINAL_REVISION", null, false, now));
                foreach (var original in window)
                    await transaction.Operations.EnqueueAsync(new(original.OperationId, CloudStateOperationKind.MetadataUpdate, item, new byte[] { 1, 3 }, now));
                if (change == "missing-original") await transaction.Operations.RemoveAsync(window[0].OperationId);
                await transaction.CommitAsync();
            }
            return new(root, paths, plan.PlanId, effect, window.Select(command => command.OperationId).ToArray());
        }

        public ValueTask DisposeAsync()
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Root));
            string name = Path.GetFileName(full); const string prefix = "MirrorPulse-cli-integration-";
            if (!string.Equals(Path.GetDirectoryName(full), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
                (File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Refuse a cleanup outside the exact oracle fixture.");
            Directory.Delete(full, recursive: true); return ValueTask.CompletedTask;
        }
    }
}
