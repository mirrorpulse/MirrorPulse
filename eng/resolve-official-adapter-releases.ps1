[CmdletBinding()]
param([string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/official-adapter-releases.json'))
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'official-adapter-release-policy.ps1')
$catalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'official-adapters.json') -Raw | ConvertFrom-Json
if ($catalog.schemaVersion -ne 1 -or @($catalog.adapters).Count -ne 5) { throw 'The official catalog must contain five providers.' }
$source = (& git -C (Join-Path $PSScriptRoot '..') rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $source -cnotmatch '\A[0-9a-f]{40}\z' -or ($env:GITHUB_SHA -and $source -cne $env:GITHUB_SHA)) {
    throw 'The shared release candidate must describe the exact product source.'
}
$previousToken = $env:GH_TOKEN
try {
    if ($env:GITHUB_TOKEN) { $env:GH_TOKEN = $env:GITHUB_TOKEN }
    $records = @(foreach ($entry in $catalog.adapters) {
        $release = & gh api "repos/$($entry.repository)/releases/latest" | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0 -or $null -eq $release) { throw 'An official stable release could not be resolved.' }
        $identity = Get-OfficialStableAdapterReleaseIdentity -Release $release -AdapterId $entry.adapterId
        $tagSource = Get-OfficialAdapterTagSource -Repository $entry.repository -Tag $identity.tag
        Get-OfficialAdapterReleaseSnapshot -Release $release -AdapterId $entry.adapterId -Repository $entry.repository -SourceSha $tagSource
    })
    $output = [IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force | Out-Null
    [ordered]@{ schemaVersion = 1; mirrorPulseSourceSha = $source; adapters = $records } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $output -Encoding utf8
    Write-Output 'Resolved one stable official release candidate with fixed tags, commit sources, asset IDs and SHA256 digests.'
} finally { $env:GH_TOKEN = $previousToken }
