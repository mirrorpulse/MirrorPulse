using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.CloudFiles;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

/// <summary>Owns the negotiated wire version and configured root bindings for one session.</summary>
public sealed class AdapterWorkerProtocolSession
{
    internal const string DurableDirectoryMoveCapability = "local-durable-directory-move";
    internal const string DurableDirectoryCopyCapability = "local-durable-directory-copy";
    internal const string DurableDirectoryCopyPreparationCapability = "local-durable-directory-copy-preparation";

    private static readonly string[] RequiredCapabilities =
        ["root-addresses", "stable-operations", "conditional-targets", "bounded-streams", "cancel-ack"];
    private static readonly JsonSerializerOptions ReadyPayloadOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
    private readonly RootRegistration[] _roots;
    private readonly bool _configurationOnly;
    private IReadOnlySet<string>? _durableCopyRoots;

    private AdapterWorkerProtocolSession(int version, RootRegistration[] roots, string[] capabilities, bool configurationOnly = false)
    {
        ProtocolVersion = version;
        _roots = roots;
        Capabilities = Array.AsReadOnly(capabilities);
        _configurationOnly = configurationOnly;
    }

    public int ProtocolVersion { get; }
    internal IReadOnlyList<string> Capabilities { get; }

    internal void RecordConnectedCapabilities(JsonElement payload)
    {
        if (!Capabilities.Contains(DurableDirectoryCopyCapability, StringComparer.Ordinal)) return;
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("durableCopyRoots", out var roots) || roots.ValueKind != JsonValueKind.Array ||
            roots.GetArrayLength() > _roots.Length)
            throw new InvalidDataException("InvalidDurableCopyRoots");
        var supported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in roots.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } root ||
                !_roots.Any(candidate => candidate.UniquenessKey == root && candidate.State == RootRegistrationState.Active) ||
                !supported.Add(root))
                throw new InvalidDataException("InvalidDurableCopyRoots");
        }
        if (Interlocked.CompareExchange(ref _durableCopyRoots, supported, null) is not null)
            throw new InvalidDataException("DuplicateDurableCopyRoots");
    }

    internal bool SupportsDurableDirectoryCopy(string? rootKey) =>
        rootKey is not null && Volatile.Read(ref _durableCopyRoots)?.Contains(rootKey) == true;

    internal bool SupportsDurableDirectoryCopyPreparation(string? rootKey) => SupportsDurableDirectoryCopy(rootKey) &&
        Capabilities.Contains(DurableDirectoryCopyPreparationCapability, StringComparer.Ordinal);

    internal static void AddDurableCopyMode(AdapterWorkerProtocolSession? protocol, string? rootKey,
        MirrorPulseDurableDirectoryCopyMode mode, Guid? parentCopy, bool conditionalTarget, Dictionary<string, object?> payload)
    {
        if (mode == MirrorPulseDurableDirectoryCopyMode.Ordinary) return;
        if (protocol?.SupportsDurableDirectoryCopy(rootKey) != true || !conditionalTarget)
            throw new NotSupportedException("DurableDirectoryCopyRequiresNegotiatedRoot");
        payload["durableDirectoryCopy"] = mode switch
        {
            MirrorPulseDurableDirectoryCopyMode.Start => "start",
            MirrorPulseDurableDirectoryCopyMode.Resume => "resume",
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        if (parentCopy is { } id) payload["durableDirectoryCopyParentId"] = id;
    }

    internal void RequireActiveRoot(string? rootKey)
    {
        if (rootKey is not null)
        {
            RootRegistration? root = _roots.SingleOrDefault(r => r.UniquenessKey == rootKey);
            if (root is null) throw new InvalidDataException("UnknownRoot");
            if (root.State != RootRegistrationState.Active) throw new IOException("RootOffline");
        }
        else if (ProtocolVersion == 2) throw new InvalidDataException("RootRequired");
    }

    public JsonElement RoutePayload(string? rootKey, JsonElement payload)
    {
        RequireActiveRoot(rootKey);
        if (ProtocolVersion == 1) return payload;
        if (string.IsNullOrWhiteSpace(rootKey)) throw new InvalidDataException("RootRequired");
        Dictionary<string, JsonElement> fields = payload.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal);
        fields["rootKey"] = JsonSerializer.SerializeToElement(rootKey);
        foreach (string name in new[] { "path", "sourcePath", "destinationPath" })
            if (fields.TryGetValue(name, out JsonElement path) && path.ValueKind == JsonValueKind.String)
                fields[name] = JsonSerializer.SerializeToElement(path.GetString()!.Replace('\\', '/'));
        return JsonSerializer.SerializeToElement(fields);
    }

    public void ValidateResponse(ControlFrameEnvelope frame, string? rootKey)
    {
        if (!frame.IsResponse || frame.ProtocolVersion != ProtocolVersion)
            throw new InvalidDataException("ResponseProtocolMismatch");
        if (ProtocolVersion == 2 && (!frame.Payload.TryGetProperty("rootKey", out JsonElement actual) ||
            actual.ValueKind != JsonValueKind.String || actual.GetString() != rootKey))
            throw new InvalidDataException("ResponseRootMismatch");
    }

    public static AdapterWorkerProtocolSession Negotiate(JsonElement hello, IEnumerable<RootRegistration> roots,
        ProtocolVersionRange packageVersions) => Negotiate(hello, roots, packageVersions, configurationOnly: false);

    internal static AdapterWorkerProtocolSession NegotiateConfigurationOnly(JsonElement hello, ProtocolVersionRange packageVersions) =>
        Negotiate(hello, [], packageVersions, configurationOnly: true);

    private static AdapterWorkerProtocolSession Negotiate(JsonElement hello, IEnumerable<RootRegistration> roots,
        ProtocolVersionRange packageVersions, bool configurationOnly)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(packageVersions);
        RootRegistration[] configured = roots.Where(r => r.State != RootRegistrationState.Removed).ToArray();
        int minimum = 1;
        int maximum = 1;
        if (hello.ValueKind != JsonValueKind.Object) throw new InvalidDataException("InvalidHello");
        if (hello.TryGetProperty("supportedVersions", out JsonElement versions))
        {
            if (versions.ValueKind != JsonValueKind.Object || !versions.TryGetProperty("minimum", out JsonElement offeredMinimum) ||
                !versions.TryGetProperty("maximum", out JsonElement offeredMaximum) || !ReadVersion(offeredMinimum, out minimum) ||
                !ReadVersion(offeredMaximum, out maximum)) throw new InvalidDataException("InvalidVersionOffer");
        }
        else if (hello.TryGetProperty("minimumProtocolVersion", out JsonElement legacyMinimum))
        {
            if (!ReadVersion(legacyMinimum, out minimum) || !hello.TryGetProperty("maximumProtocolVersion", out JsonElement legacyMaximum) ||
                !ReadVersion(legacyMaximum, out maximum)) throw new InvalidDataException("InvalidVersionOffer");
        }
        if (minimum < 1 || maximum < minimum) throw new InvalidDataException("InvalidVersionOffer");
        int selected = Math.Min(2, Math.Min(maximum, packageVersions.Maximum));
        if (selected < Math.Max(minimum, packageVersions.Minimum))
            throw new InvalidDataException("ProtocolVersionUnsupported");
        if (configurationOnly && selected != 2)
            throw new NotSupportedException("The Worker does not support configuration-only sessions.");
        string[] negotiated = [];
        if (selected == 2)
        {
            if (!hello.TryGetProperty("capabilities", out JsonElement capabilities) ||
                capabilities.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("RequiredCapabilityMissing");
            if (capabilities.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("RequiredCapabilityMissing");
            string?[] offered = capabilities.EnumerateArray().Select(c => c.GetString()).ToArray();
            if (configurationOnly)
            {
                if (!offered.Contains(AdapterConfigurationCallbackContract.Capability, StringComparer.Ordinal))
                    throw new NotSupportedException("The Worker does not support configuration callbacks.");
                return new(selected, configured, [AdapterConfigurationCallbackContract.Capability], configurationOnly: true);
            }
            if (RequiredCapabilities.Any(c => !offered.Contains(c, StringComparer.Ordinal)))
                throw new InvalidDataException("RequiredCapabilityMissing");
            if (configured.Length == 0 || configured.Select(r => r.UniquenessKey).Distinct(StringComparer.Ordinal).Count() != configured.Length)
                throw new InvalidDataException("InvalidRoots");
            negotiated = offered.Contains(AdapterDirectoryEffects.Capability, StringComparer.Ordinal)
                ? [.. RequiredCapabilities, AdapterDirectoryEffects.Capability]
                : RequiredCapabilities;
            if (offered.Contains(AdapterOriginalDirectoryWire.Capability, StringComparer.Ordinal))
            {
                negotiated = [.. negotiated, AdapterOriginalDirectoryWire.Capability];
                if (offered.Contains(AdapterAtomicDirectoryDeletionWire.Capability, StringComparer.Ordinal))
                    negotiated = [.. negotiated, AdapterAtomicDirectoryDeletionWire.Capability];
            }
            if (offered.Contains(DurableDirectoryMoveCapability, StringComparer.Ordinal))
                negotiated = [.. negotiated, DurableDirectoryMoveCapability];
            if (offered.Contains(DurableDirectoryCopyCapability, StringComparer.Ordinal))
            {
                negotiated = [.. negotiated, DurableDirectoryCopyCapability];
                if (offered.Contains(DurableDirectoryCopyPreparationCapability, StringComparer.Ordinal))
                    negotiated = [.. negotiated, DurableDirectoryCopyPreparationCapability];
            }
        }
        else if (configured.Length > 1) throw new InvalidDataException("MultipleRootsRequireProtocolV2");
        return new(selected, configured, negotiated);
    }

    private static bool ReadVersion(JsonElement value, out int version)
    {
        version = 0;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out version);
    }

    public JsonElement CreateReadyPayload(AdapterInstance instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (ProtocolVersion == 1) return JsonSerializer.SerializeToElement(instance.Configuration);
        return JsonSerializer.SerializeToElement(new
        {
            selectedVersion = ProtocolVersion,
            capabilities = Capabilities,
            roots = _roots.Select(root => new
            {
                rootKey = root.UniquenessKey,
                enabled = root.State == RootRegistrationState.Active,
                configuration = RootConfiguration(instance.Configuration, root.UniquenessKey),
            }).ToArray(),
            configuration = instance.Configuration,
            credentialBindings = instance.CredentialBindings,
            configurationOnly = _configurationOnly ? true : (bool?)null,
        }, ReadyPayloadOptions);
    }

    private static ReadOnlyDictionary<string, string> RootConfiguration(IReadOnlyDictionary<string, string> configuration, string rootKey)
    {
        var result = new Dictionary<string, string>(configuration.Where(pair => !pair.Key.StartsWith("root.", StringComparison.Ordinal)), StringComparer.Ordinal);
        string prefix = "root." + rootKey + ".";
        foreach ((string key, string value) in configuration)
            if (key.StartsWith(prefix, StringComparison.Ordinal)) result[key[prefix.Length..]] = value;
        return new ReadOnlyDictionary<string, string>(result);
    }
}
