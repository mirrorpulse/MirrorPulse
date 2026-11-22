using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseJournalOrderIndexTests
{
    private static readonly InstanceId First = InstanceId.New(), Second = InstanceId.New();

    [TestMethod]
    [DataRow("Docs/a", "docs/A", false, false, true)]
    [DataRow("Docs", "docs/a", true, false, true)]
    [DataRow("Docs/a", "docs", false, true, true)]
    [DataRow("Docs", "Docs/a", false, false, false)]
    [DataRow("Docs/a", "Docs", false, false, false)]
    [DataRow("Docs/a", "Docs/b", true, true, false)]
    [DataRow("Docs", "Docs2/a", true, false, false)]
    [DataRow("", "Docs/a", true, false, true)]
    [DataRow("Docs/a", "", false, true, true)]
    [DataRow("", "", false, false, true)]
    [DataRow("Docs\\a/", "docs/A", false, false, true)]
    public void ExactNamesAndDirectoryBoundariesKeepTheirOriginalMeaning(string earlier, string later,
        bool earlierDirectory, bool laterDirectory, bool expected)
    {
        var first = Command(1, earlier, earlierDirectory); var next = Command(2, later, laterDirectory);
        var index = new MirrorPulseJournalOrderIndex(); index.Add(first);
        Assert.AreEqual(expected, MirrorPulseJournalOrderPolicy.DependsOn(next, first));
        Assert.AreEqual(expected, index.HasEarlierDependency(next));
    }

    [TestMethod]
    public void SameSequenceAndLaterHeldRowsCannotFenceAnEarlierCandidate()
    {
        var index = new MirrorPulseJournalOrderIndex();
        index.Add(Command(9, "Docs", true)); index.Add(Command(5, "Docs/a", false));
        Assert.IsFalse(index.HasEarlierDependency(Command(4, "Docs/a", false)));
        Assert.IsFalse(index.HasEarlierDependency(Command(5, "Docs/a", false)));
        Assert.IsTrue(index.HasEarlierDependency(Command(6, "Docs/a", false)));
        index.Add(Command(1, "Docs/a", false));
        Assert.IsTrue(index.HasEarlierDependency(Command(2, "Docs/a", false)));
    }

    [TestMethod]
    public void PreviousRootAndInstancePathsRemainDependenciesOnBothSidesOfAMove()
    {
        var move = Command(1, "new/file", false) with
        { PreviousRootKey = "archive", PreviousRelativePath = "old/file", PreviousInstanceId = Second };
        var index = new MirrorPulseJournalOrderIndex(); index.Add(move);
        Assert.IsTrue(index.HasEarlierDependency(Command(2, "new/file", false)));
        Assert.IsTrue(index.HasEarlierDependency(Command(2, "old/file", false) with { InstanceId = Second, RootKey = "archive" }));
        Assert.IsFalse(index.HasEarlierDependency(Command(2, "old/file", false) with { RootKey = "archive" }));
        Assert.IsFalse(index.HasEarlierDependency(Command(2, "old/file", false) with { InstanceId = Second, RootKey = "Archive" }));
        var back = Command(3, "elsewhere", false) with { PreviousRelativePath = "new/file" };
        Assert.IsTrue(index.HasEarlierDependency(back));
    }

    [TestMethod]
    public void AllKindsAndMixedRoutesMatchTheExistingPolicyForEveryOriginalInsertionPrefix()
    {
        var commands = new List<MirrorPulseWorkerChangeCommand>();
        string[] paths = ["", "Docs", "docs/a", "Docs\\nested\\b/", "Docs2/a", "docs//empty", "archive"];
        for (int i = 0; i < 90; i++)
        {
            var command = Command((i * 17) % 31 - 2, paths[i % paths.Length], i % 3 == 0) with
            {
                InstanceId = i % 4 == 0 ? Second : First,
                RootKey = i % 5 == 0 ? "DOCS" : "docs",
                Kind = (MirrorPulseWorkerChangeKind)(i % 5),
                PreviousRelativePath = i % 2 == 0 ? paths[(i + 3) % paths.Length] : null,
                PreviousRootKey = i % 4 == 0 ? "other" : null,
                PreviousInstanceId = i % 7 == 0 ? Second : null
            };
            commands.Add(command);
        }
        var index = new MirrorPulseJournalOrderIndex(); var held = new List<MirrorPulseWorkerChangeCommand>();
        foreach (var addition in commands)
        {
            held.Add(addition); index.Add(addition);
            foreach (var candidate in commands)
                Assert.AreEqual(held.Any(earlier => earlier.Sequence < candidate.Sequence &&
                    MirrorPulseJournalOrderPolicy.DependsOn(candidate, earlier)), index.HasEarlierDependency(candidate),
                    $"Prefix {held.Count}, original sequence {candidate.Sequence}, path {candidate.RelativePath}");
        }
    }

    [TestMethod]
    public void EmptyAndReplayedHeldSetsPreserveOriginalCommandsAndIds()
    {
        var original = Command(long.MinValue, "Docs/a", false);
        var index = new MirrorPulseJournalOrderIndex();
        Assert.IsFalse(index.HasEarlierDependency(original));
        index.Add(original); index.Add(original);
        Assert.IsFalse(index.HasEarlierDependency(original));
        Assert.IsTrue(index.HasEarlierDependency(original with { Sequence = long.MaxValue }));
        Assert.AreEqual(long.MinValue, original.Sequence);
        Assert.AreNotEqual(Guid.Empty, original.OperationId);
        Assert.ThrowsExactly<ArgumentNullException>(() => index.Add(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => index.HasEarlierDependency(null!));
    }

    private static MirrorPulseWorkerChangeCommand Command(long sequence, string path, bool directory) =>
        new(Guid.NewGuid(), sequence, First, "docs", MirrorPulseWorkerChangeKind.Move, path,
            null, null, directory, Guid.NewGuid(), DateTimeOffset.UnixEpoch);
}
