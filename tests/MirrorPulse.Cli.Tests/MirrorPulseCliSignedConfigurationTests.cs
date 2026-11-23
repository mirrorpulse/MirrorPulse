using MirrorPulse.Cli;
using MirrorPulse.Control.Client;
using MirrorPulse.Control.Contracts;
using MirrorPulse.Control.Dispatch;
using MirrorPulse.Control.Transport;
using MirrorPulse.Core.Host;

namespace MirrorPulse.Cli.Tests;

[TestClass]
public sealed class MirrorPulseCliSignedConfigurationTests
{
    [TestMethod]
    public async Task CheckingAndInstallingAnUpdateAreSeparateControlCommands()
    {
        Guid offerId = Guid.NewGuid();
        string instanceId = Guid.NewGuid().ToString("D");
        string pipe = "MirrorPulse-cli-update-" + Guid.NewGuid().ToString("N");
        int installs = 0;
        var dispatcher = new MirrorPulseControlDispatcher();
        dispatcher.Register<InstanceIdArguments, AdapterUpdateCheck>(MirrorPulseControlCommands.InstanceCheckUpdate, (request, _) =>
        {
            Assert.AreEqual(instanceId, request.InstanceId);
            return ValueTask.FromResult(new AdapterUpdateCheck(AdapterUpdateAvailability.Available, "1.0.0", "1.1.0", offerId,
                "https://example.test/adapter.mpadapter"));
        });
        dispatcher.Register<InstanceInstallUpdateArguments, AdapterUpdateInstallation>(MirrorPulseControlCommands.InstanceInstallUpdate,
            (request, _) =>
            {
                Assert.AreEqual(offerId, request.OfferId);
                Interlocked.Increment(ref installs);
                return ValueTask.FromResult(new AdapterUpdateInstallation(Guid.NewGuid().ToString("D"), "1.1.0"));
            });
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serving = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new() { PipeName = pipe, ExpectedHostProcessId = Environment.ProcessId });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.AreEqual(0, await MirrorPulseCliApplication.RunAsync(["--no-start", "--json", "instance", "check-update",
                "--instance-id", instanceId], output, error, hostOperations: operations, isPlatformSupported: () => true));
            StringAssert.Contains(output.ToString(), offerId.ToString("D"));
            Assert.AreEqual(0, Volatile.Read(ref installs));
            Assert.AreEqual(0, await MirrorPulseCliApplication.RunAsync(["--no-start", "instance", "install-update", offerId.ToString("D")],
                output, error, hostOperations: operations, isPlatformSupported: () => true));
            StringAssert.Contains(output.ToString(), "Installed version 1.1.0");
            Assert.AreEqual(1, Volatile.Read(ref installs));
            Assert.AreEqual(string.Empty, error.ToString());
        }
        finally
        {
            await shutdown.CancelAsync();
            await serving;
        }
    }

    [TestMethod]
    public async Task AuthorizationCommandsUseTheSharedChannelAndPassOnlyReceiptIdsToSave()
    {
        Guid authorizationId = Guid.NewGuid();
        string installId = Guid.NewGuid().ToString("D");
        string deploymentId = Guid.NewGuid().ToString("D");
        string pipe = "MirrorPulse-cli-authorization-" + Guid.NewGuid().ToString("N");
        var dispatcher = new MirrorPulseControlDispatcher();
        var pending = new AdapterAuthorizationStatus(authorizationId, deploymentId, "sign-in",
            DateTimeOffset.UtcNow.AddMinutes(5), AdapterAuthorizationPhase.Pending, new Uri("https://example.test/authorize"));
        dispatcher.Register<InstanceAuthorizationStartArguments, AdapterAuthorizationStatus>(MirrorPulseControlCommands.InstanceAuthorizationStart,
            (request, _) =>
            {
                Assert.AreEqual(expected: installId, actual: request.InstallId);
                Assert.AreEqual("sign-in", request.CallbackId);
                Assert.AreEqual("https://example.test/", request.Configuration["source"]);
                return ValueTask.FromResult(pending);
            });
        dispatcher.Register<InstanceAuthorizationArguments, AdapterAuthorizationStatus>(MirrorPulseControlCommands.InstanceAuthorizationPoll,
            (request, _) =>
            {
                Assert.AreEqual(expected: authorizationId, actual: request.AuthorizationId);
                return ValueTask.FromResult(pending with { Phase = AdapterAuthorizationPhase.ReadyToSave, InteractionUri = null });
            });
        dispatcher.Register<InstanceAuthorizationArguments, AdapterAuthorizationStatus>(MirrorPulseControlCommands.InstanceAuthorizationCancel,
            (request, _) =>
            {
                Assert.AreEqual(expected: authorizationId, actual: request.AuthorizationId);
                return ValueTask.FromResult(pending with { Phase = AdapterAuthorizationPhase.Cancelled, InteractionUri = null });
            });
        dispatcher.Register<InstanceCreateArguments, MirrorPulseAppStatusResponse>(MirrorPulseControlCommands.InstanceCreate,
            (request, _) =>
            {
                Assert.AreEqual(expected: authorizationId, actual: request.AuthorizationIds!.Single());
                Assert.IsNull(request.SecretFields);
                return ValueTask.FromResult(new MirrorPulseAppStatusResponse(0, 0, [], [], CreatedInstanceId: deploymentId));
            });
        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serving = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new() { PipeName = pipe, ExpectedHostProcessId = Environment.ProcessId });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            await RunAsync(11, "https://example.test/authorize", "auth-start", "--install-id", installId, "--callback", "sign-in",
                "--config", "source=https://example.test/");
            await RunAsync(0, "ReadyToSave", "auth-poll", authorizationId.ToString("D"));
            await RunAsync(0, deploymentId, "create", "--install-id", installId, "--name", "Source",
                "--authorization", authorizationId.ToString("D"));
            await RunAsync(MirrorPulseControlExitCodes.Cancelled, "Cancelled", "auth-cancel", authorizationId.ToString("D"));
        }
        finally
        {
            await shutdown.CancelAsync();
            await serving;
        }

        async Task RunAsync(int expectedExit, string expectedText, params string[] arguments)
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exit = await MirrorPulseCliApplication.RunAsync(["--no-start", "instance", .. arguments], output,
                error, hostOperations: operations, isPlatformSupported: () => true);
            Assert.AreEqual(expectedExit, exit);
            StringAssert.Contains(output.ToString(), expectedText);
            Assert.AreEqual(string.Empty, error.ToString());
        }
    }

    [TestMethod]
    public async Task SendsNamedPasswordChangesAndRevisionThroughTheSharedControlChannel()
    {
        Guid expectedRevision = Guid.NewGuid();
        string instanceId = Guid.NewGuid().ToString("D");
        string pipe = "MirrorPulse-signed-configuration-" + Guid.NewGuid().ToString("N");
        var dispatcher = new MirrorPulseControlDispatcher();
        int saves = 0;
        dispatcher.Register<InstanceConfigureArguments, MirrorPulseAppStatusResponse>(MirrorPulseControlCommands.InstanceConfigure,
            (request, _) =>
            {
                saves++;
                Guid? actualRevision = request.ExpectedConfigurationRevision;
                Assert.AreEqual<Guid?>(expectedRevision, actualRevision);
                Assert.AreEqual(expected: instanceId, actual: request.InstanceId);
                Assert.AreEqual("one,two", request.Configuration["folders"]);
                Assert.AreEqual("fixture=password", request.SecretFields!["password"]);
                Assert.IsNull(request.SecretFields["pin"]);
                Assert.IsFalse(request.Configuration.ContainsKey("password"));
                return ValueTask.FromResult<MirrorPulseAppStatusResponse>(new(0, 0, [], []));
            });

        using var shutdown = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = new MirrorPulseControlPipeServer(dispatcher.DispatchAsync, pipe);
        Task serving = server.ServeAsync(shutdown.Token);
        var client = new MirrorPulseControlClient(new() { PipeName = pipe, ExpectedHostProcessId = Environment.ProcessId });
        await using var operations = new MirrorPulseCliHostOperations(new MirrorPulseHostStartupOptions(), client);
        try
        {
            string[] command = ["--no-start", "--json", "instance", "configure", "--instance-id", instanceId,
                "--name", "Documents", "--revision", expectedRevision.ToString("D"), "--config", "folders=one,two",
                "--secret-field", "password=fixture=password"];
            using var output = new StringWriter();
            using var error = new StringWriter();
            int exit = await MirrorPulseCliApplication.RunAsync([.. command, "--clear-secret-field", "pin"],
                output, error, hostOperations: operations, isPlatformSupported: () => true);
            Assert.AreEqual(0, exit);
            Assert.IsFalse(output.ToString().Contains("fixture=password", StringComparison.Ordinal));
            Assert.AreEqual(string.Empty, error.ToString());

            exit = await MirrorPulseCliApplication.RunAsync([.. command, "--clear-secret-field", "password"],
                output, error, hostOperations: operations, isPlatformSupported: () => true);
            Assert.AreEqual(2, exit);
            Assert.IsFalse(error.ToString().Contains("fixture=password", StringComparison.Ordinal));
            Assert.AreEqual(1, saves);
        }
        finally
        {
            await shutdown.CancelAsync();
            await serving;
        }
    }
}
