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

function Read-DeclaredCfSharpVersion {
    [xml]$props = Get-Content -LiteralPath (Join-Path $workspace 'Directory.Packages.props') -Raw
    $versions = @(foreach ($package in 'CfSharp', 'CfSharp.Storage.Sqlite') {
        $pin = @($props.Project.ItemGroup.PackageVersion | Where-Object Include -CEQ $package)
        if ($pin.Count -ne 1 -or [string]$pin[0].Version -cnotmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
            throw "The installed comparison requires one exact central version for $package."
        }
        [string]$pin[0].Version
    })
    if ($versions[0] -cne $versions[1]) {
        throw 'The installed comparison requires the same exact CfSharp and SQLite package version.'
    }
    return $versions[0]
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
$lockedVersion = Read-DeclaredCfSharpVersion
$referencePatchSha256 = $null
if ($Profile -ceq 'preview13-reference') {
    if (-not $ValidateOnly -and ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted')) {
        throw 'Only a disposable GitHub-hosted checkout may apply the reference inputs.'
    }
    if (-not $ValidateOnly) {
        & git -C $workspace cat-file -e "$adoption^" 2>$null
        if ($LASTEXITCODE -ne 0) {
            & git -C $workspace fetch --no-tags --depth=2 origin $adoption
            if ($LASTEXITCODE -ne 0) { throw 'Could not fetch the pinned CfSharp adoption commit and its parent.' }
        }
    }
    $patch = [IO.Path]::GetTempFileName()
    try {
        $reference = "$adoption^"
        $referenceChanges = @(git -C $workspace diff --name-only --no-renames $reference $source -- $files)
        if ($LASTEXITCODE -ne 0) { throw 'Could not compare the exact reference and selected package inputs.' }
        if ($referenceChanges.Count -eq 0) {
            throw 'The exact preview.13 reference inputs did not differ from the selected package inputs.'
        }
        & git -C $workspace diff "--output=$patch" --binary --no-renames $reference $source -- $files
        if ($LASTEXITCODE -ne 0) { throw 'Could not recover the original CfSharp adoption inputs.' }
        $referencePatchSha256 = (Get-FileHash -LiteralPath $patch).Hash
        & git -C $workspace apply --reverse --check --index -- $patch
        if ($LASTEXITCODE -ne 0) { throw 'The exact package reference overlay no longer applies.' }
        if (-not $ValidateOnly) {
            & git -C $workspace apply --reverse --index -- $patch
            if ($LASTEXITCODE -ne 0) { throw 'Could not apply the checked reference input overlay.' }
            $changed = @(git -C $workspace diff --cached --name-only)
            if ($LASTEXITCODE -ne 0 -or $changed.Count -ne $referenceChanges.Count -or
                @(Compare-Object $referenceChanges $changed -CaseSensitive).Count -ne 0) {
                throw 'The reference overlay changed files outside its exact package inputs and collector pin.'
            }
        }
    } finally {
        Remove-Item -LiteralPath $patch -ErrorAction Stop
    }
}

$expectedVersion = if ($Profile -ceq 'preview13-reference' -and -not $ValidateOnly) { '0.1.0-preview.13' } else { $lockedVersion }
if ((Read-DeclaredCfSharpVersion) -cne $expectedVersion) {
    throw "The installed comparison expected $expectedVersion in the executing package inputs."
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
