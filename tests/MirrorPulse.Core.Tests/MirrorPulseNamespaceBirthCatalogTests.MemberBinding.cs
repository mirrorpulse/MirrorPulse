using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task FullNativeBindingFindsOriginalAndBornMembershipAcrossOwners(bool born, bool directory)
    {
        using var fixture = new Fixture();
        MirrorPulseNamespaceMemberRegistration member;
        var parent = Baseline(); var proof = Protection(parent);
        await using (var first = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await ProtectAsync(first, parent, proof);
            var parentMember = await RegisterOriginalParentMembersAsync(first, parent, proof);
            if (born)
                member = BornMember(await AddBornAsync(first, Birth(parent, proof) with { IsDirectory = directory }), parentMember);
            else
            {
                var original = parent with
                {
                    EvidenceId = Guid.NewGuid(),
                    RelativePath = "Docs/existing",
                    IsDirectory = directory,
                    LocalObject = parent.LocalObject with { LocalFileId = Guid.NewGuid() }
                };
                var protection = Protection(original);
                await ProtectAsync(first, original, protection);
                member = OriginalMember(original, protection, parentMember.MemberId);
            }
            await first.RegisterNamespaceMemberAsync(member);
            Assert.AreEqual(member, await first.ReadNamespaceMemberByBindingAsync(member.LocalObject));
        }
        await using var second = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(member, await second.ReadNamespaceMemberByBindingAsync(member.LocalObject));
        Assert.AreEqual(member, await second.ReadNamespaceMemberAsync(member.MemberId));
        Assert.IsFalse(Directory.Exists(fixture.Paths.SyncRootPath));
    }

    [TestMethod]
    [DataRow("volume")]
    [DataRow("sync-root")]
    [DataRow("file-high-bits")]
    public async Task BindingLookupNeverAdoptsACompositeOrShortenedIdentity(string component)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var parent = Baseline(); var proof = Protection(parent);
        await ProtectAsync(catalog, parent, proof);
        var member = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        byte[] different = member.LocalObject.LocalFileId.ToByteArray(); different[15] ^= 1;
        var foreign = component switch
        {
            "volume" => member.LocalObject with { VolumeSerialNumber = member.LocalObject.VolumeSerialNumber + 1 },
            "sync-root" => member.LocalObject with { SyncRootFileId = Guid.NewGuid() },
            _ => member.LocalObject with { LocalFileId = new Guid(different) }
        };
        Assert.IsNull(await catalog.ReadNamespaceMemberByBindingAsync(foreign));
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberByBindingAsync(member.LocalObject));
    }

    [TestMethod]
    [DataRow("fingerprint")]
    [DataRow("index")]
    public async Task BindingLookupValidatesRetainedProvenanceRatherThanTrustingItsIndex(string damage)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var parent = Baseline(); var proof = Protection(parent);
        await ProtectAsync(catalog, parent, proof);
        var member = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        var queryBinding = damage == "index" ? member.LocalObject with { LocalFileId = Guid.NewGuid() } : member.LocalObject;
        await ExecuteSqlAsync(fixture.Paths, damage == "index"
            ? $"UPDATE namespace_members SET local_file_id='{queryBinding.LocalFileId:D}' WHERE member_id='{member.MemberId:D}';"
            : $"UPDATE namespace_members SET fingerprint=zeroblob(32) WHERE member_id='{member.MemberId:D}';");
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadNamespaceMemberByBindingAsync(queryBinding));
    }

    [TestMethod]
    public async Task CanceledBindingLookupDoesNotChangeMembershipOrNameOwnership()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var parent = Baseline(); var proof = Protection(parent);
        await ProtectAsync(catalog, parent, proof);
        var member = await RegisterOriginalParentMembersAsync(catalog, parent, proof);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => catalog.ReadNamespaceMemberByBindingAsync(member.LocalObject, cancellation.Token));
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberByBindingAsync(member.LocalObject));
        Assert.AreEqual(member, await catalog.ReadNamespaceMemberChildAsync(member.ParentMemberId!.Value, "DOCS"));
    }
}
