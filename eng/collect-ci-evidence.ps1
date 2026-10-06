[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet("build-and-test", "official-package-arm64", "official-adapters")][string]$Job,
    [Parameter(Mandatory)][ValidateSet("win-x64", "win-arm64")][string]$Runtime,
    [Parameter(Mandatory)][string]$OutputPath,
    [Parameter(Mandatory)][string[]]$TestManifests,
    [string]$PublishDirectory,
    [string]$AdapterDirectory,
    [string]$AdapterReleaseLockPath,
    [string]$IntegrationPath,
    [string]$InstalledPath,
    [string]$RejectedPath,
    [switch]$RequireNative,
    [switch]$RequireInstalled
)

$ErrorActionPreference = "Stop"
$sourceSha = (& git -C (Join-Path $PSScriptRoot "..") rev-parse HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceSha -notmatch '^[0-9a-f]{40}$') { throw "The source commit is unavailable." }
if ($env:GITHUB_SHA -and $sourceSha -cne $env:GITHUB_SHA) { throw "Evidence must describe the checked-out GitHub commit." }
if ($env:GITHUB_SHA) {
    & git -C (Join-Path $PSScriptRoot "..") diff --quiet HEAD --
    if ($LASTEXITCODE -ne 0) { throw "CI evidence cannot describe modified tracked source files." }
}
$suites = @()
foreach ($path in $TestManifests) {
    $test = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($test.schemaVersion -ne 1 -or $test.suite -notin @("managed", "native", "official", "signed", "packaged") -or
        $test.executed -le 0 -or $test.skipped -lt 0 -or $test.selected -ne ($test.executed + $test.skipped)) {
        throw "A test evidence manifest has invalid execution counts."
    }
    $categories = @($test.categories | ForEach-Object {
        if ($_.category -notin @("managed", "native", "official", "signed", "packaged") -or
            $_.executed -lt 0 -or $_.skipped -lt 0 -or $_.selected -ne ($_.executed + $_.skipped)) {
            throw "A test category has invalid execution counts."
        }
        [pscustomobject][ordered]@{category=$_.category;selected=[int]$_.selected;executed=[int]$_.executed;skipped=[int]$_.skipped}
    })
    if (($categories | Measure-Object selected -Sum).Sum -ne $test.selected -or
        ($categories | Measure-Object executed -Sum).Sum -ne $test.executed) { throw "Test category totals do not match." }
    $suites += [pscustomobject][ordered]@{suite=$test.suite;selected=[int]$test.selected;executed=[int]$test.executed;skipped=[int]$test.skipped;categories=$categories}
}
if (@($suites.suite | Select-Object -Unique).Count -ne $suites.Count) { throw "Duplicate test suites cannot be counted twice." }
$requiredSuite = switch ($Job) { "build-and-test" { "managed" }; "official-package-arm64" { "signed" }; "official-adapters" { "official" } }
if ($suites.suite -notcontains $requiredSuite) { throw "The job's required test suite is missing." }
if ($RequireNative) {
    $suiteCatalog = Get-Content -LiteralPath (Join-Path $PSScriptRoot "test-suites.json") -Raw | ConvertFrom-Json
    $requiredNativeCount = @($suiteCatalog.required.native).Count
    $native = @($suites | Where-Object suite -eq "native")
    if ($requiredNativeCount -le 0 -or $native.Count -ne 1 -or $native[0].skipped -ne 0 -or
        @($native[0].categories | Where-Object { $_.category -eq "native" -and $_.executed -eq $requiredNativeCount }).Count -ne 1) {
        throw "The dedicated native suite did not execute all required native tests."
    }
}
$artifacts = @()
function Add-Artifact([string]$Root, [string]$RelativePath, [string]$Prefix, [string]$ExpectedHash, [long]$ExpectedLength) {
    if ($RelativePath -notmatch '^[A-Za-z0-9._/-]+$' -or $RelativePath.StartsWith('/') -or
        @($RelativePath.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
        throw "An artifact path is not a safe relative path."
    }
    $file = Get-Item -LiteralPath (Join-Path $Root $RelativePath) -ErrorAction Stop
    $hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($file.PSIsContainer -or $file.Length -ne $ExpectedLength -or $hash -cne $ExpectedHash) {
        throw "A published artifact no longer matches its manifest."
    }
    [ordered]@{path="$Prefix/$RelativePath";length=$file.Length;sha256=$hash}
}
if ($PublishDirectory) {
    $publish = Get-Content -LiteralPath (Join-Path $PublishDirectory "publish-manifest.json") -Raw | ConvertFrom-Json
    if ($publish.schemaVersion -ne 1 -or $publish.runtime -ne $Runtime -or $publish.files.Count -eq 0) { throw "Invalid publish manifest." }
    foreach ($file in $publish.files) { $artifacts += Add-Artifact $PublishDirectory $file.path "cli-host" $file.sha256 $file.length }
    if ($publish.files.path -notcontains "mp.exe" -or $publish.files.path -notcontains "host/MirrorPulse.Host.exe") { throw "CLI or Host payload is missing." }
}
if ($AdapterDirectory) {
    $aggregate = @(Get-Content -LiteralPath (Join-Path $AdapterDirectory "official-adapters.manifest.json") -Raw | ConvertFrom-Json)
    if ($aggregate.Count -ne 5) { throw "The official aggregate must contain five packages." }
    foreach ($adapter in $aggregate) {
        $relative = "$($adapter.adapterId)/$($adapter.package)"
        $artifacts += Add-Artifact $AdapterDirectory $relative "official-adapters" $adapter.packageSha256 $adapter.packageLength
    }
}
$candidateHash = $null
if ($AdapterReleaseLockPath) {
    . (Join-Path $PSScriptRoot 'official-adapter-release-policy.ps1')
    $null = Read-OfficialAdapterReleaseCandidate -Path $AdapterReleaseLockPath -SourceSha $sourceSha
    $candidateFile = Get-Item -LiteralPath $AdapterReleaseLockPath
    $candidateHash = (Get-FileHash -LiteralPath $candidateFile.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    $artifacts += Add-Artifact $candidateFile.Directory.FullName $candidateFile.Name 'official-candidate' $candidateHash $candidateFile.Length
}
if ($artifacts.Count -eq 0) { throw "No artifact hashes were verified." }
$checks = @()
if ($IntegrationPath) {
    $integration = Get-Content -LiteralPath $IntegrationPath -Raw | ConvertFrom-Json
    if ($integration.schemaVersion -ne 1 -or $integration.runtime -ne $Runtime -or $integration.regression -ne $true -or
        $integration.rangeRead -ne $true -or $integration.offlineQueueRetained -ne $true -or
        $integration.uploadJournalDrained -ne $true -or $integration.conflictPersisted -ne $true -or $integration.cursorPersisted -ne $true) {
        throw "The CLI regression verification is incomplete."
    }
    $checks += [ordered]@{name="signed-local-cli-regression";executed=$true;rangeRead=$true;offlineQueueRetained=$true;uploadJournalDrained=$true;conflictPersisted=$true;cursorPersisted=$true}
} elseif ($Job -eq "official-package-arm64") { throw "The signed Local CLI regression evidence is missing." }
if ($InstalledPath) {
    $installed = Get-Content -LiteralPath $InstalledPath -Raw | ConvertFrom-Json
    if ($installed.schemaVersion -ne 1 -or $installed.runtime -ne $Runtime -or $installed.installedIdentity -ne $true -or
        $installed.cliAlias -ne $true -or $installed.hostAutoStart -ne $true -or $installed.uninstalled -ne $true -or
        $installed.cloudFilesExtension -ne $true -or $installed.adapterAssociation -ne $true -or
        $installed.packageSha256 -notmatch '^[0-9a-f]{64}$' -or $installed.osProductType -ne 1 -or
        $installed.osVersion -notmatch '^10\.0\.\d+\.\d+$' -or
        [version]$installed.osVersion -lt [version]'10.0.26100.0' -or $installed.minimumVersion -ne '10.0.26100.0') {
        throw "The installed MSIX evidence is incomplete or is not from a supported desktop build."
    }
    $checks += [ordered]@{name="installed-msix";executed=$true;packageSha256=$installed.packageSha256;installedIdentity=$true;cliAlias=$true;hostAutoStart=$true;uninstalled=$true;cloudFilesExtension=$true;adapterAssociation=$true;shellRegistration=($installed.shellRegistration -eq $true);osVersion=$installed.osVersion;osProductType=1;minimumVersion=$installed.minimumVersion}
} elseif ($RequireInstalled) { throw "The installed MSIX evidence is missing." }
if ($RejectedPath) {
    $rejected = Get-Content -LiteralPath $RejectedPath -Raw | ConvertFrom-Json
    if ($rejected.schemaVersion -ne 1 -or $rejected.runtime -ne $Runtime -or $rejected.osProductType -notin @(2,3) -or
        $rejected.osVersion -notmatch '^10\.0\.\d+\.\d+$' -or $rejected.cliRejected -ne $true -or
        $rejected.hostRejected -ne $true -or $rejected.stateUntouched -ne $true) { throw "Windows Server rejection evidence is incomplete." }
    $checks += [ordered]@{name="unsupported-server";executed=$true;osVersion=$rejected.osVersion;osProductType=[int]$rejected.osProductType;cliRejected=$true;hostRejected=$true;stateUntouched=$true}
} elseif ($Job -eq 'build-and-test' -and $env:GITHUB_SHA) { throw "Windows Server rejection evidence is required in CI." }
[ordered]@{
    schemaVersion=1;sourceSha=$sourceSha;job=$Job;runtime=$Runtime
    officialAdapterCandidateSha256=$candidateHash
    tests=$suites;checks=$checks;artifacts=$artifacts
} | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Output "Saved verified $Job evidence for $sourceSha ($Runtime)."
