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
