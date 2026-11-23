[CmdletBinding()]
param(
    [ValidateSet("win-x64", "win-arm64")]
    [string]$Runtime = "win-x64",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$sourceSha = git -C $repositoryRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $sourceSha -cnotmatch '\A[0-9a-f]{40}\z' -or
    ($env:GITHUB_SHA -and $env:GITHUB_SHA -cne $sourceSha)) {
    throw 'Could not bind the CLI and Host publish to the source revision.'
}
$ownedOutput = $false
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path ([IO.Path]::GetTempPath()) "MirrorPulse-cli-host-$Runtime-$([guid]::NewGuid().ToString('N'))"
    $ownedOutput = $true
}

$cliOutput = $OutputDirectory
$hostOutput = Join-Path $OutputDirectory "host"
New-Item -ItemType Directory -Path $cliOutput, $hostOutput -Force | Out-Null

try {
    & dotnet publish (Join-Path $repositoryRoot "src\MirrorPulse.Cli\MirrorPulse.Cli.csproj") `
        --configuration $Configuration --runtime $Runtime --self-contained true --no-restore --output $cliOutput
    if ($LASTEXITCODE -ne 0) { throw "CLI publish failed for $Runtime." }

    & dotnet publish (Join-Path $repositoryRoot "src\MirrorPulse.Host\MirrorPulse.Host.csproj") `
        --configuration $Configuration --runtime $Runtime --self-contained true --no-restore --output $hostOutput
    if ($LASTEXITCODE -ne 0) { throw "Host publish failed for $Runtime." }

    $cliExecutable = Join-Path $cliOutput "mp.exe"
    $hostExecutable = Join-Path $hostOutput "MirrorPulse.Host.exe"
    if (-not (Test-Path -LiteralPath $cliExecutable -PathType Leaf) -or
        -not (Test-Path -LiteralPath $hostExecutable -PathType Leaf)) {
        throw "The $Runtime publish output is missing mp.exe or MirrorPulse.Host.exe."
    }

    $version = (& $cliExecutable --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $version -notmatch '^MirrorPulse mp [0-9]+\.[0-9]+\.[0-9]+') {
        throw "The published $Runtime CLI did not return a valid version."
    }

    $files = Get-ChildItem -LiteralPath $OutputDirectory -File -Recurse |
        Where-Object { $_.Name -ne "publish-manifest.json" } |
        Sort-Object FullName |
        ForEach-Object {
            [ordered]@{
                path = [IO.Path]::GetRelativePath($OutputDirectory, $_.FullName).Replace('\', '/')
                sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
                length = $_.Length
            }
        }
    [ordered]@{
        schemaVersion = 1
        sourceSha = $sourceSha
        runtime = $Runtime
        configuration = $Configuration
        cliVersion = $version.Substring($version.LastIndexOf(' ') + 1)
        files = @($files)
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $OutputDirectory "publish-manifest.json") -Encoding utf8

    Write-Output "Verified CLI and Host publish for ${Runtime}: $version"
}
finally {
    if ($ownedOutput -and (Test-Path -LiteralPath $OutputDirectory)) {
        Remove-Item -LiteralPath $OutputDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
}
