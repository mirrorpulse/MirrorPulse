using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CliRootLabelEvidencePolicyTests
{
    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing-original")]
    [DataRow("foreign-root")]
    [DataRow("duplicate-operation")]
    [DataRow("changed-member")]
    [DataRow("lost-descendant")]
    [DataRow("same-host")]
    [DataRow("disabled-connected")]
    [DataRow("disabled-admitted")]
    [DataRow("disabled-admission-string")]
    [DataRow("missing-disabled-selection")]
    [DataRow("disabled-selection-same-host")]
    [DataRow("disabled-selection-not-exited")]
    [DataRow("disabled-selection-exit-string")]
    [DataRow("disabled-selection-foreign-receipt")]
    [DataRow("disabled-selection-missing-start")]
    [DataRow("prepared-restart-same-host")]
    [DataRow("recovered-foreign-operation")]
    [DataRow("recovered-early-admission")]
    [DataRow("wrong-execution-host")]
    [DataRow("old-label-reverted")]
    [DataRow("changed-bytes")]
    [DataRow("invented-ack")]
    [DataRow("boolean-string")]
    public async Task RootLifecycleRequiresOriginalTypedCommandGraphProcessAndByteObservations(string damage)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-root-label-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var evidence = BuildEvidence(directory);
            switch (damage)
            {
                case "missing-original": File.Delete(Path.Combine(directory, "active-execute.json")); break;
                case "foreign-root": ChangeOriginal(directory, "active-execute", node => node["rootId"] = Guid.NewGuid().ToString("D")); break;
                case "duplicate-operation": evidence["executions"]![1]!["operationId"] = evidence["executions"]![0]!["operationId"]!.DeepClone(); break;
                case "changed-member": ChangeOriginal(directory, "active-restarted-tree", node => node[1]!["memberId"] = Guid.NewGuid().ToString("D")); break;
                case "lost-descendant": ChangeOriginal(directory, "disabled-restarted-tree", node => node.AsArray().RemoveAt(1)); break;
                case "same-host": ChangeOriginal(directory, "active-restart-after", node => node["processId"] = 101); break;
                case "disabled-connected": ChangeOriginal(directory, "disabled-case-status", node => node["instances"]![0]!["workerConnected"] = true); break;
                case "disabled-admitted": ChangeOriginal(directory, "disabled-case-roots", node => node[0]!["namespaceReady"] = true); break;
                case "disabled-admission-string": ChangeOriginal(directory, "disabled-case-roots", node => node[0]!["namespaceReady"] = "false"); break;
                case "missing-disabled-selection": evidence.Remove("disabledSelectionRestart"); break;
                case "disabled-selection-same-host": ChangeOriginal(directory, "disabled-selection-restart-after", node => node["processId"] = 102); break;
                case "disabled-selection-not-exited": evidence["disabledSelectionRestart"]!["previousProcessExited"] = false; break;
                case "disabled-selection-exit-string": evidence["disabledSelectionRestart"]!["previousProcessExited"] = "true"; break;
                case "disabled-selection-foreign-receipt": evidence["disabledSelectionRestart"]!["currentProcessId"] = 999; break;
                case "disabled-selection-missing-start": File.Delete(Path.Combine(directory, "disabled-selection-restart-start.json")); break;
                case "prepared-restart-same-host": ChangeOriginal(directory, "disabled-case-prepared-restart-after", node => node["processId"] = 103); break;
                case "recovered-foreign-operation": ChangeOriginal(directory, "disabled-case-recovered-roots", node => node[0]!["pendingRename"]!["operationId"] = Guid.NewGuid().ToString("D")); break;
                case "recovered-early-admission": ChangeOriginal(directory, "disabled-case-recovered-roots", node => node[0]!["namespaceReady"] = true); break;
                case "wrong-execution-host": ChangeOriginal(directory, "disabled-case-execution-host", node => node["processId"] = 103); break;
                case "old-label-reverted": ChangeOriginal(directory, "historical-replay", node => node["currentDirectoryName"] = "Docs Label"); break;
                case "changed-bytes":
                    string path = Path.Combine(directory, "active-restarted-bytes.json");
                    var observation = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                    observation["payloads"]![0]!["localSha256"] = new string('B', 64);
                    await File.WriteAllTextAsync(path, observation.ToJsonString());
                    break;
                case "invented-ack": evidence["journalAckReceiptInspected"] = true; break;
                case "boolean-string": evidence["defaultHostIntegrated"] = "True"; break;
            }
            string evidencePath = Path.Combine(directory, "evidence.json");
            await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());
            string driver = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(driver, """
                param([string]$Policy,[string]$Evidence,[string]$Originals)
                $ErrorActionPreference='Stop'
                . $Policy
                try {
                    Assert-CliRootLabelLifecycleEvidence -Evidence (Get-Content $Evidence -Raw|ConvertFrom-Json) -OriginalsDirectory $Originals
                    'accepted';exit 0
                } catch { 'rejected';exit 1 }
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", driver, "-Policy", FindPolicy(), "-Evidence", evidencePath, "-Originals", directory }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { process.Kill(true); throw; }
            Assert.AreEqual(damage == "valid" ? 0 : 1, process.ExitCode, await output + await error);
            StringAssert.Contains(await output, damage == "valid" ? "accepted" : "rejected");
        }
        finally { Directory.Delete(directory, true); }
    }

    private static JsonObject BuildEvidence(string directory)
    {
        string root = Guid.NewGuid().ToString("D"), instance = Guid.NewGuid().ToString("D"), member = Guid.NewGuid().ToString("D");
        string native = Guid.NewGuid().ToString("D"), parent = Guid.NewGuid().ToString("D");
        var payloads = new JsonArray(new JsonObject { ["name"] = "queued-upload.txt", ["localSha256"] = new string('A', 64), ["sourceSha256"] = new string('A', 64) },
            new JsonObject { ["name"] = "cli-roundtrip.txt", ["localSha256"] = new string('C', 64), ["sourceSha256"] = new string('C', 64) });
        var tree = new JsonArray(new JsonObject { ["memberId"] = member, ["parentMemberId"] = parent, ["relativePath"] = "Docs", ["isDirectory"] = true, ["depth"] = 0 },
            new JsonObject { ["memberId"] = Guid.NewGuid().ToString("D"), ["parentMemberId"] = member, ["relativePath"] = "Docs/queued-upload.txt", ["isDirectory"] = false, ["depth"] = 1 },
            new JsonObject { ["memberId"] = Guid.NewGuid().ToString("D"), ["parentMemberId"] = member, ["relativePath"] = "Docs/cli-roundtrip.txt", ["isDirectory"] = false, ["depth"] = 1 });
        JsonObject Member(string name) => new() { ["memberId"] = member, ["nativeItemId"] = native, ["actualParentMemberId"] = parent, ["rootId"] = root, ["relativePath"] = name, ["isDirectory"] = true };
        void Original(string phase, JsonNode data) => File.WriteAllText(Path.Combine(directory, phase + ".json"), new JsonObject { ["kind"] = "result", ["data"] = data.DeepClone() }.ToJsonString());
        void Inventory(string phase, string name, bool disabled)
        {
            Original(phase + "-member", Member(name));
            var current = tree.DeepClone().AsArray();
            foreach (var entry in current) entry!["relativePath"] = name + entry["relativePath"]!.GetValue<string>()["Docs".Length..];
            Original(phase + "-tree", current);
            Original(phase + "-roots", new JsonArray(new JsonObject
            {
                ["root"] = new JsonObject { ["rootId"] = root, ["directoryName"] = name, ["label"] = name },
                ["syncState"] = disabled ? "Disabled" : "Active",
                ["namespaceReady"] = !disabled,
                ["pendingRename"] = null
            }));
            Original(phase + "-status", new JsonObject { ["instances"] = new JsonArray(new JsonObject { ["instanceId"] = instance, ["phase"] = disabled ? "Offline" : "Ready", ["workerConnected"] = !disabled }) });
            File.WriteAllText(Path.Combine(directory, phase + "-bytes.json"), new JsonObject { ["schemaVersion"] = 1, ["phase"] = phase, ["payloads"] = payloads.DeepClone() }.ToJsonString());
        }
        Original("initial-member", Member("Docs")); Original("initial-tree", tree);
        var executions = new JsonArray(); string source = "Docs";
        foreach (var entry in new (string Phase, string Target, bool Disabled)[] { ("active", "Docs Label", false), ("disabled-case", "DOCS LABEL", true), ("restore", "Docs", true) })
        {
            string operation = Guid.NewGuid().ToString("D");
            var receipt = new JsonObject
            {
                ["operationId"] = operation,
                ["rootId"] = root,
                ["phase"] = "Completed",
                ["sourceName"] = source,
                ["targetName"] = entry.Target,
                ["currentDirectoryName"] = entry.Target,
                ["memberCount"] = 3,
                ["currentTreeAudited"] = true,
                ["namespaceReady"] = !entry.Disabled
            };
            Original(entry.Phase + "-prepare", new JsonObject { ["operationId"] = operation, ["rootId"] = root, ["sourceName"] = source, ["targetName"] = entry.Target, ["phase"] = "Prepared" });
            Original(entry.Phase + "-execute", receipt);
            executions.Add(new JsonObject
            {
                ["phase"] = entry.Phase,
                ["operationId"] = operation,
                ["rootId"] = root,
                ["memberId"] = member,
                ["nativeItemId"] = native,
                ["sourceName"] = source,
                ["targetName"] = entry.Target,
                ["memberCount"] = 3,
                ["currentTreeAudited"] = true,
                ["disabled"] = entry.Disabled,
                ["originalReceipt"] = receipt.DeepClone()
            });
            Inventory(entry.Phase, entry.Target, entry.Disabled); source = entry.Target;
        }
        Inventory("active-restarted", "Docs Label", false); Inventory("disabled-restarted", "DOCS LABEL", true);
        Inventory("historical-replay", "DOCS LABEL", true); Inventory("restored", "Docs", false);
        var historical = executions[0]!["originalReceipt"]!.DeepClone(); historical["currentDirectoryName"] = "DOCS LABEL"; historical["namespaceReady"] = false;
        Original("historical-replay", historical);
        var evidence = new JsonObject
        {
            ["schemaVersion"] = 2,
            ["rootId"] = root,
            ["instanceId"] = instance,
            ["memberId"] = member,
            ["nativeItemId"] = native,
            ["memberCount"] = 3,
            ["executions"] = executions,
            ["residentAndSourceBytes"] = payloads,
            ["historicalReplay"] = historical,
            ["restoredDirectoryName"] = "Docs",
            ["defaultHostIntegrated"] = true,
            ["actualWorkerRequired"] = true,
            ["interruptedNativeExecution"] = false,
            ["journalAckReceiptInspected"] = false
        };
        int process = 101;
        foreach (var pair in new (string Phase, string Field)[] { ("active-restart", "activeRestart"), ("disabled-selection-restart", "disabledSelectionRestart"),
            ("disabled-case-prepared-restart", "preparedRestart"), ("disabled-restart", "disabledRestart"), ("restored-restart", "restoredRestart") })
        {
            Original(pair.Phase + "-before", new JsonObject { ["processId"] = process });
            Original(pair.Phase + "-stop", new JsonObject { ["processId"] = process });
            Original(pair.Phase + "-start", new JsonObject { ["processId"] = process + 1 });
            Original(pair.Phase + "-after", new JsonObject { ["processId"] = process + 1 });
            JsonObject owner = pair.Field == "preparedRestart" ? executions[1]!.AsObject() : evidence;
            owner[pair.Field] = new JsonObject { ["previousProcessId"] = process, ["currentProcessId"] = process + 1, ["previousProcessExited"] = true };
            process++;
        }
        var disabledCase = executions[1]!;
        Original("disabled-case-execution-host", new JsonObject { ["processId"] = disabledCase["preparedRestart"]!["currentProcessId"]!.DeepClone() });
        foreach (string phase in new[] { "disabled-case-prepared", "disabled-case-recovered" })
        {
            Original(phase + "-roots", new JsonArray(new JsonObject
            {
                ["root"] = new JsonObject { ["rootId"] = root, ["directoryName"] = "Docs Label", ["label"] = "Docs Label" },
                ["syncState"] = "NamespaceRecovery",
                ["namespaceReady"] = false,
                ["pendingRename"] = new JsonObject { ["operationId"] = disabledCase["operationId"]!.DeepClone(), ["phase"] = "Prepared" }
            }));
            Original(phase + "-status", new JsonObject
            {
                ["instances"] = new JsonArray(new JsonObject { ["instanceId"] = instance, ["phase"] = "Offline", ["workerConnected"] = false })
            });
        }
        return evidence;
    }

    private static void ChangeOriginal(string directory, string phase, Action<JsonNode> damage)
    {
        string path = Path.Combine(directory, phase + ".json"); var value = JsonNode.Parse(File.ReadAllText(path))!;
        damage(value["data"]!); File.WriteAllText(path, value.ToJsonString());
    }

    private static string FindPolicy()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            string path = Path.Combine(current.FullName, "eng", "cli-root-label-evidence-policy.ps1");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("The root Label evidence policy is missing.");
    }
}
