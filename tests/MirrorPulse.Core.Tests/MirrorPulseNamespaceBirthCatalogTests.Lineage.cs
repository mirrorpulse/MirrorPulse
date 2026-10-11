using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation)]
    public async Task BornDirectoriesAdmitNestedNativeFactsAcrossOwnersWithoutInventingOriginals(MirrorPulseNamespaceBirthOrigin origin)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var originalProtection = Protection(original);
        BornObject parent; BornObject nested; BornObject leaf;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, originalProtection);
            parent = await AddBornAsync(first, Birth(original, originalProtection) with { RelativePath = "Docs/A", IsDirectory = true });
            nested = await AddBornAsync(first, NestedBirth(parent, "B", true));
            leaf = await AddBornAsync(first, NestedBirth(nested, "leaf.txt", false) with { Origin = origin });
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        foreach (var item in new[] { parent, nested, leaf })
        {
            Assert.AreEqual(item.Birth, await second.ReadNamespaceBirthAsync(item.Birth.OperationId));
            Assert.AreEqual(item.Birth, await second.PrepareNamespaceBirthAsync(item.Birth));
            Assert.AreEqual(item.Observation, await second.ReadNamespaceBirthObservationAsync(item.Birth.OperationId));
            Assert.AreEqual(item.Protection, await second.ReadLatestNamespaceBirthProtectionAsync(item.Birth.OperationId));
            Assert.AreEqual(item.Protection, await second.RecordNamespaceBirthProtectionAsync(item.Protection));
            Assert.IsNull(await second.ReadNamespacePermissionBaselineAsync(item.Birth.OperationId));
        }
        Assert.AreEqual(Guid.Empty, leaf.Birth.ParentEvidenceId);
        Assert.AreEqual(nested.Birth.OperationId, leaf.Birth.BornParent!.BirthOperationId);
        Assert.AreEqual(nested.Observation.LocalObject, leaf.Birth.ParentLocalObject);
        Assert.AreEqual(original, await second.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
        Assert.HasCount(1, await second.ReadNamespacePermissionChangesAsync());
        var scan = await second.BeginNamespaceBirthRecoveryScanAsync();
        long after = 0;
        foreach (var item in new[] { parent, nested, leaf })
        {
            var page = await second.ReadNamespaceBirthRecoveryPageAsync(scan, after, 1);
            var entry = page.Entries.Single();
            Assert.AreEqual(item.Birth, entry.Intent);
            Assert.AreEqual(item.Observation, entry.Observation);
            Assert.AreEqual(item.Protection, entry.LatestProtection);
            Assert.IsTrue(entry.OwnsNameReservation);
            after = page.LastScannedSequence;
        }
        Assert.IsFalse((await second.ReadNamespaceBirthRecoveryPageAsync(scan, after, 1)).HasMore);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task EqualNamesBelongToTheirDirectBornParentAndCollisionsRollbackAcrossOwners()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var originalProtection = Protection(original);
        BornObject left; BornObject right; MirrorPulseNamespaceBirthIntent child;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, originalProtection);
            left = await AddBornAsync(first, Birth(original, originalProtection) with { RelativePath = "Docs/A", IsDirectory = true });
            right = await AddBornAsync(first, Birth(original, originalProtection) with { RelativePath = "Docs/B", IsDirectory = true });
            child = NestedBirth(left, "same.txt", false);
            await first.PrepareNamespaceBirthAsync(child);
            await first.PrepareNamespaceBirthAsync(NestedBirth(right, "same.txt", false));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var collision = child with { OperationId = Guid.NewGuid(), RelativePath = "Docs/A/SAME.TXT" };
        var error = await Assert.ThrowsExactlyAsync<SqliteException>(() => second.PrepareNamespaceBirthAsync(collision));
        Assert.AreEqual(19, error.SqliteErrorCode);
        Assert.IsNull(await second.ReadNamespaceBirthAsync(collision.OperationId));
        Assert.AreEqual(child, await second.ReadNamespaceBirthAsync(child.OperationId));
    }

    [TestMethod]
    [DataRow("file")]
    [DataRow("binding")]
    [DataRow("root")]
    [DataRow("path")]
    [DataRow("role")]
    [DataRow("time")]
    [DataRow("other-proof")]
    public async Task NestedAdmissionRejectsAnUnrelatedDirectParent(string scenario)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var originalProtection = Protection(original);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, originalProtection);
        var parent = await AddBornAsync(catalog, Birth(original, originalProtection) with { IsDirectory = scenario != "file", RelativePath = "Docs/A" });
        var other = await AddBornAsync(catalog, Birth(original, originalProtection) with { IsDirectory = true, RelativePath = "Docs/B" });
        var child = NestedBirth(parent, "child.txt", false);
        child = scenario switch
        {
            "binding" => child with { ParentLocalObject = other.Observation.LocalObject },
            "root" => child with { RootId = RootId.New() },
            "path" => child with { RelativePath = "Docs/A/missing/child.txt" },
            "role" => child with { RoleSid = "S-1-5-5-123-457" },
            "time" => child with { PreparedAt = parent.Observation.ObservedAt },
            "other-proof" => child with { BornParent = child.BornParent! with { ProtectionId = other.Protection.ProtectionId } },
            _ => child,
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(child));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(child.OperationId));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("mixed")]
    [DataRow("self")]
    [DataRow("empty")]
    public async Task NestedAdmissionRequiresAnExplicitExclusiveParentKind(string scenario)
    {
        using var fixture = new Fixture();
        var child = new MirrorPulseNamespaceBirthIntent(2, Guid.NewGuid(), RootId.New(), Guid.Empty, Guid.Empty,
            new(123, Guid.NewGuid(), Guid.NewGuid()), "Docs/A/child.txt", false, MirrorPulseNamespaceBirthOrigin.ControlledCreation, Role, DateTimeOffset.UtcNow)
        { BornParent = new(Guid.NewGuid(), Guid.NewGuid()) };
        child = scenario switch
        {
            "version" => child with { Version = 1 },
            "mixed" => child with { ParentEvidenceId = Guid.NewGuid() },
            "self" => child with { BornParent = child.BornParent! with { BirthOperationId = child.OperationId } },
            _ => child with { BornParent = child.BornParent! with { ProtectionId = Guid.Empty } },
        };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceBirthAsync(child));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(child.OperationId));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NestedAdmissionRequiresActualObservationAndSeparateProtection(bool observed)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var permission = Protection(original);
        var parent = Birth(original, permission) with { IsDirectory = true, RelativePath = "Docs/A" };
        var plan = Plan(parent); var start = Start(plan); var observation = Observation(parent, plan, start);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await PrepareStartedBirthAsync(catalog, original, permission, parent, plan, start);
        if (observed) await catalog.RecordNamespaceBirthObservationAsync(observation);
        var parentObject = new BornObject(parent, plan, start, observation, BirthProtection(observation, permission));
        var child = NestedBirth(parentObject, "child.txt", false);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespaceBirthAsync(child));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(child.OperationId));
    }

    [TestMethod]
    public async Task RotationFencesStaleBornAncestryAndPreservesEveryHistoricalAdmissionAndEpoch()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var permission = Protection(original);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, permission);
        var first = await AddBornAsync(catalog, Birth(original, permission) with { IsDirectory = true, RelativePath = "Docs/A" });
        var nested = await AddBornAsync(catalog, NestedBirth(first, "B", true));
        var pending = NestedBirth(nested, "pending.txt", false);
        await catalog.PrepareNamespaceBirthAsync(pending);
        var rotation = permission with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            RoleSid = "S-1-5-5-123-457",
            ExpectedDacl = permission.TargetDacl,
            TargetDacl = Protected.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal),
            PreparedAt = pending.PreparedAt.AddSeconds(1),
        };
        await ProtectAsync(catalog, original, rotation);
        Assert.AreEqual(pending, await catalog.PrepareNamespaceBirthAsync(pending));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthPlanAsync(Plan(pending)));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(NestedBirth(nested, "stale.txt", false)));
        var firstEpoch = first.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = first.Protection.ProtectionId,
            ParentPermissionOperationId = rotation.OperationId,
            RoleSid = rotation.RoleSid,
            Verification = first.Protection.Verification with { Dacl = rotation.TargetDacl, ObservedAt = rotation.PreparedAt.AddSeconds(3) },
        };
        await catalog.RecordNamespaceBirthProtectionAsync(firstEpoch);
        var nestedEpoch = nested.Protection with
        {
            ProtectionId = Guid.NewGuid(),
            PreviousProtectionId = nested.Protection.ProtectionId,
            BornParent = new(first.Birth.OperationId, firstEpoch.ProtectionId),
            RoleSid = rotation.RoleSid,
            Verification = nested.Protection.Verification with { Dacl = rotation.TargetDacl, ObservedAt = firstEpoch.Verification.ObservedAt.AddSeconds(1) },
        };
        await catalog.RecordNamespaceBirthProtectionAsync(nestedEpoch);
        var current = nested with { Protection = nestedEpoch };
        var next = NestedBirth(current, "next.txt", false);
        Assert.AreEqual(next, await catalog.PrepareNamespaceBirthAsync(next));
        var collision = next with { OperationId = Guid.NewGuid(), RelativePath = pending.RelativePath };
        var error = await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceBirthAsync(collision));
        Assert.AreEqual(19, error.SqliteErrorCode);
        foreach (var item in new[] { first, nested })
        {
            Assert.AreEqual(item.Birth, await catalog.ReadNamespaceBirthAsync(item.Birth.OperationId));
            Assert.AreEqual(item.Observation, await catalog.ReadNamespaceBirthObservationAsync(item.Birth.OperationId));
            Assert.AreEqual(item.Protection, await catalog.RecordNamespaceBirthProtectionAsync(item.Protection));
        }
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionBaselineAsync(original.EvidenceId));
    }

    [TestMethod]
    public async Task CapturingOriginalAncestorFencesNewBornDescendants()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var permission = Protection(original);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, permission);
        var parent = await AddBornAsync(catalog, Birth(original, permission) with { IsDirectory = true, RelativePath = "Docs/A" });
        var next = permission with
        {
            OperationId = Guid.NewGuid(),
            Kind = MirrorPulseNamespacePermissionChangeKind.RotateRole,
            ExpectedDacl = permission.TargetDacl,
            RoleSid = "S-1-5-5-123-457",
            TargetDacl = Protected.Replace(Role, "S-1-5-5-123-457", StringComparison.Ordinal),
            PreparedAt = parent.Protection.Verification.ObservedAt.AddSeconds(2)
        };
        var definition = new MirrorPulseNamespacePermissionTreeDefinition(Guid.NewGuid(), new(original, next), 1, next.PreparedAt.AddSeconds(-1));
        await catalog.CreateNamespacePermissionTreeAsync(definition);
        var child = NestedBirth(parent, "child.txt", false);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(child));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(child.OperationId));
        Assert.AreEqual(parent.Birth, await catalog.PrepareNamespaceBirthAsync(parent.Birth));
    }

    [TestMethod]
    [DataRow("parent-observation")]
    [DataRow("parent-plan")]
    [DataRow("parent-epoch")]
    [DataRow("reservation")]
    public async Task NestedRecoveryRejectsDamagedDirectParentFacts(string scenario)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var permission = Protection(original);
        BornObject parent; MirrorPulseNamespaceBirthIntent child;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, permission);
            parent = await AddBornAsync(first, Birth(original, permission) with { IsDirectory = true, RelativePath = "Docs/A" });
            child = NestedBirth(parent, "child.txt", false);
            await first.PrepareNamespaceBirthAsync(child);
        }
        if (scenario == "parent-epoch")
            await ReplacePayloadValueAsync(fixture.Paths, "namespace_birth_protections", "protection_id", parent.Protection.ProtectionId,
                parent.Observation.LocalObject.LocalFileId.ToString("D"), Guid.NewGuid().ToString("D"));
        else await ExecuteSqlAsync(fixture.Paths, scenario switch
        {
            "parent-observation" => "UPDATE namespace_birth_observations SET fingerprint=zeroblob(32);",
            "parent-plan" => "UPDATE namespace_birth_plans SET fingerprint=zeroblob(32);",
            _ => "UPDATE namespace_birth_reservations SET parent_kind=0 WHERE parent_kind=1;",
        });
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var scan = await second.BeginNamespaceBirthRecoveryScanAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthRecoveryPageAsync(scan, 0, 64));
        if (scenario != "reservation") await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthAsync(child.OperationId));
    }

    private sealed record BornObject(MirrorPulseNamespaceBirthIntent Birth, MirrorPulseNamespaceBirthPlan Plan,
        MirrorPulseNamespaceBirthStart Start, MirrorPulseNamespaceBirthObservation Observation, MirrorPulseNamespaceBirthProtection Protection);

    private static MirrorPulseNamespaceBirthIntent NestedBirth(BornObject parent, string name, bool directory) =>
        new(2, Guid.NewGuid(), parent.Birth.RootId, Guid.Empty, Guid.Empty, parent.Observation.LocalObject,
            parent.Birth.RelativePath + "/" + name, directory, MirrorPulseNamespaceBirthOrigin.ControlledCreation,
            parent.Protection.RoleSid, parent.Protection.Verification.ObservedAt.AddSeconds(1))
        { BornParent = new(parent.Birth.OperationId, parent.Protection.ProtectionId) };

    private static async Task<BornObject> AddBornAsync(MirrorPulseProductCatalog catalog, MirrorPulseNamespaceBirthIntent birth)
    {
        var plan = Plan(birth) with
        {
            RemoteId = "local:" + birth.OperationId.ToString("N"),
            RemoteRevision = birth.Origin == MirrorPulseNamespaceBirthOrigin.RemotePopulation ? "remote-r1" : null
        };
        var start = Start(plan);
        var observation = Observation(birth, plan, start) with { BirthDacl = Protected.Replace(Role, birth.RoleSid, StringComparison.Ordinal) };
        await catalog.PrepareNamespaceBirthAsync(birth);
        await catalog.PrepareNamespaceBirthPlanAsync(plan);
        await catalog.RecordNamespaceBirthStartAsync(start);
        if (birth.Origin != MirrorPulseNamespaceBirthOrigin.RemotePopulation)
            await catalog.PrepareNamespaceBirthConversionAsync(new(1, birth.OperationId, observation.LocalObject, birth.IsDirectory,
                observation.OwnerSid, observation.BirthDacl, observation.BirthDacl, 1, start.StartedAt.AddMilliseconds(100)));
        await catalog.RecordNamespaceBirthObservationAsync(observation);
        var protection = new MirrorPulseNamespaceBirthProtection(birth.BornParent is null ? 1 : 2, Guid.NewGuid(), birth.OperationId,
            null, birth.ParentPermissionOperationId, birth.RoleSid,
            new(observation.LocalObject, observation.OwnerSid, observation.BirthDacl, observation.ObservedAt.AddSeconds(1)), true, 1)
        { BornParent = birth.BornParent };
        await catalog.RecordNamespaceBirthProtectionAsync(protection);
        return new(birth, plan, start, observation, protection);
    }

    private static async Task ReplacePayloadValueAsync(MirrorPulse.Core.Configuration.MirrorPulseStoragePaths paths,
        string table, string keyColumn, Guid key, string from, string to)
    {
        await using var connection = Connection(paths); await connection.OpenAsync();
        await using var read = connection.CreateCommand();
        read.CommandText = $"SELECT payload FROM {table} WHERE {keyColumn}=$key;";
        read.Parameters.AddWithValue("$key", key.ToString("D"));
        string original = Encoding.UTF8.GetString((byte[])(await read.ExecuteScalarAsync())!);
        Assert.Contains(from, original);
        byte[] payload = Encoding.UTF8.GetBytes(original.Replace(from, to, StringComparison.Ordinal));
        await using var change = connection.CreateCommand();
        change.CommandText = $"UPDATE {table} SET payload=$payload,fingerprint=$fingerprint WHERE {keyColumn}=$key;";
        change.Parameters.AddWithValue("$payload", payload); change.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
        change.Parameters.AddWithValue("$key", key.ToString("D"));
        Assert.AreEqual(1, await change.ExecuteNonQueryAsync());
    }
}
