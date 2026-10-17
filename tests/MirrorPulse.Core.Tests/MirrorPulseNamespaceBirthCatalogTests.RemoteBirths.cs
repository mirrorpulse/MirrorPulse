using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RemoteBirthAdmissionAndOriginalIdentitySurviveReopenBeforePlanCommit(bool directory)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBirthIntent birth;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            birth = await PrepareRemoteBirthParentAsync(first, directory);
            Assert.AreEqual(birth, await first.PrepareNamespaceBirthAsync(birth));
            Assert.IsNull(await first.ReadNamespaceBirthPlanAsync(birth.OperationId));
            Assert.IsNull(await first.ReadNamespaceBirthObservationAsync(birth.OperationId));
        }
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var remote = birth.RemoteEntry!;
            Assert.AreEqual(birth, await second.ReadNamespaceRemoteBirthAsync(remote.InstanceId, remote.BatchId, remote.Ordinal));
            Assert.AreEqual(birth, await second.PrepareNamespaceBirthAsync(birth));
            var plan = RemoteBirthPlan(birth);
            Assert.AreEqual(plan, await second.PrepareNamespaceBirthPlanAsync(plan));
            Assert.AreEqual(plan, await second.PrepareNamespaceBirthPlanAsync(plan));
            Assert.IsNull(await second.ReadNamespaceBirthStartAsync(birth.OperationId));
            Assert.IsNull(await second.ReadNamespaceMemberAsync(birth.OperationId));
            CollectionAssert.Contains((await second.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), birth.RootId);
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    public async Task FailedEntryIndexCommitRollsBackBirthAndNameReservationTogether()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var birth = await PrepareRemoteBirthParentAsync(catalog);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER fail_remote_birth BEFORE INSERT ON namespace_remote_birth_entries BEGIN SELECT RAISE(ABORT,'Synthetic entry commit failure'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceBirthAsync(birth));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(birth.OperationId));
        var reference = birth.RemoteEntry!;
        Assert.IsNull(await catalog.ReadNamespaceRemoteBirthAsync(reference.InstanceId, reference.BatchId, reference.Ordinal));
        await ExecuteSqlAsync(fixture.Paths, "DROP TRIGGER fail_remote_birth;");
        Assert.AreEqual(birth, await catalog.PrepareNamespaceBirthAsync(birth));
        Assert.AreEqual(birth, await catalog.ReadNamespaceRemoteBirthAsync(reference.InstanceId, reference.BatchId, reference.Ordinal));
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("fingerprint")]
    [DataRow("count")]
    [DataRow("change")]
    public async Task OriginalEntryCollisionRollsBackTheOtherBirthAndDoesNotConsumeItsName(string collision)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var original = await PrepareRemoteBirthParentAsync(catalog); await catalog.PrepareNamespaceBirthAsync(original);
        var remote = original.RemoteEntry!;
        var other = original with
        {
            OperationId = Guid.NewGuid(),
            RelativePath = "Docs/other.txt",
            RemoteEntry = remote with { Ordinal = 1, ChangeId = "other", ItemId = Guid.NewGuid() }
        };
        other = collision switch
        {
            "owner" => other with { RemoteEntry = other.RemoteEntry with { Ordinal = remote.Ordinal } },
            "fingerprint" => other with { RemoteEntry = other.RemoteEntry with { BatchFingerprint = new string('B', 64) } },
            "count" => other with { RemoteEntry = other.RemoteEntry with { BatchEntryCount = 4 } },
            _ => other with { RemoteEntry = other.RemoteEntry with { ChangeId = remote.ChangeId } },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(other));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(other.OperationId));
        var valid = original with
        {
            OperationId = other.OperationId,
            RelativePath = other.RelativePath,
            RemoteEntry = remote with { Ordinal = 1, ChangeId = "independent", ItemId = Guid.NewGuid() }
        };
        Assert.AreEqual(valid, await catalog.PrepareNamespaceBirthAsync(valid));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task MoveAndBirthCannotOwnTheSameOriginalBatchEntryAcrossRoots(bool birthFirst)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var moveReference = RemoteMoveReference() with { Ordinal = 0, BatchEntryCount = 3 };
        var move = setup.Move with { Version = 2, RemoteEntry = moveReference };
        var otherRoot = Baseline() with
        {
            RelativePath = "Other",
            LocalObject = setup.Parent.LocalObject with { LocalFileId = Guid.NewGuid() }
        };
        var protection = Protection(otherRoot); await ProtectAsync(catalog, otherRoot, protection);
        var parent = OriginalMember(otherRoot, protection, setup.ParentMember.ParentMemberId);
        await catalog.RegisterNamespaceMemberAsync(parent);
        var birth = Birth(otherRoot, protection) with
        {
            Version = 3,
            Origin = MirrorPulseNamespaceBirthOrigin.RemotePopulation,
            RelativePath = "Other/remote.txt",
            RemoteEntry = RemoteBirthReference(parent.MemberId) with
            {
                InstanceId = moveReference.InstanceId,
                BatchId = moveReference.BatchId,
                BatchFingerprint = moveReference.BatchFingerprint,
                ChangeId = moveReference.ChangeId,
                Ordinal = 0
            }
        };
        if (birthFirst)
        {
            await catalog.PrepareNamespaceBirthAsync(birth);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceMemberMoveAsync(move));
            Assert.IsNull(await catalog.ReadNamespaceMemberMoveAsync(move.OperationId));
            Assert.IsNull(await catalog.ReadPendingNamespaceMemberMoveAsync(move.RootId));
        }
        else
        {
            await catalog.PrepareNamespaceMemberMoveAsync(move);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(birth));
            Assert.IsNull(await catalog.ReadNamespaceBirthAsync(birth.OperationId));
        }
    }

    [TestMethod]
    [DataRow("item")]
    [DataRow("revision")]
    [DataRow("time")]
    public async Task RemoteBirthPlanCannotBeReconstructedFromAChangedCurrentIdentity(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var birth = await PrepareRemoteBirthParentAsync(catalog); await catalog.PrepareNamespaceBirthAsync(birth);
        var plan = RemoteBirthPlan(birth);
        var invalid = changed switch
        {
            "item" => plan with { ItemId = Guid.NewGuid() },
            "revision" => plan with { RemoteRevision = "replacement" },
            _ => plan with { PreparedAt = plan.PreparedAt.AddSeconds(1) },
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthPlanAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceBirthPlanAsync(birth.OperationId));
        Assert.AreEqual(plan, await catalog.PrepareNamespaceBirthPlanAsync(plan));
    }

    [TestMethod]
    [DataRow("parent")]
    [DataRow("position")]
    public async Task RemoteBirthMustNameItsActualRegisteredParentAndOriginalPlacement(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var birth = await PrepareRemoteBirthParentAsync(catalog);
        var invalid = birth with
        {
            RemoteEntry = changed == "parent" ? birth.RemoteEntry! with { ParentMemberId = Guid.NewGuid() } :
            birth.RemoteEntry! with { ParentPositionOperationId = Guid.NewGuid() }
        };
        if (changed == "parent") await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => catalog.PrepareNamespaceBirthAsync(invalid));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(invalid.OperationId));
        Assert.AreEqual(birth, await catalog.PrepareNamespaceBirthAsync(birth));
    }

    [TestMethod]
    [DataRow("legacy-version")]
    [DataRow("missing-reference")]
    [DataRow("origin")]
    [DataRow("ordinal")]
    [DataRow("package")]
    [DataRow("policy")]
    [DataRow("fingerprint")]
    public async Task UnsupportedRemoteBirthShapeCannotLeaveAnIntentOrReservation(string changed)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var birth = await PrepareRemoteBirthParentAsync(catalog); var remote = birth.RemoteEntry!;
        var invalid = changed switch
        {
            "legacy-version" => birth with { Version = 1 },
            "missing-reference" => birth with { RemoteEntry = null },
            "origin" => birth with { Origin = MirrorPulseNamespaceBirthOrigin.Import },
            "ordinal" => birth with { RemoteEntry = remote with { Ordinal = remote.BatchEntryCount } },
            "package" => birth with { RemoteEntry = remote with { PackageVersion = "0.1.0-preview.5" } },
            "policy" => birth with { RemoteEntry = remote with { Policy = remote.Policy with { EchoSuppressionLifetimeTicks = 0 } } },
            _ => birth with { RemoteEntry = remote with { BatchFingerprint = new string('g', 64) } },
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceBirthAsync(invalid));
        Assert.IsNull(await catalog.ReadNamespaceBirthAsync(invalid.OperationId));
        Assert.AreEqual(birth, await catalog.PrepareNamespaceBirthAsync(birth));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("fingerprint")]
    [DataRow("count")]
    [DataRow("item")]
    public async Task DamagedRemoteBirthIndexCannotReleaseDispatchOrRetargetOriginalInput(string changed)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBirthIntent birth;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { birth = await PrepareRemoteBirthParentAsync(first); await first.PrepareNamespaceBirthAsync(birth); }
        await ExecuteSqlAsync(fixture.Paths, changed switch
        {
            "missing" => "DELETE FROM namespace_remote_birth_entries;",
            "fingerprint" => "UPDATE namespace_remote_birth_entries SET batch_fingerprint=replace(batch_fingerprint,'A','B');",
            "item" => "UPDATE namespace_remote_birth_entries SET item_id='00000000-0000-0000-0000-000000000001';",
            _ => "UPDATE namespace_remote_birth_entries SET entry_count=entry_count+1;"
        });
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthAsync(birth.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task Schema68UpgradeAddsNoRemoteOwnerToHistoricalPopulationBirth()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBirthIntent legacy;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var baseline = Baseline(); var protection = Protection(baseline); await ProtectAsync(first, baseline, protection);
            legacy = Birth(baseline, protection) with { Origin = MirrorPulseNamespaceBirthOrigin.RemotePopulation };
            await first.PrepareNamespaceBirthAsync(legacy);
        }
        await ExecuteSqlAsync(fixture.Paths, "DROP TABLE namespace_remote_birth_entries; PRAGMA user_version=68;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(legacy, await second.ReadNamespaceBirthAsync(legacy.OperationId));
        Assert.IsNull((await second.ReadNamespaceBirthAsync(legacy.OperationId))!.RemoteEntry);
        var instance = InstanceId.New(); Assert.IsNull(await second.ReadNamespaceRemoteBirthAsync(instance, instance + "/unknown", 0));
    }

    [TestMethod]
    public async Task OriginalRemoteIdentityIsReservedAcrossOwnersBeforeItsSeparatePlanExists()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBirthIntent birth;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        { birth = await PrepareRemoteBirthParentAsync(first); await first.PrepareNamespaceBirthAsync(birth); }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var remote = birth.RemoteEntry!;
        var competing = birth with
        {
            OperationId = Guid.NewGuid(),
            RelativePath = "Docs/competing.bin",
            RemoteEntry = remote with { Ordinal = 1, ChangeId = "different-entry" }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespaceBirthAsync(competing));
        Assert.IsNull(await second.ReadNamespaceBirthAsync(competing.OperationId));
        Assert.IsNull(await second.ReadNamespaceBirthPlanAsync(birth.OperationId));
        var independent = competing with { RemoteEntry = competing.RemoteEntry! with { ItemId = Guid.NewGuid() } };
        Assert.AreEqual(independent, await second.PrepareNamespaceBirthAsync(independent));
        Assert.AreEqual(RemoteBirthPlan(birth), await second.PrepareNamespaceBirthPlanAsync(RemoteBirthPlan(birth)));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OriginalPermissionIdentityAndRemoteBirthReservationRejectCollisionsInEitherOrder(bool birthFirst)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var birth = await PrepareRemoteBirthParentAsync(catalog);
        var original = Baseline() with
        {
            RootId = birth.RootId,
            RelativePath = "Docs/original.bin",
            IsDirectory = false,
            LocalObject = birth.ParentLocalObject with { LocalFileId = Guid.NewGuid() }
        };
        var protection = Protection(original);
        await catalog.PrepareNamespacePermissionChangeAsync(original, protection);
        var identity = new MirrorPulseNamespacePermissionLocalIdentity(1, original.EvidenceId, protection.OperationId,
            original.LocalObject, birth.RemoteEntry!.ItemId, "original-remote", protection.PreparedAt.AddSeconds(1));
        if (birthFirst)
        {
            await catalog.PrepareNamespaceBirthAsync(birth);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespacePermissionLocalIdentityAsync(identity));
            Assert.IsNull(await catalog.ReadNamespacePermissionLocalIdentityAsync(original.EvidenceId));
        }
        else
        {
            await catalog.PrepareNamespacePermissionLocalIdentityAsync(identity);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(birth));
            Assert.IsNull(await catalog.ReadNamespaceBirthAsync(birth.OperationId));
        }
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OrdinaryBirthPlanAndRemoteBirthReservationCannotShareAnItemIdentity(bool remoteFirst)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var remote = await PrepareRemoteBirthParentAsync(catalog);
        var ordinary = remote with { Version = 1, RemoteEntry = null, OperationId = Guid.NewGuid(), RelativePath = "Docs/population.bin" };
        await catalog.PrepareNamespaceBirthAsync(ordinary);
        var plan = RemoteBirthPlan(remote) with { OperationId = ordinary.OperationId };
        if (remoteFirst)
        {
            await catalog.PrepareNamespaceBirthAsync(remote);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthPlanAsync(plan));
            Assert.IsNull(await catalog.ReadNamespaceBirthPlanAsync(ordinary.OperationId));
        }
        else
        {
            await catalog.PrepareNamespaceBirthPlanAsync(plan);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(remote));
            Assert.IsNull(await catalog.ReadNamespaceBirthAsync(remote.OperationId));
        }
    }

    [TestMethod]
    public async Task ReadSideRejectsConflictingMoveOwnershipInjectedAfterBothAdmissions()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBirthIntent birth; MirrorPulseNamespaceMemberMoveIntent move;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            birth = await PrepareRemoteBirthParentAsync(first); await first.PrepareNamespaceBirthAsync(birth);
            var setup = await PrepareMovableMemberAsync(first); var remote = birth.RemoteEntry!;
            move = setup.Move with
            {
                Version = 2,
                RemoteEntry = RemoteMoveReference() with
                {
                    InstanceId = remote.InstanceId,
                    BatchId = remote.BatchId,
                    BatchFingerprint = remote.BatchFingerprint,
                    BatchEntryCount = remote.BatchEntryCount,
                    Ordinal = 1,
                    ChangeId = "different-move"
                }
            };
            await first.PrepareNamespaceMemberMoveAsync(move);
        }
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_remote_move_entries SET ordinal=0;");
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthAsync(birth.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceMemberMoveAsync(move.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RegisteredRemoteBornDirectoryAdmitsTheNextOriginalEntryAndReleasesEachOwnFence(bool directory)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBirthIntent parentBirth, child;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            parentBirth = await PrepareRemoteBirthParentAsync(catalog, true);
            var parent = await RegisterRemoteBornAsync(catalog, parentBirth);
            Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(parentBirth.RootId));
            child = NestedBirth(parent.Born, "child", directory) with
            {
                Version = 3,
                Origin = MirrorPulseNamespaceBirthOrigin.RemotePopulation,
                RemoteEntry = parentBirth.RemoteEntry! with
                {
                    Ordinal = 1,
                    ChangeId = "child-entry",
                    ItemId = Guid.NewGuid(),
                    RemoteId = "remote-child",
                    ParentMemberId = parent.Member.MemberId
                }
            };
            await catalog.PrepareNamespaceBirthAsync(child);
            Assert.IsTrue((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(child.RootId));
            await RegisterRemoteBornAsync(catalog, child);
            Assert.IsFalse((await catalog.ReadNamespaceDispatchFencedRootsAsync()).Contains(child.RootId));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        foreach (var birth in new[] { parentBirth, child })
        {
            var remote = birth.RemoteEntry!;
            Assert.AreEqual(birth, await second.ReadNamespaceRemoteBirthAsync(remote.InstanceId, remote.BatchId, remote.Ordinal));
            Assert.IsNotNull(await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        }
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("fingerprint")]
    [DataRow("identity")]
    public async Task ParentLineageAndProtectionCannotBypassACorruptRemoteBornOwnerIndex(string corrupt)
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceBirthIntent child, parentBirth;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            parentBirth = await PrepareRemoteBirthParentAsync(catalog, true);
            var parent = await RegisterRemoteBornAsync(catalog, parentBirth);
            child = NestedBirth(parent.Born, "local-child", false);
            await catalog.PrepareNamespaceBirthAsync(child);
        }
        await ExecuteSqlAsync(fixture.Paths, corrupt switch
        {
            "missing" => "DELETE FROM namespace_remote_birth_entries;",
            "fingerprint" => "UPDATE namespace_remote_birth_entries SET batch_fingerprint=replace(batch_fingerprint,'A','B');",
            _ => "UPDATE namespace_remote_birth_entries SET item_id='00000000-0000-0000-0000-000000000001';"
        });
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadNamespaceBirthAsync(child.OperationId));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => second.ReadLatestNamespaceBirthProtectionAsync(parentBirth.OperationId));
    }

    private static async Task<(BornObject Born, MirrorPulseNamespaceMemberRegistration Member)> RegisterRemoteBornAsync(
        MirrorPulseProductCatalog catalog, MirrorPulseNamespaceBirthIntent birth)
    {
        var plan = RemoteBirthPlan(birth); var start = Start(plan);
        var observation = Observation(birth, plan, start) with { IsInSync = true };
        await catalog.PrepareNamespaceBirthAsync(birth); await catalog.PrepareNamespaceBirthPlanAsync(plan);
        await catalog.RecordNamespaceBirthStartAsync(start); await catalog.RecordNamespaceBirthObservationAsync(observation);
        var protection = new MirrorPulseNamespaceBirthProtection(birth.BornParent is null ? 1 : 2, Guid.NewGuid(),
            birth.OperationId, null, birth.ParentPermissionOperationId, birth.RoleSid,
            new(observation.LocalObject, observation.OwnerSid, observation.BirthDacl, observation.ObservedAt.AddSeconds(1)), true, 1)
        { BornParent = birth.BornParent };
        await catalog.RecordNamespaceBirthProtectionAsync(protection);
        var born = new BornObject(birth, plan, start, observation, protection);
        var parent = (await catalog.ReadNamespaceMemberAsync(birth.RemoteEntry!.ParentMemberId))!;
        var member = BornMember(born, parent); await catalog.RegisterNamespaceMemberAsync(member);
        return (born, member);
    }

    private static async Task<MirrorPulseNamespaceBirthIntent> PrepareRemoteBirthParentAsync(MirrorPulseProductCatalog catalog,
        bool directory = false)
    {
        var parent = Baseline(); var protection = Protection(parent); await ProtectAsync(catalog, parent, protection);
        var member = await RegisterOriginalParentMembersAsync(catalog, parent, protection);
        return Birth(parent, protection) with
        {
            Version = 3,
            Origin = MirrorPulseNamespaceBirthOrigin.RemotePopulation,
            IsDirectory = directory,
            RemoteEntry = RemoteBirthReference(member.MemberId)
        };
    }

    private static MirrorPulseNamespaceRemoteBirthReference RemoteBirthReference(Guid parent)
    {
        var instance = InstanceId.New();
        return new(1, instance, instance + "/original", new string('A', 64), 3, 0, "original-change", Guid.NewGuid(),
            "remote-original", "opaque-v1", parent, null, "0.1.0-preview.6", "3caa382e341d260543d3afc05e25fa034fb32e16",
            new(true, true, TimeSpan.FromMinutes(5).Ticks, 2));
    }

    private static MirrorPulseNamespaceBirthPlan RemoteBirthPlan(MirrorPulseNamespaceBirthIntent birth) =>
        new(1, birth.OperationId, birth.RemoteEntry!.ItemId, birth.RemoteEntry.RemoteId, birth.RemoteEntry.RemoteRevision, birth.PreparedAt);
}
