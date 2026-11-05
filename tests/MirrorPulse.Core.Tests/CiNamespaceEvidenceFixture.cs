using System.Text.Json;

namespace MirrorPulse.Core.Tests;

internal static class CiNamespaceEvidenceFixture
{
    internal static object Create(JsonElement catalog, bool omitNativeCase = false) =>
        CreateSuite(catalog, "namespace", omitNativeCase, false);

    internal static object CreateNative(JsonElement catalog, bool omitCase = false, bool replaceDataRow = false) =>
        CreateSuite(catalog, "native", omitCase, replaceDataRow);

    private static object CreateSuite(JsonElement catalog, string suite, bool omitNativeCase, bool replaceDataRow)
    {
        var native = catalog.GetProperty("required").GetProperty("native").EnumerateArray()
            .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
        var rows = new List<Result>();
        int replacement = -1;
        foreach (var item in catalog.GetProperty("required").GetProperty(suite).EnumerateArray())
        {
            string name = item.GetString()!;
            string[] cases = catalog.TryGetProperty("requiredCases", out var mappings) && mappings.TryGetProperty(name, out var explicitCases)
                ? explicitCases.EnumerateArray().Select(value => value.GetString()!).ToArray() : [name[(name.LastIndexOf('.') + 1)..]];
            if (replacement < 0 && cases.Length > 1) replacement = rows.Count;
            foreach (string display in cases)
                rows.Add(new(name, display, native.Contains(name) ? "native" : "managed", "Passed",
                    Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D")));
        }
        if (omitNativeCase) rows.RemoveAt(rows.FindIndex(row => row.category == "native"));
        if (replaceDataRow) rows[replacement] = rows[replacement] with { @case = "Substituted data row" };
        int nativeCount = rows.Count(row => row.category == "native");
        int managedCount = rows.Count - nativeCount;
        object[] categories = [new { category = "native", selected = nativeCount, executed = nativeCount, skipped = 0 }];
        if (suite == "namespace") categories = [.. categories, new { category = "managed", selected = managedCount, executed = managedCount, skipped = 0 }];
        return new
        {
            schemaVersion = 1,
            suite,
            selected = rows.Count,
            executed = rows.Count,
            skipped = 0,
            categories,
            results = rows,
        };
    }

    private sealed record Result(string name, string @case, string category, string outcome, string testId, string executionId);
}
