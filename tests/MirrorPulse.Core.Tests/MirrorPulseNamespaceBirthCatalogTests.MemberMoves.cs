using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation, true)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import, true)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation, true)]
    public async Task BeforeMoveAdmissionAndStartSurviveOwnersWithoutTransferringTheSourceName(MirrorPulseNamespaceBirthOrigin origin, bool directory)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        var startAt = DateTimeOffset.UtcNow;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first, directory, origin);
            Assert.AreEqual(setup.Move, await first.PrepareNamespaceMemberMoveAsync(setup.Move));
            Assert.AreEqual(setup.Move, await first.ValidateNamespaceMemberMoveExecutionAsync(setup.Move.OperationId));
            startAt = setup.Move.PreparedAt.AddSeconds(1);
            Assert.AreEqual(new(1, setup.Move.OperationId, startAt), await first.RecordNamespaceMemberMoveStartAsync(new(1, setup.Move.OperationId, startAt)));
            Assert.AreEqual(setup.Member, await first.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "NEW.TXT"));
            Assert.IsNull(await first.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "moved.txt"));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(setup.Move, await second.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await second.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Move, await second.PrepareNamespaceMemberMoveAsync(setup.Move));
        Assert.AreEqual(new(1, setup.Move.OperationId, startAt), await second.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberAsync(setup.Member.MemberId));
        Assert.AreEqual(setup.Born.Observation, await second.ReadNamespaceBirthObservationAsync(setup.Born.Birth.OperationId));
        Assert.AreEqual(setup.Born.Protection, await second.ReadLatestNamespaceBirthProtectionAsync(setup.Born.Birth.OperationId));
        Assert.AreEqual(setup.Parent, await second.ReadNamespacePermissionBaselineAsync(setup.Parent.EvidenceId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespaceMemberMoveAsync(
            setup.Move with { RemoteId = "changed" }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.RecordNamespaceMemberMoveStartAsync(new(1, setup.Move.OperationId, startAt.AddSeconds(1))));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task FirstMoveCanReserveAnotherProtectedDirectParentOrChangeOnlyNameCase(bool caseOnly)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        MirrorPulseNamespaceMemberMoveIntent move;
        if (caseOnly) move = setup.Move with { Target = setup.Move.Target with { RelativePath = "Docs/NEW.TXT" } };
        else
        {
            var bornParent = await AddBornAsync(catalog, Birth(setup.Parent, setup.ParentProof) with { IsDirectory = true, RelativePath = "Docs/target" });
            var parentMember = BornMember(bornParent, setup.ParentMember); await catalog.RegisterNamespaceMemberAsync(parentMember);
            move = setup.Move with
            {
                Target = new(parentMember.MemberId, parentMember.LocalObject, bornParent.Protection.ProtectionId, "Docs/target/moved.txt"),
                PreparedAt = new[] { setup.Member.RegisteredAt, parentMember.RegisteredAt }.Max().AddSeconds(1)
            };
        }
        Assert.AreEqual(move, await catalog.PrepareNamespaceMemberMoveAsync(move));
        Assert.AreEqual(move, await catalog.ValidateNamespaceMemberMoveExecutionAsync(move.OperationId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("volume")]
    [DataRow("sync-root")]
    [DataRow("item")]
    [DataRow("remote")]
    [DataRow("owner")]
    [DataRow("role")]
    [DataRow("protection")]
    [DataRow("source-parent-binding")]
    [DataRow("target-parent-binding")]
    [DataRow("source-path")]
    [DataRow("target-parent-path")]
    [DataRow("root")]
    [DataRow("file-with-directory-proof")]
    public async Task MoveAdmissionRejectsSubstitutedObjectsAndParentsBeforeAnyReservation(string changed)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var move = setup.Move;
        var altered = changed switch
        {
            "binding" => move with { LocalObject = move.LocalObject with { LocalFileId = Guid.NewGuid() } },
            "volume" => move with { LocalObject = move.LocalObject with { VolumeSerialNumber = move.LocalObject.VolumeSerialNumber + 1 } },
            "sync-root" => move with { LocalObject = move.LocalObject with { SyncRootFileId = Guid.NewGuid() } },
            "item" => move with { ItemId = Guid.NewGuid() },
            "remote" => move with { RemoteId = "unproved:accepted" },
            "owner" => move with { OwnerSid = "S-1-5-21-100-200-300-1002" },
            "role" => move with { RoleSid = "S-1-5-5-321-654" },
            "protection" => move with { ProtectionId = Guid.NewGuid() },
            "source-parent-binding" => move with { Source = move.Source with { ParentLocalObject = move.Source.ParentLocalObject with { LocalFileId = Guid.NewGuid() } } },
            "target-parent-binding" => move with { Target = move.Target with { ParentLocalObject = move.Target.ParentLocalObject with { LocalFileId = Guid.NewGuid() } } },
            "source-path" => move with { Source = move.Source with { RelativePath = "Docs/other.txt" } },
            "target-parent-path" => move with { Target = move.Target with { RelativePath = "Other/moved.txt" } },
            "root" => move with { RootId = RootId.New() },
            _ => move with { DirectoryMoveEvidence = "AA==" }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(altered));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(move.OperationId));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(move.RootId));
        Assert.AreEqual(move, await catalog.PrepareNamespaceMemberMoveAsync(move));
    }

    [TestMethod]
    public async Task UnresolvedMoveFencesNewRootAdmissionAndPreservesHistoricalReplayAndOtherRoots()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(
            Birth(setup.Parent, setup.ParentProof) with { RelativePath = "Docs/MOVED.TXT" }));
        Assert.AreEqual(setup.Born.Birth, await catalog.PrepareNamespaceBirthAsync(setup.Born.Birth));
        var original = setup.Parent with
        { EvidenceId = Guid.NewGuid(), LocalObject = setup.Parent.LocalObject with { LocalFileId = Guid.NewGuid() }, RelativePath = "Docs/other", IsDirectory = false };
        var proof = Protection(original); await ProtectAsync(catalog, original, proof);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RegisterNamespaceMemberAsync(
            OriginalMember(original, proof, setup.ParentMember.MemberId)));
        var other = setup.Parent with
        { EvidenceId = Guid.NewGuid(), RootId = RootId.New(), RelativePath = "Other", LocalObject = setup.Parent.LocalObject with { LocalFileId = Guid.NewGuid() } };
        var otherProof = Protection(other); await ProtectAsync(catalog, other, otherProof);
        var otherMember = OriginalMember(other, otherProof, setup.ParentMember.ParentMemberId);
        await catalog.RegisterNamespaceMemberAsync(otherMember);
        var otherBirth = Birth(other, otherProof) with { RelativePath = "Other/new" };
        Assert.AreEqual(otherBirth, await catalog.PrepareNamespaceBirthAsync(otherBirth));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OccupiedOrUnresolvedBirthDestinationCannotBeReservedByAMove(bool registered)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var collision = Birth(setup.Parent, setup.ParentProof) with { RelativePath = "Docs/MOVED.TXT" };
        if (registered)
        {
            var born = await AddBornAsync(catalog, collision);
            await catalog.RegisterNamespaceMemberAsync(BornMember(born, setup.ParentMember));
        }
        else await catalog.PrepareNamespaceBirthAsync(collision);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(setup.Move));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
    }

    [TestMethod]
    public async Task DirectoryCannotReserveADestinationInsideItsOwnRegisteredDescendant()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        var nested = await AddBornAsync(catalog, NestedBirth(setup.Born, "nested", true));
        var nestedMember = BornMember(nested, setup.Member); await catalog.RegisterNamespaceMemberAsync(nestedMember);
        var cyclic = setup.Move with
        {
            Target = new(nestedMember.MemberId, nestedMember.LocalObject, nested.Protection.ProtectionId, nestedMember.RelativePath + "/cycle"),
            PreparedAt = nestedMember.RegisteredAt.AddSeconds(1)
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(cyclic));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(cyclic.RootId));
    }

    [TestMethod]
    public async Task ReservationInsertFailureRollsBackIntentAndRootFenceAcrossOwners()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_move_reservation BEFORE INSERT ON namespace_member_move_reservations BEGIN SELECT RAISE(ABORT,'synthetic interruption'); END;");
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => first.PrepareNamespaceMemberMoveAsync(setup.Move));
            Assert.IsNull(await first.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
            Assert.IsNull(await first.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
            Assert.AreEqual(setup.Member, await first.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_move_reservation;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await second.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await second.PrepareNamespaceMemberMoveAsync(setup.Move));
    }

    [TestMethod]
    public async Task HistoricalIntentAndStartReplayDoNotRefreshExecutionAfterAProtectionEpochChanges()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        var start = new MirrorPulseNamespaceMemberMoveStart(1, setup.Move.OperationId, setup.Move.PreparedAt.AddSeconds(1));
        await catalog.RecordNamespaceMemberMoveStartAsync(start);
        var next = setup.Born.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = setup.Born.Protection.ProtectionId,
            Verification = setup.Born.Protection.Verification with { ObservedAt = start.StartedAt.AddSeconds(1) }
        };
        await catalog.RecordNamespaceBirthProtectionAsync(next);
        Assert.AreEqual(setup.Move, await catalog.PrepareNamespaceMemberMoveAsync(setup.Move));
        Assert.AreEqual(start, await catalog.RecordNamespaceMemberMoveStartAsync(start));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ValidateNamespaceMemberMoveExecutionAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Move, await catalog.ReadPendingNamespaceMemberMoveAsync(setup.Move.RootId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    public async Task CanceledAdmissionAndPrematureStartLeaveNamesAndMoveStateUnchanged()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.PrepareNamespaceMemberMoveAsync(setup.Move, cancellation.Token));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(setup.Move.OperationId));
        await catalog.PrepareNamespaceMemberMoveAsync(setup.Move);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceMemberMoveStartAsync(
            new(1, setup.Move.OperationId, setup.Move.PreparedAt.AddTicks(-1))));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveStartAsync(setup.Move.OperationId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
    }

    private sealed record MoveFixture(MirrorPulseNamespacePermissionBaseline Parent, MirrorPulseNamespacePermissionIntent ParentProof,
        MirrorPulseNamespaceMemberRegistration ParentMember, BornObject Born, MirrorPulseNamespaceMemberRegistration Member,
        MirrorPulseNamespaceMemberMoveIntent Move);

    private static async Task<MoveFixture> PrepareMovableMemberAsync(MirrorPulseProductCatalog catalog, bool directory = false,
        MirrorPulseNamespaceBirthOrigin origin = MirrorPulseNamespaceBirthOrigin.ControlledCreation)
    {
        var parent = Baseline(); var proof = Protection(parent);
        await ProtectAsync(catalog, parent, proof);
        var parentMember = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        var born = await AddBornAsync(catalog, Birth(parent, proof) with { IsDirectory = directory, Origin = origin });
        var member = BornMember(born, parentMember); await catalog.RegisterNamespaceMemberAsync(member);
        var move = new MirrorPulseNamespaceMemberMoveIntent(1, Guid.NewGuid(), member.MemberId, born.Protection.ProtectionId,
            born.Birth.RootId, member.LocalObject, born.Plan.ItemId, born.Plan.RemoteId, member.OwnerSid, born.Protection.RoleSid,
            new(parentMember.MemberId, parentMember.LocalObject, proof.OperationId, member.RelativePath),
            new(parentMember.MemberId, parentMember.LocalObject, proof.OperationId, "Docs/moved.txt"), member.RegisteredAt.AddSeconds(1))
        { DirectoryMoveEvidence = directory ? "AA==" : null }; // Opaque synthetic product facts; no public/native proof is asserted here.
        return new(parent, proof, parentMember, born, member, move);
    }
}
