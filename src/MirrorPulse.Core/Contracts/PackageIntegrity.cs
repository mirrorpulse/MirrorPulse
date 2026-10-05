using System.Collections.ObjectModel;
using System.Security.Cryptography;

namespace MirrorPulse.Core.Contracts;

/// <summary>
/// Canonical uppercase SHA-256 digest for an Adapter package file.
/// </summary>
public readonly record struct Sha256Digest
{
    public Sha256Digest(string hexadecimal)
    {
        if (!TryNormalize(hexadecimal, out var normalized))
        {
            throw new ArgumentException("A SHA-256 digest must contain exactly 64 hexadecimal characters.", nameof(hexadecimal));
        }

        Hexadecimal = normalized;
    }

    public string Hexadecimal { get; }

    public static Sha256Digest Compute(ReadOnlySpan<byte> content) =>
        new(Convert.ToHexString(SHA256.HashData(content)));

    public static Sha256Digest Parse(string hexadecimal) => new(hexadecimal);

    public static bool TryParse(string? hexadecimal, out Sha256Digest digest)
    {
        if (TryNormalize(hexadecimal, out var normalized))
        {
            digest = new Sha256Digest(normalized);
            return true;
        }

        digest = default;
        return false;
    }

    public override string ToString() => Hexadecimal;

    private static bool TryNormalize(string? hexadecimal, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(hexadecimal) || hexadecimal.Length != 64)
        {
            return false;
        }

        foreach (var character in hexadecimal)
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        normalized = hexadecimal.ToUpperInvariant();
        return true;
    }
}

/// <summary>
/// Hash and size metadata for one relative file in an .mpadapter package.
/// </summary>
public sealed record PackageFileEntry
{
    public PackageFileEntry(string path, long length, Sha256Digest sha256)
    {
        if (!WindowsPackagePath.IsCanonical(path))
        {
            throw new ArgumentException("Package file paths must be safe relative paths.", nameof(path));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(length);

        Path = path;
        Length = length;
        Sha256 = sha256;
    }

    public string Path { get; }

    public long Length { get; }

    public Sha256Digest Sha256 { get; }

}

/// <summary>
/// Complete package file inventory covered by the Adapter signature.
/// </summary>
public sealed record PackageFileManifest
{
    public const string HashAlgorithm = "SHA-256";

    public PackageFileManifest(IEnumerable<PackageFileEntry> files)
    {
        ArgumentNullException.ThrowIfNull(files);
        var entries = files.ToArray();
        if (entries.GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            throw new ArgumentException("A package file manifest cannot contain duplicate paths.", nameof(files));
        }

        var paths = entries.Select(file => file.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            for (int index = path.IndexOf('/'); index >= 0; index = path.IndexOf('/', index + 1))
            {
                if (paths.Contains(path[..index]))
                    throw new ArgumentException("Package files cannot alias a parent directory.", nameof(files));
            }
        }

        Files = new ReadOnlyCollection<PackageFileEntry>(entries);
    }

    public IReadOnlyList<PackageFileEntry> Files { get; }
}
