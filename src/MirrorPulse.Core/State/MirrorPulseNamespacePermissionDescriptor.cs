using System.Security.AccessControl;

namespace MirrorPulse.Core.State;

/// <summary>Recognizes the bounded descriptor normalization made by a Windows DACL write.</summary>
/// <remarks>
/// Windows can add the auto-inheritance completion flag even when the original descriptor
/// was captured at protected object creation. No ACE, protection flag or other control flag
/// may change. Keep the requested original and actual read-back as separate immutable facts.
/// This comparison does not authorize an operation or validate a local object binding.
/// </remarks>
public static class MirrorPulseNamespacePermissionDescriptor
{
    public static bool MatchesNativeReadback(string requestedDacl, string observedDacl)
    {
        ArgumentNullException.ThrowIfNull(requestedDacl);
        ArgumentNullException.ThrowIfNull(observedDacl);
        if (requestedDacl.Length is 0 or > 65_536 || observedDacl.Length is 0 or > 65_536)
            throw new ArgumentException("The permission read-back descriptors must be bounded.");
        var descriptor = new RawSecurityDescriptor(requestedDacl);
        if (descriptor.Owner is not null || descriptor.Group is not null || descriptor.SystemAcl is not null ||
            descriptor.DiscretionaryAcl is null || descriptor.GetSddlForm(AccessControlSections.Access) != requestedDacl)
            throw new ArgumentException("Permission read-back requires a canonical access descriptor.", nameof(requestedDacl));
        if (requestedDacl == observedDacl) return true;
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        return descriptor.GetSddlForm(AccessControlSections.Access) == observedDacl;
    }
}
