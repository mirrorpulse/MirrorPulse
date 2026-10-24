using System.Security.Cryptography;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseJournalDispatchReadinessTests
{
    [TestMethod]
    public async Task ConflictCreatedAfterRequestAdmissionStillFencesTheOriginalDispatchAcrossCatalogOwners()
    {
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-dispatch-policy-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        InstanceId instance = InstanceId.New(); var router = Router(paths, instance, RootRegistrationState.Active);
        var command = new MirrorPulseWorkerChangeCommand(Guid.NewGuid(), 7, instance, "docs", MirrorPulseWorkerChangeKind.ContentUpdate,
            "file.bin", null, null, false, Guid.NewGuid(), DateTimeOffset.UtcNow);
        try
        {
            await using (var first = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                Assert.IsTrue(await first.TryRecordWorkerRequestAsync(command.OperationId, instance, SHA256.HashData([1, 2, 3])));
                Assert.IsTrue(await MirrorPulseJournalDispatchReadiness.MayDispatchAsync(command, router, first, _ => true, default));
                await first.SaveUploadConflictAsync(new(command.OperationId, instance, command.OperationId.ToString("D"), "Docs/file.bin",
                    MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, "base", "changed", DateTimeOffset.UtcNow));
                Assert.IsFalse(await MirrorPulseJournalDispatchReadiness.MayDispatchAsync(command, router, first, _ => true, default));
            }
            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                Assert.IsNotNull(await reopened.ReadWorkerRequestAsync(command.OperationId));
                Assert.IsFalse(await MirrorPulseJournalDispatchReadiness.MayDispatchAsync(command, router, reopened, _ => true, default));
            }
            Assert.IsFalse(Directory.Exists(paths.SyncRootPath)); Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath));
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CurrentDisabledRootOrUnavailableWorkerCannotInheritEarlierAdmission(bool disabled)
    {
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-dispatch-policy-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(fixture, "sync"), Path.Combine(fixture, "data"));
        InstanceId instance = InstanceId.New(); var router = Router(paths, instance, disabled ? RootRegistrationState.Disabled : RootRegistrationState.Active);
        var command = new MirrorPulseWorkerChangeCommand(Guid.NewGuid(), 1, instance, "docs", MirrorPulseWorkerChangeKind.ContentUpdate,
            "file.bin", null, null, false, null, DateTimeOffset.UtcNow);
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            Assert.IsFalse(await MirrorPulseJournalDispatchReadiness.MayDispatchAsync(command, router, catalog, _ => disabled, default));
            Assert.IsFalse(Directory.Exists(paths.SyncRootPath)); Assert.IsFalse(File.Exists(paths.CfSharpStateDatabasePath));
        }
        finally { if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
    }

    private static MirrorPulseRootRouter Router(MirrorPulseStoragePaths paths, InstanceId instance, RootRegistrationState state) => new(paths.SyncRootPath,
        [AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.dispatch"), instance, new("docs", "Docs", "Docs", false), state,
            identityScope: RootIdentityScope.InstanceRoot)]);
}
