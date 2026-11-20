using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("namespace_member_moves")]
    [DataRow("namespace_member_move_observations")]
    [DataRow("namespace_member_move_projections")]
    [DataRow("namespace_member_move_completions")]
    public async Task HistoricalMoveReadReuseCannotHideCorruptionInANewTransaction(string table)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var completion = await CompleteMoveChainStepAsync(catalog, setup.Move);
        Assert.AreEqual(completion, await catalog.ReadNamespaceMemberMoveCompletionAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move.OperationId,
            (await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).CurrentPositionOperationId);
        await ExecuteSqlAsync(fixture.Paths, $"UPDATE {table} SET fingerprint=zeroblob(32);");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberMoveCompletionAsync(setup.Move.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task RepeatedPositionHistoryReadsRetainThreeMovesAndEveryEarlierFact()
    {
        using var fixture = new Fixture();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var first = await CompleteMoveChainStepAsync(catalog, setup.Move);
        var secondIntent = ContinueMove(setup.Move, first, setup.Move.Target, "Docs/second.txt");
        var second = await CompleteMoveChainStepAsync(catalog, secondIntent);
        var thirdIntent = ContinueMove(secondIntent, second, secondIntent.Target, "Docs/third.txt");
        var third = await CompleteMoveChainStepAsync(catalog, thirdIntent);
        foreach (var completed in new[] { first, second, third })
        {
            var position = await catalog.ReadNamespaceMemberPositionAsync(setup.Member.MemberId, completed.OperationId);
            Assert.IsNotNull(position);
            Assert.AreEqual(completed.RelativePath, position.RelativePath);
            Assert.AreEqual(completed.OperationId, position.CompletedMoveOperationId);
            Assert.AreEqual(completed, await catalog.ReadNamespaceMemberMoveCompletionAsync(completed.OperationId));
        }
        Assert.AreEqual(new(setup.Member.MemberId, thirdIntent.Target.ParentMemberId, third.RelativePath, third.OperationId),
            await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
        Console.WriteLine($"ThreeMoveHistory: elapsedMilliseconds={stopwatch.ElapsedMilliseconds}; originalFactsChecked=True; nativeExecution=False.");
    }
}
