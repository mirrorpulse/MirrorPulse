using System.Security.Cryptography;
using MirrorPulse.Core.State;

namespace MirrorPulse.Core.Conflicts;

/// <summary>Holds the complete original local copy against replacement or writes during a conflict decision.</summary>
public sealed class MirrorPulseConflictLocalCopyLease : IAsyncDisposable
{
    internal MirrorPulseConflictLocalCopyLease(string path, MirrorPulseConflictKeepBothCopy proof, FileStream data)
    { Path = path; Proof = proof; Data = data; }
    public string Path { get; }
    public MirrorPulseConflictKeepBothCopy Proof { get; }
    public FileStream Data { get; }
    public ValueTask DisposeAsync() => Data.DisposeAsync();
}

public sealed partial class MirrorPulseConflictCopyStore
{
    /// <summary>Opens only the command's committed local copy, matching its originally captured complete byte proof.</summary>
    public async Task<MirrorPulseConflictLocalCopyLease> OpenLocalDecisionAsync(Guid commandId, MirrorPulseConflictRecord conflict,
        MirrorPulseConflictKeepBothCopy expected, CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("The original preservation command is required.", nameof(commandId));
        ArgumentNullException.ThrowIfNull(conflict); ArgumentNullException.ThrowIfNull(expected);
        if (expected.Length < 0 || expected.Sha256 is not { Length: 64 } || expected.Sha256.Any(character => !char.IsAsciiHexDigitUpper(character)))
            throw new ArgumentException("A complete canonical local byte proof is required.", nameof(expected));
        string path = Path.Combine(MirrorPulseConflictDirectory.GetPath(_paths, conflict.InstanceId), "local", commandId.ToString("D"),
            MirrorPulseConflictFileName.Create(conflict));
        EnsureNoReparsePoints(_paths.DataRootPath, path);
        EnsureNoReparsePoints(_paths.DataRootPath, path + ".manifest.json");
        var manifest = await ReadManifestAsync(path + ".manifest.json", cancellationToken).ConfigureAwait(false);
        ValidateIdentity(conflict, MirrorPulseConflictPreservedSide.Local, commandId, null, manifest);
        if (manifest.State != "complete" || manifest.Length != expected.Length || manifest.Sha256 != expected.Sha256)
            throw new InvalidDataException("The local decision copy lost its complete original byte proof.");
        var data = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        try
        {
            if (data.Length != expected.Length || Convert.ToHexString(await SHA256.HashDataAsync(data, cancellationToken).ConfigureAwait(false)) != expected.Sha256)
                throw new InvalidDataException("The retained local copy failed its complete byte proof.");
            data.Position = 0;
            return new(path, expected, data);
        }
        catch { await data.DisposeAsync().ConfigureAwait(false); throw; }
    }
}
