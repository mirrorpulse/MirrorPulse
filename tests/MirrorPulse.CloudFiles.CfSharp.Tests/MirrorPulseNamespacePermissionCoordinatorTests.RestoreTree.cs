using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    private const string NextRole = "S-1-5-5-123-789";
    private const string LaterRole = "S-1-5-5-123-890";
    private static readonly string NextTarget = Canonical($"D:P(A;;FRFW;;;{Owner})(A;;FA;;;{NextRole})");
    private static readonly string LaterTarget = Canonical($"D:P(A;;FRFW;;;{Owner})(A;;FA;;;{LaterRole})");

    [TestMethod]
    public async Task ReopenedTreeRotatesAllOwnedPermissionsWithoutChangingOriginalSealOrDescriptors()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespacePermissionTree tree;
        MirrorPulseNamespacePermissionTreeMember[] members;
        ObjectLease[] objects;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            (tree, members) = await CreateTreeAsync(catalog, fixture);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            var model = new TreeModel(catalog, coordinator, members);
            Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.Verified, (await model.RunAsync(tree)).Outcome);
            objects = model.Objects;
        }
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            var model = new TreeModel(catalog, coordinator, members, objects);
            model.BeforePreparationApply = async preparation =>
            {
                if (preparation.Intent.RoleSid != NextRole) return;
                foreach (var member in members)
                    Assert.AreEqual(NextRole, (await catalog.ReadNamespacePermissionObjectHistoryAsync(
                        member.Preparation.Baseline.EvidenceId))[^1].Intent.RoleSid);
            };
            Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.Verified, (await model.RestoreAsync(tree, NextRole, NextTarget)).Outcome);
            foreach (var member in members)
            {
                var history = await catalog.ReadNamespacePermissionObjectHistoryAsync(member.Preparation.Baseline.EvidenceId);
                Assert.HasCount(2, history);
                Assert.AreEqual(MirrorPulseNamespacePermissionChangeKind.RotateRole, history[^1].Intent.Kind);
                Assert.AreEqual(NextRole, history[^1].Intent.RoleSid);
                Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Verified, history[^1].Phase);
                Assert.AreEqual(member.Preparation.Baseline,
                    await catalog.ReadNamespacePermissionBaselineAsync(member.Preparation.Baseline.EvidenceId));
            }
            Assert.AreEqual(tree, await catalog.ReadNamespacePermissionTreeAsync(tree.Definition.ManifestId));
            Assert.IsTrue(objects.All(item => item.Writes == 2));
            Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.Verified, (await model.RestoreAsync(tree, NextRole, NextTarget)).Outcome);
            Assert.IsTrue(objects.All(item => item.Writes == 2));
        }
    }

    [TestMethod]
    public async Task RestartRecoversUnrecordedOldRoleWriteBeforePreparingReplacementRole()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespacePermissionTree tree;
        MirrorPulseNamespacePermissionTreeMember[] members;
        ObjectLease[] objects;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            (tree, members) = await CreateTreeAsync(catalog, fixture);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            var model = new TreeModel(catalog, coordinator, members);
            model.Objects[^1].FailAfterWrite = true;
            await Assert.ThrowsExactlyAsync<IOException>(() => model.RunAsync(tree));
            objects = model.Objects;
            Assert.AreEqual(1, objects[^1].Writes);
        }
        objects[^1].FailAfterWrite = false;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            var model = new TreeModel(catalog, coordinator, members, objects);
            Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.Verified, (await model.RestoreAsync(tree, NextRole, NextTarget)).Outcome);
            // The lost first write is read back, not repeated. Each original then rotates once.
            Assert.IsTrue(objects.All(item => item.Writes == 2));
            foreach (var member in members)
                Assert.HasCount(2, await catalog.ReadNamespacePermissionObjectHistoryAsync(member.Preparation.Baseline.EvidenceId));
        }
    }

    [TestMethod]
    public async Task InterruptedRotationIsRecoveredBeforeAnotherRestartRoleIsSelected()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespacePermissionTree tree;
        MirrorPulseNamespacePermissionTreeMember[] members;
        ObjectLease[] objects;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            (tree, members) = await CreateTreeAsync(catalog, fixture);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            var model = new TreeModel(catalog, coordinator, members);
            await model.RunAsync(tree);
            model.BeforePreparationApply = preparation =>
            {
                if (preparation.Intent.RoleSid == NextRole && !preparation.Baseline.IsDirectory)
                    model.Objects[^1].FailAfterWrite = true;
                return Task.CompletedTask;
            };
            await Assert.ThrowsExactlyAsync<IOException>(() => model.RestoreAsync(tree, NextRole, NextTarget));
            objects = model.Objects;
            Assert.AreEqual(2, objects[^1].Writes);
        }
        objects[^1].FailAfterWrite = false;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            var model = new TreeModel(catalog, coordinator, members, objects);
            Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.Verified, (await model.RestoreAsync(tree, LaterRole, LaterTarget)).Outcome);
            Assert.IsTrue(objects.All(item => item.Writes == 3));
            foreach (var member in members)
            {
                var history = await catalog.ReadNamespacePermissionObjectHistoryAsync(member.Preparation.Baseline.EvidenceId);
                Assert.HasCount(3, history);
                Assert.IsTrue(history.All(change => change.Phase == MirrorPulseNamespacePermissionPhase.Verified));
                Assert.AreEqual(LaterRole, history[^1].Intent.RoleSid);
            }
        }
    }

    [TestMethod]
    [DataRow("wrong-owner")]
    [DataRow("changed-acl")]
    [DataRow("extra-member")]
    public async Task StartupRecoveryDoesNotAdoptForeignOwnerPermissionsOrMembership(string mismatch)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (tree, members) = await CreateTreeAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var model = new TreeModel(catalog, coordinator, members);
        await model.RunAsync(tree);
        if (mismatch == "changed-acl") model.Transform = items => ChangeInventory(items, "dacl");
        if (mismatch == "extra-member") model.Transform = items => ChangeInventory(items, "extra");
        var result = await model.RestoreAsync(tree, NextRole, NextTarget,
            owner: mismatch == "wrong-owner" ? "S-1-5-21-100-200-300-1002" : Owner);
        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired, result.Outcome);
        Assert.IsTrue(model.Objects.All(item => item.Writes == 1));
        foreach (var member in members)
            Assert.HasCount(1, await catalog.ReadNamespacePermissionObjectHistoryAsync(member.Preparation.Baseline.EvidenceId));
    }

    [TestMethod]
    public async Task OriginalTreeVerificationCannotBypassALaterPendingRoleIntent()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (tree, members) = await CreateTreeAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var model = new TreeModel(catalog, coordinator, members);
        await model.RunAsync(tree);
        var prior = (await catalog.ReadNamespacePermissionObjectHistoryAsync(members[^1].Preparation.Baseline.EvidenceId))[^1];
        await catalog.PrepareNamespacePermissionChangeAsync(members[^1].Preparation.Baseline, prior.Intent with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = NextRole,
            ExpectedDacl = prior.Verification!.Dacl,
            TargetDacl = NextTarget,
            PreparedAt = DateTimeOffset.UtcNow,
        });
        var result = await model.RunAsync(tree);
        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired, result.Outcome);
        Assert.AreEqual(MirrorPulseNamespacePermissionRecoveryReason.Interrupted, result.RecoveryReason);
        Assert.AreEqual(0, result.CompletedMembers);
        Assert.IsTrue(model.Objects.All(item => item.Writes == 1));
    }

    [TestMethod]
    public async Task SameRoleDoesNotSilentlyAdoptAChangedProtectionPolicy()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (tree, members) = await CreateTreeAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var model = new TreeModel(catalog, coordinator, members);
        await model.RunAsync(tree);
        var result = await model.RestoreAsync(tree, tree.Definition.Anchor.Intent.RoleSid, Unowned);
        Assert.AreEqual(MirrorPulseNamespacePermissionTreeOutcome.RecoveryRequired, result.Outcome);
        Assert.AreEqual(MirrorPulseNamespacePermissionRecoveryReason.DaclChanged, result.RecoveryReason);
        Assert.IsTrue(model.Objects.All(item => item.Writes == 1));
        foreach (var member in members)
            Assert.HasCount(1, await catalog.ReadNamespacePermissionObjectHistoryAsync(member.Preparation.Baseline.EvidenceId));
    }
}
