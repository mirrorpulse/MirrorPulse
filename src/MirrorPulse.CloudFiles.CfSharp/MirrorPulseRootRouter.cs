using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseRoutedItem(InstanceId InstanceId, string RootKey, string RelativePath);

/// <summary>Projects active and offline Adapter roots into one Cloud Files sync root.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseRootRouter
{
    private readonly string _syncRootPath;
    private readonly Dictionary<string, RootRegistration> _roots;
    public IReadOnlyList<RootRegistration> Registrations => _roots.Values.ToArray();

    public MirrorPulseRootRouter(string syncRootPath, IEnumerable<RootRegistration> registrations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRootPath);
        ArgumentNullException.ThrowIfNull(registrations);
        _syncRootPath = Path.GetFullPath(syncRootPath);
        _roots = new Dictionary<string, RootRegistration>(StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RootRegistration root in registrations.Where(root =>
            root.State is RootRegistrationState.Active or RootRegistrationState.Disabled))
        {
            if (!string.Equals(root.Label, root.DirectoryName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A visible Adapter root's Label must equal its directory name.");
            }

            if (!labels.Add(root.Label) || !_roots.TryAdd(root.DirectoryName, root))
            {
                throw new InvalidDataException($"Duplicate first-level Adapter Label: '{root.Label}'.");
            }
        }
        if (_roots.Values.GroupBy(root => root.InstanceId).Any(group =>
            group.Count(root => root.IdentityScope == RootIdentityScope.LegacyInstance) > 1))
            throw new InvalidDataException("Multiple legacy roots require explicit identity migration.");
    }

    public RootRegistration GetRegistration(InstanceId instanceId, string rootKey) =>
        _roots.Values.SingleOrDefault(root => root.InstanceId == instanceId && root.UniquenessKey == rootKey)
        ?? throw new FileNotFoundException("The Adapter root is not registered.");

    public CloudPlaceholderIdentity CreateFileIdentity(InstanceId instanceId, string rootKey, string remoteId, string? revision = null) =>
        MirrorPulsePlaceholderIdentity.CreateForRoot(GetRegistration(instanceId, rootKey), remoteId, revision).ToCfSharp();

    public CloudProviderDirectoryPage CreateRootPage()
    {
        CloudPlaceholderSpec[] roots = _roots.Values
            .OrderBy(root => root.DirectoryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(root => root.DirectoryName, StringComparer.Ordinal)
            .Select(root => (CloudPlaceholderSpec)CloudDirectoryPlaceholderSpec
                .CreateBuilder(root.DirectoryName, CreateRootIdentity(root))
                .WithPopulationState(CloudDirectoryPopulationState.Partial)
                .Build())
            .ToArray();
        return new CloudProviderDirectoryPage(roots);
    }

    public bool IsSyncRoot(string callbackPath) => GetRelativePath(callbackPath).Length == 0;

    public MirrorPulseRoutedItem Resolve(string callbackPath, ReadOnlySpan<byte> encodedIdentity)
    {
        (RootRegistration root, string innerPath) = FindRoot(callbackPath);
        if (root.State == RootRegistrationState.Disabled)
        {
            throw new IOException("The Adapter instance is offline.");
        }


        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(encodedIdentity);
        if (innerPath.Length == 0 ? identity.ItemId != CreateRootIdentity(root).ItemId :
            !MirrorPulsePlaceholderIdentity.BelongsToRoot(root, identity))
        {
            throw new InvalidDataException("The placeholder identity belongs to another Adapter root.");
        }

        return new(root.InstanceId, root.UniquenessKey, innerPath);
    }

    public MirrorPulseRoutedItem ResolvePath(string callbackPath)
    {
        (RootRegistration root, string innerPath) = FindRoot(callbackPath);
        return new(root.InstanceId, root.UniquenessKey, innerPath);
    }

    public string ResolveUploadPath(InstanceId instanceId, string rootKey, string adapterRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterRelativePath);
        RootRegistration root = _roots.Values.SingleOrDefault(candidate =>
            candidate.InstanceId == instanceId &&
            string.Equals(candidate.UniquenessKey, rootKey, StringComparison.Ordinal))
            ?? throw new FileNotFoundException("The Adapter root is not registered.");
        if (root.State == RootRegistrationState.Disabled)
        {
            throw new IOException("The Adapter instance is offline.");
        }

        string relativePath = adapterRelativePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\0'))
        {
            throw new InvalidDataException("The upload path must stay inside its Adapter root.");
        }

        string rootPath = Path.GetFullPath(Path.Combine(_syncRootPath, root.DirectoryName));
        string path = Path.GetFullPath(Path.Combine(rootPath, relativePath));
        string prefix = rootPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The upload path escaped its Adapter root.");
        }

        return path;
    }

    private (RootRegistration Root, string InnerPath) FindRoot(string callbackPath)
    {
        string relative = GetRelativePath(callbackPath);
        if (relative.Length == 0)
        {
            throw new InvalidDataException("The sync root does not belong to one Adapter instance.");
        }

        int separator = relative.IndexOf(Path.DirectorySeparatorChar);
        string first = separator < 0 ? relative : relative[..separator];
        if (!_roots.TryGetValue(first, out RootRegistration? root))
        {
            throw new FileNotFoundException("The Adapter root is not registered.");
        }

        return (root, separator < 0 ? string.Empty : relative[(separator + 1)..]);
    }

    private string GetRelativePath(string callbackPath)
    {
        ArgumentNullException.ThrowIfNull(callbackPath);
        if (callbackPath.Length == 0 || callbackPath is "/" or "\\")
        {
            return string.Empty;
        }

        // CfSharp forwards native callbacks as volume-rooted paths (for example,
        // \Users\name\MirrorPulse) without the drive letter. Resolve those against
        // the sync root's volume; combining them as child paths misroutes callbacks.
        string candidate = Path.GetFullPath(callbackPath, _syncRootPath);
        if (string.Equals(candidate, _syncRootPath, StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        string prefix = _syncRootPath.EndsWith(Path.DirectorySeparatorChar)
            ? _syncRootPath
            : _syncRootPath + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The Cloud Files callback path is outside the MirrorPulse root.");
        }

        return candidate[prefix.Length..];
    }

    private static CloudPlaceholderIdentity CreateRootIdentity(RootRegistration root) =>
        MirrorPulsePlaceholderIdentity.Create(root.InstanceId, $"mirrorpulse-root:{root.RootId}").ToCfSharp();
}
