using System.Security.Cryptography;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Caching;

/// <summary>Retains exact import input below the Host's private data root until product completion.</summary>
/// <remarks>
/// The Host calls input staging in its normal user context before namespace execution. An input
/// digest proves only staged bytes, never native identity, protection or remote acceptance.
/// Pending inputs survive Host restart and are separate from disposable Adapter transfer caches.
/// </remarks>
public sealed class MirrorPulseLocalImportStagingStore
{
    private readonly string _directory;

    public MirrorPulseLocalImportStagingStore(MirrorPulseStoragePaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _directory = Path.Combine(paths.DataRootPath, "state", "import-inputs");
    }

    /// <summary>Copies and flushes input once; an operation ID can never replace different staged bytes.</summary>
    public async Task<MirrorPulseNamespaceImportContent> StageAsync(Guid operationId, string sourcePath,
        CancellationToken cancellationToken = default)
    {
        string target = InputPath(operationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        if (!Path.IsPathFullyQualified(sourcePath))
            throw new ArgumentException("Import input requires a caller-resolved absolute path.", nameof(sourcePath));
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_directory);
        string temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            MirrorPulseNamespaceImportContent content;
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[64 * 1024];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                }
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
                content = new(output.Length, Convert.ToHexString(hash.GetHashAndReset()));
            }
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Move(temporary, target, overwrite: false); }
            catch (IOException) when (File.Exists(target))
            {
                // Concurrent or repeated preparation may agree, but it cannot overwrite.
                await using var retained = await OpenVerifiedAsync(operationId, content, cancellationToken).ConfigureAwait(false);
            }
            return content;
        }
        finally { File.Delete(temporary); }
    }

    /// <summary>Revalidates retained input and holds it against writers and deletion through local copying.</summary>
    public async Task<FileStream> OpenVerifiedAsync(Guid operationId, MirrorPulseNamespaceImportContent content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length < 0 || content.Sha256 is not { Length: 64 } ||
            content.Sha256.Any(character => !char.IsAsciiHexDigitUpper(character)))
            throw new ArgumentException("Import input requires its original exact length and SHA256 digest.", nameof(content));
        cancellationToken.ThrowIfCancellationRequested();
        var input = new FileStream(InputPath(operationId), FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (input.Length != content.Length ||
                Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false)) != content.Sha256)
                throw new InvalidDataException("Retained import input no longer matches its original content plan.");
            input.Position = 0;
            return input;
        }
        catch { await input.DisposeAsync().ConfigureAwait(false); throw; }
    }

    /// <summary>Opens an operation-bound private input for the Host's local namespace executor.</summary>
    public async Task<MirrorPulseLocalImportInputLease> OpenLeaseAsync(Guid operationId, MirrorPulseNamespaceImportContent content,
        CancellationToken cancellationToken = default) =>
        new(operationId, content, await OpenVerifiedAsync(operationId, content, cancellationToken).ConfigureAwait(false));

    /// <summary>Removes only this operation's private input after the Host has completed the product operation.</summary>
    /// <remarks>Never call for an incomplete native operation; its input is needed for recovery.</remarks>
    public void RemoveCompleted(Guid operationId) => File.Delete(InputPath(operationId));

    private string InputPath(Guid operationId)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("A stable import operation ID is required.", nameof(operationId));
        return Path.Combine(_directory, operationId.ToString("N") + ".bin");
    }
}

/// <summary>A verified private input kept against writers and deletion until the Host finishes copying.</summary>
/// <remarks>Only the staging store creates this lease. It does not authorize a native target write.</remarks>
public sealed class MirrorPulseLocalImportInputLease : IAsyncDisposable
{
    internal MirrorPulseLocalImportInputLease(Guid operationId, MirrorPulseNamespaceImportContent content, FileStream data)
    { OperationId = operationId; Content = content; Data = data; }

    public Guid OperationId { get; }
    public MirrorPulseNamespaceImportContent Content { get; }
    public FileStream Data { get; }
    public ValueTask DisposeAsync() => Data.DisposeAsync();
}
