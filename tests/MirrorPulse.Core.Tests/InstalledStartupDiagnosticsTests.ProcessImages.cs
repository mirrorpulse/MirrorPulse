namespace MirrorPulse.Core.Tests;

public sealed partial class InstalledStartupDiagnosticsTests
{
    [TestMethod]
    [DoNotParallelize]
    [DataRow("running")]
    [DataRow("same-bytes-other-path")]
    [DataRow("hash")]
    [DataRow("length")]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("foreign-record")]
    [DataRow("linked-root")]
    [DataRow("foreign-package")]
    [DataRow("app-without-package")]
    [DataRow("app-wrong-image")]
    [DataRow("source")]
    [DataRow("escape")]
    [DataRow("exited")]
    public async Task ExecutingImageProofReadsOnlyItsActualKnownProcessAndExactOriginalInput(string scenario)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-process-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . (Join-Path (Split-Path $Policy) 'installed-runtime-originals.ps1')
                . (Join-Path (Split-Path $Policy) 'installed-process-images.ps1')
                Initialize-InstalledProcessImageReader
                $process=[Diagnostics.Process]::GetCurrentProcess();$child=$null;$link=$null
                try {
                    $executable=$process.MainModule.FileName;$name=[IO.Path]::GetFileName($executable)
                    $length=(Get-Item -LiteralPath $executable).Length;$hash=(Get-FileHash -LiteralPath $executable).Hash
                    $prepared=[pscustomobject]@{schemaVersion=1;sourceSha=('a'*40);runtime='win-x64';installLocation=[IO.Path]::GetDirectoryName($executable)
                        inputs=@([pscustomobject]@{relativePath=$name;archivePath=('inputs/'+$name);path=$executable;length=$length;sha256=$hash})}
                    $role='Fixture';$relative=$name;$package=''
                    switch -Exact ($Scenario) {
                        'same-bytes-other-path' {
                            $prepared.installLocation=Join-Path $Directory 'selected';New-Item -ItemType Directory -Path $prepared.installLocation|Out-Null
                            $copy=Join-Path $prepared.installLocation $name;Copy-Item -LiteralPath $executable -Destination $copy
                            $prepared.inputs[0].path=$copy
                        }
                        'hash' {$prepared.inputs[0].sha256='B'*64}
                        'length' {$prepared.inputs[0].length++}
                        'missing' {$prepared.inputs=@()}
                        'duplicate' {$prepared.inputs=@($prepared.inputs[0],$prepared.inputs[0])}
                        'foreign-record' {$prepared.inputs[0].path=Join-Path $Directory $name}
                        'linked-root' {
                            $link=Join-Path $Directory 'linked';New-Item -ItemType Junction -Path $link -Target $prepared.installLocation|Out-Null
                            $prepared.installLocation=$link;$prepared.inputs[0].path=Join-Path $link $name
                        }
                        'foreign-package' {$package='Not.The.Running.Package_1.0.0.0_x64__fixture'}
                        'app-without-package' {$role='App';$relative='MirrorPulse.App.exe'}
                        'app-wrong-image' {$role='App';$package='Not.The.Running.Package_1.0.0.0_x64__fixture'}
                        'source' {$prepared.sourceSha='not-an-original-source'}
                        'escape' {$relative='../'+$name}
                        'exited' {
                            $start=[Diagnostics.ProcessStartInfo]::new($executable);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
                            $start.ArgumentList.Add('-NoProfile');$start.ArgumentList.Add('-Command');$start.ArgumentList.Add('exit 0')
                            $child=[Diagnostics.Process]::Start($start);if(-not $child.WaitForExit(10000)){throw 'The owned ordinary fixture child did not exit.'}
                            $process=$child
                        }
                    }
                    $failure=$null;$proof=$null
                    try {$proof=Get-InstalledProcessImageBinding -Process $process -Prepared $prepared -Role $role -RelativePath $relative `
                        -ExpectedPackageFullName $package -PreparedSha256 ('C'*64)}catch{$failure=$_.Exception.Message}
                    if($Scenario -ceq 'running') {
                        if($null -ne $failure){throw $failure}
                        if($proof.role -cne 'Fixture' -or $proof.processId -ne $PID -or $proof.imageSha256 -cne $hash -or
                            $proof.relativePath -cne $name -or -not $proof.observedWhileRunning -or -not $proof.nativeImagePathMatched -or
                            -not $proof.sameSession -or $proof.packageIdentityMatched -or $proof.actualManagedModuleLoadPathsEstablished) {throw 'Actual ordinary-process kernel image correlation was lost or promoted to installed proof.'}
                        $json=$proof|ConvertTo-Json -Compress
                        if($json.Contains($prepared.installLocation) -or $json.Contains('commandLine') -or $json.Contains('moduleName')) {throw 'Process paths or unrelated process material escaped the bounded evidence.'}
                    } else {
                        $expected=@{'same-bytes-other-path'='actual executing process image';'hash'='original selected MSIX input';'length'='original selected MSIX input';
                            'missing'='one complete original';'duplicate'='one complete original';'foreign-record'='installed root';'linked-root'='reparse point';
                            'foreign-package'='no matching installed package identity';'app-without-package'='exact App, CLI or Host';'app-wrong-image'='exact App, CLI or Host';
                            'source'='Invalid original installed';'escape'='Invalid original installed';'exited'='exited before image observation'}
                        if($null -eq $failure -or -not $failure.Contains($expected[$Scenario])) {throw ('The actual process boundary did not refuse: '+$Scenario+'; '+$failure)}
                    }
                } finally {
                    if($child){if(-not $child.HasExited){$child.Kill($true);[void]$child.WaitForExit(10000)};$child.Dispose()}
                    if($link -and (Test-Path -LiteralPath $link)){Remove-Item -LiteralPath $link -Force}
                    if($null -eq $child){$process.Dispose()}
                }
                """);
            await RunProbeAsync(script, directory, scenario);
        }
        finally { Cleanup(prefix, directory); }
    }
}
