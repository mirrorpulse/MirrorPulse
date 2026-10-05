using System.Text.Json;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Packaging;

/// <summary>Reads the public manifest from a verified, extracted .mpadapter package.</summary>
public static class AdapterPackageManifestReader
{
    public static async Task<AdapterManifest> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await using var stream = File.OpenRead(path);
        return await ReadAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<AdapterManifest> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using JsonDocument document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        JsonElement root = document.RootElement;
        JsonElement protocol = root.GetProperty("protocol");
        JsonElement installPolicy = root.GetProperty("installPolicy");
        JsonElement instancePolicy = root.GetProperty("instancePolicy");
        JsonElement capabilities = root.GetProperty("capabilities");
        var entrypoints = root.GetProperty("entrypoints").EnumerateObject()
            .ToDictionary(item => item.Name, item => item.Value.GetString() ?? string.Empty,
                StringComparer.OrdinalIgnoreCase);
        AdapterRootDefinition[] roots = root.GetProperty("rootDefinitions").EnumerateArray()
            .Select(item => new AdapterRootDefinition(item.GetProperty("key").GetString() ?? string.Empty,
                item.GetProperty("label").GetString() ?? string.Empty,
                item.GetProperty("directoryName").GetString() ?? string.Empty,
                item.GetProperty("customEntry").GetBoolean())).ToArray();
        AdapterLocaleMetadata[] locales = root.GetProperty("localeMetadata").EnumerateObject()
            .Select(item => new AdapterLocaleMetadata(item.Name,
                item.Value.GetProperty("displayName").GetString() ?? string.Empty,
                item.Value.GetProperty("resourcePath").GetString() ?? string.Empty)).ToArray();
        AdapterConfigurationField[] configurationFields = root.TryGetProperty("configurationFields",
            out JsonElement fields) ? fields.EnumerateArray().Select(item =>
            new AdapterConfigurationField(
                item.GetProperty("key").GetString() ?? string.Empty,
                item.GetProperty("label").GetString() ?? string.Empty,
                Enum.Parse<AdapterConfigurationFieldKind>(
                    item.GetProperty("kind").GetString() ?? string.Empty, ignoreCase: true),
                item.TryGetProperty("required", out JsonElement required) && required.GetBoolean(),
                item.TryGetProperty("defaultValue", out JsonElement defaultValue) &&
                    defaultValue.ValueKind != JsonValueKind.Null ? defaultValue.GetString() : null,
                item.TryGetProperty("options", out JsonElement options) ? options.EnumerateArray()
                    .Select(option => option.GetString() ?? string.Empty).ToArray() : []))
                .ToArray() : [];
        var manifest = new AdapterManifest(root.GetProperty("schemaVersion").GetInt32(),
            AdapterId.Parse(root.GetProperty("adapterId").GetString() ?? string.Empty),
            root.GetProperty("publisher").GetString() ?? string.Empty,
            root.GetProperty("version").GetString() ?? string.Empty,
            new ProtocolVersionRange(protocol.GetProperty("minimum").GetInt32(),
                protocol.GetProperty("maximum").GetInt32()), entrypoints,
            new AdapterInstallPolicy(ReadOptionalInt(installPolicy.GetProperty("maximumInstallations"))),
            new AdapterInstancePolicy(ReadOptionalInt(instancePolicy.GetProperty("maximumInstances")),
                ReadOptionalInt(instancePolicy.GetProperty("maximumRootDefinitions"))),
            new AdapterCapabilities(capabilities.GetProperty("network").GetBoolean(),
                capabilities.GetProperty("sourceDirectory").GetBoolean(),
                capabilities.GetProperty("remoteChanges").GetBoolean(),
                capabilities.GetProperty("rangeRead").GetBoolean()),
            root.GetProperty("locales").EnumerateArray()
                .Select(item => item.GetString() ?? string.Empty).ToArray(),
            root.GetProperty("minimumMirrorPulseVersion").GetString() ?? string.Empty,
            roots, locales, configurationFields);
        Diagnostic[] errors = AdapterManifestValidator.Validate(manifest)
            .Where(item => item.Severity == DiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidDataException("The Adapter manifest is invalid: " +
                string.Join(", ", errors.Select(item => item.Code)));
        }

        return manifest;
    }

    private static int? ReadOptionalInt(JsonElement element) =>
        element.ValueKind == JsonValueKind.Null ? null : element.GetInt32();
}
