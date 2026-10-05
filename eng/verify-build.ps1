[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$solution = Join-Path $PSScriptRoot "..\MirrorPulse.sln"

function Invoke-Dotnet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

& (Join-Path $PSScriptRoot "restore-adapter-sdk.ps1")

Invoke-Dotnet @("restore", $solution, "--locked-mode")
Invoke-Dotnet @("build", $solution, "--configuration", $Configuration, "--no-restore")
Invoke-Dotnet @("test", $solution, "--configuration", $Configuration, "--no-build")
