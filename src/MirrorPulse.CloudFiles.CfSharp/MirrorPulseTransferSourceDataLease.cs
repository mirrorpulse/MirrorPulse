using System.Security.Cryptography;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Retains write access with no write sharing across an original fully resident source operation.</summary>
/// <remarks>The caller separately authenticates binding, parent, role, public identity and namespace ownership.
/// Read/write access rejects existing writable mappings; read-only FileShare.Read is insufficient. Delete
/// sharing permits the public SDK's later local deletion while this same data object remains retained.
/// This lease never exposes write operations, hydrates a source or grants remote deletion/journal authority.</remarks>
internal sealed class MirrorPulseTransferSourceDataLease : IAsyncDisposable
{
    private readonly FileStream _file;
    private MirrorPulseTransferSourceDataLease(FileStream file) => _file = file;

    internal static MirrorPulseTransferSourceDataLease Open(string fullyResidentPath) => new(new(fullyResidentPath,
        FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete, 64 * 1024,
        FileOptions.Asynchronous | FileOptions.SequentialScan));

    internal async Task VerifyAsync(long expectedLength, string expectedSha256, CancellationToken token)
    {
        if (_file.Length != expectedLength) throw new IOException("The guarded source length changed after original transfer admission.");
        _file.Position = 0;
        string hash = Convert.ToHexString(await SHA256.HashDataAsync(_file, token).ConfigureAwait(false));
        if (_file.Position != expectedLength || _file.Length != expectedLength || hash != expectedSha256)
            throw new IOException("The guarded source bytes differ from the original accepted target copy.");
    }

    public ValueTask DisposeAsync() => _file.DisposeAsync();
}
