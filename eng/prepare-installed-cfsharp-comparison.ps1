[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('locked', 'preview13-reference')][string]$Profile,
    [Parameter(Mandatory)][string]$EvidencePath,
    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$adoption = '4e32535a41495b17ef589494fc8c85b9bc37fc97'
$source = (& git -C $workspace rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '^[0-9a-f]{40}$' -or
    ($env:GITHUB_SHA -and $source -cne $env:GITHUB_SHA)) {
    throw 'The comparison requires the exact selected product source.'
}
$files = @(
    'Directory.Packages.props'
    'eng/dependency-license-sources.json'
    'eng/installed-runtime-originals.ps1'
    'src/MirrorPulse.App/packages.lock.json'
    'src/MirrorPulse.CloudFiles.CfSharp/packages.lock.json'
    'src/MirrorPulse.Host/packages.lock.json'
    'tests/MirrorPulse.CfSharp.CrashProbe/packages.lock.json'
    'tests/MirrorPulse.CloudFiles.CfSharp.Tests/packages.lock.json'
    'tests/MirrorPulse.Core.Tests/packages.lock.json'
)

function Read-InputHashes {
    foreach ($file in $files) {
        [ordered]@{path=$file;sha256=(Get-FileHash -LiteralPath (Join-Path $workspace $file)).Hash}
    }
}

$trackedChanges = if ($ValidateOnly) {
    @(git -C $workspace status --porcelain --untracked-files=no -- $files)
} else {
    @(git -C $workspace status --porcelain --untracked-files=no)
}
if ($LASTEXITCODE -ne 0 -or $trackedChanges.Count -ne 0) {
    throw 'The comparison requires a clean tracked checkout.'
}
$inputHashes = @(Read-InputHashes)
$referencePatchSha256 = $null
if ($Profile -ceq 'preview13-reference') {
    if (-not $ValidateOnly -and ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted')) {
        throw 'Only a disposable GitHub-hosted checkout may apply the reference inputs.'
    }
    $patch = [IO.Path]::GetTempFileName()
    try {
        & git -C $workspace diff "--output=$patch" --binary "$adoption^" $adoption -- $files
        if ($LASTEXITCODE -ne 0) { throw 'Could not recover the original CfSharp adoption inputs.' }
        $referencePatchSha256 = (Get-FileHash -LiteralPath $patch).Hash
        & git -C $workspace apply --reverse --check --index -- $patch
        if ($LASTEXITCODE -ne 0) { throw 'The exact package reference overlay no longer applies.' }
        if (-not $ValidateOnly) {
            & git -C $workspace apply --reverse --index -- $patch
            if ($LASTEXITCODE -ne 0) { throw 'Could not apply the checked reference input overlay.' }
            $changed = @(git -C $workspace diff --cached --name-only)
            if ($LASTEXITCODE -ne 0 -or $changed.Count -ne $files.Count -or @(Compare-Object $files $changed -CaseSensitive).Count -ne 0) {
                throw 'The reference overlay changed files outside its exact package inputs and collector pin.'
            }
        }
    } finally {
        Remove-Item -LiteralPath $patch -ErrorAction Stop
    }
}

$expectedVersion = if ($Profile -ceq 'preview13-reference' -and -not $ValidateOnly) { '0.1.0-preview.13' } else { '0.1.0-preview.14' }
$props = [xml](Get-Content -LiteralPath (Join-Path $workspace 'Directory.Packages.props') -Raw)
foreach ($package in @('CfSharp', 'CfSharp.Storage.Sqlite')) {
    $pin = @($props.Project.ItemGroup.PackageVersion | Where-Object Include -CEQ $package)
    if ($pin.Count -ne 1 -or $pin[0].Version -cne $expectedVersion) {
        throw "The installed comparison expected $expectedVersion for $package."
    }
}
$output = [IO.Path]::GetFullPath($EvidencePath, $workspace)
New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($output)) -Force | Out-Null
[ordered]@{
    schemaVersion=1;sourceSha=$source;profile=$Profile;cfSharpVersion=$expectedVersion
    runnerArchitecture=$env:RUNNER_ARCH;runnerImageOs=$env:ImageOS;runnerImageVersion=$env:ImageVersion
    validationOnly=$ValidateOnly.IsPresent
    referenceAdoptionSha=$adoption;referencePatchSha256=$referencePatchSha256
    lockedInputs=$inputHashes;executingInputs=@(Read-InputHashes)
} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $output -Encoding utf8
