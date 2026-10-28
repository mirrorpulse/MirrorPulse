using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CliRootLabelCancellationPolicyTests
{
    private static readonly string[] PayloadNames = ["queued-upload.txt", "cli-roundtrip.txt"];

    [TestMethod]
    [DataRow("valid")]
    [DataRow("missing-original")]
    [DataRow("same-host")]
    [DataRow("pending-admitted")]
    [DataRow("pending-sync-state")]
    [DataRow("foreign-pending")]
    [DataRow("disabled-connected")]
    [DataRow("foreign-cancellation")]
    [DataRow("changed-timestamp")]
    [DataRow("later-label-reverted")]
    [DataRow("changed-descendant")]
    [DataRow("lost-descendant")]
    [DataRow("changed-bytes")]
    [DataRow("missing-current-audit")]
    [DataRow("duplicate-operation")]
    [DataRow("invented-fault")]
    [DataRow("boolean-string")]
    public async Task CancellationRequiresOriginalPreparationIndependentProcessAndSeparateCurrentAdmission(string damage)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-cancellation-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            JsonObject evidence = BuildEvidence(directory);
            switch (damage)
            {
                case "missing-original": File.Delete(Path.Combine(directory, "active-cancel.json")); break;
                case "same-host": ChangeOriginal(directory, "active-restart-after", node => node["processId"] = 101); break;
                case "pending-admitted": ChangeOriginal(directory, "active-restarted-roots", node => node[0]!["namespaceReady"] = true); break;
                case "pending-sync-state": ChangeOriginal(directory, "active-restarted-roots", node => node[0]!["syncState"] = "Active"); break;
                case "foreign-pending": ChangeOriginal(directory, "active-restarted-roots", node => node[0]!["pendingRename"]!["operationId"] = Id()); break;
                case "disabled-connected": ChangeOriginal(directory, "disabled-restarted-status", node => node["instances"]![0]!["workerConnected"] = true); break;
                case "foreign-cancellation": ChangeOriginal(directory, "active-cancel", node => node["rootId"] = Id()); break;
                case "changed-timestamp":
                    ChangeOriginal(directory, "active-replay", node => node["cancelledAt"] = "2026-10-29T00:00:00Z");
                    evidence["cases"]![0]!["historicalReplay"]!["cancelledAt"] = "2026-10-29T00:00:00Z";
                    break;
                case "later-label-reverted":
                    ChangeOriginal(directory, "active-replay", node => node["currentDirectoryName"] = "Docs");
                    evidence["cases"]![0]!["historicalReplay"]!["currentDirectoryName"] = "Docs";
                    break;
                case "changed-descendant": ChangeOriginal(directory, "active-replayed-tree", node => node[1]!["parentMemberId"] = Id()); break;
                case "lost-descendant": ChangeOriginal(directory, "disabled-cancelled-tree", node => node.AsArray().RemoveAt(1)); break;
                case "changed-bytes":
                    string path = Path.Combine(directory, "disabled-replayed-bytes.json");
                    JsonNode bytes = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
                    bytes["payloads"]![0]!["localSha256"] = new string('B', 64);
                    await File.WriteAllTextAsync(path, bytes.ToJsonString());
                    break;
                case "missing-current-audit":
                    ChangeOriginal(directory, "active-cancel", node => node["currentTreeAudited"] = false);
                    evidence["cases"]![0]!["cancellation"]!["currentTreeAudited"] = false;
                    break;
                case "duplicate-operation":
                    string operation = evidence["cases"]![0]!["preparation"]!["operationId"]!.GetValue<string>();
                    ChangeOriginal(directory, "active-later-prepare", node => node["operationId"] = operation);
                    ChangeOriginal(directory, "active-later-execute", node => node["operationId"] = operation);
                    evidence["cases"]![0]!["laterExecution"]!["operationId"] = operation;
                    break;
                case "invented-fault": evidence["interruptedNativeExecution"] = true; break;
                case "boolean-string": evidence["preparedIntentAcrossIndependentHost"] = "True"; break;
            }
            string evidencePath = Path.Combine(directory, "completed.json");
            await File.WriteAllTextAsync(evidencePath, evidence.ToJsonString());
            string driver = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(driver, """
                param([string]$Policy,[string]$Evidence,[string]$Originals)
                $ErrorActionPreference='Stop'
                . $Policy
                try {
                    Assert-CliRootLabelCancellationEvidence -Evidence (Get-Content $Evidence -Raw|ConvertFrom-Json) -OriginalsDirectory $Originals
                    'accepted';exit 0
                } catch { $_.Exception.Message; 'rejected';exit 1 }
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", driver, "-Policy", FindPolicy(), "-Evidence", evidencePath, "-Originals", directory }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(); Task<string> error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
            string actual = await output; string stderr = await error;
            Assert.AreEqual(damage == "valid" ? 0 : 1, process.ExitCode, actual + stderr);
            StringAssert.Contains(actual, damage == "valid" ? "accepted" : "rejected");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static JsonObject BuildEvidence(string directory)
    {
        string root = Id(), instance = Id(), member = Id(), native = Id(), anchor = Id();
        JsonArray tree = Node(new[]
        {
            new { memberId=member, parentMemberId=anchor, isDirectory=true, relativePath="Docs", depth=0 },
            new { memberId=Id(), parentMemberId=member, isDirectory=true, relativePath="Docs/nested", depth=1 },
            new { memberId=Id(), parentMemberId=member, isDirectory=false, relativePath="Docs/queued-upload.txt", depth=1 },
            new { memberId=Id(), parentMemberId=member, isDirectory=false, relativePath="Docs/cli-roundtrip.txt", depth=1 }
        }).AsArray();
        JsonArray bytes = Node(PayloadNames.Select(name =>
            new { name, localSha256 = new string('A', 64), sourceSha256 = new string('A', 64) })).AsArray();
        JsonObject Member(string name) => Node(new { rootId = root, memberId = member, nativeItemId = native, actualParentMemberId = anchor, relativePath = name }).AsObject();
        void State(string phase, string name, bool disabled, JsonObject? preparation = null)
        {
            WriteOriginal(directory, phase + "-roots", Node(new[] { new
            {
                root = new { rootId = root, directoryName = name, label = name, state = disabled ? "Disabled" : "Active" },
                syncState = preparation is not null ? "NamespaceRecovery" : disabled ? "Disabled" : "Active",
                namespaceReady = preparation is null && !disabled, pendingRename = preparation
            } }));
            WriteOriginal(directory, phase + "-status", Node(new { instances = new[] { new { instanceId = instance, phase = disabled ? "Offline" : "Ready", workerConnected = !disabled } } }));
        }
        void Graph(string phase, string name)
        {
            WriteOriginal(directory, phase + "-member", Member(name));
            JsonArray current = tree.DeepClone().AsArray();
            foreach (JsonNode? entry in current) entry!["relativePath"] = name + entry["relativePath"]!.GetValue<string>()[4..];
            WriteOriginal(directory, phase + "-tree", current);
            File.WriteAllText(Path.Combine(directory, phase + "-bytes.json"), Node(new { schemaVersion = 1, phase, payloads = bytes }).ToJsonString());
        }
        JsonObject Prepare(string phase, string source, string target)
        {
            JsonObject prepared = Node(new { operationId = Id(), rootId = root, sourceName = source, targetName = target, phase = "Prepared", updatedAt = "2026-10-28T00:00:00Z" }).AsObject();
            WriteOriginal(directory, phase + "-prepare", prepared);
            return prepared;
        }
        JsonObject Execute(string phase, string source, string target, bool disabled)
        {
            JsonObject prepared = Prepare(phase, source, target);
            JsonObject execution = Node(new
            {
                operationId = prepared["operationId"]!.GetValue<string>(),
                rootId = root,
                sourceName = source,
                targetName = target,
                phase = "Completed",
                currentDirectoryName = target,
                memberCount = tree.Count,
                currentTreeAudited = true,
                namespaceReady = !disabled
            }).AsObject();
            WriteOriginal(directory, phase + "-execute", execution);
            State(phase, target, disabled); Graph(phase, target);
            return execution;
        }
        WriteOriginal(directory, "initial-member", Member("Docs"));
        WriteOriginal(directory, "initial-tree", tree);
        State("initial", "Docs", false);
        JsonArray cases = [];
        foreach (string phase in new[] { "active", "disabled" })
        {
            bool disabled = phase == "disabled";
            State(phase + "-before", "Docs", disabled);
            JsonObject prepared = Prepare(phase, "Docs", "Docs Cancel " + phase);
            State(phase + "-prepared", "Docs", disabled, prepared);
            State(phase + "-restarted", "Docs", disabled, prepared);
            int previous = disabled ? 303 : 101, current = disabled ? 404 : 202;
            WriteOriginal(directory, phase + "-restart-before", Node(new { processId = previous }));
            WriteOriginal(directory, phase + "-restart-after", Node(new { processId = current }));
            WriteOriginal(directory, phase + "-restart-stop", Node(new { stopped = true }));
            WriteOriginal(directory, phase + "-restart-start", Node(new { started = true }));
            JsonObject cancelled = Node(new
            {
                operationId = prepared["operationId"]!.GetValue<string>(),
                rootId = root,
                phase = "Cancelled",
                sourceName = "Docs",
                targetName = "Docs Cancel " + phase,
                cancelledAt = "2026-10-28T01:00:00Z",
                currentDirectoryName = "Docs",
                memberCount = tree.Count,
                currentTreeAudited = true,
                namespaceReady = !disabled
            }).AsObject();
            WriteOriginal(directory, phase + "-cancel", cancelled);
            State(phase + "-cancelled", "Docs", disabled); Graph(phase + "-cancelled", "Docs");
            JsonObject later = Execute(phase + "-later", "Docs", "Docs After " + phase, disabled);
            JsonObject replay = cancelled.DeepClone().AsObject(); replay["currentDirectoryName"] = "Docs After " + phase;
            WriteOriginal(directory, phase + "-replay", replay);
            State(phase + "-replayed", "Docs After " + phase, disabled); Graph(phase + "-replayed", "Docs After " + phase);
            JsonObject restored = Execute(phase + "-restore", "Docs After " + phase, "Docs", disabled);
            cases.Add(new JsonObject
            {
                ["phase"] = phase,
                ["disabled"] = disabled,
                ["preparation"] = prepared,
                ["cancellation"] = cancelled,
                ["restart"] = Node(new { previousProcessId = previous, currentProcessId = current, previousProcessExited = true }),
                ["laterExecution"] = later,
                ["historicalReplay"] = replay,
                ["restoredExecution"] = restored
            });
        }
        State("restored", "Docs", false); Graph("restored", "Docs");
        return new JsonObject
        {
            ["schemaVersion"] = 1,
            ["rootId"] = root,
            ["instanceId"] = instance,
            ["memberId"] = member,
            ["nativeItemId"] = native,
            ["memberCount"] = tree.Count,
            ["directoryName"] = "Docs",
            ["residentAndSourceBytes"] = bytes,
            ["cases"] = cases,
            ["defaultHostIntegrated"] = true,
            ["actualWorkerRequired"] = true,
            ["preparedIntentAcrossIndependentHost"] = true,
            ["interruptedNativeExecution"] = false,
            ["journalAckReceiptInspected"] = false
        };
    }

    private static string Id() => Guid.NewGuid().ToString("D");
    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value)!;
    private static void WriteOriginal(string directory, string phase, JsonNode data) =>
        File.WriteAllText(Path.Combine(directory, phase + ".json"), new JsonObject { ["kind"] = "result", ["data"] = data.DeepClone() }.ToJsonString());
    private static void ChangeOriginal(string directory, string phase, Action<JsonNode> change)
    {
        string path = Path.Combine(directory, phase + ".json"); JsonNode node = JsonNode.Parse(File.ReadAllText(path))!;
        change(node["data"]!); File.WriteAllText(path, node.ToJsonString());
    }
    private static string FindPolicy()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            string path = Path.Combine(current.FullName, "eng", "cli-root-label-cancellation-policy.ps1");
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("The independent Host cancellation policy is missing.");
    }
}
