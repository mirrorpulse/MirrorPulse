using Microsoft.Data.Sqlite;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnAuditAdmissionExpiresBeforeCompletionAndCannotBeReused(bool selectedRoot)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (anchor, root) = await PrepareAuditMembersAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        MirrorPulseNamespaceCurrentTreeAuditAdmission? retained = null;
        MirrorPulseNamespaceMemberProtectionSnapshot? member = null;
        int completions = 0;
        int result = await coordinator.RunCurrentTreeAuditAsync(anchor.MemberId, (admission, _) =>
        {
            retained = admission;
            Assert.AreEqual(selectedRoot ? root.RootId : null, admission.Inventory.ScopeRoot);
            Assert.HasCount(2, admission.Inventory.Members);
            foreach (var current in admission.Inventory.Members) admission.RequireSnapshot(current);
            member = admission.Inventory.Members.Single(current => current.Context.Member.MemberId == root.MemberId);
            return Task.FromResult(17);
        }, default, (_, _) =>
        {
            Assert.ThrowsExactly<ObjectDisposedException>(() => retained!.RequireSnapshot(member!));
            completions++; return Task.CompletedTask;
        }, selectedRoot ? root.RootId : null);
        Assert.AreEqual(17, result); Assert.AreEqual(1, completions);
        Assert.ThrowsExactly<ObjectDisposedException>(() => retained!.RequireActive());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("member")]
    [DataRow("position")]
    [DataRow("role")]
    [DataRow("epoch")]
    [DataRow("identity")]
    public async Task AnAuditAdmissionRejectsForeignOrAlteredMemberProofs(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (anchor, root) = await PrepareAuditMembersAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        _ = await coordinator.RunCurrentTreeAuditAsync(anchor.MemberId, (admission, _) =>
        {
            var current = admission.Inventory.Members.Single(member => member.Context.Member.MemberId == root.MemberId);
            var context = current.Context;
            var foreign = changed switch
            {
                "member" => current with { Context = context with { Member = context.Member with { MemberId = Guid.NewGuid() } } },
                "position" => current with { Context = context with { Location = context.Location with { RelativePath = "Docs/substituted" } } },
                "role" => current with { Context = context with { Protection = context.Protection with { RoleSid = "S-1-5-5-987-654" } } },
                "epoch" => current with { Context = context with { Protection = context.Protection with { ProtectionId = Guid.NewGuid() } } },
                _ => current with { Identity = current.Identity with { ItemId = Guid.NewGuid() } },
            };
            admission.RequireSnapshot(current);
            Assert.ThrowsExactly<InvalidDataException>(() => admission.RequireSnapshot(foreign));
            return Task.FromResult(1);
        }, default);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("role")]
    [DataRow("fence")]
    [DataRow("name")]
    public async Task AChangedCatalogGraphRejectsTheAuditBeforeCompletion(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (anchor, root) = await PrepareAuditMembersAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        bool mutationCommitted = false, completed = false;
        MirrorPulseNamespaceCurrentTreeAuditAdmission? retained = null;
        async Task<int> AuditAsync() => await coordinator.RunCurrentTreeAuditAsync(anchor.MemberId, async (admission, token) =>
        {
            retained = admission;
            // Change actual product facts through another boundary. These managed
            // catalog receipts assert no native ACL write or filesystem success.
            if (changed == "role")
            {
                foreach (var member in new[] { anchor, root })
                {
                    var baseline = (await catalog.ReadNamespacePermissionBaselineAsync(member.OriginId, token))!;
                    var previous = (await catalog.ReadLatestNamespacePermissionChangeAsync(member.OriginId, token))!;
                    var intent = previous.Intent with
                    {
                        OperationId = Guid.NewGuid(),
                        Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
                        RoleSid = "S-1-5-5-987-654",
                        ExpectedDacl = previous.Verification!.Dacl,
                        TargetDacl = previous.Intent.TargetDacl.Replace(previous.Intent.RoleSid, "S-1-5-5-987-654", StringComparison.Ordinal),
                        PreparedAt = DateTimeOffset.UtcNow,
                    };
                    await catalog.PrepareNamespacePermissionChangeAsync(baseline, intent, token);
                    await catalog.RecordNamespacePermissionApplicationAsync(intent.OperationId, baseline.LocalObject, intent.PreparedAt.AddSeconds(1), token);
                    await catalog.VerifyNamespacePermissionChangeAsync(intent.OperationId,
                        new(baseline.LocalObject, baseline.OwnerSid, intent.TargetDacl, intent.PreparedAt.AddSeconds(2)), token);
                }
                Assert.AreNotEqual(admission.Inventory.Fingerprint, (await catalog.ReadNamespaceCurrentTreeSnapshotAsync(anchor.MemberId, token)).Fingerprint);
            }
            else if (changed == "fence")
            {
                var proof = (await catalog.ReadLatestNamespacePermissionChangeAsync(root.OriginId, token))!;
                await catalog.PrepareNamespaceBirthAsync(new(1, Guid.NewGuid(), root.RootId!.Value, root.OriginId,
                    proof.Intent.OperationId, root.LocalObject, "Docs/pending.bin", false,
                    MirrorPulseNamespaceBirthOrigin.ControlledCreation, proof.Intent.RoleSid, DateTimeOffset.UtcNow), token);
                CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync(token)).ToArray(), root.RootId.Value);
            }
            else
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
                await connection.OpenAsync(token); await using var damage = connection.CreateCommand();
                damage.CommandText = "DELETE FROM namespace_member_names WHERE member_id=$member;";
                damage.Parameters.AddWithValue("$member", root.MemberId.ToString("D"));
                Assert.AreEqual(1, await damage.ExecuteNonQueryAsync(token));
            }
            mutationCommitted = true;
            return 1;
        }, default, (_, _) => { completed = true; return Task.CompletedTask; });
        if (changed == "name") await Assert.ThrowsExactlyAsync<InvalidDataException>(AuditAsync);
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(AuditAsync);
        Assert.IsTrue(mutationCommitted); Assert.IsFalse(completed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => retained!.RequireActive());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task CancellationExpiresTheAuditAdmissionAndAllowsTheNextAudit()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (anchor, _) = await PrepareAuditMembersAsync(catalog, fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        using var cancellation = new CancellationTokenSource();
        MirrorPulseNamespaceCurrentTreeAuditAdmission? retained = null;
        await Assert.ThrowsAsync<OperationCanceledException>(() => coordinator.RunCurrentTreeAuditAsync(anchor.MemberId, (admission, token) =>
        {
            retained = admission; cancellation.Cancel(); return Task.FromCanceled<int>(token);
        }, cancellation.Token));
        Assert.ThrowsExactly<ObjectDisposedException>(() => retained!.RequireActive());
        Assert.AreEqual(1, await coordinator.RunCurrentTreeAuditAsync(anchor.MemberId,
            (admission, _) => { admission.RequireActive(); return Task.FromResult(1); }, default));
    }

    private static async Task<(MirrorPulseNamespaceMemberRegistration Anchor, MirrorPulseNamespaceMemberRegistration Root)>
        PrepareAuditMembersAsync(MirrorPulseProductCatalog catalog, Fixture fixture)
    {
        var anchor = await RegisterIdentityRecoveryParentAsync(catalog, fixture, null, string.Empty, fixture.Baseline.LocalObject.SyncRootFileId);
        var root = await RegisterIdentityRecoveryParentAsync(catalog, fixture, anchor, "Docs", Guid.NewGuid());
        return (anchor, root);
    }
}
