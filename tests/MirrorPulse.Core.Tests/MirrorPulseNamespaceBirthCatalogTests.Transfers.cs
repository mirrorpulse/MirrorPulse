using System.Security.Cryptography;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 1)]
    [DataRow(false, 2)]
    [DataRow(true, 0)]
    [DataRow(true, 1)]
    [DataRow(true, 2)]
    public async Task TransferRetainsBothRootsAndOriginalSourceAcrossCopyFaultCuts(bool sameInstance, int cut)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareTransferFixtureAsync(first, sameInstance); plan = setup.Plan;
            var original = await first.PrepareNamespaceTransferAsync(plan);
            Assert.AreEqual(original, await first.PrepareNamespaceTransferAsync(plan));
            if (cut > 0) await first.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1));
            if (cut > 1) await first.RecordNamespaceTransferCopyAcceptedAsync(plan.OperationId, "accepted-v1", plan.PreparedAt.AddSeconds(2));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var recovered = (await second.ReadNamespaceTransferAsync(plan.OperationId))!;
        Assert.AreEqual(plan, recovered.Plan); Assert.AreEqual(cut > 0, recovered.CopyStartedAt is not null);
        Assert.AreEqual(cut > 1, recovered.CopyAcceptedAt is not null);
        CollectionAssert.AreEquivalent(new[] { plan.SourceRoute.RootId, plan.TargetRoute.RootId }, (await second.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        foreach (var root in new[] { plan.SourceRoute.RootId, plan.TargetRoute.RootId })
        {
            var pending = (await second.ReadNamespacePendingOperationsAsync(root)).Single();
            Assert.AreEqual(new(plan.OperationId, root, MirrorPulseNamespacePendingOperationKind.Transfer), pending);
        }
        Assert.AreEqual(plan.Source.Context.Member, await second.ReadNamespaceMemberChildAsync(plan.SourceParent.Context.Member.MemberId, "new.txt"));
        Assert.IsNull(await second.ReadNamespaceMemberChildAsync(plan.TargetParent.Context.Member.MemberId, "moved.txt"));
        Assert.AreEqual(recovered, await second.PrepareNamespaceTransferAsync(plan));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespaceTransferAsync(plan with { Sha256 = new string('F', 64) }));
        if (cut > 0) await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1)));
        if (cut > 1)
        {
            Assert.AreEqual(recovered, await second.RecordNamespaceTransferCopyAcceptedAsync(plan.OperationId, "accepted-v1", plan.PreparedAt.AddSeconds(2)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.RecordNamespaceTransferCopyAcceptedAsync(plan.OperationId, "another-target", plan.PreparedAt.AddSeconds(2)));
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("source-binding")]
    [DataRow("source-identity")]
    [DataRow("source-parent")]
    [DataRow("target-binding")]
    [DataRow("target-role")]
    [DataRow("target-root")]
    [DataRow("target-path")]
    [DataRow("scope")]
    [DataRow("same-root")]
    [DataRow("empty-effect")]
    [DataRow("shared-effect")]
    [DataRow("hash")]
    [DataRow("length")]
    [DataRow("chronology")]
    public async Task TransferRejectsSubstitutedOriginalsBeforeReservingEitherRoot(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareTransferFixtureAsync(catalog); var plan = setup.Plan;
        var altered = changed switch
        {
            "source-binding" => plan with { Source = plan.Source with { Context = plan.Source.Context with { Member = plan.Source.Context.Member with { LocalObject = plan.Source.Context.Member.LocalObject with { LocalFileId = Guid.NewGuid() } } } } },
            "source-identity" => plan with { Source = plan.Source with { Identity = plan.Source.Identity with { ItemId = Guid.NewGuid() } } },
            "source-parent" => plan with { SourceParent = plan.TargetParent },
            "target-binding" => plan with { TargetParent = plan.TargetParent with { Context = plan.TargetParent.Context with { Member = plan.TargetParent.Context.Member with { LocalObject = plan.TargetParent.Context.Member.LocalObject with { LocalFileId = Guid.NewGuid() } } } } },
            "target-role" => plan with { TargetParent = plan.TargetParent with { Context = plan.TargetParent.Context with { Protection = plan.TargetParent.Context.Protection with { RoleSid = RelocatedRole } } } },
            "target-root" => plan with { TargetRoute = plan.TargetRoute with { RootId = RootId.New() } },
            "target-path" => plan with { TargetRelativePath = "Other/moved.txt" },
            "scope" => plan with { TargetRoute = plan.TargetRoute with { ScopeSha256 = new string('F', 64) } },
            "same-root" => plan with { TargetRoute = plan.SourceRoute },
            "empty-effect" => plan with { CopyOperationId = Guid.Empty },
            "shared-effect" => plan with { DeleteOperationId = plan.CopyOperationId },
            "hash" => plan with { Sha256 = "not-a-hash" },
            "length" => plan with { Length = -1 },
            _ => plan with { PreparedAt = plan.Source.Context.Member.RegisteredAt.AddDays(-1) }
        };
        try { await catalog.PrepareNamespaceTransferAsync(altered); Assert.Fail("A substituted transfer was admitted."); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
        Assert.IsNull(await catalog.ReadNamespaceTransferAsync(plan.OperationId)); Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(plan, (await catalog.PrepareNamespaceTransferAsync(plan)).Plan);
    }

    [TestMethod]
    [DataRow("disable-target")]
    [DataRow("disable-source")]
    [DataRow("configuration")]
    [DataRow("credential-reference")]
    [DataRow("install")]
    public async Task OriginalTransferCannotStartAgainstChangedOrDisabledBackend(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareTransferFixtureAsync(catalog); var plan = setup.Plan; await catalog.PrepareNamespaceTransferAsync(plan);
        var topology = await catalog.ReadAdapterTopologyAsync();
        var changedInstanceId = changed == "disable-source" ? plan.SourceRoute.InstanceId : plan.TargetRoute.InstanceId;
        var originalInstall = topology.Installations[0];
        InstalledAdapter? newer = changed == "install" ? new(originalInstall.Manifest, InstallId.New(), originalInstall.InstallationDirectory,
            originalInstall.PackageSha256, originalInstall.Source, originalInstall.SourceReference, originalInstall.IsSigned,
            originalInstall.InstalledAt, originalInstall.LifecycleState) : null;
        var instances = topology.Instances.Select(instance => instance.InstanceId != changedInstanceId ? instance : new AdapterInstance(
            instance.AdapterId, newer?.InstallId ?? instance.InstallId, instance.InstanceId, instance.DisplayName,
            changed == "configuration" ? new Dictionary<string, string> { ["endpoint"] = "another-source" } : instance.Configuration,
            changed == "credential-reference" ? ["another-credential-reference"] : instance.CredentialReferences,
            instance.FileCacheDirectory, instance.TransferCacheDirectory, changed is not ("disable-target" or "disable-source"),
            instance.LifecycleState, instance.WorkerSessionId, instance.CreatedAt)).ToArray();
        await catalog.SaveAdapterTopologyAsync(new(newer is null ? topology.Installations : [.. topology.Installations, newer], instances, topology.Roots));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1)));
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.CopyStartedAt);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("source-fence")]
    [DataRow("target-fence")]
    [DataRow("copy-effect")]
    [DataRow("delete-effect")]
    [DataRow("payload")]
    [DataRow("source-index")]
    [DataRow("content-index")]
    [DataRow("orphan-fence")]
    public async Task DamagedTransferCannotDisappearFromStartupOrDispatchFences(string changed)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = (await PrepareTransferFixtureAsync(first)).Plan; await first.PrepareNamespaceTransferAsync(plan);
        }
        string sql = changed switch
        {
            "source-fence" => $"DELETE FROM namespace_transfer_roots WHERE root_id='{plan.SourceRoute.RootId}';",
            "target-fence" => $"DELETE FROM namespace_transfer_roots WHERE root_id='{plan.TargetRoute.RootId}';",
            "copy-effect" => "DELETE FROM namespace_transfer_effects WHERE kind=0;",
            "delete-effect" => "DELETE FROM namespace_transfer_effects WHERE kind=1;",
            "payload" => "UPDATE namespace_transfers SET fingerprint=zeroblob(32);",
            "source-index" => $"UPDATE namespace_transfers SET source_root_id='{RootId.New()}';",
            "content-index" => $"UPDATE namespace_transfers SET content_id='{Guid.NewGuid():D}';",
            _ => $"PRAGMA foreign_keys=OFF; INSERT INTO namespace_transfer_roots(root_id,operation_id) VALUES('{RootId.New()}','{Guid.NewGuid():D}');"
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceDispatchFencedRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespacePendingOperationsAsync(RootId.New()));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransferOwnsBothRootsAndCannotLendItsEffectsToGenericJournalMutations(bool copyEffect)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareTransferFixtureAsync(catalog); var plan = setup.Plan; await catalog.PrepareNamespaceTransferAsync(plan);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(setup.Source.Move));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(Birth(setup.Target, setup.TargetProof) with { RelativePath = "Archive/another.txt" }));
        Guid effect = copyEffect ? plan.CopyOperationId : plan.DeleteOperationId;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareMutationAsync(new(effect, plan.TargetRoute.InstanceId,
            plan.TargetRoute.RootKey, MirrorPulseWorkerChangeKind.Create, "moved.txt", null, false, null, plan.Length, plan.Sha256, MirrorPulseMutationOrigin.Journal)));
        Assert.IsNull(await catalog.ReadMutationAsync(effect));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceTransferAsync(plan with
        { OperationId = Guid.NewGuid(), PrivateContentId = Guid.NewGuid(), CopyOperationId = effect, DeleteOperationId = Guid.NewGuid() }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransferCopyUsesOriginalConditionalUploadAndRetainsAcceptedReceiptAcrossOwners(bool sameInstance)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan; var uploads = new TransferUploads();
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = (await PrepareTransferFixtureAsync(first, sameInstance)).Plan; await first.PrepareNamespaceTransferAsync(plan);
            uploads.OnUpload = async (request, token) =>
            {
                Assert.IsNotNull((await first.ReadNamespaceTransferAsync(plan.OperationId, token))!.CopyStartedAt);
                Assert.AreEqual(plan.TargetRoute.InstanceId, request.InstanceId); Assert.AreEqual(plan.TargetRoute.RootKey, request.RootKey);
                Assert.AreEqual(plan.CopyOperationId, request.OperationId); Assert.AreEqual("moved.txt", request.NormalizedPath);
                Assert.IsNull(request.ExpectedRevision); Assert.IsTrue(request.DestinationMustBeAbsent);
                Assert.IsTrue(request.RequireConditionalTargetSupport);
                string? suppliedHash = request.ExpectedContentSha256;
                Assert.AreEqual(plan.Sha256, suppliedHash); Assert.AreEqual(plan.Length, request.Length);
                using var target = new MemoryStream(); await request.Content.CopyToAsync(target, token);
                CollectionAssert.AreEqual(TransferBytes, target.ToArray()); return "original-target-revision";
            };
            using var content = new MemoryStream(TransferBytes, writable: false);
            var result = await new MirrorPulseTransferCopyExecutor(first, uploads, new TransferClock(plan.PreparedAt.AddSeconds(1))).CopyAsync(plan.OperationId, content);
            Assert.AreEqual("original-target-revision", result.AcceptedRevision);
            Assert.HasCount(2, await first.ReadNamespaceDispatchFencedRootsAsync());
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        using var unused = new MemoryStream();
        var replay = await new MirrorPulseTransferCopyExecutor(second, uploads).CopyAsync(plan.OperationId, unused);
        Assert.AreEqual("original-target-revision", replay.AcceptedRevision); Assert.AreEqual(1, uploads.Calls);
        Assert.AreEqual(plan.Source.Context.Member, await second.ReadNamespaceMemberAsync(plan.Source.Context.Member.MemberId));
        Assert.IsNull(await second.ReadMutationAsync(plan.DeleteOperationId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LostOrCanceledTransferReceiptNeverReuploadsOrReleasesTheSource(bool canceled)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan; var uploads = new TransferUploads();
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = (await PrepareTransferFixtureAsync(first)).Plan; await first.PrepareNamespaceTransferAsync(plan);
            uploads.OnUpload = (_, _) => canceled ? ValueTask.FromException<string>(new OperationCanceledException()) :
                ValueTask.FromException<string>(new IOException("The target accepted the bytes but its reply was lost."));
            using var content = new MemoryStream(TransferBytes, writable: false);
            try { await new MirrorPulseTransferCopyExecutor(first, uploads, new TransferClock(plan.PreparedAt.AddSeconds(1))).CopyAsync(plan.OperationId, content); Assert.Fail(); }
            catch (Exception error) when (error is IOException or OperationCanceledException) { }
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        using var original = new MemoryStream(TransferBytes, writable: false);
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => new MirrorPulseTransferCopyExecutor(second, uploads).CopyAsync(plan.OperationId, original));
        Assert.AreEqual(1, uploads.Calls); Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNull((await second.ReadNamespaceTransferAsync(plan.OperationId))!.AcceptedRevision);
        Assert.IsNull(await second.ReadMutationAsync(plan.DeleteOperationId));
    }

    [TestMethod]
    [DataRow("hash")]
    [DataRow("length")]
    [DataRow("writable")]
    public async Task InvalidPrivateTransferBytesCannotAcquireAStartOrInvokeTheWorker(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan; await catalog.PrepareNamespaceTransferAsync(plan);
        var uploads = new TransferUploads(); byte[] data = TransferBytes.ToArray();
        if (changed == "hash") data[0] ^= 1;
        if (changed == "length") data = [.. data, 0];
        using var content = new MemoryStream(data, writable: changed == "writable");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new MirrorPulseTransferCopyExecutor(catalog, uploads).CopyAsync(plan.OperationId, content));
        Assert.AreEqual(0, uploads.Calls); Assert.IsNull((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.CopyStartedAt);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransferCannotAdoptAnEarlierGenericOrNamespaceOwnerAsItsCopyEffect(bool generic)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareTransferFixtureAsync(catalog); var plan = setup.Plan;
        Guid original;
        if (generic)
        {
            original = Guid.NewGuid();
            await catalog.PrepareMutationAsync(new(original, plan.SourceRoute.InstanceId, plan.SourceRoute.RootKey,
                MirrorPulseWorkerChangeKind.ContentUpdate, "new.txt", null, false, "source-v1", plan.Length, plan.Sha256, MirrorPulseMutationOrigin.Journal));
        }
        else original = setup.Source.Born.Birth.OperationId;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceTransferAsync(plan with { CopyOperationId = original }));
        Assert.IsNull(await catalog.ReadNamespaceTransferAsync(plan.OperationId)); Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task ConcurrentTransferCopyCannotInvokeTheSameTargetTwice()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan; await catalog.PrepareNamespaceTransferAsync(plan);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var uploads = new TransferUploads { OnUpload = async (_, token) => { entered.TrySetResult(); await release.Task.WaitAsync(token); return "accepted"; } };
        var executor = new MirrorPulseTransferCopyExecutor(catalog, uploads, new TransferClock(plan.PreparedAt.AddSeconds(1)));
        using var firstContent = new MemoryStream(TransferBytes, writable: false); using var secondContent = new MemoryStream(TransferBytes, writable: false);
        var first = executor.CopyAsync(plan.OperationId, firstContent);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => executor.CopyAsync(plan.OperationId, secondContent));
            Assert.AreEqual(1, uploads.Calls);
        }
        finally { release.TrySetResult(); await first; }
        Assert.AreEqual("accepted", (await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.AcceptedRevision);
    }

    [TestMethod]
    public async Task CancellationAfterSuccessfulTargetReplyStillPersistsItsOriginalReceipt()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan; await catalog.PrepareNamespaceTransferAsync(plan);
        using var cancellation = new CancellationTokenSource();
        var uploads = new TransferUploads { OnUpload = (_, _) => { cancellation.Cancel(); return ValueTask.FromResult("accepted-before-cancellation"); } };
        using var content = new MemoryStream(TransferBytes, writable: false);
        var result = await new MirrorPulseTransferCopyExecutor(catalog, uploads, new TransferClock(plan.PreparedAt.AddSeconds(1))).CopyAsync(plan.OperationId, content, cancellation.Token);
        Assert.AreEqual("accepted-before-cancellation", result.AcceptedRevision);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync()); Assert.IsNull(await catalog.ReadMutationAsync(plan.DeleteOperationId));
    }

    [TestMethod]
    public async Task ReceiptPersistenceFailureRetainsTheStartedTransferWithoutBlindCopyRecovery()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan; var uploads = new TransferUploads();
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = (await PrepareTransferFixtureAsync(first)).Plan; await first.PrepareNamespaceTransferAsync(plan);
            uploads.OnUpload = async (_, _) =>
            {
                await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_transfer_receipt BEFORE UPDATE ON namespace_transfers BEGIN SELECT RAISE(ABORT,'synthetic-receipt-gap'); END;");
                return "accepted-but-not-durable";
            };
            using var content = new MemoryStream(TransferBytes, writable: false);
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() =>
                new MirrorPulseTransferCopyExecutor(first, uploads, new TransferClock(plan.PreparedAt.AddSeconds(1))).CopyAsync(plan.OperationId, content));
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_transfer_receipt;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        using var retained = new MemoryStream(TransferBytes, writable: false);
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => new MirrorPulseTransferCopyExecutor(second, uploads).CopyAsync(plan.OperationId, retained));
        Assert.AreEqual(1, uploads.Calls); Assert.IsNull((await second.ReadNamespaceTransferAsync(plan.OperationId))!.CopyAcceptedAt);
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task TransferCopyRejectsUnprovenLegacyTargetBeforeTakingEitherRoot()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan; var topology = await catalog.ReadAdapterTopologyAsync();
        var target = topology.Roots.Single(root => root.RootId == plan.TargetRoute.RootId);
        var legacy = new RootRegistration(target.AdapterId, target.InstanceId, target.RootId, target.UniquenessKey,
            target.Label, target.DirectoryName, target.CustomEntry, target.State, target.RegisteredAt, RootIdentityScope.LegacyInstance);
        await catalog.SaveAdapterTopologyAsync(topology with { Roots = topology.Roots.Select(root => root.RootId == target.RootId ? legacy : root).ToArray() });
        var instance = topology.Instances.Single(item => item.InstanceId == legacy.InstanceId);
        var unproven = plan with { TargetRoute = MirrorPulseTransferRoute.Capture(instance, legacy, 1) };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceTransferAsync(unproven));
        Assert.IsNull(await catalog.ReadNamespaceTransferAsync(plan.OperationId)); Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task ConditionalTransferCannotSendAnyUploadFrameToAnActualLegacyChannel()
    {
        var instance = InstanceId.New(); var session = WorkerSessionId.New(); using var pipe = new MemoryStream();
        var channel = new AdapterWorkerReadRangeClient(pipe, instance, session); var client = new AdapterWorkerUploadClient(channel, instance, session);
        using var content = new MemoryStream(TransferBytes, writable: false); using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var request = new MirrorPulseWorkerUploadRequest(instance, "target", null, content, content.Length, Guid.NewGuid(),
            Convert.ToHexString(SHA256.HashData(TransferBytes)), "legacy")
        { RequireConditionalTargetSupport = true };
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => client.UploadAsync(request, cancellation.Token).AsTask());
        Assert.AreEqual(0L, pipe.Length); Assert.AreEqual(0L, content.Position);
    }

    [TestMethod]
    [DataRow("roots")]
    [DataRow("effects")]
    [DataRow("states")]
    [DataRow("all")]
    public async Task CurrentSchemaDoesNotRecreateMissingTransferOwnershipTables(string missing)
    {
        using var fixture = new Fixture();
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await first.PrepareNamespaceTransferAsync((await PrepareTransferFixtureAsync(first)).Plan);
        string sql = missing switch
        {
            "roots" => "DROP TABLE namespace_transfer_roots;",
            "effects" => "DROP TABLE namespace_transfer_effects;",
            "states" => "DROP TABLE namespace_transfer_states;",
            _ => "DROP TABLE namespace_transfer_states; DROP TABLE namespace_transfer_effects; DROP TABLE namespace_transfer_roots; DROP TABLE namespace_transfers;"
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
    }

    [TestMethod]
    [DataRow("source-context")]
    [DataRow("target-identity")]
    [DataRow("source-location")]
    public async Task MalformedTransferSnapshotsDoNotAcquireEitherRoot(string missing)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan;
        var malformed = missing switch
        {
            "source-context" => plan with { Source = plan.Source with { Context = null! } },
            "target-identity" => plan with { TargetParent = plan.TargetParent with { Identity = null! } },
            _ => plan with { Source = plan.Source with { Context = plan.Source.Context with { Location = null! } } }
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceTransferAsync(malformed));
        Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task Schema79UpgradePreservesOriginalMembersAndInventsNoTransfer()
    {
        using var fixture = new Fixture(); string before;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await PrepareMovableMemberAsync(first); before = await ReadMemberMigrationBytesAsync(fixture.Paths);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_transfer_effects; DROP TABLE namespace_transfer_roots; DROP TABLE namespace_transfers; PRAGMA user_version=79;");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(before, await ReadMemberMigrationBytesAsync(fixture.Paths));
        Assert.IsNull(await reopened.ReadNamespaceTransferAsync(Guid.NewGuid())); Assert.IsEmpty(await reopened.ReadNamespaceDispatchFencedRootsAsync());
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var version = connection.CreateCommand(); version.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(90L, await version.ExecuteScalarAsync());
    }

    private static readonly byte[] TransferBytes = Enumerable.Range(0, 131_089).Select(value => (byte)(value * 37)).ToArray();
    private sealed record TransferFixture(MoveFixture Source, MirrorPulseNamespacePermissionBaseline Target,
        MirrorPulseNamespacePermissionIntent TargetProof, MirrorPulseNamespaceTransferPlan Plan);

    private static async Task<TransferFixture> PrepareTransferFixtureAsync(MirrorPulseProductCatalog catalog, bool sameInstance = false)
    {
        var source = await PrepareMovableMemberAsync(catalog);
        var target = source.Parent with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = RootId.New(),
            RelativePath = "Archive",
            LocalObject = source.Parent.LocalObject with { LocalFileId = Guid.NewGuid() }
        };
        var proof = Protection(target); await ProtectAsync(catalog, target, proof);
        var targetMember = OriginalMember(target, proof, source.ParentMember.ParentMemberId); await catalog.RegisterNamespaceMemberAsync(targetMember);
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.transfer"), "Example", "1.0.0", new ProtocolVersionRange(1, 2),
            new Dictionary<string, string> { ["win-x64"] = "worker/adapter.exe", ["win-arm64"] = "worker/adapter.exe" },
            new AdapterInstallPolicy(null), new AdapterInstancePolicy(null, null), new AdapterCapabilities(true, false, true, true), ["en-US"], "1.0.0");
        var installed = new InstalledAdapter(manifest, InstallId.New(), "C:\\synthetic-transfer\\installed", new Sha256Digest(new string('A', 64)),
            AdapterInstallSource.LocalFile, null, true, DateTimeOffset.UtcNow, AdapterLifecycleState.Installed);
        AdapterInstance Instance() => new(manifest.AdapterId, installed.InstallId, InstanceId.New(), "Storage",
            new Dictionary<string, string> { ["endpoint"] = "synthetic" }, [], "C:\\synthetic-transfer\\files", "C:\\synthetic-transfer\\transfers",
            true, AdapterLifecycleState.Installed, null, DateTimeOffset.UtcNow);
        var sourceInstance = Instance(); var targetInstance = sameInstance ? sourceInstance : Instance();
        var sourceRoot = new RootRegistration(manifest.AdapterId, sourceInstance.InstanceId, source.Parent.RootId!.Value,
            "source", "Docs", "Docs", false, RootRegistrationState.Active, DateTimeOffset.UtcNow, sameInstance ? RootIdentityScope.InstanceRoot : RootIdentityScope.LegacyInstance);
        var targetRoot = new RootRegistration(manifest.AdapterId, targetInstance.InstanceId, target.RootId!.Value,
            "target", "Archive", "Archive", false, RootRegistrationState.Active, DateTimeOffset.UtcNow, RootIdentityScope.InstanceRoot);
        await catalog.SaveAdapterTopologyAsync(new([installed], sameInstance ? [sourceInstance] : [sourceInstance, targetInstance], [sourceRoot, targetRoot]));
        var child = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(source.Member.MemberId))!;
        var parent = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(source.ParentMember.MemberId))!;
        var destination = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(targetMember.MemberId))!;
        var prepared = new[] { DateTimeOffset.UtcNow, child.Context.Verification.ObservedAt, parent.Context.Verification.ObservedAt,
            destination.Context.Verification.ObservedAt, child.Context.Member.RegisteredAt, targetMember.RegisteredAt }.Max().AddSeconds(1);
        var plan = new MirrorPulseNamespaceTransferPlan(1, Guid.NewGuid(), MirrorPulseTransferRoute.Capture(sourceInstance, sourceRoot, sameInstance ? 2 : 1),
            MirrorPulseTransferRoute.Capture(targetInstance, targetRoot, 2), child, parent, destination, "Archive/moved.txt", "source-v1",
            Guid.NewGuid(), TransferBytes.LongLength, Convert.ToHexString(SHA256.HashData(TransferBytes)), Guid.NewGuid(), Guid.NewGuid(), prepared);
        return new(source, target, proof, plan);
    }

    private sealed class TransferClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TransferUploads : IMirrorPulseWorkerUploadTransport
    {
        internal int Calls;
        internal Func<MirrorPulseWorkerUploadRequest, CancellationToken, ValueTask<string>> OnUpload = (_, _) => ValueTask.FromResult("accepted");
        public ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken cancellationToken)
        { Calls++; return OnUpload(request, cancellationToken); }
    }
}
