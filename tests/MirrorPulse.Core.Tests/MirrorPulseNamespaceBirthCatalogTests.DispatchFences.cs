using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation, true)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import, true)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation, true)]
    public async Task UnregisteredBirthFencesDispatchAcrossOwnersUntilRegistration(
        MirrorPulseNamespaceBirthOrigin origin, bool observed)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var proof = Protection(original);
        var birth = Birth(original, proof) with { Origin = origin };
        BornObject? born = null;
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, proof);
            await first.PrepareNamespaceBirthAsync(birth);
            if (observed) born = await AddBornAsync(first, birth);
            CollectionAssert.AreEqual(new[] { birth.RootId }, (await first.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
            Assert.HasCount(0, await first.ReadNamespaceRecoveryRootsAsync());
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        CollectionAssert.AreEqual(new[] { birth.RootId }, (await second.ReadNamespaceDispatchFencedRootsAsync()).ToArray());
        Assert.AreEqual(birth, await second.PrepareNamespaceBirthAsync(birth));
        // Recovery/bookkeeping can still finish the original birth while dispatch is fenced.
        var parentMember = await RegisterOriginalParentMembersAsync(second, original, proof);
        born ??= await AddBornAsync(second, birth);
        var member = BornMember(born, parentMember);
        Assert.AreEqual(member, await second.RegisterNamespaceMemberAsync(member));
        Assert.HasCount(0, await second.ReadNamespaceDispatchFencedRootsAsync());
        Assert.AreEqual(birth, await second.ReadNamespaceBirthAsync(birth.OperationId));
        Assert.AreEqual(born.Observation, await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        Assert.AreEqual(member, await second.ReadNamespaceMemberAsync(member.MemberId));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => second.ReadNamespaceDispatchFencedRootsAsync(cancellation.Token));
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("reservation-missing")]
    [DataRow("reservation-name")]
    [DataRow("intent-fingerprint")]
    [DataRow("operation-index")]
    public async Task DamagedUnregisteredBirthCannotSilentlyReleaseItsDispatchFence(string damage)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var proof = Protection(original); var birth = Birth(original, proof);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, proof);
            await first.PrepareNamespaceBirthAsync(birth);
        }
        string sql = damage switch
        {
            "reservation-missing" => "DELETE FROM namespace_birth_reservations;",
            "reservation-name" => "UPDATE namespace_birth_reservations SET child_name_key='OTHER.TXT';",
            "intent-fingerprint" => "UPDATE namespace_birth_intents SET fingerprint=zeroblob(32);",
            _ => "PRAGMA foreign_keys=OFF; UPDATE namespace_birth_reservations SET operation_id='{' || operation_id || '}';"
        };
        await ExecuteSqlAsync(fixture.Paths, sql);
        await using var current = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => current.ReadNamespaceDispatchFencedRootsAsync());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => current.ReadNamespacePendingOperationsAsync(birth.RootId));
        Assert.HasCount(0, await current.ReadNamespaceRecoveryRootsAsync());
    }
}
