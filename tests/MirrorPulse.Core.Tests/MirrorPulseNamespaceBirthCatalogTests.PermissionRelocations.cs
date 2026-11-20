using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    private const string RelocatedRole = "S-1-5-5-123-457";

    [TestMethod]
    public async Task OriginalRoleRotationFollowsCompletedMovesAcrossOwnersWithoutChangingFirstProvenance()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespacePermissionBaseline original;
        MirrorPulseNamespacePermissionIntent rotation;
        MirrorPulseNamespaceMemberMoveCompletion firstMove;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await WithOriginalMovableFileAsync(first, await PrepareMovableMemberAsync(first));
            original = (await first.ReadNamespacePermissionBaselineAsync(setup.Member.OriginId))!;
            firstMove = await CompleteMoveChainStepAsync(first, setup.Move);
            rotation = await RelocatedRotationAsync(first, setup, firstMove);
            await ProtectAsync(first, original, rotation);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var epoch = await second.ReadNamespaceMemberProtectionEpochAsync(setup.Member.MemberId);
        Assert.AreEqual(rotation.OperationId, epoch.ProtectionId);
        Assert.AreEqual(RelocatedRole, epoch.RoleSid);
        Assert.AreEqual(original, await second.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(rotation, (await second.ReadNamespacePermissionChangeForApplicationAsync(rotation.OperationId)).Intent);

        // The subsequent move needs the newly verified role throughout the parent lineage.
        var parentRotation = RotateOriginal(setup.ParentProof, RelocatedRole, firstMove.VerifiedAt.AddSeconds(1));
        await ProtectAsync(second, setup.Parent, parentRotation);
        var anchor = (await second.ReadNamespaceMemberAsync(setup.ParentMember.ParentMemberId!.Value))!;
        var anchorOriginal = (await second.ReadNamespacePermissionBaselineAsync(anchor.OriginId))!;
        var anchorProof = (await second.ReadLatestNamespacePermissionChangeAsync(anchor.OriginId))!.Intent;
        await ProtectAsync(second, anchorOriginal, RotateOriginal(anchorProof, RelocatedRole, firstMove.VerifiedAt.AddSeconds(1)));
        var next = ContinueMove(setup.Move, firstMove, setup.Move.Source, "Docs/again.bin") with
        {
            ProtectionId = rotation.OperationId,
            RoleSid = RelocatedRole,
            Source = setup.Move.Target with { ParentProtectionId = parentRotation.OperationId },
            Target = setup.Move.Source with { ParentProtectionId = parentRotation.OperationId, RelativePath = "Docs/again.bin" },
            PreparedAt = rotation.PreparedAt.AddSeconds(4)
        };
        var secondMove = await CompleteMoveChainStepAsync(second, next);
        Assert.AreEqual(secondMove.OperationId, (await second.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).CompletedMoveOperationId);
        Assert.AreEqual(epoch, await second.ReadNamespaceMemberProtectionEpochAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "again.bin"));
        // Historical replay cannot grant fresh execution at an earlier committed location.
        Assert.AreEqual(rotation, (await second.PrepareNamespacePermissionChangeAsync(original, rotation)).Intent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.ReadNamespacePermissionChangeForApplicationAsync(rotation.OperationId));
        Assert.AreEqual(original, await second.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task RelocatedRestoreAndReprotectionRetainTheOriginalDescriptorAndMember()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await WithOriginalMovableFileAsync(catalog, await PrepareMovableMemberAsync(catalog));
        var original = (await catalog.ReadNamespacePermissionBaselineAsync(setup.Member.OriginId))!;
        var moved = await CompleteMoveChainStepAsync(catalog, setup.Move);
        var previous = (await catalog.ReadLatestNamespacePermissionChangeAsync(original.EvidenceId))!.Intent;
        var restore = previous with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.Restore,
            RelativePath = moved.RelativePath,
            ExpectedDacl = previous.TargetDacl,
            TargetDacl = original.OriginalDacl,
            PreparedAt = moved.VerifiedAt.AddSeconds(1),
            Relocation = new(setup.Member.MemberId, moved.OperationId)
        };
        await ProtectAsync(catalog, original, restore);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceMemberProtectionEpochAsync(setup.Member.MemberId));
        var protect = restore with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.Protect,
            ExpectedDacl = original.OriginalDacl,
            TargetDacl = previous.TargetDacl,
            PreparedAt = restore.PreparedAt.AddSeconds(3)
        };
        await ProtectAsync(catalog, original, protect);
        Assert.AreEqual(protect.OperationId, (await catalog.ReadNamespaceMemberProtectionEpochAsync(setup.Member.MemberId)).ProtectionId);
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(moved.RelativePath, (await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId)).RelativePath);
    }

    [TestMethod]
    [DataRow("no-reference")]
    [DataRow("foreign-member")]
    [DataRow("missing-move")]
    [DataRow("wrong-path")]
    [DataRow("before-completion")]
    [DataRow("projection-only")]
    public async Task PermissionAdmissionCannotAdoptAnUnprovedOrPendingRelocation(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await WithOriginalMovableFileAsync(catalog, await PrepareMovableMemberAsync(catalog));
        var original = (await catalog.ReadNamespacePermissionBaselineAsync(setup.Member.OriginId))!;
        var completed = await CompleteMoveChainStepAsync(catalog, setup.Move);
        var intent = await RelocatedRotationAsync(catalog, setup, completed);
        if (mismatch == "projection-only")
        {
            var pending = ContinueMove(setup.Move, completed, setup.Move.Source, "Docs/pending.bin");
            await catalog.PrepareNamespaceMemberMoveAsync(pending);
            await catalog.RecordNamespaceMemberMoveStartAsync(new(1, pending.OperationId, pending.PreparedAt.AddSeconds(1)));
            var proof = (await catalog.ReadLatestNamespacePermissionChangeAsync(original.EvidenceId))!.Verification!;
            var native = new MirrorPulseNamespaceMemberMoveObservation(1, pending.OperationId, pending.ItemId, pending.RemoteId,
                "native-r", pending.Target.RelativePath, false, true, 1,
                proof with { ObservedAt = pending.PreparedAt.AddSeconds(2) },
                new(setup.Parent.LocalObject, setup.Parent.OwnerSid, setup.ParentProof.TargetDacl, pending.PreparedAt.AddSeconds(3)));
            await catalog.RecordNamespaceMemberMoveObservationAsync(native);
            await catalog.RecordNamespaceMemberMoveProjectionAsync(MoveProjection(native));
            intent = intent with
            {
                RelativePath = pending.Target.RelativePath,
                PreparedAt = pending.PreparedAt.AddSeconds(5),
                Relocation = new(setup.Member.MemberId, pending.OperationId)
            };
        }
        else intent = mismatch switch
        {
            "no-reference" => intent with { Relocation = null },
            "foreign-member" => intent with { Relocation = new(setup.ParentMember.MemberId, completed.OperationId) },
            "missing-move" => intent with { Relocation = new(setup.Member.MemberId, Guid.NewGuid()) },
            "wrong-path" => intent with { RelativePath = setup.Member.RelativePath },
            _ => intent with { PreparedAt = completed.VerifiedAt }
        };
        if (mismatch == "missing-move")
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespacePermissionChangeAsync(original, intent));
        else
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(original, intent));
        Assert.IsNull(await catalog.ReadNamespacePermissionChangeAsync(intent.OperationId));
        Assert.AreEqual(setup.Move.ProtectionId, (await catalog.ReadNamespaceMemberProtectionEpochAsync(setup.Member.MemberId)).ProtectionId);
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
    }

    [TestMethod]
    public async Task RelocationIsOmittedFromLegacyPermissionPayloads()
    {
        var baseline = Baseline(); var intent = Protection(baseline);
        Assert.IsFalse(JsonSerializer.Serialize(intent).Contains("Relocation", StringComparison.Ordinal));
    }

    private static async Task<MirrorPulseNamespacePermissionIntent> RelocatedRotationAsync(MirrorPulseProductCatalog catalog,
        MoveFixture setup, MirrorPulseNamespaceMemberMoveCompletion completion)
    {
        var previous = (await catalog.ReadLatestNamespacePermissionChangeAsync(setup.Member.OriginId))!.Intent;
        return RotateOriginal(previous, RelocatedRole, completion.VerifiedAt.AddSeconds(1)) with
        { RelativePath = completion.RelativePath, Relocation = new(setup.Member.MemberId, completion.OperationId) };
    }

    private static MirrorPulseNamespacePermissionIntent RotateOriginal(MirrorPulseNamespacePermissionIntent previous,
        string role, DateTimeOffset preparedAt) => previous with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = role,
            ExpectedDacl = previous.TargetDacl,
            TargetDacl = previous.TargetDacl.Replace(previous.RoleSid, role, StringComparison.Ordinal),
            PreparedAt = preparedAt
        };
}
