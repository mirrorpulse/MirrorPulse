using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task StartedExplicitDeleteCannotReuseItsRetiredPublicIdentity(bool completed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareCanonicalExplicitDeletePlanAsync(catalog);
        var plan = setup.Plan;
        await catalog.PrepareRemoteDeleteResolutionAsync(plan);
        await catalog.StartRemoteDeleteResolutionAsync(plan.Decision.CommandId, plan.PreparedAt.AddSeconds(1));
        if (completed) await CompleteExplicitGenerationDecisionAsync(catalog, plan);
        var original = await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths);
        var resolver = new MirrorPulseRemoteIdentityResolver(catalog);
        var old = await resolver.ReadCurrentAsync(setup.Root, "backend-file", "accepted");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => resolver.RequireCurrentAsync(setup.Root, old).AsTask());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => resolver.ResolveAsync(setup.Root, "backend-file", "later-v3").AsTask());
        Assert.IsNull(await catalog.ReadRemoteIdentityGenerationAsync(setup.Root.RootId, old.ItemId));
        Assert.AreEqual(original, await ReadRemoteRetirementGenerationOriginalsAsync(fixture.Paths));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private static async Task<(RootRegistration Root, MirrorPulseRemoteDeleteResolutionPlan Plan)> PrepareCanonicalExplicitDeletePlanAsync(
        MirrorPulseProductCatalog catalog)
    {
        var setup = await PrepareCanonicalRemoteRetirementAsync(catalog);
        var intent = setup.Intent; var remote = intent.RemoteEntry!;
        await catalog.RecordNamespaceRetirementStartAsync(intent.OperationId, intent.PreparedAt, remote.ItemId);
        var rejected = RemoteRetirementOutcome(intent, true);
        await catalog.SettleNamespaceRemoteRetirementAsync(rejected);
        var conflict = new MirrorPulseConflictRecord(rejected.ConflictId!.Value, remote.InstanceId, remote.ChangeId,
            intent.RelativePath, MirrorPulseConflictReason.Delete, MirrorPulseVersionComparison.Unknown, null,
            remote.RemoteRevision, rejected.ConflictDetectedAt!.Value, source: MirrorPulseConflictSource.CfSharpRemote);
        var decision = new MirrorPulseRemoteConflictActionIntent(1, Guid.NewGuid(), conflict, MirrorPulseConflictAction.KeepRemote,
            null, MirrorPulseRemoteLibraryProvenance.CurrentPackageVersion, MirrorPulseRemoteLibraryProvenance.CurrentSourceCommit);
        await catalog.PrepareRemoteConflictActionAsync(decision);
        var original = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(intent.MemberId))!;
        return (setup.Root, new(1, decision, remote.BatchFingerprint, remote.Parent,
            [new(0, 0, original, remote.PreviousRemoteRevision, remote.PreviousRemoteRevision)], rejected.VerifiedAt.AddSeconds(1)));
    }

    private static async Task CompleteExplicitGenerationDecisionAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseRemoteDeleteResolutionPlan plan, bool receipt = true)
    {
        await catalog.RecordRemoteDeleteResolutionOutcomeAsync(plan.Decision.CommandId, DecisionOutcome(plan));
        await catalog.SettleRemoteDeleteResolutionPageAsync(plan.Decision.CommandId,
            plan.Entries.Reverse().Select(entry => DecisionMember(plan, entry)).ToArray());
        await catalog.CompleteRemoteDeleteResolutionAsync(plan.Decision.CommandId, DecisionParent(plan, 10), plan.PreparedAt.AddSeconds(11));
        if (receipt) await catalog.RecordRemoteConflictActionReceiptAsync(new(plan.Decision, MirrorPulseRemoteConflictObservedStatus.Applied,
            null, plan.PreparedAt.AddSeconds(12)));
    }
}
