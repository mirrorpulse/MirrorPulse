using System.Security.AccessControl;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
[DoNotParallelize] // Short gate budgets measure competing operations within each test, independently of unrelated suite scheduling.
public sealed partial class MirrorPulseNamespacePermissionCoordinatorTests
{
    private const string Owner = "S-1-5-21-100-200-300-1001";
    private static readonly string Original = Canonical($"D:AI(A;OICI;FA;;;{Owner})");
    private static readonly string Target = Canonical($"D:P(A;;FRFW;;;{Owner})(A;;FA;;;S-1-5-5-123-456)");
    private static readonly string Unowned = Canonical($"D:P(A;;FR;;;{Owner})");

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task NativeMarkerReadbackSurvivesCrashAndLaterAuditRequiresTheRecordedDescriptor(bool interruptedWrite)
    {
        using var fixture = new Fixture();
        var descriptor = new RawSecurityDescriptor(Target);
        descriptor.SetFlags(descriptor.ControlFlags | ControlFlags.DiscretionaryAclAutoInherited);
        string nativeReadback = descriptor.GetSddlForm(AccessControlSections.Access);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent)
        {
            AfterWrite = () => nativeReadback,
            FailAfterWrite = interruptedWrite,
        };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            if (interruptedWrite)
                await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ApplyAsync(fixture.Intent.OperationId, lease));
            else
                Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await coordinator.ApplyAsync(fixture.Intent.OperationId, lease)).Outcome);
        }
        lease.FailAfterWrite = false;
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(reopened);
            var recovered = await coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
            Assert.AreEqual(interruptedWrite ? MirrorPulseNamespacePermissionOutcome.Verified : MirrorPulseNamespacePermissionOutcome.AlreadyVerified, recovered.Outcome);
            Assert.AreEqual(1, lease.Writes);
            var retained = (await reopened.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!;
            Assert.AreEqual(Target, retained.Intent.TargetDacl);
            Assert.AreEqual(nativeReadback, retained.Verification!.Dacl);
            Assert.AreEqual(fixture.Baseline, await reopened.ReadNamespacePermissionBaselineAsync(fixture.Baseline.EvidenceId));
            // The accepted write normalization is not permission to reinterpret a later
            // descriptor change: historical verification remains an exact observed fact.
            lease.Current = lease.Current with { Dacl = Target };
            var later = await coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.RecoveryRequired, later.Outcome);
            Assert.AreEqual(MirrorPulseNamespacePermissionRecoveryReason.DaclChanged, later.RecoveryReason);
            Assert.AreEqual(retained, await reopened.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId));
        }
    }

    [TestMethod]
    public async Task ApplicationRequiresPreparedEvidenceAndPersistsBeforeFreshVerification()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => coordinator.ApplyAsync(fixture.Intent.OperationId, lease));
        Assert.AreEqual(0, lease.Reads);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        lease.BeforeWrite = async () =>
        {
            var prepared = await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, prepared!.Phase);
            Assert.AreEqual(fixture.Baseline, await catalog.ReadNamespacePermissionBaselineAsync(fixture.Baseline.EvidenceId));
        };
        lease.BeforeRead = async read =>
        {
            if (read == 2)
            {
                var applied = await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId);
                Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Applied, applied!.Phase);
                Assert.IsNull(applied.Verification);
            }
        };
        var result = await coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, result.Outcome);
        Assert.AreEqual(1, lease.Writes);
        Assert.AreEqual(2, lease.Reads);
        var verified = await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId);
        Assert.AreEqual(Target, verified!.Verification!.Dacl);
        var replay = await coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.AlreadyVerified, replay.Outcome);
        Assert.AreEqual(1, lease.Writes);
        Assert.AreEqual(verified, await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId));
        Assert.IsFalse(lease.Disposed); // Caller owns the stable object lease.
    }

    [TestMethod]
    public async Task WriteThenCrashIsRecoveredAfterCatalogReopenWithoutAnotherAclWrite()
    {
        using var fixture = new Fixture();
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent) { FailAfterWrite = true };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ApplyAsync(fixture.Intent.OperationId, lease));
            Assert.AreEqual(Target, lease.Current.Dacl);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Prepared, (await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!.Phase);
        }
        lease.FailAfterWrite = false;
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(reopened);
            var result = await coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, result.Outcome);
            Assert.AreEqual(1, lease.Writes);
            Assert.AreEqual(fixture.Baseline, await reopened.ReadNamespacePermissionBaselineAsync(fixture.Baseline.EvidenceId));
        }
    }

    [TestMethod]
    public async Task ReadBackFailureRetainsAppliedFactForNextOwner()
    {
        using var fixture = new Fixture();
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent) { FailRead = 2 };
        await using (var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
            await Assert.ThrowsExactlyAsync<IOException>(() => coordinator.ApplyAsync(fixture.Intent.OperationId, lease));
            var record = await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId);
            Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Applied, record!.Phase);
            Assert.IsNull(record.Verification);
        }
        await using (var reopened = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths))
        {
            await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(reopened);
            var result = await coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
            Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, result.Outcome);
            Assert.AreEqual(1, lease.Writes);
        }
    }

    [TestMethod]
    [DataRow("file")]
    [DataRow("volume")]
    [DataRow("sync-root")]
    [DataRow("root-id")]
    [DataRow("path")]
    [DataRow("directory")]
    [DataRow("owner")]
    [DataRow("dacl")]
    public async Task ChangedObjectOwnerScopeOrUnknownAclIsFencedWithoutWriting(string field)
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent);
        lease.Current = field switch
        {
            "file" => lease.Current with { LocalObject = lease.Current.LocalObject with { LocalFileId = Guid.NewGuid() } },
            "volume" => lease.Current with { LocalObject = lease.Current.LocalObject with { VolumeSerialNumber = 2 } },
            "sync-root" => lease.Current with { LocalObject = lease.Current.LocalObject with { SyncRootFileId = Guid.NewGuid() } },
            "root-id" => lease.Current with { RootId = RootId.New() },
            "path" => lease.Current with { RelativePath = "Docs/unrelated.txt" },
            "directory" => lease.Current with { IsDirectory = true },
            "owner" => lease.Current with { OwnerSid = "S-1-5-21-100-200-300-1002" },
            "dacl" => lease.Current with { Dacl = Unowned },
            _ => throw new InvalidOperationException(),
        };
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var result = await coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.RecoveryRequired, result.Outcome);
        Assert.AreEqual(0, lease.Writes);
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.RecoveryRequired, (await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!.Phase);
        int reads = lease.Reads;
        lease.Current = new(fixture.Baseline.LocalObject, fixture.Intent.RootId, fixture.Intent.RelativePath, false, Owner, Target, DateTimeOffset.UtcNow);
        await coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
        Assert.AreEqual(reads, lease.Reads);
        Assert.AreEqual(0, lease.Writes);
    }

    [TestMethod]
    public async Task UnknownPostWriteAclRetainsAppliedFactAndDoesNotBecomeVerified()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent) { AfterWrite = () => Unowned };
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var result = await coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.RecoveryRequired, result.Outcome);
        var record = await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId);
        Assert.IsNotNull(record!.AppliedAt);
        Assert.IsNull(record.Verification);
        Assert.AreEqual(MirrorPulseNamespacePermissionRecoveryReason.DaclChanged, record.RecoveryReason);
    }

    [TestMethod]
    public async Task CancellationAfterSuccessfulWriteStillRetainsIndependentVerification()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        using var cancellation = new CancellationTokenSource();
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent) { AfterWrite = () => { cancellation.Cancel(); return Target; } };
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        var result = await coordinator.ApplyAsync(fixture.Intent.OperationId, lease, cancellation.Token);
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, result.Outcome);
        Assert.AreEqual(2, lease.Reads);
        Assert.AreEqual(MirrorPulseNamespacePermissionPhase.Verified, (await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId))!.Phase);
    }

    [TestMethod]
    public async Task LaterUnknownAclDoesNotRewritePastVerificationOrRepeatWrite()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent);
        await using var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        await coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
        var original = await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId);
        lease.Current = lease.Current with { Dacl = Unowned };
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.RecoveryRequired, (await coordinator.ApplyAsync(fixture.Intent.OperationId, lease)).Outcome);
        Assert.AreEqual(original, await catalog.ReadNamespacePermissionChangeAsync(fixture.Intent.OperationId));
        Assert.AreEqual(1, lease.Writes);
    }

    [TestMethod]
    public async Task DisposalRejectsNewAdmissionAndDrainsTheAdmittedNativeWriteAndVerification()
    {
        using var fixture = new Fixture();
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
        await catalog.PrepareNamespacePermissionChangeAsync(fixture.Baseline, fixture.Intent);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new ObjectLease(fixture.Baseline, fixture.Intent) { BeforeWrite = async () => { entered.SetResult(); await release.Task; } };
        var coordinator = new MirrorPulseNamespacePermissionCoordinator(catalog);
        Task<MirrorPulseNamespacePermissionResult> operation = coordinator.ApplyAsync(fixture.Intent.OperationId, lease);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task disposing = coordinator.DisposeAsync().AsTask();
        try
        {
            Assert.IsFalse(disposing.IsCompleted);
            await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => coordinator.ApplyAsync(fixture.Intent.OperationId, lease));
        }
        finally { release.TrySetResult(); }
        Assert.AreEqual(MirrorPulseNamespacePermissionOutcome.Verified, (await operation.WaitAsync(TimeSpan.FromSeconds(2))).Outcome);
        await disposing.WaitAsync(TimeSpan.FromSeconds(2));
        await coordinator.DisposeAsync();
        Assert.AreEqual(1, lease.Writes);
        Assert.IsFalse(lease.Disposed);
    }

    private static string Canonical(string value) => new RawSecurityDescriptor(value).GetSddlForm(AccessControlSections.Access);

    private sealed class ObjectLease : IMirrorPulseNamespacePermissionLease
    {
        public MirrorPulseNamespacePermissionObject Current { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool Disposed { get; private set; }
        public bool FailAfterWrite { get; set; }
        public int FailRead { get; set; }
        public Func<Task>? BeforeWrite { get; set; }
        public Func<int, Task>? BeforeRead { get; set; }
        public Func<string>? AfterWrite { get; set; }
        public ObjectLease(MirrorPulseNamespacePermissionBaseline baseline, MirrorPulseNamespacePermissionIntent intent) =>
            Current = new(baseline.LocalObject, intent.RootId, intent.RelativePath, baseline.IsDirectory, baseline.OwnerSid, baseline.OriginalDacl, DateTimeOffset.UtcNow);
        public async ValueTask<MirrorPulseNamespacePermissionObject> InspectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            if (BeforeRead is not null) await BeforeRead(Reads);
            if (FailRead == Reads) throw new IOException("Synthetic read-back interruption.");
            return Current with { ObservedAt = DateTimeOffset.UtcNow };
        }
        public async ValueTask ApplyDaclAsync(string dacl, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BeforeWrite is not null) await BeforeWrite();
            Writes++;
            Current = Current with { Dacl = AfterWrite?.Invoke() ?? dacl };
            if (FailAfterWrite) throw new IOException("Synthetic interruption after the native write.");
        }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-permission-coordinator", Guid.NewGuid().ToString("N"));
        public MirrorPulseStoragePaths Paths { get; }
        public MirrorPulseNamespacePermissionBaseline Baseline { get; } = new(Guid.NewGuid(), RootId.New(),
            new(1, Guid.NewGuid(), Guid.NewGuid()), "Docs/edited.txt", false, Owner, Original, DateTimeOffset.UtcNow.AddMinutes(-1));
        public MirrorPulseNamespacePermissionIntent Intent { get; }
        public Fixture()
        {
            Paths = new(Path.Combine(_directory, "sync"), Path.Combine(_directory, "data"));
            Intent = new(Guid.NewGuid(), Baseline.EvidenceId, Baseline.LocalObject, Baseline.RootId, Baseline.RelativePath,
                MirrorPulseNamespacePermissionChangeKind.Protect, "S-1-5-5-123-456", Original, Target, Baseline.CapturedAt.AddSeconds(1));
        }
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
