using System.Diagnostics;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

[TestClass]
public sealed class NativeTestOriginalsTests
{
    private static readonly string[] OriginalOutcomes = ["Passed", "Failed", "NotExecuted"];

    [TestMethod]
    [DataRow("originals", true, true, true)]
    [DataRow("wrongSource", false, false, false)]
    [DataRow("controllerHash", false, false, false)]
    [DataRow("missingInput", false, false, false)]
    [DataRow("escape", false, false, false)]
    [DataRow("staleResults", false, false, false)]
    [DataRow("changedInput", false, true, false)]
    [DataRow("changedRetainedInput", false, true, false)]
    [DataRow("changedCollector", false, true, false)]
    [DataRow("foreignAssembly", false, true, false)]
    [DataRow("duplicateDefinition", false, true, false)]
    [DataRow("missingDefinition", false, true, false)]
    [DataRow("doctype", false, true, false)]
    [DataRow("staleTrx", false, true, false)]
    [DataRow("changedPreparation", false, true, false)]
    [DataRow("replacedInputBinding", false, true, false)]
    [DataRow("wrappedOriginals", true, true, true)]
    [DataRow("actionFailure", false, true, true)]
    [DataRow("bothFailures", false, true, false)]
    [DataRow("bundleOriginals", true, true, true)]
    [DataRow("bundlePrivateConfig", true, true, true)]
    [DataRow("bundleMarker", false, true, true)]
    [DataRow("bundleMissingCollected", false, true, false)]
    [DataRow("bundleChangedInput", false, true, true)]
    [DataRow("bundleChangedCollector", false, true, true)]
    [DataRow("bundleMissingTrx", false, true, true)]
    public async Task CollectorBindsNativeRuntimeInputsAndRetainsOriginalNegativeOutcomes(string boundary, bool succeeds, bool prepared, bool collected)
    {
        string repository = SftpProtocolFixture.FindRepositoryRoot();
        string parent = Path.Combine(Path.GetTempPath(), "MirrorPulse-native-originals-" + Guid.NewGuid().ToString("N"));
        string root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        bool bundle = boundary.StartsWith("bundle", StringComparison.Ordinal);
        string framework = new DirectoryInfo(Path.GetDirectoryName(typeof(NativeTestOriginalsTests).Assembly.Location)!).Name;
        string runtime = Path.Combine(repository, "tests", "MirrorPulse.CloudFiles.CfSharp.Tests", "bin", "Release", framework);
        Directory.CreateDirectory(Path.Combine(root, "eng"));
        Directory.CreateDirectory(Path.Combine(root, "payload"));
        try
        {
            File.Copy(Path.Combine(repository, "eng", "native-test-originals.ps1"), Path.Combine(root, "eng", "native-test-originals.ps1"));
            File.Copy(Path.Combine(repository, "eng", "native-test-original-bundle.ps1"), Path.Combine(root, "eng", "native-test-original-bundle.ps1"));
            foreach (string name in new[] { "MirrorPulse.CloudFiles.CfSharp.Tests.dll", "MirrorPulse.CloudFiles.CfSharp.Tests.deps.json",
                "MirrorPulse.CloudFiles.CfSharp.Tests.runtimeconfig.json", "MirrorPulse.Core.dll", "MirrorPulse.CloudFiles.CfSharp.dll",
                "MirrorPulse.CfSharp.CrashProbe.dll", "MirrorPulse.CfSharp.CrashProbe.deps.json", "MirrorPulse.CfSharp.CrashProbe.runtimeconfig.json",
                "CfSharp.dll", "CfSharp.Native.dll", "CfSharp.Storage.Sqlite.dll", "MirrorPulse.Adapter.Sdk.dll" })
                File.Copy(Path.Combine(runtime, name), Path.Combine(root, "payload", name));
            string source = FileVersionInfo.GetVersionInfo(Path.Combine(runtime, "MirrorPulse.CloudFiles.CfSharp.Tests.dll")).ProductVersion!.Split('+')[1];
            Assert.AreEqual(40, source.Length);
            string script = Path.Combine(root, "run.ps1");
            await File.WriteAllTextAsync(script, FixtureScript);
            var start = new ProcessStartInfo("pwsh") { WorkingDirectory = root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-File", script, "-Workspace", root, "-SourceSha", source, "-Boundary", boundary })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            Task<string> output = process.StandardOutput.ReadToEndAsync(), errors = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(1));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
            string diagnostic = await output + await errors;
            Assert.AreEqual(succeeds, process.ExitCode == 0, diagnostic);
            if (boundary is "actionFailure" or "bothFailures") StringAssert.Contains(diagnostic, "ORIGINAL_NATIVE_FAILURE");
            string originals = bundle ? Path.Combine(root, "work", "native-originals") : Path.Combine(root, "originals");
            Assert.AreEqual(prepared, File.Exists(Path.Combine(originals, "prepared.json")));
            Assert.AreEqual(collected, File.Exists(Path.Combine(originals, "collected.json")));
            if (collected)
            {
                using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(originals, "collected.json")));
                JsonElement value = document.RootElement;
                Assert.AreEqual(source, value.GetProperty("sourceSha").GetString());
                Assert.AreEqual(3, value.GetProperty("resultCount").GetInt32());
                Assert.IsFalse(value.GetProperty("gateSuccessEstablishedByCollection").GetBoolean());
                CollectionAssert.AreEqual(OriginalOutcomes,
                    value.GetProperty("results").EnumerateArray().Select(row => row.GetProperty("outcome").GetString()).ToArray());
                Assert.IsTrue(value.GetProperty("results").EnumerateArray().All(row => row.GetProperty("name").GetString() == "Same display name"));
                if (boundary != "bundleMissingTrx")
                {
                    string trx = bundle ? Path.Combine(root, "work", "results", "namespace.trx") : Path.Combine(root, "results", "native.trx");
                    CollectionAssert.AreEqual(await File.ReadAllBytesAsync(trx),
                        await File.ReadAllBytesAsync(Path.Combine(originals, "results", bundle ? "namespace.trx" : "native.trx")));
                }
                using JsonDocument preparation = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(originals, "prepared.json")));
                Assert.AreEqual(12, preparation.RootElement.GetProperty("inputs").GetArrayLength());
                if (boundary != "bundleChangedInput")
                    foreach (JsonElement input in preparation.RootElement.GetProperty("inputs").EnumerateArray())
                        CollectionAssert.AreEqual(await File.ReadAllBytesAsync(input.GetProperty("path").GetString()!),
                            await File.ReadAllBytesAsync(Path.Combine(originals, input.GetProperty("archivePath").GetString()!)));
            }
            if (bundle && succeeds)
            {
                string retained = Path.Combine(parent, "retained");
                using JsonDocument receipt = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(retained, "controller-retention.json")));
                Assert.IsTrue(receipt.RootElement.GetProperty("verifiedComplete").GetBoolean());
                Assert.IsFalse(receipt.RootElement.GetProperty("gateSuccessEstablishedByRetention").GetBoolean());
                Assert.AreEqual(16, receipt.RootElement.GetProperty("files").GetArrayLength());
                Assert.AreEqual(17, Directory.EnumerateFiles(retained, "*", SearchOption.AllDirectories).Count());
                Assert.IsFalse(File.Exists(Path.Combine(retained, "configuration.json")));
                Assert.IsFalse(File.Exists(Path.Combine(retained, "private.key")));
            }
        }
        finally
        {
            string full = Path.GetFullPath(parent);
            Assert.IsTrue(full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase));
            Assert.IsTrue(Path.GetFileName(full).StartsWith("MirrorPulse-native-originals-", StringComparison.Ordinal));
            Assert.AreEqual((FileAttributes)0, File.GetAttributes(full) & FileAttributes.ReparsePoint);
            Directory.Delete(full, recursive: true);
        }
    }

    private const string FixtureScript = """
        param([string]$Workspace,[string]$SourceSha,[string]$Boundary)
        $ErrorActionPreference='Stop'
        . (Join-Path $Workspace 'eng/native-test-originals.ps1')
        $assembly=Join-Path $Workspace 'payload/MirrorPulse.CloudFiles.CfSharp.Tests.dll'
        $results=Join-Path $Workspace 'results';$originals=Join-Path $Workspace 'originals'
        $ordinary=$Boundary.StartsWith('bundle',[StringComparison]::Ordinal)
        $suite='native';$trxName='native.trx'
        if($ordinary){
          $suite='namespace-nonadmin';$trxName='namespace.trx'
          $results=Join-Path $Workspace 'work/results';$originals=Join-Path $Workspace 'work/native-originals'
          [IO.File]::WriteAllText((Join-Path $Workspace '.mp-namespace-ci'),[IO.Path]::GetFileName($Workspace))
        }
        $expectedHash=(Get-FileHash -LiteralPath $assembly).Hash
        $collectorHash=(Get-FileHash -LiteralPath (Join-Path $Workspace 'eng/native-test-originals.ps1')).Hash
        if($Boundary -ceq 'wrongSource'){$SourceSha='f'*40}
        if($Boundary -ceq 'controllerHash'){$expectedHash='0'*64}
        if($Boundary -ceq 'missingInput'){Remove-Item -LiteralPath (Join-Path $Workspace 'payload/MirrorPulse.Core.dll')}
        if($Boundary -ceq 'escape'){$originals=Join-Path $Workspace '../outside'}
        if($Boundary -ceq 'staleResults'){New-Item -ItemType Directory -Path $results|Out-Null;'<TestRun/>'|Set-Content -LiteralPath (Join-Path $results 'old.trx')}
        $wrapped=$Boundary -cin @('wrappedOriginals','actionFailure','bothFailures')
        if(-not $wrapped){Start-NativeTestOriginals -Workspace $Workspace -SourceSha $SourceSha -Suite $suite -TestAssembly $assembly -ResultsDirectory $results -OutputDirectory $originals -ExpectedTestAssemblySha256 $expectedHash}
        $action={
        New-Item -ItemType Directory -Path $results -Force|Out-Null
        $ids=@('11111111-1111-1111-1111-111111111111','22222222-2222-2222-2222-222222222222','33333333-3333-3333-3333-333333333333')
        $outcomes=@('Passed','Failed','NotExecuted');$definitions=@();$rows=@()
        $codeBase=[Security.SecurityElement]::Escape($assembly)
        if($Boundary -ceq 'foreignAssembly'){$codeBase=[Security.SecurityElement]::Escape((Join-Path $Workspace 'payload/MirrorPulse.Core.dll'))}
        foreach($index in 0..2){
          $definitions+='<UnitTest id="'+$ids[$index]+'"><TestMethod codeBase="'+$codeBase+'" className="NativeInputFixture" name="Case'+$index+'"/></UnitTest>'
          $rows+='<UnitTestResult testId="'+$ids[$index]+'" executionId="'+[guid]::NewGuid().ToString('D')+'" testName="Same display name" outcome="'+$outcomes[$index]+'"/>'
        }
        if($Boundary -ceq 'duplicateDefinition'){$definitions+=$definitions[0]}
        if($Boundary -ceq 'missingDefinition'){$definitions=$definitions[0..1]}
        $started=[DateTimeOffset]::UtcNow
        if($Boundary -ceq 'staleTrx'){$started=$started.AddDays(-1)}
        $xml='<TestRun><Times start="'+$started.ToString('o')+'"/><TestDefinitions>'+($definitions -join '')+'</TestDefinitions><Results>'+($rows -join '')+'</Results></TestRun>'
        if($Boundary -ceq 'doctype'){$xml='<!DOCTYPE TestRun [<!ENTITY x SYSTEM "file:///unread-fixture">]><TestRun>&x;</TestRun>'}
        [IO.File]::WriteAllText((Join-Path $results $trxName),$xml)
        if($Boundary -ceq 'changedInput'){[IO.File]::AppendAllText((Join-Path $Workspace 'payload/MirrorPulse.Core.dll'),'changed')}
        if($Boundary -ceq 'changedRetainedInput'){[IO.File]::AppendAllText((Join-Path $originals 'inputs/MirrorPulse.Core.dll'),'changed')}
        if($Boundary -ceq 'changedCollector'){[IO.File]::AppendAllText((Join-Path $Workspace 'eng/native-test-originals.ps1'),"`n#changed")}
        if($Boundary -cin @('changedPreparation','replacedInputBinding')){
          $path=Join-Path $originals 'prepared.json';$prepared=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
          if($Boundary -ceq 'changedPreparation'){$prepared.sourceSha='f'*40}else{$prepared.inputs[0].path=Join-Path $Workspace 'payload/MirrorPulse.Core.dll'}
          $prepared|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $path -Encoding utf8
        }
        if($Boundary -ceq 'bothFailures'){[IO.File]::AppendAllText((Join-Path $Workspace 'payload/MirrorPulse.Core.dll'),'changed')}
        if($Boundary -cin @('actionFailure','bothFailures')){throw [InvalidOperationException]::new('ORIGINAL_NATIVE_FAILURE')}
        }
        if($wrapped){
          Invoke-NativeTestWithOriginals -Workspace $Workspace -SourceSha $SourceSha -Suite $suite -TestAssembly $assembly -ResultsDirectory $results -OutputDirectory $originals -ExpectedTestAssemblySha256 $expectedHash -Action $action
        }else{& $action;Complete-NativeTestOriginals -Workspace $Workspace -SourceSha $SourceSha -OutputDirectory $originals}
        if($ordinary){
          if($Boundary -ceq 'bundleMarker'){[IO.File]::WriteAllText((Join-Path $Workspace '.mp-namespace-ci'),'changed')}
          if($Boundary -ceq 'bundleMissingCollected'){Remove-Item -LiteralPath (Join-Path $originals 'collected.json')}
          if($Boundary -ceq 'bundleChangedInput'){[IO.File]::AppendAllText((Join-Path $originals 'inputs/MirrorPulse.Core.dll'),'changed')}
          if($Boundary -ceq 'bundleChangedCollector'){[IO.File]::AppendAllText((Join-Path $originals 'policy/native-test-originals.ps1'),'changed')}
          if($Boundary -ceq 'bundleMissingTrx'){Remove-Item -LiteralPath (Join-Path $results $trxName)}
          if($Boundary -ceq 'bundlePrivateConfig'){
            [IO.File]::WriteAllText((Join-Path $Workspace 'configuration.json'),'PRIVATE_FIXTURE')
            [IO.File]::WriteAllText((Join-Path $originals 'private.key'),'PRIVATE_FIXTURE')
          }
          . (Join-Path $Workspace 'eng/native-test-original-bundle.ps1')
          $parent=[IO.Path]::GetDirectoryName($Workspace)
          Copy-OrdinaryUserNativeOriginals -Workspace $Workspace -Identifier ([IO.Path]::GetFileName($Workspace)) -SourceSha $SourceSha -ExpectedTestAssemblySha256 $expectedHash -ExpectedCollectorSha256 $collectorHash -DestinationWorkspace $parent -DestinationDirectory (Join-Path $parent 'retained') -RequireCollection $true
        }
        """;
}
