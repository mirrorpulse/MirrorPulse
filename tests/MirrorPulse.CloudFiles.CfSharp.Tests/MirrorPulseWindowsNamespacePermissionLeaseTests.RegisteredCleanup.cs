using System.Security.AccessControl;
using System.Security.Principal;
using MirrorPulse.Core.Configuration;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseWindowsNamespacePermissionLeaseTests
{
    private async Task RemoveKnownRegisteredFixturePlaceholdersForCleanupAsync(string directory, MirrorPulseStoragePaths paths,
        SecurityIdentifier user, string[] directories, string[] files)
    {
        string syncRoot = paths.SyncRootPath;
        if (!Directory.Exists(syncRoot)) return;
        if (new DirectoryInfo(directory).LinkTarget is not null || new DirectoryInfo(syncRoot).LinkTarget is not null ||
            new DirectoryInfo(paths.DataRootPath).LinkTarget is not null ||
            !string.Equals(Path.GetFullPath(paths.DataRootPath), Path.GetFullPath(Path.Combine(directory, "data")), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Registered fixture cleanup cannot follow its workspace or sync-root link.");
        // Authenticate the original GUID fixture and marker before changing any permissions.
        // Registration alone does not keep a provider connected after the test's SDK owners
        // have drained. Reconnect with the public SDK before restoring or deleting cold
        // placeholders; no source content is available to this cleanup session.
        RestoreRootGuardFixtureForCleanup(directory, syncRoot, user);
        if (directories.Length > 32 || files.Length > 32)
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
        var children = files.Select(Resolve).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var cleanupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var source = new ProtectedPermissionNoSourceProvider();
        await using (var cleanup = new MirrorPulseCloudFileSystemBuilder(paths)
            .WithStateStore(new MirrorPulseCfSharpStateSession(paths)).WithContentProvider(source).Build())
        {
            await cleanup.StartAsync(cleanupDeadline.Token);
            foreach (string path in folders)
            {
                var folder = new DirectoryInfo(path); if (!folder.Exists) continue;
                if (folder.LinkTarget is not null) throw new InvalidOperationException("Registered fixture cleanup cannot follow a directory link.");
                var access = new DirectorySecurity(); access.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                access.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                folder.SetAccessControl(access);
            }
            foreach (string path in children)
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
        Assert.AreEqual(0, source.Reads);
        TestContext.WriteLine("RegisteredFixtureCleanup: authenticatedInventory=True; publicSdkConnected=True; sourceReads=0; unregisterAfterSdkDisposal=True.");
    }
}
