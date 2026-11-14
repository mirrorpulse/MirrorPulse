using MirrorPulse.Core.Caching;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceBirthCatalogTests
{
    [TestMethod]
    public async Task DirectoryTargetReadbackRequiresOriginalAcceptancesAndRetainsSelectedRevisions()
    {
        using var fixture = new Fixture();
        MirrorPulseDirectoryTransferAdmission plan;
        MirrorPulseDirectoryTransferTargetVerification verified;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            plan = await PrepareDirectoryTransferFixtureAsync(catalog, TransferBytes);
            await catalog.PrepareNamespaceDirectoryTransferAsync(plan);
            var worker = new DirectoryTransferCopyWorker { Plan = plan };
            var verifier = new MirrorPulseDirectoryTransferTargetVerifier(
                new MirrorPulseTransferTargetVerifier(catalog, worker, worker, worker), worker,
                new TransferClock(plan.PreparedAt.AddSeconds(2)));
            var prepared = await catalog.ReadNamespaceDirectoryTransferCopyAsync(plan.Command.CommandId, default);
            await Assert.ThrowsExactlyAsync<MirrorPulseMutationAmbiguousException>(() => verifier.ReadCurrentAsync(prepared, null, default));

            string source = Path.Combine(fixture.Paths.DataRootPath, "directory-content.bin");
            await File.WriteAllBytesAsync(source, TransferBytes);
            var inputs = new MirrorPulseLocalTransferContentStore(fixture.Paths);
            await using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                await inputs.StageRetainedAsync(plan.Members[1].PrivateContentId!.Value, file);
            var copy = await new MirrorPulseDirectoryTransferCopyExecutor(catalog, inputs, worker, worker,
                new TransferClock(plan.PreparedAt.AddSeconds(1))).CopyAsync(plan.Command.CommandId, default);
            verified = await verifier.ReadCurrentAsync(copy, null, default);
            Assert.AreEqual("directory-create-receipt", copy.Steps[0].AcceptedRevision);
            Assert.AreEqual("directory-after-children", verified.Members[0].Revision);
            Assert.AreEqual("file-upload-receipt", verified.Members[1].Revision);
            await catalog.RecordNamespaceDirectoryTransferTargetAsync(verified, default);
            CollectionAssert.AreEqual(verified.Members.ToArray(),
                (await catalog.ReadNamespaceDirectoryTransferTargetAsync(plan.Command.CommandId, default))!.Members.ToArray());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                catalog.RecordNamespaceDirectoryTransferTargetAsync(verified with { VerifiedAt = verified.VerifiedAt.AddSeconds(1) }, default));
            await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
                catalog.RecordNamespaceDirectoryTransferTargetAsync(verified with
                {
                    Members = [verified.Members[0], verified.Members[1] with { Revision = "unaccepted-file-revision" }]
                }, default));
            CollectionAssert.AreEqual(verified.Members.ToArray(),
                (await verifier.ReadCurrentAsync(copy, verified, default)).Members.ToArray());
            worker.ExtraTargetChild = true;
            await Assert.ThrowsExactlyAsync<IOException>(() => verifier.ReadCurrentAsync(copy, verified, default));
            worker.ExtraTargetChild = false;
            worker.DuplicateTargetIdentity = true;
            await Assert.ThrowsExactlyAsync<IOException>(() => verifier.ReadCurrentAsync(copy, null, default));
            worker.DuplicateTargetIdentity = false;
            worker.ChangedTargetDirectory = true;
            await Assert.ThrowsExactlyAsync<IOException>(() => verifier.ReadCurrentAsync(copy, verified, default));
            Assert.HasCount(2, await catalog.ReadNamespaceDispatchFencedRootsAsync());
            Assert.IsNull(await catalog.ReadMutationAsync(plan.Members[0].DeleteOperationId));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var recovered = await reopened.ReadNamespaceDirectoryTransferTargetAsync(plan.Command.CommandId, default);
        Assert.IsNotNull(recovered);
        Assert.AreEqual(verified.VerifiedAt, recovered.VerifiedAt);
        CollectionAssert.AreEqual(verified.Members.ToArray(), recovered.Members.ToArray());
        Assert.HasCount(2, await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    [TestMethod]
    public async Task DirectoryTransferBirthCanResumeUnderTheOriginalRegisteredTargetParent()
    {
        using var fixture = new Fixture();
        MirrorPulseDirectoryTransferTargetBirth directory;
        MirrorPulseDirectoryTransferTargetBirth file;
        MirrorPulseDirectoryTransferTargetVerification verified;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var original = await PrepareDirectoryTransferFixtureAsync(catalog, TransferBytes);
            await catalog.PrepareNamespaceDirectoryTransferAsync(original);
            var worker = new DirectoryTransferCopyWorker { Plan = original };
            var inputs = new MirrorPulseLocalTransferContentStore(fixture.Paths);
            string source = Path.Combine(fixture.Paths.DataRootPath, "target-birth-input.bin");
            await File.WriteAllBytesAsync(source, TransferBytes);
            await using (var stream = File.OpenRead(source))
                await inputs.StageRetainedAsync(original.Members[1].PrivateContentId!.Value, stream);
            var copy = await new MirrorPulseDirectoryTransferCopyExecutor(catalog, inputs, worker, worker,
                new TransferClock(original.PreparedAt.AddSeconds(1))).CopyAsync(original.Command.CommandId, default);
            var verifier = new MirrorPulseDirectoryTransferTargetVerifier(
                new MirrorPulseTransferTargetVerifier(catalog, worker, worker, worker), worker,
                new TransferClock(original.PreparedAt.AddSeconds(2)));
            verified = await verifier.ReadCurrentAsync(copy, null, default);
            directory = DirectoryTransferBirth(original, verified, original.TargetParent, 0, verified.VerifiedAt.AddSeconds(1));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceDirectoryTransferBirthAsync(directory, default));
            await catalog.RecordNamespaceDirectoryTransferTargetAsync(verified, default);
            var incorrect = directory with { Plan = directory.Plan with { RemoteRevision = "directory-create-receipt" } };
            await Assert.ThrowsExactlyAsync<ArgumentException>(() => catalog.PrepareNamespaceDirectoryTransferBirthAsync(incorrect, default));
            Assert.IsNull(await catalog.ReadNamespaceBirthAsync(directory.Birth.OperationId));
            Assert.AreEqual(directory, await catalog.PrepareNamespaceDirectoryTransferBirthAsync(directory, default));
            Assert.IsNull(await catalog.ReadNamespaceBirthStartAsync(directory.Birth.OperationId));
            var premature = DirectoryTransferBirth(original, verified, original.TargetParent, 1, verified.VerifiedAt.AddSeconds(2));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.PrepareNamespaceDirectoryTransferBirthAsync(premature, default));
            Assert.IsNull(await catalog.ReadNamespaceBirthAsync(premature.Birth.OperationId));

            var start = Start(directory.Plan);
            await catalog.RecordNamespaceBirthStartAsync(start);
            var observation = Observation(directory.Birth, directory.Plan, start) with { IsInSync = true };
            await catalog.RecordNamespaceBirthObservationAsync(observation);
            var protection = new MirrorPulseNamespaceBirthProtection(1, Guid.NewGuid(), directory.Birth.OperationId, null,
                directory.Birth.ParentPermissionOperationId, directory.Birth.RoleSid,
                new(observation.LocalObject, observation.OwnerSid, observation.BirthDacl, observation.ObservedAt.AddSeconds(1)), true, 1);
            await catalog.RecordNamespaceBirthProtectionAsync(protection);
            var parent = original.TargetParent.Context.Member;
            var registered = BornMember(new(directory.Birth, directory.Plan, start, observation, protection), parent)
                with
            { MemberId = directory.Birth.OperationId };
            await catalog.RegisterNamespaceMemberAsync(registered);
            var snapshot = (await catalog.ReadNamespaceMemberProtectionSnapshotAsync(registered.MemberId))!;
            file = DirectoryTransferBirth(original, verified, snapshot, 1, registered.RegisteredAt.AddSeconds(1));
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(directory, await reopened.ReadNamespaceDirectoryTransferBirthAsync(directory.TransferId, 0, default));
        Assert.AreEqual(file, await reopened.PrepareNamespaceDirectoryTransferBirthAsync(file, default));
        Assert.AreEqual(file, await reopened.PrepareNamespaceDirectoryTransferBirthAsync(file, default));
        Assert.AreEqual(file.Birth, await reopened.ReadNamespaceBirthAsync(file.Birth.OperationId));
        Assert.AreEqual(file.Plan, await reopened.ReadNamespaceBirthPlanAsync(file.Birth.OperationId));
        Assert.IsNull(await reopened.ReadNamespaceBirthStartAsync(file.Birth.OperationId));
        Assert.HasCount(2, await reopened.ReadNamespaceDispatchFencedRootsAsync());
    }

    private static MirrorPulseDirectoryTransferTargetBirth DirectoryTransferBirth(MirrorPulseDirectoryTransferAdmission original,
        MirrorPulseDirectoryTransferTargetVerification verified, MirrorPulseNamespaceMemberProtectionSnapshot parent, int ordinal,
        DateTimeOffset time)
    {
        Guid birthId = Guid.NewGuid();
        var selected = verified.Members[ordinal];
        var birth = new MirrorPulseNamespaceBirthIntent(4, birthId, original.TargetRoute.RootId,
            parent.BornParent is null ? parent.Context.Member.OriginId : Guid.Empty,
            parent.BornParent is null ? parent.PermissionOperationId : Guid.Empty, parent.Context.Member.LocalObject,
            original.Members[ordinal].TargetRelativePath, selected.IsDirectory, MirrorPulseNamespaceBirthOrigin.TransferTarget,
            parent.Context.Protection.RoleSid, time)
        {
            BornParent = parent.BornParent,
            TransferEntry = MirrorPulseNamespaceTransferBirthReference.Create(verified, selected.CopyOperationId)
        };
        var plan = new MirrorPulseNamespaceBirthPlan(selected.Content is null ? 1 : 2, birthId, Guid.NewGuid(),
            "target:" + selected.RemoteId, selected.Revision, time)
        { Content = selected.Content };
        return new(original.Command.CommandId, ordinal, birth, plan);
    }
}
