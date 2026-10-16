using System.Security.AccessControl;
using System.Security.Principal;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private static void RemoveKnownRegisteredFixturePlaceholdersForCleanup(string directory, string syncRoot,
        SecurityIdentifier user, IReadOnlyList<string> directories, IReadOnlyList<string> files)
    {
        if (!Directory.Exists(syncRoot)) return;
        if (new DirectoryInfo(directory).LinkTarget is not null || new DirectoryInfo(syncRoot).LinkTarget is not null)
            throw new InvalidOperationException("Registered fixture cleanup cannot follow its workspace or sync-root link.");
        // Authenticate the original GUID fixture and marker before changing any permissions.
        // Remove only its explicitly listed synthetic children while Cloud Files still knows
        // their sync root. Unregistering first can invalidate placeholder name/security queries.
        RestoreRootGuardFixtureForCleanup(directory, syncRoot, user);
        if (directories.Count > 32 || files.Count > 32)
            throw new InvalidOperationException("Registered fixture cleanup exceeds its fixed inventory bound.");
        string root = Path.GetFullPath(syncRoot), prefix = root + Path.DirectorySeparatorChar;
        string Resolve(string relative)
        {
            if (Path.IsPathRooted(relative) || relative.Split('/').Any(segment => segment is "" or "." or "..") ||
                relative.Contains('\\') || relative.Contains(':'))
                throw new InvalidOperationException("Registered fixture cleanup requires an explicit relative child path.");
            string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Registered fixture cleanup escaped its authenticated sync root.");
            for (string? ancestor = Path.GetDirectoryName(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            {
                if (new DirectoryInfo(ancestor).LinkTarget is not null)
                    throw new InvalidOperationException("Registered fixture cleanup cannot traverse a directory link.");
                if (string.Equals(ancestor, root, StringComparison.OrdinalIgnoreCase)) return path;
            }
            throw new InvalidOperationException("Registered fixture cleanup lost its authenticated ancestor.");
        }
        var folders = directories.Select(Resolve).OrderBy(path => path.Length).ToArray();
        foreach (string path in folders)
        {
            var folder = new DirectoryInfo(path); if (!folder.Exists) continue;
            if (folder.LinkTarget is not null) throw new InvalidOperationException("Registered fixture cleanup cannot follow a directory link.");
            var access = new DirectorySecurity(); access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            folder.SetAccessControl(access);
        }
        foreach (string path in files.Select(Resolve))
        {
            var file = new FileInfo(path); if (!file.Exists) continue;
            if (file.LinkTarget is not null) throw new InvalidOperationException("Registered fixture cleanup cannot follow a file link.");
            var access = new FileSecurity(); access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            file.SetAccessControl(access);
            File.Delete(path); // Deleting the known synthetic placeholder never reads its cold content.
        }
        foreach (string path in folders.Reverse())
            if (Directory.Exists(path)) Directory.Delete(path, recursive: false);
    }
}
