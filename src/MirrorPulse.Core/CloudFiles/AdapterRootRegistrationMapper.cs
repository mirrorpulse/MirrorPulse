using System.Security.Cryptography;
using System.Text;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.CloudFiles;

/// <summary>
/// Converts Adapter-declared first-level roots into MP registrations for the unified Sync Root.
/// </summary>
public static class AdapterRootRegistrationMapper
{
    public static RootRegistration Map(
        AdapterId adapterId,
        InstanceId instanceId,
        AdapterRootDefinition definition,
        RootRegistrationState state = RootRegistrationState.Pending,
        DateTimeOffset? registeredAt = null,
        RootIdentityScope identityScope = RootIdentityScope.LegacyInstance)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var rootId = new RootId(CreateRootId(instanceId, definition.Key));
        return new RootRegistration(
            adapterId,
            instanceId,
            rootId,
            definition.Key,
            definition.Label,
            definition.DirectoryName,
            definition.CustomEntry,
            state,
            registeredAt ?? DateTimeOffset.UtcNow, identityScope);
    }

    public static IReadOnlyList<RootRegistration> MapAll(
        AdapterId adapterId,
        InstanceId instanceId,
        IEnumerable<AdapterRootDefinition> definitions,
        RootRegistrationState state = RootRegistrationState.Pending,
        DateTimeOffset? registeredAt = null,
        RootIdentityScope identityScope = RootIdentityScope.LegacyInstance)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        var registrations = definitions
            .Select(definition => Map(adapterId, instanceId, definition, state, registeredAt, identityScope))
            .ToArray();
        var duplicate = registrations
            .GroupBy(registration => registration.UniquenessKey, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException($"The Adapter declared duplicate root key '{duplicate.Key}'.");
        }

        return registrations;
    }

    private static Guid CreateRootId(InstanceId instanceId, string rootKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"MirrorPulse/root/{instanceId}/{rootKey}"));
        var id = new Guid(digest.AsSpan(0, 16));
        return id == Guid.Empty ? new Guid(digest.AsSpan(16, 16)) : id;
    }
}
