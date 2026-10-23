using System.Runtime.Versioning;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.16299")]
[TestClass]
public sealed class MirrorPulseRetiredTransferRootCursorTests
{
    [TestMethod]
    public void RecoveryInspectsOnlyItsBudgetAndEventuallyVisitsEveryOriginalRoot()
    {
        var roots = Enumerable.Range(0, 137).Select(index => Root(index)).ToArray();
        var router = Router(roots); var cursor = new MirrorPulseRetiredTransferRootCursor();
        var visited = new HashSet<RootId>();
        for (int turn = 0; turn < 35; turn++)
        {
            int checks = 0;
            var selected = cursor.Read(router, _ => { checks++; return true; }, CancellationToken.None);
            Assert.AreEqual(turn == 34 ? 1 : 4, checks);
            Assert.AreEqual(turn != 34, selected.ScanInProgress);
            Assert.HasCount(checks, selected.Roots);
            foreach (var root in selected.Roots) Assert.IsTrue(visited.Add(root.RootId));
        }
        Assert.HasCount(137, visited);
    }

    [TestMethod]
    public void DisabledAndOfflineRootsCannotHideTheLaterActiveRootOrCallTheirSources()
    {
        var roots = Enumerable.Range(0, 173).Select(index => Root(index,
            index == 172 ? RootRegistrationState.Active : RootRegistrationState.Disabled)).ToArray();
        var router = Router(roots); var cursor = new MirrorPulseRetiredTransferRootCursor();
        int checks = 0; var visited = new List<RootRegistration>();
        for (int turn = 0; turn < 44; turn++)
            visited.AddRange(cursor.Read(router, instance =>
            {
                checks++; Assert.AreEqual(roots[172].InstanceId, instance); return true;
            }, CancellationToken.None).Roots);
        Assert.AreEqual(1, checks); Assert.HasCount(1, visited); Assert.AreSame(roots[172], visited[0]);
        Assert.IsEmpty(new MirrorPulseRetiredTransferRootCursor().Read(Router([Root(200)]), _ => false, CancellationToken.None).Roots);
    }

    [TestMethod]
    public void PendingReplayInAnEarlierWindowKeepsRecoveryAwakeUntilACompletePassFindsNone()
    {
        var router = Router(Enumerable.Range(0, 13).Select(index => Root(index)));
        var cursor = new MirrorPulseRetiredTransferRootCursor();
        var first = cursor.Read(router, _ => true, CancellationToken.None);
        Assert.IsTrue(first.ScanInProgress); cursor.RetainPendingReplay();
        for (int turn = 0; turn < 3; turn++)
        {
            var window = cursor.Read(router, _ => true, CancellationToken.None);
            Assert.AreEqual(turn < 2, window.ScanInProgress);
            Assert.IsTrue(cursor.RecoveryInProgress, "Later empty root windows cannot erase an earlier pending replay.");
        }
        for (int turn = 0; turn < 4; turn++)
        {
            _ = cursor.Read(router, _ => true, CancellationToken.None);
            Assert.AreEqual(turn < 3, cursor.RecoveryInProgress);
        }
    }

    [TestMethod]
    public void ReplacementUsesOnlyTheCurrentSnapshotAndCannotReplayRemovedRoots()
    {
        var original = Enumerable.Range(0, 9).Select(index => Root(index)).ToArray();
        var router = Router(original); var cursor = new MirrorPulseRetiredTransferRootCursor();
        var retained = cursor.Read(router, _ => true, CancellationToken.None).Roots;
        var replacement = Root(100); router.ReplaceRegistrations([replacement]);
        var current = cursor.Read(router, _ => true, CancellationToken.None).Roots;
        Assert.HasCount(1, current); Assert.AreSame(replacement, current[0]);
        Assert.HasCount(4, retained); Assert.AreSame(original[0], retained[0]);
        router.ReplaceRegistrations([]);
        var empty = cursor.Read(router, _ => throw new AssertFailedException("Removed roots must not be checked."), CancellationToken.None);
        Assert.IsEmpty(empty.Roots); Assert.IsFalse(empty.ScanInProgress);
        router.ReplaceRegistrations([original[0]]);
        Assert.AreSame(original[0], cursor.Read(router, _ => true, CancellationToken.None).Roots[0]);
    }

    [TestMethod]
    public void CancellationBeforeSelectionCannotInspectAnySource()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var router = Router([Root(0)]); var cursor = new MirrorPulseRetiredTransferRootCursor();
        Assert.ThrowsExactly<OperationCanceledException>(() => cursor.Read(router,
            _ => throw new AssertFailedException("Cancelled recovery must not inspect a source."), stop.Token));
    }

    [TestMethod]
    public void CancellationDuringSelectionStopsBeforeAnotherSourceCheck()
    {
        using var stop = new CancellationTokenSource();
        var router = Router(Enumerable.Range(0, 10).Select(index => Root(index)));
        int checks = 0;
        Assert.ThrowsExactly<OperationCanceledException>(() => new MirrorPulseRetiredTransferRootCursor().Read(router,
            _ => { checks++; stop.Cancel(); return true; }, stop.Token));
        Assert.AreEqual(1, checks);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(65)]
    public void RecoveryBudgetMustRemainBounded(int budget) =>
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new MirrorPulseRetiredTransferRootCursor(budget));

    private static MirrorPulseRootRouter Router(IEnumerable<RootRegistration> roots) =>
        new(Path.Combine(Path.GetTempPath(), "MirrorPulse-tests", "retired-root-cursor"), roots);
    private static RootRegistration Root(int index, RootRegistrationState state = RootRegistrationState.Active) =>
        AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.cursor"), InstanceId.New(),
            new AdapterRootDefinition("local", "Root " + index, "Root " + index, false), state,
            registeredAt: DateTimeOffset.UnixEpoch, identityScope: RootIdentityScope.InstanceRoot);
}
