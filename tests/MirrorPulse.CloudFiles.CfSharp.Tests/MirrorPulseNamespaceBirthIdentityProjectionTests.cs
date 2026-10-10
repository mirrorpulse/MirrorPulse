using CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseNamespaceBirthIdentityProjectionTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OfficialProjectionPrecedesNativeStartAndReplaysWithoutInventingBindingOrJournal(bool directory)
    {
        await using var fixture = await Fixture.OpenAsync(directory);
        Assert.IsTrue(await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(fixture.Catalog, fixture.Store, fixture.Plan.OperationId));
        await fixture.Catalog.RecordNamespaceBirthStartAsync(new(1, fixture.Plan.OperationId, DateTimeOffset.UtcNow));
        Assert.IsFalse(await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(fixture.Catalog, fixture.Store, fixture.Plan.OperationId));
        await using var read = await fixture.Store.BeginTransactionAsync();
        var item = await read.Items.GetByItemIdAsync(fixture.Plan.ItemId);
        Assert.IsNotNull(item);
        Assert.AreEqual(fixture.Birth.RelativePath, item.RelativePath);
        Assert.AreEqual(fixture.Plan.RemoteId, item.RemoteId);
        Assert.AreEqual(directory ? CloudItemKind.Directory : CloudItemKind.File, item.Kind);
        Assert.IsNull(item.RemoteRevision);
        Assert.IsNull(item.LocalFileId);
        Assert.AreEqual(fixture.Plan.PreparedAt, item.UpdatedAt);
        Assert.HasCount(0, await read.Operations.ListAsync(16));
        await read.RollbackAsync();
        Assert.IsNull(await fixture.Catalog.ReadNamespaceBirthObservationAsync(fixture.Plan.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task MissingOfficialRowAfterNativeStartRequiresRecoveryWithoutInventingAnItem()
    {
        await using var fixture = await Fixture.OpenAsync(false);
        await fixture.Catalog.RecordNamespaceBirthStartAsync(new(1, fixture.Plan.OperationId, DateTimeOffset.UtcNow));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(
            fixture.Catalog, fixture.Store, fixture.Plan.OperationId));
        await using var read = await fixture.Store.BeginTransactionAsync();
        Assert.IsNull(await read.Items.GetByItemIdAsync(fixture.Plan.ItemId));
        Assert.HasCount(0, await read.Operations.ListAsync(16));
    }

    [TestMethod]
    [DataRow("item")]
    [DataRow("path")]
    [DataRow("remote")]
    [DataRow("revision")]
    [DataRow("kind")]
    [DataRow("tombstone")]
    [DataRow("remote-index")]
    public async Task ConflictingOfficialProjectionIsRetainedAndNeverAdopted(string conflict)
    {
        await using var fixture = await Fixture.OpenAsync(false);
        var existing = new CloudItemState(conflict is "item" or "remote-index" ? Guid.NewGuid() : fixture.Plan.ItemId,
            conflict == "remote" ? "another-remote" : fixture.Plan.RemoteId,
            conflict is "path" or "remote-index" ? "Docs/another" : fixture.Birth.RelativePath,
            conflict == "kind" ? CloudItemKind.Directory : CloudItemKind.File,
            conflict == "revision" ? "not-this-birth" : null, 42L, conflict == "tombstone", DateTimeOffset.UtcNow);
        await using (var write = await fixture.Store.BeginTransactionAsync())
        { await write.Items.UpsertAsync(existing); await write.CommitAsync(); }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(
            fixture.Catalog, fixture.Store, fixture.Plan.OperationId));
        await using var read = await fixture.Store.BeginTransactionAsync();
        var retained = await read.Items.GetByItemIdAsync(existing.ItemId);
        Assert.IsNotNull(retained);
        Assert.AreEqual(existing.LocalFileId, retained.LocalFileId);
        Assert.AreEqual(existing.RelativePath, retained.RelativePath);
        Assert.AreEqual(existing.UpdatedAt, retained.UpdatedAt);
        Assert.IsNull(await fixture.Catalog.ReadNamespaceBirthStartAsync(fixture.Plan.OperationId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExistingNativeProjectionIsNotAdoptedBeforeStartAndIsNotRewrittenOnReplay(bool started)
    {
        await using var fixture = await Fixture.OpenAsync(false);
        if (started) await fixture.Catalog.RecordNamespaceBirthStartAsync(new(1, fixture.Plan.OperationId, DateTimeOffset.UtcNow));
        DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        await using (var write = await fixture.Store.BeginTransactionAsync())
        {
            await write.Items.UpsertAsync(new(fixture.Plan.ItemId, fixture.Plan.RemoteId, fixture.Birth.RelativePath,
                CloudItemKind.File, null, 42L, false, observedAt));
            await write.CommitAsync();
        }
        if (started) Assert.IsFalse(await MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(fixture.Catalog, fixture.Store, fixture.Plan.OperationId));
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseNamespaceBirthIdentityProjection.PrepareAsync(
            fixture.Catalog, fixture.Store, fixture.Plan.OperationId));
        await using var read = await fixture.Store.BeginTransactionAsync();
        var row = await read.Items.GetByItemIdAsync(fixture.Plan.ItemId);
        Assert.IsNotNull(row);
        Assert.AreEqual(42L, row.LocalFileId);
        Assert.AreEqual(observedAt, row.UpdatedAt);
        Assert.HasCount(0, await read.Operations.ListAsync(16));
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-birth-projection-tests", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; private set; } = null!;
        public MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        public ICloudStateStore Store { get; private set; } = null!;
        public MirrorPulseNamespaceBirthIntent Birth { get; private set; } = null!;
        public MirrorPulseNamespaceBirthPlan Plan { get; private set; } = null!;

        public static async Task<Fixture> OpenAsync(bool directory)
        {
            var fixture = new Fixture();
            try
            {
                fixture.Paths = new(Path.Combine(fixture._directory, "sync"), Path.Combine(fixture._directory, "data"));
                fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
                fixture.Store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
                const string owner = "S-1-5-21-100-200-300-1001", role = "S-1-5-5-123-456";
                string original = Canonical($"D:(A;OICI;FA;;;{owner})");
                string protectedDacl = Canonical($"D:P(A;OICI;FRFW;;;{owner})(A;OICI;FA;;;{role})");
                var binding = new MirrorPulseLocalFileBinding(7, Guid.NewGuid(), Guid.NewGuid());
                var baseline = new MirrorPulseNamespacePermissionBaseline(Guid.NewGuid(), RootId.New(), binding, "Docs", true, owner, original, DateTimeOffset.UtcNow);
                var intent = new MirrorPulseNamespacePermissionIntent(Guid.NewGuid(), baseline.EvidenceId, binding, baseline.RootId,
                    "Docs", MirrorPulseNamespacePermissionChangeKind.Protect, role, original, protectedDacl, DateTimeOffset.UtcNow);
                await fixture.Catalog.PrepareNamespacePermissionChangeAsync(baseline, intent);
                await fixture.Catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, binding, DateTimeOffset.UtcNow);
                await fixture.Catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId, new(binding, owner, protectedDacl, DateTimeOffset.UtcNow));
                fixture.Birth = new(1, Guid.NewGuid(), baseline.RootId!.Value, baseline.EvidenceId, intent.OperationId, binding,
                    "Docs/born", directory, MirrorPulseNamespaceBirthOrigin.ControlledCreation, role, DateTimeOffset.UtcNow);
                await fixture.Catalog.PrepareNamespaceBirthAsync(fixture.Birth);
                fixture.Plan = new(1, fixture.Birth.OperationId, Guid.NewGuid(), "local:planned", null, DateTimeOffset.UtcNow);
                await fixture.Catalog.PrepareNamespaceBirthPlanAsync(fixture.Plan);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        private static string Canonical(string value) => new System.Security.AccessControl.RawSecurityDescriptor(value)
            .GetSddlForm(System.Security.AccessControl.AccessControlSections.Access);

        public async ValueTask DisposeAsync()
        {
            if (Store is not null) await Store.DisposeAsync();
            if (Catalog is not null) await Catalog.DisposeAsync();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        }
    }
}
