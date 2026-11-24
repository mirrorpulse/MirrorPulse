using System.Text.RegularExpressions;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core;

/// <summary>The stable identity and display name shared by a product edition's processes.</summary>
public sealed partial class MirrorPulseEdition
{
    private static readonly Guid StandardProviderId = new("d194eacd-c38c-49df-a238-49871f9c8d1b");

    internal MirrorPulseEdition(string id, string displayName, Guid providerId,
        string defaultAdapterIds = "", bool directAdapter = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        if (id.Length > 48 || !EditionIdPattern().IsMatch(id))
        {
            throw new ArgumentException("The edition requires a stable lowercase identifier.", nameof(id));
        }

        bool isStandard = id == "standard";
        if (providerId == Guid.Empty)
        {
            throw new ArgumentException("The edition's Cloud Files provider ID must be nonempty.", nameof(providerId));
        }

        if ((providerId == StandardProviderId) != isStandard)
        {
            throw new ArgumentException("The standard Cloud Files provider ID is reserved for the standard edition.", nameof(providerId));
        }

        AdapterId[] adapters = defaultAdapterIds.Length == 0 ? [] : defaultAdapterIds.Split(',').Select(value =>
        {
            if (!AdapterId.TryParse(value, out var adapterId) || adapterId.Value != value)
                throw new ArgumentException("Default Adapter IDs must be canonical.", nameof(defaultAdapterIds));
            return adapterId;
        }).ToArray();
        if (adapters.Distinct().Count() != adapters.Length || isStandard && adapters.Length != 0)
            throw new ArgumentException("Only custom editions can bundle unique Adapter IDs.", nameof(defaultAdapterIds));
        if (directAdapter && adapters.Length != 1)
            throw new ArgumentException("Direct presentation requires exactly one bundled Adapter.", nameof(directAdapter));

        Id = id;
        DisplayName = displayName.Trim();
        ProviderId = providerId;
        IdentityPrefix = isStandard ? "MirrorPulse" : $"MirrorPulse.{id}";
        EnvironmentPrefix = isStandard ? "MIRRORPULSE_" : $"MIRRORPULSE_EDITION_{id.Replace('-', '_').ToUpperInvariant()}_";
        DefaultAdapterIds = Array.AsReadOnly(adapters);
        DirectAdapterId = directAdapter ? adapters[0] : null;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public Guid ProviderId { get; }

    public string IdentityPrefix { get; }

    public string EnvironmentPrefix { get; }

    public IReadOnlyList<AdapterId> DefaultAdapterIds { get; }

    public AdapterId? DirectAdapterId { get; }

    [GeneratedRegex(@"\A[a-z][a-z0-9]*(-[a-z0-9]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex EditionIdPattern();
}
