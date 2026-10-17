using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CfSharp;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Preserves a file and retires its protected original before a conditional remote deletion.</summary>
[SupportedOSPlatform("windows10.0.26100")]
internal sealed class MirrorPulseConflictDeletionLocalCoordinator(CloudFileSystem fileSystem, MirrorPulseProductCatalog catalog,
    MirrorPulseRootRouter router, MirrorPulseNamespaceActor actor, MirrorPulseNamespaceExecutionSession execution,
    MirrorPulseConflictCopyStore copies)
{
    private readonly MirrorPulseNamespaceContentConfirmation _capture = new(catalog, router, actor, execution);

    internal Task<MirrorPulseCoalescingContent?> CaptureAsync(MirrorPulseConflictRecord conflict, Guid itemId, CancellationToken token) =>
        execution.RunNormalUserOperationAsync(async () =>
        {
            var file = fileSystem.GetFile(conflict.RelativePath);
            var before = await file.InspectAsync(token).ConfigureAwait(false);
            if (!before.Exists)
            {
                RequireTombstone(before, itemId);
                return null;
            }
            if (before.IsTombstone || before.IsPlaceholder && before.ContentAvailability != CloudContentAvailability.FullyAvailable)
                throw new InvalidOperationException("Preserving local conflict bytes requires a resident file.");
            await using var guard = OpenGuard(file);
            var binding = await _capture.CaptureUploadBindingAsync(file, await file.InspectAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);
            if (await catalog.ReadNamespaceMemberByBindingAsync(binding.LocalObject, token).ConfigureAwait(false) is null)
                throw new InvalidOperationException("Destructive conflict decisions require a registered protected file.");
            var content = new MirrorPulseCoalescingContent(guard.Length,
                Convert.ToHexString(await SHA256.HashDataAsync(guard, token).ConfigureAwait(false)), binding);
            await RequireOriginalAsync(file, content, itemId, token).ConfigureAwait(false);
            return content;
        });

    private sealed record LocalRemovalIntent(Guid CommandId, Guid ConflictId, Guid ItemId,
        string LocalRelativePath, MirrorPulseCoalescingContent? LocalContent);
    private sealed record LocalRemoval(LocalRemovalIntent Intent, MirrorPulseConflictLocalRemovalPhase Phase);

    internal Task CompleteAsync(MirrorPulseConflictDeletion deletion, CancellationToken token) =>
        CompleteCoreAsync(new(new(deletion.Intent.CommandId, deletion.Intent.ConflictId, deletion.Intent.ItemId,
            deletion.Intent.LocalRelativePath, deletion.Intent.LocalContent),
            (MirrorPulseConflictLocalRemovalPhase)Math.Min((int)deletion.Phase, 3)),
            (from, to, stop) => catalog.AdvanceConflictDeletionAsync(deletion.Intent.CommandId,
                (MirrorPulseConflictDeletionPhase)from, (MirrorPulseConflictDeletionPhase)to, stop), null, token);

    internal Task CompleteLocalAsync(MirrorPulseConflictLocalRemoval removal,
        Func<CloudItemSnapshot, CancellationToken, ValueTask> beforeDelete, CancellationToken token) =>
        CompleteCoreAsync(new(new(removal.Intent.CommandId, removal.Intent.ConflictId, removal.Intent.ItemId,
            removal.Intent.LocalRelativePath, removal.Intent.LocalContent),
            (MirrorPulseConflictLocalRemovalPhase)Math.Min((int)removal.Phase, 3)),
            (from, to, stop) => catalog.AdvanceConflictLocalRemovalAsync(removal.Intent.CommandId, from, to, stop), beforeDelete, token);

    private Task CompleteCoreAsync(LocalRemoval deletion,
        Func<MirrorPulseConflictLocalRemovalPhase, MirrorPulseConflictLocalRemovalPhase, CancellationToken, Task> advance,
        Func<CloudItemSnapshot, CancellationToken, ValueTask>? beforeDelete, CancellationToken token) =>
        execution.RunNormalUserOperationAsync(async () =>
        {
            var intent = deletion.Intent;
            var conflict = await catalog.ReadUploadConflictAsync(intent.ConflictId, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The preserved decision lost its original conflict.");
            var file = fileSystem.GetFile(intent.LocalRelativePath);
            if (intent.LocalContent is null)
            {
                RequireTombstone(await file.InspectAsync(token).ConfigureAwait(false), intent.ItemId);
                await AdvanceLocalBoundariesAsync(deletion, advance, token).ConfigureAwait(false);
                return;
            }
            var content = intent.LocalContent;
            var member = await catalog.ReadNamespaceMemberByBindingAsync(content.UploadBinding.LocalObject, token).ConfigureAwait(false)
                ?? throw new InvalidDataException("The preserved local object lost its registered membership.");
            Guid retirementId = new(SHA256.HashData(Encoding.UTF8.GetBytes("MirrorPulse.ConflictDeletion.Retirement.v1/" + intent.CommandId.ToString("D"))).AsSpan(0, 16));
            var retirement = new MirrorPulseNamespaceRetirementCoordinator(fileSystem, catalog, router, execution, actor) { BeforeNativeDeletion = beforeDelete };
            var current = await file.InspectAsync(token).ConfigureAwait(false);
            if (deletion.Phase >= MirrorPulseConflictLocalRemovalPhase.Retired)
            {
                _ = await VerifyPreservedAsync(intent.CommandId, conflict, content, token).ConfigureAwait(false);
                RequireTombstone(current, intent.ItemId);
                return;
            }
            if (!current.Exists)
            {
                // Absence alone is insufficient. Only the same started protected
                // retirement may recover native success before its durable receipt.
                var retained = await catalog.ReadNamespaceRetirementAsync(retirementId, token).ConfigureAwait(false);
                if (deletion.Phase != MirrorPulseConflictLocalRemovalPhase.Retiring || retained?.StartedAt is null || retained.Intent.MemberId != member.MemberId)
                    throw new InvalidDataException("Local absence has no original controlled retirement start.");
                _ = await VerifyPreservedAsync(intent.CommandId, conflict, content, token).ConfigureAwait(false);
                _ = await retirement.ExecuteAsync(retirementId, token).ConfigureAwait(false);
            }
            else
            {
                // Delete sharing permits our controlled retirement. Denying write
                // sharing pins the latest verified bytes across preservation and
                // deletion; registered parent/child ACLs retain namespace authority.
                await using var guard = OpenGuard(file);
                await RequireOriginalAsync(file, content, intent.ItemId, token).ConfigureAwait(false);
                if (guard.Length != content.Length || Convert.ToHexString(await SHA256.HashDataAsync(guard, token).ConfigureAwait(false)) != content.Sha256)
                    throw new InvalidDataException("Local bytes changed after this explicit preservation decision.");
                _ = await copies.PreserveDecisionAsync(intent.CommandId, conflict, MirrorPulseConflictPreservedSide.Local,
                    stop => { stop.ThrowIfCancellationRequested(); return ValueTask.FromResult<Stream>(OpenGuard(file)); }, token).ConfigureAwait(false);
                _ = await VerifyPreservedAsync(intent.CommandId, conflict, content, token).ConfigureAwait(false);
                if (deletion.Phase == MirrorPulseConflictLocalRemovalPhase.Prepared)
                {
                    await advance(deletion.Phase, MirrorPulseConflictLocalRemovalPhase.Preserved, token).ConfigureAwait(false);
                    deletion = deletion with { Phase = MirrorPulseConflictLocalRemovalPhase.Preserved };
                }
                var prepared = await retirement.PrepareAsync(retirementId, member.MemberId, token).ConfigureAwait(false);
                if (prepared.Intent.LocalObject != content.UploadBinding.LocalObject || prepared.Intent.RelativePath != intent.LocalRelativePath)
                    throw new InvalidDataException("The retirement does not belong to the preserved original object and location.");
                if (deletion.Phase == MirrorPulseConflictLocalRemovalPhase.Preserved)
                {
                    await advance(deletion.Phase, MirrorPulseConflictLocalRemovalPhase.Retiring, token).ConfigureAwait(false);
                    deletion = deletion with { Phase = MirrorPulseConflictLocalRemovalPhase.Retiring };
                }
                _ = await retirement.ExecuteAsync(retirementId, token).ConfigureAwait(false);
            }
            RequireTombstone(await file.InspectAsync(token).ConfigureAwait(false), intent.ItemId);
            await advance(MirrorPulseConflictLocalRemovalPhase.Retiring,
                MirrorPulseConflictLocalRemovalPhase.Retired, token).ConfigureAwait(false);
        });

    internal Task<string?> ReadPreservedPathAsync(MirrorPulseConflictDeletionIntent intent, CancellationToken token) =>
        ReadPreservedPathCoreAsync(intent.CommandId, intent.ConflictId, intent.LocalContent, token);

    internal Task<string?> ReadPreservedPathAsync(MirrorPulseConflictLocalRemovalIntent intent, CancellationToken token) =>
        ReadPreservedPathCoreAsync(intent.CommandId, intent.ConflictId, intent.LocalContent, token);

    private async Task<string?> ReadPreservedPathCoreAsync(Guid commandId, Guid conflictId, MirrorPulseCoalescingContent? content, CancellationToken token)
    {
        if (content is null) return null;
        var conflict = await catalog.ReadUploadConflictAsync(conflictId, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The preservation conflict is missing.");
        return await VerifyPreservedAsync(commandId, conflict, content, token).ConfigureAwait(false);
    }

    private async Task<string> VerifyPreservedAsync(Guid commandId, MirrorPulseConflictRecord conflict, MirrorPulseCoalescingContent content, CancellationToken token)
    {
        string path = await copies.PreserveDecisionAsync(commandId, conflict, MirrorPulseConflictPreservedSide.Local,
            _ => throw new InvalidDataException("The committed preserved local copy is missing."), token).ConfigureAwait(false);
        // PreserveDecisionAsync verifies the committed manifest and its bytes.
        // Also bind that manifest to this command's captured original content.
        await using var stream = new FileStream(path + ".manifest.json", FileMode.Open, FileAccess.Read, FileShare.Read);
        var manifest = await JsonSerializer.DeserializeAsync<MirrorPulseConflictCopyManifest>(stream, cancellationToken: token).ConfigureAwait(false);
        if (manifest?.Length != content.Length || manifest.Sha256 != content.Sha256)
            throw new InvalidDataException("The preserved copy does not match the authorized original bytes.");
        return path;
    }

    private async Task RequireOriginalAsync(CloudFile file, MirrorPulseCoalescingContent content, Guid itemId, CancellationToken token)
    {
        var snapshot = await file.InspectAsync(token).ConfigureAwait(false);
        if (!snapshot.Exists || snapshot.IsTombstone || snapshot.Length != content.Length || snapshot.LocalBinding is not { } binding ||
            new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != content.UploadBinding.LocalObject ||
            snapshot.IsPlaceholder && snapshot.ContentAvailability != CloudContentAvailability.FullyAvailable)
            throw new InvalidDataException("The preserved local object changed before retirement.");
        var member = await catalog.ReadNamespaceMemberByBindingAsync(content.UploadBinding.LocalObject, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The original file has no protected membership.");
        var location = await catalog.ReadNamespaceMemberLocationAsync(member.MemberId, token).ConfigureAwait(false);
        var identity = await catalog.ReadNamespaceMemberIdentityAsync(member.MemberId, token).ConfigureAwait(false);
        if (member.IsDirectory || location.RelativePath != file.RelativePath.Replace('\\', '/') || identity.ItemId != itemId ||
            snapshot.ItemId is { } observed && observed != itemId)
            throw new InvalidDataException("The original file lost its logical identity or committed location.");
    }

    private static async Task AdvanceLocalBoundariesAsync(LocalRemoval deletion,
        Func<MirrorPulseConflictLocalRemovalPhase, MirrorPulseConflictLocalRemovalPhase, CancellationToken, Task> advance,
        CancellationToken token)
    {
        while (deletion.Phase < MirrorPulseConflictLocalRemovalPhase.Retired)
        {
            var next = (MirrorPulseConflictLocalRemovalPhase)((int)deletion.Phase + 1);
            await advance(deletion.Phase, next, token).ConfigureAwait(false);
            deletion = deletion with { Phase = next };
        }
    }

    private static FileStream OpenGuard(CloudFile file) => new(file.FullPath, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static void RequireTombstone(CloudItemSnapshot snapshot, Guid itemId)
    {
        if (snapshot.Exists || !snapshot.IsTombstone || snapshot.ItemId != itemId)
            throw new InvalidDataException("The original logical item has not reached its public local tombstone.");
    }
}
