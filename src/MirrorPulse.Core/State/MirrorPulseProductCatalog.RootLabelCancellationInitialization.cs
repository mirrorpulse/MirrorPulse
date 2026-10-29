using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>The immutable original Label, public proof and registered objects selecting cancellation recovery.</summary>
/// <remarks>This purpose grants neither native permission nor current-tree readiness.</remarks>
public sealed record MirrorPulseRootLabelCancellationInitialization(Guid RootLabelOperationId,
    Guid RootMemberId, Guid AnchorMemberId, string LabelProofSha256);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains the existing initializer or admits one exact born-root cancellation owner atomically.</summary>
    /// <remarks>Captured roots without an initializer keep their existing cancellation route. No native
    /// facts are adopted, permissions changed, cancellation recorded or dispatch fence released here.</remarks>
    public async Task<MirrorPulseNamespaceInitializationIntent?> PrepareRootLabelCancellationInitializationAsync(
        Guid operationId, Guid initializationOperationId, DateTimeOffset preparedAt, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || initializationOperationId == Guid.Empty || operationId == initializationOperationId || preparedAt == default)
            throw new ArgumentException("Cancellation initialization requires distinct original Label and initialization identities and chronology.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            var owner = await ReadRootLabelCancellationRecoveryOwnerCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (owner is not null)
            {
                var retained = await ReadNamespaceInitializationCoreAsync(owner.InitializationOperationId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original cancellation initializer is missing.");
                transaction.Commit(); return retained;
            }
            var tree = await ReadRootLabelCancellationTreeCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var root = tree.Members.Single(member => member.Context.Member.MemberId == tree.RootMemberId);
            if (root.Context.Member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth)
            { transaction.Commit(); return null; }
            var history = await ReadManagedRootRenameHistoryCoreAsync(operationId, cancellationToken, transaction).ConfigureAwait(false);
            if (preparedAt < history.Proof!.CapturedAt || preparedAt < tree.Anchor.Context.Protection.VerifiedAt ||
                tree.Members.Any(member => preparedAt < member.Context.Protection.VerifiedAt || preparedAt < member.Context.Member.RegisteredAt))
                throw new InvalidOperationException("Cancellation initialization cannot predate its original proof and retained member protections.");
            if (await ReadNamespaceInitializationCoreAsync(initializationOperationId, transaction, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Cancellation cannot replace another initialization identity.");
            var intent = new MirrorPulseNamespaceInitializationIntent(5, initializationOperationId,
                Array.AsReadOnly(new[] { new MirrorPulseNamespaceInitializationRoot(history.Intent.RootId, Guid.Empty) { BornRootMemberId = tree.RootMemberId } }), preparedAt)
            {
                RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory,
                RootLabelCancellation = new(operationId, tree.RootMemberId, tree.Anchor.Context.Member.MemberId, RootLabelInitializationProofHash(history.Proof))
            };
            ValidateNamespaceInitialization(intent);
            await RequireNamespaceInitializationBornRootAsync(intent.Roots[0], intent.PreparedAt, transaction, cancellationToken).ConfigureAwait(false);
            await InsertNamespaceInitializationCoreAsync(intent, transaction, cancellationToken).ConfigureAwait(false);
            transaction.Commit(); return intent;
        }
        finally { _gate.Release(); }
    }

    private static bool HasValidRootLabelCancellationInitializationPurpose(MirrorPulseNamespaceInitializationIntent intent)
    {
        if (intent.Version != 5) return intent.RootLabelCancellation is null;
        return intent.RootLabelCancellation is { } purpose && purpose.RootLabelOperationId != Guid.Empty &&
            purpose.RootLabelOperationId != intent.OperationId && purpose.RootMemberId != Guid.Empty &&
            purpose.AnchorMemberId != Guid.Empty && purpose.AnchorMemberId != purpose.RootMemberId &&
            intent.Roots[0].BornRootMemberId == purpose.RootMemberId && purpose.LabelProofSha256 is { Length: 64 } hash &&
            hash.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'F');
    }

    private static string RootLabelInitializationProofHash(MirrorPulseRootRenameProof proof) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(proof, TopologyJsonOptions)));

    private async Task RequireRootLabelCancellationInitializationHistoryAsync(MirrorPulseNamespaceInitializationIntent intent,
        Guid? expectedLabelOperationId, SqliteTransaction transaction, CancellationToken token)
    {
        if (intent.RootLabelCancellation is not { } purpose) return;
        if (expectedLabelOperationId is { } expected && expected != purpose.RootLabelOperationId)
            throw new InvalidDataException("Cancellation initialization belongs to a different original Label.");
        var history = await ReadManagedRootRenameHistoryCoreAsync(purpose.RootLabelOperationId, token, transaction).ConfigureAwait(false);
        var root = await ReadNamespaceMemberCoreAsync(purpose.RootMemberId, transaction, token).ConfigureAwait(false);
        var anchor = await ReadNamespaceMemberCoreAsync(purpose.AnchorMemberId, transaction, token).ConfigureAwait(false);
        if (history.Proof is not { DirectoryMoveEvidence: not null } proof || RootLabelInitializationProofHash(proof) != purpose.LabelProofSha256 ||
            history.Intent.RootId != intent.Roots[0].RootId || proof.CapturedAt > intent.PreparedAt ||
            root is not { Origin: MirrorPulseNamespaceMemberOrigin.ProtectedBirth, IsDirectory: true } ||
            root.RootId != history.Intent.RootId || root.LocalObject != proof.LocalObject || root.ParentMemberId != purpose.AnchorMemberId ||
            anchor is not { Origin: MirrorPulseNamespaceMemberOrigin.CapturedOriginal, RootId: null, ParentMemberId: null, IsDirectory: true } ||
            anchor.RelativePath.Length != 0 || anchor.OwnerSid != root.OwnerSid ||
            anchor.LocalObject.VolumeSerialNumber != root.LocalObject.VolumeSerialNumber ||
            anchor.LocalObject.LocalFileId != root.LocalObject.SyncRootFileId || anchor.LocalObject.SyncRootFileId != root.LocalObject.SyncRootFileId)
            throw new InvalidDataException("Cancellation initialization lost its exact original Label proof, registered born root or physical anchor.");
    }
}
