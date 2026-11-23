namespace MirrorPulse.Core.Tests;

internal static class TestRepository
{
    public static string FindRepositoryRoot()
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !File.Exists(Path.Combine(directory, "MirrorPulse.sln")))
        {
            directory = Directory.GetParent(directory)?.FullName;
        }

        return directory ?? throw new DirectoryNotFoundException("The MirrorPulse repository root was not found.");
    }
}
