using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseCrossInstanceJournalTests
{
    [TestMethod]
    public void LegacyCommandsRetainTheirExactSerializedShapeWhileNewSourceRoutesChangeTheFingerprint()
    {
        var command = Command(InstanceId.New(), 1, "original.txt", Guid.NewGuid());
        var original = new
        {
            command.OperationId,
            command.Sequence,
            command.InstanceId,
            command.RootKey,
            command.Kind,
            command.RelativePath,
            command.PreviousRootKey,
            command.PreviousRelativePath,
            command.IsDirectory,
            command.ItemId,
            command.ObservedAt
        };
        Assert.AreEqual(JsonSerializer.Serialize(original), JsonSerializer.Serialize(command));
        var transfer = command with
        {
            Kind = MirrorPulseWorkerChangeKind.Move,
            PreviousRootKey = "same",
            PreviousRelativePath = "old.txt",
            PreviousInstanceId = InstanceId.New()
        };
        var serialized = JsonSerializer.SerializeToElement(transfer);
        Assert.AreEqual(transfer.PreviousInstanceId!.Value.Value,
            serialized.GetProperty(nameof(transfer.PreviousInstanceId)).GetProperty(nameof(InstanceId.Value)).GetGuid());
        Assert.AreNotEqual(JsonSerializer.Serialize(transfer), JsonSerializer.Serialize(transfer with { PreviousInstanceId = null }));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void EqualRootKeysDoNotPermitCoalescingACrossInstanceMove(bool foreign)
    {
        InstanceId instance = InstanceId.New(); Guid item = Guid.NewGuid();
        var move = Command(instance, 1, "renamed.txt", item) with
        {
            Kind = MirrorPulseWorkerChangeKind.Move,
            PreviousRootKey = "same",
            PreviousRelativePath = "original.txt",
            PreviousInstanceId = foreign ? InstanceId.New() : null
        };
        var edit = Command(instance, 2, "renamed.txt", item);
        var plan = MirrorPulseJournalCoalescingPlanner.TryPlan([move, edit], item, 2, new HashSet<Guid>());
        if (foreign) Assert.IsNull(plan); else Assert.AreEqual(MirrorPulseCoalescedEffect.MoveAndUpdateFile, plan!.Effect);
    }

    [TestMethod]
    public void AForeignMoveHoldingTheOriginalNamePreventsSourceEditsFromCoalescingAroundIt()
    {
        InstanceId source = InstanceId.New(); Guid item = Guid.NewGuid();
        var before = Command(source, 1, "original.txt", item);
        var held = Command(InstanceId.New(), 2, "renamed.txt", Guid.NewGuid()) with
        { Kind = MirrorPulseWorkerChangeKind.Move, PreviousInstanceId = source, PreviousRootKey = "same", PreviousRelativePath = "original.txt" };
        var after = Command(source, 3, "original.txt", item);
        Assert.IsNull(MirrorPulseJournalCoalescingPlanner.TryPlan([before, held, after], item, 3, new HashSet<Guid>()));
        Assert.IsNotNull(MirrorPulseJournalCoalescingPlanner.TryPlan([before, held with { PreviousInstanceId = InstanceId.New() }, after], item, 3, new HashSet<Guid>()));
    }

    private static MirrorPulseWorkerChangeCommand Command(InstanceId instance, long sequence, string path, Guid item) =>
        new(Guid.NewGuid(), sequence, instance, "same", MirrorPulseWorkerChangeKind.ContentUpdate, path, null, null, false, item, DateTimeOffset.UtcNow);
}
