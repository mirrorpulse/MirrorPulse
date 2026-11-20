using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootLabelCatalogTests
{
    private static readonly string[] ExpectedNames = ["Docs", "My Files"];

    [TestMethod]
    public async Task RecoveryReadsRootLabelsInPagesAndRejectsDamageAfterTheFirstPage()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-label-pages", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            var manifest = new AdapterManifest(1, AdapterId.Parse("example.label-pages"), "Example", "1.0.0", new(2, 2),
                new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
            var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(directory, "installed"),
                new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
            await catalog.AddInstallationAsync(installation);
            for (int index = 1; index <= 18; index++)
            {
                string label = $"Docs {index}";
                await catalog.CreateInstanceAsync(installation.InstallId, label, new Dictionary<string, string>(), [],
                    Path.Combine(directory, "files", label), Path.Combine(directory, "transfers", label), enabled: index % 2 == 0,
                    rootLabels: new Dictionary<string, string> { ["docs"] = label });
            }
            var registrations = (await catalog.ReadAdapterTopologyAsync()).Roots
                .OrderBy(root => root.RootId.ToString(), StringComparer.Ordinal).ToArray();
            var renames = new List<MirrorPulseRootRenameIntent>();
            foreach (RootRegistration registration in registrations)
                renames.Add(await catalog.PrepareManagedRootRenameAsync(registration.RootId, "Proposed " + registration.Label));
            var last = renames[^1];
            renames[^1] = await catalog.TransitionManagedRootRenameAsync(last.OperationId,
                MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);

            var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog).GetField("_connection", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(catalog)!;
            var statements = new List<string>();
            SQLitePCL.strdelegate_trace trace = (_, sql) => statements.Add(sql);
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
            try
            {
                var expected = renames.Where(intent => intent.IsPending).Select(intent => intent.RootId).OrderBy(root => root.Value).ToArray();
                CollectionAssert.AreEqual(expected, (await catalog.ReadNamespaceRecoveryRootsAsync()).ToArray());
            }
            finally
            {
                SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
                GC.KeepAlive(trace);
            }
            var pages = statements.Where(sql => sql.StartsWith("SELECT root_id,payload FROM (", StringComparison.Ordinal) &&
                sql.Contains("FROM managed_root_renames", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(2, pages);
            Assert.IsTrue(pages.All(sql => sql.Contains("LIMIT 16", StringComparison.Ordinal)));
            CollectionAssert.AreEqual(renames.ToArray(), (await catalog.ReadManagedRootRenamesAsync()).ToArray());

            await using var damage = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = paths.ProductCatalogDatabasePath,
                Pooling = false
            }.ToString());
            await damage.OpenAsync();
            await using var command = damage.CreateCommand();
            command.CommandText = "SELECT payload FROM managed_root_renames WHERE root_id=$root;";
            command.Parameters.AddWithValue("$root", last.RootId.ToString());
            string original = (string)(await command.ExecuteScalarAsync())!;
            command.CommandText = "UPDATE managed_root_renames SET payload='{}' WHERE root_id=$root;";
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRecoveryRootsAsync());

            command.CommandText = "UPDATE managed_root_renames SET root_id=NULL,payload=$payload WHERE root_id=$root;";
            command.Parameters.AddWithValue("$payload", original);
            Assert.AreEqual(1, await command.ExecuteNonQueryAsync());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task RootLabelChangePersistsWithoutReplacingIdentitySourceSettingsOrOtherRoots(bool enabled)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-label-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.labels"), "Example", "1.0.0",
            new ProtocolVersionRange(1, 1), new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new AdapterInstallPolicy(null), new AdapterInstancePolicy(null, null),
            new AdapterCapabilities(true, false, true, true), ["en-US"], "1.0.0",
            [new("docs", "Docs", "Docs", false), new("archive", "Archive", "Archive", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(root, "installed"),
            new Sha256Digest(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        RootRegistration original;
        AdapterInstance instance;
        Guid pendingRename;
        string source = Path.Combine(root, "source");
        try
        {
            Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "source remains unchanged");
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                await catalog.AddInstallationAsync(installation);
                instance = await catalog.CreateInstanceAsync(installation.InstallId, "Storage", new Dictionary<string, string> { ["sourceDirectory"] = source },
                    [], Path.Combine(root, "cache", "files"), Path.Combine(root, "cache", "transfers"), enabled);
                original = (await catalog.ReadAdapterTopologyAsync()).Roots.Single(item => item.UniquenessKey == "docs");
                RootRegistration renamed = await catalog.RenameManagedRootAsync(original.RootId, "My Files");
                Assert.AreEqual("My Files", renamed.Label);
                Assert.AreEqual(original.RootId, renamed.RootId);
                Assert.AreEqual(original.State, renamed.State);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RenameManagedRootAsync(original.RootId, "archive"));
                await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RenameManagedRootAsync(original.RootId, "../outside"));
                await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RenameManagedRootAsync(RootId.New(), "Unknown"));
                RootRegistration sibling = (await catalog.ReadAdapterTopologyAsync()).Roots.Single(item => item.UniquenessKey == "archive");
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RenameManagedRootAsync(sibling.RootId, "docs"));
                MirrorPulseAdapterTopology before = await catalog.ReadAdapterTopologyAsync();
                var reused = new RootRegistration(sibling.AdapterId, sibling.InstanceId, sibling.RootId, sibling.UniquenessKey,
                    "Docs", "Docs", sibling.CustomEntry, sibling.State, sibling.RegisteredAt, sibling.IdentityScope);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.SaveAdapterTopologyAsync(
                    new(before.Installations, before.Instances, before.Roots.Select(item => item.RootId == sibling.RootId ? reused : item).ToArray())));
                // Returning to this same root's old name is safe. Its history is
                // retained so old journal entries still have one stable owner.
                await catalog.RenameManagedRootAsync(original.RootId, "Docs");
                await catalog.RenameManagedRootAsync(original.RootId, "My Files");
                MirrorPulseRootRenameIntent pending = await catalog.PrepareManagedRootRenameAsync(original.RootId, "Queued Name");
                pendingRename = pending.OperationId;
                Assert.AreEqual("My Files", pending.SourceName);
                Assert.AreEqual(pendingRename, (await catalog.PrepareManagedRootRenameAsync(original.RootId, "Queued Name")).OperationId);
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RenameManagedRootAsync(sibling.RootId, "Queued Name"));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RenameManagedRootAsync(original.RootId, "Unrelated"));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.TransitionManagedRootRenameAsync(pendingRename,
                    MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Completed));
            }
            await using (MirrorPulseProductCatalog catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
                RootRegistration renamed = topology.Roots.Single(item => item.RootId == original.RootId);
                Assert.AreEqual("My Files", renamed.DirectoryName);
                Assert.AreEqual(original.InstanceId, renamed.InstanceId);
                Assert.AreEqual(original.AdapterId, renamed.AdapterId);
                Assert.AreEqual(original.UniquenessKey, renamed.UniquenessKey);
                Assert.AreEqual(original.RegisteredAt, renamed.RegisteredAt);
                Assert.AreEqual(original.IdentityScope, renamed.IdentityScope);
                Assert.AreEqual("Archive", topology.Roots.Single(item => item.UniquenessKey == "archive").Label);
                AdapterInstance retained = topology.Instances.Single();
                Assert.AreEqual(instance.InstanceId, retained.InstanceId);
                Assert.AreEqual(instance.InstallId, retained.InstallId);
                Assert.AreEqual(instance.Enabled, retained.Enabled);
                Assert.AreEqual(source, retained.Configuration["sourceDirectory"]);
                Assert.AreEqual("source remains unchanged", await File.ReadAllTextAsync(Path.Combine(source, "keep.txt")));
                Assert.IsFalse(Directory.Exists(Path.Combine(root, "My Files")));
                IReadOnlyList<MirrorPulseManagedRootName> names = await catalog.ReadManagedRootNamesAsync();
                Assert.HasCount(2, names);
                Assert.IsTrue(names.All(name => name.RootId == original.RootId));
                CollectionAssert.AreEquivalent(ExpectedNames, names.Select(name => name.DirectoryName).ToArray());
                MirrorPulseRootRenameIntent pending = (await catalog.ReadManagedRootRenamesAsync()).Single();
                Assert.AreEqual(pendingRename, pending.OperationId);
                Assert.AreEqual(MirrorPulseRootRenamePhase.Prepared, pending.Phase);
                Assert.AreEqual("Queued Name", pending.TargetName);
                Assert.AreEqual(original.RootId, pending.RootId);
                await catalog.TransitionManagedRootRenameAsync(pendingRename, MirrorPulseRootRenamePhase.Prepared,
                    MirrorPulseRootRenamePhase.Cancelled);
                Assert.IsFalse((await catalog.ReadManagedRootRenamesAsync()).Single().IsPending);
                RootId sibling = topology.Roots.Single(item => item.UniquenessKey == "archive").RootId;
                Assert.AreEqual("Queued Name", (await catalog.RenameManagedRootAsync(sibling, "Queued Name")).Label);
                Assert.AreEqual("My Files", (await catalog.ReadAdapterTopologyAsync()).Roots.Single(item => item.RootId == original.RootId).Label);
            }
        }
        finally { Directory.Delete(root, true); }
    }
}
