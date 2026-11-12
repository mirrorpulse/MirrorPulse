using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Workers;

/// <summary>Prepares a Host-owned, per-instance durable grant independently from disposable caches and Worker sessions.</summary>
internal static class WorkerPersistentStateDirectory
{
    internal static void RequireSeparateCaches(string stateRoot, IEnumerable<AdapterInstance> instances)
    {
        foreach (var instance in instances)
            foreach (string cache in new[] { instance.FileCacheDirectory, instance.TransferCacheDirectory })
                if (SourceDirectoryPathNormalizer.IsWithin(stateRoot, cache) || SourceDirectoryPathNormalizer.IsWithin(cache, stateRoot))
                    throw new InvalidDataException("WorkerPersistentStateCacheOverlap");
    }

    internal static string Prepare(string stateRoot, InstanceId instanceId)
    {
        if (instanceId.Value == Guid.Empty) throw new ArgumentException("An instance ID is required.", nameof(instanceId));
        string path = Path.Combine(stateRoot, instanceId.Value.ToString("N"));
        RequireExistingAncestors(path);
        Directory.CreateDirectory(path);
        RequireExistingAncestors(path);
        return path;
    }

    private static void RequireExistingAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                    throw new InvalidDataException("WorkerPersistentStateDirectoryRequired");
            }
            catch (DirectoryNotFoundException) { }
            catch (FileNotFoundException) { }
        }
    }
}
