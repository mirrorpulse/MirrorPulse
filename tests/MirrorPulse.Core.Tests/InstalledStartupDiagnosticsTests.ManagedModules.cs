namespace MirrorPulse.Core.Tests;

public sealed partial class InstalledStartupDiagnosticsTests
{
    [TestMethod]
    [DoNotParallelize]
    [DataRow("running")]
    [DataRow("hash")]
    [DataRow("length")]
    [DataRow("identity")]
    [DataRow("duplicate")]
    [DataRow("missing")]
    [DataRow("foreign-root")]
    [DataRow("non-managed")]
    [DataRow("exited")]
    [DataRow("wrong-role")]
    public async Task ManagedModuleProofObservesOnlyItsKnownRunningOriginalAndRejectsChangedInputs(string scenario)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-managed-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                $eng=Split-Path $Policy -Parent
                . (Join-Path $eng 'installed-runtime-originals.ps1')
                . (Join-Path $eng 'installed-process-images.ps1')
                . (Join-Path $eng 'installed-managed-modules.ps1')
                Initialize-InstalledProcessImageReader
                $process=[Diagnostics.Process]::GetCurrentProcess()
                $child=$null
                try{
                    $image=$process.MainModule.FileName;$root=[IO.Path]::GetDirectoryName($image);$name=[IO.Path]::GetFileName($image)
                    $assembly=[Management.Automation.PowerShell].Assembly
                    $module=$assembly.Location;$moduleName=[IO.Path]::GetFileName($module)
                    $input=[pscustomobject]@{relativePath=$moduleName;archivePath='inputs/'+$moduleName;path=$module;
                        length=(Get-Item -LiteralPath $module).Length;sha256=(Get-FileHash -LiteralPath $module).Hash;
                        metadata=[pscustomobject]@{managed=$true;identity=$assembly.GetName().FullName}}
                    $imageInput=[pscustomobject]@{relativePath=$name;archivePath='inputs/'+$name;path=$image;
                        length=(Get-Item -LiteralPath $image).Length;sha256=(Get-FileHash -LiteralPath $image).Hash}
                    $prepared=[pscustomobject]@{schemaVersion=1;sourceSha=('a'*40);runtime='win-arm64';installLocation=$root;inputs=@($imageInput,$input)}
                    # Read only this known process's actual package identity. Local
                    # packaged PowerShell and unpackaged clean CI remain real owners.
                    [uint32]$length=0;$package=''
                    $code=[MirrorPulse.InstalledProcessImageNative]::GetPackageFullName($process.SafeHandle,[ref]$length,$null)
                    if($code -eq 122){
                        $value=[Text.StringBuilder]::new([int]$length)
                        if([MirrorPulse.InstalledProcessImageNative]::GetPackageFullName($process.SafeHandle,[ref]$length,$value) -ne 0){throw 'Own package query failed.'}
                        $package=$value.ToString()
                    }elseif($code -ne 15700){throw 'Own package identity is unavailable.'}
                    $parameters=@{Process=$process;Prepared=$prepared;Role='Fixture';RelativePath=$name;ExpectedPackageFullName=$package;
                        PreparedSha256=('C'*64);DeadlineMilliseconds=100}
                    switch($Scenario){
                        'hash' {$input.sha256='0'*64}
                        'length' {$input.length++}
                        'identity' {$input.metadata.identity='Foreign, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null'}
                        'duplicate' {$prepared.inputs+= $input}
                        'missing' {$prepared.inputs=@($imageInput)}
                        'foreign-root' {$input.path=Join-Path $Directory $moduleName}
                        'non-managed' {$input.metadata.managed=$false}
                        'exited' {
                            $start=[Diagnostics.ProcessStartInfo]::new($image);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
                            $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
                            $start.ArgumentList.Add('-NoProfile');$start.ArgumentList.Add('-Command');$start.ArgumentList.Add('exit 0')
                            $child=[Diagnostics.Process]::Start($start)
                            if(-not $child.WaitForExit(10000)){throw 'The owned exit fixture did not complete.'}
                            $parameters.Process=$child
                        }
                        'wrong-role' {$parameters.Role='Host'}
                    }
                    $failure=$null;$proof=$null
                    try{$proof=Get-InstalledManagedModuleBinding @parameters}catch{$failure=$_.Exception.Message}
                    if($Scenario -ceq 'running'){
                        if($null -ne $failure){throw $failure}
                        if($proof.role -cne 'Fixture' -or $proof.processId -ne $PID -or $proof.mappedModules.Count -ne 1 -or
                           $proof.mappedModules[0].relativePath -cne $moduleName -or $proof.mappedModules[0].sha256 -cne $input.sha256 -or
                           $proof.actualManagedModuleLoadPathsEstablished -ne $true -or $proof.clrLoadContextSemanticsClaimed -ne $false){throw 'Actual known managed module mapping was lost or promoted to CLR execution proof.'}
                        $json=$proof|ConvertTo-Json -Depth 6 -Compress
                        if($json.Contains($root) -or $json.Contains('commandLine') -or $json.Contains('moduleName')){throw 'Unrelated paths/process fields escaped fixed-name evidence.'}
                        Assert-InstalledManagedModuleObservation $proof
                        $output=Join-Path $Directory 'module-originals';New-Item -ItemType Directory -Path $output|Out-Null
                        [IO.File]::WriteAllText((Join-Path $output 'prepared.json'),'{}')
                        [IO.File]::WriteAllText((Join-Path $output 'collected.json'),'{}')
                        $complete=@{Workspace=$Directory;OutputDirectory=$output;Prepared=$prepared;PreparedSha256=(Get-FileHash (Join-Path $output 'prepared.json')).Hash;
                            CollectedSha256=(Get-FileHash (Join-Path $output 'collected.json')).Hash;ExpectedPackageFullName='Fixture.Not.An.Installed.Product';VerifyShell=$false}
                        $refused=$null
                        try{Complete-InstalledManagedModuleBindings @complete -Bindings @($proof)|Out-Null}catch{$refused=$_.Exception.Message}
                        if($null -eq $refused -or -not $refused.Contains('distinct original installed role')){throw 'A real own-process fixture cannot become installed product evidence.'}
                        $cli=$proof|ConvertTo-Json -Depth 8|ConvertFrom-Json -DateKind String
                        $hostObservation=$proof|ConvertTo-Json -Depth 8|ConvertFrom-Json -DateKind String
                        $cli.role='Cli';$hostObservation.role='Host';$hostObservation.processId++
                        $refused=$null
                        try{Complete-InstalledManagedModuleBindings @complete -Bindings @($cli,$hostObservation)|Out-Null}catch{$refused=$_.Exception.Message}
                        if($null -eq $refused -or -not $refused.Contains('unchanged actual observation')){throw 'Caller-relabelled observations cannot become installed App/CLI/Host mappings.'}
                        $proof.mappedModules[0].sha256='0'*64
                        $changed=$null
                        try{Assert-InstalledManagedModuleObservation $proof}catch{$changed=$_.Exception.Message}
                        if($null -eq $changed -or -not $changed.Contains('unchanged actual observation')){throw 'A changed caller receipt cannot become the original observation.'}
                    }else{
                        $expected=@{hash='original installed bytes';length='original installed bytes';identity='managed PE identity';
                            duplicate='one complete original';missing='required managed input';'foreign-root'='declared installed root';
                            'non-managed'='one complete original';exited='exited before image observation';'wrong-role'='exact App, CLI or Host'}
                        if($null -eq $failure -or -not $failure.Contains($expected[$Scenario])){throw ('Wrong managed module refusal: '+$Scenario+'; '+$failure)}
                    }
                }finally{
                    if($null -ne $child){if(-not $child.HasExited){$child.Kill($true);[void]$child.WaitForExit(10000)};$child.Dispose()}
                    $process.Dispose()
                }
                """);
            await RunProbeAsync(script, directory, scenario);
        }
        finally { Cleanup(prefix, directory); }
    }
}
