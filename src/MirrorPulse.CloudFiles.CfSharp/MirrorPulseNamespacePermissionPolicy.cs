using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Produces explicit per-object ACLs for the current user's namespace session.</summary>
[SupportedOSPlatform("windows10.0.26100")]
public static class MirrorPulseNamespacePermissionPolicy
{
    /// <summary>Preserves ordinary content access and reserves namespace changes for the session role.</summary>
    /// <remarks>
    /// Each object has its own protected descriptor. Creation must supply that descriptor at
    /// creation time. Existing descendants must be captured and protected before the parent
    /// DACL changes; this method alone does not make a tree safe or authorize an operation.
    /// </remarks>
    public static string CreateProtectedDacl(MirrorPulseNamespaceExecutionSession session, bool isDirectory)
    {
        ArgumentNullException.ThrowIfNull(session);
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        FileSystemRights ordinary = isDirectory
            ? FileSystemRights.ReadAndExecute | FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes
            : FileSystemRights.Read | FileSystemRights.Write;
        security.AddAccessRule(new FileSystemAccessRule(session.OwnerSid, ordinary, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(session.RoleSid, FileSystemRights.FullControl, AccessControlType.Allow));
        // FileSystemSecurity supplies canonical ACE ordering. Windows records auto-inheritance
        // completion even on a protected DACL; retain that expected flag before the native write.
        var canonical = new RawSecurityDescriptor(security.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        canonical.SetFlags(canonical.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        return canonical.GetSddlForm(AccessControlSections.Access);
    }
}
