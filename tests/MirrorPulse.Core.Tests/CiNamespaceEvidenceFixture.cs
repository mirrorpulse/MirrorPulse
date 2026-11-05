using System.Text.Json;

namespace MirrorPulse.Core.Tests;

internal static class CiNamespaceEvidenceFixture
{
    internal static object Create(JsonElement catalog, bool omitNativeCase = false)
    {
        var native = catalog.GetProperty("required").GetProperty("native").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var rows = new List<Result>();
        foreach (var item in catalog.GetProperty("required").GetProperty("namespace").EnumerateArray())
        {
            string name = item.GetString()!;
            string[] cases = catalog.TryGetProperty("requiredCases", out var mappings) && mappings.TryGetProperty(name, out var explicitCases)
                ? explicitCases.EnumerateArray().Select(value => value.GetString()!).ToArray() : [name[(name.LastIndexOf('.') + 1)..]];
            foreach (string display in cases)
                rows.Add(new(name, display, native.Contains(name) ? "native" : "managed", "Passed",
                    Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")));
        }
        if (omitNativeCase) rows.RemoveAt(rows.FindIndex(row => row.category == "native"));
        int nativeCount = rows.Count(row => row.category == "native");
        int managedCount = rows.Count - nativeCount;
        return new
        {
            schemaVersion = 1,
            suite = "namespace",
            selected = rows.Count,
            executed = rows.Count,
            skipped = 0,
            categories = new[] { new { category = "native", selected = nativeCount, executed = nativeCount, skipped = 0 },
                new { category = "managed", selected = managedCount, executed = managedCount, skipped = 0 } },
            results = rows,
        };
    }

    private sealed record Result(string name, string @case, string category, string outcome, string testId, string executionId);
}
