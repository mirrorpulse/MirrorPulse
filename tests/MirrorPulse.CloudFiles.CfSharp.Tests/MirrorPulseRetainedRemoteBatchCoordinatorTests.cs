using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.19041")]
public sealed class MirrorPulseRetainedRemoteBatchCoordinatorTests
{
    [TestMethod]
    [DataRow("before-apply")]
    [DataRow("after-progress")]
    [DataRow("after-cursor")]
    public async Task ReopenedOwnerReplaysTheOriginalEnvelopeBeforeALaterIncomingBatch(string fault)
    {
        await using var fixture = await Fixture.CreateAsync();
        var original = fixture.Batch("original"); var later = fixture.Batch("later", initial: [1], final: [2]);
        bool fail = true;
        async ValueTask<MirrorPulseRemotePollApplyOutcome> Apply(InstanceId _, CloudRemoteChangeBatch batch, CloudRemoteApplyOptions policy, CancellationToken token)
        {
            fixture.Calls.Add(batch);
            Assert.IsNotNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None), "The original envelope must commit before SDK access.");
            if (fail && fault == "before-apply") throw new IOException("Before public SDK apply.");
            await fixture.CommitProgressAsync(batch, cursor: !(fail && fault == "after-progress"));
            if (fail) throw new IOException("After a durable SDK boundary.");
            return new(true, batch.FinalCursor);
        }
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Coordinator(Apply).ApplyAsync(fixture.Instance, original, fixture.Policy, default).AsTask());
        var retained = (await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None))!;
        await fixture.ReopenAsync(); fail = false;
        var result = await fixture.Coordinator(Apply).ApplyAsync(fixture.Instance, later, fixture.Policy, default);
        Assert.IsTrue(result.Completed); Assert.HasCount(3, fixture.Calls);
        Assert.AreEqual(original.BatchId, fixture.Calls[1].BatchId); Assert.AreEqual(later.BatchId, fixture.Calls[2].BatchId);
        CollectionAssert.AreEqual(original.Fingerprint.ToArray(), fixture.Calls[1].Fingerprint.ToArray());
        Assert.IsNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None));
        Assert.IsTrue(await fixture.Coordinator(Apply).ResumeAsync(fixture.Instance, default)); Assert.HasCount(3, fixture.Calls);
        Assert.IsNotEmpty(retained.Payload);
    }

    [TestMethod]
    public async Task EveryPublicChangeFieldAndApplyPolicyRoundTripsWithoutAnSdkPrivateCodec()
    {
        await using var fixture = await Fixture.CreateAsync();
        var metadata = CloudPlaceholderMetadata.CreateFileBuilder().WithAttributes(FileAttributes.Archive)
            .WithCreationTime(DateTimeOffset.UnixEpoch).WithLastAccessTime(DateTimeOffset.UnixEpoch.AddDays(1))
            .WithLastWriteTime(DateTimeOffset.UnixEpoch.AddDays(2)).WithChangeTime(DateTimeOffset.UnixEpoch.AddDays(3)).Build();
        var directory = CloudPlaceholderMetadata.CreateDirectoryBuilder().WithAttributes(FileAttributes.Directory).Build();
        string prefix = fixture.Root.DirectoryName + "\\";
        var batch = new CloudRemoteChangeBatch(fixture.Instance + "/all-fields", new byte[] { 0, 255 },
            [new("create", CloudRemoteChangeKind.FileUpsert, "opaque\nremote", "revision/1", CloudItemKind.File, prefix + "file.bin", Guid.NewGuid(),
                null, null, 123, metadata, new byte[] { 4 }),
             new("directory", CloudRemoteChangeKind.DirectoryUpsert, "dir", "v1", CloudItemKind.Directory, prefix + "Dir", Guid.NewGuid(),
                metadata: directory, cursorAfter: new byte[] { 5 }),
             new("metadata", CloudRemoteChangeKind.MetadataUpdate, "file", "v2", CloudItemKind.File, prefix + "file2.bin", Guid.NewGuid(), "v1",
                metadata: metadata, cursorAfter: new byte[] { 6 }),
             new("move", CloudRemoteChangeKind.Move, "file3", "v3", CloudItemKind.File, prefix + "target.bin", Guid.NewGuid(), "v2", prefix + "source.bin",
                321, metadata, new byte[] { 7 }),
             new("delete", CloudRemoteChangeKind.Delete, "dir2", "v4", CloudItemKind.Directory, prefix + "Retired", Guid.NewGuid(), "v3", cursorAfter: new byte[] { 8 })],
            new byte[] { 9 });
        var policy = new CloudRemoteApplyOptions
        {
            MaximumEntries = 2,
            PreserveUnsynchronizedLocalContent = false,
            SuppressLocalEcho = false,
            EchoSuppressionLifetime = TimeSpan.FromSeconds(17),
            EchoSuppressionObservationCount = 3
        };
        await Assert.ThrowsExactlyAsync<IOException>(() => fixture.Coordinator((_, _, _, _) =>
            ValueTask.FromException<MirrorPulseRemotePollApplyOutcome>(new IOException("Retained first.")))
            .ApplyAsync(fixture.Instance, batch, policy, default).AsTask());
        byte[] originalPayload = (await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None))!.Payload;
        await fixture.ReopenAsync();
        Assert.IsTrue(await fixture.Coordinator(async (_, replay, options, _) =>
        {
            CollectionAssert.AreEqual(batch.Fingerprint.ToArray(), replay.Fingerprint.ToArray());
            Assert.AreEqual(policy.MaximumEntries, options.MaximumEntries); Assert.IsFalse(options.PreserveUnsynchronizedLocalContent);
            Assert.IsFalse(options.SuppressLocalEcho); Assert.AreEqual(policy.EchoSuppressionLifetime, options.EchoSuppressionLifetime);
            Assert.AreEqual(3, options.EchoSuppressionObservationCount); Assert.IsNull(options.ConflictResolver);
            CollectionAssert.AreEqual(originalPayload, (await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None))!.Payload);
            await fixture.CommitProgressAsync(replay); return new(true, replay.FinalCursor);
        }).ResumeAsync(fixture.Instance, default));
        Assert.IsFalse(Directory.EnumerateFileSystemEntries(fixture.Paths.SyncRootPath).Any());
    }

    [TestMethod]
    [DataRow("batch")]
    [DataRow("maximum")]
    [DataRow("preserve")]
    [DataRow("suppress")]
    [DataRow("lifetime")]
    [DataRow("observations")]
    public async Task SameIdReplayCannotChangeItsBatchOrOriginalPublicPolicy(string fault)
    {
        await using var fixture = await Fixture.CreateAsync(); var original = fixture.Batch("original");
        await fixture.RetainByFailureAsync(original);
        var changed = fault == "batch" ? new CloudRemoteChangeBatch(original.BatchId, original.InitialCursor, original.Changes, new byte[] { 9 }) : original;
        var policy = fault switch
        {
            "maximum" => fixture.Policy with { MaximumEntries = 2 },
            "preserve" => fixture.Policy with { PreserveUnsynchronizedLocalContent = false },
            "suppress" => fixture.Policy with { SuppressLocalEcho = false },
            "lifetime" => fixture.Policy with { EchoSuppressionLifetime = TimeSpan.FromSeconds(17) },
            "observations" => fixture.Policy with { EchoSuppressionObservationCount = 3 },
            _ => fixture.Policy,
        };
        int calls = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Coordinator((_, _, _, _) =>
        { calls++; throw new InvalidOperationException("Do not reach SDK."); }).ApplyAsync(fixture.Instance, changed, policy, default).AsTask());
        Assert.AreEqual(0, calls); Assert.AreEqual(original.BatchId, (await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None))!.BatchId);
    }

    [TestMethod]
    [DataRow("missing-progress")]
    [DataRow("partial")]
    [DataRow("fingerprint")]
    [DataRow("last-change")]
    [DataRow("missing-cursor")]
    [DataRow("cursor")]
    [DataRow("returned-cursor")]
    public async Task CompletionMustReadBackTheActualOriginalProgressAndPublishedCursor(string fault)
    {
        await using var fixture = await Fixture.CreateAsync(); var batch = fixture.Batch("original");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Coordinator(async (_, replay, _, _) =>
        {
            await fixture.CommitProgressAsync(replay, fault: fault);
            return new(true, fault == "returned-cursor" ? new byte[] { 9 } : replay.FinalCursor);
        }).ApplyAsync(fixture.Instance, batch, fixture.Policy, default).AsTask());
        Assert.AreEqual(batch.BatchId, (await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None))!.BatchId);
    }

    [TestMethod]
    public async Task IncompleteOriginalCannotBeOvertakenByIncomingOrRootPollingWork()
    {
        await using var fixture = await Fixture.CreateAsync(); var batch = fixture.Batch("original");
        await fixture.RetainByFailureAsync(batch);
        var coordinator = fixture.Coordinator((_, replay, _, _) =>
        { fixture.Calls.Add(replay); return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(false, replay.InitialCursor)); });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(fixture.Instance,
            fixture.Batch("later"), fixture.Policy, default).AsTask());
        byte[] start = new byte[32], finish = Enumerable.Repeat((byte)1, 32).ToArray();
        var poll = new CloudRemoteChangeBatch(MirrorPulseRemotePollBatchScope.Prefix(fixture.Root) + Convert.ToHexString(start) + "/" + Convert.ToHexString(finish), start, [], finish);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(fixture.Instance, poll, fixture.Policy, default).AsTask());
        Assert.HasCount(2, fixture.Calls); Assert.IsTrue(fixture.Calls.All(value => value.BatchId == batch.BatchId));
        Assert.AreEqual(batch.BatchId, (await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None))!.BatchId);
    }

    [TestMethod]
    [DataRow("disabled")]
    [DataRow("label")]
    [DataRow("source-key")]
    [DataRow("identity-policy")]
    public async Task ChangedOrDisabledRootCannotRetargetTheRetainedEnvelope(string fault)
    {
        await using var fixture = await Fixture.CreateAsync(); await fixture.RetainByFailureAsync(fixture.Batch("original"));
        var root = fixture.Root;
        fixture.Router.ReplaceRegistrations([new(root.AdapterId, root.InstanceId, root.RootId,
            fault == "source-key" ? "new-source" : root.UniquenessKey, fault == "label" ? "Renamed" : root.Label,
            fault == "label" ? "Renamed" : root.DirectoryName, root.CustomEntry,
            fault == "disabled" ? RootRegistrationState.Disabled : RootRegistrationState.Active, root.RegisteredAt,
            fault == "identity-policy" ? RootIdentityScope.LegacyInstance : RootIdentityScope.InstanceRoot)]);
        int calls = 0;
        try { await fixture.Coordinator((_, _, _, _) => { calls++; throw new IOException("Must not access SDK."); }).ResumeAsync(fixture.Instance, default); Assert.Fail("The original mapping changed."); }
        catch (Exception error) when (error is InvalidDataException or InvalidOperationException or FileNotFoundException) { }
        Assert.AreEqual(0, calls); Assert.IsNotNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None));
    }

    [TestMethod]
    public async Task InternallyConsistentStorageCannotReplaceThePublicBatchFingerprint()
    {
        await using var fixture = await Fixture.CreateAsync(); await fixture.RetainByFailureAsync(fixture.Batch("original"));
        var original = (await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None))!;
        await fixture.Catalog.CompleteRetainedRemoteBatchAsync(original);
        var envelope = JsonNode.Parse(original.Payload)!; envelope["changes"]![0]!["remoteRevision"] = "replacement";
        await fixture.Catalog.RetainRemoteBatchAsync(original with { Payload = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(envelope) });
        int calls = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Coordinator((_, _, _, _) =>
        { calls++; throw new IOException("Do not access SDK."); }).ResumeAsync(fixture.Instance, default).AsTask());
        Assert.AreEqual(0, calls);
    }

    [TestMethod]
    public async Task RootPollingUsesItsExistingDurableSlotAndDoesNotCreateAnIncomingSlot()
    {
        await using var fixture = await Fixture.CreateAsync(); byte[] initial = new byte[32], final = Enumerable.Repeat((byte)1, 32).ToArray();
        var batch = new CloudRemoteChangeBatch(MirrorPulseRemotePollBatchScope.Prefix(fixture.Root) + Convert.ToHexString(initial) + "/" + Convert.ToHexString(final), initial, [], final);
        var result = await fixture.Coordinator(async (_, replay, _, _) =>
        { Assert.IsNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None)); return new(false, replay.InitialCursor); })
            .ApplyAsync(fixture.Instance, batch, fixture.Policy, default);
        Assert.IsFalse(result.Completed); Assert.IsNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None));
    }

    [TestMethod]
    public async Task StartupConvergesAnOriginalMultiEntryBatchWithoutWaitingForAnotherIncomingEvent()
    {
        await using var fixture = await Fixture.CreateAsync(); var seed = fixture.Batch("many");
        var batch = new CloudRemoteChangeBatch(seed.BatchId, seed.InitialCursor,
            Enumerable.Range(0, 3).Select(index => new CloudRemoteChange("change-" + index, CloudRemoteChangeKind.Delete,
                "remote-" + index, "v1", CloudItemKind.File, "Files\\file-" + index + ".bin", Guid.NewGuid(), cursorAfter: new byte[] { (byte)(index + 1) })).ToArray(),
            new byte[] { 3 });
        await fixture.RetainByFailureAsync(batch); await fixture.ReopenAsync(); int applied = 0;
        Assert.IsTrue(await fixture.Coordinator(async (_, original, _, token) =>
        {
            fixture.Calls.Add(original); applied++;
            await using var transaction = await fixture.Store.BeginTransactionAsync(token);
            await transaction.RemoteBatches.UpsertAsync(new(original.BatchId, original.Changes[applied - 1].CursorAfter.Span,
                applied, original.Changes.Count, applied == 3 ? CloudRemoteBatchStatus.Applied : CloudRemoteBatchStatus.Applying,
                [], DateTimeOffset.UtcNow, original.Fingerprint, original.Changes[applied - 1].ChangeId), token);
            if (applied == 3) await transaction.Checkpoints.UpsertAsync(new(MirrorPulseRemoteBatchCoordinator.GetCheckpointName(fixture.Instance), original.FinalCursor.Span, DateTimeOffset.UtcNow), token);
            await transaction.CommitAsync(token); return new(applied == 3, original.Changes[applied - 1].CursorAfter);
        }).ResumeAsync(fixture.Instance, default));
        Assert.HasCount(3, fixture.Calls); Assert.IsTrue(fixture.Calls.All(value => value.BatchId == batch.BatchId));
        Assert.IsNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None));
    }

    [TestMethod]
    public async Task StartupStopsAfterOneUnchangedPrefixWithoutClearingOrRepeatedlyDispatching()
    {
        await using var fixture = await Fixture.CreateAsync(); await fixture.RetainByFailureAsync(fixture.Batch("retry")); int calls = 0;
        Assert.IsFalse(await fixture.Coordinator((_, original, _, _) =>
        { calls++; return ValueTask.FromResult(new MirrorPulseRemotePollApplyOutcome(false, original.InitialCursor)); }).ResumeAsync(fixture.Instance, default));
        Assert.AreEqual(1, calls); Assert.IsNotNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InvalidIncomingScopeCannotTriggerEvenTheEarlierRetainedSdkWork(bool malformedPolling)
    {
        await using var fixture = await Fixture.CreateAsync(); var original = fixture.Batch("retained"); await fixture.RetainByFailureAsync(original);
        var invalid = new CloudRemoteChangeBatch(malformedPolling ? fixture.Instance + "/poll-root/invalid" : InstanceId.New() + "/foreign", ReadOnlyMemory<byte>.Empty, [], new byte[] { 9 });
        int calls = 0;
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => fixture.Coordinator((_, _, _, _) =>
        { calls++; throw new IOException("Invalid input must not reach SDK recovery."); }).ApplyAsync(fixture.Instance, invalid, fixture.Policy, default).AsTask());
        Assert.AreEqual(0, calls); Assert.AreEqual(original.BatchId, (await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Instance, CancellationToken.None))!.BatchId);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-incoming-batch", Guid.NewGuid().ToString("N"));
        internal InstanceId Instance { get; } = InstanceId.New();
        internal RootRegistration Root { get; private set; } = null!;
        internal MirrorPulseStoragePaths Paths { get; private set; } = null!;
        internal MirrorPulseRootRouter Router { get; private set; } = null!;
        internal MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        internal ICloudStateStore Store { get; private set; } = null!;
        internal CloudRemoteApplyOptions Policy { get; } = CloudRemoteApplyOptions.Default with { MaximumEntries = 1 };
        internal List<CloudRemoteChangeBatch> Calls { get; } = [];
        internal static async Task<Fixture> CreateAsync()
        {
            var fixture = new Fixture(); fixture.Paths = new(Path.Combine(fixture._directory, "sync"), Path.Combine(fixture._directory, "data"));
            Directory.CreateDirectory(fixture.Paths.SyncRootPath);
            fixture.Root = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.incoming-batch"), fixture.Instance,
                new("files", "Files", "Files", false), RootRegistrationState.Active, identityScope: RootIdentityScope.InstanceRoot);
            fixture.Router = new(fixture.Paths.SyncRootPath, [fixture.Root]);
            fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            fixture.Store = await MirrorPulseCfSharpStateStoreFactory.Create(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
            return fixture;
        }
        internal MirrorPulseRetainedRemoteBatchCoordinator Coordinator(Func<InstanceId, CloudRemoteChangeBatch, CloudRemoteApplyOptions, CancellationToken, ValueTask<MirrorPulseRemotePollApplyOutcome>> apply) => new(Catalog, Store, Router, apply);
        internal CloudRemoteChangeBatch Batch(string id, byte[]? initial = null, byte[]? final = null) => new(Instance + "/" + id, initial ?? [],
            [new("change-" + id, CloudRemoteChangeKind.FileUpsert, "remote", "v1", CloudItemKind.File, "Files\\file.bin", Guid.NewGuid(),
                length: 1, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build(), cursorAfter: final ?? new byte[] { 1 })], final ?? new byte[] { 1 });
        internal async Task RetainByFailureAsync(CloudRemoteChangeBatch batch) =>
            await Assert.ThrowsExactlyAsync<IOException>(() => Coordinator((_, _, _, _) => ValueTask.FromException<MirrorPulseRemotePollApplyOutcome>(new IOException("After retention.")))
                .ApplyAsync(Instance, batch, Policy, default).AsTask());
        internal async Task CommitProgressAsync(CloudRemoteChangeBatch batch, bool cursor = true, string? fault = null)
        {
            await using var transaction = await Store.BeginTransactionAsync();
            if (fault != "missing-progress") await transaction.RemoteBatches.UpsertAsync(new(batch.BatchId, batch.FinalCursor.Span,
                fault == "partial" ? 0 : batch.Changes.Count, batch.Changes.Count,
                fault == "partial" ? CloudRemoteBatchStatus.Applying : CloudRemoteBatchStatus.Applied, [], DateTimeOffset.UtcNow,
                fault == "fingerprint" ? new byte[32] : batch.Fingerprint,
                fault == "last-change" ? "different" : (batch.Changes.Count == 0 ? null : batch.Changes[^1].ChangeId)));
            if (cursor && fault != "missing-cursor") await transaction.Checkpoints.UpsertAsync(new(MirrorPulseRemoteBatchCoordinator.GetCheckpointName(Instance),
                fault == "cursor" ? new byte[] { 9 } : batch.FinalCursor.Span, DateTimeOffset.UtcNow));
            await transaction.CommitAsync();
        }
        internal async Task ReopenAsync() { await Store.DisposeAsync(); await Catalog.DisposeAsync(); Catalog = await MirrorPulseProductCatalog.OpenAsync(Paths); Store = await MirrorPulseCfSharpStateStoreFactory.Create(Paths).OpenAsync(new(Paths.SyncRootPath)); }
        public async ValueTask DisposeAsync() { await Store.DisposeAsync(); await Catalog.DisposeAsync(); Directory.Delete(_directory, true); }
    }
}
