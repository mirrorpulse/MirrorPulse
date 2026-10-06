[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/official-adapter-releases.json'), [string]$PreviewReleaseMapPath)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'official-adapter-release-policy.ps1')
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'official-adapters.json') -Raw | ConvertFrom-Json
if ($catalog.schemaVersion -ne 1 -or @($catalog.adapters).Count -ne 5) { throw 'The official catalog must contain five providers.' }
$previews = @{}
if ($PreviewReleaseMapPath) {
    $map = Get-Content -LiteralPath $PreviewReleaseMapPath -Raw | ConvertFrom-Json
    if ($map.schemaVersion -ne 1 -or @($map.adapters).Count -notin 1..5) { throw 'The explicit preview map must contain one to five official providers.' }
    foreach ($entry in $map.adapters) {
        if ($entry.adapterId -cnotin @($catalog.adapters.adapterId) -or $previews.ContainsKey($entry.adapterId)) {
            throw 'The explicit preview map has an unknown or duplicate provider.'
        }
        $null = Get-OfficialAdapterReleaseIdentity -AdapterId $entry.adapterId -AllowPreview -Release ([pscustomobject]@{
            tag_name = $entry.tag; draft = $false; prerelease = $true; published_at = 'explicit-selection'
        })
        $previews[$entry.adapterId] = [string]$entry.tag
    }
}
$source = (& git -C (Join-Path $PSScriptRoot '..') rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '\A[0-9a-f]{40}\z' -or ($env:GITHUB_SHA -and $source -cne $env:GITHUB_SHA)) {
    throw 'The shared release candidate must describe the exact product source.'
}
$previousToken = $env:GH_TOKEN
try {
    if ($env:GITHUB_TOKEN) { $env:GH_TOKEN = $env:GITHUB_TOKEN }
    $records = @(foreach ($entry in $catalog.adapters) {
        $endpoint = if ($previews.ContainsKey($entry.adapterId)) {
            "repos/$($entry.repository)/releases/tags/$([Uri]::EscapeDataString($previews[$entry.adapterId]))"
        } else { "repos/$($entry.repository)/releases/latest" }
        $release = & gh api $endpoint | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $null -eq $release) { throw 'An official stable release could not be resolved.' }
        $identity = Get-OfficialAdapterReleaseIdentity -Release $release -AdapterId $entry.adapterId -AllowPreview:($previews.ContainsKey($entry.adapterId))
        if ($previews.ContainsKey($entry.adapterId) -and $identity.tag -cne $previews[$entry.adapterId]) { throw 'The preview response changed the explicitly selected tag.' }
        $tagSource = Get-OfficialAdapterTagSource -Repository $entry.repository -Tag $identity.tag
        Get-OfficialAdapterReleaseSnapshot -Release $release -AdapterId $entry.adapterId -Repository $entry.repository -SourceSha $tagSource -AllowPreview:($previews.ContainsKey($entry.adapterId))
    })
    $output = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
    [ordered]@{ schemaVersion = 1; mirrorPulseSourceSha = $source; selection = if ($PreviewReleaseMapPath) { 'explicit-preview' } else { 'stable' }; adapters = $records } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $output -Encoding utf8
    Write-Output 'Resolved one official release candidate with fixed tags, commit sources, asset IDs and SHA256 digests.'
} finally { $env:GH_TOKEN = $previousToken }
