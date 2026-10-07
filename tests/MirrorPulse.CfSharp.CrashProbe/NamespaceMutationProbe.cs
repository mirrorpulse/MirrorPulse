using System.Text.Json;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record NamespaceMutationProbeResult(bool Completed, int? HResult, string? ErrorType);

/// <summary>Exercises user-process namespace callbacks only in a marked disposable fixture.</summary>
public static class NamespaceMutationProbe
{
    public static Task<int> RunAsync(string directory, string mode)
    {
        string root = Path.GetFullPath(directory);
        string prefix = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests") + Path.DirectorySeparatorChar;
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1" ||
            !root.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(root), "N", out _) ||
            !File.Exists(Path.Combine(root, ".mp-namespace-fixture")) ||
            mode is not ("delete-empty" or "delete-tree" or "rename")) return Task.FromResult(2);
        string path = Path.Combine(root, "sync", "Docs");
        NamespaceMutationProbeResult result;
        try
        {
            if (mode == "rename") Directory.Move(path, Path.Combine(root, "sync", "Renamed"));
            else Directory.Delete(path, recursive: mode == "delete-tree");
            result = new(true, null, null);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            result = new(false, exception.HResult, exception.GetType().Name);
        }
        Console.WriteLine(JsonSerializer.Serialize(result));
        return Task.FromResult(0);
    }
}
