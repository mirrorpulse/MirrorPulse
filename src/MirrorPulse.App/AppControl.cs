using MirrorPulse.Control.Client;

namespace MirrorPulse.App;

internal static class AppControl
{
    public static MirrorPulseControlClient Client { get; } = CreateClient();

    private static MirrorPulseControlClient CreateClient()
    {
        string installedHost = Path.Combine(AppContext.BaseDirectory, "host", "MirrorPulse.Host.exe");
        return new MirrorPulseControlClient(new()
        {
            GetExpectedHostExecutablePath = File.Exists(installedHost) ? () => installedHost : null,
        });
    }
}
