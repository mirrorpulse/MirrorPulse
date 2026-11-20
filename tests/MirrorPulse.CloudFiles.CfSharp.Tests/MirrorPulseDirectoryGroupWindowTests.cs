using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseDirectoryGroupWindowTests
{
    [TestMethod]
    public void CompletedScanMayShrinkAfterUnrelatedHeadersAreAcknowledgedWhileEveryOriginalIsRetained()
    {
        var (plan, window) = CreateWindow();
        Assert.AreEqual(20L, plan.WindowUpperSequence);
        MirrorPulseNamespaceDirectoryConfirmation.RequireOriginalDirectoryWindow(plan, window[..2], 2);
    }

    [TestMethod]
    [DataRow("sibling")]
    [DataRow("other-root")]
    [DataRow("other-instance")]
    public void UnrelatedCurrentWorkRemainsIndependentOfTheOriginalDirectoryGroup(string scope)
    {
        var (plan, window) = CreateWindow();
        var other = window[2] with
        {
            Sequence = 3,
            Kind = MirrorPulseWorkerChangeKind.Create,
            RelativePath = scope == "sibling" ? "parent/sibling.bin" : "parent/moved/child.bin",
            RootKey = scope == "other-root" ? "another" : plan.RootKey,
            InstanceId = scope == "other-instance" ? InstanceId.New() : plan.InstanceId
        };
        MirrorPulseNamespaceDirectoryConfirmation.RequireOriginalDirectoryWindow(plan, [.. window[..2], other], 3);
    }

    [TestMethod]
    public void AProjectionClearedItemReferenceRetainsOriginalFingerprintWithoutGrantingObjectAuthority()
    {
        var (plan, window) = CreateWindow();
        MirrorPulseNamespaceDirectoryConfirmation.RequireOriginalDirectoryWindow(plan,
            window[..2].Select(command => command with { ItemId = null }).ToArray(), 2);
        // The separate guarded native boundary must still prove the exact saved acceptance.
    }

    [TestMethod]
    [DataRow("missing-original")]
    [DataRow("source-path")]
    [DataRow("kind")]
    [DataRow("instance")]
    [DataRow("root")]
    [DataRow("item")]
    [DataRow("timestamp")]
    [DataRow("sequence")]
    [DataRow("bound")]
    [DataRow("duplicate-original")]
    public void MissingChangedOrIncompleteCurrentWindowsCannotAuthorizeOriginalDirectorySettlement(string mismatch)
    {
        var (plan, original) = CreateWindow();
        var window = original[..2]; long bound = 2;
        switch (mismatch)
        {
            case "missing-original": window = window[1..]; break;
            case "source-path": window[0] = window[0] with { RelativePath = "another" }; break;
            case "kind": window[0] = window[0] with { Kind = MirrorPulseWorkerChangeKind.Delete }; break;
            case "instance": window[0] = window[0] with { InstanceId = InstanceId.New() }; break;
            case "root": window[0] = window[0] with { RootKey = "another" }; break;
            case "item": window[0] = window[0] with { ItemId = Guid.NewGuid() }; break;
            case "timestamp": window[0] = window[0] with { ObservedAt = window[0].ObservedAt.AddTicks(1) }; break;
            case "sequence": window[0] = window[0] with { Sequence = 10 }; break;
            case "bound": bound = 1; break;
            case "duplicate-original": window = [.. window, window[0]]; break;
            default: throw new InvalidOperationException();
        }
        Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseNamespaceDirectoryConfirmation.RequireOriginalDirectoryWindow(plan, window, bound));
    }

    [TestMethod]
    [DataRow("child")]
    [DataRow("transient-child")]
    [DataRow("same-item")]
    [DataRow("ancestor")]
    public void LaterNamesAndTransientChildChainsRemainIndependentOriginalFacts(string dependency)
    {
        var (plan, original) = CreateWindow();
        var other = original[2] with { Sequence = 3, Kind = MirrorPulseWorkerChangeKind.Create, RelativePath = "parent/moved/child.bin" };
        if (dependency == "same-item") other = other with
        {
            ItemId = plan.ItemId,
            IsDirectory = true,
            Kind = MirrorPulseWorkerChangeKind.Move,
            PreviousRootKey = plan.RootKey,
            PreviousRelativePath = plan.FinalPath,
            RelativePath = "later"
        };
        if (dependency == "ancestor") other = other with
        {
            IsDirectory = true,
            Kind = MirrorPulseWorkerChangeKind.Move,
            PreviousRootKey = plan.RootKey,
            PreviousRelativePath = "parent",
            RelativePath = "renamed-parent"
        };
        MirrorPulseWorkerChangeCommand[] window = dependency == "transient-child"
            ? [.. original[..2], other, other with { OperationId = Guid.NewGuid(), Sequence = 4, Kind = MirrorPulseWorkerChangeKind.Delete }]
            : [.. original[..2], other];
        Assert.ThrowsExactly<InvalidOperationException>(() => MirrorPulseNamespaceDirectoryConfirmation.RequireOriginalDirectoryWindow(plan, window, 4));
    }

    private static (MirrorPulseJournalCoalescingPlan Plan, MirrorPulseWorkerChangeCommand[] Window) CreateWindow()
    {
        var instance = InstanceId.New(); var item = Guid.NewGuid(); var observed = DateTimeOffset.UtcNow;
        MirrorPulseWorkerChangeCommand[] window =
        [
            new(Guid.NewGuid(), 1, instance, "docs", MirrorPulseWorkerChangeKind.Create, "parent/original", null, null, true, item, observed),
            new(Guid.NewGuid(), 2, instance, "docs", MirrorPulseWorkerChangeKind.Move, "parent/moved", "docs", "parent/original", true, item, observed.AddTicks(1)),
            new(Guid.NewGuid(), 20, instance, "docs", MirrorPulseWorkerChangeKind.MetadataUpdate, "unrelated.bin", null, null, false, Guid.NewGuid(), observed.AddTicks(2))
        ];
        var plan = MirrorPulseJournalDirectoryCoalescingPlanner.TryPlan(window, item, 20, new HashSet<Guid>());
        Assert.IsNotNull(plan); return (plan, window);
    }
}
