using System.Runtime.Versioning;
using System.Security.Principal;
using MirrorPulse.CloudFiles.CfSharp;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[SupportedOSPlatform("windows10.0.26100")]
public sealed class MirrorPulseNormalUserExecutionTests
{
    [TestMethod]
    public async Task NormalUserBoundaryRemovesRoleAcrossAwaitAndTasksThenRestoresOuterRole()
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var session = new MirrorPulseNamespaceExecutionSession();
        await session.RunNamespaceOperationAsync(async () =>
        {
            AssertIdentity(session, caller.User!, role: true);
            int value = await session.RunNormalUserOperationAsync(async () =>
            {
                await Task.Yield();
                AssertIdentity(session, caller.User!, role: false);
                await Task.Run(() => AssertIdentity(session, caller.User!, role: false));
                await session.RunNamespaceOperationAsync(() =>
                {
                    AssertIdentity(session, caller.User!, role: true);
                    return Task.CompletedTask;
                });
                AssertIdentity(session, caller.User!, role: false);
                return 42;
            });
            Assert.AreEqual(42, value);
            AssertIdentity(session, caller.User!, role: true);
        });
        AssertIdentity(session, caller.User!, role: false);
    }

    [TestMethod]
    public async Task NormalUserFailureRestoresOuterRoleAndDoesNotLeakItToCaller()
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        await using var session = new MirrorPulseNamespaceExecutionSession();
        await session.RunNamespaceOperationAsync(async () =>
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => session.RunNormalUserOperationAsync(async () =>
            {
                await Task.Yield();
                AssertIdentity(session, caller.User!, role: false);
                throw new IOException("Synthetic source failure.");
            }));
            AssertIdentity(session, caller.User!, role: true);
        });
        AssertIdentity(session, caller.User!, role: false);
    }

    [TestMethod]
    public async Task DisposalDrainsNormalUserWorkBeforeReleasingItsIdentity()
    {
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        var session = new MirrorPulseNamespaceExecutionSession();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task work = session.RunNormalUserOperationAsync(async () =>
        {
            entered.SetResult();
            await release.Task;
            AssertIdentity(session, caller.User!, role: false);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposing = session.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(disposing.IsCompleted);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => session.RunNormalUserOperationAsync(() => Task.CompletedTask));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => session.RunNamespaceOperationAsync(() => Task.CompletedTask));
        }
        finally { release.TrySetResult(); }
        await work.WaitAsync(TimeSpan.FromSeconds(2));
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));
        await session.DisposeAsync();
    }

    private static void AssertIdentity(MirrorPulseNamespaceExecutionSession session, SecurityIdentifier user, bool role)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        Assert.AreEqual(user, identity.User);
        Assert.AreEqual(role, new WindowsPrincipal(identity).IsInRole(session.RoleSid));
    }
}
