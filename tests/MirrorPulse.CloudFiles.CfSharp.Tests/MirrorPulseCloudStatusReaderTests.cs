using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseCloudStatusReaderTests
{
    [TestMethod]
    public async Task ReadsOfficialJournalAndOpaqueCursorAfterStoreRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var instance = InstanceId.New();
        byte[] cursor = [5, 6, 7];
        try
        {
            Directory.CreateDirectory(paths.SyncRootPath);
            ICloudStateStoreFactory factory = MirrorPulseCfSharpStateStoreFactory.Create(paths);
            await using (ICloudStateStore store = await factory.OpenAsync(
                new CloudStateStoreContext(paths.SyncRootPath)))
            {
                await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
                await transaction.Operations.EnqueueAsync(new CloudOperationJournalEntry(
                    Guid.NewGuid(), CloudStateOperationKind.MetadataUpdate, null, [1],
                    DateTimeOffset.UtcNow, 0, null, 0));
                await transaction.Checkpoints.UpsertAsync(new CloudStateCheckpoint(
                    MirrorPulseRemoteBatchCoordinator.GetCheckpointName(instance), cursor,
                    DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            }

            await using ICloudStateStore reopened = await factory.OpenAsync(
                new CloudStateStoreContext(paths.SyncRootPath));
            MirrorPulseCloudStatusSnapshot snapshot = await MirrorPulseCloudStatusReader.ReadAsync(reopened, [instance]);
            Assert.AreEqual(1, snapshot.PendingUploadCount);
            Assert.AreEqual(0, snapshot.PendingRemoteConflictCount);
            Assert.HasCount(1, snapshot.Cursors);
            Assert.IsTrue(snapshot.Cursors[0].HasCursor);
            Assert.AreEqual(12, snapshot.Cursors[0].CursorFingerprint?.Length);
            Assert.AreNotEqual(Convert.ToBase64String(cursor), snapshot.Cursors[0].CursorFingerprint);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [TestMethod]
    [DataRow(RootRegistrationState.Active)]
    [DataRow(RootRegistrationState.Disabled)]
    public async Task RootScopedCursorRemainsVisibleAfterReopenWithoutPublishingAnInstanceCursor(RootRegistrationState state)
    {
        await using var fixture = await CursorFixture.CreateAsync();
        var root = fixture.Root("files", state);
        await fixture.WriteAsync(MirrorPulseRemoteBatchCoordinator.GetCheckpointName(fixture.Instance, root.RootId), [1, 2, 3]);
        await fixture.ReopenAsync();
        var snapshot = await MirrorPulseCloudStatusReader.ReadAsync(fixture.Store, [fixture.Instance], [root], CancellationToken.None);
        Assert.IsTrue(snapshot.Cursors[0].HasCursor);
        Assert.AreEqual(12, snapshot.Cursors[0].CursorFingerprint!.Length);
        Assert.AreEqual(fixture.Timestamp, snapshot.Cursors[0].UpdatedAt);
        Assert.IsFalse((await MirrorPulseCloudStatusReader.ReadAsync(fixture.Store, [fixture.Instance])).Cursors[0].HasCursor);
        await using var read = await fixture.Store.BeginTransactionAsync();
        Assert.IsNull(await read.Checkpoints.GetAsync(MirrorPulseRemoteBatchCoordinator.GetCheckpointName(fixture.Instance)));
        await read.RollbackAsync();
    }

    [TestMethod]
    public async Task SummaryIncludesEveryRootValueAndStableIdentityIndependentlyOfEnumerationAndLabels()
    {
        await using var fixture = await CursorFixture.CreateAsync();
        var first = fixture.Root("first"); var second = fixture.Root("second");
        string firstName = MirrorPulseRemoteBatchCoordinator.GetCheckpointName(fixture.Instance, first.RootId);
        string secondName = MirrorPulseRemoteBatchCoordinator.GetCheckpointName(fixture.Instance, second.RootId);
        await fixture.WriteAsync(firstName, [1]); await fixture.WriteAsync(secondName, [2]);
        async Task<string> Fingerprint(params RootRegistration[] roots) =>
            (await MirrorPulseCloudStatusReader.ReadAsync(fixture.Store, [fixture.Instance], roots, CancellationToken.None)).Cursors[0].CursorFingerprint!;
        string original = await Fingerprint(first, second);
        Assert.AreEqual(original, await Fingerprint(second, first, first));
        var renamed = new RootRegistration(first.AdapterId, first.InstanceId, first.RootId, first.UniquenessKey,
            "Renamed", "Renamed", first.CustomEntry, RootRegistrationState.Disabled, first.RegisteredAt, first.IdentityScope);
        Assert.AreEqual(original, await Fingerprint(renamed, second));
        await fixture.WriteAsync(firstName, [2]); await fixture.WriteAsync(secondName, [1]);
        Assert.AreNotEqual(original, await Fingerprint(first, second), "Swapping opaque values between stable roots must change the display summary.");
        Assert.AreNotEqual(await Fingerprint(first), await Fingerprint(second));
    }

    [TestMethod]
    public async Task StreamedAndRootCursorsBothContributeWithoutChangingTheLegacyOnlyContract()
    {
        await using var fixture = await CursorFixture.CreateAsync(); var root = fixture.Root("files");
        string streamedName = MirrorPulseRemoteBatchCoordinator.GetCheckpointName(fixture.Instance);
        string rootName = MirrorPulseRemoteBatchCoordinator.GetCheckpointName(fixture.Instance, root.RootId);
        await fixture.WriteAsync(streamedName, [9]);
        string legacy = (await MirrorPulseCloudStatusReader.ReadAsync(fixture.Store, [fixture.Instance])).Cursors[0].CursorFingerprint!;
        Assert.AreEqual(legacy, (await MirrorPulseCloudStatusReader.ReadAsync(fixture.Store, [fixture.Instance], [root], CancellationToken.None)).Cursors[0].CursorFingerprint);
        await fixture.WriteAsync(rootName, [1]);
        string both = (await MirrorPulseCloudStatusReader.ReadAsync(fixture.Store, [fixture.Instance], [root], CancellationToken.None)).Cursors[0].CursorFingerprint!;
        Assert.AreNotEqual(legacy, both);
        await fixture.WriteAsync(streamedName, [8]);
        Assert.AreNotEqual(both, (await MirrorPulseCloudStatusReader.ReadAsync(fixture.Store, [fixture.Instance], [root], CancellationToken.None)).Cursors[0].CursorFingerprint);
    }

    [TestMethod]
    public async Task MissingAndForeignRootCursorsCannotInventProgressForAnotherInstance()
    {
        await using var fixture = await CursorFixture.CreateAsync();
        var ownRoot = fixture.Root("own"); var foreignRoot = fixture.Root("foreign", instance: InstanceId.New());
        await fixture.WriteAsync(MirrorPulseRemoteBatchCoordinator.GetCheckpointName(foreignRoot.InstanceId, foreignRoot.RootId), [4]);
        var snapshot = await MirrorPulseCloudStatusReader.ReadAsync(fixture.Store, [fixture.Instance, foreignRoot.InstanceId], [ownRoot, foreignRoot], CancellationToken.None);
        var missing = snapshot.Cursors.Single(cursor => cursor.InstanceId == fixture.Instance);
        Assert.IsFalse(missing.HasCursor); Assert.IsNull(missing.CursorFingerprint); Assert.IsNull(missing.UpdatedAt);
        Assert.IsTrue(snapshot.Cursors.Single(cursor => cursor.InstanceId == foreignRoot.InstanceId).HasCursor);
    }

    private sealed class CursorFixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-root-cursor-status", Guid.NewGuid().ToString("N"));
        internal InstanceId Instance { get; } = InstanceId.New();
        internal DateTimeOffset Timestamp { get; } = DateTimeOffset.UtcNow;
        internal MirrorPulseStoragePaths Paths { get; private set; } = null!;
        internal ICloudStateStore Store { get; private set; } = null!;
        internal static async Task<CursorFixture> CreateAsync()
        {
            var fixture = new CursorFixture();
            fixture.Paths = new(Path.Combine(fixture._directory, "sync"), Path.Combine(fixture._directory, "data"));
            Directory.CreateDirectory(fixture.Paths.SyncRootPath);
            fixture.Store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
            return fixture;
        }
        internal RootRegistration Root(string name, RootRegistrationState state = RootRegistrationState.Active, InstanceId? instance = null) =>
            AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.cursor-status"), instance ?? Instance,
                new(name, name, name, false), state, identityScope: RootIdentityScope.InstanceRoot);
        internal async Task WriteAsync(string name, byte[] value)
        {
            await using var transaction = await Store.BeginTransactionAsync();
            await transaction.Checkpoints.UpsertAsync(new(name, value, Timestamp)); await transaction.CommitAsync();
        }
        internal async Task ReopenAsync() { await Store.DisposeAsync(); Store = await MirrorPulseCfSharpStateStoreFactory.Create(Paths).OpenAsync(new(Paths.SyncRootPath)); }
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); Directory.Delete(_directory, true); }
    }
}
