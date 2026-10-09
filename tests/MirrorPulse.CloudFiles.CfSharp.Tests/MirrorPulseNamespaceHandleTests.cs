using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MirrorPulse.CloudFiles.CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.26100")]
public sealed partial class MirrorPulseNamespaceHandleTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    public async Task RetainedHandleRejectsExistingAndNewlyObservedAliasesWithoutAclWrites()
    {
        MirrorPulseNamespaceExecutionSessionTests.AssertExpectedArchitecture();
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-namespace-handle-tests")) + Path.DirectorySeparatorChar;
        string fixture = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        string target = Path.Combine(fixture, "managed", "unsent.txt");
        string alias = Path.Combine(fixture, "outside", "alias.txt");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.CreateDirectory(Path.GetDirectoryName(alias)!);
            await File.WriteAllTextAsync(Path.Combine(fixture, ".mp-namespace-handle-fixture"), "synthetic");
            await File.WriteAllTextAsync(target, "latest unsent synthetic bytes");
            string original = new FileInfo(target).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            using (var retained = MirrorPulseWindowsNamespacePermissionLease.Open(target, target: true))
            {
                MirrorPulseWindowsNamespacePermissionLease.ValidateRetainedHandle(retained);
                bool created = CreateHardLink(alias, target, nint.Zero);
                int failure = created ? 0 : Marshal.GetLastPInvokeError();
                if (created)
                {
                    Assert.IsTrue(File.Exists(alias));
                    Assert.ThrowsExactly<InvalidDataException>(() => MirrorPulseWindowsNamespacePermissionLease.ValidateRetainedHandle(retained));
                }
                else
                {
                    Assert.AreNotEqual(0, failure);
                    Assert.IsFalse(File.Exists(alias));
                    MirrorPulseWindowsNamespacePermissionLease.ValidateRetainedHandle(retained);
                }
                await File.AppendAllTextAsync(target, " retained edit");
                TestContext.WriteLine($"NamespaceHandleSharing: hardLinkCreatedDuringLease={created}; newlyObservedAliasRejected={created}; nativeError={failure}; contentEditingAllowed=True; atomicLinkFreezeProven=False.");
            }
            if (File.Exists(alias)) File.Delete(alias);
            Assert.IsTrue(CreateHardLink(alias, target, nint.Zero));
            Assert.ThrowsExactly<InvalidDataException>(() =>
            {
                using var rejected = MirrorPulseWindowsNamespacePermissionLease.Open(target, target: true);
            });
            Assert.AreEqual(original, new FileInfo(target).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.AreEqual(original, new FileInfo(alias).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.AreEqual("latest unsent synthetic bytes retained edit", await File.ReadAllTextAsync(alias));
            File.Delete(alias);
            using (var retained = MirrorPulseWindowsNamespacePermissionLease.Open(target, target: true))
                MirrorPulseWindowsNamespacePermissionLease.ValidateRetainedHandle(retained);
            using WindowsIdentity caller = WindowsIdentity.GetCurrent();
            TestContext.WriteLine($"NamespaceHandleAliases: architecture={RuntimeInformation.ProcessArchitecture}; existingAliasRejected=True; noHandleLeak=True; originalPermissionsRetained=True; latestBytesRetained=True; aclWrites=0; callerElevated={new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator)}; cloudRootRegistered=False; personalDataUsed=False.");
        }
        finally { DeleteMarkedFixture(prefix, fixture); }
    }

    private static void DeleteMarkedFixture(string prefix, string fixture)
    {
        if (!Path.GetFullPath(fixture).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(fixture), "N", out _)) throw new InvalidOperationException("The synthetic handle fixture scope is invalid.");
        if (Directory.Exists(fixture))
        {
            if (!File.Exists(Path.Combine(fixture, ".mp-namespace-handle-fixture"))) throw new InvalidOperationException("The synthetic handle fixture marker is missing.");
            Directory.Delete(fixture, recursive: true);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateHardLinkW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateHardLink(string newName, string existingName, nint attributes);
}
