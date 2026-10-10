using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using MirrorPulse.CloudFiles.CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespacePermissionPolicyTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    public async Task WindowsInheritanceMatchesActualBirthRotationAndRestorationWithoutChildAclWrites()
    {
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-explicit-acl-tests", Guid.NewGuid().ToString("N"));
        string sync = Path.Combine(fixture, "sync");
        string docs = Path.Combine(sync, "Docs");
        string file = Path.Combine(docs, "born.txt");
        string child = Path.Combine(docs, "born-dir");
        string nested = Path.Combine(child, "nested.txt");
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var first = new MirrorPulseNamespaceExecutionSession();
        await using var next = new MirrorPulseNamespaceExecutionSession();
        try
        {
            Directory.CreateDirectory(docs);
            await File.WriteAllTextAsync(Path.Combine(fixture, ".mp-explicit-acl-fixture"), "synthetic");
            string originalSync = ReadDacl(sync, true);
            string originalDocs = ReadDacl(docs, true);
            string parentDacl = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(first, true);
            SetDirectoryAcl(sync, parentDacl);
            SetDirectoryAcl(docs, parentDacl);
            await first.RunNamespaceOperationAsync(async () =>
            {
                await File.WriteAllTextAsync(file, "first local bytes");
                Directory.CreateDirectory(child);
                await File.WriteAllTextAsync(nested, "nested local bytes");
            });
            AssertInheritance(parentDacl);
            await File.AppendAllTextAsync(file, " latest");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(file)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => Directory.Delete(child, true)));
            Assert.AreEqual("first local bytes latest", await File.ReadAllTextAsync(file));
            Assert.AreEqual("nested local bytes", await File.ReadAllTextAsync(nested));
            string firstBirthDacl = ReadDacl(file, false);
            string rotatedParent = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(next, true);
            await next.RunNamespaceOperationAsync(() =>
            {
                SetDirectoryAcl(sync, rotatedParent);
                SetDirectoryAcl(docs, rotatedParent);
                return Task.CompletedTask;
            });
            AssertInheritance(rotatedParent);
            Assert.IsFalse(ReadDacl(file, false).Contains(first.RoleSid.Value, StringComparison.Ordinal));
            Assert.IsTrue(ReadDacl(file, false).Contains(next.RoleSid.Value, StringComparison.Ordinal));
            Assert.AreNotEqual(firstBirthDacl, ReadDacl(file, false));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Move(file, file + ".renamed")));
            await File.AppendAllTextAsync(nested, " latest");
            await next.RunNamespaceOperationAsync(() =>
            {
                // Restore only the captured original parents, never a child's birth descriptor.
                SetDirectoryAcl(sync, originalSync);
                SetDirectoryAcl(docs, originalDocs);
                return Task.CompletedTask;
            });
            AssertInheritance(ReadDacl(docs, true));
            foreach (var subject in new[] { (file, false), (child, true), (nested, false) })
            {
                string actual = ReadDacl(subject.Item1, subject.Item2);
                Assert.IsFalse(actual.Contains(first.RoleSid.Value, StringComparison.Ordinal));
                Assert.IsFalse(actual.Contains(next.RoleSid.Value, StringComparison.Ordinal));
            }
            Assert.AreEqual("nested local bytes latest", await File.ReadAllTextAsync(nested));
            File.Delete(file);
            Directory.Delete(child, true);
            TestContext.WriteLine("InheritedBirth: actualNtfs=True; parentOnlyRotation=True; parentOnlyRestoration=True; nestedChild=True; childAclWrites=0; latestBytesRetained=True; cloudRootRegistered=False.");

            void AssertInheritance(string parent)
            {
                Assert.AreEqual(MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(parent, false), ReadDacl(file, false));
                Assert.AreEqual(MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(parent, true), ReadDacl(child, true));
                Assert.AreEqual(MirrorPulseWindowsNamespaceInheritance.CreateInheritedDacl(ReadDacl(child, true), false), ReadDacl(nested, false));
            }
        }
        finally { DeleteMarkedFixture(fixture, caller.User!); }
    }

    private static string ReadDacl(string path, bool directory) => directory
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    [TestMethod]
    [DoNotParallelize]
    public async Task ExplicitPolicyMatchesNtfsAndControlledCreationIsProtectedAtBirth()
    {
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string fixture = Path.Combine(Path.GetTempPath(), "MirrorPulse-explicit-acl-tests", Guid.NewGuid().ToString("N"));
        string sync = Path.Combine(fixture, "sync");
        string docs = Path.Combine(sync, "Docs");
        string ordinary = Path.Combine(docs, "unsent.txt");
        string controlled = Path.Combine(docs, "controlled.txt");
        string outside = Path.Combine(fixture, "outside.txt");
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        try
        {
            Directory.CreateDirectory(docs);
            await File.WriteAllTextAsync(Path.Combine(fixture, ".mp-explicit-acl-fixture"), "synthetic");
            await File.WriteAllTextAsync(ordinary, "original unsent");
            await File.WriteAllTextAsync(outside, "outside synthetic bytes");
            string fileTarget = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, isDirectory: false);
            string creationTarget = MirrorPulseNamespacePermissionPolicy.CreateNewObjectDacl(role, isDirectory: false);
            string directoryTarget = MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(role, isDirectory: true);
            SetFileAcl(ordinary, fileTarget);
            SetDirectoryAcl(docs, directoryTarget);
            SetDirectoryAcl(sync, directoryTarget);
            Assert.AreEqual(fileTarget, new FileInfo(ordinary).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.AreEqual(directoryTarget, new DirectoryInfo(docs).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.AreEqual(directoryTarget, new DirectoryInfo(sync).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            await File.AppendAllTextAsync(ordinary, " latest");
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(ordinary)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Move(ordinary, ordinary + ".renamed")));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => Directory.Delete(docs, recursive: true)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.WriteAllTextAsync(controlled, "uncontrolled"));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Move(outside, Path.Combine(docs, "imported.txt"))));
            Assert.AreEqual("outside synthetic bytes", await File.ReadAllTextAsync(outside));
            Assert.AreEqual("original unsent latest", await File.ReadAllTextAsync(ordinary));
            await role.RunNamespaceOperationAsync(async () =>
            {
                var atCreation = new FileSecurity();
                atCreation.SetSecurityDescriptorSddlForm(creationTarget, AccessControlSections.Access);
                using FileStream stream = new FileInfo(controlled).Create(FileMode.CreateNew, FileSystemRights.Write,
                    FileShare.Read, 4096, FileOptions.None, atCreation);
                // The descriptor is already present while the first writer remains open.
                Assert.AreEqual(creationTarget, new FileInfo(controlled).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
                await stream.WriteAsync(Encoding.UTF8.GetBytes("controlled synthetic bytes"));
                await stream.FlushAsync();
            });
            using (WindowsIdentity restored = WindowsIdentity.GetCurrent())
                Assert.IsFalse(new WindowsPrincipal(restored).IsInRole(role.RoleSid));
            Assert.AreEqual("controlled synthetic bytes", await File.ReadAllTextAsync(controlled));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(controlled)));
            string renamed = controlled + ".renamed";
            await role.RunNamespaceOperationAsync(() => { File.Move(controlled, renamed); return Task.CompletedTask; });
            Assert.AreEqual(creationTarget, new FileInfo(renamed).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(renamed)));
            await role.RunNamespaceOperationAsync(() => { File.Delete(renamed); return Task.CompletedTask; });
            // CFAPI placeholder creation cannot accept an explicit file descriptor. Its parent
            // must therefore also make ordinary inherited creation safe without a later ACL write.
            string inherited = Path.Combine(docs, "inherited.txt");
            await role.RunNamespaceOperationAsync(() => File.WriteAllTextAsync(inherited, "inherited synthetic bytes"));
            Assert.AreEqual("inherited synthetic bytes", await File.ReadAllTextAsync(inherited));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(inherited)));
            await role.RunNamespaceOperationAsync(() => { File.Delete(inherited); return Task.CompletedTask; });
            Assert.AreEqual("original unsent latest", await File.ReadAllTextAsync(ordinary));
            TestContext.WriteLine($"ExplicitPolicyNtfs: architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}; exactDirectoryAndFileDacl=True; ordinaryNamespaceDenied=True; latestBytesRetained=True; controlledCreationProtectedAtBirth=True; callerRestored=True; callerElevated={new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator)}; cloudRootRegistered=False; personalDataUsed=False.");
        }
        finally { DeleteMarkedFixture(fixture, caller.User!); }
    }

    private static void SetFileAcl(string path, string dacl)
    {
        var security = new FileSecurity();
        security.SetSecurityDescriptorSddlForm(dacl, AccessControlSections.Access);
        new FileInfo(path).SetAccessControl(security);
    }

    private static void SetDirectoryAcl(string path, string dacl)
    {
        var security = new DirectorySecurity();
        security.SetSecurityDescriptorSddlForm(dacl, AccessControlSections.Access);
        new DirectoryInfo(path).SetAccessControl(security);
    }

    private static void DeleteMarkedFixture(string fixture, SecurityIdentifier owner)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-explicit-acl-tests")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(fixture).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(fixture), "N", out _))
            throw new InvalidOperationException("The explicit ACL fixture cleanup target is invalid.");
        if (!Directory.Exists(fixture)) return;
        if (!File.Exists(Path.Combine(fixture, ".mp-explicit-acl-fixture")))
            throw new InvalidOperationException("The explicit ACL fixture marker is missing.");
        foreach (string path in new[] { fixture }.Concat(Directory.EnumerateDirectories(fixture, "*", SearchOption.AllDirectories)))
        {
            var directorySecurity = new DirectorySecurity();
            directorySecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            directorySecurity.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(path).SetAccessControl(directorySecurity);
        }
        foreach (string path in Directory.EnumerateFiles(fixture, "*", SearchOption.AllDirectories))
        {
            // Persistence clears a security object's modified flags. A descriptor instance
            // cannot be reused to reset the next object's ACL without marking it modified.
            var fileSecurity = new FileSecurity();
            fileSecurity.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            fileSecurity.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(path).SetAccessControl(fileSecurity);
        }
        Directory.Delete(fixture, recursive: true);
    }
}
