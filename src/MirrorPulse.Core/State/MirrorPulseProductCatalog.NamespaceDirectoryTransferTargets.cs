using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    private const string DirectoryTransferTargetVerificationTable = """
        CREATE TABLE namespace_directory_transfer_target_verifications(
            transfer_id TEXT PRIMARY KEY NOT NULL REFERENCES namespace_directory_transfer_admissions(operation_id),
            payload BLOB NOT NULL CHECK(length(payload) BETWEEN 1 AND 8388608),
            fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32))
        """;

    internal async Task<MirrorPulseDirectoryTransferTargetVerification?> ReadNamespaceDirectoryTransferTargetAsync(
        Guid transferId, CancellationToken token)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = BeginNamespaceRecoveryReadSnapshot();
            var copy = await ReadDirectoryTransferCopyCoreAsync(transferId, transaction, token).ConfigureAwait(false);
            var result = await ReadDirectoryTransferTargetCoreAsync(copy, transaction, token).ConfigureAwait(false);
            transaction.Commit();
            return result;
        }
        finally { _gate.Release(); }
    }

    internal async Task<MirrorPulseDirectoryTransferTargetVerification> RecordNamespaceDirectoryTransferTargetAsync(
        MirrorPulseDirectoryTransferTargetVerification original, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(original);
        if (original.Members is not { Count: >= 1 and <= 65_536 })
            throw new ArgumentException("The directory target verification requires a bounded original inventory.", nameof(original));
        var verified = original with { Members = Array.AsReadOnly(original.Members.ToArray()) };
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(verified, TopologyJsonOptions);
        if (payload.Length > DirectoryTransferAdmissionLimit)
            throw new ArgumentException("The directory target verification exceeds its bounded member inventory.", nameof(original));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var copy = await ReadDirectoryTransferCopyCoreAsync(original.OperationId, transaction, token).ConfigureAwait(false);
            ValidateDirectoryTransferTarget(copy, verified);
            var retained = await ReadDirectoryTransferTargetCoreAsync(copy, transaction, token).ConfigureAwait(false);
            if (retained is not null)
            {
                if (!payload.AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(retained, TopologyJsonOptions)))
                    throw new InvalidOperationException("The selected original directory target verification is immutable.");
                transaction.Commit();
                return retained;
            }
            var plan = copy.Admission;
            await RequireNamespaceTransferRoutesAsync([plan.SourceRoute, plan.TargetRoute], transaction, token,
                requireSingleRoot: true).ConfigureAwait(false);
            await RequireCurrentDirectoryTransferTreeAsync(plan, transaction, token).ConfigureAwait(false);
            validation.Dispose();
            await using var sql = _connection.CreateCommand();
            sql.Transaction = transaction;
            sql.CommandText = """
                INSERT INTO namespace_directory_transfer_target_verifications(transfer_id,payload,fingerprint)
                VALUES($transfer,$payload,$fingerprint);
                """;
            sql.Parameters.AddWithValue("$transfer", verified.OperationId.ToString("D"));
            sql.Parameters.AddWithValue("$payload", payload);
            sql.Parameters.AddWithValue("$fingerprint", SHA256.HashData(payload));
            await sql.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit();
            return verified;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseDirectoryTransferTargetVerification?> ReadDirectoryTransferTargetCoreAsync(
        MirrorPulseDirectoryTransferCopy copy, SqliteTransaction transaction, CancellationToken token)
    {
        await using var sql = _connection.CreateCommand();
        sql.Transaction = transaction;
        sql.CommandText = "SELECT payload,fingerprint FROM namespace_directory_transfer_target_verifications WHERE transfer_id=$transfer;";
        sql.Parameters.AddWithValue("$transfer", copy.Admission.Command.CommandId.ToString("D"));
        await using var reader = await sql.ExecuteReaderAsync(token).ConfigureAwait(false);
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
        return ReadNamespaceMovePayload<MirrorPulseDirectoryTransferTargetVerification>((byte[])reader[0], (byte[])reader[1],
            DirectoryTransferAdmissionLimit, value => ValidateDirectoryTransferTarget(copy, value));
    }

    private static void ValidateDirectoryTransferTarget(MirrorPulseDirectoryTransferCopy copy,
        MirrorPulseDirectoryTransferTargetVerification value)
    {
        var plan = copy.Admission;
        if (value.Version != 1 || value.OperationId != plan.Command.CommandId ||
            value.Members is null || value.Members.Count != plan.Members.Count || value.VerifiedAt == default ||
            copy.Steps.Count != plan.Members.Count || copy.Steps.Any(step => step.AcceptedAt is null || step.AcceptedAt > value.VerifiedAt))
            throw new ArgumentException("Directory target verification requires every original accepted copy and its complete final inventory.");
        var remoteIds = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < plan.Members.Count; index++)
        {
            var member = plan.Members[index];
            var target = value.Members[index];
            bool directory = member.Source.Protection.Context.Member.IsDirectory;
            string path = member.TargetRelativePath[(plan.TargetRoute.DirectoryName.Length + 1)..];
            if (target is null || target.CopyOperationId != member.CopyOperationId || target.RelativePath != path ||
                target.IsDirectory != directory || target.Content != member.Content ||
                InvalidRemoteUpdateText(target.RemoteId, 4096) || InvalidRemoteUpdateText(target.Revision, 4096) ||
                !remoteIds.Add(target.RemoteId) ||
                !directory && target.Revision != copy.Steps[index].AcceptedRevision)
                throw new ArgumentException("The verified target must retain every exact original copy effect and complete file proof.");
        }
    }
}
