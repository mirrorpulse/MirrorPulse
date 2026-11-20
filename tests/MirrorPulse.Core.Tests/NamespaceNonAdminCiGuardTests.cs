using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class NamespaceNonAdminCiGuardTests
{
    [TestMethod]
    public async Task WorkspacePermissionsUseRealPowerShellAclBoundaryOnMarkedTemporaryDirectories()
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string identifier = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-ordinary-ci-workspace", identifier);
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "work"));
        await File.WriteAllTextAsync(Path.Combine(directory, ".mp-namespace-ci"), identifier);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy, [string]$Workspace)
                $ErrorActionPreference='Stop'
                . $Policy
                $sid=[Security.Principal.SecurityIdentifier]::new('S-1-5-21-1-2-3-4001')
                $work=Join-Path $Workspace 'work'
                Set-OrdinaryUserNamespaceWorkspacePermissions -Workspace $Workspace -WorkDirectory $work -UserSid $sid
                foreach ($target in @(@{path=$Workspace;rights=[Security.AccessControl.FileSystemRights]::ReadAndExecute},
                    @{path=$work;rights=[Security.AccessControl.FileSystemRights]::FullControl})) {
                    $acl=[IO.FileSystemAclExtensions]::GetAccessControl([IO.DirectoryInfo]::new($target.path))
                    $rules=@($acl.GetAccessRules($true,$false,[Security.Principal.SecurityIdentifier]) | Where-Object {
                        $_.IdentityReference -eq $sid -and $_.AccessControlType -eq 'Allow' -and
                        ($_.FileSystemRights -band $target.rights) -eq $target.rights })
                    if ($rules.Count -ne 1) { throw 'The expected disposable workspace permission was not read back.' }
                }
                """);
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (string argument in new[] { "-NoProfile", "-File", script, "-Policy",
                Path.Combine(repository, "eng", "namespace-nonadmin-workspace.ps1"), "-Workspace", directory })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(0, process.ExitCode, await output + await error);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [TestMethod]
    [DataRow("local-controller", "restricted to disposable GitHub-hosted")]
    [DataRow("wrong-user", "not the intended non-administrator user")]
    [DataRow("wrong-architecture", "wrong architecture")]
    public async Task IneligibleExecutionStopsBeforeNativeTestsOrAccountCreation(string scenario, string reason)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-ordinary-ci-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string architecture = RuntimeInformation.ProcessArchitecture.ToString();
            using WindowsIdentity caller = WindowsIdentity.GetCurrent();
            string configuration = Path.Combine(directory, "configuration.json");
            await File.WriteAllTextAsync(configuration, JsonSerializer.Serialize(new
            {
                schemaVersion = 1,
                expectedArchitecture = scenario == "wrong-architecture" ? (architecture == "X64" ? "Arm64" : "X64") : architecture,
                expectedUserSid = scenario == "wrong-user" ? "S-1-5-21-1-2-3-4001" : caller.User!.Value,
                workDirectory = Path.Combine(directory, "must-not-be-created"),
            }));
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.Environment["GITHUB_ACTIONS"] = "false";
            start.Environment["RUNNER_ENVIRONMENT"] = "local";
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            if (scenario == "local-controller")
            {
                start.ArgumentList.Add(Path.Combine(repository, "eng", "verify-namespace-nonadmin.ps1"));
                start.ArgumentList.Add("-ExpectedArchitecture");
                start.ArgumentList.Add(architecture);
            }
            else
            {
                start.ArgumentList.Add(Path.Combine(repository, "eng", "run-namespace-nonadmin.ps1"));
                start.ArgumentList.Add("-ConfigurationPath");
                start.ArgumentList.Add(configuration);
            }
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string result = await output + await error;
            Assert.AreNotEqual(0, process.ExitCode, result);
            // An elevated runner is ineligible before even the architecture check.
            string expected = scenario == "wrong-architecture" && new WindowsPrincipal(caller).IsInRole(WindowsBuiltInRole.Administrator)
                ? "not the intended non-administrator user" : reason;
            Assert.Contains(expected, result);
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "must-not-be-created")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
