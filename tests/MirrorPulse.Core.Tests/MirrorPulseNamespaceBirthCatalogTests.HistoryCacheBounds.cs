using System.Collections;
using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(17)]
    [DataRow(33)]
    public async Task GlobalRecoveryRetainsBoundedValidatedHistoryAndEveryOriginalOwner(int count)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var seed = await PrepareMovableMemberAsync(catalog);
        var owners = new List<MirrorPulseNamespaceMemberRegistration>();
        for (int index = 1; index <= count; index++)
        {
            var member = await AddOwnerPageMemberAsync(catalog, seed, index);
            await catalog.PrepareNamespaceRemoteUpdateAsync((await RemoteUpdatePlanAsync(catalog, member)) with
            {
                OperationId = OwnerPageId(7, index)
            });
            owners.Add(member);
        }
        string originals = await ReadOwnerPageOriginalsAsync(fixture.Paths);
        var observed = await ObserveHistoryCacheBoundsAsync(catalog, async () =>
        {
            var roots = await catalog.ReadNamespaceDispatchFencedRootsAsync();
            CollectionAssert.AreEquivalent(owners.Select(member => member.RootId!.Value).ToArray(), roots.ToArray());
        });
        Console.WriteLine("NamespaceHistoryCacheRetention: " + JsonSerializer.Serialize(observed));
        Assert.IsNotEmpty(observed);
        Assert.IsTrue(observed.Values.All(retained => retained <= 16),
            "Discovering original owners must not retain a growing transaction-wide graph cache.");
        Assert.AreEqual(originals, await ReadOwnerPageOriginalsAsync(fixture.Paths));
        await ExecuteSqlAsync(fixture.Paths,
            $"UPDATE namespace_remote_updates SET fingerprint=zeroblob(32) WHERE operation_id='{OwnerPageId(7, count):D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task DirectoryPositionReadDoesNotRequireEveryEarlierGraphToRemainCached()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareDirectorySettlementAsync(catalog);
        var move = setup.Move;
        var completion = setup.Completion;
        await catalog.CompleteNamespaceDirectoryMoveAsync(completion);
        var originals = new List<MirrorPulseNamespaceDirectoryMoveCompletion> { completion };
        for (int index = 1; index <= 16; index++)
        {
            move = move with
            {
                Move = move.Move with
                {
                    OperationId = Guid.NewGuid(),
                    PreviousMoveOperationId = completion.OperationId,
                    Source = move.Move.Target,
                    Target = move.Move.Target with { RelativePath = $"Docs/cache-directory-{index}" },
                    PreparedAt = completion.Root.VerifiedAt.AddSeconds(1),
                    DirectoryMoveEvidence = "AQ=="
                }
            };
            completion = await PrepareDirectoryCompletionFactsAsync(catalog, move);
            await catalog.CompleteNamespaceDirectoryMoveAsync(completion);
            originals.Add(completion);
        }
        var observed = await ObserveHistoryCacheBoundsAsync(catalog, async () =>
        {
            Assert.AreEqual(move.Move.Target.RelativePath + "/nested/grand.txt",
                (await catalog.ReadNamespaceMemberLocationAsync(setup.Members[^1].MemberId)).RelativePath);
            Assert.IsNotNull(await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Child.MemberId));
        });
        Console.WriteLine("NamespaceHistoryCacheRetention: " + JsonSerializer.Serialize(observed));
        Assert.IsNotEmpty(observed);
        Assert.IsTrue(observed.Values.All(retained => retained <= 16));
        foreach (var original in originals)
        {
            var retained = await catalog.ReadNamespaceDirectoryMoveCompletionAsync(original.OperationId);
            Assert.IsNotNull(retained);
            Assert.AreEqual(original.OperationId, retained.OperationId);
            CollectionAssert.AreEqual(original.Entries.ToArray(), retained.Entries.ToArray());
        }
    }

    private static async Task<IReadOnlyDictionary<string, int>> ObserveHistoryCacheBoundsAsync(
        MirrorPulseProductCatalog catalog, Func<Task> read)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var connection = (SqliteConnection)typeof(MirrorPulseProductCatalog).GetField("_connection", flags)!.GetValue(catalog)!;
        var histories = (IEnumerable)typeof(MirrorPulseProductCatalog).GetField("_namespaceHistoryReads", flags)!.GetValue(catalog)!;
        var maximums = new Dictionary<string, int>(StringComparer.Ordinal);
        SQLitePCL.strdelegate_trace trace = (context, sql) =>
        {
            foreach (object pair in histories)
            {
                var transaction = (SqliteTransaction)pair.GetType().GetProperty("Key")!.GetValue(pair)!;
                if (!ReferenceEquals(transaction.Connection, connection)) continue;
                object history = pair.GetType().GetProperty("Value")!.GetValue(pair)!;
                foreach (var property in history.GetType().GetProperties(flags))
                {
                    object cache = property.GetValue(history)!;
                    int count = (int)cache.GetType().GetProperty("Count", flags)!.GetValue(cache)!;
                    maximums[property.Name] = Math.Max(maximums.GetValueOrDefault(property.Name), count);
                }
            }
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        try { await read(); }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
        return maximums;
    }
}
