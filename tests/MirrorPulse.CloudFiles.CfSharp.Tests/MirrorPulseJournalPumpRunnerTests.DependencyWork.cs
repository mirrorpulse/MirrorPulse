using System.Collections;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseJournalPumpRunnerTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(32)]
    [DataRow(64)]
    public async Task BoundedDispatchDoesNotRepeatedlyWalkTheOriginalReadyWindow(int roots)
    {
        InstanceId instance = InstanceId.New();
        var commands = Enumerable.Range(0, 512).Select(index => FairCommand(instance, $"root-{index / (512 / roots)}",
            index + 1, $"file-{index}.txt")).ToArray();
        var ready = new CountedReadyWindow(commands);
        var batch = new MirrorPulseJournalUploadBatch(ready, 0, false) { ValidatedSequenceCommands = ready };
        var calls = new List<Guid>();
        Assert.IsTrue(await new MirrorPulseJournalPumpRunner(64, 2).RunCycleAsync(_ => ValueTask.FromResult(batch),
            (command, _) => { calls.Add(command.OperationId); return ValueTask.FromResult(true); },
            (_, _, _, _) => throw new AssertFailedException("Independent original routes must dispatch without faults."), default));
        Assert.HasCount(64, calls); Assert.HasCount(64, calls.Distinct());
        TestContext.WriteLine($"JournalDispatchWork: roots={roots}; originals={commands.Length}; visited={ready.Visited}; dispatched={calls.Count}; nativeExecuted=False.");
        Assert.IsLessThanOrEqualTo(4 * commands.Length, ready.Visited,
            "A bounded dispatch cycle must not repeatedly enumerate the complete original prefix for each candidate.");
        CollectionAssert.AreEqual(commands, ready.ToArray());
    }

    private sealed class CountedReadyWindow(MirrorPulseWorkerChangeCommand[] commands) : IReadOnlyList<MirrorPulseWorkerChangeCommand>
    {
        internal int Visited { get; private set; }
        public int Count => commands.Length;
        public MirrorPulseWorkerChangeCommand this[int index] { get { Visited++; return commands[index]; } }
        public IEnumerator<MirrorPulseWorkerChangeCommand> GetEnumerator()
        {
            foreach (var command in commands) { Visited++; yield return command; }
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
