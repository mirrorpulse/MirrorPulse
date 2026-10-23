. (Join-Path $PSScriptRoot 'cli-transfer-evidence-policy.ps1')

function Invoke-CliCrossPackageVerification {
    param(
        [Parameter(Mandatory)][ValidatePattern('\A[0-9a-f]{40}\z')][string]$SourceSha,
        [AllowEmptyString()][ValidatePattern('\A[0-9]*\z')][string]$RunId,
        [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Runtime,
        [Parameter(Mandatory)][string]$EvidencePath,
        [Parameter(Mandatory)][scriptblock]$Prepare,
        [Parameter(Mandatory)][scriptblock]$Verify
    )

    $evidence = [IO.Path]::GetFullPath($EvidencePath)
    $originals = Join-Path ([IO.Path]::GetDirectoryName($evidence)) 'cli-cross-package.originals'
    $phase = 'PrepareSignedPackages'
    try {
        if (Test-Path -LiteralPath $evidence) { throw 'A previous cross-package report cannot authorize this execution.' }
        $packages = & $Prepare
        $phase = 'VerifyActualTransfers'
        & $Verify $packages | Out-Host
        $phase = 'VerifyOriginalReport'
        if (-not (Test-Path -LiteralPath $evidence -PathType Leaf)) { throw 'The cross-package verifier did not produce its original complete report.' }
        $report = Get-Content -Raw -LiteralPath $evidence | ConvertFrom-Json
        if ($report.schemaVersion -ne 3 -or $report.sourceSha -cne $SourceSha -or
            $report.runtime -cne $Runtime -or [string]$report.runId -cne $RunId) {
            throw 'The cross-package report belongs to another source, runtime, run or execution shape.'
        }
        Assert-CliTransferObservations -Evidence $report -RequireSingleRoot
    } catch {
        $originalFailure = $_
        try {
            New-Item -ItemType Directory -Force -Path $originals | Out-Null
            $record = [ordered]@{
                schemaVersion = 1; sourceSha = $SourceSha; runId = $RunId; runtime = $Runtime
                phase = $phase; hresult = $originalFailure.Exception.HResult.ToString('X8')
                originalReportPresent = (Test-Path -LiteralPath $evidence -PathType Leaf)
                accepted = $false
            }
            # Record only a closed stage and typed source identity. Preserve the
            # first report and never export command arguments or exception text.
            $path = Join-Path $originals 'verification-failure.json'
            $stream = [IO.File]::Open($path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
            try {
                $bytes = [Text.Encoding]::UTF8.GetBytes(($record | ConvertTo-Json -Depth 4))
                $stream.Write($bytes)
            } finally { $stream.Dispose() }
        } catch { Write-Host 'The independent cross-package failure observer was unavailable; the original failure is retained.' }
        $PSCmdlet.ThrowTerminatingError($originalFailure)
    }
}
