using System.Security.Cryptography;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation)]
    public async Task ReplacementAdmissionAndStartRetainOriginalMemberAndFenceAcrossCatalogOwners(MirrorPulseNamespaceBirthOrigin origin)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementIntent intent; MoveFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first, origin: origin);
            intent = Replacement((await first.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
            Assert.AreEqual(intent, await first.PrepareNamespaceReplacementAsync(intent));
            CollectionAssert.AreEqual(new[] { intent.RootId }, (await first.ReadNamespaceRecoveryRootsAsync()).ToArray());
            CollectionAssert.AreEqual(new[] { intent.RootId }, (await first.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.PrepareNamespaceMemberMoveAsync(setup.Move));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.ReadNamespaceMemberProtectionLineageAsync(intent.MemberId));
            Assert.AreEqual(intent.Original, await first.ReadNamespaceMemberProtectionSnapshotAsync(intent.MemberId));
            Assert.AreEqual(setup.Member, await first.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
            Assert.IsNull(await first.ReadMutationAsync(intent.OperationId));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(intent, await second.ReadNamespaceReplacementAsync(intent.OperationId));
        Assert.AreEqual(intent, await second.PrepareNamespaceReplacementAsync(intent));
        var start = new MirrorPulseNamespaceReplacementStart(1, intent.OperationId, intent.PreparedAt.AddSeconds(1));
        Assert.AreEqual(start, await second.RecordNamespaceReplacementStartAsync(start));
        Assert.AreEqual(start, await second.RecordNamespaceReplacementStartAsync(start));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.RecordNamespaceReplacementStartAsync(start with { StartedAt = start.StartedAt.AddSeconds(1) }));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.CancelUnstartedNamespaceReplacementAsync(new(1, intent.OperationId, start.StartedAt.AddSeconds(1))));
        CollectionAssert.AreEqual(new[] { intent.RootId }, (await second.ReadNamespaceRecoveryRootsAsync()).ToArray());
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberAsync(intent.MemberId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath)); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    public async Task NeverStartedReplacementCancellationReleasesOnlyItsFenceAndRetainsHistoryAcrossOwners()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementIntent intent; MirrorPulseNamespaceReplacementCancellation cancelled;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first);
            intent = Replacement((await first.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
            await first.PrepareNamespaceReplacementAsync(intent);
            cancelled = new(1, intent.OperationId, intent.PreparedAt.AddSeconds(1));
            Assert.AreEqual(cancelled, await first.CancelUnstartedNamespaceReplacementAsync(cancelled));
            Assert.IsEmpty(await first.ReadNamespaceRecoveryRootsAsync()); Assert.IsEmpty(await first.ReadNamespaceDispatchFencedRootsAsync());
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(cancelled, await second.CancelUnstartedNamespaceReplacementAsync(cancelled));
        Assert.AreEqual(intent, await second.PrepareNamespaceReplacementAsync(intent));
        Assert.IsEmpty(await second.ReadNamespaceRecoveryRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.RecordNamespaceReplacementStartAsync(new(1, intent.OperationId, cancelled.CancelledAt.AddSeconds(1))));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.CancelUnstartedNamespaceReplacementAsync(cancelled with { CancelledAt = cancelled.CancelledAt.AddSeconds(1) }));
        var next = intent with { OperationId = Guid.NewGuid(), PreparedAt = cancelled.CancelledAt.AddSeconds(2) };
        Assert.AreEqual(next, await second.PrepareNamespaceReplacementAsync(next));
        Assert.AreEqual(intent, await second.ReadNamespaceReplacementAsync(intent.OperationId));
        CollectionAssert.AreEqual(new[] { next.RootId }, (await second.ReadNamespaceRecoveryRootsAsync()).ToArray());
    }

    [TestMethod]
    [DataRow("binding")]
    [DataRow("position")]
    [DataRow("identity")]
    [DataRow("role")]
    [DataRow("descriptor")]
    [DataRow("root")]
    public async Task ReplacementCannotAdoptSubstitutedCurrentMemberEvidence(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var original = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!;
        var altered = changed switch
        {
            "binding" => original with { Context = original.Context with { Member = original.Context.Member with { LocalObject = original.Context.Member.LocalObject with { LocalFileId = Guid.NewGuid() } } } },
            "position" => original with { Context = original.Context with { Location = original.Context.Location with { RelativePath = "Docs/another.txt" } } },
            "identity" => original with { Identity = original.Identity with { ItemId = Guid.NewGuid() } },
            "role" => original with { Context = original.Context with { Protection = original.Context.Protection with { RoleSid = "S-1-5-5-321-654" } } },
            "descriptor" => original with { ExpectedDacl = original.ExpectedDacl + " " },
            "root" => original with { Context = original.Context with { Member = original.Context.Member with { RootId = RootId.New() } } },
            _ => throw new AssertFailedException()
        };
        var intent = Replacement(altered);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementAsync(intent));
        Assert.IsNull(await catalog.ReadNamespaceReplacementAsync(intent.OperationId));
        Assert.IsEmpty(await catalog.ReadNamespaceRecoveryRootsAsync());
        Assert.AreEqual(original, await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId));
    }

    [TestMethod]
    [DataRow("content")]
    [DataRow("encoding")]
    [DataRow("time")]
    public async Task ReplacementReplayCannotChangeItsOriginalInputsOrReopenStartedRecovery(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        await catalog.PrepareNamespaceReplacementAsync(intent);
        var altered = changed switch
        {
            "content" => intent with { Content = intent.Content with { Length = intent.Content.Length + 1 } },
            "encoding" => intent with { OriginalPlaceholderIdentity = Convert.ToBase64String([4, 5, 6]) },
            "time" => intent with { PreparedAt = intent.PreparedAt.AddSeconds(1) },
            _ => throw new AssertFailedException()
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementAsync(altered));
        Assert.AreEqual(intent, await catalog.ReadNamespaceReplacementAsync(intent.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementAsync(intent with { OperationId = Guid.NewGuid() }));
    }

    [TestMethod]
    [DataRow("pending")]
    [DataRow("fingerprint")]
    [DataRow("root-index")]
    public async Task DamagedReplacementOwnershipFailsClosedForDispatchAndRecovery(string damage)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementIntent intent;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first); intent = Replacement((await first.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
            await first.PrepareNamespaceReplacementAsync(intent);
        }
        string sql = damage switch
        {
            "pending" => "DELETE FROM namespace_replacement_pending;",
            "fingerprint" => "UPDATE namespace_replacements SET fingerprint=zeroblob(32);",
            "root-index" => $"UPDATE namespace_replacement_pending SET root_id='{RootId.New()}';",
            _ => throw new AssertFailedException()
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceRecoveryRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task ReplacementMigrationKeepsOriginalAdmissionAndAllFencesWithoutInventingExecutionEvidence()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceReplacementIntent intent;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(first); intent = Replacement((await first.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
            await first.PrepareNamespaceReplacementAsync(intent);
        }
        await ExecuteSqlAsync(fixture.Paths, "PRAGMA user_version=41;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(intent, await second.ReadNamespaceReplacementAsync(intent.OperationId));
        CollectionAssert.AreEqual(new[] { intent.RootId }, (await second.ReadNamespaceRecoveryRootsAsync()).ToArray());
        Assert.IsNull(await second.ReadMutationAsync(intent.OperationId));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    private static MirrorPulseNamespaceReplacementIntent Replacement(MirrorPulseNamespaceMemberProtectionSnapshot snapshot) =>
        new(1, Guid.NewGuid(), snapshot, Convert.ToBase64String([1, 2, 3]), new(3, Convert.ToHexString(SHA256.HashData([8, 9, 10]))),
            snapshot.Context.Verification.ObservedAt.AddSeconds(5));

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ReplacementCannotReuseBirthOrWorkerOperationOwnership(bool worker)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog); Guid occupied = worker ? Guid.NewGuid() : setup.Born.Birth.OperationId;
        if (worker) await catalog.TryRecordWorkerRequestAsync(occupied, InstanceId.New(), SHA256.HashData([1, 2, 3]));
        var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!) with { OperationId = occupied };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceReplacementAsync(intent));
        Assert.IsNull(await catalog.ReadNamespaceReplacementAsync(occupied)); Assert.IsEmpty(await catalog.ReadNamespaceRecoveryRootsAsync());
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    [DataRow("version")]
    [DataRow("id")]
    [DataRow("length")]
    [DataRow("hash")]
    [DataRow("encoding")]
    [DataRow("before-registration")]
    public async Task InvalidReplacementInputsCannotReserveARecoveryFence(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var intent = Replacement((await catalog.ReadNamespaceMemberProtectionSnapshotAsync(setup.Member.MemberId))!);
        intent = changed switch
        {
            "version" => intent with { Version = 2 },
            "id" => intent with { OperationId = Guid.Empty },
            "length" => intent with { Content = intent.Content with { Length = -1 } },
            "hash" => intent with { Content = intent.Content with { Sha256 = new string('a', 64) } },
            "encoding" => intent with { OriginalPlaceholderIdentity = "***" },
            "before-registration" => intent with { PreparedAt = setup.Member.RegisteredAt.AddTicks(-1) },
            _ => throw new AssertFailedException()
        };
        await Assert.ThrowsAsync<ArgumentException>(() => catalog.PrepareNamespaceReplacementAsync(intent));
        Assert.IsEmpty(await catalog.ReadNamespaceRecoveryRootsAsync()); Assert.IsEmpty(await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(setup.Member, await catalog.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }
}
