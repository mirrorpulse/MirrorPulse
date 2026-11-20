using System.Diagnostics;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CliRootLabelFailureLogRetentionTests
{
    [TestMethod]
    [DataRow(0, 23)]
    [DataRow(600, 32)]
    public async Task FailureCollectionRetainsTheWholeBoundedCancellationRequestAndOriginalCoarseSequence(int earlierRecords, int coarseRecords)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-root-label-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "exercise.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Repository,[string]$Fixture,[int]$EarlierRecords,[int]$CoarseRecords)
                $ErrorActionPreference='Stop'
                . (Join-Path $Repository 'eng/cli-integration-failure-policy.ps1')
                $data=Join-Path $Fixture 'data';$evidence=Join-Path $Fixture 'evidence/result.json'
                New-Item -ItemType Directory -Path (Join-Path $data 'logs/root-labels'),(Join-Path $data 'logs/cloud-files') -Force|Out-Null
                $request=[guid]::NewGuid().ToString('D');$earlier=[guid]::NewGuid().ToString('D')
                $records=[Collections.Generic.List[string]]::new()
                for($i=0;$i -lt $EarlierRecords;$i++){$records.Add((@{Code='RootLabelCancellationProgress';Fields=@{operationId=$earlier}}|ConvertTo-Json -Compress))}
                for($i=1;$i -le 10;$i++){$records.Add((@{Code='RootLabelCancellationProgress';Fields=@{operationId=$request;cancellationSequence=$i.ToString()}}|ConvertTo-Json -Compress))}
                for($i=1;$i -le 128;$i++){$records.Add((@{Code='RootLabelCancellationBornProgress';Fields=@{operationId=$request;cancellationBornStep='NamespaceBornRecoveryContextBegin';cancellationBornSequence=$i.ToString();cancellationBornElapsedMs=$i.ToString()}}|ConvertTo-Json -Compress))}
                $records.Add((@{Code='RootLabelCancellationBornProgressLimit';Fields=@{operationId=$request;cancellationBornSequence='129';cancellationBornTruncated='true'}}|ConvertTo-Json -Compress))
                for($i=11;$i -le $CoarseRecords;$i++){$records.Add((@{Code='RootLabelCancellationProgress';Fields=@{operationId=$request;cancellationSequence=$i.ToString()}}|ConvertTo-Json -Compress))}
                $source=Join-Path $data 'logs/root-labels/mirrorpulse.log';$records|Set-Content -LiteralPath $source -Encoding utf8
                1..60|Set-Content -LiteralPath (Join-Path $data 'logs/mirrorpulse.log')
                1..300|Set-Content -LiteralPath (Join-Path $data 'logs/cloud-files/mirrorpulse.log')
                $before=(Get-FileHash -LiteralPath $source).Hash
                try{throw [IO.IOException]::new('original request failure at '+$Fixture)}catch{$original=$_}
                $observed=@(foreach($phase in @('Execution','Cleanup')){
                    Write-CliFixtureFailure -Failure $original -Phase $phase -EvidencePath $evidence -FixtureRoot $Fixture -DataRoot $data
                    $prefix=Join-Path $Fixture ('evidence/cli-regression.originals/'+$phase.ToLowerInvariant())
                    $lines=@(Get-Content -LiteralPath ($prefix+'-logs-root-labels-mirrorpulse.log'))
                    $own=@($lines|ForEach-Object {$_|ConvertFrom-Json}|Where-Object {$_.Fields.operationId -ceq $request})
                    [ordered]@{phase=$phase;retainedCount=$lines.Count;requestCount=$own.Count
                        detailSequences=@($own|Where-Object Code -CEQ RootLabelCancellationBornProgress|ForEach-Object {[int]$_.Fields.cancellationBornSequence})
                        coarseSequences=@($own|Where-Object Code -CEQ RootLabelCancellationProgress|ForEach-Object {[int]$_.Fields.cancellationSequence})
                        limits=@($own|Where-Object Code -CEQ RootLabelCancellationBornProgressLimit).Count
                        generalCount=@(Get-Content -LiteralPath ($prefix+'-logs-mirrorpulse.log')).Count
                        cloudCount=@(Get-Content -LiteralPath ($prefix+'-logs-cloud-files-mirrorpulse.log')).Count
                        message=(Get-Content -LiteralPath ($prefix+'.json') -Raw|ConvertFrom-Json).message}
                })
                [ordered]@{sourceUnchanged=($before -ceq (Get-FileHash -LiteralPath $source).Hash);phases=$observed}|ConvertTo-Json -Compress -Depth 6
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", script, repository, directory, earlierRecords.ToString(System.Globalization.CultureInfo.InvariantCulture), coarseRecords.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.AreEqual(0, process.ExitCode, await error);
            string jsonLine = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries).Last().Trim();
            using var json = JsonDocument.Parse(jsonLine);
            Assert.IsTrue(json.RootElement.GetProperty("sourceUnchanged").GetBoolean());
            var phases = json.RootElement.GetProperty("phases").EnumerateArray().ToArray(); Assert.HasCount(2, phases);
            foreach (var phase in phases)
            {
                Assert.AreEqual(Math.Min(earlierRecords + coarseRecords + 129, 512), phase.GetProperty("retainedCount").GetInt32());
                Assert.AreEqual(coarseRecords + 129, phase.GetProperty("requestCount").GetInt32());
                CollectionAssert.AreEqual(Enumerable.Range(1, 128).ToArray(), phase.GetProperty("detailSequences").EnumerateArray().Select(value => value.GetInt32()).ToArray());
                CollectionAssert.AreEqual(Enumerable.Range(1, coarseRecords).ToArray(), phase.GetProperty("coarseSequences").EnumerateArray().Select(value => value.GetInt32()).ToArray());
                Assert.AreEqual(1, phase.GetProperty("limits").GetInt32());
                Assert.AreEqual(32, phase.GetProperty("generalCount").GetInt32()); Assert.AreEqual(256, phase.GetProperty("cloudCount").GetInt32());
                StringAssert.Contains(phase.GetProperty("message").GetString()!, "original request failure at <fixture>");
            }
            Assert.IsFalse(jsonLine.Contains(directory, StringComparison.OrdinalIgnoreCase));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
