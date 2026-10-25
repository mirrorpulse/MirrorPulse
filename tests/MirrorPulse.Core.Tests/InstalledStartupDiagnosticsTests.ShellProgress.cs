using System.Text.Json;
using MirrorPulse.App;

namespace MirrorPulse.Core.Tests;

public sealed partial class InstalledStartupDiagnosticsTests
{
    [TestMethod]
    public async Task EveryProducedShellStageRoundTripsThroughTheClosedFailureObserver()
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-diagnostics-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string[] stages = Enum.GetNames<MirrorPulsePackagedShellProbeStage>();
            foreach (MirrorPulsePackagedShellProbeStage stage in Enum.GetValues<MirrorPulsePackagedShellProbeStage>())
            {
                string resultPath = Path.Combine(directory, stage.ToString());
                MirrorPulsePackagedShellProbeProgress.Record(resultPath, stage);
                Assert.AreEqual("1:" + stage, await File.ReadAllTextAsync(resultPath + ".progress"));
                Assert.IsLessThanOrEqualTo(128L, new FileInfo(resultPath + ".progress").Length);
                Assert.IsFalse(File.Exists(resultPath), "Progress must not fabricate the probe's success result.");
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "stages.json"), JsonSerializer.Serialize(stages));
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . (Join-Path (Split-Path -Parent $Policy) 'installed-shell-observations.ps1')
                foreach ($stage in @(Get-Content (Join-Path $Directory 'stages.json') -Raw|ConvertFrom-Json)) {
                    $result=Get-InstalledShellProbeObservation -Process $null -ResultPath (Join-Path $Directory $stage)
                    if ($result.stageState -cne 'Observed' -or $result.stage -cne $stage -or
                        $result.resultState -cne 'Missing' -or $result.processState -cne 'Unavailable' -or
                        $null -ne $result.failureHResult -or $null -ne $result.exitCode) {throw 'The actual fixed producer stage or independent result observation changed.'}
                }
                """
            );
            await RunProbeAsync(script, directory, "all-stages");
        }
        finally { Cleanup(prefix, directory); }
    }

    [TestMethod]
    public void FailedOrInvalidShellStageWriteCannotFabricateThePrimaryProbeResult()
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-diagnostics-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string result = Path.Combine(directory, "result");
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => MirrorPulsePackagedShellProbeProgress.Record(result,
                (MirrorPulsePackagedShellProbeStage)(-1)));
            Assert.ThrowsExactly<ArgumentException>(() => MirrorPulsePackagedShellProbeProgress.Record(" ",
                MirrorPulsePackagedShellProbeStage.ProbeEntered));
            Assert.IsFalse(File.Exists(result + ".progress"));
            Directory.CreateDirectory(result + ".progress");
            MirrorPulsePackagedShellProbeProgress.Record(result, MirrorPulsePackagedShellProbeStage.WritingSuccess);
            Assert.IsTrue(Directory.Exists(result + ".progress"));
            MirrorPulsePackagedShellProbeProgress.Record(Path.Combine(directory, "missing", "result"),
                MirrorPulsePackagedShellProbeStage.WritingFailure);
            Assert.IsFalse(Directory.Exists(Path.Combine(directory, "missing")));
            Assert.IsFalse(File.Exists(result));
        }
        finally { Cleanup(prefix, directory); }
    }

    [TestMethod]
    [DataRow("missing")]
    [DataRow("private")]
    [DataRow("unknown")]
    [DataRow("version")]
    [DataRow("case")]
    [DataRow("oversized")]
    [DataRow("directory")]
    [DataRow("linked")]
    public async Task ShellStageObserverRejectsUnboundedUnrecognizedOrLinkedProgress(string scenario)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-diagnostics-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . (Join-Path (Split-Path -Parent $Policy) 'installed-shell-observations.ps1')
                $resultPath=Join-Path $Directory 'result';$progress=$resultPath+'.progress';$linkCreated=$false
                try {
                    switch ($Scenario) {
                        'private' {[IO.File]::WriteAllText($progress,'PRIVATE_TEST_SENTINEL')}
                        'unknown' {[IO.File]::WriteAllText($progress,'1:PRIVATE_TEST_SENTINEL')}
                        'version' {[IO.File]::WriteAllText($progress,'2:AppLaunched')}
                        'case' {[IO.File]::WriteAllText($progress,'1:applaunched')}
                        'oversized' {[IO.File]::WriteAllBytes($progress,[byte[]]::new(129))}
                        'directory' {New-Item -ItemType Directory $progress|Out-Null}
                        'linked' {
                            $target=Join-Path $Directory 'target';New-Item -ItemType Directory $target|Out-Null
                            New-Item -ItemType Junction -Path $progress -Target $target|Out-Null;$linkCreated=$true
                        }
                    }
                    $result=Get-InstalledShellProbeObservation -Process $null -ResultPath $resultPath
                    $json=$result|ConvertTo-Json -Compress
                    $expected=@{missing='Missing';private='Invalid';unknown='Invalid';version='Invalid';case='Invalid';oversized='TooLarge';directory='Invalid';linked='Linked'}
                    if ($result.stageState -cne $expected[$Scenario] -or $null -ne $result.stage -or
                        $result.resultState -cne 'Missing' -or $null -ne $result.failureHResult -or $null -ne $result.exitCode -or
                        $json.Contains('PRIVATE_TEST_SENTINEL') -or $json.Contains($Directory)) {throw 'Unrecognized progress escaped or changed the primary result.'}
                } finally {if ($linkCreated) {Remove-Item -LiteralPath $progress -Force}}
                """
            );
            await RunProbeAsync(script, directory, scenario);
        }
        finally { Cleanup(prefix, directory); }
    }
}
