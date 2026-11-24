[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$archive = Join-Path $root 'artifacts/managed-build.tar'
$checksum = Get-Content -LiteralPath (Join-Path $root 'artifacts/managed-build.sha256')
if ((Get-FileHash -LiteralPath $archive).Hash -cne $checksum) {
    throw 'The shared Release build changed in transit.'
}

& tar.exe -xf $archive -C $root
if ($LASTEXITCODE -ne 0) { throw 'Extracting the shared Release build failed.' }
