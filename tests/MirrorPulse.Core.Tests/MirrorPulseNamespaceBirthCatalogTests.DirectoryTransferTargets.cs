using MirrorPulse.Adapter.Sdk;
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
        AdapterAtomicDirectoryDeletionRequest deleteRequest;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var original = await PrepareDirectoryTransferFixtureAsync(catalog, TransferBytes);
            deleteRequest = DirectoryTransferDeleteRequest(original);
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
        var fresh = verified with { VerifiedAt = file.Birth.PreparedAt.AddSeconds(10) };
        DateTimeOffset started = fresh.VerifiedAt.AddSeconds(1);
        var originalReceipt = new AdapterAtomicDirectoryDeletionReceipt(1, deleteRequest.OriginalRequest.InstanceId, directory.TransferId,
            Guid.NewGuid(), deleteRequest.RequestFingerprint, deleteRequest.Admission.AdmissionFingerprint, deleteRequest.ManifestFingerprint,
            deleteRequest.OriginalRequest.Operations.Select((step, ordinal) => new AdapterAtomicDirectoryDeletedOriginal(
                step.Operation.OperationId, new(step.Operation.RootKey, step.Operation.Path),
                deleteRequest.Admission.Operations[ordinal].OriginalGeneration)).ToArray(), "AQ==");
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            Assert.AreEqual(directory, await reopened.ReadNamespaceDirectoryTransferBirthAsync(directory.TransferId, 0, default));
            Assert.AreEqual(file, await reopened.PrepareNamespaceDirectoryTransferBirthAsync(file, default));
            Assert.AreEqual(file, await reopened.PrepareNamespaceDirectoryTransferBirthAsync(file, default));
            Assert.AreEqual(file.Birth, await reopened.ReadNamespaceBirthAsync(file.Birth.OperationId));
            Assert.AreEqual(file.Plan, await reopened.ReadNamespaceBirthPlanAsync(file.Birth.OperationId));
            Assert.IsNull(await reopened.ReadNamespaceBirthStartAsync(file.Birth.OperationId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => reopened.StartNamespaceDirectoryTransferSourceDeletionAsync(
                directory.TransferId, fresh, deleteRequest, started, default));
            Assert.IsNull(await reopened.ReadNamespaceDirectoryTransferSourceDeletionAsync(directory.TransferId, default));

            var start = Start(file.Plan);
            await reopened.RecordNamespaceBirthStartAsync(start);
            var observation = Observation(file.Birth, file.Plan, start) with { IsInSync = true };
            await reopened.RecordNamespaceBirthObservationAsync(observation);
            var protection = new MirrorPulseNamespaceBirthProtection(2, Guid.NewGuid(), file.Birth.OperationId, null, Guid.Empty,
                file.Birth.RoleSid, new(observation.LocalObject, observation.OwnerSid, observation.BirthDacl,
                    observation.ObservedAt.AddSeconds(1)), true, 1)
            { BornParent = file.Birth.BornParent };
            await reopened.RecordNamespaceBirthProtectionAsync(protection);
            var parent = (await reopened.ReadNamespaceMemberAsync(directory.Birth.OperationId))!;
            await reopened.RegisterNamespaceMemberAsync(BornMember(new(file.Birth, file.Plan, start, observation, protection), parent)
                with
            { MemberId = file.Birth.OperationId });
            var first = await reopened.StartNamespaceDirectoryTransferSourceDeletionAsync(directory.TransferId, fresh, deleteRequest, started, default);
            Assert.IsNull(first.AcceptedAt);
            Assert.IsNull(first.Receipt);
            Assert.HasCount(2, await reopened.ReadNamespaceDispatchFencedRootsAsync());
        }
        await using var final = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var pending = await final.ReadNamespaceDirectoryTransferSourceDeletionAsync(directory.TransferId, default);
        Assert.IsNotNull(pending);
        Assert.AreEqual(deleteRequest.RequestFingerprint, pending.Request!.RequestFingerprint);
        Assert.IsNull(pending.Receipt);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => final.AcceptNamespaceDirectoryTransferSourceDeletionAsync(directory.TransferId,
            originalReceipt with { DeletedOriginals = originalReceipt.DeletedOriginals.Reverse().ToArray() }, started.AddSeconds(1), default));
        var accepted = await final.AcceptNamespaceDirectoryTransferSourceDeletionAsync(directory.TransferId, originalReceipt, started.AddSeconds(1), default);
        Assert.AreEqual(originalReceipt.Fingerprint(), accepted.Receipt!.Fingerprint());
        Assert.HasCount(2, await final.ReadNamespaceDispatchFencedRootsAsync());
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

    private static AdapterAtomicDirectoryDeletionRequest DirectoryTransferDeleteRequest(MirrorPulseDirectoryTransferAdmission original)
    {
        var request = MirrorPulseProductCatalog.CreateDirectoryTransferSourceAdmission(original)!;
        var leaf = request.Operations[0].Operation;
        var root = request.Operations[1].Operation;
        var parent = new AdapterDirectoryGenerationWitness(1, request.InstanceId, request.AdmissionId, Guid.NewGuid(),
            new(root.RootKey, ""), "remote-parent-generation", "remote-parent-revision", null, request.RequestFingerprint, "AQ==");
        var directory = new AdapterDirectoryGenerationWitness(1, request.InstanceId, request.AdmissionId, Guid.NewGuid(),
            new(root.RootKey, root.Path), "remote-directory-generation", root.Preconditions!.ExpectedRevision!,
            parent.Generation, request.RequestFingerprint, "AQ==");
        var admission = new AdapterDirectoryAdmission(1, request.InstanceId, request.AdmissionId, request.RequestFingerprint,
            [new(leaf.OperationId, "remote-leaf-generation", null, [directory]), new(root.OperationId, directory.Generation, directory, [parent])], "");
        return MirrorPulseProductCatalog.CreateDirectoryTransferSourceDeleteRequest(request,
            admission with { AdmissionFingerprint = admission.CalculateFingerprint() });
    }
}
