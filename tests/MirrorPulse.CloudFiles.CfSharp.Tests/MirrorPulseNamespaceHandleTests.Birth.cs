using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using MirrorPulse.CloudFiles.CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceHandleTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task RoleOnlyOrdinaryBirthRejectsNormalAliasesBeforeAnyCloudConversion()
    {
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-role-birth-tests")) + Path.DirectorySeparatorChar;
        string fixture = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        string file = Path.Combine(fixture, "born.bin"), alias = Path.Combine(fixture, "outside.link");
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var role = new MirrorPulseNamespaceExecutionSession();
        try
        {
            Directory.CreateDirectory(fixture);
            await File.WriteAllTextAsync(Path.Combine(fixture, ".mp-role-birth-fixture"), "synthetic");
            var roleOnly = new FileSecurity();
            roleOnly.SetAccessRuleProtection(true, false);
            roleOnly.AddAccessRule(new FileSystemAccessRule(role.RoleSid, FileSystemRights.FullControl, AccessControlType.Allow));
            await role.RunNamespaceOperationAsync(() =>
            {
                using FileStream writer = new FileInfo(file).Create(FileMode.CreateNew, FileSystemRights.Write,
                    FileShare.None, 4096, FileOptions.None, roleOnly);
                writer.Write([1, 2, 3]);
                writer.Flush(true);
                Assert.AreEqual(caller.User, new FileInfo(file).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)));
                return Task.CompletedTask;
            });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file));
            bool ordinary = CreateHardLink(alias, file, nint.Zero);
            int error = ordinary ? 0 : Marshal.GetLastPInvokeError();
            TestContext.WriteLine($"RoleOnlyBirth: ordinaryAliasCreated={ordinary}; nativeError={error}; writerClosed=True; cloudRegistered=False; ownerIsCurrentUser=True; callerElevated={new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator)}.");
            Assert.IsFalse(ordinary, "A role-only ordinary birth must not expose an alias window before conversion.");
            Assert.AreEqual(5, error);
            Assert.IsFalse(File.Exists(alias));
            await role.RunNamespaceOperationAsync(() =>
            {
                Assert.IsTrue(CreateHardLink(alias, file, nint.Zero), "The control proves the same native link operation can succeed for the role.");
                File.Delete(alias);
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, File.ReadAllBytes(file));
                return Task.CompletedTask;
            });
        }
        finally
        {
            CleanupRoleOnlyBirthFixture(prefix, fixture, file, caller.User!);
        }
    }

    private static void CleanupRoleOnlyBirthFixture(string prefix, string fixture, string file, SecurityIdentifier callerSid)
    {
        if (!Path.GetFullPath(fixture).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(fixture), "N", out _))
            throw new InvalidOperationException("The role birth fixture cleanup target is invalid.");
        if (Directory.Exists(fixture))
        {
            if (!File.Exists(Path.Combine(fixture, ".mp-role-birth-fixture")))
                throw new InvalidOperationException("The role birth fixture marker is missing.");
            var directoryCleanup = new DirectorySecurity();
            directoryCleanup.SetAccessRuleProtection(true, false);
            directoryCleanup.AddAccessRule(new FileSystemAccessRule(callerSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(fixture).SetAccessControl(directoryCleanup);
            if (File.Exists(file))
            {
                var cleanup = new FileSecurity();
                cleanup.SetAccessRuleProtection(true, false);
                cleanup.AddAccessRule(new FileSystemAccessRule(callerSid, FileSystemRights.FullControl, AccessControlType.Allow));
                new FileInfo(file).SetAccessControl(cleanup);
            }
            Directory.Delete(fixture, true);
        }
    }
}
