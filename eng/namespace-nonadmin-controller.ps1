function Get-OrdinaryUserNamespaceBudgetMilliseconds {
    param([Parameter(Mandatory)][ValidateRange(1, 112)][int]$TestCount)
    # The full suite now exceeds the original ten-minute envelope once process
    # startup is included. Budget the whole selected catalog, with a fixed upper
    # bound of 30 minutes (120000 + 112 * 15000). Every test retains its
    # existing native-operation deadline; the current required catalog has 104.
    return [Math]::Max(600000, 120000 + $TestCount * 15000)
}

function Get-OrdinaryUserNamespaceStepTimeoutMinutes {
    param([Parameter(Mandatory)][ValidateRange(1, 112)][int]$TestCount)
    # The workflow must let the existing child budget expire and allow the
    # controller to preserve diagnostics and remove its disposable user/profile.
    $childMilliseconds = Get-OrdinaryUserNamespaceBudgetMilliseconds -TestCount $TestCount
    return [int][Math]::Ceiling($childMilliseconds / 60000.0) + 5
}

function Copy-OrdinaryUserNamespaceFailureEvidence {
    param([Parameter(Mandatory)][string]$Workspace, [Parameter(Mandatory)][string]$Identifier,
        [Parameter(Mandatory)][string]$EvidenceDirectory)
    $workspacePath = [IO.Path]::GetFullPath($Workspace)
    $evidencePath = [IO.Path]::GetFullPath($EvidenceDirectory)
    $markerPath = Join-Path $workspacePath '.mp-namespace-ci'
    $prefix = $workspacePath + [IO.Path]::DirectorySeparatorChar
    if ($Identifier -cnotmatch '^[0-9a-f]{32}$' -or [IO.Path]::GetFileName($workspacePath) -cne $Identifier -or
        $evidencePath.Equals($workspacePath, [StringComparison]::OrdinalIgnoreCase) -or
        $evidencePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath $markerPath -PathType Leaf) -or
        ((Get-Item -LiteralPath $workspacePath).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        ((Get-Item -LiteralPath $markerPath).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        [IO.File]::ReadAllText($markerPath) -cne $Identifier) {
        throw 'Failure evidence requires the original marked disposable workspace and an external evidence directory.'
    }
    New-Item -ItemType Directory -Path $evidencePath -Force | Out-Null
    foreach ($entry in @(
        @{source='work/context.json';target='context.json'},
        @{source='work/results/namespace.trx';target='namespace.trx'},
        @{source='stdout.log';target='child-stdout.log'},
        @{source='stderr.log';target='child-stderr.log'})) {
        $sourcePath = Join-Path $workspacePath $entry.source
        $ancestor = [IO.Path]::GetDirectoryName($sourcePath)
        while ($ancestor -cne $workspacePath) {
            if (-not $ancestor.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Failure evidence escaped its workspace.' }
            if ((Test-Path -LiteralPath $ancestor) -and
                ((Get-Item -LiteralPath $ancestor).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Failure evidence must not traverse a reparse point.'
            }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) { continue }
        $file = Get-Item -LiteralPath $sourcePath
        if ($file.Attributes -band [IO.FileAttributes]::ReparsePoint -or $file.Length -gt 10MB) {
            throw 'Failure evidence must be a bounded ordinary diagnostic file.'
        }
        Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $evidencePath $entry.target)
    }
}
