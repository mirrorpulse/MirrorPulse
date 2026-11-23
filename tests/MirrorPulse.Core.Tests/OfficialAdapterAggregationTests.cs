using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class OfficialAdapterAggregationTests
{
    private static readonly string[] ExpectedAdapterIds =
    [
        "com.mirrorpulse.adapter.local",
        "com.mirrorpulse.adapter.webdav",
        "com.mirrorpulse.adapter.smb",
    ];

    [TestMethod]
    public void AggregationLockListsEveryOfficialAdapterAndTheBuildGateChecksBothArchitectures()
    {
        string repository = FindRepositoryRoot();
        using JsonDocument lockDocument = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(repository, "eng", "official-adapters.json")));
        JsonElement adapters = lockDocument.RootElement.GetProperty("adapters");
        string[] adapterIds = adapters.EnumerateArray()
            .Select(item => item.GetProperty("adapterId").GetString()!)
            .ToArray();

        CollectionAssert.AreEquivalent(ExpectedAdapterIds, adapterIds);

        string script = File.ReadAllText(Path.Combine(repository, "eng", "aggregate-official-adapters.ps1"));
        StringAssert.Contains(script, "releases/latest");
        StringAssert.Contains(script, "gh release download");
        StringAssert.Contains(script, "win-x64");
        StringAssert.Contains(script, "win-arm64");
        StringAssert.Contains(script, ".signature.json");
    }

    private static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "MirrorPulse.sln")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        return directory ?? throw new DirectoryNotFoundException("The MirrorPulse repository root was not found.");
    }
}
