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
