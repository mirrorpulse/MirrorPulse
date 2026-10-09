using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Closes an unsealed capture while retaining its immutable definition and captured members.</summary>
    /// <remarks>
    /// Releases only this capture's preparation fence. It does not authorize namespace work,
    /// restore ACLs, cancel existing executable changes or prove that previously admitted work drained.
    /// A new capture must use fresh operation IDs; cancelled operation IDs remain non-executable.
    /// </remarks>
    public async Task<MirrorPulseNamespacePermissionTree> CancelNamespacePermissionTreeCaptureAsync(Guid manifestId,
        DateTimeOffset cancelledAt, CancellationToken cancellationToken = default)
    {
        if (manifestId == Guid.Empty || cancelledAt == default) throw new ArgumentException("The capture cancellation is incomplete.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            using SqliteTransaction transaction = _connection.BeginTransaction();
            MirrorPulseNamespacePermissionTree tree = await ReadPermissionTreeCoreAsync(manifestId, cancellationToken, transaction).ConfigureAwait(false)
                ?? throw new FileNotFoundException("The permission tree is not registered.");
            if (tree.Phase == MirrorPulseNamespacePermissionTreePhase.Sealed || cancelledAt < tree.Definition.CreatedAt)
                throw new InvalidOperationException("A sealed capture or an earlier cancellation cannot be abandoned.");
            string fingerprint = await ComputePermissionTreeFingerprintCoreAsync(tree, cancelledAt, cancellationToken, transaction).ConfigureAwait(false);
            var receipt = new MirrorPulseNamespacePermissionTreeCancellation(tree.CapturedMembers, fingerprint, cancelledAt);
            if (tree.Cancellation is not null)
            {
                if (tree.Cancellation != receipt) throw new InvalidOperationException("The capture cancellation is immutable.");
                transaction.Commit();
                return tree;
            }
            await using SqliteCommand executable = _connection.CreateCommand();
            executable.Transaction = transaction;
            executable.CommandText = """
                SELECT 1 FROM namespace_permission_tree_members m JOIN namespace_permission_changes c
                    ON c.operation_id=m.operation_id
                WHERE m.manifest_id=$id LIMIT 1;
                """;
            executable.Parameters.AddWithValue("$id", manifestId.ToString("D"));
            if (await executable.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidDataException("An unsealed capture contains an executable permission change.");
            await using SqliteCommand write = _connection.CreateCommand();
            write.Transaction = transaction;
            write.CommandText = "INSERT INTO namespace_permission_tree_cancellations(manifest_id,payload) VALUES($id,$payload);";
            write.Parameters.AddWithValue("$id", manifestId.ToString("D"));
            write.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(receipt, TopologyJsonOptions));
            await write.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            transaction.Commit();
            return tree with { Phase = MirrorPulseNamespacePermissionTreePhase.Cancelled, Cancellation = receipt };
        }
        finally { _gate.Release(); }
    }

    private async Task ValidatePermissionTreeCancellationCoreAsync(MirrorPulseNamespacePermissionTree tree,
        CancellationToken token, SqliteTransaction? transaction = null)
    {
        if (tree.Cancellation is not { } receipt) return;
        string actual = await ComputePermissionTreeFingerprintCoreAsync(tree, receipt.CancelledAt, token, transaction).ConfigureAwait(false);
        if (!string.Equals(actual, receipt.Fingerprint, StringComparison.Ordinal))
            throw new InvalidDataException("The cancelled original permission evidence changed.");
    }

    private async Task<string> ComputePermissionTreeFingerprintCoreAsync(MirrorPulseNamespacePermissionTree tree,
        DateTimeOffset recordedAt, CancellationToken token, SqliteTransaction? transaction = null)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendPermissionTreeFingerprint(hash, JsonSerializer.Serialize(tree.Definition, TopologyJsonOptions));
        int count = 0;
        while (true)
        {
            IReadOnlyList<MirrorPulseNamespacePermissionTreeMember> page = await ReadPermissionTreeMembersCoreAsync(
                tree.Definition, count - 1, PermissionTreePageLimit, token, transaction).ConfigureAwait(false);
            if (page.Count == 0) break;
            foreach (MirrorPulseNamespacePermissionTreeMember member in page)
            {
                if (member.Sequence != count++ || count > tree.CapturedMembers || member.Preparation.Intent.PreparedAt > recordedAt)
                    throw new InvalidDataException("The permission capture has an invalid sequence, cursor or observation time.");
                AppendPermissionTreeFingerprint(hash, JsonSerializer.Serialize(member, TopologyJsonOptions));
            }
        }
        if (count != tree.CapturedMembers) throw new InvalidDataException("The permission capture cursor does not match its members.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
