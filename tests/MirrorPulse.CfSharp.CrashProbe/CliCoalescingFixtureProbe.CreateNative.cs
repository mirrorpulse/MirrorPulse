using System.Security.Cryptography;
using CfSharp;
using CfSharp.Storage.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public static partial class CliCoalescingFixtureProbe
{
    private static async Task<string> ReadCurrentCreateNativeIdentityAsync(string root, MirrorPulseStoragePaths paths,
        MirrorPulseConflictKeepRemoteFileStart first, MirrorPulseNamespaceBirthPlan birth, string selectedRevision)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17134) ||
            Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1")
            throw new PlatformNotSupportedException();
        string inspection = Path.Combine(root, "inspection");
        if (Path.Exists(inspection)) RequireExistingUnredirected(inspection);
        Directory.CreateDirectory(inspection);
        string database = Path.Combine(inspection, "cfsharp.db");
        if (Path.Exists(database)) RequireExistingUnredirected(database);
        // This is an independent stopped-owner inspection, not a provider or an
        // inference from a historical native receipt or the official projection.
        await using var observer = CloudFileSystem.CreateBuilder(paths.SyncRootPath)
            .WithStateStore(new SqliteCloudStateStoreFactory(database)).Build();
        await observer.StartAsync();
        var file = observer.GetFile(first.Original.Context.Location.RelativePath);
        using var visibility = new CliFixturePlaceholderVisibility();
        var current = await file.InspectAsync();
        var original = first.Original.Context.Member.LocalObject;
        if (!current.Exists || !current.IsPlaceholder || current.IsTombstone || current.Kind != CloudItemKind.File ||
            current.ContentAvailability != CloudContentAvailability.FullyAvailable ||
            current.SynchronizationState != CloudSynchronizationState.InSync ||
            current.LocalBinding is not { } binding || binding.VolumeSerialNumber != original.VolumeSerialNumber ||
            binding.SyncRootFileId != original.SyncRootFileId || binding.LocalFileId != original.LocalFileId ||
            !CloudPlaceholderIdentity.TryDecode(current.PlaceholderIdentity.Span, out var identity) || identity is null ||
            identity.ItemId != birth.ItemId || identity.RemoteId != birth.RemoteId || identity.RemoteRevision != selectedRevision)
            throw new InvalidDataException("The current native file is not the original resident birth with the selected revision.");
        return Convert.ToHexString(SHA256.HashData(current.PlaceholderIdentity.Span));
    }
}
