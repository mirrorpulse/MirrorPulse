using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    private static readonly string[] BottomUpPaths = ["Docs/Nested/edited.txt", "Docs/Nested", "Docs"];
    private static readonly int[] PartialTreeWrites = [0, 0, 1];
    [TestMethod]
    public async Task WholeTreePersistsAllOriginalsThenAppliesBottomUpAndAuditsIndependently()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (tree, members) = await CreateTreeAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var model = new TreeModel(catalog, coordinator, members);
        model.BeforeApply = async _ =>
        {
            foreach (var member in members)
                Assert.AreEqual(member.Preparation.Baseline,
                    await catalog.ReadNamespacePermissionBaselineAsync(member.Preparation.Baseline.EvidenceId));
        };
        var result = await model.RunAsync(tree);
        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.Verified, result.Outcome);
        Assert.AreEqual(3, result.CompletedMembers);
        CollectionAssert.AreEqual(BottomUpPaths, model.Applications.ToArray());
        Assert.AreEqual(2, model.Audits);
        Assert.IsTrue(model.Objects.All(item => item.Writes == 1));
        var replay = await model.RunAsync(tree);
        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.Verified, replay.Outcome);
        Assert.IsTrue(model.Objects.All(item => item.Writes == 1));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("extra")]
    [DataRow("duplicate")]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("dacl")]
    public async Task WholeTreeRejectsFreshInventoryMismatchBeforeAnyWrite(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (tree, members) = await CreateTreeAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var model = new TreeModel(catalog, coordinator, members);
        model.Transform = items => ChangeInventory(items, mismatch);
        var result = await model.RunAsync(tree);
        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired, result.Outcome);
        Assert.AreEqual(0, result.CompletedMembers);
        Assert.IsTrue(model.Objects.All(item => item.Writes == 0));
        foreach (var member in members)
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared,
                (await catalog.ReadNamespacePermissionChangeAsync(member.Preparation.Intent.OperationId))!.Phase);
    }

    [TestMethod]
    [DataRow("extra")]
    [DataRow("dacl")]
    public async Task IndividualVerificationCannotReplaceIndependentWholeTreeAudit(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (tree, members) = await CreateTreeAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var model = new TreeModel(catalog, coordinator, members);
        model.Transform = items => model.Audits == 2 ? ChangeInventory(items, mismatch) : items;
        var result = await model.RunAsync(tree);
        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired, result.Outcome);
        Assert.AreEqual(3, result.CompletedMembers);
        foreach (var member in members)
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Verified,
                (await catalog.ReadNamespacePermissionChangeAsync(member.Preparation.Intent.OperationId))!.Phase);
    }

    [TestMethod]
    public async Task PartialTreeApplicationFailureDoesNotApplyParentsAndRecoversWithoutRewritingChild()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespacePermissionTree tree;
        MirrorPulseNamespacePermissionTreeMember[] members;
        ObjectLease[] retained;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            (tree, members) = await CreateTreeAsync(catalog, fixture);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            var model = new TreeModel(catalog, coordinator, members) { FailAt = "Docs/Nested" };
            var result = await model.RunAsync(tree);
            Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired, result.Outcome);
            Assert.AreEqual(1, result.CompletedMembers);
            CollectionAssert.AreEqual(PartialTreeWrites, model.Objects.Select(item => item.Writes).ToArray());
            retained = model.Objects;
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            var model = new TreeModel(catalog, coordinator, members, retained);
            Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.Verified, (await model.RunAsync(tree)).Outcome);
            Assert.IsTrue(retained.All(item => item.Writes == 1));
        }
    }

    [TestMethod]
    public async Task StaleRoleCannotUseOriginalManifestToRewriteCurrentPermissions()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (tree, members) = await CreateTreeAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var model = new TreeModel(catalog, coordinator, members);
        var result = await model.RunAsync(tree, role: "S-1-5-5-123-789");
        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired, result.Outcome);
        Assert.AreEqual(MirrorPulseNamespacePermissionRecoveryReason.Interrupted, result.RecoveryReason);
        Assert.AreEqual(0, model.Audits);
        Assert.IsTrue(model.Objects.All(item => item.Writes == 0));
    }

    [TestMethod]
    public async Task WholeTreeApplicationCrossesBoundedPagesWithoutApplyingAParentEarly()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (tree, members) = await CreateTreeAsync(catalog, fixture, 257);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var model = new TreeModel(catalog, coordinator, members);
        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.Verified, (await model.RunAsync(tree)).Outcome);
        CollectionAssert.AreEqual(members.Reverse().Select(member => member.Preparation.Intent.RelativePath).ToArray(), model.Applications.ToArray());
        Assert.AreEqual("Docs", model.Applications[^1]);
        Assert.HasCount(257, model.Objects);
    }

    [TestMethod]
    public async Task SealedCaptureCorruptionStopsBeforeNativeAdmissionOrFreshInventory()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (tree, members) = await CreateTreeAsync(catalog, fixture);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString()))
        {
            await connection.OpenAsync();
            await using var corrupt = connection.CreateCommand();
            corrupt.CommandText = "UPDATE namespace_permission_tree_members SET fingerprint=zeroblob(32) WHERE sequence=2;";
            Assert.AreEqual(1, await corrupt.ExecuteNonQueryAsync());
        }
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var model = new TreeModel(catalog, coordinator, members);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => model.RunAsync(tree));
        Assert.AreEqual(0, model.Audits);
        Assert.IsTrue(model.Objects.All(item => item.Writes == 0));
    }

    [TestMethod]
    public async Task CancellationAndOwnerDisposalDrainAcceptedTreeWork()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (tree, members) = await CreateTreeAsync(catalog, fixture);
        var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var model = new TreeModel(catalog, coordinator, members);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        model.BeforeApply = async _ => { entered.TrySetResult(); await release.Task; };
        using var stop = new CancellationTokenSource();
        Task run = model.RunAsync(tree, cancellationToken: stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        Task disposal = coordinator.DisposeAsync().AsTask();
        Assert.IsFalse(run.IsCompleted);
        Assert.IsFalse(disposal.IsCompleted);
        release.TrySetResult();
        await Assert.ThrowsAsync<OperationCanceledException>(() => run);
        await disposal;
        Assert.IsTrue(model.Objects.All(item => item.Writes == 0));
    }

    private static MirrorPulseNamespacePermissionObject[] ChangeInventory(MirrorPulseNamespacePermissionObject[] items, string mismatch) => mismatch switch
    {
        "missing" => items[..^1],
        "extra" => [.. items, items[^1] with { RelativePath = "Docs/new.txt" }],
        "duplicate" => [.. items, items[^1]],
        "binding" => [.. items[..^1], items[^1] with { LocalObject = items[^1].LocalObject with { LocalFileId = Guid.NewGuid() } }],
        "owner" => [.. items[..^1], items[^1] with { OwnerSid = "S-1-5-21-100-200-300-1002" }],
        "dacl" => [.. items[..^1], items[^1] with { Dacl = Unowned }],
        _ => throw new ArgumentException("Unknown mismatch.", nameof(mismatch)),
    };

    private static async Task<(MirrorPulseNamespacePermissionTree Tree, MirrorPulseNamespacePermissionTreeMember[] Members)>
        CreateTreeAsync(MirrorPulseProductCatalog catalog, Fixture fixture, int count = 3)
    {
        MirrorPulseNamespacePermissionPreparation Preparation(string path, bool directory)
        {
            var baseline = fixture.Baseline with
            {
                EvidenceId = Guid.NewGuid(),
                RelativePath = path,
                IsDirectory = directory,
                LocalObject = fixture.Baseline.LocalObject with { LocalFileId = Guid.NewGuid() },
            };
            return new(baseline, fixture.Intent with
            {
                OperationId = Guid.NewGuid(),
                EvidenceId = baseline.EvidenceId,
                RelativePath = path,
                LocalObject = baseline.LocalObject,
            });
        }
        var root = Preparation("Docs", true);
        var nested = Preparation("Docs/Nested", true);
        var definition = new MirrorPulseNamespacePermissionTreeDefinition(Guid.NewGuid(), root, count, root.Baseline.CapturedAt);
        MirrorPulseNamespacePermissionTreeMember[] members =
        [new(0, null, root), new(1, root.Baseline.EvidenceId, nested), new(2, nested.Baseline.EvidenceId, Preparation("Docs/Nested/edited.txt", false))];
        if (count > 3)
            members = [new(0, null, root), .. Enumerable.Range(1, count - 1).Select(index =>
                new MirrorPulseNamespacePermissionTreeMember(index, root.Baseline.EvidenceId, Preparation($"Docs/file-{index}.txt", false)))];
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        await catalog.AppendNamespacePermissionTreeMembersAsync(definition.ManifestId, members);
        return (await catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, DateTimeOffset.UtcNow), members);
    }

    // These leases exercise product policy with a real SQLite catalog. They do not simulate
    // a native CfSharp receipt; the separate Windows probe must execute the public entry point.
    private sealed class TreeModel(MirrorPulseProductCatalog catalog, MirrorPulseNamespacePermissionCoordinator coordinator,
        MirrorPulseNamespacePermissionTreeMember[] members, ObjectLease[]? retained = null)
    {
        public ObjectLease[] Objects { get; } = retained ?? members.Select(member => new ObjectLease(member.Preparation.Baseline, member.Preparation.Intent)).ToArray();
        public List<string> Applications { get; } = [];
        public int Audits { get; private set; }
        public string? FailAt { get; init; }
        public Func<MirrorPulseNamespacePermissionObject[], MirrorPulseNamespacePermissionObject[]>? Transform { get; set; }
        public Func<string, Task>? BeforeApply { get; set; }

        public Task<MirrorPulseNamespacePermissionTreeResult> RunAsync(MirrorPulseNamespacePermissionTree tree,
            string? role = null, CancellationToken cancellationToken = default) => coordinator.ReconcileTreeCoreAsync(
                tree.Definition.ManifestId, role ?? tree.Definition.Anchor.Intent.RoleSid, InspectAsync, ApplyAsync, cancellationToken);

        private async IAsyncEnumerable<MirrorPulseNamespacePermissionObject> InspectAsync([EnumeratorCancellation] CancellationToken stop)
        {
            Audits++;
            var current = Objects.Select(item => item.Current with { ObservedAt = DateTimeOffset.UtcNow }).ToArray();
            foreach (var item in Transform?.Invoke(current) ?? current)
            {
                stop.ThrowIfCancellationRequested();
                yield return item;
                await Task.CompletedTask;
            }
        }

        private async Task<bool> ApplyAsync(MirrorPulseNamespacePermissionPreparation preparation, CancellationToken stop)
        {
            string path = preparation.Intent.RelativePath;
            if (BeforeApply is not null) await BeforeApply(path);
            if (path == FailAt) return false;
            Applications.Add(path);
            var change = await catalog.ReadNamespacePermissionChangeForApplicationAsync(preparation.Intent.OperationId, stop);
            var result = await coordinator.ApplyAdmittedAsync(change, preparation.Baseline,
                Objects.Single(item => item.Current.RelativePath == path), stop);
            return result.Outcome is MirrorPulseNamespacePermissionOutcome.Verified or MirrorPulseNamespacePermissionOutcome.AlreadyVerified;
        }
    }
}
