using MirrorPulse.Core.Contracts;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class ManifestValidatorTests
{
    private static readonly string[] ValidLocales = ["en-US", "zh-CN"];
    private static readonly string[] MissingLocales = ["zh-CN"];
    private static readonly Dictionary<string, string> ValidEntrypoints = new(StringComparer.OrdinalIgnoreCase)
    {
        ["win-x64"] = "payload/win-x64/Adapter.exe",
        ["win-arm64"] = "payload/win-arm64/Adapter.exe"
    };

    [TestMethod]
    public void ValidManifestProducesNoDiagnostics()
    {
        var diagnostics = AdapterManifestValidator.Validate(CreateManifest());

        Assert.HasCount(0, diagnostics);
    }

    [TestMethod]
    public void UnsupportedSchemaAndUnsafeEntrypointAreReported()
    {
        var manifest = new AdapterManifest(
            3,
            AdapterId.Parse("example.webdav"),
            "Example",
            "1.0.0",
            new ProtocolVersionRange(1, 1),
            new Dictionary<string, string>
            {
                ["win-x64"] = "../Adapter.exe",
                ["win-arm64"] = "payload/win-arm64/Adapter.exe"
            },
            new AdapterInstallPolicy(null),
            new AdapterInstancePolicy(null, null),
            new AdapterCapabilities(true, true, true, true),
            ValidLocales,
            "1.0.0");

        var codes = AdapterManifestValidator.Validate(manifest).Select(diagnostic => diagnostic.Code).ToArray();

        CollectionAssert.Contains(codes, "manifest.schema.unsupported");
        CollectionAssert.Contains(codes, "manifest.entrypoint.invalid");
    }

    [TestMethod]
    public void MissingFallbackLocaleAndInvalidPolicyAreReported()
    {
        var manifest = new AdapterManifest(
            1,
            AdapterId.Parse("example.webdav"),
            "Example",
            "invalid",
            new ProtocolVersionRange(1, 1),
            ValidEntrypoints,
            new AdapterInstallPolicy(0),
            new AdapterInstancePolicy(0, null),
            new AdapterCapabilities(true, true, true, true),
            MissingLocales,
            "1.0.0");

        var codes = AdapterManifestValidator.Validate(manifest).Select(diagnostic => diagnostic.Code).ToArray();

        CollectionAssert.Contains(codes, "manifest.version.invalid");
        CollectionAssert.Contains(codes, "manifest.installPolicy.invalid");
        CollectionAssert.Contains(codes, "manifest.instancePolicy.invalid");
        CollectionAssert.Contains(codes, "manifest.locales.missingFallback");
    }

    [TestMethod]
    public void InvalidConfigurationFormMetadataIsRejectedBeforeInstallation()
    {
        var manifest = new AdapterManifest(1, AdapterId.Parse("example.webdav"),
            "Example", "1.0.0", new ProtocolVersionRange(1, 1), ValidEntrypoints,
            new AdapterInstallPolicy(null), new AdapterInstancePolicy(null, null),
            new AdapterCapabilities(true, true, true, true), ValidLocales, "1.0.0",
            configurationFields:
            [
                new("endpoint", "Endpoint", AdapterConfigurationFieldKind.Choice,
                    true, "missing", ["https"]),
                new("endpoint", "Duplicate", AdapterConfigurationFieldKind.Text,
                    false, null, []),
                new("credentialReference", "Password", AdapterConfigurationFieldKind.Secret,
                    true, "unsafe-default", []),
            ]);

        string[] codes = AdapterManifestValidator.Validate(manifest)
            .Select(diagnostic => diagnostic.Code).ToArray();
        CollectionAssert.Contains(codes, "manifest.configurationFields.invalid");
        CollectionAssert.Contains(codes, "manifest.configurationField.invalid");
    }

    private static AdapterManifest CreateManifest() => new(
        1,
        AdapterId.Parse("example.webdav"),
        "Example",
        "1.0.0",
        new ProtocolVersionRange(1, 1),
        ValidEntrypoints,
        new AdapterInstallPolicy(null),
        new AdapterInstancePolicy(null, null),
        new AdapterCapabilities(true, true, true, true),
        ValidLocales,
        "1.0.0");
}
