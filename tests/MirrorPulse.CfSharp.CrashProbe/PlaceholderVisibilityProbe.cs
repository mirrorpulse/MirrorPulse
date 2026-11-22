using System.Security.Cryptography;
using System.Text.Json;
using CfSharp;
using CfSharp.Storage.Sqlite;

namespace MirrorPulse.CfSharp.CrashProbe;

/// <summary>Inspects one disposable native fixture from outside its provider process.</summary>
internal static class PlaceholderVisibilityProbe
{
    internal static async Task<int> RunAsync(string directory)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100) ||
            Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            return 2;
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string parent = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests");
        string marker = Path.Combine(root, ".mp-permission-fixture");
        if (!string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(root), "N", out _) || !Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
            !File.Exists(marker) || new FileInfo(marker).Length != 0 ||
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0)
            return 2;
        string syncRoot = Path.Combine(root, "sync");
        string database = Path.Combine(root, "data", "external-inspection", "cfsharp.db");
        await using var observer = CloudFileSystem.CreateBuilder(syncRoot)
            .WithStateStore(new SqliteCloudStateStoreFactory(database)).Build();
        await observer.StartAsync();
        var file = observer.GetFile("Docs/original.bin");
        var initial = await file.InspectAsync();
        bool exposed;
        string availability;
        string identityHash;
        string bindingHash;
        int previous, selected;
        using (var visibility = new CliFixturePlaceholderVisibility())
        {
            previous = visibility.PreviousMode;
            selected = CliFixturePlaceholderVisibility.CurrentMode;
            var current = await file.InspectAsync();
            exposed = current.IsPlaceholder;
            availability = current.ContentAvailability.ToString();
            identityHash = Convert.ToHexString(SHA256.HashData(current.PlaceholderIdentity.Span));
            bindingHash = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(current.LocalBinding)));
        }
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            InitialIsPlaceholder = initial.IsPlaceholder,
            IsPlaceholder = exposed,
            ContentAvailability = availability,
            PlaceholderIdentitySha256 = identityHash,
            LocalBindingSha256 = bindingHash,
            PreviousMode = previous,
            SelectedMode = selected,
            RestoredMode = CliFixturePlaceholderVisibility.CurrentMode
        }));
        return 0;
    }
}
