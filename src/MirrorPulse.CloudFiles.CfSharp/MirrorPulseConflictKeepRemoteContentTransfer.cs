using System.Runtime.Versioning;
using System.Security.Cryptography;
using CfSharp;
using CfSharp.Native;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp;

internal sealed record MirrorPulseKeepRemoteContentReadback(Guid CommandId, long Length, string Sha256, DateTimeOffset VerifiedAt);

/// <summary>Transfers the selected private bytes through a caller-owned proactive SDK key and reads every byte back.</summary>
/// <remarks>
/// The native caller must retain its durable application start, root admission, original object,
/// parent guards, current role and namespace actor. It prepares metadata/identity/length first.
/// This boundary neither supplies that authority nor opens Worker content, changes identity,
/// resolves conflicts, commits a native application receipt or acknowledges any journal ID.
/// An exception after a native transfer leaves the caller's original recovery owner responsible.
/// </remarks>
internal static class MirrorPulseConflictKeepRemoteContentTransfer
{
    internal const int BufferSize = 64 * 1024;

    [SupportedOSPlatform("windows10.0.26100")]
    internal static async Task<MirrorPulseKeepRemoteContentReadback> TransferAsync(CloudTransfer transfer,
        MirrorPulseConflictKeepRemoteCopies copies, MirrorPulseKeepRemoteJournalSnapshot originals,
        MirrorPulseConflictKeepRemoteJournal journal, MirrorPulseNamespaceMemberProtectionSnapshot target,
        MirrorPulseNamespaceActor actor, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(transfer); ArgumentNullException.ThrowIfNull(copies);
        ArgumentNullException.ThrowIfNull(originals); ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(target); ArgumentNullException.ThrowIfNull(actor);
        actor.AssertAccess();
        var intent = copies.Preparation.Intent;
        var lease = transfer.Lease;
        if (!lease.Options.Exclusive || !lease.Options.Access.HasFlag(CloudItemLeaseAccess.Read | CloudItemLeaseAccess.Write) ||
            lease.Item is not CloudFile || target.Context.Member.IsDirectory || target.Identity.ItemId != intent.ItemId ||
            lease.Item.RelativePath.Replace('\\', '/') != target.Context.Location.RelativePath)
            throw new InvalidOperationException("KeepRemote byte delivery requires the caller's retained exclusive read/write file lease and original member.");
        await journal.RevalidateAsync(originals, copies, token).ConfigureAwait(false);
        await RequireNativeAsync(complete: false).ConfigureAwait(false);
        string hash = await TransferRetainedAsync(copies.Remote.Data, copies.Remote.Proof,
            async (offset, bytes, stop) =>
            {
                RequireLease();
                await transfer.TransferDataAsync(offset, bytes, stop).ConfigureAwait(false);
                RequireLease();
            },
            async (offset, bytes, stop) =>
            {
                RequireLease();
                var result = await transfer.RetrieveDataAsync(offset, bytes, stop).ConfigureAwait(false);
                RequireLease();
                if (!result.CompletionStatus.Equals(NtStatus.Success))
                    throw new IOException("KeepRemote native readback did not complete successfully.");
                return result.BytesRead;
            }, token).ConfigureAwait(false);
        await RequireNativeAsync(complete: true).ConfigureAwait(false);
        await journal.RevalidateAsync(originals, copies, token).ConfigureAwait(false);
        return new(intent.CommandId, copies.Remote.Proof.Length, hash, DateTimeOffset.UtcNow);

        void RequireLease()
        {
            actor.AssertAccess(); token.ThrowIfCancellationRequested();
            if (lease.IsInvalidated) throw new IOException("The original KeepRemote transfer lease was invalidated.");
        }

        async Task RequireNativeAsync(bool complete)
        {
            RequireLease();
            var snapshot = await lease.Item.InspectAsync(token).ConfigureAwait(false);
            if (!snapshot.Exists || !snapshot.IsPlaceholder || snapshot.IsTombstone || snapshot.Kind != CloudItemKind.File ||
                snapshot.ItemId != intent.ItemId || snapshot.Length != copies.Remote.Proof.Length || snapshot.LocalBinding is not { } binding ||
                new MirrorPulseLocalFileBinding(binding.VolumeSerialNumber, binding.SyncRootFileId, binding.LocalFileId) != target.Context.Member.LocalObject ||
                !snapshot.PlaceholderIdentity.Span.SequenceEqual(new CloudPlaceholderIdentity(intent.ItemId,
                    intent.RemoteSource.RemoteId, intent.RemoteSource.RemoteRevision).Encode()) ||
                snapshot.SynchronizationState != CloudSynchronizationState.InSync ||
                complete && snapshot.Length != 0 && snapshot.ContentAvailability != CloudContentAvailability.FullyAvailable)
                throw new InvalidDataException("KeepRemote delivery lost the original object, selected public identity, length or complete resident readback.");
        }
    }

    /// <summary>Bounded byte boundary. The injected delegates do not establish any native or journal authority.</summary>
    internal static async Task<string> TransferRetainedAsync(FileStream source, MirrorPulseConflictKeepBothCopy proof,
        Func<long, ReadOnlyMemory<byte>, CancellationToken, ValueTask> transfer,
        Func<long, Memory<byte>, CancellationToken, ValueTask<int>> retrieve, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(proof);
        ArgumentNullException.ThrowIfNull(transfer); ArgumentNullException.ThrowIfNull(retrieve);
        token.ThrowIfCancellationRequested();
        if (!source.CanRead || !source.CanSeek || proof.Length < 0 || source.Length != proof.Length ||
            proof.Sha256 is not { Length: 64 } || proof.Sha256.Any(character => !char.IsAsciiHexDigitUpper(character)))
            throw new InvalidDataException("KeepRemote requires the complete originally selected readable private copy and canonical byte proof.");
        byte[] buffer = new byte[BufferSize];
        // Authenticate all original private bytes before the first native range, using absolute
        // offsets so the retained lease's stream position is never shared with another reader.
        if (await HashSourceAsync().ConfigureAwait(false) != proof.Sha256)
            throw new InvalidDataException("The selected private KeepRemote copy changed before byte delivery.");
        using var written = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long offset = 0; offset < proof.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = checked((int)Math.Min(BufferSize, proof.Length - offset));
            await ReadSourceExactlyAsync(offset, buffer.AsMemory(0, count)).ConfigureAwait(false);
            written.AppendData(buffer, 0, count);
            await transfer(offset, buffer.AsMemory(0, count), token).ConfigureAwait(false);
            offset += count;
        }
        if (source.Length != proof.Length || Convert.ToHexString(written.GetHashAndReset()) != proof.Sha256)
            throw new InvalidDataException("The selected KeepRemote source changed during byte delivery.");
        using var readback = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (long offset = 0; offset < proof.Length;)
        {
            token.ThrowIfCancellationRequested();
            int count = checked((int)Math.Min(BufferSize, proof.Length - offset));
            int received = await retrieve(offset, buffer.AsMemory(0, count), token).ConfigureAwait(false);
            if (received != count) throw new InvalidDataException("KeepRemote requires complete native range readback without a short or excessive result.");
            readback.AppendData(buffer, 0, received); offset += received;
        }
        string result = Convert.ToHexString(readback.GetHashAndReset());
        if (result != proof.Sha256 || source.Length != proof.Length || await HashSourceAsync().ConfigureAwait(false) != proof.Sha256)
            throw new InvalidDataException("KeepRemote requires exact whole-file native readback and the unchanged original private bytes.");
        token.ThrowIfCancellationRequested(); return result;

        async Task<string> HashSourceAsync()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (long offset = 0; offset < proof.Length;)
            {
                int count = checked((int)Math.Min(BufferSize, proof.Length - offset));
                await ReadSourceExactlyAsync(offset, buffer.AsMemory(0, count)).ConfigureAwait(false);
                hash.AppendData(buffer, 0, count); offset += count;
            }
            if (source.Length != proof.Length) throw new InvalidDataException("The original private KeepRemote copy changed length.");
            return Convert.ToHexString(hash.GetHashAndReset());
        }

        async Task ReadSourceExactlyAsync(long offset, Memory<byte> bytes)
        {
            int received = 0;
            while (received < bytes.Length)
            {
                token.ThrowIfCancellationRequested();
                int count = await RandomAccess.ReadAsync(source.SafeFileHandle, bytes[received..], offset + received, token).ConfigureAwait(false);
                if (count == 0) throw new EndOfStreamException("The selected private KeepRemote source ended before its retained complete length.");
                received += count;
            }
        }
    }
}
