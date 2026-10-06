using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.RegularExpressions;

namespace MirrorPulse.Core.Contracts;

/// <summary>
/// A canonical package identity: three-part stable/preview or a preserved legacy four-part version.
/// </summary>
public sealed partial class AdapterPackageVersion : IComparable<AdapterPackageVersion>, IEquatable<AdapterPackageVersion>
{
    private AdapterPackageVersion(string value, Version numericVersion, int? previewNumber)
    {
        Value = value;
        NumericVersion = numericVersion;
        PreviewNumber = previewNumber;
    }

    public string Value { get; }

    public Version NumericVersion { get; }

    public int? PreviewNumber { get; }

    public bool IsPreview => PreviewNumber.HasValue;

    public bool IsLegacyFourPart => NumericVersion.Revision >= 0;

    public static AdapterPackageVersion Parse(string value) => TryParse(value, out var version)
        ? version : throw new FormatException("The Adapter package version is not canonical or is outside the supported range.");

    public static bool TryParse(string? value, [NotNullWhen(true)] out AdapterPackageVersion? version)
    {
        version = null;
        if (value is null || value.Length > 64)
        {
            return false;
        }

        Match match = VersionPattern().Match(value);
        if (!match.Success)
        {
            return false;
        }

        Span<int> components = stackalloc int[5];
        for (int index = 0; index < components.Length; index++)
        {
            Group group = match.Groups[index + 1];
            if (group.Success && !int.TryParse(group.ValueSpan, NumberStyles.None,
                    CultureInfo.InvariantCulture, out components[index]))
            {
                return false;
            }
        }

        Version numeric = match.Groups[4].Success
            ? new Version(components[0], components[1], components[2], components[3])
            : new Version(components[0], components[1], components[2]);
        version = new AdapterPackageVersion(value, numeric, match.Groups[5].Success ? components[4] : null);
        return true;
    }

    public int CompareTo(AdapterPackageVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        int numericComparison = NumericVersion.CompareTo(other.NumericVersion);
        if (numericComparison != 0)
        {
            return numericComparison;
        }

        return (PreviewNumber, other.PreviewNumber) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            (int left, int right) => left.CompareTo(right),
        };
    }

    public bool Equals(AdapterPackageVersion? other) => other is not null &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is AdapterPackageVersion other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Value);

    public override string ToString() => Value;

    public static bool operator ==(AdapterPackageVersion? left, AdapterPackageVersion? right) => Equals(left, right);

    public static bool operator !=(AdapterPackageVersion? left, AdapterPackageVersion? right) => !Equals(left, right);

    public static bool operator <(AdapterPackageVersion? left, AdapterPackageVersion? right) =>
        left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator <=(AdapterPackageVersion? left, AdapterPackageVersion? right) =>
        left is null || left.CompareTo(right) <= 0;

    public static bool operator >(AdapterPackageVersion? left, AdapterPackageVersion? right) => right < left;

    public static bool operator >=(AdapterPackageVersion? left, AdapterPackageVersion? right) => right <= left;

    [GeneratedRegex(@"\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.(0|[1-9][0-9]*)|-preview\.([1-9][0-9]*))?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
