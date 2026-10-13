using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task ReplacementCompletionProjectsExistingProtectionAndRetainsNewerRequestsAcrossOwners(bool capturedOriginal, bool newRole)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementCompletion completed, candidate;
        MirrorPulseNamespaceReplacementExecution original;
        var offline = RootId.New();
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            (original, candidate) = await PrepareReplacementCompletionFixtureAsync(catalog, capturedOriginal, newRole);
            await catalog.RequireRootRescanAsync(offline);
            completed = await catalog.CompleteNamespaceReplacementAsync(candidate);
            Assert.AreNotEqual(Guid.Empty, completed.RescanGeneration);
            Assert.AreEqual(candidate.ProtectionId, completed.ProtectionId);
            Assert.AreEqual(original.Intent.Original.Context.Member, await catalog.ReadNamespaceMemberAsync(candidate.MemberId));
            var current = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(candidate.MemberId))!;
            Assert.AreEqual(candidate.ProtectionId, current.Context.Protection.ProtectionId);
            Assert.AreEqual(candidate.Child, current.Context.Verification);
            Assert.AreEqual(candidate.Parent.Context.Protection.RoleSid, current.Context.Protection.RoleSid);
            Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
            Assert.HasCount(2, await catalog.ReadRootRescanRequestsAsync());
            Assert.AreEqual(completed.RescanGeneration, (await catalog.ReadRootRescanRequestsAsync()).Single(request => request.RootId == original.Intent.RootId).Generation);
            Assert.IsNull(await catalog.ReadMutationAsync(candidate.OperationId));
            var after = (await catalog.ReadNamespaceReplacementExecutionAsync(candidate.OperationId))!;
            Assert.AreEqual(original.Action, after.Action); Assert.AreEqual(original.Backup, after.Backup);
            Assert.AreEqual(original.ContentCompletion, after.ContentCompletion); Assert.AreEqual(original.RestorationResult, after.RestorationResult);
            if (capturedOriginal)
            {
                var projection = (await catalog.ReadLatestNamespacePermissionChangeAsync(current.Context.Member.OriginId))!;
                Assert.AreEqual(MirrorPulseNamespacePermissionChangeKind.ReplacementProjection, projection.Intent.Kind);
                Assert.AreEqual(original.Restoration!.PreparedAt, projection.Intent.PreparedAt);
                Assert.AreEqual(new(candidate.OperationId, candidate.ActionId, candidate.RestorationId), projection.Intent.ReplacementProjection);
                var baseline = (await catalog.ReadNamespacePermissionBaselineAsync(projection.Intent.EvidenceId))!;
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionChangeAsync(baseline,
                    projection.Intent with { OperationId = Guid.NewGuid() }));
            }
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(completed, await reopened.ReadNamespaceReplacementCompletionAsync(completed.OperationId));
        var newer = await reopened.RequireRootRescanAsync(original.Intent.RootId);
        Assert.AreEqual(completed, await reopened.CompleteNamespaceReplacementAsync(candidate));
        Assert.AreEqual(newer, (await reopened.ReadRootRescanRequestsAsync()).Single(request => request.RootId == original.Intent.RootId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.CompleteNamespaceReplacementAsync(completed with { CompletedAt = completed.CompletedAt.AddTicks(1) }));
        Assert.HasCount(2, await reopened.ReadRootRescanRequestsAsync());
        Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("descriptor")]
    [DataRow("parent")]
    [DataRow("identity")]
    [DataRow("old-observation")]
    [DataRow("in-sync")]
    [DataRow("scope")]
    [DataRow("supplied-request")]
    public async Task InvalidCompletionLeavesOriginalProtectionAndRecoveryOwnershipUnchanged(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (original, candidate) = await PrepareReplacementCompletionFixtureAsync(catalog, false, false);
        candidate = changed switch
        {
            "binding" => candidate with { Child = candidate.Child with { LocalObject = candidate.Child.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "descriptor" => candidate with { Child = candidate.Child with { Dacl = original.Action!.RoleOnlyDacl } },
            "parent" => candidate with { ParentVerification = candidate.ParentVerification with { LocalObject = candidate.Child.LocalObject } },
            "identity" => candidate with { PlaceholderIdentity = "CQgH" },
            "old-observation" => candidate with { Child = candidate.Child with { ObservedAt = original.RestorationResult!.RecordedAt.AddTicks(-1) } },
            "in-sync" => candidate with { IsInSync = true },
            "scope" => candidate with { RestorationId = Guid.NewGuid() },
            "supplied-request" => candidate with { RescanGeneration = Guid.NewGuid() },
            _ => throw new AssertFailedException()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceReplacementAsync(candidate));
        Assert.IsNull(await catalog.ReadNamespaceReplacementCompletionAsync(candidate.OperationId));
        Assert.IsEmpty(await catalog.ReadRootRescanRequestsAsync());
        Assert.AreEqual(original.Intent.Original, await catalog.ReadNamespaceMemberProtectionSnapshotAsync(candidate.MemberId));
        CollectionAssert.AreEqual(new[] { original.Intent.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow(false, "namespace_replacement_completions")]
    [DataRow(true, "namespace_replacement_completions")]
    [DataRow(false, "root_rescan_generations")]
    [DataRow(true, "namespace_replacement_pending")]
    public async Task CompletionCommitFailureRollsBackPermissionRequestAndOwnFenceTogether(bool capturedOriginal, string boundary)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementCompletion candidate;
        MirrorPulseNamespaceReplacementExecution original;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            (original, candidate) = await PrepareReplacementCompletionFixtureAsync(catalog, capturedOriginal, false);
            string verb = boundary == "namespace_replacement_pending" ? "DELETE" : "INSERT";
            await ExecuteSqlAsync(fixture.Paths, $"CREATE TRIGGER fail_completion BEFORE {verb} ON {boundary} BEGIN SELECT RAISE(ABORT,'completion interrupted'); END;");
            await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceReplacementAsync(candidate));
            Assert.IsNull(await catalog.ReadNamespaceReplacementCompletionAsync(candidate.OperationId));
            Assert.AreEqual(original.Intent.Original, await catalog.ReadNamespaceMemberProtectionSnapshotAsync(candidate.MemberId));
            Assert.IsEmpty(await catalog.ReadRootRescanRequestsAsync());
            CollectionAssert.AreEqual(new[] { original.Intent.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_completion;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(candidate.ProtectionId, (await reopened.CompleteNamespaceReplacementAsync(candidate)).ProtectionId);
        Assert.HasCount(1, await reopened.ReadRootRescanRequestsAsync());
        Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DamagedCompletedProtectionCannotSupplyAnUnfencedRoot(bool capturedOriginal)
    {
        using var fixture = new Fixture(); Guid operation;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var (_, candidate) = await PrepareReplacementCompletionFixtureAsync(catalog, capturedOriginal, false);
            operation = candidate.OperationId; await catalog.CompleteNamespaceReplacementAsync(candidate);
            await ExecuteSqlAsync(fixture.Paths, capturedOriginal ?
                $"DELETE FROM namespace_permission_changes WHERE operation_id='{candidate.ProtectionId:D}';" :
                $"UPDATE namespace_birth_protections SET fingerprint=zeroblob(32) WHERE protection_id='{candidate.ProtectionId:D}';");
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceReplacementCompletionAsync(operation));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task CompletionDoesNotAdoptAnUndrainedOrUnprojectedScope()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var attempt = await PrepareReplacementRestorationFixtureAsync(catalog);
        var execution = (await catalog.ReadNamespaceReplacementExecutionAsync(attempt.OperationId))!;
        var reported = ReplacementRestorationResult(attempt, execution, "NativeAppliedProjectionPending", false);
        await catalog.RecordNamespaceReplacementRestorationResultAsync(reported);
        var candidate = CompletionObservation(execution, attempt.Parent, reported);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceReplacementAsync(candidate));
        Assert.IsNull(await catalog.ReadNamespaceReplacementCompletionAsync(attempt.OperationId));
        Assert.IsEmpty(await catalog.ReadRootRescanRequestsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompletedReplacementAllowsAnotherAdmissionButHistoricalReplayCannotReleaseIt(bool capturedOriginal)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (original, candidate) = await PrepareReplacementCompletionFixtureAsync(catalog, capturedOriginal, false);
        var completed = await catalog.CompleteNamespaceReplacementAsync(candidate);
        var snapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(candidate.MemberId))!;
        var next = Replacement(snapshot) with { PreparedAt = completed.CompletedAt.AddSeconds(1) };
        await catalog.PrepareNamespaceReplacementAsync(next);
        var request = (await catalog.ReadRootRescanRequestsAsync()).Single();
        Assert.AreEqual(completed, await catalog.CompleteNamespaceReplacementAsync(candidate));
        Assert.AreEqual(request, (await catalog.ReadRootRescanRequestsAsync()).Single());
        CollectionAssert.AreEqual(new[] { original.Intent.RootId }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementRestorationAsync(original.Restoration! with
        { RestorationId = Guid.NewGuid(), PreviousRestorationId = original.Restoration.RestorationId, PreparedAt = next.PreparedAt.AddSeconds(1) }));
        Assert.IsNull(await catalog.ReadNamespaceReplacementCompletionAsync(next.OperationId));
    }

    [TestMethod]
    public async Task LegacyScopeResultsSurviveUpgradeWithoutInventingCompletionOrRescanRequests()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementExecution original;
        MirrorPulseNamespaceReplacementCompletion candidate;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            (original, candidate) = await PrepareReplacementCompletionFixtureAsync(catalog, false, false);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_replacement_completions; PRAGMA user_version=48;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.IsNull(await reopened.ReadNamespaceReplacementCompletionAsync(candidate.OperationId));
        Assert.IsEmpty(await reopened.ReadRootRescanRequestsAsync());
        Assert.AreEqual(original.RestorationResult, (await reopened.ReadNamespaceReplacementExecutionAsync(candidate.OperationId))!.RestorationResult);
        CollectionAssert.AreEqual(new[] { original.Intent.RootId }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.AreEqual(candidate.ProtectionId, (await reopened.CompleteNamespaceReplacementAsync(candidate)).ProtectionId);
    }

    [TestMethod]
    public async Task ExplicitCompletionRevalidationKeepsTheEarlierScopeAndCopyFactsImmutable()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (original, _) = await PrepareReplacementCompletionFixtureAsync(catalog, false, false);
        var next = original.Restoration! with
        {
            RestorationId = Guid.NewGuid(),
            PreviousRestorationId = original.Restoration.RestorationId,
            PreparedAt = original.RestorationResult!.RecordedAt.AddSeconds(1),
            RevalidateForCompletion = true
        };
        await catalog.PrepareNamespaceReplacementRestorationAsync(next);
        var observed = ReplacementRestorationResult(next, original, "Completed", true);
        await catalog.RecordNamespaceReplacementRestorationResultAsync(observed);
        var current = (await catalog.ReadNamespaceReplacementExecutionAsync(next.OperationId))!;
        Assert.AreEqual(original.Backup, current.Backup); Assert.AreEqual(original.ContentCompletion, current.ContentCompletion);
        Assert.AreEqual(original.RestorationResult, await catalog.RecordNamespaceReplacementRestorationResultAsync(original.RestorationResult!));
        Assert.AreEqual(next, current.Restoration);
        var completed = await catalog.CompleteNamespaceReplacementAsync(CompletionObservation(current, next.Parent, observed));
        Assert.AreEqual(next.RestorationId, completed.RestorationId);
        Assert.HasCount(1, await catalog.ReadRootRescanRequestsAsync());
    }

    [TestMethod]
    public async Task CompletionParentObservationCannotPredateItsActualNewRoleEpoch()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (_, candidate) = await PrepareReplacementCompletionFixtureAsync(catalog, false, true);
        candidate = candidate with
        {
            ParentVerification = candidate.ParentVerification with
            { ObservedAt = candidate.Parent.Context.Verification.ObservedAt.AddTicks(-1) }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CompleteNamespaceReplacementAsync(candidate));
        Assert.IsNull(await catalog.ReadNamespaceReplacementCompletionAsync(candidate.OperationId));
        Assert.IsEmpty(await catalog.ReadRootRescanRequestsAsync());
    }

    private static async Task<(MirrorPulseNamespaceReplacementExecution Execution, MirrorPulseNamespaceReplacementCompletion Candidate)>
        PrepareReplacementCompletionFixtureAsync(MirrorPulseProductCatalog catalog, bool capturedOriginal, bool newRole)
    {
        // Real SQLite lifecycle tests with synthetic product observations; no native result is claimed.
        var setup = await PrepareMovableMemberAsync(catalog);
        var member = setup.Member;
        if (capturedOriginal)
        {
            var file = setup.Parent with
            {
                EvidenceId = Guid.NewGuid(),
                LocalObject = setup.Parent.LocalObject with { LocalFileId = Guid.NewGuid() },
                RelativePath = "Docs/original.txt",
                IsDirectory = false
            };
            var protection = Protection(file);
            await catalog.PrepareNamespacePermissionChangeAsync(file, protection);
            await catalog.PrepareNamespacePermissionLocalIdentityAsync(new(1, file.EvidenceId, protection.OperationId, file.LocalObject,
                Guid.NewGuid(), "original-file", protection.PreparedAt));
            await catalog.RecordNamespacePermissionApplicationAsync(protection.OperationId, file.LocalObject, protection.PreparedAt.AddSeconds(1));
            await catalog.VerifyNamespacePermissionChangeAsync(protection.OperationId,
                new(file.LocalObject, file.OwnerSid, protection.TargetDacl, protection.PreparedAt.AddSeconds(2)));
            member = new(1, Guid.NewGuid(), setup.ParentMember.MemberId, MirrorPulseNamespaceMemberOrigin.CapturedOriginal, file.EvidenceId,
                protection.OperationId, file.RootId, file.RelativePath, file.LocalObject, false, file.OwnerSid, protection.PreparedAt.AddSeconds(3));
            await catalog.RegisterNamespaceMemberAsync(member);
        }
        var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent);
        await catalog.RecordNamespaceReplacementStartAsync(facts.Start); await catalog.PrepareNamespaceReplacementActionAsync(facts.Action);
        await catalog.RecordNamespaceReplacementBackupAsync(facts.Backup); await catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write);
        var copied = ReplacementContentReceipt(intent, facts, false); await catalog.RecordNamespaceReplacementContentCompletionAsync(copied);
        var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.ParentMember.MemberId))!;
        var action = facts.Action;
        if (newRole)
        {
            var before = (await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!;
            var role = await ReplacementRoleActionAsync(catalog, before, "S-1-5-5-50-60");
            await catalog.PrepareNamespaceReplacementRoleActionAsync(role);
            await catalog.RecordNamespaceReplacementRoleHandoffAsync(ReplacementRoleHandoff(role, intent, true));
            action = role.Action;
        }
        var prepared = new MirrorPulseNamespaceReplacementRestoration(1, intent.OperationId, Guid.NewGuid(), null, action.ActionId,
            parent, (newRole ? action.PreparedAt.AddSeconds(2) : copied.Verification.ObservedAt.AddSeconds(1)));
        await catalog.PrepareNamespaceReplacementRestorationAsync(prepared);
        var execution = (await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!;
        var result = ReplacementRestorationResult(prepared, execution with { Action = action }, "Completed", true);
        await catalog.RecordNamespaceReplacementRestorationResultAsync(result);
        if (newRole)
        {
            Guid? parentId = setup.ParentMember.MemberId;
            while (parentId is { } id)
            {
                var current = (await catalog.ReadNamespaceMemberAsync(id))!;
                var baseline = (await catalog.ReadNamespacePermissionBaselineAsync(current.OriginId))!;
                var previous = (await catalog.ReadLatestNamespacePermissionChangeAsync(current.OriginId))!;
                var rotation = previous.Intent with
                {
                    OperationId = Guid.NewGuid(),
                    Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                    RoleSid = action.RoleSid,
                    ExpectedDacl = previous.Verification!.Dacl,
                    TargetDacl = Dacl($"D:P(A;;FA;;;{action.RoleSid})"),
                    PreparedAt = result.RecordedAt.AddSeconds(1)
                };
                await ProtectAsync(catalog, baseline, rotation);
                parentId = current.ParentMemberId;
            }
            parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.ParentMember.MemberId))!;
        }
        execution = (await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!;
        return (execution, CompletionObservation(execution, parent, result));
    }

    private static MirrorPulseNamespaceReplacementCompletion CompletionObservation(MirrorPulseNamespaceReplacementExecution execution,
        MirrorPulseNamespaceMemberProtectionSnapshot parent, MirrorPulseNamespaceReplacementRestorationResult result)
    {
        var observed = result.RecordedAt.AddSeconds(5);
        return new(1, execution.Intent.OperationId, execution.Intent.MemberId, execution.Restoration!.ActionId, result.RestorationId,
            Guid.NewGuid(), Guid.Empty, parent, result.Verification! with { ObservedAt = observed },
            parent.Context.Verification with { ObservedAt = observed }, 1, true, false, execution.Intent.OriginalPlaceholderIdentity, observed.AddSeconds(1));
    }
}
