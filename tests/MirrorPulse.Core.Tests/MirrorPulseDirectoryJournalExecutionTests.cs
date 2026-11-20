using System.Text.Json;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseDirectoryJournalExecutionTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateDirectory, false)]
    [DataRow(MirrorPulseCoalescedEffect.CreateDirectory, true)]
    [DataRow(MirrorPulseCoalescedEffect.MoveDirectory, false)]
    [DataRow(MirrorPulseCoalescedEffect.MoveDirectory, true)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteDirectory, false)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteDirectory, true)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyDirectoryAbsence, false)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyDirectoryAbsence, true)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyDirectoryUnchanged, false)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyDirectoryUnchanged, true)]
    public async Task OriginalDirectoryEffectsRetainTheirKindAndOwnershipAcrossCompletionFailureAndReopen(MirrorPulseCoalescedEffect effect, bool interrupt)
    {
        using var fixture = new Fixture(effect);
        var remote = new DirectoryRemote(fixture); int completed = 0;
        string originalPlan = JsonSerializer.Serialize(fixture.Plan);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var prepared = await first.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Baseline);
            Assert.AreEqual(4, prepared.Version); Assert.IsNull(prepared.Content); Assert.IsFalse(prepared.IsMoveOnly);
            CollectionAssert.AreEqual(fixture.Window.Select(value => value.OperationId).ToArray(), fixture.Plan.Members.Select(value => value.OperationId).ToArray());
            var executor = Executor(first, remote);
            if (interrupt)
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => executor.ExecuteAsync(fixture.Plan.PlanId, NoContent,
                    (_, _) => ValueTask.FromException(new IOException("Original local completion interrupted after real remote acceptance."))).AsTask());
                var retained = (await first.ReadMutationAsync(prepared.Steps[0].OperationId))!;
                Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, retained.State);
                Assert.IsTrue(retained.Intent.IsDirectory);
                foreach (var step in prepared.Steps.Skip(1)) Assert.IsNull(await first.ReadMutationAsync(step.OperationId));
            }
            else
                _ = await executor.ExecuteAsync(fixture.Plan.PlanId, NoContent, (value, _) => { Assert.IsTrue(value.Intent.IsDirectory); completed++; return ValueTask.CompletedTask; });
        }
        int writes = remote.Writes.Count;
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var results = await Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId, NoContent,
            (value, _) => { Assert.IsTrue(value.Intent.IsDirectory); completed++; return ValueTask.CompletedTask; });
        Assert.IsTrue(results.All(value => value.State == MirrorPulseMutationState.Acknowledged && value.Intent.IsDirectory &&
            value.Intent.ContentLength is null && value.Intent.ContentSha256 is null && value.Intent.UploadBinding is null));
        Assert.AreEqual(results.Count, completed); Assert.HasCount(writes, remote.Writes);
        Assert.AreEqual(originalPlan, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingPlanAsync(fixture.Plan.PlanId)));
        foreach (var command in fixture.Window)
        {
            Assert.IsNull(await reopened.ReadMutationAsync(command.OperationId));
            Assert.AreEqual(fixture.Plan.PlanId, (await reopened.ReadJournalCoalescingOwnerAsync(command.OperationId))!.PlanId);
        }
        int expectedWrites = effect is MirrorPulseCoalescedEffect.VerifyDirectoryAbsence or MirrorPulseCoalescedEffect.VerifyDirectoryUnchanged ? 0 : 1;
        Assert.HasCount(expectedWrites, remote.Writes);
        if (effect is MirrorPulseCoalescedEffect.CreateDirectory or MirrorPulseCoalescedEffect.MoveDirectory) Assert.IsTrue(remote.Exists(fixture.Plan.FinalPath));
        if (effect == MirrorPulseCoalescedEffect.DeleteDirectory) Assert.IsFalse(remote.Exists(fixture.Plan.OriginalPath));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("child")]
    [DataRow("transient-child")]
    [DataRow("collision")]
    [DataRow("mixed-kind")]
    [DataRow("content")]
    [DataRow("metadata")]
    [DataRow("committed")]
    [DataRow("foreign-root")]
    [DataRow("foreign-instance")]
    [DataRow("cross-root")]
    [DataRow("path-gap")]
    [DataRow("null-identity")]
    [DataRow("single")]
    [DataRow("successor")]
    public void DirectoryPlanningRetainsUnresolvedDependenciesAndCannotAdoptAnotherObject(string damage)
    {
        using var fixture = new Fixture(damage == "successor" ? MirrorPulseCoalescedEffect.VerifyDirectoryAbsence : MirrorPulseCoalescedEffect.CreateDirectory);
        var window = fixture.Window.ToList(); var committed = new HashSet<Guid>(); var first = window[0]; var last = window[^1];
        switch (damage)
        {
            case "child": window.Add(first with { OperationId = Guid.NewGuid(), Sequence = 4, ItemId = Guid.NewGuid(), RelativePath = "original/child", IsDirectory = false }); break;
            case "transient-child":
                var child = first with { OperationId = Guid.NewGuid(), Sequence = 4, ItemId = Guid.NewGuid(), RelativePath = "original/child", IsDirectory = false };
                window.Add(child); window.Add(child with { OperationId = Guid.NewGuid(), Sequence = 5, Kind = MirrorPulseWorkerChangeKind.Delete }); break;
            case "collision": window.Add(first with { OperationId = Guid.NewGuid(), Sequence = 4, ItemId = Guid.NewGuid(), RelativePath = fixture.Plan.FinalPath, IsDirectory = false }); break;
            case "mixed-kind": window[0] = first with { IsDirectory = false }; break;
            case "content": window[^1] = last with { Kind = MirrorPulseWorkerChangeKind.ContentUpdate }; break;
            case "metadata": window[^1] = last with { Kind = MirrorPulseWorkerChangeKind.MetadataUpdate }; break;
            case "committed": committed.Add(first.OperationId); break;
            case "foreign-root": window[^1] = last with { RootKey = "other" }; break;
            case "foreign-instance": window[^1] = last with { InstanceId = InstanceId.New() }; break;
            case "cross-root": window[^1] = last with { PreviousRootKey = "other" }; break;
            case "path-gap": window[^1] = last with { PreviousRelativePath = "another" }; break;
            case "null-identity": window = window.Select(value => value with { ItemId = null }).ToList(); break;
            case "single": window = [first]; break;
            case "successor": window.Add(first with { OperationId = Guid.NewGuid(), Sequence = 4 }); break;
        }
        Assert.IsNull(MirrorPulseJournalDirectoryCoalescingPlanner.TryPlan(window, fixture.ItemId, 8, committed));
        Assert.IsNull(MirrorPulseJournalCoalescingPlanner.TryPlan(fixture.Window, fixture.ItemId, 8, new HashSet<Guid>()));
    }

    [TestMethod]
    public async Task DirectoryPreparationCannotReplaceAFileWindowOrBorrowFileUploadProof()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.CreateDirectory);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var fileWindow = fixture.Window.Select(value => value with { IsDirectory = false }).ToArray();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fileWindow, null));
        Assert.IsEmpty(await catalog.ReadJournalCoalescingPlansAsync());
        var binding = new MirrorPulseUploadBinding(new(1, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile);
        Assert.ThrowsExactly<ArgumentException>(() => MirrorPulseJournalCoalescingExecutionPlanner.Create(fixture.Plan, fixture.Window,
            null, new(0, new('A', 64), binding)));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateDirectory)]
    [DataRow(MirrorPulseCoalescedEffect.MoveDirectory)]
    public async Task AcceptedDirectoryCannotResumeCompletionAgainstALaterFileWithTheSameRevision(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new DirectoryRemote(fixture);
        Guid operation;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var execution = await first.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, fixture.Baseline);
            operation = execution.Steps[0].OperationId;
            await Assert.ThrowsExactlyAsync<IOException>(() => Executor(first, remote).ExecuteAsync(fixture.Plan.PlanId, NoContent,
                (_, _) => ValueTask.FromException(new IOException("Interrupt original completion after acceptance."))).AsTask());
        }
        remote.ReplaceWithFile(fixture.Plan.FinalPath); int writes = remote.Writes.Count;
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => Executor(reopened, remote).ExecuteAsync(fixture.Plan.PlanId,
            NoContent, (_, _) => throw new AssertFailedException("A later file cannot complete the original directory.")).AsTask());
        Assert.AreEqual(MirrorPulseMutationState.RemoteAccepted, (await reopened.ReadMutationAsync(operation))!.State);
        Assert.HasCount(writes, remote.Writes); Assert.IsTrue(remote.FileExists(fixture.Plan.FinalPath));
        foreach (var original in fixture.Window) Assert.AreEqual(fixture.Plan.PlanId, (await reopened.ReadJournalCoalescingOwnerAsync(original.OperationId))!.PlanId);
    }

    [TestMethod]
    public async Task DirectoryAbsenceCannotCompleteItsSecondOriginalAddressAfterALaterOccupantAppears()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.VerifyDirectoryAbsence); var remote = new DirectoryRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var execution = await catalog.PrepareJournalCoalescingAndExecutionAsync(fixture.Plan, fixture.Window, null); int completions = 0;
        await Assert.ThrowsExactlyAsync<MirrorPulseWorkerMutationConflictException>(() => Executor(catalog, remote).ExecuteAsync(fixture.Plan.PlanId, NoContent,
            (_, _) => { completions++; remote.AddLaterFile(fixture.Plan.FinalPath); return ValueTask.CompletedTask; }).AsTask());
        Assert.AreEqual(1, completions); Assert.IsEmpty(remote.Writes); Assert.IsTrue(remote.FileExists(fixture.Plan.FinalPath));
        Assert.AreEqual(MirrorPulseMutationState.Acknowledged, (await catalog.ReadMutationAsync(execution.Steps[0].OperationId))!.State);
        Assert.AreEqual(MirrorPulseMutationState.Conflict, (await catalog.ReadMutationAsync(execution.Steps[1].OperationId))!.State);
        foreach (var original in fixture.Window) Assert.IsNull(await catalog.ReadMutationAsync(original.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    private static ValueTask<Stream> NoContent(MirrorPulseCoalescingContent _, CancellationToken __) =>
        ValueTask.FromException<Stream>(new AssertFailedException("A directory program must never open file content."));
    private static MirrorPulseJournalCoalescingExecutor Executor(MirrorPulseProductCatalog catalog, DirectoryRemote remote) =>
        new(catalog, remote, remote, remote, new(remote, directories: remote));

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-directory-journal-" + Guid.NewGuid().ToString("N"));
        internal Guid ItemId { get; } = Guid.NewGuid();
        internal MirrorPulseStoragePaths Paths { get; }
        internal MirrorPulseWorkerChangeCommand[] Window { get; }
        internal MirrorPulseJournalCoalescingPlan Plan { get; }
        internal string? Baseline { get; }
        internal string Backend => Path.Combine(_directory, "backend");
        internal Fixture(MirrorPulseCoalescedEffect effect)
        {
            Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data")); Directory.CreateDirectory(Backend);
            var instance = InstanceId.New(); var time = DateTimeOffset.UtcNow;
            MirrorPulseWorkerChangeCommand Command(int sequence, MirrorPulseWorkerChangeKind kind, string path, string? previous = null) =>
                new(Guid.NewGuid(), sequence, instance, "docs", kind, path, previous is null ? null : "docs", previous, true, ItemId, time.AddTicks(sequence));
            Window = effect switch
            {
                MirrorPulseCoalescedEffect.CreateDirectory => [Command(1, MirrorPulseWorkerChangeKind.Create, "original"), Command(2, MirrorPulseWorkerChangeKind.Move, "middle", "original"), Command(3, MirrorPulseWorkerChangeKind.Move, "final", "middle")],
                MirrorPulseCoalescedEffect.VerifyDirectoryAbsence => [Command(1, MirrorPulseWorkerChangeKind.Create, "original"), Command(2, MirrorPulseWorkerChangeKind.Move, "final", "original"), Command(3, MirrorPulseWorkerChangeKind.Delete, "final")],
                MirrorPulseCoalescedEffect.MoveDirectory => [Command(1, MirrorPulseWorkerChangeKind.Move, "middle", "original"), Command(2, MirrorPulseWorkerChangeKind.Move, "final", "middle")],
                MirrorPulseCoalescedEffect.DeleteDirectory => [Command(1, MirrorPulseWorkerChangeKind.Move, "middle", "original"), Command(2, MirrorPulseWorkerChangeKind.Delete, "middle")],
                _ => [Command(1, MirrorPulseWorkerChangeKind.Move, "middle", "original"), Command(2, MirrorPulseWorkerChangeKind.Move, "original", "middle")],
            };
            Plan = MirrorPulseJournalDirectoryCoalescingPlanner.TryPlan(Window, ItemId, 8, new HashSet<Guid>())!;
            Assert.IsNotNull(Plan); Assert.AreEqual(effect, Plan.Effect); Assert.IsTrue(Plan.IsDirectory);
            Baseline = effect is MirrorPulseCoalescedEffect.CreateDirectory or MirrorPulseCoalescedEffect.VerifyDirectoryAbsence ? null : "original-revision";
        }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); }
    }

    private sealed class DirectoryRemote : IMirrorPulseWorkerStatTransport, IMirrorPulseWorkerUploadTransport,
        IMirrorPulseWorkerMutationTransport, IMirrorPulseWorkerDirectoryPageSource
    {
        private readonly Fixture _fixture; private readonly Dictionary<string, string> _revisions = new(StringComparer.Ordinal);
        internal List<string> Writes { get; } = [];
        internal DirectoryRemote(Fixture fixture)
        { _fixture = fixture; if (fixture.Baseline is { } revision) { Directory.CreateDirectory(PathFor(fixture.Plan.OriginalPath)); _revisions.Add(fixture.Plan.OriginalPath, revision); } }
        private string PathFor(string path) => Path.Combine(_fixture.Backend, path);
        internal bool Exists(string path) => Directory.Exists(PathFor(path));
        internal bool FileExists(string path) => File.Exists(PathFor(path));
        internal void ReplaceWithFile(string path) { Directory.Delete(PathFor(path)); File.WriteAllText(PathFor(path), "later occupant"); }
        internal void AddLaterFile(string path) { File.WriteAllText(PathFor(path), "later occupant"); _revisions.Add(path, "later-revision"); }
        public ValueTask<string?> StatAsync(MirrorPulseWorkerStatRequest request, CancellationToken token) =>
            ValueTask.FromResult(_revisions.GetValueOrDefault(request.NormalizedPath));
        public ValueTask<string> UploadAsync(MirrorPulseWorkerUploadRequest request, CancellationToken token) =>
            ValueTask.FromException<string>(new AssertFailedException("Directory effects must not call upload."));
        public ValueTask<string> CreateDirectoryAsync(MirrorPulseWorkerCreateDirectoryRequest request, CancellationToken token)
        {
            Assert.IsTrue(request.MustBeAbsent); Assert.IsFalse(Exists(request.NormalizedPath));
            Directory.CreateDirectory(PathFor(request.NormalizedPath)); _revisions.Add(request.NormalizedPath, "created-revision"); Writes.Add("CreateDirectory");
            return ValueTask.FromResult("created-revision");
        }
        public ValueTask<string?> DeleteAsync(MirrorPulseWorkerDeleteRequest request, CancellationToken token)
        {
            Assert.IsTrue(request.IsDirectory); Assert.AreEqual(request.ExpectedRevision, _revisions[request.NormalizedPath]);
            Directory.Delete(PathFor(request.NormalizedPath)); _revisions.Remove(request.NormalizedPath); Writes.Add("DeleteDirectory"); return ValueTask.FromResult<string?>(null);
        }
        public ValueTask<string> MoveAsync(MirrorPulseWorkerMoveRequest request, CancellationToken token)
        {
            Assert.IsTrue(request.IsDirectory); Assert.AreEqual(request.ExpectedRevision, _revisions[request.SourcePath]); Assert.IsTrue(request.DestinationMustBeAbsent);
            Directory.Move(PathFor(request.SourcePath), PathFor(request.DestinationPath)); _revisions.Remove(request.SourcePath);
            _revisions.Add(request.DestinationPath, "moved-revision"); Writes.Add("MoveDirectory"); return ValueTask.FromResult("moved-revision");
        }
        public ValueTask<MirrorPulseWorkerDirectoryPage> ReadDirectoryPageAsync(MirrorPulseWorkerDirectoryPageRequest request, CancellationToken token) =>
            ValueTask.FromResult(new MirrorPulseWorkerDirectoryPage(_revisions.Select(pair =>
                new MirrorPulseWorkerDirectoryEntry(pair.Key, pair.Value, FileExists(pair.Key) ? "File" : "Directory", pair.Key, null, null, null, false)).ToArray(), ReadOnlyMemory<byte>.Empty, true));
    }
}
