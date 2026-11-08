[CmdletBinding()]
param([string]$PackagePath)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$pin = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'adapter-sdk.lock.json') -Raw | ConvertFrom-Json
if ($pin.schemaVersion -ne 1 -or $pin.packageId -cne 'MirrorPulse.Adapter.Sdk' -or
    $pin.version.Length -gt 64 -or $pin.version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-preview\.[1-9][0-9]*)?\z' -or
    $pin.sourceSha -cnotmatch '^[0-9a-f]{40}$' -or $pin.sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    $pin.length -le 0 -or $pin.length -gt 4MB) { throw 'The SDK pin is invalid.' }
$asset = "$($pin.packageId).$($pin.version).nupkg"
if ($pin.asset -cne $asset -or $pin.tag -cne "sdk-v$($pin.version)") { throw 'The fixed SDK asset address is invalid.' }
$feed = Join-Path $repository 'artifacts/sdk-feed'
New-Item -ItemType Directory -Path $feed -Force | Out-Null
$destination = Join-Path $feed $asset
function Assert-PinnedPackage([string]$Path) {
    if ((Get-Item -LiteralPath $Path).Length -ne $pin.length -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pin.sha256) {
        throw 'The fixed SDK package does not match its pinned length and SHA256.'
    }
}
function Receive-PinnedPackage([string]$Path) {
    $url = "https://github.com/MirrorPulse/adapter-template/releases/download/$($pin.tag)/$asset"
    try {
        Invoke-WebRequest -Uri $url -OutFile $Path -MaximumRetryCount 3 -RetryIntervalSec 2
        return
    } catch {
        $status = [int]$_.Exception.Response.StatusCode
        if ($status -notin @(408,429,500,502,503,504)) { throw }
        Write-Host "The fixed SDK download returned HTTP $status; trying its exact release asset API."
    }
    # The second route selects the same immutable bytes; no version or hash fallback is allowed.
    $release = Invoke-RestMethod -Uri "https://api.github.com/repos/mirrorpulse/adapter-template/releases/tags/$($pin.tag)" `
        -Headers @{ Accept = 'application/vnd.github+json' } -MaximumRetryCount 2 -RetryIntervalSec 2
    $selected = @($release.assets | Where-Object { $_.name -ceq $asset })
    if ($release.tag_name -cne $pin.tag -or $release.draft -or $selected.Count -ne 1 -or
        $selected[0].size -ne $pin.length -or $selected[0].url -cnotmatch '\Ahttps://api\.github\.com/repos/mirrorpulse/adapter-template/releases/assets/[1-9][0-9]*\z' -or
        $selected[0].digest -cne ('sha256:' + $pin.sha256)) {
        throw 'The fixed SDK release asset API does not match its original pin.'
    }
    Invoke-WebRequest -Uri $selected[0].url -Headers @{ Accept = 'application/octet-stream' } `
        -OutFile $Path -MaximumRetryCount 2 -RetryIntervalSec 2
}
if (Test-Path -LiteralPath $destination) {
    Assert-PinnedPackage $destination
} else {
    $temporary = Join-Path $feed ([Guid]::NewGuid().ToString('N') + '.download')
    try {
        if ($PackagePath) { Copy-Item -LiteralPath $PackagePath -Destination $temporary }
        else {
            Receive-PinnedPackage $temporary
        }
        Assert-PinnedPackage $temporary
        Move-Item -LiteralPath $temporary -Destination $destination
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
Write-Host "Verified pinned $($pin.packageId) $($pin.version) in the local SDK feed."
