using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseRemoteNamespaceFenceTests
{
    [TestMethod]
    public async Task DurableRenameFencesBothSidesOfRemoteMoveAcrossCatalogRestart()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-namespace-fence", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        AdapterId adapter = AdapterId.Parse("example.namespace-fence");
        InstanceId instance = InstanceId.New();
        RootRegistration Root(string key, string label) => AdapterRootRegistrationMapper.Map(adapter, instance,
            new AdapterRootDefinition(key, label, label, false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        RootRegistration docs = Root("docs", "Docs");
        RootRegistration archive = Root("archive", "Archive");
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [docs, archive]);
        Guid operation = Guid.Empty;
        try
        {
            for (int run = 0; run < 2; run++)
            {
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
                if (run == 0)
                {
                    var manifest = new AdapterManifest(1, adapter, "Example", "1.0.0", new(1, 1),
                        new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                        new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0",
                        [new("docs", "Docs", "Docs", false), new("archive", "Archive", "Archive", false)]);
                    var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(directory, "installed"),
                        new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
                    var configured = new AdapterInstance(adapter, installation.InstallId, instance, "Example",
                        new Dictionary<string, string>(), [], Path.Combine(directory, "files"), Path.Combine(directory, "transfers"),
                        true, AdapterLifecycleState.Enabled, null, DateTimeOffset.UtcNow);
                    await catalog.SaveAdapterTopologyAsync(new([installation], [configured], [docs, archive]));
                    operation = (await catalog.PrepareManagedRootRenameAsync(docs.RootId, "My Files")).OperationId;
                }
                var fence = new MirrorPulseRemoteNamespaceFence(router, catalog);
                Assert.IsFalse(await fence.CanPollAsync(docs, default));
                Assert.IsTrue(await fence.CanPollAsync(archive, default));
                CloudRemoteChangeBatch Batch(string path, string? previous = null) => new("remote-batch", new byte[] { 1 },
                    [new CloudRemoteChange("change", previous is null ? CloudRemoteChangeKind.FileUpsert : CloudRemoteChangeKind.Move,
                        "remote-file", "v2", CloudItemKind.File, path, previousRelativePath: previous,
                        length: 1, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build())], new byte[] { 2 });
                await fence.EnsureApplyAllowedAsync(instance, Batch("Archive\\report.txt"), default);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    fence.EnsureApplyAllowedAsync(instance, Batch("Docs\\report.txt"), default).AsTask());
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    fence.EnsureApplyAllowedAsync(instance, Batch("Archive\\report.txt", "Docs\\report.txt"), default).AsTask());
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    fence.EnsureApplyAllowedAsync(instance, new("cursor-only", new byte[] { 1 }, [], new byte[] { 2 }), default).AsTask());
                Assert.AreEqual(operation, (await catalog.ReadManagedRootRenamesAsync()).Single().OperationId);
                Assert.AreEqual("Docs", (await catalog.ReadAdapterTopologyAsync()).Roots.Single(root => root.RootId == docs.RootId).DirectoryName);
                if (run == 1)
                {
                    await catalog.TransitionManagedRootRenameAsync(operation, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
                    Assert.IsTrue(await fence.CanPollAsync(docs, default));
                    await fence.EnsureApplyAllowedAsync(instance, Batch("Docs\\report.txt"), default);
                }
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }
}
