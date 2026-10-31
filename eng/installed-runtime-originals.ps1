$script:InstalledRuntimeOriginalsCollectorPath = $PSCommandPath

function Get-InstalledOriginalPath([string]$Path, [string]$Root) {
    $boundary = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    $full = [IO.Path]::GetFullPath($Path, $boundary)
    if (-not $full.StartsWith($boundary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Installed originals must remain inside their declared root.'
    }
    $current = $full
    while ($current.Length -ge $boundary.Length) {
        if ((Test-Path -LiteralPath $current) -and
            ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Installed originals cannot traverse a reparse point.'
        }
        $current = [IO.Path]::GetDirectoryName($current)
    }
    return $full
}

function Get-InstalledRuntimeEntries([IO.Compression.ZipArchive]$Archive) {
    $entries = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $Archive.Entries) {
        $path = $entry.FullName.TrimEnd('/')
        if ($path.Length -eq 0 -or $path.Length -gt 1024 -or $path.Contains('\') -or $path.Contains(':') -or
            $path.StartsWith('/') -or @($path.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -or $entries.ContainsKey($path) -or
            [IO.Path]::GetExtension($path) -iin @('.pfx', '.p12', '.key', '.snk')) {
            throw 'The MSIX archive contains an ambiguous or unsafe entry.'
        }
        $entries.Add($path, $entry)
    }
    $required = @('MirrorPulse.App.exe', 'MirrorPulse.App.dll', 'MirrorPulse.App.deps.json', 'MirrorPulse.App.runtimeconfig.json',
        'mp.exe', 'mp.dll', 'mp.deps.json', 'mp.runtimeconfig.json',
        'host/MirrorPulse.Host.exe', 'host/MirrorPulse.Host.dll', 'host/MirrorPulse.Host.deps.json', 'host/MirrorPulse.Host.runtimeconfig.json')
    foreach ($prefix in @('', 'host/')) {
        $required += @('MirrorPulse.Core.dll', 'MirrorPulse.CloudFiles.CfSharp.dll', 'MirrorPulse.Adapter.Sdk.dll',
            'CfSharp.dll', 'CfSharp.Native.dll', 'CfSharp.Storage.Sqlite.dll') | ForEach-Object { $prefix + $_ }
    }
    foreach ($path in $required) {
        if (-not $entries.ContainsKey($path) -or $entries[$path].FullName -cne $path -or $entries[$path].Length -le 0) {
            throw 'The MSIX is missing an exact required App, CLI or Host runtime input.'
        }
    }
    $names = @(@($required) + @($entries.Keys | Where-Object { $_ -cmatch '^(host/)?MirrorPulse\.[A-Za-z0-9.]+\.dll$' }) |
        Sort-Object -CaseSensitive -Unique)
    if ($names.Count -gt 64) { throw 'The installed product input inventory exceeds its bound.' }
    return @($names | ForEach-Object { $entries[$_] })
}

# Read PE metadata without loading or executing any installed assembly.
function Get-InstalledInputMetadata([string]$Path, [string]$Runtime, [string]$SourceSha) {
    $stream = [IO.File]::OpenRead($Path)
    try {
        $reader = [Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            $machine = $reader.PEHeaders.CoffHeader.Machine.ToString()
            $expected = if ($Runtime -ceq 'win-arm64') { 'Arm64' } else { 'Amd64' }
            if ([IO.Path]::GetExtension($Path) -ceq '.exe') {
                if ($null -ne $reader.PEHeaders.CorHeader -or $machine -cne $expected) {
                    throw 'An installed apphost has the wrong native architecture or deployment shape.'
                }
                return [ordered]@{ machine = $machine; managed = $false }
            }
            if (-not $reader.HasMetadata -or $null -eq $reader.PEHeaders.CorHeader) { throw 'A managed installed input lacks CLR metadata.' }
            $flags = $reader.PEHeaders.CorHeader.Flags
            $anyCpu = $machine -ceq 'I386' -and ($flags -band [Reflection.PortableExecutable.CorFlags]::ILOnly) -ne 0 -and
                ($flags -band [Reflection.PortableExecutable.CorFlags]::Requires32Bit) -eq 0
            if (-not $anyCpu -and $machine -cne $expected) { throw 'A managed installed input targets another architecture.' }
            $metadata = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($reader)
            $definition = $metadata.GetAssemblyDefinition()
            $assemblyName = $metadata.GetString($definition.Name)
            if ($assemblyName -cne [IO.Path]::GetFileNameWithoutExtension($Path)) { throw 'An installed managed input was renamed from another assembly.' }
            $versions = @(foreach ($attributeHandle in $definition.GetCustomAttributes()) {
                $attribute = $metadata.GetCustomAttribute($attributeHandle)
                if ($attribute.Constructor.Kind.ToString() -cne 'MemberReference') { continue }
                $row = [Reflection.Metadata.Ecma335.MetadataTokens]::GetToken($attribute.Constructor) -band 0xffffff
                $constructor = $metadata.GetMemberReference([Reflection.Metadata.Ecma335.MetadataTokens]::MemberReferenceHandle($row))
                if ($constructor.Parent.Kind.ToString() -cne 'TypeReference') { continue }
                $typeRow = [Reflection.Metadata.Ecma335.MetadataTokens]::GetToken($constructor.Parent) -band 0xffffff
                $type = $metadata.GetTypeReference([Reflection.Metadata.Ecma335.MetadataTokens]::TypeReferenceHandle($typeRow))
                if ($metadata.GetString($type.Namespace) -cne 'System.Reflection' -or
                    $metadata.GetString($type.Name) -cne 'AssemblyInformationalVersionAttribute') { continue }
                $blob = $metadata.GetBlobReader($attribute.Value)
                if ($blob.ReadUInt16() -ne 1) { throw 'Invalid installed source-version metadata.' }
                $blob.ReadSerializedString()
            })
            if ($versions.Count -ne 1 -or [string]::IsNullOrEmpty($versions[0])) { throw 'An installed input lacks one unambiguous source version.' }
            $version = $versions[0]
            if ($assemblyName -ceq 'MirrorPulse.Adapter.Sdk') {
                $valid = $version -ceq '0.2.0+1008894b495bb57fe8661a6106dcf8954fbe037d'
            } elseif ($assemblyName -clike 'CfSharp*') {
                $valid = $version -ceq '0.1.0-preview.8+ee593578dba3c666cf59f4a922f0e220f115bf7f'
            } else {
                $valid = $version -cmatch ('\+' + $SourceSha + '$')
            }
            if (-not $valid) { throw 'An installed product or pinned library embeds another source revision.' }
            return [ordered]@{ machine = $machine; managed = $true; identity = [Reflection.AssemblyName]::GetAssemblyName($Path).FullName
                informationalVersion = $version }
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Copy-InstalledRuntimeOriginal([string]$Path, [string]$SourceRoot, [string]$Destination, [string]$Workspace, [string]$OutputDirectory, [long]$MaximumLength = 128MB) {
    $source = Get-InstalledOriginalPath $Path $SourceRoot
    $target = Get-InstalledOriginalPath $Destination $Workspace
    $file = Get-Item -LiteralPath $source -Force
    if ($file.PSIsContainer -or $file.Length -le 0 -or $file.Length -gt $MaximumLength -or (Test-Path -LiteralPath $target)) {
        throw 'Installed originals require bounded regular files and fresh destinations.'
    }
    $hash = (Get-FileHash -LiteralPath $source).Hash
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($target)) -Force | Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    if ((Get-FileHash -LiteralPath $source).Hash -cne $hash -or (Get-FileHash -LiteralPath $target).Hash -cne $hash) {
        throw 'An installed original changed while being retained.'
    }
    return [ordered]@{ path = $source; archivePath = [IO.Path]::GetRelativePath($OutputDirectory, $target).Replace('\', '/')
        length = $file.Length; sha256 = $hash }
}

function Start-InstalledRuntimeOriginals {
    param([Parameter(Mandatory)][string]$Workspace, [Parameter(Mandatory)][string]$PackagePath,
        [Parameter(Mandatory)][string]$InstallLocation, [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64')][string]$Runtime,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha)
    $output = Get-InstalledOriginalPath $OutputDirectory $Workspace
    if ((Test-Path -LiteralPath $output) -or [IO.Path]::GetExtension($PackagePath) -cne '.msix') { throw 'Preserve original installed input evidence; a fresh exact MSIX is required.' }
    $packageSourceRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PackagePath))
    $packageSource = Get-InstalledOriginalPath $PackagePath $packageSourceRoot
    if ((Get-Item -LiteralPath $packageSource).Length -gt 512MB) { throw 'The original MSIX exceeds its retention bound.' }
    # The read lease excludes replacement/writes through preflight and copying. Retain only
    # the selected MSIX after rejecting key entries, never the signing directory or PFX.
    $archive = [IO.Compression.ZipFile]::OpenRead($packageSource)
    try {
        $runtimeEntries = @(Get-InstalledRuntimeEntries $archive)
        New-Item -ItemType Directory -Path $output | Out-Null
        $package = Copy-InstalledRuntimeOriginal $PackagePath $packageSourceRoot (Join-Path $output 'package.msix') $Workspace $output 512MB
        $inputs = @(foreach ($entry in $runtimeEntries) {
            if ($entry.Length -gt 128MB) { throw 'A packaged runtime input exceeds its bound.' }
            $record = Copy-InstalledRuntimeOriginal (Join-Path $InstallLocation $entry.FullName) $InstallLocation (Join-Path $output ('inputs/' + $entry.FullName)) $Workspace $output
            $stream = $entry.Open()
            try { $packedHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ($record.sha256 -cne $packedHash -or $record.length -ne $entry.Length) { throw 'An actual installed input differs from its exact MSIX member.' }
            $record.relativePath = $entry.FullName
            if ([IO.Path]::GetExtension($entry.FullName) -cin @('.dll', '.exe')) {
                $record.metadata = Get-InstalledInputMetadata (Join-Path $output $record.archivePath) $Runtime $SourceSha
            }
            $record
        })
    } finally { $archive.Dispose() }
    $collector = Copy-InstalledRuntimeOriginal $script:InstalledRuntimeOriginalsCollectorPath $Workspace (Join-Path $output 'policy/installed-runtime-originals.ps1') $Workspace $output
    $prepared = Join-Path $output 'prepared.json'
    [ordered]@{ schemaVersion = 1; sourceSha = $SourceSha; runtime = $Runtime; installLocation = [IO.Path]::GetFullPath($InstallLocation)
        outputDirectory = $output; package = $package; inputs = $inputs; collector = $collector
        preparedUtc = [DateTimeOffset]::UtcNow.ToString('o'); productExecutionRequestedByCollector = $false } |
        ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $prepared -Encoding utf8
    return [pscustomobject]@{ outputDirectory = $output; preparedSha256 = (Get-FileHash -LiteralPath $prepared).Hash }
}

function Complete-InstalledRuntimeOriginals {
    param([Parameter(Mandatory)][string]$Workspace, [Parameter(Mandatory)][string]$OutputDirectory,
        [Parameter(Mandatory)][string]$InstallLocation, [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$SourceSha,
        [Parameter(Mandatory)][ValidatePattern('^[A-F0-9]{64}$')][string]$PreparedSha256)
    $output = Get-InstalledOriginalPath $OutputDirectory $Workspace
    $path = Join-Path $output 'prepared.json'
    if ((Get-FileHash -LiteralPath $path).Hash -cne $PreparedSha256 -or (Test-Path -LiteralPath (Join-Path $output 'collected.json'))) { throw 'Require the original prepared input receipt and a fresh completion.' }
    $prepared = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -DateKind String
    if ($prepared.schemaVersion -ne 1 -or $prepared.sourceSha -cne $SourceSha -or $prepared.outputDirectory -ine $output -or
        $prepared.installLocation -ine [IO.Path]::GetFullPath($InstallLocation) -or $prepared.productExecutionRequestedByCollector -ne $false) { throw 'The installed source, root or original preparation was substituted.' }
    foreach ($binding in @($prepared.package, $prepared.collector) + @($prepared.inputs)) {
        $sourceRoot = if ($binding -eq $prepared.package) { [IO.Path]::GetDirectoryName($prepared.package.path) } elseif ($binding -eq $prepared.collector) { $Workspace } else { $InstallLocation }
        $source = Get-InstalledOriginalPath $binding.path $sourceRoot
        $retained = Get-InstalledOriginalPath (Join-Path $output $binding.archivePath) $Workspace
        if ((Get-FileHash -LiteralPath $source).Hash -cne $binding.sha256 -or (Get-FileHash -LiteralPath $retained).Hash -cne $binding.sha256 -or
            (Get-Item -LiteralPath $source).Length -ne $binding.length -or (Get-Item -LiteralPath $retained).Length -ne $binding.length) { throw 'An actual installed input or retained original changed during execution.' }
    }
    [ordered]@{ schemaVersion = 1; sourceSha = $SourceSha; runtime = $prepared.runtime; preparedSha256 = $PreparedSha256
        packageSha256 = $prepared.package.sha256; inputCount = $prepared.inputs.Count; installedInputsMatchOriginalMsixMembers = $true
        actualInputBytesBeforeAndAfterUnchanged = $true; productExecutionRequestedByCollector = $false
        actualProcessImageBindingSeparatelyRequired = $true; fullP085Accepted = $false; fullS06Accepted = $false
        completedUtc = [DateTimeOffset]::UtcNow.ToString('o') } |
        ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $output 'collected.json') -Encoding utf8
    return [pscustomobject]@{ outputDirectory = $output; preparedSha256 = $PreparedSha256
        collectedSha256 = (Get-FileHash -LiteralPath (Join-Path $output 'collected.json')).Hash; inputCount = $prepared.inputs.Count }
}
