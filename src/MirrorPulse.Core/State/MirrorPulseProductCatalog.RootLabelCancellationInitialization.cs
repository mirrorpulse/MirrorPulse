using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MirrorPulse.Core.State;

/// <summary>The immutable original Label, public proof and registered objects selecting pending Label recovery.</summary>
/// <remarks>This purpose grants neither native permission nor current-tree readiness.</remarks>
public sealed record MirrorPulseRootLabelCancellationInitialization(Guid RootLabelOperationId,
    Guid RootMemberId, Guid AnchorMemberId, string LabelProofSha256);

public sealed partial class MirrorPulseProductCatalog
{
    /// <summary>Retains the existing initializer or admits the exact selected Label recovery owner atomically.</summary>
    /// <remarks>Captured roots without an initializer keep their existing cancellation route. No native
    /// facts are adopted, permissions changed, cancellation recorded or dispatch fence released here.</remarks>
    public async Task<MirrorPulseNamespaceInitializationIntent?> PrepareRootLabelCancellationInitializationAsync(
        Guid operationId, Guid initializationOperationId, DateTimeOffset preparedAt, CancellationToken cancellationToken = default) =>
        await PrepareRootLabelCancellationInitializationAsync(operationId, initializationOperationId, preparedAt, null, cancellationToken).ConfigureAwait(false);

    /// <summary>Admits execution recovery using the exact registered root provenance retained by the public Label.</summary>
    public async Task<MirrorPulseNamespaceInitializationIntent?> PrepareRootLabelCancellationInitializationAsync(
        Guid operationId, Guid initializationOperationId, DateTimeOffset preparedAt,
        MirrorPulseNamespaceInitializationRoot? executionRoot, CancellationToken cancellationToken = default)
    {
        if (operationId == Guid.Empty || initializationOperationId == Guid.Empty || operationId == initializationOperationId || preparedAt == default)
            throw new ArgumentException("Cancellation initialization requires distinct original Label and initialization identities and chronology.");
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed(); using var transaction = _connection.BeginTransaction();
            using var validation = BeginNamespaceRecoveryValidationReadPhase(transaction);
            var owner = await ReadRootLabelCancellationRecoveryOwnerCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            if (owner is not null)
            {
                var retained = await ReadNamespaceInitializationCoreAsync(owner.InitializationOperationId, transaction, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The original cancellation initializer is missing.");
                transaction.Commit(); return retained;
            }
            var tree = await ReadRootLabelCancellationTreeCoreAsync(operationId, transaction, cancellationToken).ConfigureAwait(false);
            var root = tree.Members.Single(member => member.Context.Member.MemberId == tree.RootMemberId);
            if (root.Context.Member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth && executionRoot is null)
            { transaction.Commit(); return null; }
            var history = await ReadManagedRootRenameHistoryCoreAsync(operationId, cancellationToken, transaction).ConfigureAwait(false);
            if (preparedAt < history.Proof!.CapturedAt || preparedAt < tree.Anchor.Context.Protection.VerifiedAt ||
                tree.Members.Any(member => preparedAt < member.Context.Protection.VerifiedAt || preparedAt < member.Context.Member.RegisteredAt))
                throw new InvalidOperationException("Cancellation initialization cannot predate its original proof and retained member protections.");
            if (await ReadNamespaceInitializationCoreAsync(initializationOperationId, transaction, cancellationToken).ConfigureAwait(false) is not null)
                throw new InvalidOperationException("Cancellation cannot replace another initialization identity.");
            var intent = new MirrorPulseNamespaceInitializationIntent(5, initializationOperationId,
                Array.AsReadOnly(new[] { executionRoot ?? new MirrorPulseNamespaceInitializationRoot(history.Intent.RootId, Guid.Empty)
                    { BornRootMemberId = tree.RootMemberId } }), preparedAt)
            {
                RequestedInventoryKind = MirrorPulseNamespaceInitializationInventoryKind.CurrentInventory,
                RootLabelCancellation = new(operationId, tree.RootMemberId, tree.Anchor.Context.Member.MemberId, RootLabelInitializationProofHash(history.Proof))
            };
            ValidateNamespaceInitialization(intent);
            await RequireRootLabelInitializationRootAsync(intent, root.Context.Member, transaction, cancellationToken).ConfigureAwait(false);
            validation.Dispose();
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
            (intent.Roots[0].BornRootMemberId == purpose.RootMemberId ||
                intent.Roots[0].BornRootMemberId is null && intent.Roots[0].ManifestId != Guid.Empty) &&
            purpose.LabelProofSha256 is { Length: 64 } hash &&
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
            root is not { IsDirectory: true } ||
            root.RootId != history.Intent.RootId || root.LocalObject != proof.LocalObject || root.ParentMemberId != purpose.AnchorMemberId ||
            anchor is not { Origin: MirrorPulseNamespaceMemberOrigin.CapturedOriginal, RootId: null, ParentMemberId: null, IsDirectory: true } ||
            anchor.RelativePath.Length != 0 || anchor.OwnerSid != root.OwnerSid ||
            anchor.LocalObject.VolumeSerialNumber != root.LocalObject.VolumeSerialNumber ||
            anchor.LocalObject.LocalFileId != root.LocalObject.SyncRootFileId || anchor.LocalObject.SyncRootFileId != root.LocalObject.SyncRootFileId)
            throw new InvalidDataException("Label initialization lost its exact original public proof, registered root or physical anchor.");
        await RequireRootLabelInitializationRootAsync(intent, root, transaction, token).ConfigureAwait(false);
    }

    private async Task RequireRootLabelInitializationRootAsync(MirrorPulseNamespaceInitializationIntent intent,
        MirrorPulseNamespaceMemberRegistration member, SqliteTransaction transaction, CancellationToken token)
    {
        var selected = intent.Roots[0];
        if (selected.RootId != member.RootId)
            throw new InvalidDataException("Label initialization cannot replace its selected original root.");
        if (selected.BornRootMemberId is { } born)
        {
            if (member.Origin != MirrorPulseNamespaceMemberOrigin.ProtectedBirth || born != member.MemberId)
                throw new InvalidDataException("Label initialization lost its registered born-root provenance.");
            _ = await RequireNamespaceInitializationBornRootAsync(selected, intent.PreparedAt, transaction, token).ConfigureAwait(false);
            return;
        }
        var capture = await ReadPermissionTreeCoreAsync(selected.ManifestId, token, transaction).ConfigureAwait(false);
        if (member.Origin != MirrorPulseNamespaceMemberOrigin.CapturedOriginal ||
            capture is not { Phase: MirrorPulseNamespacePermissionTreePhase.Sealed, Seal: not null } ||
            capture.Definition.Anchor.Baseline.EvidenceId != member.OriginId ||
            capture.Definition.Anchor.Baseline.RootId != member.RootId ||
            capture.Definition.Anchor.Baseline.LocalObject != member.LocalObject ||
            capture.Definition.Anchor.Baseline.OwnerSid != member.OwnerSid || capture.Seal.SealedAt > intent.PreparedAt)
            throw new InvalidDataException("Label initialization requires its exact original sealed root capture.");
    }
}
