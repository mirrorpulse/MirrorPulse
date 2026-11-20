using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CloudFiles.CfSharp.Tests;

[TestClass]
public sealed class MirrorPulseRetainedRemoteBatchRecoveryPumpTests
{
    [TestMethod]
    public async Task RealCatalogPagesRotatePastOfflineFailedAndUnchangedOwnersWithoutReplacingInput()
    {
        await using var fixture = await Fixture.CreateAsync(135);
        var calls = new List<InstanceId>();
        await using var pump = new MirrorPulseRetainedRemoteBatchRecoveryPump(fixture.Catalog,
            instance => instance != fixture.Owners[0], (instance, _) =>
            {
                calls.Add(instance);
                if (instance == fixture.Owners[1]) throw new IOException("Transient original-input recovery failure.");
                return ValueTask.FromResult(false);
            });
        await pump.RecoverOnceAsync(CancellationToken.None);
        CollectionAssert.AreEqual(fixture.Owners.Skip(1).Take(63).ToArray(), calls.ToArray());
        calls.Clear(); await pump.RecoverOnceAsync(CancellationToken.None);
        CollectionAssert.AreEqual(fixture.Owners.Skip(64).Take(64).ToArray(), calls.ToArray());
        calls.Clear(); await pump.RecoverOnceAsync(CancellationToken.None);
        CollectionAssert.AreEqual(fixture.Owners.Skip(128).ToArray(), calls.ToArray());
        calls.Clear(); await pump.RecoverOnceAsync(CancellationToken.None);
        CollectionAssert.AreEqual(fixture.Owners.Skip(1).Take(63).ToArray(), calls.ToArray());
        foreach (var owner in fixture.Owners)
            CollectionAssert.AreEqual(Fixture.Payload, (await fixture.Catalog.ReadRetainedRemoteBatchAsync(owner))!.Payload);
    }

    [TestMethod]
    public async Task FailedStatusWriteAndUnrelatedCancellationCannotPreventTheNextOwner()
    {
        await using var fixture = await Fixture.CreateAsync(3);
        await using var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Paths.ProductCatalogDatabasePath, Pooling = false }.ToString());
        await database.OpenAsync();
        await using var command = database.CreateCommand();
        command.CommandText = "CREATE TRIGGER fail_recovery_status BEFORE INSERT ON instance_runtime BEGIN SELECT RAISE(ABORT,'Synthetic status failure'); END;";
        await command.ExecuteNonQueryAsync();
        var calls = new List<InstanceId>();
        await using var pump = new MirrorPulseRetainedRemoteBatchRecoveryPump(fixture.Catalog, null, (instance, _) =>
        {
            calls.Add(instance);
            if (instance == fixture.Owners[0]) throw new IOException("Original recovery failed before status.");
            if (instance == fixture.Owners[1]) throw new OperationCanceledException("An unrelated operation was canceled.");
            return ValueTask.FromResult(false);
        });
        await pump.RecoverOnceAsync(CancellationToken.None);
        CollectionAssert.AreEqual(fixture.Owners, calls.ToArray());
        foreach (var owner in fixture.Owners) Assert.IsNotNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(owner));
    }

    [TestMethod]
    public async Task TimerRetriesAndCompletesOriginalInputWithoutAnyNewIncomingEvent()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using var pump = new MirrorPulseRetainedRemoteBatchRecoveryPump(fixture.Catalog, null, async (instance, token) =>
        {
            if (++calls == 1) return false;
            var original = await fixture.Catalog.ReadRetainedRemoteBatchAsync(instance, token);
            CollectionAssert.AreEqual(Fixture.Payload, original!.Payload);
            await fixture.Catalog.CompleteRetainedRemoteBatchAsync(original, token);
            completed.TrySetResult(); return true;
        }, TimeSpan.FromMilliseconds(1));
        await pump.RecoverOnceAsync(CancellationToken.None);
        Assert.IsNotNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Owners[0]));
        pump.Start(); await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pump.DisposeAsync();
        Assert.AreEqual(2, calls);
        Assert.IsNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Owners[0]));
        Assert.ThrowsExactly<ObjectDisposedException>(pump.Start);
    }

    [TestMethod]
    public async Task ShutdownCancelsQueuedInstanceAdmissionWithoutTakingItsLibrary()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        await using var scheduler = new MirrorPulseInstanceScheduler();
        await using var held = await scheduler.AcquireAsync(fixture.Owners);
        var queued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int libraryCalls = 0;
        ValueTask<bool> WithLibrary(Func<CancellationToken, ValueTask<bool>> action, CancellationToken token)
        { libraryCalls++; return action(token); }
        await using var pump = new MirrorPulseRetainedRemoteBatchRecoveryPump(fixture.Catalog, null, (instance, token) =>
        {
            queued.TrySetResult();
            return MirrorPulseRuntimeInstanceDispatcher.RunAsync(scheduler, instance, WithLibrary,
                _ => ValueTask.FromResult(true), token);
        }, TimeSpan.FromMilliseconds(1));
        pump.Start(); await queued.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await pump.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, libraryCalls);
        Assert.IsNotNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Owners[0]));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ShutdownWaitsForAnAdmittedRecoveryToReleaseItsCurrentOwner(bool faultingCancellationCallback)
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var pump = new MirrorPulseRetainedRemoteBatchRecoveryPump(fixture.Catalog, null, async (_, token) =>
        {
            entered.TrySetResult();
            using var registration = token.Register(() =>
            {
                canceled.TrySetResult();
                if (faultingCancellationCallback) throw new IOException("A synthetic cancellation callback failure.");
            });
            await release.Task;
            token.ThrowIfCancellationRequested(); return true;
        }, TimeSpan.FromMilliseconds(1));
        pump.Start(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var drain = pump.DisposeAsync().AsTask();
        try
        {
            await canceled.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.IsFalse(drain.IsCompleted);
        }
        finally { release.TrySetResult(); await drain.WaitAsync(TimeSpan.FromSeconds(5)); }
        Assert.IsNotNull(await fixture.Catalog.ReadRetainedRemoteBatchAsync(fixture.Owners[0]));
    }

    [TestMethod]
    public async Task NewOwnerBehindThePageCursorIsVisitedAfterWrapAndCompletedOwnersDisappear()
    {
        await using var fixture = await Fixture.CreateAsync(2);
        var seen = new List<InstanceId>();
        await using var pump = new MirrorPulseRetainedRemoteBatchRecoveryPump(fixture.Catalog, null, async (instance, token) =>
        {
            seen.Add(instance);
            var original = (await fixture.Catalog.ReadRetainedRemoteBatchAsync(instance, token))!;
            await fixture.Catalog.CompleteRetainedRemoteBatchAsync(original, token); return true;
        });
        await pump.RecoverOnceAsync(CancellationToken.None);
        CollectionAssert.AreEqual(fixture.Owners, seen.ToArray());
        var late = InstanceId.Parse("00000000-0000-0000-0000-000000000001");
        await fixture.Catalog.RetainRemoteBatchAsync(new(late, late + "/original", Fixture.Payload));
        seen.Clear(); await pump.RecoverOnceAsync(CancellationToken.None);
        CollectionAssert.AreEqual(new[] { late }, seen.ToArray());
        seen.Clear(); await pump.RecoverOnceAsync(CancellationToken.None); Assert.IsEmpty(seen);
    }

    [TestMethod]
    public async Task OverlappingTurnsWaitAndCancelWithoutConcurrentOriginalDispatch()
    {
        await using var fixture = await Fixture.CreateAsync(1);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        await using var pump = new MirrorPulseRetainedRemoteBatchRecoveryPump(fixture.Catalog, null, async (_, token) =>
        { calls++; entered.TrySetResult(); await release.Task.WaitAsync(token); return false; });
        using var cancel = new CancellationTokenSource();
        var first = pump.RecoverOnceAsync(CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = pump.RecoverOnceAsync(cancel.Token);
            cancel.Cancel(); await Assert.ThrowsAsync<OperationCanceledException>(() => second);
            Assert.AreEqual(1, calls);
        }
        finally { release.TrySetResult(); await first.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        internal static readonly byte[] Payload = [1, 2, 3]; // Opaque to the scheduler; only the actual replay coordinator interprets it.
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-retained-retry", Guid.NewGuid().ToString("N"));
        internal MirrorPulseStoragePaths Paths { get; private set; } = null!;
        internal MirrorPulseProductCatalog Catalog { get; private set; } = null!;
        internal InstanceId[] Owners { get; private set; } = [];
        internal static async Task<Fixture> CreateAsync(int count)
        {
            var fixture = new Fixture();
            fixture.Paths = new(Path.Combine(fixture._directory, "sync"), Path.Combine(fixture._directory, "data"));
            fixture.Catalog = await MirrorPulseProductCatalog.OpenAsync(fixture.Paths);
            fixture.Owners = Enumerable.Range(0, count).Select(_ => InstanceId.New()).OrderBy(owner => owner.ToString(), StringComparer.Ordinal).ToArray();
            foreach (var owner in fixture.Owners)
                await fixture.Catalog.RetainRemoteBatchAsync(new(owner, owner + "/original", Payload));
            return fixture;
        }
        public async ValueTask DisposeAsync() { await Catalog.DisposeAsync(); Directory.Delete(_directory, true); }
    }
}
