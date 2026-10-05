[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'official-adapter-release-policy.ps1')

foreach ($version in @('0.0.0', '1.2.3', '0.1.2.3', '2147483647.0.0')) {
    $release = [pscustomobject]@{ tag_name = 'v' + $version; draft = $false; prerelease = $false; published_at = '2026-01-01T00:00:00Z' }
    $identity = Get-OfficialStableAdapterReleaseIdentity -Release $release -AdapterId 'com.mirrorpulse.adapter.local'
    if ($identity.version -cne $version -or $identity.tag -cne $release.tag_name -or $identity.channel -cne 'stable') {
        throw 'The official release selector changed a published stable identity.'
    }
}

$refused = @(
    @{ tag_name = 'v1.0.0-preview.1'; draft = $false; prerelease = $true; published_at = '2026-01-01T00:00:00Z' },
    @{ tag_name = 'v1.0.0-preview.1'; draft = $false; prerelease = $false; published_at = '2026-01-01T00:00:00Z' },
    @{ tag_name = 'v1.0.0'; draft = $true; prerelease = $false; published_at = '2026-01-01T00:00:00Z' },
    @{ tag_name = 'v1.0.0'; draft = $false; prerelease = $false; published_at = $null },
    @{ tag_name = 'v1.0.0'; draft = 'false'; prerelease = $false; published_at = '2026-01-01T00:00:00Z' },
    @{ tag_name = 'v1.0.0'; draft = $false; prerelease = 'false'; published_at = '2026-01-01T00:00:00Z' },
    @{ tag_name = 'v01.0.0'; draft = $false; prerelease = $false; published_at = '2026-01-01T00:00:00Z' },
    @{ tag_name = 'v2147483648.0.0'; draft = $false; prerelease = $false; published_at = '2026-01-01T00:00:00Z' },
    @{ tag_name = 'v1.0.0.0.0'; draft = $false; prerelease = $false; published_at = '2026-01-01T00:00:00Z' },
    @{ tag_name = "v1.0.0`n"; draft = $false; prerelease = $false; published_at = '2026-01-01T00:00:00Z' },
    @{ tag_name = '../v1.0.0'; draft = $false; prerelease = $false; published_at = '2026-01-01T00:00:00Z' }
)
foreach ($release in $refused) {
    $rejected = $false
    try { $null = Get-OfficialStableAdapterReleaseIdentity -Release ([pscustomobject]$release) -AdapterId 'com.mirrorpulse.adapter.local' }
    catch { $rejected = $true }
    if (-not $rejected) { throw 'The default official aggregate accepted an unpublished, preview, ambiguous, or malformed release.' }
}
Write-Output 'Official stable selection passed: four preserved identities and eleven rejected release boundaries.'
