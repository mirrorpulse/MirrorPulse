using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.16299")]
[TestClass]
public sealed class MirrorPulseAdapterRemoteBatchMapperTests
{
    [TestMethod]
    public void WorkerCannotSelectTheHostPollingCursorNamespace()
    {
        var instance = InstanceId.New();
        var batch = new AdapterRemoteChangeBatch("poll-root/worker-selected", ReadOnlyMemory<byte>.Empty, [], new byte[] { 1 });
        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseAdapterRemoteBatchMapper.Map(instance, CreateRoots(instance), batch));
    }

    [TestMethod]
    public void MapsFiveRemoteKindsIntoOneCfSharpBatchWithInstanceScopedIdentity()
    {
        var instance = InstanceId.New();
        RootRegistration[] roots = CreateRoots(instance);
        var fileMetadata = new AdapterRemoteMetadata(FileAttributes.Normal);
        var directoryMetadata = new AdapterRemoteMetadata(FileAttributes.Directory);
        var source = new AdapterRemoteChangeBatch(
            "batch-1", new byte[] { 1 },
            [
                new("create", AdapterRemoteChangeKind.FileUpsert, "docs", "file-1", "v1",
                    AdapterRemoteItemKind.File, "a.txt", Length: 10, Metadata: fileMetadata, CursorAfter: new byte[] { 2 }),
                new("update", AdapterRemoteChangeKind.FileUpsert, "docs", "file-1", "v2",
                    AdapterRemoteItemKind.File, "a.txt", PreviousRemoteRevision: "v1", Length: 11, Metadata: fileMetadata),
                new("directory", AdapterRemoteChangeKind.DirectoryUpsert, "docs", "dir-1", "v1",
                    AdapterRemoteItemKind.Directory, "sub", Metadata: directoryMetadata),
                new("metadata", AdapterRemoteChangeKind.MetadataUpdate, "docs", "file-1", "v3",
                    AdapterRemoteItemKind.File, "a.txt", Metadata: fileMetadata),
                new("move", AdapterRemoteChangeKind.Move, "photos", "file-1", "v4",
                    AdapterRemoteItemKind.File, "moved.txt", PreviousRootKey: "docs", PreviousRelativePath: "a.txt"),
                new("delete", AdapterRemoteChangeKind.Delete, "photos", "file-1", "v5",
                    AdapterRemoteItemKind.File, "moved.txt"),
            ],
            new byte[] { 3 });

        CloudRemoteChangeBatch mapped = MirrorPulseAdapterRemoteBatchMapper.Map(instance, roots, source);

        Assert.AreEqual($"{instance}/batch-1", mapped.BatchId);
        Assert.HasCount(6, mapped.Changes);
        Assert.AreEqual(CloudRemoteChangeKind.FileUpsert, mapped.Changes[0].Kind);
        Assert.AreEqual("Documents\\a.txt", mapped.Changes[0].RelativePath);
        Assert.AreEqual("v1", mapped.Changes[1].PreviousRemoteRevision);
        Assert.AreEqual(CloudRemoteChangeKind.DirectoryUpsert, mapped.Changes[2].Kind);
        Assert.AreEqual(CloudRemoteChangeKind.MetadataUpdate, mapped.Changes[3].Kind);
        Assert.AreEqual(CloudRemoteChangeKind.Move, mapped.Changes[4].Kind);
        Assert.AreEqual("Documents\\a.txt", mapped.Changes[4].PreviousRelativePath);
        Assert.AreEqual("Photos\\moved.txt", mapped.Changes[4].RelativePath);
        Assert.AreEqual(CloudRemoteChangeKind.Delete, mapped.Changes[5].Kind);
        Assert.AreEqual(
            MirrorPulsePlaceholderIdentity.Create(instance, "file-1").ToCfSharp().ItemId,
            mapped.Changes[0].ItemId);
        CollectionAssert.AreEqual(new byte[] { 2 }, mapped.Changes[0].CursorAfter.ToArray());
        CollectionAssert.AreEqual(new byte[] { 3 }, mapped.FinalCursor.ToArray());
        CollectionAssert.AreEqual(
            mapped.Fingerprint.ToArray(),
            MirrorPulseAdapterRemoteBatchMapper.Map(instance, roots, source).Fingerprint.ToArray());
    }

    [TestMethod]
    public void RejectsEscapingPathAndUnregisteredRootBeforeCfSharpMutation()
    {
        var instance = InstanceId.New();
        RootRegistration[] roots = CreateRoots(instance);
        AdapterRemoteChange valid = new(
            "one", AdapterRemoteChangeKind.FileUpsert, "docs", "file", "v1",
            AdapterRemoteItemKind.File, "a.txt", Length: 1,
            Metadata: new AdapterRemoteMetadata(FileAttributes.Normal));

        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseAdapterRemoteBatchMapper.Map(
            instance, roots, new AdapterRemoteChangeBatch("b", ReadOnlyMemory<byte>.Empty, [valid with { RelativePath = "../outside" }], ReadOnlyMemory<byte>.Empty)));
        Assert.ThrowsExactly<FileNotFoundException>(() => MirrorPulseAdapterRemoteBatchMapper.Map(
            instance, roots, new AdapterRemoteChangeBatch("b", ReadOnlyMemory<byte>.Empty, [valid with { RootKey = "missing" }], ReadOnlyMemory<byte>.Empty)));
        Assert.ThrowsExactly<ArgumentException>(() => MirrorPulseAdapterRemoteBatchMapper.Map(
            instance, roots, new AdapterRemoteChangeBatch("b", ReadOnlyMemory<byte>.Empty, [valid, valid], ReadOnlyMemory<byte>.Empty)));
    }

    private static RootRegistration[] CreateRoots(InstanceId instance) =>
        AdapterRootRegistrationMapper.MapAll(
            AdapterId.Parse("example.drive"), instance,
            [new AdapterRootDefinition("docs", "Documents", "Documents", false),
             new AdapterRootDefinition("photos", "Photos", "Photos", false)],
            RootRegistrationState.Active).ToArray();
}
