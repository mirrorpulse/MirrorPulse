using System.Reflection;

namespace MirrorPulse.Core;

/// <summary>
/// Identifies the product line without coupling the core library to a UI host.
/// </summary>
public static class ProductInfo
{
    public static MirrorPulseEdition Edition { get; } = ReadEdition();

    public static string Name => Edition.DisplayName;

    private static MirrorPulseEdition ReadEdition()
    {
        var metadata = typeof(ProductInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToArray();

        return new MirrorPulseEdition(
            ReadValue("MirrorPulse.EditionId"),
            ReadValue("MirrorPulse.EditionName"),
            Guid.Parse(ReadValue("MirrorPulse.EditionProviderId")));

        string ReadValue(string key) => metadata.SingleOrDefault(attribute => attribute.Key == key)?.Value is { } value
            ? value
            : throw new InvalidOperationException($"The product build is missing its {key} assembly metadata.");
    }
}
