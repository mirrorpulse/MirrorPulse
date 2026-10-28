using System.Text.Json;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private sealed record CancellationBornCut(MirrorPulseNamespaceBornMemberRecoveryAction Action,
        MirrorPulseNamespaceBornMemberRecoveryCompletion? Completion);

    private static async Task<CancellationBornCut> InterruptOriginalCancellationBornFixtureAsync(CloudFileSystem fileSystem,
        MirrorPulseProductCatalog catalog, MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession execution,
        MirrorPulseNamespaceActor actor, MirrorPulseInstanceScheduler scheduler, MirrorPulseNamespacePermissionCoordinator permissions,
        RootRegistration root, Guid operationId, MirrorPulseNamespaceBornMemberRecoveryCheckpoint cut, CancellationToken token)
    {
        var plan = await catalog.ReadRootLabelPendingPermissionRecoveryAsync(operationId, token);
        Assert.IsNotNull(plan); Assert.IsEmpty(plan.PendingOriginals); Assert.HasCount(2, plan.BornMembers);
        Guid memberId = plan.BornMembers[0];
        var before = await catalog.ReadNamespaceInitializationStatusAsync(plan.Owner.InitializationOperationId, token);
        var originalHistory = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(memberId, token);
        bool reached = false, seededOriginalDescriptor = false;
        var recovery = new MirrorPulseRootLabelCancellationRecoveryCoordinator(fileSystem, catalog, router, execution, actor, scheduler, permissions)
        {
            BornCheckpoint = async (point, stop) =>
            {
                if (point == MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterActionPrepared && cut != point)
                {
                    // Root rotation can already propagate the target inherited ACL. Seed
                    // the exact retained old descriptor on this one fixture object so
                    // the following production scope must perform a real Access write.
                    var actions = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(memberId, stop);
                    var preparedAction = actions[^1];
                    try
                    {
                        Console.WriteLine($"CancellationBornSeedObservation: cut={cut}; stage=Prepared; " +
                            $"sameOriginalOwner={preparedAction.RootLabelRecoveryOwner == new MirrorPulseNamespaceBornRootLabelRecoveryOwner(operationId, plan.Owner)}; " +
                            $"targetMatchesRetained={MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(preparedAction.TargetDacl, preparedAction.Child.BirthProtection!.Verification.Dacl)}; diagnosticOnly=True.");
                    }
                    catch (Exception diagnosticFailure) when (diagnosticFailure is not OutOfMemoryException)
                    { Console.WriteLine($"CancellationBornSeedObservation: cut={cut}; stage=Prepared; observationUnavailable=True; failureKind={diagnosticFailure.GetType().Name}; diagnosticOnly=True."); }
                    Assert.AreEqual(new MirrorPulseNamespaceBornRootLabelRecoveryOwner(operationId, plan.Owner), preparedAction.RootLabelRecoveryOwner);
                    Assert.IsFalse(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(preparedAction.TargetDacl, preparedAction.Child.BirthProtection!.Verification.Dacl));
                    CloudItem item = preparedAction.Child.Member.IsDirectory ? fileSystem.GetDirectory(preparedAction.Child.Location.RelativePath) : fileSystem.GetFile(preparedAction.Child.Location.RelativePath);
                    var seed = await permissions.RunAdmittedBornMemberRecoveryAsync(preparedAction.ActionId, (current, admitted) =>
                        MirrorPulseWindowsNamespacePermissionLease.RunOwnedProtectedAsync(item, router, current.Child.Member.LocalObject,
                            async (lease, scopeToken) =>
                            {
                                var actual = await lease.InspectAsync(scopeToken);
                                try
                                {
                                    Console.WriteLine($"CancellationBornSeedObservation: cut={cut}; stage=BeforeSeed; " +
                                        $"sameBinding={current.Child.Member.LocalObject == actual.LocalObject}; sameOwner={current.Child.Member.OwnerSid == actual.OwnerSid}; " +
                                        $"samePath={current.Child.Location.RelativePath == actual.RelativePath}; sameRoot={current.Child.Member.RootId == actual.RootId}; " +
                                        $"sameKind={current.Child.Member.IsDirectory == actual.IsDirectory}; singleLink={actual.LinkCount == 1}; " +
                                        $"targetMatchesNative={MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.TargetDacl, actual.Dacl)}; " +
                                        $"retainedMatchesNative={MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.Child.BirthProtection!.Verification.Dacl, actual.Dacl)}; diagnosticOnly=True.");
                                }
                                catch (Exception diagnosticFailure) when (diagnosticFailure is not OutOfMemoryException)
                                { Console.WriteLine($"CancellationBornSeedObservation: cut={cut}; stage=BeforeSeed; observationUnavailable=True; failureKind={diagnosticFailure.GetType().Name}; diagnosticOnly=True."); }
                                Assert.AreEqual(current.Child.Member.LocalObject, actual.LocalObject); Assert.AreEqual(current.Child.Member.OwnerSid, actual.OwnerSid);
                                Assert.AreEqual(current.Child.Location.RelativePath, actual.RelativePath); Assert.AreEqual(current.Child.Member.RootId, actual.RootId);
                                Assert.AreEqual(current.Child.Member.IsDirectory, actual.IsDirectory); Assert.AreEqual(1u, actual.LinkCount);
                                Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.TargetDacl, actual.Dacl) ||
                                    MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.Child.BirthProtection!.Verification.Dacl, actual.Dacl));
                                scopeToken.ThrowIfCancellationRequested();
                                SeedRetainedInheritedCancellationFixtureDacl(item, current.Child.BirthProtection!.Verification.Dacl);
                                var seedReadback = await lease.InspectAsync(scopeToken);
                                try
                                {
                                    Console.WriteLine($"CancellationBornSeedObservation: cut={cut}; stage=SeedReadback; " +
                                        $"sameBinding={current.Child.Member.LocalObject == seedReadback.LocalObject}; sameOwner={current.Child.Member.OwnerSid == seedReadback.OwnerSid}; " +
                                        $"targetMatchesNative={MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.TargetDacl, seedReadback.Dacl)}; " +
                                        $"retainedMatchesNative={MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.Child.BirthProtection.Verification.Dacl, seedReadback.Dacl)}; diagnosticOnly=True.");
                                }
                                catch (Exception diagnosticFailure) when (diagnosticFailure is not OutOfMemoryException)
                                { Console.WriteLine($"CancellationBornSeedObservation: cut={cut}; stage=SeedReadback; observationUnavailable=True; failureKind={diagnosticFailure.GetType().Name}; diagnosticOnly=True."); }
                                Assert.IsTrue(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.Child.BirthProtection.Verification.Dacl,
                                    seedReadback.Dacl));
                                Assert.IsFalse(MirrorPulseNamespacePermissionDescriptor.MatchesNativeReadback(current.TargetDacl, seedReadback.Dacl));
                                Assert.AreEqual(actual.LocalObject, seedReadback.LocalObject); Assert.AreEqual(actual.OwnerSid, seedReadback.OwnerSid);
                            }, admitted), stop);
                    Console.WriteLine($"CancellationBornSeedObservation: cut={cut}; stage=ScopeResult; scopeCompleted={seed.Outcome == CloudProtectedLocalOperationOutcome.Completed}; drained={seed.Drained}; diagnosticOnly=True.");
                    Assert.AreEqual(CloudProtectedLocalOperationOutcome.Completed, seed.Outcome); Assert.IsTrue(seed.Drained);
                    seededOriginalDescriptor = true;
                }
                if (point != cut) return;
                reached = true;
                throw new IOException("Deliberate original cancellation born recovery interruption.");
            }
        };
        // Match the Host ownership order. This scope never grants current readiness.
        await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
        await using (await work.WaitForRecoveryAsync([root.RootId], token))
        {
            _ = await Assert.ThrowsExactlyAsync<IOException>(() => scheduler.RunAsync<int>(root.InstanceId, async admitted =>
            {
                await recovery.ReplayAsync(operationId, admitted);
                return 0;
            }, token).AsTask());
        }
        Assert.IsTrue(reached); Assert.IsFalse(work.IsAdmitted(root.RootId));
        Assert.AreEqual(cut != MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterActionPrepared, seededOriginalDescriptor);
        var history = await catalog.ReadNamespaceBornMemberRecoveryActionsAsync(memberId, token);
        Assert.HasCount(originalHistory.Count + 1, history);
        foreach (var original in originalHistory)
            Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(history.Single(value => value.ActionId == original.ActionId)));
        var action = history[^1];
        Assert.AreEqual(2, action.Version); Assert.AreEqual(plan.Owner.InitializationOperationId, action.InitializationOperationId);
        Assert.AreEqual(new MirrorPulseNamespaceBornRootLabelRecoveryOwner(operationId, plan.Owner), action.RootLabelRecoveryOwner);
        var completion = await catalog.ReadNamespaceBornMemberRecoveryCompletionAsync(action.ActionId, token);
        if (cut == MirrorPulseNamespaceBornMemberRecoveryCheckpoint.AfterCompletionRecorded) Assert.IsNotNull(completion);
        else Assert.IsNull(completion);
        Assert.AreEqual(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await catalog.ReadNamespaceInitializationStatusAsync(plan.Owner.InitializationOperationId, token)));
        Assert.AreEqual(plan.Owner, await catalog.ReadRootLabelCancellationRecoveryOwnerAsync(operationId, token));
        Assert.IsNull(await catalog.ReadUnsealedRootLabelCancellationAsync(operationId, token));
        Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(operationId, token));
        Assert.IsTrue((await catalog.ReadNamespaceDispatchFencedRootsAsync(token)).Contains(root.RootId));
        return new(action, completion);
    }
}
