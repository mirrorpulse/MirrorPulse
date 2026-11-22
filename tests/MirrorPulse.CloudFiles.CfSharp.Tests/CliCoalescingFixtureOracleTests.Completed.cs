using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class PendingOracleBoundaryTests
{
    [TestMethod]
    public async Task IndependentCompletedOracleBindsTheActualArchiveRetryIdentityReceiptAndEveryOriginal()
    {
        await using var fixture = await Fixture.CreateAsync();
        Guid commandId = await fixture.CompleteAsync();
        var first = await RunAsync(fixture.Root, completed: true);
        Assert.AreEqual(0, first.ExitCode, first.Error);
        var audit = JsonSerializer.Deserialize<CliCoalescingFixtureAudit>(first.Output, JsonOptions)!;
        Assert.AreEqual(commandId, audit.CommandId); Assert.AreEqual(fixture.PlanId, audit.PlanId);
        Assert.AreEqual(2, audit.AcknowledgedOriginalCount); Assert.AreEqual(0, audit.PendingOriginalCount);
        Assert.IsTrue(audit.ActualNativeConfirmation); Assert.IsNotNull(audit.AuthorizedEffectId);
        Assert.AreNotEqual(fixture.EffectId, audit.AuthorizedEffectId.Value);
        Assert.IsFalse(fixture.OriginalIds.Contains(audit.AuthorizedEffectId.Value));
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(new byte[] { 3, 5, 7 })), audit.LocalSha256);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(Fixture.SelectedRemote)), audit.PreservedRemoteSha256);
        Assert.IsFalse(first.Output.Contains("PRIVATE_", StringComparison.Ordinal));
        var second = await RunAsync(fixture.Root, completed: true);
        Assert.AreEqual(0, second.ExitCode, second.Error); Assert.AreEqual(first.Output, second.Output);
        // This SQL/provider fixture supplies native receipt premises. It does not
        // execute Cloud Files or establish the actual signed Host acceptance.
    }

    [TestMethod]
    [DataRow("changed-copy")]
    [DataRow("original-still-pending")]
    [DataRow("command-still-pending")]
    [DataRow("missing-confirmation")]
    [DataRow("incomplete-confirmation")]
    [DataRow("short-confirmation")]
    [DataRow("missing-native-identity")]
    [DataRow("foreign-native-identity")]
    public async Task IndependentCompletedOracleRefusesUnprovenCompletionEvenWithAnAcknowledgedEffect(string change)
    {
        await using var fixture = await Fixture.CreateAsync();
        await fixture.CompleteAsync(change);
        var result = await RunAsync(fixture.Root, completed: true);
        Assert.AreNotEqual(0, result.ExitCode); Assert.AreEqual(string.Empty, result.Output.Trim());
        Assert.IsFalse(result.Error.Contains("PRIVATE_", StringComparison.Ordinal));
        Assert.IsFalse(result.Error.Contains(fixture.Root, StringComparison.Ordinal));
    }

    private sealed partial class Fixture
    {
        internal static readonly byte[] SelectedRemote = [11, 13, 17];

        internal async Task<Guid> CompleteAsync(string? change = null)
        {
            await using var catalog = await MirrorPulseProductCatalog.OpenAsync(Paths);
            await using var store = await MirrorPulseCfSharpStateStoreFactory.Create(Paths).OpenAsync(new(Paths.SyncRootPath));
            var plan = await catalog.ReadJournalCoalescingPlanAsync(PlanId);
            var selected = (await catalog.ReadUploadConflictsAsync())[0];
            Guid command = Guid.NewGuid();
            var source = new MirrorPulseConflictRemoteSource(1, plan.InstanceId.ToString(), plan.RootKey, plan.FinalPath,
                "PRIVATE_REMOTE_ID", selected.RemoteRevision!, SelectedRemote.Length);
            await catalog.SaveUserCommandAsync(new(command, "keepboth", selected.ConflictId.ToString("D"), "pending"));
            await catalog.PrepareConflictKeepBothAsync(command, selected.ConflictId, plan.ItemId, selected.RelativePath, source, null);
            var copies = new MirrorPulseConflictCopyStore(Paths);
            string copyPath = await copies.PreserveRemoteDecisionAsync(command, selected, source,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(SelectedRemote, writable: false)));
            await using (var copy = await copies.OpenRemoteDecisionAsync(command, selected, source))
                await catalog.RecordConflictKeepBothCopyAsync(command, copy.Proof);
            await catalog.AuthorizeJournalCoalescingRetryAsync(command, selected.ConflictId, true);
            await catalog.RecordConflictKeepBothOriginalRetriesPreparedAsync(command);
            var retry = await catalog.PrepareJournalCoalescingMutationAsync(PlanId, 0);
            Guid accepted = retry.Intent.OperationId;
            await catalog.TransitionMutationAsync(accepted, MirrorPulseMutationState.Prepared, MirrorPulseMutationState.Executing);
            const string revision = "PRIVATE_ACCEPTED_REVISION";
            await catalog.TransitionMutationAsync(accepted, MirrorPulseMutationState.Executing, MirrorPulseMutationState.RemoteAccepted, revision);
            var proof = new MirrorPulseContentAcceptanceProof(accepted, retry.Intent.UploadBinding!, plan.ItemId, source.RemoteId, revision,
                retry.Intent.ContentLength!.Value, retry.Intent.ContentSha256!);
            await catalog.SaveContentAcceptanceProofAsync(proof);
            if (change != "missing-confirmation")
            {
                string? identity = change == "missing-native-identity" ? null : Convert.ToBase64String(new CloudPlaceholderIdentity(
                    change == "foreign-native-identity" ? Guid.NewGuid() : proof.AcceptedItemId, proof.RemoteId, revision).Encode());
                await catalog.SaveContentConfirmationReceiptAsync(accepted, new(MirrorPulseContentConfirmationOutcome.Confirmed,
                    change == "incomplete-confirmation" ? MirrorPulseContentConfirmationStage.Mark : MirrorPulseContentConfirmationStage.Complete,
                    MirrorPulseContentConfirmationStage.Complete, true, true, true, true, change == "short-confirmation" ? 0 : proof.Length,
                    1, null, null, null, null, true, identity, DateTimeOffset.UtcNow));
            }
            await catalog.TransitionMutationAsync(accepted, MirrorPulseMutationState.RemoteAccepted, MirrorPulseMutationState.Acknowledged, revision);
            if (change != "original-still-pending")
            {
                await using var transaction = await store.BeginTransactionAsync();
                foreach (Guid id in OriginalIds) await transaction.Operations.RemoveAsync(id);
                await transaction.CommitAsync();
            }
            if (change != "command-still-pending") await catalog.CompleteJournalCoalescingRetryCommandsAsync(PlanId);
            if (change == "changed-copy") await File.WriteAllBytesAsync(copyPath, new byte[] { 19, 23, 29 });
            return command;
        }
    }
}
