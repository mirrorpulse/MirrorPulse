using System.Globalization;
using System.Security.Cryptography;
using System.Security.Principal;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    // Remote ids, revisions, change ids, and the 4-byte length are synthetic fixture inputs.
    // Capture the settled native Move without an upload pump, then project and acknowledge
    // its exact original using public CfSharp APIs. Other journal rows retain their payloads.
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeControlledNonemptyDirectoryMoveCharacterizesItsOriginalJournalAcrossRuntimeOwners()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(directory, "sync"), Path.Combine(directory, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        RootRegistration root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.recursive-journal-shape"), InstanceId.New(),
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [root]);
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        SecurityIdentifier fixtureOwner = caller.User ?? throw new InvalidOperationException("The fixture caller has no user SID.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        const string fixtureRevision = "fixture-v1";
        const int fixtureColdLength = 4;
        const string sourceDirectory = "Docs/album";
        const string nestedDirectory = "Docs/album/nested";
        const string coldFile = "Docs/album/cold.bin";
        const string targetDirectory = "Docs/relocated";
        const string targetNested = "Docs/relocated/nested";
        const string targetCold = "Docs/relocated/cold.bin";
        var albumIdentity = router.CreateFileIdentity(root.InstanceId, "docs", "fixture-input:album", fixtureRevision);
        var nestedIdentity = router.CreateFileIdentity(root.InstanceId, "docs", "fixture-input:nested", fixtureRevision);
        var coldIdentity = router.CreateFileIdentity(root.InstanceId, "docs", "fixture-input:cold", fixtureRevision);
        var directoryMetadata = CloudPlaceholderMetadata.CreateDirectoryBuilder().Build();
        var fileMetadata = CloudPlaceholderMetadata.CreateFileBuilder().Build();
        var batch = new CloudRemoteChangeBatch(root.InstanceId + "/fixture-recursive-journal-shape", ReadOnlyMemory<byte>.Empty,
            [new("fixture-input-album", CloudRemoteChangeKind.DirectoryUpsert, albumIdentity.RemoteId, fixtureRevision, CloudItemKind.Directory,
                sourceDirectory, albumIdentity.ItemId, metadata: directoryMetadata),
             new("fixture-input-nested", CloudRemoteChangeKind.DirectoryUpsert, nestedIdentity.RemoteId, fixtureRevision, CloudItemKind.Directory,
                nestedDirectory, nestedIdentity.ItemId, metadata: directoryMetadata),
             new("fixture-input-cold", CloudRemoteChangeKind.FileUpsert, coldIdentity.RemoteId, fixtureRevision, CloudItemKind.File,
                coldFile, coldIdentity.ItemId, length: fixtureColdLength, metadata: fileMetadata, cursorAfter: new byte[] { 1 })],
            new byte[] { 1 });
        Guid moveId = Guid.NewGuid();
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(directory, ".mp-permission-fixture"), string.Empty, deadline.Token);
            var setupReads = new RemoteMovementContentGuard();
            var setupState = new MirrorPulseCfSharpStateSession(paths);
            await using (var setup = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(setupState).WithContentProvider(setupReads).Build())
            {
                await setup.StartAsync(deadline.Token);
                await using (var feed = setup.CreateLocalChangeFeed())
                {
                    await feed.StartAsync(deadline.Token);
                    (await new MirrorPulseRootPopulationCoordinator(setup, feed).PopulateAsync(router, deadline.Token)).ThrowIfAnyFailed();
                }
                var applied = await setup.ApplyRemoteChangesAsync(batch, cancellationToken: deadline.Token);
                Assert.AreEqual(CloudRemoteBatchStatus.Applied, applied.Status);
                Assert.IsFalse(applied.RequiresRetry);
                Assert.AreEqual(3, applied.AppliedEntryCount);
                Assert.IsEmpty(applied.ConflictIds);
                int appliedEntries = 0;
                foreach (var entry in applied.Entries)
                {
                    Assert.AreEqual(CloudRemoteApplyEntryStatus.Applied, entry.Status);
                    appliedEntries++;
                }
                Assert.AreEqual(3, appliedEntries);
                foreach (string path in new[] { "Docs", sourceDirectory, nestedDirectory })
                    await setup.GetDirectory(path).SetPopulationStateAsync(CloudDirectoryPopulationState.Complete, cancellationToken: deadline.Token);
                SetProtectedFixtureUserOwner(setup.Root, fixtureOwner);
                SetProtectedFixtureUserOwner(setup.GetDirectory("Docs"), fixtureOwner);
                SetProtectedFixtureUserOwner(setup.GetDirectory(sourceDirectory), fixtureOwner);
                SetProtectedFixtureUserOwner(setup.GetDirectory(nestedDirectory), fixtureOwner);
                SetProtectedFixtureUserOwner(setup.GetFile(coldFile), fixtureOwner);
                await AssertRecursiveJournalColdFileAsync(setup, coldFile, coldIdentity.ItemId, fixtureColdLength, deadline.Token);
                Assert.AreEqual(0, setupReads.Reads);
            }

            MirrorPulseNamespaceMemberRegistration docsMember;
            MirrorPulseNamespaceMemberRegistration albumMember;
            MirrorPulseNamespaceMemberRegistration nestedMember;
            MirrorPulseNamespaceMemberRegistration coldMember;
            Dictionary<Guid, RecursiveJournalOriginalFact> before;
            Dictionary<Guid, RecursiveJournalOriginalFact> after;
            RecursiveJournalPathScan decoded;
            await using (var first = await RecursiveJournalRuntimeOwner.OpenAsync(paths, root, router, saveTopology: true, deadline.Token))
            {
                AssertRecursiveJournalStartup(first.Runtime, root.RootId);
                Assert.IsEmpty(await first.Catalog.ReadPendingNamespaceInitializationsAsync(deadline.Token));
                var docs = await first.Runtime.InspectNamespaceMemberAsync("Docs", true, deadline.Token);
                var album = await first.Runtime.InspectNamespaceMemberAsync(sourceDirectory, true, deadline.Token);
                var nested = await first.Runtime.InspectNamespaceMemberAsync(nestedDirectory, true, deadline.Token);
                var cold = await first.Runtime.InspectNamespaceMemberAsync(coldFile, false, deadline.Token);
                var retainedDocs = (await first.Catalog.ReadNamespaceMemberAsync(docs.MemberId, deadline.Token))!;
                Assert.IsNotNull(retainedDocs.ParentMemberId);
                var anchor = (await first.Catalog.ReadNamespaceMemberAsync(retainedDocs.ParentMemberId.Value, deadline.Token))!;
                Assert.IsNull(anchor.RootId);
                Assert.IsNull(anchor.ParentMemberId);
                Assert.AreEqual(string.Empty, anchor.RelativePath);
                Assert.IsTrue(anchor.IsDirectory);
                Assert.AreEqual(retainedDocs.LocalObject.SyncRootFileId, anchor.LocalObject.LocalFileId);
                docsMember = await ReadRecursiveJournalCapturedMemberAsync(first.Catalog, docs, anchor.MemberId, "Docs", deadline.Token);
                albumMember = await ReadRecursiveJournalCapturedMemberAsync(first.Catalog, album, docs.MemberId, sourceDirectory, deadline.Token);
                nestedMember = await ReadRecursiveJournalCapturedMemberAsync(first.Catalog, nested, album.MemberId, nestedDirectory, deadline.Token);
                coldMember = await ReadRecursiveJournalCapturedMemberAsync(first.Catalog, cold, album.MemberId, coldFile, deadline.Token);
                Assert.AreEqual(albumIdentity.ItemId, album.NativeItemId);
                Assert.AreEqual(nestedIdentity.ItemId, nested.NativeItemId);
                Assert.AreEqual(coldIdentity.ItemId, cold.NativeItemId);
                before = await WaitForStableRecursiveJournalAsync(first.OfficialStore, deadline.Token);
                var intent = await first.Runtime.PrepareNamespaceMemberMoveAsync(moveId, album.MemberId, docs.MemberId, "relocated", deadline.Token);
                Assert.AreEqual(moveId, intent.OperationId);
                Assert.AreEqual(album.MemberId, intent.MemberId);
                Assert.AreEqual(albumIdentity.ItemId, intent.ItemId);
                Assert.AreEqual(sourceDirectory, intent.Source.RelativePath.Replace('\\', '/'));
                Assert.AreEqual(targetDirectory, intent.Target.RelativePath.Replace('\\', '/'));
                Assert.AreEqual(docs.MemberId, intent.Target.ParentMemberId);
                var completion = await first.Runtime.ExecuteNamespaceMemberMoveAsync(moveId, deadline.Token);
                Assert.AreEqual(moveId, completion.OperationId);
                Assert.IsTrue(completion.IsDirectory);
                Assert.AreEqual(albumIdentity.ItemId, completion.ItemId);
                Assert.AreEqual(albumIdentity.RemoteId, completion.RemoteId);
                Assert.AreEqual(targetDirectory, completion.RelativePath.Replace('\\', '/'));
                Assert.IsNull(await first.Catalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token));
                Assert.IsEmpty(await first.Catalog.ReadNamespacePendingOperationsAsync(root.RootId, deadline.Token));
                after = await WaitForStableRecursiveJournalAsync(first.OfficialStore, deadline.Token);
                foreach (var original in before)
                {
                    Assert.IsTrue(after.ContainsKey(original.Key));
                    Assert.AreEqual(original.Value, after[original.Key]);
                }
                decoded = await ReadRecursiveJournalPathsAsync(first.Journal, deadline.Token);
                Assert.AreEqual(docsMember, await first.Catalog.ReadNamespaceMemberAsync(docsMember.MemberId, deadline.Token));
                Assert.AreEqual(albumMember, await first.Catalog.ReadNamespaceMemberAsync(albumMember.MemberId, deadline.Token));
                Assert.AreEqual(nestedMember, await first.Catalog.ReadNamespaceMemberAsync(nestedMember.MemberId, deadline.Token));
                Assert.AreEqual(coldMember, await first.Catalog.ReadNamespaceMemberAsync(coldMember.MemberId, deadline.Token));
                await AssertRecursiveJournalTargetAsync(first.Runtime, first.Catalog, docsMember, albumMember, nestedMember, coldMember,
                    albumIdentity.ItemId, nestedIdentity.ItemId, coldIdentity.ItemId, moveId, deadline.Token);
                AssertRecursiveJournalPaths(paths, sourcePresent: false);
                Assert.IsEmpty(first.Probe.Calls, string.Join(",", first.Probe.Calls));
            }

            var durable = await AssertRecursiveJournalFactsRetainedAsync(paths, before, after, deadline.Token);
            await using (var second = await RecursiveJournalRuntimeOwner.OpenAsync(paths, root, router, saveTopology: false, deadline.Token))
            {
                AssertRecursiveJournalStartup(second.Runtime, root.RootId);
                Assert.IsEmpty(await second.Catalog.ReadPendingNamespaceInitializationsAsync(deadline.Token));
                Assert.IsNull(await second.Catalog.ReadPendingNamespaceMemberMoveAsync(root.RootId, deadline.Token));
                Assert.AreEqual(docsMember, await second.Catalog.ReadNamespaceMemberAsync(docsMember.MemberId, deadline.Token));
                Assert.AreEqual(albumMember, await second.Catalog.ReadNamespaceMemberAsync(albumMember.MemberId, deadline.Token));
                Assert.AreEqual(nestedMember, await second.Catalog.ReadNamespaceMemberAsync(nestedMember.MemberId, deadline.Token));
                Assert.AreEqual(coldMember, await second.Catalog.ReadNamespaceMemberAsync(coldMember.MemberId, deadline.Token));
                await AssertRecursiveJournalTargetAsync(second.Runtime, second.Catalog, docsMember, albumMember, nestedMember, coldMember,
                    albumIdentity.ItemId, nestedIdentity.ItemId, coldIdentity.ItemId, moveId, deadline.Token);
                AssertRecursiveJournalPaths(paths, sourcePresent: false);
                Assert.IsEmpty(second.Probe.Calls, string.Join(",", second.Probe.Calls));
            }
            await AssertRecursiveJournalFactsRetainedAsync(paths, before, after, deadline.Token);

            var finalReads = new RemoteMovementContentGuard();
            var finalState = new MirrorPulseCfSharpStateSession(paths);
            await using (var inspect = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(finalState)
                .WithContentProvider(finalReads).Build())
            {
                await inspect.StartAsync(deadline.Token);
                await AssertRecursiveJournalColdFileAsync(inspect, targetCold, coldIdentity.ItemId, fixtureColdLength, deadline.Token);
                Assert.AreEqual(0, finalReads.Reads);
                var moveRow = durable.Values.Single(row => row.Kind == nameof(CloudStateOperationKind.Move) &&
                    !before.ContainsKey(row.OperationId));
                var originalMovePaths = decoded.Paths[moveRow.OperationId];
                Assert.AreEqual(sourceDirectory, originalMovePaths.PreviousPath?.Replace('\\', '/'));
                Assert.AreEqual(targetDirectory, originalMovePaths.RelativePath.Replace('\\', '/'));
                await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths, deadline.Token);
                var admission = await new MirrorPulseJournalDirectoryMoveAdmissionReader(catalog,
                    finalState, router, inspect)
                    .ReadAsync(new(moveRow.OperationId, moveRow.Sequence, root.InstanceId, root.UniquenessKey,
                        MirrorPulseWorkerChangeKind.Move, "relocated", null, "album", true, albumIdentity.ItemId, DateTimeOffset.UtcNow), deadline.Token);
                Assert.IsNotNull(admission);
                Assert.AreEqual(moveId, admission.LocalMoveOperationId);
                Assert.AreEqual(moveRow.OperationId, admission.JournalOperationId);
                Assert.AreEqual(moveRow.PayloadSha256, admission.OriginalJournalPayloadSha256);
                Assert.HasCount(3, admission.Members);
                Assert.AreEqual(coldIdentity.ItemId, admission.Members.Single(member => member.TargetPath == targetCold).OriginalItemId);
                await AssertRecursiveJournalRemoteProjectionAsync(admission, root, router, deadline.Token);
                var mutation = new MirrorPulseMutationIntent(moveRow.OperationId, root.InstanceId, root.UniquenessKey,
                    MirrorPulseWorkerChangeKind.Move, "relocated", "album", true, admission.Root.OriginalRevision,
                    null, null, MirrorPulseMutationOrigin.Journal);
                var startedMove = await catalog.StartJournalDirectoryMoveAsync(admission, mutation, deadline.Token);
                Assert.AreEqual(MirrorPulseMutationState.Executing, startedMove.Mutation.State);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.TransitionMutationAsync(moveRow.OperationId,
                    MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, admission.Root.OriginalRevision, deadline.Token));
                await Assert.ThrowsExactlyAsync<InvalidDataException>(() =>
                    catalog.AcceptJournalDirectoryMoveAsync(moveRow.OperationId, "another revision", deadline.Token));
                await catalog.AcceptJournalDirectoryMoveAsync(moveRow.OperationId, admission.Root.OriginalRevision!, deadline.Token);
                var targetPlan = admission.Members.Select(member =>
                {
                    string path = member.TargetPath[(root.DirectoryName.Length + 1)..];
                    var identity = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, path, member.OriginalRevision);
                    return new MirrorPulseDirectoryMoveTarget(member.MemberId, identity.ItemId, identity.RemoteId, identity.RemoteRevision);
                }).ToArray();
                var saved = await catalog.SaveJournalDirectoryMoveTargetsAsync(moveRow.OperationId, targetPlan, deadline.Token);
                Assert.IsFalse(saved.AcknowledgementStarted);
                Assert.AreEqual(0, saved.ProjectedMemberCount);
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    catalog.BeginJournalDirectoryMoveAcknowledgementAsync(moveRow.OperationId, deadline.Token));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    catalog.CompleteJournalDirectoryMoveAcknowledgementAsync(moveRow.OperationId, deadline.Token));
                var changedTarget = targetPlan.ToArray();
                changedTarget[1] = changedTarget[1] with { ItemId = Guid.NewGuid() };
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    catalog.SaveJournalDirectoryMoveTargetsAsync(moveRow.OperationId, changedTarget, deadline.Token));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    catalog.RecordJournalDirectoryMoveProjectionAsync(moveRow.OperationId, 0, targetPlan[1], deadline.Token));
                await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    catalog.RecordJournalDirectoryMoveProjectionAsync(moveRow.OperationId, 1, targetPlan[1], deadline.Token));
                await using var role = new MirrorPulseNamespaceExecutionSession();
                await using var actor = new MirrorPulseNamespaceActor();
                await using var scheduler = new MirrorPulseInstanceScheduler();
                await using var work = new MirrorPulseNamespaceWorkAdmission([root.RootId]);
                await using var permissions = new MirrorPulseNamespacePermissionCoordinator(catalog);
                var started = await new MirrorPulseNamespaceStartupCoordinator(inspect, catalog, router, role, actor,
                    scheduler, permissions, work).StartAsync(deadline.Token);
                await using var guard = started.Guard;
                Assert.IsTrue(started.Roots.Single().Admitted);
                var originalCold = admission.Members.Single(member => member.TargetPath == targetCold);
                var projectedCold = router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, "relocated/cold.bin", originalCold.OriginalRevision);
                var patch = new MirrorPulseJournalDirectoryMoveIdentityPatch(inspect, finalState, catalog, router, actor, role);
                for (int ordinal = 0; ordinal < admission.Members.Count; ordinal++)
                {
                    var target = targetPlan[ordinal];
                    var identity = new CloudPlaceholderIdentity(target.ItemId, target.RemoteId, target.Revision);
                    var projection = new MirrorPulseJournalDirectoryMoveProjection(admission.Members[ordinal], identity);
                    var observed = await patch.ApplyAsync(admission, projection, deadline.Token);
                    if (ordinal == 0)
                    {
                        // Native projection committed, but the Host has not yet retained its readback.
                        var interrupted = await catalog.ReadJournalDirectoryMoveAsync(moveRow.OperationId, deadline.Token);
                        Assert.AreEqual(0, interrupted!.ProjectedMemberCount);
                        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                            catalog.BeginJournalDirectoryMoveAcknowledgementAsync(moveRow.OperationId, deadline.Token));
                        Assert.AreEqual(observed, await patch.ApplyAsync(admission, projection, deadline.Token));
                    }
                    var recorded = await catalog.RecordJournalDirectoryMoveProjectionAsync(moveRow.OperationId, ordinal, observed, deadline.Token);
                    Assert.AreEqual(ordinal + 1, recorded.ProjectedMemberCount);
                    var replay = await catalog.RecordJournalDirectoryMoveProjectionAsync(moveRow.OperationId, ordinal, observed, deadline.Token);
                    Assert.AreEqual(recorded.ProjectedMemberCount, replay.ProjectedMemberCount);
                }
                await AssertRecursiveJournalColdFileAsync(inspect, targetCold, projectedCold.ItemId, fixtureColdLength, deadline.Token);
                Assert.AreEqual(0, finalReads.Reads);
                var projectedJournal = await ReadRecursiveJournalOriginalsAsync(finalState.OpenStore, deadline.Token);
                foreach (var original in durable.Values)
                {
                    Assert.IsTrue(projectedJournal.TryGetValue(original.OperationId, out var retained));
                    Assert.AreEqual(original.Sequence, retained.Sequence);
                    Assert.AreEqual(original.Kind, retained.Kind);
                    Assert.AreEqual(original.PayloadSha256, retained.PayloadSha256);
                }
                Assert.IsNull(projectedJournal[moveRow.OperationId].ItemId);
                await using var movedFeed = inspect.CreateLocalChangeFeed();
                await movedFeed.StartAsync(deadline.Token);
                var projectedPaths = await ReadRecursiveJournalPathsAsync(movedFeed, deadline.Token);
                Assert.IsFalse(projectedPaths.RescanWithheld);
                Assert.IsNull(projectedPaths.UndecodedOperationId);
                Assert.AreEqual(originalMovePaths, projectedPaths.Paths[moveRow.OperationId]);
                await catalog.BeginJournalDirectoryMoveAcknowledgementAsync(moveRow.OperationId, deadline.Token);
                await movedFeed.AcknowledgeAsync([new CloudLocalChangeAcknowledgement(moveRow.OperationId, admission.Root.OriginalRevision)], deadline.Token);
                var acknowledged = await ReadRecursiveJournalOriginalsAsync(finalState.OpenStore, deadline.Token);
                Assert.IsFalse(acknowledged.ContainsKey(moveRow.OperationId));
                foreach (var retained in projectedJournal.Values.Where(row => row.OperationId != moveRow.OperationId))
                {
                    Assert.IsTrue(acknowledged.TryGetValue(retained.OperationId, out var other));
                    Assert.AreEqual(retained.PayloadSha256, other.PayloadSha256);
                }
                var transport = new RecursiveJournalProjectionWorker(admission.Members, root.DirectoryName);
                var recovery = new MirrorPulseJournalDirectoryMoveRecovery(catalog, finalState, router, movedFeed,
                    transport, transport, patch);
                Assert.IsTrue(await recovery.ResumeAsync(moveRow.OperationId, deadline.Token));
                Assert.IsTrue(await recovery.ResumeAsync(moveRow.OperationId, deadline.Token));
                var finished = await catalog.ReadJournalDirectoryMoveAsync(moveRow.OperationId, deadline.Token);
                Assert.IsNotNull(finished);
                Assert.AreEqual(MirrorPulseMutationState.Acknowledged, finished.Mutation.State);
                Assert.IsTrue(finished.AcknowledgementStarted);
                Assert.AreEqual(admission.Members.Count, finished.ProjectedMemberCount);
                CollectionAssert.AreEqual(targetPlan, finished.Targets!.ToArray());
            }

            var added = durable.Values.Where(row => !before.ContainsKey(row.OperationId)).OrderBy(row => row.Sequence).ThenBy(row => row.OperationId).ToArray();
            int rawMoveCount = added.Count(row => row.Kind == nameof(CloudStateOperationKind.Move));
            int decodedMoveCount = 0;
            bool descendantPaths = false;
            foreach (var row in added)
            {
                if (!decoded.Paths.TryGetValue(row.OperationId, out var path)) continue;
                if (path.Kind == nameof(CloudLocalChangeKind.Move)) decodedMoveCount++;
                if (RecursiveJournalDescendantPath(path)) descendantPaths = true;
            }
            bool descendantOriginals = added.Any(row => row.ItemId == nestedIdentity.ItemId || row.ItemId == coldIdentity.ItemId);
            string kinds = added.Length == 0 ? "none" : string.Join(",", added.GroupBy(row => row.Kind).OrderBy(group => group.Key).Select(group => group.Key + "=" + group.Count()));
            var shown = added.Where(row => row.Kind == nameof(CloudStateOperationKind.Move) || row.ItemId == nestedIdentity.ItemId || row.ItemId == coldIdentity.ItemId)
                .Take(RecursiveJournalMoveViewLimit).Select(row => DescribeRecursiveJournalMove(row, decoded, nestedIdentity.ItemId, coldIdentity.ItemId)).ToArray();
            string moveText = shown.Length == 0 ? "none" : string.Join(" | ", shown);
            Guid? undecodedOperation = decoded.UndecodedOperationId;
            string undecoded = undecodedOperation is null ? "none" : undecodedOperation.Value.ToString("D") + "@" + decoded.UndecodedSequence;
            TestContext.WriteLine("RecursiveJournalShape: fixtureRemoteIds=fixture-input:album,fixture-input:nested,fixture-input:cold; " +
                "fixtureRevision=" + fixtureRevision + "; fixtureColdLength=" + fixtureColdLength + "; capturedOriginals=True; retainedMemberIds=True; " +
                "retainedNativeItemIds=True; sameRootRuntimeDirectoryMove=True; sourceAbsent=True; targetPresent=True; beforeCount=" + before.Count +
                "; stableAfterCount=" + after.Count + "; durableCount=" + durable.Count + "; addedCount=" + added.Length + "; addedKinds=" + kinds +
                "; rawMoveCount=" + rawMoveCount + "; decodedMoveCount=" + decodedMoveCount + "; descendantOriginals=" + descendantOriginals +
                "; descendantPaths=" + descendantPaths + "; shownMoves=" + shown.Length + "; moves=" + moveText + "; undecoded=" + undecoded +
                "; rescanWithheld=" + decoded.RescanWithheld + "; originalRowsUnchangedAcrossReopen=True; exactRootMoveAcknowledgedAfterProjection=True; probeCalls=0; " +
                "coldOnlineOnly=True; byteTransferProved=False; signedProcessExecutionProved=False.");
        }
        finally
        {
            try
            {
                await RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(directory, paths, fixtureOwner,
                    ["Docs", sourceDirectory, nestedDirectory, targetDirectory, targetNested], [coldFile, targetCold]);
            }
            finally { registry.Unregister(paths.SyncRootPath); }
            DeleteFixture(directory);
        }
    }

    private static async Task AssertRecursiveJournalRemoteProjectionAsync(MirrorPulseJournalDirectoryMoveAdmission original,
        RootRegistration root, MirrorPulseRootRouter router, CancellationToken token)
    {
        var members = original.Members.Select((member, index) => member with
        {
            OriginalRevision = $"native-v2:0000000000000001:{(index + 1).ToString("x32", CultureInfo.InvariantCulture)}:{(member.IsDirectory ? "directory" : "file")}:1",
        }).ToArray();
        var fixture = original with { Members = members };
        var remote = new RecursiveJournalProjectionWorker(members, root.DirectoryName);
        var plan = await MirrorPulseJournalDirectoryMoveProjectionPlan.CreateAsync(fixture, members[0].OriginalRevision!, router, remote, token);
        Assert.HasCount(3, plan);
        foreach (var member in plan)
        {
            string path = member.Original.TargetPath[(root.DirectoryName.Length + 1)..];
            Assert.AreEqual(router.CreateFileIdentity(root.InstanceId, root.UniquenessKey, path, member.Original.OriginalRevision), member.TargetIdentity);
            Assert.AreNotEqual(member.Original.OriginalItemId, member.TargetIdentity.ItemId);
        }
        remote.ReplaceFileGeneration = true;
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() =>
            MirrorPulseJournalDirectoryMoveProjectionPlan.CreateAsync(fixture, members[0].OriginalRevision!, router, remote, token));
    }

    private sealed class RecursiveJournalProjectionWorker(IReadOnlyList<MirrorPulseJournalDirectoryMoveMember> members, string label)
        : IMirrorPulseWorkerDirectoryPageSource, IMirrorPulseWorkerMutationTransport
    {
        public bool ReplaceFileGeneration { get; set; }

        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("Directory Move recovery must not send a Delete.");

        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken cancellationToken) =>
            throw new AssertFailedException("A retained accepted directory Move must not be sent again.");

        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request,
            CancellationToken cancellationToken)
        {
            var entries = new List<MirrorPulseWorkerDirectoryEntry>();
            foreach (var member in members)
            {
                string path = member.TargetPath[(label.Length + 1)..];
                int slash = path.LastIndexOf('/');
                if ((slash < 0 ? string.Empty : path[..slash]) != request.NormalizedPath) continue;
                string revision = member.OriginalRevision!;
                if (!member.IsDirectory) revision = ReplaceFileGeneration
                    ? "native-v2:0000000000000001:00000000000000000000000000000009:file:2"
                    : revision[..^1] + "2";
                entries.Add(new(path, revision, member.IsDirectory ? "Directory" : "File", path,
                    member.IsDirectory ? null : 4, null, null, false));
            }
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(entries, ReadOnlyMemory<byte>.Empty, true));
        }
    }

    private const int RecursiveJournalPageLimit = 256;
    private const int RecursiveJournalPageCeiling = 16;
    private const int RecursiveJournalStableReads = 8;
    private const int RecursiveJournalMoveViewLimit = 16;

    private static void AssertRecursiveJournalStartup(IMirrorPulseCloudRuntime runtime, RootId rootId)
    {
        var started = runtime.NamespaceStartupResults.Single();
        Assert.AreEqual(rootId, started.RootId);
        Assert.IsTrue(started.Admitted);
        Assert.IsNull(started.RecoveryCode);
    }

    private static async Task<MirrorPulseNamespaceMemberRegistration> ReadRecursiveJournalCapturedMemberAsync(
        MirrorPulseProductCatalog catalog, MirrorPulseNamespaceMemberInspection inspection, Guid? parentMemberId, string relativePath, CancellationToken token)
    {
        Assert.AreEqual(parentMemberId, inspection.ActualParentMemberId);
        Assert.AreEqual(relativePath, inspection.RelativePath.Replace('\\', '/'));
        var member = await catalog.ReadNamespaceMemberAsync(inspection.MemberId, token);
        Assert.IsNotNull(member);
        Assert.AreEqual(MirrorPulseNamespaceMemberOrigin.CapturedOriginal, member.Origin);
        Assert.AreEqual(inspection.MemberId, member.MemberId);
        Assert.AreEqual(relativePath, member.RelativePath.Replace('\\', '/'));
        Assert.AreEqual(inspection.IsDirectory, member.IsDirectory);
        return member;
    }

    private static async Task AssertRecursiveJournalTargetAsync(IMirrorPulseCloudRuntime runtime, MirrorPulseProductCatalog catalog,
        MirrorPulseNamespaceMemberRegistration docs, MirrorPulseNamespaceMemberRegistration album, MirrorPulseNamespaceMemberRegistration nested,
        MirrorPulseNamespaceMemberRegistration cold, Guid albumItemId, Guid nestedItemId, Guid coldItemId, Guid moveId, CancellationToken token)
    {
        var moved = await runtime.InspectNamespaceMemberAsync("Docs/relocated", true, token);
        var movedNested = await runtime.InspectNamespaceMemberAsync("Docs/relocated/nested", true, token);
        var movedCold = await runtime.InspectNamespaceMemberAsync("Docs/relocated/cold.bin", false, token);
        Assert.AreEqual(album.MemberId, moved.MemberId);
        Assert.AreEqual(nested.MemberId, movedNested.MemberId);
        Assert.AreEqual(cold.MemberId, movedCold.MemberId);
        Assert.AreEqual(albumItemId, moved.NativeItemId);
        Assert.AreEqual(nestedItemId, movedNested.NativeItemId);
        Assert.AreEqual(coldItemId, movedCold.NativeItemId);
        Assert.AreEqual(docs.MemberId, moved.ActualParentMemberId);
        Assert.AreEqual(album.MemberId, movedNested.ActualParentMemberId);
        Assert.AreEqual(album.MemberId, movedCold.ActualParentMemberId);
        await AssertRecursiveJournalLocationAsync(catalog, album.MemberId, docs.MemberId, "Docs/relocated", moveId, token);
        await AssertRecursiveJournalLocationAsync(catalog, nested.MemberId, album.MemberId, "Docs/relocated/nested", moveId, token);
        await AssertRecursiveJournalLocationAsync(catalog, cold.MemberId, album.MemberId, "Docs/relocated/cold.bin", moveId, token);
        Assert.AreEqual("Docs", (await catalog.ReadNamespaceMemberLocationAsync(docs.MemberId, token)).RelativePath.Replace('\\', '/'));
    }

    private static async Task AssertRecursiveJournalLocationAsync(MirrorPulseProductCatalog catalog, Guid memberId, Guid parentMemberId,
        string relativePath, Guid moveId, CancellationToken token)
    {
        var location = await catalog.ReadNamespaceMemberLocationAsync(memberId, token);
        Assert.AreEqual(parentMemberId, location.ParentMemberId);
        Assert.AreEqual(relativePath, location.RelativePath.Replace('\\', '/'));
        Assert.AreEqual(moveId, location.CurrentPositionOperationId);
    }

    private static void AssertRecursiveJournalPaths(MirrorPulseStoragePaths paths, bool sourcePresent)
    {
        Assert.AreEqual(sourcePresent, Directory.Exists(Path.Combine(paths.SyncRootPath, "Docs", "album")));
        Assert.AreEqual(sourcePresent, Directory.Exists(Path.Combine(paths.SyncRootPath, "Docs", "album", "nested")));
        Assert.AreEqual(sourcePresent, File.Exists(Path.Combine(paths.SyncRootPath, "Docs", "album", "cold.bin")));
        Assert.IsTrue(Directory.Exists(Path.Combine(paths.SyncRootPath, "Docs", "relocated")));
        Assert.IsTrue(Directory.Exists(Path.Combine(paths.SyncRootPath, "Docs", "relocated", "nested")));
        Assert.IsTrue(File.Exists(Path.Combine(paths.SyncRootPath, "Docs", "relocated", "cold.bin")));
    }

    private static async Task AssertRecursiveJournalColdFileAsync(CloudFileSystem fileSystem, string relativePath, Guid itemId, int length, CancellationToken token)
    {
        var cold = await fileSystem.GetFile(relativePath).InspectAsync(token);
        Assert.IsTrue(cold.IsPlaceholder);
        Assert.AreEqual(CloudContentAvailability.OnlineOnly, cold.ContentAvailability);
        Assert.AreEqual((long)length, cold.Length);
        Assert.IsFalse(cold.PlaceholderIdentity.IsEmpty);
        Assert.AreEqual(itemId, CloudPlaceholderIdentity.Decode(cold.PlaceholderIdentity.Span).ItemId);
    }

    private static async Task<Dictionary<Guid, RecursiveJournalOriginalFact>> WaitForStableRecursiveJournalAsync(ICloudStateStore store, CancellationToken token)
    {
        Dictionary<Guid, RecursiveJournalOriginalFact>? previous = null;
        int stable = 0;
        while (stable < RecursiveJournalStableReads)
        {
            var current = await ReadRecursiveJournalOriginalsAsync(store, token);
            if (previous is not null && SameRecursiveJournalFacts(previous, current)) stable++;
            else stable = 0;
            previous = current;
            if (stable < RecursiveJournalStableReads) await Task.Delay(25, token);
        }
        return previous ?? throw new InvalidOperationException("The official journal stability read did not complete.");
    }

    private static bool SameRecursiveJournalFacts(Dictionary<Guid, RecursiveJournalOriginalFact> left, Dictionary<Guid, RecursiveJournalOriginalFact> right)
    {
        if (left.Count != right.Count) return false;
        foreach (var pair in left)
        {
            if (!right.TryGetValue(pair.Key, out var fact) || !Equals(fact, pair.Value)) return false;
        }
        return true;
    }

    private static async Task<Dictionary<Guid, RecursiveJournalOriginalFact>> ReadRecursiveJournalOriginalsAsync(ICloudStateStore store, CancellationToken token)
    {
        await using var read = await store.BeginTransactionAsync(token);
        var paging = read.Operations as ICloudOperationJournalPaging;
        Assert.IsNotNull(paging);
        long through = await paging.GetHighWaterSequenceAsync(token);
        var rows = new Dictionary<Guid, RecursiveJournalOriginalFact>();
        long after = 0;
        for (int index = 0; index < RecursiveJournalPageCeiling; index++)
        {
            var page = await paging.ReadPageAsync(after, through, RecursiveJournalPageLimit, token);
            foreach (var row in page.Operations) rows.Add(row.OperationId, RecursiveJournalOriginalFact.From(row));
            if (!page.HasMore)
            {
                await read.RollbackAsync(token);
                return rows;
            }
            Assert.IsGreaterThan(after, page.LastScannedSequence);
            after = page.LastScannedSequence;
        }
        await read.RollbackAsync(token);
        Assert.Fail("The official journal exceeded its complete observation ceiling.");
        return rows;
    }

    private static async Task<Dictionary<Guid, RecursiveJournalOriginalFact>> AssertRecursiveJournalFactsRetainedAsync(MirrorPulseStoragePaths paths,
        Dictionary<Guid, RecursiveJournalOriginalFact> before, Dictionary<Guid, RecursiveJournalOriginalFact> after, CancellationToken token)
    {
        await using var store = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new(paths.SyncRootPath), token);
        var durable = await ReadRecursiveJournalOriginalsAsync(store, token);
        foreach (var original in before.Concat(after))
        {
            Assert.IsTrue(durable.ContainsKey(original.Key), original.Key.ToString("D"));
            Assert.AreEqual(original.Value, durable[original.Key]);
        }
        return durable;
    }

    private static async Task<RecursiveJournalPathScan> ReadRecursiveJournalPathsAsync(CloudLocalChangeFeed feed, CancellationToken token)
    {
        var paths = new Dictionary<Guid, RecursiveJournalDecodedPath>();
        var scan = await feed.BeginScanAsync(token);
        if (scan.RequiresFullRescan) return new(paths, true, null, null);
        long after = 0;
        for (int index = 0; index < RecursiveJournalPageCeiling; index++)
        {
            CloudLocalChangePage page;
            try { page = await feed.ReadPageAsync(scan, after, RecursiveJournalPageLimit, token); }
            catch (CloudLocalChangeJournalException error) { return new(paths, false, error.OperationId, error.Sequence); }
            if (page.RequiresFullRescan) return new(paths, true, null, null);
            foreach (var change in page.Changes)
                paths[change.OperationId] = new(change.Kind.ToString(), change.IsDirectory, change.PreviousRelativePath, change.RelativePath);
            if (!page.HasMore) return new(paths, false, null, null);
            Assert.IsGreaterThan(after, page.LastScannedSequence);
            after = page.LastScannedSequence;
        }
        Assert.Fail("The official local-change scan exceeded its observation ceiling.");
        return new(paths, false, null, null);
    }

    private static bool RecursiveJournalDescendantPath(RecursiveJournalDecodedPath path)
    {
        string current = path.RelativePath.Replace('\\', '/');
        string? previous = path.PreviousPath?.Replace('\\', '/');
        return IsRecursiveJournalDescendant(current) || previous is not null && IsRecursiveJournalDescendant(previous);
        static bool IsRecursiveJournalDescendant(string candidate) =>
            candidate.StartsWith("Docs/album/", StringComparison.Ordinal) || candidate.StartsWith("Docs/relocated/", StringComparison.Ordinal);
    }

    private static string DescribeRecursiveJournalMove(RecursiveJournalOriginalFact row, RecursiveJournalPathScan decoded, Guid nestedItemId, Guid coldItemId)
    {
        decoded.Paths.TryGetValue(row.OperationId, out var path);
        string? previous = path?.PreviousPath;
        string decodedKind = path is null ? "withheld" : path.Kind;
        string directory = path is null ? "unknown" : path.IsDirectory.ToString();
        string from = previous is null ? "-" : previous.Replace('\\', '/');
        string to = path is null ? "withheld" : path.RelativePath.Replace('\\', '/');
        bool descendantItem = row.ItemId == nestedItemId || row.ItemId == coldItemId;
        bool descendantPath = path is not null && RecursiveJournalDescendantPath(path);
        return "seq=" + row.Sequence + ",kind=" + row.Kind + ",decoded=" + decodedKind + ",dir=" + directory +
            ",item=" + (row.ItemId?.ToString("D") ?? "-") + ",from=" + from + ",to=" + to +
            ",descendantItem=" + descendantItem + ",descendantPath=" + descendantPath;
    }

    private sealed record RecursiveJournalOriginalFact(Guid OperationId, long Sequence, string Kind, Guid? ItemId, int AttemptCount, DateTimeOffset CreatedAt, string PayloadSha256)
    {
        public static RecursiveJournalOriginalFact From(CloudOperationJournalEntry operation) => new(operation.OperationId, operation.Sequence,
            operation.Kind.ToString(), operation.ItemId, operation.AttemptCount, operation.CreatedAt, Convert.ToHexString(SHA256.HashData(operation.Payload.Span)));
    }

    private sealed record RecursiveJournalDecodedPath(string Kind, bool IsDirectory, string? PreviousPath, string RelativePath);
    private sealed record RecursiveJournalPathScan(Dictionary<Guid, RecursiveJournalDecodedPath> Paths, bool RescanWithheld, Guid? UndecodedOperationId, long? UndecodedSequence);

    private sealed class RecursiveJournalRuntimeOwner : IAsyncDisposable
    {
        private readonly MirrorPulseNamespaceExecutionSession _role;
        private readonly MirrorPulseNamespaceActor _actor;
        private readonly MirrorPulseInstanceScheduler _scheduler;
        private readonly MirrorPulseNamespaceWorkAdmission _work;
        private RecursiveJournalRuntimeOwner(IMirrorPulseCloudRuntime runtime, MirrorPulseProductCatalog catalog, MirrorPulseNamespaceExecutionSession role,
            MirrorPulseNamespaceActor actor, MirrorPulseInstanceScheduler scheduler, MirrorPulseNamespaceWorkAdmission work,
            DisabledCreationStoreObserver store, CloudLocalChangeFeed journal, MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker probe)
        {
            Runtime = runtime; Catalog = catalog; _role = role; _actor = actor; _scheduler = scheduler; _work = work;
            OfficialStore = store.Store; Journal = journal; Probe = probe;
        }

        internal IMirrorPulseCloudRuntime Runtime { get; }
        internal MirrorPulseProductCatalog Catalog { get; }
        internal ICloudStateStore OfficialStore { get; }
        internal CloudLocalChangeFeed Journal { get; }
        internal MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker Probe { get; }

        public static async Task<RecursiveJournalRuntimeOwner> OpenAsync(MirrorPulseStoragePaths paths, RootRegistration root, MirrorPulseRootRouter router, bool saveTopology, CancellationToken token)
        {
            MirrorPulseNamespaceExecutionSession? role = new();
            MirrorPulseNamespaceActor? actor = new();
            MirrorPulseInstanceScheduler? scheduler = new();
            MirrorPulseNamespaceWorkAdmission? work = new([root.RootId]);
            MirrorPulseProductCatalog? catalog = null;
            IMirrorPulseCloudRuntime? runtime = null;
            try
            {
                catalog = await MirrorPulseProductCatalog.OpenAsync(paths, token);
                if (saveTopology) await SaveAcceptedIdentityFixtureTopologyAsync(catalog, root, token);
                var probe = new MirrorPulseNormalUserWorkerTransportTests.IdentityProbeWorker(role);
                var transport = new MirrorPulseNormalUserWorkerTransport(role, probe, probe, probe, probe, probe);
                CloudLocalChangeFeed? journal = null;
                DisabledCreationStoreObserver? store = null;
                var factory = new CfSharpMirrorPulseCloudRuntimeFactory(
                    provider: new MirrorPulseDemandProvider(router, transport, new MirrorPulseAdapterDirectoryPageSource(transport), work, catalog),
                    router: router, catalog: catalog, mayDispatch: _ => false, scheduler: scheduler, namespaceExecution: role, namespaceActor: actor,
                    namespaceWork: work, initializeNamespaceOnStart: true,
                    transfers: new MirrorPulseTransferWorkerServices(transport, transport, transport, transport, _ => 2, transport))
                {
                    StateStoreDecorator = inner => store = new DisabledCreationStoreObserver(inner),
                    TransferJournalReady = feed => journal = feed
                };
                runtime = factory.Create(paths);
                await runtime.StartAsync(token);
                if (store is null || journal is null || !journal.IsStarted)
                    throw new InvalidOperationException("The protected runtime did not retain its official store and running local-change feed.");
                var owner = new RecursiveJournalRuntimeOwner(runtime, catalog, role, actor, scheduler, work, store, journal, probe);
                runtime = null; catalog = null; role = null; actor = null; scheduler = null; work = null;
                return owner;
            }
            finally
            {
                if (runtime is not null) await runtime.DisposeAsync();
                if (catalog is not null) await catalog.DisposeAsync();
                if (work is not null) await work.DisposeAsync();
                if (scheduler is not null) await scheduler.DisposeAsync();
                if (actor is not null) await actor.DisposeAsync();
                if (role is not null) await role.DisposeAsync();
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Runtime.DisposeAsync();
            await Catalog.DisposeAsync();
            await _work.DisposeAsync();
            await _scheduler.DisposeAsync();
            await _actor.DisposeAsync();
            await _role.DisposeAsync();
        }
    }
}
