using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace MirrorPulse.Core.Tests;

public sealed partial class InstalledStartupDiagnosticsTests
{
    [TestMethod]
    [DoNotParallelize] // Real PE/ZIP fixtures and child PowerShell must keep their existing request budget.
    [DataRow("complete")]
    [DataRow("foreign-source")]
    [DataRow("foreign-library")]
    [DataRow("renamed")]
    [DataRow("missing-version")]
    [DataRow("duplicate-version")]
    [DataRow("missing-member")]
    [DataRow("duplicate-member")]
    [DataRow("unsafe-member")]
    [DataRow("private-key-entry")]
    [DataRow("different-installed-bytes")]
    [DataRow("wrong-native-architecture")]
    [DataRow("changed-after")]
    [DataRow("changed-retained")]
    [DataRow("changed-package")]
    [DataRow("changed-prepared")]
    [DataRow("linked-package")]
    [DataRow("reused-output")]
    public async Task InstalledInputOriginalsBindCompletePackageAndActualFilesWithoutRunningProductCode(string scenario)
    {
        string prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "MirrorPulse-installed-diagnostics-tests"));
        string directory = Path.Combine(prefix, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            const string source = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
            string install = Path.Combine(directory, "installed"), packageDirectory = Path.Combine(directory, "package");
            Directory.CreateDirectory(install); Directory.CreateDirectory(packageDirectory);
            string collector = Path.Combine(directory, "eng", "installed-runtime-originals.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(collector)!);
            File.Copy(Path.Combine(Path.GetDirectoryName(Policy())!, "installed-runtime-originals.ps1"), collector);
            string[] paths = ["MirrorPulse.App.exe", "MirrorPulse.App.dll", "MirrorPulse.App.deps.json", "MirrorPulse.App.runtimeconfig.json",
                "mp.exe", "mp.dll", "mp.deps.json", "mp.runtimeconfig.json", "MirrorPulse.Control.Client.dll",
                "host/MirrorPulse.Host.exe", "host/MirrorPulse.Host.dll", "host/MirrorPulse.Host.deps.json", "host/MirrorPulse.Host.runtimeconfig.json",
                "MirrorPulse.Core.dll", "MirrorPulse.CloudFiles.CfSharp.dll", "MirrorPulse.Adapter.Sdk.dll", "CfSharp.dll", "CfSharp.Native.dll", "CfSharp.Storage.Sqlite.dll",
                "host/MirrorPulse.Core.dll", "host/MirrorPulse.CloudFiles.CfSharp.dll", "host/MirrorPulse.Adapter.Sdk.dll", "host/CfSharp.dll", "host/CfSharp.Native.dll", "host/CfSharp.Storage.Sqlite.dll"];
            foreach (string relative in paths)
            {
                string path = Path.Combine(install, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                string name = Path.GetFileName(relative);
                if (name.EndsWith(".exe", StringComparison.Ordinal))
                {
                    byte[] native = await File.ReadAllBytesAsync(Environment.ProcessPath!);
                    if (scenario == "wrong-native-architecture" && relative == "mp.exe")
                    {
                        int offset = BinaryPrimitives.ReadInt32LittleEndian(native.AsSpan(0x3c));
                        ushort other = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? (ushort)0x8664 : (ushort)0xaa64;
                        BinaryPrimitives.WriteUInt16LittleEndian(native.AsSpan(offset + 4), other);
                    }
                    await File.WriteAllBytesAsync(path, native);
                }
                else if (name.EndsWith(".dll", StringComparison.Ordinal))
                {
                    string assemblyName = Path.GetFileNameWithoutExtension(name);
                    string version = assemblyName == "MirrorPulse.Adapter.Sdk" ? "0.2.0+1008894b495bb57fe8661a6106dcf8954fbe037d" :
                        assemblyName.StartsWith("CfSharp", StringComparison.Ordinal) ? "0.1.0-preview.8+ee593578dba3c666cf59f4a922f0e220f115bf7f" : "1.0.0+" + source;
                    if (relative == "MirrorPulse.Core.dll" && scenario == "foreign-source") version = "1.0.0+" + new string('a', 40);
                    if (relative == "CfSharp.dll" && scenario == "foreign-library") version = "0.1.0-preview.7+" + new string('a', 40);
                    if (relative == "MirrorPulse.Core.dll" && scenario == "renamed") assemblyName = "Other.Core";
                    var builder = new PersistedAssemblyBuilder(new AssemblyName(assemblyName), typeof(object).Assembly);
                    builder.DefineDynamicModule(assemblyName).DefineType("Fixture", TypeAttributes.Public).CreateType();
                    if (relative != "MirrorPulse.Core.dll" || scenario != "missing-version")
                    {
                        var attribute = new CustomAttributeBuilder(typeof(AssemblyInformationalVersionAttribute).GetConstructor([typeof(string)])!, [version]);
                        builder.SetCustomAttribute(attribute);
                        if (relative == "MirrorPulse.Core.dll" && scenario == "duplicate-version") builder.SetCustomAttribute(attribute);
                    }
                    builder.Save(path);
                }
                else await File.WriteAllTextAsync(path, "{}");
            }
            string package = Path.Combine(packageDirectory, "fixture.msix");
            using (var zip = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                foreach (string relative in paths)
                {
                    if (scenario == "missing-member" && relative == "host/MirrorPulse.Host.deps.json") continue;
                    zip.CreateEntryFromFile(Path.Combine(install, relative), relative);
                }
                string? extra = scenario switch
                {
                    "duplicate-member" => "cfsharp.dll",
                    "unsafe-member" => "../escape",
                    "private-key-entry" => "PrivateSigning.pfx",
                    _ => null
                };
                if (extra is not null)
                {
                    await using StreamWriter writer = new(zip.CreateEntry(extra).Open());
                    await writer.WriteAsync("PRIVATE_TEST_SENTINEL");
                }
            }
            if (scenario == "different-installed-bytes") await File.AppendAllTextAsync(Path.Combine(install, "MirrorPulse.Core.dll"), "changed");
            // An adjacent signing file is deliberately outside the retained MSIX selection.
            await File.WriteAllTextAsync(Path.Combine(packageDirectory, "TestSigning.pfx"), "PRIVATE_TEST_SENTINEL");
            string runtime = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64";
            await File.WriteAllTextAsync(Path.Combine(directory, "input.json"), JsonSerializer.Serialize(new { source, runtime, package, install }));
            string script = Path.Combine(directory, "verify.ps1");
            await File.WriteAllTextAsync(script, """
                param([string]$Policy,[string]$Directory,[string]$Scenario)
                $ErrorActionPreference='Stop'
                . (Join-Path $Directory 'eng/installed-runtime-originals.ps1')
                $data=Get-Content -LiteralPath (Join-Path $Directory 'input.json') -Raw|ConvertFrom-Json
                $output=Join-Path $Directory 'originals';$link=$null;$failure=$null
                try {
                    try {
                        $package=$data.package
                        if($Scenario -ceq 'linked-package') {
                            $link=Join-Path $Directory 'linked-package'
                            New-Item -ItemType Junction -Path $link -Target (Split-Path -Parent $package)|Out-Null
                            $package=Join-Path $link 'fixture.msix'
                        }
                        $before=Start-InstalledRuntimeOriginals -Workspace $Directory -PackagePath $package -InstallLocation $data.install -OutputDirectory $output -Runtime $data.runtime -SourceSha $data.source
                        switch($Scenario) {
                            'changed-after' {[IO.File]::AppendAllText((Join-Path $data.install 'MirrorPulse.Core.dll'),'changed')}
                            'changed-retained' {[IO.File]::AppendAllText((Join-Path $output 'inputs/MirrorPulse.Core.dll'),'changed')}
                            'changed-package' {[IO.File]::AppendAllText($data.package,'changed')}
                            'changed-prepared' {[IO.File]::AppendAllText((Join-Path $output 'prepared.json'),' ')}
                            'reused-output' {$null=Start-InstalledRuntimeOriginals -Workspace $Directory -PackagePath $package -InstallLocation $data.install -OutputDirectory $output -Runtime $data.runtime -SourceSha $data.source}
                        }
                        $after=Complete-InstalledRuntimeOriginals -Workspace $Directory -SourceSha $data.source -InstallLocation $data.install -OutputDirectory $output -PreparedSha256 $before.preparedSha256
                    } catch {$failure=$_.Exception.Message}
                    if($Scenario -ceq 'complete') {
                        if($null -ne $failure) {throw $failure}
                        $prepared=Get-Content -LiteralPath (Join-Path $output 'prepared.json') -Raw|ConvertFrom-Json -DateKind String
                        $collected=Get-Content -LiteralPath (Join-Path $output 'collected.json') -Raw|ConvertFrom-Json -DateKind String
                        if($after.inputCount -ne 25 -or $prepared.inputs.Count -ne 25 -or $prepared.productExecutionRequestedByCollector -or
                            $collected.fullP085Accepted -or $collected.fullS06Accepted -or -not $collected.actualProcessImageBindingSeparatelyRequired -or
                            -not $collected.actualInputBytesBeforeAndAfterUnchanged -or -not $collected.installedInputsMatchOriginalMsixMembers -or
                            $collected.preparedSha256 -cne $before.preparedSha256) {throw 'Complete source/input receipts or separate execution authority were lost.'}
                        foreach($binding in @($prepared.package,$prepared.collector)+@($prepared.inputs)) {
                            if((Get-FileHash -LiteralPath (Join-Path $output $binding.archivePath)).Hash -cne $binding.sha256) {throw 'An original byte binding changed.'}
                        }
                    } else {
                        $expected=@{'foreign-source'='another source revision';'foreign-library'='another source revision';'renamed'='renamed from another assembly';
                            'missing-version'='one unambiguous source version';'duplicate-version'='one unambiguous source version';'missing-member'='exact required';
                            'duplicate-member'='ambiguous or unsafe';'unsafe-member'='ambiguous or unsafe';'private-key-entry'='ambiguous or unsafe';
                            'different-installed-bytes'='differs from its exact MSIX member';'wrong-native-architecture'='wrong native architecture';
                            'changed-after'='changed during execution';'changed-retained'='changed during execution';'changed-package'='changed during execution';
                            'changed-prepared'='original prepared input receipt';'linked-package'='reparse point';'reused-output'='fresh exact MSIX'}
                        if($null -eq $failure -or -not $failure.Contains($expected[$Scenario]) -or (Test-Path -LiteralPath (Join-Path $output 'collected.json'))) {
                            throw ('The specific original boundary did not refuse: '+$Scenario+'; '+$failure)
                        }
                    }
                    if(@(Get-ChildItem -LiteralPath $output -Recurse -File -Filter '*.pfx' -ErrorAction SilentlyContinue).Count -or
                        ($Scenario -ceq 'private-key-entry' -and (Test-Path -LiteralPath (Join-Path $output 'package.msix')))) {throw 'Signing files entered retained runtime originals.'}
                } finally {if($null -ne $link -and (Test-Path -LiteralPath $link)){Remove-Item -LiteralPath $link -Force}}
                """);
            await RunProbeAsync(script, directory, scenario);
        }
        finally { Cleanup(prefix, directory); }
    }
}
