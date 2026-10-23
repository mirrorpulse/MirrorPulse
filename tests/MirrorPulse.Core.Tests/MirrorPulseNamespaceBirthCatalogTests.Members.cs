using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation, true)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import, true)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation, true)]
    public async Task VerifiedBirthTransfersItsReservationToStableMembershipAcrossOwners(MirrorPulseNamespaceBirthOrigin origin, bool directory)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProof = Protection(parent);
        var birth = Birth(parent, parentProof) with { Origin = origin, IsDirectory = directory };
        MirrorPulseNamespaceMemberRegistration member;
        MirrorPulseNamespaceMemberRegistration parentMember;
        MirrorPulseNamespaceBirthObservation observation;
        MirrorPulseNamespaceBirthProtection protection;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, parent, parentProof);
            parentMember = await RegisterOriginalParentMembersAsync(first, parent, parentProof);
            var born = await AddBornAsync(first, birth);
            observation = born.Observation; protection = born.Protection;
            member = BornMember(born, parentMember);
            Assert.AreEqual(member, await first.RegisterNamespaceMemberAsync(member));
            var entry = (await first.ReadNamespaceBirthRecoveryPageAsync(await first.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single();
            Assert.IsFalse(entry.OwnsNameReservation);
            Assert.AreEqual(member, entry.MemberRegistration);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(member, await second.ReadNamespaceMemberAsync(member.MemberId));
        Assert.AreEqual(member, await second.RegisterNamespaceMemberAsync(member));
        Assert.AreEqual(member, await second.ReadNamespaceMemberChildAsync(parentMember.MemberId, "NEW.TXT"));
        Assert.AreEqual(observation, await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.AreEqual(protection, await second.ReadLatestNamespaceBirthProtectionAsync(birth.OperationId));
        Assert.AreEqual(parent, await second.ReadNamespacePermissionBaselineAsync(parent.EvidenceId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespaceBirthAsync(birth with
        { OperationId = Guid.NewGuid(), RelativePath = "Docs/NEW.TXT" }));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task OriginalMembershipKeepsPreProtectionEvidenceAndBlocksNewBirthAtTheOccupiedName()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var parentProof = Protection(parent);
        var original = parent with
        {
            EvidenceId = Guid.NewGuid(),
            LocalObject = parent.LocalObject with { LocalFileId = Guid.NewGuid() },
            RelativePath = "Docs/existing.txt",
            IsDirectory = false
        };
        var originalProof = Protection(original);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, parentProof);
        var parentMember = await RegisterOriginalParentMembersAsync(catalog, parent, parentProof);
        await ProtectAsync(catalog, original, originalProof);
        var member = OriginalMember(original, originalProof, parentMember.MemberId);
        Assert.AreEqual(member, await catalog.RegisterNamespaceMemberAsync(member));
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberChildAsync(parentMember.MemberId, "EXISTING.TXT"));
        var collision = Birth(parent, parentProof) with { RelativePath = "Docs/Existing.txt" };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(collision));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(collision.OperationId));
        var other = collision with { OperationId = Guid.NewGuid(), RelativePath = "Docs/other.txt" };
        Assert.AreEqual(other, await catalog.PrepareNamespaceBirthAsync(other));
    }

    [TestMethod]
    public async Task NestedBornParentsKeepTheirOwnNamesAfterPendingReservationsAreTransferred()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var originalProof = Protection(original);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, originalProof);
        var originalMember = await RegisterOriginalParentMembersAsync(catalog, original, originalProof);
        var parent = await AddBornAsync(catalog, Birth(original, originalProof) with { IsDirectory = true, RelativePath = "Docs/born" });
        var parentMember = BornMember(parent, originalMember);
        await catalog.RegisterNamespaceMemberAsync(parentMember);
        var child = await AddBornAsync(catalog, NestedBirth(parent, "leaf.bin", false));
        var childMember = BornMember(child, parentMember);
        Assert.AreEqual(childMember, await catalog.RegisterNamespaceMemberAsync(childMember));
        Assert.AreEqual(childMember, await catalog.ReadNamespaceMemberChildAsync(parentMember.MemberId, "LEAF.BIN"));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(
            NestedBirth(parent, "LEAF.BIN", false)));
        var nextEpoch = parent.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = parent.Protection.ProtectionId,
            Verification = parent.Protection.Verification with { ObservedAt = childMember.RegisteredAt.AddSeconds(1) }
        };
        Assert.AreEqual(nextEpoch, await catalog.RecordNamespaceBirthProtectionAsync(nextEpoch));
        Assert.AreEqual(parentMember, await catalog.ReadNamespaceMemberAsync(parentMember.MemberId));
        Assert.AreEqual(childMember, await catalog.RegisterNamespaceMemberAsync(childMember));
        var entries = (await catalog.ReadNamespaceBirthRecoveryPageAsync(await catalog.BeginNamespaceBirthRecoveryScanAsync(), 0, 8)).Entries;
        Assert.HasCount(2, entries); Assert.IsTrue(entries.All(entry => !entry.OwnsNameReservation && entry.MemberRegistration is not null));
    }

    [TestMethod]
    public async Task ReservationReleaseFailureRollsBackMemberAndOccupiedNameBeforeRestart()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent);
        MirrorPulseNamespaceMemberRegistration member;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, parent, proof);
            var parentMember = await RegisterOriginalParentMembersAsync(first, parent, proof);
            var born = await AddBornAsync(first, Birth(parent, proof)); member = BornMember(born, parentMember);
            await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_member_transfer BEFORE DELETE ON namespace_birth_reservations BEGIN SELECT RAISE(ABORT,'test transfer interruption'); END;");
            await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => first.RegisterNamespaceMemberAsync(member));
            Assert.IsNull(await first.ReadNamespaceMemberAsync(member.MemberId));
            Assert.IsNull(await first.ReadNamespaceMemberChildAsync(parentMember.MemberId, "new.txt"));
            Assert.IsTrue((await first.ReadNamespaceBirthRecoveryPageAsync(await first.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single().OwnsNameReservation);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_member_transfer;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(member, await second.RegisterNamespaceMemberAsync(member));
        Assert.IsFalse((await second.ReadNamespaceBirthRecoveryPageAsync(await second.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single().OwnsNameReservation);
    }

    [TestMethod]
    public async Task ConcurrentMemberIdsCannotAdoptOneBirthAndTheLosingInsertRollsBack()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, proof);
        var parentMember = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        var born = await AddBornAsync(catalog, Birth(parent, proof)); var member = BornMember(born, parentMember);
        var other = member with { MemberId = Guid.NewGuid() };
        var results = await Task.WhenAll(AttemptMemberAsync(catalog, member), AttemptMemberAsync(catalog, other));
        Assert.HasCount(1, results.OfType<MirrorPulseNamespaceMemberRegistration>());
        var winner = results.Single(result => result is not null)!;
        Assert.AreEqual(winner, await catalog.ReadNamespaceMemberChildAsync(parentMember.MemberId, "new.txt"));
        var loser = winner == member ? other : member;
        Assert.IsNull(await catalog.ReadNamespaceMemberAsync(loser.MemberId));

        static async Task<MirrorPulseNamespaceMemberRegistration?> AttemptMemberAsync(MirrorPulseProductCatalog owner, MirrorPulseNamespaceMemberRegistration candidate)
        {
            try { return await owner.RegisterNamespaceMemberAsync(candidate); }
            catch (InvalidOperationException) { return null; }
            catch (Microsoft.Data.Sqlite.SqliteException) { return null; }
        }
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("owner")]
    [DataRow("root")]
    [DataRow("path")]
    [DataRow("kind")]
    [DataRow("time")]
    public async Task BornMembershipRejectsChangedFirstFactsAndRetainsItsReservation(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, proof);
        var parentMember = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        var born = await AddBornAsync(catalog, Birth(parent, proof)); var member = BornMember(born, parentMember);
        var wrong = scenario switch
        {
            "binding" => member with { LocalObject = member.LocalObject with { LocalFileId = Guid.NewGuid() } },
            "owner" => member with { OwnerSid = "S-1-5-21-100-200-300-1002" },
            "root" => member with { RootId = RootId.New() },
            "path" => member with { RelativePath = "Docs/other.txt" },
            "kind" => member with { IsDirectory = true },
            _ => member with { RegisteredAt = born.Protection.Verification.ObservedAt.AddTicks(-1) },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RegisterNamespaceMemberAsync(wrong));
        Assert.IsNull(await catalog.ReadNamespaceMemberAsync(member.MemberId));
        Assert.IsTrue((await catalog.ReadNamespaceBirthRecoveryPageAsync(await catalog.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single().OwnsNameReservation);
    }

    [TestMethod]
    [DataRow("missing-observation")]
    [DataRow("missing-protection")]
    [DataRow("missing-parent")]
    [DataRow("missing-reservation")]
    public async Task UnfinishedBirthCannotBePromotedByInventingMembership(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent); var birth = Birth(parent, proof);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, proof);
        var parentMember = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        var plan = Plan(birth); var start = Start(plan); var observation = Observation(birth, plan, start);
        var protection = BirthProtection(observation, proof);
        await catalog.PrepareNamespaceBirthAsync(birth); await catalog.PrepareNamespaceBirthPlanAsync(plan); await catalog.RecordNamespaceBirthStartAsync(start);
        await catalog.PrepareNamespaceBirthConversionAsync(new(1, birth.OperationId, observation.LocalObject, birth.IsDirectory,
            observation.OwnerSid, observation.BirthDacl, observation.BirthDacl, 1, start.StartedAt.AddMilliseconds(100)));
        if (scenario != "missing-observation") await catalog.RecordNamespaceBirthObservationAsync(observation);
        if (scenario is "missing-parent" or "missing-reservation") await catalog.RecordNamespaceBirthProtectionAsync(protection);
        var member = BornMember(new(birth, plan, start, observation, protection), parentMember);
        if (scenario == "missing-parent") member = member with { ParentMemberId = Guid.NewGuid() };
        if (scenario == "missing-reservation") await ExecuteSqlAsync(fixture.Paths, "DELETE FROM namespace_birth_reservations;");
        if (scenario == "missing-reservation") await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RegisterNamespaceMemberAsync(member));
        else await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.RegisterNamespaceMemberAsync(member));
        Assert.IsNull(await catalog.ReadNamespaceMemberAsync(member.MemberId));
    }

    [TestMethod]
    public async Task HistoricalMembershipReplaySurvivesRoleRotationButNewStaleRegistrationIsRejected()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, proof);
        var member = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        var rotation = proof with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-123-458",
            ExpectedDacl = proof.TargetDacl,
            TargetDacl = proof.TargetDacl.Replace(Role, "S-1-5-5-123-458", StringComparison.Ordinal),
            PreparedAt = member.RegisteredAt.AddSeconds(1)
        };
        await ProtectAsync(catalog, parent, rotation);
        Assert.AreEqual(member, await catalog.RegisterNamespaceMemberAsync(member));
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RegisterNamespaceMemberAsync(member with
        { MemberId = Guid.NewGuid(), RegisteredAt = rotation.PreparedAt.AddSeconds(3) }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RegisterNamespaceMemberAsync(member with
        { ProtectionId = rotation.OperationId }));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("id")]
    [DataRow("origin")]
    [DataRow("origin-id")]
    [DataRow("proof-id")]
    [DataRow("self-parent")]
    [DataRow("empty-parent")]
    [DataRow("orphan")]
    [DataRow("root")]
    [DataRow("scope")]
    [DataRow("path")]
    public async Task InvalidMemberProvenanceFailsBeforeAdmission(string scenario)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var proof = Protection(original);
        var member = OriginalMember(original, proof, Guid.NewGuid());
        var wrong = scenario switch
        {
            "version" => member with { Version = 2 },
            "id" => member with { MemberId = Guid.Empty },
            "origin" => member with { Origin = (MirrorPulseNamespaceMemberOrigin)77 },
            "origin-id" => member with { OriginId = Guid.Empty },
            "proof-id" => member with { ProtectionId = Guid.Empty },
            "self-parent" => member with { ParentMemberId = member.MemberId },
            "empty-parent" => member with { ParentMemberId = Guid.Empty },
            "orphan" => member with { ParentMemberId = null },
            "root" => member with { RootId = null },
            "scope" => member with { LocalObject = member.LocalObject with { LocalFileId = member.LocalObject.SyncRootFileId } },
            _ => member with { RelativePath = "Docs/../wrong" },
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.RegisterNamespaceMemberAsync(wrong));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("index")]
    [DataRow("canonical")]
    public async Task MemberReaderRejectsCorruptIndexesAndNoncanonicalHistory(string scenario)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent);
        MirrorPulseNamespaceMemberRegistration member;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, parent, proof);
            member = await RegisterOriginalParentMembersAsync(first, parent, proof);
        }
        await using (var connection = Connection(fixture.Paths))
        {
            await connection.OpenAsync(); await using var command = connection.CreateCommand();
            command.CommandText = scenario switch
            {
                "fingerprint" => "UPDATE namespace_members SET fingerprint=zeroblob(32) WHERE member_id=$id;",
                "index" => "UPDATE namespace_members SET local_file_id='00000000-0000-0000-0000-000000000001' WHERE member_id=$id;",
                _ => "UPDATE namespace_members SET payload=$payload,fingerprint=$fingerprint WHERE member_id=$id;",
            };
            command.Parameters.AddWithValue("$id", member.MemberId.ToString("D"));
            if (scenario == "canonical")
            {
                byte[] payload = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(member, BirthProtectionJsonOptions) + " ");
                command.Parameters.AddWithValue("$payload", payload); command.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            }
            await command.ExecuteNonQueryAsync();
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceMemberAsync(member.MemberId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.RegisterNamespaceMemberAsync(member));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OccupiedNameReaderRejectsIndexesThatDisagreeWithProvenance(bool wrongParent)
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, proof);
        var member = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        await ExecuteSqlAsync(fixture.Paths, wrongParent
            ? $"UPDATE namespace_member_names SET parent_member_id='{member.MemberId:D}';"
            : "UPDATE namespace_member_names SET child_name_key='OTHER';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberChildAsync(
            wrongParent ? member.MemberId : member.ParentMemberId!.Value, wrongParent ? "Docs" : "other"));
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberAsync(member.MemberId));
    }

    [TestMethod]
    public async Task OriginalRegistrationCannotStealAnUnresolvedBirthName()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent); var birth = Birth(parent, proof);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, proof);
        var parentMember = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        await catalog.PrepareNamespaceBirthAsync(birth);
        var original = parent with
        {
            EvidenceId = Guid.NewGuid(),
            IsDirectory = false,
            RelativePath = "Docs/NEW.TXT",
            LocalObject = parent.LocalObject with { LocalFileId = Guid.NewGuid() }
        };
        var originalProof = Protection(original); await ProtectAsync(catalog, original, originalProof);
        var member = OriginalMember(original, originalProof, parentMember.MemberId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RegisterNamespaceMemberAsync(member));
        Assert.IsNull(await catalog.ReadNamespaceMemberAsync(member.MemberId));
        var entry = (await catalog.ReadNamespaceBirthRecoveryPageAsync(await catalog.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single();
        Assert.IsTrue(entry.OwnsNameReservation); Assert.IsNull(entry.MemberRegistration);
    }

    [TestMethod]
    public async Task TwoOriginalObjectsCannotOccupyTheSameCaseInsensitiveMemberName()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, proof);
        var parentMember = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        var original = parent with
        {
            EvidenceId = Guid.NewGuid(),
            IsDirectory = false,
            RelativePath = "Docs/existing.txt",
            LocalObject = parent.LocalObject with { LocalFileId = Guid.NewGuid() }
        };
        var originalProof = Protection(original); await ProtectAsync(catalog, original, originalProof);
        var member = OriginalMember(original, originalProof, parentMember.MemberId); await catalog.RegisterNamespaceMemberAsync(member);
        var other = original with
        {
            EvidenceId = Guid.NewGuid(),
            RelativePath = "Docs/EXISTING.TXT",
            LocalObject = original.LocalObject with { LocalFileId = Guid.NewGuid() }
        };
        var otherProof = Protection(other); await ProtectAsync(catalog, other, otherProof);
        var collision = OriginalMember(other, otherProof, parentMember.MemberId);
        await Assert.ThrowsExactlyAsync<Microsoft.Data.Sqlite.SqliteException>(() => catalog.RegisterNamespaceMemberAsync(collision));
        Assert.IsNull(await catalog.ReadNamespaceMemberAsync(collision.MemberId));
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberChildAsync(parentMember.MemberId, "existing.txt"));
    }

    [TestMethod]
    public async Task ActiveParentCaptureRejectsNewMembershipWithoutChangingBirthHistory()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, proof);
        var parentMember = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        var born = await AddBornAsync(catalog, Birth(parent, proof)); var member = BornMember(born, parentMember);
        var rotation = proof with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-321-654",
            ExpectedDacl = proof.TargetDacl,
            TargetDacl = proof.TargetDacl.Replace(Role, "S-1-5-5-321-654", StringComparison.Ordinal),
            PreparedAt = member.RegisteredAt.AddSeconds(1)
        };
        await catalog.CreateNamespacePermissionTreeAsync(new(Guid.NewGuid(), new(parent, rotation), 1, member.RegisteredAt));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RegisterNamespaceMemberAsync(member));
        Assert.IsNull(await catalog.ReadNamespaceMemberAsync(member.MemberId));
        var entry = (await catalog.ReadNamespaceBirthRecoveryPageAsync(await catalog.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single();
        Assert.AreEqual(born.Protection, entry.LatestProtection); Assert.IsTrue(entry.OwnsNameReservation);
    }

    [TestMethod]
    public async Task Schema34MigrationPreservesBirthFactsWithoutBackfillingMembershipOrReleasingNames()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent); var birth = Birth(parent, proof);
        BornObject born;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { await ProtectAsync(first, parent, proof); born = await AddBornAsync(first, birth); }
        string originalBytes = await ReadMemberMigrationBytesAsync(fixture.Paths);
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_member_names; DROP TABLE namespace_members; PRAGMA user_version=34;");
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(originalBytes, await ReadMemberMigrationBytesAsync(fixture.Paths));
        var entry = (await current.ReadNamespaceBirthRecoveryPageAsync(await current.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single();
        Assert.IsNull(entry.MemberRegistration); Assert.IsTrue(entry.OwnsNameReservation);
        Assert.AreEqual(born.Protection, entry.LatestProtection);
        await using var connection = Connection(fixture.Paths); await connection.OpenAsync();
        await using var query = connection.CreateCommand(); query.CommandText = "PRAGMA user_version;";
        Assert.AreEqual(86L, await query.ExecuteScalarAsync());
        query.CommandText = "SELECT COUNT(*) FROM namespace_members;"; Assert.AreEqual(0L, await query.ExecuteScalarAsync());
        query.CommandText = "PRAGMA foreign_key_check;"; Assert.IsNull(await query.ExecuteScalarAsync());
    }

    [TestMethod]
    public async Task CanceledRegistrationLeavesTheOriginalBirthReservationAndNoMember()
    {
        using var fixture = new Fixture();
        var parent = Baseline(); var proof = Protection(parent);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, parent, proof);
        var parentMember = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        var born = await AddBornAsync(catalog, Birth(parent, proof)); var member = BornMember(born, parentMember);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => catalog.RegisterNamespaceMemberAsync(member, cancellation.Token));
        Assert.IsNull(await catalog.ReadNamespaceMemberAsync(member.MemberId));
        Assert.IsTrue((await catalog.ReadNamespaceBirthRecoveryPageAsync(await catalog.BeginNamespaceBirthRecoveryScanAsync(), 0, 1)).Entries.Single().OwnsNameReservation);
    }

    private static MirrorPulseNamespaceMemberRegistration OriginalMember(MirrorPulseNamespacePermissionBaseline original,
        MirrorPulseNamespacePermissionIntent protection, Guid? parentId) => new(1, Guid.NewGuid(), parentId,
            MirrorPulseNamespaceMemberOrigin.CapturedOriginal, original.EvidenceId, protection.OperationId,
            protection.RootId, protection.RelativePath, original.LocalObject, original.IsDirectory, original.OwnerSid, protection.PreparedAt.AddSeconds(3));

    private static MirrorPulseNamespaceMemberRegistration BornMember(BornObject born, MirrorPulseNamespaceMemberRegistration parent) =>
        new(1, Guid.NewGuid(), parent.MemberId, MirrorPulseNamespaceMemberOrigin.ProtectedBirth, born.Birth.OperationId,
            born.Protection.ProtectionId, born.Birth.RootId, born.Birth.RelativePath, born.Observation.LocalObject,
            born.Birth.IsDirectory, born.Observation.OwnerSid, born.Protection.Verification.ObservedAt.AddSeconds(1));

    private static async Task<MirrorPulseNamespaceMemberRegistration> RegisterOriginalParentMembersAsync(MirrorPulseProductCatalog catalog,
        MirrorPulseNamespacePermissionBaseline parent, MirrorPulseNamespacePermissionIntent proof)
    {
        var anchor = parent with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = null,
            RelativePath = string.Empty,
            LocalObject = parent.LocalObject with { LocalFileId = parent.LocalObject.SyncRootFileId },
            CapturedAt = parent.CapturedAt.AddSeconds(-10)
        };
        var anchorProof = Protection(anchor);
        await ProtectAsync(catalog, anchor, anchorProof);
        var anchorMember = OriginalMember(anchor, anchorProof, null);
        await catalog.RegisterNamespaceMemberAsync(anchorMember);
        var parentMember = OriginalMember(parent, proof, anchorMember.MemberId);
        return await catalog.RegisterNamespaceMemberAsync(parentMember);
    }

    private static async Task<string> ReadMemberMigrationBytesAsync(MirrorPulse.Core.Configuration.MirrorPulseStoragePaths paths)
    {
        await using var connection = Connection(paths); await connection.OpenAsync(); await using var query = connection.CreateCommand();
        query.CommandText = "SELECT hex(payload)||':'||hex(fingerprint) FROM namespace_birth_intents UNION ALL SELECT hex(payload)||':'||hex(fingerprint) FROM namespace_birth_observations UNION ALL SELECT hex(payload)||':'||hex(fingerprint) FROM namespace_birth_protections;";
        await using var reader = await query.ExecuteReaderAsync(); var rows = new List<string>();
        while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        return string.Join('\n', rows);
    }
}
