using System.Security.AccessControl;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation)]
    public async Task ReplacementBackupAndWriteIntentRetainFirstFactsAcrossCatalogOwners(MirrorPulseNamespaceBirthOrigin origin)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementExecution expected;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first, origin: origin);
            var intent = Replacement((await first.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
            var facts = ReplacementExecutionFacts(intent);
            await first.PrepareNamespaceReplacementAsync(intent); await first.RecordNamespaceReplacementStartAsync(facts.Start);
            Assert.AreEqual(facts.Action, await first.PrepareNamespaceReplacementActionAsync(facts.Action));
            Assert.AreEqual(facts.Backup, await first.RecordNamespaceReplacementBackupAsync(facts.Backup));
            Assert.AreEqual(facts.Write, await first.RecordNamespaceReplacementWriteStartAsync(facts.Write));
            expected = new(intent, facts.Start, null, facts.Action, facts.Backup, facts.Write);
            Assert.AreEqual(expected, await first.ReadNamespaceReplacementExecutionAsync(intent.OperationId));
            Assert.AreEqual(setup.Member, await first.ReadNamespaceMemberAsync(intent.MemberId));
            Assert.IsNull(await first.ReadMutationAsync(intent.OperationId));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(expected, await second.ReadNamespaceReplacementExecutionAsync(expected.Intent.OperationId));
        Assert.AreEqual(expected.Action, await second.PrepareNamespaceReplacementActionAsync(expected.Action!));
        Assert.AreEqual(expected.Backup, await second.RecordNamespaceReplacementBackupAsync(expected.Backup!));
        Assert.AreEqual(expected.WriteStart, await second.RecordNamespaceReplacementWriteStartAsync(expected.WriteStart!));
        CollectionAssert.AreEqual(new[] { expected.Intent.RootId }, (await second.ReadNamespaceRecoveryRootsAsync()).ToArray());
        CollectionAssert.AreEqual(new[] { expected.Intent.RootId }, (await second.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("action")]
    [DataRow("backup")]
    [DataRow("write")]
    public async Task ReplacementCannotRecordSideEffectIntentWithoutItsActualPredecessor(string stage)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent);
        if (stage != "action") await catalog.RecordNamespaceReplacementStartAsync(facts.Start);
        if (stage == "write") await catalog.PrepareNamespaceReplacementActionAsync(facts.Action);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
        {
            if (stage == "action") await catalog.PrepareNamespaceReplacementActionAsync(facts.Action);
            else if (stage == "backup") await catalog.RecordNamespaceReplacementBackupAsync(facts.Backup);
            else await catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write);
        });
        var observed = (await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!;
        Assert.IsNull(observed.Backup); Assert.IsNull(observed.WriteStart);
        CollectionAssert.AreEqual(new[] { intent.RootId }, (await catalog.ReadNamespaceRecoveryRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("role")]
    [DataRow("restore")]
    [DataRow("time")]
    public async Task ReplacementActionCannotSubstituteAdmittedOriginalFacts(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent); await catalog.RecordNamespaceReplacementStartAsync(facts.Start);
        var altered = changed switch
        {
            "binding" => facts.Action with { LocalObject = facts.Action.LocalObject with { LocalFileId = Guid.NewGuid() } },
            "owner" => facts.Action with { OwnerSid = "S-1-5-21-1-2-3-1002" },
            "role" => facts.Action with { RoleSid = "S-1-5-5-321-654" },
            "restore" => facts.Action with { RestoreDacl = "D:P(A;;FR;;;SY)" },
            "time" => facts.Action with { PreparedAt = facts.Start.StartedAt.AddTicks(-1) },
            _ => throw new AssertFailedException()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementActionAsync(altered));
        Assert.IsNull((await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!.Action);
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("action")]
    [DataRow("owner")]
    [DataRow("descriptor")]
    [DataRow("time")]
    public async Task ReplacementBackupMustDescribeTheSameRoleOnlyOriginalBeforeDataChanges(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent); await catalog.RecordNamespaceReplacementStartAsync(facts.Start);
        await catalog.PrepareNamespaceReplacementActionAsync(facts.Action);
        var altered = changed switch
        {
            "binding" => facts.Backup with { Verification = facts.Backup.Verification with { LocalObject = facts.Action.LocalObject with { LocalFileId = Guid.NewGuid() } } },
            "action" => facts.Backup with { ActionId = Guid.NewGuid() },
            "owner" => facts.Backup with { Verification = facts.Backup.Verification with { OwnerSid = "S-1-5-21-1-2-3-1002" } },
            "descriptor" => facts.Backup with { Verification = facts.Backup.Verification with { Dacl = facts.Action.RestoreDacl } },
            "time" => facts.Backup with { Verification = facts.Backup.Verification with { ObservedAt = facts.Action.PreparedAt.AddTicks(-1) } },
            _ => throw new AssertFailedException()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementBackupAsync(altered));
        Assert.IsNull((await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!.Backup);
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.RecordNamespaceReplacementBackupAsync(facts.Backup with { LinkCount = 2 }));
    }

    [TestMethod]
    public async Task ReplacementCannotRewriteFirstBackupOrItsWriteStartWithLaterBytes()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent); await catalog.RecordNamespaceReplacementStartAsync(facts.Start);
        await catalog.PrepareNamespaceReplacementActionAsync(facts.Action); await catalog.RecordNamespaceReplacementBackupAsync(facts.Backup);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementBackupAsync(facts.Backup with { Content = facts.Backup.Content with { Length = 13 } }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write with { ActionId = Guid.NewGuid() }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write with { StartedAt = facts.Backup.Verification.ObservedAt.AddTicks(-1) }));
        await catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write with { StartedAt = facts.Write.StartedAt.AddTicks(1) }));
        Assert.AreEqual(facts.Backup, (await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!.Backup);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.CancelUnstartedNamespaceReplacementAsync(new(1, intent.OperationId, facts.Write.StartedAt)));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("action-index")]
    [DataRow("missing-action")]
    [DataRow("missing-backup")]
    public async Task ReplacementExecutionDamageFailsClosedForStatusRecoveryAndDispatch(string damage)
    {
        using var fixture = new Fixture(); Guid id;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first); var intent = Replacement((await first.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
            id = intent.OperationId; var facts = ReplacementExecutionFacts(intent); await first.PrepareNamespaceReplacementAsync(intent);
            await first.RecordNamespaceReplacementStartAsync(facts.Start); await first.PrepareNamespaceReplacementActionAsync(facts.Action);
            await first.RecordNamespaceReplacementBackupAsync(facts.Backup); await first.RecordNamespaceReplacementWriteStartAsync(facts.Write);
        }
        await ExecuteSqlAsync(fixture.Paths, damage switch
        {
            "fingerprint" => "UPDATE namespace_replacement_execution_facts SET fingerprint=zeroblob(32) WHERE kind=2;",
            "action-index" => $"UPDATE namespace_replacement_execution_facts SET action_id='{Guid.NewGuid():D}' WHERE kind=2;",
            "missing-action" => "DELETE FROM namespace_replacement_execution_facts WHERE kind=1;",
            "missing-backup" => "DELETE FROM namespace_replacement_execution_facts WHERE kind=2;",
            _ => throw new AssertFailedException()
        });
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceReplacementExecutionAsync(id));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceRecoveryRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task ReplacementExecutionMigrationPreservesBeforeWriteFactsWithoutClaimingCompletion()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementIntent intent;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first); intent = Replacement((await first.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
            await first.PrepareNamespaceReplacementAsync(intent); await first.RecordNamespaceReplacementStartAsync(ReplacementExecutionFacts(intent).Start);
        }
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=42;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var restored = (await second.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!;
        Assert.AreEqual(intent, restored.Intent); Assert.IsNotNull(restored.Start);
        Assert.IsNull(restored.Action); Assert.IsNull(restored.Backup); Assert.IsNull(restored.WriteStart);
        CollectionAssert.AreEqual(new[] { intent.RootId }, (await second.ReadNamespaceRecoveryRootsAsync()).ToArray());
    }

    private static (MirrorPulseNamespaceReplacementStart Start, MirrorPulseNamespaceReplacementAction Action,
        MirrorPulseNamespaceReplacementBackup Backup, MirrorPulseNamespaceReplacementWriteStart Write) ReplacementExecutionFacts(MirrorPulseNamespaceReplacementIntent intent)
    {
        var start = new MirrorPulseNamespaceReplacementStart(1, intent.OperationId, intent.PreparedAt.AddSeconds(1));
        var original = intent.Original.Context;
        var roleOnly = new RawSecurityDescriptor($"D:P(A;;FA;;;{original.Protection.RoleSid})").GetSddlForm(AccessControlSections.Access);
        var action = new MirrorPulseNamespaceReplacementAction(1, intent.OperationId, Guid.NewGuid(), original.Member.LocalObject,
            original.Verification.OwnerSid, original.Protection.RoleSid, roleOnly, intent.Original.ExpectedDacl, start.StartedAt.AddSeconds(1));
        var backup = new MirrorPulseNamespaceReplacementBackup(1, intent.OperationId, action.ActionId,
            new(0, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData([]))),
            new(action.LocalObject, action.OwnerSid, action.RoleOnlyDacl, action.PreparedAt.AddSeconds(1)), 1);
        return (start, action, backup, new(1, intent.OperationId, action.ActionId, backup.Verification.ObservedAt.AddSeconds(1)));
    }

    [TestMethod]
    public async Task ReplacementBackupRetainsActualAutoInheritanceReadbackWithoutRewritingTheOriginalIntent()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        var facts = ReplacementExecutionFacts(intent); await catalog.PrepareNamespaceReplacementAsync(intent);
        await catalog.RecordNamespaceReplacementStartAsync(facts.Start); await catalog.PrepareNamespaceReplacementActionAsync(facts.Action);
        var descriptor = new RawSecurityDescriptor(facts.Action.RoleOnlyDacl);
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        string expectedReadback = descriptor.GetSddlForm(AccessControlSections.Access);
        var backup = facts.Backup with { Verification = facts.Backup.Verification with { Dacl = expectedReadback } };
        await catalog.RecordNamespaceReplacementBackupAsync(backup); await catalog.RecordNamespaceReplacementWriteStartAsync(facts.Write);
        var retained = (await catalog.ReadNamespaceReplacementExecutionAsync(intent.OperationId))!;
        Assert.AreEqual(expectedReadback, retained.Backup!.Verification.Dacl); Assert.AreEqual(facts.Action.RoleOnlyDacl, retained.Action!.RoleOnlyDacl);
        Assert.AreNotEqual(retained.Action.RoleOnlyDacl, retained.Backup.Verification.Dacl);
    }
}
