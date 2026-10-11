using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private static async Task RegisterNativeOriginalMembersAsync(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role,
        List<MirrorPulseNamespacePermissionPreparation> originals,
        Dictionary<Guid, MirrorPulseNamespaceMemberRegistration> members, CancellationToken token)
    {
        await role.RunNamespaceOperationAsync(async () =>
        {
            foreach (var original in originals)
            {
                var proof = await catalog.ReadLatestNamespacePermissionChangeAsync(original.Baseline.EvidenceId, token);
                Assert.IsNotNull(proof?.Verification);
                CloudItem item = NativeOriginalMemberItem(fileSystem, original.Baseline);
                int separator = original.Baseline.RelativePath.LastIndexOf('/');
                CloudDirectory? parent = original.Baseline.RootId is null ? null : separator < 0 ? fileSystem.Root :
                    fileSystem.GetDirectory(original.Baseline.RelativePath[..separator]);
                var member = new MirrorPulseNamespaceMemberRegistration(1, Guid.NewGuid(), parent is null ? null :
                    members.Values.Single(value => value.IsDirectory && value.RelativePath == parent.RelativePath).MemberId,
                    MirrorPulseNamespaceMemberOrigin.CapturedOriginal, original.Baseline.EvidenceId, proof.Intent.OperationId,
                    proof.Intent.RootId, proof.Intent.RelativePath, original.Baseline.LocalObject, original.Baseline.IsDirectory,
                    original.Baseline.OwnerSid, DateTimeOffset.UtcNow);
                using var canceled = new CancellationTokenSource(); canceled.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(() => MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, canceled.Token));
                Assert.IsNull(await catalog.ReadNamespaceMemberAsync(member.MemberId, token));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => role.RunNormalUserOperationAsync(
                    () => MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, token)));
                Assert.AreEqual(member, await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, token));
                Assert.AreEqual(member, await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, token));
                members.Add(member.OriginId, member);
            }
        });
    }

    private static CloudItem NativeOriginalMemberItem(CloudFileSystem fileSystem, MirrorPulseNamespacePermissionBaseline original) =>
        original.RootId is null ? fileSystem.Root : original.IsDirectory ? fileSystem.GetDirectory(original.RelativePath) : fileSystem.GetFile(original.RelativePath);

    private static async Task RegisterNativeBornMemberAsync(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role, Guid birthId,
        Dictionary<Guid, MirrorPulseNamespaceMemberRegistration> members, CancellationToken token)
    {
        var birth = await catalog.ReadNamespaceBirthAsync(birthId, token); Assert.IsNotNull(birth);
        var observation = await catalog.ReadNamespaceBirthObservationAsync(birthId, token); Assert.IsNotNull(observation);
        var proof = await catalog.ReadLatestNamespaceBirthProtectionAsync(birthId, token); Assert.IsNotNull(proof);
        var parentMember = members[birth.BornParent?.BirthOperationId ?? birth.ParentEvidenceId];
        var member = new MirrorPulseNamespaceMemberRegistration(1, Guid.NewGuid(), parentMember.MemberId,
            MirrorPulseNamespaceMemberOrigin.ProtectedBirth, birthId, proof.ProtectionId, birth.RootId, birth.RelativePath,
            observation.LocalObject, birth.IsDirectory, observation.OwnerSid, DateTimeOffset.UtcNow);
        CloudItem item = birth.IsDirectory ? fileSystem.GetDirectory(birth.RelativePath) : fileSystem.GetFile(birth.RelativePath);
        CloudDirectory parent = fileSystem.GetDirectory(parentMember.RelativePath);
        await role.RunNamespaceOperationAsync(async () =>
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => role.RunNormalUserOperationAsync(
                () => MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, token)));
            await Assert.ThrowsAsync<InvalidDataException>(() => MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member,
                item, fileSystem.Root, router, token));
            await Assert.ThrowsAsync<InvalidDataException>(() => MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog,
                member with { MemberId = Guid.NewGuid(), LocalObject = member.LocalObject with { LocalFileId = Guid.NewGuid() } }, item, parent, router, token));
            Assert.AreEqual(member, await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, token));
            Assert.AreEqual(member, await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, item, parent, router, token));
            string leaf = birth.RelativePath[(birth.RelativePath.LastIndexOf('/') + 1)..];
            Assert.AreEqual(member, await catalog.ReadNamespaceMemberChildAsync(parentMember.MemberId, leaf.ToUpperInvariant(), token));
            var currentParent = await MirrorPulseNamespaceProtectionProofReader.ReadMemberAsync(catalog, parentMember, token);
            var collision = birth with
            {
                OperationId = Guid.NewGuid(),
                RelativePath = parentMember.RelativePath + "/" + leaf.ToUpperInvariant(),
                ParentPermissionOperationId = currentParent.PermissionOperationId,
                BornParent = currentParent.BornParent,
                RoleSid = currentParent.RoleSid,
                PreparedAt = DateTimeOffset.UtcNow,
            };
            await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.PrepareNamespaceBirthAsync(collision, token));
            Assert.IsNull(await catalog.ReadNamespaceBirthAsync(collision.OperationId, token));
        });
        members.Add(birthId, member);
        var entry = (await catalog.ReadNamespaceBirthRecoveryPageAsync(await catalog.BeginNamespaceBirthRecoveryScanAsync(token), 0, 64, token))
            .Entries.Single(value => value.Intent.OperationId == birthId);
        Assert.IsFalse(entry.OwnsNameReservation); Assert.AreEqual(member, entry.MemberRegistration);
        Assert.AreEqual(observation, entry.Observation);
    }

    private static async Task VerifyNativeMemberRegistrationsAsync(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role,
        IReadOnlyDictionary<Guid, MirrorPulseNamespaceMemberRegistration> members, CancellationToken token)
    {
        await role.RunNamespaceOperationAsync(async () =>
        {
            foreach (var original in members.Values)
            {
                Assert.AreEqual(original, await catalog.ReadNamespaceMemberAsync(original.MemberId, token));
                CloudItem item = original.IsDirectory ? fileSystem.GetDirectory(original.RelativePath) : fileSystem.GetFile(original.RelativePath);
                CloudDirectory? parent = original.ParentMemberId is null ? null : fileSystem.GetDirectory(
                    members.Values.Single(value => value.MemberId == original.ParentMemberId).RelativePath);
                Assert.AreEqual(original, await MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, original, item, parent, router, token));
            }
        });
    }

    private static async Task RejectNativeStagedMemberBeforeObservationAsync(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
        MirrorPulseRootRouter router, MirrorPulseNamespaceExecutionSession role, NativeStagedBirth staged,
        MirrorPulseNamespaceMemberRegistration parentMember, CancellationToken token)
    {
        var member = new MirrorPulseNamespaceMemberRegistration(1, Guid.NewGuid(), parentMember.MemberId,
            MirrorPulseNamespaceMemberOrigin.ProtectedBirth, staged.Birth.OperationId, Guid.NewGuid(), staged.Birth.RootId,
            staged.Birth.RelativePath, staged.Conversion.LocalObject, false, staged.Conversion.OwnerSid, DateTimeOffset.UtcNow);
        await role.RunNamespaceOperationAsync(async () => await Assert.ThrowsAsync<FileNotFoundException>(() =>
            MirrorPulseNamespaceMemberRegistrar.RegisterAsync(catalog, member, fileSystem.GetFile(staged.Birth.RelativePath),
                fileSystem.GetDirectory(parentMember.RelativePath), router, token)));
        Assert.IsNull(await catalog.ReadNamespaceMemberAsync(member.MemberId, token));
        var entry = (await catalog.ReadNamespaceBirthRecoveryPageAsync(await catalog.BeginNamespaceBirthRecoveryScanAsync(token), 0, 64, token))
            .Entries.Single(value => value.Intent.OperationId == staged.Birth.OperationId);
        Assert.IsNull(entry.Observation); Assert.IsNull(entry.MemberRegistration); Assert.IsTrue(entry.OwnsNameReservation);
    }
}
