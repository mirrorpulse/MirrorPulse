using System.Security.Cryptography;
using System.Text.Json;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Conflicts;

public enum MirrorPulseConflictPreservedSide
{
    Local,
    Remote,
}

internal enum MirrorPulseConflictCopyCheckpoint { StagingFlushed, DataCommitted, ManifestCommitted }
public sealed record MirrorPulseConflictCopyManifest(int SchemaVersion, Guid ConflictId, string InstanceId,
    MirrorPulseConflictPreservedSide Side, long Length, string? Sha256, string State, string StageFileName);

/// <summary>Preserves one selected side in MP's private data root before a destructive decision.</summary>
public sealed class MirrorPulseConflictCopyStore
{
    private readonly MirrorPulseStoragePaths _paths;
    private readonly Action<MirrorPulseConflictCopyCheckpoint>? _checkpoint;

    public MirrorPulseConflictCopyStore(MirrorPulseStoragePaths paths) : this(paths, null) { }
    internal MirrorPulseConflictCopyStore(MirrorPulseStoragePaths paths, Action<MirrorPulseConflictCopyCheckpoint>? checkpoint)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _checkpoint = checkpoint;
    }

    public Task<string> PreserveAsync(
        MirrorPulseConflictRecord conflict,
        MirrorPulseConflictPreservedSide side,
        Func<CancellationToken, ValueTask<Stream>> openSource,
        CancellationToken cancellationToken = default) => PreserveCoreAsync(conflict, side, null, openSource, cancellationToken);

    /// <summary>Isolates an immutable command's preserved bytes from earlier choices for the same conflict.</summary>
    public Task<string> PreserveDecisionAsync(Guid commandId, MirrorPulseConflictRecord conflict,
        MirrorPulseConflictPreservedSide side, Func<CancellationToken, ValueTask<Stream>> openSource,
        CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("A preservation command ID is required.", nameof(commandId));
        return PreserveCoreAsync(conflict, side, commandId, openSource, cancellationToken);
    }

    private async Task<string> PreserveCoreAsync(MirrorPulseConflictRecord conflict, MirrorPulseConflictPreservedSide side,
        Guid? commandId, Func<CancellationToken, ValueTask<Stream>> openSource, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        ArgumentNullException.ThrowIfNull(openSource);
        if (!Enum.IsDefined(side))
        {
            throw new ArgumentOutOfRangeException(nameof(side), side, null);
        }

        string directory = Path.Combine(
            MirrorPulseConflictDirectory.GetPath(_paths, conflict.InstanceId),
            side.ToString().ToLowerInvariant());
        if (commandId is { } scope) directory = Path.Combine(directory, scope.ToString("D"));
        Directory.CreateDirectory(directory);
        EnsureNoReparsePoints(_paths.DataRootPath, directory);
        string destination = Path.Combine(directory, MirrorPulseConflictFileName.Create(conflict));
        string manifestPath = destination + ".manifest.json";
        string pendingPath = destination + ".pending.json";
        await using var lease = new FileStream(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(destination))
        {
            if (File.Exists(manifestPath))
            {
                MirrorPulseConflictCopyManifest existing = await ReadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
                await VerifyAsync(conflict, side, destination, existing, "complete", cancellationToken).ConfigureAwait(false);
                File.Delete(pendingPath);
                return destination;
            }
            if (!File.Exists(pendingPath))
                throw new InvalidDataException("An existing conflict copy must have a verifiable intent or manifest.");
        }
        else if (File.Exists(manifestPath))
        {
            throw new InvalidDataException("The committed preserved conflict file is missing.");
        }

        if (File.Exists(pendingPath))
        {
            MirrorPulseConflictCopyManifest intent = await ReadManifestAsync(pendingPath, cancellationToken).ConfigureAwait(false);
            ValidateIdentity(conflict, side, intent);
            string fileName = intent.StageFileName;
            if (Path.GetFileName(fileName) != fileName || !fileName.StartsWith(Path.GetFileName(destination) + ".", StringComparison.Ordinal) ||
                !fileName.EndsWith(".tmp", StringComparison.Ordinal))
                throw new InvalidDataException("The conflict staging path is invalid.");
            string stagePath = Path.Combine(directory, fileName);
            if (intent.State == "staged" && (File.Exists(destination) || File.Exists(stagePath)))
            {
                string committed = File.Exists(destination) ? destination : stagePath;
                await VerifyAsync(conflict, side, committed, intent, "staged", cancellationToken).ConfigureAwait(false);
                if (committed == stagePath) File.Move(stagePath, destination, overwrite: false);
                await WriteManifestAsync(manifestPath, intent with { State = "complete" }, cancellationToken).ConfigureAwait(false);
                File.Delete(pendingPath);
                return destination;
            }
            if (File.Exists(destination) || intent.State is not ("copying" or "staged"))
                throw new InvalidDataException("The incomplete conflict save cannot be safely replayed.");
            if (File.Exists(stagePath))
            {
                EnsureNoReparsePoints(_paths.DataRootPath, stagePath);
                File.Delete(stagePath);
            }
            // No committed bytes exist. The conflict remains pending, so recopy its current source.
        }

        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var manifest = new MirrorPulseConflictCopyManifest(1, conflict.ConflictId, conflict.InstanceId.ToString(),
            side, 0, null, "copying", Path.GetFileName(temporary));
        await WriteManifestAsync(pendingPath, manifest, cancellationToken).ConfigureAwait(false);
        try
        {
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long length = 0;
            await using (Stream source = await openSource(cancellationToken).ConfigureAwait(false))
            await using (var target = new FileStream(
                temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                long? expected = source.CanSeek ? source.Length - source.Position : null;
                byte[] buffer = new byte[81920];
                int count;
                while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                    digest.AppendData(buffer.AsSpan(0, count));
                    length = checked(length + count);
                }
                if (expected is { } expectedLength && length != expectedLength)
                    throw new InvalidDataException("The conflict source changed length while preserving it.");
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(flushToDisk: true);
            }
            manifest = manifest with { Length = length, Sha256 = Convert.ToHexString(digest.GetHashAndReset()), State = "staged" };
            await WriteManifestAsync(pendingPath, manifest, cancellationToken).ConfigureAwait(false);
            _checkpoint?.Invoke(MirrorPulseConflictCopyCheckpoint.StagingFlushed);
            File.Move(temporary, destination, overwrite: false);
            _checkpoint?.Invoke(MirrorPulseConflictCopyCheckpoint.DataCommitted);
            await WriteManifestAsync(manifestPath, manifest with { State = "complete" }, cancellationToken).ConfigureAwait(false);
            _checkpoint?.Invoke(MirrorPulseConflictCopyCheckpoint.ManifestCommitted);
            File.Delete(pendingPath);
            return destination;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task<MirrorPulseConflictCopyManifest> ReadManifestAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) throw new InvalidDataException("The preserved conflict copy has no committed manifest.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return await JsonSerializer.DeserializeAsync<MirrorPulseConflictCopyManifest>(stream, cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("The preserved conflict manifest is empty.");
    }

    private static async Task WriteManifestAsync(string path, MirrorPulseConflictCopyManifest manifest, CancellationToken cancellationToken)
    {
        string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }
            File.Move(staging, path, overwrite: true);
        }
        finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    private async Task VerifyAsync(MirrorPulseConflictRecord conflict, MirrorPulseConflictPreservedSide side,
        string path, MirrorPulseConflictCopyManifest manifest, string state, CancellationToken cancellationToken)
    {
        ValidateIdentity(conflict, side, manifest);
        if (manifest.State != state || manifest.Length < 0 || manifest.Sha256 is null || manifest.Sha256.Length != 64)
            throw new InvalidDataException("The preserved conflict manifest does not match this conflict.");
        EnsureNoReparsePoints(_paths.DataRootPath, path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length != manifest.Length ||
            !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)), manifest.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("The preserved conflict copy failed its content verification.");
    }

    private static void ValidateIdentity(MirrorPulseConflictRecord conflict, MirrorPulseConflictPreservedSide side,
        MirrorPulseConflictCopyManifest manifest)
    {
        if (manifest.SchemaVersion != 1 || manifest.ConflictId != conflict.ConflictId ||
            manifest.InstanceId != conflict.InstanceId.ToString() || manifest.Side != side)
            throw new InvalidDataException("The preserved conflict manifest belongs to another conflict or side.");
    }

    public ValueTask<Stream> OpenLocalAsync(
        MirrorPulseConflictRecord conflict,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(conflict);
        cancellationToken.ThrowIfCancellationRequested();
        string fullPath = Path.GetFullPath(Path.Combine(
            _paths.SyncRootPath,
            conflict.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (string.Equals(fullPath, _paths.SyncRootPath, StringComparison.OrdinalIgnoreCase) ||
            !SourceDirectoryPathNormalizer.IsWithin(_paths.SyncRootPath, fullPath))
        {
            throw new InvalidDataException("The local conflict path escapes the sync root.");
        }

        EnsureNoReparsePoints(_paths.SyncRootPath, fullPath);
        Stream stream = new FileStream(
            fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return ValueTask.FromResult(stream);
    }

    private static void EnsureNoReparsePoints(string root, string target)
    {
        string current = Path.GetFullPath(root);
        string fullTarget = Path.GetFullPath(target);
        if (!SourceDirectoryPathNormalizer.IsWithin(current, fullTarget))
        {
            throw new InvalidDataException("The conflict path escapes its storage root.");
        }

        if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A conflict path cannot traverse a reparse point.");
        }

        string relative = Path.GetRelativePath(current, fullTarget);
        foreach (string part in relative.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
            {
                continue;
            }

            current = Path.Combine(current, part);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("A conflict path cannot traverse a reparse point.");
            }
        }
    }
}
