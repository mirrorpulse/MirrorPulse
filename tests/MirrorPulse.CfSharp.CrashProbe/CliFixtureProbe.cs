using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MirrorPulse.Core.Configuration;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;
using MirrorPulse.Core.Sync;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record CliFixtureMarker(int SchemaVersion, string InstanceId, string RootId, string RootKey, string DirectoryName);
public sealed record CliFixtureReadResult(int SchemaVersion, int WholeFileReads, int RangeReads, long BinaryLength, string BinarySha256);
public sealed record CliFixtureAuditResult(int SchemaVersion, bool RouteVerified, int EnumeratedMutations,
    int ReadOnlyFileMutations, int QueuedFileMutations, int AcknowledgedQueuedFileMutations);

/// <summary>Independent consumers and a stopped-Host catalog oracle for synthetic CLI fixtures only.</summary>
public static class CliFixtureProbe
{
    public const string MarkerFileName = ".mp-cli-fixture.json";
    public const int BinaryLength = 2 * 1024 * 1024 + 257;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] ReadOnlyPaths = ["nested/fixture.txt", "nested/fixture.bin"];

    public static async Task<int> RunAsync(string directory, bool audit)
    {
        string root = Path.GetFullPath(directory);
        string parent = Path.TrimEndingDirectorySeparator(Path.GetTempPath());
        const string prefix = "MirrorPulse-cli-integration-";
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(root));
        if (!string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root)), parent, StringComparison.OrdinalIgnoreCase) ||
            !name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _) ||
            !Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            return 2;
        string markerPath = Path.Combine(root, MarkerFileName);
        if (!File.Exists(markerPath) || (File.GetAttributes(markerPath) & FileAttributes.ReparsePoint) != 0 ||
            new FileInfo(markerPath).Length > 4096) return 2;
        CliFixtureMarker marker = JsonSerializer.Deserialize<CliFixtureMarker>(await File.ReadAllTextAsync(markerPath), JsonOptions)
            ?? throw new InvalidDataException("The CLI fixture marker is empty.");
        if (marker.SchemaVersion != 1 || !Guid.TryParse(marker.InstanceId, out Guid instance) || instance == Guid.Empty ||
            !Guid.TryParse(marker.RootId, out Guid rootId) || rootId == Guid.Empty || string.IsNullOrWhiteSpace(marker.RootKey) ||
            string.IsNullOrWhiteSpace(marker.DirectoryName) || marker.DirectoryName is "." or ".." ||
            marker.DirectoryName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return 2;
        object result = audit ? await AuditAsync(root, marker) : await ReadAsync(root, marker);
        Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static async Task<CliFixtureReadResult> ReadAsync(string root, CliFixtureMarker marker)
    {
        int fullReads = 0, rangeReads = 0;
        string? binaryHash = null;
        foreach (string relative in ReadOnlyPaths)
        {
            string source = Path.Combine(root, "source", relative);
            foreach (string path in new[] { Path.Combine(root, "source"), Path.GetDirectoryName(source)!, source })
                if (!Path.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The synthetic source is missing or redirected.");
            if (new FileInfo(source).Length != (relative.EndsWith(".bin", StringComparison.Ordinal) ? BinaryLength : "0123456789ABCDEF-local-fixture"u8.Length))
                throw new InvalidDataException("The synthetic source length changed.");
            byte[] expected = await File.ReadAllBytesAsync(source);
            if (relative.EndsWith(".bin", StringComparison.Ordinal) && expected.Length != BinaryLength ||
                relative.EndsWith(".txt", StringComparison.Ordinal) && !expected.AsSpan().SequenceEqual("0123456789ABCDEF-local-fixture"u8))
                throw new InvalidDataException("The synthetic source fixture changed.");
            string target = Path.Combine(root, "sync", marker.DirectoryName, relative);
            string hash = Convert.ToHexStringLower(SHA256.HashData(expected));
            if (relative.EndsWith(".bin", StringComparison.Ordinal)) binaryHash = hash;
            for (int read = 0; read < 3; read++)
            {
                await using var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                    65536, FileOptions.Asynchronous);
                if (stream.Length != expected.Length || !string.Equals(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream)), hash, StringComparison.Ordinal))
                    throw new InvalidDataException("The external consumer read different hydrated bytes.");
                fullReads++;
                long[] offsets = expected.Length == BinaryLength ? [0, 7, 65531, 1024 * 1024 - 3, 2 * 1024 * 1024 - 3, BinaryLength - 31] : [7];
                foreach (long offset in offsets)
                {
                    stream.Position = offset;
                    byte[] range = new byte[Math.Min(31, expected.Length - (int)offset)];
                    await stream.ReadExactlyAsync(range);
                    if (!range.AsSpan().SequenceEqual(expected.AsSpan((int)offset, range.Length)))
                        throw new InvalidDataException("The external consumer read different range bytes.");
                    rangeReads++;
                }
            }
        }
        return new(1, fullReads, rangeReads, BinaryLength, binaryHash!);
    }

    private static async Task<CliFixtureAuditResult> AuditAsync(string root, CliFixtureMarker marker)
    {
        var paths = new MirrorPulseStoragePaths(Path.Combine(root, "sync"), Path.Combine(root, "data"));
        foreach (string path in new[] { paths.DataRootPath, Path.GetDirectoryName(paths.ProductCatalogDatabasePath)!, paths.ProductCatalogDatabasePath })
            if (!Path.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("The isolated catalog path is missing or redirected.");
        // Take the same exclusive owner lease as the Host. This oracle must never read a live Host catalog.
        await using var catalog = await MirrorPulseProductCatalog.OpenAsync(paths);
        MirrorPulseAdapterTopology topology = await catalog.ReadAdapterTopologyAsync();
        InstanceId instanceId = InstanceId.Parse(marker.InstanceId);
        RootRegistration registration = topology.Roots.Single(root => root.InstanceId == instanceId && root.RootId == RootId.Parse(marker.RootId));
        if (topology.Instances.Count != 1 || topology.Roots.Count != 1 || topology.Instances[0].InstanceId != instanceId ||
            registration.UniquenessKey != marker.RootKey || registration.DirectoryName != marker.DirectoryName)
            throw new InvalidDataException("The catalog does not match the synthetic fixture route.");
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = paths.ProductCatalogDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        await connection.OpenAsync();
        await using var query = connection.CreateCommand();
        // Read MP-owned IDs, including completed intents; decode their public records using the catalog API.
        // Queue counts and ReadIncompleteMutationsAsync would hide fast, acknowledged redundant uploads.
        query.CommandText = "SELECT operation_id FROM mutation_intents ORDER BY operation_id;";
        var ids = new List<Guid>();
        await using (var reader = await query.ExecuteReaderAsync())
            while (await reader.ReadAsync()) ids.Add(Guid.Parse(reader.GetString(0)));
        int readOnly = 0, queued = 0, acknowledged = 0;
        foreach (Guid id in ids)
        {
            MirrorPulseMutationRecord record = await catalog.ReadMutationAsync(id)
                ?? throw new InvalidDataException("A retained fixture mutation disappeared.");
            if (record.Intent.InstanceId != instanceId || record.Intent.RootKey != marker.RootKey)
                throw new InvalidDataException("A mutation escaped the isolated fixture route.");
            if (record.Intent.IsDirectory || record.Intent.Kind is not (MirrorPulseWorkerChangeKind.Create or MirrorPulseWorkerChangeKind.ContentUpdate)) continue;
            string relative = record.Intent.RelativePath.Replace('\\', '/');
            if (ReadOnlyPaths.Contains(relative, StringComparer.OrdinalIgnoreCase) ||
                string.Equals(relative, "cli-roundtrip.txt", StringComparison.OrdinalIgnoreCase)) readOnly++;
            if (string.Equals(relative, "queued-upload.txt", StringComparison.OrdinalIgnoreCase))
            {
                queued++;
                if (record.State == MirrorPulseMutationState.Acknowledged) acknowledged++;
            }
        }
        return new(1, true, ids.Count, readOnly, queued, acknowledged);
    }
}
