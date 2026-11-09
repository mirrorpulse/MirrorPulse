using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task ExistingPlaceholderRestorationRetainsAccessOnlyFactsAndOriginalBeforeScopeProofAcrossOwners()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementRestoration attempt;
        MirrorPulseNamespaceReplacementRestorationResult result;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            attempt = await PrepareExistingAccessRestorationAsync(catalog);
            var execution = (await catalog.ReadNamespaceReplacementExecutionAsync(attempt.OperationId))!;
            result = ExistingAccessResult(attempt, execution);
            Assert.AreEqual(result, await catalog.RecordNamespaceReplacementRestorationResultAsync(result));
            Assert.IsFalse(result.NativeIdentityPrepared); Assert.IsFalse(result.NativeConverted); Assert.IsFalse(result.DurableProjectionCommitted);
            Assert.IsNull(await catalog.ReadNamespaceReplacementCompletionAsync(attempt.OperationId));
            Assert.IsNull(await catalog.ReadMutationAsync(attempt.OperationId));
            Assert.IsTrue((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(execution.Intent.RootId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = (await reopened.ReadNamespaceReplacementExecutionAsync(attempt.OperationId))!;
        Assert.AreEqual(attempt, retained.Restoration); Assert.AreEqual(result, retained.RestorationResult);
        Assert.AreEqual(result, await reopened.RecordNamespaceReplacementRestorationResultAsync(result));
        Assert.IsNotNull(retained.Backup); Assert.IsNotNull(retained.ContentCompletion);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.RecordNamespaceReplacementRestorationResultAsync(result with { NativeIdentityPrepared = true }));
    }

    [TestMethod]
    [DataRow("missing-proof")]
    [DataRow("legacy-with-proof")]
    [DataRow("foreign-object")]
    [DataRow("foreign-identity")]
    [DataRow("before-copy")]
    [DataRow("after-preparation")]
    public async Task ExistingPlaceholderRestorationRejectsUnboundOrRetrospectiveBeforeScopeProof(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var first = await PrepareReplacementRestorationFixtureAsync(catalog);
        var execution = (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!;
        var next = ExistingAccessAttempt(first, execution);
        next = damage switch
        {
            "missing-proof" => next with { ExistingPlaceholder = null },
            "legacy-with-proof" => next with { Version = 1 },
            "foreign-object" => next with { ExistingPlaceholder = next.ExistingPlaceholder! with { LocalObject = execution.Action!.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "foreign-identity" => next with { ExistingPlaceholder = next.ExistingPlaceholder! with { PlaceholderIdentity = Convert.ToBase64String([9, 8, 7]) } },
            "before-copy" => next with { ExistingPlaceholder = next.ExistingPlaceholder! with { ObservedAt = execution.ContentCompletion!.Verification.ObservedAt.AddTicks(-1) } },
            "after-preparation" => next with { ExistingPlaceholder = next.ExistingPlaceholder! with { ObservedAt = next.PreparedAt.AddTicks(1) } },
            _ => throw new AssertFailedException()
        };
        if (damage is "missing-proof" or "legacy-with-proof" or "after-preparation")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceReplacementRestorationAsync(next));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementRestorationAsync(next));
        Assert.AreEqual(first, (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!.Restoration);
    }

    [TestMethod]
    [DataRow("invented-preparation")]
    [DataRow("invented-conversion")]
    [DataRow("invented-projection")]
    [DataRow("wrong-version")]
    [DataRow("callback")]
    [DataRow("drain")]
    [DataRow("access-write")]
    [DataRow("access-readback")]
    [DataRow("object")]
    [DataRow("parent")]
    [DataRow("identity")]
    [DataRow("in-sync")]
    public async Task ExistingPlaceholderRestorationRejectsInventedOrIncompleteAccessResult(string damage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var attempt = await PrepareExistingAccessRestorationAsync(catalog);
        var execution = (await catalog.ReadNamespaceReplacementExecutionAsync(attempt.OperationId))!;
        var result = ExistingAccessResult(attempt, execution);
        result = damage switch
        {
            "invented-preparation" => result with { NativeIdentityPrepared = true },
            "invented-conversion" => result with { NativeConverted = true },
            "invented-projection" => result with { DurableProjectionCommitted = true },
            "wrong-version" => result with { Version = 1 },
            "callback" => result with { CallbackCompleted = false },
            "drain" => result with { Drained = false },
            "access-write" => result with { AccessDescriptorApplied = false },
            "access-readback" => result with { AccessDescriptorReadBack = false },
            "object" => result with { Verification = result.Verification! with { LocalObject = result.ExpectedObject with { LocalFileId = Guid.NewGuid() } } },
            "parent" => result with { ParentVerification = result.ParentVerification! with { LocalObject = result.ExpectedObject } },
            "identity" => result with { PlaceholderIdentity = Convert.ToBase64String([9, 8, 7]) },
            "in-sync" => result with { IsInSync = true },
            _ => throw new AssertFailedException()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementRestorationResultAsync(result));
        Assert.IsNull((await catalog.ReadNamespaceReplacementExecutionAsync(attempt.OperationId))!.RestorationResult);
    }

    private static async Task<MirrorPulseNamespaceReplacementRestoration> PrepareExistingAccessRestorationAsync(MirrorPulseProductCatalog catalog)
    {
        var first = await PrepareReplacementRestorationFixtureAsync(catalog);
        var execution = (await catalog.ReadNamespaceReplacementExecutionAsync(first.OperationId))!;
        return await catalog.PrepareNamespaceReplacementRestorationAsync(ExistingAccessAttempt(first, execution));
    }

    private static MirrorPulseNamespaceReplacementRestoration ExistingAccessAttempt(MirrorPulseNamespaceReplacementRestoration first,
        MirrorPulseNamespaceReplacementExecution execution) => first with
        {
            Version = 2,
            RestorationId = Guid.NewGuid(),
            PreviousRestorationId = first.RestorationId,
            PreparedAt = first.PreparedAt.AddSeconds(2),
            ExistingPlaceholder = new(1, execution.Action!.LocalObject, execution.Intent.OriginalPlaceholderIdentity, first.PreparedAt.AddSeconds(1))
        };

    private static MirrorPulseNamespaceReplacementRestorationResult ExistingAccessResult(MirrorPulseNamespaceReplacementRestoration attempt,
        MirrorPulseNamespaceReplacementExecution execution) => ReplacementRestorationResult(attempt, execution, "Completed", true) with
        {
            Version = 2,
            NativeStage = "AccessDescriptor",
            NativeIdentityPrepared = false,
            NativeConverted = false,
            DurableProjectionCommitted = false,
            PreparationHResult = null,
            PreparationUsn = null
        };
}
