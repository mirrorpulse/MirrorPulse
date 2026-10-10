using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using MirrorPulse.CloudFiles.CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespaceHandleTests
{
    [TestMethod]
    [DoNotParallelize]
    public async Task RetainedOwnerMetadataCanRotateAbandonedRoleWithoutNormalContentAccess()
    {
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-role-birth-tests")) + Path.DirectorySeparatorChar;
        string fixture = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        string file = Path.Combine(fixture, "born.bin"), alias = Path.Combine(fixture, "outside.link");
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var next = new MirrorPulseNamespaceExecutionSession();
        string oldRole;
        try
        {
            Directory.CreateDirectory(fixture);
            await File.WriteAllTextAsync(Path.Combine(fixture, ".mp-role-birth-fixture"), "synthetic");
            await using (var first = new MirrorPulseNamespaceExecutionSession())
            {
                oldRole = first.RoleSid.Value;
                var birthSecurity = new FileSecurity();
                birthSecurity.SetSecurityDescriptorSddlForm(MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(first), AccessControlSections.Access);
                await first.RunNamespaceOperationAsync(() =>
                {
                    var parent = new DirectorySecurity();
                    parent.SetSecurityDescriptorSddlForm(MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(first, true), AccessControlSections.Access);
                    new DirectoryInfo(fixture).SetAccessControl(parent);
                    using FileStream writer = new FileInfo(file).Create(FileMode.CreateNew, FileSystemRights.Write,
                        FileShare.None, 4096, FileOptions.None, birthSecurity);
                    writer.Write([1, 2, 3]); writer.Flush(true);
                    Assert.AreEqual(caller.User, new FileInfo(file).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)));
                    return Task.CompletedTask;
                });
            }
            var nextParent = new DirectorySecurity();
            nextParent.SetSecurityDescriptorSddlForm(MirrorPulseNamespacePermissionPolicy.CreateProtectedDacl(next, true), AccessControlSections.Access);
            new DirectoryInfo(fixture).SetAccessControl(nextParent);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => next.RunNamespaceOperationAsync(async () =>
                _ = await File.ReadAllBytesAsync(file)));
            Assert.IsFalse(CreateHardLink(alias, file, nint.Zero));
            Assert.AreEqual(5, Marshal.GetLastPInvokeError());
            using (SafeFileHandle readControl = OpenOwnerMetadata(file, 0x00020000, 3, nint.Zero, 3, 0x42200000, nint.Zero))
                TestContext.WriteLine($"OwnerMetadataReadControl: opened={!readControl.IsInvalid}; nativeError={(readControl.IsInvalid ? Marshal.GetLastPInvokeError() : 0)}.");
            using (SafeFileHandle writeDacl = OpenOwnerMetadata(file, 0x00040000, 3, nint.Zero, 3, 0x42200000, nint.Zero))
                TestContext.WriteLine($"OwnerMetadataWriteDacl: opened={!writeDacl.IsInvalid}; nativeError={(writeDacl.IsInvalid ? Marshal.GetLastPInvokeError() : 0)}.");
            using (SafeFileHandle metadata = OpenOwnerMetadata(file, 0x00060000, 3, nint.Zero, 3, 0x42200000, nint.Zero))
            {
                if (metadata.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
                Assert.AreEqual(1U, MirrorPulseWindowsNamespacePermissionLease.ValidateRetainedHandle(metadata));
                var descriptor = new OwnerMetadataSecurity(metadata);
                Assert.AreEqual(caller.User, descriptor.GetOwner(typeof(SecurityIdentifier)));
                Assert.IsTrue(descriptor.GetSecurityDescriptorSddlForm(AccessControlSections.Access).Contains(oldRole, StringComparison.Ordinal));
                var rotated = new FileSecurity();
                rotated.SetSecurityDescriptorSddlForm(MirrorPulseNamespacePermissionPolicy.CreateOrdinaryFileBirthDacl(next), AccessControlSections.Access);
                descriptor.SetSecurityDescriptorSddlForm(rotated.GetSecurityDescriptorSddlForm(AccessControlSections.Access), AccessControlSections.Access);
                descriptor.Apply(metadata);
                Assert.AreEqual(1U, MirrorPulseWindowsNamespacePermissionLease.ValidateRetainedHandle(metadata));
                var readback = new OwnerMetadataSecurity(metadata);
                Assert.IsFalse(readback.GetSecurityDescriptorSddlForm(AccessControlSections.Access).Contains(oldRole, StringComparison.Ordinal));
                Assert.IsTrue(readback.GetSecurityDescriptorSddlForm(AccessControlSections.Access).Contains(next.RoleSid.Value, StringComparison.Ordinal));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.AppendAllTextAsync(file, "not admitted"));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(file)));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Move(file, file + ".ordinary")));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.WriteAllTextAsync(Path.Combine(fixture, "ordinary.txt"), "not admitted"));
                Assert.IsFalse(CreateHardLink(alias, file, nint.Zero));
                Assert.AreEqual(5, Marshal.GetLastPInvokeError());
                // A metadata handle does not freeze the name. The parent ACL and a shared
                // Host actor must exclude unrelated namespace operations during recovery.
                await next.RunNamespaceOperationAsync(() =>
                {
                    File.Move(file, file + ".replaced");
                    Assert.AreEqual(1U, MirrorPulseWindowsNamespacePermissionLease.ValidateRetainedHandle(metadata));
                    File.Move(file + ".replaced", file);
                    return Task.CompletedTask;
                });
            }
            await next.RunNamespaceOperationAsync(async () =>
                CollectionAssert.AreEqual(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(file)));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.ReadAllBytesAsync(file));
            TestContext.WriteLine($"OrdinaryBirthOwnerRecovery: oldRoleDisposed=True; ownerMetadataOnly=True; parentAclClosed=True; normalNamespaceDenied=True; metadataNameFreeze=False; sharedActorRequired=True; normalDataNeverGranted=True; aliasesDenied=True; latestBytesRetained=True; cloudRegistered=False; callerElevated={new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator)}; productRecoveryIntegrated=False.");
        }
        finally { CleanupRoleOnlyBirthFixture(prefix, fixture, file, caller.User!); }
    }

    private sealed class OwnerMetadataSecurity(SafeFileHandle handle)
        : NativeObjectSecurity(false, ResourceType.FileObject, handle, AccessControlSections.Owner | AccessControlSections.Access)
    {
        public override Type AccessRightType => typeof(FileSystemRights);
        public override Type AccessRuleType => typeof(OwnerMetadataAccessRule);
        public override Type AuditRuleType => typeof(OwnerMetadataAuditRule);
        public override AccessRule AccessRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AccessControlType type) =>
            new OwnerMetadataAccessRule(identityReference, accessMask, isInherited, inheritanceFlags, propagationFlags, type);
        public override AuditRule AuditRuleFactory(IdentityReference identityReference, int accessMask, bool isInherited,
            InheritanceFlags inheritanceFlags, PropagationFlags propagationFlags, AuditFlags flags) =>
            new OwnerMetadataAuditRule(identityReference, accessMask, isInherited, inheritanceFlags, propagationFlags, flags);
        public void Apply(SafeFileHandle handle) => Persist(handle, AccessControlSections.Access);
    }

    private sealed class OwnerMetadataAccessRule(IdentityReference identity, int mask, bool inherited,
        InheritanceFlags inheritance, PropagationFlags propagation, AccessControlType type)
        : AccessRule(identity, mask, inherited, inheritance, propagation, type);

    private sealed class OwnerMetadataAuditRule(IdentityReference identity, int mask, bool inherited,
        InheritanceFlags inheritance, PropagationFlags propagation, AuditFlags flags)
        : AuditRule(identity, mask, inherited, inheritance, propagation, flags);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial SafeFileHandle OpenOwnerMetadata(string path, uint access, uint share,
        nint attributes, uint disposition, uint flags, nint template);
}
