using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    private const string HistoricalPreview7Version = "0.1.0-preview.7";
    private const string HistoricalPreview7Source = "137d6e2a9c1b15f337e8e7ca3566d5e3f29fcdb9";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Preview7RemoteUpdateReplaysItsOriginalPublicationAfterReopen(bool started)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceRemoteUpdate original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            var plan = await RemoteUpdatePlanAsync(catalog, setup.Member);
            original = await catalog.PrepareNamespaceRemoteUpdateAsync(plan);
            if (started) original = await catalog.StartNamespaceRemoteUpdateAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1));
        }
        // This is a historical product-catalog fixture, not SDK or native proof.
        await StagePreview7CatalogPublicationAsync(fixture.Paths, "update", original.Plan.OperationId);
        original = original with { Plan = original.Plan with { PackageVersion = HistoricalPreview7Version, PackageSource = HistoricalPreview7Source } };

        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, await reopened.ReadNamespaceRemoteUpdateAsync(original.Plan.OperationId));
        Assert.AreEqual(original, await reopened.PrepareNamespaceRemoteUpdateAsync(original.Plan));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceRemoteUpdateAsync(original.Plan with
        { PackageVersion = MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, PackageSource = MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit }));
        Assert.AreEqual(original, await reopened.ReadNamespaceRemoteUpdateAsync(original.Plan.OperationId));
        Assert.IsNull(original.Outcome);
        CollectionAssert.AreEqual(new[] { original.Plan.Child.Context.Member.RootId!.Value }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Preview7ExplicitDeleteRemainsReadableAndImmutableAfterReopen(bool started)
    {
        using var fixture = new Fixture();
        MirrorPulseRemoteDeleteResolution original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var plan = await PrepareDeleteDecisionFixtureAsync(catalog, 0);
            original = await catalog.PrepareRemoteDeleteResolutionAsync(plan);
            if (started) original = await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
        }
        await StagePreview7CatalogPublicationAsync(fixture.Paths, "delete", original.Plan.Decision.CommandId);
        original = original with
        {
            Plan = original.Plan with
            {
                Decision = original.Plan.Decision with
                { PackageVersion = HistoricalPreview7Version, PackageSource = HistoricalPreview7Source }
            }
        };

        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await reopened.ReadRemoteDeleteResolutionAsync(original.Plan.Decision.CommandId);
        Assert.IsNotNull(retained);
        Assert.AreEqual(original.Plan.Decision, retained.Plan.Decision);
        CollectionAssert.AreEqual(original.Plan.Entries.ToArray(), retained.Plan.Entries.ToArray());
        Assert.AreEqual(original.StartedAt, retained.StartedAt);
        Assert.AreEqual(original.NextOrdinal, retained.NextOrdinal);
        var replay = await reopened.PrepareRemoteDeleteResolutionAsync(retained.Plan);
        Assert.AreEqual(retained.Plan.Decision, replay.Plan.Decision);
        Assert.AreEqual(retained.StartedAt, replay.StartedAt);
        Assert.IsNull(replay.Outcome);
        Assert.IsNull(replay.CompletedAt);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareRemoteDeleteResolutionAsync(retained.Plan with
        { Decision = retained.Plan.Decision with { PackageVersion = MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, PackageSource = MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit } }));
        Assert.AreEqual(original.Plan.Decision, (await reopened.ReadRemoteDeleteResolutionAsync(original.Plan.Decision.CommandId))!.Plan.Decision);
        CollectionAssert.AreEqual(new[] { original.Plan.Entries[0].Original.Context.Member.RootId!.Value }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Preview7CannotAcquireANewUpdateOrSealedDeleteOwner(bool deletion)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceRemoteUpdatePlan? update = null;
        MirrorPulseRemoteDeleteResolutionPlan? delete = null;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            if (deletion) delete = await PrepareDeleteDecisionFixtureAsync(catalog, 0);
            else
            {
                var setup = await PrepareMovableMemberAsync(catalog);
                update = await RemoteUpdatePlanAsync(catalog, setup.Member);
            }
        }
        if (deletion) await StagePreview7CatalogPublicationAsync(fixture.Paths, "decision-only", delete!.Decision.CommandId);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        if (deletion)
        {
            var current = delete ?? throw new InvalidOperationException("The delete fixture was not prepared.");
            var plan = current with { Decision = current.Decision with { PackageVersion = HistoricalPreview7Version, PackageSource = HistoricalPreview7Source } };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareRemoteDeleteResolutionAsync(plan));
            Assert.IsNull(await reopened.ReadRemoteDeleteResolutionAsync(plan.Decision.CommandId));
        }
        else
        {
            var current = update ?? throw new InvalidOperationException("The update fixture was not prepared.");
            var plan = current with { PackageVersion = HistoricalPreview7Version, PackageSource = HistoricalPreview7Source };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceRemoteUpdateAsync(plan));
            Assert.IsNull(await reopened.ReadNamespaceRemoteUpdateAsync(plan.OperationId));
        }
        Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    private static async Task StagePreview7CatalogPublicationAsync(MirrorPulseStoragePaths paths, string boundary, Guid id)
    {
        (string Table, string Key)[] tables = boundary switch
        {
            "update" => [("namespace_remote_updates", "operation_id")],
            "delete" => [("remote_delete_resolutions", "command_id"), ("remote_conflict_actions", "command_id")],
            "decision-only" => [("remote_conflict_actions", "command_id")],
            _ => throw new ArgumentException("Unknown historical catalog fixture boundary.", nameof(boundary))
        };
        await using var connection = Connection(paths);
        await connection.OpenAsync();
        using var transaction = connection.BeginTransaction();
        foreach (var table in tables)
        {
            await using var read = connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = $"SELECT payload FROM {table.Table} WHERE {table.Key}=$id;";
            read.Parameters.AddWithValue("$id", id.ToString("D"));
            var payload = (byte[])(await read.ExecuteScalarAsync())!;
            string current = Encoding.UTF8.GetString(payload);
            StringAssert.Contains(current, MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion);
            StringAssert.Contains(current, MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit);
            byte[] historical = Encoding.UTF8.GetBytes(current.Replace(MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, HistoricalPreview7Version,
                StringComparison.Ordinal).Replace(MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit, HistoricalPreview7Source, StringComparison.Ordinal));
            await using var write = connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = $"UPDATE {table.Table} SET payload=$payload,fingerprint=$hash WHERE {table.Key}=$id;";
            write.Parameters.AddWithValue("$id", id.ToString("D"));
            write.Parameters.AddWithValue("$payload", historical);
            write.Parameters.AddWithValue("$hash", SHA256.HashData(historical));
            Assert.AreEqual(1, await write.ExecuteNonQueryAsync());
        }
        transaction.Commit();
    }
}
