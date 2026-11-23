namespace MirrorPulse.Core.Contracts;

/// <summary>Localized presentation verified as part of an installed Adapter package.</summary>
public sealed record AdapterPackagePresentation(string LogoPath,
    IReadOnlyDictionary<string, AdapterPackageLocale> Locales)
{
    public AdapterPackageLocale Resolve(string? locale) =>
        locale is not null && Locales.TryGetValue(locale.ToLowerInvariant(), out var selected) ? selected : Locales["en-us"];
}

public sealed record AdapterPackageLocale(string Title, string Description,
    IReadOnlyList<AdapterPackageFormControl> Controls);

public enum AdapterPackageFormKind { Heading, Paragraph, Image, Text, Password, SingleChoice, MultiChoice, CheckBox, Callback }

public sealed record AdapterPackageFormOption(string Id, string Label);

public sealed record AdapterPackageFormControl(AdapterPackageFormKind Kind, string Label, string? Key = null,
    string? AssetPath = null, bool Required = false, string? DefaultValue = null,
    IReadOnlyList<AdapterPackageFormOption>? Options = null, IReadOnlyList<string>? WritableFields = null);
