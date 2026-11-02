using System.Security.Cryptography;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseConflictKeepRemoteJournalTests
{
    // The typed feed is injected: these are real SDK SQLite/catalog/copy boundaries, not native feed or Host acceptance.
    private sealed class Fixture : IAsyncDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-keep-remote-journal", Guid.NewGuid().ToString("N"));
        private static readonly byte[] LocalBytes = [1, 2, 3];
        private static readonly byte[] RemoteBytes = [4, 5, 6];
        internal MirrorPulseStoragePaths Paths => new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
        internal RootRegistration Root { get; } = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.keep-remote-journal"), InstanceId.New(),
            new("docs", "Docs", "Docs", false), RootRegistrationState.Active);
        internal MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        internal ICloudStateStore Store { get; private set; } = null!;
        internal MirrorPulseConflictKeepRemoteCopies Copies { get; private set; } = null!;
        internal MirrorPulseJournalCoalescingPlan Plan { get; private set; } = null!;
        internal IReadOnlyList<MirrorPulseLocalChangeObservation> Observations { get; private set; } = null!;
        internal Func<Task>? DuringRead { get; set; }
        internal Func<IReadOnlyList<MirrorPulseLocalChangeObservation>, IReadOnlyList<MirrorPulseLocalChangeObservation>>? ChangeObservations { get; set; }
        internal int PublicReads { get; private set; }
        internal Worker Source { get; private set; } = null!;

        internal static async Task<Fixture> OpenAsync(MirrorPulseCoalescedEffect effect = MirrorPulseCoalescedEffect.UpdateFile)
        {
            var fixture = new Fixture();
            try
            {
                Directory.CreateDirectory(fixture.Paths.SyncRootPath);
                fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
                fixture.Store = await new MirrorPulseCfSharpStateSession(fixture.Paths).OpenAsync(new(fixture.Paths.SyncRootPath));
                await fixture.PrepareAsync(effect);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        internal MirrorPulseConflictKeepRemoteJournal Guard() => new(Store, Catalog, new(Paths.SyncRootPath, [Root]), async (members, token) =>
        {
            token.ThrowIfCancellationRequested(); PublicReads++;
            CollectionAssert.AreEqual(Plan.Members.Select(member => member.OperationId).ToArray(), members.Select(member => member.OperationId).ToArray());
            if (DuringRead is not null) await DuringRead();
            return ChangeObservations?.Invoke(Observations) ?? Observations;
        });

        internal async Task ReopenOwnersAsync()
        {
            await Store.DisposeAsync(); await Catalog.DisposeAsync();
            Catalog = await MirrorPulseProductCatalog.OpenAsync(Paths);
            Store = await new MirrorPulseCfSharpStateSession(Paths).OpenAsync(new(Paths.SyncRootPath));
        }

        private async Task PrepareAsync(MirrorPulseCoalescedEffect effect)
        {
            Guid item = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
            (CloudLocalChangeKind Kind, string Path, string? Previous)[] steps = effect switch
            {
                MirrorPulseCoalescedEffect.CreateFile => [(CloudLocalChangeKind.Create, "new.bin", null), (CloudLocalChangeKind.ContentUpdate, "new.bin", null)],
                MirrorPulseCoalescedEffect.VerifyRemoteAbsence => [(CloudLocalChangeKind.Create, "old.bin", null), (CloudLocalChangeKind.Delete, "old.bin", null)],
                MirrorPulseCoalescedEffect.DeleteFile => [(CloudLocalChangeKind.ContentUpdate, "old.bin", null), (CloudLocalChangeKind.Delete, "old.bin", null)],
                MirrorPulseCoalescedEffect.MoveFile => [(CloudLocalChangeKind.Move, "middle.bin", "old.bin"), (CloudLocalChangeKind.Move, "new.bin", "middle.bin")],
                MirrorPulseCoalescedEffect.MoveAndUpdateFile => [(CloudLocalChangeKind.Move, "new.bin", "old.bin"), (CloudLocalChangeKind.ContentUpdate, "new.bin", null)],
                MirrorPulseCoalescedEffect.VerifyRemoteUnchanged => [(CloudLocalChangeKind.Move, "middle.bin", "old.bin"), (CloudLocalChangeKind.Move, "old.bin", "middle.bin")],
                _ => [(CloudLocalChangeKind.ContentUpdate, "old.bin", null), (CloudLocalChangeKind.ContentUpdate, "old.bin", null)]
            };
            Observations = steps.Select((step, index) => new MirrorPulseLocalChangeObservation(Guid.NewGuid(), index + 1, step.Kind,
                item, "Docs/" + step.Path, step.Previous is null ? null : "Docs/" + step.Previous, false, now.AddTicks(index))).ToArray();
            var window = MirrorPulseLocalBatchMapper.MapObservations(Observations, new(Paths.SyncRootPath, [Root])).Commands;
            Plan = MirrorPulseJournalCoalescingPlanner.TryPlan(window, item, window.Count, new HashSet<Guid>())!;
            Assert.AreEqual(effect, Plan.Effect);
            var content = new MirrorPulseCoalescingContent(LocalBytes.Length, Convert.ToHexString(SHA256.HashData(LocalBytes)),
                new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile));
            var execution = await Catalog.PrepareJournalCoalescingAndExecutionAsync(Plan, window,
                effect is MirrorPulseCoalescedEffect.CreateFile or MirrorPulseCoalescedEffect.VerifyRemoteAbsence ? null : "old",
                effect is MirrorPulseCoalescedEffect.CreateFile or MirrorPulseCoalescedEffect.UpdateFile or MirrorPulseCoalescedEffect.MoveAndUpdateFile ? content : null);
            var rejected = await Catalog.PrepareJournalCoalescingMutationAsync(Plan.PlanId, 0);
            await Catalog.TransitionMutationAsync(rejected.Intent.OperationId, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            await Catalog.TransitionMutationAsync(rejected.Intent.OperationId, MirrorPulseMutationState.Executing, MirrorPulseMutationState.Conflict);
            await using (var transaction = await Store.BeginTransactionAsync())
            {
                await transaction.Items.UpsertAsync(new(item, "fixture-" + item.ToString("N"), "Docs/" + Plan.FinalPath,
                    CloudItemKind.File, "old", null, false, now));
                await transaction.CommitAsync();
            }
            Guid conflict = Guid.Empty;
            foreach (var member in Plan.Members)
            {
                conflict = Guid.NewGuid();
                await Catalog.SaveUploadConflictAsync(new(conflict, Root.InstanceId, member.OperationId.ToString("D"), "Docs/" + Plan.FinalPath,
                    MirrorPulseConflictReason.StaleRemoteRevision, MirrorPulseVersionComparison.Diverged, execution.ExpectedRevision, "selected", now));
                await using var transaction = await Store.BeginTransactionAsync();
                await transaction.Operations.EnqueueAsync(new(member.OperationId, CloudStateOperationKind.MetadataUpdate, item,
                    new byte[] { 9, 7, 3 }, now, 4, now.AddHours(1)));
                await transaction.CommitAsync();
            }
            Guid command = Guid.NewGuid();
            await Catalog.SaveUserCommandAsync(new(command, "keepremote", conflict.ToString("D"), "pending"));
            string remotePath = rejected.Intent.Kind == MirrorPulseWorkerChangeKind.Move ? rejected.Intent.PreviousRelativePath! : rejected.Intent.RelativePath;
            var source = new MirrorPulseConflictRemoteSource(1, Root.InstanceId.ToString(), "docs", remotePath, "selected-object", "selected", RemoteBytes.Length);
            var local = effect is MirrorPulseCoalescedEffect.DeleteFile or MirrorPulseCoalescedEffect.VerifyRemoteAbsence ? null : content;
            await Catalog.PrepareConflictKeepRemoteAsync(command, conflict, item, "Docs/" + Plan.FinalPath, source, local);
            Source = new(source);
            var copies = new MirrorPulseConflictCopyStore(Paths);
            Copies = await new MirrorPulseConflictKeepRemotePreserver(Catalog, copies, new(copies, Source, Source, Source))
                .RetainAsync(command, (_, _) => ValueTask.FromResult<Stream>(new MemoryStream(LocalBytes, writable: false)));
        }

        internal async Task ChangeRowAsync(string change)
        {
            await using var transaction = await Store.BeginTransactionAsync();
            var entry = (await transaction.Operations.GetAsync(Plan.Members[0].OperationId))!;
            Guid? itemId = entry.ItemId;
            if (change == "item")
            {
                itemId = Guid.NewGuid();
                await transaction.Items.UpsertAsync(new(itemId.Value, "foreign-fixture", "Docs/foreign.bin",
                    CloudItemKind.File, "old", null, false, entry.CreatedAt));
            }
            if (change is "missing" or "sequence") await transaction.Operations.RemoveAsync(entry.OperationId);
            if (change == "sequence")
                await transaction.Operations.EnqueueAsync(new(entry.OperationId, entry.Kind, entry.ItemId, entry.Payload.Span, entry.CreatedAt, entry.AttemptCount, entry.RetryAfter));
            else if (change == "successor")
                await transaction.Operations.EnqueueAsync(new(Guid.NewGuid(), entry.Kind, Plan.ItemId, new byte[] { 8 }, entry.CreatedAt.AddSeconds(1)));
            else if (change != "missing")
                await transaction.Operations.UpdateAsync(new(entry.OperationId, change == "kind" ? CloudStateOperationKind.ContentUpdate : entry.Kind,
                    itemId, change == "payload" ? new byte[] { 4, 5, 6 } : entry.Payload.Span,
                    change == "created" ? entry.CreatedAt.AddTicks(1) : entry.CreatedAt, change == "attempt" ? entry.AttemptCount + 1 : entry.AttemptCount,
                    change == "retry" ? entry.RetryAfter!.Value.AddTicks(1) : entry.RetryAfter, entry.Sequence));
            await transaction.CommitAsync();
        }

        internal async Task<string> RowsAsync()
        {
            await using var transaction = await Store.BeginTransactionAsync();
            var rows = await transaction.Operations.ListByItemIdAsync(Plan.ItemId, Plan.Members.Count + 2);
            string result = System.Text.Json.JsonSerializer.Serialize(rows.Select(row => new
            {
                row.OperationId,
                row.Sequence,
                row.ItemId,
                row.Kind,
                payload = Convert.ToHexString(row.Payload.Span),
                row.CreatedAt,
                row.AttemptCount,
                row.RetryAfter
            }));
            await transaction.RollbackAsync(); return result;
        }

        public async ValueTask DisposeAsync()
        {
            if (Copies is not null) await Copies.DisposeAsync();
            if (Store is not null) await Store.DisposeAsync();
            if (Catalog is not null) await Catalog.DisposeAsync();
            string root = Path.GetFullPath(_directory);
            string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-keep-remote-journal")) + Path.DirectorySeparatorChar;
            if (!root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Fixture cleanup escaped its private temporary directory.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        internal sealed class Worker(MirrorPulseConflictRemoteSource source) : IMirrorPulseWorkerStatTransport,
            IMirrorPulseWorkerRangeTransport, IMirrorPulseWorkerDirectoryPageSource
        {
            internal int Calls { get; private set; }
            public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult<string?>(source.RemoteRevision); }
            public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested(); Calls++;
                return ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage([new(source.RemoteId, source.RemoteRevision, "File", source.RelativePath,
                    source.Length, null, null, false)], ReadOnlyMemory<byte>.Empty, true));
            }
            public ValueTask<Stream> ReadRangeAsync(MirrorPulseWorkerReadRangeRequest request, CancellationToken cancellationToken)
            { cancellationToken.ThrowIfCancellationRequested(); Calls++; return ValueTask.FromResult<Stream>(new MemoryStream(RemoteBytes, checked((int)request.Offset), checked((int)request.Length), false)); }
        }
    }
}
