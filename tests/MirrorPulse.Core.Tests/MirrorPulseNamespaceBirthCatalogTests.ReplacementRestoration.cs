using System.Security.Cryptography;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow("NativeAppliedProjectionPending", false)]
    [DataRow("Completed", false)]
    [DataRow("Completed", true)]
    public async Task ReplacementRestorationKeepsReportedScopeResultSeparateFromProductCompletionAcrossOwners(string outcome, bool verified)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementRestoration attempt;
        MirrorPulseNamespaceReplacementRestorationResult result;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            attempt = await PrepareReplacementRestorationFixtureAsync(first);
            result = ReplacementRestorationResult(attempt, (await first.ReadNamespaceReplacementExecutionAsync(attempt.OperationId))!, outcome, verified);
            Assert.AreEqual(result, await first.RecordNamespaceReplacementRestorationResultAsync(result));
            CollectionAssert.AreEqual(new[] { attempt.Parent.Context.Member.RootId }, (await first.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
            Assert.IsNull(await first.ReadMutationAsync(attempt.OperationId));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await second.ReadNamespaceReplacementExecutionAsync(attempt.OperationId))!;
        Assert.AreEqual(attempt, actual.Restoration); Assert.AreEqual(result, actual.RestorationResult);
        Assert.AreEqual(result, await second.RecordNamespaceReplacementRestorationResultAsync(result));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.RecordNamespaceReplacementRestorationResultAsync(result with { RecordedAt = result.RecordedAt.AddTicks(1) }));
    }

    [TestMethod]
    [DataRow("projection")]
    [DataRow("drain")]
    [DataRow("callback")]
    [DataRow("binding")]
    [DataRow("dacl")]
    [DataRow("parent")]
    [DataRow("identity")]
    [DataRow("links")]
    [DataRow("inSync")]
    [DataRow("time")]
    public async Task ReplacementRestorationRejectsInventedProductVerification(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var attempt = await PrepareReplacementRestorationFixtureAsync(catalog);
        var execution = (await catalog.ReadNamespaceReplacementExecutionAsync(attempt.OperationId))!;
        var result = ReplacementRestorationResult(attempt, execution, "Completed", true);
        result = changed switch
        {
            "projection" => result with { DurableProjectionCommitted = false },
            "drain" => result with { Drained = false },
            "callback" => result with { CallbackCompleted = false },
            "binding" => result with { Verification = result.Verification! with { LocalObject = result.ExpectedObject with { LocalFileId = Guid.NewGuid() } } },
            "dacl" => result with { Verification = result.Verification! with { Dacl = execution.Action!.RoleOnlyDacl } },
            "parent" => result with { ParentVerification = result.ParentVerification! with { LocalObject = result.ExpectedObject } },
            "identity" => result with { PlaceholderIdentity = Convert.ToBase64String([9, 8, 7]) },
            "links" => result with { LinkCount = 2 },
            "inSync" => result with { IsInSync = true },
            "time" => result with { Verification = result.Verification! with { ObservedAt = attempt.PreparedAt.AddTicks(-1) } },
            _ => throw new AssertFailedException()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementRestorationResultAsync(result));
        Assert.IsNull((await catalog.ReadNamespaceReplacementExecutionAsync(attempt.OperationId))!.RestorationResult);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementRestorationRetainsInterruptedPredecessorAndRejectsUndrainedSupersession(bool undrained)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementRestoration first;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            first = await PrepareReplacementRestorationFixtureAsync(catalog);
            if (undrained)
            {
                var execution = (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!;
                await catalog.RecordNamespaceReplacementRestorationResultAsync(ReplacementRestorationResult(first, execution, "Canceled", false) with { Drained = false });
            }
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var next = first with { RestorationId = Guid.NewGuid(), PreviousRestorationId = first.RestorationId, PreparedAt = first.PreparedAt.AddSeconds(3) };
        if (undrained) await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceReplacementRestorationAsync(next));
        else
        {
            Assert.AreEqual(next, await reopened.PrepareNamespaceReplacementRestorationAsync(next));
            Assert.AreEqual(next, (await reopened.ReadNamespaceReplacementExecutionAsync(first.OperationId))!.Restoration);
            await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_replacement_restorations SET fingerprint=zeroblob(32) WHERE sequence=(SELECT MIN(sequence) FROM namespace_replacement_restorations);");
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceReplacementExecutionAsync(first.OperationId));
        }
    }

    [TestMethod]
    [DataRow("verified")]
    [DataRow("wrongPredecessor")]
    [DataRow("role")]
    [DataRow("time")]
    [DataRow("workerId")]
    [DataRow("parentPlacement")]
    public async Task ReplacementRestorationCannotReplaceProvenRecoveryOrBorrowAnotherOperation(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var first = await PrepareReplacementRestorationFixtureAsync(catalog);
        var execution = (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!;
        var next = first with { RestorationId = Guid.NewGuid(), PreviousRestorationId = first.RestorationId, PreparedAt = first.PreparedAt.AddSeconds(3) };
        if (changed == "verified") await catalog.RecordNamespaceReplacementRestorationResultAsync(ReplacementRestorationResult(first, execution, "Completed", true));
        else if (changed == "wrongPredecessor") next = next with { PreviousRestorationId = Guid.NewGuid() };
        else if (changed == "role") next = next with { Parent = next.Parent with { Context = next.Parent.Context with { Protection = next.Parent.Context.Protection with { RoleSid = "S-1-5-5-50-60" } } } };
        else if (changed == "time") next = next with { PreparedAt = execution.ContentCompletion!.Verification.ObservedAt.AddTicks(-1) };
        else if (changed == "parentPlacement") next = next with { Parent = next.Parent with { Context = next.Parent.Context with { Location = next.Parent.Context.Location with { RelativePath = "other" } } } };
        else await catalog.TryRecordWorkerRequestAsync(next.RestorationId, InstanceId.New(), SHA256.HashData([1, 2, 3]));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementRestorationAsync(next));
        Assert.AreEqual(first, (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!.Restoration);
    }

    [TestMethod]
    public async Task ReplacementRestorationMigrationKeepsCopiedContentWithoutInventingOldScopeEvidence()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementExecution original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var attempt = await PrepareReplacementRestorationFixtureAsync(first);
            original = (await first.ReadNamespaceReplacementExecutionAsync(attempt.OperationId))!;
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_replacement_restoration_results; DROP TABLE namespace_replacement_restorations; PRAGMA user_version=44;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var actual = (await second.ReadNamespaceReplacementExecutionAsync(original.Intent.OperationId))!;
        Assert.AreEqual(original.Action, actual.Action); Assert.AreEqual(original.Backup, actual.Backup); Assert.AreEqual(original.ContentCompletion, actual.ContentCompletion);
        Assert.IsNull(actual.Restoration); Assert.IsNull(actual.RestorationResult);
        CollectionAssert.AreEqual(new[] { original.Intent.RootId }, (await second.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    public async Task ReplacementRestorationRejectsLateResultWithoutPoisoningItsSuccessor()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var first = await PrepareReplacementRestorationFixtureAsync(catalog);
        var execution = (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!;
        var next = first with { RestorationId = Guid.NewGuid(), PreviousRestorationId = first.RestorationId, PreparedAt = first.PreparedAt.AddSeconds(3) };
        await catalog.PrepareNamespaceReplacementRestorationAsync(next);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementRestorationResultAsync(
            ReplacementRestorationResult(first, execution, "Completed", true)));
        var actual = (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!;
        Assert.AreEqual(next, actual.Restoration); Assert.IsNull(actual.RestorationResult);
    }

    private static async Task<MirrorPulseNamespaceReplacementRestoration> PrepareReplacementRestorationFixtureAsync(MirrorPulseProductCatalog catalog)
    {
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent);
        await catalog.RecordNamespaceReplacementStartAsync(facts.Start); await catalog.PrepareNamespaceReplacementActionAsync(facts.Action);
        await catalog.RecordNamespaceReplacementBackupAsync(facts.Backup); await catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write);
        var receipt = ReplacementContentReceipt(intent, facts, false); await catalog.RecordNamespaceReplacementContentCompletionAsync(receipt);
        var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(intent.Original.Context.Location.ParentMemberId!.Value))!;
        var attempt = new MirrorPulseNamespaceReplacementRestoration(1, intent.OperationId, Guid.NewGuid(), null, facts.Action.ActionId, parent, receipt.Verification.ObservedAt.AddSeconds(1));
        return await catalog.PrepareNamespaceReplacementRestorationAsync(attempt);
    }

    private static MirrorPulseNamespaceReplacementRestorationResult ReplacementRestorationResult(MirrorPulseNamespaceReplacementRestoration attempt,
        MirrorPulseNamespaceReplacementExecution execution, string outcome, bool verified)
    {
        var action = execution.Action!; var observed = attempt.PreparedAt.AddSeconds(1);
        return new(1, attempt.OperationId, attempt.RestorationId, outcome, "Released", action.LocalObject,
            true, true, true, true, outcome != "NativeAppliedProjectionPending", true, true, true, 0, 1, null,
            new(action.LocalObject, action.OwnerSid, action.RestoreDacl, observed),
            attempt.Parent.Context.Verification with { ObservedAt = observed }, 1, true, false,
            execution.Intent.OriginalPlaceholderIdentity, verified, observed.AddSeconds(1));
    }
}
