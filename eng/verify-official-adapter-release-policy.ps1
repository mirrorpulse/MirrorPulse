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

$fixture = [pscustomobject]@{
    id = 7; tag_name = 'v1.0.0'; draft = $false; prerelease = $false
    published_at = '2026-01-01T00:00:00Z'; html_url = 'https://github.com/MirrorPulse/adapter-local/releases/tag/v1.0.0'
    assets = @(
        [pscustomobject]@{ id = 1; name = 'com.mirrorpulse.adapter.local-1.0.0.mpadapter'; size = 7; digest = 'sha256:' + ('a' * 64) },
        [pscustomobject]@{ id = 2; name = 'com.mirrorpulse.adapter.local-1.0.0.mpadapter.signature.json'; size = 7; digest = 'sha256:' + ('b' * 64) }
    )
}
$arguments = @{ AdapterId = 'com.mirrorpulse.adapter.local'; Repository = 'MirrorPulse/adapter-local'; SourceSha = ('a' * 40) }
$snapshot = Get-OfficialAdapterReleaseSnapshot @arguments -Release $fixture
if ($snapshot.sourceSha -cne ('a' * 40) -or $snapshot.release.id -ne 7 -or $snapshot.release.assets.Count -ne 2) {
    throw 'The frozen release changed its source, release ID, or legacy inventory.'
}
foreach ($mutate in @(
    { param($release) $release.id = 0 },
    { param($release) $release.assets[0].digest = 'sha256:invalid' },
    { param($release) $release.assets[0].size = 256MB + 1 },
    { param($release) $release.assets[0].size = 0 },
    { param($release) $release.assets[0].id = $release.assets[1].id },
    { param($release) $release.assets[0].name = '../payload.mpadapter' },
    { param($release) $release.assets[1].name = $release.assets[0].name },
    { param($release) $release.assets = @($release.assets[0]) }
)) {
    $release = $fixture | ConvertTo-Json -Depth 5 | ConvertFrom-Json
    & $mutate $release
    $rejected = $false
    try { $null = Get-OfficialAdapterReleaseSnapshot @arguments -Release $release } catch { $rejected = $true }
    if (-not $rejected) { throw 'The frozen release accepted an ambiguous, unsafe, or unbounded asset inventory.' }
}
foreach ($invalid in @(
    @{ AdapterId = 'com.mirrorpulse.adapter.local'; Repository = 'MirrorPulse/adapter-webdav'; SourceSha = ('a' * 40) },
    @{ AdapterId = 'com.mirrorpulse.adapter.local'; Repository = 'MirrorPulse/adapter-local'; SourceSha = 'main' }
)) {
    $rejected = $false
    try { $null = Get-OfficialAdapterReleaseSnapshot @invalid -Release $fixture } catch { $rejected = $true }
    if (-not $rejected) { throw 'The frozen release accepted a different repository or mutable source name.' }
}
$fixture.assets += @(
    [pscustomobject]@{ id = 3; name = 'com.mirrorpulse.adapter.local-1.0.0.mpadapter.public.pem'; size = 7; digest = 'sha256:' + ('c' * 64) },
    [pscustomobject]@{ id = 4; name = 'provider-release.json'; size = 7; digest = 'sha256:' + ('d' * 64) }
)
if ((Get-OfficialAdapterReleaseSnapshot @arguments -Release $fixture).release.assets.Count -ne 4) {
    throw 'The frozen release refused the current four-asset publication inventory.'
}
Write-Output 'Frozen official release selection passed: legacy and current inventories plus ten rejected source and asset boundaries.'

$preview = $fixture | ConvertTo-Json -Depth 5 | ConvertFrom-Json
$preview.tag_name = 'v1.0.0-preview.1'
$preview.prerelease = $true
foreach ($asset in $preview.assets) { $asset.name = $asset.name.Replace('-1.0.0.', '-1.0.0-preview.1.') }
$identity = Get-OfficialAdapterReleaseIdentity -Release $preview -AdapterId $arguments.AdapterId -AllowPreview
$snapshot = Get-OfficialAdapterReleaseSnapshot @arguments -Release $preview -AllowPreview
if ($identity.version -cne '1.0.0-preview.1' -or $identity.channel -cne 'preview' -or -not $snapshot.release.prerelease) {
    throw 'Explicit preview verification changed the selected version or release classification.'
}
$rejected = $false
try { $null = Get-OfficialAdapterReleaseSnapshot @arguments -Release $preview } catch { $rejected = $true }
if (-not $rejected) { throw 'A preview entered the default frozen candidate.' }
foreach ($tag in @('v1.0.0-preview.0', 'v1.0.0-preview.01', 'v1.0.0-preview.2147483648', 'v1.0.0.0-preview.1', 'v1.0.0-Preview.1')) {
    $preview.tag_name = $tag
    $rejected = $false
    try { $null = Get-OfficialAdapterReleaseIdentity -Release $preview -AdapterId $arguments.AdapterId -AllowPreview } catch { $rejected = $true }
    if (-not $rejected) { throw 'Explicit preview verification accepted a malformed or unbounded preview version.' }
}
Write-Output 'Explicit preview selection passed: one fixed preview and six rejected default/version boundaries.'
