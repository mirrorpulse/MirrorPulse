using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseRootRenameEvidenceTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task EncodedDirectoryEvidenceSurvivesRestartAndCannotBeAddedOrReplacedLater(bool includeEvidence)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-rename-envelope", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var proof = new MirrorPulseRootRenameProof(Guid.NewGuid(), new(1, Guid.NewGuid(), Guid.NewGuid()),
            Convert.ToBase64String([1, 2, 3]), DateTimeOffset.UtcNow, includeEvidence ? Convert.ToBase64String([4, 5, 6]) : null);
        Guid operation;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                RootRegistration root = await AddRootAsync(catalog, directory);
                operation = (await catalog.PrepareManagedRootRenameAsync(root.RootId, "New Files")).OperationId;
                await catalog.SaveManagedRootRenameProofAsync(operation, proof);
            }
            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                Assert.AreEqual(proof, (await reopened.ReadManagedRootRenameHistoryAsync()).Single().Proof);
                await reopened.SaveManagedRootRenameProofAsync(operation, proof);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.SaveManagedRootRenameProofAsync(operation,
                    proof with { DirectoryMoveEvidence = Convert.ToBase64String([7, 8, 9]) }));
                if (includeEvidence)
                    await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.SaveManagedRootRenameProofAsync(operation,
                        proof with { DirectoryMoveEvidence = null }));
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("not base64")]
    [DataRow("AQ==\n")]
    [DataRow("oversize")]
    public async Task InvalidDirectoryEvidenceIsRejectedBeforeAnOriginalProofIsSaved(string value)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-rename-envelope", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        try
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            RootRegistration root = await AddRootAsync(catalog, directory);
            Guid operation = (await catalog.PrepareManagedRootRenameAsync(root.RootId, "New Files")).OperationId;
            var proof = new MirrorPulseRootRenameProof(Guid.NewGuid(), new(1, Guid.NewGuid(), Guid.NewGuid()),
                Convert.ToBase64String([1]), DateTimeOffset.UtcNow,
                value == "oversize" ? Convert.ToBase64String(new byte[131073]) : value);
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.SaveManagedRootRenameProofAsync(operation, proof));
            Assert.IsNull((await catalog.ReadManagedRootRenameHistoryAsync()).Single().Proof);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task CompletedRenameKeepsOriginalProofWhenTheNextRenameIsPreparedAndRestarted()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-rename-evidence", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var binding = new MirrorPulseLocalFileBinding(1, Guid.NewGuid(), Guid.NewGuid());
        var proof = new MirrorPulseRootRenameProof(Guid.NewGuid(), binding, Convert.ToBase64String([1, 2, 3]), DateTimeOffset.UtcNow);
        Guid completed;
        Guid next;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                RootRegistration root = await AddRootAsync(catalog, directory);
                MirrorPulseRootRenameIntent rename = await catalog.PrepareManagedRootRenameAsync(root.RootId, "My Files");
                completed = rename.OperationId;
                await catalog.SaveManagedRootRenameProofAsync(completed, proof);
                await catalog.SaveManagedRootRenameProofAsync(completed, proof);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.SaveManagedRootRenameProofAsync(completed,
                    proof with { LocalObject = binding with { LocalFileId = Guid.NewGuid() } }));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.TransitionManagedRootRenameAsync(completed,
                    MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.NativeObserved));
                var observation = new MirrorPulseRootRenameObservation(binding, proof.PlaceholderIdentity, "My Files", proof.CapturedAt.AddSeconds(1));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ObserveManagedRootRenameAsync(completed,
                    observation with { LocalObject = binding with { LocalFileId = Guid.NewGuid() } }));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ObserveManagedRootRenameAsync(completed,
                    observation with { PlaceholderIdentity = Convert.ToBase64String([4]) }));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ObserveManagedRootRenameAsync(completed,
                    observation with { DirectoryName = "Unrelated" }));
                await catalog.ObserveManagedRootRenameAsync(completed, observation);
                await catalog.ObserveManagedRootRenameAsync(completed, observation);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.TransitionManagedRootRenameAsync(completed,
                    MirrorPulseRootRenamePhase.NativeObserved, MirrorPulseRootRenamePhase.LocalProjected));
                await catalog.RenameManagedRootAsync(root.RootId, "My Files");
                await catalog.TransitionManagedRootRenameAsync(completed, MirrorPulseRootRenamePhase.NativeObserved, MirrorPulseRootRenamePhase.LocalProjected);
                await catalog.TransitionManagedRootRenameAsync(completed, MirrorPulseRootRenamePhase.LocalProjected, MirrorPulseRootRenamePhase.Completed);
                next = (await catalog.PrepareManagedRootRenameAsync(root.RootId, "New Files")).OperationId;
                await catalog.SaveManagedRootRenameProofAsync(next, proof with { CapturedAt = proof.CapturedAt.AddSeconds(2) });
            }
            await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                IReadOnlyList<MirrorPulseRootRenameHistory> history = await reopened.ReadManagedRootRenameHistoryAsync();
                Assert.HasCount(2, history);
                MirrorPulseRootRenameHistory original = history.Single(item => item.Intent.OperationId == completed);
                Assert.AreEqual(MirrorPulseRootRenamePhase.Completed, original.Intent.Phase);
                Assert.AreEqual(proof, original.Proof);
                Assert.AreEqual(binding, original.Observation!.LocalObject);
                Assert.AreEqual("My Files", original.Observation.DirectoryName);
                Assert.AreEqual(next, (await reopened.ReadManagedRootRenamesAsync()).Single().OperationId);
                await reopened.TransitionManagedRootRenameAsync(next, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);
                Assert.AreEqual(MirrorPulseRootRenamePhase.Cancelled,
                    (await reopened.ReadManagedRootRenameHistoryAsync()).Single(item => item.Intent.OperationId == next).Intent.Phase);
                Assert.AreEqual("My Files", (await reopened.ReadAdapterTopologyAsync()).Roots.Single().Label);
            }
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [TestMethod]
    public async Task Schema17NativeObservationWithoutOriginalProofCannotAdoptTheCurrentObject()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-rename-migration", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        MirrorPulseRootRenameIntent rename;
        try
        {
            await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(paths))
            {
                RootRegistration root = await AddRootAsync(catalog, directory);
                rename = await catalog.PrepareManagedRootRenameAsync(root.RootId, "My Files");
            }
            // Model a real legacy record whose native rename already happened without captured proof.
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                await using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "UPDATE managed_root_renames SET payload=json_set(payload, '$.Phase', $phase); DROP TABLE managed_root_rename_history; PRAGMA user_version=17;";
                command.Parameters.AddWithValue("$phase", (int)MirrorPulseRootRenamePhase.NativeObserved);
                await command.ExecuteNonQueryAsync();
            }
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(paths);
            MirrorPulseRootRenameHistory migrated = (await reopened.ReadManagedRootRenameHistoryAsync()).Single();
            Assert.AreEqual(rename.OperationId, migrated.Intent.OperationId);
            Assert.IsNull(migrated.Proof);
            Assert.IsNull(migrated.Observation);
            var replacement = new MirrorPulseRootRenameProof(Guid.NewGuid(), new(1, Guid.NewGuid(), Guid.NewGuid()),
                Convert.ToBase64String([1]), DateTimeOffset.UtcNow);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.SaveManagedRootRenameProofAsync(rename.OperationId, replacement));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.TransitionManagedRootRenameAsync(rename.OperationId,
                MirrorPulseRootRenamePhase.NativeObserved, MirrorPulseRootRenamePhase.LocalProjected));
            Assert.IsTrue((await reopened.ReadManagedRootRenamesAsync()).Single().IsPending);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    private static async Task<RootRegistration> AddRootAsync(MirrorPulseProductCatalog catalog, string directory)
    {
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.rename-evidence"), "Example", "1.0.0", new(1, 1),
            new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(directory, "installed"),
            new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        await catalog.AddInstallationAsync(installation);
        await catalog.CreateInstanceAsync(installation.InstallId, "Example", new Dictionary<string, string>(), [],
            Path.Combine(directory, "files"), Path.Combine(directory, "transfers"), true);
        return (await catalog.ReadAdapterTopologyAsync()).Roots.Single();
    }
}
