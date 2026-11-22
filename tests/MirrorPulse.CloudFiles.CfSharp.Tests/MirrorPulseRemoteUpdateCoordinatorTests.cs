using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseRemoteUpdateCoordinatorTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void ExistingUpdateValidationPreservesItsDeclaredNullablePredecessor(bool directory, bool predecessor)
    {
        var instance = InstanceId.New(); var change = Change(directory, predecessor);
        var batch = new CloudRemoteChangeBatch(instance + "/update", new byte[] { 1 }, [change], new byte[] { 2 });
        Assert.AreSame(change, MirrorPulseRemoteUpdateCoordinator.RequireChange(instance, batch, 0, CloudRemoteApplyOptions.Default));
        Assert.AreEqual(predecessor ? "v1" : null, change.PreviousRemoteRevision);
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("negative")]
    [DataRow("end")]
    [DataRow("delete")]
    [DataRow("missing-identity")]
    [DataRow("move-path")]
    [DataRow("no-preservation")]
    [DataRow("budget")]
    [DataRow("observations")]
    [DataRow("lifetime")]
    public void ExistingUpdateRejectsForeignOrUnsafeInputsBeforeAnyNativeAdmission(string invalid)
    {
        var instance = InstanceId.New(); var change = Change(false, true); var options = CloudRemoteApplyOptions.Default;
        if (invalid == "delete") change = new(change.ChangeId, CloudRemoteChangeKind.Delete, change.RemoteId, "v2", CloudItemKind.File, change.RelativePath, change.ItemId);
        if (invalid == "missing-identity") change = new(change.ChangeId, change.Kind, change.RemoteId, "v2", change.ItemKind,
            change.RelativePath, length: 4, metadata: change.Metadata);
        if (invalid == "move-path") change = new(change.ChangeId, CloudRemoteChangeKind.Move, change.RemoteId, "v2", change.ItemKind,
            change.RelativePath, change.ItemId, previousRelativePath: "Docs/old", length: 4, metadata: change.Metadata);
        if (invalid == "no-preservation") options = options with { PreserveUnsynchronizedLocalContent = false };
        if (invalid == "budget") options = options with { MaximumEntries = 0 };
        if (invalid == "observations") options = options with { EchoSuppressionObservationCount = 65 };
        if (invalid == "lifetime") options = options with { EchoSuppressionLifetime = TimeSpan.Zero };
        var batch = new CloudRemoteChangeBatch(instance + "/update", new byte[] { 1 }, [change], new byte[] { 2 });
        if (invalid is "instance" or "negative" or "end")
            Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseRemoteUpdateCoordinator.RequireChange(
                invalid == "instance" ? InstanceId.New() : instance, batch, invalid == "negative" ? -1 : invalid == "end" ? 1 : 0, options));
        else Assert.ThrowsExactly<NotSupportedException>(() => MirrorPulseRemoteUpdateCoordinator.RequireChange(instance, batch, 0, options));
    }

    [TestMethod]
    public void ExistingUpdateDigestIncludesOriginalMetadataLengthCursorAndPredecessor()
    {
        var original = Change(false, true); string digest = MirrorPulseRemoteUpdateCoordinator.ChangeFingerprint(original);
        CloudRemoteChange Alter(string? previous, long length, byte cursor, CloudPlaceholderMetadata metadata) => new(original.ChangeId, original.Kind,
            original.RemoteId, original.RemoteRevision, original.ItemKind, original.RelativePath, original.ItemId,
            previousRemoteRevision: previous, length: length, metadata: metadata, cursorAfter: new byte[] { cursor });
        foreach (var altered in new[]
        {
            Alter(null, 4, 2, original.Metadata!), Alter("v1", 5, 2, original.Metadata!), Alter("v1", 4, 3, original.Metadata!),
            Alter("v1", 4, 2, CloudPlaceholderMetadata.CreateFileBuilder().WithAttributes(FileAttributes.Hidden).Build())
        }) Assert.AreNotEqual(digest, MirrorPulseRemoteUpdateCoordinator.ChangeFingerprint(altered));
        Assert.AreEqual(digest, MirrorPulseRemoteUpdateCoordinator.ChangeFingerprint(original));
    }

    private static CloudRemoteChange Change(bool directory, bool predecessor) => new("update",
        directory ? CloudRemoteChangeKind.DirectoryUpsert : CloudRemoteChangeKind.FileUpsert, "remote", "v2",
        directory ? CloudItemKind.Directory : CloudItemKind.File, "Docs/target", Guid.NewGuid(),
        previousRemoteRevision: predecessor ? "v1" : null, length: directory ? null : 4,
        metadata: directory ? CloudPlaceholderMetadata.CreateDirectoryBuilder().Build() : CloudPlaceholderMetadata.CreateFileBuilder().Build(),
        cursorAfter: new byte[] { 2 });
}
