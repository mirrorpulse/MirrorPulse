[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'win-arm64')][string]$Runtime = 'win-arm64',
    [Parameter(Mandatory)][string]$ReleaseLockPath,
    [Parameter(Mandatory)][string]$LocalPackageDirectory,
    [Parameter(Mandatory)][string]$WebDavPackageDirectory,
    [Parameter(Mandatory)][string]$EvidencePath
)

$ErrorActionPreference = 'Stop'
if ($env:MIRRORPULSE_NATIVE_TEST -cne '1') { throw 'Cross-package verification requires an explicitly enabled disposable native test environment.' }
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceSha = (& git -C $repository rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceSha -cnotmatch '\A[0-9a-f]{40}\z' -or
    ($env:GITHUB_SHA -and $sourceSha -cne $env:GITHUB_SHA)) { throw 'The checked-out cross-package source identity is unavailable.' }
. (Join-Path $PSScriptRoot 'cli-cross-package-verification-policy.ps1')
Invoke-CliCrossPackageVerification -SourceSha $sourceSha -RunId ([string]$env:GITHUB_RUN_ID) -Runtime $Runtime -EvidencePath $EvidencePath -Prepare {
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'aggregate-official-adapters.ps1') -ReleaseLockPath $ReleaseLockPath `
        -AdapterIds com.mirrorpulse.adapter.webdav -OutputDirectory $WebDavPackageDirectory | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The exact frozen signed WebDAV package is required.' }
    $local = @(Get-ChildItem -LiteralPath $LocalPackageDirectory -Recurse -Filter '*.mpadapter' -File)
    $webdav = @(Get-ChildItem -LiteralPath $WebDavPackageDirectory -Recurse -Filter '*.mpadapter' -File)
    if ($local.Count -ne 1 -or $webdav.Count -ne 1) { throw 'Exactly one frozen package per backend is required.' }
    [pscustomobject]@{ Local = $local[0].FullName; WebDav = $webdav[0].FullName }
} -Verify {
    param($packages)
    & pwsh -NoProfile -File (Join-Path $PSScriptRoot 'verify-cli-transfers.ps1') -Runtime $Runtime `
        -PackagePath $packages.Local -WebDavPackagePath $packages.WebDav -EvidencePath $EvidencePath | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'The real signed cross-package CLI transfer matrix failed.' }
}
