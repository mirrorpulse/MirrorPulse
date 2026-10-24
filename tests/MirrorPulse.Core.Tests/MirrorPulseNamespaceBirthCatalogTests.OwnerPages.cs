using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("retirement")]
    [DataRow("batch")]
    [DataRow("update")]
    [DataRow("born")]
    public async Task GlobalOwnerRecoveryPagesActualOriginalsAndRejectsLateDamageWithoutPartialRoots(string kind)
    {
        using var fixture = new Fixture(); var owners = new List<(RootId Root, Guid Id, Guid Member)>();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var seed = await PrepareMovableMemberAsync(catalog);
            for (int index = 1; index <= 17; index++)
            {
                var member = await AddOwnerPageMemberAsync(catalog, seed, index);
                Guid id = OwnerPageId(1, index);
                switch (kind)
                {
                    case "retirement":
                        await catalog.PrepareNamespaceRetirementAsync((await RemoteRetirementAsync(catalog, member)) with
                        {
                            Version = 1,
                            OperationId = id,
                            RemoteEntry = null
                        });
                        break;
                    case "batch":
                        await catalog.PrepareNamespaceRetirementBatchAsync(id, member.MemberId);
                        break;
                    case "update":
                        await catalog.PrepareNamespaceRemoteUpdateAsync((await RemoteUpdatePlanAsync(catalog, member)) with { OperationId = id });
                        break;
                    default:
                        var context = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId))!;
                        var action = await RegisteredRecoveryActionAsync(catalog, member,
                            new[] { DateTimeOffset.UtcNow, context.Context.Verification.ObservedAt, member.RegisteredAt }.Max().AddSeconds(10));
                        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action with { ActionId = id });
                        break;
                }
                owners.Add((member.RootId!.Value, id, member.MemberId));
            }
            string originals = await ReadOwnerPageOriginalsAsync(fixture.Paths);
            var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
            var statements = new List<string>();
            SQLitePCL.strdelegate_trace trace = (_, sql) => statements.Add(sql);
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
            try
            {
                var roots = await catalog.ReadNamespaceDispatchFencedRootsAsync();
                CollectionAssert.AreEquivalent(owners.Select(owner => owner.Root).ToArray(), roots.ToArray());
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
            string table = kind switch
            {
                "retirement" => "namespace_member_retirements",
                "batch" => "namespace_member_retirement_batches",
                "update" => "namespace_remote_updates",
                _ => "namespace_born_member_recovery_actions"
            };
            var pages = statements.Where(sql => sql.StartsWith(kind == "born" ? "SELECT member_id FROM (" : "SELECT operation_id FROM ", StringComparison.Ordinal) &&
                sql.Contains("FROM " + table, StringComparison.Ordinal) &&
                sql.Contains("LIMIT 16", StringComparison.Ordinal) && !sql.Contains("WHERE operation_id=", StringComparison.Ordinal)).ToArray();
            Assert.HasCount(2, pages, "Seventeen actual original owners must be read through two bounded ID pages.");
            Assert.AreEqual(originals, await ReadOwnerPageOriginalsAsync(fixture.Paths));
            var selected = await catalog.ReadNamespacePendingOperationsAsync(owners[0].Root);
            Assert.IsTrue(selected.Any(entry => entry.OperationId == owners[0].Id));
            string key = kind == "born" ? "action_id" : "operation_id";
            await ExecuteSqlAsync(fixture.Paths, $"UPDATE {table} SET fingerprint=zeroblob(32) WHERE {key}='{owners[^1].Id:D}';");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespacePendingOperationsAsync(owners[0].Root));
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            _ = await reopened.ReadNamespaceDispatchFencedRootsAsync();
        });
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("retirement")]
    [DataRow("batch")]
    [DataRow("update")]
    [DataRow("born")]
    public async Task InitialOwnerPageCannotHideAnEmptyOrphanIdFromAnotherRoot(string kind)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); string member = setup.Member.MemberId.ToString("D");
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; " + (kind switch
        {
            "retirement" => $"INSERT INTO namespace_member_retirements VALUES('', '{member}', CAST('{{}}' AS BLOB), zeroblob(32), 0);",
            "batch" => $"INSERT INTO namespace_member_retirement_batches VALUES('', '{member}', '{RootId.New()}', CAST('{{}}' AS BLOB), zeroblob(32), 0);",
            "update" => $"INSERT INTO namespace_remote_update_pending VALUES('{RootId.New()}', '');",
            _ => $"INSERT INTO namespace_born_member_recovery_heads VALUES('', '{Guid.NewGuid():D}');"
        }));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespacePendingOperationsAsync(setup.Member.RootId!.Value));
    }

    private static Guid OwnerPageId(int prefix, int index) => Guid.Parse(prefix.ToString("D8", CultureInfo.InvariantCulture) +
        "-0000-0000-0000-" + index.ToString("D12", CultureInfo.InvariantCulture));

    private static async Task<MirrorPulseNamespaceMemberRegistration> AddOwnerPageMemberAsync(
        MirrorPulseProductCatalog catalog, MoveFixture seed, int index)
    {
        var parent = seed.Parent with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = RootId.New(),
            RelativePath = "OwnerRoot" + index.ToString(CultureInfo.InvariantCulture),
            LocalObject = seed.Parent.LocalObject with { LocalFileId = Guid.NewGuid() },
            CapturedAt = DateTimeOffset.UtcNow
        };
        var proof = Protection(parent); await ProtectAsync(catalog, parent, proof);
        var registered = OriginalMember(parent, proof, seed.ParentMember.ParentMemberId);
        await catalog.RegisterNamespaceMemberAsync(registered);
        var born = await AddBornAsync(catalog, Birth(parent, proof) with
        {
            OperationId = OwnerPageId(2, index),
            RelativePath = parent.RelativePath + "/leaf.bin"
        });
        var member = BornMember(born, registered); return await catalog.RegisterNamespaceMemberAsync(member);
    }

    private static async Task<string> ReadOwnerPageOriginalsAsync(MirrorPulse.Core.Configuration.MirrorPulseStoragePaths paths)
    {
        await using var connection = Connection(paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = """
            SELECT 'retirement',operation_id,hex(payload),hex(fingerprint) FROM namespace_member_retirements
            UNION ALL SELECT 'batch',operation_id,hex(payload),hex(fingerprint) FROM namespace_member_retirement_batches
            UNION ALL SELECT 'update',operation_id,hex(payload),hex(fingerprint) FROM namespace_remote_updates
            UNION ALL SELECT 'born',action_id,hex(payload),hex(fingerprint) FROM namespace_born_member_recovery_actions
            ORDER BY 1,2;
            """;
        var rows = new List<string>(); await using var reader = await query.ExecuteReaderAsync();
        while (await reader.ReadAsync()) rows.Add(string.Join('|', Enumerable.Range(0, 4).Select(reader.GetValue)));
        return string.Join('\n', rows);
    }
}
