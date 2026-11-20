using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [TestCategory("CiMovedBornAdmissionProfile")]
    [DataRow(1)]
    [DataRow(5)]
    [DataRow(12)]
    public async Task LabelBornAdmissionRetainsCompletedPositionsAndRechecksIndependentTransactions(int labels)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCancellationInitializationFixtureAsync(catalog, true, true);
        for (int index = 0; index < labels; index++)
        {
            var root = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Root.MemberId))!;
            var anchor = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Anchor.MemberId))!;
            setup = setup with
            {
                Move = setup.Move with
                {
                    ProtectionId = root.Context.Protection.ProtectionId,
                    RoleSid = root.Context.Protection.RoleSid,
                    Source = setup.Move.Source with { ParentProtectionId = anchor.Context.Protection.ProtectionId },
                    Target = setup.Move.Target with { ParentProtectionId = anchor.Context.Protection.ProtectionId }
                }
            };
            var completion = await PrepareRootSubtreeFactsAsync(catalog, setup);
            await catalog.CompleteNamespaceDirectoryMoveAsync(completion);
            var rename = await catalog.PrepareManagedRootRenameWithProofAsync(setup.Move.RootId, "Next" + index,
                setup.Move.Target.RelativePath, setup.Rename.Proof! with { CapturedAt = completion.Root.VerifiedAt.AddSeconds(1) });
            setup = setup with
            {
                Rename = rename,
                Move = setup.Move with
                {
                    OperationId = rename.Intent.OperationId,
                    Source = setup.Move.Source with { RelativePath = setup.Move.Target.RelativePath },
                    Target = setup.Move.Target with { RelativePath = rename.Intent.TargetName },
                    PreviousMoveOperationId = completion.OperationId,
                    SourcePositionOperationId = completion.OperationId,
                    PreparedAt = rename.Proof!.CapturedAt.AddSeconds(1)
                }
            };
        }
        var initialization = (await catalog.PrepareRootLabelCancellationInitializationAsync(setup.Move.OperationId,
            Guid.NewGuid(), setup.Move.PreparedAt.AddSeconds(1)))!;
        var owner = (await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(setup.Move.OperationId))!;
        var child = (await catalog.ReadNamespaceMemberRecoveryLineageAsync(initialization.OperationId, setup.Members[^1].MemberId))[0];
        var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(child.Location.ParentMemberId!.Value))!;
        var action = new MirrorPulseNamespaceBornMemberRecoveryAction(2, Guid.NewGuid(), initialization.OperationId,
            null, child, parent, child.BirthProtection!.Verification.Dacl, initialization.PreparedAt.AddSeconds(1))
        { RootLabelRecoveryOwner = new(setup.Move.OperationId, owner) };
        await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
        var expected = await catalog.ReadRegisteredNamespaceBirthSnapshotAsync(child.Member.MemberId, parent);
        var measured = await MeasureCiMovedBornAdmissionAsync(catalog, async () =>
        {
            var first = await catalog.ReadRootLabelBornRecoverySnapshotAsync(action);
            var second = await catalog.ReadRootLabelBornRecoverySnapshotAsync(action);
            Assert.AreEqual(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
            return second;
        });
        Assert.AreEqual(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(measured.Result));
        Assert.AreEqual(child.Location.CurrentPositionOperationId, measured.Result.Location.CurrentPositionOperationId);
        Console.WriteLine("CiMovedBornAdmissionSql: " + JsonSerializer.Serialize(new
        { labels, selects = measured.Selects, measured.TableReads, independentAdmissions = 2, nativeExecuted = false, publicDeadlineSpeedupEstablished = false }));
        // A generous fixed three-member work budget catches repeated move-history reads.
        Assert.IsLessThanOrEqualTo(130 * labels + 400, measured.Selects);
        string damage = labels switch
        {
            1 => "UPDATE namespace_member_names SET child_name_key='WRONG';",
            5 => "UPDATE namespace_birth_protections SET fingerprint=zeroblob(32);",
            _ => "UPDATE namespace_member_positions SET fingerprint=zeroblob(32);"
        };
        await ExecuteSqlAsync(fixture.Paths, damage);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadRootLabelBornRecoverySnapshotAsync(action));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    private static async Task<(T Result, int Selects, IReadOnlyDictionary<string, int> TableReads)> MeasureCiMovedBornAdmissionAsync<T>(
        MirrorPulseProductCatalog catalog, Func<Task<T>> read)
    {
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog)
            .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
        long changes = TotalChanges();
        int selects = 0;
        var tables = new Dictionary<string, int>(StringComparer.Ordinal);
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (!sql.TrimStart().StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)) return;
            selects++;
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(sql, @"\bFROM\s+([a-z_]+)\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                string table = match.Groups[1].Value;
                tables[table] = tables.GetValueOrDefault(table) + 1;
            }
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        T result;
        try { result = await read(); }
        finally
        {
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null);
            GC.KeepAlive(trace);
        }
        Assert.AreEqual(changes, TotalChanges(), "Independent Label recovery reads must not modify catalog state.");
        return (result, selects, tables);

        long TotalChanges()
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT total_changes();";
            return (long)command.ExecuteScalar()!;
        }
    }
}
