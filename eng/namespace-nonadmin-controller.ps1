function Get-OrdinaryUserNamespaceBudgetMilliseconds {
    param([Parameter(Mandatory)][ValidateRange(1, 112)][int]$TestCount, [string[]]$TestNames)
    $executions = $TestCount
    $transferDeadlineMilliseconds = 0
    if ($PSBoundParameters.ContainsKey('TestNames')) {
        if ($TestNames.Count -ne $TestCount) { throw 'The namespace budget must describe the exact selected catalog.' }
        $selected = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $repeated = [Collections.Generic.Dictionary[string, int]]::new([StringComparer]::Ordinal)
        $prefix = 'MirrorPulse.CloudFiles.CfSharp.Tests.MirrorPulseWindowsNamespacePermissionLeaseTests.'
        $repeated.Add($prefix + 'NativeHostTransferRetainsSourceAndBothFencesAfterOriginalCopyAndWholeTargetVerification', 2)
        $repeated.Add($prefix + 'NativeHostTransferDeletesOnlyOriginalRemoteSourceWhileRetainingGuardedLocalBytes', 2)
        $repeated.Add($prefix + 'NativeHostTransferRetiresOriginalSourceThroughTheSdkAndKeepsPrivateBytesAndBothFences', 2)
        $repeated.Add($prefix + 'NativeHostTransferSourceRetirementResumesOriginalNativeAndReceiptBoundariesAcrossStoreOwners', 10)
        $repeated.Add($prefix + 'NativeHostTransferAcknowledgesOriginalClaimAndRecoversSdkReturnBeforeReceiptAcrossStoreOwners', 2)
        foreach ($name in $TestNames) {
            if ([string]::IsNullOrWhiteSpace($name) -or $name.Length -gt 512 -or $name -cnotmatch '^[A-Za-z0-9_.]+$' -or -not $selected.Add($name)) {
                throw 'The namespace budget requires distinct, bounded test names.'
            }
            if ($repeated.ContainsKey($name)) {
                $executions += $repeated[$name] - 1
                # Each original transfer scope/cutpoint already owns a 45-second
                # native deadline. The aggregate controller must preserve it;
                # its ordinary 15-second per-case estimate cannot replace it.
                $transferDeadlineMilliseconds += $repeated[$name] * 30000
            }
        }
    }
    # Count both scopes inside the five selected transfer methods, including the
    # retirement matrix and ACK retries inside their original native deadlines.
    # At most 112 names plus 13 repeated executions and the existing 45-second
    # deadlines for all 18 transfer executions gives a bounded 42m15s child
    # envelope. Count-only callers retain 30 minutes; the step adds five minutes
    # for controller diagnostics and disposable-account/profile cleanup.
    return [Math]::Max(600000, 120000 + $executions * 15000 + $transferDeadlineMilliseconds)
}

function Get-OrdinaryUserNamespaceStepTimeoutMinutes {
    param([Parameter(Mandatory)][ValidateRange(1, 112)][int]$TestCount, [string[]]$TestNames)
    # The workflow must let the existing child budget expire and allow the
    # controller to preserve diagnostics and remove its disposable user/profile.
    $parameters = @{ TestCount = $TestCount }
    if ($PSBoundParameters.ContainsKey('TestNames')) { $parameters.TestNames = $TestNames }
    $childMilliseconds = Get-OrdinaryUserNamespaceBudgetMilliseconds @parameters
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
