using System.Collections.ObjectModel;
using System.Text.Json;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

/// <summary>Owns the negotiated wire version and configured root bindings for one session.</summary>
public sealed class AdapterWorkerProtocolSession
{
    private static readonly string[] RequiredCapabilities =
        ["root-addresses", "stable-operations", "conditional-targets", "bounded-streams", "cancel-ack"];
    private readonly RootRegistration[] _roots;

    private AdapterWorkerProtocolSession(int version, RootRegistration[] roots)
    {
        ProtocolVersion = version;
        _roots = roots;
    }

    public int ProtocolVersion { get; }

    public JsonElement RoutePayload(string? rootKey, JsonElement payload)
    {
        if (rootKey is not null)
        {
            RootRegistration? root = _roots.SingleOrDefault(r => r.UniquenessKey == rootKey);
            if (root is null) throw new InvalidDataException("UnknownRoot");
            if (root.State != RootRegistrationState.Active) throw new IOException("RootOffline");
        }
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
        ProtocolVersionRange packageVersions)
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
        if (selected == 2)
        {
            if (!hello.TryGetProperty("capabilities", out JsonElement capabilities) ||
                capabilities.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("RequiredCapabilityMissing");
            if (capabilities.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.String))
                throw new InvalidDataException("RequiredCapabilityMissing");
            string?[] offered = capabilities.EnumerateArray().Select(c => c.GetString()).ToArray();
            if (RequiredCapabilities.Any(c => !offered.Contains(c, StringComparer.Ordinal)))
                throw new InvalidDataException("RequiredCapabilityMissing");
            if (configured.Length == 0 || configured.Select(r => r.UniquenessKey).Distinct(StringComparer.Ordinal).Count() != configured.Length)
                throw new InvalidDataException("InvalidRoots");
        }
        else if (configured.Length > 1) throw new InvalidDataException("MultipleRootsRequireProtocolV2");
        return new(selected, configured);
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
            capabilities = RequiredCapabilities,
            roots = _roots.Select(root => new
            {
                rootKey = root.UniquenessKey,
                enabled = root.State == RootRegistrationState.Active,
                configuration = RootConfiguration(instance.Configuration, root.UniquenessKey),
            }).ToArray(),
            configuration = instance.Configuration,
        });
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
