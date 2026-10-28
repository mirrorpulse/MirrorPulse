using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseJournalDispatchOrderTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public void OriginalPrefixDependenciesSurviveOutOfOrderAcceptanceWithoutAdoptingOtherOperations(int order)
    {
        InstanceId first = InstanceId.New(), second = InstanceId.New();
        string[] paths = ["", "Docs", "docs/a", "Docs\\nested\\b/", "Docs2/a", "docs//empty", "archive"];
        var commands = Enumerable.Range(0, 90).Select(index => new MirrorPulseWorkerChangeCommand(Guid.NewGuid(),
            (index * 17) % 31 - 2, index % 4 == 0 ? second : first, index % 5 == 0 ? "DOCS" : "docs",
            (MirrorPulseWorkerChangeKind)(index % 5), paths[index % paths.Length], index % 4 == 0 ? "other" : null,
            index % 2 == 0 ? paths[(index + 3) % paths.Length] : null, index % 3 == 0, Guid.NewGuid(), DateTimeOffset.UnixEpoch)
        { PreviousInstanceId = index % 7 == 0 ? second : null }).ToArray();
        commands[39] = commands[11];
        commands[57] = commands[17] with { RelativePath = "independent/retained.txt" };
        var original = commands.ToArray();
        var pending = new MirrorPulseJournalDispatchOrder(Array.AsReadOnly(commands));
        var accepted = new HashSet<Guid>();
        CheckEveryOriginal();
        IEnumerable<int> indices = Enumerable.Range(0, commands.Length);
        indices = order switch { 1 => indices.Reverse(), 2 => indices.OrderBy(index => index % 7), 3 => indices.Where(index => index % 3 == 0), _ => indices };
        foreach (int selected in indices)
        {
            accepted.Add(commands[selected].OperationId);
            pending.Accept(commands[selected].OperationId);
            pending.Accept(commands[selected].OperationId);
            pending.Accept(Guid.Empty);
            CheckEveryOriginal();
        }
        CollectionAssert.AreEqual(original, commands);

        void CheckEveryOriginal()
        {
            foreach (var candidate in commands)
                Assert.AreEqual(commands.TakeWhile(earlier => earlier != candidate).Any(earlier =>
                    !accepted.Contains(earlier.OperationId) && MirrorPulseJournalOrderPolicy.DependsOn(candidate, earlier)),
                    pending.HasEarlierDependency(candidate), $"Acceptance order {order}, retained count {accepted.Count}, path {candidate.RelativePath}");
        }
    }
}
