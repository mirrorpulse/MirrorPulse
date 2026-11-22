using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task OriginalControlledRetirementContextSurvivesOwnersWithoutRestoringItsActiveName(bool directory, bool moved)
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceControlledRetirementContext original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog, directory);
            DateTimeOffset preparedAt = setup.Member.RegisteredAt.AddSeconds(1);
            if (moved)
            {
                if (directory)
                {
                    var completion = await PrepareDirectoryCompletionFactsAsync(catalog, setup);
                    await catalog.CompleteNamespaceDirectoryMoveAsync(completion);
                    preparedAt = completion.Root.VerifiedAt.AddSeconds(1);
                }
                else preparedAt = (await CompleteMoveChainStepAsync(catalog, setup.Move)).VerifiedAt.AddSeconds(1);
            }
            var location = await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId);
            var intent = Retire(setup, Guid.NewGuid()) with { RelativePath = location.RelativePath, PreparedAt = preparedAt };
            await catalog.PrepareNamespaceRetirementAsync(intent);
            await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, preparedAt, setup.Born.Plan.ItemId);
            await catalog.RecordNamespaceRetirementNativeDeletedAsync(intent.OperationId, preparedAt.AddSeconds(1));
            await catalog.CompleteNamespaceRetirementAsync(intent.OperationId, preparedAt.AddSeconds(2));
            original = await catalog.ReadNamespaceControlledRetirementContextAsync(intent.OperationId);
            Assert.AreEqual(setup.Member, original.Member); Assert.AreEqual(location, original.Location);
            Assert.AreEqual(setup.Born.Plan.ItemId, original.Identity.ItemId);
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(original, await reopened.ReadNamespaceControlledRetirementContextAsync(original.Retirement.OperationId));
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
        Assert.IsNull(await reopened.ReadNamespaceMemberChildAsync(original.Location.ParentMemberId!.Value,
            original.Location.RelativePath.Split('/')[^1]));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("prepared")]
    [DataRow("started")]
    [DataRow("native-only")]
    [DataRow("legacy-start")]
    [DataRow("foreign-start-id")]
    public async Task ControlledHistoricalContextCannotInventMissingNativeOrLogicalRetirementFacts(string cut)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var intent = Retire(setup, Guid.NewGuid()); await catalog.PrepareNamespaceRetirementAsync(intent);
        if (cut != "prepared")
            await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt,
                cut == "legacy-start" ? null : cut == "foreign-start-id" ? Guid.NewGuid() : setup.Born.Plan.ItemId);
        if (cut is "native-only" or "legacy-start" or "foreign-start-id")
            await catalog.RecordNamespaceRetirementNativeDeletedAsync(intent.OperationId, intent.PreparedAt.AddSeconds(1));
        if (cut is "legacy-start" or "foreign-start-id")
            await catalog.CompleteNamespaceRetirementAsync(intent.OperationId, intent.PreparedAt.AddSeconds(2));
        if (cut == "foreign-start-id")
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceControlledRetirementContextAsync(intent.OperationId));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceControlledRetirementContextAsync(intent.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }
}
