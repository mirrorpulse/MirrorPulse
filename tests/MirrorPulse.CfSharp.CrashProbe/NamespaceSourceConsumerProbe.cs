using System.Security.Principal;
using System.Text.Json;

namespace MirrorPulse.CfSharp.CrashProbe;

public sealed record NamespaceSourceConsumerResult(string UserSid, bool NamespaceRolePresent,
    string ProcessArchitecture, string[] Names, byte[]? Content);

/// <summary>Reads a fixed synthetic directory from a process outside its Cloud Files provider.</summary>
public static class NamespaceSourceConsumerProbe
{
    public static async Task<int> RunAsync(string directory, string mode, string roleSid)
    {
        string root = Path.GetFullPath(directory);
        string parent = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-tests");
        string marker = Path.Combine(root, ".mp-permission-fixture");
        if (Environment.GetEnvironmentVariable("MIRRORPULSE_NATIVE_TEST") != "1" ||
            !string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(root), "N", out _) ||
            !Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0 ||
            !File.Exists(marker) || new FileInfo(marker).Length != 0 ||
            (File.GetAttributes(marker) & FileAttributes.ReparsePoint) != 0 ||
            mode is not ("enumerate" or "read") || !roleSid.StartsWith("S-1-5-5-", StringComparison.Ordinal))
            return 2;
        using WindowsIdentity caller = WindowsIdentity.GetCurrent();
        bool hasRole = caller.Groups?.Any(group => group.Value == roleSid) == true;
        if (caller.User is null || hasRole) return 2;
        string docs = Path.Combine(root, "sync", "Docs");
        if (new DirectoryInfo(docs).LinkTarget is not null) return 2;
        string[] names = mode == "enumerate"
            ? Directory.GetFiles(docs).Select(path => Path.GetFileName(path)!).ToArray() : [];
        byte[]? content = mode == "read" ? await File.ReadAllBytesAsync(Path.Combine(docs, "cold.bin")) : null;
        Console.WriteLine(JsonSerializer.Serialize(new NamespaceSourceConsumerResult(caller.User.Value,
            hasRole, System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), names, content)));
        return 0;
    }
}
