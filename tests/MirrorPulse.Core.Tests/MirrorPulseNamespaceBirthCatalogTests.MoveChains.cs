using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RepeatedRelocationsRetainOriginalProvenanceAndAdvanceOnlyAfterCompletionAcrossRestart(bool originalFile)
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        MirrorPulseNamespaceMemberMoveIntent second, third;
        MirrorPulseNamespaceMemberMoveCompletion firstCompletion;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            if (originalFile) setup = await WithOriginalMovableFileAsync(catalog, setup);
            var bornParent = await AddBornAsync(catalog, Birth(setup.Parent, setup.ParentProof) with { IsDirectory = true, RelativePath = "Docs/target" });
            var parent = BornMember(bornParent, setup.ParentMember); await catalog.RegisterNamespaceMemberAsync(parent);
            setup = setup with
            {
                Move = setup.Move with
                {
                    Target = new(parent.MemberId, parent.LocalObject, bornParent.Protection.ProtectionId, "Docs/target/moved.txt"),
                    PreparedAt = new[] { setup.Move.PreparedAt, parent.RegisteredAt }.Max().AddSeconds(1)
                }
            };
            Assert.AreEqual(new(setup.Member.MemberId, setup.Member.ParentMemberId, setup.Member.RelativePath, null),
                await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
            firstCompletion = await CompleteMoveChainStepAsync(catalog, setup.Move);
            second = ContinueMove(setup.Move, firstCompletion, setup.Move.Source, originalFile ? "Docs/ORIGINAL.BIN" : "Docs/NEW.TXT");
            await catalog.PrepareNamespaceMemberMoveAsync(second);
            await catalog.RecordNamespaceMemberMoveStartAsync(new(1, second.OperationId, second.PreparedAt.AddSeconds(1)));
            Assert.AreEqual(new(setup.Member.MemberId, parent.MemberId, firstCompletion.RelativePath, setup.Move.OperationId),
                await catalog.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(second, await reopened.ReadPendingNamespaceMemberMoveAsync(second.RootId));
            var completed = await CompleteMoveChainStepAsync(reopened, second);
            third = ContinueMove(second, completed, second.Target, "Docs/final.txt");
            await CompleteMoveChainStepAsync(reopened, third);
            Assert.AreEqual(firstCompletion, await reopened.CompleteNamespaceMemberMoveAsync(firstCompletion));
            Assert.AreEqual(setup.Move, await reopened.PrepareNamespaceMemberMoveAsync(setup.Move));
            Assert.AreEqual(new(setup.Member.MemberId, third.Target.ParentMemberId, third.Target.RelativePath, third.OperationId),
                await reopened.ReadNamespaceMemberLocationAsync(setup.Member.MemberId));
            Assert.IsNull(await reopened.ReadNamespaceMemberChildAsync(second.Source.ParentMemberId, "moved.txt"));
            Assert.IsNull(await reopened.ReadNamespaceMemberChildAsync(second.Target.ParentMemberId, originalFile ? "original.bin" : "new.txt"));
            Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberChildAsync(third.Target.ParentMemberId, "FINAL.TXT"));
            Assert.AreEqual(setup.Member, await reopened.ReadNamespaceMemberAsync(setup.Member.MemberId));
            Assert.AreEqual(setup.Born.Observation, await reopened.ReadNamespaceBirthObservationAsync(setup.Born.Birth.OperationId));
            Assert.AreEqual(setup.Parent, await reopened.ReadNamespacePermissionBaselineAsync(setup.Parent.EvidenceId));
            Assert.IsNull(await reopened.ReadPendingNamespaceMemberMoveAsync(third.RootId));
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("stale")]
    [DataRow("unknown")]
    [DataRow("source-path")]
    [DataRow("source-parent")]
    [DataRow("chronology")]
    [DataRow("identity")]
    [DataRow("remote")]
    public async Task LaterAdmissionCannotForkOrRewindItsCompletedPredecessor(string mismatch)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var first = await CompleteMoveChainStepAsync(catalog, setup.Move);
        var second = ContinueMove(setup.Move, first, setup.Move.Target, "Docs/second.txt");
        var completed = await CompleteMoveChainStepAsync(catalog, second);
        var third = ContinueMove(second, completed, second.Target, "Docs/third.txt");
        var altered = mismatch switch
        {
            "missing" => third with { PreviousMoveOperationId = null },
            "stale" => third with { PreviousMoveOperationId = setup.Move.OperationId, Source = setup.Move.Target },
            "unknown" => third with { PreviousMoveOperationId = Guid.NewGuid() },
            "source-path" => third with { Source = third.Source with { RelativePath = setup.Move.Source.RelativePath } },
            "source-parent" => third with { Source = third.Source with { ParentLocalObject = third.LocalObject } },
            "chronology" => third with { PreparedAt = completed.VerifiedAt.AddTicks(-1) },
            "identity" => third with { ItemId = Guid.NewGuid() },
            _ => third with { RemoteId = "unproved:accepted" }
        };
        if (mismatch == "unknown")
            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespaceMemberMoveAsync(altered));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(altered));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(third.OperationId));
        Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(third.RootId));
        Assert.AreEqual(second.OperationId, (await catalog.ReadNamespaceMemberLocationAsync(third.MemberId)).CompletedMoveOperationId);
        Assert.AreEqual(third, await catalog.PrepareNamespaceMemberMoveAsync(third));
    }

    [TestMethod]
    public async Task FailedLaterHandoverRetainsThePreviousPositionAndRecoveryFenceAtomically()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var first = await CompleteMoveChainStepAsync(catalog, setup.Move);
        var second = ContinueMove(setup.Move, first, setup.Move.Target, "Docs/second.txt");
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER reject_later_completion BEFORE INSERT ON namespace_member_move_completions BEGIN SELECT RAISE(ABORT,'interrupted handover'); END;");
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => CompleteMoveChainStepAsync(catalog, second));
        Assert.AreEqual(setup.Move.OperationId, (await catalog.ReadNamespaceMemberLocationAsync(second.MemberId)).CompletedMoveOperationId);
        Assert.AreEqual(second, await catalog.ReadPendingNamespaceMemberMoveAsync(second.RootId));
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberChildAsync(second.Source.ParentMemberId, "moved.txt"));
        Assert.IsNull(await catalog.ReadNamespaceMemberChildAsync(second.Target.ParentMemberId, "second.txt"));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER reject_later_completion;");
        await CompleteMoveChainStepAsync(catalog, second);
        Assert.AreEqual(second.OperationId, (await catalog.ReadNamespaceMemberLocationAsync(second.MemberId)).CompletedMoveOperationId);
    }

    private static MirrorPulseNamespaceMemberMoveIntent ContinueMove(MirrorPulseNamespaceMemberMoveIntent previous,
        MirrorPulseNamespaceMemberMoveCompletion completion, MirrorPulseNamespaceMoveEndpoint destination, string targetPath) =>
        previous with
        {
            OperationId = Guid.NewGuid(),
            PreviousMoveOperationId = previous.OperationId,
            Source = previous.Target,
            Target = destination with { RelativePath = targetPath },
            PreparedAt = completion.VerifiedAt.AddSeconds(1)
        };

    private static async Task<MirrorPulseNamespaceMemberMoveCompletion> CompleteMoveChainStepAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberMoveIntent intent)
    {
        await catalog.PrepareNamespaceMemberMoveAsync(intent);
        await catalog.RecordNamespaceMemberMoveStartAsync(new(1, intent.OperationId, intent.PreparedAt.AddSeconds(1)));
        var member = await catalog.ReadNamespaceMemberAsync(intent.MemberId); Assert.IsNotNull(member);
        var parent = await catalog.ReadNamespaceMemberAsync(intent.Target.ParentMemberId); Assert.IsNotNull(parent);
        var childProof = await MemberVerificationAsync(member); var parentProof = await MemberVerificationAsync(parent);
        var native = new MirrorPulseNamespaceMemberMoveObservation(1, intent.OperationId, intent.ItemId, intent.RemoteId, "native-r",
            intent.Target.RelativePath, member.IsDirectory, true, 1,
            childProof with { ObservedAt = intent.PreparedAt.AddSeconds(2) }, parentProof with { ObservedAt = intent.PreparedAt.AddSeconds(3) });
        var projection = MoveProjection(native);
        await catalog.RecordNamespaceMemberMoveObservationAsync(native); await catalog.RecordNamespaceMemberMoveProjectionAsync(projection);
        return await catalog.CompleteNamespaceMemberMoveAsync(MoveCompletion(native, projection));

        async Task<MirrorPulseNamespacePermissionVerification> MemberVerificationAsync(MirrorPulseNamespaceMemberRegistration registered) =>
            registered.Origin == MirrorPulseNamespaceMemberOrigin.CapturedOriginal
                ? (await catalog.ReadLatestNamespacePermissionChangeAsync(registered.OriginId))!.Verification!
                : (await catalog.ReadLatestNamespaceBirthProtectionAsync(registered.OriginId))!.Verification;
    }

    private static async Task<MoveFixture> WithOriginalMovableFileAsync(MirrorPulseProductCatalog catalog, MoveFixture setup)
    {
        var original = setup.Parent with
        {
            EvidenceId = Guid.NewGuid(),
            IsDirectory = false,
            RelativePath = "Docs/original.bin",
            LocalObject = setup.Parent.LocalObject with { LocalFileId = Guid.NewGuid() }
        };
        var protection = Protection(original);
        await catalog.PrepareNamespacePermissionChangeAsync(original, protection);
        var identity = new MirrorPulseNamespacePermissionLocalIdentity(1, original.EvidenceId, protection.OperationId,
            original.LocalObject, Guid.NewGuid(), "local:original", protection.PreparedAt.AddSeconds(1));
        await catalog.PrepareNamespacePermissionLocalIdentityAsync(identity);
        await catalog.RecordNamespacePermissionApplicationAsync(protection.OperationId, original.LocalObject, protection.PreparedAt.AddSeconds(1));
        await catalog.VerifyNamespacePermissionChangeAsync(protection.OperationId,
            new(original.LocalObject, original.OwnerSid, protection.TargetDacl, protection.PreparedAt.AddSeconds(2)));
        var member = OriginalMember(original, protection, setup.ParentMember.MemberId); await catalog.RegisterNamespaceMemberAsync(member);
        return setup with
        {
            Member = member,
            Move = setup.Move with
            {
                MemberId = member.MemberId,
                ProtectionId = protection.OperationId,
                LocalObject = member.LocalObject,
                ItemId = identity.ItemId,
                RemoteId = identity.RemoteId,
                Source = setup.Move.Source with { RelativePath = member.RelativePath }
            }
        };
    }
}
