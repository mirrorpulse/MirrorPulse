# Read-only evidence for fixed, known managed PE modules in an already identified
# installed App/CLI/Host process. No process discovery, memory reading or module loading.
$script:InstalledManagedModulePolicyPath=$PSCommandPath
$script:InstalledManagedModulePolicySha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash
$script:InstalledManagedModuleCaptures=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)

function Get-InstalledManagedModuleProfile {
    param([Parameter(Mandatory)][ValidateSet('App','Cli','Host','Fixture')][string]$Role)
    $allowed=@('MirrorPulse.App.dll','mp.dll','MirrorPulse.Host.dll','MirrorPulse.Control.dll','MirrorPulse.Core.dll',
        'MirrorPulse.CloudFiles.CfSharp.dll','MirrorPulse.Adapter.Sdk.dll','CfSharp.dll','CfSharp.Native.dll','CfSharp.Storage.Sqlite.dll')
    $required=switch($Role){
        'App' {@('MirrorPulse.App.dll')}
        'Cli' {@('mp.dll','MirrorPulse.Control.dll','MirrorPulse.Core.dll')}
        'Host' {@('MirrorPulse.Host.dll','MirrorPulse.Control.dll','MirrorPulse.Core.dll','MirrorPulse.CloudFiles.CfSharp.dll',
            'CfSharp.dll','CfSharp.Native.dll','CfSharp.Storage.Sqlite.dll')}
        'Fixture' {$allowed=@('System.Management.Automation.dll');@('System.Management.Automation.dll')}
    }
    @{allowed=$allowed;required=@($required);prefix=if($Role -ceq 'Host'){'host/'}else{''}}
}

function Get-InstalledManagedBindingDigest($Binding) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(
        ($Binding|ConvertTo-Json -Depth 8 -Compress))))
}

function Assert-InstalledManagedModuleObservation($Binding) {
    if(-not $script:InstalledManagedModuleCaptures.ContainsKey([string]$Binding.observationId) -or
       $script:InstalledManagedModuleCaptures[[string]$Binding.observationId] -cne (Get-InstalledManagedBindingDigest $Binding)){
        throw 'A managed mapping is not the unchanged actual observation made by this verifier.'
    }
}

function Get-InstalledManagedModuleBinding {
    param([Parameter(Mandatory)][Diagnostics.Process]$Process,[Parameter(Mandatory)]$Prepared,
        [Parameter(Mandatory)][ValidateSet('App','Cli','Host','Fixture')][string]$Role,
        [Parameter(Mandatory)][string]$RelativePath,
        [AllowEmptyString()][Parameter(Mandatory)][string]$ExpectedPackageFullName,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$PreparedSha256,
        [ValidateRange(1,15000)][int]$DeadlineMilliseconds=8000)
    $parameters=@{Process=$Process;Prepared=$Prepared;Role=$Role;RelativePath=$RelativePath;
        ExpectedPackageFullName=$ExpectedPackageFullName;PreparedSha256=$PreparedSha256}
    $before=Get-InstalledProcessImageBinding @parameters
    $profile=Get-InstalledManagedModuleProfile -Role $Role
    $allowed=$profile.allowed;$required=$profile.required;$prefix=$profile.prefix
    $selected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($input in $Prepared.inputs){
        if($null -eq $input.relativePath -or $input.relativePath -cnotmatch '^(host/)?[A-Za-z0-9.-]+\.dll$'){continue}
        $name=[IO.Path]::GetFileName($input.relativePath)
        if($name -cnotin $allowed -or $input.relativePath -cne ($prefix+$name)){continue}
        if($input.metadata.managed -isnot [bool] -or -not $input.metadata.managed -or
            $input.archivePath -cne ('inputs/'+$input.relativePath) -or $input.sha256 -cnotmatch '^[A-F0-9]{64}$' -or
            $input.length -le 0 -or $input.length -gt 128MB -or -not $selected.TryAdd($name,$input)){
            throw 'A selected managed module must be one complete original installed input.'
        }
        $expected=Get-InstalledOriginalPath (Join-Path $Prepared.installLocation $input.relativePath) $Prepared.installLocation
        if($input.path -cne $expected){throw 'A selected managed module changed its declared installed root.'}
    }
    foreach($name in $required){if(-not $selected.ContainsKey($name)){throw 'The original role lost a required managed input.'}}
    $watch=[Diagnostics.Stopwatch]::StartNew()
    while($true){
        $Process.Refresh()
        if($Process.HasExited -or $Process.Id -ne $before.processId -or
           $Process.StartTime.ToUniversalTime() -ne [DateTimeOffset]::Parse($before.startedUtc).UtcDateTime){
            throw 'The original process exited or changed before managed module observation.'
        }
        $mapped=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        $count=0
        foreach($module in $Process.Modules){
            if(++$count -gt 1024){throw 'The original process module inventory exceeds its observation bound.'}
            # Read a path only after a fixed allowlisted basename matched. No other
            # module name/path is returned or retained in diagnostic evidence.
            $name=$module.ModuleName
            if(-not $selected.ContainsKey($name)){continue}
            if($mapped.ContainsKey($name)){throw 'The original process maps an ambiguous managed module name.'}
            $input=$selected[$name]
            $actual=Get-InstalledOriginalPath $module.FileName $Prepared.installLocation
            $expected=Get-InstalledOriginalPath $input.path $Prepared.installLocation
            if(-not [string]::Equals($actual,$expected,[StringComparison]::OrdinalIgnoreCase)){
                throw 'The actual mapped managed module differs from its original installed path.'
            }
            $mapped.Add($name,[pscustomobject]@{original=$input;actual=$actual})
        }
        if(@($required|Where-Object {-not $mapped.ContainsKey($_)}).Count -eq 0){break}
        if($watch.ElapsedMilliseconds -ge $DeadlineMilliseconds){throw 'The running original did not map every required managed module before the observation deadline.'}
        Start-Sleep -Milliseconds 25
    }
    # Wait only for actual module mappings. Hash the complete observed set once,
    # rather than rereading DLL bytes on every startup poll.
    $moduleProofs=@(foreach($match in $mapped.Values){
        $input=$match.original;$actual=$match.actual
        if((Get-Item -LiteralPath $actual).Length -ne $input.length -or (Get-FileHash -LiteralPath $actual).Hash -cne $input.sha256){
            throw 'The actual mapped managed module differs from its original installed bytes.'
        }
        $identity=[Reflection.AssemblyName]::GetAssemblyName($actual).FullName
        if($identity -cne $input.metadata.identity){throw 'The mapped managed PE identity differs from its original input.'}
        [pscustomobject][ordered]@{relativePath=$input.relativePath;sha256=$input.sha256;length=$input.length;
            identity=$identity;actualMappedPathMatched=$true;originalInstalledBytesMatched=$true}
    })
    $after=Get-InstalledProcessImageBinding @parameters
    if($before.processId -ne $after.processId -or $before.startedUtc -cne $after.startedUtc -or
       $before.packageIdentitySha256 -cne $after.packageIdentitySha256){throw 'The native process owner changed during managed module observation.'}
    $binding=[pscustomobject][ordered]@{observationId=[guid]::NewGuid().ToString('D');role=$Role;processId=$after.processId;startedUtc=$after.startedUtc;observedUtc=$after.observedUtc;
        preparedSha256=$PreparedSha256;sourceSha=$Prepared.sourceSha;imageSha256=$after.imageSha256;
        sameSession=$true;observedWhileRunning=$true;nativeImagePathMatched=$true;packageIdentityMatched=$after.packageIdentityMatched;
        packageIdentitySha256=$after.packageIdentitySha256;requiredModules=@($required|ForEach-Object {$prefix+$_});
        mappedModules=@($moduleProofs|Sort-Object relativePath);actualManagedModuleLoadPathsEstablished=$true;
        clrLoadContextSemanticsClaimed=$false}
    if($script:InstalledManagedModuleCaptures.Count -ge 16){throw 'Managed module observations exceed the original verifier bound.'}
    $script:InstalledManagedModuleCaptures.Add($binding.observationId,(Get-InstalledManagedBindingDigest $binding))
    return $binding
}

function Complete-InstalledManagedModuleBindings {
    param([Parameter(Mandatory)][string]$Workspace,[Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)]$Prepared,[Parameter(Mandatory)][object[]]$Bindings,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$PreparedSha256,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$CollectedSha256,
        [Parameter(Mandatory)][string]$ExpectedPackageFullName,[Parameter(Mandatory)][bool]$VerifyShell)
    $output=Get-InstalledOriginalPath $OutputDirectory $Workspace;$path=Join-Path $output 'process-managed-modules.json'
    if((Test-Path -LiteralPath $path) -or (Get-FileHash -LiteralPath (Join-Path $output 'prepared.json')).Hash -cne $PreparedSha256 -or
       (Get-FileHash -LiteralPath (Join-Path $output 'collected.json')).Hash -cne $CollectedSha256){throw 'Require the actual original installed input receipts and fresh module output.'}
    $roles=if($VerifyShell){@('App','Cli','Host')}else{@('Cli','Host')}
    if($Bindings.Count -ne $roles.Count -or @(Compare-Object @($roles|Sort-Object) @($Bindings.role|Sort-Object) -CaseSensitive).Count -or
       @($Bindings.processId|Sort-Object -Unique).Count -ne $roles.Count){throw 'Require each distinct original installed role without a fixture or duplicate.'}
    $packageHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($ExpectedPackageFullName)))
    foreach($binding in $Bindings){
        Assert-InstalledManagedModuleObservation $binding
        $profile=Get-InstalledManagedModuleProfile -Role $binding.role
        $required=@($profile.required|ForEach-Object {$profile.prefix+$_})
        if(@(Compare-Object $required @($binding.requiredModules) -CaseSensitive).Count){throw 'The original role changed its required managed modules.'}
        if($binding.sourceSha -cne $Prepared.sourceSha -or $binding.preparedSha256 -cne $PreparedSha256 -or
           -not $binding.observedWhileRunning -or -not $binding.sameSession -or -not $binding.nativeImagePathMatched -or
           -not $binding.packageIdentityMatched -or $binding.packageIdentitySha256 -cne $packageHash -or
           $binding.actualManagedModuleLoadPathsEstablished -ne $true -or $binding.clrLoadContextSemanticsClaimed -ne $false -or
           $binding.mappedModules.Count -lt 1 -or $binding.mappedModules.Count -gt 10){throw 'An actual managed mapping lost its original installed process owner.'}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach($module in $binding.mappedModules){
            $original=@($Prepared.inputs|Where-Object relativePath -ceq $module.relativePath)
            if($original.Count -ne 1 -or -not $seen.Add($module.relativePath) -or $module.sha256 -cne $original[0].sha256 -or
                [IO.Path]::GetFileName($module.relativePath) -cnotin $profile.allowed -or
                $module.relativePath -cne ($profile.prefix+[IO.Path]::GetFileName($module.relativePath)) -or
                $module.length -ne $original[0].length -or $module.identity -cne $original[0].metadata.identity -or
                $module.actualMappedPathMatched -ne $true -or $module.originalInstalledBytesMatched -ne $true){throw 'A managed mapping changed its original installed input.'}
        }
        if(@($binding.requiredModules|Where-Object {-not $seen.Contains($_)}).Count){throw 'An actual required managed module observation is missing.'}
    }
    if((Get-FileHash -LiteralPath $script:InstalledManagedModulePolicyPath).Hash -cne $script:InstalledManagedModulePolicySha256){throw 'The original managed module observer changed during execution.'}
    $collector=Copy-InstalledRuntimeOriginal $script:InstalledManagedModulePolicyPath $Workspace `
        (Join-Path $output 'policy/installed-managed-modules.ps1') $Workspace $output
    [ordered]@{schemaVersion=1;sourceSha=$Prepared.sourceSha;runtime=$Prepared.runtime;preparedSha256=$PreparedSha256;collectedSha256=$CollectedSha256;
        bindings=$Bindings;collector=$collector;actualManagedModuleLoadPathsEstablished=$true;clrLoadContextSemanticsClaimed=$false;
        fullP085Accepted=$false;fullS06Accepted=$false;completedUtc=[DateTimeOffset]::UtcNow.ToString('o')}|
        ConvertTo-Json -Depth 8|Set-Content -LiteralPath $path -Encoding utf8
    [pscustomobject]@{archivePath='process-managed-modules.json';sha256=(Get-FileHash -LiteralPath $path).Hash;processCount=$Bindings.Count}
}
