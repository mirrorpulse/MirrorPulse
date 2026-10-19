using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>The immutable public apply policy that influences SDK remote-file-move preparation.</summary>
public sealed record MirrorPulseRemoteFileMovePolicy(bool PreserveUnsynchronizedLocalContent,
    bool SuppressLocalEcho, long EchoSuppressionLifetimeTicks, int EchoSuppressionObservationCount);

/// <summary>Admission to a supported SDK protocol, not proof that SDK preparation or movement committed.</summary>
public sealed record MirrorPulseRemoteFileMoveProtocolAdmission(int Version, Guid OperationId,
    string Protocol, string PackageVersion, string PackageSourceCommit, string MoveFingerprint,
    MirrorPulseNamespaceMemberMoveStart OriginalStart, MirrorPulseRemoteFileMovePolicy Policy,
    DateTimeOffset AdmittedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public const string CfSharpRemoteFileMoveProtocol = "CfSharp.RemoteFileMove.v1";

    /// <summary>Retains SDK protocol and policy before dispatch, without replacing the original start.</summary>
    /// <remarks>The Host must first verify the unchanged native source, official source revision,
    /// current role and both protected parents while holding its namespace actor and guards.
    /// Existing moved objects cannot acquire this historical admission from their current target.</remarks>
    public async Task<MirrorPulseRemoteFileMoveProtocolAdmission> AdmitNamespaceRemoteFileMoveProtocolAsync(
        Guid operationId, string packageVersion, string packageSourceCommit, MirrorPulseRemoteFileMovePolicy policy,
        DateTimeOffset admittedAt, CancellationToken cancellationToken = default)
    {
        ValidateRemoteFileMoveLibrary(packageVersion, packageSourceCommit, policy);
        if (operationId == Guid.Empty || admittedAt == default)
            throw new ArgumentException("An original move ID and verified admission time are required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadRemoteFileMoveProtocolCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.PackageVersion != packageVersion || replay.PackageSourceCommit != packageSourceCommit || replay.Policy != policy)
                    throw new InvalidOperationException("The original SDK protocol admission and policy are immutable.");
                transaction.Commit(); return replay;
            }
            var intent = await RequireNamespaceMemberMoveExecutionCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var member = await ReadNamespaceMemberCoreAsync(intent.MemberId, transaction, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidDataException("The original remote file lost its registered member.");
            var start = await ReadNamespaceMemberMoveStartCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (intent.RemoteEntry is null || member.IsDirectory || start?.Version != 2)
                throw new InvalidOperationException("Protocol admission requires an original remote file start with its actual official baseline.");
            await using (var facts = _connection.CreateCommand())
            {
                facts.Transaction = transaction;
                facts.CommandText = """
                    SELECT 1 FROM namespace_member_move_observations WHERE operation_id=$id
                    UNION ALL SELECT 1 FROM namespace_member_move_projections WHERE operation_id=$id
                    UNION ALL SELECT 1 FROM namespace_member_move_completions WHERE operation_id=$id
                    UNION ALL SELECT 1 FROM namespace_remote_move_rejections WHERE operation_id=$id LIMIT 1;
                    """;
                facts.Parameters.AddWithValue("$id", operationId.ToString("D"));
                if (await facts.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                    throw new InvalidOperationException("Existing movement or settlement cannot acquire a new before-dispatch protocol admission.");
            }
            var admission = new MirrorPulseRemoteFileMoveProtocolAdmission(1, operationId, CfSharpRemoteFileMoveProtocol,
                packageVersion, packageSourceCommit, RemoteFileMoveIntentFingerprint(intent), start, policy, admittedAt);
            ValidateRemoteFileMoveProtocol(admission);
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(admission, TopologyJsonOptions);
            await using var insert = _connection.CreateCommand(); insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO namespace_remote_file_move_protocols(operation_id,payload,fingerprint) VALUES($id,$payload,$hash);";
            insert.Parameters.AddWithValue("$id", operationId.ToString("D"));
            insert.Parameters.AddWithValue("$payload", payload); insert.Parameters.AddWithValue("$hash", SHA256.HashData(payload));
            await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return admission;
        }
        finally { _gate.Release(); }
    }

    public async Task<MirrorPulseRemoteFileMoveProtocolAdmission?> ReadNamespaceRemoteFileMoveProtocolAsync(Guid operationId,
        CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An original move ID is required.", nameof(operationId));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var value = await ReadRemoteFileMoveProtocolCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return value;
        }
        finally { _gate.Release(); }
    }

    private async Task<MirrorPulseRemoteFileMoveProtocolAdmission?> ReadRemoteFileMoveProtocolCoreAsync(Guid operationId,
        SqliteTransaction transaction, CancellationToken token)
    {
        MirrorPulseRemoteFileMoveProtocolAdmission admission;
        await using (var query = _connection.CreateCommand())
        {
            query.Transaction = transaction;
            query.CommandText = "SELECT payload,fingerprint FROM namespace_remote_file_move_protocols WHERE operation_id=$id;";
            query.Parameters.AddWithValue("$id", operationId.ToString("D"));
            await using var reader = await query.ExecuteReaderAsync(token).ConfigureAwait(false);
            if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;
            admission = ReadNamespaceMovePayload<MirrorPulseRemoteFileMoveProtocolAdmission>((byte[])reader[0], (byte[])reader[1],
                NamespaceMemberMoveStartPayloadLimit, ValidateRemoteFileMoveProtocol);
        }
        var intent = await ReadNamespaceMemberMoveCoreAsync(operationId, transaction, token, false).ConfigureAwait(false)
            ?? throw new InvalidDataException("The SDK protocol admission lost its original move.");
        var start = await ReadNamespaceMemberMoveStartCoreAsync(operationId, transaction, token, false).ConfigureAwait(false);
        var member = await ReadNamespaceMemberCoreAsync(intent.MemberId, transaction, token).ConfigureAwait(false);
        if (admission.OperationId != operationId || intent.RemoteEntry is null || member is null || member.IsDirectory ||
            admission.OriginalStart != start || admission.MoveFingerprint != RemoteFileMoveIntentFingerprint(intent))
            throw new InvalidDataException("The SDK protocol admission lost its original file, start or immutable batch reference.");
        return admission;
    }

    private static string RemoteFileMoveIntentFingerprint(MirrorPulseNamespaceMemberMoveIntent intent) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(intent, TopologyJsonOptions)));

    private static void ValidateRemoteFileMoveProtocol(MirrorPulseRemoteFileMoveProtocolAdmission admission)
    {
        ValidateRemoteFileMoveLibrary(admission.PackageVersion, admission.PackageSourceCommit, admission.Policy);
        if (admission.Version != 1 || admission.OperationId == Guid.Empty || admission.Protocol != CfSharpRemoteFileMoveProtocol ||
            admission.MoveFingerprint is not { Length: 64 } ||
            admission.MoveFingerprint.Any(character => character is not (>= '0' and <= '9') and not (>= 'A' and <= 'F')) ||
            admission.OriginalStart is not { Version: 2 } ||
            admission.OriginalStart.OperationId != admission.OperationId || admission.AdmittedAt == default ||
            admission.AdmittedAt < admission.OriginalStart.StartedAt)
            throw new ArgumentException("The remote file protocol admission is incomplete.", nameof(admission));
        ValidateNamespaceMemberMoveStart(admission.OriginalStart);
    }

    private static void ValidateRemoteFileMoveLibrary(string packageVersion, string sourceCommit, MirrorPulseRemoteFileMovePolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (!MirrorPulseRemoteLibraryProvenance.IsSupported(packageVersion, sourceCommit) ||
            policy.EchoSuppressionLifetimeTicks is <= 0 or > TimeSpan.TicksPerHour ||
            policy.EchoSuppressionObservationCount is < 1 or > 64)
            throw new ArgumentException("The selected SDK package and bounded original apply policy must support the file-move recovery protocol.");
    }

    private static async Task EnsureNamespaceRemoteFileMoveProtocolSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS namespace_remote_file_move_protocols(
                operation_id TEXT PRIMARY KEY REFERENCES namespace_member_moves(operation_id),
                payload BLOB NOT NULL, fingerprint BLOB NOT NULL CHECK(length(fingerprint)=32));
            """;
        await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
    }
}
