using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task ConfirmedIdentityTransitionsSurviveRestartWithoutRewritingFirstMembershipOrLocation(bool originalFile, bool reverified)
    {
        using var fixture = new Fixture(); MoveFixture setup; MirrorPulseNamespaceIdentityAcceptance first, second;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            if (originalFile) setup = await WithOriginalMovableFileAsync(catalog, setup);
            var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
            var initial = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
            Assert.AreEqual(new(setup.Member.MemberId, setup.Move.ItemId, setup.Move.RemoteId, null, setup.Move.ItemId, setup.Move.RemoteId), initial);
            var upload = await PrepareIdentityUploadAsync(catalog, setup, root, setup.Member.RegisteredAt.AddSeconds(1), reverified ? "reverified" : null);
            first = await CorrelateAsync(catalog, setup.Member.MemberId, initial, upload.Proof.OperationId);
            await catalog.TransitionMutationAsync(upload.Proof.OperationId, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged);
            var next = await PrepareIdentityUploadAsync(catalog, setup, root, first.Confirmation.ObservedAt.AddSeconds(1), reverified ? "reverified" : null);
            second = await CorrelateAsync(catalog, setup.Member.MemberId,
                await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId), next.Proof.OperationId);
            // Later confirmation attempts are mutable observations; they cannot erase the original successful correlation.
            await catalog.SaveContentConfirmationReceiptAsync(first.Proof.OperationId, first.Confirmation with
            { Outcome = MirrorPulseContentConfirmationOutcome.ContentMismatch, NativeConfirmationVerified = false });
            Assert.AreEqual(first, await CorrelateAsync(catalog, setup.Member.MemberId, initial, first.Proof.OperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var current = await reopened.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        Assert.AreEqual(second.Proof.OperationId, current.AcceptanceOperationId);
        Assert.AreEqual(second.Proof.AcceptedItemId, current.ItemId); Assert.AreEqual(second.Proof.RemoteId, current.RemoteId);
        Assert.AreEqual(setup.Move.ItemId, current.OriginalItemId); Assert.AreEqual(setup.Move.RemoteId, current.OriginalRemoteId);
        Assert.AreEqual(first, await reopened.ReadNamespaceIdentityAcceptanceAsync(first.Proof.OperationId));
        Assert.AreEqual(second, await reopened.ReadNamespaceIdentityAcceptanceAsync(second.Proof.OperationId));
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Member.RelativePath, (await reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).RelativePath);
        Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId,
            originalFile ? "original.bin" : "new.txt"));
        Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await reopened.ReadMutationAsync(second.Proof.OperationId))!.State);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("missing-proof")]
    [DataRow("missing-receipt")]
    [DataRow("projection-pending")]
    [DataRow("native-unconfirmed")]
    [DataRow("bytes")]
    [DataRow("binding")]
    [DataRow("previous")]
    [DataRow("instance")]
    [DataRow("root-key")]
    [DataRow("conflict")]
    public async Task IdentityCorrelationCannotAdoptUnconfirmedForeignOrUnboundAcceptance(string mismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
        var initial = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        var upload = await PrepareIdentityUploadAsync(catalog, setup, root, setup.Member.RegisteredAt.AddSeconds(1), mismatch);
        if (mismatch == "conflict")
            await catalog.TransitionMutationAsync(upload.Proof.OperationId, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Conflict);
        Task<MirrorPulseNamespaceIdentityAcceptance> Attempt() => CorrelateAsync(catalog, setup.Member.MemberId,
            mismatch == "previous" ? initial with { ItemId = Guid.NewGuid() } : initial, upload.Proof.OperationId);
        if (mismatch.StartsWith("missing-", StringComparison.Ordinal)) await Assert.ThrowsExactlyAsync<FileNotFoundException>(Attempt);
        else if (mismatch is "projection-pending" or "native-unconfirmed" or "bytes") await Assert.ThrowsExactlyAsync<ArgumentException>(Attempt);
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(Attempt);
        Assert.IsNull(await catalog.ReadNamespaceIdentityAcceptanceAsync(upload.Proof.OperationId));
        Assert.AreEqual(initial, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task IdentityHistoryCannotForkOrRewindAndFailedInsertionLeavesNoCorrelation()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
        var initial = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        var firstUpload = await PrepareIdentityUploadAsync(catalog, setup, root, setup.Member.RegisteredAt.AddSeconds(1));
        var first = await CorrelateAsync(catalog, setup.Member.MemberId, initial, firstUpload.Proof.OperationId);
        var current = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        var next = await PrepareIdentityUploadAsync(catalog, setup, root, first.Confirmation.ObservedAt.AddSeconds(1));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CorrelateAsync(catalog, setup.Member.MemberId, initial, next.Proof.OperationId));
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.RecordNamespaceIdentityAcceptanceAsync(setup.Member.MemberId,
            next.Proof.OperationId, current.AcceptanceOperationId, current.ItemId!.Value, current.RemoteId!, cancel.Token));
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER reject_identity BEFORE INSERT ON namespace_member_identity_acceptances BEGIN SELECT RAISE(ABORT,'interrupted identity'); END;");
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => CorrelateAsync(catalog, setup.Member.MemberId, current, next.Proof.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceIdentityAcceptanceAsync(next.Proof.OperationId));
        Assert.AreEqual(current, await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER reject_identity;");
        await CorrelateAsync(catalog, setup.Member.MemberId, current, next.Proof.OperationId);
        Assert.AreEqual(next.Proof.OperationId, (await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId)).AcceptanceOperationId);
    }

    [TestMethod]
    public async Task PendingNamespaceMovementPreventsIdentityPromotionWithoutChangingItsRetainedProof()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var root = await AddIdentityTopologyAsync(catalog, setup.Move.RootId);
        var initial = await catalog.ReadNamespaceMemberIdentityAsync(setup.Member.MemberId);
        var upload = await PrepareIdentityUploadAsync(catalog, setup, root, setup.Member.RegisteredAt.AddSeconds(1));
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => CorrelateAsync(catalog, setup.Member.MemberId, initial, upload.Proof.OperationId));
        Assert.AreEqual(upload.Proof, await catalog.ReadContentAcceptanceProofAsync(upload.Proof.OperationId));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
    }

    private static Task<MirrorPulseNamespaceIdentityAcceptance> CorrelateAsync(MirrorPulseProductCatalog catalog, Guid memberId,
        MirrorPulseNamespaceMemberIdentity previous, Guid operationId) => catalog.RecordNamespaceIdentityAcceptanceAsync(memberId,
            operationId, previous.AcceptanceOperationId, previous.ItemId!.Value, previous.RemoteId!);

    private static async Task<RootRegistration> AddIdentityTopologyAsync(MirrorPulseProductCatalog catalog, RootId rootId)
    {
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.identities"), "Example", "1.0.0", new(1, 1),
            new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new(null), new(null, null), new(true, false, true, true), ["en-US"], "1.0.0", [new("docs", "Docs", "Docs", false)]);
        var installation = new InstalledAdapter(manifest, InstallId.New(), Path.GetTempPath(), new(new string('A', 64)),
            AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        var instance = new AdapterInstance(manifest.AdapterId, installation.InstallId, InstanceId.New(), "Storage",
            new Dictionary<string, string>(), [], Path.GetTempPath(), Path.GetTempPath(), true, AdapterLifecycleState.Configured, null, DateTimeOffset.UtcNow);
        var root = new RootRegistration(manifest.AdapterId, instance.InstanceId, rootId, "docs", "Docs", "Docs", false,
            RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
        await catalog.SaveAdapterTopologyAsync(new([installation], [instance], [root])); return root;
    }

    private static async Task<(MirrorPulseContentAcceptanceProof Proof, MirrorPulseContentConfirmationReceipt Receipt)> PrepareIdentityUploadAsync(
        MirrorPulseProductCatalog catalog, MoveFixture setup, RootRegistration root, DateTimeOffset observedAt, string? mismatch = null)
    {
        var binding = new MirrorPulseUploadBinding(mismatch == "binding" ? setup.Member.LocalObject with { LocalFileId = Guid.NewGuid() } : setup.Member.LocalObject,
            MirrorPulseContentPreparation.ReplacePlaceholderIdentity, "AQID"); // Opaque synthetic Core facts; no CfSharp decoding/native success is asserted.
        var intent = new MirrorPulseMutationIntent(Guid.NewGuid(), mismatch == "instance" ? InstanceId.New() : root.InstanceId,
            mismatch == "root-key" ? "another-root" : root.UniquenessKey, MirrorPulseWorkerChangeKind.ContentUpdate,
            "new.txt", null, false, null, 4, new string('B', 64), MirrorPulseMutationOrigin.Journal, binding);
        await catalog.PrepareMutationAsync(intent);
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
        await catalog.TransitionMutationAsync(intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, "accepted");
        var proof = new MirrorPulseContentAcceptanceProof(intent.OperationId, binding, Guid.NewGuid(), "source:" + Guid.NewGuid(), "accepted", 4, intent.ContentSha256!);
        var receipt = new MirrorPulseContentConfirmationReceipt(MirrorPulseContentConfirmationOutcome.Confirmed,
            MirrorPulseContentConfirmationStage.Complete, MirrorPulseContentConfirmationStage.Complete, true, true,
            mismatch != "native-unconfirmed", mismatch != "projection-pending", mismatch == "bytes" ? 3 : mismatch == "reverified" ? 8 : 4, 1,
            0, 0, null, null, true, "BAUG", observedAt);
        if (mismatch != "missing-proof") await catalog.SaveContentAcceptanceProofAsync(proof);
        if (mismatch is not ("missing-proof" or "missing-receipt")) await catalog.SaveContentConfirmationReceiptAsync(intent.OperationId, receipt);
        return (proof, receipt);
    }
}
