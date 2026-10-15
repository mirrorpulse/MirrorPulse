using System.Security.Principal;
using CfSharp;
using CfSharp.Storage.Sqlite;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliFixtureCleanupResult(int SchemaVersion, bool ExclusiveCatalogOwner,
    int ValidatedObjects, int RestoredObjects);

public static partial class CliFixtureProbe
{
    // This entry point is available only in the test probe. RunAsync has already
    // validated the exact temporary GUID directory and its bounded fixture marker.
    // No arbitrary path or recursive ACL operation is accepted.
    private static async Task<CliFixtureCleanupResult> CleanupPermissionsAsync(string root, CliFixtureMarker marker)
    {
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        foreach (string path in new[] { paths.DataRootPath, Path.GetDirectoryName(paths.ProductCatalogDatabasePath)!, paths.ProductCatalogDatabasePath })
            if (!Path.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Fixture cleanup requires an existing, unredirected product catalog.");
        // This is the same exclusive owner lease as the Host. Never relax a live
        // Host's ACLs, even if its control pipe has already stopped responding.
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        var topology = await catalog.ReadAdapterTopologyAsync();
        InstanceId instance = InstanceId.Parse(marker.InstanceId);
        var registration = topology.Roots.Single(candidate => candidate.RootId == RootId.Parse(marker.RootId));
        if (topology.Instances.Count != 1 || topology.Roots.Count != 1 || topology.Instances[0].InstanceId != instance ||
            registration.InstanceId != instance || registration.UniquenessKey != marker.RootKey || registration.DirectoryName != marker.DirectoryName)
            throw new InvalidDataException("Fixture cleanup requires the exact synthetic route.");
        string inspection = Path.Combine(root, "inspection");
        if (Path.Exists(inspection) && (File.GetAttributes(inspection) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The fixture inspection directory was redirected.");
        Directory.CreateDirectory(inspection);
        string database = Path.Combine(inspection, "cfsharp.db");
        if (Path.Exists(database) && (File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The fixture inspection database was redirected.");
        await using var fileSystem = CloudFileSystem.CreateBuilder(paths.SyncRootPath)
            .WithStateStore(new SqliteCloudStateStoreFactory(database)).Build();
        await fileSystem.StartAsync();
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, [registration]);
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [""] = true,
            [marker.DirectoryName] = true,
            [marker.DirectoryName + "/nested"] = true,
            [marker.DirectoryName + "/nested/fixture.txt"] = false,
            [marker.DirectoryName + "/nested/fixture.bin"] = false,
            [marker.DirectoryName + "/cli-roundtrip.txt"] = false,
            [marker.DirectoryName + "/queued-upload.txt"] = false,
        };
        using var user = WindowsIdentity.GetCurrent();
        string owner = user.User?.Value ?? throw new InvalidOperationException("Fixture cleanup requires a Windows user.");
        var leases = new List<MirrorPulseWindowsNamespacePermissionLease>();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<CloudItem>(); pending.Enqueue(fileSystem.Root);
        try
        {
            while (pending.TryDequeue(out var item))
            {
                string relative = item.RelativePath.Replace('\\', '/');
                if (!allowed.TryGetValue(relative, out bool directory) || directory != (item.Kind == CloudItemKind.Directory) || !observed.Add(relative))
                    throw new InvalidDataException("An unexpected namespace entry prevents fixture cleanup.");
                var lease = await MirrorPulseWindowsNamespacePermissionLease.OpenAsync(item, router);
                leases.Add(lease);
                var facts = await lease.InspectAsync(CancellationToken.None);
                if (facts.OwnerSid != owner || facts.LinkCount != 1)
                    throw new InvalidDataException("Fixture cleanup cannot change a foreign or aliased object.");
                // Public inspection and retained object/ancestor handles reject foreign
                // reparse points without following a directory link or reading content.
                if (item is CloudDirectory folder)
                    await foreach (var child in folder.EnumerateLocalChildrenAsync())
                    {
                        if (!allowed.ContainsKey(child.RelativePath.Replace('\\', '/')) || pending.Count + observed.Count >= allowed.Count)
                            throw new InvalidDataException("An unexpected namespace entry prevents fixture cleanup.");
                        pending.Enqueue(child);
                    }
            }
            // Complete validation precedes the first ACL write. Children are restored
            // before their parents; all names remain retained until writes finish.
            for (int index = leases.Count - 1; index >= 0; index--)
            {
                var facts = await leases[index].InspectAsync(CancellationToken.None);
                if (facts.OwnerSid != owner || facts.LinkCount != 1)
                    throw new InvalidDataException("A fixture object changed before permission cleanup.");
                string access = facts.IsDirectory ? "D:P(A;OICI;FA;;;" + owner + ")" : "D:P(A;;FA;;;" + owner + ")";
                await leases[index].ApplyDaclAsync(access, CancellationToken.None);
            }
            return new(1, true, observed.Count, leases.Count);
        }
        finally
        {
            for (int index = leases.Count - 1; index >= 0; index--) await leases[index].DisposeAsync();
        }
    }
}
