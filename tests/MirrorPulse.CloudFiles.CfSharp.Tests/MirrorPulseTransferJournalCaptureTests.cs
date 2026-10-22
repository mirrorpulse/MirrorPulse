using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseTransferJournalCaptureTests
{
    private static readonly Guid SourceItem = Guid.Parse("77000000-0000-4000-8000-000000000001");
    private const string SourcePath = "Docs/source.bin";

    [TestMethod]
    [DataRow(CloudLocalChangeKind.Create, "Docs/source.bin")]
    [DataRow(CloudLocalChangeKind.Create, @"Docs\source.bin")]
    [DataRow(CloudLocalChangeKind.ContentUpdate, "Docs/source.bin")]
    [DataRow(CloudLocalChangeKind.ContentUpdate, @"Docs\source.bin")]
    public void OriginalContentObservationRetainsEveryPublicFieldAndRawWindowsPath(CloudLocalChangeKind kind, string path)
    {
        var observation = Observation(kind) with { RelativePath = path };
        var member = MirrorPulseTransferJournalCapture.SelectMember(SourceItem, SourcePath, observation);
        Assert.IsNotNull(member);
        Assert.AreEqual(observation.OperationId, member.OperationId); Assert.AreEqual(observation.Sequence, member.Sequence);
        Assert.AreEqual(observation.ItemId, member.ItemId); Assert.AreEqual(path, member.RelativePath);
        Assert.AreEqual(observation.PreviousRelativePath, member.PreviousRelativePath);
        Assert.AreEqual(observation.IsDirectory, member.IsDirectory); Assert.AreEqual(observation.ObservedAt, member.ObservedAt);
        Assert.AreEqual(kind == CloudLocalChangeKind.Create ? MirrorPulseWorkerChangeKind.Create : MirrorPulseWorkerChangeKind.ContentUpdate, member.Kind);
    }

    [TestMethod]
    [DataRow("missing-item")]
    [DataRow("foreign-item")]
    [DataRow("empty-item")]
    [DataRow("other-path")]
    [DataRow("case")]
    [DataRow("move")]
    [DataRow("delete")]
    [DataRow("directory")]
    [DataRow("previous-path")]
    [DataRow("unknown-kind")]
    [DataRow("missing-path")]
    public void AffectedUnknownOrNamespaceWorkCannotBecomeAClaimForCurrentSourceBytes(string change)
    {
        var original = Observation(CloudLocalChangeKind.ContentUpdate);
        var observed = change switch
        {
            "missing-item" => original with { ItemId = null },
            "foreign-item" => original with { ItemId = Guid.NewGuid() },
            "empty-item" => original with { ItemId = Guid.Empty },
            "other-path" => original with { RelativePath = "Docs/other.bin" },
            "case" => original with { RelativePath = "Docs/SOURCE.bin" },
            "move" => original with { Kind = CloudLocalChangeKind.Move, RelativePath = "Archive/moved.bin", PreviousRelativePath = SourcePath },
            "delete" => original with { Kind = CloudLocalChangeKind.Delete },
            "directory" => original with { IsDirectory = true },
            "previous-path" => original with { PreviousRelativePath = SourcePath },
            "unknown-kind" => original with { Kind = (CloudLocalChangeKind)999 },
            _ => original with { RelativePath = null! }
        };
        Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseTransferJournalCapture.SelectMember(SourceItem, SourcePath, observed));
        Assert.AreEqual(original.OperationId, observed.OperationId); Assert.AreEqual(original.Sequence, observed.Sequence);
    }

    [TestMethod]
    [DataRow(CloudLocalChangeKind.Create)]
    [DataRow(CloudLocalChangeKind.ContentUpdate)]
    [DataRow(CloudLocalChangeKind.Move)]
    [DataRow(CloudLocalChangeKind.Delete)]
    public void IndependentOriginalWorkRemainsOutsideThisSourcesContentClaim(CloudLocalChangeKind kind)
    {
        var observed = Observation(kind) with
        {
            ItemId = Guid.NewGuid(),
            RelativePath = "Other/root.bin",
            PreviousRelativePath = kind == CloudLocalChangeKind.Move ? "Other/previous.bin" : null
        };
        Assert.IsNull(MirrorPulseTransferJournalCapture.SelectMember(SourceItem, SourcePath, observed));
        Assert.AreNotEqual(SourceItem, observed.ItemId); Assert.AreEqual("Other/root.bin", observed.RelativePath);
    }

    [TestMethod]
    [DataRow("current")]
    [DataRow("foreign")]
    [DataRow("missing")]
    public void MetadataRemainsPendingForItsSeparateContentPolicyWithoutBorrowingTransferAuthority(string association)
    {
        var observed = Observation(CloudLocalChangeKind.MetadataUpdate) with
        { ItemId = association == "current" ? SourceItem : association == "foreign" ? Guid.NewGuid() : null };
        Assert.IsNull(MirrorPulseTransferJournalCapture.SelectMember(SourceItem, SourcePath, observed));
        Assert.AreEqual(CloudLocalChangeKind.MetadataUpdate, observed.Kind); Assert.AreEqual(SourcePath, observed.RelativePath);
    }

    private static MirrorPulseLocalChangeObservation Observation(CloudLocalChangeKind kind) => new(Guid.NewGuid(), 17,
        kind, SourceItem, SourcePath, null, false, DateTimeOffset.UtcNow);
}
