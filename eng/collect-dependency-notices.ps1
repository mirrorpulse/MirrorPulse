[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot "../artifacts/licenses"))

$ErrorActionPreference = "Stop"
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$licenseDirectory = Join-Path $OutputDirectory "licenses"
New-Item -ItemType Directory -Path $licenseDirectory -Force | Out-Null
$components = @{}
$sdkPin = Get-Content -LiteralPath (Join-Path $PSScriptRoot "adapter-sdk.lock.json") -Raw | ConvertFrom-Json
$licenseSources = Get-Content -LiteralPath (Join-Path $PSScriptRoot "dependency-license-sources.json") -Raw | ConvertFrom-Json
$projects = @(Get-ChildItem "$repositoryRoot/src", "$repositoryRoot/Adapters/official" -Filter "*.csproj" -File -Recurse |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
foreach ($project in $projects) {
    $assetsPath = (& dotnet msbuild $project.FullName -nologo -getProperty:ProjectAssetsFile | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $assetsPath -PathType Leaf)) {
        throw "Restore the project before collecting dependencies: $($project.Name)"
    }
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $packages = @{}
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -eq "package") {
            $packages[$library.Name] = [pscustomobject]@{path=$library.Value.path;sha512=$library.Value.sha512;kind="restored"}
        }
    }
    foreach ($framework in $assets.project.frameworks.PSObject.Properties) {
        foreach ($dependency in $framework.Value.downloadDependencies) {
            if ($dependency.name -notmatch '\.Runtime\.') { continue }
            $bounds = $dependency.version.Trim('[',']').Split(',').Trim()
            $version = $bounds[0]
            if ($bounds.Count -gt 2 -or ($bounds.Count -eq 2 -and $bounds[0] -ne $bounds[1])) {
                throw "A runtime download dependency is not locked to one version."
            }
            $packages["$($dependency.name)/$version"] = [pscustomobject]@{
                path="$($dependency.name.ToLowerInvariant())/$version";sha512=$null;kind="sdk-download"
            }
        }
    }
    foreach ($package in $packages.GetEnumerator()) {
        $parts = $package.Key.Split('/')
        $id, $version = $parts[0], $parts[1]
        if ($components.ContainsKey($package.Key)) { continue }
        $directory = $null
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $candidate = Join-Path $folder $package.Value.path
            if (Test-Path -LiteralPath $candidate -PathType Container) { $directory = $candidate; break }
        }
        if ($null -eq $directory) { throw "The restored package is unavailable: $($package.Key)" }
        $nuspecFile = @(Get-ChildItem -LiteralPath $directory -Filter "*.nuspec" -File)
        $archive = @(Get-ChildItem -LiteralPath $directory -Filter "*.nupkg" -File)
        if ($nuspecFile.Count -ne 1 -or $archive.Count -ne 1) { throw "Package metadata is incomplete: $($package.Key)" }
        $stream = [IO.File]::OpenRead($archive[0].FullName)
        try { $hash = [Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData($stream)) }
        finally { $stream.Dispose() }
        # NuGet's content hash excludes the signing envelope; the archive hash does not.
        $archiveHash = (Get-Content -LiteralPath "$($archive[0].FullName).sha512" -Raw).Trim()
        $metadata = Get-Content -LiteralPath (Join-Path $directory ".nupkg.metadata") -Raw | ConvertFrom-Json
        if ($hash -cne $archiveHash -or ($package.Value.sha512 -and $package.Value.sha512 -cne $metadata.contentHash)) {
            throw "Package hash does not match NuGet restore metadata: $($package.Key)"
        }
        [xml]$nuspec = Get-Content -LiteralPath $nuspecFile[0].FullName -Raw
        $license = $nuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
        $licenseUrl = $nuspec.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='licenseUrl']")
        if ($null -eq $license -and $null -eq $licenseUrl) { throw "A package has no declared license: $($package.Key)" }
        $licenseValue = if ($license) { [string]$license.InnerText } else { [string]$licenseUrl.InnerText }
        $licenseKind = if ($license) { [string]$license.type } else { "url" }
        $copied = @()
        if ($licenseKind -eq "file") {
            $path = [IO.Path]::GetFullPath((Join-Path $directory $licenseValue))
            if (-not $path.StartsWith([IO.Path]::GetFullPath($directory) + [IO.Path]::DirectorySeparatorChar,
                    [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "A declared package license file is invalid: $($package.Key)"
            }
            $destination = "$id.$version.LICENSE.txt"
            Copy-Item -LiteralPath $path -Destination (Join-Path $licenseDirectory $destination) -Force
            $copied += $destination
        }
        foreach ($notice in Get-ChildItem -LiteralPath $directory -File | Where-Object { $_.Name -match '^(LICENSE|LICENCE|COPYING|COPYRIGHT|NOTICE|THIRD[-_]PARTY[-_]NOTICES)(\..*)?$' }) {
            $destination = "$id.$version.$($notice.Name)"
            Copy-Item -LiteralPath $notice.FullName -Destination (Join-Path $licenseDirectory $destination) -Force
            $copied += $destination
        }
        $licenseSource = $null
        if ($licenseKind -eq "expression" -and -not ($copied -match '\.(LICENSE|LICENCE|COPYING)(\.|$)')) {
            $fallback = @($licenseSources | Where-Object { $id -like $_.package -and $version -eq $_.version })
            if ($fallback.Count -ne 1) { throw "Provide an exact upstream license source for $($package.Key)." }
            $licenseSource = $fallback[0].url
            $destination = "$id.$version.LICENSE.txt"
            if ($fallback[0].repositoryFile) {
                Copy-Item -LiteralPath (Join-Path $repositoryRoot $fallback[0].repositoryFile) -Destination (Join-Path $licenseDirectory $destination) -Force
            } else {
                Invoke-WebRequest -Uri $licenseSource -OutFile (Join-Path $licenseDirectory $destination)
            }
            $copied += $destination
        }
        $packageSource = "https://www.nuget.org/packages/$id/$version"
        if ($id -ceq $sdkPin.packageId) {
            if ($version -cne $sdkPin.version) { throw "The restored SDK version differs from the fixed release." }
            $packageSource = "https://github.com/MirrorPulse/adapter-template/releases/tag/$($sdkPin.tag)"
        }
        $components[$package.Key] = [ordered]@{
            id=$id;version=$version;kind=$package.Value.kind;sha512=$hash;nugetContentHash=$metadata.contentHash
            licenseType=$licenseKind;license=$licenseValue;licenseTextSource=$licenseSource;noticeFiles=@($copied | Sort-Object -Unique)
            source=$packageSource
        }
    }
}
$inventory = @($components.Keys | Sort-Object | ForEach-Object { $components[$_] })
[ordered]@{schemaVersion=1;scope="restored production project graphs and SDK runtime downloads; reference packs are build inputs";packages=$inventory} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory "inventory.json") -Encoding utf8
$bomComponents = @(
    foreach ($package in $inventory) {
        $license = if ($package.licenseType -eq "expression") { @{expression=$package.license} }
            else { @{license=@{name="NuGet declared $($package.licenseType) license: $($package.license)";url=$package.source}} }
        [ordered]@{
            type="library";name=$package.id;version=$package.version
            purl="pkg:nuget/$($package.id)@$($package.version)"
            licenses=@($license)
            hashes=@(@{alg="SHA-512";content=([Convert]::ToHexString([Convert]::FromBase64String($package.sha512))).ToLowerInvariant()})
        }
    }
)
[ordered]@{bomFormat="CycloneDX";specVersion="1.6";version=1;components=$bomComponents} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $OutputDirectory "bom.cdx.json") -Encoding utf8
$noticeLines = @(
    "# Dependency notices", "", "Generated from restored production project graphs and SDK runtime downloads.",
    "This inventory includes build tools; it does not assert that every listed package is shipped.",
    "Exact license and NOTICE texts are preserved in the licenses directory.", "",
    "| Package | Version | Declared license | Texts |", "| --- | --- | --- | --- |"
)
foreach ($package in $inventory) {
    $texts = @($package.noticeFiles | ForEach-Object { "[$_](licenses/$_)" }) -join ", "
    $noticeLines += "| [$($package.id)]($($package.source)) | $($package.version) | $($package.license.Replace('|', '\|')) | $texts |"
}
$noticeLines | Set-Content -LiteralPath (Join-Path $OutputDirectory "NOTICE.md") -Encoding utf8
Write-Output "Collected $($inventory.Count) declared package licenses and a CycloneDX dependency baseline."
