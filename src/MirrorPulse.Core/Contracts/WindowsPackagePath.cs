using System.Text;

namespace MirrorPulse.Core.Contracts;

/// <summary>One canonical relative spelling for Windows package files and manifest resources.</summary>
public static class WindowsPackagePath
{
    public static bool IsCanonical(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.StartsWith('/') ||
            path.Contains('\\') || !path.IsNormalized(NormalizationForm.FormC)) return false;
        foreach (string segment in path.Split('/'))
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".." || segment != segment.Trim() ||
                segment.EndsWith('.') || segment.Any(character => char.IsControl(character) ||
                    character is ':' or '<' or '>' or '"' or '|' or '?' or '*' or '~')) return false;
            string stem = segment.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" or "CLOCK$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9' or '¹' or '²' or '³'))
                return false;
        }
        return true;
    }
}
