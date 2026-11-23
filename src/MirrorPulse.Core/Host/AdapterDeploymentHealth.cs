using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

/// <summary>A validated deployment issue with a label from the installed package's selected locale.</summary>
public sealed record MirrorPulseAdapterHealthIssue(AdapterHealthReason Reason, string? FieldKey = null, string? FieldLabel = null)
{
    public bool RequiresConfiguration => Reason is AdapterHealthReason.MissingConfiguration or AdapterHealthReason.InvalidConfiguration or
        AdapterHealthReason.MissingCredential or AdapterHealthReason.ExpiredCredential;
}

public static class AdapterDeploymentHealth
{
    internal const string ReportMessage = "AdapterHealthReport";

    internal static AdapterHealthIssue[] ReadReport(AdapterPackagePresentation presentation, JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("issues", out JsonElement issues) ||
            issues.ValueKind != JsonValueKind.Array || issues.GetArrayLength() > 16)
            throw new InvalidDataException("The Adapter health report is invalid.");

        var fields = Fields(presentation.Resolve(null));
        var accepted = new HashSet<AdapterHealthIssue>();
        foreach (JsonElement item in issues.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("reason", out JsonElement reasonValue) ||
                reasonValue.ValueKind != JsonValueKind.String || reasonValue.GetString() is not { Length: <= 32 } reasonText ||
                !Enum.TryParse(reasonText, ignoreCase: false, out AdapterHealthReason reason) || reason.ToString() != reasonText)
                throw new InvalidDataException("The Adapter reported an unknown health reason.");

            string? key = null;
            if (item.TryGetProperty("fieldKey", out JsonElement rawKey) && rawKey.ValueKind != JsonValueKind.Null)
            {
                if (rawKey.ValueKind != JsonValueKind.String || rawKey.GetString() is not { Length: > 0 and <= 64 } fieldKey ||
                    !fields.TryGetValue(fieldKey, out var field) || reason is AdapterHealthReason.SourceUnavailable or AdapterHealthReason.WorkerFailure ||
                    (reason is AdapterHealthReason.MissingCredential or AdapterHealthReason.ExpiredCredential && field.Kind != AdapterPackageFormKind.Password))
                    throw new InvalidDataException("The Adapter reported a health issue for an inapplicable field.");
                key = fieldKey;
            }

            if (!accepted.Add(new(reason, key)))
                throw new InvalidDataException("The Adapter health report contains duplicate issues.");
        }
        return [.. accepted];
    }

    public static IReadOnlyList<MirrorPulseAdapterHealthIssue> Read(InstalledAdapter installation, AdapterInstance instance,
        IReadOnlyList<AdapterHealthIssue> reported, string? locale)
    {
        if (!installation.IsSigned || installation.Manifest.Presentation is not { } presentation || instance.InstallId != installation.InstallId)
            return [];

        var canonical = Fields(presentation.Resolve(null));
        var localized = Fields(presentation.Resolve(locale));
        var issues = new HashSet<AdapterHealthIssue>();
        foreach (AdapterPackageFormControl field in canonical.Values)
        {
            string key = field.Key!;
            if (field.Kind == AdapterPackageFormKind.Password)
            {
                if (field.Required && instance.CredentialBindings?.ContainsKey(key) != true)
                    issues.Add(new(AdapterHealthReason.MissingCredential, key));
            }
            else if (instance.Configuration.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value))
            {
                if (!SignedAdapterFormValidator.AcceptsCallbackValue(field, value))
                    issues.Add(new(AdapterHealthReason.InvalidConfiguration, key));
            }
            else if (field.Required)
            {
                issues.Add(new(AdapterHealthReason.MissingConfiguration, key));
            }
        }

        if (instance.Enabled) issues.UnionWith(reported);
        return issues.OrderBy(issue => issue.FieldKey, StringComparer.Ordinal).ThenBy(issue => issue.Reason)
            .Select(issue => new MirrorPulseAdapterHealthIssue(issue.Reason, issue.FieldKey,
                issue.FieldKey is { } key && localized.TryGetValue(key, out var field) ? field.Label : null)).ToArray();
    }

    private static Dictionary<string, AdapterPackageFormControl> Fields(AdapterPackageLocale locale) => locale.Controls
        .Where(control => control.Key is not null && control.Kind != AdapterPackageFormKind.Callback)
        .ToDictionary(control => control.Key!, StringComparer.Ordinal);
}
