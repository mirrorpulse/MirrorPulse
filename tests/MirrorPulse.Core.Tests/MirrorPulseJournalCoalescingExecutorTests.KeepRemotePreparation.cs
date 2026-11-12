using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;
using Fixture = MirrorPulse.Core.Tests.MirrorPulseJournalCoalescingExecutionTests.Fixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseJournalCoalescingExecutorTests
{
    [TestMethod]
    [DataRow(MirrorPulseCoalescedEffect.CreateFile)]
    [DataRow(MirrorPulseCoalescedEffect.UpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveFile)]
    [DataRow(MirrorPulseCoalescedEffect.MoveAndUpdateFile)]
    [DataRow(MirrorPulseCoalescedEffect.DeleteFile)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteAbsence)]
    [DataRow(MirrorPulseCoalescedEffect.VerifyRemoteUnchanged)]
    public async Task KeepRemotePreparationRetainsBothSidesAndEveryOriginalWithoutApplicationOrAcknowledgement(MirrorPulseCoalescedEffect effect)
    {
        using var fixture = new Fixture(effect); var remote = new FileRemote(fixture); Guid command;
        MirrorPulseConflictKeepRemotePreparation prepared; string execution, rejected;
        var copies = new MirrorPulseConflictCopyStore(fixture.Paths);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, remote); command = prepared.Intent.CommandId;
            execution = JsonSerializer.Serialize(await catalog.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId));
            rejected = JsonSerializer.Serialize(await catalog.ReadMutationAsync(prepared.Intent.RejectedEffect.OperationId));
            CollectionAssert.AreEqual(fixture.Plan.Members.Select(member => member.OperationId).ToArray(), prepared.Intent.OriginalOperationIds.ToArray());
            foreach (Guid original in prepared.Intent.OriginalOperationIds) Assert.AreEqual(command, await catalog.ReadConflictKeepRemoteOwnerAsync(original));
            Assert.IsNull(prepared.LocalPreservation); Assert.IsNull(prepared.RemoteCopy);
            var remoteProof = new MirrorPulseConflictKeepBothCopy(SelectedRemoteBytes.Length, Convert.ToHexString(SHA256.HashData(SelectedRemoteBytes)));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.RecordConflictKeepRemoteRemoteCopyAsync(command, remoteProof));
            var conflict = (await catalog.ReadUploadConflictAsync(prepared.Intent.ConflictId))!;
            MirrorPulseConflictKeepBothCopy? localProof = null;
            if (prepared.Intent.LocalContent is { } content)
            {
                string localPath = await copies.PreserveDecisionAsync(command, conflict, MirrorPulseConflictPreservedSide.Local,
                    _ => ValueTask.FromResult<Stream>(new MemoryStream(FinalBytes, writable: false)));
                await using var local = File.OpenRead(localPath);
                localProof = new(local.Length, Convert.ToHexString(await SHA256.HashDataAsync(local)));
                Assert.AreEqual(content.Length, localProof.Length); Assert.AreEqual(content.Sha256, localProof.Sha256);
            }
            await catalog.RecordConflictKeepRemoteLocalPreservationAsync(command, new(localProof));
            await copies.PreserveRemoteDecisionAsync(command, conflict, prepared.Intent.RemoteSource,
                _ => ValueTask.FromResult<Stream>(new MemoryStream(SelectedRemoteBytes, writable: false)));
            await using var retained = await copies.OpenRemoteDecisionAsync(command, conflict, prepared.Intent.RemoteSource);
            await catalog.RecordConflictKeepRemoteRemoteCopyAsync(command, retained.Proof);
            Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
            Assert.IsEmpty(await catalog.ReadPendingJournalCoalescingRetryGroupsAsync()); Assert.IsEmpty(remote.Writes);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var replay = (await reopened.ReadConflictKeepRemoteAsync(command))!;
        Assert.IsNotNull(replay.LocalPreservation); Assert.IsNotNull(replay.RemoteCopy);
        Assert.AreEqual(JsonSerializer.Serialize(prepared.Intent), JsonSerializer.Serialize(replay.Intent));
        Assert.AreEqual(JsonSerializer.Serialize(replay), JsonSerializer.Serialize(await reopened.PrepareConflictKeepRemoteAsync(command,
            replay.Intent.ConflictId, replay.Intent.ItemId, replay.Intent.LocalRelativePath, replay.Intent.RemoteSource, replay.Intent.LocalContent)));
        await reopened.RecordConflictKeepRemoteLocalPreservationAsync(command, replay.LocalPreservation);
        await reopened.RecordConflictKeepRemoteRemoteCopyAsync(command, replay.RemoteCopy);
        Assert.AreEqual(execution, JsonSerializer.Serialize(await reopened.ReadJournalCoalescingExecutionAsync(fixture.Plan.PlanId)));
        Assert.AreEqual(rejected, JsonSerializer.Serialize(await reopened.ReadMutationAsync(prepared.Intent.RejectedEffect.OperationId)));
        Assert.AreEqual("pending", (await reopened.ReadUserCommandAsync(command))!.State);
        Assert.HasCount(fixture.Plan.Members.Count, await reopened.ReadUploadConflictsAsync());
        Assert.IsEmpty(remote.Writes); Assert.IsFalse(File.Exists(fixture.Paths.CfSharpStateDatabasePath));
    }

    [TestMethod]
    [DataRow("keeplocal")]
    [DataRow("keepboth")]
    [DataRow("deletelocal")]
    [DataRow("deleteremote")]
    [DataRow("refreshconflict")]
    [DataRow("keepremote")]
    public async Task PreparedKeepRemoteOwnershipRefusesEveryCompetingGroupChoice(string action)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, remote); var intent = prepared.Intent; Guid other = Guid.NewGuid();
        if (action == "refreshconflict")
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => catalog.BeginCoalescedConflictRemoteRefreshAsync(other, intent.ConflictIds[1]));
            Assert.IsNull(await catalog.ReadUserCommandAsync(other));
        }
        else
        {
            await catalog.SaveUserCommandAsync(new(other, action, intent.ConflictIds[1].ToString("D"), "pending"));
            Task Attempt() => action switch
            {
                "keeplocal" => catalog.AuthorizeJournalCoalescingRetryAsync(other, intent.ConflictIds[1], true),
                "keepboth" => catalog.PrepareConflictKeepBothAsync(other, intent.ConflictIds[1], intent.ItemId, intent.LocalRelativePath, intent.RemoteSource, null),
                "deletelocal" => catalog.PrepareConflictLocalRemovalAsync(other, intent.ConflictIds[1], intent.RemoteSource.RootKey, intent.ItemId, intent.LocalRelativePath, intent.LocalContent),
                "deleteremote" => catalog.PrepareConflictDeletionAsync(other, intent.ConflictIds[1], intent.RemoteSource.RootKey, intent.ItemId, intent.LocalRelativePath, intent.LocalContent),
                _ => catalog.PrepareConflictKeepRemoteAsync(other, intent.ConflictIds[1], intent.ItemId, intent.LocalRelativePath, intent.RemoteSource, intent.LocalContent)
            };
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(Attempt);
        }
        foreach (Guid original in intent.OriginalOperationIds) Assert.AreEqual(intent.CommandId, await catalog.ReadConflictKeepRemoteOwnerAsync(original));
        Assert.HasCount(intent.OriginalOperationIds.Count, await catalog.ReadUploadConflictsAsync());
        Assert.IsNull((await catalog.ReadConflictKeepRemoteAsync(intent.CommandId))!.LocalPreservation); Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    [DataRow("local-absence")]
    [DataRow("local-length")]
    [DataRow("local-hash")]
    [DataRow("remote-length")]
    [DataRow("remote-hash-shape")]
    [DataRow("remote-replacement")]
    public async Task KeepRemoteCopiesRefuseMissingChangedOrReplacedByteProofs(string invalid)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, remote); var content = prepared.Intent.LocalContent!;
        var local = new MirrorPulseConflictKeepBothCopy(content.Length, content.Sha256);
        if (invalid.StartsWith("local-", StringComparison.Ordinal))
        {
            MirrorPulseConflictKeepBothCopy? bad = invalid switch { "local-absence" => null, "local-length" => local with { Length = local.Length + 1 }, _ => local with { Sha256 = new string('A', 64) } };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RecordConflictKeepRemoteLocalPreservationAsync(prepared.Intent.CommandId, new(bad)));
            Assert.IsNull((await catalog.ReadConflictKeepRemoteAsync(prepared.Intent.CommandId))!.LocalPreservation);
        }
        else
        {
            await catalog.RecordConflictKeepRemoteLocalPreservationAsync(prepared.Intent.CommandId, new(local));
            var proof = new MirrorPulseConflictKeepBothCopy(SelectedRemoteBytes.Length, Convert.ToHexString(SHA256.HashData(SelectedRemoteBytes)));
            if (invalid == "remote-replacement") await catalog.RecordConflictKeepRemoteRemoteCopyAsync(prepared.Intent.CommandId, proof);
            var bad = invalid switch { "remote-length" => proof with { Length = proof.Length + 1 }, "remote-hash-shape" => proof with { Sha256 = "PRIVATE_BYTES" }, _ => proof with { Sha256 = new string('A', 64) } };
            await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.RecordConflictKeepRemoteRemoteCopyAsync(prepared.Intent.CommandId, bad));
            Assert.AreEqual(invalid == "remote-replacement" ? proof : null, (await catalog.ReadConflictKeepRemoteAsync(prepared.Intent.CommandId))!.RemoteCopy);
        }
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(prepared.Intent.CommandId))!.State);
        Assert.HasCount(prepared.Intent.OriginalOperationIds.Count, await catalog.ReadUploadConflictsAsync()); Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task KeepRemoteMembershipInsertFailureRollsBackTheWholeOriginalGroup(bool acceptCommand)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        var selected = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        Guid command = Guid.NewGuid();
        if (!acceptCommand)
            await catalog.SaveUserCommandAsync(new(command, "keepremote", selected.SelectedConflictIds[0].ToString("D"), "pending"));
        var source = KeepRemoteSource(fixture, selected);
        await using var connection = ObservationFaultConnection(fixture); await connection.OpenAsync(); await using var fault = connection.CreateCommand();
        fault.CommandText = "CREATE TRIGGER reject_keep_remote_member BEFORE INSERT ON conflict_keep_remote_members WHEN NEW.ordinal=1 BEGIN SELECT RAISE(ABORT,'actual member insert fault'); END;";
        await fault.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<SqliteException>(PrepareAsync);
        Assert.IsNull(await catalog.ReadConflictKeepRemoteAsync(command));
        Assert.AreEqual(acceptCommand ? null : "pending", (await catalog.ReadUserCommandAsync(command))?.State);
        Assert.IsEmpty(await catalog.ReadPendingConflictKeepRemotePreparationsAsync());
        foreach (var original in fixture.Plan.Members) Assert.IsNull(await catalog.ReadConflictKeepRemoteOwnerAsync(original.OperationId));
        Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
        fault.CommandText = "DROP TRIGGER reject_keep_remote_member;"; await fault.ExecuteNonQueryAsync();
        var prepared = await PrepareAsync();
        Assert.AreEqual(command, prepared.Intent.CommandId);
        Assert.AreEqual("pending", (await catalog.ReadUserCommandAsync(command))?.State);
        CollectionAssert.AreEqual(new[] { command }, (await catalog.ReadPendingConflictKeepRemotePreparationsAsync()).ToArray());
        Assert.IsEmpty(await catalog.ReadPendingConflictKeepRemotePreparationsAsync(command));
        Assert.AreEqual(JsonSerializer.Serialize(prepared), JsonSerializer.Serialize(await PrepareAsync()));

        Task<MirrorPulseConflictKeepRemotePreparation> PrepareAsync() => acceptCommand
            ? catalog.AcceptConflictKeepRemoteAsync(command, selected.SelectedConflictIds[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, source, fixture.Content)
            : catalog.PrepareConflictKeepRemoteAsync(command, selected.SelectedConflictIds[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, source, fixture.Content);
    }

    [TestMethod]
    [DataRow("changed-source")]
    [DataRow("command-id-reuse")]
    public async Task KeepRemoteAcceptanceFailureRetainsOriginalCommandAndLeavesTheGroupUnowned(string failure)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        var selected = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        Guid command = Guid.NewGuid();
        var source = KeepRemoteSource(fixture, selected);
        MirrorPulseUserCommandRecord? original = null;
        if (failure == "command-id-reuse")
        {
            original = new(command, "retry", selected.SelectedConflictIds[0].ToString("D"), "failed");
            await catalog.SaveUserCommandAsync(original);
        }
        else source = source with { RemoteRevision = "a-different-selected-revision" };

        Task<MirrorPulseConflictKeepRemotePreparation> AcceptAsync() => catalog.AcceptConflictKeepRemoteAsync(command,
            selected.SelectedConflictIds[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, source, fixture.Content);
        if (failure == "command-id-reuse") await Assert.ThrowsExactlyAsync<SqliteException>(AcceptAsync);
        else await Assert.ThrowsExactlyAsync<InvalidDataException>(AcceptAsync);

        Assert.AreEqual(original, await catalog.ReadUserCommandAsync(command));
        Assert.IsNull(await catalog.ReadConflictKeepRemoteAsync(command));
        Assert.IsEmpty(await catalog.ReadPendingConflictKeepRemotePreparationsAsync());
        foreach (var member in fixture.Plan.Members)
            Assert.IsNull(await catalog.ReadConflictKeepRemoteOwnerAsync(member.OperationId));
        Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync());
        Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    [DataRow("conflict_keep_remote_preparations")]
    [DataRow("conflict_keep_remote_members")]
    [DataRow("conflict_keep_remote_copies")]
    public async Task Schema94RefusesMissingKeepRemoteOwnershipWithoutRepairingHistory(string table)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) { }
        await using var connection = ObservationFaultConnection(fixture); await connection.OpenAsync(); await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys=OFF; DROP TABLE " + table + ";"; await command.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;"; command.Parameters.AddWithValue("$name", table);
        Assert.AreEqual(0L, await command.ExecuteScalarAsync());
        command.Parameters.Clear(); command.CommandText = "PRAGMA user_version;"; Assert.AreEqual(106L, await command.ExecuteScalarAsync());
    }

    private static MirrorPulseConflictRemoteSource KeepRemoteSource(Fixture fixture, MirrorPulseCoalescedConflictRemoteObservation selected) =>
        new(1, fixture.Plan.InstanceId.ToString(), fixture.Plan.RootKey, selected.RemoteRelativePath, "selected-remote-object", selected.RemoteRevision, SelectedRemoteBytes.Length);

    [TestMethod]
    [DataRow("item")]
    [DataRow("remote-version")]
    [DataRow("remote-id")]
    [DataRow("local-content")]
    [DataRow("path")]
    public async Task RetainedKeepRemoteCommandCannotReplaceItsSelectedObjectOrEitherSide(string changed)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, remote); var intent = prepared.Intent;
        var source = changed switch { "remote-version" => intent.RemoteSource with { RemoteRevision = "must-not-retarget" }, "remote-id" => intent.RemoteSource with { RemoteId = "another-object" }, _ => intent.RemoteSource };
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.PrepareConflictKeepRemoteAsync(intent.CommandId, intent.ConflictId,
            changed == "item" ? Guid.NewGuid() : intent.ItemId, changed == "path" ? "another-local-name" : intent.LocalRelativePath, source,
            changed == "local-content" ? intent.LocalContent! with { Sha256 = new string('A', 64) } : intent.LocalContent));
        Assert.AreEqual(JsonSerializer.Serialize(prepared), JsonSerializer.Serialize(await catalog.ReadConflictKeepRemoteAsync(intent.CommandId)));
    }

    [TestMethod]
    [DataRow("preparation")]
    [DataRow("members")]
    [DataRow("local-copy")]
    [DataRow("remote-copy")]
    public async Task KeepRemoteReadsRefuseDamagedOriginalMembershipOrCopyReceipts(string damaged)
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var prepared = await PrepareKeepRemoteForTestAsync(catalog, fixture, remote); var content = prepared.Intent.LocalContent!;
        await catalog.RecordConflictKeepRemoteLocalPreservationAsync(prepared.Intent.CommandId, new(new(content.Length, content.Sha256)));
        await catalog.RecordConflictKeepRemoteRemoteCopyAsync(prepared.Intent.CommandId, new(SelectedRemoteBytes.Length, Convert.ToHexString(SHA256.HashData(SelectedRemoteBytes))));
        await using var connection = ObservationFaultConnection(fixture); await connection.OpenAsync(); await using var fault = connection.CreateCommand();
        fault.CommandText = damaged switch
        {
            "preparation" => "UPDATE conflict_keep_remote_preparations SET fingerprint=zeroblob(32);",
            "members" => "UPDATE conflict_keep_remote_members SET ordinal=ordinal+100;",
            _ => "UPDATE conflict_keep_remote_copies SET fingerprint=zeroblob(32) WHERE side=" + (damaged == "local-copy" ? "0" : "1") + ";"
        };
        await fault.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => catalog.ReadConflictKeepRemoteAsync(prepared.Intent.CommandId));
        Assert.HasCount(fixture.Plan.Members.Count, await catalog.ReadUploadConflictsAsync()); Assert.IsEmpty(remote.Writes);
    }

    [TestMethod]
    public async Task Schema93UpgradePreservesEveryOriginalSelectedEpochAndRejectedEffect()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        MirrorPulseCoalescedConflictRemoteObservation selected; string rejected;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
            selected = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
            rejected = JsonSerializer.Serialize(await catalog.ReadMutationAsync(request.RejectedEffect.OperationId));
        }
        await using (var connection = ObservationFaultConnection(fixture))
        {
            await connection.OpenAsync(); await using var old = connection.CreateCommand();
            old.CommandText = "DROP TABLE conflict_keep_remote_copies; DROP TABLE conflict_keep_remote_members; DROP TABLE conflict_keep_remote_preparations; PRAGMA user_version=93;";
            await old.ExecuteNonQueryAsync();
        }
        await using var upgraded = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        Assert.AreEqual(JsonSerializer.Serialize(selected), JsonSerializer.Serialize(await upgraded.ReadCoalescedConflictRemoteObservationEpochAsync(selected.ObservationId)));
        Assert.AreEqual(rejected, JsonSerializer.Serialize(await upgraded.ReadMutationAsync(selected.RejectedEffect.OperationId)));
        foreach (Guid original in selected.OriginalOperationIds) Assert.IsNull(await upgraded.ReadConflictKeepRemoteOwnerAsync(original));
        Assert.HasCount(selected.OriginalOperationIds.Count, await upgraded.ReadUploadConflictsAsync());
    }

    [TestMethod]
    public async Task AClaimedOlderVersionCannotInventRetainedKeepRemoteOwnership()
    {
        using var fixture = new Fixture(MirrorPulseCoalescedEffect.UpdateFile); var remote = new FileRemote(fixture);
        Guid command;
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths)) command = (await PrepareKeepRemoteForTestAsync(catalog, fixture, remote)).Intent.CommandId;
        await using var connection = ObservationFaultConnection(fixture); await connection.OpenAsync(); await using var old = connection.CreateCommand();
        old.CommandText = "PRAGMA user_version=93;"; await old.ExecuteNonQueryAsync();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(async () => { await using var rejected = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths); });
        old.CommandText = "SELECT command_id FROM conflict_keep_remote_preparations;"; Assert.AreEqual(command.ToString("D"), await old.ExecuteScalarAsync());
        old.CommandText = "PRAGMA user_version;"; Assert.AreEqual(93L, await old.ExecuteScalarAsync());
    }

    private static async Task<MirrorPulseConflictKeepRemotePreparation> PrepareKeepRemoteForTestAsync(MirrorPulseProductCatalog catalog, Fixture fixture, FileRemote remote)
    {
        var (previous, request) = await RejectWithoutVersionMetadataAsync(catalog, fixture, remote);
        var selected = await catalog.PrepareCoalescedConflictRemoteObservationAsync(previous[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, request);
        Guid command = Guid.NewGuid(); await catalog.SaveUserCommandAsync(new(command, "keepremote", selected.SelectedConflictIds[0].ToString("D"), "pending"));
        var local = fixture.Plan.Effect is MirrorPulseCoalescedEffect.DeleteFile or MirrorPulseCoalescedEffect.VerifyRemoteAbsence ? null :
            fixture.Content ?? new MirrorPulseCoalescingContent(FinalBytes.Length, Convert.ToHexString(SHA256.HashData(FinalBytes)),
                new(new(42, Guid.NewGuid(), Guid.NewGuid()), MirrorPulseContentPreparation.ConvertRegularFile));
        return await catalog.PrepareConflictKeepRemoteAsync(command, selected.SelectedConflictIds[0], fixture.Plan.ItemId, fixture.Plan.FinalPath, KeepRemoteSource(fixture, selected), local);
    }
}
