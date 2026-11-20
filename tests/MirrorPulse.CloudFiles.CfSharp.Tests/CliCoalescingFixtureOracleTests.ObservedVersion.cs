using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.CfSharp.CrashProbe;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class PendingOracleBoundaryTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task IndependentProcessReadsSeparateVersionObservationWithoutRewritingOrDisclosingTheOriginalError(bool corruptHistory)
    {
        await using var fixture = await Fixture.CreateAsync("missing-version");
        MirrorPulseCoalescedConflictRemoteObservation observed; string originalEffect;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var rejected = (await catalog.ReadMutationAsync(fixture.EffectId))!;
            originalEffect = JsonSerializer.Serialize(rejected);
            var group = (await catalog.ReadJournalCoalescingOwnerAsync(fixture.OriginalIds[0]))!;
            var previous = await catalog.ReadUploadConflictsAsync();
            DateTimeOffset now = DateTimeOffset.UtcNow;
            // The fixture supplies synthetic observation inputs. The independent
            // process reads and validates the real SQLite history and pending SDK IDs.
            observed = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0].ConflictId, group.ItemId, previous[0].RelativePath,
                new(1, Guid.NewGuid(), rejected.Intent, "PRIVATE_SELECTED_AFTER_STAT", now, now));
            if (corruptHistory)
            {
                var historical = (await catalog.ReadUploadConflictAsync(observed.PreviousConflictIds[0]))!;
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = fixture.Paths.ProductCatalogDatabasePath, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
                await connection.OpenAsync(); await using var fault = connection.CreateCommand();
                fault.CommandText = "UPDATE upload_conflicts SET local_revision='PRIVATE_REWRITTEN_ERROR' WHERE conflict_id=$id AND status=2;";
                fault.Parameters.AddWithValue("$id", historical.ConflictId.ToString("D"));
                Assert.AreEqual(1, await fault.ExecuteNonQueryAsync());
            }
        }
        var result = await RunAsync(fixture.Root, completed: false);
        Assert.IsFalse(result.Output.Contains("PRIVATE_", StringComparison.Ordinal));
        Assert.IsFalse(result.Error.Contains("PRIVATE_", StringComparison.Ordinal));
        Assert.IsFalse(result.Output.Contains(fixture.Root, StringComparison.Ordinal));
        Assert.IsFalse(result.Error.Contains(fixture.Root, StringComparison.Ordinal));
        if (corruptHistory)
        {
            Assert.AreNotEqual(0, result.ExitCode); Assert.AreEqual(string.Empty, result.Output.Trim());
            return;
        }
        Assert.AreEqual(0, result.ExitCode, result.Error);
        var audit = JsonSerializer.Deserialize<CliCoalescingFixtureAudit>(result.Output, JsonOptions)!;
        var selected = audit.SelectedRemoteObservation;
        Assert.IsNotNull(selected); Assert.AreEqual(observed.ObservationId, selected.ObservationId); Assert.AreEqual(fixture.EffectId, selected.RejectedEffectId);
        Assert.IsFalse(selected.OriginalErrorExpectedRevisionPresent); Assert.IsNull(selected.OriginalErrorExpectedRevisionSha256);
        Assert.IsFalse(selected.OriginalErrorActualRevisionPresent); Assert.IsNull(selected.OriginalErrorActualRevisionSha256);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("PRIVATE_ORIGINAL_REVISION"))), selected.OriginalRequestExpectedRevisionSha256);
        Assert.AreEqual(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("PRIVATE_SELECTED_AFTER_STAT"))), selected.SelectedRemoteRevisionSha256);
        CollectionAssert.AreEqual(fixture.OriginalIds, selected.OriginalOperationIds.ToArray());
        CollectionAssert.AreEqual(observed.SelectedConflictIds.ToArray(), selected.SelectedConflictIds.ToArray());
        Assert.AreEqual(2, audit.PendingOriginalCount); Assert.AreEqual(0, audit.AcknowledgedOriginalCount);
        Assert.IsFalse(audit.ActualNativeConfirmation); Assert.IsNull(audit.CommandId); Assert.IsNull(audit.AuthorizedEffectId);
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(originalEffect, JsonSerializer.Serialize(await reopened.ReadMutationAsync(fixture.EffectId)));
        Assert.IsEmpty(await reopened.ReadPendingJournalCoalescingRetryGroupsAsync());
        foreach (Guid id in observed.PreviousConflictIds)
        {
            var historical = (await reopened.ReadUploadConflictAsync(id))!;
            Assert.IsNull(historical.LocalRevision); Assert.IsNull(historical.RemoteRevision);
        }
    }
}
