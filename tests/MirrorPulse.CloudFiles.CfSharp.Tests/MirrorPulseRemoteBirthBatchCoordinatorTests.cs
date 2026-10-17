using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRemoteBirthBatchCoordinatorTests
{
    [TestMethod]
    [DataRow("changed-batch")]
    [DataRow("changed-order")]
    [DataRow("changed-length")]
    [DataRow("changed-root-path")]
    [DataRow("changed-preservation")]
    [DataRow("changed-echo")]
    public void OriginalRemoteBirthCannotBeRetargetedByAnAlteredBatchOrTransientPolicy(string altered)
    {
        var instance = InstanceId.New(); var item = Guid.NewGuid();
        var original = Batch(instance, item); var options = CloudRemoteApplyOptions.Default;
        var birth = Birth(instance, original, options);
        var incoming = original;
        if (altered == "changed-batch") incoming = Batch(instance, item, id: "other");
        if (altered == "changed-order") incoming = new(original.BatchId, original.InitialCursor,
            [original.Changes[0], new("later", CloudRemoteChangeKind.Delete, "other", "v1", CloudItemKind.File, "Docs\\other", Guid.NewGuid())], original.FinalCursor);
        if (altered == "changed-length") incoming = Batch(instance, item, length: 8);
        if (altered == "changed-root-path") incoming = Batch(instance, item, path: "Other\\file");
        if (altered == "changed-preservation") options = options with { PreserveUnsynchronizedLocalContent = !options.PreserveUnsynchronizedLocalContent };
        if (altered == "changed-echo") options = options with { SuppressLocalEcho = !options.SuppressLocalEcho };
        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseRemoteBirthBatchCoordinator.RequireOriginal(birth, instance, incoming, 0, options));
    }

    [TestMethod]
    public void EntryBudgetMayChangeWithoutRewritingTheOriginalBirthPolicy()
    {
        var instance = InstanceId.New(); var batch = Batch(instance, Guid.NewGuid()); var options = CloudRemoteApplyOptions.Default;
        var birth = Birth(instance, batch, options);
        MirrorPulseRemoteBirthBatchCoordinator.RequireOriginal(birth, instance, batch, 0, options with { MaximumEntries = 1 });
    }

    [TestMethod]
    [DataRow("foreign-instance")]
    [DataRow("negative-ordinal")]
    [DataRow("past-end")]
    [DataRow("replacement")]
    [DataRow("missing-item")]
    [DataRow("delete")]
    [DataRow("budget")]
    public void IncompatibleInputIsRejectedBeforeSdkOrCatalogAdmission(string invalid)
    {
        var instance = InstanceId.New(); var batch = Batch(instance, Guid.NewGuid()); var options = CloudRemoteApplyOptions.Default;
        int ordinal = 0;
        if (invalid == "foreign-instance") instance = InstanceId.New();
        if (invalid == "negative-ordinal") ordinal = -1;
        if (invalid == "past-end") ordinal = 1;
        if (invalid == "replacement") batch = new(batch.BatchId, ReadOnlyMemory<byte>.Empty, [new("birth", CloudRemoteChangeKind.FileUpsert,
            "remote", "v2", CloudItemKind.File, "Docs\\file", Guid.NewGuid(), previousRemoteRevision: "v1", length: 4,
            metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build())], new byte[] { 1 });
        if (invalid == "missing-item") batch = Batch(instance, null);
        if (invalid == "delete") batch = new(batch.BatchId, ReadOnlyMemory<byte>.Empty, [new("delete", CloudRemoteChangeKind.Delete,
            "remote", "v1", CloudItemKind.File, "Docs\\file", Guid.NewGuid())], new byte[] { 1 });
        // A bounded original policy must be retained even when there is no transient resolver.
        if (invalid == "budget") options = options with { MaximumEntries = 0 };
        if (invalid is "foreign-instance" or "negative-ordinal" or "past-end")
            Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseRemoteBirthBatchCoordinator.RequireChange(instance, batch, ordinal, options));
        else Assert.ThrowsExactly<NotSupportedException>(() => MirrorPulseRemoteBirthBatchCoordinator.RequireChange(instance, batch, ordinal, options));
    }

    [TestMethod]
    [DataRow(0, 9)]
    [DataRow(1, 1)]
    [DataRow(2, 1)]
    [DataRow(3, 8)]
    public void PrefixCursorRetainsTheLastActualOpaqueCursorWhenAnIntermediateEntryHasNone(int prefix, int expected)
    {
        var instance = InstanceId.New(); var entry = Batch(instance, Guid.NewGuid()).Changes[0];
        var batch = new CloudRemoteChangeBatch(instance + "/sparse", new byte[] { 9 },
            [new("first", entry.Kind, entry.RemoteId, entry.RemoteRevision, entry.ItemKind, entry.RelativePath, entry.ItemId,
                length: entry.Length, metadata: entry.Metadata, cursorAfter: new byte[] { 1 }),
             new("second", CloudRemoteChangeKind.Delete, "other", "v1", CloudItemKind.File, "Docs\\other", Guid.NewGuid()),
             new("third", CloudRemoteChangeKind.Delete, "third", "v1", CloudItemKind.File, "Docs\\third", Guid.NewGuid())],
            new byte[] { 8 });
        CollectionAssert.AreEqual(new byte[] { (byte)expected }, MirrorPulseRemoteBirthBatchCoordinator.PrefixCursor(batch, prefix).ToArray());
    }

    private static CloudRemoteChangeBatch Batch(InstanceId instance, Guid? item, string id = "original", string path = "Docs\\file", long length = 4) =>
        new(instance + "/" + id, ReadOnlyMemory<byte>.Empty,
            [new("birth", CloudRemoteChangeKind.FileUpsert, "remote", "v1", CloudItemKind.File, path, item,
                length: length, metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build())], new byte[] { 1 });

    private static MirrorPulseNamespaceBirthIntent Birth(InstanceId instance, CloudRemoteChangeBatch batch, CloudRemoteApplyOptions policy) =>
        new(3, Guid.NewGuid(), RootId.New(), Guid.NewGuid(), Guid.NewGuid(), new(123, Guid.NewGuid(), Guid.NewGuid()),
            "Docs/file", false, MirrorPulseNamespaceBirthOrigin.RemotePopulation, "S-1-5-5-123-456", DateTimeOffset.UtcNow)
        {
            RemoteEntry = new(1, instance, batch.BatchId, Convert.ToHexString(batch.Fingerprint.Span), batch.Changes.Count, 0, batch.Changes[0].ChangeId,
                batch.Changes[0].ItemId!.Value, "remote", "v1", Guid.NewGuid(), null, "0.1.0-preview.6",
                "3caa382e341d260543d3afc05e25fa034fb32e16", new(policy.PreserveUnsynchronizedLocalContent, policy.SuppressLocalEcho,
                    policy.EchoSuppressionLifetime.Ticks, policy.EchoSuppressionObservationCount))
        };
}
