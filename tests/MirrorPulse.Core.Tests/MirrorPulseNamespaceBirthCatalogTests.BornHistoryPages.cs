using System.Reflection;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(17, false)]
    [DataRow(17, true)]
    [DataRow(33, false)]
    [DataRow(33, true)]
    public async Task BornRecoveryDiscoveryPagesEveryOriginalAndRetainsPublicHistoryAndReplay(int count, bool damageOldest)
    {
        using var fixture = new Fixture();
        Guid member;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog);
            member = setup.Member.MemberId;
            var actions = await PrepareBornHistoryPageChainAsync(catalog, setup.Member, setup.Move.PreparedAt.AddSeconds(5), count);
            string originals = await ReadOwnerPageOriginalsAsync(fixture.Paths);
            var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog).GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!;
            var statements = new List<string>();
            SQLitePCL.strdelegate_trace trace = (_, sql) => statements.Add(sql);
            SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
            try
            {
                var roots = await catalog.ReadNamespaceDispatchFencedRootsAsync();
                Assert.IsTrue(roots.Contains(setup.Member.RootId!.Value));
            }
            finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
            var pages = statements.Where(sql => sql.Contains("SELECT sequence FROM namespace_born_member_recovery_actions", StringComparison.Ordinal) &&
                sql.Contains("LIMIT 16", StringComparison.Ordinal)).ToArray();
            Assert.HasCount((count + 15) / 16, pages, "Discovery must scan original action sequences in bounded pages instead of loading every payload.");
            CollectionAssert.AreEqual(actions.ToArray(), (await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(member)).ToArray());
            Assert.AreEqual(actions[0], await catalog.PrepareNamespaceBornMemberRecoveryAsync(actions[0]));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(actions[0].ActionId));
            Assert.AreEqual(actions[^1], await catalog.ValidateNamespaceBornMemberRecoveryExecutionAsync(actions[^1].ActionId));
            Assert.IsTrue((await catalog.ReadNamespacePendingOperationsAsync(setup.Member.RootId.Value)).Any(entry => entry.OperationId == actions[^1].ActionId));
            Assert.AreEqual(originals, await ReadOwnerPageOriginalsAsync(fixture.Paths));
            Guid damaged = damageOldest ? actions[0].ActionId : actions[^1].ActionId;
            await ExecuteSqlAsync(fixture.Paths,
                $"UPDATE namespace_born_member_recovery_actions SET fingerprint=zeroblob(32) WHERE action_id='{damaged:D}';");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceBornMemberRecoveryActionsAsync(member));
        }
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () =>
        {
            await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            _ = await reopened.ReadNamespaceBornMemberRecoveryActionsAsync(member);
        });
    }

    [TestMethod]
    public async Task InitialBornHistoryPageCannotHideTheLowestSequenceOriginal()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var actions = await PrepareBornHistoryPageChainAsync(catalog, setup.Member, setup.Move.PreparedAt.AddSeconds(5), 17);
        await ExecuteSqlAsync(fixture.Paths,
            $"UPDATE namespace_born_member_recovery_actions SET sequence=-9223372036854775808,fingerprint=zeroblob(32) WHERE action_id='{actions[0].ActionId:D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceBornMemberRecoveryActionsAsync(setup.Member.MemberId));
    }

    private static async Task<IReadOnlyList<MirrorPulseNamespaceBornMemberRecoveryAction>> PrepareBornHistoryPageChainAsync(
        MirrorPulseProductCatalog catalog, MirrorPulseNamespaceMemberRegistration member, DateTimeOffset preparedAt, int count)
    {
        var first = await RegisteredRecoveryActionAsync(catalog, member, preparedAt);
        var actions = new List<MirrorPulseNamespaceBornMemberRecoveryAction>();
        for (int index = 0; index < count; index++)
        {
            var action = first with
            {
                ActionId = Guid.NewGuid(),
                PreviousActionId = index == 0 ? null : actions[^1].ActionId,
                PreparedAt = first.PreparedAt.AddSeconds(index)
            };
            await catalog.PrepareNamespaceBornMemberRecoveryAsync(action);
            actions.Add(action);
        }
        return actions.AsReadOnly();
    }
}
