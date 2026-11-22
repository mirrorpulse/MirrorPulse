using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.Core.Tests;

public sealed partial class MirrorPulseNamespaceWorkAdmissionTests
{
    [TestMethod]
    public async Task RetainedDownstreamWorkDrainsWithItsOriginalRootWithoutAdmittingNewWork()
    {
        RootId first = RootId.New(), other = RootId.New();
        await using var admission = new MirrorPulseNamespaceWorkAdmission([first, other]);
        await OpenAsync(admission, first, other);
        using var original = admission.Enter([first]);
        var recovering = admission.WaitForRecoveryAsync([first]).AsTask();
        Assert.IsFalse(admission.TryEnter([first], out _));
        using var downstream = admission.Retain(original, [first]);
        original.Dispose();
        Assert.IsFalse(recovering.IsCompleted);
        Assert.ThrowsExactly<InvalidOperationException>(() => admission.Retain(original, [first]));
        Assert.ThrowsExactly<InvalidOperationException>(() => admission.Retain(downstream, [first, other]));
        await using var foreign = new MirrorPulseNamespaceWorkAdmission([first]);
        Assert.ThrowsExactly<InvalidOperationException>(() => foreign.Retain(downstream, [first]));
        Assert.AreEqual(43, await admission.RunAsync(other, _ => ValueTask.FromResult(43)));
        downstream.Dispose();
        await using (await recovering.WaitAsync(TimeSpan.FromSeconds(5))) { }
        Assert.IsFalse(admission.IsAdmitted(first));
        Assert.ThrowsExactly<InvalidOperationException>(() => admission.Retain(downstream, [first]));
    }

    [TestMethod]
    public async Task RetainedWorkKeepsShutdownDrainingAndCannotCreateAnotherScopeAfterShutdown()
    {
        RootId root = RootId.New();
        var admission = new MirrorPulseNamespaceWorkAdmission([root]);
        await OpenAsync(admission, root);
        using var original = admission.Enter([root]);
        using var retained = admission.Retain(original, [root]);
        Task stopping = admission.DisposeAsync().AsTask();
        original.Dispose();
        Assert.IsFalse(stopping.IsCompleted);
        Assert.ThrowsExactly<ObjectDisposedException>(() => admission.Retain(retained, [root]));
        retained.Dispose();
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
