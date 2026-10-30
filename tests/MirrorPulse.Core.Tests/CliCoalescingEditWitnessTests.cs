using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CliCoalescingEditWitnessTests
{
    [TestMethod]
    [DataRow("unchangedGlobalCount", true)]
    [DataRow("decreasedGlobalCount", true)]
    [DataRow("earlierSeedAcknowledged", true)]
    [DataRow("secondEditRetainsFirst", true)]
    [DataRow("emptyEdit", true)]
    [DataRow("noFreshTarget", false)]
    [DataRow("oldTimestamp", false)]
    [DataRow("changedTarget", false)]
    [DataRow("partialBefore", false)]
    [DataRow("partialAfter", false)]
    [DataRow("liveOwner", false)]
    [DataRow("sdkAcknowledged", false)]
    [DataRow("mutated", false)]
    [DataRow("tombstone", false)]
    [DataRow("duplicateId", false)]
    [DataRow("duplicateSequence", false)]
    [DataRow("badPayload", false)]
    [DataRow("wrongAssociation", false)]
    [DataRow("changedOriginalPayload", false)]
    [DataRow("changedOriginalTimestamp", false)]
    [DataRow("requiredOriginalLost", false)]
    [DataRow("requiredOriginalForeign", false)]
    [DataRow("bytesChanged", false)]
    [DataRow("lengthChanged", false)]
    [DataRow("fileMissing", false)]
    [DataRow("emptyTarget", false)]
    [DataRow("missingUpperSequence", false)]
    [DataRow("stringUpperSequence", false)]
    [DataRow("scanIncomplete", false)]
    [DataRow("missingAttempt", false)]
    [DataRow("badTimestamp", false)]
    public async Task ExactEditWitnessRequiresFreshTargetOriginalsCompleteBytesAndRetainedEarlierEdits(string boundary, bool succeeds)
    {
        string root = Path.Combine(Path.GetTempPath(), "MirrorPulse-edit-witness-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string repository = SftpProtocolFixture.FindRepositoryRoot();
            string helper = Path.Combine(root, "witness.ps1"), runner = Path.Combine(root, "run.ps1"), local = Path.Combine(root, "local.bin");
            File.Copy(Path.Combine(repository, "eng", "cli-coalescing-edit-witness.ps1"), helper);
            byte[] expected = boundary == "emptyEdit" ? [] : Encoding.UTF8.GetBytes("PRIVATE_FIXTURE_EXPECTED_CONTENT");
            byte[] actual = boundary == "bytesChanged" ? Encoding.UTF8.GetBytes("PRIVATE_FIXTURE_UNWANTED_CONTENT") :
                boundary == "lengthChanged" ? [1] : expected;
            if (boundary != "fileMissing") await File.WriteAllBytesAsync(local, actual);
            await File.WriteAllTextAsync(runner, FixtureScript);
            var start = new ProcessStartInfo("pwsh") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", runner, "-Boundary", boundary, "-ExpectedBase64", Convert.ToBase64String(expected) })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            string text = await output, diagnostic = await errors;
            Assert.AreEqual(succeeds, process.ExitCode == 0, diagnostic);
            Assert.IsFalse(text.Contains("PRIVATE_", StringComparison.Ordinal));
            Assert.IsFalse(text.Contains(root, StringComparison.Ordinal));
            Assert.AreEqual(boundary != "fileMissing", File.Exists(local));
            if (boundary != "fileMissing") CollectionAssert.AreEqual(actual, await File.ReadAllBytesAsync(local));
            if (succeeds)
            {
                using JsonDocument document = JsonDocument.Parse(text);
                JsonElement witness = document.RootElement;
                Assert.IsFalse(witness.GetProperty("globalPendingCountUsedAsProof").GetBoolean());
                Assert.IsFalse(witness.GetProperty("originalSdkAcknowledgementExecuted").GetBoolean());
                Assert.IsFalse(witness.GetProperty("mutationExecuted").GetBoolean());
                Assert.AreEqual(Convert.ToHexString(SHA256.HashData(expected)), witness.GetProperty("localSha256").GetString());
                Assert.AreEqual(expected.Length, witness.GetProperty("localLength").GetInt32());
                Assert.AreEqual("22222222-2222-2222-2222-222222222222", witness.GetProperty("originalOperationIds")[0].GetString());
                Assert.AreEqual(1, witness.GetProperty("originalOperationIds").GetArrayLength());
                Assert.AreEqual(boundary == "secondEditRetainsFirst" ? 1 : 0, witness.GetProperty("retainedPrecedingOriginalIds").GetArrayLength());
            }
        }
        finally
        {
            string full = Path.GetFullPath(root);
            Assert.IsTrue(full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFileName(full).StartsWith("MirrorPulse-edit-witness-", StringComparison.Ordinal));
            Assert.AreEqual((FileAttributes)0, File.GetAttributes(full) & FileAttributes.ReparsePoint);
            Directory.Delete(full, recursive: true);
        }
    }

    private const string FixtureScript = """
        param([string]$Boundary,[string]$ExpectedBase64)
        $ErrorActionPreference='Stop'
        . (Join-Path $PSScriptRoot 'witness.ps1')
        $target='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';$other='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb'
        $written=[DateTimeOffset]::Parse('2026-10-30T12:00:00Z')
        function Row([string]$Id,[long]$Sequence,[string]$Item,[DateTimeOffset]$Created){
          [pscustomobject]@{operationId=$Id;sequence=$Sequence;kind='ContentUpdate';itemId=$Item;associatedWithTarget=($Item -ceq $target);attemptCount=0;createdAt=$Created.ToString('o');payloadSha256=('A'*64)}
        }
        function Snapshot([object[]]$Rows,[long]$Upper){
          [pscustomobject]@{schemaVersion=1;exclusiveCatalogOwner=$true;journalInventoryComplete=$true;targetItemId=$target;targetItemIsTombstone=$false;
          throughSequence=$Upper;lastScannedSequence=$Upper;journal=$Rows;originalSdkAcknowledgementExecuted=$false;mutationExecuted=$false;globalPendingCount=3}
        }
        $seed=Row '11111111-1111-1111-1111-111111111111' 1 $target ($written.AddSeconds(-1))
        $new=Row '22222222-2222-2222-2222-222222222222' 2 $target ($written.AddSeconds(1))
        $before=Snapshot @($seed) 1
        $after=Snapshot @($seed.PSObject.Copy(),$new) 2
        $required=@()
        switch($Boundary){
          'decreasedGlobalCount' {$after.globalPendingCount=2}
          'earlierSeedAcknowledged' {$after.journal=@($new)}
          'secondEditRetainsFirst' {$required=@([guid]$seed.operationId)}
          'noFreshTarget' {$new.itemId=$other;$new.associatedWithTarget=$false}
          'oldTimestamp' {$new.createdAt=$written.AddSeconds(-1).ToString('o')}
          'changedTarget' {$after.targetItemId=$other;foreach($row in $after.journal){$row.itemId=$other}}
          'partialBefore' {$before.journalInventoryComplete=$false}
          'partialAfter' {$after.journalInventoryComplete=$false}
          'liveOwner' {$after.exclusiveCatalogOwner=$false}
          'sdkAcknowledged' {$after.originalSdkAcknowledgementExecuted=$true}
          'mutated' {$after.mutationExecuted=$true}
          'tombstone' {$after.targetItemIsTombstone=$true}
          'duplicateId' {$new.operationId=$seed.operationId}
          'duplicateSequence' {$new.sequence=1}
          'badPayload' {$new.payloadSha256='INVALID'}
          'wrongAssociation' {$new.associatedWithTarget=$false}
          'changedOriginalPayload' {$after.journal[0].payloadSha256='B'*64}
          'changedOriginalTimestamp' {$after.journal[0].createdAt=$written.ToString('o')}
          'requiredOriginalLost' {$required=@([guid]$seed.operationId);$after.journal=@($new)}
          'requiredOriginalForeign' {$required=@([guid]'33333333-3333-3333-3333-333333333333')}
          'emptyTarget' {$after.targetItemId=[guid]::Empty.ToString('D')}
          'missingUpperSequence' {$after.throughSequence=$null}
          'stringUpperSequence' {$after.throughSequence='2'}
          'scanIncomplete' {$after.lastScannedSequence=1}
          'missingAttempt' {$new.attemptCount=$null}
          'badTimestamp' {$new.createdAt='INVALID'}
        }
        $beforeText=$before|ConvertTo-Json -Depth 6 -Compress;$afterText=$after|ConvertTo-Json -Depth 6 -Compress
        $witness=Get-CliCoalescingEditWitness -Before $before -After $after -WrittenAt $written -LocalPath (Join-Path $PSScriptRoot 'local.bin') -ExpectedContent ([Convert]::FromBase64String($ExpectedBase64)) -RequiredOriginalIds $required
        if(($before|ConvertTo-Json -Depth 6 -Compress) -cne $beforeText -or ($after|ConvertTo-Json -Depth 6 -Compress) -cne $afterText){throw 'Input snapshot mutated.'}
        $witness|ConvertTo-Json -Depth 6 -Compress
        """;
}
