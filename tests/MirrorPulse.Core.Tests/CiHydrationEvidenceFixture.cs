namespace MirrorPulse.Core.Tests;

internal static class CiHydrationEvidenceFixture
{
    public static Dictionary<string, object?> Create(string scenario)
    {
        var reads = new List<Dictionary<string, object?>>();
        foreach (string phase in new[] { "Online", "HostStopped", "HostRestarted", "InstanceDisabled" })
            reads.Add(new()
            {
                ["schemaVersion"] = 1,
                ["phase"] = phase,
                ["wholeFileReads"] = 6,
                ["rangeReads"] = 21,
                ["binaryLength"] = 2097409,
                ["binarySha256"] = new string('b', 64)
            });
        var audits = new List<Dictionary<string, object?>>
        {
            new() { ["schemaVersion"] = 1, ["phase"] = "BeforeLocalWrite", ["routeVerified"] = true,
                ["enumeratedMutations"] = 0, ["readOnlyFileMutations"] = 0, ["queuedFileMutations"] = 0, ["acknowledgedQueuedFileMutations"] = 0 },
            new() { ["schemaVersion"] = 1, ["phase"] = "AfterLocalWrite", ["routeVerified"] = true,
                ["enumeratedMutations"] = 1, ["readOnlyFileMutations"] = 0, ["queuedFileMutations"] = 1, ["acknowledgedQueuedFileMutations"] = 1 },
        };
        string anchor = Guid.NewGuid().ToString("D"), root = Guid.NewGuid().ToString("D"), member = Guid.NewGuid().ToString("D");
        var namespaceAudits = new List<Dictionary<string, object?>>();
        foreach (string phase in new[] { "NoAdapters", "InitialRoot", "RestartedRoot", "DisabledRoot", "ReenabledRoot" })
            namespaceAudits.Add(new()
            {
                ["phase"] = phase,
                ["anchorMemberId"] = anchor,
                ["rootId"] = root,
                ["memberId"] = member,
                ["state"] = phase == "DisabledRoot" ? "Disabled" : "Active",
                ["freshAdmission"] = true
            });
        var value = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 2,
            ["runtime"] = "win-arm64",
            ["regression"] = true,
            ["name"] = "signed-local-cli-regression",
            ["executed"] = true,
            ["rangeRead"] = true,
            ["offlineQueueRetained"] = true,
            ["uploadJournalDrained"] = true,
            ["conflictPersisted"] = true,
            ["cursorPersisted"] = true,
            ["hydration"] = new { reads, audits },
            ["namespace"] = new { defaultHostIntegrated = true, audits = namespaceAudits },
        };
        switch (scenario)
        {
            case "missing-hydration": value.Remove("hydration"); break;
            case "missing-count": audits[1].Remove("readOnlyFileMutations"); break;
            case "string-count": reads[0]["wholeFileReads"] = "6"; break;
            case "partial": reads[0]["rangeReads"] = 1; break;
            case "duplicate-phase": reads[1]["phase"] = "Online"; break;
            case "content-changed": reads[1]["binarySha256"] = new string('c', 64); break;
            case "redundant-upload": audits[1]["readOnlyFileMutations"] = 1; audits[1]["enumeratedMutations"] = 2; break;
            case "no-positive-control": audits[1]["queuedFileMutations"] = 0; audits[1]["acknowledgedQueuedFileMutations"] = 0; break;
            case "incomplete-local-write": audits[1]["acknowledgedQueuedFileMutations"] = 0; break;
            case "unknown-version": value["schemaVersion"] = 1; break;
            case "default-host-missing": value.Remove("namespace"); break;
            case "default-host-changed-member": namespaceAudits[2]["memberId"] = Guid.NewGuid().ToString("D"); break;
            case "default-host-string-admission": namespaceAudits[2]["freshAdmission"] = "true"; break;
            case "default-host-disabled-active": namespaceAudits[3]["state"] = "Active"; break;
        }
        return value;
    }
}
