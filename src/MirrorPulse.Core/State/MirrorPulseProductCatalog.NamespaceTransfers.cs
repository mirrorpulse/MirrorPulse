using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.State;

/// <summary>Original backend scope, without credential values or raw connection settings.</summary>
public sealed record MirrorPulseTransferRoute(InstanceId InstanceId, RootId RootId, InstallId InstallId,
    string RootKey, string DirectoryName, int ProtocolVersion, string ScopeSha256)
{
    public static MirrorPulseTransferRoute Capture(AdapterInstance instance, RootRegistration root, int protocolVersion)
    {
        ArgumentNullException.ThrowIfNull(instance); ArgumentNullException.ThrowIfNull(root);
        if (!instance.Enabled || root.State != RootRegistrationState.Active || root.InstanceId != instance.InstanceId ||
            root.AdapterId != instance.AdapterId || protocolVersion is not (1 or 2) ||
            protocolVersion == 1 && root.IdentityScope != RootIdentityScope.LegacyInstance)
            throw new InvalidOperationException("A transfer requires an enabled original instance and an active, explicitly routed root.");
        byte[] scope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            Adapter = instance.AdapterId.ToString(),
            Install = instance.InstallId.ToString(),
            Configuration = instance.Configuration.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray(),
            Credentials = instance.CredentialReferences.Order(StringComparer.Ordinal).ToArray(),
            root.UniquenessKey,
            root.IdentityScope
        });
        return new(instance.InstanceId, root.RootId, instance.InstallId, root.UniquenessKey,
            root.DirectoryName, protocolVersion, Convert.ToHexString(SHA256.HashData(scope)));
    }
}

/// <summary>An original file copy admission. Source retirement needs a separate verified completion protocol.</summary>
/// <remarks>The private content ID is not a path or proof that bytes have been captured. The Host must
/// durably retain and freshly verify that content before dispatch. Original member provenance is immutable;
/// the destination will require its own birth and native/public identity. No phase here authorizes deletion.</remarks>
public sealed record MirrorPulseNamespaceTransferPlan(int Version, Guid OperationId,
    MirrorPulseTransferRoute SourceRoute, MirrorPulseTransferRoute TargetRoute,
    MirrorPulseNamespaceMemberProtectionSnapshot Source, MirrorPulseNamespaceMemberProtectionSnapshot SourceParent,
    MirrorPulseNamespaceMemberProtectionSnapshot TargetParent, string TargetRelativePath, string? SourceRemoteRevision,
    Guid PrivateContentId, long Length, string Sha256, Guid CopyOperationId, Guid DeleteOperationId, DateTimeOffset PreparedAt);

/// <summary>A durable copy start is uncertainty until the original upload receipt is saved.</summary>
public sealed record MirrorPulseNamespaceTransferCopy(MirrorPulseNamespaceTransferPlan Plan,
    DateTimeOffset? CopyStartedAt, string? AcceptedRevision, DateTimeOffset? CopyAcceptedAt)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseTransferTargetVerification? TargetVerification { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public Guid? TargetBirthOperationId { get; init; }

    [System.Text.Json.Serialization.JsonIgnore]
    public MirrorPulseNamespaceTransferTargetBirth? TargetBirth { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceTransferTargetPublication? TargetPublication { get; init; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public MirrorPulseNamespaceTransferSourceDeletion? SourceDeletion { get; init; }
}

/// <summary>Whole target content and identity observed at the original accepted revision, not source retirement authority.</summary>
public sealed record MirrorPulseTransferTargetVerification(int Version, Guid OperationId, string AcceptedRevision,
    string RemoteId, long Length, string Sha256, DateTimeOffset VerifiedAt);

public sealed partial class MirrorPulseProductCatalog
{
    public async Task<MirrorPulseNamespaceTransferCopy> PrepareNamespaceTransferAsync(
        MirrorPulseNamespaceTransferPlan plan, CancellationToken token = default)
    {
        ValidateNamespaceTransferPlan(plan);
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var replay = await ReadNamespaceTransferCoreAsync(plan.OperationId, transaction, token).ConfigureAwait(false);
            if (replay is not null)
            {
                if (replay.Plan != plan) throw new InvalidOperationException("The original transfer and its source bytes are immutable.");
                transaction.Commit(); return replay;
            }
            foreach (Guid id in new[] { plan.OperationId, plan.CopyOperationId, plan.DeleteOperationId })
                await RequireNamespaceTransferIdAvailableAsync(id, transaction, token).ConfigureAwait(false);
            await RequireCurrentNamespaceTransferAsync(plan, transaction, token).ConfigureAwait(false);
            var result = new MirrorPulseNamespaceTransferCopy(plan, null, null, null);
            await WriteNamespaceTransferCoreAsync(result, transaction, token, insert: true).ConfigureAwait(false);
            await using var reserve = _connection.CreateCommand(); reserve.Transaction = transaction;
            reserve.CommandText = """
                INSERT INTO namespace_transfer_roots(root_id,operation_id) VALUES($source,$id),($target,$id);
                INSERT INTO namespace_transfer_effects(effect_id,operation_id,kind) VALUES($copy,$id,0),($delete,$id,1);
                """;
            reserve.Parameters.AddWithValue("$id", plan.OperationId.ToString("D"));
            reserve.Parameters.AddWithValue("$source", plan.SourceRoute.RootId.ToString());
            reserve.Parameters.AddWithValue("$target", plan.TargetRoute.RootId.ToString());
            reserve.Parameters.AddWithValue("$copy", plan.CopyOperationId.ToString("D"));
            reserve.Parameters.AddWithValue("$delete", plan.DeleteOperationId.ToString("D"));
            await reserve.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Historical state; a saved upload receipt is neither full copy verification nor permission to delete.</summary>
    public async Task<MirrorPulseNamespaceTransferCopy?> ReadNamespaceTransferAsync(Guid operationId, CancellationToken token = default)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A transfer ID is required.", nameof(operationId));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction(deferred: true);
            var result = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return result;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Acquires the only first-dispatch claim. Replaying a retained start never grants another upload.</summary>
    public async Task<MirrorPulseNamespaceTransferCopy> StartNamespaceTransferCopyAsync(Guid operationId,
        DateTimeOffset startedAt, CancellationToken token = default)
    {
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            if (copy.CopyStartedAt is not null)
                throw new InvalidOperationException("The original copy may already have executed; another invocation is not authorized.");
            if (startedAt < copy.Plan.PreparedAt) throw new ArgumentException("Copy start must follow its original admission.", nameof(startedAt));
            await RequireCurrentNamespaceTransferAsync(copy.Plan, transaction, token, admitted: true).ConfigureAwait(false);
            copy = copy with { CopyStartedAt = startedAt };
            await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }

    /// <remarks>Only the original successful UploadComplete may supply this revision. Stat, matching remote
    /// bytes, an ambiguous operation or a later session's uncorrelated reply cannot supply an acceptance receipt.</remarks>
    public async Task<MirrorPulseNamespaceTransferCopy> RecordNamespaceTransferCopyAcceptedAsync(Guid operationId,
        string acceptedRevision, DateTimeOffset acceptedAt, CancellationToken token = default)
    {
        if (InvalidRemoteUpdateText(acceptedRevision, 4096)) throw new ArgumentException("An original upload revision is required.", nameof(acceptedRevision));
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var copy = await ReadNamespaceTransferCoreAsync(operationId, transaction, token).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The original transfer is missing.");
            if (copy.CopyStartedAt is not { } started || acceptedAt < started)
                throw new InvalidOperationException("Target acceptance requires its original durable copy start.");
            if (copy.CopyAcceptedAt is not null)
            {
                if (copy.AcceptedRevision != acceptedRevision || copy.CopyAcceptedAt != acceptedAt)
                    throw new InvalidOperationException("The original upload receipt is immutable.");
                transaction.Commit(); return copy;
            }
            copy = copy with { AcceptedRevision = acceptedRevision, CopyAcceptedAt = acceptedAt };
            await WriteNamespaceTransferCoreAsync(copy, transaction, token).ConfigureAwait(false);
            transaction.Commit(); return copy;
        }
        finally { _gate.Release(); }
    }
}
