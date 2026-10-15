using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task ScopedInitializationCompletesAHealthyRootWithoutReleasingAnotherRootsPendingProtection()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceInitializationCompletion completed; RootId blocked, selected; Guid operation;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareBornRootInitializationAsync(catalog);
            blocked = setup.OriginalRoot.RootId!.Value; selected = setup.Roots[0].RootId!.Value;
            var intent = ScopedInitialization(setup, selected); operation = intent.OperationId;
            await catalog.PrepareNamespaceInitializationAsync(ScopedInitialization(setup, blocked));
            await catalog.PrepareNamespaceInitializationAsync(intent);
            var baseline = (await catalog.ReadNamespacePermissionBaselineAsync(setup.OriginalRoot.OriginId))!;
            var latest = (await catalog.ReadLatestNamespacePermissionChangeAsync(baseline.EvidenceId))!;
            await catalog.PrepareNamespacePermissionChangeAsync(baseline, latest.Intent with
            {
                OperationId = Guid.NewGuid(),
                Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                RoleSid = "S-1-5-5-123-457",
                ExpectedDacl = latest.Verification!.Dacl,
                TargetDacl = latest.Intent.TargetDacl.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal),
                PreparedAt = intent.PreparedAt.AddSeconds(1),
            });
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.Anchor));
            var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor, selected);
            Assert.AreEqual(selected, inventory.ScopeRoot); Assert.HasCount(3, inventory.Members);
            Assert.IsTrue(inventory.Members.All(member => member.Context.Member.RootId is null || member.Context.Member.RootId == selected));
            CollectionAssert.AreEqual(new[] { selected }, inventory.FencedRoots.ToArray());
            // These synthetic observations prove catalog correlation, not a native audit.
            completed = await catalog.CompleteNamespaceCurrentInitializationAsync(operation, inventory, intent.PreparedAt.AddMinutes(1));
            Assert.AreEqual(selected, completed.ScopeRoot); Assert.AreEqual(3, completed.MemberCount);
            CollectionAssert.AreEqual(new[] { blocked }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(completed, await reopened.ReadNamespaceInitializationCompletionAsync(operation));
        Assert.AreEqual(selected, (await reopened.ReadNamespaceInitializationStatusAsync(operation)).Completion!.ScopeRoot);
        CollectionAssert.AreEqual(new[] { blocked }, (await reopened.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow("whole-inventory")]
    [DataRow("another-root")]
    [DataRow("missing-member")]
    public async Task ScopedInitializationRejectsAnInventoryOutsideItsExplicitRoot(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareBornRootInitializationAsync(catalog); RootId selected = setup.Roots[0].RootId!.Value;
        var intent = ScopedInitialization(setup, selected); await catalog.PrepareNamespaceInitializationAsync(intent);
        var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor, selected);
        var wrong = invalid switch
        {
            "whole-inventory" => await catalog.ReadNamespaceCurrentTreeSnapshotAsync(setup.Anchor),
            "another-root" => inventory with { ScopeRoot = setup.Roots[1].RootId },
            "missing-member" => inventory with { Members = inventory.Members.Take(inventory.Members.Count - 1).ToArray() },
            _ => throw new ArgumentException("Unknown invalid inventory.", nameof(invalid)),
        };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(
            intent.OperationId, wrong, intent.PreparedAt.AddMinutes(1)));
        Assert.IsNull(await catalog.ReadNamespaceInitializationCompletionAsync(intent.OperationId));
        CollectionAssert.AreEqual(new[] { selected }, (await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
    }

    [TestMethod]
    public async Task ScopedInventoryCannotStandInForAWholeTreeCompletion()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareBornRootInitializationAsync(catalog); await catalog.PrepareNamespaceInitializationAsync(setup.Intent);
        var scoped = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor, setup.Roots[0].RootId!.Value);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(
            setup.Intent.OperationId, scoped, setup.Intent.PreparedAt.AddMinutes(1)));
        Assert.HasCount(3, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task ScopedCompletionAndItsOwnFenceReleaseRollbackTogether()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareBornRootInitializationAsync(catalog); RootId selected = setup.Roots[0].RootId!.Value;
        var intent = ScopedInitialization(setup, selected); await catalog.PrepareNamespaceInitializationAsync(intent);
        var inventory = await catalog.ReadNamespaceCurrentRootSnapshotAsync(setup.Anchor, selected);
        await ExecuteSqlAsync(fixture.Paths,
            "CREATE TRIGGER fail_scope_release BEFORE DELETE ON namespace_initialization_roots BEGIN SELECT RAISE(ABORT,'injected'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.CompleteNamespaceCurrentInitializationAsync(
            intent.OperationId, inventory, intent.PreparedAt.AddMinutes(1)));
        Assert.IsNull(await catalog.ReadNamespaceInitializationCompletionAsync(intent.OperationId));
        Assert.HasCount(1, await catalog.ReadPendingNamespaceInitializationsAsync());
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_scope_release;");
        Assert.AreEqual(selected, (await catalog.CompleteNamespaceCurrentInitializationAsync(
            intent.OperationId, inventory, intent.PreparedAt.AddMinutes(1))).ScopeRoot);
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("multiple")]
    [DataRow("original-route")]
    public async Task VersionFourRequiresOneExplicitCurrentRoot(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareBornRootInitializationAsync(catalog);
        var intent = ScopedInitialization(setup, setup.Roots[0].RootId!.Value);
        intent = invalid switch
        {
            "empty" => intent with { Roots = [] },
            "multiple" => intent with { Roots = setup.Intent.Roots },
            "original-route" => intent with { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.OriginalInventory },
            _ => throw new ArgumentException("Unknown invalid scope.", nameof(invalid)),
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceInitializationAsync(intent));
        Assert.IsEmpty(await catalog.ReadPendingNamespaceInitializationsAsync());
    }

    private static MirrorPulseNamespaceInitializationIntent ScopedInitialization(BornRootInitializationSetup setup, RootId root) =>
        new(4, Guid.NewGuid(), [setup.Intent.Roots.Single(reference => reference.RootId == root)], setup.Intent.PreparedAt)
        { RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory };
}
