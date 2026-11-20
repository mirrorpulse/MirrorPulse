using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task ImportContentAndIdentityRemainImmutableAcrossOwnersWithoutFilesystemOrRemoteEffects()
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original);
        var birth = Birth(original, protection) with { Origin = MirrorPulseNamespaceBirthOrigin.Import };
        var content = new MirrorPulseNamespaceImportContent(4, Convert.ToHexString(SHA256.HashData([1, 2, 3, 4])));
        var plan = Plan(birth) with { Version = 2, Content = content };
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, original, protection);
            await first.PrepareNamespaceBirthAsync(birth);
            Assert.AreEqual(plan, await first.PrepareNamespaceBirthPlanAsync(plan));
            Assert.IsNull(await first.ReadNamespaceBirthStartAsync(birth.OperationId));
            Assert.IsNull(await first.ReadNamespaceBirthConversionAsync(birth.OperationId));
            Assert.IsNull(await first.ReadNamespaceBirthObservationAsync(birth.OperationId));
            Assert.IsNull(await first.ReadMutationAsync(birth.OperationId));
        }
        await using (var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(plan, await second.ReadNamespaceBirthPlanAsync(birth.OperationId));
            Assert.AreEqual(plan, await second.PrepareNamespaceBirthPlanAsync(plan));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespaceBirthPlanAsync(plan with
            { Content = content with { Length = 5 } }));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => second.PrepareNamespaceBirthPlanAsync(plan with
            { Content = content with { Sha256 = new('A', 64) } }));
            Assert.AreEqual(plan, await second.ReadNamespaceBirthPlanAsync(birth.OperationId));
            var start = Start(plan);
            Assert.AreEqual(start, (await second.RecordNamespaceBirthStartAsync(start)).Start);
            Assert.IsNull(await second.ReadNamespaceBirthObservationAsync(birth.OperationId));
        }
        Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow(1, true, 0L, "upper")]
    [DataRow(2, false, 0L, "upper")]
    [DataRow(3, true, 0L, "upper")]
    [DataRow(2, true, -1L, "upper")]
    [DataRow(2, true, 0L, "lower")]
    [DataRow(2, true, 0L, "short")]
    [DataRow(2, true, 0L, "invalid")]
    public async Task MalformedImportPlanCannotStartOrChangeItsReservation(int version, bool hasContent, long length, string digestKind)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original);
        var birth = Birth(original, protection) with { Origin = MirrorPulseNamespaceBirthOrigin.Import };
        string digest = digestKind switch { "lower" => new('a', 64), "short" => new('A', 63), "invalid" => new('G', 64), _ => new('A', 64) };
        var plan = Plan(birth) with { Version = version, Content = hasContent ? new(length, digest) : null };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceBirthPlanAsync(plan));
        Assert.IsNull(await catalog.ReadNamespaceBirthPlanAsync(birth.OperationId));
        Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(birth.OperationId));
        Assert.AreEqual(birth, await catalog.ReadNamespaceBirthAsync(birth.OperationId));
    }

    [TestMethod]
    [DataRow(MirrorPulseNamespaceBirthOrigin.ControlledCreation, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.RemotePopulation, false)]
    [DataRow(MirrorPulseNamespaceBirthOrigin.Import, true)]
    public async Task ContentPlanCannotSubstituteForAnotherBirthKind(MirrorPulseNamespaceBirthOrigin origin, bool isDirectory)
    {
        using var fixture = new Fixture();
        var original = Baseline(); var protection = Protection(original);
        var birth = Birth(original, protection) with { Origin = origin, IsDirectory = isDirectory };
        var plan = Plan(birth) with { Version = 2, Content = new(0, Convert.ToHexString(SHA256.HashData([]))) };
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await ProtectAsync(catalog, original, protection);
        await catalog.PrepareNamespaceBirthAsync(birth);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthPlanAsync(plan));
        Assert.IsNull(await catalog.ReadNamespaceBirthPlanAsync(birth.OperationId));
    }

    [TestMethod]
    public void LegacyBirthPlanSerializationKeepsTheOriginalPayloadWithoutAContentField()
    {
        var plan = new MirrorPulseNamespaceBirthPlan(1, Guid.NewGuid(), Guid.NewGuid(), "original-identity", null, DateTimeOffset.UtcNow);
        byte[] original = JsonSerializer.SerializeToUtf8Bytes(new
        { plan.Version, plan.OperationId, plan.ItemId, plan.RemoteId, plan.RemoteRevision, plan.PreparedAt });
        CollectionAssert.AreEqual(original, JsonSerializer.SerializeToUtf8Bytes(plan));
    }
}
