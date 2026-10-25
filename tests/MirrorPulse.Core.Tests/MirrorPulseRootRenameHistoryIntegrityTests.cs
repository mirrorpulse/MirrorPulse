using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed partial class MirrorPulseRootRenameHistoryIntegrityTests
{
    [TestMethod]
    [DataRow("owner-root")]
    [DataRow("payload-operation")]
    [DataRow("payload-root")]
    [DataRow("payload-phase")]
    [DataRow("proof-null-json")]
    [DataRow("proof-item")]
    [DataRow("proof-binding")]
    [DataRow("proof-identity")]
    [DataRow("proof-evidence")]
    [DataRow("observation-without-proof")]
    [DataRow("observation-binding")]
    [DataRow("observation-target")]
    [DataRow("observation-time")]
    public async Task HistoryRejectsDamagedOwnersAndEvidenceWithoutReplacingOriginals(string damage)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.Catalog.ObserveManagedRootRenameAsync(fixture.Intent.OperationId,
            new(fixture.Proof.LocalObject, fixture.Proof.PlaceholderIdentity, fixture.Intent.TargetName,
                fixture.Proof.CapturedAt.AddSeconds(1)));
        await fixture.DamageAsync("managed_root_rename_history", damage);
        string original = await fixture.ReadRawAsync("managed_root_rename_history");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenameHistoryAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            fixture.Catalog.SaveManagedRootRenameProofAsync(fixture.Intent.OperationId, fixture.Proof));
        Assert.AreEqual(original, await fixture.ReadRawAsync("managed_root_rename_history"));
        await fixture.ReopenAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenameHistoryAsync());
        Assert.AreEqual(original, await fixture.ReadRawAsync("managed_root_rename_history"));
    }

    [TestMethod]
    [DataRow("owner-root")]
    [DataRow("payload-root")]
    public async Task CurrentRenameRejectsAnIndexedRootThatDisagreesWithItsPayload(string damage)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.DamageAsync("managed_root_renames", damage);
        string original = await fixture.ReadRawAsync("managed_root_renames");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenamesAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
            fixture.Catalog.PrepareManagedRootRenameAsync(fixture.Intent.RootId, fixture.Intent.TargetName));
        Assert.AreEqual(original, await fixture.ReadRawAsync("managed_root_renames"));
        await fixture.ReopenAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Catalog.ReadManagedRootRenamesAsync());
        Assert.AreEqual(original, await fixture.ReadRawAsync("managed_root_renames"));
    }

    private sealed class Fixture(string directory, MirrorPulseStoragePaths paths,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRenameIntent intent, MirrorPulseRootRenameProof proof) : IAsyncDisposable
    {
        public MirrorPulseProductCatalog Catalog { get; private set; } = catalog;
        public MirrorPulseRootRenameIntent Intent => intent;
        public MirrorPulseRootRenameProof Proof => proof;

        public static async Task<Fixture> CreateAsync()
        {
            string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", Guid.NewGuid().ToString("N"));
            var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
            var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
            try
            {
                var manifest = new AdapterManifest(1, AdapterId.Parse("example.rename-integrity"), "Example", "1.0.0", new(1, 1),
                    new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
                    new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
                var installation = new InstalledAdapter(manifest, InstallId.New(), Path.Combine(directory, "installed"),
                    new(new string('A', 64)), AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
                await catalog.AddInstallationAsync(installation);
                await catalog.CreateInstanceAsync(installation.InstallId, "Example", new Dictionary<string, string>(), [],
                    Path.Combine(directory, "files"), Path.Combine(directory, "transfers"), true);
                RootRegistration root = (await catalog.ReadAdapterTopologyAsync()).Roots.Single();
                MirrorPulseRootRenameIntent intent = await catalog.PrepareManagedRootRenameAsync(root.RootId, "Renamed");
                var proof = new MirrorPulseRootRenameProof(Guid.NewGuid(), new(1, Guid.NewGuid(), Guid.NewGuid()),
                    Convert.ToBase64String([1, 2, 3]), DateTimeOffset.UtcNow, Convert.ToBase64String([4, 5, 6]));
                await catalog.SaveManagedRootRenameProofAsync(intent.OperationId, proof);
                return new(directory, paths, catalog, intent, proof);
            }
            catch { await catalog.DisposeAsync(); Directory.Delete(directory, true); throw; }
        }

        public async Task ReopenAsync()
        {
            await Catalog.DisposeAsync();
            Catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        }

        public async Task DamageAsync(string table, string damage)
        {
            string column = damage switch
            {
                "owner-root" => "root_id",
                _ when damage.StartsWith("payload-", StringComparison.Ordinal) => "payload",
                _ when damage.StartsWith("observation-", StringComparison.Ordinal) && damage != "observation-without-proof" => "observation",
                _ => "proof",
            };
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT {column} FROM {table};";
            string raw = (string)(await command.ExecuteScalarAsync())!;
            JsonNode? value = column == "root_id" ? null : JsonNode.Parse(raw);
            switch (damage)
            {
                case "owner-root": raw = RootId.New().ToString(); break;
                case "payload-operation": value!["OperationId"] = Guid.NewGuid(); break;
                case "payload-root": value!["RootId"] = RootId.New().ToString(); break;
                case "payload-phase": value!["Phase"] = 999; break;
                case "proof-null-json": value = null; break;
                case "proof-item": value!["ItemId"] = Guid.Empty; break;
                case "proof-binding": value!["LocalObject"]!["LocalFileId"] = Guid.Empty; break;
                case "proof-identity": value!["PlaceholderIdentity"] = "AQID\n"; break;
                case "proof-evidence": value!["DirectoryMoveEvidence"] = "invalid"; break;
                case "observation-without-proof": break;
                case "observation-binding": value!["LocalObject"]!["LocalFileId"] = Guid.NewGuid(); break;
                case "observation-target": value!["DirectoryName"] = "Unrelated"; break;
                case "observation-time": value!["ObservedAt"] = proof.CapturedAt.AddSeconds(-1); break;
                default: throw new AssertFailedException("Unknown synthetic damage.");
            }
            command.CommandText = $"UPDATE {table} SET {column}=$value;";
            command.Parameters.AddWithValue("$value", damage == "observation-without-proof" ? DBNull.Value :
                column == "root_id" ? raw : value?.ToJsonString() ?? "null");
            await command.ExecuteNonQueryAsync();
        }

        public async Task<string> ReadRawAsync(string table)
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = table == "managed_root_renames"
                ? "SELECT json_array(root_id,payload) FROM managed_root_renames;"
                : "SELECT json_array(operation_id,root_id,payload,proof,observation) FROM managed_root_rename_history;";
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async ValueTask DisposeAsync()
        {
            await Catalog.DisposeAsync();
            Directory.Delete(directory, true);
        }
    }
}
