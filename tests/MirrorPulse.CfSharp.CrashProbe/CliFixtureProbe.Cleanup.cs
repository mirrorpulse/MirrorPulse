using System.Security.AccessControl;
using System.Security.Principal;
using CfSharp;
using CfSharp.Storage.Sqlite;
using MirrorPulse.CloudFiles.CfSharp;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliFixtureCleanupResult(int SchemaVersion, bool ExclusiveCatalogOwner,
    int ValidatedObjects, int RestoredObjects, bool ProtectedNamespace = true);

public static partial class CliFixtureProbe
{
    // This entry point is available only in the test probe. RunAsync has already
    // validated the exact temporary GUID directory and its bounded fixture marker.
    // No arbitrary path or recursive ACL operation is accepted.
    private static async Task<CliFixtureCleanupResult> CleanupPermissionsAsync(string root, CliFixtureMarker marker)
    {
        string stage = "CatalogOwnership";
        try { return await CleanupPermissionsCoreAsync(root, marker, value => stage = value); }
        catch (Exception failure) { failure.Data["CliFixtureCleanupStage"] = stage; throw; }
    }

    private static async Task<CliFixtureCleanupResult> CleanupPermissionsCoreAsync(string root, CliFixtureMarker marker, Action<string> setStage)
    {
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        foreach (string path in new[] { paths.DataRootPath, Path.GetDirectoryName(paths.ProductCatalogDatabasePath)!, paths.ProductCatalogDatabasePath })
            if (!Path.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Fixture cleanup requires an existing, unredirected product catalog.");
        // This is the same exclusive owner lease as the Host. Never relax a live
        // Host's ACLs, even if its control pipe has already stopped responding.
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        var topology = await catalog.ReadAdapterTopologyAsync();
        setStage("RouteValidation");
        bool anchorOnly = IsAnchorOnlyCleanup(marker);
        RootRegistration[] roots;
        if (anchorOnly)
        {
            if (topology.Instances.Count != 0 || topology.Roots.Count != 0)
                throw new InvalidDataException("Anchor-only cleanup cannot include a configured Adapter namespace.");
            roots = [];
        }
        else
        {
            InstanceId instance = InstanceId.Parse(marker.InstanceId);
            var registration = topology.Roots.Single(candidate => candidate.RootId == RootId.Parse(marker.RootId));
            if (topology.Instances.Count != 1 || topology.Roots.Count != 1 || topology.Instances[0].InstanceId != instance ||
                registration.InstanceId != instance || registration.UniquenessKey != marker.RootKey || registration.DirectoryName != marker.DirectoryName)
                throw new InvalidDataException("Fixture cleanup requires the exact synthetic route.");
            roots = [registration];
        }
        // An unprotected fixture requires no permission restoration.
        // Every MP ACL write is preceded by a permission intent or birth intent.
        // With neither present, do not acquire a protected-object lease or rewrite
        // ordinary ACLs. The harness still validates its exact GUID deletion target.
        if ((await catalog.ReadNamespacePermissionChangesAsync()).Count == 0 &&
            (await catalog.BeginNamespaceBirthRecoveryScanAsync()).ThroughSequence == 0)
            return new(1, true, 0, 0, ProtectedNamespace: false);
        setStage("InspectionStore");
        string inspection = Path.Combine(root, "inspection");
        if (Path.Exists(inspection) && (File.GetAttributes(inspection) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The fixture inspection directory was redirected.");
        Directory.CreateDirectory(inspection);
        string database = Path.Combine(inspection, "cfsharp.db");
        if (Path.Exists(database) && (File.GetAttributes(database) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("The fixture inspection database was redirected.");
        setStage("SdkConstruction");
        await using var fileSystem = CloudFileSystem.CreateBuilder(paths.SyncRootPath)
            .WithStateStore(new SqliteCloudStateStoreFactory(database)).Build();
        setStage("SdkStart");
        await fileSystem.StartAsync();
        var router = new MirrorPulseRootRouter(paths.SyncRootPath, roots);
        var allowed = new Dictionary<string, bool>(StringComparer.Ordinal) { [""] = true };
        if (!anchorOnly)
        {
            allowed.Add(marker.DirectoryName, true); allowed.Add(marker.DirectoryName + "/nested", true);
            allowed.Add(marker.DirectoryName + "/nested/fixture.txt", false); allowed.Add(marker.DirectoryName + "/nested/fixture.bin", false);
            allowed.Add(marker.DirectoryName + "/cli-roundtrip.txt", false); allowed.Add(marker.DirectoryName + "/queued-upload.txt", false);
        }
        using var user = WindowsIdentity.GetCurrent();
        string owner = user.User?.Value ?? throw new InvalidOperationException("Fixture cleanup requires a Windows user.");
        var leases = new List<MirrorPulseWindowsNamespacePermissionLease>();
        var observed = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<CloudItem>(); pending.Enqueue(fileSystem.Root);
        try
        {
            while (pending.TryDequeue(out var item))
            {
                setStage("InventoryValidation");
                string relative = item.RelativePath.Replace('\\', '/');
                if (!allowed.TryGetValue(relative, out bool directory) || directory != (item.Kind == CloudItemKind.Directory) || !observed.Add(relative))
                    throw new InvalidDataException("An unexpected namespace entry prevents fixture cleanup.");
                setStage("PermissionLease");
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
                setStage("PermissionRestoration");
                var facts = await leases[index].InspectAsync(CancellationToken.None);
                if (facts.OwnerSid != owner || facts.LinkCount != 1)
                    throw new InvalidDataException("A fixture object changed before permission cleanup.");
                string access = CreateCleanupAccessDescriptor(new SecurityIdentifier(owner), facts.IsDirectory);
                await leases[index].ApplyDaclAsync(access, CancellationToken.None);
            }
            return new(1, true, observed.Count, leases.Count);
        }
        finally
        {
            for (int index = leases.Count - 1; index >= 0; index--) await leases[index].DisposeAsync();
        }
    }

    public static string CreateCleanupAccessDescriptor(SecurityIdentifier owner, bool isDirectory) =>
        new RawSecurityDescriptor(isDirectory ? "D:P(A;OICI;FA;;;" + owner.Value + ")" : "D:P(A;;FA;;;" + owner.Value + ")")
            .GetSddlForm(AccessControlSections.Access);
}
