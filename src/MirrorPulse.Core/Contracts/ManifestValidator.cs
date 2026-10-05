using System.Collections.ObjectModel;

namespace MirrorPulse.Core.Contracts;

/// <summary>
/// Performs static validation of manifest v1 metadata before package execution.
/// </summary>
public static class AdapterManifestValidator
{
    private const int SupportedSchemaVersion = 1;

    public static IReadOnlyList<Diagnostic> Validate(AdapterManifest? manifest)
    {
        var diagnostics = new List<Diagnostic>();
        if (manifest is null)
        {
            diagnostics.Add(Error("manifest.missing", "The Adapter manifest is required."));
            return new ReadOnlyCollection<Diagnostic>(diagnostics);
        }

        if (manifest.SchemaVersion != SupportedSchemaVersion)
        {
            diagnostics.Add(Error("manifest.schema.unsupported", "The Adapter manifest schema is not supported."));
        }

        if (string.IsNullOrWhiteSpace(manifest.AdapterId.Value))
        {
            diagnostics.Add(Error("manifest.adapterId.missing", "The Adapter manifest must define an adapter ID."));
        }

        if (string.IsNullOrWhiteSpace(manifest.Publisher))
        {
            diagnostics.Add(Error("manifest.publisher.missing", "The Adapter manifest must define a publisher."));
        }

        if (!IsVersion(manifest.Version))
        {
            diagnostics.Add(Error("manifest.version.invalid", "The Adapter version must be a numeric version."));
        }

        if (manifest.Protocol.Minimum < 1 || manifest.Protocol.Maximum < manifest.Protocol.Minimum)
        {
            diagnostics.Add(Error("manifest.protocol.invalid", "The protocol range must start at one and have an ordered maximum."));
        }

        ValidateEntrypoints(manifest.Entrypoints, diagnostics);
        ValidateInstallPolicy(manifest.InstallPolicy, diagnostics);
        ValidateInstancePolicy(manifest.InstancePolicy, diagnostics);
        ValidateRootDefinitions(manifest.RootDefinitions, manifest.InstancePolicy, diagnostics);
        ValidateLocaleMetadata(manifest.Locales, manifest.LocaleMetadata, diagnostics);
        ValidateConfigurationFields(manifest.ConfigurationFields, diagnostics);

        if (manifest.Capabilities is null)
        {
            diagnostics.Add(Error("manifest.capabilities.missing", "The Adapter manifest must declare capabilities."));
        }

        ValidateLocales(manifest.Locales, diagnostics);
        if (!IsVersion(manifest.MinimumMirrorPulseVersion))
        {
            diagnostics.Add(Error("manifest.minimumVersion.invalid", "The minimum MirrorPulse version must be numeric."));
        }

        return new ReadOnlyCollection<Diagnostic>(diagnostics);
    }

    private static void ValidateEntrypoints(
        IReadOnlyDictionary<string, string>? entrypoints,
        List<Diagnostic> diagnostics)
    {
        if (entrypoints is null)
        {
            diagnostics.Add(Error("manifest.entrypoints.missing", "The Adapter manifest must declare Worker entrypoints."));
            return;
        }

        foreach (var runtime in new[] { "win-x64", "win-arm64" })
        {
            if (!entrypoints.TryGetValue(runtime, out var path) || !IsSafeExecutablePath(path))
            {
                diagnostics.Add(Error(
                    "manifest.entrypoint.invalid",
                    $"The manifest must provide a safe executable entrypoint for {runtime}."));
            }
        }
        if (entrypoints.Any(pair => pair.Key is not ("win-x64" or "win-arm64") || !IsSafeExecutablePath(pair.Value)))
        {
            diagnostics.Add(Error("manifest.entrypoint.invalid", "Worker entrypoints must declare supported Windows runtimes and canonical paths."));
        }
    }

    private static void ValidateInstallPolicy(AdapterInstallPolicy? policy, List<Diagnostic> diagnostics)
    {
        if (policy is null || policy.MaximumInstallations is > 0 or null)
        {
            return;
        }

        diagnostics.Add(Error("manifest.installPolicy.invalid", "The maximum installation count must be positive or null."));
    }

    private static void ValidateInstancePolicy(AdapterInstancePolicy? policy, List<Diagnostic> diagnostics)
    {
        if (policy is null)
        {
            diagnostics.Add(Error("manifest.instancePolicy.missing", "The Adapter manifest must declare instance policy."));
            return;
        }

        if (policy.MaximumInstances is <= 0 || policy.MaximumRootDefinitions is <= 0)
        {
            diagnostics.Add(Error("manifest.instancePolicy.invalid", "Instance and root limits must be positive or null."));
        }
    }

    private static void ValidateLocales(IReadOnlyList<string>? locales, List<Diagnostic> diagnostics)
    {
        if (locales is null || locales.Count == 0 || !locales.Any(locale => string.Equals(locale, "en-US", StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(Error("manifest.locales.missingFallback", "The Adapter manifest must include the en-US locale."));
            return;
        }

        if (locales.Any(string.IsNullOrWhiteSpace) || locales.Distinct(StringComparer.OrdinalIgnoreCase).Count() != locales.Count)
        {
            diagnostics.Add(Error("manifest.locales.duplicate", "Manifest locales must be non-empty and unique."));
        }
    }

    private static void ValidateRootDefinitions(
        IReadOnlyList<AdapterRootDefinition>? definitions,
        AdapterInstancePolicy? policy,
        List<Diagnostic> diagnostics)
    {
        if (definitions is null)
        {
            diagnostics.Add(Error("manifest.rootDefinitions.missing", "The Adapter root definitions collection is required."));
            return;
        }

        if (policy?.MaximumRootDefinitions is int maximum && definitions.Count > maximum)
        {
            diagnostics.Add(Error("manifest.rootDefinitions.limitExceeded", "The Adapter declares more roots than its instance policy permits."));
        }

        if (definitions.GroupBy(definition => definition.Key, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
        {
            diagnostics.Add(Error("manifest.rootDefinitions.duplicate", "Adapter root definition keys must be unique."));
        }
    }

    private static void ValidateLocaleMetadata(
        IReadOnlyList<string> locales,
        IReadOnlyDictionary<string, AdapterLocaleMetadata>? metadata,
        List<Diagnostic> diagnostics)
    {
        if (metadata is null || metadata.Count == 0)
        {
            return;
        }

        if (metadata.Keys.Any(locale => !locales.Contains(locale, StringComparer.OrdinalIgnoreCase)))
        {
            diagnostics.Add(Error("manifest.localeMetadata.undeclared", "Locale metadata must reference a declared locale."));
        }

        if (!metadata.Keys.Contains("en-US", StringComparer.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("manifest.localeMetadata.missingFallback", "Locale metadata must include en-US when metadata is declared."));
        }
    }

    private static void ValidateConfigurationFields(
        IReadOnlyList<AdapterConfigurationField> fields,
        List<Diagnostic> diagnostics)
    {
        if (fields.Count > 32 || fields.GroupBy(field => field.Key, StringComparer.Ordinal)
            .Any(group => group.Count() > 1) ||
            fields.Count(field => field.Kind == AdapterConfigurationFieldKind.Secret) > 1)
        {
            diagnostics.Add(Error("manifest.configurationFields.invalid",
                "Configuration field keys must be unique, with at most one secret field and 32 fields."));
        }

        foreach (AdapterConfigurationField field in fields)
        {
            if (string.IsNullOrWhiteSpace(field.Key) || field.Key.Length > 64 ||
                !field.Key.All(character => char.IsAsciiLetterOrDigit(character) ||
                    character is '_' or '-' or '.') ||
                string.IsNullOrWhiteSpace(field.Label) || field.Label.Length > 128 ||
                (field.Key == "credentialReference") !=
                    (field.Kind == AdapterConfigurationFieldKind.Secret) ||
                (string.Equals(field.Key, "credentialReference", StringComparison.OrdinalIgnoreCase) &&
                    field.Key != "credentialReference") ||
                (field.Kind == AdapterConfigurationFieldKind.Secret && field.DefaultValue is not null) ||
                (field.Kind == AdapterConfigurationFieldKind.Choice &&
                    (field.Options.Count == 0 || field.Options.Any(string.IsNullOrWhiteSpace) ||
                     field.Options.Distinct(StringComparer.Ordinal).Count() != field.Options.Count ||
                     (field.DefaultValue is not null && !field.Options.Contains(field.DefaultValue,
                         StringComparer.Ordinal)))) ||
                (field.Kind != AdapterConfigurationFieldKind.Choice && field.Options.Count > 0) ||
                (field.Kind == AdapterConfigurationFieldKind.Toggle && field.DefaultValue is not null &&
                    !bool.TryParse(field.DefaultValue, out _)))
            {
                diagnostics.Add(Error("manifest.configurationField.invalid",
                    $"The configuration field '{field.Key}' has invalid metadata."));
            }
        }
    }

    private static bool IsSafeExecutablePath(string? path)
        => WindowsPackagePath.IsCanonical(path) && path!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && Version.TryParse(value, out var version) && version.Major >= 0;

    private static Diagnostic Error(string code, string message) =>
        new(code, message, DiagnosticSeverity.Error);
}
