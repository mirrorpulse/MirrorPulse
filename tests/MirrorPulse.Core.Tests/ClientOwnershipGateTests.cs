using System.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class ClientOwnershipGateTests
{
    [TestMethod]
    [DataRow("none", true)]
    [DataRow("reference", false)]
    [DataRow("database", false)]
    [DataRow("linked", false)]
    public async Task GateRejectsClientStorageReferencesAccessAndLinkedSource(string violation, bool succeeds)
    {
        string repository = TestRepository.FindRepositoryRoot();
        string root = Path.Combine(Path.GetTempPath(), $"MirrorPulse-ownership-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "eng"));
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "eng", "architecture-exceptions.json"),
                """{"schemaVersion":1,"sourceLines":[]}""");
            foreach (string client in new[] { "MirrorPulse.Cli", "MirrorPulse.Control", "MirrorPulse.App" })
            {
                string folder = Path.Combine(root, "src", client);
                Directory.CreateDirectory(folder);
                string body = client == "MirrorPulse.Cli" && violation == "reference"
                    ? """<ItemGroup><ProjectReference Include="../MirrorPulse.CloudFiles.CfSharp/MirrorPulse.CloudFiles.CfSharp.csproj" /></ItemGroup>"""
                    : client == "MirrorPulse.Cli" && violation == "linked"
                        ? """<ItemGroup><Compile Include="../Outside.cs" /></ItemGroup>""" : "";
                await File.WriteAllTextAsync(Path.Combine(folder, client + ".csproj"), "<Project>" + body + "</Project>");
                if (client == "MirrorPulse.Cli" && violation == "database")
                    await File.WriteAllTextAsync(Path.Combine(folder, "Access.cs"), "using MirrorPulse.Core.State;");
            }
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string argument in new[] { "-NoProfile", "-File",
                Path.Combine(repository, "eng", "verify-architecture.ps1"), "-RepositoryRoot", root })
                start.ArgumentList.Add(argument);
            using Process process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            Task<string> error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.AreEqual(succeeds, process.ExitCode == 0, await output + await error);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
