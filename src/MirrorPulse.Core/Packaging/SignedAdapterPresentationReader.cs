using System.IO.Compression;
using System.Text.Json;
using MirrorPulse.Adapter.Sdk;
using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Packaging;

internal static class SignedAdapterPresentationReader
{
    public static AdapterPackagePresentation Read(JsonElement manifest, ZipArchive archive,
        IReadOnlyCollection<string> signedFiles)
    {
        AdapterPackageMetadata metadata = AdapterPackageMetadata.Read(manifest, signedFiles);
        using Stream formStream = Open(metadata.FormPath);
        AdapterConfigurationForm form = AdapterConfigurationForm.Read(formStream, signedFiles);
        var locales = new Dictionary<string, AdapterPackageLocale>(StringComparer.Ordinal);
        foreach (var entry in metadata.LocaleFiles)
        {
            using Stream fallback = Open(metadata.LocaleFiles["en-US"]);
            using Stream? preferred = entry.Key.Equals("en-US", StringComparison.OrdinalIgnoreCase) ? null : Open(entry.Value);
            AdapterConfigurationTranslations labels = AdapterConfigurationTranslations.Read(metadata, form, fallback, preferred);
            locales.Add(entry.Key.ToLowerInvariant(), new(labels.Resolve(metadata.TitleKey), labels.Resolve(metadata.DescriptionKey),
                form.Controls.Select(control => Localize(control, labels)).ToArray()));
        }

        return new(metadata.LogoPath, locales);

        Stream Open(string path) => archive.GetEntry(path)?.Open()
            ?? throw new InvalidDataException("A signed Adapter form asset is missing from the archive.");
    }

    private static AdapterPackageFormControl Localize(AdapterFormControl control, AdapterConfigurationTranslations labels) =>
        control switch
        {
            AdapterFormContent content => new(content.Kind == AdapterFormContentKind.Heading
                ? AdapterPackageFormKind.Heading : AdapterPackageFormKind.Paragraph, labels.Resolve(content.TextKey)),
            AdapterFormImage image => new(AdapterPackageFormKind.Image, labels.Resolve(image.AltKey), AssetPath: image.Path),
            AdapterFormField field => new(ToKind(field.Kind), labels.Resolve(field.LabelKey), field.Key,
                Required: field.Required, DefaultValue: field.DefaultValue,
                Options: field.Options.Select(option => new AdapterPackageFormOption(option.Id, labels.Resolve(option.LabelKey))).ToArray()),
            AdapterFormCallback callback => new(AdapterPackageFormKind.Callback, labels.Resolve(callback.LabelKey), callback.Id,
                WritableFields: callback.WritableFields),
            _ => throw new InvalidDataException("The signed Adapter form has an unsupported control."),
        };

    private static AdapterPackageFormKind ToKind(AdapterFormFieldKind kind) => kind switch
    {
        AdapterFormFieldKind.Text => AdapterPackageFormKind.Text,
        AdapterFormFieldKind.Password => AdapterPackageFormKind.Password,
        AdapterFormFieldKind.SingleChoice => AdapterPackageFormKind.SingleChoice,
        AdapterFormFieldKind.MultiChoice => AdapterPackageFormKind.MultiChoice,
        AdapterFormFieldKind.CheckBox => AdapterPackageFormKind.CheckBox,
        _ => throw new InvalidDataException("The signed Adapter form has an unsupported field type."),
    };
}
