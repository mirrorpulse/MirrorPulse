using System.Diagnostics;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class CliTransferFileWaitTests
{
    [TestMethod]
    [DataRow("sharing-released")]
    [DataRow("wrong-bytes")]
    [DataRow("other-io")]
    [DataRow("access-denied")]
    [DataRow("already-matches")]
    public async Task ActualTransferProducerWaitsOnlyForTransientSharingAndStillRequiresTheWholeExpectedHash(string scenario)
    {
        string directory = Path.Combine(Path.GetTempPath(), "MirrorPulse-transfer-wait-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "exercise.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Repository,[string]$Fixture,[string]$Scenario)
                $ErrorActionPreference='Stop'
                $tokens=$null;$errors=$null
                $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $Repository 'eng/verify-cli-transfers.ps1'),[ref]$tokens,[ref]$errors)
                if($errors.Count){throw 'The actual producer does not parse.'}
                $functions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
                    $node.Name -cin @('Read-TransferHash','Wait-TransferHash')},$true))
                if($functions.Count -ne 2){throw 'The original producer hash functions are missing.'}
                foreach($function in $functions){. ([scriptblock]::Create($function.Extent.Text))}
                $target=Join-Path $Fixture 'source.bin';$ready=Join-Path $Fixture 'ready';$attempt=Join-Path $Fixture 'attempt'
                [IO.File]::WriteAllText($target,'actual expected bytes')
                $expected=(Read-TransferHash $target)
                if($Scenario -eq 'wrong-bytes'){[IO.File]::WriteAllText($target,'different actual bytes')}
                $script:hashCalls=0;$script:originalHash=(Get-Item function:Read-TransferHash).ScriptBlock
                function Read-TransferHash {
                    param([string]$Path)
                    $script:hashCalls++
                    if($Scenario -eq 'other-io'){throw [IO.IOException]::new('Original unrelated I/O failure.',-2147024865)}
                    if($Scenario -eq 'access-denied'){throw [UnauthorizedAccessException]::new('Original access refusal.')}
                    if($Scenario -eq 'sharing-released' -and $script:hashCalls -eq 1){[IO.File]::WriteAllText($attempt,'attempted')}
                    & $script:originalHash -Path $Path
                }
                $writer=$null
                try {
                    if($Scenario -eq 'sharing-released'){
                        $writerScript=Join-Path $Fixture 'writer.ps1'
                        @'
                param([string]$Target,[string]$Ready,[string]$Attempt)
                $ErrorActionPreference='Stop'
                $stream=[IO.File]::Open($Target,[IO.FileMode]::Open,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try {
                    [IO.File]::WriteAllText($Ready,'locked')
                    $deadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
                    while(-not(Test-Path -LiteralPath $Attempt)){
                        if([DateTimeOffset]::UtcNow -ge $deadline){throw 'The reader never attempted its actual observation.'}
                        Start-Sleep -Milliseconds 10
                    }
                    Start-Sleep -Milliseconds 600
                }finally{$stream.Dispose()}
                '@ | Set-Content -LiteralPath $writerScript -Encoding utf8
                        $start=[Diagnostics.ProcessStartInfo]::new('pwsh')
                        $start.UseShellExecute=$false;$start.CreateNoWindow=$true
                        foreach($argument in @('-NoProfile','-File',$writerScript,'-Target',$target,'-Ready',$ready,'-Attempt',$attempt)){$start.ArgumentList.Add($argument)}
                        $writer=[Diagnostics.Process]::Start($start)
                        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(10)
                        while(-not(Test-Path -LiteralPath $ready)){
                            if($writer.HasExited -or [DateTimeOffset]::UtcNow -ge $deadline){throw 'The real writer did not acquire its exclusive ordinary-file handle.'}
                            Start-Sleep -Milliseconds 10
                        }
                    }
                    $phase='OriginalContentObservation';$failure=$null;$succeeded=$false
                    try{Wait-TransferHash -Path $target -Expected $expected;$succeeded=$true}
                    catch{$failure=[ordered]@{type=$_.Exception.GetType().FullName;hresult=$_.Exception.HResult.ToString('X8');message=$_.Exception.Message}}
                    if($null -ne $writer){
                        if(-not $writer.WaitForExit(5000) -or $writer.ExitCode -ne 0){throw 'The owned writer did not finish successfully.'}
                    }
                    [ordered]@{succeeded=$succeeded;hashCalls=$script:hashCalls;failure=$failure} | ConvertTo-Json -Compress -Depth 4
                }finally{
                    if($null -ne $writer){if(-not $writer.HasExited){$writer.Kill($true);$writer.WaitForExit()};$writer.Dispose()}
                }
                """);
            var start = new ProcessStartInfo("pwsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-File", script, SftpProtocolFixture.FindRepositoryRoot(), directory, scenario })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(45)); }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
            Assert.AreEqual(0, process.ExitCode, await error);
            using var json = JsonDocument.Parse((await output).Trim()); var result = json.RootElement;
            Assert.AreEqual(scenario is "sharing-released" or "already-matches", result.GetProperty("succeeded").GetBoolean());
            if (scenario is "sharing-released" or "wrong-bytes") Assert.IsGreaterThan(1, result.GetProperty("hashCalls").GetInt32());
            else Assert.AreEqual(1, result.GetProperty("hashCalls").GetInt32());
            if (scenario == "other-io") Assert.AreEqual("8007001F", result.GetProperty("failure").GetProperty("hresult").GetString());
            if (scenario == "access-denied") Assert.AreEqual("System.UnauthorizedAccessException", result.GetProperty("failure").GetProperty("type").GetString());
            if (scenario == "wrong-bytes") StringAssert.StartsWith(result.GetProperty("failure").GetProperty("message").GetString(),
                "The actual transfer or later user write did not produce its expected complete source bytes.");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
