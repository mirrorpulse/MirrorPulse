# The CLI's self-contained runtime is shipped at the package root. The App must
# use that same deployment model; an app-local hostfxr takes priority over global
# .NET locations even when the App runtimeconfig requests a shared framework.
function Assert-InstalledAppRuntimeLayout {
    param([Parameter(Mandatory)][System.IO.Compression.ZipArchive]$Archive)
    foreach($name in @('hostfxr.dll','hostpolicy.dll','coreclr.dll','System.Private.CoreLib.dll','MirrorPulse.App.runtimeconfig.json')) {
        $entries=@($Archive.Entries|Where-Object {$_.FullName.Equals($name,[StringComparison]::OrdinalIgnoreCase)})
        if ($entries.Count -ne 1 -or $entries[0].Length -le 0) { throw "The installed App requires one nonempty root-level $name payload." }
        if ($name -cne 'MirrorPulse.App.runtimeconfig.json') { continue }
        if ($entries[0].Length -gt 65536) { throw 'The App runtime configuration exceeds its verification bound.' }
        $stream=$entries[0].Open()
        try {
            $buffer=[byte[]]::new(65537);$count=0
            while ($count -lt $buffer.Length) {
                $read=$stream.Read($buffer,$count,$buffer.Length-$count)
                if ($read -eq 0) { break }
                $count+=$read
            }
            if ($count -gt 65536) { throw 'The App runtime configuration exceeds its verification bound.' }
            $configuration=([Text.UTF8Encoding]::new($false,$true).GetString($buffer,0,$count))|ConvertFrom-Json -Depth 16
        } finally { $stream.Dispose() }
    }
    $options=$configuration.runtimeOptions
    $frameworks=@($options.includedFrameworks)
    $core=@($frameworks|Where-Object {$_.name -ceq 'Microsoft.NETCore.App'})
    [version]$version=$null
    if ($null -eq $options -or $null -ne $options.framework -or $null -ne $options.frameworks -or
        $frameworks.Count -gt 4 -or $core.Count -ne 1 -or
        -not [version]::TryParse([string]$core[0].version,[ref]$version) -or $version.Major -lt 1) {
        throw 'The App must carry a self-contained runtime configuration beside the CLI runtime, without shared-framework requests.'
    }
}
