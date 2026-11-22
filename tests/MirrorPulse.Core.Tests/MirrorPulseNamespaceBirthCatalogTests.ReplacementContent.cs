using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementContentReceiptRetainsHistoryWithoutCompletingProtectionOrAcknowledgingUpload(bool placeholder)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementContentCompletion expected;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first); var intent = Replacement((await first.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
            var facts = ReplacementExecutionFacts(intent); await first.PrepareNamespaceReplacementAsync(intent);
            await first.RecordNamespaceReplacementStartAsync(facts.Start); await first.PrepareNamespaceReplacementActionAsync(facts.Action);
            await first.RecordNamespaceReplacementBackupAsync(facts.Backup); await first.RecordNamespaceReplacementWriteStartAsync(facts.Write);
            expected = ReplacementContentReceipt(intent, facts, placeholder);
            Assert.AreEqual(expected, await first.RecordNamespaceReplacementContentCompletionAsync(expected));
            Assert.AreEqual(setup.Member, await first.ReadNamespaceMemberAsync(intent.MemberId));
            Assert.IsNull(await first.ReadMutationAsync(intent.OperationId));
            CollectionAssert.AreEqual(new[] { intent.RootId }, (await first.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(expected, (await second.ReadNamespaceReplacementExecutionAsync(expected.OperationId))!.ContentCompletion);
        Assert.AreEqual(expected, await second.RecordNamespaceReplacementContentCompletionAsync(expected));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.RecordNamespaceReplacementContentCompletionAsync(
            expected with { Verification = expected.Verification with { ObservedAt = expected.Verification.ObservedAt.AddTicks(1) } }));
    }

    [TestMethod]
    [DataRow("content")]
    [DataRow("binding")]
    [DataRow("identity")]
    [DataRow("action")]
    [DataRow("descriptor")]
    [DataRow("time")]
    public async Task ReplacementContentReceiptRejectsSubstitutedBytesObjectIdentityOrProtection(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent);
        await catalog.RecordNamespaceReplacementStartAsync(facts.Start); await catalog.PrepareNamespaceReplacementActionAsync(facts.Action);
        await catalog.RecordNamespaceReplacementBackupAsync(facts.Backup); await catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write);
        var receipt = ReplacementContentReceipt(intent, facts, true);
        receipt = changed switch
        {
            "content" => receipt with { Content = receipt.Content with { Length = 10 } },
            "binding" => receipt with { Verification = receipt.Verification with { LocalObject = receipt.Verification.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "identity" => receipt with { PlaceholderIdentity = Convert.ToBase64String([3, 2, 1]) },
            "action" => receipt with { ActionId = Guid.NewGuid() },
            "descriptor" => receipt with { Verification = receipt.Verification with { Dacl = facts.Action.RestoreDacl } },
            "time" => receipt with { Verification = receipt.Verification with { ObservedAt = facts.Write.StartedAt.AddTicks(-1) } },
            _ => throw new AssertFailedException()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementContentCompletionAsync(receipt));
        Assert.IsNull((await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!.ContentCompletion);
    }

    [TestMethod]
    public async Task ReplacementContentReceiptRequiresWriteStartAndCannotClaimInSyncOrMultipleLinks()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent);
        await catalog.RecordNamespaceReplacementStartAsync(facts.Start); await catalog.PrepareNamespaceReplacementActionAsync(facts.Action);
        await catalog.RecordNamespaceReplacementBackupAsync(facts.Backup); var receipt = ReplacementContentReceipt(intent, facts, true);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementContentCompletionAsync(receipt));
        await catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementContentCompletionAsync(receipt with { IsInSync = true }));
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.RecordNamespaceReplacementContentCompletionAsync(receipt with { LinkCount = 2 }));
    }

    private static MirrorPulseNamespaceReplacementContentCompletion ReplacementContentReceipt(MirrorPulseNamespaceReplacementIntent intent,
        (MirrorPulseNamespaceReplacementStart Start, MirrorPulseNamespaceReplacementAction Action, MirrorPulseNamespaceReplacementBackup Backup,
            MirrorPulseNamespaceReplacementWriteStart Write) facts, bool placeholder) =>
        new(1, intent.OperationId, facts.Action.ActionId, intent.Content,
            new(facts.Action.LocalObject, facts.Action.OwnerSid, facts.Action.RoleOnlyDacl, facts.Write.StartedAt.AddSeconds(1)),
            1, placeholder, false, placeholder ? intent.OriginalPlaceholderIdentity : null);
}
