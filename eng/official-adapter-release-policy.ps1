function Get-OfficialStableAdapterReleaseIdentity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][string]$AdapterId
    )

    if ($Release.draft -isnot [bool] -or $Release.draft -or
        $Release.prerelease -isnot [bool] -or $Release.prerelease -or
        [string]::IsNullOrWhiteSpace([string]$Release.published_at)) {
        throw "The default aggregate requires a published stable release for '$AdapterId'."
    }
    $tag = [string]$Release.tag_name
    # Preserve previously published four-component identities without normalizing them.
    if ($tag -cnotmatch '\Av(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(\.(0|[1-9][0-9]*))?\z') {
        throw "The default aggregate refuses a preview or noncanonical release tag for '$AdapterId'."
    }
    $version = $tag.Substring(1)
    $parsed = $null
    if (-not [Version]::TryParse($version, [ref]$parsed)) {
        throw "The stable release version exceeds its supported numeric bounds for '$AdapterId'."
    }
    [pscustomobject]@{ tag = $tag; version = $version; channel = 'stable' }
}

function Get-OfficialAdapterReleaseSnapshot {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object]$Release,
        [Parameter(Mandatory)][string]$AdapterId,
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$SourceSha
    )
    $identity = Get-OfficialStableAdapterReleaseIdentity -Release $Release -AdapterId $AdapterId
    if ($AdapterId -cnotmatch '\Acom\.mirrorpulse\.adapter\.[a-z]+\z' -or
        $Repository -cne ('MirrorPulse/adapter-' + $AdapterId.Substring('com.mirrorpulse.adapter.'.Length)) -or
        $SourceSha -cnotmatch '\A[0-9a-f]{40}\z' -or [long]$Release.id -le 0) {
        throw 'The official release snapshot has an invalid repository, source, or release identity.'
    }
    $package = $AdapterId + '-' + $identity.version + '.mpadapter'
    $required = @($package, ($package + '.signature.json'))
    $optional = @(($package + '.public.pem'), 'provider-release.json')
    $assets = @($Release.assets)
    $names = @($assets | ForEach-Object { [string]$_.name })
    if ($assets.Count -notin @(2, 4) -or @($names | Select-Object -Unique).Count -ne $assets.Count -or
        @($assets.id | Select-Object -Unique).Count -ne $assets.Count) {
        throw 'The official release asset inventory is incomplete or ambiguous.'
    }
    foreach ($name in $required) {
        if ($name -cnotin $names) { throw 'The official release is missing its exact package or signature.' }
    }
    foreach ($asset in $assets) {
        if ([string]$asset.name -cnotin ($required + $optional) -or [long]$asset.id -le 0 -or
            [long]$asset.size -le 0 -or [long]$asset.size -gt 256MB -or
            [string]$asset.digest -cnotmatch '\Asha256:[0-9a-f]{64}\z') {
            throw 'An official release asset lacks a bounded identity and SHA256 digest.'
        }
    }
    [pscustomobject][ordered]@{
        adapterId = $AdapterId; repository = $Repository; sourceSha = $SourceSha
        release = [pscustomobject][ordered]@{
            id = [long]$Release.id; tag_name = $identity.tag; draft = $false; prerelease = $false
            published_at = [string]$Release.published_at; html_url = [string]$Release.html_url
            assets = @(foreach ($asset in $assets) {
                [pscustomobject][ordered]@{ id = [long]$asset.id; name = [string]$asset.name
                    size = [long]$asset.size; digest = [string]$asset.digest }
            })
        }
    }
}

function Get-OfficialAdapterTagSource {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Repository, [Parameter(Mandatory)][string]$Tag)
    $reference = & gh api "repos/$Repository/git/ref/tags/$([Uri]::EscapeDataString($Tag))" | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or $null -eq $reference) { throw 'The official release tag could not be resolved.' }
    $target = $reference.object
    for ($depth = 0; $depth -lt 4; $depth++) {
        if ([string]$target.sha -cnotmatch '\A[0-9a-f]{40}\z') { throw 'The release tag target has an invalid source identity.' }
        if ($target.type -ceq 'commit') { return [string]$target.sha }
        if ($target.type -cne 'tag') { throw 'The release tag does not identify a commit.' }
        $annotated = & gh api "repos/$Repository/git/tags/$($target.sha)" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $null -eq $annotated) { throw 'The annotated release tag could not be resolved.' }
        $target = $annotated.object
    }
    throw 'The release tag exceeds its bounded annotation depth.'
}
