namespace MirrorPulse.Core.Tests;

public sealed partial class InstalledStartupDiagnosticsTests
{
    [TestMethod]
    [DataRow("valid")]
    [DataRow("framework")]
    [DataRow("frameworks")]
    [DataRow("missing-runtime")]
    [DataRow("missing-config")]
    [DataRow("oversized")]
    [DataRow("invalid")]
    [DataRow("duplicate-config")]
    [DataRow("invalid-version")]
    [DataRow("missing-included")]
    public async Task InstalledPackageRejectsMixedRuntimeModelsBeforeInstallation(string scenario)
    {
        await RunSharedShellOutputProbeAsync(scenario, """
            . (Join-Path (Split-Path -Parent $Policy) 'installed-runtime-layout.ps1')
            $memory=[IO.MemoryStream]::new()
            try {
                $zip=[IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Create,$true)
                try {
                    foreach($name in @('hostfxr.dll','hostpolicy.dll','coreclr.dll','System.Private.CoreLib.dll')) {
                        if ($Scenario -ceq 'missing-runtime' -and $name -ceq 'coreclr.dll') { continue }
                        $writer=[IO.StreamWriter]::new($zip.CreateEntry($name).Open())
                        try {$writer.Write('synthetic archive entry')} finally {$writer.Dispose()}
                    }
                    $options=@{includedFrameworks=@(@{name='Microsoft.NETCore.App';version='10.0.0'})}
                    switch($Scenario) {
                        'framework' {$options.framework=@{name='Microsoft.NETCore.App';version='10.0.0'}}
                        'frameworks' {$options.frameworks=@(@{name='Microsoft.NETCore.App';version='10.0.0'})}
                        'invalid-version' {$options.includedFrameworks[0].version='invalid'}
                        'missing-included' {$options.includedFrameworks=@()}
                    }
                    if ($Scenario -cne 'missing-config') {
                        $text=@{runtimeOptions=$options}|ConvertTo-Json -Depth 6
                        if ($Scenario -ceq 'oversized') {$text=' '*65537}
                        if ($Scenario -ceq 'invalid') {$text='{invalid'}
                        $names=@('MirrorPulse.App.runtimeconfig.json')
                        if ($Scenario -ceq 'duplicate-config') {$names+= 'mirrorpulse.app.runtimeconfig.json'}
                        foreach($name in $names) {
                            $writer=[IO.StreamWriter]::new($zip.CreateEntry($name).Open())
                            try {$writer.Write($text)} finally {$writer.Dispose()}
                        }
                    }
                } finally {$zip.Dispose()}
                $memory.Position=0
                $archive=[IO.Compression.ZipArchive]::new($memory,[IO.Compression.ZipArchiveMode]::Read,$true)
                try {
                    $entryCount=$archive.Entries.Count;$rejected=$false
                    try {Assert-InstalledAppRuntimeLayout -Archive $archive} catch {$rejected=$true}
                    if (($Scenario -ceq 'valid') -eq $rejected -or $archive.Entries.Count -ne $entryCount) {
                        throw 'Only a complete self-contained runtime layout may pass the read-only pre-installation boundary.'
                    }
                } finally {$archive.Dispose()}
                if (Test-Path -LiteralPath $resultPath) {throw 'Archive validation cannot invent installed Shell success.'}
            } finally {$memory.Dispose()}
            """);
    }
}
