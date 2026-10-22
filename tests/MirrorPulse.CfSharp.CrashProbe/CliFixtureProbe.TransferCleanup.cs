using System.Text.Json;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public static partial class CliFixtureProbe
{
    /// <summary>Exclusive stopped-Host cleanup for the separately authenticated signed transfer fixture.</summary>
    public static async Task<int> RunTransferCleanupAsync(string directory)
    {
        string stage = "FixtureAuthentication";
        try
        {
            await using var fixture = await OpenTransferFixtureAsync(directory, value => stage = value);
            var result = await CleanupAuthenticatedInventoryAsync(fixture.Root, fixture.Paths, fixture.Catalog,
                fixture.Inventory.Roots, fixture.Inventory.AllowedEntries,
                value => stage = value);
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }
        catch (Exception failure)
        {
            failure.Data["CliFixtureCleanupStage"] = stage;
            throw;
        }
    }

    private sealed record TransferFixtureCatalog(string Root, MirrorPulseStoragePaths Paths, CliTransferFixtureMarker Marker,
        MirrorPulseProductCatalog Catalog, CliTransferFixtureInventory Inventory) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Catalog.DisposeAsync();
    }

    private static async Task<TransferFixtureCatalog> OpenTransferFixtureAsync(string directory, Action<string> setStage)
    {
        string root = CliTransferFixtureLayout.RequireFixtureDirectory(directory);
        string markerPath = Path.Combine(root, CliTransferFixtureLayout.MarkerFileName);
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        foreach (string path in new[] { root, paths.SyncRootPath, paths.DataRootPath,
            Path.GetDirectoryName(paths.ProductCatalogDatabasePath)!, paths.ProductCatalogDatabasePath, markerPath })
            if (!Path.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The synthetic transfer fixture or its retained catalog was redirected or lost.");
        if (new FileInfo(markerPath).Length > 8192)
            throw new InvalidDataException("The synthetic transfer marker exceeds its bounded inventory.");
        var marker = JsonSerializer.Deserialize<CliTransferFixtureMarker>(await File.ReadAllBytesAsync(markerPath), JsonOptions)
            ?? throw new InvalidDataException("The synthetic transfer fixture marker is empty.");
        setStage("CatalogOwnership");
        var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        try
        {
            setStage("RouteValidation");
            var inventory = CliTransferFixtureLayout.Authenticate(root, marker, await catalog.ReadAdapterTopologyAsync());
            return new(root, paths, marker, catalog, inventory);
        }
        catch { await catalog.DisposeAsync(); throw; }
    }
}
