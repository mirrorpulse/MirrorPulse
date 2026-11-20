using System.Diagnostics;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CliDisabledSettlementEvidenceTests
{
    [TestMethod]
    [DataRow("identity-valid")]
    [DataRow("identity-no-confirmation")]
    [DataRow("identity-foreign-member")]
    [DataRow("identity-binding-changed")]
    [DataRow("identity-wrong-native")]
    [DataRow("identity-wrong-original")]
    [DataRow("ack-valid")]
    [DataRow("ack-only-before")]
    [DataRow("ack-wrong-payload")]
    [DataRow("ack-wrong-revision")]
    [DataRow("ack-foreign-root")]
    [DataRow("ack-wrong-binding")]
    [DataRow("ack-still-pending")]
    [DataRow("ack-header-remains")]
    [DataRow("ack-no-baseline")]
    [DataRow("ack-pair-mismatch")]
    [DataRow("ack-reversed-order")]
    [DataRow("ack-duplicate-success")]
    [DataRow("ack-invalid-correlation")]
    public async Task ReenabledEvidenceSeparatesOriginalMembershipFromConfirmedIdentityAndRequiresTheRealAckPair(string scenario)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-disabled-evidence", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . $Policy
                # Boundary validation data only: this script executes no SDK/native/source/ACK operation.
                $member='11111111-1111-4111-8111-111111111111';$root='22222222-2222-4222-8222-222222222222'
                $item='33333333-3333-4333-8333-333333333333';$accepted='44444444-4444-4444-8444-444444444444'
                $operation='55555555-5555-4555-8555-555555555555';$instance='66666666-6666-4666-8666-666666666666'
                $correlation='77777777-7777-4777-8777-777777777777'
                $original=[pscustomobject]@{operationId=$operation;itemId=$item;kind='ContentUpdate';sequence=42;payloadSha256=('A'*64)}
                $before=[pscustomobject]@{
                    members=@([pscustomobject]@{key='file';memberId=$member;rootId=$root;isDirectory=$false;originalItemId=$item;originalBindingSha256=('B'*64)})
                    identities=@([pscustomobject]@{key='file';memberId=$member;itemId=$item;originalItemId=$item;acceptanceOperationId=$null;acceptedRevisionSha256=$null;nativeBindingSha256=('C'*64)})
                    roots=@([pscustomobject]@{rootId=$root;instanceId=$instance})
                    remoteSnapshots=@([pscustomobject]@{rootId=$root;entries=@([pscustomobject]@{publicItemId=$item;remoteRevisionSha256=('D'*64)})})
                    journal=@($original)
                }
                $after=$before|ConvertTo-Json -Depth 12|ConvertFrom-Json
                $after.journal=@()
                $outcome=[pscustomobject]@{operationId=$operation;pending=$false;effects=@([pscustomobject]@{state='Missing'})}
                $inspection=[pscustomobject]@{memberId=$member;rootId=$root;isDirectory=$false;nativeItemId=$accepted}
                $fields=[pscustomobject]@{correlationId=$correlation;operationId=$operation;instanceId=$instance;rootId=$root;kind='ContentUpdate';journalSequence='42';
                    journalPayloadSha256=('A'*64);nativeIdentitySha256=('E'*64);nativeBindingSha256=('C'*64);acknowledgedRevisionSha256=('D'*64)}
                $observation=[pscustomobject]@{Code='JournalAcceptedContentObservation';OccurredAt='2026-11-11T00:00:00Z';Fields=$fields}
                $ack=[pscustomobject]@{Code='JournalAcceptedContentAcknowledged';OccurredAt='2026-11-11T00:00:01Z';Fields=($fields|ConvertTo-Json|ConvertFrom-Json)}
                $events=@($observation,$ack)
                if($Scenario.StartsWith('identity-',[StringComparison]::Ordinal)){
                    $after.identities[0].itemId=$accepted;$after.identities[0].acceptanceOperationId=$operation
                    switch($Scenario){
                        'identity-valid' {}
                        'identity-no-confirmation' {$after.identities[0].acceptanceOperationId=$null}
                        'identity-foreign-member' {$inspection.memberId=$instance}
                        'identity-binding-changed' {$after.members[0].originalBindingSha256=('F'*64)}
                        'identity-wrong-native' {$inspection.nativeItemId=$item}
                        'identity-wrong-original' {$after.identities[0].originalItemId=$accepted}
                        default {throw 'Unknown identity evidence case.'}
                    }
                }else{
                    switch($Scenario){
                        'ack-valid' {}
                        'ack-only-before' {$events=@($observation)}
                        'ack-wrong-payload' {$observation.Fields.journalPayloadSha256=('F'*64);$ack.Fields.journalPayloadSha256=('F'*64)}
                        'ack-wrong-revision' {$observation.Fields.acknowledgedRevisionSha256=('F'*64);$ack.Fields.acknowledgedRevisionSha256=('F'*64)}
                        'ack-foreign-root' {$observation.Fields.rootId=$instance;$ack.Fields.rootId=$instance}
                        'ack-wrong-binding' {$observation.Fields.nativeBindingSha256=('F'*64);$ack.Fields.nativeBindingSha256=('F'*64)}
                        'ack-still-pending' {$outcome.pending=$true}
                        'ack-header-remains' {$after.journal=@($original)}
                        'ack-no-baseline' {$before.remoteSnapshots=@()}
                        'ack-pair-mismatch' {$ack.Fields.nativeIdentitySha256=('F'*64)}
                        'ack-reversed-order' {$ack.OccurredAt='2026-11-10T00:00:00Z'}
                        'ack-duplicate-success' {$events+=@($ack)}
                        'ack-invalid-correlation' {$observation.Fields.correlationId='unqualified';$ack.Fields.correlationId='unqualified'}
                        default {throw 'Unknown actual ACK evidence case.'}
                    }
                }
                $refused=$false
                try{
                    if($Scenario.StartsWith('identity-',[StringComparison]::Ordinal)){
                        Assert-CliDisabledCurrentIdentity -Before $before -After $after -Inspection $inspection -Key file
                    }else{
                        Assert-CliDisabledAcceptedObservation -Before $before -After $after -Original $original -Outcome $outcome -Events $events
                    }
                }catch{$refused=$true}
                $expected=$Scenario -cnotin @('identity-valid','ack-valid')
                if($refused -ne $expected){throw 'The reenable evidence gate accepted unrelated/incomplete facts or rejected its complete boundary input.'}
                "Evidence validation only: $Scenario"
                """.ReplaceLineEndings("\r\n"));
            var start = new ProcessStartInfo("pwsh")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            foreach (string value in new[] { "-NoProfile", "-File", script, "-Policy",
                Path.Combine(SftpProtocolFixture.FindRepositoryRoot(), "eng", "cli-disabled-settlement-evidence.ps1"), "-Scenario", scenario }) start.ArgumentList.Add(value);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The evidence policy process did not start.");
            try
            {
                Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                await process.WaitForExitAsync(deadline.Token);
                Assert.AreEqual(0, process.ExitCode, await error + await output);
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
