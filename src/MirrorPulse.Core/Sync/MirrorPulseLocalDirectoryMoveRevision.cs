namespace MirrorPulse.Core.Sync;

/// <summary>Compares the native object generation carried by Local revisions without accepting a later content revision.</summary>
internal static class MirrorPulseLocalDirectoryMoveRevision
{
    internal static bool IsSameObject(string? original, string? observed, bool isDirectory)
    {
        ReadOnlySpan<char> expected = Generation(original, isDirectory);
        return !expected.IsEmpty && expected.SequenceEqual(Generation(observed, isDirectory));
    }

    private static ReadOnlySpan<char> Generation(string? revision, bool isDirectory)
    {
        const string prefix = "native-v2:";
        const int volumeLength = 16;
        const int fileIdLength = 32;
        int volumeStart = prefix.Length;
        int fileIdStart = volumeStart + volumeLength + 1;
        int generationLength = fileIdStart + fileIdLength;
        ReadOnlySpan<char> value = revision.AsSpan();
        ReadOnlySpan<char> kind = isDirectory ? ":directory:" : ":file:";
        if (value.Length <= generationLength + kind.Length || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value[volumeStart + volumeLength] != ':' || !value[generationLength..].StartsWith(kind, StringComparison.Ordinal))
            return default;
        foreach (char digit in value.Slice(volumeStart, volumeLength))
            if (!Uri.IsHexDigit(digit)) return default;
        if (!Guid.TryParseExact(value.Slice(fileIdStart, fileIdLength), "N", out var fileId) || fileId == Guid.Empty)
            return default;
        return value[..generationLength];
    }
}
