using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using Microsoft.Data.Sqlite;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliCoalescingFixtureAudit(int SchemaVersion, bool ExclusiveCatalogOwner, string RawEffect,
    Guid PlanId, IReadOnlyList<Guid> OriginalOperationIds, int OriginalCount, int PendingOriginalCount,
    int AcknowledgedOriginalCount, bool OriginalExpectedRevisionPresent, string OriginalEffectState,
    Guid RejectedEffectId, Guid? CommandId, Guid? AuthorizedEffectId, string? LocalSha256,
    string? PreservedRemoteSha256, bool ActualNativeConfirmation);

/// <summary>Reads actual retained coalescing facts only in an authenticated stopped synthetic Host fixture.</summary>
public static class CliCoalescingFixtureProbe
{
    private sealed record Marker(int SchemaVersion, string InstanceId, string RootId, string RootKey, string DirectoryName);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task<int> RunAsync(string directory, bool completed)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        const string prefix = "MirrorPulse-cli-integration-";
        string name = Path.GetFileName(root);
        if (!string.Equals(Path.GetDirectoryName(root), Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _)) return 2;
        RequireExistingUnredirected(root);
        string markerPath = Path.Combine(root, ".mp-cli-fixture.json"); RequireExistingUnredirected(markerPath);
        if (new FileInfo(markerPath).Length > 4096) return 2;
        var marker = JsonSerializer.Deserialize<Marker>(await File.ReadAllTextAsync(markerPath), JsonOptions)
            ?? throw new InvalidDataException("The synthetic coalescing marker is missing.");
        if (marker.SchemaVersion != 1 || !Guid.TryParse(marker.InstanceId, out Guid instanceGuid) || instanceGuid == Guid.Empty ||
            !Guid.TryParse(marker.RootId, out Guid rootGuid) || rootGuid == Guid.Empty || string.IsNullOrWhiteSpace(marker.RootKey) ||
            string.IsNullOrWhiteSpace(marker.DirectoryName) || marker.DirectoryName is "." or ".." ||
            marker.DirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return 2;
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        foreach (string path in new[] { paths.SyncRootPath, paths.DataRootPath, Path.GetDirectoryName(paths.ProductCatalogDatabasePath)!, paths.ProductCatalogDatabasePath })
            RequireExistingUnredirected(path);
        // Acquire the Host's real exclusive lease before any catalog or official-store evidence.
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        var topology = await catalog.ReadAdapterTopologyAsync();
        var route = topology.Roots.Single();
        if (topology.Instances.Count != 1 || topology.Instances[0].InstanceId.Value != instanceGuid || route.InstanceId.Value != instanceGuid ||
            route.RootId.Value != rootGuid || route.UniquenessKey != marker.RootKey || route.DirectoryName != marker.DirectoryName ||
            route.State != RootRegistrationState.Active) throw new InvalidDataException("The fixture route changed.");
        var plans = await catalog.ReadJournalCoalescingPlansAsync();
        if (plans.Count > 64) throw new InvalidDataException("The isolated fixture exceeds its plan bound.");
        var selected = plans.Where(plan => plan.InstanceId == route.InstanceId && plan.RootKey == route.UniquenessKey &&
            plan.OriginalPath == "cli-roundtrip.txt" && plan.FinalPath == "cli-roundtrip.txt").ToArray();
        if (selected.Length != 1 || selected[0].Effect != MirrorPulseCoalescedEffect.UpdateFile || selected[0].Members.Count is < 2 or > 64)
            throw new InvalidDataException("The fixture requires one actual repeated-content UpdateFile group.");
        var plan = selected[0];
        var execution = await catalog.ReadJournalCoalescingExecutionAsync(plan.PlanId)
            ?? throw new InvalidDataException("The original execution is missing.");
        if (string.IsNullOrEmpty(execution.ExpectedRevision) || execution.Content is null || execution.Steps.Count != 1 ||
            execution.Steps[0].Kind != MirrorPulseCoalescingStepKind.Upload) throw new InvalidDataException("The original upload baseline or content proof is missing.");
        Guid[] originalIds = plan.Members.Select(member => member.OperationId).ToArray();
        Guid rejectedId = execution.Steps[0].OperationId;
        var rejected = await catalog.ReadMutationAsync(rejectedId);
        if (originalIds.Contains(rejectedId) || rejected is not { State: MirrorPulseMutationState.Conflict } ||
            rejected.Intent.Origin != MirrorPulseMutationOrigin.CoalescedJournal || rejected.Intent.ExpectedRevision != execution.ExpectedRevision)
            throw new InvalidDataException("The original rejected effect must remain distinct and unchanged.");
        RequireExistingUnredirected(paths.CfSharpStateDatabasePath);
        await using var store = await MirrorPulseCfSharpStateStoreFactory.Create(paths).OpenAsync(new(paths.SyncRootPath));
        int pending = 0;
        await using (var transaction = await store.BeginTransactionAsync())
        {
            foreach (Guid id in originalIds) if (await transaction.Operations.GetAsync(id) is not null) pending++;
            await transaction.RollbackAsync();
        }
        Guid? commandId = null, acceptedId = null;
        string? localHash = null, preservedHash = null;
        if (!completed)
        {
            if (pending != originalIds.Length || (await catalog.ReadCurrentCoalescingKeepBothCommandsAsync(plan.PlanId)).Count != 0)
                throw new InvalidDataException("Every original must still be pending before the choice.");
            var conflicts = await catalog.ReadUploadConflictsAsync();
            foreach (Guid id in originalIds)
                if (!conflicts.Any(conflict => conflict.InstanceId == route.InstanceId && conflict.ChangeId == id.ToString("D") &&
                    conflict.RelativePath == route.DirectoryName + "/cli-roundtrip.txt" && conflict.Reason == MirrorPulseConflictReason.StaleRemoteRevision))
                    throw new InvalidDataException("Each pending original must retain its real conditional conflict.");
        }
        else
        {
            if (pending != 0) throw new InvalidDataException("The complete choice cannot hide an unacknowledged original.");
            var commands = await catalog.ReadCurrentCoalescingKeepBothCommandsAsync(plan.PlanId);
            if (commands.Count != 1) throw new InvalidDataException("The original program requires one retained KeepBoth authorization.");
            commandId = commands[0];
            var decision = await catalog.ReadConflictKeepBothAsync(commandId.Value)
                ?? throw new InvalidDataException("The original decision is missing.");
            var command = await catalog.ReadUserCommandAsync(commandId.Value);
            if (command is not { Action: "keepboth", State: "resolved" } || command.TargetId != decision.Intent.ConflictId.ToString("D") ||
                !decision.RetryAuthorized || !decision.OriginalRetriesPrepared || decision.Copy is null || decision.Intent.PlanId != plan.PlanId ||
                !originalIds.SequenceEqual(decision.Intent.OriginalOperationIds) || decision.Intent.RejectedEffect != rejected.Intent)
                throw new InvalidDataException("The resolved command must retain its complete original decision and copy proof.");
            var conflict = await catalog.ReadUploadConflictAsync(decision.Intent.ConflictId)
                ?? throw new InvalidDataException("The selected remote conflict disappeared.");
            await using var copy = await new MirrorPulseConflictCopyStore(paths).OpenRemoteDecisionAsync(commandId.Value, conflict, decision.Intent.RemoteSource);
            preservedHash = Convert.ToHexString(await SHA256.HashDataAsync(copy.Data));
            if (copy.Proof != decision.Copy || preservedHash != decision.Copy.Sha256) throw new InvalidDataException("The actual private bytes no longer match their immutable proof.");
            // Enumerate only MP-owned retry IDs through a read-only connection, then use public records.
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths.ProductCatalogDatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            await connection.OpenAsync(); await using var query = connection.CreateCommand();
            query.CommandText = "SELECT operation_id FROM journal_coalescing_retries WHERE plan_id=$plan AND command_id=$command ORDER BY ordinal LIMIT 3;";
            query.Parameters.AddWithValue("$plan", plan.PlanId.ToString("D")); query.Parameters.AddWithValue("$command", commandId.Value.ToString("D"));
            var ids = new List<Guid>();
            await using (var reader = await query.ExecuteReaderAsync()) while (await reader.ReadAsync()) ids.Add(Guid.Parse(reader.GetString(0)));
            if (ids.Count != 1 || ids[0] == rejectedId || originalIds.Contains(ids[0])) throw new InvalidDataException("The authorized effect must have its own immutable ID.");
            acceptedId = ids[0];
            var accepted = await catalog.ReadMutationAsync(acceptedId.Value);
            var proof = await catalog.ReadContentAcceptanceProofAsync(acceptedId.Value);
            var receipt = await catalog.ReadContentConfirmationReceiptAsync(acceptedId.Value);
            if (accepted is not { State: MirrorPulseMutationState.Acknowledged } || accepted.Intent.Origin != MirrorPulseMutationOrigin.CoalescedJournal ||
                accepted.Intent.Kind != MirrorPulseWorkerChangeKind.ContentUpdate || accepted.Intent.InstanceId != route.InstanceId || accepted.Intent.RootKey != route.UniquenessKey ||
                accepted.Intent.RelativePath != "cli-roundtrip.txt" || accepted.Intent.ExpectedRevision != decision.Intent.RemoteSource.RemoteRevision ||
                proof is null || receipt is not { MayAcknowledge: true, Stage: MirrorPulseContentConfirmationStage.Complete } ||
                receipt.BytesVerified < proof.Length || proof.UploadBinding != accepted.Intent.UploadBinding ||
                proof.AcceptedRevision != accepted.AcceptedRevision || proof.Sha256 != accepted.Intent.ContentSha256 || proof.Length != accepted.Intent.ContentLength)
                throw new InvalidDataException("The authorized effect requires actual accepted bytes and native confirmation before original acknowledgments.");
            byte[] encoded = Convert.FromBase64String(receipt.ObservedPlaceholderIdentity ?? string.Empty);
            if (encoded.Length is 0 or > 4096 || Convert.ToBase64String(encoded) != receipt.ObservedPlaceholderIdentity)
                throw new InvalidDataException("The confirmation requires its exact public native identity.");
            var observed = CloudPlaceholderIdentity.Decode(encoded);
            if (observed.ItemId != proof.AcceptedItemId || observed.RemoteId != proof.RemoteId ||
                observed.RemoteRevision != (proof.AcceptedRevision ?? string.Empty))
                throw new InvalidDataException("The confirmed native identity does not match the accepted effect.");
            localHash = proof.Sha256;
        }
        var result = new CliCoalescingFixtureAudit(1, true, plan.Effect.ToString(), plan.PlanId, originalIds, originalIds.Length,
            pending, originalIds.Length - pending, true, rejected.State.ToString(), rejectedId, commandId, acceptedId,
            localHash, preservedHash, completed);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static void RequireExistingUnredirected(string path)
    {
        if (!Path.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The stopped synthetic oracle requires existing unredirected fixture paths.");
    }
}
