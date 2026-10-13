using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    private static readonly JsonSerializerOptions RetirementBatchJsonOptions = new() { Converters = { new RetirementBatchRootIdConverter() } };

    [TestMethod]
    public async Task MalformedRetirementBatchFenceCannotAdmitANewBirth()
    {
        using var fixture = new Fixture();
        MoveFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first);
            await first.PrepareNamespaceRetirementBatchAsync(Guid.NewGuid(), setup.Member.MemberId);
        }
        await ExecuteSqlAsync(fixture.Paths, "UPDATE namespace_member_retirement_batches SET operation_id='invalid';");
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.PrepareNamespaceBirthAsync(
            Birth(setup.Parent, setup.ParentProof) with { RelativePath = "Docs/blocked-by-damaged-fence.txt" }));
    }

    [TestMethod]
    public async Task SubtreeRetirementResumesLeafFirstAndReleasesFenceOnlyAfterEveryReceipt()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceRetirementBatch batch;
        MoveFixture setup;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(first, directory: true);
            var nested = await AddBornAsync(first, NestedBirth(setup.Born, "nested", false));
            var child = BornMember(nested, setup.Member);
            await first.RegisterNamespaceMemberAsync(child);
            batch = await first.PrepareNamespaceRetirementBatchAsync(Guid.NewGuid(), setup.Member.MemberId);
            var parentIntent = BatchRetirement(batch, setup.Member, setup.Born.Protection.RoleSid);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.PrepareNamespaceRetirementAsync(parentIntent));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => first.CompleteNamespaceRetirementBatchAsync(batch.OperationId));
            var childIntent = BatchRetirement(batch, child, nested.Protection.RoleSid);
            await first.PrepareNamespaceRetirementAsync(childIntent);
            await first.RecordNamespaceRetirementStartAsync(childIntent.OperationId, childIntent.PreparedAt);
            await first.RecordNamespaceRetirementNativeDeletedAsync(childIntent.OperationId, childIntent.PreparedAt.AddSeconds(1));
            await first.CompleteNamespaceRetirementAsync(childIntent.OperationId, childIntent.PreparedAt.AddSeconds(2));
            CollectionAssert.Contains((await first.ReadNamespaceRecoveryRootsAsync()).ToArray(), batch.RootId);
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var retained = await second.PrepareNamespaceRetirementBatchAsync(batch.OperationId, batch.MemberId);
        CollectionAssert.AreEqual(batch.Entries.ToArray(), retained.Entries.ToArray());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.CompleteNamespaceRetirementBatchAsync(batch.OperationId));
        var last = BatchRetirement(batch, setup.Member, setup.Born.Protection.RoleSid);
        await second.PrepareNamespaceRetirementAsync(last);
        await second.RecordNamespaceRetirementStartAsync(last.OperationId, last.PreparedAt);
        await second.RecordNamespaceRetirementNativeDeletedAsync(last.OperationId, last.PreparedAt.AddSeconds(1));
        await second.CompleteNamespaceRetirementAsync(last.OperationId, last.PreparedAt.AddSeconds(2));
        var completed = await second.CompleteNamespaceRetirementBatchAsync(batch.OperationId);
        Assert.AreEqual(MirrorPulseNamespaceRetirementBatchPhase.Completed, completed.Phase);
        Assert.AreEqual(completed.OperationId, (await second.CompleteNamespaceRetirementBatchAsync(batch.OperationId)).OperationId);
        CollectionAssert.DoesNotContain((await second.ReadNamespaceRecoveryRootsAsync()).ToArray(), batch.RootId);
        Assert.IsNull(await second.ReadNamespaceMemberChildAsync(setup.ParentMember.MemberId, "new.txt"));
        Assert.AreEqual(setup.Member, await second.ReadNamespaceMemberAsync(setup.Member.MemberId));
    }

    [TestMethod]
    [DataRow("operation")]
    [DataRow("binding")]
    [DataRow("batch")]
    public async Task BatchMemberAdmissionRejectsUnsealedOperationOrObject(string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var batch = await catalog.PrepareNamespaceRetirementBatchAsync(Guid.NewGuid(), setup.Member.MemberId);
        var intent = BatchRetirement(batch, setup.Member, setup.Born.Protection.RoleSid);
        intent = damage switch
        {
            "operation" => intent with { OperationId = Guid.NewGuid() },
            "binding" => intent with { LocalObject = setup.Parent.LocalObject },
            _ => intent with { BatchOperationId = Guid.NewGuid() }
        };
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceRetirementAsync(intent));
        Assert.IsNull(await catalog.ReadNamespaceRetirementAsync(intent.OperationId));
        CollectionAssert.Contains((await catalog.ReadNamespaceRecoveryRootsAsync()).ToArray(), batch.RootId);
    }

    private static MirrorPulseNamespaceRetirementIntent BatchRetirement(MirrorPulseNamespaceRetirementBatch batch,
        MirrorPulseNamespaceMemberRegistration member, string roleSid) =>
        new(1, batch.MemberOperationId(member.MemberId), member.MemberId, batch.RootId, member.LocalObject,
            batch.Entries.Single(entry => entry.MemberId == member.MemberId).RelativePath, member.IsDirectory,
            member.OwnerSid, roleSid, batch.PreparedAt.AddSeconds(1))
        { BatchOperationId = batch.OperationId };

    [TestMethod]
    public async Task BothRetirementAdmissionsRejectTheFirstLevelManagedRoot()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var setup = await PrepareMovableMemberAsync(catalog);
        var root = setup.ParentMember;
        var intent = new MirrorPulseNamespaceRetirementIntent(1, Guid.NewGuid(), root.MemberId,
            root.RootId!.Value, root.LocalObject, root.RelativePath, true, root.OwnerSid,
            setup.ParentProof.RoleSid, DateTimeOffset.UtcNow);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceRetirementAsync(intent));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceRetirementBatchAsync(Guid.NewGuid(), root.MemberId));
        Assert.IsNull(await catalog.ReadNamespaceRetirementAsync(intent.OperationId));
        Assert.AreEqual(root, await catalog.ReadNamespaceMemberAsync(root.MemberId));
    }

    [TestMethod]
    public async Task RecoveryRequiredSubtreeStillFencesTheRootAcrossCatalogOwners()
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceRetirementBatch batch;
        MoveFixture setup;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            setup = await PrepareMovableMemberAsync(catalog);
            batch = await catalog.PrepareNamespaceRetirementBatchAsync(Guid.NewGuid(), setup.Member.MemberId);
        }
        await RewriteRetirementBatchAsync(fixture, batch with { Phase = MirrorPulseNamespaceRetirementBatchPhase.RecoveryRequired });
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        CollectionAssert.Contains((await reopened.ReadNamespaceRecoveryRootsAsync()).ToArray(), batch.RootId);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.PrepareNamespaceBirthAsync(
            Birth(setup.Parent, setup.ParentProof) with { RelativePath = "Docs/blocked.txt" }));
    }

    [TestMethod]
    [DataRow("duplicate")]
    [DataRow("parent")]
    [DataRow("depth")]
    [DataRow("path")]
    public async Task SubtreeReadRejectsDisconnectedEntriesEvenWithValidPayloadHash(string damage)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceRetirementBatch batch;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var setup = await PrepareMovableMemberAsync(catalog, directory: true);
            var nested = await AddBornAsync(catalog, NestedBirth(setup.Born, "nested", false));
            await catalog.RegisterNamespaceMemberAsync(BornMember(nested, setup.Member));
            batch = await catalog.PrepareNamespaceRetirementBatchAsync(Guid.NewGuid(), setup.Member.MemberId);
        }
        var entries = batch.Entries.ToArray();
        entries[1] = damage switch
        {
            "duplicate" => entries[0],
            "parent" => entries[1] with { ParentMemberId = Guid.NewGuid() },
            "depth" => entries[1] with { Depth = 0 },
            _ => entries[1] with { RelativePath = "Docs/elsewhere/nested" }
        };
        await RewriteRetirementBatchAsync(fixture, batch with { Entries = entries });
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => reopened.ReadNamespaceRetirementBatchAsync(batch.OperationId));
    }

    private static async Task RewriteRetirementBatchAsync(Fixture fixture, MirrorPulseNamespaceRetirementBatch batch)
    {
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(batch, RetirementBatchJsonOptions);
        await using var connection = Connection(fixture.Paths);
        await connection.OpenAsync();
        await using var update = connection.CreateCommand();
        update.CommandText = "UPDATE namespace_member_retirement_batches SET payload=$payload,fingerprint=$hash,phase=$phase WHERE operation_id=$id;";
        update.Parameters.AddWithValue("$payload", payload);
        update.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        update.Parameters.AddWithValue("$phase", (int)batch.Phase);
        update.Parameters.AddWithValue("$id", batch.OperationId.ToString("D"));
        Assert.AreEqual(1, await update.ExecuteNonQueryAsync());
    }

    private sealed class RetirementBatchRootIdConverter : JsonConverter<RootId>
    {
        public override RootId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => RootId.Parse(reader.GetString()!);
        public override void Write(Utf8JsonWriter writer, RootId value, JsonSerializerOptions options) => writer.WriteStringValue(value.ToString());
    }
}
