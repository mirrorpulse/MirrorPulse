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
}
