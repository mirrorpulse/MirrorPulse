[CmdletBinding()]
param([string]$RepositoryRoot = (Join-Path $PSScriptRoot ".."))

$ErrorActionPreference = "Stop"
$RepositoryRoot = (Resolve-Path -LiteralPath $RepositoryRoot).Path
$exceptionsPath = Join-Path $RepositoryRoot "eng/architecture-exceptions.json"
$exceptions = Get-Content -LiteralPath $exceptionsPath -Raw | ConvertFrom-Json
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenReferences = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$sourcePattern = 'MirrorPulse\.Core\.State|MirrorPulseProductCatalog|Microsoft\.Data\.Sqlite|\bSqliteConnection\b|\bCloudFileSystem\b|MirrorPulse\.CloudFiles\.CfSharp|\busing\s+CfSharp\b|\bICloudStateStore\b|"product\.db"'
$allowedReferences = @{
    "MirrorPulse.Cli" = @("MirrorPulse.Control")
    "MirrorPulse.Control" = @("MirrorPulse.Core")
    "MirrorPulse.App" = @("MirrorPulse.Control", "MirrorPulse.Core", "MirrorPulse.CloudFiles.CfSharp")
}
foreach ($client in @("MirrorPulse.Cli", "MirrorPulse.Control", "MirrorPulse.App")) {
    $directory = Join-Path $RepositoryRoot "src/$client"
    [xml]$project = Get-Content -LiteralPath (Join-Path $directory "$client.csproj") -Raw
    foreach ($reference in $project.SelectNodes("//ProjectReference")) {
        $name = [IO.Path]::GetFileNameWithoutExtension($reference.Include.Replace('\', '/'))
        if ($allowedReferences[$client] -notcontains $name) {
            throw "$client has an unapproved project reference: $name"
        }
        $resolved = [IO.Path]::GetFullPath((Join-Path $directory $reference.Include))
        $expected = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot "src/$name/$name.csproj"))
        if (-not [string]::Equals($resolved, $expected, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$client references a project outside its approved source location."
        }
        if ($name -ne "MirrorPulse.Control") {
            $record = @($exceptions.projectExceptions | Where-Object { $_.project -ceq $client -and $_.reference -ceq $name })
            if ($record.Count -ne 1 -or [string]::IsNullOrWhiteSpace($record[0].reason)) {
                throw "$client has an undocumented legacy reference: $name"
            }
            [void]$seenReferences.Add("$client|$name")
        }
    }
    foreach ($reference in $project.SelectNodes("//PackageReference")) {
        if ($reference.Include -match '^(CfSharp(?:\.|$)|Microsoft\.Data\.Sqlite(?:\.|$)|SQLitePCLRaw(?:\.|$))') {
            throw "$client directly references a Host-owned storage package."
        }
    }
    foreach ($compile in $project.SelectNodes("//Compile[@Include]")) {
        $path = [IO.Path]::GetFullPath((Join-Path $directory $compile.Include))
        if (-not $path.StartsWith($directory + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "$client links source outside its reviewed project boundary."
        }
    }
    foreach ($file in Get-ChildItem -LiteralPath $directory -Filter "*.cs" -File -Recurse) {
        $relative = [IO.Path]::GetRelativePath($RepositoryRoot, $file.FullName).Replace('\', '/')
        if ($relative -match '/(?:bin|obj)/') { continue }
        foreach ($line in Get-Content -LiteralPath $file.FullName) {
            if ($line -notmatch $sourcePattern) { continue }
            $trimmed = $line.Trim()
            $match = @($exceptions.sourceLines | Where-Object { $_.path -ceq $relative -and $_.line -ceq $trimmed })
            if ($match.Count -ne 1 -or $client -ne "MirrorPulse.App") {
                throw "Client source accesses a Host-owned boundary: $relative"
            }
            if (-not $seen.Add("$relative|$trimmed")) {
                throw "A legacy source exception was duplicated: $relative"
            }
        }
    }
}
foreach ($exception in $exceptions.projectExceptions) {
    if (-not $seenReferences.Contains("$($exception.project)|$($exception.reference)")) {
        throw "A legacy project-reference exception is stale: $($exception.project)"
    }
}
foreach ($exception in $exceptions.sourceLines) {
    if ([string]::IsNullOrWhiteSpace($exception.reason) -or -not $seen.Contains("$($exception.path)|$($exception.line)")) {
        throw "An architecture exception is stale or has no explanation: $($exception.path)"
    }
}
Write-Output "Client ownership gate passed; $($seen.Count) explicitly recorded legacy App source lines remain."
