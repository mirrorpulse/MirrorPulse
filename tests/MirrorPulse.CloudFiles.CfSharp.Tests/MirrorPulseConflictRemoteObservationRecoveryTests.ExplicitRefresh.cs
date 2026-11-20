using System.Text.Json;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseConflictRemoteObservationRecoveryTests
{
    [TestMethod]
    public async Task ExplicitRefreshOwnerRetainsNewEpochsAndReplaysAnEarlierCompletedRequestWithoutSourceAccess()
    {
        await using var fixture = await Fixture.OpenAsync(); var group = await fixture.PrepareAsync();
        await fixture.Recovery().RepairAsync(default);
        var initial = (await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect))!;
        string original = JsonSerializer.Serialize(await fixture.Catalog.ReadMutationAsync(group.Effect));
        var service = RefreshOwner(fixture); Guid command = Guid.NewGuid(), target = initial.SelectedConflictIds[0];
        fixture.Stats.Result = "current-second-choice";
        MirrorPulseCoalescedConflictRemoteObservation first, second;
        using (var admission = await service.RetainAsync(target, default))
            first = (await service.ExecuteAdmittedAsync(admission, command, default))!;
        Assert.AreEqual(initial.ObservationId, first.PreviousObservationId); Assert.IsNull(first.OriginalErrorExpectedRevision);
        fixture.Stats.Result = "current-third-choice";
        using (var admission = await service.RetainAsync(first.SelectedConflictIds[0], default))
            second = (await service.ExecuteAdmittedAsync(admission, Guid.NewGuid(), default))!;
        int calls = fixture.Stats.Requests.Count; fixture.Stats.Fail = true;
        await fixture.ReopenAsync(); service = RefreshOwner(fixture, enabled: false);
        Assert.AreEqual(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await service.ReadCompletedAsync(command, target, default)));
        Assert.HasCount(calls, fixture.Stats.Requests);
        Assert.AreEqual(JsonSerializer.Serialize(second), JsonSerializer.Serialize(await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect)));
        Assert.AreEqual(original, JsonSerializer.Serialize(await fixture.Catalog.ReadMutationAsync(group.Effect)));
        CollectionAssert.AreEquivalent(second.SelectedConflictIds.ToArray(), fixture.Center.Query().Select(row => row.ConflictId).ToArray());
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.ReadCompletedAsync(command, second.SelectedConflictIds[0], default));
        Assert.IsEmpty(await fixture.Catalog.ReadPendingJournalCoalescingRetryGroupsAsync()); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("null")]
    [DataRow("empty")]
    [DataRow("failure")]
    [DataRow("canceled")]
    public async Task ExplicitRefreshOwnerKeepsAnUnobservedCommandPendingAndRetainsTheOldSelection(string boundary)
    {
        await using var fixture = await Fixture.OpenAsync(); var group = await fixture.PrepareAsync();
        await fixture.Recovery().RepairAsync(default); var previous = (await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect))!;
        Guid command = Guid.NewGuid(); var service = RefreshOwner(fixture); using var cancel = new CancellationTokenSource();
        fixture.Stats.Result = boundary == "null" ? null : boundary == "empty" ? "" : "actual-current"; fixture.Stats.Fail = boundary == "failure";
        if (boundary == "canceled") fixture.Stats.AfterStat = () => cancel.Cancel();
        using (var admission = await service.RetainAsync(previous.SelectedConflictIds[0], default))
        {
            Task<MirrorPulseCoalescedConflictRemoteObservation?> Observe() => service.ExecuteAdmittedAsync(admission, command, cancel.Token).AsTask();
            if (boundary == "failure") await Assert.ThrowsExactlyAsync<IOException>(Observe);
            else if (boundary == "canceled") await Assert.ThrowsAsync<OperationCanceledException>(Observe);
            else Assert.IsNull(await Observe());
        }
        Assert.AreEqual("pending", (await fixture.Catalog.ReadUserCommandAsync(command))!.State);
        Assert.IsNull(await fixture.Catalog.ReadCoalescedConflictRemoteRefreshAsync(command));
        Assert.AreEqual(JsonSerializer.Serialize(previous), JsonSerializer.Serialize(await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect)));
        Assert.IsEmpty(await fixture.Catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
        fixture.Stats.Result = "actual-later-success"; fixture.Stats.Fail = false; fixture.Stats.AfterStat = null;
        using var resumed = await service.RetainAsync(previous.SelectedConflictIds[0], default);
        var current = await service.ExecuteAdmittedAsync(resumed, command, default);
        Assert.IsNotNull(current); Assert.AreEqual("resolved", (await fixture.Catalog.ReadUserCommandAsync(command))!.State);
        Assert.AreEqual("actual-later-success", current.RemoteRevision); Assert.IsNull(current.OriginalErrorActualRevision);
    }

    [TestMethod]
    [DataRow("disabled-instance")]
    [DataRow("disabled-root")]
    [DataRow("closed-root")]
    [DataRow("foreign-item")]
    public async Task ExplicitRefreshOwnerCannotPrepareOrAccessSourceOutsideItsCurrentAdmittedItem(string boundary)
    {
        await using var fixture = await Fixture.OpenAsync(boundary == "disabled-root"); var group = await fixture.PrepareAsync(foreignItem: boundary == "foreign-item");
        await using var work = boundary == "closed-root" ? new MirrorPulseNamespaceWorkAdmission([fixture.Root.RootId]) : null;
        var service = RefreshOwner(fixture, boundary != "disabled-instance", work);
        if (boundary == "foreign-item") await Assert.ThrowsExactlyAsync<InvalidDataException>(() => service.RetainAsync(group.Conflicts[0], default));
        else await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.RetainAsync(group.Conflicts[0], default));
        Assert.IsEmpty(fixture.Stats.Requests); Assert.IsNull(await fixture.Catalog.ReadCoalescedConflictRemoteObservationAsync(group.Effect));
        Assert.IsEmpty(await fixture.Catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
    }

    [TestMethod]
    public async Task ExplicitRefreshOwnerRefusesAChangedEpochAfterAdmissionInsteadOfRetargetingTheRequest()
    {
        await using var fixture = await Fixture.OpenAsync(); var group = await fixture.PrepareAsync(); var service = RefreshOwner(fixture); Guid command = Guid.NewGuid();
        using var retained = await service.RetainAsync(group.Conflicts[0], default);
        await fixture.Recovery().RepairAsync(default); int calls = fixture.Stats.Requests.Count;
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => service.ExecuteAdmittedAsync(retained, command, default).AsTask());
        Assert.HasCount(calls, fixture.Stats.Requests); Assert.IsNull(await fixture.Catalog.ReadUserCommandAsync(command));
        Assert.IsEmpty(await fixture.Catalog.ReadPendingJournalCoalescingRetryGroupsAsync());
    }

    private static MirrorPulseConflictRemoteVersionRefresh RefreshOwner(Fixture fixture, bool enabled = true, MirrorPulseNamespaceWorkAdmission? work = null) =>
        new(fixture.Catalog, fixture.Router, fixture.Stats.StatAsync, _ => enabled, work,
            (observed, token) => fixture.Recovery().PublishAsync(observed, token));
}
