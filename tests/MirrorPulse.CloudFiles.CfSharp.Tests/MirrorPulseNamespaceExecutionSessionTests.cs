using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using MirrorPulse.CloudFiles.CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNamespaceExecutionSessionTests
{
    [TestMethod]
    public async Task NamespaceBirthUsesCurrentUserOwnerWithoutChangingTheNormalSourceToken()
    {
        AssertExpectedArchitecture();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        SecurityIdentifier? originalDefaultOwner = caller.Owner;
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-namespace-default-owner", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "born.txt");
        try
        {
            await using var session = new MirrorPulseNamespaceExecutionSession();
            await session.RunNamespaceOperationAsync(async () =>
            {
                using WindowsIdentity active = WindowsIdentity.GetCurrent();
                Assert.AreEqual(caller.User, active.User);
                Assert.AreEqual(caller.User, active.Owner);
                Assert.IsTrue(new WindowsPrincipal(active).IsInRole(session.RoleSid));
                await File.WriteAllTextAsync(file, "owned at creation");
            });
            Assert.AreEqual(caller.User, new FileInfo(file).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)));
            Assert.AreEqual("owned at creation", await File.ReadAllTextAsync(file));
            await session.RunNormalUserOperationAsync(() =>
            {
                using WindowsIdentity normal = WindowsIdentity.GetCurrent();
                Assert.AreEqual(caller.User, normal.User);
                Assert.AreEqual(originalDefaultOwner, normal.Owner);
                Assert.IsFalse(new WindowsPrincipal(normal).IsInRole(session.RoleSid));
                return Task.CompletedTask;
            });
            using WindowsIdentity restored = WindowsIdentity.GetCurrent();
            Assert.AreEqual(originalDefaultOwner, restored.Owner);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    internal static void AssertExpectedArchitecture()
    {
        string? expected = Environment.GetEnvironmentVariable("MIRRORPULSE_NAMESPACE_TEST_ARCHITECTURE");
        if (expected is not null) Assert.AreEqual(expected, RuntimeInformation.ProcessArchitecture.ToString());
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NAMESPACE_REQUIRE_NONADMIN") == "1")
        {
            using WindowsIdentity caller = WindowsIdentity.GetCurrent();
            Assert.IsFalse(new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator),
                "The dedicated ordinary-user gate must execute with a non-administrator token.");
        }
    }

    [TestMethod]
    public async Task RoleFlowsAcrossAwaitAndRestoresTheOriginalCurrentUser()
    {
        AssertExpectedArchitecture();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var session = new MirrorPulseNamespaceExecutionSession();
        Assert.AreEqual(caller.User, session.OwnerSid);
        Assert.IsFalse(new WindowsPrincipal(caller).IsInRole(session.RoleSid));
        await session.RunNamespaceOperationAsync(async () =>
        {
            await Task.Yield();
            using WindowsIdentity active = WindowsIdentity.GetCurrent();
            Assert.AreEqual(caller.User, active.User);
            Assert.IsTrue(new WindowsPrincipal(active).IsInRole(session.RoleSid));
            await Task.Run(() =>
            {
                using WindowsIdentity child = WindowsIdentity.GetCurrent();
                Assert.AreEqual(caller.User, child.User);
                Assert.IsTrue(new WindowsPrincipal(child).IsInRole(session.RoleSid));
            });
        });
        using WindowsIdentity restored = WindowsIdentity.GetCurrent();
        Assert.AreEqual(caller.User, restored.User);
        Assert.IsFalse(new WindowsPrincipal(restored).IsInRole(session.RoleSid));
    }

    [TestMethod]
    public async Task DisposalDrainsActiveWorkAndRejectsNewOperations()
    {
        AssertExpectedArchitecture();
        await using var session = new MirrorPulseNamespaceExecutionSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = session.RunNamespaceOperationAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposing = session.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(disposing.IsCompleted);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => session.RunNamespaceOperationAsync(() => Task.CompletedTask));
        }
        finally { release.TrySetResult(); }
        await work.WaitAsync(TimeSpan.FromSeconds(2));
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync();
    }

    [TestMethod]
    public async Task FailedOperationRestoresCallerAndDoesNotPreventDisposal()
    {
        AssertExpectedArchitecture();
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        var session = new MirrorPulseNamespaceExecutionSession();
        try
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => session.RunNamespaceOperationAsync(async () =>
            {
                await Task.Yield();
                throw new IOException("Synthetic local operation failure.");
            }));
            using WindowsIdentity restored = WindowsIdentity.GetCurrent();
            Assert.AreEqual(caller.User, restored.User);
            Assert.IsFalse(new WindowsPrincipal(restored).IsInRole(session.RoleSid));
        }
        finally { await session.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
    }
}
