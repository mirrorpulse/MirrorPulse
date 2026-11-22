using System.Text.Json;
using MirrorPulse.Core.Contracts;
using MirrorPulse.Core.Diagnostics;

namespace MirrorPulse.Core.Tests;

internal static class WorkerFailureTestDiagnostics
{
    public static async Task<string> ReadAsync(string directory)
    {
        string path = Path.Combine(directory, "mirrorpulse.log");
        if (!File.Exists(path)) return "No Worker failure observation was retained.";
        var safe = new List<SafeLogEntry>();
        int readCount = 0;
        using var reader = new StreamReader(path);
        while (await reader.ReadLineAsync() is { } line)
        {
            if (++readCount > 256) return "The Worker diagnostic file exceeds the fixture limit.";
            if (line.Length > 16384) return "The Worker diagnostic entry exceeds the fixture limit.";
            var entry = JsonSerializer.Deserialize<SafeLogEntry>(line);
            if (entry?.Code != "WorkerSessionFailed") continue;
            safe.Add(SafeDiagnosticPolicy.Sanitize(new(LogLevel.Warning, "worker", entry.Code, entry.OccurredAt,
                entry.Fields.Select(field => new LogField(field.Key, field.Value)).ToArray()), entry.DiagnosticId));
            if (safe.Count > 4) safe.RemoveAt(0);
        }
        return JsonSerializer.Serialize(safe);
    }
}
