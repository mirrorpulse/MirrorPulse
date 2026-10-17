using Microsoft.Data.Sqlite;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task OriginalRemoteRetirementTreeRetainsEveryMemberAcrossBoundedPagesAndOwners()
    {
        using var fixture = new Fixture(); MirrorPulseNamespaceRetirementBatch batch;
        MirrorPulseNamespaceRemoteRetirementTreeEntry[] expected;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareRemoteTreeAsync(first, 65); expected = setup.Entries;
            batch = await first.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId, setup.Intent.RemoteEntry!, expected, setup.Intent.PreparedAt);
            Assert.HasCount(66, batch.Entries); Assert.AreEqual(2, batch.Version);
            var replay = await first.PrepareNamespaceRemoteRetirementTreeAsync(batch.OperationId, batch.RemoteEntry!, expected, batch.PreparedAt);
            Assert.AreEqual(batch.RemoteTreeFingerprint, replay.RemoteTreeFingerprint);
            CollectionAssert.AreEqual(batch.Entries.ToArray(), replay.Entries.ToArray());
            var page = await first.ReadNamespaceRemoteRetirementTreePageAsync(batch.OperationId);
            Assert.HasCount(64, page.Entries); Assert.IsTrue(page.HasMore); Assert.AreEqual(63, page.LastOrdinal);
            CollectionAssert.AreEqual(expected.Take(64).ToArray(), page.Entries.ToArray());
            CollectionAssert.Contains((await first.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), batch.RootId);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.PrepareNamespaceRetirementBatchAsync(batch.OperationId, batch.MemberId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.CompleteNamespaceRetirementBatchAsync(batch.OperationId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.PrepareNamespaceRetirementAsync(
                new(1, batch.MemberOperationId(batch.Entries[^1].MemberId), batch.Entries[^1].MemberId, batch.RootId,
                    batch.Entries[^1].LocalObject, batch.Entries[^1].RelativePath, false, batch.Entries[^1].OwnerSid,
                    batch.RemoteEntry!.Child.Context.Protection.RoleSid, batch.PreparedAt.AddSeconds(1))
                { BatchOperationId = batch.OperationId }));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = (await second.ReadNamespaceRemoteRetirementTreeAsync(batch.RemoteEntry!.InstanceId, batch.RemoteEntry.BatchId, batch.RemoteEntry.Ordinal))!;
        Assert.AreEqual(batch.RemoteEntry, retained.RemoteEntry); Assert.AreEqual(batch.RemoteTreeFingerprint, retained.RemoteTreeFingerprint);
        var tail = await second.ReadNamespaceRemoteRetirementTreePageAsync(batch.OperationId, 63);
        Assert.HasCount(2, tail.Entries); Assert.IsFalse(tail.HasMore); Assert.AreEqual(65, tail.LastOrdinal);
        CollectionAssert.AreEqual(expected.Skip(64).ToArray(), tail.Entries.ToArray());
        Assert.HasCount(0, (await second.ReadNamespaceRemoteRetirementTreePageAsync(batch.OperationId, 65)).Entries);
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath)); Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("order")]
    [DataRow("binding")]
    [DataRow("identity")]
    [DataRow("permission")]
    [DataRow("parent")]
    [DataRow("depth")]
    [DataRow("ordinal")]
    public async Task OriginalRemoteRetirementTreeRejectsIncompleteOrChangedRegisteredSubtrees(string invalid)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteTreeAsync(catalog, 2); var entries = setup.Entries.ToArray(); var child = entries[1];
        if (invalid == "missing") entries = entries[..^1];
        if (invalid == "duplicate") entries[2] = child with { Ordinal = 2 };
        if (invalid == "order") (entries[0], entries[1]) = (entries[1] with { Ordinal = 0 }, entries[0] with { Ordinal = 1 });
        if (invalid == "binding") entries[1] = child with
        {
            Original = child.Original with
            {
                Context = child.Original.Context with
                { Member = child.Original.Context.Member with { LocalObject = child.Original.Context.Member.LocalObject with { LocalFileId = Guid.NewGuid() } } }
            }
        };
        if (invalid == "identity") entries[1] = child with { Original = child.Original with { Identity = child.Original.Identity with { ItemId = Guid.NewGuid() } } };
        if (invalid == "permission") entries[1] = child with
        {
            Original = child.Original with
            {
                Context = child.Original.Context with
                { Protection = child.Original.Context.Protection with { ProtectionId = Guid.NewGuid() } }
            }
        };
        if (invalid == "parent") entries[1] = child with
        {
            Original = child.Original with
            {
                Context = child.Original.Context with
                { Location = child.Original.Context.Location with { ParentMemberId = Guid.NewGuid() } }
            }
        };
        if (invalid == "depth") entries[1] = child with { Depth = 2 };
        if (invalid == "ordinal") entries[1] = child with { Ordinal = 9 };
        if (invalid == "binding")
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId,
                setup.Intent.RemoteEntry!, entries, setup.Intent.PreparedAt));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId,
                setup.Intent.RemoteEntry!, entries, setup.Intent.PreparedAt));
        Assert.IsNull(await catalog.ReadNamespaceRetirementBatchAsync(setup.Intent.OperationId));
        Assert.HasCount(0, await catalog.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task OriginalRemoteRetirementTreeAdmissionAndEveryMetadataRowCommitAtomically()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteTreeAsync(catalog, 2);
        await ExecuteSqlAsync(fixture.Paths, "CREATE TRIGGER abort_tree_entry BEFORE INSERT ON namespace_remote_retirement_tree_entries WHEN NEW.ordinal=1 BEGIN SELECT RAISE(ABORT,'synthetic manifest failure'); END;");
        await Assert.ThrowsExactlyAsync<SqliteException>(() => catalog.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId,
            setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt));
        Assert.IsNull(await catalog.ReadNamespaceRetirementBatchAsync(setup.Intent.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceRemoteRetirementTreeAsync(setup.Intent.RemoteEntry!.InstanceId, setup.Intent.RemoteEntry.BatchId, setup.Intent.RemoteEntry.Ordinal));
        Assert.HasCount(0, await catalog.ReadNamespaceDispatchFencedRootsAsync());
        Assert.HasCount(3, await catalog.ReadNamespaceMemberTreeAsync(setup.Intent.MemberId));
    }

    [TestMethod]
    [DataRow("owner")]
    [DataRow("missing-row")]
    [DataRow("entry-hash")]
    [DataRow("entry-payload")]
    public async Task OriginalRemoteRetirementTreeRejectsLostOrCorruptManifestFacts(string corrupt)
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteTreeAsync(catalog, 2);
        var batch = await catalog.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId, setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt);
        await ExecuteSqlAsync(fixture.Paths, corrupt switch
        {
            "owner" => "UPDATE namespace_remote_retirement_tree_owners SET tree_count=1;",
            "missing-row" => "DELETE FROM namespace_remote_retirement_tree_entries WHERE ordinal=1;",
            "entry-hash" => "UPDATE namespace_remote_retirement_tree_entries SET fingerprint=zeroblob(32) WHERE ordinal=1;",
            _ => "UPDATE namespace_remote_retirement_tree_entries SET payload=x'7b7d' WHERE ordinal=1;"
        });
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRemoteRetirementTreePageAsync(batch.OperationId));
        if (corrupt != "entry-payload") await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceRetirementBatchAsync(batch.OperationId));
        else CollectionAssert.Contains((await catalog.ReadNamespaceDispatchFencedRootsAsync()).ToArray(), batch.RootId);
    }

    [TestMethod]
    public async Task OriginalRemoteRetirementTreeCannotChangeAStoredDescendantRevisionOnReplay()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareRemoteTreeAsync(catalog, 1);
        var batch = await catalog.PrepareNamespaceRemoteRetirementTreeAsync(setup.Intent.OperationId, setup.Intent.RemoteEntry!, setup.Entries, setup.Intent.PreparedAt);
        var changed = setup.Entries.ToArray(); changed[1] = changed[1] with { OfficialRemoteRevision = "changed" };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceRemoteRetirementTreeAsync(batch.OperationId,
            batch.RemoteEntry!, changed, batch.PreparedAt));
        CollectionAssert.AreEqual(setup.Entries, (await catalog.ReadNamespaceRemoteRetirementTreePageAsync(batch.OperationId)).Entries.ToArray());
    }

    [TestMethod]
    public async Task OriginalBirthSdkEntryCannotBeReassignedToARemoteTreeDelete()
    {
        using var fixture = new Fixture(); await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var birth = await PrepareRemoteBirthParentAsync(catalog, true); var registered = await RegisterRemoteBornAsync(catalog, birth);
        var intent = await RemoteRetirementAsync(catalog, registered.Member); var remote = intent.RemoteEntry! with
        {
            InstanceId = birth.RemoteEntry!.InstanceId,
            BatchId = birth.RemoteEntry.BatchId,
            BatchFingerprint = birth.RemoteEntry.BatchFingerprint,
            BatchEntryCount = birth.RemoteEntry.BatchEntryCount,
            Ordinal = birth.RemoteEntry.Ordinal,
            ChangeId = birth.RemoteEntry.ChangeId
        };
        var entry = new MirrorPulseNamespaceRemoteRetirementTreeEntry(0, 0, remote.Child, remote.NativeRemoteRevision, remote.OfficialRemoteRevision);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareNamespaceRemoteRetirementTreeAsync(intent.OperationId,
            remote, [entry], intent.PreparedAt));
        Assert.IsNull(await catalog.ReadNamespaceRetirementBatchAsync(intent.OperationId));
    }

    private static async Task<(MirrorPulseNamespaceRetirementIntent Intent, MirrorPulseNamespaceRemoteRetirementTreeEntry[] Entries)> PrepareRemoteTreeAsync(
        MirrorPulseProductCatalog catalog, int children)
    {
        var setup = await PrepareMovableMemberAsync(catalog, directory: true);
        for (int index = 0; index < children; index++)
        {
            var nested = await AddBornAsync(catalog, NestedBirth(setup.Born, "child-" + index, false));
            await catalog.RegisterNamespaceMemberAsync(BornMember(nested, setup.Member));
        }
        var intent = await RemoteRetirementAsync(catalog, setup.Member);
        var graph = await catalog.ReadNamespaceMemberTreeAsync(setup.Member.MemberId);
        var entries = new List<MirrorPulseNamespaceRemoteRetirementTreeEntry>();
        foreach (var node in graph)
        {
            var snapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(node.MemberId))!;
            entries.Add(new(entries.Count, node.Depth, snapshot, entries.Count == 0 ? intent.RemoteEntry!.NativeRemoteRevision : null,
                entries.Count == 0 ? intent.RemoteEntry!.OfficialRemoteRevision : null));
        }
        intent = intent with { PreparedAt = entries.Select(entry => entry.Original.Context.Verification.ObservedAt).Append(intent.PreparedAt).Max().AddSeconds(1) };
        return (intent, entries.ToArray());
    }
}
