using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CaptureDrainsEarlierRootOrParentApplicationBeforeInspectingAnchor(bool parent)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var preparation = parent ? Parent(fixture) : new(fixture.Baseline, fixture.Intent);
        await catalog.PrepareNamespacePermissionChangeAsync(preparation.Baseline, preparation.Intent);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var defined = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishCapture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new ObjectLease(preparation.Baseline, preparation.Intent)
        {
            BeforeWrite = async () => { entered.SetResult(); await release.Task; },
        };
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        using var callbackRelease = new CallbackRelease(release, finishCapture);
        Task<MirrorPulseNamespacePermissionResult> application = coordinator.ApplyAsync(preparation.Intent.OperationId, lease);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var definition = Capture(fixture);
        Task<MirrorPulseNamespacePermissionTree> capturing = coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
            async token =>
            {
                Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Verified,
                    (await catalog.ReadNamespacePermissionChangeAsync(preparation.Intent.OperationId, token))!.Phase);
                defined.SetResult();
                return definition;
            }, async (tree, token) =>
            {
                await finishCapture.Task;
                await catalog.AppendNamespacePermissionTreeMembersAsync(tree.Definition.ManifestId, [new(0, null, tree.Definition.Anchor)], token);
                await catalog.SealNamespacePermissionTreeAsync(tree.Definition.ManifestId, DateTimeOffset.UtcNow, token);
            });
        try
        {
            Assert.IsFalse(defined.Task.IsCompleted);
            Assert.IsNull(await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(preparation.Intent.OperationId, lease));
            release.TrySetResult();
            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await application.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
            await defined.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            release.TrySetResult();
            finishCapture.TrySetResult();
        }
        Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Sealed, (await capturing.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified, (await coordinator.ApplyAsync(preparation.Intent.OperationId, lease)).Outcome);
        Assert.AreEqual(1, lease.Writes);
        Assert.IsFalse(lease.Disposed);
    }

    [TestMethod]
    public async Task CaptureFencesRootAndParentButAnotherRootCanApplyWhileCallbackIsRunning()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        var parent = Parent(fixture);
        var other = OtherRoot(fixture);
        foreach (var preparation in new MirrorPulseNamespacePermissionPreparation[] { new(fixture.Baseline, fixture.Intent), parent, other })
            await catalog.PrepareNamespacePermissionChangeAsync(preparation.Baseline, preparation.Intent);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = Capture(fixture);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        using var callbackRelease = new CallbackRelease(release);
        Task<MirrorPulseNamespacePermissionTree> capturing = coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
            _ => Task.FromResult(definition), async (tree, token) =>
            {
                entered.SetResult();
                await release.Task;
                await catalog.CancelNamespacePermissionTreeCaptureAsync(tree.Definition.ManifestId, DateTimeOffset.UtcNow, token);
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var rootLease = new ObjectLease(fixture.Baseline, fixture.Intent);
        var parentLease = new ObjectLease(parent.Baseline, parent.Intent);
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(fixture.Intent.OperationId, rootLease));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(parent.Intent.OperationId, parentLease));
            var otherLease = new ObjectLease(other.Baseline, other.Intent);
            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
                (await coordinator.ApplyAsync(other.Intent.OperationId, otherLease).WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
            Assert.AreEqual(1, otherLease.Writes);
            Assert.AreEqual(0, rootLease.Reads);
            Assert.AreEqual(0, parentLease.Reads);
        }
        finally { release.TrySetResult(); }
        Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Cancelled, (await capturing.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await coordinator.ApplyAsync(fixture.Intent.OperationId, rootLease)).Outcome);
    }

    [TestMethod]
    public async Task CaptureFailureRetainsOriginalPageAcrossRestartAndCanResumeBeforeApplication()
    {
        using var fixture = new Fixture();
        var definition = Capture(fixture);
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
                _ => Task.FromResult(definition), async (tree, token) =>
                {
                    await catalog.AppendNamespacePermissionTreeMembersAsync(tree.Definition.ManifestId, [new(0, null, definition.Anchor)], token);
                    throw new IOException("Synthetic interruption after retaining the original page.");
                }));
            var lease = new ObjectLease(fixture.Baseline, fixture.Intent);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.ApplyAsync(fixture.Intent.OperationId, lease));
            Assert.AreEqual(0, lease.Reads);
        }
        await using var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var recovering = new MirrorPulseNamespacePermissionCoordinator(reopened);
        var freshLease = new ObjectLease(fixture.Baseline, fixture.Intent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => recovering.ApplyAsync(fixture.Intent.OperationId, freshLease));
        var resumed = await recovering.CaptureNamespacePermissionTreeAsync(Scope(definition), _ => Task.FromResult(definition), async (tree, token) =>
        {
            Assert.AreEqual(1, tree.CapturedMembers);
            CollectionAssert.AreEqual(new[] { new MirrorPulseNamespacePermissionTreeMember(0, null, definition.Anchor) },
                (await reopened.ReadNamespacePermissionTreeMembersAsync(definition.ManifestId, cancellationToken: token)).ToArray());
            await reopened.SealNamespacePermissionTreeAsync(definition.ManifestId, DateTimeOffset.UtcNow, token);
        });
        Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Sealed, resumed.Phase);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await recovering.ApplyAsync(fixture.Intent.OperationId, freshLease)).Outcome);
        Assert.AreEqual(1, freshLease.Writes);
    }

    [TestMethod]
    public async Task CancellationDuringDrainNeverCapturesAnAnchorOrCancelsTheAcceptedWrite()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent)
        {
            BeforeWrite = async () => { entered.SetResult(); await release.Task; },
        };
        using var callbackRelease = new CallbackRelease(release);
        Task<MirrorPulseNamespacePermissionResult> application = coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var cancellation = new CancellationTokenSource();
        var definition = Capture(fixture);
        int definitions = 0;
        Task<MirrorPulseNamespacePermissionTree> capturing = coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
            _ => { definitions++; return Task.FromResult(definition); }, (_, _) => Task.CompletedTask, cancellation.Token);
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(() => capturing);
            Assert.AreEqual(0, definitions);
            Assert.IsNull(await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId));
            Assert.IsFalse(application.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await application.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified, (await coordinator.ApplyAsync(fixture.Intent.OperationId, lease)).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CancellationAndDisposalWaitForTheActualCaptureCallbackToExit(bool defining)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = Capture(fixture);
        using var callbackRelease = new CallbackRelease(release);
        Task<MirrorPulseNamespacePermissionTree> capturing = coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
            async token =>
            {
                if (defining)
                {
                    entered.SetResult();
                    await release.Task;
                    token.ThrowIfCancellationRequested();
                }
                return definition;
            }, async (_, token) =>
            {
                entered.SetResult();
                await release.Task;
                token.ThrowIfCancellationRequested();
            }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        Task disposing = coordinator.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(capturing.IsCompleted);
            Assert.IsFalse(disposing.IsCompleted);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => coordinator.ApplyAsync(fixture.Intent.OperationId,
                new ObjectLease(fixture.Baseline, fixture.Intent)));
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
                _ => Task.FromResult(definition), (_, _) => Task.CompletedTask));
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<OperationCanceledException>(() => capturing);
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.DisposeAsync();
        var retained = await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId);
        if (defining)
        {
            Assert.IsNull(retained);
            return;
        }
        Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Capturing, retained!.Phase);
        await using var recovering = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => recovering.ApplyAsync(fixture.Intent.OperationId, lease));
        Assert.AreEqual(0, lease.Reads);
    }

    [TestMethod]
    public async Task DuplicateRootCaptureIsRejectedWhileIndependentRootCapturesCanOverlap()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var definition = Capture(fixture);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callbackRelease = new CallbackRelease(release);
        Task<MirrorPulseNamespacePermissionTree> capturing = coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
            _ => Task.FromResult(definition), async (_, _) => { entered.SetResult(); await release.Task; });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
                _ => throw new AssertFailedException("A duplicate capture must not inspect an anchor."), (_, _) => Task.CompletedTask));
            var other = OtherRoot(fixture, true);
            var otherDefinition = new MirrorPulseNamespacePermissionTreeDefinition(Guid.NewGuid(), other, 1, other.Baseline.CapturedAt);
            var otherResult = await coordinator.CaptureNamespacePermissionTreeAsync(Scope(otherDefinition), _ => Task.FromResult(otherDefinition),
                async (tree, token) => { await catalog.CancelNamespacePermissionTreeCaptureAsync(tree.Definition.ManifestId, DateTimeOffset.UtcNow, token); })
                .WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Cancelled, otherResult.Phase);
            Assert.IsFalse(capturing.IsCompleted);
        }
        finally { release.TrySetResult(); }
        Assert.AreEqual(MirrorPulseNamespacePermissionTreePhase.Capturing, (await capturing.WaitAsync(TimeSpan.FromSeconds(2))).Phase);
    }

    [TestMethod]
    [DataRow("root")]
    [DataRow("volume")]
    [DataRow("sync-root")]
    public async Task MismatchedDefinitionIsRejectedBeforeDurableCaptureAndReleasesOnlyRuntimeFence(string field)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var definition = Capture(fixture);
        var scope = Scope(definition);
        scope = field switch
        {
            "root" => scope with { RootId = RootId.New() },
            "volume" => scope with { VolumeSerialNumber = 2 },
            _ => scope with { SyncRootFileId = Guid.NewGuid() },
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => coordinator.CaptureNamespacePermissionTreeAsync(scope,
            _ => Task.FromResult(definition), (_, _) => throw new AssertFailedException("A misbound capture must not run.")));
        Assert.IsNull(await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId));
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
            (await coordinator.ApplyAsync(fixture.Intent.OperationId, new ObjectLease(fixture.Baseline, fixture.Intent))).Outcome);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TerminalCaptureCannotBeReopenedOrInvokeAnotherCaptureCallback(bool cancel)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var definition = Capture(fixture);
        var retained = await coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition), _ => Task.FromResult(definition), async (tree, token) =>
        {
            await catalog.AppendNamespacePermissionTreeMembersAsync(tree.Definition.ManifestId, [new(0, null, definition.Anchor)], token);
            if (cancel) await catalog.CancelNamespacePermissionTreeCaptureAsync(definition.ManifestId, DateTimeOffset.UtcNow, token);
            else await catalog.SealNamespacePermissionTreeAsync(definition.ManifestId, DateTimeOffset.UtcNow, token);
        });
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
            _ => Task.FromResult(definition), (_, _) => throw new AssertFailedException("Terminal evidence must not be captured again.")));
        Assert.AreEqual(retained, await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId));
        Assert.HasCount(0, await catalog.ReadNamespacePermissionChangesAsync());
    }

    [TestMethod]
    [DataRow("volume")]
    [DataRow("sync-root")]
    [DataRow("root")]
    public async Task IncompleteCaptureScopeNeverInvokesCallbacksOrLeavesAdmissionClosed(string field)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var definition = Capture(fixture);
        var scope = Scope(definition);
        scope = field switch
        {
            "volume" => scope with { VolumeSerialNumber = 0 },
            "sync-root" => scope with { SyncRootFileId = Guid.Empty },
            _ => scope with { RootId = default },
        };
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => coordinator.CaptureNamespacePermissionTreeAsync(scope,
            _ => throw new AssertFailedException("An incomplete scope must not inspect an anchor."),
            (_, _) => throw new AssertFailedException("An incomplete scope must not run capture.")));
        Assert.IsNull(await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId));
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
            (await coordinator.ApplyAsync(fixture.Intent.OperationId, new ObjectLease(fixture.Baseline, fixture.Intent))).Outcome);
    }

    [TestMethod]
    public async Task AnchorInspectionFailureReleasesRuntimeFenceWithoutCreatingDurableEvidence()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var definition = Capture(fixture);
        await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.CaptureNamespacePermissionTreeAsync(Scope(definition),
            _ => throw new IOException("Synthetic anchor inspection failure."),
            (_, _) => throw new AssertFailedException("Capture must not follow a failed inspection.")));
        Assert.IsNull(await catalog.ReadNamespacePermissionTreeAsync(definition.ManifestId));
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified,
            (await coordinator.ApplyAsync(fixture.Intent.OperationId, new ObjectLease(fixture.Baseline, fixture.Intent))).Outcome);
    }

    // Release held callbacks even when a preceding assertion or bounded wait fails.
    // Declared after the coordinator so this cleanup precedes asynchronous disposal.
    private sealed class CallbackRelease(params TaskCompletionSource[] callbacks) : IDisposable
    {
        public void Dispose()
        {
            foreach (TaskCompletionSource callback in callbacks) callback.TrySetResult();
        }
    }

    private static MirrorPulseNamespacePermissionCaptureScope Scope(MirrorPulseNamespacePermissionTreeDefinition definition) =>
        new(definition.Anchor.Baseline.LocalObject.VolumeSerialNumber, definition.Anchor.Baseline.LocalObject.SyncRootFileId,
            definition.Anchor.Intent.RootId!.Value);

    private static MirrorPulseNamespacePermissionPreparation Parent(Fixture fixture)
    {
        var baseline = fixture.Baseline with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = null,
            RelativePath = string.Empty,
            IsDirectory = true,
            LocalObject = fixture.Baseline.LocalObject with { LocalFileId = fixture.Baseline.LocalObject.SyncRootFileId },
        };
        var intent = fixture.Intent with
        {
            OperationId = Guid.NewGuid(),
            EvidenceId = baseline.EvidenceId,
            RootId = baseline.RootId,
            RelativePath = baseline.RelativePath,
            LocalObject = baseline.LocalObject,
        };
        return new(baseline, intent);
    }

    private static MirrorPulseNamespacePermissionPreparation OtherRoot(Fixture fixture, bool directory = false)
    {
        var baseline = fixture.Baseline with
        {
            EvidenceId = Guid.NewGuid(),
            RootId = RootId.New(),
            RelativePath = directory ? "Other" : "Other/file.txt",
            IsDirectory = directory,
            LocalObject = fixture.Baseline.LocalObject with { LocalFileId = Guid.NewGuid() },
        };
        var intent = fixture.Intent with
        {
            OperationId = Guid.NewGuid(),
            EvidenceId = baseline.EvidenceId,
            RootId = baseline.RootId,
            RelativePath = baseline.RelativePath,
            LocalObject = baseline.LocalObject,
        };
        return new(baseline, intent);
    }
}
