using System.Text.Json;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Compatibility;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Conflicts;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Host;
using MirrorPulse.Core.Transport;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class MirrorPulseControlContractTests
{
    [TestMethod]
    public void CommandCatalogContainsUniqueStableNames()
    {
        var names = MirrorPulseControlCommands.Catalog.Select(command => command.Name).ToArray();

        Assert.AreEqual(names.Length, names.Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(names.All(name => name.Contains('.', StringComparison.Ordinal)));
        Assert.AreEqual(MirrorPulseControlCommands.HostStatus,
            MirrorPulseControlCommands.Catalog[0].Name);
    }

    [TestMethod]
    public void ProtocolAndExitCodesAreStable()
    {
        var schemaVersion = MirrorPulseControlSchema.CurrentVersion.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var mediaType = string.Concat(MirrorPulseControlSchema.MediaType);
        var success = MirrorPulseControlExitCodes.Success.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var internalFailure = MirrorPulseControlExitCodes.Internal.ToString(System.Globalization.CultureInfo.InvariantCulture);

        Assert.AreEqual("1", schemaVersion);
        Assert.AreEqual("application/vnd.mirrorpulse.control+json", mediaType);
        Assert.AreEqual("0", success);
        Assert.AreEqual("70", internalFailure);
        Assert.IsTrue(MirrorPulseControlErrorCodes.UnknownCommand.Contains("control", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SensitiveCommandsAreExplicitlyMarked()
    {
        var create = MirrorPulseControlCommands.Catalog.Single(command =>
            command.Name == MirrorPulseControlCommands.InstanceCreate);
        var settings = MirrorPulseControlCommands.Catalog.Single(command =>
            command.Name == MirrorPulseControlCommands.SettingsSet);

        Assert.IsTrue(create.AcceptsSensitiveInput);
        Assert.IsTrue(settings.AcceptsSensitiveInput);
        var list = MirrorPulseControlCommands.Catalog.Single(command =>
            command.Name == MirrorPulseControlCommands.AdapterList);
        Assert.IsFalse(list.AcceptsSensitiveInput);
    }

    [TestMethod]
    public void RequestResponseAndEventEnvelopesRoundTripThroughCanonicalJson()
    {
        using var argumentsDocument = JsonDocument.Parse("{\"enabled\":true}");
        var request = new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion,
            Guid.NewGuid(),
            MirrorPulseControlCommands.InstanceEnable,
            argumentsDocument.RootElement,
            "test-client");
        var requestRoundTrip = MirrorPulseControlJsonCodec.Deserialize<ControlRequestEnvelope>(
            MirrorPulseControlJsonCodec.Serialize(request));

        Assert.AreEqual(request.RequestId, requestRoundTrip.RequestId);
        Assert.AreEqual(request.Command, requestRoundTrip.Command);
        Assert.IsTrue(requestRoundTrip.Arguments.GetProperty("enabled").GetBoolean());

        var response = ControlResponseEnvelope.Failure(
            request.RequestId,
            new ControlError(MirrorPulseControlErrorCodes.HostUnavailable,
                "Host is unavailable.", ErrorCategory.Network, retryable: true, "diag-1"));
        var responseRoundTrip = MirrorPulseControlJsonCodec.Deserialize<ControlResponseEnvelope>(
            MirrorPulseControlJsonCodec.Serialize(response));
        Assert.IsFalse(responseRoundTrip.Succeeded);
        Assert.AreEqual(MirrorPulseControlErrorCodes.HostUnavailable, responseRoundTrip.Error!.Code);
        Assert.IsTrue(responseRoundTrip.Error.Retryable);

        using var dataDocument = JsonDocument.Parse("{\"phase\":\"running\"}");
        var @event = new ControlEventEnvelope(
            MirrorPulseControlSchema.CurrentVersion,
            request.RequestId,
            0,
            "sync.progress",
            dataDocument.RootElement,
            isTerminal: false);
        var eventRoundTrip = MirrorPulseControlJsonCodec.Deserialize<ControlEventEnvelope>(
            MirrorPulseControlControlJson(@event));
        Assert.AreEqual("sync.progress", eventRoundTrip.EventType);
    }

    [TestMethod]
    public void EnvelopesRejectUnsupportedVersionAndInvalidResponseShape()
    {
        using var arguments = JsonDocument.Parse("{}");
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ControlRequestEnvelope(
            0, Guid.NewGuid(), MirrorPulseControlCommands.HostStatus, arguments.RootElement));
        Assert.ThrowsExactly<ArgumentException>(() => new ControlResponseEnvelope(
            MirrorPulseControlSchema.CurrentVersion, Guid.NewGuid(), false));
    }

    [TestMethod]
    public async Task ControlTransportRoundTripsLengthPrefixedPayloads()
    {
        await using var stream = new MemoryStream();
        var payload = new byte[] { 1, 2, 3, 4 };
        await MirrorPulseControlPipeTransport.WriteFrameAsync(stream, payload);
        stream.Position = 0;

        var roundTrip = await MirrorPulseControlPipeTransport.ReadFrameAsync(stream);

        CollectionAssert.AreEqual(payload, roundTrip);
    }

    [TestMethod]
    public void CurrentUserPipeNameIsVersioned()
    {
        var pipeName = MirrorPulseControlPipeNames.CurrentUserV1();

        StringAssert.StartsWith(pipeName, "MirrorPulse-control-v1-");
    }

    [TestMethod]
    public void CommandArgumentsExposeTypedInstanceAndConflictShapes()
    {
        var instance = new InstanceCreateArguments(
            Guid.NewGuid().ToString("D"),
            "Documents",
            new Dictionary<string, string> { ["sourceDirectory"] = "C:\\Documents" },
            new Dictionary<string, string> { ["default"] = "Documents" },
            "secret",
            true);
        var conflict = new ConflictResolveArguments(
            Guid.NewGuid(), MirrorPulseConflictAction.KeepBoth, "conflicts/file.txt");

        Assert.AreEqual("Documents", instance.DisplayName);
        Assert.AreEqual(MirrorPulseConflictAction.KeepBoth, conflict.Action);
        Assert.IsTrue(typeof(MirrorPulseSensitiveDataAttribute).IsAssignableFrom(
            typeof(InstanceCreateArguments).GetProperty(nameof(InstanceCreateArguments.Secret))!
                .GetCustomAttributes(typeof(MirrorPulseSensitiveDataAttribute), inherit: true)
                .Single().GetType()));
    }

    private static byte[] MirrorPulseControlControlJson(ControlEventEnvelope value) =>
        MirrorPulseControlJsonCodec.Serialize(value);

    [TestMethod]
    public async Task DispatcherInvokesTypedHandlerAndReturnsStructuredData()
    {
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<InstanceEnableArguments, object>(
            MirrorPulseControlCommands.InstanceEnable,
            (arguments, _) => ValueTask.FromResult<object>(new { arguments.Enabled }));
        using var document = JsonDocument.Parse("{\"instanceId\":\"instance\",\"enabled\":true}");
        var request = new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion,
            Guid.NewGuid(),
            MirrorPulseControlCommands.InstanceEnable,
            document.RootElement);

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsTrue(response.Succeeded);
        Assert.IsTrue(response.Data!.Value.GetProperty("enabled").GetBoolean());
    }

    [TestMethod]
    public async Task DispatcherReturnsSafeErrorsForUnknownAndThrowingCommands()
    {
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ControlEmptyArguments, object>(
            MirrorPulseControlCommands.HostStatus,
            (_, _) => ValueTask.FromException<object>(new InvalidOperationException("secret-value")));
        using var document = JsonDocument.Parse("{}");
        var unknown = new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion, Guid.NewGuid(), "unknown.command", document.RootElement);
        var known = new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion, Guid.NewGuid(), MirrorPulseControlCommands.HostStatus,
            document.RootElement);

        var unknownResponse = await dispatcher.DispatchAsync(unknown);
        var knownResponse = await dispatcher.DispatchAsync(known);

        Assert.AreEqual(MirrorPulseControlErrorCodes.UnknownCommand, unknownResponse.Error!.Code);
        Assert.AreEqual(MirrorPulseControlErrorCodes.InternalFailure, knownResponse.Error!.Code);
        Assert.IsFalse(knownResponse.Error.Message.Contains("secret-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task LegacyBridgeRegistersStatusAndMutationCommands()
    {
        var expected = new MirrorPulseAppStatusResponse(3, 1, [], []);
        var dispatcher = new MirrorPulseControlDispatcher();
        var bridge = new MirrorPulseLegacyCommandBridge(
            _ => Task.FromResult(expected),
            (_, _) => Task.FromResult(expected),
            (_, _, _) => Task.FromResult(expected),
            (_, _, _) => Task.FromResult(expected),
            (_, _, _) => Task.FromResult(expected),
            (_, _) => Task.FromResult(expected),
            (_, _) => Task.FromResult(expected));
        bridge.Register(dispatcher);
        using var document = JsonDocument.Parse("{}");
        var request = new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion, Guid.NewGuid(),
            MirrorPulseControlCommands.SyncStatus, document.RootElement);

        var response = await dispatcher.DispatchAsync(request);

        Assert.IsTrue(dispatcher.RegisteredCommands.Contains(MirrorPulseControlCommands.InstanceCreate));
        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual(3, response.Data!.Value.GetProperty("pendingUploads").GetInt32());
    }

    [TestMethod]
    public async Task LegacyBridgeExposesHostLifecycleCommandsSeparatelyFromSyncStatus()
    {
        var hostStatus = new MirrorPulseHostStatus(
            "Running", 123, "MirrorPulse-control-test", DateTimeOffset.UtcNow,
            true, true);
        var expected = new MirrorPulseAppStatusResponse(0, 0, [], []);
        var dispatcher = new MirrorPulseControlDispatcher();
        var bridge = new MirrorPulseLegacyCommandBridge(
            _ => Task.FromResult(expected),
            hostStatus: _ => Task.FromResult(hostStatus),
            hostStart: (_, _) => Task.FromResult(hostStatus),
            hostStop: (_, _) => Task.FromResult(hostStatus with { RequestedAction = "stop" }),
            hostRestart: (_, _) => Task.FromResult(hostStatus with { RequestedAction = "restart" }));
        bridge.Register(dispatcher);
        using var document = JsonDocument.Parse("{}");

        var status = await dispatcher.DispatchAsync(new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion, Guid.NewGuid(),
            MirrorPulseControlCommands.HostStatus, document.RootElement));
        var restart = await dispatcher.DispatchAsync(new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion, Guid.NewGuid(),
            MirrorPulseControlCommands.HostRestart, document.RootElement));

        Assert.IsTrue(status.Succeeded);
        Assert.AreEqual("Running", status.Data!.Value.GetProperty("state").GetString());
        Assert.AreEqual("restart", restart.Data!.Value.GetProperty("requestedAction").GetString());
    }

    [TestMethod]
    public async Task LegacyBridgeExposesTopologyWithoutCredentialValues()
    {
        var topology = new MirrorPulseControlTopology(
            [new MirrorPulseControlInstallation(
                "local", Guid.NewGuid().ToString("D"), "1.0.0", "MirrorPulse",
                "C:\\Adapters\\local", new string('A', 64), "LocalFile", null,
                true, DateTimeOffset.UtcNow, "Installed", "0.1.0")],
            [new MirrorPulseControlInstance(
                "local", Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
                "Documents", new Dictionary<string, string> { ["sourceDirectory"] = "C:\\Documents" },
                ["credential-ref"], "C:\\Cache", "C:\\Transfer", true, "Enabled", null,
                DateTimeOffset.UtcNow)],
            [],
            []);
        var dispatcher = new MirrorPulseControlDispatcher();
        new MirrorPulseLegacyCommandBridge(
            _ => Task.FromResult(new MirrorPulseAppStatusResponse(0, 0, [], [])),
            topology: _ => Task.FromResult(topology)).Register(dispatcher);
        using var document = JsonDocument.Parse("{}");

        var response = await dispatcher.DispatchAsync(new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion, Guid.NewGuid(),
            MirrorPulseControlCommands.AdapterList, document.RootElement));

        Assert.IsTrue(response.Succeeded);
        Assert.AreEqual("credential-ref", response.Data!.Value
            .GetProperty("instances")[0].GetProperty("credentialReferences")[0].GetString());
        Assert.IsFalse(response.Data.Value.GetProperty("instances")[0].ToString()
            .Contains("secret", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public async Task LegacyBridgeExposesSettingsReadAndWriteCommands()
    {
        var settings = new MirrorPulseControlSettings(1, "en-US", false, false, [], "MirrorPulse");
        var dispatcher = new MirrorPulseControlDispatcher();
        new MirrorPulseLegacyCommandBridge(
            _ => Task.FromResult(new MirrorPulseAppStatusResponse(0, 0, [], [])),
            settingsGet: _ => Task.FromResult(settings),
            settingsSet: (update, _) => Task.FromResult(settings with
            {
                DeveloperMode = update.DeveloperMode ?? settings.DeveloperMode,
                Locale = update.Locale ?? settings.Locale,
            })).Register(dispatcher);
        using var getDocument = JsonDocument.Parse("{}");
        using var setDocument = JsonDocument.Parse("{\"developerMode\":true,\"locale\":\"zh-CN\"}");

        var get = await dispatcher.DispatchAsync(new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion, Guid.NewGuid(),
            MirrorPulseControlCommands.SettingsGet, getDocument.RootElement));
        var set = await dispatcher.DispatchAsync(new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion, Guid.NewGuid(),
            MirrorPulseControlCommands.SettingsSet, setDocument.RootElement));

        Assert.AreEqual("en-US", get.Data!.Value.GetProperty("locale").GetString());
        Assert.IsTrue(set.Data!.Value.GetProperty("developerMode").GetBoolean());
        Assert.AreEqual("zh-CN", set.Data.Value.GetProperty("locale").GetString());
    }

    [TestMethod]
    public async Task LegacyBridgeExposesDiagnosticsCollectionCommand()
    {
        var dispatcher = new MirrorPulseControlDispatcher();
        new MirrorPulseLegacyCommandBridge(
            _ => Task.FromResult(new MirrorPulseAppStatusResponse(0, 0, [], [])),
            diagnostics: (arguments, _) => Task.FromResult(new MirrorPulseControlDiagnosticsResult(
                arguments.OutputPath ?? "diagnostics.zip", DateTimeOffset.UtcNow, arguments.IncludeLogs)))
            .Register(dispatcher);
        using var document = JsonDocument.Parse("{\"includeLogs\":true,\"outputPath\":\"diagnostics.zip\"}");

        var response = await dispatcher.DispatchAsync(new ControlRequestEnvelope(
            MirrorPulseControlSchema.CurrentVersion, Guid.NewGuid(),
            MirrorPulseControlCommands.DiagnosticsCollect, document.RootElement));

        Assert.IsTrue(response.Succeeded);
        Assert.IsTrue(response.Data!.Value.GetProperty("includedLogs").GetBoolean());
        Assert.AreEqual("diagnostics.zip", response.Data.Value.GetProperty("packagePath").GetString());
    }

    [TestMethod]
    public async Task TypedClientSendsRequestAndDeserializesResponse()
    {
        var pipeName = $"MirrorPulse-control-test-{Guid.NewGuid():N}";
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ControlEmptyArguments, MirrorPulseAppStatusResponse>(
            MirrorPulseControlCommands.SyncStatus,
            (_, _) => ValueTask.FromResult(new MirrorPulseAppStatusResponse(4, 0, [], [])));
        using var shutdown = new CancellationTokenSource();
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipeName);
        var serverTask = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions
        {
            PipeName = pipeName,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            RequestTimeout = TimeSpan.FromSeconds(5)
        });

        var result = await client.GetStatusAsync();

        Assert.AreEqual(4, result.PendingUploads);
        shutdown.Cancel();
        await serverTask;
    }

    [TestMethod]
    public async Task TypedRootStatusPreservesNamespaceRecoveryOverTheCurrentUserPipe()
    {
        string name = $"MirrorPulse-root-status-{Guid.NewGuid():N}";
        string rootId = Guid.NewGuid().ToString("D");
        string renameId = Guid.NewGuid().ToString("D");
        var root = new MirrorPulseControlRoot("example.local", Guid.NewGuid().ToString("D"), rootId,
            "docs", "Documents", "Documents", false, "Active", DateTimeOffset.UtcNow);
        IReadOnlyList<MirrorPulseControlRootStatus> snapshot = [new(root, "NamespaceRecovery", true,
            new(renameId, "Documents", "My Files", "Prepared", DateTimeOffset.UtcNow))];
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<ControlEmptyArguments, IReadOnlyList<MirrorPulseControlRootStatus>>(
            MirrorPulseControlCommands.RootList, (_, _) => ValueTask.FromResult(snapshot));
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, name);
        Task serving = server.ServeAsync(shutdown.Token);
        try
        {
            var client = new MirrorPulseControlClient(new() { PipeName = name, ExpectedHostProcessId = Environment.ProcessId });
            MirrorPulseControlRootStatus actual = (await client.GetRootsAsync(shutdown.Token)).Single();
            Assert.AreEqual(rootId, actual.Root.RootId);
            Assert.AreEqual("NamespaceRecovery", actual.SyncState);
            Assert.IsTrue(actual.RequiresFullRescan);
            Assert.AreEqual(renameId, actual.PendingRename?.OperationId);
            Assert.AreEqual("My Files", actual.PendingRename?.TargetName);
        }
        finally
        {
            await shutdown.CancelAsync();
            await serving;
        }
    }

    [TestMethod]
    public async Task TypedClientCanInvokeHostStarterAfterInitialConnectionFailure()
    {
        var started = false;
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions
        {
            PipeName = $"MirrorPulse-control-missing-{Guid.NewGuid():N}",
            ConnectTimeout = TimeSpan.FromMilliseconds(50),
            RequestTimeout = TimeSpan.FromSeconds(2),
            EnsureHostStartedAsync = _ =>
            {
                started = true;
                return Task.CompletedTask;
            }
        });

        var exception = await Assert.ThrowsExactlyAsync<MirrorPulseControlException>(async () =>
            await client.GetStatusAsync());

        Assert.IsTrue(started);
        Assert.AreEqual(MirrorPulseControlErrorCodes.HostUnavailable, exception.Error.Code);
    }

    [TestMethod]
    public async Task TypedClientConvertsHostDisconnectToStructuredError()
    {
        var pipeName = $"MirrorPulse-control-disconnect-{Guid.NewGuid():N}";
        var acceptTask = Task.Run(async () =>
        {
            await using var server = SecureNamedPipeServerFactory.Create(new NamedPipeServerOptions(pipeName));
            await server.WaitForConnectionAsync();
        });
        var client = new MirrorPulseControlClient(new MirrorPulseControlClientOptions
        {
            PipeName = pipeName,
            ConnectTimeout = TimeSpan.FromSeconds(2),
            RequestTimeout = TimeSpan.FromSeconds(5)
        });

        var exception = await Assert.ThrowsExactlyAsync<MirrorPulseControlException>(async () =>
            await client.GetStatusAsync());
        await acceptTask;

        Assert.AreEqual(MirrorPulseControlErrorCodes.HostUnavailable, exception.Error.Code);
    }

    [TestMethod]
    public async Task HostLocatorReportsMissingCurrentUserHost()
    {
        var locator = new MirrorPulseHostLocator($"MirrorPulse-control-locator-{Guid.NewGuid():N}");

        bool running = await locator.IsRunningAsync(TimeSpan.FromMilliseconds(50));

        Assert.IsFalse(running);
    }

    [TestMethod]
    public async Task HostStartupCoordinatorRejectsUntrustedDevelopmentPath()
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-host-start", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string executable = Path.Combine(root, "MirrorPulse.Host.exe");
        await File.WriteAllTextAsync(executable, "test");
        await using var coordinator = new MirrorPulseHostStartupCoordinator(
            options: new MirrorPulseHostStartupOptions
            {
                DevelopmentHostPath = executable,
                DeveloperMode = false,
            });

        var exception = await Assert.ThrowsExactlyAsync<MirrorPulseControlException>(async () =>
            await coordinator.EnsureStartedAsync());

        Assert.AreEqual(MirrorPulseControlErrorCodes.HostPathUntrusted, exception.Error.Code);
        Directory.Delete(root, recursive: true);
    }

    [TestMethod]
    public async Task HostStartupCoordinatorReportsMissingTrustedHost()
    {
        await using var coordinator = new MirrorPulseHostStartupCoordinator(
            options: new MirrorPulseHostStartupOptions
            {
                InstalledHostPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Host.exe"),
            });

        var exception = await Assert.ThrowsExactlyAsync<MirrorPulseControlException>(async () =>
            await coordinator.EnsureStartedAsync());

        Assert.AreEqual(MirrorPulseControlErrorCodes.HostExecutableNotFound, exception.Error.Code);
    }
}
