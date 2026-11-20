using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseRootRenameHistoryIntegrityTests
{
    [TestMethod]
    public async Task AtomicPreparationRetainsOriginalProofAndReceiptAcrossRetryAndRestart()
    {
        await using var fixture = await Fixture.CreateAsync();
        await CancelSeedPreparationAsync(fixture);
        var prepared = await fixture.Catalog.PrepareManagedRootRenameWithProofAsync(fixture.Intent.RootId,
            "Atomic target", fixture.Intent.SourceName, fixture.Proof);
        Assert.AreEqual(MirrorPulseRootRenamePhase.Prepared, prepared.Intent.Phase);
        Assert.AreEqual(fixture.Proof, prepared.Proof);
        Assert.IsNull(prepared.Observation);
        string original = PreparationRows(OriginalConnection(fixture.Catalog));
        var replacement = fixture.Proof with { LocalObject = fixture.Proof.LocalObject with { LocalFileId = Guid.NewGuid() } };
        Assert.AreEqual(prepared, await fixture.Catalog.PrepareManagedRootRenameWithProofAsync(fixture.Intent.RootId,
            "Atomic target", fixture.Intent.SourceName, replacement));
        Assert.AreEqual(original, PreparationRows(OriginalConnection(fixture.Catalog)));
        await fixture.ReopenAsync();
        Assert.AreEqual(prepared, await fixture.Catalog.ReadManagedRootRenameHistoryAsync(prepared.Intent.OperationId));
        Assert.AreEqual(prepared, await fixture.Catalog.PrepareManagedRootRenameWithProofAsync(fixture.Intent.RootId,
            "Atomic target", fixture.Intent.SourceName, replacement));
        Assert.AreEqual(original, PreparationRows(OriginalConnection(fixture.Catalog)));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AtomicPreparationCannotUpgradeAnUnwitnessedLegacyIntent(bool bindingOnly)
    {
        await using var fixture = await Fixture.CreateAsync();
        await CancelSeedPreparationAsync(fixture);
        var legacy = await fixture.Catalog.PrepareManagedRootRenameAsync(fixture.Intent.RootId, "Legacy target");
        if (bindingOnly) await fixture.Catalog.SaveManagedRootRenameProofAsync(legacy.OperationId, fixture.Proof with { DirectoryMoveEvidence = null });
        string original = PreparationRows(OriginalConnection(fixture.Catalog));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Catalog.PrepareManagedRootRenameWithProofAsync(
            fixture.Intent.RootId, legacy.TargetName, fixture.Intent.SourceName, fixture.Proof));
        Assert.AreEqual(original, PreparationRows(OriginalConnection(fixture.Catalog)));
        await fixture.ReopenAsync();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => fixture.Catalog.PrepareManagedRootRenameWithProofAsync(
            fixture.Intent.RootId, legacy.TargetName, fixture.Intent.SourceName, fixture.Proof));
        Assert.AreEqual(original, PreparationRows(OriginalConnection(fixture.Catalog)));
        Assert.IsTrue((await fixture.Catalog.ReadManagedRootRenamesAsync()).Single().IsPending);
    }

    [TestMethod]
    [DataRow("intent")]
    [DataRow("proof")]
    public async Task CancellationAtActualIntentOrProofWriteRollsBackTheCompletePreparation(string checkpoint)
    {
        await using var fixture = await Fixture.CreateAsync();
        await CancelSeedPreparationAsync(fixture);
        SqliteConnection connection = OriginalConnection(fixture.Catalog);
        string original = PreparationRows(connection);
        using var canceled = new CancellationTokenSource();
        bool reached = false;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (!reached && sql.TrimStart().StartsWith(checkpoint == "intent" ? "INSERT INTO managed_root_renames" :
                "UPDATE managed_root_rename_history SET proof=", StringComparison.Ordinal))
            {
                reached = true;
                canceled.Cancel();
            }
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        try
        {
            await Assert.ThrowsAsync<OperationCanceledException>(() => fixture.Catalog.PrepareManagedRootRenameWithProofAsync(
                fixture.Intent.RootId, "Canceled target", fixture.Intent.SourceName, fixture.Proof, canceled.Token));
        }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
        Assert.IsTrue(reached, "The original transaction must actually reach its selected write boundary.");
        Assert.AreEqual(original, PreparationRows(connection));
        await fixture.ReopenAsync();
        Assert.AreEqual(original, PreparationRows(OriginalConnection(fixture.Catalog)));
        Assert.IsFalse((await fixture.Catalog.ReadManagedRootRenamesAsync()).Any(intent => intent.IsPending));
    }

    [TestMethod]
    [DataRow("source")]
    [DataRow("reserved")]
    [DataRow("missing-evidence")]
    public async Task RejectedPreparationPublishesNeitherIntentNorProof(string reason)
    {
        await using var fixture = await Fixture.CreateAsync();
        await CancelSeedPreparationAsync(fixture);
        SqliteConnection connection = OriginalConnection(fixture.Catalog);
        if (reason == "reserved")
        {
            await using SqliteCommand reserve = connection.CreateCommand();
            reserve.CommandText = "INSERT INTO managed_root_names(directory_name,root_id) VALUES('Rejected target',$owner);";
            reserve.Parameters.AddWithValue("$owner", RootId.New().ToString());
            await reserve.ExecuteNonQueryAsync();
        }
        string original = PreparationRows(connection);
        Func<Task> prepare = () => fixture.Catalog.PrepareManagedRootRenameWithProofAsync(fixture.Intent.RootId,
            "Rejected target", reason == "source" ? "Changed source" : fixture.Intent.SourceName,
            reason == "missing-evidence" ? fixture.Proof with { DirectoryMoveEvidence = null } : fixture.Proof);
        if (reason == "source") await Assert.ThrowsExactlyAsync<InvalidOperationException>(prepare);
        else if (reason == "reserved") await Assert.ThrowsExactlyAsync<InvalidDataException>(prepare);
        else await Assert.ThrowsExactlyAsync<ArgumentException>(prepare);
        Assert.AreEqual(original, PreparationRows(connection));
        await fixture.ReopenAsync();
        Assert.AreEqual(original, PreparationRows(OriginalConnection(fixture.Catalog)));
    }

    [TestMethod]
    public async Task AnotherConnectionCannotObserveTheIntentBeforeItsOriginalProof()
    {
        await using var fixture = await Fixture.CreateAsync();
        await CancelSeedPreparationAsync(fixture);
        SqliteConnection connection = OriginalConnection(fixture.Catalog);
        string original = PreparationRows(connection);
        string? observed = null;
        Exception? observationFailure = null;
        SQLitePCL.strdelegate_trace trace = (_, sql) =>
        {
            if (sql.StartsWith("UPDATE managed_root_rename_history SET proof=", StringComparison.Ordinal))
            {
                try
                {
                    using var observer = new SqliteConnection(new SqliteConnectionStringBuilder
                    {
                        DataSource = connection.DataSource,
                        Mode = SqliteOpenMode.ReadOnly,
                        Cache = SqliteCacheMode.Private,
                        Pooling = false,
                    }.ToString());
                    observer.Open();
                    observed = PreparationRows(observer);
                }
                catch (Exception failure) { observationFailure = failure; }
            }
        };
        SQLitePCL.raw.sqlite3_trace(connection.Handle!, trace, null);
        MirrorPulseRootRenameHistory prepared;
        try
        {
            prepared = await fixture.Catalog.PrepareManagedRootRenameWithProofAsync(fixture.Intent.RootId,
                "Visible together", fixture.Intent.SourceName, fixture.Proof);
        }
        finally { SQLitePCL.raw.sqlite3_trace(connection.Handle!, (SQLitePCL.strdelegate_trace)null!, null); GC.KeepAlive(trace); }
        Assert.IsNull(observationFailure, observationFailure?.ToString());
        Assert.AreEqual(original, observed, "An independent connection must retain the prior committed view at the actual proof-write boundary.");
        Assert.AreEqual(fixture.Proof, prepared.Proof);
        Assert.AreEqual(prepared, await fixture.Catalog.ReadManagedRootRenameHistoryAsync(prepared.Intent.OperationId));
        Assert.HasCount(2, await fixture.Catalog.ReadManagedRootRenameHistoryAsync());
    }

    private static Task<MirrorPulseRootRenameIntent> CancelSeedPreparationAsync(Fixture fixture) =>
        fixture.Catalog.TransitionManagedRootRenameAsync(fixture.Intent.OperationId, MirrorPulseRootRenamePhase.Prepared, MirrorPulseRootRenamePhase.Cancelled);

    private static string PreparationRows(SqliteConnection connection)
    {
        using SqliteCommand read = connection.CreateCommand();
        read.CommandText = """
            SELECT json_array(
                (SELECT json_group_array(json_array(root_id,payload)) FROM (SELECT root_id,payload FROM managed_root_renames ORDER BY root_id)),
                (SELECT json_group_array(json_array(operation_id,root_id,payload,proof,observation)) FROM
                    (SELECT operation_id,root_id,payload,proof,observation FROM managed_root_rename_history ORDER BY rowid)));
            """;
        return (string)read.ExecuteScalar()!;
    }
}
