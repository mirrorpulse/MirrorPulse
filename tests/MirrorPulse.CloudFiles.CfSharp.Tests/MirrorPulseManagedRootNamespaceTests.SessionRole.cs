using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using CfSharp;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[SupportedOSPlatform("windows10.0.19041")]
public sealed partial class MirrorPulseManagedRootNamespaceTests
{
    [TestMethod]
    [DoNotParallelize]
    [TestCategory("NativeCloudFiles")]
    public async Task NativeNamespaceRoleKeepsAclClosedDuringControlledOperationsAndCfSharpRestart()
    {
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            Assert.Inconclusive("Requires the disposable NativeCloudFiles verification environment.");
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests", Guid.NewGuid().ToString("N"));
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        var registry = new CfSharpMirrorPulseCloudRootRegistry();
        InstanceId instance = InstanceId.New();
        RootRegistration registration = AdapterRootRegistrationMapper.Map(AdapterId.Parse("example.namespace-role"), instance,
            new AdapterRootDefinition("docs", "Docs", "Docs", false), RootRegistrationState.Active,
            identityScope: RootIdentityScope.InstanceRoot);
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        using WindowsIdentity user = WindowsIdentity.GetCurrent();
        byte[] online = Enumerable.Range(0, 65_553).Select(index => (byte)(index % 251)).ToArray();
        byte[] acceptedBytes = Encoding.UTF8.GetBytes("accepted original role proof");
        MirrorPulseContentAcceptanceProof? proof = null;
        CloudLocalFileBinding? binding = null;
        SecurityIdentifier? previousRole = null;
        string docs = Path.Combine(paths.SyncRootPath, "Docs");
        string nested = Path.Combine(docs, "Nested");
        string unsent = Path.Combine(nested, "unsent.txt");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            registry.Register(new(paths.SyncRootPath, "0.1.0", Guid.NewGuid(), [1, 2, 3]));
            await File.WriteAllTextAsync(Path.Combine(root, ".mp-namespace-fixture"), string.Empty, timeout.Token);
            for (int run = 0; run < 2; run++)
            {
                using var role = new NamespaceSessionRole();
                if (previousRole is not null) Assert.AreNotEqual(previousRole, role.RoleSid);
                previousRole = role.RoleSid;
                if (run == 1) InstallFixtureNamespaceAcl(paths.SyncRootPath, user.User!, role.RoleSid);
                var source = new StrictProtectionRangeSource(online, instance);
                var state = new MirrorPulseCfSharpStateSession(paths);
                await using var fileSystem = new MirrorPulseCloudFileSystemBuilder(paths).WithStateStore(state)
                    .WithContentProvider(new MirrorPulseDemandProvider(router, source)).Build();
                await role.RunAsync(async () => await fileSystem.StartAsync(timeout.Token));
                await using CloudLocalChangeFeed feed = fileSystem.CreateLocalChangeFeed();
                await feed.StartAsync(timeout.Token);
                if (run == 0)
                {
                    await role.RunAsync(async () => await new MirrorPulseRootPopulationCoordinator(fileSystem, feed).PopulateAsync(router, timeout.Token));
                    InstallFixtureNamespaceAcl(paths.SyncRootPath, user.User!, role.RoleSid);
                    await role.RunAsync(async () =>
                    {
                        Directory.CreateDirectory(nested);
                        await File.WriteAllTextAsync(unsent, "original unsent", timeout.Token);
                        await File.WriteAllTextAsync(Path.Combine(nested, "replacement.tmp"), "replacement", timeout.Token);
                        await File.WriteAllBytesAsync(Path.Combine(docs, "accepted.txt"), acceptedBytes, timeout.Token);
                        await fileSystem.GetDirectory("Docs").CreatePlaceholdersAsync([
                            CloudFilePlaceholderSpec.CreateBuilder("online.bin", router.CreateFileIdentity(instance, "docs", "online", "v1"), online.Length)
                                .WithInSyncState(true).WithInitialAvailability(CloudAvailabilityTarget.OnlineOnly).Build()], cancellationToken: timeout.Token);
                    });
                    await File.AppendAllTextAsync(unsent, " latest", timeout.Token); // Normal user, no namespace role.
                    CloudItemSnapshot original = await fileSystem.GetFile(Path.Combine("Docs", "accepted.txt")).InspectAsync(timeout.Token);
                    proof = new(Guid.NewGuid(), MirrorPulseContentConfirmation.CaptureUploadBinding(original),
                        MirrorPulsePlaceholderIdentity.Create(instance, "accepted", "v2").ToCfSharp().ItemId,
                        "accepted", "v2", acceptedBytes.Length, Convert.ToHexString(SHA256.HashData(acceptedBytes)));
                    binding = original.LocalBinding;
                }
                foreach (string mode in new[] { "delete-child", "rename-child", "replace-child", "rename", "delete-tree" })
                    Assert.IsFalse((await RunNamespaceProcessAsync(root, mode, timeout.Token)).Completed);
                Assert.AreEqual("original unsent latest", await File.ReadAllTextAsync(unsent, timeout.Token));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => File.WriteAllTextAsync(Path.Combine(docs, "uncontrolled.txt"), "uncontrolled", timeout.Token));
                string outside = Path.Combine(root, $"outside-{run}.txt");
                await File.WriteAllTextAsync(outside, "outside bytes", timeout.Token);
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Move(outside, Path.Combine(docs, "imported.txt")), timeout.Token));
                Assert.AreEqual("outside bytes", await File.ReadAllTextAsync(outside, timeout.Token));
                string controlled = Path.Combine(nested, "controlled.txt");
                string renamed = Path.Combine(nested, "renamed.txt");
                await role.RunAsync(async () =>
                {
                    await File.WriteAllTextAsync(controlled, "controlled create", timeout.Token);
                    File.Move(controlled, renamed);
                    string replacement = Path.Combine(nested, "controlled-replacement.tmp");
                    await File.WriteAllTextAsync(replacement, "controlled replacement", timeout.Token);
                    File.Move(replacement, renamed, overwrite: true);
                });
                Assert.AreEqual("controlled replacement", await File.ReadAllTextAsync(renamed, timeout.Token));
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Task.Run(() => File.Delete(renamed), timeout.Token));
                await role.RunAsync(() => { File.Delete(renamed); return Task.CompletedTask; });
                Assert.IsFalse(File.Exists(renamed));
                CollectionAssert.AreEqual(online, await File.ReadAllBytesAsync(Path.Combine(docs, "online.bin"), timeout.Token));
                if (run == 0) Assert.IsGreaterThan(0, source.Hydrations);
                CloudFile file = fileSystem.GetFile(Path.Combine("Docs", "accepted.txt"));
                Assert.AreEqual(binding, (await file.InspectAsync(timeout.Token)).LocalBinding);
                MirrorPulseContentConfirmationReceipt receipt = await MirrorPulseContentConfirmation.ConfirmAsync(file, proof!, timeout.Token);
                for (int attempt = 1; attempt < 3 && receipt.Outcome is
                    MirrorPulseContentConfirmationOutcome.Busy or MirrorPulseContentConfirmationOutcome.ProtectionLost; attempt++)
                {
                    await Task.Delay(50, timeout.Token);
                    receipt = await MirrorPulseContentConfirmation.ConfirmAsync(file, proof!, timeout.Token);
                }
                TestContext.WriteLine($"NamespaceRoleConfirmation: restart={run}; outcome={receipt.Outcome}; stage={receipt.Stage}; nativeHResult={receipt.NativeErrorHResult:X8}.");
                Assert.IsTrue(receipt.MayAcknowledge);
                if (run == 1) Assert.AreEqual(MirrorPulseContentConfirmationOutcome.AlreadyConfirmed, receipt.Outcome);
                TestContext.WriteLine($"NamespaceRoleProbe: restart={run}; currentUserRetained=True; normalNamespaceDenied=True; inPlaceEdit=True; controlledCreateRenameReplaceDelete=True; permissionRelaxationWindows=0; publicConfirmation=True; productIntegrated=False; elevatedCaller={new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator)}; OS={Environment.OSVersion.Version}.");
            }
        }
        finally
        {
            registry.Unregister(paths.SyncRootPath);
            if (Directory.Exists(paths.SyncRootPath))
            {
                var cleanup = new DirectorySecurity();
                cleanup.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
                cleanup.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
                new DirectoryInfo(paths.SyncRootPath).SetAccessControl(cleanup);
            }
            if (Directory.Exists(docs)) ResetMarkedFixtureAcl(root, docs, user.User!);
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void InstallFixtureNamespaceAcl(string root, SecurityIdentifier user, SecurityIdentifier role)
    {
        // Only the test-created GUID tree is passed by this fixture. The current
        // user can edit existing files; only the extra current-user role may
        // change namespace. No temporary grant to the ordinary user is needed.
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.ReadAndExecute |
            FileSystemRights.WriteAttributes | FileSystemRights.WriteExtendedAttributes,
            InheritanceFlags.ContainerInherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Read | FileSystemRights.Write,
            InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(role, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(root).SetAccessControl(acl);
    }
}
