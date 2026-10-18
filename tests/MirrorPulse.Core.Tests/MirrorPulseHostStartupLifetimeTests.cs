using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseHostStartupLifetimeTests
{
    [TestMethod]
    public void ExistingVersionOneHostStatusDoesNotRequireTheNewStartupPhaseField()
    {
        var status = MirrorPulseControlJsonCodec.Deserialize<MirrorPulseHostStatus>("""
            {"state":"Running","processId":123,"controlPipeName":"fixture","startedAt":"2026-10-18T00:00:00Z","canStop":true,"canRestart":true}
            """u8);
        Assert.AreEqual("Running", status.State); Assert.AreEqual(123, status.ProcessId);
        Assert.IsTrue(status.CanStop); Assert.IsTrue(status.CanRestart); Assert.IsNull(status.StartupPhase);
    }

    [TestMethod]
    public async Task ControlIsQueryableBeforeInitializationAndDoesNotAdmitSdkCommandsUntilReady()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MirrorPulseLifecycleState state = MirrorPulseLifecycleState.Starting;
        int mutations = 0;
        string pipe = "MirrorPulse-startup-" + Guid.NewGuid().ToString("N");
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ControlEmptyArguments, MirrorPulseHostStatus>(MirrorPulseControlCommands.HostStatus,
            (_, _) => ValueTask.FromResult(Status(state, pipe)));
        dispatcher.Register<ControlEmptyArguments, string>(MirrorPulseControlCommands.NamespaceMemberCreate,
            (_, _) => { Interlocked.Increment(ref mutations); return ValueTask.FromResult("created"); });
        var server = new MirrorPulseControlPipeServer((request, token) => MirrorPulseControlStartupAdmission.Reject(state, request) is { } rejection
            ? ValueTask.FromResult(rejection) : dispatcher.DispatchAsync(request, token), pipe);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task lifetime = MirrorPulseHostStartupLifetime.RunAsync(server.ServeAsync, async token =>
        {
            entered.TrySetResult(); await release.Task.WaitAsync(token); state = MirrorPulseLifecycleState.Running;
        }, shutdown.Token);
        var client = new MirrorPulseControlClient(new() { PipeName = pipe, RequestTimeout = TimeSpan.FromSeconds(3) });
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var starting = await client.GetHostStatusAsync();
            Assert.AreEqual("Starting", starting.State); Assert.IsTrue(starting.CanStop); Assert.AreEqual("CloudFiles", starting.StartupPhase);
            var failure = await Assert.ThrowsExactlyAsync<MirrorPulseControlException>(() => client.SendAsync<ControlEmptyArguments, string>(
                MirrorPulseControlCommands.NamespaceMemberCreate, new()));
            Assert.AreEqual(MirrorPulseControlErrorCodes.HostBusy, failure.Error.Code); Assert.IsTrue(failure.Error.Retryable); Assert.AreEqual(0, mutations);
            Task<MirrorPulseHostStatus> ready = client.WaitForHostReadyAsync();
            Assert.IsFalse(ready.IsCompleted); release.TrySetResult();
            Assert.AreEqual("Running", (await ready).State);
            Assert.AreEqual("created", await client.SendAsync<ControlEmptyArguments, string>(MirrorPulseControlCommands.NamespaceMemberCreate, new()));
            Assert.AreEqual(1, mutations);
        }
        finally { release.TrySetResult(); shutdown.Cancel(); await lifetime; }
    }

    [TestMethod]
    public async Task StopDuringInitializationWaitsForAdmittedWorkToActuallyDrain()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var shutdown = new CancellationTokenSource();
        Task lifetime = MirrorPulseHostStartupLifetime.RunAsync(async token => await Task.Delay(Timeout.InfiniteTimeSpan, token), async token =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            entered.TrySetResult(); await release.Task; token.ThrowIfCancellationRequested();
        }, shutdown.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); shutdown.Cancel();
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.IsFalse(lifetime.IsCompleted);
        release.TrySetResult(); await Assert.ThrowsAsync<OperationCanceledException>(async () => await lifetime);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task AnUnclaimedControlListenerNeverStartsNamespaceInitialization(bool synchronous)
    {
        var original = new IOException("fixture listener claim failed"); int starts = 0;
        Task Serve(CancellationToken _) => synchronous ? throw original : Task.FromException(original);
        var observed = await Assert.ThrowsExactlyAsync<IOException>(() => MirrorPulseHostStartupLifetime.RunAsync(Serve,
            _ => { starts++; return Task.CompletedTask; }));
        Assert.AreSame(original, observed); Assert.AreEqual(0, starts);
    }

    [TestMethod]
    public async Task IndependentInitializationAndListenerFailuresArePreservedOnceAfterDrain()
    {
        var listener = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new IOException("fixture control failure"); var second = new InvalidDataException("fixture initialization drain failure");
        Task lifetime = MirrorPulseHostStartupLifetime.RunAsync(_ => listener.Task, async token =>
        {
            using var registration = token.Register(() => canceled.TrySetResult());
            entered.TrySetResult(); await release.Task; throw second;
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); listener.TrySetException(first);
        await canceled.Task.WaitAsync(TimeSpan.FromSeconds(2)); Assert.IsFalse(lifetime.IsCompleted); release.TrySetResult();
        var errors = await Assert.ThrowsExactlyAsync<AggregateException>(async () => await lifetime);
        Assert.HasCount(2, errors.InnerExceptions); Assert.AreSame(first, errors.InnerExceptions[0]); Assert.AreSame(second, errors.InnerExceptions[1]);
    }

    [TestMethod]
    public async Task InitializationFailureClosesAndDrainsItsPublishedListener()
    {
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new InvalidDataException("fixture startup failure");
        var observed = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => MirrorPulseHostStartupLifetime.RunAsync(async token =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { canceled.TrySetResult(); }
        }, _ => Task.FromException(failure)));
        Assert.AreSame(failure, observed); Assert.IsTrue(canceled.Task.IsCompleted);
    }

    [TestMethod]
    public async Task CancelBeforeAdmissionDoesNotPublishOrInitializeAnything()
    {
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => MirrorPulseHostStartupLifetime.RunAsync(
            _ => throw new AssertFailedException("No listener is admitted."), _ => throw new AssertFailedException("No namespace work is admitted."), stop.Token));
    }

    [TestMethod]
    [DataRow("Failed")]
    [DataRow("Stopped")]
    [DataRow("Stopping")]
    [DataRow("Unknown")]
    public async Task ReadinessDoesNotMistakeAStoppedFailedOrUnknownHostForRunning(string state)
    {
        await WithStatusPipeAsync(() => new(state, Environment.ProcessId, "fixture", DateTimeOffset.UtcNow, false, false), async client =>
        {
            var failure = await Assert.ThrowsExactlyAsync<MirrorPulseControlException>(() => client.WaitForHostReadyAsync());
            Assert.AreEqual(MirrorPulseControlErrorCodes.HostStartFailed, failure.Error.Code);
        });
    }

    [TestMethod]
    public async Task ReadinessTimeoutLeavesTheActualControlListenerQueryableAndAbleToComplete()
    {
        string state = "Starting";
        await WithStatusPipeAsync(() => new(state, Environment.ProcessId, "fixture", DateTimeOffset.UtcNow, true, true, StartupPhase: "CloudFiles"), async client =>
        {
            var failure = await Assert.ThrowsExactlyAsync<MirrorPulseControlException>(() => client.WaitForHostReadyAsync());
            Assert.AreEqual(MirrorPulseControlErrorCodes.RequestTimeout, failure.Error.Code); Assert.IsTrue(failure.Error.Retryable);
            Assert.AreEqual("Starting", (await client.GetHostStatusAsync()).State);
            state = "Running"; Assert.AreEqual("Running", (await client.WaitForHostReadyAsync()).State);
        }, TimeSpan.FromMilliseconds(250));
    }

    [TestMethod]
    public async Task ReadinessCancellationDoesNotCancelTheHostInitializationOwner()
    {
        await WithStatusPipeAsync(() => new("Starting", Environment.ProcessId, "fixture", DateTimeOffset.UtcNow, true, true), async client =>
        {
            using var stop = new CancellationTokenSource(); Task waiting = client.WaitForHostReadyAsync(stop.Token); stop.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);
            Assert.AreEqual("Starting", (await client.GetHostStatusAsync()).State);
        });
    }

    private static MirrorPulseHostStatus Status(MirrorPulseLifecycleState state, string pipe) =>
        new(state.ToString(), Environment.ProcessId, pipe, DateTimeOffset.UtcNow, true, true,
            StartupPhase: state == MirrorPulseLifecycleState.Starting ? "CloudFiles" : null);

    private static async Task WithStatusPipeAsync(Func<MirrorPulseHostStatus> status, Func<MirrorPulseControlClient, Task> verify, TimeSpan? timeout = null)
    {
        string pipe = "MirrorPulse-ready-" + Guid.NewGuid().ToString("N");
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ControlEmptyArguments, MirrorPulseHostStatus>(MirrorPulseControlCommands.HostStatus, (_, _) => ValueTask.FromResult(status()));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        Task serving = server.ServeAsync(shutdown.Token);
        try { await verify(new(new() { PipeName = pipe, RequestTimeout = timeout ?? TimeSpan.FromSeconds(2) })); }
        finally { shutdown.Cancel(); await serving; }
    }
}
