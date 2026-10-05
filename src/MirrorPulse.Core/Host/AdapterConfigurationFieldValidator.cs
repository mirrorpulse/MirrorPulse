using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

/// <summary>Applies declared defaults and validates each instance before it reaches a Worker.</summary>
public static class AdapterConfigurationFieldValidator
{
    public static IReadOnlyDictionary<string, string> ValidateAndApplyDefaults(
        AdapterManifest manifest,
        IReadOnlyDictionary<string, string> values,
        string? secret)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(values);
        return Validate(manifest, values, !string.IsNullOrEmpty(secret));
    }

    /// <summary>Validates a user patch and preserves only the instance's managed credential pointer.</summary>
    public static IReadOnlyDictionary<string, string> ApplyPatch(
        AdapterManifest manifest,
        AdapterInstance instance,
        IReadOnlyDictionary<string, string> patch)
    {
        ArgumentNullException.ThrowIfNull(instance);
        RejectSecretSettings(manifest, patch);
        var values = new Dictionary<string, string>(instance.Configuration, StringComparer.Ordinal);
        values.Remove("credentialReference", out string? reference);
        if (reference is not null && !instance.CredentialReferences.Contains(reference, StringComparer.Ordinal))
        {
            throw new InvalidDataException("The instance credential reference is inconsistent.");
        }

        foreach (var pair in patch)
        {
            values[pair.Key] = pair.Value;
        }

        var result = new Dictionary<string, string>(Validate(manifest, values, reference is not null),
            StringComparer.Ordinal);
        if (reference is not null)
        {
            result["credentialReference"] = reference;
        }

        return result;
    }

    private static void RejectSecretSettings(AdapterManifest manifest, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Keys.Any(key => string.Equals(key, "credentialReference", StringComparison.OrdinalIgnoreCase) ||
            manifest.ConfigurationFields.Any(field => field.Kind == AdapterConfigurationFieldKind.Secret &&
                string.Equals(field.Key, key, StringComparison.OrdinalIgnoreCase))))
        {
            throw new InvalidDataException("Secret settings and credential references are managed by MirrorPulse.");
        }

        if (values.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || pair.Key.Length > 128 ||
                pair.Value is null || pair.Value.Length > 4096))
        {
            throw new InvalidDataException("An Adapter setting exceeds the supported bounds.");
        }
    }

    private static Dictionary<string, string> Validate(
        AdapterManifest manifest, IReadOnlyDictionary<string, string> values, bool hasCredential)
    {
        RejectSecretSettings(manifest, values);
        var result = new Dictionary<string, string>(values, StringComparer.Ordinal);
        foreach (AdapterConfigurationField field in manifest.ConfigurationFields)
        {
            if (field.Kind == AdapterConfigurationFieldKind.Secret)
            {
                if (field.Required && !hasCredential)
                {
                    throw new InvalidDataException($"The Adapter requires '{field.Label}'.");
                }

                continue;
            }

            if (!result.TryGetValue(field.Key, out string? value) && field.DefaultValue is not null)
            {
                value = field.DefaultValue;
                result[field.Key] = value;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                if (field.Required)
                {
                    throw new InvalidDataException($"The Adapter requires '{field.Label}'.");
                }

                result.Remove(field.Key);
                continue;
            }

            if (value.Length > 4096 ||
                (field.Kind == AdapterConfigurationFieldKind.Choice &&
                    !field.Options.Contains(value, StringComparer.Ordinal)) ||
                (field.Kind == AdapterConfigurationFieldKind.Toggle &&
                    !bool.TryParse(value, out _)))
            {
                throw new InvalidDataException($"The Adapter setting '{field.Label}' is invalid.");
            }

            if (field.Kind == AdapterConfigurationFieldKind.Toggle)
            {
                result[field.Key] = bool.Parse(value) ? "true" : "false";
            }
        }

        return result;
    }
}
