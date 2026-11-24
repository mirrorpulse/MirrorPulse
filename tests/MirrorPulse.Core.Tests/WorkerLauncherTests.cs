using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Workers;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class WorkerLauncherTests
{
    private static readonly string[] ExitArguments = ["/c", "exit 0"];

    [TestMethod]
    public void PackagedWorkerLaunchPathsRequireTheOpenedFileInItsOwnInstallation()
    {
        string root = Directory.CreateTempSubdirectory("MirrorPulse-launch-").FullName;
        try
        {
            string installation = Directory.CreateDirectory(Path.Combine(root, "install")).FullName;
            string worker = Path.Combine(Directory.CreateDirectory(Path.Combine(installation, "worker")).FullName, "worker.exe");
            File.WriteAllText(worker, "fixture");
            var request = new WorkerLaunchRequest(InstanceId.New(), WorkerSessionId.New(), worker, installation);
            var (executable, workingDirectory) = PackagedWorkerPaths.ResolvePackageFiles(request);
            Assert.AreEqual(worker, executable, ignoreCase: true);
            Assert.AreEqual(installation, workingDirectory, ignoreCase: true);
            var outside = new WorkerLaunchRequest(request.InstanceId, request.WorkerSessionId, Path.Combine(root, "outside.exe"), installation);
            Assert.ThrowsExactly<InvalidDataException>(() => PackagedWorkerPaths.ResolvePackageFiles(outside));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TestMethod]
    public async Task LauncherStartsProcessWithInstanceMetadata()
    {
        var request = new WorkerLaunchRequest(
            InstanceId.New(),
            WorkerSessionId.New(),
            Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            AppContext.BaseDirectory,
            ExitArguments,
            new Dictionary<string, string>());
        using var worker = WorkerProcessLauncher.Start(request);

        await worker.WaitForExitAsync();

        Assert.AreEqual(request.InstanceId, worker.InstanceId);
        Assert.AreEqual(request.WorkerSessionId, worker.WorkerSessionId);
        Assert.IsTrue(worker.Process.HasExited);
        Assert.AreEqual(0, worker.Process.ExitCode);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task WorkerCannotSeeInheritedSecretsOrInjectedRuntimeEnvironment()
    {
        const string marker = "MIRRORPULSE_TEST_PARENT_SECRET";
        Environment.SetEnvironmentVariable(marker, "fixture-marker");
        try
        {
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "cmd.exe");
            var request = new WorkerLaunchRequest(InstanceId.New(), WorkerSessionId.New(), executable,
                AppContext.BaseDirectory, ["/d", "/c",
                    "if defined MIRRORPULSE_TEST_PARENT_SECRET (exit 17) else if defined GITHUB_TOKEN (exit 18) else if defined DOTNET_STARTUP_HOOKS (exit 19) else if not defined SystemRoot (exit 20) else (exit 0)"]);
            using WorkerProcessHandle worker = WorkerProcessLauncher.Start(request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.WaitForExitAsync(timeout.Token);
            Assert.AreEqual(0, worker.Process.ExitCode);

            foreach (string key in new[] { "PATH", "GITHUB_TOKEN", "DOTNET_STARTUP_HOOKS", "COMPlus_ReadyToRun", "MP_UNKNOWN" })
            {
                Assert.ThrowsExactly<InvalidDataException>(() => WorkerProcessLauncher.Start(new WorkerLaunchRequest(
                    request.InstanceId, request.WorkerSessionId, executable, AppContext.BaseDirectory, ExitArguments,
                    new Dictionary<string, string> { [key] = "fixture" })));
            }
        }
        finally { Environment.SetEnvironmentVariable(marker, null); }
    }

    [TestMethod]
    public void LaunchRequestCopiesArgumentsAndEnvironment()
    {
        var arguments = new[] { "--pipe", "mirrorpulse" };
        var environment = new Dictionary<string, string> { ["MP_MODE"] = "worker" };
        var request = new WorkerLaunchRequest(
            InstanceId.New(),
            WorkerSessionId.New(),
            Environment.ProcessPath!,
            AppContext.BaseDirectory,
            arguments,
            environment);

        arguments[0] = "changed";
        environment["MP_MODE"] = "changed";

        Assert.AreEqual("--pipe", request.Arguments[0]);
        Assert.AreEqual("worker", request.Environment["MP_MODE"]);
    }
}
