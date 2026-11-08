[CmdletBinding()]
param([string]$PackagePath, [switch]$RequireCachedPackage)
$ErrorActionPreference = 'Stop'
if ($RequireCachedPackage -and $PSBoundParameters.ContainsKey('PackagePath')) {
    throw 'A required frozen SDK package cannot be combined with a supplied package path.'
}
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
function Get-SdkRequestStatus([Management.Automation.ErrorRecord]$Failure) {
    $errorObject = $Failure.Exception
    for ($depth = 0; $depth -lt 4 -and $null -ne $errorObject; $depth++) {
        if ($null -ne $errorObject.Response -and $null -ne $errorObject.Response.StatusCode) {
            return [int]$errorObject.Response.StatusCode
        }
        if ($errorObject -is [Net.Http.HttpRequestException] -or $errorObject -is [OperationCanceledException] -or
            $errorObject -is [TimeoutException] -or $errorObject -is [Net.WebException]) { return 0 }
        $errorObject = $errorObject.InnerException
    }
    return -1
}
function Get-SdkApiHeaders([string]$Accept) {
    $headers = @{ Accept = $Accept }
    if ($env:GITHUB_ACTIONS -ceq 'true' -and -not [string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) {
        $headers.Authorization = 'Bearer ' + $env:GITHUB_TOKEN
    }
    return $headers
}
function New-SdkRequestFailure([string]$Boundary, [int]$Status) {
    $category = if ($Status -gt 0) { "HTTP $Status" } elseif ($Status -eq 0) { 'transport unavailable' } else { 'request failed' }
    # Keep server bodies and credentials out of ordinary build logs.
    return [IO.IOException]::new("The fixed SDK $Boundary failed ($category).")
}
function Receive-PinnedPackage([string]$Path) {
    $url = "https://github.com/MirrorPulse/adapter-template/releases/download/$($pin.tag)/$asset"
    try {
        Invoke-WebRequest -Uri $url -OutFile $Path -MaximumRetryCount 1 -RetryIntervalSec 2 `
            -ConnectionTimeoutSeconds 15 -OperationTimeoutSeconds 30
        return
    } catch {
        $status = Get-SdkRequestStatus $_
        if ($status -notin @(0,408,429,500,502,503,504)) { throw (New-SdkRequestFailure 'download' $status) }
        $category = if ($status -eq 0) { 'transport unavailable' } else { "HTTP $status" }
        Write-Host "The fixed SDK download is temporarily unavailable ($category); trying its exact release asset API."
    }
    # The second route selects the same immutable bytes; no version or hash fallback is allowed.
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/mirrorpulse/adapter-template/releases/tags/$($pin.tag)" `
            -Headers (Get-SdkApiHeaders 'application/vnd.github+json') -MaximumRetryCount 2 -RetryIntervalSec 2 `
            -ConnectionTimeoutSeconds 15 -OperationTimeoutSeconds 30
    } catch { throw (New-SdkRequestFailure 'release metadata request' (Get-SdkRequestStatus $_)) }
    $selected = @($release.assets | Where-Object { $_.name -ceq $asset })
    if ($release.tag_name -cne $pin.tag -or $release.draft -or $selected.Count -ne 1 -or
        $selected[0].size -ne $pin.length -or $selected[0].url -cnotmatch '\Ahttps://api\.github\.com/repos/mirrorpulse/adapter-template/releases/assets/[1-9][0-9]*\z' -or
        $selected[0].digest -cne ('sha256:' + $pin.sha256)) {
        throw 'The fixed SDK release asset API does not match its original pin.'
    }
    try {
        # PowerShell drops Authorization on redirects; never preserve it for the asset host.
        Invoke-WebRequest -Uri $selected[0].url -Headers (Get-SdkApiHeaders 'application/octet-stream') `
            -OutFile $Path -MaximumRetryCount 2 -RetryIntervalSec 2 -ConnectionTimeoutSeconds 15 -OperationTimeoutSeconds 30
    } catch { throw (New-SdkRequestFailure 'release asset download' (Get-SdkRequestStatus $_)) }
}
if (Test-Path -LiteralPath $destination) {
    Assert-PinnedPackage $destination
} else {
    if ($RequireCachedPackage) { throw 'The required frozen SDK package is missing from the shared feed.' }
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
