using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace MirrorPulse.CloudFiles.CfSharp;

/// <summary>Produces owned ACLs with safe child inheritance for the current user's namespace session.</summary>
[SupportedOSPlatform("windows10.0.26100")]
public static class MirrorPulseNamespacePermissionPolicy
{
    /// <summary>Preserves ordinary content access and reserves namespace changes for the session role.</summary>
    /// <remarks>
    /// Directories grant inherited content access without ordinary namespace rights. Cloud Files
    /// placeholder creation relies on those inherited entries. Controlled creation supplies its
    /// descriptor explicitly. Existing descendants must be captured and protected before the
    /// parent changes; this method alone does not make a tree safe or authorize an operation.
    /// </remarks>
    public static string CreateProtectedDacl(MirrorPulseNamespaceExecutionSession session, bool isDirectory) =>
        CreateDacl(session, isDirectory, forCreation: false);

    /// <summary>Creates the descriptor supplied atomically when a new protected object is created.</summary>
    /// <remarks>Creation and a later permission write have different native auto-inheritance flags.</remarks>
    public static string CreateNewObjectDacl(MirrorPulseNamespaceExecutionSession session, bool isDirectory) =>
        CreateDacl(session, isDirectory, forCreation: true);

    private static string CreateDacl(MirrorPulseNamespaceExecutionSession session, bool isDirectory, bool forCreation)
    {
        ArgumentNullException.ThrowIfNull(session);
        FileSystemSecurity security = isDirectory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        if (isDirectory)
        {
            security.AddAccessRule(new FileSystemAccessRule(session.OwnerSid, FileSystemRights.ReadAndExecute |
                FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes,
                InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(session.OwnerSid, FileSystemRights.Read | FileSystemRights.Write,
                InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(session.RoleSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        }
        else
        {
            security.AddAccessRule(new FileSystemAccessRule(session.OwnerSid, FileSystemRights.Read | FileSystemRights.Write, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(session.RoleSid, FileSystemRights.FullControl, AccessControlType.Allow));
        }
        // FileSystemSecurity supplies canonical ACE ordering. Windows records auto-inheritance
        // completion even on a protected DACL; retain that expected flag before the native write.
        var canonical = new RawSecurityDescriptor(security.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        if (!forCreation) canonical.SetFlags(canonical.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        return canonical.GetSddlForm(AccessControlSections.Access);
    }
}
