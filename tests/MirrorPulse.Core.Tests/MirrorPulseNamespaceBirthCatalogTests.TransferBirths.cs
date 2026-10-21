using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TransferTargetBirthRetainsDistinctIdentityAndBothFencesAcrossOwners(bool sameInstance)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferCopy admitted; MirrorPulseNamespaceTransferTargetBirth target;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var verified = await PrepareVerifiedTransferBirthAsync(first, sameInstance); target = TransferBirth(verified);
            admitted = await first.PrepareNamespaceTransferTargetBirthAsync(target);
            Assert.AreEqual(target, admitted.TargetBirth); Assert.AreEqual(target.Birth.OperationId, admitted.TargetBirthOperationId);
            Assert.AreEqual(target.Birth, await first.ReadNamespaceBirthAsync(target.Birth.OperationId));
            Assert.AreEqual(target.Plan, await first.ReadNamespaceBirthPlanAsync(target.Birth.OperationId));
            Assert.IsNull(await first.ReadNamespaceBirthStartAsync(target.Birth.OperationId));
            Assert.IsNull(await first.ReadNamespaceBirthObservationAsync(target.Birth.OperationId));
            Assert.IsNull(await first.ReadNamespaceMemberAsync(target.Birth.OperationId));
            Assert.HasCount(2, await first.ReadNamespaceDispatchFencedRootsAsync());
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(admitted, await second.ReadNamespaceTransferAsync(admitted.Plan.OperationId));
        Assert.AreEqual(admitted, await second.PrepareNamespaceTransferTargetBirthAsync(target));
        Assert.AreEqual(admitted.Plan.Source.Context.Member, await second.ReadNamespaceMemberAsync(admitted.Plan.Source.Context.Member.MemberId));
        Assert.IsNull(await second.ReadNamespaceMemberChildAsync(admitted.Plan.TargetParent.Context.Member.MemberId, "moved.txt"));
        Assert.IsNull(await second.ReadMutationAsync(admitted.Plan.DeleteOperationId));
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("copy-effect")]
    [DataRow("source-item")]
    [DataRow("root")]
    [DataRow("path")]
    [DataRow("parent")]
    [DataRow("role")]
    [DataRow("revision")]
    [DataRow("length")]
    [DataRow("hash")]
    [DataRow("verification")]
    [DataRow("chronology")]
    public async Task TransferTargetBirthRejectsSubstitutionWithoutPartialBirthAdmission(string change)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareVerifiedTransferBirthAsync(catalog); var target = TransferBirth(copy);
        var changed = change switch
        {
            "owner" => target with { OperationId = Guid.NewGuid() },
            "copy-effect" => target with { Birth = target.Birth with { OperationId = copy.Plan.CopyOperationId }, Plan = target.Plan with { OperationId = copy.Plan.CopyOperationId } },
            "source-item" => target with { Plan = target.Plan with { ItemId = copy.Plan.Source.Identity.ItemId!.Value } },
            "root" => target with { Birth = target.Birth with { RootId = copy.Plan.SourceRoute.RootId } },
            "path" => target with { Birth = target.Birth with { RelativePath = "Archive/another.txt" } },
            "parent" => target with { Birth = target.Birth with { ParentLocalObject = copy.Plan.SourceParent.Context.Member.LocalObject } },
            "role" => target with { Birth = target.Birth with { RoleSid = RelocatedRole } },
            "revision" => target with { Plan = target.Plan with { RemoteRevision = "another-target-version" } },
            "length" => target with { Plan = target.Plan with { Content = target.Plan.Content! with { Length = copy.Plan.Length + 1 } } },
            "hash" => target with { Plan = target.Plan with { Content = target.Plan.Content! with { Sha256 = new string('F', 64) } } },
            "verification" => target with { Birth = target.Birth with { TransferEntry = target.Birth.TransferEntry! with { TargetVerificationSha256 = new string('F', 64) } } },
            _ => target with { Birth = target.Birth with { PreparedAt = copy.Plan.PreparedAt } }
        };
        try { await catalog.PrepareNamespaceTransferTargetBirthAsync(changed); Assert.Fail("A substituted target was admitted."); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or FileNotFoundException) { }
        Assert.IsNull((await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId))!.TargetBirth);
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(target.Birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthPlanAsync(target.Birth.OperationId));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(target, (await catalog.PrepareNamespaceTransferTargetBirthAsync(target)).TargetBirth);
    }

    [TestMethod]
    public async Task TransferTargetBirthCannotBeAdmittedByTheGenericBirthApiOrReplaceItsFirstPlan()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var copy = await PrepareVerifiedTransferBirthAsync(catalog); var target = TransferBirth(copy);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(target.Birth));
        await catalog.PrepareNamespaceTransferTargetBirthAsync(target);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(target.Birth));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthPlanAsync(target.Plan with { ItemId = Guid.NewGuid() }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceTransferTargetBirthAsync(
            target with { Birth = target.Birth with { OperationId = Guid.NewGuid() } }));
        Assert.AreEqual(target, (await catalog.ReadNamespaceTransferAsync(copy.Plan.OperationId))!.TargetBirth);
    }

    [TestMethod]
    public async Task TransferTargetBirthRequiresWholeVerificationBeyondTheOriginalAcceptedUpload()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var plan = await PrepareAcceptedTransferTargetAsync(catalog, TransferBytes);
        var accepted = (await catalog.ReadNamespaceTransferAsync(plan.OperationId))!;
        var hypothetical = accepted with { TargetVerification = new(1, plan.OperationId, accepted.AcceptedRevision!, "target-id", plan.Length, plan.Sha256, plan.PreparedAt.AddSeconds(3)) };
        var target = TransferBirth(hypothetical);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceTransferTargetBirthAsync(target));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(target.Birth.OperationId));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task FailedTransferTargetTransactionRollsBackTheNameIdentityAndOwnerTogether()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferCopy copy; MirrorPulseNamespaceTransferTargetBirth target;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            copy = await PrepareVerifiedTransferBirthAsync(first); target = TransferBirth(copy);
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_transfer_birth BEFORE UPDATE ON namespace_transfers BEGIN SELECT RAISE(ABORT,'synthetic-target-birth-gap'); END;");
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => first.PrepareNamespaceTransferTargetBirthAsync(target));
            Assert.IsNull(await first.ReadNamespaceBirthAsync(target.Birth.OperationId));
            Assert.IsNull(await first.ReadNamespaceBirthPlanAsync(target.Birth.OperationId));
            Assert.AreEqual(copy, await first.ReadNamespaceTransferAsync(copy.Plan.OperationId));
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_transfer_birth;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(target, (await second.PrepareNamespaceTransferTargetBirthAsync(target)).TargetBirth);
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("mapping")]
    [DataRow("birth")]
    [DataRow("plan")]
    [DataRow("index")]
    [DataRow("owner")]
    public async Task CorruptTransferTargetOwnershipCannotBecomeAnUnownedGenericBirth(string missing)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferCopy copy; MirrorPulseNamespaceTransferTargetBirth target;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { copy = await PrepareVerifiedTransferBirthAsync(first); target = TransferBirth(copy); await first.PrepareNamespaceTransferTargetBirthAsync(target); }
        string sql = missing switch
        {
            "mapping" => "DELETE FROM namespace_transfer_target_births;",
            "birth" => "DELETE FROM namespace_birth_intents;",
            "plan" => "DELETE FROM namespace_birth_plans;",
            "index" => $"UPDATE namespace_transfer_target_births SET item_id='{Guid.NewGuid():D}';",
            _ => "DELETE FROM namespace_transfer_effects; DELETE FROM namespace_transfer_roots; DELETE FROM namespace_transfers;"
        };
        // Deliberately inject inconsistent durable files, bypassing FK protection only in this corruption fixture.
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA foreign_keys=OFF; " + sql);
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceDispatchFencedRootsAsync());
        if (missing != "birth") await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthAsync(target.Birth.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task Schema80UpgradeRetainsExactTransferPayloadAndCreatesNoTargetBirth()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferCopy copy; string original;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { copy = await PrepareVerifiedTransferBirthAsync(first); original = await ReadTransferPayloadHexAsync(fixture.Paths); }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_transfer_target_births; PRAGMA user_version=80;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(copy, await second.ReadNamespaceTransferAsync(copy.Plan.OperationId));
        Assert.AreEqual(original, await ReadTransferPayloadHexAsync(fixture.Paths));
        Assert.IsNull((await second.ReadNamespaceTransferAsync(copy.Plan.OperationId))!.TargetBirth);
        Assert.HasCount(2, await second.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task Schema81MissingTransferBirthTableCannotEraseOriginalOwnership()
    {
        using var fixture = new Fixture();
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
            await first.PrepareNamespaceTransferTargetBirthAsync(TransferBirth(await PrepareVerifiedTransferBirthAsync(first)));
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_transfer_target_births;");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseProductCatalog.OpenAsync(fixture.Paths));
    }

    [TestMethod]
    public async Task TransferTargetBirthRetainsARegisteredBornDirectoryAsItsExactParent()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareTransferFixtureAsync(catalog);
        var parent = await AddBornAsync(catalog, Birth(setup.Target, setup.TargetProof) with { IsDirectory = true, RelativePath = "Archive/folder" });
        var member = BornMember(parent, setup.Plan.TargetParent.Context.Member); await catalog.RegisterNamespaceMemberAsync(member);
        var snapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(member.MemberId))!;
        var plan = setup.Plan with { TargetParent = snapshot, TargetRelativePath = "Archive/folder/moved.txt", PreparedAt = member.RegisteredAt.AddSeconds(1) };
        await catalog.PrepareNamespaceTransferAsync(plan); await catalog.StartNamespaceTransferCopyAsync(plan.OperationId, plan.PreparedAt.AddSeconds(1));
        await catalog.RecordNamespaceTransferCopyAcceptedAsync(plan.OperationId, "accepted-target-v1", plan.PreparedAt.AddSeconds(2));
        var copy = await catalog.RecordNamespaceTransferTargetVerificationAsync(new(1, plan.OperationId, "accepted-target-v1", "original-target-id",
            plan.Length, plan.Sha256, plan.PreparedAt.AddSeconds(3)));
        var target = TransferBirth(copy); await catalog.PrepareNamespaceTransferTargetBirthAsync(target);
        Assert.AreEqual(new MirrorPulseNamespaceBornParent(parent.Birth.OperationId, parent.Protection.ProtectionId), target.Birth.BornParent);
        Assert.AreEqual(Guid.Empty, target.Birth.ParentEvidenceId); Assert.AreEqual(Guid.Empty, target.Birth.ParentPermissionOperationId);
        Assert.AreEqual(target.Birth, await catalog.ReadNamespaceBirthAsync(target.Birth.OperationId));
        Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow("plan")]
    [DataRow("owner-receipt")]
    [DataRow("pointer")]
    public async Task TransferTargetBirthRejectsRehashedButSubstitutedDurableFacts(string change)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceTransferCopy copy; MirrorPulseNamespaceTransferTargetBirth target;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { copy = await PrepareVerifiedTransferBirthAsync(first); target = TransferBirth(copy); await first.PrepareNamespaceTransferTargetBirthAsync(target); }
        if (change == "plan")
            await ReplacePayloadValueAsync(fixture.Paths, "namespace_birth_plans", "operation_id", target.Birth.OperationId, "accepted-target-v1", "another-target-v2");
        else if (change == "owner-receipt")
            await ReplacePayloadValueAsync(fixture.Paths, "namespace_transfer_target_births", "transfer_id", copy.Plan.OperationId,
                target.Birth.TransferEntry!.TargetVerificationSha256, new string('F', 64));
        else
            await ReplacePayloadValueAsync(fixture.Paths, "namespace_transfers", "operation_id", copy.Plan.OperationId,
                target.Birth.OperationId.ToString("D"), Guid.NewGuid().ToString("D"));
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceTransferAsync(copy.Plan.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthAsync(target.Birth.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthPlanAsync(target.Birth.OperationId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    private static async Task<MirrorPulseNamespaceTransferCopy> PrepareVerifiedTransferBirthAsync(MirrorPulseProductCatalog catalog, bool sameInstance = false)
    {
        var plan = await PrepareAcceptedTransferTargetAsync(catalog, TransferBytes, sameInstance);
        return await catalog.RecordNamespaceTransferTargetVerificationAsync(new(1, plan.OperationId, "accepted-target-v1", "original-target-id",
            plan.Length, plan.Sha256, plan.PreparedAt.AddSeconds(3)));
    }

    private static MirrorPulseNamespaceTransferTargetBirth TransferBirth(MirrorPulseNamespaceTransferCopy copy)
    {
        var parent = copy.Plan.TargetParent; Guid birthId = Guid.NewGuid(); var time = copy.TargetVerification!.VerifiedAt.AddSeconds(1);
        var birth = new MirrorPulseNamespaceBirthIntent(4, birthId, copy.Plan.TargetRoute.RootId,
            parent.BornParent is null ? parent.Context.Member.OriginId : Guid.Empty,
            parent.BornParent is null ? parent.PermissionOperationId : Guid.Empty, parent.Context.Member.LocalObject,
            copy.Plan.TargetRelativePath, false, MirrorPulseNamespaceBirthOrigin.TransferTarget, parent.Context.Protection.RoleSid, time)
        { BornParent = parent.BornParent, TransferEntry = MirrorPulseNamespaceTransferBirthReference.Create(copy) };
        // Opaque SDK identity is a catalog premise; the Host must select it through the public root router.
        var plan = new MirrorPulseNamespaceBirthPlan(2, birthId, Guid.NewGuid(), "target-scoped:original-target-id", copy.AcceptedRevision, time)
        { Content = new(copy.Plan.Length, copy.Plan.Sha256) };
        return new(1, copy.Plan.OperationId, birth, plan);
    }

    private static async Task<string> ReadTransferPayloadHexAsync(MirrorPulseStoragePaths paths)
    {
        await using var connection = Connection(paths); await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "SELECT hex(payload)||hex(fingerprint) FROM namespace_transfers;";
        return (string)(await command.ExecuteScalarAsync())!;
    }
}
