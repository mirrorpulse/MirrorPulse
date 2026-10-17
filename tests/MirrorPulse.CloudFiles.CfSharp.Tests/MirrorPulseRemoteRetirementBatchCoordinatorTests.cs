using CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRemoteRetirementBatchCoordinatorTests
{
    [TestMethod]
    [DataRow("batch")]
    [DataRow("identity")]
    [DataRow("revision")]
    [DataRow("previous-revision")]
    [DataRow("path")]
    [DataRow("echo")]
    public void OriginalRemoteDeleteRetainsItsCompleteBatchEntryAndPublicPolicy(string altered)
    {
        var instance = InstanceId.New(); var item = Guid.NewGuid(); var batch = Batch(instance, item);
        var options = CloudRemoteApplyOptions.Default; var original = Original(instance, batch, options);
        var incoming = altered switch
        {
            "batch" => Batch(instance, item, id: "another"),
            "identity" => Batch(instance, Guid.NewGuid()),
            "revision" => Batch(instance, item, revision: "v3"),
            "previous-revision" => Batch(instance, item, previous: "other"),
            "path" => Batch(instance, item, path: "Other\\file"),
            _ => batch
        };
        if (altered == "echo") options = options with { SuppressLocalEcho = !options.SuppressLocalEcho };
        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseRemoteRetirementBatchCoordinator.RequireOriginal(original, instance, incoming, 0, options));
    }

    [TestMethod]
    public void OriginalRemoteDeleteAllowsAChangedTurnBudgetWithoutReplacingItsDurablePolicy()
    {
        var instance = InstanceId.New(); var batch = Batch(instance, Guid.NewGuid()); var options = CloudRemoteApplyOptions.Default;
        MirrorPulseRemoteRetirementBatchCoordinator.RequireOriginal(Original(instance, batch, options), instance, batch, 0, options with { MaximumEntries = 1 });
    }

    [TestMethod]
    [DataRow("instance")]
    [DataRow("negative-ordinal")]
    [DataRow("past-end")]
    [DataRow("missing-item")]
    [DataRow("birth")]
    [DataRow("preservation")]
    [DataRow("budget")]
    [DataRow("echo-lifetime")]
    public void IncompatibleRemoteDeleteInputIsRejectedBeforeSdkOrCatalogAdmission(string invalid)
    {
        var instance = InstanceId.New(); var batch = Batch(instance, Guid.NewGuid()); var options = CloudRemoteApplyOptions.Default; int ordinal = 0;
        if (invalid == "instance") instance = InstanceId.New();
        if (invalid == "negative-ordinal") ordinal = -1;
        if (invalid == "past-end") ordinal = 1;
        if (invalid == "missing-item") batch = Batch(instance, null);
        if (invalid == "birth") batch = new(batch.BatchId, batch.InitialCursor,
            [new("delete", CloudRemoteChangeKind.FileUpsert, "remote", "v2", CloudItemKind.File, "Docs\\file", Guid.NewGuid(), length: 4,
                metadata: CloudPlaceholderMetadata.CreateFileBuilder().Build())], batch.FinalCursor);
        if (invalid == "preservation") options = options with { PreserveUnsynchronizedLocalContent = false };
        if (invalid == "budget") options = options with { MaximumEntries = 0 };
        if (invalid == "echo-lifetime") options = options with { EchoSuppressionLifetime = TimeSpan.Zero };
        if (invalid is "instance" or "negative-ordinal" or "past-end")
            Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseRemoteRetirementBatchCoordinator.RequireChange(instance, batch, ordinal, options));
        else Assert.ThrowsExactly<NotSupportedException>(() => MirrorPulseRemoteRetirementBatchCoordinator.RequireChange(instance, batch, ordinal, options));
    }

    private static CloudRemoteChangeBatch Batch(InstanceId instance, Guid? item, string id = "original", string path = "Docs\\file",
        string revision = "v2", string previous = "v1") => new(instance + "/" + id, new byte[] { 1 },
            [new("delete", CloudRemoteChangeKind.Delete, "remote", revision, CloudItemKind.File, path, item,
                previousRemoteRevision: previous, cursorAfter: new byte[] { 2 })], new byte[] { 2 });

    private static MirrorPulseNamespaceRetirementIntent Original(InstanceId instance, CloudRemoteChangeBatch batch, CloudRemoteApplyOptions options)
    {
        var change = batch.Changes[0]; var root = RootId.New(); var parentId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var parent = Snapshot(root, parentId, null, "Docs", true, Guid.NewGuid(), "parent", now);
        var child = Snapshot(root, Guid.NewGuid(), parentId, "Docs/file", false, change.ItemId!.Value, change.RemoteId, now);
        return new(2, Guid.NewGuid(), child.Context.Member.MemberId, root, child.Context.Member.LocalObject, "Docs/file", false,
            child.Context.Member.OwnerSid, child.Context.Protection.RoleSid, now)
        {
            RemoteEntry = new(1, instance, batch.BatchId, Convert.ToHexString(batch.Fingerprint.Span), batch.Changes.Count, 0, change.ChangeId,
            change.ItemId.Value, change.RemoteId, change.PreviousRemoteRevision, change.RemoteRevision, "v1", "v1", child, parent,
            "0.1.0-preview.6", "3caa382e341d260543d3afc05e25fa034fb32e16",
            new(options.PreserveUnsynchronizedLocalContent, options.SuppressLocalEcho, options.EchoSuppressionLifetime.Ticks, options.EchoSuppressionObservationCount))
        };
    }

    private static MirrorPulseNamespaceMemberProtectionSnapshot Snapshot(RootId root, Guid memberId, Guid? parent, string path,
        bool directory, Guid item, string remote, DateTimeOffset now)
    {
        var binding = new MirrorPulseLocalFileBinding(1, Guid.NewGuid(), Guid.NewGuid()); var protectionId = Guid.NewGuid();
        var member = new MirrorPulseNamespaceMemberRegistration(1, memberId, parent, MirrorPulseNamespaceMemberOrigin.CapturedOriginal,
            Guid.NewGuid(), protectionId, root, path, binding, directory, "S-1-5-21-1-2-3-1001", now);
        var context = new MirrorPulseNamespaceMemberProtectionContext(member, new(memberId, parent, path, null),
            new(memberId, protectionId, "S-1-5-5-1-2", now), new(binding, member.OwnerSid, "D:P", now), null);
        return new(context, new(memberId, item, remote, null, item, remote), "D:P", protectionId, null, null);
    }
}
