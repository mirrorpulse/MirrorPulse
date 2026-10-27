using System.Runtime.Versioning;
using CfSharp;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.State;

namespace MirrorPulse.CloudFiles.CfSharp;

public sealed record MirrorPulseRoutedItem(InstanceId InstanceId, string RootKey, string RelativePath);

/// <summary>Projects active and offline Adapter roots into one Cloud Files sync root.</summary>
[SupportedOSPlatform("windows10.0.16299")]
public sealed class MirrorPulseRootRouter
{
    private readonly string _syncRootPath;
    private sealed record RoutingSnapshot(Dictionary<string, RootRegistration> Roots,
        Dictionary<string, RootRegistration> HistoricalNames,
        Dictionary<(InstanceId Instance, string Key), RootRegistration?> Routes,
        Dictionary<RootId, RootRegistration> StableIds, Dictionary<Guid, RootRegistration> PublicRootIds,
        IReadOnlyList<RootRegistration> Registrations);
    private RoutingSnapshot _snapshot;
    private readonly MirrorPulseRemoteIdentityResolver? _identityResolver;
    public IReadOnlyList<RootRegistration> Registrations => Volatile.Read(ref _snapshot).Registrations;

    public MirrorPulseRootRouter(string syncRootPath, IEnumerable<RootRegistration> registrations,
        IEnumerable<MirrorPulseManagedRootName>? historicalNames = null,
        MirrorPulseRemoteIdentityResolver? identityResolver = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(syncRootPath);
        ArgumentNullException.ThrowIfNull(registrations);
        _syncRootPath = Path.GetFullPath(syncRootPath);
        _snapshot = CreateSnapshot(registrations, historicalNames);
        _identityResolver = identityResolver;
    }

    /// <summary>Publishes a validated mapping atomically; native callbacks use current names only.</summary>
    public void ReplaceRegistrations(IEnumerable<RootRegistration> registrations,
        IEnumerable<MirrorPulseManagedRootName>? historicalNames = null) =>
        Volatile.Write(ref _snapshot, CreateSnapshot(registrations, historicalNames));

    // The controlled local caller owns root drain and instance/library admission.
    // Refresh only Active/Disabled metadata; no name, identity, routing scope,
    // native protection, source permission or readiness can be adopted here.
    internal void RefreshLocalCommandLifecycleRegistration(RootRegistration current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (current.State is not (RootRegistrationState.Active or RootRegistrationState.Disabled))
            throw new InvalidOperationException("A local root command requires its current active or disabled lifecycle.");
        while (true)
        {
            var before = Volatile.Read(ref _snapshot);
            if (!before.StableIds.TryGetValue(current.RootId, out var retained))
                throw new InvalidOperationException("A local root command cannot recreate a missing current route.");
            var sameScope = new RootRegistration(current.AdapterId, current.InstanceId, current.RootId, current.UniquenessKey,
                current.Label, current.DirectoryName, current.CustomEntry, retained.State, current.RegisteredAt, current.IdentityScope);
            if (retained != sameScope)
                throw new InvalidOperationException("A local lifecycle refresh cannot replace the retained root scope, name or identity.");
            if (retained == current) return;
            var after = before with
            {
                Roots = ReplaceLifecycleValue(before.Roots, current),
                HistoricalNames = ReplaceLifecycleValue(before.HistoricalNames, current),
                Routes = before.Routes.ToDictionary(pair => pair.Key,
                    pair => pair.Value?.RootId == current.RootId ? current : pair.Value, before.Routes.Comparer),
                StableIds = ReplaceLifecycleValue(before.StableIds, current),
                PublicRootIds = ReplaceLifecycleValue(before.PublicRootIds, current),
                Registrations = Array.AsReadOnly(before.Registrations.Select(root => root.RootId == current.RootId ? current : root).ToArray())
            };
            if (ReferenceEquals(Interlocked.CompareExchange(ref _snapshot, after, before), before)) return;
        }
    }

    private static Dictionary<TKey, RootRegistration> ReplaceLifecycleValue<TKey>(Dictionary<TKey, RootRegistration> source,
        RootRegistration current) where TKey : notnull =>
        source.ToDictionary(pair => pair.Key, pair => pair.Value.RootId == current.RootId ? current : pair.Value, source.Comparer);

    private static RoutingSnapshot CreateSnapshot(IEnumerable<RootRegistration> registrations,
        IEnumerable<MirrorPulseManagedRootName>? historicalNames)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var roots = new Dictionary<string, RootRegistration>(StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (RootRegistration root in registrations.Where(root =>
            root.State is RootRegistrationState.Active or RootRegistrationState.Disabled))
        {
            if (!string.Equals(root.Label, root.DirectoryName, StringComparison.Ordinal))
            {
                throw new InvalidDataException("A visible Adapter root's Label must equal its directory name.");
            }

            if (!labels.Add(root.Label) || !roots.TryAdd(root.DirectoryName, root))
            {
                throw new InvalidDataException($"Duplicate first-level Adapter Label: '{root.Label}'.");
            }
        }
        if (roots.Values.GroupBy(root => root.InstanceId).Any(group =>
            group.Count(root => root.IdentityScope == RootIdentityScope.LegacyInstance) > 1))
            throw new InvalidDataException("Multiple legacy roots require explicit identity migration.");
        var byId = roots.Values.ToDictionary(root => root.RootId);
        var names = new Dictionary<string, RootRegistration>(StringComparer.OrdinalIgnoreCase);
        foreach (MirrorPulseManagedRootName name in historicalNames ?? [])
        {
            // Removed roots retain their reservation in the catalog but are not routable.
            if (!byId.TryGetValue(name.RootId, out RootRegistration? owner)) continue;
            _ = new RootRegistration(owner.AdapterId, owner.InstanceId, owner.RootId, owner.UniquenessKey,
                name.DirectoryName, name.DirectoryName, owner.CustomEntry, owner.State, owner.RegisteredAt, owner.IdentityScope);
            if (roots.TryGetValue(name.DirectoryName, out RootRegistration? current) && current.RootId != owner.RootId ||
                names.TryGetValue(name.DirectoryName, out RootRegistration? previous) && previous.RootId != owner.RootId)
                throw new InvalidDataException("A historical root name has more than one stable owner.");
            names[name.DirectoryName] = owner;
        }
        var routes = new Dictionary<(InstanceId Instance, string Key), RootRegistration?>();
        foreach (var root in roots.Values)
        {
            var key = (root.InstanceId, root.UniquenessKey);
            // Preserve the old lookup-time rejection of ambiguous routes. A
            // duplicate must never silently select either registered object.
            if (!routes.TryAdd(key, root)) routes[key] = null;
        }
        var publicRootIds = roots.Values.ToDictionary(root => CreateRootIdentity(root).ItemId);
        return new(roots, names, routes, byId, publicRootIds, Array.AsReadOnly(roots.Values.ToArray()));
    }

    public RootRegistration GetRegistration(InstanceId instanceId, string rootKey)
    {
        if (!Volatile.Read(ref _snapshot).Routes.TryGetValue((instanceId, rootKey), out var root))
            throw new FileNotFoundException("The Adapter root is not registered.");
        return root ?? throw new InvalidOperationException("The Adapter root route is ambiguous.");
    }

    internal RootRegistration? FindRegistration(RootId rootId) =>
        Volatile.Read(ref _snapshot).StableIds.GetValueOrDefault(rootId);

    // Identity classification grants neither source access nor native admission.
    internal RootRegistration? FindRootByPublicItemId(Guid itemId) =>
        Volatile.Read(ref _snapshot).PublicRootIds.GetValueOrDefault(itemId);

    public CloudPlaceholderIdentity CreateFileIdentity(InstanceId instanceId, string rootKey, string remoteId, string? revision = null) =>
        MirrorPulsePlaceholderIdentity.CreateForRoot(GetRegistration(instanceId, rootKey), remoteId, revision).ToCfSharp();

    /// <summary>Selects the current backend generation; historical recovery must use its retained original identity instead.</summary>
    public ValueTask<CloudPlaceholderIdentity> CreateCurrentFileIdentityAsync(InstanceId instanceId, string rootKey,
        string remoteId, string? revision = null, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var root = GetRegistration(instanceId, rootKey);
        if (root.State != RootRegistrationState.Active) throw new IOException("The Adapter instance is offline.");
        return _identityResolver is null
            ? ValueTask.FromResult(MirrorPulsePlaceholderIdentity.CreateForRoot(root, remoteId, revision).ToCfSharp())
            : _identityResolver.ResolveAsync(root, remoteId, revision, token);
    }

    internal ValueTask RequireCurrentCallbackAsync(MirrorPulseRoutedItem route, ReadOnlyMemory<byte> encodedIdentity, CancellationToken token) =>
        _identityResolver is null || route.RelativePath.Length == 0 ? ValueTask.CompletedTask :
            _identityResolver.RequireCurrentAsync(GetRegistration(route.InstanceId, route.RootKey),
                CloudPlaceholderIdentity.Decode(encodedIdentity.Span), token);

    public CloudProviderDirectoryPage CreateRootPage()
    {
        CloudPlaceholderSpec[] roots = Volatile.Read(ref _snapshot).Roots.Values
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

    public MirrorPulseRoutedItem Resolve(string callbackPath, ReadOnlySpan<byte> encodedIdentity) =>
        ResolveIdentity(callbackPath, encodedIdentity, rootEntry: false);

    // Local entry creation does not authorize source access for a disabled root.
    internal MirrorPulseRoutedItem ResolveRootEntry(string callbackPath, ReadOnlySpan<byte> encodedIdentity) =>
        ResolveIdentity(callbackPath, encodedIdentity, rootEntry: true);

    private MirrorPulseRoutedItem ResolveIdentity(string callbackPath, ReadOnlySpan<byte> encodedIdentity, bool rootEntry)
    {
        (RootRegistration root, string innerPath) = FindRoot(callbackPath, allowHistoricalName: false);
        if (rootEntry && innerPath.Length != 0)
            throw new InvalidDataException("Local root entry creation requires a configured first-level directory.");
        if (!rootEntry && root.State == RootRegistrationState.Disabled)
        {
            throw new IOException("The Adapter instance is offline.");
        }


        CloudPlaceholderIdentity identity = CloudPlaceholderIdentity.Decode(encodedIdentity);
        if (innerPath.Length == 0 ? identity.ItemId != CreateRootIdentity(root).ItemId :
            !MirrorPulsePlaceholderIdentity.BelongsToRoot(root, identity))
        {
            throw new InvalidDataException("The placeholder identity belongs to another Adapter root.");
        }
        if (rootEntry && !encodedIdentity.SequenceEqual(CreateRootIdentity(root).Encode()))
            throw new InvalidDataException("Local root entry creation requires its complete configured identity.");

        return new(root.InstanceId, root.UniquenessKey, innerPath);
    }

    public MirrorPulseRoutedItem ResolvePath(string callbackPath)
    {
        (RootRegistration root, string innerPath) = FindRoot(callbackPath, allowHistoricalName: true);
        return new(root.InstanceId, root.UniquenessKey, innerPath);
    }

    public MirrorPulseRoutedItem ResolveCurrentPath(string callbackPath)
    {
        (RootRegistration root, string innerPath) = FindRoot(callbackPath, allowHistoricalName: false);
        return new(root.InstanceId, root.UniquenessKey, innerPath);
    }

    public string ResolveUploadPath(InstanceId instanceId, string rootKey, string adapterRelativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(adapterRelativePath);
        RootRegistration root = GetRegistration(instanceId, rootKey);
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

    private (RootRegistration Root, string InnerPath) FindRoot(string callbackPath, bool allowHistoricalName)
    {
        string relative = GetRelativePath(callbackPath);
        if (relative.Length == 0)
        {
            throw new InvalidDataException("The sync root does not belong to one Adapter instance.");
        }

        int separator = relative.IndexOf(Path.DirectorySeparatorChar);
        string first = separator < 0 ? relative : relative[..separator];
        RoutingSnapshot snapshot = Volatile.Read(ref _snapshot);
        if (!snapshot.Roots.TryGetValue(first, out RootRegistration? root) &&
            !(allowHistoricalName && snapshot.HistoricalNames.TryGetValue(first, out root)))
        {
            throw new FileNotFoundException("The Adapter root is not registered.");
        }

        return (root, separator < 0 ? string.Empty : relative[(separator + 1)..]);
    }

    // Native callbacks may be absolute or volume rooted; item APIs require a path
    // relative to this sync root. Share the same boundary check with routing.
    internal string GetRelativePath(string callbackPath)
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
