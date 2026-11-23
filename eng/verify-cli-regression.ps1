[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$CliHostPublishDirectory,
    [string]$EvidencePath
)

$ErrorActionPreference = "Stop"
$integration = Join-Path $PSScriptRoot "verify-cli-integration.ps1"
$publishArguments = if ($CliHostPublishDirectory) { @('-CliHostPublishDirectory', $CliHostPublishDirectory) }
& pwsh -NoProfile -File $integration -Runtime $Runtime -PackagePath $PackagePath `
    -Configuration $Configuration -Regression -EvidencePath $EvidencePath @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "The CLI regression matrix failed for $Runtime."
}
