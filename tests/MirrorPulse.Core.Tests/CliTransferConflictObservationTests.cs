using System.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CliTransferConflictObservationTests
{
    [TestMethod]
    [DataRow("valid")]
    [DataRow("mixed")]
    [DataRow("bounded")]
    [DataRow("invalid")]
    [DataRow("oversized")]
    [DataRow("failed")]
    public async Task FailureConflictQueryRetainsOnlyBoundedActualPublicRelations(string scenario)
    {
        string prefix = Path.Combine(Path.GetTempPath(), "MirrorPulse-conflict-observation-tests");
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "probe.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . $Policy
                $cli=Join-Path $Directory 'cli.ps1';$input=Join-Path $Directory 'input.json'
                $capture=Join-Path $Directory 'arguments.json';$env:MP_OBSERVATION_FIXTURE=$Directory
                [IO.File]::WriteAllText($cli,@'
                [IO.File]::AppendAllText((Join-Path $env:MP_OBSERVATION_FIXTURE 'calls.txt'),'1')
                $args|ConvertTo-Json -Compress|Set-Content (Join-Path $env:MP_OBSERVATION_FIXTURE 'arguments.json')
                [IO.File]::ReadAllText((Join-Path $env:MP_OBSERVATION_FIXTURE 'input.json'))
                if(Test-Path (Join-Path $env:MP_OBSERVATION_FIXTURE 'fail')){exit 23}
                exit 0
                '@)
                $id=[guid]::NewGuid().ToString('D');$instance=[guid]::NewGuid().ToString('D')
                $item=[ordered]@{conflictId=$id;instanceId=$instance;reason='StaleRemoteRevision';source='Upload';status='Pending';
                    relativePath='PRIVATE_TEST_SENTINEL';localRevision='PRIVATE_TEST_SENTINEL-a';remoteRevision='PRIVATE_TEST_SENTINEL-b';message='PRIVATE_TEST_SENTINEL'}
                $items=@($item)
                if($Scenario -ceq 'bounded'){$items=@(1..101|ForEach-Object {$item})}
                if($Scenario -ceq 'mixed'){
                    $items=@('Equal','LocalMissing','RemoteMissing','BothMissing','Unavailable'|ForEach-Object {
                        $copy=[ordered]@{};foreach($key in $item.Keys){$copy[$key]=$item[$key]}
                        switch($_){
                            'Equal' {$copy.remoteRevision=$copy.localRevision}
                            'LocalMissing' {$copy.localRevision=$null}
                            'RemoteMissing' {$copy.remoteRevision=$null}
                            'BothMissing' {$copy.localRevision=$null;$copy.remoteRevision=''}
                            'Unavailable' {$copy.localRevision=@{private='PRIVATE_TEST_SENTINEL'};$copy.reason='PRIVATE_TEST_SENTINEL';$copy.source='PRIVATE_TEST_SENTINEL';$copy.status='PRIVATE_TEST_SENTINEL';$copy.conflictId='PRIVATE_TEST_SENTINEL'}
                        };$copy
                    })
                }
                $value=@{kind='result';data=@{items=$items;nextCursor=$(if($Scenario -ceq 'bounded'){'PRIVATE_TEST_SENTINEL'}else{$null})}}|ConvertTo-Json -Depth 8
                if($Scenario -ceq 'invalid'){$value='PRIVATE_TEST_SENTINEL'}
                if($Scenario -ceq 'oversized'){$value='x'*1048577}
                [IO.File]::WriteAllText($input,$value)
                if($Scenario -ceq 'failed'){[IO.File]::WriteAllText((Join-Path $Directory 'fail'),'')}
                $result=Get-CliTransferConflictObservations -CliPath $cli
                if([IO.File]::ReadAllText((Join-Path $Directory 'calls.txt')) -cne '1'){throw 'Exactly one public read-only query is allowed.'}
                $arguments=@(Get-Content $capture -Raw|ConvertFrom-Json)
                if([string]::Join('|',$arguments) -cne '--json|--developer-mode|--no-start|--timeout|5|conflict|list'){throw 'Only the bounded non-starting conflict list query is allowed.'}
                $json=$result|ConvertTo-Json -Depth 6 -Compress
                if($json.Contains('PRIVATE_TEST_SENTINEL') -or $json.Contains($Directory)){throw 'Private path, revision, message or cursor escaped.'}
                if($Scenario -cin @('invalid','oversized','failed')){
                    if(-not $result.observationUnavailable -or $result.items.Count -ne 0 -or $null -ne $result.count){throw 'Invalid/failed data cannot create conflict facts.'}
                }else{
                    if($result.observationUnavailable){throw 'Actual public conflict facts were lost.'}
                    if($Scenario -ceq 'bounded'){
                        if($result.count -ne 101 -or $result.items.Count -ne 64 -or -not $result.truncated -or -not $result.moreAvailable){throw 'The bounded prefix and undisclosed continuation were lost.'}
                    }elseif($Scenario -ceq 'mixed'){
                        if([string]::Join(',',@($result.items.revisionRelation)) -cne 'Equal,LocalMissing,RemoteMissing,BothMissing,Unavailable' -or
                            $result.items[-1].reason -cne 'Unclassified' -or $result.items[-1].source -cne 'Unclassified' -or
                            $result.items[-1].status -cne 'Unclassified' -or $null -ne $result.items[-1].conflictId){throw 'Unknown or malformed fields were adopted.'}
                    }elseif($result.items.Count -ne 1 -or $result.items[0].conflictId -cne $id -or $result.items[0].instanceId -cne $instance -or
                        $result.items[0].reason -cne 'StaleRemoteRevision' -or $result.items[0].source -cne 'Upload' -or
                        $result.items[0].status -cne 'Pending' -or $result.items[0].revisionRelation -cne 'Different'){throw 'The actual closed conflict identity and relation changed.'}
                }
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string value in new[] { "-NoProfile", "-File", script, "-Policy", Path.Combine(SftpProtocolFixture.FindRepositoryRoot(), "eng", "cli-transfer-conflict-observations.ps1"), "-Directory", directory, "-Scenario", scenario })
                start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new IOException("The synthetic observation probe did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            Assert.AreEqual(0, process.ExitCode, await output + await error);
        }
        finally { Cleanup(prefix, directory); }
    }

    private static void Cleanup(string prefix, string directory)
    {
        string target = Path.GetFullPath(directory);
        if (!target.StartsWith(Path.GetFullPath(prefix) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || new DirectoryInfo(target).LinkTarget is not null)
            throw new InvalidOperationException("Synthetic observation cleanup must remain inside its original directory.");
        Directory.Delete(target, recursive: true);
    }
}
