using System.Security.Cryptography;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, 0)]
    [DataRow(false, 131_089)]
    [DataRow(true, 1_048_583)]
    public async Task TargetWholeReadbackRetainsItsOriginalAcceptedIdentityWithoutRetiringSource(bool sameInstance, int length)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan; MirrorPulseNamespaceTransferCopy verified;
        byte[] bytes = Enumerable.Range(0, length).Select(value => (byte)(value * 37)).ToArray();
        var target = new TransferTarget(bytes);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareAcceptedTransferTargetAsync(first, bytes, sameInstance); target.Plan = plan;
            verified = await new MirrorPulseTransferTargetVerifier(first, target, target, target,
                new TransferClock(plan.PreparedAt.AddSeconds(3))).VerifyAsync(plan.OperationId);
            Assert.IsNotNull(verified.TargetVerification); Assert.AreEqual("original-target-id", verified.TargetVerification.RemoteId);
            Assert.AreEqual(plan.Sha256, verified.TargetVerification.Sha256); Assert.AreEqual(plan.Length, verified.TargetVerification.Length);
            Assert.AreEqual(2, target.Stats); Assert.AreEqual(2, target.Pages);
            Assert.AreEqual((length + 1_048_575) / 1_048_576, target.Reads);
            Assert.HasCount(2, await first.ReadNamespaceDispatchFencedRootsAsync());
            Assert.IsNull(await first.ReadMutationAsync(plan.DeleteOperationId));
            Assert.IsNull(await first.ReadNamespaceMemberChildAsync(plan.TargetParent.Context.Member.MemberId, "moved.txt"));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(verified, await second.ReadNamespaceTransferAsync(plan.OperationId));
        Assert.AreEqual(verified, await new MirrorPulseTransferTargetVerifier(second, target, target, target).VerifyAsync(plan.OperationId));
        Assert.AreEqual(2, target.Stats); Assert.AreEqual(2, target.Pages);
        Assert.AreEqual(plan.Source.Context.Member, await second.ReadNamespaceMemberChildAsync(plan.SourceParent.Context.Member.MemberId, "new.txt"));
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.RecordNamespaceTransferTargetVerificationAsync(
            verified.TargetVerification! with { RemoteId = "another-target" }));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("stat-before")]
    [DataRow("stat-after")]
    [DataRow("length")]
    [DataRow("revision")]
    [DataRow("remote-id")]
    [DataRow("type")]
    [DataRow("deleted")]
    [DataRow("absent")]
    [DataRow("duplicate")]
    [DataRow("duplicate-later-page")]
    [DataRow("cursor-cycle")]
    [DataRow("missing-cursor")]
    [DataRow("page-limit")]
    [DataRow("oversized-page")]
    [DataRow("short-read")]
    [DataRow("extra-read")]
    [DataRow("changed-bytes")]
    [DataRow("changed-id")]
    public async Task TargetReadbackMismatchRetainsTheSourceAndBothOriginalFences(string failure)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await PrepareAcceptedTransferTargetAsync(catalog, TransferBytes); var target = new TransferTarget(TransferBytes) { Plan = plan, Failure = failure };
        try
        {
            await new MirrorPulseTransferTargetVerifier(catalog, target, target, target,
                new TransferClock(plan.PreparedAt.AddSeconds(3))).VerifyAsync(plan.OperationId);
            Assert.Fail("Invalid target readback was accepted.");
        }
        catch (Exception error) when (error is IOException or InvalidDataException) { }
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.TargetVerification);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync()); Assert.IsNull(await catalog.ReadMutationAsync(plan.DeleteOperationId));
        Assert.AreEqual(plan.Source.Context.Member, await catalog.ReadNamespaceMemberAsync(plan.Source.Context.Member.MemberId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MatchingTargetCannotCreateTheOriginalMissingUploadAcceptance(bool started)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = (await PrepareTransferFixtureAsync(catalog)).Plan; await catalog.PrepareNamespaceTransferAsync(plan);
        if (started) await catalog.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1));
        var target = new TransferTarget(TransferBytes) { Plan = plan };
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() =>
            new MirrorPulseTransferTargetVerifier(catalog, target, target, target).VerifyAsync(plan.OperationId));
        Assert.AreEqual(0, target.Stats); Assert.AreEqual(0, target.Pages); Assert.AreEqual(0, target.Reads);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordNamespaceTransferTargetVerificationAsync(
            new(1, plan.OperationId, "accepted-target-v1", "original-target-id", plan.Length, plan.Sha256, plan.PreparedAt.AddSeconds(3))));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("disabled")]
    [DataRow("changed-scope")]
    public async Task TargetReadbackCannotAccessChangedOrDisabledBackend(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await PrepareAcceptedTransferTargetAsync(catalog, TransferBytes); var topology = await catalog.ReadAdapterTopologyAsync();
        await catalog.SaveAdapterTopologyAsync(topology with
        {
            Instances = topology.Instances.Select(instance => instance.InstanceId != plan.TargetRoute.InstanceId ? instance : new AdapterInstance(
                instance.AdapterId, instance.InstallId, instance.InstanceId, instance.DisplayName,
                change == "changed-scope" ? new Dictionary<string, string> { ["endpoint"] = "another-backend" } : instance.Configuration,
                instance.CredentialReferences, instance.FileCacheDirectory, instance.TransferCacheDirectory, change != "disabled",
                instance.LifecycleState, instance.WorkerSessionId, instance.CreatedAt)).ToArray()
        });
        var target = new TransferTarget(TransferBytes) { Plan = plan };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => new MirrorPulseTransferTargetVerifier(catalog, target, target, target).VerifyAsync(plan.OperationId));
        Assert.AreEqual(0, target.Stats); Assert.AreEqual(0, target.Reads); Assert.AreEqual(0, target.Pages);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task CanceledTargetReadbackRetainsOriginalAcceptanceWithoutFabricatingVerification()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await PrepareAcceptedTransferTargetAsync(catalog, TransferBytes);
        using var cancellation = new CancellationTokenSource();
        var target = new TransferTarget(TransferBytes) { Plan = plan, CancelDuringRead = cancellation };
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => new MirrorPulseTransferTargetVerifier(catalog, target, target, target,
            new TransferClock(plan.PreparedAt.AddSeconds(3))).VerifyAsync(plan.OperationId, cancellation.Token));
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.TargetVerification);
        Assert.AreEqual("accepted-target-v1", (await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.AcceptedRevision);
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsNull(await catalog.ReadMutationAsync(plan.DeleteOperationId));
    }

    [TestMethod]
    public async Task TargetVerificationWriteFailureCanRepeatReadbackWithoutAnotherUploadOrSourceDeletion()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferPlan plan;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareAcceptedTransferTargetAsync(first, TransferBytes); var target = new TransferTarget(TransferBytes) { Plan = plan };
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_target_receipt BEFORE UPDATE ON namespace_transfers BEGIN SELECT RAISE(ABORT,'synthetic-target-verification-gap'); END;");
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => new MirrorPulseTransferTargetVerifier(first, target, target, target,
                new TransferClock(plan.PreparedAt.AddSeconds(3))).VerifyAsync(plan.OperationId));
            Assert.IsNull((await first.ReadNamespaceTransferAsync(plan.OperationId))!.TargetVerification);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_target_receipt;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var repeated = new TransferTarget(TransferBytes) { Plan = plan };
        Assert.IsNotNull((await new MirrorPulseTransferTargetVerifier(second, repeated, repeated, repeated,
            new TransferClock(plan.PreparedAt.AddSeconds(4))).VerifyAsync(plan.OperationId)).TargetVerification);
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync()); Assert.IsNull(await second.ReadMutationAsync(plan.DeleteOperationId));
    }

    [TestMethod]
    [DataRow("revision")]
    [DataRow("length")]
    [DataRow("hash")]
    [DataRow("chronology")]
    public async Task TargetVerificationMustRetainItsOriginalAcceptedReceiptAndWholeByteProof(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await PrepareAcceptedTransferTargetAsync(catalog, TransferBytes);
        var original = new MirrorPulseTransferTargetVerification(1, plan.OperationId, "accepted-target-v1", "original-target-id",
            plan.Length, plan.Sha256, plan.PreparedAt.AddSeconds(3));
        var invalid = changed switch
        {
            "revision" => original with { AcceptedRevision = "another-accepted-version" },
            "length" => original with { Length = original.Length + 1 },
            "hash" => original with { Sha256 = new string('F', 64) },
            _ => original with { VerifiedAt = plan.PreparedAt }
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RecordNamespaceTransferTargetVerificationAsync(invalid));
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(plan.OperationId))!.TargetVerification);
    }

    private static async Task<MirrorPulseNamespaceTransferPlan> PrepareAcceptedTransferTargetAsync(MirrorPulseProductCatalog catalog,
        byte[] bytes, bool sameInstance = false, bool localOnlySource = false, bool captureOriginalJournal = false, bool emptyJournal = false,
        MirrorPulseNamespaceTransferPlan? originalPlan = null)
    {
        var original = originalPlan ?? (await PrepareTransferFixtureAsync(catalog, sameInstance)).Plan;
        var plan = original with
        { Length = bytes.LongLength, Sha256 = Convert.ToHexString(SHA256.HashData(bytes)), SourceRemoteRevision = originalPlan is null ? localOnlySource ? null : "source-v1" : original.SourceRemoteRevision };
        if (captureOriginalJournal) plan = WithTransferCommandOrigin(plan with { SourceRoute = plan.SourceRoute with { ProtocolVersion = 2 } });
        await catalog.PrepareNamespaceTransferAsync(plan);
        if (captureOriginalJournal)
        {
            var claim = TransferJournalClaim(plan) with { CapturedAt = plan.PreparedAt.AddMilliseconds(500) };
            await catalog.PrepareNamespaceTransferJournalClaimAsync(emptyJournal ? claim with { Members = [] } : claim);
        }
        await catalog.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1));
        // The original successful Worker upload is the premise; the production verifier and durable readback are tested here.
        await catalog.RecordNamespaceTransferCopyAcceptedAsync(plan.OperationId, "accepted-target-v1", plan.PreparedAt.AddSeconds(2));
        return plan;
    }

    private sealed class TransferTarget(byte[] bytes) : IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerDirectoryPageSource
    {
        internal MirrorPulseNamespaceTransferPlan Plan = null!;
        internal string Failure = string.Empty;
        internal int Stats, Pages, Reads;
        internal CancellationTokenSource? CancelDuringRead;
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken)
        {
            Stats++; Assert.AreEqual(Plan.TargetRoute.InstanceId, request.InstanceId); Assert.AreEqual(Plan.TargetRoute.RootKey, request.RootKey);
            Assert.AreEqual("moved.txt", request.NormalizedPath);
            return ValueTask.FromResult<string?>(Failure == "stat-before" || Failure == "stat-after" && Stats > 1 ? "another-version" : "accepted-target-v1");
        }

        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken cancellationToken)
        {
            Pages++; Assert.AreEqual(Plan.TargetRoute.InstanceId, request.InstanceId); Assert.AreEqual(Plan.TargetRoute.RootKey, request.RootKey);
            Assert.AreEqual(string.Empty, request.NormalizedPath); Assert.AreEqual(512, request.PageSize);
            var entry = new MirrorPulseWorkerDirectoryEntry(Failure == "remote-id" ? "" : Failure == "replaced-id" || Failure == "changed-id" && Pages > 1 ? "another-target-id" : "original-target-id",
                Failure == "revision" ? "another-version" : "accepted-target-v1", Failure == "type" ? "Directory" : "File", "moved.txt",
                Failure == "length" ? bytes.LongLength + 1 : bytes.LongLength, null, null, Failure == "deleted");
            MirrorPulseWorkerDirectoryEntry[] entries = Failure switch
            {
                "absent" => [],
                "duplicate" => [entry, entry],
                "oversized-page" => Enumerable.Repeat(entry, 513).ToArray(),
                "cursor-cycle" or "missing-cursor" or "page-limit" => [],
                _ => [entry]
            };
            bool complete = Failure is not ("cursor-cycle" or "missing-cursor" or "page-limit" or "duplicate-later-page") || Failure == "duplicate-later-page" && Pages > 1;
            ReadOnlyMemory<byte> next = complete || Failure == "missing-cursor" ? ReadOnlyMemory<byte>.Empty :
                Failure == "page-limit" ? BitConverter.GetBytes(Pages) : new byte[] { 1 };
            return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(entries, next, complete));
        }

        public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
        {
            Reads++; Assert.AreEqual(Plan.TargetRoute.InstanceId, request.InstanceId); Assert.AreEqual(Plan.TargetRoute.RootKey, request.RootKey);
            Assert.AreEqual("moved.txt", request.NormalizedPath); Assert.AreEqual("accepted-target-v1", request.ExpectedRevision);
            Assert.IsTrue(request.Length is >= 1 and <= 1_048_576);
            if (CancelDuringRead is { } cancel)
            {
                cancel.Cancel(); return ValueTask.FromException<Stream>(new OperationCanceledException(cancellationToken));
            }
            byte[] content = bytes.AsSpan((int)request.Offset, (int)request.Length).ToArray();
            if (Failure == "short-read") content = content[..^1];
            if (Failure == "extra-read") content = [.. content, 0];
            if (Failure == "changed-bytes") content[0] ^= 1;
            return ValueTask.FromResult<Stream>(new MemoryStream(content, writable: false));
        }
    }
}
