using CfSharp;
using Microsoft.Data.Sqlite;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRootIdentityMigrationTests
{
    [TestMethod]
    public void RootScopedIdsAreStableAndAnIdentityCannotCrossRoots()
    {
        InstanceId instance = InstanceId.New();
        RootRegistration Root(string key) => AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.drive"), instance,
            new(key, key, key, false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        RootRegistration[] roots = [Root("left"), Root("right")];
        string path = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var firstRouter = new MirrorPulseRootRouter(path, roots);
        CloudPlaceholderIdentity left = firstRouter.CreateFileIdentity(instance, "left", "same-id", "revision");
        CloudPlaceholderIdentity right = firstRouter.CreateFileIdentity(instance, "right", "same-id", "revision");
        Assert.AreNotEqual(left.ItemId, right.ItemId);
        Assert.AreNotEqual(left.RemoteId, right.RemoteId);
        MirrorPulsePlaceholderIdentity decoded = MirrorPulsePlaceholderIdentity.Decode(instance, left.Encode());
        Assert.AreEqual("left", decoded.RootKey);
        Assert.AreEqual("same-id", decoded.RemoteId);
        CollectionAssert.AreEqual(left.Encode(), decoded.Encode());
        Assert.AreEqual(left.ItemId, firstRouter.CreateFileIdentity(instance, "left", "same-id", "later-revision").ItemId);
        var restarted = new MirrorPulseRootRouter(path, roots);
        CollectionAssert.AreEqual(left.Encode(), restarted.CreateFileIdentity(instance, "left", "same-id", "revision").Encode());
        Assert.AreEqual("left", restarted.Resolve(Path.Combine(path, "left", "same.txt"), left.Encode()).RootKey);
        Assert.ThrowsExactly<InvalidDataException>(() => restarted.Resolve(Path.Combine(path, "right", "same.txt"), left.Encode()));
        Assert.ThrowsExactly<InvalidDataException>(() => restarted.Resolve(Path.Combine(path, "left", "same.txt"),
            MirrorPulsePlaceholderIdentity.Create(instance, "same-id", "revision").Encode()));
    }

    [TestMethod]
    public async Task OldTopologyAndOfficialSqliteRetainExistingIdsWhenAScopedRootIsAdded()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        InstanceId instance = InstanceId.New();
        AdapterId adapter = AdapterId.Parse("example.drive");
        InstallId install = InstallId.New();
        var manifest = new AdapterManifest(1, adapter, "Example", "1.0.0", new(1, 2),
            new Dictionary<string, string> { ["win-x64"] = "worker.exe", ["win-arm64"] = "worker.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0");
        var installed = new InstalledAdapter(manifest, install, directory, new(new string('A', 64)),
            AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var configured = new AdapterInstance(adapter, install, instance, "Example", new Dictionary<string, string>(), [],
            Path.Combine(directory, "files"), Path.Combine(directory, "transfers"), true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
        RootRegistration legacy = AdapterRootRegistrationMapper.Map(adapter, instance, new("legacy", "Legacy", "Legacy", false), RootRegistrationState.Active);
        CloudPlaceholderIdentity original = MirrorPulsePlaceholderIdentity.Create(instance, "same-id", "revision").ToCfSharp();
        RootRegistration modern = AdapterRootRegistrationMapper.Map(adapter, instance, new("modern", "Modern", "Modern", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        try
        {
            Directory.CreateDirectory(paths.SyncRootPath);
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
                await catalog.SaveAdapterTopologyAsync(new([installed], [configured], [legacy]));
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using var removeField = connection.CreateCommand();
                removeField.CommandText = "UPDATE adapter_topology SET payload=json_remove(payload, '$.Roots[0].IdentityScope');";
                await removeField.ExecuteNonQueryAsync();
                removeField.CommandText = "SELECT payload FROM adapter_topology WHERE id=1;";
                Assert.DoesNotContain((string)(await removeField.ExecuteScalarAsync())!, "IdentityScope");
            }
            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                MirrorPulseAdapterTopology old = await reopened.ReadAdapterTopologyAsync();
                Assert.AreEqual(RootIdentityScope.LegacyInstance, old.Roots[0].IdentityScope);
                await reopened.SaveAdapterTopologyAsync(old with { Roots = [old.Roots[0], modern] });
            }
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
                var router = new MirrorPulseRootRouter(paths.SyncRootPath, topology.Roots);
                CollectionAssert.AreEqual(original.Encode(), router.CreateFileIdentity(instance, "legacy", "same-id", "revision").Encode());
                CloudPlaceholderIdentity scoped = router.CreateFileIdentity(instance, "modern", "same-id", "revision");
                await using ICloudStateStore store = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new(paths.SyncRootPath));
                await using ICloudStateTransaction transaction = await store.BeginTransactionAsync();
                await transaction.Items.UpsertAsync(new(original.ItemId, original.RemoteId, "Legacy\\same.txt", CloudItemKind.File, null, null, false, DateTimeOffset.UtcNow));
                await transaction.Items.UpsertAsync(new(scoped.ItemId, scoped.RemoteId, "Modern\\same.txt", CloudItemKind.File, null, null, false, DateTimeOffset.UtcNow));
                await transaction.CommitAsync();
            }
            await using ICloudStateStore verified = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new(paths.SyncRootPath));
            await using ICloudStateTransaction read = await verified.BeginTransactionAsync();
            Assert.AreEqual("Legacy\\same.txt", (await read.Items.GetByItemIdAsync(original.ItemId))!.RelativePath);
            Assert.IsNotNull(await read.Items.GetByItemIdAsync(MirrorPulsePlaceholderIdentity.CreateForRoot(modern, "same-id", "revision").ToCfSharp().ItemId));
            await read.RollbackAsync();
        }
        finally { Directory.Delete(directory, true); }
    }
}
