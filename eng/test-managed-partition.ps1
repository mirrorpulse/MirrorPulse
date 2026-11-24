[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('CatalogA', 'CatalogB', 'Core', 'Clients')][string]$Partition,
    [switch]$ListCases
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$preparation = Get-Content -LiteralPath (Join-Path $root 'artifacts/test-results/managed-originals/prepared.json') -Raw | ConvertFrom-Json
$source = git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $preparation.sourceSha -cne $source -or
    ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $source)) {
    throw 'The partition must run against the shared source revision.'
}

$catalogPrefix = 'MirrorPulse.Core.Tests.MirrorPulseNamespaceBirthCatalogTests.'
$catalogA = @('D','E','F','J','L','M','P','R','S','V')
$filter = switch ($Partition) {
    'CatalogA' { ($catalogA | ForEach-Object { "FullyQualifiedName~$catalogPrefix$_" }) -join '|' }
    'CatalogB' { (@("FullyQualifiedName~$catalogPrefix") + @($catalogA | ForEach-Object { "FullyQualifiedName!~$catalogPrefix$_" })) -join '&' }
    'Core' { "FullyQualifiedName!~$catalogPrefix" }
}
$names = if ($Partition -eq 'Clients') {
    @('MirrorPulse.Cli.Tests', 'MirrorPulse.CloudFiles.CfSharp.Tests')
} else {
    @('MirrorPulse.Core.Tests')
}
$results = Join-Path $root "artifacts/test-results/managed/$Partition"
New-Item -ItemType Directory -Path $results | Out-Null
foreach ($name in $names) {
    $prepared = @($preparation.assemblies | Where-Object { [IO.Path]::GetFileNameWithoutExtension($_.path) -ceq $name })
    if ($prepared.Count -ne 1 -or $prepared[0].path -cnotmatch "\Atests/$([regex]::Escape($name))/bin/Release/net[0-9.]+-windows[0-9.]+/$([regex]::Escape($name))\.dll\z") {
        throw "The shared test assembly binding is missing for $name."
    }
    $assembly = Join-Path $root $prepared[0].path
    if ((Get-FileHash -LiteralPath $assembly).Hash -cne $prepared[0].sha256) {
        throw "The shared test assembly has changed for $name."
    }
    if ($ListCases) {
        $arguments = @($assembly, '/ListTests', "/Diag:$(Join-Path $results ($name + '.discovery.log'))")
        if ($filter) { $arguments += "/TestCaseFilter:$filter" }
        & dotnet vstest @arguments
    } else {
        $arguments = @($assembly, '--results-directory', $results, '--logger', "trx;LogFileName=$name.trx",
            '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'none')
        if ($filter) { $arguments += @('--filter', $filter) }
        & dotnet test @arguments
    }
    if ($LASTEXITCODE -ne 0) { throw "Partition $Partition failed for $name." }
}
