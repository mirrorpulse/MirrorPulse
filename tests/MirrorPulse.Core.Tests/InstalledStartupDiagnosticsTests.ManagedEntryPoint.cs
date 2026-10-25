using System.Text.Json;
using MirrorPulse.Worker.ProtocolFixture;

namespace MirrorPulse.Core.Tests;

public sealed partial class InstalledStartupDiagnosticsTests
{
    [TestMethod]
    [DataRow("managed")]
    [DataRow("library")]
    [DataRow("invalid")]
    [DataRow("directory")]
    [DataRow("linked")]
    public async Task InstalledProbeEntryInspectionReadsRealMetadataWithoutExecutingTheTarget(string scenario)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-diagnostics-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var fixture = typeof(ProtocolFixtureMarker).Assembly;
            var entry = fixture.EntryPoint!;
            await File.WriteAllTextAsync(Path.Combine(directory, "expected.json"), JsonSerializer.Serialize(new
            {
                assembly = fixture.Location,
                library = typeof(MirrorPulse.Core.Contracts.RootId).Assembly.Location,
                type = entry.DeclaringType!.FullName,
                method = entry.Name
            }));
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . (Join-Path (Split-Path -Parent $Policy) 'installed-shell-observations.ps1')
                $expected=Get-Content (Join-Path $Directory 'expected.json') -Raw|ConvertFrom-Json
                $inputPath=Join-Path $Directory 'target';$linked=$false
                try {
                    switch($Scenario) {
                        'managed' {Copy-Item -LiteralPath $expected.assembly -Destination $inputPath}
                        'library' {Copy-Item -LiteralPath $expected.library -Destination $inputPath}
                        'invalid' {[IO.File]::WriteAllText($inputPath,'PRIVATE_TEST_SENTINEL')}
                        'directory' {New-Item -ItemType Directory $inputPath|Out-Null}
                        'linked' {
                            $target=Join-Path $Directory 'foreign';New-Item -ItemType Directory $target|Out-Null
                            New-Item -ItemType Junction -Path $inputPath -Target $target|Out-Null;$linked=$true
                        }
                    }
                    $rejected=$false;$entry=$null
                    try {$entry=Get-InstalledManagedEntryPoint -AssemblyPath $inputPath} catch {$rejected=$true}
                    if($Scenario -ceq 'managed') {
                        if($rejected -or $entry.type -cne $expected.type -or $entry.method -cne $expected.method) {throw 'The real managed entry metadata was not retained.'}
                        $json=$entry|ConvertTo-Json -Compress
                        if($json.Contains($Directory) -or $json.Contains('PRIVATE_TEST_SENTINEL')) {throw 'The metadata result exposed private input.'}
                    } elseif(-not $rejected -or $null -ne $entry) {throw 'An invalid or absent method entry point was accepted.'}
                    if(Test-Path -LiteralPath (Join-Path $Directory 'result')) {throw 'Metadata inspection must not execute a probe.'}
                } finally {if($linked){Remove-Item -LiteralPath $inputPath -Force}}
                """);
            await RunProbeAsync(script, directory, scenario);
        }
        finally { Cleanup(prefix, directory); }
    }

    [TestMethod]
    [DataRow("empty")]
    [DataRow("invalid")]
    [DataRow("missing")]
    public async Task ShellObservationRetainsActualBoundedReadLengthsWithoutPrivateContent(string scenario)
    {
        await RunSharedShellOutputProbeAsync(scenario, """
            if($Scenario -cne 'missing') {
                Initialize-InstalledShellProbeOutput -ResultPath $resultPath
                if($Scenario -ceq 'invalid') {
                    [IO.File]::WriteAllText($resultPath,'PRIVATE_TEST_SENTINEL')
                    [IO.File]::WriteAllText($resultPath+'.progress','1:PRIVATE_TEST_SENTINEL')
                }
            }
            $observed=Get-InstalledShellProbeObservation -Process $null -ResultPath $resultPath
            if($Scenario -ceq 'missing') {
                if($null -ne $observed.resultBytesRead -or $null -ne $observed.progressBytesRead) {throw 'Missing files do not establish empty reads.'}
            } else {
                if($observed.resultBytesRead -ne (Get-Item -LiteralPath $resultPath).Length -or
                    $observed.progressBytesRead -ne (Get-Item -LiteralPath ($resultPath+'.progress')).Length -or
                    $observed.resultState -cne 'Invalid' -or $observed.stageState -cne 'Invalid') {throw 'Actual empty or invalid reads must retain their independent bounded lengths.'}
            }
            $json=$observed|ConvertTo-Json -Compress
            if($json.Contains('PRIVATE_TEST_SENTINEL') -or $json.Contains($Directory)) {throw 'Read lengths cannot expose arbitrary probe text or paths.'}
            """);
    }
}
