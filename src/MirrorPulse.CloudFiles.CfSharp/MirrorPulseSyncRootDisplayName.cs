using MirrorPulse.Core;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Selects the Shell name for the one MP sync root from installed instance registrations.</summary>
public static class MirrorPulseSyncRootDisplayName
{
    public static string Resolve(
        IEnumerable<AdapterInstance> instances,
        IEnumerable<RootRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(instances);
        ArgumentNullException.ThrowIfNull(registrations);

        AdapterInstance[] current = instances.ToArray();
        RootRegistration[] visible = registrations
            .Where(root => root.State is RootRegistrationState.Active or RootRegistrationState.Disabled)
            .ToArray();
        if (current.Length == 1 && visible.Length > 0 &&
            visible.All(root => root.InstanceId == current[0].InstanceId && root.CustomEntry))
        {
            return current[0].DisplayName;
        }

        return ProductInfo.Name;
    }
}
