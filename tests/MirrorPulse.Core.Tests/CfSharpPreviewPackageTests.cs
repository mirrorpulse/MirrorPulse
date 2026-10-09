namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CfSharpPreviewPackageTests
{
    [TestMethod]
    public void OnlyIntegrationReferencesThePinnedCfSharpPreviewPackage()
    {
        var projectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MirrorPulse.Core/MirrorPulse.Core.csproj"));
        var integrationPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MirrorPulse.CloudFiles.CfSharp/MirrorPulse.CloudFiles.CfSharp.csproj"));
        var packagePropsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Directory.Packages.props"));
        var project = File.ReadAllText(projectPath);
        var integration = File.ReadAllText(integrationPath);
        var packageProps = File.ReadAllText(packagePropsPath);

        Assert.IsFalse(project.Contains("<PackageReference Include=\"CfSharp\" />", StringComparison.Ordinal));
        StringAssert.Contains(integration, "<PackageReference Include=\"CfSharp\" />");
        StringAssert.Contains(packageProps, "<PackageVersion Include=\"CfSharp\" Version=\"0.1.0-preview.4\" />");
    }

    [TestMethod]
    public void IntegrationProjectReferencesTheMatchingOfficialSqliteProvider()
    {
        var integrationProjectPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MirrorPulse.CloudFiles.CfSharp/MirrorPulse.CloudFiles.CfSharp.csproj"));
        var packagePropsPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../Directory.Packages.props"));
        var integrationProject = File.ReadAllText(integrationProjectPath);
        var packageProps = File.ReadAllText(packagePropsPath);

        StringAssert.Contains(integrationProject, "<PackageReference Include=\"CfSharp.Storage.Sqlite\" />");
        StringAssert.Contains(packageProps, "<PackageVersion Include=\"CfSharp.Storage.Sqlite\" Version=\"0.1.0-preview.4\" />");
    }
}
