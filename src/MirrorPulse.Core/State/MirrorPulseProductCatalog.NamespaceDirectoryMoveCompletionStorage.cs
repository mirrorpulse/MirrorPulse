using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private async Task InsertNamespaceDirectoryMoveCompletionCoreAsync(MirrorPulseNamespaceDirectoryMoveCompletion completion,
        byte[] payload, SqliteTransaction transaction, CancellationToken token)
    {
        await using SqliteCommand insert = _connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO namespace_directory_move_completions(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);";
        insert.Parameters.AddWithValue("$id", completion.OperationId.ToString("D"));
        insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
        await insert.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }

    private async Task<MirrorPulseNamespaceDirectoryMoveCompletion?> ReadNamespaceDirectoryMoveCompletionCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
        => (await ReadNamespaceDirectoryMoveCompletionGraphCoreAsync(operationId, transaction, token).ConfigureAwait(false))?.Completion;

    private async Task<NamespaceDirectoryHistoryGraph?> ReadNamespaceDirectoryMoveCompletionGraphCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var reads = _namespaceHistoryReads.GetOrCreateValue(transaction);
        if (!reads.DirectoryCompletions.TryGetValue(operationId, out var retained))
        {
            MirrorPulseNamespaceDirectoryMoveCompletion completion;
            await using (SqliteCommand query = _connection.CreateCommand())
            {
                query.Transaction = transaction;
                query.CommandText = "SELECT operation_id,payload,fingerprint FROM namespace_directory_move_completions WHERE operation_id=$id;";
                query.Parameters.AddWithValue("$id", operationId.ToString("D"));
                await using SqliteDataReader reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
                if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
                completion = ReadNamespaceMovePayload<MirrorPulseNamespaceDirectoryMoveCompletion>((byte[])reader[1], (byte[])reader[2],
                    NamespaceDirectoryMoveCompletionLimit, ValidateNamespaceDirectoryMoveCompletion);
                if (completion.OperationId != operationId || reader.GetString(0) != operationId.ToString("D"))
                    throw new InvalidDataException("The complete directory receipt index is invalid.");
            }
            try
            {
                var snapshot = await ValidateNamespaceDirectoryMoveCompletionReferencesAsync(completion, transaction, token).ConfigureAwait(false);
                var root = await ReadNamespaceMemberMoveCompletionRowAsync(operationId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The entire subtree lost its atomic root completion.");
                if (root.MemberId != snapshot.Entries.Single(entry => entry.Depth == 0).MemberId || root.Completion != completion.Root)
                    throw new InvalidDataException("The entire subtree receipt and root completion disagree.");
                var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The directory receipt lost its original move intent.");
                if (intent.Version == 3)
                    await ValidateManagedRootSubtreeRenameReceiptAsync(intent, completion, transaction, token).ConfigureAwait(false);
                retained = new(completion, snapshot, snapshot.Entries.Select((entry, index) => (entry.MemberId, Index: index))
                    .ToDictionary(entry => entry.MemberId, entry => entry.Index));
                reads.DirectoryCompletions.Add(operationId, retained);
            }
            catch (Exception error) when (error is InvalidOperationException or FileNotFoundException)
            { throw new InvalidDataException("The directory completion lost its original sealed, native or public evidence.", error); }
        }
        if (!reads.DirectoryPositions.Contains(operationId))
        {
            await ValidateNamespaceDirectorySettledPositionsAsync(retained.Completion, retained.Snapshot, transaction, token).ConfigureAwait(false);
            reads.DirectoryPositions.Add(operationId);
        }
        return retained;
    }

    private async Task ValidateNamespaceDirectorySettledPositionsAsync(MirrorPulseNamespaceDirectoryMoveCompletion completion,
        MirrorPulseNamespaceDirectoryMoveSnapshot snapshot, SqliteTransaction transaction, CancellationToken token)
    {
        await using (SqliteCommand count = _connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM namespace_member_positions WHERE operation_id=$id;";
            count.Parameters.AddWithValue("$id", completion.OperationId.ToString("D"));
            if ((long)(await count.ExecuteScalarAsync(token).ConfigureAwait(false))! != snapshot.Entries.Count)
                throw new InvalidDataException("The directory operation must settle exactly its sealed members.");
        }
        for (int index = 0; index < snapshot.Entries.Count; index++)
        {
            var entry = snapshot.Entries[index];
            var position = await ReadNamespaceMemberPositionRowAsync(entry.MemberId, completion.OperationId, transaction, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The directory receipt lost a member's atomic position settlement.");
            if (position != DirectoryMovePosition(completion, entry, index))
                throw new InvalidDataException("A directory position disagrees with its exact sealed predecessor and completed member evidence.");
            if (position.PreviousPositionOperationId is { } previous)
            {
                var previousPosition = await ReadNamespaceMemberPositionRowAsync(entry.MemberId, previous, transaction, token).ConfigureAwait(false);
                if (position.LegacyAnchor ? previousPosition is not null || entry.PreviousPositionOperationId is not null ||
                    entry.PreviousMoveOperationId != previous : previousPosition is null || previousPosition.VerifiedAt >= position.VerifiedAt)
                    throw new InvalidDataException("A directory position lost its original earlier position or explicit legacy relocation anchor.");
            }
        }
    }
}
