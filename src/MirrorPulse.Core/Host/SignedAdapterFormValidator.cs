using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Host;

internal static class SignedAdapterFormValidator
{
    private static readonly Dictionary<string, string?> NoSecretChanges = new(StringComparer.Ordinal);

    public static Dictionary<string, string> Preview(AdapterPackagePresentation presentation, AdapterInstance? instance,
        IReadOnlyDictionary<string, string> patch) => Apply(presentation, instance, patch, NoSecretChanges, requireComplete: false);

    public static Dictionary<string, string> Apply(
        AdapterPackagePresentation presentation, AdapterInstance? instance,
        IReadOnlyDictionary<string, string> patch, IReadOnlyDictionary<string, string?> secretChanges,
        bool requireComplete = true)
    {
        Dictionary<string, AdapterPackageFormControl> fields = presentation.Resolve(null).Controls
            .Where(control => control.Key is not null && control.Kind != AdapterPackageFormKind.Callback)
            .ToDictionary(control => control.Key!, StringComparer.Ordinal);
        if (patch.Keys.Any(key => !fields.TryGetValue(key, out var field) || field.Kind == AdapterPackageFormKind.Password) ||
            secretChanges.Keys.Any(key => !fields.TryGetValue(key, out var field) || field.Kind != AdapterPackageFormKind.Password))
        {
            throw new AdapterConfigurationValidationException("An Adapter setting was undeclared or sent through the wrong input channel.");
        }

        var values = new Dictionary<string, string>(instance?.Configuration ?? patch, StringComparer.Ordinal);
        foreach (AdapterPackageFormControl field in fields.Values)
        {
            string key = field.Key!;
            if (field.Kind == AdapterPackageFormKind.Password)
            {
                values.Remove(key);
                bool changed = secretChanges.TryGetValue(key, out string? secret);
                if (secret is { Length: > 16384 } || (secret is not null && string.IsNullOrWhiteSpace(secret)))
                    throw new AdapterConfigurationValidationException($"The Adapter password field '{key}' is invalid.");
                bool present = changed ? secret is not null : instance?.CredentialBindings?.ContainsKey(key) is true;
                if (requireComplete && field.Required && !present)
                    throw new AdapterConfigurationValidationException($"The Adapter requires password field '{key}'.");
                continue;
            }

            string? value = patch.TryGetValue(key, out string? submitted) ? submitted :
                values.TryGetValue(key, out string? previous) ? previous : field.DefaultValue;
            if (string.IsNullOrWhiteSpace(value))
            {
                if (requireComplete && field.Required)
                    throw new AdapterConfigurationValidationException($"The Adapter requires setting '{key}'.");
                values.Remove(key);
                continue;
            }

            if (value.Length > 16384 || !Accepts(field, value))
                throw new AdapterConfigurationValidationException($"The Adapter setting '{key}' is invalid.");
            values[key] = value;
        }

        return values;
    }

    internal static bool AcceptsCallbackValue(AdapterPackageFormControl field, string? value) =>
        value is { Length: <= 16384 } && (!field.Required || !string.IsNullOrWhiteSpace(value)) &&
        (field.Kind == AdapterPackageFormKind.Password ? !string.IsNullOrWhiteSpace(value) :
            (field.Kind == AdapterPackageFormKind.MultiChoice && value.Length == 0) || Accepts(field, value));

    private static bool Accepts(AdapterPackageFormControl field, string value) => field.Kind switch
    {
        AdapterPackageFormKind.Text => true,
        AdapterPackageFormKind.CheckBox => value is "true" or "false",
        AdapterPackageFormKind.SingleChoice => field.Options!.Any(option => option.Id == value),
        AdapterPackageFormKind.MultiChoice => ValidChoices(field.Options!, value),
        _ => false,
    };

    private static bool ValidChoices(IReadOnlyList<AdapterPackageFormOption> options, string value)
    {
        string[] selected = value.Split(',');
        var allowed = options.Select(option => option.Id).ToHashSet(StringComparer.Ordinal);
        return selected.Length == selected.Distinct(StringComparer.Ordinal).Count() && selected.All(allowed.Contains);
    }
}
