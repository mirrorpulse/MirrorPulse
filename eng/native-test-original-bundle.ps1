function Copy-OrdinaryUserNativeOriginals {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Workspace,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{32}$')][string]$Identifier,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedTestAssemblySha256,
        [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{64}$')][string]$ExpectedCollectorSha256,
        [Parameter(Mandatory)][string]$DestinationWorkspace,
        [Parameter(Mandatory)][string]$DestinationDirectory,
        [bool]$RequireCollection = $false
    )
    . (Join-Path $PSScriptRoot 'native-test-originals.ps1')
    $sourceRoot = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Workspace))
    $marker = Get-NativeOriginalPath (Join-Path $sourceRoot '.mp-namespace-ci') $sourceRoot
    if ([IO.Path]::GetFileName($sourceRoot) -cne $Identifier -or
        -not (Test-Path -LiteralPath $marker -PathType Leaf) -or [IO.File]::ReadAllText($marker) -cne $Identifier) {
        throw 'Native input retention requires the original marked disposable workspace.'
    }
    $originals = Get-NativeOriginalPath (Join-Path $sourceRoot 'work/native-originals') $sourceRoot
    if (-not (Test-Path -LiteralPath $originals)) {
        if ($RequireCollection) { throw 'The ordinary-user native originals are missing.' }
        return
    }
    $destination = Get-NativeOriginalPath $DestinationDirectory $DestinationWorkspace
    if (Test-Path -LiteralPath $destination) {
        if ($RequireCollection) { throw 'Preserve the existing ordinary-user native originals.' }
        return
    }
    $names = @('prepared.json', 'collected.json', 'policy/native-test-originals.ps1', 'results/namespace.trx') +
        @(Get-NativeRuntimeInputNames | ForEach-Object { 'inputs/' + $_ })
    $copied = @(foreach ($name in $names) {
        $source = Get-NativeOriginalPath (Join-Path $originals $name) $sourceRoot
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            if ($RequireCollection) { throw 'A required ordinary-user native original is missing.' }
            continue
        }
        $target = Get-NativeOriginalPath (Join-Path $destination $name) $DestinationWorkspace
        $file = Get-Item -LiteralPath $source -Force
        if ($file.Length -gt 128MB -or (Test-Path -LiteralPath $target)) { throw 'A native original is oversized or already retained.' }
        $hash = (Get-FileHash -LiteralPath $source).Hash
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
        Copy-Item -LiteralPath $source -Destination $target
        if ((Get-FileHash -LiteralPath $source).Hash -cne $hash -or (Get-FileHash -LiteralPath $target).Hash -cne $hash) {
            throw 'An ordinary-user native original changed during retention.'
        }
        [ordered]@{ path = $name; length = $file.Length; sha256 = $hash }
    })
    if ($RequireCollection) {
        $preparedPath = Join-Path $destination 'prepared.json'
        $prepared = Get-Content -LiteralPath $preparedPath -Raw | ConvertFrom-Json
        $collected = Get-Content -LiteralPath (Join-Path $destination 'collected.json') -Raw | ConvertFrom-Json
        $assembly = Join-Path $sourceRoot 'payload/MirrorPulse.CloudFiles.CfSharp.Tests.dll'
        if ($prepared.schemaVersion -ne 1 -or $collected.schemaVersion -ne 1 -or $prepared.sourceSha -cne $SourceSha -or $collected.sourceSha -cne $SourceSha -or
            $prepared.suite -cne 'namespace-nonadmin' -or $collected.suite -cne 'namespace-nonadmin' -or $prepared.inputs.Count -ne 12 -or
            $prepared.workspace -ine $sourceRoot -or $prepared.outputDirectory -ine $originals -or $prepared.testAssembly -ine $assembly -or
            $collected.testAssembly -ine $assembly -or $collected.preparedSha256 -cne (Get-FileHash -LiteralPath $preparedPath).Hash -or
            $collected.gateSuccessEstablishedByCollection -ne $false -or $collected.resultCount -lt 1 -or $collected.resultCount -ne @($collected.results).Count -or
            @($collected.results | Where-Object { $_.codeBase -ine $assembly }).Count -or
            $prepared.collector.sha256 -ine $ExpectedCollectorSha256 -or
            (Get-FileHash -LiteralPath (Join-Path $destination 'policy/native-test-originals.ps1')).Hash -ine $ExpectedCollectorSha256 -or
            (Get-FileHash -LiteralPath (Join-Path $destination 'inputs/MirrorPulse.CloudFiles.CfSharp.Tests.dll')).Hash -ine $ExpectedTestAssemblySha256) {
            throw 'Ordinary-user native originals lost their actual source, controller input or original collection binding.'
        }
        foreach ($name in @(Get-NativeRuntimeInputNames)) {
            $binding = @($prepared.inputs | Where-Object { $_.path -ieq (Join-Path $sourceRoot ('payload/' + $name)) -and $_.archivePath -ceq ('inputs/' + $name) })
            $retained = Join-Path $destination ('inputs/' + $name)
            if ($binding.Count -ne 1 -or $binding[0].sha256 -cne (Get-FileHash -LiteralPath $retained).Hash -or $binding[0].length -ne (Get-Item -LiteralPath $retained).Length) {
                throw 'A retained native runtime input differs from its original preparation.'
            }
            Assert-NativeProductSource $retained $SourceSha
        }
        $trx = Get-NativeOriginalPath (Join-Path $sourceRoot 'work/results/namespace.trx') $sourceRoot
        if ($collected.originals.Count -ne 1 -or $collected.originals[0].archivePath -cne 'results/namespace.trx' -or
            $collected.originals[0].sha256 -cne (Get-FileHash -LiteralPath (Join-Path $destination 'results/namespace.trx')).Hash -or
            $collected.originals[0].sha256 -cne (Get-FileHash -LiteralPath $trx).Hash) { throw 'The retained native report differs from the actual ordinary-user TRX.' }
    }
    if ($copied.Count) {
        [ordered]@{ schemaVersion = 1; sourceSha = $SourceSha; verifiedComplete = $RequireCollection; files = $copied
            expectedControllerAssemblySha256 = $ExpectedTestAssemblySha256; gateSuccessEstablishedByRetention = $false } |
            ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $destination 'controller-retention.json') -Encoding utf8
    }
}
